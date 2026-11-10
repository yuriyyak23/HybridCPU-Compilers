using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU.Compiler.Core.IR.Telemetry.Contracts;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Plan for distributing parallel work across virtual threads.
/// </summary>
/// <param name="WorkerCount">Number of VTs to use (1–3 for SMT_WAYS=4, VT0 reserved for coordinator).</param>
/// <param name="ChunkSize">Iterations per worker (except possibly the last).</param>
/// <param name="RemainderChunk">Extra iterations assigned to the last worker.</param>
/// <param name="CoordinatorVtId">VT running the coordinator (always 0).</param>
/// <param name="WorkerVtIds">VT IDs assigned to workers.</param>
public sealed record ChunkPlan(
    int WorkerCount,
    long ChunkSize,
    long RemainderChunk,
    int CoordinatorVtId,
    IReadOnlyList<int> WorkerVtIds);

/// <summary>
/// Given a <see cref="ParallelRegionInfo"/>, decides how to partition work across VTs.
/// Optionally uses telemetry profile feedback to tune worker count and chunk sizes.
/// </summary>
public sealed class PartitionPlanner
{
    /// <summary>Maximum number of worker VTs (SMT_WAYS - 1; VT0 is coordinator).</summary>
    private const int MaxWorkers = 3;

    /// <summary>VT ID reserved for the coordinator.</summary>
    private const int CoordinatorVt = 0;

    /// <summary>
    /// When <c>true</c>, the planner uses a loaded telemetry profile to adjust worker count
    /// and chunk sizes based on prior runtime performance. Default: <c>false</c>.
    /// </summary>
    public bool UseProfileGuidedDecomposition { get; set; }

    /// <summary>
    /// Telemetry profile reader supplying worker performance metrics.
    /// </summary>
    public TelemetryProfileReader? ProfileReader { get; set; }

    /// <summary>
    /// Reject rate threshold above which the planner reduces worker count to decrease VT contention.
    /// </summary>
    private const double HighRejectRateThreshold = 0.20;

    /// <summary>
    /// Imbalance ratio threshold above which the planner considers chunk rebalancing.
    /// Computed as (maxCycles - minCycles) / maxCycles.
    /// </summary>
    private const double ImbalanceThreshold = 0.30;

    /// <summary>
    /// Creates a chunk plan for the given parallel region.
    /// </summary>
    /// <param name="region">The parallel region to partition.</param>
    /// <returns>A chunk plan, or <c>null</c> if the region cannot be parallelized (e.g., ≤ 0 iterations).</returns>
    public ChunkPlan? PlanPartition(ParallelRegionInfo region)
    {
        ArgumentNullException.ThrowIfNull(region);

        long totalIterations = region.IterationCount;

        if (totalIterations <= 0)
        {
            return null;
        }

        // Single iteration → no decomposition (sequential fallback)
        if (totalIterations == 1)
        {
            return null;
        }

        int workerCount = (int)Math.Min(MaxWorkers, totalIterations);

        // Profile-guided worker count reduction
        if (UseProfileGuidedDecomposition && ProfileReader is { HasProfile: true })
        {
            workerCount = AdjustWorkerCount(workerCount);
        }

        long chunkSize = totalIterations / workerCount;
        long remainder = totalIterations % workerCount;

        List<int> workerVtIds = new List<int>(workerCount);
        for (int i = 0; i < workerCount; i++)
        {
            // Workers use VT IDs 1..MaxWorkers
            workerVtIds.Add(i + 1);
        }

        return new ChunkPlan(
            WorkerCount: workerCount,
            ChunkSize: chunkSize,
            RemainderChunk: remainder,
            CoordinatorVtId: CoordinatorVt,
            WorkerVtIds: workerVtIds);
    }

    /// <summary>
    /// Computes the iteration range [start, end) for a specific worker.
    /// </summary>
    /// <param name="plan">The chunk plan.</param>
    /// <param name="workerIndex">Zero-based worker index.</param>
    /// <param name="region">The parallel region.</param>
    /// <returns>Tuple of (iterationStart, iterationEnd) for this worker.</returns>
    public static (long Start, long End) GetWorkerRange(ChunkPlan plan, int workerIndex, ParallelRegionInfo region)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(region);

        if (workerIndex < 0 || workerIndex >= plan.WorkerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(workerIndex));
        }

        long baseStart = region.IterationStart + workerIndex * plan.ChunkSize * region.IterationStep;
        long chunkIterations = plan.ChunkSize;

        // Last worker gets the remainder
        if (workerIndex == plan.WorkerCount - 1)
        {
            chunkIterations += plan.RemainderChunk;
        }

        long end = baseStart + chunkIterations * region.IterationStep;
        return (baseStart, end);
    }

    /// <summary>
    /// Adjusts worker count based on profile data.
    /// Reduces workers when high reject rates indicate VT contention.
    /// </summary>
    private int AdjustWorkerCount(int baseWorkerCount)
    {
        if (ProfileReader is null || !ProfileReader.HasProfile)
            return baseWorkerCount;

        IReadOnlyDictionary<string, WorkerPerformanceMetrics> workerMetrics = ProfileReader.GetAllWorkerMetrics();
        if (workerMetrics.Count == 0)
            return baseWorkerCount;

        // Check average reject rate across workers
        double totalRejectRate = 0.0;
        int workerWithMetrics = 0;
        foreach (WorkerPerformanceMetrics metrics in workerMetrics.Values)
        {
            totalRejectRate += metrics.RejectRate;
            workerWithMetrics++;
        }

        if (workerWithMetrics == 0)
            return baseWorkerCount;

        double avgRejectRate = totalRejectRate / workerWithMetrics;

        // High reject rate → reduce worker count (less VT contention)
        if (avgRejectRate > HighRejectRateThreshold && baseWorkerCount > 1)
        {
            return Math.Max(1, baseWorkerCount - 1);
        }

        return baseWorkerCount;
    }

    /// <summary>
    /// Computes the worker imbalance ratio from the profile.
    /// Returns 0.0 when no profile data is available.
    /// </summary>
    public double GetWorkerImbalanceRatio()
    {
        if (!UseProfileGuidedDecomposition || ProfileReader is null || !ProfileReader.HasProfile)
            return 0.0;

        IReadOnlyDictionary<string, WorkerPerformanceMetrics> workerMetrics = ProfileReader.GetAllWorkerMetrics();
        if (workerMetrics.Count < 2)
            return 0.0;

        long maxCycles = long.MinValue;
        long minCycles = long.MaxValue;

        foreach (WorkerPerformanceMetrics metrics in workerMetrics.Values)
        {
            if (metrics.TotalCycles > maxCycles)
                maxCycles = metrics.TotalCycles;
            if (metrics.TotalCycles < minCycles)
                minCycles = metrics.TotalCycles;
        }

        if (maxCycles <= 0)
            return 0.0;

        return (double)(maxCycles - minCycles) / maxCycles;
    }
}
