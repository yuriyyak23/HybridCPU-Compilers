using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Intent;

namespace HybridCPU.Compiler.Core.IR.Lowering;

public enum CompilerLoweringDecisionKind
{
    Rejected = 0,
    NoEmission,
    StructuralOnly,
    ParserOnly,
    HelperAbiOnly,
    FutureGated
}

public enum CompilerEmissionClass
{
    None = 0,
    NoEmission,
    CarrierCandidate,
    SidebandOnly,
    DescriptorSidebandOnly,
    EvidenceOnly
}

public enum CompilerProductionLoweringStatus
{
    Rejected = 0,
    NotProductionLowering,
    ParserOnly,
    HelperAbiOnly,
    NoEmission,
    FutureGated,
    ProductionCarrierPackageRuntimeAuthorityPending
}

public enum CompilerRejectReason
{
    None = 0,
    UnknownContour,
    MissingDescriptorSideband,
    CrossContourFallbackForbidden,
    AuthorityStrengtheningRejected,
    RuntimeAuthorityOwned,
    VmxEmissionForbidden,
    VmxBackendEmissionForbidden = VmxEmissionForbidden,
    SecureComputeEmissionForbidden,
    HelperAbiOnly,
    ParserOnly,
    DescriptorAbiOnly,
    CarrierIsNotPublication,
    CapabilityObservationOnly
}

public enum CompilerHelperRecoveryStatus
{
    NotRecognized = 0,
    HelperAbiRecovered,
    HelperAbiRejected
}

public enum CompilerProducedArtifactKind
{
    None = 0,
    Carrier,
    Sideband,
    Descriptor,
    TypedSlotFacts,
    StructuralAgreement,
    Evidence
}

public enum CompilerRequiredArtifactKind
{
    None = 0,
    DescriptorSideband,
    PolicySideband,
    RuntimeLegalityA,
    RuntimeLegalityB,
    RuntimeCommit,
    RuntimeRetire,
    RuntimePublication
}

public enum FallbackPolicyKind
{
    Forbidden = 0,
    SameContourStructuralRetryOnly
}

public sealed record NoFallbackProof(
    string ProofId,
    FallbackPolicyKind PolicyKind,
    string Reason)
{
    public static NoFallbackProof Forbidden(string proofId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proofId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(proofId, FallbackPolicyKind.Forbidden, reason);
    }
}

public sealed record FallbackPolicy(
    FallbackPolicyKind Kind,
    bool AllowsCrossContourFallback,
    string Reason)
{
    public static FallbackPolicy Forbidden(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(FallbackPolicyKind.Forbidden, AllowsCrossContourFallback: false, reason);
    }
}

public sealed class LegacyApiTranslation
{
    private LegacyApiTranslation(
        string sourceApi,
        string sourceMeaning,
        bool sourceValue,
        bool strengthensAuthority)
    {
        SourceApi = sourceApi;
        SourceMeaning = sourceMeaning;
        SourceValue = sourceValue;
        StrengthensAuthority = strengthensAuthority;
    }

    public string SourceApi { get; }

    public string SourceMeaning { get; }

    public bool SourceValue { get; }

    public bool StrengthensAuthority { get; }

    public static LegacyApiTranslation Create(
        string sourceApi,
        string sourceMeaning,
        bool sourceValue,
        bool strengthensAuthority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceApi);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMeaning);

        if (strengthensAuthority)
        {
            throw new InvalidOperationException(
                "Legacy compiler bool/Try/admission results cannot be translated into stronger runtime authority.");
        }

        return new(sourceApi, sourceMeaning, sourceValue, strengthensAuthority: false);
    }

    public static LegacyApiTranslation StructuralOnly(
        string sourceApi,
        bool sourceValue,
        string sourceMeaning) =>
        Create(sourceApi, sourceMeaning, sourceValue, strengthensAuthority: false);
}

public abstract record CompilerLoweringDecision(
    CompilerLoweringDecisionKind DecisionKind,
    SemanticIntentKind IntentKind,
    ExecutionContourKind ContourKind,
    CompilerCoreResultHeader Header,
    CompilerEmissionClass EmissionClass,
    CompilerProductionLoweringStatus ProductionLoweringStatus,
    NoFallbackProof NoFallbackProof,
    FallbackPolicy FallbackPolicy,
    IReadOnlyList<CompilerProducedArtifactKind> ProducedArtifacts,
    IReadOnlyList<CompilerRequiredArtifactKind> RequiredArtifacts,
    IReadOnlyList<CompilerRejectReason> RejectReasons,
    LegacyApiTranslation? LegacyTranslation,
    string Reason)
{
    public CompilerAuthorityClass AuthorityClass => Header.AuthorityClass;

    public CompilerAuthoritySourceKind AuthoritySourceKind => Header.AuthoritySourceKind;

    public CompilerEvidenceClass EvidenceClass => Header.EvidenceClass;

    public CompilerExecutionClaim ExecutionClaim => Header.ExecutionClaim;

    public CompilerPublicationClass PublicationClass => Header.PublicationClass;

    public CompilerRuntimeAuthorityDependency RuntimeAuthorityDependency => Header.RuntimeAuthorityDependency;

    public static CompilerLoweringDecision Reject(
        SemanticIntentKind intentKind,
        ExecutionContourKind contourKind,
        CompilerRejectReason rejectReason,
        string reason) =>
        new CompilerRejectedLoweringDecision(
            CompilerLoweringDecisionKind.Rejected,
            intentKind,
            contourKind,
            CompilerCoreResultHeader.NoEmissionParserOnly(CompilerEvidenceClass.NegativeGateEvidence),
            CompilerEmissionClass.NoEmission,
            CompilerProductionLoweringStatus.Rejected,
            NoFallbackProof.Forbidden($"reject:{intentKind}:{contourKind}:{rejectReason}", reason),
            FallbackPolicy.Forbidden("Rejected lowering decision cannot use fallback."),
            Array.Empty<CompilerProducedArtifactKind>(),
            Array.Empty<CompilerRequiredArtifactKind>(),
            [rejectReason],
            LegacyTranslation: null,
            reason);

    public static CompilerLoweringDecision FromLegacyStructuralBool(
        bool sourceValue,
        string sourceApi,
        SemanticIntentKind intentKind,
        ExecutionContourKind contourKind,
        string reason) =>
        new CompilerLegacyStructuralLoweringDecision(
            CompilerLoweringDecisionKind.StructuralOnly,
            intentKind,
            contourKind,
            new CompilerCoreResultHeader(
                CompilerAuthorityClass.StructuralAdmissionEvidence,
                CompilerAuthoritySourceKind.CompilerStructuralModel,
                CompilerEvidenceClass.StructuralAdmissionEvidence,
                CompilerPublicationClass.EvidenceOnly,
                CompilerExecutionClaim.NoExecutionClaim,
                CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
                CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
                CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired),
            CompilerEmissionClass.EvidenceOnly,
            CompilerProductionLoweringStatus.NotProductionLowering,
            NoFallbackProof.Forbidden($"legacy:{sourceApi}:{intentKind}:{contourKind}", reason),
            FallbackPolicy.Forbidden("Legacy structural compiler bool cannot select cross-contour fallback."),
            [CompilerProducedArtifactKind.Evidence],
            [CompilerRequiredArtifactKind.RuntimeLegalityA, CompilerRequiredArtifactKind.RuntimeLegalityB],
            Array.Empty<CompilerRejectReason>(),
            LegacyApiTranslation.StructuralOnly(
                sourceApi,
                sourceValue,
                "Legacy bool is structural compiler evidence only."),
            reason);

    public static CompilerLoweringDecision FromLegacyBackendLoweringDecision(
        HybridCPU.Compiler.Core.IR.CompilerBackendLoweringDecision sourceDecision,
        string sourceApi,
        SemanticIntentKind intentKind,
        ExecutionContourKind contourKind)
    {
        ArgumentNullException.ThrowIfNull(sourceDecision);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceApi);

        bool sourceValue = sourceDecision.IsAllowedObservation;
        CompilerLoweringDecisionKind decisionKind = sourceValue
            ? CompilerLoweringDecisionKind.FutureGated
            : CompilerLoweringDecisionKind.Rejected;
        CompilerProductionLoweringStatus productionStatus = sourceValue
            ? CompilerProductionLoweringStatus.FutureGated
            : CompilerProductionLoweringStatus.Rejected;
        IReadOnlyList<CompilerRejectReason> rejectReasons = sourceValue
            ? [CompilerRejectReason.CapabilityObservationOnly]
            : [CompilerRejectReason.CapabilityObservationOnly, CompilerRejectReason.AuthorityStrengtheningRejected];

        return new CompilerLegacyBackendObservationLoweringDecision(
            decisionKind,
            intentKind,
            contourKind,
            new CompilerCoreResultHeader(
                CompilerAuthorityClass.CompilerEvidenceProduction,
                CompilerAuthoritySourceKind.RuntimeOwnedPolicyReference,
                CompilerEvidenceClass.RuntimeContractObservationEvidence,
                CompilerPublicationClass.EvidenceOnly,
                CompilerExecutionClaim.NoExecutionClaim,
                CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
                CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
                CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired |
                CompilerRuntimeAuthorityDependency.RuntimeCommitRequired |
                CompilerRuntimeAuthorityDependency.RuntimeRetireRequired |
                CompilerRuntimeAuthorityDependency.RuntimePublicationRequired),
            CompilerEmissionClass.EvidenceOnly,
            productionStatus,
            NoFallbackProof.Forbidden(
                $"legacy-backend:{sourceApi}:{intentKind}:{contourKind}",
                "Legacy backend capability bool cannot select execution or fallback authority."),
            FallbackPolicy.Forbidden("Legacy backend capability observation cannot route fallback."),
            [CompilerProducedArtifactKind.Evidence],
            [
                CompilerRequiredArtifactKind.RuntimeLegalityA,
                CompilerRequiredArtifactKind.RuntimeLegalityB,
                CompilerRequiredArtifactKind.RuntimeCommit,
                CompilerRequiredArtifactKind.RuntimeRetire,
                CompilerRequiredArtifactKind.RuntimePublication
            ],
            rejectReasons,
            LegacyApiTranslation.StructuralOnly(
                sourceApi,
                sourceValue,
                "Legacy backend bool is capability observation/evidence only."),
            sourceDecision.Reason);
    }

    public static CompilerLoweringDecision FromLegacyHelperRecoveryBool(
        bool sourceValue,
        CompilerHelperRecoveryStatus recoveryStatus,
        string sourceApi,
        SemanticIntentKind intentKind,
        ExecutionContourKind contourKind,
        string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceApi);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        CompilerLoweringDecisionKind decisionKind = recoveryStatus switch
        {
            CompilerHelperRecoveryStatus.HelperAbiRecovered => CompilerLoweringDecisionKind.HelperAbiOnly,
            CompilerHelperRecoveryStatus.HelperAbiRejected => CompilerLoweringDecisionKind.Rejected,
            _ => CompilerLoweringDecisionKind.ParserOnly
        };
        CompilerProductionLoweringStatus productionStatus = recoveryStatus switch
        {
            CompilerHelperRecoveryStatus.HelperAbiRecovered => CompilerProductionLoweringStatus.HelperAbiOnly,
            CompilerHelperRecoveryStatus.HelperAbiRejected => CompilerProductionLoweringStatus.Rejected,
            _ => CompilerProductionLoweringStatus.ParserOnly
        };
        CompilerExecutionClaim executionClaim = recoveryStatus == CompilerHelperRecoveryStatus.HelperAbiRecovered
            ? CompilerExecutionClaim.HelperOnly
            : CompilerExecutionClaim.ParserOnly;
        CompilerEvidenceClass evidenceClass = recoveryStatus == CompilerHelperRecoveryStatus.HelperAbiRejected
            ? CompilerEvidenceClass.NegativeGateEvidence
            : CompilerEvidenceClass.ParserEvidence;
        IReadOnlyList<CompilerRejectReason> rejectReasons = recoveryStatus switch
        {
            CompilerHelperRecoveryStatus.HelperAbiRecovered => [CompilerRejectReason.HelperAbiOnly],
            CompilerHelperRecoveryStatus.HelperAbiRejected => [CompilerRejectReason.HelperAbiOnly, CompilerRejectReason.AuthorityStrengtheningRejected],
            _ => [CompilerRejectReason.ParserOnly]
        };

        return new CompilerLegacyHelperRecoveryLoweringDecision(
            decisionKind,
            intentKind,
            contourKind,
            new CompilerCoreResultHeader(
                CompilerAuthorityClass.CompilerEvidenceProduction,
                CompilerAuthoritySourceKind.CompilerAbiValidator,
                evidenceClass,
                CompilerPublicationClass.EvidenceOnly,
                executionClaim,
                CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
                CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
                CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired),
            CompilerEmissionClass.EvidenceOnly,
            productionStatus,
            NoFallbackProof.Forbidden(
                $"legacy-helper:{sourceApi}:{recoveryStatus}:{intentKind}:{contourKind}",
                "Legacy helper/parser recovery bool cannot select production lowering or fallback authority."),
            FallbackPolicy.Forbidden("Legacy helper/parser recovery cannot route fallback."),
            [CompilerProducedArtifactKind.Evidence],
            [CompilerRequiredArtifactKind.RuntimeLegalityA, CompilerRequiredArtifactKind.RuntimeLegalityB],
            rejectReasons,
            LegacyApiTranslation.StructuralOnly(
                sourceApi,
                sourceValue,
                "Legacy TryRecoverFromInstruction bool is helper/parser recognition evidence only."),
            reason);
    }

    public static CompilerLoweringDecision FromPositiveHelperEmission(
        string sourceApi,
        SemanticIntentKind intentKind,
        ExecutionContourKind contourKind,
        string noFallbackProofId,
        string reason,
        IReadOnlyList<CompilerProducedArtifactKind>? producedArtifacts = null,
        IReadOnlyList<CompilerRequiredArtifactKind>? requiredArtifacts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceApi);
        ArgumentException.ThrowIfNullOrWhiteSpace(noFallbackProofId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new CompilerPositiveHelperEmissionLoweringDecision(
            CompilerLoweringDecisionKind.HelperAbiOnly,
            intentKind,
            contourKind,
            new CompilerCoreResultHeader(
                CompilerAuthorityClass.TransportConstruction,
                CompilerAuthoritySourceKind.CompilerAbiValidator,
                CompilerEvidenceClass.DescriptorAbiEvidence,
                CompilerPublicationClass.CarrierBytesOnly,
                CompilerExecutionClaim.HelperOnly,
                CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
                CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
                CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired |
                CompilerRuntimeAuthorityDependency.RuntimeCommitRequired |
                CompilerRuntimeAuthorityDependency.RuntimeRetireRequired |
                CompilerRuntimeAuthorityDependency.RuntimePublicationRequired),
            CompilerEmissionClass.CarrierCandidate,
            CompilerProductionLoweringStatus.HelperAbiOnly,
            NoFallbackProof.Forbidden(
                noFallbackProofId,
                "Positive helper emission cannot select cross-contour fallback or production authority."),
            FallbackPolicy.Forbidden("Positive helper emission cannot route fallback."),
            producedArtifacts ?? [CompilerProducedArtifactKind.Carrier, CompilerProducedArtifactKind.Evidence],
            requiredArtifacts ??
            [
                CompilerRequiredArtifactKind.RuntimeLegalityA,
                CompilerRequiredArtifactKind.RuntimeLegalityB,
                CompilerRequiredArtifactKind.RuntimeCommit,
                CompilerRequiredArtifactKind.RuntimeRetire,
                CompilerRequiredArtifactKind.RuntimePublication
            ],
            [CompilerRejectReason.HelperAbiOnly, CompilerRejectReason.CarrierIsNotPublication],
            LegacyTranslation: null,
            reason);
    }
}

public sealed record CompilerPositiveEmissionResult<TPlan>(
    CompilerLoweringDecision Decision,
    TPlan Plan,
    string SourceApi,
    string Reason)
    where TPlan : class;

public sealed record CompilerRejectedLoweringDecision(
    CompilerLoweringDecisionKind DecisionKind,
    SemanticIntentKind IntentKind,
    ExecutionContourKind ContourKind,
    CompilerCoreResultHeader Header,
    CompilerEmissionClass EmissionClass,
    CompilerProductionLoweringStatus ProductionLoweringStatus,
    NoFallbackProof NoFallbackProof,
    FallbackPolicy FallbackPolicy,
    IReadOnlyList<CompilerProducedArtifactKind> ProducedArtifacts,
    IReadOnlyList<CompilerRequiredArtifactKind> RequiredArtifacts,
    IReadOnlyList<CompilerRejectReason> RejectReasons,
    LegacyApiTranslation? LegacyTranslation,
    string Reason)
    : CompilerLoweringDecision(
        DecisionKind,
        IntentKind,
        ContourKind,
        Header,
        EmissionClass,
        ProductionLoweringStatus,
        NoFallbackProof,
        FallbackPolicy,
        ProducedArtifacts,
        RequiredArtifacts,
        RejectReasons,
        LegacyTranslation,
        Reason);

public sealed record CompilerLegacyStructuralLoweringDecision(
    CompilerLoweringDecisionKind DecisionKind,
    SemanticIntentKind IntentKind,
    ExecutionContourKind ContourKind,
    CompilerCoreResultHeader Header,
    CompilerEmissionClass EmissionClass,
    CompilerProductionLoweringStatus ProductionLoweringStatus,
    NoFallbackProof NoFallbackProof,
    FallbackPolicy FallbackPolicy,
    IReadOnlyList<CompilerProducedArtifactKind> ProducedArtifacts,
    IReadOnlyList<CompilerRequiredArtifactKind> RequiredArtifacts,
    IReadOnlyList<CompilerRejectReason> RejectReasons,
    LegacyApiTranslation? LegacyTranslation,
    string Reason)
    : CompilerLoweringDecision(
        DecisionKind,
        IntentKind,
        ContourKind,
        Header,
        EmissionClass,
        ProductionLoweringStatus,
        NoFallbackProof,
        FallbackPolicy,
        ProducedArtifacts,
        RequiredArtifacts,
        RejectReasons,
        LegacyTranslation,
        Reason);

public sealed record CompilerLegacyBackendObservationLoweringDecision(
    CompilerLoweringDecisionKind DecisionKind,
    SemanticIntentKind IntentKind,
    ExecutionContourKind ContourKind,
    CompilerCoreResultHeader Header,
    CompilerEmissionClass EmissionClass,
    CompilerProductionLoweringStatus ProductionLoweringStatus,
    NoFallbackProof NoFallbackProof,
    FallbackPolicy FallbackPolicy,
    IReadOnlyList<CompilerProducedArtifactKind> ProducedArtifacts,
    IReadOnlyList<CompilerRequiredArtifactKind> RequiredArtifacts,
    IReadOnlyList<CompilerRejectReason> RejectReasons,
    LegacyApiTranslation? LegacyTranslation,
    string Reason)
    : CompilerLoweringDecision(
        DecisionKind,
        IntentKind,
        ContourKind,
        Header,
        EmissionClass,
        ProductionLoweringStatus,
        NoFallbackProof,
        FallbackPolicy,
        ProducedArtifacts,
        RequiredArtifacts,
        RejectReasons,
        LegacyTranslation,
        Reason);

public sealed record CompilerLegacyHelperRecoveryLoweringDecision(
    CompilerLoweringDecisionKind DecisionKind,
    SemanticIntentKind IntentKind,
    ExecutionContourKind ContourKind,
    CompilerCoreResultHeader Header,
    CompilerEmissionClass EmissionClass,
    CompilerProductionLoweringStatus ProductionLoweringStatus,
    NoFallbackProof NoFallbackProof,
    FallbackPolicy FallbackPolicy,
    IReadOnlyList<CompilerProducedArtifactKind> ProducedArtifacts,
    IReadOnlyList<CompilerRequiredArtifactKind> RequiredArtifacts,
    IReadOnlyList<CompilerRejectReason> RejectReasons,
    LegacyApiTranslation? LegacyTranslation,
    string Reason)
    : CompilerLoweringDecision(
        DecisionKind,
        IntentKind,
        ContourKind,
        Header,
        EmissionClass,
        ProductionLoweringStatus,
        NoFallbackProof,
        FallbackPolicy,
        ProducedArtifacts,
        RequiredArtifacts,
        RejectReasons,
        LegacyTranslation,
        Reason);

public sealed record CompilerPositiveHelperEmissionLoweringDecision(
    CompilerLoweringDecisionKind DecisionKind,
    SemanticIntentKind IntentKind,
    ExecutionContourKind ContourKind,
    CompilerCoreResultHeader Header,
    CompilerEmissionClass EmissionClass,
    CompilerProductionLoweringStatus ProductionLoweringStatus,
    NoFallbackProof NoFallbackProof,
    FallbackPolicy FallbackPolicy,
    IReadOnlyList<CompilerProducedArtifactKind> ProducedArtifacts,
    IReadOnlyList<CompilerRequiredArtifactKind> RequiredArtifacts,
    IReadOnlyList<CompilerRejectReason> RejectReasons,
    LegacyApiTranslation? LegacyTranslation,
    string Reason)
    : CompilerLoweringDecision(
        DecisionKind,
        IntentKind,
        ContourKind,
        Header,
        EmissionClass,
        ProductionLoweringStatus,
        NoFallbackProof,
        FallbackPolicy,
        ProducedArtifacts,
        RequiredArtifacts,
        RejectReasons,
        LegacyTranslation,
        Reason);
