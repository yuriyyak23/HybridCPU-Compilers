using System.Reflection;
using HybridCPU.Compiler.Core.IR.Contracts;
using HybridCPU.Compiler.Native;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase05ContractTests
{
    [Fact]
    public void SchemaCatalog_DeclaresPurposeAuthorityProducerConsumerCompatibilityAndFields()
    {
        CompilerSchemaDeclaration[] declarations =
        [
            CompilerCrossLayerSchemaCatalogV1.Capability,
            CompilerCrossLayerSchemaCatalogV1.Envelope,
            CompilerCrossLayerSchemaCatalogV1.Provenance
        ];

        Assert.All(declarations, declaration =>
        {
            CompilerSchemaCompatibility.ValidateDeclaration(declaration);
            Assert.NotEmpty(declaration.Purpose);
            Assert.NotEmpty(declaration.Producer);
            Assert.NotEmpty(declaration.Consumer);
            Assert.NotEmpty(declaration.Fields);
            Assert.DoesNotContain("RuntimeAuthorityRequired", declaration.AuthorityClass.ToString(), StringComparison.Ordinal);
        });
        Assert.Contains(
            CompilerCrossLayerSchemaCatalogV1.Provenance.Fields,
            field => field.Name == "profitability_profile_hash" &&
                field.Semantics == CompilerSchemaFieldSemantics.ProfitabilityOnly);
    }

    [Fact]
    public void Compatibility_AllowsDeclaredNMinusOneAndNonSemanticForwardMinor_RejectsSemanticOrMajorSkew()
    {
        CompilerSchemaDeclaration consumer = Schema(1, 1, 0,
            new CompilerSchemaFieldDeclaration("semantic", CompilerSchemaFieldSemantics.Semantic, 0));
        Assert.Equal(
            CompilerCompatibilityDisposition.Compatible,
            CompilerSchemaCompatibility.Evaluate(Schema(1, 0, 0,
                new CompilerSchemaFieldDeclaration("semantic", CompilerSchemaFieldSemantics.Semantic, 0)), consumer).Disposition);
        Assert.Equal(
            CompilerCompatibilityDisposition.Compatible,
            CompilerSchemaCompatibility.Evaluate(Schema(1, 2, 0,
                new CompilerSchemaFieldDeclaration("semantic", CompilerSchemaFieldSemantics.Semantic, 0),
                new CompilerSchemaFieldDeclaration("note", CompilerSchemaFieldSemantics.NonSemantic, 2)), consumer).Disposition);
        Assert.Equal(
            CompilerCompatibilityDisposition.Unsupported,
            CompilerSchemaCompatibility.Evaluate(Schema(1, 2, 0,
                new CompilerSchemaFieldDeclaration("semantic", CompilerSchemaFieldSemantics.Semantic, 0),
                new CompilerSchemaFieldDeclaration("new_semantics", CompilerSchemaFieldSemantics.Semantic, 2)), consumer).Disposition);
        Assert.Equal(
            CompilerCompatibilityDisposition.Unsupported,
            CompilerSchemaCompatibility.Evaluate(Schema(2, 0, 0,
                new CompilerSchemaFieldDeclaration("semantic", CompilerSchemaFieldSemantics.Semantic, 0)), consumer).Disposition);
    }

    [Fact]
    public void CapabilityValidation_TreatsAbsenceUnknownUnsupportedAndExperimentalAsNoPermission()
    {
        CompilerFeatureSet available = CompilerCrossLayerSchemaCatalogV1.NativeV1;
        Assert.True(CompilerCapabilityValidator.ValidateRequired(
            ["frontend.native-api", "isa.hybridcpu-w8-native-v1"], available).IsSatisfied);

        foreach (string capability in new[] { "absent", "frontend.llvm", "schedule.modulo" })
        {
            CompilerCapabilityValidationResult result = CompilerCapabilityValidator.ValidateRequired([capability], available);
            Assert.False(result.IsSatisfied);
            Assert.Contains(capability, result.MissingCapabilities);
        }
    }

    [Fact]
    public void NativeFrontend_ValidatesRequiredCapabilityAndProducesDeterministicVersionedProvenance()
    {
        NativeFrontendRequest request = ExactRequest();
        NativeAssemblyFrontend frontend = new NativeAssemblyFrontend();

        NativeCompilationResult first = frontend.Compile(request);
        NativeCompilationResult second = frontend.Compile(request);

        Assert.True(first.Succeeded);
        Assert.NotNull(first.Artifacts);
        Assert.NotNull(second.Artifacts);
        Assert.Equal(first.Artifacts.BinaryImage, second.Artifacts.BinaryImage);
        Assert.Equal(first.Artifacts.Provenance, second.Artifacts.Provenance);
        Assert.Equal(first.Artifacts.Provenance.Build.ToCanonicalBytes(), second.Artifacts.Provenance.Build.ToCanonicalBytes());
        Assert.Equal(first.Artifacts.Provenance.Envelope.ToCanonicalBytes(), second.Artifacts.Provenance.Envelope.ToCanonicalBytes());
        Assert.Equal("0123456789abcdef", first.Artifacts.Provenance.Build.SourceCommit);
        Assert.Equal("fedcba9876543210", first.Artifacts.Provenance.Build.SourceTree);

        NativeCompilationResult rejected = frontend.Compile(request with
        {
            RequiredCapabilities = ["frontend.future-unknown"]
        });
        Assert.Equal(NativeCompilationStatus.Unsupported, rejected.Status);
        Assert.Equal("HCN0005", Assert.Single(rejected.Diagnostics).Code);
        Assert.Null(rejected.Artifacts);
    }

    [Fact]
    public void NativeFrontend_RejectsUnknownMajorSchemaBeforeAnyArtifactEmission()
    {
        CompilerSchemaDeclaration incompatible = CompilerCrossLayerSchemaCatalogV1.Envelope with
        {
            Version = new CompilerSchemaVersion(99, 0),
            Fields = CompilerCrossLayerSchemaCatalogV1.Envelope.Fields
                .Select(field => field with { IntroducedMinorVersion = 0 })
                .ToArray()
        };
        NativeCompilationResult result = new NativeAssemblyFrontend().Compile(ExactRequest() with
        {
            ConsumerEnvelopeSchema = incompatible
        });

        Assert.Equal(NativeCompilationStatus.Unsupported, result.Status);
        Assert.Equal("HCN0006", Assert.Single(result.Diagnostics).Code);
        Assert.Null(result.Artifacts);
    }

    [Fact]
    public void ProfileRemoval_ChangesProfitabilityProvenanceOnly_NotCarrierOrSemanticCacheKey()
    {
        NativeAssemblyFrontend frontend = new NativeAssemblyFrontend();
        NativeCompilationResult profiled = frontend.Compile(ExactRequest() with { ProfitabilityProfileHash = "profile-sha256" });
        NativeCompilationResult unprofiled = frontend.Compile(ExactRequest() with { ProfitabilityProfileHash = null });

        Assert.NotNull(profiled.Artifacts);
        Assert.NotNull(unprofiled.Artifacts);
        Assert.Equal(profiled.Artifacts.BinaryImage, unprofiled.Artifacts.BinaryImage);
        Assert.Equal(profiled.Artifacts.Provenance.CacheKey, unprofiled.Artifacts.Provenance.CacheKey);
        Assert.NotEqual(profiled.Artifacts.Provenance.Build.Digest, unprofiled.Artifacts.Provenance.Build.Digest);
        Assert.Equal("profile-sha256", profiled.Artifacts.Provenance.Build.ProfitabilityProfileHash);
        Assert.Null(unprofiled.Artifacts.Provenance.Build.ProfitabilityProfileHash);
    }

    [Theory]
    [InlineData("runtime_epoch")]
    [InlineData("freshness_token")]
    [InlineData("owner_valid")]
    [InlineData("replay_generation")]
    [InlineData("legality_decision")]
    [InlineData("execution_permission")]
    [InlineData("commit_permission")]
    [InlineData("retire_permission")]
    public void CompilerEnvelope_RejectsRuntimeAuthorityFields(string fieldName)
    {
        CompilerCrossLayerEnvelopeV1 envelope = Envelope() with
        {
            NonSemanticExtensions = new Dictionary<string, string> { [fieldName] = "forged" }
        };
        Assert.Throws<ArgumentException>(() => envelope.ToCanonicalBytes());
    }

    [Fact]
    public void CompilerEnvelopePublicFields_DoNotExposeRuntimeAuthorityState()
    {
        string[] forbidden = ["Epoch", "Freshness", "Owner", "Replay", "LegalityDecision", "ExecutionPermission", "CommitPermission", "RetirePermission"];
        PropertyInfo[] properties = typeof(CompilerCrossLayerEnvelopeV1).GetProperties(BindingFlags.Instance | BindingFlags.Public);
        Assert.All(properties, property => Assert.DoesNotContain(
            forbidden,
            token => property.Name.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void CacheKey_InvalidatesEverySemanticIdentity()
    {
        CompilerSemanticCacheKeyV1 baseline = CacheKey();
        CompilerSemanticCacheKeyV1[] mutations =
        [
            baseline with { TargetArchitectureRevision = "target-2" },
            baseline with { MachineDigest = "machine-2" },
            baseline with { TopologyDigest = "topology-2" },
            baseline with { AbiSchemaVersion = "abi-2" },
            baseline with { FeatureSetDigest = "features-2" },
            baseline with { DeterministicOptionsDigest = "options-2" },
            baseline with { DataLayoutVersion = "layout-2" },
            baseline with { InputDigest = "input-2" }
        ];
        Assert.All(mutations, mutation => Assert.NotEqual(baseline.Digest, mutation.Digest));
    }

    [Fact]
    public void Migration_CreatesNewProducerIdentityAndPreservesExplicitLineageOnly()
    {
        CompilerBuildProvenanceV1 original = BuildProvenance();
        CompilerBuildProvenanceV1 migrated = original.Migrate("hybridcpu.migrator", "2.0", new CompilerSchemaVersion(1, 1));

        Assert.NotEqual(original.Digest, migrated.Digest);
        Assert.Equal(original.Digest, migrated.MigratedFromProvenanceDigest);
        Assert.Equal("hybridcpu.migrator", migrated.ProducerId);
        Assert.NotEqual(original.ProducerId, migrated.ProducerId);
    }

    [Fact]
    public void ProfileDerivedSources_AreRejectedFromSemanticEvidenceLocations()
    {
        CompilerEnvelopeAuthorityGuard.RejectProfileInSemanticLocations(["target", "machine"]);
        Assert.Throws<ArgumentException>(() =>
            CompilerEnvelopeAuthorityGuard.RejectProfileInSemanticLocations(["target", "profile"]));
    }

    private static CompilerSchemaDeclaration Schema(
        int major,
        int minor,
        int minimumMinor,
        params CompilerSchemaFieldDeclaration[] fields) =>
        new("test-schema", new(major, minor), "test", HybridCPU.Compiler.Core.IR.Authority.CompilerAuthorityClass.CompilerEvidenceProduction,
            "producer", "consumer", minimumMinor, MigrationIsLossless: false, fields);

    private static NativeFrontendRequest ExactRequest() => new(
        0,
        AssemblySource: "ADDI r1, r0, 7\nADD r2, r1, r1",
        OptionsIdentity: "phase05-options")
    {
        BuildIdentity = new NativeBuildIdentity(
            "phase05-compiler",
            "0123456789abcdef",
            "fedcba9876543210",
            [new("dotnet-sdk", "10.0.100", "sdk-digest")],
            "hybridcpu-native-datalayout/v1",
            "hybridcpu-native-abi/v1"),
        RequiredCapabilities = ["frontend.native-asm", "isa.hybridcpu-w8-native-v1"]
    };

    private static CompilerBuildProvenanceV1 BuildProvenance() => new(
        CompilerCrossLayerSchemaCatalogV1.Provenance.SchemaId,
        new(1, 0), "compiler", "1", "commit", "tree", [new("sdk", "1", "digest")],
        "frontend", "1", "options", "layout", "abi", null);

    private static CompilerCrossLayerEnvelopeV1 Envelope() => new(
        CompilerCrossLayerSchemaCatalogV1.Envelope.SchemaId,
        new(1, 0), "compiler", "target", "machine", "topology", "abi", "features", [], "provenance");

    private static CompilerSemanticCacheKeyV1 CacheKey() =>
        new("target", "machine", "topology", "abi", "features", "options", "layout", "input");
}
