using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace HybridCPU.Compiler.NativeAot;

public enum NativeAotSeamStatusV1 : byte
{
    Success = 0,
    Unsupported = 1,
    InvalidInput = 2,
    VersionSkew = 3,
    CompilationFailed = 4
}

public enum NativeAotSeamOutputKindV1 : byte
{
    RawCode = 0,
    RelocatableObject = 1,
    RestrictedImage = 2
}

public sealed record NativeAotSeamRequestV1(
    NativeAotSeamOutputKindV1 OutputKind,
    string AssemblyPath,
    string TypeName,
    string MethodName,
    string OutputPath,
    string SourceCommit,
    string PatchDigest);

public sealed record NativeAotSeamDiagnosticV1(string Code, string Message);

public sealed record NativeAotObjectArtifactV1(
    string SchemaId,
    NativeAotSeamArtifactV1 CodeArtifact,
    string ObjectFormatContractDigest,
    string ObjectOptionsDigest,
    string ObjectMetadataDigest,
    string ObjectSha256,
    int ObjectLength)
{
    [JsonIgnore]
    public bool HasLinkAuthority => false;

    [JsonIgnore]
    public bool HasRuntimeAuthority => false;

    [JsonIgnore]
    public bool HasPublicationAuthority => false;
}

public sealed record NativeAotRestrictedImageArtifactV1(
    string SchemaId,
    NativeAotSeamArtifactV1 CodeArtifact,
    string RegisterAllocationContractDigest,
    string RegisterAllocationOptionsDigest,
    string RegisterAllocationWitnessDigest,
    string ObjectFormatContractDigest,
    string ObjectSha256,
    string StaticLinkOptionsDigest,
    string LinkMapDigest,
    string StartupOptionsDigest,
    string EntrySymbol,
    ulong EntryAddress,
    string PackageSha256,
    int PackageLength,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProfileId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BodyPresentationDigest = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ManagedGraphDigest = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CompiledMethodCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OrderedObjectDigest = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BackendProvenanceDigest = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BoundedRecursionContractDigest = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RecursionProofDigest = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RecursionStackEvidenceDigest = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaximumDynamicDepth = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RequiredStackBytes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? DerivedRequiredWorkstreams = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? WorkstreamEvidence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? UnclassifiedRuntimeModules = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WorkstreamDiscoveryDigest = null)
{
    [JsonIgnore]
    public bool HasRuntimeAuthority => false;

    [JsonIgnore]
    public bool HasExecutionAuthority => false;

    [JsonIgnore]
    public bool HasPublicationAuthority => false;
}

public sealed record NativeAotPresentedBodyV1(string TypeName, string MethodName, int MetadataToken = 0);

public sealed record NativeAotBodyPresentationV1(
    string SchemaId,
    int SchemaVersion,
    string ProfileId,
    string AssemblySha256,
    NativeAotPresentedBodyV1 Root,
    IReadOnlyList<NativeAotPresentedBodyV1> PresentedBodies,
    string ContractDigest)
{
    public const string Schema = "hybridcpu.nativeaot-body-presentation/v1";
    public const int Version = 2;

    [JsonIgnore]
    public bool OwnsReachability => false;

    [JsonIgnore]
    public bool HasRuntimeAuthority => false;
}

public sealed record NativeAotSeamArtifactV1(
    string SchemaId,
    NativeAotSeamStatusV1 Status,
    string SourceCommit,
    string PatchDigest,
    string TargetContractDigest,
    string ManagedAbiDigest,
    string CilMatrixDigest,
    string CilOptionsDigest,
    string MethodIdentity,
    string MethodToken,
    string PeSha256,
    string CodeSha256,
    int CodeLength,
    IReadOnlyList<string> Relocations,
    IReadOnlyList<string> FrameRecords,
    string? GcInfo,
    string? EhInfo,
    IReadOnlyList<NativeAotSeamDiagnosticV1> Diagnostics)
{
    [JsonIgnore]
    public bool HasRuntimeAuthority => false;

    [JsonIgnore]
    public bool HasPublicationAuthority => false;
}

public static class NativeAotSeamBaselineV1
{
    public const string SchemaId = "hybridcpu.nativeaot-seam/v1";
    public const string SourceCommit = "94ea82652cdd4e0f8046b5bd5becbd11461482ca";
    public const string SdkVersion = "10.0.105";
    public const string SourceArchiveSha256 = "694b9134d338db6b0d31b1a73d9c89e5419432f2f0622b610ecdf5455e851b96";
    public const string PatchDigest = "e8c8169badfd8b70541c7ee733b384fdb41285b0652d7a6c94d427647d93e1b9";

    public static string OptionsDigest => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
        SchemaId, SourceCommit, SdkVersion, PatchDigest, "single-method", "no-host-fallback", "no-object-emission",
        "relocations=reject", "gc=reject", "eh=reject", "interop=reject")))).ToLowerInvariant();
}
