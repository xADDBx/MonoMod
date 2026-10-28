using MonoMod.Core.Platforms.Architectures;
using MonoMod.Utils;
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using static MonoMod.Core.Interop.CoreCLR;
using static MonoMod.Core.Interop.Fx;

namespace MonoMod.Core.Platforms.Runtimes
{
    internal sealed class FxCLR4Runtime : FxBaseRuntime
    {

        public FxCLR4Runtime(ISystem system) : base(system)
        {
            if (system.DefaultAbi is { } abi)
            {
                // the only place I could find the actual version number of 4.5 (without just testing myself on a Win7 VM) is here:
                // https://stackoverflow.com/a/11512846
                if (PlatformDetection.Architecture == ArchitectureKind.x86_64 &&
                    (PlatformDetection.RuntimeVersion.Revision >= 17379 ||
                    PlatformDetection.RuntimeVersion.Minor >= 5))
                {
                    AbiCore = AbiForCoreFx45X64(abi);
                }

                if (PlatformDetection.Architecture == ArchitectureKind.Arm64)
                {
                    AbiCore = AbiForCoreFx45ARM64(abi);
                }
            }
        }


        public override RuntimeFeature Features
            => base.Features & ~RuntimeFeature.RequiresBodyThunkWalking;

        private readonly object sync = new();
        // never freed, the JIT may run through them at any time
        private (V21.CompileMethodDelegate Hook, IAllocatedMemory Stub)? jitHook;

        // the JIT hook is only installed once something wants to know about compiled methods
        public override event OnMethodCompiledCallback? OnMethodCompiled
        {
            add
            {
                lock (sync)
                    jitHook ??= InstallJitHook();
                base.OnMethodCompiled += value;
            }
            remove => base.OnMethodCompiled -= value;
        }

        private unsafe (V21.CompileMethodDelegate, IAllocatedMemory) InstallJitHook()
        {
            // compileMethod is the first slot of ICorJitCompiler
            var compileMethodSlot = GetVTableEntry(JitObject, 0);

            var hook = Core21Runtime.CreateFxCompileMethodHook(this, InvokeCompileMethodPtr, *compileMethodSlot);
            var hookPtr = Marshal.GetFunctionPointerForDelegate(hook);

            // the JIT can't compile the hook while going through it
            InvokeCompileMethodToPrepare(hookPtr);

            // The JIT is shared by all app domains, but calls through the hook would compile in its domain. A stub only sends it the
            // calls ICLRRuntimeHost::GetCurrentAppDomainId says are from this domain; ids aren't reused, so after an unload none are.
            // Sources: Architectures/*/fx_jit_filter*
            var (code, hostOffs, idOffs, hookOffs, origOffs) = PlatformDetection.Architecture switch
            {
                ArchitectureKind.x86_64 => (JitFilterX64.ToArray(), 0x14, 0x36, 0x3C, 0x48),
                ArchitectureKind.x86 => (JitFilterX86.ToArray(), 0x04, 0x10, 0x15, 0x1C),
                ArchitectureKind.Arm64 => (JitFilterArm64.ToArray(), 0x58, 0x70, 0x60, 0x68),
                var arch => throw new PlatformNotSupportedException($"No JIT hook on {arch}"),
            };
            // not in the .NET Framework 3.5 reference assemblies
            Unsafe.WriteUnaligned(ref code[hostOffs], (IntPtr)typeof(RuntimeEnvironment).GetMethod("GetRuntimeInterfaceAsIntPtr")!.Invoke(null,
                [new Guid("90F1A06E-7712-4762-86B5-7A5EBA6BDB02"), new Guid("90F1A06C-7712-4762-86B5-7A5EBA6BDB02")])!); // CLRRuntimeHost, ICLRRuntimeHost
            Unsafe.WriteUnaligned(ref code[idOffs], AppDomain.CurrentDomain.Id);
            Unsafe.WriteUnaligned(ref code[hookOffs], hookPtr);
            Unsafe.WriteUnaligned(ref code[origOffs], *compileMethodSlot);
            var stub = Shared.CreateSingleExecutableStub(System, code);

            var stubPtr = stub.BaseAddress;
            System.PatchData(PatchTargetKind.ReadOnly, (IntPtr)compileMethodSlot, new ReadOnlySpan<byte>(&stubPtr, IntPtr.Size), default);
            return (hook, stub);
        }

        private static ReadOnlySpan<byte> JitFilterX64 => [
            0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x48, 0x83, 0xEC, 0x38, 0xC7, 0x44, 0x24, 0x20, 0x00, 0x00,
            0x00, 0x00, 0x48, 0xB9, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x48, 0x8D, 0x54, 0x24,
            0x20, 0x48, 0x8B, 0x01, 0xFF, 0x50, 0x48, 0x8B, 0x44, 0x24, 0x20, 0x48, 0x83, 0xC4, 0x38, 0x41,
            0x59, 0x41, 0x58, 0x5A, 0x59, 0x3D, 0x22, 0x22, 0x22, 0x22, 0x48, 0xB8, 0x33, 0x33, 0x33, 0x33,
            0x33, 0x33, 0x33, 0x33, 0x74, 0x0A, 0x48, 0xB8, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44,
            0xFF, 0xE0,
        ];

        private static ReadOnlySpan<byte> JitFilterX86 => [
            0x6A, 0x00, 0x54, 0xB8, 0x11, 0x11, 0x11, 0x11, 0x50, 0x8B, 0x00, 0xFF, 0x50, 0x24, 0x58, 0x3D,
            0x22, 0x22, 0x22, 0x22, 0xB8, 0x33, 0x33, 0x33, 0x33, 0x74, 0x05, 0xB8, 0x44, 0x44, 0x44, 0x44,
            0xFF, 0xE0,
        ];

        private static ReadOnlySpan<byte> JitFilterArm64 => [
            0xFD, 0x7B, 0xBB, 0xA9, 0xFD, 0x03, 0x00, 0x91, 0xE0, 0x07, 0x01, 0xA9, 0xE2, 0x0F, 0x02, 0xA9,
            0xE4, 0x17, 0x03, 0xA9, 0xFF, 0x43, 0x00, 0xB9, 0x00, 0x02, 0x00, 0x58, 0xE1, 0x03, 0x01, 0x91,
            0x10, 0x00, 0x40, 0xF9, 0x10, 0x26, 0x40, 0xF9, 0x00, 0x02, 0x3F, 0xD6, 0xE9, 0x43, 0x40, 0xB9,
            0xE0, 0x07, 0x41, 0xA9, 0xE2, 0x0F, 0x42, 0xA9, 0xE4, 0x17, 0x43, 0xA9, 0xFD, 0x7B, 0xC5, 0xA8,
            0x8A, 0x01, 0x00, 0x18, 0xF0, 0x00, 0x00, 0x58, 0x3F, 0x01, 0x0A, 0x6B, 0x40, 0x00, 0x00, 0x54,
            0xD0, 0x00, 0x00, 0x58, 0x00, 0x02, 0x1F, 0xD6, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];

        private unsafe IntPtr GetMethodBodyPtr(MethodBase method, RuntimeMethodHandle handle)
        {
            var md = (V48.MethodDesc*)handle.Value;

            // https://github.com/dotnet/runtime/blob/70ae3df4a6f3c92fb6b315afc405edd10ff38579/src/coreclr/vm/genmeth.cpp#L1061-L1072
            if (md->IsUnboxingStub && (!md->HasMethodInstantiation || md->IsGenericMethodDefinition))
                md = V48.MethodDesc.FindTightlyBoundWrappedMethodDesc(md);
            else if (md->IsInstantiatingStub && md->TryAsInstantiated(out var inst))
                md = inst->IMD_GetWrappedMethodDesc();

            var ptr = (IntPtr)md->GetNativeCode();

            return ptr;
        }

        public override unsafe IntPtr GetMethodEntryPoint(MethodBase method)
        {
            method = GetIdentifiable(method);
            var handle = GetMethodHandle(method);

            var didPrepare = false;
            GetPtr:
            // we want to invoke _CompileMethod, not _PrepareMethod because _CompileMethod calls directly into DoPrestub, while _PrepareMethod
            // seems to skip prestub-intercepted remoting methods
            Helpers.Assert(TryInvokeBclCompileMethod(handle));
            // get then throw away the function pointer to try to ensure that the pointer is restored
            _ = handle.GetFunctionPointer();
            var ptr = GetMethodBodyPtr(method, handle);

            if (ptr == IntPtr.Zero)
            { // the method hasn't been JITted yet
                if (!didPrepare)
                {
                    Helpers.Assert(TryInvokeBclCompileMethod(handle));
                    didPrepare = true;
                    goto GetPtr;
                }
                else
                {
                    // we've already run a prepare, lets try another approach
                    ptr = handle.GetFunctionPointer();

                    throw new InvalidOperationException($"Could not get entry point normally, GetFunctionPointer() = {ptr:x16}");
                }
            }

            return ptr;
        }
    }
}
