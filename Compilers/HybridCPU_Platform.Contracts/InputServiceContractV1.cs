namespace HybridCPU.Platform.Contracts;

public static class HybridCpuInputServiceContractV1
{
    public const string SchemaId="hybridcpu.input-service/v1";
    public const ulong PullOperation=0x5049_4e50; // PINP
    public const string ManagedTypeIdentity="DoomSharp.Core.Input.InputEvent";
    public static string ContractDigest { get; }=HybridCpuPlatformContractV1.Hash(
        $"{SchemaId}|pull:{PullOperation}|args=none|buffer=none|return=null-or-trusted-managed-ref|"+
        $"type={ManagedTypeIdentity}|provider=loader-managed-input-registry-only|sync|no-reentry|gc=deferred|root-transfer=atomic");
}
