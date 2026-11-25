using MonoMod.Utils;
using System;
using System.Linq;
using System.Reflection;

namespace MonoMod.Core.Platforms.Runtimes
{
    internal abstract partial class FxCoreBaseRuntime : IGenericSharingRuntime
    {
        private static readonly Type CanonType = typeof(object).Assembly.GetType("System.__Canon")!;

        // an instantiating stub passes the generic context to the shared code
        protected virtual bool TryUnwrapInstantiatingStub(RuntimeMethodHandle handle, out RuntimeMethodHandle wrapped)
        {
            wrapped = default;
            return false;
        }

        private static MethodBase GetDefinition(MethodBase method)
            => method is MethodInfo mi ? mi.GetActualGenericMethodDefinition() : method.GetUnfilledMethodOnGenericType();

        private static Type Canonicalize(Type type)
            => !IsGenericSharedType(type) ? type
            : !type.IsValueType ? CanonType
            : type.GetGenericTypeDefinition().MakeGenericType(type.GetGenericArguments().Select(Canonicalize).ToArray());

        private static bool IsSharedGenericCode(MethodBase method, out Type[] typeArgs, out Type[] methodArgs)
        {
            typeArgs = method.DeclaringType is { IsGenericType: true } declaringType ? declaringType.GetGenericArguments() : Type.EmptyTypes;
            methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;
            return typeArgs.Concat(methodArgs).Any(IsGenericSharedType);
        }

        public bool TryGetSharedCode(MethodBase method, out SharedGenericCode code)
        {
            code = default;
            if (!IsSharedGenericCode(method, out var typeArgs, out var methodArgs))
            {
                return false;
            }

            var declaringType = method.DeclaringType;
            var canonicalType = typeArgs.Length > 0
                ? declaringType!.GetGenericTypeDefinition().MakeGenericType(typeArgs.Select(Canonicalize).ToArray())
                : declaringType!;
            var canonical = MethodBase.GetMethodFromHandle(GetDefinition(method).MethodHandle, canonicalType.TypeHandle)!;
            if (methodArgs.Length > 0)
            {
                canonical = ((MethodInfo)canonical).MakeGenericMethod(methodArgs.Select(Canonicalize).ToArray());
            }

            if (method.IsVirtual && canonicalType.IsValueType && !TryGetNonStubMethodHandle(canonical.MethodHandle, out _))
            {
                throw new NotSupportedException($"Cannot find the shared code of virtual method {method} of a value type on this runtime");
            }

            // the JIT reports the shared code with the wrapped MethodDesc, so the method is identified the same way
            var handle = GetMethodHandle(canonical);
            if (TryUnwrapInstantiatingStub(handle, out var wrapped))
            {
                handle = wrapped;
                canonical = MethodBase.GetMethodFromHandle(handle, canonicalType.TypeHandle)!;
            }

            code = new(canonical, handle, !MethodRequiresGenericContext(method));
            return true;
        }

        public MethodBase ResolveInstantiation(SharedGenericCode code, IntPtr context)
        {
            if (code.Method.IsGenericMethod)
            {
                // the exact MethodDesc
                return MethodBase.GetMethodFromHandle(JitHookHelpers.CreateHandleForHandlePointer(context),
                    JitHookHelpers.GetDeclaringTypeOfMethodHandle(context).TypeHandle)!;
            }

            // the exact MethodTable, or that of this, which may be derived from the instantiation
            var definition = code.Method.DeclaringType!.GetGenericTypeDefinition();
            var type = JitHookHelpers.GetTypeFromNativeHandle(context);
            while (!type.IsGenericType || type.GetGenericTypeDefinition() != definition)
            {
                type = type.BaseType!;
            }
            return MethodBase.GetMethodFromHandle(GetDefinition(code.Method).MethodHandle, type.TypeHandle)!;
        }
    }
}
