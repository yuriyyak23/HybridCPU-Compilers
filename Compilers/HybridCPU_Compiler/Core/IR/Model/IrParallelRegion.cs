using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Classification of parallelizable region types detected in the IR.
/// </summary>
public enum IrParallelKind
{
    /// <summary>Simple parallel for-loop with independent iterations.</summary>
    ForLoop = 0,

    /// <summary>Standalone reduction (accumulate via associative operator).</summary>
    Reduction = 1,

    /// <summary>For-loop with an embedded reduction pattern.</summary>
    ForLoopWithReduction = 2
}

/// <summary>
/// Describes a reduction operation to be distributed across workers.
/// </summary>
/// <param name="ReduceOpcode">Associative opcode used for reduction (e.g., ADD, Min, Max).</param>
/// <param name="IdentityElement">Identity element for the reduction (0 for sum, long.MaxValue for min, long.MinValue for max).</param>
/// <param name="AccumulatorRegister">Register holding the accumulator in the original loop body.</param>
/// <param name="PartialResultBaseAddress">Memory base address where workers store partial results.</param>
public sealed record ReductionPlan(
    HybridCpuOpcode ReduceOpcode,
    long IdentityElement,
    int AccumulatorRegister,
    int PartialResultBaseAddress);

/// <summary>
/// Describes a parallelizable region detected or annotated in the IR.
/// </summary>
/// <param name="StartInstructionIndex">First instruction index of the parallel region.</param>
/// <param name="EndInstructionIndex">Last instruction index (exclusive) of the parallel region.</param>
/// <param name="Kind">Classification of the parallel region.</param>
/// <param name="InductionVariableRegister">Register used as the loop induction variable.</param>
/// <param name="IterationStart">Loop iteration start value.</param>
/// <param name="IterationEnd">Loop iteration end value (exclusive).</param>
/// <param name="IterationStep">Loop iteration step.</param>
/// <param name="SharedReadRegisters">Registers read but not written by the loop body (shared across workers).</param>
/// <param name="SharedWriteRegisters">Registers written by the loop body via indexed access (partitioned across workers).</param>
/// <param name="PrivateRegisters">Registers private to each iteration (worker-local copies).</param>
/// <param name="Reduction">Optional reduction plan when the region contains a reduction pattern.</param>
public sealed record ParallelRegionInfo(
    int StartInstructionIndex,
    int EndInstructionIndex,
    IrParallelKind Kind,
    int InductionVariableRegister,
    long IterationStart,
    long IterationEnd,
    long IterationStep,
    IReadOnlyList<int> SharedReadRegisters,
    IReadOnlyList<int> SharedWriteRegisters,
    IReadOnlyList<int> PrivateRegisters,
    ReductionPlan? Reduction)
{
    /// <summary>
    /// Gets the total number of iterations in this parallel region.
    /// </summary>
    public long IterationCount => IterationStep > 0
        ? (IterationEnd - IterationStart + IterationStep - 1) / IterationStep
        : 0;
}
