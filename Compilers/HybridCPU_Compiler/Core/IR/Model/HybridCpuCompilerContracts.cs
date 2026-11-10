namespace HybridCPU.Compiler.Core.IR;

public enum NativeFrontendMode : byte
{
    NativeVLIW = 0
}

public enum IrTypedSlotPolicyMode : byte
{
    CompatibilityValidation = 0,
    StrictVerification = 1,
    RequiredForAdmission = 2
}

public static class HybridCpuCompilerContract
{
    public const int Version = 6;

    public static void ThrowIfVersionMismatch(int actualVersion, string consumerSurface)
    {
        if (actualVersion != Version)
        {
            throw new InvalidOperationException(
                $"Compiler contract mismatch: {consumerSurface} requires HybridCPU compiler contract v{Version}, but artifact declares v{actualVersion}.");
        }
    }
}
