namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Bootstrap declaration for binding a source label name to an IR instruction index.
    /// </summary>
    public sealed record IrLabelDeclaration(
        string Name,
        int InstructionIndex,
        IrSourceSpan? SourceSpan = null,
        string? SectionName = null,
        string? FunctionName = null);

    /// <summary>
    /// Bootstrap declaration for binding an entry-point name to an IR instruction index.
    /// </summary>
    public sealed record IrEntryPointDeclaration(
        string Name,
        IrEntryPointKind Kind,
        int InstructionIndex,
        IrSourceSpan? SourceSpan = null,
        string? SectionName = null,
        string? FunctionName = null);
}
