using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Result of late deterministic lane binding for one bundle.
/// </summary>
public sealed record IrLateLaneBindingResult(
    bool BindingSuccess,
    IReadOnlyList<int> AssignedLanes,
    IReadOnlyList<IrSlotBindingKind> BindingKinds,
    byte OccupiedLaneMask,
    string? FailureReason);
