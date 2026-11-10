using System.Globalization;
using System.Text;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU.Compiler.Core.IR;

public sealed partial class HybridCpuLoopMiiAnalyzerV1
{
    public IrLoopCanonicalizationResultV1 Canonicalize(
        IrProgram program,
        HybridCpuMiiResourceModelV1? resourceModel = null,
        HybridCpuLoopMiiBudgetsV1? budgets = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        resourceModel ??= HybridCpuMiiResourceModelV1.Default;
        budgets ??= HybridCpuLoopMiiBudgetsV1.Production;
        ValidateBudgets(budgets);
        string shapeDigest = ComputeProgramShapeDigest(program);
        if (!HasValidModelDigest(resourceModel))
        {
            return new(
                IrCanonicalLoopStatusV1.Unknown,
                shapeDigest,
                HybridCpuTargetMachineContractV1.Default.ContractDigest,
                resourceModel.ModelDigest,
                Array.Empty<IrCanonicalLoopV1>(),
                [new("HCMI0006", "MII resource-model digest does not bind its capacities.")]);
        }
        IrValueAnalysisReportV1 valueAnalysis = new HybridCpuValueLivenessPressureAnalyzerV1().Analyze(program);
        if (valueAnalysis.Status != IrValueAnalysisStatus.Complete)
        {
            return new(
                IrCanonicalLoopStatusV1.Unknown,
                shapeDigest,
                HybridCpuTargetMachineContractV1.Default.ContractDigest,
                resourceModel.ModelDigest,
                Array.Empty<IrCanonicalLoopV1>(),
                [new("HCMI0001", "Loop value/liveness analysis is not complete.")]);
        }

        Dictionary<int, IrBasicBlock> blocks = program.BasicBlocks.ToDictionary(static block => block.Id);
        Dictionary<int, HashSet<int>> dominators = ComputeDominators(blocks);
        IGrouping<int, IrControlFlowEdge>[] backedgeGroups = program.ControlFlowGraph.Edges
            .Where(edge => dominators[edge.SourceBlockId].Contains(edge.TargetBlockId))
            .GroupBy(static edge => edge.TargetBlockId)
            .OrderBy(static group => group.Key)
            .ToArray();
        if (backedgeGroups.Length > budgets.MaximumLoops)
        {
            return new(
                IrCanonicalLoopStatusV1.BudgetExhausted,
                shapeDigest,
                HybridCpuTargetMachineContractV1.Default.ContractDigest,
                resourceModel.ModelDigest,
                Array.Empty<IrCanonicalLoopV1>(),
                [new("HCMI0002", "Loop count exceeds the deterministic production budget.")]);
        }

        IrProgramDependencyGraph dependencies = new HybridCpuProgramDependencyAnalyzer().AnalyzeProgram(program);
        List<IrCanonicalLoopV1> loops = new List<IrCanonicalLoopV1>(backedgeGroups.Length);
        foreach (IGrouping<int, IrControlFlowEdge> group in backedgeGroups)
        {
            loops.Add(BuildLoop(
                program,
                blocks,
                group.Key,
                group.Select(static edge => edge.SourceBlockId).Distinct().Order().ToArray(),
                dependencies,
                valueAnalysis,
                shapeDigest,
                resourceModel,
                budgets));
        }
        IrCanonicalLoopStatusV1 status = loops.Any(static loop => loop.Status == IrCanonicalLoopStatusV1.BudgetExhausted)
            ? IrCanonicalLoopStatusV1.BudgetExhausted
            : loops.Any(static loop => loop.Status == IrCanonicalLoopStatusV1.Unsupported)
                ? IrCanonicalLoopStatusV1.Unsupported
                : loops.Any(static loop => loop.Status == IrCanonicalLoopStatusV1.Unknown)
                    ? IrCanonicalLoopStatusV1.Unknown
                    : IrCanonicalLoopStatusV1.Qualified;
        bool irreducibleCycle = HasUnrepresentedCyclicScc(program, loops);
        if (irreducibleCycle) status = IrCanonicalLoopStatusV1.Unsupported;
        return new(
            status,
            shapeDigest,
            HybridCpuTargetMachineContractV1.Default.ContractDigest,
            resourceModel.ModelDigest,
            loops.ToArray(),
            irreducibleCycle
                ? [new("HCMI0008", "Irreducible or unrepresented cyclic control flow is unsupported.")]
                : Array.Empty<IrRegionSchedulingDiagnosticV1>());
    }

    public IrLoopMiiReportV1 ComputeMii(
        IrProgram program,
        IrCanonicalLoopV1 loop,
        HybridCpuMiiResourceModelV1? resourceModel = null,
        string? profileDigest = null,
        HybridCpuLoopMiiBudgetsV1? budgets = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(loop);
        resourceModel ??= HybridCpuMiiResourceModelV1.Default;
        budgets ??= HybridCpuLoopMiiBudgetsV1.Production;
        ValidateBudgets(budgets);
        if (!HasValidModelDigest(resourceModel))
            return RejectedReport(loop, resourceModel, profileDigest, IrLoopMiiEligibilityV1.IneligibleUnknown,
                "HCMI0006", "MII resource-model digest does not bind its capacities.");
        if (profileDigest is not null && !IsSha256(profileDigest))
            return RejectedReport(loop, resourceModel, profileDigest, IrLoopMiiEligibilityV1.IneligibleUnknown,
                "HCMI0003", "Profile identity must be a lowercase SHA-256 digest.");

        IrLoopCanonicalizationResultV1 canonicalization = Canonicalize(program, resourceModel, budgets);
        IrCanonicalLoopV1? canonicalLoop = canonicalization.Loops.SingleOrDefault(candidate =>
            candidate.HeaderBlockId == loop.HeaderBlockId &&
            candidate.BlockIds.SequenceEqual(loop.BlockIds) &&
            candidate.LatchBlockIds.SequenceEqual(loop.LatchBlockIds));
        if (canonicalLoop is null ||
            !string.Equals(loop.SchemaId, canonicalLoop.SchemaId, StringComparison.Ordinal) ||
            !string.Equals(loop.LoopId, canonicalLoop.LoopId, StringComparison.Ordinal) ||
            !string.Equals(loop.VersionStamp, canonicalLoop.VersionStamp, StringComparison.Ordinal) ||
            !string.Equals(loop.ProgramVersionStamp, canonicalLoop.ProgramVersionStamp, StringComparison.Ordinal) ||
            !string.Equals(loop.InstructionDigest, canonicalLoop.InstructionDigest, StringComparison.Ordinal) ||
            !string.Equals(loop.DistanceDagDigest, canonicalLoop.DistanceDagDigest, StringComparison.Ordinal))
            return RejectedReport(loop, resourceModel, profileDigest, IrLoopMiiEligibilityV1.StaleProof,
                "HCMI0004", "Loop distance/MII subject is not the canonical derivation for this program and resource model.");

        string expectedProgramVersion = ComputeVersionStamp(
            ComputeProgramShapeDigest(program),
            loop.BlockIds,
            program.Contract.DerivedFacts.ProgramMutation,
            resourceModel.ModelDigest);
        string expectedDistanceDigest = ComputeDistanceDagDigest(loop.DistanceDependencies);
        string expectedInstructionDigest = ComputeInstructionDigest(program.BasicBlocks
            .Where(block => loop.BlockIds.Contains(block.Id))
            .SelectMany(static block => block.Instructions)
            .OrderBy(static instruction => instruction.Index).ToArray());
        string expectedVersion = HybridCpuLoopMiiContractV1.Hash(
            $"hybridcpu.loop-version/v1|{expectedProgramVersion}|{expectedInstructionDigest}|{expectedDistanceDigest}");
        string expectedLoopId = HybridCpuLoopMiiContractV1.Hash(string.Join('|',
            HybridCpuLoopMiiContractV1.SchemaId,
            expectedVersion,
            loop.HeaderBlockId.ToString(CultureInfo.InvariantCulture),
            string.Join(',', loop.LatchBlockIds),
            string.Join(',', loop.BlockIds)));
        if (loop.MutationStamp != program.Contract.DerivedFacts.ProgramMutation ||
            !string.Equals(loop.ProgramVersionStamp, expectedProgramVersion, StringComparison.Ordinal) ||
            !string.Equals(loop.InstructionDigest, expectedInstructionDigest, StringComparison.Ordinal) ||
            !string.Equals(loop.DistanceDagDigest, expectedDistanceDigest, StringComparison.Ordinal) ||
            !string.Equals(loop.VersionStamp, expectedVersion, StringComparison.Ordinal) ||
            !string.Equals(loop.LoopId, expectedLoopId, StringComparison.Ordinal))
            return RejectedReport(loop, resourceModel, profileDigest, IrLoopMiiEligibilityV1.StaleProof,
                "HCMI0004", "Loop distance/MII subject is stale for the program or resource model.");
        if (loop.Status != IrCanonicalLoopStatusV1.Qualified)
        {
            IrLoopMiiEligibilityV1 eligibility = loop.Status == IrCanonicalLoopStatusV1.BudgetExhausted
                ? IrLoopMiiEligibilityV1.BudgetExhausted
                : loop.Status == IrCanonicalLoopStatusV1.Unknown
                    ? IrLoopMiiEligibilityV1.IneligibleUnknown
                    : IrLoopMiiEligibilityV1.IneligibleUnsupported;
            return RejectedReport(loop, resourceModel, profileDigest, eligibility,
                "HCMI0005", loop.Reason);
        }

        IrValueAnalysisReportV1 currentValueAnalysis = new HybridCpuValueLivenessPressureAnalyzerV1().Analyze(program);
        if (currentValueAnalysis.Status != IrValueAnalysisStatus.Complete ||
            !string.Equals(currentValueAnalysis.ValueFlowDigest, loop.ValueAnalysis.ValueFlowDigest, StringComparison.Ordinal))
            return RejectedReport(loop, resourceModel, profileDigest, IrLoopMiiEligibilityV1.StaleProof,
                "HCMI0007", "Loop value/liveness/pressure proof is stale.");
        IrCanonicalLoopV1 currentLoop = canonicalLoop with { ValueAnalysis = currentValueAnalysis };
        IrMiiComponentResultV1[] components =
        [
            ComputeRecMii(currentLoop, resourceModel, budgets.MaximumEnumeratedCycles, budgets.MaximumRecurrenceRelaxations),
            ComputeSlotMii(currentLoop, resourceModel),
            ComputePrfMii(currentLoop, resourceModel, writes: false),
            ComputePrfMii(currentLoop, resourceModel, writes: true),
            ComputeRegisterGroupMii(currentLoop, resourceModel),
            ComputeMemoryMii(currentLoop, resourceModel, banks: true),
            ComputeMemoryMii(currentLoop, resourceModel, banks: false),
            ComputeLaneMii(currentLoop, resourceModel, lane6: true),
            ComputeLaneMii(currentLoop, resourceModel, lane6: false),
            ComputeCertificateMii(currentLoop, resourceModel)
        ];
        IrLoopMiiEligibilityV1 resultStatus = components.Any(static component =>
            component.Status == IrMiiComponentStatusV1.Unsupported)
            ? IrLoopMiiEligibilityV1.IneligibleUnsupported
            : components.Any(static component => component.Status == IrMiiComponentStatusV1.Unknown)
                ? IrLoopMiiEligibilityV1.IneligibleUnknown
                : IrLoopMiiEligibilityV1.EligibleLowerBound;
        int? lowerBound = resultStatus == IrLoopMiiEligibilityV1.EligibleLowerBound
            ? components.Where(static component => component.Status == IrMiiComponentStatusV1.Proven)
                .Max(static component => component.Value!.Value)
            : null;
        return new(
            resultStatus,
            lowerBound,
            components,
            CreateProofStamp(currentLoop, resourceModel, profileDigest, components),
            Diagnostics(components));
    }

    private static IrCanonicalLoopV1 BuildLoop(
        IrProgram program,
        IReadOnlyDictionary<int, IrBasicBlock> blocks,
        int headerId,
        IReadOnlyList<int> latchIds,
        IrProgramDependencyGraph dependencies,
        IrValueAnalysisReportV1 valueAnalysis,
        string shapeDigest,
        HybridCpuMiiResourceModelV1 resourceModel,
        HybridCpuLoopMiiBudgetsV1 budgets)
    {
        SortedSet<int> loopBlocks = NaturalLoopBlocks(blocks, headerId, latchIds);
        int[] outsideHeaderPredecessors = blocks[headerId].PredecessorBlockIds
            .Where(id => !loopBlocks.Contains(id)).Order().ToArray();
        int[] exits = program.ControlFlowGraph.Edges
            .Where(edge => loopBlocks.Contains(edge.SourceBlockId) && !loopBlocks.Contains(edge.TargetBlockId))
            .Select(static edge => edge.TargetBlockId).Distinct().Order().ToArray();
        IrCanonicalLoopStatusV1 status = IrCanonicalLoopStatusV1.Qualified;
        string reason = "qualified-natural-loop";
        if (loopBlocks.Count > budgets.MaximumBlocksPerLoop)
            (status, reason) = (IrCanonicalLoopStatusV1.BudgetExhausted, "loop-block-budget-exhausted");
        else if (latchIds.Count != 1 || outsideHeaderPredecessors.Length != 1)
            (status, reason) = (IrCanonicalLoopStatusV1.Unsupported, "loop-is-not-canonical-single-preheader-single-latch");
        else if (HasExternalNonHeaderEntry(program, loopBlocks, headerId))
            (status, reason) = (IrCanonicalLoopStatusV1.Unsupported, "irreducible-or-multiple-entry-loop");
        else if (exits.Length > 1)
            (status, reason) = (IrCanonicalLoopStatusV1.Unsupported, "unsupported-multiple-side-exits");

        IrInstruction[] instructions = loopBlocks.SelectMany(id => blocks[id].Instructions)
            .OrderBy(static instruction => instruction.Index).ToArray();
        if (status == IrCanonicalLoopStatusV1.Qualified)
        {
            (IrCanonicalLoopStatusV1 Status, string Reason)? effectDisposition = EffectDisposition(instructions);
            if (effectDisposition.HasValue)
                (status, reason) = effectDisposition.Value;
        }

        IrLoopPhiIncomingV1[] phi = program.ValueFlow.Accesses
            .Where(access => access.Kind == IrValueAccessKind.PhiEdgeUse &&
                access.PhiTargetBlockId == headerId && access.PhiSourceBlockId.HasValue)
            .OrderBy(static access => access.ValueId, StringComparer.Ordinal)
            .ThenBy(static access => access.PhiSourceBlockId)
            .Select(static access => new IrLoopPhiIncomingV1(
                access.ValueId,
                access.PhiSourceBlockId!.Value,
                access.PhiTargetBlockId!.Value,
                access.InstructionIndex))
            .ToArray();
        // Distance/MII evidence is meaningful only for a loop that has passed the
        // canonical-shape and effect gates.  Building a potentially dense distance
        // graph for an already rejected loop cannot qualify it and used to let a
        // secondary edge budget overwrite the exact fail-closed rejection reason.
        IrLoopDistanceDependencyV1[] distanceEdges = [];
        if (status == IrCanonicalLoopStatusV1.Qualified)
        {
            distanceEdges = BuildDistanceEdges(
                program, loopBlocks, headerId, latchIds, dependencies, phi,
                budgets.MaximumDistanceEdges, out bool distanceBudgetExhausted);
            if (distanceBudgetExhausted)
                (status, reason) = (IrCanonicalLoopStatusV1.BudgetExhausted, "distance-edge-budget-exhausted");
            else if (HasZeroDistanceCycle(distanceEdges))
                (status, reason) = (IrCanonicalLoopStatusV1.Unsupported, "cyclic-zero-distance-dependency");
        }
        if (status == IrCanonicalLoopStatusV1.Qualified &&
            distanceEdges.Any(static edge => edge.Precision == IrLoopProofPrecisionV1.Unknown))
            (status, reason) = (IrCanonicalLoopStatusV1.Unknown, "unknown-loop-carried-memory-relation");

        int[] blockIds = loopBlocks.ToArray();
        string programVersionStamp = ComputeVersionStamp(
            shapeDigest,
            blockIds,
            program.Contract.DerivedFacts.ProgramMutation,
            resourceModel.ModelDigest);
        string distanceDagDigest = ComputeDistanceDagDigest(distanceEdges);
        string instructionDigest = ComputeInstructionDigest(instructions);
        string versionStamp = HybridCpuLoopMiiContractV1.Hash(
            $"hybridcpu.loop-version/v1|{programVersionStamp}|{instructionDigest}|{distanceDagDigest}");
        string loopId = HybridCpuLoopMiiContractV1.Hash(string.Join('|',
            HybridCpuLoopMiiContractV1.SchemaId,
            versionStamp,
            headerId.ToString(CultureInfo.InvariantCulture),
            string.Join(',', latchIds),
            string.Join(',', blockIds)));
        return new(
            HybridCpuLoopMiiContractV1.SchemaId,
            loopId,
            versionStamp,
            programVersionStamp,
            instructionDigest,
            distanceDagDigest,
            program.Contract.DerivedFacts.ProgramMutation,
            outsideHeaderPredecessors.Length == 1 ? outsideHeaderPredecessors[0] : -1,
            headerId,
            latchIds,
            exits,
            blockIds,
            instructions,
            phi,
            distanceEdges,
            valueAnalysis,
            status,
            reason);
    }

    private static IrLoopDistanceDependencyV1[] BuildDistanceEdges(
        IrProgram program,
        IReadOnlySet<int> loopBlocks,
        int headerId,
        IReadOnlyList<int> latchIds,
        IrProgramDependencyGraph dependencies,
        IReadOnlyList<IrLoopPhiIncomingV1> phi,
        int maximumEdges,
        out bool budgetExhausted)
    {
        Dictionary<string, IrLoopDistanceDependencyV1> result = new Dictionary<string, IrLoopDistanceDependencyV1>(StringComparer.Ordinal);
        budgetExhausted = false;
        foreach (IrBasicBlockDependencyGraph graph in dependencies.BlockGraphs.Where(graph => loopBlocks.Contains(graph.BlockId)))
        {
            foreach (IrInstructionDependency dependency in graph.Dependencies)
            {
                AddDistance(result, dependency, 0, IrLoopProofPrecisionV1.Exact, "intra-iteration-dependency");
                if (result.Count > maximumEdges)
                {
                    budgetExhausted = true;
                    return OrderedEdges();
                }
            }
        }
        foreach (IrInterBlockDependency dependency in dependencies.InterBlockGraph.Dependencies.Where(item =>
                     loopBlocks.Contains(item.SourceBlockId) && loopBlocks.Contains(item.TargetBlockId)))
        {
            int distance = latchIds.Contains(dependency.SourceBlockId) && dependency.TargetBlockId == headerId ? 1 : 0;
            AddDistance(result, dependency.Dependency, distance, IrLoopProofPrecisionV1.Exact,
                distance == 0 ? "intra-iteration-cfg-edge" : "canonical-backedge");
            if (result.Count > maximumEdges)
            {
                budgetExhausted = true;
                return OrderedEdges();
            }
        }
        foreach (IrLoopPhiIncomingV1 incoming in phi.Where(item => latchIds.Contains(item.SourceBlockId)))
        {
            int? producer = program.ValueFlow.Accesses
                .Where(access => access.ValueId == incoming.ValueId && access.Kind == IrValueAccessKind.Def)
                .Where(access => program.Instructions.Any(instruction => instruction.Index == access.InstructionIndex))
                .Select(static access => (int?)access.InstructionIndex)
                .LastOrDefault();
            if (producer.HasValue)
            {
                IrInstruction producerInstruction = program.Instructions.Single(instruction => instruction.Index == producer.Value);
                AddDistance(result, new(
                    IrInstructionDependencyKind.RegisterRaw,
                    producer.Value,
                    incoming.InstructionIndex,
                    producerInstruction.Annotation.MinimumLatencyCycles,
                    IrOperandKind.VirtualValue),
                    1,
                    IrLoopProofPrecisionV1.Exact,
                    $"phi-backedge:{incoming.ValueId}");
                if (result.Count > maximumEdges)
                {
                    budgetExhausted = true;
                    return OrderedEdges();
                }
            }
        }

        IrInstruction[] memory = loopBlocks.SelectMany(id => program.BasicBlocks.Single(block => block.Id == id).Instructions)
            .Where(static instruction => instruction.SideEffects.Memory.Kind != IrMemoryEffectKind.None)
            .OrderBy(static instruction => instruction.Index).ToArray();
        foreach (IrInstruction producer in memory)
        {
            foreach (IrInstruction consumer in memory)
            {
                IrRegionMemoryRelationV1 relation = HybridCpuRegionSchedulerV1.ClassifyMemoryRelation(
                    producer.SideEffects.Memory, consumer.SideEffects.Memory);
                if (relation == IrRegionMemoryRelationV1.NoAlias) continue;
                IrLoopProofPrecisionV1 precision = relation switch
                {
                    IrRegionMemoryRelationV1.MustAlias => IrLoopProofPrecisionV1.Exact,
                    IrRegionMemoryRelationV1.MayAlias => IrLoopProofPrecisionV1.Conservative,
                    _ => IrLoopProofPrecisionV1.Unknown
                };
                AddDistance(result, new(
                    IrInstructionDependencyKind.Memory,
                    producer.Index,
                    consumer.Index,
                    1,
                    MemoryPrecision: relation == IrRegionMemoryRelationV1.MustAlias
                        ? IrMemoryDependencyPrecision.Must : IrMemoryDependencyPrecision.May,
                    DominantEffectKind: IrHazardEffectKind.MemoryBank),
                    1,
                    precision,
                    $"memory-carried:{relation}");
                if (result.Count > maximumEdges)
                {
                    budgetExhausted = true;
                    return OrderedEdges();
                }
            }
        }
        return OrderedEdges();

        IrLoopDistanceDependencyV1[] OrderedEdges() => result.Values.OrderBy(static edge => edge.ProducerInstructionIndex)
            .ThenBy(static edge => edge.ConsumerInstructionIndex)
            .ThenBy(static edge => edge.IterationDistance)
            .ThenBy(static edge => edge.Kind)
            .ToArray();
    }

    private static void AddDistance(
        IDictionary<string, IrLoopDistanceDependencyV1> result,
        IrInstructionDependency dependency,
        int distance,
        IrLoopProofPrecisionV1 precision,
        string reason)
    {
        string key = string.Join(':',
            dependency.ProducerInstructionIndex,
            dependency.ConsumerInstructionIndex,
            distance,
            dependency.Kind,
            dependency.MinimumLatencyCycles,
            precision);
        string edgeId = HybridCpuLoopMiiContractV1.Hash($"hybridcpu.loop-distance-edge/v1|{key}|{reason}");
        result[key] = new(
            edgeId,
            dependency.ProducerInstructionIndex,
            dependency.ConsumerInstructionIndex,
            distance,
            Math.Max(1, (int)dependency.MinimumLatencyCycles),
            dependency.Kind,
            precision,
            reason);
    }

    private static IrMiiComponentResultV1 ComputeRecMii(
        IrCanonicalLoopV1 loop,
        HybridCpuMiiResourceModelV1 model,
        int maximumCycles, int maximumRelaxations)
    {
        if (loop.DistanceDependencies.Any(static edge => edge.IterationDistance < 0))
            return Component(loop, model, IrMiiComponentKindV1.RecMii, IrMiiComponentStatusV1.Unsupported,
                null, 0, null, IrLoopProofPrecisionV1.Unknown, ["negative-distance"], "negative-iteration-distance");
        if (loop.DistanceDependencies.Any(static edge => edge.IterationDistance == 0 &&
            edge.ProducerInstructionIndex == edge.ConsumerInstructionIndex))
            return Component(loop, model, IrMiiComponentKindV1.RecMii, IrMiiComponentStatusV1.Unsupported,
                null, 0, null, IrLoopProofPrecisionV1.Unknown, ["zero-distance-self-cycle"], "cyclic-zero-distance-dependency");
        if (!loop.DistanceDependencies.Any(static edge => edge.IterationDistance > 0))
            return NotApplicable(loop, model, IrMiiComponentKindV1.RecMii, "no-loop-carried-dependence");

        CycleEnumeration cycles = EnumerateCycles(loop.DistanceDependencies, maximumCycles);
        if (cycles.BudgetExhausted)
            cycles = SolveRecurrenceRatio(loop.DistanceDependencies, maximumRelaxations);
        if (cycles.BudgetExhausted)
            return Component(loop, model, IrMiiComponentKindV1.RecMii, IrMiiComponentStatusV1.Unknown,
                null, cycles.MaximumNumerator, null, IrLoopProofPrecisionV1.Unknown,
                cycles.Contributors, cycles.FailureReason ?? "recurrence-cycle-budget-exhausted");
        if (cycles.MaximumIi == 0)
            return NotApplicable(loop, model, IrMiiComponentKindV1.RecMii, "no-distance-cycle");
        return Component(loop, model, IrMiiComponentKindV1.RecMii, IrMiiComponentStatusV1.Proven,
            cycles.MaximumIi, cycles.MaximumNumerator, cycles.MaximumDistance,
            cycles.Precision, cycles.Contributors, "max-cycle-ceil-sum-latency-over-sum-distance");
    }

    private static IrMiiComponentResultV1 ComputeSlotMii(IrCanonicalLoopV1 loop, HybridCpuMiiResourceModelV1 model)
    {
        int issue = Ceiling(loop.Instructions.Count, HybridCpuSlotModel.SlotCount);
        Dictionary<string, (int Count, int Capacity)> demands = new Dictionary<string, (int Count, int Capacity)>(StringComparer.Ordinal)
        {
            ["issue-width"] = (loop.Instructions.Count, HybridCpuSlotModel.SlotCount),
            ["alu-class"] = (loop.Instructions.Count(static instruction => instruction.Annotation.RequiredSlotClass == IrSlotClass.AluClass), 4),
            ["lsu-class"] = (loop.Instructions.Count(static instruction => instruction.Annotation.RequiredSlotClass == IrSlotClass.LsuClass), 2),
            ["lane6-class"] = (loop.Instructions.Count(static instruction => instruction.Annotation.RequiredSlotClass is IrSlotClass.DmaStreamClass or IrSlotClass.MatrixTileStreamClass), 1),
            ["lane7-class"] = (loop.Instructions.Count(static instruction => instruction.Annotation.RequiredSlotClass is IrSlotClass.BranchControl or IrSlotClass.SystemSingleton), 1)
        };
        int value = Math.Max(issue, demands.Values.Max(static demand => Ceiling(demand.Count, demand.Capacity)));
        string[] contributors = demands.Where(pair => Ceiling(pair.Value.Count, pair.Value.Capacity) == value)
            .Select(pair => $"{pair.Key}:{pair.Value.Count}/{pair.Value.Capacity}").Order().ToArray();
        return Component(loop, model, IrMiiComponentKindV1.SlotMii, IrMiiComponentStatusV1.Proven,
            Math.Max(1, value), loop.Instructions.Count, HybridCpuSlotModel.SlotCount,
            IrLoopProofPrecisionV1.Exact, contributors, "w8-shared-slot-domain-demand");
    }

    private static IrMiiComponentResultV1 ComputePrfMii(
        IrCanonicalLoopV1 loop,
        HybridCpuMiiResourceModelV1 model,
        bool writes)
    {
        int demand = loop.Instructions.Sum(instruction => (writes ? instruction.Annotation.Defs : instruction.Annotation.Uses)
            .Count(static operand => operand.Kind is IrOperandKind.ArchitecturalRegister or IrOperandKind.Pointer or IrOperandKind.VirtualValue));
        IrMiiComponentKindV1 kind = writes ? IrMiiComponentKindV1.PrfWritePortMii : IrMiiComponentKindV1.PrfReadPortMii;
        if (demand == 0) return NotApplicable(loop, model, kind, "no-prf-port-demand");
        int? capacity = writes ? model.Topology.PrfWritePortCapacity : model.Topology.PrfReadPortCapacity;
        if (!capacity.HasValue)
            return Component(loop, model, kind, IrMiiComponentStatusV1.Unknown, null, demand, null,
                IrLoopProofPrecisionV1.Unknown, [$"accesses:{demand}"], "unknown-prf-port-capacity");
        return Component(loop, model, kind, IrMiiComponentStatusV1.Proven,
            Ceiling(demand, capacity.Value), demand, capacity, IrLoopProofPrecisionV1.Exact,
            [$"accesses:{demand}"], "static-prf-port-demand");
    }

    private static IrMiiComponentResultV1 ComputeRegisterGroupMii(
        IrCanonicalLoopV1 loop,
        HybridCpuMiiResourceModelV1 model)
    {
        IrBlockPressureV1[] pressure = loop.ValueAnalysis.Pressure
            .Where(item => loop.BlockIds.Contains(item.BlockId)).ToArray();
        IrRegisterGroupPressureV1[] groups = pressure.SelectMany(static item => item.RegisterGroups).ToArray();
        if (groups.Length == 0)
            return NotApplicable(loop, model, IrMiiComponentKindV1.RegisterGroupMii, "no-register-group-pressure");
        if (groups.Any(static group => group.IsPossibleRatherThanAssigned))
            return Component(loop, model, IrMiiComponentKindV1.RegisterGroupMii, IrMiiComponentStatusV1.Unknown,
                null, groups.Max(static group => group.PeakLiveValues), HybridCpuMachineTopologyV1.RegistersPerGroup,
                IrLoopProofPrecisionV1.Unknown,
                groups.Where(static group => group.IsPossibleRatherThanAssigned)
                    .Select(static group => $"group:{group.RegisterGroup}:possible").Order().ToArray(),
                "possible-register-group-is-not-a-proven-assignment");
        int peak = groups.Max(static group => group.PeakLiveValues);
        return Component(loop, model, IrMiiComponentKindV1.RegisterGroupMii, IrMiiComponentStatusV1.Proven,
            Math.Max(1, Ceiling(peak, HybridCpuMachineTopologyV1.RegistersPerGroup)),
            peak, HybridCpuMachineTopologyV1.RegistersPerGroup, IrLoopProofPrecisionV1.Exact,
            groups.Where(group => group.PeakLiveValues == peak)
                .Select(static group => $"group:{group.RegisterGroup}:peak:{group.PeakLiveValues}").Order().ToArray(),
            "phase07-exact-group-pressure");
    }

    private static IrMiiComponentResultV1 ComputeMemoryMii(
        IrCanonicalLoopV1 loop,
        HybridCpuMiiResourceModelV1 model,
        bool banks)
    {
        IrMiiComponentKindV1 kind = banks ? IrMiiComponentKindV1.MemoryBankMii : IrMiiComponentKindV1.MemoryChannelMii;
        IrInstruction[] memoryInstructions = loop.Instructions.Where(static instruction =>
            instruction.Annotation.MemoryReadRegion is not null || instruction.Annotation.MemoryWriteRegion is not null).ToArray();
        if (memoryInstructions.Length == 0) return NotApplicable(loop, model, kind, "no-memory-demand");
        IrAddressResourceEvidenceV1[] evidence = memoryInstructions
            .Select(instruction => IrTopologyResourceFootprintBuilderV1.Build(instruction, model.Topology))
            .Select(footprint => banks ? footprint.Banks : footprint.Channels).ToArray();
        if (evidence.Any(static item => item.Precision != IrAddressEvidenceKindV1.Exact || item.ResourceIds.Count != 1))
            return Component(loop, model, kind, IrMiiComponentStatusV1.Unknown, null, memoryInstructions.Length, null,
                IrLoopProofPrecisionV1.Unknown,
                evidence.Select(static item => $"{item.Precision}:{string.Join(',', item.ResourceIds)}").Order().ToArray(),
                banks ? "memory-bank-mapping-not-exact" : "memory-channel-mapping-not-exact");
        int peak = evidence.SelectMany(static item => item.ResourceIds).GroupBy(static id => id).Max(static group => group.Count());
        return Component(loop, model, kind, IrMiiComponentStatusV1.Proven, peak, peak, 1,
            IrLoopProofPrecisionV1.Exact,
            evidence.SelectMany(static item => item.ResourceIds).GroupBy(static id => id)
                .Where(group => group.Count() == peak).Select(group => $"resource:{group.Key}:demand:{peak}").Order().ToArray(),
            banks ? "exact-static-bank-demand" : "exact-static-channel-demand");
    }

    private static IrMiiComponentResultV1 ComputeLaneMii(
        IrCanonicalLoopV1 loop,
        HybridCpuMiiResourceModelV1 model,
        bool lane6)
    {
        IrMiiComponentKindV1 kind = lane6 ? IrMiiComponentKindV1.Lane6Mii : IrMiiComponentKindV1.Lane7Mii;
        IrInstruction[] demand = loop.Instructions.Where(instruction => lane6
            ? instruction.Annotation.RequiredSlotClass is IrSlotClass.DmaStreamClass or IrSlotClass.MatrixTileStreamClass
            : instruction.Annotation.RequiredSlotClass is IrSlotClass.BranchControl or IrSlotClass.SystemSingleton).ToArray();
        if (demand.Length == 0) return NotApplicable(loop, model, kind, lane6 ? "no-lane6-demand" : "no-lane7-demand");
        return Component(loop, model, kind, IrMiiComponentStatusV1.Proven, demand.Length, demand.Length, 1,
            IrLoopProofPrecisionV1.Exact,
            demand.Select(static instruction => instruction.StableIdentity).Order().ToArray(),
            lane6 ? "lane6-special-contour-capacity" : "lane7-shared-control-system-capacity");
    }

    private static IrMiiComponentResultV1 ComputeCertificateMii(
        IrCanonicalLoopV1 loop,
        HybridCpuMiiResourceModelV1 model)
    {
        IrInstruction[] demand = loop.Instructions.Where(static instruction =>
            instruction.Annotation.RequiredSlotClass is IrSlotClass.DmaStreamClass or IrSlotClass.MatrixTileStreamClass or
                IrSlotClass.BranchControl or IrSlotClass.SystemSingleton).ToArray();
        if (demand.Length == 0)
            return NotApplicable(loop, model, IrMiiComponentKindV1.CertificateMii, "no-structural-certificate-demand");
        if (!model.StructuralCertificateCapacity.HasValue)
            return Component(loop, model, IrMiiComponentKindV1.CertificateMii, IrMiiComponentStatusV1.Unknown,
                null, demand.Length, null, IrLoopProofPrecisionV1.Unknown,
                demand.Select(static instruction => instruction.StableIdentity).Order().ToArray(),
                "unknown-compiler-structural-certificate-capacity");
        return Component(loop, model, IrMiiComponentKindV1.CertificateMii, IrMiiComponentStatusV1.Proven,
            Ceiling(demand.Length, model.StructuralCertificateCapacity.Value),
            demand.Length, model.StructuralCertificateCapacity, IrLoopProofPrecisionV1.Exact,
            demand.Select(static instruction => instruction.StableIdentity).Order().ToArray(),
            "compiler-structural-certificate-capacity-not-runtime-token");
    }

    private static CycleEnumeration EnumerateCycles(
        IReadOnlyList<IrLoopDistanceDependencyV1> edges,
        int maximumCycles)
    {
        // Parallel dependencies with identical endpoints and distance describe the same
        // recurrence step. The largest latency dominates every cycle using a weaker edge:
        // distance is unchanged and cycle latency cannot decrease. Keep a real edge as
        // witness, with deterministic tie breaking; original distance DAG remains intact.
        IrLoopDistanceDependencyV1[] recurrenceEdges = edges.GroupBy(static edge => (edge.ProducerInstructionIndex,
                edge.ConsumerInstructionIndex, edge.IterationDistance, edge.Precision))
            .Select(static group => group.OrderByDescending(static edge => edge.LatencyCycles)
                .ThenBy(static edge => edge.EdgeId, StringComparer.Ordinal).First()).ToArray();
        Dictionary<int, IrLoopDistanceDependencyV1[]> outgoing = recurrenceEdges.GroupBy(static edge => edge.ProducerInstructionIndex)
            .ToDictionary(static group => group.Key, group => group.OrderBy(static edge => edge.ConsumerInstructionIndex)
                .ThenBy(static edge => edge.EdgeId, StringComparer.Ordinal).ToArray());
        int[] nodes = edges.SelectMany(static edge => new[] { edge.ProducerInstructionIndex, edge.ConsumerInstructionIndex })
            .Distinct().Order().ToArray();
        int count = 0, maximumIi = 0, maximumNumerator = 0, maximumDistance = 0;
        IrLoopProofPrecisionV1 precision = IrLoopProofPrecisionV1.Exact;
        SortedSet<string> contributors = new SortedSet<string>(StringComparer.Ordinal);
        foreach (int start in nodes)
        {
            HashSet<int> visited = new HashSet<int> { start };
            List<IrLoopDistanceDependencyV1> path = new List<IrLoopDistanceDependencyV1>();
            Visit(start, start, visited, path);
            if (count > maximumCycles) break;
        }
        return new(count > maximumCycles, maximumIi, maximumNumerator, maximumDistance, precision, contributors.ToArray());

        void Visit(
            int start,
            int current,
            HashSet<int> visited,
            List<IrLoopDistanceDependencyV1> path)
        {
            if (!outgoing.TryGetValue(current, out IrLoopDistanceDependencyV1[]? nextEdges)) return;
            foreach (IrLoopDistanceDependencyV1 edge in nextEdges)
            {
                if (edge.ConsumerInstructionIndex < start) continue;
                if (edge.ConsumerInstructionIndex == start)
                {
                    count++;
                    if (count > maximumCycles) return;
                    int numerator = path.Sum(static item => item.LatencyCycles) + edge.LatencyCycles;
                    int distance = path.Sum(static item => item.IterationDistance) + edge.IterationDistance;
                    if (distance <= 0) continue;
                    int ii = Ceiling(numerator, distance);
                    if (ii > maximumIi)
                    {
                        maximumIi = ii;
                        maximumNumerator = numerator;
                        maximumDistance = distance;
                        precision = path.Append(edge).Any(static item => item.Precision != IrLoopProofPrecisionV1.Exact)
                            ? IrLoopProofPrecisionV1.Conservative : IrLoopProofPrecisionV1.Exact;
                        contributors.Clear();
                    }
                    if (ii == maximumIi)
                        contributors.Add(string.Join(',', path.Append(edge).Select(static item => item.EdgeId)));
                    continue;
                }
                if (!visited.Add(edge.ConsumerInstructionIndex)) continue;
                path.Add(edge);
                Visit(start, edge.ConsumerInstructionIndex, visited, path);
                path.RemoveAt(path.Count - 1);
                visited.Remove(edge.ConsumerInstructionIndex);
                if (count > maximumCycles) return;
            }
        }
    }

    private static IrLoopMiiReportV1 RejectedReport(
        IrCanonicalLoopV1 loop,
        HybridCpuMiiResourceModelV1 model,
        string? profileDigest,
        IrLoopMiiEligibilityV1 eligibility,
        string code,
        string reason)
    {
        IrMiiComponentStatusV1 componentStatus = eligibility == IrLoopMiiEligibilityV1.IneligibleUnknown
            ? IrMiiComponentStatusV1.Unknown : IrMiiComponentStatusV1.Unsupported;
        IrMiiComponentResultV1[] components = Enum.GetValues<IrMiiComponentKindV1>()
            .Select(kind => Component(loop, model, kind, componentStatus, null, 0, null,
                IrLoopProofPrecisionV1.Unknown, Array.Empty<string>(), reason)).ToArray();
        return new(
            eligibility,
            null,
            components,
            CreateProofStamp(loop, model, profileDigest, components),
            [new(code, reason)]);
    }

    private static IrLoopMiiProofStampV1 CreateProofStamp(
        IrCanonicalLoopV1 loop,
        HybridCpuMiiResourceModelV1 model,
        string? profileDigest,
        IReadOnlyList<IrMiiComponentResultV1> components)
    {
        string input = string.Join('|',
            "hybridcpu.loop-mii-proof/v1",
            loop.LoopId,
            loop.VersionStamp,
            loop.MutationStamp.Value.ToString(CultureInfo.InvariantCulture),
            HybridCpuTargetMachineContractV1.Default.ContractDigest,
            model.ModelDigest,
            HybridCpuLoopMiiContractV1.Default.ContractDigest,
            profileDigest ?? "absent",
            string.Join(';', components.Select(static component => string.Join(':',
                component.Component,
                component.Status,
                component.Value?.ToString(CultureInfo.InvariantCulture) ?? "none",
                component.Numerator,
                component.Capacity?.ToString(CultureInfo.InvariantCulture) ?? "none",
                component.Precision,
                string.Join(',', component.Contributors)))));
        return new(
            "hybridcpu.loop-mii-proof/v1",
            loop.LoopId,
            loop.VersionStamp,
            loop.MutationStamp,
            HybridCpuTargetMachineContractV1.Default.ContractDigest,
            model.ModelDigest,
            HybridCpuLoopMiiContractV1.Default.ContractDigest,
            profileDigest ?? "absent",
            HybridCpuLoopMiiContractV1.Hash(input));
    }

    private static IrMiiComponentResultV1 Component(
        IrCanonicalLoopV1 loop,
        HybridCpuMiiResourceModelV1 model,
        IrMiiComponentKindV1 kind,
        IrMiiComponentStatusV1 status,
        int? value,
        int numerator,
        int? capacity,
        IrLoopProofPrecisionV1 precision,
        IReadOnlyList<string> contributors,
        string reason) => new(
            kind,
            status,
            value,
            numerator,
            capacity,
            precision,
            contributors,
            reason,
            HybridCpuTargetMachineContractV1.Default.ContractDigest,
            model.ModelDigest);

    private static IrMiiComponentResultV1 NotApplicable(
        IrCanonicalLoopV1 loop,
        HybridCpuMiiResourceModelV1 model,
        IrMiiComponentKindV1 kind,
        string reason) => Component(loop, model, kind, IrMiiComponentStatusV1.NotApplicable,
            null, 0, null, IrLoopProofPrecisionV1.Exact, Array.Empty<string>(), reason);

    private static IrRegionSchedulingDiagnosticV1[] Diagnostics(IReadOnlyList<IrMiiComponentResultV1> components) =>
        components.Where(static component => component.Status is IrMiiComponentStatusV1.Unknown or IrMiiComponentStatusV1.Unsupported)
            .Select(component => new IrRegionSchedulingDiagnosticV1(
                component.Status == IrMiiComponentStatusV1.Unknown ? "HCMI1001" : "HCMI1002",
                $"{component.Component}: {component.Reason}; numerator={component.Numerator}; capacity={component.Capacity?.ToString(CultureInfo.InvariantCulture) ?? "Unknown"}."))
            .ToArray();

    private static SortedSet<int> NaturalLoopBlocks(
        IReadOnlyDictionary<int, IrBasicBlock> blocks,
        int headerId,
        IReadOnlyList<int> latchIds)
    {
        SortedSet<int> result = new SortedSet<int> { headerId };
        Stack<int> stack = new Stack<int>();
        foreach (int latch in latchIds)
        {
            if (result.Add(latch)) stack.Push(latch);
        }
        while (stack.Count > 0)
        {
            int blockId = stack.Pop();
            foreach (int predecessor in blocks[blockId].PredecessorBlockIds.OrderByDescending(static id => id))
            {
                if (result.Add(predecessor) && predecessor != headerId) stack.Push(predecessor);
            }
        }
        return result;
    }

    private static Dictionary<int, HashSet<int>> ComputeDominators(IReadOnlyDictionary<int, IrBasicBlock> blocks)
    {
        int[] all = blocks.Keys.Order().ToArray();
        Dictionary<int, HashSet<int>> result = new Dictionary<int, HashSet<int>>();
        foreach (IrBasicBlock block in blocks.Values)
            result[block.Id] = block.PredecessorBlockIds.Count == 0 ? [block.Id] : new HashSet<int>(all);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (IrBasicBlock block in blocks.Values.OrderBy(static block => block.Id))
            {
                if (block.PredecessorBlockIds.Count == 0) continue;
                HashSet<int> next = new HashSet<int>(result[block.PredecessorBlockIds[0]]);
                foreach (int predecessor in block.PredecessorBlockIds.Skip(1)) next.IntersectWith(result[predecessor]);
                next.Add(block.Id);
                if (next.SetEquals(result[block.Id])) continue;
                result[block.Id] = next;
                changed = true;
            }
        }
        return result;
    }

    private static bool HasExternalNonHeaderEntry(IrProgram program, IReadOnlySet<int> loopBlocks, int headerId) =>
        program.ControlFlowGraph.Edges.Any(edge => !loopBlocks.Contains(edge.SourceBlockId) &&
            loopBlocks.Contains(edge.TargetBlockId) && edge.TargetBlockId != headerId);

    private static bool HasUnrepresentedCyclicScc(
        IrProgram program,
        IReadOnlyList<IrCanonicalLoopV1> loops)
    {
        Dictionary<int, int[]> outgoing = program.BasicBlocks.ToDictionary(
            static block => block.Id,
            block => program.ControlFlowGraph.Edges.Where(edge => edge.SourceBlockId == block.Id)
                .Select(static edge => edge.TargetBlockId).Distinct().Order().ToArray());
        foreach (int start in outgoing.Keys.Order())
        {
            HashSet<int> reachable = Reachable(start, outgoing);
            int[] component = reachable.Where(candidate => Reachable(candidate, outgoing).Contains(start))
                .Order().ToArray();
            bool cyclic = component.Length > 1 || outgoing[start].Contains(start);
            if (!cyclic) continue;
            if (!loops.Any(loop => component.All(loop.BlockIds.Contains))) return true;
        }
        return false;
    }

    private static HashSet<int> Reachable(int start, IReadOnlyDictionary<int, int[]> outgoing)
    {
        HashSet<int> result = new HashSet<int>();
        Stack<int> stack = new Stack<int>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            int current = stack.Pop();
            if (!result.Add(current)) continue;
            if (!outgoing.TryGetValue(current, out int[]? targets)) continue;
            foreach (int target in targets) stack.Push(target);
        }
        return result;
    }

    private static (IrCanonicalLoopStatusV1 Status, string Reason)? EffectDisposition(
        IReadOnlyList<IrInstruction> instructions)
    {
        foreach (IrInstruction instruction in instructions)
        {
            IrMemoryEffectKind memory = instruction.SideEffects.Memory.Kind;
            if (memory.HasFlag(IrMemoryEffectKind.Atomic))
                return (IrCanonicalLoopStatusV1.Unsupported, "unsupported-loop-atomic");
            if (memory.HasFlag(IrMemoryEffectKind.Volatile))
                return (IrCanonicalLoopStatusV1.Unsupported, "unsupported-loop-volatile");
            if (memory.HasFlag(IrMemoryEffectKind.Fence))
                return (IrCanonicalLoopStatusV1.Unsupported, "unsupported-loop-fence");
            IrArchitecturalEffectKind effects = instruction.SideEffects.ArchitecturalEffects;
            if (effects.HasFlag(IrArchitecturalEffectKind.Call))
                return (IrCanonicalLoopStatusV1.Unsupported, "unsupported-loop-call");
            if (effects.HasFlag(IrArchitecturalEffectKind.TrapOrFault) || instruction.Semantics.OperationMayFault)
                return (IrCanonicalLoopStatusV1.Unsupported, "unsupported-loop-exception-or-fault");
            if (effects.HasFlag(IrArchitecturalEffectKind.Unknown))
                return (IrCanonicalLoopStatusV1.Unknown, "unknown-loop-side-effect");
        }
        return null;
    }

    private static string ComputeProgramShapeDigest(IrProgram program)
    {
        StringBuilder builder = new StringBuilder("hybridcpu.loop-program-shape/v1")
            .Append('|').Append(program.VirtualThreadId)
            .Append('|').Append(program.Contract.DerivedFacts.ProgramMutation.Value);
        foreach (IrBasicBlock block in program.BasicBlocks.OrderBy(static block => block.Id))
        {
            builder.Append("|b:").Append(block.Id)
                .Append(':').AppendJoin(',', block.Instructions.Select(InstructionKey))
                .Append(':').AppendJoin(',', block.PredecessorBlockIds.Order())
                .Append(':').AppendJoin(',', block.SuccessorBlockIds.Order());
        }
        foreach (IrControlFlowEdge edge in program.ControlFlowGraph.Edges.OrderBy(static edge => edge.SourceBlockId)
                     .ThenBy(static edge => edge.TargetBlockId).ThenBy(static edge => edge.Kind))
            builder.Append("|e:").Append(edge.SourceBlockId).Append(':').Append(edge.TargetBlockId).Append(':').Append(edge.Kind);
        foreach (IrValueAccessV1 access in program.ValueFlow.Accesses.OrderBy(static access => access.ValueId, StringComparer.Ordinal)
                     .ThenBy(static access => access.InstructionIndex).ThenBy(static access => access.Kind))
            builder.Append("|v:").Append(access.ValueId).Append(':').Append(access.InstructionIndex).Append(':')
                .Append(access.Kind).Append(':').Append(access.PhiSourceBlockId).Append(':').Append(access.PhiTargetBlockId);
        foreach (IrVirtualValueV1 value in program.ValueFlow.Values.OrderBy(static value => value.StableId, StringComparer.Ordinal))
            builder.Append("|d:").Append(value.StableId).Append(':').Append(value.ValueKind.Kind).Append(':')
                .Append(value.ValueKind.BitWidth).Append(':').Append(value.VirtualClass).Append(':')
                .Append(value.Allocation.FixedRegisterId).Append(':').Append(value.Allocation.IsAllocatable).Append(':')
                .AppendJoin(',', value.Allocation.LegalRegisterGroups.Order()).Append(':')
                .Append(value.Allocation.TargetContractDigest);
        return HybridCpuLoopMiiContractV1.Hash(builder.ToString());
    }

    private static string ComputeVersionStamp(
        string shapeDigest,
        IReadOnlyList<int> blockIds,
        IrMutationStamp mutation,
        string resourceModelDigest) => HybridCpuLoopMiiContractV1.Hash(string.Join('|',
            "hybridcpu.loop-proof-version/v1",
            shapeDigest,
            string.Join(',', blockIds),
            mutation.Value.ToString(CultureInfo.InvariantCulture),
            HybridCpuTargetMachineContractV1.Default.ContractDigest,
            resourceModelDigest));

    private static string ComputeDistanceDagDigest(IReadOnlyList<IrLoopDistanceDependencyV1> edges) =>
        HybridCpuLoopMiiContractV1.Hash(string.Join('|',
            "hybridcpu.loop-distance-dag/v1",
            string.Join(';', edges.OrderBy(static edge => edge.ProducerInstructionIndex)
                .ThenBy(static edge => edge.ConsumerInstructionIndex)
                .ThenBy(static edge => edge.IterationDistance)
                .ThenBy(static edge => edge.Kind)
                .Select(static edge => string.Join(':',
                    edge.EdgeId,
                    edge.ProducerInstructionIndex,
                    edge.ConsumerInstructionIndex,
                    edge.IterationDistance,
                    edge.LatencyCycles,
                    edge.Kind,
                    edge.Precision,
                    edge.ProofReason)))));

    private static string ComputeInstructionDigest(IReadOnlyList<IrInstruction> instructions) =>
        HybridCpuLoopMiiContractV1.Hash(string.Join('|',
            "hybridcpu.loop-instructions/v1",
            string.Join(';', instructions.OrderBy(static instruction => instruction.Index).Select(InstructionKey))));

    private static string InstructionKey(IrInstruction instruction) => string.Join(':',
        instruction.Index,
        instruction.StableIdentity,
        instruction.Opcode,
        instruction.DataType,
        instruction.PredicateMask,
        instruction.Annotation.RequiredSlotClass,
        instruction.Annotation.StructurallyAllowedSlots,
        instruction.Annotation.MinimumLatencyCycles,
        instruction.Annotation.Serialization,
        instruction.Annotation.ControlFlowKind,
        instruction.Semantics.OperationMayFault,
        instruction.SideEffects.Memory.Kind,
        instruction.SideEffects.Memory.AddressSpace,
        instruction.SideEffects.Memory.Ordering,
        RegionKey(instruction.SideEffects.Memory.ReadRegion),
        RegionKey(instruction.SideEffects.Memory.WriteRegion),
        instruction.SideEffects.ArchitecturalEffects,
        string.Join(',', instruction.Annotation.Defs.Select(static operand => $"{operand.Kind}-{operand.Value}")),
        string.Join(',', instruction.Annotation.Uses.Select(static operand => $"{operand.Kind}-{operand.Value}")));

    private static string RegionKey(IrMemoryRegion? region) => region is null
        ? "none"
        : $"{region.Address.ToString(CultureInfo.InvariantCulture)}-{region.Length.ToString(CultureInfo.InvariantCulture)}-{region.IsWrite}";

    private static int Ceiling(int numerator, int denominator) =>
        numerator == 0 ? 0 : checked((numerator + denominator - 1) / denominator);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HasValidModelDigest(HybridCpuMiiResourceModelV1 model) =>
        string.Equals(
            model.ModelDigest,
            HybridCpuMiiResourceModelV1.Create(model.Topology, model.StructuralCertificateCapacity).ModelDigest,
            StringComparison.Ordinal);

    private static void ValidateBudgets(HybridCpuLoopMiiBudgetsV1 budgets)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        if (budgets.MaximumLoops <= 0 || budgets.MaximumBlocksPerLoop <= 0 ||
            budgets.MaximumDistanceEdges <= 0 || budgets.MaximumEnumeratedCycles <= 0 || budgets.MaximumRecurrenceRelaxations < 0)
            throw new ArgumentOutOfRangeException(nameof(budgets));
    }

    private sealed record CycleEnumeration(
        bool BudgetExhausted,
        int MaximumIi,
        int MaximumNumerator,
        int MaximumDistance,
        IrLoopProofPrecisionV1 Precision,
        IReadOnlyList<string> Contributors)
    {
        public string? FailureReason { get; init; }
    }
}
