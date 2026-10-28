using MonoMod.Utils;
using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace MonoMod.Core.Platforms.Runtimes
{
    internal sealed partial class MonoRuntime : IGenericSharingRuntime
    {
        // only instantiations over reference types share code, the first one seen represents the body
        private readonly ConcurrentDictionary<MethodBase, MethodBase> sharedForms = new();

        public MethodBase? GetSharedForm(MethodBase closed)
        {
            if (closed.DeclaringType is not { } declType || closed.ContainsGenericParameters)
                return null;
            var args = GenericSharing.TypeArguments(closed);
            if (args.Length == 0 || args.Any(t => t.IsValueType))
                return null;

            // Mono may still decide not to share a method
            var form = sharedForms.GetOrAdd(GenericSharing.GetDefinition(closed), closed);
            return GetMethodEntryPoint(form) == GetMethodEntryPoint(closed) ? form : null;
        }

        // the RGCTX register holds the MRGCTX for generic methods and the vtable for static and value type methods
        public bool ContextIsThis(MethodBase sharedForm) => !sharedForm.IsGenericMethod && !sharedForm.IsStatic && !sharedForm.DeclaringType!.IsValueType;

        public unsafe MethodBase? ResolveInstantiation(MethodBase sharedForm, IntPtr context)
        {
            if (sharedForm.IsGenericMethod)
            {
                var def = GenericSharing.GetDefinition(sharedForm);
                // struct MonoMethodRuntimeGenericContext { MonoVTable *class_vtable; MonoGenericInst *method_inst; ... }
                // struct MonoGenericInst { gint32 id; guint type_argc : 22; ...; MonoType *type_argv[]; }
                var declType = GetTypeFromVTable(*(IntPtr*)context);
                var inst = *(byte**)((byte*)context + IntPtr.Size);
                var argc = def.GetGenericArguments().Length;
                if (declType is null || inst is null || (*(uint*)(inst + 4) & 0x3FFFFF) != argc)
                    return null;
                var args = new Type[argc];
                for (var i = 0; i < argc; i++)
                {
                    if (GetTypeFromMonoType(((IntPtr*)(inst + 8))[i]) is not { } arg)
                        return null;
                    args[i] = arg;
                }
                return ((MethodInfo)MethodBase.GetMethodFromHandle(def.MethodHandle, declType.TypeHandle)!).MakeGenericMethod(args);
            }
            return GenericSharing.FindInstantiation(sharedForm, GetTypeFromVTable(context));
        }

        // RuntimeTypeHandle is a MonoType*
        private static Type? GetTypeFromMonoType(IntPtr type) => type == IntPtr.Zero ? null : Type.GetTypeFromHandle(Unsafe.As<IntPtr, RuntimeTypeHandle>(ref type));

        // MonoVTable starts with its MonoClass*, which embeds its MonoType at an offset that depends on the version
        private static readonly int classTypeOffset = GetClassTypeOffset();

        private static unsafe int GetClassTypeOffset()
        {
            // the MonoType of a non-generic class starts with its MonoClass*
            var type = typeof(MonoRuntime).TypeHandle.Value;
            var offset = (long)type - (long)*(IntPtr*)type;
            if (offset is > 0 and < 0x400)
                return (int)offset;
            MMDbgLog.Warning($"Could not find the MonoType of a MonoClass (offset {offset}); shared generic instantiations can't be resolved");
            return -1;
        }

        private static unsafe Type? GetTypeFromClass(byte* klass)
            => klass == null || classTypeOffset < 0 ? null : GetTypeFromMonoType((IntPtr)(klass + classTypeOffset));

        private static unsafe Type? GetTypeFromVTable(IntPtr vtable) => vtable == IntPtr.Zero ? null : GetTypeFromClass(*(byte**)vtable);

        // Mono 5.6+ reports compiled methods to profilers. The exports have to come from the running runtime, loading another
        // Mono library would start a second one.
        private IntPtr[]? profilerExports;

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Not finding the exports only disables the compile hook.")]
        private IntPtr[]? FindExports(params string[] names)
        {
            try
            {
                // the main program (system Mono), or a loaded Mono library (embedded Mono like Unity's)
                var modules = system.EnumerateLoadedModuleFiles()
                    .Where(f => Path.GetFileName(f)?.Contains("mono", StringComparison.OrdinalIgnoreCase) ?? false)
                    .Distinct();
                foreach (var module in new string?[] { null }.Concat(modules))
                {
                    var exports = new IntPtr[names.Length];
                    if (DynDll.TryOpenLibrary(module, out var lib) && Enumerable.Range(0, names.Length).All(i => lib.TryGetExport(names[i], out exports[i])))
                        return exports;
                }
            }
            catch (Exception e)
            {
                MMDbgLog.Warning($"Error looking for Mono's profiler API: {e}");
            }
            MMDbgLog.Info("Mono's profiler API was not found; generic instantiations compiled later won't be reported");
            return null;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void JitDoneCallback(IntPtr profiler, IntPtr method, IntPtr jitInfo);

        private OnMethodCompiledCallback? onMethodCompiled;
        // kept alive while the profiler may call it
        private JitDoneCallback? jitDone;
        [ThreadStatic]
        private static bool inJitDone;

        public unsafe event OnMethodCompiledCallback? OnMethodCompiled
        {
            add
            {
                if (Interlocked.CompareExchange(ref jitDone, OnJitDone, null) is null && FindExports(
                    "mono_profiler_create", "mono_profiler_set_jit_done_callback", "mono_jit_info_get_code_start", "mono_jit_info_get_code_size") is { } exports)
                {
                    profilerExports = exports;
                    var profiler = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)exports[0])(IntPtr.Zero);
                    ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)exports[1])(profiler, Marshal.GetFunctionPointerForDelegate(jitDone!));
                    // the callback can't run once its domain is unloaded (e.g. by Unity)
                    EventHandler remove = (_, _) => ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)exports[1])(profiler, IntPtr.Zero);
                    AppDomain.CurrentDomain.DomainUnload += remove;
                    AppDomain.CurrentDomain.ProcessExit += remove;
                }
                Helpers.EventAdd<OnMethodCompiledCallback>(ref onMethodCompiled, value!);
            }
            remove => Helpers.EventRemove<OnMethodCompiledCallback>(ref onMethodCompiled, value!);
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Exceptions must not escape into the JIT.")]
        private unsafe void OnJitDone(IntPtr profiler, IntPtr method, IntPtr jitInfo)
        {
            // struct _MonoMethod { guint16 flags, iflags; guint32 token; MonoClass *klass; MonoMethodSignature *signature;
            //     const char *name; unsigned int inline_info:1, inline_failure:1, wrapper_type:5, string_ctor:1, save_lmf:1,
            //     dynamic:1, sre_method:1, is_generic:1, is_inflated:1, ...; }
            // only inflated methods which aren't wrappers
            var bits = *(uint*)((byte*)method + 8 + 3 * IntPtr.Size);
            if (inJitDone || onMethodCompiled is not { } handlers || (bits & 0x107C) != 0x1000)
                return;

            inJitDone = true;
            try
            {
                var handle = Unsafe.As<IntPtr, RuntimeMethodHandle>(ref method);
                if (GetTypeFromClass(*(byte**)((byte*)method + 8)) is not { } type
                    || MethodBase.GetMethodFromHandle(handle, type.TypeHandle) is not { } compiled
                    || !GenericSharing.TypeArguments(compiled).Any(t => t.IsValueType))
                    return;
                var codeStart = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)profilerExports![2])(jitInfo);
                var codeSize = ((delegate* unmanaged[Cdecl]<IntPtr, int>)profilerExports[3])(jitInfo);
                handlers(handle, compiled, codeStart, IntPtr.Zero, (ulong)codeSize);
            }
            catch (Exception e)
            {
                MMDbgLog.Error($"Error reporting a compiled method: {e}");
            }
            finally
            {
                inJitDone = false;
            }
        }
    }
}
