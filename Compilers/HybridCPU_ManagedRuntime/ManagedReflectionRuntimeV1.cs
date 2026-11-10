using HybridCPU.Platform.Contracts;

namespace HybridCPU.ManagedRuntime;

public enum HybridCpuManagedReflectionStatusV1 : byte
{
    Success = 0,
    Disabled = 1,
    MissingMetadata = 2,
    InvalidMetadata = 3,
    UnsupportedDynamicCode = 4
}

public sealed record HybridCpuManagedReflectionOptionsV1(bool Enabled, string OptionsDigest)
{
    public static HybridCpuManagedReflectionOptionsV1 Production { get; } = Create(false);
    public static HybridCpuManagedReflectionOptionsV1 Qualification { get; } = Create(true);
    public static HybridCpuManagedReflectionOptionsV1 Create(bool enabled) => new(enabled,
        HybridCpuPlatformContractV1.Hash($"hybridcpu.managed-reflection-runtime-options/v1|{enabled}|dynamic-code=false"));
}

public sealed class HybridCpuManagedPublicTypeV1
{
    internal HybridCpuManagedPublicTypeV1(HybridCpuManagedReflectionTypeV1 metadata)
    {
        Metadata = metadata;
        ReflectionObjectId = HybridCpuManagedReflectionContractV1.StableId("public-type", metadata.TypeDigest);
    }

    internal HybridCpuManagedReflectionTypeV1 Metadata { get; }
    public ulong ReflectionObjectId { get; }
    public ulong RuntimeTypeId => Metadata.TypeId;
    public string StableIdentity => Metadata.StableIdentity;
    public HybridCpuManagedTypeKindV1 Kind => Metadata.Kind;
    public bool IsExactConstructedGeneric => StableIdentity.Contains('<', StringComparison.Ordinal) &&
        StableIdentity.EndsWith('>');
}

public sealed record HybridCpuManagedReflectionResultV1(
    HybridCpuManagedReflectionStatusV1 Status,
    string Reason,
    HybridCpuManagedPublicTypeV1? Type,
    IReadOnlyList<HybridCpuManagedReflectionMemberV1> Members,
    string ResultDigest)
{
    public bool IsSuccess => Status == HybridCpuManagedReflectionStatusV1.Success;
}

/// <summary>Runtime-owned public Type-object identity, cache and retained member lookup.</summary>
public sealed class HybridCpuManagedReflectionRuntimeV1
{
    private readonly HybridCpuManagedReflectionTableV1 _table;
    private readonly HybridCpuManagedReflectionOptionsV1 _options;
    private readonly Dictionary<ulong, HybridCpuManagedPublicTypeV1> _cache = [];

    public HybridCpuManagedReflectionRuntimeV1(HybridCpuManagedReflectionTableV1 table,
        HybridCpuManagedReflectionOptionsV1? options = null)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _options = options ?? HybridCpuManagedReflectionOptionsV1.Production;
        if (_options.OptionsDigest != HybridCpuManagedReflectionOptionsV1.Create(_options.Enabled).OptionsDigest)
            throw new ArgumentException("Reflection runtime options are not contract-bound.", nameof(options));
    }

    public HybridCpuManagedReflectionResultV1 GetType(ulong runtimeTypeId)
    {
        if (!_options.Enabled) return Failure(HybridCpuManagedReflectionStatusV1.Disabled,
            "Public reflection is default-disabled.");
        if (!HybridCpuManagedReflectionContractV1.IsValid(_table))
            return Failure(HybridCpuManagedReflectionStatusV1.InvalidMetadata,
                "The retained reflection table is malformed or digest-skewed.");
        HybridCpuManagedReflectionTypeV1? metadata = _table.Types.SingleOrDefault(type => type.TypeId == runtimeTypeId);
        if (metadata is null) return Failure(HybridCpuManagedReflectionStatusV1.MissingMetadata,
            "No public reflection metadata was retained for the runtime type.");
        if (!_cache.TryGetValue(runtimeTypeId, out HybridCpuManagedPublicTypeV1? type))
        {
            type = new(metadata);
            _cache.Add(runtimeTypeId, type);
        }
        return Success(type, type.Metadata.Members);
    }

    public HybridCpuManagedReflectionResultV1 GetType(string stableIdentity)
    {
        if (!_options.Enabled) return Failure(HybridCpuManagedReflectionStatusV1.Disabled,
            "Public reflection is default-disabled.");
        if (!HybridCpuManagedReflectionContractV1.IsValid(_table))
            return Failure(HybridCpuManagedReflectionStatusV1.InvalidMetadata,
                "The retained reflection table is malformed or digest-skewed.");
        if (string.IsNullOrWhiteSpace(stableIdentity))
            return Failure(HybridCpuManagedReflectionStatusV1.MissingMetadata, "An exact retained type identity is required.");
        HybridCpuManagedReflectionTypeV1? metadata = _table.Types.SingleOrDefault(type =>
            string.Equals(type.StableIdentity, stableIdentity, StringComparison.Ordinal));
        return metadata is null ? Failure(HybridCpuManagedReflectionStatusV1.MissingMetadata,
            "No public reflection metadata was retained for the exact type identity.") : GetType(metadata.TypeId);
    }

    public HybridCpuManagedReflectionResultV1 GetMembers(HybridCpuManagedPublicTypeV1 type, string? name = null)
    {
        if (type is null) return Failure(HybridCpuManagedReflectionStatusV1.MissingMetadata,
            "A cached public Type object is required.");
        HybridCpuManagedReflectionResultV1 resolved = GetType(type.RuntimeTypeId);
        if (!resolved.IsSuccess || !ReferenceEquals(resolved.Type, type)) return resolved;
        HybridCpuManagedReflectionMemberV1[] members = type.Metadata.Members
            .Where(member => name is null || string.Equals(member.Name, name, StringComparison.Ordinal))
            .ToArray();
        return members.Length == 0 && name is not null
            ? Failure(HybridCpuManagedReflectionStatusV1.MissingMetadata,
                "The selected public member was not retained.")
            : Success(type, members);
    }

    public HybridCpuManagedReflectionResultV1 RequestDynamicCode(string operation) =>
        Failure(HybridCpuManagedReflectionStatusV1.UnsupportedDynamicCode,
            $"Dynamic reflection operation '{operation}' is outside bounded reflection V1.");

    public int CachedTypeCount => _cache.Count;

    private HybridCpuManagedReflectionResultV1 Success(HybridCpuManagedPublicTypeV1 type,
        IReadOnlyList<HybridCpuManagedReflectionMemberV1> members) => new(
        HybridCpuManagedReflectionStatusV1.Success, string.Empty, type, members,
        Digest(HybridCpuManagedReflectionStatusV1.Success, type.ReflectionObjectId, members));

    private HybridCpuManagedReflectionResultV1 Failure(HybridCpuManagedReflectionStatusV1 status, string reason) =>
        new(status, reason, null, [], Digest(status, 0, [], reason));

    private string Digest(HybridCpuManagedReflectionStatusV1 status, ulong type,
        IReadOnlyList<HybridCpuManagedReflectionMemberV1> members, string reason = "") =>
        HybridCpuPlatformContractV1.Hash(string.Join('|', HybridCpuManagedReflectionContractV1.ContractDigest,
            _table.TableDigest, status, type, string.Join(';', members.Select(static member => member.MemberDigest)), reason));
}
