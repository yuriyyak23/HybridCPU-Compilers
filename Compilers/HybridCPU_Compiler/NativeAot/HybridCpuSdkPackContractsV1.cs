using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;

namespace HybridCPU.Compiler.NativeAot;

public sealed record HybridCpuRuntimePackManifestV1(
    string SchemaId,
    string PackVersion,
    string TargetRid,
    string TargetEnvironment,
    IReadOnlyList<string> SupportedHostRids,
    IReadOnlyList<string> SupportedPublishSdkVersions,
    string SourceCommit,
    string PatchDigest,
    string PublishPatchDigest,
    string PatchSetDigest,
    string TargetContractDigest,
    string ManagedAbiDigest,
    string ManagedFeatureSetDigest,
    string ObjectFormatDigest,
    string LinkOptionsDigest,
    string StartupOptionsDigest,
    string GuestAbiBindingPackageIdentity,
    string GuestAbiBindingPackageDigest,
    IReadOnlyList<string> QualifiedWorkstreams,
    IReadOnlyList<string> UnsupportedWorkstreams,
    IReadOnlyList<string> PublishQualifiedWorkstreams,
    string ManagedSafetyLevel,
    bool FspAllowed,
    bool VdsaAllowed,
    bool RequiresLlvm,
    string ContractDigest);

public sealed record HybridCpuPublishProvenanceV1(
    string SchemaId,
    string PackContractDigest,
    string PackVersion,
    string TargetRid,
    string HostRid,
    string SdkVersion,
    string SourceCommit,
    string PatchDigest,
    string PublishPatchDigest,
    string PatchSetDigest,
    string TargetContractDigest,
    string ManagedAbiDigest,
    string ManagedFeatureSetDigest,
    string LinkerIdentity,
    string LinkOptionsDigest,
    string InputAssemblySha256,
    string ImageSha256,
    int ImageLength,
    IReadOnlyList<string> RequiredWorkstreams,
    string? IlCompilerSha256,
    string? RuntimeReferenceSetDigest,
    string? InstalledPackFilesSha256,
    string? DotNetHostSha256,
    string? AdapterSha256,
    bool TraversedIlCompilerGraph,
    string OptionsDigest,
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? DeclaredWorkstreams = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WorkstreamDiscoveryDigest = null)
{
    [JsonIgnore]
    public bool HasRuntimeAuthority => false;

    [JsonIgnore]
    public bool HasPublicationAuthority => false;
}

public static class HybridCpuSdkPackContractV1
{
    public const string SchemaId = "hybridcpu.sdk-pack/v1";
    public const string ProvenanceSchemaId = "hybridcpu.publish-provenance/v1";
    public const string PackVersion = "1.15.193-refplan7-phase15";
    public const string TargetRid = "hybridcpu";
    public const string TargetEnvironment = "controlled-bare-metal-simulator-v1";
    public const string LinkerIdentity = "HybridCpuStaticLinkerV1";
    public const string PublishPatchDigest = "1e0a458fef0d35b58df9c6eb851d26ddf6c7cad577c6d9510c14376c90c26644";

    public static IReadOnlyList<string> SupportedHostRids { get; } = ["win-x64"];
    public static IReadOnlyList<string> SupportedPublishSdkVersions { get; } = ["10.0.204"];

    // These workstreams are wired through the NativeAOT body-presentation, managed graph importer,
    // object linker and restricted-image path. EH remains component-qualified but is deliberately
    // withheld until finally continuation, native unwind/state transfer, GC-root/safepoint and
    // loader-backed handled/unhandled execution evidence qualify the complete publish boundary.
    public static IReadOnlyList<string> PublishQualifiedWorkstreams { get; } =
    [
        "exact-aot-generics",
        "gc-maps-safepoints",
        "static-type-initialization",
        "szarray-core",
        "type-layout-object-references",
        "utf16-string-literals",
        "virtual-interface-dispatch"
    ];

    public static HybridCpuRuntimePackManifestV1 CreateManifest()
    {
        HybridCpuManagedFeatureSetV1 features = HybridCpuManagedFeatureSetV1.Default;
        string[] qualified = features.Workstreams
            .Where(static item => item.Support == HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff)
            .Select(static item => item.Identity).Order(StringComparer.Ordinal).ToArray();
        string[] unsupported = features.Workstreams
            .Where(static item => item.Support != HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff)
            .Select(static item => item.Identity).Order(StringComparer.Ordinal).ToArray();
        string digest = Hash(string.Join('|', SchemaId, PackVersion, TargetRid, TargetEnvironment,
            string.Join(',', SupportedHostRids), string.Join(',', SupportedPublishSdkVersions), NativeAotSeamBaselineV1.SourceCommit,
            NativeAotSeamBaselineV1.PatchDigest, PublishPatchDigest, PatchSetDigest,
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, features.ContractDigest,
            HybridCpuObjectFormatContractV1.ContractDigest, HybridCpuStaticLinkOptionsV1.Production.OptionsDigest,
            HybridCpuRestrictedStartupOptionsV1.Production.OptionsDigest,
            DoomSharpGuestAbiBindingPackageV1.PackageIdentity, DoomSharpGuestAbiBindingPackageV1.ContractDigest,
            string.Join(',', qualified),
            string.Join(',', unsupported), $"publish-qualified={string.Join(',', PublishQualifiedWorkstreams)}",
            features.ManagedSafetyLevel, features.FspAllowed, features.VdsaAllowed,
            "requires-llvm=false"));
        return new(SchemaId, PackVersion, TargetRid, TargetEnvironment, SupportedHostRids, SupportedPublishSdkVersions,
            NativeAotSeamBaselineV1.SourceCommit, NativeAotSeamBaselineV1.PatchDigest,
            PublishPatchDigest, PatchSetDigest,
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, features.ContractDigest,
            HybridCpuObjectFormatContractV1.ContractDigest, HybridCpuStaticLinkOptionsV1.Production.OptionsDigest,
            HybridCpuRestrictedStartupOptionsV1.Production.OptionsDigest,
            DoomSharpGuestAbiBindingPackageV1.PackageIdentity, DoomSharpGuestAbiBindingPackageV1.ContractDigest,
            qualified, unsupported, PublishQualifiedWorkstreams,
            features.ManagedSafetyLevel.ToString(), features.FspAllowed, features.VdsaAllowed, false, digest);
    }

    public static string? Validate(
        HybridCpuRuntimePackManifestV1 manifest,
        string targetRid,
        string hostRid,
        string sdkVersion,
        IReadOnlyList<string> requiredWorkstreams)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(requiredWorkstreams);
        HybridCpuRuntimePackManifestV1 expected = CreateManifest();
        if (!string.Equals(targetRid, TargetRid, StringComparison.Ordinal) ||
            !string.Equals(manifest.TargetRid, TargetRid, StringComparison.Ordinal))
            return "HCPUB1001: target RID must be exactly 'hybridcpu'.";
        if (!SupportedHostRids.Contains(hostRid, StringComparer.Ordinal) ||
            !manifest.SupportedHostRids.Contains(hostRid, StringComparer.Ordinal))
            return $"HCPUB1002: host RID '{hostRid}' is not qualified by this pack.";
        if (!SupportedPublishSdkVersions.Contains(sdkVersion, StringComparer.Ordinal) ||
            !manifest.SupportedPublishSdkVersions.Contains(sdkVersion, StringComparer.Ordinal))
            return $"HCPUB1007: publish SDK '{sdkVersion}' is not qualified by this pack.";
        if (!string.Equals(manifest.ContractDigest, expected.ContractDigest, StringComparison.Ordinal) ||
            !string.Equals(manifest.PackVersion, expected.PackVersion, StringComparison.Ordinal) ||
            !string.Equals(manifest.SourceCommit, expected.SourceCommit, StringComparison.Ordinal) ||
            !string.Equals(manifest.PatchDigest, expected.PatchDigest, StringComparison.Ordinal) ||
            !string.Equals(manifest.PublishPatchDigest, expected.PublishPatchDigest, StringComparison.Ordinal) ||
            !string.Equals(manifest.PatchSetDigest, expected.PatchSetDigest, StringComparison.Ordinal) ||
            !string.Equals(manifest.TargetContractDigest, expected.TargetContractDigest, StringComparison.Ordinal) ||
            !string.Equals(manifest.ManagedAbiDigest, expected.ManagedAbiDigest, StringComparison.Ordinal) ||
            !string.Equals(manifest.ManagedFeatureSetDigest, expected.ManagedFeatureSetDigest, StringComparison.Ordinal) ||
            !string.Equals(manifest.ObjectFormatDigest, expected.ObjectFormatDigest, StringComparison.Ordinal) ||
            !string.Equals(manifest.LinkOptionsDigest, expected.LinkOptionsDigest, StringComparison.Ordinal) ||
            !string.Equals(manifest.StartupOptionsDigest, expected.StartupOptionsDigest, StringComparison.Ordinal) ||
            !string.Equals(manifest.GuestAbiBindingPackageIdentity, expected.GuestAbiBindingPackageIdentity, StringComparison.Ordinal) ||
            !string.Equals(manifest.GuestAbiBindingPackageDigest, expected.GuestAbiBindingPackageDigest, StringComparison.Ordinal) ||
            manifest.RequiresLlvm)
            return "HCPUB1003: compiler/runtime/ABI/toolchain pack version skew detected.";
        string[] unsupportedForPublish = requiredWorkstreams
            .Where(item => !manifest.PublishQualifiedWorkstreams.Contains(item, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return unsupportedForPublish.Length == 0
            ? null
            : $"HCPUB1004: unsupported managed publish workstream(s): {string.Join(", ", unsupportedForPublish)}.";
    }

    public static string OptionsDigest(string hostRid, string sdkVersion, IReadOnlyList<string> requirements) =>
        Hash(string.Join('|', SchemaId, CreateManifest().ContractDigest, TargetRid, hostRid, sdkVersion,
            string.Join(',', requirements.Order(StringComparer.Ordinal)), "no-host-fallback", "no-jit-fallback",
            "no-llvm-fallback", "deterministic-work-only"));

    public static string? ValidateDeclaredWorkstreams(
        IReadOnlyList<string> declaredWorkstreams,
        IReadOnlyList<string> derivedWorkstreams)
    {
        ArgumentNullException.ThrowIfNull(declaredWorkstreams);
        ArgumentNullException.ThrowIfNull(derivedWorkstreams);
        if (declaredWorkstreams.Count == 0) return null;
        string[] omitted = derivedWorkstreams.Except(declaredWorkstreams, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return omitted.Length == 0 ? null :
            $"HCPUB1010: declared managed workstreams omit compiler-derived requirement(s): {string.Join(", ", omitted)}.";
    }

    public static string? ValidateInstalledPackFiles(
        string manifestPath,
        string adapterPath,
        string ilCompilerPath,
        string runtimeReferenceDirectory)
    {
        try
        {
            string manifestFullPath = Path.GetFullPath(manifestPath);
            string root = Path.GetDirectoryName(manifestFullPath)!;
            string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestFullPath));
            JsonElement manifest = document.RootElement;
            if (manifest.GetProperty("schema").GetString() != "hybridcpu.installed-pack-files/v1" ||
                manifest.GetProperty("packVersion").GetString() != PackVersion ||
                manifest.GetProperty("targetRid").GetString() != TargetRid)
                return "HCPUB1008: installed pack file manifest identity is invalid.";
            Dictionary<string, (string Digest, long Bytes)> entries = new Dictionary<string, (string Digest, long Bytes)>(StringComparer.Ordinal);
            foreach (JsonElement element in manifest.GetProperty("files").EnumerateArray())
            {
                string? name = element.GetProperty("name").GetString();
                string? digest = element.GetProperty("sha256").GetString();
                long bytes = element.GetProperty("bytes").GetInt64();
                if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains("..", StringComparison.Ordinal) ||
                    name.Contains("llvm", StringComparison.OrdinalIgnoreCase) || digest is not { Length: 64 } || bytes < 0 ||
                    !entries.TryAdd(name.Replace('/', Path.DirectorySeparatorChar), (digest, bytes)))
                    return "HCPUB1008: installed pack file manifest contains an invalid entry.";
            }
            foreach ((string name, (string digest, long bytes)) in entries)
            {
                string path = Path.GetFullPath(Path.Combine(root, name));
                if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path) ||
                    new FileInfo(path).Length != bytes ||
                    !string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), digest, StringComparison.Ordinal))
                    return $"HCPUB1008: installed pack file digest mismatch: {name}.";
            }
            string[] required = [Path.GetFullPath(adapterPath), Path.GetFullPath(ilCompilerPath)];
            string[] references = Directory.GetFiles(runtimeReferenceDirectory, "*.dll");
            foreach (string path in required.Concat(references))
            {
                if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !entries.ContainsKey(Path.GetRelativePath(root, path)))
                    return "HCPUB1008: selected compiler/runtime input is outside the installed pack manifest.";
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return $"HCPUB1008: installed pack file manifest cannot be validated: {exception.Message}";
        }
    }

    public static string PatchSetDigest =>
        Hash($"{NativeAotSeamBaselineV1.PatchDigest}|{PublishPatchDigest}");

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
