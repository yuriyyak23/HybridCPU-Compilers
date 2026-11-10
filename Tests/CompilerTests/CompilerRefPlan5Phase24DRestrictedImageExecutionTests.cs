using System.Diagnostics;
using System.Text.Json;
using TraceLevel = HybridCPU_ISE.Core.TraceLevel;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.Compiler.NativeAot;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Core;
using YAKSys_Hybrid_CPU;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase24DRestrictedImageExecutionTests
{
    private static readonly string FixtureAssembly = typeof(RestrictedCilCSharpFixtures).Assembly.Location;
    private const string FixtureType = "HybridCPU_ISE.Tests.CompilerTests.RestrictedCilCSharpFixtures";

    [Fact]
    public void RestrictedCSharpAdd_ImageRunsThroughExistingDecodeExecuteAndRetirePath()
    {
        using ImageRun run = RunImage(nameof(RestrictedCilCSharpFixtures.Add));
        Assert.Equal(0, run.ExitCode);
        NativeAotRestrictedImageArtifactV1 manifest = Assert.IsType<NativeAotRestrictedImageArtifactV1>(run.Manifest);
        HybridCpuRestrictedImageV1 image = new HybridCpuRestrictedImageBuilderV1().Inspect(run.Package);
        Assert.Equal(HybridCpuStartupStatusV1.Success, image.Status);
        HybridCpuStartupRegisterStateV1 registers = Assert.IsType<HybridCpuStartupRegisterStateV1>(image.InitialRegisters);
        // The seeded link register is biased for the JALR immediate. It is not
        // the architectural PC at which execution returns to the caller.
        ulong returnPc = HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel;

        Processor.CPU_Core core = new Processor.CPU_Core(0,
            CpuCorePlatformContext.CreateFixed(Processor.MainMemory, ProcessorMode.Compiler));
        core.InitializePipeline();
        core.PrepareExecutionStart(image.EntryAddress);
        for (int register = 0; register < 32; register++)
            core.WriteCommittedArch(0, register, 0);
        core.WriteCommittedPc(0, image.EntryAddress);
        core.WriteCommittedArch(0, registers.StackPointerRegister, registers.StackPointer);
        core.WriteCommittedArch(0, registers.FramePointerRegister, registers.FramePointer);
        core.WriteCommittedArch(0, registers.ThreadPointerRegister, registers.ThreadPointer);
        core.WriteCommittedArch(0, registers.ReturnAddressRegister, registers.ReturnAddress);
        core.WriteCommittedArch(0, HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[0], 7);
        core.WriteCommittedArch(0, HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[1], 12);

        TraceSink trace = new TraceSink(TraceFormat.JSON, Path.Combine(Path.GetTempPath(), "phase24d-image-execution.trace.json"));
        trace.SetEnabled(true);
        trace.SetLevel(TraceLevel.Full);
        TraceSink? priorTrace = Processor.TraceSink;
        Processor.TraceSink = trace;

        try
        {
            int retired = 0;
            while (core.ReadCommittedPc(0) != returnPc && retired++ < 32)
            {
                ulong pc = core.ReadCommittedPc(0);
                Assert.True(pc >= image.ImageBase && pc < image.ImageBase + (ulong)image.ImageBytes.Length,
                    $"Image execution left its mapped range after {retired - 1} bundles: pc=0x{pc:x}, " +
                    $"return-pc=0x{returnPc:x}, ra=0x{core.ReadArch(0, registers.ReturnAddressRegister):x}.");
                int bundleIndex = checked((int)((pc - image.ImageBase) / HybridCpuBundleSerializer.BundleSizeBytes));
                Assert.InRange(bundleIndex, 0, image.ImageBytes.Length / HybridCpuBundleSerializer.BundleSizeBytes - 1);
                VLIW_Instruction[] bundle = ReadBundle(image.ImageBytes, bundleIndex);
                bool control = bundle.Any(static instruction => instruction.OpCode is
                    >= (uint)Processor.CPU_Core.InstructionsEnum.JAL and <= (uint)Processor.CPU_Core.InstructionsEnum.BGEU);
                RetireBundle(core, bundle, pc);
                // This seam retires supplied bundles; ordinary fallthrough is
                // advanced by the fixture, as in the later managed-image drivers.
                if (!control || core.ReadCommittedPc(0) == pc)
                    core.WriteCommittedPc(0, checked(pc + (ulong)HybridCpuBundleSerializer.BundleSizeBytes));
            }

            Assert.True(retired <= 32);
            Assert.Equal(returnPc, core.ReadCommittedPc(0));
            Assert.Equal(19UL, core.ReadArch(0, registers.ReturnValueRegister));
            Assert.Equal(manifest.EntryAddress, image.EntryAddress);
            Assert.Equal(manifest.PackageSha256, image.PackageSha256);
            Assert.NotEmpty(trace.GetThreadTrace(0));
            Assert.Contains(trace.GetThreadTrace(0), evt => evt.RegisterFile is not null && evt.RegisterFile.Length > 10);
        }
        finally
        {
            Processor.TraceSink = priorTrace;
        }
    }

    [Fact]
    public void CompileImage_IsByteAndManifestDeterministicAndCarriesAllLayerDigests()
    {
        using ImageRun first = RunImage(nameof(RestrictedCilCSharpFixtures.Add));
        using ImageRun second = RunImage(nameof(RestrictedCilCSharpFixtures.Add));

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(first.Package, second.Package);
        Assert.Equal(first.ManifestJson, second.ManifestJson);
        NativeAotRestrictedImageArtifactV1 manifest = Assert.IsType<NativeAotRestrictedImageArtifactV1>(first.Manifest);
        Assert.Equal("hybridcpu.nativeaot-restricted-image/v1", manifest.SchemaId);
        Assert.Equal("HybridCPU.DotNetAot.ScalarControlFlowV2", manifest.ProfileId);
        Assert.DoesNotContain("\"ManagedGraphDigest\"", first.ManifestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"CompiledMethodCount\"", first.ManifestJson, StringComparison.Ordinal);
        Assert.All(new[]
        {
            manifest.RegisterAllocationContractDigest,
            manifest.RegisterAllocationOptionsDigest,
            manifest.RegisterAllocationWitnessDigest,
            manifest.ObjectFormatContractDigest,
            manifest.ObjectSha256,
            manifest.StaticLinkOptionsDigest,
            manifest.LinkMapDigest,
            manifest.StartupOptionsDigest,
            manifest.PackageSha256
        }, static digest => Assert.Matches("^[0-9a-f]{64}$", digest));
    }

    [Fact]
    public void UnsupportedManagedProgram_FailsBeforeImageEmission()
    {
        using ImageRun run = RunImage(nameof(RestrictedCilCSharpFixtures.GenericIdentity));

        Assert.Equal(4, run.ExitCode);
        Assert.Contains("HCCIL1012", run.StandardError, StringComparison.Ordinal);
        Assert.Empty(run.Package);
        Assert.Null(run.Manifest);
    }

    [Fact]
    public void RestrictedImageManifestDoesNotAcquireRuntimeExecutionOrPublicationAuthority()
    {
        using ImageRun run = RunImage(nameof(RestrictedCilCSharpFixtures.Add));

        NativeAotRestrictedImageArtifactV1 manifest = Assert.IsType<NativeAotRestrictedImageArtifactV1>(run.Manifest);
        Assert.False(manifest.HasRuntimeAuthority);
        Assert.False(manifest.HasExecutionAuthority);
        Assert.False(manifest.HasPublicationAuthority);
        Assert.DoesNotContain("LLVM", manifest.SchemaId, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("host", run.ManifestJson, StringComparison.OrdinalIgnoreCase);
    }

    private static void RetireBundle(Processor.CPU_Core core, VLIW_Instruction[] bundle, ulong pc)
    {
        core.TestRunDecodeStageWithFetchedBundle(bundle, pc);
        core.TestRunExecuteStageFromCurrentDecodeState();
        core.TestRunMemoryAndWriteBackStagesFromCurrentExecuteState();
    }

    private static VLIW_Instruction[] ReadBundle(byte[] image, int bundleIndex)
    {
        VLIW_Bundle bundle = new VLIW_Bundle();
        Assert.True(bundle.TryReadBytes(image, checked(bundleIndex * HybridCpuBundleSerializer.BundleSizeBytes)));
        return Enumerable.Range(0, 8).Select(bundle.GetInstruction).ToArray();
    }

    private static ImageRun RunImage(string method)
    {
        string output = Path.Combine(Path.GetTempPath(), $"hybridcpu-24d-{Guid.NewGuid():N}.hce");
        ProcessStartInfo start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(NativeAotSeamArtifactV1).Assembly.Location);
        start.ArgumentList.Add("compile-image");
        AddPair(start, "--assembly", FixtureAssembly);
        AddPair(start, "--type", FixtureType);
        AddPair(start, "--method", method);
        AddPair(start, "--out", output);
        AddPair(start, "--source-commit", NativeAotSeamBaselineV1.SourceCommit);
        AddPair(start, "--patch-digest", NativeAotSeamBaselineV1.PatchDigest);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Adapter process did not start.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        byte[] package = File.Exists(output) ? File.ReadAllBytes(output) : Array.Empty<byte>();
        string json = File.Exists(output + ".json") ? File.ReadAllText(output + ".json") : string.Empty;
        NativeAotRestrictedImageArtifactV1? manifest = string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize<NativeAotRestrictedImageArtifactV1>(json);
        return new(process.ExitCode, stdout, stderr, output, package, json, manifest);
    }

    private static void AddPair(ProcessStartInfo start, string name, string value)
    {
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(value);
    }

    private sealed record ImageRun(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        string OutputPath,
        byte[] Package,
        string ManifestJson,
        NativeAotRestrictedImageArtifactV1? Manifest) : IDisposable
    {
        public void Dispose()
        {
            if (File.Exists(OutputPath)) File.Delete(OutputPath);
            if (File.Exists(OutputPath + ".json")) File.Delete(OutputPath + ".json");
        }
    }
}
