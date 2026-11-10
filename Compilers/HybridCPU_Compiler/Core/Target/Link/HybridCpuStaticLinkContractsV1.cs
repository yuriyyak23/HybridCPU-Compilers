using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Compiler.Core.Target.Link;

public enum HybridCpuLinkStatusV1 : byte
{
    Success = 0,
    Unsupported = 1,
    Invalid = 2,
    CorruptInput = 3,
    VersionSkew = 4
}

public sealed record HybridCpuLinkInputV1(string Identity, byte[] ObjectBytes);

public sealed record HybridCpuStaticLinkOptionsV1(
    string SchemaId,
    ulong ImageBase,
    int PageAlignmentBytes,
    int MaximumInputs,
    int MaximumImageBytes,
    string ObjectFormatContractDigest,
    string TargetContractDigest,
    string ManagedAbiDigest,
    string OptionsDigest)
{
    public static HybridCpuStaticLinkOptionsV1 Production { get; } = CreateProduction();

    private static HybridCpuStaticLinkOptionsV1 CreateProduction()
    {
        const string schema = "hybridcpu.static-link/v1";
        const ulong imageBase = 0x0001_0000;
        const int page = 4096;
        // One HCO per admitted managed method plus a bounded allowance for runtime/type/dispatch objects.
        const int maximumInputs = 4096 + 256;
        const int maximumImageBytes = 256 * 1024 * 1024;
        string objectDigest = Object.HybridCpuObjectFormatContractV1.ContractDigest;
        string targetDigest = HybridCpuTargetPlatformContractV1.Default.ContractDigest;
        string abiDigest = Managed.HybridCpuManagedAbiFamilyV1.Default.ContractDigest;
        string options = Hash(string.Join('|', schema, imageBase, page, maximumInputs, maximumImageBytes,
            objectDigest, targetDigest, abiDigest, "section-kind,module,section", "no-comdat", "no-weak",
            "no-dynamic-libraries", "no-host-input", "no-timestamps", "no-wall-clock"));
        return new(schema, imageBase, page, maximumInputs, maximumImageBytes,
            objectDigest, targetDigest, abiDigest, options);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record HybridCpuLinkedSectionV1(
    string ModuleIdentity,
    string Name,
    HybridCpuObjectSectionKind Kind,
    ulong Address,
    ulong Size,
    int AlignmentBytes);

public sealed record HybridCpuLinkedSymbolV1(
    string Name,
    string ModuleIdentity,
    HybridCpuSymbolBinding Binding,
    HybridCpuSymbolVisibility Visibility,
    ulong Address,
    ulong Size);

public sealed record HybridCpuAppliedRelocationV1(
    string ModuleIdentity,
    string SectionName,
    ulong Offset,
    HybridCpuRelocationKind Kind,
    string TargetSymbol,
    long Addend,
    ulong PlaceAddress,
    ulong TargetAddress,
    ulong EncodedValue,
    int WidthBits,
    string? ResolvedViaThunkSymbol = null);

public sealed record HybridCpuLinkDiagnosticV1(string Code, string Message);

public sealed record HybridCpuStaticLinkArtifactV1(
    HybridCpuLinkStatusV1 Status,
    ulong ImageBase,
    byte[] ImageBytes,
    string ImageSha256,
    string LinkMapDigest,
    string OptionsDigest,
    IReadOnlyList<HybridCpuLinkedSectionV1> Sections,
    IReadOnlyList<HybridCpuLinkedSymbolV1> Symbols,
    IReadOnlyList<HybridCpuAppliedRelocationV1> AppliedRelocations,
    IReadOnlyList<HybridCpuLinkDiagnosticV1> Diagnostics)
{
    public bool LinkerResolvedSymbols => Status == HybridCpuLinkStatusV1.Success;
    public bool HasRuntimeAuthority => false;
    public bool HasExecutionAuthority => false;
    public bool HasPublicationAuthority => false;
}
