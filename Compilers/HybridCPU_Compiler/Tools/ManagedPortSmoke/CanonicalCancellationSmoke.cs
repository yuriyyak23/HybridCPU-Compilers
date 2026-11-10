using System.Security.Cryptography;
using System.Text.Json;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;

internal static class CanonicalCancellationSmoke
{
    internal static HybridCpuInstructionWord[] Words(int count) => Enumerable.Repeat(new HybridCpuInstructionWord {
        OpCode = (uint)HybridCpuOpcode.ADDI, DataTypeValue = HybridCpuDataType.INT32,
        PredicateMask = byte.MaxValue, Word1 = HybridCpuInstructionWord.PackArchRegs(10, 10, HybridCpuInstructionWord.NoArchReg),
        Src2Pointer = 1, VirtualThreadId = 0
    }, count).ToArray();

    private static string Projection(HybridCpuCompiledProgram program) =>
        Convert.ToHexString(SHA256.HashData(program.ProgramImage)) + ":" +
        JsonSerializer.Serialize(program.LoweredBundleAnnotations) + ":" +
        JsonSerializer.Serialize(HybridCpuAgreementSummaryV1.From(program.AdmissibilityAgreement));

    public static void Run()
    {
        int checks = 0;
        foreach (int size in new[] { 16, 48, 49, 192, 193 })
        {
            HybridCpuInstructionWord[] words = Words(size);
            HybridCpuCompiledProgram baseline = HybridCpuCanonicalCompiler.CompileProgram(0, words);
            List<string> legacyEvents = new List<string>();
            HybridCpuCanonicalCompiler.CompileProgram(0, words, progressObserver: (stage, _) => legacyEvents.Add(stage));
            List<string> noneEvents = new List<string>();
            HybridCpuCompiledProgram none = HybridCpuCanonicalCompiler.CompileProgram(CancellationToken.None, 0, words,
                progressObserver: (stage, _) => noneEvents.Add(stage));
            if (Projection(baseline) != Projection(none) || !legacyEvents.SequenceEqual(noneEvents))
                throw new Exception("Canonical None artifact/observer drift");
            using CancellationTokenSource active = new CancellationTokenSource();
            if (Projection(baseline) != Projection(HybridCpuCanonicalCompiler.CompileProgram(active.Token, 0, words)))
                throw new Exception("Uncancelled token artifact drift");
            if (legacyEvents.Contains("ScheduleFallback") != (size > 48)) throw new Exception("Scheduling mode drift");
            Console.WriteLine($"canonical size={size} imageSHA256={Convert.ToHexString(SHA256.HashData(baseline.ProgramImage))} projectionSHA256={Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Projection(baseline))))}");
            checks += 3;
            using CancellationTokenSource pre = new CancellationTokenSource(); pre.Cancel();
            bool returned = false;
            try { HybridCpuCanonicalCompiler.CompileProgram(pre.Token, 0, words,
                progressObserver: (_, _) => throw new Exception("Precancel reached observer")); returned = true; }
            catch (OperationCanceledException e) when (e.CancellationToken == pre.Token) { }
            if (returned) throw new Exception("Precancel returned artifact");
            checks++;
            foreach (string stop in legacyEvents.Distinct())
            {
                using CancellationTokenSource cancellation = new CancellationTokenSource(); bool reached = false; returned = false;
                try {
                    HybridCpuCanonicalCompiler.CompileProgram(cancellation.Token, 0, words, progressObserver: (stage, _) => {
                        if (stage == stop) { reached = true; cancellation.Cancel(); }
                    });
                    returned = true;
                }
                catch (OperationCanceledException e) when (e.CancellationToken == cancellation.Token) { }
                if (!reached || returned) throw new Exception("Stage cancellation returned artifact: " + stop);
                if (Projection(baseline) != Projection(HybridCpuCanonicalCompiler.CompileProgram(0, words)))
                    throw new Exception("Reuse after stage cancellation drift: " + stop);
                checks++;
            }
            try { HybridCpuCanonicalCompiler.CompileProgram(pre.Token, 0, words, frontendMode: (NativeFrontendMode)255);
                throw new Exception("Invalid frontend admitted"); }
            catch (ArgumentOutOfRangeException e) when (e.ParamName == "frontendMode") { }
            checks++;
        }
        Console.WriteLine($"PASS canonical cancellation {checks} controls: local/fallback artifacts, stage cancellation, no partial return, reuse and precedence; Qualification: no");
    }
}
