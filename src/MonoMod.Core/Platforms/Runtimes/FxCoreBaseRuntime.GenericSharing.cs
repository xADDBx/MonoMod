using System;
using System.Linq;
using System.Reflection;

namespace MonoMod.Core.Platforms.Runtimes
{
    internal abstract partial class FxCoreBaseRuntime : IGenericSharingRuntime
    {
        private static readonly Type? CanonType = typeof(object).Assembly.GetType("System.__Canon");

        internal static bool ContainsCanon(Type type)
            => type == CanonType
            || type.HasElementType && ContainsCanon(type.GetElementType()!)
            || type.IsGenericType && type.GetGenericArguments().Any(ContainsCanon);

        // the MethodDesc whose code runs when the method is called
        protected virtual RuntimeMethodHandle GetCodeHandle(MethodBase method) => GetMethodHandle(method);

        private static Type Canonicalize(Type type)
            => !IsGenericSharedType(type) ? type
            : !type.IsValueType ? CanonType!
            : type.GetGenericTypeDefinition().MakeGenericType(type.GetGenericArguments().Select(Canonicalize).ToArray());

        public MethodBase? GetSharedForm(MethodBase closed)
        {
            if (closed.DeclaringType is not { } declType || closed.ContainsGenericParameters)
                return null;

            var typeArgs = declType.GetGenericArguments();
            var methodArgs = closed.IsGenericMethod ? closed.GetGenericArguments() : Type.EmptyTypes;
            if (!typeArgs.Concat(methodArgs).Any(IsGenericSharedType))
                return null;

            var canonType = typeArgs.Length > 0 ? declType.GetGenericTypeDefinition().MakeGenericType(typeArgs.Select(Canonicalize).ToArray()) : declType;
            var form = MethodBase.GetMethodFromHandle(GenericSharing.GetDefinition(closed).MethodHandle, canonType.TypeHandle)!;
            return methodArgs.Length > 0 ? ((MethodInfo)form).MakeGenericMethod(methodArgs.Select(Canonicalize).ToArray()) : form;
        }

        public virtual bool ContextIsThis(MethodBase sharedForm) => !RequiresGenericContext(sharedForm);

        public MethodBase? ResolveInstantiation(MethodBase sharedForm, IntPtr context)
        {
            if (sharedForm.IsGenericMethod)
                return MethodBase.GetMethodFromHandle(JitHookHelpers.CreateHandleForHandlePointer(context), JitHookHelpers.GetDeclaringTypeOfMethodHandle(context).TypeHandle);
            return GenericSharing.FindInstantiation(sharedForm, JitHookHelpers.GetTypeFromNativeHandle(context));
        }
    }
}
