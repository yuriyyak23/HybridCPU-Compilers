namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Source binding for an IR instruction produced by a future scanner/parser path.
    /// </summary>
    public sealed record IrInstructionSourceBinding(int InstructionIndex, IrSourceSpan SourceSpan);

    /// <summary>
    /// Parser-oriented section declaration projected into the IR ownership layer.
    /// </summary>
    public sealed record IrSectionDeclaration(string Name, IrSourceSpan SourceSpan);

    /// <summary>
    /// Parser-oriented function declaration projected into the IR ownership layer.
    /// </summary>
    public sealed record IrFunctionDeclaration(string Name, string SectionName, int EntryInstructionIndex, IrSourceSpan SourceSpan);

    /// <summary>
    /// Source-bound symbol reference used for scoped resolution against program metadata.
    /// </summary>
    public sealed record IrSourceSymbolReference(string Name, IrSourceSpan SourceSpan, string? SectionName = null, string? FunctionName = null);
}
