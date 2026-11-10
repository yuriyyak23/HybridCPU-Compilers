namespace MinimalAsmApp.Examples.Support;

public sealed class CpuProgramRunOptions
{
    public IReadOnlyList<int> RegisterDump { get; init; } = Array.Empty<int>();

    public IReadOnlyList<ulong> MemoryDump { get; init; } = Array.Empty<ulong>();

    public IReadOnlyDictionary<ulong, ulong> InitialMemory { get; init; } =
        new Dictionary<ulong, ulong>();

    public ulong? RetirementTarget { get; init; }

    public ulong EmissionBaseAddress { get; init; }

    public ulong HardCycleLimit { get; init; } = 512;

    public int DrainCycles { get; init; } = 8;

    public bool CaptureTrace { get; init; }

    public int MaxTraceLines { get; init; } = 64;

    public IReadOnlyList<int> TraceRegisters { get; init; } = Array.Empty<int>();
}
