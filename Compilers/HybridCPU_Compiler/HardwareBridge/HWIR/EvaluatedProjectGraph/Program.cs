using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Graph;

// Evaluate only. No MSBuild target, restore or build is executed in the input tree.
if (args.Length != 3 || args.Any(p => !Path.IsPathFullyQualified(p)))
    throw new ArgumentException("Expected absolute project, SDK directory and new output paths.");
string project = Path.GetFullPath(args[0]), sdk = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
string modelRoot = Directory.GetParent(Path.GetDirectoryName(project)!)!.FullName;
string compilerRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "HybridCPU Compilers"));
static bool Under(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
if (Under(output, modelRoot) || Under(output, compilerRoot)) throw new InvalidOperationException("Output inside an input tree is forbidden.");
for (var parent = new DirectoryInfo(Path.GetDirectoryName(output)!); parent is not null; parent = parent.Parent)
    if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked output ancestor is forbidden.");
if (File.Exists(output) || !File.Exists(Path.Combine(sdk, "MSBuild.dll")) || !Directory.Exists(Path.Combine(sdk, "Sdks")))
    throw new InvalidOperationException("Output exists or SDK directory is invalid.");
Environment.SetEnvironmentVariable("MSBuildSDKsPath", Path.Combine(sdk, "Sdks"));
Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", Path.Combine(sdk, "MSBuild.dll"));
var globals = new Dictionary<string, string> { ["Configuration"] = "Debug", ["EnableInternalTestHooks"] = "true" };
using var collection = new ProjectCollection();
var graph = new ProjectGraph(new ProjectGraphEntryPoint(project, globals), collection);
if (graph.ProjectNodes.Count > 100) throw new InvalidDataException("Project graph exceeds limit.");
static string Identity(ProjectGraphNode node) => node.ProjectInstance.FullPath + "|" + string.Join(";", node.ProjectInstance.GlobalProperties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value));
var nodes = graph.ProjectNodes.OrderBy(Identity, StringComparer.OrdinalIgnoreCase).Select(node =>
{
    var instance = node.ProjectInstance;
    var files = instance.GetItems("Compile").Select(item => item.GetMetadataValue("FullPath")).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    var assetsPath = instance.GetPropertyValue("ProjectAssetsFile");
    object? assets = null;
    if (Path.IsPathFullyQualified(assetsPath) && File.Exists(assetsPath))
    {
        var bytes = File.ReadAllBytes(assetsPath);
        assets = new { path = assetsPath, length = bytes.LongLength, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
    }
    var evaluatedItems = new[] { "Reference", "PackageReference", "FrameworkReference", "Analyzer" }.ToDictionary(kind => kind,
        kind => instance.GetItems(kind).Select(item => new { identity = item.EvaluatedInclude,
            fullPath = item.GetMetadataValue("FullPath"), version = item.GetMetadataValue("Version"),
            hintPath = item.GetMetadataValue("HintPath"), aliases = item.GetMetadataValue("Aliases"),
            privateAssets = item.GetMetadataValue("PrivateAssets"), includeAssets = item.GetMetadataValue("IncludeAssets"),
            excludeAssets = item.GetMetadataValue("ExcludeAssets"),
            fileSha256 = kind == "Analyzer" && File.Exists(item.GetMetadataValue("FullPath"))
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item.GetMetadataValue("FullPath")))).ToLowerInvariant() : null }).OrderBy(item => item.identity, StringComparer.OrdinalIgnoreCase).ToArray());
    return new { id = Identity(node), project = instance.FullPath, globalProperties = instance.GlobalProperties,
        properties = new { configuration = instance.GetPropertyValue("Configuration"), targetFramework = instance.GetPropertyValue("TargetFramework"),
            targetFrameworks = instance.GetPropertyValue("TargetFrameworks"), defineConstants = instance.GetPropertyValue("DefineConstants"),
            enableInternalTestHooks = instance.GetPropertyValue("EnableInternalTestHooks"), assemblyName = instance.GetPropertyValue("AssemblyName") },
        compileFiles = files, projectAssets = assets, evaluatedItems,
        references = node.ProjectReferences.Select(Identity).Order(StringComparer.OrdinalIgnoreCase).ToArray() };
}).ToArray();
var result = new { schema = "hardwarebridge.evaluated-project-graph/v2", disposition = "evaluation-only-unqualified", root = project,
    rootProjectSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(project))).ToLowerInvariant(), sdkDirectory = sdk,
    globalProperties = globals, nodes,
    scope = "MSBuild ProjectGraph evaluated nodes, items and edges; project.assets.json bytes when present. Targets, restore freshness, resolved references, complete source generators and concurrent freshness are not established." };
using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
JsonSerializer.Serialize(stream, result, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine($"Evaluated {nodes.Length} project graph nodes; no targets executed.");
