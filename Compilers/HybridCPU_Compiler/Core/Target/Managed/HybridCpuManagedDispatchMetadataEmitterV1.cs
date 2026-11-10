using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public sealed record HybridCpuManagedMethodSymbolBindingV1(
    ulong MethodId,
    string StableIdentity,
    string SymbolName);

public sealed record HybridCpuManagedDispatchMetadataArtifactV1(
    HybridCpuPlatformFactStatus Status,
    string Reason,
    HybridCpuObjectSectionV1? Section,
    IReadOnlyList<HybridCpuObjectRelocationV1> Relocations,
    string Digest)
{
    public bool IsSuccess => Status == HybridCpuPlatformFactStatus.Supported;
    public bool HasLinkAuthority => false;
    public bool HasIseExecutionAuthority => false;
}

/// <summary>
/// Emits deterministic POD dispatch rows with ordinary HCO Absolute64 relocations.
/// The static linker owns address resolution; ManagedRuntime owns lookup semantics.
/// </summary>
public sealed class HybridCpuManagedDispatchMetadataEmitterV1
{
    public const string SchemaId = "hybridcpu.managed-dispatch-metadata/v1";
    public const string SectionName = ".hcmd";
    public const int HeaderBytes = 24;
    public const int MethodRowBytes = 24;
    public const int VirtualRowBytes = 32;
    public const int InterfaceRowBytes = 40;
    public const int MethodCountOffset = 12;
    public const int VirtualCountOffset = 16;
    public const int InterfaceCountOffset = 20;
    public const int InterfaceRuntimeTypeOffset = 0;
    public const int InterfaceTypeOffset = 8;
    public const int InterfaceSlotOffset = 16;
    public const int InterfaceCodeAddressOffset = 32;
    public const string Symbol = "__hybridcpu_managed_dispatch_metadata";

    public HybridCpuManagedDispatchMetadataArtifactV1 Emit(
        IEnumerable<HybridCpuManagedMethodSymbolBindingV1> methodBindings,
        IEnumerable<HybridCpuManagedVirtualDispatchEntryV1> virtualEntries,
        IEnumerable<HybridCpuManagedInterfaceDispatchEntryV1> interfaceEntries)
    {
        ArgumentNullException.ThrowIfNull(methodBindings);
        ArgumentNullException.ThrowIfNull(virtualEntries);
        ArgumentNullException.ThrowIfNull(interfaceEntries);
        HybridCpuManagedMethodSymbolBindingV1[] methods = methodBindings.OrderBy(static row => row.MethodId).ToArray();
        HybridCpuManagedVirtualDispatchEntryV1[] virtuals = virtualEntries
            .OrderBy(static row => row.RuntimeTypeId).ThenBy(static row => row.SlotId).ToArray();
        HybridCpuManagedInterfaceDispatchEntryV1[] interfaces = interfaceEntries
            .OrderBy(static row => row.RuntimeTypeId).ThenBy(static row => row.InterfaceTypeId)
            .ThenBy(static row => row.SlotId).ToArray();
        if (methods.Length > HybridCpuPlatformContractV1.MaximumManagedMethods ||
            virtuals.Length > HybridCpuPlatformContractV1.MaximumManagedTypes * HybridCpuPlatformContractV1.MaximumManagedVirtualSlotsPerType ||
            interfaces.Length > HybridCpuPlatformContractV1.MaximumManagedTypes * HybridCpuPlatformContractV1.MaximumManagedInterfaceSlotsPerType)
            return Failure(HybridCpuPlatformFactStatus.Unsupported, "Dispatch metadata exceeds deterministic budgets.");
        if (methods.Any(static row => row is null || row.MethodId == 0 || string.IsNullOrWhiteSpace(row.StableIdentity) ||
                string.IsNullOrWhiteSpace(row.SymbolName)) ||
            methods.Select(static row => row.MethodId).Distinct().Count() != methods.Length ||
            methods.Select(static row => row.StableIdentity).Distinct(StringComparer.Ordinal).Count() != methods.Length ||
            methods.Select(static row => row.SymbolName).Distinct(StringComparer.Ordinal).Count() != methods.Length ||
            virtuals.Any(static row => row.RuntimeTypeId == 0 || row.SlotId == 0 || row.MethodId == 0) ||
            interfaces.Any(static row => row.RuntimeTypeId == 0 || row.InterfaceTypeId == 0 || row.SlotId == 0 || row.MethodId == 0) ||
            virtuals.Select(static row => (row.RuntimeTypeId, row.SlotId)).Distinct().Count() != virtuals.Length ||
            interfaces.Select(static row => (row.RuntimeTypeId, row.InterfaceTypeId, row.SlotId)).Distinct().Count() != interfaces.Length)
            return Failure(HybridCpuPlatformFactStatus.Invalid, "Dispatch metadata identities and keys must be non-zero and unique.");
        Dictionary<ulong, HybridCpuManagedMethodSymbolBindingV1> symbols = methods.ToDictionary(static row => row.MethodId);
        if (virtuals.Concat(interfaces.Select(static row => new HybridCpuManagedVirtualDispatchEntryV1(
                row.RuntimeTypeId, row.SlotId, row.MethodId, row.CodeAddress))).Any(row => !symbols.ContainsKey(row.MethodId)))
            return Failure(HybridCpuPlatformFactStatus.Invalid, "Every dispatch row requires one exact method-symbol binding.");

        int length = checked(HeaderBytes + methods.Length * MethodRowBytes +
            virtuals.Length * VirtualRowBytes + interfaces.Length * InterfaceRowBytes);
        byte[] bytes = new byte[length];
        "HCDP0001"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), methods.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), virtuals.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), interfaces.Length);
        List<HybridCpuObjectRelocationV1> relocations = new List<HybridCpuObjectRelocationV1>(checked(methods.Length + virtuals.Length + interfaces.Length));
        int offset = HeaderBytes;
        foreach (HybridCpuManagedMethodSymbolBindingV1 method in methods)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), method.MethodId);
            SHA256.HashData(Encoding.UTF8.GetBytes(method.StableIdentity)).AsSpan(0, 8).CopyTo(bytes.AsSpan(offset + 8));
            relocations.Add(new(SectionName, checked((ulong)(offset + 16)), HybridCpuRelocationKind.Absolute64,
                method.SymbolName, 0));
            offset += MethodRowBytes;
        }
        foreach (HybridCpuManagedVirtualDispatchEntryV1 row in virtuals)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), row.RuntimeTypeId);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 8), row.SlotId);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 16), row.MethodId);
            relocations.Add(new(SectionName, checked((ulong)(offset + 24)), HybridCpuRelocationKind.Absolute64,
                symbols[row.MethodId].SymbolName, 0));
            offset += VirtualRowBytes;
        }
        foreach (HybridCpuManagedInterfaceDispatchEntryV1 row in interfaces)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), row.RuntimeTypeId);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 8), row.InterfaceTypeId);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 16), row.SlotId);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 24), row.MethodId);
            relocations.Add(new(SectionName, checked((ulong)(offset + 32)), HybridCpuRelocationKind.Absolute64,
                symbols[row.MethodId].SymbolName, 0));
            offset += InterfaceRowBytes;
        }
        HybridCpuObjectRelocationV1[] orderedRelocations = relocations.OrderBy(static row => row.Offset).ToArray();
        string digest = HybridCpuPlatformContractV1.Hash(string.Join('|', SchemaId,
            HybridCpuPlatformContractV1.ContractDigest, Convert.ToHexString(bytes),
            string.Join(';', orderedRelocations.Select(static row => $"{row.Offset}:{row.TargetSymbol}:{row.Addend}"))));
        return new(HybridCpuPlatformFactStatus.Supported, string.Empty,
            new(SectionName, HybridCpuObjectSectionKind.ReadOnlyData, 8, bytes, checked((ulong)bytes.Length)),
            orderedRelocations, digest);
    }

    private static HybridCpuManagedDispatchMetadataArtifactV1 Failure(HybridCpuPlatformFactStatus status, string reason) =>
        new(status, reason, null, [], HybridCpuPlatformContractV1.Hash($"{SchemaId}|failure|{status}|{reason}"));
}
