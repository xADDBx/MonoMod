using MonoMod.Utils;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using static MonoMod.Core.Interop.CoreCLR;

namespace MonoMod.Core.Platforms.Runtimes
{
    // the JIT hook, shared by .NET Core and .NET Framework 4.6+ (RyuJIT)
    internal abstract partial class FxCoreBaseRuntime
    {
        private static bool IsMaybeClrJitPath(string path)
            => Path.GetFileName(path) == DynDll.MakeDllName("clrjit");

        protected virtual string GetClrJitPath()
        {
            string? clrjitFile = null;

            if (Switches.TryGetSwitchValue(Switches.JitPath, out var swValue) && swValue is string jitPath)
            {
                if (!IsMaybeClrJitPath(jitPath))
                {
                    MMDbgLog.Warning($"Provided value for MonoMod.JitPath switch '{jitPath}' does not look like a ClrJIT path");
                }
                else
                {
                    clrjitFile = System.EnumerateLoadedModuleFiles()
                        .FirstOrDefault(f => f is not null && f == jitPath);
                    if (clrjitFile is null)
                    {
                        MMDbgLog.Warning($"Provided path for MonoMod.JitPath switch was not loaded in this process. jitPath: {jitPath}");
                    }
                }
            }

            clrjitFile ??= System.EnumerateLoadedModuleFiles()
                .FirstOrDefault(f => f is not null && IsMaybeClrJitPath(f));

            if (clrjitFile is null)
                throw new PlatformNotSupportedException("Could not locate clrjit library");

            MMDbgLog.Trace($"Got jit path: {clrjitFile}");
            return clrjitFile;
        }

        private IntPtr? lazyJitObject;
        protected IntPtr JitObject => lazyJitObject ??= GetJitObject();

        private unsafe IntPtr GetJitObject()
        {
            var path = GetClrJitPath();

            if (!DynDll.TryOpenLibrary(path, out var clrjit))
                throw new PlatformNotSupportedException("Could not open clrjit library");

            try
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr>)DynDll.GetExport(clrjit, "getJit"))();
            }
            catch
            {
                DynDll.CloseLibrary(clrjit);
                throw;
            }
        }

        protected virtual InvokeCompileMethodPtr InvokeCompileMethodPtr => V21.InvokeCompileMethodPtr;

        protected static unsafe IntPtr* GetVTableEntry(IntPtr @object, int index)
            => (*(IntPtr**)@object) + index;

        protected unsafe virtual void InvokeCompileMethodToPrepare(IntPtr method)
        {
            V21.CORINFO_METHOD_INFO methodInfo;
            byte* nativeStart;
            uint nativeSize;
            InvokeCompileMethodPtr.InvokeCompileMethod(method, IntPtr.Zero, IntPtr.Zero, &methodInfo, 0, &nativeStart, &nativeSize);
        }

        private static readonly Func<FxCoreBaseRuntime, JitHookHelpersHolder> createJitHookHelpersFunc = CreateJitHookHelpers;
        private static JitHookHelpersHolder CreateJitHookHelpers(FxCoreBaseRuntime self) => new(self);

        private readonly object sync = new();
        private JitHookHelpersHolder? lazyJitHookHelpers;
        protected internal JitHookHelpersHolder JitHookHelpers => Helpers.GetOrInitWithLock(ref lazyJitHookHelpers, sync, createJitHookHelpersFunc, this);

        protected virtual MethodInfo MakeCreateRuntimeMethodInfoStub(Type methodHandleInternal)
        {
            var runtimeMethodInfoStubCtorArgs = new[] { typeof(IntPtr), typeof(object) };
            var runtimeMethodInfoStub = typeof(RuntimeMethodHandle).Assembly.GetType("System.RuntimeMethodInfoStub");
            Helpers.DAssert(runtimeMethodInfoStub is not null);
            var runtimeMethodInfoStubCtor = runtimeMethodInfoStub.GetConstructor(runtimeMethodInfoStubCtorArgs);
            Helpers.DAssert(runtimeMethodInfoStubCtor is not null);

            MethodInfo runtimeMethodInfoStubCtorWrapper;
            using (var dmd = new DynamicMethodDefinition(
                    "new RuntimeMethodInfoStub", runtimeMethodInfoStub, runtimeMethodInfoStubCtorArgs
                ))
            {
                var il = dmd.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Newobj, runtimeMethodInfoStubCtor);
                il.Emit(OpCodes.Ret);

                runtimeMethodInfoStubCtorWrapper = dmd.Generate();
            }

            return runtimeMethodInfoStubCtorWrapper;
        }

        protected internal sealed class JitHookHelpersHolder
        {
            public delegate object MethodHandle_GetLoaderAllocatorD(IntPtr methodHandle);
            public delegate object CreateRuntimeMethodInfoStubD(IntPtr methodHandle, object loaderAllocator);
            public delegate RuntimeMethodHandle CreateRuntimeMethodHandleD(object runtimeMethodInfo);
            public delegate Type GetDeclaringTypeOfMethodHandleD(IntPtr methodHandle);
            public delegate Type GetTypeFromNativeHandleD(IntPtr handle);

            public readonly MethodHandle_GetLoaderAllocatorD MethodHandle_GetLoaderAllocator;
            public readonly CreateRuntimeMethodInfoStubD CreateRuntimeMethodInfoStub;
            public readonly CreateRuntimeMethodHandleD CreateRuntimeMethodHandle;
            public readonly GetDeclaringTypeOfMethodHandleD GetDeclaringTypeOfMethodHandle;
            public readonly GetTypeFromNativeHandleD GetTypeFromNativeHandle;

            public RuntimeMethodHandle CreateHandleForHandlePointer(IntPtr handle)
                => CreateRuntimeMethodHandle(CreateRuntimeMethodInfoStub(handle, MethodHandle_GetLoaderAllocator(handle)));

            public JitHookHelpersHolder(FxCoreBaseRuntime runtime)
            {

                const BindingFlags StaticNonPublic = BindingFlags.Static | BindingFlags.NonPublic;

                // GetLoaderAllocator should always be present
                { // set up GetLoaderAllocator
                    var getLoaderAllocator = typeof(RuntimeMethodHandle).GetMethod("GetLoaderAllocator", StaticNonPublic);
                    Helpers.DAssert(getLoaderAllocator is not null);

                    MethodInfo invokeWrapper;
                    using (var dmd = new DynamicMethodDefinition(
                            "MethodHandle_GetLoaderAllocator", typeof(object), new Type[] { typeof(IntPtr) }
                        ))
                    {
                        var il = dmd.GetILGenerator();
                        var paramType = getLoaderAllocator.GetParameters().First().ParameterType;
                        il.Emit(OpCodes.Ldarga_S, 0);
                        // Unsafe.As shouldn't be needed
                        //il.Emit(OpCodes.Call, Unsafe_As.MakeGenericMethod(typeof(IntPtr), paramType));
                        il.Emit(OpCodes.Ldobj, paramType);
                        il.Emit(OpCodes.Call, getLoaderAllocator);
                        il.Emit(OpCodes.Ret);

                        invokeWrapper = dmd.Generate();
                    }

                    MethodHandle_GetLoaderAllocator = invokeWrapper.CreateDelegate<MethodHandle_GetLoaderAllocatorD>();
                }

                { // set up GetTypeFromNativeHandle
                    var getTypeFromHandleUnsafe = runtime.GetOrCreateGetTypeFromHandleUnsafe();
                    GetTypeFromNativeHandle = getTypeFromHandleUnsafe.CreateDelegate<GetTypeFromNativeHandleD>();
                }

                var methodHandleInternal = typeof(RuntimeMethodHandle).Assembly.GetType("System.RuntimeMethodHandleInternal");
                Helpers.DAssert(methodHandleInternal is not null);

                { // set up GetDeclaringTypeOfMethodHandle
                    var getDeclaringType = typeof(RuntimeMethodHandle).GetMethod("GetDeclaringType", StaticNonPublic, null, [methodHandleInternal], null);
                    Helpers.DAssert(getDeclaringType is not null);

                    MethodInfo invokeWrapper;
                    using (var dmd = new DynamicMethodDefinition(
                            "GetDeclaringTypeOfMethodHandle", typeof(Type), [typeof(IntPtr)]
                        ))
                    {
                        var il = dmd.GetILGenerator();
                        il.Emit(OpCodes.Ldarga_S, 0);
                        // Unsafe.As shouldn't be needed
                        //il.Emit(OpCodes.Call, Unsafe_As.MakeGenericMethod(typeof(IntPtr), methodHandleInternal));
                        il.Emit(OpCodes.Ldobj, methodHandleInternal);
                        il.Emit(OpCodes.Call, getDeclaringType);
                        il.Emit(OpCodes.Ret);

                        invokeWrapper = dmd.Generate();
                    }

                    GetDeclaringTypeOfMethodHandle = invokeWrapper.CreateDelegate<GetDeclaringTypeOfMethodHandleD>();
                }

                { // set up CreateRuntimeMethodInfoStub
                    CreateRuntimeMethodInfoStub = runtime
                        .MakeCreateRuntimeMethodInfoStub(methodHandleInternal)
                        .CreateDelegate<CreateRuntimeMethodInfoStubD>();
                }

                { // set up CreateRuntimeMethodHandle
                    var ctor = typeof(RuntimeMethodHandle).GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).First();

                    MethodInfo ctorWrapper;
                    using (var dmd = new DynamicMethodDefinition(
                            "new RuntimeMethodHandle", typeof(RuntimeMethodHandle), new Type[] { typeof(object) }
                        ))
                    {
                        var il = dmd.GetILGenerator();
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Newobj, ctor);
                        il.Emit(OpCodes.Ret);

                        ctorWrapper = dmd.Generate();
                    }

                    CreateRuntimeMethodHandle = ctorWrapper.CreateDelegate<CreateRuntimeMethodHandleD>();
                }
            }
        }

        // overridden where the managed method is missing
        protected virtual MethodInfo GetOrCreateGetTypeFromHandleUnsafe()
            => typeof(Type).GetMethod("GetTypeFromHandleUnsafe", (BindingFlags)(-1))!;
    }
}
