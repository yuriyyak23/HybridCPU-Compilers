using HybridCPU.RuntimeKernel;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.ManagedRuntime;

/// <summary>Explicit virtual-time conversion for guest clock services. No wall-clock or CPU-cycle source.</summary>
public sealed class HybridCpuManagedFixedRateClockV1
{
    private readonly IHybridCpuRuntimeKernelV1 _kernel;
    private readonly ulong _sourceTicksPerSecond;

    public HybridCpuManagedFixedRateClockV1(IHybridCpuRuntimeKernelV1 kernel, ulong sourceTicksPerSecond)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        if (sourceTicksPerSecond == 0) throw new ArgumentOutOfRangeException(nameof(sourceTicksPerSecond));
        _kernel = kernel;
        _sourceTicksPerSecond = sourceTicksPerSecond;
    }

    public int ReadTics(int ticsPerSecond)
    {
        if (ticsPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticsPerSecond));
        HybridCpuExecutionContextDescriptorV1 context = _kernel.CurrentContext() ?? throw new InvalidOperationException("Virtual clock requires a live execution context.");
        HybridCpuExternalServiceRequestV1 draft = new HybridCpuExternalServiceRequestV1(context.ContextId, HybridCpuHostServiceV1.Clock,
            HybridCpuVirtualClockServiceContractV1.ReadTicksOperation, HybridCpuPrivilegeModeV1.User,
            0, 0, HybridCpuHostBufferAccessV1.None, [], string.Empty);
        HybridCpuExternalServiceRequestV1 request = draft with { TransitionDigest = HybridCpuManagedInteropContractV1.ComputeTransitionDigest(draft) };
        HybridCpuExternalServiceResultV1 response = _kernel.ExternalServiceTransition(request);
        if (!response.IsSuccess || response.ResultDigest != HybridCpuManagedInteropContractV1.ComputeResultDigest(
                request, response.Status, response.ReturnValue, response.ErrorCode, response.Reason))
            throw new InvalidOperationException("Virtual clock service rejected the exact kernel transition.");
        ulong source = response.ReturnValue;
        ulong whole = source / _sourceTicksPerSecond;
        ulong fraction = source % _sourceTicksPerSecond;
        ulong rate = (uint)ticsPerSecond;
        if (whole > (ulong)int.MaxValue / rate) throw new OverflowException("Guest virtual clock exceeds Int32 tics.");

        // floor(fraction * rate / frequency), without a potentially overflowing product,
        // floating-point rounding, UInt128, or a loop proportional to the elapsed time.
        ulong quotient = 0, remainder = 0;
        for (int bit = 30; bit >= 0; bit--)
        {
            quotient *= 2;
            if (remainder >= _sourceTicksPerSecond - remainder)
            { remainder -= _sourceTicksPerSecond - remainder; quotient++; }
            else remainder += remainder;
            if ((rate & (1UL << bit)) == 0) continue;
            if (remainder >= _sourceTicksPerSecond - fraction)
            { remainder -= _sourceTicksPerSecond - fraction; quotient++; }
            else remainder += fraction;
        }
        ulong result = whole * rate + quotient;
        if (result > int.MaxValue) throw new OverflowException("Guest virtual clock exceeds Int32 tics.");
        return (int)result;
    }
}
