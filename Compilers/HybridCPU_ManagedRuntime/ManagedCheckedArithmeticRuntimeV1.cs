namespace HybridCPU.ManagedRuntime;

/// <summary>
/// Semantic reference for the managed checked-arithmetic helper surface. Native runtime
/// implementations must raise the same managed exception before returning any ISA-defined
/// zero-divisor result.
/// </summary>
public static class ManagedCheckedArithmeticRuntimeV1
{
    public static uint DivideUInt32(uint numerator, uint denominator)
    {
        if (denominator == 0)
            throw new DivideByZeroException();
        return numerator / denominator;
    }

    public static int DivideInt32(int numerator, int denominator)
    {
        if (denominator == 0) throw new DivideByZeroException();
        if (numerator == int.MinValue && denominator == -1) throw new OverflowException();
        return numerator / denominator;
    }

    public static long DivideInt64(long numerator, long denominator)
    {
        if (denominator == 0) throw new DivideByZeroException();
        if (numerator == long.MinValue && denominator == -1) throw new OverflowException();
        return numerator / denominator;
    }

    public static int RemainderInt32(int numerator, int denominator)
    {
        if (denominator == 0) throw new DivideByZeroException();
        if (numerator == int.MinValue && denominator == -1) throw new OverflowException();
        return numerator % denominator;
    }
}
