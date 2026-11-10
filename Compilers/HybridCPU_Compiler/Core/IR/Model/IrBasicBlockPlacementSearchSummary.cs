namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Summary metrics for one Stage 6 basic-block placement search.
    /// </summary>
    public sealed record IrBasicBlockPlacementSearchSummary(
        int EvaluatedPlacementCount,
        int ParetoOptimalPlacementCount,
        int DominatedPlacementCount)
    {
        /// <summary>
        /// Gets an empty basic-block placement search summary.
        /// </summary>
        public static IrBasicBlockPlacementSearchSummary Empty { get; } = new(0, 0, 0);
    }
}
