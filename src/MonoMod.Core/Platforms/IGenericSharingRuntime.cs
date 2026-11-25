using System;
using System.Reflection;

namespace MonoMod.Core.Platforms
{
    /// <param name="Method">The method whose compiled code it is.</param>
    /// <param name="Handle">The handle to compile and find that code with.</param>
    /// <param name="ContextIsThis">Whether calls are told apart by the type of <see langword="this"/> instead of a generic context.</param>
    internal readonly record struct SharedGenericCode(MethodBase Method, RuntimeMethodHandle Handle, bool ContextIsThis);

    internal interface IGenericSharingRuntime
    {
        /// <summary>
        /// Gets the code <paramref name="method"/> runs, if that code is shared with other instantiations.
        /// </summary>
        bool TryGetSharedCode(MethodBase method, out SharedGenericCode code);

        /// <summary>
        /// Gets the instantiation a call into <paramref name="code"/> runs as, from its generic context, or from the type handle of <see langword="this"/>.
        /// </summary>
        MethodBase ResolveInstantiation(SharedGenericCode code, IntPtr context);
    }
}
