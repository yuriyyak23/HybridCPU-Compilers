namespace HybridCPU.Compiler.Core.IR;

public sealed partial class HybridCpuLoopMiiAnalyzerV1
{
    // A canonical iteration's distance-zero graph must be a DAG. Nested backedges
    // cannot silently become intra-iteration recurrence proof. This is an optimization
    // shape rejection; the caller's existing explicit ordinary-loop fallback governs use.
    private static bool HasZeroDistanceCycle(IReadOnlyList<IrLoopDistanceDependencyV1> edges)
    {
        IrLoopDistanceDependencyV1[] zero = edges.Where(e => e.IterationDistance == 0).ToArray();
        int[] nodes = zero.SelectMany(e => new[] { e.ProducerInstructionIndex, e.ConsumerInstructionIndex }).Distinct().ToArray();
        Dictionary<int, int> degree = nodes.ToDictionary(n => n, _ => 0);
        Dictionary<int, IrLoopDistanceDependencyV1[]> outgoing = zero.GroupBy(e => e.ProducerInstructionIndex).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (IrLoopDistanceDependencyV1? edge in zero) degree[edge.ConsumerInstructionIndex]++;
        Queue<int> ready = new Queue<int>(nodes.Where(n => degree[n] == 0).Order());
        int count = 0;
        while (ready.TryDequeue(out int node)) {
            count++;
            foreach (IrLoopDistanceDependencyV1? edge in outgoing.GetValueOrDefault(node) ?? [])
                if (--degree[edge.ConsumerInstructionIndex] == 0) ready.Enqueue(edge.ConsumerInstructionIndex);
        }
        return count != nodes.Length;
    }

    // At integer II=k, a cycle violates k iff sum(latency-k*distance)>0.
    // Longest-path Bellman-Ford from a synthetic zero source detects such a cycle.
    // Binary search finds the least feasible integer k; a positive cycle at k-1 is
    // a real lower-bound witness. The final feasibility check supplies the upper bound.
    private static CycleEnumeration SolveRecurrenceRatio(IReadOnlyList<IrLoopDistanceDependencyV1> input, int maximumRelaxations)
    {
        string failure = "recurrence-ratio-incomplete";
        CycleEnumeration Incomplete() => new(true, 0, 0, 0, IrLoopProofPrecisionV1.Unknown, []) { FailureReason = failure };
        if (maximumRelaxations <= 0 || input.Count == 0 ||
            input.Any(e => e.IterationDistance < 0 || e.LatencyCycles < 0)) return Incomplete();
        IrLoopDistanceDependencyV1[] edges = input.OrderBy(e => e.ProducerInstructionIndex).ThenBy(e => e.ConsumerInstructionIndex)
            .ThenBy(e => e.EdgeId, StringComparer.Ordinal).ToArray();
        int[] nodes = edges.SelectMany(e => new[] { e.ProducerInstructionIndex, e.ConsumerInstructionIndex }).Distinct().Order().ToArray();
        Dictionary<int, int> indices = nodes.Select((node, index) => (node, index)).ToDictionary(row => row.node, row => row.index);
        int[] from = edges.Select(e => indices[e.ProducerInstructionIndex]).ToArray();
        int[] to = edges.Select(e => indices[e.ConsumerInstructionIndex]).ToArray();
        long used = 0;
        bool exhausted = false;
        (bool Positive, int[] Cycle) Detect(int ii)
        {
            long[] distances = new long[nodes.Length];
            int[] predecessor = Enumerable.Repeat(-1, nodes.Length).ToArray();
            int updated = -1;
            for (int round = 0; round < nodes.Length; round++) {
                updated = -1;
                for (int edge = 0; edge < edges.Length; edge++) {
                    if (++used > maximumRelaxations) { failure = "recurrence-relaxation-budget-exhausted"; exhausted = true; return (false, []); }
                    long candidate = checked(distances[from[edge]] + edges[edge].LatencyCycles - (long)ii * edges[edge].IterationDistance);
                    if (candidate <= distances[to[edge]]) continue;
                    distances[to[edge]] = candidate; predecessor[to[edge]] = edge; updated = to[edge];
                }
                if (updated < 0) return (false, []);
            }
            int vertex = updated;
            for (int step = 0; step < nodes.Length; step++) {
                if (predecessor[vertex] < 0) { exhausted = true; return (false, []); }
                vertex = from[predecessor[vertex]];
            }
            int start = vertex;
            List<int> cycle = new List<int>();
            do {
                int edge = predecessor[vertex];
                if (edge < 0 || cycle.Count >= nodes.Length) { exhausted = true; return (false, []); }
                cycle.Add(edge); vertex = from[edge];
            } while (vertex != start);
            cycle.Reverse();
            return (true, cycle.ToArray());
        }
        try {
            long sum = edges.Sum(e => (long)e.LatencyCycles);
            if (sum > int.MaxValue) return Incomplete();
            int upper = Math.Max(1, (int)sum);
            // A positive zero-distance cycle has no finite feasible II; never certify it.
            if (Detect(upper).Positive) { failure = "recurrence-positive-zero-distance-cycle"; return Incomplete(); }
            if (exhausted) return Incomplete();
            (bool Positive, int[] Cycle) atZero = Detect(0);
            if (exhausted) return Incomplete();
            if (!atZero.Positive) return new(false, 0, 0, 0, IrLoopProofPrecisionV1.Exact, []);
            int lower = 1;
            while (lower < upper) {
                int middle = lower + (upper - lower) / 2;
                bool positive = Detect(middle).Positive;
                if (exhausted) return Incomplete();
                if (positive) lower = middle + 1; else upper = middle;
            }
            (bool Positive, int[] Cycle) witness = Detect(lower - 1);
            if (exhausted || !witness.Positive || Detect(lower).Positive || exhausted) return Incomplete();
            IrLoopDistanceDependencyV1[] cycleEdges = witness.Cycle.Select(index => edges[index]).ToArray();
            long latency = cycleEdges.Sum(e => (long)e.LatencyCycles), distance = cycleEdges.Sum(e => (long)e.IterationDistance);
            if (distance <= 0 || latency > int.MaxValue || distance > int.MaxValue ||
                (latency + distance - 1) / distance != lower) return Incomplete();
            for (int index = 0; index < cycleEdges.Length; index++)
                if (cycleEdges[index].ConsumerInstructionIndex != cycleEdges[(index + 1) % cycleEdges.Length].ProducerInstructionIndex)
                    return Incomplete();
            return new(false, lower, (int)latency, (int)distance,
                cycleEdges.Any(e => e.Precision != IrLoopProofPrecisionV1.Exact) ? IrLoopProofPrecisionV1.Conservative : IrLoopProofPrecisionV1.Exact,
                [string.Join(',', cycleEdges.Select(e => e.EdgeId))]);
        }
        catch (OverflowException) { return Incomplete(); }
    }
}
