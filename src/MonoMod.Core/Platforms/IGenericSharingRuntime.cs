using System;
using System.Reflection;

namespace MonoMod.Core.Platforms
{
    internal interface IGenericSharingRuntime
    {
        // null if the body of closed isn't shared
        MethodBase? GetSharedForm(MethodBase closed);

        // whether a shared generic body knows which instantiation it runs as from the type of this, instead of a hidden
        // argument identifying the method instantiation (generic methods) or the type (static methods, value type this)
        bool ContextIsThis(MethodBase sharedForm);

        // context is the hidden argument, or the type handle of this
        MethodBase? ResolveInstantiation(MethodBase sharedForm, IntPtr context);
    }

    internal static class GenericSharing
    {
        public static MethodBase GetDefinition(MethodBase method) => method.Module.ResolveMethod(method.MetadataToken)!;

        // the type arguments of the declaring type, then those of the method
        public static Type[] TypeArguments(MethodBase method)
            => [.. method.DeclaringType!.GetGenericArguments(), .. method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes];

        // type may be a subclass of the instantiation
        public static MethodBase? FindInstantiation(MethodBase sharedForm, Type? type)
        {
            var typeDef = sharedForm.DeclaringType!.GetGenericTypeDefinition();
            for (; type is not null; type = type.BaseType)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeDef)
                    return MethodBase.GetMethodFromHandle(GetDefinition(sharedForm).MethodHandle, type.TypeHandle);
            }
            return null;
        }
    }
}
