using System.Buffers.Binary;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.ManagedRuntime;

public sealed partial class HybridCpuManagedArrayRuntimeV1
{
    // Checked fused ldelema/initobj operation. No interior reference or value buffer is
    // exported, and no allocation/collection occurs between validation and the clear.
    public HybridCpuManagedShapeResultV1 ZeroValue(ulong array, int index, ulong arrayTypeHandle)
    {
        if (array == 0) return Failure(HybridCpuManagedShapeStatusV1.NullReference, "SZARRAY reference is null.");
        HybridCpuManagedTypeDescriptorV1? expected = _types.ResolveTypeHandle(arrayTypeHandle);
        if (expected is not { Kind: HybridCpuManagedTypeKindV1.SzArray,
                ArrayShape: { IsSzArray: true, ElementStorageKind: HybridCpuManagedStorageKindV1.BlittableValue } shape } ||
            !_heap.TryGetAllocation(array, out HybridCpuManagedActiveAllocationV1? allocation) || allocation is null || allocation.TypeId != expected.TypeId ||
            shape.ElementTypeId is not ulong elementId || _types.TypeHandle(elementId) is not ulong elementHandle)
            return Failure(HybridCpuManagedShapeStatusV1.ArrayTypeMismatch, "Array initobj requires the exact runtime SZARRAY type handle.");
        HybridCpuManagedShapeResultV1 check = ResolveValueElement(array, index, elementHandle, shape.ElementSizeBytes, out int offset);
        if (!check.IsSuccess) return check;
        HybridCpuManagedHeapResultV1 clear = _heap.ClearObjectBytes(array, offset, shape.ElementSizeBytes);
        return clear.IsSuccess ? check : Failure(HybridCpuManagedShapeStatusV1.HeapFailure, clear.Reason);
    }

    // Component operations on a caller-owned value buffer. These do not admit CIL managed
    // byrefs or publish ABI helpers: the compiler must first provide exact value lifetimes.
    public HybridCpuManagedShapeResultV1 LoadValue(ulong array, int index, ulong elementTypeHandle,
        Span<byte> destination)
    {
        HybridCpuManagedShapeResultV1 check = ResolveValueElement(array, index, elementTypeHandle, destination.Length, out int offset);
        if (!check.IsSuccess) return check;
        return _heap.TryReadObjectBytes(array, offset, destination) ? check :
            Failure(HybridCpuManagedShapeStatusV1.HeapFailure, "Value element could not be read from its allocation.");
    }

    public HybridCpuManagedShapeResultV1 StoreValue(ulong array, int index, ulong elementTypeHandle,
        ReadOnlySpan<byte> source)
    {
        HybridCpuManagedShapeResultV1 check = ResolveValueElement(array, index, elementTypeHandle, source.Length, out int offset);
        if (!check.IsSuccess) return check;
        HybridCpuManagedHeapResultV1 write = _heap.WriteObjectBytes(array, offset, source);
        return write.IsSuccess ? check : Failure(HybridCpuManagedShapeStatusV1.HeapFailure, write.Reason);
    }

    private HybridCpuManagedShapeResultV1 ResolveValueElement(ulong array, int index, ulong elementTypeHandle,
        int payloadBytes, out int offset)
    {
        offset = 0;
        if (array == 0) return Failure(HybridCpuManagedShapeStatusV1.NullReference, "SZARRAY reference is null.");
        if (!_heap.TryGetAllocation(array, out HybridCpuManagedActiveAllocationV1? allocation) || allocation is null ||
            _types.Resolve(allocation.TypeId) is not
                { Kind: HybridCpuManagedTypeKindV1.SzArray, ArrayShape: { IsSzArray: true } shape })
            return Failure(HybridCpuManagedShapeStatusV1.InvalidType, "Reference is not a runtime-owned SZARRAY.");
        HybridCpuManagedTypeDescriptorV1? element = _types.ResolveTypeHandle(elementTypeHandle);
        if (element is not { Kind: HybridCpuManagedTypeKindV1.ValueType,
                ValueTypeShape: { ObjectReferenceOffsets.Count: 0 } value } ||
            shape.ElementStorageKind != HybridCpuManagedStorageKindV1.BlittableValue ||
            shape.ElementTypeId != element.TypeId)
            return Failure(HybridCpuManagedShapeStatusV1.ArrayTypeMismatch,
                "Value array access requires the exact reference-free element type, not merely matching byte width.");
        if (value.PayloadSizeBytes != shape.ElementSizeBytes || value.PayloadAlignmentBytes != shape.ElementAlignmentBytes ||
            shape.LengthOffsetBytes < HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes ||
            shape.LengthOffsetBytes > allocation.ObjectSizeBytes - sizeof(int) || shape.DataOffsetBytes < shape.LengthOffsetBytes + sizeof(int) ||
            shape.DataOffsetBytes > allocation.ObjectSizeBytes)
            return Failure(HybridCpuManagedShapeStatusV1.InvalidType, "Value SZARRAY storage does not match the exact payload shape.");
        Span<byte> encodedLength = stackalloc byte[sizeof(int)];
        if (!_heap.TryReadObjectBytes(array, shape.LengthOffsetBytes, encodedLength))
            return Failure(HybridCpuManagedShapeStatusV1.HeapFailure, "Value SZARRAY length could not be read.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(encodedLength);
        if (length < 0 || length > (allocation.ObjectSizeBytes - shape.DataOffsetBytes) / shape.ElementSizeBytes)
            return Failure(HybridCpuManagedShapeStatusV1.InvalidType, "Value SZARRAY length exceeds its allocation extent.");
        if ((uint)index >= (uint)length)
            return Failure(HybridCpuManagedShapeStatusV1.BoundsViolation, "SZARRAY index is outside [0, Length).");
        if (payloadBytes != shape.ElementSizeBytes)
            return Failure(HybridCpuManagedShapeStatusV1.InvalidType, "Value buffer length must exactly match the element payload.");
        // The validated allocation extent bounds this product and sum by ObjectSizeBytes.
        offset = checked(shape.DataOffsetBytes + index * shape.ElementSizeBytes);
        return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, array);
    }
}
