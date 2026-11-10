using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace HybridCPU.Compiler.Core.IR.Telemetry;

public sealed record CompilerPhase00RuntimeControlSampleV1(
    int Repetition,
    bool Succeeded,
    int ExitCode,
    string NormalizedOutcomeFingerprint,
    string NormalizedOutcomeRelativePath);

public sealed record CompilerPhase00RuntimeControlProfileV1(
    string ProfileId,
    ulong WorkloadIterations,
    int RepetitionCount,
    bool AllRunsSucceeded,
    bool OutcomesDeterministic,
    string? CommonOutcomeFingerprint,
    IReadOnlyList<CompilerPhase00RuntimeControlSampleV1> Samples);

public sealed record CompilerPhase00RuntimeControlReportV1(
    string Schema,
    string BaseCommitSha,
    string Branch,
    CompilerSubjectDispositionV1 SubjectDisposition,
    string BuildConfiguration,
    string ToolVersion,
    string SourceManifestFingerprint,
    IReadOnlyList<CompilerSourceFileHashV1> SourceFiles,
    IReadOnlyList<CompilerPhase00RuntimeControlProfileV1> Profiles)
{
    public const string SchemaName = "CompilerPhase00RuntimeControlReportV1";

    public bool Succeeded => Profiles.Count == 4 &&
        Profiles.All(static profile => profile.AllRunsSucceeded && profile.OutcomesDeterministic);
}

public sealed record CompilerPhase00RuntimeTimingDistributionV1(
    int SampleCount,
    double MinimumMilliseconds,
    double MedianMilliseconds,
    double MaximumMilliseconds,
    double MeanMilliseconds,
    double PopulationStandardDeviationMilliseconds,
    double? CoefficientOfVariation);

public sealed record CompilerPhase00RuntimeControlResourceSampleV1(
    int Repetition,
    double ProcessElapsedMilliseconds,
    string RawReportRelativePath,
    string RawReportSha256,
    string StandardOutputSha256,
    string StandardErrorSha256);

public sealed record CompilerPhase00RuntimeControlResourceProfileV1(
    string ProfileId,
    CompilerPhase00RuntimeTimingDistributionV1 ProcessElapsed,
    IReadOnlyList<CompilerPhase00RuntimeControlResourceSampleV1> Samples);

public sealed record CompilerPhase00RuntimeControlResourceReportV1(
    string Schema,
    IReadOnlyList<CompilerPhase00RuntimeControlResourceProfileV1> Profiles)
{
    public const string SchemaName = "CompilerPhase00RuntimeControlResourceReportV1";
}

/// <summary>
/// Canonicalizes raw diagnostic results for repeatability checks. Only physical-time observations
/// and rates derived directly from physical time are excluded. Correctness, workload identity,
/// checksums and architectural/runtime counters remain in the outcome fingerprint. This is an
/// offline evidence operation and has no compiler code-selection or runtime-authority role.
/// </summary>
public static class CompilerPhase00RuntimeControlEvidenceV1
{
    private static readonly HashSet<string> NondeterministicProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartedUtc",
        "FinishedUtc",
        "ElapsedSeconds",
        "ElapsedMilliseconds"
    };

    public static byte[] NormalizeOutcomeJson(ReadOnlySpan<byte> rawJson)
    {
        using JsonDocument document = JsonDocument.Parse(rawJson.ToArray());
        ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            WriteCanonical(writer, document.RootElement);
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static string ComputeSha256(ReadOnlySpan<byte> contents) =>
        Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();

    public static CompilerPhase00RuntimeTimingDistributionV1 CreateTimingDistribution(
        IReadOnlyList<double> elapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(elapsedMilliseconds);
        if (elapsedMilliseconds.Count == 0)
        {
            throw new ArgumentException("At least one timing sample is required.", nameof(elapsedMilliseconds));
        }

        double[] ordered = elapsedMilliseconds.OrderBy(static value => value).ToArray();
        if (ordered.Any(static value => !double.IsFinite(value) || value < 0.0d))
        {
            throw new ArgumentException("Timing samples must be finite and non-negative.", nameof(elapsedMilliseconds));
        }

        double mean = ordered.Average();
        double variance = ordered.Average(value =>
        {
            double difference = value - mean;
            return difference * difference;
        });
        double standardDeviation = Math.Sqrt(variance);
        double median = ordered.Length % 2 == 0
            ? (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2.0d
            : ordered[ordered.Length / 2];

        return new CompilerPhase00RuntimeTimingDistributionV1(
            ordered.Length,
            ordered[0],
            median,
            ordered[^1],
            mean,
            standardDeviation,
            mean == 0.0d ? null : standardDeviation / mean);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject()
                             .Where(static property => !IsNondeterministic(property.Name))
                             .OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsNondeterministic(string propertyName) =>
        NondeterministicProperties.Contains(propertyName) ||
        propertyName.EndsWith("PerMillisecond", StringComparison.OrdinalIgnoreCase);
}
