using HybridCPU.Compiler.Cil;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;

internal static class EmptyValueArraySmoke
{
    public static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        byte[] pe = File.ReadAllBytes(typeof(EmptyValueFixture).Assembly.Location);
        string identity = $"[{typeof(Payload).Assembly.GetName().Name}]{typeof(Payload).FullName}";
        HybridCpuManagedTypeSystemBuilderV1 builder = new HybridCpuManagedTypeSystemBuilderV1();
        HybridCpuManagedTypeDeclarationV1 declaration = new HybridCpuManagedTypeDeclarationV1(identity, HybridCpuManagedTypeKindV1.ValueType, null, [],
            [new("X", HybridCpuManagedStorageKindV1.Primitive, 4, 4, false, 0)], ValueTypeShape: new(4, 4, [], 16));
        HybridCpuManagedTypeSystemBuildV1 elementTypes = builder.Build([declaration]);
        Check(elementTypes.IsSuccess, elementTypes.Reason);
        HybridCpuManagedTypeDescriptorV1 element = elementTypes.TypeSystem!.Descriptors.Single();
        HybridCpuManagedTypeDeclarationV1 array = new HybridCpuManagedTypeDeclarationV1(identity + "[]", HybridCpuManagedTypeKindV1.SzArray, null, [], [],
            new(HybridCpuManagedStorageKindV1.BlittableValue, element.TypeId, 4, 4, 16, 24, true, false));
        HybridCpuManagedTypeSystemBuildV1 built = builder.Build([declaration, array]);
        Check(built.IsSuccess, built.Reason);
        HybridCpuManagedTypeDescriptorV1 descriptor = built.TypeSystem!.Descriptors.Single(d => d.StableIdentity.EndsWith("[]", StringComparison.Ordinal));
        RestrictedCilArrayTypeBindingV1 binding = new RestrictedCilArrayTypeBindingV1(typeof(Payload).MetadataToken, descriptor,
            built.TypeSystem.TypeHandle(descriptor.TypeId)!.Value, element);
        RestrictedCilImportResultV1 Import(RestrictedCilArrayTypeBindingV1? row, string method = nameof(EmptyValueFixture.Empty)) =>
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                arrayBindings: row is null ? [] : [row]).ImportImage(pe, new(typeof(EmptyValueFixture).FullName!, method));
        void Rejected(RestrictedCilArrayTypeBindingV1? row, string expected)
        {
            RestrictedCilImportResultV1 result = Import(row);
            Check(result.Diagnostics.Any(d => d.Code == expected), string.Join(";", result.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        }
        RestrictedCilImportResultV1 success = Import(binding);
        Check(success.Status == RestrictedCilImportStatusV1.Success,
            string.Join(";", success.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        ManagedCallGraphCompilationV1 graph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2, arrayBindings: [binding])
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "empty-value-smoke",
                [new(typeof(EmptyValueFixture).FullName!, nameof(EmptyValueFixture.Empty))], []));
        Check(graph.Status == RestrictedCilImportStatusV1.Success && graph.Methods.Count == 1,
            "Empty value intrinsic must not instantiate an aggregate-generic managed body: " +
            string.Join(";", graph.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Rejected(null, "HCCIL1462");
        Rejected(binding with { ElementTypeDescriptor = null }, "HCCIL1462");
        Rejected(binding with { ElementTypeMetadataToken = typeof(InlineFixed).MetadataToken }, "HCCIL1462");
        HybridCpuManagedTypeDescriptorV1 wrongArray = descriptor with { ArrayShape = descriptor.ArrayShape! with { ElementTypeId = element.TypeId ^ 1 } };
        wrongArray = wrongArray with { DescriptorDigest = HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(wrongArray) };
        Rejected(binding with { TypeDescriptor = wrongArray }, "HCCIL1462");
        // Even internally consistent forged descriptors must agree with the actual PE payload.
        HybridCpuManagedTypeSystemBuildV1 wide = builder.Build([declaration with
        {
            Fields = [new("X", HybridCpuManagedStorageKindV1.Primitive, 8, 8, false, 0)],
            ValueTypeShape = new(8, 8, [], 16)
        }, array with { ArrayShape = array.ArrayShape! with { ElementSizeBytes = 8, ElementAlignmentBytes = 8 } }]);
        Check(wide.IsSuccess, wide.Reason);
        Rejected(binding with
        {
            TypeDescriptor = wide.TypeSystem!.Descriptors.Single(d => d.Kind == HybridCpuManagedTypeKindV1.SzArray),
            ElementTypeDescriptor = wide.TypeSystem.Descriptors.Single(d => d.Kind == HybridCpuManagedTypeKindV1.ValueType)
        }, "HCCIL1462");
        Check(Import(binding, nameof(EmptyValueFixture.OtherGeneric)).Status != RestrictedCilImportStatusV1.Success,
            "General struct generic ABI must not be enabled by Array.Empty");
        Check(Import(binding, nameof(EmptyValueFixture.ReferencePayload)).Diagnostics.Any(d => d.Code == "HCCIL1461"),
            "Reference-containing value element requires a GC-capable array contract");
        Check(ReferenceEquals(EmptyValueFixture.Empty(), EmptyValueFixture.Empty()) && EmptyValueFixture.Empty().Length == 0,
            "CoreCLR reference behavior");
        Console.WriteLine("PASS Array.Empty<struct> intrinsic, exact PE layout/descriptor binding, graph and fail-closed generic negatives");
    }
}

public static class EmptyValueFixture
{
    public static Payload[] Empty() => Array.Empty<Payload>();
    public static Payload OtherGeneric(Payload value) => Fixture.Identity(value);
    public static RefInline[] ReferencePayload() => Array.Empty<RefInline>();
}
