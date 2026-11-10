using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Compiler.Core.IR.Resources;

public enum CompilerResourceShadowDecisionV1 : byte
{
    Allowed = 0,
    Rejected = 1,
    UnknownFallback = 2
}

public enum CompilerResourceMismatchKindV1 : byte
{
    None = 0,
    LegacyAllowedModelRejected = 1,
    LegacyRejectedModelAllowed = 2,
    UnsupportedFallback = 3,
    ShadowEvaluationFailure = 4
}

/// <summary>
/// Deterministic Phase 01 shadow record. Both decisions are compiler structural evidence only.
/// </summary>
public sealed record CompilerResourceDecisionV1(
    string Schema,
    string MachineDescriptionKey,
    IReadOnlyList<int> InstructionIndexes,
    bool LegacyResourceDecision,
    CompilerResourceShadowDecisionV1 ModelDecision,
    CompilerResourceMismatchKindV1 MismatchKind,
    HybridCpuResourceReasonCodeV1 ReasonCode,
    string StateDigest,
    IReadOnlyList<string> FootprintFingerprints)
{
    public const string SchemaName = "CompilerResourceDecisionV1";
    public bool IsDisagreement => MismatchKind is
        CompilerResourceMismatchKindV1.LegacyAllowedModelRejected or
        CompilerResourceMismatchKindV1.LegacyRejectedModelAllowed;
}

public static class CompilerResourceShadowEvaluatorV1
{
    private static readonly IrHazardReason[] ResourceReasons =
    [
        IrHazardReason.StructuralResourceConflict,
        IrHazardReason.ExclusiveCycleRequired,
        IrHazardReason.SlotCapacityExceeded,
        IrHazardReason.NoLegalSlotAssignment,
        IrHazardReason.ClassCapacityExceeded,
        IrHazardReason.AliasedLaneConflict
    ];

    public static CompilerResourceDecisionV1 Evaluate(
        IReadOnlyList<IrInstruction> instructions,
        IrCandidateBundleAnalysis legacyAnalysis,
        IHybridCpuMachineResourceModel? model = null)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(legacyAnalysis);
        model ??= HybridCpuMachineResourceModelV1.Default;

        bool legacyAllowed = IsLegacyResourceAllowed(legacyAnalysis);
        HybridCpuCycleResourceState state = HybridCpuCycleResourceState.Empty(model.Description);
        List<IrResourceFootprint> footprints = new List<IrResourceFootprint>(instructions.Count);
        HybridCpuResourceReservationResultV1? lastResult = null;

        foreach (IrInstruction instruction in instructions)
        {
            IrResourceFootprint footprint = model.GetResourceFootprint(instruction);
            footprints.Add(footprint);
            lastResult = model.CanReserve(state, footprint);
            if (lastResult.Decision != HybridCpuResourceReservationDecisionV1.Allowed)
            {
                break;
            }
            state = model.Reserve(state, footprint);
        }

        CompilerResourceShadowDecisionV1 modelDecision = lastResult?.Decision switch
        {
            HybridCpuResourceReservationDecisionV1.Rejected => CompilerResourceShadowDecisionV1.Rejected,
            HybridCpuResourceReservationDecisionV1.RequiresLegacyFallback => CompilerResourceShadowDecisionV1.UnknownFallback,
            _ => CompilerResourceShadowDecisionV1.Allowed
        };
        CompilerResourceMismatchKindV1 mismatch = modelDecision switch
        {
            CompilerResourceShadowDecisionV1.UnknownFallback => CompilerResourceMismatchKindV1.UnsupportedFallback,
            CompilerResourceShadowDecisionV1.Rejected when legacyAllowed =>
                CompilerResourceMismatchKindV1.LegacyAllowedModelRejected,
            CompilerResourceShadowDecisionV1.Allowed when !legacyAllowed =>
                CompilerResourceMismatchKindV1.LegacyRejectedModelAllowed,
            _ => CompilerResourceMismatchKindV1.None
        };

        return new CompilerResourceDecisionV1(
            CompilerResourceDecisionV1.SchemaName,
            model.Description.Key,
            Array.AsReadOnly(instructions.Select(instruction => instruction.Index).ToArray()),
            legacyAllowed,
            modelDecision,
            mismatch,
            lastResult?.ReasonCode ?? HybridCpuResourceReasonCodeV1.None,
            state.StateDigest,
            Array.AsReadOnly(footprints.Select(footprint => footprint.Fingerprint).ToArray()));
    }

    public static CompilerResourceDecisionV1 CreateEvaluationFailure(
        IReadOnlyList<IrInstruction> instructions,
        IrCandidateBundleAnalysis legacyAnalysis,
        IHybridCpuMachineResourceModel? model = null)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(legacyAnalysis);
        model ??= HybridCpuMachineResourceModelV1.Default;
        return new CompilerResourceDecisionV1(
            CompilerResourceDecisionV1.SchemaName,
            model.Description.Key,
            Array.AsReadOnly(instructions.Select(instruction => instruction.Index).ToArray()),
            IsLegacyResourceAllowed(legacyAnalysis),
            CompilerResourceShadowDecisionV1.UnknownFallback,
            CompilerResourceMismatchKindV1.ShadowEvaluationFailure,
            HybridCpuResourceReasonCodeV1.ShadowEvaluationFailure,
            HybridCpuCycleResourceState.Empty(model.Description).StateDigest,
            Array.Empty<string>());
    }

    private static bool IsLegacyResourceAllowed(IrCandidateBundleAnalysis legacyAnalysis) =>
        !legacyAnalysis.Legality.Hazards.Any(hazard =>
            Array.IndexOf(ResourceReasons, hazard.Reason) >= 0);
}

public sealed record CompilerResourceShadowReportV1(
    string Schema,
    string MachineDescriptionKey,
    int DecisionCount,
    int AgreementCount,
    int DisagreementCount,
    int FallbackCount,
    int AttemptedDecisionCount,
    int TruncatedDecisionCount,
    string Fingerprint,
    IReadOnlyList<CompilerResourceDecisionV1> Decisions)
{
    public const string SchemaName = "CompilerResourceShadowReportV1";
}

/// <summary>
/// Bounded in-memory sink used only by the explicit shadow compilation entry point.
/// </summary>
public sealed class CompilerResourceShadowCollectorV1
{
    private readonly object _sync = new();
    private readonly List<CompilerResourceDecisionV1> _decisions = [];
    private readonly int _maximumDecisions;
    private int _attemptedDecisions;

    public CompilerResourceShadowCollectorV1(int maximumDecisions = 64)
    {
        if (maximumDecisions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDecisions));
        }
        _maximumDecisions = maximumDecisions;
    }

    public void Record(CompilerResourceDecisionV1 decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        int attempt = System.Threading.Interlocked.Increment(ref _attemptedDecisions);
        if (attempt > _maximumDecisions)
        {
            return;
        }

        lock (_sync)
        {
            if (_decisions.Count < _maximumDecisions)
            {
                _decisions.Add(decision);
            }
        }
    }

    internal bool TryBeginEvaluation()
    {
        int attempt = System.Threading.Interlocked.Increment(ref _attemptedDecisions);
        return attempt <= _maximumDecisions;
    }

    internal void RecordEvaluated(CompilerResourceDecisionV1 decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        lock (_sync)
        {
            if (_decisions.Count < _maximumDecisions)
            {
                _decisions.Add(decision);
            }
        }
    }

    internal void AbandonEvaluation() { }

    public CompilerResourceShadowReportV1 Complete()
    {
        CompilerResourceDecisionV1[] snapshot;
        int attemptedDecisions;
        lock (_sync)
        {
            snapshot = _decisions.ToArray();
            attemptedDecisions = System.Threading.Volatile.Read(ref _attemptedDecisions);
        }

        int disagreements = snapshot.Count(decision => decision.IsDisagreement);
        int fallbacks = snapshot.Count(decision =>
            decision.ModelDecision == CompilerResourceShadowDecisionV1.UnknownFallback);
        int agreements = snapshot.Length - disagreements - fallbacks;
        int truncatedDecisions = attemptedDecisions - snapshot.Length;
        return new CompilerResourceShadowReportV1(
            CompilerResourceShadowReportV1.SchemaName,
            HybridCpuMachineDescriptionV1.Default.Key,
            snapshot.Length,
            agreements,
            disagreements,
            fallbacks,
            attemptedDecisions,
            truncatedDecisions,
            ComputeFingerprint(snapshot, attemptedDecisions, truncatedDecisions),
            Array.AsReadOnly(snapshot));
    }

    private static string ComputeFingerprint(
        IReadOnlyList<CompilerResourceDecisionV1> decisions,
        int attemptedDecisions,
        int truncatedDecisions)
    {
        StringBuilder builder = new StringBuilder(CompilerResourceShadowReportV1.SchemaName)
            .Append('|').Append(attemptedDecisions.ToString(CultureInfo.InvariantCulture))
            .Append('|').Append(truncatedDecisions.ToString(CultureInfo.InvariantCulture));
        foreach (CompilerResourceDecisionV1 decision in decisions)
        {
            builder.Append('|').Append(decision.LegacyResourceDecision ? '1' : '0')
                .Append(':').Append(((byte)decision.ModelDecision).ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(((byte)decision.MismatchKind).ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(((byte)decision.ReasonCode).ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(decision.StateDigest);
            foreach (int instructionIndex in decision.InstructionIndexes)
            {
                builder.Append(':').Append(instructionIndex.ToString(CultureInfo.InvariantCulture));
            }
            foreach (string footprint in decision.FootprintFingerprints)
            {
                builder.Append(':').Append(footprint);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }
}

public sealed record HybridCpuCompilationResourceShadowResultV1(
    HybridCpuCompiledProgram CompiledProgram,
    CompilerResourceShadowReportV1 ShadowReport);
