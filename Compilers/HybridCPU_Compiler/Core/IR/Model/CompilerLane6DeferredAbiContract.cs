using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR;

public enum CompilerLane6DeferredAbiClass : byte
{
    QueueControl = 0,
    CapabilityQuery = 1,
    DescriptorParserV2 = 2,
    DescriptorOp = 3,
    DescriptorShape = 4
}

/// <summary>
/// Compiler-visible Lane6 contract for queue/query/DSC2 rows that must remain no-emission.
/// </summary>
public sealed class CompilerLane6DeferredAbiContract
{
    private static readonly string[] QueueControlRequiredPolicyDecisions =
    [
        "QueueCommandEncoding",
        "QueueAuthority",
        "TokenNamespaceAbi",
        "QueueHandleAbi",
        "TokenLifecycleAbi",
        "QueueOwnershipModel",
        "QueueStateModel",
        "QueueRollbackJournal",
        "QueueRuntimeAdmission",
        "TypedQueueMicroOp",
        "RetireOwnedPublication",
        "RetireOwnedSideEffectPublication",
        "ReplayRollbackConformance",
        "NoRetirePublicationBeforeQueueAuthority",
        "NoDmaStreamComputeFallback",
        "NoDscStatusFallback",
        "NoDscQueryCapsFallback",
        "NoDsc2Fallback",
        "NoLane7Fallback",
        "NoExternalBackendFallback"
    ];

    private static readonly string[] DscPollRequiredPolicyDecisions =
    [
        .. QueueControlRequiredPolicyDecisions,
        "DscPollCompletionSemantics"
    ];

    private static readonly string[] DscWaitRequiredPolicyDecisions =
    [
        .. QueueControlRequiredPolicyDecisions,
        "CommandScopeAbi",
        "WaitProgressFairnessPolicy"
    ];

    private static readonly string[] DscCancelRequiredPolicyDecisions =
    [
        .. QueueControlRequiredPolicyDecisions,
        "CommandScopeAbi",
        "CancelRollbackJournal"
    ];

    private static readonly string[] DscFenceRequiredPolicyDecisions =
    [
        .. QueueControlRequiredPolicyDecisions,
        "QueueOrderingAbi",
        "FenceCompletionOrderingModel"
    ];

    private static readonly string[] DscCommitRequiredPolicyDecisions =
    [
        .. QueueControlRequiredPolicyDecisions,
        "StagedCommitAuthority",
        "CommitPublicationModel"
    ];

    private static readonly string[] CapabilityQueryRequiredPolicyDecisions =
    [
        "CapabilityQueryAbi",
        "QuerySelectorAbi",
        "CapabilityResultAbi",
        "BoundedResultFootprint",
        "ResultScrubbingPolicy",
        "RetireOwnedPublication",
        "ReplayStableResult",
        "ReplayRollbackConformance",
        "NoHostEvidenceLeak",
        "NoRetirePublicationBeforeQueryAuthority",
        "NoDscQueryCapsFallback",
        "NoLane7Fallback",
        "NoExternalBackendFallback"
    ];

    private static readonly string[] BackendQueryRequiredPolicyDecisions =
    [
        .. CapabilityQueryRequiredPolicyDecisions,
        "BackendCapabilityAbi"
    ];

    private static readonly string[] ShapeQueryRequiredPolicyDecisions =
    [
        .. CapabilityQueryRequiredPolicyDecisions,
        "ShapeQueryAbi"
    ];

    private static readonly string[] DescriptorParserV2RequiredPolicyDecisions =
    [
        "DescriptorV2Adr",
        "DescriptorV2ParserManifest",
        "BackwardCompatibleDecoder",
        "DescriptorV2ExecutionPolicy",
        "DescriptorV2AdmissionPolicy",
        "RuntimeAdmission",
        "RetireCommitAuthority",
        "DescriptorV2RetireReplayPolicy",
        "ReplayDeterminism",
        "ParserOnlyConformance",
        "DescriptorV2GoldenArtifacts",
        "NoDsc2ExecutionBeforeAdr",
        "ParserAcceptanceIsNotExecutionEvidence",
        "NoParserToExecutionPromotion",
        "NoDmaStreamComputeFallback",
        "NoQueueRuntimeFallback",
        "NoLane7Fallback",
        "NoExternalBackendFallback"
    ];

    private static readonly string[] DescriptorMetadataNoEmissionRequiredPolicyDecisions =
    [
        "DescriptorParserOnlyStatus",
        "NoOpcodeAllocation",
        "NoCompilerEmission",
        "NoCompilerHelperAuthority",
        "DescriptorPayloadAbi",
        "TypedDescriptorProjection",
        "DescriptorMaterializer",
        "BackendRuntimeAdmission",
        "RuntimeExecutionEvidenceAbsent",
        "RuntimeOwnedLegalityFinal",
        "RetireCommitAuthority",
        "ReplayRollbackConformance",
        "GoldenArtifacts",
        "NoHiddenScalarLowering",
        "NoHiddenVectorLowering",
        "NoGenericDmaStreamComputeFallbackAsAuthority",
        "NoDsc2Fallback",
        "NoLane7Fallback",
        "NoExternalBackendFallback",
        "NoVmxSpecificPath"
    ];

    private static readonly string[] DescriptorOpRequiredPolicyDecisions =
    [
        .. DescriptorMetadataNoEmissionRequiredPolicyDecisions,
        "DescriptorOpAbi",
        "DescriptorOpTypeAbi",
        "DescriptorParserValidation"
    ];

    private static readonly string[] DescriptorShapeRequiredPolicyDecisions =
    [
        .. DescriptorMetadataNoEmissionRequiredPolicyDecisions,
        "ShapeAbi",
        "ShapeEnumAbi",
        "ShapeParserManifest",
        "ShapeFaultModel",
        "AliasOverlapPolicy"
    ];

    private CompilerLane6DeferredAbiContract(
        string mnemonic,
        CompilerLane6DeferredAbiClass abiClass,
        string extensionName,
        string evidenceBoundary,
        string abiDecision,
        string operandShape,
        string dataSemantics,
        string resultSemantics,
        IReadOnlyList<string> requiredPolicyDecisions,
        bool isQueueControl = false,
        bool requiresQueueAuthority = false,
        bool requiresTokenNamespaceAbi = false,
        bool requiresQueueHandleAbi = false,
        bool requiresTokenLifecycleAbi = false,
        bool requiresQueueOwnershipModel = false,
        bool requiresQueueStateModel = false,
        bool requiresQueueRollbackJournal = false,
        bool requiresQueueRuntimeAdmission = false,
        bool requiresQueueCommandEncoding = false,
        bool requiresCommandScopeAbi = false,
        bool requiresQueueOrderingAbi = false,
        bool requiresStagedCommitAuthority = false,
        bool requiresDscPollCompletionSemantics = false,
        bool requiresWaitProgressFairnessPolicy = false,
        bool requiresCancelRollbackJournal = false,
        bool requiresFenceCompletionOrderingModel = false,
        bool requiresCommitPublicationModel = false,
        bool isCapabilityQuery = false,
        bool isReadOnlyQuery = false,
        bool requiresCapabilityQueryAbi = false,
        bool requiresQuerySelectorAbi = false,
        bool requiresCapabilityResultAbi = false,
        bool requiresBoundedResultFootprint = false,
        bool requiresResultScrubbingPolicy = false,
        bool requiresReplayStableResult = false,
        bool requiresBackendCapabilityAbi = false,
        bool requiresShapeQueryAbi = false,
        bool isDescriptorOwned = false,
        bool isCarrierOnly = false,
        bool isParserOnly = false,
        bool isDescriptorOnly = false,
        bool isDescriptorParserOnlyBoundary = false,
        bool isDescriptorOp = false,
        bool isDescriptorShape = false,
        bool requiresDescriptorOpAbi = false,
        bool requiresShapeAbi = false,
        bool requiresDescriptorPayloadAbi = false,
        bool requiresTypedDescriptorProjection = false,
        bool requiresDescriptorMaterializer = false,
        bool requiresDescriptorParserValidation = false,
        bool requiresDescriptorOpTypeAbi = false,
        bool requiresShapeEnumAbi = false,
        bool requiresShapeParserManifest = false,
        bool requiresShapeFaultModel = false,
        bool requiresAliasOverlapPolicy = false,
        bool requiresArithmeticPolicyAbi = false,
        bool requiresSignednessTypePolicyAbi = false,
        bool requiresOverflowPolicyAbi = false,
        bool requiresBoundsPolicyAbi = false,
        bool requiresConversionPolicyAbi = false,
        bool requiresRoundingSaturationTrapPolicy = false,
        bool requiresPredicateFootprintAbi = false,
        bool requiresSelectResultFootprintAbi = false,
        bool requiresReductionResultFootprintAbi = false,
        bool requiresScalarOrSurfaceResultPolicy = false,
        bool requiresStrideAbi = false,
        bool requiresTileShapeAbi = false,
        bool requiresIndexSurfaceAbi = false,
        bool requiresTwoDimensionalShapeAbi = false,
        bool requiresMultiRangeAbi = false,
        bool requiresBackendRuntimeAdmission = false,
        bool runtimeExecutionEvidenceAbsent = false,
        bool requiresRetireReplayGoldenEvidence = false,
        bool runtimeOwnedLegalityIsFinal = false,
        bool requiresDescriptorV2Adr = false,
        bool requiresDescriptorV2ParserManifest = false,
        bool requiresBackwardCompatibleDecoder = false,
        bool requiresDescriptorV2ExecutionPolicy = false,
        bool requiresDescriptorV2AdmissionPolicy = false,
        bool requiresRuntimeAdmission = false,
        bool requiresRetireCommitAuthority = false,
        bool requiresDescriptorV2RetireReplayPolicy = false,
        bool requiresReplayDeterminism = false,
        bool requiresParserOnlyConformance = false,
        bool requiresDescriptorV2GoldenArtifacts = false,
        bool noDsc2ExecutionBeforeAdr = false,
        bool parserAcceptanceIsNotExecutionEvidence = false,
        bool noParserToExecutionPromotion = false,
        bool requiresDecoderEncoderAbi = false,
        bool requiresInstructionIrProjection = false,
        bool requiresRegistryMaterializer = false,
        bool requiresSchedulerLaneBinding = false,
        bool requiresRetireOwnedPublication = false,
        bool requiresRetireOwnedSideEffectPublication = false,
        bool requiresReplayRollbackConformance = false,
        bool requiresGoldenArtifacts = false,
        bool requiresFutureVirtualizationBoundaryPolicy = false,
        bool requiresNoHostEvidenceLeak = false,
        bool noRetirePublicationBeforeQueueAuthority = false,
        bool noRetirePublicationBeforeQueryAuthority = false,
        bool noGuestVisibleHostEvidence = false,
        bool noHostEvidenceLeak = false,
        bool noHostOwnedEvidencePublication = false,
        bool existingDmaStreamComputeEvidenceIsInsufficient = false,
        bool existingDscStatusEvidenceIsInsufficient = false,
        bool existingDscQueryCapsEvidenceIsInsufficient = false,
        bool dsc2ParserEvidenceIsInsufficient = false,
        bool phase10DescriptorOpEvidenceIsInsufficient = false,
        bool noScalarOpcodePublication = false,
        bool noDecoderEncoderAbiPublication = false,
        bool noExecutableDecoderEncoderAbiPublication = false,
        bool noInstructionIrProjectionPublication = false,
        bool noRegistryMaterializerPublication = false,
        bool noTypedMicroOpPublication = false,
        bool noSchedulerLaneBindingPublication = false,
        bool noRuntimeAdmissionPublication = false,
        bool noExecutionCapturePublication = false,
        bool noRetireCommitPublication = false,
        bool noReplayRollbackPublication = false,
        bool noCompilerHelperEmission = false,
        bool noHiddenScalarLowering = false,
        bool noHiddenVectorLowering = false,
        bool noMultiOpEmission = false,
        bool noDmaStreamComputeFallback = false,
        bool noGenericDmaStreamComputeFallbackAsAuthority = false,
        bool noDscStatusFallback = false,
        bool noDscQueryCapsFallback = false,
        bool noDsc2Fallback = false,
        bool noQueueRuntimeFallback = false,
        bool noLane7Fallback = false,
        bool noExternalBackendFallback = false,
        bool noVmxSpecificPath = false)
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
        IsQueueControl = isQueueControl;
        RequiresQueueAuthority = requiresQueueAuthority;
        RequiresTokenNamespaceAbi = requiresTokenNamespaceAbi;
        RequiresQueueHandleAbi = requiresQueueHandleAbi;
        RequiresTokenLifecycleAbi = requiresTokenLifecycleAbi;
        RequiresQueueOwnershipModel = requiresQueueOwnershipModel;
        RequiresQueueStateModel = requiresQueueStateModel;
        RequiresQueueRollbackJournal = requiresQueueRollbackJournal;
        RequiresQueueRuntimeAdmission = requiresQueueRuntimeAdmission;
        RequiresQueueCommandEncoding = requiresQueueCommandEncoding;
        RequiresCommandScopeAbi = requiresCommandScopeAbi;
        RequiresQueueOrderingAbi = requiresQueueOrderingAbi;
        RequiresStagedCommitAuthority = requiresStagedCommitAuthority;
        RequiresDscPollCompletionSemantics = requiresDscPollCompletionSemantics;
        RequiresWaitProgressFairnessPolicy = requiresWaitProgressFairnessPolicy;
        RequiresCancelRollbackJournal = requiresCancelRollbackJournal;
        RequiresFenceCompletionOrderingModel = requiresFenceCompletionOrderingModel;
        RequiresCommitPublicationModel = requiresCommitPublicationModel;
        IsCapabilityQuery = isCapabilityQuery;
        IsReadOnlyQuery = isReadOnlyQuery;
        RequiresCapabilityQueryAbi = requiresCapabilityQueryAbi;
        RequiresQuerySelectorAbi = requiresQuerySelectorAbi;
        RequiresCapabilityResultAbi = requiresCapabilityResultAbi;
        RequiresBoundedResultFootprint = requiresBoundedResultFootprint;
        RequiresResultScrubbingPolicy = requiresResultScrubbingPolicy;
        RequiresReplayStableResult = requiresReplayStableResult;
        RequiresBackendCapabilityAbi = requiresBackendCapabilityAbi;
        RequiresShapeQueryAbi = requiresShapeQueryAbi;
        IsDescriptorOwned = isDescriptorOwned;
        IsCarrierOnly = isCarrierOnly;
        IsParserOnly = isParserOnly;
        IsDescriptorOnly = isDescriptorOnly;
        IsDescriptorParserOnlyBoundary = isDescriptorParserOnlyBoundary;
        IsDescriptorOp = isDescriptorOp;
        IsDescriptorShape = isDescriptorShape;
        RequiresDescriptorOpAbi = requiresDescriptorOpAbi;
        RequiresShapeAbi = requiresShapeAbi;
        RequiresDescriptorPayloadAbi = requiresDescriptorPayloadAbi;
        RequiresTypedDescriptorProjection = requiresTypedDescriptorProjection;
        RequiresDescriptorMaterializer = requiresDescriptorMaterializer;
        RequiresDescriptorParserValidation = requiresDescriptorParserValidation;
        RequiresDescriptorOpTypeAbi = requiresDescriptorOpTypeAbi;
        RequiresShapeEnumAbi = requiresShapeEnumAbi;
        RequiresShapeParserManifest = requiresShapeParserManifest;
        RequiresShapeFaultModel = requiresShapeFaultModel;
        RequiresAliasOverlapPolicy = requiresAliasOverlapPolicy;
        RequiresArithmeticPolicyAbi = requiresArithmeticPolicyAbi;
        RequiresSignednessTypePolicyAbi = requiresSignednessTypePolicyAbi;
        RequiresOverflowPolicyAbi = requiresOverflowPolicyAbi;
        RequiresBoundsPolicyAbi = requiresBoundsPolicyAbi;
        RequiresConversionPolicyAbi = requiresConversionPolicyAbi;
        RequiresRoundingSaturationTrapPolicy = requiresRoundingSaturationTrapPolicy;
        RequiresPredicateFootprintAbi = requiresPredicateFootprintAbi;
        RequiresSelectResultFootprintAbi = requiresSelectResultFootprintAbi;
        RequiresReductionResultFootprintAbi = requiresReductionResultFootprintAbi;
        RequiresScalarOrSurfaceResultPolicy = requiresScalarOrSurfaceResultPolicy;
        RequiresStrideAbi = requiresStrideAbi;
        RequiresTileShapeAbi = requiresTileShapeAbi;
        RequiresIndexSurfaceAbi = requiresIndexSurfaceAbi;
        RequiresTwoDimensionalShapeAbi = requiresTwoDimensionalShapeAbi;
        RequiresMultiRangeAbi = requiresMultiRangeAbi;
        RequiresBackendRuntimeAdmission = requiresBackendRuntimeAdmission;
        RuntimeExecutionEvidenceAbsent = runtimeExecutionEvidenceAbsent;
        RequiresRetireReplayGoldenEvidence = requiresRetireReplayGoldenEvidence;
        RuntimeOwnedLegalityIsFinal = runtimeOwnedLegalityIsFinal;
        RequiresDescriptorV2Adr = requiresDescriptorV2Adr;
        RequiresDescriptorV2ParserManifest = requiresDescriptorV2ParserManifest;
        RequiresBackwardCompatibleDecoder = requiresBackwardCompatibleDecoder;
        RequiresDescriptorV2ExecutionPolicy = requiresDescriptorV2ExecutionPolicy;
        RequiresDescriptorV2AdmissionPolicy = requiresDescriptorV2AdmissionPolicy;
        RequiresRuntimeAdmission = requiresRuntimeAdmission;
        RequiresRetireCommitAuthority = requiresRetireCommitAuthority;
        RequiresDescriptorV2RetireReplayPolicy = requiresDescriptorV2RetireReplayPolicy;
        RequiresReplayDeterminism = requiresReplayDeterminism;
        RequiresParserOnlyConformance = requiresParserOnlyConformance;
        RequiresDescriptorV2GoldenArtifacts = requiresDescriptorV2GoldenArtifacts;
        NoDsc2ExecutionBeforeAdr = noDsc2ExecutionBeforeAdr;
        ParserAcceptanceIsNotExecutionEvidence = parserAcceptanceIsNotExecutionEvidence;
        NoParserToExecutionPromotion = noParserToExecutionPromotion;
        RequiresDecoderEncoderAbi = requiresDecoderEncoderAbi;
        RequiresInstructionIrProjection = requiresInstructionIrProjection;
        RequiresRegistryMaterializer = requiresRegistryMaterializer;
        RequiresSchedulerLaneBinding = requiresSchedulerLaneBinding;
        RequiresRetireOwnedPublication = requiresRetireOwnedPublication;
        RequiresRetireOwnedSideEffectPublication = requiresRetireOwnedSideEffectPublication;
        RequiresReplayRollbackConformance = requiresReplayRollbackConformance;
        RequiresGoldenArtifacts = requiresGoldenArtifacts;
        RequiresFutureVirtualizationBoundaryPolicy = requiresFutureVirtualizationBoundaryPolicy;
        RequiresNoHostEvidenceLeak = requiresNoHostEvidenceLeak;
        NoRetirePublicationBeforeQueueAuthority = noRetirePublicationBeforeQueueAuthority;
        NoRetirePublicationBeforeQueryAuthority = noRetirePublicationBeforeQueryAuthority;
        NoGuestVisibleHostEvidence = noGuestVisibleHostEvidence;
        NoHostEvidenceLeak = noHostEvidenceLeak;
        NoHostOwnedEvidencePublication = noHostOwnedEvidencePublication;
        ExistingDmaStreamComputeEvidenceIsInsufficient = existingDmaStreamComputeEvidenceIsInsufficient;
        ExistingDscStatusEvidenceIsInsufficient = existingDscStatusEvidenceIsInsufficient;
        ExistingDscQueryCapsEvidenceIsInsufficient = existingDscQueryCapsEvidenceIsInsufficient;
        Dsc2ParserEvidenceIsInsufficient = dsc2ParserEvidenceIsInsufficient;
        Phase10DescriptorOpEvidenceIsInsufficient = phase10DescriptorOpEvidenceIsInsufficient;
        NoScalarOpcodePublication = noScalarOpcodePublication;
        NoDecoderEncoderAbiPublication = noDecoderEncoderAbiPublication;
        NoExecutableDecoderEncoderAbiPublication = noExecutableDecoderEncoderAbiPublication;
        NoInstructionIrProjectionPublication = noInstructionIrProjectionPublication;
        NoRegistryMaterializerPublication = noRegistryMaterializerPublication;
        NoTypedMicroOpPublication = noTypedMicroOpPublication;
        NoSchedulerLaneBindingPublication = noSchedulerLaneBindingPublication;
        NoRuntimeAdmissionPublication = noRuntimeAdmissionPublication;
        NoExecutionCapturePublication = noExecutionCapturePublication;
        NoRetireCommitPublication = noRetireCommitPublication;
        NoReplayRollbackPublication = noReplayRollbackPublication;
        NoCompilerHelperEmission = noCompilerHelperEmission;
        NoHiddenScalarLowering = noHiddenScalarLowering;
        NoHiddenVectorLowering = noHiddenVectorLowering;
        NoMultiOpEmission = noMultiOpEmission;
        NoDmaStreamComputeFallback = noDmaStreamComputeFallback;
        NoGenericDmaStreamComputeFallbackAsAuthority = noGenericDmaStreamComputeFallbackAsAuthority;
        NoDscStatusFallback = noDscStatusFallback;
        NoDscQueryCapsFallback = noDscQueryCapsFallback;
        NoDsc2Fallback = noDsc2Fallback;
        NoQueueRuntimeFallback = noQueueRuntimeFallback;
        NoLane7Fallback = noLane7Fallback;
        NoExternalBackendFallback = noExternalBackendFallback;
        NoVmxSpecificPath = noVmxSpecificPath;
    }

    public static CompilerLane6DeferredAbiContract DscPoll { get; } =
        CreateQueueControlRow(
            "DSC_POLL",
            "NoAllocationUntilDscPollQueueTokenCompletionRetireReplayAbi",
            "Queue poll command, token completion authority, and nonblocking result semantics are not selected.",
            "Future execution may observe queue completion only after token lifecycle, replay, and retire publication close.",
            DscPollRequiredPolicyDecisions,
            requiresDscPollCompletionSemantics: true);

    public static CompilerLane6DeferredAbiContract DscWait { get; } =
        CreateQueueControlRow(
            "DSC_WAIT",
            "NoAllocationUntilDscWaitQueueTokenScopeProgressRetireReplayAbi",
            "Queue wait scope, progress/fairness policy, and token lifecycle authority are not selected.",
            "Future execution may block or wait only after explicit progress, replay, and retire publication policy close.",
            DscWaitRequiredPolicyDecisions,
            requiresCommandScopeAbi: true,
            requiresWaitProgressFairnessPolicy: true);

    public static CompilerLane6DeferredAbiContract DscCancel { get; } =
        CreateQueueControlRow(
            "DSC_CANCEL",
            "NoAllocationUntilDscCancelQueueTokenRollbackRetireReplayAbi",
            "Queue cancel scope, rollback journal, and token lifecycle authority are not selected.",
            "Future execution may publish cancel side effects only through queue authority, rollback, and retire-owned side effects.",
            DscCancelRequiredPolicyDecisions,
            requiresCommandScopeAbi: true,
            requiresCancelRollbackJournal: true);

    public static CompilerLane6DeferredAbiContract DscFence { get; } =
        CreateQueueControlRow(
            "DSC_FENCE",
            "NoAllocationUntilDscFenceQueueOrderingRetireReplayAbi",
            "Queue fence ordering scope and completion model are not selected.",
            "Future execution may publish queue-ordering side effects only after ordering, replay, and retire publication close.",
            DscFenceRequiredPolicyDecisions,
            requiresQueueOrderingAbi: true,
            requiresFenceCompletionOrderingModel: true);

    public static CompilerLane6DeferredAbiContract DscCommit { get; } =
        CreateQueueControlRow(
            "DSC_COMMIT",
            "NoAllocationUntilDscCommitStagedQueueRetireReplayAbi",
            "Staged commit authority and queue publication model are not selected.",
            "Future execution may publish committed queue side effects only after staged authority and retire ownership close.",
            DscCommitRequiredPolicyDecisions,
            requiresStagedCommitAuthority: true,
            requiresCommitPublicationModel: true);

    public static CompilerLane6DeferredAbiContract DscQueryBackend { get; } =
        CreateCapabilityQueryRow(
            "DSC_QUERY_BACKEND",
            "NoAllocationUntilBackendCapabilityResultScrubbingReplayPublicationAbi",
            "Backend capability selector ABI, bounded result footprint, and host-evidence scrub policy are not selected.",
            "Future execution may publish backend capability data only through bounded, scrubbed, replay-stable retire publication.",
            BackendQueryRequiredPolicyDecisions,
            requiresBackendCapabilityAbi: true);

    public static CompilerLane6DeferredAbiContract DscQueryShape { get; } =
        CreateCapabilityQueryRow(
            "DSC_QUERY_SHAPE",
            "NoAllocationUntilShapeCapabilityResultScrubbingReplayPublicationAbi",
            "Shape query selector ABI, bounded result footprint, and host-evidence scrub policy are not selected.",
            "Future execution may publish shape capability data only through bounded, scrubbed, replay-stable retire publication.",
            ShapeQueryRequiredPolicyDecisions,
            requiresShapeQueryAbi: true);

    public static CompilerLane6DeferredAbiContract Dsc2 { get; } =
        new(
            "DSC2",
            CompilerLane6DeferredAbiClass.DescriptorParserV2,
            "Lane6DSC",
            "ParserOnlyCarrierNoExecution",
            "NoAllocationUntilDescriptorV2AdrParserManifestRuntimeAuthorityRetireReplayAbi",
            "descriptor-v2 sideband carrier; no executable instruction operands",
            "Descriptor-v2 parser acceptance and footprint metadata are not execution authority.",
            "Future execution may publish descriptor-v2 effects only after ADR, runtime admission, retire, replay, and golden evidence.",
            DescriptorParserV2RequiredPolicyDecisions,
            isDescriptorOwned: true,
            isCarrierOnly: true,
            isParserOnly: true,
            isDescriptorParserOnlyBoundary: true,
            runtimeExecutionEvidenceAbsent: true,
            requiresRetireReplayGoldenEvidence: true,
            runtimeOwnedLegalityIsFinal: true,
            requiresDescriptorV2Adr: true,
            requiresDescriptorV2ParserManifest: true,
            requiresBackwardCompatibleDecoder: true,
            requiresDescriptorV2ExecutionPolicy: true,
            requiresDescriptorV2AdmissionPolicy: true,
            requiresRuntimeAdmission: true,
            requiresRetireCommitAuthority: true,
            requiresDescriptorV2RetireReplayPolicy: true,
            requiresReplayDeterminism: true,
            requiresParserOnlyConformance: true,
            requiresDescriptorV2GoldenArtifacts: true,
            noDsc2ExecutionBeforeAdr: true,
            parserAcceptanceIsNotExecutionEvidence: true,
            noParserToExecutionPromotion: true,
            requiresFutureVirtualizationBoundaryPolicy: true,
            requiresNoHostEvidenceLeak: true,
            noGuestVisibleHostEvidence: true,
            noHostEvidenceLeak: true,
            noHostOwnedEvidencePublication: true,
            existingDmaStreamComputeEvidenceIsInsufficient: true,
            existingDscStatusEvidenceIsInsufficient: true,
            existingDscQueryCapsEvidenceIsInsufficient: true,
            phase10DescriptorOpEvidenceIsInsufficient: true,
            noScalarOpcodePublication: true,
            noExecutableDecoderEncoderAbiPublication: true,
            noInstructionIrProjectionPublication: true,
            noRegistryMaterializerPublication: true,
            noTypedMicroOpPublication: true,
            noSchedulerLaneBindingPublication: true,
            noRuntimeAdmissionPublication: true,
            noExecutionCapturePublication: true,
            noRetireCommitPublication: true,
            noReplayRollbackPublication: true,
            noCompilerHelperEmission: true,
            noHiddenScalarLowering: true,
            noHiddenVectorLowering: true,
            noMultiOpEmission: true,
            noDmaStreamComputeFallback: true,
            noGenericDmaStreamComputeFallbackAsAuthority: true,
            noDscStatusFallback: true,
            noDscQueryCapsFallback: true,
            noQueueRuntimeFallback: true,
            noLane7Fallback: true,
            noExternalBackendFallback: true,
            noVmxSpecificPath: true);

    public static IReadOnlyList<CompilerLane6DeferredAbiContract> DescriptorOpRows { get; } =
    [
        CreateDescriptorOpRow(
            "DmaStreamCompute.SUB",
            WithPolicies(DescriptorOpRequiredPolicyDecisions, "ArithmeticPolicyAbi"),
            requiresArithmeticPolicyAbi: true),
        CreateDescriptorOpRow(
            "DmaStreamCompute.MIN",
            WithPolicies(DescriptorOpRequiredPolicyDecisions, "SignednessTypePolicyAbi"),
            requiresSignednessTypePolicyAbi: true),
        CreateDescriptorOpRow(
            "DmaStreamCompute.MAX",
            WithPolicies(DescriptorOpRequiredPolicyDecisions, "SignednessTypePolicyAbi"),
            requiresSignednessTypePolicyAbi: true),
        CreateDescriptorOpRow(
            "DmaStreamCompute.ABSDIFF",
            WithPolicies(DescriptorOpRequiredPolicyDecisions, "OverflowPolicyAbi"),
            requiresOverflowPolicyAbi: true),
        CreateDescriptorOpRow(
            "DmaStreamCompute.CLAMP",
            WithPolicies(DescriptorOpRequiredPolicyDecisions, "BoundsPolicyAbi"),
            requiresBoundsPolicyAbi: true),
        CreateDescriptorOpRow(
            "DmaStreamCompute.CONVERT",
            WithPolicies(DescriptorOpRequiredPolicyDecisions, "ConversionPolicyAbi", "RoundingSaturationTrapPolicy"),
            requiresConversionPolicyAbi: true,
            requiresRoundingSaturationTrapPolicy: true),
        CreateDescriptorOpRow(
            "DmaStreamCompute.COMPARE",
            WithPolicies(DescriptorOpRequiredPolicyDecisions, "PredicateFootprintAbi"),
            requiresPredicateFootprintAbi: true),
        CreateDescriptorOpRow(
            "DmaStreamCompute.SELECT",
            WithPolicies(DescriptorOpRequiredPolicyDecisions, "PredicateFootprintAbi", "SelectResultFootprintAbi"),
            requiresPredicateFootprintAbi: true,
            requiresSelectResultFootprintAbi: true),
        CreateDescriptorOpRow(
            "DmaStreamCompute.REDUCE_SUM",
            WithPolicies(DescriptorOpRequiredPolicyDecisions, "ReductionResultFootprintAbi", "ScalarOrSurfaceResultPolicy"),
            requiresReductionResultFootprintAbi: true,
            requiresScalarOrSurfaceResultPolicy: true),
        CreateDescriptorOpRow(
            "DmaStreamCompute.REDUCE_MIN",
            WithPolicies(DescriptorOpRequiredPolicyDecisions, "ReductionResultFootprintAbi", "ScalarOrSurfaceResultPolicy"),
            requiresReductionResultFootprintAbi: true,
            requiresScalarOrSurfaceResultPolicy: true),
        CreateDescriptorOpRow(
            "DmaStreamCompute.REDUCE_MAX",
            WithPolicies(DescriptorOpRequiredPolicyDecisions, "ReductionResultFootprintAbi", "ScalarOrSurfaceResultPolicy"),
            requiresReductionResultFootprintAbi: true,
            requiresScalarOrSurfaceResultPolicy: true),
        CreateDescriptorOpRow(
            "DmaStreamCompute.REDUCE_AND",
            WithPolicies(DescriptorOpRequiredPolicyDecisions, "ReductionResultFootprintAbi", "ScalarOrSurfaceResultPolicy"),
            requiresReductionResultFootprintAbi: true,
            requiresScalarOrSurfaceResultPolicy: true),
        CreateDescriptorOpRow(
            "DmaStreamCompute.REDUCE_OR",
            WithPolicies(DescriptorOpRequiredPolicyDecisions, "ReductionResultFootprintAbi", "ScalarOrSurfaceResultPolicy"),
            requiresReductionResultFootprintAbi: true,
            requiresScalarOrSurfaceResultPolicy: true),
        CreateDescriptorOpRow(
            "DmaStreamCompute.REDUCE_XOR",
            WithPolicies(DescriptorOpRequiredPolicyDecisions, "ReductionResultFootprintAbi", "ScalarOrSurfaceResultPolicy"),
            requiresReductionResultFootprintAbi: true,
            requiresScalarOrSurfaceResultPolicy: true)
    ];

    public static IReadOnlyList<CompilerLane6DeferredAbiContract> DescriptorShapeRows { get; } =
    [
        CreateDescriptorShapeRow(
            "DSC_SHAPE_STRIDED",
            WithPolicies(DescriptorShapeRequiredPolicyDecisions, "StrideAbi"),
            requiresStrideAbi: true),
        CreateDescriptorShapeRow(
            "DSC_SHAPE_TILED",
            WithPolicies(DescriptorShapeRequiredPolicyDecisions, "TileShapeAbi"),
            requiresTileShapeAbi: true),
        CreateDescriptorShapeRow(
            "DSC_SHAPE_SCATTER_GATHER",
            WithPolicies(DescriptorShapeRequiredPolicyDecisions, "IndexSurfaceAbi"),
            requiresIndexSurfaceAbi: true),
        CreateDescriptorShapeRow(
            "DSC_SHAPE_2D",
            WithPolicies(DescriptorShapeRequiredPolicyDecisions, "2DShapeAbi"),
            requiresTwoDimensionalShapeAbi: true),
        CreateDescriptorShapeRow(
            "DSC_SHAPE_MULTI_RANGE",
            WithPolicies(DescriptorShapeRequiredPolicyDecisions, "MultiRangeAbi"),
            requiresMultiRangeAbi: true)
    ];

    public static IReadOnlyList<CompilerLane6DeferredAbiContract> QueueControlRows { get; } =
    [
        DscPoll,
        DscWait,
        DscCancel,
        DscFence,
        DscCommit
    ];

    public static IReadOnlyList<CompilerLane6DeferredAbiContract> CapabilityQueryRows { get; } =
    [
        DscQueryBackend,
        DscQueryShape
    ];

    public static IReadOnlyList<CompilerLane6DeferredAbiContract> DescriptorParserRows { get; } =
    [
        Dsc2
    ];

    public static IReadOnlyList<CompilerLane6DeferredAbiContract> AllDeferredLane6Rows { get; } =
    [
        .. DescriptorOpRows,
        .. DescriptorShapeRows,
        DscPoll,
        DscWait,
        DscCancel,
        DscFence,
        DscCommit,
        DscQueryBackend,
        DscQueryShape,
        Dsc2
    ];

    private static string[] WithPolicies(
        IReadOnlyList<string> basePolicyDecisions,
        params string[] additionalPolicyDecisions) =>
        [.. basePolicyDecisions, .. additionalPolicyDecisions];

    private static CompilerLane6DeferredAbiContract CreateDescriptorOpRow(
        string mnemonic,
        IReadOnlyList<string> requiredPolicyDecisions,
        bool requiresArithmeticPolicyAbi = false,
        bool requiresSignednessTypePolicyAbi = false,
        bool requiresOverflowPolicyAbi = false,
        bool requiresBoundsPolicyAbi = false,
        bool requiresConversionPolicyAbi = false,
        bool requiresRoundingSaturationTrapPolicy = false,
        bool requiresPredicateFootprintAbi = false,
        bool requiresSelectResultFootprintAbi = false,
        bool requiresReductionResultFootprintAbi = false,
        bool requiresScalarOrSurfaceResultPolicy = false) =>
        new(
            mnemonic,
            CompilerLane6DeferredAbiClass.DescriptorOp,
            "Lane6DescriptorOp",
            "Lane6DescriptorOwnedNoExecution",
            "NoAllocationUntilDescriptorOpAbiRuntimeEvidenceRetireReplayGoldenHelperAuthority",
            "descriptor-owned operation payload; no executable compiler instruction operands",
            "Descriptor-op metadata is parser/descriptor-only and is not opcode, helper, or lowering authority.",
            "Future execution requires explicit descriptor op ABI, runtime evidence, retire/replay/golden proof, and runtime-owned legality.",
            requiredPolicyDecisions,
            isDescriptorOwned: true,
            isDescriptorOnly: true,
            isDescriptorParserOnlyBoundary: true,
            isDescriptorOp: true,
            requiresDescriptorOpAbi: true,
            requiresDescriptorPayloadAbi: true,
            requiresTypedDescriptorProjection: true,
            requiresDescriptorMaterializer: true,
            requiresDescriptorParserValidation: true,
            requiresDescriptorOpTypeAbi: true,
            requiresBackendRuntimeAdmission: true,
            runtimeExecutionEvidenceAbsent: true,
            requiresRetireReplayGoldenEvidence: true,
            runtimeOwnedLegalityIsFinal: true,
            requiresRuntimeAdmission: true,
            requiresRetireCommitAuthority: true,
            requiresReplayDeterminism: true,
            requiresReplayRollbackConformance: true,
            requiresGoldenArtifacts: true,
            requiresFutureVirtualizationBoundaryPolicy: true,
            requiresNoHostEvidenceLeak: true,
            noGuestVisibleHostEvidence: true,
            noHostEvidenceLeak: true,
            noHostOwnedEvidencePublication: true,
            existingDmaStreamComputeEvidenceIsInsufficient: true,
            dsc2ParserEvidenceIsInsufficient: true,
            phase10DescriptorOpEvidenceIsInsufficient: true,
            noScalarOpcodePublication: true,
            noDecoderEncoderAbiPublication: true,
            noInstructionIrProjectionPublication: true,
            noRegistryMaterializerPublication: true,
            noTypedMicroOpPublication: true,
            noSchedulerLaneBindingPublication: true,
            noRuntimeAdmissionPublication: true,
            noExecutionCapturePublication: true,
            noRetireCommitPublication: true,
            noReplayRollbackPublication: true,
            noCompilerHelperEmission: true,
            noHiddenScalarLowering: true,
            noHiddenVectorLowering: true,
            noMultiOpEmission: true,
            noDmaStreamComputeFallback: true,
            noGenericDmaStreamComputeFallbackAsAuthority: true,
            noDsc2Fallback: true,
            noQueueRuntimeFallback: true,
            noLane7Fallback: true,
            noExternalBackendFallback: true,
            noVmxSpecificPath: true,
            requiresArithmeticPolicyAbi: requiresArithmeticPolicyAbi,
            requiresSignednessTypePolicyAbi: requiresSignednessTypePolicyAbi,
            requiresOverflowPolicyAbi: requiresOverflowPolicyAbi,
            requiresBoundsPolicyAbi: requiresBoundsPolicyAbi,
            requiresConversionPolicyAbi: requiresConversionPolicyAbi,
            requiresRoundingSaturationTrapPolicy: requiresRoundingSaturationTrapPolicy,
            requiresPredicateFootprintAbi: requiresPredicateFootprintAbi,
            requiresSelectResultFootprintAbi: requiresSelectResultFootprintAbi,
            requiresReductionResultFootprintAbi: requiresReductionResultFootprintAbi,
            requiresScalarOrSurfaceResultPolicy: requiresScalarOrSurfaceResultPolicy);

    private static CompilerLane6DeferredAbiContract CreateDescriptorShapeRow(
        string mnemonic,
        IReadOnlyList<string> requiredPolicyDecisions,
        bool requiresStrideAbi = false,
        bool requiresTileShapeAbi = false,
        bool requiresIndexSurfaceAbi = false,
        bool requiresTwoDimensionalShapeAbi = false,
        bool requiresMultiRangeAbi = false) =>
        new(
            mnemonic,
            CompilerLane6DeferredAbiClass.DescriptorShape,
            "Lane6DescriptorShape",
            "Lane6ShapeContourNoExecution",
            "NoAllocationUntilShapeAbiRuntimeEvidenceRetireReplayGoldenHelperAuthority",
            "descriptor-owned shape/range payload; no executable compiler instruction operands",
            "Shape metadata is parser/descriptor-only and is not enum, helper, or lowering authority.",
            "Future execution requires explicit shape ABI, runtime evidence, retire/replay/golden proof, and runtime-owned legality.",
            requiredPolicyDecisions,
            isDescriptorOwned: true,
            isDescriptorOnly: true,
            isDescriptorParserOnlyBoundary: true,
            isDescriptorShape: true,
            requiresShapeAbi: true,
            requiresDescriptorPayloadAbi: true,
            requiresTypedDescriptorProjection: true,
            requiresDescriptorMaterializer: true,
            requiresShapeEnumAbi: true,
            requiresShapeParserManifest: true,
            requiresShapeFaultModel: true,
            requiresAliasOverlapPolicy: true,
            requiresBackendRuntimeAdmission: true,
            runtimeExecutionEvidenceAbsent: true,
            requiresRetireReplayGoldenEvidence: true,
            runtimeOwnedLegalityIsFinal: true,
            requiresRuntimeAdmission: true,
            requiresRetireCommitAuthority: true,
            requiresReplayDeterminism: true,
            requiresReplayRollbackConformance: true,
            requiresGoldenArtifacts: true,
            requiresFutureVirtualizationBoundaryPolicy: true,
            requiresNoHostEvidenceLeak: true,
            noGuestVisibleHostEvidence: true,
            noHostEvidenceLeak: true,
            noHostOwnedEvidencePublication: true,
            existingDmaStreamComputeEvidenceIsInsufficient: true,
            dsc2ParserEvidenceIsInsufficient: true,
            phase10DescriptorOpEvidenceIsInsufficient: true,
            noScalarOpcodePublication: true,
            noDecoderEncoderAbiPublication: true,
            noInstructionIrProjectionPublication: true,
            noRegistryMaterializerPublication: true,
            noTypedMicroOpPublication: true,
            noSchedulerLaneBindingPublication: true,
            noRuntimeAdmissionPublication: true,
            noExecutionCapturePublication: true,
            noRetireCommitPublication: true,
            noReplayRollbackPublication: true,
            noCompilerHelperEmission: true,
            noHiddenScalarLowering: true,
            noHiddenVectorLowering: true,
            noMultiOpEmission: true,
            noDmaStreamComputeFallback: true,
            noGenericDmaStreamComputeFallbackAsAuthority: true,
            noDsc2Fallback: true,
            noQueueRuntimeFallback: true,
            noLane7Fallback: true,
            noExternalBackendFallback: true,
            noVmxSpecificPath: true,
            requiresStrideAbi: requiresStrideAbi,
            requiresTileShapeAbi: requiresTileShapeAbi,
            requiresIndexSurfaceAbi: requiresIndexSurfaceAbi,
            requiresTwoDimensionalShapeAbi: requiresTwoDimensionalShapeAbi,
            requiresMultiRangeAbi: requiresMultiRangeAbi);

    private static CompilerLane6DeferredAbiContract CreateQueueControlRow(
        string mnemonic,
        string abiDecision,
        string dataSemantics,
        string resultSemantics,
        IReadOnlyList<string> requiredPolicyDecisions,
        bool requiresCommandScopeAbi = false,
        bool requiresQueueOrderingAbi = false,
        bool requiresStagedCommitAuthority = false,
        bool requiresDscPollCompletionSemantics = false,
        bool requiresWaitProgressFairnessPolicy = false,
        bool requiresCancelRollbackJournal = false,
        bool requiresFenceCompletionOrderingModel = false,
        bool requiresCommitPublicationModel = false) =>
        new(
            mnemonic,
            CompilerLane6DeferredAbiClass.QueueControl,
            "Lane6QueueControl",
            "Lane6QueueControlNoExecution",
            abiDecision,
            "queue token, handle, command scope, or future canonical queue-control descriptor",
            dataSemantics,
            resultSemantics,
            requiredPolicyDecisions,
            isQueueControl: true,
            requiresQueueAuthority: true,
            requiresTokenNamespaceAbi: true,
            requiresQueueHandleAbi: true,
            requiresTokenLifecycleAbi: true,
            requiresQueueOwnershipModel: true,
            requiresQueueStateModel: true,
            requiresQueueRollbackJournal: true,
            requiresQueueRuntimeAdmission: true,
            requiresQueueCommandEncoding: true,
            requiresCommandScopeAbi: requiresCommandScopeAbi,
            requiresQueueOrderingAbi: requiresQueueOrderingAbi,
            requiresStagedCommitAuthority: requiresStagedCommitAuthority,
            requiresDscPollCompletionSemantics: requiresDscPollCompletionSemantics,
            requiresWaitProgressFairnessPolicy: requiresWaitProgressFairnessPolicy,
            requiresCancelRollbackJournal: requiresCancelRollbackJournal,
            requiresFenceCompletionOrderingModel: requiresFenceCompletionOrderingModel,
            requiresCommitPublicationModel: requiresCommitPublicationModel,
            requiresDecoderEncoderAbi: true,
            requiresInstructionIrProjection: true,
            requiresRegistryMaterializer: true,
            requiresSchedulerLaneBinding: true,
            requiresRetireOwnedPublication: true,
            requiresRetireOwnedSideEffectPublication: true,
            requiresReplayRollbackConformance: true,
            requiresGoldenArtifacts: true,
            requiresFutureVirtualizationBoundaryPolicy: true,
            requiresNoHostEvidenceLeak: true,
            noRetirePublicationBeforeQueueAuthority: true,
            noGuestVisibleHostEvidence: true,
            noHostEvidenceLeak: true,
            noHostOwnedEvidencePublication: true,
            existingDmaStreamComputeEvidenceIsInsufficient: true,
            existingDscStatusEvidenceIsInsufficient: true,
            existingDscQueryCapsEvidenceIsInsufficient: true,
            dsc2ParserEvidenceIsInsufficient: true,
            noScalarOpcodePublication: true,
            noDecoderEncoderAbiPublication: true,
            noInstructionIrProjectionPublication: true,
            noRegistryMaterializerPublication: true,
            noTypedMicroOpPublication: true,
            noSchedulerLaneBindingPublication: true,
            noRuntimeAdmissionPublication: true,
            noExecutionCapturePublication: true,
            noRetireCommitPublication: true,
            noReplayRollbackPublication: true,
            noCompilerHelperEmission: true,
            noHiddenScalarLowering: true,
            noHiddenVectorLowering: true,
            noMultiOpEmission: true,
            noDmaStreamComputeFallback: true,
            noDscStatusFallback: true,
            noDscQueryCapsFallback: true,
            noDsc2Fallback: true,
            noLane7Fallback: true,
            noExternalBackendFallback: true,
            noVmxSpecificPath: true);

    private static CompilerLane6DeferredAbiContract CreateCapabilityQueryRow(
        string mnemonic,
        string abiDecision,
        string dataSemantics,
        string resultSemantics,
        IReadOnlyList<string> requiredPolicyDecisions,
        bool requiresBackendCapabilityAbi = false,
        bool requiresShapeQueryAbi = false) =>
        new(
            mnemonic,
            CompilerLane6DeferredAbiClass.CapabilityQuery,
            "Lane6DscQuery",
            "Lane6CapabilityQueryNoExecution",
            abiDecision,
            "rd plus query selector or future canonical capability-query descriptor",
            dataSemantics,
            resultSemantics,
            requiredPolicyDecisions,
            isCapabilityQuery: true,
            isReadOnlyQuery: true,
            requiresCapabilityQueryAbi: true,
            requiresQuerySelectorAbi: true,
            requiresCapabilityResultAbi: true,
            requiresBoundedResultFootprint: true,
            requiresResultScrubbingPolicy: true,
            requiresReplayStableResult: true,
            requiresBackendCapabilityAbi: requiresBackendCapabilityAbi,
            requiresShapeQueryAbi: requiresShapeQueryAbi,
            requiresDecoderEncoderAbi: true,
            requiresInstructionIrProjection: true,
            requiresRegistryMaterializer: true,
            requiresSchedulerLaneBinding: true,
            requiresRetireOwnedPublication: true,
            requiresReplayRollbackConformance: true,
            requiresGoldenArtifacts: true,
            requiresFutureVirtualizationBoundaryPolicy: true,
            requiresNoHostEvidenceLeak: true,
            noRetirePublicationBeforeQueryAuthority: true,
            noGuestVisibleHostEvidence: true,
            noHostEvidenceLeak: true,
            noHostOwnedEvidencePublication: true,
            existingDmaStreamComputeEvidenceIsInsufficient: true,
            existingDscStatusEvidenceIsInsufficient: true,
            existingDscQueryCapsEvidenceIsInsufficient: true,
            dsc2ParserEvidenceIsInsufficient: true,
            noScalarOpcodePublication: true,
            noDecoderEncoderAbiPublication: true,
            noInstructionIrProjectionPublication: true,
            noRegistryMaterializerPublication: true,
            noTypedMicroOpPublication: true,
            noSchedulerLaneBindingPublication: true,
            noRuntimeAdmissionPublication: true,
            noExecutionCapturePublication: true,
            noRetireCommitPublication: true,
            noReplayRollbackPublication: true,
            noCompilerHelperEmission: true,
            noHiddenScalarLowering: true,
            noHiddenVectorLowering: true,
            noMultiOpEmission: true,
            noDmaStreamComputeFallback: true,
            noDscStatusFallback: true,
            noDscQueryCapsFallback: true,
            noDsc2Fallback: true,
            noLane7Fallback: true,
            noExternalBackendFallback: true,
            noVmxSpecificPath: true);

    public string Mnemonic { get; }
    public CompilerLane6DeferredAbiClass AbiClass { get; }
    public string ExtensionName { get; }
    public string EvidenceBoundary { get; }
    public string AbiDecision { get; }
    public string OperandShape { get; }
    public string DataSemantics { get; }
    public string ResultSemantics { get; }
    public IReadOnlyList<string> RequiredPolicyDecisions { get; }
    public bool IsQueueControl { get; }
    public bool RequiresQueueAuthority { get; }
    public bool RequiresTokenNamespaceAbi { get; }
    public bool RequiresQueueHandleAbi { get; }
    public bool RequiresTokenLifecycleAbi { get; }
    public bool RequiresQueueOwnershipModel { get; }
    public bool RequiresQueueStateModel { get; }
    public bool RequiresQueueRollbackJournal { get; }
    public bool RequiresQueueRuntimeAdmission { get; }
    public bool RequiresQueueCommandEncoding { get; }
    public bool RequiresCommandScopeAbi { get; }
    public bool RequiresQueueOrderingAbi { get; }
    public bool RequiresStagedCommitAuthority { get; }
    public bool RequiresDscPollCompletionSemantics { get; }
    public bool RequiresWaitProgressFairnessPolicy { get; }
    public bool RequiresCancelRollbackJournal { get; }
    public bool RequiresFenceCompletionOrderingModel { get; }
    public bool RequiresCommitPublicationModel { get; }
    public bool IsCapabilityQuery { get; }
    public bool IsReadOnlyQuery { get; }
    public bool RequiresCapabilityQueryAbi { get; }
    public bool RequiresQuerySelectorAbi { get; }
    public bool RequiresCapabilityResultAbi { get; }
    public bool RequiresBoundedResultFootprint { get; }
    public bool RequiresResultScrubbingPolicy { get; }
    public bool RequiresReplayStableResult { get; }
    public bool RequiresBackendCapabilityAbi { get; }
    public bool RequiresShapeQueryAbi { get; }
    public bool IsDescriptorOwned { get; }
    public bool IsCarrierOnly { get; }
    public bool IsParserOnly { get; }
    public bool IsDescriptorOnly { get; }
    public bool IsDescriptorParserOnlyBoundary { get; }
    public bool IsDescriptorOp { get; }
    public bool IsDescriptorShape { get; }
    public bool RequiresDescriptorOpAbi { get; }
    public bool RequiresShapeAbi { get; }
    public bool RequiresDescriptorPayloadAbi { get; }
    public bool RequiresTypedDescriptorProjection { get; }
    public bool RequiresDescriptorMaterializer { get; }
    public bool RequiresDescriptorParserValidation { get; }
    public bool RequiresDescriptorOpTypeAbi { get; }
    public bool RequiresShapeEnumAbi { get; }
    public bool RequiresShapeParserManifest { get; }
    public bool RequiresShapeFaultModel { get; }
    public bool RequiresAliasOverlapPolicy { get; }
    public bool RequiresArithmeticPolicyAbi { get; }
    public bool RequiresSignednessTypePolicyAbi { get; }
    public bool RequiresOverflowPolicyAbi { get; }
    public bool RequiresBoundsPolicyAbi { get; }
    public bool RequiresConversionPolicyAbi { get; }
    public bool RequiresRoundingSaturationTrapPolicy { get; }
    public bool RequiresPredicateFootprintAbi { get; }
    public bool RequiresSelectResultFootprintAbi { get; }
    public bool RequiresReductionResultFootprintAbi { get; }
    public bool RequiresScalarOrSurfaceResultPolicy { get; }
    public bool RequiresStrideAbi { get; }
    public bool RequiresTileShapeAbi { get; }
    public bool RequiresIndexSurfaceAbi { get; }
    public bool RequiresTwoDimensionalShapeAbi { get; }
    public bool RequiresMultiRangeAbi { get; }
    public bool RequiresBackendRuntimeAdmission { get; }
    public bool RuntimeExecutionEvidenceAbsent { get; }
    public bool RequiresRetireReplayGoldenEvidence { get; }
    public bool RuntimeOwnedLegalityIsFinal { get; }
    public bool RequiresDescriptorV2Adr { get; }
    public bool RequiresDescriptorV2ParserManifest { get; }
    public bool RequiresBackwardCompatibleDecoder { get; }
    public bool RequiresDescriptorV2ExecutionPolicy { get; }
    public bool RequiresDescriptorV2AdmissionPolicy { get; }
    public bool RequiresRuntimeAdmission { get; }
    public bool RequiresRetireCommitAuthority { get; }
    public bool RequiresDescriptorV2RetireReplayPolicy { get; }
    public bool RequiresReplayDeterminism { get; }
    public bool RequiresParserOnlyConformance { get; }
    public bool RequiresDescriptorV2GoldenArtifacts { get; }
    public bool NoDsc2ExecutionBeforeAdr { get; }
    public bool ParserAcceptanceIsNotExecutionEvidence { get; }
    public bool NoParserToExecutionPromotion { get; }
    public bool RequiresDecoderEncoderAbi { get; }
    public bool RequiresInstructionIrProjection { get; }
    public bool RequiresRegistryMaterializer { get; }
    public bool RequiresSchedulerLaneBinding { get; }
    public bool RequiresRetireOwnedPublication { get; }
    public bool RequiresRetireOwnedSideEffectPublication { get; }
    public bool RequiresReplayRollbackConformance { get; }
    public bool RequiresGoldenArtifacts { get; }
    public bool RequiresFutureVirtualizationBoundaryPolicy { get; }
    public bool RequiresNoHostEvidenceLeak { get; }
    public bool NoRetirePublicationBeforeQueueAuthority { get; }
    public bool NoRetirePublicationBeforeQueryAuthority { get; }
    public bool NoGuestVisibleHostEvidence { get; }
    public bool NoHostEvidenceLeak { get; }
    public bool NoHostOwnedEvidencePublication { get; }
    public bool ExistingDmaStreamComputeEvidenceIsInsufficient { get; }
    public bool ExistingDscStatusEvidenceIsInsufficient { get; }
    public bool ExistingDscQueryCapsEvidenceIsInsufficient { get; }
    public bool Dsc2ParserEvidenceIsInsufficient { get; }
    public bool Phase10DescriptorOpEvidenceIsInsufficient { get; }
    public bool NoScalarOpcodePublication { get; }
    public bool NoDecoderEncoderAbiPublication { get; }
    public bool NoExecutableDecoderEncoderAbiPublication { get; }
    public bool NoInstructionIrProjectionPublication { get; }
    public bool NoRegistryMaterializerPublication { get; }
    public bool NoTypedMicroOpPublication { get; }
    public bool NoSchedulerLaneBindingPublication { get; }
    public bool NoRuntimeAdmissionPublication { get; }
    public bool NoExecutionCapturePublication { get; }
    public bool NoRetireCommitPublication { get; }
    public bool NoReplayRollbackPublication { get; }
    public bool NoCompilerHelperEmission { get; }
    public bool NoHiddenScalarLowering { get; }
    public bool NoHiddenVectorLowering { get; }
    public bool NoMultiOpEmission { get; }
    public bool NoDmaStreamComputeFallback { get; }
    public bool NoGenericDmaStreamComputeFallbackAsAuthority { get; }
    public bool NoDscStatusFallback { get; }
    public bool NoDscQueryCapsFallback { get; }
    public bool NoDsc2Fallback { get; }
    public bool NoQueueRuntimeFallback { get; }
    public bool NoLane7Fallback { get; }
    public bool NoExternalBackendFallback { get; }
    public bool NoVmxSpecificPath { get; }
    public bool HasOpcodeAllocation => false;
    public bool HasScalarOpcodeAllocation => false;
    public bool IsExecutable => false;
    public bool CompilerEmissionAllowed => false;
    public bool CompilerHelperAllowed => false;

    public void RequireCompilerEmissionAuthority()
    {
        string requiredDecisions = AbiClass switch
        {
            CompilerLane6DeferredAbiClass.QueueControl =>
                "queue command encoding, token lifecycle, queue authority, rollback/replay, and retire-owned side-effect ABI decisions",
            CompilerLane6DeferredAbiClass.CapabilityQuery =>
                "capability query selector, bounded result footprint, result scrubbing, replay, and retire-owned publication ABI decisions",
            CompilerLane6DeferredAbiClass.DescriptorParserV2 =>
                "descriptor-v2 ADR, parser manifest, runtime admission, retire, replay, and compiler construction ABI decisions",
            CompilerLane6DeferredAbiClass.DescriptorOp =>
                "descriptor op ABI, runtime execution evidence, retire/replay/golden evidence, runtime-owned legality, and helper authority decisions",
            CompilerLane6DeferredAbiClass.DescriptorShape =>
                "shape ABI, runtime execution evidence, retire/replay/golden evidence, runtime-owned legality, and helper authority decisions",
            _ => "required Lane6 ABI decisions"
        };

        throw new InvalidOperationException(
            $"{Mnemonic} compiler emission is blocked until {requiredDecisions} are explicit.");
    }
}
