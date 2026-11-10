using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Intent;

namespace HybridCPU.Compiler.Core.IR.Lowering.Production;

public enum CompilerProductionLoweringResultKind
{
    Rejected = 0,
    FutureGated,
    RuntimeAuthorityPending
}

/// <summary>
/// Result envelope for a future production provider. A package, when one is
/// eventually present, remains separated from runtime authority and carries
/// the complete runtime dependency map.
/// </summary>
public sealed record CompilerProductionLoweringResult(
    CompilerProductionLoweringResultKind ResultKind,
    CompilerProductionLoweringGateResult GateResult,
    CompilerEmissionPackage? Package,
    CompilerRuntimeAuthorityDependency RuntimeAuthorityStillRequired,
    NoFallbackProof NoFallbackProof,
    IReadOnlyList<string> TelemetryEvidence,
    string Reason)
{
    /// <summary>Normalized references to existing compiler facts; never an authority grant.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CompilerProductionEvidenceSummaryV1 EvidenceSummary => CompilerProductionEvidenceSummaryV1.From(this);

    private const CompilerRuntimeAuthorityDependency RuntimeAuthorityPending =
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired |
        CompilerRuntimeAuthorityDependency.RuntimeCommitRequired |
        CompilerRuntimeAuthorityDependency.RuntimeRetireRequired |
        CompilerRuntimeAuthorityDependency.RuntimePublicationRequired;

    public static CompilerProductionLoweringResult Rejected(
        CompilerProductionLoweringGateResult gateResult,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(gateResult);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new(
            CompilerProductionLoweringResultKind.Rejected,
            gateResult,
            Package: null,
            RuntimeAuthorityPending,
            NoFallbackProof.Forbidden(
                $"production-rejected:{gateResult.ContourKind}",
                "Rejected production lowering cannot route fallback."),
            Array.Empty<string>(),
            reason);
    }

    public static CompilerProductionLoweringResult FutureGated(
        CompilerProductionLoweringGateResult gateResult,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(gateResult);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new(
            CompilerProductionLoweringResultKind.FutureGated,
            gateResult,
            Package: null,
            RuntimeAuthorityPending,
            NoFallbackProof.Forbidden(
                $"production-future-gated:{gateResult.ContourKind}",
                "Future-gated production lowering cannot route fallback."),
            Array.Empty<string>(),
            reason);
    }

    public static CompilerProductionLoweringResult RuntimePending(
        CompilerProductionLoweringGateResult gateResult,
        CompilerEmissionPackage package,
        NoFallbackProof noFallbackProof,
        IReadOnlyList<string> telemetryEvidence,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(gateResult);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(noFallbackProof);
        ArgumentNullException.ThrowIfNull(telemetryEvidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!gateResult.IsSatisfied)
        {
            throw new InvalidOperationException(
                "A runtime-pending production package requires every explicit compiler gate.");
        }

        if (gateResult.RuntimeAuthorityStillRequired != RuntimeAuthorityPending)
        {
            throw new InvalidOperationException(
                "A production package cannot weaken the runtime authority dependency map.");
        }

        if (!package.SeparationProof.CarrierSeparatedFromSideband ||
            !package.SeparationProof.DescriptorSeparatedFromAuthority ||
            !package.SeparationProof.TypedSlotFactsSeparatedFromLegality ||
            !package.SeparationProof.EvidenceSeparatedFromProductionLowering ||
            !package.SeparationProof.BridgeSeparatedFromExecution)
        {
            throw new InvalidOperationException(
                "A production package must preserve separated artifact envelopes.");
        }

        RuntimeBridgeEnvelope? runtimeBridge = package.RuntimeBridgeInput;
        if (runtimeBridge is null ||
            !runtimeBridge.RuntimeLegalityAStillRequired ||
            !runtimeBridge.RuntimeLegalityBStillRequired ||
            !runtimeBridge.RuntimeCommitStillRequired ||
            !runtimeBridge.RuntimeRetireStillRequired ||
            !runtimeBridge.RuntimePublicationStillRequired)
        {
            throw new InvalidOperationException(
                "A production package must retain the complete runtime lifecycle dependency map.");
        }

        return new(
            CompilerProductionLoweringResultKind.RuntimeAuthorityPending,
            gateResult,
            package,
            RuntimeAuthorityPending,
            noFallbackProof,
            [.. telemetryEvidence],
            reason);
    }
}

/// <summary>
/// Shared fail-closed preflight for future production-provider implementations.
/// A provider must run this before constructing any package; a null result
/// means only that this contract found no preflight rejection.
/// </summary>
public static class CompilerProductionLoweringProviderContract
{
    public static CompilerProductionLoweringResult? PreflightBeforeProduce(
        ExecutionContourKind providerContour,
        CompilerSemanticIntent intent,
        ContourAnalysisReport analysis,
        CompilerProductionLoweringContext context,
        CompilerProductionLoweringGateResult gateResult)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gateResult);

        if (analysis.ContourKind != providerContour ||
            gateResult.ContourKind != providerContour ||
            analysis.Intent.Kind != intent.Kind)
        {
            return CompilerProductionLoweringResult.Rejected(
                gateResult,
                "Production provider preflight rejected a contour or intent mismatch; cross-contour fallback is forbidden.");
        }

        if (!gateResult.IsSatisfied ||
            analysis.CapabilityObservation.MissingGates.Count != 0)
        {
            return CompilerProductionLoweringResult.FutureGated(
                gateResult,
                "Production provider preflight rejected missing explicit compiler gates.");
        }

        bool sidebandPresent =
            analysis.RequiredSidebandPresent || context.CandidatePackage?.Sideband is not null;
        bool descriptorPresent =
            analysis.RequiredDescriptorPresent || context.CandidatePackage?.Descriptor is not null;
        if ((intent.RequiresSideband && !sidebandPresent) ||
            (intent.RequiresDescriptor && !descriptorPresent))
        {
            return CompilerProductionLoweringResult.FutureGated(
                gateResult,
                "Production provider preflight rejected missing contour artifact requirements.");
        }

        if (!analysis.RuntimeLegalityRequired ||
            !context.TargetProfile.AllowsBackendEmission)
        {
            return CompilerProductionLoweringResult.Rejected(
                gateResult,
                "Production provider preflight rejected a weakened runtime authority dependency map.");
        }

        return null;
    }
}
