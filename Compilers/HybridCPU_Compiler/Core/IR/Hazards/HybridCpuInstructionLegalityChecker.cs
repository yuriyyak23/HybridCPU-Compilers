using System;
using System.Collections.Generic;
using System.Linq;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Resources;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Evaluates same-cycle candidate legality without introducing scheduling or bundling decisions.
    /// </summary>
    public sealed class HybridCpuInstructionLegalityChecker
    {
        private readonly HybridCpuDependencyAnalyzer _dependencyAnalyzer = new();

        /// <summary>
        /// Optional default-off Phase 01 resource shadow sink. It never supplies legality.
        /// </summary>
        public CompilerResourceShadowCollectorV1? ResourceShadowCollector { get; set; }

        public IHybridCpuMachineResourceModel ResourceModel { get; set; } =
            HybridCpuMachineResourceModelV1.Default;

        /// <summary>
        /// Returns the centralized execution profile for an opcode.
        /// </summary>
        public IrOpcodeExecutionProfile GetExecutionProfile(HybridCpuOpcode opcode)
        {
            return HybridCpuHazardModel.GetExecutionProfile(opcode);
        }

        /// <summary>
        /// Analyzes candidate-group legality and returns compiler-facing query metadata.
        /// </summary>
        public IrCandidateBundleAnalysis AnalyzeCandidateBundle(IReadOnlyList<IrInstruction> instructions)
        {
            ArgumentNullException.ThrowIfNull(instructions);

            // Phase 03: class-capacity pre-check (advisory diagnostic, not blocking).
            IrClassCapacityResult capacityResult = HybridCpuClassCapacityChecker.CheckCapacity(instructions);

            if (instructions.Count <= 1)
            {
                IrSlotAssignmentAnalysis trivialSlotAnalysis = HybridCpuSlotModel.AnalyzeStructuralAssignment(GetStructurallyAllowedSlotMasks(instructions));
                IrStructuralResourceAnalysis trivialStructuralAnalysis = HybridCpuStructuralResourceModel.AnalyzeResources(instructions);
                IrCandidateBundleAnalysis trivialAnalysis = new IrCandidateBundleAnalysis(
                    Instructions: instructions,
                    Legality: IrBundleLegalityResult.StructurallyAdmissible,
                    SlotAnalysis: trivialSlotAnalysis,
                    StructuralAnalysis: trivialStructuralAnalysis,
                    ClassCapacityResult: capacityResult);
                ObserveResourceShadow(instructions, trivialAnalysis);
                return trivialAnalysis;
            }

            List<IrHazardDiagnostic> hazards = new List<IrHazardDiagnostic>();

            AddClassCapacityHazards(capacityResult, hazards);

            for (int leftIndex = 0; leftIndex < instructions.Count; leftIndex++)
            {
                for (int rightIndex = leftIndex + 1; rightIndex < instructions.Count; rightIndex++)
                {
                    hazards.AddRange(AnalyzePair(instructions[leftIndex], instructions[rightIndex], cycleDistance: 0));
                }
            }

            IrSlotAssignmentAnalysis slotAnalysis = HybridCpuSlotModel.AnalyzeStructuralAssignment(GetStructurallyAllowedSlotMasks(instructions));
            AddSlotHazards(slotAnalysis, hazards);
            IrStructuralResourceAnalysis structuralAnalysis = HybridCpuStructuralResourceModel.AnalyzeResources(instructions);

            IrCandidateBundleAnalysis result = new IrCandidateBundleAnalysis(
                Instructions: instructions,
                Legality: new IrBundleLegalityResult(hazards),
                SlotAnalysis: slotAnalysis,
                StructuralAnalysis: structuralAnalysis,
                ClassCapacityResult: capacityResult);
            ObserveResourceShadow(instructions, result);
            return result;
        }

        /// <summary>
        /// Analyzes compiler structural admission for the provided instructions.
        /// This result is evidence only; runtime Legality A/B remain runtime-owned.
        /// </summary>
        public CompilerStructuralBundleAdmissionResult AnalyzeStructuralCandidateBundle(
            IReadOnlyList<IrInstruction> instructions)
        {
            IrBundleLegalityResult result = AnalyzeCandidateBundle(instructions).Legality;
            return CompilerStructuralAuthorityQuarantine.FromBundleLegalityResult(
                result,
                nameof(AnalyzeStructuralCandidateBundle));
        }

        /// <summary>
        /// Legacy structural legality surface. Use AnalyzeStructuralCandidateBundle for typed authority.
        /// </summary>
        [Obsolete(
            "EvaluateCandidateBundle returns compiler structural evidence only; use AnalyzeStructuralCandidateBundle.",
            false)]
        public IrBundleLegalityResult EvaluateCandidateBundle(IReadOnlyList<IrInstruction> instructions)
        {
            return AnalyzeCandidateBundle(instructions).Legality;
        }

        /// <summary>
        /// Analyzes cluster-prepared compiler structural admission for an instruction group.
        /// This result is evidence only; runtime Legality A/B remain runtime-owned.
        /// </summary>
        public CompilerStructuralBundleAdmissionResult AnalyzeClusterPreparedStructuralAdmission(
            IReadOnlyList<IrInstruction> instructions)
        {
            IrBundleLegalityResult result = EvaluateClusterPreparedLegalityCore(instructions);
            return CompilerStructuralAuthorityQuarantine.FromBundleLegalityResult(
                result,
                nameof(AnalyzeClusterPreparedStructuralAdmission));
        }

        /// <summary>
        /// Phase 06: Evaluates cluster-prepared legality for an instruction group.
        /// Returns hazards for groups that are legal only under sequential 1-slot issue
        /// but would be rejected by the cluster-prepared decode path.
        /// This tightening ensures compiler-emitted bundles survive concurrent scalar issue.
        /// </summary>
        /// <param name="instructions">The candidate instruction group.</param>
        /// <returns>Legality result including cluster-prepared-specific hazards.</returns>
        [Obsolete(
            "EvaluateClusterPreparedLegality returns compiler structural evidence only; use AnalyzeClusterPreparedStructuralAdmission.",
            false)]
        public IrBundleLegalityResult EvaluateClusterPreparedLegality(IReadOnlyList<IrInstruction> instructions)
        {
            return EvaluateClusterPreparedLegalityCore(instructions);
        }

        private IrBundleLegalityResult EvaluateClusterPreparedLegalityCore(IReadOnlyList<IrInstruction> instructions)
        {
            ArgumentNullException.ThrowIfNull(instructions);

            // Start from the standard legality analysis
            IrCandidateBundleAnalysis baseAnalysis = AnalyzeCandidateBundle(instructions);

            // If already illegal under standard checks, return as-is
            if (!baseAnalysis.IsStructurallyAdmissible)
                return baseAnalysis.Legality;

            if (instructions.Count <= 1)
                return baseAnalysis.Legality;

            // Phase 06 cluster-aware tightening:
            // Count scalar ops in the group — if more than 4, the group cannot be issued
            // on the cluster-prepared path (4-way scalar ceiling per Phase 02).
            int scalarCount = 0;
            for (int i = 0; i < instructions.Count; i++)
            {
                IrIssueSlotMask structurallyAllowedSlots = instructions[i].Annotation.StructurallyAllowedSlots;
                if ((structurallyAllowedSlots & IrIssueSlotMask.Scalar) != IrIssueSlotMask.None &&
                    (structurallyAllowedSlots & ~IrIssueSlotMask.Scalar) == IrIssueSlotMask.None)
                {
                    // Instruction is scalar-only (can only go in slots 0-3)
                    scalarCount++;
                }
            }

            List<IrHazardDiagnostic> clusterHazards = new List<IrHazardDiagnostic>();

            if (scalarCount > 4)
            {
                clusterHazards.Add(new IrHazardDiagnostic(
                    Category: IrHazardCategory.Structural,
                    Reason: IrHazardReason.ClusterPreparedSequentialOnly,
                    LeftInstructionIndex: null,
                    RightInstructionIndex: null,
                    Message: $"Group contains {scalarCount} scalar-only instructions exceeding the 4-way scalar cluster ceiling. Legal only under sequential 1-slot issue."));
            }

            // Check for WAR hazards that the standard checker flags as advisory
            // but cluster-prepared path would reject under concurrent execution.
            // Under cluster-prepared path, WAR between scalar-only instructions
            // requires runtime check — if the group relies on sequential ordering
            // to avoid the WAR conflict, it must be split.
            for (int leftIndex = 0; leftIndex < instructions.Count; leftIndex++)
            {
                for (int rightIndex = leftIndex + 1; rightIndex < instructions.Count; rightIndex++)
                {
                    IReadOnlyList<IrHazardDiagnostic> pairHazards = AnalyzePair(
                        instructions[leftIndex], instructions[rightIndex], cycleDistance: 0);

                    for (int h = 0; h < pairHazards.Count; h++)
                    {
                        IrHazardDiagnostic hazard = pairHazards[h];

                        // Under cluster-prepared path, same-cycle WAR between scalar ops
                        // needs runtime triage — flag as cluster-sequential-only if both are scalar
                        if (hazard.Reason == IrHazardReason.WriteAfterRead &&
                            IsScalarOnlyInstruction(instructions[leftIndex]) &&
                            IsScalarOnlyInstruction(instructions[rightIndex]))
                        {
                            clusterHazards.Add(new IrHazardDiagnostic(
                                Category: IrHazardCategory.Data,
                                Reason: IrHazardReason.ClusterPreparedSequentialOnly,
                                LeftInstructionIndex: instructions[leftIndex].Index,
                                RightInstructionIndex: instructions[rightIndex].Index,
                                Message: $"Scalar instructions {instructions[leftIndex].Index} and {instructions[rightIndex].Index} have a WAR dependency requiring runtime triage under cluster-prepared path.",
                                DataHazard: IrDataHazardKind.WriteAfterRead,
                                DependencyKind: hazard.DependencyKind));
                        }
                    }
                }
            }

            if (clusterHazards.Count == 0)
                return baseAnalysis.Legality;

            // Merge standard hazards (empty, since base was legal) with cluster-specific hazards
            return new IrBundleLegalityResult(clusterHazards);
        }

        /// <summary>
        /// Returns whether the instruction can only be placed in scalar slots (0-3).
        /// </summary>
        private static bool IsScalarOnlyInstruction(IrInstruction instruction)
        {
            IrIssueSlotMask structurallyAllowedSlots = instruction.Annotation.StructurallyAllowedSlots;
            return (structurallyAllowedSlots & IrIssueSlotMask.Scalar) != IrIssueSlotMask.None &&
                   (structurallyAllowedSlots & ~IrIssueSlotMask.Scalar) == IrIssueSlotMask.None;
        }

        /// <summary>
        /// Analyzes pairwise legality between two instructions with an explicit cycle distance.
        /// </summary>
        public IReadOnlyList<IrHazardDiagnostic> AnalyzePair(IrInstruction first, IrInstruction second, int cycleDistance)
        {
            ArgumentNullException.ThrowIfNull(first);
            ArgumentNullException.ThrowIfNull(second);
            ArgumentOutOfRangeException.ThrowIfNegative(cycleDistance);

            List<IrHazardDiagnostic> hazards = new List<IrHazardDiagnostic>();
            IReadOnlyList<IrInstructionDependency> dependencies = _dependencyAnalyzer.AnalyzePair(first, second);

            AddDependencyHazards(first, second, dependencies, cycleDistance, hazards);
            AddSharedStructuralHazards(first, second, cycleDistance, hazards);

            return hazards;
        }

        private static void AddDependencyHazards(
            IrInstruction first,
            IrInstruction second,
            IReadOnlyList<IrInstructionDependency> dependencies,
            int cycleDistance,
            ICollection<IrHazardDiagnostic> hazards)
        {
            foreach (IrInstructionDependency dependency in dependencies)
            {
                switch (dependency.Kind)
                {
                    case IrInstructionDependencyKind.RegisterRaw:
                        if (cycleDistance == 0)
                        {
                            hazards.Add(new IrHazardDiagnostic(
                                Category: IrHazardCategory.Data,
                                Reason: IrHazardReason.ReadAfterWrite,
                                LeftInstructionIndex: first.Index,
                                RightInstructionIndex: second.Index,
                                Message: $"Instruction {second.Index} reads a value defined by instruction {first.Index} in the same candidate group.",
                                DataHazard: IrDataHazardKind.ReadAfterWrite,
                                DependencyKind: dependency.Kind,
                                DominantEffectKind: dependency.DominantEffectKind));
                        }

                        if (cycleDistance < dependency.MinimumLatencyCycles)
                        {
                            hazards.Add(new IrHazardDiagnostic(
                                Category: IrHazardCategory.Latency,
                                Reason: IrHazardReason.LatencyConstraint,
                                LeftInstructionIndex: first.Index,
                                RightInstructionIndex: second.Index,
                                Message: $"Instruction {second.Index} requires at least {dependency.MinimumLatencyCycles} cycle(s) of separation from producer {first.Index}.",
                                RequiredLatencyCycles: dependency.MinimumLatencyCycles,
                                DependencyKind: dependency.Kind,
                                DominantEffectKind: dependency.DominantEffectKind));
                        }
                        break;

                    case IrInstructionDependencyKind.RegisterWar:
                        if (cycleDistance == 0)
                        {
                            hazards.Add(new IrHazardDiagnostic(
                                Category: IrHazardCategory.Data,
                                Reason: IrHazardReason.WriteAfterRead,
                                LeftInstructionIndex: first.Index,
                                RightInstructionIndex: second.Index,
                                Message: $"Instruction {second.Index} overwrites a value still read by instruction {first.Index} in the same candidate group.",
                                DataHazard: IrDataHazardKind.WriteAfterRead,
                                DependencyKind: dependency.Kind,
                                DominantEffectKind: dependency.DominantEffectKind));
                        }
                        break;

                    case IrInstructionDependencyKind.RegisterWaw:
                        if (cycleDistance == 0)
                        {
                            hazards.Add(new IrHazardDiagnostic(
                                Category: IrHazardCategory.Data,
                                Reason: IrHazardReason.WriteAfterWrite,
                                LeftInstructionIndex: first.Index,
                                RightInstructionIndex: second.Index,
                                Message: $"Instructions {first.Index} and {second.Index} both define the same storage in one candidate group.",
                                DataHazard: IrDataHazardKind.WriteAfterWrite,
                                DependencyKind: dependency.Kind,
                                DominantEffectKind: dependency.DominantEffectKind));
                        }
                        break;

                    case IrInstructionDependencyKind.Memory:
                        string memoryVerb = dependency.MemoryPrecision == IrMemoryDependencyPrecision.May ? "may access" : "access";

                        if (cycleDistance == 0)
                        {
                            hazards.Add(new IrHazardDiagnostic(
                                Category: IrHazardCategory.Data,
                                Reason: IrHazardReason.MemoryDependency,
                                LeftInstructionIndex: first.Index,
                                RightInstructionIndex: second.Index,
                                Message: $"Instructions {first.Index} and {second.Index} {memoryVerb} overlapping memory in a conflicting way.",
                                DataHazard: IrDataHazardKind.MemoryDependency,
                                DependencyKind: dependency.Kind,
                                MemoryPrecision: dependency.MemoryPrecision,
                                DominantEffectKind: dependency.DominantEffectKind));
                        }

                        if (cycleDistance < dependency.MinimumLatencyCycles)
                        {
                            hazards.Add(new IrHazardDiagnostic(
                                Category: IrHazardCategory.Latency,
                                Reason: IrHazardReason.LatencyConstraint,
                                LeftInstructionIndex: first.Index,
                                RightInstructionIndex: second.Index,
                                Message: $"Memory dependence between instructions {first.Index} and {second.Index} requires at least {dependency.MinimumLatencyCycles} cycle(s) of separation.",
                                RequiredLatencyCycles: dependency.MinimumLatencyCycles,
                                DependencyKind: dependency.Kind,
                                MemoryPrecision: dependency.MemoryPrecision,
                                DominantEffectKind: dependency.DominantEffectKind));
                        }
                        break;

                    case IrInstructionDependencyKind.Control:
                        if (cycleDistance < dependency.MinimumLatencyCycles)
                        {
                            hazards.Add(new IrHazardDiagnostic(
                                Category: IrHazardCategory.Control,
                                Reason: IrHazardReason.ControlDependency,
                                LeftInstructionIndex: first.Index,
                                RightInstructionIndex: second.Index,
                                Message: $"Instruction {second.Index} is control-dependent on instruction {first.Index} and cannot share the same cycle group.",
                                RequiredLatencyCycles: dependency.MinimumLatencyCycles,
                                RelevantResources: dependency.StructuralResources,
                                DependencyKind: dependency.Kind,
                                DominantEffectKind: dependency.DominantEffectKind));
                        }
                        break;

                    case IrInstructionDependencyKind.Serialization:
                        if (cycleDistance < dependency.MinimumLatencyCycles)
                        {
                            hazards.Add(new IrHazardDiagnostic(
                                Category: IrHazardCategory.Structural,
                                Reason: IrHazardReason.ExclusiveCycleRequired,
                                LeftInstructionIndex: first.Index,
                                RightInstructionIndex: second.Index,
                                Message: $"Instructions {first.Index} and {second.Index} cannot share a cycle because at least one instruction is serializing.",
                                RequiredLatencyCycles: dependency.MinimumLatencyCycles,
                                RelevantResources: dependency.StructuralResources,
                                DependencyKind: dependency.Kind,
                                DominantEffectKind: dependency.DominantEffectKind));
                        }
                        break;
                }
            }
        }

        private static void AddSharedStructuralHazards(IrInstruction first, IrInstruction second, int cycleDistance, ICollection<IrHazardDiagnostic> hazards)
        {
            if (cycleDistance != 0)
            {
                return;
            }

            IrStructuralResourceAnalysis analysis = HybridCpuStructuralResourceModel.AnalyzeResources(new[] { first, second });
            foreach (IrStructuralResourceUsage conflict in analysis.ConflictingUsages)
            {
                hazards.Add(new IrHazardDiagnostic(
                    Category: IrHazardCategory.Structural,
                    Reason: IrHazardReason.StructuralResourceConflict,
                    LeftInstructionIndex: first.Index,
                    RightInstructionIndex: second.Index,
                    Message: $"Instructions {first.Index} and {second.Index} exceed structural capacity for resource {conflict.Resource} ({conflict.UsedUnits}/{conflict.Capacity}).",
                    RelevantResources: conflict.Resource));
            }
        }

        private static void AddClassCapacityHazards(IrClassCapacityResult capacityResult, ICollection<IrHazardDiagnostic> hazards)
        {
            if (!capacityResult.IsWithinCapacity)
            {
                IReadOnlyList<IrSlotClass> overcommitted = capacityResult.GetOvercommittedClasses();
                if (overcommitted.Count > 0)
                {
                    string classList = string.Join(", ", overcommitted.Select(c => c.ToString()));
                    hazards.Add(new IrHazardDiagnostic(
                        Category: IrHazardCategory.Structural,
                        Reason: IrHazardReason.ClassCapacityExceeded,
                        LeftInstructionIndex: null,
                        RightInstructionIndex: null,
                        Message: $"Class-capacity exceeded for: {classList}."));
                }

                if (capacityResult.HasAliasedLaneConflict)
                {
                    hazards.Add(new IrHazardDiagnostic(
                        Category: IrHazardCategory.Structural,
                        Reason: IrHazardReason.AliasedLaneConflict,
                        LeftInstructionIndex: null,
                        RightInstructionIndex: null,
                        Message: "Aliased-lane conflict: BranchControl and SystemSingleton both require lane 7."));
                }
            }
        }

        private static void AddSlotHazards(IrSlotAssignmentAnalysis slotAnalysis, ICollection<IrHazardDiagnostic> hazards)
        {
            if (slotAnalysis.CandidateInstructionCount > HybridCpuSlotModel.SlotCount)
            {
                hazards.Add(new IrHazardDiagnostic(
                    Category: IrHazardCategory.Slot,
                    Reason: IrHazardReason.SlotCapacityExceeded,
                    LeftInstructionIndex: null,
                    RightInstructionIndex: null,
                    Message: $"Candidate group contains {slotAnalysis.CandidateInstructionCount} instructions but the current slot model exposes only {HybridCpuSlotModel.SlotCount} slots.",
                    RelevantSlots: IrIssueSlotMask.All));
                return;
            }

            if (!slotAnalysis.HasStructuralPlacement)
            {
                hazards.Add(new IrHazardDiagnostic(
                    Category: IrHazardCategory.Slot,
                    Reason: IrHazardReason.NoLegalSlotAssignment,
                    LeftInstructionIndex: null,
                    RightInstructionIndex: null,
                    Message: "Candidate group has no legal slot assignment under the current HybridCPU slot model.",
                    RelevantSlots: slotAnalysis.StructurallyAllowedSlots));
            }
        }

        private static IReadOnlyList<IrIssueSlotMask> GetStructurallyAllowedSlotMasks(IReadOnlyList<IrInstruction> instructions)
        {
            List<IrIssueSlotMask> structurallyAllowedSlots = new List<IrIssueSlotMask>(instructions.Count);
            foreach (IrInstruction instruction in instructions)
            {
                structurallyAllowedSlots.Add(instruction.Annotation.StructurallyAllowedSlots);
            }

            return structurallyAllowedSlots;
        }

        private void ObserveResourceShadow(
            IReadOnlyList<IrInstruction> instructions,
            IrCandidateBundleAnalysis legacyAnalysis)
        {
            CompilerResourceShadowCollectorV1? collector = ResourceShadowCollector;
            if (collector is null)
            {
                return;
            }

            if (!collector.TryBeginEvaluation())
            {
                return;
            }

            try
            {
                collector.RecordEvaluated(CompilerResourceShadowEvaluatorV1.Evaluate(
                    instructions,
                    legacyAnalysis,
                    ResourceModel));
            }
            catch
            {
                // The shadow path is observational. Preserve the legacy decision and record a
                // deterministic fallback instead of allowing diagnostic failure to escape.
                try
                {
                    collector.RecordEvaluated(CompilerResourceShadowEvaluatorV1.CreateEvaluationFailure(
                        instructions,
                        legacyAnalysis,
                        ResourceModel));
                }
                catch
                {
                    // Even failure reporting is forbidden from influencing compiler output.
                    collector.AbandonEvaluation();
                }
            }
        }

    }
}
