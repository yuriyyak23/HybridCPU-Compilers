using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Bootstrap function-like ownership container projected from IR entry points.
    /// </summary>
    public sealed record IrFunction(
        string Name,
        string SectionName,
        int EntryInstructionIndex,
        int EntryBlockId,
        int EndInstructionIndex,
        IReadOnlyList<int> BlockIds,
        IReadOnlyList<string> LabelNames,
        IReadOnlyList<string> EntryPointNames,
    bool IsSynthetic,
    IrSourceSpan? SourceSpan = null);
}
