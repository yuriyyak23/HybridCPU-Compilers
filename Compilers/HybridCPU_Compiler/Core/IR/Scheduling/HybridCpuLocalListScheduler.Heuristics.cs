using System;
using System.Collections.Generic;
using System.Numerics;
using HybridCPU.Compiler.Core.IR.Resources;

namespace HybridCPU.Compiler.Core.IR
{
    public sealed partial class HybridCpuLocalListScheduler
    {
        private CycleGroupBuildResult BuildCycleGroup(
            IReadOnlyList<IrSchedulingNode> readyNodes,
            IReadOnlySet<int> unscheduledInstructionIndexes,
            IReadOnlyDictionary<int, int> remainingDependencyCounts,
            IReadOnlyDictionary<int, int> readyCycles,
            IReadOnlyDictionary<int, IrSchedulingNode> nodesByInstructionIndex,
            IReadOnlyDictionary<int, int> transitiveSuccessorCounts,
            int currentCycle)
        {
            if (!UseJointCycleComposition)
            {
                return new CycleGroupBuildResult(
                    BuildGreedyCycleGroup(
                        readyNodes,
                        unscheduledInstructionIndexes,
                        remainingDependencyCounts,
                        readyCycles,
                        nodesByInstructionIndex,
                        transitiveSuccessorCounts,
                        currentCycle),
                    null);
            }

            List<IrSchedulingNode> existingPath = BuildGreedyCycleGroup(
                readyNodes,
                unscheduledInstructionIndexes,
                remainingDependencyCounts,
                readyCycles,
                nodesByInstructionIndex,
                transitiveSuccessorCounts,
                currentCycle);
            if (existingPath.Count >= GetJointMembershipWidthUpperBound(readyNodes))
            {
                MetricsCollector?.RecordFallback("JointCycleComposition:ExistingPathAtWidthUpperBound");
                return new CycleGroupBuildResult(existingPath, null);
            }

            IrCycleMembershipCandidate[] candidates = new IrCycleMembershipCandidate[readyNodes.Count];
            for (int index = 0; index < readyNodes.Count; index++)
            {
                IrSchedulingNode node = readyNodes[index];
                candidates[index] = new IrCycleMembershipCandidate(
                    node.InstructionIndex,
                    node.Instruction,
                    node.CriticalPathLengthCycles,
                    GetTransitiveSuccessorCount(node.InstructionIndex, transitiveSuccessorCounts));
            }

            IrCycleSearchResult searchResult = _cycleGroupSearch.Search(new IrCycleSearchRequest(
                candidates,
                JointCycleSearchOptions,
                HybridCpuMachineDescriptionV1.Default.Key));
            if (searchResult.Summary.ExistingPathFallbackRequired || searchResult.ChosenMembers.Count == 0)
            {
                MetricsCollector?.RecordFallback($"JointCycleComposition:{searchResult.Summary.FallbackReason}");
                return new CycleGroupBuildResult(existingPath, null);
            }

            // The existing path may discover zero-latency successors during this cycle. The
            // initial ready-window search cannot claim an improvement unless its complete,
            // validated membership is strictly wider than that fully formed baseline group.
            if (searchResult.ChosenMembers.Count < existingPath.Count)
            {
                MetricsCollector?.RecordFallback("JointCycleComposition:ExistingPathNotWorse");
                return new CycleGroupBuildResult(existingPath, null);
            }

            List<IrSchedulingNode> chosenNodes = new List<IrSchedulingNode>(searchResult.ChosenMembers.Count);
            foreach (IrCycleMembershipCandidate member in searchResult.ChosenMembers)
            {
                chosenNodes.Add(nodesByInstructionIndex[member.InstructionIndex]);
            }

            return new CycleGroupBuildResult(chosenNodes, searchResult);
        }

        private static int GetJointMembershipWidthUpperBound(IReadOnlyList<IrSchedulingNode> readyNodes)
        {
            int issueBound = Math.Min(HybridCpuMachineDescriptionV1.Default.Width, readyNodes.Count);
            HashSet<IrSlotClass> observedClasses = new HashSet<IrSlotClass>();
            int classCapacityBound = 0;
            bool hasUnknownClass = false;
            foreach (IrSchedulingNode node in readyNodes)
            {
                IrSlotClass slotClass = node.Instruction.Annotation.RequiredSlotClass;
                if (!observedClasses.Add(slotClass)) continue;
                if (!HybridCpuMachineDescriptionV1.Default.TryGetSlotClass(
                        slotClass,
                        out HybridCpuSlotClassTopologyV1 topology) ||
                    !topology.CountedByLegacyCompiler)
                {
                    hasUnknownClass = true;
                    break;
                }

                classCapacityBound += topology.Capacity;
            }

            return hasUnknownClass
                ? issueBound
                : Math.Min(issueBound, classCapacityBound);
        }

        private List<IrSchedulingNode> BuildGreedyCycleGroup(
            IReadOnlyList<IrSchedulingNode> readyNodes,
            IReadOnlySet<int> unscheduledInstructionIndexes,
            IReadOnlyDictionary<int, int> remainingDependencyCounts,
            IReadOnlyDictionary<int, int> readyCycles,
            IReadOnlyDictionary<int, IrSchedulingNode> nodesByInstructionIndex,
            IReadOnlyDictionary<int, int> transitiveSuccessorCounts,
            int currentCycle)
        {
            List<IrSchedulingNode> scheduledThisCycle = new List<IrSchedulingNode>(readyNodes.Count);
            List<IrInstruction> cycleInstructions = new List<IrInstruction>(readyNodes.Count);
            List<IrSchedulingNode> remainingReadyNodes = new List<IrSchedulingNode>(readyNodes);

            while (remainingReadyNodes.Count > 0)
            {
                if (scheduledThisCycle.Count > 0 && ShouldReserveSlack(cycleInstructions, remainingReadyNodes))
                {
                    MetricsCollector?.RecordFallback("SchedulerSlackReservation");
                    break;
                }

                if (scheduledThisCycle.Count > 0 && ShouldStopForLoadClusteringWindow(cycleInstructions, remainingReadyNodes))
                {
                    MetricsCollector?.RecordFallback("SchedulerLoadClusteringWindow");
                    break;
                }

                IrSchedulingNode? selectedCandidate = SelectBestCycleCandidate(
                    remainingReadyNodes,
                    cycleInstructions,
                    unscheduledInstructionIndexes,
                    remainingDependencyCounts,
                    nodesByInstructionIndex,
                    transitiveSuccessorCounts);

                if (selectedCandidate is null)
                {
                    break;
                }

                scheduledThisCycle.Add(selectedCandidate);
                cycleInstructions.Add(selectedCandidate.Instruction);
                remainingReadyNodes.Remove(selectedCandidate);
                AddSameCycleReadyNodes(
                    remainingReadyNodes,
                    cycleInstructions,
                    unscheduledInstructionIndexes,
                    remainingDependencyCounts,
                    readyCycles,
                    nodesByInstructionIndex,
                    currentCycle);
            }

            return scheduledThisCycle;
        }

        private sealed record CycleGroupBuildResult(
            IReadOnlyList<IrSchedulingNode> Nodes,
            IrCycleSearchResult? SearchResult);

        private IrSchedulingNode? SelectBestCycleCandidate(
            IReadOnlyList<IrSchedulingNode> readyNodes,
            IReadOnlyList<IrInstruction> scheduledCycleInstructions,
            IReadOnlySet<int> unscheduledInstructionIndexes,
            IReadOnlyDictionary<int, int> remainingDependencyCounts,
            IReadOnlyDictionary<int, IrSchedulingNode> nodesByInstructionIndex,
            IReadOnlyDictionary<int, int> transitiveSuccessorCounts)
        {
            IrSchedulingNode? bestCandidate = null;
            ReadyNodePriority bestPriority = default;
            int decision = ProfileDecisionTrace?.BeginDecision() ?? 0;
            IReadOnlyDictionary<int, int> currentCycleSatisfiedDependencyCounts = BuildCurrentCycleSatisfiedDependencyCounts(
                scheduledCycleInstructions,
                nodesByInstructionIndex);

            for (int index = 0; index < readyNodes.Count; index++)
            {
                IrSchedulingNode candidate = readyNodes[index];
                bool structurallyPruned = !CanAddToCycle(scheduledCycleInstructions, candidate.Instruction);
                MetricsCollector?.RecordSchedulerCandidate(structurallyPruned);
                if (structurallyPruned)
                {
                    ProfileDecisionTrace?.Record(decision, "candidate", candidate.Instruction.Index,
                        scheduledCycleInstructions.Count, false, null);
                    continue;
                }

                ReadyNodePriority candidatePriority = CreateCycleCandidatePriority(
                    candidate,
                    readyNodes,
                    scheduledCycleInstructions,
                    unscheduledInstructionIndexes,
                    remainingDependencyCounts,
                    currentCycleSatisfiedDependencyCounts,
                    nodesByInstructionIndex,
                    transitiveSuccessorCounts);

                ProfileDecisionTrace?.Record(decision, "candidate", candidate.Instruction.Index,
                    scheduledCycleInstructions.Count, true, candidatePriority.ClassPressurePenalty);

                if (bestCandidate is null || CompareCycleCandidatePriorities(candidatePriority, bestPriority) < 0)
                {
                    bestCandidate = candidate;
                    bestPriority = candidatePriority;
                }
            }

            if (bestCandidate is not null)
                ProfileDecisionTrace?.Record(decision, "selection", bestCandidate.Instruction.Index,
                    scheduledCycleInstructions.Count, true, bestPriority.ClassPressurePenalty);
            return bestCandidate;
        }

        private static Dictionary<int, IrSchedulingNode> BuildNodeMap(IrBasicBlockSchedulingDag dag)
        {
            Dictionary<int, IrSchedulingNode> nodesByInstructionIndex = new Dictionary<int, IrSchedulingNode>(dag.Nodes.Count);
            foreach (IrSchedulingNode node in dag.Nodes)
            {
                nodesByInstructionIndex[node.InstructionIndex] = node;
            }

            return nodesByInstructionIndex;
        }

        private static Dictionary<int, int> BuildTransitiveSuccessorCounts(
            IrBasicBlockSchedulingDag dag,
            IReadOnlyDictionary<int, IrSchedulingNode> nodesByInstructionIndex)
        {
            return BuildExactSuccessorCounts(dag, out _, out _);
        }

        // Exact reverse-topological bitset union, never a priority approximation.
        // Storage: V * ceil(V/64) words + O(V). Work: O(V+E) validation/topology,
        // E membership checks, at most E * ceil(V/64) unions and V * ceil(V/64)
        // popcounts. No recursion and no per-root graph traversal. A successor already
        // in the row has had its entire closure included, so its union is redundant.
        // Counters expose actual bounded work to regression tests without a time limit.
        private static Dictionary<int, int> BuildExactSuccessorCounts(
            IrBasicBlockSchedulingDag dag, out long edgeVisits, out long wordUnions)
        {
            int count = dag.Nodes.Count;
            Dictionary<int, int> positions = new Dictionary<int, int>(count);
            int[] indegrees = new int[count];
            for (int index = 0; index < count; index++)
            {
                if (!positions.TryAdd(dag.Nodes[index].InstructionIndex, index))
                    throw new InvalidOperationException($"Scheduling DAG {dag.BlockId} has duplicate instruction indexes.");
            }
            edgeVisits = 0;
            wordUnions = 0;
            foreach (IrSchedulingNode node in dag.Nodes)
            {
                foreach (IrInstructionDependency edge in node.OutgoingDependencies)
                {
                    edgeVisits++;
                    if (edge.ProducerInstructionIndex != node.InstructionIndex ||
                        !positions.TryGetValue(edge.ConsumerInstructionIndex, out int consumer))
                        throw new InvalidOperationException($"Scheduling DAG {dag.BlockId} has an invalid dependency edge.");
                    indegrees[consumer] = checked(indegrees[consumer] + 1);
                }
            }
            // FIFO, seeded in input order; no dictionary enumeration affects topology.
            int[] order = new int[count];
            int end = 0;
            for (int index = 0; index < count; index++)
                if (indegrees[index] == 0) order[end++] = index;
            for (int next = 0; next < end; next++)
            {
                foreach (IrInstructionDependency edge in dag.Nodes[order[next]].OutgoingDependencies)
                {
                    edgeVisits++;
                    int consumer = positions[edge.ConsumerInstructionIndex];
                    if (--indegrees[consumer] == 0) order[end++] = consumer;
                }
            }
            if (end != count)
            {
                int[] cyclic = Enumerable.Range(0, count).Where(index => indegrees[index] != 0).Take(16).ToArray();
                HashSet<int> cyclicInstructions = cyclic.Select(index => dag.Nodes[index].InstructionIndex).ToHashSet();
                string nodes = string.Join(',', cyclic.Select(index =>
                    $"{dag.Nodes[index].InstructionIndex}:{dag.Nodes[index].Instruction.StableIdentity}:indegree={indegrees[index]}"));
                string edges = string.Join(',', cyclic.SelectMany(index => dag.Nodes[index].OutgoingDependencies)
                    .Where(edge => cyclicInstructions.Contains(edge.ConsumerInstructionIndex)).Take(32)
                    .Select(edge => $"{edge.ProducerInstructionIndex}->{edge.ConsumerInstructionIndex}:{edge.Kind}"));
                throw new InvalidOperationException(
                    $"Scheduling DAG {dag.BlockId} contains a dependency cycle; nodes={nodes}; internalEdges={edges}.");
            }

            int words = (int)(((long)count + 63) / 64);
            ulong[][] closures = new ulong[count][];
            Dictionary<int, int> result = new Dictionary<int, int>(count);
            for (int next = count - 1; next >= 0; next--)
            {
                int position = order[next];
                IrSchedulingNode node = dag.Nodes[position];
                ulong[] row = new ulong[words];
                closures[position] = row;
                foreach (IrInstructionDependency edge in node.OutgoingDependencies)
                {
                    edgeVisits++;
                    int consumer = positions[edge.ConsumerInstructionIndex];
                    ulong bit = 1UL << (consumer & 63);
                    if ((row[consumer >> 6] & bit) != 0) continue;
                    row[consumer >> 6] |= bit;
                    ulong[] successor = closures[consumer];
                    for (int word = 0; word < words; word++)
                    {
                        row[word] |= successor[word];
                        wordUnions++;
                    }
                }
                int descendants = 0;
                foreach (ulong word in row) descendants += BitOperations.PopCount(word);
                result.Add(node.InstructionIndex, descendants);
            }
            return result;
        }

        private int CompareReadyNodes(
            IrSchedulingNode left,
            IrSchedulingNode right,
            IReadOnlySet<int> unscheduledInstructionIndexes,
            IReadOnlyDictionary<int, int> remainingDependencyCounts,
            IReadOnlyDictionary<int, IrSchedulingNode> nodesByInstructionIndex,
            IReadOnlyDictionary<int, int> transitiveSuccessorCounts)
        {
            ReadyNodePriority leftPriority = CreateReadyNodePriority(
                left,
                unscheduledInstructionIndexes,
                remainingDependencyCounts,
                null,
                nodesByInstructionIndex,
                transitiveSuccessorCounts);
            ReadyNodePriority rightPriority = CreateReadyNodePriority(
                right,
                unscheduledInstructionIndexes,
                remainingDependencyCounts,
                null,
                nodesByInstructionIndex,
                transitiveSuccessorCounts);

            return CompareReadyNodePriorities(leftPriority, rightPriority);
        }

        private ReadyNodePriority CreateReadyNodePriority(
            IrSchedulingNode node,
            IReadOnlySet<int> unscheduledInstructionIndexes,
            IReadOnlyDictionary<int, int> remainingDependencyCounts,
            IReadOnlyDictionary<int, int>? currentCycleSatisfiedDependencyCounts,
            IReadOnlyDictionary<int, IrSchedulingNode> nodesByInstructionIndex,
            IReadOnlyDictionary<int, int> transitiveSuccessorCounts)
        {
            return new ReadyNodePriority(
                CriticalPathLengthCycles: node.CriticalPathLengthCycles,
                SuccessorPressureScore: CalculateSuccessorPressureScore(node, unscheduledInstructionIndexes, remainingDependencyCounts, currentCycleSatisfiedDependencyCounts, nodesByInstructionIndex),
                TypedEffectScore: GetTypedEffectScore(node),
                SameCycleReadySuccessorScore: CalculateSameCycleReadySuccessorScore(node, unscheduledInstructionIndexes, remainingDependencyCounts, currentCycleSatisfiedDependencyCounts, nodesByInstructionIndex),
                ReadySuccessorScore: CalculateReadySuccessorScore(node, unscheduledInstructionIndexes, remainingDependencyCounts, currentCycleSatisfiedDependencyCounts, nodesByInstructionIndex),
                TransitiveSuccessorCount: GetTransitiveSuccessorCount(node.InstructionIndex, transitiveSuccessorCounts),
                MinimumLatencyCycles: node.Instruction.Annotation.MinimumLatencyCycles,
                BankAwareHintPenalty: GetBankAwareHintPenalty(node.Instruction, []),
                SlotFlexibility: GetSlotFlexibility(node.Instruction.Annotation.StructurallyAllowedSlots),
                PostFspScore: GetPostFspScore(node.Instruction),
                LoopPhaseSelectionScore: GetLoopPhaseSelectionScore(node.Instruction, []),
                VtAwareBackendPressureScore: GetVtAwareBackendPressureScore(node.Instruction),
                FlexibilityScore: GetFlexibilityTieBreakScore(node.Instruction),
                PinnedLaneChokePenalty: GetPinnedLaneChokePenalty(node.Instruction, []),
                SameCycleRawChainPenalty: 0,
                LoopNormalizationPenalty: 0,
                LoadClusteringPenalty: 0,
                CompatibilityCount: 0,
                ClassPressurePenalty: 0,
                InstructionIndex: node.Instruction.Index);
        }

        private ReadyNodePriority CreateCycleCandidatePriority(
            IrSchedulingNode candidate,
            IReadOnlyList<IrSchedulingNode> readyNodes,
            IReadOnlyList<IrInstruction> scheduledCycleInstructions,
            IReadOnlySet<int> unscheduledInstructionIndexes,
            IReadOnlyDictionary<int, int> remainingDependencyCounts,
            IReadOnlyDictionary<int, int> currentCycleSatisfiedDependencyCounts,
            IReadOnlyDictionary<int, IrSchedulingNode> nodesByInstructionIndex,
            IReadOnlyDictionary<int, int> transitiveSuccessorCounts)
        {
            ReadyNodePriority basePriority = CreateReadyNodePriority(
                candidate,
                unscheduledInstructionIndexes,
                remainingDependencyCounts,
                currentCycleSatisfiedDependencyCounts,
                nodesByInstructionIndex,
                transitiveSuccessorCounts);

            return basePriority with
            {
                BankAwareHintPenalty = basePriority.BankAwareHintPenalty + GetBankAwareHintPenalty(candidate.Instruction, scheduledCycleInstructions),
                PostFspScore = basePriority.PostFspScore + GetPostFspScore(candidate.Instruction, scheduledCycleInstructions),
                LoopPhaseSelectionScore = basePriority.LoopPhaseSelectionScore + GetLoopPhaseSelectionScore(candidate.Instruction, scheduledCycleInstructions),
                FlexibilityScore = GetFlexibilityTieBreakScore(candidate.Instruction),
                PinnedLaneChokePenalty = GetPinnedLaneChokePenalty(candidate.Instruction, scheduledCycleInstructions),
                SameCycleRawChainPenalty = GetSameCycleRawChainPenalty(candidate.Instruction, scheduledCycleInstructions),
                LoopNormalizationPenalty = GetLoopNormalizationPenalty(candidate.Instruction, scheduledCycleInstructions),
                LoadClusteringPenalty = GetLoadClusteringPenalty(candidate.Instruction, scheduledCycleInstructions),
                CompatibilityCount = CalculateCompatibilityCount(candidate, readyNodes, scheduledCycleInstructions),
                ClassPressurePenalty = GetCycleClassPressurePenalty(candidate.Instruction, scheduledCycleInstructions)
            };
        }

        private int CalculateCompatibilityCount(
            IrSchedulingNode candidate,
            IReadOnlyList<IrSchedulingNode> readyNodes,
            IReadOnlyList<IrInstruction> scheduledCycleInstructions)
        {
            List<IrInstruction> candidateCycleInstructions = new List<IrInstruction>(scheduledCycleInstructions.Count + 1);
            for (int index = 0; index < scheduledCycleInstructions.Count; index++)
            {
                candidateCycleInstructions.Add(scheduledCycleInstructions[index]);
            }

            candidateCycleInstructions.Add(candidate.Instruction);
            int compatibilityCount = 0;

            for (int index = 0; index < readyNodes.Count; index++)
            {
                IrSchedulingNode other = readyNodes[index];
                if (other.InstructionIndex == candidate.InstructionIndex)
                {
                    continue;
                }

                if (CanAddToCycle(candidateCycleInstructions, other.Instruction))
                {
                    compatibilityCount++;
                }
            }

            return compatibilityCount;
        }

        private static int CalculateReadySuccessorScore(
            IrSchedulingNode node,
            IReadOnlySet<int> unscheduledInstructionIndexes,
            IReadOnlyDictionary<int, int> remainingDependencyCounts,
            IReadOnlyDictionary<int, int>? currentCycleSatisfiedDependencyCounts,
            IReadOnlyDictionary<int, IrSchedulingNode> nodesByInstructionIndex)
        {
            int readySuccessorScore = 0;

            foreach (IrInstructionDependency dependency in node.OutgoingDependencies)
            {
                if (!unscheduledInstructionIndexes.Contains(dependency.ConsumerInstructionIndex))
                {
                    continue;
                }

                int effectiveRemainingDependencyCount = GetEffectiveRemainingDependencyCount(
                    dependency.ConsumerInstructionIndex,
                    remainingDependencyCounts,
                    currentCycleSatisfiedDependencyCounts);
                if (effectiveRemainingDependencyCount != 1)
                {
                    continue;
                }

                int consumerCriticalPathLengthCycles = nodesByInstructionIndex.TryGetValue(dependency.ConsumerInstructionIndex, out IrSchedulingNode? consumerNode) && consumerNode is not null
                    ? consumerNode.CriticalPathLengthCycles
                    : 1;

                readySuccessorScore += dependency.MinimumLatencyCycles + consumerCriticalPathLengthCycles;
            }

            return readySuccessorScore;
        }

        private static int CalculateSameCycleReadySuccessorScore(
            IrSchedulingNode node,
            IReadOnlySet<int> unscheduledInstructionIndexes,
            IReadOnlyDictionary<int, int> remainingDependencyCounts,
            IReadOnlyDictionary<int, int>? currentCycleSatisfiedDependencyCounts,
            IReadOnlyDictionary<int, IrSchedulingNode> nodesByInstructionIndex)
        {
            if (currentCycleSatisfiedDependencyCounts is null || currentCycleSatisfiedDependencyCounts.Count == 0)
            {
                return 0;
            }

            int sameCycleReadySuccessorScore = 0;

            foreach (IrInstructionDependency dependency in node.OutgoingDependencies)
            {
                if (!unscheduledInstructionIndexes.Contains(dependency.ConsumerInstructionIndex) ||
                    !remainingDependencyCounts.TryGetValue(dependency.ConsumerInstructionIndex, out int originalRemainingDependencyCount) ||
                    originalRemainingDependencyCount <= 1)
                {
                    continue;
                }

                int effectiveRemainingDependencyCount = GetEffectiveRemainingDependencyCount(
                    dependency.ConsumerInstructionIndex,
                    remainingDependencyCounts,
                    currentCycleSatisfiedDependencyCounts);
                if (effectiveRemainingDependencyCount != 1)
                {
                    continue;
                }

                int consumerCriticalPathLengthCycles = nodesByInstructionIndex.TryGetValue(dependency.ConsumerInstructionIndex, out IrSchedulingNode? consumerNode) && consumerNode is not null
                    ? consumerNode.CriticalPathLengthCycles
                    : 1;

                sameCycleReadySuccessorScore += dependency.MinimumLatencyCycles + consumerCriticalPathLengthCycles;
            }

            return sameCycleReadySuccessorScore;
        }

        private static int CalculateSuccessorPressureScore(
            IrSchedulingNode node,
            IReadOnlySet<int> unscheduledInstructionIndexes,
            IReadOnlyDictionary<int, int> remainingDependencyCounts,
            IReadOnlyDictionary<int, int>? currentCycleSatisfiedDependencyCounts,
            IReadOnlyDictionary<int, IrSchedulingNode> nodesByInstructionIndex)
        {
            int successorPressureScore = 0;

            foreach (IrInstructionDependency dependency in node.OutgoingDependencies)
            {
                if (!unscheduledInstructionIndexes.Contains(dependency.ConsumerInstructionIndex))
                {
                    continue;
                }

                int effectiveRemainingDependencyCount = GetEffectiveRemainingDependencyCount(
                    dependency.ConsumerInstructionIndex,
                    remainingDependencyCounts,
                    currentCycleSatisfiedDependencyCounts);
                if (effectiveRemainingDependencyCount <= 0)
                {
                    continue;
                }

                int consumerCriticalPathLengthCycles = nodesByInstructionIndex.TryGetValue(dependency.ConsumerInstructionIndex, out IrSchedulingNode? consumerNode) && consumerNode is not null
                    ? consumerNode.CriticalPathLengthCycles
                    : 1;

                successorPressureScore += (dependency.MinimumLatencyCycles + consumerCriticalPathLengthCycles) * effectiveRemainingDependencyCount;
            }

            return successorPressureScore;
        }

        private static Dictionary<int, int> BuildCurrentCycleSatisfiedDependencyCounts(
            IReadOnlyList<IrInstruction> scheduledCycleInstructions,
            IReadOnlyDictionary<int, IrSchedulingNode> nodesByInstructionIndex)
        {
            Dictionary<int, int> satisfiedDependencyCounts = new Dictionary<int, int>();
            for (int index = 0; index < scheduledCycleInstructions.Count; index++)
            {
                IrInstruction instruction = scheduledCycleInstructions[index];
                if (!nodesByInstructionIndex.TryGetValue(instruction.Index, out IrSchedulingNode? node) || node is null)
                {
                    continue;
                }

                foreach (IrInstructionDependency dependency in node.OutgoingDependencies)
                {
                    if (dependency.MinimumLatencyCycles != 0)
                    {
                        continue;
                    }

                    if (!satisfiedDependencyCounts.TryAdd(dependency.ConsumerInstructionIndex, 1))
                    {
                        satisfiedDependencyCounts[dependency.ConsumerInstructionIndex]++;
                    }
                }
            }

            return satisfiedDependencyCounts;
        }

        private static int GetEffectiveRemainingDependencyCount(
            int consumerInstructionIndex,
            IReadOnlyDictionary<int, int> remainingDependencyCounts,
            IReadOnlyDictionary<int, int>? currentCycleSatisfiedDependencyCounts)
        {
            if (!remainingDependencyCounts.TryGetValue(consumerInstructionIndex, out int remainingDependencyCount))
            {
                return 0;
            }

            if (currentCycleSatisfiedDependencyCounts is null ||
                !currentCycleSatisfiedDependencyCounts.TryGetValue(consumerInstructionIndex, out int satisfiedDependencyCountInCurrentCycle))
            {
                return remainingDependencyCount;
            }

            return Math.Max(remainingDependencyCount - satisfiedDependencyCountInCurrentCycle, 0);
        }

        private static void AddSameCycleReadyNodes(
            ICollection<IrSchedulingNode> remainingReadyNodes,
            IReadOnlyList<IrInstruction> scheduledCycleInstructions,
            IReadOnlySet<int> unscheduledInstructionIndexes,
            IReadOnlyDictionary<int, int> remainingDependencyCounts,
            IReadOnlyDictionary<int, int> readyCycles,
            IReadOnlyDictionary<int, IrSchedulingNode> nodesByInstructionIndex,
            int currentCycle)
        {
            IReadOnlyDictionary<int, int> currentCycleSatisfiedDependencyCounts = BuildCurrentCycleSatisfiedDependencyCounts(
                scheduledCycleInstructions,
                nodesByInstructionIndex);

            if (currentCycleSatisfiedDependencyCounts.Count == 0)
            {
                return;
            }

            HashSet<int> alreadyReady = new HashSet<int>();
            foreach (IrSchedulingNode readyNode in remainingReadyNodes)
            {
                alreadyReady.Add(readyNode.InstructionIndex);
            }

            foreach (IrInstruction scheduledInstruction in scheduledCycleInstructions)
            {
                alreadyReady.Add(scheduledInstruction.Index);
            }

            foreach ((int instructionIndex, IrSchedulingNode node) in nodesByInstructionIndex)
            {
                if (!unscheduledInstructionIndexes.Contains(instructionIndex) ||
                    alreadyReady.Contains(instructionIndex))
                {
                    continue;
                }

                if (!readyCycles.TryGetValue(instructionIndex, out int readyCycle) ||
                    readyCycle > currentCycle)
                {
                    continue;
                }

                if (GetEffectiveRemainingDependencyCount(
                        instructionIndex,
                        remainingDependencyCounts,
                        currentCycleSatisfiedDependencyCounts) != 0)
                {
                    continue;
                }

                remainingReadyNodes.Add(node);
            }
        }

        private static int CompareReadyNodePriorities(ReadyNodePriority left, ReadyNodePriority right, bool includeFinalIndex = true)
        {
            int criticalPathComparison = right.CriticalPathLengthCycles.CompareTo(left.CriticalPathLengthCycles);
            if (criticalPathComparison != 0)
            {
                return criticalPathComparison;
            }

            int successorPressureComparison = right.SuccessorPressureScore.CompareTo(left.SuccessorPressureScore);
            if (successorPressureComparison != 0)
            {
                return successorPressureComparison;
            }

            int typedEffectComparison = right.TypedEffectScore.CompareTo(left.TypedEffectScore);
            if (typedEffectComparison != 0)
            {
                return typedEffectComparison;
            }

            int readySuccessorComparison = right.ReadySuccessorScore.CompareTo(left.ReadySuccessorScore);
            if (readySuccessorComparison != 0)
            {
                return readySuccessorComparison;
            }

            int transitiveSuccessorComparison = right.TransitiveSuccessorCount.CompareTo(left.TransitiveSuccessorCount);
            if (transitiveSuccessorComparison != 0)
            {
                return transitiveSuccessorComparison;
            }

            int latencyComparison = right.MinimumLatencyCycles.CompareTo(left.MinimumLatencyCycles);
            if (latencyComparison != 0)
            {
                return latencyComparison;
            }

            int bankAwareHintComparison = left.BankAwareHintPenalty.CompareTo(right.BankAwareHintPenalty);
            if (bankAwareHintComparison != 0)
            {
                return bankAwareHintComparison;
            }

            int postFspComparison = right.PostFspScore.CompareTo(left.PostFspScore);
            if (postFspComparison != 0)
            {
                return postFspComparison;
            }

            int loopPhaseComparison = right.LoopPhaseSelectionScore.CompareTo(left.LoopPhaseSelectionScore);
            if (loopPhaseComparison != 0)
            {
                return loopPhaseComparison;
            }

            int pinnedLaneComparison = left.PinnedLaneChokePenalty.CompareTo(right.PinnedLaneChokePenalty);
            if (pinnedLaneComparison != 0)
            {
                return pinnedLaneComparison;
            }

            int slotFlexibilityComparison = left.SlotFlexibility.CompareTo(right.SlotFlexibility);
            if (slotFlexibilityComparison != 0)
            {
                return slotFlexibilityComparison;
            }

            int vtAwareBackendPressureComparison = right.VtAwareBackendPressureScore.CompareTo(left.VtAwareBackendPressureScore);
            if (vtAwareBackendPressureComparison != 0)
            {
                return vtAwareBackendPressureComparison;
            }

            int stealabilityComparison = right.FlexibilityScore.CompareTo(left.FlexibilityScore);
            if (stealabilityComparison != 0)
            {
                return stealabilityComparison;
            }

            return includeFinalIndex ? left.InstructionIndex.CompareTo(right.InstructionIndex) : 0;
        }

        private int CompareCycleCandidatePriorities(ReadyNodePriority left, ReadyNodePriority right)
        {
            if (!UseClassPressureSmoothingTieBreaks)
            {
                int profilePenaltyComparison = left.ClassPressurePenalty.CompareTo(right.ClassPressurePenalty);
                if (profilePenaltyComparison != 0)
                {
                    return profilePenaltyComparison;
                }
            }

            int sameCycleReadySuccessorComparison = right.SameCycleReadySuccessorScore.CompareTo(left.SameCycleReadySuccessorScore);
            if (sameCycleReadySuccessorComparison != 0)
            {
                return sameCycleReadySuccessorComparison;
            }

            int sameCycleRawChainComparison = left.SameCycleRawChainPenalty.CompareTo(right.SameCycleRawChainPenalty);
            if (sameCycleRawChainComparison != 0)
            {
                return sameCycleRawChainComparison;
            }

            int loopNormalizationComparison = left.LoopNormalizationPenalty.CompareTo(right.LoopNormalizationPenalty);
            if (loopNormalizationComparison != 0)
            {
                return loopNormalizationComparison;
            }

            int loadClusteringComparison = left.LoadClusteringPenalty.CompareTo(right.LoadClusteringPenalty);
            if (loadClusteringComparison != 0)
            {
                return loadClusteringComparison;
            }

            // Smoothing must compare pressure before the final unique index; otherwise
            // distinct candidates never reach the advertised advisory tie-break.
            int readyNodeComparison = CompareReadyNodePriorities(left, right, includeFinalIndex: !UseClassPressureSmoothingTieBreaks);
            if (readyNodeComparison != 0)
            {
                return readyNodeComparison;
            }

            if (UseClassPressureSmoothingTieBreaks)
            {
                int pressureComparison = left.ClassPressurePenalty.CompareTo(right.ClassPressurePenalty);
                if (pressureComparison != 0)
                {
                    return pressureComparison;
                }
                // Equal (including profile-off zero) pressure retains the old index order.
                return left.InstructionIndex.CompareTo(right.InstructionIndex);
            }

            int compatibilityComparison = right.CompatibilityCount.CompareTo(left.CompatibilityCount);
            if (compatibilityComparison != 0)
            {
                return compatibilityComparison;
            }

            return left.InstructionIndex.CompareTo(right.InstructionIndex);
        }

        private static int GetSlotFlexibility(IrIssueSlotMask structurallyAllowedSlots)
        {
            uint slotMask = (uint)structurallyAllowedSlots;
            return BitOperations.PopCount(slotMask);
        }

        private static int GetTransitiveSuccessorCount(int instructionIndex, IReadOnlyDictionary<int, int> transitiveSuccessorCounts)
        {
            return transitiveSuccessorCounts.TryGetValue(instructionIndex, out int count) ? count : 0;
        }

        private int GetTypedEffectScore(IrSchedulingNode node)
        {
            if (!UseTypedEffectEdgeOrdering)
            {
                return 0;
            }

            int score = 0;
            foreach (IrInstructionDependency dependency in node.OutgoingDependencies)
            {
                score += dependency.DominantEffectKind switch
                {
                    IrHazardEffectKind.ControlFlow => 3,
                    IrHazardEffectKind.SystemBarrier => 3,
                    IrHazardEffectKind.PinnedLane => 2,
                    IrHazardEffectKind.MemoryBank => _currentBlockContext.CrossDomainRejectSignal > 0.0 ? 2 : 1,
                    IrHazardEffectKind.RegisterData => 1,
                    _ => 0
                };
            }

            return score;
        }

        private static int GetSameCycleRawChainPenalty(
            IrInstruction candidate,
            IReadOnlyList<IrInstruction> scheduledCycleInstructions)
        {
            if (!TryGetScalarRegisterAccess(candidate, out ushort candidateWriteRegister, out ushort candidateReadRegisterA, out ushort candidateReadRegisterB))
            {
                return 0;
            }

            int penalty = 0;
            for (int index = 0; index < scheduledCycleInstructions.Count; index++)
            {
                IrInstruction scheduledInstruction = scheduledCycleInstructions[index];
                if (scheduledInstruction.VirtualThreadId != candidate.VirtualThreadId ||
                    !TryGetScalarRegisterAccess(scheduledInstruction, out ushort scheduledWriteRegister, out _, out _))
                {
                    continue;
                }

                if (scheduledWriteRegister != 0 &&
                    (scheduledWriteRegister == candidateReadRegisterA || scheduledWriteRegister == candidateReadRegisterB))
                {
                    penalty += 2;
                }

                if (candidateWriteRegister != 0 && candidateWriteRegister == scheduledWriteRegister)
                {
                    penalty += 1;
                }
            }

            return penalty;
        }

        private static bool TryGetScalarRegisterAccess(
            IrInstruction instruction,
            out ushort writeRegister,
            out ushort readRegisterA,
            out ushort readRegisterB)
        {
            writeRegister = 0;
            readRegisterA = 0;
            readRegisterB = 0;

            if (instruction.StreamLength > 1 ||
                !TryGetPackedRegisterOperand(instruction, out ulong packedRegisters))
            {
                return false;
            }

            writeRegister = (ushort)(packedRegisters & 0xFFFF);
            readRegisterA = (ushort)((packedRegisters >> 16) & 0xFFFF);
            readRegisterB = (ushort)((packedRegisters >> 32) & 0xFFFF);
            return writeRegister != 0 || readRegisterA != 0 || readRegisterB != 0;
        }

        private static bool TryGetPackedRegisterOperand(IrInstruction instruction, out ulong packedRegisters)
        {
            for (int index = 0; index < instruction.Operands.Count; index++)
            {
                IrOperand operand = instruction.Operands[index];
                if (operand.Kind == IrOperandKind.Pointer && operand.Name == "destsrc1")
                {
                    packedRegisters = operand.Value;
                    return true;
                }
            }

            packedRegisters = 0;
            return false;
        }

        private readonly record struct ReadyNodePriority(
            int CriticalPathLengthCycles,
            int SuccessorPressureScore,
            int TypedEffectScore,
            int SameCycleReadySuccessorScore,
            int ReadySuccessorScore,
            int TransitiveSuccessorCount,
            int MinimumLatencyCycles,
            int BankAwareHintPenalty,
            int SlotFlexibility,
            int PostFspScore,
            int LoopPhaseSelectionScore,
            int VtAwareBackendPressureScore,
            int FlexibilityScore,
            int PinnedLaneChokePenalty,
            int SameCycleRawChainPenalty,
            int LoopNormalizationPenalty,
            int LoadClusteringPenalty,
            int CompatibilityCount,
            int ClassPressurePenalty,
            int InstructionIndex);
    }
}
