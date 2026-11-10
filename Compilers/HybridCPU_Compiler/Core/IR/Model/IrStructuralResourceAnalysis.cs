using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Describes usage for one structural resource in a candidate issue group.
    /// </summary>
    public sealed record IrStructuralResourceUsage(
        IrStructuralResource Resource,
        int UsedUnits,
        int Capacity)
    {
        /// <summary>
        /// Gets a value indicating whether the resource usage exceeds modeled capacity.
        /// </summary>
        public bool IsOverSubscribed => UsedUnits > Capacity;
    }

    /// <summary>
    /// Summarizes structural resource pressure for a candidate cycle group.
    /// </summary>
    public sealed record IrStructuralResourceAnalysis(
        IrStructuralResource CombinedResources,
        IReadOnlyList<IrStructuralResourceUsage> ResourceUsages,
        IReadOnlyList<IrStructuralResourceUsage> ConflictingUsages)
    {
        /// <summary>
        /// Gets a value indicating whether the candidate group exceeds any structural capacity.
        /// </summary>
        public bool HasConflicts => ConflictingUsages.Count != 0;
    }
}
