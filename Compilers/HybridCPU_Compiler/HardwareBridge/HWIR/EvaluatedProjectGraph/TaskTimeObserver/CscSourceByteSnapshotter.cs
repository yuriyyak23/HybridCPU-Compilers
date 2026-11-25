using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Re-reads current source bytes only after binding five Csc source maps to identical pinned
/// pre/post source inventories and a separately pinned generated-source inventory.
/// The resulting snapshots do not prove that Csc read or consumed these bytes.
/// </summary>
internal static class CscSourceByteSnapshotter
{
    private const int MaxJsonBytes = 16 * 1024 * 1024;
    private const int MaxManifestRows = 10000;
    private const int MaxSourceFileBytes = 32 * 1024 * 1024;
    private const long MaxPackageBytes = 256L * 1024 * 1024;
    private static readonly string[] ExpectedMapNames =
    [
        "HybridCPU_ExternalRuntime.Contracts.csproj.map.json",
        "HybridCPU_ISE.csproj.map.json",
        "HybridCPU.ManagedRuntime.csproj.map.json",
        "HybridCPU.Platform.Contracts.csproj.map.json",
        "HybridCPU.RuntimeKernel.csproj.map.json"
    ];
    private const string GeneratedInventoryName = "generated-source-inventory.v1.json";

    private sealed record PinnedInput(string Name, byte[] Bytes, string Sha);
    private sealed record FileIdentity(long Bytes, string Sha);
    private sealed record WriteItem(string Path, byte[] Content, string Sha);
    private sealed record PackageBuild(string OutputDirectory, byte[] Manifest, IReadOnlyList<WriteItem> Writes);
    private sealed class SnapshotFile
    {
        public required string Role { get; init; }
        public required string OriginalPath { get; init; }
        public required string SnapshotPath { get; init; }
        public required long Bytes { get; init; }
        public required string Sha { get; init; }
        public required byte[] Content { get; init; }
        public HashSet<string> Nodes { get; } = new(StringComparer.Ordinal);
    }

    public static int Main(string[] args)
    {
        bool outputCreated = false;
        string outputDirectory = "";
        try
        {
            if (args.Length != 11 || args[0] != "snapshot")
                throw new InvalidDataException("Usage: snapshot <map-set-manifest> <map-set-sha256> <source-root> <pre-manifest> <pre-sha256> <post-manifest> <post-sha256> <generated-inventory-sha256> <expected-source-rows> <new-direct-temp-directory>");
            PackageBuild package = BuildPackage(args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8],
                ParseRowCount(args[9]), args[10]);
            outputDirectory = package.OutputDirectory;
            CreateDirectoryExclusive(outputDirectory);
            outputCreated = true;
            foreach (WriteItem item in package.Writes)
            {
                string full = UnderRoot(outputDirectory, item.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                WriteNewVerified(full, item.Content, item.Sha);
            }
            byte[] manifest = package.Manifest;
            string manifestPath = Path.Combine(outputDirectory, "snapshot-manifest.json");
            WriteNewVerified(manifestPath, manifest, Hash(manifest));
            using JsonDocument resultDoc = JsonDocument.Parse(manifest);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "UnqualifiedSourceSnapshotPublished",
                outputDirectory,
                sourceFiles = resultDoc.RootElement.GetProperty("files").GetArrayLength(),
                evidenceFiles = resultDoc.RootElement.GetProperty("evidence").GetArrayLength(),
                manifestSha256 = Hash(manifest),
                cscReadOrConsumedBytes = "NotEstablished",
                completeCompilerInputClosure = "NotEstablished",
                independentCoreAdmission = "NotAdmitted"
            }));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
                                      ArgumentException or InvalidOperationException or FormatException or OverflowException or
                                      DllNotFoundException or EntryPointNotFoundException)
        {
            if (outputCreated)
            {
                try { Directory.Delete(outputDirectory, recursive: true); } catch { }
            }
            Console.Error.WriteLine("REFUSED: " + ex.Message);
            return 2;
        }
    }

    private static int ParseRowCount(string value)
    {
        if (!int.TryParse(value, out int count) || count is < 1 or > 100_000)
            throw new InvalidDataException("Expected tracked source row count must be bounded positive integer.");
        return count;
    }

    private static PackageBuild BuildPackage(string mapSetPath, string mapSetPin, string sourceRootPath,
        string prePath, string prePin, string postPath, string postPin, string generatedPin,
        int expectedSourceRows, string requestedOutputDirectory)
    {
        RequireSha(mapSetPin, "map-set manifest"); RequireSha(prePin, "pre-source manifest");
        RequireSha(postPin, "post-source manifest"); RequireSha(generatedPin, "generated-source inventory");
        if (!OperatingSystem.IsWindows()) throw new InvalidDataException("Windows filesystem and path semantics are required.");
        string sourceRoot = SafeDirectory(sourceRootPath);
        PinnedInput mapSetInput = ReadPinnedJson(mapSetPath, mapSetPin, "source-map manifest");
        PinnedInput preInput = ReadPinnedJson(prePath, prePin, "pre-build source manifest");
        PinnedInput postInput = ReadPinnedJson(postPath, postPin, "post-build source manifest");
        if (!preInput.Bytes.AsSpan().SequenceEqual(postInput.Bytes) || preInput.Sha != postInput.Sha ||
            !prePin.Equals(postPin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Pinned pre/post source manifests are not byte-identical.");

        using JsonDocument mapSetDoc = JsonDocument.Parse(mapSetInput.Bytes, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement mapSet = mapSetDoc.RootElement; CheckDuplicateProperties(mapSet);
        RequireProperties(mapSet, "schema", "files", "fileCount", "totalBytes");
        if (Get(mapSet, "schema") != "circuitweave.csc-source-argument-map-snapshot/v1")
            throw new InvalidDataException("Unsupported source-map manifest schema.");
        JsonElement mapRows = mapSet.GetProperty("files");
        if (mapRows.ValueKind != JsonValueKind.Array || mapRows.GetArrayLength() < 2 || mapRows.GetArrayLength() > 6 ||
            Int(mapSet, "fileCount") != mapRows.GetArrayLength())
            throw new InvalidDataException("Source-map manifest must contain bounded map files and one generated inventory.");
        decimal declaredTotal = Decimal(mapSet.GetProperty("totalBytes"));
        var mapInputs = new List<PinnedInput>();
        var listed = new Dictionary<string, FileIdentity>(StringComparer.OrdinalIgnoreCase);
        long totalListed = 0;
        foreach (JsonElement row in mapRows.EnumerateArray())
        {
            RequireProperties(row, "path", "bytes", "sha256");
            string name = Get(row, "path");
            if (Path.GetFileName(name) != name || name.Contains('/') || name.Contains('\\'))
                throw new InvalidDataException("Map-set evidence paths must be simple file names.");
            long bytes = Long(row, "bytes"); string sha = Get(row, "sha256"); RequireSha(sha, "map-set row");
            if (bytes is < 1 or > MaxJsonBytes || !listed.TryAdd(name, new(bytes, sha.ToLowerInvariant())))
                throw new InvalidDataException("Map-set file length is outside bounds or name is duplicated.");
            totalListed = checked(totalListed + bytes);
        }
        if (totalListed > MaxPackageBytes || declaredTotal != totalListed)
            throw new InvalidDataException("Map-set total byte count differs from its rows or exceeds bounds.");
        string[] names = listed.Keys.Order(StringComparer.Ordinal).ToArray();
        string[] mapNames = names.Where(n => n.EndsWith(".map.json", StringComparison.Ordinal)).ToArray();
        string[] expectedNames = (expectedSourceRows == 3530 ? ExpectedMapNames : mapNames)
            .Append(GeneratedInventoryName).Order(StringComparer.Ordinal).ToArray();
        if (mapNames.Length != mapRows.GetArrayLength() - 1 ||
            !listed.Keys.Order(StringComparer.Ordinal).SequenceEqual(expectedNames, StringComparer.Ordinal))
            throw new InvalidDataException("Map-set rows do not name the expected maps and generated inventory.");
        if (mapNames.Length is < 1 or > 5) throw new InvalidDataException("Source-map count is outside bounds.");

        string mapSetDirectory = Path.GetDirectoryName(Path.GetFullPath(mapSetPath))!;
        SafeDirectory(mapSetDirectory);
        var evidence = new List<PinnedInput> { mapSetInput, preInput, postInput };
        foreach (string name in expectedNames)
        {
            string path = Path.Combine(mapSetDirectory, name);
            FileIdentity pin = listed[name];
            PinnedInput item = ReadPinnedJson(path, pin.Sha, "map-set child " + name);
            if (item.Bytes.LongLength != pin.Bytes) throw new InvalidDataException("Map-set row length mismatch: " + name);
            evidence.Add(item);
            if (name != GeneratedInventoryName) mapInputs.Add(item);
        }
        PinnedInput generatedInput = evidence.Single(x => x.Name == GeneratedInventoryName);
        if (!generatedInput.Sha.Equals(generatedPin, StringComparison.OrdinalIgnoreCase) ||
            !generatedInput.Sha.Equals(listed[GeneratedInventoryName].Sha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Generated inventory differs from the external SHA-256 pin.");

        Dictionary<string, FileIdentity> tracked = ReadSourceManifest(preInput.Bytes, sourceRoot, expectedSourceRows);
        using JsonDocument generatedDoc = JsonDocument.Parse(generatedInput.Bytes, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement generatedRoot = generatedDoc.RootElement; CheckDuplicateProperties(generatedRoot);
        RequireProperties(generatedRoot, "schema", "disposition", "graphSha256", "files");
        if (Get(generatedRoot, "schema") != "hardwarebridge.csc-generated-source-inventory/v1" ||
            Get(generatedRoot, "disposition") != "snapshot-unqualified")
            throw new InvalidDataException("Unsupported generated-source inventory.");
        string graphSha = Get(generatedRoot, "graphSha256"); RequireSha(graphSha, "generated inventory graph");
        JsonElement generatedRows = generatedRoot.GetProperty("files");
        if (generatedRows.ValueKind != JsonValueKind.Array || generatedRows.GetArrayLength() is < 1 or > 15 ||
            (expectedSourceRows == 3530 && generatedRows.GetArrayLength() != 15))
            throw new InvalidDataException("Generated-source inventory row count is outside scope.");
        var generated = new Dictionary<string, (string NodeId, string Project, long Bytes, string Sha, byte[] Content)>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement row in generatedRows.EnumerateArray())
        {
            RequireProperties(row, "nodeId", "project", "role", "path", "bytes", "sha256");
            string node = Get(row, "nodeId"), project = Get(row, "project"), role = Get(row, "role"), path = Get(row, "path");
            if (!Path.IsPathFullyQualified(project) || !Path.IsPathFullyQualified(path) || role != "msbuild-generated" ||
                !Under(sourceRoot, project, allowRoot: false))
                throw new InvalidDataException("Generated inventory row identity, project root or role is invalid.");
            long bytes = Long(row, "bytes"); string sha = Get(row, "sha256"); RequireSha(sha, "generated source row");
            if (bytes is < 0 or > MaxSourceFileBytes) throw new InvalidDataException("Generated source length is outside bounds.");
            string full = SafeExistingFile(path, MaxSourceFileBytes, allowEmpty: true);
            byte[] content = ReadBytes(full, MaxSourceFileBytes);
            if (content.LongLength != bytes || !Hash(content).Equals(sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Generated source file differs from its pinned byte row.");
            if (!generated.TryAdd(full, (node, Path.GetFullPath(project), bytes, sha.ToLowerInvariant(), content)))
                throw new InvalidDataException("Duplicate generated source path.");
        }

        var snapshots = new Dictionary<string, SnapshotFile>(StringComparer.OrdinalIgnoreCase);
        var mappedGenerated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mapNodes = new HashSet<string>(StringComparer.Ordinal);
        var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PinnedInput mapInput in mapInputs)
        {
            using JsonDocument mapDoc = JsonDocument.Parse(mapInput.Bytes, new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement map = mapDoc.RootElement; CheckDuplicateProperties(map);
            if (Get(map, "schema") != "hardwarebridge.csc-source-argument-map/v1" ||
                Get(map, "disposition") != "source-argument-map-unqualified" ||
                !Get(map, "graphSha256").Equals(graphSha, StringComparison.OrdinalIgnoreCase) ||
                !Get(map, "generatedInventorySha256").Equals(generatedInput.Sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Source map is stale or not an unqualified map for this graph/inventory.");
            JsonElement claims = map.GetProperty("claims");
            if (claims.GetProperty("ordinarySourceBytesHashed").ValueKind != JsonValueKind.False ||
                Get(claims, "cscReadOrConsumedResponse") != "NotEstablished" ||
                Get(claims, "completeCompilerInputClosure") != "NotEstablished" || Get(claims, "admission") != "NotAdmitted")
                throw new InvalidDataException("Source map claims exceed the permitted evidence scope.");
            string nodeId = Get(map, "nodeId"), projectPath = Path.GetFullPath(Get(map, "project"));
            if (!Path.IsPathFullyQualified(Get(map, "project")) || !Under(sourceRoot, projectPath, allowRoot: false) ||
                !nodeId.StartsWith(projectPath + "|", StringComparison.OrdinalIgnoreCase) ||
                !mapNodes.Add(nodeId) || !seenProjects.Add(projectPath))
                throw new InvalidDataException("Source map node/project identity is invalid or duplicated.");
            JsonElement sources = map.GetProperty("sources");
            if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() != Int(map, "sourceCount") ||
                Int(map, "generatedSourceCount") != sources.EnumerateArray().Count(x => Get(x, "role") == "msbuild-generated"))
                throw new InvalidDataException("Source map source counts are inconsistent.");
            var withinMap = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement source in sources.EnumerateArray())
            {
                string role = Get(source, "role"), path = Get(source, "resolvedPath");
                if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Mapped source path is not absolute.");
                string full = Path.GetFullPath(path);
                if (!withinMap.Add(full)) throw new InvalidDataException("Duplicate source path in map.");
                if (role == "evaluated-compile")
                {
                    string compilePath = Get(source, "evaluatedCompilePath");
                    if (!SamePath(full, compilePath) || source.GetProperty("generatedBytes").ValueKind != JsonValueKind.Null ||
                        source.GetProperty("generatedSha256").ValueKind != JsonValueKind.Null || !Under(sourceRoot, full, allowRoot: false))
                        throw new InvalidDataException("Evaluated Compile source row is inconsistent or outside source root.");
                    string relative = Relative(sourceRoot, full);
                    if (!tracked.TryGetValue(relative, out FileIdentity? identity))
                        throw new InvalidDataException("Ordinary source is absent from the pinned Golden source manifest: " + relative);
                    byte[] content = ReadBytes(SafeExistingFile(full, MaxSourceFileBytes, allowEmpty: true), MaxSourceFileBytes);
                    if (content.LongLength != identity.Bytes || !Hash(content).Equals(identity.Sha, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Current ordinary source bytes differ from the pinned Golden source manifest: " + relative);
                    AddSnapshot(snapshots, role, full, "sources/" + relative, identity.Bytes, identity.Sha, content, nodeId);
                }
                else if (role == "msbuild-generated")
                {
                    if (source.GetProperty("evaluatedCompilePath").ValueKind != JsonValueKind.Null || !generated.TryGetValue(full, out var identity) ||
                        identity.NodeId != nodeId || !SamePath(identity.Project, projectPath) ||
                        Long(source, "generatedBytes") != identity.Bytes || !Get(source, "generatedSha256").Equals(identity.Sha, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Mapped generated source differs from inventory or node/project binding.");
                    if (!mappedGenerated.Add(full)) throw new InvalidDataException("Generated source is mapped more than once.");
                    string key = Hash(Encoding.UTF8.GetBytes(full.ToUpperInvariant()))[..24];
                    string relative = "generated/" + key + "/" + Path.GetFileName(full);
                    AddSnapshot(snapshots, role, full, relative, identity.Bytes, identity.Sha, identity.Content, nodeId);
                }
                else throw new InvalidDataException("Unsupported source-map role.");
            }
        }
        if (mapNodes.Count != mapInputs.Count || seenProjects.Count != mapInputs.Count || mappedGenerated.Count != generated.Count ||
            !mappedGenerated.SetEquals(generated.Keys))
            throw new InvalidDataException("Five project maps do not cover all pinned generated-source rows exactly once.");

        foreach (PinnedInput item in evidence)
            if (item.Bytes.LongLength > MaxJsonBytes) throw new InvalidDataException("Evidence file exceeds package bounds.");
        long bytesToWrite = snapshots.Values.Sum(x => x.Content.LongLength) + evidence.Sum(x => x.Bytes.LongLength);
        if (bytesToWrite > MaxPackageBytes) throw new InvalidDataException("Source snapshot package exceeds byte budget.");

        string outputDirectory = ValidateOutputDirectory(requestedOutputDirectory);
        var writes = new List<WriteItem>();
        var evidenceRows = new List<object>();
        foreach (PinnedInput item in evidence.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            string relative = EvidenceRelative(item.Name);
            writes.Add(new(relative, item.Bytes, item.Sha));
            evidenceRows.Add(new { path = relative, bytes = item.Bytes.LongLength, sha256 = item.Sha });
        }
        var snapshotRows = new List<object>();
        foreach (SnapshotFile file in snapshots.Values.OrderBy(x => x.SnapshotPath, StringComparer.Ordinal))
        {
            writes.Add(new(file.SnapshotPath, file.Content, file.Sha));
            snapshotRows.Add(new { role = file.Role, originalPath = file.OriginalPath, snapshotPath = file.SnapshotPath,
                bytes = file.Bytes, sha256 = file.Sha, referencedByNodeIds = file.Nodes.Order(StringComparer.Ordinal).ToArray() });
        }
        var result = new
        {
            schema = "hardwarebridge.csc-source-byte-snapshot/v1",
            disposition = "current-byte-snapshot-unqualified",
            identity = new { sourceRoot, trackedManifestRows = expectedSourceRows, generatedRows = generated.Count,
                sourceMapCount = mapInputs.Count, graphSha256 = graphSha.ToLowerInvariant() },
            inputPins = new { sourceMapManifestSha256 = mapSetInput.Sha, preSourceManifestSha256 = preInput.Sha,
                postSourceManifestSha256 = postInput.Sha, generatedInventorySha256 = generatedInput.Sha },
            files = snapshotRows, evidence = evidenceRows,
            claims = new { prePostTrackedManifestBytesIdentical = true, mappedOrdinarySourceBytesMatchPinnedManifest = true,
                generatedCurrentBytesMatchPinnedInventory = true, cscReadOrConsumedResponse = "NotEstablished",
                completeCompilerInputClosure = "NotEstablished", provenance = "UnqualifiedLocalSnapshot", admission = "NotAdmitted" }
        };
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions { WriteIndented = true });
        return new(outputDirectory, manifestBytes, writes);
    }

    private static void AddSnapshot(Dictionary<string, SnapshotFile> snapshots, string role, string original,
        string relative, long bytes, string sha, byte[] content, string node)
    {
        if (bytes != content.LongLength || !Hash(content).Equals(sha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Snapshot content changed after input verification.");
        if (snapshots.TryGetValue(relative, out SnapshotFile? existing))
        {
            if (existing.Role != role || existing.Bytes != bytes || existing.Sha != sha || !SamePath(existing.OriginalPath, original))
                throw new InvalidDataException("Conflicting source identities map to one snapshot path.");
            existing.Nodes.Add(node);
            return;
        }
        var item = new SnapshotFile { Role = role, OriginalPath = original, SnapshotPath = relative,
            Bytes = bytes, Sha = sha.ToLowerInvariant(), Content = content };
        item.Nodes.Add(node); snapshots.Add(relative, item);
    }

    private static Dictionary<string, FileIdentity> ReadSourceManifest(byte[] bytes, string root, int expectedRows)
    {
        using JsonDocument doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() != expectedRows)
            throw new InvalidDataException("Pinned Golden source manifest row count differs from expected count.");
        var result = new Dictionary<string, FileIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            CheckDuplicateProperties(row); RequireProperties(row, "bytes", "path", "sha256");
            string relative = NormalizeRelative(Get(row, "path")); long length = Long(row, "bytes");
            string sha = Get(row, "sha256"); RequireSha(sha, "source-manifest row");
            if (length is < 0 or > MaxSourceFileBytes || !result.TryAdd(relative, new(length, sha.ToLowerInvariant())))
                throw new InvalidDataException("Source-manifest row length is invalid or path duplicated.");
        }
        return result;
    }

    private static byte[] ReadBytes(string path, int max)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 0 || stream.Length > max) throw new InvalidDataException("File exceeds read limit.");
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    private static PinnedInput ReadPinnedJson(string path, string pin, string label)
    {
        string safe = SafeExistingFile(path, MaxJsonBytes, allowEmpty: false);
        byte[] bytes = ReadBytes(safe, MaxJsonBytes); string sha = Hash(bytes);
        if (!sha.Equals(pin, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(label + " bytes differ from external SHA-256 pin.");
        return new(Path.GetFileName(safe), bytes, sha);
    }
    private static string SafeDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Directory path must be absolute.");
        string full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new InvalidDataException("Directory is missing.");
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Directory path contains a reparse point.");
        return full;
    }
    private static string SafeExistingFile(string path, int max, bool allowEmpty)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("File path must be absolute.");
        string full = Path.GetFullPath(path); var file = new FileInfo(full);
        if (!file.Exists || (!allowEmpty && file.Length == 0) || file.Length > max || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("File is missing, empty, oversized or linked.");
        for (var current = file.Directory; current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("File path contains a reparse point.");
        return full;
    }
    private static bool Under(string root, string path, bool allowRoot)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return (allowRoot && fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) ||
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static string Relative(string root, string full)
    {
        if (!Under(root, full, allowRoot: false)) throw new InvalidDataException("Path is outside source root.");
        return NormalizeRelative(Path.GetRelativePath(root, full));
    }
    private static string NormalizeRelative(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || Path.IsPathFullyQualified(path) || normalized.Contains(':'))
            throw new InvalidDataException("Manifest path is not a relative path.");
        string[] parts = normalized.Split('/');
        if (parts.Any(p => p.Length == 0 || p == "." || p == "..")) throw new InvalidDataException("Manifest relative path escapes its root.");
        return string.Join('/', parts);
    }
    private static string UnderRoot(string root, string relative)
    {
        string normalized = NormalizeRelative(relative);
        string full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!Under(root, full, allowRoot: false)) throw new InvalidDataException("Output path escapes package root.");
        return full;
    }
    private static string EvidenceRelative(string name) => name switch
    {
        "manifest.json" => "evidence/source-map-set/manifest.json",
        "source-manifest-pre.json" => "evidence/source-manifest/pre.json",
        "source-manifest-post.json" => "evidence/source-manifest/post.json",
        GeneratedInventoryName => "evidence/source-maps/" + GeneratedInventoryName,
        _ when name.EndsWith(".map.json", StringComparison.Ordinal) => "evidence/source-maps/" + name,
        _ => throw new InvalidDataException("Unsupported evidence input name.")
    };
    private static string ValidateOutputDirectory(string path)
    {
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Output directory must be absolute.");
        string full = Path.GetFullPath(path);
        if (!Path.GetDirectoryName(full)!.Equals(temp, StringComparison.OrdinalIgnoreCase) || Directory.Exists(full) || File.Exists(full))
            throw new InvalidDataException("Output must be a new direct child of TEMP.");
        return full;
    }
    private static void CreateDirectoryExclusive(string path)
    {
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string full = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(path) || !Path.GetDirectoryName(full)!.Equals(temp, StringComparison.OrdinalIgnoreCase) || Directory.Exists(full) || File.Exists(full))
            throw new InvalidDataException("Output must be a new direct child of TEMP.");
        if (!CreateDirectoryW(full, IntPtr.Zero)) throw new IOException("Exclusive output directory creation failed (Win32 " + Marshal.GetLastWin32Error() + ").");
    }
    private static void WriteNewVerified(string path, byte[] bytes, string sha)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes); stream.Flush(flushToDisk: true);
        }
        byte[] written = ReadBytes(path, checked((int)Math.Max(bytes.LongLength, 1)));
        if (written.LongLength != bytes.LongLength || !Hash(written).Equals(sha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Published bytes differ from their expected SHA-256.");
    }
    private static string Get(JsonElement value, string property) => GetString(value.GetProperty(property));
    private static string GetString(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException("Expected JSON string.");
    private static int Int(JsonElement value, string property) => checked((int)Long(value, property));
    private static long Long(JsonElement value, string property) => value.GetProperty(property).ValueKind == JsonValueKind.Number ? value.GetProperty(property).GetInt64() : throw new InvalidDataException("Expected integer JSON value.");
    private static decimal Decimal(JsonElement value) => value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : throw new InvalidDataException("Expected numeric JSON value.");
    private static void RequireProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected JSON object.");
        string[] actual = value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw new InvalidDataException("Unexpected/missing JSON property.");
    }
    private static void CheckDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
                CheckDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) CheckDuplicateProperties(item);
    }
    private static void RequireSha(string value, string label)
    { if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid " + label + " SHA-256."); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool SamePath(string a, string b) => Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
}
