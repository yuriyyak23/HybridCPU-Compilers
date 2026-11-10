using System.Reflection;
using System.Reflection.Emit;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;

internal static class SchedulerSuccessorSmoke
{
    private static readonly MethodInfo Counter = typeof(HybridCpuLocalListScheduler)
        .GetMethod("BuildExactSuccessorCounts", BindingFlags.NonPublic | BindingFlags.Static)!;

    public static void Run()
    {
        PersistedAssemblyBuilder assembly = new PersistedAssemblyBuilder(new AssemblyName("SchedulerFixture"), typeof(object).Assembly);
        TypeBuilder type = assembly.DefineDynamicModule("Main").DefineType("Fixture", TypeAttributes.Public);
        ILGenerator il = type.DefineMethod("Calculate", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [typeof(int)]).GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_3); il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_2); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Add); il.Emit(OpCodes.Ret);
        type.CreateType();
        using MemoryStream stream = new MemoryStream();
        assembly.Save(stream);
        RestrictedCilImportResultV1 imported = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportImage(stream.ToArray(), new("Fixture", "Calculate"));
        Check(imported.Program is not null, "fixture import");
        IrInstruction template = imported.Program!.Instructions[0];

        IrBasicBlockSchedulingDag Graph(int count, IEnumerable<(int From, int To)> pairs)
        {
            List<IrInstructionDependency>[] incoming = Enumerable.Range(0, count).Select(_ => new List<IrInstructionDependency>()).ToArray();
            List<IrInstructionDependency>[] outgoing = Enumerable.Range(0, count).Select(_ => new List<IrInstructionDependency>()).ToArray();
            List<IrInstructionDependency> edges = new List<IrInstructionDependency>();
            foreach ((int from, int to) in pairs)
            {
                // Non-dense instruction identities must not be used as bit indexes.
                IrInstructionDependency edge = new IrInstructionDependency(default, 7 + from * 3, 7 + to * 3, 1);
                outgoing[from].Add(edge);
                if (to >= 0 && to < count) incoming[to].Add(edge);
                edges.Add(edge);
            }
            IrInstruction[] instructions = Enumerable.Range(0, count).Select(i => template with { Index = 7 + i * 3 }).ToArray();
            IrSchedulingNode[] nodes = instructions.Select((instruction, i) => new IrSchedulingNode(instruction.Index,
                instruction, incoming[i], outgoing[i], 1)).ToArray();
            return new(0, instructions, nodes, edges);
        }

        void Verify(IrBasicBlockSchedulingDag graph)
        {
            (Dictionary<int, int> Counts, long Edges, long Words) first = Count(graph);
            (Dictionary<int, int> Counts, long Edges, long Words) second = Count(graph);
            Check(first.Counts.OrderBy(p => p.Key).SequenceEqual(second.Counts.OrderBy(p => p.Key)) &&
                first.Edges == second.Edges && first.Words == second.Words, "deterministic counts/work");
            Dictionary<int, IrSchedulingNode> map = graph.Nodes.ToDictionary(n => n.InstructionIndex);
            foreach (IrSchedulingNode root in graph.Nodes)
            {
                HashSet<int> seen = new HashSet<int>();
                Stack<int> pending = new Stack<int>();
                pending.Push(root.InstructionIndex);
                while (pending.TryPop(out int current))
                {
                    if (!seen.Add(current)) continue;
                    foreach (IrInstructionDependency edge in map[current].OutgoingDependencies) pending.Push(edge.ConsumerInstructionIndex);
                }
                Check(first.Counts[root.InstructionIndex] == seen.Count - 1, "exact DFS oracle parity");
            }
        }
        Verify(Graph(0, []));
        Verify(Graph(5, [(0, 1), (1, 2), (2, 3), (3, 4)]));
        Verify(Graph(6, [(0, 1), (0, 2), (1, 3), (2, 3), (4, 5)]));
        Verify(Graph(5, []));
        Verify(Graph(3, [(2, 0), (0, 1)])); // node order is not assumed topological
        Verify(Graph(3, [(0, 1), (0, 1), (1, 2)]));
        Random random = new Random(193);
        for (int trial = 0; trial < 100; trial++)
        {
            List<(int, int)> edges = new List<(int, int)>();
            for (int i = 0; i < 35; i++)
                for (int j = i + 1; j < 35; j++)
                    if (random.Next(4) == 0) edges.Add((i, j));
            Verify(Graph(35, edges.OrderBy(_ => random.Next()).ToArray()));
        }
        Reject(Graph(2, [(0, 1), (1, 0)]));
        Reject(Graph(1, [(0, 0)]));
        Reject(Graph(2, [(0, 2)]));
        IrBasicBlockSchedulingDag valid = Graph(2, [(0, 1)]);
        Reject(new(0, valid.Instructions, [valid.Nodes[0], valid.Nodes[0]], valid.Dependencies));
        IrSchedulingNode wrongProducer = valid.Nodes[0] with { OutgoingDependencies = [valid.Dependencies[0] with { ProducerInstructionIndex = 999 }] };
        Reject(new(0, valid.Instructions, [wrongProducer, valid.Nodes[1]], valid.Dependencies));

        const int large = 16384;
        (Dictionary<int, int> Counts, long Edges, long Words) chain = Count(Graph(large, Enumerable.Range(0, large - 1).Select(i => (i, i + 1))));
        for (int i = 0; i < large; i++) Check(chain.Counts[7 + i * 3] == large - i - 1, "large chain count");
        Check(chain.Edges == 3L * (large - 1) && chain.Words == (long)(large - 1) * (large / 64), "bounded chain work");
        const int denseSize = 1024;
        IEnumerable<(int i, int j)> denseEdges = from i in Enumerable.Range(0, denseSize)
                         from j in Enumerable.Range(i + 1, denseSize - i - 1) select (i, j);
        (Dictionary<int, int> Counts, long Edges, long Words) dense = Count(Graph(denseSize, denseEdges));
        Check(dense.Edges == 3L * denseSize * (denseSize - 1) / 2 &&
              dense.Words == (long)(denseSize - 1) * (denseSize / 64), "redundant dense unions eliminated exactly");
        for (int i = 0; i < denseSize; i++) Check(dense.Counts[7 + i * 3] == denseSize - i - 1, "dense count");

        string Schedule()
        {
            IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(imported.Program);
            foreach (IrBasicBlockSchedule block in schedule.BlockSchedules) Verify(block.Dag);
            return string.Join(';', schedule.BlockSchedules.SelectMany(b => b.ScheduledInstructions)
                .Select(i => $"{i.InstructionIndex}:{i.Cycle}:{i.OrderInCycle}:{i.ReadyCycle}"));
        }
        Check(Schedule() == Schedule(), "deterministic production schedule");
        Console.WriteLine($"PASS SchedulerSuccessorSmoke: exact DAG counts, malformed rejection, deterministic schedule; chain edge visits={chain.Edges}, word unions={chain.Words}; dense unions={dense.Words}");
    }

    private static (Dictionary<int, int> Counts, long Edges, long Words) Count(IrBasicBlockSchedulingDag graph)
    {
        object?[] args = [graph, 0L, 0L];
        Dictionary<int, int> counts = (Dictionary<int, int>)Counter.Invoke(null, args)!;
        return (counts, (long)args[1]!, (long)args[2]!);
    }

    private static void Reject(IrBasicBlockSchedulingDag graph)
    {
        try { Count(graph); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { return; }
        throw new Exception("Scheduler accepted malformed/cyclic DAG");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Scheduler successor regression: " + message);
    }
}
