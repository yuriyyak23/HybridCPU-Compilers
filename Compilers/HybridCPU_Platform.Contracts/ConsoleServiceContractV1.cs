namespace HybridCPU.Platform.Contracts;

public static class HybridCpuConsoleServiceContractV1
{
    public const string SchemaId = "hybridcpu.console-service/v1";
    public const ulong WriteUtf16Operation = 0x4357_3136; // CW16
    public const ulong SetTitleUtf16Operation = 0x4354_3136; // CT16
    public const int MaximumCodeUnits = 1024 * 1024;
    public const int StringLengthOffsetBytes = 16;
    public const int StringDataOffsetBytes = 20;
    public static string ContractDigest { get; } = HybridCpuPlatformContractV1.Hash(
        $"{SchemaId}|write:{WriteUtf16Operation}|title:{SetTitleUtf16Operation}|buffer=read-utf16le|args=0|max-code-units={MaximumCodeUnits}|"+
        $"string-length={StringLengthOffsetBytes}|string-data={StringDataOffsetBytes}|sync|no-reentry|gc=deferred-nonmoving");
}
