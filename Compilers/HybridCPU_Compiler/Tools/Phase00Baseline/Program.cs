using System.Diagnostics;
using System.Text.Json;
using HybridCPU.Compiler.Core.IR.Telemetry;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core.Contracts;
using static YAKSys_Hybrid_CPU.Processor;

const string toolVersion = "HybridCPU.Compiler.Phase00Baseline/v1";
Dictionary<string, string?> options = ParseOptions(args);
string repositoryRoot = Path.GetFullPath(Require(options, "--repo-root"));
string evidenceRoot = Path.GetFullPath(Path.Combine(
    repositoryRoot,
    "HybridCPU_Compiler",
    "RefPlan3",
    "evidence"));
string outputDirectory = Path.GetFullPath(Require(options, "--output-dir"));
EnsurePathWithin(outputDirectory, evidenceRoot);
bool allowDirtyAttributed = options.ContainsKey("--allow-dirty-attributed");
bool overwrite = options.ContainsKey("--overwrite");

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
    "HybridCPU_ISE.Tests/CompilerTests/CompilerRefPlan3Phase00AMetricsTests.cs",
    "HybridCPU_ISE.Tests/CompilerTests/CompilerRefPlan3Phase00EvidenceTests.cs",
    "Documentation/AsmAppTestResults3.md");
CompilerSubjectDispositionV1 disposition = string.IsNullOrWhiteSpace(scopedStatus)
    ? CompilerSubjectDispositionV1.Clean
    : allowDirtyAttributed
        ? CompilerSubjectDispositionV1.DirtyAttributed
        : CompilerSubjectDispositionV1.DirtyUnattributed;
if (disposition == CompilerSubjectDispositionV1.DirtyUnattributed)
{
    throw new InvalidOperationException(
        "The scoped subject is dirty. Re-run with --allow-dirty-attributed only after reviewing " +
        "the exact file-hash scope; unattributed dirty subjects cannot produce a baseline.");
}

CompilerSourceFileHashV1[] sourceFiles = CaptureSourceFiles(repositoryRoot);
#pragma warning disable CS0618 // Compiler-mode bootstrap is isolated to this offline evidence tool.
_ = new Processor(ProcessorMode.Compiler);
#pragma warning restore CS0618
CompilerPhase00BaselineCaptureResultV1 capture = CompilerPhase00BaselineCaptureV1.Capture(
    baseSha,
    branch,
    disposition,
    options.GetValueOrDefault("--configuration") ?? "Debug",
    CompilerContract.Version,
    toolVersion,
    sourceFiles);

Directory.CreateDirectory(outputDirectory);
WriteNewOrReplace(
    Path.Combine(outputDirectory, "compiler_phase00_baseline_v1.json"),
    capture.DeterministicReport.ToDeterministicJsonBytes(),
    overwrite);
WriteNewOrReplace(
    Path.Combine(outputDirectory, "compiler_phase00_resources_v1.json"),
    capture.ResourceReport.ToJsonBytes(),
    overwrite);
var invocation = new
{
    schema = "CompilerPhase00BaselineInvocationV1",
    toolVersion,
    baseSha,
    branch,
    subjectDisposition = disposition.ToString(),
    sourceFileCount = sourceFiles.Length,
    sourceScope = new[]
    {
        "HybridCPU_Compiler compiler/build sources",
        "HybridCPU_ISE referenced build sources (read-only)",
        "RefPlan3 Phase 00 compiler tests",
        "historical AsmAppTestResults3 snapshot"
    },
    deterministicArtifact = "compiler_phase00_baseline_v1.json",
    resourceArtifact = "compiler_phase00_resources_v1.json"
};
WriteNewOrReplace(
    Path.Combine(outputDirectory, "compiler_phase00_invocation_v1.json"),
    JsonSerializer.SerializeToUtf8Bytes(invocation, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    }),
    overwrite);

Console.WriteLine($"Captured {capture.DeterministicReport.Profiles.Count} compiler profiles.");
Console.WriteLine($"Subject: {baseSha} ({disposition}), {sourceFiles.Length} exact source files.");
Console.WriteLine($"Artifacts: {outputDirectory}");
return 0;

static CompilerSourceFileHashV1[] CaptureSourceFiles(string repositoryRoot)
{
    SortedSet<string> relativePaths = new SortedSet<string>(StringComparer.Ordinal);
    AddBuildSources(relativePaths, repositoryRoot, "HybridCPU_Compiler", excludeEvidence: true);
    AddBuildSources(relativePaths, repositoryRoot, "HybridCPU_ISE", excludeEvidence: false);
    AddIfPresent(relativePaths, repositoryRoot, "HybridCPU_ISE.Tests/CompilerTests/CompilerRefPlan3Phase00AMetricsTests.cs");
    AddIfPresent(relativePaths, repositoryRoot, "HybridCPU_ISE.Tests/CompilerTests/CompilerRefPlan3Phase00EvidenceTests.cs");
    AddIfPresent(relativePaths, repositoryRoot, "Documentation/AsmAppTestResults3.md");
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

    return relativePaths
        .Select(relativePath => CompilerSourceFileHashV1.Create(
            relativePath,
            File.ReadAllBytes(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)))))
        .ToArray();
}

static void AddBuildSources(
    ISet<string> paths,
    string repositoryRoot,
    string relativeRoot,
    bool excludeEvidence)
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
        if (extension is ".cs" or ".csproj" or ".props" or ".targets")
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

static void WriteNewOrReplace(string path, byte[] contents, bool overwrite)
{
    if (File.Exists(path) && !overwrite)
    {
        throw new IOException($"Artifact '{path}' already exists. Pass --overwrite to replace it deliberately.");
    }

    File.WriteAllBytes(path, contents);
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
