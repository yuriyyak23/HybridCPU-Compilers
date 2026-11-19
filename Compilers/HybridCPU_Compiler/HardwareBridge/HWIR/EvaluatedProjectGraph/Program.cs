using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Graph;

// Evaluate only. No MSBuild target, restore or build is executed in the input tree.
if (args.Length is not (3 or 4) || args.Any(p => !Path.IsPathFullyQualified(p)))
    throw new ArgumentException("Expected absolute project, SDK directory, new output path and optional prior source inventory.");
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
    string[] files = instance.GetItems("Compile").Select(item => item.GetMetadataValue("FullPath")).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    string assetsPath = instance.GetPropertyValue("ProjectAssetsFile");
    object? assets = null;
    if (Path.IsPathFullyQualified(assetsPath) && File.Exists(assetsPath))
    {
        byte[] bytes = File.ReadAllBytes(assetsPath);
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
object? inventoryAudit = null;
if (args.Length == 4)
{
    var inventoryFile = new FileInfo(args[3]);
    if (!inventoryFile.Exists || inventoryFile.Length is < 1 or > 32 * 1024 * 1024 || (inventoryFile.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException("Prior source inventory is missing, linked or oversized.");
    byte[] inventoryBytes = File.ReadAllBytes(inventoryFile.FullName);
    using var inventoryDoc = JsonDocument.Parse(inventoryBytes, new JsonDocumentOptions { MaxDepth = 64 });
    var inventory = inventoryDoc.RootElement;
    if (inventory.GetProperty("schema").GetString() != "circuitweave.source-coverage-index/v1" ||
        inventory.GetProperty("disposition").GetString() != "incomplete-unqualified")
        throw new InvalidDataException("Unsupported prior source inventory.");
    var imports = inventory.GetProperty("msbuildImports");
    if (imports.GetProperty("schema").GetString() != "hardwarebridge.msbuild-import-graph/v1")
        throw new InvalidDataException("Prior evaluated imports are unavailable.");
    var previousProjects = new[] { imports.GetProperty("root") }.Concat(imports.GetProperty("projects").EnumerateArray()).ToArray();
    if (previousProjects.Length > 100 || previousProjects.Select(p => Path.GetFullPath(p.GetProperty("project").GetString()!)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != previousProjects.Length)
        throw new InvalidDataException("Prior project identities are duplicated or exceed limits.");
    var projectsByPath = previousProjects.ToDictionary(p => Path.GetFullPath(p.GetProperty("project").GetString()!), StringComparer.OrdinalIgnoreCase);
    var projectResults = nodes.Select(node =>
    {
        if (!projectsByPath.TryGetValue(node.project, out var previous))
            return new { project = node.project, previousFound = false, added = Array.Empty<string>(), removed = Array.Empty<string>(), propertyDifferences = new[] { "project missing from prior independent evaluations" } };
        var oldFiles = previous.GetProperty("compileFiles").EnumerateArray().Select(x => Path.GetFullPath(x.GetString()!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newFiles = node.compileFiles.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var differences = new List<string>();
        foreach (var (name, current) in new[] { ("configuration", node.properties.configuration), ("targetFramework", node.properties.targetFramework), ("defineConstants", node.properties.defineConstants), ("enableInternalTestHooks", node.properties.enableInternalTestHooks) })
            if (previous.GetProperty(name).GetString() != current) differences.Add(name + ": prior=" + previous.GetProperty(name).GetString() + "; current=" + current);
        return new { project = node.project, previousFound = true, added = newFiles.Except(oldFiles, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), removed = oldFiles.Except(newFiles, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), propertyDifferences = differences.ToArray() };
    }).ToArray();
    string[] absentProjects = projectsByPath.Keys.Except(nodes.Select(n => n.project), StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    var identities = new Dictionary<string, (long Length, string Hash)>(StringComparer.OrdinalIgnoreCase);
    void AddIdentities(JsonElement items)
    {
        foreach (var item in items.EnumerateArray())
        {
            string path = Path.GetFullPath(item.GetProperty("path").GetString()!); long length = item.GetProperty("length").GetInt64(); string hash = item.GetProperty("sha256").GetString()!;
            if (length < 0 || length > 128L * 1024 * 1024 || hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("Invalid prior file identity.");
            if (identities.TryGetValue(path, out var existing) && (existing.Length != length || !existing.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Conflicting prior file identities.");
            identities[path] = (length, hash);
            if (identities.Count > 20_000) throw new InvalidDataException("Prior file identities exceed limit.");
        }
    }
    AddIdentities(inventory.GetProperty("capturedSourceFiles"));
    foreach (var prior in previousProjects) AddIdentities(prior.GetProperty("files"));
    var changed = new List<object>();
    foreach (var (path, expected) in identities.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
    {
        var file = new FileInfo(path);
        string? failure = !file.Exists ? "missing" : (file.Attributes & FileAttributes.ReparsePoint) != 0 ? "linked" : file.Length != expected.Length ? "length-changed" : null;
        if (failure is null)
        {
            for (var directory = file.Directory; directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) { failure = "linked-parent"; break; }
        }
        if (failure is null)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(expected.Hash, StringComparison.OrdinalIgnoreCase)) failure = "hash-changed";
        }
        if (failure is not null) changed.Add(new { path, reason = failure });
    }
    inventoryAudit = new { inventorySha256 = Convert.ToHexString(SHA256.HashData(inventoryBytes)).ToLowerInvariant(), checkedFileIdentities = identities.Count,
        changedFiles = changed, projects = projectResults, absentProjects,
        scope = "Current evaluation versus prior independent project evaluations, captured source and import bytes. Property differences are explicit; targets, generator output, resolved references and semantic completeness are not revalidated." };
}
var result = new { schema = "hardwarebridge.evaluated-project-graph/v2", disposition = "evaluation-only-unqualified", root = project,
    rootProjectSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(project))).ToLowerInvariant(), sdkDirectory = sdk,
    globalProperties = globals, nodes, inventoryAudit,
    scope = "MSBuild ProjectGraph evaluated nodes, items and edges; project.assets.json bytes when present. Targets, restore freshness, resolved references, complete source generators and concurrent freshness are not established." };
using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
JsonSerializer.Serialize(stream, result, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine($"Evaluated {nodes.Length} project graph nodes; no targets executed.");
