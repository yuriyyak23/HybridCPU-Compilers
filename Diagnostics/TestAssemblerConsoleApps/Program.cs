using System.Diagnostics;
using System.Globalization;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal static class Program
{
    public static int Main(string[] args)
    {
        CompatAssemblyResolver.EnsureRegistered();

        if (TryParseWorkerInvocation(args, out string workerProfileId, out string artifactDirectory))
        {
            return RunWorker(workerProfileId, artifactDirectory);
        }

        try
        {
            PrintBanner();

            if (args.Length > 0 && IsHelpCommand(args[0]))
            {
                PrintUsage();
                return 0;
            }

            ParentCommandOptions options = ParseParentCommandOptions(args);
            if (options.Command is not null && IsHelpCommand(options.Command))
            {
                PrintUsage();
                return 0;
            }

            options = ResolveIterationBudget(options);
            options = ResolveTelemetryLogMode(options);
            FrontendMode frontendMode = ParseFrontend(args);
            int commandExitCode = options.Command is null
                ? RunDefaultMatrix(frontendMode, options)
                : ExecuteParentCommand(options.Command, frontendMode, options);
            Console.WriteLine("Done.");
            return commandExitCode;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int ExecuteParentCommand(string commandArg, FrontendMode frontendMode, ParentCommandOptions options)
    {
        string normalized = NormalizeCommand(commandArg);

        if (IsMatrixSmokeCommand(normalized))
        {
            return RunNamedMatrix("matrix-smoke", "Matrix Smoke", DiagnosticProfileCatalog.MatrixSmoke, frontendMode, options);
        }

        if (IsMatrixRuntimeCommand(normalized))
        {
            return RunNamedMatrix("matrix-runtime", "Matrix Runtime", DiagnosticProfileCatalog.MatrixRuntime, frontendMode, options);
        }

        if (IsMatrixMemoryCommand(normalized))
        {
            return RunNamedMatrix("matrix-memory", "Matrix Memory", DiagnosticProfileCatalog.MatrixMemory, frontendMode, options);
        }

        if (IsMatrixFullCommand(normalized))
        {
            return RunNamedMatrix("matrix-full", "Matrix Full", DiagnosticProfileCatalog.MatrixFull, frontendMode, options);
        }

        if (IsMatrixWideCommand(normalized))
        {
            return RunNamedMatrix("matrix-wide", "Matrix Wide", DiagnosticProfileCatalog.MatrixWide, frontendMode, options);
        }

        if (IsMatrixSpecCommand(normalized))
        {
            return RunNamedMatrix("matrix-spec", "Matrix SPEC-like", DiagnosticProfileCatalog.MatrixSpec, frontendMode, options);
        }

        if (IsWhiteBookSmokeCommand(normalized))
        {
            return RunNamedMatrix("whitebook-smoke", "Stream WhiteBook Smoke", DiagnosticProfileCatalog.WhiteBookSmoke, frontendMode, options);
        }

        if (IsWhiteBookFullCommand(normalized))
        {
            return RunNamedMatrix("whitebook-full", "Stream WhiteBook Full", DiagnosticProfileCatalog.WhiteBookFull, frontendMode, options);
        }

        if (IsBaselineVsFspComparisonCommand(normalized))
        {
            return RunBaselineVsFspComparison(frontendMode, options);
        }

        if (IsBankDistributionPairLongCommand(normalized))
        {
            return RunBankDistributionPair(frontendMode, options, useLongProfiles: true);
        }

        if (IsBankDistributionPairCommand(normalized))
        {
            return RunBankDistributionPair(frontendMode, options, useLongProfiles: false);
        }

        DiagnosticRunProfile profile = ApplyOverrides(
            DiagnosticProfileCatalog.Resolve(commandArg) with
            {
                FrontendMode = frontendMode
            },
            options);

        return RunSingleProfile(profile);
    }

    private static int RunDefaultMatrix(FrontendMode frontendMode, ParentCommandOptions options)
    {
        Console.WriteLine("=== Default SPEC-like diagnostic matrix ===");
        IReadOnlyList<DiagnosticRunProfile> profiles = ApplyOverrides(
            DiagnosticProfileCatalog.DefaultMatrix
                .Select(profile => profile with { FrontendMode = frontendMode })
                .ToArray(),
            options);

        DiagnosticBatchRunResult batch = ExecuteBatchRun(
            commandId: "matrix",
            displayName: "Default SPEC-like diagnostic matrix",
            profiles,
            finalizeArtifacts: null);

        PrintBatchSummary(batch);
        return batch.Status == DiagnosticRunStatus.Succeeded ? 0 : 1;
    }

    private static int RunNamedMatrix(
        string commandId,
        string displayName,
        IReadOnlyList<DiagnosticRunProfile> profiles,
        FrontendMode frontendMode,
        ParentCommandOptions options)
    {
        Console.WriteLine($"=== {displayName} ===");
        IReadOnlyList<DiagnosticRunProfile> effectiveProfiles = ApplyOverrides(
            profiles.Select(profile => profile with { FrontendMode = frontendMode }).ToArray(),
            options);

        DiagnosticBatchRunResult batch = ExecuteBatchRun(commandId, displayName, effectiveProfiles, finalizeArtifacts: null);
        PrintBatchSummary(batch);
        return batch.Status == DiagnosticRunStatus.Succeeded ? 0 : 1;
    }

    private static int RunSingleProfile(DiagnosticRunProfile profile)
    {
        Console.WriteLine($"=== {profile.DisplayName} ===");

        DiagnosticRunResult result = new DiagnosticRunController().Execute(profile);
        ReplayCapturedConsole(result);
        PrintSingleRunSummary(result);
        PrintStreamVectorFinalSummaryIfAvailable(result);
        PrintMatrixTileFinalSummaryIfAvailable(result);
        return result.Status == DiagnosticRunStatus.Succeeded ? 0 : 1;
    }

    private static int RunBankDistributionPair(FrontendMode frontendMode, ParentCommandOptions options, bool useLongProfiles)
    {
        Console.WriteLine("=== Bank distribution pair ===");
        string leftProfileId = useLongProfiles ? "lk-long" : "lk";
        string rightProfileId = useLongProfiles ? "bnmcz-long" : "bnmcz";
        IReadOnlyList<DiagnosticRunProfile> profiles =
        [
            ApplyOverrides(DiagnosticProfileCatalog.Resolve(leftProfileId) with { FrontendMode = frontendMode }, options),
            ApplyOverrides(DiagnosticProfileCatalog.Resolve(rightProfileId) with { FrontendMode = frontendMode }, options)
        ];

        DiagnosticBatchRunResult batch = ExecuteBatchRun(
            commandId: useLongProfiles ? "banks-long" : "banks",
            displayName: useLongProfiles ? "Bank distribution pair (Long Budget)" : "Bank distribution pair",
            profiles,
            finalizeArtifacts: static (writer, generatedFiles, results) =>
            {
                if (results.Count < 2 ||
                    results[0].Metrics is not { } latencyHidingMetrics ||
                    results[1].Metrics is not { } bankDistributedMetrics)
                {
                    return null;
                }

                BankDistributionComparisonReport comparison = new BankDistributionComparisonReport(
                    ComparisonId: "banks",
                    LatencyHidingArtifactDirectory: results[0].ArtifactDirectory,
                    BankDistributedArtifactDirectory: results[1].ArtifactDirectory,
                    LatencyHidingStatus: results[0].Status.ToString(),
                    BankDistributedStatus: results[1].Status.ToString(),
                    IpcDelta: bankDistributedMetrics.Ipc - latencyHidingMetrics.Ipc,
                    CycleDelta: (long)bankDistributedMetrics.CycleCount - (long)latencyHidingMetrics.CycleCount,
                    PartialWidthIssueDelta: (long)bankDistributedMetrics.PartialWidthIssueCount - (long)latencyHidingMetrics.PartialWidthIssueCount,
                    RetiredPhysicalLanesPerRetireCycleDelta: bankDistributedMetrics.RetiredPhysicalLanesPerRetireCycle - latencyHidingMetrics.RetiredPhysicalLanesPerRetireCycle,
                    BytesTransferredDelta: (long)bankDistributedMetrics.BytesTransferred - (long)latencyHidingMetrics.BytesTransferred);

                writer.WriteJson("bank_distribution_comparison.json", comparison);
                generatedFiles["comparison"] = "bank_distribution_comparison.json";
                return $"Bank delta captured for {results[0].Profile.Id} vs {results[1].Profile.Id}.";
            });

        PrintBankBatchSummary(batch);
        return batch.Status == DiagnosticRunStatus.Succeeded ? 0 : 1;
    }

    private static int RunBaselineVsFspComparison(FrontendMode frontendMode, ParentCommandOptions options)
    {
        Console.WriteLine("=== Baseline vs FSP comparison ===");
        IReadOnlyList<DiagnosticRunProfile> profiles =
        [
            ApplyOverrides(DiagnosticProfileCatalog.Resolve("alu") with { FrontendMode = frontendMode }, options),
            ApplyOverrides(DiagnosticProfileCatalog.Resolve("vt") with { FrontendMode = frontendMode }, options)
        ];

        DiagnosticBatchRunResult batch = ExecuteBatchRun(
            commandId: "compare-baseline-fsp",
            displayName: "Baseline vs FSP comparison",
            profiles,
            finalizeArtifacts: static (writer, generatedFiles, results) =>
            {
                if (results.Count < 2 ||
                    results[0].Metrics is not { } baselineMetrics ||
                    results[1].Metrics is not { } fspMetrics)
                {
                    return null;
                }

                SimpleAsmMetricComparisonReport comparison = new SimpleAsmMetricComparisonReport(
                    ComparisonId: "compare-baseline-fsp",
                    LeftProfileId: results[0].Profile.Id,
                    RightProfileId: results[1].Profile.Id,
                    LeftArtifactDirectory: results[0].ArtifactDirectory,
                    RightArtifactDirectory: results[1].ArtifactDirectory,
                    LeftStatus: results[0].Status.ToString(),
                    RightStatus: results[1].Status.ToString(),
                    IpcDelta: fspMetrics.Ipc - baselineMetrics.Ipc,
                    CycleDelta: (long)fspMetrics.CycleCount - (long)baselineMetrics.CycleCount,
                    PartialWidthIssueDelta: (long)fspMetrics.PartialWidthIssueCount - (long)baselineMetrics.PartialWidthIssueCount,
                    RetiredPhysicalLanesPerRetireCycleDelta: fspMetrics.RetiredPhysicalLanesPerRetireCycle - baselineMetrics.RetiredPhysicalLanesPerRetireCycle,
                    BytesTransferredDelta: (long)fspMetrics.BytesTransferred - (long)baselineMetrics.BytesTransferred);

                writer.WriteJson("compare_baseline_fsp.json", comparison);
                generatedFiles["comparison"] = "compare_baseline_fsp.json";
                return $"FSP delta captured for {results[0].Profile.Id} vs {results[1].Profile.Id}.";
            });

        PrintBaselineVsFspSummary(batch);
        return batch.Status == DiagnosticRunStatus.Succeeded ? 0 : 1;
    }

    private static DiagnosticBatchRunResult ExecuteBatchRun(
        string commandId,
        string displayName,
        IReadOnlyList<DiagnosticRunProfile> profiles,
        Func<DiagnosticArtifactWriter, Dictionary<string, string>, IReadOnlyList<DiagnosticRunResult>, string?>? finalizeArtifacts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(profiles);

        DiagnosticRunController controller = new DiagnosticRunController();
        string artifactDirectory = controller.CreateArtifactDirectory(commandId);
        DiagnosticArtifactWriter writer = new DiagnosticArtifactWriter(artifactDirectory);
        writer.EnsureDirectory();
        writer.WriteJson("profiles.json", profiles);

        Dictionary<string, string> childArtifacts = profiles.ToDictionary(profile => profile.Id, profile => profile.Id, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> generatedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["profiles"] = "profiles.json"
        };

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        writer.WriteManifest(CreateBatchManifest(
            commandId,
            displayName,
            artifactDirectory,
            status: DiagnosticRunStatus.Running,
            frontendProfile: profiles.Count > 0 ? profiles[0].FrontendMode.ToString() : null,
            shadowFrontendMode: profiles.Count > 0 ? profiles[0].ShadowFrontendMode.ToString() : null,
            startedUtc,
            finishedUtc: null,
            elapsed: null,
            exitCode: null,
            failureMessage: null,
            childArtifacts,
            generatedFiles));

        Stopwatch stopwatch = Stopwatch.StartNew();
        List<DiagnosticRunResult> results = new List<DiagnosticRunResult>(profiles.Count);

        foreach (DiagnosticRunProfile profile in profiles)
        {
            Console.WriteLine($"--- Running {profile.Id} [{profile.FrontendMode}] ---");
            DiagnosticRunResult result = controller.Execute(profile, Path.Combine(artifactDirectory, profile.Id));
            results.Add(result);
            ReplayCapturedConsole(result);
            PrintSingleRunSummary(result);
            Console.WriteLine();
        }

        stopwatch.Stop();

        writer.WriteJson("results.json", results);
        generatedFiles["results"] = "results.json";

        string? finalizeMessage = finalizeArtifacts?.Invoke(writer, generatedFiles, results);
        DiagnosticRunStatus aggregateStatus = AggregateStatus(results.Select(result => result.Status));
        string? failureMessage = finalizeMessage;
        if (aggregateStatus != DiagnosticRunStatus.Succeeded)
        {
            failureMessage = $"{results.Count(result => result.Status != DiagnosticRunStatus.Succeeded)} of {results.Count} child runs did not succeed.";
        }

        writer.WriteManifest(CreateBatchManifest(
            commandId,
            displayName,
            artifactDirectory,
            aggregateStatus,
            frontendProfile: profiles.Count > 0 ? profiles[0].FrontendMode.ToString() : null,
            shadowFrontendMode: profiles.Count > 0 ? profiles[0].ShadowFrontendMode.ToString() : null,
            startedUtc,
            finishedUtc: DateTimeOffset.UtcNow,
            elapsed: stopwatch.Elapsed,
            exitCode: aggregateStatus == DiagnosticRunStatus.Succeeded ? 0 : 1,
            failureMessage,
            childArtifacts,
            generatedFiles));

        return new DiagnosticBatchRunResult(commandId, displayName, artifactDirectory, aggregateStatus, results, failureMessage);
    }

    private static int RunWorker(string profileId, string artifactDirectory)
    {
        DiagnosticArtifactWriter writer = new DiagnosticArtifactWriter(artifactDirectory);
        writer.EnsureDirectory();

        DiagnosticRunProfile profile = writer.TryReadJson<DiagnosticRunProfile>("profile.json")
            ?? DiagnosticProfileCatalog.Resolve(profileId);
        writer.WriteJson("profile.json", profile);
        DiagnosticProgressArtifactSink progressSink = new DiagnosticProgressArtifactSink(artifactDirectory, profile.TelemetryLogMode);

        Dictionary<string, string> generatedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["profile"] = "profile.json",
            ["stdout"] = "stdout.log",
            ["stderr"] = "stderr.log"
        };

        DiagnosticArtifactManifest existingManifest = writer.TryReadManifest() ?? CreateSingleManifest(
            profile,
            artifactDirectory,
            status: DiagnosticRunStatus.Pending,
            startedUtc: null,
            finishedUtc: null,
            elapsed: null,
            processId: null,
            exitCode: null,
            failureMessage: null,
            generatedFiles);

        DateTimeOffset startedUtc = existingManifest.StartedUtc ?? DateTimeOffset.UtcNow;
        writer.WriteManifest(CreateSingleManifest(
            profile,
            artifactDirectory,
            status: DiagnosticRunStatus.Running,
            startedUtc,
            finishedUtc: null,
            elapsed: null,
            processId: Environment.ProcessId,
            exitCode: null,
            failureMessage: null,
            generatedFiles));

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            DiagnosticShadowFrontend.BeginWorker(profile.ShadowFrontendMode);
            generatedFiles["shadow_frontend"] = DiagnosticShadowFrontend.ArtifactFileName;
            DiagnosticRunStatus status;
            string? failureMessage = null;

            try
            {
                switch (profile.WorkloadKind)
                {
                    case DiagnosticWorkloadKind.SimpleAsmMode:
                        if (profile.SimpleAsmMode is null)
                        {
                            throw new InvalidOperationException($"Profile '{profile.Id}' does not define a simple-ASM mode.");
                        }

                        SimpleAsmAppMetrics metrics = RunMode(
                            profile.SimpleAsmMode.Value,
                            profile.FrontendMode,
                            profile.WorkloadIterations,
                            profile,
                            progressSink);
                        writer.WriteJson("metrics.json", metrics);
                        generatedFiles["metrics"] = "metrics.json";
                        TimingMemoryReport timingMemoryReport = TimingMemoryReport.Create(metrics);
                        writer.WriteJson(TimingMemoryReport.ArtifactFileName, timingMemoryReport);
                        writer.WriteJson(
                            TimingMemoryReport.LegacyArtifactFileName,
                            timingMemoryReport.AsLegacyCompatibilityProjection());
                        generatedFiles[TimingMemoryReport.ManifestKey] = TimingMemoryReport.ArtifactFileName;
                        generatedFiles[TimingMemoryReport.LegacyManifestKey] = TimingMemoryReport.LegacyArtifactFileName;
                        status = string.IsNullOrWhiteSpace(metrics.FailureMessage)
                            ? DiagnosticRunStatus.Succeeded
                            : DiagnosticRunStatus.Failed;
                        failureMessage = string.IsNullOrWhiteSpace(metrics.FailureMessage)
                            ? null
                            : metrics.FailureMessage;
                        break;

                    case DiagnosticWorkloadKind.ReplayPhasePair:
                        ReplayPhaseBenchmarkPairReport replayReport = ExecuteReplayPhasePairWorkload(profile.WorkloadIterations);
                        writer.WriteJson("replay_report.json", replayReport);
                        generatedFiles["replay_report"] = "replay_report.json";
                        status = DiagnosticRunStatus.Succeeded;
                        break;

                    case DiagnosticWorkloadKind.SafetyVerifierNegativeControls:
                        SafetyVerifierNegativeControlsReport safetyReport = ExecuteSafetyVerifierNegativeControlsWorkload();
                        writer.WriteJson("safety_verifier_negative_controls.json", safetyReport);
                        generatedFiles["safety_verifier_negative_controls"] = "safety_verifier_negative_controls.json";
                        status = safetyReport.Succeeded ? DiagnosticRunStatus.Succeeded : DiagnosticRunStatus.Failed;
                        failureMessage = safetyReport.Succeeded ? null : "One or more SafetyVerifier negative controls did not reject as expected.";
                        break;

                    case DiagnosticWorkloadKind.ReplayReuseDiagnostics:
                        ReplayReuseDiagnosticsReport replayReuseReport = ExecuteReplayReuseDiagnosticsWorkload(profile.WorkloadIterations);
                        writer.WriteJson("replay_reuse_report.json", replayReuseReport);
                        generatedFiles["replay_reuse_report"] = "replay_reuse_report.json";
                        status = replayReuseReport.Succeeded ? DiagnosticRunStatus.Succeeded : DiagnosticRunStatus.Failed;
                        failureMessage = replayReuseReport.Succeeded ? null : "Replay reuse diagnostics did not produce the required lookup/hit/miss/invalidation evidence.";
                        break;

                    case DiagnosticWorkloadKind.AssistantDecisionMatrix:
                        AssistantDecisionMatrixReport assistantReport = ExecuteAssistantDecisionMatrixWorkload();
                        writer.WriteJson("assistant_matrix_report.json", assistantReport);
                        generatedFiles["assistant_matrix_report"] = "assistant_matrix_report.json";
                        status = assistantReport.Succeeded ? DiagnosticRunStatus.Succeeded : DiagnosticRunStatus.Failed;
                        failureMessage = assistantReport.Succeeded ? null : "Assistant decision matrix did not match expected decisions.";
                        break;

                    case DiagnosticWorkloadKind.StreamVectorSpecSuite:
                        StreamVectorSpecSuiteReport streamVectorReport =
                            ExecuteStreamVectorSpecSuiteWorkload(profile.WorkloadIterations);
                        writer.WriteJson("stream_vector_spec_report.json", streamVectorReport);
                        generatedFiles["stream_vector_spec_report"] = "stream_vector_spec_report.json";
                        status = streamVectorReport.Succeeded ? DiagnosticRunStatus.Succeeded : DiagnosticRunStatus.Failed;
                        failureMessage = streamVectorReport.Succeeded
                            ? null
                            : "One or more Stream/Vector SPEC-like scenarios failed correctness validation.";
                        break;

                    case DiagnosticWorkloadKind.MatrixTileSpecSuite:
                        MatrixTileSpecSuiteReport matrixTileReport =
                            ExecuteMatrixTileSpecSuiteWorkload(profile.WorkloadIterations);
                        writer.WriteJson("matrix_tile_spec_report.json", matrixTileReport);
                        generatedFiles["matrix_tile_spec_report"] = "matrix_tile_spec_report.json";
                        status = matrixTileReport.Succeeded ? DiagnosticRunStatus.Succeeded : DiagnosticRunStatus.Failed;
                        failureMessage = matrixTileReport.Succeeded
                            ? null
                            : "One or more MatrixTile SPEC-like pressure scenarios failed runtime validation.";
                        break;

                    case DiagnosticWorkloadKind.WhiteBookContractDiagnostics:
                        WhiteBookContractDiagnosticsReport whiteBookReport =
                            ExecuteWhiteBookContractDiagnosticsWorkload(profile.WorkloadIterations);
                        writer.WriteJson("whitebook_contract_report.json", whiteBookReport);
                        generatedFiles["whitebook_contract_report"] = "whitebook_contract_report.json";
                        status = whiteBookReport.Succeeded ? DiagnosticRunStatus.Succeeded : DiagnosticRunStatus.Failed;
                        failureMessage = whiteBookReport.Succeeded
                            ? null
                            : "One or more Stream WhiteBook current/future boundary probes failed.";
                        break;

                    default:
                        throw new NotSupportedException($"Unhandled workload kind '{profile.WorkloadKind}'.");
                }
            }
            finally
            {
                DiagnosticShadowFrontend.WriteReport(writer);
            }

            stopwatch.Stop();

            int exitCode = status == DiagnosticRunStatus.Succeeded ? 0 : 1;
            writer.WriteManifest(CreateSingleManifest(
                profile,
                artifactDirectory,
                status,
                startedUtc,
                finishedUtc: DateTimeOffset.UtcNow,
                elapsed: stopwatch.Elapsed,
                processId: Environment.ProcessId,
                exitCode,
                failureMessage,
                generatedFiles));

            return exitCode;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            writer.WriteManifest(CreateSingleManifest(
                profile,
                artifactDirectory,
                status: DiagnosticRunStatus.Failed,
                startedUtc,
                finishedUtc: DateTimeOffset.UtcNow,
                elapsed: stopwatch.Elapsed,
                processId: Environment.ProcessId,
                exitCode: 1,
                failureMessage: ex.Message,
                generatedFiles));

            Console.Error.WriteLine($"Fatal worker error: {ex.Message}");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static SimpleAsmAppMetrics RunMode(SimpleAsmAppMode mode)
    {
        return RunMode(
            mode,
            FrontendMode.NativeVLIW,
            DiagnosticRunProfile.DefaultWorkloadIterations,
            profile: null,
            progressSink: null);
    }

    private static SimpleAsmAppMetrics RunMode(SimpleAsmAppMode mode, FrontendMode frontendMode)
    {
        return RunMode(
            mode,
            frontendMode,
            DiagnosticRunProfile.DefaultWorkloadIterations,
            profile: null,
            progressSink: null);
    }

    private static SimpleAsmAppMetrics RunMode(
        SimpleAsmAppMode mode,
        FrontendMode frontendMode,
        ulong workloadIterations,
        DiagnosticRunProfile? profile,
        DiagnosticProgressArtifactSink? progressSink)
    {
        Console.WriteLine($">>> Starting mode: {mode} [{frontendMode}]");
        Console.WriteLine($"SPEC-like iterations: {workloadIterations:N0}");
        Console.Out.Flush();

        Stopwatch stopwatch = Stopwatch.StartNew();
        SimpleAsmApp app = new();
        if (profile is not null && progressSink is not null)
        {
            app.AttachProgressSink(profile, progressSink);
        }

        SimpleAsmAppMetrics metrics = app.ExecuteMeasuredProgram(mode, frontendMode, workloadIterations);
        stopwatch.Stop();

        Console.WriteLine($"Mode: {mode}");
        Console.WriteLine("Validation surface: primary");
        Console.WriteLine($"Frontend profile: {metrics.FrontendProfile}");
        Console.WriteLine($"Program variant: {metrics.ProgramVariant}");
        Console.WriteLine($"Frontend supported: {metrics.FrontendSupported}");
        Console.WriteLine($"Elapsed: {stopwatch.Elapsed}");
        Console.WriteLine($"Workload shape: {metrics.WorkloadShape}");
        if (metrics.UsesMultipleSlices)
        {
            Console.WriteLine($"Reference slice iterations: {metrics.ReferenceSliceIterations:N0}");
            Console.WriteLine($"Slice executions: {metrics.SliceExecutionCount:N0}");
        }
        Console.WriteLine($"Reference slice instructions: {metrics.LoopBodyInstructionCount}");
        Console.WriteLine($"Aggregate retirement target: {metrics.DynamicRetirementTarget}");
        Console.WriteLine("Diagnostics run completed.");
        Console.WriteLine($"IPC (retire-normalized): {metrics.RetireIpc:F4}");
        Console.WriteLine($"Raw cycle IPC: {metrics.Ipc:F4}");
        Console.WriteLine($"Instructions retired: {metrics.InstructionsRetired}");
        Console.WriteLine($"Cycle count: {metrics.CycleCount}");
        Console.WriteLine($"Pipeline stalls: {metrics.StallCycles}");
        Console.WriteLine($"Active cycles: {metrics.ActiveCycles}");
        Console.WriteLine($"Stall share: {metrics.StallShare:P2}");
        Console.WriteLine($"Effective issue width: {metrics.EffectiveIssueWidth:F4}");
        Console.WriteLine($"Data hazards: {metrics.DataHazards}");
        Console.WriteLine($"Memory stalls: {metrics.MemoryStalls}");
        Console.WriteLine($"Load-use bubbles: {metrics.LoadUseBubbles}");
        Console.WriteLine($"WAW hazards: {metrics.WAWHazards}");
        Console.WriteLine($"Control hazards: {metrics.ControlHazards}");
        Console.WriteLine($"Branch mispredicts: {metrics.BranchMispredicts}");
        Console.WriteLine($"Frontend stalls: {metrics.FrontendStalls}");
        Console.WriteLine($"Scalar issue width [0]: {metrics.ScalarIssueWidth0Cycles}");
        Console.WriteLine($"Scalar issue width [1]: {metrics.ScalarIssueWidth1Cycles}");
        Console.WriteLine($"Scalar issue width [2]: {metrics.ScalarIssueWidth2Cycles}");
        Console.WriteLine($"Scalar issue width [3]: {metrics.ScalarIssueWidth3Cycles}");
        Console.WriteLine($"Scalar issue width [4]: {metrics.ScalarIssueWidth4Cycles}");
        Console.WriteLine($"Total bursts: {metrics.TotalBursts}");
        Console.WriteLine($"Bytes transferred: {metrics.BytesTransferred}");
        PrintTimingAndMemoryInterpretation(metrics);
        Console.WriteLine($"NOPs avoided: {metrics.NopAvoided}");
        Console.WriteLine($"NOPs due to no class capacity: {metrics.NopDueToNoClassCapacity}");
        Console.WriteLine($"NOPs due to pinned constraint: {metrics.NopDueToPinnedConstraint}");
        Console.WriteLine($"NOPs due to resource conflict: {metrics.NopDueToResourceConflict}");
        Console.WriteLine($"NOPs due to dynamic state: {metrics.NopDueToDynamicState}");
        Console.WriteLine($"Last SMT legality reject kind: {metrics.LastSmtLegalityRejectKind}");
        Console.WriteLine($"Last SMT legality authority source: {metrics.LastSmtLegalityAuthoritySource}");
        Console.WriteLine($"SMT owner-context guard rejects: {metrics.SmtOwnerContextGuardRejects}");
        Console.WriteLine($"SMT domain guard rejects: {metrics.SmtDomainGuardRejects}");
        Console.WriteLine($"SMT boundary guard rejects: {metrics.SmtBoundaryGuardRejects}");
        Console.WriteLine($"SMT shared-resource certificate rejects: {metrics.SmtSharedResourceCertificateRejects}");
        Console.WriteLine($"SMT register-group certificate rejects: {metrics.SmtRegisterGroupCertificateRejects}");
        Console.WriteLine(
            "SMT legality rejects by class: " +
            $"ALU={metrics.SmtLegalityRejectByAluClass}, " +
            $"LSU={metrics.SmtLegalityRejectByLsuClass}, " +
            $"DMA/Stream={metrics.SmtLegalityRejectByDmaStreamClass}, " +
            $"Branch/Control={metrics.SmtLegalityRejectByBranchControl}, " +
            $"System={metrics.SmtLegalityRejectBySystemSingleton}");
        Console.WriteLine($"Slack reclaim attempts: {metrics.SlackReclaimAttemptCount}");
        Console.WriteLine($"Class-flexible injects: {metrics.ClassFlexibleInjects}");
        Console.WriteLine($"Hard-pinned injects: {metrics.HardPinnedInjects}");
        Console.WriteLine($"Slack reclaim ratio: {metrics.SlackReclaimRatio:F4}");
        Console.WriteLine($"Flexible inject share: {metrics.FlexibleInjectShare:F4}");
        Console.WriteLine($"Multi-lane execute count: {metrics.MultiLaneExecuteCount}");
        Console.WriteLine($"Cluster prepared execution choices: {metrics.ClusterPreparedExecutionChoiceCount}");
        Console.WriteLine($"Wide-path successes: {metrics.WidePathSuccessCount}");
        Console.WriteLine($"Partial-width issues: {metrics.PartialWidthIssueCount}");
        Console.WriteLine($"Decoder prepared scalar groups: {metrics.DecoderPreparedScalarGroupCount}");
        Console.WriteLine($"VT spread per bundle: {metrics.VTSpreadPerBundle}");
        Console.WriteLine($"Issue packet prepared lane sum: {metrics.IssuePacketPreparedLaneCountSum}");
        Console.WriteLine($"Issue packet materialized lane sum: {metrics.IssuePacketMaterializedLaneCountSum}");
        Console.WriteLine($"Issue packet prepared physical lane sum: {metrics.IssuePacketPreparedPhysicalLaneCountSum}");
        Console.WriteLine($"Issue packet materialized physical lane sum: {metrics.IssuePacketMaterializedPhysicalLaneCountSum}");
        Console.WriteLine($"Issue packet width drops: {metrics.IssuePacketWidthDropCount}");
        Console.WriteLine($"Prepared scalar-projection lanes per cluster choice: {metrics.IssuePacketPreparedLanesPerClusterChoice:F4}");
        Console.WriteLine($"Materialized scalar-lane occupancy per cluster choice: {metrics.IssuePacketMaterializedLanesPerClusterChoice:F4}");
        Console.WriteLine($"Prepared physical lanes per cluster choice: {metrics.IssuePacketPreparedPhysicalLanesPerClusterChoice:F4}");
        Console.WriteLine($"Materialized physical lanes per cluster choice: {metrics.IssuePacketMaterializedPhysicalLanesPerClusterChoice:F4}");
        Console.WriteLine($"Physical lane realization rate: {metrics.IssuePacketLaneRealizationRate:F4}");
        Console.WriteLine($"Physical lane loss per cluster choice: {metrics.IssuePacketLaneLossPerClusterChoice:F4}");
        Console.WriteLine($"Width-drop share: {metrics.IssuePacketWidthDropShare:F4}");
        Console.WriteLine($"Scalar lanes retired: {metrics.ScalarLanesRetired}");
        Console.WriteLine($"Non-scalar lanes retired: {metrics.NonScalarLanesRetired}");
        Console.WriteLine($"Retire cycles: {metrics.RetireCycleCount}");
        Console.WriteLine($"Retired physical lanes per retire cycle: {metrics.RetiredPhysicalLanesPerRetireCycle:F4}");
        Console.WriteLine($"Compiler stage: {metrics.CompilerStage}");
        Console.WriteLine($"Decoder stage: {metrics.DecoderStage}");
        Console.WriteLine($"Likely failing stage: {metrics.LikelyFailingStage}");
        Console.WriteLine($"Failure message: {(string.IsNullOrWhiteSpace(metrics.FailureMessage) ? "<none>" : metrics.FailureMessage)}");
        Console.WriteLine($"Reference slice emitted instructions: {metrics.EmittedInstructionCount}");
        Console.WriteLine($"Reference slice bundle count: {metrics.BundleCount}");
        Console.WriteLine($"Compiler emitted distinct VTs: {metrics.CompilerEmittedDistinctVirtualThreadCount}");
        Console.WriteLine($"Compiler IR distinct VTs: {metrics.CompilerIrDistinctVirtualThreadCount}");
        Console.WriteLine($"Compiler schedule cycle groups: {metrics.CompilerScheduleCycleGroupCount}");
        Console.WriteLine($"Compiler schedule cross-VT cycle groups: {metrics.CompilerScheduleCrossVtCycleGroupCount}");
        Console.WriteLine($"Compiler schedule avg width: {metrics.CompilerScheduleAverageWidth:F4}");
        Console.WriteLine($"Compiler schedule avg VT spread: {metrics.CompilerScheduleAverageVtSpread:F4}");
        Console.WriteLine($"Compiler schedule max VT spread: {metrics.CompilerScheduleMaxVtSpread}");
        Console.WriteLine($"Compiler bundle count: {metrics.CompilerBundleCount}");
        Console.WriteLine($"Compiler cross-VT bundles: {metrics.CompilerBundleCrossVtCount}");
        Console.WriteLine($"Compiler bundle avg VT spread: {metrics.CompilerBundleAverageVtSpread:F4}");
        Console.WriteLine($"Compiler bundle max VT spread: {metrics.CompilerBundleMaxVtSpread}");
        Console.WriteLine($"First opcode: 0x{metrics.FirstOpcode:X}");
        Console.WriteLine($"First opcode registered: {metrics.FirstOpcodeRegistered}");
        Console.WriteLine($"Dominant effect: {metrics.DominantEffect}");
        Console.WriteLine($"NOP elision skips: {metrics.NopElisionSkipCount}");
        if (metrics.HasEligibilityTelemetry)
        {
            Console.WriteLine($"Eligibility masked cycles: {metrics.EligibilityMaskedCycles}");
            Console.WriteLine($"Eligibility masked ready candidates: {metrics.EligibilityMaskedReadyCandidates}");
            Console.WriteLine(
                $"Eligibility masks: requested=0x{metrics.LastEligibilityRequestedMask:X2}, normalized=0x{metrics.LastEligibilityNormalizedMask:X2}, ready=0x{metrics.LastEligibilityReadyPortMask:X2}, visible=0x{metrics.LastEligibilityVisibleReadyMask:X2}, masked=0x{metrics.LastEligibilityMaskedReadyMask:X2}");
        }
        if (metrics.HasPhaseCertificateTelemetry)
        {
            Console.WriteLine($"Phase certificate ready hits: {metrics.PhaseCertificateReadyHits}");
            Console.WriteLine($"Phase certificate ready misses: {metrics.PhaseCertificateReadyMisses}");
            Console.WriteLine($"Phase certificate reuse hit rate: {metrics.PhaseCertificateReuseHitRate:P2}");
            Console.WriteLine($"Phase certificate estimated checks saved: {metrics.EstimatedPhaseCertificateChecksSaved}");
            Console.WriteLine(
                $"Phase certificate invalidations: total={metrics.PhaseCertificateInvalidations}, mutation={metrics.PhaseCertificateMutationInvalidations}, phase-mismatch={metrics.PhaseCertificatePhaseMismatchInvalidations}");
        }
        if (metrics.HasStreamIngressWarmTelemetry)
        {
            Console.WriteLine($"SRF L1 bypass hits: {metrics.L1BypassHits}");
            Console.WriteLine(
                $"Foreground warm: {metrics.ForegroundWarmSuccesses}/{metrics.ForegroundWarmAttempts} success ({metrics.ForegroundWarmSuccessRate:P2}), reuse={metrics.ForegroundWarmReuseHits}, bypass={metrics.ForegroundBypassHits}");
            Console.WriteLine(
                $"Assist warm: {metrics.AssistWarmSuccesses}/{metrics.AssistWarmAttempts} success ({metrics.AssistWarmSuccessRate:P2}), reuse={metrics.AssistWarmReuseHits}, bypass={metrics.AssistBypassHits}");
            Console.WriteLine(
                $"Warm rejects: translation={metrics.StreamWarmTranslationRejects}, backend={metrics.StreamWarmBackendRejects}, resident-budget={metrics.AssistWarmResidentBudgetRejects}, loading-budget={metrics.AssistWarmLoadingBudgetRejects}, no-victim={metrics.AssistWarmNoVictimRejects}");
        }
        if (metrics.ShowcaseExecuted)
        {
            Console.WriteLine($"Showcase covers FSP: {metrics.ShowcaseCoversFsp}");
            Console.WriteLine($"Showcase covers typed-slot placement: {metrics.ShowcaseCoversTypedSlot}");
            Console.WriteLine($"Showcase covers admission/certificate seam: {metrics.ShowcaseCoversAdmission}");
            Console.WriteLine($"Showcase covers direct execution contract: {metrics.ShowcaseCoversSurfaceContract}");
            Console.WriteLine($"Showcase covers vector runtime: {metrics.ShowcaseCoversVector}");
            Console.WriteLine($"Showcase covers stream runtime: {metrics.ShowcaseCoversStream}");
            Console.WriteLine($"Showcase covers CSR plane: {metrics.ShowcaseCoversCsr}");
            Console.WriteLine($"Showcase covers system/FSM plane: {metrics.ShowcaseCoversSystem}");
            Console.WriteLine($"Showcase covers VMX plane: {metrics.ShowcaseCoversVmx}");
            Console.WriteLine($"Showcase covers trace/event observability: {metrics.ShowcaseCoversObservability}");
            Console.WriteLine($"Showcase trace events: {metrics.ShowcaseTraceEventCount}");
            Console.WriteLine($"Showcase queued pipeline events: {metrics.ShowcasePipelineEventCount}");
            Console.WriteLine($"Showcase FSM transitions applied: {metrics.ShowcaseFsmTransitionCount}");
            Console.WriteLine($"Showcase direct telemetry instructions: {metrics.ShowcaseDirectTelemetryInstrRetired}");
            Console.WriteLine($"Showcase direct barrier count: {metrics.ShowcaseDirectBarrierCount}");
            Console.WriteLine($"Showcase direct VMEXIT count: {metrics.ShowcaseDirectVmExitCount}");
            Console.WriteLine($"Showcase final pipeline state: {metrics.ShowcaseFinalPipelineState}");
            Console.WriteLine($"Assist runtime status: {metrics.ShowcaseAssistRuntimeStatus}");
        }

        if (!string.IsNullOrWhiteSpace(metrics.FailureMessage))
        {
            Console.Error.WriteLine($"Mode {mode} reported diagnostic failure: {metrics.FailureMessage}");
        }

        return metrics;
    }

    private static void PrintTimingAndMemoryInterpretation(SimpleAsmAppMetrics metrics)
    {
        TimingMemoryReport report = TimingMemoryReport.Create(metrics);
        Console.WriteLine($"Timing/memory comparison schema: {report.SchemaVersion} / producer {report.ProducerSchemaVersion}");
        Console.WriteLine($"Timing comparison policy: {report.TimingComparisonPolicy}");
        Console.WriteLine($"Cycle decomposition: total={report.TotalCycles}, pipeline-stall={report.PipelineStallCycles}, memory-stall={report.MemoryStallCycles}, non-memory-stall={RenderTelemetryMetric(report.NonMemoryStallCyclesMetric)}");
        Console.WriteLine($"Fine-grained cycle breakdown: {report.FineGrainedCycleBreakdownAvailability}");
        Console.WriteLine($"Memory telemetry disposition: {report.MemoryTelemetryDisposition}");
        Console.WriteLine($"Memory telemetry note: {report.MemoryTelemetryMessage}");
        Console.WriteLine($"Memory requests: accepted={RenderTelemetryMetric(report.AcceptedMemoryRequests)}, completed={RenderTelemetryMetric(report.CompletedMemoryRequests)}");
        Console.WriteLine($"Memory request lifecycle: telemetry-baseline-outstanding={RenderTelemetryMetric(report.TelemetryBaselineOutstandingRequests)}, canceled={RenderTelemetryMetric(report.CanceledMemoryRequests)}, consumed={RenderTelemetryMetric(report.ConsumedMemoryCompletions)}, outstanding={RenderTelemetryMetric(report.OutstandingMemoryRequests)}, identity-balance={report.RequestIdentityBalanceDisposition}");
        Console.WriteLine($"Data reads: accepted={RenderTelemetryMetric(report.DataReadAcceptedRequests)}, completed={RenderTelemetryMetric(report.DataReadCompletedRequests)}, bytes={RenderTelemetryMetric(report.DataReadBytes)}");
        Console.WriteLine($"Data writes: accepted={RenderTelemetryMetric(report.DataWriteAcceptedRequests)}, completed={RenderTelemetryMetric(report.DataWriteCompletedRequests)}, committed-bytes={RenderTelemetryMetric(report.CommittedDataWriteBytes)}");
        Console.WriteLine($"Instruction fetch: accepted={RenderTelemetryMetric(report.InstructionFetchAcceptedRequests)}, completed={RenderTelemetryMetric(report.InstructionFetchCompletedRequests)}, physical-read-bytes={RenderTelemetryMetric(report.InstructionFetchReadBytes)}");
        Console.WriteLine($"Admission rejects: queue-full={RenderTelemetryMetric(report.QueueFullRejects)}, bank-conflict={RenderTelemetryMetric(report.BankConflictRejects)}");
        Console.WriteLine($"Controller cycle bounds: edges={RenderTelemetryMetric(report.MemoryControllerCycles)}, read-service={RenderTelemetryMetric(report.MemoryReadServiceCycles)}, store-readiness={RenderTelemetryMetric(report.MemoryStoreReadinessServiceCycles)}, completion-publication={RenderTelemetryMetric(report.MemoryCompletionPublicationCycles)}");
        Console.WriteLine($"Scheduler diagnostic policy: {report.SchedulerDiagnosticComparisonPolicy}");
        Console.WriteLine($"Eligibility-mask policy: {report.EligibilityMaskComparisonPolicy}");
    }

    private static string RenderTelemetryMetric(TelemetryMetric metric) =>
        metric.Availability == "Available"
            ? metric.Value?.ToString() ?? "Unavailable"
            : "Unavailable";

    private static ReplayPhaseBenchmarkPairReport ExecuteReplayPhasePairWorkload(ulong iterations)
    {
        Console.WriteLine("=== Replay phase pair ===");
        Console.WriteLine($"SPEC-like iterations: {iterations:N0}");
        ReplayPhaseBenchmarkPairReport report = new SimpleAsmApp().ExecuteReplayPhaseBenchmarkPair(iterations);
        PrintReplayPhasePairSummary(report);
        return report;
    }

    private static void PrintReplayPhasePairSummary(ReplayPhaseBenchmarkPairReport report)
    {
        Console.WriteLine("Replay pair summary:");
        Console.WriteLine($"Iterations: {report.StablePhase.Iterations:N0}");
        Console.WriteLine(
            $"Stable phase: hits={report.StablePhase.PhaseCertificateReadyHits}, misses={report.StablePhase.PhaseCertificateReadyMisses}, hit-rate={report.StablePhase.PhaseCertificateReuseHitRate:P2}, checks-saved={report.StablePhase.EstimatedChecksSaved}, invalidations={report.StablePhase.PhaseCertificateInvalidations}");
        Console.WriteLine(
            $"Rotating phase: hits={report.RotatingPhase.PhaseCertificateReadyHits}, misses={report.RotatingPhase.PhaseCertificateReadyMisses}, hit-rate={report.RotatingPhase.PhaseCertificateReuseHitRate:P2}, checks-saved={report.RotatingPhase.EstimatedChecksSaved}, invalidations={report.RotatingPhase.PhaseCertificateInvalidations}");
        Console.WriteLine(
            $"Replay-aware cycle delta (stable - rotating): {report.StablePhase.ReplayAwareCycles - report.RotatingPhase.ReplayAwareCycles:+#;-#;0}");
        Console.WriteLine(
            $"Ready-hit delta (stable - rotating): {report.StablePhase.PhaseCertificateReadyHits - report.RotatingPhase.PhaseCertificateReadyHits:+#;-#;0}");
        Console.WriteLine(
            $"Checks-saved delta (stable - rotating): {report.StablePhase.EstimatedChecksSaved - report.RotatingPhase.EstimatedChecksSaved:+#;-#;0}");
        Console.WriteLine(
            $"Phase-mismatch invalidation delta (stable - rotating): {report.StablePhase.PhaseCertificatePhaseMismatchInvalidations - report.RotatingPhase.PhaseCertificatePhaseMismatchInvalidations:+#;-#;0}");
    }

    private static SafetyVerifierNegativeControlsReport ExecuteSafetyVerifierNegativeControlsWorkload()
    {
        Console.WriteLine("=== SafetyVerifier negative controls ===");
        SafetyVerifierNegativeControlsReport report = new SimpleAsmApp().ExecuteSafetyVerifierNegativeControls();
        Console.WriteLine(
            $"Counters: owner={report.Counters.OwnerContextRejects}, domain={report.Counters.DomainRejects}, boundary={report.Counters.BoundaryRejects}, invalid-replay={report.Counters.InvalidReplayBoundaryRejects}, stale-witness={report.Counters.StaleWitnessTemplateRejects}");
        foreach (SafetyVerifierNegativeControlResult control in report.Controls)
        {
            Console.WriteLine(
                $"{control.Scenario}: rejected={control.Rejected}, actual={control.ActualRejectKind}/{control.ActualAuthoritySource}, counter={control.CounterValue}, passed={control.Passed}");
        }

        return report;
    }

    private static ReplayReuseDiagnosticsReport ExecuteReplayReuseDiagnosticsWorkload(ulong iterations)
    {
        Console.WriteLine("=== Replay template reuse diagnostics ===");
        Console.WriteLine($"SPEC-like iterations: {iterations:N0}");
        ReplayReuseDiagnosticsReport report = new SimpleAsmApp().ExecuteReplayReuseDiagnostics(iterations);
        Console.WriteLine(
            $"Template aggregate: attempts={report.Aggregate.ReplayTemplateLookupAttempts}, hits={report.Aggregate.ReplayTemplateHits}, misses={report.Aggregate.ReplayTemplateMisses}, hit-rate={report.Aggregate.HitRate:P2}");
        Console.WriteLine(
            $"Invalidations: phase-key={report.Aggregate.InvalidationsByPhaseKey}, structural={report.Aggregate.InvalidationsByStructuralIdentity}, boundary={report.Aggregate.InvalidationsByBoundaryState}, witness-accesses={report.Aggregate.WitnessAccesses}, fallback-to-live-witness={report.Aggregate.FallbackToLiveWitness}");
        foreach (ReplayTemplateReuseScenarioReport scenario in report.Scenarios)
        {
            Console.WriteLine(
                $"{scenario.Scenario}: attempts={scenario.Metrics.ReplayTemplateLookupAttempts}, hits={scenario.Metrics.ReplayTemplateHits}, misses={scenario.Metrics.ReplayTemplateMisses}, warmup-misses={scenario.Metrics.WarmupMisses}, fallback-to-live-witness={scenario.Metrics.FallbackToLiveWitness}, passed={scenario.Passed}");
        }

        return report;
    }

    private static AssistantDecisionMatrixReport ExecuteAssistantDecisionMatrixWorkload()
    {
        Console.WriteLine("=== Assistant decision matrix ===");
        AssistantDecisionMatrixReport report = new SimpleAsmApp().ExecuteAssistantDecisionMatrix();
        Console.WriteLine(
            $"Matrix aggregate: attempts={report.Aggregate.DecisionAttempts}, accepted={report.Aggregate.Accepted}, quota-rejects={report.Aggregate.QuotaRejects}, backpressure-rejects={report.Aggregate.BackpressureRejects}, owner-domain-rejects={report.Aggregate.OwnerDomainAdministratorRejects}, invalid-replay-rejects={report.Aggregate.InvalidReplayRejects}, primary-priority-rejects={report.Aggregate.PrimaryStreamPriorityRejects}");
        foreach (AssistantDecisionScenarioReport scenario in report.Scenarios)
        {
            Console.WriteLine(
                $"{scenario.Scenario}: expected={scenario.ExpectedOutcome}/{scenario.ExpectedReason}, actual={scenario.ActualOutcome}/{scenario.ActualReason}, passed={scenario.Passed}, detail={scenario.Detail}");
        }
        AssistantVisibilityDiagnosticsReport visibility = report.VisibilityDiagnostics;
        Console.WriteLine(
            $"{visibility.Scenario}: expected={visibility.ExpectedOutcome}, actual={visibility.ActualOutcome}, invalidation={visibility.InvalidationReason}, passed={visibility.Passed}, scope={visibility.EvidenceScope}");
        Console.WriteLine(
            $"Assistant visibility/non-retirement counters: assist accepted={visibility.Metrics.AssistAccepted}, replay-invalidated-after-acceptance={visibility.Metrics.AssistReplayInvalidatedAfterAcceptance}, assist discarded={visibility.Metrics.AssistDiscarded}, assist retire records={visibility.Metrics.AssistRetireRecords}, assist architectural writes={visibility.Metrics.AssistArchitecturalWrites}, assist committed stores={visibility.Metrics.AssistCommittedStores}, assist telemetry events={visibility.Metrics.AssistTelemetryEvents}, assist carrier publications={visibility.Metrics.AssistCarrierPublications}, foreground retire records preserved={visibility.Metrics.ForegroundRetireRecordsPreserved}");

        return report;
    }

    private static StreamVectorSpecSuiteReport ExecuteStreamVectorSpecSuiteWorkload(ulong iterations)
    {
        Console.WriteLine("=== Stream/Vector SPEC-like suite ===");
        Console.WriteLine($"SPEC-like iterations: {iterations:N0}");

        StreamVectorSpecSuiteReport report = new StreamVectorSpecSuite().Execute(iterations);
        Console.WriteLine(
            $"Suite aggregate: scenarios={report.Aggregate.ScenarioCount}, passed={report.Aggregate.PassedScenarioCount}, " +
            $"dynamic-instructions={report.Aggregate.DynamicInstructionCount:N0}, vector-elements={report.Aggregate.VectorElementsProcessed:N0}, " +
            $"modeled-bytes={report.Aggregate.ModeledBytesTouched:N0}, checksum=0x{report.Aggregate.ResultChecksum:X16}");

        foreach (StreamVectorSpecScenarioReport scenario in report.Scenarios)
        {
            Console.WriteLine(
                $"{scenario.Id}: passed={scenario.Passed}, instructions={scenario.DynamicInstructionCount:N0}, " +
                $"elements={scenario.VectorElementsProcessed:N0}, bytes={scenario.ModeledBytesTouched:N0}, " +
                $"error={scenario.MaxAbsoluteError:G4}, checksum=0x{scenario.ResultChecksum:X16}");
            if (!scenario.Passed && !string.IsNullOrWhiteSpace(scenario.FailureMessage))
            {
                Console.WriteLine($"  failure: {scenario.FailureMessage}");
            }
        }

        return report;
    }

    private static MatrixTileSpecSuiteReport ExecuteMatrixTileSpecSuiteWorkload(ulong iterations)
    {
        Console.WriteLine("=== MatrixTile SPEC-like pressure suite ===");
        Console.WriteLine($"SPEC-like iterations: {iterations:N0}");

        MatrixTileSpecSuiteReport report = new MatrixTileSpecSuite().Execute(iterations);
        Console.WriteLine(
            $"Suite aggregate: scenarios={report.Aggregate.ScenarioCount}, passed={report.Aggregate.PassedScenarioCount}, " +
            $"runtime-instructions={report.Aggregate.RuntimeInstructionCount:N0}, compiler-emissions={report.Aggregate.CompilerEmissionCount:N0}, " +
            $"retire-publications={report.Aggregate.RetirePublicationCount:N0}, " +
            $"replay-round-trips={report.Aggregate.ReplayRoundTripCount:N0}, fail-closed-rejections={report.Aggregate.FailClosedRejectionCount:N0}, " +
            $"stream-bytes={report.Aggregate.StreamBytesTransferred:N0}, checksum=0x{report.Aggregate.ResultChecksum:X16}");

        foreach (MatrixTileSpecScenarioReport scenario in report.Scenarios)
        {
            Console.WriteLine(
                $"{scenario.Id}: passed={scenario.Passed}, instructions={scenario.RuntimeInstructionCount:N0}, " +
                $"compiler={scenario.CompilerEmissionCount:N0}, retire={scenario.RetirePublicationCount:N0}, replay={scenario.ReplayRoundTripCount:N0}, " +
                $"rejected={scenario.FailClosedRejectionCount:N0}, bytes={scenario.StreamBytesTransferred:N0}, " +
                $"invalidations={scenario.StreamInvalidationCount:N0}, checksum=0x{scenario.ResultChecksum:X16}");
            if (!scenario.Passed && !string.IsNullOrWhiteSpace(scenario.FailureMessage))
            {
                Console.WriteLine($"  failure: {scenario.FailureMessage}");
            }
        }

        return report;
    }

    private static WhiteBookContractDiagnosticsReport ExecuteWhiteBookContractDiagnosticsWorkload(ulong iterations)
    {
        Console.WriteLine("=== Stream WhiteBook contract diagnostics ===");
        Console.WriteLine($"SPEC-like iterations: {iterations:N0}");

        WhiteBookContractDiagnosticsReport report =
            new SimpleAsmApp().ExecuteWhiteBookContractDiagnostics(iterations);
        Console.WriteLine(
            $"WhiteBook contract aggregate: probes={report.Summary.PassedProbeCount}/{report.Summary.ProbeCount}, " +
            $"claims={report.Summary.CoveredClaimGroups.Count}, status={(report.Succeeded ? "passed" : "failed")}");

        foreach (WhiteBookContractProbeResult probe in report.Probes)
        {
            Console.WriteLine(
                $"{probe.Id}: passed={probe.Passed}, boundary={probe.ExpectedBoundary}, observed={probe.ObservedEvidence}");
            if (!probe.Passed && !string.IsNullOrWhiteSpace(probe.FailureMessage))
            {
                Console.WriteLine($"  failure: {probe.FailureMessage}");
            }
        }

        return report;
    }

    private static void PrintSingleRunSummary(DiagnosticRunResult result)
    {
        Console.WriteLine($"Run status: {result.Status}");
        if (result.Status == DiagnosticRunStatus.TimedOut)
        {
            Console.WriteLine("Worker exit: timeout (synthetic exit code 124)");
        }
        else
        {
            Console.WriteLine($"Worker exit code: {(result.ExitCode.HasValue ? result.ExitCode.Value : -1)}");
        }

        Console.WriteLine($"Elapsed: {result.Elapsed}");
        Console.WriteLine($"Artifacts: {result.ArtifactDirectory}");
        if (result.LastCheckpoint is { } checkpoint)
        {
            Console.WriteLine($"Last checkpoint: {checkpoint.Stage} ({checkpoint.Reason})");
            if (checkpoint.ObservedCycleCount.HasValue && checkpoint.ObservedInstructionsRetired.HasValue)
            {
                Console.WriteLine($"Last observed progress: cycles={checkpoint.ObservedCycleCount.Value}, retired={checkpoint.ObservedInstructionsRetired.Value}");
            }

            if (checkpoint.ActiveVirtualThreadId.HasValue && checkpoint.ActiveLivePc.HasValue)
            {
                Console.WriteLine($"Last observed core focus: VT={checkpoint.ActiveVirtualThreadId.Value}, PC=0x{checkpoint.ActiveLivePc.Value:X}");
            }

            Console.WriteLine($"Likely blocked phase: {InferLikelyBlockedPhase(checkpoint.Stage)}");
        }

        string timeoutDumpPath = Path.Combine(result.ArtifactDirectory, "timeout_partial_dump.json");
        if (File.Exists(timeoutDumpPath))
        {
            Console.WriteLine($"Timeout partial dump: {timeoutDumpPath}");
        }

        if (!string.IsNullOrWhiteSpace(result.Manifest.FailureMessage))
        {
            Console.WriteLine($"Failure message: {result.Manifest.FailureMessage}");
        }
    }

    private static void PrintBatchSummary(DiagnosticBatchRunResult batch)
    {
        Console.WriteLine($"{batch.DisplayName} summary:");
        Console.WriteLine($"Aggregate status: {batch.Status}");
        Console.WriteLine($"Child runs: {batch.Results.Count}");
        Console.WriteLine($"Artifacts: {batch.ArtifactDirectory}");
        if (!string.IsNullOrWhiteSpace(batch.FailureMessage))
        {
            Console.WriteLine($"Summary note: {batch.FailureMessage}");
        }

        PrintStreamVectorFinalSummaryIfAvailable(batch);
        PrintMatrixTileFinalSummaryIfAvailable(batch);
    }

    private static void PrintStreamVectorFinalSummaryIfAvailable(DiagnosticBatchRunResult batch)
    {
        DiagnosticRunResult? streamVectorResult = batch.Results.FirstOrDefault(static result =>
            result.Profile.WorkloadKind == DiagnosticWorkloadKind.StreamVectorSpecSuite);
        if (streamVectorResult is null)
        {
            return;
        }

        PrintStreamVectorFinalSummaryIfAvailable(streamVectorResult);
    }

    private static void PrintMatrixTileFinalSummaryIfAvailable(DiagnosticBatchRunResult batch)
    {
        DiagnosticRunResult? matrixTileResult = batch.Results.FirstOrDefault(static result =>
            result.Profile.WorkloadKind == DiagnosticWorkloadKind.MatrixTileSpecSuite);
        if (matrixTileResult is null)
        {
            return;
        }

        PrintMatrixTileFinalSummaryIfAvailable(matrixTileResult);
    }

    private static void PrintMatrixTileFinalSummaryIfAvailable(DiagnosticRunResult result)
    {
        if (result.Profile.WorkloadKind != DiagnosticWorkloadKind.MatrixTileSpecSuite)
        {
            return;
        }

        DiagnosticArtifactWriter writer = new DiagnosticArtifactWriter(result.ArtifactDirectory);
        MatrixTileSpecSuiteReport? report =
            writer.TryReadJson<MatrixTileSpecSuiteReport>("matrix_tile_spec_report.json");
        if (report is null)
        {
            Console.WriteLine("MatrixTile final telemetry unavailable: matrix_tile_spec_report.json was not produced or could not be read.");
            return;
        }

        double elapsedMs = Math.Max(report.Aggregate.ElapsedMilliseconds, 0.000001d);
        Console.WriteLine();
        Console.WriteLine("=== MatrixTile final benchmarks, resources, and fail-closed diagnostics ===");
        Console.WriteLine(
            $"Suite: {report.SuiteId}, status={(report.Succeeded ? "Passed" : "Failed")}, iterations={report.Iterations:N0}, " +
            $"artifact={Path.Combine(result.ArtifactDirectory, "matrix_tile_spec_report.json")}");
        Console.WriteLine($"Runtime closure: {report.RuntimeClosureDecision}");
        Console.WriteLine(
            $"Aggregate: scenarios={report.Aggregate.PassedScenarioCount}/{report.Aggregate.ScenarioCount}, " +
            $"instructions={report.Aggregate.RuntimeInstructionCount:N0}, compiler-emissions={report.Aggregate.CompilerEmissionCount:N0}, " +
            $"retire={report.Aggregate.RetirePublicationCount:N0}, " +
            $"replay-round-trips={report.Aggregate.ReplayRoundTripCount:N0}, fail-closed={report.Aggregate.FailClosedRejectionCount:N0}, " +
            $"stream-bytes={report.Aggregate.StreamBytesTransferred:N0}, stream-invalidations={report.Aggregate.StreamInvalidationCount:N0}, " +
            $"elapsed-ms={report.Aggregate.ElapsedMilliseconds:N3}");
        Console.WriteLine(
            $"Throughput: runtime-instructions/ms={report.Aggregate.RuntimeInstructionCount / elapsedMs:N2}, " +
            $"compiler-emissions/ms={report.Aggregate.CompilerEmissionCount / elapsedMs:N2}, " +
            $"retire-publications/ms={report.Aggregate.RetirePublicationCount / elapsedMs:N2}, " +
            $"stream-bytes/ms={report.Aggregate.StreamBytesTransferred / elapsedMs:N2}");
        Console.WriteLine(
            $"Baselines: smoke-throughput={(report.Aggregate.MeetsSmokeThroughputBaseline ? "passed" : "failed")}, " +
            $"runtime-instructions/ms={report.Aggregate.RuntimeInstructionsPerMillisecond:N2}, " +
            $"retire/ms={report.Aggregate.RetirePublicationsPerMillisecond:N2}, " +
            $"replay/ms={report.Aggregate.ReplayRoundTripsPerMillisecond:N2}, " +
            $"stream-bytes/ms={report.Aggregate.StreamBytesPerMillisecond:N2}");

        Console.WriteLine("Scenarios:");
        foreach (MatrixTileSpecScenarioReport scenario in report.Scenarios)
        {
            Console.WriteLine(
                $"  {scenario.Id}: {(scenario.Passed ? "passed" : "failed")}, contour={scenario.Contour}, " +
                $"instructions={scenario.RuntimeInstructionCount:N0}, retire={scenario.RetirePublicationCount:N0}, " +
                $"compiler={scenario.CompilerEmissionCount:N0}, replay={scenario.ReplayRoundTripCount:N0}, rejected={scenario.FailClosedRejectionCount:N0}, " +
                $"bytes={scenario.StreamBytesTransferred:N0}, invalidations={scenario.StreamInvalidationCount:N0}, " +
                $"elapsed-ms={scenario.ElapsedMilliseconds:N3}, " +
                $"baseline={(scenario.MeetsSmokeThroughputBaseline ? "pass" : "fail")}, " +
                $"inst/ms={scenario.RuntimeInstructionsPerMillisecond:N2}, bytes/ms={scenario.StreamBytesPerMillisecond:N2}, " +
                $"opcodes={string.Join('/', scenario.Opcodes)}");
            foreach (string evidence in scenario.ResourceEvidence)
            {
                Console.WriteLine($"    resource: {evidence}");
            }

            if (!scenario.Passed && !string.IsNullOrWhiteSpace(scenario.FailureMessage))
            {
                Console.WriteLine($"    failure: {scenario.FailureMessage}");
            }
        }
    }

    private static void PrintStreamVectorFinalSummaryIfAvailable(DiagnosticRunResult result)
    {
        if (result.Profile.WorkloadKind != DiagnosticWorkloadKind.StreamVectorSpecSuite)
        {
            return;
        }

        DiagnosticArtifactWriter writer = new DiagnosticArtifactWriter(result.ArtifactDirectory);
        StreamVectorSpecSuiteReport? report =
            writer.TryReadJson<StreamVectorSpecSuiteReport>("stream_vector_spec_report.json");
        if (report is null)
        {
            Console.WriteLine("Stream/Vector final telemetry unavailable: stream_vector_spec_report.json was not produced or could not be read.");
            return;
        }

        PrintStreamVectorFinalSummary(report, result.ArtifactDirectory);
    }

    private static void PrintStreamVectorFinalSummary(
        StreamVectorSpecSuiteReport report,
        string artifactDirectory)
    {
        double elapsedMs = Math.Max(report.Aggregate.ElapsedMilliseconds, 0.000001d);
        ulong telemetryBytes = SumTelemetry(report, static telemetry => checked((ulong)Math.Max(telemetry.TotalBytesTransferred, 0)));
        long totalBursts = report.Scenarios.Sum(static scenario => scenario.Telemetry.TotalBursts);
        long foregroundWarmAttempts = report.Scenarios.Sum(static scenario => scenario.Telemetry.ForegroundWarmAttempts);
        long foregroundWarmSuccesses = report.Scenarios.Sum(static scenario => scenario.Telemetry.ForegroundWarmSuccesses);
        long foregroundWarmReuseHits = report.Scenarios.Sum(static scenario => scenario.Telemetry.ForegroundWarmReuseHits);
        long foregroundBypassHits = report.Scenarios.Sum(static scenario => scenario.Telemetry.ForegroundBypassHits);
        long assistWarmAttempts = report.Scenarios.Sum(static scenario => scenario.Telemetry.AssistWarmAttempts);
        long assistWarmSuccesses = report.Scenarios.Sum(static scenario => scenario.Telemetry.AssistWarmSuccesses);
        long assistWarmReuseHits = report.Scenarios.Sum(static scenario => scenario.Telemetry.AssistWarmReuseHits);
        long assistBypassHits = report.Scenarios.Sum(static scenario => scenario.Telemetry.AssistBypassHits);
        long translationRejects = report.Scenarios.Sum(static scenario => scenario.Telemetry.StreamWarmTranslationRejects);
        long backendRejects = report.Scenarios.Sum(static scenario => scenario.Telemetry.StreamWarmBackendRejects);
        ulong dmaBytesRead = SumTelemetry(report, static telemetry => telemetry.DmaBytesRead);
        ulong dmaBytesStaged = SumTelemetry(report, static telemetry => telemetry.DmaBytesStaged);
        int dmaReadBursts = report.Scenarios.Sum(static scenario => scenario.Telemetry.DmaReadBurstCount);
        ulong dmaLatencyCycles = SumTelemetry(report, static telemetry => telemetry.DmaModeledLatencyCycles);
        ulong dmaElementOps = SumTelemetry(report, static telemetry => telemetry.DmaElementOperations);
        int dmaDirectWrites = report.Scenarios.Sum(static scenario => scenario.Telemetry.DmaDirectDestinationWrites);
        bool dmaUsedLane6 = report.Scenarios.Any(static scenario => scenario.Telemetry.DmaUsedLane6Backend);

        Console.WriteLine();
        Console.WriteLine("=== Stream/Vector final benchmarks, telemetry, statistics ===");
        Console.WriteLine(
            $"Suite: {report.SuiteId}, status={(report.Succeeded ? "Passed" : "Failed")}, iterations={report.Iterations:N0}, artifact={Path.Combine(artifactDirectory, "stream_vector_spec_report.json")}");
        Console.WriteLine(
            $"Aggregate: scenarios={report.Aggregate.PassedScenarioCount}/{report.Aggregate.ScenarioCount}, " +
            $"dynamic-instructions={report.Aggregate.DynamicInstructionCount:N0}, vector-elements={report.Aggregate.VectorElementsProcessed:N0}, " +
            $"modeled-bytes={report.Aggregate.ModeledBytesTouched:N0}, elapsed-ms={report.Aggregate.ElapsedMilliseconds:N3}, checksum=0x{report.Aggregate.ResultChecksum:X16}");
        Console.WriteLine(
            $"Throughput: vector-elements/ms={report.Aggregate.VectorElementsProcessed / elapsedMs:N2}, " +
            $"modeled-bytes/ms={report.Aggregate.ModeledBytesTouched / elapsedMs:N2}, dynamic-instructions/ms={report.Aggregate.DynamicInstructionCount / elapsedMs:N2}");

        Console.WriteLine("Benchmarks:");
        foreach (StreamVectorSpecScenarioReport scenario in report.Scenarios)
        {
            double scenarioElapsedMs = Math.Max(scenario.ElapsedMilliseconds, 0.000001d);
            Console.WriteLine(
                $"  {scenario.Id}: {(scenario.Passed ? "passed" : "failed")}, algorithm={scenario.Algorithm}, " +
                $"instructions={scenario.DynamicInstructionCount:N0}, elements={scenario.VectorElementsProcessed:N0}, " +
                $"bytes={scenario.ModeledBytesTouched:N0}, elapsed-ms={scenario.ElapsedMilliseconds:N3}, " +
                $"elements/ms={scenario.VectorElementsProcessed / scenarioElapsedMs:N2}, error={scenario.MaxAbsoluteError:G4}, opcodes={string.Join('/', scenario.Opcodes)}");
        }

        Console.WriteLine("Stream telemetry:");
        Console.WriteLine(
            $"  bursts={totalBursts:N0}, transferred-bytes={telemetryBytes:N0}, foreground-warm={foregroundWarmSuccesses:N0}/{foregroundWarmAttempts:N0}, " +
            $"foreground-reuse={foregroundWarmReuseHits:N0}, foreground-bypass={foregroundBypassHits:N0}");
        Console.WriteLine(
            $"  assist-warm={assistWarmSuccesses:N0}/{assistWarmAttempts:N0}, assist-reuse={assistWarmReuseHits:N0}, assist-bypass={assistBypassHits:N0}, " +
            $"translation-rejects={translationRejects:N0}, backend-rejects={backendRejects:N0}");

        Console.WriteLine("DMA lane6 telemetry:");
        Console.WriteLine(
            $"  lane6-backend-used={dmaUsedLane6}, direct-destination-writes={dmaDirectWrites:N0}, bytes-read={dmaBytesRead:N0}, " +
            $"bytes-staged={dmaBytesStaged:N0}, read-bursts={dmaReadBursts:N0}, modeled-latency-cycles={dmaLatencyCycles:N0}, element-ops={dmaElementOps:N0}");
    }

    private static ulong SumTelemetry(
        StreamVectorSpecSuiteReport report,
        Func<StreamVectorSpecTelemetry, ulong> selector)
    {
        ulong total = 0;
        foreach (StreamVectorSpecScenarioReport scenario in report.Scenarios)
        {
            total += selector(scenario.Telemetry);
        }

        return total;
    }

    private static void PrintBankBatchSummary(DiagnosticBatchRunResult batch)
    {
        PrintBatchSummary(batch);

        if (batch.Results.Count >= 2 &&
            batch.Results[0].Metrics is { } latencyHidingMetrics &&
            batch.Results[1].Metrics is { } bankDistributedMetrics)
        {
            PrintBankDistributionPairSummary(latencyHidingMetrics, bankDistributedMetrics);
            Console.WriteLine($"Comparison artifact: {Path.Combine(batch.ArtifactDirectory, "bank_distribution_comparison.json")}");
            return;
        }

        Console.WriteLine("Bank pair summary unavailable because one or more child runs did not emit metrics.");
    }

    private static void PrintBaselineVsFspSummary(DiagnosticBatchRunResult batch)
    {
        PrintBatchSummary(batch);

        if (batch.Results.Count >= 2 &&
            batch.Results[0].Metrics is { } baselineMetrics &&
            batch.Results[1].Metrics is { } fspMetrics)
        {
            Console.WriteLine("Baseline vs FSP summary:");
            Console.WriteLine("Scalar baseline: single-thread no-vector contour.");
            Console.WriteLine("VT + FSP: packed scalar contour with cross-VT slot pilfering enabled.");
            Console.WriteLine($"IPC delta (VT+FSP - baseline): {fspMetrics.Ipc - baselineMetrics.Ipc:+0.0000;-0.0000;0.0000}");
            Console.WriteLine($"Cycle delta (VT+FSP - baseline): {(long)fspMetrics.CycleCount - (long)baselineMetrics.CycleCount:+#;-#;0}");
            Console.WriteLine($"Partial-width issue delta (VT+FSP - baseline): {(long)fspMetrics.PartialWidthIssueCount - (long)baselineMetrics.PartialWidthIssueCount:+#;-#;0}");
            Console.WriteLine($"Retired lanes/retire-cycle delta (VT+FSP - baseline): {fspMetrics.RetiredPhysicalLanesPerRetireCycle - baselineMetrics.RetiredPhysicalLanesPerRetireCycle:+0.0000;-0.0000;0.0000}");
            Console.WriteLine($"Bytes transferred delta (VT+FSP - baseline): {(long)fspMetrics.BytesTransferred - (long)baselineMetrics.BytesTransferred:+#;-#;0}");
            Console.WriteLine($"Comparison artifact: {Path.Combine(batch.ArtifactDirectory, "compare_baseline_fsp.json")}");
            return;
        }

        Console.WriteLine("Baseline vs FSP summary unavailable because one or more child runs did not emit metrics.");
    }

    private static void PrintBankDistributionPairSummary(
        SimpleAsmAppMetrics latencyHidingMetrics,
        SimpleAsmAppMetrics bankDistributedMetrics)
    {
        Console.WriteLine("Bank pair summary:");
        Console.WriteLine("Lk: live latency-hiding load kernel companion.");
        Console.WriteLine("Bnmcz: live bank-no-conflict mixed-zoo companion.");
        Console.WriteLine($"IPC delta (Bnmcz - Lk): {bankDistributedMetrics.Ipc - latencyHidingMetrics.Ipc:+0.0000;-0.0000;0.0000}");
        Console.WriteLine($"Cycle delta (Bnmcz - Lk): {(long)bankDistributedMetrics.CycleCount - (long)latencyHidingMetrics.CycleCount:+#;-#;0}");
        Console.WriteLine($"Partial-width issue delta (Bnmcz - Lk): {(long)bankDistributedMetrics.PartialWidthIssueCount - (long)latencyHidingMetrics.PartialWidthIssueCount:+#;-#;0}");
        Console.WriteLine($"Retired lanes/retire-cycle delta (Bnmcz - Lk): {bankDistributedMetrics.RetiredPhysicalLanesPerRetireCycle - latencyHidingMetrics.RetiredPhysicalLanesPerRetireCycle:+0.0000;-0.0000;0.0000}");
        Console.WriteLine($"Bytes transferred delta (Bnmcz - Lk): {(long)bankDistributedMetrics.BytesTransferred - (long)latencyHidingMetrics.BytesTransferred:+#;-#;0}");
    }

    private static void ReplayCapturedConsole(DiagnosticRunResult result)
    {
        ReplayCapturedStream(Path.Combine(result.ArtifactDirectory, "stdout.log"), isError: false);
        ReplayCapturedStream(Path.Combine(result.ArtifactDirectory, "stderr.log"), isError: true);
    }

    private static void ReplayCapturedStream(string path, bool isError)
    {
        if (!File.Exists(path))
        {
            return;
        }

        string content = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        if (isError)
        {
            Console.Error.Write(content);
            if (!content.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            {
                Console.Error.WriteLine();
            }
        }
        else
        {
            Console.Write(content);
            if (!content.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            {
                Console.WriteLine();
            }
        }
    }

    private static DiagnosticArtifactManifest CreateSingleManifest(
        DiagnosticRunProfile profile,
        string artifactDirectory,
        DiagnosticRunStatus status,
        DateTimeOffset? startedUtc,
        DateTimeOffset? finishedUtc,
        TimeSpan? elapsed,
        int? processId,
        int? exitCode,
        string? failureMessage,
        Dictionary<string, string> generatedFiles)
    {
        return new DiagnosticArtifactManifest
        {
            RunKind = "single",
            CommandId = profile.Id,
            DisplayName = profile.DisplayName,
            Status = status.ToString(),
            ArtifactDirectory = artifactDirectory,
            WorkerProfileId = profile.Id,
            WorkloadKind = profile.WorkloadKind.ToString(),
            FrontendProfile = profile.FrontendMode.ToString(),
            ShadowFrontendMode = profile.ShadowFrontendMode.ToString(),
            ProcessId = processId,
            ExitCode = exitCode,
            FailureMessage = failureMessage,
            StartedUtc = startedUtc,
            FinishedUtc = finishedUtc,
            ElapsedSeconds = elapsed?.TotalSeconds,
            GeneratedFiles = new Dictionary<string, string>(generatedFiles, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static DiagnosticArtifactManifest CreateBatchManifest(
        string commandId,
        string displayName,
        string artifactDirectory,
        DiagnosticRunStatus status,
        string? frontendProfile,
        string? shadowFrontendMode,
        DateTimeOffset startedUtc,
        DateTimeOffset? finishedUtc,
        TimeSpan? elapsed,
        int? exitCode,
        string? failureMessage,
        Dictionary<string, string> childArtifacts,
        Dictionary<string, string> generatedFiles)
    {
        return new DiagnosticArtifactManifest
        {
            RunKind = "batch",
            CommandId = commandId,
            DisplayName = displayName,
            Status = status.ToString(),
            ArtifactDirectory = artifactDirectory,
            FrontendProfile = frontendProfile,
            ShadowFrontendMode = shadowFrontendMode,
            ProcessId = Environment.ProcessId,
            ExitCode = exitCode,
            FailureMessage = failureMessage,
            StartedUtc = startedUtc,
            FinishedUtc = finishedUtc,
            ElapsedSeconds = elapsed?.TotalSeconds,
            GeneratedFiles = new Dictionary<string, string>(generatedFiles, StringComparer.OrdinalIgnoreCase),
            ChildArtifacts = new Dictionary<string, string>(childArtifacts, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static DiagnosticRunStatus AggregateStatus(IEnumerable<DiagnosticRunStatus> statuses)
    {
        bool sawAny = false;
        bool sawFailure = false;

        foreach (DiagnosticRunStatus status in statuses)
        {
            sawAny = true;
            if (status == DiagnosticRunStatus.TimedOut)
            {
                return DiagnosticRunStatus.TimedOut;
            }

            if (status == DiagnosticRunStatus.Failed || status == DiagnosticRunStatus.Pending || status == DiagnosticRunStatus.Running)
            {
                sawFailure = true;
            }
        }

        if (!sawAny)
        {
            return DiagnosticRunStatus.Pending;
        }

        return sawFailure ? DiagnosticRunStatus.Failed : DiagnosticRunStatus.Succeeded;
    }

    private static bool IsBankDistributionPairCommand(string arg)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arg);

        string normalized = NormalizeCommand(arg);
        return normalized is "banks" or "bank-pair" or "compare-banks";
    }

    private static bool IsBankDistributionPairLongCommand(string arg)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arg);

        string normalized = NormalizeCommand(arg);
        return normalized is "banks-long" or "bank-pair-long" or "compare-banks-long";
    }

    private static bool IsBaselineVsFspComparisonCommand(string arg)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arg);

        string normalized = NormalizeCommand(arg);
        return normalized is "compare-baseline-fsp" or "baseline-vs-fsp";
    }

    private static bool IsMatrixSmokeCommand(string arg)
    {
        return arg == "matrix-smoke";
    }

    private static bool IsMatrixRuntimeCommand(string arg)
    {
        return arg == "matrix-runtime";
    }

    private static bool IsMatrixMemoryCommand(string arg)
    {
        return arg == "matrix-memory";
    }

    private static bool IsMatrixFullCommand(string arg)
    {
        return arg == "matrix-full";
    }

    private static bool IsMatrixWideCommand(string arg)
    {
        return arg is "matrix-wide" or "matrix-timeout-probe";
    }

    private static bool IsMatrixSpecCommand(string arg)
    {
        return arg is "matrix-spec" or "spec-matrix";
    }

    private static bool IsWhiteBookSmokeCommand(string arg)
    {
        return arg is "whitebook-smoke" or "matrix-whitebook" or "matrix-whitebook-smoke";
    }

    private static bool IsWhiteBookFullCommand(string arg)
    {
        return arg is "whitebook-full" or "matrix-whitebook-full";
    }

    private static bool IsHelpCommand(string arg)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arg);

        string normalized = NormalizeCommand(arg);
        return normalized is "help" or "h";
    }

    private static bool TryParseWorkerInvocation(string[] args, out string profileId, out string artifactDirectory)
    {
        profileId = string.Empty;
        artifactDirectory = string.Empty;

        if (args.Length < 4 || !args[0].Equals("--worker", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        profileId = args[1];
        for (int i = 2; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--artifact-dir", StringComparison.OrdinalIgnoreCase))
            {
                artifactDirectory = args[i + 1];
                return !string.IsNullOrWhiteSpace(profileId) && !string.IsNullOrWhiteSpace(artifactDirectory);
            }
        }

        throw new ArgumentException("Worker invocation requires '--worker <profile-id> --artifact-dir <path>'.");
    }

    private static FrontendMode ParseFrontend(string[] args)
    {
        _ = args;
        return FrontendMode.NativeVLIW;
    }

    private static ParentCommandOptions ParseParentCommandOptions(string[] args)
    {
        string? command = null;
        int? timeoutMs = null;
        int? heartbeatMs = null;
        ulong? workloadIterations = null;
        DiagnosticTelemetryLogMode? telemetryLogMode = null;
        ShadowFrontendMode shadowFrontendMode = ShadowFrontendMode.Off;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--shadow-frontend", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException("Expected '--shadow-frontend <off|observe|cold-warm>'.");
                shadowFrontendMode = DiagnosticShadowFrontend.ParseMode(args[++i]);
                continue;
            }

            if (arg.Equals("--timeout-ms", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[++i], out int parsedTimeoutMs) || parsedTimeoutMs <= 0)
                {
                    throw new ArgumentException("Expected '--timeout-ms <positive-int>'.");
                }

                timeoutMs = parsedTimeoutMs;
                continue;
            }

            if (arg.Equals("--heartbeat-ms", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[++i], out int parsedHeartbeatMs) || parsedHeartbeatMs <= 0)
                {
                    throw new ArgumentException("Expected '--heartbeat-ms <positive-int>'.");
                }

                heartbeatMs = parsedHeartbeatMs;
                continue;
            }

            if (arg.Equals("--iterations", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length ||
                    !TryParsePositiveUInt64(args[++i], out ulong parsedIterations))
                {
                    throw new ArgumentException("Expected '--iterations <positive-int>'.");
                }

                workloadIterations = parsedIterations;
                continue;
            }

            if (arg.Equals("--telemetry-logs", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--telemetry", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !TryParseTelemetryLogMode(args[++i], out DiagnosticTelemetryLogMode parsedMode))
                {
                    throw new ArgumentException("Expected '--telemetry-logs <minimal|extended>'.");
                }

                telemetryLogMode = parsedMode;
                continue;
            }

            if (arg.Equals("--minimal-logs", StringComparison.OrdinalIgnoreCase))
            {
                telemetryLogMode = DiagnosticTelemetryLogMode.Minimal;
                continue;
            }

            if (arg.Equals("--extended-telemetry", StringComparison.OrdinalIgnoreCase))
            {
                telemetryLogMode = DiagnosticTelemetryLogMode.Extended;
                continue;
            }

            if (command is null)
            {
                command = arg;
                continue;
            }

            throw new ArgumentException($"Unexpected argument '{arg}'.");
        }

        return new ParentCommandOptions(command, timeoutMs, heartbeatMs, workloadIterations, telemetryLogMode, shadowFrontendMode);
    }

    private static DiagnosticRunProfile ApplyOverrides(DiagnosticRunProfile profile, ParentCommandOptions options)
    {
        ulong workloadIterations = options.WorkloadIterations ?? profile.WorkloadIterations;
        return profile with
        {
            WallClockTimeoutMs = options.TimeoutMs ?? ComputeAutoTimeoutMs(profile.WallClockTimeoutMs, workloadIterations),
            HeartbeatIntervalMs = options.HeartbeatMs ?? profile.HeartbeatIntervalMs,
            WorkloadIterations = workloadIterations,
            TelemetryLogMode = options.TelemetryLogMode ?? profile.TelemetryLogMode,
            ShadowFrontendMode = options.ShadowFrontendMode
        };
    }

    private static IReadOnlyList<DiagnosticRunProfile> ApplyOverrides(
        IReadOnlyList<DiagnosticRunProfile> profiles,
        ParentCommandOptions options)
    {
        return profiles.Select(profile => ApplyOverrides(profile, options)).ToArray();
    }

    private static ParentCommandOptions ResolveIterationBudget(ParentCommandOptions options)
    {
        if (options.WorkloadIterations.HasValue)
        {
            PrintIterationBudget(options.WorkloadIterations.Value, explicitOverride: true, options.TimeoutMs.HasValue);
            return options;
        }

        ulong suggestedIterations = SuggestDefaultIterations(options.Command);
        ulong resolvedIterations = PromptForWorkloadIterations(options.Command, suggestedIterations);
        PrintIterationBudget(resolvedIterations, explicitOverride: false, options.TimeoutMs.HasValue);
        return options with
        {
            WorkloadIterations = resolvedIterations
        };
    }

    private static ParentCommandOptions ResolveTelemetryLogMode(ParentCommandOptions options)
    {
        if (options.TelemetryLogMode.HasValue)
        {
            PrintTelemetryLogMode(options.TelemetryLogMode.Value, explicitOverride: true);
            return options;
        }

        DiagnosticTelemetryLogMode resolvedMode = PromptForTelemetryLogMode();
        PrintTelemetryLogMode(resolvedMode, explicitOverride: false);
        return options with
        {
            TelemetryLogMode = resolvedMode
        };
    }

    private static DiagnosticTelemetryLogMode PromptForTelemetryLogMode()
    {
        if (Console.IsInputRedirected)
        {
            return DiagnosticTelemetryLogMode.Minimal;
        }

        while (true)
        {
            Console.Write("Enable extended telemetry logging? This writes heartbeat history and partial telemetry files. [y/N]: ");
            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
            {
                return DiagnosticTelemetryLogMode.Minimal;
            }

            string normalized = input.Trim().ToLowerInvariant();
            if (normalized is "y" or "yes")
            {
                return DiagnosticTelemetryLogMode.Extended;
            }

            if (normalized is "n" or "no")
            {
                return DiagnosticTelemetryLogMode.Minimal;
            }

            Console.WriteLine("Enter 'y' for extended telemetry or 'n' for minimal logs.");
        }
    }

    private static void PrintTelemetryLogMode(DiagnosticTelemetryLogMode telemetryLogMode, bool explicitOverride)
    {
        Console.WriteLine($"Telemetry logging: {telemetryLogMode}");
        Console.WriteLine(telemetryLogMode == DiagnosticTelemetryLogMode.Extended
            ? "Extended telemetry will write heartbeat.ndjson plus partial telemetry/replay snapshots."
            : "Minimal logging will keep console-equivalent stdout/stderr, manifests, result metrics, and the latest heartbeat only.");
        if (explicitOverride)
        {
            Console.WriteLine("Telemetry logging mode was selected from command-line options.");
        }

        Console.WriteLine();
    }

    private static ulong SuggestDefaultIterations(string? command)
    {
        string normalized = command is null ? string.Empty : NormalizeCommand(command);
        if (normalized is "replay" or "replay-phase" or "replay-pair")
        {
            return 1_000_000UL;
        }

        if (normalized is "stream-vector" or "stream-suite" or "vector-suite" or "spec-stream" or "spec-stream-vector")
        {
            return 250UL;
        }

        if (string.IsNullOrEmpty(normalized) ||
            IsMatrixSmokeCommand(normalized) ||
            IsMatrixRuntimeCommand(normalized) ||
            IsMatrixMemoryCommand(normalized) ||
            IsMatrixFullCommand(normalized) ||
            IsMatrixWideCommand(normalized) ||
            IsMatrixSpecCommand(normalized) ||
            IsWhiteBookSmokeCommand(normalized) ||
            IsWhiteBookFullCommand(normalized))
        {
            return 250UL;
        }

        return 1_000UL;
    }

    private static ulong PromptForWorkloadIterations(string? command, ulong suggestedIterations)
    {
        if (Console.IsInputRedirected)
        {
            return suggestedIterations;
        }

        string scopeLabel = string.IsNullOrWhiteSpace(command)
            ? "default SPEC-like matrix"
            : command;

        while (true)
        {
            Console.Write($"SPEC-like iterations for {scopeLabel} [{suggestedIterations:N0}]: ");
            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
            {
                return suggestedIterations;
            }

            if (TryParsePositiveUInt64(input, out ulong parsedIterations))
            {
                return parsedIterations;
            }

            Console.WriteLine("Enter a positive integer, for example 1000000.");
        }
    }

    private static void PrintIterationBudget(ulong workloadIterations, bool explicitOverride, bool explicitTimeout)
    {
        Console.WriteLine($"Configured SPEC-like iterations: {workloadIterations:N0}");
        if (!explicitTimeout)
        {
            Console.WriteLine(explicitOverride
                ? "Wall-clock budgets will be auto-scaled from the requested iteration count."
                : "Wall-clock budgets will be auto-scaled from the prompted iteration count.");
        }

        Console.WriteLine();
    }

    private static bool TryParsePositiveUInt64(string value, out ulong parsed)
    {
        string normalized = value
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal);

        return ulong.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
               && parsed > 0;
    }

    private static bool TryParseTelemetryLogMode(string value, out DiagnosticTelemetryLogMode telemetryLogMode)
    {
        string normalized = value.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "minimal":
            case "min":
            case "none":
            case "off":
            case "false":
            case "0":
                telemetryLogMode = DiagnosticTelemetryLogMode.Minimal;
                return true;

            case "extended":
            case "full":
            case "verbose":
            case "on":
            case "true":
            case "1":
                telemetryLogMode = DiagnosticTelemetryLogMode.Extended;
                return true;

            default:
                telemetryLogMode = DiagnosticTelemetryLogMode.Minimal;
                return false;
        }
    }

    private static int ComputeAutoTimeoutMs(int baseTimeoutMs, ulong workloadIterations)
    {
        const double BaselineIterations = 100d;
        if (workloadIterations <= (ulong)BaselineIterations)
        {
            return baseTimeoutMs;
        }

        double scale = (double)workloadIterations / BaselineIterations;
        double scaledTimeout = baseTimeoutMs * Math.Clamp(scale, 1.0d, 512.0d);
        return scaledTimeout >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Ceiling(scaledTimeout);
    }

    private static string NormalizeCommand(string arg)
    {
        string trimmed = arg.Trim();
        while (trimmed.StartsWith("-", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..];
        }

        return trimmed.ToLowerInvariant();
    }

    private static void PrintBanner()
    {
        Console.WriteLine("HybridCPU ISE diagnostics console");
        Console.WriteLine("Primary runtime validation harness starting...");
        Console.WriteLine();
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: TestAssemblerConsoleApps [command] [--iterations N] [--timeout-ms N] [--heartbeat-ms N] [--telemetry-logs minimal|extended] [--shadow-frontend off|observe|cold-warm]");
        Console.WriteLine("ShadowFrontend: off (default), observe (compare), cold-warm (serving opt-in); HOT is disabled. Snapshots: shadow_frontend.json.");
        Console.WriteLine("No arguments: runs the default SPEC-like diagnostic matrix and asks for the loop-iteration budget at startup.");
        Console.WriteLine("Single profiles: showcase, showcase-long, vt, novt, alu, max, lk, lk-long, bnmcz, bnmcz-long, replay, safety, replay-reuse, assistant, stream-vector, matrix-tile, whitebook-contract.");
        Console.WriteLine("Architectural aliases: showcase-runtime, vt-fsp, spec-rate, single-thread-vector, spec-vector, scalar-baseline, spec-int, throughput-max, spec-mix, bank-pressure-lh, spec-mem-lh, bank-pressure-bnmcz, spec-mem-bank, replay-phase, safety-verifier, replay-template, assistant-matrix, spec-stream-vector, mtile, spec-matrix-tile, stream-whitebook-contract.");
        Console.WriteLine("Comparisons: banks, banks-long, compare-banks, compare-baseline-fsp.");
        Console.WriteLine("Matrices: matrix-smoke, matrix-runtime, matrix-memory, matrix-full, matrix-wide, matrix-spec, whitebook-smoke, whitebook-full.");
        Console.WriteLine("Overrides: --iterations fixes the SPEC-like loop budget without prompting, --timeout-ms overrides auto-scaled wall-clock budgets, --heartbeat-ms changes live checkpoint cadence.");
        Console.WriteLine("Telemetry logging: --minimal-logs keeps compact artifacts; --extended-telemetry or --telemetry-logs extended enables heartbeat history and partial telemetry snapshots.");
    }

    private sealed record DiagnosticBatchRunResult(
        string CommandId,
        string DisplayName,
        string ArtifactDirectory,
        DiagnosticRunStatus Status,
        IReadOnlyList<DiagnosticRunResult> Results,
        string? FailureMessage);

    private sealed record ParentCommandOptions(
        string? Command,
        int? TimeoutMs,
        int? HeartbeatMs,
        ulong? WorkloadIterations,
        DiagnosticTelemetryLogMode? TelemetryLogMode,
        ShadowFrontendMode ShadowFrontendMode);

    private static string InferLikelyBlockedPhase(string? checkpointStage)
    {
        return checkpointStage switch
        {
            "ProgramInitialization" => "memory seeding or early workload setup",
            "MemorySeeded" => "instruction emission",
            "InstructionEmission" => "IR build",
            "CanonicalCompileStarting" => "IR build",
            "IrBuildStarting" => "IR build",
            "IrBuild" => "scheduler setup",
            "ScheduleStarting" => "scheduler",
            "ScheduleFallback" => "program-order scheduler fallback",
            "Schedule" => "bundle materialization",
            "BundleMaterializationStarting" => "bundle materialization",
            "BundleMaterialization" => "bundle lowering",
            "LoweringStarting" => "bundle lowering",
            "Lowering" => "bundle serialization",
            "SerializationStarting" => "bundle serialization",
            "Serialization" => "bundle emission",
            "CanonicalCompile" => "bundle emission",
            "BundleEmissionStarting" => "memory write",
            "MemoryWriteStarting" => "memory write",
            "MemoryWrite" => "fetch-state invalidation",
            "FetchStateInvalidationStarting" => "fetch-state invalidation",
            "FetchStateInvalidation" => "bundle annotation publication",
            "BundleEmission" => "bundle annotation publication",
            "BundleAnnotationPublishStarting" => "bundle annotation publication",
            "BundleAnnotationPublish" => "pipeline setup or decode loop",
            "PipelineDecodeLoop" => "pipeline decode loop",
            "PipelineDrainStarting" => "post-image pipeline drain",
            "PipelineDrainCompleted" => "post-image pipeline drain",
            "ShowcaseRuntimeProbes" => "showcase runtime probes",
            _ => "the phase immediately after the last published checkpoint"
        };
    }
}
