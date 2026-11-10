using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridCPU.Compiler.Core.IR.Telemetry;

const string toolVersion = "HybridCPU.Compiler.Phase00RuntimeControls/v1";
Dictionary<string, string?> options = ParseOptions(args);
string repositoryRoot = Path.GetFullPath(Require(options, "--repo-root"));
string evidenceRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "HybridCPU_Compiler", "RefPlan3", "evidence"));
string outputDirectory = Path.GetFullPath(Require(options, "--output-dir"));
EnsurePathWithin(outputDirectory, evidenceRoot);
string configuration = options.GetValueOrDefault("--configuration") ?? "Debug";
int repetitions = ParsePositiveInt(options.GetValueOrDefault("--repetitions") ?? "3", "--repetitions");
bool allowDirtyAttributed = options.ContainsKey("--allow-dirty-attributed");
bool overwrite = options.ContainsKey("--overwrite");

if (Directory.Exists(outputDirectory) &&
    Directory.EnumerateFileSystemEntries(outputDirectory).Any() &&
    !overwrite)
{
    throw new IOException(
        $"Output directory '{outputDirectory}' already contains artifacts. " +
        "Pass --overwrite to replace the same bounded artifact layout deliberately.");
}

string runnerAssembly = Path.Combine(
    repositoryRoot,
    "TestAssemblerConsoleApps",
    "bin",
    configuration,
    "net10.0",
    "TestAssemblerConsoleApps.dll");
if (!File.Exists(runnerAssembly))
{
    throw new FileNotFoundException(
        "Build TestAssemblerConsoleApps for the requested configuration before capture.",
        runnerAssembly);
}

string baseSha = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
string branch = RunGit(repositoryRoot, "branch", "--show-current").Trim();
string scopedStatus = RunGit(
    repositoryRoot,
    "status",
    "--porcelain=v1",
    "--untracked-files=all",
    "--",
    "HybridCPU_Compiler",
    "HybridCPU_ISE",
    "TestAssemblerConsoleApps",
    "HybridCPU_ISE.Tests/CompilerTests/CompilerRefPlan3Phase00AMetricsTests.cs",
    "HybridCPU_ISE.Tests/CompilerTests/CompilerRefPlan3Phase00EvidenceTests.cs");
CompilerSubjectDispositionV1 disposition = string.IsNullOrWhiteSpace(scopedStatus)
    ? CompilerSubjectDispositionV1.Clean
    : allowDirtyAttributed
        ? CompilerSubjectDispositionV1.DirtyAttributed
        : CompilerSubjectDispositionV1.DirtyUnattributed;
if (disposition == CompilerSubjectDispositionV1.DirtyUnattributed)
{
    throw new InvalidOperationException(
        "The runtime-control subject is dirty. Review the exact source scope and pass " +
        "--allow-dirty-attributed; unattributed subjects fail closed.");
}

CompilerSourceFileHashV1[] sourceFiles = CaptureSourceFiles(repositoryRoot);
string sourceManifestFingerprint = ComputeSourceManifestFingerprint(baseSha, branch, disposition, sourceFiles);
Directory.CreateDirectory(outputDirectory);

ControlProfile[] profiles =
[
    new("replay", "Replay Phase Pair (SPEC-like scheduler certificate slice)", 1, 1_000_000UL, 120_000),
    new("safety", "SafetyVerifier Negative Controls", 2, 1UL, 60_000),
    new("stream-vector", "Stream/Vector SPEC-like Suite", 5, 250UL, 180_000),
    new("matrix-tile", "MatrixTile SPEC-like Pressure Suite", 6, 500UL, 300_000)
];

List<CompilerPhase00RuntimeControlProfileV1> deterministicProfiles = new List<CompilerPhase00RuntimeControlProfileV1>(profiles.Length);
List<CompilerPhase00RuntimeControlResourceProfileV1> resourceProfiles = new List<CompilerPhase00RuntimeControlResourceProfileV1>(profiles.Length);
foreach (ControlProfile profile in profiles)
{
    List<CompilerPhase00RuntimeControlSampleV1> deterministicSamples = new List<CompilerPhase00RuntimeControlSampleV1>(repetitions);
    List<CompilerPhase00RuntimeControlResourceSampleV1> resourceSamples = new List<CompilerPhase00RuntimeControlResourceSampleV1>(repetitions);
    for (int repetition = 1; repetition <= repetitions; repetition++)
    {
        string relativeRunDirectory = $"raw/{profile.Id}/run-{repetition:D3}";
        string runDirectory = Path.Combine(outputDirectory, relativeRunDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(runDirectory);
        WriteProfile(runDirectory, profile);

        ProcessResult processResult = RunWorker(runnerAssembly, profile, runDirectory);
        string standardOutputPath = Path.Combine(runDirectory, "stdout.log");
        string standardErrorPath = Path.Combine(runDirectory, "stderr.log");
        File.WriteAllText(standardOutputPath, processResult.StandardOutput);
        File.WriteAllText(standardErrorPath, processResult.StandardError);

        string rawReportName = ReportName(profile.Id);
        string rawReportPath = Path.Combine(runDirectory, rawReportName);
        if (!File.Exists(rawReportPath))
        {
            throw new InvalidOperationException(
                $"Control '{profile.Id}' repetition {repetition} did not produce '{rawReportName}'. " +
                $"Exit={processResult.ExitCode}, timeout={processResult.TimedOut}. stderr={processResult.StandardError}");
        }

        byte[] rawReport = File.ReadAllBytes(rawReportPath);
        byte[] normalizedOutcome = CompilerPhase00RuntimeControlEvidenceV1.NormalizeOutcomeJson(rawReport);
        string normalizedName = "normalized_outcome.json";
        string normalizedPath = Path.Combine(runDirectory, normalizedName);
        File.WriteAllBytes(normalizedPath, normalizedOutcome);
        bool reportSucceeded = ReadSucceeded(rawReport, profile.Id);
        bool succeeded = !processResult.TimedOut && processResult.ExitCode == 0 && reportSucceeded;
        string normalizedFingerprint = CompilerPhase00RuntimeControlEvidenceV1.ComputeSha256(normalizedOutcome);

        deterministicSamples.Add(new CompilerPhase00RuntimeControlSampleV1(
            repetition,
            succeeded,
            processResult.ExitCode,
            normalizedFingerprint,
            $"{relativeRunDirectory}/{normalizedName}"));
        resourceSamples.Add(new CompilerPhase00RuntimeControlResourceSampleV1(
            repetition,
            processResult.Elapsed.TotalMilliseconds,
            $"{relativeRunDirectory}/{rawReportName}",
            Hash(rawReport),
            Hash(File.ReadAllBytes(standardOutputPath)),
            Hash(File.ReadAllBytes(standardErrorPath))));
    }

    string[] distinctOutcomes = deterministicSamples
        .Select(static sample => sample.NormalizedOutcomeFingerprint)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    deterministicProfiles.Add(new CompilerPhase00RuntimeControlProfileV1(
        profile.Id,
        profile.Iterations,
        repetitions,
        deterministicSamples.All(static sample => sample.Succeeded),
        distinctOutcomes.Length == 1,
        distinctOutcomes.Length == 1 ? distinctOutcomes[0] : null,
        deterministicSamples.AsReadOnly()));
    resourceProfiles.Add(new CompilerPhase00RuntimeControlResourceProfileV1(
        profile.Id,
        CompilerPhase00RuntimeControlEvidenceV1.CreateTimingDistribution(
            resourceSamples.Select(static sample => sample.ProcessElapsedMilliseconds).ToArray()),
        resourceSamples.AsReadOnly()));
}

CompilerPhase00RuntimeControlReportV1 deterministicReport = new CompilerPhase00RuntimeControlReportV1(
    CompilerPhase00RuntimeControlReportV1.SchemaName,
    baseSha,
    branch,
    disposition,
    configuration,
    toolVersion,
    sourceManifestFingerprint,
    Array.AsReadOnly(sourceFiles),
    deterministicProfiles.AsReadOnly());
CompilerPhase00RuntimeControlResourceReportV1 resourceReport = new CompilerPhase00RuntimeControlResourceReportV1(
    CompilerPhase00RuntimeControlResourceReportV1.SchemaName,
    resourceProfiles.AsReadOnly());

JsonSerializerOptions jsonOptions = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = true
};
File.WriteAllBytes(
    Path.Combine(outputDirectory, "compiler_phase00_runtime_controls_v1.json"),
    JsonSerializer.SerializeToUtf8Bytes(deterministicReport, jsonOptions));
File.WriteAllBytes(
    Path.Combine(outputDirectory, "compiler_phase00_runtime_control_resources_v1.json"),
    JsonSerializer.SerializeToUtf8Bytes(resourceReport, jsonOptions));
File.WriteAllBytes(
    Path.Combine(outputDirectory, "compiler_phase00_runtime_control_invocation_v1.json"),
    JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = "CompilerPhase00RuntimeControlInvocationV1",
        toolVersion,
        baseSha,
        branch,
        subjectDisposition = disposition.ToString(),
        configuration,
        repetitions,
        sourceFileCount = sourceFiles.Length,
        sourceManifestFingerprint,
        runnerAssembly = Path.GetRelativePath(repositoryRoot, runnerAssembly).Replace('\\', '/'),
        profiles = profiles.Select(static profile => new
        {
            profile.Id,
            profile.Iterations,
            profile.TimeoutMilliseconds
        })
    }, jsonOptions));

Console.WriteLine($"Captured {profiles.Length} runtime controls x {repetitions} repetitions.");
Console.WriteLine($"Subject: {baseSha} ({disposition}), {sourceFiles.Length} exact source files.");
Console.WriteLine($"Deterministic outcomes: {deterministicReport.Succeeded}.");
Console.WriteLine($"Artifacts: {outputDirectory}");
return deterministicReport.Succeeded ? 0 : 1;

static ProcessResult RunWorker(string runnerAssembly, ControlProfile profile, string runDirectory)
{
    ProcessStartInfo startInfo = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = runDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add(runnerAssembly);
    startInfo.ArgumentList.Add("--worker");
    startInfo.ArgumentList.Add(profile.Id);
    startInfo.ArgumentList.Add("--artifact-dir");
    startInfo.ArgumentList.Add(runDirectory);

    using Process process = Process.Start(startInfo) ??
        throw new InvalidOperationException($"Unable to start runtime-control worker '{profile.Id}'.");
    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
    Stopwatch stopwatch = Stopwatch.StartNew();
    bool timedOut = !process.WaitForExit(profile.TimeoutMilliseconds);
    if (timedOut)
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
    }

    stopwatch.Stop();
    return new ProcessResult(
        process.ExitCode,
        timedOut,
        stopwatch.Elapsed,
        stdoutTask.GetAwaiter().GetResult(),
        stderrTask.GetAwaiter().GetResult());
}

static void WriteProfile(string runDirectory, ControlProfile profile)
{
    byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
    {
        profile.Id,
        profile.DisplayName,
        WorkloadKind = profile.WorkloadKind,
        FrontendMode = 0,
        WallClockTimeoutMs = profile.TimeoutMilliseconds,
        SimpleAsmMode = (int?)null,
        HeartbeatIntervalMs = 1000,
        HeartbeatCycleStride = 2048UL,
        WorkloadIterations = profile.Iterations,
        TelemetryLogMode = 0
    }, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllBytes(Path.Combine(runDirectory, "profile.json"), json);
}

static string ReportName(string profileId) => profileId switch
{
    "replay" => "replay_report.json",
    "safety" => "safety_verifier_negative_controls.json",
    "stream-vector" => "stream_vector_spec_report.json",
    "matrix-tile" => "matrix_tile_spec_report.json",
    _ => throw new ArgumentOutOfRangeException(nameof(profileId), profileId, "Unknown runtime control.")
};

static bool ReadSucceeded(ReadOnlySpan<byte> rawReport, string profileId)
{
    using JsonDocument document = JsonDocument.Parse(rawReport.ToArray());
    if (profileId == "replay")
    {
        return document.RootElement.TryGetProperty("StablePhase", out _) &&
            document.RootElement.TryGetProperty("RotatingPhase", out _);
    }

    return document.RootElement.TryGetProperty("Succeeded", out JsonElement succeeded) &&
        succeeded.ValueKind == JsonValueKind.True;
}

static CompilerSourceFileHashV1[] CaptureSourceFiles(string repositoryRoot)
{
    SortedSet<string> relativePaths = new SortedSet<string>(StringComparer.Ordinal);
    AddBuildSources(relativePaths, repositoryRoot, "HybridCPU_Compiler", excludeEvidence: true);
    AddBuildSources(relativePaths, repositoryRoot, "HybridCPU_ISE", excludeEvidence: false);
    AddBuildSources(relativePaths, repositoryRoot, "TestAssemblerConsoleApps", excludeEvidence: false);
    AddIfPresent(relativePaths, repositoryRoot, "HybridCPU_ISE.Tests/CompilerTests/CompilerRefPlan3Phase00AMetricsTests.cs");
    AddIfPresent(relativePaths, repositoryRoot, "HybridCPU_ISE.Tests/CompilerTests/CompilerRefPlan3Phase00EvidenceTests.cs");
    foreach (string rootBuildFile in new[]
             {
                 "Directory.Build.props",
                 "Directory.Build.targets",
                 "Directory.Packages.props",
                 "global.json"
             })
    {
        AddIfPresent(relativePaths, repositoryRoot, rootBuildFile);
    }

    return relativePaths.Select(relativePath => CompilerSourceFileHashV1.Create(
        relativePath,
        File.ReadAllBytes(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))))).ToArray();
}

static void AddBuildSources(ISet<string> paths, string repositoryRoot, string relativeRoot, bool excludeEvidence)
{
    string absoluteRoot = Path.Combine(repositoryRoot, relativeRoot);
    foreach (string path in Directory.EnumerateFiles(absoluteRoot, "*", SearchOption.AllDirectories))
    {
        string relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
        string[] segments = relative.Split('/');
        if (segments.Any(static segment => segment is "bin" or "obj") ||
            (excludeEvidence && relative.StartsWith("HybridCPU_Compiler/RefPlan3/evidence/", StringComparison.Ordinal)))
        {
            continue;
        }

        string extension = Path.GetExtension(path);
        if (extension is ".cs" or ".csproj" or ".props" or ".targets" or ".json")
        {
            paths.Add(relative);
        }
    }
}

static void AddIfPresent(ISet<string> paths, string repositoryRoot, string relativePath)
{
    if (File.Exists(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))))
    {
        paths.Add(relativePath);
    }
}

static string ComputeSourceManifestFingerprint(
    string baseSha,
    string branch,
    CompilerSubjectDispositionV1 disposition,
    IReadOnlyList<CompilerSourceFileHashV1> sourceFiles)
{
    StringBuilder canonical = new StringBuilder();
    Append(canonical, baseSha);
    Append(canonical, branch);
    Append(canonical, ((int)disposition).ToString(CultureInfo.InvariantCulture));
    foreach (CompilerSourceFileHashV1 file in sourceFiles)
    {
        Append(canonical, file.RelativePath);
        Append(canonical, file.Sha256);
        Append(canonical, file.LengthBytes.ToString(CultureInfo.InvariantCulture));
    }

    return Hash(Encoding.UTF8.GetBytes(canonical.ToString()));
}

static void Append(StringBuilder builder, string value) => builder.Append(value.Length).Append(':').Append(value).Append(';');

static string Hash(ReadOnlySpan<byte> contents) => Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();

static string RunGit(string workingDirectory, params string[] arguments)
{
    ProcessStartInfo startInfo = new ProcessStartInfo("git")
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    foreach (string argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using Process process = Process.Start(startInfo) ??
        throw new InvalidOperationException("Unable to start local Git for subject attribution.");
    string stdout = process.StandardOutput.ReadToEnd();
    string stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
    }

    return stdout;
}

static void EnsurePathWithin(string path, string root)
{
    string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException($"Output directory must be inside '{root}'.", nameof(path));
    }
}

static Dictionary<string, string?> ParseOptions(string[] arguments)
{
    Dictionary<string, string?> result = new Dictionary<string, string?>(StringComparer.Ordinal);
    for (int index = 0; index < arguments.Length; index++)
    {
        string key = arguments[index];
        if (key is "--allow-dirty-attributed" or "--overwrite")
        {
            result[key] = null;
            continue;
        }

        if (index + 1 >= arguments.Length)
        {
            throw new ArgumentException($"Missing value for '{key}'.");
        }

        result[key] = arguments[++index];
    }

    return result;
}

static string Require(IReadOnlyDictionary<string, string?> options, string key) =>
    options.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Required option '{key}' is missing.");

static int ParsePositiveInt(string value, string optionName) =>
    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
        ? parsed
        : throw new ArgumentException($"{optionName} must be a positive integer.");

internal sealed record ControlProfile(
    string Id,
    string DisplayName,
    int WorkloadKind,
    ulong Iterations,
    int TimeoutMilliseconds);

internal sealed record ProcessResult(
    int ExitCode,
    bool TimedOut,
    TimeSpan Elapsed,
    string StandardOutput,
    string StandardError);
