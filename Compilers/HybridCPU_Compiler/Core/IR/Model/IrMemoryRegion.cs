namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Normalized memory access region used by dependence analysis.
    /// </summary>
    public sealed record IrMemoryRegion(ulong Address, uint Length, bool IsWrite);
}
