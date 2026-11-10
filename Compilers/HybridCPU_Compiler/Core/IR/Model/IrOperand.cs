namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Normalized IR operand.
    /// </summary>
    public sealed record IrOperand(IrOperandKind Kind, ulong Value, string Name);
}
