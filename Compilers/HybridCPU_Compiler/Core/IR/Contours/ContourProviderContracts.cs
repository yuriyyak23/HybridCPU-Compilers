using System;
using System.Collections.Generic;
using System.Linq;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.IR.Lowering;
using HybridCPU.Compiler.Core.IR.Lowering.Production;

namespace HybridCPU.Compiler.Core.IR.Contours;

public enum CompilerCapabilityObservationState
{
    NoEmission = 0,
    ParserOnly,
    HelperOnly,
    ValidationOnly,
    ScopedRuntimeContour,
    ProductionBlocked,
    ProductionCandidateRequiresRuntimeLegality,
    FutureGated
}

public sealed record CompilerTargetProfile(
    string Name,
    bool AllowsCarrierEmission = true,
    bool AllowsBackendEmission = false);

public sealed record CompilerLoweringContext(
    CompilerTargetProfile TargetProfile,
    string ProducerSurface);

public sealed record CompilerCapabilityObservation(
    ExecutionContourKind ContourKind,
    CompilerCapabilityObservationState State,
    CompilerAuthoritySourceKind AuthoritySourceKind,
    CompilerRuntimeAuthorityDependency RuntimeAuthorityDependency,
    IReadOnlyList<string> RequiredGates,
    IReadOnlyList<string> MissingGates,
    string Reason);

public sealed record ContourAnalysisReport(
    ExecutionContourKind ContourKind,
    CompilerSemanticIntent Intent,
    CompilerCapabilityObservation CapabilityObservation,
    bool ProviderAvailable,
    bool RequiredSidebandPresent,
    bool RequiredDescriptorPresent,
    bool RuntimeLegalityRequired,
    IReadOnlyList<CompilerRejectReason> RejectReasons,
    CompilerEvidenceEnvelope Evidence,
    string Reason);

public interface IContourAnalyzer
{
    ExecutionContourKind ContourKind { get; }

    ContourAnalysisReport Analyze(CompilerSemanticIntent intent, CompilerLoweringContext context);
}

public interface IContourLoweringProvider
{
    ExecutionContourKind ContourKind { get; }

    CompilerCapabilityObservation ObserveCapability(CompilerTargetProfile target);

    CompilerLoweringDecision Lower(
        CompilerSemanticIntent intent,
        ContourAnalysisReport analysis,
        CompilerLoweringContext context);
}

/// <summary>
/// Separate contract for a future contour-specific production provider.
/// Shell providers do not implement this interface and remain fail-closed.
/// </summary>
public interface IContourProductionLoweringProvider
{
    ExecutionContourKind ContourKind { get; }

    CompilerProductionLoweringGateResult EvaluateProductionGates(
        CompilerSemanticIntent intent,
        ContourAnalysisReport analysis,
        CompilerProductionLoweringContext context);

    CompilerProductionLoweringResult TryProduce(
        CompilerSemanticIntent intent,
        ContourAnalysisReport analysis,
        CompilerProductionLoweringContext context);
}

public interface IContourLoweringProviderRegistry
{
    IContourAnalyzer ResolveAnalyzer(ExecutionContourKind contourKind);

    IContourLoweringProvider ResolveProvider(ExecutionContourKind contourKind);

    IContourLoweringProvider ResolveShellProvider(ExecutionContourKind contourKind);

    IContourProductionLoweringProvider? ResolveProductionProvider(
        ExecutionContourKind contourKind,
        CompilerProductionLoweringContext context);

    IReadOnlyList<CompilerCapabilityObservation> ObserveAll(CompilerTargetProfile target);
}

public sealed class DefaultContourLoweringProviderRegistry : IContourLoweringProviderRegistry
{
    public static DefaultContourLoweringProviderRegistry Instance { get; } = new();

    private readonly IReadOnlyDictionary<ExecutionContourKind, IContourAnalyzer> _analyzers;
    private readonly IReadOnlyDictionary<ExecutionContourKind, IContourLoweringProvider> _providers;
    private readonly IReadOnlyDictionary<ExecutionContourKind, IContourProductionLoweringProvider> _productionProviders;
    private readonly RejectedContourAnalyzer _rejectedAnalyzer = new();
    private readonly RejectedContourLoweringProvider _rejectedProvider = new();

    public DefaultContourLoweringProviderRegistry(
        IEnumerable<IContourProductionLoweringProvider>? productionProviders = null)
    {
        IContourLoweringProvider[] providers =
        [
            new ScalarVliwLoweringProvider(),
            new LoadStoreVliwLoweringProvider(),
            new BranchControlVliwLoweringProvider(),
            new StreamVectorLoweringProvider(),
            new MatrixTileLoweringProvider(),
            new DmaStreamComputeLoweringProvider(),
            new L7SdcLoweringProvider(),
            new VmxProjectionLoweringProvider(),
            new SecureComputeAdmissionLoweringProvider()
        ];

        _providers = providers.ToDictionary(static provider => provider.ContourKind);
        _analyzers = providers
            .Select(static provider => new ContourAnalyzerShell(provider.ContourKind, provider))
            .ToDictionary(static analyzer => analyzer.ContourKind, static analyzer => (IContourAnalyzer)analyzer);
        _productionProviders = (productionProviders ?? Array.Empty<IContourProductionLoweringProvider>())
            .ToDictionary(static provider => provider.ContourKind);
    }

    public IContourAnalyzer ResolveAnalyzer(ExecutionContourKind contourKind) =>
        _analyzers.TryGetValue(contourKind, out IContourAnalyzer? analyzer)
            ? analyzer
            : _rejectedAnalyzer;

    public IContourLoweringProvider ResolveProvider(ExecutionContourKind contourKind) =>
        _providers.TryGetValue(contourKind, out IContourLoweringProvider? provider)
            ? provider
            : _rejectedProvider;

    public IContourLoweringProvider ResolveShellProvider(ExecutionContourKind contourKind) =>
        ResolveProvider(contourKind);

    public IContourProductionLoweringProvider? ResolveProductionProvider(
        ExecutionContourKind contourKind,
        CompilerProductionLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Phase 04 defines the separate resolution boundary only. No
        // production provider is registered until a later provider phase.
        if (context.ProductionProfile.Mode != CompilerProductionLoweringProfileMode.ExplicitlyEnabled ||
            !context.TargetProfile.AllowsBackendEmission ||
            !context.ProductionProfile.EnabledContours.Contains(contourKind) ||
            !context.ProductionProfile.EnabledGateIds.Contains(CompilerProductionLoweringGateIds.Profile) ||
            !context.ProductionProfile.EnabledGateIds.Contains(CompilerProductionLoweringGateIds.Contour(contourKind)))
        {
            return null;
        }

        return _productionProviders.TryGetValue(contourKind, out IContourProductionLoweringProvider? provider)
            ? provider
            : null;
    }

    public IReadOnlyList<CompilerCapabilityObservation> ObserveAll(CompilerTargetProfile target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return _providers.Values
            .Select(provider => provider.ObserveCapability(target))
            .ToArray();
    }
}

public sealed class ScalarVliwLoweringProvider()
    : ContourLoweringProviderShell(
        ExecutionContourKind.NativeVliwScalar,
        CompilerCapabilityObservationState.ScopedRuntimeContour,
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired,
        "Native scalar VLIW contour is a scoped runtime contour; runtime legality remains required.")
{
}

public sealed class LoadStoreVliwLoweringProvider()
    : ContourLoweringProviderShell(
        ExecutionContourKind.NativeVliwLoadStore,
        CompilerCapabilityObservationState.ScopedRuntimeContour,
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired,
        "Native load/store VLIW contour cannot claim memory publication.")
{
}

public sealed class BranchControlVliwLoweringProvider()
    : ContourLoweringProviderShell(
        ExecutionContourKind.NativeVliwBranchControl,
        CompilerCapabilityObservationState.ScopedRuntimeContour,
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired,
        "Native branch/control VLIW contour cannot claim commit or retire.")
{
}

public sealed class StreamVectorLoweringProvider()
    : ContourLoweringProviderShell(
        ExecutionContourKind.StreamEngineVector,
        CompilerCapabilityObservationState.HelperOnly,
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired,
        "Stream/vector contour is helper/transport evidence only in this slice; scalar fallback is forbidden.")
{
}

public sealed class MatrixTileLoweringProvider()
    : ContourLoweringProviderShell(
        ExecutionContourKind.MatrixTileHelperOnly,
        CompilerCapabilityObservationState.HelperOnly,
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired,
        "MatrixTile contour is helper ABI only; scalar/vector/Stream fallback is forbidden.")
{
}

public sealed class DmaStreamComputeLoweringProvider()
    : ContourLoweringProviderShell(
        ExecutionContourKind.DmaStreamComputeLane6,
        CompilerCapabilityObservationState.ScopedRuntimeContour,
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired,
        "DmaStreamCompute contour is lane6 only; L7/Stream/scalar fallback is forbidden.")
{
}

public sealed class L7SdcLoweringProvider()
    : ContourLoweringProviderShell(
        ExecutionContourKind.L7SdcLane7,
        CompilerCapabilityObservationState.ScopedRuntimeContour,
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired,
        "L7-SDC contour is lane7 descriptor-sideband only; DSC/Stream/scalar fallback is forbidden.")
{
}

public sealed class VmxProjectionLoweringProvider()
    : ContourLoweringProviderShell(
        ExecutionContourKind.VmxProjectionOnly,
        CompilerCapabilityObservationState.NoEmission,
        CompilerRuntimeAuthorityDependency.NoRuntimeActionBecauseNoEmission,
        "VMX contour is projection/no-emission only; VMCS is not compiler-owned state.")
{
}

public sealed class SecureComputeAdmissionLoweringProvider()
    : ContourLoweringProviderShell(
        ExecutionContourKind.SecureComputePolicyAdmissionOnly,
        CompilerCapabilityObservationState.NoEmission,
        CompilerRuntimeAuthorityDependency.NoRuntimeActionBecauseNoEmission,
        "SecureCompute contour is policy/admission/evidence-only; secure backend execution is forbidden.")
{
}

public abstract class ContourLoweringProviderShell(
    ExecutionContourKind contourKind,
    CompilerCapabilityObservationState observationState,
    CompilerRuntimeAuthorityDependency runtimeDependency,
    string reason) : IContourLoweringProvider
{
    public ExecutionContourKind ContourKind { get; } = contourKind;

    public CompilerCapabilityObservation ObserveCapability(CompilerTargetProfile target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new(
            ContourKind,
            observationState,
            CompilerAuthoritySourceKind.CompilerStructuralModel,
            runtimeDependency,
            Array.Empty<string>(),
            Array.Empty<string>(),
            reason);
    }

    public CompilerLoweringDecision Lower(
        CompilerSemanticIntent intent,
        ContourAnalysisReport analysis,
        CompilerLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(context);

        if (analysis.ContourKind != ContourKind)
        {
            return CompilerLoweringDecision.Reject(
                intent.Kind,
                analysis.ContourKind,
                CompilerRejectReason.CrossContourFallbackForbidden,
                "Provider received analysis for another contour; cross-contour fallback is forbidden.");
        }

        return CompilerLoweringDecision.Reject(
            intent.Kind,
            ContourKind,
            CompilerRejectReason.RuntimeAuthorityOwned,
            "Provider shell does not perform production lowering; runtime authority remains required.");
    }
}

internal sealed class ContourAnalyzerShell(
    ExecutionContourKind contourKind,
    IContourLoweringProvider provider) : IContourAnalyzer
{
    public ExecutionContourKind ContourKind { get; } = contourKind;

    public ContourAnalysisReport Analyze(CompilerSemanticIntent intent, CompilerLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);

        CompilerCapabilityObservation observation = provider.ObserveCapability(context.TargetProfile);
        return new(
            ContourKind,
            intent,
            observation,
            ProviderAvailable: true,
            RequiredSidebandPresent: !intent.RequiresSideband,
            RequiredDescriptorPresent: !intent.RequiresDescriptor,
            RuntimeLegalityRequired: observation.RuntimeAuthorityDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired),
            RejectReasons: Array.Empty<CompilerRejectReason>(),
            CreateEvidence(ContourKind, "Contour analysis report is evidence only and does not lower."),
            "Analyze completed without carrier/sideband/descriptor emission.");
    }

    private static CompilerEvidenceEnvelope CreateEvidence(ExecutionContourKind contourKind, string reason) =>
        new(
            [CompilerArtifactKind.CompilerEvidenceEnvelope],
            CompilerEvidenceClass.StructuralEvidence,
            $"{contourKind}: {reason}",
            new CompilerCoreResultHeader(
                CompilerAuthorityClass.CompilerEvidenceProduction,
                CompilerAuthoritySourceKind.CompilerStructuralModel,
                CompilerEvidenceClass.StructuralEvidence,
                CompilerPublicationClass.EvidenceOnly,
                CompilerExecutionClaim.NoExecutionClaim,
                CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
                CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
                CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired));
}

internal sealed class RejectedContourAnalyzer : IContourAnalyzer
{
    public ExecutionContourKind ContourKind => ExecutionContourKind.UnknownRejected;

    public ContourAnalysisReport Analyze(CompilerSemanticIntent intent, CompilerLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);

        CompilerCapabilityObservation observation = new(
            ExecutionContourKind.UnknownRejected,
            CompilerCapabilityObservationState.NoEmission,
            CompilerAuthoritySourceKind.CompilerStructuralModel,
            CompilerRuntimeAuthorityDependency.NoRuntimeActionBecauseNoEmission,
            Array.Empty<string>(),
            ["known contour"],
            "Unknown contour fails closed; registry is not a fallback router.");

        return new(
            ExecutionContourKind.UnknownRejected,
            intent,
            observation,
            ProviderAvailable: false,
            RequiredSidebandPresent: false,
            RequiredDescriptorPresent: false,
            RuntimeLegalityRequired: false,
            [CompilerRejectReason.UnknownContour],
            new CompilerEvidenceEnvelope(
                [CompilerArtifactKind.CompilerEvidenceEnvelope],
                CompilerEvidenceClass.NegativeGateEvidence,
                "Unknown contour rejected without scalar fallback.",
                CompilerCoreResultHeader.NoEmissionParserOnly(CompilerEvidenceClass.NegativeGateEvidence)),
            "Unknown contour rejected; no provider fallback attempted.");
    }
}

internal sealed class RejectedContourLoweringProvider : IContourLoweringProvider
{
    public ExecutionContourKind ContourKind => ExecutionContourKind.UnknownRejected;

    public CompilerCapabilityObservation ObserveCapability(CompilerTargetProfile target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new(
            ExecutionContourKind.UnknownRejected,
            CompilerCapabilityObservationState.NoEmission,
            CompilerAuthoritySourceKind.CompilerStructuralModel,
            CompilerRuntimeAuthorityDependency.NoRuntimeActionBecauseNoEmission,
            Array.Empty<string>(),
            ["known contour"],
            "Unknown contour has no provider and cannot fall back.");
    }

    public CompilerLoweringDecision Lower(
        CompilerSemanticIntent intent,
        ContourAnalysisReport analysis,
        CompilerLoweringContext context) =>
        CompilerLoweringDecision.Reject(
            intent.Kind,
            ExecutionContourKind.UnknownRejected,
            CompilerRejectReason.UnknownContour,
            "Unknown contour rejected by registry; scalar fallback is forbidden.");
}
