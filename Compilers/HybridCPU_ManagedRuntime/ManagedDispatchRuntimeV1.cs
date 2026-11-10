using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.ManagedRuntime;

public enum HybridCpuManagedDispatchStatusV1 : byte
{
    Success = 0,
    InvalidMetadata = 1,
    MissingDependency = 2,
    BudgetExhausted = 3,
    NullReceiver = 4,
    InvalidObject = 5,
    MissingSlot = 6,
    InvalidCast = 7
}

public sealed record HybridCpuManagedMethodDeclarationV1(
    string StableIdentity,
    ulong DeclaringTypeId,
    string SignatureIdentity,
    ulong CodeAddress,
    int MetadataOrdinal,
    bool IsVirtual,
    bool IsNewSlot,
    ulong? OverrideSlotId = null);

public sealed record HybridCpuManagedInterfaceImplementationV1(
    ulong RuntimeTypeId,
    ulong InterfaceTypeId,
    ulong InterfaceSlotId,
    string ImplementationMethodIdentity);

public sealed record HybridCpuManagedDispatchTableBuildV1(
    HybridCpuManagedDispatchStatusV1 Status,
    string Reason,
    HybridCpuManagedDispatchTableV1? Table,
    string Digest)
{
    public bool IsSuccess => Status == HybridCpuManagedDispatchStatusV1.Success;
}

public sealed record HybridCpuManagedDispatchResultV1(
    HybridCpuManagedDispatchStatusV1 Status,
    string Reason,
    ulong ObjectReference,
    ulong RuntimeTypeId,
    ulong MethodId,
    ulong CodeAddress,
    string ResultDigest)
{
    public bool IsSuccess => Status == HybridCpuManagedDispatchStatusV1.Success;
}

public sealed class HybridCpuManagedDispatchTableV1
{
    internal HybridCpuManagedDispatchTableV1(
        IReadOnlyList<HybridCpuManagedMethodEntryV1> methods,
        IReadOnlyList<HybridCpuManagedVirtualDispatchEntryV1> virtualEntries,
        IReadOnlyList<HybridCpuManagedInterfaceDispatchEntryV1> interfaceEntries,
        string digest)
    {
        Methods = methods;
        VirtualEntries = virtualEntries;
        InterfaceEntries = interfaceEntries;
        Digest = digest;
        MethodsById = methods.ToDictionary(static row => row.MethodId);
        VirtualByKey = virtualEntries.ToDictionary(static row => (row.RuntimeTypeId, row.SlotId));
        InterfaceByKey = interfaceEntries.ToDictionary(static row =>
            (row.RuntimeTypeId, row.InterfaceTypeId, row.SlotId));
    }

    public IReadOnlyList<HybridCpuManagedMethodEntryV1> Methods { get; }
    public IReadOnlyList<HybridCpuManagedVirtualDispatchEntryV1> VirtualEntries { get; }
    public IReadOnlyList<HybridCpuManagedInterfaceDispatchEntryV1> InterfaceEntries { get; }
    public string Digest { get; }
    internal IReadOnlyDictionary<ulong, HybridCpuManagedMethodEntryV1> MethodsById { get; }
    internal IReadOnlyDictionary<(ulong Type, ulong Slot), HybridCpuManagedVirtualDispatchEntryV1> VirtualByKey { get; }
    internal IReadOnlyDictionary<(ulong Type, ulong Interface, ulong Slot), HybridCpuManagedInterfaceDispatchEntryV1> InterfaceByKey { get; }
    public bool HasExecutionAuthority => false;
}

public sealed class HybridCpuManagedDispatchTableBuilderV1
{
    public const string SchemaId = "hybridcpu.managed-dispatch-table/v1";

    public HybridCpuManagedDispatchTableBuildV1 Build(
        HybridCpuManagedTypeSystemV1 types,
        IEnumerable<HybridCpuManagedMethodDeclarationV1> methodDeclarations,
        IEnumerable<HybridCpuManagedInterfaceImplementationV1> interfaceImplementations)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(methodDeclarations);
        ArgumentNullException.ThrowIfNull(interfaceImplementations);
        HybridCpuManagedMethodDeclarationV1[] declarations = methodDeclarations.ToArray();
        HybridCpuManagedInterfaceImplementationV1[] implementations = interfaceImplementations.ToArray();
        if (declarations.Length > HybridCpuPlatformContractV1.MaximumManagedMethods)
            return Failure(HybridCpuManagedDispatchStatusV1.BudgetExhausted, "Managed method budget was exhausted.");
        if (declarations.Any(static row => row is null || string.IsNullOrWhiteSpace(row.StableIdentity) ||
                string.IsNullOrWhiteSpace(row.SignatureIdentity) || row.DeclaringTypeId == 0 || row.MetadataOrdinal < 0) ||
            declarations.Select(static row => row.StableIdentity).Distinct(StringComparer.Ordinal).Count() != declarations.Length)
            return Failure(HybridCpuManagedDispatchStatusV1.InvalidMetadata,
                "Method identities, signatures, declaring types and metadata ordinals must be exact and unique.");

        List<(HybridCpuManagedMethodDeclarationV1 Declaration, HybridCpuManagedMethodEntryV1 Entry)> methods = new List<(HybridCpuManagedMethodDeclarationV1 Declaration, HybridCpuManagedMethodEntryV1 Entry)>();
        foreach (HybridCpuManagedMethodDeclarationV1 declaration in declarations
                     .OrderBy(static row => row.DeclaringTypeId).ThenBy(static row => row.MetadataOrdinal)
                     .ThenBy(static row => row.StableIdentity, StringComparer.Ordinal))
        {
            HybridCpuManagedTypeDescriptorV1? owner = types.Resolve(declaration.DeclaringTypeId);
            if (owner is null)
                return Failure(HybridCpuManagedDispatchStatusV1.MissingDependency,
                    $"Declaring type for method '{declaration.StableIdentity}' is absent.");
            if (!declaration.IsVirtual && (declaration.IsNewSlot || declaration.OverrideSlotId is not null) ||
                declaration.IsVirtual && declaration.IsNewSlot == declaration.OverrideSlotId.HasValue ||
                owner.Kind != HybridCpuManagedTypeKindV1.Interface && declaration.CodeAddress == 0 ||
                declaration.CodeAddress != 0 && declaration.CodeAddress % 8 != 0)
                return Failure(HybridCpuManagedDispatchStatusV1.InvalidMetadata,
                    $"Method flags or code address for '{declaration.StableIdentity}' are inconsistent.");
            ulong methodId = ComputeMethodId(declaration.StableIdentity, declaration.SignatureIdentity);
            methods.Add((declaration, new(methodId, declaration.StableIdentity, declaration.DeclaringTypeId,
                declaration.SignatureIdentity, declaration.CodeAddress)));
        }
        if (methods.Select(static row => row.Entry.MethodId).Distinct().Count() != methods.Count)
            return Failure(HybridCpuManagedDispatchStatusV1.InvalidMetadata, "Managed method identity hash collision detected.");

        Dictionary<ulong, Dictionary<ulong, HybridCpuManagedVirtualDispatchEntryV1>> vtables = [];
        Dictionary<ulong, string> slotSignatures = [];
        Dictionary<(ulong Interface, ulong Slot), string> interfaceSlots = [];
        foreach (HybridCpuManagedTypeDescriptorV1 type in types.Descriptors
                     .OrderBy(type => InheritanceDepth(types, type)).ThenBy(static row => row.TypeId))
        {
            if (type.Kind == HybridCpuManagedTypeKindV1.Interface)
            {
                foreach ((HybridCpuManagedMethodDeclarationV1 Declaration, HybridCpuManagedMethodEntryV1 Entry) method in methods.Where(row => row.Declaration.DeclaringTypeId == type.TypeId))
                {
                    if (!method.Declaration.IsVirtual || !method.Declaration.IsNewSlot || method.Declaration.CodeAddress != 0)
                        return Failure(HybridCpuManagedDispatchStatusV1.InvalidMetadata,
                            "Interface methods must be abstract virtual new-slot declarations.");
                    ulong slot = ComputeSlotId(type.TypeId, method.Declaration.StableIdentity, method.Declaration.SignatureIdentity);
                    if (!interfaceSlots.TryAdd((type.TypeId, slot), method.Declaration.SignatureIdentity))
                        return Failure(HybridCpuManagedDispatchStatusV1.InvalidMetadata, "Interface slot identity collision detected.");
                }
                continue;
            }
            Dictionary<ulong, HybridCpuManagedVirtualDispatchEntryV1> table = type.BaseTypeId is ulong parent && vtables.TryGetValue(parent, out Dictionary<ulong, HybridCpuManagedVirtualDispatchEntryV1>? inherited)
                ? inherited.ToDictionary(static row => row.Key, row => row.Value with { RuntimeTypeId = type.TypeId })
                : new Dictionary<ulong, HybridCpuManagedVirtualDispatchEntryV1>();
            foreach ((HybridCpuManagedMethodDeclarationV1 Declaration, HybridCpuManagedMethodEntryV1 Entry) method in methods.Where(row => row.Declaration.DeclaringTypeId == type.TypeId && row.Declaration.IsVirtual)
                         .OrderBy(static row => row.Declaration.MetadataOrdinal).ThenBy(static row => row.Declaration.StableIdentity, StringComparer.Ordinal))
            {
                ulong slot = method.Declaration.IsNewSlot
                    ? ComputeSlotId(type.TypeId, method.Declaration.StableIdentity, method.Declaration.SignatureIdentity)
                    : method.Declaration.OverrideSlotId!.Value;
                if (method.Declaration.IsNewSlot)
                {
                    if (table.ContainsKey(slot) || !slotSignatures.TryAdd(slot, method.Declaration.SignatureIdentity))
                        return Failure(HybridCpuManagedDispatchStatusV1.InvalidMetadata, "Virtual new-slot identity collision detected.");
                }
                else if (!table.ContainsKey(slot) || !slotSignatures.TryGetValue(slot, out string? signature) ||
                         !string.Equals(signature, method.Declaration.SignatureIdentity, StringComparison.Ordinal))
                    return Failure(HybridCpuManagedDispatchStatusV1.InvalidMetadata,
                        $"Override '{method.Declaration.StableIdentity}' does not name an inherited signature-exact slot.");
                table[slot] = new(type.TypeId, slot, method.Entry.MethodId, method.Entry.CodeAddress);
            }
            if (table.Count > HybridCpuPlatformContractV1.MaximumManagedVirtualSlotsPerType)
                return Failure(HybridCpuManagedDispatchStatusV1.BudgetExhausted, "Per-type virtual-slot budget was exhausted.");
            vtables.Add(type.TypeId, table);
        }

        Dictionary<string, HybridCpuManagedMethodEntryV1> methodByIdentity = methods.ToDictionary(
            static row => row.Declaration.StableIdentity, static row => row.Entry, StringComparer.Ordinal);
        List<HybridCpuManagedInterfaceDispatchEntryV1> interfaceRows = new List<HybridCpuManagedInterfaceDispatchEntryV1>();
        foreach (HybridCpuManagedInterfaceImplementationV1 implementation in implementations
                     .OrderBy(static row => row.RuntimeTypeId).ThenBy(static row => row.InterfaceTypeId)
                     .ThenBy(static row => row.InterfaceSlotId).ThenBy(static row => row.ImplementationMethodIdentity, StringComparer.Ordinal))
        {
            HybridCpuManagedTypeDescriptorV1? runtimeType = types.Resolve(implementation.RuntimeTypeId);
            HybridCpuManagedTypeDescriptorV1? interfaceType = types.Resolve(implementation.InterfaceTypeId);
            if (runtimeType is null || interfaceType?.Kind != HybridCpuManagedTypeKindV1.Interface ||
                !types.IsAssignable(implementation.RuntimeTypeId, implementation.InterfaceTypeId) ||
                !interfaceSlots.TryGetValue((implementation.InterfaceTypeId, implementation.InterfaceSlotId), out string? signature) ||
                !methodByIdentity.TryGetValue(implementation.ImplementationMethodIdentity, out HybridCpuManagedMethodEntryV1? target) ||
                target.CodeAddress == 0 || !types.IsAssignable(implementation.RuntimeTypeId, target.DeclaringTypeId) ||
                !string.Equals(signature, target.SignatureIdentity, StringComparison.Ordinal))
                return Failure(HybridCpuManagedDispatchStatusV1.InvalidMetadata,
                    "An interface implementation is missing or inconsistent with runtime assignability and slot signature.");
            interfaceRows.Add(new(implementation.RuntimeTypeId, implementation.InterfaceTypeId,
                implementation.InterfaceSlotId, target.MethodId, target.CodeAddress));
        }
        if (interfaceRows.Select(static row => (row.RuntimeTypeId, row.InterfaceTypeId, row.SlotId)).Distinct().Count() != interfaceRows.Count)
            return Failure(HybridCpuManagedDispatchStatusV1.InvalidMetadata, "Interface dispatch keys must be unique.");
        Dictionary<(ulong RuntimeTypeId, ulong InterfaceTypeId, ulong SlotId), HybridCpuManagedInterfaceDispatchEntryV1> materializedInterfaces = interfaceRows.ToDictionary(
            static row => (row.RuntimeTypeId, row.InterfaceTypeId, row.SlotId));
        foreach (HybridCpuManagedTypeDescriptorV1 type in types.Descriptors
                     .Where(static row => row.Kind != HybridCpuManagedTypeKindV1.Interface)
                     .OrderBy(type => InheritanceDepth(types, type)).ThenBy(static row => row.TypeId))
        {
            if (type.BaseTypeId is not ulong parent) continue;
            foreach (HybridCpuManagedInterfaceDispatchEntryV1 inherited in materializedInterfaces.Values
                         .Where(row => row.RuntimeTypeId == parent).OrderBy(static row => row.InterfaceTypeId)
                         .ThenBy(static row => row.SlotId).ToArray())
            {
                HybridCpuManagedInterfaceDispatchEntryV1 effective = inherited with { RuntimeTypeId = type.TypeId };
                HybridCpuManagedVirtualDispatchEntryV1? parentVirtual = vtables[parent].Values
                    .SingleOrDefault(row => row.MethodId == inherited.MethodId);
                if (parentVirtual is not null && vtables[type.TypeId].TryGetValue(parentVirtual.SlotId,
                        out HybridCpuManagedVirtualDispatchEntryV1? overrideTarget))
                    effective = effective with { MethodId = overrideTarget.MethodId, CodeAddress = overrideTarget.CodeAddress };
                materializedInterfaces.TryAdd((type.TypeId, inherited.InterfaceTypeId, inherited.SlotId), effective);
            }
        }
        if (materializedInterfaces.Count > checked(types.Descriptors.Count * HybridCpuPlatformContractV1.MaximumManagedInterfaceSlotsPerType))
            return Failure(HybridCpuManagedDispatchStatusV1.BudgetExhausted,
                "Interface dispatch rows exceed deterministic budgets.");

        HybridCpuManagedMethodEntryV1[] methodRows = methods.Select(static row => row.Entry).OrderBy(static row => row.MethodId).ToArray();
        HybridCpuManagedVirtualDispatchEntryV1[] virtualRows = vtables.Values.SelectMany(static row => row.Values)
            .OrderBy(static row => row.RuntimeTypeId).ThenBy(static row => row.SlotId).ToArray();
        HybridCpuManagedInterfaceDispatchEntryV1[] orderedInterfaceRows = materializedInterfaces.Values
            .OrderBy(static row => row.RuntimeTypeId).ThenBy(static row => row.InterfaceTypeId).ThenBy(static row => row.SlotId).ToArray();
        string digest = HybridCpuPlatformContractV1.Hash(string.Join('|', SchemaId, types.Digest,
            string.Join(';', methodRows.Select(static row => $"{row.MethodId}:{row.StableIdentity}:{row.DeclaringTypeId}:{row.SignatureIdentity}:{row.CodeAddress}")),
            string.Join(';', virtualRows.Select(static row => $"{row.RuntimeTypeId}:{row.SlotId}:{row.MethodId}:{row.CodeAddress}")),
            string.Join(';', orderedInterfaceRows.Select(static row => $"{row.RuntimeTypeId}:{row.InterfaceTypeId}:{row.SlotId}:{row.MethodId}:{row.CodeAddress}"))));
        return new(HybridCpuManagedDispatchStatusV1.Success, string.Empty,
            new(methodRows, virtualRows, orderedInterfaceRows, digest), digest);
    }

    public static ulong ComputeMethodId(string stableIdentity, string signatureIdentity) =>
        NonZeroId($"hybridcpu.managed-method-id/v1|{stableIdentity}|{signatureIdentity}");

    public static ulong ComputeSlotId(ulong declaringTypeId, string stableIdentity, string signatureIdentity) =>
        NonZeroId($"hybridcpu.managed-slot-id/v1|{declaringTypeId}|{stableIdentity}|{signatureIdentity}");

    private static ulong NonZeroId(string text)
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return value == 0 ? 1 : value;
    }

    private static int InheritanceDepth(HybridCpuManagedTypeSystemV1 types, HybridCpuManagedTypeDescriptorV1 type)
    {
        int depth = 0;
        for (ulong? current = type.BaseTypeId; current is ulong parent; current = types.Resolve(parent)?.BaseTypeId)
            depth = checked(depth + 1);
        return depth;
    }

    private static HybridCpuManagedDispatchTableBuildV1 Failure(HybridCpuManagedDispatchStatusV1 status, string reason) =>
        new(status, reason, null, HybridCpuPlatformContractV1.Hash($"{SchemaId}|failure|{status}|{reason}"));
}

public sealed class HybridCpuManagedDispatchRuntimeV1
{
    public const string SchemaId = "hybridcpu.managed-dispatch-runtime/v1";
    private readonly HybridCpuManagedTypeSystemV1 _types;
    private readonly HybridCpuManagedHeapAllocatorV1 _heap;
    private readonly HybridCpuManagedDispatchTableV1 _table;

    public HybridCpuManagedDispatchRuntimeV1(HybridCpuManagedTypeSystemV1 types,
        HybridCpuManagedHeapAllocatorV1 heap, HybridCpuManagedDispatchTableV1 table)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _heap = heap ?? throw new ArgumentNullException(nameof(heap));
        _table = table ?? throw new ArgumentNullException(nameof(table));
        ContractDigest = HybridCpuPlatformContractV1.Hash($"{SchemaId}|{types.Digest}|{table.Digest}|jalr=generic|ise-authority=false");
    }

    public string ContractDigest { get; }
    public bool HasExecutionAuthority => false;
    public bool HasObjectLifetimeAuthority => false;

    public HybridCpuManagedDispatchResultV1 ResolveVirtual(ulong receiver, ulong slotId)
    {
        ObjectResolution resolved = ResolveObject(receiver);
        if (!resolved.IsSuccess) return Failure(resolved.Status, resolved.Reason, receiver);
        if (!_table.VirtualByKey.TryGetValue((resolved.TypeId, slotId), out HybridCpuManagedVirtualDispatchEntryV1? entry))
            return Failure(HybridCpuManagedDispatchStatusV1.MissingSlot, "Virtual slot is absent for the exact runtime type.", receiver, resolved.TypeId);
        return Success(receiver, resolved.TypeId, entry.MethodId, entry.CodeAddress, "Virtual slot resolved deterministically.");
    }

    public HybridCpuManagedDispatchResultV1 ResolveInterface(ulong receiver, ulong interfaceTypeId, ulong slotId)
    {
        ObjectResolution resolved = ResolveObject(receiver);
        if (!resolved.IsSuccess) return Failure(resolved.Status, resolved.Reason, receiver);
        if (!_types.IsAssignable(resolved.TypeId, interfaceTypeId) ||
            !_table.InterfaceByKey.TryGetValue((resolved.TypeId, interfaceTypeId, slotId), out HybridCpuManagedInterfaceDispatchEntryV1? entry))
            return Failure(HybridCpuManagedDispatchStatusV1.MissingSlot,
                "Interface slot is absent for the exact assignable runtime type.", receiver, resolved.TypeId);
        return Success(receiver, resolved.TypeId, entry.MethodId, entry.CodeAddress, "Interface slot resolved deterministically.");
    }

    public HybridCpuManagedDispatchResultV1 IsInstance(ulong receiver, ulong targetTypeId)
    {
        if (_types.Resolve(targetTypeId) is null)
            return Failure(HybridCpuManagedDispatchStatusV1.InvalidMetadata, "Type-test target is absent.", receiver);
        if (receiver == 0) return Success(0, 0, 0, 0, "isinst preserves a null receiver.");
        ObjectResolution resolved = ResolveObject(receiver);
        if (!resolved.IsSuccess) return Failure(resolved.Status, resolved.Reason, receiver);
        return _types.IsAssignable(resolved.TypeId, targetTypeId)
            ? Success(receiver, resolved.TypeId, 0, 0, "isinst accepted the exact runtime type.")
            : Success(0, resolved.TypeId, 0, 0, "isinst returned null for a non-assignable runtime type.");
    }

    public HybridCpuManagedDispatchResultV1 CastClass(ulong receiver, ulong targetTypeId)
    {
        HybridCpuManagedDispatchResultV1 test = IsInstance(receiver, targetTypeId);
        if (!test.IsSuccess || receiver == 0 || test.ObjectReference != 0) return test;
        return Failure(HybridCpuManagedDispatchStatusV1.InvalidCast,
            "castclass rejected a non-assignable runtime type.", receiver, test.RuntimeTypeId);
    }

    private ObjectResolution ResolveObject(ulong receiver)
    {
        if (receiver == 0) return new(false, HybridCpuManagedDispatchStatusV1.NullReceiver, "Managed dispatch receiver is null.", 0);
        byte[]? bytes = _heap.ReadObjectBytes(receiver);
        if (bytes is null || bytes.Length < HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes)
            return new(false, HybridCpuManagedDispatchStatusV1.InvalidObject, "Receiver is not an active managed object.", 0);
        ulong handle = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        HybridCpuManagedTypeDescriptorV1? type = _types.ResolveTypeHandle(handle);
        return type is null
            ? new(false, HybridCpuManagedDispatchStatusV1.InvalidObject, "Receiver type handle is invalid.", 0)
            : new(true, HybridCpuManagedDispatchStatusV1.Success, string.Empty, type.TypeId);
    }

    private static HybridCpuManagedDispatchResultV1 Success(ulong receiver, ulong typeId, ulong methodId,
        ulong codeAddress, string reason) => Result(HybridCpuManagedDispatchStatusV1.Success, reason, receiver, typeId, methodId, codeAddress);

    private static HybridCpuManagedDispatchResultV1 Failure(HybridCpuManagedDispatchStatusV1 status, string reason,
        ulong receiver, ulong typeId = 0) => Result(status, reason, receiver, typeId, 0, 0);

    private static HybridCpuManagedDispatchResultV1 Result(HybridCpuManagedDispatchStatusV1 status, string reason,
        ulong receiver, ulong typeId, ulong methodId, ulong codeAddress) =>
        new(status, reason, receiver, typeId, methodId, codeAddress,
            HybridCpuPlatformContractV1.Hash($"{SchemaId}|{status}|{reason}|{receiver}|{typeId}|{methodId}|{codeAddress}"));

    private sealed record ObjectResolution(bool IsSuccess, HybridCpuManagedDispatchStatusV1 Status, string Reason, ulong TypeId);
}
