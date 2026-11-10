using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Bootstrap section container for a VT-local IR program.
    /// </summary>
    public sealed record IrSection(
        string Name,
        byte VirtualThreadId,
        int StartInstructionIndex,
        int EndInstructionIndex,
        IReadOnlyList<int> BlockIds,
        IReadOnlyList<string> FunctionNames,
        IReadOnlyList<string> LabelNames,
        IReadOnlyList<string> EntryPointNames,
    bool IsSynthetic,
    IrSourceSpan? SourceSpan = null);
}
