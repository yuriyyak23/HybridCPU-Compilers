using HybridCPU_ISE.CloseToHSL.Core.Runtime.Managed;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.Compiler.NativeAot;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests.CompilerTests;

[Collection("RefPlan6 ISE Execution")]
public sealed class CompilerRefPlan6Phase07BoundedRecursionTests
{
    private static readonly byte[] FixtureImage = File.ReadAllBytes(typeof(Phase07RecursionFixtures).Assembly.Location);
    private static readonly string FixtureType = typeof(Phase07RecursionFixtures).FullName!;

    [Fact]
    public void Capability_IsV1ExtensionDefaultOffAndNonAuthoritative()
    {
        ManagedBoundedRecursionContractV1 contract = ManagedBoundedRecursionContractV1.Default;
        Assert.Equal("hybridcpu.scalar-control-flow-v2.bounded-recursion/v1", ManagedBoundedRecursionContractV1.SchemaId);
        Assert.True(contract.ExtendsScalarControlFlowV1);
        Assert.False(contract.RequiresRuntimeGuard);
        Assert.False(contract.HasRuntimeAuthority);
        Assert.True(ManagedBoundedRecursionOptionsV1.Qualification.IsValid);
        Assert.True(ManagedBoundedRecursionOptionsV1.Disabled.IsValid);
        Assert.False(ManagedBoundedRecursionOptionsV1.Create(true, 65, 1024).IsValid);
    }

    [Fact]
    public void DirectAndMutualCountdownSccs_HaveStableProofsAndCompilationOrder()
    {
        ManagedCallGraphCompilationV1 direct = Import(nameof(Phase07RecursionFixtures.DirectRoot),
            [nameof(Phase07RecursionFixtures.DirectRoot), nameof(Phase07RecursionFixtures.Direct)]);
        ManagedCallGraphCompilationV1 mutual = Import(nameof(Phase07RecursionFixtures.MutualRoot),
            [nameof(Phase07RecursionFixtures.MutualRoot), nameof(Phase07RecursionFixtures.MutualA), nameof(Phase07RecursionFixtures.MutualB)]);
        ManagedCallGraphCompilationV1 mutualShuffled = Import(nameof(Phase07RecursionFixtures.MutualRoot),
            [nameof(Phase07RecursionFixtures.MutualB), nameof(Phase07RecursionFixtures.MutualRoot), nameof(Phase07RecursionFixtures.MutualA)]);

        AssertSuccess(direct);
        AssertSuccess(mutual);
        AssertSuccess(mutualShuffled);
        ManagedRecursionProofV1 directProof = Assert.Single(direct.Graph!.RecursionProofs!);
        Assert.Equal(6, directProof.MaximumSccInvocations);
        Assert.Equal([5], directProof.ConstantIngressArguments);
        Assert.Equal(7, direct.Graph.MaximumDynamicDepth);
        Assert.Equal(mutual.Graph!.GraphDigest, mutualShuffled.Graph!.GraphDigest);
        Assert.Equal(mutual.Graph.CompilationOrder, mutualShuffled.Graph.CompilationOrder);
        Assert.Equal(mutual.Graph.RecursionProofs!.Single().ProofDigest,
            mutualShuffled.Graph.RecursionProofs!.Single().ProofDigest);
    }

    [Fact]
    public void Recursion_RemainsRejectedWithoutCapabilityAndForUnprovedShapes()
    {
        AssertFailure(Import(nameof(Phase07RecursionFixtures.DirectRoot),
            [nameof(Phase07RecursionFixtures.DirectRoot), nameof(Phase07RecursionFixtures.Direct)], ManagedBoundedRecursionOptionsV1.Disabled),
            RestrictedCilImportStatusV1.Unsupported, "HCSCF-RECURSION1001");
        AssertFailure(Import(nameof(Phase07RecursionFixtures.UnguardedRoot),
            [nameof(Phase07RecursionFixtures.UnguardedRoot), nameof(Phase07RecursionFixtures.Unguarded)]),
            RestrictedCilImportStatusV1.Unsupported, "HCSCF-RECURSION1006");
        AssertFailure(Import(nameof(Phase07RecursionFixtures.NonDecrementRoot),
            [nameof(Phase07RecursionFixtures.NonDecrementRoot), nameof(Phase07RecursionFixtures.NonDecrement)]),
            RestrictedCilImportStatusV1.Unsupported, "HCSCF-RECURSION1005");
        AssertFailure(Import(nameof(Phase07RecursionFixtures.DynamicIngressRoot),
            [nameof(Phase07RecursionFixtures.DynamicIngressRoot), nameof(Phase07RecursionFixtures.Direct)]),
            RestrictedCilImportStatusV1.Unsupported, "HCSCF-RECURSION1007");
    }

    [Fact]
    public void DynamicDepthBudget_FailsAtExactProvenBoundary()
    {
        ManagedBoundedRecursionOptionsV1 exact = ManagedBoundedRecursionOptionsV1.Create(true, 7, 1024 * 1024);
        ManagedBoundedRecursionOptionsV1 oneShort = ManagedBoundedRecursionOptionsV1.Create(true, 6, 1024 * 1024);
        AssertSuccess(Import(nameof(Phase07RecursionFixtures.DirectRoot),
            [nameof(Phase07RecursionFixtures.DirectRoot), nameof(Phase07RecursionFixtures.Direct)], exact));
        AssertFailure(Import(nameof(Phase07RecursionFixtures.DirectRoot),
            [nameof(Phase07RecursionFixtures.DirectRoot), nameof(Phase07RecursionFixtures.Direct)], oneShort),
            RestrictedCilImportStatusV1.BudgetExhausted, "HCSCF-RECURSION2003");
    }

    [Fact]
    public void PostRaStackBudget_FailsAtExactConservativeBoundary()
    {
        ManagedCallGraphCompilationV1 admitted = Import(nameof(Phase07RecursionFixtures.DirectRoot),
            [nameof(Phase07RecursionFixtures.DirectRoot), nameof(Phase07RecursionFixtures.Direct)]);
        AssertSuccess(admitted);
        string entry = admitted.Graph!.RootIdentities.Single();
        ScalarControlFlowV2LinkedProgramV1 first = new ScalarControlFlowV2ObjectLinkerV1().Link(admitted, entry);
        Assert.Equal(ScalarControlFlowV2LinkStatusV1.Success, first.Status);
        int required = Assert.IsType<ManagedRecursionStackEvidenceV1>(first.RecursionStackEvidence).RequiredStackBytes;
        Assert.True(required > 0);

        ManagedCallGraphCompilationV1 exact = Import(nameof(Phase07RecursionFixtures.DirectRoot),
            [nameof(Phase07RecursionFixtures.DirectRoot), nameof(Phase07RecursionFixtures.Direct)],
            ManagedBoundedRecursionOptionsV1.Create(true, 7, required));
        Assert.Equal(ScalarControlFlowV2LinkStatusV1.Success,
            new ScalarControlFlowV2ObjectLinkerV1().Link(exact, exact.Graph!.RootIdentities.Single()).Status);
        ManagedCallGraphCompilationV1 oneByteShort = Import(nameof(Phase07RecursionFixtures.DirectRoot),
            [nameof(Phase07RecursionFixtures.DirectRoot), nameof(Phase07RecursionFixtures.Direct)],
            ManagedBoundedRecursionOptionsV1.Create(true, 7, required - 1));
        ScalarControlFlowV2LinkedProgramV1 rejected = new ScalarControlFlowV2ObjectLinkerV1().Link(
            oneByteShort, oneByteShort.Graph!.RootIdentities.Single());
        Assert.Equal(ScalarControlFlowV2LinkStatusV1.BudgetExhausted, rejected.Status);
        Assert.Equal("HCSCF-RECURSION2004", Assert.Single(rejected.Diagnostics).Code);
    }

    [Theory]
    [InlineData(nameof(Phase07RecursionFixtures.DirectRoot), 16UL,
        nameof(Phase07RecursionFixtures.DirectRoot), nameof(Phase07RecursionFixtures.Direct))]
    [InlineData(nameof(Phase07RecursionFixtures.MutualRoot), 6UL,
        nameof(Phase07RecursionFixtures.MutualRoot), nameof(Phase07RecursionFixtures.MutualA), nameof(Phase07RecursionFixtures.MutualB))]
    [InlineData(nameof(Phase07RecursionFixtures.LiveRoot), 20UL,
        nameof(Phase07RecursionFixtures.LiveRoot), nameof(Phase07RecursionFixtures.Live))]
    [InlineData(nameof(Phase07RecursionFixtures.NestedHelperRoot), 24UL,
        nameof(Phase07RecursionFixtures.NestedHelperRoot), nameof(Phase07RecursionFixtures.NestedHelper), nameof(Phase07RecursionFixtures.Helper))]
    public void BoundedRecursion_ExecutesOnIseWithFramesAndSavedValues(string root, ulong expected, params string[] methods)
    {
        ManagedCallGraphCompilationV1 compilation = Import(root, methods);
        AssertSuccess(compilation);
        ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(
            compilation, compilation.Graph!.RootIdentities.Single());
        Assert.Equal(ScalarControlFlowV2LinkStatusV1.Success, linked.Status);
        Assert.NotNull(linked.RecursionStackEvidence);
        Assert.Equal(expected, Execute(Assert.IsType<HybridCpuRestrictedImageV1>(linked.RestrictedImage)));
    }

    [Fact]
    public void ConstantReturnFromTakenBranch_ExecutesOnIse()
    {
        ManagedCallGraphCompilationV1 compilation = Import(nameof(Phase07RecursionFixtures.ConstantBranchRoot),
            [nameof(Phase07RecursionFixtures.ConstantBranchRoot), nameof(Phase07RecursionFixtures.ConstantBranch)]);
        AssertSuccess(compilation);
        ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(
            compilation, compilation.Graph!.RootIdentities.Single());
        Assert.Equal(ScalarControlFlowV2LinkStatusV1.Success, linked.Status);
        Assert.Equal(1UL, Execute(Assert.IsType<HybridCpuRestrictedImageV1>(linked.RestrictedImage)));
    }

    [Fact]
    public void RecursiveFrames_WithRegisterPressureSpills_ExecuteAndRestoreStack()
    {
        ManagedCallGraphCompilationV1 compilation = Import(nameof(Phase07RecursionFixtures.PressureRoot),
            [nameof(Phase07RecursionFixtures.PressureRoot), nameof(Phase07RecursionFixtures.Pressure)]);
        AssertSuccess(compilation);
        ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(
            compilation, compilation.Graph!.RootIdentities.Single());
        Assert.Equal(ScalarControlFlowV2LinkStatusV1.Success, linked.Status);
        ScalarControlFlowV2MethodObjectV1 recursive = linked.MethodObjects.Single(method =>
            method.MethodIdentity.Contains(".Pressure(System.Int32)", StringComparison.Ordinal));
        Assert.True(recursive.FrameSizeBytes > 32);
        Assert.True(recursive.SpillCount > 0);
        Assert.Equal(960UL, Execute(Assert.IsType<HybridCpuRestrictedImageV1>(linked.RestrictedImage)));
    }

    [Fact]
    public void ObjectLinkAndImage_AreDeterministicAcrossPresentedBodyOrder()
    {
        string[] methods = [nameof(Phase07RecursionFixtures.MutualRoot), nameof(Phase07RecursionFixtures.MutualA), nameof(Phase07RecursionFixtures.MutualB)];
        ManagedCallGraphCompilationV1 firstCompilation = Import(nameof(Phase07RecursionFixtures.MutualRoot), methods);
        ManagedCallGraphCompilationV1 secondCompilation = Import(nameof(Phase07RecursionFixtures.MutualRoot), methods.Reverse().ToArray());
        AssertSuccess(firstCompilation);
        AssertSuccess(secondCompilation);
        ScalarControlFlowV2LinkedProgramV1 first = new ScalarControlFlowV2ObjectLinkerV1().Link(firstCompilation, firstCompilation.Graph!.RootIdentities.Single());
        ScalarControlFlowV2LinkedProgramV1 second = new ScalarControlFlowV2ObjectLinkerV1().Link(secondCompilation, secondCompilation.Graph!.RootIdentities.Single());
        Assert.Equal(ScalarControlFlowV2LinkStatusV1.Success, first.Status);
        Assert.Equal(ScalarControlFlowV2LinkStatusV1.Success, second.Status);
        Assert.Equal(first.OrderedObjectDigest, second.OrderedObjectDigest);
        Assert.Equal(first.ProvenanceDigest, second.ProvenanceDigest);
        Assert.Equal(first.RecursionStackEvidence!.EvidenceDigest, second.RecursionStackEvidence!.EvidenceDigest);
        Assert.Equal(first.RestrictedImage!.PackageSha256, second.RestrictedImage!.PackageSha256);
        Assert.Equal(first.RestrictedImage.PackageBytes, second.RestrictedImage.PackageBytes);
    }

    [Fact]
    public void NativeAotAdapter_PropagatesV1RecursionProofAndStackProvenance()
    {
        string runDirectory = Path.Combine(Path.GetTempPath(), $"hybridcpu-r6-phase07-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);
        string output = Path.Combine(runDirectory, "phase07.hcexe");
        string presentationPath = output + ".body-world.json";
        try
        {
            string assemblyPath = Path.Combine(runDirectory, Path.GetFileName(typeof(Phase07RecursionFixtures).Assembly.Location));
            File.Copy(typeof(Phase07RecursionFixtures).Assembly.Location, assemblyPath);
            string assemblySha = Hash(File.ReadAllBytes(assemblyPath));
            NativeAotPresentedBodyV1[] bodies = [
                PresentedBody(nameof(Phase07RecursionFixtures.Direct)),
                PresentedBody(nameof(Phase07RecursionFixtures.DirectRoot))];
            NativeAotBodyPresentationV1 draft = new NativeAotBodyPresentationV1(NativeAotBodyPresentationV1.Schema,
                NativeAotBodyPresentationV1.Version, ScalarControlFlowV2ProfileContractV1.ProfileId,
                assemblySha, PresentedBody(nameof(Phase07RecursionFixtures.DirectRoot)), bodies, string.Empty);
            NativeAotBodyPresentationV1 presentation = draft with { ContractDigest = PresentationDigest(draft) };
            File.WriteAllText(presentationPath, JsonSerializer.Serialize(presentation));

            ProcessStartInfo start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.Environment["HYBRIDCPU_NATIVEAOT_BODY_PRESENTATION_V1"] = presentationPath;
            start.Environment["HYBRIDCPU_BOUNDED_RECURSION_V1"] = JsonSerializer.Serialize(
                ManagedBoundedRecursionOptionsV1.Qualification);
            start.ArgumentList.Add(typeof(NativeAotSeamArtifactV1).Assembly.Location);
            start.ArgumentList.Add("compile-image");
            AddPair(start, "--assembly", assemblyPath);
            AddPair(start, "--type", FixtureType);
            AddPair(start, "--method", nameof(Phase07RecursionFixtures.DirectRoot));
            AddPair(start, "--out", output);
            AddPair(start, "--source-commit", NativeAotSeamBaselineV1.SourceCommit);
            AddPair(start, "--patch-digest", NativeAotSeamBaselineV1.PatchDigest);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Adapter process did not start.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"stdout={stdout}; stderr={stderr}");
            NativeAotRestrictedImageArtifactV1 artifact = Assert.IsType<NativeAotRestrictedImageArtifactV1>(
                JsonSerializer.Deserialize<NativeAotRestrictedImageArtifactV1>(File.ReadAllText(output + ".json")));
            Assert.Equal(ManagedBoundedRecursionContractV1.Default.ContractDigest, artifact.BoundedRecursionContractDigest);
            Assert.Matches("^[0-9a-f]{64}$", artifact.RecursionProofDigest);
            Assert.Matches("^[0-9a-f]{64}$", artifact.RecursionStackEvidenceDigest);
            Assert.Equal(7, artifact.MaximumDynamicDepth);
            Assert.True(artifact.RequiredStackBytes > 0);
            Assert.False(artifact.HasRuntimeAuthority);
        }
        finally
        {
            if (Directory.Exists(runDirectory)) Directory.Delete(runDirectory, recursive: true);
        }
    }

    private static ManagedCallGraphCompilationV1 Import(string root, IReadOnlyList<string> presented,
        ManagedBoundedRecursionOptionsV1? options = null) =>
        new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
            ManagedBodyWorldModeV1.AdapterPresented, FixtureImage, "phase07-recursion-fixture.dll",
            [new(FixtureType, root)], presented.Select(static method => new RestrictedCilMethodSelectorV1(FixtureType, method)).ToArray(),
            options ?? ManagedBoundedRecursionOptionsV1.Qualification));

    private static void AssertSuccess(ManagedCallGraphCompilationV1 result)
    {
        Assert.True(result.Status == RestrictedCilImportStatusV1.Success,
            $"{result.Status}: {string.Join(" | ", result.Diagnostics.Select(static item => $"{item.Code}:{item.Message}:{item.StableSourceIdentity}"))}");
        Assert.NotNull(result.Graph);
        Assert.Empty(result.Diagnostics);
    }

    private static void AssertFailure(ManagedCallGraphCompilationV1 result, RestrictedCilImportStatusV1 status, string code)
    {
        Assert.Equal(status, result.Status);
        Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
        Assert.Null(result.Graph);
        Assert.Empty(result.Methods);
    }

    private static string PresentationDigest(NativeAotBodyPresentationV1 presentation) =>
        Hash(Encoding.UTF8.GetBytes(string.Join('|', presentation.SchemaId, presentation.SchemaVersion,
            presentation.ProfileId, presentation.AssemblySha256,
            $"{presentation.Root.TypeName}::{presentation.Root.MethodName}:0x{presentation.Root.MetadataToken:x8}",
            string.Join(';', presentation.PresentedBodies.Select(static body =>
                $"{body.TypeName}::{body.MethodName}:0x{body.MetadataToken:x8}")))));

    private static NativeAotPresentedBodyV1 PresentedBody(string methodName)
    {
        MethodInfo method = typeof(Phase07RecursionFixtures).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Missing recursion fixture method '{methodName}'.");
        return new(FixtureType, methodName, method.MetadataToken);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void AddPair(ProcessStartInfo start, string name, string value)
    {
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(value);
    }

    private static ulong Execute(HybridCpuRestrictedImageV1 image)
    {
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
            Processor.CPU_Core core = new Processor.CPU_Core(0, CpuCorePlatformContext.CreateFixed(Processor.MainMemory, ProcessorMode.Compiler));
            core.InitializePipeline();
            core.PrepareExecutionStart(image.EntryAddress);
            for (int register = 0; register < 32; register++) core.WriteCommittedArch(0, register, 0);
            core.WriteCommittedPc(0, image.EntryAddress);
            core.WriteCommittedArch(0, registers.StackPointerRegister, registers.StackPointer);
            core.WriteCommittedArch(0, registers.FramePointerRegister, registers.FramePointer);
            core.WriteCommittedArch(0, registers.ThreadPointerRegister, registers.ThreadPointer);
            core.WriteCommittedArch(0, registers.ReturnAddressRegister, registers.ReturnAddress);
            int retired = 0;
            while (core.ReadCommittedPc(0) != HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel && retired++ < 2048)
            {
                ulong pc = core.ReadCommittedPc(0);
                int bundleIndex = checked((int)((pc - image.ImageBase) / HybridCpuBundleSerializer.BundleSizeBytes));
                Assert.InRange(bundleIndex, 0, image.ImageBytes.Length / HybridCpuBundleSerializer.BundleSizeBytes - 1);
                VLIW_Bundle decoded = new VLIW_Bundle();
                Assert.True(decoded.TryReadBytes(image.ImageBytes, bundleIndex * HybridCpuBundleSerializer.BundleSizeBytes));
                VLIW_Instruction[] bundle = Enumerable.Range(0, 8).Select(decoded.GetInstruction).ToArray();
                bool control = bundle.Any(static instruction => instruction.OpCode is
                    >= (uint)Processor.CPU_Core.InstructionsEnum.JAL and <= (uint)Processor.CPU_Core.InstructionsEnum.BGEU);
                core.TestRunDecodeStageWithFetchedBundle(bundle, pc);
                core.TestRunExecuteStageFromCurrentDecodeState();
                core.TestRunMemoryAndWriteBackStagesFromCurrentExecuteState();
                if (!control) core.WriteCommittedPc(0, pc);
                if (core.ReadCommittedPc(0) == pc)
                    core.WriteCommittedPc(0, checked(pc + (ulong)HybridCpuBundleSerializer.BundleSizeBytes));
            }
            Assert.True(retired <= 2048);
            Assert.Equal(HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel, core.ReadCommittedPc(0));
            Assert.Equal(registers.StackPointer, core.ReadArch(0, registers.StackPointerRegister));
            return core.ReadArch(0, registers.ReturnValueRegister);
        }
        finally
        {
            Processor.MainMemory = originalMemory;
            Processor.CurrentProcessorMode = originalMode;
            Processor.Memory = originalMemorySubsystem;
        }
    }

}

public static class Phase07RecursionFixtures
{
    public static int ConstantBranchRoot() => ConstantBranch(0);
    public static int ConstantBranch(int value) => value == 0 ? 1 : 2;
    public static int DirectRoot() => Direct(5);
    public static int Direct(int value) => value == 0 ? 1 : Direct(value - 1) + value;
    public static int MutualRoot() => MutualA(6);
    public static int MutualA(int value) => value == 0 ? 0 : MutualB(value - 1) + 1;
    public static int MutualB(int value) => value == 0 ? 0 : MutualA(value - 1) + 1;
    public static int LiveRoot() => Live(4);
    public static int Live(int value)
    {
        int preserved = value + 2;
        return value == 0 ? preserved : Live(value - 1) + preserved;
    }
    public static int NestedHelperRoot() => NestedHelper(4);
    public static int NestedHelper(int value) => value == 0 ? 0 : Helper(value) + NestedHelper(value - 1);
    public static int Helper(int value) => value * 2 + 1;
    public static int UnguardedRoot() => Unguarded(3);
    public static int Unguarded(int value) => Unguarded(value - 1);
    public static int NonDecrementRoot() => NonDecrement(3);
    public static int NonDecrement(int value) => value == 0 ? 0 : NonDecrement(value);
    public static int DynamicIngressRoot(int value) => Direct(value);
    public static int PressureRoot() => Pressure(3);
    public static int Pressure(int value)
    {
        int a01 = value + 1, a02 = value + 2, a03 = value + 3, a04 = value + 4, a05 = value + 5;
        int a06 = value + 6, a07 = value + 7, a08 = value + 8, a09 = value + 9, a10 = value + 10;
        int a11 = value + 11, a12 = value + 12, a13 = value + 13, a14 = value + 14, a15 = value + 15;
        int a16 = value + 16, a17 = value + 17, a18 = value + 18, a19 = value + 19, a20 = value + 20;
        int recursive = value == 0 ? 0 : Pressure(value - 1);
        return recursive + a01 + a02 + a03 + a04 + a05 + a06 + a07 + a08 + a09 + a10 +
            a11 + a12 + a13 + a14 + a15 + a16 + a17 + a18 + a19 + a20;
    }
}
