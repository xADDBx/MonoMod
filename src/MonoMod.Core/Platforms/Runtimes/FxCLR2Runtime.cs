using System;
using System.Reflection;

namespace MonoMod.Core.Platforms.Runtimes
{
    internal sealed class FxCLR2Runtime : FxBaseRuntime
    {

        public FxCLR2Runtime(ISystem system) : base(system)
        {
            if (AbiCore is null)
            {
                // TODO: where is the generic context passed on CLR 2?
                AbiCore = system.DefaultAbi;
            }
        }

        // where CLR 2 passes the generic context is unknown, and it lacks the reflection internals resolving instantiations
        public override bool ContextIsThis(MethodBase sharedForm)
            => throw new PlatformNotSupportedException("Detours on generic bodies shared between instantiations are not supported on .NET Framework 3.5");

        /*
        public override RuntimeFeature Features => base.Features & ~RuntimeFeature.DisableInlining;
        // TODO: figure out how to disable inlining on CLR 2
        public override void DisableInlining(MethodBase method) {
            throw new PlatformNotSupportedException();
        }
        */
    }
}
