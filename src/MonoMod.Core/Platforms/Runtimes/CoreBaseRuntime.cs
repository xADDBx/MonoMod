using MonoMod.Core.Utils;
using MonoMod.Utils;
using System;

namespace MonoMod.Core.Platforms.Runtimes
{
    internal abstract class CoreBaseRuntime : FxCoreBaseRuntime, IInitialize
    {

        public static CoreBaseRuntime CreateForVersion(Version version, ISystem system, IArchitecture arch)
        {

            switch (version.Major)
            {
                case 2:
                case 4:
                    // .NET Core 2.x
                    // Note that .NET Core 2.x does not return a reasonable number for its version like 2.1, instead it gives 4.6.xxxxxx, like Framework.
                    return new Core21Runtime(system);

                case 3:
                    // .NET Core 3.x
                    return version.Minor switch
                    {
                        0 => new Core30Runtime(system),
                        1 => new Core31Runtime(system),
                        _ => throw new PlatformNotSupportedException($"Unknown .NET Core 3.x minor version {version.Minor}"),
                    };

                case 5:
                    // .NET 5.0.x
                    return new Core50Runtime(system);

                case 6:
                    // .NET 6.0.x
                    return new Core60Runtime(system, arch);

                case 7:
                    // .NET 7.0.x
                    return new Core70Runtime(system, arch);
                case 8:
                    // .NET 8.0.x
                    return new Core80Runtime(system, arch);
                case 9:
                    // .NET 9.0.x
                    return new Core90Runtime(system, arch);
                case 10:
                    // .NET 10.0.x
                    return new Core100Runtime(system, arch);
                case 11:
                    // .NET 11.0.x
                    return new Core110Runtime(system, arch);

                // currently, we need to manually add support for new versions.
                // TODO: possibly fall back to a JIT GUID check if we can?

                default:
                    throw new PlatformNotSupportedException($"CoreCLR version {version} is not supported");
            }

            throw new NotImplementedException();
        }

        public override RuntimeKind Target => RuntimeKind.CoreCLR;

        protected CoreBaseRuntime(ISystem system) : base(system)
        {
            if (system.DefaultAbi is { } abi)
            {
                if (PlatformDetection.Architecture == ArchitectureKind.x86_64)
                {
                    AbiCore = AbiForCoreFx45X64(abi);
                }
                else if (PlatformDetection.Architecture == ArchitectureKind.Arm64)
                {
                    AbiCore = AbiForCoreFx45ARM64(abi);
                }
            }
        }

        void IInitialize.Initialize()
        {
            InstallJitHook(JitObject);
        }

        protected virtual void InstallJitHook(IntPtr jit) => InstallManagedJitHook(jit);

        protected abstract void InstallManagedJitHook(IntPtr jit);

        private INativeExceptionHelper? lazyNativeExceptionHelper;
        protected INativeExceptionHelper? NativeExceptionHelper => lazyNativeExceptionHelper ??= System.NativeExceptionHelper;
        protected IntPtr EHNativeToManaged(IntPtr target, out IDisposable? handle)
        {
            if (NativeExceptionHelper is not null)
            {
                return NativeExceptionHelper.CreateNativeToManagedHelper(target, out handle);
            }
            handle = null;
            return target;
        }
        protected IntPtr EHManagedToNative(IntPtr target, out IDisposable? handle)
        {
            if (NativeExceptionHelper is not null)
            {
                return NativeExceptionHelper.CreateManagedToNativeHelper(target, out handle);
            }
            handle = null;
            return target;
        }
    }
}
