using System;
using System.Collections.Generic;
using System.Linq;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.IR.Lowering;
using Xunit;

namespace HybridCPU_ISE.Tests.CompilerTests;

/// <summary>
/// Phase 03 tests for the explicit compiler-side gate contract. The positive
/// fixture remains synthetic at the gate-evaluator level. A satisfied gate only
/// describes a package precondition; runtime authority remains pending and is
/// never represented by this result.
/// </summary>
public sealed class CompilerPhase03ExplicitProductionGateTests
{
    [Theory]
    [InlineData(
        ExecutionContourKind.DmaStreamComputeLane6,
        SemanticIntentKind.DmaStreamCompute)]
    [InlineData(
        ExecutionContourKind.L7SdcLane7,
        SemanticIntentKind.ExternalAcceleratorCommand)]
    public void CompleteExplicitGate_ReturnsRuntimeAuthorityPendingStatus(
        ExecutionContourKind contourKind,
        SemanticIntentKind intentKind)
    {
        CompilerProductionLoweringGateResult result =
            CompilerProductionLoweringGateEvaluator.Evaluate(
                CreateContext(contourKind),
                CreateCompleteRequest(contourKind, intentKind));

        Assert.True(result.IsSatisfied);
        Assert.Empty(result.MissingGates);
        Assert.Equal(
            CompilerProductionLoweringStatus.ProductionCarrierPackageRuntimeAuthorityPending,
            result.ProductionLoweringStatus);
        Assert.Equal(
            RuntimeAuthorityPending,
            result.RuntimeAuthorityStillRequired);
        Assert.Contains(
            CompilerProductionLoweringGateIds.Contour(contourKind),
            result.SatisfiedGates);
        Assert.Contains("runtime authority remains pending", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CarrierEmissionFlagAloneCannotSatisfyProfileGate()
    {
        CompilerProductionLoweringContext context = CreateContext(
            ExecutionContourKind.DmaStreamComputeLane6,
            allowsBackendEmission: false);

        CompilerProductionLoweringGateResult result =
            CompilerProductionLoweringGateEvaluator.Evaluate(
                context,
                CreateCompleteRequest(
                    ExecutionContourKind.DmaStreamComputeLane6,
                    SemanticIntentKind.DmaStreamCompute));

        Assert.False(result.IsSatisfied);
        Assert.Contains(CompilerProductionLoweringGateIds.Profile, result.MissingGates);
        Assert.DoesNotContain(CompilerProductionLoweringGateIds.Profile, result.SatisfiedGates);
    }

    [Theory]
    [InlineData(CompilerProductionLoweringSourceKind.CompatibilityAdapter)]
    [InlineData(CompilerProductionLoweringSourceKind.DescriptorEvidence)]
    [InlineData(CompilerProductionLoweringSourceKind.ParserValidation)]
    [InlineData(CompilerProductionLoweringSourceKind.HelperAbi)]
    [InlineData(CompilerProductionLoweringSourceKind.RuntimeGuardObservation)]
    public void CompatibilityAndEvidenceSurfacesCannotSatisfyExplicitProviderGate(
        CompilerProductionLoweringSourceKind sourceKind)
    {
        const ExecutionContourKind contourKind = ExecutionContourKind.DmaStreamComputeLane6;
        CompilerProductionLoweringGateRequest request =
            CreateCompleteRequest(contourKind, SemanticIntentKind.DmaStreamCompute) with
            {
                SourceKind = sourceKind
            };

        CompilerProductionLoweringGateResult result =
            CompilerProductionLoweringGateEvaluator.Evaluate(CreateContext(contourKind), request);

        Assert.False(result.IsSatisfied);
        Assert.Contains(CompilerProductionLoweringGateIds.ExplicitProvider, result.MissingGates);
        Assert.Equal(
            CompilerProductionLoweringStatus.FutureGated,
            result.ProductionLoweringStatus);
    }

    [Fact]
    public void CrossContourClassificationFailsClosedWithoutDscL7Fallback()
    {
        const ExecutionContourKind requestedContour = ExecutionContourKind.L7SdcLane7;
        CompilerProductionLoweringGateRequest request =
            CreateCompleteRequest(requestedContour, SemanticIntentKind.ExternalAcceleratorCommand) with
            {
                ClassifiedContourKind = ExecutionContourKind.DmaStreamComputeLane6
            };

        CompilerProductionLoweringGateResult result =
            CompilerProductionLoweringGateEvaluator.Evaluate(
                CreateContext(requestedContour),
                request);

        Assert.False(result.IsSatisfied);
        Assert.Contains(
            CompilerProductionLoweringGateIds.Contour(requestedContour),
            result.MissingGates);
        Assert.Contains("cross-contour", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void L7DescriptorlessSubmitFailsArtifactGate()
    {
        const ExecutionContourKind contourKind = ExecutionContourKind.L7SdcLane7;
        HashSet<CompilerProductionArtifactEnvelopeKind> artifacts =
            CompleteArtifacts(contourKind);
        artifacts.Remove(CompilerProductionArtifactEnvelopeKind.Descriptor);

        CompilerProductionLoweringGateResult result =
            CompilerProductionLoweringGateEvaluator.Evaluate(
                CreateContext(contourKind),
                CreateCompleteRequest(contourKind, SemanticIntentKind.ExternalAcceleratorCommand) with
                {
                    PresentArtifacts = artifacts
                });

        Assert.False(result.IsSatisfied);
        Assert.Contains(CompilerProductionLoweringGateIds.Artifact, result.MissingGates);
        Assert.Contains("artifact.envelopes-complete", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExecutionContourKind.VmxProjectionOnly)]
    [InlineData(ExecutionContourKind.SecureComputePolicyAdmissionOnly)]
    [InlineData(ExecutionContourKind.UnknownRejected)]
    [InlineData(ExecutionContourKind.FutureGated)]
    public void NonProductionContoursCannotBeEnabledByProfile(
        ExecutionContourKind contourKind)
    {
        SemanticIntentKind intentKind = contourKind switch
        {
            ExecutionContourKind.VmxProjectionOnly => SemanticIntentKind.VmxCompatibilityProjection,
            ExecutionContourKind.SecureComputePolicyAdmissionOnly => SemanticIntentKind.SecureComputeAdmission,
            _ => SemanticIntentKind.RuntimeAssist
        };

        CompilerProductionLoweringGateResult result =
            CompilerProductionLoweringGateEvaluator.Evaluate(
                CreateContext(contourKind),
                CreateCompleteRequest(contourKind, intentKind));

        Assert.False(result.IsSatisfied);
        Assert.Contains(CompilerProductionLoweringGateIds.Contour(contourKind), result.MissingGates);
        Assert.Equal(
            CompilerProductionLoweringStatus.FutureGated,
            result.ProductionLoweringStatus);
    }

    private static CompilerProductionLoweringContext CreateContext(
        ExecutionContourKind contourKind,
        bool allowsBackendEmission = true)
    {
        return new(
            new CompilerTargetProfile(
                "phase03-explicit-gate-test",
                AllowsCarrierEmission: true,
                AllowsBackendEmission: allowsBackendEmission),
            "CompilerPhase03ExplicitProductionGateTests.synthetic-provider",
            new CompilerProductionLoweringProfile(
                "phase03-explicit-gate-test-profile",
                Mode: CompilerProductionLoweringProfileMode.ExplicitlyEnabled,
                new HashSet<ExecutionContourKind> { contourKind },
                CompilerProductionLoweringGateIds.AllFor(contourKind)));
    }

    private static CompilerProductionLoweringGateRequest CreateCompleteRequest(
        ExecutionContourKind contourKind,
        SemanticIntentKind intentKind) =>
        new()
        {
            IntentKind = intentKind,
            ContourKind = contourKind,
            ClassifiedContourKind = contourKind,
            SourceKind = CompilerProductionLoweringSourceKind.ExplicitProvider,
            IntentClassifierComplete = true,
            PresentArtifacts = CompleteArtifacts(contourKind),
            DeclaredRuntimeDependencies = RuntimeAuthorityPending,
            NoFallbackProofPresent = true,
            IseDecodeParityPresent = true,
            TelemetryComplete = true,
            EvidenceComplete = true
        };

    private static HashSet<CompilerProductionArtifactEnvelopeKind> CompleteArtifacts(
        ExecutionContourKind contourKind)
    {
        HashSet<CompilerProductionArtifactEnvelopeKind> artifacts = new HashSet<CompilerProductionArtifactEnvelopeKind>
        {
            CompilerProductionArtifactEnvelopeKind.Carrier,
            CompilerProductionArtifactEnvelopeKind.TypedSlotFacts,
            CompilerProductionArtifactEnvelopeKind.Evidence,
            CompilerProductionArtifactEnvelopeKind.RuntimeBridge
        };

        if (contourKind is
            ExecutionContourKind.StreamEngineVector or
            ExecutionContourKind.MatrixTileHelperOnly or
            ExecutionContourKind.DmaStreamComputeLane6 or
            ExecutionContourKind.L7SdcLane7)
        {
            artifacts.Add(CompilerProductionArtifactEnvelopeKind.Sideband);
        }

        if (contourKind is
            ExecutionContourKind.DmaStreamComputeLane6 or
            ExecutionContourKind.L7SdcLane7)
        {
            artifacts.Add(CompilerProductionArtifactEnvelopeKind.Descriptor);
        }

        return artifacts;
    }

    private const CompilerRuntimeAuthorityDependency RuntimeAuthorityPending =
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired |
        CompilerRuntimeAuthorityDependency.RuntimeCommitRequired |
        CompilerRuntimeAuthorityDependency.RuntimeRetireRequired |
        CompilerRuntimeAuthorityDependency.RuntimePublicationRequired;
}
