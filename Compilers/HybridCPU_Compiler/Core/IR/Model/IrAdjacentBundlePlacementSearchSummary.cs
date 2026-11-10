namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Summary metrics for one Stage 6 adjacent-bundle placement search.
    /// </summary>
    public sealed record IrAdjacentBundlePlacementSearchSummary(
        int EvaluatedPlacementPairCount,
        int ParetoOptimalPlacementPairCount,
        int DominatedPlacementPairCount)
    {
        /// <summary>
        /// Gets an empty adjacent-bundle search summary.
        /// </summary>
        public static IrAdjacentBundlePlacementSearchSummary Empty { get; } = new(0, 0, 0);
    }
}
