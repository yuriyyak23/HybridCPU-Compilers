using System;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Bridge;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Evidence;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.IR.Lowering;
using Xunit;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerPhase09VmxSecureComputeNegativeMatrixTests
{
    [Fact]
    public void VmxBackendEmissionRequestRejectsWithNoEmissionAndNoVmcsOwnership()
    {
        CompilerSemanticIntent intent = new CompilerSemanticIntent(
            SemanticIntentKind.VmxCompatibilityProjection,
            "VMX",
            RequiresDescriptor: false,
            RequiresSideband: true,
            RequiresToken: false,
            RequiresRuntimeLegality: false,
            IsCompatibilityProjection: true,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: false,
            IsParserOnly: true,
            "Phase09 VMX backend emission negative gate.");
        CompilerExecutionContourSelection selection =
            CompilerDefaultExecutionContourSelector.Instance.SelectContour(intent);
        CompilerLoweringContext context = new CompilerLoweringContext(
            new CompilerTargetProfile(
                "phase09-vmx-backend-request",
                AllowsCarrierEmission: true,
                AllowsBackendEmission: true),
            "CompilerPhase09VmxSecureComputeNegativeMatrixTests");

        IContourAnalyzer analyzer =
            DefaultContourLoweringProviderRegistry.Instance.ResolveAnalyzer(ExecutionContourKind.VmxProjectionOnly);
        ContourAnalysisReport analysis = analyzer.Analyze(intent, context);
        IContourLoweringProvider provider =
            DefaultContourLoweringProviderRegistry.Instance.ResolveProvider(ExecutionContourKind.VmxProjectionOnly);
        CompilerLoweringDecision providerDecision = provider.Lower(intent, analysis, context);
        CompilerLoweringDecision backendRequestDecision = CompilerLoweringDecision.Reject(
            intent.Kind,
            ExecutionContourKind.VmxProjectionOnly,
            CompilerRejectReason.VmxBackendEmissionForbidden,
            "VMX backend emission request is forbidden; VMX is projection/no-emission only.");

        Assert.Equal(ExecutionContourKind.VmxProjectionOnly, selection.Kind);
        Assert.True(selection.IsEmissionForbidden);
        Assert.True(selection.IsFallbackForbidden);
        Assert.False(selection.RequiresRuntimeLegalityA);
        Assert.False(selection.RequiresRuntimeLegalityB);
        Assert.Contains("VMCS is not compiler-owned state", selection.SelectionReason, StringComparison.Ordinal);
        Assert.Equal(CompilerCapabilityObservationState.NoEmission, analysis.CapabilityObservation.State);
        Assert.Equal(CompilerRuntimeAuthorityDependency.NoRuntimeActionBecauseNoEmission, analysis.CapabilityObservation.RuntimeAuthorityDependency);
        Assert.Contains("projection/no-emission", analysis.CapabilityObservation.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VMCS is not compiler-owned state", analysis.CapabilityObservation.Reason, StringComparison.Ordinal);
        Assert.Equal(CompilerEmissionClass.NoEmission, providerDecision.EmissionClass);
        Assert.Equal(CompilerProductionLoweringStatus.Rejected, providerDecision.ProductionLoweringStatus);
        Assert.Equal(CompilerRejectReason.VmxBackendEmissionForbidden, Assert.Single(backendRequestDecision.RejectReasons));
        Assert.Equal(CompilerEmissionClass.NoEmission, backendRequestDecision.EmissionClass);
        Assert.Equal(CompilerExecutionClaim.ParserOnly, backendRequestDecision.ExecutionClaim);
        Assert.Equal(CompilerPublicationClass.EvidenceOnly, backendRequestDecision.PublicationClass);
        Assert.Empty(backendRequestDecision.ProducedArtifacts);
        Assert.False(backendRequestDecision.FallbackPolicy.AllowsCrossContourFallback);
    }

    [Fact]
    public void SecureComputeBackendExecutionRequestRejectsWithPolicyAdmissionEvidenceOnly()
    {
        CompilerSemanticIntent intent = new CompilerSemanticIntent(
            SemanticIntentKind.SecureComputeAdmission,
            "SecureCompute",
            RequiresDescriptor: false,
            RequiresSideband: true,
            RequiresToken: false,
            RequiresRuntimeLegality: false,
            IsCompatibilityProjection: false,
            IsPolicyAdmissionOnly: true,
            IsHelperAbiOnly: false,
            IsParserOnly: true,
            "Phase09 SecureCompute backend execution negative gate.");
        CompilerExecutionContourSelection selection =
            CompilerDefaultExecutionContourSelector.Instance.SelectContour(intent);
        CompilerLoweringContext context = new CompilerLoweringContext(
            new CompilerTargetProfile(
                "phase09-securecompute-backend-request",
                AllowsCarrierEmission: true,
                AllowsBackendEmission: true),
            "CompilerPhase09VmxSecureComputeNegativeMatrixTests");

        IContourAnalyzer analyzer =
            DefaultContourLoweringProviderRegistry.Instance.ResolveAnalyzer(ExecutionContourKind.SecureComputePolicyAdmissionOnly);
        ContourAnalysisReport analysis = analyzer.Analyze(intent, context);
        IContourLoweringProvider provider =
            DefaultContourLoweringProviderRegistry.Instance.ResolveProvider(ExecutionContourKind.SecureComputePolicyAdmissionOnly);
        CompilerLoweringDecision providerDecision = provider.Lower(intent, analysis, context);
        CompilerLoweringDecision backendRequestDecision = CompilerLoweringDecision.Reject(
            intent.Kind,
            ExecutionContourKind.SecureComputePolicyAdmissionOnly,
            CompilerRejectReason.SecureComputeEmissionForbidden,
            "SecureCompute backend execution request is forbidden; compiler layer is policy/admission/evidence-only.");

        Assert.Equal(ExecutionContourKind.SecureComputePolicyAdmissionOnly, selection.Kind);
        Assert.True(selection.IsEmissionForbidden);
        Assert.True(selection.IsFallbackForbidden);
        Assert.False(selection.RequiresRuntimeLegalityA);
        Assert.False(selection.RequiresRuntimeLegalityB);
        Assert.Contains("policy/admission/evidence-only", selection.SelectionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure backend execution is forbidden", selection.SelectionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CompilerCapabilityObservationState.NoEmission, analysis.CapabilityObservation.State);
        Assert.Equal(CompilerRuntimeAuthorityDependency.NoRuntimeActionBecauseNoEmission, analysis.CapabilityObservation.RuntimeAuthorityDependency);
        Assert.Contains("policy/admission/evidence-only", analysis.CapabilityObservation.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure backend execution is forbidden", analysis.CapabilityObservation.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CompilerEmissionClass.NoEmission, providerDecision.EmissionClass);
        Assert.Equal(CompilerProductionLoweringStatus.Rejected, providerDecision.ProductionLoweringStatus);
        Assert.Equal(CompilerRejectReason.SecureComputeEmissionForbidden, Assert.Single(backendRequestDecision.RejectReasons));
        Assert.Equal(CompilerEmissionClass.NoEmission, backendRequestDecision.EmissionClass);
        Assert.Equal(CompilerExecutionClaim.ParserOnly, backendRequestDecision.ExecutionClaim);
        Assert.Equal(CompilerPublicationClass.EvidenceOnly, backendRequestDecision.PublicationClass);
        Assert.Empty(backendRequestDecision.ProducedArtifacts);
        Assert.False(backendRequestDecision.FallbackPolicy.AllowsCrossContourFallback);
    }

    [Theory]
    [InlineData(SemanticIntentKind.VmxCompatibilityProjection, ExecutionContourKind.VmxProjectionOnly, "VMX projection evidence is not VMCS state.")]
    [InlineData(SemanticIntentKind.SecureComputeAdmission, ExecutionContourKind.SecureComputePolicyAdmissionOnly, "SecureCompute admission evidence is not secure backend execution.")]
    public void ProjectionAndAdmissionEvidenceSnapshotsRemainHostOwnedEvidenceOnly(
        SemanticIntentKind intentKind,
        ExecutionContourKind contourKind,
        string statement)
    {
        CompilerEvidenceRecord record = new CompilerEvidenceRecord(
            CompilerEvidenceClass.NegativeGateEvidence,
            EvidenceOwnershipDomain.CompilerHostOwned,
            EvidenceAuthoritySemantics.EvidenceOnly,
            $"phase09-{contourKind}",
            statement,
            IsAuthority: false,
            "compiler evidence only; no backend execution, VMCS ownership, commit, retire or publication");
        CompilerEvidenceSnapshot snapshot = new CompilerEvidenceSnapshot(
            Guid.NewGuid(),
            intentKind,
            contourKind,
            CompilerCapabilityObservationState.NoEmission,
            new CompilerLoweringDecisionSummary(
                CompilerLoweringDecisionKind.Rejected,
                CompilerEmissionClass.NoEmission,
                CompilerProductionLoweringStatus.Rejected,
                intentKind == SemanticIntentKind.VmxCompatibilityProjection
                    ? CompilerRejectReason.VmxBackendEmissionForbidden
                    : CompilerRejectReason.SecureComputeEmissionForbidden,
                CompilerExecutionClaim.ParserOnly,
                CompilerPublicationClass.EvidenceOnly,
                CompilerRuntimeAuthorityDependency.NoRuntimeActionBecauseNoEmission,
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
            EvidenceOwnershipDomain.CompilerHostOwned,
            EvidenceAuthoritySemantics.EvidenceOnly,
            SidebandRequirement.OptionalCompatibility,
            DescriptorAbiStatus.None,
            "ObservedOnly",
            TypedSlotFactStaging.MissingCompatibility,
            BridgeIngressStatus.BridgeIngressRejected,
            FallbackPolicyKind.Forbidden,
            $"phase09-no-backend:{contourKind}",
            [record],
            ["backend emission", "runtime authority", "architectural publication"],
            RuntimeLegalityAStillRequired: false,
            RuntimeLegalityBStillRequired: false,
            RuntimeCommitStillRequired: false,
            RuntimeRetireStillRequired: false,
            RuntimePublicationStillRequired: false,
            statement);

        EvidenceIsolationValidationResult isolation =
            EvidenceIsolationValidator.Validate(snapshot);
        IReadOnlyDictionary<string, string> serialized =
            CompilerEvidenceSnapshotSerializer.Serialize(snapshot);

        Assert.True(isolation.IsEvidenceIsolated, string.Join(Environment.NewLine, isolation.Diagnostics));
        Assert.Equal("NoEmission", serialized["emission.class"]);
        Assert.Equal("Rejected", serialized["decision.kind"]);
        Assert.Equal("EvidenceOnly", serialized["evidence.authority_semantics"]);
        Assert.Equal("Forbidden", serialized["fallback.policy"]);
        Assert.Equal("False", serialized["runtime_publication.required"]);
    }

    [Fact]
    public void VmxAndSecureComputeDoNotPrepareCarrierDescriptorBridgeOrProductionPackage()
    {
        CompilerLoweringDecision vmx = CompilerLoweringDecision.Reject(
            SemanticIntentKind.VmxCompatibilityProjection,
            ExecutionContourKind.VmxProjectionOnly,
            CompilerRejectReason.VmxBackendEmissionForbidden,
            "VMX backend emission forbidden.");
        CompilerLoweringDecision secure = CompilerLoweringDecision.Reject(
            SemanticIntentKind.SecureComputeAdmission,
            ExecutionContourKind.SecureComputePolicyAdmissionOnly,
            CompilerRejectReason.SecureComputeEmissionForbidden,
            "SecureCompute backend execution forbidden.");

        foreach (CompilerLoweringDecision decision in new[] { vmx, secure })
        {
            Assert.Equal(CompilerEmissionClass.NoEmission, decision.EmissionClass);
            Assert.Equal(CompilerProductionLoweringStatus.Rejected, decision.ProductionLoweringStatus);
            Assert.Empty(decision.ProducedArtifacts);
            Assert.DoesNotContain(CompilerProducedArtifactKind.Carrier, decision.ProducedArtifacts);
            Assert.DoesNotContain(CompilerProducedArtifactKind.Descriptor, decision.ProducedArtifacts);
            Assert.DoesNotContain(CompilerProducedArtifactKind.TypedSlotFacts, decision.ProducedArtifacts);
            Assert.Equal(CompilerPublicationClass.EvidenceOnly, decision.PublicationClass);
            Assert.False(decision.FallbackPolicy.AllowsCrossContourFallback);
            Assert.Equal(FallbackPolicyKind.Forbidden, decision.NoFallbackProof.PolicyKind);
        }
    }
}
