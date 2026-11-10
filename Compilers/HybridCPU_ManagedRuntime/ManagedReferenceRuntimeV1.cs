using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

namespace HybridCPU.ManagedRuntime;

public enum HybridCpuManagedNullCheckStatusV1 : byte
{
    NonNull = 0,
    NullTerminated = 1,
    KernelFailure = 2
}

public sealed record HybridCpuManagedNullCheckResultV1(
    HybridCpuManagedNullCheckStatusV1 Status,
    ulong Reference,
    string Reason)
{
    public bool MayContinue => Status == HybridCpuManagedNullCheckStatusV1.NonNull;
}

public sealed class HybridCpuManagedReferenceRuntimeV1
{
    public const int NullReferenceExitCode = unchecked((int)0x8000_0001);

    public HybridCpuManagedNullCheckResultV1 CheckNotNull(ulong reference, IHybridCpuRuntimeKernelV1 kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        if (reference != 0) return new(HybridCpuManagedNullCheckStatusV1.NonNull, reference, string.Empty);
        HybridCpuKernelResultV1 exit = kernel.ProcessExit(NullReferenceExitCode);
        return exit.IsSuccess
            ? new(HybridCpuManagedNullCheckStatusV1.NullTerminated, 0, "Managed null check terminated the current process through RuntimeKernel.")
            : new(HybridCpuManagedNullCheckStatusV1.KernelFailure, 0, exit.Reason);
    }
}
