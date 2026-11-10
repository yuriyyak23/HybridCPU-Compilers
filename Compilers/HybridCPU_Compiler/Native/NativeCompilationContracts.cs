using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Contracts;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU.Compiler.Native;

public enum NativeCompilationStatus : byte
{
    Success = 0,
    Unsupported = 1,
    InvalidInput = 2,
    BudgetExhausted = 3,
    ConservativeFallback = 4
}

public enum NativeArtifactKind : byte
{
    NormalizedAssembly = 0,
    CanonicalIr = 1,
    Schedule = 2,
    Bundles = 3,
    BinaryImage = 4,
    ObjectFile = 5,
    Evidence = 6,
    SchedulingReport = 7,
    Provenance = 8
}

public enum NativeArtifactDisposition : byte
{
    Available = 0,
    Unsupported = 1
}

public sealed record NativeArtifactMatrixRow(
    NativeArtifactKind Kind,
    NativeArtifactDisposition Disposition,
    string Reason);

public static class NativeArtifactMatrix
{
    public const string Schema = "hybridcpu.native-artifact-matrix/v1";

    public static IReadOnlyList<NativeArtifactMatrixRow> Rows { get; } = Array.AsReadOnly<NativeArtifactMatrixRow>(
    [
        new(NativeArtifactKind.NormalizedAssembly, NativeArtifactDisposition.Available, "Canonical native assembly ingress is retained in normalized form."),
        new(NativeArtifactKind.CanonicalIr, NativeArtifactDisposition.Available, "Canonical IR is produced by the shared Core pipeline."),
        new(NativeArtifactKind.Schedule, NativeArtifactDisposition.Available, "Schedule is produced by the shared Core scheduler."),
        new(NativeArtifactKind.Bundles, NativeArtifactDisposition.Available, "Exact W=8 bundles are produced by the shared Core bundle former."),
        new(NativeArtifactKind.BinaryImage, NativeArtifactDisposition.Available, "The shared Core serializer produces the native carrier image."),
        new(NativeArtifactKind.ObjectFile, NativeArtifactDisposition.Unsupported, "No standalone HybridCPU object container ABI is currently published."),
        new(NativeArtifactKind.Evidence, NativeArtifactDisposition.Available, "Deterministic structural fingerprints are exposed as compiler evidence only."),
        new(NativeArtifactKind.SchedulingReport, NativeArtifactDisposition.Available, "Deterministic schedule and bundle fingerprints are exposed."),
        new(NativeArtifactKind.Provenance, NativeArtifactDisposition.Available, "Schema, target, model, options and input digests are exposed.")
    ]);
}

public sealed record NativeCompilerDiagnostic(
    NativeCompilationStatus Status,
    string Code,
    string Message,
    int? SourceLine = null);

public sealed record NativeSchedulingReport(
    string Schema,
    string ScheduleFingerprint,
    string BundleFingerprint,
    int BundleCount);

public sealed record NativeCompilationProvenance(
    string Schema,
    string FrontendId,
    string TargetDigest,
    string ModelDigest,
    string OptionsDigest,
    string InputDigest,
    int CompilerContractVersion,
    CompilerBuildProvenanceV1 Build,
    CompilerCrossLayerEnvelopeV1 Envelope,
    CompilerSemanticCacheKeyV1 CacheKey);

public sealed record NativeBuildIdentity(
    string ProducerVersion,
    string SourceCommit,
    string SourceTree,
    IReadOnlyList<CompilerToolchainIdentity> Toolchains,
    string DataLayoutVersion,
    string AbiVersion)
{
    public int TargetSchemaMajor { get; init; } = HybridCpuTargetMachineContractV1.SchemaMajor;
    public string TargetTriple { get; init; } = HybridCpuTargetMachineContractV1.TargetTriple;
    public string DataLayoutIdentity { get; init; } = HybridCpuTargetMachineContractV1.DataLayoutIdentity;

    public static NativeBuildIdentity Unknown { get; } = new(
        "unknown",
        "unknown",
        "unknown",
        Array.Empty<CompilerToolchainIdentity>(),
        HybridCpuTargetMachineContractV1.DataLayoutVersion,
        "hybridcpu-native-abi/v1");
}

public sealed record NativeCompilationArtifacts(
    string? NormalizedAssembly,
    HybridCpuCompiledProgram CompiledProgram,
    NativeSchedulingReport SchedulingReport,
    NativeCompilationProvenance Provenance)
{
    public IrProgram CanonicalIr => CompiledProgram.BundleLayout.Program;
    public IrProgramSchedule Schedule => CompiledProgram.ProgramSchedule;
    public IrProgramBundlingResult Bundles => CompiledProgram.BundleLayout;
    public byte[] BinaryImage => CompiledProgram.ProgramImage;
}

public sealed record NativeCompilationResult(
    NativeCompilationStatus Status,
    NativeCompilationArtifacts? Artifacts,
    IReadOnlyList<NativeCompilerDiagnostic> Diagnostics)
{
    public bool Succeeded =>
        Status is NativeCompilationStatus.Success or NativeCompilationStatus.ConservativeFallback;
}

public sealed record NativeFrontendRequest(
    byte VirtualThreadId,
    string? AssemblySource = null,
    IReadOnlyList<HybridCpuInstructionWord>? InstructionWords = null,
    string OptionsIdentity = "canonical-defaults")
{
    public NativeBuildIdentity BuildIdentity { get; init; } = NativeBuildIdentity.Unknown;
    public CompilerFeatureSet AvailableCapabilities { get; init; } = CompilerCrossLayerSchemaCatalogV1.NativeV1;
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();
    public string? ProfitabilityProfileHash { get; init; }
    public CompilerSchemaDeclaration ConsumerCapabilitySchema { get; init; } = CompilerCrossLayerSchemaCatalogV1.Capability;
    public CompilerSchemaDeclaration ConsumerEnvelopeSchema { get; init; } = CompilerCrossLayerSchemaCatalogV1.Envelope;
    public CompilerSchemaDeclaration ConsumerProvenanceSchema { get; init; } = CompilerCrossLayerSchemaCatalogV1.Provenance;
}

public interface INativeCompilerFrontend
{
    string Id { get; }
    NativeCompilationResult Compile(NativeFrontendRequest request);
}

/// <summary>
/// Explicit, immutable frontend selection owned outside Core. Core never discovers or probes frontends.
/// </summary>
public sealed class NativeFrontendRegistry
{
    private readonly IReadOnlyDictionary<string, INativeCompilerFrontend> _frontends;

    public NativeFrontendRegistry(IEnumerable<INativeCompilerFrontend> frontends)
    {
        ArgumentNullException.ThrowIfNull(frontends);
        Dictionary<string, INativeCompilerFrontend> registered = new(StringComparer.Ordinal);
        foreach (INativeCompilerFrontend frontend in frontends.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(frontend);
            if (!registered.TryAdd(frontend.Id, frontend))
                throw new ArgumentException($"Duplicate native frontend id '{frontend.Id}'.", nameof(frontends));
        }
        _frontends = registered;
    }

    public IReadOnlyList<string> FrontendIds => _frontends.Keys.OrderBy(static id => id, StringComparer.Ordinal).ToArray();

    public NativeCompilationResult Compile(string frontendId, NativeFrontendRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frontendId);
        ArgumentNullException.ThrowIfNull(request);
        return _frontends.TryGetValue(frontendId, out INativeCompilerFrontend? frontend)
            ? frontend.Compile(request)
            : NativeCompilationFailure.UnsupportedFrontend(frontendId);
    }
}

internal static class NativeCompilationFailure
{
    public static NativeCompilationResult Create(
        NativeCompilationStatus status,
        string code,
        string message,
        int? sourceLine = null) =>
        new(status, null, [new NativeCompilerDiagnostic(status, code, message, sourceLine)]);

    public static NativeCompilationResult UnsupportedFrontend(string frontendId) =>
        Create(
            NativeCompilationStatus.Unsupported,
            "HCN0001",
            $"Native frontend '{frontendId}' is not registered.");
}
