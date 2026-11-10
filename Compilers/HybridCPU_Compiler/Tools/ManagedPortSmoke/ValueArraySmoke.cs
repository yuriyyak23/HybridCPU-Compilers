using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

internal static class ValueArraySmoke
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    public static void Run()
    {
        Check(Exercise() == Exercise(), "Value array component bytes must be deterministic");
        Console.WriteLine("PASS value SZARRAY exact identity, CoreCLR copy parity, bounds/atomic failure, GC and determinism");
    }

    private static string Exercise()
    {
        HybridCpuManagedTypeDeclarationV1 Value(string name, int size, int alignment) =>
            new(name, HybridCpuManagedTypeKindV1.ValueType, null, [],
                [new("payload", HybridCpuManagedStorageKindV1.BlittableValue, size, alignment, false, 0)],
                ValueTypeShape: new(size, alignment, [], 16));
        HybridCpuManagedTypeDeclarationV1[] values = new[] { Value("Fixed", 4, 4), Value("OtherFixed", 4, 4), Value("Triple", 12, 4) };
        HybridCpuManagedTypeSystemBuilderV1 builder = new HybridCpuManagedTypeSystemBuilderV1();
        HybridCpuManagedTypeSystemBuildV1 baseTypes = builder.Build(values);
        Check(baseTypes.IsSuccess, baseTypes.Reason);
        ulong Id(string name) => baseTypes.TypeSystem!.Descriptors.Single(t => t.StableIdentity == name).TypeId;
        HybridCpuManagedTypeDeclarationV1 ArrayOf(string name, ulong? id, int size = 4, int align = 4) =>
            new(name, HybridCpuManagedTypeKindV1.SzArray, null, [], [],
                new(HybridCpuManagedStorageKindV1.BlittableValue, id, size, align, 16, 24, true, false));
        HybridCpuManagedTypeDeclarationV1 fixedArray = ArrayOf("Fixed[]", Id("Fixed"));
        HybridCpuManagedTypeDeclarationV1 tripleArray = ArrayOf("Triple[]", Id("Triple"), 12);
        Check(!builder.Build([.. values, fixedArray with { ArrayShape = fixedArray.ArrayShape! with { ElementTypeId = null } }]).IsSuccess,
            "Anonymous blittable arrays must not erase type identity");
        Check(builder.Build([.. values, ArrayOf("Missing[]", ulong.MaxValue)]).Status == HybridCpuManagedTypeSystemStatusV1.MissingDependency,
            "Unknown value type must fail closed");
        Check(!builder.Build([.. values, ArrayOf("BadSize[]", Id("Fixed"), 8)]).IsSuccess, "Element width mismatch");
        Check(!builder.Build([.. values, ArrayOf("BadAlign[]", Id("Fixed"), 4, 2)]).IsSuccess, "Element alignment mismatch");
        HybridCpuManagedTypeDeclarationV1 referenceValue = new HybridCpuManagedTypeDeclarationV1("WithRef", HybridCpuManagedTypeKindV1.ValueType,
            null, [], [new("ref", HybridCpuManagedStorageKindV1.ObjectReference, 8, 8, false, 0)],
            ValueTypeShape: new(8, 8, [0], 16));
        HybridCpuManagedTypeSystemBuildV1 refTypes = builder.Build([referenceValue]);
        Check(refTypes.IsSuccess, refTypes.Reason);
        Check(!builder.Build([referenceValue, ArrayOf("WithRef[]", refTypes.TypeSystem!.Descriptors[0].TypeId, 8, 8)]).IsSuccess,
            "Reference-containing payload cannot be declared blittable");
        HybridCpuManagedTypeSystemBuildV1 built = builder.Build([.. values, fixedArray, tripleArray]);
        Check(built.IsSuccess, built.Reason);
        HybridCpuManagedTypeSystemV1 types = built.TypeSystem!;
        ulong Handle(string name) => types.TypeHandle(types.Descriptors.Single(t => t.StableIdentity == name).TypeId)!.Value;
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Check(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x100000, 4096, 0x100000, 0x200000, 4096, 0, 1000)).IsSuccess, "boot");
        HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types,
            HybridCpuManagedHeapOptionsV1.Create(0x40000000, 4096, 4096, -3));
        Check(heap.Initialize().IsSuccess, "heap init");
        HybridCpuManagedArrayRuntimeV1 arrays = new HybridCpuManagedArrayRuntimeV1(types, heap);
        ulong array = arrays.NewArray(Handle("Fixed[]"), 3).ObjectReference;
        Check(array != 0, "array allocation");
        InlineFixed[] reference = new[] { new InlineFixed { Value = int.MinValue }, new InlineFixed { Value = -1 }, new InlineFixed { Value = int.MaxValue } };
        for (int i = 0; i < reference.Length; i++)
            Check(arrays.StoreValue(array, i, Handle("Fixed"), MemoryMarshal.AsBytes(reference.AsSpan(i, 1))).IsSuccess, "store Fixed");
        InlineFixed[] copied = new InlineFixed[3];
        for (int i = 0; i < copied.Length; i++)
            Check(arrays.LoadValue(array, i, Handle("Fixed"), MemoryMarshal.AsBytes(copied.AsSpan(i, 1))).IsSuccess, "load Fixed");
        Check(copied.Select(v => v.Value).SequenceEqual(reference.Select(v => v.Value)), "CoreCLR value copy parity");
        copied[0].Value = 42;
        Check(BinaryPrimitives.ReadInt32LittleEndian(heap.ReadObjectBytes(array)!.AsSpan(24)) == int.MinValue, "loads must copy, not alias");
        byte[] before = heap.ReadObjectBytes(array)!;
        byte[] input = [1, 2, 3, 4];
        Check(arrays.StoreValue(array, 1, Handle("OtherFixed"), input).Status == HybridCpuManagedShapeStatusV1.ArrayTypeMismatch,
            "Equal width does not authorize another element type");
        foreach (int index in new[] { -1, 3, int.MinValue, int.MaxValue })
            Check(arrays.StoreValue(array, index, Handle("Fixed"), input).Status == HybridCpuManagedShapeStatusV1.BoundsViolation, "bounds");
        Check(!arrays.StoreValue(array, 1, Handle("Fixed"), new byte[3]).IsSuccess, "short source");
        Check(!arrays.StoreValue(array, 1, Handle("Fixed"), new byte[8]).IsSuccess, "long source");
        Check(before.SequenceEqual(heap.ReadObjectBytes(array)!), "Rejected writes must not partially modify heap");
        byte[] destination = Enumerable.Repeat((byte)0xa5, 4).ToArray();
        Check(!arrays.LoadValue(array, 3, Handle("Fixed"), destination).IsSuccess && destination.All(b => b == 0xa5), "Rejected load must preserve destination");
        Check(!heap.TryReadObjectBytes(array, int.MaxValue, destination) && destination.All(b => b == 0xa5), "Heap bounded read must not partially write");
        Check(!heap.TryReadObjectBytes(ulong.MaxValue, 0, destination), "Heap bounded read requires a live object");
        Check(arrays.StoreValue(0, 0, Handle("Fixed"), input).Status == HybridCpuManagedShapeStatusV1.NullReference, "null array");
        Check(!arrays.StoreValue(array, 0, 0, input).IsSuccess, "unknown element handle");
        Check(arrays.StoreInt32(array, 0, 7).Status == HybridCpuManagedShapeStatusV1.InvalidType, "No primitive-op alias for Fixed[]");
        ulong empty = arrays.Empty(Handle("Fixed[]")).ObjectReference;
        Check(empty != 0 && arrays.StoreValue(empty, 0, Handle("Fixed"), input).Status == HybridCpuManagedShapeStatusV1.BoundsViolation, "empty value array");
        Check(arrays.NewArray(Handle("Triple[]"), int.MaxValue).Status == HybridCpuManagedShapeStatusV1.SizeOverflow, "checked allocation size");
        ulong triple = arrays.NewArray(Handle("Triple[]"), 2).ObjectReference;
        byte[] twelve = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
        Check(arrays.StoreValue(triple, 1, Handle("Triple"), twelve).IsSuccess, "12-byte non-scalar payload");
        twelve[0] = 99;
        byte[] tripleBytes = heap.ReadObjectBytes(triple)!;
        Check(tripleBytes.AsSpan(24, 12).ToArray().All(b => b == 0) && tripleBytes[36] == 1, "store must copy one element, not adjacent bytes");
        // A scalar-looking address in a blittable payload must not become a GC edge.
        ulong garbage = arrays.NewArray(Handle("Fixed[]"), 1).ObjectReference;
        BinaryPrimitives.WriteUInt64LittleEndian(twelve, garbage);
        Check(arrays.StoreValue(triple, 0, Handle("Triple"), twelve).IsSuccess, "opaque bits store");
        int allocations = heap.Trace.Count;
        Check(arrays.LoadValue(triple, 0, Handle("Triple"), twelve).IsSuccess && heap.Trace.Count == allocations,
            "Element copy must not allocate a guest object or box");
        HybridCpuManagedAbiFamilyV1 abi = HybridCpuManagedAbiFamilyV1.Default;
        HybridCpuManagedNonMovingGcV1 gc = new HybridCpuManagedNonMovingGcV1(types, heap, abi.ContractDigest, abi.TargetContractDigest,
            abi.NativeAbiDigest, HybridCpuManagedAbiFamilyV1.RuntimePackRevision);
        HybridCpuManagedNonMovingGcResultV1 collected = gc.Collect(new([], [], [new("fixed", HybridCpuManagedGcRootSourceV1.Handle, array),
            new("triple", HybridCpuManagedGcRootSourceV1.Handle, triple)], Arrays: arrays), HybridCpuManagedNonMovingGcOptionsV1.Qualification);
        Check(collected.IsSuccess && collected.ReclaimedObjects.Contains(garbage) && collected.ReachableObjects.Contains(triple),
            "GC must preserve array roots without treating blittable bits as pointers: " + collected.Reason);
        Span<byte> badLength = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(badLength, int.MaxValue);
        Check(heap.WriteObjectBytes(array, 16, badLength).IsSuccess, "corrupt fixture length");
        Check(arrays.StoreValue(array, 0, Handle("Fixed"), input).Status == HybridCpuManagedShapeStatusV1.InvalidType, "malformed extent validation");
        return Convert.ToHexString(SHA256.HashData(heap.ReadObjectBytes(triple)!));
    }
}
