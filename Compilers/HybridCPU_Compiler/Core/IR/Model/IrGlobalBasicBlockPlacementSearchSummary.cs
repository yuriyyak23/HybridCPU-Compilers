namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Summary metrics for one scalable global Stage 6 basic-block placement search.
    /// </summary>
    public sealed record IrGlobalBasicBlockPlacementSearchSummary(
        int BundleCount,
        int EvaluatedTransitionCount,
        int RetainedStateCount)
    {
        /// <summary>
        /// Gets an empty scalable global basic-block search summary.
        /// </summary>
        public static IrGlobalBasicBlockPlacementSearchSummary Empty { get; } = new(0, 0, 0);
    }
}
