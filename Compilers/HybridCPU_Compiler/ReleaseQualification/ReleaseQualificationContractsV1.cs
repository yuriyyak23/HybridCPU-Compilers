using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridCPU.Compiler.Core.IR.Contracts;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Llvm;
using HybridCPU.Compiler.NativeAot;
using HybridCPU.Compiler.Oracle;

namespace HybridCPU.Compiler.Release;

public enum HybridCpuReleaseProfileV1 : byte
{
    Native = 0,
    Llvm = 1,
    DotNetAotRestricted = 2,
    DotNetAotManaged = 3,
    Full = 4
}

public enum HybridCpuReleaseDispositionV1 : byte
{
    ReleaseAuthorized = 0,
    VerifiedDefaultOff = 1,
    Unsupported = 2
}

public enum HybridCpuReleaseFeatureSupportV1 : byte
{
    ReleaseAuthorized = 0,
    VerifiedDefaultOff = 1,
    ContractOnly = 2,
    Unsupported = 3
}

public sealed record HybridCpuReleaseIdentityV1(
    string CompilerCommit,
    string TargetRevision,
    string MachineDigest,
    string TopologyDigest,
    string TargetDigest,
    string NativeAbiDigest,
    string ManagedAbiDigest,
    string EvidenceSchema,
    string CapabilitySchema,
    string FrontendToolchain,
    string OptionsDigest,
    string ProfileDataHashOrAbsence,
    string RuntimePackAndLinkerIdentity);

public sealed record HybridCpuReleaseProfileRowV1(
    HybridCpuReleaseProfileV1 Profile,
    HybridCpuReleaseDispositionV1 Disposition,
    bool DefaultEnabled,
    HybridCpuReleaseIdentityV1 Identity,
    IReadOnlyList<string> Artifacts,
    IReadOnlyList<string> EnabledCapabilities,
    IReadOnlyList<string> UnsupportedCapabilities,
    string DeterministicFallback,
    string QualificationEvidenceSha256);

public sealed record HybridCpuReleaseFeatureRowV1(
    string Feature,
    HybridCpuReleaseFeatureSupportV1 Native,
    HybridCpuReleaseFeatureSupportV1 Llvm,
    HybridCpuReleaseFeatureSupportV1 DotNetAotRestricted,
    HybridCpuReleaseFeatureSupportV1 DotNetAotManaged,
    HybridCpuReleaseFeatureSupportV1 Full,
    string AuthorityOwner,
    string Fallback);

public sealed record HybridCpuCompatibilityWindowV1(
    string Schema,
    int Major,
    int MinimumMinor,
    int MaximumMinor,
    bool LosslessMigrationAvailable,
    string OutsideWindowDisposition);

public sealed record HybridCpuReleaseBenchmarkSummaryV1(
    string Corpus,
    int Cases,
    int DeterministicRuns,
    string ScheduleFingerprint,
    string BundleFingerprint,
    string ImageFingerprint,
    int BundleCount,
    long DeterministicInstructionBudget,
    int OracleComparableCases,
    int OracleUnknownCases,
    string OracleDisposition,
    string OracleContractDigest,
    string OracleEvidenceSha256);

public sealed record HybridCpuReleaseTelemetryV1(
    string Schema,
    string CompilerCommit,
    string Corpus,
    int Runs,
    double CompileMilliseconds,
    long PeakWorkingSetBytes,
    bool SelectsProductionOutput);

public sealed record HybridCpuReleaseFeatureMatrixV1(
    string Schema,
    string CompilerCommit,
    IReadOnlyList<HybridCpuReleaseFeatureRowV1> Features,
    string Digest);

public sealed record HybridCpuReleaseManifestV1(
    string Schema,
    string CompilerCommit,
    string Phase26EvidenceSha256,
    IReadOnlyList<HybridCpuReleaseProfileRowV1> Profiles,
    IReadOnlyList<HybridCpuReleaseFeatureRowV1> Features,
    IReadOnlyList<HybridCpuCompatibilityWindowV1> CompatibilityWindows,
    HybridCpuReleaseBenchmarkSummaryV1 Benchmark,
    IReadOnlyList<string> AuthorityProhibitions,
    IReadOnlyList<string> KnownUnsupported,
    string DeterministicDigest);

public sealed record HybridCpuReleaseValidationV1(bool IsValid, string Code, string Reason);

public static class HybridCpuReleaseQualificationV1
{
    public const string SchemaId = "hybridcpu.release-manifest/v1";
    public const string FeatureMatrixSchemaId = "hybridcpu.release-feature-matrix/v1";
    public const string Phase26PackContractDigest = "9d726bee108d236cbc45d2d90032f4e82376965d9c08142d14e05fad3a1454f3";
    public const string RefPlan7Phase05PackContractDigest = "af5ef227db7e820619b7892010c38c2909fe0ed5fe9cd1391ba23017171e5e24";
    public const string RefPlan7Phase06PackContractDigest = "3ecf97210c9378ae7983a787c098a5c55b8f1c4940ec102569e4850f4463dcf3";
    public const string RefPlan7Phase07PackContractDigest = "c2a44f67dcc0468d5a155a4633c5fe3a069ecfc9abf6cc924f32488d3d3085db";
    public const string RefPlan7Phase08PackContractDigest = "6ccfb96a7b234ca4bd1f3b32c92770c5c3c1893b8cd0cef62d10d4a2363c06c7";
    public const string RefPlan7Phase09PackContractDigest = "29f0793a5db109deb6b5300381a88650a18b5cc5fa7378a98714e54da5169681";
    public const string RefPlan7Phase10PackContractDigest = "61189e31f45f81b3148e09c5dfcc7521985aefe290706644fc6c6576cecb6510";
    public const string RefPlan7Phase11PackContractDigest = "3c4104b27c48c4e605d386230e89bc4ba3e75dda1c9075e1138cbf678ce71e35";
    public const string RefPlan7Phase12PackContractDigest = "8f771fc6e04d3aae39513a697b9202c45cf0423e7fe1e59798d2d23621d254ec";
    public const string RefPlan7Phase13PackContractDigest = "64d1042dd3f8f265e4529d0358063cad924c3b89ce8b51e5aabece98524f20c6";
    public const string RefPlan7Phase14PackContractDigest = "c5e4af50d6ce1de523e0b7b6c79d73a8d7869964e62c228c9bedb0d60f475ce1";
    public const string RefPlan7Phase15PackContractDigest = "7fa95a6012991c7235c0a4e04da96d8852b22acce070628a36aa1ab5ccc260e0";
    public const string Phase26PatchSetDigest = "d4dbe99064e7836027f47b60ba871f9c36df2b29ad634265deb8a55482e21d49";
    public const string Phase26PublishSdk = "10.0.204";
    public const string AbsentProfileHash = "absent";

    private static readonly string[] NativeArtifacts =
    [
        "canonical-ir", "schedule", "bundles", "asm", "binary", "evidence", "scheduling-report", "provenance"
    ];

    private static readonly string[] AuthorityProhibitions =
    [
        "runtime-legality", "runtime-freshness", "runtime-execution", "runtime-publication", "runtime-commit", "runtime-retire"
    ];

    public static HybridCpuReleaseManifestV1 Create(
        string compilerCommit,
        string phase26EvidenceSha256,
        HybridCpuReleaseBenchmarkSummaryV1 benchmark)
    {
        RequiredHex(compilerCommit, 40, nameof(compilerCommit));
        RequiredHex(phase26EvidenceSha256, 64, nameof(phase26EvidenceSha256));
        ArgumentNullException.ThrowIfNull(benchmark);

        string target = HybridCpuTargetPlatformContractV1.Default.ContractDigest;
        string nativeAbi = HybridCpuNativeAbiContractV2.Default.ContractDigest;
        string managedAbi = HybridCpuManagedAbiFamilyV1.Default.ContractDigest;
        string machine = HybridCpuMachineDescriptionV1.Default.ContractDigest;
        string topology = HybridCpuMachineTopologyV1.Default.ContractDigest;
        string evidenceSchema = SchemaIdentity(CompilerCrossLayerSchemaCatalogV1.Envelope);
        string capabilitySchema = SchemaIdentity(CompilerCrossLayerSchemaCatalogV1.Capability);
        HybridCpuRuntimePackManifestV1 sdkPack = HybridCpuSdkPackContractV1.CreateManifest();
        string[] managedPublishCapabilities = sdkPack.PublishQualifiedWorkstreams
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] managedUnsupportedCapabilities = sdkPack.QualifiedWorkstreams
            .Concat(sdkPack.UnsupportedWorkstreams)
            .Except(managedPublishCapabilities, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        HybridCpuReleaseIdentityV1 Identity(string toolchain, string options, string runtimePack) => new(
            compilerCommit,
            HybridCpuTargetMachineContractV1.TargetArchitectureRevision,
            machine,
            topology,
            target,
            nativeAbi,
            managedAbi,
            evidenceSchema,
            capabilitySchema,
            toolchain,
            options,
            AbsentProfileHash,
            runtimePack);

        HybridCpuReleaseProfileRowV1[] profiles =
        [
            new(HybridCpuReleaseProfileV1.Native, HybridCpuReleaseDispositionV1.ReleaseAuthorized, true,
                Identity("native-asm-v1/native-carrier-v1", Hash("phase27|native|canonical-defaults"),
                    $"hco:{HybridCpuObjectFormatContractV1.ContractDigest}|linker:HybridCpuStaticLinkerV1"),
                NativeArtifacts, ["native-value-codegen", "exact-w8-bundles"], ["standalone-object-container"],
                "Exact existing program-order fallback; unsupported object mode is reported without substituting another frontend.",
                phase26EvidenceSha256),
            new(HybridCpuReleaseProfileV1.Llvm, HybridCpuReleaseDispositionV1.VerifiedDefaultOff, false,
                Identity($"LLVM/LLVMSharp {LlvmToolchainContractV1.LlvmRelease}", LlvmToolchainContractV1.Default.OptionsDigest,
                    "none; optional ingress only"),
                NativeArtifacts, ["llvm-structural-import", "llvm-semantic-firewall"], ["llvm-owned-scheduling", "llvm-backend-emission"],
                "Disable the optional LLVM ingress and use the identical Native/Core path; version or semantic skew rejects before Canonical IR.",
                phase26EvidenceSha256),
            new(HybridCpuReleaseProfileV1.DotNetAotRestricted, HybridCpuReleaseDispositionV1.ReleaseAuthorized, false,
                Identity($"restricted-cil-v1|ILCompiler:{NativeAotSeamBaselineV1.SourceCommit}|SDK:{Phase26PublishSdk}",
                    Hash($"phase27|restricted|{HybridCpuSdkPackContractV1.PatchSetDigest}"),
                    $"{HybridCpuSdkPackContractV1.PackVersion}|{RefPlan7Phase15PackContractDigest}|HybridCpuStaticLinkerV1"),
                ["canonical-ir", "schedule", "bundles", "object", "binary", "evidence", "provenance"],
                ["restricted-static-value-method", "custom-rid-publish"],
                ["managed-object-state", "gc-publish", "eh-publish", "interop", "tls"],
                "Unsupported CIL, feature, pack, SDK or patch input fails before image emission; no host/JIT/LLVM fallback.",
                phase26EvidenceSha256),
            new(HybridCpuReleaseProfileV1.DotNetAotManaged, HybridCpuReleaseDispositionV1.ReleaseAuthorized, false,
                Identity($"managed-contract-v1.20|ILCompiler:{NativeAotSeamBaselineV1.SourceCommit}|SDK:{Phase26PublishSdk}",
                    Hash($"phase27|managed|{sdkPack.ContractDigest}|{string.Join(',', managedPublishCapabilities)}"),
                    $"{sdkPack.PackVersion}|{sdkPack.ContractDigest}|HybridCpuStaticLinkerV1"),
                ["canonical-ir", "schedule", "bundles", "object", "binary", "evidence", "provenance"],
                managedPublishCapabilities,
                managedUnsupportedCapabilities,
                "Derive requirements from the compiled and linked graph; reject every workstream outside the exact SDK publish-qualified set before retaining an image.",
                phase26EvidenceSha256),
            new(HybridCpuReleaseProfileV1.Full, HybridCpuReleaseDispositionV1.Unsupported, false,
                Identity("selected-components-only", Hash("phase27|full|unsupported"), "no-full-runtime-pack"),
                ["component-evidence"], [], ["full-managed-runtime", "dynamic-fsp", "runtime-vdsa"],
                "Select an independently qualified profile; never merge component evidence into an umbrella capability.",
                phase26EvidenceSha256)
        ];

        HybridCpuReleaseFeatureRowV1[] features =
        [
            Feature("canonical-core", HybridCpuReleaseFeatureSupportV1.ReleaseAuthorized,
                HybridCpuReleaseFeatureSupportV1.VerifiedDefaultOff, HybridCpuReleaseFeatureSupportV1.ReleaseAuthorized,
                HybridCpuReleaseFeatureSupportV1.ContractOnly, HybridCpuReleaseFeatureSupportV1.ContractOnly,
                "compiler-core", "Reject incompatible schema/model/target digests."),
            Feature("llvm-ingress", HybridCpuReleaseFeatureSupportV1.Unsupported,
                HybridCpuReleaseFeatureSupportV1.VerifiedDefaultOff, HybridCpuReleaseFeatureSupportV1.Unsupported,
                HybridCpuReleaseFeatureSupportV1.Unsupported, HybridCpuReleaseFeatureSupportV1.ContractOnly,
                "optional-llvm-adapter", "Disable LLVM and preserve Native/Core byte path."),
            Feature("region-loop-modulo-vt", HybridCpuReleaseFeatureSupportV1.VerifiedDefaultOff,
                HybridCpuReleaseFeatureSupportV1.VerifiedDefaultOff, HybridCpuReleaseFeatureSupportV1.VerifiedDefaultOff,
                HybridCpuReleaseFeatureSupportV1.ContractOnly, HybridCpuReleaseFeatureSupportV1.VerifiedDefaultOff,
                "compiler-core", "Kill switch returns exact basic-block/native fallback and invalidates transformed facts."),
            Feature("fsp-vdsa", HybridCpuReleaseFeatureSupportV1.VerifiedDefaultOff,
                HybridCpuReleaseFeatureSupportV1.VerifiedDefaultOff, HybridCpuReleaseFeatureSupportV1.VerifiedDefaultOff,
                HybridCpuReleaseFeatureSupportV1.Unsupported, HybridCpuReleaseFeatureSupportV1.VerifiedDefaultOff,
                "runtime-for-dynamic-admission", "Ignore advisory evidence; runtime independently admits or suppresses work."),
            Feature("gc-maps-safepoints", HybridCpuReleaseFeatureSupportV1.Unsupported,
                HybridCpuReleaseFeatureSupportV1.Unsupported, HybridCpuReleaseFeatureSupportV1.Unsupported,
                HybridCpuReleaseFeatureSupportV1.ReleaseAuthorized, HybridCpuReleaseFeatureSupportV1.ContractOnly,
                "runtime-gc", "Require the exact SDK workstream and reject missing final maps or safepoints."),
            ManagedPublishFeature("exact-aot-generics", "compiler-managed-runtime"),
            ManagedPublishFeature("static-type-initialization", "compiler-managed-runtime"),
            ManagedPublishFeature("szarray-core", "compiler-managed-runtime"),
            ManagedPublishFeature("type-layout-object-references", "compiler-managed-runtime"),
            ManagedPublishFeature("utf16-string-literals", "compiler-managed-runtime"),
            ManagedPublishFeature("virtual-interface-dispatch", "compiler-managed-runtime"),
            Feature("managed-eh-core", HybridCpuReleaseFeatureSupportV1.Unsupported,
                HybridCpuReleaseFeatureSupportV1.Unsupported, HybridCpuReleaseFeatureSupportV1.ContractOnly,
                HybridCpuReleaseFeatureSupportV1.VerifiedDefaultOff, HybridCpuReleaseFeatureSupportV1.ContractOnly,
                "compiler-managed-runtime", "Keep the bounded component default-off; no publish qualification."),
            Feature("fault-trap-integration", HybridCpuReleaseFeatureSupportV1.Unsupported,
                HybridCpuReleaseFeatureSupportV1.Unsupported, HybridCpuReleaseFeatureSupportV1.ContractOnly,
                HybridCpuReleaseFeatureSupportV1.VerifiedDefaultOff, HybridCpuReleaseFeatureSupportV1.ContractOnly,
                "kernel-managed-runtime", "Exact TrapAbi classification and zero-address read/write mapping remain component-only, default-off and non-publish."),
            Feature("interop-tls", HybridCpuReleaseFeatureSupportV1.Unsupported,
                HybridCpuReleaseFeatureSupportV1.Unsupported, HybridCpuReleaseFeatureSupportV1.Unsupported,
                HybridCpuReleaseFeatureSupportV1.VerifiedDefaultOff, HybridCpuReleaseFeatureSupportV1.Unsupported,
                "runtime-kernel-managed-runtime", "Threading/TLS and the bounded blittable interop component are default-off and not publish authority."),
            Feature("bounded-reflection", HybridCpuReleaseFeatureSupportV1.Unsupported,
                HybridCpuReleaseFeatureSupportV1.Unsupported, HybridCpuReleaseFeatureSupportV1.ContractOnly,
                HybridCpuReleaseFeatureSupportV1.VerifiedDefaultOff, HybridCpuReleaseFeatureSupportV1.Unsupported,
                "compiler-managed-runtime", "Explicitly retained public metadata and cached Type objects are default-off; dynamic code and managed publish remain unsupported."),
            Feature("async-runtime-libraries", HybridCpuReleaseFeatureSupportV1.Unsupported,
                HybridCpuReleaseFeatureSupportV1.Unsupported, HybridCpuReleaseFeatureSupportV1.ContractOnly,
                HybridCpuReleaseFeatureSupportV1.VerifiedDefaultOff, HybridCpuReleaseFeatureSupportV1.Unsupported,
                "runtime-kernel-managed-runtime", "Ordinary CIL state machines use bounded cooperative Task/continuation/timer libraries; preemption and managed publish remain unsupported.")
        ];

        HybridCpuCompatibilityWindowV1[] windows = CreateCompatibilityWindows();

        HybridCpuReleaseManifestV1 unsigned = new HybridCpuReleaseManifestV1(
            SchemaId, compilerCommit, phase26EvidenceSha256, profiles, features, windows, benchmark,
            AuthorityProhibitions,
            ["general-nativeaot", "full-managed-runtime", "multi-host-publish", "dynamic-fsp-authority", "runtime-vdsa-authority"],
            string.Empty);
        return unsigned with { DeterministicDigest = ComputeDigest(unsigned) };
    }

    public static HybridCpuReleaseValidationV1 Validate(HybridCpuReleaseManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Schema != SchemaId || !IsHex(manifest.CompilerCommit, 40) || !IsHex(manifest.Phase26EvidenceSha256, 64))
            return Invalid("HCRL1001", "Manifest schema or exact implementation/evidence identity is invalid.");
        if (HybridCpuSdkPackContractV1.CreateManifest().ContractDigest != RefPlan7Phase15PackContractDigest ||
            HybridCpuSdkPackContractV1.PatchSetDigest != Phase26PatchSetDigest)
            return Invalid("HCRL1012", "The exact accepted runtime-pack or the Phase 26 patch-set contract drifted from the release baseline.");
        if (!HasValidProfileRows(manifest.Profiles))
            return Invalid("HCRL1002", "Every profile requires one independent release row.");
        if (manifest.Profiles.Any(static row => row.Disposition == HybridCpuReleaseDispositionV1.Unsupported && row.DefaultEnabled))
            return Invalid("HCRL1003", "Unsupported profiles cannot be default-enabled.");
        if (manifest.Profiles.Any(row => row.Identity.CompilerCommit != manifest.CompilerCommit ||
                row.Identity.TargetRevision != HybridCpuTargetMachineContractV1.TargetArchitectureRevision ||
                row.Identity.MachineDigest != HybridCpuMachineDescriptionV1.Default.ContractDigest ||
                row.Identity.TopologyDigest != HybridCpuMachineTopologyV1.Default.ContractDigest ||
                row.Identity.TargetDigest != HybridCpuTargetPlatformContractV1.Default.ContractDigest ||
                row.Identity.NativeAbiDigest != HybridCpuNativeAbiContractV2.Default.ContractDigest ||
                row.Identity.ManagedAbiDigest != HybridCpuManagedAbiFamilyV1.Default.ContractDigest ||
                row.QualificationEvidenceSha256 != manifest.Phase26EvidenceSha256 ||
                row.Identity.OptionsDigest.Length != 64 || !row.Identity.OptionsDigest.All(Uri.IsHexDigit)))
            return Invalid("HCRL1009", "A profile row is not bound to the exact release target/model/ABI/evidence/options identity.");
        HybridCpuReleaseProfileRowV1 managed = manifest.Profiles.Single(static row => row.Profile == HybridCpuReleaseProfileV1.DotNetAotManaged);
        string[] expectedManagedCapabilities = HybridCpuSdkPackContractV1.CreateManifest().PublishQualifiedWorkstreams
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (managed.Disposition != HybridCpuReleaseDispositionV1.ReleaseAuthorized || managed.DefaultEnabled ||
            !managed.EnabledCapabilities.Order(StringComparer.Ordinal).SequenceEqual(expectedManagedCapabilities, StringComparer.Ordinal) ||
            manifest.Profiles.Single(static row => row.Profile == HybridCpuReleaseProfileV1.Full).Disposition !=
            HybridCpuReleaseDispositionV1.Unsupported)
            return Invalid("HCRL1004", "Managed release authority must equal the exact SDK publish-qualified subset; the full profile must remain unsupported.");
        if (!HasValidFeatureRows(manifest.Features))
            return Invalid("HCRL1014", "Feature rows require unique identities, explicit owner/fallback and defined contour support values.");
        if (!MatchesManagedPublicationSubset(manifest.Features, expectedManagedCapabilities))
            return Invalid("HCRL1013", "The managed feature matrix publication authority must equal the exact SDK publish-qualified subset.");
        if (!HasConsistentFeatureAuthority(manifest.Features, manifest.Profiles))
            return Invalid("HCRL1015", "A feature cannot be release-authorized outside its release-authorized profile contour.");
        HybridCpuReleaseProfileRowV1 native = manifest.Profiles.Single(static row => row.Profile == HybridCpuReleaseProfileV1.Native);
        if (!NativeArtifacts.All(native.Artifacts.Contains) || native.Identity.FrontendToolchain.Contains("LLVM", StringComparison.OrdinalIgnoreCase) ||
            native.Identity.FrontendToolchain.Contains("ILCompiler", StringComparison.OrdinalIgnoreCase))
            return Invalid("HCRL1005", "Native artifact or dependency independence is incomplete.");
        if (manifest.AuthorityProhibitions.Count != AuthorityProhibitions.Length ||
            AuthorityProhibitions.Except(manifest.AuthorityProhibitions, StringComparer.Ordinal).Any())
            return Invalid("HCRL1006", "Runtime authority prohibitions are incomplete.");
        if (manifest.Benchmark.DeterministicRuns < 2 || manifest.Benchmark.Cases <= 0 ||
            manifest.Benchmark.DeterministicInstructionBudget <= 0 ||
            manifest.Benchmark.OracleComparableCases < 0 || manifest.Benchmark.OracleUnknownCases < 0 ||
            manifest.Benchmark.OracleContractDigest != HybridCpuExactSchedulingOracleContractV1.Default.ContractDigest ||
            !IsHex(manifest.Benchmark.OracleEvidenceSha256, 64))
            return Invalid("HCRL1011", "Benchmark/oracle summary is not bound to deterministic work and exact evidence.");
        if (manifest.CompatibilityWindows.Any(static window => window.Major <= 0 || window.MinimumMinor < 0 ||
                window.MaximumMinor < window.MinimumMinor || window.OutsideWindowDisposition.Length == 0))
            return Invalid("HCRL1007", "Compatibility window is malformed.");
        if (JsonSerializer.Serialize(manifest.CompatibilityWindows.OrderBy(static row => row.Schema, StringComparer.Ordinal)) !=
            JsonSerializer.Serialize(CreateCompatibilityWindows().OrderBy(static row => row.Schema, StringComparer.Ordinal)))
            return Invalid("HCRL1010", "Compatibility windows differ from the exact current schema/ABI/object/pack contracts.");
        string expected = ComputeDigest(manifest with { DeterministicDigest = string.Empty });
        if (!string.Equals(expected, manifest.DeterministicDigest, StringComparison.Ordinal))
            return Invalid("HCRL1008", "Release manifest digest mismatch.");
        return new(true, "HCRL0000", "Release manifest is internally consistent.");
    }

    public static string Serialize(HybridCpuReleaseManifestV1 manifest) =>
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

    public static HybridCpuReleaseFeatureMatrixV1 CreateFeatureMatrix(HybridCpuReleaseManifestV1 manifest)
    {
        HybridCpuReleaseValidationV1 validation = Validate(manifest);
        if (!validation.IsValid) throw new ArgumentException(validation.Reason, nameof(manifest));
        return SnapshotFeatureMatrix(manifest.CompilerCommit, manifest.Features);
    }

    private static HybridCpuReleaseFeatureMatrixV1 SnapshotFeatureMatrix(string compilerCommit,
        IReadOnlyList<HybridCpuReleaseFeatureRowV1> features)
    {
        ReadOnlyCollection<HybridCpuReleaseFeatureRowV1> owned = Array.AsReadOnly(features.ToArray());
        string digest = Hash(string.Join('|', FeatureMatrixSchemaId, compilerCommit,
            string.Join('|', owned.OrderBy(static row => row.Feature, StringComparer.Ordinal)
                .Select(static row => JsonSerializer.Serialize(row)))));
        return new(FeatureMatrixSchemaId, compilerCommit, owned, digest);
    }

    private static bool HasValidProfileRows(IReadOnlyList<HybridCpuReleaseProfileRowV1> profiles)
    {
        HybridCpuReleaseProfileV1[] expected = Enum.GetValues<HybridCpuReleaseProfileV1>();
        return profiles is not null && profiles.Count == expected.Length &&
            profiles.All(static row => row is not null && Enum.IsDefined(row.Profile) && Enum.IsDefined(row.Disposition)) &&
            profiles.Select(static row => row.Profile).Distinct().Count() == expected.Length;
    }

    private static bool HasConsistentFeatureAuthority(IReadOnlyList<HybridCpuReleaseFeatureRowV1> features,
        IReadOnlyList<HybridCpuReleaseProfileRowV1> profiles)
    {
        bool Allowed(HybridCpuReleaseFeatureSupportV1 support, HybridCpuReleaseProfileV1 profile) =>
            support != HybridCpuReleaseFeatureSupportV1.ReleaseAuthorized || profiles.Any(row =>
                row.Profile == profile && row.Disposition == HybridCpuReleaseDispositionV1.ReleaseAuthorized);
        return features.All(row => Allowed(row.Native, HybridCpuReleaseProfileV1.Native) &&
            Allowed(row.Llvm, HybridCpuReleaseProfileV1.Llvm) &&
            Allowed(row.DotNetAotRestricted, HybridCpuReleaseProfileV1.DotNetAotRestricted) &&
            Allowed(row.DotNetAotManaged, HybridCpuReleaseProfileV1.DotNetAotManaged) &&
            Allowed(row.Full, HybridCpuReleaseProfileV1.Full));
    }

    private static bool HasValidFeatureRows(IReadOnlyList<HybridCpuReleaseFeatureRowV1> features)
    {
        if (features is null) return false;
        HashSet<string> identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (HybridCpuReleaseFeatureRowV1? row in features)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.Feature) || !identities.Add(row.Feature) ||
                string.IsNullOrWhiteSpace(row.AuthorityOwner) || string.IsNullOrWhiteSpace(row.Fallback) ||
                !Enum.IsDefined(row.Native) || !Enum.IsDefined(row.Llvm) || !Enum.IsDefined(row.DotNetAotRestricted) ||
                !Enum.IsDefined(row.DotNetAotManaged) || !Enum.IsDefined(row.Full)) return false;
        }
        return true;
    }

    private static bool MatchesManagedPublicationSubset(IReadOnlyList<HybridCpuReleaseFeatureRowV1> features,
        IReadOnlyList<string> expectedCapabilities) => features
        .Where(static row => row.DotNetAotManaged == HybridCpuReleaseFeatureSupportV1.ReleaseAuthorized)
        .Select(static row => row.Feature).Order(StringComparer.Ordinal)
        .SequenceEqual(expectedCapabilities.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static HybridCpuReleaseFeatureRowV1 ManagedPublishFeature(string feature, string owner) =>
        Feature(feature,
            HybridCpuReleaseFeatureSupportV1.Unsupported,
            HybridCpuReleaseFeatureSupportV1.Unsupported,
            HybridCpuReleaseFeatureSupportV1.Unsupported,
            HybridCpuReleaseFeatureSupportV1.ReleaseAuthorized,
            HybridCpuReleaseFeatureSupportV1.ContractOnly,
            owner,
            "Require compiler-derived use and exact runtime-pack qualification; reject missing lowering, object, registration, or runtime evidence.");

    private static HybridCpuReleaseFeatureRowV1 Feature(
        string feature,
        HybridCpuReleaseFeatureSupportV1 native,
        HybridCpuReleaseFeatureSupportV1 llvm,
        HybridCpuReleaseFeatureSupportV1 restricted,
        HybridCpuReleaseFeatureSupportV1 managed,
        HybridCpuReleaseFeatureSupportV1 full,
        string owner,
        string fallback) => new(feature, native, llvm, restricted, managed, full, owner, fallback);

    private static HybridCpuCompatibilityWindowV1 Window(CompilerSchemaDeclaration declaration) => new(
        declaration.SchemaId, declaration.Version.Major, declaration.Version.Minor, declaration.Version.Minor,
        declaration.MigrationIsLossless, "reject-or-explicit-versioned-migration");

    private static HybridCpuCompatibilityWindowV1[] CreateCompatibilityWindows() =>
    [
        Window(CompilerCrossLayerSchemaCatalogV1.Capability),
        Window(CompilerCrossLayerSchemaCatalogV1.Envelope),
        Window(CompilerCrossLayerSchemaCatalogV1.Provenance),
        new(HybridCpuNativeAbiContractV2.SchemaId, HybridCpuNativeAbiContractV2.SchemaMajor,
            HybridCpuNativeAbiContractV2.SchemaMinor, HybridCpuNativeAbiContractV2.SchemaMinor, false, "reject-before-lowering"),
        new(HybridCpuManagedAbiFamilyV1.SchemaId, HybridCpuManagedAbiFamilyV1.SchemaMajor,
            HybridCpuManagedAbiFamilyV1.SchemaMinor, HybridCpuManagedAbiFamilyV1.SchemaMinor, false, "reject-before-codegen"),
        new(HybridCpuObjectFormatContractV1.SchemaId, HybridCpuObjectFormatContractV1.SchemaMajor,
            HybridCpuObjectFormatContractV1.SchemaMinor, HybridCpuObjectFormatContractV1.SchemaMinor, false, "reject-before-link"),
        new(HybridCpuSdkPackContractV1.SchemaId, 1, 0, 0, false, "reject-before-codegen")
    ];

    private static string SchemaIdentity(CompilerSchemaDeclaration declaration) =>
        $"{declaration.SchemaId}@{declaration.Version.Major}.{declaration.Version.Minor}";

    private static HybridCpuReleaseValidationV1 Invalid(string code, string reason) => new(false, code, reason);

    private static string ComputeDigest(HybridCpuReleaseManifestV1 manifest)
    {
        StringBuilder text = new StringBuilder();
        text.Append(manifest.Schema).Append('|').Append(manifest.CompilerCommit).Append('|')
            .Append(manifest.Phase26EvidenceSha256);
        foreach (HybridCpuReleaseProfileRowV1 row in manifest.Profiles.OrderBy(static row => row.Profile))
            text.Append('|').Append(row.Profile).Append(':').Append(row.Disposition).Append(':').Append(row.DefaultEnabled)
                .Append(':').Append(JsonSerializer.Serialize(row.Identity)).Append(':')
                .Append(string.Join(',', row.Artifacts)).Append(':').Append(string.Join(',', row.EnabledCapabilities))
                .Append(':').Append(string.Join(',', row.UnsupportedCapabilities)).Append(':')
                .Append(row.DeterministicFallback).Append(':').Append(row.QualificationEvidenceSha256);
        foreach (HybridCpuReleaseFeatureRowV1 row in manifest.Features.OrderBy(static row => row.Feature, StringComparer.Ordinal))
            text.Append('|').Append(JsonSerializer.Serialize(row));
        foreach (HybridCpuCompatibilityWindowV1 window in manifest.CompatibilityWindows.OrderBy(static row => row.Schema, StringComparer.Ordinal))
            text.Append('|').Append(JsonSerializer.Serialize(window));
        text.Append('|').Append(manifest.Benchmark.Corpus).Append(':').Append(manifest.Benchmark.Cases).Append(':')
            .Append(manifest.Benchmark.DeterministicRuns).Append(':').Append(manifest.Benchmark.ScheduleFingerprint)
            .Append(':').Append(manifest.Benchmark.BundleFingerprint).Append(':').Append(manifest.Benchmark.ImageFingerprint)
            .Append(':').Append(manifest.Benchmark.BundleCount).Append(':').Append(manifest.Benchmark.DeterministicInstructionBudget)
            .Append(':').Append(manifest.Benchmark.OracleComparableCases).Append(':')
            .Append(manifest.Benchmark.OracleUnknownCases).Append(':').Append(manifest.Benchmark.OracleDisposition)
            .Append(':').Append(manifest.Benchmark.OracleContractDigest).Append(':').Append(manifest.Benchmark.OracleEvidenceSha256)
            .Append('|').Append(string.Join(',', manifest.AuthorityProhibitions))
            .Append('|').Append(string.Join(',', manifest.KnownUnsupported));
        return Hash(text.ToString());
    }

    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static bool IsHex(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);

    private static void RequiredHex(string value, int length, string parameter)
    {
        if (!IsHex(value, length)) throw new ArgumentException($"Expected a {length}-character hexadecimal identity.", parameter);
    }
}
