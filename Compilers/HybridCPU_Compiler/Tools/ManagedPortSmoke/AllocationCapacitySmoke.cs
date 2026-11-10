using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;

internal static class AllocationCapacitySmoke
{
    public static void Run()
    {
        CheckFrontierEquivalence();
        CheckManagedMetadataCapacity();
        CheckRejectedLoopSkipsDistanceDag();
        const int operations = 9001;
        const int instructionCapacityFloor = 20655;
        PersistedAssemblyBuilder assembly = new PersistedAssemblyBuilder(new AssemblyName("AllocationCapacityFixture"), typeof(object).Assembly);
        TypeBuilder type = assembly.DefineDynamicModule("Main").DefineType("Fixture", TypeAttributes.Public);
        ILGenerator il = type.DefineMethod("Negate", MethodAttributes.Public | MethodAttributes.Static,
            typeof(long), [typeof(long)]).GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        // One actual virtual definition per CIL operation, with a dependent chain.
        // Independent literal loads would instead stress ready-window composition.
        for (int i = 0; i < operations; i++) il.Emit(OpCodes.Neg);
        il.Emit(OpCodes.Ret);
        type.CreateType();
        using MemoryStream stream = new MemoryStream();
        assembly.Save(stream);
        byte[] pe = stream.ToArray();
        RestrictedCilImportResultV1 imported = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportImage(pe, new("Fixture", "Negate"));
        Check(imported.Program is not null, string.Join(';', imported.Diagnostics));
        IrProgram program = imported.Program!;
        CheckClosedCfgFrameClassification(program);
        CheckDependencyCompatibleAllocationOrder(program);
        CheckControlFrontierEquivalence(program.Instructions[0]);
        Check(program.Instructions.Count > 4096 && program.ValueFlow.Values.Count > 8192,
            $"fixture must exceed both legacy input limits: IR={program.Instructions.Count}, values={program.ValueFlow.Values.Count}");
        Check(HybridCpuRegisterAllocationBudgetsV1.Production.MaximumInstructions > instructionCapacityFloor &&
              HybridCpuRegisterAllocationBudgetsV1.Production.MaximumValues == 16384,
            "production must admit the confirmed 20655-instruction Doom body without broadening its value budget");
        HybridCpuValueAnalysisBudgetsV1 analysisBudgets = HybridCpuValueAnalysisBudgetsV1.Production;
        foreach (HybridCpuValueAnalysisBudgetsV1? budget in new[]
        {
            analysisBudgets with { MaximumInstructions = program.Instructions.Count - 1 },
            analysisBudgets with { MaximumValues = program.ValueFlow.Values.Count - 1 },
            analysisBudgets with { MaximumAccesses = program.ValueFlow.Accesses.Count - 1 }
        })
            Check(new HybridCpuValueLivenessPressureAnalyzerV1(budget).Analyze(program).Status ==
                IrValueAnalysisStatus.BudgetExhausted, "analysis bounds must remain fail-closed");
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        Check(schedule.ValueAnalysis.Status == IrValueAnalysisStatus.Complete,
            $"large input needs actual complete liveness, never substituted facts: " +
            $"{schedule.ValueAnalysis.Status}/{schedule.ValueAnalysis.Reason}; " +
            $"IR={program.Instructions.Count}, values={program.ValueFlow.Values.Count}, accesses={program.ValueFlow.Accesses.Count}");
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        HybridCpuMiiResourceModelV1 model = (HybridCpuMiiResourceModelV1)typeof(ScalarControlFlowV2ObjectLinkerV1)
            .GetField("ResourceModel", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        HybridCpuRegisterAllocationOptionsV1 options = HybridCpuRegisterAllocationOptionsV1.Qualification;
        HybridCpuScheduleAwareRegisterAllocatorV1 allocator = new HybridCpuScheduleAwareRegisterAllocatorV1();
        foreach (HybridCpuRegisterAllocationBudgetsV1? budget in new[]
        {
            options.Budgets with { MaximumInstructions = program.Instructions.Count - 1 },
            options.Budgets with { MaximumValues = program.ValueFlow.Values.Count - 1 }
        })
        {
            IrRegisterAllocationResultV1 rejected = allocator.Allocate(schedule, bundles, resourceModel: model,
                options: HybridCpuRegisterAllocationOptionsV1.Create(true, true, budget));
            Check(rejected.Status == IrRegisterAllocationStatusV1.BudgetExhausted && rejected.Witness is null &&
                ReferenceEquals(rejected.FinalSchedule, schedule), "over-budget input preserves exact input, with no success witness");
        }
        IrRegisterAllocationResultV1 invalid = allocator.Allocate(schedule, bundles, resourceModel: model,
            options: options with { OptionsDigest = new string('0', 64) });
        Check(invalid.Status == IrRegisterAllocationStatusV1.InvalidInput && invalid.Witness is null, "budget digest binding");
        IrRegisterAllocationResultV1 result = allocator.Allocate(schedule, bundles, resourceModel: model, options: options);
        Check(result.Status == IrRegisterAllocationStatusV1.Allocated && result.Witness is not null, result.Reason);
        IrAllocationProofRebuildV1 proof = result.Witness!.Rebuild;
        Check(proof.DependenciesCurrent && proof.LivenessCurrent && proof.PressureCurrent &&
            proof.ResourceFactsCurrent && proof.MiiRecomputed && proof.ExactW8PlacementRecomputed,
            "large allocation must rebuild every required proof");
        IReadOnlyList<HybridCpuInstructionBundle> lowered = new HybridCpuBundleLowerer().LowerProgram(result.FinalBundles);
        lowered = HybridCpuControlFlowRelocationResolver.ApplyRelocations(result.FinalBundles, lowered);
        byte[] code = new HybridCpuBundleSerializer().SerializeProgram(lowered);
        MethodInfo reference = Assembly.Load(pe).GetType("Fixture")!.GetMethod("Negate")!;
        foreach (ulong input in new[] { 0UL, 1UL, 0x8000000000000000UL, ulong.MaxValue, ulong.MaxValue - 2000, 0x123456789abcdef0UL })
        {
            ulong expected = unchecked((ulong)(long)reference.Invoke(null, [unchecked((long)input)])!);
            Check(NativeConstantSmoke.Evaluate(code, input) == expected,
                "emitted native arithmetic fragment parity with CoreCLR (not ISE execution)");
        }
        Console.WriteLine($"PASS AllocationCapacitySmoke: IR={program.Instructions.Count}, values={program.ValueFlow.Values.Count}; confirmed Doom instruction floor, exact budget negatives, proofs, native fragment/CoreCLR parity; sha256={Convert.ToHexString(SHA256.HashData(code))}");
    }

    private static void CheckRejectedLoopSkipsDistanceDag()
    {
        PersistedAssemblyBuilder assembly = new PersistedAssemblyBuilder(new AssemblyName("RejectedLoopFixture"), typeof(object).Assembly);
        TypeBuilder type = assembly.DefineDynamicModule("Main").DefineType("Fixture", TypeAttributes.Public);
        ILGenerator il = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static,
            typeof(void), [typeof(int)]).GetILGenerator();
        Label header = il.DefineLabel();
        il.MarkLabel(header);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Call, typeof(Math).GetMethod(nameof(Math.Abs), [typeof(long)])!);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Starg_S, (byte)0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, header);
        il.Emit(OpCodes.Ret);
        type.CreateType();
        using MemoryStream stream = new MemoryStream();
        assembly.Save(stream);
        RestrictedCilImportResultV1 imported = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportImage(stream.ToArray(), new("Fixture", "Run"));
        Check(imported.Program is not null, string.Join(';', imported.Diagnostics));
        HybridCpuMiiResourceModelV1 model = (HybridCpuMiiResourceModelV1)typeof(ScalarControlFlowV2ObjectLinkerV1)
            .GetField("ResourceModel", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        IrLoopCanonicalizationResultV1 canonical = new HybridCpuLoopMiiAnalyzerV1().Canonicalize(
            imported.Program!, model, HybridCpuLoopMiiBudgetsV1.Production with { MaximumDistanceEdges = 1 });
        IrCanonicalLoopV1 loop = canonical.Loops.Single();
        Check(loop.Status == IrCanonicalLoopStatusV1.Unsupported && loop.Reason == "unsupported-loop-call" &&
            loop.DistanceDependencies.Count == 0,
            $"rejected loop must preserve its exact reason without distance-DAG work: {loop.Status}/{loop.Reason}/{loop.DistanceDependencies.Count}");
    }

    private static void CheckDependencyCompatibleAllocationOrder(IrProgram program)
    {
        IrInstruction[] instructions = program.Instructions.Take(4).ToArray();
        IrInstructionDependency[] dependencies = new[]
        {
            new IrInstructionDependency(IrInstructionDependencyKind.Memory,
                instructions[0].Index, instructions[2].Index, 1),
            new IrInstructionDependency(IrInstructionDependencyKind.Serialization,
                instructions[1].Index, instructions[3].Index, 1)
        };
        IrBasicBlockDependencyGraph block = new IrBasicBlockDependencyGraph(0, instructions, dependencies);
        Dictionary<int, long> preferred = instructions.ToDictionary(instruction => instruction.Index,
            instruction => (long)-instruction.Index);
        MethodInfo method = typeof(HybridCpuScheduleAwareRegisterAllocatorV1).GetMethod(
            "BuildDependencyCompatibleTopologyOrder", BindingFlags.Static | BindingFlags.NonPublic)!;
        Dictionary<int, int> order = (Dictionary<int, int>)method.Invoke(null, [block, dependencies, preferred, CancellationToken.None])!;
        Check(order[instructions[0].Index] < order[instructions[2].Index] &&
              order[instructions[1].Index] < order[instructions[3].Index],
            "allocation topology order must respect non-register dependencies before preferred-rank tie breaking");

        IrInstructionDependency[] cycle =
        [
            dependencies[0],
            new(IrInstructionDependencyKind.Memory, instructions[2].Index, instructions[0].Index, 1)
        ];
        bool rejected = false;
        try { method.Invoke(null, [new IrBasicBlockDependencyGraph(0, instructions, cycle), cycle, preferred, CancellationToken.None]); }
        catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException) { rejected = true; }
        Check(rejected, "cyclic non-register allocation dependencies must fail closed");
    }

    private static void CheckClosedCfgFrameClassification(IrProgram program)
    {
        MethodInfo terminal = typeof(HybridCpuScheduleAwareRegisterAllocatorV1).GetMethod(
            "HasTerminalPath", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo balanced = typeof(HybridCpuScheduleAwareRegisterAllocatorV1).GetMethod(
            "HasBalancedFrameExits", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check((bool)terminal.Invoke(null, [program])! && (bool)balanced.Invoke(null, [program])!,
            "ordinary ret CFG must be terminal and frame-balanced");

        IrBasicBlock[] closedBlocks = program.BasicBlocks.Select(block => block.SuccessorBlockIds.Count == 0
            ? block with { SuccessorBlockIds = [block.Id], ExitBlock = false }
            : block).ToArray();
        IrProgram closed = program with { ControlFlowGraph = new ControlFlowGraph(closedBlocks, program.ControlFlowGraph.Edges) };
        Check(!(bool)terminal.Invoke(null, [closed])! && (bool)balanced.Invoke(null, [closed])!,
            "closed infinite CFG has no observable frame exit");

        IrBasicBlock exit = program.BasicBlocks.Single(block => block.SuccessorBlockIds.Count == 0);
        IrBasicBlock invalidExit = exit with { Instructions = exit.Instructions.Take(exit.Instructions.Count - 1).ToArray() };
        IrProgram invalid = program with { ControlFlowGraph = new ControlFlowGraph(
            program.BasicBlocks.Select(block => block.Id == exit.Id ? invalidExit : block).ToArray(),
            program.ControlFlowGraph.Edges) };
        Check((bool)terminal.Invoke(null, [invalid])! && !(bool)balanced.Invoke(null, [invalid])!,
            "terminal path without return/unwind transfer must remain fail-closed");
    }

    private static void CheckManagedMetadataCapacity()
    {
        const int doomSafepoints = 5617;
        Check(HybridCpuManagedMetadataBudgetsV1.Production.MaximumSafepoints ==
            HybridCpuManagedAbiFamilyV1.MaximumSafepoints,
            "compiler metadata budget must equal the encoded ABI bound");
        Check(HybridCpuManagedNonMovingGcBudgetsV1.Production.MaximumSafepoints ==
            HybridCpuManagedAbiFamilyV1.MaximumSafepoints,
            "runtime parser budget must equal the encoded ABI bound");
        HybridCpuSafepointRecordV1[] records = Enumerable.Range(0, doomSafepoints)
            .Select(index => new HybridCpuSafepointRecordV1(
                checked(index * HybridCpuBundleSerializer.BundleSizeBytes),
                HybridCpuSafepointCategoryV1.CallSite, []))
            .ToArray();
        HybridCpuGcInfoEncodingResultV1 first = HybridCpuManagedAbiEncodingV1.EncodeGcInfo(records);
        HybridCpuGcInfoEncodingResultV1 second = HybridCpuManagedAbiEncodingV1.EncodeGcInfo(records);
        Check(first.Status == HybridCpuPlatformFactStatus.Supported && first.Bytes.SequenceEqual(second.Bytes) &&
            first.Digest == second.Digest, "5617 exact call-site maps must encode deterministically");
        Check(BitConverter.ToInt32(first.Bytes, 8) == doomSafepoints,
            "encoded HCMG safepoint count must preserve every required call site");
        HybridCpuSafepointRecordV1[] overBound = Enumerable.Range(0,
                HybridCpuManagedAbiFamilyV1.MaximumSafepoints + 1)
            .Select(index => new HybridCpuSafepointRecordV1(
                checked(index * HybridCpuBundleSerializer.BundleSizeBytes),
                HybridCpuSafepointCategoryV1.CallSite, []))
            .ToArray();
        Check(HybridCpuManagedAbiEncodingV1.EncodeGcInfo(overBound).Status ==
            HybridCpuPlatformFactStatus.Unsupported,
            "HCMG encoding must fail closed above the synchronized ABI/runtime bound");
    }

    private static void CheckFrontierEquivalence()
    {
        MethodInfo digest = typeof(HybridCpuScheduleAwareRegisterAllocatorV1).GetMethod(
            "DigestDependencies", BindingFlags.Static | BindingFlags.NonPublic)!;
        IrProgramDependencyGraph emptyGraph = new IrProgramDependencyGraph([], new IrInterBlockDependencyGraph([], []));
        string emptyActual = (string)digest.Invoke(null, [emptyGraph])!;
        string emptyExpected = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("dependencies/v1||"))).ToLowerInvariant();
        Check(emptyActual == emptyExpected, "streaming dependency digest must preserve legacy UTF-8 bytes");
        MethodInfo method = typeof(HybridCpuScheduleAwareRegisterAllocatorV1).GetMethod(
            "BuildRegisterGroupSerializationFrontier", BindingFlags.Static | BindingFlags.NonPublic)!;
        Random random = new Random(193);
        for (int trial = 0; trial < 200; trial++)
        {
            int count = trial < 5 ? trial + 1 : random.Next(2, 80);
            ushort[] reads = Enumerable.Range(0, count).Select(_ => (ushort)random.Next(1 << 16)).ToArray();
            ushort[] writes = Enumerable.Range(0, count).Select(_ => (ushort)random.Next(1 << 16)).ToArray();
            int[] order = Enumerable.Range(0, count).OrderBy(_ => random.Next()).ToArray();
            Dictionary<int, int> position = order.Select((ordinal, index) => (ordinal, index)).ToDictionary(x => x.ordinal, x => x.index);
            List<(int, int)> allPairs = new List<(int, int)>();
            for (int left = 0; left < count; left++)
                for (int right = left + 1; right < count; right++)
                {
                    int producer = order[left], consumer = order[right];
                    if ((reads[consumer] & writes[producer]) != 0 ||
                        (writes[consumer] & reads[producer]) != 0 ||
                        (writes[consumer] & writes[producer]) != 0)
                        allPairs.Add((producer, consumer));
                }
            (int, int)[] frontier = ((IEnumerable<(int, int)>)method.Invoke(null, [reads, writes, order, CancellationToken.None])!).ToArray();
            bool[,] Closure(IEnumerable<(int From, int To)> edges)
            {
                bool[,] closure = new bool[count, count];
                foreach ((int from, int to) in edges) closure[from, to] = true;
                for (int middle = 0; middle < count; middle++)
                    for (int from = 0; from < count; from++)
                        if (closure[from, middle])
                            for (int to = 0; to < count; to++) closure[from, to] |= closure[middle, to];
                return closure;
            }
            bool[,] expected = Closure(allPairs), actual = Closure(frontier);
            for (int from = 0; from < count; from++)
                for (int to = 0; to < count; to++)
                    Check(expected[from, to] == actual[from, to],
                        $"frontier reachability differs at trial {trial}, {from}->{to}");
            Check(frontier.All(edge => position[edge.Item1] < position[edge.Item2]), "frontier must follow exact topology order");
        }
        ushort[] malformed = [1, 2];
        try { method.Invoke(null, [malformed, new ushort[1], new[] { 0, 1 }, CancellationToken.None]); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { return; }
        throw new Exception("Allocation capacity regression: malformed frontier input was accepted");
    }

    private static void CheckControlFrontierEquivalence(IrInstruction template)
    {
        Random random = new Random(194);
        for (int trial = 0; trial < 100; trial++)
        {
            const int count = 32;
            IrInstruction[] instructions = Enumerable.Range(0, count).Select(index =>
            {
                int shape = random.Next(8);
                IrSerializationKind serialization = shape == 0 ? IrSerializationKind.ExclusiveCycle : IrSerializationKind.None;
                IrControlFlowKind control = shape == 1 ? IrControlFlowKind.ConditionalBranch : IrControlFlowKind.None;
                bool pinned = shape is 2 or 3;
                IrIssueSlotMask slots = pinned ? (shape == 2 ? IrIssueSlotMask.Slot2 : IrIssueSlotMask.Slot3) : IrIssueSlotMask.Scalar;
                return template with
                {
                    Index = index,
                    Annotation = template.Annotation with
                    {
                        Serialization = serialization,
                        ControlFlowKind = control,
#pragma warning disable CS0618
                        LegalSlots = slots,
#pragma warning restore CS0618
                        BindingKind = pinned ? IrSlotBindingKind.HardPinned : IrSlotBindingKind.ClassFlexible,
                        MemoryReadRegion = null,
                        MemoryWriteRegion = null,
                        Defs = [], Uses = []
                    }
                };
            }).ToArray();
            IrBasicBlock block = new IrBasicBlock(0, 0, count - 1, 0, (ulong)count, false, instructions,
                [], [], true, false, null, [], null, null);
            HybridCpuDependencyAnalyzer pair = new HybridCpuDependencyAnalyzer();
            List<IrInstructionDependency> expectedEdges = new List<IrInstructionDependency>();
            for (int from = 0; from < count; from++)
                for (int to = from + 1; to < count; to++)
                    expectedEdges.AddRange(pair.AnalyzePair(instructions[from], instructions[to]).Where(d =>
                        d.Kind is IrInstructionDependencyKind.Control or IrInstructionDependencyKind.Serialization));
            IrInstructionDependency[] actualEdges = new HybridCpuBasicBlockDependencyAnalyzer().AnalyzeBlock(block).Dependencies
                .Where(d => d.Kind is IrInstructionDependencyKind.Control or IrInstructionDependencyKind.Serialization)
                .ToArray();
            int[,] LongestConstraints(IEnumerable<IrInstructionDependency> edges)
            {
                int[,] result = new int[count, count];
                for (int source = 0; source < count; source++)
                {
                    int[] distance = Enumerable.Repeat(int.MinValue, count).ToArray();
                    distance[source] = 0;
                    for (int from = source; from < count; from++)
                        if (distance[from] != int.MinValue)
                            foreach (IrInstructionDependency edge in edges.Where(edge =>
                                edge.ProducerInstructionIndex == from))
                                distance[edge.ConsumerInstructionIndex] = Math.Max(
                                    distance[edge.ConsumerInstructionIndex],
                                    checked(distance[from] + edge.MinimumLatencyCycles));
                    for (int target = 0; target < count; target++) result[source, target] = distance[target];
                }
                return result;
            }
            int[,] expected = LongestConstraints(expectedEdges), actual = LongestConstraints(actualEdges);
            for (int from = 0; from < count; from++)
                for (int to = 0; to < count; to++)
                    Check(expected[from, to] == actual[from, to],
                        $"weighted control/serialization constraint differs at trial {trial}, {from}->{to}: " +
                        $"expected={expected[from, to]}, actual={actual[from, to]}");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Allocation capacity regression: " + message);
    }
}
