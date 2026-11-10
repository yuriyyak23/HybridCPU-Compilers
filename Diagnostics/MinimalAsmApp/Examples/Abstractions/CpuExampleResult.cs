namespace MinimalAsmApp.Examples.Abstractions;

public sealed record CpuExampleResult(
    bool Success,
    string Output,
    IReadOnlyDictionary<string, ulong> Registers,
    IReadOnlyDictionary<string, ulong> Memory,
    IReadOnlyList<string> Trace,
    IReadOnlyList<string> Notes)
{
    public static CpuExampleResult Ok(
        string output,
        IReadOnlyDictionary<string, ulong>? registers = null,
        IReadOnlyDictionary<string, ulong>? memory = null,
        IReadOnlyList<string>? trace = null,
        IReadOnlyList<string>? notes = null)
    {
        return new CpuExampleResult(
            true,
            output,
            registers ?? EmptyDictionary,
            memory ?? EmptyDictionary,
            trace ?? Array.Empty<string>(),
            notes ?? Array.Empty<string>());
    }

    public static CpuExampleResult Fail(
        string output,
        IReadOnlyDictionary<string, ulong>? registers = null,
        IReadOnlyDictionary<string, ulong>? memory = null,
        IReadOnlyList<string>? trace = null,
        IReadOnlyList<string>? notes = null)
    {
        return new CpuExampleResult(
            false,
            output,
            registers ?? EmptyDictionary,
            memory ?? EmptyDictionary,
            trace ?? Array.Empty<string>(),
            notes ?? Array.Empty<string>());
    }

    private static readonly IReadOnlyDictionary<string, ulong> EmptyDictionary =
        new Dictionary<string, ulong>();
}
