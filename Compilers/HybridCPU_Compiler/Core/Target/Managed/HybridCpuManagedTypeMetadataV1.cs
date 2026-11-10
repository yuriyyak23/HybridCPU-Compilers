using System.Buffers.Binary;
using System.Text;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public enum HybridCpuManagedTypeMetadataStatusV1 : byte
{
    Encoded = 0,
    InvalidInput = 1,
    BudgetExhausted = 2
}

public sealed record HybridCpuManagedTypeMetadataArtifactV1(
    HybridCpuManagedTypeMetadataStatusV1 Status,
    string Reason,
    byte[] Bytes,
    string Digest);

public sealed class HybridCpuManagedTypeMetadataEncoderV1
{
    public const string SchemaId = "hybridcpu.managed-type-metadata/v1";
    public const string ShapeSchemaId = "hybridcpu.managed-type-metadata/v2";
    public const int MaximumBytes = 4 * 1024 * 1024;

    public HybridCpuManagedTypeMetadataArtifactV1 Encode(IEnumerable<HybridCpuManagedTypeDescriptorV1> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        HybridCpuManagedTypeDescriptorV1[] ordered = descriptors.OrderBy(static descriptor => descriptor.TypeId).ToArray();
        if (ordered.Length > HybridCpuPlatformContractV1.MaximumManagedTypes)
            return Failure(HybridCpuManagedTypeMetadataStatusV1.BudgetExhausted, "Managed type metadata count budget was exhausted.");
        if (ordered.Select(static descriptor => descriptor.TypeId).Distinct().Count() != ordered.Length ||
            ordered.Any(static descriptor => !Valid(descriptor)))
            return Failure(HybridCpuManagedTypeMetadataStatusV1.InvalidInput, "Managed type descriptors are malformed or not contract-bound.");
        using MemoryStream stream = new MemoryStream();
        bool shapeAware = ordered.Any(static descriptor => descriptor.ArrayShape is not null || descriptor.StringShape is not null || descriptor.ValueTypeShape is not null);
        if (shapeAware && ordered.Any(static descriptor =>
            descriptor.ArrayShape is not null && (descriptor.Kind != HybridCpuManagedTypeKindV1.SzArray || descriptor.StringShape is not null || descriptor.ValueTypeShape is not null) ||
            descriptor.StringShape is not null && (descriptor.Kind != HybridCpuManagedTypeKindV1.String || descriptor.ArrayShape is not null || descriptor.ValueTypeShape is not null) ||
            descriptor.ValueTypeShape is not null && (descriptor.Kind != HybridCpuManagedTypeKindV1.ValueType || descriptor.ArrayShape is not null || descriptor.StringShape is not null) ||
            descriptor.ArrayShape is null && descriptor.StringShape is null && descriptor.ValueTypeShape is null))
            return Failure(HybridCpuManagedTypeMetadataStatusV1.InvalidInput,
                "Shape-aware metadata v2 requires one exact array, String or value-type shape per descriptor.");
        WriteText(stream, shapeAware ? ShapeSchemaId : SchemaId);
        WriteText(stream, HybridCpuPlatformContractV1.ContractDigest);
        WriteInt32(stream, ordered.Length);
        foreach (HybridCpuManagedTypeDescriptorV1 descriptor in ordered)
        {
            WriteUInt64(stream, descriptor.TypeId);
            WriteText(stream, descriptor.StableIdentity);
            stream.WriteByte((byte)descriptor.Kind);
            WriteUInt64(stream, descriptor.BaseTypeId ?? 0);
            WriteUInt64s(stream, descriptor.InterfaceTypeIds);
            WriteInt32(stream, descriptor.InstanceSizeBytes);
            WriteInt32(stream, descriptor.InstanceAlignmentBytes);
            WriteFields(stream, descriptor.InstanceFields);
            WriteInt32(stream, descriptor.StaticLayout.SizeBytes);
            WriteInt32(stream, descriptor.StaticLayout.AlignmentBytes);
            WriteFields(stream, descriptor.StaticLayout.Fields);
            WriteInt32s(stream, descriptor.StaticLayout.ObjectReferenceOffsets);
            WriteUInt64s(stream, descriptor.VirtualSlotMethodIds);
            WriteUInt64s(stream, descriptor.InterfaceSlotMethodIds);
            if (shapeAware)
            {
                if (descriptor.ArrayShape is HybridCpuManagedArrayShapeV1 arrayShape)
                {
                    stream.WriteByte(1);
                    stream.WriteByte((byte)arrayShape.ElementStorageKind);
                    WriteUInt64(stream, arrayShape.ElementTypeId ?? 0);
                    WriteInt32(stream, arrayShape.ElementSizeBytes);
                    WriteInt32(stream, arrayShape.ElementAlignmentBytes);
                    WriteInt32(stream, arrayShape.LengthOffsetBytes);
                    WriteInt32(stream, arrayShape.DataOffsetBytes);
                    stream.WriteByte(arrayShape.IsSzArray ? (byte)1 : (byte)0);
                    stream.WriteByte(arrayShape.RequiresReferenceStoreCheck ? (byte)1 : (byte)0);
                }
                else if (descriptor.StringShape is HybridCpuManagedStringShapeV1 stringShape)
                {
                    stream.WriteByte(2);
                    WriteInt32(stream, stringShape.LengthOffsetBytes);
                    WriteInt32(stream, stringShape.DataOffsetBytes);
                    WriteInt32(stream, stringShape.CharacterSizeBytes);
                    stream.WriteByte(stringShape.IsImmutable ? (byte)1 : (byte)0);
                }
                else
                {
                    HybridCpuManagedValueTypeShapeV1 valueShape = descriptor.ValueTypeShape!;
                    stream.WriteByte(3);
                    WriteInt32(stream, valueShape.PayloadSizeBytes);
                    WriteInt32(stream, valueShape.PayloadAlignmentBytes);
                    WriteInt32s(stream, valueShape.ObjectReferenceOffsets);
                    WriteInt32(stream, valueShape.BoxedPayloadOffsetBytes);
                }
            }
            WriteText(stream, descriptor.DescriptorDigest);
            if (stream.Length > MaximumBytes)
                return Failure(HybridCpuManagedTypeMetadataStatusV1.BudgetExhausted, "Managed type metadata byte budget was exhausted.");
        }
        byte[] bytes = stream.ToArray();
        return new(HybridCpuManagedTypeMetadataStatusV1.Encoded, string.Empty, bytes,
            HybridCpuPlatformContractV1.Hash(Convert.ToHexString(bytes)));
    }

    private static bool Valid(HybridCpuManagedTypeDescriptorV1 descriptor) =>
        descriptor is not null && descriptor.SchemaId == HybridCpuManagedTypeDescriptorContractV1.SchemaId &&
        descriptor.SchemaMajor == HybridCpuManagedTypeDescriptorContractV1.SchemaMajor &&
        descriptor.SchemaMinor <= HybridCpuManagedTypeDescriptorContractV1.SchemaMinor && descriptor.TypeId != 0 &&
        !string.IsNullOrWhiteSpace(descriptor.StableIdentity) && descriptor.InterfaceTypeIds is not null &&
        descriptor.InstanceFields is not null && descriptor.StaticLayout is not null &&
        descriptor.VirtualSlotMethodIds is not null && descriptor.InterfaceSlotMethodIds is not null &&
        descriptor.InstanceSizeBytes >= 0 && descriptor.InstanceAlignmentBytes is 1 or 2 or 4 or 8 or 16 &&
        (descriptor.InstanceSizeBytes % descriptor.InstanceAlignmentBytes == 0 ||
         descriptor.Kind == HybridCpuManagedTypeKindV1.String && descriptor.InstanceSizeBytes == 20 &&
         descriptor.StringShape is { LengthOffsetBytes: 16, DataOffsetBytes: 20, CharacterSizeBytes: 2, IsImmutable: true }) &&
        descriptor.InterfaceTypeIds.SequenceEqual(descriptor.InterfaceTypeIds.Order()) &&
        descriptor.InterfaceTypeIds.Distinct().Count() == descriptor.InterfaceTypeIds.Count &&
        ValidFields(descriptor.InstanceFields, descriptor.InstanceSizeBytes) &&
        ValidFields(descriptor.StaticLayout.Fields, descriptor.StaticLayout.SizeBytes) &&
        descriptor.StaticLayout.ObjectReferenceOffsets.SequenceEqual(
            descriptor.StaticLayout.Fields.Where(static field => field.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference)
                .Select(static field => field.OffsetBytes)) &&
        descriptor.DescriptorDigest == HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(descriptor);

    private static bool ValidFields(IReadOnlyList<HybridCpuManagedFieldLayoutV1> fields, int extent) =>
        fields.Count <= HybridCpuPlatformContractV1.MaximumManagedFieldsPerType &&
        fields.Select(static field => field.Identity).Distinct(StringComparer.Ordinal).Count() == fields.Count &&
        fields.All(field => !string.IsNullOrWhiteSpace(field.Identity) && field.OffsetBytes >= 0 &&
            Enum.IsDefined(field.StorageKind) &&
            (field.StorageKind != HybridCpuManagedStorageKindV1.ObjectReference || field.SizeBytes == 8 && field.AlignmentBytes == 8) &&
            field.SizeBytes > 0 && field.AlignmentBytes is 1 or 2 or 4 or 8 or 16 &&
            field.OffsetBytes % field.AlignmentBytes == 0 && field.OffsetBytes <= extent - field.SizeBytes);

    private static void WriteFields(Stream stream, IReadOnlyList<HybridCpuManagedFieldLayoutV1> fields)
    {
        WriteInt32(stream, fields.Count);
        foreach (HybridCpuManagedFieldLayoutV1 field in fields)
        {
            WriteText(stream, field.Identity);
            stream.WriteByte((byte)field.StorageKind);
            WriteInt32(stream, field.OffsetBytes);
            WriteInt32(stream, field.SizeBytes);
            WriteInt32(stream, field.AlignmentBytes);
        }
    }

    private static void WriteUInt64s(Stream stream, IReadOnlyList<ulong> values)
    {
        WriteInt32(stream, values.Count);
        foreach (ulong value in values) WriteUInt64(stream, value);
    }

    private static void WriteInt32s(Stream stream, IReadOnlyList<int> values)
    {
        WriteInt32(stream, values.Count);
        foreach (int value in values) WriteInt32(stream, value);
    }

    private static void WriteText(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static HybridCpuManagedTypeMetadataArtifactV1 Failure(HybridCpuManagedTypeMetadataStatusV1 status, string reason) =>
        new(status, reason, [], HybridCpuPlatformContractV1.Hash($"{SchemaId}|{status}|{reason}"));
}
