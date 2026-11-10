namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Compiler-owned structural diagnostic mask. It is evidence only and carries no
/// runtime admission, freshness, publication, or execution authority.
/// </summary>
public readonly record struct IrSafetyMask128(ulong Low, ulong High)
{
    public static IrSafetyMask128 Zero => default;

    public static IrSafetyMask128 operator |(IrSafetyMask128 left, IrSafetyMask128 right) =>
        new(left.Low | right.Low, left.High | right.High);
}
