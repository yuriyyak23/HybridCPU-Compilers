using System;
using System.Linq;
using System.Reflection;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCpuCanonicalCompiler = HybridCPU.Compiler.Core.Runtime.NativeTransportRuntimeAdapter;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Registers;
using static YAKSys_Hybrid_CPU.Processor;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;
using SlotClass = HybridCPU.Compiler.Core.IR.IrSlotClass;
using SlotClassLaneMap = HybridCPU.Compiler.Core.Runtime.RuntimeSlotClassLaneMapAdapter;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan3Phase01ResourceModelTests
{
    [Fact]
    public void CompilerOwnedMachineDescriptionIsVersionedDeterministicAndMatchesTopologyOracle()
    {
        HybridCpuMachineDescriptionV1 description = HybridCpuMachineDescriptionV1.Default;

        Assert.Equal("HybridCpuMachineDescriptionV1", description.Schema);
        Assert.Equal(1, description.Version);
        Assert.Equal(8, description.Width);
        Assert.Equal(64, description.ContractDigest.Length);
        Assert.Equal(description.Key, HybridCpuMachineDescriptionV1.Default.Key);

        foreach (HybridCpuSlotClassTopologyV1 slotClass in description.SlotClasses)
        {
            Assert.Equal(SlotClassLaneMap.GetLaneMask(slotClass.SlotClass), slotClass.PhysicalLaneMask);
            Assert.Equal(SlotClassLaneMap.GetClassCapacity(slotClass.SlotClass), slotClass.Capacity);
        }

        Assert.True(description.TryGetSlotClass(SlotClass.BranchControl, out HybridCpuSlotClassTopologyV1 branch));
        Assert.True(description.TryGetSlotClass(SlotClass.SystemSingleton, out HybridCpuSlotClassTopologyV1 system));
        Assert.Equal(branch.PhysicalLaneMask, system.PhysicalLaneMask);
        Assert.True(description.TryGetSlotClass(SlotClass.DmaStreamClass, out HybridCpuSlotClassTopologyV1 dma));
        Assert.True(description.TryGetSlotClass(SlotClass.MatrixTileStreamClass, out HybridCpuSlotClassTopologyV1 matrix));
        Assert.Equal(dma.PhysicalLaneMask, matrix.PhysicalLaneMask);
        Assert.False(matrix.CountedByLegacyCompiler);
    }

    [Fact]
    public void FootprintExtractionUsesCanonicalFactsAndNeverConvertsUnknownToFree()
    {
        IrInstruction instruction = CreateInstructions(1)[0];
        instruction = WithFacts(
            instruction,
            IrIssueSlotMask.Slot0 | IrIssueSlotMask.Slot1,
            SlotClass.AluClass,
            IrSlotBindingKind.ClassFlexible,
            structural: IrStructuralResource.ReductionUnit,
            readRegion: new IrMemoryRegion(0x100, 16, false));
        IHybridCpuMachineResourceModel model = HybridCpuMachineResourceModelV1.Default;

        IrResourceFootprint first = model.GetResourceFootprint(instruction);
        IrResourceFootprint second = model.GetResourceFootprint(instruction);

        Assert.Equal(IrResourceFootprint.SchemaName, first.Schema);
        Assert.Equal(IrResourceFootprintProvenanceV1.StaticCanonical, first.Provenance);
        Assert.Equal(IrResourceFactPrecisionV1.Exact, first.SlotPrecision);
        Assert.Equal(IrMemoryEffectV1.Read, first.MemoryEffect);
        Assert.True(first.RequiresExactPlacement);
        Assert.False(first.RequiresLegacyFallback);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Contains(first.StructuralDemands, demand =>
            demand.Resource == IrStructuralResource.ReductionUnit && demand.Units == 1);

        IrInstruction unknown = WithFacts(
            instruction with { Index = 7 },
            IrIssueSlotMask.None,
            SlotClass.Unclassified,
            IrSlotBindingKind.ClassFlexible);
        IrResourceFootprint unknownFootprint = model.GetResourceFootprint(unknown);
        HybridCpuResourceReservationResultV1 unknownResult = model.CanReserve(
            HybridCpuCycleResourceState.Empty(model.Description),
            unknownFootprint);

        Assert.True(unknownFootprint.RequiresLegacyFallback);
        Assert.Equal(IrResourceFactPrecisionV1.Unknown, unknownFootprint.SlotPrecision);
        Assert.Equal(IrResourceFootprintProvenanceV1.Unknown, unknownFootprint.Provenance);
        Assert.Equal(HybridCpuResourceReservationDecisionV1.RequiresLegacyFallback, unknownResult.Decision);
        Assert.NotEqual(HybridCpuResourceReservationDecisionV1.Allowed, unknownResult.Decision);
    }

    [Fact]
    public void MatrixTileStreamTopologyGapIsExplicitFallbackNotZeroDemand()
    {
        IrInstruction matrixLoad = CreateInstructions(1)[0] with
        {
            Opcode = (HybridCpuOpcode)(uint)InstructionsEnum.MTILE_LOAD
        };

        IrResourceFootprint footprint =
            HybridCpuMachineResourceModelV1.Default.GetResourceFootprint(matrixLoad);

        Assert.True(footprint.RequiresLegacyFallback);
        Assert.Equal("MatrixTileStreamTopologyGap", footprint.FallbackReason);
        Assert.Equal(IrResourceFactPrecisionV1.Unknown, footprint.ClassPrecision);
        Assert.Equal(IrResourceFootprintProvenanceV1.ConservativeFallback, footprint.Provenance);
    }

    [Fact]
    public void CycleStateUsesExistingExactPlacementAndRejectsDuplicateMutation()
    {
        IrInstruction[] instructions = CreateInstructions(3);
        instructions[0] = WithFacts(instructions[0], IrIssueSlotMask.Slot0 | IrIssueSlotMask.Slot1, SlotClass.AluClass, IrSlotBindingKind.ClassFlexible);
        instructions[1] = WithFacts(instructions[1], IrIssueSlotMask.Slot0, SlotClass.AluClass, IrSlotBindingKind.ClassFlexible);
        instructions[2] = WithFacts(instructions[2], IrIssueSlotMask.Slot1 | IrIssueSlotMask.Slot2, SlotClass.AluClass, IrSlotBindingKind.ClassFlexible);
        IHybridCpuMachineResourceModel model = HybridCpuMachineResourceModelV1.Default;
        HybridCpuCycleResourceState state = HybridCpuCycleResourceState.Empty(model.Description);

        foreach (IrInstruction instruction in instructions)
        {
            IrResourceFootprint footprint = model.GetResourceFootprint(instruction);
            Assert.True(model.CanReserve(state, footprint).IsAllowed);
            HybridCpuCycleResourceState previous = state;
            state = model.Reserve(state, footprint);
            Assert.Equal(instruction.Index, state.ReservedFootprints[^1].InstructionIndex);
            Assert.Equal(state.ReservedInstructionCount - 1, previous.ReservedInstructionCount);
        }

        Assert.Equal(3, state.ReservedInstructionCount);
        Assert.True(HybridCpuSlotModel.HasStructuralPlacement(
            state.ReservedFootprints.Select(footprint => footprint.StructurallyAllowedSlots).ToArray()));
        HybridCpuResourceReservationResultV1 duplicate = model.CanReserve(
            state,
            model.GetResourceFootprint(instructions[0]));
        Assert.Equal(HybridCpuResourceReasonCodeV1.DuplicateInstruction, duplicate.ReasonCode);
        Assert.Throws<InvalidOperationException>(() => model.Reserve(state, model.GetResourceFootprint(instructions[0])));
    }

    [Fact]
    public void StructuredReasonsCoverAliasSerializationCapacityAndNoPlacement()
    {
        IHybridCpuMachineResourceModel model = HybridCpuMachineResourceModelV1.Default;
        IrInstruction[] source = CreateInstructions(6);

        IrInstruction branch = WithFacts(source[0], IrIssueSlotMask.Slot7, SlotClass.BranchControl, IrSlotBindingKind.HardPinned);
        IrInstruction system = WithFacts(source[1], IrIssueSlotMask.Slot7, SlotClass.SystemSingleton, IrSlotBindingKind.HardPinned);
        HybridCpuCycleResourceState aliasState = model.Reserve(
            HybridCpuCycleResourceState.Empty(model.Description),
            model.GetResourceFootprint(branch));
        Assert.Equal(
            HybridCpuResourceReasonCodeV1.AliasedLaneConflict,
            model.CanReserve(aliasState, model.GetResourceFootprint(system)).ReasonCode);

        IrInstruction exclusive = WithFacts(
            source[2], IrIssueSlotMask.Slot0, SlotClass.AluClass,
            IrSlotBindingKind.ClassFlexible, IrSerializationKind.ExclusiveCycle);
        HybridCpuCycleResourceState ordinaryState = model.Reserve(
            HybridCpuCycleResourceState.Empty(model.Description),
            model.GetResourceFootprint(source[3]));
        Assert.Equal(
            HybridCpuResourceReasonCodeV1.ExclusiveCycleRequired,
            model.CanReserve(ordinaryState, model.GetResourceFootprint(exclusive)).ReasonCode);

        IrInstruction pinnedA = WithFacts(source[4], IrIssueSlotMask.Slot6, SlotClass.LsuClass, IrSlotBindingKind.HardPinned);
        IrInstruction pinnedB = WithFacts(source[5], IrIssueSlotMask.Slot6, SlotClass.LsuClass, IrSlotBindingKind.HardPinned);
        HybridCpuCycleResourceState pinnedState = model.Reserve(
            HybridCpuCycleResourceState.Empty(model.Description),
            model.GetResourceFootprint(pinnedA));
        Assert.Equal(
            HybridCpuResourceReasonCodeV1.NoStructuralPlacement,
            model.CanReserve(pinnedState, model.GetResourceFootprint(pinnedB)).ReasonCode);

        IrInstruction[] alu = CreateInstructions(5);
        HybridCpuCycleResourceState capacityState = HybridCpuCycleResourceState.Empty(model.Description);
        for (int index = 0; index < 4; index++)
        {
            alu[index] = WithFacts(alu[index], IrIssueSlotMask.Scalar, SlotClass.AluClass, IrSlotBindingKind.ClassFlexible);
            capacityState = model.Reserve(capacityState, model.GetResourceFootprint(alu[index]));
        }
        alu[4] = WithFacts(alu[4], IrIssueSlotMask.Scalar, SlotClass.AluClass, IrSlotBindingKind.ClassFlexible);
        Assert.Equal(
            HybridCpuResourceReasonCodeV1.ClassCapacityExceeded,
            model.CanReserve(capacityState, model.GetResourceFootprint(alu[4])).ReasonCode);
    }

    [Fact]
    public void ShadowResourceSubsetMatchesLegacyAndClassifiesUnsupportedFallback()
    {
        IrInstruction[] source = CreateInstructions(2);
        IrInstruction first = WithFacts(source[0], IrIssueSlotMask.Slot6, SlotClass.LsuClass, IrSlotBindingKind.HardPinned);
        IrInstruction second = WithFacts(source[1], IrIssueSlotMask.Slot6, SlotClass.LsuClass, IrSlotBindingKind.HardPinned);
        HybridCpuInstructionLegalityChecker checker = new HybridCpuInstructionLegalityChecker();
        IrCandidateBundleAnalysis legacy = checker.AnalyzeCandidateBundle([first, second]);

        CompilerResourceDecisionV1 decision =
            CompilerResourceShadowEvaluatorV1.Evaluate([first, second], legacy);

        Assert.False(decision.LegacyResourceDecision);
        Assert.Equal(CompilerResourceShadowDecisionV1.Rejected, decision.ModelDecision);
        Assert.Equal(CompilerResourceMismatchKindV1.None, decision.MismatchKind);

        IrInstruction matrix = source[0] with { Opcode = (HybridCpuOpcode)(uint)InstructionsEnum.MTILE_STORE };
        IrCandidateBundleAnalysis matrixLegacy = checker.AnalyzeCandidateBundle([matrix]);
        CompilerResourceDecisionV1 fallback =
            CompilerResourceShadowEvaluatorV1.Evaluate([matrix], matrixLegacy);
        Assert.Equal(CompilerResourceShadowDecisionV1.UnknownFallback, fallback.ModelDecision);
        Assert.Equal(CompilerResourceMismatchKindV1.UnsupportedFallback, fallback.MismatchKind);
        Assert.False(fallback.IsDisagreement);
    }

    [Fact]
    public void SupportedClassCountDomainWithinW8HasZeroLegacyShadowDisagreement()
    {
        IrInstruction[] source = CreateInstructions(9);
        HybridCpuInstructionLegalityChecker checker = new HybridCpuInstructionLegalityChecker();

        for (int aluCount = 0; aluCount <= 5; aluCount++)
            for (int lsuCount = 0; lsuCount <= 3; lsuCount++)
                for (int dmaCount = 0; dmaCount <= 2; dmaCount++)
                    for (int branchCount = 0; branchCount <= 2; branchCount++)
                        for (int systemCount = 0; systemCount <= 2; systemCount++)
                        {
                            int total = aluCount + lsuCount + dmaCount + branchCount + systemCount;
                            if (total is 0 or > 9)
                            {
                                continue;
                            }

                            IrInstruction[] candidate = new IrInstruction[total];
                            int next = 0;
                            AddClass(candidate, source, ref next, aluCount, IrIssueSlotMask.Scalar, SlotClass.AluClass);
                            AddClass(candidate, source, ref next, lsuCount, IrIssueSlotMask.Slot4 | IrIssueSlotMask.Slot5, SlotClass.LsuClass);
                            AddClass(candidate, source, ref next, dmaCount, IrIssueSlotMask.Slot6, SlotClass.DmaStreamClass);
                            AddClass(candidate, source, ref next, branchCount, IrIssueSlotMask.Slot7, SlotClass.BranchControl);
                            AddClass(candidate, source, ref next, systemCount, IrIssueSlotMask.Slot7, SlotClass.SystemSingleton);

                            IrCandidateBundleAnalysis legacy = checker.AnalyzeCandidateBundle(candidate);
                            CompilerResourceDecisionV1 shadow = CompilerResourceShadowEvaluatorV1.Evaluate(candidate, legacy);
                            Assert.False(shadow.IsDisagreement);
                            Assert.NotEqual(CompilerResourceShadowDecisionV1.UnknownFallback, shadow.ModelDecision);
                        }
    }

    [Fact]
    public void EveryNonEmptyW8MaskPairMatchesExistingExactPlacement()
    {
        IrInstruction[] source = CreateInstructions(2);
        IHybridCpuMachineResourceModel model = HybridCpuMachineResourceModelV1.Default;

        for (int firstMaskValue = 1; firstMaskValue <= 0xFF; firstMaskValue++)
            for (int secondMaskValue = 1; secondMaskValue <= 0xFF; secondMaskValue++)
            {
                IrIssueSlotMask firstMask = (IrIssueSlotMask)firstMaskValue;
                IrIssueSlotMask secondMask = (IrIssueSlotMask)secondMaskValue;
                IrResourceFootprint first = model.GetResourceFootprint(WithFacts(
                    source[0], firstMask, SlotClass.AluClass, IrSlotBindingKind.ClassFlexible));
                IrResourceFootprint second = model.GetResourceFootprint(WithFacts(
                    source[1], secondMask, SlotClass.AluClass, IrSlotBindingKind.ClassFlexible));
                HybridCpuCycleResourceState state = model.Reserve(
                    HybridCpuCycleResourceState.Empty(model.Description),
                    first);

                bool exactPlacement = HybridCpuSlotModel.HasStructuralPlacement([firstMask, secondMask]);
                Assert.Equal(exactPlacement, model.CanReserve(state, second).IsAllowed);
            }
    }

    [Fact]
    public void ShadowDiagnosticsRepeatOneHundredTimesAndReportCollectorCap()
    {
        IrInstruction[] candidate = CreateInstructions(4);
        HybridCpuInstructionLegalityChecker checker = new HybridCpuInstructionLegalityChecker();
        IrCandidateBundleAnalysis legacy = checker.AnalyzeCandidateBundle(candidate);
        string? expectedFingerprint = null;

        for (int repeat = 0; repeat < 100; repeat++)
        {
            CompilerResourceShadowCollectorV1 collector = new CompilerResourceShadowCollectorV1(maximumDecisions: 1);
            collector.Record(CompilerResourceShadowEvaluatorV1.Evaluate(candidate, legacy));
            collector.Record(CompilerResourceShadowEvaluatorV1.Evaluate(candidate, legacy));
            CompilerResourceShadowReportV1 report = collector.Complete();
            expectedFingerprint ??= report.Fingerprint;
            Assert.Equal(expectedFingerprint, report.Fingerprint);
            Assert.Equal(1, report.DecisionCount);
            Assert.Equal(2, report.AttemptedDecisionCount);
            Assert.Equal(1, report.TruncatedDecisionCount);
        }
    }

    [Fact]
    public void ShadowCollectorCapStopsModelEvaluationByDeterministicCounter()
    {
        IrInstruction[] candidate = CreateInstructions(4);
        CountingResourceModel model = new CountingResourceModel();
        CompilerResourceShadowCollectorV1 collector = new CompilerResourceShadowCollectorV1(maximumDecisions: 1);
        HybridCpuInstructionLegalityChecker checker = new HybridCpuInstructionLegalityChecker
        {
            ResourceModel = model,
            ResourceShadowCollector = collector
        };

        _ = checker.AnalyzeCandidateBundle(candidate);
        int callsAtCap = model.FootprintCallCount;
        _ = checker.AnalyzeCandidateBundle(candidate);
        CompilerResourceShadowReportV1 report = collector.Complete();

        Assert.True(callsAtCap > 0);
        Assert.Equal(callsAtCap, model.FootprintCallCount);
        Assert.Equal(1, report.DecisionCount);
        Assert.Equal(2, report.AttemptedDecisionCount);
        Assert.Equal(1, report.TruncatedDecisionCount);
    }

    [Fact]
    public void ExplicitShadowCompilationPreservesEveryCanonicalArtifactAndIsDeterministic()
    {
        _ = new Processor(ProcessorMode.Compiler);
        VLIW_Instruction[] input = CreateEncodedInstructions(8);
        HybridCpuCompiledProgram baseline = HybridCpuCanonicalCompiler.CompileProgram(0, input);

        HybridCpuCompilationResourceShadowResultV1 first =
            HybridCpuCanonicalCompiler.CompileProgramWithResourceShadow(0, input);
        HybridCpuCompilationResourceShadowResultV1 second =
            HybridCpuCanonicalCompiler.CompileProgramWithResourceShadow(0, input);

        AssertArtifactsEqual(baseline, first.CompiledProgram);
        AssertArtifactsEqual(baseline, second.CompiledProgram);
        Assert.True(first.ShadowReport.DecisionCount > 0);
        Assert.InRange(first.ShadowReport.DecisionCount, 1, 64);
        Assert.True(first.ShadowReport.AttemptedDecisionCount >= first.ShadowReport.DecisionCount);
        Assert.Equal(0, first.ShadowReport.DisagreementCount);
        Assert.Equal(0, first.ShadowReport.FallbackCount);
        Assert.Equal(first.ShadowReport.Fingerprint, second.ShadowReport.Fingerprint);
        Assert.Equal(first.ShadowReport.Decisions.Count, second.ShadowReport.Decisions.Count);
        for (int index = 0; index < first.ShadowReport.Decisions.Count; index++)
        {
            AssertDecisionEqual(
                first.ShadowReport.Decisions[index],
                second.ShadowReport.Decisions[index]);
        }
    }

    [Fact]
    public void ProgramOrderFallbackIsObservedWithoutBecomingItsDecisionSource()
    {
        IrProgram program = new HybridCpuIrBuilder().BuildProgram(
            0,
            NativeTransportRuntimeAdapter.ToCore(CreateEncodedInstructions(2)));
        IrProgramDependencyGraph dependencyGraph =
            new HybridCpuProgramDependencyAnalyzer().AnalyzeProgram(program);
        IrProgramSchedule baseline =
            new HybridCpuProgramOrderLocalScheduler().ScheduleProgram(program, dependencyGraph);
        CompilerResourceShadowCollectorV1 collector = new CompilerResourceShadowCollectorV1();
        IrProgramSchedule observed = new HybridCpuProgramOrderLocalScheduler
        {
            ResourceShadowCollector = collector
        }.ScheduleProgram(program, dependencyGraph);
        CompilerResourceShadowReportV1 report = collector.Complete();

        Assert.Equal(
            CompilerScheduleFingerprintV1.HashSchedule(baseline),
            CompilerScheduleFingerprintV1.HashSchedule(observed));
        Assert.True(report.DecisionCount > 0);
        Assert.Equal(0, report.DisagreementCount);
        Assert.Equal(0, report.FallbackCount);
    }

    [Fact]
    public void ShadowEvaluationFailureIsUnknownAndCannotChangeLegacyAnalysis()
    {
        IrInstruction[] candidate = CreateInstructions(2);
        IrCandidateBundleAnalysis baseline =
            new HybridCpuInstructionLegalityChecker().AnalyzeCandidateBundle(candidate);
        CompilerResourceShadowCollectorV1 collector = new CompilerResourceShadowCollectorV1();
        HybridCpuInstructionLegalityChecker checker = new HybridCpuInstructionLegalityChecker
        {
            ResourceModel = new ThrowingResourceModel(),
            ResourceShadowCollector = collector
        };

        IrCandidateBundleAnalysis observed = checker.AnalyzeCandidateBundle(candidate);
        CompilerResourceShadowReportV1 report = collector.Complete();

        Assert.Equal(baseline.IsStructurallyAdmissible, observed.IsStructurallyAdmissible);
        Assert.Equal(
            baseline.Legality.Hazards.Select(hazard => hazard.Reason),
            observed.Legality.Hazards.Select(hazard => hazard.Reason));
        CompilerResourceDecisionV1 failure = Assert.Single(report.Decisions);
        Assert.Equal(CompilerResourceShadowDecisionV1.UnknownFallback, failure.ModelDecision);
        Assert.Equal(CompilerResourceMismatchKindV1.ShadowEvaluationFailure, failure.MismatchKind);
        Assert.Equal(HybridCpuResourceReasonCodeV1.ShadowEvaluationFailure, failure.ReasonCode);
        Assert.False(failure.IsDisagreement);
    }

    [Fact]
    public void FeasibilitySurfaceCannotAcceptProfilesOrClaimRuntimeAuthority()
    {
        MethodInfo[] feasibilityMethods = typeof(IHybridCpuMachineResourceModel).GetMethods();
        Assert.DoesNotContain(feasibilityMethods.SelectMany(method => method.GetParameters()), parameter =>
            parameter.ParameterType.Name.Contains("Profile", StringComparison.OrdinalIgnoreCase));

        string[] forbidden = ["ExecutionReady", "Commit", "Retire", "Publication", "RuntimeLegality"];
        string[] publicNames = typeof(HybridCpuResourceReservationResultV1)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(member => member.Name)
            .ToArray();
        foreach (string name in forbidden)
        {
            Assert.DoesNotContain(publicNames, member =>
                member.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static IrInstruction[] CreateInstructions(int count)
    {
        HybridCpuIrBuilder builder = new HybridCpuIrBuilder();
        return builder.BuildProgram(
            0,
            NativeTransportRuntimeAdapter.ToCore(CreateEncodedInstructions(count))).Instructions.ToArray();
    }

    private static VLIW_Instruction[] CreateEncodedInstructions(int count)
    {
        VLIW_Instruction[] instructions = new VLIW_Instruction[count];
        for (int index = 0; index < count; index++)
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

    private static IrInstruction WithFacts(
        IrInstruction instruction,
        IrIssueSlotMask slots,
        SlotClass slotClass,
        IrSlotBindingKind bindingKind,
        IrSerializationKind serialization = IrSerializationKind.None,
        IrStructuralResource structural = IrStructuralResource.None,
        IrMemoryRegion? readRegion = null) =>
        instruction with
        {
            Annotation = instruction.Annotation with
            {
                LegalSlots = slots,
                RequiredSlotClass = slotClass,
                BindingKind = bindingKind,
                Serialization = serialization,
                StructuralResources = structural,
                MemoryReadRegion = readRegion,
                MemoryWriteRegion = null,
                ControlFlowKind = IrControlFlowKind.None,
                IsBarrierLike = false
            }
        };

    private static void AddClass(
        IrInstruction[] destination,
        IrInstruction[] source,
        ref int next,
        int count,
        IrIssueSlotMask slots,
        SlotClass slotClass)
    {
        for (int index = 0; index < count; index++)
        {
            destination[next] = WithFacts(
                source[next],
                slots,
                slotClass,
                slots is IrIssueSlotMask.Slot6 or IrIssueSlotMask.Slot7
                    ? IrSlotBindingKind.HardPinned
                    : IrSlotBindingKind.ClassFlexible);
            next++;
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
                    expectedAnnotations.TryGetInstructionSlotMetadata(
                        slotIndex,
                        out IrInstructionSlotMetadata expectedMetadata),
                    actualAnnotations.TryGetInstructionSlotMetadata(
                        slotIndex,
                        out IrInstructionSlotMetadata actualMetadata));
                Assert.Equal(expectedMetadata, actualMetadata);
            }
        }
    }

    private static void AssertDecisionEqual(
        CompilerResourceDecisionV1 expected,
        CompilerResourceDecisionV1 actual)
    {
        Assert.Equal(expected.Schema, actual.Schema);
        Assert.Equal(expected.MachineDescriptionKey, actual.MachineDescriptionKey);
        Assert.Equal(expected.LegacyResourceDecision, actual.LegacyResourceDecision);
        Assert.Equal(expected.ModelDecision, actual.ModelDecision);
        Assert.Equal(expected.MismatchKind, actual.MismatchKind);
        Assert.Equal(expected.ReasonCode, actual.ReasonCode);
        Assert.Equal(expected.StateDigest, actual.StateDigest);
        Assert.Equal(expected.InstructionIndexes.ToArray(), actual.InstructionIndexes.ToArray());
        Assert.Equal(expected.FootprintFingerprints.ToArray(), actual.FootprintFingerprints.ToArray());
    }

    private sealed class ThrowingResourceModel : IHybridCpuMachineResourceModel
    {
        public HybridCpuMachineDescriptionV1 Description => HybridCpuMachineDescriptionV1.Default;

        public IrResourceFootprint GetResourceFootprint(IrInstruction instruction) =>
            throw new InvalidOperationException("Synthetic shadow failure.");

        public HybridCpuResourceReservationResultV1 CanReserve(
            HybridCpuCycleResourceState state,
            IrResourceFootprint footprint) => throw new InvalidOperationException();

        public HybridCpuCycleResourceState Reserve(
            HybridCpuCycleResourceState state,
            IrResourceFootprint footprint) => throw new InvalidOperationException();

        public HybridCpuResourceIncrementalCostV1 IncrementalCost(
            HybridCpuCycleResourceState state,
            IrResourceFootprint footprint) => throw new InvalidOperationException();
    }

    private sealed class CountingResourceModel : IHybridCpuMachineResourceModel
    {
        private readonly IHybridCpuMachineResourceModel _inner =
            HybridCpuMachineResourceModelV1.Default;

        public int FootprintCallCount { get; private set; }
        public HybridCpuMachineDescriptionV1 Description => _inner.Description;

        public IrResourceFootprint GetResourceFootprint(IrInstruction instruction)
        {
            FootprintCallCount++;
            return _inner.GetResourceFootprint(instruction);
        }

        public HybridCpuResourceReservationResultV1 CanReserve(
            HybridCpuCycleResourceState state,
            IrResourceFootprint footprint) => _inner.CanReserve(state, footprint);

        public HybridCpuCycleResourceState Reserve(
            HybridCpuCycleResourceState state,
            IrResourceFootprint footprint) => _inner.Reserve(state, footprint);

        public HybridCpuResourceIncrementalCostV1 IncrementalCost(
            HybridCpuCycleResourceState state,
            IrResourceFootprint footprint) => _inner.IncrementalCost(state, footprint);
    }
}
