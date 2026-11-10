using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;

// Tool-only measurement lane. It is not referenced by production compiler projects and
// its results are performance observations, never legality or publication evidence.
if (args is [string cancellationMode, string timeoutText] &&
    cancellationMode is "--scheduler-cancel-after" or "--fallback-cancel-after" or "--canonical-cancel-after")
{
    int timeout = int.Parse(timeoutText, System.Globalization.CultureInfo.InvariantCulture);
    if (timeout <= 0) throw new ArgumentOutOfRangeException(nameof(timeout));
    SchedulerBenchmarks fixture = new SchedulerBenchmarks { OperationCount = 2048 };
    fixture.Setup();
    using CancellationTokenSource cancellation = new CancellationTokenSource();
    cancellation.CancelAfter(timeout);
    Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        IrProgramSchedule schedule = cancellationMode switch {
            "--fallback-cancel-after" => fixture.ScheduleInProgramOrder(cancellation.Token),
            "--canonical-cancel-after" => fixture.CompileCanonical(cancellation.Token).ProgramSchedule,
            _ => fixture.Schedule(cancellation.Token)
        };
        Console.WriteLine($"Completed; blocks={schedule.BlockSchedules.Count}; elapsedMs={watch.Elapsed.TotalMilliseconds:F3}");
    }
    catch (OperationCanceledException exception) when (exception.CancellationToken == cancellation.Token)
    {
        Console.WriteLine($"Cancelled; no schedule returned; elapsedMs={watch.Elapsed.TotalMilliseconds:F3}");
    }
}
else if (args is ["--threshold-baseline", string output])
    ThresholdBaseline.Run(output);
else if (args is ["--profile-ingress-checks"])
    ProfileIngressChecks.Run();
else if (args is ["--profile-mixed-baseline", string profileOutput])
    MixedProfileBaseline.Run(profileOutput);
else if (args is ["--profile-candidate-catalog", string catalogOutput])
    ProfileCandidateCatalog.Run(catalogOutput);
else
    BenchmarkRunner.Run<SchedulerBenchmarks>();

[MemoryDiagnoser]
public class SchedulerBenchmarks
{
    private IrProgram _program = null!;

    [Params(64, 512, 2048)]
    public int OperationCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        PersistedAssemblyBuilder assembly = new PersistedAssemblyBuilder(new AssemblyName("SchedulerBenchmarkFixture"), typeof(object).Assembly);
        TypeBuilder type = assembly.DefineDynamicModule("Main").DefineType("Fixture", TypeAttributes.Public);
        ILGenerator il = type.DefineMethod("Calculate", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [typeof(int)]).GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        for (int index = 0; index < OperationCount; index++)
        {
            il.Emit(OpCodes.Ldc_I4, index | 1);
            il.Emit(index % 3 == 0 ? OpCodes.Xor : OpCodes.Add);
        }
        il.Emit(OpCodes.Ret);
        type.CreateType();
        using MemoryStream stream = new MemoryStream();
        assembly.Save(stream);
        RestrictedCilImportResultV1 imported = new RestrictedCilImporterV1(
            mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportImage(
                stream.ToArray(), new("Fixture", "Calculate"));
        _program = imported.Program ?? throw new InvalidOperationException(string.Join(';',
            imported.Diagnostics.Select(static diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
    }

    [Benchmark]
    public IrProgramSchedule Schedule() => new HybridCpuLocalListScheduler().ScheduleProgram(_program);

    public IrProgramSchedule Schedule(CancellationToken cancellationToken) =>
        new HybridCpuLocalListScheduler().ScheduleProgram(cancellationToken, _program);

    public IrProgramSchedule ScheduleInProgramOrder(CancellationToken cancellationToken) =>
        new HybridCpuProgramOrderLocalScheduler().ScheduleProgram(cancellationToken, _program);

    public HybridCpuCompiledProgram CompileCanonical(CancellationToken cancellationToken)
    {
        HybridCpuInstructionWord[] words = Enumerable.Repeat(new HybridCpuInstructionWord {
            OpCode = (uint)HybridCpuOpcode.ADDI, DataTypeValue = HybridCpuDataType.INT32,
            PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(10, 10, HybridCpuInstructionWord.NoArchReg),
            Src2Pointer = 1, VirtualThreadId = 0
        }, OperationCount).ToArray();
        return HybridCPU.Compiler.Core.HybridCpuCanonicalCompiler.CompileProgram(cancellationToken, 0, words);
    }
}
