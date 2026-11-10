namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Summary metrics for one Stage 6 adjacent-bundle triplet placement search.
    /// </summary>
    public sealed record IrAdjacentBundleTripletPlacementSearchSummary(
        int EvaluatedPlacementTripletCount,
        int ParetoOptimalPlacementTripletCount,
        int DominatedPlacementTripletCount)
    {
        /// <summary>
        /// Gets an empty adjacent-bundle triplet search summary.
        /// </summary>
        public static IrAdjacentBundleTripletPlacementSearchSummary Empty { get; } = new(0, 0, 0);
    }
}
