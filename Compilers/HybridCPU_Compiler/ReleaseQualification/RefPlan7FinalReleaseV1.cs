using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.Compiler.NativeAot;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Release;

public enum HybridCpuRuntimeDeterminismDispositionV1 : byte
{
    Deterministic = 0,
    DeterministicWhenVirtualized = 1,
    DeterministicWhenReplayed = 2
}

public sealed record HybridCpuRefPlan7ComponentRevisionsV1(
    string CompilerSha,
    string ManagedRuntimeSha,
    string RuntimeKernelSha,
    string IseSha);

public sealed record HybridCpuRefPlan7AbiDigestsV1(
    string NativeAbiDigest,
    string ManagedAbiDigest,
    string ImageAbiDigest,
    string KernelAbiDigest,
    string TrapAbiDigest);

public sealed record HybridCpuRefPlan7EvidenceArtifactV1(
    string Phase,
    string Path,
    string Sha256,
    string Disposition);

public sealed record HybridCpuRefPlan7ImageQualificationV1(
    string SourceDigest,
    string ToolchainDigest,
    string OptionsDigest,
    bool CleanBuildA,
    string BuildASha256,
    bool CleanBuildB,
    string BuildBSha256);

public sealed record HybridCpuRefPlan7RuntimeDeterminismClaimV1(
    string Category,
    HybridCpuRuntimeDeterminismDispositionV1 Disposition,
    string EvidencePhase,
    string Boundary);

public sealed record HybridCpuRefPlan7AuthorityBoundaryV1(
    string Source,
    string ForbiddenDependency,
    bool Passed,
    string NeutralBoundary);

public sealed record HybridCpuRefPlan7FinalMatrixRowV1(
    string Capability,
    string EvidencePhases,
    bool Qualified,
    string FinalRepresentation);

public sealed record HybridCpuRefPlan7FinalReleaseManifestV1(
    string Schema,
    HybridCpuRefPlan7ComponentRevisionsV1 Components,
    HybridCpuRefPlan7AbiDigestsV1 AbiDigests,
    IReadOnlyList<string> EnabledFeatureBits,
    IReadOnlyList<string> QualifiedDefaultOffFeatureBits,
    IReadOnlyList<HybridCpuRefPlan7EvidenceArtifactV1> EvidenceArtifacts,
    HybridCpuRefPlan7ImageQualificationV1 ImageQualification,
    IReadOnlyList<HybridCpuRefPlan7RuntimeDeterminismClaimV1> RuntimeDeterminism,
    IReadOnlyList<string> NondeterministicInputs,
    IReadOnlyList<HybridCpuRefPlan7AuthorityBoundaryV1> AuthorityBoundaries,
    IReadOnlyList<HybridCpuRefPlan7FinalMatrixRowV1> FinalMatrix,
    IReadOnlyList<string> FailureDomains,
    string RuntimePackDigest,
    string DeterministicDigest);

public sealed record HybridCpuRefPlan7FinalReleaseValidationV1(bool IsValid, string Code, string Reason);

public static class HybridCpuRefPlan7FinalReleaseV1
{
    public const string SchemaId = "hybridcpu.refplan7-final-release/v1";
    public const string EvidenceClosed = "EvidenceClosed";

    private static readonly HybridCpuRefPlan7RuntimeDeterminismClaimV1[] RuntimeRules =
    [
        new("deterministic-compilation", HybridCpuRuntimeDeterminismDispositionV1.Deterministic, "00-16", "identical source/toolchain/options/contracts"),
        new("deterministic-image-bytes", HybridCpuRuntimeDeterminismDispositionV1.Deterministic, "08,16", "clean A/B restricted images"),
        new("deterministic-single-context-runtime", HybridCpuRuntimeDeterminismDispositionV1.DeterministicWhenVirtualized, "01,10", "virtualized time and inputs"),
        new("deterministic-cooperative-scheduler", HybridCpuRuntimeDeterminismDispositionV1.DeterministicWhenVirtualized, "11,12,15", "cooperative scheduler and virtual deadlines"),
        new("external-input-scheduling-event-replay", HybridCpuRuntimeDeterminismDispositionV1.DeterministicWhenReplayed, "00,10", "logged external inputs, scheduling and events")
    ];

    private static readonly string[] ExternalNondeterminism =
    [
        "wall-clock", "host-filesystem", "host-network", "host-randomness", "host-scheduling"
    ];

    private static readonly (string Source, string Forbidden, string Boundary)[] BoundaryRules =
    [
        ("Compiler", "ISE execution/scheduler implementation", "HybridCPU.Platform.Contracts and architecture contracts"),
        ("ISE", "ManagedRuntime/compiler metadata", "architecture/ISA contracts only"),
        ("RuntimeKernel", "compiler IR/type-system implementation", "HybridCPU.Platform.Contracts"),
        ("ManagedRuntime", "ISE pipeline/retire/MMU implementation", "HybridCPU.Platform.Contracts and RuntimeKernel")
    ];

    private static readonly (string Capability, string Phases, string Final)[] RequiredMatrix =
    [
        ("Compiler/CIL/CFG/SSA/phi", "00,08", "canonical CFG/SSA and linked image"),
        ("ABI/frame/stack-walk", "01,09", "post-RA frame and unwind records"),
        ("object/type/static-init", "02,04", "linked object metadata and bootstrap registrations"),
        ("stack-maps/GC", "05,11", "final-PC stack maps and rendezvous roots"),
        ("dispatch/delegates/generics", "06,07,08", "linked tables and exact method bodies"),
        ("managed-EH", "09", "final-PC EH clauses and unwind data"),
        ("trap/MMU", "10", "committed TrapAbi state"),
        ("threads/TLS", "11", "kernel context and managed thread/TLS records"),
        ("memory-order/synchronization", "12", "ISA ordering plus runtime monitor/wait records"),
        ("interop", "13", "final thunk ABI and host-transition records"),
        ("async/reflection", "14,15", "retained metadata and ordinary CIL/EH plans"),
        ("ISE execution", "00,10", "architectural execute/commit/retire observations"),
        ("positive/negative/property/fuzz", "00-15", "checked-in deterministic test corpus"),
        ("cross-layer end-to-end", "00,05,08,09,10,15", "linked image/runtime/ISE evidence")
    ];

    private static readonly string[] Domains =
    [
        "managed-exception-or-oom", "runtime-fail-fast", "kernel-or-service-error",
        "architectural-illegal-privilege-protection-page-alignment-trap", "integrity-failure"
    ];

    public static HybridCpuRefPlan7FinalReleaseManifestV1 Create(
        string monorepoCommit,
        IReadOnlyList<HybridCpuRefPlan7EvidenceArtifactV1> evidenceArtifacts,
        HybridCpuRefPlan7ImageQualificationV1 imageQualification)
    {
        RequireHex(monorepoCommit, 40, nameof(monorepoCommit));
        ArgumentNullException.ThrowIfNull(evidenceArtifacts);
        ArgumentNullException.ThrowIfNull(imageQualification);
        if (!TryVerifyEvidenceArtifacts(evidenceArtifacts, monorepoCommit, out string evidenceFailure))
            throw new InvalidDataException("HCR7L1011: " + evidenceFailure);
        HybridCpuRefPlan7ComponentRevisionsV1 components = new HybridCpuRefPlan7ComponentRevisionsV1(
            monorepoCommit, monorepoCommit, monorepoCommit, monorepoCommit);
        HybridCpuRefPlan7AbiDigestsV1 abi = CurrentAbiDigests();
        string[] qualified = HybridCpuManagedFeatureSetV1.Default.Workstreams
            .Where(static row => row.Support == HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff)
            .Select(static row => row.Identity).Order(StringComparer.Ordinal).ToArray();
        HybridCpuRefPlan7RuntimeDeterminismClaimV1[] runtime = RuntimeRules.ToArray();
        HybridCpuRefPlan7AuthorityBoundaryV1[] boundaries = BoundaryRules
            .Select(static row => new HybridCpuRefPlan7AuthorityBoundaryV1(row.Source, row.Forbidden, true, row.Boundary)).ToArray();
        HybridCpuRefPlan7FinalMatrixRowV1[] matrix = RequiredMatrix
            .Select(static row => new HybridCpuRefPlan7FinalMatrixRowV1(row.Capability, row.Phases, true, row.Final)).ToArray();
        HybridCpuRefPlan7FinalReleaseManifestV1 unsigned = new HybridCpuRefPlan7FinalReleaseManifestV1(
            SchemaId, components, abi, [], qualified,
            evidenceArtifacts.OrderBy(static row => row.Phase, StringComparer.Ordinal).ToArray(), imageQualification,
            runtime, ExternalNondeterminism, boundaries, matrix, Domains,
            HybridCpuSdkPackContractV1.CreateManifest().ContractDigest, string.Empty);
        return unsigned with { DeterministicDigest = ComputeDigest(unsigned) };
    }

    public static HybridCpuRefPlan7FinalReleaseValidationV1 Validate(HybridCpuRefPlan7FinalReleaseManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Schema != SchemaId || !ComponentRevisionsValid(manifest.Components))
            return Invalid("HCR7L1001", "Schema or exact component revisions are invalid.");
        if (!TryVerifyEvidenceArtifacts(manifest.EvidenceArtifacts, manifest.Components.CompilerSha,
                out string evidenceFailure))
            return Invalid("HCR7L1011", evidenceFailure);
        HybridCpuRefPlan7AbiDigestsV1 expectedAbi = CurrentAbiDigests();
        if (manifest.AbiDigests != expectedAbi || manifest.RuntimePackDigest != HybridCpuSdkPackContractV1.CreateManifest().ContractDigest)
            return Invalid("HCR7L1002", "Native, managed, image, kernel, trap or runtime-pack ABI identity drifted.");
        string[] phases = Enumerable.Range(0, 16).Select(static phase => phase.ToString("00")).ToArray();
        if (manifest.EvidenceArtifacts.Count != phases.Length ||
            !manifest.EvidenceArtifacts.Select(static row => row.Phase).SequenceEqual(phases) ||
            manifest.EvidenceArtifacts.Any(static row => row.Disposition != EvidenceClosed ||
                string.IsNullOrWhiteSpace(row.Path) || !IsHex(row.Sha256, 64)))
            return Invalid("HCR7L1003", "Every Phase 00-15 evidence artifact must be exact and EvidenceClosed.");
        string[] qualified = HybridCpuManagedFeatureSetV1.Default.Workstreams
            .Where(static row => row.Support == HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff)
            .Select(static row => row.Identity).Order(StringComparer.Ordinal).ToArray();
        if (manifest.EnabledFeatureBits.Count != 0 || !manifest.QualifiedDefaultOffFeatureBits.SequenceEqual(qualified))
            return Invalid("HCR7L1004", "Managed features are component-qualified but not default-enabled.");
        if (!HasValidImageQualification(manifest.ImageQualification))
            return Invalid("HCR7L1005", "Clean build A/B image bytes are not identically qualified.");
        if (!HasValidRuntimeDeterminismRows(manifest.RuntimeDeterminism) ||
            manifest.NondeterministicInputs is null || !manifest.NondeterministicInputs.SequenceEqual(ExternalNondeterminism))
            return Invalid("HCR7L1006", "Runtime determinism categories or external-input boundaries are incomplete.");
        if (manifest.AuthorityBoundaries is null || manifest.AuthorityBoundaries.Count != BoundaryRules.Length ||
            manifest.AuthorityBoundaries.Any(static row => row is null || !row.Passed) ||
            !manifest.AuthorityBoundaries.Select(static row => (row.Source, row.ForbiddenDependency, row.NeutralBoundary)).SequenceEqual(BoundaryRules))
            return Invalid("HCR7L1007", "An authority dependency boundary is missing or failed.");
        if (manifest.FinalMatrix.Count != RequiredMatrix.Length || manifest.FinalMatrix.Any(static row => !row.Qualified) ||
            !manifest.FinalMatrix.Select(static row => (row.Capability, row.EvidencePhases, row.FinalRepresentation)).SequenceEqual(RequiredMatrix))
            return Invalid("HCR7L1008", "The required final qualification matrix is incomplete.");
        if (!manifest.FailureDomains.SequenceEqual(Domains))
            return Invalid("HCR7L1009", "Managed, runtime, kernel, architectural and integrity failure domains are not distinct.");
        if (manifest.DeterministicDigest != ComputeDigest(manifest with { DeterministicDigest = string.Empty }))
            return Invalid("HCR7L1010", "Final release manifest digest mismatch.");
        return new(true, "HCR7L0000", "RefPlan7 final release manifest is exact and internally consistent.");
    }

    private static bool HasValidRuntimeDeterminismRows(IReadOnlyList<HybridCpuRefPlan7RuntimeDeterminismClaimV1>? rows) =>
        rows is not null && rows.Count == RuntimeRules.Length &&
        !rows.Any(static row => row is null || !Enum.IsDefined(row.Disposition)) &&
        rows.Select(static row => (row.Category, row.Disposition))
            .SequenceEqual(RuntimeRules.Select(static row => (row.Category, row.Disposition)));

    // Structural admission of producer declarations; no build execution is witnessed here.
    internal static bool HasValidImageQualification(HybridCpuRefPlan7ImageQualificationV1? image) =>
        image is not null && image.CleanBuildA && image.CleanBuildB &&
        IsHex(image.SourceDigest, 64) && IsHex(image.ToolchainDigest, 64) &&
        IsHex(image.OptionsDigest, 64) && IsHex(image.BuildASha256, 64) &&
        image.BuildASha256 == image.BuildBSha256;

    public static string Serialize(HybridCpuRefPlan7FinalReleaseManifestV1 manifest) =>
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

    private static bool ComponentRevisionsValid(HybridCpuRefPlan7ComponentRevisionsV1 revisions) =>
        revisions is not null && !string.IsNullOrWhiteSpace(revisions.CompilerSha) &&
        IsHex(revisions.CompilerSha, 40) && revisions.ManagedRuntimeSha == revisions.CompilerSha &&
        revisions.RuntimeKernelSha == revisions.CompilerSha && revisions.IseSha == revisions.CompilerSha;

    private static string ComputeDigest(HybridCpuRefPlan7FinalReleaseManifestV1 manifest) => Hash(
        JsonSerializer.Serialize(manifest with { DeterministicDigest = string.Empty }));

    private static HybridCpuRefPlan7FinalReleaseValidationV1 Invalid(string code, string reason) => new(false, code, reason);

    // Evidence-index rows are untrusted paths. A green release matrix is only derivable after
    // every phase artifact has been opened, content-hashed and minimally schema/identity checked.
    // This deliberately rejects legacy evidence that cannot bind all component revisions to the
    // requested final-release commit instead of silently treating it as current qualification.
    private static HybridCpuRefPlan7AbiDigestsV1 CurrentAbiDigests() => new(
        HybridCpuNativeAbiContractV2.Default.ContractDigest,
        HybridCpuManagedAbiFamilyV1.Default.ContractDigest,
        HybridCpuRestrictedStartupOptionsV1.Production.OptionsDigest,
        HybridCpuKernelThreadingContractV1.ContractDigest,
        HybridCpuTrapAbiV1.ContractDigest);

    private static bool TryVerifyEvidenceArtifacts(IReadOnlyList<HybridCpuRefPlan7EvidenceArtifactV1> artifacts,
        string expectedCommit, out string failure)
    {
        string[] phases = Enumerable.Range(0, 16).Select(static phase => phase.ToString("00")).ToArray();
        if (artifacts is null || artifacts.Count != phases.Length || artifacts.Any(static artifact => artifact is null) ||
            !artifacts.Select(static artifact => artifact.Phase).SequenceEqual(phases))
        {
            failure = "Evidence artifacts must contain exactly ordered Phase 00-15 rows.";
            return false;
        }
        foreach (HybridCpuRefPlan7EvidenceArtifactV1 artifact in artifacts)
        {
            if (artifact.Disposition != EvidenceClosed || string.IsNullOrWhiteSpace(artifact.Path) ||
                string.IsNullOrWhiteSpace(artifact.Sha256) || !IsHex(artifact.Sha256, 64) || !File.Exists(artifact.Path))
            {
                failure = $"Phase {artifact.Phase} evidence path, disposition or digest is invalid.";
                return false;
            }
            byte[] bytes;
            try { bytes = File.ReadAllBytes(artifact.Path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure = $"Phase {artifact.Phase} evidence cannot be opened: {exception.Message}";
                return false;
            }
            string actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actualHash, artifact.Sha256, StringComparison.Ordinal))
            {
                failure = $"Phase {artifact.Phase} evidence SHA-256 does not match its index row.";
                return false;
            }
            try
            {
                using JsonDocument document = JsonDocument.Parse(bytes);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !HasUniqueJsonMembers(root) || !StringProperty(root, "schema", out string schema) ||
                    !MatchesEvidenceSchema(artifact.Phase, schema) || !StringProperty(root, "phase", out string phase) ||
                    !string.Equals(phase, artifact.Phase, StringComparison.Ordinal) ||
                    !EvidenceClosedProperty(root) || !HasBoundComponentRevisions(root, expectedCommit) ||
                    !HasBoundPhaseContracts(artifact.Phase, root))
                {
                    failure = $"Phase {artifact.Phase} evidence schema, phase, EvidenceClosed state, component revisions or contract digests are incomplete.";
                    return false;
                }
            }
            catch (JsonException)
            {
                failure = $"Phase {artifact.Phase} evidence is not valid JSON.";
                return false;
            }
        }
        failure = string.Empty;
        return true;
    }

    private static bool MatchesEvidenceSchema(string phase, string schema) => schema is not null && schema == (phase switch
    {
        "00" => "HybridCPU.RefPlan7.Phase00ConformanceEvidenceV1",
        "01" => "HybridCPU.RefPlan7.Phase01BootstrapEvidenceV1",
        "02" => "HybridCPU.RefPlan7.Phase02ManagedReferencesEvidenceV1",
        "03" => "HybridCPU.RefPlan7.Phase03.ManagedAllocationEvidenceV1",
        "04" => "HybridCPU.RefPlan7.Phase04.ManagedCoreShapesEvidenceV1",
        "05" => "HybridCPU.RefPlan7.Phase05.NonMovingGcEvidenceV1",
        "06" => "HybridCPU.RefPlan7.Phase06.ManagedDispatchEvidenceV1",
        "07" => "HybridCPU.RefPlan7.Phase07.DelegatesEvidenceV1",
        "08" => "HybridCPU.RefPlan7.Phase08.ExactAotGenericsEvidenceV1",
        "09" => "HybridCPU.RefPlan7.Phase09.ManagedEhEvidenceV1",
        "10" => "HybridCPU.RefPlan7.Phase10.FaultTrapEvidenceV1",
        "11" => "HybridCPU.RefPlan7.Phase11.ThreadingTlsEvidenceV1",
        "12" => "HybridCPU.RefPlan7.Phase12.SynchronizationEvidenceV1",
        "13" => "HybridCPU.RefPlan7.Phase13.ManagedInteropEvidenceV1",
        "14" => "HybridCPU.RefPlan7.Phase14.ManagedReflectionEvidenceV1",
        "15" => "HybridCPU.RefPlan7.Phase15.ManagedAsyncEvidenceV1",
        _ => null
    }) && phase is not null && phase.Length == 2;

    internal static HybridCpuRefPlan7EvidenceArtifactV1[] ParseEvidenceIndex(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!HasUniqueJsonMembers(document.RootElement))
            throw new InvalidDataException("Evidence index contains duplicate JSON members.");
        return document.RootElement.Deserialize<HybridCpuRefPlan7EvidenceArtifactV1[]>()
            ?? throw new InvalidDataException("Evidence index is empty.");
    }

    private static bool HasUniqueJsonMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
                if (!names.Add(property.Name) || !HasUniqueJsonMembers(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in element.EnumerateArray())
                if (!HasUniqueJsonMembers(item)) return false;
        return true;
    }

    private static bool EvidenceClosedProperty(JsonElement root) =>
        (StringProperty(root, "status", out string status) || StringProperty(root, "disposition", out status)) &&
        string.Equals(status, EvidenceClosed, StringComparison.Ordinal);

    private static bool HasBoundComponentRevisions(JsonElement root, string expectedCommit)
    {
        if (!root.TryGetProperty("componentRevisions", out JsonElement revisions) || revisions.ValueKind != JsonValueKind.Object)
            return false;
        foreach (string component in new[] { "compiler", "ise", "managedRuntime", "runtimeKernel" })
        {
            if (!revisions.TryGetProperty(component, out JsonElement value) || !RevisionEquals(value, expectedCommit))
                return false;
        }
        return true;
    }

    private static bool RevisionEquals(JsonElement value, string expectedCommit) => value.ValueKind switch
    {
        JsonValueKind.String => string.Equals(value.GetString(), expectedCommit, StringComparison.Ordinal),
        JsonValueKind.Object => StringProperty(value, "revision", out string revision) &&
                                string.Equals(revision, expectedCommit, StringComparison.Ordinal),
        _ => false
    };

    private static bool HasContractDigests(JsonElement root)
    {
        if (!root.TryGetProperty("contracts", out JsonElement contracts) || contracts.ValueKind != JsonValueKind.Object)
            return false;
        if (!MatchesManagedAbiDigest(contracts)) return false;
        int digestCount = 0;
        HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in contracts.EnumerateObject())
        {
            if (!names.Add(property.Name)) return false;
            if (property.Name.EndsWith("Digest", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String && IsHex(property.Value.GetString() ?? string.Empty, 64))
                digestCount++;
        }
        return digestCount >= 3;
    }

    private static bool HasBoundPhaseContracts(string phase, JsonElement root) =>
        HasContractDigests(root) &&
        MatchesPhaseTargetDigest(phase, root.GetProperty("contracts")) &&
        MatchesPhaseNativeAbiDigest(phase, root.GetProperty("contracts")) &&
        MatchesPhaseFeatureSetDigest(phase, root.GetProperty("contracts")) &&
        MatchesPhaseRuntimePackDigest(phase, root.GetProperty("contracts"));

    private static bool MatchesPhaseTargetDigest(string phase, JsonElement contracts) =>
        phase is not ("12" or "13" or "14" or "15") ||
        (StringProperty(contracts, "targetPlatformContractDigest", out string digest) &&
         string.Equals(digest, HybridCpuTargetPlatformContractV1.Default.ContractDigest, StringComparison.Ordinal));

    private static bool MatchesPhaseNativeAbiDigest(string phase, JsonElement contracts) =>
        phase is not ("00" or "01" or "02" or "03" or "04" or "05") ||
        (StringProperty(contracts, "nativeAbiDigest", out string digest) &&
         string.Equals(digest, HybridCpuNativeAbiContractV2.Default.ContractDigest, StringComparison.Ordinal));

    private static bool MatchesPhaseFeatureSetDigest(string phase, JsonElement contracts) =>
        phase == "00" ||
        (StringProperty(contracts, "managedFeatureSetDigest", out string digest) &&
         string.Equals(digest, HybridCpuManagedFeatureSetV1.Default.ContractDigest, StringComparison.Ordinal));

    private static bool MatchesPhaseRuntimePackDigest(string phase, JsonElement contracts) =>
        phase == "00" ||
        (StringProperty(contracts, "runtimePackDigest", out string digest) &&
         string.Equals(digest, HybridCpuSdkPackContractV1.CreateManifest().ContractDigest, StringComparison.Ordinal));

    private static bool MatchesManagedAbiDigest(JsonElement contracts) =>
        StringProperty(contracts, "managedAbiDigest", out string digest) &&
        string.Equals(digest, HybridCpuManagedAbiFamilyV1.Default.ContractDigest, StringComparison.Ordinal);

    private static bool StringProperty(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }
    private static bool IsHex(string? value, int length) => value is not null && value.Length == length && value.All(Uri.IsHexDigit);
    private static void RequireHex(string value, int length, string parameter)
    {
        if (!IsHex(value, length)) throw new ArgumentException($"Expected a {length}-character hexadecimal identity.", parameter);
    }
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
