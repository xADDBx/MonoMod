using MonoMod.Utils;
using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace MonoMod.Core.Platforms.Runtimes
{
    internal abstract partial class FxCoreBaseRuntime
    {
        private static readonly Func<FxCoreBaseRuntime, JitHookHelpersHolder> createJitHookHelpersFunc = CreateJitHookHelpers;
        private static JitHookHelpersHolder CreateJitHookHelpers(FxCoreBaseRuntime self) => new(self);

        private readonly object jitHookHelpersLock = new();
        private JitHookHelpersHolder? lazyJitHookHelpers;
        protected unsafe JitHookHelpersHolder JitHookHelpers => Helpers.GetOrInitWithLock(ref lazyJitHookHelpers, jitHookHelpersLock, createJitHookHelpersFunc, this);

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

        protected sealed class JitHookHelpersHolder
        {
            public delegate object MethodHandle_GetLoaderAllocatorD(IntPtr methodHandle);
            public delegate object CreateRuntimeMethodInfoStubD(IntPtr methodHandle, object loaderAllocator);
            public delegate RuntimeMethodHandle CreateRuntimeMethodHandleD(object runtimeMethodInfo);
            public delegate Type GetDeclaringTypeOfMethodHandleD(IntPtr methodHandle);
            public delegate Type GetTypeFromNativeHandleD(IntPtr handle);
            public delegate RuntimeMethodHandle RuntimeMethodHandle_FromIntPtr(IntPtr value);

            public readonly MethodHandle_GetLoaderAllocatorD MethodHandle_GetLoaderAllocator;
            public readonly CreateRuntimeMethodInfoStubD CreateRuntimeMethodInfoStub;
            public readonly CreateRuntimeMethodHandleD CreateRuntimeMethodHandle;
            public readonly GetDeclaringTypeOfMethodHandleD GetDeclaringTypeOfMethodHandle;
            public readonly GetTypeFromNativeHandleD GetTypeFromNativeHandle;
            public readonly RuntimeMethodHandle_FromIntPtr? Bcl_RuntimeMethodHandle_FromIntPtr;



            public RuntimeMethodHandle CreateHandleForHandlePointer(IntPtr handle)
            {
                if (Bcl_RuntimeMethodHandle_FromIntPtr is { } bclMeth)
                {
                    return bclMeth(handle);
                }

                return CreateRuntimeMethodHandle(CreateRuntimeMethodInfoStub(handle, MethodHandle_GetLoaderAllocator(handle)));
            }

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

                // .NET 7+ expose this helper directly. Since we use this type on even much newer runtimes, 
                Bcl_RuntimeMethodHandle_FromIntPtr = typeof(RuntimeMethodHandle)
                    .GetMethod("FromIntPtr", BindingFlags.Public | BindingFlags.Static, null, [typeof(IntPtr)], null)
                    ?.CreateDelegate<RuntimeMethodHandle_FromIntPtr>();
            }
        }

        protected virtual MethodInfo GetOrCreateGetTypeFromHandleUnsafe()
            => typeof(Type).GetMethod("GetTypeFromHandleUnsafe", (BindingFlags)(-1))
            ?? throw new PlatformNotSupportedException("Type.GetTypeFromHandleUnsafe does not exist");
    }
}
