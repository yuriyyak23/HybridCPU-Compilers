using System;
using System.Collections.Generic;
using System.Linq;

namespace HybridCPU.Compiler.Core.IR.Authority;

/// <summary>
/// Structural-only replacement surface for legacy bundle legality terminology.
/// </summary>
public sealed record CompilerStructuralBundleAdmissionResult(
    CompilerCoreResultHeader Header,
    bool IsStructurallyAdmissible,
    int HazardCount,
    IReadOnlyList<IrHazardDiagnostic> Hazards,
    string SourceMember,
    string Reason);

/// <summary>
/// Structural-only replacement surface for legacy slot legality terminology.
/// </summary>
public sealed record CompilerStructuralPlacementReport(
    CompilerCoreResultHeader Header,
    bool HasStructuralPlacement,
    int CandidateInstructionCount,
    IrIssueSlotMask StructurallyAllowedSlots,
    int DistinctStructurallyAllowedSlotCount,
    IReadOnlyList<IrIssueSlotMask> InstructionStructurallyAllowedSlots,
    string SourceMember,
    string Reason);

/// <summary>
/// Structural-only candidate bundle analysis wrapper. This aggregate does not
/// grant runtime legality, execution, publication, commit, or retire authority.
/// </summary>
public sealed record CompilerStructuralCandidateBundleAnalysis(
    CompilerCoreResultHeader Header,
    bool IsStructurallyAdmissible,
    CompilerStructuralBundleAdmissionResult Admission,
    CompilerStructuralPlacementReport Placement,
    string SourceMember,
    string Reason);

/// <summary>
/// Quarantines legacy compiler bool surfaces as structural evidence only.
/// </summary>
public static class CompilerStructuralAuthorityQuarantine
{
    private const string StructuralAdmissionReason =
        "Legacy compiler legality result is structural admission evidence only; runtime Legality A/B remain runtime-owned.";

    private const string StructuralPlacementReason =
        "Legacy slot assignment result is structural placement evidence only; it is not runtime legality or execution authority.";

    private static readonly CompilerCoreResultHeader StructuralAdmissionHeader = new(
        CompilerAuthorityClass.StructuralAdmissionEvidence,
        CompilerAuthoritySourceKind.CompilerStructuralModel,
        CompilerEvidenceClass.StructuralAdmissionEvidence,
        CompilerPublicationClass.EvidenceOnly,
        CompilerExecutionClaim.NoExecutionClaim,
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired);

    private static readonly CompilerCoreResultHeader StructuralPlacementHeader = new(
        CompilerAuthorityClass.StructuralPlacementEvidence,
        CompilerAuthoritySourceKind.CompilerStructuralModel,
        CompilerEvidenceClass.StructuralPlacementEvidence,
        CompilerPublicationClass.EvidenceOnly,
        CompilerExecutionClaim.NoExecutionClaim,
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired);

    private static readonly CompilerCoreResultHeader StructuralCandidateHeader = new(
        CompilerAuthorityClass.StructuralAgreement,
        CompilerAuthoritySourceKind.CompilerStructuralModel,
        CompilerEvidenceClass.StructuralEvidence,
        CompilerPublicationClass.EvidenceOnly,
        CompilerExecutionClaim.NoExecutionClaim,
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired);

    public static CompilerStructuralBundleAdmissionResult FromBundleLegalityResult(
        IrBundleLegalityResult result,
        string sourceMember)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMember);

        IReadOnlyList<IrHazardDiagnostic> hazards =
            result.Hazards.Count == 0 ? Array.Empty<IrHazardDiagnostic>() : result.Hazards.ToArray();

        return new(
            StructuralAdmissionHeader,
            result.IsStructurallyAdmissible,
            hazards.Count,
            hazards,
            sourceMember,
            StructuralAdmissionReason);
    }

    public static CompilerStructuralPlacementReport FromSlotAssignmentAnalysis(
        IrSlotAssignmentAnalysis analysis,
        string sourceMember)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMember);

        IReadOnlyList<IrIssueSlotMask> instructionSlots = analysis.InstructionStructurallyAllowedSlots.Count == 0
            ? Array.Empty<IrIssueSlotMask>()
            : analysis.InstructionStructurallyAllowedSlots.ToArray();

        return new(
            StructuralPlacementHeader,
            analysis.HasStructuralPlacement,
            analysis.CandidateInstructionCount,
            analysis.StructurallyAllowedSlots,
            analysis.DistinctLegalSlotCount,
            instructionSlots,
            sourceMember,
            StructuralPlacementReason);
    }

    public static CompilerStructuralCandidateBundleAnalysis FromCandidateBundleAnalysis(
        IrCandidateBundleAnalysis analysis,
        string sourceMember)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMember);

        CompilerStructuralBundleAdmissionResult admission =
            FromBundleLegalityResult(analysis.Legality, sourceMember);
        CompilerStructuralPlacementReport placement =
            FromSlotAssignmentAnalysis(analysis.SlotAnalysis, sourceMember);

        return new(
            StructuralCandidateHeader,
            analysis.IsStructurallyAdmissible,
            admission,
            placement,
            sourceMember,
            "Legacy candidate analysis is structural compiler evidence only; it cannot be converted into runtime legality.");
    }
}
