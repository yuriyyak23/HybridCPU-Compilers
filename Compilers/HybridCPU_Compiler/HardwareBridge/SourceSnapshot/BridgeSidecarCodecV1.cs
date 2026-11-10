// Deterministic diagnostic sidecar envelope; integrity checksum is not a signature or proof.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridCPU.Compiler.HardwareBridge.CompilerFacts;
using HybridCPU.Compiler.HardwareBridge.GoldenOracle;

namespace HybridCPU.Compiler.HardwareBridge.SourceSnapshot;

/// <summary>
/// Versioned JSON bytes in stable declared-property order. Read requires an expected live snapshot
/// digest; a self-checksum detects corruption only and confers no external trust.
/// </summary>
public static class BridgeSidecarCodecV1
{
    public const string SchemaId = "hybridcpu.hardwarebridge.sidecar/v1";
    public const int MaxSidecarBytes = 16 * 1024 * 1024;

    public static byte[] Write(BridgeSourceSnapshotV1 source,
        BridgeCompilerFactsV1? facts = null, GoldenObservationV1? oracle = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.HasConsistentDigest() ||
            facts is not null && (facts.SnapshotDigest != source.SnapshotDigest || !ValidFacts(facts)) ||
            oracle is not null && !ValidOracle(oracle))
            throw new ArgumentException("Unknown/stale candidate sidecar component.");
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = SchemaId, Source = source, Facts = facts, GoldenOracle = oracle
        });
        string digest = Hash(payload);
        byte[] result = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = SchemaId, PayloadSha256 = digest,
            Payload = JsonSerializer.Deserialize<JsonElement>(payload)
        });
        if (result.Length > MaxSidecarBytes)
            throw new ArgumentException("Diagnostic sidecar byte budget exceeded.");
        return result;
    }

    /// <summary>Throws on missing/malformed, stale or altered evidence; never auto-loads a delegate.</summary>
    public static JsonDocument ReadAndValidate(ReadOnlyMemory<byte> bytes, string expectedSnapshotDigest,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.IsEmpty || bytes.Length > MaxSidecarBytes ||
            string.IsNullOrWhiteSpace(expectedSnapshotDigest))
            throw new ArgumentException("Empty, oversized or unbound sidecar.");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Malformed sidecar JSON.", nameof(bytes), exception);
        }
        try
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryString(root, "Schema", out string? schema) || schema != SchemaId ||
                !TryString(root, "PayloadSha256", out string? digest) ||
                !root.TryGetProperty("Payload", out JsonElement payload) ||
                payload.ValueKind != JsonValueKind.Object ||
                !string.Equals(digest, Hash(Encoding.UTF8.GetBytes(payload.GetRawText())), StringComparison.Ordinal) ||
                !TryString(payload, "Schema", out string? payloadSchema) || payloadSchema != SchemaId ||
                !payload.TryGetProperty("Source", out JsonElement source) ||
                source.ValueKind != JsonValueKind.Object ||
                !TryString(source, "Schema", out string? sourceSchema) ||
                sourceSchema != BridgeSourceSnapshotV1.SchemaId ||
                !TryString(source, "SnapshotDigest", out string? sourceDigest) ||
                sourceDigest != expectedSnapshotDigest ||
                !JsonSerializer.Deserialize<BridgeSourceSnapshotV1>(source.GetRawText())!.HasConsistentDigest())
                throw new ArgumentException("Sidecar integrity, schema or current source identity mismatch.");
            if (!payload.TryGetProperty("Facts", out JsonElement facts) ||
                !payload.TryGetProperty("GoldenOracle", out JsonElement oracle))
                throw new ArgumentException("Incomplete sidecar payload.");
            if (facts.ValueKind != JsonValueKind.Null &&
                (!TryString(facts, "Schema", out string? factsSchema) ||
                 factsSchema != BridgeCompilerFactsV1.SchemaId ||
                 !TryString(facts, "SnapshotDigest", out string? factsDigest) ||
                 factsDigest != sourceDigest ||
                 !TryString(facts, "Status", out string? status) ||
                 status is not ("Candidate" or "Rejected") ||
                 !ValidFacts(JsonSerializer.Deserialize<BridgeCompilerFactsV1>(facts.GetRawText()))))
                throw new ArgumentException("Stale or invalid facts payload.");
            if (oracle.ValueKind != JsonValueKind.Null &&
                (!TryString(oracle, "Schema", out string? oracleSchema) ||
                 oracleSchema != GoldenObservationV1.SchemaId ||
                 !TryString(oracle, "Status", out string? observationStatus) ||
                 observationStatus is not ("Observed" or "Blocked") ||
                 !ValidOracle(JsonSerializer.Deserialize<GoldenObservationV1>(oracle.GetRawText()))))
                throw new ArgumentException("Invalid oracle observation payload.");
            cancellationToken.ThrowIfCancellationRequested();
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static bool ValidFacts(BridgeCompilerFactsV1? facts) =>
        facts is not null && facts.Schema == BridgeCompilerFactsV1.SchemaId &&
        (facts.Status is "Candidate" or "Rejected") && facts.Methods is not null &&
        facts.RootIdentities is not null && facts.Edges is not null && facts.SourceMaps is not null &&
        facts.Diagnostics is not null &&
        (facts.Status != "Candidate" ||
            (facts.ImportStatus == "Success" &&
             IsHexHash(facts.SourceInputDigest) && IsHexHash(facts.PeSha256) &&
             IsHexHash(facts.PdbSha256) && !string.IsNullOrWhiteSpace(facts.GraphDigest) &&
             !string.IsNullOrWhiteSpace(facts.GraphContractDigest) &&
             facts.Methods.Count > 0 && facts.RootIdentities.Count > 0));

    private static bool ValidOracle(GoldenObservationV1? observation) =>
        observation is not null && observation.Schema == GoldenObservationV1.SchemaId &&
        observation.AdapterId == TrustedAddWordOracleV1.AllowlistedAdapterId &&
        !string.IsNullOrWhiteSpace(observation.TrustedSourceLabel) &&
        observation.Cases is { Count: >= 1 and <= 512 } &&
        observation.Cases.All(c => c is not null && c.A is not null && c.B is not null &&
            (c.Status == "Blocked" && c.Output is null ||
             c.Status == "Observed" && c.A.IsCanonical && c.B.IsCanonical &&
             c.A.Width == 32 && c.B.Width == 32 && c.A.Signed == c.B.Signed &&
             c.Output is { Width: 32 } output && output.IsCanonical &&
             output.Signed == c.A.Signed)) &&
        (observation.Status == "Observed" && observation.Cases.All(c => c.Status == "Observed") ||
         observation.Status == "Blocked" && observation.Cases.Any(c => c.Status == "Blocked"));

    private static bool IsHexHash(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool TryString(JsonElement value, string name, out string? text)
    {
        text = null;
        return value.TryGetProperty(name, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String &&
            (text = property.GetString()) is not null;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));
}
