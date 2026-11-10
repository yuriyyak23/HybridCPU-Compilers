using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU.Compiler.Core.IR.Resources;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Local list-scheduling foundation for HybridCPU IR basic blocks.
    /// </summary>
    public sealed partial class HybridCpuLocalListScheduler
    {
        private readonly HybridCpuProgramDependencyAnalyzer _programDependencyAnalyzer = new();
        private readonly HybridCpuBasicBlockSchedulingDagBuilder _dagBuilder = new();
        private readonly HybridCpuInstructionLegalityChecker _legalityChecker = new();
        private readonly HybridCpuCycleGroupSearch _cycleGroupSearch = new();
        private SchedulerProgramContext _currentProgramContext;
        private SchedulerBlockContext _currentBlockContext;
        private bool _previousScheduledCycleWasPinnedLaneChoke;

        /// <summary>
        /// Optional observational metrics collector. It is never read by scheduler policy.
        /// </summary>
        public CompilerScheduleMetricsCollectorV1? MetricsCollector { get; set; }

        public CompilerResourceShadowCollectorV1? ResourceShadowCollector
        {
            get => _legalityChecker.ResourceShadowCollector;
            set => _legalityChecker.ResourceShadowCollector = value;
        }

        /// <summary>
        /// Schedules all basic blocks in one IR program using a fresh dependence graph.
        /// </summary>
        public IrProgramSchedule ScheduleProgram(IrProgram program) => ScheduleProgram(CancellationToken.None, program);

        /// <summary>Cancellation is observed at dependency, block and cycle boundaries; no partial schedule is returned.</summary>
        public IrProgramSchedule ScheduleProgram(CancellationToken cancellationToken, IrProgram program)
        {
            ArgumentNullException.ThrowIfNull(program);
            cancellationToken.ThrowIfCancellationRequested();
            return ScheduleProgram(program, _programDependencyAnalyzer.AnalyzeProgram(program), cancellationToken);
        }

        /// <summary>
        /// Schedules all basic blocks in one IR program using an existing dependence graph.
        /// </summary>
        public IrProgramSchedule ScheduleProgram(IrProgram program, IrProgramDependencyGraph dependencyGraph) =>
            ScheduleProgram(program, dependencyGraph, CancellationToken.None);

        public IrProgramSchedule ScheduleProgram(IrProgram program, IrProgramDependencyGraph dependencyGraph, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(program);
            ArgumentNullException.ThrowIfNull(dependencyGraph);
            cancellationToken.ThrowIfCancellationRequested();

            program = program with
            {
                Contract = program.Contract with
                {
                    DerivedFacts = program.Contract.DerivedFacts.WithDependenciesCurrent()
                }
            };

            _currentProgramContext = CreateProgramContext(program);
            List<IrBasicBlockSchedule> blockSchedules = new List<IrBasicBlockSchedule>(program.BasicBlocks.Count);
            try
            {
                foreach (IrBasicBlock block in program.BasicBlocks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    blockSchedules.Add(ScheduleBlock(block, dependencyGraph, cancellationToken));
                }
            }
            finally
            {
                _currentProgramContext = default;
                _currentBlockContext = default;
                _previousScheduledCycleWasPinnedLaneChoke = false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new IrProgramSchedule(program, dependencyGraph, blockSchedules);
        }

        /// <summary>
        /// Schedules one basic block using the program dependence graph as the legality and dependence oracle.
        /// </summary>
        public IrBasicBlockSchedule ScheduleBlock(IrBasicBlock block, IrProgramDependencyGraph dependencyGraph) =>
            ScheduleBlock(block, dependencyGraph, CancellationToken.None);

        public IrBasicBlockSchedule ScheduleBlock(IrBasicBlock block, IrProgramDependencyGraph dependencyGraph, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(block);
            ArgumentNullException.ThrowIfNull(dependencyGraph);
            cancellationToken.ThrowIfCancellationRequested();

            if (!UseJointCycleComposition)
            {
                return ScheduleBlockCore(block, dependencyGraph, cancellationToken);
            }

            if (block.Instructions.Count > JointCycleSearchOptions.TopK)
            {
                // A top-K slice cannot compare complete whole-block alternatives beyond this
                // deterministic size gate. Preserve the exact existing path for the entire block.
                UseJointCycleComposition = false;
                try
                {
                    MetricsCollector?.RecordFallback("JointCycleComposition:BlockInstructionCap");
                    return ScheduleBlockCore(block, dependencyGraph, cancellationToken);
                }
                finally
                {
                    UseJointCycleComposition = true;
                }
            }

            // Compare complete block schedules, not partial alternatives. The comparator executes
            // the exact pre-Phase-02 path and deterministically retains it unless joint composition
            // reduces latency-inclusive schedule length. No timing observation selects this result.
            UseJointCycleComposition = false;
            IrBasicBlockSchedule existingPath;
            try
            {
                existingPath = ScheduleBlockCore(block, dependencyGraph, cancellationToken);
            }
            finally
            {
                UseJointCycleComposition = true;
            }

            IrBasicBlockSchedule joint = ScheduleBlockCore(block, dependencyGraph, cancellationToken);
            if (joint.ScheduleLength >= existingPath.ScheduleLength)
            {
                MetricsCollector?.RecordFallback("JointCycleComposition:BlockExistingPathNotWorse");
                return existingPath;
            }

            return joint;
        }

        private IrBasicBlockSchedule ScheduleBlockCore(IrBasicBlock block, IrProgramDependencyGraph dependencyGraph, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(block);
            ArgumentNullException.ThrowIfNull(dependencyGraph);

            _currentBlockContext = CreateBlockContext(block);
            _previousScheduledCycleWasPinnedLaneChoke = false;
            try
            {
                IrBasicBlockSchedulingDag dag = _dagBuilder.BuildBlockDag(block, dependencyGraph);
                IReadOnlyDictionary<int, IrSchedulingNode> nodesByInstructionIndex = BuildNodeMap(dag);
                IReadOnlyDictionary<int, int> transitiveSuccessorCounts = BuildTransitiveSuccessorCounts(dag, nodesByInstructionIndex);
                List<IrScheduleCycleGroup> cycleGroups = new List<IrScheduleCycleGroup>();
                List<IrScheduledInstruction> scheduledInstructions = new List<IrScheduledInstruction>(dag.Nodes.Count);
                Dictionary<int, int> remainingDependencyCounts = new Dictionary<int, int>(dag.Nodes.Count);
                Dictionary<int, int> readyCycles = new Dictionary<int, int>(dag.Nodes.Count);
                HashSet<int> unscheduledInstructionIndexes = new HashSet<int>();

                foreach (IrSchedulingNode node in dag.Nodes)
                {
                    remainingDependencyCounts[node.InstructionIndex] = node.IncomingDependencies.Count;
                    readyCycles[node.InstructionIndex] = 0;
                    unscheduledInstructionIndexes.Add(node.InstructionIndex);
                }

                int currentCycle = 0;
                while (unscheduledInstructionIndexes.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    List<IrSchedulingNode> readyNodes = GetReadyNodes(
                        dag,
                        unscheduledInstructionIndexes,
                        remainingDependencyCounts,
                        readyCycles,
                        nodesByInstructionIndex,
                        transitiveSuccessorCounts,
                        currentCycle);
                    MetricsCollector?.RecordReadyWindow(block.Id, currentCycle, readyNodes.Count);
                    if (readyNodes.Count == 0)
                    {
                        _previousScheduledCycleWasPinnedLaneChoke = false;
                        currentCycle = GetNextReadyCycle(unscheduledInstructionIndexes, remainingDependencyCounts, readyCycles, currentCycle);
                        continue;
                    }

                    CycleGroupBuildResult cycleBuild = BuildCycleGroup(
                        readyNodes,
                        unscheduledInstructionIndexes,
                        remainingDependencyCounts,
                        readyCycles,
                        nodesByInstructionIndex,
                        transitiveSuccessorCounts,
                        currentCycle);
                    IReadOnlyList<IrSchedulingNode> scheduledThisCycle = cycleBuild.Nodes;
                    if (scheduledThisCycle.Count == 0)
                    {
                        throw new InvalidOperationException($"Scheduler failed to form a legal cycle group for block {block.Id} at cycle {currentCycle}.");
                    }

                    List<IrInstruction> cycleInstructions = new List<IrInstruction>(scheduledThisCycle.Count);
                    for (int index = 0; index < scheduledThisCycle.Count; index++)
                    {
                        cycleInstructions.Add(scheduledThisCycle[index].Instruction);
                    }

                    IrCandidateBundleAnalysis legalityAnalysis = _legalityChecker.AnalyzeCandidateBundle(cycleInstructions);
                    if (!legalityAnalysis.IsStructurallyAdmissible)
                    {
                        throw new InvalidOperationException($"Scheduler formed an illegal cycle group for block {block.Id} at cycle {currentCycle}.");
                    }

                    for (int orderInCycle = 0; orderInCycle < scheduledThisCycle.Count; orderInCycle++)
                    {
                        IrSchedulingNode node = scheduledThisCycle[orderInCycle];
                        unscheduledInstructionIndexes.Remove(node.InstructionIndex);
                        scheduledInstructions.Add(new IrScheduledInstruction(
                            InstructionIndex: node.InstructionIndex,
                            Instruction: node.Instruction,
                            Cycle: currentCycle,
                            OrderInCycle: orderInCycle,
                            ReadyCycle: readyCycles[node.InstructionIndex],
                            CriticalPathLengthCycles: node.CriticalPathLengthCycles));
                    }

                    foreach (IrSchedulingNode node in scheduledThisCycle)
                    {
                        foreach (IrInstructionDependency dependency in node.OutgoingDependencies)
                        {
                            if (!unscheduledInstructionIndexes.Contains(dependency.ConsumerInstructionIndex))
                            {
                                continue;
                            }

                            remainingDependencyCounts[dependency.ConsumerInstructionIndex]--;
                            readyCycles[dependency.ConsumerInstructionIndex] = Math.Max(
                                readyCycles[dependency.ConsumerInstructionIndex],
                                currentCycle + dependency.MinimumLatencyCycles);
                        }
                    }

                    cycleGroups.Add(new IrScheduleCycleGroup(
                        currentCycle,
                        cycleInstructions,
                        legalityAnalysis,
                        cycleBuild.SearchResult?.PlacementWitness,
                        cycleBuild.SearchResult?.Summary));
                    _previousScheduledCycleWasPinnedLaneChoke = IsPinnedLaneChokeCycle(scheduledThisCycle);
                    currentCycle++;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new IrBasicBlockSchedule(block, dag, cycleGroups, scheduledInstructions);
            }
            finally
            {
                _currentBlockContext = default;
                _previousScheduledCycleWasPinnedLaneChoke = false;
            }
        }

        private List<IrSchedulingNode> GetReadyNodes(
                IrBasicBlockSchedulingDag dag,
                IReadOnlySet<int> unscheduledInstructionIndexes,
                IReadOnlyDictionary<int, int> remainingDependencyCounts,
                IReadOnlyDictionary<int, int> readyCycles,
                IReadOnlyDictionary<int, IrSchedulingNode> nodesByInstructionIndex,
                IReadOnlyDictionary<int, int> transitiveSuccessorCounts,
                int currentCycle)
        {
            List<IrSchedulingNode> readyNodes = new List<IrSchedulingNode>();
            foreach (IrSchedulingNode node in dag.Nodes)
            {
                if (!unscheduledInstructionIndexes.Contains(node.InstructionIndex))
                {
                    continue;
                }

                if (remainingDependencyCounts[node.InstructionIndex] != 0)
                {
                    continue;
                }

                if (readyCycles[node.InstructionIndex] > currentCycle)
                {
                    continue;
                }

                readyNodes.Add(node);
            }

            readyNodes.Sort((left, right) => CompareReadyNodes(
                left,
                right,
                unscheduledInstructionIndexes,
                remainingDependencyCounts,
                nodesByInstructionIndex,
                transitiveSuccessorCounts));
            return readyNodes;
        }

        private int GetNextReadyCycle(
                IReadOnlySet<int> unscheduledInstructionIndexes,
                IReadOnlyDictionary<int, int> remainingDependencyCounts,
                IReadOnlyDictionary<int, int> readyCycles,
                int currentCycle)
        {
            int nextReadyCycle = int.MaxValue;
            foreach (int instructionIndex in unscheduledInstructionIndexes)
            {
                if (remainingDependencyCounts[instructionIndex] != 0)
                {
                    continue;
                }

                nextReadyCycle = Math.Min(nextReadyCycle, readyCycles[instructionIndex]);
            }

            if (nextReadyCycle == int.MaxValue)
            {
                throw new InvalidOperationException("Scheduling DAG contains unsatisfied dependencies that prevent further progress.");
            }

            return Math.Max(currentCycle + 1, nextReadyCycle);
        }

        private bool CanAddToCycle(IReadOnlyList<IrInstruction> scheduledCycleInstructions, IrInstruction candidate)
        {
            List<IrInstruction> candidateInstructions = new List<IrInstruction>(scheduledCycleInstructions.Count + 1);
            for (int index = 0; index < scheduledCycleInstructions.Count; index++)
            {
                candidateInstructions.Add(scheduledCycleInstructions[index]);
            }

            candidateInstructions.Add(candidate);
            return _legalityChecker.AnalyzeCandidateBundle(candidateInstructions).IsStructurallyAdmissible;
        }

        private SchedulerProgramContext CreateProgramContext(IrProgram program)
        {
            return new SchedulerProgramContext(
                ReductionAwareShapingEnabled: UseReductionAwareShaping,
                TreatAsReductionCoordinator: UseReductionAwareShaping && TreatAsReductionCoordinator,
                TreatAsCoordinatorPath: TreatAsCoordinatorPath,
                VirtualThreadId: program.VirtualThreadId,
                BackendResourceShapingPressure: HasUsableProfile
                    ? ProfileReader!.GetBackendResourceShapingPressure(program.VirtualThreadId, TreatAsCoordinatorPath)
                    : 0.0);
        }

        private SchedulerBlockContext CreateBlockContext(IrBasicBlock block)
        {
            double bankClusteringSignal = HasUsableProfile
                ? ProfileReader!.GetAdvisoryMemoryClusteringSignal()
                : 0.0;
            double bankPressureSignal = HasUsableProfile
                ? ProfileReader!.GetAdvisoryBankPressureSignal()
                : 0.0;
            double crossDomainRejectSignal = HasUsableProfile
                ? ProfileReader!.GetCrossDomainRejectRate()
                : 0.0;

            if (!HasUsableLoopPhaseTelemetry)
            {
                return new SchedulerBlockContext(
                    LoopPcAddress: block.StartAddress,
                    HasLoopProfile: false,
                    IsHotLoop: false,
                    IterationsSampled: 0,
                    OverallClassVariance: 0.0,
                    TemplateReuseRate: 0.0,
                    BankClusteringSignal: bankClusteringSignal,
                    BankPressureSignal: bankPressureSignal,
                    CrossDomainRejectSignal: crossDomainRejectSignal);
            }

            LoopPhaseClassProfile? resolvedProfile = ProfileReader!.TryResolveLoopProfile(
                block.StartAddress, block.EndAddress);

            if (resolvedProfile is null)
            {
                return new SchedulerBlockContext(
                    LoopPcAddress: block.StartAddress,
                    HasLoopProfile: false,
                    IsHotLoop: false,
                    IterationsSampled: 0,
                    OverallClassVariance: 0.0,
                    TemplateReuseRate: 0.0,
                    BankClusteringSignal: bankClusteringSignal,
                    BankPressureSignal: bankPressureSignal,
                    CrossDomainRejectSignal: crossDomainRejectSignal);
            }

            return new SchedulerBlockContext(
                LoopPcAddress: resolvedProfile.LoopPcAddress,
                HasLoopProfile: true,
                IsHotLoop: resolvedProfile.IterationsSampled >= HotLoopIterationThreshold,
                IterationsSampled: resolvedProfile.IterationsSampled,
                OverallClassVariance: resolvedProfile.OverallClassVariance,
                TemplateReuseRate: resolvedProfile.TemplateReuseRate,
                BankClusteringSignal: bankClusteringSignal,
                BankPressureSignal: bankPressureSignal,
                CrossDomainRejectSignal: crossDomainRejectSignal);
        }

        private readonly record struct SchedulerProgramContext(
            bool ReductionAwareShapingEnabled,
            bool TreatAsReductionCoordinator,
            bool TreatAsCoordinatorPath,
            byte VirtualThreadId,
            double BackendResourceShapingPressure);

        private readonly record struct SchedulerBlockContext(
            ulong LoopPcAddress,
            bool HasLoopProfile,
            bool IsHotLoop,
            int IterationsSampled,
            double OverallClassVariance,
            double TemplateReuseRate,
            double BankClusteringSignal,
            double BankPressureSignal,
            double CrossDomainRejectSignal);
    }
}
