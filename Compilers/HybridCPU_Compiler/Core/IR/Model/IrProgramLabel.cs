namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Named label bound to an IR instruction address.
    /// </summary>
    public sealed record IrProgramLabel(
        string Name,
        int InstructionIndex,
        ulong Address,
        int BlockId,
        bool IsSynthetic,
    bool IsEntryLabel,
    string? SectionName,
    string? FunctionName,
    IrSourceSpan? SourceSpan = null);
}
