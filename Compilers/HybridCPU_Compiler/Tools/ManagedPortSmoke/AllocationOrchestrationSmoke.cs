using System.Reflection;
using System.Reflection.Emit;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;

internal static class AllocationOrchestrationSmoke
{
    public static void Run()
    {
        PersistedAssemblyBuilder assembly = new PersistedAssemblyBuilder(new AssemblyName("AllocationOrchestration"), typeof(object).Assembly);
        TypeBuilder type = assembly.DefineDynamicModule("fixture").DefineType("Fixture", TypeAttributes.Public);
        MethodBuilder method = type.DefineMethod("Read", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
        ILGenerator il = method.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Neg); il.Emit(OpCodes.Ret);
        type.CreateType(); using MemoryStream stream = new MemoryStream(); assembly.Save(stream);
        RestrictedCilImportResultV1 imported = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportImage(stream.ToArray(), new("Fixture", "Read"));
        IrProgram program = imported.Program ?? throw new Exception(string.Join(';', imported.Diagnostics));
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        HybridCpuBundleFormer former = new HybridCpuBundleFormer();
        HybridCpuMiiResourceModelV1 resource = HybridCpuMiiResourceModelV1.Create(new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8);
        IrRegisterAllocationResultV1 Allocate(IrProgramSchedule s, IrProgramBundlingResult b) =>
            new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(s, b, resourceModel: resource,
                options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        (IrProgramSchedule Schedule, IrProgramBundlingResult Bundles, IrRegisterAllocationResultV1? Allocation) legacy = HybridCpuProgramBundler.CompileWithAllocation(program, _ => schedule, former, Allocate);
        (IrProgramSchedule Schedule, IrProgramBundlingResult Bundles, IrRegisterAllocationResultV1? Allocation) current = HybridCpuProgramBundler.CompileWithAllocation(CancellationToken.None, program, _ => schedule, former, Allocate);
        if (legacy.Allocation?.Witness is null || current.Allocation?.Witness?.WitnessDigest != legacy.Allocation.Witness.WitnessDigest ||
            !ReferenceEquals(current.Bundles, current.Allocation.FinalBundles) ||
            !HybridCpuIrProgramCompiler.CompileAllocated(legacy.Allocation).ProgramImage.SequenceEqual(
                HybridCpuIrProgramCompiler.CompileAllocated(current.Allocation).ProgramImage))
            throw new Exception("Allocated None witness/final bundles/image drift.");
        using CancellationTokenSource cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Cancel(() => HybridCpuProgramBundler.CompileWithAllocation(cancelled.Token, program,
            _ => throw new Exception("Pre-cancel schedule invoked"), former, Allocate), cancelled.Token);
        using CancellationTokenSource scheduled = new CancellationTokenSource();
        Cancel(() => HybridCpuProgramBundler.CompileWithAllocation(scheduled.Token, program,
            _ => { scheduled.Cancel(); return schedule; }, former,
            (_, _) => throw new Exception("Allocation invoked after cancelled schedule")), scheduled.Token);
        using CancellationTokenSource allocated = new CancellationTokenSource();
        bool reached = false;
        Cancel(() => HybridCpuProgramBundler.CompileWithAllocation(allocated.Token, program, _ => schedule, former,
            (s, b) => { reached = true; IrRegisterAllocationResultV1 result = Allocate(s, b); allocated.Cancel(); return result; }), allocated.Token);
        if (!reached) throw new Exception("Allocation cancellation callback not reached.");
        (IrProgramSchedule Schedule, IrProgramBundlingResult Bundles, IrRegisterAllocationResultV1? Allocation) absent = HybridCpuProgramBundler.CompileWithAllocation(CancellationToken.None, program, _ => schedule, former, null);
        if (absent.Allocation is not null || !ReferenceEquals(absent.Bundles.ProgramSchedule, schedule))
            throw new Exception("Absent allocator semantics changed.");
        (IrProgramSchedule Schedule, IrProgramBundlingResult Bundles, IrRegisterAllocationResultV1? Allocation) unwitnessed = HybridCpuProgramBundler.CompileWithAllocation(CancellationToken.None, program, _ => schedule, former,
            (_, _) => legacy.Allocation with { Witness = null });
        if (ReferenceEquals(unwitnessed.Bundles, legacy.Allocation.FinalBundles) || unwitnessed.Allocation?.Witness is not null)
            throw new Exception("Unwitnessed allocation replaced original layout.");
        (IrProgramSchedule Schedule, IrProgramBundlingResult Bundles, IrRegisterAllocationResultV1? Allocation) rejected = HybridCpuProgramBundler.CompileWithAllocation(CancellationToken.None, program, _ => schedule, former,
            (s, b) => new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(s, b, resourceModel: resource,
                options: HybridCpuRegisterAllocationOptionsV1.Create(true, true,
                    HybridCpuRegisterAllocationOptionsV1.Qualification.Budgets with { MaximumInstructions = 1 })));
        if (rejected.Allocation?.Status != IrRegisterAllocationStatusV1.BudgetExhausted ||
            !ReferenceEquals(rejected.Bundles, rejected.Allocation.OriginalBundles))
            throw new Exception("Rejected allocation diagnostic/layout changed.");
        (IrProgramSchedule Schedule, IrProgramBundlingResult Bundles, IrRegisterAllocationResultV1? Allocation) reused = HybridCpuProgramBundler.CompileWithAllocation(program, _ => schedule, former, Allocate);
        if (reused.Allocation?.Witness?.WitnessDigest != legacy.Allocation.Witness.WitnessDigest)
            throw new Exception("Allocation callback cancellation changed reuse.");
        Console.WriteLine("PASS allocation orchestration None witness/image, pre/schedule/callback cancellation, absent/unwitnessed/budget rejection and reuse");
    }

    private static void Cancel(Action action, CancellationToken token)
    {
        try { action(); throw new Exception("Partial allocated result after cancellation."); }
        catch (OperationCanceledException e) when (e.CancellationToken == token) { }
    }
}
