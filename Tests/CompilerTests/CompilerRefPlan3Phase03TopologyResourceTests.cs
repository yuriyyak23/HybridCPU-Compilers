using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCpuCanonicalCompiler = HybridCPU.Compiler.Core.Runtime.NativeTransportRuntimeAdapter;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;
using SlotClass = HybridCPU.Compiler.Core.IR.IrSlotClass;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan3Phase03TopologyResourceTests
{
    [Fact]
    public void DefaultTopologyBindsStableLayoutAndLeavesLiveGeometryUnknown()
    {
        HybridCpuMachineTopologyV1 first = HybridCpuMachineTopologyV1.Default;
        HybridCpuMachineTopologyV1 second = new HybridCpuMachineTopologyV1();

        Assert.Equal(4, HybridCpuMachineTopologyV1.VirtualThreadCount);
        Assert.Equal(16, HybridCpuMachineTopologyV1.RegisterGroupCount);
        Assert.Equal(4, HybridCpuMachineTopologyV1.RegistersPerGroup);
        Assert.Null(first.PrfReadPortCapacity);
        Assert.Null(first.PrfWritePortCapacity);
        Assert.Null(first.MemoryBankCount);
        Assert.Null(first.MemoryChannelCount);
        Assert.Equal(first.ContractDigest, second.ContractDigest);
        Assert.Equal(64, first.ContractDigest.Length);
        Assert.Equal(new HybridCpuTopologyFeatureFlagsV1(), HybridCpuTopologyFeatureFlagsV1.DefaultOff);
    }

    [Fact]
    public void RegisterMasksMatchRuntimeCertificateLayout()
    {
        IrInstruction instruction = WithAccess(CreateInstruction(0), reads: [0, 4, 63], writes: [7, 8]);
        IrTopologyResourceFootprintV1 footprint = Build(instruction);
        uint runtimeMask = MicroOpAdmissionMetadata.BuildRegisterHazardMask([0, 4, 63], [7, 8]);

        Assert.Equal((ushort)(runtimeMask & 0xFFFF), footprint.RegisterReadGroupMask);
        Assert.Equal((ushort)(runtimeMask >> 16), footprint.RegisterWriteGroupMask);
        Assert.Equal(3, footprint.RequiredPrfReadPorts);
        Assert.Equal(2, footprint.RequiredPrfWritePorts);
        Assert.Equal(IrResourceFactPrecisionV1.Exact, footprint.RegisterPrecision);
        Assert.NotNull(HybridCpuMachineResourceModelV1.Default.GetResourceFootprint(instruction).Topology);
    }

    [Fact]
    public void RarIsAllowedAndVirtualThreadMasksRemainSeparate()
    {
        HybridCpuTopologyResourceModelV1 model = new();
        IrTopologyResourceFootprintV1 first = model.GetResourceFootprint(
            WithAccess(CreateInstruction(0, virtualThreadId: 0), reads: [4], writes: []));
        IrTopologyResourceFootprintV1 sameVtRead = model.GetResourceFootprint(
            WithAccess(CreateInstruction(1, virtualThreadId: 0), reads: [7], writes: []));
        IrTopologyResourceFootprintV1 otherVtWrite = model.GetResourceFootprint(
            WithAccess(CreateInstruction(2, virtualThreadId: 1), reads: [], writes: [5]));
        HybridCpuTopologyCycleStateV1 state = model.Reserve(
            HybridCpuTopologyCycleStateV1.Empty(model.Topology), first);

        Assert.Equal(CompilerTopologyShadowDecisionV1.Allowed, model.CanReserve(state, sameVtRead).Decision);
        Assert.Equal(CompilerTopologyShadowDecisionV1.Allowed, model.CanReserve(state, otherVtWrite).Decision);
        Assert.Equal(first.RegisterReadGroupMask, state.RegisterReadGroupMasks[0]);
        Assert.Equal(0, state.RegisterReadGroupMasks[1]);
        Assert.Equal(first.RequiredPrfReadPorts, state.PrfReadPortsUsed);
    }

    [Fact]
    public void GeneratedRegisterDifferentialHasZeroKnownFactFalseNegatives()
    {
        HybridCpuTopologyResourceModelV1 model = new();
        int knownConflicts = 0;
        int falseNegatives = 0;
        int nextIndex = 0;
        for (byte firstRegister = 0; firstRegister < 32; firstRegister += 3)
            for (byte secondRegister = 0; secondRegister < 32; secondRegister += 5)
                foreach ((bool firstWrite, bool secondWrite) in new[] { (false, false), (false, true), (true, false), (true, true) })
                {
                    IrInstruction firstInstruction = WithAccess(CreateInstruction(nextIndex++),
                        firstWrite ? [] : [firstRegister], firstWrite ? [firstRegister] : []);
                    IrInstruction secondInstruction = WithAccess(CreateInstruction(nextIndex++),
                        secondWrite ? [] : [secondRegister], secondWrite ? [secondRegister] : []);
                    IrTopologyResourceFootprintV1 first = model.GetResourceFootprint(firstInstruction);
                    IrTopologyResourceFootprintV1 second = model.GetResourceFootprint(secondInstruction);
                    uint firstRuntime = MicroOpAdmissionMetadata.BuildRegisterHazardMask(
                        firstWrite ? [] : [firstRegister], firstWrite ? [firstRegister] : []);
                    uint secondRuntime = MicroOpAdmissionMetadata.BuildRegisterHazardMask(
                        secondWrite ? [] : [secondRegister], secondWrite ? [secondRegister] : []);
                    ushort firstRead = (ushort)firstRuntime;
                    ushort firstWriteMask = (ushort)(firstRuntime >> 16);
                    ushort secondRead = (ushort)secondRuntime;
                    ushort secondWriteMask = (ushort)(secondRuntime >> 16);
                    bool runtimeConflict = ((secondRead & firstWriteMask) |
                                            (secondWriteMask & firstRead) |
                                            (secondWriteMask & firstWriteMask)) != 0;
                    HybridCpuTopologyCycleStateV1 state = model.Reserve(
                        HybridCpuTopologyCycleStateV1.Empty(model.Topology), first);
                    bool compilerConflict = model.CanReserve(state, second).Decision ==
                                            CompilerTopologyShadowDecisionV1.PredictedConflict;
                    if (runtimeConflict)
                    {
                        knownConflicts++;
                        if (!compilerConflict) falseNegatives++;
                    }
                }

        Assert.True(knownConflicts > 0);
        Assert.Equal(0, falseNegatives);
    }

    [Theory]
    [InlineData(true, false, false, true, HybridCpuTopologyReasonCodeV1.RegisterRawConflict)]
    [InlineData(false, true, true, false, HybridCpuTopologyReasonCodeV1.RegisterWarConflict)]
    [InlineData(true, false, true, false, HybridCpuTopologyReasonCodeV1.RegisterWawConflict)]
    public void KnownRegisterConflictsAreClassified(
        bool firstWrites, bool firstReads, bool secondWrites, bool secondReads,
        HybridCpuTopologyReasonCodeV1 expected)
    {
        HybridCpuTopologyResourceModelV1 model = new();
        IrInstruction firstInstruction = WithAccess(CreateInstruction(0),
            firstReads ? [4] : [], firstWrites ? [4] : []);
        IrInstruction secondInstruction = WithAccess(CreateInstruction(1),
            secondReads ? [7] : [], secondWrites ? [7] : []);
        HybridCpuTopologyCycleStateV1 state = model.Reserve(
            HybridCpuTopologyCycleStateV1.Empty(model.Topology), model.GetResourceFootprint(firstInstruction));

        HybridCpuTopologyReservationResultV1 result = model.CanReserve(state, model.GetResourceFootprint(secondInstruction));

        Assert.Equal(CompilerTopologyShadowDecisionV1.PredictedConflict, result.Decision);
        Assert.Equal(expected, result.Reason);
    }

    [Fact]
    public void KnownPortCapacityIsConservedButDefaultCapacityRemainsUnknown()
    {
        HybridCpuMachineTopologyV1 topology = new HybridCpuMachineTopologyV1(prfReadPortCapacity: 2, prfWritePortCapacity: 1);
        HybridCpuTopologyResourceModelV1 model = new HybridCpuTopologyResourceModelV1(topology);
        IrTopologyResourceFootprintV1 first = model.GetResourceFootprint(
            WithAccess(CreateInstruction(0), reads: [0, 4], writes: []));
        IrTopologyResourceFootprintV1 second = model.GetResourceFootprint(
            WithAccess(CreateInstruction(1), reads: [8], writes: []));
        HybridCpuTopologyCycleStateV1 state = model.Reserve(HybridCpuTopologyCycleStateV1.Empty(topology), first);

        HybridCpuTopologyReservationResultV1 result = model.CanReserve(state, second);

        Assert.Equal(HybridCpuTopologyReasonCodeV1.PrfReadPortsExceeded, result.Reason);
        Assert.Equal(2, result.Used);
        Assert.Equal(2, result.Capacity);
    }

    [Fact]
    public void AddressEvidenceImplementsExactFiniteAllAndUnknownLattice()
    {
        HybridCpuMachineTopologyV1 known = new HybridCpuMachineTopologyV1(
            memoryBankCount: 16, memoryBankWidthBytes: 4096,
            memoryChannelCount: 4, memoryChannelWidthBytes: 16384);
        IrTopologyResourceFootprintV1 exact = Build(
            WithMemory(CreateInstruction(0), 4096, 4), known);
        IrTopologyResourceFootprintV1 finite = Build(
            WithMemory(CreateInstruction(1), 4095, 2), known);
        IrTopologyResourceFootprintV1 all = Build(
            WithMemory(CreateInstruction(2), 0, 4096 * 9), known);
        IrTopologyResourceFootprintV1 unknown = Build(
            WithMemory(CreateInstruction(3), 4096, 4));

        Assert.Equal(IrAddressEvidenceKindV1.Exact, exact.Banks.Precision);
        Assert.Equal([1], exact.Banks.ResourceIds);
        Assert.Equal(IrAddressEvidenceKindV1.FiniteSet, finite.Banks.Precision);
        Assert.Equal([0, 1], finite.Banks.ResourceIds);
        Assert.Equal(IrAddressEvidenceKindV1.All, all.Banks.Precision);
        Assert.Equal(IrAddressEvidenceKindV1.Unknown, unknown.Banks.Precision);
    }

    [Fact]
    public void OnlySameExactBankIsHardConflictAndFiniteUnknownRemainCostEvidence()
    {
        HybridCpuMachineTopologyV1 topology = new HybridCpuMachineTopologyV1(memoryBankCount: 16, memoryBankWidthBytes: 4096);
        HybridCpuTopologyResourceModelV1 model = new HybridCpuTopologyResourceModelV1(topology);
        IrTopologyResourceFootprintV1 first = model.GetResourceFootprint(
            WithMemory(WithAccess(CreateInstruction(0), [], []), 4096, 4));
        HybridCpuTopologyCycleStateV1 state = model.Reserve(HybridCpuTopologyCycleStateV1.Empty(topology), first);
        IrTopologyResourceFootprintV1 same = model.GetResourceFootprint(
            WithMemory(WithAccess(CreateInstruction(1), [], []), 4100, 4));
        IrTopologyResourceFootprintV1 distinct = model.GetResourceFootprint(
            WithMemory(WithAccess(CreateInstruction(2), [], []), 8192, 4));
        IrTopologyResourceFootprintV1 finite = model.GetResourceFootprint(
            WithMemory(WithAccess(CreateInstruction(3), [], []), 8191, 2));

        Assert.Equal(HybridCpuTopologyReasonCodeV1.ExactBankConflict, model.CanReserve(state, same).Reason);
        Assert.Equal(CompilerTopologyShadowDecisionV1.Allowed, model.CanReserve(state, distinct).Decision);
        Assert.Equal(CompilerTopologyShadowDecisionV1.Allowed, model.CanReserve(state, finite).Decision);
        Assert.True(model.IncrementalCost(state, finite).PossibleBankPressure > 0);
    }

    [Fact]
    public void CertificateLaneClassesCollideWithoutCreatingRuntimeCertificate()
    {
        HybridCpuTopologyResourceModelV1 model = new();
        IrTopologyResourceFootprintV1 dma = model.GetResourceFootprint(
            WithSlotClass(WithAccess(CreateInstruction(0), [], []), SlotClass.DmaStreamClass));
        IrTopologyResourceFootprintV1 matrix = model.GetResourceFootprint(
            WithSlotClass(WithAccess(CreateInstruction(1), [], []), SlotClass.MatrixTileStreamClass));
        HybridCpuTopologyCycleStateV1 state = model.Reserve(HybridCpuTopologyCycleStateV1.Empty(model.Topology), dma);

        Assert.Equal(CompilerCertificateClassV1.Lane6Stream, dma.CertificateClass);
        Assert.Equal(HybridCpuTopologyReasonCodeV1.CertificateClassConflict, model.CanReserve(state, matrix).Reason);
    }

    [Fact]
    public void UnknownRegisterAndStaleTopologyFailToConservativeFallback()
    {
        HybridCpuTopologyResourceModelV1 model = new();
        IrTopologyResourceFootprintV1 special = model.GetResourceFootprint(
            WithAccess(CreateInstruction(0), reads: [64], writes: []));
        IrTopologyResourceFootprintV1 stale = Build(CreateInstruction(1)) with { TopologyDigest = "stale" };
        HybridCpuTopologyCycleStateV1 state = HybridCpuTopologyCycleStateV1.Empty(model.Topology);

        Assert.Equal(HybridCpuTopologyReasonCodeV1.UnknownRegisterEvidence, model.CanReserve(state, special).Reason);
        Assert.Equal(HybridCpuTopologyReasonCodeV1.StaleTopologyDigest, model.CanReserve(state, stale).Reason);
        Assert.Equal(CompilerTopologyShadowDecisionV1.UnknownFallback, model.CanReserve(state, stale).Decision);
    }

    [Fact]
    public void ExplicitTopologyShadowPreservesExactDefaultArtifactsAndIsDeterministic()
    {
        VLIW_Instruction[] firstInput = CreateEncodedInstructions(12);
        VLIW_Instruction[] secondInput = CreateEncodedInstructions(12);
        HybridCpuCompiledProgram baseline = HybridCpuCanonicalCompiler.CompileProgram(0, firstInput);
        HybridCpuCompilationTopologyShadowResultV1 first =
            HybridCpuCanonicalCompiler.CompileProgramWithTopologyShadow(0, secondInput);
        HybridCpuCompilationTopologyShadowResultV1 second =
            HybridCpuCanonicalCompiler.CompileProgramWithTopologyShadow(0, CreateEncodedInstructions(12));

        Assert.Equal(baseline.ProgramImage, first.CompiledProgram.ProgramImage);
        Assert.Equal(CompilerScheduleFingerprintV1.HashSchedule(baseline.ProgramSchedule),
            CompilerScheduleFingerprintV1.HashSchedule(first.CompiledProgram.ProgramSchedule));
        Assert.Equal(CompilerScheduleFingerprintV1.HashBundles(baseline.BundleLayout),
            CompilerScheduleFingerprintV1.HashBundles(first.CompiledProgram.BundleLayout));
        Assert.Equal(first.ShadowReport.Fingerprint, second.ShadowReport.Fingerprint);
        Assert.True(first.ShadowReport.DecisionCount > 0);
    }

    [Fact]
    public void Phase03ProductionSourcesHaveNoWallClockProfileOrRuntimeAuthorityImports()
    {
        string root = FindRepositoryRoot();
        string[] paths =
        [
            "Compilers/HybridCPU_Compiler/Core/IR/Resources/HybridCpuMachineTopologyV1.cs",
            "Compilers/HybridCPU_Compiler/Core/IR/Resources/IrTopologyResourceFootprintV1.cs",
            "Compilers/HybridCPU_Compiler/Core/IR/Resources/CompilerTopologyShadowV1.cs"
        ];
        string[] forbidden =
        [
            "Stopwatch", "DateTime", "ProfileReader", "PhysicalRegisterFile", "RenameMap",
            "CommitMap", "FreeList", "BundleResourceCertificate4Way", "SafetyVerifier", "LegalityDecision"
        ];

        foreach (string path in paths)
        {
            string source = File.ReadAllText(Path.Combine(root, path));
            foreach (string token in forbidden) Assert.DoesNotContain(token, source, StringComparison.Ordinal);
        }
    }

    private static IrTopologyResourceFootprintV1 Build(
        IrInstruction instruction, HybridCpuMachineTopologyV1? topology = null) =>
        IrTopologyResourceFootprintBuilderV1.Build(instruction, topology ?? HybridCpuMachineTopologyV1.Default);

    private static IrInstruction WithAccess(IrInstruction instruction, byte[] reads, byte[] writes) =>
        instruction with
        {
            Annotation = instruction.Annotation with
            {
                Uses = Array.AsReadOnly(reads.Select(value => new IrOperand(IrOperandKind.Pointer, value, "read")).ToArray()),
                Defs = Array.AsReadOnly(writes.Select(value => new IrOperand(IrOperandKind.Pointer, value, "write")).ToArray())
            }
        };

    private static IrInstruction WithMemory(IrInstruction instruction, ulong address, uint length) =>
        instruction with
        {
            Annotation = instruction.Annotation with
            {
                MemoryReadRegion = new IrMemoryRegion(address, length, false),
                MemoryWriteRegion = null
            }
        };

    private static IrInstruction WithSlotClass(IrInstruction instruction, SlotClass slotClass) =>
        instruction with { Annotation = instruction.Annotation with { RequiredSlotClass = slotClass } };

    private static IrInstruction CreateInstruction(int index, byte virtualThreadId = 0)
    {
        HybridCpuIrBuilder builder = new HybridCpuIrBuilder();
        VLIW_Instruction encoded = CreateEncodedInstruction(index, virtualThreadId);
        IrInstruction result = builder.BuildProgram(
            virtualThreadId,
            [NativeTransportRuntimeAdapter.ToCore(in encoded)]).Instructions[0];
        return result with { Index = index, VirtualThreadId = virtualThreadId };
    }

    private static VLIW_Instruction CreateEncodedInstruction(int index, byte virtualThreadId) => new()
    {
        OpCode = (uint)InstructionsEnum.ADDI,
        DataTypeValue = DataTypeEnum.INT32,
        PredicateMask = 0xFF,
        DestSrc1Pointer = VLIW_Instruction.PackArchRegs(
            checked((byte)((index % 24) + 1)), 31, VLIW_Instruction.NoArchReg),
        Src2Pointer = (ulong)(index + 1),
        VirtualThreadId = virtualThreadId
    };

    private static VLIW_Instruction[] CreateEncodedInstructions(int count) =>
        Enumerable.Range(0, count).Select(index => CreateEncodedInstruction(index, 0)).ToArray();

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Compilers", "HybridCPU_Compiler"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
