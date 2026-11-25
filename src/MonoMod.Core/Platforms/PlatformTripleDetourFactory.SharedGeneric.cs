using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Logs;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;

#if NET9_0_OR_GREATER
using Lock = System.Threading.Lock;
#else
using Lock = object;
#endif

namespace MonoMod.Core.Platforms
{
    internal sealed partial class PlatformTripleDetourFactory
    {
        // dispatchers are never removed, calls may still be running through them
        private readonly Dictionary<IntPtr, SharedGenericDispatcher> dispatchers = new();
        private readonly Lock dispatchersLock = new();

        private ManagedCoreDetour CreateSharedGenericDetour(MethodBase src, SharedGenericCode code, MethodBase dst)
        {
            if (!triple.SupportedFeatures.Has(ArchitectureFeature.CreateAltEntryPoint))
            {
                // instantiations without a detour run the shared code through an alternate entrypoint
                throw new NotSupportedException("Current architecture does not support alternate " +
                    "entrypoints, which are required for shared generic detours.");
            }

            DisableInlining(src, triple.Runtime.GetMethodHandle(src));
            var realTarget = triple.GetRealDetourTarget(src, dst, GetArgumentMap(src, dst));

            SharedGenericDispatcher? dispatcher;
            lock (dispatchersLock)
            {
                if (!dispatchers.TryGetValue(code.Handle.Value, out dispatcher))
                {
                    DisableInlining(code.Method, code.Handle);
                    dispatcher = new SharedGenericDispatcher(this, (IGenericSharingRuntime)triple.Runtime, code, src);
                    dispatchers.Add(code.Handle.Value, dispatcher);
                }
            }

            return new ManagedCoreDetour(new SharedGenericDetourBox(this, src, dst, realTarget, dispatcher));
        }

        private sealed class SharedGenericDetourBox(PlatformTripleDetourFactory factory,
            MethodBase src, MethodBase dst, MethodBase realDst, SharedGenericDispatcher dispatcher)
            : ManagedDetourBoxBase(factory, src, dst)
        {
            protected override void ApplyCore()
            {
                MMDbgLog.Trace($"Applying detour from shared generic instantiation {SrcMethod} to {realDst}");

                dispatcher.EnsureApplied();
                dispatcher.SetEntry(SrcMethod, realDst);
            }

            protected override void OnBeforeUndo()
            {
                base.OnBeforeUndo();
                dispatcher.ClearEntry(SrcMethod);
            }

            // the code belongs to the dispatcher
            public override void OnRelatedMethodCompiled(MethodBase method, RuntimeMethodHandle handle, IntPtr codeStart, IntPtr codeRw, ulong codeSize) { }
        }

        // Detours shared generic code to a method which calls the entry of the calling instantiation, or the original code if it has none.
        private sealed class SharedGenericDispatcher : ManagedDetourBoxBase
        {
            private static readonly MethodInfo LookupMethod = typeof(SharedGenericDispatcher).GetMethod(nameof(Lookup))!;

            // resolving runs shared code which may be dispatched itself
            [ThreadStatic]
            private static bool resolving;

            private readonly IGenericSharingRuntime runtime;
            private readonly SharedGenericCode code;
            private readonly RuntimeMethodHandle targetHandle;
            private IntPtr origEntry;
            private bool walking;
            private volatile bool recompileWaiting;

            private readonly Lock entriesLock = new();
            private readonly Dictionary<MethodBase, IntPtr> entries = new();
            private readonly Dictionary<IntPtr, MethodBase> instantiations = new();
            private Dictionary<IntPtr, IntPtr> table = new();

            public SharedGenericDispatcher(PlatformTripleDetourFactory factory, IGenericSharingRuntime runtime, SharedGenericCode code, MethodBase instantiation)
                : base(factory, code.Method, CreateTarget(factory.triple, code, instantiation, out var cell))
            {
                this.runtime = runtime;
                this.code = code;
                DynamicReferenceManager.SetValue(cell, this);
                targetHandle = Triple.Runtime.GetMethodHandle(DstMethod);
            }

            public void EnsureApplied()
            {
                lock (Sync)
                {
                    if (!IsApplied)
                    {
                        Apply();
                    }
                }
            }

            protected override void ApplyCore()
            {
                MMDbgLog.Trace($"Applying dispatcher to shared generic code of {SrcMethod}");

                Triple.Compile(DstMethod, targetHandle);
                Triple.Compile(SrcMethod, code.Handle);
                walking = true;
                Thread.MemoryBarrier();
                var from = Triple.GetNativeMethodBody(SrcMethod, code.Handle);
                // wait for new code to go live, unless its compile callback is waiting for us to finish (it then detours the code itself)
                while (!recompileWaiting && Factory.IsAwaitingPublish(code.Handle, from))
                {
                    Thread.Sleep(0);
                    from = Triple.GetNativeMethodBody(SrcMethod, code.Handle);
                }
                var detour = Triple.CreateNativeDetour(from, targetHandle.GetFunctionPointer(), ref origEntry);
                Detours.Add(detour.Simple);
                if (detour.AltHandle is { } altHandle)
                {
                    AllocHandles.Add(altHandle);
                }
            }

            [SuppressMessage("Design", "CA1031:Do not catch general exception types",
                Justification = "We don't want to bubble exceptions out of here, since we're running cooperatively with other handlers")]
            public override void OnRelatedMethodCompiled(MethodBase method, RuntimeMethodHandle handle, IntPtr codeStart, IntPtr codeRw, ulong codeSize)
            {
                if ((!IsApplied && !walking) || Triple.GetIdentifiable(method) != SrcMethod)
                {
                    return;
                }

                recompileWaiting = true;
                lock (Sync)
                {
                    if (!IsApplied || IsApplying)
                    {
                        return;
                    }

                    IsApplying = true;

                    try
                    {
                        // origEntry stays valid, it keeps running the code it was created from
                        Detours.Add(Triple.CreateSimpleDetour(codeStart, targetHandle.GetFunctionPointer(), detourMaxSize: (int)codeSize, fromRw: codeRw));
                    }
                    catch (Exception e)
                    {
                        MMDbgLog.Error($"An exception occurred while updating the dispatcher of {SrcMethod}: {e}");
                    }
                    finally
                    {
                        IsApplying = false;
                    }
                }
            }

            public void SetEntry(MethodBase instantiation, MethodBase realTarget)
            {
                Triple.Compile(realTarget);
                var entry = Triple.Runtime.GetMethodHandle(realTarget).GetFunctionPointer();
                lock (entriesLock)
                {
                    entries[instantiation] = entry;
                    UpdateTable();
                }
            }

            public void ClearEntry(MethodBase instantiation)
            {
                lock (entriesLock)
                {
                    if (entries.Remove(instantiation))
                    {
                        UpdateTable();
                    }
                }
            }

            // NOTE: requires entriesLock being held
            private void UpdateTable()
            {
                var newTable = new Dictionary<IntPtr, IntPtr>(instantiations.Count);
                foreach (var pair in instantiations)
                {
                    newTable[pair.Key] = entries.TryGetValue(pair.Value, out var entry) ? entry : origEntry;
                }
                Volatile.Write(ref table, newTable);
            }

            public IntPtr Lookup(object? self, IntPtr context)
            {
                var key = code.ContextIsThis ? self?.GetType().TypeHandle.Value ?? IntPtr.Zero : context;
                return Volatile.Read(ref table).TryGetValue(key, out var entry) ? entry : Resolve(key);
            }

            private IntPtr Resolve(IntPtr key)
            {
                // key is zero for a null this
                if (key == IntPtr.Zero || resolving)
                {
                    return origEntry;
                }

                resolving = true;
                try
                {
                    var instantiation = Triple.GetIdentifiable(runtime.ResolveInstantiation(code, key));
                    lock (entriesLock)
                    {
                        instantiations[key] = instantiation;
                        UpdateTable();
                        return table[key];
                    }
                }
                finally
                {
                    resolving = false;
                }
            }

            private static MethodInfo CreateTarget(PlatformTriple triple, SharedGenericCode code, MethodBase instantiation, out DynamicReferenceCell dispatcher)
            {
                // every instantiation running the code has the same ABI
                var abi = triple.GetMethodAbi(MethodAbiUsage.RealMethodBody, instantiation);
                var args = abi.Arguments.Span;
                var argTypes = new Type[args.Length];
                for (var i = 0; i < args.Length; i++)
                {
                    argTypes[i] = args[i].Type;
                }

                using var dmd = new DynamicMethodDefinition(DebugFormatter.Format($"SharedGenericDispatcher<{code.Method}>"), abi.AbiReturn, argTypes);
                var module = dmd.Module!;
                var il = dmd.GetILProcessor();

                var site = new CallSite(module.ImportReference(abi.AbiReturn));
                for (var i = 0; i < argTypes.Length; i++)
                {
                    il.Emit(OpCodes.Ldarg, i);
                    site.Parameters.Add(new ParameterDefinition(module.ImportReference(argTypes[i])));
                }

                _ = il.EmitNewTypedReference<SharedGenericDispatcher>(null, out dispatcher);
                if (code.ContextIsThis)
                {
                    il.Emit(OpCodes.Ldarg, abi.ThisArgument);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Conv_I);
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Ldarg, abi.GenericContextArgument);
                }
                il.Emit(OpCodes.Call, module.ImportReference(LookupMethod));
                il.Emit(OpCodes.Calli, site);
                il.Emit(OpCodes.Ret);

                return dmd.Generate();
            }
        }
    }
}
