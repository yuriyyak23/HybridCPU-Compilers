using System.Buffers.Binary;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.Compiler.NativeAot;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RefPlan7.Phase00.Corpus;
using HybridCPU.RuntimeKernel;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase07ManagedDelegateTests
{
    private const ulong StaticMethod = 11;
    private const ulong InstanceMethod = 12;
    private const ulong StaticAddress = 0x110000;
    private const ulong InstanceAddress = 0x120000;
    private const ulong ThunkAddress = 0x130000;
    private static readonly ulong Signature = HybridCpuManagedDelegateRuntimeV1.ComputeSignatureId("int32(int32)");
    private static readonly ulong OpenSignature = HybridCpuManagedDelegateRuntimeV1.ComputeSignatureId(
        "int32(object-reference,int32)");
    private static readonly byte[] FixtureImage = File.ReadAllBytes(typeof(DelegateCorpus).Assembly.Location);
    private static readonly HybridCpuMiiResourceModelV1 ResourceModel =
        HybridCpuMiiResourceModelV1.Create(new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8);

    [Fact]
    public void Runtime_StaticClosedOpenAndFunctionPointersAreExactAndDeterministic()
    {
        Subject subject = CreateSubject();
        HybridCpuManagedDelegateResultV1 pointer = subject.Runtime.ResolveFunctionPointer(StaticMethod, Signature);
        Assert.True(pointer.IsSuccess, pointer.Reason);
        Assert.Equal(StaticAddress, pointer.CodeAddress);
        Assert.Equal(pointer, subject.Runtime.ValidateFunctionPointer(StaticAddress, Signature));
        Assert.Equal(InstanceAddress,
            subject.Runtime.ResolveFunctionPointer(InstanceMethod, OpenSignature).CodeAddress);

        HybridCpuManagedDelegateResultV1 @static = subject.Runtime.Create(new(
            subject.DelegateTypeHandle, Signature, HybridCpuManagedDelegateKindV1.Static, StaticAddress));
        HybridCpuManagedDelegateResultV1 closed = subject.Runtime.Create(new(
            subject.DelegateTypeHandle, Signature, HybridCpuManagedDelegateKindV1.ClosedInstance,
            InstanceAddress, subject.Target));
        HybridCpuManagedDelegateResultV1 open = subject.Runtime.Create(new(
            subject.DelegateTypeHandle, Signature, HybridCpuManagedDelegateKindV1.OpenInstance, InstanceAddress));

        Assert.True(@static.IsSuccess, @static.Reason);
        Assert.True(closed.IsSuccess, closed.Reason);
        Assert.True(open.IsSuccess, open.Reason);
        Assert.Equal((StaticAddress, 0UL, HybridCpuManagedDelegateKindV1.Static), Invocation(subject, @static));
        Assert.Equal((InstanceAddress, subject.Target, HybridCpuManagedDelegateKindV1.ClosedInstance), Invocation(subject, closed));
        Assert.Equal((InstanceAddress, 0UL, HybridCpuManagedDelegateKindV1.OpenInstance), Invocation(subject, open));
        Assert.Equal(ThunkAddress, subject.Runtime.ResolveInvocation(closed.DelegateReference, Signature).InvocationAddress);

        Subject repeated = CreateSubject(reverseMethods: true);
        Assert.Equal(subject.Runtime.ContractDigest, repeated.Runtime.ContractDigest);
    }

    [Fact]
    public void Runtime_NullSignatureFormMulticastAndNativePointerFailuresRemainDistinct()
    {
        Subject subject = CreateSubject();
        Assert.Equal(HybridCpuManagedDelegateStatusV1.NullDelegate,
            subject.Runtime.ResolveInvocation(0, Signature).Status);
        Assert.Equal(HybridCpuManagedDelegateStatusV1.SignatureMismatch,
            subject.Runtime.ValidateFunctionPointer(0xdeadbeef, Signature).Status);
        Assert.Equal(HybridCpuManagedDelegateStatusV1.SignatureMismatch,
            subject.Runtime.ResolveFunctionPointer(StaticMethod, Signature + 1).Status);
        Assert.Equal(HybridCpuManagedDelegateStatusV1.NullTarget,
            subject.Runtime.Create(new(subject.DelegateTypeHandle, Signature,
                HybridCpuManagedDelegateKindV1.ClosedInstance, InstanceAddress)).Status);
        Assert.Equal(HybridCpuManagedDelegateStatusV1.InvalidMetadata,
            subject.Runtime.Create(new(subject.DelegateTypeHandle, Signature,
                HybridCpuManagedDelegateKindV1.Static, InstanceAddress)).Status);
        Assert.Equal(HybridCpuManagedDelegateStatusV1.MulticastUnsupported,
            subject.Runtime.Create(new(subject.DelegateTypeHandle, Signature,
                HybridCpuManagedDelegateKindV1.Static, StaticAddress, InvocationCount: 2)).Status);
        Subject malformedLayout = CreateSubject(malformedDelegateLayout: true);
        Assert.Equal(HybridCpuManagedDelegateStatusV1.InvalidMetadata,
            malformedLayout.Runtime.Create(new(malformedLayout.DelegateTypeHandle, Signature,
                HybridCpuManagedDelegateKindV1.Static, StaticAddress)).Status);

        HybridCpuManagedDelegateResultV1 created = subject.Runtime.Create(new(subject.DelegateTypeHandle,
            Signature, HybridCpuManagedDelegateKindV1.Static, StaticAddress));
        Assert.True(created.IsSuccess, created.Reason);
        Assert.True(subject.Heap.WriteObjectBytes(created.DelegateReference,
            HybridCpuPlatformContractV1.ManagedDelegateLayout.KindOffsetBytes,
            [(byte)HybridCpuManagedDelegateKindV1.ClosedInstance]).IsSuccess);
        Assert.Equal(HybridCpuManagedDelegateStatusV1.InvalidDelegate,
            subject.Runtime.ResolveInvocation(created.DelegateReference, Signature).Status);

        Subject corruptedThunk = CreateSubject();
        HybridCpuManagedDelegateResultV1 other = corruptedThunk.Runtime.Create(new(corruptedThunk.DelegateTypeHandle,
            Signature, HybridCpuManagedDelegateKindV1.Static, StaticAddress));
        byte[] badThunk = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(badThunk, 0xdeadbeef);
        Assert.True(corruptedThunk.Heap.WriteObjectBytes(other.DelegateReference,
            HybridCpuPlatformContractV1.ManagedDelegateLayout.InvocationThunkOffsetBytes, badThunk).IsSuccess);
        Assert.Equal(HybridCpuManagedDelegateStatusV1.InvalidDelegate,
            corruptedThunk.Runtime.ResolveInvocation(other.DelegateReference, Signature).Status);
    }

    [Fact]
    public void DelegateTarget_IsAnExactGcVisibleReferenceAndSurvivesNonMovingCollection()
    {
        Subject subject = CreateSubject();
        HybridCpuManagedDelegateResultV1 created = subject.Runtime.Create(new(
            subject.DelegateTypeHandle, Signature, HybridCpuManagedDelegateKindV1.ClosedInstance,
            InstanceAddress, subject.Target));
        Assert.True(created.IsSuccess, created.Reason);
        HybridCpuManagedGcRootRegistryV1 roots = new HybridCpuManagedGcRootRegistryV1();
        Assert.True(roots.Register("delegate", HybridCpuManagedGcRootSourceV1.Handle, created.DelegateReference));
        HybridCpuManagedNonMovingGcV1 gc = new HybridCpuManagedNonMovingGcV1(subject.Types, subject.Heap,
            new string('a', 64), new string('b', 64), new string('c', 64), "phase07-test");

        HybridCpuManagedNonMovingGcResultV1 result = gc.Collect(new([], [], roots.Snapshot()),
            HybridCpuManagedNonMovingGcOptionsV1.Qualification);

        Assert.True(result.IsSuccess, result.Reason);
        Assert.Contains(created.DelegateReference, result.ReachableObjects);
        Assert.Contains(subject.Target, result.ReachableObjects);
        Assert.NotNull(subject.Heap.ReadObjectBytes(subject.Target));
        Assert.Equal(subject.Target, subject.Runtime.ResolveInvocation(created.DelegateReference, Signature).TargetObject);
    }

    [Fact]
    public void Contracts_AreNeutralBoundedAndCarryNoIseAuthority()
    {
        Subject subject = CreateSubject();
        HybridCpuManagedDelegateLayoutV1 layout = HybridCpuPlatformContractV1.ManagedDelegateLayout;
        Assert.Equal((16, 24, 32, 40, 48, 56, 64), (layout.TargetObjectOffsetBytes,
            layout.CodePointerOffsetBytes, layout.ContextOffsetBytes, layout.SignatureIdOffsetBytes,
            layout.KindOffsetBytes, layout.InvocationThunkOffsetBytes, layout.MinimumObjectSizeBytes));
        Assert.Equal(16_384, HybridCpuPlatformContractV1.MaximumManagedFunctionPointers);
        Assert.Equal((1, 63), (HybridCpuManagedAbiFamilyV1.SchemaMajor, HybridCpuManagedAbiFamilyV1.SchemaMinor));
        Assert.Equal("hybridcpu.runtime-pack/managed-contract-v1.25", HybridCpuManagedAbiFamilyV1.RuntimePackRevision);
        foreach (string helper in new[] { "__hybridcpu_managed_get_function_pointer",
                     "__hybridcpu_managed_get_virtual_function_pointer", "__hybridcpu_managed_validate_function_pointer",
                     "__hybridcpu_managed_create_delegate", "__hybridcpu_managed_resolve_delegate" })
            Assert.Equal(HybridCpuManagedAbiSupportV1.Supported,
                HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(helper)!.Support);
        Assert.Equal(HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff,
            HybridCpuManagedFeatureSetV1.Default.Workstreams.Single(static row =>
                row.Identity == "delegates-function-pointers").Support);
        Assert.Equal("1.15.193-refplan7-phase15", HybridCpuSdkPackContractV1.PackVersion);
        Assert.Contains("delegates-function-pointers",
            HybridCpuSdkPackContractV1.CreateManifest().QualifiedWorkstreams);
        Assert.DoesNotContain("delegates-function-pointers",
            HybridCpuSdkPackContractV1.CreateManifest().PublishQualifiedWorkstreams);
        Assert.True(subject.Runtime.HasDelegateSemanticAuthority);
        Assert.False(subject.Runtime.HasIseExecutionAuthority);
        Assert.DoesNotContain(typeof(HybridCpuPlatformContractV1).Assembly.GetReferencedAssemblies(), reference =>
            reference.Name is "HybridCPU.Compiler.Core" or "HybridCPU.ManagedRuntime" or "HybridCPU_ISE");
    }

    [Fact]
    public void CheckedInCil_StaticClosedInvokeAndManagedCalliUseExactBindingsAndGenericJalr()
    {
        RestrictedCilImportResultV1 @static = ImportDelegate(nameof(DelegateCorpus.CreateStatic));
        RestrictedCilImportResultV1 closed = ImportDelegate(nameof(DelegateCorpus.CreateClosed));
        RestrictedCilImportResultV1 invoke = ImportDelegate(nameof(DelegateCorpus.Invoke));
        RestrictedCilImportResultV1 calli = ImportDelegate(nameof(DelegateCorpus.InvokeManagedPointer));
        RestrictedCilImportResultV1 passedCalli = ImportDelegate(nameof(DelegateCorpus.InvokePassedManagedPointer));

        AssertImport(@static, "__hybridcpu_managed_get_function_pointer", "__hybridcpu_managed_create_delegate");
        AssertImport(closed, "__hybridcpu_managed_get_virtual_function_pointer", "__hybridcpu_managed_create_delegate");
        AssertImport(invoke, "__hybridcpu_managed_resolve_delegate");
        AssertImport(calli, "__hybridcpu_managed_get_function_pointer");
        Assert.DoesNotContain(calli.Program!.Instructions, static row =>
            row.Annotation.BranchTargetSymbolName == "__hybridcpu_managed_validate_function_pointer");
        AssertImport(passedCalli, "__hybridcpu_managed_validate_function_pointer");
        Assert.Contains(invoke.Program!.Instructions, static row =>
            row.Opcode == HybridCpuOpcode.JALR && row.Annotation.ControlFlowKind == IrControlFlowKind.Call);
        Assert.Contains(calli.Program!.Instructions, static row =>
            row.Opcode == HybridCpuOpcode.JALR && row.Annotation.ControlFlowKind == IrControlFlowKind.Call);
        Assert.Contains(passedCalli.Program!.Instructions, static row =>
            row.Opcode == HybridCpuOpcode.JALR && row.Annotation.ControlFlowKind == IrControlFlowKind.Call);
    }

    [Fact]
    public void CheckedInCil_MissingSignatureBindingRejectsWithoutArtifact()
    {
        RestrictedCilImportResultV1 result = new RestrictedCilImporterV1(
            mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportImage(FixtureImage,
            new(typeof(DelegateCorpus).FullName!, nameof(DelegateCorpus.CreateStatic)), "phase07-missing-binding");

        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, result.Status);
        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, static row => row.Code == "HCCIL1602");
    }

    [Fact]
    public void CheckedInCil_MismatchedMetadataAndDelegateSignaturesRejectWithoutArtifact()
    {
        int calliOffset = FindOpcode(nameof(DelegateCorpus.InvokePassedManagedPointer), 0x29);
        int calliToken = BitConverter.ToInt32(
            MethodBytes(nameof(DelegateCorpus.InvokePassedManagedPointer)), calliOffset + 1);
        RestrictedCilImportResultV1 calli = new RestrictedCilImporterV1(
            mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            calliBindings:
            [
                new(calliToken, OpenSignature,
                    [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32],
                    RestrictedCilTypeV1.Int32)
            ]).ImportImage(FixtureImage,
            new(typeof(DelegateCorpus).FullName!, nameof(DelegateCorpus.InvokePassedManagedPointer)),
            "phase07-mismatched-calli-signature");
        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, calli.Status);
        Assert.Null(calli.Program);
        Assert.Contains(calli.Diagnostics, static row => row.Code == "HCCIL1610");

        int plusOne = typeof(DelegateCorpus).GetMethod(nameof(DelegateCorpus.PlusOne))!.MetadataToken;
        int constructor = typeof(IntUnaryDelegate).GetConstructors().Single().MetadataToken;
        int newobjOffset = FindOpcode(nameof(DelegateCorpus.CreateStatic), 0x73);
        RestrictedCilImportResultV1 creation = new RestrictedCilImporterV1(
            mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            functionPointerBindings:
            [
                new(plusOne, StaticMethod, Signature, [RestrictedCilTypeV1.Int32],
                    RestrictedCilTypeV1.Int32, false)
            ],
            delegateCreationBindings:
            [
                new(constructor, newobjOffset, 0xd001, OpenSignature,
                    HybridCpuManagedDelegateKindV1.Static,
                    typeof(DelegateCorpus).GetMethod(nameof(DelegateCorpus.CreateStatic))!.MetadataToken)
            ]).ImportImage(FixtureImage,
            new(typeof(DelegateCorpus).FullName!, nameof(DelegateCorpus.CreateStatic)),
            "phase07-mismatched-delegate-signature");
        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, creation.Status);
        Assert.Null(creation.Program);
        Assert.Contains(creation.Diagnostics, static row => row.Code == "HCCIL1634");

        RestrictedCilImportResultV1 target = new RestrictedCilImporterV1(
            mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            functionPointerBindings:
            [
                new(plusOne, StaticMethod, OpenSignature,
                    [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32],
                    RestrictedCilTypeV1.Int32, true)
            ]).ImportImage(FixtureImage,
            new(typeof(DelegateCorpus).FullName!, nameof(DelegateCorpus.CreateStatic)),
            "phase07-mismatched-target-signature");
        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, target.Status);
        Assert.Null(target.Program);
        Assert.Contains(target.Diagnostics, static row => row.Code == "HCCIL1605");
    }

    [Fact]
    public void BodyWorld_FunctionPointerTargetIsReachableButNotAnExecutionEdge()
    {
        RestrictedCilImporterV1 importer = CreateImporter(nameof(DelegateCorpus.InvokeManagedPointer));
        ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, FixtureImage, "phase07-body-world",
            [new(typeof(DelegateCorpus).FullName!, nameof(DelegateCorpus.InvokeManagedPointer))], []));

        Assert.Equal(RestrictedCilImportStatusV1.Success, graph.Status);
        ManagedCallGraphEdgeV1 edge = Assert.Single(graph.Graph!.Edges, static row => row.IsFunctionPointerTarget);
        Assert.Contains(nameof(DelegateCorpus.PlusOne), edge.CalleeIdentity, StringComparison.Ordinal);
        Assert.False(edge.IsDispatchCandidate);
        Assert.Equal(1, graph.Graph.MaximumAcyclicDepth);
        Assert.Contains(graph.Methods, static row => row.Identity.MethodName == nameof(DelegateCorpus.PlusOne));
        Assert.DoesNotContain(graph.Methods.Single(static row =>
            row.Identity.MethodName == nameof(DelegateCorpus.InvokeManagedPointer)).DirectCalleeIdentities,
            identity => identity.Contains(nameof(DelegateCorpus.PlusOne), StringComparison.Ordinal));

        ManagedCallGraphCompilationV1 closed = CreateImporter(nameof(DelegateCorpus.CreateClosedIdentity)).ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule, FixtureImage, "phase07-closed-body-world",
            [new(typeof(DelegateCorpus).FullName!, nameof(DelegateCorpus.CreateClosedIdentity))], []));
        Assert.True(closed.Status == RestrictedCilImportStatusV1.Success,
            string.Join(';', closed.Diagnostics.Select(static row => $"{row.Code}:{row.Message}")));
        ManagedCallGraphEdgeV1 instanceEdge = Assert.Single(closed.Graph!.Edges,
            static row => row.IsFunctionPointerTarget);
        Assert.Contains(nameof(DelegateTarget.Identity), instanceEdge.CalleeIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public void HcfpLinkBootstrapAndIse_ExecuteResolverThunkAndOrdinaryJalrTarget()
    {
        const string entrySymbol = "phase07_delegate_entry";
        const string targetSymbol = "phase07_delegate_target";
        const string thunkSymbol = "phase07_delegate_thunk";
        const string resolverSymbol = "__hybridcpu_managed_resolve_delegate";
        HybridCpuManagedFunctionPointerMetadataEmitterV1 emitter = new HybridCpuManagedFunctionPointerMetadataEmitterV1();
        HybridCpuManagedFunctionPointerMetadataArtifactV1 metadata = emitter.Emit([
            new(StaticMethod, Signature, targetSymbol, false, thunkSymbol)
        ]);
        HybridCpuManagedFunctionPointerMetadataArtifactV1 repeated = emitter.Emit([
            new(StaticMethod, Signature, targetSymbol, false, thunkSymbol)
        ]);
        Assert.True(metadata.IsSuccess, metadata.Reason);
        Assert.Equal(metadata.Digest, repeated.Digest);
        Assert.Equal(metadata.Section!.Data, repeated.Section!.Data);
        Assert.Equal(metadata.Relocations, repeated.Relocations);
        Assert.False(metadata.HasLinkAuthority);
        Assert.False(metadata.HasIseExecutionAuthority);

        HybridCpuBundleSerializer serializer = new HybridCpuBundleSerializer();
        byte[] entryCode = serializer.SerializeProgram([
            Bundle(Addi(18, 10, 0)), Bundle(Call()), Bundle(Addi(5, 10, 0)),
            Bundle(Addi(10, 18, 0)), Bundle(IndirectCall())]);
        byte[] resolverCode = serializer.SerializeProgram([Bundle(Addi(10, 21, 0)), Bundle(Return())]);
        byte[] thunkCode = serializer.SerializeProgram([Bundle(Addi(5, 20, 0)), Bundle(IndirectCall())]);
        byte[] targetCode = serializer.SerializeProgram([Bundle(Addi(10, 10, 7)), Bundle(Return())]);
        HybridCpuObjectWriterV1 writer = new HybridCpuObjectWriterV1();
        byte[] CodeObject(string symbol, byte[] code, IReadOnlyList<HybridCpuObjectSymbolV1>? declarations = null,
            IReadOnlyList<HybridCpuObjectRelocationV1>? relocations = null)
        {
            HybridCpuObjectArtifactV1 artifact = writer.Write(new(
                [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes,
                    code, (ulong)code.Length)],
                [new(symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default,
                    ".text", 0, (ulong)code.Length, true), .. declarations ?? []], relocations ?? [],
                HybridCpuTargetPlatformContractV1.Default.ContractDigest,
                HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
            Assert.Equal(HybridCpuObjectStatusV1.Success, artifact.Status);
            return artifact.Bytes;
        }
        HybridCpuObjectArtifactV1 metadataObject = writer.Write(new([metadata.Section],
            [
                new(targetSymbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, null, 0, 0, false),
                new(thunkSymbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, null, 0, 0, false)
            ], metadata.Relocations, HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
        Assert.Equal(HybridCpuObjectStatusV1.Success, metadataObject.Status);
        HybridCpuObjectSymbolV1 resolverDeclaration = new HybridCpuObjectSymbolV1(resolverSymbol, HybridCpuSymbolBinding.Global,
            HybridCpuSymbolVisibility.Default, null, 0, 0, false);
        HybridCpuStaticLinkArtifactV1 link = new HybridCpuStaticLinkerV1().Link([
            new("a-entry", CodeObject(entrySymbol, entryCode, [resolverDeclaration],
                [new(".text", (ulong)HybridCpuBundleSerializer.BundleSizeBytes,
                    HybridCpuRelocationKind.ManagedCallRelativeSigned16, resolverSymbol, 0)])),
            new("b-target", CodeObject(targetSymbol, targetCode)),
            new("c-thunk", CodeObject(thunkSymbol, thunkCode)),
            new("d-resolver", CodeObject(resolverSymbol, resolverCode)),
            new("z-function-pointers", metadataObject.Bytes)
        ]);
        Assert.Equal(HybridCpuLinkStatusV1.Success, link.Status);
        Assert.Equal(2, link.AppliedRelocations.Count(static row => row.Kind == HybridCpuRelocationKind.Absolute64));
        HybridCpuLinkedSectionV1 linkedMetadata = link.Sections.Single(static row =>
            row.ModuleIdentity == "z-function-pointers" &&
            row.Name == HybridCpuManagedFunctionPointerMetadataEmitterV1.SectionName);
        int metadataOffset = checked((int)(linkedMetadata.Address - link.ImageBase));
        byte[] linkedBytes = link.ImageBytes.AsSpan(metadataOffset, checked((int)linkedMetadata.Size)).ToArray();
        Assert.Equal("HCFP0001"u8.ToArray(), linkedBytes.AsSpan(0, 8).ToArray());
        Assert.Equal(Address(targetSymbol), BinaryPrimitives.ReadUInt64LittleEndian(linkedBytes.AsSpan(32, 8)));
        Assert.Equal(Address(thunkSymbol), BinaryPrimitives.ReadUInt64LittleEndian(linkedBytes.AsSpan(40, 8)));

        Subject subject = CreateSubject(staticAddress: Address(targetSymbol), thunkAddress: Address(thunkSymbol));
        HybridCpuManagedDelegateResultV1 created = subject.Runtime.Create(new(subject.DelegateTypeHandle,
            Signature, HybridCpuManagedDelegateKindV1.Static, Address(targetSymbol)));
        Assert.True(created.IsSuccess, created.Reason);
        HybridCpuManagedDelegateResultV1 resolved = subject.Runtime.ResolveInvocation(created.DelegateReference, Signature);
        Assert.Equal(Address(thunkSymbol), resolved.InvocationAddress);
        Assert.Equal(Address(targetSymbol), resolved.CodeAddress);

        HybridCpuRuntimeHelperV1 resolverAbi = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(resolverSymbol)!;
        HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, entrySymbol, entrySymbol,
            [new(resolverSymbol, resolverAbi.Signature, true)]);
        HybridCpuRestrictedImageBuilderV1 imageBuilder = new HybridCpuRestrictedImageBuilderV1();
        HybridCpuRestrictedImageV1 image = imageBuilder.Inspect(imageBuilder.Build(
            new(link, entrySymbol, RuntimeBootstrap: descriptor)).PackageBytes);
        Assert.Equal(HybridCpuStartupStatusV1.Success, image.Status);
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, image.OptionsDigest,
            image.ImageBase, AlignUp((ulong)image.ImageBytes.Length, 4096), image.EntryAddress,
            HybridCpuRestrictedStartupOptionsV1.Production.StackBase,
            HybridCpuRestrictedStartupOptionsV1.Production.StackSize, 0)).IsSuccess);
        Assert.True(new HybridCpuManagedBootstrapRuntimeV1(HybridCpuManagedAbiFamilyV1.Default.ContractDigest)
            .Bootstrap(descriptor, kernel, new Dictionary<string, HybridCpuRuntimeHelperEntryV1>
            {
                [resolverSymbol] = static context => context.ContextCarrierAddress != 0
            }).IsSuccess);

        Processor.MultiBankMemoryArea memory = new Processor.MultiBankMemoryArea(4, 0x400000UL);
        Processor.CPU_Core core = new Processor.CPU_Core(0, CpuCorePlatformContext.CreateFixed(memory, ProcessorMode.Compiler));
        core.InitializePipeline();
        core.PrepareExecutionStart(image.EntryAddress);
        core.WriteCommittedArch(0, 10, 5);
        core.WriteCommittedArch(0, 20, resolved.CodeAddress);
        core.WriteCommittedArch(0, 21, resolved.InvocationAddress);
        Retire(core, image, image.EntryAddress);
        Retire(core, image, image.EntryAddress + BundleBytes);
        Retire(core, image, Address(resolverSymbol));
        Retire(core, image, Address(resolverSymbol) + BundleBytes);
        Retire(core, image, image.EntryAddress + 2 * BundleBytes);
        Retire(core, image, image.EntryAddress + 3 * BundleBytes);
        Retire(core, image, image.EntryAddress + 4 * BundleBytes);
        Assert.Equal(Address(thunkSymbol), core.ReadCommittedPc(0));
        Retire(core, image, Address(thunkSymbol));
        Retire(core, image, Address(thunkSymbol) + BundleBytes);
        Assert.Equal(Address(targetSymbol), core.ReadCommittedPc(0));
        Retire(core, image, Address(targetSymbol));
        Assert.Equal(12UL, core.ReadArch(0, 10));

        ulong Address(string name) => link.Symbols.Single(row => row.Name == name).Address;
    }

    [Fact]
    public void HcfpMetadata_DuplicateMalformedAndBudgetOverflowFailClosed()
    {
        HybridCpuManagedFunctionPointerMetadataEmitterV1 emitter = new HybridCpuManagedFunctionPointerMetadataEmitterV1();
        HybridCpuManagedFunctionPointerMetadataArtifactV1 duplicate = emitter.Emit([
            new(1, 2, "code-a", false, "thunk-a"),
            new(1, 2, "code-b", false, "thunk-b")
        ]);
        Assert.Equal(HybridCpuPlatformFactStatus.Invalid, duplicate.Status);
        Assert.Null(duplicate.Section);
        Assert.Empty(duplicate.Relocations);
        HybridCpuManagedFunctionPointerMetadataArtifactV1 multipleShapes = emitter.Emit([
            new(1, 2, "code-a", true, "thunk-a"),
            new(1, 3, "code-a", true, "thunk-b")
        ]);
        HybridCpuManagedFunctionPointerMetadataArtifactV1 reversedShapes = emitter.Emit([
            new(1, 3, "code-a", true, "thunk-b"),
            new(1, 2, "code-a", true, "thunk-a")
        ]);
        Assert.True(multipleShapes.IsSuccess, multipleShapes.Reason);
        Assert.Equal(multipleShapes.Digest, reversedShapes.Digest);
        Assert.Equal(multipleShapes.Section!.Data, reversedShapes.Section!.Data);
        Assert.Equal(HybridCpuPlatformFactStatus.Invalid,
            emitter.Emit([new(0, 2, "code", false, "thunk")]).Status);

        HybridCpuManagedFunctionPointerSymbolBindingV1[] overflow = Enumerable.Range(1,
                HybridCpuPlatformContractV1.MaximumManagedFunctionPointers + 1)
            .Select(static value => new HybridCpuManagedFunctionPointerSymbolBindingV1(
                (ulong)value, 1, $"code-{value:D5}", false, $"thunk-{value:D5}"))
            .ToArray();
        HybridCpuManagedFunctionPointerMetadataArtifactV1 exhausted = emitter.Emit(overflow);
        Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, exhausted.Status);
        Assert.Null(exhausted.Section);
        Assert.Empty(exhausted.Relocations);
    }

    [Fact]
    public void DelegateInvoke_FinalLoweredCallSitesCarryExactRootsAndStableMetadata()
    {
        RestrictedCilImportResultV1 import = ImportDelegate(nameof(DelegateCorpus.Invoke));
        Assert.Equal(RestrictedCilImportStatusV1.Success, import.Status);
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(import.Program!);
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(
            schedule, bundles, resourceModel: ResourceModel,
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        Assert.Equal(IrRegisterAllocationStatusV1.Allocated, allocation.Status);
        IrInstruction[] indirectCalls = allocation.FinalSchedule.Program.Instructions.Where(static row =>
            row.Opcode == HybridCpuOpcode.JALR && row.Annotation.ControlFlowKind == IrControlFlowKind.Call).ToArray();
        Assert.NotEmpty(indirectCalls);
        Assert.All(indirectCalls, static indirect => Assert.Contains(indirect.Operands, static operand =>
            operand.Kind == IrOperandKind.ArchitecturalRegister && operand.Value == 5));

        HybridCpuManagedMetadataFinalizerV1 finalizer = new HybridCpuManagedMetadataFinalizerV1();
        HybridCpuManagedMetadataArtifactV1 metadata = finalizer.FinalizeRequiredCallSites(
            "Phase07.DelegateInvoke", 0, allocation, HybridCpuManagedMetadataOptionsV1.Qualification);
        HybridCpuManagedMetadataArtifactV1 repeated = finalizer.FinalizeRequiredCallSites(
            "Phase07.DelegateInvoke", 0, allocation, HybridCpuManagedMetadataOptionsV1.Qualification);
        Assert.Equal(HybridCpuManagedMetadataStatusV1.Finalized, metadata.Status);
        int requiredSafepoints = allocation.OriginalSchedule!.Program.Instructions.Count(static row =>
            row.Annotation.IsManagedGcSafepoint &&
            (row.Annotation.ControlFlowKind == IrControlFlowKind.Call ||
             row.SideEffects.ArchitecturalEffects.HasFlag(IrArchitecturalEffectKind.Call)));
        Assert.Equal(requiredSafepoints, metadata.Safepoints.Count);
        Assert.True(requiredSafepoints >= 2);
        Assert.All(metadata.Safepoints, static point => Assert.Contains(point.LiveReferences,
            static root => root.ReferenceKind == HybridCpuGcReferenceKindV1.ObjectReference));
        Assert.Equal(metadata.ResultDigest, repeated.ResultDigest);
        Assert.Equal(metadata.GcInfo, repeated.GcInfo);

        RestrictedCilImportResultV1 calliImport = ImportDelegate(nameof(DelegateCorpus.InvokeManagedPointer));
        Assert.Equal(RestrictedCilImportStatusV1.Success, calliImport.Status);
        IrProgramSchedule calliSchedule = new HybridCpuLocalListScheduler().ScheduleProgram(calliImport.Program!);
        IrProgramBundlingResult calliBundles = new HybridCpuBundleFormer().BundleProgram(calliSchedule);
        IrRegisterAllocationResultV1 calliAllocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(
            calliSchedule, calliBundles, resourceModel: ResourceModel,
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        Assert.Equal(IrRegisterAllocationStatusV1.Allocated, calliAllocation.Status);
        IrInstruction[] finalCalliInstructions = calliAllocation.FinalSchedule.Program.Instructions.ToArray();
        Assert.StartsWith("phase20:prologue", finalCalliInstructions[0].StableIdentity, StringComparison.Ordinal);
        Assert.StartsWith("phase20:epilogue", finalCalliInstructions[^2].StableIdentity, StringComparison.Ordinal);
        Assert.Contains(calliAllocation.FinalSchedule.Program.Instructions, static row =>
            row.Opcode == HybridCpuOpcode.JALR && row.Annotation.ControlFlowKind == IrControlFlowKind.Call &&
            row.Operands.Any(static operand => operand.Kind == IrOperandKind.ArchitecturalRegister && operand.Value == 5));
        HybridCpuManagedMetadataArtifactV1 calliMetadata = finalizer.FinalizeRequiredCallSites(
            "Phase07.ManagedCalli", 0, calliAllocation, HybridCpuManagedMetadataOptionsV1.Qualification);
        Assert.Equal(HybridCpuManagedMetadataStatusV1.Finalized, calliMetadata.Status);
        int requiredCalliSafepoints = calliAllocation.OriginalSchedule!.Program.Instructions.Count(static row =>
            row.Annotation.IsManagedGcSafepoint &&
            (row.Annotation.ControlFlowKind == IrControlFlowKind.Call ||
             row.SideEffects.ArchitecturalEffects.HasFlag(IrArchitecturalEffectKind.Call)));
        Assert.Equal(requiredCalliSafepoints, calliMetadata.Safepoints.Count);
    }

    private static void AssertImport(RestrictedCilImportResultV1 import, params string[] helpers)
    {
        Assert.True(import.Status == RestrictedCilImportStatusV1.Success,
            string.Join(';', import.Diagnostics.Select(static row => $"{row.Code}:{row.Message}")));
        foreach (string helper in helpers)
            Assert.Contains(import.Program!.Instructions, row =>
                row.Annotation.ControlFlowKind == IrControlFlowKind.Call &&
                row.Annotation.BranchTargetSymbolName == helper);
    }

    private static RestrictedCilImportResultV1 ImportDelegate(string method) =>
        CreateImporter(method).ImportImage(FixtureImage,
            new(typeof(DelegateCorpus).FullName!, method), $"phase07-{method}");

    private static RestrictedCilImporterV1 CreateImporter(string method)
    {
        int plusOne = typeof(DelegateCorpus).GetMethod(nameof(DelegateCorpus.PlusOne))!.MetadataToken;
        int add = typeof(DelegateTarget).GetMethod(nameof(DelegateTarget.Add))!.MetadataToken;
        int identity = typeof(DelegateTarget).GetMethod(nameof(DelegateTarget.Identity))!.MetadataToken;
        int constructor = typeof(IntUnaryDelegate).GetConstructors().Single().MetadataToken;
        int invoke = typeof(IntUnaryDelegate).GetMethod(nameof(IntUnaryDelegate.Invoke))!.MetadataToken;
        int staticMethod = typeof(DelegateCorpus).GetMethod(nameof(DelegateCorpus.CreateStatic))!.MetadataToken;
        int closedMethod = typeof(DelegateCorpus).GetMethod(nameof(DelegateCorpus.CreateClosed))!.MetadataToken;
        int closedIdentityMethod = typeof(DelegateCorpus).GetMethod(nameof(DelegateCorpus.CreateClosedIdentity))!.MetadataToken;
        int staticNewobjOffset = FindOpcode(nameof(DelegateCorpus.CreateStatic), 0x73);
        int closedNewobjOffset = FindOpcode(nameof(DelegateCorpus.CreateClosed), 0x73);
        int closedIdentityNewobjOffset = FindOpcode(nameof(DelegateCorpus.CreateClosedIdentity), 0x73);
        bool methodHasCalli = method is nameof(DelegateCorpus.InvokeManagedPointer) or
            nameof(DelegateCorpus.InvokePassedManagedPointer);
        string calliMethod = methodHasCalli ? method : nameof(DelegateCorpus.InvokeManagedPointer);
        int calliOffset = FindOpcode(calliMethod, 0x29);
        int calliToken = BitConverter.ToInt32(MethodBytes(calliMethod), calliOffset + 1);
        return new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            functionPointerBindings:
            [
                new(plusOne, StaticMethod, Signature, [RestrictedCilTypeV1.Int32], RestrictedCilTypeV1.Int32, false),
                new(add, InstanceMethod, Signature,
                    [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32], RestrictedCilTypeV1.Int32,
                    true, add, [RestrictedCilTypeV1.Int32]),
                new(identity, InstanceMethod + 1, Signature,
                    [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32], RestrictedCilTypeV1.Int32,
                    true, identity, [RestrictedCilTypeV1.Int32])
            ],
            calliBindings: [new(calliToken, Signature, [RestrictedCilTypeV1.Int32], RestrictedCilTypeV1.Int32)],
            delegateCreationBindings:
            [
                new(constructor, staticNewobjOffset, 0xd001, Signature, HybridCpuManagedDelegateKindV1.Static,
                    staticMethod),
                new(constructor, closedNewobjOffset, 0xd001, Signature,
                    HybridCpuManagedDelegateKindV1.ClosedInstance, closedMethod),
                new(constructor, closedIdentityNewobjOffset, 0xd001, Signature,
                    HybridCpuManagedDelegateKindV1.ClosedInstance, closedIdentityMethod)
            ],
            delegateInvokeBindings:
            [new(invoke, Signature, [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32],
                RestrictedCilTypeV1.Int32)],
            dispatchBindings:
            [
                new(add, "DelegateTarget.Add",
                    [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32], RestrictedCilTypeV1.Int32,
                    RestrictedCilDispatchKindV1.Virtual, 0x7001, CandidateMethodMetadataTokens: [add]),
                new(identity, "DelegateTarget.Identity",
                    [RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.Int32], RestrictedCilTypeV1.Int32,
                    RestrictedCilDispatchKindV1.Virtual, 0x7002, CandidateMethodMetadataTokens: [identity])
            ]);
    }

    private static int FindOpcode(string method, byte opcode) => Array.IndexOf(MethodBytes(method), opcode);

    private static byte[] MethodBytes(string method) => typeof(DelegateCorpus).GetMethod(method)!
        .GetMethodBody()!.GetILAsByteArray()!;

    private static (ulong Code, ulong Target, HybridCpuManagedDelegateKindV1 Kind) Invocation(
        Subject subject, HybridCpuManagedDelegateResultV1 value)
    {
        HybridCpuManagedDelegateResultV1 result = subject.Runtime.ResolveInvocation(value.DelegateReference, Signature);
        Assert.True(result.IsSuccess, result.Reason);
        return (result.CodeAddress, result.TargetObject, result.Kind);
    }

    private static Subject CreateSubject(bool reverseMethods = false, ulong staticAddress = StaticAddress,
        ulong instanceAddress = InstanceAddress, ulong thunkAddress = ThunkAddress,
        bool malformedDelegateLayout = false)
    {
        HybridCpuManagedFieldDeclarationV1[] delegateFields =
        [
            new("target", HybridCpuManagedStorageKindV1.ObjectReference, 8, 8, false, 0),
            new("code", HybridCpuManagedStorageKindV1.Primitive, 8, 8, false, 1),
            new("context", HybridCpuManagedStorageKindV1.Primitive, 8, 8, false, 2),
            new("signature", HybridCpuManagedStorageKindV1.Primitive, 8, 8, false, 3),
            new("kind", HybridCpuManagedStorageKindV1.Primitive, 8, 8, false, 4),
            new("thunk", HybridCpuManagedStorageKindV1.Primitive, 8, 8, false, 5)
        ];
        if (malformedDelegateLayout)
            delegateFields = [.. delegateFields,
                new("extra", HybridCpuManagedStorageKindV1.Primitive, 8, 8, false, 6)];
        HybridCpuManagedTypeSystemBuildV1 built = new HybridCpuManagedTypeSystemBuilderV1().Build([
            new("Phase07.Target", HybridCpuManagedTypeKindV1.Class, null, [], []),
            new("Phase07.Delegate", HybridCpuManagedTypeKindV1.Class, null, [], delegateFields)
        ]);
        Assert.True(built.IsSuccess, built.Reason);
        HybridCpuManagedTypeSystemV1 types = built.TypeSystem!;
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('d', 64),
            0x100000, 4096, 0x100000, 0x200000, 4096, 0)).IsSuccess);
        HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types,
            HybridCpuManagedHeapOptionsV1.Create(0x41000000, 4096, 4096, -3));
        Assert.True(heap.Initialize().IsSuccess);
        HybridCpuManagedTypeDescriptorV1 targetType = types.Descriptors.Single(row => row.StableIdentity == "Phase07.Target");
        HybridCpuManagedTypeDescriptorV1 delegateType = types.Descriptors.Single(row => row.StableIdentity == "Phase07.Delegate");
        ulong target = heap.Allocate(types.TypeHandle(targetType.TypeId)!.Value).ObjectReference;
        HybridCpuManagedFunctionPointerEntryV1[] methods =
        [
            new(StaticMethod, Signature, staticAddress, false, thunkAddress),
            new(InstanceMethod, Signature, instanceAddress, true, thunkAddress),
            new(InstanceMethod, OpenSignature, instanceAddress, true, thunkAddress)
        ];
        if (reverseMethods) Array.Reverse(methods);
        return new(types, heap, new(types, heap, methods), target, types.TypeHandle(delegateType.TypeId)!.Value);
    }

    private sealed record Subject(HybridCpuManagedTypeSystemV1 Types, HybridCpuManagedHeapAllocatorV1 Heap,
        HybridCpuManagedDelegateRuntimeV1 Runtime, ulong Target, ulong DelegateTypeHandle);

    private static ulong BundleBytes => (ulong)HybridCpuBundleSerializer.BundleSizeBytes;

    private static void Retire(Processor.CPU_Core core, HybridCpuRestrictedImageV1 image, ulong address) =>
        CompilerRefPlan7Phase03ManagedHeapAllocatorTests.RetireBundle(core,
            CompilerRefPlan7Phase03ManagedHeapAllocatorTests.ReadBundle(image, address), address);

    private static ulong AlignUp(ulong value, ulong alignment) => checked((value + alignment - 1) / alignment * alignment);

    private static HybridCpuInstructionBundle Bundle(params HybridCpuInstructionWord[] instructions)
    {
        HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
        for (int index = 0; index < instructions.Length; index++) bundle.SetInstruction(index, instructions[index]);
        return bundle;
    }

    private static HybridCpuInstructionWord Addi(byte destination, byte source, ushort immediate) => new()
    {
        OpCode = (uint)HybridCpuOpcode.ADDI, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
        Word1 = HybridCpuInstructionWord.PackArchRegs(destination, source, HybridCpuInstructionWord.NoArchReg),
        Immediate = immediate
    };

    private static HybridCpuInstructionWord IndirectCall() => new()
    {
        OpCode = (uint)HybridCpuOpcode.JALR, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
        Word1 = HybridCpuInstructionWord.PackArchRegs((byte)HybridCpuNativeAbiContractV2.ReturnAddressRegister, 5,
            HybridCpuInstructionWord.NoArchReg)
    };

    private static HybridCpuInstructionWord Call() => new()
    {
        OpCode = (uint)HybridCpuOpcode.JAL, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
        Word1 = HybridCpuInstructionWord.PackArchRegs((byte)HybridCpuNativeAbiContractV2.ReturnAddressRegister,
            HybridCpuInstructionWord.NoArchReg, HybridCpuInstructionWord.NoArchReg)
    };

    private static HybridCpuInstructionWord Return() => new()
    {
        OpCode = (uint)HybridCpuOpcode.JALR, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
        Word1 = HybridCpuInstructionWord.PackArchRegs(0, (byte)HybridCpuNativeAbiContractV2.ReturnAddressRegister,
            HybridCpuInstructionWord.NoArchReg), Immediate = HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes
    };
}
