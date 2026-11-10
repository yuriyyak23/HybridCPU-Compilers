using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Runtime;

public enum HybridCpuStartupStatusV1 : byte
{
    Success = 0,
    Unsupported = 1,
    Invalid = 2,
    CorruptInput = 3,
    VersionSkew = 4
}

public sealed record HybridCpuRestrictedStartupOptionsV1(
    string SchemaId,
    ulong StackBase,
    ulong StackSize,
    ulong ReturnSentinel,
    int PackageHeaderBytes,
    int PayloadAlignmentBytes,
    int MaximumPackageBytes,
    string LinkOptionsDigest,
    string TargetContractDigest,
    string NativeAbiDigest,
    string ManagedAbiDigest,
    string OptionsDigest)
{
    public static HybridCpuRestrictedStartupOptionsV1 Production { get; } = CreateProduction();

    private static HybridCpuRestrictedStartupOptionsV1 CreateProduction()
    {
        const string schema = "hybridcpu.restricted-startup/v1";
        const ulong stackBase = 0x2000_0000;
        const ulong stackSize = 1024 * 1024;
        const ulong returnSentinel = 0xffff_ffff_ffff_ffe0;
        // Bytes 384..399 carry the optional global-pointer register/address pair.
        // Keep bootstrap metadata beyond those fields and reserve one aligned contract tail.
        const int headerBytes = 416;
        const int payloadAlignment = 4096;
        const int maximumPackageBytes = 256 * 1024 * 1024 + payloadAlignment;
        string linkOptions = HybridCpuStaticLinkOptionsV1.Production.OptionsDigest;
        string target = HybridCpuTargetPlatformContractV1.Default.ContractDigest;
        string nativeAbi = HybridCpuNativeAbiContractV2.Default.ContractDigest;
        string managedAbi = HybridCpuManagedAbiFamilyV1.Default.ContractDigest;
        string digest = Hash(string.Join('|', schema, stackBase, stackSize, returnSentinel, headerBytes,
            payloadAlignment, maximumPackageBytes, linkOptions, target, nativeAbi, managedAbi,
            $"bundle={HybridCpuBundleSerializer.BundleSizeBytes}", "entry=global-default-code",
            HybridCpuPlatformContractV1.ContractDigest,
            "static-init=registered-only", "helpers=bootstrap-only", "host-services=process-exit-only", "no-tls",
            "managed-eh-sections-default-off", "no-fault-mapping", "no-interop", "no-dynamic-link", "no-timestamps", "no-wall-clock"));
        return new(schema, stackBase, stackSize, returnSentinel, headerBytes, payloadAlignment,
            maximumPackageBytes, linkOptions, target, nativeAbi, managedAbi, digest);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record HybridCpuStartupRegisterStateV1(
    int StackPointerRegister,
    int FramePointerRegister,
    int ThreadPointerRegister,
    int ReturnAddressRegister,
    int ReturnValueRegister,
    ulong StackPointer,
    ulong FramePointer,
    ulong ThreadPointer,
    ulong ReturnAddress,
    int GlobalPointerRegister = HybridCpuNativeAbiContractV2.GlobalPointerRegister,
    ulong GlobalPointer = 0);

public sealed record HybridCpuRestrictedStartupRequestV1(
    HybridCpuStaticLinkArtifactV1 LinkedImage,
    string EntrySymbol,
    int ReturnAddressAdjustmentBytes = 0,
    HybridCpuImageRuntimeBootstrapDescriptorV1? RuntimeBootstrap = null,
    string? GlobalPointerSymbol = null);

public sealed record HybridCpuStartupDiagnosticV1(string Code, string Message);

public sealed record HybridCpuRestrictedImageV1(
    HybridCpuStartupStatusV1 Status,
    byte[] PackageBytes,
    byte[] ImageBytes,
    ulong ImageBase,
    ulong EntryAddress,
    string EntrySymbol,
    HybridCpuStartupRegisterStateV1? InitialRegisters,
    string PackageSha256,
    string ImageSha256,
    string LinkMapDigest,
    string OptionsDigest,
    IReadOnlyList<HybridCpuStartupDiagnosticV1> Diagnostics,
    HybridCpuImageRuntimeBootstrapDescriptorV1? RuntimeBootstrap = null)
{
    public bool StartupContractSatisfied => Status == HybridCpuStartupStatusV1.Success;
    public bool HasRuntimeAuthority => false;
    public bool HasExecutionAuthority => false;
    public bool HasPublicationAuthority => false;
    public bool HasCommitOrRetireAuthority => false;
}
