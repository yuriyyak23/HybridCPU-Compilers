using System;
using System.Linq;
using System.Reflection;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCpuCanonicalCompiler = HybridCPU.Compiler.Core.Runtime.NativeTransportRuntimeAdapter;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Registers;
using static YAKSys_Hybrid_CPU.Processor;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan3Phase00AMetricsTests
{
    [Fact]
    public void MetricsEnabledDisabledAndAbsentPreserveCanonicalArtifacts()
    {
        _ = new Processor(ProcessorMode.Compiler);
        VLIW_Instruction[] input = CreateIndependentAluInstructions(6);

        HybridCpuCompiledProgram absent = HybridCpuCanonicalCompiler.CompileProgram(0, input);
        HybridCpuCompilationMetricsResultV1 disabled =
            HybridCpuCanonicalCompiler.CompileProgramWithMetrics(
                0,
                input,
                new CompilerScheduleMetricsRequestV1(Enabled: false));
        HybridCpuCompilationMetricsResultV1 enabled =
            HybridCpuCanonicalCompiler.CompileProgramWithMetrics(
                0,
                input,
                new CompilerScheduleMetricsRequestV1());

        Assert.Null(disabled.ScheduleMetrics);
        Assert.Null(disabled.ResourceTelemetry);
        Assert.NotNull(enabled.ScheduleMetrics);
        Assert.NotNull(enabled.ResourceTelemetry);
        AssertArtifactsEqual(absent, disabled.CompiledProgram);
        AssertArtifactsEqual(absent, enabled.CompiledProgram);
    }

    [Fact]
    public void SameInputProfileModelAndOptionsProduceByteIdenticalDeterministicMetrics()
    {
        _ = new Processor(ProcessorMode.Compiler);
        VLIW_Instruction[] input = CreateIndependentAluInstructions(4);
        CompilerScheduleMetricsRequestV1 request = new CompilerScheduleMetricsRequestV1(
            ProfileIdentity: "profile:none",
            ModelIdentity: "model:w8-current",
            OptionsIdentity: "options:canonical-defaults");

        byte[]? expected = null;
        string? expectedScheduleFingerprint = null;
        string? expectedBundleFingerprint = null;
        for (int repetition = 0; repetition < 100; repetition++)
        {
            HybridCpuCompilationMetricsResultV1 result =
                HybridCpuCanonicalCompiler.CompileProgramWithMetrics(0, input, request);
            CompilerScheduleMetricsV1 metrics = Assert.IsType<CompilerScheduleMetricsV1>(result.ScheduleMetrics);
            byte[] current = metrics.ToDeterministicJsonBytes();

            expected ??= current;
            expectedScheduleFingerprint ??= metrics.ScheduleFingerprint;
            expectedBundleFingerprint ??= metrics.BundleFingerprint;
            Assert.Equal(expected, current);
            Assert.Equal(expectedScheduleFingerprint, metrics.ScheduleFingerprint);
            Assert.Equal(expectedBundleFingerprint, metrics.BundleFingerprint);
        }
    }

    [Fact]
    public void InputFingerprintIncludesCanonicalPerInstructionSideband()
    {
        _ = new Processor(ProcessorMode.Compiler);
        VLIW_Instruction[] input = CreateIndependentAluInstructions(2);
        VliwBundleAnnotations singleVtAnnotations = new VliwBundleAnnotations(
        [
            new InstructionSlotMetadata(VtId.Create(0), SlotMetadata.Default),
            new InstructionSlotMetadata(VtId.Create(0), SlotMetadata.Default)
        ]);
        VliwBundleAnnotations mixedVtAnnotations = new VliwBundleAnnotations(
        [
            new InstructionSlotMetadata(VtId.Create(0), SlotMetadata.Default),
            new InstructionSlotMetadata(VtId.Create(1), SlotMetadata.Default)
        ]);

        HybridCpuCompilationMetricsResultV1 singleVt =
            HybridCpuCanonicalCompiler.CompileProgramWithMetrics(
                0,
                input,
                new CompilerScheduleMetricsRequestV1(),
                bundleAnnotations: singleVtAnnotations);
        HybridCpuCompilationMetricsResultV1 mixedVt =
            HybridCpuCanonicalCompiler.CompileProgramWithMetrics(
                0,
                input,
                new CompilerScheduleMetricsRequestV1(),
                bundleAnnotations: mixedVtAnnotations);

        Assert.NotEqual(
            Assert.IsType<CompilerScheduleMetricsV1>(singleVt.ScheduleMetrics).InputFingerprint,
            Assert.IsType<CompilerScheduleMetricsV1>(mixedVt.ScheduleMetrics).InputFingerprint);
    }

    [Fact]
    public void MetricsCountersReconcileWithScheduleAndBundlingResults()
    {
        _ = new Processor(ProcessorMode.Compiler);
        HybridCpuCompilationMetricsResultV1 result =
            HybridCpuCanonicalCompiler.CompileProgramWithMetrics(
                0,
                CreateIndependentAluInstructions(7),
                new CompilerScheduleMetricsRequestV1());
        CompilerScheduleMetricsV1 metrics = Assert.IsType<CompilerScheduleMetricsV1>(result.ScheduleMetrics);
        IrProgramSchedule schedule = result.CompiledProgram.ProgramSchedule;
        IrProgramBundlingResult bundling = result.CompiledProgram.BundleLayout;
        IrMaterializedBundle[] bundles = bundling.BlockResults.SelectMany(static block => block.Bundles).ToArray();

        Assert.Equal(schedule.BlockSchedules.Sum(static block => block.ScheduleLength), metrics.ScheduleCycles);
        Assert.Equal(bundles.Length, metrics.BundleCount);
        Assert.Equal(bundles.Max(static bundle => bundle.IssuedInstructionCount), metrics.MaxPackedVtWidth);
        Assert.Equal(
            bundles.Max(static bundle => bundle.CycleGroup.Instructions
                .GroupBy(static instruction => instruction.VirtualThreadId)
                .Max(static group => group.Count())),
            metrics.MaxSingleVtWidth);
        Assert.Equal(
            bundles.Sum(static bundle => (long)bundle.PlacementSearchSummary.EvaluatedPlacementCount),
            metrics.PlacementEvaluatedCount);
        Assert.Equal(
            bundles.Sum(static bundle => (long)bundle.PlacementSearchSummary.ParetoOptimalPlacementCount),
            metrics.PlacementParetoOptimalCount);
        Assert.Equal(
            bundles.Sum(static bundle => (long)bundle.PlacementSearchSummary.DominatedPlacementCount),
            metrics.PlacementDominatedCount);
        Assert.All(metrics.Blocks, static block => Assert.Equal($"bb:{block.BlockId}", block.RegionIdentity));
        Assert.Equal(CompilerMetricEvidenceQualityV1.Measured, metrics.ReadyWindowEvidenceQuality);
        Assert.True(metrics.SchedulerCandidateEvaluatedCount > 0);
    }

    [Fact]
    public void ResourceTelemetryIsMeasuredButExcludedFromDeterministicPayload()
    {
        _ = new Processor(ProcessorMode.Compiler);
        HybridCpuCompilationMetricsResultV1 result =
            HybridCpuCanonicalCompiler.CompileProgramWithMetrics(
                0,
                CreateIndependentAluInstructions(2),
                new CompilerScheduleMetricsRequestV1());
        CompilerCompileResourceTelemetryV1 resource =
            Assert.IsType<CompilerCompileResourceTelemetryV1>(result.ResourceTelemetry);
        string deterministicJson = Assert.IsType<CompilerScheduleMetricsV1>(result.ScheduleMetrics)
            .ToDeterministicJson();

        Assert.Equal(CompilerMetricEvidenceQualityV1.Measured, resource.EvidenceQuality);
        Assert.True(resource.ElapsedTimestampTicks >= 0);
        Assert.True(resource.TimestampFrequency > 0);
        Assert.True(resource.ObservedPeakManagedMemoryBytes > 0);
        Assert.DoesNotContain("elapsed", deterministicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("memory", deterministicJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SyntheticGreedyCounterexampleAndW8PlacementUseExistingExactSearch()
    {
        IrIssueSlotMask[] greedyCounterexample =
        [
            IrIssueSlotMask.Slot0 | IrIssueSlotMask.Slot1,
            IrIssueSlotMask.Slot0,
            IrIssueSlotMask.Slot1 | IrIssueSlotMask.Slot2
        ];
        IrBundlePlacementSearchResult exact =
            HybridCpuSlotModel.SearchStructuralAssignments(greedyCounterexample);

        Assert.True(exact.HasStructuralPlacement);
        Assert.Equal(3, exact.BestInstructionSlots.Distinct().Count());
        Assert.NotEqual(0, exact.BestInstructionSlots[0]);

        IrIssueSlotMask[] widthEight =
        [
            IrIssueSlotMask.Slot0,
            IrIssueSlotMask.Slot1,
            IrIssueSlotMask.Slot2,
            IrIssueSlotMask.Slot3,
            IrIssueSlotMask.Slot4,
            IrIssueSlotMask.Slot5,
            IrIssueSlotMask.Slot6,
            IrIssueSlotMask.Slot7
        ];
        IrBundlePlacementSearchResult w8 = HybridCpuSlotModel.SearchStructuralAssignments(widthEight);
        Assert.True(w8.HasStructuralPlacement);
        Assert.Equal(Enumerable.Range(0, 8), w8.BestInstructionSlots);
    }

    [Fact]
    public void SyntheticSystemSerializationPinnedLanesAndNoPlacementRemainBaselineConstraints()
    {
        IrOpcodeExecutionProfile system = HybridCpuHazardModel.GetExecutionProfile(
            NativeTransportRuntimeAdapter.ToCore(InstructionsEnum.ECALL));
        IrOpcodeExecutionProfile serialization = HybridCpuHazardModel.GetExecutionProfile(
            NativeTransportRuntimeAdapter.ToCore(InstructionsEnum.FENCE));
        IrOpcodeExecutionProfile lsu = HybridCpuHazardModel.GetExecutionProfile(
            NativeTransportRuntimeAdapter.ToCore(InstructionsEnum.LD));
        IrOpcodeExecutionProfile lane7 = HybridCpuHazardModel.GetExecutionProfile(
            NativeTransportRuntimeAdapter.ToCore(InstructionsEnum.BEQ));

        Assert.Equal(IrResourceClass.System, system.ResourceClass);
        Assert.Equal(SlotClass.SystemSingleton, (SlotClass)(byte)system.DerivedSlotClass);
        Assert.Equal(IrIssueSlotMask.Slot7, system.StructurallyAllowedSlots);
        Assert.NotEqual(IrSerializationKind.None, serialization.Serialization);
        Assert.Equal(IrResourceClass.LoadStore, lsu.ResourceClass);
        Assert.Equal(IrIssueSlotMask.Slot4 | IrIssueSlotMask.Slot5, lsu.StructurallyAllowedSlots);
        Assert.Equal(IrIssueSlotMask.Slot7, lane7.StructurallyAllowedSlots);
        IrBundlePlacementSearchResult pinnedLanePlacement =
            HybridCpuSlotModel.SearchStructuralAssignments(
                [IrIssueSlotMask.Slot6, IrIssueSlotMask.Slot7]);
        Assert.Equal([6, 7], pinnedLanePlacement.BestInstructionSlots);

        IrBundlePlacementSearchResult noPlacement = HybridCpuSlotModel.SearchStructuralAssignments(
            [IrIssueSlotMask.Slot6, IrIssueSlotMask.Slot6]);
        Assert.False(noPlacement.HasStructuralPlacement);
        Assert.Equal(0, noPlacement.Summary.EvaluatedPlacementCount);
    }

    [Fact]
    public void CanonicalProgramOrderFallbackIsReportedWithoutChangingOutput()
    {
        _ = new Processor(ProcessorMode.Compiler);
        VLIW_Instruction[] input = CreateIndependentAluInstructions(49);
        HybridCpuCompiledProgram baseline = HybridCpuCanonicalCompiler.CompileProgram(0, input);
        HybridCpuCompilationMetricsResultV1 observed =
            HybridCpuCanonicalCompiler.CompileProgramWithMetrics(
                0,
                input,
                new CompilerScheduleMetricsRequestV1());
        CompilerScheduleMetricsV1 metrics = Assert.IsType<CompilerScheduleMetricsV1>(observed.ScheduleMetrics);

        AssertArtifactsEqual(baseline, observed.CompiledProgram);
        Assert.Contains("SchedulerBasicBlockInstructionCap", metrics.FallbackReasons);
        Assert.Equal(1, metrics.SchedulerCapHitCount);
        Assert.Equal(CompilerMetricEvidenceQualityV1.Unavailable, metrics.ReadyWindowEvidenceQuality);
    }

    [Fact]
    public void MetricsSurfaceContainsNoRuntimeAuthorityPredicates()
    {
        string[] forbidden = ["IsAllowed", "ExecutionReady", "Commit", "Retire", "CapabilityAuthority"];
        string[] memberNames = typeof(CompilerScheduleMetricsV1)
            .GetMembers(BindingFlags.Instance | BindingFlags.Public)
            .Select(static member => member.Name)
            .ToArray();

        foreach (string forbiddenName in forbidden)
        {
            Assert.DoesNotContain(memberNames, name =>
                name.Contains(forbiddenName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void AssertArtifactsEqual(
        HybridCpuCompiledProgram expected,
        HybridCpuCompiledProgram actual)
    {
        Assert.Equal(expected.ProgramImage, actual.ProgramImage);
        Assert.Equal(
            CompilerScheduleFingerprintV1.HashSchedule(expected.ProgramSchedule),
            CompilerScheduleFingerprintV1.HashSchedule(actual.ProgramSchedule));
        Assert.Equal(
            CompilerScheduleFingerprintV1.HashBundles(expected.BundleLayout),
            CompilerScheduleFingerprintV1.HashBundles(actual.BundleLayout));
        Assert.Equal(expected.LoweredBundles.Count, actual.LoweredBundles.Count);
        Assert.Equal(expected.LoweredBundleAnnotations.Count, actual.LoweredBundleAnnotations.Count);
        for (int bundleIndex = 0; bundleIndex < expected.LoweredBundleAnnotations.Count; bundleIndex++)
        {
            IrBundleAnnotations expectedAnnotations = expected.LoweredBundleAnnotations[bundleIndex];
            IrBundleAnnotations actualAnnotations = actual.LoweredBundleAnnotations[bundleIndex];
            Assert.Equal(expectedAnnotations.Count, actualAnnotations.Count);
            for (int slotIndex = 0; slotIndex < expectedAnnotations.Count; slotIndex++)
            {
                Assert.Equal(
                    expectedAnnotations.TryGetInstructionSlotMetadata(slotIndex, out IrInstructionSlotMetadata expectedMetadata),
                    actualAnnotations.TryGetInstructionSlotMetadata(slotIndex, out IrInstructionSlotMetadata actualMetadata));
                Assert.Equal(expectedMetadata, actualMetadata);
            }
        }
    }

    private static VLIW_Instruction[] CreateIndependentAluInstructions(int count)
    {
        VLIW_Instruction[] instructions = new VLIW_Instruction[count];
        for (int index = 0; index < instructions.Length; index++)
        {
            instructions[index] = new VLIW_Instruction
            {
                OpCode = (uint)InstructionsEnum.ADDI,
                DataTypeValue = DataTypeEnum.INT32,
                PredicateMask = 0xFF,
                DestSrc1Pointer = VLIW_Instruction.PackArchRegs(
                    checked((byte)((index % 30) + 1)),
                    31,
                    VLIW_Instruction.NoArchReg),
                Src2Pointer = (ulong)(index + 1),
                VirtualThreadId = 0
            };
        }

        return instructions;
    }
}
