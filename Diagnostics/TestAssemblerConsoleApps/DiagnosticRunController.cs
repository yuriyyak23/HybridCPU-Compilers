using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed class DiagnosticRunController
{
    private readonly string _entryAssemblyPath;

    public DiagnosticRunController()
    {
        _entryAssemblyPath = Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrWhiteSpace(_entryAssemblyPath))
        {
            throw new InvalidOperationException("Unable to resolve the current diagnostics assembly path for bounded orchestration.");
        }
    }

    public string CreateArtifactDirectory(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        string root = Path.Combine(Environment.CurrentDirectory, "TestResults", "TestAssemblerConsoleApps");
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        return Path.Combine(root, $"{timestamp}_{commandId}");
    }

    public DiagnosticRunResult Execute(DiagnosticRunProfile profile)
    {
        return Execute(profile, CreateArtifactDirectory(profile.Id));
    }

    public DiagnosticRunResult Execute(DiagnosticRunProfile profile, string artifactDirectory)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);

        DiagnosticArtifactWriter writer = new DiagnosticArtifactWriter(artifactDirectory);
        writer.EnsureDirectory();
        writer.WriteJson("profile.json", profile);

        string stdoutPath = writer.GetPath("stdout.log");
        string stderrPath = writer.GetPath("stderr.log");
        string metricsPath = writer.GetPath("metrics.json");
        string timingMemoryReportPath = writer.GetPath(TimingMemoryReport.ArtifactFileName);
        string legacyTimingMemoryReportPath = writer.GetPath(TimingMemoryReport.LegacyArtifactFileName);
        string replayPath = writer.GetPath("replay_report.json");
        string safetyNegativeControlsPath = writer.GetPath("safety_verifier_negative_controls.json");
        string replayReusePath = writer.GetPath("replay_reuse_report.json");
        string assistantMatrixPath = writer.GetPath("assistant_matrix_report.json");
        string streamVectorSpecPath = writer.GetPath("stream_vector_spec_report.json");
        string matrixTileSpecPath = writer.GetPath("matrix_tile_spec_report.json");
        string whiteBookContractPath = writer.GetPath("whitebook_contract_report.json");
        string heartbeatPath = writer.GetPath("heartbeat.json");
        string heartbeatHistoryPath = writer.GetPath("heartbeat.ndjson");
        string heartbeatSummaryPath = writer.GetPath("heartbeat_summary.txt");
        string partialTelemetryPath = writer.GetPath("telemetry_profile.partial.json");
        string partialReplayTokenPath = writer.GetPath("replay_token.partial.json");

        Dictionary<string, string> generatedFiles = new Dictionary<string, string>
        {
            ["stderr"] = "stderr.log",
            ["profile"] = "profile.json",
            ["stdout"] = "stdout.log",
        };

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        DiagnosticArtifactManifest runningManifest = new DiagnosticArtifactManifest
        {
            RunKind = "single",
            CommandId = profile.Id,
            DisplayName = profile.DisplayName,
            Status = DiagnosticRunStatus.Running.ToString(),
            ArtifactDirectory = artifactDirectory,
            WorkerProfileId = profile.Id,
            WorkloadKind = profile.WorkloadKind.ToString(),
            FrontendProfile = profile.FrontendMode.ToString(),
            ShadowFrontendMode = profile.ShadowFrontendMode.ToString(),
            StartedUtc = startedUtc,
            GeneratedFiles = new Dictionary<string, string>(generatedFiles)
        };
        writer.WriteManifest(runningManifest);

        using Process process = StartWorkerProcess(profile, artifactDirectory);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Stopwatch stopwatch = Stopwatch.StartNew();

        bool timedOut = !process.WaitForExit(profile.WallClockTimeoutMs);
        if (timedOut)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process already exited between timeout and kill attempt.
            }

            process.WaitForExit();
        }

        stopwatch.Stop();

        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        File.WriteAllText(stdoutPath, stdout);
        File.WriteAllText(stderrPath, stderr);

        DiagnosticArtifactManifest manifest = writer.TryReadManifest() ?? runningManifest;
        DiagnosticRunStatus status = ParseStatus(manifest.Status);
        DiagnosticExecutionCheckpoint? lastCheckpoint = File.Exists(heartbeatPath)
            ? writer.TryReadJson<DiagnosticExecutionCheckpoint>("heartbeat.json")
            : null;
        int? exitCode = process.HasExited ? process.ExitCode : null;
        if (timedOut)
        {
            status = DiagnosticRunStatus.TimedOut;
            exitCode = 124;
            manifest = manifest with
            {
                Status = status.ToString(),
                ExitCode = exitCode,
                FailureMessage = BuildTimeoutFailureMessage(profile, lastCheckpoint),
                FinishedUtc = DateTimeOffset.UtcNow,
                ElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
                ProcessId = process.Id,
                GeneratedFiles = new Dictionary<string, string>(generatedFiles)
            };
            writer.WriteManifest(manifest);
        }
        else if (status == DiagnosticRunStatus.Running || status == DiagnosticRunStatus.Pending)
        {
            status = exitCode == 0 ? DiagnosticRunStatus.Succeeded : DiagnosticRunStatus.Failed;
            manifest = manifest with
            {
                Status = status.ToString(),
                ExitCode = exitCode,
                FinishedUtc = DateTimeOffset.UtcNow,
                ElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
                ProcessId = process.Id,
                GeneratedFiles = new Dictionary<string, string>(generatedFiles)
            };
            writer.WriteManifest(manifest);
        }

        RegisterGeneratedFile(generatedFiles, metricsPath, "metrics", "metrics.json");
        RegisterGeneratedFile(generatedFiles, writer.GetPath(DiagnosticShadowFrontend.ArtifactFileName), "shadow_frontend", DiagnosticShadowFrontend.ArtifactFileName);
        RegisterGeneratedFile(
            generatedFiles,
            timingMemoryReportPath,
            TimingMemoryReport.ManifestKey,
            TimingMemoryReport.ArtifactFileName);
        RegisterGeneratedFile(
            generatedFiles,
            legacyTimingMemoryReportPath,
            TimingMemoryReport.LegacyManifestKey,
            TimingMemoryReport.LegacyArtifactFileName);
        RegisterGeneratedFile(generatedFiles, replayPath, "replay_report", "replay_report.json");
        RegisterGeneratedFile(generatedFiles, safetyNegativeControlsPath, "safety_verifier_negative_controls", "safety_verifier_negative_controls.json");
        RegisterGeneratedFile(generatedFiles, replayReusePath, "replay_reuse_report", "replay_reuse_report.json");
        RegisterGeneratedFile(generatedFiles, assistantMatrixPath, "assistant_matrix_report", "assistant_matrix_report.json");
        RegisterGeneratedFile(generatedFiles, streamVectorSpecPath, "stream_vector_spec_report", "stream_vector_spec_report.json");
        RegisterGeneratedFile(generatedFiles, matrixTileSpecPath, "matrix_tile_spec_report", "matrix_tile_spec_report.json");
        RegisterGeneratedFile(generatedFiles, whiteBookContractPath, "whitebook_contract_report", "whitebook_contract_report.json");
        RegisterGeneratedFile(generatedFiles, heartbeatPath, "heartbeat", "heartbeat.json");
        RegisterGeneratedFile(generatedFiles, heartbeatHistoryPath, "heartbeat_history", "heartbeat.ndjson");
        RegisterGeneratedFile(generatedFiles, heartbeatSummaryPath, "heartbeat_summary", "heartbeat_summary.txt");
        RegisterGeneratedFile(generatedFiles, partialTelemetryPath, "telemetry_profile_partial", "telemetry_profile.partial.json");
        RegisterGeneratedFile(generatedFiles, partialReplayTokenPath, "replay_token_partial", "replay_token.partial.json");

        if (timedOut)
        {
            WriteTimeoutArtifacts(writer, manifest, lastCheckpoint, stdout, stderr, generatedFiles);
        }

        manifest = manifest with
        {
            GeneratedFiles = new Dictionary<string, string>(generatedFiles)
        };
        writer.WriteManifest(manifest);

        SimpleAsmAppMetrics? metrics = File.Exists(metricsPath)
            ? writer.TryReadJson<SimpleAsmAppMetrics>("metrics.json")
            : null;
        ReplayPhaseBenchmarkPairReport? replayReport = File.Exists(replayPath)
            ? writer.TryReadJson<ReplayPhaseBenchmarkPairReport>("replay_report.json")
            : null;

        return new DiagnosticRunResult(
            profile,
            status,
            artifactDirectory,
            exitCode,
            stopwatch.Elapsed,
            manifest,
            metrics,
            replayReport,
            lastCheckpoint);
    }

    private Process StartWorkerProcess(DiagnosticRunProfile profile, string artifactDirectory)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(_entryAssemblyPath);
        startInfo.ArgumentList.Add("--worker");
        startInfo.ArgumentList.Add(profile.Id);
        startInfo.ArgumentList.Add("--artifact-dir");
        startInfo.ArgumentList.Add(artifactDirectory);

        Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"Failed to start bounded worker process for profile '{profile.Id}'.");
        }

        return process;
    }

    private static DiagnosticRunStatus ParseStatus(string? status)
    {
        return Enum.TryParse(status, ignoreCase: true, out DiagnosticRunStatus parsed)
            ? parsed
            : DiagnosticRunStatus.Failed;
    }

    private static void RegisterGeneratedFile(
        Dictionary<string, string> generatedFiles,
        string absolutePath,
        string key,
        string relativeName)
    {
        if (File.Exists(absolutePath))
        {
            generatedFiles[key] = relativeName;
        }
    }

    private static void WriteTimeoutArtifacts(
        DiagnosticArtifactWriter writer,
        DiagnosticArtifactManifest manifest,
        DiagnosticExecutionCheckpoint? lastCheckpoint,
        string stdout,
        string stderr,
        Dictionary<string, string> generatedFiles)
    {
        string stdoutTail = ExtractTail(stdout, 80);
        string stderrTail = ExtractTail(stderr, 80);

        if (!string.IsNullOrWhiteSpace(stdoutTail))
        {
            writer.WriteText("stdout_tail.txt", stdoutTail);
            generatedFiles["stdout_tail"] = "stdout_tail.txt";
        }

        if (!string.IsNullOrWhiteSpace(stderrTail))
        {
            writer.WriteText("stderr_tail.txt", stderrTail);
            generatedFiles["stderr_tail"] = "stderr_tail.txt";
        }

        DiagnosticTimeoutPartialDump timeoutDump = new DiagnosticTimeoutPartialDump
        {
            CommandId = manifest.CommandId,
            DisplayName = manifest.DisplayName,
            ArtifactDirectory = manifest.ArtifactDirectory,
            Status = manifest.Status,
            CapturedUtc = DateTimeOffset.UtcNow,
            Manifest = manifest,
            LastCheckpoint = lastCheckpoint,
            StdoutTail = string.IsNullOrWhiteSpace(stdoutTail) ? null : stdoutTail,
            StderrTail = string.IsNullOrWhiteSpace(stderrTail) ? null : stderrTail,
            ArtifactFileSizes = CaptureArtifactFileSizes(writer.ArtifactDirectory)
        };

        writer.WriteJson("timeout_partial_dump.json", timeoutDump);
        generatedFiles["timeout_partial_dump"] = "timeout_partial_dump.json";
        writer.WriteText("timeout_summary.txt", BuildTimeoutSummary(timeoutDump));
        generatedFiles["timeout_summary"] = "timeout_summary.txt";
    }

    private static Dictionary<string, long> CaptureArtifactFileSizes(string artifactDirectory)
    {
        return Directory.GetFiles(artifactDirectory, "*", SearchOption.TopDirectoryOnly)
            .ToDictionary(
                path => Path.GetFileName(path),
                path => new FileInfo(path).Length,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string ExtractTail(string content, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        string[] lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.None);

        int takeCount = Math.Min(maxLines, lines.Length);
        return string.Join(Environment.NewLine, lines.Skip(lines.Length - takeCount));
    }

    private static string BuildTimeoutSummary(DiagnosticTimeoutPartialDump timeoutDump)
    {
        StringBuilder summary = new StringBuilder();
        summary.AppendLine($"Command: {timeoutDump.CommandId}");
        summary.AppendLine($"Display name: {timeoutDump.DisplayName}");
        summary.AppendLine($"Status: {timeoutDump.Status}");
        summary.AppendLine($"Captured: {timeoutDump.CapturedUtc:O}");
        summary.AppendLine($"Artifacts: {timeoutDump.ArtifactDirectory}");

        if (timeoutDump.LastCheckpoint is { } checkpoint)
        {
            summary.AppendLine($"Last checkpoint stage: {checkpoint.Stage}");
            summary.AppendLine($"Last checkpoint reason: {checkpoint.Reason}");
            if (checkpoint.ObservedCycleCount.HasValue)
            {
                summary.AppendLine($"Observed cycles: {checkpoint.ObservedCycleCount.Value}");
            }

            if (checkpoint.ObservedInstructionsRetired.HasValue)
            {
                summary.AppendLine($"Observed retired instructions: {checkpoint.ObservedInstructionsRetired.Value}");
            }

            if (checkpoint.ActiveVirtualThreadId.HasValue)
            {
                summary.AppendLine($"Active VT: {checkpoint.ActiveVirtualThreadId.Value}");
            }

            if (checkpoint.ActiveLivePc.HasValue)
            {
                summary.AppendLine($"Active PC: 0x{checkpoint.ActiveLivePc.Value:X}");
            }

            if (!string.IsNullOrWhiteSpace(checkpoint.CurrentState))
            {
                summary.AppendLine($"Current state: {checkpoint.CurrentState}");
            }
        }

        return summary.ToString();
    }

    private static string BuildTimeoutFailureMessage(
        DiagnosticRunProfile profile,
        DiagnosticExecutionCheckpoint? lastCheckpoint)
    {
        string baseMessage = $"Worker exceeded wall-clock timeout of {profile.WallClockTimeoutMs} ms.";
        if (lastCheckpoint is null)
        {
            return baseMessage;
        }

        string likelyBlockedPhase = InferLikelyBlockedPhase(lastCheckpoint.Stage);
        return $"{baseMessage} Last checkpoint: {lastCheckpoint.Stage} ({lastCheckpoint.Reason}). Likely blocked in {likelyBlockedPhase}.";
    }

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
