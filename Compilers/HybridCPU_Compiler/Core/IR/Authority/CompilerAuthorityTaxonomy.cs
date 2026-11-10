using System;

namespace HybridCPU.Compiler.Core.IR.Authority;

/// <summary>
/// Compiler-side authority class. These values classify compiler products only;
/// they do not grant runtime legality, execution, commit, retire or architectural publication.
/// </summary>
public enum CompilerAuthorityClass
{
    None = 0,
    StructuralAgreement,
    StructuralAdmissionEvidence,
    StructuralPlacementEvidence,
    TransportConstruction,
    DescriptorAbiConstruction,
    TypedSlotFactProduction,
    CompilerEvidenceProduction,
    RuntimeBridgePreparation,
    RuntimeAuthorityRequired
}

/// <summary>
/// Source of the limited compiler-side authority described by <see cref="CompilerAuthorityClass"/>.
/// Runtime-owned policy references are observations, not permission to execute.
/// </summary>
public enum CompilerAuthoritySourceKind
{
    None = 0,
    CompilerStructuralModel,
    CompilerAbiValidator,
    CompilerCarrierSerializer,
    CompilerSidebandProjector,
    RuntimeContractObservation,
    RuntimeOwnedPolicyReference,
    TestOnlyHarness
}

/// <summary>
/// Runtime work still required after a compiler product exists.
/// This is intentionally a dependency map, not a compiler execution claim.
/// </summary>
[Flags]
public enum CompilerRuntimeAuthorityDependency
{
    None = 0,
    RuntimeLegalityARequired = 1 << 0,
    RuntimeLegalityBRequired = 1 << 1,
    RuntimeCommitRequired = 1 << 2,
    RuntimeRetireRequired = 1 << 3,
    RuntimePublicationRequired = 1 << 4,
    RuntimeExecutionRequired = 1 << 5,
    NoRuntimeActionBecauseNoEmission = 1 << 6
}

/// <summary>
/// Classifies evidence emitted or observed by the compiler.
/// Evidence is not runtime authority.
/// </summary>
public enum CompilerEvidenceClass
{
    NoEvidence = 0,
    ParserEvidence,
    StructuralEvidence,
    StructuralAdmissionEvidence,
    StructuralPlacementEvidence,
    DescriptorAbiEvidence,
    TypedSlotEvidence,
    HazardSummaryEvidence,
    ResourceExpectationEvidence,
    RuntimeContractObservationEvidence,
    NoFallbackEvidence,
    NegativeGateEvidence
}

/// <summary>
/// Publication of compiler products only. This is not architectural memory/register
/// publication and not runtime commit/retire.
/// </summary>
public enum CompilerPublicationClass
{
    NoPublication = 0,
    CarrierBytesOnly,
    SidebandOnly,
    DescriptorOnly,
    FactsOnly,
    EvidenceOnly,
    RuntimeBridgeEnvelopeOnly
}

/// <summary>
/// What, if anything, the compiler result claims about execution.
/// No value represents completed runtime execution.
/// </summary>
public enum CompilerExecutionClaim
{
    NoExecutionClaim = 0,
    ParserOnly,
    HelperOnly,
    ScopedTestContourOnly,
    RuntimeExecutionRequired,
    ProductionExecutionForbidden
}

/// <summary>
/// Common authority/evidence header for new compiler Core result objects.
/// </summary>
public sealed record CompilerCoreResultHeader(
    CompilerAuthorityClass AuthorityClass,
    CompilerAuthoritySourceKind AuthoritySourceKind,
    CompilerEvidenceClass EvidenceClass,
    CompilerPublicationClass PublicationClass,
    CompilerExecutionClaim ExecutionClaim,
    CompilerRuntimeAuthorityDependency RuntimeAuthorityDependency)
{
    public static CompilerCoreResultHeader NoEmissionParserOnly(CompilerEvidenceClass evidenceClass) =>
        new(
            CompilerAuthorityClass.CompilerEvidenceProduction,
            CompilerAuthoritySourceKind.CompilerAbiValidator,
            evidenceClass,
            CompilerPublicationClass.EvidenceOnly,
            CompilerExecutionClaim.ParserOnly,
            CompilerRuntimeAuthorityDependency.NoRuntimeActionBecauseNoEmission);

    public static CompilerCoreResultHeader TransportRequiresRuntimeLegality(
        CompilerAuthorityClass authorityClass,
        CompilerAuthoritySourceKind authoritySourceKind,
        CompilerEvidenceClass evidenceClass,
        CompilerPublicationClass publicationClass) =>
        new(
            authorityClass,
            authoritySourceKind,
            evidenceClass,
            publicationClass,
            CompilerExecutionClaim.RuntimeExecutionRequired,
            CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
            CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
            CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired);
}
