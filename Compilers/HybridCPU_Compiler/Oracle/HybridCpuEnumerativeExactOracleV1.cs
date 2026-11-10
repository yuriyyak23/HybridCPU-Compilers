using System.Globalization;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU.Compiler.Oracle;

public sealed class HybridCpuEnumerativeExactOracleV1 : IHybridCpuExactSchedulingOracleV1
{
    public IrExactOracleQueryV1 QueryAtIi(
        IrProgram program,
        IrCanonicalLoopV1 loop,
        IrLoopMiiReportV1 mii,
        int initiationInterval,
        HybridCpuMiiResourceModelV1? resourceModel = null,
        HybridCpuExactOracleOptionsV1? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(mii);
        resourceModel ??= HybridCpuMiiResourceModelV1.Default;
        options ??= HybridCpuExactOracleOptionsV1.Ci;
        string fingerprint = ModelFingerprint(resourceModel, options);
        if (!HasValidOptions(options) || initiationInterval <= 0)
            return Result(HybridCpuExactOracleStatusV1.InvalidModel, initiationInterval, null, null, null, null,
                HybridCpuExactOracleLimitKindV1.None, true, 0, 0,
                "Oracle options or initiation interval are invalid.", fingerprint, options);

        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrLoopMiiReportV1 canonicalMii = analyzer.ComputeMii(program, loop, resourceModel);
        IrCanonicalLoopV1? subject = analyzer.Canonicalize(program, resourceModel).Loops.SingleOrDefault(candidate =>
            string.Equals(candidate.LoopId, loop.LoopId, StringComparison.Ordinal));
        if (subject is null || subject.Status != IrCanonicalLoopStatusV1.Qualified ||
            canonicalMii.Eligibility != IrLoopMiiEligibilityV1.EligibleLowerBound ||
            !MiiComponentsEqual(mii, canonicalMii))
            return Result(HybridCpuExactOracleStatusV1.InvalidModel, initiationInterval, null, null, null, null,
                HybridCpuExactOracleLimitKindV1.None, true, 0, 0,
                "Canonical loop or complete Phase 13 MII proof cannot be re-derived.", fingerprint, options);

        if (initiationInterval < canonicalMii.ProvenLowerBoundIi)
        {
            string[] core = BuildProvenLowerBoundCore(canonicalMii.Components, initiationInterval);
            IrExactOracleUnsatProofV1 proof = UnsatProof(initiationInterval, core, 0, 0, true, fingerprint);
            return Result(HybridCpuExactOracleStatusV1.Unsat, initiationInterval, null, null, null, proof,
                HybridCpuExactOracleLimitKindV1.None, true, 0, 0,
                "Phase 13 proven lower-bound components prove this II infeasible.", fingerprint, options);
        }
        if (subject.Instructions.Count > options.Budgets.MaximumOperations)
            return Result(HybridCpuExactOracleStatusV1.ResourceLimit, initiationInterval, null, null, null, null,
                HybridCpuExactOracleLimitKindV1.MaximumOperations, true, 0, 0,
                "Subject exceeds the deterministic exact-oracle operation bound.", fingerprint, options);

        IrInstruction[] order = subject.Instructions.OrderBy(static instruction => instruction.Index).ToArray();
        Dictionary<int, int> moduloTimes = new Dictionary<int, int>();
        int states = 0;
        int relaxations = 0;
        bool assignmentBudgetHit = false;
        bool relaxationBudgetHit = false;
        bool invalidModel = false;
        string invalidReason = string.Empty;
        IrModuloScheduleWitnessV1? rejectedWitness = null;
        IrModuloWitnessValidationV1? rejectedValidation = null;
        IrModuloScheduleWitnessV1? accepted = null;
        IrModuloWitnessValidationV1? acceptedValidation = null;

        Visit(0);
        if (accepted is not null)
            return Result(HybridCpuExactOracleStatusV1.Sat, initiationInterval, accepted, null, acceptedValidation,
                null, HybridCpuExactOracleLimitKindV1.None, true, states, relaxations,
                "Exact modulo-time enumeration found a Core-revalidated SAT witness.", fingerprint, options);
        if (assignmentBudgetHit || relaxationBudgetHit)
            return Result(HybridCpuExactOracleStatusV1.ResourceLimit, initiationInterval, null, rejectedWitness,
                rejectedValidation, null,
                assignmentBudgetHit ? HybridCpuExactOracleLimitKindV1.MaximumAssignmentStates :
                    HybridCpuExactOracleLimitKindV1.MaximumStageRelaxations,
                true, states, relaxations,
                "A deterministic exact-oracle work-unit limit was reached; infeasibility is not proven.",
                fingerprint, options);
        if (invalidModel)
            return Result(HybridCpuExactOracleStatusV1.InvalidModel, initiationInterval, null, rejectedWitness,
                rejectedValidation, null, HybridCpuExactOracleLimitKindV1.None, true, states, relaxations,
                invalidReason, fingerprint, options);

        string[] exhaustiveCore =
        [
            "complete-modulo-time-domain",
            "exact-stage-difference-system",
            "shared-exact-w8-placement",
            "core-resource-and-witness-validation"
        ];
        IrExactOracleUnsatProofV1 exhaustive = UnsatProof(
            initiationInterval, exhaustiveCore, states, relaxations, true, fingerprint);
        return Result(HybridCpuExactOracleStatusV1.Unsat, initiationInterval, null, null, null, exhaustive,
            HybridCpuExactOracleLimitKindV1.None, true, states, relaxations,
            "Every modulo-time assignment is infeasible under the comparable Core contract.", fingerprint, options);

        bool Visit(int ordinal)
        {
            if (ordinal == order.Length)
            {
                IReadOnlyDictionary<int, int>? cycles;
                if (options.OmittedConstraintFamily == HybridCpuOracleConstraintFamilyV1.TemporalDistance)
                {
                    cycles = new Dictionary<int, int>(moduloTimes);
                }
                else
                {
                    StageSolution stage = SolveStages(subject.DistanceDependencies, moduloTimes,
                        initiationInterval, options.Budgets.MaximumStageRelaxations - relaxations);
                    relaxations += stage.Relaxations;
                    if (stage.BudgetHit)
                    {
                        relaxationBudgetHit = true;
                        return true;
                    }
                    if (!stage.Feasible) return false;
                    cycles = MaterializeCycles(moduloTimes, stage.Stages!, initiationInterval);
                    if (cycles is null)
                    {
                        invalidModel = true;
                        invalidReason = "Exact stage solution overflowed the representable cycle domain.";
                        return true;
                    }
                }

                IrModuloCandidateWitnessResultV1 candidate = new HybridCpuModuloSchedulerV1().CreateValidatedWitness(
                    program, subject, canonicalMii, initiationInterval, cycles, resourceModel);
                if (candidate.IsValid)
                {
                    accepted = candidate.Witness;
                    acceptedValidation = candidate.Validation;
                    return true;
                }
                if (candidate.Witness is not null)
                {
                    rejectedWitness = candidate.Witness;
                    rejectedValidation = candidate.Validation;
                }
                if (options.OmittedConstraintFamily != HybridCpuOracleConstraintFamilyV1.None &&
                    candidate.Witness is not null)
                {
                    invalidModel = true;
                    invalidReason = "A deliberately incomplete oracle model produced a SAT candidate rejected by Core.";
                    return true;
                }
                if (options.OmittedConstraintFamily != HybridCpuOracleConstraintFamilyV1.None)
                    return false;
                if (candidate.Status is IrModuloScheduleStatusV1.UnsupportedSemanticOrResourceFact or
                    IrModuloScheduleStatusV1.StaleProof or IrModuloScheduleStatusV1.BudgetExhausted)
                {
                    invalidModel = true;
                    invalidReason = "Core could not establish comparable target/resource semantics for an oracle candidate.";
                    return true;
                }
                return false;
            }

            IrInstruction instruction = order[ordinal];
            for (int modulo = 0; modulo < initiationInterval; modulo++)
            {
                states++;
                if (states > options.Budgets.MaximumAssignmentStates)
                {
                    assignmentBudgetHit = true;
                    return true;
                }
                moduloTimes[instruction.Index] = modulo;
                if (Visit(ordinal + 1)) return true;
                moduloTimes.Remove(instruction.Index);
            }
            return false;
        }
    }

    public IrExactOracleMinimumReportV1 FindMinimum(
        IrProgram program,
        IrCanonicalLoopV1 loop,
        IrLoopMiiReportV1 mii,
        IrModuloScheduleResultV1? productionResult = null,
        HybridCpuMiiResourceModelV1? resourceModel = null,
        HybridCpuExactOracleOptionsV1? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(mii);
        resourceModel ??= HybridCpuMiiResourceModelV1.Default;
        options ??= HybridCpuExactOracleOptionsV1.Ci;
        if (!HasValidOptions(options))
        {
            IrExactOracleQueryV1 invalid = QueryAtIi(program, loop, mii, 1, resourceModel, options);
            IrExactOracleOptimalityGapV1 invalidGap = BuildGap(productionResult, null, null, [invalid]);
            string invalidDigest = HybridCpuExactSchedulingOracleContractV1.Hash(string.Join('|',
                "hybridcpu.exact-oracle-minimum-report/v1", HybridCpuExactOracleStatusV1.InvalidModel,
                "1", "none", invalid.ResultDigest, invalidGap.ComparisonDigest, resourceModel.ModelDigest,
                HybridCpuExactSchedulingOracleContractV1.Default.ContractDigest, options.OptionsDigest));
            return new(HybridCpuExactOracleStatusV1.InvalidModel, 1, null, null, [invalid], invalidGap,
                HybridCpuTargetMachineContractV1.Default.ContractDigest, resourceModel.ModelDigest,
                HybridCpuExactSchedulingOracleContractV1.Default.ContractDigest, options.OptionsDigest,
                invalidDigest);
        }

        int lowerBound = mii.ProvenLowerBoundIi ?? 1;
        List<IrExactOracleQueryV1> queries = new List<IrExactOracleQueryV1>();
        bool unresolvedLower = false;
        IrExactOracleQueryV1? feasible = null;
        for (int offset = 0; offset < options.Budgets.MaximumIiCandidates; offset++)
        {
            int ii;
            try
            {
                ii = checked(lowerBound + offset);
            }
            catch (OverflowException)
            {
                unresolvedLower = true;
                break;
            }
            IrExactOracleQueryV1 query = QueryAtIi(program, loop, mii, ii, resourceModel, options);
            queries.Add(query);
            if (query.Status == HybridCpuExactOracleStatusV1.Sat)
            {
                feasible = query;
                break;
            }
            if (query.Status != HybridCpuExactOracleStatusV1.Unsat) unresolvedLower = true;
        }

        HybridCpuExactOracleStatusV1 status;
        int? minimum = null;
        IrModuloScheduleWitnessV1? witness = feasible?.Witness;
        if (feasible is not null && !unresolvedLower)
        {
            status = HybridCpuExactOracleStatusV1.Sat;
            minimum = feasible.InitiationInterval;
        }
        else if (feasible is not null)
        {
            status = HybridCpuExactOracleStatusV1.Unknown;
        }
        else if (queries.Any(static query => query.Status == HybridCpuExactOracleStatusV1.InvalidModel))
        {
            status = HybridCpuExactOracleStatusV1.InvalidModel;
        }
        else
        {
            status = HybridCpuExactOracleStatusV1.ResourceLimit;
        }

        IrExactOracleOptimalityGapV1 gap = BuildGap(productionResult, minimum, witness, queries);
        string reportDigest = HybridCpuExactSchedulingOracleContractV1.Hash(string.Join('|',
            "hybridcpu.exact-oracle-minimum-report/v1",
            status,
            I(lowerBound),
            minimum?.ToString(CultureInfo.InvariantCulture) ?? "none",
            string.Join(',', queries.Select(static query => query.ResultDigest)),
            gap.ComparisonDigest,
            resourceModel.ModelDigest,
            HybridCpuExactSchedulingOracleContractV1.Default.ContractDigest,
            options.OptionsDigest));
        return new(status, lowerBound, minimum, witness, queries, gap,
            HybridCpuTargetMachineContractV1.Default.ContractDigest,
            resourceModel.ModelDigest,
            HybridCpuExactSchedulingOracleContractV1.Default.ContractDigest,
            options.OptionsDigest,
            reportDigest);
    }

    public static IrExactOracleQueryV1 OperationalResourceLimit(
        int initiationInterval,
        HybridCpuExactOracleLimitKindV1 kind,
        string reason,
        HybridCpuMiiResourceModelV1? resourceModel = null,
        HybridCpuExactOracleOptionsV1? options = null)
    {
        if (kind is not (HybridCpuExactOracleLimitKindV1.WallClockWatchdog or
            HybridCpuExactOracleLimitKindV1.SolverCrash or
            HybridCpuExactOracleLimitKindV1.UnsupportedSolverFeature))
            throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        resourceModel ??= HybridCpuMiiResourceModelV1.Default;
        options ??= HybridCpuExactOracleOptionsV1.Ci;
        return Result(HybridCpuExactOracleStatusV1.ResourceLimit, initiationInterval, null, null, null, null,
            kind, false, 0, 0, reason, ModelFingerprint(resourceModel, options), options);
    }

    private static StageSolution SolveStages(
        IReadOnlyList<IrLoopDistanceDependencyV1> edges,
        IReadOnlyDictionary<int, int> moduloTimes,
        int ii,
        int remainingRelaxations)
    {
        int[] indexes = moduloTimes.Keys.Order().ToArray();
        Dictionary<int, int> stages = indexes.ToDictionary(static index => index, static _ => 0);
        int relaxations = 0;
        for (int pass = 0; pass < indexes.Length; pass++)
        {
            bool changed = false;
            foreach (IrLoopDistanceDependencyV1 edge in edges.OrderBy(static edge => edge.ProducerInstructionIndex)
                         .ThenBy(static edge => edge.ConsumerInstructionIndex).ThenBy(static edge => edge.EdgeId, StringComparer.Ordinal))
            {
                relaxations++;
                if (relaxations > remainingRelaxations)
                    return new(false, null, relaxations, true);
                long rhs = (long)edge.LatencyCycles - ((long)edge.IterationDistance * ii) -
                    (moduloTimes[edge.ConsumerInstructionIndex] - moduloTimes[edge.ProducerInstructionIndex]);
                long delta = CeilDiv(rhs, ii);
                long required = stages[edge.ProducerInstructionIndex] + delta;
                if (required <= stages[edge.ConsumerInstructionIndex]) continue;
                if (required > int.MaxValue) return new(false, null, relaxations, false);
                stages[edge.ConsumerInstructionIndex] = (int)required;
                changed = true;
                if (pass == indexes.Length - 1) return new(false, null, relaxations, false);
            }
            if (!changed) break;
        }
        int minimum = stages.Values.Min();
        if (minimum != 0)
        {
            foreach (int index in indexes) stages[index] -= minimum;
        }
        return new(true, stages, relaxations, false);
    }

    private static IReadOnlyDictionary<int, int>? MaterializeCycles(
        IReadOnlyDictionary<int, int> moduloTimes,
        IReadOnlyDictionary<int, int> stages,
        int ii)
    {
        try
        {
            return moduloTimes.Keys.ToDictionary(index => index,
                index => checked(moduloTimes[index] + checked(ii * stages[index])));
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static long CeilDiv(long numerator, int denominator) =>
        numerator >= 0 ? checked((numerator + denominator - 1) / denominator) : numerator / denominator;

    private static IrExactOracleOptimalityGapV1 BuildGap(
        IrModuloScheduleResultV1? production,
        int? oracleMinimum,
        IrModuloScheduleWitnessV1? oracleWitness,
        IReadOnlyList<IrExactOracleQueryV1> queries)
    {
        bool comparable = production?.Status == IrModuloScheduleStatusV1.Feasible &&
            production.Witness is not null && oracleMinimum.HasValue && oracleWitness is not null &&
            production.Witness.TargetDigest == oracleWitness.TargetDigest &&
            production.Witness.ResourceModelDigest == oracleWitness.ResourceModelDigest &&
            production.Witness.SchedulerContractDigest == oracleWitness.SchedulerContractDigest;
        int? productionIi = production?.ChosenIi;
        int? iiGap = comparable ? productionIi - oracleMinimum : null;
        decimal? relative = comparable ? decimal.Divide(productionIi!.Value, oracleMinimum!.Value) : null;
        int? productionObjective = comparable ? production!.Witness!.LifetimePressure.KernelSpanCycles : null;
        int? oracleObjective = comparable ? oracleWitness!.LifetimePressure.KernelSpanCycles : null;
        int? objectiveGap = comparable ? productionObjective - oracleObjective : null;
        long? productionWorkUnits = production is null ? null : production.Attempts.Sum(static attempt =>
            (long)attempt.SdcRelaxations + attempt.CandidateStates + attempt.ResourceCuts +
            attempt.RefinementIterations + attempt.ExactPlacementStates);
        long oracleWorkUnits = queries.Sum(static query => (long)query.AssignmentStates + query.StageRelaxations);
        string reason = comparable ? "Comparable Core legality/resource/lifetime contract." :
            "Unscored because no proven oracle minimum or comparable production witness exists.";
        string digest = HybridCpuExactSchedulingOracleContractV1.Hash(string.Join('|',
            "hybridcpu.exact-oracle-gap/v1",
            comparable,
            productionIi?.ToString(CultureInfo.InvariantCulture) ?? "none",
            oracleMinimum?.ToString(CultureInfo.InvariantCulture) ?? "none",
            iiGap?.ToString(CultureInfo.InvariantCulture) ?? "none",
            relative?.ToString(CultureInfo.InvariantCulture) ?? "none",
            productionObjective?.ToString(CultureInfo.InvariantCulture) ?? "none",
            oracleObjective?.ToString(CultureInfo.InvariantCulture) ?? "none",
            objectiveGap?.ToString(CultureInfo.InvariantCulture) ?? "none",
            productionWorkUnits?.ToString(CultureInfo.InvariantCulture) ?? "none",
            oracleWorkUnits.ToString(CultureInfo.InvariantCulture),
            reason));
        return new(comparable, productionIi, oracleMinimum, iiGap, relative,
            productionObjective, oracleObjective, objectiveGap,
            productionWorkUnits, oracleWorkUnits, reason, digest);
    }

    private static IrExactOracleUnsatProofV1 UnsatProof(
        int ii,
        IReadOnlyList<string> core,
        int assignments,
        int relaxations,
        bool complete,
        string fingerprint)
    {
        string digest = HybridCpuExactSchedulingOracleContractV1.Hash(string.Join('|',
            "hybridcpu.exact-oracle-unsat-proof/v1", I(ii), string.Join(';', core),
            I(assignments), I(relaxations), complete, fingerprint));
        return new(ii, core, assignments, relaxations, complete, digest);
    }

    private static IrExactOracleQueryV1 Result(
        HybridCpuExactOracleStatusV1 status,
        int ii,
        IrModuloScheduleWitnessV1? witness,
        IrModuloScheduleWitnessV1? rejectedWitness,
        IrModuloWitnessValidationV1? validation,
        IrExactOracleUnsatProofV1? proof,
        HybridCpuExactOracleLimitKindV1 limitKind,
        bool deterministic,
        int states,
        int relaxations,
        string reason,
        string fingerprint,
        HybridCpuExactOracleOptionsV1 options)
    {
        string digest = HybridCpuExactSchedulingOracleContractV1.Hash(string.Join('|',
            "hybridcpu.exact-oracle-query/v1", status, I(ii), witness?.WitnessDigest ?? "none",
            rejectedWitness?.WitnessDigest ?? "none", validation?.ValidationDigest ?? "none",
            proof?.ProofDigest ?? "none", limitKind, deterministic, I(states), I(relaxations), reason,
            fingerprint, options.OptionsDigest));
        return new(status, ii, witness, rejectedWitness, validation, proof, limitKind,
            deterministic, states, relaxations, reason, fingerprint, digest);
    }

    private static string ModelFingerprint(
        HybridCpuMiiResourceModelV1 model,
        HybridCpuExactOracleOptionsV1 options) => HybridCpuExactSchedulingOracleContractV1.Hash(string.Join('|',
            HybridCpuExactSchedulingOracleContractV1.SchemaId,
            HybridCpuExactSchedulingOracleContractV1.BackendIdentity,
            HybridCpuExactSchedulingOracleContractV1.Default.ContractDigest,
            HybridCpuTargetMachineContractV1.Default.ContractDigest,
            model.ModelDigest,
            HybridCpuModuloSchedulingContractV1.Default.ContractDigest,
            options.OptionsDigest));

    private static bool HasValidOptions(HybridCpuExactOracleOptionsV1 options)
    {
        if (options.Budgets is null || string.IsNullOrWhiteSpace(options.OptionsDigest)) return false;
        try
        {
            HybridCpuExactOracleOptionsV1.Validate(options.Budgets);
            return options.OptionsDigest == HybridCpuExactOracleOptionsV1.Create(
                options.Budgets, options.OmittedConstraintFamily).OptionsDigest;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool MiiComponentsEqual(IrLoopMiiReportV1 supplied, IrLoopMiiReportV1 canonical) =>
        supplied.Eligibility == canonical.Eligibility && supplied.ProvenLowerBoundIi == canonical.ProvenLowerBoundIi &&
        supplied.Components.Select(ComponentProjection).SequenceEqual(
            canonical.Components.Select(ComponentProjection), StringComparer.Ordinal);

    private static string ComponentProjection(IrMiiComponentResultV1 component) => string.Join(':',
        component.Component, component.Status,
        component.Value?.ToString(CultureInfo.InvariantCulture) ?? "none",
        component.Numerator.ToString(CultureInfo.InvariantCulture),
        component.Capacity?.ToString(CultureInfo.InvariantCulture) ?? "none",
        component.Precision, string.Join(',', component.Contributors),
        component.TargetDigest, component.ResourceModelDigest);

    private static string ComponentCore(IrMiiComponentResultV1 component) => string.Join(':',
        component.Component,
        component.Value?.ToString(CultureInfo.InvariantCulture) ?? "none",
        component.Numerator.ToString(CultureInfo.InvariantCulture),
        component.Capacity?.ToString(CultureInfo.InvariantCulture) ?? "none",
        string.Join(',', component.Contributors));

    private static string[] BuildProvenLowerBoundCore(
        IReadOnlyList<IrMiiComponentResultV1> components,
        int initiationInterval) => components
        .Where(component => component.Status == IrMiiComponentStatusV1.Proven &&
            component.Value > initiationInterval)
        .OrderBy(static component => component.Component)
        .Select(ComponentCore).ToArray();

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record StageSolution(
        bool Feasible,
        IReadOnlyDictionary<int, int>? Stages,
        int Relaxations,
        bool BudgetHit);
}
