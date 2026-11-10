using HybridCPU_ISE.CloseToHSL.Core.Runtime.Managed;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.Compiler.NativeAot;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests.CompilerTests;

[Collection("RefPlan6 ISE Execution")]
public sealed class CompilerRefPlan6Phase05NativeAotPublishTests
{
    private static readonly string FixtureAssembly = typeof(ManagedCallGraphFixtures).Assembly.Location;
    private static readonly string FixtureType = typeof(ManagedCallGraphFixtures).FullName!;

    [Fact]
    public void AdapterPresentedWorld_EmitsAllReachableMethodsAndStableProvenance()
    {
        string[] methods = [
            nameof(ManagedCallGraphFixtures.ParameterlessRoot),
            nameof(ManagedCallGraphFixtures.LoopHelper),
            nameof(ManagedCallGraphFixtures.Left),
            nameof(ManagedCallGraphFixtures.Right),
            nameof(ManagedCallGraphFixtures.Shared)];

        using ImageRun first = Run(nameof(ManagedCallGraphFixtures.ParameterlessRoot), methods);
        using ImageRun second = Run(nameof(ManagedCallGraphFixtures.ParameterlessRoot), methods.Reverse());

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(first.Package, second.Package);
        Assert.Equal(first.ManifestJson, second.ManifestJson);
        NativeAotRestrictedImageArtifactV1 manifest = Assert.IsType<NativeAotRestrictedImageArtifactV1>(first.Manifest);
        Assert.Equal("hybridcpu.nativeaot-restricted-image/v2", manifest.SchemaId);
        Assert.Equal(ScalarControlFlowV2ProfileContractV1.ProfileId, manifest.ProfileId);
        Assert.Equal(5, manifest.CompiledMethodCount);
        Assert.Matches("^[0-9a-f]{64}$", manifest.BodyPresentationDigest);
        Assert.Matches("^[0-9a-f]{64}$", manifest.ManagedGraphDigest);
        Assert.Matches("^[0-9a-f]{64}$", manifest.OrderedObjectDigest);
        Assert.Matches("^[0-9a-f]{64}$", manifest.BackendProvenanceDigest);
        Assert.False(manifest.HasRuntimeAuthority);
        Assert.False(manifest.HasExecutionAuthority);
        Assert.False(manifest.HasPublicationAuthority);
    }

    [Fact]
    public void MissingPresentedCallee_FailsClosedBeforeBackend()
    {
        using ImageRun run = Run(nameof(ManagedCallGraphFixtures.ParameterlessRoot),
            [nameof(ManagedCallGraphFixtures.ParameterlessRoot)]);

        Assert.Equal(13, run.ExitCode);
        Assert.Contains("HCSCF-BODYWORLD1003", run.StandardError, StringComparison.Ordinal);
        Assert.Empty(run.Package);
        Assert.Null(run.Manifest);
    }

    [Fact]
    public void AdapterPresentedImage_ExecutesNestedCallsSharedCalleeAndLoopOnIse()
    {
        string[] methods = [
            nameof(ManagedCallGraphFixtures.ParameterlessRoot),
            nameof(ManagedCallGraphFixtures.LoopHelper),
            nameof(ManagedCallGraphFixtures.Left),
            nameof(ManagedCallGraphFixtures.Right),
            nameof(ManagedCallGraphFixtures.Shared)];
        using ImageRun run = Run(nameof(ManagedCallGraphFixtures.ParameterlessRoot), methods);
        Assert.Equal(0, run.ExitCode);
        HybridCpuRestrictedImageV1 image = new HybridCpuRestrictedImageBuilderV1().Inspect(run.Package);
        Assert.Equal(HybridCpuStartupStatusV1.Success, image.Status);
        HybridCpuStartupRegisterStateV1 registers = Assert.IsType<HybridCpuStartupRegisterStateV1>(image.InitialRegisters);
        Processor.MainMemoryArea originalMemory = Processor.MainMemory;
        ProcessorMode originalMode = Processor.CurrentProcessorMode;
        MemorySubsystem? originalMemorySubsystem = Processor.Memory;
        try
        {
            Processor.CurrentProcessorMode = ProcessorMode.Compiler;
            Processor.Memory = null;
            Processor.MainMemory = new HybridCpuIseSparseMainMemoryAreaV1();
            Processor.CPU_Core core = new Processor.CPU_Core(0,
                CpuCorePlatformContext.CreateFixed(Processor.MainMemory, ProcessorMode.Compiler));
            core.InitializePipeline();
            core.PrepareExecutionStart(image.EntryAddress);
            for (int register = 0; register < 32; register++) core.WriteCommittedArch(0, register, 0);
            core.WriteCommittedPc(0, image.EntryAddress);
            core.WriteCommittedArch(0, registers.StackPointerRegister, registers.StackPointer);
            core.WriteCommittedArch(0, registers.FramePointerRegister, registers.FramePointer);
            core.WriteCommittedArch(0, registers.ThreadPointerRegister, registers.ThreadPointer);
            core.WriteCommittedArch(0, registers.ReturnAddressRegister, registers.ReturnAddress);

            int retired = 0;
            while (core.ReadCommittedPc(0) != HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel && retired++ < 512)
            {
                ulong pc = core.ReadCommittedPc(0);
                int bundleIndex = checked((int)((pc - image.ImageBase) / HybridCpuBundleSerializer.BundleSizeBytes));
                Assert.InRange(bundleIndex, 0, image.ImageBytes.Length / HybridCpuBundleSerializer.BundleSizeBytes - 1);
                VLIW_Bundle decoded = new VLIW_Bundle();
                Assert.True(decoded.TryReadBytes(image.ImageBytes, bundleIndex * HybridCpuBundleSerializer.BundleSizeBytes));
                VLIW_Instruction[] bundle = Enumerable.Range(0, 8).Select(decoded.GetInstruction).ToArray();
                RetireBundle(core, bundle, pc);
                if (core.ReadCommittedPc(0) == pc)
                    core.WriteCommittedPc(0, checked(pc + (ulong)HybridCpuBundleSerializer.BundleSizeBytes));
            }

            Assert.True(retired <= 512);
            Assert.Equal(HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel, core.ReadCommittedPc(0));
            Assert.Equal(30UL, core.ReadArch(0, registers.ReturnValueRegister));
            Assert.Equal(registers.StackPointer, core.ReadArch(0, registers.StackPointerRegister));
        }
        finally
        {
            Processor.MainMemory = originalMemory;
            Processor.CurrentProcessorMode = originalMode;
            Processor.Memory = originalMemorySubsystem;
        }
    }

    [Fact]
    public void RecursivePresentedWorld_RemainsRejectedByCompilerOwnedSccGate()
    {
        using ImageRun run = Run(nameof(ManagedCallGraphFixtures.ParameterlessSelfRecursive),
            [nameof(ManagedCallGraphFixtures.ParameterlessSelfRecursive)]);

        Assert.Equal(13, run.ExitCode);
        Assert.Contains("HCSCF-RECURSION1001", run.StandardError, StringComparison.Ordinal);
        Assert.Empty(run.Package);
    }

    [Fact]
    public void TamperedBodyPresentationDigest_IsRejectedBeforeImport()
    {
        using ImageRun run = Run(nameof(ManagedCallGraphFixtures.ParameterlessRoot),
            [nameof(ManagedCallGraphFixtures.ParameterlessRoot)], tamperDigest: true);

        Assert.Equal(12, run.ExitCode);
        Assert.Contains("HCNAOT3002", run.StandardError, StringComparison.Ordinal);
        Assert.Empty(run.Package);
    }

    [Fact]
    public void RestrictedStartupRejectsParameterizedManagedEntry()
    {
        using ImageRun run = Run(nameof(ManagedCallGraphFixtures.Root),
            [nameof(ManagedCallGraphFixtures.Root), nameof(ManagedCallGraphFixtures.Middle), nameof(ManagedCallGraphFixtures.Leaf)]);

        Assert.Equal(14, run.ExitCode);
        Assert.Contains("HCNAOT3004", run.StandardError, StringComparison.Ordinal);
        Assert.Empty(run.Package);
    }

    [Fact]
    public void SdkProfile_IsExplicitAndKeepsLegacyRouteWithoutFallbacks()
    {
        string root = FindRepositoryRoot();
        string props = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "NativeAot", "Packaging", "HybridCPU.Sdk.props"));
        string targets = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "NativeAot", "Packaging", "HybridCPU.Sdk.targets"));
        string legacy = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "NativeAot", "Samples", "Phase26RestrictedPublish", "Phase26RestrictedPublish.csproj"));
        string profile = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "NativeAot", "Samples", "Phase05ScalarControlFlowV2Publish", "Phase05ScalarControlFlowV2Publish.csproj"));

        Assert.Contains("LegacyRestrictedSingleMethod", props, StringComparison.Ordinal);
        Assert.Contains(ScalarControlFlowV2ProfileContractV1.ProfileId, targets, StringComparison.Ordinal);
        Assert.Contains("--presented-methods", targets, StringComparison.Ordinal);
        Assert.Contains("HCPUB1113", targets, StringComparison.Ordinal);
        Assert.DoesNotContain("HybridCpuProfile", legacy, StringComparison.Ordinal);
        Assert.Contains(ScalarControlFlowV2ProfileContractV1.ProfileId, profile, StringComparison.Ordinal);
        Assert.DoesNotContain("LLVM", props + targets + profile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JIT", props + targets + profile, StringComparison.OrdinalIgnoreCase);
    }

    private static ImageRun Run(string rootMethod, IEnumerable<string> methodNames, bool tamperDigest = false)
    {
        string runDirectory = Path.Combine(Path.GetTempPath(), $"hybridcpu-r6-phase05-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);
        string assembly = Path.Combine(runDirectory, Path.GetFileName(FixtureAssembly));
        File.Copy(FixtureAssembly, assembly);
        string output = Path.Combine(runDirectory, "image.hcexe");
        string presentationPath = output + ".body-world.json";
        string assemblySha = Hash(File.ReadAllBytes(assembly));
        NativeAotPresentedBodyV1[] bodies = methodNames
            .Select(PresentedBody)
            .Distinct()
            .OrderBy(static body => body.TypeName, StringComparer.Ordinal)
            .ThenBy(static body => body.MethodName, StringComparer.Ordinal)
            .ToArray();
        NativeAotBodyPresentationV1 draft = new NativeAotBodyPresentationV1(NativeAotBodyPresentationV1.Schema,
            NativeAotBodyPresentationV1.Version, ScalarControlFlowV2ProfileContractV1.ProfileId,
            assemblySha, PresentedBody(rootMethod), bodies, string.Empty);
        NativeAotBodyPresentationV1 presentation = draft with
        {
            ContractDigest = tamperDigest ? new string('0', 64) : PresentationDigest(draft)
        };
        File.WriteAllText(presentationPath, JsonSerializer.Serialize(presentation));

        ProcessStartInfo start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["HYBRIDCPU_NATIVEAOT_BODY_PRESENTATION_V1"] = presentationPath;
        start.ArgumentList.Add(typeof(NativeAotSeamArtifactV1).Assembly.Location);
        start.ArgumentList.Add("compile-image");
        AddPair(start, "--assembly", assembly);
        AddPair(start, "--type", FixtureType);
        AddPair(start, "--method", rootMethod);
        AddPair(start, "--out", output);
        AddPair(start, "--source-commit", NativeAotSeamBaselineV1.SourceCommit);
        AddPair(start, "--patch-digest", NativeAotSeamBaselineV1.PatchDigest);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Adapter process did not start.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        byte[] package = File.Exists(output) ? File.ReadAllBytes(output) : [];
        string json = File.Exists(output + ".json") ? File.ReadAllText(output + ".json") : string.Empty;
        NativeAotRestrictedImageArtifactV1? manifest = string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize<NativeAotRestrictedImageArtifactV1>(json);
        return new(process.ExitCode, stdout, stderr, output, presentationPath, package, json, manifest, runDirectory);
    }

    private static string PresentationDigest(NativeAotBodyPresentationV1 presentation) =>
        Hash(Encoding.UTF8.GetBytes(string.Join('|', presentation.SchemaId, presentation.SchemaVersion,
            presentation.ProfileId, presentation.AssemblySha256,
            $"{presentation.Root.TypeName}::{presentation.Root.MethodName}:0x{presentation.Root.MetadataToken:x8}",
            string.Join(';', presentation.PresentedBodies.Select(static body =>
                $"{body.TypeName}::{body.MethodName}:0x{body.MetadataToken:x8}")))));

    private static NativeAotPresentedBodyV1 PresentedBody(string methodName)
    {
        MethodInfo method = typeof(ManagedCallGraphFixtures).GetMethod(methodName,
            BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException(FixtureType, methodName);
        return new(FixtureType, methodName, method.MetadataToken);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void RetireBundle(Processor.CPU_Core core, VLIW_Instruction[] bundle, ulong pc)
    {
        bool control = bundle.Any(static instruction => instruction.OpCode is
            >= (uint)Processor.CPU_Core.InstructionsEnum.JAL and <= (uint)Processor.CPU_Core.InstructionsEnum.BGEU);
        core.TestRunDecodeStageWithFetchedBundle(bundle, pc);
        core.TestRunExecuteStageFromCurrentDecodeState();
        core.TestRunMemoryAndWriteBackStagesFromCurrentExecuteState();
        if (!control) core.WriteCommittedPc(0, pc);
    }

    private static void AddPair(ProcessStartInfo start, string name, string value)
    {
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(value);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Compilers", "HybridCPU_Compiler")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record ImageRun(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        string OutputPath,
        string PresentationPath,
        byte[] Package,
        string ManifestJson,
        NativeAotRestrictedImageArtifactV1? Manifest,
        string RunDirectory) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(RunDirectory)) Directory.Delete(RunDirectory, recursive: true);
        }
    }

}
