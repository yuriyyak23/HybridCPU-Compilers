using System.Buffers.Binary;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.ManagedRuntime;

public sealed class HybridCpuManagedTypeTestRuntimeV1
{
    private readonly HybridCpuManagedTypeSystemV1 _types;
    private readonly HybridCpuManagedHeapAllocatorV1 _heap;

    public HybridCpuManagedTypeTestRuntimeV1(HybridCpuManagedTypeSystemV1 types,
        HybridCpuManagedHeapAllocatorV1 heap)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _heap = heap ?? throw new ArgumentNullException(nameof(heap));
    }

    public HybridCpuManagedShapeResultV1 IsInstance(ulong receiver, ulong targetTypeHandle)
    {
        HybridCpuManagedTypeDescriptorV1? target = _types.ResolveTypeHandle(targetTypeHandle);
        if (target is null)
            return new(HybridCpuManagedShapeStatusV1.InvalidType,
                "isinst target handle does not resolve to exact image-owned type metadata.");
        if (receiver == 0)
            return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, 0);
        byte[]? bytes = _heap.ReadObjectBytes(receiver);
        if (bytes is null || bytes.Length < HybridCPU.Platform.Contracts.HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes)
            return new(HybridCpuManagedShapeStatusV1.InvalidType,
                "isinst receiver is not an active managed object.");
        HybridCpuManagedTypeDescriptorV1? actual = _types.ResolveTypeHandle(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes));
        if (actual is null)
            return new(HybridCpuManagedShapeStatusV1.InvalidType,
                "isinst receiver has an invalid runtime type handle.");
        return new(HybridCpuManagedShapeStatusV1.Success, string.Empty,
            _types.IsAssignable(actual.TypeId, target.TypeId) ? receiver : 0);
    }
}
