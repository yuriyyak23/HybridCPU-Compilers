using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public enum HybridCpuManagedReflectionRetentionStatusV1 : byte
{
    Retained = 0,
    Disabled = 1,
    InvalidInput = 2,
    MissingMetadata = 3,
    BudgetExhausted = 4
}

public sealed record HybridCpuManagedReflectionMemberDeclarationV1(
    string DeclaringTypeIdentity,
    string StableIdentity,
    string Name,
    HybridCpuManagedReflectionMemberKindV1 Kind,
    string Signature,
    bool IsPublic,
    bool IsStatic,
    int MetadataOrdinal);

public sealed record HybridCpuManagedReflectionRootV1(
    string TypeIdentity,
    IReadOnlyList<string> MemberIdentities);

public sealed record HybridCpuManagedReflectionRetentionOptionsV1(
    bool Enabled,
    int MaximumTypes,
    int MaximumMembers,
    string OptionsDigest)
{
    public static HybridCpuManagedReflectionRetentionOptionsV1 Production { get; } = Create(false, 1024, 8192);
    public static HybridCpuManagedReflectionRetentionOptionsV1 Qualification { get; } = Create(true, 1024, 8192);

    public static HybridCpuManagedReflectionRetentionOptionsV1 Create(bool enabled, int types, int members) =>
        new(enabled, types, members, HybridCpuPlatformContractV1.Hash(string.Join('|',
            "hybridcpu.managed-reflection-retention-options/v1", enabled, types, members, "explicit-only")));
}

public sealed record HybridCpuManagedReflectionRetentionResultV1(
    HybridCpuManagedReflectionRetentionStatusV1 Status,
    string Reason,
    HybridCpuManagedReflectionTableV1? Table,
    byte[] MetadataBytes,
    string ResultDigest)
{
    public bool IsSuccess => Status == HybridCpuManagedReflectionRetentionStatusV1.Retained;
}

/// <summary>Compiler-owned deterministic reachability and explicit public metadata retention.</summary>
public sealed class HybridCpuManagedReflectionMetadataBuilderV1
{
    public HybridCpuManagedReflectionRetentionResultV1 Build(
        IReadOnlyList<HybridCpuManagedTypeDescriptorV1> descriptors,
        IReadOnlyList<HybridCpuManagedReflectionMemberDeclarationV1> members,
        IReadOnlyList<HybridCpuManagedReflectionRootV1> roots,
        HybridCpuManagedReflectionRetentionOptionsV1? options = null)
    {
        options ??= HybridCpuManagedReflectionRetentionOptionsV1.Production;
        if (!ValidOptions(options) || descriptors is null || members is null || roots is null)
            return Failure(HybridCpuManagedReflectionRetentionStatusV1.InvalidInput, "Reflection retention inputs or options are invalid.");
        if (!options.Enabled)
            return Failure(HybridCpuManagedReflectionRetentionStatusV1.Disabled, "Public reflection retention is default-disabled.");
        if (descriptors.Any(static type => type is null ||
                type.SchemaId != HybridCpuManagedTypeDescriptorContractV1.SchemaId ||
                type.SchemaMajor != HybridCpuManagedTypeDescriptorContractV1.SchemaMajor ||
                type.SchemaMinor > HybridCpuManagedTypeDescriptorContractV1.SchemaMinor ||
                type.DescriptorDigest != HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(type)) ||
            descriptors.Select(static type => type.StableIdentity).Distinct(StringComparer.Ordinal).Count() != descriptors.Count ||
            members.Any(static member => string.IsNullOrWhiteSpace(member.DeclaringTypeIdentity) ||
                string.IsNullOrWhiteSpace(member.StableIdentity) || string.IsNullOrWhiteSpace(member.Name) ||
                string.IsNullOrWhiteSpace(member.Signature) || !Enum.IsDefined(member.Kind) || member.MetadataOrdinal < 0) ||
            members.Select(static member => member.StableIdentity).Distinct(StringComparer.Ordinal).Count() != members.Count ||
            roots.Any(static root => string.IsNullOrWhiteSpace(root.TypeIdentity) || root.MemberIdentities is null))
            return Failure(HybridCpuManagedReflectionRetentionStatusV1.InvalidInput, "Reflection identities must be exact and unique.");

        Dictionary<string, HybridCpuManagedTypeDescriptorV1> types = descriptors
            .ToDictionary(static type => type.StableIdentity, StringComparer.Ordinal);
        Dictionary<string, HybridCpuManagedReflectionMemberDeclarationV1> declaredMembers = members
            .ToDictionary(static member => member.StableIdentity, StringComparer.Ordinal);
        string[] rootedTypes = roots.Select(static root => root.TypeIdentity)
            .Concat(roots.SelectMany(static root => root.MemberIdentities)
                .Select(identity => declaredMembers.TryGetValue(identity, out HybridCpuManagedReflectionMemberDeclarationV1? member)
                    ? member.DeclaringTypeIdentity : string.Empty))
            .Where(static identity => !string.IsNullOrEmpty(identity)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        string[] rootedMembers = roots.SelectMany(static root => root.MemberIdentities)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (rootedTypes.Any(identity => !types.ContainsKey(identity)) ||
            rootedMembers.Any(identity => !declaredMembers.TryGetValue(identity, out HybridCpuManagedReflectionMemberDeclarationV1? member) || !member.IsPublic) ||
            roots.Any(root => root.MemberIdentities.Any(identity => declaredMembers.TryGetValue(identity, out HybridCpuManagedReflectionMemberDeclarationV1? member) &&
                member.DeclaringTypeIdentity != root.TypeIdentity)))
            return Failure(HybridCpuManagedReflectionRetentionStatusV1.MissingMetadata,
                "An explicitly rooted public type or member is absent, non-public or belongs to another type.");
        if (rootedTypes.Length > options.MaximumTypes || rootedMembers.Length > options.MaximumMembers)
            return Failure(HybridCpuManagedReflectionRetentionStatusV1.BudgetExhausted,
                "Reflection retention exceeded its deterministic type/member budget.");

        HybridCpuManagedReflectionTypeV1[] retained = rootedTypes.Select(identity =>
        {
            HybridCpuManagedTypeDescriptorV1 descriptor = types[identity];
            HybridCpuManagedReflectionMemberV1[] retainedMembers = rootedMembers
                .Select(memberIdentity => declaredMembers[memberIdentity])
                .Where(member => member.DeclaringTypeIdentity == identity)
                .OrderBy(static member => member.MetadataOrdinal).ThenBy(static member => member.StableIdentity, StringComparer.Ordinal)
                .Select(member =>
                {
                    HybridCpuManagedReflectionMemberV1 draft = new HybridCpuManagedReflectionMemberV1(
                        HybridCpuManagedReflectionContractV1.StableId("member", member.StableIdentity), descriptor.TypeId,
                        member.StableIdentity, member.Name, member.Kind, member.Signature, member.IsStatic,
                        member.MetadataOrdinal, string.Empty);
                    return draft with { MemberDigest = HybridCpuManagedReflectionContractV1.MemberDigest(draft) };
                }).ToArray();
            HybridCpuManagedReflectionTypeV1 typeDraft = new HybridCpuManagedReflectionTypeV1(descriptor.TypeId, descriptor.StableIdentity,
                descriptor.Kind, retainedMembers, string.Empty);
            return typeDraft with { TypeDigest = HybridCpuManagedReflectionContractV1.TypeDigest(typeDraft) };
        }).OrderBy(static type => type.TypeId).ToArray();
        string retentionDigest = HybridCpuPlatformContractV1.Hash(string.Join('|',
            HybridCpuManagedReflectionContractV1.ContractDigest, options.OptionsDigest,
            string.Join(';', rootedTypes), string.Join(';', rootedMembers)));
        HybridCpuManagedReflectionTableV1 tableDraft = new HybridCpuManagedReflectionTableV1(HybridCpuManagedReflectionContractV1.SchemaId,
            HybridCpuManagedReflectionContractV1.SchemaVersion, retained, retentionDigest, string.Empty);
        HybridCpuManagedReflectionTableV1 table = tableDraft with
        {
            TableDigest = HybridCpuManagedReflectionContractV1.TableDigest(tableDraft)
        };
        return HybridCpuManagedReflectionContractV1.IsValid(table)
            ? new(HybridCpuManagedReflectionRetentionStatusV1.Retained, string.Empty, table,
                HybridCpuManagedReflectionContractV1.Serialize(table),
                Digest(HybridCpuManagedReflectionRetentionStatusV1.Retained, table.TableDigest))
            : Failure(HybridCpuManagedReflectionRetentionStatusV1.InvalidInput, "Retained reflection table failed its closed contract.");
    }

    private static bool ValidOptions(HybridCpuManagedReflectionRetentionOptionsV1 options) =>
        options.MaximumTypes is > 0 and <= HybridCpuManagedReflectionContractV1.MaximumRetainedTypes &&
        options.MaximumMembers is > 0 and <= HybridCpuManagedReflectionContractV1.MaximumRetainedMembers &&
        options.OptionsDigest == HybridCpuManagedReflectionRetentionOptionsV1.Create(
            options.Enabled, options.MaximumTypes, options.MaximumMembers).OptionsDigest;

    private static HybridCpuManagedReflectionRetentionResultV1 Failure(
        HybridCpuManagedReflectionRetentionStatusV1 status, string reason) =>
        new(status, reason, null, [], Digest(status, reason));

    private static string Digest(HybridCpuManagedReflectionRetentionStatusV1 status, string value) =>
        HybridCpuPlatformContractV1.Hash(string.Join('|', HybridCpuManagedReflectionContractV1.ContractDigest, status, value));
}
