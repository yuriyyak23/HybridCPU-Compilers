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
            facts is not null && (facts.Schema != BridgeCompilerFactsV1.SchemaId ||
                facts.SnapshotDigest != source.SnapshotDigest ||
                facts.Status is not ("Candidate" or "Rejected")) ||
            oracle is not null && (oracle.Schema != GoldenObservationV1.SchemaId ||
                oracle.Status is not ("Observed" or "Blocked")))
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
                 status is not ("Candidate" or "Rejected")))
                throw new ArgumentException("Stale or invalid facts payload.");
            if (oracle.ValueKind != JsonValueKind.Null &&
                (!TryString(oracle, "Schema", out string? oracleSchema) ||
                 oracleSchema != GoldenObservationV1.SchemaId ||
                 !TryString(oracle, "Status", out string? observationStatus) ||
                 observationStatus is not ("Observed" or "Blocked")))
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
