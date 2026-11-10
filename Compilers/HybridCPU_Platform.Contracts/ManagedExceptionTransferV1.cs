using System.Buffers.Binary;

namespace HybridCPU.Platform.Contracts;

/// <summary>Private runtime/compiler transfer record, not an ISA extension or image admission proof.
/// Its storage must remain readable and immovable throughout the non-safepoint transfer stub.</summary>
public static class HybridCpuManagedExceptionTransferV1
{
    public const uint Magic = 0x54454348; // HCET
    public const ushort Version = 1;
    public const int ProgramCounterOffset = 8;
    public const int RegistersOffset = 16;
    public const int RegisterCount = 32;
    public const int SizeBytes = RegistersOffset + RegisterCount * 8;
    public static int RegisterOffset(int register) => register is >= 0 and < RegisterCount
        ? RegistersOffset + register * 8 : throw new ArgumentOutOfRangeException(nameof(register));

    public static byte[] Encode(ulong handlerPc, IReadOnlyList<ulong> registers, ulong exceptionReference)
    {
        ArgumentNullException.ThrowIfNull(registers);
        if (handlerPc == 0 || handlerPc % 256 != 0 || registers.Count != RegisterCount || registers[0] != 0 ||
            registers[2] == 0 || registers[2] % 16 != 0 || exceptionReference == 0)
            throw new ArgumentException("Invalid managed handler transfer context.");
        byte[] bytes = new byte[SizeBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(ProgramCounterOffset), handlerPc);
        for (int register = 0; register < RegisterCount; register++)
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(RegisterOffset(register)),
                register == 10 ? exceptionReference : registers[register]);
        return bytes;
    }
}
