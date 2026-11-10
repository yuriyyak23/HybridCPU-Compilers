using HybridCPU_ISE.CloseToHSL.Core.Runtime.Managed;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.Compiler.NativeAot;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RefPlan7.Phase00.Corpus;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase08ExactAotGenericsTests
{
    private static readonly byte[] FixtureImage = File.ReadAllBytes(typeof(GenericCorpus).Assembly.Location);
    private static readonly string FixtureType = typeof(GenericCorpus).FullName!;

    [Fact]
    public void ExactClosedMethodSpecs_CreateDistinctBodiesAndNestedReachability()
    {
        ManagedCallGraphCompilationV1 graph = ImportWorld(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            nameof(GenericCorpus.ExactIntProgram), nameof(GenericCorpus.ExactLongProgram),
            nameof(GenericCorpus.ExactReferenceProgram));

        Assert.True(graph.Status == RestrictedCilImportStatusV1.Success,
            string.Join(" | ", graph.Diagnostics.Select(static row => $"{row.Code}:{row.Message}")));
        ManagedCompiledMethodV1[] identities = graph.Methods
            .Where(static row => row.Identity.MethodName == nameof(GenericCorpus.Identity)).ToArray();
        Assert.Equal(3, identities.Length);
        Assert.Single(identities.Select(static row => row.Identity.MetadataToken).Distinct());
        Assert.Equal(3, identities.Select(static row => row.Identity.StableIdentity).Distinct().Count());
        Assert.All(identities, static row =>
        {
            Assert.Equal("hybridcpu.managed-method-exact-generic/v1", row.Identity.SchemaId);
            Assert.Single(row.Identity.GenericMethodArguments!);
        });
        Assert.Contains(identities, static row =>
            row.Identity.GenericMethodArguments![0].StableTypeIdentity == "System.Int32");
        Assert.Contains(identities, static row =>
            row.Identity.GenericMethodArguments![0].StableTypeIdentity == "System.Int64");
        Assert.Contains(identities, static row =>
            row.Identity.GenericMethodArguments![0].StableTypeIdentity.EndsWith("GenericReference", StringComparison.Ordinal) &&
            row.Identity.GenericMethodArguments![0].CarrierType == RestrictedCilTypeV1.ObjectReference);
        Assert.Equal(6, graph.Graph!.ExactGenericInstantiationCount);
        Assert.Equal(ExactAotGenericsContractV1.Default.ContractDigest,
            graph.Graph.ExactAotGenericsContractDigest);
        ManagedCallGraphCompilationV1 reversed = ImportWorld(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            nameof(GenericCorpus.ExactReferenceProgram), nameof(GenericCorpus.ExactLongProgram),
            nameof(GenericCorpus.ExactIntProgram));
        Assert.Equal(graph.Graph.GraphDigest, reversed.Graph!.GraphDigest);
        Assert.Equal(graph.Graph.CompilationOrder, reversed.Graph.CompilationOrder);
    }

    [Fact]
    public void AdapterPresentedWorld_AdmitsOnlyPresentedDefinitionsAndDiscoversExactInstances()
    {
        ManagedCallGraphCompilationV1 graph = new RestrictedCilImporterV1(
            mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
            ManagedBodyWorldModeV1.AdapterPresented, FixtureImage, "phase08-adapter-presented",
            [new(FixtureType, nameof(GenericCorpus.ExactIntProgram))],
            [new(FixtureType, nameof(GenericCorpus.ExactIntProgram)),
             new(FixtureType, nameof(GenericCorpus.Nested)),
             new(FixtureType, nameof(GenericCorpus.Identity))]));

        Assert.Equal(RestrictedCilImportStatusV1.Success, graph.Status);
        Assert.Equal(3, graph.Methods.Count);
        Assert.Equal(2, graph.Methods.Count(static row => row.Identity.SchemaId ==
            "hybridcpu.managed-method-exact-generic/v1"));
    }

    [Fact]
    public void ConstructedGenericTypeSpec_IsAnExactCompiledTypeInstantiation()
    {
        ManagedCallGraphCompilationV1 graph = ImportWorld(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            nameof(GenericCorpus.ConstructedTypeProgram));

        Assert.True(graph.Status == RestrictedCilImportStatusV1.Success,
            string.Join(" | ", graph.Diagnostics.Select(static row => $"{row.Code}:{row.Message}")));
        ManagedCompiledMethodV1 body = graph.Methods.Single(static row =>
            row.Identity.DeclaringType.EndsWith("GenericBox`1", StringComparison.Ordinal));
        RestrictedCilGenericArgumentV1 argument = Assert.Single(body.Identity.GenericTypeArguments!);
        Assert.Equal("System.Int32", argument.StableTypeIdentity);
        Assert.Equal(RestrictedCilTypeV1.Int32, argument.CarrierType);
        Assert.Equal(47UL, Execute(Link(nameof(GenericCorpus.ConstructedTypeProgram)).RestrictedImage!));
    }

    [Theory]
    [InlineData(nameof(GenericCorpus.GenericVirtualProgram), RestrictedCilDispatchKindV1.Virtual)]
    [InlineData(nameof(GenericCorpus.GenericInterfaceProgram), RestrictedCilDispatchKindV1.Interface)]
    public void GenericVirtualAndInterfaceCalls_UseExactConstructedSignatureAndCandidateBody(
        string methodName,
        RestrictedCilDispatchKindV1 kind)
    {
        int callToken = CallvirtToken(methodName);
        int candidateToken = typeof(GenericImplementation<>).GetMethod(nameof(GenericImplementation<int>.Invoke))!.MetadataToken;
        RestrictedCilGenericArgumentV1[] arguments = [new("System.Int32", RestrictedCilTypeV1.Int32)];
        RestrictedCilDispatchBindingV1 binding = new RestrictedCilDispatchBindingV1(callToken, $"phase08:{kind}:invoke",
            [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32], RestrictedCilTypeV1.Int32,
            kind, kind == RestrictedCilDispatchKindV1.Virtual ? 0x8101UL : 0x8102UL,
            kind == RestrictedCilDispatchKindV1.Interface ? 0x8201UL : null,
            GenericTypeArguments: arguments,
            ExactGenericCandidates: [new(candidateToken, arguments)]);
        ManagedCallGraphCompilationV1 graph = new RestrictedCilImporterV1(
            mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            dispatchBindings: [binding]).ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, FixtureImage, $"phase08-{kind}",
            [new(FixtureType, methodName)], []));

        Assert.True(graph.Status == RestrictedCilImportStatusV1.Success,
            string.Join(" | ", graph.Diagnostics.Select(static row => $"{row.Code}:{row.Message}")));
        ManagedCompiledMethodV1 candidate = graph.Methods.Single(static row =>
            row.Identity.DeclaringType.EndsWith("GenericImplementation`1", StringComparison.Ordinal));
        Assert.Equal("System.Int32", Assert.Single(candidate.Identity.GenericTypeArguments!).StableTypeIdentity);
        Assert.Contains(graph.Graph!.Edges, row => row.IsDispatchCandidate && row.MetadataToken == candidateToken);
    }

    [Fact]
    public void OpenAndConstrainedGenericBodies_FailClosedWithDistinctDiagnostics()
    {
        RestrictedCilImporterV1 importer = new(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        RestrictedCilImportResultV1 open = importer.ImportImage(FixtureImage,
            new(FixtureType, nameof(GenericCorpus.Identity)), "phase08-open");
        ManagedCallGraphCompilationV1 constrained = ImportWorld(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            nameof(GenericCorpus.UnsupportedConstraintProgram));

        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, open.Status);
        Assert.Equal("HCCIL1012", Assert.Single(open.Diagnostics).Code);
        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, constrained.Status);
        Assert.Equal("HCCIL1702", Assert.Single(constrained.Diagnostics).Code);
    }

    [Fact]
    public void ExactInstantiationBudget_IsDeterministicAndFailsBeforeArtifacts()
    {
        ScalarControlFlowV2Budgets budgets = ScalarControlFlowV2ProfileContractV1.Default.Budgets with
        {
            MaximumReachableMethods = 2
        };
        ManagedCallGraphCompilationV1 graph = new RestrictedCilImporterV1(
            mode: RestrictedCilImportModeV1.ScalarControlFlowV2, graphBudgets: budgets).ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, FixtureImage, "phase08-budget",
            [new(FixtureType, nameof(GenericCorpus.ExactIntProgram))], []));

        Assert.Equal(RestrictedCilImportStatusV1.BudgetExhausted, graph.Status);
        Assert.Equal("HCSCF-BUDGET2006", Assert.Single(graph.Diagnostics).Code);
        Assert.Empty(graph.Methods);
        Assert.Null(graph.Graph);
    }

    [Fact]
    public void ExactAotContract_ForbidsSharingDictionariesRuntimeCodegenAndAuthorityExpansion()
    {
        ExactAotGenericsContractV1 contract = ExactAotGenericsContractV1.Default;
        Assert.True(contract.ExactBodyPerInstantiation);
        Assert.True(contract.SupportsConstructedReferenceTypes);
        Assert.False(contract.SupportsGenericSharing);
        Assert.False(contract.SupportsRuntimeDictionaries);
        Assert.False(contract.SupportsRuntimeCodeGeneration);
        Assert.False(contract.SupportsOpenInstantiations);
        Assert.False(contract.SupportsGenericConstraints);
        Assert.False(contract.HasRuntimeAuthority);
        Assert.False(contract.HasIseAuthority);
        Assert.False(contract.OwnsReachability);
        Assert.Equal(64, contract.ContractDigest.Length);
        Assert.Contains(RestrictedCilSupportMatrixV1.Default.Features, static row =>
            row.Feature == "exact-aot-generics-core" && row.Support == RestrictedCilMatrixSupportV1.Supported);
    }

    [Fact]
    public void ConstructedGenericRuntimeIdentities_HaveIndependentStaticsAndExactDispatchTypes()
    {
        HybridCpuManagedTypeSystemBuildV1 first = BuildConstructedTypes(reverse: false);
        HybridCpuManagedTypeSystemBuildV1 second = BuildConstructedTypes(reverse: true);
        Assert.True(first.IsSuccess, first.Reason);
        Assert.True(second.IsSuccess, second.Reason);
        Assert.Equal(first.Digest, second.Digest);
        HybridCpuManagedTypeSystemV1 types = first.TypeSystem!;
        HybridCpuManagedTypeDescriptorV1 intBox = types.Descriptors.Single(static row =>
            row.StableIdentity == "Phase08.Box<System.Int32>");
        HybridCpuManagedTypeDescriptorV1 refBox = types.Descriptors.Single(static row =>
            row.StableIdentity == "Phase08.Box<Phase08.GenericReference>");
        Assert.NotEqual(intBox.TypeId, refBox.TypeId);
        Assert.NotSame(types.StaticStorage(intBox.TypeId), types.StaticStorage(refBox.TypeId));
        types.StaticStorage(intBox.TypeId)![0] = 0x5a;
        Assert.Equal(0, types.StaticStorage(refBox.TypeId)![0]);
        HybridCpuManagedTypeDescriptorV1 implementation = types.Descriptors.Single(static row =>
            row.StableIdentity == "Phase08.Implementation<System.Int32>");
        HybridCpuManagedTypeDescriptorV1 contract = types.Descriptors.Single(static row =>
            row.StableIdentity == "Phase08.IContract<System.Int32>");
        Assert.True(types.IsAssignable(implementation.TypeId, contract.TypeId));
        HybridCpuManagedTypeDescriptorV1 baseType = types.Descriptors.Single(static row =>
            row.StableIdentity == "Phase08.Base<System.Int32>");
        HybridCpuManagedTypeDescriptorV1 derivedType = types.Descriptors.Single(static row =>
            row.StableIdentity == "Phase08.Derived<System.Int32>");
        const string signature = "object-ref,int32->int32";
        ulong virtualSlot = HybridCpuManagedDispatchTableBuilderV1.ComputeSlotId(baseType.TypeId,
            "Phase08.Base<System.Int32>.Invoke", signature);
        ulong interfaceSlot = HybridCpuManagedDispatchTableBuilderV1.ComputeSlotId(contract.TypeId,
            "Phase08.IContract<System.Int32>.Invoke", signature);
        HybridCpuManagedDispatchTableBuildV1 dispatch = new HybridCpuManagedDispatchTableBuilderV1().Build(types,
        [
            new("Phase08.IContract<System.Int32>.Invoke", contract.TypeId, signature, 0, 0, true, true),
            new("Phase08.Implementation<System.Int32>.Invoke", implementation.TypeId, signature, 0x1000, 0, false, false),
            new("Phase08.Base<System.Int32>.Invoke", baseType.TypeId, signature, 0x2000, 0, true, true),
            new("Phase08.Derived<System.Int32>.Invoke", derivedType.TypeId, signature, 0x3000, 0, true, false, virtualSlot)
        ],
        [new(implementation.TypeId, contract.TypeId, interfaceSlot,
            "Phase08.Implementation<System.Int32>.Invoke")]);
        Assert.True(dispatch.IsSuccess, dispatch.Reason);
        Assert.Contains(dispatch.Table!.VirtualEntries, row =>
            row.RuntimeTypeId == derivedType.TypeId && row.SlotId == virtualSlot);
        Assert.Contains(dispatch.Table.InterfaceEntries, row =>
            row.RuntimeTypeId == implementation.TypeId && row.InterfaceTypeId == contract.TypeId &&
            row.SlotId == interfaceSlot);
    }

    [Fact]
    public void ExactGenericImage_IsDeterministicAndExecutesThroughLoaderAndIse()
    {
        ScalarControlFlowV2LinkedProgramV1 first = Link(nameof(GenericCorpus.ExactIntProgram));
        ScalarControlFlowV2LinkedProgramV1 second = Link(nameof(GenericCorpus.ExactIntProgram));
        Assert.Equal(first.LinkedImage!.ImageSha256, second.LinkedImage!.ImageSha256);
        Assert.Equal(SHA256.HashData(first.RestrictedImage!.PackageBytes),
            SHA256.HashData(second.RestrictedImage!.PackageBytes));
        Assert.Equal(37UL, Execute(first.RestrictedImage));
    }

    [Fact]
    public void NativeAotAdapter_PresentsExactGenericWorldAndEmitsDeterministicExecutableImage()
    {
        AdapterRun first = RunAdapter();
        AdapterRun second = RunAdapter();
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(first.Package, second.Package);
        NativeAotRestrictedImageArtifactV1 manifest = Assert.IsType<NativeAotRestrictedImageArtifactV1>(first.Manifest);
        Assert.Equal(3, manifest.CompiledMethodCount);
        Assert.Matches("^[0-9a-f]{64}$", manifest.ManagedGraphDigest);
        HybridCpuRestrictedImageV1 image = new HybridCpuRestrictedImageBuilderV1().Inspect(first.Package);
        Assert.Equal(HybridCpuStartupStatusV1.Success, image.Status);
        Assert.Equal(37UL, Execute(image));
    }

    private static ManagedCallGraphCompilationV1 ImportWorld(ManagedBodyWorldModeV1 mode, params string[] roots) =>
        new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
            mode, FixtureImage, "refplan7-phase08-generics-corpus.dll",
            roots.Select(static root => new RestrictedCilMethodSelectorV1(FixtureType, root)).ToArray(), []));

    private static ScalarControlFlowV2LinkedProgramV1 Link(string root)
    {
        ManagedCallGraphCompilationV1 graph = ImportWorld(ManagedBodyWorldModeV1.StandaloneRestrictedModule, root);
        Assert.Equal(RestrictedCilImportStatusV1.Success, graph.Status);
        string entry = graph.Graph!.RootIdentities.Single();
        ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(graph, entry);
        Assert.Equal(ScalarControlFlowV2LinkStatusV1.Success, linked.Status);
        return linked;
    }

    private static AdapterRun RunAdapter()
    {
        string runDirectory = Path.Combine(Path.GetTempPath(), $"hybridcpu-r7-phase08-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);
        string assemblyPath = Path.Combine(runDirectory, Path.GetFileName(typeof(GenericCorpus).Assembly.Location));
        File.Copy(typeof(GenericCorpus).Assembly.Location, assemblyPath);
        string output = Path.Combine(runDirectory, "phase08.hcexe");
        string presentationPath = output + ".body-world.json";
        NativeAotPresentedBodyV1[] bodies =
        [
            PresentedBody(nameof(GenericCorpus.ExactIntProgram)),
            PresentedBody(nameof(GenericCorpus.Identity)),
            PresentedBody(nameof(GenericCorpus.Nested))
        ];
        bodies = bodies.OrderBy(static row => row.TypeName, StringComparer.Ordinal)
            .ThenBy(static row => row.MethodName, StringComparer.Ordinal)
            .ThenBy(static row => row.MetadataToken).ToArray();
        NativeAotBodyPresentationV1 draft = new NativeAotBodyPresentationV1(NativeAotBodyPresentationV1.Schema,
            NativeAotBodyPresentationV1.Version, ScalarControlFlowV2ProfileContractV1.ProfileId,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))).ToLowerInvariant(),
            PresentedBody(nameof(GenericCorpus.ExactIntProgram)), bodies, string.Empty);
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
            draft.SchemaId, draft.SchemaVersion, draft.ProfileId, draft.AssemblySha256,
            $"{draft.Root.TypeName}::{draft.Root.MethodName}:0x{draft.Root.MetadataToken:x8}",
            string.Join(';', draft.PresentedBodies.Select(static row =>
                $"{row.TypeName}::{row.MethodName}:0x{row.MetadataToken:x8}"))))))
            .ToLowerInvariant();
        File.WriteAllText(presentationPath, JsonSerializer.Serialize(draft with { ContractDigest = digest }));
        try
        {
            ProcessStartInfo start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.Environment["HYBRIDCPU_NATIVEAOT_BODY_PRESENTATION_V1"] = presentationPath;
            start.ArgumentList.Add(typeof(NativeAotSeamArtifactV1).Assembly.Location);
            start.ArgumentList.Add("compile-image");
            AddPair(start, "--assembly", assemblyPath);
            AddPair(start, "--type", FixtureType);
            AddPair(start, "--method", nameof(GenericCorpus.ExactIntProgram));
            AddPair(start, "--out", output);
            AddPair(start, "--source-commit", NativeAotSeamBaselineV1.SourceCommit);
            AddPair(start, "--patch-digest", NativeAotSeamBaselineV1.PatchDigest);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Adapter did not start.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            byte[] package = File.Exists(output) ? File.ReadAllBytes(output) : [];
            NativeAotRestrictedImageArtifactV1? manifest = File.Exists(output + ".json")
                ? JsonSerializer.Deserialize<NativeAotRestrictedImageArtifactV1>(File.ReadAllText(output + ".json"))
                : null;
            return new(process.ExitCode, stdout, stderr, package, manifest);
        }
        finally
        {
            if (Directory.Exists(runDirectory)) Directory.Delete(runDirectory, recursive: true);
        }
    }

    private static NativeAotPresentedBodyV1 PresentedBody(string methodName)
    {
        MethodInfo method = typeof(GenericCorpus).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Missing generic corpus method '{methodName}'.");
        return new(FixtureType, methodName, method.MetadataToken);
    }

    private static int CallvirtToken(string methodName)
    {
        byte[] bytes = typeof(GenericCorpus).GetMethod(methodName)!.GetMethodBody()!.GetILAsByteArray()!;
        int offset = Array.IndexOf(bytes, (byte)0x6f);
        Assert.True(offset >= 0 && offset + 5 <= bytes.Length);
        return BitConverter.ToInt32(bytes, offset + 1);
    }

    private static void AddPair(ProcessStartInfo start, string name, string value)
    {
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(value);
    }

    private static HybridCpuManagedTypeSystemBuildV1 BuildConstructedTypes(bool reverse)
    {
        HybridCpuManagedTypeDeclarationV1[] declarations =
        [
            new("Phase08.Box<System.Int32>", HybridCpuManagedTypeKindV1.Class, null, [],
                [new("Value", HybridCpuManagedStorageKindV1.Primitive, 8, 8, true, 0)]),
            new("Phase08.Box<Phase08.GenericReference>", HybridCpuManagedTypeKindV1.Class, null, [],
                [new("Value", HybridCpuManagedStorageKindV1.ObjectReference, 8, 8, true, 0)]),
            new("Phase08.IContract<System.Int32>", HybridCpuManagedTypeKindV1.Interface, null, [], []),
            new("Phase08.Implementation<System.Int32>", HybridCpuManagedTypeKindV1.Class, null,
                ["Phase08.IContract<System.Int32>"], []),
            new("Phase08.Base<System.Int32>", HybridCpuManagedTypeKindV1.Class, null, [], []),
            new("Phase08.Derived<System.Int32>", HybridCpuManagedTypeKindV1.Class,
                "Phase08.Base<System.Int32>", [], [])
        ];
        return new HybridCpuManagedTypeSystemBuilderV1().Build(reverse ? declarations.Reverse() : declarations);
    }

    private static ulong Execute(HybridCpuRestrictedImageV1 image)
    {
        HybridCpuStartupRegisterStateV1 registers = image.InitialRegisters!;
        Processor.MainMemoryArea originalMemory = Processor.MainMemory;
        ProcessorMode originalMode = Processor.CurrentProcessorMode;
        MemorySubsystem? originalSubsystem = Processor.Memory;
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
            core.WriteCommittedArch(0, registers.ReturnAddressRegister, registers.ReturnAddress);
            int retired = 0;
            while (core.ReadCommittedPc(0) != HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel && retired++ < 256)
            {
                ulong pc = core.ReadCommittedPc(0);
                int index = checked((int)((pc - image.ImageBase) / HybridCpuBundleSerializer.BundleSizeBytes));
                Assert.InRange(index, 0, image.ImageBytes.Length / HybridCpuBundleSerializer.BundleSizeBytes - 1);
                VLIW_Instruction[] bundle = CompilerRefPlan7Phase03ManagedHeapAllocatorTests.ReadBundle(image, pc);
                bool control = bundle.Any(static instruction => instruction.OpCode is
                    >= (uint)Processor.CPU_Core.InstructionsEnum.JAL and <= (uint)Processor.CPU_Core.InstructionsEnum.BGEU);
                core.TestRunDecodeStageWithFetchedBundle(bundle, pc);
                core.TestRunExecuteStageFromCurrentDecodeState();
                core.TestRunMemoryAndWriteBackStagesFromCurrentExecuteState();
                if (!control || core.ReadCommittedPc(0) == pc)
                    core.WriteCommittedPc(0, checked(pc + (ulong)HybridCpuBundleSerializer.BundleSizeBytes));
            }
            Assert.True(retired <= 256);
            Assert.Equal(HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel, core.ReadCommittedPc(0));
            return core.ReadArch(0, registers.ReturnValueRegister);
        }
        finally
        {
            Processor.MainMemory = originalMemory;
            Processor.CurrentProcessorMode = originalMode;
            Processor.Memory = originalSubsystem;
        }
    }


    private sealed record AdapterRun(int ExitCode, string StandardOutput, string StandardError,
        byte[] Package, NativeAotRestrictedImageArtifactV1? Manifest);
}
