namespace HybridCPU.ManagedRuntime;

/// <summary>Exact CoreLib integer math operations exported by managed runtime pack V1.</summary>
public static class HybridCpuManagedMathRuntimeV1
{
    public static long AbsInt64(long value)
    {
        if (value == long.MinValue)
            throw new OverflowException("Negating the minimum Int64 value is not representable.");
        return value < 0 ? -value : value;
    }

    public static int MaxInt32(int left, int right) => left >= right ? left : right;
}
