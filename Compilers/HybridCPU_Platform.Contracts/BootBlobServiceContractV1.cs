namespace HybridCPU.Platform.Contracts;

public static class HybridCpuBootBlobServiceContractV1
{
    public const string SchemaId="hybridcpu.boot-blob-service/v1";
    public const ulong GetOperation=0x424c_4f42; // BLOB
    public const int MaximumBlobId=1024;
    public const int MaximumBlobBytes=64*1024*1024;
    public static string ContractDigest { get; }=HybridCpuPlatformContractV1.Hash(
        $"{SchemaId}|get:{GetOperation}|arg=positive-i32-id|max-id={MaximumBlobId}|return=trusted-managed-byte-array-ref|"+
        $"max-bytes={MaximumBlobBytes}|immutable|process-root|provider=loader-managed-registry-only");
}
