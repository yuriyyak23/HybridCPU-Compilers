// Caller-allowlisted, synchronous, read-only scalar observations; not RTL comparison or proof.
using System.Text.Json;

namespace HybridCPU.Compiler.HardwareBridge.GoldenOracle;

/// <summary>Exact little-significance bit pattern; signedness controls interpretation, not storage.</summary>
public sealed record GoldenWordV1(int Width, bool Signed, ulong Bits)
{
    public bool IsCanonical =>
        Width is 8 or 16 or 32 or 64 && (Width == 64 || (Bits >> Width) == 0);

    public long ToSigned64()
    {
        if (!IsCanonical || !Signed) throw new ArgumentException("Expected a canonical signed word.");
        int shift = 64 - Width;
        return unchecked((long)(Bits << shift)) >> shift;
    }
}

public sealed record GoldenVectorV1(GoldenWordV1 A, GoldenWordV1 B);

public sealed record GoldenCaseV1(
    GoldenWordV1 A, GoldenWordV1 B, GoldenWordV1? Output,
    string Status, string? Diagnostic);

/// <summary>Observation status is Observed or Blocked, never hardware Pass/Verified.</summary>
public sealed record GoldenObservationV1(
    string Schema, string AdapterId, string TrustedSourceLabel,
    string Status, IReadOnlyList<GoldenCaseV1> Cases)
{
    public const string SchemaId = "hybridcpu.hardwarebridge.golden-observation/v1";
    public byte[] ToJsonUtf8() => JsonSerializer.SerializeToUtf8Bytes(this);
}

/// <summary>
/// A single trusted 32-bit, stateless AddWord adapter explicitly registered by the host.
/// The host MUST audit the delegate's purity; this bridge cannot sandbox or attest arbitrary code.
/// There is no name-based assembly resolution or delegate loading from JSON.
/// </summary>
public sealed class TrustedAddWordOracleV1
{
    private readonly Func<uint, uint, uint> _trusted;
    private readonly string _sourceLabel;
    private readonly bool _signed;
    public const string AllowlistedAdapterId = "explicit-trusted-addword32/v1";

    private TrustedAddWordOracleV1(Func<uint, uint, uint> trusted, string sourceLabel, bool signed)
    {
        _trusted = trusted; _sourceLabel = sourceLabel; _signed = signed;
    }

    /// <summary>Registration is a caller-controlled trust decision, not a purity/semantic proof.</summary>
    public static TrustedAddWordOracleV1 RegisterExplicitlyTrusted(
        Func<uint, uint, uint> trustedPureAddWord, string reviewedSourceLabel, bool signed)
    {
        ArgumentNullException.ThrowIfNull(trustedPureAddWord);
        if (string.IsNullOrWhiteSpace(reviewedSourceLabel) || reviewedSourceLabel.Length > 128 ||
            reviewedSourceLabel.Any(char.IsControl))
            throw new ArgumentException("An explicit trusted source identifier is required.");
        return new(trustedPureAddWord, reviewedSourceLabel, signed);
    }

    public GoldenObservationV1 Observe(string adapterId, IReadOnlyList<GoldenVectorV1> vectors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        cancellationToken.ThrowIfCancellationRequested();
        if (vectors.Count is < 1 or > 512)
            return Blocked("HB3001: Vector count outside 1..512.");
        if (!string.Equals(adapterId, AllowlistedAdapterId, StringComparison.Ordinal))
            return Blocked("HB3002: No trusted adapter registered for the requested ID.");
        var cases = new List<GoldenCaseV1>(vectors.Count);
        foreach (GoldenVectorV1 vector in vectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (vector is null || vector.A is null || vector.B is null ||
                !vector.A.IsCanonical || !vector.B.IsCanonical ||
                vector.A.Width != 32 || vector.B.Width != 32 ||
                vector.A.Signed != _signed || vector.B.Signed != _signed)
            {
                // 64-bit/high-half, mixed sign, heap, exceptions and unknown widths have no contract.
                cases.Add(new(vector?.A ?? new(32, _signed, 0), vector?.B ?? new(32, _signed, 0),
                    null, "Blocked", "HB3003: Unsupported scalar input width/sign/bit pattern."));
                continue;
            }
            try
            {
                // Only the explicitly caller-reviewed delegate may execute.
                uint value = _trusted((uint)vector.A.Bits, (uint)vector.B.Bits);
                cancellationToken.ThrowIfCancellationRequested();
                cases.Add(new(vector.A, vector.B, new(32, _signed, value), "Observed", null));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A thrown exception is an observation failure, never a normal scalar output.
                cases.Add(new(vector.A, vector.B, null, "Blocked",
                    "HB3004: Trusted adapter threw " + exception.GetType().Name));
            }
        }
        return new(GoldenObservationV1.SchemaId, AllowlistedAdapterId, _sourceLabel,
            cases.All(c => c.Status == "Observed") ? "Observed" : "Blocked",
            Array.AsReadOnly(cases.ToArray()));

        GoldenObservationV1 Blocked(string reason) => new(
            GoldenObservationV1.SchemaId, AllowlistedAdapterId, _sourceLabel, "Blocked",
            Array.AsReadOnly(new[] {
                new GoldenCaseV1(new(32, _signed, 0), new(32, _signed, 0), null, "Blocked", reason)
            }));
    }
}
