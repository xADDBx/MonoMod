using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace MonoMod.Core
{
    /// <summary>
    /// An optional extension of <see cref="IDetourFactory"/> for generic methods whose native body is shared between several
    /// instantiations. A plain <see cref="ICoreDetour"/> on one of them would affect all of them; a shared generic detour patches
    /// the body once and dispatches each call to the entry registered for its instantiation.
    /// </summary>
    [CLSCompliant(true)]
    public interface ISharedGenericDetourFactory
    {
        /// <summary>
        /// Gets the method representing the native body <paramref name="closed"/> runs, if that body is shared with other instantiations.
        /// </summary>
        /// <param name="closed">A closed generic method, or a method on a closed generic type.</param>
        /// <param name="sharedForm">The shared form, the same object for every instantiation sharing the body.</param>
        /// <returns><see langword="true"/> if <paramref name="closed"/> runs a shared body; otherwise <see langword="false"/>.</returns>
        bool TryGetSharedGenericForm(MethodBase closed, [MaybeNullWhen(false)] out MethodBase sharedForm);

        /// <summary>
        /// Creates and applies the detour on the shared body represented by <paramref name="sharedForm"/>. Instantiations
        /// without an entry run the original body. Only one such detour should exist per shared form.
        /// </summary>
        /// <param name="sharedForm">A shared form returned by <see cref="TryGetSharedGenericForm(MethodBase, out MethodBase)"/>.</param>
        /// <param name="onUnknownInstantiation">Invoked, possibly concurrently and repeatedly, when an instantiation without an
        /// entry first calls the shared body, before the call is dispatched. It may set an entry for it.</param>
        /// <returns>The created <see cref="ICoreSharedGenericDetour"/>.</returns>
        ICoreSharedGenericDetour CreateSharedGenericDetour(MethodBase sharedForm, Action<MethodBase> onUnknownInstantiation);

        /// <summary>
        /// Raised on the compiling thread when the runtime compiles the body of a generic instantiation: either an instantiation
        /// which doesn't share its body, or a shared form. Detours which handlers create on that method apply to the code being
        /// compiled. Not reported are methods compiled while a detour is being created on the same thread, and, on Mono,
        /// instantiations whose type arguments are all reference types. Never raised on .NET Framework 3.5 and Mono before 5.6.
        /// </summary>
        event Action<MethodBase>? GenericMethodCompiled;
    }

    /// <summary>
    /// A detour on a shared generic body which dispatches each instantiation to its own entry.
    /// </summary>
    [CLSCompliant(true)]
    public interface ICoreSharedGenericDetour : ICoreDetourBase
    {
        /// <summary>
        /// Gets the shared form this detour was created for.
        /// </summary>
        MethodBase SharedForm { get; }

        /// <summary>
        /// Routes calls of the instantiation <paramref name="closed"/> to <paramref name="target"/>.
        /// </summary>
        /// <param name="closed">An instantiation running the shared body.</param>
        /// <param name="target">A method with the signature of <paramref name="closed"/>, with <c>this</c> as an explicit
        /// first parameter for instance methods.</param>
        void SetEntry(MethodBase closed, MethodBase target);

        /// <summary>
        /// Makes calls of the instantiation <paramref name="closed"/> run the original body again.
        /// </summary>
        /// <param name="closed">An instantiation running the shared body.</param>
        void ClearEntry(MethodBase closed);

        /// <summary>
        /// Forgets the instantiations without an entry seen so far, so that their next call invokes <c>onUnknownInstantiation</c> again.
        /// </summary>
        void ResetUnknownInstantiations();
    }
}
