using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Compiler.Core.Target;

/// <summary>
/// Authoritative native call-control extension for the W=8 bundled execution contract.
/// It describes compiler compensation for the ISA-visible four-byte link increment; it
/// grants no runtime execution, freshness, publication or retirement authority.
/// </summary>
public sealed class HybridCpuNativeCallControlContractV1
{
    public const string SchemaId = "hybridcpu.native-call-control";
    public const int SchemaVersion = 1;
    public const int LinkIncrementBytes = 4;
    public const int SequentialBundleStrideBytes = 256;
    public const int ManagedReturnAdjustmentBytes = SequentialBundleStrideBytes - LinkIncrementBytes;
    public const int MaximumRegisterArguments = 8;
    public const int MaximumRegisterReturns = 1;

    public static HybridCpuNativeCallControlContractV1 Default { get; } = new();

    private HybridCpuNativeCallControlContractV1()
    {
        NativeAbiDigest = HybridCpuNativeAbiContractV2.Default.ContractDigest;
        TargetMachineDigest = HybridCpuTargetMachineContractV1.Default.ContractDigest;
        ContractDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
            SchemaId, SchemaVersion, NativeAbiDigest, TargetMachineDigest,
            $"link=x{HybridCpuNativeAbiContractV2.ReturnAddressRegister}:pc+{LinkIncrementBytes}",
            $"bundle-stride={SequentialBundleStrideBytes}",
            $"return=jalr:x0:x{HybridCpuNativeAbiContractV2.ReturnAddressRegister}:+{ManagedReturnAdjustmentBytes}",
            $"register-args={MaximumRegisterArguments}", $"register-returns={MaximumRegisterReturns}",
            "runtime-authority=none")))).ToLowerInvariant();
    }

    public string NativeAbiDigest { get; }
    public string TargetMachineDigest { get; }
    public string ContractDigest { get; }

    public bool MatchesExecutionContract(int linkIncrementBytes, int sequentialBundleStrideBytes) =>
        linkIncrementBytes == LinkIncrementBytes &&
        sequentialBundleStrideBytes == SequentialBundleStrideBytes;

    public ulong BiasReturnSentinel(ulong returnSentinel) =>
        checked(returnSentinel - (ulong)ManagedReturnAdjustmentBytes);
}
