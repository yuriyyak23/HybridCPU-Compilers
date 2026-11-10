using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.IR.Lowering;
using HybridCPU.Compiler.Core.IR.Lowering.Production;
using Xunit;

namespace HybridCPU_ISE.Tests.CompilerTests;

/// <summary>
/// Phase 04 contract split tests. The registry exposes separate shell and
/// production-provider paths; only contours with an explicit completed phase
/// are registered, while later contours remain unregistered and fail closed.
/// </summary>
public sealed class CompilerPhase04ProviderContractSplitTests
{
    [Theory]
    [InlineData(ExecutionContourKind.NativeVliwScalar)]
    [InlineData(ExecutionContourKind.DmaStreamComputeLane6)]
    [InlineData(ExecutionContourKind.L7SdcLane7)]
    public void ShellResolutionRemainsCompatibilityPath(
        ExecutionContourKind contourKind)
    {
        IContourLoweringProvider legacyResolution =
            DefaultContourLoweringProviderRegistry.Instance.ResolveProvider(contourKind);
        IContourLoweringProvider shellResolution =
            DefaultContourLoweringProviderRegistry.Instance.ResolveShellProvider(contourKind);

        Assert.Same(legacyResolution, shellResolution);
        Assert.Equal(contourKind, shellResolution.ContourKind);
    }

    [Theory]
    [InlineData(CompilerProductionLoweringProfileMode.CompatibilityOnly)]
    [InlineData(CompilerProductionLoweringProfileMode.ExplicitlyEnabled)]
    public void ProductionResolutionDoesNotReturnUnregisteredProvider(
        CompilerProductionLoweringProfileMode mode)
    {
        const ExecutionContourKind contourKind = ExecutionContourKind.MatrixTileHelperOnly;
        CompilerProductionLoweringContext context = CreateContext(contourKind, mode);

        IContourProductionLoweringProvider? provider =
            DefaultContourLoweringProviderRegistry.Instance.ResolveProductionProvider(
                contourKind,
                context);

        Assert.Null(provider);
    }

    [Fact]
    public void UnknownContourRemainsRejectedAndCannotResolveProductionPath()
    {
        const ExecutionContourKind contourKind = ExecutionContourKind.UnknownRejected;
        CompilerProductionLoweringContext context = CreateContext(
            contourKind,
            CompilerProductionLoweringProfileMode.ExplicitlyEnabled);

        IContourAnalyzer analyzer =
            DefaultContourLoweringProviderRegistry.Instance.ResolveAnalyzer(contourKind);
        IContourLoweringProvider shell =
            DefaultContourLoweringProviderRegistry.Instance.ResolveShellProvider(contourKind);
        IContourProductionLoweringProvider? production =
            DefaultContourLoweringProviderRegistry.Instance.ResolveProductionProvider(
                contourKind,
                context);

        Assert.Equal(ExecutionContourKind.UnknownRejected, analyzer.ContourKind);
        Assert.Equal(ExecutionContourKind.UnknownRejected, shell.ContourKind);
        Assert.Null(production);
    }

    [Fact]
    public void ProductionResultFactoriesKeepMissingGatesAndRuntimeDependenciesExplicit()
    {
        const ExecutionContourKind contourKind = ExecutionContourKind.L7SdcLane7;
        CompilerProductionLoweringGateResult gate =
            CompilerProductionLoweringGateEvaluator.Evaluate(
                CreateContext(contourKind, CompilerProductionLoweringProfileMode.CompatibilityOnly),
                new CompilerProductionLoweringGateRequest
                {
                    IntentKind = SemanticIntentKind.ExternalAcceleratorCommand,
                    ContourKind = contourKind,
                    ClassifiedContourKind = contourKind,
                    SourceKind = CompilerProductionLoweringSourceKind.CompatibilityAdapter
                });

        CompilerProductionLoweringResult result =
            CompilerProductionLoweringResult.FutureGated(
                gate,
                "Phase 04 contract probe remains future-gated.");

        Assert.Equal(CompilerProductionLoweringResultKind.FutureGated, result.ResultKind);
        Assert.Null(result.Package);
        Assert.NotEmpty(result.GateResult.MissingGates);
        Assert.Equal(RuntimeAuthorityPending, result.RuntimeAuthorityStillRequired);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Fact]
    public void FutureProviderPreflightRejectsCrossContourAnalysisBeforePackageConstruction()
    {
        CompilerSemanticIntent intent = CreateIntent();
        CompilerLoweringContext shellContext = new(
            new CompilerTargetProfile("phase04-shell"),
            "CompilerPhase04ProviderContractSplitTests");
        ContourAnalysisReport analysis =
            DefaultContourLoweringProviderRegistry.Instance
                .ResolveAnalyzer(ExecutionContourKind.DmaStreamComputeLane6)
                .Analyze(intent, shellContext);
        CompilerProductionLoweringGateResult gate =
            CompilerProductionLoweringGateEvaluator.Evaluate(
                CreateContext(
                    ExecutionContourKind.L7SdcLane7,
                    CompilerProductionLoweringProfileMode.CompatibilityOnly),
                new CompilerProductionLoweringGateRequest
                {
                    IntentKind = intent.Kind,
                    ContourKind = ExecutionContourKind.L7SdcLane7,
                    ClassifiedContourKind = ExecutionContourKind.L7SdcLane7,
                    SourceKind = CompilerProductionLoweringSourceKind.CompatibilityAdapter
                });

        CompilerProductionLoweringResult? rejection =
            CompilerProductionLoweringProviderContract.PreflightBeforeProduce(
                ExecutionContourKind.L7SdcLane7,
                intent,
                analysis,
                CreateContext(
                    ExecutionContourKind.L7SdcLane7,
                    CompilerProductionLoweringProfileMode.ExplicitlyEnabled),
                gate);

        Assert.NotNull(rejection);
        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, rejection!.ResultKind);
        Assert.Null(rejection.Package);
        Assert.Contains("cross-contour", rejection.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static CompilerProductionLoweringContext CreateContext(
        ExecutionContourKind contourKind,
        CompilerProductionLoweringProfileMode mode) =>
        new(
            new CompilerTargetProfile(
                "phase04-provider-contract-test",
                AllowsCarrierEmission: true,
                AllowsBackendEmission: true),
            "CompilerPhase04ProviderContractSplitTests",
            new CompilerProductionLoweringProfile(
                "phase04-provider-contract-test-profile",
                mode,
                new HashSet<ExecutionContourKind> { contourKind },
                CompilerProductionLoweringGateIds.AllFor(contourKind)));

    private static CompilerSemanticIntent CreateIntent() =>
        new(
            SemanticIntentKind.DmaStreamCompute,
            "DmaStreamCompute",
            RequiresDescriptor: true,
            RequiresSideband: true,
            RequiresToken: true,
            RequiresRuntimeLegality: true,
            IsCompatibilityProjection: false,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: false,
            IsParserOnly: false,
            "Phase 04 contract guard probe.");

    private const CompilerRuntimeAuthorityDependency RuntimeAuthorityPending =
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired |
        CompilerRuntimeAuthorityDependency.RuntimeCommitRequired |
        CompilerRuntimeAuthorityDependency.RuntimeRetireRequired |
        CompilerRuntimeAuthorityDependency.RuntimePublicationRequired;
}
