using System;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal readonly record struct SimpleAsmAppMetrics(
    double Ipc,
    ulong InstructionsRetired,
    ulong CycleCount,
    ulong StallCycles,
    ulong DataHazards,
    ulong MemoryStalls,
    ulong LoadUseBubbles,
    ulong WAWHazards,
    ulong ControlHazards,
    ulong BranchMispredicts,
    ulong FrontendStalls,
    ulong ScalarIssueWidth0Cycles,
    ulong ScalarIssueWidth1Cycles,
    ulong ScalarIssueWidth2Cycles,
    ulong ScalarIssueWidth3Cycles,
    ulong ScalarIssueWidth4Cycles,
    ulong TotalBursts,
    ulong BytesTransferred,
    ulong NopAvoided,
    ulong NopDueToNoClassCapacity,
    ulong NopDueToPinnedConstraint,
    ulong NopDueToResourceConflict,
    ulong NopDueToDynamicState,
    ulong ClassFlexibleInjects,
    ulong HardPinnedInjects,
    ulong EligibilityMaskedCycles,
    ulong EligibilityMaskedReadyCandidates,
    byte LastEligibilityRequestedMask,
    byte LastEligibilityNormalizedMask,
    byte LastEligibilityReadyPortMask,
    byte LastEligibilityVisibleReadyMask,
    byte LastEligibilityMaskedReadyMask,
    ulong MultiLaneExecuteCount,
    ulong ClusterPreparedExecutionChoiceCount,
    ulong WidePathSuccessCount,
    ulong PartialWidthIssueCount,
    ulong DecoderPreparedScalarGroupCount,
    ulong VTSpreadPerBundle,
    ulong IssuePacketPreparedLaneCountSum,
    ulong IssuePacketMaterializedLaneCountSum,
    ulong IssuePacketPreparedPhysicalLaneCountSum,
    ulong IssuePacketMaterializedPhysicalLaneCountSum,
    ulong IssuePacketWidthDropCount,
    int MeasurementStartBundleSlot,
    double EmittedAverageActiveVtPerWindow,
    int EmittedMaxActiveVtPerWindow,
    int MeasurementPhaseNonZeroVtInstructionCount,
    int MeasurementPhaseInstructionCount,
    double DecodedAverageActiveVtPerWindow,
    int DecodedMaxActiveVtPerWindow,
    int DecodedNonZeroVtInstructionCount,
    string CompilerStage,
    string DecoderStage,
    string LikelyFailingStage,
    string FailureMessage,
    int EmittedInstructionCount,
    int BundleCount,
    uint FirstOpcode,
    bool FirstOpcodeRegistered,
    string FrontendProfile,
    bool FrontendSupported,
    string ProgramVariant,
    int CompilerEmittedDistinctVirtualThreadCount,
    int CompilerIrDistinctVirtualThreadCount,
    int CompilerScheduleCycleGroupCount,
    int CompilerScheduleCrossVtCycleGroupCount,
    double CompilerScheduleAverageWidth,
    double CompilerScheduleAverageVtSpread,
    int CompilerScheduleMaxVtSpread,
    int CompilerBundleCount,
    int CompilerBundleCrossVtCount,
    double CompilerBundleAverageVtSpread,
    int CompilerBundleMaxVtSpread,
    ulong NopElisionSkipCount,
    ulong ScalarLanesRetired,
    ulong NonScalarLanesRetired,
    ulong RetireCycleCount,
    bool ShowcaseExecuted,
    bool ShowcaseCoversFsp,
    bool ShowcaseCoversTypedSlot,
    bool ShowcaseCoversAdmission,
    bool ShowcaseCoversSurfaceContract,
    bool ShowcaseCoversVector,
    bool ShowcaseCoversStream,
    bool ShowcaseCoversCsr,
    bool ShowcaseCoversSystem,
    bool ShowcaseCoversVmx,
    bool ShowcaseCoversObservability,
    string ShowcaseAssistRuntimeStatus,
    int ShowcaseTraceEventCount,
    int ShowcasePipelineEventCount,
    int ShowcaseFsmTransitionCount,
    ulong ShowcaseDirectTelemetryInstrRetired,
    ulong ShowcaseDirectBarrierCount,
    ulong ShowcaseDirectVmExitCount,
    string ShowcaseFinalPipelineState,
    ulong PhaseCertificateReadyHits,
    ulong PhaseCertificateReadyMisses,
    ulong EstimatedPhaseCertificateChecksSaved,
    ulong PhaseCertificateInvalidations,
    ulong PhaseCertificateMutationInvalidations,
    ulong PhaseCertificatePhaseMismatchInvalidations,
    ulong L1BypassHits,
    ulong ForegroundWarmAttempts,
    ulong ForegroundWarmSuccesses,
    ulong ForegroundWarmReuseHits,
    ulong ForegroundBypassHits,
    ulong AssistWarmAttempts,
    ulong AssistWarmSuccesses,
    ulong AssistWarmReuseHits,
    ulong AssistBypassHits,
    ulong StreamWarmTranslationRejects,
    ulong StreamWarmBackendRejects,
    ulong AssistWarmResidentBudgetRejects,
    ulong AssistWarmLoadingBudgetRejects,
    ulong AssistWarmNoVictimRejects,
    ulong SmtOwnerContextGuardRejects,
    ulong SmtDomainGuardRejects,
    ulong SmtBoundaryGuardRejects,
    ulong SmtSharedResourceCertificateRejects,
    ulong SmtRegisterGroupCertificateRejects,
    ulong SmtLegalityRejectByAluClass,
    ulong SmtLegalityRejectByLsuClass,
    ulong SmtLegalityRejectByDmaStreamClass,
    ulong SmtLegalityRejectByBranchControl,
    ulong SmtLegalityRejectBySystemSingleton,
    string LastSmtLegalityRejectKind,
    string LastSmtLegalityAuthoritySource,
    ulong WorkloadIterations,
    int LoopBodyInstructionCount,
    ulong DynamicRetirementTarget,
    string WorkloadShape,
    ulong SliceExecutionCount,
    ulong ReferenceSliceIterations,
    string MemoryCycleTelemetrySchemaVersion,
    bool MemoryCycleTelemetryAvailable,
    ulong MemoryControllerCycles,
    ulong MemoryReadServiceCycles,
    ulong MemoryStoreReadinessServiceCycles,
    ulong MemoryCompletionPublicationCycles,
    ulong MemoryAcceptedRequests,
    ulong MemoryCompletedRequests,
    ulong DataReadAcceptedRequests,
    ulong DataReadCompletedRequests,
    ulong DataWriteAcceptedRequests,
    ulong DataWriteCompletedRequests,
    ulong DataReadBytes,
    ulong CommittedDataWriteBytes,
    bool InstructionFetchReadBytesTelemetryAvailable,
    ulong InstructionFetchReadBytes,
    bool InstructionFetchRequestTelemetryAvailable,
    ulong MemoryQueueFullRejects,
    bool MemoryBankConflictRejectTelemetryAvailable,
    ulong MemoryBankConflictRejects,
    ulong MemoryTelemetryBaselineOutstandingRequests,
    ulong MemoryCanceledRequests,
    ulong MemoryConsumedCompletions,
    ulong MemoryOutstandingRequests)
{
    public ulong ActiveCycles => CycleCount > StallCycles ? CycleCount - StallCycles : 0;

    public double RetireIpc =>
        RetireCycleCount == 0
            ? 0.0
            : (double)InstructionsRetired / RetireCycleCount;

    public double StallShare =>
        CycleCount == 0
            ? 0.0
            : (double)StallCycles / CycleCount;

    public double SlackReclaimRatio =>
        SlackReclaimAttemptCount == 0
            ? 0.0
            : (double)NopAvoided / SlackReclaimAttemptCount;

    public ulong SlackReclaimAttemptCount =>
        NopAvoided
        + NopDueToNoClassCapacity
        + NopDueToPinnedConstraint
        + NopDueToResourceConflict
        + NopDueToDynamicState;

    public double FlexibleInjectShare =>
        (ClassFlexibleInjects + HardPinnedInjects) == 0
            ? 0.0
            : (double)ClassFlexibleInjects / (ClassFlexibleInjects + HardPinnedInjects);

    public bool HasMeasuredSlackReclamation =>
        SlackReclaimAttemptCount > 0 || ClassFlexibleInjects > 0 || HardPinnedInjects > 0;

    public bool HasSmtLegalityRejectTelemetry =>
        SmtOwnerContextGuardRejects > 0 ||
        SmtDomainGuardRejects > 0 ||
        SmtBoundaryGuardRejects > 0 ||
        SmtSharedResourceCertificateRejects > 0 ||
        SmtRegisterGroupCertificateRejects > 0 ||
        SmtLegalityRejectByAluClass > 0 ||
        SmtLegalityRejectByLsuClass > 0 ||
        SmtLegalityRejectByDmaStreamClass > 0 ||
        SmtLegalityRejectByBranchControl > 0 ||
        SmtLegalityRejectBySystemSingleton > 0 ||
        !string.Equals(LastSmtLegalityRejectKind, "None", StringComparison.Ordinal);

    public bool HasMeasuredClusterPacking =>
        MultiLaneExecuteCount > 0
        || ClusterPreparedExecutionChoiceCount > 0
        || WidePathSuccessCount > 0
        || PartialWidthIssueCount > 0
        || DecoderPreparedScalarGroupCount > 0;

    public double MultiLaneExecuteRate =>
        CycleCount == 0
            ? 0.0
            : (double)MultiLaneExecuteCount / CycleCount;

    public double EffectiveIssueWidth =>
        ActiveCycles == 0
            ? 0.0
            : (double)InstructionsRetired / ActiveCycles;

    public double WidePathSuccessRate =>
        CycleCount == 0
            ? 0.0
            : (double)WidePathSuccessCount / CycleCount;

    public double RetiredPerWidePathSuccess =>
        WidePathSuccessCount == 0
            ? 0.0
            : (double)InstructionsRetired / WidePathSuccessCount;

    public double CyclesPerWidePathSuccess =>
        WidePathSuccessCount == 0
            ? 0.0
            : (double)CycleCount / WidePathSuccessCount;

    public double PreparedGroupRealizationRate =>
        DecoderPreparedScalarGroupCount == 0
            ? 0.0
            : (double)ClusterPreparedExecutionChoiceCount / DecoderPreparedScalarGroupCount;

    public double AverageVtSpreadPerPreparedBundle =>
        DecoderPreparedScalarGroupCount == 0
            ? 0.0
            : (double)VTSpreadPerBundle / DecoderPreparedScalarGroupCount;

    public double IssuePacketPreparedLanesPerClusterChoice =>
        ClusterPreparedExecutionChoiceCount == 0
            ? 0.0
            : (double)IssuePacketPreparedLaneCountSum / ClusterPreparedExecutionChoiceCount;

    public double IssuePacketMaterializedLanesPerClusterChoice =>
        ClusterPreparedExecutionChoiceCount == 0
            ? 0.0
            : (double)IssuePacketMaterializedLaneCountSum / ClusterPreparedExecutionChoiceCount;

    // Realization/loss compares physical packet width. Auxiliary carriers can legitimately
    // materialize into scalar physical lanes without counting toward the scalar-projection
    // advisory mask, so scalar-projection sums are not a safe denominator here.
    public double IssuePacketLaneRealizationRate =>
        IssuePacketPreparedPhysicalLaneCountSum == 0
            ? 0.0
            : (double)IssuePacketMaterializedPhysicalLaneCountSum / IssuePacketPreparedPhysicalLaneCountSum;

    public double IssuePacketLaneLossPerClusterChoice =>
        ClusterPreparedExecutionChoiceCount == 0
            ? 0.0
            : ((double)IssuePacketPreparedPhysicalLaneCountSum - IssuePacketMaterializedPhysicalLaneCountSum) / ClusterPreparedExecutionChoiceCount;

    public double IssuePacketWidthDropShare =>
        ClusterPreparedExecutionChoiceCount == 0
            ? 0.0
            : (double)IssuePacketWidthDropCount / ClusterPreparedExecutionChoiceCount;

    public double IssuePacketPreparedPhysicalLanesPerClusterChoice =>
        ClusterPreparedExecutionChoiceCount == 0
            ? 0.0
            : (double)IssuePacketPreparedPhysicalLaneCountSum / ClusterPreparedExecutionChoiceCount;

    public double IssuePacketMaterializedPhysicalLanesPerClusterChoice =>
        ClusterPreparedExecutionChoiceCount == 0
            ? 0.0
            : (double)IssuePacketMaterializedPhysicalLaneCountSum / ClusterPreparedExecutionChoiceCount;

    public double RetiredPhysicalLanesPerRetireCycle =>
        RetireCycleCount == 0
            ? 0.0
            : (double)(ScalarLanesRetired + NonScalarLanesRetired) / RetireCycleCount;

    public bool UsesMultipleSlices => SliceExecutionCount > 1;

    public bool MemoryRequestIdentityBalanced =>
        !MemoryCycleTelemetryAvailable ||
        (UInt128)MemoryTelemetryBaselineOutstandingRequests + MemoryAcceptedRequests ==
        (UInt128)MemoryCanceledRequests + MemoryConsumedCompletions + MemoryOutstandingRequests;

    public bool HasEligibilityTelemetry =>
        EligibilityMaskedCycles > 0 ||
        EligibilityMaskedReadyCandidates > 0 ||
        LastEligibilityRequestedMask != 0 ||
        LastEligibilityNormalizedMask != 0 ||
        LastEligibilityReadyPortMask != 0 ||
        LastEligibilityVisibleReadyMask != 0 ||
        LastEligibilityMaskedReadyMask != 0;

    public bool HasPhaseCertificateTelemetry =>
        PhaseCertificateReadyHits > 0 ||
        PhaseCertificateReadyMisses > 0 ||
        EstimatedPhaseCertificateChecksSaved > 0 ||
        PhaseCertificateInvalidations > 0 ||
        PhaseCertificateMutationInvalidations > 0 ||
        PhaseCertificatePhaseMismatchInvalidations > 0;

    public double PhaseCertificateReuseHitRate
    {
        get
        {
            ulong totalReadinessChecks = PhaseCertificateReadyHits + PhaseCertificateReadyMisses;
            return totalReadinessChecks == 0
                ? 0.0
                : (double)PhaseCertificateReadyHits / totalReadinessChecks;
        }
    }

    public bool HasStreamIngressWarmTelemetry =>
        L1BypassHits > 0 ||
        ForegroundWarmAttempts > 0 ||
        ForegroundWarmSuccesses > 0 ||
        ForegroundWarmReuseHits > 0 ||
        ForegroundBypassHits > 0 ||
        AssistWarmAttempts > 0 ||
        AssistWarmSuccesses > 0 ||
        AssistWarmReuseHits > 0 ||
        AssistBypassHits > 0 ||
        StreamWarmTranslationRejects > 0 ||
        StreamWarmBackendRejects > 0 ||
        AssistWarmResidentBudgetRejects > 0 ||
        AssistWarmLoadingBudgetRejects > 0 ||
        AssistWarmNoVictimRejects > 0;

    public double ForegroundWarmSuccessRate =>
        ForegroundWarmAttempts == 0
            ? 0.0
            : (double)ForegroundWarmSuccesses / ForegroundWarmAttempts;

    public double AssistWarmSuccessRate =>
        AssistWarmAttempts == 0
            ? 0.0
            : (double)AssistWarmSuccesses / AssistWarmAttempts;

    public string DominantEffect => LikelyFailingStage;
}
