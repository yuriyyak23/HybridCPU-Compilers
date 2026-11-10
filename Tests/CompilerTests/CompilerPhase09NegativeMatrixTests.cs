using System;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Bridge;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.IR.Lowering;
using HybridCPU.Compiler.Core.Runtime;
using Xunit;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Contracts;
using CompilerTypedSlotFactStaging = HybridCPU.Compiler.Core.IR.Bridge.TypedSlotFactStaging;
using TypedSlotBundleFacts = HybridCPU.Compiler.Core.IR.IrTypedSlotBundleFacts;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerPhase09NegativeMatrixTests
{
    [Fact]
    public void DescriptorValidator_RejectsValidTransportDescriptorWithoutPayload()
    {
        DescriptorEnvelope descriptor = new DescriptorEnvelope(
            ExecutionContourKind.L7SdcLane7,
            DescriptorAbiStatus.ValidTransportDescriptor,
            Array.Empty<object>(),
            SidebandRequirement.RequiredForDescriptorSubmit,
            Array.Empty<string>(),
            Header(
                CompilerAuthorityClass.DescriptorAbiConstruction,
                CompilerAuthoritySourceKind.CompilerAbiValidator,
                CompilerEvidenceClass.DescriptorAbiEvidence,
                CompilerPublicationClass.DescriptorOnly));

        CompilerArtifactValidationResult result =
            DescriptorEnvelopeValidator.Instance.Validate(descriptor);

        Assert.False(result.IsAuthorityScopedValidation);
        Assert.True(result.HasValidationDiagnostics);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, result.ExecutionClaim);
        Assert.True(result.RuntimeLegalityStillRequired);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Contains("not execution authority", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RuntimeBridgeAcceptedReport_StillRequiresRuntimeLegalityAndPublicationStages()
    {
        DescriptorEnvelope descriptor = new DescriptorEnvelope(
            ExecutionContourKind.ParserOnly,
            DescriptorAbiStatus.ParserOnlyDescriptor,
            Array.Empty<object>(),
            SidebandRequirement.OptionalCompatibility,
            Array.Empty<string>(),
            Header(
                CompilerAuthorityClass.DescriptorAbiConstruction,
                CompilerAuthoritySourceKind.CompilerAbiValidator,
                CompilerEvidenceClass.DescriptorAbiEvidence,
                CompilerPublicationClass.DescriptorOnly));

        BridgeAcceptanceReport report =
            CompilerRuntimeBridge.Instance.AcceptDescriptor(descriptor);

        Assert.Equal(BridgeIngressStatus.BridgeIngressAccepted, report.Status);
        Assert.True(report.RuntimeLegalityAStillRequired);
        Assert.True(report.RuntimeLegalityBStillRequired);
        Assert.True(report.RuntimeCommitStillRequired);
        Assert.True(report.RuntimeRetireStillRequired);
        Assert.True(report.RuntimePublicationStillRequired);
    }

    [Fact]
    public void TypedSlotFactsBridge_MissingCompatibilityIsWeakerThanValidatedFacts()
    {
        TypedSlotFactsEnvelope facts = new TypedSlotFactsEnvelope(
            Array.Empty<TypedSlotBundleFacts>(),
            NativeTransportRuntimeAdapter.ToCore(CompilerContract.CurrentTypedSlotPolicy.Mode),
            CompilerTypedSlotFactStaging.MissingCompatibility,
            BundleCount: 1,
            TypedSlotFactBundleCount: 0,
            StructuralEvidenceOnly: true,
            RuntimeLegalityStillRequired: true,
            Header(
                CompilerAuthorityClass.TypedSlotFactProduction,
                CompilerAuthoritySourceKind.RuntimeOwnedPolicyReference,
                CompilerEvidenceClass.TypedSlotEvidence,
                CompilerPublicationClass.FactsOnly));

        BridgeAcceptanceReport report =
            CompilerRuntimeBridge.Instance.AcceptTypedSlotFacts(facts);

        Assert.Equal(BridgeIngressStatus.CompatibilityAcceptedMissingFacts, report.Status);
        Assert.True(report.RuntimeLegalityAStillRequired);
        Assert.True(report.RuntimeLegalityBStillRequired);
        Assert.Contains("weaker than validated facts", report.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderRegistry_UnknownContourFailsClosedWithoutScalarFallback()
    {
        CompilerSemanticIntent intent = CompilerSemanticIntent.Unknown("phase09 unknown contour negative gate");
        CompilerLoweringContext context = new CompilerLoweringContext(
            new CompilerTargetProfile("phase09-test"),
            "CompilerPhase09NegativeMatrixTests");

        IContourAnalyzer analyzer =
            DefaultContourLoweringProviderRegistry.Instance.ResolveAnalyzer(ExecutionContourKind.FutureGated);
        ContourAnalysisReport analysis = analyzer.Analyze(intent, context);
        IContourLoweringProvider provider =
            DefaultContourLoweringProviderRegistry.Instance.ResolveProvider(ExecutionContourKind.FutureGated);
        CompilerLoweringDecision decision = provider.Lower(intent, analysis, context);

        Assert.False(analysis.ProviderAvailable);
        Assert.Equal(ExecutionContourKind.UnknownRejected, analysis.ContourKind);
        Assert.Contains(CompilerRejectReason.UnknownContour, analysis.RejectReasons);
        Assert.Equal(CompilerLoweringDecisionKind.Rejected, decision.DecisionKind);
        Assert.Equal(CompilerRejectReason.UnknownContour, Assert.Single(decision.RejectReasons));
        Assert.False(decision.FallbackPolicy.AllowsCrossContourFallback);
        Assert.Equal(FallbackPolicyKind.Forbidden, decision.NoFallbackProof.PolicyKind);
        Assert.Equal(CompilerEmissionClass.NoEmission, decision.EmissionClass);
    }

    [Fact]
    public void ProviderRejectsCrossContourAnalysisWithoutFallback()
    {
        CompilerSemanticIntent intent = CompilerSemanticIntent.Unknown("phase09 cross-contour negative gate");
        CompilerLoweringContext context = new CompilerLoweringContext(
            new CompilerTargetProfile("phase09-test"),
            "CompilerPhase09NegativeMatrixTests");

        IContourAnalyzer scalarAnalyzer =
            DefaultContourLoweringProviderRegistry.Instance.ResolveAnalyzer(ExecutionContourKind.NativeVliwScalar);
        ContourAnalysisReport scalarAnalysis = scalarAnalyzer.Analyze(intent, context);
        IContourLoweringProvider matrixProvider =
            DefaultContourLoweringProviderRegistry.Instance.ResolveProvider(ExecutionContourKind.MatrixTileHelperOnly);

        CompilerLoweringDecision decision = matrixProvider.Lower(intent, scalarAnalysis, context);

        Assert.Equal(CompilerLoweringDecisionKind.Rejected, decision.DecisionKind);
        Assert.Equal(
            CompilerRejectReason.CrossContourFallbackForbidden,
            Assert.Single(decision.RejectReasons));
        Assert.False(decision.FallbackPolicy.AllowsCrossContourFallback);
        Assert.Equal(CompilerProductionLoweringStatus.Rejected, decision.ProductionLoweringStatus);
        Assert.Equal(CompilerExecutionClaim.ParserOnly, decision.ExecutionClaim);
    }

    private static CompilerCoreResultHeader Header(
        CompilerAuthorityClass authorityClass,
        CompilerAuthoritySourceKind sourceKind,
        CompilerEvidenceClass evidenceClass,
        CompilerPublicationClass publicationClass) =>
        new(
            authorityClass,
            sourceKind,
            evidenceClass,
            publicationClass,
            CompilerExecutionClaim.NoExecutionClaim,
            CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
            CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
            CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired);
}
