namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Compiler-side symbolic control-transfer classification for relocation diagnostics.
    /// </summary>
    public enum IrControlTransferKind
    {
        Branch = 0,
        ConditionalBranch = 1,
        Call = 2,
        Return = 3
    }

    /// <summary>
    /// Symbolic control-flow target emitted before final VLIW bundle placement is known.
    /// </summary>
    public sealed record IrControlFlowTargetReference(
        int InstructionIndex,
        string TargetName,
        IrControlTransferKind TransferKind);
}
