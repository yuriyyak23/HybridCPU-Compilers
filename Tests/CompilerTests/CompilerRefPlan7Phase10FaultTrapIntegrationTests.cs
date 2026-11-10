using System.Buffers.Binary;
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
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core.Registers;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase10FaultTrapIntegrationTests
{
    [Fact]
    public void TrapAbi_BindsExactCommittedStateAndIsDeterministic()
    {
        HybridCpuArchitecturalTrapRecordV1 first = Trap(sequence: 7);
        HybridCpuArchitecturalTrapRecordV1 second = Trap(sequence: 7);
        HybridCpuArchitecturalTrapRecordV1 next = Trap(sequence: 8);
        Assert.Equal(first.CommittedState.StateDigest, second.CommittedState.StateDigest);
        Assert.Equal(first.RecordDigest, second.RecordDigest);
        Assert.NotEqual(first.RecordDigest, next.RecordDigest);
        Assert.True(HybridCpuTrapAbiV1.TryValidate(first, out string reason), reason);
        Assert.False(first.FaultingInstructionRetired);
        Assert.True(first.NoYoungerArchitecturalPublication);
        Assert.Equal(32, first.CommittedState.IntegerRegisters.Count);

        HybridCpuArchitecturalTrapRecordV1 corrupt = first with
        {
            CommittedState = first.CommittedState with { IntegerRegisters = Enumerable.Repeat(1UL, 32).ToArray() }
        };
        Assert.False(HybridCpuTrapAbiV1.TryValidate(corrupt, out _));
    }

    [Fact]
    public void RuntimeKernel_ClassifiesExactManagedCandidateAndEnforcesNestedLifoReturn()
    {
        DeterministicRuntimeKernelV1 kernel = Kernel();
        HybridCpuArchitecturalTrapRecordV1 outer = Trap(sequence: 1);
        HybridCpuArchitecturalTrapRecordV1 inner = Trap(sequence: 2);
        HybridCpuKernelTrapResultV1 first = kernel.TrapEntry(outer);
        HybridCpuKernelTrapResultV1 second = kernel.TrapEntry(inner);
        Assert.Equal(HybridCpuKernelTrapDispositionV1.ManagedPolicyEligible, first.Disposition);
        Assert.Equal(1, first.TrapDepth);
        Assert.Equal(2, second.TrapDepth);
        Assert.Equal(HybridCpuKernelStatusV1.TrapStateMismatch, kernel.TrapReturn(outer).Status);
        Assert.Equal(1, kernel.TrapReturn(inner).TrapDepth);
        Assert.Equal(0, kernel.TrapReturn(outer).TrapDepth);

        kernel = Kernel();
        HybridCpuArchitecturalTrapFrameV1 legacy = new HybridCpuArchitecturalTrapFrameV1(0, 0x1000_0200, 0x200f_fe00, 7, 0);
        Assert.True(kernel.TrapEntry(legacy).IsSuccess);
        Assert.True(kernel.TrapEntry(outer).IsSuccess);
        Assert.Equal(HybridCpuKernelStatusV1.TrapStateMismatch, kernel.TrapReturn(legacy).Status);
        Assert.True(kernel.TrapReturn(outer).IsSuccess);
        Assert.True(kernel.TrapReturn(legacy).IsSuccess);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void RuntimeKernel_RejectsNonPrecisePublicationFacts(bool retired, bool noYoungerPublication)
    {
        DeterministicRuntimeKernelV1 kernel = Kernel();
        HybridCpuArchitecturalTrapRecordV1 record = Trap(retired: retired,
            noYoungerPublication: noYoungerPublication);
        HybridCpuKernelTrapResultV1 result = kernel.TrapEntry(record);
        Assert.Equal(HybridCpuKernelStatusV1.InvalidRequest, result.Status);
        Assert.Equal(HybridCpuKernelTrapDispositionV1.Rejected, result.Disposition);
    }

    [Fact]
    public void ApprovedZeroAddressReadFault_MapsToExactNullReferenceAndManagedCatch()
    {
        DeterministicRuntimeKernelV1 kernel = Kernel();
        HybridCpuArchitecturalTrapRecordV1 record = Trap();
        HybridCpuKernelTrapResultV1 entry = kernel.TrapEntry(record);
        HybridCpuManagedTypeSystemV1 types = Types();
        ulong exceptionBase = Type(types, "System.Exception").TypeId;
        HybridCpuManagedExceptionRuntimeV1 eh = new HybridCpuManagedExceptionRuntimeV1(types,
            [Registration("catcher", 0x4000, exceptionBase)]);
        HybridCpuManagedTrapMapperV1 mapper = new HybridCpuManagedTrapMapperV1(types,
            HybridCpuManagedTrapMappingOptionsV1.Qualification);
        HybridCpuManagedTrapDispatchResultV1 result = mapper.MapAndDispatch(record, entry,
            static _ => 0x9000, eh, [new("catcher", 0x4010, 0x8000, 0)]);
        Assert.True(result.IsHandled);
        Assert.Equal("System.NullReferenceException", result.Mapping.ExceptionTypeIdentity);
        Assert.Equal("catcher", result.Dispatch!.HandlerMethodIdentity);
        Assert.False(result.Mapping.HasIseExecutionAuthority);
        Assert.False(result.Dispatch.UsedArchitecturalTrap);
        Assert.True(kernel.TrapReturn(record).IsSuccess);
    }

    [Fact]
    public void NonZeroAddressAndExecuteFaultsNeverBecomeManagedExceptions()
    {
        HybridCpuManagedTypeSystemV1 types = Types();
        HybridCpuManagedTrapMapperV1 mapper = new HybridCpuManagedTrapMapperV1(types,
            HybridCpuManagedTrapMappingOptionsV1.Qualification);
        DeterministicRuntimeKernelV1 kernel = Kernel();
        HybridCpuArchitecturalTrapRecordV1 nonzero = Trap(address: 0x1000);
        HybridCpuKernelTrapResultV1 admitted = kernel.TrapEntry(nonzero);
        HybridCpuManagedTrapMappingResultV1 mapping = mapper.Map(nonzero, admitted, static _ => 0x9000);
        Assert.Equal(HybridCpuManagedTrapMappingStatusV1.NotMappable, mapping.Status);
        Assert.True(kernel.TrapReturn(nonzero).IsSuccess);

        kernel = Kernel();
        HybridCpuArchitecturalTrapRecordV1 execute = Trap(access: HybridCpuMemoryAccessKindV1.Execute);
        HybridCpuKernelTrapResultV1 failed = kernel.TrapEntry(execute);
        Assert.Equal(HybridCpuKernelStatusV1.ProcessExited, failed.Status);
        Assert.Equal(HybridCpuKernelTrapDispositionV1.ProcessFailure, failed.Disposition);
        Assert.Equal(HybridCpuKernelStatusV1.NoCurrentContext,
            kernel.ReserveVm(new(0x3000_0000, 4096, HybridCpuVmProtectionV1.None)).Status);
    }

    [Fact]
    public void IllegalInstructionAndIntegrityTrapRemainKernelProcessFailures()
    {
        foreach (HybridCpuArchitecturalTrapClassV1 trapClass in new[]
                 { HybridCpuArchitecturalTrapClassV1.IllegalInstruction, HybridCpuArchitecturalTrapClassV1.IntegrityFailure })
        {
            DeterministicRuntimeKernelV1 kernel = Kernel();
            HybridCpuArchitecturalTrapRecordV1 record = Trap(trapClass: trapClass,
                access: HybridCpuMemoryAccessKindV1.None, hasAddress: false,
                semantic: HybridCpuFaultAddressSemanticV1.None,
                resume: HybridCpuTrapResumePolicyV1.ProcessFailure);
            HybridCpuKernelTrapResultV1 result = kernel.TrapEntry(record);
            Assert.Equal(HybridCpuKernelStatusV1.ProcessExited, result.Status);
            Assert.Equal(HybridCpuKernelTrapDispositionV1.ProcessFailure, result.Disposition);
            Assert.False(result.GrantsManagedExceptionIdentity);
        }
    }

    [Fact]
    public void MappingIsDefaultOffAndImplicitNullChecksCannotBeEnabled()
    {
        HybridCpuManagedTypeSystemV1 types = Types();
        DeterministicRuntimeKernelV1 kernel = Kernel();
        HybridCpuArchitecturalTrapRecordV1 record = Trap();
        HybridCpuKernelTrapResultV1 entry = kernel.TrapEntry(record);
        HybridCpuManagedTrapMappingResultV1 disabled = new HybridCpuManagedTrapMapperV1(types)
            .Map(record, entry, static _ => 0x9000);
        Assert.Equal(HybridCpuManagedTrapMappingStatusV1.Disabled, disabled.Status);
        Assert.False(HybridCpuManagedTrapMappingOptionsV1.Qualification.ImplicitNullChecksEnabled);
        Assert.Throws<ArgumentException>(() => HybridCpuManagedTrapMappingOptionsV1.Create(true, true, true));
    }

    [Fact]
    public void TrapIntegrationPreservesLayerAuthorityAndHasNoIseProductionDependency()
    {
        string[] forbidden = ["HybridCPU_ISE", "YAKSys_Hybrid_CPU"];
        foreach (System.Reflection.Assembly assembly in new[]
                 { typeof(HybridCpuTrapAbiV1).Assembly, typeof(DeterministicRuntimeKernelV1).Assembly,
                     typeof(HybridCpuManagedTrapMapperV1).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                forbidden.Any(name => reference.Name?.Contains(name, StringComparison.Ordinal) == true));
        Assert.False(new HybridCpuKernelTrapResultV1(HybridCpuKernelStatusV1.Success,
            HybridCpuKernelTrapDispositionV1.ManagedPolicyEligible, 0, new string('a', 64), string.Empty)
            .HasIseExecutionAuthority);
    }

    [Fact]
    public void RealImageLoadFault_ExposesPreciseIseFactsAndMapsThroughKernelToManagedCatch()
    {
        HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
        bundle.SetInstruction(0, new HybridCpuInstructionWord
        {
            OpCode = (uint)HybridCpuOpcode.LD,
            DataTypeValue = HybridCpuDataType.INT64,
            PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(10, 0, HybridCpuInstructionWord.NoArchReg),
            Immediate = 0,
            VirtualThreadId = 0
        });
        byte[] code = new HybridCpuBundleSerializer().SerializeProgram([bundle]);
        HybridCpuObjectArtifactV1 obj = new HybridCpuObjectWriterV1().Write(new(
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes,
                code, (ulong)code.Length)],
            [new("fault_entry", HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default,
                ".text", 0, (ulong)code.Length, true)], [],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
        Assert.Equal(HybridCpuObjectStatusV1.Success, obj.Status);
        HybridCpuStaticLinkArtifactV1 link = new HybridCpuStaticLinkerV1().Link([new("fault", obj.Bytes)]);
        Assert.Equal(HybridCpuLinkStatusV1.Success, link.Status);
        HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, "fault_entry", "fault_entry", [], []);
        HybridCpuRestrictedImageV1 built = new HybridCpuRestrictedImageBuilderV1().Build(
            new(link, "fault_entry", RuntimeBootstrap: descriptor));
        HybridCpuRestrictedImageV1 image = new HybridCpuRestrictedImageBuilderV1().Inspect(built.PackageBytes);
        Assert.Equal(HybridCpuStartupStatusV1.Success, image.Status);

        DeterministicRuntimeKernelV1 kernel = Kernel();
        Assert.True(new HybridCpuManagedBootstrapRuntimeV1(HybridCpuManagedAbiFamilyV1.Default.ContractDigest)
            .Bootstrap(image.RuntimeBootstrap!, kernel, new Dictionary<string, HybridCpuRuntimeHelperEntryV1>())
            .IsSuccess);

        Processor.MainMemoryArea originalMemory = Processor.MainMemory;
        ProcessorMode originalMode = Processor.CurrentProcessorMode;
        MemorySubsystem? originalSubsystem = Processor.Memory;
        const ulong handlerPc = 0x1800;
        try
        {
            Processor.CurrentProcessorMode = ProcessorMode.Compiler;
            Processor.Memory = null;
            Processor.MainMemory = new RejectingMainMemory();
            Processor.CPU_Core core = new Processor.CPU_Core(0,
                CpuCorePlatformContext.CreateFixed(Processor.MainMemory, ProcessorMode.Compiler));
            core.InitializePipeline();
            core.PrepareExecutionStart(image.EntryAddress);
            core.WriteCommittedPc(0, image.EntryAddress);
            core.WriteCommittedArch(0, 2, 0x200f_ff00);
            core.WriteCommittedArch(0, 10, 0xfeed_face);
            core.Csr.Write(CsrAddresses.Mtvec, handlerPc, PrivilegeLevel.Machine);
            ulong retiredBefore = core.GetPipelineControl().InstructionsRetired;
            VLIW_Bundle nativeBundle = new VLIW_Bundle();
            Assert.True(nativeBundle.TryReadBytes(code, 0));
            VLIW_Instruction[] slots = Enumerable.Range(0, HybridCpuInstructionBundle.SlotCount)
                .Select(nativeBundle.GetInstruction).ToArray();
            core.TestRunDecodeStageWithFetchedBundle(slots, image.EntryAddress);
            core.TestRunExecuteStageFromCurrentDecodeState();
            YAKSys_Hybrid_CPU.Core.PageFaultException fault = Assert.Throws<YAKSys_Hybrid_CPU.Core.PageFaultException>(
                core.TestRunMemoryAndWriteBackStagesFromCurrentExecuteState);

            Assert.Equal(0UL, fault.FaultAddress);
            Assert.False(fault.IsWrite);
            Assert.Equal(retiredBefore, core.GetPipelineControl().InstructionsRetired);
            Assert.Equal(0xfeed_faceUL, core.ReadArch(0, 10));
            ulong[] registers = Enumerable.Range(0, 32).Select(index => core.ReadArch(0, index)).ToArray();
            HybridCpuCommittedRegisterStateV1 state = HybridCpuTrapAbiV1.CreateCommittedState(
                image.EntryAddress, core.ReadArch(0, 2), registers);
            HybridCpuArchitecturalTrapRecordV1 record = HybridCpuTrapAbiV1.CreateRecord(1, 1,
                new(0, image.EntryAddress, core.ReadArch(0, 2), 13, fault.FaultAddress),
                HybridCpuArchitecturalTrapClassV1.SynchronousMemoryFault, 0,
                HybridCpuMemoryAccessKindV1.Read, HybridCpuPrivilegeModeV1.User,
                HybridCpuExecutionModeV1.ManagedUser, true, HybridCpuFaultAddressSemanticV1.VirtualAddress,
                state, false, true, HybridCpuTrapResumePolicyV1.ManagedDispatch);
            HybridCpuKernelTrapResultV1 entry = kernel.TrapEntry(record);
            HybridCpuManagedTypeSystemV1 types = Types();
            ulong exceptionBase = Type(types, "System.Exception").TypeId;
            HybridCpuManagedExceptionRuntimeV1 runtime = new HybridCpuManagedExceptionRuntimeV1(types,
                [Registration("catcher", 0x4000, exceptionBase)]);
            HybridCpuManagedTrapDispatchResultV1 mapped = new HybridCpuManagedTrapMapperV1(types,
                HybridCpuManagedTrapMappingOptionsV1.Qualification).MapAndDispatch(record, entry,
                static _ => 0x9000, runtime, [new("catcher", 0x4010, 0x8000, 0)]);
            Assert.True(mapped.IsHandled);
            Assert.True(kernel.TrapReturn(record).IsSuccess);
        }
        finally
        {
            Processor.MainMemory = originalMemory;
            Processor.CurrentProcessorMode = originalMode;
            Processor.Memory = originalSubsystem;
        }
    }

    private static DeterministicRuntimeKernelV1 Kernel()
    {
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x1000_0000, 0x20_0000, 0x1000_0000, 0x2000_0000, 0x10_0000, 0)).IsSuccess);
        return kernel;
    }

    private static HybridCpuArchitecturalTrapRecordV1 Trap(ulong sequence = 1, ulong address = 0,
        HybridCpuArchitecturalTrapClassV1 trapClass = HybridCpuArchitecturalTrapClassV1.SynchronousMemoryFault,
        HybridCpuMemoryAccessKindV1 access = HybridCpuMemoryAccessKindV1.Read,
        bool hasAddress = true,
        HybridCpuFaultAddressSemanticV1 semantic = HybridCpuFaultAddressSemanticV1.VirtualAddress,
        bool retired = false, bool noYoungerPublication = true,
        HybridCpuTrapResumePolicyV1 resume = HybridCpuTrapResumePolicyV1.ManagedDispatch)
    {
        const ulong pc = 0x1000_0100;
        const ulong sp = 0x200f_ff00;
        HybridCpuCommittedRegisterStateV1 state = HybridCpuTrapAbiV1.CreateCommittedState(pc, sp,
            Enumerable.Range(0, 32).Select(static value => (ulong)value));
        return HybridCpuTrapAbiV1.CreateRecord(1, sequence,
            new(0, pc, sp, trapClass == HybridCpuArchitecturalTrapClassV1.SynchronousMemoryFault ? 13UL : 2UL,
                hasAddress ? address : 0), trapClass, subcause: 1, access,
            HybridCpuPrivilegeModeV1.User, HybridCpuExecutionModeV1.ManagedUser,
            hasAddress, semantic, state, retired, noYoungerPublication, resume);
    }

    private static HybridCpuManagedTypeSystemV1 Types()
    {
        HybridCpuManagedTypeSystemBuildV1 build = new HybridCpuManagedTypeSystemBuilderV1().Build([
            new("System.Exception", HybridCpuManagedTypeKindV1.Class, null, [], []),
            new("System.NullReferenceException", HybridCpuManagedTypeKindV1.Class, "System.Exception", [], [])
        ]);
        Assert.True(build.IsSuccess, build.Reason);
        return build.TypeSystem!;
    }

    private static HybridCpuManagedTypeDescriptorV1 Type(HybridCpuManagedTypeSystemV1 types, string identity) =>
        Assert.Single(types.Descriptors, row => row.StableIdentity == identity);

    private static HybridCpuManagedEhMethodRegistrationV1 Registration(string identity, int start, ulong catchType)
    {
        byte[] eh = new byte[52];
        BinaryPrimitives.WriteUInt32LittleEndian(eh, HybridCpuManagedEhSchemaV1.EhMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(eh.AsSpan(4), HybridCpuManagedEhSchemaV1.SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(eh.AsSpan(8), 1);
        eh[12] = (byte)HybridCpuManagedEhClauseKindV1.Catch;
        BinaryPrimitives.WriteInt32LittleEndian(eh.AsSpan(16), 0);
        BinaryPrimitives.WriteInt32LittleEndian(eh.AsSpan(20), 0x100);
        BinaryPrimitives.WriteInt32LittleEndian(eh.AsSpan(24), 0x100);
        BinaryPrimitives.WriteInt32LittleEndian(eh.AsSpan(28), 0x100);
        BinaryPrimitives.WriteUInt64LittleEndian(eh.AsSpan(32), catchType);
        BinaryPrimitives.WriteInt32LittleEndian(eh.AsSpan(40), 0);
        byte[] unwind = HybridCpuManagedUnwindCodecV2.Encode(new(HybridCpuManagedFrameKindV1.Managed,
            HybridCpuManagedCfaBaseV1.StackPointer, 0, HybridCpuNativeAbiContractV2.ReturnAddressRegister,
            null, []));
        return new(identity, start, 0x200, eh, unwind);
    }

    private sealed class RejectingMainMemory : Processor.MainMemoryArea
    {
        public override long Length => 0x3000_0000;
        public override bool TryReadPhysicalRange(ulong physicalAddress, Span<byte> buffer) =>
            throw new YAKSys_Hybrid_CPU.Core.PageFaultException(physicalAddress, isWrite: false);
        public override bool TryWritePhysicalRange(ulong physicalAddress, ReadOnlySpan<byte> buffer) =>
            throw new YAKSys_Hybrid_CPU.Core.PageFaultException(physicalAddress, isWrite: true);
    }
}
