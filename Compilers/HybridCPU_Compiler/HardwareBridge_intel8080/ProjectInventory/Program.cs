using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Build.Evaluation;

// Evaluation only: never asks MSBuild to execute targets or build the imported project.
if (args.Length != 3 || args.Any(x => !Path.IsPathFullyQualified(x)))
    throw new ArgumentException("Expected absolute project, SDK directory and new output file paths.");

string projectPath = Path.GetFullPath(args[0]);
string sdk = Path.GetFullPath(args[1]);
string output = Path.GetFullPath(args[2]);
if (!File.Exists(projectPath)) throw new FileNotFoundException("Project file not found.", projectPath);
string allowedRoot = Path.GetFullPath(@"C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080").TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
if (!output.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("8080 project inventory output must remain inside CircuitWeave-main/artifacts/8080.");
string modelRoot = Directory.GetParent(Path.GetDirectoryName(projectPath)!)!.FullName;
if (output.StartsWith(Path.TrimEndingDirectorySeparator(modelRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Output inside the source project tree is forbidden.");
for (var parent = new DirectoryInfo(Path.GetDirectoryName(output)!); parent is not null; parent = parent.Parent)
    if (Directory.Exists(parent.FullName) && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked output directory is forbidden.");
if (File.Exists(output) || Directory.Exists(output)) throw new InvalidOperationException("Existing output is forbidden.");
if (!File.Exists(Path.Combine(sdk, "MSBuild.dll")) || !Directory.Exists(Path.Combine(sdk, "Sdks")))
    throw new InvalidOperationException("SDK directory is invalid.");

Environment.SetEnvironmentVariable("MSBuildSDKsPath", Path.Combine(sdk, "Sdks"));
Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", Path.Combine(sdk, "MSBuild.dll"));
using var collection = new ProjectCollection(new Dictionary<string, string> { ["Configuration"] = "Debug" });
var project = collection.LoadProject(projectPath);
string[] importedPaths = project.Imports.Select(x => x.ImportedProject.FullPath).Append(project.FullPath)
    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
var files = importedPaths.Select(path =>
{
    byte[] bytes = File.ReadAllBytes(path);
    return new { path, length = bytes.LongLength, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
}).ToArray();
var result = new
{
    schema = "hardwarebridge.intel8080.msbuild-import-inventory/v1",
    disposition = "evaluation-only-unqualified",
    project = project.FullPath,
    configuration = project.GetPropertyValue("Configuration"),
    targetFramework = project.GetPropertyValue("TargetFramework"),
    targetFrameworks = project.GetPropertyValue("TargetFrameworks"),
    assemblyName = project.GetPropertyValue("AssemblyName"),
    outputType = project.GetPropertyValue("OutputType"),
    langVersion = project.GetPropertyValue("LangVersion"),
    defineConstants = project.GetPropertyValue("DefineConstants"),
    packageReferences = project.GetItems("PackageReference").Select(item => new
        { id = item.EvaluatedInclude, version = item.GetMetadataValue("Version") }).ToArray(),
    projectReferences = project.GetItems("ProjectReference").Select(item => item.GetMetadataValue("FullPath")).ToArray(),
    compileFiles = project.GetItems("Compile").Select(item => item.GetMetadataValue("FullPath")).ToArray(),
    sdkDirectory = sdk,
    files,
    scope = "Selected Debug MSBuild evaluation only. No targets, restore or source build were requested. Conditional imports absent in this configuration, generator closure, resolved references and later freshness are not established."
};

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
JsonSerializer.Serialize(stream, result, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine($"Captured {files.Length} project/import identities and {result.compileFiles.Length} Compile items; no project target execution or source build.");
