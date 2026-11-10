using HybridCPU_ISE.CloseToHSL.Core.Runtime.Managed;
using System.Buffers.Binary;
using System.Reflection;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase04ManagedCoreShapesTests
{
    [Fact]
    public void SzArray_PrimitiveRoundTripLengthBoundsAndOverflowAreRuntimeOwned()
    {
        HybridCpuManagedTypeSystemV1 types = Build(IntArray());
        HybridCpuManagedTypeDescriptorV1 descriptor = Assert.Single(types.Descriptors);
        HybridCpuManagedHeapAllocatorV1 heap = Heap(types); Assert.True(heap.Initialize().IsSuccess);
        HybridCpuManagedArrayRuntimeV1 arrays = new HybridCpuManagedArrayRuntimeV1(types, heap);

        HybridCpuManagedShapeResultV1 created = arrays.NewArray(types.TypeHandle(descriptor.TypeId)!.Value, 3);
        Assert.True(created.IsSuccess, created.Reason);
        Assert.Equal(3, arrays.Length(created.ObjectReference).ScalarValue);
        Assert.True(arrays.StoreInt32(created.ObjectReference, 1, -17).IsSuccess);
        Assert.Equal(-17, arrays.LoadInt32(created.ObjectReference, 1).ScalarValue);
        Assert.Equal(HybridCpuManagedShapeStatusV1.BoundsViolation, arrays.LoadInt32(created.ObjectReference, 3).Status);
        Assert.Equal(HybridCpuManagedShapeStatusV1.NegativeLength,
            arrays.NewArray(types.TypeHandle(descriptor.TypeId)!.Value, -1).Status);
        Assert.Equal(HybridCpuManagedShapeStatusV1.SizeOverflow,
            arrays.NewArray(types.TypeHandle(descriptor.TypeId)!.Value, int.MaxValue).Status);
    }

    [Fact]
    public void SzArray_ReferenceStoreChecksExactRuntimeAssignability()
    {
        HybridCpuManagedTypeSystemV1 types = Build(
            Class("Base"), Class("Derived", "Base"), Class("Other"), RefArray("Base[]", "Base"));
        HybridCpuManagedHeapAllocatorV1 heap = Heap(types); Assert.True(heap.Initialize().IsSuccess);
        HybridCpuManagedArrayRuntimeV1 arrays = new HybridCpuManagedArrayRuntimeV1(types, heap);
        ulong New(string identity) => heap.Allocate(types.TypeHandle(Type(types, identity).TypeId)!.Value).ObjectReference;
        HybridCpuManagedTypeDescriptorV1 arrayType = Type(types, "Base[]");
        ulong array = arrays.NewArray(types.TypeHandle(arrayType.TypeId)!.Value, 2).ObjectReference;

        Assert.True(arrays.StoreReference(array, 0, New("Derived")).IsSuccess);
        Assert.True(arrays.StoreReference(array, 1, 0).IsSuccess);
        Assert.Equal(HybridCpuManagedShapeStatusV1.ArrayTypeMismatch,
            arrays.StoreReference(array, 1, New("Other")).Status);
    }

    [Fact]
    public void Strings_AreUtf16ImmutableAndLiteralMaterializationIsDeterministic()
    {
        HybridCpuManagedTypeSystemV1 firstTypes = Build(StringType());
        HybridCpuManagedTypeSystemV1 secondTypes = Build(StringType());
        HybridCpuManagedHeapAllocatorV1 firstHeap = Heap(firstTypes); HybridCpuManagedHeapAllocatorV1 secondHeap = Heap(secondTypes);
        Assert.True(firstHeap.Initialize().IsSuccess); Assert.True(secondHeap.Initialize().IsSuccess);
        HybridCpuManagedStringRuntimeV1 first = new HybridCpuManagedStringRuntimeV1(firstTypes, firstHeap);
        HybridCpuManagedStringRuntimeV1 second = new HybridCpuManagedStringRuntimeV1(secondTypes, secondHeap);
        ulong firstHandle = firstTypes.TypeHandle(Assert.Single(firstTypes.Descriptors).TypeId)!.Value;
        ulong secondHandle = secondTypes.TypeHandle(Assert.Single(secondTypes.Descriptors).TypeId)!.Value;

        HybridCpuManagedShapeResultV1 a = first.MaterializeLiteral(firstHandle, "A\U0001F642Z");
        HybridCpuManagedShapeResultV1 b = second.MaterializeLiteral(secondHandle, "A\U0001F642Z");
        Assert.Equal(4, first.Length(a.ObjectReference).ScalarValue);
        Assert.Equal(0xD83D, first.Character(a.ObjectReference, 1).ScalarValue);
        Assert.Equal(a.ObjectReference, first.MaterializeLiteral(firstHandle, "A\U0001F642Z").ObjectReference);
        Assert.Equal(a.ObjectReference, b.ObjectReference);
        Assert.Equal(first.Literals, second.Literals);
        Assert.Equal(firstHeap.ReadObjectBytes(a.ObjectReference), secondHeap.ReadObjectBytes(b.ObjectReference));
        Assert.True(first.RegisterLiteral(7, firstHandle, "A\U0001F642Z").IsSuccess);
        Assert.Equal(HybridCpuManagedShapeStatusV1.InvalidType,
            first.RegisterLiteral(7, firstHandle, "different").Status);
    }

    [Fact]
    public void BlittableValueType_BoxesRoundTripsAndReferenceBearingShapeFailsClosed()
    {
        HybridCpuManagedTypeSystemV1 types = Build(Value("Pair", []), Value("WithRef", [0]));
        HybridCpuManagedHeapAllocatorV1 heap = Heap(types); Assert.True(heap.Initialize().IsSuccess);
        HybridCpuManagedValueTypeRuntimeV1 values = new HybridCpuManagedValueTypeRuntimeV1(types, heap);
        byte[] payload = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(payload, 11); BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), -9);
        HybridCpuManagedTypeDescriptorV1 pair = Type(types, "Pair");
        HybridCpuManagedShapeResultV1 boxed = values.Box(types.TypeHandle(pair.TypeId)!.Value, payload);

        Assert.True(boxed.IsSuccess, boxed.Reason);
        Assert.Equal(payload, values.Unbox(boxed.ObjectReference, pair.TypeId));
        HybridCpuManagedTypeDescriptorV1 withRef = Type(types, "WithRef");
        Assert.Equal(HybridCpuManagedShapeStatusV1.UnsupportedShape,
            values.Box(types.TypeHandle(withRef.TypeId)!.Value, payload).Status);
        Assert.Equal([0], withRef.ValueTypeShape!.ObjectReferenceOffsets);
    }

    [Fact]
    public void TypeInitialization_IsExactlyOnceRecursiveAndStickyOnFailure()
    {
        HybridCpuManagedTypeSystemV1 types = Build(Class("Good"), Class("Bad"));
        HybridCpuManagedTypeInitializerRuntimeV1 runtime = new HybridCpuManagedTypeInitializerRuntimeV1(types);
        ulong good = Type(types, "Good").TypeId; ulong bad = Type(types, "Bad").TypeId;
        int count = 0;
        HybridCpuManagedShapeResultV1 outer = runtime.EnsureInitialized(good, () =>
        {
            count++;
            Assert.True(runtime.EnsureInitialized(good, () => throw new InvalidOperationException()).IsSuccess);
            return true;
        });
        Assert.True(outer.IsSuccess); Assert.True(runtime.EnsureInitialized(good, () => { count++; return true; }).IsSuccess);
        Assert.Equal(1, count); Assert.Equal(HybridCpuManagedTypeInitializationStateV1.Initialized, types.InitializationState(good));
        Assert.Equal(HybridCpuManagedShapeStatusV1.InitializationFailed, runtime.EnsureInitialized(bad, () => false).Status);
        Assert.Equal(HybridCpuManagedShapeStatusV1.InitializationFailed, runtime.EnsureInitialized(bad, () => true).Status);
        Assert.Equal(HybridCpuManagedTypeInitializationStateV1.Failed, types.InitializationState(bad));
    }

    [Fact]
    public void StaticFields_UseExactRuntimeLayoutAndRejectKindOrOffsetMismatch()
    {
        HybridCpuManagedTypeSystemV1 types = Build(new HybridCpuManagedTypeDeclarationV1("Statics", HybridCpuManagedTypeKindV1.Class, null, [],
            [new("Count", HybridCpuManagedStorageKindV1.Primitive, 4, 4, true, 0),
             new("Root", HybridCpuManagedStorageKindV1.ObjectReference, 8, 8, true, 1)]));
        HybridCpuManagedTypeDescriptorV1 type = Assert.Single(types.Descriptors);
        ulong handle = types.TypeHandle(type.TypeId)!.Value;
        HybridCpuManagedStaticFieldRuntimeV1 fields = new HybridCpuManagedStaticFieldRuntimeV1(types);
        int countOffset = type.StaticLayout.Fields.Single(field => field.Identity == "Count").OffsetBytes;
        int rootOffset = type.StaticLayout.Fields.Single(field => field.Identity == "Root").OffsetBytes;

        Assert.True(fields.StoreInt32(handle, countOffset, -91).IsSuccess);
        Assert.Equal(-91, fields.LoadInt32(handle, countOffset).ScalarValue);
        Assert.True(fields.StoreReference(handle, rootOffset, 0x1234).IsSuccess);
        Assert.Equal(0x1234, fields.LoadReference(handle, rootOffset).ScalarValue);
        Assert.Equal(HybridCpuManagedShapeStatusV1.InvalidType, fields.LoadReference(handle, countOffset).Status);
        Assert.Equal(HybridCpuManagedShapeStatusV1.InvalidType, fields.LoadInt32(handle, 999).Status);
        Assert.Equal([rootOffset], type.StaticLayout.ObjectReferenceOffsets);
    }

    [Fact]
    public void Bootstrap_ExecutesOrderedTypeInitializersAndRejectsMissingInitializer()
    {
        HybridCpuManagedTypeSystemV1 types = Build(Class("A"), Class("B"));
        HybridCpuManagedTypeDescriptorV1 a = Type(types, "A"), b = Type(types, "B");
        HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, "entry", "entry", moduleInitializers:
            [new("m", "b_init", 1, b.TypeId), new("m", "a_init", 0, a.TypeId)]);
        DeterministicRuntimeKernelV1 kernel = BootKernel();
        List<string> order = new List<string>();
        HybridCpuManagedBootstrapRuntimeV1 runtime = new HybridCpuManagedBootstrapRuntimeV1(HybridCpuManagedAbiFamilyV1.Default.ContractDigest);
        HybridCpuManagedBootstrapResultV1 result = runtime.Bootstrap(descriptor, kernel,
            new Dictionary<string, HybridCpuRuntimeHelperEntryV1>(), types,
            new Dictionary<string, Func<bool>> { ["a_init"] = () => { order.Add("a"); return true; }, ["b_init"] = () => { order.Add("b"); return true; } });

        Assert.True(result.IsSuccess, result.Reason); Assert.Equal(["a", "b"], order);
        Assert.Equal(HybridCpuManagedBootstrapStatusV1.MissingHelper,
            runtime.Bootstrap(descriptor, kernel, new Dictionary<string, HybridCpuRuntimeHelperEntryV1>(), types).Status);
        Assert.Equal(HybridCpuManagedBootstrapStatusV1.MissingHelper,
            runtime.Bootstrap(descriptor, kernel, new Dictionary<string, HybridCpuRuntimeHelperEntryV1>(), types,
                new Dictionary<string, Func<bool>>()).Status);
    }

    [Fact]
    public void Bootstrap_RegistersCanonicalStringLiteralsAsStaticRootsAndRejectsMissingRuntime()
    {
        HybridCpuManagedTypeSystemV1 types = Build(StringType());
        HybridCpuManagedTypeDescriptorV1 text = Assert.Single(types.Descriptors);
        HybridCpuManagedHeapAllocatorV1 heap = Heap(types); Assert.True(heap.Initialize().IsSuccess);
        HybridCpuManagedStringRuntimeV1 strings = new HybridCpuManagedStringRuntimeV1(types, heap);
        HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, "entry", "entry",
            managedTypes: [new(text.TypeId, text.StableIdentity, text.DescriptorDigest, 0, 1, "literal:phase04")],
            stringLiterals: [new("literal:phase04", 7, text.TypeId, "phase04-\U0001F642")]);
        HybridCpuManagedBootstrapRuntimeV1 runtime = new HybridCpuManagedBootstrapRuntimeV1(HybridCpuManagedAbiFamilyV1.Default.ContractDigest);
        DeterministicRuntimeKernelV1 kernel = BootKernel();

        HybridCpuManagedBootstrapResultV1 result = runtime.Bootstrap(descriptor, kernel,
            new Dictionary<string, HybridCpuRuntimeHelperEntryV1>(), types, stringRuntime: strings);

        Assert.True(result.IsSuccess, result.Reason);
        Assert.Contains("literal:phase04", result.RegisteredStaticRoots);
        ulong reference = Assert.IsType<ulong>(strings.ResolveLiteral(7));
        Assert.Equal(10, strings.Length(reference).ScalarValue);
        Assert.Equal(0xD83D, strings.Character(reference, 8).ScalarValue);
        Assert.Equal(HybridCpuManagedBootstrapStatusV1.HelperFailure,
            runtime.Bootstrap(descriptor, kernel, new Dictionary<string, HybridCpuRuntimeHelperEntryV1>(), types).Status);
    }

    [Fact]
    public void ManagedAbiV16_PreservesCoreShapesButKeepsInteriorReferencesAndBarriersUnsupported()
    {
        HybridCpuManagedAbiFamilyV1 abi = HybridCpuManagedAbiFamilyV1.Default;
        Assert.Equal(63, HybridCpuManagedAbiFamilyV1.SchemaMinor);
        Assert.All(new[] { "__hybridcpu_managed_newarr", "__hybridcpu_managed_array_length", "__hybridcpu_managed_ldstr", "__hybridcpu_managed_box_i4", "__hybridcpu_managed_box_i8", "__hybridcpu_managed_ensure_type_initialized" },
            symbol => Assert.Equal(HybridCpuManagedAbiSupportV1.Supported, abi.ResolveRuntimeHelper(symbol)!.Support));
        Assert.Equal(HybridCpuManagedAbiSupportV1.Unsupported, abi.ResolveRuntimeHelper("__hybridcpu_managed_write_barrier")!.Support);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Unsupported, abi.Features.Single(feature => feature.Identity == "managed.byref").Support);
    }

    [Fact]
    public void Cil_SzArrayAndStringOpcodesLowerToVersionedRuntimeHelpers()
    {
        byte[] pe = File.ReadAllBytes(typeof(Phase04CilShapesFixture).Assembly.Location);
        string typeName = typeof(Phase04CilShapesFixture).FullName!;
        MethodInfo make = typeof(Phase04CilShapesFixture).GetMethod(nameof(Phase04CilShapesFixture.MakeIntArray))!;
        MethodInfo load = typeof(Phase04CilShapesFixture).GetMethod(nameof(Phase04CilShapesFixture.LoadInt))!;
        MethodInfo store = typeof(Phase04CilShapesFixture).GetMethod(nameof(Phase04CilShapesFixture.StoreInt))!;
        MethodInfo length = typeof(Phase04CilShapesFixture).GetMethod(nameof(Phase04CilShapesFixture.Length))!;
        MethodInfo literal = typeof(Phase04CilShapesFixture).GetMethod(nameof(Phase04CilShapesFixture.Literal))!;
        MethodInfo stringLength = typeof(Phase04CilShapesFixture).GetMethod(nameof(Phase04CilShapesFixture.StringLength))!;
        MethodInfo stringChar = typeof(Phase04CilShapesFixture).GetMethod(nameof(Phase04CilShapesFixture.StringCharacter))!;
        MethodInfo makeRefs = typeof(Phase04CilShapesFixture).GetMethod(nameof(Phase04CilShapesFixture.MakeObjectArray))!;
        MethodInfo loadRef = typeof(Phase04CilShapesFixture).GetMethod(nameof(Phase04CilShapesFixture.LoadRef))!;
        MethodInfo storeRef = typeof(Phase04CilShapesFixture).GetMethod(nameof(Phase04CilShapesFixture.StoreRef))!;
        HybridCpuManagedTypeSystemV1 types = Build(IntArray(), StringType(), Class("System.Object"), RefArray("System.Object[]", "System.Object"));
        HybridCpuManagedTypeDescriptorV1 array = Type(types, "System.Int32[]"), refs = Type(types, "System.Object[]"), text = Type(types, "System.String");
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            arrayBindings: [new(TokenAfter(make, 0x8d), array, types.TypeHandle(array.TypeId)!.Value),
                new(TokenAfter(makeRefs, 0x8d), refs, types.TypeHandle(refs.TypeId)!.Value)],
            stringBindings: [new("phase04-\U0001F642", 7, text, types.TypeHandle(text.TypeId)!.Value)]);

        RestrictedCilImportResultV1 makeResult = Import(importer, pe, typeName, make);
        RestrictedCilImportResultV1 loadResult = Import(importer, pe, typeName, load);
        RestrictedCilImportResultV1 storeResult = Import(importer, pe, typeName, store);
        RestrictedCilImportResultV1 lengthResult = Import(importer, pe, typeName, length);
        RestrictedCilImportResultV1 literalResult = Import(importer, pe, typeName, literal);

        AssertCalls(makeResult, "__hybridcpu_managed_newarr");
        AssertCalls(loadResult, "__hybridcpu_managed_array_load_i4");
        AssertCalls(storeResult, "__hybridcpu_managed_array_store_i4");
        AssertCalls(lengthResult, "__hybridcpu_managed_array_length");
        AssertCalls(literalResult, "__hybridcpu_managed_ldstr");
        AssertCalls(Import(importer, pe, typeName, stringLength), "__hybridcpu_managed_string_length");
        AssertCalls(Import(importer, pe, typeName, stringChar), "__hybridcpu_managed_string_char");
        AssertCalls(Import(importer, pe, typeName, makeRefs), "__hybridcpu_managed_newarr");
        AssertCalls(Import(importer, pe, typeName, loadRef), "__hybridcpu_managed_array_load_ref");
        AssertCalls(Import(importer, pe, typeName, storeRef), "__hybridcpu_managed_array_store_ref");
        string literalResultIdentity = literalResult.Program!.Instructions.Single(instruction =>
                instruction.StableIdentity.EndsWith(":ldstr:result-copy", StringComparison.Ordinal))
            .Annotation.Defs.Single(operand => operand.Kind == IrOperandKind.VirtualValue).Name!;
        Assert.Equal(IrCanonicalValueKind.ManagedObjectReference,
            literalResult.Program.ValueFlow.Values.Single(value => value.StableId == literalResultIdentity).ValueKind.Kind);

        ManagedTypeUniversePlanV1 arrayUniverse = new ManagedTypeUniversePlanV1(
            [new(types.TypeHandle(array.TypeId)!.Value, array.TypeId, array.BaseTypeId ?? 0, array.StableIdentity, array)],
            new string('a', 64));
        ManagedCallGraphCompilationV1 arrayCompilation = new ManagedCallGraphCompilationV1(RestrictedCilImportStatusV1.Success,
            [new(new("test", "test", "test", make.MetadataToken, typeName, make.Name, "test", "test", new string('b', 64)),
                makeResult, [])], null, [], TypeUniverse: arrayUniverse,
            ArrayTypeUses: [new("test", Array.IndexOf(make.GetMethodBody()!.GetILAsByteArray()!, (byte)0x8d),
                array.TypeId, types.TypeHandle(array.TypeId)!.Value)]);
        ManagedDispatchTypeObjectArtifactV1 arrayMetadata = ManagedDispatchTypeObjectV1.Emit(arrayCompilation);
        Assert.Equal(array.TypeId, Assert.Single(arrayMetadata.Rows).Descriptor.TypeId);
        Assert.NotNull(Assert.Single(arrayMetadata.Rows).Descriptor.ArrayShape);
        HybridCpuStaticLinkArtifactV1 arrayMetadataLink = new HybridCpuStaticLinkerV1().Link(
            [new(ManagedDispatchTypeObjectV1.ModuleIdentity, arrayMetadata.ObjectArtifact.Bytes)]);
        HybridCpuManagedTypeRegistrationV1[] arrayRegistrations = ManagedDispatchTypeObjectV1.Registrations(arrayMetadata, arrayMetadataLink);
        Assert.True(HybridCpuManagedImageTypeLoaderV1.Load(arrayMetadataLink.ImageBytes, arrayRegistrations,
            new Dictionary<ulong, ulong> { [array.TypeId] = types.TypeHandle(array.TypeId)!.Value }).IsSuccess);
    }

    [Fact]
    public void Cil_BoxUnboxAndStaticInitializationLowerWithExactBindings()
    {
        byte[] pe = File.ReadAllBytes(typeof(Phase04CilShapesFixture).Assembly.Location);
        MethodInfo box = typeof(Phase04CilShapesFixture).GetMethod(nameof(Phase04CilShapesFixture.BoxInt))!;
        MethodInfo unbox = typeof(Phase04CilShapesFixture).GetMethod(nameof(Phase04CilShapesFixture.UnboxInt))!;
        HybridCpuManagedTypeSystemV1 valueTypes = Build(Value("System.Int32", [], 4));
        HybridCpuManagedTypeDescriptorV1 intType = Assert.Single(valueTypes.Descriptors);
        RestrictedCilImporterV1 valueImporter = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            valueTypeBindings:
            [new(TokenAfter(box, 0x8c), RestrictedCilTypeV1.Int32, intType, valueTypes.TypeHandle(intType.TypeId)!.Value)]);
        AssertCalls(Import(valueImporter, pe, typeof(Phase04CilShapesFixture).FullName!, box), "__hybridcpu_managed_box_i4");
        AssertCalls(Import(valueImporter, pe, typeof(Phase04CilShapesFixture).FullName!, unbox), "__hybridcpu_managed_unbox_i4");

        MethodInfo read = typeof(Phase04StaticFixture).GetMethod(nameof(Phase04StaticFixture.Read))!;
        ConstructorInfo cctor = typeof(Phase04StaticFixture).TypeInitializer!;
        HybridCpuManagedTypeSystemV1 staticTypes = Build(new HybridCpuManagedTypeDeclarationV1(typeof(Phase04StaticFixture).FullName!, HybridCpuManagedTypeKindV1.Class, null, [],
            [new(nameof(Phase04StaticFixture.Value), HybridCpuManagedStorageKindV1.Primitive, 4, 4, true, 0)]));
        HybridCpuManagedTypeDescriptorV1 owner = Assert.Single(staticTypes.Descriptors);
        RestrictedCilImporterV1 staticImporter = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            fieldLayouts: [new(typeof(Phase04StaticFixture).FullName!, nameof(Phase04StaticFixture.Value), true, RestrictedCilTypeV1.Int32, owner, "phase04_static")],
            typeInitializationBindings: [new(typeof(Phase04StaticFixture).FullName!, cctor.MetadataToken, owner, staticTypes.TypeHandle(owner.TypeId)!.Value)]);
        RestrictedCilImportResultV1 staticResult = Import(staticImporter, pe, typeof(Phase04StaticFixture).FullName!, read);
        AssertCalls(staticResult, "__hybridcpu_managed_ensure_type_initialized", "__hybridcpu_managed_static_load_i4");
    }

    [Fact]
    public void Cil_MissingBindingsAndLdelemaFailClosedWithStableDiagnostics()
    {
        byte[] pe = File.ReadAllBytes(typeof(Phase04CilShapesFixture).Assembly.Location);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        MethodInfo make = typeof(Phase04CilShapesFixture).GetMethod(nameof(Phase04CilShapesFixture.MakeIntArray))!;
        MethodInfo address = typeof(Phase04CilShapesFixture).GetMethod(nameof(Phase04CilShapesFixture.FirstAddress))!;
        RestrictedCilImportResultV1 missing = Import(importer, pe, typeof(Phase04CilShapesFixture).FullName!, make);
        RestrictedCilImportResultV1 interior = Import(importer, pe, typeof(Phase04CilShapesFixture).FullName!, address);
        Assert.Equal("HCCIL1401", Assert.Single(missing.Diagnostics).Code);
        Assert.Equal("HCCIL1010", Assert.Single(interior.Diagnostics).Code);
        Assert.True(RestrictedCilSupportMatrixV1.Default.TryGetOpcode(0x8f, out RestrictedCilOpcodeContractV1? ldelema));
        Assert.Equal("HCCIL1408", ldelema!.FailureCode);
        Assert.Equal(RestrictedCilMatrixSupportV1.Unsupported, ldelema.Support);
    }

    [Fact]
    public void RealImage_LoaderBootstrapAndIseExecuteCheckedSzArrayLoadThroughGenericIsa()
    {
        const string entry = "phase04_array_entry";
        const string helperName = "__hybridcpu_managed_array_load_i4";
        HybridCpuManagedTypeSystemV1 types = Build(IntArray());
        HybridCpuManagedTypeDescriptorV1 arrayType = Assert.Single(types.Descriptors);
        ulong handle = types.TypeHandle(arrayType.TypeId)!.Value;
        HybridCpuStaticLinkArtifactV1 link = LinkArrayLoadImage(entry, helperName, 0x4000);
        HybridCpuRuntimeHelperV1 helperAbi = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(helperName)!;
        HybridCpuImageRuntimeBootstrapDescriptorV1 bootstrapDescriptor = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, entry, entry,
            [new(helperName, helperAbi.Signature, true)], managedTypes:
            [new(arrayType.TypeId, arrayType.StableIdentity, arrayType.DescriptorDigest, 0, 1, null)]);
        HybridCpuRestrictedImageBuilderV1 imageBuilder = new HybridCpuRestrictedImageBuilderV1();
        HybridCpuRestrictedImageV1 image = imageBuilder.Inspect(imageBuilder.Build(
            new(link, entry, RuntimeBootstrap: bootstrapDescriptor)).PackageBytes);
        Assert.Equal(HybridCpuStartupStatusV1.Success, image.Status);
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        HybridCpuKernelBootResultV1 boot = kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, image.OptionsDigest,
            image.ImageBase, AlignUp((ulong)image.ImageBytes.Length, 4096), image.EntryAddress,
            HybridCpuRestrictedStartupOptionsV1.Production.StackBase, HybridCpuRestrictedStartupOptionsV1.Production.StackSize, 0));
        Assert.True(boot.IsSuccess, boot.Reason);
        HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types,
            HybridCpuManagedHeapOptionsV1.Create(0x4000, 4096, 4096, -3));
        Assert.True(heap.Initialize().IsSuccess);
        HybridCpuManagedArrayRuntimeV1 arrays = new HybridCpuManagedArrayRuntimeV1(types, heap);
        ulong array = arrays.NewArray(handle, 3).ObjectReference;
        Assert.Equal(0x4000UL, array);
        Assert.True(arrays.StoreInt32(array, 1, 73).IsSuccess);
        Assert.True(new HybridCpuManagedBootstrapRuntimeV1(HybridCpuManagedAbiFamilyV1.Default.ContractDigest).Bootstrap(
            bootstrapDescriptor, kernel, new Dictionary<string, HybridCpuRuntimeHelperEntryV1>
            { [helperName] = _ => true }, types).IsSuccess);

        Processor.MainMemoryArea originalMemory = Processor.MainMemory;
        ProcessorMode originalMode = Processor.CurrentProcessorMode;
        MemorySubsystem? originalSubsystem = Processor.Memory;
        try
        {
            Processor.CurrentProcessorMode = ProcessorMode.Compiler; Processor.Memory = null;
            HybridCpuIseSparseMainMemoryAreaV1 memory = new HybridCpuIseSparseMainMemoryAreaV1(); Processor.MainMemory = memory;
            Assert.True(memory.TryWritePhysicalRange(array, heap.ReadObjectBytes(array)!));
            Processor.CPU_Core core = new Processor.CPU_Core(0, CpuCorePlatformContext.CreateFixed(memory, ProcessorMode.Compiler));
            core.InitializePipeline(); core.PrepareExecutionStart(image.EntryAddress);
            for (int register = 0; register < 32; register++) core.WriteCommittedArch(0, register, 0);
            core.WriteCommittedPc(0, image.EntryAddress);
            ulong bundle = (ulong)HybridCpuBundleSerializer.BundleSizeBytes;
            Retire(core, ReadBundle(image, image.EntryAddress), image.EntryAddress);
            Assert.Equal(array, core.ReadArch(0, 10));
            Retire(core, ReadBundle(image, image.EntryAddress + bundle), image.EntryAddress + bundle);
            Assert.Equal(1UL, core.ReadArch(0, 11));
            Retire(core, ReadBundle(image, image.EntryAddress + 2 * bundle), image.EntryAddress + 2 * bundle);
            HybridCpuLinkedSymbolV1 helper = link.Symbols.Single(symbol => symbol.Name == helperName);
            Assert.Equal(helper.Address, core.ReadCommittedPc(0));
            Retire(core, ReadBundle(image, helper.Address), helper.Address);
            Retire(core, ReadBundle(image, helper.Address + bundle), helper.Address + bundle);
            Retire(core, ReadBundle(image, helper.Address + 2 * bundle), helper.Address + 2 * bundle);
            Assert.Equal(image.EntryAddress + 3 * bundle, core.ReadCommittedPc(0));
            Retire(core, ReadBundle(image, image.EntryAddress + 3 * bundle), image.EntryAddress + 3 * bundle);
            Assert.Equal(73UL, core.ReadArch(0, 10));
        }
        finally { Processor.MainMemory = originalMemory; Processor.CurrentProcessorMode = originalMode; Processor.Memory = originalSubsystem; }
    }

    private static HybridCpuManagedTypeDeclarationV1 Class(string name, string? parent = null) => new(name, HybridCpuManagedTypeKindV1.Class, parent, [], []);
    private static HybridCpuManagedTypeDeclarationV1 IntArray() => new("System.Int32[]", HybridCpuManagedTypeKindV1.SzArray, null, [], [],
        new(HybridCpuManagedStorageKindV1.Primitive, null, 4, 4, 16, 24, true, false));
    private static HybridCpuManagedTypeDeclarationV1 RefArray(string name, string element) => new(name, HybridCpuManagedTypeKindV1.SzArray, null, [], [],
        new(HybridCpuManagedStorageKindV1.ObjectReference, StableTypeId(element), 8, 8, 16, 24, true, true));
    private static HybridCpuManagedTypeDeclarationV1 StringType() => new("System.String", HybridCpuManagedTypeKindV1.String, null, [], [], null,
        new(16, 20, 2, true));
    private static HybridCpuManagedTypeDeclarationV1 Value(string name, IReadOnlyList<int> refs, int payloadSize = 8) => new(name, HybridCpuManagedTypeKindV1.ValueType, null, [],
        refs.Select((offset, ordinal) => new HybridCpuManagedFieldDeclarationV1($"ref{ordinal}", HybridCpuManagedStorageKindV1.ObjectReference, 8, 8, false, ordinal)).ToArray(), null, null,
        new(payloadSize, payloadSize >= 8 ? 8 : 4, refs, 16));
    private static ulong StableTypeId(string name) => new HybridCpuManagedTypeSystemBuilderV1().Build([Class(name)]).TypeSystem!.Descriptors[0].TypeId;
    private static HybridCpuManagedTypeDescriptorV1 Type(HybridCpuManagedTypeSystemV1 types, string name) => types.Descriptors.Single(type => type.StableIdentity == name);
    private static HybridCpuManagedTypeSystemV1 Build(params HybridCpuManagedTypeDeclarationV1[] declarations)
    { HybridCpuManagedTypeSystemBuildV1 build = new HybridCpuManagedTypeSystemBuilderV1().Build(declarations); Assert.True(build.IsSuccess, build.Reason); return build.TypeSystem!; }
    private static HybridCpuManagedHeapAllocatorV1 Heap(HybridCpuManagedTypeSystemV1 types) => new(BootKernel(), types,
        HybridCpuManagedHeapOptionsV1.Create(0x4000_0000, 1024 * 1024, 1024 * 1024, -3));
    private static DeterministicRuntimeKernelV1 BootKernel()
    {
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64), 0x10_0000, 4096, 0x10_0000, 0x20_0000, 4096, 0)).IsSuccess);
        return kernel;
    }

    private static RestrictedCilImportResultV1 Import(RestrictedCilImporterV1 importer, byte[] pe, string type, MethodInfo method) =>
        importer.ImportImage(pe, new(type, method.Name, method.MetadataToken), "phase04-cil");
    private static void AssertCalls(RestrictedCilImportResultV1 result, params string[] symbols)
    {
        Assert.True(result.Status == RestrictedCilImportStatusV1.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(row => $"{row.Code}: {row.Message}")));
        foreach (string symbol in symbols) Assert.Contains(result.Program!.Instructions,
            instruction => instruction.Annotation.BranchTargetSymbolName == symbol);
    }
    private static int TokenAfter(MethodInfo method, byte opcode)
    {
        byte[] bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        int offset = Array.IndexOf(bytes, opcode);
        Assert.True(offset >= 0 && offset <= bytes.Length - 5, $"Opcode 0x{opcode:x2} missing in {method.Name}: {Convert.ToHexString(bytes)}");
        return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4));
    }
    private static ulong AlignUp(ulong value, ulong alignment) => checked((value + alignment - 1) / alignment * alignment);

    private static HybridCpuStaticLinkArtifactV1 LinkArrayLoadImage(string entry, string helper, ushort arrayAddress)
    {
        HybridCpuBundleSerializer serializer = new HybridCpuBundleSerializer();
        byte[] managed = serializer.SerializeProgram([Bundle(Addi(10, 0, arrayAddress)), Bundle(Addi(11, 0, 1)), Bundle(Call()), Bundle(Addi(10, 10, 0))]);
        byte[] runtime = serializer.SerializeProgram([Bundle(Addi(10, 10, 28)), Bundle(LoadWord(10, 10, 0)), Bundle(Return())]);
        HybridCpuObjectWriterV1 writer = new HybridCpuObjectWriterV1();
        byte[] Object(byte[] code, IReadOnlyList<HybridCpuObjectSymbolV1> symbols, IReadOnlyList<HybridCpuObjectRelocationV1> relocations)
        {
            HybridCpuObjectArtifactV1 artifact = writer.Write(new(
                [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes, code, (ulong)code.Length)],
                symbols, relocations, HybridCpuTargetPlatformContractV1.Default.ContractDigest, HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
            Assert.Equal(HybridCpuObjectStatusV1.Success, artifact.Status); return artifact.Bytes;
        }
        HybridCpuObjectSymbolV1 Def(string name, ulong size) => new(name, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, ".text", 0, size, true);
        HybridCpuObjectSymbolV1 Decl(string name) => new(name, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, null, 0, 0, false);
        ulong bundle = (ulong)HybridCpuBundleSerializer.BundleSizeBytes;
        HybridCpuStaticLinkArtifactV1 result = new HybridCpuStaticLinkerV1().Link([
            new("managed", Object(managed, [Def(entry, (ulong)managed.Length), Decl(helper)],
                [new(".text", 2 * bundle, HybridCpuRelocationKind.ManagedCallRelativeSigned16, helper, 0)])),
            new("runtime", Object(runtime, [Def(helper, (ulong)runtime.Length)], []))]);
        Assert.Equal(HybridCpuLinkStatusV1.Success, result.Status); return result;
    }
    private static HybridCpuInstructionBundle Bundle(params HybridCpuInstructionWord[] instructions)
    { HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle(); for (int i = 0; i < instructions.Length; i++) bundle.SetInstruction(i, instructions[i]); return bundle; }
    private static HybridCpuInstructionWord Addi(byte destination, byte source, ushort immediate) => new()
    { OpCode = (uint)HybridCpuOpcode.ADDI, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
      Word1 = HybridCpuInstructionWord.PackArchRegs(destination, source, HybridCpuInstructionWord.NoArchReg), Immediate = immediate, VirtualThreadId = 0 };
    private static HybridCpuInstructionWord LoadWord(byte destination, byte address, ushort offset) => new()
    { OpCode = (uint)HybridCpuOpcode.LW, DataTypeValue = HybridCpuDataType.INT32, PredicateMask = byte.MaxValue,
      Word1 = HybridCpuInstructionWord.PackArchRegs(destination, address, HybridCpuInstructionWord.NoArchReg), Immediate = offset, VirtualThreadId = 0 };
    private static HybridCpuInstructionWord Call() => new()
    { OpCode = (uint)HybridCpuOpcode.JAL, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
      Word1 = HybridCpuInstructionWord.PackArchRegs((byte)HybridCpuNativeAbiContractV2.ReturnAddressRegister, HybridCpuInstructionWord.NoArchReg, HybridCpuInstructionWord.NoArchReg), VirtualThreadId = 0 };
    private static HybridCpuInstructionWord Return() => new()
    { OpCode = (uint)HybridCpuOpcode.JALR, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
      Word1 = HybridCpuInstructionWord.PackArchRegs(0, (byte)HybridCpuNativeAbiContractV2.ReturnAddressRegister, HybridCpuInstructionWord.NoArchReg),
      Immediate = HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes, VirtualThreadId = 0 };
    private static VLIW_Instruction[] ReadBundle(HybridCpuRestrictedImageV1 image, ulong address)
    { int index = checked((int)((address - image.ImageBase) / HybridCpuBundleSerializer.BundleSizeBytes)); VLIW_Bundle bundle = new VLIW_Bundle();
      Assert.True(bundle.TryReadBytes(image.ImageBytes, index * HybridCpuBundleSerializer.BundleSizeBytes));
      return Enumerable.Range(0, HybridCpuInstructionBundle.SlotCount).Select(bundle.GetInstruction).ToArray(); }
    private static void Retire(Processor.CPU_Core core, VLIW_Instruction[] bundle, ulong pc)
    { bool control = bundle.Any(instruction => instruction.OpCode is >= (uint)Processor.CPU_Core.InstructionsEnum.JAL and <= (uint)Processor.CPU_Core.InstructionsEnum.BGEU);
      core.TestRunDecodeStageWithFetchedBundle(bundle, pc); core.TestRunExecuteStageFromCurrentDecodeState(); core.TestRunMemoryAndWriteBackStagesFromCurrentExecuteState();
      if (!control) core.WriteCommittedPc(0, pc); }

}

public static class Phase04CilShapesFixture
{
    public static int[] MakeIntArray(int length) => new int[length];
    public static int LoadInt(int[] values, int index) => values[index];
    public static void StoreInt(int[] values, int index, int value) => values[index] = value;
    public static int Length(int[] values) => values.Length;
    public static string Literal() => "phase04-\U0001F642";
    public static int StringLength(string value) => value.Length;
    public static char StringCharacter(string value, int index) => value[index];
    public static object BoxInt(int value) => value;
    public static int UnboxInt(object value) => (int)value;
    public static object[] MakeObjectArray(int length) => new object[length];
    public static object LoadRef(object[] values, int index) => values[index];
    public static void StoreRef(object[] values, int index, object value) => values[index] = value;
    public static ref int FirstAddress(int[] values) => ref values[0];
}

public static class Phase04StaticFixture
{
    public static int Value;
    static Phase04StaticFixture() => Value = 41;
    public static int Read() => Value;
}
