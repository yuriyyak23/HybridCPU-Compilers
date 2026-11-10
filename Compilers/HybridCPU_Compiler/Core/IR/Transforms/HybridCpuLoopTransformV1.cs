using System.Globalization;

namespace HybridCPU.Compiler.Core.IR;

public sealed class HybridCpuLoopTransformV1
{
    private const IrLoopInvalidatedAnalysisKindV1 AllStructuralFacts =
        IrLoopInvalidatedAnalysisKindV1.CanonicalCapabilityAndSideEffects |
        IrLoopInvalidatedAnalysisKindV1.DependencyDag |
        IrLoopInvalidatedAnalysisKindV1.LoopDistances |
        IrLoopInvalidatedAnalysisKindV1.Liveness |
        IrLoopInvalidatedAnalysisKindV1.Pressure |
        IrLoopInvalidatedAnalysisKindV1.ResourceModel |
        IrLoopInvalidatedAnalysisKindV1.Mii |
        IrLoopInvalidatedAnalysisKindV1.Placement;

    public IrLoopTransformResultV1 MaterializeModuloExpansion(
        IrProgram program,
        IrCanonicalLoopV1 loop,
        IrLoopMiiReportV1 mii,
        IrModuloScheduleWitnessV1 witness,
        long tripCount,
        HybridCpuMiiResourceModelV1? resourceModel = null,
        HybridCpuLoopTransformOptionsV1? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(mii);
        ArgumentNullException.ThrowIfNull(witness);
        resourceModel ??= HybridCpuMiiResourceModelV1.Default;
        options ??= HybridCpuLoopTransformOptionsV1.Production;
        if (!HasValidOptions(options))
            return Fallback(IrLoopTransformKindV1.ModuloExpansion, IrLoopTransformStatusV1.InvalidModel,
                loop, options, "HCLT0001", "Loop-transform options do not bind their deterministic budgets.");
        if (options.ModuloExpansionSwitch == IrLoopTransformSwitchV1.Disabled)
            return Fallback(IrLoopTransformKindV1.ModuloExpansion, IrLoopTransformStatusV1.DisabledFallback,
                loop, options, "HCLT0002", "Modulo expansion is disabled; exact original-loop fallback selected.");
        if (tripCount < 0 || tripCount > options.Budgets.MaximumKnownTripCount)
            return Fallback(IrLoopTransformKindV1.ModuloExpansion, IrLoopTransformStatusV1.BudgetExhausted,
                loop, options, "HCLT0003", "Known trip count is outside the deterministic materialization bound.");
        long operationCount = checked(tripCount * loop.Instructions.Count);
        if (operationCount > options.Budgets.MaximumMaterializedOperations)
            return Fallback(IrLoopTransformKindV1.ModuloExpansion, IrLoopTransformStatusV1.BudgetExhausted,
                loop, options, "HCLT0004", "Materialized operation count exceeds its deterministic bound.");

        IrFrontendAdapterResultV1 frontend = CanonicalIrFrontendBoundaryV1.Validate(program);
        if (frontend.Status != IrFrontendAdapterStatus.Success)
            return Fallback(IrLoopTransformKindV1.ModuloExpansion, IrLoopTransformStatusV1.Ineligible,
                loop, options, "HCLT0005", "Canonical capability/side-effect validation rejected the expansion subject.");
        HybridCpuModuloSchedulerV1 scheduler = new HybridCpuModuloSchedulerV1();
        IrModuloWitnessValidationV1 validation = scheduler.ValidateWitness(
            program, loop, mii, witness, resourceModel);
        if (validation.Status != IrModuloScheduleStatusV1.Feasible)
            return Fallback(IrLoopTransformKindV1.ModuloExpansion, IrLoopTransformStatusV1.StaleProof,
                loop, options, "HCLT0006", "Phase 14 kernel witness is stale or invalid for this expansion subject.");
        if (loop.ExitBlockIds.Count > 1)
            return Fallback(IrLoopTransformKindV1.ModuloExpansion, IrLoopTransformStatusV1.Ineligible,
                loop, options, "HCLT0007", "Multiple exits require a compensation protocol and are not eligible.");

        IrModuloExpansionV1? expansion = BuildExpansion(
            program, loop, witness, validation, tripCount, out string? failure);
        if (expansion is null)
            return Fallback(IrLoopTransformKindV1.ModuloExpansion, IrLoopTransformStatusV1.InvalidModel,
                loop, options, "HCLT0008", failure!);

        string[] proofRefs =
        [
            loop.VersionStamp,
            loop.DistanceDagDigest,
            mii.ProofStamp.ProofDigest,
            witness.WitnessDigest,
            validation.ValidationDigest,
            expansion.PlacementValidationDigest,
            expansion.TemporalValidationDigest
        ];
        IrLoopTransformFreshFactsV1 facts = FreshFacts(
            loop.MutationStamp,
            loop.DistanceDagDigest,
            loop.ValueAnalysis.ValueFlowDigest,
            mii.ProofStamp.ProofDigest,
            witness.WitnessDigest,
            validation.ValidationDigest,
            IrLoopInvalidatedAnalysisKindV1.None);
        IrLoopTransformProfitabilityV1 profitability = Profitability(
            IrLoopTransformProfileDispositionV1.AbsentStaticPolicy,
            witness.InitiationInterval,
            witness.InitiationInterval,
            1,
            0,
            checked((int)operationCount),
            0,
            0,
            operationCount,
            "Materialization is a correctness operation; it does not claim a production KPI win.");
        string digest = ResultDigest(
            IrLoopTransformKindV1.ModuloExpansion,
            IrLoopTransformStatusV1.Accepted,
            loop.LoopId,
            expansion.ExpansionDigest,
            facts.FactsDigest,
            profitability.EvidenceDigest,
            Array.Empty<IrLoopTransformCandidateV1>(),
            options.OptionsDigest);
        return new(
            IrLoopTransformKindV1.ModuloExpansion,
            IrLoopTransformStatusV1.Accepted,
            null,
            loop,
            expansion,
            mii,
            witness,
            proofRefs,
            IrLoopInvalidatedAnalysisKindV1.None,
            facts,
            profitability,
            Array.Empty<IrLoopTransformCandidateV1>(),
            loop.LoopId,
            Array.Empty<IrRegionSchedulingDiagnosticV1>(),
            digest);
    }

    public IrLoopTransformResultV1 TransformArchitectureDrivenUnroll(
        IrProgram program,
        IrCanonicalLoopV1 loop,
        IrLoopMiiReportV1 mii,
        IrModuloScheduleWitnessV1 baselineWitness,
        long? knownTripCount,
        HybridCpuMiiResourceModelV1? resourceModel = null,
        IrLoopTransformProfileV1? profile = null,
        HybridCpuLoopTransformOptionsV1? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(mii);
        ArgumentNullException.ThrowIfNull(baselineWitness);
        resourceModel ??= HybridCpuMiiResourceModelV1.Default;
        profile ??= IrLoopTransformProfileV1.Absent;
        options ??= HybridCpuLoopTransformOptionsV1.Production;
        if (!HasValidOptions(options) || !HasValidProfile(profile))
            return Fallback(IrLoopTransformKindV1.ArchitectureDrivenUnroll,
                IrLoopTransformStatusV1.InvalidModel, loop, options,
                "HCLT1001", "Transform options or profitability-only profile identity are invalid.");
        if (options.ArchitectureUnrollSwitch == IrLoopTransformSwitchV1.Disabled)
            return Fallback(IrLoopTransformKindV1.ArchitectureDrivenUnroll,
                IrLoopTransformStatusV1.DisabledFallback, loop, options,
                "HCLT1002", "Architecture-driven unrolling is disabled; factor 1 selected.");
        if (knownTripCount is null || knownTripCount < 0 ||
            knownTripCount > options.Budgets.MaximumKnownTripCount)
            return Fallback(IrLoopTransformKindV1.ArchitectureDrivenUnroll,
                IrLoopTransformStatusV1.DisabledFallback, loop, options,
                "HCLT1003", "Unknown or out-of-budget trip count selects deterministic factor 1.");
        if (loop.Instructions.Count > options.Budgets.MaximumBodyOperations || loop.BlockIds.Count != 1)
            return Fallback(IrLoopTransformKindV1.ArchitectureDrivenUnroll,
                IrLoopTransformStatusV1.Ineligible, loop, options,
                "HCLT1004", "Only bounded single-block canonical loops are eligible for the first transform contract.");
        if (HasUnsupportedTransformEffect(loop.Instructions))
            return Fallback(IrLoopTransformKindV1.ArchitectureDrivenUnroll,
                IrLoopTransformStatusV1.Ineligible, loop, options,
                "HCLT1005", "Control, exceptional, atomic, volatile, fence or unknown effects prevent cloning.");
        if (HasUnsupportedUnrollValueFlow(program, loop))
            return Fallback(IrLoopTransformKindV1.ArchitectureDrivenUnroll,
                IrLoopTransformStatusV1.Ineligible, loop, options,
                "HCLT1008", "Unfixed virtual SSA values require an explicit lane-renaming proof and fail closed.");

        HybridCpuModuloSchedulerV1 scheduler = new HybridCpuModuloSchedulerV1();
        IrModuloWitnessValidationV1 baselineValidation = scheduler.ValidateWitness(
            program, loop, mii, baselineWitness, resourceModel);
        if (baselineValidation.Status != IrModuloScheduleStatusV1.Feasible)
            return Fallback(IrLoopTransformKindV1.ArchitectureDrivenUnroll,
                IrLoopTransformStatusV1.StaleProof, loop, options,
                "HCLT1006", "Baseline Phase 14 witness is stale or invalid.");

        int baselinePressure = PeakPressure(loop.ValueAnalysis);
        List<IrLoopTransformCandidateV1> candidates = new List<IrLoopTransformCandidateV1>
        {
            Candidate(IrLoopTransformKindV1.ArchitectureDrivenUnroll, 1,
                IrLoopTransformCandidateStatusV1.Baseline, baselineWitness.InitiationInterval,
                0, 0, 0, 0, "Exact original-loop fallback.")
        };
        CandidateState? best = null;
        foreach (int factor in options.CandidateUnrollFactors.Where(static factor => factor > 1).Order())
        {
            long workUnits = 1;
            if (knownTripCount.Value < checked(factor * 2L))
            {
                candidates.Add(Candidate(IrLoopTransformKindV1.ArchitectureDrivenUnroll, factor,
                    IrLoopTransformCandidateStatusV1.TripCountInsufficient, null,
                    checked((factor - 1) * loop.Instructions.Count), 0, 0, workUnits,
                    "Trip count cannot amortize the transformed kernel and remainder path."));
                continue;
            }
            int transformedOperations = checked(loop.Instructions.Count * factor);
            int codeDelta = transformedOperations - loop.Instructions.Count;
            int growthPercent = checked(codeDelta * 100 / Math.Max(1, loop.Instructions.Count));
            if (transformedOperations > options.Budgets.MaximumTransformedOperations ||
                growthPercent > options.Budgets.MaximumCodeGrowthPercent)
            {
                candidates.Add(Candidate(IrLoopTransformKindV1.ArchitectureDrivenUnroll, factor,
                    IrLoopTransformCandidateStatusV1.CodeGrowthLimit, null, codeDelta, 0, 0,
                    workUnits, "Transformed body exceeds the operation or code-growth bound."));
                continue;
            }

            IrProgram transformed;
            try
            {
                transformed = BuildUnrolledProgram(program, loop, factor);
            }
            catch (OverflowException)
            {
                candidates.Add(Candidate(IrLoopTransformKindV1.ArchitectureDrivenUnroll, factor,
                    IrLoopTransformCandidateStatusV1.BudgetExhausted, null, codeDelta, 0, 0,
                    workUnits, "Index or deterministic work arithmetic overflowed."));
                continue;
            }
            workUnits += transformed.Instructions.Count;
            IrFrontendAdapterResultV1 frontend = CanonicalIrFrontendBoundaryV1.Validate(transformed);
            if (frontend.Status != IrFrontendAdapterStatus.Success)
            {
                candidates.Add(Candidate(IrLoopTransformKindV1.ArchitectureDrivenUnroll, factor,
                    IrLoopTransformCandidateStatusV1.AnalysisRejected, null, codeDelta, 0, 0,
                    workUnits, "Post-clone Canonical IR capability/side-effect validation failed."));
                continue;
            }
            HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
            IrLoopCanonicalizationResultV1 canonicalization = analyzer.Canonicalize(transformed, resourceModel);
            IrCanonicalLoopV1? transformedLoop = canonicalization.Loops.SingleOrDefault(candidate =>
                candidate.HeaderBlockId == loop.HeaderBlockId);
            if (transformedLoop is null || transformedLoop.Status != IrCanonicalLoopStatusV1.Qualified)
            {
                candidates.Add(Candidate(IrLoopTransformKindV1.ArchitectureDrivenUnroll, factor,
                    IrLoopTransformCandidateStatusV1.AnalysisRejected, null, codeDelta, 0, 0,
                    workUnits, "Transformed loop could not be canonically re-derived."));
                continue;
            }
            IrLoopMiiReportV1 transformedMii = analyzer.ComputeMii(transformed, transformedLoop, resourceModel);
            workUnits += transformedLoop.DistanceDependencies.Count + transformedLoop.ValueAnalysis.Intervals.Count;
            if (transformedMii.Eligibility != IrLoopMiiEligibilityV1.EligibleLowerBound)
            {
                candidates.Add(Candidate(IrLoopTransformKindV1.ArchitectureDrivenUnroll, factor,
                    IrLoopTransformCandidateStatusV1.AnalysisRejected, null, codeDelta, 0, 0,
                    workUnits, "Fresh dependency, liveness, pressure or MII analysis is incomplete."));
                continue;
            }
            IrModuloScheduleResultV1 schedule = scheduler.Schedule(
                transformed, transformedLoop, transformedMii, resourceModel);
            workUnits += ScheduleWorkUnits(schedule);
            if (schedule.Status != IrModuloScheduleStatusV1.Feasible || schedule.Witness is null)
            {
                IrLoopTransformCandidateStatusV1 rejection = schedule.Status == IrModuloScheduleStatusV1.BudgetExhausted
                    ? IrLoopTransformCandidateStatusV1.BudgetExhausted
                    : IrLoopTransformCandidateStatusV1.ScheduleRejected;
                candidates.Add(Candidate(IrLoopTransformKindV1.ArchitectureDrivenUnroll, factor,
                    rejection, schedule.ChosenIi, codeDelta, 0, 0, workUnits,
                    "Fresh production scheduler did not produce a validated candidate witness."));
                continue;
            }
            IrModuloWitnessValidationV1 transformedValidation = scheduler.ValidateWitness(
                transformed, transformedLoop, transformedMii, schedule.Witness, resourceModel);
            if (transformedValidation.Status != IrModuloScheduleStatusV1.Feasible)
            {
                candidates.Add(Candidate(IrLoopTransformKindV1.ArchitectureDrivenUnroll, factor,
                    IrLoopTransformCandidateStatusV1.ScheduleRejected, schedule.ChosenIi,
                    codeDelta, 0, 0, workUnits,
                    "Fresh exact W=8 placement witness failed independent revalidation."));
                continue;
            }

            int pressure = PeakPressure(transformedLoop.ValueAnalysis);
            int pressureDelta = pressure - baselinePressure;
            if (pressureDelta > options.Budgets.MaximumPressureIncrease)
            {
                candidates.Add(Candidate(IrLoopTransformKindV1.ArchitectureDrivenUnroll, factor,
                    IrLoopTransformCandidateStatusV1.PressureLimit, schedule.ChosenIi,
                    codeDelta, pressureDelta, 0, workUnits,
                    "Fresh Phase 07 register-group pressure exceeds the transform bound."));
                continue;
            }
            int denominator = checked(baselineWitness.InitiationInterval * factor);
            int benefit = checked((denominator - schedule.Witness.InitiationInterval) * 10_000 / denominator);
            if (benefit < options.Budgets.MinimumBenefitBasisPoints)
            {
                candidates.Add(Candidate(IrLoopTransformKindV1.ArchitectureDrivenUnroll, factor,
                    IrLoopTransformCandidateStatusV1.NoKpiBenefit, schedule.ChosenIi,
                    codeDelta, pressureDelta, benefit, workUnits,
                    "Fresh II per source iteration does not clear the deterministic KPI threshold."));
                continue;
            }

            IrProgram currentProgram = MarkFresh(transformed);
            IrLoopTransformCandidateV1 accepted = Candidate(
                IrLoopTransformKindV1.ArchitectureDrivenUnroll,
                factor,
                IrLoopTransformCandidateStatusV1.Accepted,
                schedule.Witness.InitiationInterval,
                codeDelta,
                pressureDelta,
                benefit,
                workUnits,
                "Fresh schedule improves II per source iteration within code-growth and pressure bounds.");
            candidates.Add(accepted);
            CandidateState state = new CandidateState(
                factor, currentProgram, transformedLoop, transformedMii, schedule.Witness,
                transformedValidation, accepted);
            if (best is null || IsBetter(state, best)) best = state;
        }

        if (best is null)
            return FallbackWithCandidates(
                IrLoopTransformKindV1.ArchitectureDrivenUnroll,
                IrLoopTransformStatusV1.DisabledFallback,
                loop,
                options,
                candidates,
                "HCLT1007",
                "No candidate proved a deterministic KPI benefit; exact factor-1 fallback selected.");

        long remainder = knownTripCount.Value % best.Factor;
        IrLoopTransformProfitabilityV1 profitability = Profitability(
            profile.Disposition,
            baselineWitness.InitiationInterval,
            best.Witness.InitiationInterval,
            best.Factor,
            best.Candidate.BenefitBasisPoints,
            best.Candidate.CodeSizeDeltaOperations,
            best.Candidate.PressureDelta,
            remainder,
            candidates.Sum(static candidate => candidate.DeterministicWorkUnits),
            remainder == 0
                ? "Exact factor partition; no remainder iterations."
                : "Remainder executes through the exact original-loop fallback identity.");
        IrLoopTransformFreshFactsV1 facts = FreshFacts(
            best.Loop.MutationStamp,
            best.Loop.DistanceDagDigest,
            best.Loop.ValueAnalysis.ValueFlowDigest,
            best.Mii.ProofStamp.ProofDigest,
            best.Witness.WitnessDigest,
            best.Validation.ValidationDigest,
            AllStructuralFacts);
        string[] proofRefs =
        [
            loop.VersionStamp,
            baselineWitness.WitnessDigest,
            best.Loop.VersionStamp,
            best.Loop.DistanceDagDigest,
            best.Mii.ProofStamp.ProofDigest,
            best.Witness.WitnessDigest,
            best.Validation.ValidationDigest,
            profitability.EvidenceDigest
        ];
        string digest = ResultDigest(
            IrLoopTransformKindV1.ArchitectureDrivenUnroll,
            IrLoopTransformStatusV1.Accepted,
            loop.LoopId,
            best.Loop.LoopId,
            facts.FactsDigest,
            profitability.EvidenceDigest,
            candidates,
            options.OptionsDigest);
        return new(
            IrLoopTransformKindV1.ArchitectureDrivenUnroll,
            IrLoopTransformStatusV1.Accepted,
            best.Program,
            best.Loop,
            null,
            best.Mii,
            best.Witness,
            proofRefs,
            AllStructuralFacts,
            facts,
            profitability,
            candidates,
            loop.LoopId,
            Array.Empty<IrRegionSchedulingDiagnosticV1>(),
            digest);
    }

    public IrLoopTransformResultV1 TransformLoopFusion(
        IrProgram program,
        IrCanonicalLoopV1 firstLoop,
        IrLoopMiiReportV1 firstMii,
        IrModuloScheduleWitnessV1 firstWitness,
        IrLoopIterationDomainProofV1 firstDomain,
        IrCanonicalLoopV1 secondLoop,
        IrLoopMiiReportV1 secondMii,
        IrModuloScheduleWitnessV1 secondWitness,
        IrLoopIterationDomainProofV1 secondDomain,
        HybridCpuMiiResourceModelV1? resourceModel = null,
        HybridCpuLoopTransformOptionsV1? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(firstLoop);
        ArgumentNullException.ThrowIfNull(firstMii);
        ArgumentNullException.ThrowIfNull(firstWitness);
        ArgumentNullException.ThrowIfNull(firstDomain);
        ArgumentNullException.ThrowIfNull(secondLoop);
        ArgumentNullException.ThrowIfNull(secondMii);
        ArgumentNullException.ThrowIfNull(secondWitness);
        ArgumentNullException.ThrowIfNull(secondDomain);
        resourceModel ??= HybridCpuMiiResourceModelV1.Default;
        options ??= HybridCpuLoopTransformOptionsV1.Production;
        if (!HasValidOptions(options) || !HasValidDomain(firstLoop, firstDomain) ||
            !HasValidDomain(secondLoop, secondDomain))
            return Fallback(IrLoopTransformKindV1.LoopFusion,
                IrLoopTransformStatusV1.InvalidModel, firstLoop, options,
                "HCLT2001", "Fusion options or static iteration-domain proof are invalid.");
        if (options.FusionSwitch == IrLoopTransformSwitchV1.Disabled)
            return Fallback(IrLoopTransformKindV1.LoopFusion,
                IrLoopTransformStatusV1.DisabledFallback, firstLoop, options,
                "HCLT2002", "Loop fusion is disabled; the original loop pair remains exact.");
        if (firstDomain.InitialValue != secondDomain.InitialValue ||
            firstDomain.ExclusiveLimit != secondDomain.ExclusiveLimit ||
            firstDomain.Step != secondDomain.Step || firstDomain.TripCount != secondDomain.TripCount)
            return Fallback(IrLoopTransformKindV1.LoopFusion,
                IrLoopTransformStatusV1.Ineligible, firstLoop, options,
                "HCLT2003", "Fusion requires identical statically proven iteration domains.");
        if (firstLoop.BlockIds.Count != 1 || secondLoop.BlockIds.Count != 1 ||
            firstLoop.ExitBlockIds.Count != 1 || firstLoop.ExitBlockIds[0] != secondLoop.PreheaderBlockId ||
            secondLoop.ExitBlockIds.Count > 1)
            return Fallback(IrLoopTransformKindV1.LoopFusion,
                IrLoopTransformStatusV1.Ineligible, firstLoop, options,
                "HCLT2004", "Fusion requires immediately adjacent single-block loops and a simple final exit.");
        IrBasicBlock connector = program.BasicBlocks.Single(block => block.Id == secondLoop.PreheaderBlockId);
        if (FusionRemovesExternallyNamedBoundary(program, secondLoop.PreheaderBlockId,
                secondLoop.BlockIds.Single()))
            return Fallback(IrLoopTransformKindV1.LoopFusion,
                IrLoopTransformStatusV1.Ineligible, firstLoop, options,
                "HCLT2015", "Fusion cannot remove a labeled, callable or externally entered boundary.");
        if (connector.Instructions.Any(static instruction => instruction.Opcode != HybridCpuOpcode.Nope ||
                HasUnsupportedTransformEffect([instruction])) ||
            program.ValueFlow.Accesses.Any(access => connector.Instructions.Any(instruction =>
                instruction.Index == access.InstructionIndex)))
            return Fallback(IrLoopTransformKindV1.LoopFusion,
                IrLoopTransformStatusV1.Ineligible, firstLoop, options,
                "HCLT2005", "The adjacency connector must be effect-free and value-neutral.");
        if (HasUnsupportedTransformEffect(firstLoop.Instructions) ||
            HasUnsupportedTransformEffect(secondLoop.Instructions))
            return Fallback(IrLoopTransformKindV1.LoopFusion,
                IrLoopTransformStatusV1.Ineligible, firstLoop, options,
                "HCLT2006", "Exceptional, control, atomic, volatile, fence or unknown effects prevent fusion.");
        if (!CrossLoopMemoryIsIndependent(firstLoop, secondLoop))
            return Fallback(IrLoopTransformKindV1.LoopFusion,
                IrLoopTransformStatusV1.Ineligible, firstLoop, options,
                "HCLT2007", "Cross-loop MayAlias/MustAlias memory effects prevent fusion.");
        if (!CrossLoopValuesAreIndependent(program, firstLoop, secondLoop))
            return Fallback(IrLoopTransformKindV1.LoopFusion,
                IrLoopTransformStatusV1.Ineligible, firstLoop, options,
                "HCLT2008", "The first fusion slice requires disjoint virtual-value domains.");

        HybridCpuModuloSchedulerV1 scheduler = new HybridCpuModuloSchedulerV1();
        if (scheduler.ValidateWitness(program, firstLoop, firstMii, firstWitness, resourceModel).Status !=
                IrModuloScheduleStatusV1.Feasible ||
            scheduler.ValidateWitness(program, secondLoop, secondMii, secondWitness, resourceModel).Status !=
                IrModuloScheduleStatusV1.Feasible)
            return Fallback(IrLoopTransformKindV1.LoopFusion,
                IrLoopTransformStatusV1.StaleProof, firstLoop, options,
                "HCLT2009", "One of the input kernel witnesses is stale or invalid.");
        int transformedOperations = checked(firstLoop.Instructions.Count + secondLoop.Instructions.Count);
        if (transformedOperations > options.Budgets.MaximumTransformedOperations)
            return Fallback(IrLoopTransformKindV1.LoopFusion,
                IrLoopTransformStatusV1.BudgetExhausted, firstLoop, options,
                "HCLT2010", "Fused body exceeds the deterministic transformed-operation bound.");

        IrProgram transformed = BuildFusedProgram(program, firstLoop, secondLoop);
        if (CanonicalIrFrontendBoundaryV1.Validate(transformed).Status != IrFrontendAdapterStatus.Success)
            return Fallback(IrLoopTransformKindV1.LoopFusion,
                IrLoopTransformStatusV1.Ineligible, firstLoop, options,
                "HCLT2011", "Post-fusion Canonical IR capability/side-effect validation failed.");
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrCanonicalLoopV1? fusedLoop = analyzer.Canonicalize(transformed, resourceModel).Loops
            .SingleOrDefault(candidate => candidate.HeaderBlockId == firstLoop.HeaderBlockId);
        if (fusedLoop is null || fusedLoop.Status != IrCanonicalLoopStatusV1.Qualified)
            return Fallback(IrLoopTransformKindV1.LoopFusion,
                IrLoopTransformStatusV1.Ineligible, firstLoop, options,
                "HCLT2012", "Fresh canonicalization rejected the fused loop.");
        IrLoopMiiReportV1 fusedMii = analyzer.ComputeMii(transformed, fusedLoop, resourceModel);
        IrModuloScheduleResultV1 fusedSchedule = fusedMii.Eligibility == IrLoopMiiEligibilityV1.EligibleLowerBound
            ? scheduler.Schedule(transformed, fusedLoop, fusedMii, resourceModel)
            : new(IrModuloScheduleStatusV1.UnsupportedSemanticOrResourceFact, null, null,
                Array.Empty<IrModuloInfeasibleIiReasonV1>(), fusedMii.Diagnostics,
                IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly, "analysis-rejected");
        if (fusedSchedule.Status != IrModuloScheduleStatusV1.Feasible || fusedSchedule.Witness is null)
            return Fallback(IrLoopTransformKindV1.LoopFusion,
                fusedSchedule.Status == IrModuloScheduleStatusV1.BudgetExhausted
                    ? IrLoopTransformStatusV1.BudgetExhausted
                    : IrLoopTransformStatusV1.Ineligible,
                firstLoop, options, "HCLT2013", "Fresh fused scheduling or placement failed.");
        IrModuloWitnessValidationV1 fusedValidation = scheduler.ValidateWitness(
            transformed, fusedLoop, fusedMii, fusedSchedule.Witness, resourceModel);
        if (fusedValidation.Status != IrModuloScheduleStatusV1.Feasible)
            return Fallback(IrLoopTransformKindV1.LoopFusion,
                IrLoopTransformStatusV1.InvalidModel, firstLoop, options,
                "HCLT2014", "Fresh fused witness failed independent Core revalidation.");

        int baselineIi = checked(firstWitness.InitiationInterval + secondWitness.InitiationInterval);
        int benefit = checked((baselineIi - fusedSchedule.Witness.InitiationInterval) * 10_000 / baselineIi);
        int baselinePressure = Math.Max(PeakPressure(firstLoop.ValueAnalysis), PeakPressure(secondLoop.ValueAnalysis));
        int pressureDelta = PeakPressure(fusedLoop.ValueAnalysis) - baselinePressure;
        if (pressureDelta > options.Budgets.MaximumPressureIncrease ||
            benefit < options.Budgets.MinimumBenefitBasisPoints)
            return Fallback(IrLoopTransformKindV1.LoopFusion,
                IrLoopTransformStatusV1.DisabledFallback, firstLoop, options,
                "HCLT2015", "Fused candidate does not clear deterministic pressure/KPI gates.");

        transformed = MarkFresh(transformed);
        long workUnits = transformedOperations + fusedLoop.DistanceDependencies.Count +
            fusedLoop.ValueAnalysis.Intervals.Count + ScheduleWorkUnits(fusedSchedule);
        IrLoopTransformProfitabilityV1 profitability = Profitability(
            IrLoopTransformProfileDispositionV1.AbsentStaticPolicy,
            baselineIi,
            fusedSchedule.Witness.InitiationInterval,
            1,
            benefit,
            0,
            pressureDelta,
            0,
            workUnits,
            "Two statically equal independent iteration domains share one freshly validated kernel.");
        IrLoopTransformCandidateV1 accepted = Candidate(
            IrLoopTransformKindV1.LoopFusion, 1, IrLoopTransformCandidateStatusV1.Accepted,
            fusedSchedule.Witness.InitiationInterval, 0, pressureDelta, benefit, workUnits,
            "Independent adjacent loops fused after fresh cross-loop legality and resource analysis.");
        IrLoopTransformFreshFactsV1 facts = FreshFacts(
            fusedLoop.MutationStamp, fusedLoop.DistanceDagDigest, fusedLoop.ValueAnalysis.ValueFlowDigest,
            fusedMii.ProofStamp.ProofDigest, fusedSchedule.Witness.WitnessDigest,
            fusedValidation.ValidationDigest, AllStructuralFacts);
        string digest = ResultDigest(
            IrLoopTransformKindV1.LoopFusion, IrLoopTransformStatusV1.Accepted,
            firstLoop.LoopId, fusedLoop.LoopId, facts.FactsDigest, profitability.EvidenceDigest,
            [accepted], options.OptionsDigest);
        return new(
            IrLoopTransformKindV1.LoopFusion,
            IrLoopTransformStatusV1.Accepted,
            transformed,
            fusedLoop,
            null,
            fusedMii,
            fusedSchedule.Witness,
            [
                firstDomain.ProofDigest,
                secondDomain.ProofDigest,
                firstWitness.WitnessDigest,
                secondWitness.WitnessDigest,
                fusedLoop.DistanceDagDigest,
                fusedMii.ProofStamp.ProofDigest,
                fusedSchedule.Witness.WitnessDigest,
                fusedValidation.ValidationDigest
            ],
            AllStructuralFacts,
            facts,
            profitability,
            [accepted],
            firstLoop.LoopId,
            Array.Empty<IrRegionSchedulingDiagnosticV1>(),
            digest);
    }

    public IrLoopTransformResultV1 TransformUnrollAndJam(
        IrProgram program,
        IrCanonicalLoopV1 outerLoop,
        IrLoopMiiReportV1 outerMii,
        IrModuloScheduleWitnessV1 outerWitness,
        IrLoopIterationDomainProofV1 outerDomain,
        IrCanonicalLoopV1 innerLoop,
        IrLoopMiiReportV1 innerMii,
        IrModuloScheduleWitnessV1 innerWitness,
        IrLoopIterationDomainProofV1 innerDomain,
        HybridCpuMiiResourceModelV1? resourceModel = null,
        HybridCpuLoopTransformOptionsV1? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(outerLoop);
        ArgumentNullException.ThrowIfNull(outerMii);
        ArgumentNullException.ThrowIfNull(outerWitness);
        ArgumentNullException.ThrowIfNull(outerDomain);
        ArgumentNullException.ThrowIfNull(innerLoop);
        ArgumentNullException.ThrowIfNull(innerMii);
        ArgumentNullException.ThrowIfNull(innerWitness);
        ArgumentNullException.ThrowIfNull(innerDomain);
        resourceModel ??= HybridCpuMiiResourceModelV1.Default;
        options ??= HybridCpuLoopTransformOptionsV1.Production;
        if (!HasValidOptions(options) || !HasValidDomain(outerLoop, outerDomain) ||
            !HasValidDomain(innerLoop, innerDomain))
            return Fallback(IrLoopTransformKindV1.UnrollAndJam,
                IrLoopTransformStatusV1.InvalidModel, outerLoop, options,
                "HCLT3001", "Unroll-and-jam options or static iteration-domain proofs are invalid.");
        if (options.UnrollAndJamSwitch == IrLoopTransformSwitchV1.Disabled)
            return Fallback(IrLoopTransformKindV1.UnrollAndJam,
                IrLoopTransformStatusV1.DisabledFallback, outerLoop, options,
                "HCLT3002", "Unroll-and-jam is disabled; exact nested-loop fallback selected.");
        HashSet<int> outerBlocks = outerLoop.BlockIds.ToHashSet();
        HashSet<int> innerBlocks = innerLoop.BlockIds.ToHashSet();
        if (innerBlocks.Count == 0 || innerBlocks.Count >= outerBlocks.Count ||
            !innerBlocks.IsSubsetOf(outerBlocks) || innerLoop.PreheaderBlockId != outerLoop.HeaderBlockId ||
            outerLoop.ExitBlockIds.Count > 1 || innerLoop.ExitBlockIds.Count != 1 ||
            !outerBlocks.Contains(innerLoop.ExitBlockIds[0]))
            return Fallback(IrLoopTransformKindV1.UnrollAndJam,
                IrLoopTransformStatusV1.Ineligible, outerLoop, options,
                "HCLT3003", "Only a depth-two rectangular perfect nest with simple exits is eligible.");
        if (HasUnsupportedTransformEffect(outerLoop.Instructions) ||
            program.ValueFlow.Accesses.Any(access => outerLoop.Instructions.Any(instruction =>
                instruction.Index == access.InstructionIndex)))
            return Fallback(IrLoopTransformKindV1.UnrollAndJam,
                IrLoopTransformStatusV1.Ineligible, outerLoop, options,
                "HCLT3004", "The first jam slice requires effect-free, value-neutral nest operations.");
        if (outerLoop.Instructions.Where(instruction => !innerLoop.Instructions.Any(inner =>
                inner.Index == instruction.Index)).Any(static instruction => instruction.Opcode != HybridCpuOpcode.Nope))
            return Fallback(IrLoopTransformKindV1.UnrollAndJam,
                IrLoopTransformStatusV1.Ineligible, outerLoop, options,
                "HCLT3007", "Outer-only blocks must be value-neutral control scaffolding in the first jam slice.");
        HybridCpuModuloSchedulerV1 scheduler = new HybridCpuModuloSchedulerV1();
        if (scheduler.ValidateWitness(program, outerLoop, outerMii, outerWitness, resourceModel).Status !=
                IrModuloScheduleStatusV1.Feasible ||
            scheduler.ValidateWitness(program, innerLoop, innerMii, innerWitness, resourceModel).Status !=
                IrModuloScheduleStatusV1.Feasible)
            return Fallback(IrLoopTransformKindV1.UnrollAndJam,
                IrLoopTransformStatusV1.StaleProof, outerLoop, options,
                "HCLT3005", "An outer or inner kernel witness is stale or invalid.");

        List<IrLoopTransformCandidateV1> candidates = new List<IrLoopTransformCandidateV1>
        {
            Candidate(IrLoopTransformKindV1.UnrollAndJam, 1,
                IrLoopTransformCandidateStatusV1.Baseline, outerWitness.InitiationInterval,
                0, 0, 0, 0, "Exact original perfect-nest fallback.")
        };
        JamCandidateState? best = null;
        foreach (int factor in options.CandidateUnrollFactors.Where(static factor => factor is 2 or 4).Order())
        {
            int transformedCount = checked(program.Instructions.Count +
                innerLoop.Instructions.Count * (factor - 1));
            int codeDelta = transformedCount - program.Instructions.Count;
            if (transformedCount > options.Budgets.MaximumTransformedOperations ||
                checked(codeDelta * 100 / Math.Max(1, innerLoop.Instructions.Count)) >
                    options.Budgets.MaximumCodeGrowthPercent)
            {
                candidates.Add(Candidate(IrLoopTransformKindV1.UnrollAndJam, factor,
                    IrLoopTransformCandidateStatusV1.CodeGrowthLimit, null, codeDelta, 0, 0, 1,
                    "Jammed nest exceeds the operation or code-growth bound."));
                continue;
            }
            IrProgram transformed = BuildUnrollAndJamProgram(program, outerLoop, innerLoop, factor);
            long workUnits = transformed.Instructions.Count;
            if (CanonicalIrFrontendBoundaryV1.Validate(transformed).Status != IrFrontendAdapterStatus.Success)
            {
                candidates.Add(Candidate(IrLoopTransformKindV1.UnrollAndJam, factor,
                    IrLoopTransformCandidateStatusV1.AnalysisRejected, null, codeDelta, 0, 0,
                    workUnits, "Post-jam Canonical IR validation failed."));
                continue;
            }
            HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
            IrCanonicalLoopV1[] transformedLoops = analyzer.Canonicalize(transformed, resourceModel).Loops.ToArray();
            IrCanonicalLoopV1? transformedOuter = transformedLoops.SingleOrDefault(loop =>
                loop.HeaderBlockId == outerLoop.HeaderBlockId);
            IrCanonicalLoopV1? transformedInner = transformedLoops.SingleOrDefault(loop =>
                loop.HeaderBlockId == innerLoop.HeaderBlockId);
            if (transformedOuter is null || transformedInner is null ||
                transformedOuter.Status != IrCanonicalLoopStatusV1.Qualified ||
                transformedInner.Status != IrCanonicalLoopStatusV1.Qualified)
            {
                candidates.Add(Candidate(IrLoopTransformKindV1.UnrollAndJam, factor,
                    IrLoopTransformCandidateStatusV1.AnalysisRejected, null, codeDelta, 0, 0,
                    workUnits, "Fresh canonicalization rejected the transformed perfect nest."));
                continue;
            }
            IrLoopMiiReportV1 transformedOuterMii = analyzer.ComputeMii(
                transformed, transformedOuter, resourceModel);
            IrLoopMiiReportV1 transformedInnerMii = analyzer.ComputeMii(
                transformed, transformedInner, resourceModel);
            workUnits += transformedOuter.DistanceDependencies.Count + transformedInner.DistanceDependencies.Count +
                transformedOuter.ValueAnalysis.Intervals.Count;
            if (transformedOuterMii.Eligibility != IrLoopMiiEligibilityV1.EligibleLowerBound ||
                transformedInnerMii.Eligibility != IrLoopMiiEligibilityV1.EligibleLowerBound)
            {
                candidates.Add(Candidate(IrLoopTransformKindV1.UnrollAndJam, factor,
                    IrLoopTransformCandidateStatusV1.AnalysisRejected, null, codeDelta, 0, 0,
                    workUnits, "Fresh outer/inner dependency, pressure or MII analysis is incomplete."));
                continue;
            }
            IrModuloScheduleResultV1 outerSchedule = scheduler.Schedule(
                transformed, transformedOuter, transformedOuterMii, resourceModel);
            IrModuloScheduleResultV1 innerSchedule = scheduler.Schedule(
                transformed, transformedInner, transformedInnerMii, resourceModel);
            workUnits += ScheduleWorkUnits(outerSchedule) + ScheduleWorkUnits(innerSchedule);
            if (outerSchedule.Status != IrModuloScheduleStatusV1.Feasible || outerSchedule.Witness is null ||
                innerSchedule.Status != IrModuloScheduleStatusV1.Feasible || innerSchedule.Witness is null)
            {
                candidates.Add(Candidate(IrLoopTransformKindV1.UnrollAndJam, factor,
                    outerSchedule.Status == IrModuloScheduleStatusV1.BudgetExhausted ||
                    innerSchedule.Status == IrModuloScheduleStatusV1.BudgetExhausted
                        ? IrLoopTransformCandidateStatusV1.BudgetExhausted
                        : IrLoopTransformCandidateStatusV1.ScheduleRejected,
                    outerSchedule.ChosenIi, codeDelta, 0, 0, workUnits,
                    "Fresh outer or inner scheduling/placement failed."));
                continue;
            }
            IrModuloWitnessValidationV1 outerValidation = scheduler.ValidateWitness(
                transformed, transformedOuter, transformedOuterMii, outerSchedule.Witness, resourceModel);
            IrModuloWitnessValidationV1 innerValidation = scheduler.ValidateWitness(
                transformed, transformedInner, transformedInnerMii, innerSchedule.Witness, resourceModel);
            if (outerValidation.Status != IrModuloScheduleStatusV1.Feasible ||
                innerValidation.Status != IrModuloScheduleStatusV1.Feasible)
            {
                candidates.Add(Candidate(IrLoopTransformKindV1.UnrollAndJam, factor,
                    IrLoopTransformCandidateStatusV1.ScheduleRejected, outerSchedule.ChosenIi,
                    codeDelta, 0, 0, workUnits,
                    "Fresh outer or inner witness failed independent Core revalidation."));
                continue;
            }
            int pressureDelta = PeakPressure(transformedOuter.ValueAnalysis) - PeakPressure(outerLoop.ValueAnalysis);
            int denominator = checked(outerWitness.InitiationInterval * factor);
            int benefit = checked((denominator - outerSchedule.Witness.InitiationInterval) * 10_000 / denominator);
            if (pressureDelta > options.Budgets.MaximumPressureIncrease ||
                benefit < options.Budgets.MinimumBenefitBasisPoints)
            {
                candidates.Add(Candidate(IrLoopTransformKindV1.UnrollAndJam, factor,
                    pressureDelta > options.Budgets.MaximumPressureIncrease
                        ? IrLoopTransformCandidateStatusV1.PressureLimit
                        : IrLoopTransformCandidateStatusV1.NoKpiBenefit,
                    outerSchedule.ChosenIi, codeDelta, pressureDelta, benefit, workUnits,
                    "Jammed nest does not clear deterministic pressure/KPI gates."));
                continue;
            }
            IrLoopTransformCandidateV1 accepted = Candidate(
                IrLoopTransformKindV1.UnrollAndJam, factor, IrLoopTransformCandidateStatusV1.Accepted,
                outerSchedule.Witness.InitiationInterval, codeDelta, pressureDelta, benefit, workUnits,
                "Perfect nest outer iterations are cloned and inner bodies jammed with fresh proofs.");
            candidates.Add(accepted);
            JamCandidateState state = new JamCandidateState(
                factor, MarkFresh(transformed), transformedOuter, transformedOuterMii,
                outerSchedule.Witness, outerValidation, transformedInner, transformedInnerMii,
                innerSchedule.Witness, innerValidation, accepted);
            if (best is null || IsBetter(state, best)) best = state;
        }
        if (best is null)
            return FallbackWithCandidates(
                IrLoopTransformKindV1.UnrollAndJam, IrLoopTransformStatusV1.DisabledFallback,
                outerLoop, options, candidates, "HCLT3006",
                "No perfect-nest candidate cleared all fresh correctness, pressure and KPI gates.");

        long remainder = outerDomain.TripCount % best.Factor;
        IrLoopTransformProfitabilityV1 profitability = Profitability(
            IrLoopTransformProfileDispositionV1.AbsentStaticPolicy,
            outerWitness.InitiationInterval,
            best.OuterWitness.InitiationInterval,
            best.Factor,
            best.Candidate.BenefitBasisPoints,
            best.Candidate.CodeSizeDeltaOperations,
            best.Candidate.PressureDelta,
            remainder,
            candidates.Sum(static candidate => candidate.DeterministicWorkUnits),
            remainder == 0
                ? "Outer domain partitions exactly into jammed factors."
                : "Outer remainder executes through the exact original perfect-nest fallback.");
        string combinedDependency = HybridCpuLoopTransformContractV1.Hash(
            $"{best.OuterLoop.DistanceDagDigest}|{best.InnerLoop.DistanceDagDigest}");
        string combinedMii = HybridCpuLoopTransformContractV1.Hash(
            $"{best.OuterMii.ProofStamp.ProofDigest}|{best.InnerMii.ProofStamp.ProofDigest}");
        string combinedPlacement = HybridCpuLoopTransformContractV1.Hash(
            $"{best.OuterWitness.WitnessDigest}|{best.InnerWitness.WitnessDigest}");
        string combinedValidation = HybridCpuLoopTransformContractV1.Hash(
            $"{best.OuterValidation.ValidationDigest}|{best.InnerValidation.ValidationDigest}");
        IrLoopTransformFreshFactsV1 facts = FreshFacts(
            best.OuterLoop.MutationStamp, combinedDependency,
            best.OuterLoop.ValueAnalysis.ValueFlowDigest, combinedMii,
            combinedPlacement, combinedValidation, AllStructuralFacts);
        string digest = ResultDigest(
            IrLoopTransformKindV1.UnrollAndJam, IrLoopTransformStatusV1.Accepted,
            outerLoop.LoopId, best.OuterLoop.LoopId, facts.FactsDigest,
            profitability.EvidenceDigest, candidates, options.OptionsDigest);
        return new(
            IrLoopTransformKindV1.UnrollAndJam,
            IrLoopTransformStatusV1.Accepted,
            best.Program,
            best.OuterLoop,
            null,
            best.OuterMii,
            best.OuterWitness,
            [
                outerDomain.ProofDigest,
                innerDomain.ProofDigest,
                outerWitness.WitnessDigest,
                innerWitness.WitnessDigest,
                best.OuterLoop.DistanceDagDigest,
                best.InnerLoop.DistanceDagDigest,
                best.OuterMii.ProofStamp.ProofDigest,
                best.InnerMii.ProofStamp.ProofDigest,
                best.OuterWitness.WitnessDigest,
                best.InnerWitness.WitnessDigest,
                best.OuterValidation.ValidationDigest,
                best.InnerValidation.ValidationDigest
            ],
            AllStructuralFacts,
            facts,
            profitability,
            candidates,
            outerLoop.LoopId,
            Array.Empty<IrRegionSchedulingDiagnosticV1>(),
            digest);
    }

    private static IrModuloExpansionV1? BuildExpansion(
        IrProgram program,
        IrCanonicalLoopV1 loop,
        IrModuloScheduleWitnessV1 witness,
        IrModuloWitnessValidationV1 validation,
        long tripCount,
        out string? failure)
    {
        failure = null;
        if (witness.Operations.Count != loop.Instructions.Count || witness.Operations.Count == 0)
        {
            failure = "Kernel witness does not cover the canonical loop exactly.";
            return null;
        }
        int minimumCycle = witness.Operations.Min(static operation => operation.Cycle);
        int maximumCycle = witness.Operations.Max(static operation => operation.Cycle);
        int depth = checked((maximumCycle - minimumCycle) / witness.InitiationInterval + 1);
        int fillBoundary = checked((depth - 1) * witness.InitiationInterval);
        int drainBoundary = checked((int)tripCount * witness.InitiationInterval);
        Dictionary<int, IrInstruction> instructions = loop.Instructions.ToDictionary(static instruction => instruction.Index);
        Dictionary<int, IrModuloScheduledOperationV1> scheduled = witness.Operations
            .ToDictionary(static operation => operation.InstructionIndex);
        Dictionary<int, IrValueAccessV1[]> accesses = program.ValueFlow.Accesses
            .Where(access => instructions.ContainsKey(access.InstructionIndex))
            .GroupBy(static access => access.InstructionIndex)
            .ToDictionary(static group => group.Key, group => group.OrderBy(static access => access.Kind)
                .ThenBy(static access => access.ValueId, StringComparer.Ordinal).ToArray());
        List<IrModuloExpandedOperationV1> expanded = new List<IrModuloExpandedOperationV1>(checked((int)tripCount * loop.Instructions.Count));
        for (int iteration = 0; iteration < tripCount; iteration++)
        {
            foreach (IrModuloScheduledOperationV1 operation in witness.Operations
                         .OrderBy(static operation => operation.Cycle)
                         .ThenBy(static operation => operation.IssueSlot)
                         .ThenBy(static operation => operation.InstructionIndex))
            {
                int absolute = checked(iteration * witness.InitiationInterval + operation.Cycle - minimumCycle);
                IrModuloExpansionPhaseV1 phase = absolute < fillBoundary
                    ? IrModuloExpansionPhaseV1.Prolog
                    : absolute >= drainBoundary
                        ? IrModuloExpansionPhaseV1.Epilog
                        : IrModuloExpansionPhaseV1.Kernel;
                IrInstruction original = instructions[operation.InstructionIndex];
                int materializedIndex = expanded.Count;
                string instanceId = HybridCpuLoopTransformContractV1.Hash(string.Join('|',
                    "hybridcpu.modulo-expanded-operation/v1",
                    loop.LoopId,
                    witness.WitnessDigest,
                    iteration.ToString(CultureInfo.InvariantCulture),
                    original.StableIdentity,
                    absolute.ToString(CultureInfo.InvariantCulture),
                    operation.IssueSlot.ToString(CultureInfo.InvariantCulture)));
                IrSourceOriginChainV1 origin = AppendGeneratedOrigin(
                    original.OriginChain, instanceId, original.SourceSpan, $"modulo-{phase.ToString().ToLowerInvariant()}");
                IrInstruction clone = original with
                {
                    Index = materializedIndex,
                    StableIdentity = instanceId,
                    OriginChain = origin
                };
                IrExpandedValueBindingV1[] bindings = accesses.GetValueOrDefault(
                        original.Index, Array.Empty<IrValueAccessV1>())
                    .Select(access => BindValue(access, iteration)).ToArray();
                expanded.Add(new(
                    instanceId,
                    materializedIndex,
                    original.Index,
                    original.StableIdentity,
                    iteration,
                    absolute,
                    absolute % witness.InitiationInterval,
                    operation.IssueSlot,
                    phase,
                    clone,
                    bindings));
            }
        }
        expanded = expanded.OrderBy(static operation => operation.AbsoluteCycle)
            .ThenBy(static operation => operation.IssueSlot)
            .ThenBy(static operation => operation.OriginalInstructionIndex)
            .ThenBy(static operation => operation.SourceIteration).ToList();

        if (!ValidateExpandedPlacement(loop, expanded, out string placementDigest, out failure) ||
            !ValidateExpandedTemporal(loop, expanded, tripCount, out string temporalDigest, out failure))
            return null;

        IrLoopLivenessV1? loopLiveness = loop.ValueAnalysis.Loops.SingleOrDefault(candidate =>
            candidate.HeaderBlockId == loop.HeaderBlockId);
        string[] liveIn = loopLiveness?.LiveIn.Order(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
        string[] liveOut = loopLiveness?.LiveOut.Order(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
        IReadOnlyDictionary<string, string> liveInMapping = liveIn.ToDictionary(
            static value => value, static value => $"livein:{value}", StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> liveOutMapping = liveOut.ToDictionary(
            static value => value,
            value => tripCount == 0 ? $"livein:{value}" : Versioned(value, checked((int)tripCount - 1)),
            StringComparer.Ordinal);
        string expansionDigest = HybridCpuLoopTransformContractV1.Hash(string.Join('|',
            HybridCpuLoopTransformContractV1.ExpansionSchemaId,
            loop.LoopId,
            loop.VersionStamp,
            witness.WitnessDigest,
            tripCount.ToString(CultureInfo.InvariantCulture),
            witness.InitiationInterval.ToString(CultureInfo.InvariantCulture),
            depth.ToString(CultureInfo.InvariantCulture),
            string.Join(';', expanded.Select(OperationKey)),
            string.Join(';', liveInMapping.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key}:{pair.Value}")),
            string.Join(';', liveOutMapping.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key}:{pair.Value}")),
            validation.ValidationDigest,
            placementDigest,
            temporalDigest));
        return new(
            HybridCpuLoopTransformContractV1.ExpansionSchemaId,
            expansionDigest,
            loop.LoopId,
            loop.VersionStamp,
            witness.WitnessDigest,
            tripCount,
            witness.InitiationInterval,
            depth,
            expanded.Where(static operation => operation.Phase == IrModuloExpansionPhaseV1.Prolog).ToArray(),
            expanded.Where(static operation => operation.Phase == IrModuloExpansionPhaseV1.Kernel).ToArray(),
            expanded.Where(static operation => operation.Phase == IrModuloExpansionPhaseV1.Epilog).ToArray(),
            liveInMapping,
            liveOutMapping,
            placementDigest,
            temporalDigest);

        IrExpandedValueBindingV1 BindValue(IrValueAccessV1 access, int iteration)
        {
            int sourceIteration = access.Kind == IrValueAccessKind.PhiEdgeUse ? iteration - 1 : iteration;
            string version = sourceIteration < 0 ? $"livein:{access.ValueId}" : Versioned(access.ValueId, sourceIteration);
            return new(
                access.ValueId,
                version,
                access.Kind,
                sourceIteration,
                access.Kind == IrValueAccessKind.PhiEdgeUse ? sourceIteration : null);
        }
    }

    private static IrProgram BuildUnrolledProgram(
        IrProgram program,
        IrCanonicalLoopV1 loop,
        int factor)
    {
        int loopBlockId = loop.BlockIds.Single();
        List<InstructionDraft> drafts = new List<InstructionDraft>();
        Dictionary<int, List<InstructionDraft>> blockDrafts = new Dictionary<int, List<InstructionDraft>>();
        foreach (IrBasicBlock block in program.BasicBlocks.OrderBy(static block => block.StartInstructionIndex)
                     .ThenBy(static block => block.Id))
        {
            List<InstructionDraft> items = new List<InstructionDraft>();
            int lanes = block.Id == loopBlockId ? factor : 1;
            for (int lane = 0; lane < lanes; lane++)
            {
                foreach (IrInstruction instruction in block.Instructions.OrderBy(static instruction => instruction.Index))
                {
                    InstructionDraft draft = new InstructionDraft(block.Id, instruction, lane, drafts.Count);
                    items.Add(draft);
                    drafts.Add(draft);
                }
            }
            blockDrafts.Add(block.Id, items);
        }
        Dictionary<(int Index, int Lane), int> map = drafts.ToDictionary(
            static draft => (draft.Original.Index, draft.Lane),
            static draft => draft.NewIndex);
        Dictionary<int, int> firstMap = drafts.Where(static draft => draft.Lane == 0)
            .ToDictionary(static draft => draft.Original.Index, static draft => draft.NewIndex);
        Dictionary<int, int> lastLoopMap = drafts.Where(draft => draft.BlockId == loopBlockId && draft.Lane == factor - 1)
            .ToDictionary(static draft => draft.Original.Index, static draft => draft.NewIndex);
        Dictionary<(int Index, int Lane), IrInstruction> materialized = new Dictionary<(int Index, int Lane), IrInstruction>();
        foreach (InstructionDraft draft in drafts)
        {
            IrInstruction original = draft.Original;
            bool generated = draft.BlockId == loopBlockId;
            string identity = generated
                ? HybridCpuLoopTransformContractV1.Hash(string.Join('|',
                    "hybridcpu.unrolled-operation/v1",
                    loop.LoopId,
                    factor.ToString(CultureInfo.InvariantCulture),
                    draft.Lane.ToString(CultureInfo.InvariantCulture),
                    original.StableIdentity))
                : original.StableIdentity;
            IrSourceOriginChainV1 origin = generated
                ? AppendGeneratedOrigin(original.OriginChain, identity, original.SourceSpan,
                    $"unroll-factor-{factor}-lane-{draft.Lane}")
                : original.OriginChain;
            int? branchTarget = original.Annotation.ResolvedBranchTargetInstructionIndex is int target &&
                firstMap.TryGetValue(target, out int mappedTarget)
                ? mappedTarget
                : original.Annotation.ResolvedBranchTargetInstructionIndex;
            materialized[(original.Index, draft.Lane)] = original with
            {
                Index = draft.NewIndex,
                StableIdentity = identity,
                OriginChain = origin,
                Annotation = original.Annotation with { ResolvedBranchTargetInstructionIndex = branchTarget }
            };
        }
        IrBasicBlock[] blocks = program.BasicBlocks.Select(block =>
        {
            IrInstruction[] items = blockDrafts[block.Id]
                .Select(draft => materialized[(draft.Original.Index, draft.Lane)]).ToArray();
            return block with
            {
                StartInstructionIndex = items[0].Index,
                EndInstructionIndex = items[^1].Index,
                Instructions = items
            };
        }).ToArray();
        IrInstruction[] instructions = drafts.Select(draft =>
            materialized[(draft.Original.Index, draft.Lane)]).ToArray();

        List<IrValueAccessV1> accesses = new List<IrValueAccessV1>();
        HashSet<int> loopIndexes = loop.Instructions.Select(static instruction => instruction.Index).ToHashSet();
        foreach (IrValueAccessV1 access in program.ValueFlow.Accesses)
        {
            if (!loopIndexes.Contains(access.InstructionIndex))
            {
                accesses.Add(access with { InstructionIndex = firstMap[access.InstructionIndex] });
                continue;
            }
            if (access.Kind == IrValueAccessKind.PhiEdgeUse)
            {
                accesses.Add(access with { InstructionIndex = map[(access.InstructionIndex, 0)] });
                continue;
            }
            for (int lane = 0; lane < factor; lane++)
                accesses.Add(access with { InstructionIndex = map[(access.InstructionIndex, lane)] });
        }
        IrValueFlowGraphV1 flow = program.ValueFlow with
        {
            Accesses = accesses.OrderBy(static access => access.InstructionIndex)
                .ThenBy(static access => access.Kind)
                .ThenBy(static access => access.ValueId, StringComparer.Ordinal).ToArray()
        };
        IrProgramLabel[] labels = program.Labels.Select(label => label with
        {
            InstructionIndex = firstMap[label.InstructionIndex]
        }).ToArray();
        IrEntryPointMetadata[] entries = program.EntryPoints.Select(entry => entry with
        {
            InstructionIndex = firstMap[entry.InstructionIndex]
        }).ToArray();
        IrSection[] sections = program.Sections.Select(section => section with
        {
            StartInstructionIndex = firstMap[section.StartInstructionIndex],
            EndInstructionIndex = MapEnd(section.EndInstructionIndex)
        }).ToArray();
        IrFunction[] functions = program.Functions.Select(function => function with
        {
            EntryInstructionIndex = firstMap[function.EntryInstructionIndex],
            EndInstructionIndex = MapEnd(function.EndInstructionIndex)
        }).ToArray();
        IrSectionSymbolGroup[] sectionGroups = sections.Select(section => new IrSectionSymbolGroup(
            section,
            blocks.Where(block => section.BlockIds.Contains(block.Id)).ToArray(),
            functions.Where(function => section.FunctionNames.Contains(function.Name)).ToArray(),
            labels.Where(label => string.Equals(label.SectionName, section.Name, StringComparison.Ordinal)).ToArray(),
            entries.Where(entry => string.Equals(entry.SectionName, section.Name, StringComparison.Ordinal)).ToArray())).ToArray();
        IrFunctionSymbolGroup[] functionGroups = functions.Select(function => new IrFunctionSymbolGroup(
            function,
            blocks.Where(block => function.BlockIds.Contains(block.Id)).ToArray(),
            labels.Where(label => string.Equals(label.FunctionName, function.Name, StringComparison.Ordinal)).ToArray(),
            entries.Where(entry => string.Equals(entry.FunctionName, function.Name, StringComparison.Ordinal)).ToArray())).ToArray();
        IrDerivedFactVersionsV1 invalidated = program.Contract.DerivedFacts.InvalidateAfterScheduleChangingMutation();
        return program with
        {
            Instructions = instructions,
            ControlFlowGraph = program.ControlFlowGraph with { Blocks = blocks },
            Labels = labels,
            EntryPoints = entries,
            Sections = sections,
            Functions = functions,
            Symbols = new(labels, entries, sections, functions, sectionGroups, functionGroups),
            ValueFlow = flow,
            FrontendEvidence = IrFrontendAnalysisEvidenceSetV1.Empty,
            Contract = program.Contract with { DerivedFacts = invalidated }
        };

        int MapEnd(int index) => lastLoopMap.GetValueOrDefault(index, firstMap[index]);
    }

    private static IrProgram BuildFusedProgram(
        IrProgram program,
        IrCanonicalLoopV1 firstLoop,
        IrCanonicalLoopV1 secondLoop)
    {
        int firstBlockId = firstLoop.BlockIds.Single();
        int secondBlockId = secondLoop.BlockIds.Single();
        int connectorId = secondLoop.PreheaderBlockId;
        HashSet<int> removedBlocks = [connectorId, secondBlockId];
        List<IrBasicBlock> retained = program.BasicBlocks
            .Where(block => !removedBlocks.Contains(block.Id))
            .OrderBy(static block => block.StartInstructionIndex).ThenBy(static block => block.Id).ToList();
        IrBasicBlock firstBlock = retained.Single(block => block.Id == firstBlockId);
        List<(int BlockId, IrInstruction Instruction, bool Generated)> drafts = new List<(int BlockId, IrInstruction Instruction, bool Generated)>();
        foreach (IrBasicBlock block in retained)
        {
            if (block.Id == firstBlockId)
            {
                drafts.AddRange(firstLoop.Instructions.OrderBy(static instruction => instruction.Index)
                    .Select(instruction => (firstBlockId, instruction, true)));
                drafts.AddRange(secondLoop.Instructions.OrderBy(static instruction => instruction.Index)
                    .Select(instruction => (firstBlockId, instruction, true)));
            }
            else
            {
                drafts.AddRange(block.Instructions.OrderBy(static instruction => instruction.Index)
                    .Select(instruction => (block.Id, instruction, false)));
            }
        }
        Dictionary<int, int> indexMap = drafts.Select((draft, index) => (draft.Instruction.Index, index))
            .ToDictionary(static item => item.Index, static item => item.index);
        Dictionary<int, IrInstruction> instructionsByOriginal = drafts.Select((draft, index) =>
        {
            string identity = draft.Generated
                ? HybridCpuLoopTransformContractV1.Hash(string.Join('|',
                    "hybridcpu.fused-operation/v1", firstLoop.LoopId, secondLoop.LoopId,
                    draft.Instruction.StableIdentity, index.ToString(CultureInfo.InvariantCulture)))
                : draft.Instruction.StableIdentity;
            IrSourceOriginChainV1 origin = draft.Generated
                ? AppendGeneratedOrigin(draft.Instruction.OriginChain, identity,
                    draft.Instruction.SourceSpan, "loop-fusion")
                : draft.Instruction.OriginChain;
            int? branchTarget = draft.Instruction.Annotation.ResolvedBranchTargetInstructionIndex is int target &&
                indexMap.TryGetValue(target, out int mapped) ? mapped : null;
            return (draft.Instruction.Index, Instruction: draft.Instruction with
            {
                Index = index,
                StableIdentity = identity,
                OriginChain = origin,
                Annotation = draft.Instruction.Annotation with
                {
                    ResolvedBranchTargetInstructionIndex = branchTarget
                }
            });
        }).ToDictionary(static item => item.Index, static item => item.Instruction);
        IrControlFlowEdge[] edges = program.ControlFlowGraph.Edges
            .Where(edge => !removedBlocks.Contains(edge.SourceBlockId) &&
                !removedBlocks.Contains(edge.TargetBlockId))
            .Append(new(firstBlockId, firstBlockId, IrControlFlowEdgeKind.Branch))
            .Concat(secondLoop.ExitBlockIds.Select(exit =>
                new IrControlFlowEdge(firstBlockId, exit, IrControlFlowEdgeKind.Fallthrough)))
            .Distinct().OrderBy(static edge => edge.SourceBlockId)
            .ThenBy(static edge => edge.TargetBlockId).ThenBy(static edge => edge.Kind).ToArray();
        IrBasicBlock[] blocks = retained.Select(block =>
        {
            IrInstruction[] items = block.Id == firstBlockId
                ? [
                    .. firstLoop.Instructions.OrderBy(static instruction => instruction.Index)
                        .Select(instruction => instructionsByOriginal[instruction.Index]),
                    .. secondLoop.Instructions.OrderBy(static instruction => instruction.Index)
                        .Select(instruction => instructionsByOriginal[instruction.Index])
                ]
                : block.Instructions.Select(instruction => instructionsByOriginal[instruction.Index]).ToArray();
            int[] predecessors = edges.Where(edge => edge.TargetBlockId == block.Id)
                .Select(static edge => edge.SourceBlockId).Distinct().Order().ToArray();
            int[] successors = edges.Where(edge => edge.SourceBlockId == block.Id)
                .Select(static edge => edge.TargetBlockId).Distinct().Order().ToArray();
            return block with
            {
                StartInstructionIndex = items[0].Index,
                EndInstructionIndex = items[^1].Index,
                Instructions = items,
                PredecessorBlockIds = predecessors,
                SuccessorBlockIds = successors,
                ExitBlock = successors.Length == 0
            };
        }).ToArray();
        IrInstruction[] instructions = blocks.OrderBy(static block => block.StartInstructionIndex)
            .SelectMany(static block => block.Instructions).ToArray();
        IrValueAccessV1[] accesses = program.ValueFlow.Accesses
            .Where(access => indexMap.ContainsKey(access.InstructionIndex))
            .Select(access => access with
            {
                InstructionIndex = indexMap[access.InstructionIndex],
                PhiSourceBlockId = Redirect(access.PhiSourceBlockId),
                PhiTargetBlockId = Redirect(access.PhiTargetBlockId)
            }).OrderBy(static access => access.InstructionIndex).ThenBy(static access => access.Kind)
            .ThenBy(static access => access.ValueId, StringComparer.Ordinal).ToArray();
        IrProgramLabel[] labels = program.Labels.Where(label => indexMap.ContainsKey(label.InstructionIndex))
            .Select(label => label with
            {
                InstructionIndex = indexMap[label.InstructionIndex],
                BlockId = Redirect(label.BlockId)!.Value
            }).ToArray();
        IrEntryPointMetadata[] entries = program.EntryPoints
            .Where(entry => indexMap.ContainsKey(entry.InstructionIndex))
            .Select(entry => entry with
            {
                InstructionIndex = indexMap[entry.InstructionIndex],
                BlockId = Redirect(entry.BlockId)!.Value
            }).ToArray();
        IrSection[] sections = program.Sections.Select(section =>
        {
            int[] blockIds = section.BlockIds.Select(id => Redirect(id)!.Value).Distinct().Order().ToArray();
            IrBasicBlock[] owned = blocks.Where(block => blockIds.Contains(block.Id)).ToArray();
            return section with
            {
                StartInstructionIndex = owned.Min(static block => block.StartInstructionIndex),
                EndInstructionIndex = owned.Max(static block => block.EndInstructionIndex),
                BlockIds = blockIds
            };
        }).ToArray();
        IrFunction[] functions = program.Functions.Select(function =>
        {
            int[] blockIds = function.BlockIds.Select(id => Redirect(id)!.Value).Distinct().Order().ToArray();
            IrBasicBlock[] owned = blocks.Where(block => blockIds.Contains(block.Id)).ToArray();
            return function with
            {
                EntryInstructionIndex = indexMap.GetValueOrDefault(function.EntryInstructionIndex,
                    owned.Min(static block => block.StartInstructionIndex)),
                EntryBlockId = Redirect(function.EntryBlockId)!.Value,
                EndInstructionIndex = owned.Max(static block => block.EndInstructionIndex),
                BlockIds = blockIds
            };
        }).ToArray();
        IrSectionSymbolGroup[] sectionGroups = sections.Select(section => new IrSectionSymbolGroup(
            section,
            blocks.Where(block => section.BlockIds.Contains(block.Id)).ToArray(),
            functions.Where(function => section.FunctionNames.Contains(function.Name)).ToArray(),
            labels.Where(label => string.Equals(label.SectionName, section.Name, StringComparison.Ordinal)).ToArray(),
            entries.Where(entry => string.Equals(entry.SectionName, section.Name, StringComparison.Ordinal)).ToArray())).ToArray();
        IrFunctionSymbolGroup[] functionGroups = functions.Select(function => new IrFunctionSymbolGroup(
            function,
            blocks.Where(block => function.BlockIds.Contains(block.Id)).ToArray(),
            labels.Where(label => string.Equals(label.FunctionName, function.Name, StringComparison.Ordinal)).ToArray(),
            entries.Where(entry => string.Equals(entry.FunctionName, function.Name, StringComparison.Ordinal)).ToArray())).ToArray();
        IrDerivedFactVersionsV1 invalidated = program.Contract.DerivedFacts.InvalidateAfterScheduleChangingMutation();
        return program with
        {
            Instructions = instructions,
            ControlFlowGraph = new(blocks, edges),
            Labels = labels,
            EntryPoints = entries,
            Sections = sections,
            Functions = functions,
            Symbols = new(labels, entries, sections, functions, sectionGroups, functionGroups),
            ValueFlow = program.ValueFlow with { Accesses = accesses },
            FrontendEvidence = IrFrontendAnalysisEvidenceSetV1.Empty,
            Contract = program.Contract with { DerivedFacts = invalidated }
        };

        int? Redirect(int? blockId) => blockId switch
        {
            null => null,
            var id when id == connectorId || id == secondBlockId => firstBlockId,
            _ => blockId
        };
    }

    private static IrProgram BuildUnrollAndJamProgram(
        IrProgram program,
        IrCanonicalLoopV1 outerLoop,
        IrCanonicalLoopV1 innerLoop,
        int factor)
    {
        HashSet<int> transformedIndexes = innerLoop.Instructions.Select(static instruction => instruction.Index).ToHashSet();
        List<InstructionDraft> drafts = new List<InstructionDraft>();
        Dictionary<int, List<InstructionDraft>> blockDrafts = new Dictionary<int, List<InstructionDraft>>();
        foreach (IrBasicBlock block in program.BasicBlocks.OrderBy(static block => block.StartInstructionIndex)
                     .ThenBy(static block => block.Id))
        {
            List<InstructionDraft> items = new List<InstructionDraft>();
            int lanes = innerLoop.BlockIds.Contains(block.Id) ? factor : 1;
            for (int lane = 0; lane < lanes; lane++)
            {
                foreach (IrInstruction instruction in block.Instructions.OrderBy(static instruction => instruction.Index))
                {
                    InstructionDraft draft = new InstructionDraft(block.Id, instruction, lane, drafts.Count);
                    items.Add(draft);
                    drafts.Add(draft);
                }
            }
            blockDrafts.Add(block.Id, items);
        }
        Dictionary<(int Index, int Lane), int> map = drafts.ToDictionary(
            static draft => (draft.Original.Index, draft.Lane), static draft => draft.NewIndex);
        Dictionary<int, int> firstMap = drafts.Where(static draft => draft.Lane == 0)
            .ToDictionary(static draft => draft.Original.Index, static draft => draft.NewIndex);
        Dictionary<(int Index, int Lane), IrInstruction> materialized = drafts.ToDictionary(
            static draft => (draft.Original.Index, draft.Lane), draft =>
            {
                bool generated = transformedIndexes.Contains(draft.Original.Index);
                string identity = generated
                    ? HybridCpuLoopTransformContractV1.Hash(string.Join('|',
                        "hybridcpu.unroll-jam-operation/v1", outerLoop.LoopId,
                        factor.ToString(CultureInfo.InvariantCulture),
                        draft.Lane.ToString(CultureInfo.InvariantCulture),
                        draft.Original.StableIdentity))
                    : draft.Original.StableIdentity;
                IrSourceOriginChainV1 origin = generated
                    ? AppendGeneratedOrigin(draft.Original.OriginChain, identity,
                        draft.Original.SourceSpan, $"unroll-jam-factor-{factor}-lane-{draft.Lane}")
                    : draft.Original.OriginChain;
                int? target = draft.Original.Annotation.ResolvedBranchTargetInstructionIndex is int originalTarget &&
                    firstMap.TryGetValue(originalTarget, out int mappedTarget) ? mappedTarget : null;
                return draft.Original with
                {
                    Index = draft.NewIndex,
                    StableIdentity = identity,
                    OriginChain = origin,
                    Annotation = draft.Original.Annotation with
                    {
                        ResolvedBranchTargetInstructionIndex = target
                    }
                };
            });
        IrBasicBlock[] blocks = program.BasicBlocks.Select(block =>
        {
            IrInstruction[] items = blockDrafts[block.Id]
                .Select(draft => materialized[(draft.Original.Index, draft.Lane)]).ToArray();
            return block with
            {
                StartInstructionIndex = items[0].Index,
                EndInstructionIndex = items[^1].Index,
                Instructions = items
            };
        }).ToArray();
        IrInstruction[] instructions = drafts.Select(draft =>
            materialized[(draft.Original.Index, draft.Lane)]).ToArray();
        List<IrValueAccessV1> accesses = new List<IrValueAccessV1>();
        foreach (IrValueAccessV1 access in program.ValueFlow.Accesses)
        {
            if (!transformedIndexes.Contains(access.InstructionIndex))
            {
                accesses.Add(access with { InstructionIndex = firstMap[access.InstructionIndex] });
                continue;
            }
            for (int lane = 0; lane < factor; lane++)
                accesses.Add(access with { InstructionIndex = map[(access.InstructionIndex, lane)] });
        }
        IrProgramLabel[] labels = program.Labels.Select(label => label with
        {
            InstructionIndex = firstMap[label.InstructionIndex]
        }).ToArray();
        IrEntryPointMetadata[] entries = program.EntryPoints.Select(entry => entry with
        {
            InstructionIndex = firstMap[entry.InstructionIndex]
        }).ToArray();
        IrSection[] sections = program.Sections.Select(section =>
        {
            IrBasicBlock[] owned = blocks.Where(block => section.BlockIds.Contains(block.Id)).ToArray();
            return section with
            {
                StartInstructionIndex = owned.Min(static block => block.StartInstructionIndex),
                EndInstructionIndex = owned.Max(static block => block.EndInstructionIndex)
            };
        }).ToArray();
        IrFunction[] functions = program.Functions.Select(function =>
        {
            IrBasicBlock[] owned = blocks.Where(block => function.BlockIds.Contains(block.Id)).ToArray();
            return function with
            {
                EntryInstructionIndex = firstMap[function.EntryInstructionIndex],
                EndInstructionIndex = owned.Max(static block => block.EndInstructionIndex)
            };
        }).ToArray();
        IrSectionSymbolGroup[] sectionGroups = sections.Select(section => new IrSectionSymbolGroup(
            section,
            blocks.Where(block => section.BlockIds.Contains(block.Id)).ToArray(),
            functions.Where(function => section.FunctionNames.Contains(function.Name)).ToArray(),
            labels.Where(label => string.Equals(label.SectionName, section.Name, StringComparison.Ordinal)).ToArray(),
            entries.Where(entry => string.Equals(entry.SectionName, section.Name, StringComparison.Ordinal)).ToArray())).ToArray();
        IrFunctionSymbolGroup[] functionGroups = functions.Select(function => new IrFunctionSymbolGroup(
            function,
            blocks.Where(block => function.BlockIds.Contains(block.Id)).ToArray(),
            labels.Where(label => string.Equals(label.FunctionName, function.Name, StringComparison.Ordinal)).ToArray(),
            entries.Where(entry => string.Equals(entry.FunctionName, function.Name, StringComparison.Ordinal)).ToArray())).ToArray();
        IrDerivedFactVersionsV1 invalidated = program.Contract.DerivedFacts.InvalidateAfterScheduleChangingMutation();
        return program with
        {
            Instructions = instructions,
            ControlFlowGraph = program.ControlFlowGraph with { Blocks = blocks },
            Labels = labels,
            EntryPoints = entries,
            Sections = sections,
            Functions = functions,
            Symbols = new(labels, entries, sections, functions, sectionGroups, functionGroups),
            ValueFlow = program.ValueFlow with
            {
                Accesses = accesses.OrderBy(static access => access.InstructionIndex)
                    .ThenBy(static access => access.Kind)
                    .ThenBy(static access => access.ValueId, StringComparer.Ordinal).ToArray()
            },
            FrontendEvidence = IrFrontendAnalysisEvidenceSetV1.Empty,
            Contract = program.Contract with { DerivedFacts = invalidated }
        };
    }

    private static IrProgram MarkFresh(IrProgram program)
    {
        IrDerivedFactVersionsV1 facts = program.Contract.DerivedFacts
            .WithDependenciesCurrent()
            .WithValueAnalysisCurrent() with
        {
            Mii = program.Contract.DerivedFacts.ProgramMutation,
            Placement = program.Contract.DerivedFacts.ProgramMutation
        };
        return program with { Contract = program.Contract with { DerivedFacts = facts } };
    }

    private static bool ValidateExpandedPlacement(
        IrCanonicalLoopV1 loop,
        IReadOnlyList<IrModuloExpandedOperationV1> expanded,
        out string digest,
        out string? failure)
    {
        foreach (IrModuloExpandedOperationV1 operation in expanded)
        {
            IrIssueSlotMask slot = (IrIssueSlotMask)(1 << operation.IssueSlot);
            IrInstruction original = loop.Instructions.Single(instruction =>
                instruction.Index == operation.OriginalInstructionIndex);
            if ((original.Annotation.StructurallyAllowedSlots & slot) == 0)
            {
                digest = string.Empty;
                failure = "Expanded operation uses a slot outside the original structural slot set.";
                return false;
            }
        }
        if (expanded.GroupBy(static operation => (operation.AbsoluteCycle, operation.IssueSlot))
            .Any(static group => group.Count() != 1))
        {
            digest = string.Empty;
            failure = "Expanded prolog/kernel/epilog contains an exact W=8 slot collision.";
            return false;
        }
        digest = HybridCpuLoopTransformContractV1.Hash(string.Join('|',
            "hybridcpu.expanded-placement-validation/v1",
            loop.LoopId,
            string.Join(';', expanded.Select(static operation => string.Join(':',
                operation.SourceIteration,
                operation.OriginalInstructionIndex,
                operation.AbsoluteCycle,
                operation.IssueSlot)))));
        failure = null;
        return true;
    }

    private static bool ValidateExpandedTemporal(
        IrCanonicalLoopV1 loop,
        IReadOnlyList<IrModuloExpandedOperationV1> expanded,
        long tripCount,
        out string digest,
        out string? failure)
    {
        Dictionary<(int Instruction, int Iteration), IrModuloExpandedOperationV1> instances = expanded
            .ToDictionary(static operation => (operation.OriginalInstructionIndex, operation.SourceIteration));
        List<string> validated = new List<string>();
        foreach (IrLoopDistanceDependencyV1 edge in loop.DistanceDependencies.OrderBy(static edge => edge.EdgeId, StringComparer.Ordinal))
        {
            for (int producerIteration = 0; producerIteration < tripCount; producerIteration++)
            {
                int consumerIteration = checked(producerIteration + edge.IterationDistance);
                if (consumerIteration >= tripCount) continue;
                IrModuloExpandedOperationV1 producer = instances[(edge.ProducerInstructionIndex, producerIteration)];
                IrModuloExpandedOperationV1 consumer = instances[(edge.ConsumerInstructionIndex, consumerIteration)];
                if (consumer.AbsoluteCycle - producer.AbsoluteCycle < edge.LatencyCycles)
                {
                    digest = string.Empty;
                    failure = $"Expanded temporal mapping violates dependence {edge.EdgeId}.";
                    return false;
                }
                validated.Add($"{edge.EdgeId}:{producerIteration}:{consumerIteration}:{producer.AbsoluteCycle}:{consumer.AbsoluteCycle}");
            }
        }
        digest = HybridCpuLoopTransformContractV1.Hash(string.Join('|',
            "hybridcpu.expanded-temporal-validation/v1",
            loop.DistanceDagDigest,
            string.Join(';', validated)));
        failure = null;
        return true;
    }

    private static IrSourceOriginChainV1 AppendGeneratedOrigin(
        IrSourceOriginChainV1 original,
        string identity,
        IrSourceSpan? span,
        string transform) => new(
            original.SchemaVersion,
            [
                .. original.Links,
                new(
                    $"generated:{transform}:{identity}",
                    IrSourceOriginKind.Generated,
                    "HybridCPU.Compiler.Core.Transforms",
                    "1",
                    span,
                    IrFrontendEvidenceTrust.ValidatedStructural)
            ]);

    private static bool HasUnsupportedTransformEffect(IReadOnlyList<IrInstruction> instructions) =>
        instructions.Any(static instruction =>
            instruction.Annotation.ControlFlowKind != IrControlFlowKind.None ||
            instruction.Annotation.IsBarrierLike ||
            instruction.Annotation.MayTrap ||
            instruction.Semantics.OperationMayFault ||
            instruction.SideEffects.Memory.Kind.HasFlag(IrMemoryEffectKind.Atomic) ||
            instruction.SideEffects.Memory.Kind.HasFlag(IrMemoryEffectKind.Volatile) ||
            instruction.SideEffects.Memory.Kind.HasFlag(IrMemoryEffectKind.Fence) ||
            instruction.SideEffects.Memory.Kind.HasFlag(IrMemoryEffectKind.Unknown) ||
            instruction.SideEffects.ArchitecturalEffects != IrArchitecturalEffectKind.None);

    private static bool HasValidDomain(
        IrCanonicalLoopV1 loop,
        IrLoopIterationDomainProofV1 proof)
    {
        try
        {
            return proof.Step > 0 && proof.ExclusiveLimit >= proof.InitialValue &&
                string.Equals(proof.LoopId, loop.LoopId, StringComparison.Ordinal) &&
                string.Equals(proof.LoopVersionStamp, loop.VersionStamp, StringComparison.Ordinal) &&
                string.Equals(proof.ProofDigest, IrLoopIterationDomainProofV1.Create(
                    loop, proof.InitialValue, proof.ExclusiveLimit, proof.Step).ProofDigest,
                    StringComparison.Ordinal);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool CrossLoopMemoryIsIndependent(
        IrCanonicalLoopV1 first,
        IrCanonicalLoopV1 second)
    {
        IrInstruction[] firstMemory = first.Instructions.Where(static instruction =>
            instruction.SideEffects.Memory.Kind != IrMemoryEffectKind.None).ToArray();
        IrInstruction[] secondMemory = second.Instructions.Where(static instruction =>
            instruction.SideEffects.Memory.Kind != IrMemoryEffectKind.None).ToArray();
        foreach (IrInstruction left in firstMemory)
        {
            foreach (IrInstruction right in secondMemory)
            {
                if (HybridCpuRegionSchedulerV1.ClassifyMemoryRelation(
                        left.SideEffects.Memory, right.SideEffects.Memory) != IrRegionMemoryRelationV1.NoAlias)
                    return false;
            }
        }
        return true;
    }

    private static bool CrossLoopValuesAreIndependent(
        IrProgram program,
        IrCanonicalLoopV1 first,
        IrCanonicalLoopV1 second)
    {
        HashSet<int> firstIndexes = first.Instructions.Select(static instruction => instruction.Index).ToHashSet();
        HashSet<int> secondIndexes = second.Instructions.Select(static instruction => instruction.Index).ToHashSet();
        HashSet<string> firstValues = program.ValueFlow.Accesses
            .Where(access => firstIndexes.Contains(access.InstructionIndex))
            .Select(static access => access.ValueId).ToHashSet(StringComparer.Ordinal);
        return !program.ValueFlow.Accesses
            .Where(access => secondIndexes.Contains(access.InstructionIndex))
            .Select(static access => access.ValueId).Any(firstValues.Contains);
    }

    private static bool FusionRemovesExternallyNamedBoundary(
        IrProgram program,
        int connectorBlockId,
        int secondLoopBlockId)
    {
        HashSet<int> removedBlocks = [connectorBlockId, secondLoopBlockId];
        HashSet<int> removedInstructions = program.BasicBlocks
            .Where(block => removedBlocks.Contains(block.Id))
            .SelectMany(static block => block.Instructions)
            .Select(static instruction => instruction.Index)
            .ToHashSet();
        return program.Labels.Any(label => removedBlocks.Contains(label.BlockId) ||
                removedInstructions.Contains(label.InstructionIndex)) ||
            program.EntryPoints.Any(entry => removedBlocks.Contains(entry.BlockId) ||
                removedInstructions.Contains(entry.InstructionIndex)) ||
            program.Functions.Any(function => removedBlocks.Contains(function.EntryBlockId) ||
                removedInstructions.Contains(function.EntryInstructionIndex));
    }

    private static bool HasUnsupportedUnrollValueFlow(
        IrProgram program,
        IrCanonicalLoopV1 loop)
    {
        HashSet<int> indexes = loop.Instructions.Select(static instruction => instruction.Index).ToHashSet();
        HashSet<string> used = program.ValueFlow.Accesses
            .Where(access => indexes.Contains(access.InstructionIndex))
            .Select(static access => access.ValueId).ToHashSet(StringComparer.Ordinal);
        return program.ValueFlow.Values.Any(value => used.Contains(value.StableId) &&
            value.Allocation.IsAllocatable && !value.Allocation.FixedRegisterId.HasValue);
    }

    private static int PeakPressure(IrValueAnalysisReportV1 report) => report.Pressure
        .SelectMany(static block => block.RegisterGroups)
        .Select(static pressure => pressure.PeakLiveValues)
        .DefaultIfEmpty(0)
        .Max();

    private static long ScheduleWorkUnits(IrModuloScheduleResultV1 schedule) => schedule.Attempts.Sum(static attempt =>
        (long)attempt.SdcRelaxations + attempt.CandidateStates + attempt.ResourceCuts +
        attempt.RefinementIterations + attempt.ExactPlacementStates);

    private static bool IsBetter(CandidateState candidate, CandidateState current)
    {
        long candidateCost = (long)candidate.Witness.InitiationInterval * current.Factor;
        long currentCost = (long)current.Witness.InitiationInterval * candidate.Factor;
        if (candidateCost != currentCost) return candidateCost < currentCost;
        if (candidate.Candidate.CodeSizeDeltaOperations != current.Candidate.CodeSizeDeltaOperations)
            return candidate.Candidate.CodeSizeDeltaOperations < current.Candidate.CodeSizeDeltaOperations;
        return candidate.Factor < current.Factor;
    }

    private static bool IsBetter(JamCandidateState candidate, JamCandidateState current)
    {
        long candidateCost = (long)candidate.OuterWitness.InitiationInterval * current.Factor;
        long currentCost = (long)current.OuterWitness.InitiationInterval * candidate.Factor;
        if (candidateCost != currentCost) return candidateCost < currentCost;
        if (candidate.Candidate.CodeSizeDeltaOperations != current.Candidate.CodeSizeDeltaOperations)
            return candidate.Candidate.CodeSizeDeltaOperations < current.Candidate.CodeSizeDeltaOperations;
        return candidate.Factor < current.Factor;
    }

    private static bool HasValidOptions(HybridCpuLoopTransformOptionsV1 options)
    {
        if (options.Budgets is null || options.CandidateUnrollFactors is null ||
            string.IsNullOrWhiteSpace(options.OptionsDigest)) return false;
        try
        {
            HybridCpuLoopTransformOptionsV1.Validate(options.Budgets, options.CandidateUnrollFactors);
            return string.Equals(options.OptionsDigest, HybridCpuLoopTransformOptionsV1.Create(
                options.Budgets,
                options.CandidateUnrollFactors,
                options.ModuloExpansionSwitch,
                options.ArchitectureUnrollSwitch,
                options.FusionSwitch,
                options.UnrollAndJamSwitch).OptionsDigest, StringComparison.Ordinal);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool HasValidProfile(IrLoopTransformProfileV1 profile) =>
        profile.Disposition == IrLoopTransformProfileDispositionV1.AbsentStaticPolicy
            ? profile.ExpectedTripCount is null && string.Equals(profile.ProfileDigest, "absent", StringComparison.Ordinal)
            : profile.ExpectedTripCount is >= 0 && IsSha256(profile.ProfileDigest);

    private static bool IsSha256(string value) => value.Length == 64 &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IrLoopTransformFreshFactsV1 FreshFacts(
        IrMutationStamp mutation,
        string dependency,
        string valueFlow,
        string mii,
        string placement,
        string validation,
        IrLoopInvalidatedAnalysisKindV1 recomputed)
    {
        string digest = HybridCpuLoopTransformContractV1.Hash(string.Join('|',
            "hybridcpu.loop-transform-fresh-facts/v1",
            mutation.Value.ToString(CultureInfo.InvariantCulture),
            dependency,
            valueFlow,
            mii,
            placement,
            validation,
            recomputed));
        return new(mutation, dependency, valueFlow, mii, placement, validation, recomputed, digest);
    }

    private static IrLoopTransformProfitabilityV1 Profitability(
        IrLoopTransformProfileDispositionV1 profile,
        int baselineIi,
        int candidateIi,
        int factor,
        int benefit,
        int codeDelta,
        int pressureDelta,
        long remainder,
        long workUnits,
        string reason)
    {
        string digest = HybridCpuLoopTransformContractV1.Hash(string.Join('|',
            "hybridcpu.loop-transform-profitability/v1",
            profile,
            baselineIi.ToString(CultureInfo.InvariantCulture),
            candidateIi.ToString(CultureInfo.InvariantCulture),
            factor.ToString(CultureInfo.InvariantCulture),
            benefit.ToString(CultureInfo.InvariantCulture),
            codeDelta.ToString(CultureInfo.InvariantCulture),
            pressureDelta.ToString(CultureInfo.InvariantCulture),
            remainder.ToString(CultureInfo.InvariantCulture),
            workUnits.ToString(CultureInfo.InvariantCulture),
            reason));
        return new(profile, baselineIi, candidateIi, factor, benefit, codeDelta, pressureDelta,
            remainder, workUnits, reason, digest);
    }

    private static IrLoopTransformCandidateV1 Candidate(
        IrLoopTransformKindV1 kind,
        int factor,
        IrLoopTransformCandidateStatusV1 status,
        int? ii,
        int codeDelta,
        int pressureDelta,
        int benefit,
        long workUnits,
        string reason)
    {
        string digest = HybridCpuLoopTransformContractV1.Hash(string.Join('|',
            "hybridcpu.loop-transform-candidate/v1", kind, factor, status,
            ii?.ToString(CultureInfo.InvariantCulture) ?? "none",
            codeDelta.ToString(CultureInfo.InvariantCulture),
            pressureDelta.ToString(CultureInfo.InvariantCulture),
            benefit.ToString(CultureInfo.InvariantCulture),
            workUnits.ToString(CultureInfo.InvariantCulture), reason));
        return new(kind, factor, status, ii, codeDelta, pressureDelta, benefit, workUnits, reason, digest);
    }

    private static IrLoopTransformResultV1 Fallback(
        IrLoopTransformKindV1 kind,
        IrLoopTransformStatusV1 status,
        IrCanonicalLoopV1 loop,
        HybridCpuLoopTransformOptionsV1 options,
        string code,
        string reason) => FallbackWithCandidates(
            kind, status, loop, options, Array.Empty<IrLoopTransformCandidateV1>(), code, reason);

    private static IrLoopTransformResultV1 FallbackWithCandidates(
        IrLoopTransformKindV1 kind,
        IrLoopTransformStatusV1 status,
        IrCanonicalLoopV1 loop,
        HybridCpuLoopTransformOptionsV1 options,
        IReadOnlyList<IrLoopTransformCandidateV1> candidates,
        string code,
        string reason)
    {
        string digest = ResultDigest(kind, status, loop.LoopId, "none", "none", "none",
            candidates, options.OptionsDigest);
        return new(
            kind,
            status,
            null,
            null,
            null,
            null,
            null,
            Array.Empty<string>(),
            IrLoopInvalidatedAnalysisKindV1.None,
            null,
            null,
            candidates,
            loop.LoopId,
            [new(code, reason)],
            digest);
    }

    private static string ResultDigest(
        IrLoopTransformKindV1 kind,
        IrLoopTransformStatusV1 status,
        string fallback,
        string output,
        string facts,
        string profitability,
        IReadOnlyList<IrLoopTransformCandidateV1> candidates,
        string options) => HybridCpuLoopTransformContractV1.Hash(string.Join('|',
            "hybridcpu.loop-transform-result/v1",
            kind,
            status,
            fallback,
            output,
            facts,
            profitability,
            string.Join(',', candidates.Select(static candidate => candidate.CandidateDigest)),
            options));

    private static string Versioned(string value, int iteration) =>
        $"{value}@iteration:{iteration.ToString(CultureInfo.InvariantCulture)}";

    private static string OperationKey(IrModuloExpandedOperationV1 operation) => string.Join(':',
        operation.InstanceId,
        operation.MaterializedInstructionIndex.ToString(CultureInfo.InvariantCulture),
        operation.OriginalInstructionIndex.ToString(CultureInfo.InvariantCulture),
        operation.SourceIteration.ToString(CultureInfo.InvariantCulture),
        operation.AbsoluteCycle.ToString(CultureInfo.InvariantCulture),
        operation.ModuloCycle.ToString(CultureInfo.InvariantCulture),
        operation.IssueSlot.ToString(CultureInfo.InvariantCulture),
        operation.Phase,
        string.Join(',', operation.ValueBindings.Select(static binding =>
            $"{binding.OriginalValueId}>{binding.VersionedValueId}>{binding.AccessKind}")));

    private sealed record InstructionDraft(
        int BlockId,
        IrInstruction Original,
        int Lane,
        int NewIndex);

    private sealed record CandidateState(
        int Factor,
        IrProgram Program,
        IrCanonicalLoopV1 Loop,
        IrLoopMiiReportV1 Mii,
        IrModuloScheduleWitnessV1 Witness,
        IrModuloWitnessValidationV1 Validation,
        IrLoopTransformCandidateV1 Candidate);

    private sealed record JamCandidateState(
        int Factor,
        IrProgram Program,
        IrCanonicalLoopV1 OuterLoop,
        IrLoopMiiReportV1 OuterMii,
        IrModuloScheduleWitnessV1 OuterWitness,
        IrModuloWitnessValidationV1 OuterValidation,
        IrCanonicalLoopV1 InnerLoop,
        IrLoopMiiReportV1 InnerMii,
        IrModuloScheduleWitnessV1 InnerWitness,
        IrModuloWitnessValidationV1 InnerValidation,
        IrLoopTransformCandidateV1 Candidate);
}
