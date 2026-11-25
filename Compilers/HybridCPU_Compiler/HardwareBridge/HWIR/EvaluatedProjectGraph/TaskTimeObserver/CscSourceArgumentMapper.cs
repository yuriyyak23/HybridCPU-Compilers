using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Maps pinned Csc response source operands to one evaluated Compile inventory.</summary>
internal static class CscSourceArgumentMapper
{
    private const int MaxGraphBytes = 32 * 1024 * 1024;
    private const int MaxRspBytes = 2 * 1024 * 1024;
    private const int MaxManifestBytes = 8 * 1024 * 1024;
    private const int MaxGeneratedBytes = 128 * 1024 * 1024;
    private const int MaxSources = 20000;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 10 || args[0] != "map")
                throw new InvalidDataException("Usage: map <graph> <graph-sha256> <node-id> <rsp-copy> <rsp-sha256> <generated-inventory> <inventory-sha256> <working-directory> <new-output>");
            byte[] output = Map(args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8]);
            string destination = Path.GetFullPath(args[9]);
            string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            string parent = Path.GetDirectoryName(destination) ?? "";
            if (!Path.IsPathFullyQualified(args[9]) || !Under(parent, temp) || !Directory.Exists(parent) ||
                File.Exists(destination) || Directory.Exists(destination))
                throw new InvalidDataException("Output must be a new file below an existing TEMP directory.");
            for (var dir = new DirectoryInfo(parent); dir is not null; dir = dir.Parent)
            {
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Output has a linked ancestor.");
                if (SamePath(dir.FullName, temp)) break;
            }
            using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(output);
            stream.Flush(flushToDisk: true);
            Console.WriteLine($"PASS mapped {JsonDocument.Parse(output).RootElement.GetProperty("sources").GetArrayLength()} source operands; Csc read/consumption is NotEstablished.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
                                      ArgumentException or InvalidOperationException or FormatException or DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine("REFUSED: " + ex.Message);
            return 2;
        }
    }

    private static byte[] Map(string graphPath, string graphPin, string nodeId, string rspPath, string rspPin,
        string inventoryPath, string inventoryPin, string cwd)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidDataException("Windows argv parsing is required.");
        RequireSha(graphPin, "graph"); RequireSha(rspPin, "response"); RequireSha(inventoryPin, "generated inventory");
        string graphFile = SafeFile(graphPath, MaxGraphBytes), rspFile = SafeFile(rspPath, MaxRspBytes);
        string inventoryFile = SafeFile(inventoryPath, MaxManifestBytes);
        byte[] graphBytes = ReadBounded(graphFile, MaxGraphBytes), rspBytes = ReadBounded(rspFile, MaxRspBytes), inventoryBytes = ReadBounded(inventoryFile, MaxManifestBytes);
        string graphSha = Hash(graphBytes), rspSha = Hash(rspBytes), inventorySha = Hash(inventoryBytes);
        if (!graphSha.Equals(graphPin, StringComparison.OrdinalIgnoreCase) || !rspSha.Equals(rspPin, StringComparison.OrdinalIgnoreCase) ||
            !inventorySha.Equals(inventoryPin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Graph, response or generated-inventory bytes differ from their external SHA-256 pins.");
        using JsonDocument graphDoc = JsonDocument.Parse(graphBytes, new JsonDocumentOptions { MaxDepth = 64 });
        JsonElement graph = graphDoc.RootElement;
        CheckDuplicateProperties(graph);
        if (Get(graph, "schema") != "hardwarebridge.evaluated-project-graph/v2" ||
            Get(graph, "disposition") != "evaluation-only-unqualified")
            throw new InvalidDataException("Unsupported or qualified graph contract.");
        JsonElement nodes = graph.GetProperty("nodes");
        if (nodes.ValueKind != JsonValueKind.Array || nodes.GetArrayLength() is < 1 or > 100)
            throw new InvalidDataException("Graph node inventory is missing or outside bounds.");
        var graphNodes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement node in nodes.EnumerateArray())
        {
            string id = Get(node, "id"), nodeProject = Path.GetFullPath(Get(node, "project"));
            if (!Path.IsPathFullyQualified(Get(node, "project")) || !graphNodes.TryAdd(id, nodeProject))
                throw new InvalidDataException("Graph node identity or project path is invalid/duplicated.");
        }
        JsonElement selected = default;
        int found = 0;
        foreach (JsonElement node in nodes.EnumerateArray())
        {
            if (Get(node, "id") == nodeId) { selected = node; found++; }
        }
        if (found != 1) throw new InvalidDataException("Requested node identity is absent or duplicated.");
        string project = Path.GetFullPath(Get(selected, "project"));
        string work = Path.GetFullPath(cwd);
        if (!Path.IsPathFullyQualified(cwd) || !SamePath(Path.GetDirectoryName(project)!, work) || !File.Exists(project))
            throw new InvalidDataException("Recorded working directory does not equal the selected project's directory.");
        SafeExistingSource(project);

        JsonElement compile = selected.GetProperty("compileFiles");
        if (compile.ValueKind != JsonValueKind.Array || compile.GetArrayLength() is < 1 or > MaxSources)
            throw new InvalidDataException("Evaluated Compile inventory is missing or outside bounds.");
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in compile.EnumerateArray())
        {
            string path = GetString(item);
            if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Compile path is not absolute.");
            string normalized = Path.GetFullPath(path);
            if (!normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Compile item is not a C# source.");
            if (!expected.TryAdd(normalized, path)) throw new InvalidDataException("Duplicate evaluated Compile path.");
            SafeExistingSource(normalized);
        }

        using JsonDocument inventoryDoc = JsonDocument.Parse(inventoryBytes, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement inventory = inventoryDoc.RootElement;
        CheckDuplicateProperties(inventory);
        RequireProperties(inventory, "schema", "disposition", "graphSha256", "files");
        if (Get(inventory, "schema") != "hardwarebridge.csc-generated-source-inventory/v1" ||
            Get(inventory, "disposition") != "snapshot-unqualified" ||
            !Get(inventory, "graphSha256").Equals(graphSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported generated-source inventory schema.");
        JsonElement generatedFiles = inventory.GetProperty("files");
        if (generatedFiles.ValueKind != JsonValueKind.Array || generatedFiles.GetArrayLength() > MaxSources)
            throw new InvalidDataException("Generated-source inventory is missing or exceeds bounds.");
        var allGenerated = new Dictionary<string, (string NodeId, string Project, string Role, long Bytes, string Sha)>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement row in generatedFiles.EnumerateArray())
        {
            RequireProperties(row, "nodeId", "project", "role", "path", "bytes", "sha256");
            string rowNode = Get(row, "nodeId"), rowProjectText = Get(row, "project"), rowPathText = Get(row, "path"), role = Get(row, "role");
            if (!graphNodes.TryGetValue(rowNode, out string? graphProject) || !Path.IsPathFullyQualified(rowProjectText) ||
                !SamePath(graphProject, rowProjectText) || role != "msbuild-generated" || !Path.IsPathFullyQualified(rowPathText))
                throw new InvalidDataException("Generated row is not bound to an evaluated node/project or has an unsupported role/path.");
            string rowPath = Path.GetFullPath(rowPathText);
            if (!rowPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Generated inventory path is not a C# file.");
            long declaredBytes = row.GetProperty("bytes").ValueKind == JsonValueKind.Number ? row.GetProperty("bytes").GetInt64() : -1;
            string declaredSha = Get(row, "sha256"); RequireSha(declaredSha, "generated source");
            if (declaredBytes < 0 || declaredBytes > MaxGeneratedBytes) throw new InvalidDataException("Generated source length is outside bounds.");
            SafeExistingSource(rowPath);
            var generatedInfo = new FileInfo(rowPath);
            if (generatedInfo.Length != declaredBytes || !HashFile(rowPath).Equals(declaredSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Generated source bytes differ from pinned inventory row.");
            if (!allGenerated.TryAdd(rowPath, (rowNode, graphProject, role, declaredBytes, declaredSha.ToLowerInvariant())))
                throw new InvalidDataException("Duplicate generated-source path.");
        }
        var selectedGenerated = allGenerated.Where(pair => pair.Value.NodeId == nodeId).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (string path in selectedGenerated.Keys)
            if (expected.ContainsKey(path)) throw new InvalidDataException("Generated source duplicates an evaluated Compile path.");
        if (expected.Count + selectedGenerated.Count is < 1 or > MaxSources)
            throw new InvalidDataException("Combined Compile/generated source set is outside bounds.");

        string rspText;
        try { rspText = new UTF8Encoding(false, true).GetString(rspBytes); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Response bytes are not strict UTF-8.", ex); }
        if (rspText.Length > 0 && rspText[0] == '\uFEFF') rspText = rspText[1..];
        string[] args = ParseWindowsArgs(rspText);
        if (args.Any(x => x.StartsWith('@'))) throw new InvalidDataException("Nested response files are unsupported.");
        var mapped = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string token, int index) in args.Select((value, i) => (value, i)))
        {
            if (!token.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (token.StartsWith('/') || token.StartsWith('-')) continue;
            string absolute = Path.GetFullPath(Path.IsPathFullyQualified(token) ? token : Path.Combine(work, token));
            if (!seen.Add(absolute)) throw new InvalidDataException("Duplicate source operand in response.");
            if (expected.TryGetValue(absolute, out string? evaluated))
                mapped.Add(new { argumentIndex = index, token, resolvedPath = absolute, evaluatedCompilePath = (string?)evaluated,
                    role = "evaluated-compile", generatedBytes = (long?)null, generatedSha256 = (string?)null });
            else if (selectedGenerated.TryGetValue(absolute, out var generated))
                mapped.Add(new { argumentIndex = index, token, resolvedPath = absolute, evaluatedCompilePath = (string?)null,
                    role = generated.Role, generatedBytes = (long?)generated.Bytes, generatedSha256 = (string?)generated.Sha });
            else throw new InvalidDataException("Response source is absent from evaluated Compile and pinned generated inventory: " + absolute);
        }
        if (seen.Count != expected.Count + selectedGenerated.Count)
        {
            string? missingCompile = expected.Keys.FirstOrDefault(k => !seen.Contains(k));
            string missing = missingCompile ?? selectedGenerated.Keys.First(k => !seen.Contains(k));
            throw new InvalidDataException("Evaluated Compile/generated item is absent from response operands: " + missing);
        }
        if (mapped.Count != expected.Count + selectedGenerated.Count) throw new InvalidDataException("Source argument count differs from Compile/generated inventory.");
        var result = new
        {
            schema = "hardwarebridge.csc-source-argument-map/v1",
            disposition = "source-argument-map-unqualified",
            graphSha256 = graphSha.ToLowerInvariant(), rspSha256 = rspSha.ToLowerInvariant(), generatedInventorySha256 = inventorySha.ToLowerInvariant(),
            nodeId, project, workingDirectory = work, sourceCount = mapped.Count, generatedSourceCount = selectedGenerated.Count, sources = mapped,
            claims = new { evaluatedCompileAndPinnedGeneratedSetMatched = true, ordinarySourceBytesHashed = false,
                cscReadOrConsumedResponse = "NotEstablished", completeCompilerInputClosure = "NotEstablished", admission = "NotAdmitted" }
        };
        return JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string[] ParseWindowsArgs(string text)
    {
        if (text.Length is < 1 or > 2 * 1024 * 1024 || text.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new InvalidDataException("Response is empty, oversized or contains unsupported control characters.");
        IntPtr argv = CommandLineToArgvW(text, out int argc);
        if (argv == IntPtr.Zero) throw new InvalidDataException("CommandLineToArgvW failed.");
        try
        {
            if (argc is < 1 or > MaxSources + 8192) throw new InvalidDataException("Response token count is outside bounds.");
            var values = new string[argc];
            for (int i = 0; i < argc; i++)
            {
                values[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? throw new InvalidDataException("Invalid response token.");
                if (values[i].Length == 0 || values[i].IndexOfAny(['\0', '\r', '\n']) >= 0) throw new InvalidDataException("Empty response token or control character.");
            }
            return values;
        }
        finally { _ = LocalFree(argv); }
    }

    private static void SafeExistingSource(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Compile source is missing or linked.");
        for (var dir = file.Directory; dir is not null; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Compile source has a linked ancestor.");
    }
    private static void CheckDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate graph JSON property.");
                CheckDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in value.EnumerateArray()) CheckDuplicateProperties(child);
    }
    private static void RequireProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected JSON object.");
        string[] actual = value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        string[] expected = names.Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) throw new InvalidDataException("Unexpected or missing generated-inventory JSON property.");
    }
    private static string SafeFile(string path, int limit)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Input path must be absolute.");
        string full = Path.GetFullPath(path); var file = new FileInfo(full);
        if (!file.Exists || file.Length is < 1 || file.Length > limit || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Pinned input is missing, linked, empty or oversized.");
        for (var dir = file.Directory; dir is not null; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Input has a linked ancestor.");
        return full;
    }
    private static byte[] ReadBounded(string path, int limit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 || stream.Length > limit) throw new InvalidDataException("Input length outside bounds.");
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static string Get(JsonElement value, string property) => GetString(value.GetProperty(property));
    private static string GetString(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException("Expected JSON string.");
    private static void RequireSha(string value, string label)
    { if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid " + label + " SHA-256 pin."); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool Under(string path, string root)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return full.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) || full.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static bool SamePath(string a, string b) => Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr LocalFree(IntPtr memory);
}
