namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Summary metrics for one Stage 6 physical slot placement search.
    /// </summary>
    public sealed record IrBundlePlacementSearchSummary(
        int EvaluatedPlacementCount,
        int ParetoOptimalPlacementCount,
        int DominatedPlacementCount)
    {
        /// <summary>
        /// Gets an empty search summary.
        /// </summary>
        public static IrBundlePlacementSearchSummary Empty { get; } = new(0, 0, 0);
    }
}
