using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Basic block in the normalized compiler IR.
    /// </summary>
    public sealed record IrBasicBlock(
        int Id,
        int StartInstructionIndex,
        int EndInstructionIndex,
        ulong StartAddress,
        ulong EndAddress,
        bool HasUnresolvedControlTransfer,
        IReadOnlyList<IrInstruction> Instructions,
        IReadOnlyList<int> PredecessorBlockIds,
        IReadOnlyList<int> SuccessorBlockIds,
        bool ExitBlock,
        bool BarrierBoundary,
        string? PrimaryLabel,
        IReadOnlyList<string> LabelNames,
        string? SectionName,
    string? FunctionName,
    IrSourceSpan? SourceSpan = null);
}
