using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR;

public enum CompilerVectorHelperClosedAbiClass : byte
{
    IndexedMemory = 0,
    MaskPrefix = 1,
    ZeroExtend = 2,
    PrefixScan = 3,
    SaturatingArithmetic = 4,
    MovementPermutation = 5,
    DotProductWide = 6
}

/// <summary>
/// Compiler-visible contract for runtime-executable vector rows whose typed helpers remain closed.
/// </summary>
public sealed class CompilerVectorHelperClosedAbiContract
{
    private static readonly string[] CommonRequiredPolicyDecisions =
    [
        "VectorHelperAbi",
        "OperandPayloadConstructionAbi",
        "CapabilityGatingAbi",
        "RuntimeOpcodeEvidenceIsNotHelperAuthority",
        "VectorLegalityMatrixIsFinalRuntimeAuthority",
        "RetireReplayGoldenHelperEvidence",
        "NoRawVectorTransportPromotion",
        "NoTypedFacadeHelperEmission",
        "NoHiddenScalarLowering",
        "NoLane6Fallback",
        "NoLane7Fallback",
        "NoExternalBackendFallback"
    ];

    private static readonly string[] IndexedMemoryRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "IndexedMemoryHelperAbi",
        "IndexedDescriptorPayloadAbi",
        "FaultReplayPublicationPolicy",
        "NoScalarMemoryFallback",
        "NoLane6DmaFallback",
        "NoDsc2Fallback"
    ];

    private static readonly string[] MaskPrefixRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "MaskPrefixHelperAbi",
        "PredicateOnlyDestinationPolicy",
        "TailMaskBehaviorPolicy",
        "NoVmsifVmsofAlias",
        "NoVectorSelectMergeAlias"
    ];

    private static readonly string[] ZeroExtendRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "VectorSourceWidthHelperAbi",
        "UnsignedExtensionPolicy",
        "NoVsextAlias",
        "NoWidenNarrowConvertAlias"
    ];

    private static readonly string[] PrefixScanRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "PrefixScanHelperAbi",
        "InclusiveExclusivePolicy",
        "ActiveVlTailBehaviorPolicy",
        "ReplayStablePrefixPublication",
        "NoScanMinMaxAlias"
    ];

    private static readonly string[] SaturatingArithmeticRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "SaturatingAddHelperAbi",
        "SignednessWidthClampPolicy",
        "NoSaturatingSubMulShiftAlias",
        "NoAverageClipAlias"
    ];

    private static readonly string[] MovementPermutationRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "MovementPermutationHelperAbi",
        "PayloadCanonicalization",
        "FixedLaneShapePolicy",
        "NoStructureMovementAlias",
        "NoDescriptorShapeFallback"
    ];

    private static readonly string[] DotProductWideRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "DotTileHelperAbi",
        "AccumulatorResultFootprintAbi",
        "DeterministicOrderingReplayPolicy",
        "NoBlockscaleAccumI16I32Alias",
        "NoLane6DescriptorFallback"
    ];

    private CompilerVectorHelperClosedAbiContract(
        string mnemonic,
        string runtimeOpcodeCandidate,
        CompilerVectorHelperClosedAbiClass abiClass,
        string extensionName,
        string evidenceBoundary,
        string abiDecision,
        string operandShape,
        string dataSemantics,
        string resultSemantics,
        IReadOnlyList<string> requiredPolicyDecisions,
        bool isIndexedMemory = false,
        bool isGather = false,
        bool isScatter = false,
        bool requiresIndexedMemoryHelperAbi = false,
        bool requiresIndexedDescriptorPayloadAbi = false,
        bool requiresFaultReplayPublicationPolicy = false,
        bool isMaskPrefix = false,
        bool requiresMaskPrefixHelperAbi = false,
        bool requiresPredicateOnlyDestinationPolicy = false,
        bool requiresTailMaskBehaviorPolicy = false,
        bool rejectsVmsifVmsofAlias = false,
        bool rejectsVectorSelectMergeAlias = false,
        bool isZeroExtend = false,
        bool requiresVectorSourceWidthHelperAbi = false,
        bool requiresUnsignedExtensionPolicy = false,
        bool rejectsVsextAlias = false,
        bool rejectsWidenNarrowConvertAlias = false,
        bool isPrefixScan = false,
        bool requiresPrefixScanHelperAbi = false,
        bool requiresInclusiveExclusivePolicy = false,
        bool requiresActiveVlTailBehaviorPolicy = false,
        bool requiresReplayStablePrefixPublication = false,
        bool rejectsScanMinMaxAlias = false,
        bool isSaturatingArithmetic = false,
        bool requiresSaturatingAddHelperAbi = false,
        bool requiresSignednessWidthClampPolicy = false,
        bool rejectsSaturatingSubMulShiftAlias = false,
        bool rejectsAverageClipAlias = false,
        bool isMovementPermutation = false,
        bool requiresMovementPermutationHelperAbi = false,
        bool requiresPayloadCanonicalization = false,
        bool requiresFixedLaneShapePolicy = false,
        bool requiresSlideOneDeltaPolicy = false,
        bool requiresPermute2ControlAbi = false,
        bool requiresTransposeShapeAbi = false,
        bool rejectsStructureMovementAlias = false,
        bool rejectsDescriptorShapeFallback = false,
        bool isDotProductWide = false,
        bool requiresDotTileHelperAbi = false,
        bool requiresAccumulatorResultFootprintAbi = false,
        bool requiresDeterministicOrderingReplayPolicy = false,
        bool rejectsBlockscaleAccumI16I32Alias = false,
        bool noScalarMemoryFallback = false,
        bool noLane6DmaFallback = false,
        bool noLane6DescriptorFallback = false,
        bool noDsc2Fallback = false)
    {
        Mnemonic = mnemonic;
        RuntimeOpcodeCandidate = runtimeOpcodeCandidate;
        AbiClass = abiClass;
        ExtensionName = extensionName;
        EvidenceBoundary = evidenceBoundary;
        AbiDecision = abiDecision;
        OperandShape = operandShape;
        DataSemantics = dataSemantics;
        ResultSemantics = resultSemantics;
        RequiredPolicyDecisions = requiredPolicyDecisions;
        IsIndexedMemory = isIndexedMemory;
        IsGather = isGather;
        IsScatter = isScatter;
        RequiresIndexedMemoryHelperAbi = requiresIndexedMemoryHelperAbi;
        RequiresIndexedDescriptorPayloadAbi = requiresIndexedDescriptorPayloadAbi;
        RequiresFaultReplayPublicationPolicy = requiresFaultReplayPublicationPolicy;
        IsMaskPrefix = isMaskPrefix;
        RequiresMaskPrefixHelperAbi = requiresMaskPrefixHelperAbi;
        RequiresPredicateOnlyDestinationPolicy = requiresPredicateOnlyDestinationPolicy;
        RequiresTailMaskBehaviorPolicy = requiresTailMaskBehaviorPolicy;
        RejectsVmsifVmsofAlias = rejectsVmsifVmsofAlias;
        RejectsVectorSelectMergeAlias = rejectsVectorSelectMergeAlias;
        IsZeroExtend = isZeroExtend;
        RequiresVectorSourceWidthHelperAbi = requiresVectorSourceWidthHelperAbi;
        RequiresUnsignedExtensionPolicy = requiresUnsignedExtensionPolicy;
        RejectsVsextAlias = rejectsVsextAlias;
        RejectsWidenNarrowConvertAlias = rejectsWidenNarrowConvertAlias;
        IsPrefixScan = isPrefixScan;
        RequiresPrefixScanHelperAbi = requiresPrefixScanHelperAbi;
        RequiresInclusiveExclusivePolicy = requiresInclusiveExclusivePolicy;
        RequiresActiveVlTailBehaviorPolicy = requiresActiveVlTailBehaviorPolicy;
        RequiresReplayStablePrefixPublication = requiresReplayStablePrefixPublication;
        RejectsScanMinMaxAlias = rejectsScanMinMaxAlias;
        IsSaturatingArithmetic = isSaturatingArithmetic;
        RequiresSaturatingAddHelperAbi = requiresSaturatingAddHelperAbi;
        RequiresSignednessWidthClampPolicy = requiresSignednessWidthClampPolicy;
        RejectsSaturatingSubMulShiftAlias = rejectsSaturatingSubMulShiftAlias;
        RejectsAverageClipAlias = rejectsAverageClipAlias;
        IsMovementPermutation = isMovementPermutation;
        RequiresMovementPermutationHelperAbi = requiresMovementPermutationHelperAbi;
        RequiresPayloadCanonicalization = requiresPayloadCanonicalization;
        RequiresFixedLaneShapePolicy = requiresFixedLaneShapePolicy;
        RequiresSlideOneDeltaPolicy = requiresSlideOneDeltaPolicy;
        RequiresPermute2ControlAbi = requiresPermute2ControlAbi;
        RequiresTransposeShapeAbi = requiresTransposeShapeAbi;
        RejectsStructureMovementAlias = rejectsStructureMovementAlias;
        RejectsDescriptorShapeFallback = rejectsDescriptorShapeFallback;
        IsDotProductWide = isDotProductWide;
        RequiresDotTileHelperAbi = requiresDotTileHelperAbi;
        RequiresAccumulatorResultFootprintAbi = requiresAccumulatorResultFootprintAbi;
        RequiresDeterministicOrderingReplayPolicy = requiresDeterministicOrderingReplayPolicy;
        RejectsBlockscaleAccumI16I32Alias = rejectsBlockscaleAccumI16I32Alias;
        NoScalarMemoryFallback = noScalarMemoryFallback;
        NoLane6DmaFallback = noLane6DmaFallback;
        NoLane6DescriptorFallback = noLane6DescriptorFallback;
        NoDsc2Fallback = noDsc2Fallback;
    }

    public static CompilerVectorHelperClosedAbiContract GatherIndexed { get; } =
        CreateIndexedMemoryRow(
            "VGATHER",
            "VGATHER",
            "NoAllocationUntilIndexedGatherHelperAbiFaultReplayCapabilityNoFallback",
            "indexed read descriptor plus destination vector payload",
            "Runtime executable indexed-read evidence is not a typed compiler gather helper ABI.",
            "Future helper emission requires canonical indexed payload construction, fault/replay policy, and no scalar or Lane6 fallback.",
            isGather: true);

    public static CompilerVectorHelperClosedAbiContract ScatterIndexed { get; } =
        CreateIndexedMemoryRow(
            "VSCATTER",
            "VSCATTER",
            "NoAllocationUntilIndexedScatterHelperAbiFaultReplayCapabilityNoFallback",
            "indexed write descriptor plus source vector payload",
            "Runtime executable indexed-write evidence is not a typed compiler scatter helper ABI.",
            "Future helper emission requires canonical indexed payload construction, fault/replay policy, and no scalar or Lane6 fallback.",
            isScatter: true);

    public static CompilerVectorHelperClosedAbiContract MaskSetBeforeFirst { get; } =
        new(
            "VMSBF",
            "VMSBF",
            CompilerVectorHelperClosedAbiClass.MaskPrefix,
            "VectorMaskPrefixPublication",
            "RuntimeExecutableHelperClosed",
            "NoAllocationUntilMaskPrefixHelperAbiPredicateTailNoAlias",
            "predicate-mask prefix payload; no typed facade helper operands",
            "Runtime executable mask-prefix evidence does not authorize a typed compiler helper.",
            "Future helper emission requires predicate-only destination, tail/mask behavior, and no VMSIF/VMSOF or select/merge aliasing.",
            MaskPrefixRequiredPolicyDecisions,
            isMaskPrefix: true,
            requiresMaskPrefixHelperAbi: true,
            requiresPredicateOnlyDestinationPolicy: true,
            requiresTailMaskBehaviorPolicy: true,
            rejectsVmsifVmsofAlias: true,
            rejectsVectorSelectMergeAlias: true);

    public static CompilerVectorHelperClosedAbiContract ZeroExtend { get; } =
        new(
            "VZEXT",
            "VZEXT",
            CompilerVectorHelperClosedAbiClass.ZeroExtend,
            "VectorZeroExtendPublication",
            "RuntimeExecutableHelperClosed",
            "NoAllocationUntilVectorSourceWidthUnsignedExtensionHelperAbiNoAlias",
            "packed source-width payload; no typed facade helper operands",
            "Runtime executable zero-extension evidence does not authorize widen, narrow, signed, or convert helpers.",
            "Future helper emission requires source-width construction and unsigned extension policy.",
            ZeroExtendRequiredPolicyDecisions,
            isZeroExtend: true,
            requiresVectorSourceWidthHelperAbi: true,
            requiresUnsignedExtensionPolicy: true,
            rejectsVsextAlias: true,
            rejectsWidenNarrowConvertAlias: true);

    public static CompilerVectorHelperClosedAbiContract ScanSum { get; } =
        new(
            "VSCAN.SUM",
            "VSCAN_SUM",
            CompilerVectorHelperClosedAbiClass.PrefixScan,
            "VectorScanPrefixPublication",
            "RuntimeExecutableHelperClosed",
            "NoAllocationUntilPrefixScanHelperAbiInclusiveActiveVlReplayNoAlias",
            "prefix-sum payload; no typed facade helper operands",
            "Runtime executable prefix-sum evidence does not authorize a typed scan helper.",
            "Future helper emission requires inclusive/exclusive decision, active VL/tail behavior, and replay-stable publication.",
            PrefixScanRequiredPolicyDecisions,
            isPrefixScan: true,
            requiresPrefixScanHelperAbi: true,
            requiresInclusiveExclusivePolicy: true,
            requiresActiveVlTailBehaviorPolicy: true,
            requiresReplayStablePrefixPublication: true,
            rejectsScanMinMaxAlias: true);

    public static CompilerVectorHelperClosedAbiContract SaturatingAdd { get; } =
        new(
            "VADD.SAT",
            "VADD",
            CompilerVectorHelperClosedAbiClass.SaturatingArithmetic,
            "VectorSaturatingAddPolicy",
            "RuntimeExecutableHelperClosed",
            "NoAllocationUntilSaturatingAddHelperAbiSignednessWidthClampNoAlias",
            "saturating-add policy payload; no typed facade helper operands",
            "Runtime executable saturating-add evidence does not authorize other saturating arithmetic helpers.",
            "Future helper emission requires signedness, width, clamp, and explicit separation from reserved saturating rows.",
            SaturatingArithmeticRequiredPolicyDecisions,
            isSaturatingArithmetic: true,
            requiresSaturatingAddHelperAbi: true,
            requiresSignednessWidthClampPolicy: true,
            rejectsSaturatingSubMulShiftAlias: true,
            rejectsAverageClipAlias: true);

    public static CompilerVectorHelperClosedAbiContract SlideOneUp { get; } =
        CreateMovementPermutationRow(
            "VSLIDE1UP",
            "VSLIDE1UP",
            "NoAllocationUntilSlideOneUpHelperAbiPayloadCanonicalization",
            "single-surface fixed-one-lane slide-up payload",
            "Runtime executable slide-up evidence does not authorize a typed compiler slide helper.",
            "Future helper emission requires fixed-lane payload canonicalization and no structure or descriptor fallback.",
            requiresSlideOneDeltaPolicy: true);

    public static CompilerVectorHelperClosedAbiContract SlideOneDown { get; } =
        CreateMovementPermutationRow(
            "VSLIDE1DOWN",
            "VSLIDE1DOWN",
            "NoAllocationUntilSlideOneDownHelperAbiPayloadCanonicalization",
            "single-surface fixed-one-lane slide-down payload",
            "Runtime executable slide-down evidence does not authorize a typed compiler slide helper.",
            "Future helper emission requires fixed-lane payload canonicalization and no structure or descriptor fallback.",
            requiresSlideOneDeltaPolicy: true);

    public static CompilerVectorHelperClosedAbiContract Permute2 { get; } =
        CreateMovementPermutationRow(
            "VPERM2",
            "VPERM2",
            "NoAllocationUntilPermute2HelperAbiControlPayloadCanonicalization",
            "two-source two-lane immediate-controlled payload",
            "Runtime executable two-source permutation evidence does not authorize a typed compiler permutation helper.",
            "Future helper emission requires control payload canonicalization and no structure or descriptor fallback.",
            requiresPermute2ControlAbi: true);

    public static CompilerVectorHelperClosedAbiContract Transpose { get; } =
        CreateMovementPermutationRow(
            "VTRANSPOSE",
            "VTRANSPOSE",
            "NoAllocationUntilTransposeHelperAbiShapePayloadCanonicalization",
            "single-surface fixed 2x2 transpose payload",
            "Runtime executable transpose evidence does not authorize a typed compiler transpose helper.",
            "Future helper emission requires shape payload canonicalization and no structure or descriptor fallback.",
            requiresTransposeShapeAbi: true);

    public static CompilerVectorHelperClosedAbiContract DotProductWide { get; } =
        new(
            "VDOT.WIDE",
            "VDOT_WIDE",
            CompilerVectorHelperClosedAbiClass.DotProductWide,
            "VectorDotProductWideScalarFootprint",
            "RuntimeExecutableHelperClosed",
            "NoAllocationUntilDotTileHelperAbiAccumulatorFootprintDeterministicReplayNoAlias",
            "packed scalar-footprint dot payload; no typed facade helper operands",
            "Runtime executable wide-dot evidence does not authorize blockscale, accum, I16, I32, or separate-destination helpers.",
            "Future helper emission requires dot/tile ABI, accumulator/result footprint, deterministic ordering, and replay policy.",
            DotProductWideRequiredPolicyDecisions,
            isDotProductWide: true,
            requiresDotTileHelperAbi: true,
            requiresAccumulatorResultFootprintAbi: true,
            requiresDeterministicOrderingReplayPolicy: true,
            rejectsBlockscaleAccumI16I32Alias: true,
            noLane6DescriptorFallback: true);

    public static IReadOnlyList<CompilerVectorHelperClosedAbiContract> AllHelperClosedVectorRows { get; } =
    [
        GatherIndexed,
        ScatterIndexed,
        MaskSetBeforeFirst,
        ZeroExtend,
        ScanSum,
        SaturatingAdd,
        SlideOneUp,
        SlideOneDown,
        Permute2,
        Transpose,
        DotProductWide
    ];

    private static CompilerVectorHelperClosedAbiContract CreateIndexedMemoryRow(
        string mnemonic,
        string runtimeOpcodeCandidate,
        string abiDecision,
        string operandShape,
        string dataSemantics,
        string resultSemantics,
        bool isGather = false,
        bool isScatter = false) =>
        new(
            mnemonic,
            runtimeOpcodeCandidate,
            CompilerVectorHelperClosedAbiClass.IndexedMemory,
            "VectorIndexedMemory",
            "RuntimeExecutableHelperClosed",
            abiDecision,
            operandShape,
            dataSemantics,
            resultSemantics,
            IndexedMemoryRequiredPolicyDecisions,
            isIndexedMemory: true,
            isGather: isGather,
            isScatter: isScatter,
            requiresIndexedMemoryHelperAbi: true,
            requiresIndexedDescriptorPayloadAbi: true,
            requiresFaultReplayPublicationPolicy: true,
            noScalarMemoryFallback: true,
            noLane6DmaFallback: true,
            noDsc2Fallback: true);

    private static CompilerVectorHelperClosedAbiContract CreateMovementPermutationRow(
        string mnemonic,
        string runtimeOpcodeCandidate,
        string abiDecision,
        string operandShape,
        string dataSemantics,
        string resultSemantics,
        bool requiresSlideOneDeltaPolicy = false,
        bool requiresPermute2ControlAbi = false,
        bool requiresTransposeShapeAbi = false) =>
        new(
            mnemonic,
            runtimeOpcodeCandidate,
            CompilerVectorHelperClosedAbiClass.MovementPermutation,
            "VectorMovementPermutation",
            "RuntimeExecutableHelperClosed",
            abiDecision,
            operandShape,
            dataSemantics,
            resultSemantics,
            MovementPermutationRequiredPolicyDecisions,
            isMovementPermutation: true,
            requiresMovementPermutationHelperAbi: true,
            requiresPayloadCanonicalization: true,
            requiresFixedLaneShapePolicy: true,
            requiresSlideOneDeltaPolicy: requiresSlideOneDeltaPolicy,
            requiresPermute2ControlAbi: requiresPermute2ControlAbi,
            requiresTransposeShapeAbi: requiresTransposeShapeAbi,
            rejectsStructureMovementAlias: true,
            rejectsDescriptorShapeFallback: true);

    public string Mnemonic { get; }
    public string RuntimeOpcodeCandidate { get; }
    public CompilerVectorHelperClosedAbiClass AbiClass { get; }
    public string ExtensionName { get; }
    public string EvidenceBoundary { get; }
    public string AbiDecision { get; }
    public string OperandShape { get; }
    public string DataSemantics { get; }
    public string ResultSemantics { get; }
    public IReadOnlyList<string> RequiredPolicyDecisions { get; }
    public bool IsIndexedMemory { get; }
    public bool IsGather { get; }
    public bool IsScatter { get; }
    public bool RequiresIndexedMemoryHelperAbi { get; }
    public bool RequiresIndexedDescriptorPayloadAbi { get; }
    public bool RequiresFaultReplayPublicationPolicy { get; }
    public bool IsMaskPrefix { get; }
    public bool RequiresMaskPrefixHelperAbi { get; }
    public bool RequiresPredicateOnlyDestinationPolicy { get; }
    public bool RequiresTailMaskBehaviorPolicy { get; }
    public bool RejectsVmsifVmsofAlias { get; }
    public bool RejectsVectorSelectMergeAlias { get; }
    public bool IsZeroExtend { get; }
    public bool RequiresVectorSourceWidthHelperAbi { get; }
    public bool RequiresUnsignedExtensionPolicy { get; }
    public bool RejectsVsextAlias { get; }
    public bool RejectsWidenNarrowConvertAlias { get; }
    public bool IsPrefixScan { get; }
    public bool RequiresPrefixScanHelperAbi { get; }
    public bool RequiresInclusiveExclusivePolicy { get; }
    public bool RequiresActiveVlTailBehaviorPolicy { get; }
    public bool RequiresReplayStablePrefixPublication { get; }
    public bool RejectsScanMinMaxAlias { get; }
    public bool IsSaturatingArithmetic { get; }
    public bool RequiresSaturatingAddHelperAbi { get; }
    public bool RequiresSignednessWidthClampPolicy { get; }
    public bool RejectsSaturatingSubMulShiftAlias { get; }
    public bool RejectsAverageClipAlias { get; }
    public bool IsMovementPermutation { get; }
    public bool RequiresMovementPermutationHelperAbi { get; }
    public bool RequiresPayloadCanonicalization { get; }
    public bool RequiresFixedLaneShapePolicy { get; }
    public bool RequiresSlideOneDeltaPolicy { get; }
    public bool RequiresPermute2ControlAbi { get; }
    public bool RequiresTransposeShapeAbi { get; }
    public bool RejectsStructureMovementAlias { get; }
    public bool RejectsDescriptorShapeFallback { get; }
    public bool IsDotProductWide { get; }
    public bool RequiresDotTileHelperAbi { get; }
    public bool RequiresAccumulatorResultFootprintAbi { get; }
    public bool RequiresDeterministicOrderingReplayPolicy { get; }
    public bool RejectsBlockscaleAccumI16I32Alias { get; }
    public bool NoScalarMemoryFallback { get; }
    public bool NoLane6DmaFallback { get; }
    public bool NoLane6DescriptorFallback { get; }
    public bool NoDsc2Fallback { get; }
    public bool RuntimeExecutable => true;
    public bool HasRuntimeOpcodeAllocation => true;
    public bool HasRuntimeConformanceEvidence => true;
    public bool HasVectorLegalityMatrixEvidence => true;
    public bool RawVectorTransportAllowed => true;
    public bool RawVectorTransportIsHelperAuthority => false;
    public bool CompilerEmissionAllowed => false;
    public bool CompilerHelperAllowed => false;
    public bool TypedFacadeAllowed => false;
    public bool TypedHelperAllowed => false;
    public bool RequiresVectorHelperAbi => true;
    public bool RequiresOperandPayloadConstructionAbi => true;
    public bool RequiresCapabilityGatingAbi => true;
    public bool RequiresRetireReplayGoldenHelperEvidence => true;
    public bool RuntimeOwnedLegalityIsFinal => true;
    public bool NoRawVectorTransportPromotion => true;
    public bool NoTypedFacadeHelperEmission => true;
    public bool NoHiddenScalarLowering => true;
    public bool NoHiddenLane6Lowering => true;
    public bool NoLane7Fallback => true;
    public bool NoExternalBackendFallback => true;

    public void RequireCompilerHelperAuthority()
    {
        string requiredDecisions = AbiClass switch
        {
            CompilerVectorHelperClosedAbiClass.IndexedMemory =>
                "indexed-memory helper ABI, payload construction, fault/replay publication, capability gating, and no scalar/Lane6 fallback decisions",
            CompilerVectorHelperClosedAbiClass.MaskPrefix =>
                "mask-prefix helper ABI, predicate destination, tail/mask behavior, and no VMSIF/VMSOF or select/merge alias decisions",
            CompilerVectorHelperClosedAbiClass.ZeroExtend =>
                "source-width helper ABI, unsigned extension policy, and no VSEXT/widen/narrow/convert alias decisions",
            CompilerVectorHelperClosedAbiClass.PrefixScan =>
                "prefix-scan helper ABI, inclusive/exclusive policy, active VL/tail behavior, and replay decisions",
            CompilerVectorHelperClosedAbiClass.SaturatingArithmetic =>
                "saturating-add helper ABI, signedness/width/clamp policy, and no reserved saturating alias decisions",
            CompilerVectorHelperClosedAbiClass.MovementPermutation =>
                "movement/permutation helper ABI, payload canonicalization, fixed-lane shape, and no structure/descriptor fallback decisions",
            CompilerVectorHelperClosedAbiClass.DotProductWide =>
                "dot/tile helper ABI, accumulator/result footprint, deterministic ordering, replay, and no variant alias decisions",
            _ => "required vector helper ABI decisions"
        };

        throw new InvalidOperationException(
            $"{Mnemonic} typed compiler helper emission is blocked until {requiredDecisions} are explicit.");
    }
}
