using System.Text;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.ManagedRuntime;

// Loads the class/interface subset of existing managed-type-metadata/v1.
// This installs managed runtime metadata/storage, not CPU execution or GC-root authority.
public static class HybridCpuManagedImageTypeLoaderV1
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private const string Schema = "hybridcpu.managed-type-metadata/v1";
    private const string ShapeSchema = "hybridcpu.managed-type-metadata/v2";

    public static HybridCpuManagedTypeSystemBuildV1 Load(ReadOnlyMemory<byte> image,
        IReadOnlyList<HybridCpuManagedTypeRegistrationV1> registrations,
        IReadOnlyDictionary<ulong, ulong> imageHandles)
    {
        try
        {
            if (registrations.Count == 0 || registrations.Count > HybridCpuPlatformContractV1.MaximumManagedTypes ||
                registrations.Select(row => row.TypeId).Distinct().Count() != registrations.Count)
                throw new InvalidDataException("Invalid image type registration count or duplicate TypeId.");
            List<HybridCpuManagedTypeDescriptorV1> descriptors = new List<HybridCpuManagedTypeDescriptorV1>();
            long total = 0, staticBytes = 0;
            List<(int Start, int End)> ranges = new List<(int Start, int End)>();
            foreach (HybridCpuManagedTypeRegistrationV1 registration in registrations)
            {
                int offset = registration.MetadataOffsetBytes, size = registration.MetadataSizeBytes;
                if (offset < 0 || size <= 0 || size > MaximumBytes || offset > image.Length - size ||
                    (total += size) > MaximumBytes || ranges.Any(range => offset < range.End && offset + size > range.Start))
                    throw new InvalidDataException("Image type metadata range is invalid, overlapping or over budget.");
                ranges.Add((offset, checked(offset + size)));
                using MemoryStream stream = new MemoryStream(image.Slice(offset, size).ToArray(), writable: false);
                using BinaryReader reader = new BinaryReader(stream, new UTF8Encoding(false, true));
                string Text()
                {
                    int count = Count(MaximumBytes);
                    if (count > stream.Length - stream.Position) throw new InvalidDataException("Truncated metadata text.");
                    return new UTF8Encoding(false, true).GetString(reader.ReadBytes(count));
                }
                int Count(int maximum)
                {
                    int count = reader.ReadInt32();
                    if (count < 0 || count > maximum) throw new InvalidDataException("Metadata count exceeds bounds.");
                    return count;
                }
                ulong[] Ids(int maximum)
                {
                    int count = Count(maximum);
                    if (count > (stream.Length - stream.Position) / 8) throw new InvalidDataException("Truncated metadata IDs.");
                    ulong[] ids = new ulong[count];
                    for (int i = 0; i < count; i++) ids[i] = reader.ReadUInt64();
                    return ids;
                }
                HybridCpuManagedFieldLayoutV1[] Fields()
                {
                    int count = Count(HybridCpuPlatformContractV1.MaximumManagedFieldsPerType);
                    HybridCpuManagedFieldLayoutV1[] fields = new HybridCpuManagedFieldLayoutV1[count];
                    for (int i = 0; i < count; i++) fields[i] = new(Text(), (HybridCpuManagedStorageKindV1)reader.ReadByte(),
                        reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                    return fields;
                }
                string actualSchema = Text();
                string actualContract = Text();
                int actualRowCount = reader.ReadInt32();
                bool shapeAware = actualSchema == ShapeSchema;
                if (actualSchema is not (Schema or ShapeSchema) || actualContract != HybridCpuPlatformContractV1.ContractDigest || actualRowCount != 1)
                    throw new InvalidDataException($"Unsupported metadata schema, contract or row count: " +
                        $"schema='{actualSchema}', contract='{actualContract}', expected-contract='{HybridCpuPlatformContractV1.ContractDigest}', rows={actualRowCount}.");
                ulong id = reader.ReadUInt64();
                string identity = Text();
                HybridCpuManagedTypeKindV1 kind = (HybridCpuManagedTypeKindV1)reader.ReadByte();
                ulong parent = reader.ReadUInt64();
                ulong[] interfaces = Ids(HybridCpuPlatformContractV1.MaximumManagedInterfacesPerType);
                int instanceSize = reader.ReadInt32(), instanceAlignment = reader.ReadInt32();
                HybridCpuManagedFieldLayoutV1[] fields = Fields();
                int staticSize = reader.ReadInt32(), staticAlignment = reader.ReadInt32();
                HybridCpuManagedFieldLayoutV1[] staticFields = Fields();
                int rootCount = Count(HybridCpuPlatformContractV1.MaximumManagedFieldsPerType);
                int[] roots = new int[rootCount];
                for (int i = 0; i < roots.Length; i++) roots[i] = reader.ReadInt32();
                ulong[] virtualSlots = Ids(HybridCpuPlatformContractV1.MaximumManagedVirtualSlotsPerType);
                ulong[] interfaceSlots = Ids(HybridCpuPlatformContractV1.MaximumManagedInterfaceSlotsPerType);
                HybridCpuManagedStringShapeV1? stringShape = null;
                HybridCpuManagedValueTypeShapeV1? valueTypeShape = null;
                HybridCpuManagedArrayShapeV1? arrayShape = null;
                if (shapeAware)
                {
                    byte shapeTag = reader.ReadByte();
                    if (shapeTag == 1)
                    {
                        HybridCpuManagedStorageKindV1 storage = (HybridCpuManagedStorageKindV1)reader.ReadByte();
                        ulong elementType = reader.ReadUInt64();
                        int elementSize = reader.ReadInt32();
                        int elementAlignment = reader.ReadInt32();
                        int lengthOffset = reader.ReadInt32();
                        int dataOffset = reader.ReadInt32();
                        byte szArray = reader.ReadByte(), storeCheck = reader.ReadByte();
                        if (szArray > 1 || storeCheck > 1) throw new InvalidDataException("Shape-aware array flags are non-canonical.");
                        arrayShape = new(storage, elementType == 0 ? null : elementType, elementSize,
                            elementAlignment, lengthOffset, dataOffset, szArray == 1, storeCheck == 1);
                    }
                    else if (shapeTag == 2)
                    {
                        int lengthOffset = reader.ReadInt32();
                        int dataOffset = reader.ReadInt32();
                        int characterSize = reader.ReadInt32();
                        byte immutable = reader.ReadByte();
                        if (immutable > 1) throw new InvalidDataException("Shape-aware String immutability flag is non-canonical.");
                        stringShape = new(lengthOffset, dataOffset, characterSize, immutable == 1);
                    }
                    else if (shapeTag == 3)
                    {
                        int payloadSize = reader.ReadInt32();
                        int payloadAlignment = reader.ReadInt32();
                        int referenceCount = Count(HybridCpuPlatformContractV1.MaximumManagedFieldsPerType);
                        int[] referenceOffsets = new int[referenceCount];
                        for (int i = 0; i < referenceOffsets.Length; i++) referenceOffsets[i] = reader.ReadInt32();
                        valueTypeShape = new(payloadSize, payloadAlignment, referenceOffsets, reader.ReadInt32());
                    }
                    else throw new InvalidDataException("Shape-aware metadata contains an unsupported shape tag.");
                }
                string digest = Text();
                HybridCpuManagedTypeDescriptorV1 descriptor = new HybridCpuManagedTypeDescriptorV1(HybridCpuManagedTypeDescriptorContractV1.SchemaId,
                    HybridCpuManagedTypeDescriptorContractV1.SchemaMajor, HybridCpuManagedTypeDescriptorContractV1.SchemaMinor,
                    id, identity, kind, parent == 0 ? null : parent, interfaces, instanceSize, instanceAlignment, fields,
                    new(staticSize, staticAlignment, staticFields, roots), virtualSlots, interfaceSlots, digest,
                    ArrayShape: arrayShape, StringShape: stringShape, ValueTypeShape: valueTypeShape);
                if (stream.Position != stream.Length || id == 0 || string.IsNullOrWhiteSpace(identity) ||
                    id != registration.TypeId || identity != registration.StableIdentity || digest != registration.DescriptorDigest ||
                    digest != HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(descriptor) ||
                    kind is not (HybridCpuManagedTypeKindV1.Class or HybridCpuManagedTypeKindV1.Interface or HybridCpuManagedTypeKindV1.String or HybridCpuManagedTypeKindV1.ValueType or HybridCpuManagedTypeKindV1.SzArray) ||
                    (kind == HybridCpuManagedTypeKindV1.SzArray && arrayShape is not { IsSzArray: true }) ||
                    (kind != HybridCpuManagedTypeKindV1.SzArray && arrayShape is not null) ||
                    (kind == HybridCpuManagedTypeKindV1.String && stringShape is not
                        { LengthOffsetBytes: 16, DataOffsetBytes: 20, CharacterSizeBytes: 2, IsImmutable: true }) ||
                    (kind != HybridCpuManagedTypeKindV1.String && stringShape is not null) ||
                    (kind == HybridCpuManagedTypeKindV1.ValueType && valueTypeShape is not
                        { PayloadSizeBytes: > 0, PayloadAlignmentBytes: 1 or 2 or 4 or 8 or 16, BoxedPayloadOffsetBytes: HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes }) ||
                    (kind != HybridCpuManagedTypeKindV1.ValueType && valueTypeShape is not null) ||
                    (valueTypeShape is not null && (valueTypeShape.PayloadSizeBytes != instanceSize ||
                        valueTypeShape.PayloadAlignmentBytes != instanceAlignment ||
                        !valueTypeShape.ObjectReferenceOffsets.SequenceEqual(
                            fields.Where(static field => field.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference)
                                .Select(static field => field.OffsetBytes)))) ||
                    !Layout(fields, instanceSize, instanceAlignment,
                        allowExactStringExtent: kind == HybridCpuManagedTypeKindV1.String && stringShape is
                            { LengthOffsetBytes: 16, DataOffsetBytes: 20, CharacterSizeBytes: 2, IsImmutable: true }) ||
                    !Layout(staticFields, staticSize, staticAlignment) ||
                    (kind is HybridCpuManagedTypeKindV1.Class or HybridCpuManagedTypeKindV1.String && (instanceSize < HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes ||
                        fields.Any(field => field.OffsetBytes < HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes))) ||
                    (kind == HybridCpuManagedTypeKindV1.Interface && (instanceSize != 0 || fields.Length != 0 || staticSize != 0 || parent != 0)) ||
                    !interfaces.SequenceEqual(interfaces.Distinct().Order()) || interfaces.Contains(0UL) ||
                    !roots.SequenceEqual(staticFields.Where(field => field.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference).Select(field => field.OffsetBytes)) ||
                    (staticBytes += staticSize) > MaximumBytes)
                    throw new InvalidDataException("Image type descriptor is inconsistent or outside supported layout bounds.");
                descriptors.Add(descriptor);
            }
            Dictionary<ulong, HybridCpuManagedTypeDescriptorV1> byId = descriptors.ToDictionary(row => row.TypeId);
            if (descriptors.Select(row => row.StableIdentity).Distinct(StringComparer.Ordinal).Count() != descriptors.Count ||
                descriptors.Any(row => !imageHandles.TryGetValue(row.TypeId, out ulong handle) || handle == 0 ||
                    handle > HybridCpuPlatformContractV1.MaximumManagedTypes) ||
                descriptors.Select(row => imageHandles[row.TypeId]).Distinct().Count() != descriptors.Count)
                throw new InvalidDataException("Image type handles or identities are missing, duplicated or invalid.");
            foreach (HybridCpuManagedTypeDescriptorV1 descriptor in descriptors)
            {
                foreach (ulong iface in descriptor.InterfaceTypeIds)
                    if (!byId.TryGetValue(iface, out HybridCpuManagedTypeDescriptorV1? target) || target.Kind != HybridCpuManagedTypeKindV1.Interface)
                        throw new InvalidDataException("Image interface dependency is absent or not an interface.");
                HashSet<ulong> visited = new HashSet<ulong>();
                HybridCpuManagedTypeDescriptorV1 current = descriptor;
                while (current.BaseTypeId is ulong parent)
                {
                    if (!visited.Add(current.TypeId) || !byId.TryGetValue(parent, out HybridCpuManagedTypeDescriptorV1? next) || next.Kind != HybridCpuManagedTypeKindV1.Class ||
                        current.Kind == HybridCpuManagedTypeKindV1.Class && current.InstanceSizeBytes < next.InstanceSizeBytes)
                        throw new InvalidDataException("Image base dependency is missing, cyclic or inconsistent.");
                    current = next;
                }
            }
            HybridCpuManagedTypeDescriptorV1[] ordered = descriptors.OrderBy(row => row.TypeId).ToArray();
            string resultDigest = HybridCpuPlatformContractV1.Hash(string.Join(';', ordered.Select(row =>
                $"{imageHandles[row.TypeId]}:{row.DescriptorDigest}")));
            return new(HybridCpuManagedTypeSystemStatusV1.Success, "", new(ordered, resultDigest, imageHandles), resultDigest);
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or ArgumentException or OverflowException)
        {
            return new(HybridCpuManagedTypeSystemStatusV1.InvalidMetadata, error.Message, null,
                HybridCpuPlatformContractV1.Hash("image-type-load-failed|" + error.Message));
        }
    }

    private static bool Layout(IReadOnlyList<HybridCpuManagedFieldLayoutV1> fields, int size, int alignment,
        bool allowExactStringExtent = false) =>
        size >= 0 && size <= MaximumBytes && alignment is 1 or 2 or 4 or 8 or 16 &&
        (size % alignment == 0 || allowExactStringExtent && size == 20 && alignment == 8) &&
        fields.Select(field => field.Identity).Distinct(StringComparer.Ordinal).Count() == fields.Count &&
        fields.All(field => !string.IsNullOrWhiteSpace(field.Identity) && Enum.IsDefined(field.StorageKind) &&
            field.SizeBytes > 0 && field.OffsetBytes >= 0 && field.AlignmentBytes is 1 or 2 or 4 or 8 or 16 &&
            field.OffsetBytes % field.AlignmentBytes == 0 && field.OffsetBytes <= size - field.SizeBytes &&
            (field.StorageKind != HybridCpuManagedStorageKindV1.ObjectReference || field.SizeBytes == 8 && field.AlignmentBytes == 8)) &&
        fields.OrderBy(field => field.OffsetBytes).Zip(fields.OrderBy(field => field.OffsetBytes).Skip(1))
            .All(pair => pair.First.OffsetBytes <= pair.Second.OffsetBytes - pair.First.SizeBytes);
}
