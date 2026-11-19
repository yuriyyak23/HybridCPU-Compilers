// HB-INT-03: an opt-in, unqualified software-provenance companion to Core's v2 chunks.
// v1 codec and v1 digest preimage are intentionally untouched.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridCPU.Compiler.HardwareBridge.CompilerFacts;

namespace HybridCPU.Compiler.HardwareBridge.SourceSnapshot;

public sealed record BridgeV2SourceRole(string Path, string Role);
public sealed record BridgeV2Evidence(
    string SdkVersion, ReadOnlyMemory<byte> CscImage, ReadOnlyMemory<byte> CscOptionsUtf8,
    ReadOnlyMemory<byte> PeImage, ReadOnlyMemory<byte> PdbImage,
    IReadOnlyList<BridgeV2SourceRole> SourceRoles);
public sealed record BridgeV2ProvenancePayload(
    string Schema, string SourceSnapshotDigest, string? CompanionSourceInputDigest,
    string SdkVersion, string CscSha256, string CscOptionsSha256,
    string? PeSha256, string? PdbSha256, string SoftwareStatus,
    string CoreIndexSha256, string ClosureSha256,
    IReadOnlyList<BridgeV2SourceRole> SourceRoles, IReadOnlyList<BridgeV2FileRow> Inputs);
public sealed record BridgeV2ProvenanceEnvelope(
    string Schema, string PayloadSha256, BridgeV2ProvenancePayload Payload);

public enum BridgeV2Refusal
{
    MalformedJson, DuplicateProperty, UnsupportedSchema, NonCanonical,
    Oversized, StaleSource, InvalidRole, InvalidPin, MissingInput, Cancelled
}
public sealed class BridgeV2RefusalException(BridgeV2Refusal code, string message) : Exception(message)
{
    public BridgeV2Refusal Code { get; } = code;
}

/// <summary>
/// Separate bounded provenance handshake. Digests are checksums, NOT signatures.
/// Consumer must independently inspect actual source/PE/PDB/Csc bytes and Roslyn body
/// ownership; Bridge cannot confer Verified, AcceptedRtl or Physical signoff.
/// </summary>
public static class BridgeProvenanceCodecV2
{
    public const string SchemaId = "hybridcpu.hardwarebridge.provenance-index/v2";
    public const int MaxEnvelopeBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Write(BridgeSourceSnapshotV1 source, BridgeCompilerFactsV1 facts,
        IReadOnlyList<BridgeV2Input> inputs, BridgeV2Evidence evidence,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!source.HasConsistentDigest() || facts.Schema != BridgeCompilerFactsV1.SchemaId ||
            facts.SnapshotDigest != source.SnapshotDigest ||
            facts.Status is not ("Candidate" or "Rejected"))
            throw new ArgumentException("HBINT03_V2_SOURCE: stale/unsupported source or software status.");
        if (string.IsNullOrWhiteSpace(evidence.SdkVersion) || evidence.SdkVersion.Length > 128 ||
            evidence.SdkVersion.Any(char.IsControl) ||
            evidence.CscImage.IsEmpty || evidence.CscImage.Length > 33_554_432 ||
            evidence.CscOptionsUtf8.IsEmpty || evidence.CscOptionsUtf8.Length > 1_048_576)
            throw new ArgumentException("HBINT03_V2_CSC: explicit SDK/compiler/arguments bytes required.");
        try { _ = StrictUtf8.GetString(evidence.CscOptionsUtf8.Span); }
        catch (DecoderFallbackException ex) { throw new ArgumentException("HBINT03_V2_CSC_UTF8", ex); }

        bool candidate = facts.Status == "Candidate";
        if (candidate)
        {
            if (!BridgeSegmentIndexV2.IsSha(facts.SourceInputDigest) ||
                !BridgeSegmentIndexV2.IsSha(facts.PeSha256) ||
                !BridgeSegmentIndexV2.IsSha(facts.PdbSha256) ||
                facts.ImportStatus != "Success" ||
                evidence.PeImage.IsEmpty || evidence.PdbImage.IsEmpty ||
                facts.PeSha256 != BridgeSegmentIndexV2.Hash(evidence.PeImage.Span) ||
                facts.PdbSha256 != BridgeSegmentIndexV2.Hash(evidence.PdbImage.Span))
                throw new ArgumentException("HBINT03_V2_PE_PDB: candidate facts/pins mismatch.");
        }
        else if (!evidence.PeImage.IsEmpty || !evidence.PdbImage.IsEmpty ||
                 facts.SourceInputDigest is not null || facts.PeSha256 is not null ||
                 facts.PdbSha256 is not null)
            throw new ArgumentException("HBINT03_V2_REJECTED: rejected facts cannot claim compiler pins.");

        BridgeV2Transport transport = BridgeSegmentIndexV2.Write(source.Roots, inputs, cancellationToken);
        var sourceRows = transport.Inputs.Where(x => x.Role == "source").ToArray();
        if (sourceRows.Length != source.Documents.Count)
            throw new ArgumentException("HBINT03_V2_SOURCE: source inventory differs from v1 snapshot.");
        foreach (BridgeDocumentDigestV1 document in source.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sourceRows.Any(x => x.Path == document.VirtualPath &&
                x.Sha256 == document.Sha256 && x.Bytes == document.Utf8Bytes))
                throw new ArgumentException("HBINT03_V2_SOURCE: source bytes/path inconsistent.");
        }
        var references = transport.Inputs.Where(x =>
            x.Role is "owned-body" or "metadata-only" or "unselected").ToArray();
        if (references.Length != source.References.Count)
            throw new ArgumentException("HBINT03_V2_REFERENCE: reference count mismatch.");
        foreach (BridgeReferenceDigestV1 reference in source.References)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!references.Any(x => x.Role == reference.Role &&
                x.Sha256 == reference.Sha256 && x.Bytes == reference.Bytes))
                throw new ArgumentException("HBINT03_V2_REFERENCE: role/bytes mismatch.");
        }
        var pe = transport.Inputs.Where(x => x.Role == "compiler-pe").ToArray();
        var pdb = transport.Inputs.Where(x => x.Role == "compiler-pdb").ToArray();
        if (pe.Length != (candidate ? 1 : 0) || pdb.Length != (candidate ? 1 : 0) ||
            candidate && (pe[0].Sha256 != facts.PeSha256 || pe[0].Bytes != evidence.PeImage.Length ||
                          pdb[0].Sha256 != facts.PdbSha256 || pdb[0].Bytes != evidence.PdbImage.Length))
            throw new ArgumentException("HBINT03_V2_PE_PDB: transport compiler artifacts differ.");

        if (evidence.SourceRoles is null || evidence.SourceRoles.Count != sourceRows.Length)
            throw new ArgumentException("HBINT03_V2_SOURCE_ROLE: explicit roles missing.");
        var roles = evidence.SourceRoles.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
        for (int i = 0; i < roles.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (roles[i] is null || roles[i].Path != sourceRows[i].Path ||
                roles[i].Role is not ("original" or "working" or "generated"))
                throw new ArgumentException("HBINT03_V2_SOURCE_ROLE: unknown or missing source role.");
        }
        var payload = new BridgeV2ProvenancePayload(
            SchemaId, source.SnapshotDigest, facts.SourceInputDigest, evidence.SdkVersion,
            BridgeSegmentIndexV2.Hash(evidence.CscImage.Span),
            BridgeSegmentIndexV2.Hash(evidence.CscOptionsUtf8.Span),
            candidate ? facts.PeSha256 : null, candidate ? facts.PdbSha256 : null,
            facts.Status, BridgeSegmentIndexV2.Hash(transport.IndexUtf8),
            transport.ClosureSha256, Array.AsReadOnly(roles),
            Array.AsReadOnly(transport.Inputs.ToArray()));
        byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[] result = JsonSerializer.SerializeToUtf8Bytes(new BridgeV2ProvenanceEnvelope(
            SchemaId, BridgeSegmentIndexV2.Hash(payloadBytes), payload));
        if (result.Length > MaxEnvelopeBytes)
            throw new ArgumentException("HBINT03_V2_SIZE: envelope too large.");
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>
    /// Strict typed refusal for unknown schema/fields, duplicate properties, stale live
    /// source and changed actual byte pins. Requires raw inputs from the caller; still NOT
    /// independent Core ownership verification.
    /// </summary>
    public static BridgeV2ProvenancePayload ReadAndValidate(ReadOnlyMemory<byte> bytes,
        BridgeSourceSnapshotV1 expectedCurrentSource, BridgeCompilerFactsV1 facts,
        IReadOnlyList<BridgeV2Input> actualInputs, BridgeV2Evidence actualEvidence,
        string expectedSdkVersion, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            throw new BridgeV2RefusalException(BridgeV2Refusal.Cancelled, "Cancelled.");
        if (bytes.IsEmpty || bytes.Length > MaxEnvelopeBytes)
            throw new BridgeV2RefusalException(BridgeV2Refusal.Oversized, "Envelope byte budget.");
        if (expectedCurrentSource is null || !expectedCurrentSource.HasConsistentDigest())
            throw new BridgeV2RefusalException(BridgeV2Refusal.StaleSource, "Expected live source missing.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes,
                new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow,
                    AllowTrailingCommas = false });
            RejectDuplicates(document.RootElement, cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("Schema", out JsonElement schema) ||
                schema.ValueKind != JsonValueKind.String || schema.GetString() != SchemaId)
                throw new BridgeV2RefusalException(BridgeV2Refusal.UnsupportedSchema,
                    "Unknown v2 envelope schema.");
            BridgeV2ProvenanceEnvelope? parsed =
                JsonSerializer.Deserialize<BridgeV2ProvenanceEnvelope>(bytes.Span);
            if (parsed is null || parsed.Payload is null || parsed.Payload.Schema != SchemaId)
                throw new BridgeV2RefusalException(BridgeV2Refusal.UnsupportedSchema,
                    "Unknown v2 payload schema.");
            if (!bytes.Span.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(parsed)))
                throw new BridgeV2RefusalException(BridgeV2Refusal.NonCanonical,
                    "Unknown/reordered fields or non-canonical compact UTF-8 JSON.");
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(parsed.Payload);
            if (parsed.PayloadSha256 != BridgeSegmentIndexV2.Hash(payload))
                throw new BridgeV2RefusalException(BridgeV2Refusal.InvalidPin,
                    "Payload checksum is inconsistent.");
            if (parsed.Payload.SourceSnapshotDigest != expectedCurrentSource.SnapshotDigest)
                throw new BridgeV2RefusalException(BridgeV2Refusal.StaleSource,
                    "Current source root differs.");
            if (parsed.Payload.SdkVersion != expectedSdkVersion)
                throw new BridgeV2RefusalException(BridgeV2Refusal.InvalidPin, "SDK pin differs.");
            byte[] reconstructed = Write(expectedCurrentSource, facts, actualInputs,
                actualEvidence, cancellationToken);
            if (!bytes.Span.SequenceEqual(reconstructed))
                throw new BridgeV2RefusalException(BridgeV2Refusal.InvalidPin,
                    "Current source/Csc/PE/PDB/reference/chunk bytes or roles differ.");
            return parsed.Payload;
        }
        catch (BridgeV2RefusalException) { throw; }
        catch (OperationCanceledException ex)
        {
            throw new BridgeV2RefusalException(BridgeV2Refusal.Cancelled, ex.Message);
        }
        catch (JsonException ex)
        {
            throw new BridgeV2RefusalException(BridgeV2Refusal.MalformedJson, ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            OverflowException or NullReferenceException)
        {
            throw new BridgeV2RefusalException(BridgeV2Refusal.InvalidPin, ex.Message);
        }
    }

    private static void RejectDuplicates(JsonElement node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in node.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new BridgeV2RefusalException(BridgeV2Refusal.DuplicateProperty,
                        "Duplicate JSON property: " + property.Name);
                RejectDuplicates(property.Value, cancellationToken);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in node.EnumerateArray())
                RejectDuplicates(item, cancellationToken);
    }
}
