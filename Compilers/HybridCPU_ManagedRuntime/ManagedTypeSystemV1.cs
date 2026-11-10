using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.ManagedRuntime;

public sealed record HybridCpuManagedFieldDeclarationV1(
    string Identity,
    HybridCpuManagedStorageKindV1 StorageKind,
    int SizeBytes,
    int AlignmentBytes,
    bool IsStatic,
    int MetadataOrdinal);

public sealed record HybridCpuManagedTypeDeclarationV1(
    string StableIdentity,
    HybridCpuManagedTypeKindV1 Kind,
    string? BaseTypeIdentity,
    IReadOnlyList<string> InterfaceTypeIdentities,
    IReadOnlyList<HybridCpuManagedFieldDeclarationV1> Fields,
    HybridCpuManagedArrayShapeV1? ArrayShape = null,
    HybridCpuManagedStringShapeV1? StringShape = null,
    HybridCpuManagedValueTypeShapeV1? ValueTypeShape = null);

public enum HybridCpuManagedTypeSystemStatusV1 : byte
{
    Success = 0,
    InvalidMetadata = 1,
    MissingDependency = 2,
    CyclicDependency = 3,
    BudgetExhausted = 4
}

public sealed record HybridCpuManagedTypeSystemBuildV1(
    HybridCpuManagedTypeSystemStatusV1 Status,
    string Reason,
    HybridCpuManagedTypeSystemV1? TypeSystem,
    string Digest)
{
    public bool IsSuccess => Status == HybridCpuManagedTypeSystemStatusV1.Success;
}

public sealed class HybridCpuManagedTypeSystemV1
{
    private readonly IReadOnlyDictionary<ulong, HybridCpuManagedTypeDescriptorV1> _types;
    private readonly IReadOnlyDictionary<ulong, HybridCpuManagedTypeDescriptorV1> _typeHandles;
    private readonly IReadOnlyDictionary<ulong, ulong> _handlesByTypeId;
    private readonly Dictionary<ulong, byte[]> _staticStorage;
    private readonly Dictionary<ulong, HybridCpuManagedTypeInitializationStateV1> _initialization;

    internal HybridCpuManagedTypeSystemV1(IReadOnlyList<HybridCpuManagedTypeDescriptorV1> descriptors, string digest,
        IReadOnlyDictionary<ulong, ulong>? imageHandles = null)
    {
        Descriptors = descriptors;
        Digest = digest;
        _types = descriptors.ToDictionary(static descriptor => descriptor.TypeId);
        _typeHandles = descriptors.OrderBy(static descriptor => descriptor.TypeId).Select((descriptor, ordinal) =>
                (Handle: imageHandles is null ? checked((ulong)(ordinal + 1)) : imageHandles[descriptor.TypeId], Descriptor: descriptor))
            .ToDictionary(static row => row.Handle, static row => row.Descriptor);
        _handlesByTypeId = _typeHandles.ToDictionary(static row => row.Value.TypeId, static row => row.Key);
        _staticStorage = descriptors.ToDictionary(static descriptor => descriptor.TypeId,
            static descriptor => new byte[descriptor.StaticLayout.SizeBytes]);
        _initialization = descriptors.ToDictionary(static descriptor => descriptor.TypeId,
            static _ => HybridCpuManagedTypeInitializationStateV1.Uninitialized);
    }

    public IReadOnlyList<HybridCpuManagedTypeDescriptorV1> Descriptors { get; }
    public string Digest { get; }
    public bool HasExecutionAuthority => false;
    public bool HasObjectLifetimeAuthority => true;
    public bool HasGcAuthority => false;

    public HybridCpuManagedTypeDescriptorV1? Resolve(ulong typeId) =>
        _types.TryGetValue(typeId, out HybridCpuManagedTypeDescriptorV1? descriptor) ? descriptor : null;

    public HybridCpuManagedTypeDescriptorV1? ResolveTypeHandle(ulong typeHandle) =>
        _typeHandles.TryGetValue(typeHandle, out HybridCpuManagedTypeDescriptorV1? descriptor) ? descriptor : null;

    public ulong? TypeHandle(ulong typeId) =>
        _handlesByTypeId.TryGetValue(typeId, out ulong handle) ? handle : null;

    public bool IsAssignable(ulong sourceTypeId, ulong targetTypeId)
    {
        if (!_types.ContainsKey(sourceTypeId) || !_types.ContainsKey(targetTypeId)) return false;
        if (sourceTypeId == targetTypeId) return true;
        Queue<ulong> pending = new Queue<ulong>();
        HashSet<ulong> visited = new HashSet<ulong>();
        pending.Enqueue(sourceTypeId);
        while (pending.Count != 0 && visited.Count <= HybridCpuPlatformContractV1.MaximumManagedTypes)
        {
            ulong current = pending.Dequeue();
            if (!visited.Add(current) || !_types.TryGetValue(current, out HybridCpuManagedTypeDescriptorV1? descriptor)) continue;
            if (descriptor.BaseTypeId is ulong baseType)
            {
                if (baseType == targetTypeId) return true;
                pending.Enqueue(baseType);
            }
            foreach (ulong interfaceType in descriptor.InterfaceTypeIds)
            {
                if (interfaceType == targetTypeId) return true;
                pending.Enqueue(interfaceType);
            }
        }
        return false;
    }

    public byte[]? StaticStorage(ulong typeId) =>
        _staticStorage.TryGetValue(typeId, out byte[]? storage) ? storage : null;

    public HybridCpuManagedTypeInitializationStateV1 InitializationState(ulong typeId) =>
        _initialization.TryGetValue(typeId, out HybridCpuManagedTypeInitializationStateV1 state)
            ? state : HybridCpuManagedTypeInitializationStateV1.Failed;

    public bool TryBeginInitialization(ulong typeId) =>
        Transition(typeId, HybridCpuManagedTypeInitializationStateV1.Uninitialized,
            HybridCpuManagedTypeInitializationStateV1.Running);

    public bool TryCompleteInitialization(ulong typeId, bool succeeded) =>
        Transition(typeId, HybridCpuManagedTypeInitializationStateV1.Running, succeeded
            ? HybridCpuManagedTypeInitializationStateV1.Initialized
            : HybridCpuManagedTypeInitializationStateV1.Failed);

    private bool Transition(ulong typeId, HybridCpuManagedTypeInitializationStateV1 expected,
        HybridCpuManagedTypeInitializationStateV1 next)
    {
        if (!_initialization.TryGetValue(typeId, out HybridCpuManagedTypeInitializationStateV1 current) || current != expected)
            return false;
        _initialization[typeId] = next;
        return true;
    }
}

public sealed class HybridCpuManagedTypeSystemBuilderV1
{
    public HybridCpuManagedTypeSystemBuildV1 Build(IEnumerable<HybridCpuManagedTypeDeclarationV1> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        HybridCpuManagedTypeDeclarationV1[] input = declarations.ToArray();
        if (input.Length > HybridCpuPlatformContractV1.MaximumManagedTypes)
            return Failure(HybridCpuManagedTypeSystemStatusV1.BudgetExhausted, "Managed type budget was exhausted.");
        if (input.Any(static type => type is null || string.IsNullOrWhiteSpace(type.StableIdentity) ||
                type.InterfaceTypeIdentities is null || type.Fields is null) ||
            input.Select(static type => type.StableIdentity).Distinct(StringComparer.Ordinal).Count() != input.Length)
            return Failure(HybridCpuManagedTypeSystemStatusV1.InvalidMetadata, "Managed type identities must be present and unique.");

        Dictionary<string, HybridCpuManagedTypeDeclarationV1> byIdentity = input
            .ToDictionary(static type => type.StableIdentity, StringComparer.Ordinal);
        Dictionary<string, HybridCpuManagedTypeDescriptorV1> descriptors = new Dictionary<string, HybridCpuManagedTypeDescriptorV1>(StringComparer.Ordinal);
        HashSet<string> visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (string identity in byIdentity.Keys.Order(StringComparer.Ordinal))
        {
            HybridCpuManagedTypeSystemBuildV1? failure;
            try
            {
                failure = BuildOne(identity, byIdentity, descriptors, visiting);
            }
            catch (OverflowException)
            {
                return Failure(HybridCpuManagedTypeSystemStatusV1.InvalidMetadata,
                    $"Managed type layout for '{identity}' overflowed checked V1 bounds.");
            }
            if (failure is not null) return failure;
        }
        HybridCpuManagedTypeDescriptorV1[] ordered = descriptors.Values.OrderBy(static type => type.TypeId).ToArray();
        if (ordered.Select(static type => type.TypeId).Distinct().Count() != ordered.Length)
            return Failure(HybridCpuManagedTypeSystemStatusV1.InvalidMetadata, "Managed type identity hash collision detected.");
        HashSet<ulong> knownTypeIds = ordered.Select(static type => type.TypeId).ToHashSet();
        if (ordered.Any(type => type.ArrayShape is { ElementStorageKind: HybridCpuManagedStorageKindV1.ObjectReference, ElementTypeId: ulong element } &&
                !knownTypeIds.Contains(element)))
            return Failure(HybridCpuManagedTypeSystemStatusV1.MissingDependency,
                "A reference SZARRAY element TypeId is absent from the exact runtime type system.");
        if (ordered.Any(type => type.ValueTypeShape is { } shape &&
                !shape.ObjectReferenceOffsets.SequenceEqual(type.InstanceFields
                    .Where(static field => field.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference)
                    .Select(static field => field.OffsetBytes).Order())))
            return Failure(HybridCpuManagedTypeSystemStatusV1.InvalidMetadata,
                "A value-type pointer map does not exactly match its runtime field layout.");
        foreach (HybridCpuManagedTypeDescriptorV1 type in ordered)
        {
            if (type.ArrayShape is not { ElementStorageKind: HybridCpuManagedStorageKindV1.BlittableValue } array) continue;
            HybridCpuManagedTypeDescriptorV1? element = ordered.SingleOrDefault(candidate => candidate.TypeId == array.ElementTypeId);
            if (element is null)
                return Failure(HybridCpuManagedTypeSystemStatusV1.MissingDependency,
                    "A value SZARRAY element TypeId is absent from the exact runtime type system.");
            if (element.Kind != HybridCpuManagedTypeKindV1.ValueType ||
                element.ValueTypeShape is not { ObjectReferenceOffsets.Count: 0 } value ||
                value.PayloadSizeBytes != array.ElementSizeBytes || value.PayloadAlignmentBytes != array.ElementAlignmentBytes)
                return Failure(HybridCpuManagedTypeSystemStatusV1.InvalidMetadata,
                    "A blittable SZARRAY requires an exact reference-free value payload size and alignment.");
        }
        string digest = HybridCpuPlatformContractV1.Hash(string.Join('|', "hybridcpu.managed-type-system/v1",
            HybridCpuPlatformContractV1.ContractDigest, string.Join(';', ordered.Select(static type => type.DescriptorDigest))));
        return new(HybridCpuManagedTypeSystemStatusV1.Success, string.Empty,
            new HybridCpuManagedTypeSystemV1(ordered, digest), digest);
    }

    private static HybridCpuManagedTypeSystemBuildV1? BuildOne(string identity,
        IReadOnlyDictionary<string, HybridCpuManagedTypeDeclarationV1> declarations,
        IDictionary<string, HybridCpuManagedTypeDescriptorV1> descriptors,
        ISet<string> visiting)
    {
        if (descriptors.ContainsKey(identity)) return null;
        if (!declarations.TryGetValue(identity, out HybridCpuManagedTypeDeclarationV1? declaration))
            return Failure(HybridCpuManagedTypeSystemStatusV1.MissingDependency, $"Managed type dependency '{identity}' is absent.");
        if (!visiting.Add(identity))
            return Failure(HybridCpuManagedTypeSystemStatusV1.CyclicDependency, $"Managed type dependency cycle reaches '{identity}'.");
        if (declaration.Fields.Count > HybridCpuPlatformContractV1.MaximumManagedFieldsPerType ||
            declaration.InterfaceTypeIdentities.Count > HybridCpuPlatformContractV1.MaximumManagedInterfacesPerType)
            return Failure(HybridCpuManagedTypeSystemStatusV1.BudgetExhausted, $"Managed type '{identity}' exceeds a deterministic metadata budget.");

        if (declaration.BaseTypeIdentity is { } baseIdentity)
        {
            HybridCpuManagedTypeSystemBuildV1? failure = BuildOne(baseIdentity, declarations, descriptors, visiting);
            if (failure is not null) return failure;
        }
        foreach (string interfaceIdentity in declaration.InterfaceTypeIdentities.Order(StringComparer.Ordinal))
        {
            HybridCpuManagedTypeSystemBuildV1? failure = BuildOne(interfaceIdentity, declarations, descriptors, visiting);
            if (failure is not null) return failure;
            if (descriptors[interfaceIdentity].Kind != HybridCpuManagedTypeKindV1.Interface)
                return Failure(HybridCpuManagedTypeSystemStatusV1.InvalidMetadata, $"Implemented type '{interfaceIdentity}' is not an interface.");
        }
        HybridCpuManagedFieldDeclarationV1[] fields = declaration.Fields
            .OrderBy(static field => field.MetadataOrdinal).ThenBy(static field => field.Identity, StringComparer.Ordinal).ToArray();
        if (fields.Any(static field => string.IsNullOrWhiteSpace(field.Identity) || field.MetadataOrdinal < 0 ||
                field.SizeBytes <= 0 || field.AlignmentBytes is not (1 or 2 or 4 or 8 or 16) ||
                field.SizeBytes % field.AlignmentBytes != 0 ||
                field.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference &&
                (field.SizeBytes != 8 || field.AlignmentBytes != 8)) ||
            fields.Select(static field => field.Identity).Distinct(StringComparer.Ordinal).Count() != fields.Length ||
            fields.Select(static field => field.MetadataOrdinal).Distinct().Count() != fields.Length)
            return Failure(HybridCpuManagedTypeSystemStatusV1.InvalidMetadata, $"Managed fields for '{identity}' are malformed.");
        if (declaration.Kind == HybridCpuManagedTypeKindV1.Interface && fields.Length != 0)
            return Failure(HybridCpuManagedTypeSystemStatusV1.InvalidMetadata, "Interface fields are outside the Phase 02 type model.");
        if (!ValidShape(declaration))
            return Failure(HybridCpuManagedTypeSystemStatusV1.InvalidMetadata, $"Managed shape for '{identity}' is malformed or inconsistent with its kind.");

        int instanceOffset = declaration.Kind == HybridCpuManagedTypeKindV1.ValueType ? 0 :
            declaration.BaseTypeIdentity is { } parent
                ? descriptors[parent].InstanceSizeBytes : HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes;
        int instanceAlignment = HybridCpuPlatformContractV1.ManagedObjectAlignmentBytes;
        int staticOffset = 0;
        int staticAlignment = 1;
        List<HybridCpuManagedFieldLayoutV1> instanceFields = new List<HybridCpuManagedFieldLayoutV1>();
        List<HybridCpuManagedFieldLayoutV1> staticFields = new List<HybridCpuManagedFieldLayoutV1>();
        foreach (HybridCpuManagedFieldDeclarationV1 field in fields)
        {
            if (field.IsStatic)
            {
                staticOffset = Align(staticOffset, field.AlignmentBytes);
                staticFields.Add(new(field.Identity, field.StorageKind, staticOffset, field.SizeBytes, field.AlignmentBytes));
                staticOffset = checked(staticOffset + field.SizeBytes);
                staticAlignment = Math.Max(staticAlignment, field.AlignmentBytes);
            }
            else
            {
                instanceOffset = Align(instanceOffset, field.AlignmentBytes);
                instanceFields.Add(new(field.Identity, field.StorageKind, instanceOffset, field.SizeBytes, field.AlignmentBytes));
                instanceOffset = checked(instanceOffset + field.SizeBytes);
                instanceAlignment = Math.Max(instanceAlignment, field.AlignmentBytes);
            }
        }
        if (declaration.Kind == HybridCpuManagedTypeKindV1.Interface) instanceOffset = 0;
        if (declaration.ArrayShape is { } array) instanceOffset = array.DataOffsetBytes;
        if (declaration.StringShape is { } text) instanceOffset = text.DataOffsetBytes;
        if (declaration.ValueTypeShape is { } value) instanceOffset = value.PayloadSizeBytes;
        ulong typeId = TypeId(identity);
        HybridCpuManagedTypeDescriptorV1 draft = new HybridCpuManagedTypeDescriptorV1(
            HybridCpuManagedTypeDescriptorContractV1.SchemaId,
            HybridCpuManagedTypeDescriptorContractV1.SchemaMajor,
            HybridCpuManagedTypeDescriptorContractV1.SchemaMinor,
            typeId, identity, declaration.Kind,
            declaration.BaseTypeIdentity is { } baseType ? descriptors[baseType].TypeId : null,
            declaration.InterfaceTypeIdentities.Select(name => descriptors[name].TypeId).Order().ToArray(),
            Align(instanceOffset, instanceAlignment), instanceAlignment, instanceFields,
            new(Align(staticOffset, staticAlignment), staticAlignment, staticFields,
                staticFields.Where(static field => field.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference)
                    .Select(static field => field.OffsetBytes).ToArray()),
            [], [], string.Empty, declaration.ArrayShape, declaration.StringShape, declaration.ValueTypeShape);
        descriptors.Add(identity, draft with
        {
            DescriptorDigest = HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(draft)
        });
        visiting.Remove(identity);
        return null;
    }

    private static int Align(int value, int alignment) => checked((value + alignment - 1) / alignment * alignment);

    private static bool ValidShape(HybridCpuManagedTypeDeclarationV1 declaration)
    {
        int count = (declaration.ArrayShape is null ? 0 : 1) + (declaration.StringShape is null ? 0 : 1) +
            (declaration.ValueTypeShape is null ? 0 : 1);
        if (count != (declaration.Kind is HybridCpuManagedTypeKindV1.SzArray or HybridCpuManagedTypeKindV1.String or HybridCpuManagedTypeKindV1.ValueType ? 1 : 0))
            return false;
        if (declaration.Kind is HybridCpuManagedTypeKindV1.SzArray or HybridCpuManagedTypeKindV1.String or HybridCpuManagedTypeKindV1.ValueType && declaration.BaseTypeIdentity is not null)
            return false;
        if (declaration.ArrayShape is { } array)
            return Enum.IsDefined(array.ElementStorageKind) && array.IsSzArray && array.ElementSizeBytes > 0 && array.ElementAlignmentBytes is 1 or 2 or 4 or 8 or 16 &&
                array.ElementSizeBytes % array.ElementAlignmentBytes == 0 &&
                array.LengthOffsetBytes >= HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes &&
                array.DataOffsetBytes >= array.LengthOffsetBytes + sizeof(int) &&
                array.DataOffsetBytes % array.ElementAlignmentBytes == 0 &&
                (array.ElementStorageKind == HybridCpuManagedStorageKindV1.ObjectReference
                    ? array.ElementSizeBytes == 8 && array.ElementAlignmentBytes == 8 && array.ElementTypeId is not null && array.RequiresReferenceStoreCheck
                    : array.ElementStorageKind == HybridCpuManagedStorageKindV1.BlittableValue
                        ? array.ElementTypeId is not null && !array.RequiresReferenceStoreCheck
                        : array.ElementTypeId is null && !array.RequiresReferenceStoreCheck);
        if (declaration.StringShape is { } text)
            return text.CharacterSizeBytes == 2 && text.IsImmutable &&
                text.LengthOffsetBytes >= HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes &&
                text.DataOffsetBytes >= text.LengthOffsetBytes + sizeof(int);
        if (declaration.ValueTypeShape is { } value)
            return value.PayloadSizeBytes > 0 && value.PayloadAlignmentBytes is 1 or 2 or 4 or 8 or 16 &&
                value.PayloadSizeBytes % value.PayloadAlignmentBytes == 0 &&
                value.BoxedPayloadOffsetBytes >= HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes &&
                value.ObjectReferenceOffsets is not null && value.ObjectReferenceOffsets.SequenceEqual(value.ObjectReferenceOffsets.Order()) &&
                value.ObjectReferenceOffsets.Distinct().Count() == value.ObjectReferenceOffsets.Count &&
                value.ObjectReferenceOffsets.All(offset => offset >= 0 && offset % 8 == 0 && offset <= value.PayloadSizeBytes - 8);
        return true;
    }

    private static ulong TypeId(string identity)
    {
        ulong result = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"hybridcpu.managed-type-id/v1|{identity}")));
        return result == 0 ? 1 : result;
    }

    private static HybridCpuManagedTypeSystemBuildV1 Failure(HybridCpuManagedTypeSystemStatusV1 status, string reason) =>
        new(status, reason, null, HybridCpuPlatformContractV1.Hash($"hybridcpu.managed-type-system/failure/v1|{status}|{reason}"));
}
