using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.Target.Runtime;

namespace HybridCPU.Compiler.Cil;

public sealed record ManagedBoundedRecursionOptionsV1(
    bool Enabled,
    int MaximumDynamicDepth,
    int MaximumStackBytes,
    string OptionsDigest)
{
    public static ManagedBoundedRecursionOptionsV1 Disabled { get; } = Create(false, 0, 0);
    public static ManagedBoundedRecursionOptionsV1 Qualification { get; } = Create(
        true,
        ManagedBoundedRecursionContractV1.MaximumDynamicDepth,
        ManagedBoundedRecursionContractV1.MaximumStackBytes);

    public static ManagedBoundedRecursionOptionsV1 Create(bool enabled, int maximumDynamicDepth, int maximumStackBytes)
    {
        string digest = Hash(string.Join('|', ManagedBoundedRecursionContractV1.SchemaId, enabled,
            maximumDynamicDepth, maximumStackBytes, "proof=int32-countdown-to-zero",
            "overflow=compile-time-rejection", "runtime-authority=none"));
        return new(enabled, maximumDynamicDepth, maximumStackBytes, digest);
    }

    public bool IsValid => !Enabled
        ? MaximumDynamicDepth == 0 && MaximumStackBytes == 0 && OptionsDigest == Create(false, 0, 0).OptionsDigest
        : MaximumDynamicDepth is > 0 and <= ManagedBoundedRecursionContractV1.MaximumDynamicDepth &&
          MaximumStackBytes is > 0 and <= ManagedBoundedRecursionContractV1.MaximumStackBytes &&
          OptionsDigest == Create(true, MaximumDynamicDepth, MaximumStackBytes).OptionsDigest;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>
/// Versioned V1 extension for statically bounded scalar recursion. It grants no runtime
/// authority: admission proves all SCC ingress values and countdown transitions, while
/// the existing linker checks the post-RA conservative frame bound before emitting HCEXE.
/// </summary>
public sealed class ManagedBoundedRecursionContractV1
{
    public const string SchemaId = "hybridcpu.scalar-control-flow-v2.bounded-recursion/v1";
    public const int SchemaVersion = 1;
    public const int MaximumRecursiveSccs = 1;
    public const int MaximumMethodsPerRecursiveScc = 8;
    public const int MaximumDynamicDepth = 64;
    public const int MaximumStackBytes = 1024 * 1024;
    public const int CountdownStep = 1;

    public static ManagedBoundedRecursionContractV1 Default { get; } = new();

    private ManagedBoundedRecursionContractV1()
    {
        ContractDigest = Hash(string.Join('|', SchemaId, SchemaVersion, MaximumRecursiveSccs,
            MaximumMethodsPerRecursiveScc, MaximumDynamicDepth, MaximumStackBytes, CountdownStep,
            HybridCpuRestrictedStartupOptionsV1.Production.StackBase,
            HybridCpuRestrictedStartupOptionsV1.Production.StackSize,
            "signature=static-non-generic-int32(int32)",
            "base=arg0-zero-bypasses-recursive-edge", "transition=arg0-minus-one",
            "ingress=nonnegative-compile-time-int32", "stack=post-ra-conservative-upper-bound",
            "overflow=compile-time-rejection", "runtime-guard=none", "runtime-authority=none"));
    }

    public string ContractDigest { get; }
    public bool HasRuntimeAuthority => false;
    public bool RequiresRuntimeGuard => false;
    public bool ExtendsScalarControlFlowV1 => true;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
