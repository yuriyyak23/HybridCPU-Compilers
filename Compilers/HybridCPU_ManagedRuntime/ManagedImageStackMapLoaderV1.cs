using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.ManagedRuntime;

public enum HybridCpuManagedImageStackMapLoadStatusV1 : byte
{
    Loaded = 0,
    InvalidInput = 1,
    MissingMetadata = 2,
    AmbiguousMetadata = 3,
    MalformedMetadata = 4
}

public sealed record HybridCpuManagedImageStackMapLoadResultV1(
    HybridCpuManagedImageStackMapLoadStatusV1 Status,
    string Reason,
    IReadOnlyList<HybridCpuManagedStackMapRegistrationV1> Registrations)
{
    public bool IsSuccess => Status == HybridCpuManagedImageStackMapLoadStatusV1.Loaded;
}

/// <summary>
/// Reconstructs runtime stack-map registrations exclusively from HCEXE image-owned HCMG/HCW2
/// records and the inspected bootstrap digest table. No symbol-map or compiler object survives
/// this boundary; missing, duplicate, malformed or digest-mismatched metadata fails closed.
/// </summary>
public static class HybridCpuManagedImageStackMapLoaderV1
{
    private delegate int RecordLength(ReadOnlySpan<byte> bytes);
    private const uint GcMagic = 0x474d4348;
    private const uint UnwindMagic = HybridCpuManagedEhSchemaV1.UnwindMagic;
    private const uint CodeManagerMagic = 0x4d4d4348;

    public static HybridCpuManagedImageStackMapLoadResultV1 Load(
        ReadOnlyMemory<byte> image,
        IReadOnlyList<HybridCpuCodeManagerRegistrationV1> codeRecords,
        string managedAbiDigest,
        string targetPlatformDigest,
        string nativeAbiDigest,
        string runtimePackRevision)
    {
        if (image.IsEmpty || codeRecords is null ||
            !Sha(managedAbiDigest) || !Sha(targetPlatformDigest) || !Sha(nativeAbiDigest) ||
            string.IsNullOrWhiteSpace(runtimePackRevision) ||
            codeRecords.Any(static row => row is null || string.IsNullOrWhiteSpace(row.MethodIdentity) ||
                row.CodeStartOffsetBytes < 0 || row.CodeSizeBytes <= 0 || !Sha(row.GcInfoDigest) || !Sha(row.UnwindInfoDigest)) ||
            codeRecords.Select(static row => row.MethodIdentity).Distinct(StringComparer.Ordinal).Count() != codeRecords.Count)
            return Failure(HybridCpuManagedImageStackMapLoadStatusV1.InvalidInput,
                "Image stack-map loading requires unique bounded code records and exact ABI digests.");
        if (codeRecords.Count == 0)
            return new(HybridCpuManagedImageStackMapLoadStatusV1.Loaded, string.Empty, []);

        byte[] bytes = image.ToArray();
        Dictionary<string, List<byte[]>> gc = Scan(bytes, GcMagic, GcLength);
        Dictionary<string, List<byte[]>> unwind = Scan(bytes, UnwindMagic, UnwindLength);
        List<HybridCpuManagedStackMapRegistrationV1> registrations = new List<HybridCpuManagedStackMapRegistrationV1>(codeRecords.Count);
        foreach (HybridCpuCodeManagerRegistrationV1 row in codeRecords.OrderBy(static row => row.CodeStartOffsetBytes)
                     .ThenBy(static row => row.MethodIdentity, StringComparer.Ordinal))
        {
            if (!gc.TryGetValue(row.GcInfoDigest, out List<byte[]>? gcMatches) || gcMatches.Count == 0 ||
                !unwind.TryGetValue(row.UnwindInfoDigest, out List<byte[]>? unwindMatches) || unwindMatches.Count == 0)
                return Failure(HybridCpuManagedImageStackMapLoadStatusV1.MissingMetadata,
                    $"Image-owned GC or unwind metadata is absent for '{row.MethodIdentity}'.");
            if (gcMatches.Count != 1 || unwindMatches.Count != 1)
                return Failure(HybridCpuManagedImageStackMapLoadStatusV1.AmbiguousMetadata,
                    $"Image-owned GC or unwind metadata is not unique for '{row.MethodIdentity}'.");

            byte[] gcInfo = gcMatches[0];
            byte[] unwindInfo = unwindMatches[0];
            byte[] codeManager = EncodeCodeManager(row, gcInfo, unwindInfo, managedAbiDigest);
            registrations.Add(new(row.MethodIdentity, gcInfo, codeManager, managedAbiDigest,
                targetPlatformDigest, nativeAbiDigest, runtimePackRevision, unwindInfo));
        }
        return new(HybridCpuManagedImageStackMapLoadStatusV1.Loaded, string.Empty, registrations);
    }

    private static Dictionary<string, List<byte[]>> Scan(byte[] image, uint magic, RecordLength length)
    {
        Dictionary<string, List<byte[]>> result = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);
        for (int offset = 0; offset <= image.Length - 12; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset)) != magic) continue;
            int size;
            try { size = length(image.AsSpan(offset)); }
            catch (Exception exception) when (exception is ArgumentException or OverflowException) { continue; }
            if (size <= 0 || size > image.Length - offset) continue;
            byte[] record = image.AsSpan(offset, size).ToArray();
            string digest = Convert.ToHexString(SHA256.HashData(record)).ToLowerInvariant();
            if (!result.TryGetValue(digest, out List<byte[]>? matches)) result.Add(digest, matches = []);
            if (!matches.Any(existing => existing.AsSpan().SequenceEqual(record)))
                matches.Add(record);
            offset += size - 1;
        }
        return result;
    }

    private static int GcLength(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) != 0) return -1;
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        if (count < 0 || count > HybridCpuManagedNonMovingGcBudgetsV1.Production.MaximumSafepoints) return -1;
        int offset = 12;
        for (int index = 0; index < count; index++)
        {
            if (bytes.Length - offset < 8) return -1;
            int roots = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 6)..]);
            offset = checked(offset + 8 + roots * 16);
            if (offset > bytes.Length) return -1;
        }
        return offset;
    }

    private static int UnwindLength(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 28 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != HybridCpuManagedEhSchemaV1.SchemaVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) != 0) return -1;
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]);
        return length >= 28 ? length : -1;
    }

    private static byte[] EncodeCodeManager(HybridCpuCodeManagerRegistrationV1 row, byte[] gcInfo, byte[] unwindInfo,
        string managedAbiDigest)
    {
        byte[] bytes = new byte[92];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, CodeManagerMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);
        SHA256.HashData(Encoding.UTF8.GetBytes(row.MethodIdentity)).AsSpan(0, 8).CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), row.CodeStartOffsetBytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), row.CodeSizeBytes);
        string gcDigest = HybridCpuPlatformContractV1.Hash($"gc-info|{managedAbiDigest}|{Convert.ToHexString(gcInfo)}");
        Convert.FromHexString(gcDigest).CopyTo(bytes, 28);
        SHA256.HashData(unwindInfo).CopyTo(bytes, 60);
        return bytes;
    }

    private static bool Sha(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static HybridCpuManagedImageStackMapLoadResultV1 Failure(
        HybridCpuManagedImageStackMapLoadStatusV1 status, string reason) => new(status, reason, []);
}
