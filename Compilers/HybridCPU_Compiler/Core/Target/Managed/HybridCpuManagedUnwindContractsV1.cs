using System.Buffers.Binary;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public enum HybridCpuManagedFrameKindV1 : byte
{
    Managed = 0,
    RuntimeHelper = 1
}

public enum HybridCpuManagedCfaBaseV1 : byte
{
    StackPointer = 0,
    FramePointer = 1
}

public sealed record HybridCpuManagedSavedRegisterV1(
    int RegisterId,
    int CfaRelativeOffsetBytes);

public sealed record HybridCpuManagedUnwindRecordV1(
    HybridCpuManagedFrameKindV1 FrameKind,
    HybridCpuManagedCfaBaseV1 CfaBase,
    int CfaOffsetBytes,
    int ReturnPcCfaRelativeOffsetBytes,
    IReadOnlyList<HybridCpuManagedSavedRegisterV1> SavedRegisters);

public static class HybridCpuManagedUnwindCodecV1
{
    private const uint Magic = 0x574d4348; // HCMW
    public const int MaximumSavedRegisters = 32;

    public static byte[] Encode(HybridCpuManagedUnwindRecordV1 record)
    {
        Validate(record);
        HybridCpuManagedSavedRegisterV1[] saved = record.SavedRegisters.OrderBy(static row => row.RegisterId).ToArray();
        byte[] bytes = new byte[20 + saved.Length * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 0);
        bytes[8] = (byte)record.FrameKind;
        bytes[9] = (byte)record.CfaBase;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), checked((ushort)saved.Length));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), record.CfaOffsetBytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), record.ReturnPcCfaRelativeOffsetBytes);
        int offset = 20;
        foreach (HybridCpuManagedSavedRegisterV1 row in saved)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), row.RegisterId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4), row.CfaRelativeOffsetBytes);
            offset += 8;
        }
        return bytes;
    }

    public static HybridCpuManagedUnwindRecordV1 Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) != 0)
            throw new ArgumentException("Managed unwind record header is invalid.", nameof(bytes));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        if (count > MaximumSavedRegisters || bytes.Length != 20 + count * 8)
            throw new ArgumentException("Managed unwind record size exceeds the deterministic schema.", nameof(bytes));
        HybridCpuManagedSavedRegisterV1[] saved = new HybridCpuManagedSavedRegisterV1[count];
        int offset = 20;
        for (int index = 0; index < count; index++)
        {
            saved[index] = new(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 4)..]));
            offset += 8;
        }
        HybridCpuManagedUnwindRecordV1 record = new HybridCpuManagedUnwindRecordV1((HybridCpuManagedFrameKindV1)bytes[8],
            (HybridCpuManagedCfaBaseV1)bytes[9], BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]), saved);
        Validate(record);
        return record;
    }

    private static void Validate(HybridCpuManagedUnwindRecordV1 record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(record.SavedRegisters);
        if (!Enum.IsDefined(record.FrameKind) || !Enum.IsDefined(record.CfaBase) ||
            record.CfaOffsetBytes < 0 || record.CfaOffsetBytes % 8 != 0 ||
            record.ReturnPcCfaRelativeOffsetBytes < 0 || record.ReturnPcCfaRelativeOffsetBytes % 8 != 0 ||
            record.SavedRegisters.Count > MaximumSavedRegisters ||
            record.SavedRegisters.Any(static row => row is null || row.RegisterId is < 1 or > 31 ||
                row.CfaRelativeOffsetBytes < 0 || row.CfaRelativeOffsetBytes % 8 != 0) ||
            record.SavedRegisters.Select(static row => row.RegisterId).Distinct().Count() != record.SavedRegisters.Count)
            throw new ArgumentException("Managed unwind record is malformed or exceeds deterministic budgets.", nameof(record));
    }
}

public sealed record HybridCpuManagedUnwindRecordV2(
    HybridCpuManagedFrameKindV1 FrameKind,
    HybridCpuManagedCfaBaseV1 CfaBase,
    int CfaOffsetBytes,
    int? ReturnPcRegisterId,
    int? ReturnPcCfaRelativeOffsetBytes,
    IReadOnlyList<HybridCpuManagedSavedRegisterV1> SavedRegisters);

public static class HybridCpuManagedUnwindCodecV2
{
    public const int MaximumSavedRegisters = 32;

    public static byte[] Encode(HybridCpuManagedUnwindRecordV2 record)
    {
        Validate(record);
        HybridCpuManagedSavedRegisterV1[] saved = record.SavedRegisters.OrderBy(static row => row.RegisterId).ToArray();
        byte[] bytes = new byte[28 + saved.Length * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, HybridCpuManagedEhSchemaV1.UnwindMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), HybridCpuManagedEhSchemaV1.SchemaVersion);
        bytes[8] = (byte)record.FrameKind;
        bytes[9] = (byte)record.CfaBase;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), checked((ushort)saved.Length));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), record.CfaOffsetBytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), record.ReturnPcRegisterId ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), record.ReturnPcCfaRelativeOffsetBytes ?? int.MinValue);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), bytes.Length);
        int offset = 28;
        foreach (HybridCpuManagedSavedRegisterV1 row in saved)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), row.RegisterId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4), row.CfaRelativeOffsetBytes);
            offset += 8;
        }
        return bytes;
    }

    public static HybridCpuManagedUnwindRecordV2 Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != HybridCpuManagedEhSchemaV1.UnwindMagic ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != HybridCpuManagedEhSchemaV1.SchemaVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) != 0 || BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]) != bytes.Length)
            throw new ArgumentException("Managed unwind v2 header is invalid.", nameof(bytes));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        if (count > MaximumSavedRegisters || bytes.Length != 28 + count * 8)
            throw new ArgumentException("Managed unwind v2 size exceeds the deterministic schema.", nameof(bytes));
        HybridCpuManagedSavedRegisterV1[] saved = new HybridCpuManagedSavedRegisterV1[count];
        for (int index = 0, offset = 28; index < count; index++, offset += 8)
            saved[index] = new(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 4)..]));
        int register = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        int stack = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        HybridCpuManagedUnwindRecordV2 result = new HybridCpuManagedUnwindRecordV2((HybridCpuManagedFrameKindV1)bytes[8],
            (HybridCpuManagedCfaBaseV1)bytes[9], BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]),
            register < 0 ? null : register, stack == int.MinValue ? null : stack, saved);
        Validate(result);
        return result;
    }

    private static void Validate(HybridCpuManagedUnwindRecordV2 record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(record.SavedRegisters);
        bool registerReturn = record.ReturnPcRegisterId is >= 1 and <= 31 && record.ReturnPcCfaRelativeOffsetBytes is null;
        bool stackReturn = record.ReturnPcRegisterId is null && record.ReturnPcCfaRelativeOffsetBytes is int stack &&
            stack != int.MinValue && stack % 8 == 0;
        if (!Enum.IsDefined(record.FrameKind) || !Enum.IsDefined(record.CfaBase) ||
            record.CfaOffsetBytes < 0 || record.CfaOffsetBytes % 8 != 0 || (!registerReturn && !stackReturn) ||
            record.SavedRegisters.Count > MaximumSavedRegisters ||
            record.SavedRegisters.Any(static row => row is null || row.RegisterId is < 1 or > 31 ||
                row.CfaRelativeOffsetBytes == int.MinValue || row.CfaRelativeOffsetBytes % 8 != 0) ||
            record.SavedRegisters.Select(static row => row.RegisterId).Distinct().Count() != record.SavedRegisters.Count)
            throw new ArgumentException("Managed unwind v2 record is malformed or exceeds deterministic budgets.", nameof(record));
    }
}
