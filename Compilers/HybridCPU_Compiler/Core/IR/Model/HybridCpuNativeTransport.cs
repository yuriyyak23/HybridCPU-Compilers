using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace HybridCPU.Compiler.Core.IR;

public enum HybridCpuDataType : byte
{
    INT8 = 0, UINT8 = 1, INT16 = 2, UINT16 = 3, INT32 = 4, UINT32 = 5,
    FLOAT32 = 6, INT64 = 7, UINT64 = 8, FLOAT64 = 9, FLOAT16 = 10,
    BFLOAT16 = 11, FLOAT8_E4M3 = 12, FLOAT8_E5M2 = 13
}

public static class HybridCpuDataTypes
{
    public static int SizeOf(HybridCpuDataType type) => type switch
    {
        HybridCpuDataType.INT8 or HybridCpuDataType.UINT8 or
            HybridCpuDataType.FLOAT8_E4M3 or HybridCpuDataType.FLOAT8_E5M2 => 1,
        HybridCpuDataType.INT16 or HybridCpuDataType.UINT16 or
            HybridCpuDataType.FLOAT16 or HybridCpuDataType.BFLOAT16 => 2,
        HybridCpuDataType.INT32 or HybridCpuDataType.UINT32 or HybridCpuDataType.FLOAT32 => 4,
        HybridCpuDataType.INT64 or HybridCpuDataType.UINT64 or HybridCpuDataType.FLOAT64 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown HybridCPU data type.")
    };
}

/// <summary>Compiler-owned, fixed-width native instruction carrier.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HybridCpuInstructionWord
{
    public const int EncodedSize = 32;
    public const ushort NoReg = ushort.MaxValue;
    public const byte NoArchReg = byte.MaxValue;
    private const ulong RetiredPolicyGapMask = 1UL << 50;

    private ulong _word0;
    private ulong _word1;
    private ulong _word2;
    private ulong _word3;

    public ulong Word0 { readonly get => _word0; set => _word0 = value; }
    public ulong Word1 { readonly get => _word1; set => _word1 = value; }
    public ulong Word2 { readonly get => _word2; set => _word2 = value; }
    public ulong Word3
    {
        readonly get => _word3;
        set
        {
            if ((value & RetiredPolicyGapMask) != 0)
                throw new ArgumentException("Retired scheduling-policy carrier bit must be zero.", nameof(value));
            _word3 = value;
        }
    }

    public uint OpCode { readonly get => (uint)(_word0 >> 48); set => _word0 = (_word0 & 0x0000_FFFF_FFFF_FFFFUL) | ((ulong)(value & 0xFFFF) << 48); }
    public byte DataType { readonly get => (byte)(_word0 >> 32); set => _word0 = (_word0 & 0xFFFF_FF00_FFFF_FFFFUL) | ((ulong)value << 32); }
    public HybridCpuDataType DataTypeValue { readonly get => (HybridCpuDataType)DataType; set => DataType = (byte)value; }
    public byte PredicateMask { readonly get => (byte)(_word0 >> 24); set => _word0 = (_word0 & 0xFFFF_FFFF_00FF_FFFFUL) | ((ulong)value << 24); }
    public ushort Immediate { readonly get => (ushort)_word0; set => _word0 = (_word0 & 0xFFFF_FFFF_FFFF_0000UL) | value; }
    public byte Reserved { readonly get => (byte)(_word0 >> 40); set => _word0 = (_word0 & 0xFFFF_00FF_FFFF_FFFFUL) | ((ulong)value << 40); }
    public ulong DestSrc1Pointer { readonly get => _word1; set => _word1 = value; }
    public ulong Src2Pointer { readonly get => _word2; set => _word2 = value; }
    public ushort Reg1ID => (ushort)_word1;
    public ushort Reg2ID => (ushort)(_word1 >> 16);
    public ushort Reg3ID => (ushort)(_word1 >> 32);
    public uint SourceAPointer { readonly get => (uint)_word1; set => _word1 = (_word1 & 0xFFFF_FFFF_0000_0000UL) | value; }
    public uint SourceBPointer { readonly get => (uint)(_word1 >> 32); set => _word1 = (_word1 & 0x0000_0000_FFFF_FFFFUL) | ((ulong)value << 32); }
    public uint DestinationPointer { readonly get => (uint)_word2; set => _word2 = (_word2 & 0xFFFF_FFFF_0000_0000UL) | value; }
    public uint VectorDataLength { readonly get => (uint)(_word2 >> 32); set => _word2 = (_word2 & 0x0000_0000_FFFF_FFFFUL) | ((ulong)value << 32); }
    public ushort RowStride { readonly get => (ushort)((_word3 >> 51) & 0x1FFF); set { if (value > 0x1FFF) throw new ArgumentOutOfRangeException(nameof(value)); _word3 = (_word3 & 0x0007_FFFF_FFFF_FFFFUL) | ((ulong)value << 51); } }
    public byte VirtualThreadId { readonly get => (byte)((_word3 >> 48) & 3); set { if (value > 3) throw new ArgumentOutOfRangeException(nameof(value)); _word3 = (_word3 & 0xFFFC_FFFF_FFFF_FFFFUL) | ((ulong)value << 48); } }
    public uint StreamLength { readonly get => (uint)(_word3 >> 16); set => _word3 = (_word3 & 0xFFFF_0000_0000_FFFFUL) | ((ulong)value << 16); }
    public ushort Stride { readonly get => (ushort)_word3; set => _word3 = (_word3 & 0xFFFF_FFFF_FFFF_0000UL) | value; }

    public bool Saturating { readonly get => GetFlag(16); set => SetFlag(16, value); }
    public bool Release { readonly get => GetFlag(17); set => SetFlag(17, value); }
    public bool Acquire { readonly get => GetFlag(18); set => SetFlag(18, value); }
    public bool MaskAgnostic { readonly get => GetFlag(19); set => SetFlag(19, value); }
    public bool TailAgnostic { readonly get => GetFlag(20); set => SetFlag(20, value); }
    public bool Indexed { readonly get => GetFlag(21); set => SetFlag(21, value); }
    public bool Is2D { readonly get => GetFlag(22); set => SetFlag(22, value); }
    public bool Reduction { readonly get => GetFlag(23); set => SetFlag(23, value); }

    private readonly bool GetFlag(int bit) => ((_word0 >> bit) & 1) != 0;
    private void SetFlag(int bit, bool value) => _word0 = value ? _word0 | (1UL << bit) : _word0 & ~(1UL << bit);

    public static ulong PackArchRegs(byte rd, byte rs1, byte rs2)
    {
        static ulong Pack(byte value, string name) => value switch
        {
            NoArchReg => NoReg,
            <= 31 => value,
            _ => throw new ArgumentOutOfRangeException(name, value, "Architectural register id must be in [0, 31] or NoArchReg.")
        };
        return Pack(rd, nameof(rd)) | (Pack(rs1, nameof(rs1)) << 16) | (Pack(rs2, nameof(rs2)) << 32);
    }

    public static bool TryUnpackArchRegs(ulong packed, out byte rd, out byte rs1, out byte rs2)
    {
        static bool Decode(ushort value, out byte result)
        {
            if (value == NoReg) { result = NoArchReg; return true; }
            if (value <= 31) { result = (byte)value; return true; }
            result = default; return false;
        }
        if (Decode((ushort)packed, out rd) && Decode((ushort)(packed >> 16), out rs1) && Decode((ushort)(packed >> 32), out rs2)) return true;
        rd = rs1 = rs2 = default; return false;
    }

    public readonly bool TryWriteBytes(Span<byte> destination)
    {
        if (destination.Length < EncodedSize) return false;
        BinaryPrimitives.WriteUInt64LittleEndian(destination, _word0);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], _word1);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], _word2);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], _word3);
        return true;
    }

    public bool TryReadBytes(ReadOnlySpan<byte> source, int offset = 0)
    {
        if (offset < 0 || source.Length - offset < EncodedSize) return false;
        _word0 = BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]);
        _word1 = BinaryPrimitives.ReadUInt64LittleEndian(source[(offset + 8)..]);
        _word2 = BinaryPrimitives.ReadUInt64LittleEndian(source[(offset + 16)..]);
        Word3 = BinaryPrimitives.ReadUInt64LittleEndian(source[(offset + 24)..]);
        return true;
    }

}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HybridCpuInstructionBundle
{
    public const int SlotCount = 8;

    private HybridCpuInstructionWord _i0, _i1, _i2, _i3, _i4, _i5, _i6, _i7;

    public readonly HybridCpuInstructionWord GetInstruction(int index) => index switch
    {
        0 => _i0,
        1 => _i1,
        2 => _i2,
        3 => _i3,
        4 => _i4,
        5 => _i5,
        6 => _i6,
        7 => _i7,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public void SetInstruction(int index, HybridCpuInstructionWord instruction)
    {
        switch (index)
        {
            case 0: _i0 = instruction; break;
            case 1: _i1 = instruction; break;
            case 2: _i2 = instruction; break;
            case 3: _i3 = instruction; break;
            case 4: _i4 = instruction; break;
            case 5: _i5 = instruction; break;
            case 6: _i6 = instruction; break;
            case 7: _i7 = instruction; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public readonly bool TryWriteBytes(Span<byte> destination)
    {
        if (destination.Length < 8 * HybridCpuInstructionWord.EncodedSize) return false;
        for (int index = 0; index < 8; index++)
            GetInstruction(index).TryWriteBytes(destination[(index * HybridCpuInstructionWord.EncodedSize)..]);
        return true;
    }

    public bool TryReadBytes(ReadOnlySpan<byte> source, int offset = 0)
    {
        const int encodedSize = 8 * HybridCpuInstructionWord.EncodedSize;
        if (offset < 0 || source.Length - offset < encodedSize) return false;
        for (int index = 0; index < 8; index++)
        {
            HybridCpuInstructionWord instruction = new HybridCpuInstructionWord();
            if (!instruction.TryReadBytes(source, offset + index * HybridCpuInstructionWord.EncodedSize)) return false;
            SetInstruction(index, instruction);
        }
        return true;
    }
}
