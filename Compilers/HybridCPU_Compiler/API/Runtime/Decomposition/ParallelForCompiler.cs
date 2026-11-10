using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR.Telemetry;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Result of compiling a parallel region through the full backend pipeline.
/// </summary>
/// <param name="Region">The parallel region that was decomposed.</param>
/// <param name="Plan">The chunk plan used for decomposition.</param>
/// <param name="WorkerPrograms">IR programs generated for each worker.</param>
/// <param name="CoordinatorProgram">IR program generated for the coordinator.</param>
/// <param name="WorkerAgreements">Admissibility agreements for each worker program.</param>
/// <param name="CoordinatorAgreement">Admissibility agreement for the coordinator program.</param>
public sealed record ParallelCompilationResult(
    ParallelRegionInfo Region,
    ChunkPlan Plan,
    IReadOnlyList<IrProgram> WorkerPrograms,
    IrProgram CoordinatorProgram,
    IReadOnlyList<IrAdmissibilityAgreement> WorkerAgreements,
    IrAdmissibilityAgreement CoordinatorAgreement)
{
    /// <summary>
    /// Gets a value indicating whether all worker bundles are structurally admissible.
    /// </summary>
    public bool AllWorkersAdmissible
    {
        get
        {
            foreach (IrAdmissibilityAgreement agreement in WorkerAgreements)
            {
                if (!agreement.AllBundlesAdmissible)
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Gets a value indicating whether all programs in the decomposition are structurally admissible.
    /// This includes both worker programs and the coordinator program.
    /// </summary>
    public bool AllProgramsAdmissible =>
        AllWorkersAdmissible
        && CoordinatorAgreement.AllBundlesAdmissible;

    /// <summary>
    /// Gets a value indicating whether all worker typed-slot facts are valid.
    /// Coordinator validity is intentionally excluded from this worker-only aggregate.
    /// </summary>
    public bool AllWorkerTypedSlotFactsValid
    {
        get
        {
            foreach (IrAdmissibilityAgreement agreement in WorkerAgreements)
            {
                if (!agreement.AllTypedSlotFactsValid)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Gets a value indicating whether all typed-slot facts are valid across the full
    /// parallel compilation result, including both workers and coordinator.
    /// </summary>
    public bool AllTypedSlotFactsValid
    {
        get
        {
            return AllWorkerTypedSlotFactsValid
                && CoordinatorAgreement.AllTypedSlotFactsValid;
        }
    }

    /// <summary>
    /// Gets the aggregate admissibility agreement across workers and coordinator.
    /// This is the CI-facing agreement summary surface for parallel decomposition.
    /// </summary>
    public IrAdmissibilityAgreement OverallAgreement =>
        new(WorkerAgreements
            .SelectMany(agreement => agreement.BundleResults)
            .Concat(CoordinatorAgreement.BundleResults)
            .ToList());
}

/// <summary>
/// Orchestrates the full parallel-for compilation pipeline:
/// detection → partition → worker synthesis → coordinator synthesis → backend pipeline validation.
/// </summary>
public sealed class ParallelForCompiler
{
    private readonly PartitionPlanner _planner = new();
    private readonly WorkerFunctionSynthesizer _workerSynthesizer = new();
    private readonly CoordinatorFunctionSynthesizer _coordinatorSynthesizer = new();

    /// <summary>
    /// Enables scheduler telemetry guidance for worker and coordinator programs.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseProfileGuidedScheduling { get; set; }

    /// <summary>
    /// Enables Phase 02 bounded joint cycle composition in instantiated schedulers.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseJointCycleComposition { get; set; }

    /// <summary>
    /// Versioned deterministic Phase 02 search limits.
    /// </summary>
    public HybridCpuCycleSearchOptionsV1 JointCycleSearchOptions { get; set; } =
        HybridCpuCycleSearchOptionsV1.Default;

    /// <summary>
    /// Telemetry profile reader reused by worker and coordinator scheduling.
    /// </summary>
    public TelemetryProfileReader? ProfileReader { get; set; }

    /// <summary>
    /// Enables the Wave 1 post-FSP scoring model in the instantiated schedulers.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UsePostFspScoring { get; set; }

    /// <summary>
    /// Enables Wave 1 slack reservation in the instantiated schedulers.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseConditionalSlackReservation { get; set; }

    /// <summary>
    /// Enables bounded class-pressure smoothing tie-breaks in the instantiated schedulers.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseClassPressureSmoothingTieBreaks { get; set; }

    /// <summary>
    /// Enables pinned-lane choke avoidance in the instantiated schedulers.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UsePinnedLaneChokeAvoidance { get; set; }

    /// <summary>
    /// Enables flexibility-aware ordering in the instantiated schedulers.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseFlexibilityAwareOrdering { get; set; }

    /// <summary>
    /// Enables conservative reduction-aware shaping for parallel-for workers and coordinators.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseReductionAwareShaping { get; set; }

    /// <summary>
    /// Enables loop-phase telemetry consumption in instantiated schedulers.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseLoopPhaseTelemetry { get; set; }

    /// <summary>
    /// Enables phase-stable loop shaping in instantiated schedulers.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UsePhaseStableLoopShaping { get; set; }

    /// <summary>
    /// Enables hot-loop telemetry-guided selection in instantiated schedulers.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseHotLoopTelemetryGuidance { get; set; }

    /// <summary>
    /// Enables local loop body normalization in instantiated schedulers.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseLoopBodyNormalization { get; set; }

    /// <summary>
    /// Enables advisory load clustering avoidance in instantiated schedulers.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseLoadClusteringAvoidance { get; set; }

    /// <summary>
    /// Enables bounded worker-path VT-aware backend pressure tie-breaks in instantiated schedulers.
    /// Coordinator-special paths remain excluded.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseVtAwareBackendPressureTieBreaks { get; set; }

    /// <summary>
    /// Enables bounded certificate-aware Stage 6 placement tie-breaks in instantiated bundle formers.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseCertificateAwareCoalescingTieBreaks { get; set; }

    /// <summary>
    /// Compiles a parallel-for region from explicit parameters.
    /// Workers pass through the full backend pipeline: schedule → bundle → admit → emit facts.
    /// </summary>
    /// <param name="iterationStart">Loop start value.</param>
    /// <param name="iterationEnd">Loop end value (exclusive).</param>
    /// <param name="iterationStep">Loop step.</param>
    /// <param name="bodyOpcodes">Opcodes forming the loop body.</param>
    /// <param name="inductionReg">Register used as induction variable.</param>
    /// <param name="reduction">Optional reduction plan.</param>
    /// <returns>Compilation result, or <c>null</c> if decomposition is not possible.</returns>
    public ParallelCompilationResult? CompileParallelFor(
        long iterationStart,
        long iterationEnd,
        long iterationStep,
        IReadOnlyList<HybridCpuOpcode> bodyOpcodes,
        int inductionReg,
        ReductionPlan? reduction)
    {
        ArgumentNullException.ThrowIfNull(bodyOpcodes);

        IrParallelKind kind = reduction is not null
            ? IrParallelKind.ForLoopWithReduction
            : IrParallelKind.ForLoop;

        ParallelRegionInfo region = new ParallelRegionInfo(
            StartInstructionIndex: 0,
            EndInstructionIndex: 0,
            Kind: kind,
            InductionVariableRegister: inductionReg,
            IterationStart: iterationStart,
            IterationEnd: iterationEnd,
            IterationStep: iterationStep,
            SharedReadRegisters: [],
            SharedWriteRegisters: [],
            PrivateRegisters: [],
            Reduction: reduction);

        // Plan partition
        ChunkPlan? plan = _planner.PlanPartition(region);
        if (plan is null)
        {
            return null;
        }

        // Synthesize workers
        List<IrProgram> workerPrograms = new List<IrProgram>(plan.WorkerCount);
        for (int i = 0; i < plan.WorkerCount; i++)
        {
            (long start, long end) = PartitionPlanner.GetWorkerRange(plan, i, region);
            IrProgram workerProgram = _workerSynthesizer.SynthesizeWorker(
                region,
                (byte)plan.WorkerVtIds[i],
                start, end,
                bodyOpcodes);
            workerPrograms.Add(workerProgram);
        }

        // Synthesize coordinator
        IrProgram coordinatorProgram = _coordinatorSynthesizer.SynthesizeCoordinator(
            region, plan);

        // Run all workers through the full backend pipeline
        HybridCpuLocalListScheduler workerScheduler = CreateScheduler(reduction is not null, treatAsReductionCoordinator: false, treatAsCoordinatorPath: false);
        HybridCpuLocalListScheduler coordinatorScheduler = CreateScheduler(reduction is not null, treatAsReductionCoordinator: reduction is not null, treatAsCoordinatorPath: true);
        HybridCpuBundleBuilder bundleBuilder = new HybridCpuBundleBuilder();

        List<IrAdmissibilityAgreement> workerAgreements = new List<IrAdmissibilityAgreement>(plan.WorkerCount);
        for (int workerIndex = 0; workerIndex < workerPrograms.Count; workerIndex++)
        {
            IrProgram workerProgram = workerPrograms[workerIndex];
            IrProgramSchedule schedule = workerScheduler.ScheduleProgram(workerProgram);
            HybridCpuBundleFormer workerBundleFormer = CreateBundleFormer(plan.WorkerVtIds[workerIndex], treatAsCoordinatorPath: false);
            IrProgramBundlingResult bundlingResult = workerBundleFormer.BundleProgram(schedule);
            IrAdmissibilityAgreement agreement = bundleBuilder.BuildAgreement(bundlingResult);
            workerAgreements.Add(agreement);
        }

        // Run coordinator through the pipeline
        IrProgramSchedule coordSchedule = coordinatorScheduler.ScheduleProgram(coordinatorProgram);
        HybridCpuBundleFormer coordinatorBundleFormer = CreateBundleFormer(plan.CoordinatorVtId, treatAsCoordinatorPath: true);
        IrProgramBundlingResult coordBundling = coordinatorBundleFormer.BundleProgram(coordSchedule);
        IrAdmissibilityAgreement coordAgreement = bundleBuilder.BuildAgreement(coordBundling);

        return new ParallelCompilationResult(
            Region: region,
            Plan: plan,
            WorkerPrograms: workerPrograms,
            CoordinatorProgram: coordinatorProgram,
            WorkerAgreements: workerAgreements,
            CoordinatorAgreement: coordAgreement);
    }

    private HybridCpuLocalListScheduler CreateScheduler(bool hasReduction, bool treatAsReductionCoordinator, bool treatAsCoordinatorPath)
    {
        return new HybridCpuLocalListScheduler
        {
            UseJointCycleComposition = UseJointCycleComposition,
            JointCycleSearchOptions = JointCycleSearchOptions,
            UseProfileGuidedScheduling = UseProfileGuidedScheduling,
            ProfileReader = ProfileReader,
            UsePostFspScoring = UsePostFspScoring,
            UseConditionalSlackReservation = UseConditionalSlackReservation,
            UseClassPressureSmoothingTieBreaks = UseClassPressureSmoothingTieBreaks,
            UsePinnedLaneChokeAvoidance = UsePinnedLaneChokeAvoidance,
            UseFlexibilityAwareOrdering = UseFlexibilityAwareOrdering,
            UseReductionAwareShaping = UseReductionAwareShaping && hasReduction,
            TreatAsReductionCoordinator = UseReductionAwareShaping && hasReduction && treatAsReductionCoordinator,
            UseLoopPhaseTelemetry = UseLoopPhaseTelemetry,
            UsePhaseStableLoopShaping = UsePhaseStableLoopShaping,
            UseHotLoopTelemetryGuidance = UseHotLoopTelemetryGuidance,
            UseLoopBodyNormalization = UseLoopBodyNormalization,
            UseLoadClusteringAvoidance = UseLoadClusteringAvoidance,
            UseVtAwareBackendPressureTieBreaks = UseVtAwareBackendPressureTieBreaks,
            TreatAsCoordinatorPath = treatAsCoordinatorPath
        };
    }

    private HybridCpuBundleFormer CreateBundleFormer(int virtualThreadId, bool treatAsCoordinatorPath)
    {
        return new HybridCpuBundleFormer(useClassFirstBinding: true)
        {
            ProfileReader = ProfileReader,
            UseCertificateAwareCoalescingTieBreaks = UseCertificateAwareCoalescingTieBreaks,
            VirtualThreadId = virtualThreadId,
            TreatAsCoordinatorPath = treatAsCoordinatorPath
        };
    }
}
