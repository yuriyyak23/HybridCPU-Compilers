namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Summary metrics for one scalable global Stage 6 program placement search.
    /// </summary>
    public sealed record IrProgramPlacementSearchSummary(
        int BlockCount,
        int BundleCount,
        int EvaluatedTransitionCount,
        int RetainedStateCount)
    {
        /// <summary>
        /// Gets an empty scalable global program search summary.
        /// </summary>
        public static IrProgramPlacementSearchSummary Empty { get; } = new(0, 0, 0, 0);
    }
}
