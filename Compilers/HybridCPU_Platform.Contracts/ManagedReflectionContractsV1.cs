using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Platform.Contracts;

public enum HybridCpuManagedReflectionMemberKindV1 : byte
{
    Field = 0,
    Method = 1,
    Property = 2
}

public sealed record HybridCpuManagedReflectionMemberV1(
    ulong MemberId,
    ulong DeclaringTypeId,
    string StableIdentity,
    string Name,
    HybridCpuManagedReflectionMemberKindV1 Kind,
    string Signature,
    bool IsStatic,
    int MetadataOrdinal,
    string MemberDigest);

public sealed record HybridCpuManagedReflectionTypeV1(
    ulong TypeId,
    string StableIdentity,
    HybridCpuManagedTypeKindV1 Kind,
    IReadOnlyList<HybridCpuManagedReflectionMemberV1> Members,
    string TypeDigest);

public sealed record HybridCpuManagedReflectionTableV1(
    string SchemaId,
    int SchemaVersion,
    IReadOnlyList<HybridCpuManagedReflectionTypeV1> Types,
    string RetentionDigest,
    string TableDigest);

/// <summary>Language-neutral retained public metadata. It grants no loading, codegen or execution authority.</summary>
public static class HybridCpuManagedReflectionContractV1
{
    public const string SchemaId = "hybridcpu.managed-reflection/v1";
    public const int SchemaVersion = 1;
    public const int MaximumRetainedTypes = 1024;
    public const int MaximumRetainedMembers = 8192;
    public const int MaximumTextBytes = 1024;

    public static string ContractDigest { get; } = Hash(string.Join('|', SchemaId, SchemaVersion,
        MaximumRetainedTypes, MaximumRetainedMembers, MaximumTextBytes, "explicit-roots",
        "public-selected-members", "deterministic-order", "dynamic-code=false", "assembly-load=false",
        "kernel=false", "ise=false"));

    public static ulong StableId(string domain, string identity)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{SchemaId}|{domain}|{identity}"));
        ulong result = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(digest);
        return result == 0 ? 1 : result;
    }

    public static string MemberDigest(HybridCpuManagedReflectionMemberV1 member) => Hash(string.Join('|',
        ContractDigest, member.MemberId, member.DeclaringTypeId, member.StableIdentity, member.Name,
        member.Kind, member.Signature, member.IsStatic, member.MetadataOrdinal));

    public static string TypeDigest(HybridCpuManagedReflectionTypeV1 type) => Hash(string.Join('|',
        ContractDigest, type.TypeId, type.StableIdentity, type.Kind,
        string.Join(';', type.Members.Select(static member => member.MemberDigest))));

    public static string TableDigest(HybridCpuManagedReflectionTableV1 table) => Hash(string.Join('|',
        ContractDigest, table.SchemaId, table.SchemaVersion, table.RetentionDigest,
        string.Join(';', table.Types.Select(static type => type.TypeDigest))));

    public static byte[] Serialize(HybridCpuManagedReflectionTableV1 table)
    {
        ArgumentNullException.ThrowIfNull(table);
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true);
        writer.Write(table.SchemaId);
        writer.Write(table.SchemaVersion);
        writer.Write(table.RetentionDigest);
        writer.Write(table.TableDigest);
        writer.Write(table.Types.Count);
        foreach (HybridCpuManagedReflectionTypeV1 type in table.Types)
        {
            writer.Write(type.TypeId);
            writer.Write(type.StableIdentity);
            writer.Write((byte)type.Kind);
            writer.Write(type.TypeDigest);
            writer.Write(type.Members.Count);
            foreach (HybridCpuManagedReflectionMemberV1 member in type.Members)
            {
                writer.Write(member.MemberId);
                writer.Write(member.DeclaringTypeId);
                writer.Write(member.StableIdentity);
                writer.Write(member.Name);
                writer.Write((byte)member.Kind);
                writer.Write(member.Signature);
                writer.Write(member.IsStatic);
                writer.Write(member.MetadataOrdinal);
                writer.Write(member.MemberDigest);
            }
        }
        writer.Flush();
        return stream.ToArray();
    }

    public static bool IsValid(HybridCpuManagedReflectionTableV1? table)
    {
        if (table is null || table.SchemaId != SchemaId || table.SchemaVersion != SchemaVersion ||
            table.Types is null || table.Types.Count > MaximumRetainedTypes ||
            table.Types.Select(static type => type.TypeId).Distinct().Count() != table.Types.Count ||
            table.Types.Select(static type => type.StableIdentity).Distinct(StringComparer.Ordinal).Count() != table.Types.Count ||
            !table.Types.SequenceEqual(table.Types.OrderBy(static type => type.TypeId)) ||
            table.Types.Sum(static type => type.Members?.Count ?? MaximumRetainedMembers + 1) > MaximumRetainedMembers)
            return false;
        foreach (HybridCpuManagedReflectionTypeV1 type in table.Types)
        {
            if (type.TypeId == 0 || !Text(type.StableIdentity) || type.Members is null ||
                !type.Members.SequenceEqual(type.Members.OrderBy(static member => member.MetadataOrdinal)
                    .ThenBy(static member => member.MemberId)) ||
                type.Members.Select(static member => member.MemberId).Distinct().Count() != type.Members.Count ||
                type.Members.Any(member => member.DeclaringTypeId != type.TypeId || member.MemberId == 0 ||
                    !Text(member.StableIdentity) || !Text(member.Name) || !Text(member.Signature) ||
                    !Enum.IsDefined(member.Kind) || member.MetadataOrdinal < 0 ||
                    member.MemberDigest != MemberDigest(member)) || type.TypeDigest != TypeDigest(type))
                return false;
        }
        return Text(table.RetentionDigest) && table.TableDigest == TableDigest(table);
    }

    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) &&
        Encoding.UTF8.GetByteCount(value) <= MaximumTextBytes;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
