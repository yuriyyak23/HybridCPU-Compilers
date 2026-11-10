using System.Threading;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>Orders existing scheduling and bundling stages without selecting caller policy.</summary>
public static class HybridCpuProgramBundler
{
    /// <summary>
    /// Runs the caller's bounded allocation after initial bundling. Rejected allocation
    /// is returned unchanged for the caller's existing diagnostic; only witnessed
    /// allocation may replace the layout consumed by lowering/relocation.
    /// </summary>
    public static (IrProgramSchedule Schedule, IrProgramBundlingResult Bundles,
        IrRegisterAllocationResultV1? Allocation) CompileWithAllocation(
        IrProgram program,
        Func<IrProgram, IrProgramSchedule> scheduleProgram,
        HybridCpuBundleFormer bundler,
        Func<IrProgramSchedule, IrProgramBundlingResult, IrRegisterAllocationResultV1>? allocate)
        => CompileWithAllocation(CancellationToken.None, program, scheduleProgram, bundler, allocate);

    /// <summary>Checks cancellation around caller-owned allocation; the callback controls its internal token lifetime.</summary>
    public static (IrProgramSchedule Schedule, IrProgramBundlingResult Bundles,
        IrRegisterAllocationResultV1? Allocation) CompileWithAllocation(
        CancellationToken cancellationToken,
        IrProgram program,
        Func<IrProgram, IrProgramSchedule> scheduleProgram,
        HybridCpuBundleFormer bundler,
        Func<IrProgramSchedule, IrProgramBundlingResult, IrRegisterAllocationResultV1>? allocate)
    {
        (IrProgramSchedule? schedule, IrProgramBundlingResult? bundles) = Compile(cancellationToken, program, scheduleProgram, bundler);
        cancellationToken.ThrowIfCancellationRequested();
        IrRegisterAllocationResultV1? allocation = allocate?.Invoke(schedule, bundles);
        cancellationToken.ThrowIfCancellationRequested();
        if (allocation is { Status: IrRegisterAllocationStatusV1.Allocated, Witness: not null })
            bundles = allocation.FinalBundles;
        cancellationToken.ThrowIfCancellationRequested();
        return (schedule, bundles, allocation);
    }

    public static (IrProgramSchedule Schedule, IrProgramBundlingResult Bundles) Compile(
        IrProgram program,
        Func<IrProgram, IrProgramSchedule> scheduleProgram,
        HybridCpuBundleFormer bundler,
        Action<IrProgramSchedule>? scheduleCompleted = null,
        Action? bundlingStarting = null)
        => Compile(CancellationToken.None, program, scheduleProgram, bundler, scheduleCompleted, bundlingStarting);

    public static (IrProgramSchedule Schedule, IrProgramBundlingResult Bundles) Compile(
        CancellationToken cancellationToken,
        IrProgram program,
        Func<IrProgram, IrProgramSchedule> scheduleProgram,
        HybridCpuBundleFormer bundler,
        Action<IrProgramSchedule>? scheduleCompleted = null,
        Action? bundlingStarting = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IrProgramSchedule schedule = scheduleProgram(program);
        cancellationToken.ThrowIfCancellationRequested();
        scheduleCompleted?.Invoke(schedule);
        cancellationToken.ThrowIfCancellationRequested();
        bundlingStarting?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return (schedule, bundler.BundleProgram(cancellationToken, schedule));
    }
}
