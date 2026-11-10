using System.Reflection;
using HybridCPU.Compiler.Core.IR;

internal static class RecurrenceParallelChecks
{
    public static void Run()
    {
        MethodInfo method = typeof(HybridCpuLoopMiiAnalyzerV1).GetMethod("EnumerateCycles", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo ratio = typeof(HybridCpuLoopMiiAnalyzerV1).GetMethod("SolveRecurrenceRatio", BindingFlags.NonPublic | BindingFlags.Static)!;
        (bool Exhausted, int Ii, string[] Contributors) Compute(IrLoopDistanceDependencyV1[] edges, int budget) {
            object result = method.Invoke(null, [edges, budget])!;
            Type type = result.GetType();
            return ((bool)type.GetProperty("BudgetExhausted")!.GetValue(result)!,
                (int)type.GetProperty("MaximumIi")!.GetValue(result)!,
                (string[])type.GetProperty("Contributors")!.GetValue(result)!);
        }
        int Oracle(IrLoopDistanceDependencyV1[] edges) {
            int best = 0;
            foreach (int start in edges.SelectMany(e => new[] { e.ProducerInstructionIndex, e.ConsumerInstructionIndex }).Distinct()) {
                HashSet<int> visited = new HashSet<int> { start };
                void Visit(int node, int latency, int distance) {
                    foreach (IrLoopDistanceDependencyV1? edge in edges.Where(e => e.ProducerInstructionIndex == node)) {
                        if (edge.ConsumerInstructionIndex == start) {
                            int d = distance + edge.IterationDistance;
                            if (d > 0) best = Math.Max(best, (latency + edge.LatencyCycles + d - 1) / d);
                        }
                        else if (visited.Add(edge.ConsumerInstructionIndex)) {
                            Visit(edge.ConsumerInstructionIndex, latency + edge.LatencyCycles, distance + edge.IterationDistance);
                            visited.Remove(edge.ConsumerInstructionIndex);
                        }
                    }
                }
                Visit(start, 0, 0);
            }
            return best;
        }
        Random random = new Random(19321);
        for (int test = 0; test < 48; test++) {
            List<IrLoopDistanceDependencyV1> edges = new List<IrLoopDistanceDependencyV1>();
            for (int from = 0; from < 4; from++) for (int to = 0; to < 4; to++) {
                if (random.Next(3) == 0) continue;
                for (int parallel = 0; parallel < 3; parallel++)
                    edges.Add(new($"e{edges.Count}", from, to, from < to ? 0 : 1, random.Next(1, 8),
                        IrInstructionDependencyKind.RegisterRaw, IrLoopProofPrecisionV1.Exact, "oracle"));
            }
            IrLoopDistanceDependencyV1[] input = edges.ToArray(); (bool Exhausted, int Ii, string[] Contributors) result = Compute(input, 8192);
            object exact = ratio.Invoke(null, [input, 8_000_000])!;
            if ((bool)exact.GetType().GetProperty("BudgetExhausted")!.GetValue(exact)! ||
                (int)exact.GetType().GetProperty("MaximumIi")!.GetValue(exact)! != Oracle(input))
                throw new Exception("Exact positive-cycle ratio differs from independent exhaustive oracle");
            IReadOnlyList<string> witnesses = (IReadOnlyList<string>)exact.GetType().GetProperty("Contributors")!.GetValue(exact)!;
            foreach (string witness in witnesses) {
                IrLoopDistanceDependencyV1[] cycle = witness.Split(',').Select(id => input.Single(e => e.EdgeId == id)).ToArray();
                int distance = cycle.Sum(e => e.IterationDistance), latency = cycle.Sum(e => e.LatencyCycles);
                if (distance <= 0 || (latency + distance - 1) / distance != Oracle(input) ||
                    cycle.Where((edge, index) => edge.ConsumerInstructionIndex != cycle[(index + 1) % cycle.Length].ProducerInstructionIndex).Any())
                    throw new Exception("Exact ratio witness is not a closed attaining input cycle");
            }
            if (result.Exhausted || result.Ii != Oracle(input) ||
                result.Contributors.SelectMany(c => c.Split(',')).Any(id => !input.Any(e => e.EdgeId == id)) ||
                result.Ii != Compute(input.Reverse().ToArray(), 8192).Ii)
                throw new Exception("Parallel recurrence exhaustive oracle/witness/order mismatch");
        }
        IrLoopDistanceDependencyV1[] dense = Enumerable.Range(0, 40).SelectMany(index => new[] {
            new IrLoopDistanceDependencyV1("a" + index, 0, 1, 0, index + 1, IrInstructionDependencyKind.RegisterRaw, IrLoopProofPrecisionV1.Exact, "parallel"),
            new IrLoopDistanceDependencyV1("b" + index, 1, 0, 1, index + 1, IrInstructionDependencyKind.RegisterRaw, IrLoopProofPrecisionV1.Exact, "parallel") }).ToArray();
        (bool Exhausted, int Ii, string[] Contributors) bounded = Compute(dense, 1);
        if (bounded.Exhausted || bounded.Ii != 80) throw new Exception("Dominated parallel cycles consumed cycle budget");
        IrLoopDistanceDependencyV1[] differentDistances = dense.Append(new("different", 1, 0, 2, 100, IrInstructionDependencyKind.RegisterRaw,
            IrLoopProofPrecisionV1.Exact, "different-distance")).ToArray();
        if (!Compute(differentDistances, 1).Exhausted || Compute(differentDistances, 8192).Ii != Oracle(differentDistances))
            throw new Exception("Distinct-distance budget/admission contract changed");
        object denied = ratio.Invoke(null, [dense, 1])!;
        if (!(bool)denied.GetType().GetProperty("BudgetExhausted")!.GetValue(denied)!)
            throw new Exception("Recurrence relaxation budget ignored");
        IrLoopDistanceDependencyV1[] impossible = new[] { new IrLoopDistanceDependencyV1("z0", 0, 1, 0, 1, IrInstructionDependencyKind.RegisterRaw,
            IrLoopProofPrecisionV1.Exact, "zero-distance"), new IrLoopDistanceDependencyV1("z1", 1, 0, 0, 1,
            IrInstructionDependencyKind.RegisterRaw, IrLoopProofPrecisionV1.Exact, "zero-distance") };
        object noFiniteRatio = ratio.Invoke(null, [impossible, 8_000_000])!;
        if (!(bool)noFiniteRatio.GetType().GetProperty("BudgetExhausted")!.GetValue(noFiniteRatio)!)
            throw new Exception("Positive zero-distance cycle incorrectly certified a finite ratio");
        MethodInfo shape = typeof(HybridCpuLoopMiiAnalyzerV1).GetMethod("HasZeroDistanceCycle", BindingFlags.NonPublic | BindingFlags.Static)!;
        if (!(bool)shape.Invoke(null, [impossible])! || (bool)shape.Invoke(null, [dense])!)
            throw new Exception("Canonical distance-zero DAG shape gate failed");
        Console.WriteLine("PASS recurrence parallel dominance: 48 independent exhaustive max-II oracles, real edge witnesses/order, bounded parallel cycles and distinct-distance exhaustion");
    }
}
