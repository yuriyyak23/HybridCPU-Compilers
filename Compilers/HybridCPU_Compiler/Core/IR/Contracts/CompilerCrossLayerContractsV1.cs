using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HybridCPU.Compiler.Core.IR.Authority;

namespace HybridCPU.Compiler.Core.IR.Contracts;

public enum CompilerCompatibilityDisposition : byte
{
    Compatible = 0,
    NeedsMigration = 1,
    Unsupported = 2,
    Unknown = 3
}

public enum CompilerFeatureSupport : byte
{
    Supported = 0,
    Unsupported = 1,
    ExperimentalDefaultOff = 2,
    RequiredByInput = 3,
    Unknown = 4
}

public enum CompilerManagedSafetyLevel : byte
{
    NotApplicable = 0,
    UnmanagedOnly = 1,
    RestrictedManaged = 2,
    ManagedRuntimeRequired = 3,
    Unknown = 4
}

public enum CompilerSchemaFieldSemantics : byte
{
    Semantic = 0,
    NonSemantic = 1,
    ProfitabilityOnly = 2
}

public sealed record CompilerSchemaVersion(int Major, int Minor)
{
    public override string ToString() => $"{Major.ToString(CultureInfo.InvariantCulture)}.{Minor.ToString(CultureInfo.InvariantCulture)}";
}

public sealed record CompilerSchemaFieldDeclaration(
    string Name,
    CompilerSchemaFieldSemantics Semantics,
    int IntroducedMinorVersion);

/// <summary>
/// Machine-readable declaration for a compiler-owned schema. AuthorityClass classifies
/// build-time products only and can never represent runtime permission.
/// </summary>
public sealed record CompilerSchemaDeclaration(
    string SchemaId,
    CompilerSchemaVersion Version,
    string Purpose,
    CompilerAuthorityClass AuthorityClass,
    string Producer,
    string Consumer,
    int MinimumCompatibleMinorVersion,
    bool MigrationIsLossless,
    IReadOnlyList<CompilerSchemaFieldDeclaration> Fields);

public sealed record CompilerCompatibilityResult(
    CompilerCompatibilityDisposition Disposition,
    string Code,
    string Reason);

public static class CompilerSchemaCompatibility
{
    public static CompilerCompatibilityResult Evaluate(
        CompilerSchemaDeclaration producer,
        CompilerSchemaDeclaration consumer)
    {
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(consumer);
        ValidateDeclaration(producer);
        ValidateDeclaration(consumer);

        if (!string.Equals(producer.SchemaId, consumer.SchemaId, StringComparison.Ordinal))
            return Unknown("HCC0001", "Schema identities differ and no migration is declared.");
        if (producer.Version.Major != consumer.Version.Major)
            return Unsupported("HCC0002", "A legality-relevant schema major mismatch fails closed.");
        if (producer.Version.Minor < consumer.MinimumCompatibleMinorVersion)
            return producer.MigrationIsLossless
                ? Migration("HCC0003", "Producer minor version requires an explicit lossless migration.")
                : Unsupported("HCC0004", "Producer minor version is outside the consumer compatibility range.");
        if (producer.Version.Minor <= consumer.Version.Minor)
            return Compatible();

        string[] forwardFields = producer.Fields
            .Where(field => field.IntroducedMinorVersion > consumer.Version.Minor)
            .Where(field => field.Semantics != CompilerSchemaFieldSemantics.NonSemantic)
            .Select(static field => field.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        return forwardFields.Length == 0
            ? Compatible()
            : Unsupported("HCC0005", $"Unknown forward semantic fields fail closed: {string.Join(",", forwardFields)}.");
    }

    public static void ValidateDeclaration(CompilerSchemaDeclaration declaration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declaration.SchemaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(declaration.Purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(declaration.Producer);
        ArgumentException.ThrowIfNullOrWhiteSpace(declaration.Consumer);
        ArgumentNullException.ThrowIfNull(declaration.Version);
        ArgumentNullException.ThrowIfNull(declaration.Fields);
        if (declaration.Version.Major <= 0 || declaration.Version.Minor < 0)
            throw new ArgumentOutOfRangeException(nameof(declaration), "Schema versions must be positive major/non-negative minor values.");
        if (declaration.MinimumCompatibleMinorVersion < 0 ||
            declaration.MinimumCompatibleMinorVersion > declaration.Version.Minor)
            throw new ArgumentOutOfRangeException(nameof(declaration), "Compatibility range is invalid.");
        if (declaration.AuthorityClass is CompilerAuthorityClass.RuntimeAuthorityRequired)
            throw new ArgumentException("A compiler-produced schema cannot carry runtime authority.", nameof(declaration));

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (CompilerSchemaFieldDeclaration field in declaration.Fields)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field.Name);
            if (!names.Add(field.Name))
                throw new ArgumentException($"Duplicate schema field '{field.Name}'.", nameof(declaration));
            if (field.IntroducedMinorVersion < 0 || field.IntroducedMinorVersion > declaration.Version.Minor)
                throw new ArgumentOutOfRangeException(nameof(declaration), $"Field '{field.Name}' has an invalid introduction version.");
        }
    }

    private static CompilerCompatibilityResult Compatible() =>
        new(CompilerCompatibilityDisposition.Compatible, "HCC0000", "Schema versions are compatible without authority elevation.");
    private static CompilerCompatibilityResult Migration(string code, string reason) =>
        new(CompilerCompatibilityDisposition.NeedsMigration, code, reason);
    private static CompilerCompatibilityResult Unsupported(string code, string reason) =>
        new(CompilerCompatibilityDisposition.Unsupported, code, reason);
    private static CompilerCompatibilityResult Unknown(string code, string reason) =>
        new(CompilerCompatibilityDisposition.Unknown, code, reason);
}

public sealed record CompilerFeatureCapability(string Id, CompilerFeatureSupport Support);

public sealed record CompilerFeatureSet(
    string SchemaId,
    CompilerSchemaVersion Version,
    IReadOnlyList<CompilerFeatureCapability> FrontendFeatures,
    IReadOnlyList<CompilerFeatureCapability> IsaContours,
    IReadOnlyList<CompilerFeatureCapability> SchedulingTransforms,
    IReadOnlyList<string> EvidenceSchemaVersions,
    CompilerManagedSafetyLevel ManagedSafetyLevel)
{
    public byte[] ToCanonicalBytes() => CompilerContractCanonicalJson.Serialize(this with
    {
        FrontendFeatures = Sort(FrontendFeatures),
        IsaContours = Sort(IsaContours),
        SchedulingTransforms = Sort(SchedulingTransforms),
        EvidenceSchemaVersions = EvidenceSchemaVersions.OrderBy(static item => item, StringComparer.Ordinal).ToArray()
    });
    [JsonIgnore]
    public string Digest => CompilerContractCanonicalJson.Hash(ToCanonicalBytes());

    private static IReadOnlyList<CompilerFeatureCapability> Sort(IEnumerable<CompilerFeatureCapability> values) =>
        values.OrderBy(static item => item.Id, StringComparer.Ordinal).ThenBy(static item => item.Support).ToArray();
}

public sealed record CompilerCapabilityValidationResult(
    bool IsSatisfied,
    IReadOnlyList<string> MissingCapabilities,
    string Reason);

public static class CompilerCapabilityValidator
{
    public static CompilerCapabilityValidationResult ValidateRequired(
        IEnumerable<string> requiredCapabilities,
        CompilerFeatureSet available)
    {
        ArgumentNullException.ThrowIfNull(requiredCapabilities);
        ArgumentNullException.ThrowIfNull(available);
        Dictionary<string, CompilerFeatureSupport> known = available.FrontendFeatures
            .Concat(available.IsaContours)
            .Concat(available.SchedulingTransforms)
            .GroupBy(static item => item.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Last().Support, StringComparer.Ordinal);
        string[] missing = requiredCapabilities
            .Distinct(StringComparer.Ordinal)
            .Where(id => !known.TryGetValue(id, out CompilerFeatureSupport state) ||
                state is not (CompilerFeatureSupport.Supported or CompilerFeatureSupport.RequiredByInput))
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        return new(
            missing.Length == 0,
            missing,
            missing.Length == 0
                ? "All input-required capabilities are explicitly supported."
                : $"Required capabilities fail closed: {string.Join(",", missing)}.");
    }
}

public sealed record CompilerToolchainIdentity(string Name, string Version, string Digest);

public sealed record CompilerBuildProvenanceV1(
    string SchemaId,
    CompilerSchemaVersion SchemaVersion,
    string ProducerId,
    string ProducerVersion,
    string SourceCommit,
    string SourceTree,
    IReadOnlyList<CompilerToolchainIdentity> Toolchains,
    string FrontendId,
    string FrontendVersion,
    string OptionsDigest,
    string DataLayoutVersion,
    string AbiVersion,
    string? ProfitabilityProfileHash,
    string? MigratedFromProvenanceDigest = null)
{
    public byte[] ToCanonicalBytes()
    {
        Validate();
        return CompilerContractCanonicalJson.Serialize(this with
        {
            Toolchains = Toolchains
                .OrderBy(static item => item.Name, StringComparer.Ordinal)
                .ThenBy(static item => item.Version, StringComparer.Ordinal)
                .ThenBy(static item => item.Digest, StringComparer.Ordinal)
                .ToArray()
        });
    }
    [JsonIgnore]
    public string Digest => CompilerContractCanonicalJson.Hash(ToCanonicalBytes());

    public CompilerBuildProvenanceV1 Migrate(
        string migratorProducerId,
        string migratorProducerVersion,
        CompilerSchemaVersion targetVersion) =>
        this with
        {
            SchemaVersion = targetVersion,
            ProducerId = migratorProducerId,
            ProducerVersion = migratorProducerVersion,
            MigratedFromProvenanceDigest = Digest
        };

    private void Validate()
    {
        ArgumentNullException.ThrowIfNull(SchemaVersion);
        ArgumentNullException.ThrowIfNull(Toolchains);
        foreach ((string Value, string Name) item in new[]
        {
            (SchemaId, nameof(SchemaId)),
            (ProducerId, nameof(ProducerId)),
            (ProducerVersion, nameof(ProducerVersion)),
            (SourceCommit, nameof(SourceCommit)),
            (SourceTree, nameof(SourceTree)),
            (FrontendId, nameof(FrontendId)),
            (FrontendVersion, nameof(FrontendVersion)),
            (OptionsDigest, nameof(OptionsDigest)),
            (DataLayoutVersion, nameof(DataLayoutVersion)),
            (AbiVersion, nameof(AbiVersion))
        })
        {
            if (string.IsNullOrWhiteSpace(item.Value))
                throw new ArgumentException("Build provenance identity must be explicit; use 'unknown' for a declared absence.", item.Name);
        }
        foreach (CompilerToolchainIdentity toolchain in Toolchains)
        {
            if (string.IsNullOrWhiteSpace(toolchain.Name) ||
                string.IsNullOrWhiteSpace(toolchain.Version) ||
                string.IsNullOrWhiteSpace(toolchain.Digest))
                throw new ArgumentException("Toolchain identity is incomplete.", nameof(Toolchains));
        }
    }
}

public sealed record CompilerCrossLayerEnvelopeV1(
    string SchemaId,
    CompilerSchemaVersion SchemaVersion,
    string ProducerVersion,
    string TargetArchitectureRevision,
    string MachineDigest,
    string TopologyDigest,
    string AbiVersion,
    string FeatureSetDigest,
    IReadOnlyList<string> RequiredConsumerCapabilities,
    string ProvenanceDigest,
    IReadOnlyDictionary<string, string>? NonSemanticExtensions = null)
{
    public byte[] ToCanonicalBytes()
    {
        CompilerEnvelopeAuthorityGuard.Validate(this);
        return CompilerContractCanonicalJson.Serialize(this with
        {
            RequiredConsumerCapabilities = RequiredConsumerCapabilities.OrderBy(static item => item, StringComparer.Ordinal).ToArray(),
            NonSemanticExtensions = NonSemanticExtensions is null
                ? null
                : NonSemanticExtensions
                    .OrderBy(static item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal)
        });
    }

    [JsonIgnore]
    public string Digest => CompilerContractCanonicalJson.Hash(ToCanonicalBytes());
}

public sealed record CompilerSemanticCacheKeyV1(
    string TargetArchitectureRevision,
    string MachineDigest,
    string TopologyDigest,
    string AbiSchemaVersion,
    string FeatureSetDigest,
    string DeterministicOptionsDigest,
    string DataLayoutVersion,
    string InputDigest)
{
    [JsonIgnore]
    public string Digest => CompilerContractCanonicalJson.Hash(CompilerContractCanonicalJson.Serialize(this));
}

public sealed record CompilerProfitabilityMetadataV1(
    string SchemaId,
    string? ProfileHash,
    IReadOnlyDictionary<string, string> Observations)
{
    [JsonIgnore]
    public CompilerSchemaFieldSemantics Semantics => CompilerSchemaFieldSemantics.ProfitabilityOnly;
}

public static class CompilerEnvelopeAuthorityGuard
{
    private static readonly string[] ForbiddenAuthorityTokens =
    [
        "epoch", "freshness", "owner", "replay", "legalitydecision", "executionpermission",
        "publicationpermission", "commitpermission", "retirepermission", "generation"
    ];

    public static void Validate(CompilerCrossLayerEnvelopeV1 envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        Required(envelope.SchemaId, nameof(envelope.SchemaId));
        Required(envelope.ProducerVersion, nameof(envelope.ProducerVersion));
        Required(envelope.TargetArchitectureRevision, nameof(envelope.TargetArchitectureRevision));
        Required(envelope.MachineDigest, nameof(envelope.MachineDigest));
        Required(envelope.TopologyDigest, nameof(envelope.TopologyDigest));
        Required(envelope.AbiVersion, nameof(envelope.AbiVersion));
        Required(envelope.FeatureSetDigest, nameof(envelope.FeatureSetDigest));
        Required(envelope.ProvenanceDigest, nameof(envelope.ProvenanceDigest));
        if (envelope.NonSemanticExtensions is null) return;
        foreach (string name in envelope.NonSemanticExtensions.Keys)
        {
            string normalized = new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            if (ForbiddenAuthorityTokens.Any(normalized.Contains))
                throw new ArgumentException($"Compiler envelope field '{name}' is runtime-authority-bearing and is rejected.", nameof(envelope));
        }
    }

    public static void RejectProfileInSemanticLocations(IEnumerable<string> semanticFieldSources)
    {
        ArgumentNullException.ThrowIfNull(semanticFieldSources);
        if (semanticFieldSources.Any(static source => string.Equals(source, "profile", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Profile-derived input is ProfitabilityOnly and cannot populate semantic legality/evidence fields.", nameof(semanticFieldSources));
    }

    private static void Required(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Required semantic identity is absent.", name);
    }
}

public static class CompilerContractCanonicalJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public static byte[] Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.SerializeToUtf8Bytes(value, Options);
    }

    public static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public static class CompilerCrossLayerSchemaCatalogV1
{
    public static CompilerSchemaDeclaration Capability { get; } = new(
        "hybridcpu.compiler-capabilities",
        new CompilerSchemaVersion(1, 0),
        "Declare frontend, ISA, transform, evidence and managed-safety support without granting permission.",
        CompilerAuthorityClass.CompilerEvidenceProduction,
        "HybridCPU.Compiler.Core",
        "Compiler frontends and lowering boundaries",
        0,
        MigrationIsLossless: false,
        [
            new("frontend_features", CompilerSchemaFieldSemantics.Semantic, 0),
            new("isa_contours", CompilerSchemaFieldSemantics.Semantic, 0),
            new("scheduling_transforms", CompilerSchemaFieldSemantics.Semantic, 0),
            new("evidence_schema_versions", CompilerSchemaFieldSemantics.Semantic, 0),
            new("managed_safety_level", CompilerSchemaFieldSemantics.Semantic, 0)
        ]);

    public static CompilerSchemaDeclaration Envelope { get; } = new(
        "hybridcpu.compiler-cross-layer-envelope",
        new CompilerSchemaVersion(1, 0),
        "Bind immutable build-time evidence to target, machine, topology, ABI, features and provenance.",
        CompilerAuthorityClass.CompilerEvidenceProduction,
        "HybridCPU.Compiler.Core",
        "Frontend, cache, lowering and runtime ingress adapters",
        0,
        MigrationIsLossless: false,
        [
            new("target_architecture_revision", CompilerSchemaFieldSemantics.Semantic, 0),
            new("machine_digest", CompilerSchemaFieldSemantics.Semantic, 0),
            new("topology_digest", CompilerSchemaFieldSemantics.Semantic, 0),
            new("abi_version", CompilerSchemaFieldSemantics.Semantic, 0),
            new("feature_set_digest", CompilerSchemaFieldSemantics.Semantic, 0),
            new("required_consumer_capabilities", CompilerSchemaFieldSemantics.Semantic, 0),
            new("provenance_digest", CompilerSchemaFieldSemantics.Semantic, 0),
            new("non_semantic_extensions", CompilerSchemaFieldSemantics.NonSemantic, 0)
        ]);

    public static CompilerSchemaDeclaration Provenance { get; } = new(
        "hybridcpu.compiler-build-provenance",
        new CompilerSchemaVersion(1, 0),
        "Record deterministic build identity and migration lineage.",
        CompilerAuthorityClass.CompilerEvidenceProduction,
        "HybridCPU compiler frontend or explicit migration tool",
        "Evidence consumers and cache",
        0,
        MigrationIsLossless: false,
        [
            new("source_commit", CompilerSchemaFieldSemantics.Semantic, 0),
            new("source_tree", CompilerSchemaFieldSemantics.Semantic, 0),
            new("toolchains", CompilerSchemaFieldSemantics.Semantic, 0),
            new("frontend", CompilerSchemaFieldSemantics.Semantic, 0),
            new("options_digest", CompilerSchemaFieldSemantics.Semantic, 0),
            new("data_layout_version", CompilerSchemaFieldSemantics.Semantic, 0),
            new("abi_version", CompilerSchemaFieldSemantics.Semantic, 0),
            new("profitability_profile_hash", CompilerSchemaFieldSemantics.ProfitabilityOnly, 0),
            new("migrated_from_provenance_digest", CompilerSchemaFieldSemantics.Semantic, 0)
        ]);

    public static CompilerFeatureSet NativeV1 { get; } = new(
        Capability.SchemaId,
        Capability.Version,
        [
            new("frontend.native-api", CompilerFeatureSupport.Supported),
            new("frontend.native-asm", CompilerFeatureSupport.Supported),
            new("frontend.llvm", CompilerFeatureSupport.Unsupported),
            new("frontend.dotnet", CompilerFeatureSupport.Unsupported)
        ],
        [new("isa.hybridcpu-w8-native-v1", CompilerFeatureSupport.Supported)],
        [
            new("schedule.canonical-bounded", CompilerFeatureSupport.Supported),
            new("schedule.modulo", CompilerFeatureSupport.ExperimentalDefaultOff)
        ],
        ["hybridcpu.native-scheduling-report/v1", Envelope.SchemaId + "/1.0"],
        CompilerManagedSafetyLevel.UnmanagedOnly);
}
