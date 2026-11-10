// Candidate-only projection DTOs; no hardware qualification, executable output or runtime authority.
using System.Text.Json;

namespace HybridCPU.Compiler.HardwareBridge.CompilerFacts;

/// <summary>Diagnostic from existing Companion/CIL stages or a fail-closed bridge check.</summary>
public sealed record BridgeDiagnosticV1(string Layer, string Code, string Severity, string Message,
    string? Source = null, int? Start = null, int? Length = null);

public sealed record BridgeMethodFactV1(
    string StableIdentity, string AssemblyIdentity, string ModuleName, string DeclaringType,
    string MethodName, string CanonicalSignature, int MetadataToken,
    string IdentityDigest, string PeSha256, string ImportStatus,
    IReadOnlyList<string> DirectCallees);

public sealed record BridgeEdgeFactV1(
    string StableId, string Caller, string Callee, int IlOffset, int MetadataToken);

public sealed record BridgeMapRowV1(
    string MethodIdentity, string PeSha256, string PdbSha256, int MethodToken,
    int IlOffset, int IrIndex, string? Document, string? Checksum, int? Line, int? Column);

/// <summary>
/// Immutable snapshot of admitted *software* facts. Candidate is never Verified or AcceptedRtl.
/// SourceInputDigest is reported by Companion and is distinct from BridgeSourceSnapshotV1.SnapshotDigest.
/// </summary>
public sealed record BridgeCompilerFactsV1(
    string Schema, string Status, string SnapshotDigest, string? SourceInputDigest,
    string? ArtifactSchema, string? PeSha256, string? PdbSha256,
    string? GraphDigest, string? GraphContractDigest, string? ImportStatus,
    IReadOnlyList<string> RootIdentities, IReadOnlyList<BridgeMethodFactV1> Methods,
    IReadOnlyList<BridgeEdgeFactV1> Edges, IReadOnlyList<BridgeMapRowV1> SourceMaps,
    IReadOnlyList<BridgeDiagnosticV1> Diagnostics)
{
    public const string SchemaId = "hybridcpu.hardwarebridge.compiler-facts/v1";
    public byte[] ToJsonUtf8() => JsonSerializer.SerializeToUtf8Bytes(this);

    public static BridgeCompilerFactsV1 Rejected(string snapshotDigest, string code, string reason) =>
        new(SchemaId, "Rejected", snapshotDigest, null, null, null, null, null, null, null,
            Array.Empty<string>(), Array.Empty<BridgeMethodFactV1>(), Array.Empty<BridgeEdgeFactV1>(),
            Array.Empty<BridgeMapRowV1>(), Array.AsReadOnly(new[] {
                new BridgeDiagnosticV1("HardwareBridge", code, "Error", reason)
            }));
}
