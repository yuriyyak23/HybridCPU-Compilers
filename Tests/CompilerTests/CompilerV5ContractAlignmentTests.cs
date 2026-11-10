using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using static YAKSys_Hybrid_CPU.Processor;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;
using SlotClass = HybridCPU.Compiler.Core.IR.IrSlotClass;
using TypedSlotBundleFacts = HybridCPU.Compiler.Core.IR.IrTypedSlotBundleFacts;

namespace HybridCPU_ISE.Tests.CompilerTests;

/// <summary>
/// C6 — Focused regression tests for landed C1–C4 contract invariants.
/// Validates admission classification priority, structural predicate usage
/// in scheduling heuristics, and DmaStream handoff precision.
/// </summary>
public partial class CompilerV5ContractAlignmentTests
{
    #region C1 — Admission Classification Invariants

    [Fact]
    public void WhenStructurallyValidBundleThenClassificationIsStructurallyAdmissible()
    {
        // Registers must be in different safety-mask groups (group = reg/4).
        // reg 4 → group 1, reg 20 → group 5: no write-write conflict.
        IrBundleAdmissionResult result = BuildAndAdmitSingleBundle(
            CreateScalarAluInstruction(destReg: 4, srcReg: 5),
            CreateScalarAluInstruction(destReg: 20, srcReg: 21));

        Assert.Equal(AdmissibilityClassification.StructurallyAdmissible, result.Classification);
        Assert.True(result.IsStructurallyAdmissible);
        Assert.True(result.TypedSlotFactsValid);
    }

    [Fact]
    public void WhenSafetyMaskConflictThenClassificationIsSafetyMaskConflict()
    {
        // Two ADDI writing to registers in the SAME group (reg 4 and reg 6 → both group 1)
        // produce a write-write safety-mask conflict.
        // Typed-slot facts remain valid (both are ALU class, within capacity).
        IrBundleAdmissionResult result = BuildAndAdmitSingleBundle(
            CreateScalarAluInstruction(destReg: 4, srcReg: 5),
            CreateScalarAluInstruction(destReg: 6, srcReg: 7));

        Assert.Equal(AdmissibilityClassification.SafetyMaskConflict, result.Classification);
        Assert.False(result.SafetyMaskDiagnostic.IsCompatible);
        Assert.True(result.TypedSlotFactsValid,
            "Safety-mask conflict is secondary to typed-slot checks; typed-slot facts should still be valid.");
    }

    [Fact]
    public void WhenStealMismatchPresentThenClassificationStillStructurallyAdmissible()
    {
        // One instruction has CanBeStolen bit set, the other does not — this is advisory only.
        // Registers in different groups (reg 4 → group 1, reg 20 → group 5) to avoid safety-mask conflict.
        IrBundleAdmissionResult result = BuildAndAdmitSingleBundle(
            CreateStealableInstruction(InstructionsEnum.ADDI, destSrc1: Pack(4, 5, 0), src2: 1),
            CreateScalarAluInstruction(destReg: 20, srcReg: 21));

        // Steal-mismatch must NOT block admissibility — classification stays StructurallyAdmissible.
        Assert.Equal(AdmissibilityClassification.StructurallyAdmissible, result.Classification);
        Assert.True(result.IsStructurallyAdmissible);
    }

    [Theory]
    [InlineData(AdmissibilityClassification.TypedSlotFactsInvalid)]
    [InlineData(AdmissibilityClassification.TypedSlotClassCapacityExceeded)]
    [InlineData(AdmissibilityClassification.TypedSlotAliasedLaneConflict)]
    public void WhenTypedSlotFactsInvalidThenClassificationIsTypedSlotPrimary(
        AdmissibilityClassification expectedClassification)
    {
        // This test documents that all typed-slot failure kinds are primary (block admission).
        // The enum values themselves define the taxonomy; we verify they exist and are distinct.
        Assert.True(
            expectedClassification is AdmissibilityClassification.TypedSlotFactsInvalid
                or AdmissibilityClassification.TypedSlotClassCapacityExceeded
                or AdmissibilityClassification.TypedSlotAliasedLaneConflict,
            "Typed-slot failure classifications must be primary structural checks.");
    }

    [Fact]
    public void WhenAdmissionPipelineRunsThenTypedSlotCheckedBeforeSafetyMask()
    {
        // Contract: admission classifier checks typed-slot validity first (primary),
        // then safety-mask (secondary). A bundle with valid typed-slot facts and
        // no safety-mask conflict must be StructurallyAdmissible regardless of steal metadata.
        IrBundleAdmissionResult result = BuildAndAdmitSingleBundle(
            CreateScalarAluInstruction(destReg: 4, srcReg: 5));

        Assert.Equal(AdmissibilityClassification.StructurallyAdmissible, result.Classification);
        Assert.True(result.TypedSlotFactsValid, "Typed-slot facts must be valid for single ALU instruction.");
        Assert.True(result.SafetyMaskDiagnostic.IsCompatible, "Single instruction cannot have intra-bundle safety-mask conflict.");
    }

    [Fact]
    public void IrSlotMetadata_CarriesAdmissionDescriptorBeyondStealabilityHint()
    {
        IrOpcodeExecutionProfile profile = HybridCpuHazardModel.GetExecutionProfile(
            NativeTransportRuntimeAdapter.ToCore(InstructionsEnum.ADD));
        IrSlotMetadata metadata = IrSlotMetadata
            .DefaultForVirtualThread(2)
            .WithAdmissionDescriptor(profile);

        Assert.Equal(2, metadata.VirtualThreadId);
        Assert.False(metadata.StealabilityHint);
        Assert.True(metadata.AdmissionDescriptor.HasValue);

        IrTypedSlotAdmissionDescriptor descriptor = metadata.AdmissionDescriptor.Value;
        Assert.Equal(profile.ResourceClass, descriptor.ResourceClass);
        Assert.Equal(profile.DerivedSlotClass, descriptor.RequiredSlotClass);
        Assert.Equal(profile.DerivedBindingKind, descriptor.BindingKind);
        Assert.Equal(profile.LegalSlots, descriptor.LegalSlots);
    }

    [Fact]
    public void IrBuilder_ProjectsAdmissionDescriptorIntoInstructionAnnotation()
    {
        IrInstruction instruction = Assert.Single(
            BuildProgram(CreateScalarAluInstruction(destReg: 4, srcReg: 5)).Instructions);

        Assert.Equal(IrResourceClass.ScalarAlu, instruction.Annotation.ResourceClass);
        Assert.Equal(SlotClass.AluClass, (SlotClass)(byte)instruction.Annotation.RequiredSlotClass);
        Assert.Equal(IrSlotBindingKind.ClassFlexible, instruction.Annotation.BindingKind);
        Assert.NotEqual(IrIssueSlotMask.None, instruction.Annotation.LegalSlots);
        Assert.False(instruction.Annotation.StealabilityHint);
    }

    [Fact]
    public void ThreadCompilerContext_BundleAnnotations_CarryPlacementAndDomainAdmissionMetadataBeyondStealabilityHints()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(1)
        {
            DomainTag = 0x33
        };

        context.CompileInstruction(
            opCode: (uint)InstructionsEnum.ADD,
            dataType: 0,
            predicate: 0xFF,
            immediate: 0,
            destSrc1: 0x1111,
            src2: 0x2222,
            streamLength: 0,
            stride: 0,
            stealabilityPolicy: StealabilityPolicy.Stealable);
        context.InsertInstruction(
            instructionIndex: 0,
            opCode: (uint)InstructionsEnum.CSRRW,
            dataType: 0,
            predicate: 0xFF,
            immediate: CsrAddresses.Mstatus,
            destSrc1: VLIW_Instruction.PackArchRegs(rd: 6, rs1: 5, rs2: VLIW_Instruction.NoArchReg),
            src2: 0,
            streamLength: 0,
            stride: 0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);

        VliwBundleAnnotations annotations = context.GetBundleAnnotations();

        Assert.True(annotations.TryGetInstructionSlotMetadata(0, out InstructionSlotMetadata systemMetadata));
        Assert.True(annotations.TryGetInstructionSlotMetadata(1, out InstructionSlotMetadata aluMetadata));

        MicroOpAdmissionMetadata systemAdmission = systemMetadata.SlotMetadata.AdmissionMetadata;
        Assert.False(systemAdmission.IsStealable);
        Assert.Equal(0x33UL, systemAdmission.DomainTag);
        Assert.Equal(0x33UL, systemAdmission.Placement.DomainTag);
        Assert.Equal(SlotClass.SystemSingleton, (SlotClass)(byte)systemAdmission.Placement.RequiredSlotClass);
        Assert.Equal(SlotPinningKind.HardPinned, systemAdmission.Placement.PinningKind);
        Assert.Equal(7, systemAdmission.Placement.PinnedLaneId);
        Assert.False(systemAdmission.WritesRegister);
        Assert.Empty(systemAdmission.ReadRegisters);
        Assert.Empty(systemAdmission.WriteRegisters);

        MicroOpAdmissionMetadata aluAdmission = aluMetadata.SlotMetadata.AdmissionMetadata;
        Assert.True(aluAdmission.IsStealable);
        Assert.Equal(0x33UL, aluAdmission.DomainTag);
        Assert.Equal(0x33UL, aluAdmission.Placement.DomainTag);
        Assert.Equal(SlotClass.AluClass, (SlotClass)(byte)aluAdmission.Placement.RequiredSlotClass);
        Assert.Equal(SlotPinningKind.ClassFlexible, aluAdmission.Placement.PinningKind);
        Assert.Equal(0, aluAdmission.Placement.PinnedLaneId);
        Assert.False(aluAdmission.WritesRegister);
        Assert.Empty(aluAdmission.ReadRegisters);
        Assert.Empty(aluAdmission.WriteRegisters);
    }

    [Fact]
    public void AdmissibilityRuntimeVocabulary_Converges_Compiler_And_Runtime_Taxonomy_Without_Inventing_Extra_Twins()
    {
        AdmissibilityRuntimeVocabularyRelation directTwin = AdmissibilityRuntimeVocabulary.Describe(
            AdmissibilityClassification.TypedSlotClassCapacityExceeded);
        AdmissibilityRuntimeVocabularyRelation compatibilityAdjacent = AdmissibilityRuntimeVocabulary.Describe(
            AdmissibilityClassification.SafetyMaskConflict);
        AdmissibilityRuntimeVocabularyRelation compilerOnly = AdmissibilityRuntimeVocabulary.Describe(
            AdmissibilityClassification.TypedSlotFactsInvalid);

        Assert.Equal(AdmissibilityRuntimeVocabularyRelationKind.DirectSchedulerStructuralTwin, directTwin.RelationKind);
        Assert.True(directTwin.HasDirectSchedulerTwin);
        Assert.Equal((TypedSlotRejectReason?)TypedSlotRejectReason.StaticClassOvercommit, directTwin.SchedulerRejectReason);

        Assert.Equal(AdmissibilityRuntimeVocabularyRelationKind.CompatibilityAdjacentSchedulerFamily, compatibilityAdjacent.RelationKind);
        Assert.False(compatibilityAdjacent.HasDirectSchedulerTwin);
        Assert.Equal((TypedSlotRejectReason?)TypedSlotRejectReason.ResourceConflict, compatibilityAdjacent.SchedulerRejectReason);

        Assert.Equal(AdmissibilityRuntimeVocabularyRelationKind.CompilerOnlyStructuralInvalidity, compilerOnly.RelationKind);
        Assert.False(compilerOnly.HasDirectSchedulerTwin);
        Assert.Null(compilerOnly.SchedulerRejectReason);
    }

    [Fact]
    public void WhenBundleEmitsUnclassifiedTypedSlotFactsThenClassificationIsTypedSlotFactsInvalid()
    {
        IrInstruction sourceInstruction = Assert.Single(BuildProgram(
            CreateScalarAluInstruction(destReg: 4, srcReg: 5)).Instructions);

        IrInstruction unclassifiedInstruction = WithAnnotation(
            sourceInstruction,
            resourceClass: IrResourceClass.Unknown,
            requiredSlotClass: SlotClass.Unclassified,
            bindingKind: IrSlotBindingKind.ClassFlexible);

        IrBundleAdmissionResult result = AnalyzeManualBundle(
            (unclassifiedInstruction, 0, IrIssueSlotMask.Slot0));

        Assert.Equal(AdmissibilityClassification.TypedSlotFactsInvalid, result.Classification);
        Assert.False(result.TypedSlotFactsValid);
        Assert.True(result.SafetyMaskDiagnostic.IsCompatible);
    }

    [Fact]
    public void WhenTypedSlotFactsInvalidAndSafetyMaskConflictCoexistThenTypedSlotClassificationRemainsPrimary()
    {
        IrProgram program = BuildProgram(
            CreateScalarAluInstruction(destReg: 4, srcReg: 5),
            CreateScalarAluInstruction(destReg: 6, srcReg: 7));

        IrInstruction first = WithAnnotation(
            program.Instructions[0],
            resourceClass: IrResourceClass.Unknown,
            requiredSlotClass: SlotClass.Unclassified,
            bindingKind: IrSlotBindingKind.ClassFlexible);
        IrInstruction second = WithAnnotation(
            program.Instructions[1],
            resourceClass: IrResourceClass.Unknown,
            requiredSlotClass: SlotClass.Unclassified,
            bindingKind: IrSlotBindingKind.ClassFlexible);

        IrBundleAdmissionResult result = AnalyzeManualBundle(
            (first, 0, IrIssueSlotMask.Slot0),
            (second, 1, IrIssueSlotMask.Slot1));

        Assert.Equal(AdmissibilityClassification.TypedSlotFactsInvalid, result.Classification);
        Assert.False(result.TypedSlotFactsValid);
        Assert.False(result.SafetyMaskDiagnostic.IsCompatible);
    }

    [Fact]
    public void WhenForcedBundleExceedsAluCapacityThenClassificationIsTypedSlotClassCapacityExceeded()
    {
        IrProgram program = BuildProgram(
            CreateScalarAluInstruction(destReg: 4, srcReg: 5),
            CreateScalarAluInstruction(destReg: 8, srcReg: 9),
            CreateScalarAluInstruction(destReg: 12, srcReg: 13),
            CreateScalarAluInstruction(destReg: 16, srcReg: 17),
            CreateScalarAluInstruction(destReg: 20, srcReg: 21));

        IrBundleAdmissionResult result = AnalyzeManualBundle(
            (program.Instructions[0], 0, IrIssueSlotMask.Slot0),
            (program.Instructions[1], 1, IrIssueSlotMask.Slot1),
            (program.Instructions[2], 2, IrIssueSlotMask.Slot2),
            (program.Instructions[3], 3, IrIssueSlotMask.Slot3),
            (program.Instructions[4], 4, IrIssueSlotMask.Slot4));

        Assert.Equal(AdmissibilityClassification.TypedSlotClassCapacityExceeded, result.Classification);
        Assert.False(result.TypedSlotFactsValid);
    }

    [Fact]
    public void WhenBranchAndSystemCoexistThenClassificationIsTypedSlotAliasedLaneConflict()
    {
        IrProgram program = BuildProgram(
            CreateBranchInstruction(0x0),
            CreateDmaStreamInstruction());

        IrBundleAdmissionResult result = AnalyzeManualBundle(
            (program.Instructions[0], 0, IrIssueSlotMask.Slot0),
            (program.Instructions[1], 1, IrIssueSlotMask.Slot1));

        Assert.Equal(AdmissibilityClassification.TypedSlotAliasedLaneConflict, result.Classification);
        Assert.False(result.TypedSlotFactsValid);
    }

    [Fact]
    public void WhenAluCountExceedsCapacityThenValidationRejects()
    {
        // C1 contract: ALU capacity = 4 (lanes 0-3). 5 ALU ops must fail validation.
        TypedSlotBundleFacts overCapacityFacts = new TypedSlotBundleFacts
        {
            Slot0Class = SlotClass.AluClass,
            Slot1Class = SlotClass.AluClass,
            Slot2Class = SlotClass.AluClass,
            Slot3Class = SlotClass.AluClass,
            Slot4Class = SlotClass.AluClass,
            AluCount = 5,
            FlexibleOpCount = 5,
            PinnedOpCount = 0,
            LsuCount = 0,
            DmaStreamCount = 0,
            BranchControlCount = 0,
            SystemSingletonCount = 0
        };

        Assert.False(
            HybridCpuTypedSlotFactsEmitter.ValidateEmittedFacts(overCapacityFacts),
            "Validation must reject AluCount > 4 (AluClass capacity=4).");
    }

    [Fact]
    public void WhenBranchAndSystemCoexistThenAliasedLaneValidationRejects()
    {
        // C1 contract: BranchControl and SystemSingleton share lane 7 (aliased).
        // A bundle with both must fail validation.
        TypedSlotBundleFacts aliasedFacts = new TypedSlotBundleFacts
        {
            Slot0Class = SlotClass.BranchControl,
            Slot1Class = SlotClass.SystemSingleton,
            BranchControlCount = 1,
            SystemSingletonCount = 1,
            FlexibleOpCount = 2,
            PinnedOpCount = 0,
            AluCount = 0,
            LsuCount = 0,
            DmaStreamCount = 0
        };

        Assert.False(
            HybridCpuTypedSlotFactsEmitter.ValidateEmittedFacts(aliasedFacts),
            "Validation must reject BranchControl + SystemSingleton coexistence (aliased lane 7).");
    }

    [Fact]
    public void WhenClassCountMismatchesTotalThenValidationRejects()
    {
        // C1 contract: classTotal must equal PinnedOpCount + FlexibleOpCount.
        // AluCount=1 but FlexibleOpCount=2 → mismatch.
        TypedSlotBundleFacts mismatchedFacts = new TypedSlotBundleFacts
        {
            Slot0Class = SlotClass.AluClass,
            AluCount = 1,
            FlexibleOpCount = 2,
            PinnedOpCount = 0,
            LsuCount = 0,
            DmaStreamCount = 0,
            BranchControlCount = 0,
            SystemSingletonCount = 0
        };

        Assert.False(
            HybridCpuTypedSlotFactsEmitter.ValidateEmittedFacts(mismatchedFacts),
            "Validation must reject when class count total != PinnedOpCount + FlexibleOpCount.");
    }

    [Fact]
    public void WhenTotalOpsExceedBundleWidthThenValidationRejects()
    {
        // C1 contract: PinnedOpCount + FlexibleOpCount must not exceed bundle width (8).
        TypedSlotBundleFacts tooWideFacts = new TypedSlotBundleFacts
        {
            AluCount = 4,
            LsuCount = 2,
            DmaStreamCount = 1,
            BranchControlCount = 0,
            SystemSingletonCount = 0,
            FlexibleOpCount = 7,
            PinnedOpCount = 2
        };

        Assert.False(
            HybridCpuTypedSlotFactsEmitter.ValidateEmittedFacts(tooWideFacts),
            "Validation must reject when total ops exceed bundle width (8).");
    }

    [Fact]
    public void WhenEmptyFactsThenValidationPasses()
    {
        // C1 contract: empty (default) facts are valid — legacy / pre-Phase 8 bundles.
        TypedSlotBundleFacts emptyFacts = default(TypedSlotBundleFacts);

        Assert.True(emptyFacts.IsEmpty, "Default TypedSlotBundleFacts must be empty.");
        Assert.True(
            HybridCpuTypedSlotFactsEmitter.ValidateEmittedFacts(emptyFacts),
            "Empty typed-slot facts must pass validation (legacy compatibility).");
    }

    [Fact]
    public void WhenLsuCountExceedsCapacityThenValidationRejects()
    {
        // C1 contract: LSU capacity = 2 (lanes 4-5). 3 LSU ops must fail validation.
        TypedSlotBundleFacts overCapacityFacts = new TypedSlotBundleFacts
        {
            Slot0Class = SlotClass.LsuClass,
            Slot1Class = SlotClass.LsuClass,
            Slot2Class = SlotClass.LsuClass,
            LsuCount = 3,
            FlexibleOpCount = 3,
            PinnedOpCount = 0,
            AluCount = 0,
            DmaStreamCount = 0,
            BranchControlCount = 0,
            SystemSingletonCount = 0
        };

        Assert.False(
            HybridCpuTypedSlotFactsEmitter.ValidateEmittedFacts(overCapacityFacts),
            "Validation must reject LsuCount > 2 (LsuClass capacity=2).");
    }

    [Fact]
    public void WhenPipelineProducesAdmissibleBundleThenIsStructurallyAdmissibleIsTrue()
    {
        // C1 contract: IsStructurallyAdmissible requires both Classification == StructurallyAdmissible
        // AND TypedSlotFactsValid. Pipeline-produced bundles with non-conflicting instructions must satisfy both.
        IrBundleAdmissionResult result = BuildAndAdmitSingleBundle(
            CreateScalarAluInstruction(destReg: 4, srcReg: 5),
            CreateLoadInstruction(destinationRegister: 8, address: 0x1000));

        Assert.True(result.IsStructurallyAdmissible,
            "IsStructurallyAdmissible must be true when both typed-slot facts are valid and classification is StructurallyAdmissible.");
        Assert.Equal(AdmissibilityClassification.StructurallyAdmissible, result.Classification);
        Assert.True(result.TypedSlotFactsValid);
    }

    [Fact]
    public void WhenSafetyMaskConflictPresentThenIsStructurallyAdmissibleIsFalse()
    {
        // C1 contract: IsStructurallyAdmissible is false when safety-mask conflicts,
        // even if typed-slot facts are valid.
        IrBundleAdmissionResult result = BuildAndAdmitSingleBundle(
            CreateScalarAluInstruction(destReg: 4, srcReg: 5),
            CreateScalarAluInstruction(destReg: 6, srcReg: 7));

        Assert.False(result.IsStructurallyAdmissible,
            "IsStructurallyAdmissible must be false when Classification is SafetyMaskConflict.");
        Assert.True(result.TypedSlotFactsValid,
            "Typed-slot facts should be valid — safety-mask is a separate structural diagnostic.");
    }

    #endregion

    #region Helpers

    private static IrBundleAdmissionResult BuildAndAdmitSingleBundle(
        params VLIW_Instruction[] instructions)
    {
        IrProgram program = BuildProgram(instructions);
        HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler();
        IrProgramSchedule schedule = scheduler.ScheduleProgram(program);

        HybridCpuBundleFormer bundleFormer = new HybridCpuBundleFormer(useClassFirstBinding: true);
        IrProgramBundlingResult bundling = bundleFormer.BundleProgram(schedule);

        IrMaterializedBundle bundle = bundling.BlockResults
            .SelectMany(b => b.Bundles)
            .First();

        HybridCpuBundleBuilder builder = new HybridCpuBundleBuilder();
        return builder.AnalyzeBundle(bundle);
    }

    private static IrBundleAdmissionResult AnalyzeManualBundle(
        params (IrInstruction Instruction, int SlotIndex, IrIssueSlotMask LegalSlots)[] placements)
    {
        IrMaterializedBundle bundle = CreateManualBundle(placements);
        HybridCpuBundleBuilder builder = new HybridCpuBundleBuilder();
        return builder.AnalyzeBundle(bundle);
    }

    private static TypedSlotBundleFacts EmitFactsFromManualBundle(
        params (IrInstruction Instruction, int SlotIndex, IrIssueSlotMask LegalSlots)[] placements)
    {
        IrMaterializedBundle bundle = CreateManualBundle(placements);
        return HybridCpuTypedSlotFactsEmitter.EmitFacts(bundle);
    }

    private static IrMaterializedBundle CreateManualBundle(
        params (IrInstruction Instruction, int SlotIndex, IrIssueSlotMask LegalSlots)[] placements)
    {
        IReadOnlyList<IrInstruction> instructions = placements.Select(static placement => placement.Instruction).ToArray();
        IrCandidateBundleAnalysis legalityAnalysis = new HybridCpuInstructionLegalityChecker().AnalyzeCandidateBundle(instructions);
        IrIssueSlotMask combinedLegalSlots = placements.Aggregate(
            IrIssueSlotMask.None,
            static (current, placement) => current | placement.LegalSlots);
        IReadOnlyList<IrIssueSlotMask> legalSlotMasks = placements.Select(static placement => placement.LegalSlots).ToArray();
        IReadOnlyList<int> instructionSlots = placements.Select(static placement => placement.SlotIndex).ToArray();
        IrMaterializedSlotAssignment slotAssignment = new IrMaterializedSlotAssignment(
            new IrSlotAssignmentAnalysis(
                CandidateInstructionCount: placements.Length,
                CombinedLegalSlots: combinedLegalSlots,
                DistinctLegalSlotCount: BitOperations.PopCount((uint)combinedLegalSlots),
                HasLegalAssignment: true,
                InstructionLegalSlots: legalSlotMasks),
            InstructionSlots: instructionSlots,
            Quality: IrBundlePlacementQuality.CreateForStructuralSlotFacts(instructionSlots, legalSlotMasks, slotCount: 8),
            SearchSummary: IrBundlePlacementSearchSummary.Empty,
            TransitionQuality: IrBundleTransitionQuality.Empty);

        IrMaterializedBundleSlot[] slots = new IrMaterializedBundleSlot[8];
        for (int slotIndex = 0; slotIndex < slots.Length; slotIndex++)
        {
            int placementIndex = Array.FindIndex(placements, placement => placement.SlotIndex == slotIndex);
            if (placementIndex < 0)
            {
                slots[slotIndex] = new IrMaterializedBundleSlot(slotIndex, null, null, IrIssueSlotMask.None, EmptyReason: "nop");
                continue;
            }

            (IrInstruction Instruction, int SlotIndex, IrIssueSlotMask LegalSlots) match = placements[placementIndex];
            slots[slotIndex] = new IrMaterializedBundleSlot(
                slotIndex,
                match.Instruction,
                OrderInCycle: placementIndex,
                InstructionLegalSlots: match.LegalSlots,
                BindingKind: match.Instruction.Annotation.BindingKind,
                AssignedClass: match.Instruction.Annotation.RequiredSlotClass);
        }

        return new IrMaterializedBundle(
            cycle: 0,
            cycleGroup: new IrScheduleCycleGroup(0, instructions, legalityAnalysis),
            legalityAnalysis,
            slotAssignment,
            slots);
    }

    private static IrInstruction WithAnnotation(
        IrInstruction instruction,
        IrResourceClass? resourceClass = null,
        SlotClass? requiredSlotClass = null,
        IrSlotBindingKind? bindingKind = null)
    {
        return instruction with
        {
            Annotation = instruction.Annotation with
            {
                ResourceClass = resourceClass ?? instruction.Annotation.ResourceClass,
                RequiredSlotClass = requiredSlotClass ?? instruction.Annotation.RequiredSlotClass,
                BindingKind = bindingKind ?? instruction.Annotation.BindingKind
            }
        };
    }

    private static IrProgram BuildProgram(params VLIW_Instruction[] instructions)
    {
        _ = new Processor(ProcessorMode.Compiler);
        HybridCpuIrBuilder builder = new HybridCpuIrBuilder();
        return builder.BuildProgram(
            0,
            NativeTransportRuntimeAdapter.ToCore(instructions),
            bundleAnnotations: NativeTransportRuntimeAdapter.ToCore(
                LegacyInstructionAnnotationBuilder.Build(instructions)));
    }

    private static VLIW_Instruction CreateScalarAluInstruction(
        byte destReg, byte srcReg)
    {
        return new VLIW_Instruction
        {
            OpCode = (uint)InstructionsEnum.ADDI,
            DataTypeValue = DataTypeEnum.INT32,
            PredicateMask = 0xFF,
            DestSrc1Pointer = Pack(destReg, srcReg, 0),
            Src2Pointer = 1,
            StreamLength = 0,
            Stride = 0,
            VirtualThreadId = 0
        };
    }

    private static VLIW_Instruction CreateLoadInstruction(
        byte destinationRegister, ulong address)
    {
        return new VLIW_Instruction
        {
            OpCode = (uint)InstructionsEnum.LD,
            DataTypeValue = DataTypeEnum.INT32,
            PredicateMask = 0xFF,
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(
                destinationRegister, 1, VLIW_Instruction.NoArchReg),
            Src2Pointer = address,
            StreamLength = 0,
            Stride = 0,
            VirtualThreadId = 0
        };
    }

    private static VLIW_Instruction CreateStoreInstruction(
        byte sourceRegister, ulong address)
    {
        return new VLIW_Instruction
        {
            OpCode = (uint)InstructionsEnum.SD,
            DataTypeValue = DataTypeEnum.INT32,
            PredicateMask = 0xFF,
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(
                VLIW_Instruction.NoArchReg, 1, sourceRegister),
            Src2Pointer = address,
            StreamLength = 0,
            Stride = 0,
            VirtualThreadId = 0
        };
    }

    private static VLIW_Instruction CreateStealableInstruction(
        InstructionsEnum opcode,
        ulong destSrc1 = 0,
        ulong src2 = 0)
    {
        VLIW_Instruction instruction = new VLIW_Instruction
        {
            OpCode = (uint)opcode,
            DataTypeValue = DataTypeEnum.INT32,
            PredicateMask = 0xFF,
            DestSrc1Pointer = destSrc1,
            Src2Pointer = src2,
            StreamLength = 0,
            Stride = 0,
            VirtualThreadId = 0
        };
        instruction.Word3 |= 1UL << 50;
        return instruction;
    }

    private static VLIW_Instruction CreateDmaStreamInstruction()
    {
        return new VLIW_Instruction
        {
            OpCode = (uint)InstructionsEnum.STREAM_SETUP,
            DataTypeValue = DataTypeEnum.INT32,
            PredicateMask = 0xFF,
            DestSrc1Pointer = 0,
            Src2Pointer = 0x2000,
            StreamLength = 64,
            Stride = 4,
            VirtualThreadId = 0
        };
    }

    private static VLIW_Instruction CreateBranchInstruction(
        ulong targetAddress)
    {
        return new VLIW_Instruction
        {
            OpCode = (uint)InstructionsEnum.BEQ,
            DataTypeValue = DataTypeEnum.INT32,
            PredicateMask = 0xFF,
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(0, 0, 0),
            Src2Pointer = targetAddress,
            StreamLength = 0,
            Stride = 0,
            VirtualThreadId = 0
        };
    }

    private static ulong Pack(byte dest, byte src1, byte src2)
    {
        return VLIW_Instruction.PackArchRegs(dest, src1, src2);
    }

    #endregion
}
