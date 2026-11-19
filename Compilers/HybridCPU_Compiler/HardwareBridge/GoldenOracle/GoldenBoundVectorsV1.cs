// HB-INT-04 continuation: exact, non-executing scalar request/observation binding.
// SHA-256 here is transport integrity, never an independent Golden correctness attestation.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;

namespace HybridCPU.Compiler.HardwareBridge.GoldenOracle;

/// <summary>
/// An immutable software-facts envelope for the existing reviewed AddWord32 observation.
/// No delegate, execution policy, memory/clock semantics, accepted RTL or proof status is encoded.
/// </summary>
public sealed record GoldenBoundVectorsV1(
    string Schema,
    string SourceSnapshotDigest,
    IReadOnlyList<BridgeDocumentDigestV1> OriginalDocuments,
    string OriginalSourceSetSha256,
    string CompiledAssemblySha256,
    int RootIndex,
    string AdapterId,
    string ReviewedSourceLabel,
    IReadOnlyList<GoldenVectorV1> RequestedVectors,
    GoldenObservationV1 Observation,
    string ReplayIdentity)
{
    public const string SchemaId = "hybridcpu.hardwarebridge.golden-bound-vectors/v1";
    public byte[] ToJsonUtf8() => JsonSerializer.SerializeToUtf8Bytes(this);
}

/// <summary>
/// Data-only codec: binds caller-supplied observed AddWord cases to exact independently requested
/// vector list, source snapshot and compiled bytes. Does not establish that any delegate was pure,
/// source/PE correspond, observations came from Golden, or RTL matches.
/// </summary>
public static class GoldenBoundVectorsCodecV1
{
    public const int MaxJsonBytes = 2 * 1024 * 1024;
    public const int MaxAssemblyBytes = 32 * 1024 * 1024;
    public const int MaxVectors = 512;
    private static readonly JsonSerializerOptions Strict = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    /// <summary>
    /// No assembly or delegate load. Existing caller explicitly invokes the reviewed AddWord
    /// adapter outside this codec; the codec only matches the resulting data to the request.
    /// </summary>
    public static GoldenBoundVectorsV1 Capture(
        BridgeSourceSnapshotV1 source,
        IReadOnlyList<ReadOnlyMemory<byte>> originalDocumentBytes,
        ReadOnlyMemory<byte> compiledAssemblyBytes,
        int rootIndex,
        IReadOnlyList<GoldenVectorV1> requestedVectors,
        GoldenObservationV1 observation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(originalDocumentBytes);
        ArgumentNullException.ThrowIfNull(requestedVectors);
        ArgumentNullException.ThrowIfNull(observation);
        if (!source.HasConsistentDigest() || originalDocumentBytes.Count != source.Documents.Count ||
            compiledAssemblyBytes.IsEmpty || compiledAssemblyBytes.Length > MaxAssemblyBytes ||
            rootIndex < 0 || rootIndex >= source.Roots.Count)
            throw new ArgumentException("HB4100: Missing/stale source snapshot, original bytes, assembly or root.");

        for (int i = 0; i < source.Documents.Count; ++i)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BridgeDocumentDigestV1 document = source.Documents[i];
            ReadOnlyMemory<byte> bytes = originalDocumentBytes[i];
            if (bytes.Length != document.Utf8Bytes || Hash(bytes.Span) != document.Sha256)
                throw new ArgumentException("HB4101: Original source bytes do not match captured source.");
        }
        if (!ConsistentVectors(requestedVectors, observation, cancellationToken))
            throw new ArgumentException("HB4102: Unbound vector, wrong adapter, malformed case or observation mismatch.");

        var docs = Array.AsReadOnly(source.Documents.ToArray());
        var vectors = Array.AsReadOnly(requestedVectors.Select(v =>
            new GoldenVectorV1(v.A, v.B)).ToArray());
        GoldenObservationV1 frozenObservation = observation with
        {
            Cases = Array.AsReadOnly(observation.Cases.ToArray())
        };
        var result = new GoldenBoundVectorsV1(
            GoldenBoundVectorsV1.SchemaId, source.SnapshotDigest, docs,
            Hash(JsonSerializer.SerializeToUtf8Bytes(docs)),
            Hash(compiledAssemblyBytes.Span), rootIndex,
            TrustedAddWordOracleV1.AllowlistedAdapterId, observation.TrustedSourceLabel,
            vectors, frozenObservation, "");
        result = result with { ReplayIdentity = Digest(result) };
        if (result.ToJsonUtf8().Length > MaxJsonBytes)
            throw new ArgumentException("HB4103: Bounded vector interchange exceeds JSON budget.");
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>
    /// The caller MUST supply expected source, assembly digest, reviewed label and ordered input
    /// vectors independently of the document being read. No self-supplied expectation is accepted.
    /// </summary>
    public static GoldenBoundVectorsV1 ReadAndValidate(
        ReadOnlyMemory<byte> utf8,
        BridgeSourceSnapshotV1 expectedSource,
        string expectedCompiledAssemblySha256,
        string expectedReviewedSourceLabel,
        IReadOnlyList<GoldenVectorV1> independentlyRequestedVectors,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(expectedSource);
        ArgumentNullException.ThrowIfNull(independentlyRequestedVectors);
        if (!expectedSource.HasConsistentDigest() || utf8.IsEmpty ||
            utf8.Length > MaxJsonBytes || !IsHash(expectedCompiledAssemblySha256) ||
            !IsLabel(expectedReviewedSourceLabel))
            throw new ArgumentException("HB4104: Expected source/assembly/label or byte budget invalid.");
        try
        {
            using JsonDocument json = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicateKeys(json.RootElement);
            GoldenBoundVectorsV1? record = JsonSerializer.Deserialize<GoldenBoundVectorsV1>(utf8.Span, Strict);
            if (record is null || !ValidEnvelope(record, expectedSource,
                    expectedCompiledAssemblySha256, expectedReviewedSourceLabel,
                    independentlyRequestedVectors, cancellationToken) ||
                !string.Equals(record.ReplayIdentity, Digest(record), StringComparison.Ordinal) ||
                !utf8.Span.SequenceEqual(record.ToJsonUtf8()))
                throw new ArgumentException("HB4105: Noncanonical, stale or invalid vector replay envelope.");
            cancellationToken.ThrowIfCancellationRequested();
            return record with
            {
                OriginalDocuments = Array.AsReadOnly(record.OriginalDocuments.ToArray()),
                RequestedVectors = Array.AsReadOnly(record.RequestedVectors.ToArray()),
                Observation = record.Observation with
                {
                    Cases = Array.AsReadOnly(record.Observation.Cases.ToArray())
                }
            };
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("HB4105: Malformed/unknown vector replay fields.", nameof(utf8), exception);
        }
    }

    private static bool ValidEnvelope(
        GoldenBoundVectorsV1 value,
        BridgeSourceSnapshotV1 expectedSource,
        string assemblyHash,
        string reviewedLabel,
        IReadOnlyList<GoldenVectorV1> independentVectors,
        CancellationToken cancellationToken)
    {
        if (value.Schema != GoldenBoundVectorsV1.SchemaId ||
            value.SourceSnapshotDigest != expectedSource.SnapshotDigest ||
            value.CompiledAssemblySha256 != assemblyHash ||
            value.RootIndex < 0 || value.RootIndex >= expectedSource.Roots.Count ||
            value.AdapterId != TrustedAddWordOracleV1.AllowlistedAdapterId ||
            value.ReviewedSourceLabel != reviewedLabel ||
            value.OriginalDocuments is null ||
            !value.OriginalDocuments.SequenceEqual(expectedSource.Documents) ||
            value.OriginalSourceSetSha256 != Hash(JsonSerializer.SerializeToUtf8Bytes(value.OriginalDocuments)) ||
            value.RequestedVectors is null ||
            value.RequestedVectors.Count != independentVectors.Count ||
            value.Observation is null ||
            !ConsistentVectors(value.RequestedVectors, value.Observation, cancellationToken))
            return false;
        for (int i = 0; i < independentVectors.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (independentVectors[i] is null || independentVectors[i] != value.RequestedVectors[i])
                return false;
        }
        return true;
    }

    private static bool ConsistentVectors(IReadOnlyList<GoldenVectorV1> vectors,
        GoldenObservationV1 observation, CancellationToken cancellationToken)
    {
        if (vectors.Count is < 1 or > MaxVectors ||
            observation.Schema != GoldenObservationV1.SchemaId ||
            observation.AdapterId != TrustedAddWordOracleV1.AllowlistedAdapterId ||
            !IsLabel(observation.TrustedSourceLabel) ||
            observation.Cases is null || observation.Cases.Count != vectors.Count)
            return false;
        int blocked = 0;
        for (int i = 0; i < vectors.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GoldenVectorV1 v = vectors[i];
            GoldenCaseV1 c = observation.Cases[i];
            if (v is null || v.A is null || v.B is null || !Canonical32(v.A) || !Canonical32(v.B) ||
                v.A.Signed != v.B.Signed || c is null || c.A != v.A || c.B != v.B)
                return false;
            if (c.Status == "Observed")
            {
                if (c.Diagnostic is not null || c.Output is null || !Canonical32(c.Output) ||
                    c.Output.Signed != v.A.Signed)
                    return false;
            }
            else if (c.Status == "Blocked")
            {
                if (c.Output is not null || c.Diagnostic is not { Length: > 0 and <= 256 })
                    return false;
                blocked++;
            }
            else return false;
        }
        return observation.Status == (blocked == 0 ? "Observed" : "Blocked");
    }

    private static bool Canonical32(GoldenWordV1 value) => value.Width == 32 && value.IsCanonical;

    private static bool IsLabel(string? value) =>
        value is { Length: > 0 and <= 128 } && !string.IsNullOrWhiteSpace(value) &&
        value.All(c => !char.IsControl(c));

    private static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static string Digest(GoldenBoundVectorsV1 value) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            value.Schema, value.SourceSnapshotDigest, value.OriginalDocuments,
            value.OriginalSourceSetSha256, value.CompiledAssemblySha256, value.RootIndex,
            value.AdapterId, value.ReviewedSourceLabel, value.RequestedVectors, value.Observation
        }));

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static void RejectDuplicateKeys(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in json.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ArgumentException("HB4105: Duplicate JSON member.");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (json.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement member in json.EnumerateArray())
                RejectDuplicateKeys(member);
        }
    }
}
