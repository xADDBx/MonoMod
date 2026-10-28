using MonoMod.Core;
using MonoMod.Core.Platforms;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace MonoMod.RuntimeDetour
{
    /// <summary>
    /// A hook on the instantiations of an open generic method, or of a method on an open generic type. Each instantiation is
    /// hooked by a <see cref="Hook"/> to the matching instantiation of the target, a generic method definition taking the type
    /// parameters of the source's declaring type, then those of the source method, and otherwise the parameters a
    /// <see cref="Hook"/> target takes. Instantiations which the target's generic constraints don't allow aren't hooked.
    /// </summary>
    /// <remarks>
    /// Hooked are the instantiations which have hooks of any kind or run the body shared by reference type instantiations, and
    /// those which the runtime compiles after this hook was applied. Hook any other instantiation with a <see cref="Hook"/> on it.
    /// Unlike a <see cref="Hook"/>, a <see cref="GenericHook"/> stays applied until it is undone or disposed.
    /// </remarks>
    public sealed class GenericHook : IDetour
    {
        private static readonly object registryLock = new();
        private static volatile GenericHook[] appliedHooks = [];

        private readonly IDetourFactory factory;
        private readonly ISharedGenericDetourFactory? sharedFactory;
        private readonly object sync = new();
        private readonly Dictionary<MethodBase, Hook?> instantiations = [];
        private readonly Dictionary<MethodBase, Thread> creating = [];
        private volatile bool isApplied;
        private bool disposedValue;

        /// <summary>
        /// Gets the open generic source method.
        /// </summary>
        public MethodBase Source { get; }

        /// <summary>
        /// Gets the generic method definition hooked onto each instantiation of <see cref="Source"/>.
        /// </summary>
        public MethodInfo Target { get; }

        /// <inheritdoc/>
        public DetourConfig? Config { get; }

        /// <inheritdoc/>
        public bool IsValid => !disposedValue;

        /// <inheritdoc/>
        public bool IsApplied => isApplied;

        /// <summary>
        /// Gets the hooks on the instantiations hooked so far.
        /// </summary>
        public IEnumerable<Hook> Instantiations
        {
            get
            {
                lock (sync)
                    return instantiations.Values.OfType<Hook>().ToList();
            }
        }

        /// <inheritdoc cref="GenericHook(MethodBase, MethodInfo, DetourConfig?, bool, IDetourFactory?)"/>
        public GenericHook(MethodBase source, MethodInfo target, bool applyByDefault = true)
            : this(source, target, DetourContext.GetDefaultConfig(), applyByDefault) { }

        /// <summary>
        /// Constructs a <see cref="GenericHook"/> hooking the instantiations of <paramref name="source"/> with those of <paramref name="target"/>.
        /// </summary>
        /// <param name="source">The open generic source method.</param>
        /// <param name="target">The generic target method definition.</param>
        /// <param name="config">The <see cref="DetourConfig"/> to use for the hook on each instantiation.</param>
        /// <param name="applyByDefault">Whether or not this hook should be applied when the constructor finishes.</param>
        /// <param name="factory">The <see cref="IDetourFactory"/> to use for the hook on each instantiation, the default one if <see langword="null"/>.</param>
        public GenericHook(MethodBase source, MethodInfo target, DetourConfig? config, bool applyByDefault = true, IDetourFactory? factory = null)
        {
            Helpers.ThrowIfArgumentNull(source);
            Helpers.ThrowIfArgumentNull(target);

            var declType = source.DeclaringType;
            if (declType is null || !(source.IsGenericMethodDefinition || !source.IsGenericMethod && declType.IsGenericTypeDefinition)
                || declType.IsGenericType && !declType.IsGenericTypeDefinition)
                throw new ArgumentException("Source method is not an open generic definition, hook its instantiations with Hook instead", nameof(source));
            var typeParams = TypeArguments(source).Length;
            if (!target.IsGenericMethodDefinition || target.GetGenericArguments().Length != typeParams)
                throw new ArgumentException($"Target method must be a generic method definition with {typeParams} type parameters", nameof(target));

            Source = source;
            Target = target;
            this.factory = factory ?? DetourContext.GetDefaultFactory();
            sharedFactory = this.factory as ISharedGenericDetourFactory;
            Config = config;

            MMDbgLog.Trace($"Creating GenericHook from {Source} to {Target}");

            if (applyByDefault)
                Apply();
        }

        // the type arguments of the declaring type, then those of the method
        private static Type[] TypeArguments(MethodBase method)
            => [.. method.DeclaringType!.GetGenericArguments(), .. method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes];

        // the first check keeps MetadataToken off methods which throw on it, like dynamic methods
        private bool IsInstantiationOfSource(MethodBase method)
            => (method.IsGenericMethod || method.DeclaringType is { IsGenericType: true }) && !method.ContainsGenericParameters
            && method.MetadataToken == Source.MetadataToken && method.Module == Source.Module;

        // called for instantiations calling a shared body without an entry, which wait for the hooks other threads are creating
        // on them, and for methods getting a hook of any kind, which mustn't: the threads creating those may be waiting for theirs
        internal static void OfferInstantiation(MethodBase closed, bool wait)
        {
            foreach (var hook in appliedHooks)
            {
                if (hook.IsInstantiationOfSource(closed))
                    hook.AddInstantiation(closed, wait);
            }
        }

        private void OnMethodCompiled(MethodBase method)
        {
            if (!IsInstantiationOfSource(method))
                return;
            // instantiations running a shared body are added when they first call it
            if (sharedFactory!.TryGetSharedGenericForm(method, out var sharedForm))
                _ = DetourManager.GetSharedGenericDetour(sharedFactory, sharedForm);
            else
                AddInstantiation(method);
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Instantiations are hooked from inside calls and compilations, which must not fail because of it.")]
        private void AddInstantiation(MethodBase closed, bool wait = false)
        {
            closed = PlatformTriple.Current.GetIdentifiable(closed);
            lock (sync)
            {
                if (!isApplied)
                    return;
                if (instantiations.ContainsKey(closed))
                {
                    while (wait && creating.TryGetValue(closed, out var thread) && thread != Thread.CurrentThread)
                        _ = Monitor.Wait(sync);
                    return;
                }
                // recorded first, creating its hook offers it again
                instantiations.Add(closed, null);
                creating.Add(closed, Thread.CurrentThread);
            }

            MethodInfo? target = null;
            try
            {
                target = Target.MakeGenericMethod(TypeArguments(closed));
                // outside the lock: creating it offers the instantiation to the other generic hooks, which may be doing the same
                var hook = new Hook(closed, target, factory, Config, false);
                lock (sync)
                {
                    if (disposedValue)
                    {
                        hook.Dispose();
                        return;
                    }
                    instantiations[closed] = hook;
                    if (isApplied)
                        hook.Apply();
                }
            }
            catch (ArgumentException) when (target is null)
            {
                // the target's constraints don't allow it
            }
            catch (Exception e)
            {
                MMDbgLog.Error($"Could not hook instantiation {closed} of {Source} with {Target}: {e}");
            }
            finally
            {
                lock (sync)
                {
                    _ = creating.Remove(closed);
                    Monitor.PulseAll(sync);
                }
            }
        }

        // runs the body shared by the reference type instantiations, null if the constraints don't allow it
        private MethodBase? MakeSharedInstantiation()
        {
            try
            {
                var declType = Source.DeclaringType!;
                if (declType.IsGenericTypeDefinition)
                    declType = declType.MakeGenericType(declType.GetGenericArguments().Select(ReferenceArgument).ToArray());
                var method = MethodBase.GetMethodFromHandle(Source.MethodHandle, declType.TypeHandle)!;
                return method is MethodInfo { IsGenericMethodDefinition: true } info
                    ? info.MakeGenericMethod(info.GetGenericArguments().Select(ReferenceArgument).ToArray())
                    : method;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        // object may not satisfy the constraints, but a class or interface constraint satisfies itself
        private static Type ReferenceArgument(Type parameter)
            => parameter.GetGenericParameterConstraints().FirstOrDefault(c => !c.IsValueType && !c.ContainsGenericParameters) ?? typeof(object);

        private void CheckDisposed()
        {
            if (disposedValue)
                throw new ObjectDisposedException(ToString());
        }

        /// <summary>
        /// Applies this hook if it was not already applied.
        /// </summary>
        public void Apply()
        {
            lock (sync)
            {
                CheckDisposed();
                if (isApplied)
                    return;
                MMDbgLog.Trace($"Applying GenericHook from {Source} to {Target}");
                isApplied = true;
                foreach (var hook in instantiations.Values)
                    hook?.Apply();
                lock (registryLock)
                    appliedHooks = [.. appliedHooks, this];
                // pairs with DetourManager.GetDetourState
                Thread.MemoryBarrier();
                if (sharedFactory is not null)
                    sharedFactory.GenericMethodCompiled += OnMethodCompiled;
            }

            if (sharedFactory is not null)
            {
                if (MakeSharedInstantiation() is { } sharedInstantiation && sharedFactory.TryGetSharedGenericForm(sharedInstantiation, out var sharedForm))
                    _ = DetourManager.GetSharedGenericDetour(sharedFactory, sharedForm);
                // shared bodies may have run instantiations without entries already
                DetourManager.ResetUnknownInstantiations(IsInstantiationOfSource);
            }
            foreach (var method in DetourManager.DetouredMethods.Where(IsInstantiationOfSource))
                AddInstantiation(method);
        }

        /// <summary>
        /// Undoes this hook if it was applied.
        /// </summary>
        public void Undo()
        {
            lock (sync)
            {
                CheckDisposed();
                if (!isApplied)
                    return;
                MMDbgLog.Trace($"Undoing GenericHook from {Source} to {Target}");
                isApplied = false;
                foreach (var hook in instantiations.Values)
                    hook?.Undo();
                lock (registryLock)
                    appliedHooks = appliedHooks.Where(h => h != this).ToArray();
                if (sharedFactory is not null)
                    sharedFactory.GenericMethodCompiled -= OnMethodCompiled;
            }
        }

        /// <summary>
        /// Undoes this hook and disposes the hooks on its instantiations.
        /// </summary>
        public void Dispose()
        {
            lock (sync)
            {
                if (disposedValue)
                    return;
                Undo();
                foreach (var hook in instantiations.Values)
                    hook?.Dispose();
                instantiations.Clear();
                disposedValue = true;
            }
        }
    }
}
