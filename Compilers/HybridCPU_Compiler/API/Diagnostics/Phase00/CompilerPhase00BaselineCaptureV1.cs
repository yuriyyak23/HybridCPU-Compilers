using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace HybridCPU.Compiler.Core.IR.Telemetry;

public sealed record CompilerPhase00HistoricalDeltaV1(
    CompilerMetricEvidenceQualityV1 HistoricalScheduleCyclesEvidenceQuality,
    int? ScheduleCyclesDelta,
    int CycleGroupCountDelta,
    int BundleCountDelta,
    decimal AverageWidthDelta);

public sealed record CompilerPhase00BaselineProfileV1(
    string ProfileId,
    string WorkloadShape,
    int ReferenceSliceIterations,
    int CurrentCycleGroupCount,
    decimal CurrentAverageWidth,
    CompilerHistoricalScheduleBaselineV1 HistoricalBaseline,
    CompilerPhase00HistoricalDeltaV1 HistoricalDelta,
    CompilerBenchmarkRecordV1 CurrentRecord);

/// <summary>
/// Deterministic compiler-only Phase 00 baseline. Resource timings are kept in a separate report
/// because wall-clock and managed-memory observations cannot be byte deterministic.
/// </summary>
public sealed record CompilerPhase00BaselineReportV1(
    string Schema,
    IReadOnlyList<CompilerPhase00BaselineProfileV1> Profiles)
{
    public const string SchemaName = "CompilerPhase00BaselineReportV1";

    public byte[] ToDeterministicJsonBytes() =>
        JsonSerializer.SerializeToUtf8Bytes(this, CompilerScheduleMetricsJsonV1.Options);

    public string ToDeterministicJson() => Encoding.UTF8.GetString(ToDeterministicJsonBytes());
}

public sealed record CompilerPhase00ResourceSampleV1(
    string ProfileId,
    CompilerCompileResourceTelemetryV1 Telemetry);

public sealed record CompilerPhase00ResourceReportV1(
    string Schema,
    IReadOnlyList<CompilerPhase00ResourceSampleV1> Samples)
{
    public const string SchemaName = "CompilerPhase00ResourceReportV1";

    public byte[] ToJsonBytes() =>
        JsonSerializer.SerializeToUtf8Bytes(this, CompilerScheduleMetricsJsonV1.Options);
}

public sealed record CompilerPhase00BaselineCaptureResultV1(
    CompilerPhase00BaselineReportV1 DeterministicReport,
    CompilerPhase00ResourceReportV1 ResourceReport);

/// <summary>
/// Runs only the canonical compiler over the frozen representative carriers. It does not emit to
/// memory or invoke runtime admission, execution, publication, FSP/replay, commit or retire paths.
/// </summary>
public static class CompilerPhase00BaselineCaptureV1
{
    public static CompilerPhase00BaselineCaptureResultV1 Capture(
        string baseCommitSha,
        string branch,
        CompilerSubjectDispositionV1 subjectDisposition,
        string buildConfiguration,
        int runtimeContractVersion,
        string toolVersion,
        IReadOnlyList<CompilerSourceFileHashV1> sourceFiles)
    {
        List<CompilerPhase00BaselineProfileV1> profiles = new List<CompilerPhase00BaselineProfileV1>();
        List<CompilerPhase00ResourceSampleV1> resources = new List<CompilerPhase00ResourceSampleV1>();

        foreach (CompilerRepresentativeInputDescriptorV1 descriptor in
                 CompilerPhase00RepresentativeInputCorpusV1.Descriptors)
        {
            CompilerRepresentativeInputInstanceV1 input =
                CompilerPhase00RepresentativeInputCorpusV1.Create(descriptor.Id);
            HybridCpuCompilationMetricsResultV1 result =
                HybridCpuCanonicalCompiler.CompileProgramWithMetrics(
                    0,
                    input.Instructions,
                    new CompilerScheduleMetricsRequestV1(ProfileIdentity: descriptor.Id),
                    bundleAnnotations: input.BundleAnnotations);
            CompilerScheduleMetricsV1 metrics = result.ScheduleMetrics ??
                throw new InvalidOperationException($"Profile '{descriptor.Id}' did not produce schedule metrics.");
            CompilerCompileResourceTelemetryV1 resource = result.ResourceTelemetry ??
                throw new InvalidOperationException($"Profile '{descriptor.Id}' did not produce resource telemetry.");
            int cycleGroupCount = result.CompiledProgram.ProgramSchedule.BlockSchedules
                .Sum(static block => block.CycleGroups.Count);
            int issuedInstructionCount = result.CompiledProgram.BundleLayout.BlockResults
                .SelectMany(static block => block.Bundles)
                .Sum(static bundle => bundle.IssuedInstructionCount);
            decimal averageWidth = decimal.Round(
                (decimal)issuedInstructionCount / metrics.BundleCount,
                4,
                MidpointRounding.ToEven);
            CompilerBenchmarkManifestV1 manifest = CompilerBenchmarkManifestBuilderV1.Create(
                baseCommitSha,
                branch,
                subjectDisposition,
                buildConfiguration,
                runtimeContractVersion,
                toolVersion,
                metrics,
                sourceFiles);

            profiles.Add(new CompilerPhase00BaselineProfileV1(
                descriptor.Id,
                descriptor.WorkloadShape,
                descriptor.ReferenceSliceIterations,
                cycleGroupCount,
                averageWidth,
                descriptor.HistoricalBaseline,
                new CompilerPhase00HistoricalDeltaV1(
                    CompilerMetricEvidenceQualityV1.Unavailable,
                    ScheduleCyclesDelta: null,
                    cycleGroupCount - descriptor.HistoricalBaseline.CycleGroupCount,
                    metrics.BundleCount - descriptor.HistoricalBaseline.BundleCount,
                    averageWidth - descriptor.HistoricalBaseline.AverageWidth),
                new CompilerBenchmarkRecordV1(manifest, metrics)));
            resources.Add(new CompilerPhase00ResourceSampleV1(descriptor.Id, resource));
        }

        return new CompilerPhase00BaselineCaptureResultV1(
            new CompilerPhase00BaselineReportV1(
                CompilerPhase00BaselineReportV1.SchemaName,
                profiles.AsReadOnly()),
            new CompilerPhase00ResourceReportV1(
                CompilerPhase00ResourceReportV1.SchemaName,
                resources.AsReadOnly()));
    }
}
