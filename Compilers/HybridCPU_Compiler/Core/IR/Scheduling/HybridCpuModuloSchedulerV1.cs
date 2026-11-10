using System.Globalization;
using System.Numerics;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU.Compiler.Core.IR;

public sealed class HybridCpuModuloSchedulerV1
{
    public IrModuloScheduleResultV1 Schedule(
        IrProgram program,
        IrCanonicalLoopV1 loop,
        IrLoopMiiReportV1 mii,
        HybridCpuMiiResourceModelV1? resourceModel = null,
        HybridCpuModuloSchedulerOptionsV1? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(mii);
        resourceModel ??= HybridCpuMiiResourceModelV1.Default;
        options ??= HybridCpuModuloSchedulerOptionsV1.Production;
        if (!HasValidOptions(options))
            return Rejected(IrModuloScheduleStatusV1.UnsupportedSemanticOrResourceFact,
                "HCMS0001", "Modulo scheduler options digest does not bind its deterministic budgets.", options);

        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrLoopMiiReportV1 canonicalMii = analyzer.ComputeMii(program, loop, resourceModel);
        if (canonicalMii.Eligibility == IrLoopMiiEligibilityV1.StaleProof)
            return Rejected(IrModuloScheduleStatusV1.StaleProof,
                "HCMS0002", "Loop subject or MII input is stale.", options);
        if (canonicalMii.Eligibility != IrLoopMiiEligibilityV1.EligibleLowerBound ||
            !MiiComponentsEqual(mii, canonicalMii))
            return Rejected(IrModuloScheduleStatusV1.UnsupportedSemanticOrResourceFact,
                "HCMS0003", "A complete canonical Phase 13 MII family is required.", options);

        IrLoopCanonicalizationResultV1 canonicalization = analyzer.Canonicalize(program, resourceModel);
        IrCanonicalLoopV1? subject = canonicalization.Loops.SingleOrDefault(candidate =>
            string.Equals(candidate.LoopId, loop.LoopId, StringComparison.Ordinal));
        if (subject is null || subject.Status != IrCanonicalLoopStatusV1.Qualified)
            return Rejected(IrModuloScheduleStatusV1.StaleProof,
                "HCMS0002", "Canonical loop subject cannot be re-derived.", options);

        int lowerBound = canonicalMii.ProvenLowerBoundIi!.Value;
        WorkCounters counters = new WorkCounters(options.Budgets);
        List<IrModuloInfeasibleIiReasonV1> attempts = new List<IrModuloInfeasibleIiReasonV1>();
        for (int attempt = 0; attempt < options.Budgets.MaximumIiAttempts; attempt++)
        {
            int ii;
            try
            {
                ii = checked(lowerBound + attempt);
            }
            catch (OverflowException)
            {
                return BudgetResult(attempts, options, "II range overflowed deterministic integer bounds.");
            }

            TemporalSolution temporal = SolveTemporal(subject, ii, counters);
            if (temporal.Status == IrModuloScheduleStatusV1.BudgetExhausted)
                return BudgetResult(attempts, options, temporal.Reason);
            if (temporal.Status != IrModuloScheduleStatusV1.Feasible)
            {
                attempts.Add(CreateAttempt(ii, temporal.Status, temporal.BindingFacts, counters, temporal.Reason));
                continue;
            }

            int peakPressure = PeakExactRegisterGroupPressure(subject);
            int pressureCapacity = checked(HybridCpuMachineTopologyV1.RegistersPerGroup * ii);
            if (peakPressure > pressureCapacity)
            {
                attempts.Add(CreateAttempt(ii, IrModuloScheduleStatusV1.LifetimePressureInfeasible,
                    [$"peak-group-pressure:{I(peakPressure)}", $"capacity-at-ii:{I(pressureCapacity)}"], counters,
                    "Phase 07 exact register-group pressure exceeds the conservative capacity at this II."));
                continue;
            }

            counters.RefinementIterations++;
            if (counters.RefinementIterations > options.Budgets.MaximumRefinementIterations)
                return BudgetResult(attempts, options, "Modulo refinement-iteration budget exhausted.");
            SearchOutcome search = SearchAssignments(subject, resourceModel, ii, temporal.EarliestCycles!, counters);
            if (search.Status == IrModuloScheduleStatusV1.BudgetExhausted)
                return BudgetResult(attempts, options, search.Reason);
            if (search.Status != IrModuloScheduleStatusV1.Feasible || search.Cycles is null)
            {
                attempts.Add(CreateAttempt(ii, search.Status, search.BindingFacts, counters, search.Reason));
                continue;
            }

            IrModuloScheduleWitnessV1 witness = BuildWitness(
                subject, canonicalMii, resourceModel, options, ii, search.Cycles, peakPressure);
            IrModuloWitnessValidationV1 validation = ValidateWitness(
                program, subject, canonicalMii, witness, resourceModel, options);
            if (!validation.IsValid)
            {
                IrModuloScheduleStatusV1 status = validation.Status == IrModuloScheduleStatusV1.BudgetExhausted
                    ? IrModuloScheduleStatusV1.BudgetExhausted
                    : IrModuloScheduleStatusV1.ExactPlacementInfeasible;
                if (status == IrModuloScheduleStatusV1.BudgetExhausted)
                    return BudgetResult(attempts, options, "Independent witness validation exhausted its budget.");
                attempts.Add(CreateAttempt(ii, status,
                    validation.Diagnostics.Select(static diagnostic => diagnostic.Code).ToArray(), counters,
                    "Independently revalidated witness was rejected."));
                continue;
            }

            string resultDigest = ResultDigest(IrModuloScheduleStatusV1.Feasible, ii, witness.WitnessDigest, attempts, options);
            return new(
                IrModuloScheduleStatusV1.Feasible,
                ii,
                witness,
                attempts.ToArray(),
                Array.Empty<IrRegionSchedulingDiagnosticV1>(),
                IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly,
                resultDigest);
        }

        IrModuloScheduleStatusV1 terminal = attempts.LastOrDefault()?.Status ??
            IrModuloScheduleStatusV1.UnsupportedSemanticOrResourceFact;
        string reason = "All deterministic II attempts were proven infeasible within the configured range.";
        return new(
            terminal,
            null,
            null,
            attempts.ToArray(),
            [new("HCMS1001", reason)],
            IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly,
            ResultDigest(terminal, null, "absent", attempts, options));
    }

    public IrModuloWitnessValidationV1 ValidateWitness(
        IrProgram program,
        IrCanonicalLoopV1 loop,
        IrLoopMiiReportV1 mii,
        IrModuloScheduleWitnessV1 witness,
        HybridCpuMiiResourceModelV1? resourceModel = null,
        HybridCpuModuloSchedulerOptionsV1? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(mii);
        ArgumentNullException.ThrowIfNull(witness);
        resourceModel ??= HybridCpuMiiResourceModelV1.Default;
        options ??= HybridCpuModuloSchedulerOptionsV1.Production;
        List<IrRegionSchedulingDiagnosticV1> diagnostics = new List<IrRegionSchedulingDiagnosticV1>();
        if (!HasValidOptions(options))
            return Invalid(IrModuloScheduleStatusV1.UnsupportedSemanticOrResourceFact,
                "HCMS2001", "Invalid deterministic options binding.", diagnostics);

        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrLoopMiiReportV1 canonicalMii = analyzer.ComputeMii(program, loop, resourceModel);
        IrLoopCanonicalizationResultV1 canonicalization = analyzer.Canonicalize(program, resourceModel);
        IrCanonicalLoopV1? subject = canonicalization.Loops.SingleOrDefault(candidate =>
            string.Equals(candidate.LoopId, loop.LoopId, StringComparison.Ordinal));
        if (subject is null || canonicalMii.Eligibility != IrLoopMiiEligibilityV1.EligibleLowerBound ||
            !MiiComponentsEqual(mii, canonicalMii))
            return Invalid(IrModuloScheduleStatusV1.StaleProof,
                "HCMS2002", "Loop or MII proof cannot be canonically re-derived.", diagnostics);
        if (witness.SchemaId != HybridCpuModuloSchedulingContractV1.SchemaId ||
            witness.LoopId != subject.LoopId || witness.LoopVersionStamp != subject.VersionStamp ||
            witness.MiiProofDigest != canonicalMii.ProofStamp.ProofDigest ||
            witness.TargetDigest != HybridCpuTargetMachineContractV1.Default.ContractDigest ||
            witness.ResourceModelDigest != resourceModel.ModelDigest ||
            witness.SchedulerContractDigest != HybridCpuModuloSchedulingContractV1.Default.ContractDigest ||
            witness.OptionsDigest != options.OptionsDigest ||
            witness.OutputDisposition != IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly ||
            witness.InitiationInterval < canonicalMii.ProvenLowerBoundIi)
            return Invalid(IrModuloScheduleStatusV1.StaleProof,
                "HCMS2003", "Witness provenance or lower-bound binding is stale.", diagnostics);

        Dictionary<int, IrModuloScheduledOperationV1> operations;
        try
        {
            operations = witness.Operations.ToDictionary(static operation => operation.InstructionIndex);
        }
        catch (ArgumentException)
        {
            return Invalid(IrModuloScheduleStatusV1.ExactPlacementInfeasible,
                "HCMS2004", "Witness contains duplicate instruction identities.", diagnostics);
        }
        if (operations.Count != subject.Instructions.Count || subject.Instructions.Any(instruction =>
                !operations.TryGetValue(instruction.Index, out IrModuloScheduledOperationV1? operation) ||
                operation.InstructionIdentity != instruction.StableIdentity || operation.Cycle < 0 ||
                operation.ModuloCycle != operation.Cycle % witness.InitiationInterval))
            return Invalid(IrModuloScheduleStatusV1.ExactPlacementInfeasible,
                "HCMS2005", "Witness operation coverage, identity or modulo-cycle equation is invalid.", diagnostics);
        string[] expectedEdgeRefs = subject.DistanceDependencies.Select(static edge => edge.EdgeId)
            .Order(StringComparer.Ordinal).ToArray();
        string[] expectedMiiRefs = canonicalMii.Components.Select(ComponentProofRef)
            .Order(StringComparer.Ordinal).ToArray();
        if (!expectedEdgeRefs.SequenceEqual(witness.DependenceProofRefs.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            !expectedMiiRefs.SequenceEqual(witness.MiiProofRefs.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            return Invalid(IrModuloScheduleStatusV1.StaleProof,
                "HCMS2013", "Witness dependence or MII proof references are stale.", diagnostics);

        foreach (IrLoopDistanceDependencyV1 edge in subject.DistanceDependencies)
        {
            if (!operations.TryGetValue(edge.ProducerInstructionIndex, out IrModuloScheduledOperationV1? producer) ||
                !operations.TryGetValue(edge.ConsumerInstructionIndex, out IrModuloScheduledOperationV1? consumer))
                return Invalid(IrModuloScheduleStatusV1.StaleProof,
                    "HCMS2006", "Distance edge endpoint is missing from the witness.", diagnostics);
            long required = (long)edge.LatencyCycles - ((long)edge.IterationDistance * witness.InitiationInterval);
            if ((long)consumer.Cycle - producer.Cycle < required)
                return Invalid(IrModuloScheduleStatusV1.TemporalInfeasible,
                    "HCMS2007", $"Temporal constraint failed for edge {edge.EdgeId}.", diagnostics);
        }

        WorkCounters validationCounters = new WorkCounters(options.Budgets);
        List<IrModuloResourceReservationV1> expectedReservations = new List<IrModuloResourceReservationV1>();
        foreach (IGrouping<int, IrModuloScheduledOperationV1> group in witness.Operations
                     .GroupBy(static operation => operation.ModuloCycle).OrderBy(static group => group.Key))
        {
            IrInstruction[] instructions = group.OrderBy(static operation => operation.InstructionIndex)
                .Select(operation => subject.Instructions.Single(instruction => instruction.Index == operation.InstructionIndex))
                .ToArray();
            GroupEvaluation evaluation = EvaluateGroup(subject, instructions, resourceModel, validationCounters);
            if (evaluation.Status != IrModuloScheduleStatusV1.Feasible || evaluation.Reservation is null)
                return Invalid(evaluation.Status, "HCMS2008", evaluation.Reason, diagnostics);
            int[] chosenSlots = group.OrderBy(static operation => operation.InstructionIndex)
                .Select(static operation => operation.IssueSlot).ToArray();
            if (!ChosenSlotsAreStructural(instructions, chosenSlots) ||
                !chosenSlots.SequenceEqual(evaluation.Placement!.BestInstructionSlots))
                return Invalid(IrModuloScheduleStatusV1.ExactPlacementInfeasible,
                    "HCMS2009", "Witness issue slots are not an exact W=8 structural assignment.", diagnostics);
            expectedReservations.Add(evaluation.Reservation with { ModuloCycle = group.Key });
        }
        if (!ReservationProjection(expectedReservations).SequenceEqual(
                ReservationProjection(witness.ResourceReservations), StringComparer.Ordinal))
            return Invalid(IrModuloScheduleStatusV1.StaleProof,
                "HCMS2010", "Witness resource reservations do not match independent reconstruction.", diagnostics);

        IrModuloLifetimePressureSummaryV1 expectedPressure = BuildLifetimeSummary(
            subject, operations.Values, witness.InitiationInterval, PeakExactRegisterGroupPressure(subject));
        if (expectedPressure != witness.LifetimePressure)
            return Invalid(IrModuloScheduleStatusV1.StaleProof,
                "HCMS2011", "Witness lifetime/pressure summary is stale.", diagnostics);
        string expectedDigest = WitnessDigest(witness with { WitnessDigest = string.Empty });
        if (witness.WitnessDigest != expectedDigest)
            return Invalid(IrModuloScheduleStatusV1.StaleProof,
                "HCMS2012", "Witness digest does not bind its contents.", diagnostics);
        string validationDigest = HybridCpuModuloSchedulingContractV1.Hash(
            $"hybridcpu.modulo-witness-validation/v1|{witness.WitnessDigest}|valid");
        return new(IrModuloScheduleStatusV1.Feasible, Array.Empty<IrRegionSchedulingDiagnosticV1>(), validationDigest);
    }

    /// <summary>
    /// Canonicalizes an externally selected cycle assignment into a versioned witness and then
    /// revalidates it. This is a shared semantic validator for independent research/CI search
    /// backends; it does not invoke the production assignment search or mutate compiler output.
    /// </summary>
    public IrModuloCandidateWitnessResultV1 CreateValidatedWitness(
        IrProgram program,
        IrCanonicalLoopV1 loop,
        IrLoopMiiReportV1 mii,
        int initiationInterval,
        IReadOnlyDictionary<int, int> cycles,
        HybridCpuMiiResourceModelV1? resourceModel = null,
        HybridCpuModuloSchedulerOptionsV1? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(mii);
        ArgumentNullException.ThrowIfNull(cycles);
        resourceModel ??= HybridCpuMiiResourceModelV1.Default;
        options ??= HybridCpuModuloSchedulerOptionsV1.Production;
        if (!HasValidOptions(options))
            return CandidateRejected(IrModuloScheduleStatusV1.UnsupportedSemanticOrResourceFact,
                "HCMS3001", "Candidate options digest does not bind its deterministic budgets.");

        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrLoopMiiReportV1 canonicalMii = analyzer.ComputeMii(program, loop, resourceModel);
        IrLoopCanonicalizationResultV1 canonicalization = analyzer.Canonicalize(program, resourceModel);
        IrCanonicalLoopV1? subject = canonicalization.Loops.SingleOrDefault(candidate =>
            string.Equals(candidate.LoopId, loop.LoopId, StringComparison.Ordinal));
        if (subject is null || canonicalMii.Eligibility != IrLoopMiiEligibilityV1.EligibleLowerBound ||
            !MiiComponentsEqual(mii, canonicalMii))
            return CandidateRejected(IrModuloScheduleStatusV1.StaleProof,
                "HCMS3002", "Candidate loop or MII proof cannot be canonically re-derived.");
        if (initiationInterval < canonicalMii.ProvenLowerBoundIi ||
            cycles.Count != subject.Instructions.Count ||
            subject.Instructions.Any(instruction => !cycles.TryGetValue(instruction.Index, out int cycle) || cycle < 0))
            return CandidateRejected(IrModuloScheduleStatusV1.ExactPlacementInfeasible,
                "HCMS3003", "Candidate II, operation coverage or cycle domain is invalid.");

        int peakPressure = PeakExactRegisterGroupPressure(subject);
        if (peakPressure > checked(HybridCpuMachineTopologyV1.RegistersPerGroup * initiationInterval))
            return CandidateRejected(IrModuloScheduleStatusV1.LifetimePressureInfeasible,
                "HCMS3004", "Candidate II violates the Phase 07 conservative register-group pressure bound.");

        WorkCounters counters = new WorkCounters(options.Budgets);
        foreach (IGrouping<int, KeyValuePair<int, int>> group in cycles.GroupBy(pair => pair.Value % initiationInterval))
        {
            IrInstruction[] instructions = group.OrderBy(static pair => pair.Key)
                .Select(pair => subject.Instructions.Single(instruction => instruction.Index == pair.Key)).ToArray();
            GroupEvaluation evaluation = EvaluateGroup(subject, instructions, resourceModel, counters);
            if (evaluation.Status != IrModuloScheduleStatusV1.Feasible)
                return CandidateRejected(evaluation.Status, "HCMS3005", evaluation.Reason);
        }

        IrModuloScheduleWitnessV1 witness = BuildWitness(
            subject, canonicalMii, resourceModel, options, initiationInterval, cycles, peakPressure);
        IrModuloWitnessValidationV1 validation = ValidateWitness(
            program, subject, canonicalMii, witness, resourceModel, options);
        return new(validation.Status, witness, validation);
    }

    private static TemporalSolution SolveTemporal(IrCanonicalLoopV1 loop, int ii, WorkCounters counters)
    {
        int[] indexes = loop.Instructions.Select(static instruction => instruction.Index).Order().ToArray();
        HashSet<int> indexSet = indexes.ToHashSet();
        IrLoopDistanceDependencyV1[] edges = loop.DistanceDependencies
            .OrderBy(static edge => edge.ProducerInstructionIndex)
            .ThenBy(static edge => edge.ConsumerInstructionIndex)
            .ThenBy(static edge => edge.EdgeId, StringComparer.Ordinal).ToArray();
        if (edges.Any(edge => !indexSet.Contains(edge.ProducerInstructionIndex) ||
                !indexSet.Contains(edge.ConsumerInstructionIndex) || edge.IterationDistance < 0))
            return new(IrModuloScheduleStatusV1.UnsupportedSemanticOrResourceFact, null,
                ["invalid-distance-edge"], "Distance DAG contains an invalid endpoint or distance.");
        Dictionary<int, int> cycles = indexes.ToDictionary(static index => index, static _ => 0);
        for (int pass = 0; pass < indexes.Length; pass++)
        {
            bool changed = false;
            foreach (IrLoopDistanceDependencyV1 edge in edges)
            {
                counters.SdcRelaxations++;
                if (counters.SdcRelaxations > counters.Budgets.MaximumSdcRelaxations)
                    return new(IrModuloScheduleStatusV1.BudgetExhausted, null,
                        [edge.EdgeId], "SDC relaxation budget exhausted.");
                long required = (long)cycles[edge.ProducerInstructionIndex] + edge.LatencyCycles -
                    ((long)edge.IterationDistance * ii);
                if (required <= cycles[edge.ConsumerInstructionIndex]) continue;
                if (required > int.MaxValue)
                    return new(IrModuloScheduleStatusV1.UnsupportedSemanticOrResourceFact, null,
                        [edge.EdgeId], "Temporal cycle value overflowed deterministic integer bounds.");
                cycles[edge.ConsumerInstructionIndex] = (int)required;
                changed = true;
                if (pass == indexes.Length - 1)
                    return new(IrModuloScheduleStatusV1.TemporalInfeasible, null,
                        [edge.EdgeId], "Positive temporal constraint cycle proves this II infeasible.");
            }
            if (!changed) break;
        }
        int minimum = cycles.Values.Min();
        if (minimum < 0)
        {
            foreach (int index in indexes) cycles[index] -= minimum;
        }
        return new(IrModuloScheduleStatusV1.Feasible, cycles,
            Array.Empty<string>(), "Temporal SDC constraints have a deterministic earliest solution.");
    }

    private static SearchOutcome SearchAssignments(
        IrCanonicalLoopV1 loop,
        HybridCpuMiiResourceModelV1 model,
        int ii,
        IReadOnlyDictionary<int, int> earliest,
        WorkCounters counters)
    {
        IrInstruction[] order = loop.Instructions.OrderBy(instruction =>
                BitOperations.PopCount((uint)instruction.Annotation.StructurallyAllowedSlots))
            .ThenBy(instruction => earliest[instruction.Index])
            .ThenBy(static instruction => instruction.Index).ToArray();
        int maximumCycle;
        try
        {
            int subjectBound = checked(ii * Math.Max(1, loop.Instructions.Count));
            int deterministicSpan = Math.Min(counters.Budgets.MaximumScheduleSpanCycles, subjectBound);
            maximumCycle = checked(earliest.Values.Max() + deterministicSpan);
        }
        catch (OverflowException)
        {
            return new(IrModuloScheduleStatusV1.BudgetExhausted, null,
                ["schedule-span-overflow"], "Schedule-span bound overflowed deterministic integer limits.");
        }
        Dictionary<int, int> assigned = new Dictionary<int, int>();
        SortedSet<string> exactBindings = new SortedSet<string>(StringComparer.Ordinal);
        SortedSet<string> resourceBindings = new SortedSet<string>(StringComparer.Ordinal);
        SortedSet<string> unsupportedBindings = new SortedSet<string>(StringComparer.Ordinal);
        bool feasible = Visit(0);
        if (feasible)
            return new(IrModuloScheduleStatusV1.Feasible, new Dictionary<int, int>(assigned),
                Array.Empty<string>(), "Deterministic bounded assignment search found a kernel witness.");
        if (counters.BudgetHit)
            return new(IrModuloScheduleStatusV1.BudgetExhausted, null,
                ["candidate-or-cut-or-placement-budget"],
                $"Deterministic assignment-search budget exhausted: candidates={counters.CandidateStates}; " +
                $"cuts={counters.ResourceCuts}; exact-placement-states={counters.ExactPlacementStates}.");
        if (unsupportedBindings.Count > 0)
            return new(IrModuloScheduleStatusV1.UnsupportedSemanticOrResourceFact, null, unsupportedBindings.ToArray(),
                "A required compiler-visible resource fact is unsupported or unknown.");
        if (exactBindings.Count > 0)
            return new(IrModuloScheduleStatusV1.ExactPlacementInfeasible, null, exactBindings.ToArray(),
                "Temporal assignments exist but shared exact W=8 placement rejected every candidate.");
        if (resourceBindings.Count > 0)
            return new(IrModuloScheduleStatusV1.DiscreteResourceInfeasible, null, resourceBindings.ToArray(),
                "Discrete modulo resources rejected every temporal assignment.");
        return new(IrModuloScheduleStatusV1.TemporalInfeasible, null,
            ["bounded-cycle-domain"], "No temporal assignment exists within the deterministic schedule-span domain.");

        bool Visit(int ordinal)
        {
            if (ordinal == order.Length) return TemporalConstraintsHold(loop.DistanceDependencies, assigned, ii);
            IrInstruction instruction = order[ordinal];
            for (int cycle = earliest[instruction.Index]; cycle <= maximumCycle; cycle++)
            {
                counters.CandidateStates++;
                if (counters.CandidateStates > counters.Budgets.MaximumCandidateStates)
                {
                    counters.BudgetHit = true;
                    return false;
                }
                assigned[instruction.Index] = cycle;
                if (!PartialTemporalConstraintsHold(loop.DistanceDependencies, assigned, ii))
                {
                    assigned.Remove(instruction.Index);
                    continue;
                }
                IrInstruction[] group = assigned.Where(pair => pair.Value % ii == cycle % ii)
                    .Select(pair => loop.Instructions.Single(candidate => candidate.Index == pair.Key))
                    .OrderBy(static candidate => candidate.Index).ToArray();
                GroupEvaluation evaluation = EvaluateGroup(loop, group, model, counters);
                if (evaluation.Status == IrModuloScheduleStatusV1.BudgetExhausted)
                {
                    counters.BudgetHit = true;
                    assigned.Remove(instruction.Index);
                    return false;
                }
                if (evaluation.Status != IrModuloScheduleStatusV1.Feasible)
                {
                    counters.ResourceCuts++;
                    if (counters.ResourceCuts > counters.Budgets.MaximumResourceCuts)
                    {
                        counters.BudgetHit = true;
                        assigned.Remove(instruction.Index);
                        return false;
                    }
                    string binding = $"mod:{I(cycle % ii)}:{evaluation.Reason}";
                    if (evaluation.Status == IrModuloScheduleStatusV1.ExactPlacementInfeasible)
                        exactBindings.Add(binding);
                    else if (evaluation.Status == IrModuloScheduleStatusV1.UnsupportedSemanticOrResourceFact)
                        unsupportedBindings.Add(binding);
                    else
                        resourceBindings.Add(binding);
                    assigned.Remove(instruction.Index);
                    continue;
                }
                if (Visit(ordinal + 1)) return true;
                assigned.Remove(instruction.Index);
                if (counters.BudgetHit) return false;
            }
            return false;
        }
    }

    private static GroupEvaluation EvaluateGroup(
        IrCanonicalLoopV1 loop,
        IReadOnlyList<IrInstruction> instructions,
        HybridCpuMiiResourceModelV1 model,
        WorkCounters counters)
    {
        int reads = instructions.Sum(instruction => CountPrf(instruction.Annotation.Uses));
        int writes = instructions.Sum(instruction => CountPrf(instruction.Annotation.Defs));
        if ((reads > 0 && !model.Topology.PrfReadPortCapacity.HasValue) ||
            (writes > 0 && !model.Topology.PrfWritePortCapacity.HasValue))
            return GroupEvaluation.Unsupported("unknown-prf-capacity");
        if (reads > model.Topology.PrfReadPortCapacity.GetValueOrDefault(int.MaxValue))
            return GroupEvaluation.Resource($"prf-read:{I(reads)}/{I(model.Topology.PrfReadPortCapacity!.Value)}");
        if (writes > model.Topology.PrfWritePortCapacity.GetValueOrDefault(int.MaxValue))
            return GroupEvaluation.Resource($"prf-write:{I(writes)}/{I(model.Topology.PrfWritePortCapacity!.Value)}");

        string[] registerGroups = BuildRegisterGroupReservations(loop, instructions, model.Topology);

        List<int> banks = new List<int>();
        List<int> channels = new List<int>();
        foreach (IrInstruction instruction in instructions)
        {
            if (instruction.Annotation.MemoryReadRegion is null && instruction.Annotation.MemoryWriteRegion is null) continue;
            IrTopologyResourceFootprintV1 footprint = IrTopologyResourceFootprintBuilderV1.Build(instruction, model.Topology);
            if (footprint.Banks.Precision != IrAddressEvidenceKindV1.Exact || footprint.Banks.ResourceIds.Count != 1 ||
                footprint.Channels.Precision != IrAddressEvidenceKindV1.Exact || footprint.Channels.ResourceIds.Count != 1)
                return GroupEvaluation.Unsupported("unknown-bank-or-channel-mapping");
            banks.Add(footprint.Banks.ResourceIds[0]);
            channels.Add(footprint.Channels.ResourceIds[0]);
        }
        IGrouping<int, int>? bankConflict = banks.GroupBy(static id => id).FirstOrDefault(static group => group.Count() > 1);
        if (bankConflict is not null)
            return GroupEvaluation.Resource($"bank:{I(bankConflict.Key)}:demand:{I(bankConflict.Count())}/1");
        IGrouping<int, int>? channelConflict = channels.GroupBy(static id => id).FirstOrDefault(static group => group.Count() > 1);
        if (channelConflict is not null)
            return GroupEvaluation.Resource($"channel:{I(channelConflict.Key)}:demand:{I(channelConflict.Count())}/1");

        int lane6 = instructions.Count(static instruction => instruction.Annotation.RequiredSlotClass is
            IrSlotClass.DmaStreamClass or IrSlotClass.MatrixTileStreamClass);
        int lane7 = instructions.Count(static instruction => instruction.Annotation.RequiredSlotClass is
            IrSlotClass.BranchControl or IrSlotClass.SystemSingleton);
        if (lane6 > 1) return GroupEvaluation.Resource($"lane6:{I(lane6)}/1");
        if (lane7 > 1) return GroupEvaluation.Resource($"lane7:{I(lane7)}/1");
        int certificates = lane6 + lane7;
        if (certificates > 0 && !model.StructuralCertificateCapacity.HasValue)
            return GroupEvaluation.Unsupported("unknown-structural-certificate-capacity");
        if (certificates > model.StructuralCertificateCapacity.GetValueOrDefault(int.MaxValue))
            return GroupEvaluation.Resource(
                $"certificate:{I(certificates)}/{I(model.StructuralCertificateCapacity!.Value)}");

        IrBundlePlacementSearchResult placement;
        try
        {
            placement = HybridCpuSlotModel.SearchStructuralAssignments(
                instructions.Select(static instruction => instruction.Annotation.StructurallyAllowedSlots).ToArray());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return GroupEvaluation.Unsupported($"exact-placement-backend:{exception.GetType().Name}");
        }
        counters.ExactPlacementStates += placement.Summary.EvaluatedPlacementCount;
        if (counters.ExactPlacementStates > counters.Budgets.MaximumExactPlacementStates)
            return GroupEvaluation.Budget("exact-placement-state-budget-exhausted");
        if (!placement.HasStructuralPlacement)
            return GroupEvaluation.PlacementFailure("shared-exact-w8-search-rejected");

        int moduloCycle = 0;
        string fingerprint = HybridCpuModuloSchedulingContractV1.Hash(string.Join('|',
            "hybridcpu.modulo-placement/v1",
            string.Join(',', instructions.Select(static instruction => I(instruction.Index))),
            string.Join(',', placement.BestInstructionSlots.Select(static slot => I(slot)))));
        IrModuloResourceReservationV1 reservation = new IrModuloResourceReservationV1(
            moduloCycle,
            instructions.Count,
            reads,
            writes,
            registerGroups,
            banks.GroupBy(static id => id).OrderBy(static group => group.Key)
                .Select(group => $"{I(group.Key)}:{I(group.Count())}").ToArray(),
            channels.GroupBy(static id => id).OrderBy(static group => group.Key)
                .Select(group => $"{I(group.Key)}:{I(group.Count())}").ToArray(),
            lane6,
            lane7,
            certificates,
            fingerprint);
        return new(IrModuloScheduleStatusV1.Feasible, placement, reservation, "validated");
    }

    private static IrModuloScheduleWitnessV1 BuildWitness(
        IrCanonicalLoopV1 loop,
        IrLoopMiiReportV1 mii,
        HybridCpuMiiResourceModelV1 model,
        HybridCpuModuloSchedulerOptionsV1 options,
        int ii,
        IReadOnlyDictionary<int, int> cycles,
        int peakPressure)
    {
        List<IrModuloScheduledOperationV1> operations = new List<IrModuloScheduledOperationV1>();
        List<IrModuloResourceReservationV1> reservations = new List<IrModuloResourceReservationV1>();
        foreach (IGrouping<int, KeyValuePair<int, int>> group in cycles.GroupBy(pair => pair.Value % ii)
                     .OrderBy(static group => group.Key))
        {
            IrInstruction[] instructions = group.OrderBy(static pair => pair.Key)
                .Select(pair => loop.Instructions.Single(instruction => instruction.Index == pair.Key)).ToArray();
            IrBundlePlacementSearchResult placement = HybridCpuSlotModel.SearchStructuralAssignments(
                instructions.Select(static instruction => instruction.Annotation.StructurallyAllowedSlots).ToArray());
            WorkCounters counters = new WorkCounters(options.Budgets);
            GroupEvaluation evaluation = EvaluateGroup(loop, instructions, model, counters);
            IrModuloResourceReservationV1 reservation = evaluation.Reservation! with { ModuloCycle = group.Key };
            reservations.Add(reservation);
            for (int ordinal = 0; ordinal < instructions.Length; ordinal++)
            {
                IrInstruction instruction = instructions[ordinal];
                operations.Add(new(
                    instruction.Index,
                    instruction.StableIdentity,
                    cycles[instruction.Index],
                    group.Key,
                    placement.BestInstructionSlots[ordinal]));
            }
        }
        operations.Sort(static (left, right) => left.InstructionIndex.CompareTo(right.InstructionIndex));
        string[] edgeRefs = loop.DistanceDependencies.Select(static edge => edge.EdgeId)
            .Order(StringComparer.Ordinal).ToArray();
        string[] miiRefs = mii.Components.Select(ComponentProofRef).Order(StringComparer.Ordinal).ToArray();
        IrModuloLifetimePressureSummaryV1 lifetime = BuildLifetimeSummary(loop, operations, ii, peakPressure);
        IrModuloScheduleWitnessV1 witness = new IrModuloScheduleWitnessV1(
            HybridCpuModuloSchedulingContractV1.SchemaId,
            string.Empty,
            loop.LoopId,
            loop.VersionStamp,
            mii.ProofStamp.ProofDigest,
            ii,
            operations,
            reservations,
            edgeRefs,
            miiRefs,
            lifetime,
            HybridCpuTargetMachineContractV1.Default.ContractDigest,
            model.ModelDigest,
            HybridCpuModuloSchedulingContractV1.Default.ContractDigest,
            options.OptionsDigest,
            IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly);
        return witness with { WitnessDigest = WitnessDigest(witness) };
    }

    private static IrModuloLifetimePressureSummaryV1 BuildLifetimeSummary(
        IrCanonicalLoopV1 loop,
        IEnumerable<IrModuloScheduledOperationV1> operations,
        int ii,
        int peakPressure)
    {
        IrModuloScheduledOperationV1[] materialized = operations.ToArray();
        int minimum = materialized.Min(static operation => operation.Cycle);
        int maximum = materialized.Max(static operation => operation.Cycle);
        return new(
            loop.ValueAnalysis.ValueFlowDigest,
            minimum,
            maximum,
            checked(maximum - minimum + 1),
            peakPressure,
            checked(HybridCpuMachineTopologyV1.RegistersPerGroup * ii),
            IrLoopProofPrecisionV1.Exact);
    }

    private static string WitnessDigest(IrModuloScheduleWitnessV1 witness) =>
        HybridCpuModuloSchedulingContractV1.Hash(string.Join('|',
            HybridCpuModuloSchedulingContractV1.SchemaId,
            witness.LoopId,
            witness.LoopVersionStamp,
            witness.MiiProofDigest,
            witness.InitiationInterval.ToString(CultureInfo.InvariantCulture),
            string.Join(';', witness.Operations.OrderBy(static operation => operation.InstructionIndex)
                .Select(HybridCpuModuloSchedulingContractV1.OperationKey)),
            string.Join(';', ReservationProjection(witness.ResourceReservations)),
            string.Join(',', witness.DependenceProofRefs.Order(StringComparer.Ordinal)),
            string.Join(',', witness.MiiProofRefs.Order(StringComparer.Ordinal)),
            string.Join(':', witness.LifetimePressure.ValueFlowDigest, I(witness.LifetimePressure.MinimumCycle),
                I(witness.LifetimePressure.MaximumCycle), I(witness.LifetimePressure.KernelSpanCycles),
                I(witness.LifetimePressure.PeakRegisterGroupPressure), I(witness.LifetimePressure.CapacityAtChosenIi),
                witness.LifetimePressure.Precision),
            witness.TargetDigest,
            witness.ResourceModelDigest,
            witness.SchedulerContractDigest,
            witness.OptionsDigest,
            witness.OutputDisposition));

    private static IEnumerable<string> ReservationProjection(IEnumerable<IrModuloResourceReservationV1> reservations) =>
        reservations.OrderBy(static reservation => reservation.ModuloCycle).Select(reservation => string.Join(':',
            I(reservation.ModuloCycle),
            I(reservation.IssueCount),
            I(reservation.PrfReads),
            I(reservation.PrfWrites),
            string.Join(',', reservation.RegisterGroupReservations),
            string.Join(',', reservation.BankReservations),
            string.Join(',', reservation.ChannelReservations),
            I(reservation.Lane6Reservations),
            I(reservation.Lane7Reservations),
            I(reservation.StructuralCertificateReservations),
            reservation.ExactPlacementFingerprint));

    private static int PeakExactRegisterGroupPressure(IrCanonicalLoopV1 loop)
    {
        IrRegisterGroupPressureV1[] groups = loop.ValueAnalysis.Pressure
            .Where(pressure => loop.BlockIds.Contains(pressure.BlockId))
            .SelectMany(static pressure => pressure.RegisterGroups)
            .Where(static group => !group.IsPossibleRatherThanAssigned).ToArray();
        return groups.Length == 0 ? 0 : groups.Max(static group => group.PeakLiveValues);
    }

    private static string[] BuildRegisterGroupReservations(
        IrCanonicalLoopV1 loop,
        IReadOnlyList<IrInstruction> instructions,
        HybridCpuMachineTopologyV1 topology)
    {
        IEnumerable<int> directGroups = instructions.SelectMany(static instruction =>
                instruction.Annotation.Defs.Concat(instruction.Annotation.Uses))
            .Where(static operand => (operand.Kind is IrOperandKind.ArchitecturalRegister or IrOperandKind.Pointer) &&
                operand.Value <= HybridCpuMachineTopologyV1.MaximumRepresentableRegisterId)
            .Select(operand => topology.GetRegisterGroup((int)operand.Value));
        IEnumerable<string> direct = directGroups.GroupBy(static group => group).OrderBy(static group => group.Key)
            .Select(group => $"access-group:{I(group.Key)}:count:{I(group.Count())}");
        IEnumerable<string> pressure = loop.ValueAnalysis.Pressure
            .Where(item => loop.BlockIds.Contains(item.BlockId))
            .SelectMany(static item => item.RegisterGroups)
            .Where(static group => !group.IsPossibleRatherThanAssigned)
            .GroupBy(static group => group.RegisterGroup).OrderBy(static group => group.Key)
            .Select(group => $"phase07-group:{I(group.Key)}:peak:{I(group.Max(static item => item.PeakLiveValues))}");
        return direct.Concat(pressure).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static int CountPrf(IReadOnlyList<IrOperand> operands) => operands.Count(static operand =>
        operand.Kind is IrOperandKind.ArchitecturalRegister or IrOperandKind.Pointer or IrOperandKind.VirtualValue);

    private static bool TemporalConstraintsHold(
        IReadOnlyList<IrLoopDistanceDependencyV1> edges,
        IReadOnlyDictionary<int, int> cycles,
        int ii) => edges.All(edge =>
        (long)cycles[edge.ConsumerInstructionIndex] - cycles[edge.ProducerInstructionIndex] >=
        (long)edge.LatencyCycles - ((long)edge.IterationDistance * ii));

    private static bool PartialTemporalConstraintsHold(
        IReadOnlyList<IrLoopDistanceDependencyV1> edges,
        IReadOnlyDictionary<int, int> cycles,
        int ii) => edges.All(edge =>
        !cycles.TryGetValue(edge.ProducerInstructionIndex, out int producer) ||
        !cycles.TryGetValue(edge.ConsumerInstructionIndex, out int consumer) ||
        (long)consumer - producer >= (long)edge.LatencyCycles - ((long)edge.IterationDistance * ii));

    private static bool ChosenSlotsAreStructural(
        IReadOnlyList<IrInstruction> instructions,
        IReadOnlyList<int> slots)
    {
        if (instructions.Count != slots.Count || slots.Distinct().Count() != slots.Count) return false;
        for (int ordinal = 0; ordinal < instructions.Count; ordinal++)
        {
            int slot = slots[ordinal];
            if ((uint)slot >= HybridCpuSlotModel.SlotCount) return false;
            IrIssueSlotMask mask = (IrIssueSlotMask)(1 << slot);
            if ((instructions[ordinal].Annotation.StructurallyAllowedSlots & mask) == 0) return false;
        }
        return HybridCpuSlotModel.SearchStructuralAssignments(
            instructions.Select(static instruction => instruction.Annotation.StructurallyAllowedSlots).ToArray())
            .HasStructuralPlacement;
    }

    private static bool MiiComponentsEqual(IrLoopMiiReportV1 supplied, IrLoopMiiReportV1 canonical) =>
        supplied.Eligibility == canonical.Eligibility && supplied.ProvenLowerBoundIi == canonical.ProvenLowerBoundIi &&
        supplied.Components.Select(ComponentProjection).SequenceEqual(
            canonical.Components.Select(ComponentProjection), StringComparer.Ordinal);

    private static string ComponentProjection(IrMiiComponentResultV1 component) => string.Join(':',
        component.Component,
        component.Status,
        component.Value?.ToString(CultureInfo.InvariantCulture) ?? "none",
        component.Numerator.ToString(CultureInfo.InvariantCulture),
        component.Capacity?.ToString(CultureInfo.InvariantCulture) ?? "none",
        component.Precision,
        string.Join(',', component.Contributors),
        component.TargetDigest,
        component.ResourceModelDigest);

    private static string ComponentProofRef(IrMiiComponentResultV1 component) =>
        HybridCpuModuloSchedulingContractV1.Hash($"hybridcpu.mii-component-proof-ref/v1|{ComponentProjection(component)}");

    private static bool HasValidOptions(HybridCpuModuloSchedulerOptionsV1 options)
    {
        try
        {
            HybridCpuModuloSchedulerOptionsV1.Validate(options.Budgets);
            return options.OptionsDigest == HybridCpuModuloSchedulerOptionsV1.Create(options.Budgets).OptionsDigest;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static IrModuloInfeasibleIiReasonV1 CreateAttempt(
        int ii,
        IrModuloScheduleStatusV1 status,
        IReadOnlyList<string> bindingFacts,
        WorkCounters counters,
        string reason) => new(
            ii,
            status,
            bindingFacts,
            counters.SdcRelaxations,
            counters.CandidateStates,
            counters.ResourceCuts,
            counters.RefinementIterations,
            counters.ExactPlacementStates,
            reason);

    private static IrModuloScheduleResultV1 Rejected(
        IrModuloScheduleStatusV1 status,
        string code,
        string reason,
        HybridCpuModuloSchedulerOptionsV1 options) => new(
            status,
            null,
            null,
            Array.Empty<IrModuloInfeasibleIiReasonV1>(),
            [new(code, reason)],
            IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly,
            ResultDigest(status, null, "absent", Array.Empty<IrModuloInfeasibleIiReasonV1>(), options));

    private static IrModuloScheduleResultV1 BudgetResult(
        IReadOnlyList<IrModuloInfeasibleIiReasonV1> attempts,
        HybridCpuModuloSchedulerOptionsV1 options,
        string reason) => new(
            IrModuloScheduleStatusV1.BudgetExhausted,
            null,
            null,
            attempts.ToArray(),
            [new("HCMS1002", $"Budget exhaustion is not an infeasibility proof: {reason}")],
            IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly,
            ResultDigest(IrModuloScheduleStatusV1.BudgetExhausted, null, "absent", attempts, options));

    private static IrModuloWitnessValidationV1 Invalid(
        IrModuloScheduleStatusV1 status,
        string code,
        string reason,
        ICollection<IrRegionSchedulingDiagnosticV1> diagnostics)
    {
        diagnostics.Add(new(code, reason));
        string digest = HybridCpuModuloSchedulingContractV1.Hash(string.Join('|',
            "hybridcpu.modulo-witness-validation/v1",
            status,
            string.Join(';', diagnostics.Select(static diagnostic => $"{diagnostic.Code}:{diagnostic.Message}"))));
        return new(status, diagnostics.ToArray(), digest);
    }

    private static IrModuloCandidateWitnessResultV1 CandidateRejected(
        IrModuloScheduleStatusV1 status,
        string code,
        string reason)
    {
        List<IrRegionSchedulingDiagnosticV1> diagnostics = new List<IrRegionSchedulingDiagnosticV1>();
        IrModuloWitnessValidationV1 validation = Invalid(status, code, reason, diagnostics);
        return new(status, null, validation);
    }

    private static string ResultDigest(
        IrModuloScheduleStatusV1 status,
        int? ii,
        string witnessDigest,
        IReadOnlyList<IrModuloInfeasibleIiReasonV1> attempts,
        HybridCpuModuloSchedulerOptionsV1 options) => HybridCpuModuloSchedulingContractV1.Hash(string.Join('|',
            "hybridcpu.modulo-schedule-result/v1",
            status,
            ii?.ToString(CultureInfo.InvariantCulture) ?? "none",
            witnessDigest,
            options.OptionsDigest,
            string.Join(';', attempts.Select(static attempt =>
                $"{I(attempt.InitiationInterval)}:{attempt.Status}:{string.Join(',', attempt.BindingFacts)}:{attempt.Reason}"))));

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed class WorkCounters
    {
        public WorkCounters(IrModuloScheduleBudgetsV1 budgets) => Budgets = budgets;

        public IrModuloScheduleBudgetsV1 Budgets { get; }
        public int SdcRelaxations { get; set; }
        public int CandidateStates { get; set; }
        public int ResourceCuts { get; set; }
        public int RefinementIterations { get; set; }
        public int ExactPlacementStates { get; set; }
        public bool BudgetHit { get; set; }
    }

    private sealed record TemporalSolution(
        IrModuloScheduleStatusV1 Status,
        IReadOnlyDictionary<int, int>? EarliestCycles,
        IReadOnlyList<string> BindingFacts,
        string Reason);

    private sealed record SearchOutcome(
        IrModuloScheduleStatusV1 Status,
        IReadOnlyDictionary<int, int>? Cycles,
        IReadOnlyList<string> BindingFacts,
        string Reason);

    private sealed record GroupEvaluation(
        IrModuloScheduleStatusV1 Status,
        IrBundlePlacementSearchResult? Placement,
        IrModuloResourceReservationV1? Reservation,
        string Reason)
    {
        public static GroupEvaluation Resource(string reason) =>
            new(IrModuloScheduleStatusV1.DiscreteResourceInfeasible, null, null, reason);

        public static GroupEvaluation PlacementFailure(string reason) =>
            new(IrModuloScheduleStatusV1.ExactPlacementInfeasible, null, null, reason);

        public static GroupEvaluation Unsupported(string reason) =>
            new(IrModuloScheduleStatusV1.UnsupportedSemanticOrResourceFact, null, null, reason);

        public static GroupEvaluation Budget(string reason) =>
            new(IrModuloScheduleStatusV1.BudgetExhausted, null, null, reason);
    }
}
