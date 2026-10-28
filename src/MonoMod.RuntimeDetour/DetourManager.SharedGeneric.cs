using MonoMod.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MonoMod.RuntimeDetour
{
    public static partial class DetourManager
    {
        // shared generic bodies stay detoured, hooks on their instantiations only set and clear entries
        private static readonly Dictionary<MethodBase, ICoreSharedGenericDetour> sharedGenericDetours = [];

        internal static ICoreSharedGenericDetour GetSharedGenericDetour(ISharedGenericDetourFactory factory, MethodBase sharedForm)
        {
            lock (sharedGenericDetours)
            {
                if (!sharedGenericDetours.TryGetValue(sharedForm, out var detour))
                    sharedGenericDetours.Add(sharedForm, detour = factory.CreateSharedGenericDetour(sharedForm, static closed => GenericHook.OfferInstantiation(closed, wait: true)));
                return detour;
            }
        }

        internal static void ResetUnknownInstantiations(Func<MethodBase, bool> sharedFormMatches)
        {
            lock (sharedGenericDetours)
            {
                foreach (var detour in sharedGenericDetours.Values.Where(d => sharedFormMatches(d.SharedForm)))
                    detour.ResetUnknownInstantiations();
            }
        }

        // every method a hook was created on so far
        internal static IEnumerable<MethodBase> DetouredMethods => detourStates.Where(s => s.Value.HookCreated).Select(s => s.Key);

        private static ICoreDetour CreateRootDetour(IDetourFactory factory, MethodBase source, MethodBase syncProxy)
            => factory is ISharedGenericDetourFactory generic && generic.TryGetSharedGenericForm(source, out var sharedForm)
                ? new SharedGenericEntryDetour(GetSharedGenericDetour(generic, sharedForm), source, syncProxy)
                : factory.CreateDetour(new(source, syncProxy)
                {
                    ApplyByDefault = false,
                    CreateSourceCloneIfNotILClone = true,
                });

        private sealed class SharedGenericEntryDetour(ICoreSharedGenericDetour shared, MethodBase source, MethodBase target) : ICoreDetour
        {
            public MethodBase Source => source;
            public MethodBase Target => target;
            public bool IsApplied { get; private set; }

            public void Apply()
            {
                shared.SetEntry(source, target);
                IsApplied = true;
            }

            public void Undo()
            {
                shared.ClearEntry(source);
                IsApplied = false;
            }

            public void Dispose() => Undo();
        }
    }
}
