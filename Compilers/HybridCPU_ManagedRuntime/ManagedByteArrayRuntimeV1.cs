using System.Buffers.Binary;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.ManagedRuntime;

public sealed partial class HybridCpuManagedArrayRuntimeV1
{
    public HybridCpuManagedShapeResultV1 LoadInt8(ulong array, int index) => AccessByte(array, index, null, true);
    public HybridCpuManagedShapeResultV1 LoadUInt8(ulong array, int index) => AccessByte(array, index, null, false);
    public HybridCpuManagedShapeResultV1 StoreInt8(ulong array, int index, int value) =>
        AccessByte(array, index, unchecked((byte)value), false);

    private HybridCpuManagedShapeResultV1 AccessByte(ulong array, int index, byte? value, bool signed)
    {
        if (array == 0) return Failure(HybridCpuManagedShapeStatusV1.NullReference, "SZARRAY reference is null.");
        if (!_heap.TryGetAllocation(array, out HybridCpuManagedActiveAllocationV1? allocation) || allocation is null ||
            _types.Resolve(allocation.TypeId) is not { Kind: HybridCpuManagedTypeKindV1.SzArray,
                ArrayShape: { IsSzArray: true, ElementStorageKind: HybridCpuManagedStorageKindV1.Primitive,
                    ElementSizeBytes: 1, ElementAlignmentBytes: 1 } shape })
            return Failure(HybridCpuManagedShapeStatusV1.InvalidType, "Byte element access requires a one-byte primitive SZARRAY.");
        if (shape.LengthOffsetBytes < HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes ||
            shape.LengthOffsetBytes > allocation.ObjectSizeBytes - sizeof(int) ||
            shape.DataOffsetBytes < shape.LengthOffsetBytes + sizeof(int) || shape.DataOffsetBytes > allocation.ObjectSizeBytes)
            return Failure(HybridCpuManagedShapeStatusV1.InvalidType, "Byte SZARRAY shape exceeds its allocation.");
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        if (!_heap.TryReadObjectBytes(array, shape.LengthOffsetBytes, buffer))
            return Failure(HybridCpuManagedShapeStatusV1.HeapFailure, "SZARRAY length read failed.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        if (length < 0 || length > allocation.ObjectSizeBytes - shape.DataOffsetBytes)
            return Failure(HybridCpuManagedShapeStatusV1.InvalidType, "SZARRAY length exceeds its allocation.");
        if ((uint)index >= (uint)length)
            return Failure(HybridCpuManagedShapeStatusV1.BoundsViolation, "SZARRAY index is outside [0, Length).");
        int offset = checked(shape.DataOffsetBytes + index);
        if (value.HasValue)
        {
            buffer[0] = value.Value;
            HybridCpuManagedHeapResultV1 write = _heap.WriteObjectBytes(array, offset, buffer[..1]);
            return write.IsSuccess ? new(HybridCpuManagedShapeStatusV1.Success, string.Empty, array, value.Value) :
                Failure(HybridCpuManagedShapeStatusV1.HeapFailure, write.Reason);
        }
        if (!_heap.TryReadObjectBytes(array, offset, buffer[..1]))
            return Failure(HybridCpuManagedShapeStatusV1.HeapFailure, "SZARRAY element read failed.");
        return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, array,
            signed ? unchecked((sbyte)buffer[0]) : buffer[0]);
    }
}
