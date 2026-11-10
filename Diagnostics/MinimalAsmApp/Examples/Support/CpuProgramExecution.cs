namespace MinimalAsmApp.Examples.Support;

public sealed record CpuProgramExecution(
    int BundleCount,
    ulong InstructionsRetired,
    ulong Cycles,
    IReadOnlyDictionary<string, ulong> Registers,
    IReadOnlyDictionary<string, ulong> Memory,
    IReadOnlyList<string> Trace)
{
    public ulong Register(int registerId) => Registers[FormatRegister(registerId)];

    public ulong MemoryValue(ulong address) => Memory[FormatMemory(address)];

    public void ExpectRegister(int registerId, ulong expected)
    {
        ulong actual = Register(registerId);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{FormatRegister(registerId)} = {actual}, expected {expected}.");
        }
    }

    public void ExpectMemory(ulong address, ulong expected)
    {
        ulong actual = MemoryValue(address);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{FormatMemory(address)} = {actual}, expected {expected}.");
        }
    }

    public static string FormatRegister(int registerId) => $"x{registerId}";

    public static string FormatMemory(ulong address) => $"mem[0x{address:X}]";
}
