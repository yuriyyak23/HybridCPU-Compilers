using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.RefPlan7.Phase00.Corpus;
using HybridCPU_ISE.Arch;
using System.Security.Cryptography;
using YAKSys_Hybrid_CPU;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase00InstanceCarrierTests
{
    private static readonly byte[] FixtureImage = File.ReadAllBytes(typeof(InstanceCarrierCorpus).Assembly.Location);
    private static readonly string FixtureType = typeof(InstanceCarrierCorpus).FullName!;
    private static readonly HybridCpuMiiResourceModelV1 ResourceModel =
        HybridCpuMiiResourceModelV1.Create(new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8);

    [Fact]
    public void InstanceBodyAndDirectCallee_UseHiddenReceiverInOrdinaryAbi()
    {
        ManagedCallGraphCompilationV1 graph = Import(nameof(InstanceCarrierCorpus.DirectNestedCall));

        Assert.Equal(RestrictedCilImportStatusV1.Success, graph.Status);
        Assert.True(graph.Methods.Count >= 2);
        Assert.All(graph.Methods, static method => Assert.StartsWith("instance(", method.Identity.CanonicalSignature));
        Assert.Contains(graph.Methods, static method =>
            method.Identity.MethodName == "AddOne");
        ManagedCompiledMethodV1 root = graph.Methods.Single(static method =>
            method.Identity.MethodName == nameof(InstanceCarrierCorpus.DirectNestedCall));
        IrProgram program = root.Import.Program!;
        IrVirtualValueV1 receiver = program.ValueFlow.Values.Single(static value => value.StableId.EndsWith(":arg:0:abi", StringComparison.Ordinal));
        IrVirtualValueV1 explicitArgument = program.ValueFlow.Values.Single(static value => value.StableId.EndsWith(":arg:1:abi", StringComparison.Ordinal));
        Assert.Equal(HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[0], receiver.Allocation.FixedRegisterId);
        Assert.Equal(HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[1], explicitArgument.Allocation.FixedRegisterId);
        IrInstruction call = Assert.Single(program.Instructions,
            static instruction => instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call);
        IrOperand[] callValues = call.Annotation.Uses.Where(static operand => operand.Kind == IrOperandKind.VirtualValue).ToArray();
        Assert.Equal(3, callValues.Length);
        IrVirtualValueV1[] callAbiValues = callValues
            .Select(operand => program.ValueFlow.Values.Single(value => value.StableId == operand.Name))
            .ToArray();
        Assert.Equal([5, HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[0],
            HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[1]],
            callAbiValues.Select(static value => value.Allocation.FixedRegisterId));

        IrRegisterAllocationResultV1 allocation = Allocate(root);
        Assert.Equal(IrRegisterAllocationStatusV1.Allocated, allocation.Status);
        Assert.Contains(allocation.FinalSchedule.Program.Instructions, static instruction =>
            instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call);
    }

    [Fact]
    public void HiddenReceiverAndFourExplicitArgumentsFitCurrentRegisterArgumentBudget()
    {
        ManagedCallGraphCompilationV1 graph = Import(nameof(InstanceCarrierCorpus.FourExplicitArguments));

        Assert.Equal(RestrictedCilImportStatusV1.Success, graph.Status);
        Assert.NotNull(graph.Graph);
        Assert.Empty(graph.Diagnostics);
    }

    [Fact]
    public void InstanceImportAndFinalAllocation_AreDeterministic()
    {
        ManagedCallGraphCompilationV1 first = Import(nameof(InstanceCarrierCorpus.DirectNestedCall));
        ManagedCallGraphCompilationV1 second = Import(nameof(InstanceCarrierCorpus.DirectNestedCall));
        Assert.Equal(first.Graph!.GraphDigest, second.Graph!.GraphDigest);

        byte[] firstBytes = SerializeFinalBundles(first);
        byte[] secondBytes = SerializeFinalBundles(second);
        Assert.Equal(SHA256.HashData(firstBytes), SHA256.HashData(secondBytes));
    }

    [Fact]
    public void InstanceCarrier_AndLaterVirtualDispatchHaveDistinctQualifiedEvidence()
    {
        RestrictedCilSupportMatrixV1 matrix = RestrictedCilSupportMatrixV1.Default;
        Assert.Contains(matrix.Features, static feature =>
            feature.Feature == "instance-receiver-carrier" && feature.Support == RestrictedCilMatrixSupportV1.Supported);
        Assert.Contains(matrix.Features, static feature =>
            feature.Feature == "managed-references" && feature.Support == RestrictedCilMatrixSupportV1.Supported);
        Assert.Contains(ScalarControlFlowV2ProfileContractV1.Default.Features, static feature =>
            feature.Feature == "virtual-interface-calls" &&
            feature.Disposition == ScalarControlFlowV2Disposition.Guaranteed &&
            feature.Boundary.Contains("Phase 06", StringComparison.Ordinal));
    }

    [Fact]
    public void ThreadPointerCarrier_IsVtIsolatedAcrossActiveVtAndTrapFlushTransitions()
    {
        Processor.MultiBankMemoryArea memory = new Processor.MultiBankMemoryArea(4, 0x10000UL);
        Processor.CPU_Core core = new Processor.CPU_Core(0, CpuCorePlatformContext.CreateFixed(memory, ProcessorMode.Compiler));
        core.InitializePipeline();
        core.PrepareExecutionStart(0, activeVtId: 0);
        const ulong vt0ThreadPointer = 0x1111_0000_0000_0004UL;
        const ulong vt3ThreadPointer = 0x3333_0000_0000_0004UL;

        core.WriteCommittedArch(0, HybridCpuNativeAbiContractV2.ThreadPointerRegister, vt0ThreadPointer);
        core.WriteCommittedArch(3, HybridCpuNativeAbiContractV2.ThreadPointerRegister, vt3ThreadPointer);
        core.ActiveVirtualThreadId = 3;
        core.FlushPipeline(YAKSys_Hybrid_CPU.Core.AssistInvalidationReason.Trap);

        Assert.Equal(vt0ThreadPointer,
            core.ReadArch(0, HybridCpuNativeAbiContractV2.ThreadPointerRegister));
        Assert.Equal(vt3ThreadPointer,
            core.ReadArch(3, HybridCpuNativeAbiContractV2.ThreadPointerRegister));
        Assert.Equal(vt3ThreadPointer,
            core.ReadArch(core.ReadActiveVirtualThreadId(), HybridCpuNativeAbiContractV2.ThreadPointerRegister));
        Assert.Equal(vt0ThreadPointer, unchecked((ulong)core.CreateLiveCpuStateAdapter(0)
            .ReadRegister(0, HybridCpuNativeAbiContractV2.ThreadPointerRegister)));
        Assert.Equal(vt3ThreadPointer, unchecked((ulong)core.CreateLiveCpuStateAdapter(3)
            .ReadRegister(3, HybridCpuNativeAbiContractV2.ThreadPointerRegister)));
    }

    [Fact]
    public void ArchitecturalRead_UsesPrfOnlyForAStillSpeculativeRename()
    {
        Processor.MultiBankMemoryArea memory = new Processor.MultiBankMemoryArea(4, 0x10000UL);
        Processor.CPU_Core core = new Processor.CPU_Core(0, CpuCorePlatformContext.CreateFixed(memory, ProcessorMode.Compiler));
        const int vtId = 0;
        int register = HybridCpuNativeAbiContractV2.ThreadPointerRegister;
        const int speculativePhysicalRegister = 90;
        const ulong committedValue = 0x1111UL;
        const ulong speculativeValue = 0x2222UL;

        core.WriteCommittedArch(vtId, register, committedValue);
        core.ArchRenameMap.Remap(vtId, register, speculativePhysicalRegister);
        core.PhysicalRegisters.Write(speculativePhysicalRegister, speculativeValue);

        Assert.Equal(speculativeValue, core.ReadArch(vtId, register));

        core.ArchCommitMap.RestoreInto(core.ArchRenameMap);
        Assert.Equal(committedValue, core.ReadArch(vtId, register));
    }

    private static ManagedCallGraphCompilationV1 Import(string root) =>
        new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, FixtureImage, "refplan7-phase00-instance-corpus.dll",
            [new(FixtureType, root)], []));

    private static IrRegisterAllocationResultV1 Allocate(ManagedCompiledMethodV1 method)
    {
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(method.Import.Program!);
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        return new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(schedule, bundles,
            resourceModel: ResourceModel, options: HybridCpuRegisterAllocationOptionsV1.Qualification);
    }

    private static byte[] SerializeFinalBundles(ManagedCallGraphCompilationV1 graph)
    {
        List<byte> bytes = new List<byte>();
        foreach (ManagedCompiledMethodV1 method in graph.Methods.OrderBy(static method => method.Identity.StableIdentity, StringComparer.Ordinal))
        {
            IrRegisterAllocationResultV1 allocation = Allocate(method);
            Assert.Equal(IrRegisterAllocationStatusV1.Allocated, allocation.Status);
            IReadOnlyList<HybridCpuInstructionBundle> lowered = HybridCpuControlFlowRelocationResolver.ApplyRelocations(
                allocation.FinalBundles, new HybridCpuBundleLowerer().LowerProgram(allocation.FinalBundles));
            bytes.AddRange(new HybridCpuBundleSerializer().SerializeProgram(lowered));
        }
        return bytes.ToArray();
    }
}
