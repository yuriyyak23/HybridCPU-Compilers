using System.Runtime.InteropServices;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

internal static class ArrayZeroSmoke
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        byte[] pe = File.ReadAllBytes(typeof(ArrayZeroFixture).Assembly.Location);
        string identity = $"[{typeof(ArrayZeroPayload).Assembly.GetName().Name}]{typeof(ArrayZeroPayload).FullName}";
        HybridCpuManagedTypeSystemBuilderV1 builder = new HybridCpuManagedTypeSystemBuilderV1();
        HybridCpuManagedTypeDeclarationV1 valueDeclaration = new HybridCpuManagedTypeDeclarationV1(identity, HybridCpuManagedTypeKindV1.ValueType, null, [],
            [new("First", HybridCpuManagedStorageKindV1.Primitive, 4, 4, false, 0),
             new("Last", HybridCpuManagedStorageKindV1.Primitive, 4, 4, false, 1)], ValueTypeShape: new(8, 4, [], 16));
        HybridCpuManagedTypeSystemBuildV1 valueOnly = builder.Build([valueDeclaration]); Check(valueOnly.IsSuccess, valueOnly.Reason);
        HybridCpuManagedTypeDescriptorV1 element = valueOnly.TypeSystem!.Descriptors.Single();
        HybridCpuManagedTypeDeclarationV1 arrayDeclaration = new HybridCpuManagedTypeDeclarationV1(identity + "[]", HybridCpuManagedTypeKindV1.SzArray, null, [], [],
            new(HybridCpuManagedStorageKindV1.BlittableValue, element.TypeId, 8, 4, 16, 24, true, false));
        HybridCpuManagedTypeSystemBuildV1 built = builder.Build([valueDeclaration, arrayDeclaration]); Check(built.IsSuccess, built.Reason);
        HybridCpuManagedTypeSystemV1 types = built.TypeSystem!;
        HybridCpuManagedTypeDescriptorV1 arrayDescriptor = types.Descriptors.Single(d => d.Kind == HybridCpuManagedTypeKindV1.SzArray);
        HybridCpuManagedTypeDescriptorV1 valueDescriptor = types.Descriptors.Single(d => d.Kind == HybridCpuManagedTypeKindV1.ValueType);
        ulong arrayHandle = types.TypeHandle(arrayDescriptor.TypeId)!.Value;
        RestrictedCilArrayTypeBindingV1 binding = new RestrictedCilArrayTypeBindingV1(typeof(ArrayZeroPayload).MetadataToken,
            arrayDescriptor, arrayHandle, valueDescriptor);
        RestrictedCilImportResultV1 Import(RestrictedCilArrayTypeBindingV1? supplied, string method) =>
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
                arrayBindings: supplied is null ? [] : [supplied]).ImportImage(pe,
                new(typeof(ArrayZeroFixture).FullName!, method));
        RestrictedCilImportResultV1 imported = Import(binding, nameof(ArrayZeroFixture.Zero));
        Check(imported.Diagnostics.Any(d => d.Code == "HCCIL1863"),
            "Exact ldelema/initobj pair must reach the explicit missing-native-helper gate: " + Describe(imported));
        ManagedCallGraphCompilationV1 graph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2, arrayBindings: [binding])
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "array-zero-smoke",
                [new(typeof(ArrayZeroFixture).FullName!, nameof(ArrayZeroFixture.Zero))], []));
        Check(graph.Diagnostics.Any(d => d.Code == "HCCIL1863") && graph.Graph is null && graph.Methods.Count == 0,
            "Graph must remain closed before IR until the ABI/runtime pack supplies the helper: " + string.Join(';', graph.Diagnostics));
        Check(Import(null, nameof(ArrayZeroFixture.Zero)).Diagnostics.Any(d => d.Code == "HCCIL1862"),
            "Missing exact array descriptor must fail closed");
        Check(Import(binding with { ElementTypeDescriptor = null }, nameof(ArrayZeroFixture.Zero)).Diagnostics.Any(d => d.Code == "HCCIL1862"),
            "Missing element descriptor must fail closed");
        Check(Import(binding, nameof(ArrayZeroFixture.ReferenceZero)).Diagnostics.Any(d => d.Code == "HCCIL1408"),
            "Reference-containing value initobj remains behind interior-byref/GC-barrier qualification");
        RestrictedCilImportResultV1 separated = Import(binding, nameof(ArrayZeroFixture.Separated));
        Check(separated.Diagnostics.Any(d =>
                (d.Code == "HCCIL1001" && d.Message.Contains("0x0071", StringComparison.Ordinal)) ||
                d.Code == "HCCIL1003"),
            "An ldelema consumed by ldobj before a later initobj must not be fused: " + Describe(separated));

        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Check(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x100000, 4096, 0x100000, 0x200000, 4096, 0, 1000)).IsSuccess, "boot");
        HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types,
            HybridCpuManagedHeapOptionsV1.Create(0x40000000, 4096, 4096, -3));
        Check(heap.Initialize().IsSuccess, "heap");
        HybridCpuManagedArrayRuntimeV1 arrays = new HybridCpuManagedArrayRuntimeV1(types, heap);
        ulong array = arrays.NewArray(arrayHandle, 3).ObjectReference;
        ArrayZeroPayload payload = new ArrayZeroPayload { First = unchecked((int)0xdeadbeef), Last = int.MaxValue };
        byte[] bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref payload, 1)).ToArray();
        Check(arrays.StoreValue(array, 1, types.TypeHandle(valueDescriptor.TypeId)!.Value, bytes).IsSuccess, "fixture store");
        ArrayZeroPayload[] clr = [default, payload, default]; ArrayZeroFixture.Zero(clr, 1);
        Check(arrays.ZeroValue(array, 1, arrayHandle).IsSuccess && clr[1].First == 0 && clr[1].Last == 0,
            "Runtime/CoreCLR zero operation");
        byte[] copied = new byte[8];
        Check(arrays.LoadValue(array, 1, types.TypeHandle(valueDescriptor.TypeId)!.Value, copied).IsSuccess && copied.All(b => b == 0),
            "Complete payload must be zero");
        Check(arrays.LoadValue(array, 0, types.TypeHandle(valueDescriptor.TypeId)!.Value, copied).IsSuccess && copied.All(b => b == 0) &&
            arrays.LoadValue(array, 2, types.TypeHandle(valueDescriptor.TypeId)!.Value, copied).IsSuccess && copied.All(b => b == 0),
            "Adjacent elements remain unchanged");
        Check(arrays.ZeroValue(0, 0, arrayHandle).Status == HybridCpuManagedShapeStatusV1.NullReference &&
            arrays.ZeroValue(array, -1, arrayHandle).Status == HybridCpuManagedShapeStatusV1.BoundsViolation &&
            arrays.ZeroValue(array, 3, arrayHandle).Status == HybridCpuManagedShapeStatusV1.BoundsViolation &&
            arrays.ZeroValue(array, 0, types.TypeHandle(valueDescriptor.TypeId)!.Value).Status == HybridCpuManagedShapeStatusV1.ArrayTypeMismatch,
            "Runtime null/bounds/type validation");
        Check(HybridCpuManagedAbiFamilyV1.SchemaMinor == 63 && HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(
            "__hybridcpu_managed_array_zero_value") is null, "ABI v1.23 must not advertise a missing native helper");
        Console.WriteLine("PASS fused ldelema/initobj validation, exact descriptors, runtime/CoreCLR bytes, bounds/type/escape/GC negatives and explicit pre-IR native-helper gate");
    }
    private static string Describe(RestrictedCilImportResultV1 result) =>
        string.Join(';', result.Diagnostics.Select(d => d.Code + ":" + d.Message));
}

public struct ArrayZeroPayload { public int First; public int Last; }
public static class ArrayZeroFixture
{
    public static void Zero(ArrayZeroPayload[] values, int index) => values[index] = default;
    public static void ReferenceZero(RefInline[] values, int index) => values[index] = default;
    public static void Separated(ArrayZeroPayload[] values, int index)
    {
        ref ArrayZeroPayload location = ref values[index];
        ArrayZeroPayload other = location;
        location = default;
        GC.KeepAlive(other);
    }
}
