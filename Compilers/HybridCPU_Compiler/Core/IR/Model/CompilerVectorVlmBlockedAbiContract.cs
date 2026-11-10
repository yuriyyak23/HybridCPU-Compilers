using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR;

public enum CompilerVectorVlmBlockedAbiClass : byte
{
    PredicateSelectMerge = 0,
    PredicateScalarSummary = 1,
    PredicateMaskPrefix = 2,
    SignedExtend = 3,
    WideningArithmetic = 4,
    WideningMultiplyAccumulate = 5,
    NarrowingShift = 6,
    Conversion = 7,
    PrefixScanMinMax = 8,
    StructureMovement = 9,
    SegmentMemory = 10,
    FixedPointSaturation = 11,
    FixedPointAverageClip = 12,
    DotTileVariant = 13
}

/// <summary>
/// Compiler-visible no-emission ABI audit for VLM/runtime-blocked vector rows.
/// </summary>
public sealed class CompilerVectorVlmBlockedAbiContract
{
    private static readonly string[] CommonRequiredPolicyDecisions =
    [
        "VectorHelperAbi",
        "VlmLegalityEvidence",
        "RuntimeMaterializerEvidence",
        "MaskTailPolicy",
        "ResultFootprintAbi",
        "RetireReplayGoldenConformance",
        "RuntimeOwnedLegalityIsFinal",
        "NoRawVectorTransportPromotion",
        "NoTypedFacadeHelperEmission",
        "NoHiddenScalarLowering",
        "NoLane6Fallback",
        "NoLane7Fallback",
        "NoExternalBackendFallback"
    ];

    private static readonly string[] PredicateSelectRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "PredicatePolarityPolicy",
        "SelectMergeResultAbi",
        "PredicateSidebandAbi",
        "NoCzeroAliasPromotion",
        "NoMaskPrefixAliasPromotion"
    ];

    private static readonly string[] PredicateScalarSummaryRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "ScalarPredicateSummaryFootprint",
        "NoActiveElementSentinelPolicy",
        "ActiveVlTailSemantics",
        "RetireOwnedScalarPublication",
        "NoScalarReductionAliasPromotion"
    ];

    private static readonly string[] PredicateMaskPrefixRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "PredicateOnlyDestinationPolicy",
        "IncludingFirstOnlyFirstPolicy",
        "StagedMaskPublication",
        "RollbackEvidence",
        "NoVmsbfAliasPromotion",
        "NoSelectMergeAliasPromotion"
    ];

    private static readonly string[] SignedExtendRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "VectorSourceWidthAbi",
        "SignedExtensionPolicy",
        "SignednessSeparationPolicy",
        "NoVzextAliasPromotion",
        "NoWidenNarrowConvertAliasPromotion"
    ];

    private static readonly string[] WideningArithmeticRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "SourceDestinationWidthSideband",
        "ElementWidthLmulVlAbi",
        "SignednessAbi",
        "WideningOverflowPolicyAbi",
        "SeparateFromBaseVectorArithmetic",
        "NoVzextVsextAliasPromotion",
        "NoBaseArithmeticAliasPromotion"
    ];

    private static readonly string[] WideningMultiplyAccumulateRequiredPolicyDecisions =
    [
        .. WideningArithmeticRequiredPolicyDecisions,
        "AccumulatorFootprintAbi",
        "AccumulatorPrecisionPolicy",
        "NoDotProductAliasPromotion"
    ];

    private static readonly string[] NarrowingShiftRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "SourceDestinationWidthSideband",
        "ElementWidthLmulVlAbi",
        "NarrowingPolicyAbi",
        "ShiftOperandAbi",
        "RoundingSaturationTrapPolicy",
        "TruncationPublicationPolicy",
        "NoClipAverageAliasPromotion",
        "NoBaseShiftAliasPromotion"
    ];

    private static readonly string[] ConversionRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "ConversionPolicyAbi",
        "ConversionTypeDomainAbi",
        "RoundingSaturationTrapPolicy",
        "NanPolicyAbi",
        "ConversionResultFootprintAbi",
        "ElementWidthLmulVlAbi",
        "NoVzextVsextAliasPromotion",
        "NoWidenNarrowArithmeticAliasPromotion",
        "NoScalarConversionAliasPromotion"
    ];

    private static readonly string[] PrefixScanMinMaxRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "PrefixScanPolicyAbi",
        "PrefixMinMaxOrderingPolicy",
        "InclusiveExclusivePolicy",
        "ActiveVlTailBehaviorPolicy",
        "ElementTypeSideband",
        "SignednessAbi",
        "ReplayDeterministicPrefixPublication",
        "NoVscanSumAliasPromotion",
        "NoReductionAliasPromotion",
        "NoSegmentMovementAliasPromotion"
    ];

    private static readonly string[] StructureMovementRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "StructureShapeAbi",
        "ShapeOrderingPolicy",
        "PayloadCanonicalization",
        "ElementOrderPolicy",
        "ActiveVlTailBehaviorPolicy",
        "StagedVectorPublication",
        "NoMovementPermutationAliasPromotion",
        "NoSegmentMemoryAliasPromotion",
        "NoHiddenStreamEngineFallback"
    ];

    private static readonly string[] SegmentMemoryRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "SegmentMemoryShapeAbi",
        "SegmentCountAbi",
        "FaultReplayPolicy",
        "ByteOrderingPolicy",
        "AlignmentFaultPolicy",
        "SegmentOrderingPolicy",
        "RetireStagedPublicationOrCommit",
        "NoBaseMemoryOpcodeDuplication",
        "NoStructureMovementAliasPromotion",
        "NoIndexedOr2DMemoryAliasPromotion"
    ];

    private static readonly string[] FixedPointSaturationRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "SaturatingPolicyAbi",
        "SignednessWidthClampPolicy",
        "ElementWidthLmulVlAbi",
        "OverflowPolicyAbi",
        "VlmMaterializationPolicy",
        "StagedPublicationRetirePolicy",
        "ReplayRollbackGoldenEvidence",
        "SeparateFromClosedVaddSat",
        "NoVaddSatFallback",
        "NoBaseVectorArithmeticFallback",
        "NoBaseVectorShiftFallback",
        "NoScalarHelperFallback",
        "NoLane6StreamFallback",
        "NoLane7AcceleratorFallback",
        "NoVmxSpecificPathFallback",
        "NoExecutableRowAliasPromotion",
        "NoSaturatingAddAliasPromotion",
        "NoAverageClipAliasPromotion",
        "NoBaseArithmeticOrShiftAliasPromotion"
    ];

    private static readonly string[] FixedPointAverageClipRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "ElementWidthLmulVlAbi",
        "SignednessAbi",
        "RoundingTruncationPolicyAbi",
        "OverflowPolicyAbi",
        "VlmMaterializationPolicy",
        "StagedPublicationRetirePolicy",
        "ReplayRollbackGoldenEvidence",
        "NoVaddSatFallback",
        "NoFixedPointSaturationFallback",
        "NoBaseVectorArithmeticFallback",
        "NoBaseVectorShiftFallback",
        "NoNarrowWidenConvertFallback",
        "NoScalarHelperFallback",
        "NoLane6StreamFallback",
        "NoLane7AcceleratorFallback",
        "NoVmxSpecificPathFallback",
        "NoExecutableRowAliasPromotion",
        "NoAverageClipAliasPromotion",
        "NoBaseArithmeticOrShiftAliasPromotion"
    ];

    private static readonly string[] DotTileVariantRequiredPolicyDecisions =
    [
        .. CommonRequiredPolicyDecisions,
        "DotVariantAbi",
        "DotTileHelperAbi",
        "AccumulatorPrecisionAbi",
        "AccumulatorResultFootprintAbi",
        "DeterministicOrderingReplayPolicy",
        "VlmMaterializationPolicy",
        "StagedPublicationRetirePolicy",
        "ReplayRollbackGoldenEvidence",
        "SeparateFromScopedVdotWide",
        "NoScopedVdotWideFallback",
        "NoNameOnlyVdotWideExtension",
        "NoBaseDotProductFallback",
        "NoWideningFmaFallback",
        "NoLane6DescriptorFallback",
        "NoMatrixTileFallback",
        "NoScalarHelperFallback",
        "NoLane7AcceleratorFallback",
        "NoVmxSpecificPathFallback",
        "NoExecutableRowAliasPromotion",
        "NoHostOwnedEvidencePublication"
    ];

    private CompilerVectorVlmBlockedAbiContract(
        string mnemonic,
        CompilerVectorVlmBlockedAbiClass abiClass,
        string extensionName,
        string evidenceBoundary,
        string abiDecision,
        string operandShape,
        string dataSemantics,
        string resultSemantics,
        IReadOnlyList<string> requiredPolicyDecisions,
        bool isPredicateSelectMerge = false,
        bool requiresPredicatePolarityPolicy = false,
        bool requiresSelectMergeResultAbi = false,
        bool requiresPredicateSidebandAbi = false,
        bool rejectsCzeroAliasPromotion = false,
        bool rejectsMaskPrefixAliasPromotion = false,
        bool isPredicateScalarSummary = false,
        bool requiresScalarPredicateSummaryFootprint = false,
        bool requiresNoActiveElementSentinelPolicy = false,
        bool requiresActiveVlTailSemantics = false,
        bool requiresRetireOwnedScalarPublication = false,
        bool rejectsScalarReductionAliasPromotion = false,
        bool isPredicateMaskPrefix = false,
        bool requiresPredicateOnlyDestinationPolicy = false,
        bool requiresIncludingFirstOnlyFirstPolicy = false,
        bool requiresStagedMaskPublication = false,
        bool requiresRollbackEvidence = false,
        bool rejectsVmsbfAliasPromotion = false,
        bool rejectsSelectMergeAliasPromotion = false,
        bool isSignedExtend = false,
        bool requiresVectorSourceWidthAbi = false,
        bool requiresSignedExtensionPolicy = false,
        bool requiresSignednessSeparationPolicy = false,
        bool rejectsVzextAliasPromotion = false,
        bool rejectsWidenNarrowConvertAliasPromotion = false,
        bool isWideningArithmetic = false,
        bool isWideningMultiplyAccumulate = false,
        bool requiresSourceDestinationWidthSideband = false,
        bool requiresElementWidthLmulVlAbi = false,
        bool requiresSignednessAbi = false,
        bool requiresWideningOverflowPolicyAbi = false,
        bool requiresAccumulatorFootprintAbi = false,
        bool requiresAccumulatorPrecisionPolicy = false,
        bool separateFromBaseVectorArithmetic = false,
        bool rejectsVzextVsextAliasPromotion = false,
        bool rejectsBaseArithmeticAliasPromotion = false,
        bool rejectsDotProductAliasPromotion = false,
        bool isNarrowingShift = false,
        bool requiresNarrowingPolicyAbi = false,
        bool requiresShiftOperandAbi = false,
        bool requiresRoundingSaturationTrapPolicy = false,
        bool requiresTruncationPublicationPolicy = false,
        bool rejectsClipAverageAliasPromotion = false,
        bool rejectsBaseShiftAliasPromotion = false,
        bool isConversion = false,
        bool requiresConversionPolicyAbi = false,
        bool requiresConversionTypeDomainAbi = false,
        bool requiresNanPolicyAbi = false,
        bool requiresConversionResultFootprintAbi = false,
        bool rejectsWidenNarrowArithmeticAliasPromotion = false,
        bool rejectsScalarConversionAliasPromotion = false,
        bool isPrefixScanMinMax = false,
        bool requiresPrefixScanPolicyAbi = false,
        bool requiresPrefixMinMaxOrderingPolicy = false,
        bool requiresInclusiveExclusivePolicy = false,
        bool requiresActiveVlTailBehaviorPolicy = false,
        bool requiresElementTypeSideband = false,
        bool requiresReplayDeterministicPrefixPublication = false,
        bool rejectsVscanSumAliasPromotion = false,
        bool rejectsReductionAliasPromotion = false,
        bool rejectsSegmentMovementAliasPromotion = false,
        bool isStructureMovement = false,
        bool requiresStructureShapeAbi = false,
        bool requiresShapeOrderingPolicy = false,
        bool requiresPayloadCanonicalization = false,
        bool requiresElementOrderPolicy = false,
        bool requiresStagedVectorPublication = false,
        bool rejectsMovementPermutationAliasPromotion = false,
        bool rejectsSegmentMemoryAliasPromotion = false,
        bool rejectsHiddenStreamEngineFallback = false,
        bool isSegmentMemory = false,
        bool isSegmentLoad = false,
        bool isSegmentStore = false,
        int segmentCount = 0,
        bool requiresSegmentMemoryShapeAbi = false,
        bool requiresSegmentCountAbi = false,
        bool requiresFaultReplayPolicy = false,
        bool requiresByteOrderingPolicy = false,
        bool requiresAlignmentFaultPolicy = false,
        bool requiresSegmentOrderingPolicy = false,
        bool requiresRetireStagedPublication = false,
        bool requiresRetireStagedCommit = false,
        bool rejectsBaseMemoryOpcodeDuplication = false,
        bool rejectsStructureMovementAliasPromotion = false,
        bool rejectsIndexedOr2DMemoryAliasPromotion = false,
        bool isFixedPointSaturation = false,
        bool requiresSaturatingPolicyAbi = false,
        bool requiresSignednessWidthClampPolicy = false,
        bool requiresOverflowPolicyAbi = false,
        bool requiresVlmMaterializationPolicy = false,
        bool requiresStagedPublicationRetirePolicy = false,
        bool requiresReplayRollbackGoldenEvidence = false,
        bool requiresSaturatingShiftPolicyAbi = false,
        bool requiresSaturatingShiftMeaningDecision = false,
        bool mayRemainReservedIfNonMeaningful = false,
        bool separateFromClosedVaddSat = false,
        bool noVaddSatFallback = false,
        bool noBaseVectorArithmeticFallback = false,
        bool noBaseVectorShiftFallback = false,
        bool noScalarHelperFallback = false,
        bool noLane6StreamFallback = false,
        bool noLane7AcceleratorFallback = false,
        bool noVmxSpecificPathFallback = false,
        bool noExecutableRowAliasPromotion = false,
        bool rejectsSaturatingAddAliasPromotion = false,
        bool rejectsAverageClipAliasPromotion = false,
        bool rejectsBaseArithmeticOrShiftAliasPromotion = false,
        bool isFixedPointAverageClip = false,
        bool isFixedPointAverage = false,
        bool isRoundedFixedPointAverage = false,
        bool isFixedPointClip = false,
        bool requiresAveragePolicyAbi = false,
        bool requiresRoundingPolicyAbi = false,
        bool requiresRoundingTruncationPolicyAbi = false,
        bool requiresClipBoundsAbi = false,
        bool requiresResultWidthPolicyAbi = false,
        bool noFixedPointSaturationFallback = false,
        bool noNarrowWidenConvertFallback = false,
        bool rejectsFixedPointSaturationAliasPromotion = false,
        bool isDotTileVariant = false,
        bool isDotBlockscaleVariant = false,
        bool isDotAccumulatorVariant = false,
        bool isDotWideIntegerVariant = false,
        bool requiresDotVariantAbi = false,
        bool requiresDotTileHelperAbi = false,
        bool requiresAccumulatorPrecisionAbi = false,
        bool requiresAccumulatorResultFootprintAbi = false,
        bool requiresScaleMetadataAbi = false,
        bool requiresSeparateResultSurfaceAbi = false,
        bool requiresWiderIntegerContourAbi = false,
        bool requiresDeterministicOrderingReplayPolicy = false,
        bool separateFromScopedVdotWide = false,
        bool noScopedVdotWideFallback = false,
        bool noNameOnlyVdotWideExtension = false,
        bool noBaseDotProductFallback = false,
        bool noWideningFmaFallback = false,
        bool noLane6DescriptorFallback = false,
        bool noMatrixTileFallback = false,
        bool noHostOwnedEvidencePublication = false)
    {
        Mnemonic = mnemonic;
        AbiClass = abiClass;
        ExtensionName = extensionName;
        EvidenceBoundary = evidenceBoundary;
        AbiDecision = abiDecision;
        OperandShape = operandShape;
        DataSemantics = dataSemantics;
        ResultSemantics = resultSemantics;
        RequiredPolicyDecisions = requiredPolicyDecisions;
        IsPredicateSelectMerge = isPredicateSelectMerge;
        RequiresPredicatePolarityPolicy = requiresPredicatePolarityPolicy;
        RequiresSelectMergeResultAbi = requiresSelectMergeResultAbi;
        RequiresPredicateSidebandAbi = requiresPredicateSidebandAbi;
        RejectsCzeroAliasPromotion = rejectsCzeroAliasPromotion;
        RejectsMaskPrefixAliasPromotion = rejectsMaskPrefixAliasPromotion;
        IsPredicateScalarSummary = isPredicateScalarSummary;
        RequiresScalarPredicateSummaryFootprint = requiresScalarPredicateSummaryFootprint;
        RequiresNoActiveElementSentinelPolicy = requiresNoActiveElementSentinelPolicy;
        RequiresActiveVlTailSemantics = requiresActiveVlTailSemantics;
        RequiresRetireOwnedScalarPublication = requiresRetireOwnedScalarPublication;
        RejectsScalarReductionAliasPromotion = rejectsScalarReductionAliasPromotion;
        IsPredicateMaskPrefix = isPredicateMaskPrefix;
        RequiresPredicateOnlyDestinationPolicy = requiresPredicateOnlyDestinationPolicy;
        RequiresIncludingFirstOnlyFirstPolicy = requiresIncludingFirstOnlyFirstPolicy;
        RequiresStagedMaskPublication = requiresStagedMaskPublication;
        RequiresRollbackEvidence = requiresRollbackEvidence;
        RejectsVmsbfAliasPromotion = rejectsVmsbfAliasPromotion;
        RejectsSelectMergeAliasPromotion = rejectsSelectMergeAliasPromotion;
        IsSignedExtend = isSignedExtend;
        RequiresVectorSourceWidthAbi = requiresVectorSourceWidthAbi;
        RequiresSignedExtensionPolicy = requiresSignedExtensionPolicy;
        RequiresSignednessSeparationPolicy = requiresSignednessSeparationPolicy;
        RejectsVzextAliasPromotion = rejectsVzextAliasPromotion;
        RejectsWidenNarrowConvertAliasPromotion = rejectsWidenNarrowConvertAliasPromotion;
        IsWideningArithmetic = isWideningArithmetic;
        IsWideningMultiplyAccumulate = isWideningMultiplyAccumulate;
        RequiresSourceDestinationWidthSideband = requiresSourceDestinationWidthSideband;
        RequiresElementWidthLmulVlAbi = requiresElementWidthLmulVlAbi;
        RequiresSignednessAbi = requiresSignednessAbi;
        RequiresWideningOverflowPolicyAbi = requiresWideningOverflowPolicyAbi;
        RequiresAccumulatorFootprintAbi = requiresAccumulatorFootprintAbi;
        RequiresAccumulatorPrecisionPolicy = requiresAccumulatorPrecisionPolicy;
        SeparateFromBaseVectorArithmetic = separateFromBaseVectorArithmetic;
        RejectsVzextVsextAliasPromotion = rejectsVzextVsextAliasPromotion;
        RejectsBaseArithmeticAliasPromotion = rejectsBaseArithmeticAliasPromotion;
        RejectsDotProductAliasPromotion = rejectsDotProductAliasPromotion;
        IsNarrowingShift = isNarrowingShift;
        RequiresNarrowingPolicyAbi = requiresNarrowingPolicyAbi;
        RequiresShiftOperandAbi = requiresShiftOperandAbi;
        RequiresRoundingSaturationTrapPolicy = requiresRoundingSaturationTrapPolicy;
        RequiresTruncationPublicationPolicy = requiresTruncationPublicationPolicy;
        RejectsClipAverageAliasPromotion = rejectsClipAverageAliasPromotion;
        RejectsBaseShiftAliasPromotion = rejectsBaseShiftAliasPromotion;
        IsConversion = isConversion;
        RequiresConversionPolicyAbi = requiresConversionPolicyAbi;
        RequiresConversionTypeDomainAbi = requiresConversionTypeDomainAbi;
        RequiresNanPolicyAbi = requiresNanPolicyAbi;
        RequiresConversionResultFootprintAbi = requiresConversionResultFootprintAbi;
        RejectsWidenNarrowArithmeticAliasPromotion = rejectsWidenNarrowArithmeticAliasPromotion;
        RejectsScalarConversionAliasPromotion = rejectsScalarConversionAliasPromotion;
        IsPrefixScanMinMax = isPrefixScanMinMax;
        RequiresPrefixScanPolicyAbi = requiresPrefixScanPolicyAbi;
        RequiresPrefixMinMaxOrderingPolicy = requiresPrefixMinMaxOrderingPolicy;
        RequiresInclusiveExclusivePolicy = requiresInclusiveExclusivePolicy;
        RequiresActiveVlTailBehaviorPolicy = requiresActiveVlTailBehaviorPolicy;
        RequiresElementTypeSideband = requiresElementTypeSideband;
        RequiresReplayDeterministicPrefixPublication = requiresReplayDeterministicPrefixPublication;
        RejectsVscanSumAliasPromotion = rejectsVscanSumAliasPromotion;
        RejectsReductionAliasPromotion = rejectsReductionAliasPromotion;
        RejectsSegmentMovementAliasPromotion = rejectsSegmentMovementAliasPromotion;
        IsStructureMovement = isStructureMovement;
        RequiresStructureShapeAbi = requiresStructureShapeAbi;
        RequiresShapeOrderingPolicy = requiresShapeOrderingPolicy;
        RequiresPayloadCanonicalization = requiresPayloadCanonicalization;
        RequiresElementOrderPolicy = requiresElementOrderPolicy;
        RequiresStagedVectorPublication = requiresStagedVectorPublication;
        RejectsMovementPermutationAliasPromotion = rejectsMovementPermutationAliasPromotion;
        RejectsSegmentMemoryAliasPromotion = rejectsSegmentMemoryAliasPromotion;
        RejectsHiddenStreamEngineFallback = rejectsHiddenStreamEngineFallback;
        IsSegmentMemory = isSegmentMemory;
        IsSegmentLoad = isSegmentLoad;
        IsSegmentStore = isSegmentStore;
        SegmentCount = segmentCount;
        RequiresSegmentMemoryShapeAbi = requiresSegmentMemoryShapeAbi;
        RequiresSegmentCountAbi = requiresSegmentCountAbi;
        RequiresFaultReplayPolicy = requiresFaultReplayPolicy;
        RequiresByteOrderingPolicy = requiresByteOrderingPolicy;
        RequiresAlignmentFaultPolicy = requiresAlignmentFaultPolicy;
        RequiresSegmentOrderingPolicy = requiresSegmentOrderingPolicy;
        RequiresRetireStagedPublication = requiresRetireStagedPublication;
        RequiresRetireStagedCommit = requiresRetireStagedCommit;
        RejectsBaseMemoryOpcodeDuplication = rejectsBaseMemoryOpcodeDuplication;
        RejectsStructureMovementAliasPromotion = rejectsStructureMovementAliasPromotion;
        RejectsIndexedOr2DMemoryAliasPromotion = rejectsIndexedOr2DMemoryAliasPromotion;
        IsFixedPointSaturation = isFixedPointSaturation;
        RequiresSaturatingPolicyAbi = requiresSaturatingPolicyAbi;
        RequiresSignednessWidthClampPolicy = requiresSignednessWidthClampPolicy;
        RequiresOverflowPolicyAbi = requiresOverflowPolicyAbi;
        RequiresVlmMaterializationPolicy = requiresVlmMaterializationPolicy;
        RequiresStagedPublicationRetirePolicy = requiresStagedPublicationRetirePolicy;
        RequiresReplayRollbackGoldenEvidence = requiresReplayRollbackGoldenEvidence;
        RequiresSaturatingShiftPolicyAbi = requiresSaturatingShiftPolicyAbi;
        RequiresSaturatingShiftMeaningDecision = requiresSaturatingShiftMeaningDecision;
        MayRemainReservedIfNonMeaningful = mayRemainReservedIfNonMeaningful;
        SeparateFromClosedVaddSat = separateFromClosedVaddSat;
        NoVaddSatFallback = noVaddSatFallback;
        NoBaseVectorArithmeticFallback = noBaseVectorArithmeticFallback;
        NoBaseVectorShiftFallback = noBaseVectorShiftFallback;
        NoScalarHelperFallback = noScalarHelperFallback;
        NoLane6StreamFallback = noLane6StreamFallback;
        NoLane7AcceleratorFallback = noLane7AcceleratorFallback;
        NoVmxSpecificPathFallback = noVmxSpecificPathFallback;
        NoExecutableRowAliasPromotion = noExecutableRowAliasPromotion;
        RejectsSaturatingAddAliasPromotion = rejectsSaturatingAddAliasPromotion;
        RejectsAverageClipAliasPromotion = rejectsAverageClipAliasPromotion;
        RejectsBaseArithmeticOrShiftAliasPromotion = rejectsBaseArithmeticOrShiftAliasPromotion;
        IsFixedPointAverageClip = isFixedPointAverageClip;
        IsFixedPointAverage = isFixedPointAverage;
        IsRoundedFixedPointAverage = isRoundedFixedPointAverage;
        IsFixedPointClip = isFixedPointClip;
        RequiresAveragePolicyAbi = requiresAveragePolicyAbi;
        RequiresRoundingPolicyAbi = requiresRoundingPolicyAbi;
        RequiresRoundingTruncationPolicyAbi = requiresRoundingTruncationPolicyAbi;
        RequiresClipBoundsAbi = requiresClipBoundsAbi;
        RequiresResultWidthPolicyAbi = requiresResultWidthPolicyAbi;
        NoFixedPointSaturationFallback = noFixedPointSaturationFallback;
        NoNarrowWidenConvertFallback = noNarrowWidenConvertFallback;
        RejectsFixedPointSaturationAliasPromotion = rejectsFixedPointSaturationAliasPromotion;
        IsDotTileVariant = isDotTileVariant;
        IsDotBlockscaleVariant = isDotBlockscaleVariant;
        IsDotAccumulatorVariant = isDotAccumulatorVariant;
        IsDotWideIntegerVariant = isDotWideIntegerVariant;
        RequiresDotVariantAbi = requiresDotVariantAbi;
        RequiresDotTileHelperAbi = requiresDotTileHelperAbi;
        RequiresAccumulatorPrecisionAbi = requiresAccumulatorPrecisionAbi;
        RequiresAccumulatorResultFootprintAbi = requiresAccumulatorResultFootprintAbi;
        RequiresScaleMetadataAbi = requiresScaleMetadataAbi;
        RequiresSeparateResultSurfaceAbi = requiresSeparateResultSurfaceAbi;
        RequiresWiderIntegerContourAbi = requiresWiderIntegerContourAbi;
        RequiresDeterministicOrderingReplayPolicy = requiresDeterministicOrderingReplayPolicy;
        SeparateFromScopedVdotWide = separateFromScopedVdotWide;
        NoScopedVdotWideFallback = noScopedVdotWideFallback;
        NoNameOnlyVdotWideExtension = noNameOnlyVdotWideExtension;
        NoBaseDotProductFallback = noBaseDotProductFallback;
        NoWideningFmaFallback = noWideningFmaFallback;
        NoLane6DescriptorFallback = noLane6DescriptorFallback;
        NoMatrixTileFallback = noMatrixTileFallback;
        NoHostOwnedEvidencePublication = noHostOwnedEvidencePublication;
    }

    public static CompilerVectorVlmBlockedAbiContract Merge { get; } =
        CreatePredicateSelectMergeRow(
            "VMERGE",
            "NoAllocationUntilPredicateMergeVlmMaskTailResultReplayAbi",
            "Predicate merge polarity, mask/tail semantics, and result footprint are not selected.",
            "Future helper emission requires VLM legality, predicate sideband ABI, staged publication, and no scalar or mask-prefix aliasing.");

    public static CompilerVectorVlmBlockedAbiContract Select { get; } =
        CreatePredicateSelectMergeRow(
            "VSELECT",
            "NoAllocationUntilPredicateSelectVlmMaskTailResultReplayAbi",
            "Predicate select polarity, mask/tail semantics, and result footprint are not selected.",
            "Future helper emission requires VLM legality, predicate sideband ABI, staged publication, and no scalar or mask-prefix aliasing.");

    public static CompilerVectorVlmBlockedAbiContract First { get; } =
        CreatePredicateScalarSummaryRow(
            "VFIRST",
            "NoAllocationUntilVectorFirstScalarSummarySentinelTailReplayAbi",
            "First-active predicate summary, no-active sentinel, active VL, and tail semantics are not selected.",
            "Future helper emission requires scalar result footprint, retire-owned publication, replay evidence, and no reduction aliasing.");

    public static CompilerVectorVlmBlockedAbiContract Any { get; } =
        CreatePredicateScalarSummaryRow(
            "VANY",
            "NoAllocationUntilVectorAnyScalarSummaryActiveVlTailReplayAbi",
            "Any-active predicate summary, active VL, and tail semantics are not selected.",
            "Future helper emission requires scalar result footprint, retire-owned publication, replay evidence, and no reduction aliasing.");

    public static CompilerVectorVlmBlockedAbiContract All { get; } =
        CreatePredicateScalarSummaryRow(
            "VALL",
            "NoAllocationUntilVectorAllScalarSummaryActiveVlTailReplayAbi",
            "All-active predicate summary, active VL, and tail semantics are not selected.",
            "Future helper emission requires scalar result footprint, retire-owned publication, replay evidence, and no reduction aliasing.");

    public static CompilerVectorVlmBlockedAbiContract MaskSetIncludingFirst { get; } =
        CreatePredicateMaskPrefixRow(
            "VMSIF",
            "NoAllocationUntilMaskSetIncludingFirstPredicateOnlyTailRollbackAbi",
            "Including-first mask prefix semantics, predicate-only destination, and staged rollback policy are not selected.",
            "Future helper emission requires VLM legality, predicate-only publication, rollback evidence, and no VMSBF or select/merge aliasing.");

    public static CompilerVectorVlmBlockedAbiContract MaskSetOnlyFirst { get; } =
        CreatePredicateMaskPrefixRow(
            "VMSOF",
            "NoAllocationUntilMaskSetOnlyFirstPredicateOnlyTailRollbackAbi",
            "Only-first mask prefix semantics, predicate-only destination, and staged rollback policy are not selected.",
            "Future helper emission requires VLM legality, predicate-only publication, rollback evidence, and no VMSBF or select/merge aliasing.");

    public static CompilerVectorVlmBlockedAbiContract SignExtend { get; } =
        new(
            "VSEXT",
            CompilerVectorVlmBlockedAbiClass.SignedExtend,
            "VectorSignedExtendVlmBlocked",
            "VectorSignedExtendVlmRuntimeBlocked",
            "NoAllocationUntilSignedVectorSourceWidthVlmTailReplayNoAlias",
            "packed signed source-width payload; no typed facade helper operands",
            "Signed source-width ABI, mask/tail policy, and VLM materialization are not selected.",
            "Future helper emission requires signed-extension policy, staged publication, and explicit separation from VZEXT, widen, narrow, or convert rows.",
            SignedExtendRequiredPolicyDecisions,
            isSignedExtend: true,
            requiresVectorSourceWidthAbi: true,
            requiresSignedExtensionPolicy: true,
            requiresSignednessSeparationPolicy: true,
            rejectsVzextAliasPromotion: true,
            rejectsWidenNarrowConvertAliasPromotion: true);

    public static CompilerVectorVlmBlockedAbiContract FixedPointSatSubAudit { get; } =
        CreateFixedPointSaturationRow(
            "VSUB.SAT",
            "NoAllocationUntilFixedPointSatSubSignednessWidthClampReplayAbi",
            "Saturating subtract signedness, element width, clamp, overflow, mask/tail policy, and VLM materialization are not selected.",
            "Future helper emission requires explicit saturation policy ABI, deterministic retire/replay, and no aliasing with closed VADD.SAT, average/clip, or base arithmetic rows.",
            requiresShiftOperandAbi: false,
            mayRemainReservedIfNonMeaningful: false);

    public static CompilerVectorVlmBlockedAbiContract FixedPointSatMulAudit { get; } =
        CreateFixedPointSaturationRow(
            "VMUL.SAT",
            "NoAllocationUntilFixedPointSatMulProductPrecisionClampReplayAbi",
            "Saturating multiply product precision, signedness, element width, clamp, overflow, mask/tail policy, and VLM materialization are not selected.",
            "Future helper emission requires explicit saturation policy ABI, deterministic retire/replay, and no aliasing with closed VADD.SAT, average/clip, dot, or base arithmetic rows.",
            requiresShiftOperandAbi: false,
            mayRemainReservedIfNonMeaningful: false);

    public static CompilerVectorVlmBlockedAbiContract FixedPointSatLeftShiftAudit { get; } =
        CreateFixedPointSaturationRow(
            "VSLL.SAT",
            "NoAllocationUntilFixedPointSatLeftShiftOperandClampReplayAbi",
            "Saturating left-shift operand ABI, signedness, element width, clamp, overflow, mask/tail policy, and VLM materialization are not selected.",
            "Future helper emission requires explicit shift-operand and saturation policy ABI, deterministic retire/replay, and no aliasing with closed VADD.SAT, clip, or base shift rows.",
            requiresShiftOperandAbi: true,
            mayRemainReservedIfNonMeaningful: false);

    public static CompilerVectorVlmBlockedAbiContract FixedPointSatLogicalRightShiftAudit { get; } =
        CreateFixedPointSaturationRow(
            "VSRL.SAT",
            "NoAllocationUntilFixedPointSatLogicalRightShiftMeaningClampReplayAbi",
            "Saturating logical right-shift operand ABI, signedness, element width, saturation meaningfulness, mask/tail policy, and VLM materialization are not selected.",
            "Future helper emission requires an explicit decision that logical right-shift saturation is meaningful, deterministic retire/replay, and no aliasing with base shift or clip rows.",
            requiresShiftOperandAbi: true,
            mayRemainReservedIfNonMeaningful: true);

    public static CompilerVectorVlmBlockedAbiContract FixedPointSatArithmeticRightShiftAudit { get; } =
        CreateFixedPointSaturationRow(
            "VSRA.SAT",
            "NoAllocationUntilFixedPointSatArithmeticRightShiftMeaningClampReplayAbi",
            "Saturating arithmetic right-shift operand ABI, signedness, element width, saturation meaningfulness, mask/tail policy, and VLM materialization are not selected.",
            "Future helper emission requires an explicit decision that arithmetic right-shift saturation is meaningful, deterministic retire/replay, and no aliasing with base shift or clip rows.",
            requiresShiftOperandAbi: true,
            mayRemainReservedIfNonMeaningful: true);

    public static CompilerVectorVlmBlockedAbiContract FixedPointAvgAudit { get; } =
        CreateFixedPointAverageClipRow(
            "VAVG",
            "NoAllocationUntilFixedPointAverageSignednessWidthRoundingReplayAbi",
            "Fixed-point average signedness, element width, overflow, rounding/truncation, mask/tail policy, and VLM materialization are not selected.",
            "Future helper emission requires explicit average policy ABI, deterministic staged retire/replay, golden evidence, and no aliasing with VADD.SAT, fixed-point saturation, base arithmetic, or scalar helper rows.",
            isAverage: true,
            isRoundedAverage: false,
            isClip: false);

    public static CompilerVectorVlmBlockedAbiContract FixedPointRoundedAvgAudit { get; } =
        CreateFixedPointAverageClipRow(
            "VAVG.R",
            "NoAllocationUntilRoundedFixedPointAverageSignednessWidthRoundingReplayAbi",
            "Rounded fixed-point average signedness, element width, rounding mode, overflow, mask/tail policy, and VLM materialization are not selected.",
            "Future helper emission requires explicit rounded-average policy ABI, deterministic staged retire/replay, golden evidence, and no aliasing with VADD.SAT, fixed-point saturation, base arithmetic, or scalar helper rows.",
            isAverage: true,
            isRoundedAverage: true,
            isClip: false);

    public static CompilerVectorVlmBlockedAbiContract FixedPointBoundsAudit { get; } =
        CreateFixedPointAverageClipRow(
            "VCLIP",
            "NoAllocationUntilFixedPointBoundsResultWidthRoundingReplayAbi",
            "Fixed-point bounds encoding, result width, narrowing behavior, signedness, rounding/truncation, mask/tail policy, and VLM materialization are not selected.",
            "Future helper emission requires explicit bounds/result-width policy ABI, deterministic staged retire/replay, golden evidence, and no aliasing with narrowing, conversion, fixed-point saturation, base arithmetic, or scalar helper rows.",
            isAverage: false,
            isRoundedAverage: false,
            isClip: true);

    public static CompilerVectorVlmBlockedAbiContract DotScaleVariantAudit { get; } =
        CreateDotTileVariantRow(
            "VDOT.BLOCKSCALE",
            "NoAllocationUntilDotScaleVariantMetadataAccumulatorReplayAbi",
            "Block-scale metadata ABI, accumulator precision/result footprint, element-domain policy, deterministic ordering, and VLM materialization are not selected.",
            "Future helper emission requires a variant-specific dot/tile ABI, scale metadata payload, staged retire/replay, golden evidence, and no fallback to scoped VDOT.WIDE, base dot-product, widening/FMA, Lane6 descriptor, matrix/tile, or VMX-specific paths.",
            isBlockscaleVariant: true,
            isAccumulatorVariant: false,
            isWideIntegerVariant: false);

    public static CompilerVectorVlmBlockedAbiContract DotAccumulatorVariantAudit { get; } =
        CreateDotTileVariantRow(
            "VDOT.ACCUM",
            "NoAllocationUntilDotAccumulatorVariantFootprintReplayAbi",
            "Accumulator result footprint ABI, accumulator precision, update ordering, deterministic publication, and VLM materialization are not selected.",
            "Future helper emission requires a variant-specific accumulator dot/tile ABI, staged retire/replay, golden evidence, and no fallback to scoped VDOT.WIDE, base dot-product, widening/FMA, Lane6 descriptor, matrix/tile, or VMX-specific paths.",
            isBlockscaleVariant: false,
            isAccumulatorVariant: true,
            isWideIntegerVariant: false);

    public static CompilerVectorVlmBlockedAbiContract DotWide16VariantAudit { get; } =
        CreateDotTileVariantRow(
            "VDOT.WIDE.I16",
            "NoAllocationUntilDotWideI16VariantContourReplayAbi",
            "I16 wide-integer dot contour ABI, accumulator precision/result footprint, deterministic ordering, and VLM materialization are not selected.",
            "Future helper emission requires an explicit I16 wide-integer contour ABI separate from scoped VDOT.WIDE, staged retire/replay, golden evidence, and no name-only extension or fallback to base dot-product/widening paths.",
            isBlockscaleVariant: false,
            isAccumulatorVariant: false,
            isWideIntegerVariant: true);

    public static CompilerVectorVlmBlockedAbiContract DotWide32VariantAudit { get; } =
        CreateDotTileVariantRow(
            "VDOT.WIDE.I32",
            "NoAllocationUntilDotWideI32VariantContourReplayAbi",
            "I32 wide-integer dot contour ABI, accumulator precision/result footprint, deterministic ordering, and VLM materialization are not selected.",
            "Future helper emission requires an explicit I32 wide-integer contour ABI separate from scoped VDOT.WIDE, staged retire/replay, golden evidence, and no name-only extension or fallback to base dot-product/widening paths.",
            isBlockscaleVariant: false,
            isAccumulatorVariant: false,
            isWideIntegerVariant: true);

    public static CompilerVectorVlmBlockedAbiContract WideningAddSigned { get; } =
        CreateWideningArithmeticRow(
            "VWADD",
            "NoAllocationUntilSignedWideningAddWidthLmulVlOverflowReplayAbi",
            "Signed widening-add source/destination width, LMUL/VL, overflow, and mask/tail policy are not selected.",
            "Future helper emission requires explicit widening payload construction, result footprint, replay, and no base vector arithmetic aliasing.");

    public static CompilerVectorVlmBlockedAbiContract WideningAddUnsigned { get; } =
        CreateWideningArithmeticRow(
            "VWADDU",
            "NoAllocationUntilUnsignedWideningAddWidthLmulVlOverflowReplayAbi",
            "Unsigned widening-add source/destination width, LMUL/VL, overflow, and mask/tail policy are not selected.",
            "Future helper emission requires explicit widening payload construction, result footprint, replay, and no base vector arithmetic aliasing.");

    public static CompilerVectorVlmBlockedAbiContract WideningSubtractSigned { get; } =
        CreateWideningArithmeticRow(
            "VWSUB",
            "NoAllocationUntilSignedWideningSubWidthLmulVlOverflowReplayAbi",
            "Signed widening-subtract source/destination width, LMUL/VL, overflow, and mask/tail policy are not selected.",
            "Future helper emission requires explicit widening payload construction, result footprint, replay, and no base vector arithmetic aliasing.");

    public static CompilerVectorVlmBlockedAbiContract WideningSubtractUnsigned { get; } =
        CreateWideningArithmeticRow(
            "VWSUBU",
            "NoAllocationUntilUnsignedWideningSubWidthLmulVlOverflowReplayAbi",
            "Unsigned widening-subtract source/destination width, LMUL/VL, overflow, and mask/tail policy are not selected.",
            "Future helper emission requires explicit widening payload construction, result footprint, replay, and no base vector arithmetic aliasing.");

    public static CompilerVectorVlmBlockedAbiContract WideningMultiplySigned { get; } =
        CreateWideningArithmeticRow(
            "VWMUL",
            "NoAllocationUntilSignedWideningMulWidthLmulVlOverflowReplayAbi",
            "Signed widening-multiply source/destination width, LMUL/VL, overflow, and mask/tail policy are not selected.",
            "Future helper emission requires explicit widening payload construction, result footprint, replay, and no base vector arithmetic aliasing.");

    public static CompilerVectorVlmBlockedAbiContract WideningMultiplyUnsigned { get; } =
        CreateWideningArithmeticRow(
            "VWMULU",
            "NoAllocationUntilUnsignedWideningMulWidthLmulVlOverflowReplayAbi",
            "Unsigned widening-multiply source/destination width, LMUL/VL, overflow, and mask/tail policy are not selected.",
            "Future helper emission requires explicit widening payload construction, result footprint, replay, and no base vector arithmetic aliasing.");

    public static CompilerVectorVlmBlockedAbiContract WideningMultiplyAccumulate { get; } =
        new(
            "VWMACC",
            CompilerVectorVlmBlockedAbiClass.WideningMultiplyAccumulate,
            "VectorWideningMultiplyAccumulateVlmBlocked",
            "VectorWidenNarrowConvertFailClosed",
            "NoAllocationUntilWideningMultiplyAccumulateWidthAccumulatorReplayAbi",
            "widening multiply-accumulate vector payload plus accumulator footprint; no typed facade helper operands",
            "Widening multiply-accumulate width, accumulator precision, LMUL/VL, overflow, and mask/tail policy are not selected.",
            "Future helper emission requires accumulator footprint ABI, deterministic retire/replay, and no dot-product or base arithmetic aliasing.",
            WideningMultiplyAccumulateRequiredPolicyDecisions,
            isWideningArithmetic: true,
            isWideningMultiplyAccumulate: true,
            requiresSourceDestinationWidthSideband: true,
            requiresElementWidthLmulVlAbi: true,
            requiresSignednessAbi: true,
            requiresWideningOverflowPolicyAbi: true,
            requiresAccumulatorFootprintAbi: true,
            requiresAccumulatorPrecisionPolicy: true,
            separateFromBaseVectorArithmetic: true,
            rejectsVzextVsextAliasPromotion: true,
            rejectsBaseArithmeticAliasPromotion: true,
            rejectsDotProductAliasPromotion: true);

    public static CompilerVectorVlmBlockedAbiContract NarrowingShiftRightLogical { get; } =
        CreateNarrowingShiftRow(
            "VNSRL",
            "NoAllocationUntilNarrowingLogicalShiftWidthRoundingReplayAbi",
            "Logical narrowing-shift width, shift operand, truncation, rounding/saturation/trap, and mask/tail policy are not selected.",
            "Future helper emission requires narrowing policy ABI, staged publication, replay, and no clip/average or base-shift aliasing.");

    public static CompilerVectorVlmBlockedAbiContract NarrowingShiftRightArithmetic { get; } =
        CreateNarrowingShiftRow(
            "VNSRA",
            "NoAllocationUntilNarrowingArithmeticShiftWidthRoundingReplayAbi",
            "Arithmetic narrowing-shift width, shift operand, truncation, rounding/saturation/trap, and mask/tail policy are not selected.",
            "Future helper emission requires narrowing policy ABI, staged publication, replay, and no clip/average or base-shift aliasing.");

    public static CompilerVectorVlmBlockedAbiContract ConvertToSignedInteger { get; } =
        CreateConversionRow(
            "VCVT.I",
            "NoAllocationUntilVectorConvertSignedIntegerTypeRoundingNanReplayAbi",
            "Vector conversion type-domain, signed integer result footprint, NaN, rounding/saturation/trap, and mask/tail policy are not selected.",
            "Future helper emission requires explicit conversion ABI, deterministic result publication, replay, and no VSEXT/VZEXT or widening/narrowing aliasing.");

    public static CompilerVectorVlmBlockedAbiContract ConvertToUnsignedInteger { get; } =
        CreateConversionRow(
            "VCVT.U",
            "NoAllocationUntilVectorConvertUnsignedIntegerTypeRoundingNanReplayAbi",
            "Vector conversion type-domain, unsigned integer result footprint, NaN, rounding/saturation/trap, and mask/tail policy are not selected.",
            "Future helper emission requires explicit conversion ABI, deterministic result publication, replay, and no VSEXT/VZEXT or widening/narrowing aliasing.");

    public static CompilerVectorVlmBlockedAbiContract ConvertToFloatingPoint { get; } =
        CreateConversionRow(
            "VCVT.F",
            "NoAllocationUntilVectorConvertFloatingPointTypeRoundingNanReplayAbi",
            "Vector conversion type-domain, floating-point result footprint, NaN, rounding/saturation/trap, and mask/tail policy are not selected.",
            "Future helper emission requires explicit conversion ABI, deterministic result publication, replay, and no scalar conversion aliasing.");

    public static CompilerVectorVlmBlockedAbiContract PrefixScanMinimum { get; } =
        CreatePrefixScanMinMaxRow(
            "VSCAN.MIN",
            "NoAllocationUntilPrefixMinScanOrderingActiveVlTailReplayNoAlias",
            "Prefix-min scan ordering, signedness/type sideband, inclusive/exclusive behavior, active VL, mask/tail, and result footprint are not selected.",
            "Future helper emission requires deterministic prefix publication, replay, and no VSCAN.SUM, reduction, or segment/movement aliasing.");

    public static CompilerVectorVlmBlockedAbiContract PrefixScanMaximum { get; } =
        CreatePrefixScanMinMaxRow(
            "VSCAN.MAX",
            "NoAllocationUntilPrefixMaxScanOrderingActiveVlTailReplayNoAlias",
            "Prefix-max scan ordering, signedness/type sideband, inclusive/exclusive behavior, active VL, mask/tail, and result footprint are not selected.",
            "Future helper emission requires deterministic prefix publication, replay, and no VSCAN.SUM, reduction, or segment/movement aliasing.");

    public static CompilerVectorVlmBlockedAbiContract Zip { get; } =
        CreateStructureMovementRow(
            "VZIP",
            "NoAllocationUntilVectorZipStructureShapeOrderingPublicationNoAlias",
            "Vector zip structure shape, element ordering, active VL, mask/tail, payload canonicalization, and staged publication are not selected.",
            "Future helper emission requires explicit structure movement ABI, deterministic replay, and no movement-permutation or segment-memory aliasing.");

    public static CompilerVectorVlmBlockedAbiContract Unzip { get; } =
        CreateStructureMovementRow(
            "VUNZIP",
            "NoAllocationUntilVectorUnzipStructureShapeOrderingPublicationNoAlias",
            "Vector unzip structure shape, element ordering, active VL, mask/tail, payload canonicalization, and staged publication are not selected.",
            "Future helper emission requires explicit structure movement ABI, deterministic replay, and no movement-permutation or segment-memory aliasing.");

    public static CompilerVectorVlmBlockedAbiContract Interleave { get; } =
        CreateStructureMovementRow(
            "VINTERLEAVE",
            "NoAllocationUntilVectorInterleaveStructureShapeOrderingPublicationNoAlias",
            "Vector interleave structure shape, element ordering, active VL, mask/tail, payload canonicalization, and staged publication are not selected.",
            "Future helper emission requires explicit structure movement ABI, deterministic replay, and no movement-permutation or segment-memory aliasing.");

    public static CompilerVectorVlmBlockedAbiContract Deinterleave { get; } =
        CreateStructureMovementRow(
            "VDEINTERLEAVE",
            "NoAllocationUntilVectorDeinterleaveStructureShapeOrderingPublicationNoAlias",
            "Vector deinterleave structure shape, element ordering, active VL, mask/tail, payload canonicalization, and staged publication are not selected.",
            "Future helper emission requires explicit structure movement ABI, deterministic replay, and no movement-permutation or segment-memory aliasing.");

    public static CompilerVectorVlmBlockedAbiContract LoadSegment2 { get; } =
        CreateSegmentMemoryRow(
            "VLDSEG2",
            2,
            isLoad: true);

    public static CompilerVectorVlmBlockedAbiContract LoadSegment4 { get; } =
        CreateSegmentMemoryRow(
            "VLDSEG4",
            4,
            isLoad: true);

    public static CompilerVectorVlmBlockedAbiContract LoadSegment8 { get; } =
        CreateSegmentMemoryRow(
            "VLDSEG8",
            8,
            isLoad: true);

    public static CompilerVectorVlmBlockedAbiContract StoreSegment2 { get; } =
        CreateSegmentMemoryRow(
            "VSTSEG2",
            2,
            isLoad: false);

    public static CompilerVectorVlmBlockedAbiContract StoreSegment4 { get; } =
        CreateSegmentMemoryRow(
            "VSTSEG4",
            4,
            isLoad: false);

    public static CompilerVectorVlmBlockedAbiContract StoreSegment8 { get; } =
        CreateSegmentMemoryRow(
            "VSTSEG8",
            8,
            isLoad: false);

    public static IReadOnlyList<CompilerVectorVlmBlockedAbiContract> AllVlmBlockedRows { get; } =
    [
        Merge,
        Select,
        First,
        Any,
        All,
        MaskSetIncludingFirst,
        MaskSetOnlyFirst,
        SignExtend,
        FixedPointSatSubAudit,
        FixedPointSatMulAudit,
        FixedPointSatLeftShiftAudit,
        FixedPointSatLogicalRightShiftAudit,
        FixedPointSatArithmeticRightShiftAudit,
        FixedPointAvgAudit,
        FixedPointRoundedAvgAudit,
        FixedPointBoundsAudit,
        DotScaleVariantAudit,
        DotAccumulatorVariantAudit,
        DotWide16VariantAudit,
        DotWide32VariantAudit,
        WideningAddSigned,
        WideningAddUnsigned,
        WideningSubtractSigned,
        WideningSubtractUnsigned,
        WideningMultiplySigned,
        WideningMultiplyUnsigned,
        WideningMultiplyAccumulate,
        NarrowingShiftRightLogical,
        NarrowingShiftRightArithmetic,
        ConvertToSignedInteger,
        ConvertToUnsignedInteger,
        ConvertToFloatingPoint,
        PrefixScanMinimum,
        PrefixScanMaximum,
        Zip,
        Unzip,
        Interleave,
        Deinterleave,
        LoadSegment2,
        LoadSegment4,
        LoadSegment8,
        StoreSegment2,
        StoreSegment4,
        StoreSegment8
    ];

    private static CompilerVectorVlmBlockedAbiContract CreatePredicateSelectMergeRow(
        string mnemonic,
        string abiDecision,
        string dataSemantics,
        string resultSemantics) =>
        new(
            mnemonic,
            CompilerVectorVlmBlockedAbiClass.PredicateSelectMerge,
            "VectorPredicateSelectMergeVlmBlocked",
            "VectorContourFailClosed",
            abiDecision,
            "predicate vector sources plus mask/tail policy; no typed facade helper operands",
            dataSemantics,
            resultSemantics,
            PredicateSelectRequiredPolicyDecisions,
            isPredicateSelectMerge: true,
            requiresPredicatePolarityPolicy: true,
            requiresSelectMergeResultAbi: true,
            requiresPredicateSidebandAbi: true,
            rejectsCzeroAliasPromotion: true,
            rejectsMaskPrefixAliasPromotion: true);

    private static CompilerVectorVlmBlockedAbiContract CreatePredicateScalarSummaryRow(
        string mnemonic,
        string abiDecision,
        string dataSemantics,
        string resultSemantics) =>
        new(
            mnemonic,
            CompilerVectorVlmBlockedAbiClass.PredicateScalarSummary,
            "VectorPredicateScalarSummaryVlmBlocked",
            "VectorScalarResultContourFailClosed",
            abiDecision,
            "predicate vector source plus scalar result footprint; no typed facade helper operands",
            dataSemantics,
            resultSemantics,
            PredicateScalarSummaryRequiredPolicyDecisions,
            isPredicateScalarSummary: true,
            requiresScalarPredicateSummaryFootprint: true,
            requiresNoActiveElementSentinelPolicy: true,
            requiresActiveVlTailSemantics: true,
            requiresRetireOwnedScalarPublication: true,
            rejectsScalarReductionAliasPromotion: true);

    private static CompilerVectorVlmBlockedAbiContract CreatePredicateMaskPrefixRow(
        string mnemonic,
        string abiDecision,
        string dataSemantics,
        string resultSemantics) =>
        new(
            mnemonic,
            CompilerVectorVlmBlockedAbiClass.PredicateMaskPrefix,
            "VectorPredicateMaskPrefixVlmBlocked",
            "VectorPredicateOnlyContourFailClosed",
            abiDecision,
            "predicate-only destination mask prefix payload; no typed facade helper operands",
            dataSemantics,
            resultSemantics,
            PredicateMaskPrefixRequiredPolicyDecisions,
            isPredicateMaskPrefix: true,
            requiresPredicateOnlyDestinationPolicy: true,
            requiresIncludingFirstOnlyFirstPolicy: true,
            requiresStagedMaskPublication: true,
            requiresRollbackEvidence: true,
            rejectsVmsbfAliasPromotion: true,
            rejectsSelectMergeAliasPromotion: true);

    private static CompilerVectorVlmBlockedAbiContract CreateWideningArithmeticRow(
        string mnemonic,
        string abiDecision,
        string dataSemantics,
        string resultSemantics) =>
        new(
            mnemonic,
            CompilerVectorVlmBlockedAbiClass.WideningArithmetic,
            "VectorWideningArithmeticVlmBlocked",
            "VectorWidenNarrowConvertFailClosed",
            abiDecision,
            "widening vector arithmetic payload plus source/destination width sideband; no typed facade helper operands",
            dataSemantics,
            resultSemantics,
            WideningArithmeticRequiredPolicyDecisions,
            isWideningArithmetic: true,
            requiresSourceDestinationWidthSideband: true,
            requiresElementWidthLmulVlAbi: true,
            requiresSignednessAbi: true,
            requiresWideningOverflowPolicyAbi: true,
            separateFromBaseVectorArithmetic: true,
            rejectsVzextVsextAliasPromotion: true,
            rejectsBaseArithmeticAliasPromotion: true);

    private static CompilerVectorVlmBlockedAbiContract CreateFixedPointSaturationRow(
        string mnemonic,
        string abiDecision,
        string dataSemantics,
        string resultSemantics,
        bool requiresShiftOperandAbi,
        bool mayRemainReservedIfNonMeaningful)
    {
        IReadOnlyList<string> requiredPolicyDecisions = requiresShiftOperandAbi
            ? [.. FixedPointSaturationRequiredPolicyDecisions, "ShiftOperandAbi", "SaturatingShiftPolicyAbi", "SaturatingShiftMeaningDecision"]
            : FixedPointSaturationRequiredPolicyDecisions;

        return new(
            mnemonic,
            CompilerVectorVlmBlockedAbiClass.FixedPointSaturation,
            "VectorFixedPointSaturationVlmBlocked",
            "VectorFixedPointSaturatingFailClosed",
            abiDecision,
            requiresShiftOperandAbi
                ? "saturating vector shift payload plus shift operand and signedness/width/clamp sideband; no typed facade helper operands"
                : "saturating vector arithmetic payload plus signedness/width/clamp sideband; no typed facade helper operands",
            dataSemantics,
            resultSemantics,
            requiredPolicyDecisions,
            isFixedPointSaturation: true,
            requiresSaturatingPolicyAbi: true,
            requiresSignednessWidthClampPolicy: true,
            requiresElementWidthLmulVlAbi: true,
            requiresSignednessAbi: true,
            requiresOverflowPolicyAbi: true,
            requiresVlmMaterializationPolicy: true,
            requiresStagedPublicationRetirePolicy: true,
            requiresReplayRollbackGoldenEvidence: true,
            requiresShiftOperandAbi: requiresShiftOperandAbi,
            requiresSaturatingShiftPolicyAbi: requiresShiftOperandAbi,
            requiresSaturatingShiftMeaningDecision: requiresShiftOperandAbi,
            mayRemainReservedIfNonMeaningful: mayRemainReservedIfNonMeaningful,
            separateFromClosedVaddSat: true,
            noVaddSatFallback: true,
            noBaseVectorArithmeticFallback: true,
            noBaseVectorShiftFallback: true,
            noScalarHelperFallback: true,
            noLane6StreamFallback: true,
            noLane7AcceleratorFallback: true,
            noVmxSpecificPathFallback: true,
            noExecutableRowAliasPromotion: true,
            rejectsSaturatingAddAliasPromotion: true,
            rejectsClipAverageAliasPromotion: true,
            rejectsBaseArithmeticAliasPromotion: true,
            rejectsBaseShiftAliasPromotion: requiresShiftOperandAbi,
            rejectsAverageClipAliasPromotion: true,
            rejectsBaseArithmeticOrShiftAliasPromotion: true);
    }

    private static CompilerVectorVlmBlockedAbiContract CreateFixedPointAverageClipRow(
        string mnemonic,
        string abiDecision,
        string dataSemantics,
        string resultSemantics,
        bool isAverage,
        bool isRoundedAverage,
        bool isClip)
    {
        IReadOnlyList<string> requiredPolicyDecisions =
        [
            .. FixedPointAverageClipRequiredPolicyDecisions,
            .. (isAverage ? new[] { "AveragePolicyAbi" } : Array.Empty<string>()),
            .. (isRoundedAverage ? new[] { "RoundingPolicyAbi" } : Array.Empty<string>()),
            .. (isClip
                ? new[] { "ClipBoundsAbi", "NarrowingPolicyAbi", "ResultWidthPolicyAbi" }
                : Array.Empty<string>())
        ];

        return new(
            mnemonic,
            CompilerVectorVlmBlockedAbiClass.FixedPointAverageClip,
            "VectorFixedPointAverageClipVlmBlocked",
            "VectorFixedPointSaturatingFailClosed",
            abiDecision,
            isClip
                ? "fixed-point bounds/narrowing payload plus result-width and rounding/truncation sideband; no typed facade helper operands"
                : "fixed-point average payload plus signedness/width and rounding/truncation sideband; no typed facade helper operands",
            dataSemantics,
            resultSemantics,
            requiredPolicyDecisions,
            requiresElementWidthLmulVlAbi: true,
            requiresSignednessAbi: true,
            requiresOverflowPolicyAbi: true,
            requiresRoundingSaturationTrapPolicy: isClip,
            requiresRoundingTruncationPolicyAbi: true,
            requiresNarrowingPolicyAbi: isClip,
            requiresVlmMaterializationPolicy: true,
            requiresStagedPublicationRetirePolicy: true,
            requiresReplayRollbackGoldenEvidence: true,
            noVaddSatFallback: true,
            noFixedPointSaturationFallback: true,
            noBaseVectorArithmeticFallback: true,
            noBaseVectorShiftFallback: true,
            noNarrowWidenConvertFallback: true,
            noScalarHelperFallback: true,
            noLane6StreamFallback: true,
            noLane7AcceleratorFallback: true,
            noVmxSpecificPathFallback: true,
            noExecutableRowAliasPromotion: true,
            rejectsClipAverageAliasPromotion: true,
            rejectsBaseArithmeticAliasPromotion: true,
            rejectsBaseShiftAliasPromotion: true,
            rejectsWidenNarrowConvertAliasPromotion: isClip,
            rejectsAverageClipAliasPromotion: true,
            rejectsBaseArithmeticOrShiftAliasPromotion: true,
            isFixedPointAverageClip: true,
            isFixedPointAverage: isAverage,
            isRoundedFixedPointAverage: isRoundedAverage,
            isFixedPointClip: isClip,
            requiresAveragePolicyAbi: isAverage,
            requiresRoundingPolicyAbi: isRoundedAverage,
            requiresClipBoundsAbi: isClip,
            requiresResultWidthPolicyAbi: isClip,
            rejectsFixedPointSaturationAliasPromotion: true);
    }

    private static CompilerVectorVlmBlockedAbiContract CreateDotTileVariantRow(
        string mnemonic,
        string abiDecision,
        string dataSemantics,
        string resultSemantics,
        bool isBlockscaleVariant,
        bool isAccumulatorVariant,
        bool isWideIntegerVariant)
    {
        IReadOnlyList<string> requiredPolicyDecisions =
        [
            .. DotTileVariantRequiredPolicyDecisions,
            .. (isBlockscaleVariant ? new[] { "ScaleMetadataAbi" } : Array.Empty<string>()),
            .. (!isWideIntegerVariant ? new[] { "SeparateResultSurfaceAbi" } : Array.Empty<string>()),
            .. (isWideIntegerVariant ? new[] { "WiderIntegerContourAbi" } : Array.Empty<string>())
        ];

        return new(
            mnemonic,
            CompilerVectorVlmBlockedAbiClass.DotTileVariant,
            "VectorDotTileVariantVlmBlocked",
            "VectorDotMatrixDeferredNoExecution",
            abiDecision,
            isWideIntegerVariant
                ? "wide-integer dot/tile payload plus accumulator/result footprint sideband; no typed facade helper operands"
                : "variant dot/tile payload plus accumulator/result footprint sideband; no typed facade helper operands",
            dataSemantics,
            resultSemantics,
            requiredPolicyDecisions,
            requiresElementWidthLmulVlAbi: true,
            requiresAccumulatorFootprintAbi: true,
            requiresAccumulatorPrecisionPolicy: true,
            requiresVlmMaterializationPolicy: true,
            requiresStagedPublicationRetirePolicy: true,
            requiresReplayRollbackGoldenEvidence: true,
            noScalarHelperFallback: true,
            noLane6StreamFallback: true,
            noLane7AcceleratorFallback: true,
            noVmxSpecificPathFallback: true,
            noExecutableRowAliasPromotion: true,
            noScopedVdotWideFallback: true,
            noNameOnlyVdotWideExtension: true,
            noBaseDotProductFallback: true,
            noWideningFmaFallback: true,
            noLane6DescriptorFallback: true,
            noMatrixTileFallback: true,
            noHostOwnedEvidencePublication: true,
            rejectsDotProductAliasPromotion: true,
            isDotTileVariant: true,
            isDotBlockscaleVariant: isBlockscaleVariant,
            isDotAccumulatorVariant: isAccumulatorVariant,
            isDotWideIntegerVariant: isWideIntegerVariant,
            requiresDotVariantAbi: true,
            requiresDotTileHelperAbi: true,
            requiresAccumulatorPrecisionAbi: true,
            requiresAccumulatorResultFootprintAbi: true,
            requiresScaleMetadataAbi: isBlockscaleVariant,
            requiresSeparateResultSurfaceAbi: !isWideIntegerVariant,
            requiresWiderIntegerContourAbi: isWideIntegerVariant,
            requiresDeterministicOrderingReplayPolicy: true,
            separateFromScopedVdotWide: true);
    }

    private static CompilerVectorVlmBlockedAbiContract CreateNarrowingShiftRow(
        string mnemonic,
        string abiDecision,
        string dataSemantics,
        string resultSemantics) =>
        new(
            mnemonic,
            CompilerVectorVlmBlockedAbiClass.NarrowingShift,
            "VectorNarrowingShiftVlmBlocked",
            "VectorWidenNarrowConvertFailClosed",
            abiDecision,
            "narrowing shift payload plus source/destination width sideband; no typed facade helper operands",
            dataSemantics,
            resultSemantics,
            NarrowingShiftRequiredPolicyDecisions,
            isNarrowingShift: true,
            requiresSourceDestinationWidthSideband: true,
            requiresElementWidthLmulVlAbi: true,
            requiresNarrowingPolicyAbi: true,
            requiresShiftOperandAbi: true,
            requiresRoundingSaturationTrapPolicy: true,
            requiresTruncationPublicationPolicy: true,
            rejectsClipAverageAliasPromotion: true,
            rejectsBaseShiftAliasPromotion: true);

    private static CompilerVectorVlmBlockedAbiContract CreateConversionRow(
        string mnemonic,
        string abiDecision,
        string dataSemantics,
        string resultSemantics) =>
        new(
            mnemonic,
            CompilerVectorVlmBlockedAbiClass.Conversion,
            "VectorConversionVlmBlocked",
            "VectorWidenNarrowConvertFailClosed",
            abiDecision,
            "vector conversion payload plus source/destination type-domain sideband; no typed facade helper operands",
            dataSemantics,
            resultSemantics,
            ConversionRequiredPolicyDecisions,
            isConversion: true,
            requiresConversionPolicyAbi: true,
            requiresConversionTypeDomainAbi: true,
            requiresElementWidthLmulVlAbi: true,
            requiresRoundingSaturationTrapPolicy: true,
            requiresNanPolicyAbi: true,
            requiresConversionResultFootprintAbi: true,
            rejectsVzextVsextAliasPromotion: true,
            rejectsWidenNarrowArithmeticAliasPromotion: true,
            rejectsScalarConversionAliasPromotion: true);

    private static CompilerVectorVlmBlockedAbiContract CreatePrefixScanMinMaxRow(
        string mnemonic,
        string abiDecision,
        string dataSemantics,
        string resultSemantics) =>
        new(
            mnemonic,
            CompilerVectorVlmBlockedAbiClass.PrefixScanMinMax,
            "VectorPrefixScanMinMaxVlmBlocked",
            "VectorScanContourFailClosed",
            abiDecision,
            "prefix min/max scan payload plus element type and signedness sideband; no typed facade helper operands",
            dataSemantics,
            resultSemantics,
            PrefixScanMinMaxRequiredPolicyDecisions,
            isPrefixScanMinMax: true,
            requiresPrefixScanPolicyAbi: true,
            requiresPrefixMinMaxOrderingPolicy: true,
            requiresInclusiveExclusivePolicy: true,
            requiresActiveVlTailBehaviorPolicy: true,
            requiresElementTypeSideband: true,
            requiresSignednessAbi: true,
            requiresReplayDeterministicPrefixPublication: true,
            rejectsVscanSumAliasPromotion: true,
            rejectsReductionAliasPromotion: true,
            rejectsSegmentMovementAliasPromotion: true);

    private static CompilerVectorVlmBlockedAbiContract CreateStructureMovementRow(
        string mnemonic,
        string abiDecision,
        string dataSemantics,
        string resultSemantics) =>
        new(
            mnemonic,
            CompilerVectorVlmBlockedAbiClass.StructureMovement,
            "VectorStructureMovementVlmBlocked",
            "VectorStructureMovementFailClosed",
            abiDecision,
            "structure movement payload plus shape and element-order sideband; no typed facade helper operands",
            dataSemantics,
            resultSemantics,
            StructureMovementRequiredPolicyDecisions,
            isStructureMovement: true,
            requiresStructureShapeAbi: true,
            requiresShapeOrderingPolicy: true,
            requiresPayloadCanonicalization: true,
            requiresElementOrderPolicy: true,
            requiresActiveVlTailBehaviorPolicy: true,
            requiresStagedVectorPublication: true,
            rejectsMovementPermutationAliasPromotion: true,
            rejectsSegmentMemoryAliasPromotion: true,
            rejectsHiddenStreamEngineFallback: true);

    private static CompilerVectorVlmBlockedAbiContract CreateSegmentMemoryRow(
        string mnemonic,
        int segmentCount,
        bool isLoad)
    {
        string operation = isLoad ? "load" : "store";
        string publication = isLoad ? "staged publication" : "staged commit";

        return new(
            mnemonic,
            CompilerVectorVlmBlockedAbiClass.SegmentMemory,
            "VectorSegmentMemoryVlmBlocked",
            "VectorSegmentMemoryFailClosed",
            $"NoAllocationUntilVectorSegment{segmentCount}{operation}MemoryShapeFaultReplayNoAlias",
            $"segment-{segmentCount} vector {operation} memory payload plus segment shape sideband; no typed facade helper operands",
            $"Segment-{segmentCount} {operation} memory shape, byte ordering, alignment/fault policy, mask/tail behavior, and VLM materialization are not selected.",
            $"Future helper emission requires segment memory ABI, deterministic {publication}, replay, and no base-memory, structure-movement, indexed, or 2D aliasing.",
            SegmentMemoryRequiredPolicyDecisions,
            isSegmentMemory: true,
            isSegmentLoad: isLoad,
            isSegmentStore: !isLoad,
            segmentCount: segmentCount,
            requiresSegmentMemoryShapeAbi: true,
            requiresSegmentCountAbi: true,
            requiresFaultReplayPolicy: true,
            requiresByteOrderingPolicy: true,
            requiresAlignmentFaultPolicy: true,
            requiresSegmentOrderingPolicy: true,
            requiresRetireStagedPublication: isLoad,
            requiresRetireStagedCommit: !isLoad,
            rejectsBaseMemoryOpcodeDuplication: true,
            rejectsStructureMovementAliasPromotion: true,
            rejectsIndexedOr2DMemoryAliasPromotion: true);
    }

    public string Mnemonic { get; }
    public CompilerVectorVlmBlockedAbiClass AbiClass { get; }
    public string ExtensionName { get; }
    public string EvidenceBoundary { get; }
    public string AbiDecision { get; }
    public string OperandShape { get; }
    public string DataSemantics { get; }
    public string ResultSemantics { get; }
    public IReadOnlyList<string> RequiredPolicyDecisions { get; }
    public bool IsPredicateSelectMerge { get; }
    public bool RequiresPredicatePolarityPolicy { get; }
    public bool RequiresSelectMergeResultAbi { get; }
    public bool RequiresPredicateSidebandAbi { get; }
    public bool RejectsCzeroAliasPromotion { get; }
    public bool RejectsMaskPrefixAliasPromotion { get; }
    public bool IsPredicateScalarSummary { get; }
    public bool RequiresScalarPredicateSummaryFootprint { get; }
    public bool RequiresNoActiveElementSentinelPolicy { get; }
    public bool RequiresActiveVlTailSemantics { get; }
    public bool RequiresRetireOwnedScalarPublication { get; }
    public bool RejectsScalarReductionAliasPromotion { get; }
    public bool IsPredicateMaskPrefix { get; }
    public bool RequiresPredicateOnlyDestinationPolicy { get; }
    public bool RequiresIncludingFirstOnlyFirstPolicy { get; }
    public bool RequiresStagedMaskPublication { get; }
    public bool RequiresRollbackEvidence { get; }
    public bool RejectsVmsbfAliasPromotion { get; }
    public bool RejectsSelectMergeAliasPromotion { get; }
    public bool IsSignedExtend { get; }
    public bool RequiresVectorSourceWidthAbi { get; }
    public bool RequiresSignedExtensionPolicy { get; }
    public bool RequiresSignednessSeparationPolicy { get; }
    public bool RejectsVzextAliasPromotion { get; }
    public bool RejectsWidenNarrowConvertAliasPromotion { get; }
    public bool IsWideningArithmetic { get; }
    public bool IsWideningMultiplyAccumulate { get; }
    public bool RequiresSourceDestinationWidthSideband { get; }
    public bool RequiresElementWidthLmulVlAbi { get; }
    public bool RequiresSignednessAbi { get; }
    public bool RequiresWideningOverflowPolicyAbi { get; }
    public bool RequiresAccumulatorFootprintAbi { get; }
    public bool RequiresAccumulatorPrecisionPolicy { get; }
    public bool SeparateFromBaseVectorArithmetic { get; }
    public bool RejectsVzextVsextAliasPromotion { get; }
    public bool RejectsBaseArithmeticAliasPromotion { get; }
    public bool RejectsDotProductAliasPromotion { get; }
    public bool IsNarrowingShift { get; }
    public bool RequiresNarrowingPolicyAbi { get; }
    public bool RequiresShiftOperandAbi { get; }
    public bool RequiresRoundingSaturationTrapPolicy { get; }
    public bool RequiresTruncationPublicationPolicy { get; }
    public bool RejectsClipAverageAliasPromotion { get; }
    public bool RejectsBaseShiftAliasPromotion { get; }
    public bool IsConversion { get; }
    public bool RequiresConversionPolicyAbi { get; }
    public bool RequiresConversionTypeDomainAbi { get; }
    public bool RequiresNanPolicyAbi { get; }
    public bool RequiresConversionResultFootprintAbi { get; }
    public bool RejectsWidenNarrowArithmeticAliasPromotion { get; }
    public bool RejectsScalarConversionAliasPromotion { get; }
    public bool IsPrefixScanMinMax { get; }
    public bool RequiresPrefixScanPolicyAbi { get; }
    public bool RequiresPrefixMinMaxOrderingPolicy { get; }
    public bool RequiresInclusiveExclusivePolicy { get; }
    public bool RequiresActiveVlTailBehaviorPolicy { get; }
    public bool RequiresElementTypeSideband { get; }
    public bool RequiresReplayDeterministicPrefixPublication { get; }
    public bool RejectsVscanSumAliasPromotion { get; }
    public bool RejectsReductionAliasPromotion { get; }
    public bool RejectsSegmentMovementAliasPromotion { get; }
    public bool IsStructureMovement { get; }
    public bool RequiresStructureShapeAbi { get; }
    public bool RequiresShapeOrderingPolicy { get; }
    public bool RequiresPayloadCanonicalization { get; }
    public bool RequiresElementOrderPolicy { get; }
    public bool RequiresStagedVectorPublication { get; }
    public bool RejectsMovementPermutationAliasPromotion { get; }
    public bool RejectsSegmentMemoryAliasPromotion { get; }
    public bool RejectsHiddenStreamEngineFallback { get; }
    public bool IsSegmentMemory { get; }
    public bool IsSegmentLoad { get; }
    public bool IsSegmentStore { get; }
    public int SegmentCount { get; }
    public bool RequiresSegmentMemoryShapeAbi { get; }
    public bool RequiresSegmentCountAbi { get; }
    public bool RequiresFaultReplayPolicy { get; }
    public bool RequiresByteOrderingPolicy { get; }
    public bool RequiresAlignmentFaultPolicy { get; }
    public bool RequiresSegmentOrderingPolicy { get; }
    public bool RequiresRetireStagedPublication { get; }
    public bool RequiresRetireStagedCommit { get; }
    public bool RejectsBaseMemoryOpcodeDuplication { get; }
    public bool RejectsStructureMovementAliasPromotion { get; }
    public bool RejectsIndexedOr2DMemoryAliasPromotion { get; }
    public bool IsFixedPointSaturation { get; }
    public bool RequiresSaturatingPolicyAbi { get; }
    public bool RequiresSignednessWidthClampPolicy { get; }
    public bool RequiresOverflowPolicyAbi { get; }
    public bool RequiresVlmMaterializationPolicy { get; }
    public bool RequiresStagedPublicationRetirePolicy { get; }
    public bool RequiresReplayRollbackGoldenEvidence { get; }
    public bool RequiresSaturatingShiftPolicyAbi { get; }
    public bool RequiresSaturatingShiftMeaningDecision { get; }
    public bool MayRemainReservedIfNonMeaningful { get; }
    public bool SeparateFromClosedVaddSat { get; }
    public bool NoVaddSatFallback { get; }
    public bool NoBaseVectorArithmeticFallback { get; }
    public bool NoBaseVectorShiftFallback { get; }
    public bool NoScalarHelperFallback { get; }
    public bool NoLane6StreamFallback { get; }
    public bool NoLane7AcceleratorFallback { get; }
    public bool NoVmxSpecificPathFallback { get; }
    public bool NoExecutableRowAliasPromotion { get; }
    public bool RejectsSaturatingAddAliasPromotion { get; }
    public bool RejectsAverageClipAliasPromotion { get; }
    public bool RejectsBaseArithmeticOrShiftAliasPromotion { get; }
    public bool IsFixedPointAverageClip { get; }
    public bool IsFixedPointAverage { get; }
    public bool IsRoundedFixedPointAverage { get; }
    public bool IsFixedPointClip { get; }
    public bool RequiresAveragePolicyAbi { get; }
    public bool RequiresRoundingPolicyAbi { get; }
    public bool RequiresRoundingTruncationPolicyAbi { get; }
    public bool RequiresClipBoundsAbi { get; }
    public bool RequiresResultWidthPolicyAbi { get; }
    public bool NoFixedPointSaturationFallback { get; }
    public bool NoNarrowWidenConvertFallback { get; }
    public bool RejectsFixedPointSaturationAliasPromotion { get; }
    public bool IsDotTileVariant { get; }
    public bool IsDotBlockscaleVariant { get; }
    public bool IsDotAccumulatorVariant { get; }
    public bool IsDotWideIntegerVariant { get; }
    public bool RequiresDotVariantAbi { get; }
    public bool RequiresDotTileHelperAbi { get; }
    public bool RequiresAccumulatorPrecisionAbi { get; }
    public bool RequiresAccumulatorResultFootprintAbi { get; }
    public bool RequiresScaleMetadataAbi { get; }
    public bool RequiresSeparateResultSurfaceAbi { get; }
    public bool RequiresWiderIntegerContourAbi { get; }
    public bool RequiresDeterministicOrderingReplayPolicy { get; }
    public bool SeparateFromScopedVdotWide { get; }
    public bool NoScopedVdotWideFallback { get; }
    public bool NoNameOnlyVdotWideExtension { get; }
    public bool NoBaseDotProductFallback { get; }
    public bool NoWideningFmaFallback { get; }
    public bool NoLane6DescriptorFallback { get; }
    public bool NoMatrixTileFallback { get; }
    public bool NoHostOwnedEvidencePublication { get; }
    public bool RuntimeExecutable => false;
    public bool HasRuntimeOpcodeAllocation => false;
    public bool HasRuntimeConformanceEvidence => false;
    public bool HasVectorLegalityMatrixEvidence => false;
    public bool HasRuntimeMaterializerEvidence => false;
    public bool RawVectorTransportAllowedForThisContour => false;
    public bool RawVectorTransportIsHelperAuthority => false;
    public bool CompilerEmissionAllowed => false;
    public bool CompilerHelperAllowed => false;
    public bool TypedFacadeAllowed => false;
    public bool TypedHelperAllowed => false;
    public bool RequiresVectorHelperAbi => true;
    public bool RequiresVlmLegalityEvidence => true;
    public bool RequiresRuntimeMaterializerEvidence => true;
    public bool RequiresMaskTailPolicy => true;
    public bool RequiresResultFootprintAbi => true;
    public bool RequiresRetireReplayGoldenConformance => true;
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
            CompilerVectorVlmBlockedAbiClass.PredicateSelectMerge =>
                "predicate select/merge helper ABI, polarity, predicate sideband, mask/tail, VLM materialization, replay, and no CZERO or mask-prefix alias decisions",
            CompilerVectorVlmBlockedAbiClass.PredicateScalarSummary =>
                "scalar predicate-summary footprint, no-active sentinel policy, active VL/tail semantics, retire publication, replay, and no scalar reduction alias decisions",
            CompilerVectorVlmBlockedAbiClass.PredicateMaskPrefix =>
                "predicate-only mask-prefix ABI, including-first/only-first policy, staged publication, rollback, and no VMSBF or select/merge alias decisions",
            CompilerVectorVlmBlockedAbiClass.SignedExtend =>
                "signed source-width helper ABI, signed-extension policy, VLM materialization, mask/tail, replay, and no VZEXT/widen/narrow/convert alias decisions",
            CompilerVectorVlmBlockedAbiClass.WideningArithmetic =>
                "widening arithmetic helper ABI, source/destination width sideband, LMUL/VL policy, signedness, overflow/result footprint, replay, and no VZEXT/VSEXT or base arithmetic alias decisions",
            CompilerVectorVlmBlockedAbiClass.FixedPointSaturation =>
                "fixed-point saturation helper ABI, signedness/width/clamp policy, overflow policy, shift policy where applicable, VLM materialization, staged retire publication, replay/rollback/golden evidence, and no VADD.SAT, base arithmetic/shift, scalar helper, Lane6 stream, Lane7 accelerator, VMX-specific, or executable-row alias decisions",
            CompilerVectorVlmBlockedAbiClass.FixedPointAverageClip =>
                "fixed-point average/clip helper ABI, average or bounds/result-width policy, rounding/truncation, signedness, VLM materialization, staged retire publication, replay/rollback/golden evidence, and no VADD.SAT, fixed-point saturation, base arithmetic/shift, narrowing/conversion, scalar helper, Lane6 stream, Lane7 accelerator, VMX-specific, or executable-row alias decisions",
            CompilerVectorVlmBlockedAbiClass.DotTileVariant =>
                "variant-specific dot/tile helper ABI, accumulator precision/result footprint, scale metadata or wide-integer contour policy where applicable, deterministic ordering, VLM materialization, staged retire publication, replay/rollback/golden evidence, and no scoped VDOT.WIDE, base dot-product, widening/FMA, Lane6 descriptor, matrix/tile, scalar helper, Lane7 accelerator, VMX-specific, or executable-row alias decisions",
            CompilerVectorVlmBlockedAbiClass.WideningMultiplyAccumulate =>
                "widening multiply-accumulate helper ABI, accumulator footprint, source/destination width sideband, LMUL/VL policy, overflow/result footprint, replay, and no dot-product or base arithmetic alias decisions",
            CompilerVectorVlmBlockedAbiClass.NarrowingShift =>
                "narrowing shift helper ABI, source/destination width sideband, shift operand, truncation/rounding/saturation/trap policy, replay, and no clip/average or base-shift alias decisions",
            CompilerVectorVlmBlockedAbiClass.Conversion =>
                "conversion helper ABI, source/destination type-domain sideband, rounding/saturation/trap policy, NaN policy, result footprint, replay, and no VZEXT/VSEXT, widening/narrowing, or scalar conversion alias decisions",
            CompilerVectorVlmBlockedAbiClass.PrefixScanMinMax =>
                "prefix min/max scan helper ABI, ordering, inclusive/exclusive policy, active VL/tail behavior, signedness/type sideband, deterministic prefix publication, replay, and no VSCAN.SUM, reduction, or segment/movement alias decisions",
            CompilerVectorVlmBlockedAbiClass.StructureMovement =>
                "structure movement helper ABI, shape/order sideband, payload canonicalization, active VL/tail behavior, staged publication, replay, and no movement-permutation, segment-memory, or StreamEngine fallback alias decisions",
            CompilerVectorVlmBlockedAbiClass.SegmentMemory =>
                "segment memory helper ABI, segment count, memory shape, byte ordering, alignment/fault policy, staged publication or commit, replay, and no base-memory, structure-movement, indexed, or 2D alias decisions",
            _ => "required vector VLM/runtime-blocked ABI decisions"
        };

        throw new InvalidOperationException(
            $"{Mnemonic} typed compiler helper emission is blocked until {requiredDecisions} are explicit.");
    }
}
