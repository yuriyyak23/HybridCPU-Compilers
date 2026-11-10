namespace HybridCPU.Compiler.Core.IR.Telemetry;

/// <summary>Existing advisory ranking modes; structural admission always runs before ranking.</summary>
public enum HybridCpuProfileSelectionPolicyV1
{
    ClassPressureSmoothing = 0,
    ClassPressurePriority = 1
}

/// <summary>No profile discovery: callers explicitly opt in and supply a versioned JSON snapshot.</summary>
public sealed record HybridCpuProfileCompilationRequestV1(bool Enabled = false, string? ProfileJson = null)
{
    public bool CaptureDecisionTrace { get; init; }
    public HybridCpuProfileSelectionPolicyV1 SelectionPolicy { get; init; } = HybridCpuProfileSelectionPolicyV1.ClassPressureSmoothing;
}

/// <summary>Scalar policy/provenance sidecar for this opt-in lane; no runtime/publication authority.</summary>
public sealed record HybridCpuProfileCompilationResultV1(
    HybridCpuCompiledProgram CompiledProgram,
    string ProfileInputIdentity,
    string ProfileStatus,
    string? ActiveProfileDigest,
    string EffectiveOptionsDigest,
    string ScheduleFingerprint)
{
    public HybridCpuProfileDecisionTraceV1? DecisionTrace { get; init; }
}

public sealed record HybridCpuProfileCandidateEventV1(
    int Decision, string Kind, int InstructionIndex, int CyclePrefixLength,
    bool StructurallyAdmissible, int? ClassPressureCostBucket);

/// <summary>Observational, bounded local-list events. Truncation never changes search or fallback.</summary>
public sealed record HybridCpuProfileDecisionTraceV1(
    string Schema, bool Truncated, IReadOnlyList<HybridCpuProfileCandidateEventV1> Events);

internal sealed class HybridCpuProfileDecisionTraceCollectorV1
{
    private const int MaximumEvents = 1024;
    private readonly List<HybridCpuProfileCandidateEventV1> _events = new();
    private int _decision;
    private bool _truncated;
    public int BeginDecision() => _decision++;
    public void Record(int decision, string kind, int instruction, int prefix, bool admissible, int? cost)
    {
        if (_events.Count == MaximumEvents) { _truncated = true; return; }
        _events.Add(new(decision, kind, instruction, prefix, admissible, cost));
    }
    public HybridCpuProfileDecisionTraceV1 Complete() => new(
        "hybridcpu.profile-decision-trace/v1", _truncated, Array.AsReadOnly(_events.ToArray()));
}
