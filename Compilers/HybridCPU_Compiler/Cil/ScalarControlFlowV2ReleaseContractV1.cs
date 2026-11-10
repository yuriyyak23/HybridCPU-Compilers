using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;

namespace HybridCPU.Compiler.Cil;

public sealed class ScalarControlFlowV2CompatibilityFingerprintV1
{
    public const string SchemaId = "hybridcpu.scalar-control-flow-v2.compatibility-fingerprint/v1";

    public static ScalarControlFlowV2CompatibilityFingerprintV1 Default { get; } = new();

    private ScalarControlFlowV2CompatibilityFingerprintV1()
    {
        CompilerContractVersion = HybridCpuCompilerContract.Version;
        TargetDigest = HybridCpuTargetMachineContractV1.Default.ContractDigest;
        NativeAbiDigest = HybridCpuNativeAbiContractV2.Default.ContractDigest;
        ManagedAbiDigest = HybridCpuManagedAbiFamilyV1.Default.ContractDigest;
        CallControlDigest = HybridCpuNativeCallControlContractV1.Default.ContractDigest;
        StartupDigest = HybridCpuRestrictedStartupOptionsV1.Production.OptionsDigest;
        ContractDigest = Hash(string.Join('|', SchemaId, CompilerContractVersion,
            HybridCpuTargetMachineContractV1.TargetTriple, HybridCpuTargetMachineContractV1.PointerBitWidth,
            HybridCpuTargetMachineContractV1.ArchitecturalRegisterCount,
            HybridCpuTargetMachineContractV1.ArchitecturalRegisterBitWidth,
            HybridCpuInstructionBundle.SlotCount, HybridCpuBundleSerializer.BundleSizeBytes,
            HybridCpuManagedCallRelocationContractV1.InstructionSlotSizeBytes,
            HybridCpuNativeCallControlContractV1.LinkIncrementBytes,
            HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes,
            HybridCpuNativeAbiContractV2.StackAlignmentBytes,
            string.Join(',', HybridCpuNativeAbiContractV2.Default.ArgumentRegisters),
            string.Join(',', HybridCpuNativeAbiContractV2.Default.ReturnRegisters),
            string.Join(',', HybridCpuNativeAbiContractV2.Default.CallerSavedRegisters),
            string.Join(',', HybridCpuNativeAbiContractV2.Default.CalleeSavedRegisters),
            string.Join(',', HybridCpuNativeAbiContractV2.Default.ReservedRegisters),
            TargetDigest, NativeAbiDigest, ManagedAbiDigest, CallControlDigest, StartupDigest,
            "typed-slot=structural-only", "runtime-authority=ise-loader-execution-fault-publication-commit-retire"));
    }

    public int CompilerContractVersion { get; }
    public string TargetDigest { get; }
    public string NativeAbiDigest { get; }
    public string ManagedAbiDigest { get; }
    public string CallControlDigest { get; }
    public string StartupDigest { get; }
    public string ContractDigest { get; }
    public bool CompilerMetadataHasRuntimeAuthority => false;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class ScalarControlFlowV2ReleaseContractV1
{
    public const string SchemaId = "hybridcpu.scalar-control-flow-v2.release/v1";
    private static readonly string[] DeferredTable =
    [
        "boolean-and-narrow-integer-boundary-qualification", "int64-uint64-boundary-qualification",
        "native-int-native-uint", "unproved-or-dynamic-recursion", "switch",
        "heap-gc-arrays", "managed-byref-interior-liveness", "exception-handling",
        "reflection-dynamic", "async-threading-tls",
        "unsupported-generics-layouts", "default-enablement"
    ];

    public static ScalarControlFlowV2ReleaseContractV1 Default { get; } = new();

    private ScalarControlFlowV2ReleaseContractV1()
    {
        ProfileDigest = ScalarControlFlowV2ProfileContractV1.Default.ContractDigest;
        CompatibilityFingerprint = ScalarControlFlowV2CompatibilityFingerprintV1.Default.ContractDigest;
        DeferredCapabilities = Array.AsReadOnly(DeferredTable.Order(StringComparer.Ordinal).ToArray());
        ContractDigest = Hash(string.Join('|', SchemaId, ProfileDigest, CompatibilityFingerprint,
            Disposition, DefaultEnabled, AllowsFallback, string.Join(';', DeferredCapabilities)));
    }

    public string Disposition => "VerifiedOptInPreview";
    public bool DefaultEnabled => false;
    public bool AllowsFallback => false;
    public bool HasRuntimeAuthority => false;
    public string ProfileDigest { get; }
    public string CompatibilityFingerprint { get; }
    public IReadOnlyList<string> DeferredCapabilities { get; }
    public string ContractDigest { get; }

    public bool Validate() =>
        ProfileDigest == ScalarControlFlowV2ProfileContractV1.Default.ContractDigest &&
        CompatibilityFingerprint == ScalarControlFlowV2CompatibilityFingerprintV1.Default.ContractDigest &&
        !DefaultEnabled && !AllowsFallback && !HasRuntimeAuthority;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
