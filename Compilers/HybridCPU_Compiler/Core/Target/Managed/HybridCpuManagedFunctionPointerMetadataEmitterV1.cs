using System.Buffers.Binary;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public sealed record HybridCpuManagedFunctionPointerSymbolBindingV1(
    ulong MethodId,
    ulong SignatureId,
    string CodeSymbolName,
    bool IsInstanceMethod,
    string InvocationThunkSymbolName);

public sealed record HybridCpuManagedFunctionPointerMetadataArtifactV1(
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
/// Emits deterministic managed function-pointer POD rows. The linker owns both
/// code-address relocations; ManagedRuntime owns signature and delegate semantics.
/// </summary>
public sealed class HybridCpuManagedFunctionPointerMetadataEmitterV1
{
    public const string SchemaId = "hybridcpu.managed-function-pointer-metadata/v1";
    public const string SectionName = ".hcfp";
    private const int HeaderBytes = 16;
    private const int RowBytes = 40;

    public HybridCpuManagedFunctionPointerMetadataArtifactV1 Emit(
        IEnumerable<HybridCpuManagedFunctionPointerSymbolBindingV1> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        HybridCpuManagedFunctionPointerSymbolBindingV1[] rows = bindings
            .OrderBy(static row => row.MethodId).ThenBy(static row => row.SignatureId).ToArray();
        if (rows.Length > HybridCpuPlatformContractV1.MaximumManagedFunctionPointers)
            return Failure(HybridCpuPlatformFactStatus.Unsupported,
                "Managed function-pointer metadata exceeds its deterministic budget.");
        if (rows.Any(static row => row is null || row.MethodId == 0 || row.SignatureId == 0 ||
                string.IsNullOrWhiteSpace(row.CodeSymbolName) || string.IsNullOrWhiteSpace(row.InvocationThunkSymbolName)) ||
            rows.Select(static row => (row.MethodId, row.SignatureId)).Distinct().Count() != rows.Length)
            return Failure(HybridCpuPlatformFactStatus.Invalid,
                "Managed function-pointer keys must be non-zero and unique and symbols must be present.");

        byte[] bytes = new byte[checked(HeaderBytes + rows.Length * RowBytes)];
        "HCFP0001"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), rows.Length);
        List<HybridCpuObjectRelocationV1> relocations = new List<HybridCpuObjectRelocationV1>(checked(rows.Length * 2));
        int offset = HeaderBytes;
        foreach (HybridCpuManagedFunctionPointerSymbolBindingV1 row in rows)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), row.MethodId);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 8), row.SignatureId);
            relocations.Add(new(SectionName, checked((ulong)(offset + 16)), HybridCpuRelocationKind.Absolute64,
                row.CodeSymbolName, 0));
            relocations.Add(new(SectionName, checked((ulong)(offset + 24)), HybridCpuRelocationKind.Absolute64,
                row.InvocationThunkSymbolName, 0));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 32), row.IsInstanceMethod ? 1UL : 0UL);
            offset += RowBytes;
        }
        HybridCpuObjectRelocationV1[] orderedRelocations = relocations.OrderBy(static row => row.Offset).ToArray();
        string digest = HybridCpuPlatformContractV1.Hash(string.Join('|', SchemaId,
            HybridCpuPlatformContractV1.ContractDigest, Convert.ToHexString(bytes),
            string.Join(';', orderedRelocations.Select(static row =>
                $"{row.Offset}:{row.TargetSymbol}:{row.Addend}"))));
        return new(HybridCpuPlatformFactStatus.Supported, string.Empty,
            new(SectionName, HybridCpuObjectSectionKind.ReadOnlyData, 8, bytes, checked((ulong)bytes.Length)),
            orderedRelocations, digest);
    }

    private static HybridCpuManagedFunctionPointerMetadataArtifactV1 Failure(
        HybridCpuPlatformFactStatus status,
        string reason) => new(status, reason, null, [],
        HybridCpuPlatformContractV1.Hash($"{SchemaId}|failure|{status}|{reason}"));
}
