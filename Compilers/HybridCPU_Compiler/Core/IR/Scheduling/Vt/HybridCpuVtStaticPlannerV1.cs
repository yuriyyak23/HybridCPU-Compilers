using System.Globalization;
using System.Text;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.IR.Telemetry;

namespace HybridCPU.Compiler.Core.IR.Scheduling.Vt;

/// <summary>
/// Deterministically composes already-valid VT-local cycle groups into static W=8 placement
/// evidence. It does not emit carriers or predict live Stage A/B ownership.
/// </summary>
public sealed class HybridCpuVtStaticPlannerV1
{
    public IrVtStaticPlanV1 Plan(
        IReadOnlyList<IrVtLocalScheduleEvidenceV1> streams,
        IReadOnlyList<IrCrossVtRelationshipProofV1>? relationshipProofs = null,
        HybridCpuTopologyResourceModelV1? topologyModel = null,
        HybridCpuVtStaticPlanningOptionsV1? options = null)
    {
        ArgumentNullException.ThrowIfNull(streams);
        relationshipProofs ??= Array.Empty<IrCrossVtRelationshipProofV1>();
        topologyModel ??= new HybridCpuTopologyResourceModelV1();
        options ??= HybridCpuVtStaticPlanningOptionsV1.Production;
        IrVtLocalScheduleEvidenceV1[] ordered = streams
            .Where(static stream => stream is not null)
            .OrderBy(static stream => stream.VirtualThreadId).ToArray();
        string fallbackDigest = LocalBaselineDigest(ordered);

        if (!HasValidOptions(options) || ordered.Length != streams.Count || ordered.Length == 0)
            return Fallback(
                IrVtStaticPlanStatusV1.InvalidModel,
                ordered,
                topologyModel.Topology.ContractDigest,
                options.OptionsDigest,
                fallbackDigest,
                "HCVT1001",
                "VT planning inputs or deterministic budgets are invalid.");
        if (options.PlanningSwitch == IrVtStaticPlanningSwitchV1.Disabled)
            return Fallback(
                IrVtStaticPlanStatusV1.DisabledFallback,
                ordered,
                topologyModel.Topology.ContractDigest,
                options.OptionsDigest,
                fallbackDigest,
                "HCVT1002",
                "Cross-VT planning is disabled; every original VT-local schedule remains exact.");
        if (ordered.Length > options.Budgets.MaximumStreams)
            return Fallback(
                IrVtStaticPlanStatusV1.BudgetExhausted,
                ordered,
                topologyModel.Topology.ContractDigest,
                options.OptionsDigest,
                fallbackDigest,
                "HCVT1003",
                "Active VT stream count exceeds the deterministic production bound.");
        if (ordered.Select(static stream => stream.VirtualThreadId).Distinct().Count() != ordered.Length ||
            ordered.Any(static stream => stream.VirtualThreadId >= HybridCpuMachineTopologyV1.VirtualThreadCount))
            return Fallback(
                IrVtStaticPlanStatusV1.InvalidModel,
                ordered,
                topologyModel.Topology.ContractDigest,
                options.OptionsDigest,
                fallbackDigest,
                "HCVT1004",
                "VT identities must be unique values in the target's four-context domain.");

        foreach (IrVtLocalScheduleEvidenceV1 stream in ordered)
        {
            IrVtStaticPlanStatusV1 validation = ValidateLocalStream(stream);
            if (validation != IrVtStaticPlanStatusV1.Accepted)
                return Fallback(
                    validation,
                    ordered,
                    topologyModel.Topology.ContractDigest,
                    options.OptionsDigest,
                    fallbackDigest,
                    validation == IrVtStaticPlanStatusV1.StaleProof ? "HCVT1005" : "HCVT1006",
                    validation == IrVtStaticPlanStatusV1.StaleProof
                        ? "A VT-local program or schedule digest is stale."
                        : "A VT-local schedule is not a complete Canonical IR schedule for its assigned VT.");
        }

        int localCarriers = ordered.Sum(static stream => stream.LocalCarrierCount);
        int operations = ordered.Sum(static stream => stream.LocalSchedule.Program.Instructions.Count);
        if (localCarriers > options.Budgets.MaximumLocalCarriers ||
            operations > options.Budgets.MaximumOperations)
            return Fallback(
                IrVtStaticPlanStatusV1.BudgetExhausted,
                ordered,
                topologyModel.Topology.ContractDigest,
                options.OptionsDigest,
                fallbackDigest,
                "HCVT1007",
                "VT-local carrier or operation count exceeds deterministic production bounds.");
        if (!ValidateRelationshipProofs(ordered, relationshipProofs))
            return Fallback(
                IrVtStaticPlanStatusV1.StaleProof,
                ordered,
                topologyModel.Topology.ContractDigest,
                options.OptionsDigest,
                fallbackDigest,
                "HCVT1008",
                "Every active VT pair requires one current explicit static-independence proof.");
        if (HasUnsupportedCrossVtSemantics(ordered))
            return Fallback(
                IrVtStaticPlanStatusV1.Ineligible,
                ordered,
                topologyModel.Topology.ContractDigest,
                options.OptionsDigest,
                fallbackDigest,
                "HCVT1009",
                "Synchronization, aliasing memory, exception/control or special-state semantics require VT-local fallback.");

        StreamState[] states = ordered.Select(BuildState).ToArray();
        List<IrVtStaticJointCycleV1> jointCycles = new List<IrVtStaticJointCycleV1>();
        int evaluated = 0;
        int structuralRejects = 0;
        int resourceRejects = 0;
        int placements = 0;
        while (states.Any(static state => state.Position < state.Carriers.Count))
        {
            StreamState[] active = states.Where(static state => state.Position < state.Carriers.Count).ToArray();
            Candidate? best = null;
            int subsetCount = checked(1 << active.Length);
            for (int mask = 1; mask < subsetCount; mask++)
            {
                evaluated++;
                if (evaluated > options.Budgets.MaximumSubsetEvaluations)
                    return Fallback(
                        IrVtStaticPlanStatusV1.BudgetExhausted,
                        ordered,
                        topologyModel.Topology.ContractDigest,
                        options.OptionsDigest,
                        fallbackDigest,
                        "HCVT1010",
                        "Cross-VT subset enumeration exhausted its deterministic work-unit budget.",
                        new(evaluated, structuralRejects, resourceRejects, placements, evaluated + placements));
                StreamState[] selected = active.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
                int memberCount = selected.Sum(static state => state.Carriers[state.Position].Members.Count);
                if (memberCount > HybridCpuMachineDescriptionV1.IssueWidth)
                {
                    structuralRejects++;
                    continue;
                }
                CandidateEvaluation evaluation = EvaluateCandidate(selected, topologyModel, options.OptionsDigest);
                placements += evaluation.PlacementEvaluated;
                if (evaluation.Status == CandidateStatus.StructuralRejected)
                {
                    structuralRejects++;
                    continue;
                }
                if (evaluation.Status == CandidateStatus.ResourceRejected)
                {
                    resourceRejects++;
                    continue;
                }
                Candidate candidate = evaluation.Candidate!;
                if (best is null || IsBetter(candidate, best)) best = candidate;
            }
            if (best is null)
                return Fallback(
                    IrVtStaticPlanStatusV1.UnknownFallback,
                    ordered,
                    topologyModel.Topology.ContractDigest,
                    options.OptionsDigest,
                    fallbackDigest,
                    "HCVT1011",
                    "No complete structural/topology candidate exists; no partial cross-VT plan is retained.",
                    new(evaluated, structuralRejects, resourceRejects, placements, evaluated + placements));

            IrVtStaticCycleMemberV1[] members = best.Members.Select((member, index) => new IrVtStaticCycleMemberV1(
                member.VirtualThreadId,
                member.Original.Index,
                member.Original.StableIdentity,
                member.LocalCarrierOrdinal,
                best.Slots[index])).ToArray();
            jointCycles.Add(new(
                jointCycles.Count,
                members,
                IrVtClassCompatibilityEvidenceV1.StructurallyCompatible,
                IrVtSlotCompatibilityEvidenceV1.ExactW8StructuralPlacement,
                best.TopologyDigest,
                best.PlacementDigest));
            foreach (StreamState state in best.SelectedStates) state.Position++;
        }

        IrVtStaticPlanningCountersV1 counters = new IrVtStaticPlanningCountersV1(
            evaluated,
            structuralRejects,
            resourceRejects,
            placements,
            evaluated + placements);
        int saved = Math.Max(0, localCarriers - jointCycles.Count);
        string planDigest = PlanDigest(
            IrVtStaticPlanStatusV1.Accepted,
            fallbackDigest,
            topologyModel.Topology.ContractDigest,
            options.OptionsDigest,
            jointCycles,
            counters);
        return new(
            IrVtStaticPlanStatusV1.Accepted,
            ordered,
            jointCycles,
            localCarriers,
            jointCycles.Count,
            saved,
            fallbackDigest,
            topologyModel.Topology.ContractDigest,
            options.OptionsDigest,
            counters,
            HybridCpuVtSchedulingContractV1.EvidenceOnlyHeader,
            HybridCpuVtSchedulingContractV1.RuntimeBoundary,
            Array.Empty<IrVtPlanningDiagnosticV1>(),
            planDigest);
    }

    private static StreamState BuildState(IrVtLocalScheduleEvidenceV1 stream)
    {
        List<LocalCarrier> carriers = new List<LocalCarrier>();
        int instructionOrdinal = 0;
        int carrierOrdinal = 0;
        foreach (IrBasicBlockSchedule block in stream.LocalSchedule.BlockSchedules)
        {
            foreach (IrScheduleCycleGroup group in block.CycleGroups.OrderBy(static group => group.Cycle))
            {
                List<LocalMember> members = new List<LocalMember>();
                foreach (IrInstruction instruction in group.Instructions
                             .OrderBy(static instruction => instruction.Index))
                {
                    int syntheticIndex = checked(stream.VirtualThreadId * 2048 + instructionOrdinal++);
                    members.Add(new(
                        stream.VirtualThreadId,
                        instruction,
                        instruction with { Index = syntheticIndex },
                        carrierOrdinal));
                }
                carriers.Add(new(carrierOrdinal++, members));
            }
        }
        return new(stream, carriers);
    }

    private static CandidateEvaluation EvaluateCandidate(
        IReadOnlyList<StreamState> selected,
        HybridCpuTopologyResourceModelV1 topologyModel,
        string optionsDigest)
    {
        LocalMember[] members = selected
            .SelectMany(static state => state.Carriers[state.Position].Members)
            .OrderBy(static member => member.VirtualThreadId)
            .ThenBy(static member => member.Original.Index).ToArray();
        IrInstruction[] structural = members.Select(static member => member.Structural).ToArray();
        HybridCpuInstructionLegalityChecker checker = new HybridCpuInstructionLegalityChecker();
        if (checker.AnalyzeStructuralCandidateBundle(structural).HazardCount != 0)
            return new(CandidateStatus.StructuralRejected, null, 0);

        IrBundlePlacementSearchResult placement;
        try
        {
            placement = HybridCpuExistingPlacementBackendV1.Instance.SearchStructuralAssignments(
                structural.Select(static instruction => instruction.Annotation.StructurallyAllowedSlots).ToArray());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new(CandidateStatus.StructuralRejected, null, 0);
        }
        if (placement.BestInstructionSlots.Count != structural.Length)
            return new(CandidateStatus.StructuralRejected, null, 1);

        HybridCpuTopologyCycleStateV1 topologyState = HybridCpuTopologyCycleStateV1.Empty(topologyModel.Topology);
        List<string> footprintDigests = new List<string>();
        foreach (IrInstruction instruction in structural)
        {
            IrTopologyResourceFootprintV1 footprint = topologyModel.GetResourceFootprint(instruction);
            HybridCpuTopologyReservationResultV1 reservation = topologyModel.CanReserve(topologyState, footprint);
            if (reservation.Decision != CompilerTopologyShadowDecisionV1.Allowed)
                return new(CandidateStatus.ResourceRejected, null, 1);
            topologyState = topologyModel.Reserve(topologyState, footprint);
            footprintDigests.Add(footprint.Fingerprint);
        }
        string topologyDigest = HybridCpuVtSchedulingContractV1.Hash(string.Join('|', footprintDigests));
        IrCyclePlacementWitness witness = new IrCyclePlacementWitness(
            HybridCpuMachineDescriptionV1.Default.Key,
            optionsDigest,
            structural.Select(static instruction => instruction.Index).ToArray(),
            placement.BestInstructionSlots);
        return new(
            CandidateStatus.Accepted,
            new(
                selected.ToArray(),
                members,
                placement.BestInstructionSlots.ToArray(),
                topologyDigest,
                witness.Fingerprint,
                placement.BestQuality),
            1);
    }

    private static IrVtStaticPlanStatusV1 ValidateLocalStream(IrVtLocalScheduleEvidenceV1 stream)
    {
        if (!string.Equals(stream.ProgramDigest,
                CompilerScheduleFingerprintV1.HashProgramInput(stream.LocalSchedule.Program),
                StringComparison.Ordinal) ||
            !string.Equals(stream.ScheduleDigest,
                CompilerScheduleFingerprintV1.HashSchedule(stream.LocalSchedule),
                StringComparison.Ordinal))
            return IrVtStaticPlanStatusV1.StaleProof;
        IrProgram program = stream.LocalSchedule.Program;
        if (program.VirtualThreadId != stream.VirtualThreadId ||
            program.Instructions.Any(instruction => instruction.VirtualThreadId != stream.VirtualThreadId) ||
            CanonicalIrFrontendBoundaryV1.Validate(program).Status != IrFrontendAdapterStatus.Success)
            return IrVtStaticPlanStatusV1.Ineligible;

        IrInstruction[] scheduled = stream.LocalSchedule.BlockSchedules
            .SelectMany(static block => block.CycleGroups)
            .SelectMany(static group => group.Instructions).ToArray();
        if (scheduled.Length != program.Instructions.Count ||
            scheduled.Select(static instruction => instruction.Index).Distinct().Count() != scheduled.Length ||
            scheduled.Any(instruction => !program.Instructions.Any(original =>
                original.Index == instruction.Index &&
                string.Equals(original.StableIdentity, instruction.StableIdentity, StringComparison.Ordinal))) ||
            stream.LocalCarrierCount != stream.LocalSchedule.BlockSchedules.Sum(static block => block.CycleGroups.Count))
            return IrVtStaticPlanStatusV1.Ineligible;
        return IrVtStaticPlanStatusV1.Accepted;
    }

    private static bool ValidateRelationshipProofs(
        IReadOnlyList<IrVtLocalScheduleEvidenceV1> streams,
        IReadOnlyList<IrCrossVtRelationshipProofV1> proofs)
    {
        int required = checked(streams.Count * (streams.Count - 1) / 2);
        if (proofs.Count != required) return false;
        HashSet<(byte Left, byte Right)> seen = new HashSet<(byte Left, byte Right)>();
        for (int left = 0; left < streams.Count; left++)
        {
            for (int right = left + 1; right < streams.Count; right++)
            {
                IrCrossVtRelationshipProofV1 expected = IrCrossVtRelationshipProofV1.CreateIndependent(
                    streams[left], streams[right]);
                IrCrossVtRelationshipProofV1? actual = proofs.SingleOrDefault(proof =>
                    proof.LeftVirtualThreadId == expected.LeftVirtualThreadId &&
                    proof.RightVirtualThreadId == expected.RightVirtualThreadId);
                if (actual is null || !seen.Add((actual.LeftVirtualThreadId, actual.RightVirtualThreadId)) ||
                    actual != expected)
                    return false;
            }
        }
        return true;
    }

    private static bool HasUnsupportedCrossVtSemantics(IReadOnlyList<IrVtLocalScheduleEvidenceV1> streams)
    {
        foreach (IrInstruction instruction in streams.SelectMany(static stream => stream.LocalSchedule.Program.Instructions))
        {
            IrMemoryEffectKind memory = instruction.SideEffects.Memory.Kind;
            if (instruction.SideEffects.ArchitecturalEffects != IrArchitecturalEffectKind.None ||
                instruction.Annotation.Serialization != IrSerializationKind.None ||
                instruction.Annotation.IsBarrierLike ||
                instruction.Annotation.MayTrap ||
                instruction.Annotation.ControlFlowKind != IrControlFlowKind.None ||
                (memory & (IrMemoryEffectKind.Atomic | IrMemoryEffectKind.Volatile |
                    IrMemoryEffectKind.Fence | IrMemoryEffectKind.Unknown)) != 0 ||
                instruction.SideEffects.Memory.AddressSpace is IrAddressSpaceIdentity.ThreadLocal or
                    IrAddressSpaceIdentity.Device or IrAddressSpaceIdentity.Unknown)
                return true;
        }
        for (int left = 0; left < streams.Count; left++)
        {
            for (int right = left + 1; right < streams.Count; right++)
            {
                if (streams[left].LocalSchedule.Program.Instructions.Any(leftInstruction =>
                    streams[right].LocalSchedule.Program.Instructions.Any(rightInstruction =>
                        CrossMemoryRequiresOrdering(
                            leftInstruction.SideEffects.Memory,
                            rightInstruction.SideEffects.Memory))))
                    return true;
            }
        }
        return false;
    }

    private static bool CrossMemoryRequiresOrdering(
        IrCanonicalMemoryEffectV1 left,
        IrCanonicalMemoryEffectV1 right)
    {
        if (left.Kind == IrMemoryEffectKind.None || right.Kind == IrMemoryEffectKind.None)
            return false;
        if (left.AddressSpace != right.AddressSpace)
            return false;
        IrMemoryEffectKind writes = IrMemoryEffectKind.Write | IrMemoryEffectKind.Atomic |
            IrMemoryEffectKind.Volatile | IrMemoryEffectKind.Fence;
        if ((left.Kind & writes) == 0 && (right.Kind & writes) == 0)
            return false;
        IrMemoryRegion[] leftRegions = new[] { left.ReadRegion, left.WriteRegion }
            .Where(static region => region is not null).Cast<IrMemoryRegion>().ToArray();
        IrMemoryRegion[] rightRegions = new[] { right.ReadRegion, right.WriteRegion }
            .Where(static region => region is not null).Cast<IrMemoryRegion>().ToArray();
        if (leftRegions.Length == 0 || rightRegions.Length == 0)
            return true;
        return leftRegions.Any(leftRegion => rightRegions.Any(rightRegion => RegionsOverlap(leftRegion, rightRegion)));
    }

    private static bool RegionsOverlap(IrMemoryRegion left, IrMemoryRegion right)
    {
        ulong leftLength = Math.Max(1U, left.Length);
        ulong rightLength = Math.Max(1U, right.Length);
        ulong leftEnd = left.Address > ulong.MaxValue - (leftLength - 1)
            ? ulong.MaxValue : left.Address + leftLength - 1;
        ulong rightEnd = right.Address > ulong.MaxValue - (rightLength - 1)
            ? ulong.MaxValue : right.Address + rightLength - 1;
        return left.Address <= rightEnd && right.Address <= leftEnd;
    }

    private static bool HasValidOptions(HybridCpuVtStaticPlanningOptionsV1 options)
    {
        HybridCpuVtStaticPlanningBudgetsV1 budgets = options.Budgets;
        if (budgets is null || budgets.MaximumStreams is < 1 or > 4 ||
            budgets.MaximumLocalCarriers is < 1 or > 256 ||
            budgets.MaximumOperations is < 1 or > 1024 ||
            budgets.MaximumSubsetEvaluations is < 1 or > 4096)
            return false;
        return options == HybridCpuVtStaticPlanningOptionsV1.Create(options.PlanningSwitch, budgets);
    }

    private static bool IsBetter(Candidate candidate, Candidate current)
    {
        if (candidate.SelectedStates.Count != current.SelectedStates.Count)
            return candidate.SelectedStates.Count > current.SelectedStates.Count;
        if (candidate.Members.Count != current.Members.Count)
            return candidate.Members.Count > current.Members.Count;
        int quality = CompareQuality(candidate.Quality, current.Quality);
        if (quality != 0) return quality < 0;
        string candidateKey = string.Join(',', candidate.SelectedStates.Select(static state => state.Stream.VirtualThreadId));
        string currentKey = string.Join(',', current.SelectedStates.Select(static state => state.Stream.VirtualThreadId));
        return string.CompareOrdinal(candidateKey, currentKey) < 0;
    }

    private static int CompareQuality(IrBundlePlacementQuality left, IrBundlePlacementQuality right)
    {
        int comparison = left.InternalGapCount.CompareTo(right.InternalGapCount);
        if (comparison != 0) return comparison;
        comparison = left.OrderInversionCount.CompareTo(right.OrderInversionCount);
        if (comparison != 0) return comparison;
        comparison = left.ConstrainedSlotDisplacementCost.CompareTo(right.ConstrainedSlotDisplacementCost);
        return comparison != 0 ? comparison : left.SlotIndexSum.CompareTo(right.SlotIndexSum);
    }

    private static string LocalBaselineDigest(IReadOnlyList<IrVtLocalScheduleEvidenceV1> streams)
    {
        StringBuilder payload = new StringBuilder("hybridcpu.vt-local-fallback/v1");
        foreach (IrVtLocalScheduleEvidenceV1 stream in streams)
            payload.Append('|').Append(stream.VirtualThreadId)
                .Append(':').Append(stream.ProgramDigest)
                .Append(':').Append(stream.ScheduleDigest)
                .Append(':').Append(stream.LocalCarrierCount);
        return HybridCpuVtSchedulingContractV1.Hash(payload.ToString());
    }

    private static string PlanDigest(
        IrVtStaticPlanStatusV1 status,
        string fallbackDigest,
        string topologyDigest,
        string optionsDigest,
        IReadOnlyList<IrVtStaticJointCycleV1> cycles,
        IrVtStaticPlanningCountersV1 counters)
    {
        StringBuilder payload = new StringBuilder(HybridCpuVtSchedulingContractV1.SchemaId)
            .Append('|').Append((byte)status)
            .Append('|').Append(fallbackDigest)
            .Append('|').Append(topologyDigest)
            .Append('|').Append(optionsDigest)
            .Append('|').Append(counters.SubsetsEvaluated)
            .Append(':').Append(counters.StructuralRejects)
            .Append(':').Append(counters.SharedResourceRejects)
            .Append(':').Append(counters.ExactPlacementsEvaluated)
            .Append(':').Append(counters.DeterministicWorkUnits);
        foreach (IrVtStaticJointCycleV1 cycle in cycles)
        {
            payload.Append('|').Append(cycle.JointCycle)
                .Append(':').Append(cycle.TopologyEvidenceDigest)
                .Append(':').Append(cycle.PlacementEvidenceDigest);
            foreach (IrVtStaticCycleMemberV1 member in cycle.Members)
                payload.Append(':').Append(member.VirtualThreadId)
                    .Append('/').Append(member.InstructionIndex)
                    .Append('/').Append(member.LocalCarrierOrdinal)
                    .Append('/').Append(member.StructuralSlot);
        }
        return HybridCpuVtSchedulingContractV1.Hash(payload.ToString());
    }

    private static IrVtStaticPlanV1 Fallback(
        IrVtStaticPlanStatusV1 status,
        IReadOnlyList<IrVtLocalScheduleEvidenceV1> streams,
        string topologyDigest,
        string optionsDigest,
        string fallbackDigest,
        string code,
        string message,
        IrVtStaticPlanningCountersV1? counters = null)
    {
        counters ??= new(0, 0, 0, 0, 0);
        int localCarriers = streams.Sum(static stream => stream.LocalCarrierCount);
        string digest = PlanDigest(
            status,
            fallbackDigest,
            topologyDigest,
            optionsDigest,
            Array.Empty<IrVtStaticJointCycleV1>(),
            counters);
        return new(
            status,
            streams,
            Array.Empty<IrVtStaticJointCycleV1>(),
            localCarriers,
            localCarriers,
            0,
            fallbackDigest,
            topologyDigest,
            optionsDigest,
            counters,
            HybridCpuVtSchedulingContractV1.EvidenceOnlyHeader,
            HybridCpuVtSchedulingContractV1.RuntimeBoundary,
            [new(code, message)],
            digest);
    }

    private enum CandidateStatus : byte { Accepted, StructuralRejected, ResourceRejected }

    private sealed record LocalMember(
        byte VirtualThreadId,
        IrInstruction Original,
        IrInstruction Structural,
        int LocalCarrierOrdinal);

    private sealed record LocalCarrier(int Ordinal, IReadOnlyList<LocalMember> Members);

    private sealed class StreamState(
        IrVtLocalScheduleEvidenceV1 stream,
        IReadOnlyList<LocalCarrier> carriers)
    {
        public IrVtLocalScheduleEvidenceV1 Stream { get; } = stream;
        public IReadOnlyList<LocalCarrier> Carriers { get; } = carriers;
        public int Position { get; set; }
    }

    private sealed record Candidate(
        IReadOnlyList<StreamState> SelectedStates,
        IReadOnlyList<LocalMember> Members,
        IReadOnlyList<int> Slots,
        string TopologyDigest,
        string PlacementDigest,
        IrBundlePlacementQuality Quality);

    private readonly record struct CandidateEvaluation(
        CandidateStatus Status,
        Candidate? Candidate,
        int PlacementEvaluated);
}
