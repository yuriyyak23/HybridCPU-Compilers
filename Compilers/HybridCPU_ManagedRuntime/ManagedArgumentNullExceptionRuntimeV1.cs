using System.Buffers.Binary;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.ManagedRuntime;

/// <summary>
/// Invariant-resource ArgumentNullException(string paramName) contract. All exception fields and
/// UTF-16 values live in the managed heap; no host Exception instance or exception side table is used.
/// Other constructors and localized resources are not implied by this bounded contract.
/// </summary>
public sealed class HybridCpuManagedArgumentNullExceptionRuntimeV1
{
    public const int NullArgumentHResult = unchecked((int)0x80004003);
    public const string InvariantNullMessage = "Value cannot be null.";
    public const int ArgumentOutOfRangeHResult = unchecked((int)0x80131502);
    public const string InvariantOutOfRangeMessage = "Specified argument was out of the range of valid values.";
    private readonly HybridCpuManagedTypeSystemV1 _types;
    private readonly HybridCpuManagedHeapAllocatorV1 _heap;
    private readonly ulong _exceptionTypeId;
    private readonly ulong _stringHandle;
    private readonly HybridCpuManagedStringShapeV1 _stringShape;
    private readonly int _messageOffset, _parameterOffset, _hresultOffset;
    private readonly string _exceptionTypeIdentity, _defaultMessage;
    private readonly int _defaultHResult;

    public HybridCpuManagedArgumentNullExceptionRuntimeV1(HybridCpuManagedTypeSystemV1 types,
        HybridCpuManagedHeapAllocatorV1 heap, ulong argumentNullTypeId, ulong stringHandle)
        : this(types, heap, argumentNullTypeId, stringHandle, "System.ArgumentNullException",
            InvariantNullMessage, NullArgumentHResult)
    {
    }

    public static HybridCpuManagedArgumentNullExceptionRuntimeV1 ForArgumentOutOfRange(
        HybridCpuManagedTypeSystemV1 types, HybridCpuManagedHeapAllocatorV1 heap,
        ulong argumentOutOfRangeTypeId, ulong stringHandle) =>
        new(types, heap, argumentOutOfRangeTypeId, stringHandle, "System.ArgumentOutOfRangeException",
            InvariantOutOfRangeMessage, ArgumentOutOfRangeHResult);

    private HybridCpuManagedArgumentNullExceptionRuntimeV1(HybridCpuManagedTypeSystemV1 types,
        HybridCpuManagedHeapAllocatorV1 heap, ulong exceptionTypeId, ulong stringHandle,
        string exceptionTypeIdentity, string defaultMessage, int defaultHResult)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _heap = heap ?? throw new ArgumentNullException(nameof(heap));
        _exceptionTypeId = exceptionTypeId;
        _stringHandle = stringHandle;
        _exceptionTypeIdentity = exceptionTypeIdentity;
        _defaultMessage = defaultMessage;
        _defaultHResult = defaultHResult;
        HybridCpuManagedTypeDescriptorV1? type = types.Resolve(exceptionTypeId);
        if (type?.StableIdentity != exceptionTypeIdentity || type.Kind != HybridCpuManagedTypeKindV1.Class ||
            types.ResolveTypeHandle(stringHandle) is not { Kind: HybridCpuManagedTypeKindV1.String,
                StringShape: { CharacterSizeBytes: 2, IsImmutable: true } shape })
            throw new ArgumentException("Exact ArgumentException-family and immutable UTF-16 descriptors are required.");
        _stringShape = shape;
        _messageOffset = Field("System.Exception", "_message", HybridCpuManagedStorageKindV1.ObjectReference, 8);
        _parameterOffset = Field("System.ArgumentException", "_paramName", HybridCpuManagedStorageKindV1.ObjectReference, 8);
        _hresultOffset = Field("System.Exception", "_HResult", HybridCpuManagedStorageKindV1.Primitive, 4);
    }

    private int Field(string owner, string name, HybridCpuManagedStorageKindV1 kind, int size)
    {
        HybridCpuManagedTypeDescriptorV1? declaring = _types.Descriptors.SingleOrDefault(type => type.StableIdentity == owner);
        HybridCpuManagedFieldLayoutV1? field = declaring?.InstanceFields.SingleOrDefault(field => field.Identity == name);
        if (declaring is null || !_types.IsAssignable(_exceptionTypeId, declaring.TypeId) || field is null ||
            field.StorageKind != kind || field.SizeBytes != size || field.AlignmentBytes != size ||
            field.OffsetBytes < HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes ||
            field.OffsetBytes > _types.Resolve(_exceptionTypeId)!.InstanceSizeBytes - size)
            throw new ArgumentException($"Missing exact exception field {owner}::{name}.");
        return field.OffsetBytes;
    }

    public HybridCpuManagedShapeResultV1 Construct(ulong receiver, ulong paramName)
    {
        if (!Receiver(receiver, out byte[]? bytes)) return Invalid(receiver);
        if (paramName != 0 && !ReadString(paramName, out _))
            return new(HybridCpuManagedShapeStatusV1.InvalidType, "paramName must be null or an exact runtime string.");
        HybridCpuManagedShapeResultV1 message = MakeString(_defaultMessage);
        if (!message.IsSuccess) return message;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(_messageOffset, 8), message.ObjectReference);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(_parameterOffset, 8), paramName);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(_hresultOffset, 4), _defaultHResult);
        HybridCpuManagedHeapResultV1 write = _heap.WriteObjectBytes(receiver, 16, bytes.AsSpan(16));
        return write.IsSuccess ? new(HybridCpuManagedShapeStatusV1.Success, string.Empty, receiver)
            : new(HybridCpuManagedShapeStatusV1.HeapFailure, write.Reason);
    }

    public HybridCpuManagedShapeResultV1 ParamName(ulong receiver) => ReadField(receiver, _parameterOffset);

    public HybridCpuManagedShapeResultV1 HResult(ulong receiver) => Receiver(receiver, out byte[]? bytes)
        ? new(HybridCpuManagedShapeStatusV1.Success, string.Empty, receiver,
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(_hresultOffset, 4))) : Invalid(receiver);

    public HybridCpuManagedShapeResultV1 Message(ulong receiver)
    {
        if (!Receiver(receiver, out byte[]? bytes)) return Invalid(receiver);
        ulong message = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(_messageOffset, 8));
        ulong parameter = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(_parameterOffset, 8));
        if (!ReadString(message, out string? text) || parameter != 0 && !ReadString(parameter, out _))
            return new(HybridCpuManagedShapeStatusV1.InvalidType, "Exception contains an invalid string field.");
        if (parameter == 0) return new(HybridCpuManagedShapeStatusV1.Success, string.Empty, message);
        ReadString(parameter, out string? name);
        return name!.Length == 0 ? new(HybridCpuManagedShapeStatusV1.Success, string.Empty, message)
            : MakeString(text + " (Parameter '" + name + "')");
    }

    private HybridCpuManagedShapeResultV1 ReadField(ulong receiver, int offset)
    {
        if (!Receiver(receiver, out byte[]? bytes)) return Invalid(receiver);
        ulong reference = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
        return reference == 0 || ReadString(reference, out _)
            ? new(HybridCpuManagedShapeStatusV1.Success, string.Empty, reference)
            : new(HybridCpuManagedShapeStatusV1.InvalidType, "Exception field is not a runtime string.");
    }

    private bool Receiver(ulong receiver, out byte[] bytes)
    {
        bytes = _heap.ReadObjectBytes(receiver)!;
        if (bytes is null || bytes.Length < 16) return false;
        HybridCpuManagedTypeDescriptorV1? actual = _types.ResolveTypeHandle(BinaryPrimitives.ReadUInt64LittleEndian(bytes));
        return actual is not null && bytes.Length >= actual.InstanceSizeBytes &&
            _types.IsAssignable(actual.TypeId, _exceptionTypeId);
    }

    private bool ReadString(ulong reference, out string? value)
    {
        value = null;
        byte[]? bytes = _heap.ReadObjectBytes(reference);
        if (bytes is null || bytes.Length < _stringShape.DataOffsetBytes ||
            BinaryPrimitives.ReadUInt64LittleEndian(bytes) != _stringHandle) return false;
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(_stringShape.LengthOffsetBytes, 4));
        if (length < 0 || length > (bytes.Length - _stringShape.DataOffsetBytes) / 2) return false;
        char[] chars = new char[length];
        for (int index = 0; index < length; index++)
            chars[index] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(_stringShape.DataOffsetBytes + index * 2, 2));
        value = new string(chars);
        return true;
    }

    private HybridCpuManagedShapeResultV1 MakeString(string value)
    {
        int size;
        try { size = checked(_stringShape.DataOffsetBytes + value.Length * 2); }
        catch (OverflowException) { return new(HybridCpuManagedShapeStatusV1.SizeOverflow, "Exception message size overflow."); }
        size = Math.Max(size, _types.ResolveTypeHandle(_stringHandle)!.InstanceSizeBytes);
        HybridCpuManagedHeapResultV1 allocated = _heap.AllocateVariable(_stringHandle, size);
        if (!allocated.IsSuccess) return new(HybridCpuManagedShapeStatusV1.HeapFailure, allocated.Reason);
        byte[] bytes = new byte[size - 16];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(_stringShape.LengthOffsetBytes - 16, 4), value.Length);
        for (int index = 0; index < value.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(_stringShape.DataOffsetBytes - 16 + index * 2, 2), value[index]);
        HybridCpuManagedHeapResultV1 write = _heap.WriteObjectBytes(allocated.ObjectReference, 16, bytes);
        return write.IsSuccess ? new(HybridCpuManagedShapeStatusV1.Success, string.Empty, allocated.ObjectReference)
            : new(HybridCpuManagedShapeStatusV1.HeapFailure, write.Reason);
    }

    private HybridCpuManagedShapeResultV1 Invalid(ulong reference) =>
        new(reference == 0 ? HybridCpuManagedShapeStatusV1.NullReference : HybridCpuManagedShapeStatusV1.InvalidType,
            "Receiver must be a runtime-owned object assignable to the configured " + _exceptionTypeIdentity + ".");
}
