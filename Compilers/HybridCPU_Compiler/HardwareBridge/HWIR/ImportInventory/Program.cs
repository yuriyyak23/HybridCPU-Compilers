using System.Security.Cryptography;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Build.Evaluation;

// Evaluation only: no targets or builds are invoked against the input project.
if (args.Length is < 3 or > 7 || args.Take(3).Any(x => !Path.IsPathFullyQualified(x)))
    throw new ArgumentException("Expected absolute project, SDK directory, new output file, optional project graph, optional Debug/Release configuration, optional TargetFramework and optional matched source capture.");
string projectPath = Path.GetFullPath(args[0]);
string sdk = Path.GetFullPath(args[1]);
string output = Path.GetFullPath(args[2]);
string configuration = "Debug";
string? graphPath = null;
string? targetFramework = null;
string? sourceCapturePath = null;
if (args.Length == 4)
{
    if (args[3] is "Debug" or "Release") configuration = args[3];
    else graphPath = Path.GetFullPath(args[3]);
}
else if (args.Length == 5)
{
    if (args[3] is "Debug" or "Release")
    {
        configuration = args[3];
        graphPath = Path.GetFullPath(args[4]);
    }
    else
    {
        graphPath = Path.GetFullPath(args[3]);
        if (args[4] is not ("Debug" or "Release"))
            throw new ArgumentException("Configuration must be Debug or Release.");
        configuration = args[4];
    }
}
else if (args.Length == 6)
{
    if (args[3] is "Debug" or "Release")
    {
        configuration = args[3];
        graphPath = Path.GetFullPath(args[4]);
    }
    else
    {
        graphPath = Path.GetFullPath(args[3]);
        if (args[4] is not ("Debug" or "Release"))
            throw new ArgumentException("Configuration must be Debug or Release.");
        configuration = args[4];
    }
    targetFramework = args[5];
    if (string.IsNullOrWhiteSpace(targetFramework) || targetFramework.Contains(Path.DirectorySeparatorChar) || targetFramework.Contains(Path.AltDirectorySeparatorChar))
        throw new ArgumentException("TargetFramework must be a non-empty framework moniker.");
}
else if (args.Length == 7)
{
    if (args[3] is "Debug" or "Release")
    {
        configuration = args[3];
        graphPath = Path.GetFullPath(args[4]);
    }
    else
    {
        graphPath = Path.GetFullPath(args[3]);
        if (args[4] is not ("Debug" or "Release"))
            throw new ArgumentException("Configuration must be Debug or Release.");
        configuration = args[4];
    }
    targetFramework = args[5];
    sourceCapturePath = Path.GetFullPath(args[6]);
    if (string.IsNullOrWhiteSpace(targetFramework) || targetFramework.Contains(Path.DirectorySeparatorChar) || targetFramework.Contains(Path.AltDirectorySeparatorChar))
        throw new ArgumentException("TargetFramework must be a non-empty framework moniker.");
}
string modelRoot = Directory.GetParent(Path.GetDirectoryName(projectPath)!)!.FullName;
if (output.StartsWith(Path.TrimEndingDirectorySeparator(modelRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Output inside model tree is forbidden.");
for (var parent = new DirectoryInfo(Path.GetDirectoryName(output)!); parent is not null; parent = parent.Parent)
    if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked output directory is forbidden.");
if (File.Exists(output)) throw new InvalidOperationException("Output already exists.");
if (!File.Exists(Path.Combine(sdk, "MSBuild.dll")) || !Directory.Exists(Path.Combine(sdk, "Sdks")))
    throw new InvalidOperationException("SDK directory is invalid.");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    if (string.IsNullOrWhiteSpace(name.Name)) return null;
    string candidate = Path.Combine(sdk, name.Name + ".dll");
    return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
};
Environment.SetEnvironmentVariable("MSBuildSDKsPath", Path.Combine(sdk, "Sdks"));
Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", Path.Combine(sdk, "MSBuild.dll"));
var capture = (string projectFile, string? framework, IReadOnlyDictionary<string, string>? capturedProperties = null, JsonElement? expectedCscFiles = null) =>
{
    var globalProperties = new Dictionary<string, string> { ["Configuration"] = configuration };
    if (!string.IsNullOrWhiteSpace(framework)) globalProperties["TargetFramework"] = framework;
    if (capturedProperties is not null)
        foreach ((string key, string value) in capturedProperties) globalProperties[key] = value;
    using var collection = new ProjectCollection(globalProperties);
    var project = collection.LoadProject(projectFile);
    string[] paths = project.Imports.Select(x => x.ImportedProject.FullPath).Append(project.FullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    var files = paths.Select(path =>
    {
        byte[] data = File.ReadAllBytes(path);
        return new { path, length = data.LongLength, sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() };
    }).ToArray();
    string[] compileFiles = project.GetItems("Compile").Select(item => item.GetMetadataValue("FullPath"))
        .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    object[] cscGeneratedInputs = Array.Empty<object>();
    if (expectedCscFiles is JsonElement expected)
    {
        Dictionary<string, JsonElement> expectedByPath = expected.EnumerateArray().ToDictionary(
            file => Path.GetFullPath(file.GetProperty("path").GetString()!), file => file, StringComparer.OrdinalIgnoreCase);
        if (expectedByPath.Count != expected.GetArrayLength() || compileFiles.Any(path => !expectedByPath.ContainsKey(Path.GetFullPath(path))))
            throw new InvalidDataException("Evaluated Compile list contains duplicate or uncaptured sources.");
        cscGeneratedInputs = expectedByPath.Where(pair => !compileFiles.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            .Select(pair => (object)new { path = pair.Key, length = pair.Value.GetProperty("length").GetInt64(),
                sha256 = pair.Value.GetProperty("sha256").GetString(), reason = "captured-csc-input-not-present-in-target-free-evaluation" }).ToArray();
    }
    return new { schema = "hardwarebridge.msbuild-import-inventory/v1", disposition = "evaluation-only-unqualified",
        project = project.FullPath, configuration = project.GetPropertyValue("Configuration"),
        targetFramework = project.GetPropertyValue("TargetFramework"),
        enableInternalTestHooks = project.GetPropertyValue("EnableInternalTestHooks"),
        defineConstants = project.GetPropertyValue("DefineConstants"), langVersion = project.GetPropertyValue("LangVersion"),
        compileFiles, cscGeneratedInputs, sdkDirectory = sdk, files,
        scope = "Resolved imports observed by this MSBuild evaluation only; no targets run, no proof of absent conditional imports, generator closure, project-reference propagation or later freshness." };
};
JsonDocument? sourceCapture = null;
JsonElement sourceCaptureRoot = default;
string? sourceCaptureSha256 = null;
if (sourceCapturePath is not null)
{
    var captureInfo = new FileInfo(sourceCapturePath);
    if (!captureInfo.Exists || captureInfo.Length is < 1 or > 32 * 1024 * 1024 || (captureInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException("Matched source capture missing, linked or oversized.");
    byte[] captureBytes = File.ReadAllBytes(sourceCapturePath);
    sourceCapture = JsonDocument.Parse(captureBytes.AsMemory().Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? captureBytes.AsMemory(3) : captureBytes.AsMemory());
    sourceCaptureRoot = sourceCapture.RootElement;
    if (sourceCaptureRoot.GetProperty("schema").GetString() != "circuitweave.msbuild-source-byte-inventory.v1" ||
        !string.Equals(Path.GetFullPath(sourceCaptureRoot.GetProperty("project").GetString()!), projectPath, StringComparison.OrdinalIgnoreCase) ||
        sourceCaptureRoot.GetProperty("properties").GetProperty("Configuration").GetString() != configuration ||
        sourceCaptureRoot.GetProperty("properties").GetProperty("TargetFramework").GetString() != targetFramework)
        throw new InvalidDataException("Matched source capture project/configuration/TargetFramework differs.");
    sourceCaptureSha256 = Convert.ToHexString(SHA256.HashData(captureBytes)).ToLowerInvariant();
}
Dictionary<string, string> CapturedProperties(JsonElement properties)
{
    Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (string name in new[] { "Configuration", "TargetFramework", "DefineConstants", "LangVersion", "Nullable", "EnableInternalTestHooks" })
        if (properties.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length != 0)
            values[name] = text;
    return values;
}
var root = sourceCapture is null ? capture(projectPath, targetFramework) : capture(projectPath, targetFramework,
    CapturedProperties(sourceCaptureRoot.GetProperty("properties")), sourceCaptureRoot.GetProperty("files"));
object result = root;
if (graphPath is not null)
{
    byte[] graphBytes = File.ReadAllBytes(graphPath);
    using var graph = JsonDocument.Parse(graphBytes);
    if (graph.RootElement.GetProperty("schema").GetString() != "hardwarebridge.msbuild-project-graph/v1")
        throw new InvalidDataException("Unsupported project graph.");
    var projects = graph.RootElement.GetProperty("projects");
    if (projects.ValueKind != JsonValueKind.Array || projects.GetArrayLength() > 100)
        throw new InvalidDataException("Project graph exceeds limits.");
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { projectPath };
    var references = projects.EnumerateArray().Select(entry =>
    {
        string path = Path.GetFullPath(entry.GetProperty("project").GetString()!);
        if (!seen.Add(path)) throw new InvalidDataException("Duplicate project in graph.");
        string? framework = entry.GetProperty("properties").TryGetProperty("TargetFramework", out var frameworkElement)
            ? frameworkElement.GetString() : targetFramework;
        return sourceCapture is null ? capture(path, framework) : capture(path, framework,
            CapturedProperties(entry.GetProperty("properties")), entry.GetProperty("files"));
    }).ToArray();
    result = new { schema = sourceCapture is null ? "hardwarebridge.msbuild-import-graph/v1" : "hardwarebridge.msbuild-import-graph/v2",
        disposition = sourceCapture is null ? "independent-evaluations-unqualified" : "evaluation-with-captured-csc-properties-unqualified",
        projectGraphSha256 = Convert.ToHexString(SHA256.HashData(graphBytes)).ToLowerInvariant(), sourceCaptureSha256, root, projects = references,
        scope = sourceCapture is null
            ? "Separate MSBuild evaluations; no target execution, no proof of absent conditional imports, generator closure or later freshness."
            : "Separate MSBuild evaluations seeded with the matched Csc-captured Configuration, TargetFramework, DefineConstants, LangVersion and nullable properties; Compile lists and imports are evaluated independently, but those global compiler properties are inputs rather than independently inferred facts; no target execution or generator completeness claim." };
}
using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
JsonSerializer.Serialize(stream, result, new JsonSerializerOptions { WriteIndented = true });
sourceCapture?.Dispose();
Console.WriteLine($"Captured {root.files.Length} root project/import identities" + (graphPath is not null ? " plus project-reference evaluations" : "") + $" in {configuration}; no target execution or completeness claim.");
