using System.Threading;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>Composes existing compiler stages over frontend-admitted IR.</summary>
public static class HybridCpuIrProgramCompiler
{
    public static HybridCpuCompiledProgram Compile(
        IrProgram program,
        Func<IrProgram, IrProgramSchedule> scheduleProgram,
        HybridCpuBundleFormer bundler,
        Action<string, string>? progressObserver = null)
        => Compile(CancellationToken.None, program, scheduleProgram, bundler, progressObserver);

    public static HybridCpuCompiledProgram Compile(
        CancellationToken cancellationToken,
        IrProgram program,
        Func<IrProgram, IrProgramSchedule> scheduleProgram,
        HybridCpuBundleFormer bundler,
        Action<string, string>? progressObserver = null)
    {
        (IrProgramSchedule? programSchedule, IrProgramBundlingResult? bundleLayout) = HybridCpuProgramBundler.Compile(cancellationToken, program, scheduleProgram, bundler,
            scheduleCompleted: schedule => progressObserver?.Invoke("Schedule", $"Schedule contains {schedule.BlockSchedules.Count} block schedule(s) and {CountCycleGroups(schedule)} cycle group(s)."),
            bundlingStarting: () => progressObserver?.Invoke("BundleMaterializationStarting", "Materializing legal bundle placements."));
        progressObserver?.Invoke("BundleMaterialization", $"Bundle layout contains {CountBundles(bundleLayout)} bundle(s).");
        cancellationToken.ThrowIfCancellationRequested();

        HybridCpuCompiledProgram compiled = Complete(cancellationToken, programSchedule, bundleLayout, progressObserver);
        cancellationToken.ThrowIfCancellationRequested();
        return compiled;
    }

    /// <summary>Completes only witnessed allocation using its final schedule and placement.</summary>
    public static HybridCpuCompiledProgram CompileAllocated(IrRegisterAllocationResultV1 allocation)
        => CompileAllocated(CancellationToken.None, allocation);

    public static HybridCpuCompiledProgram CompileAllocated(
        CancellationToken cancellationToken, IrRegisterAllocationResultV1 allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        if (allocation.Status != IrRegisterAllocationStatusV1.Allocated || allocation.Witness is null)
            throw new InvalidOperationException("Compiled-program completion requires witnessed register allocation.");
        cancellationToken.ThrowIfCancellationRequested();
        return Complete(cancellationToken, allocation.FinalSchedule, allocation.FinalBundles, progressObserver: null);
    }

    private static HybridCpuCompiledProgram Complete(
        CancellationToken cancellationToken, IrProgramSchedule programSchedule, IrProgramBundlingResult bundleLayout,
        Action<string, string>? progressObserver)
    {

        IrAdmissibilityAgreement agreement = new HybridCpuBundleBuilder().BuildAgreement(cancellationToken, bundleLayout);
        progressObserver?.Invoke("AgreementSummary", HybridCpuAgreementSummaryV1.From(agreement).FormatDiagnostic());

        (IReadOnlyList<HybridCpuInstructionBundle>? loweredBundles, IReadOnlyList<IrBundleAnnotations>? loweredBundleAnnotations, byte[]? programImage) = HybridCpuPostBundleCompiler.Compile(
            cancellationToken, bundleLayout, resolveControlFlowTargets: true, emitAnnotations: true, progressObserver: progressObserver);

        return new HybridCpuCompiledProgram(
            programSchedule,
            bundleLayout,
            loweredBundles,
            programImage,
            HybridCpuCompilerContract.Version,
            admissibilityAgreement: agreement,
            loweredBundleAnnotations: loweredBundleAnnotations);
    }

    private static int CountCycleGroups(IrProgramSchedule programSchedule)
    {
        int totalCycleGroups = 0;
        for (int blockIndex = 0; blockIndex < programSchedule.BlockSchedules.Count; blockIndex++)
        {
            totalCycleGroups += programSchedule.BlockSchedules[blockIndex].CycleGroups.Count;
        }

        return totalCycleGroups;
    }

    private static int CountBundles(IrProgramBundlingResult bundleLayout)
    {
        int totalBundles = 0;
        for (int blockIndex = 0; blockIndex < bundleLayout.BlockResults.Count; blockIndex++)
        {
            totalBundles += bundleLayout.BlockResults[blockIndex].Bundles.Count;
        }

        return totalBundles;
    }

}
