using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Bridge;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Evidence;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.IR.Lowering;
using Xunit;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerPhase09FallbackEvidenceNegativeMatrixTests
{
    [Theory]
    [InlineData(ExecutionContourKind.NativeVliwScalar, ExecutionContourKind.MatrixTileHelperOnly)]
    [InlineData(ExecutionContourKind.StreamEngineVector, ExecutionContourKind.NativeVliwScalar)]
    [InlineData(ExecutionContourKind.DmaStreamComputeLane6, ExecutionContourKind.L7SdcLane7)]
    [InlineData(ExecutionContourKind.L7SdcLane7, ExecutionContourKind.DmaStreamComputeLane6)]
    [InlineData(ExecutionContourKind.VmxProjectionOnly, ExecutionContourKind.NativeVliwScalar)]
    [InlineData(ExecutionContourKind.SecureComputePolicyAdmissionOnly, ExecutionContourKind.NativeVliwScalar)]
    public void CrossContourProviderMismatchRejectsWithoutHiddenFallback(
        ExecutionContourKind analysisContour,
        ExecutionContourKind providerContour)
    {
        CompilerSemanticIntent intent = CompilerSemanticIntent.Unknown(
            "Phase09 hidden fallback negative gate.");
        CompilerLoweringContext context = new CompilerLoweringContext(
            new CompilerTargetProfile("phase09-fallback-evidence"),
            "CompilerPhase09FallbackEvidenceNegativeMatrixTests");
        IContourAnalyzer analyzer =
            DefaultContourLoweringProviderRegistry.Instance.ResolveAnalyzer(analysisContour);
        ContourAnalysisReport analysis = analyzer.Analyze(intent, context);
        IContourLoweringProvider provider =
            DefaultContourLoweringProviderRegistry.Instance.ResolveProvider(providerContour);

        CompilerLoweringDecision decision = provider.Lower(intent, analysis, context);

        Assert.Equal(CompilerLoweringDecisionKind.Rejected, decision.DecisionKind);
        Assert.Equal(CompilerRejectReason.CrossContourFallbackForbidden, Assert.Single(decision.RejectReasons));
        Assert.False(decision.FallbackPolicy.AllowsCrossContourFallback);
        Assert.Equal(FallbackPolicyKind.Forbidden, decision.FallbackPolicy.Kind);
        Assert.Equal(FallbackPolicyKind.Forbidden, decision.NoFallbackProof.PolicyKind);
        Assert.Equal(CompilerEmissionClass.NoEmission, decision.EmissionClass);
        Assert.Empty(decision.ProducedArtifacts);
    }

    [Fact]
    public void SameContourStructuralRetryPolicyIsRecordedSeparatelyFromLoweringFallback()
    {
        FallbackPolicy sameContourRetry = new FallbackPolicy(
            FallbackPolicyKind.SameContourStructuralRetryOnly,
            AllowsCrossContourFallback: false,
            "Same-contour placement retry may adjust structural evidence only.");
        NoFallbackProof noLoweringFallback = NoFallbackProof.Forbidden(
            "phase09-same-contour-structural-retry-not-lowering-fallback",
            "Structural placement retry is not production lowering fallback.");

        Assert.Equal(FallbackPolicyKind.SameContourStructuralRetryOnly, sameContourRetry.Kind);
        Assert.False(sameContourRetry.AllowsCrossContourFallback);
        Assert.Equal(FallbackPolicyKind.Forbidden, noLoweringFallback.PolicyKind);
        Assert.Contains("structural", sameContourRetry.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not production lowering fallback", noLoweringFallback.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(EvidenceOwnershipDomain.GuestVisibleForbidden)]
    [InlineData(EvidenceOwnershipDomain.DomainArchitecturalStateForbidden)]
    public void HostOwnedEvidenceCannotEnterGuestOrDomainArchitecturalState(
        EvidenceOwnershipDomain forbiddenDomain)
    {
        CompilerEvidenceSnapshot snapshot = CreateNegativeSnapshot(
            forbiddenDomain,
            [new CompilerEvidenceRecord(
                CompilerEvidenceClass.NegativeGateEvidence,
                forbiddenDomain,
                EvidenceAuthoritySemantics.EvidenceOnly,
                "phase09-forbidden-evidence-domain",
                "Host-owned evidence must not enter guest or domain architectural state.",
                IsAuthority: false,
                "host/compiler evidence only")]);

        EvidenceIsolationValidationResult result =
            EvidenceIsolationValidator.Validate(snapshot);

        Assert.True(result.HasIsolationViolations);
        Assert.False(result.IsEvidenceIsolated);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Contains("cannot enter guest-visible or domain architectural state", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EvidenceRecordCannotClaimAuthorityWhenSemanticsAreEvidenceOnly()
    {
        CompilerEvidenceSnapshot snapshot = CreateNegativeSnapshot(
            EvidenceOwnershipDomain.CompilerHostOwned,
            [new CompilerEvidenceRecord(
                CompilerEvidenceClass.NegativeGateEvidence,
                EvidenceOwnershipDomain.CompilerHostOwned,
                EvidenceAuthoritySemantics.EvidenceOnly,
                "phase09-authority-claim",
                "Evidence-only record attempted to claim authority.",
                IsAuthority: true,
                "invalid authority claim")]);

        EvidenceIsolationValidationResult result =
            EvidenceIsolationValidator.Validate(snapshot);

        Assert.True(result.HasIsolationViolations);
        Assert.False(result.IsEvidenceIsolated);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Contains("claims authority despite non-authority semantics", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NegativeDecisionTelemetryContainsRequiredAuthorityEvidenceEmissionAndFallbackFields()
    {
        CompilerEvidenceSnapshot snapshot = CreateNegativeSnapshot(
            EvidenceOwnershipDomain.CompilerHostOwned,
            [new CompilerEvidenceRecord(
                CompilerEvidenceClass.NegativeGateEvidence,
                EvidenceOwnershipDomain.CompilerHostOwned,
                EvidenceAuthoritySemantics.EvidenceOnly,
                "phase09-telemetry-fields",
                "Negative decisions produce structured evidence fields.",
                IsAuthority: false,
                "evidence only")]);

        IReadOnlyDictionary<string, string> serialized =
            CompilerEvidenceSnapshotSerializer.Serialize(snapshot);

        Assert.Equal("Rejected", serialized["decision.kind"]);
        Assert.Equal("L7SdcLane7", serialized["contour.kind"]);
        Assert.Equal("CompilerEvidenceProduction", serialized["authority.class"]);
        Assert.Equal("NegativeGateEvidence", serialized["evidence.class"]);
        Assert.Equal("NoEmission", serialized["emission.class"]);
        Assert.Equal("Rejected", serialized["production_lowering.status"]);
        Assert.Equal("Forbidden", serialized["fallback.policy"]);
        Assert.Equal("phase09-fallback-evidence-proof", serialized["fallback.proof_id"]);
        Assert.Equal("True", serialized["runtime_legality_a.required"]);
        Assert.Equal("True", serialized["runtime_publication.required"]);
        Assert.Equal("runtime legality A,runtime legality B,runtime publication", serialized["missing_gates"]);
    }

    private static CompilerEvidenceSnapshot CreateNegativeSnapshot(
        EvidenceOwnershipDomain ownershipDomain,
        IReadOnlyList<CompilerEvidenceRecord> records) =>
        new(
            Guid.NewGuid(),
            SemanticIntentKind.ExternalAcceleratorCommand,
            ExecutionContourKind.L7SdcLane7,
            CompilerCapabilityObservationState.ScopedRuntimeContour,
            new CompilerLoweringDecisionSummary(
                CompilerLoweringDecisionKind.Rejected,
                CompilerEmissionClass.NoEmission,
                CompilerProductionLoweringStatus.Rejected,
                CompilerRejectReason.CrossContourFallbackForbidden,
                CompilerExecutionClaim.NoExecutionClaim,
                CompilerPublicationClass.EvidenceOnly,
                CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
                CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
                CompilerRuntimeAuthorityDependency.RuntimePublicationRequired,
                CarrierEmitted: false,
                SidebandEmitted: false,
                DescriptorEmitted: false,
                TypedSlotFactsEmitted: false,
                StructuralAgreementEmitted: false,
                EvidenceEmitted: true,
                BridgeEnvelopePrepared: false,
                ProductionLoweringClaimed: false),
            CompilerAuthorityClass.CompilerEvidenceProduction,
            CompilerAuthoritySourceKind.CompilerStructuralModel,
            CompilerEvidenceClass.NegativeGateEvidence,
            ownershipDomain,
            EvidenceAuthoritySemantics.EvidenceOnly,
            SidebandRequirement.RequiredForDescriptorSubmit,
            DescriptorAbiStatus.None,
            "ObservedOnly",
            TypedSlotFactStaging.MissingCompatibility,
            BridgeIngressStatus.BridgeIngressRejected,
            FallbackPolicyKind.Forbidden,
            "phase09-fallback-evidence-proof",
            records,
            ["runtime legality A", "runtime legality B", "runtime publication"],
            RuntimeLegalityAStillRequired: true,
            RuntimeLegalityBStillRequired: true,
            RuntimeCommitStillRequired: false,
            RuntimeRetireStillRequired: false,
            RuntimePublicationStillRequired: true,
            "Phase09 fallback/evidence negative decision.");
}
