using System.Diagnostics;
using System.Text.Json;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU.Compiler.Core.IR.Telemetry.Contracts;
using HybridCPU.Compiler.Native;

internal static class MixedProfileBaseline
{
    public static void Run(string output)
    {
        HybridCpuInstructionWord Carrier(string source)
        {
            NativeCompilationResult admitted = new NativeAssemblyFrontend().Compile(new NativeFrontendRequest(0, source));
            if (!admitted.Succeeded) throw new Exception(source + ": " + string.Join(';', admitted.Diagnostics));
            uint opcode = (uint)admitted.Artifacts!.CanonicalIr.Instructions.Single().Opcode;
            return Enumerable.Range(0, 8).Select(admitted.Artifacts.CompiledProgram.LoweredBundles[0].GetInstruction).First(w => w.OpCode == opcode);
        }
        List<object> rows = new List<object>();
        foreach (string? shape in new[] { "alternating", "grouped", "load-use" })
        {
            HybridCpuInstructionWord[] words = Enumerable.Range(0, 16).Select(n =>
            {
                bool load = shape == "grouped" ? n < 8 : n % 2 == 0;
                return Carrier(load ? $"LW r{10 + n % 8}, r30, {n * 4}" :
                    $"ADD r{18 + n % 8}, r{(shape == "load-use" ? 10 + (n - 1) % 8 : 31)}, r0");
            }).ToArray();
            HybridCpuProfileCompilationResultV1 baseline = HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words, new());
            HybridCpuProfileCompilationRequestV1 offRequest = new HybridCpuProfileCompilationRequestV1 { CaptureDecisionTrace = true };
            HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words, offRequest);
            using Process baselineProcess = Process.GetCurrentProcess();
            TimeSpan baselineCpu = baselineProcess.TotalProcessorTime;
            long baselineAllocation = GC.GetAllocatedBytesForCurrentThread();
            Stopwatch baselineWatch = Stopwatch.StartNew();
            HybridCpuProfileCompilationResultV1 measuredOff = HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words, offRequest);
            baselineWatch.Stop();
            baselineAllocation = GC.GetAllocatedBytesForCurrentThread() - baselineAllocation;
            double baselineCpuMs = (baselineProcess.TotalProcessorTime - baselineCpu).TotalMilliseconds;
            if (!measuredOff.CompiledProgram.ProgramImage.SequenceEqual(baseline.CompiledProgram.ProgramImage))
                throw new Exception("Baseline tracing changed bytes.");
            if (baseline.CompiledProgram.ProgramSchedule.Program.Instructions.Select(i => i.Annotation.RequiredSlotClass).Distinct().Count() < 2)
                throw new Exception("Fixture has no slot mix.");
            foreach (IrSlotClass pressure in new[] { IrSlotClass.AluClass, IrSlotClass.LsuClass })
            foreach (HybridCpuProfileSelectionPolicyV1 selection in Enum.GetValues<HybridCpuProfileSelectionPolicyV1>())
            {
                    TypedSlotTelemetryProfile profile = new TypedSlotTelemetryProfile(baseline.ProfileInputIdentity,
                    new Dictionary<IrSlotClass, long> { [pressure] = 1 },
                    new Dictionary<IrSlotClass, long> { [pressure] = 9 },
                    new Dictionary<IrTypedSlotRejectReason, long>(), 0, 1, 1, 0, 0, 0, 0, 0,
                    new Dictionary<int, long>(), null);
                string json = JsonSerializer.Serialize(new { Schema = TelemetryProfileReader.ProfileSchemaV1, Profile = profile });
                    HybridCpuProfileCompilationRequestV1 request = new HybridCpuProfileCompilationRequestV1(true, json) { CaptureDecisionTrace = true, SelectionPolicy = selection };
                HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words, request); // exact-shape warmup
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                using Process process = Process.GetCurrentProcess();
                    TimeSpan cpu = process.TotalProcessorTime;
                    Stopwatch watch = Stopwatch.StartNew();
                    HybridCpuProfileCompilationResultV1 active = HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words, request);
                watch.Stop();
                long allocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
                double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                    HybridCpuProfileCompilationResultV1 repeat = HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words, request);
                    HybridCpuProfileCompilationResultV1 noTrace = HybridCpuCanonicalCompiler.CompileProgramWithProfile(0, words, request with { CaptureDecisionTrace = false });
                if (active.ProfileStatus != "profile-active" ||
                    !active.CompiledProgram.ProgramImage.SequenceEqual(repeat.CompiledProgram.ProgramImage) ||
                    !active.CompiledProgram.ProgramImage.SequenceEqual(noTrace.CompiledProgram.ProgramImage) ||
                    JsonSerializer.Serialize(active.DecisionTrace) != JsonSerializer.Serialize(repeat.DecisionTrace))
                    throw new Exception("Active mixed profile reproducibility drift.");
                    HybridCpuProfileDecisionTraceV1 trace = active.DecisionTrace!;
                foreach (HybridCpuProfileCandidateEventV1? e in trace.Events.Where(e => e.Kind == "selection"))
                    if (!trace.Events.Any(c => c.Decision == e.Decision && c.Kind == "candidate" && c.InstructionIndex == e.InstructionIndex && c.StructurallyAdmissible))
                        throw new Exception("Profile bypassed admission.");
                int Cycles(HybridCpuCompiledProgram p) => p.ProgramSchedule.BlockSchedules.Sum(b => b.ScheduleLength);
                    IrProgramBundlingQuality a = baseline.CompiledProgram.BundleLayout.Quality;
                    IrProgramBundlingQuality b = active.CompiledProgram.BundleLayout.Quality;
                rows.Add(new { Shape = shape, Pressure = pressure.ToString(), Selection = selection.ToString(),
                    Deterministic = new { baseline.ProfileInputIdentity, active.ActiveProfileDigest, active.EffectiveOptionsDigest,
                        BaselineSchedule = baseline.ScheduleFingerprint, ActiveSchedule = active.ScheduleFingerprint,
                        BaselineImageSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(baseline.CompiledProgram.ProgramImage)),
                        ActiveImageSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(active.CompiledProgram.ProgramImage)),
                        BaselineCycles = Cycles(baseline.CompiledProgram), ActiveCycles = Cycles(active.CompiledProgram),
                        BaselineBundles = a.BundleCount, ActiveBundles = b.BundleCount,
                        BaselineNops = a.NopSlotCount, ActiveNops = b.NopSlotCount,
                        BaselinePlacementSearch = a.PlacementSearchEvaluatedCount, ActivePlacementSearch = b.PlacementSearchEvaluatedCount,
                        PositiveCostEvents = trace.Events.Count(e => e.ClassPressureCostBucket > 0), Trace = trace },
                    WallMilliseconds = watch.Elapsed.TotalMilliseconds, CpuMilliseconds = cpuMs, AllocatedBytes = allocation,
                    BaselineWallMilliseconds = baselineWatch.Elapsed.TotalMilliseconds,
                    BaselineCpuMilliseconds = baselineCpuMs, BaselineAllocatedBytes = baselineAllocation,
                    WorkingSetBytes = process.WorkingSet64 });
            }
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { Schema = "hybridcpu.profile-mixed-baseline/v1", Qualification = "no", Rows = rows }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS mixed-slot profile baseline: 12 A/B cases; active repeat/trace-off bytes equal; Qualification: no");
    }
}
