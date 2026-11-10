using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace HybridCPU.Compiler.Core.IR.Telemetry;

/// <summary>
/// Attribution state of the exact source subject used for a benchmark record.
/// This is evidence classification, not release authorization.
/// </summary>
public enum CompilerSubjectDispositionV1 : byte
{
    Clean = 0,
    DirtyAttributed = 1,
    DirtyUnattributed = 2
}

/// <summary>
/// SHA-256 identity of one compiler-owned source file relative to the repository root.
/// </summary>
public sealed record CompilerSourceFileHashV1(
    string RelativePath,
    string Sha256,
    long LengthBytes)
{
    public static CompilerSourceFileHashV1 Create(
        string relativePath,
        ReadOnlySpan<byte> contents)
    {
        string normalizedPath = NormalizeRelativePath(relativePath);
        return new CompilerSourceFileHashV1(
            normalizedPath,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(contents)).ToLowerInvariant(),
            contents.Length);
    }

    internal static string NormalizeRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string normalized = relativePath.Replace('\\', '/');
        string[] segments = normalized.Split('/');
        if (normalized.Length == 0 ||
            normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Contains(':', StringComparison.Ordinal) ||
            segments.Any(static segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Source manifest paths must stay relative to the repository root.", nameof(relativePath));
        }

        return normalized;
    }
}

/// <summary>
/// Versioned attribution manifest for one deterministic compiler benchmark record.
/// </summary>
public sealed record CompilerBenchmarkManifestV1(
    string Schema,
    string BaseCommitSha,
    string Branch,
    CompilerSubjectDispositionV1 SubjectDisposition,
    string BuildConfiguration,
    int RuntimeContractVersion,
    string ToolVersion,
    string InputFingerprint,
    string ProfileFingerprint,
    string ModelFingerprint,
    string OptionsFingerprint,
    string SourceManifestFingerprint,
    string RunId,
    IReadOnlyList<CompilerSourceFileHashV1> SourceFiles)
{
    public const string SchemaName = "CompilerBenchmarkManifestV1";

    public byte[] ToDeterministicJsonBytes() =>
        JsonSerializer.SerializeToUtf8Bytes(this, CompilerScheduleMetricsJsonV1.Options);

    public string ToDeterministicJson() => Encoding.UTF8.GetString(ToDeterministicJsonBytes());
}

/// <summary>
/// Constructs canonical benchmark manifests from caller-captured Git and file-hash evidence.
/// It performs no Git, filesystem, runtime, or publication operation.
/// </summary>
public static class CompilerBenchmarkManifestBuilderV1
{
    public static CompilerBenchmarkManifestV1 Create(
        string baseCommitSha,
        string branch,
        CompilerSubjectDispositionV1 subjectDisposition,
        string buildConfiguration,
        int runtimeContractVersion,
        string toolVersion,
        CompilerScheduleMetricsV1 metrics,
        IReadOnlyList<CompilerSourceFileHashV1> sourceFiles)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ValidateRequiredText(baseCommitSha, nameof(baseCommitSha));
        ValidateRequiredText(branch, nameof(branch));
        ValidateRequiredText(buildConfiguration, nameof(buildConfiguration));
        ValidateRequiredText(toolVersion, nameof(toolVersion));
        if (!IsGitObjectId(baseCommitSha))
        {
            throw new ArgumentException("Base commit must be a 40- or 64-digit hexadecimal Git object ID.", nameof(baseCommitSha));
        }

        if (runtimeContractVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(runtimeContractVersion));
        }

        if (metrics.Schema != CompilerScheduleMetricsV1.SchemaName)
        {
            throw new ArgumentException($"Unsupported metrics schema '{metrics.Schema}'.", nameof(metrics));
        }

        CompilerSourceFileHashV1[] orderedFiles = sourceFiles
            .Select(ValidateAndNormalizeFile)
            .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (orderedFiles.Length == 0)
        {
            throw new ArgumentException("A benchmark manifest requires at least one exact source-file hash.", nameof(sourceFiles));
        }

        for (int index = 1; index < orderedFiles.Length; index++)
        {
            if (string.Equals(
                    orderedFiles[index - 1].RelativePath,
                    orderedFiles[index].RelativePath,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Duplicate source manifest path '{orderedFiles[index].RelativePath}'.",
                    nameof(sourceFiles));
            }
        }

        string sourceManifestFingerprint = ComputeSourceManifestFingerprint(
            baseCommitSha,
            branch,
            subjectDisposition,
            orderedFiles);
        string runId = ComputeRunId(
            sourceManifestFingerprint,
            buildConfiguration,
            runtimeContractVersion,
            toolVersion,
            metrics);

        return new CompilerBenchmarkManifestV1(
            CompilerBenchmarkManifestV1.SchemaName,
            baseCommitSha,
            branch,
            subjectDisposition,
            buildConfiguration,
            runtimeContractVersion,
            toolVersion,
            metrics.InputFingerprint,
            metrics.ProfileFingerprint,
            metrics.ModelFingerprint,
            metrics.OptionsFingerprint,
            sourceManifestFingerprint,
            runId,
            Array.AsReadOnly(orderedFiles));
    }

    internal static string ComputeRunId(
        string sourceManifestFingerprint,
        string buildConfiguration,
        int runtimeContractVersion,
        string toolVersion,
        CompilerScheduleMetricsV1 metrics) =>
        CompilerScheduleFingerprintV1.HashIdentity(string.Join(
            "|",
            CompilerBenchmarkManifestV1.SchemaName,
            sourceManifestFingerprint,
            buildConfiguration,
            runtimeContractVersion,
            toolVersion,
            metrics.InputFingerprint,
            metrics.ProfileFingerprint,
            metrics.ModelFingerprint,
            metrics.OptionsFingerprint));

    internal static CompilerSourceFileHashV1 ValidateAndNormalizeFile(CompilerSourceFileHashV1 file)
    {
        ArgumentNullException.ThrowIfNull(file);
        string normalizedPath = CompilerSourceFileHashV1.NormalizeRelativePath(file.RelativePath);
        if (file.LengthBytes < 0)
        {
            throw new ArgumentException($"Negative source length for '{normalizedPath}'.", nameof(file));
        }

        if (!IsSha256(file.Sha256))
        {
            throw new ArgumentException($"Invalid SHA-256 for '{normalizedPath}'.", nameof(file));
        }

        return file with
        {
            RelativePath = normalizedPath,
            Sha256 = file.Sha256.ToLowerInvariant()
        };
    }

    internal static string ComputeSourceManifestFingerprint(
        string baseCommitSha,
        string branch,
        CompilerSubjectDispositionV1 subjectDisposition,
        IReadOnlyList<CompilerSourceFileHashV1> files)
    {
        StringBuilder canonical = new StringBuilder();
        AppendCanonical(canonical, baseCommitSha);
        AppendCanonical(canonical, branch);
        AppendCanonical(canonical, ((int)subjectDisposition).ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (CompilerSourceFileHashV1 file in files)
        {
            AppendCanonical(canonical, file.RelativePath);
            AppendCanonical(canonical, file.Sha256);
            AppendCanonical(canonical, file.LengthBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return CompilerScheduleFingerprintV1.HashIdentity(canonical.ToString());
    }

    private static void AppendCanonical(StringBuilder builder, string value)
    {
        builder.Append(value.Length);
        builder.Append(':');
        builder.Append(value);
        builder.Append(';');
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool IsGitObjectId(string? value) =>
        value is { Length: 40 or 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static void ValidateRequiredText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Manifest identity fields cannot be empty.", parameterName);
        }
    }
}

/// <summary>
/// One attributable metrics record suitable for deterministic comparison.
/// </summary>
public sealed record CompilerBenchmarkRecordV1(
    CompilerBenchmarkManifestV1 Manifest,
    CompilerScheduleMetricsV1 Metrics);

public sealed record CompilerMetricsComparisonRequirementsV1(
    bool RequirePlacementPrunedCounter = false);

public sealed record CompilerMetricDeltaV1(
    string Metric,
    long Baseline,
    long Candidate,
    long AbsoluteDelta,
    decimal? RelativeDelta);

public sealed record CompilerMetricsFirstDivergenceV1(
    string Path,
    string BaselineValue,
    string CandidateValue);

/// <summary>
/// Result of an offline metrics comparison. Failure means evidence is not comparable; it never
/// changes compiler output and is not runtime admission or lifecycle authority.
/// </summary>
public sealed record CompilerMetricsComparisonV1(
    bool IsComparable,
    bool IsEquivalent,
    string? ComparisonFailureReason,
    CompilerMetricsFirstDivergenceV1? FirstDivergence,
    IReadOnlyList<CompilerMetricDeltaV1> Deltas);

/// <summary>
/// Deterministic Phase 00 baseline comparator with absolute/relative deltas and first divergence.
/// </summary>
public static class CompilerScheduleMetricsComparatorV1
{
    public static CompilerMetricsComparisonV1 Compare(
        CompilerBenchmarkRecordV1 baseline,
        CompilerBenchmarkRecordV1 candidate,
        CompilerMetricsComparisonRequirementsV1? requirements = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        requirements ??= new CompilerMetricsComparisonRequirementsV1();

        string? validationFailure = ValidateRecord(baseline, "baseline", requirements)
            ?? ValidateRecord(candidate, "candidate", requirements);
        if (validationFailure is not null)
        {
            return Failed(validationFailure);
        }

        CompilerMetricsFirstDivergenceV1? identityDivergence = CompareIdentity(baseline, candidate);
        if (identityDivergence is not null)
        {
            return new CompilerMetricsComparisonV1(
                IsComparable: false,
                IsEquivalent: false,
                "Input/profile/model/options identity mismatch.",
                identityDivergence,
                Array.Empty<CompilerMetricDeltaV1>());
        }

        IReadOnlyList<CompilerMetricDeltaV1> deltas = CreateDeltas(baseline.Metrics, candidate.Metrics);
        CompilerMetricsFirstDivergenceV1? firstDivergence = FindFirstMetricsDivergence(
            baseline.Metrics,
            candidate.Metrics);
        return new CompilerMetricsComparisonV1(
            IsComparable: true,
            IsEquivalent: firstDivergence is null,
            ComparisonFailureReason: null,
            firstDivergence,
            deltas);
    }

    private static string? ValidateRecord(
        CompilerBenchmarkRecordV1 record,
        string role,
        CompilerMetricsComparisonRequirementsV1 requirements)
    {
        if (record.Manifest is null || record.Metrics is null)
        {
            return $"{role}: manifest and metrics are required.";
        }

        if (record.Manifest.Schema != CompilerBenchmarkManifestV1.SchemaName)
        {
            return $"{role}: unsupported manifest schema '{record.Manifest.Schema}'.";
        }

        if (record.Metrics.Schema != CompilerScheduleMetricsV1.SchemaName)
        {
            return $"{role}: unsupported metrics schema '{record.Metrics.Schema}'.";
        }

        if (string.IsNullOrWhiteSpace(record.Manifest.SourceManifestFingerprint))
        {
            return $"{role}: source manifest fingerprint is missing.";
        }

        if (record.Manifest.SourceFiles is null || record.Manifest.SourceFiles.Count == 0)
        {
            return $"{role}: exact source-file hashes are missing.";
        }

        if (record.Manifest.SubjectDisposition == CompilerSubjectDispositionV1.DirtyUnattributed)
        {
            return $"{role}: dirty subject is unattributed.";
        }

        if (requirements.RequirePlacementPrunedCounter &&
            (record.Metrics.PlacementPrunedEvidenceQuality != CompilerMetricEvidenceQualityV1.Measured ||
             record.Metrics.PlacementPrunedCount is null))
        {
            return $"{role}: required placement-pruned counter is unavailable.";
        }

        CompilerSourceFileHashV1[] normalizedFiles;
        try
        {
            normalizedFiles = record.Manifest.SourceFiles
                .Select(CompilerBenchmarkManifestBuilderV1.ValidateAndNormalizeFile)
                .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
                .ToArray();
        }
        catch (ArgumentException exception)
        {
            return $"{role}: invalid source manifest: {exception.Message}";
        }

        if (normalizedFiles.Select(static file => file.RelativePath).Distinct(StringComparer.Ordinal).Count() !=
            normalizedFiles.Length)
        {
            return $"{role}: duplicate source manifest path.";
        }

        string expectedSourceFingerprint = CompilerBenchmarkManifestBuilderV1.ComputeSourceManifestFingerprint(
            record.Manifest.BaseCommitSha,
            record.Manifest.Branch,
            record.Manifest.SubjectDisposition,
            normalizedFiles);
        if (!string.Equals(
                expectedSourceFingerprint,
                record.Manifest.SourceManifestFingerprint,
                StringComparison.Ordinal))
        {
            return $"{role}: source manifest fingerprint does not match exact file hashes.";
        }

        if (!string.Equals(record.Manifest.InputFingerprint, record.Metrics.InputFingerprint, StringComparison.Ordinal) ||
            !string.Equals(record.Manifest.ProfileFingerprint, record.Metrics.ProfileFingerprint, StringComparison.Ordinal) ||
            !string.Equals(record.Manifest.ModelFingerprint, record.Metrics.ModelFingerprint, StringComparison.Ordinal) ||
            !string.Equals(record.Manifest.OptionsFingerprint, record.Metrics.OptionsFingerprint, StringComparison.Ordinal))
        {
            return $"{role}: manifest identity does not match metrics identity.";
        }

        string expectedRunId = CompilerBenchmarkManifestBuilderV1.ComputeRunId(
            record.Manifest.SourceManifestFingerprint,
            record.Manifest.BuildConfiguration,
            record.Manifest.RuntimeContractVersion,
            record.Manifest.ToolVersion,
            record.Metrics);
        if (!string.Equals(expectedRunId, record.Manifest.RunId, StringComparison.Ordinal))
        {
            return $"{role}: run ID does not match manifest identity.";
        }

        return null;
    }

    private static CompilerMetricsFirstDivergenceV1? CompareIdentity(
        CompilerBenchmarkRecordV1 baseline,
        CompilerBenchmarkRecordV1 candidate)
    {
        return FirstDifference("input_fingerprint", baseline.Metrics.InputFingerprint, candidate.Metrics.InputFingerprint)
            ?? FirstDifference("profile_fingerprint", baseline.Metrics.ProfileFingerprint, candidate.Metrics.ProfileFingerprint)
            ?? FirstDifference("model_fingerprint", baseline.Metrics.ModelFingerprint, candidate.Metrics.ModelFingerprint)
            ?? FirstDifference("options_fingerprint", baseline.Metrics.OptionsFingerprint, candidate.Metrics.OptionsFingerprint);
    }

    private static CompilerMetricsFirstDivergenceV1? FindFirstMetricsDivergence(
        CompilerScheduleMetricsV1 baseline,
        CompilerScheduleMetricsV1 candidate)
    {
        CompilerMetricsFirstDivergenceV1? divergence =
            FirstDifference("schedule_cycles", baseline.ScheduleCycles, candidate.ScheduleCycles)
            ?? FirstDifference("bundle_count", baseline.BundleCount, candidate.BundleCount)
            ?? FirstDifference("max_single_vt_width", baseline.MaxSingleVtWidth, candidate.MaxSingleVtWidth)
            ?? FirstDifference("max_packed_vt_width", baseline.MaxPackedVtWidth, candidate.MaxPackedVtWidth)
            ?? FirstDifference("max_ready_window_size", baseline.MaxReadyWindowSize, candidate.MaxReadyWindowSize);
        if (divergence is not null)
        {
            return divergence;
        }

        int commonBlockCount = Math.Min(baseline.Blocks.Count, candidate.Blocks.Count);
        for (int index = 0; index < commonBlockCount; index++)
        {
            CompilerScheduleBlockMetricsV1 left = baseline.Blocks[index];
            CompilerScheduleBlockMetricsV1 right = candidate.Blocks[index];
            divergence = FirstDifference($"blocks[{index}].block_id", left.BlockId, right.BlockId)
                ?? FirstDifference($"blocks[{index}].region_identity", left.RegionIdentity, right.RegionIdentity)
                ?? FirstDifference($"blocks[{index}].schedule_cycles", left.ScheduleCycles, right.ScheduleCycles)
                ?? FirstDifference($"blocks[{index}].bundle_count", left.BundleCount, right.BundleCount)
                ?? FirstDifference($"blocks[{index}].max_single_vt_width", left.MaxSingleVtWidth, right.MaxSingleVtWidth)
                ?? FirstDifference($"blocks[{index}].max_packed_vt_width", left.MaxPackedVtWidth, right.MaxPackedVtWidth)
                ?? FirstDifference($"blocks[{index}].max_ready_window_size", left.MaxReadyWindowSize, right.MaxReadyWindowSize);
            if (divergence is not null)
            {
                return divergence;
            }
        }

        return FirstDifference("blocks.count", baseline.Blocks.Count, candidate.Blocks.Count)
            ?? FirstDifference("scheduler_candidate_evaluated_count", baseline.SchedulerCandidateEvaluatedCount, candidate.SchedulerCandidateEvaluatedCount)
            ?? FirstDifference("scheduler_candidate_pruned_count", baseline.SchedulerCandidatePrunedCount, candidate.SchedulerCandidatePrunedCount)
            ?? FirstDifference("placement_evaluated_count", baseline.PlacementEvaluatedCount, candidate.PlacementEvaluatedCount)
            ?? FirstDifference("placement_pareto_optimal_count", baseline.PlacementParetoOptimalCount, candidate.PlacementParetoOptimalCount)
            ?? FirstDifference("placement_dominated_count", baseline.PlacementDominatedCount, candidate.PlacementDominatedCount)
            ?? FirstDifference("scheduler_cap_hit_count", baseline.SchedulerCapHitCount, candidate.SchedulerCapHitCount)
            ?? FirstDifference("placement_cap_hit_count", baseline.PlacementCapHitCount, candidate.PlacementCapHitCount)
            ?? FirstDifference("fallback_reasons", string.Join("|", baseline.FallbackReasons), string.Join("|", candidate.FallbackReasons))
            ?? FirstDifference("schedule_fingerprint", baseline.ScheduleFingerprint, candidate.ScheduleFingerprint)
            ?? FirstDifference("bundle_fingerprint", baseline.BundleFingerprint, candidate.BundleFingerprint);
    }

    private static IReadOnlyList<CompilerMetricDeltaV1> CreateDeltas(
        CompilerScheduleMetricsV1 baseline,
        CompilerScheduleMetricsV1 candidate)
    {
        return
        [
            CreateDelta("schedule_cycles", baseline.ScheduleCycles, candidate.ScheduleCycles),
            CreateDelta("bundle_count", baseline.BundleCount, candidate.BundleCount),
            CreateDelta("max_single_vt_width", baseline.MaxSingleVtWidth, candidate.MaxSingleVtWidth),
            CreateDelta("max_packed_vt_width", baseline.MaxPackedVtWidth, candidate.MaxPackedVtWidth),
            CreateDelta("max_ready_window_size", baseline.MaxReadyWindowSize, candidate.MaxReadyWindowSize),
            CreateDelta("scheduler_candidate_evaluated_count", baseline.SchedulerCandidateEvaluatedCount, candidate.SchedulerCandidateEvaluatedCount),
            CreateDelta("scheduler_candidate_pruned_count", baseline.SchedulerCandidatePrunedCount, candidate.SchedulerCandidatePrunedCount),
            CreateDelta("placement_evaluated_count", baseline.PlacementEvaluatedCount, candidate.PlacementEvaluatedCount),
            CreateDelta("placement_pareto_optimal_count", baseline.PlacementParetoOptimalCount, candidate.PlacementParetoOptimalCount),
            CreateDelta("placement_dominated_count", baseline.PlacementDominatedCount, candidate.PlacementDominatedCount)
        ];
    }

    private static CompilerMetricDeltaV1 CreateDelta(string metric, long baseline, long candidate)
    {
        long absoluteDelta = candidate - baseline;
        decimal? relativeDelta = baseline == 0
            ? null
            : (decimal)absoluteDelta / baseline;
        return new CompilerMetricDeltaV1(metric, baseline, candidate, absoluteDelta, relativeDelta);
    }

    private static CompilerMetricsComparisonV1 Failed(string reason) =>
        new(false, false, reason, null, Array.Empty<CompilerMetricDeltaV1>());

    private static CompilerMetricsFirstDivergenceV1? FirstDifference<T>(
        string path,
        T baseline,
        T candidate)
    {
        return EqualityComparer<T>.Default.Equals(baseline, candidate)
            ? null
            : new CompilerMetricsFirstDivergenceV1(
                path,
                baseline?.ToString() ?? "null",
                candidate?.ToString() ?? "null");
    }
}
