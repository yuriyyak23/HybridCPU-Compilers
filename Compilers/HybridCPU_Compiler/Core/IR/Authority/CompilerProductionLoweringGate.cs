using System;
using System.Collections.Generic;
using System.Linq;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.IR.Lowering;

namespace HybridCPU.Compiler.Core.IR.Authority;

/// <summary>
/// Stable identifiers for compiler-side production-lowering preconditions.
/// Satisfying these identifiers never grants runtime permission to execute.
/// </summary>
public static class CompilerProductionLoweringGateIds
{
    public const string Profile = "profile.production-lowering-enabled";
    public const string Intent = "intent.classifier-complete";
    public const string Artifact = "artifact.envelopes-complete";
    public const string RuntimeDependency = "runtime.legality-and-lifecycle-declared";
    public const string NoFallback = "fallback.no-cross-contour-proof";
    public const string Parity = "parity.golden-and-ise";
    public const string TelemetryEvidence = "telemetry.evidence-complete";
    public const string ExplicitProvider = "source.explicit-production-provider";
    public const string MemoryFaultRuntimeDependency = "runtime.memory-order-fault-dependency-declared";
    public const string StreamEngineVectorDirectTransferProduction = "StreamEngineVector.DirectTransferProduction";

    public static string Contour(ExecutionContourKind contourKind) =>
        $"contour.exact:{contourKind}";

    public static IReadOnlySet<string> AllFor(ExecutionContourKind contourKind) =>
        new HashSet<string>(StringComparer.Ordinal)
        {
            Profile,
            Contour(contourKind),
            Intent,
            Artifact,
            RuntimeDependency,
            NoFallback,
            Parity,
            TelemetryEvidence,
            ExplicitProvider,
            MemoryFaultRuntimeDependency,
            StreamEngineVectorDirectTransferProduction
        };
}

public enum CompilerProductionLoweringProfileMode
{
    CompatibilityOnly = 0,
    ExplicitlyEnabled
}

/// <summary>
/// Explicit compiler profile for a future production provider. This is a
/// compiler precondition model, not a runtime legality or execution switch.
/// </summary>
public sealed record CompilerProductionLoweringProfile(
    string Name,
    CompilerProductionLoweringProfileMode Mode,
    IReadOnlySet<ExecutionContourKind> EnabledContours,
    IReadOnlySet<string> EnabledGateIds);

/// <summary>
/// Explicit build/test readiness observations consumed by a production provider.
/// These are preconditions and evidence declarations, never runtime authority.
/// </summary>
public sealed record CompilerProductionLoweringReadiness(
    bool GoldenArtifactCoverage,
    bool IseDecodeParityPresent,
    bool TelemetryComplete,
    bool EvidenceComplete)
{
    public bool MemoryOrderingAndFaultRuntimeRequired { get; init; }

    public static CompilerProductionLoweringReadiness Missing { get; } = new(
        GoldenArtifactCoverage: false,
        IseDecodeParityPresent: false,
        TelemetryComplete: false,
        EvidenceComplete: false);

    public static CompilerProductionLoweringReadiness Complete { get; } = new(
        GoldenArtifactCoverage: true,
        IseDecodeParityPresent: true,
        TelemetryComplete: true,
        EvidenceComplete: true);

    public static CompilerProductionLoweringReadiness CompleteLoadStore { get; } =
        Complete with { MemoryOrderingAndFaultRuntimeRequired = true };
}

public sealed record CompilerProductionLoweringContext(
    CompilerTargetProfile TargetProfile,
    string ProducerSurface,
    CompilerProductionLoweringProfile ProductionProfile)
{
    /// <summary>
    /// Candidate carrier package supplied for a shadow comparison. The
    /// production provider does not obtain or execute runtime state from it.
    /// </summary>
    public CompilerEmissionPackage? CandidatePackage { get; init; }

    public CompilerProductionLoweringReadiness Readiness { get; init; } =
        CompilerProductionLoweringReadiness.Missing;
}

public enum CompilerProductionLoweringSourceKind
{
    ExplicitProvider = 0,
    CompatibilityAdapter,
    DescriptorEvidence,
    ParserValidation,
    HelperAbi,
    RuntimeGuardObservation
}

public enum CompilerProductionArtifactEnvelopeKind
{
    Carrier = 0,
    Sideband,
    Descriptor,
    TypedSlotFacts,
    Evidence,
    RuntimeBridge
}

/// <summary>
/// Facts a future explicit provider must present to the gate evaluator.
/// These facts describe package construction and declared dependencies only.
/// </summary>
public sealed record CompilerProductionLoweringGateRequest
{
    public required SemanticIntentKind IntentKind { get; init; }

    public required ExecutionContourKind ContourKind { get; init; }

    public required ExecutionContourKind ClassifiedContourKind { get; init; }

    public required CompilerProductionLoweringSourceKind SourceKind { get; init; }

    public bool IntentClassifierComplete { get; init; }

    public IReadOnlySet<CompilerProductionArtifactEnvelopeKind> PresentArtifacts { get; init; } =
        new HashSet<CompilerProductionArtifactEnvelopeKind>();

    public CompilerRuntimeAuthorityDependency DeclaredRuntimeDependencies { get; init; }

    public bool NoFallbackProofPresent { get; init; }

    public bool IseDecodeParityPresent { get; init; }

    public bool TelemetryComplete { get; init; }

    public bool EvidenceComplete { get; init; }

    public bool MemoryOrderingAndFaultRuntimeRequired { get; init; }

    public bool DirectTransferScopeComplete { get; init; }
}

/// <summary>
/// Result of checking compiler-side production preconditions. A satisfied
/// result still carries all runtime authority dependencies and is not a
/// runtime permission, execution result, publication, commit, or retire.
/// </summary>
public sealed record CompilerProductionLoweringGateResult(
    ExecutionContourKind ContourKind,
    bool IsSatisfied,
    IReadOnlyList<string> SatisfiedGates,
    IReadOnlyList<string> MissingGates,
    CompilerRuntimeAuthorityDependency RuntimeAuthorityStillRequired,
    string Reason)
{
    public CompilerProductionLoweringStatus ProductionLoweringStatus =>
        IsSatisfied
            ? CompilerProductionLoweringStatus.ProductionCarrierPackageRuntimeAuthorityPending
            : CompilerProductionLoweringStatus.FutureGated;
}

/// <summary>
/// Pure compiler-side evaluator for the Phase 03 gate contract. It is not
/// consumed by a provider in this phase and deliberately has no runtime
/// runtime legality verdict return type.
/// </summary>
public static class CompilerProductionLoweringGateEvaluator
{
    private const CompilerRuntimeAuthorityDependency RuntimeAuthorityPending =
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired |
        CompilerRuntimeAuthorityDependency.RuntimeCommitRequired |
        CompilerRuntimeAuthorityDependency.RuntimeRetireRequired |
        CompilerRuntimeAuthorityDependency.RuntimePublicationRequired;

    public static CompilerProductionLoweringGateResult Evaluate(
        CompilerProductionLoweringContext context,
        CompilerProductionLoweringGateRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context.TargetProfile);
        ArgumentNullException.ThrowIfNull(context.ProductionProfile);
        ArgumentNullException.ThrowIfNull(context.ProductionProfile.EnabledContours);
        ArgumentNullException.ThrowIfNull(context.ProductionProfile.EnabledGateIds);
        ArgumentNullException.ThrowIfNull(request.PresentArtifacts);

        List<string> satisfied = new List<string>();
        List<string> missing = new List<string>();

        void Check(string gateId, bool condition)
        {
            if (condition && context.ProductionProfile.EnabledGateIds.Contains(gateId))
            {
                satisfied.Add(gateId);
            }
            else
            {
                missing.Add(gateId);
            }
        }

        bool profileEnabled =
            context.ProductionProfile.Mode == CompilerProductionLoweringProfileMode.ExplicitlyEnabled &&
            context.TargetProfile.AllowsBackendEmission;
        Check(CompilerProductionLoweringGateIds.Profile, profileEnabled);

        bool exactContour =
            request.ContourKind == request.ClassifiedContourKind &&
            context.ProductionProfile.EnabledContours.Contains(request.ContourKind) &&
            request.ContourKind is not
                (ExecutionContourKind.None or
                 ExecutionContourKind.UnknownRejected or
                 ExecutionContourKind.FutureGated or
                 ExecutionContourKind.VmxProjectionOnly or
                 ExecutionContourKind.SecureComputePolicyAdmissionOnly or
                 ExecutionContourKind.NoEmission);
        Check(CompilerProductionLoweringGateIds.Contour(request.ContourKind), exactContour);

        Check(
            CompilerProductionLoweringGateIds.ExplicitProvider,
            request.SourceKind == CompilerProductionLoweringSourceKind.ExplicitProvider);

        Check(
            CompilerProductionLoweringGateIds.Intent,
            request.IntentClassifierComplete && request.IntentKind != SemanticIntentKind.NonExecutable);

        Check(
            CompilerProductionLoweringGateIds.Artifact,
            RequiredArtifacts(request.ContourKind).IsSubsetOf(request.PresentArtifacts));

        Check(
            CompilerProductionLoweringGateIds.RuntimeDependency,
            HasAllRuntimeDependencies(request.DeclaredRuntimeDependencies));

        Check(CompilerProductionLoweringGateIds.NoFallback, request.NoFallbackProofPresent);
        Check(CompilerProductionLoweringGateIds.Parity, request.IseDecodeParityPresent);
        Check(
            CompilerProductionLoweringGateIds.TelemetryEvidence,
            request.TelemetryComplete && request.EvidenceComplete);
        if (request.ContourKind == ExecutionContourKind.NativeVliwLoadStore)
        {
            Check(
                CompilerProductionLoweringGateIds.MemoryFaultRuntimeDependency,
                request.MemoryOrderingAndFaultRuntimeRequired);
        }

        if (request.ContourKind == ExecutionContourKind.StreamEngineVector)
        {
            Check(
                CompilerProductionLoweringGateIds.StreamEngineVectorDirectTransferProduction,
                request.DirectTransferScopeComplete);
        }

        bool satisfiedAll = missing.Count == 0;
        string reason = satisfiedAll
            ? "Explicit compiler production preconditions are satisfied; runtime authority remains pending for Legality A/B, execution, publication, commit, and retire."
            : $"Production lowering remains future-gated; missing explicit preconditions: {string.Join(", ", missing)}.";
        if (request.ContourKind != request.ClassifiedContourKind)
        {
            reason += " Cross-contour fallback is forbidden.";
        }

        return new(
            request.ContourKind,
            satisfiedAll,
            satisfied,
            missing,
            RuntimeAuthorityPending,
            reason);
    }

    private static IReadOnlySet<CompilerProductionArtifactEnvelopeKind> RequiredArtifacts(
        ExecutionContourKind contourKind)
    {
        HashSet<CompilerProductionArtifactEnvelopeKind> required = new HashSet<CompilerProductionArtifactEnvelopeKind>
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
            required.Add(CompilerProductionArtifactEnvelopeKind.Sideband);
        }

        if (contourKind is
            ExecutionContourKind.DmaStreamComputeLane6 or
            ExecutionContourKind.L7SdcLane7)
        {
            required.Add(CompilerProductionArtifactEnvelopeKind.Descriptor);
        }

        return required;
    }

    private static bool HasAllRuntimeDependencies(CompilerRuntimeAuthorityDependency dependencies) =>
        (dependencies & RuntimeAuthorityPending) == RuntimeAuthorityPending;
}
