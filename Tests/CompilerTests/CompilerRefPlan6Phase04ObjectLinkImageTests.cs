using HybridCPU_ISE.CloseToHSL.Core.Runtime.Managed;
using System.Reflection;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests.CompilerTests;

[Collection("RefPlan6 ISE Execution")]
public sealed class CompilerRefPlan6Phase04ObjectLinkImageTests
{
    private static readonly byte[] FixtureImage = File.ReadAllBytes(typeof(ManagedCallGraphFixtures).Assembly.Location);
    private static readonly string FixtureType = typeof(ManagedCallGraphFixtures).FullName!;

    [Fact]
    public void ManagedCallRelocation_HasExactCarrierBasisWidthAndFailClosedRange()
    {
        HybridCpuRelocationEvaluationV1 forward = HybridCpuObjectFormatContractV1.EvaluateRelocation(
            HybridCpuRelocationKind.ManagedCallRelativeSigned16, 0x10800, 0x10060, 0);
        HybridCpuRelocationEvaluationV1 backward = HybridCpuObjectFormatContractV1.EvaluateRelocation(
            HybridCpuRelocationKind.ManagedCallRelativeSigned16, 0x10000, 0x10820, 0);
        HybridCpuRelocationEvaluationV1 overflow = HybridCpuObjectFormatContractV1.EvaluateRelocation(
            HybridCpuRelocationKind.ManagedCallRelativeSigned16, 0x20000, 0x10000, 0);
        HybridCpuRelocationEvaluationV1 badAddend = HybridCpuObjectFormatContractV1.EvaluateRelocation(
            HybridCpuRelocationKind.ManagedCallRelativeSigned16, 0x10800, 0x10000, 1);

        Assert.Equal(16, forward.WidthBits);
        Assert.Equal(0x0800UL, forward.EncodedValue);
        Assert.Equal((ulong)unchecked((ushort)-0x800), backward.EncodedValue);
        Assert.Equal("HCOBJ1013", overflow.DiagnosticCode);
        Assert.Equal("HCOBJ1016", badAddend.DiagnosticCode);
        Assert.Equal(HybridCpuBundleSerializer.BundleSizeBytes,
            HybridCpuManagedCallRelocationContractV1.BundleSizeBytes);
        Assert.Equal(HybridCpuInstructionWord.EncodedSize,
            HybridCpuManagedCallRelocationContractV1.InstructionSlotSizeBytes);
    }

    [Fact]
    public void OneHcoPerMethod_ContainsStableDefinitionDeclarationsAndUnresolvedCalls()
    {
        ManagedCallGraphCompilationV1 graph = Import(nameof(ManagedCallGraphFixtures.Root));
        ScalarControlFlowV2LinkedProgramV1 result = Link(graph, nameof(ManagedCallGraphFixtures.Root));

        Assert.Equal(ScalarControlFlowV2LinkStatusV1.Success, result.Status);
        Assert.Equal(graph.Graph!.CompilationOrder, result.MethodObjects.Select(static item => item.MethodIdentity));
        Assert.Equal(3, result.MethodObjects.Count);
        Assert.All(result.MethodObjects, static item => Assert.Equal(HybridCpuObjectStatusV1.Success, item.ObjectArtifact.Status));
        Assert.All(result.MethodObjects, static item => Assert.Matches("^m[0-9]{4}:[0-9a-f]{32}$", item.ModuleIdentity));
        ScalarControlFlowV2MethodObjectV1 root = result.MethodObjects.Single(item =>
            item.MethodIdentity == graph.Graph.RootIdentities.Single());
        Assert.Single(root.ObjectArtifact.Symbols, symbol =>
            symbol.IsDefinition && symbol.Name == root.MethodIdentity);
        Assert.Single(root.ObjectArtifact.Symbols, static symbol => !symbol.IsDefinition);
        HybridCpuObjectRelocationV1[] relocations = root.ObjectArtifact.Relocations.ToArray();
        Assert.Equal(2, relocations.Length);
        Assert.Contains(relocations, static relocation =>
            relocation.Kind == HybridCpuRelocationKind.ManagedCallPcRelativeHighSigned16 && relocation.Addend == 0);
        Assert.Contains(relocations, static relocation =>
            relocation.Kind == HybridCpuRelocationKind.ManagedCallPcRelativeLowSigned16 &&
            relocation.Addend > 0 && relocation.Addend % HybridCpuBundleSerializer.BundleSizeBytes == 0);
        Assert.All(relocations, static relocation =>
            Assert.Equal(0UL, relocation.Offset % HybridCpuInstructionWord.EncodedSize));
    }

    [Fact]
    public void MultiMethodLink_IsIndependentOfDiscoveryOrderAndPreservesSharedCalleeAndLoopHelper()
    {
        ScalarControlFlowV2LinkedProgramV1 first = Link(
            Import(nameof(ManagedCallGraphFixtures.DiamondRoot)), nameof(ManagedCallGraphFixtures.DiamondRoot));
        ScalarControlFlowV2LinkedProgramV1 second = Link(
            Import(nameof(ManagedCallGraphFixtures.DiamondRoot)), nameof(ManagedCallGraphFixtures.DiamondRoot));
        HybridCpuLinkInputV1[] reversed = first.MethodObjects.Reverse().Select(static item =>
            new HybridCpuLinkInputV1(item.ModuleIdentity, item.ObjectArtifact.Bytes)).ToArray();
        HybridCpuStaticLinkArtifactV1 relinked = new HybridCpuStaticLinkerV1().Link(reversed);
        ScalarControlFlowV2LinkedProgramV1 loop = Link(
            Import(nameof(ManagedCallGraphFixtures.LoopRoot)), nameof(ManagedCallGraphFixtures.LoopRoot));

        Assert.Equal(first.OrderedObjectDigest, second.OrderedObjectDigest);
        Assert.Equal(first.ProvenanceDigest, second.ProvenanceDigest);
        Assert.Equal(first.RestrictedImage!.PackageBytes, second.RestrictedImage!.PackageBytes);
        Assert.Equal(first.LinkedImage!.ImageBytes, relinked.ImageBytes);
        Assert.Equal(first.LinkedImage.LinkMapDigest, relinked.LinkMapDigest);
        Assert.Equal(4, first.MethodObjects.Count);
        Assert.Equal(8, first.LinkedImage.AppliedRelocations.Count);
        Assert.Equal(ScalarControlFlowV2LinkStatusV1.Success, loop.Status);
        string loopHelperIdentity = Import(nameof(ManagedCallGraphFixtures.LoopRoot)).Methods.Single(static method =>
            method.Identity.MethodName == nameof(ManagedCallGraphFixtures.LoopHelper)).Identity.StableIdentity;
        Assert.True(loop.MethodObjects.Single(item => item.MethodIdentity == loopHelperIdentity)
            .ObjectArtifact.Relocations.Count == 0);
    }

    [Fact]
    public void OneMethodNoCallObject_RemainsCanonicalWithoutRelocationExtensionBytes()
    {
        ManagedCallGraphCompilationV1 graph = Import(nameof(ManagedCallGraphFixtures.Leaf));
        ScalarControlFlowV2LinkedProgramV1 result = Link(graph, nameof(ManagedCallGraphFixtures.Leaf));
        ScalarControlFlowV2MethodObjectV1 method = Assert.Single(result.MethodObjects);
        HybridCpuObjectArtifactV1 direct = new HybridCpuObjectWriterV1().Write(new(
            method.ObjectArtifact.Sections, method.ObjectArtifact.Symbols, Array.Empty<HybridCpuObjectRelocationV1>(),
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));

        Assert.Empty(method.ObjectArtifact.Relocations);
        Assert.Equal(direct.Bytes, method.ObjectArtifact.Bytes);
        Assert.Equal(direct.ObjectSha256, method.ObjectArtifact.ObjectSha256);
    }

    [Fact]
    public void MissingDuplicateAndMalformedRelocationsFailClosedWhileManagedCallRangeUsesCurrentThunk()
    {
        ManagedCallGraphCompilationV1 graph = Import(nameof(ManagedCallGraphFixtures.Root));
        ScalarControlFlowV2LinkedProgramV1 valid = Link(graph, nameof(ManagedCallGraphFixtures.Root));
        ScalarControlFlowV2MethodObjectV1 caller = valid.MethodObjects.Single(item =>
            item.MethodIdentity == graph.Graph!.RootIdentities.Single());
        HybridCpuStaticLinkerV1 linker = new();
        HybridCpuStaticLinkArtifactV1 missing = linker.Link([new("caller", caller.ObjectArtifact.Bytes)]);
        HybridCpuStaticLinkArtifactV1 duplicate = linker.Link([
            new("a", valid.MethodObjects[0].ObjectArtifact.Bytes),
            new("b", valid.MethodObjects[0].ObjectArtifact.Bytes)]);

        byte[] jal = CallCode();
        HybridCpuObjectArtifactV1 unsupported = Object(jal,
            [Definition("caller", (ulong)jal.Length), Declaration("target")],
            [new(".text", 0, HybridCpuRelocationKind.BundleRelativeSigned16, "target", 0)]);
        HybridCpuObjectArtifactV1 misaligned = Object(jal,
            [Definition("caller", (ulong)jal.Length), Declaration("target")],
            [new(".text", 2, HybridCpuRelocationKind.ManagedCallRelativeSigned16, "target", 0)]);

        byte[] overflowCaller = ObjectBytes(jal,
            [Definition("caller", (ulong)jal.Length), Declaration("target")],
            [new(".text", 0, HybridCpuRelocationKind.ManagedCallRelativeSigned16, "target", 0)]);
        byte[] padding = ObjectBytes(new byte[0x8000], [Definition("padding", 0x8000)], []);
        byte[] target = ObjectBytes(new byte[HybridCpuBundleSerializer.BundleSizeBytes],
            [Definition("target", HybridCpuBundleSerializer.BundleSizeBytes)], []);
        HybridCpuStaticLinkArtifactV1 range = linker.Link([
            new("a-caller", overflowCaller), new("b-padding", padding), new("c-target", target)]);

        Assert.Equal("HCLINK1003", Assert.Single(missing.Diagnostics).Code);
        Assert.Equal("HCLINK1002", Assert.Single(duplicate.Diagnostics).Code);
        Assert.Equal("HCOBJ1015", Assert.Single(unsupported.Diagnostics).Code);
        Assert.Equal("HCOBJ1016", Assert.Single(misaligned.Diagnostics).Code);
        Assert.Equal(HybridCpuLinkStatusV1.Success, range.Status);
        Assert.Empty(range.Diagnostics);
        Assert.NotEmpty(range.ImageBytes);
    }

    [Fact]
    public void InvalidEntryAndStartupAdjustment_FailClosed()
    {
        ManagedCallGraphCompilationV1 graph = Import(nameof(ManagedCallGraphFixtures.Root));
        ScalarControlFlowV2LinkedProgramV1 invalidEntry = new ScalarControlFlowV2ObjectLinkerV1().Link(
            graph, graph.Methods.Single(static method => method.Identity.MethodName == nameof(ManagedCallGraphFixtures.Leaf)).Identity.StableIdentity);
        ScalarControlFlowV2LinkedProgramV1 valid = Link(graph, nameof(ManagedCallGraphFixtures.Root));
        HybridCpuRestrictedImageV1 badAdjustment = new HybridCpuRestrictedImageBuilderV1().Build(new(
            valid.LinkedImage!, valid.EntryIdentity, ReturnAddressAdjustmentBytes: 1));

        Assert.Equal(ScalarControlFlowV2LinkStatusV1.InvalidInput, invalidEntry.Status);
        Assert.Equal("HCSCF-LINK4001", Assert.Single(invalidEntry.Diagnostics).Code);
        Assert.Equal(HybridCpuStartupStatusV1.Unsupported, badAdjustment.Status);
        Assert.Equal("HCSTART1007", Assert.Single(badAdjustment.Diagnostics).Code);
    }

    [Fact]
    public void ProfileObjectInputBudgetOverflow_FailsBeforeBackendOrLink()
    {
        ManagedCallGraphCompilationV1 admitted = Import(nameof(ManagedCallGraphFixtures.Leaf));
        ManagedCompiledMethodV1 method = Assert.Single(admitted.Methods);
        ManagedCallGraphCompilationV1 oversized = admitted with
        {
            Methods = Enumerable.Repeat(method,
                ScalarControlFlowV2ProfileContractV1.Default.Budgets.MaximumReachableMethods + 1).ToArray()
        };

        ScalarControlFlowV2LinkedProgramV1 result = new ScalarControlFlowV2ObjectLinkerV1().Link(
            oversized, admitted.Graph!.RootIdentities.Single());

        Assert.Equal(ScalarControlFlowV2LinkStatusV1.BudgetExhausted, result.Status);
        Assert.Equal("HCSCF-BACKEND-BUDGET4001", Assert.Single(result.Diagnostics).Code);
        Assert.Empty(result.MethodObjects);
        Assert.Null(result.LinkedImage);
        Assert.Null(result.RestrictedImage);
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(5, 9)]
    [InlineData(17, 33)]
    public void LinkedCrossObjectCalls_ExecuteThroughRestrictedImageAndIse(int value, int expected)
    {
        ScalarControlFlowV2LinkedProgramV1 program = Link(
            Import(nameof(ManagedCallGraphFixtures.Root)), nameof(ManagedCallGraphFixtures.Root));
        HybridCpuRestrictedImageV1 image = Assert.IsType<HybridCpuRestrictedImageV1>(program.RestrictedImage);
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
            core.WriteCommittedArch(0, HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[0], unchecked((ulong)(long)value));

            int retired = 0;
            while (core.ReadCommittedPc(0) != HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel && retired++ < 256)
            {
                ulong pc = core.ReadCommittedPc(0);
                int bundleIndex = checked((int)((pc - image.ImageBase) / HybridCpuBundleSerializer.BundleSizeBytes));
                Assert.InRange(bundleIndex, 0, image.ImageBytes.Length / HybridCpuBundleSerializer.BundleSizeBytes - 1);
                VLIW_Instruction[] bundle = ReadBundle(image.ImageBytes, bundleIndex);
                RetireBundle(core, bundle, pc);
                if (core.ReadCommittedPc(0) == pc)
                    core.WriteCommittedPc(0, checked(pc + (ulong)HybridCpuBundleSerializer.BundleSizeBytes));
            }

            Assert.True(retired <= 256);
            Assert.Equal(HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel, core.ReadCommittedPc(0));
            Assert.Equal(unchecked((ulong)(long)expected), core.ReadArch(0, registers.ReturnValueRegister));
            Assert.Equal(registers.StackPointer, core.ReadArch(0, registers.StackPointerRegister));
        }
        finally
        {
            Processor.MainMemory = originalMemory;
            Processor.CurrentProcessorMode = originalMode;
            Processor.Memory = originalMemorySubsystem;
        }
    }

    private static ManagedCallGraphCompilationV1 Import(string root) =>
        new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, FixtureImage, "phase04-fixture.dll",
            [new(FixtureType, root)], []));

    private static ScalarControlFlowV2LinkedProgramV1 Link(ManagedCallGraphCompilationV1 graph, string rootName)
    {
        string entry = graph.Methods.Single(method => method.Identity.MethodName == rootName).Identity.StableIdentity;
        ScalarControlFlowV2LinkedProgramV1 result = new ScalarControlFlowV2ObjectLinkerV1().Link(graph, entry);
        Assert.True(result.Status == ScalarControlFlowV2LinkStatusV1.Success,
            $"{result.Status}: {string.Join('|', result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}:{diagnostic.StableIdentity}:{diagnostic.Message}"))}");
        Assert.False(result.HasRuntimeAuthority);
        Assert.False(result.HasExecutionAuthority);
        Assert.False(result.HasPublicationAuthority);
        return result;
    }

    private static HybridCpuObjectArtifactV1 Object(
        byte[] code,
        IReadOnlyList<HybridCpuObjectSymbolV1> symbols,
        IReadOnlyList<HybridCpuObjectRelocationV1> relocations) =>
        new HybridCpuObjectWriterV1().Write(new(
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes, code, (ulong)code.Length)],
            symbols, relocations, HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));

    private static byte[] ObjectBytes(
        byte[] code,
        IReadOnlyList<HybridCpuObjectSymbolV1> symbols,
        IReadOnlyList<HybridCpuObjectRelocationV1> relocations)
    {
        HybridCpuObjectArtifactV1 artifact = Object(code, symbols, relocations);
        Assert.Equal(HybridCpuObjectStatusV1.Success, artifact.Status);
        return artifact.Bytes;
    }

    private static HybridCpuObjectSymbolV1 Definition(string name, ulong size) =>
        new(name, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default,
            ".text", 0, size, IsDefinition: true);

    private static HybridCpuObjectSymbolV1 Declaration(string name) =>
        new(name, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default,
            null, 0, 0, IsDefinition: false);

    private static byte[] CallCode()
    {
        HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
        bundle.SetInstruction(0, new HybridCpuInstructionWord { OpCode = (uint)HybridCpuOpcode.JAL });
        return new HybridCpuBundleSerializer().SerializeProgram([bundle]);
    }

    private static VLIW_Instruction[] ReadBundle(byte[] image, int bundleIndex)
    {
        VLIW_Bundle bundle = new VLIW_Bundle();
        Assert.True(bundle.TryReadBytes(image, checked(bundleIndex * HybridCpuBundleSerializer.BundleSizeBytes)));
        return Enumerable.Range(0, HybridCpuInstructionBundle.SlotCount).Select(bundle.GetInstruction).ToArray();
    }

    private static void RetireBundle(Processor.CPU_Core core, VLIW_Instruction[] bundle, ulong pc)
    {
        bool control = bundle.Any(static instruction => instruction.OpCode is
            >= (uint)Processor.CPU_Core.InstructionsEnum.JAL and <= (uint)Processor.CPU_Core.InstructionsEnum.BGEU);
        core.TestRunDecodeStageWithFetchedBundle(bundle, pc);
        core.TestRunExecuteStageFromCurrentDecodeState();
        core.TestRunMemoryAndWriteBackStagesFromCurrentExecuteState();
        if (!control) core.WriteCommittedPc(0, pc);
    }

}
