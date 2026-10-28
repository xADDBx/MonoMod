using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Core.Platforms.Architectures;
using MonoMod.Core.Platforms.Runtimes;
using MonoMod.Logs;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace MonoMod.Core.Platforms
{
    internal sealed partial class PlatformTripleDetourFactory : ISharedGenericDetourFactory
    {
        public bool TryGetSharedGenericForm(MethodBase closed, [MaybeNullWhen(false)] out MethodBase sharedForm)
        {
            Helpers.ThrowIfArgumentNull(closed);
            sharedForm = (triple.Runtime as IGenericSharingRuntime)?.GetSharedForm(closed);
            return sharedForm is not null;
        }

        public ICoreSharedGenericDetour CreateSharedGenericDetour(MethodBase sharedForm, Action<MethodBase> onUnknownInstantiation)
        {
            Helpers.ThrowIfArgumentNull(sharedForm);
            Helpers.ThrowIfArgumentNull(onUnknownInstantiation);
            if (triple.Runtime is not IGenericSharingRuntime runtime)
                throw new PlatformNotSupportedException("The current runtime does not support shared generic detours");
            return new SharedGenericDetour(triple, runtime, sharedForm, onUnknownInstantiation);
        }

        private Action<MethodBase>? genericMethodCompiled;
        private int compileHookSubscribed;

        public event Action<MethodBase>? GenericMethodCompiled
        {
            add
            {
                if (Interlocked.Exchange(ref compileHookSubscribed, 1) == 0)
                {
                    // after the detours' own handler, which must not re-patch the detours ours create on the compiled code
                    if (triple.SupportedFeatures.Has(RuntimeFeature.CompileMethodHook))
                        Detour.EnsureSubscribed(triple);
                    triple.Runtime.OnMethodCompiled += OnMethodCompiled;
                }
                Helpers.EventAdd<Action<MethodBase>>(ref genericMethodCompiled, value!);
            }
            remove => Helpers.EventRemove<Action<MethodBase>>(ref genericMethodCompiled, value!);
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Exceptions must not escape into the compiler.")]
        private void OnMethodCompiled(RuntimeMethodHandle handle, MethodBase? method, IntPtr codeStart, IntPtr codeRw, ulong codeSize)
        {
            if (genericMethodCompiled is not { } handlers || method is null || Detour.CreatingDepth > 0
                || !(method.IsGenericMethod || method.DeclaringType is { IsGenericType: true }))
                return;

            var previous = Detour.KnownBody;
            Detour.KnownBody = (triple.GetIdentifiable(method), codeStart, codeRw, (int)codeSize);
            try
            {
                handlers(method);
            }
            catch (Exception e)
            {
                MMDbgLog.Error($"Error handling the compilation of {method}: {e}");
            }
            finally
            {
                Detour.KnownBody = previous;
            }
        }

        // Detours the shared body to a dispatcher which jumps to the entry of the calling instantiation, found by its generic
        // context. Entries take the native arguments of the shared body, so do the original body and its clones.
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Resolution runs inside calls of the shared method, which must not fail because of it.")]
        private sealed unsafe class SharedGenericDetour : ICoreSharedGenericDetour
        {
            private static readonly MethodInfo LookupMethod = typeof(SharedGenericDetour).GetMethod(nameof(Lookup))!;

            private delegate IntPtr NativeResolver(IntPtr cookie, IntPtr key);
            private static readonly NativeResolver NativeResolve = ResolveFromStub;
            private static readonly IntPtr NativeResolvePtr = Marshal.GetFunctionPointerForDelegate(NativeResolve);

            [ThreadStatic]
            private static bool resolving;
            [ThreadStatic]
            private static bool cloning;

            private readonly PlatformTriple triple;
            private readonly IGenericSharingRuntime runtime;
            private readonly bool contextIsThis;
            private readonly Action<MethodBase> onUnknown;
            private readonly Detour detour;
            private readonly IntPtr tableSlot;
            private readonly IntPtr missEntry;
            // never freed, calls may still be running through it
#pragma warning disable CA2213 // Disposable fields should be disposed
            private readonly IAllocatedMemory? stub;
#pragma warning restore CA2213 // Disposable fields should be disposed

            private readonly object sync = new();
            // the targets keep the code the table points to alive
            private readonly Dictionary<MethodBase, (MethodBase Target, IntPtr Entry)> entries = [];
            private readonly Dictionary<MethodBase, (MethodBase Target, IntPtr Entry)> origClones = [];
            private readonly Dictionary<IntPtr, MethodBase?> keys = [];
            private Dictionary<IntPtr, IntPtr> table = [];
            private readonly Dictionary<IntPtr, int> nativeSlots = [];
            private int nativeCapacity;

            public MethodBase SharedForm { get; }
            public bool IsApplied => detour.IsApplied;
            public IntPtr OrigEntry => detour.AltEntry;

            public SharedGenericDetour(PlatformTriple triple, IGenericSharingRuntime runtime, MethodBase sharedForm, Action<MethodBase> onUnknown)
            {
                this.triple = triple;
                this.runtime = runtime;
                this.onUnknown = onUnknown;
                SharedForm = sharedForm;
                contextIsThis = runtime.ContextIsThis(sharedForm);

                if (!triple.TryDisableInlining(sharedForm))
                    MMDbgLog.Warning($"Could not disable inlining of method {sharedForm}; detours may not be reliable");

                if (triple.Runtime.Target is RuntimeKind.Mono)
                {
                    // Mono passes the context in a register no managed signature can take
                    tableSlot = Marshal.AllocHGlobal(IntPtr.Size);
                    var cookie = GCHandle.ToIntPtr(GCHandle.Alloc(this));
                    stub = GenericDispatchStub.Create(triple.System, triple.Architecture.Target, contextIsThis, tableSlot, cookie,
                        NativeResolvePtr, out missEntry);
                    GrowNativeTable();
                    detour = new Detour(triple, sharedForm, sharedForm, sharedBody: true, nativeTarget: stub.BaseAddress);
                }
                else
                {
                    detour = new Detour(triple, sharedForm, CreateDispatcher(), sharedBody: true);
                }
                detour.Apply();
            }

            private static IntPtr ResolveFromStub(IntPtr cookie, IntPtr key)
            {
                var self = (SharedGenericDetour)GCHandle.FromIntPtr(cookie).Target!;
                try
                {
                    return self.Resolve(key);
                }
                catch (Exception e)
                {
                    var message = DebugFormatter.Format($"Could not dispatch instantiation 0x{key:x} of {self.SharedForm}: {e}");
                    // there is no way to fail the call from here, and without an alternate entry point (Arm64) nothing to run instead
                    if (self.OrigEntry == IntPtr.Zero)
                        Environment.FailFast(message);
                    MMDbgLog.Error(message);
                    return self.OrigEntry;
                }
            }

            public IntPtr Lookup(object? self, IntPtr context)
            {
                var key = contextIsThis ? self?.GetType().TypeHandle.Value ?? IntPtr.Zero : context;
                return Volatile.Read(ref table).TryGetValue(key, out var entry) ? entry : Resolve(key);
            }

            private IntPtr Resolve(IntPtr key)
            {
                // calls made while resolving (e.g. by the callback creating a hook) run the original body and aren't recorded
                if (resolving)
                    return OrigEntry != IntPtr.Zero ? OrigEntry : OrigFor(ResolveKey(key));

                resolving = true;
                try
                {
                    var closed = ResolveKey(key);
                    bool known;
                    lock (sync)
                        known = closed is null || entries.ContainsKey(closed);
                    if (!known)
                    {
                        try
                        {
                            onUnknown(closed!);
                        }
                        catch (Exception e)
                        {
                            MMDbgLog.Error($"Error handling new instantiation {closed} of {SharedForm}: {e}");
                        }
                    }

                    if (closed is null && OrigEntry == IntPtr.Zero)
                        return OrigFor(null);
                    lock (sync)
                    {
                        keys[key] = closed;
                        Publish();
                        return table[key];
                    }
                }
                finally
                {
                    resolving = false;
                }
            }

            private MethodBase? ResolveKey(IntPtr key)
            {
                try
                {
                    var closed = key == IntPtr.Zero ? null : runtime.ResolveInstantiation(SharedForm, key);
                    return closed is null ? null : triple.GetIdentifiable(closed);
                }
                catch (Exception e)
                {
                    MMDbgLog.Error($"Could not resolve instantiation 0x{key:x} of {SharedForm}: {e}");
                    return null;
                }
            }

            // called in the lock
            private void Publish()
            {
                var newTable = new Dictionary<IntPtr, IntPtr>(keys.Count);
                foreach (var pair in keys)
                    newTable[pair.Key] = pair.Value is { } closed && entries.TryGetValue(closed, out var entry) ? entry.Entry : OrigFor(pair.Value);
                Volatile.Write(ref table, newTable);
                if (tableSlot == IntPtr.Zero)
                    return;

                // The stub may be walking the native table, so it is only changed in place: keys stay, leading to the miss path
                // when unknown, and a key is written after its entry. The stub reads a key and its entry without a barrier, so
                // unused entries lead to the miss path too.
                var native = *(IntPtr**)tableSlot;
                foreach (var pair in nativeSlots)
                    native[2 * pair.Value + 1] = newTable.TryGetValue(pair.Key, out var entry) ? entry : missEntry;
                foreach (var pair in newTable)
                {
                    if (pair.Key == IntPtr.Zero || nativeSlots.ContainsKey(pair.Key))
                        continue;
                    var i = nativeSlots.Count;
                    // keep a terminating key
                    if (i + 1 >= nativeCapacity)
                        native = GrowNativeTable();
                    native[2 * i + 1] = pair.Value;
                    Volatile.Write(ref native[2 * i], pair.Key);
                    nativeSlots.Add(pair.Key, i);
                }
            }

            // called in the lock; the old table is never freed, as the stub may still be walking it
            private IntPtr* GrowNativeTable()
            {
                var old = *(IntPtr**)tableSlot;
                nativeCapacity = Math.Max(4, 2 * nativeCapacity);
                var native = (IntPtr*)Marshal.AllocHGlobal(2 * nativeCapacity * IntPtr.Size);
                for (var i = 0; i < nativeCapacity; i++)
                {
                    var used = i < nativeSlots.Count;
                    native[2 * i] = used ? old[2 * i] : IntPtr.Zero;
                    native[2 * i + 1] = used ? old[2 * i + 1] : missEntry;
                }
                Volatile.Write(ref *(IntPtr*)tableSlot, (IntPtr)native);
                return native;
            }

            // without an alternate entry point (Arm64), the original body of each instantiation runs as a clone, that of the
            // instantiation over object if it is unknown (e.g. called on a null this)
            private IntPtr OrigFor(MethodBase? closed)
            {
                if (OrigEntry != IntPtr.Zero)
                    return OrigEntry;
                closed ??= triple.GetIdentifiable(ObjectInstantiation());
                lock (sync)
                {
                    if (origClones.TryGetValue(closed, out var clone))
                        return clone.Entry;
                    if (cloning)
                        throw new NotSupportedException($"Cannot run the original body of {SharedForm} for this instantiation on this architecture");
                    cloning = true;
                    try
                    {
                        using var dmd = new DynamicMethodDefinition(closed);
                        return (origClones[closed] = EntryFor(closed, dmd.Generate())).Entry;
                    }
                    finally
                    {
                        cloning = false;
                    }
                }
            }

            private MethodBase ObjectInstantiation()
            {
                var declType = SharedForm.DeclaringType!;
                if (declType.IsGenericType)
                    declType = declType.GetGenericTypeDefinition().MakeGenericType(declType.GetGenericArguments().Select(MapCanon).ToArray());
                var method = MethodBase.GetMethodFromHandle(GenericSharing.GetDefinition(SharedForm).MethodHandle, declType.TypeHandle)!;
                return SharedForm.IsGenericMethod ? ((MethodInfo)method).MakeGenericMethod(SharedForm.GetGenericArguments().Select(MapCanon).ToArray()) : method;
            }

            private (MethodBase Target, IntPtr Entry) EntryFor(MethodBase closed, MethodBase target)
            {
                var glue = triple.GetRealDetourTarget(closed, target);
                triple.Compile(glue);
                return (glue, triple.Runtime.GetMethodHandle(glue).GetFunctionPointer());
            }

            public void SetEntry(MethodBase closed, MethodBase target)
            {
                Helpers.ThrowIfArgumentNull(closed);
                Helpers.ThrowIfArgumentNull(target);
                closed = triple.GetIdentifiable(closed);
                if (!triple.TryDisableInlining(closed))
                    MMDbgLog.Warning($"Could not disable inlining of method {closed}; detours may not be reliable");
                var entry = EntryFor(closed, target);

                lock (sync)
                {
                    entries[closed] = entry;
                    Publish();
                }
            }

            public void ClearEntry(MethodBase closed)
            {
                Helpers.ThrowIfArgumentNull(closed);
                lock (sync)
                {
                    if (entries.Remove(triple.GetIdentifiable(closed)))
                        Publish();
                }
            }

            public void ResetUnknownInstantiations()
            {
                lock (sync)
                {
                    foreach (var key in keys.Where(p => p.Value is null || !entries.ContainsKey(p.Value)).Select(p => p.Key).ToList())
                        keys.Remove(key);
                    Publish();
                }
            }

            private MethodInfo CreateDispatcher()
            {
                var hasReturnBuffer = triple.HasReturnBufferArgument(MapCanon(SharedForm is MethodInfo mi ? mi.ReturnType : typeof(void)));
                var (nativeReturnType, args, thisPos, _, contextPos, _) = triple.GetNativeSignature(SharedForm, hasReturnBuffer, !contextIsThis);
                nativeReturnType = MapCanon(nativeReturnType);
                args = args.ConvertAll(MapCanon);

                using var dmd = new DynamicMethodDefinition(DebugFormatter.Format($"SharedGenericDispatch<{SharedForm}>"), nativeReturnType, args.ToArray());
                var module = dmd.Module!;
                var il = dmd.GetILProcessor();

                // return Lookup(this or null, context or 0)(all arguments);
                for (var i = 0; i < args.Count; i++)
                    il.Emit(OpCodes.Ldarg, i);
                // never freed, calls may still be running through the dispatcher
                _ = il.EmitNewTypedReference(this, out _);
                if (contextIsThis)
                    il.Emit(OpCodes.Ldarg, thisPos);
                else
                    il.Emit(OpCodes.Ldnull);
                if (contextPos >= 0)
                    il.Emit(OpCodes.Ldarg, contextPos);
                else
                {
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Conv_I);
                }
                il.Emit(OpCodes.Call, module.ImportReference(LookupMethod));
                var site = new CallSite(module.ImportReference(nativeReturnType));
                foreach (var arg in args)
                    site.Parameters.Add(new ParameterDefinition(module.ImportReference(arg)));
                il.Emit(OpCodes.Calli, site);
                il.Emit(OpCodes.Ret);

                return dmd.Generate();
            }

            // the dispatcher can't reference __Canon
            private static Type MapCanon(Type type)
                => !FxCoreBaseRuntime.ContainsCanon(type) ? type
                : type.IsByRef ? MapCanon(type.GetElementType()!).MakeByRefType()
                : type.IsValueType ? type.GetGenericTypeDefinition().MakeGenericType(type.GetGenericArguments().Select(MapCanon).ToArray())
                : typeof(object);

            public void Apply() => detour.Apply();

            public void Undo() => detour.Undo();

            public void Dispose() => detour.Dispose();
        }
    }
}
