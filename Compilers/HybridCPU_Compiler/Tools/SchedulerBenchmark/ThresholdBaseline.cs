using System.Diagnostics;
using System.Text.Json;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU.Compiler.Native;

internal static class ThresholdBaseline
{
    public static void Run(string output)
    {
        NativeCompilationResult admitted = new NativeAssemblyFrontend().Compile(new NativeFrontendRequest(0, "ADDI r10, r10, 1"));
        HybridCpuInstructionBundle bundle = admitted.Artifacts!.CompiledProgram.LoweredBundles[0];
        HybridCpuInstructionWord word = Enumerable.Range(0, 8).Select(bundle.GetInstruction).First(w => w.OpCode == (uint)HybridCpuOpcode.ADDI);
        List<object> rows = new List<object>();
        HybridCpuInstructionWord Carrier(string source)
        {
            NativeCompilationResult result = new NativeAssemblyFrontend().Compile(new NativeFrontendRequest(0, source));
            if (!result.Succeeded) throw new Exception(string.Join(';', result.Diagnostics));
            HybridCpuInstructionBundle value = result.Artifacts!.CompiledProgram.LoweredBundles[0];
            uint opcode = (uint)result.Artifacts.CanonicalIr.Instructions.Single().Opcode;
            return Enumerable.Range(0, 8).Select(value.GetInstruction).First(w => w.OpCode == opcode);
        }
        foreach ((string, int, bool) item in new[] { ("block48", 48, false), ("block49", 49, false), ("total192", 192, true), ("total193", 193, true),
                     ("independent", 16, false), ("shared-descendants", 24, false), ("alu-multiply-mix", 24, false) })
        {
            HybridCpuInstructionWord[] words = Enumerable.Repeat(word, item.Item2).ToArray();
            if (item.Item1 == "independent")
                words = Enumerable.Range(10, 16).Select(r => Carrier($"ADDI r{r}, r0, 1")).ToArray();
            if (item.Item1 == "shared-descendants")
                words = Enumerable.Range(0, 24).Select(n => Carrier(n < 8 ? $"ADDI r{10 + n}, r0, 1" :
                    $"ADD r{18 + n % 8}, r{10 + n % 8}, r{10 + (n + 1) % 8}")).ToArray();
            if (item.Item1 == "alu-multiply-mix")
                words = Enumerable.Range(0, 24).Select(n => Carrier($"{(n % 2 == 0 ? "ADD" : "MUL")} r{10 + n % 8}, r{18 + n % 8}, r{18 + (n + 1) % 8}")).ToArray();
            IrLabelDeclaration[] labels = item.Item3 ? Enumerable.Range(0, (item.Item2 + 39) / 40).Select(i => new IrLabelDeclaration("block" + i, i * 40)).ToArray() : [];
            List<string> events = new List<string>();
            // Warm the exact shape before measuring, without changing compiler policy.
            HybridCpuCanonicalCompiler.CompileProgram(0, words, labelDeclarations: labels);
            using Process process = Process.GetCurrentProcess();
            Dictionary<string, (long Timestamp, TimeSpan Cpu, long Allocated)> stageStarts = new Dictionary<string, (long Timestamp, TimeSpan Cpu, long Allocated)>();
            List<object> stages = new List<object>();
            void Observe(string stage, string message)
            {
                events.Add(stage);
                if (stage.EndsWith("Starting", StringComparison.Ordinal))
                    stageStarts[stage[..^8]] = (Stopwatch.GetTimestamp(), process.TotalProcessorTime, GC.GetAllocatedBytesForCurrentThread());
                else if (stageStarts.Remove(stage, out (long Timestamp, TimeSpan Cpu, long Allocated) start))
                    stages.Add(new { Stage = stage, WallMilliseconds = Stopwatch.GetElapsedTime(start.Timestamp).TotalMilliseconds,
                        CpuMilliseconds = (process.TotalProcessorTime - start.Cpu).TotalMilliseconds,
                        AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - start.Allocated,
                        WorkingSetBytes = process.WorkingSet64 });
            }
            TimeSpan cpu = process.TotalProcessorTime;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            Stopwatch watch = Stopwatch.StartNew();
            HybridCpuCompilationMetricsResultV1 measured = HybridCpuCanonicalCompiler.CompileProgramWithMetrics(0, words, new CompilerScheduleMetricsRequestV1(),
                labelDeclarations: labels, progressObserver: Observe);
            watch.Stop();
            double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            HybridCpuCompiledProgram plain = HybridCpuCanonicalCompiler.CompileProgram(0, words, labelDeclarations: labels);
            HybridCpuCompiledProgram repeat = HybridCpuCanonicalCompiler.CompileProgram(0, words, labelDeclarations: labels);
            if (!plain.ProgramImage.SequenceEqual(measured.CompiledProgram.ProgramImage) || !plain.ProgramImage.SequenceEqual(repeat.ProgramImage))
                throw new Exception("Metrics/repetition changed output");
            int maxBlock = measured.CompiledProgram.BundleLayout.Program.BasicBlocks.Max(b => b.Instructions.Count);
            bool fallback = events.Contains("ScheduleFallback");
            if (item.Item3 && maxBlock > 48) throw new Exception("Total threshold fixture failed to isolate block threshold");
            if (fallback != (item.Item2 == 49 || item.Item2 == 193)) throw new Exception("Threshold selection drift");
            rows.Add(new { Name = item.Item1, Instructions = item.Item2, MaxBlock = maxBlock, Fallback = fallback,
                WallMilliseconds = watch.Elapsed.TotalMilliseconds, CpuMilliseconds = cpuMs, AllocatedBytes = allocatedBytes,
                WorkingSetBytes = process.WorkingSet64, Stages = stages, measured.ScheduleMetrics, measured.ResourceTelemetry });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { Schema = "hybridcpu.threshold-baseline/v1", Qualification = "no", Rows = rows }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS threshold/DAG baseline7 cases; metrics-off/repeat equality; Qualification: no");
    }
}
