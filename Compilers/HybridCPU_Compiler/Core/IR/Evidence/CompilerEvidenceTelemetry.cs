using System;
using System.Collections.Generic;
using System.Linq;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Bridge;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.IR.Lowering;

namespace HybridCPU.Compiler.Core.IR.Evidence;

public enum EvidenceOwnershipDomain
{
    CompilerHostOwned = 0,
    RuntimeObserved,
    TestHarnessOwned,
    GuestVisibleForbidden,
    DomainArchitecturalStateForbidden
}

public enum EvidenceAuthoritySemantics
{
    EvidenceOnly = 0,
    DiagnosticOnly,
    CompatibilityObservation,
    RuntimePolicyReferenceOnly,
    ForbiddenAsAuthority
}

public sealed record CompilerEvidenceRecord(
    CompilerEvidenceClass EvidenceClass,
    EvidenceOwnershipDomain OwnershipDomain,
    EvidenceAuthoritySemantics AuthoritySemantics,
    string Source,
    string Statement,
    bool IsAuthority,
    string AuthorityBoundary);

public sealed record CompilerLoweringDecisionSummary(
    CompilerLoweringDecisionKind DecisionKind,
    CompilerEmissionClass EmissionClass,
    CompilerProductionLoweringStatus ProductionLoweringStatus,
    CompilerRejectReason? RejectReason,
    CompilerExecutionClaim ExecutionClaim,
    CompilerPublicationClass PublicationClass,
    CompilerRuntimeAuthorityDependency RuntimeAuthorityDependency,
    bool CarrierEmitted,
    bool SidebandEmitted,
    bool DescriptorEmitted,
    bool TypedSlotFactsEmitted,
    bool StructuralAgreementEmitted,
    bool EvidenceEmitted,
    bool BridgeEnvelopePrepared,
    bool ProductionLoweringClaimed);

public sealed record CompilerEvidenceSnapshot(
    Guid EvidenceId,
    SemanticIntentKind IntentKind,
    ExecutionContourKind ContourKind,
    CompilerCapabilityObservationState CapabilityObservationState,
    CompilerLoweringDecisionSummary DecisionSummary,
    CompilerAuthorityClass AuthorityClass,
    CompilerAuthoritySourceKind AuthoritySourceKind,
    CompilerEvidenceClass EvidenceClass,
    EvidenceOwnershipDomain OwnershipDomain,
    EvidenceAuthoritySemantics AuthoritySemantics,
    SidebandRequirement SidebandRequirement,
    DescriptorAbiStatus DescriptorAbiStatus,
    string TypedSlotPolicyMode,
    TypedSlotFactStaging TypedSlotStaging,
    BridgeIngressStatus BridgeStatus,
    FallbackPolicyKind FallbackPolicyKind,
    string FallbackProofId,
    IReadOnlyList<CompilerEvidenceRecord> Records,
    IReadOnlyList<string> MissingGates,
    bool RuntimeLegalityAStillRequired,
    bool RuntimeLegalityBStillRequired,
    bool RuntimeCommitStillRequired,
    bool RuntimeRetireStillRequired,
    bool RuntimePublicationStillRequired,
    string Reason);

public static class CompilerEvidenceSnapshotSerializer
{
    public static IReadOnlyDictionary<string, string> Serialize(CompilerEvidenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["intent.kind"] = snapshot.IntentKind.ToString(),
            ["contour.kind"] = snapshot.ContourKind.ToString(),
            ["capability.observation_state"] = snapshot.CapabilityObservationState.ToString(),
            ["decision.kind"] = snapshot.DecisionSummary.DecisionKind.ToString(),
            ["emission.class"] = snapshot.DecisionSummary.EmissionClass.ToString(),
            ["production_lowering.status"] = snapshot.DecisionSummary.ProductionLoweringStatus.ToString(),
            ["authority.class"] = snapshot.AuthorityClass.ToString(),
            ["authority.source_kind"] = snapshot.AuthoritySourceKind.ToString(),
            ["evidence.class"] = snapshot.EvidenceClass.ToString(),
            ["evidence.ownership_domain"] = snapshot.OwnershipDomain.ToString(),
            ["evidence.authority_semantics"] = snapshot.AuthoritySemantics.ToString(),
            ["runtime_dependency"] = snapshot.DecisionSummary.RuntimeAuthorityDependency.ToString(),
            ["runtime_legality_a.required"] = snapshot.RuntimeLegalityAStillRequired.ToString(),
            ["runtime_legality_b.required"] = snapshot.RuntimeLegalityBStillRequired.ToString(),
            ["runtime_commit.required"] = snapshot.RuntimeCommitStillRequired.ToString(),
            ["runtime_retire.required"] = snapshot.RuntimeRetireStillRequired.ToString(),
            ["runtime_publication.required"] = snapshot.RuntimePublicationStillRequired.ToString(),
            ["sideband.requirement"] = snapshot.SidebandRequirement.ToString(),
            ["descriptor.abi_status"] = snapshot.DescriptorAbiStatus.ToString(),
            ["typed_slot.policy_mode"] = snapshot.TypedSlotPolicyMode,
            ["typed_slot.staging"] = snapshot.TypedSlotStaging.ToString(),
            ["reject.reason"] = snapshot.DecisionSummary.RejectReason?.ToString() ?? string.Empty,
            ["fallback.policy"] = snapshot.FallbackPolicyKind.ToString(),
            ["fallback.proof_id"] = snapshot.FallbackProofId,
            ["bridge.status"] = snapshot.BridgeStatus.ToString(),
            ["missing_gates"] = string.Join(",", snapshot.MissingGates),
            ["reason"] = snapshot.Reason
        };
    }
}

public sealed record EvidenceIsolationValidationResult(
    [property: Obsolete(
        "Evidence IsValid is an isolation validation predicate only; it is not runtime legality or publication authority.",
        false)]
    bool IsValid,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>
    /// True when evidence remains isolated from guest/domain architectural state.
    /// This is not runtime legality or publication authority.
    /// </summary>
#pragma warning disable CS0618
    public bool IsEvidenceIsolated => IsValid;
#pragma warning restore CS0618

    /// <summary>
    /// True when host-owned evidence attempted to cross an isolation boundary.
    /// </summary>
    public bool HasIsolationViolations => Diagnostics.Count != 0;
}

public static class EvidenceIsolationValidator
{
    public static EvidenceIsolationValidationResult Validate(CompilerEvidenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        List<string> diagnostics = new List<string>();
        ValidateDomain(snapshot.OwnershipDomain, "snapshot", diagnostics);

        foreach (CompilerEvidenceRecord record in snapshot.Records)
        {
            ValidateDomain(record.OwnershipDomain, record.Source, diagnostics);
            if (record.IsAuthority &&
                record.AuthoritySemantics is EvidenceAuthoritySemantics.ForbiddenAsAuthority
                    or EvidenceAuthoritySemantics.EvidenceOnly
                    or EvidenceAuthoritySemantics.DiagnosticOnly
                    or EvidenceAuthoritySemantics.CompatibilityObservation
                    or EvidenceAuthoritySemantics.RuntimePolicyReferenceOnly)
            {
                diagnostics.Add($"{record.Source}: evidence record claims authority despite non-authority semantics.");
            }
        }

        return new(diagnostics.Count == 0, diagnostics.ToArray());
    }

    private static void ValidateDomain(
        EvidenceOwnershipDomain domain,
        string source,
        List<string> diagnostics)
    {
        if (domain is EvidenceOwnershipDomain.GuestVisibleForbidden
            or EvidenceOwnershipDomain.DomainArchitecturalStateForbidden)
        {
            diagnostics.Add($"{source}: host/compiler evidence cannot enter guest-visible or domain architectural state.");
        }
    }
}
