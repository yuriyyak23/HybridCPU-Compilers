namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Entry-point classification attached to an IR program.
    /// </summary>
    public enum IrEntryPointKind
    {
        ProgramEntry = 0,
        EntryPoint = 1,
        CallTarget = 2,
        InterruptHandler = 3
    }

    /// <summary>
    /// Named entry point bound to an IR instruction address.
    /// </summary>
    public sealed record IrEntryPointMetadata(
        string Name,
        IrEntryPointKind Kind,
        int InstructionIndex,
        ulong Address,
        int BlockId,
    bool IsSynthetic,
    string? SectionName,
    string? FunctionName,
    IrSourceSpan? SourceSpan = null);
}
