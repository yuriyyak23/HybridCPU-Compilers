using HybridCPU_ISE.CloseToHSL.Core.Runtime.Managed;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase01RuntimeKernelBootstrapTests
{
    private const string BootstrapHelper = "__hybridcpu_runtime_bootstrap";

    [Fact]
    public void KernelBoot_OwnsSingleContextStackVmAndVtCarrier()
    {
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        HybridCpuKernelBootResultV1 boot = kernel.Boot(BootInfo());

        Assert.True(boot.IsSuccess, boot.Reason);
        HybridCpuExecutionContextDescriptorV1 context = Assert.IsType<HybridCpuExecutionContextDescriptorV1>(boot.Context);
        Assert.Equal(0, context.VirtualThreadCarrier);
        Assert.Equal(0x2100_0000UL, context.InitialStackPointer);
        Assert.Equal(2, context.VmMappings.Count);
        Assert.Equal(HybridCpuVmProtectionV1.Read | HybridCpuVmProtectionV1.Execute, context.VmMappings[0].Protection);
        Assert.Equal(HybridCpuVmProtectionV1.Read | HybridCpuVmProtectionV1.Write, context.VmMappings[1].Protection);
        Assert.Equal(HybridCpuKernelStatusV1.Unsupported, kernel.Boot(BootInfo()).Status);
    }

    [Fact]
    public void KernelVmAndTrapTransitions_AreExactBoundedAndFailClosed()
    {
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(BootInfo()).IsSuccess);
        HybridCpuVmRangeV1 reserved = new HybridCpuVmRangeV1(0x3000_0000, 0x2000, HybridCpuVmProtectionV1.None);
        Assert.True(kernel.ReserveVm(reserved).IsSuccess);
        Assert.Equal(HybridCpuKernelStatusV1.AddressConflict, kernel.ReserveVm(reserved).Status);
        Assert.True(kernel.CommitVm(reserved with { Protection = HybridCpuVmProtectionV1.Read | HybridCpuVmProtectionV1.Write }).IsSuccess);
        Assert.True(kernel.ProtectVm(reserved with { Protection = HybridCpuVmProtectionV1.Read }).IsSuccess);
        Assert.True(kernel.ReleaseVm(reserved.Address, reserved.Size).IsSuccess);

        HybridCpuArchitecturalTrapFrameV1 trap = new HybridCpuArchitecturalTrapFrameV1(0, 0x1000_0100, 0x20ff_ff00, 7, 0xdead_0000);
        Assert.True(kernel.TrapEntry(trap).IsSuccess);
        Assert.Equal(HybridCpuKernelStatusV1.TrapStateMismatch,
            kernel.TrapReturn(trap with { ProgramCounter = trap.ProgramCounter + 32 }).Status);
        Assert.True(kernel.TrapReturn(trap).IsSuccess);
        Assert.Equal(HybridCpuKernelStatusV1.Unsupported,
            kernel.HostTransition(new(HybridCpuHostServiceV1.Console, 0, [])).Status);
        Assert.Equal(HybridCpuKernelStatusV1.InvalidRequest,
            kernel.ProtectVm(new(0x1000_0000, 0x20_0000, (HybridCpuVmProtectionV1)0x80)).Status);
        Assert.Equal(HybridCpuKernelStatusV1.InvalidRequest,
            new DeterministicRuntimeKernelV1().Boot(BootInfo() with { ImageContractDigest = "not-a-digest" }).Status);

        DeterministicRuntimeKernelV1 exiting = new DeterministicRuntimeKernelV1();
        Assert.True(exiting.Boot(BootInfo()).IsSuccess);
        Assert.Equal(HybridCpuKernelStatusV1.Success,
            exiting.HostTransition(new(HybridCpuHostServiceV1.ProcessExit, 0, [7])).Status);
        Assert.Equal(HybridCpuKernelStatusV1.NoCurrentContext,
            exiting.ReserveVm(new(0x3000_0000, 0x1000, HybridCpuVmProtectionV1.None)).Status);
    }

    [Fact]
    public void ManagedBootstrap_RegistersCanonicalMetadataAndInvokesVersionedHelper()
    {
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(BootInfo()).IsSuccess);
        HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor = Descriptor();
        int calls = 0;
        Dictionary<string, HybridCpuRuntimeHelperEntryV1> helpers = new Dictionary<string, HybridCpuRuntimeHelperEntryV1>(StringComparer.Ordinal)
        {
            ["__hybridcpu_runtime_bootstrap"] = context =>
            {
                calls++;
                return context.ContextCarrierAddress != 0;
            }
        };

        HybridCpuManagedBootstrapResultV1 result = Runtime().Bootstrap(descriptor, kernel, helpers,
            initializers: new Dictionary<string, Func<bool>>(StringComparer.Ordinal)
            { ["__hybridcpu_static_init_a"] = static () => true });

        Assert.True(result.IsSuccess, result.Reason);
        Assert.Equal(1, calls);
        Assert.Equal(["method:a"], result.RegisteredMethods);
        Assert.Equal(["root:a"], result.RegisteredStaticRoots);
        Assert.Equal(["__hybridcpu_static_init_a"], result.RegisteredModuleInitializers);
    }

    [Fact]
    public void ManagedBootstrap_MissingHelperAndTamperedDescriptorFailBeforeRegistration()
    {
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(BootInfo()).IsSuccess);
        HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor = Descriptor();
        HybridCpuManagedBootstrapRuntimeV1 runtime = Runtime();

        HybridCpuManagedBootstrapResultV1 missing = runtime.Bootstrap(descriptor, kernel,
            new Dictionary<string, HybridCpuRuntimeHelperEntryV1>());
        Assert.Equal(HybridCpuManagedBootstrapStatusV1.MissingHelper, missing.Status);
        Assert.Empty(missing.RegisteredMethods);

        HybridCpuManagedBootstrapResultV1 tampered = runtime.Bootstrap(
            descriptor with { ManagedEntrySymbol = "tampered" }, kernel,
            new Dictionary<string, HybridCpuRuntimeHelperEntryV1>());
        Assert.Equal(HybridCpuManagedBootstrapStatusV1.InvalidDescriptor, tampered.Status);
        Assert.Empty(tampered.RegisteredMethods);

        HybridCpuImageRuntimeBootstrapDescriptorV1 skewed = HybridCpuImageRuntimeBootstrapContractV1.Create(
            new string('f', 64), descriptor.RuntimeEntrySymbol, descriptor.ManagedEntrySymbol,
            descriptor.RuntimeHelpers, descriptor.CodeManagerRecords, descriptor.StaticRoots, descriptor.ModuleInitializers);
        Assert.Equal(HybridCpuManagedBootstrapStatusV1.InvalidDescriptor,
            runtime.Bootstrap(skewed, kernel, new Dictionary<string, HybridCpuRuntimeHelperEntryV1>()).Status);
    }

    [Fact]
    public void BootstrapDescriptor_IsCanonicalAndDeterministic()
    {
        HybridCpuImageRuntimeBootstrapDescriptorV1 first = Descriptor(reverse: false);
        HybridCpuImageRuntimeBootstrapDescriptorV1 second = Descriptor(reverse: true);

        Assert.Equal(first.DescriptorDigest, second.DescriptorDigest);
        Assert.Equal(first.RuntimeHelpers, second.RuntimeHelpers);
        Assert.Equal(first.CodeManagerRecords, second.CodeManagerRecords);
        Assert.Equal(first.StaticRoots, second.StaticRoots);
        Assert.Equal(first.ModuleInitializers, second.ModuleInitializers);
        Assert.Equal(first.DescriptorDigest, HybridCpuImageRuntimeBootstrapContractV1.ComputeDigest(first));
        Assert.Matches("^[0-9a-f]{64}$", first.DescriptorDigest);
    }

    [Fact]
    public void NewProjectsRespectAuthorityDependencyBoundary()
    {
        string root = FindRepositoryRoot();
        string platformProject = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Platform.Contracts", "HybridCPU.Platform.Contracts.csproj"));
        string kernelProject = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_RuntimeKernel", "HybridCPU.RuntimeKernel.csproj"));
        string runtimeProject = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_ManagedRuntime", "HybridCPU.ManagedRuntime.csproj"));
        string coreProject = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "Core", "HybridCPU.Compiler.Core.csproj"));

        Assert.DoesNotContain("ProjectReference", platformProject, StringComparison.Ordinal);
        Assert.Contains("HybridCPU.Platform.Contracts.csproj", kernelProject, StringComparison.Ordinal);
        Assert.DoesNotContain("HybridCPU_ISE", kernelProject + runtimeProject + coreProject, StringComparison.Ordinal);
        Assert.DoesNotContain("HybridCPU.Compiler", kernelProject + runtimeProject, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeKernel", coreProject, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedAbiV16_PreservesPriorHelpersAndAddsDispatchContract()
    {
        HybridCpuManagedAbiFamilyV1 family = HybridCpuManagedAbiFamilyV1.Default;
        HybridCpuRuntimeHelperV1 bootstrap = Assert.IsType<HybridCpuRuntimeHelperV1>(family.ResolveRuntimeHelper(BootstrapHelper));

        Assert.Equal(1, HybridCpuManagedAbiFamilyV1.SchemaMajor);
        Assert.Equal(63, HybridCpuManagedAbiFamilyV1.SchemaMinor);
        Assert.Equal(HybridCpuPlatformContractV1.ContractDigest, family.PlatformContractDigest);
        Assert.Equal("void(execution-context:nuint)", bootstrap.Signature);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported, bootstrap.Support);
        HybridCpuRuntimeHelperV1 allocation = Assert.IsType<HybridCpuRuntimeHelperV1>(
            family.ResolveRuntimeHelper("__hybridcpu_managed_alloc"));
        Assert.Equal("object-ref(type-handle:nuint)", allocation.Signature);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported, allocation.Support);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported,
            family.Features.Single(static feature => feature.Identity == "managed.tls").Support);
        Assert.Equal(HybridCpuNativeAbiContractV2.ThreadPointerRegister, family.ThreadPointerRegister);
    }

    [Fact]
    public void ManagedUnwindRecord_IsCanonicalRoundTripAndRejectsMalformedState()
    {
        HybridCpuManagedUnwindRecordV1 record = new HybridCpuManagedUnwindRecordV1(HybridCpuManagedFrameKindV1.RuntimeHelper,
            HybridCpuManagedCfaBaseV1.FramePointer, 32, 8,
            [new(18, 24), new(HybridCpuNativeAbiContractV2.ReturnAddressRegister, 16)]);

        byte[] first = HybridCpuManagedUnwindCodecV1.Encode(record);
        byte[] second = HybridCpuManagedUnwindCodecV1.Encode(record with { SavedRegisters = record.SavedRegisters.Reverse().ToArray() });
        HybridCpuManagedUnwindRecordV1 decoded = HybridCpuManagedUnwindCodecV1.Decode(first);

        Assert.Equal(first, second);
        Assert.Equal([HybridCpuNativeAbiContractV2.ReturnAddressRegister, 18],
            decoded.SavedRegisters.Select(static row => row.RegisterId));
        Assert.Throws<ArgumentException>(() => HybridCpuManagedUnwindCodecV1.Encode(
            record with { SavedRegisters = [new(18, 8), new(18, 16)] }));
        Assert.Throws<ArgumentException>(() => HybridCpuManagedUnwindCodecV1.Decode(first[..^1]));
    }

    [Fact]
    public void RestrictedManagedImage_DescriptorRoundTripsAndTamperingFailsClosed()
    {
        HybridCpuStaticLinkArtifactV1 link = LinkBootstrapImage();
        HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor = DescriptorFor(link);
        HybridCpuRestrictedImageBuilderV1 builder = new HybridCpuRestrictedImageBuilderV1();

        HybridCpuRestrictedImageV1 first = builder.Build(new(link, "__hybridcpu_runtime_entry", RuntimeBootstrap: descriptor));
        HybridCpuRestrictedImageV1 second = builder.Build(new(link, "__hybridcpu_runtime_entry", RuntimeBootstrap: descriptor));
        HybridCpuRestrictedImageV1 inspected = builder.Inspect(first.PackageBytes);

        Assert.Equal(HybridCpuStartupStatusV1.Success, first.Status);
        Assert.Equal(first.PackageBytes, second.PackageBytes);
        Assert.Equal(64, first.PackageSha256.Length);
        Assert.Equal(64, descriptor.DescriptorDigest.Length);
        HybridCpuImageRuntimeBootstrapDescriptorV1 roundTripped = Assert.IsType<HybridCpuImageRuntimeBootstrapDescriptorV1>(inspected.RuntimeBootstrap);
        Assert.Equal(descriptor.DescriptorDigest, roundTripped.DescriptorDigest);
        Assert.Equal(descriptor.RuntimeHelpers, roundTripped.RuntimeHelpers);
        Assert.Equal(descriptor.CodeManagerRecords, roundTripped.CodeManagerRecords);
        Assert.Equal(descriptor.StaticRoots, roundTripped.StaticRoots);
        Assert.Equal(descriptor.ModuleInitializers, roundTripped.ModuleInitializers);
        Assert.Equal(first.PackageSha256, inspected.PackageSha256);

        byte[] corrupt = first.PackageBytes.ToArray();
        corrupt[HybridCpuRestrictedStartupOptionsV1.Production.PackageHeaderBytes + 12] ^= 0x40;
        Assert.Equal(HybridCpuStartupStatusV1.CorruptInput, builder.Inspect(corrupt).Status);

        byte[] impossibleEntry = first.PackageBytes.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(impossibleEntry.AsSpan(40), ulong.MaxValue - 31);
        RecomputePackageChecksum(impossibleEntry);
        HybridCpuRestrictedImageV1 impossible = builder.Inspect(impossibleEntry);
        Assert.Equal(HybridCpuStartupStatusV1.CorruptInput, impossible.Status);
        Assert.Equal("HCSTART2012", Assert.Single(impossible.Diagnostics).Code);

        byte[] unknownSchema = first.PackageBytes.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(unknownSchema.AsSpan(10), 2);
        RecomputePackageChecksum(unknownSchema);
        Assert.Equal(HybridCpuStartupStatusV1.VersionSkew, builder.Inspect(unknownSchema).Status);

        HybridCpuImageRuntimeBootstrapDescriptorV1 badSignature = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, "__hybridcpu_runtime_entry", "managed_entry",
            [new(BootstrapHelper, "void(wrong)", true)], descriptor.CodeManagerRecords);
        HybridCpuRestrictedImageV1 rejected = builder.Build(new(link, "__hybridcpu_runtime_entry", RuntimeBootstrap: badSignature));
        Assert.Equal(HybridCpuStartupStatusV1.Unsupported, rejected.Status);
        Assert.Equal("HCSTART1012", Assert.Single(rejected.Diagnostics).Code);
        Assert.Throws<ArgumentException>(() => new HybridCpuManagedBootstrapDescriptorBuilderV1().Create(link,
            "__hybridcpu_runtime_entry", "managed_entry", [], ["__hybridcpu_managed_write_barrier"]));
    }

    [Fact]
    public void ManagedImage_BootstrapsKernelRuntimeAndExecutesRelocatedEntryHelperPathOnIse()
    {
        HybridCpuStaticLinkArtifactV1 link = LinkBootstrapImage();
        HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor = DescriptorFor(link);
        HybridCpuRestrictedImageBuilderV1 builder = new HybridCpuRestrictedImageBuilderV1();
        HybridCpuRestrictedImageV1 image = builder.Inspect(builder.Build(
            new(link, "__hybridcpu_runtime_entry", RuntimeBootstrap: descriptor)).PackageBytes);
        Assert.Equal(HybridCpuStartupStatusV1.Success, image.Status);

        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        ulong mappedImageSize = AlignUp((ulong)image.ImageBytes.Length, 4096);
        HybridCpuKernelBootResultV1 boot = kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest,
            image.OptionsDigest, image.ImageBase, mappedImageSize, image.EntryAddress,
            HybridCpuRestrictedStartupOptionsV1.Production.StackBase,
            HybridCpuRestrictedStartupOptionsV1.Production.StackSize, 0));
        HybridCpuExecutionContextDescriptorV1 context = Assert.IsType<HybridCpuExecutionContextDescriptorV1>(boot.Context);
        int helperCalls = 0;
        HybridCpuManagedBootstrapResultV1 bootstrap = Runtime().Bootstrap(
            Assert.IsType<HybridCpuImageRuntimeBootstrapDescriptorV1>(image.RuntimeBootstrap), kernel,
            new Dictionary<string, HybridCpuRuntimeHelperEntryV1>(StringComparer.Ordinal)
            {
                [BootstrapHelper] = supplied => { helperCalls++; return supplied == context; }
            }, initializers: new Dictionary<string, Func<bool>>(StringComparer.Ordinal)
            { ["__hybridcpu_static_init_a"] = static () => true });
        Assert.True(bootstrap.IsSuccess, bootstrap.Reason);
        Assert.Equal(1, helperCalls);
        Assert.Equal(["__hybridcpu_static_init_a"], bootstrap.RegisteredModuleInitializers);

        HybridCpuLinkedSymbolV1 helper = link.Symbols.Single(static row => row.Name == BootstrapHelper);
        HybridCpuLinkedSymbolV1 managed = link.Symbols.Single(static row => row.Name == "managed_entry");
        Processor.MainMemoryArea originalMemory = Processor.MainMemory;
        ProcessorMode originalMode = Processor.CurrentProcessorMode;
        MemorySubsystem? originalSubsystem = Processor.Memory;
        try
        {
            Processor.CurrentProcessorMode = ProcessorMode.Compiler;
            Processor.Memory = null;
            Processor.MainMemory = new HybridCpuIseSparseMainMemoryAreaV1();
            Processor.CPU_Core core = new Processor.CPU_Core(0,
                CpuCorePlatformContext.CreateFixed(Processor.MainMemory, ProcessorMode.Compiler));
            core.InitializePipeline();
            core.PrepareExecutionStart(image.EntryAddress);
            for (int register = 0; register < 32; register++) core.WriteCommittedArch(0, register, 0);
            core.WriteCommittedPc(0, image.EntryAddress);
            core.WriteCommittedArch(0, HybridCpuNativeAbiContractV2.StackPointerRegister, context.InitialStackPointer);
            core.WriteCommittedArch(0, HybridCpuNativeAbiContractV2.ThreadPointerRegister, context.ContextCarrierAddress);

            RetireBundle(core, ReadBundle(image, image.EntryAddress), image.EntryAddress);
            Assert.Equal(helper.Address, core.ReadCommittedPc(0));
            Assert.Equal(context.ContextCarrierAddress, core.ReadArch(0, HybridCpuNativeAbiContractV2.ThreadPointerRegister));
            RetireBundle(core, ReadBundle(image, helper.Address), helper.Address);
            Assert.Equal(managed.Address, core.ReadCommittedPc(0));
            RetireBundle(core, ReadBundle(image, managed.Address), managed.Address);
            Assert.Equal(41UL, core.ReadArch(0, HybridCpuNativeAbiContractV2.Default.ReturnRegisters[0]));
        }
        finally
        {
            Processor.MainMemory = originalMemory;
            Processor.CurrentProcessorMode = originalMode;
            Processor.Memory = originalSubsystem;
        }
    }

    private static HybridCpuBootInfoV1 BootInfo() => new(
        HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
        0x1000_0000, 0x20_0000, 0x1000_0100, 0x2000_0000, 0x100_0000, 0);

    private static HybridCpuImageRuntimeBootstrapDescriptorV1 Descriptor(bool reverse = false)
    {
        HybridCpuRuntimeHelperImportV1[] helpers = [new(BootstrapHelper, "void(execution-context:nuint)", true)];
        HybridCpuCodeManagerRegistrationV1[] methods = [new("method:a", 0, 32, new string('1', 64), new string('2', 64))];
        HybridCpuStaticRootRegistrationV1[] roots = [new("root:a", 0x1000_1000, 8)];
        HybridCpuModuleInitializerRegistrationV1[] initializers = [new("module:a", "__hybridcpu_static_init_a", 0)];
        return HybridCpuImageRuntimeBootstrapContractV1.Create(HybridCpuManagedAbiFamilyV1.Default.ContractDigest,
            "__hybridcpu_runtime_entry", "managed_entry", reverse ? helpers.Reverse() : helpers,
            reverse ? methods.Reverse() : methods, reverse ? roots.Reverse() : roots,
            reverse ? initializers.Reverse() : initializers);
    }

    private static HybridCpuStaticLinkArtifactV1 LinkBootstrapImage()
    {
        byte[] runtime = CallCode();
        byte[] helper = CallCode();
        byte[] managed = ValueCode(41);
        byte[] initializer = ValueCode(0);
        HybridCpuObjectWriterV1 writer = new HybridCpuObjectWriterV1();
        byte[] Object(byte[] code, IReadOnlyList<HybridCpuObjectSymbolV1> symbols,
            IReadOnlyList<HybridCpuObjectRelocationV1> relocations)
        {
            HybridCpuObjectArtifactV1 artifact = writer.Write(new(
                [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes, code, (ulong)code.Length)],
                symbols, relocations, HybridCpuTargetPlatformContractV1.Default.ContractDigest,
                HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
            Assert.Equal(HybridCpuObjectStatusV1.Success, artifact.Status);
            return artifact.Bytes;
        }
        HybridCpuObjectSymbolV1 Definition(string name, ulong size) => new(name, HybridCpuSymbolBinding.Global,
            HybridCpuSymbolVisibility.Default, ".text", 0, size, true);
        HybridCpuObjectSymbolV1 Declaration(string name) => new(name, HybridCpuSymbolBinding.Global,
            HybridCpuSymbolVisibility.Default, null, 0, 0, false);

        HybridCpuStaticLinkArtifactV1 link = new HybridCpuStaticLinkerV1().Link([
            new("a-runtime", Object(runtime, [Definition("__hybridcpu_runtime_entry", (ulong)runtime.Length), Declaration(BootstrapHelper)],
                [new(".text", 0, HybridCpuRelocationKind.ManagedCallRelativeSigned16, BootstrapHelper, 0)])),
            new("b-helper", Object(helper, [Definition(BootstrapHelper, (ulong)helper.Length), Declaration("managed_entry")],
                [new(".text", 0, HybridCpuRelocationKind.ManagedCallRelativeSigned16, "managed_entry", 0)])),
            new("c-managed", Object(managed, [Definition("managed_entry", (ulong)managed.Length)], [])),
            new("d-initializer", Object(initializer,
                [Definition("__hybridcpu_static_init_a", (ulong)initializer.Length)], []))]);
        Assert.Equal(HybridCpuLinkStatusV1.Success, link.Status);
        return link;
    }

    private static HybridCpuImageRuntimeBootstrapDescriptorV1 DescriptorFor(HybridCpuStaticLinkArtifactV1 link)
    {
        HybridCpuManagedUnwindRecordV1 unwind = new HybridCpuManagedUnwindRecordV1(HybridCpuManagedFrameKindV1.Managed,
            HybridCpuManagedCfaBaseV1.StackPointer, 0, 0, []);
        return new HybridCpuManagedBootstrapDescriptorBuilderV1().Create(link,
            "__hybridcpu_runtime_entry", "managed_entry",
            [new("managed_entry", [1, 2, 3, 4], unwind)], [BootstrapHelper],
            moduleInitializers: [new("module:a", "__hybridcpu_static_init_a", 0)]);
    }

    private static byte[] CallCode()
    {
        HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
        bundle.SetInstruction(0, new HybridCpuInstructionWord
        {
            OpCode = (uint)HybridCpuOpcode.JAL,
            PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(
                (byte)HybridCpuNativeAbiContractV2.ReturnAddressRegister,
                HybridCpuInstructionWord.NoArchReg, HybridCpuInstructionWord.NoArchReg)
        });
        return new HybridCpuBundleSerializer().SerializeProgram([bundle]);
    }

    private static byte[] ValueCode(ushort value)
    {
        HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
        bundle.SetInstruction(0, new HybridCpuInstructionWord
        {
            OpCode = (uint)HybridCpuOpcode.ADDI,
            DataTypeValue = HybridCpuDataType.INT32,
            PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(
                (byte)HybridCpuNativeAbiContractV2.Default.ReturnRegisters[0], 0, HybridCpuInstructionWord.NoArchReg),
            Immediate = value,
            VirtualThreadId = 0
        });
        return new HybridCpuBundleSerializer().SerializeProgram([bundle]);
    }

    private static VLIW_Instruction[] ReadBundle(HybridCpuRestrictedImageV1 image, ulong address)
    {
        int bundleIndex = checked((int)((address - image.ImageBase) / HybridCpuBundleSerializer.BundleSizeBytes));
        VLIW_Bundle bundle = new VLIW_Bundle();
        Assert.True(bundle.TryReadBytes(image.ImageBytes, bundleIndex * HybridCpuBundleSerializer.BundleSizeBytes));
        return Enumerable.Range(0, HybridCpuInstructionBundle.SlotCount).Select(bundle.GetInstruction).ToArray();
    }

    private static void RetireBundle(Processor.CPU_Core core, VLIW_Instruction[] bundle, ulong pc)
    {
        bool control = bundle.Any(static instruction => instruction.OpCode is
            >= (uint)Processor.CPU_Core.InstructionsEnum.JAL and <= (uint)Processor.CPU_Core.InstructionsEnum.BGEU);
        core.TestRunDecodeStageWithFetchedBundle(bundle, pc);
        core.TestRunExecuteStageFromCurrentDecodeState();
        core.TestRunMemoryAndWriteBackStagesFromCurrentExecuteState();
        if (!control) core.WriteCommittedPc(0, pc);
    }

    private static ulong AlignUp(ulong value, ulong alignment) => checked((value + alignment - 1) / alignment * alignment);

    private static void RecomputePackageChecksum(byte[] package)
    {
        package.AsSpan(288, 32).Clear();
        System.Security.Cryptography.SHA256.HashData(package).CopyTo(package, 288);
    }


    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Compilers", "HybridCPU_Compiler")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static HybridCpuManagedBootstrapRuntimeV1 Runtime() =>
        new(HybridCpuManagedAbiFamilyV1.Default.ContractDigest);
}
