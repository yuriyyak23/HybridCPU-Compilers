using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Build.Evaluation;

// Evaluation only: no targets or builds are invoked against the input project.
if (args.Length is not (3 or 4) || args.Any(x => !Path.IsPathFullyQualified(x)))
    throw new ArgumentException("Expected absolute project, SDK directory, new output file, and optional project graph paths.");
var projectPath = Path.GetFullPath(args[0]);
var sdk = Path.GetFullPath(args[1]);
var output = Path.GetFullPath(args[2]);
var modelRoot = Directory.GetParent(Path.GetDirectoryName(projectPath)!)!.FullName;
if (output.StartsWith(Path.TrimEndingDirectorySeparator(modelRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Output inside model tree is forbidden.");
for (var parent = new DirectoryInfo(Path.GetDirectoryName(output)!); parent is not null; parent = parent.Parent)
    if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked output directory is forbidden.");
if (File.Exists(output)) throw new InvalidOperationException("Output already exists.");
if (!File.Exists(Path.Combine(sdk, "MSBuild.dll")) || !Directory.Exists(Path.Combine(sdk, "Sdks")))
    throw new InvalidOperationException("SDK directory is invalid.");
Environment.SetEnvironmentVariable("MSBuildSDKsPath", Path.Combine(sdk, "Sdks"));
Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", Path.Combine(sdk, "MSBuild.dll"));
using var collection = new ProjectCollection(new Dictionary<string, string> { ["Configuration"] = "Debug" });
var capture = (Project project) =>
{
    var paths = project.Imports.Select(x => x.ImportedProject.FullPath).Append(project.FullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    var files = paths.Select(path =>
    {
        var data = File.ReadAllBytes(path);
        return new { path, length = data.LongLength, sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() };
    }).ToArray();
    return new { schema = "hardwarebridge.msbuild-import-inventory/v1", disposition = "evaluation-only-unqualified",
        project = project.FullPath, configuration = project.GetPropertyValue("Configuration"),
        targetFramework = project.GetPropertyValue("TargetFramework"),
        enableInternalTestHooks = project.GetPropertyValue("EnableInternalTestHooks"),
        defineConstants = project.GetPropertyValue("DefineConstants"), langVersion = project.GetPropertyValue("LangVersion"),
        compileFiles = project.GetItems("Compile").Select(item => item.GetMetadataValue("FullPath"))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray(), sdkDirectory = sdk, files,
        scope = "Resolved imports observed by this MSBuild evaluation only; no targets run, no proof of absent conditional imports, generator closure, project-reference propagation or later freshness." };
};
var root = capture(collection.LoadProject(projectPath));
object result = root;
if (args.Length == 4)
{
    var graphBytes = File.ReadAllBytes(args[3]);
    using var graph = JsonDocument.Parse(graphBytes);
    if (graph.RootElement.GetProperty("schema").GetString() != "hardwarebridge.msbuild-project-graph/v1")
        throw new InvalidDataException("Unsupported project graph.");
    var projects = graph.RootElement.GetProperty("projects");
    if (projects.ValueKind != JsonValueKind.Array || projects.GetArrayLength() > 100)
        throw new InvalidDataException("Project graph exceeds limits.");
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { projectPath };
    var references = projects.EnumerateArray().Select(entry =>
    {
        var path = Path.GetFullPath(entry.GetProperty("project").GetString()!);
        if (!seen.Add(path)) throw new InvalidDataException("Duplicate project in graph.");
        return capture(collection.LoadProject(path));
    }).ToArray();
    result = new { schema = "hardwarebridge.msbuild-import-graph/v1", disposition = "independent-evaluations-unqualified",
        projectGraphSha256 = Convert.ToHexString(SHA256.HashData(graphBytes)).ToLowerInvariant(), root, projects = references,
        scope = "Separate Debug MSBuild evaluations; not proof of exact project-reference property propagation, absent conditional imports, generator closure or later freshness." };
}
using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
JsonSerializer.Serialize(stream, result, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine($"Captured {root.files.Length} root project/import identities" + (args.Length == 4 ? " plus project-reference evaluations" : "") + "; no target execution or completeness claim.");
