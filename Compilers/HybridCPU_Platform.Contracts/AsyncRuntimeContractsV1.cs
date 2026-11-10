using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Platform.Contracts;

public enum HybridCpuDeadlineDispositionV1 : byte
{
    Scheduled = 0,
    Completed = 1,
    Cancelled = 2,
    Invalid = 3,
    BudgetExhausted = 4
}

public sealed record HybridCpuDeadlineRequestV1(
    ulong ContextId,
    ulong DeadlineTick,
    ulong Token);

public sealed record HybridCpuDeadlineResultV1(
    HybridCpuDeadlineDispositionV1 Disposition,
    ulong MonotonicTick,
    ulong Token,
    IReadOnlyList<ulong> CompletedTokens,
    string Reason,
    string ResultDigest)
{
    public bool IsSuccess => Disposition is HybridCpuDeadlineDispositionV1.Scheduled or HybridCpuDeadlineDispositionV1.Completed;
}

public static class HybridCpuAsyncRuntimeContractV1
{
    public const string SchemaId = "hybridcpu.async-runtime/v1";
    public const int SchemaVersion = 1;
    public const int MaximumPendingDeadlines = 1024;
    public const int MaximumTasks = 1024;
    public const int MaximumContinuations = 4096;
    public const int MaximumWorkers = 4;

    public static string ContractDigest { get; } = Hash(string.Join('|', SchemaId, SchemaVersion,
        MaximumPendingDeadlines, MaximumTasks, MaximumContinuations, MaximumWorkers,
        "monotonic-only", "virtual-time-testable", "deadline-order=deadline+sequence+token",
        "task-state=single-terminal-transition", "execution-context=none-or-immutable-carrier",
        "no-preemption", "no-async-opcode"));

    public static string ResultDigest(HybridCpuDeadlineDispositionV1 disposition, ulong tick, ulong token,
        IReadOnlyList<ulong> completed, string reason) => Hash(string.Join('|', ContractDigest,
            disposition, tick, token, string.Join(',', completed), reason));

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
