using System.Buffers.Binary;
using System.Text;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.ManagedRuntime;

public enum HybridCpuManagedShapeStatusV1 : byte
{
    Success = 0,
    NullReference = 1,
    InvalidType = 2,
    NegativeLength = 3,
    SizeOverflow = 4,
    BoundsViolation = 5,
    ArrayTypeMismatch = 6,
    UnsupportedShape = 7,
    HeapFailure = 8,
    InitializationFailed = 9
}

public sealed record HybridCpuManagedShapeResultV1(
    HybridCpuManagedShapeStatusV1 Status,
    string Reason,
    ulong ObjectReference = 0,
    long ScalarValue = 0)
{
    public bool IsSuccess => Status == HybridCpuManagedShapeStatusV1.Success;
}

/// <summary>Runtime-owned SZARRAY operations. CIL lowering supplies exact descriptor handles.</summary>
public sealed partial class HybridCpuManagedArrayRuntimeV1
{
    private readonly HybridCpuManagedTypeSystemV1 _types;
    private readonly HybridCpuManagedHeapAllocatorV1 _heap;
    private readonly SortedDictionary<ulong, byte[]> _fieldData = [];
    private readonly SortedDictionary<ulong, ulong> _emptyArrays = [];

    public IReadOnlyList<ulong> EmptyArrayRoots => _emptyArrays.Values.ToArray();

    public HybridCpuManagedShapeResultV1 Empty(ulong typeHandle)
    {
        if (_emptyArrays.TryGetValue(typeHandle, out ulong cached))
            return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, cached);
        HybridCpuManagedShapeResultV1 allocation = NewArray(typeHandle, 0);
        if (allocation.IsSuccess) _emptyArrays.Add(typeHandle, allocation.ObjectReference);
        return allocation;
    }

    public HybridCpuManagedArrayRuntimeV1(HybridCpuManagedTypeSystemV1 types, HybridCpuManagedHeapAllocatorV1 heap)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _heap = heap ?? throw new ArgumentNullException(nameof(heap));
    }

    public HybridCpuManagedShapeResultV1 NewArray(ulong typeHandle, int length)
    {
        HybridCpuManagedTypeDescriptorV1? type = _types.ResolveTypeHandle(typeHandle);
        if (type?.Kind != HybridCpuManagedTypeKindV1.SzArray || type.ArrayShape is not { IsSzArray: true } shape)
            return Failure(HybridCpuManagedShapeStatusV1.InvalidType, "newarr requires an exact SZARRAY descriptor handle.");
        if (length < 0) return Failure(HybridCpuManagedShapeStatusV1.NegativeLength, "SZARRAY length cannot be negative.");
        int size;
        try { size = checked(shape.DataOffsetBytes + checked(length * shape.ElementSizeBytes)); }
        catch (OverflowException) { return Failure(HybridCpuManagedShapeStatusV1.SizeOverflow, "SZARRAY byte size overflowed checked V1 bounds."); }
        HybridCpuManagedHeapResultV1 allocation = _heap.AllocateVariable(typeHandle, Math.Max(size, type.InstanceSizeBytes));
        if (!allocation.IsSuccess) return Failure(HybridCpuManagedShapeStatusV1.HeapFailure, allocation.Reason);
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, length);
        HybridCpuManagedHeapResultV1 write = _heap.WriteObjectBytes(allocation.ObjectReference, shape.LengthOffsetBytes, lengthBytes);
        return write.IsSuccess ? new(HybridCpuManagedShapeStatusV1.Success, string.Empty, allocation.ObjectReference, length) :
            Failure(HybridCpuManagedShapeStatusV1.HeapFailure, write.Reason);
    }

    public HybridCpuManagedShapeResultV1 Length(ulong array) => WithArray(array, (type, shape, bytes) =>
        new(HybridCpuManagedShapeStatusV1.Success, string.Empty, array,
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(shape.LengthOffsetBytes, 4))));

    public HybridCpuManagedShapeResultV1 LoadInt32(ulong array, int index) => Access(array, index,
        HybridCpuManagedStorageKindV1.Primitive, 4, null, false);

    public HybridCpuManagedShapeResultV1 LoadInt64(ulong array, int index) => Access(array, index,
        HybridCpuManagedStorageKindV1.Primitive, 8, null, false);

    public HybridCpuManagedShapeResultV1 LoadInt16(ulong array, int index) => Access(array, index,
        HybridCpuManagedStorageKindV1.Primitive, 2, null, false, signed16: true);

    public HybridCpuManagedShapeResultV1 LoadUInt16(ulong array, int index) => Access(array, index,
        HybridCpuManagedStorageKindV1.Primitive, 2, null, false);

    public HybridCpuManagedShapeResultV1 StoreInt16(ulong array, int index, int value) => Access(array, index,
        HybridCpuManagedStorageKindV1.Primitive, 2, unchecked((ushort)value), true);

    public HybridCpuManagedShapeResultV1 StoreInt32(ulong array, int index, int value) => Access(array, index,
        HybridCpuManagedStorageKindV1.Primitive, 4, unchecked((ulong)(uint)value), true);

    public HybridCpuManagedShapeResultV1 LoadReference(ulong array, int index) => Access(array, index,
        HybridCpuManagedStorageKindV1.ObjectReference, 8, null, false);

    public HybridCpuManagedShapeResultV1 StoreReference(ulong array, int index, ulong value)
    {
        HybridCpuManagedTypeDescriptorV1? valueType = value == 0 ? null : ObjectType(value);
        return WithArray(array, (type, shape, bytes) =>
        {
            if (shape.ElementStorageKind != HybridCpuManagedStorageKindV1.ObjectReference || shape.ElementSizeBytes != 8)
                return Failure(HybridCpuManagedShapeStatusV1.InvalidType, "stelem.ref requires a reference-element SZARRAY.");
            if (value != 0 && (valueType is null || shape.ElementTypeId is not ulong target || !_types.IsAssignable(valueType.TypeId, target)))
                return Failure(HybridCpuManagedShapeStatusV1.ArrayTypeMismatch, "Reference value is not assignable to the SZARRAY element type.");
            return AccessKnown(array, index, shape, bytes, value, true);
        });
    }

    /// <summary>
    /// Copies an exact SZARRAY range after validating both complete ranges and every
    /// reference-store constraint. The source snapshot gives memmove semantics for
    /// overlapping ranges and keeps rejected operations free of partial writes.
    /// </summary>
    public HybridCpuManagedShapeResultV1 Copy(ulong sourceArray, int sourceIndex,
        ulong destinationArray, int destinationIndex, int length)
    {
        if (sourceArray == 0 || destinationArray == 0)
            return Failure(HybridCpuManagedShapeStatusV1.NullReference, "Array.Copy requires non-null source and destination SZARRAY references.");
        if (length < 0)
            return Failure(HybridCpuManagedShapeStatusV1.NegativeLength, "Array.Copy length cannot be negative.");
        if (sourceIndex < 0 || destinationIndex < 0)
            return Failure(HybridCpuManagedShapeStatusV1.BoundsViolation, "Array.Copy indices cannot be negative.");

        byte[]? sourceBytes = _heap.ReadObjectBytes(sourceArray);
        byte[]? destinationBytes = sourceArray == destinationArray ? sourceBytes : _heap.ReadObjectBytes(destinationArray);
        HybridCpuManagedTypeDescriptorV1? sourceType = ObjectType(sourceArray);
        HybridCpuManagedTypeDescriptorV1? destinationType = sourceArray == destinationArray ? sourceType : ObjectType(destinationArray);
        if (sourceBytes is null || destinationBytes is null ||
            sourceType?.Kind != HybridCpuManagedTypeKindV1.SzArray || sourceType.ArrayShape is not { IsSzArray: true } sourceShape ||
            destinationType?.Kind != HybridCpuManagedTypeKindV1.SzArray || destinationType.ArrayShape is not { IsSzArray: true } destinationShape)
            return Failure(HybridCpuManagedShapeStatusV1.InvalidType, "Array.Copy references must resolve to runtime-owned SZARRAY descriptors.");

        if (!TryValidateArrayExtent(sourceShape, sourceBytes, out int sourceLength) ||
            !TryValidateArrayExtent(destinationShape, destinationBytes, out int destinationLength))
            return Failure(HybridCpuManagedShapeStatusV1.InvalidType, "Array.Copy rejected a malformed SZARRAY extent.");
        if (sourceIndex > sourceLength || length > sourceLength - sourceIndex ||
            destinationIndex > destinationLength || length > destinationLength - destinationIndex)
            return Failure(HybridCpuManagedShapeStatusV1.BoundsViolation, "Array.Copy range is outside the source or destination SZARRAY.");

        bool referenceCopy = sourceShape.ElementStorageKind == HybridCpuManagedStorageKindV1.ObjectReference &&
            destinationShape.ElementStorageKind == HybridCpuManagedStorageKindV1.ObjectReference &&
            sourceShape.ElementSizeBytes == 8 && destinationShape.ElementSizeBytes == 8;
        bool exactPrimitiveCopy = sourceShape.ElementStorageKind == HybridCpuManagedStorageKindV1.Primitive &&
            destinationShape.ElementStorageKind == HybridCpuManagedStorageKindV1.Primitive &&
            sourceShape.ElementSizeBytes == destinationShape.ElementSizeBytes && sourceType.TypeId == destinationType.TypeId;
        bool exactValueCopy = sourceShape.ElementStorageKind == HybridCpuManagedStorageKindV1.BlittableValue &&
            destinationShape.ElementStorageKind == HybridCpuManagedStorageKindV1.BlittableValue &&
            sourceShape.ElementSizeBytes == destinationShape.ElementSizeBytes &&
            sourceShape.ElementTypeId is not null && sourceShape.ElementTypeId == destinationShape.ElementTypeId;
        if (!referenceCopy && !exactPrimitiveCopy && !exactValueCopy)
            return Failure(HybridCpuManagedShapeStatusV1.ArrayTypeMismatch,
                "Array.Copy V1 requires reference arrays or identical primitive/blittable element descriptors.");

        int byteCount;
        int sourceOffset;
        int destinationOffset;
        try
        {
            byteCount = checked(length * sourceShape.ElementSizeBytes);
            sourceOffset = checked(sourceShape.DataOffsetBytes + sourceIndex * sourceShape.ElementSizeBytes);
            destinationOffset = checked(destinationShape.DataOffsetBytes + destinationIndex * destinationShape.ElementSizeBytes);
        }
        catch (OverflowException)
        {
            return Failure(HybridCpuManagedShapeStatusV1.SizeOverflow, "Array.Copy byte range overflowed checked V1 bounds.");
        }

        if (referenceCopy)
        {
            if (destinationShape.ElementTypeId is not ulong destinationElementType)
                return Failure(HybridCpuManagedShapeStatusV1.UnsupportedShape, "Reference SZARRAY destination has no exact element type identity.");
            for (int offset = 0; offset < byteCount; offset += 8)
            {
                ulong value = BinaryPrimitives.ReadUInt64LittleEndian(sourceBytes.AsSpan(sourceOffset + offset, 8));
                HybridCpuManagedTypeDescriptorV1? valueType = value == 0 ? null : ObjectType(value);
                if (value != 0 && (valueType is null || !_types.IsAssignable(valueType.TypeId, destinationElementType)))
                    return Failure(HybridCpuManagedShapeStatusV1.ArrayTypeMismatch,
                        "Array.Copy reference value is not assignable to the destination SZARRAY element type.");
            }
        }

        if (byteCount == 0)
            return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, destinationArray, 0);
        byte[] payload = sourceBytes.AsSpan(sourceOffset, byteCount).ToArray();
        HybridCpuManagedHeapResultV1 write = _heap.WriteObjectBytes(destinationArray, destinationOffset, payload);
        return write.IsSuccess
            ? new(HybridCpuManagedShapeStatusV1.Success, string.Empty, destinationArray, length)
            : Failure(HybridCpuManagedShapeStatusV1.HeapFailure, write.Reason);
    }

    public HybridCpuManagedShapeResultV1 Copy(ulong sourceArray, ulong destinationArray, int length) =>
        Copy(sourceArray, 0, destinationArray, 0, length);

    public HybridCpuManagedShapeResultV1 Clear(ulong array)
    {
        if (array == 0)
            return Failure(HybridCpuManagedShapeStatusV1.NullReference, "Array.Clear requires a non-null SZARRAY reference.");
        byte[]? bytes = _heap.ReadObjectBytes(array);
        HybridCpuManagedTypeDescriptorV1? type = ObjectType(array);
        if (bytes is null || type?.Kind != HybridCpuManagedTypeKindV1.SzArray ||
            type.ArrayShape is not { IsSzArray: true } shape || !TryValidateArrayExtent(shape, bytes, out int length))
            return Failure(HybridCpuManagedShapeStatusV1.InvalidType,
                "Array.Clear requires a runtime-owned SZARRAY with a valid allocation extent.");
        int byteCount;
        try { byteCount = checked(length * shape.ElementSizeBytes); }
        catch (OverflowException)
        {
            return Failure(HybridCpuManagedShapeStatusV1.SizeOverflow, "Array.Clear payload size overflowed checked V1 bounds.");
        }
        if (byteCount == 0)
            return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, array);
        HybridCpuManagedHeapResultV1 clear = _heap.ClearObjectBytes(array, shape.DataOffsetBytes, byteCount);
        return clear.IsSuccess
            ? new(HybridCpuManagedShapeStatusV1.Success, string.Empty, array, length)
            : Failure(HybridCpuManagedShapeStatusV1.HeapFailure, clear.Reason);
    }

    public bool RegisterFieldData(ulong handle, ReadOnlySpan<byte> data)
    {
        if (handle is 0 or > 4096 || data.IsEmpty || data.Length > 1_048_576 ||
            _fieldData.Count >= 4096 || _fieldData.ContainsKey(handle)) return false;
        _fieldData.Add(handle, data.ToArray());
        return true;
    }

    public HybridCpuManagedShapeResultV1 InitializeArray(ulong array, ulong fieldDataHandle)
    {
        if (!_fieldData.TryGetValue(fieldDataHandle, out byte[]? data))
            return Failure(HybridCpuManagedShapeStatusV1.InitializationFailed,
                "InitializeArray requires an exact registered FieldRVA data handle.");
        return WithArray(array, (_, shape, bytes) =>
        {
            if (shape.ElementStorageKind != HybridCpuManagedStorageKindV1.Primitive)
                return Failure(HybridCpuManagedShapeStatusV1.UnsupportedShape,
                    "InitializeArray requires an exact primitive-element SZARRAY.");
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(shape.LengthOffsetBytes, 4));
            int payloadSize;
            try { payloadSize = checked(length * shape.ElementSizeBytes); }
            catch (OverflowException)
            {
                return Failure(HybridCpuManagedShapeStatusV1.SizeOverflow,
                    "InitializeArray payload size overflowed checked V1 bounds.");
            }
            if (data.Length != payloadSize)
                return Failure(HybridCpuManagedShapeStatusV1.InitializationFailed,
                    "FieldRVA payload size does not exactly match the target SZARRAY payload.");
            HybridCpuManagedHeapResultV1 write = _heap.WriteObjectBytes(array, shape.DataOffsetBytes, data);
            return write.IsSuccess
                ? new(HybridCpuManagedShapeStatusV1.Success, string.Empty, array, length)
                : Failure(HybridCpuManagedShapeStatusV1.HeapFailure, write.Reason);
        });
    }

    private HybridCpuManagedShapeResultV1 Access(ulong array, int index, HybridCpuManagedStorageKindV1 kind,
        int size, ulong? value, bool write, bool signed16 = false) => WithArray(array, (_, shape, bytes) =>
    {
        if (shape.ElementStorageKind != kind || shape.ElementSizeBytes != size)
            return Failure(HybridCpuManagedShapeStatusV1.InvalidType, "Array element opcode does not match the runtime element shape.");
        return AccessKnown(array, index, shape, bytes, value, write, signed16);
    });

    private HybridCpuManagedShapeResultV1 AccessKnown(ulong array, int index, HybridCpuManagedArrayShapeV1 shape,
        byte[] bytes, ulong? value, bool write, bool signed16 = false)
    {
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(shape.LengthOffsetBytes, 4));
        if ((uint)index >= (uint)length) return Failure(HybridCpuManagedShapeStatusV1.BoundsViolation, "SZARRAY index is outside [0, Length).");
        int offset = checked(shape.DataOffsetBytes + index * shape.ElementSizeBytes);
        if (!write)
        {
            long scalar = shape.ElementSizeBytes switch
            {
                2 when signed16 => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset, 2)),
                2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2)),
                4 => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)),
                _ => unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8)))
            };
            return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, array, scalar);
        }
        Span<byte> encoded = stackalloc byte[8];
        if (shape.ElementSizeBytes == 2) BinaryPrimitives.WriteUInt16LittleEndian(encoded, unchecked((ushort)value!.Value));
        else if (shape.ElementSizeBytes == 4) BinaryPrimitives.WriteUInt32LittleEndian(encoded, unchecked((uint)value!.Value));
        else BinaryPrimitives.WriteUInt64LittleEndian(encoded, value!.Value);
        HybridCpuManagedHeapResultV1 result = _heap.WriteObjectBytes(array, offset, encoded[..shape.ElementSizeBytes]);
        return result.IsSuccess ? new(HybridCpuManagedShapeStatusV1.Success, string.Empty, array, unchecked((long)value.Value)) :
            Failure(HybridCpuManagedShapeStatusV1.HeapFailure, result.Reason);
    }

    private HybridCpuManagedShapeResultV1 WithArray(ulong array,
        Func<HybridCpuManagedTypeDescriptorV1, HybridCpuManagedArrayShapeV1, byte[], HybridCpuManagedShapeResultV1> operation)
    {
        if (array == 0) return Failure(HybridCpuManagedShapeStatusV1.NullReference, "SZARRAY reference is null.");
        byte[]? bytes = _heap.ReadObjectBytes(array);
        HybridCpuManagedTypeDescriptorV1? type = ObjectType(array);
        if (bytes is null || type?.Kind != HybridCpuManagedTypeKindV1.SzArray || type.ArrayShape is null)
            return Failure(HybridCpuManagedShapeStatusV1.InvalidType, "Reference is not a runtime-owned SZARRAY.");
        return operation(type, type.ArrayShape, bytes);
    }

    private HybridCpuManagedTypeDescriptorV1? ObjectType(ulong reference)
    {
        byte[]? bytes = _heap.ReadObjectBytes(reference);
        return bytes is null ? null : _types.ResolveTypeHandle(BinaryPrimitives.ReadUInt64LittleEndian(bytes));
    }

    private static bool TryValidateArrayExtent(HybridCpuManagedArrayShapeV1 shape, byte[] bytes, out int length)
    {
        length = 0;
        if (shape.LengthOffsetBytes < 0 || shape.LengthOffsetBytes > bytes.Length - 4 ||
            shape.DataOffsetBytes < 0 || shape.DataOffsetBytes > bytes.Length || shape.ElementSizeBytes <= 0)
            return false;
        length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(shape.LengthOffsetBytes, 4));
        if (length < 0) return false;
        try { return checked(shape.DataOffsetBytes + checked(length * shape.ElementSizeBytes)) <= bytes.Length; }
        catch (OverflowException) { return false; }
    }

    private static HybridCpuManagedShapeResultV1 Failure(HybridCpuManagedShapeStatusV1 status, string reason) => new(status, reason);
}

public sealed class HybridCpuManagedStringRuntimeV1
{
    private readonly HybridCpuManagedTypeSystemV1 _types;
    private readonly HybridCpuManagedHeapAllocatorV1 _heap;
    private readonly ulong? _defaultStringTypeHandle;
    private readonly SortedDictionary<string, ulong> _literals = new(StringComparer.Ordinal);
    private readonly SortedDictionary<ulong, (ulong TypeHandle, string Value, ulong Reference)> _literalHandles = [];

    public HybridCpuManagedStringRuntimeV1(HybridCpuManagedTypeSystemV1 types, HybridCpuManagedHeapAllocatorV1 heap,
        ulong? defaultStringTypeHandle = null)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _heap = heap ?? throw new ArgumentNullException(nameof(heap));
        if (defaultStringTypeHandle is ulong handle &&
            types.ResolveTypeHandle(handle) is not { Kind: HybridCpuManagedTypeKindV1.String, StringShape: not null })
            throw new ArgumentException("Default string handle must resolve to one exact runtime string descriptor.", nameof(defaultStringTypeHandle));
        _defaultStringTypeHandle = defaultStringTypeHandle;
    }

    public IReadOnlyDictionary<string, ulong> Literals => _literals;
    public IReadOnlyDictionary<ulong, ulong> LiteralHandles => _literalHandles.ToDictionary(
        static row => row.Key, static row => row.Value.Reference);

    public HybridCpuManagedShapeResultV1 RegisterLiteral(ulong literalHandle, ulong typeHandle, string value)
    {
        if (literalHandle == 0) return new(HybridCpuManagedShapeStatusV1.InvalidType, "String literal handle must be non-zero.");
        ArgumentNullException.ThrowIfNull(value);
        if (_literalHandles.TryGetValue(literalHandle, out (ulong TypeHandle, string Value, ulong Reference) existing))
            return existing.TypeHandle == typeHandle && StringComparer.Ordinal.Equals(existing.Value, value)
                ? new(HybridCpuManagedShapeStatusV1.Success, string.Empty, existing.Reference, value.Length)
                : new(HybridCpuManagedShapeStatusV1.InvalidType, "String literal handle is already bound to a different exact literal contract.");
        HybridCpuManagedShapeResultV1 result = MaterializeLiteral(typeHandle, value);
        if (result.IsSuccess) _literalHandles.Add(literalHandle, (typeHandle, value, result.ObjectReference));
        return result;
    }

    public ulong? ResolveLiteral(ulong literalHandle) =>
        _literalHandles.TryGetValue(literalHandle, out (ulong TypeHandle, string Value, ulong Reference) registration) ? registration.Reference : null;

    public HybridCpuManagedShapeResultV1 MaterializeLiteral(ulong typeHandle, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string key = HybridCpuPlatformContractV1.Hash($"utf16-literal/v1|{typeHandle}|{value}");
        if (_literals.TryGetValue(key, out ulong existing)) return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, existing, value.Length);
        HybridCpuManagedTypeDescriptorV1? type = _types.ResolveTypeHandle(typeHandle);
        if (type?.Kind != HybridCpuManagedTypeKindV1.String || type.StringShape is not { CharacterSizeBytes: 2, IsImmutable: true } shape)
            return new(HybridCpuManagedShapeStatusV1.InvalidType, "ldstr requires an exact immutable UTF-16 string descriptor.");
        byte[] payload = new byte[checked(value.Length * sizeof(char))];
        for (int index = 0; index < value.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(index * sizeof(char), sizeof(char)), value[index]);
        int size;
        try { size = checked(shape.DataOffsetBytes + payload.Length); }
        catch (OverflowException) { return new(HybridCpuManagedShapeStatusV1.SizeOverflow, "String byte size overflowed checked V1 bounds."); }
        HybridCpuManagedHeapResultV1 allocation = _heap.AllocateVariable(typeHandle, Math.Max(size, type.InstanceSizeBytes));
        if (!allocation.IsSuccess) return new(HybridCpuManagedShapeStatusV1.HeapFailure, allocation.Reason);
        Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        if (!_heap.WriteObjectBytes(allocation.ObjectReference, shape.LengthOffsetBytes, length).IsSuccess ||
            !_heap.WriteObjectBytes(allocation.ObjectReference, shape.DataOffsetBytes, payload).IsSuccess)
            return new(HybridCpuManagedShapeStatusV1.HeapFailure, "String payload write failed.");
        _literals.Add(key, allocation.ObjectReference);
        return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, allocation.ObjectReference, value.Length);
    }

    public HybridCpuManagedShapeResultV1 FromCharArray(ulong typeHandle, ulong array)
    {
        HybridCpuManagedTypeDescriptorV1? stringType = _types.ResolveTypeHandle(typeHandle);
        if (stringType?.Kind != HybridCpuManagedTypeKindV1.String ||
            stringType.StringShape is not { CharacterSizeBytes: 2, IsImmutable: true } stringShape)
            return new(HybridCpuManagedShapeStatusV1.InvalidType,
                "String(char[]) requires an exact immutable UTF-16 string descriptor handle.");
        if (array == 0)
            return new(HybridCpuManagedShapeStatusV1.NullReference, "String(char[]) source array is null.");
        byte[]? source = _heap.ReadObjectBytes(array);
        if (source is null || source.Length < sizeof(ulong))
            return new(HybridCpuManagedShapeStatusV1.InvalidType, "String(char[]) source is not a runtime-owned object.");
        HybridCpuManagedTypeDescriptorV1? arrayType = _types.ResolveTypeHandle(
            BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(0, sizeof(ulong))));
        if (arrayType?.Kind != HybridCpuManagedTypeKindV1.SzArray ||
            arrayType.ArrayShape is not { IsSzArray: true, ElementStorageKind: HybridCpuManagedStorageKindV1.Primitive,
                ElementSizeBytes: 2, ElementAlignmentBytes: 2 } arrayShape ||
            arrayShape.LengthOffsetBytes < HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes ||
            arrayShape.LengthOffsetBytes > source.Length - sizeof(int) ||
            arrayShape.DataOffsetBytes < arrayShape.LengthOffsetBytes + sizeof(int) ||
            arrayShape.DataOffsetBytes > source.Length)
            return new(HybridCpuManagedShapeStatusV1.InvalidType,
                "String(char[]) requires an exact two-byte primitive SZARRAY source.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(arrayShape.LengthOffsetBytes, sizeof(int)));
        int payloadBytes;
        int objectSize;
        try
        {
            payloadBytes = checked(length * sizeof(char));
            objectSize = checked(stringShape.DataOffsetBytes + payloadBytes);
        }
        catch (OverflowException)
        {
            return new(HybridCpuManagedShapeStatusV1.SizeOverflow, "String(char[]) payload size overflowed checked V1 bounds.");
        }
        if (length < 0 || payloadBytes > source.Length - arrayShape.DataOffsetBytes)
            return new(HybridCpuManagedShapeStatusV1.InvalidType, "String(char[]) source length exceeds its allocation.");
        HybridCpuManagedHeapResultV1 allocation = _heap.AllocateVariable(typeHandle,
            Math.Max(objectSize, stringType.InstanceSizeBytes));
        if (!allocation.IsSuccess)
            return new(HybridCpuManagedShapeStatusV1.HeapFailure, allocation.Reason);
        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, length);
        if (!_heap.WriteObjectBytes(allocation.ObjectReference, stringShape.LengthOffsetBytes, lengthBytes).IsSuccess ||
            !_heap.WriteObjectBytes(allocation.ObjectReference, stringShape.DataOffsetBytes,
                source.AsSpan(arrayShape.DataOffsetBytes, payloadBytes)).IsSuccess)
            return new(HybridCpuManagedShapeStatusV1.HeapFailure, "String(char[]) payload copy failed.");
        return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, allocation.ObjectReference, length);
    }

    public HybridCpuManagedShapeResultV1 Length(ulong reference) => Read(reference, 0, readLength: true);
    public HybridCpuManagedShapeResultV1 Character(ulong reference, int index) => Read(reference, index, readLength: false);

    public HybridCpuManagedShapeResultV1 AreEqual(ulong first, ulong second) => Compare(first, second, false);

    public HybridCpuManagedShapeResultV1 AreNotEqual(ulong first, ulong second) => Compare(first, second, true);

    private HybridCpuManagedShapeResultV1 Compare(ulong first, ulong second, bool negate)
    {
        if (first == second)
            return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, ScalarValue: negate ? 0 : 1);
        if (first == 0 || second == 0)
            return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, ScalarValue: negate ? 1 : 0);
        byte[]? firstBytes = _heap.ReadObjectBytes(first);
        byte[]? secondBytes = _heap.ReadObjectBytes(second);
        if (!TryStringPayload(firstBytes, out HybridCpuManagedTypeDescriptorV1? firstType, out int firstOffset, out int firstBytesLength) ||
            !TryStringPayload(secondBytes, out HybridCpuManagedTypeDescriptorV1? secondType, out int secondOffset, out int secondBytesLength) ||
            firstType!.TypeId != secondType!.TypeId)
            return new(HybridCpuManagedShapeStatusV1.InvalidType,
                "String equality requires exact valid immutable UTF-16 string references.");
        bool equal = firstBytesLength == secondBytesLength &&
            firstBytes!.AsSpan(firstOffset, firstBytesLength).SequenceEqual(secondBytes!.AsSpan(secondOffset, secondBytesLength));
        return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, ScalarValue: equal != negate ? 1 : 0);
    }

    private bool TryStringPayload(byte[]? bytes, out HybridCpuManagedTypeDescriptorV1? type,
        out int offset, out int byteLength)
    {
        type = null;
        offset = 0;
        byteLength = 0;
        if (bytes is null || bytes.Length < sizeof(ulong)) return false;
        type = _types.ResolveTypeHandle(BinaryPrimitives.ReadUInt64LittleEndian(bytes));
        if (type?.Kind != HybridCpuManagedTypeKindV1.String ||
            type.StringShape is not { CharacterSizeBytes: 2, IsImmutable: true } shape ||
            shape.LengthOffsetBytes < HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes ||
            shape.LengthOffsetBytes > bytes.Length - sizeof(int) || shape.DataOffsetBytes < shape.LengthOffsetBytes + sizeof(int) ||
            shape.DataOffsetBytes > bytes.Length)
            return false;
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(shape.LengthOffsetBytes, sizeof(int)));
        if (length < 0) return false;
        try { byteLength = checked(length * sizeof(char)); }
        catch (OverflowException) { return false; }
        if (byteLength > bytes.Length - shape.DataOffsetBytes) return false;
        offset = shape.DataOffsetBytes;
        return true;
    }

    public HybridCpuManagedShapeResultV1 Concat2(ulong first, ulong second) => Concat([first, second]);

    public HybridCpuManagedShapeResultV1 Concat3(ulong first, ulong second, ulong third) => Concat([first, second, third]);

    private HybridCpuManagedShapeResultV1 Concat(ulong[] references)
    {
        if (_defaultStringTypeHandle is not ulong typeHandle ||
            _types.ResolveTypeHandle(typeHandle) is not { StringShape: { CharacterSizeBytes: 2, IsImmutable: true } shape } type)
            return new(HybridCpuManagedShapeStatusV1.InvalidType,
                "String.Concat requires one exact configured immutable UTF-16 string descriptor.");
        byte[][] payloads = new byte[references.Length][];
        int totalCharacters = 0;
        for (int index = 0; index < references.Length; index++)
        {
            if (references[index] == 0) { payloads[index] = []; continue; }
            byte[]? bytes = _heap.ReadObjectBytes(references[index]);
            if (bytes is null || bytes.Length < shape.DataOffsetBytes ||
                _types.ResolveTypeHandle(BinaryPrimitives.ReadUInt64LittleEndian(bytes))?.TypeId != type.TypeId)
                return new(HybridCpuManagedShapeStatusV1.InvalidType, "String.Concat operand is not an exact runtime string.");
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(shape.LengthOffsetBytes, sizeof(int)));
            if (length < 0 || length > (bytes.Length - shape.DataOffsetBytes) / sizeof(char))
                return new(HybridCpuManagedShapeStatusV1.InvalidType, "String.Concat operand has an invalid UTF-16 shape.");
            try { totalCharacters = checked(totalCharacters + length); }
            catch (OverflowException) { return new(HybridCpuManagedShapeStatusV1.SizeOverflow, "String.Concat length overflowed checked V1 bounds."); }
            payloads[index] = bytes.AsSpan(shape.DataOffsetBytes, checked(length * sizeof(char))).ToArray();
        }
        int size;
        try { size = checked(shape.DataOffsetBytes + checked(totalCharacters * sizeof(char))); }
        catch (OverflowException) { return new(HybridCpuManagedShapeStatusV1.SizeOverflow, "String.Concat allocation size overflowed checked V1 bounds."); }
        HybridCpuManagedHeapResultV1 allocation = _heap.AllocateVariable(typeHandle, Math.Max(size, type.InstanceSizeBytes));
        if (!allocation.IsSuccess) return new(HybridCpuManagedShapeStatusV1.HeapFailure, allocation.Reason);
        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, totalCharacters);
        if (!_heap.WriteObjectBytes(allocation.ObjectReference, shape.LengthOffsetBytes, lengthBytes).IsSuccess)
            return new(HybridCpuManagedShapeStatusV1.HeapFailure, "String.Concat length write failed.");
        int offset = shape.DataOffsetBytes;
        foreach (byte[] payload in payloads)
        {
            if (!_heap.WriteObjectBytes(allocation.ObjectReference, offset, payload).IsSuccess)
                return new(HybridCpuManagedShapeStatusV1.HeapFailure, "String.Concat payload write failed.");
            offset = checked(offset + payload.Length);
        }
        return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, allocation.ObjectReference, totalCharacters);
    }

    private HybridCpuManagedShapeResultV1 Read(ulong reference, int index, bool readLength)
    {
        if (reference == 0) return new(HybridCpuManagedShapeStatusV1.NullReference, "String reference is null.");
        byte[]? bytes = _heap.ReadObjectBytes(reference);
        if (bytes is null) return new(HybridCpuManagedShapeStatusV1.InvalidType, "Reference is not runtime-owned.");
        HybridCpuManagedTypeDescriptorV1? type = _types.ResolveTypeHandle(BinaryPrimitives.ReadUInt64LittleEndian(bytes));
        if (type?.StringShape is not { } shape) return new(HybridCpuManagedShapeStatusV1.InvalidType, "Reference is not a string.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(shape.LengthOffsetBytes, 4));
        if (readLength) return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, reference, length);
        if ((uint)index >= (uint)length) return new(HybridCpuManagedShapeStatusV1.BoundsViolation, "String index is outside [0, Length).");
        return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, reference,
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(shape.DataOffsetBytes + index * 2, 2)));
    }
}

public sealed class HybridCpuManagedValueTypeRuntimeV1
{
    private readonly HybridCpuManagedTypeSystemV1 _types;
    private readonly HybridCpuManagedHeapAllocatorV1 _heap;
    public HybridCpuManagedValueTypeRuntimeV1(HybridCpuManagedTypeSystemV1 types, HybridCpuManagedHeapAllocatorV1 heap) { _types = types; _heap = heap; }

    public HybridCpuManagedShapeResultV1 Box(ulong typeHandle, ReadOnlySpan<byte> payload)
    {
        HybridCpuManagedTypeDescriptorV1? type = _types.ResolveTypeHandle(typeHandle);
        if (type?.ValueTypeShape is not { } shape || shape.ObjectReferenceOffsets.Count != 0)
            return new(HybridCpuManagedShapeStatusV1.UnsupportedShape, "Only fixed-layout blittable value types can be boxed in Phase 04.");
        if (payload.Length != shape.PayloadSizeBytes) return new(HybridCpuManagedShapeStatusV1.InvalidType, "Box payload size does not match the value-type descriptor.");
        HybridCpuManagedHeapResultV1 result = _heap.AllocateVariable(typeHandle, checked(shape.BoxedPayloadOffsetBytes + payload.Length));
        if (!result.IsSuccess) return new(HybridCpuManagedShapeStatusV1.HeapFailure, result.Reason);
        HybridCpuManagedHeapResultV1 write = _heap.WriteObjectBytes(result.ObjectReference, shape.BoxedPayloadOffsetBytes, payload);
        return write.IsSuccess ? new(HybridCpuManagedShapeStatusV1.Success, string.Empty, result.ObjectReference) : new(HybridCpuManagedShapeStatusV1.HeapFailure, write.Reason);
    }

    public byte[]? Unbox(ulong reference, ulong expectedTypeId)
    {
        byte[]? bytes = _heap.ReadObjectBytes(reference);
        if (bytes is null) return null;
        HybridCpuManagedTypeDescriptorV1? type = _types.ResolveTypeHandle(BinaryPrimitives.ReadUInt64LittleEndian(bytes));
        if (type?.TypeId != expectedTypeId || type.ValueTypeShape is not { } shape || shape.ObjectReferenceOffsets.Count != 0) return null;
        return bytes.AsSpan(shape.BoxedPayloadOffsetBytes, shape.PayloadSizeBytes).ToArray();
    }
}

public sealed class HybridCpuManagedTypeInitializerRuntimeV1
{
    private readonly HybridCpuManagedTypeSystemV1 _types;
    public HybridCpuManagedTypeInitializerRuntimeV1(HybridCpuManagedTypeSystemV1 types) => _types = types;

    public HybridCpuManagedShapeResultV1 EnsureInitialized(ulong typeId, Func<bool> initializer)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        return _types.InitializationState(typeId) switch
        {
            HybridCpuManagedTypeInitializationStateV1.Initialized => new(HybridCpuManagedShapeStatusV1.Success, string.Empty),
            HybridCpuManagedTypeInitializationStateV1.Running => new(HybridCpuManagedShapeStatusV1.Success, "Recursive same-context request observes initialization in progress."),
            HybridCpuManagedTypeInitializationStateV1.Failed => new(HybridCpuManagedShapeStatusV1.InitializationFailed, "Type initializer previously failed; failure is sticky."),
            _ => Run(typeId, initializer)
        };
    }

    private HybridCpuManagedShapeResultV1 Run(ulong typeId, Func<bool> initializer)
    {
        if (_types.Resolve(typeId) is null || !_types.TryBeginInitialization(typeId))
            return new(HybridCpuManagedShapeStatusV1.InvalidType, "Type initializer requires an exact runtime type identity.");
        bool succeeded;
        try { succeeded = initializer(); } catch { succeeded = false; }
        if (!_types.TryCompleteInitialization(typeId, succeeded))
            return new(HybridCpuManagedShapeStatusV1.InitializationFailed, "Type initialization state transition was rejected.");
        return succeeded ? new(HybridCpuManagedShapeStatusV1.Success, string.Empty) :
            new(HybridCpuManagedShapeStatusV1.InitializationFailed, "Type initializer failed; the type is permanently failed.");
    }
}

public sealed class HybridCpuManagedStaticFieldRuntimeV1
{
    private readonly HybridCpuManagedTypeSystemV1 _types;
    public HybridCpuManagedStaticFieldRuntimeV1(HybridCpuManagedTypeSystemV1 types) => _types = types;

    public HybridCpuManagedShapeResultV1 LoadInt32(ulong typeHandle, int offset) => Read(typeHandle, offset, 4, false);
    public HybridCpuManagedShapeResultV1 LoadReference(ulong typeHandle, int offset) => Read(typeHandle, offset, 8, true);
    public HybridCpuManagedShapeResultV1 StoreInt32(ulong typeHandle, int offset, int value) => Write(typeHandle, offset, 4, unchecked((ulong)(uint)value), false);
    public HybridCpuManagedShapeResultV1 StoreReference(ulong typeHandle, int offset, ulong value) => Write(typeHandle, offset, 8, value, true);

    private HybridCpuManagedShapeResultV1 Read(ulong handle, int offset, int size, bool reference)
    {
        if (!Field(handle, offset, size, reference, out HybridCpuManagedTypeDescriptorV1? type, out byte[]? storage))
            return new(HybridCpuManagedShapeStatusV1.InvalidType, "Static load does not match an exact runtime field layout.");
        long value = size == 4 ? BinaryPrimitives.ReadInt32LittleEndian(storage.AsSpan(offset, 4)) :
            unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(storage.AsSpan(offset, 8)));
        return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, 0, value);
    }

    private HybridCpuManagedShapeResultV1 Write(ulong handle, int offset, int size, ulong value, bool reference)
    {
        if (!Field(handle, offset, size, reference, out HybridCpuManagedTypeDescriptorV1? type, out byte[]? storage))
            return new(HybridCpuManagedShapeStatusV1.InvalidType, "Static store does not match an exact runtime field layout.");
        if (size == 4) BinaryPrimitives.WriteUInt32LittleEndian(storage.AsSpan(offset, 4), unchecked((uint)value));
        else BinaryPrimitives.WriteUInt64LittleEndian(storage.AsSpan(offset, 8), value);
        return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, 0, unchecked((long)value));
    }

    private bool Field(ulong handle, int offset, int size, bool reference,
        out HybridCpuManagedTypeDescriptorV1? type, out byte[]? storage)
    {
        type = _types.ResolveTypeHandle(handle); storage = type is null ? null : _types.StaticStorage(type.TypeId);
        HybridCpuManagedFieldLayoutV1? field = type?.StaticLayout.Fields.SingleOrDefault(row => row.OffsetBytes == offset);
        return storage is not null && field is not null && field.SizeBytes == size &&
            (field.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference) == reference &&
            offset >= 0 && offset <= storage.Length - size;
    }
}
