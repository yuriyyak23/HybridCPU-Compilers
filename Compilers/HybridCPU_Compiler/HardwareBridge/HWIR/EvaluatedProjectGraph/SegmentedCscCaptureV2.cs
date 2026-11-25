using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// Candidate-only C# writer for the HardwareBridge Csc closure v2 wire format.
/// It consumes previously captured graph/observation facts and never launches MSBuild or Csc.
/// </summary>
internal static class SegmentedCscCaptureV2
{
    private const string Schema = "hybridcpu.hardwarebridge.csc-closure-index/v2";
    private const string GraphSchema = "hardwarebridge.evaluated-project-graph/v2";
    private const string ObservationSchema = "hybridcpu.hardwarebridge.csc-observation/v2";
    private const int ChunkSize = 1024 * 1024;
    private const long MaxFile = 128L * ChunkSize;
    private const int MaxFiles = 20_000;
    private const long MaxBytes = 8L * 1024 * ChunkSize;
    private const int MaxIndex = 16 * ChunkSize;
    private const int MaxGraph = 4 * ChunkSize;
    private const int MaxObservation = 8 * ChunkSize;
    private static readonly HashSet<string> InputKinds = new(StringComparer.Ordinal)
    {
        "csc-source", "generator-source", "reference", "analyzer", "analyzer-config",
        "additional-file", "source-link", "app-config", "embedded", "csc-binary", "project", "assets"
    };
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record FileIdentity(string Path, string LogicalPath, string Kind, string Role,
        string Sha256, long Bytes, string? OriginalPath = null);
    private sealed record Chunk(string Name, int Bytes, string Sha256);
    private sealed record Entry(string NodeId, string Kind, string Role, string LogicalPath,
        string Path, string Sha256, long Bytes, List<Chunk>? Chunks = null);
    private sealed record Command(string NodeId, string ProjectRole, string ArgvSha256,
        JsonElement Argv, JsonElement GlobalProperties, string WorkingDirectory,
        string? GeneratorOutputDirectory);
    private sealed record FileArgument(string Kind, string Path);
    private sealed record Inputs(List<Entry> Entries, List<Command> Commands,
        List<FileIdentity> Originals, string GraphSha256, string ObservationSha256);

    public static int Run(string[] args)
    {
        if (args.Length is not (4 or 5))
            throw new ArgumentException("capture-v2 requires <graph.json> <observation.json> <golden-root> <new-temp-output> [timeout-seconds]");
        string graph = RequireAbsolute(args[0], "graph");
        string observation = RequireAbsolute(args[1], "observation");
        string goldenRoot = RequireAbsolute(args[2], "Golden root");
        string output = RequireAbsolute(args[3], "output");
        double timeout = args.Length == 5 && double.TryParse(args[4], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double seconds) ? seconds : args.Length == 5 ? 0 : 300;
        if (!double.IsFinite(timeout) || timeout <= 0 || timeout > 3600)
            throw new InvalidDataException("Capture timeout must be within 0..3600 seconds.");

        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string outputParent = Path.GetDirectoryName(output) ?? throw new InvalidDataException("Output has no parent directory.");
        if (!PathEquals(Path.GetFullPath(outputParent), tempRoot) || Directory.Exists(output) || File.Exists(output))
            throw new InvalidDataException("Output must be a new direct child of TEMP.");
        ValidatePath(tempRoot, isDirectory: true);
        ValidatePath(goldenRoot, isDirectory: true);
        if (IsUnder(output, goldenRoot)) throw new InvalidDataException("Capture output is nested in the Golden tree.");

        Stopwatch clock = Stopwatch.StartNew();
        Inputs inputs = BuildInputs(graph, observation, goldenRoot, clock, timeout);
        CheckTime(clock, timeout);
        CreateNewDirectory(output);
        try
        {
            int chunkNumber = 0;
            for (int entryIndex = 0; entryIndex < inputs.Entries.Count; entryIndex++)
            {
                Entry entry = inputs.Entries[entryIndex] with { Chunks = [] };
                inputs.Entries[entryIndex] = entry;
                CheckTime(clock, timeout);
                using FileStream source = OpenStable(entry.Path, out FileState before);
                using IncrementalHash fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = 0;
                byte[] buffer = new byte[ChunkSize];
                while (true)
                {
                    CheckTime(clock, timeout);
                    int read = source.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    total += read;
                    if (total > MaxFile) throw new InvalidDataException("Oversized compiler input while copying: " + entry.Path);
                    ReadOnlySpan<byte> bytes = buffer.AsSpan(0, read);
                    fileHash.AppendData(bytes);
                    string name = $"chunk-{chunkNumber:D8}.bin";
                    string destination = Path.Combine(output, name);
                    using (FileStream sink = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        sink.Write(bytes);
                        sink.Flush(flushToDisk: true);
                    }
                    entry.Chunks.Add(new(name, read, Sha(bytes)));
                    chunkNumber++;
                }
                EnsureStable(source, entry.Path, before);
                string actual = Convert.ToHexStringLower(fileHash.GetHashAndReset());
                if (total != entry.Bytes || !StringComparer.Ordinal.Equals(actual, entry.Sha256))
                    throw new InvalidDataException("Input changed while captured: " + entry.Path);
            }

            CheckTime(clock, timeout);
            foreach (Entry entry in inputs.Entries)
            {
                CheckTime(clock, timeout);
                VerifyFile(entry.Path, entry.Sha256, entry.Bytes);
            }
            foreach (FileIdentity original in inputs.Originals)
            {
                CheckTime(clock, timeout);
                VerifyFile(original.Path, original.Sha256, original.Bytes);
            }
            if (Sha(ReadBounded(graph, MaxGraph)) != inputs.GraphSha256 ||
                Sha(ReadBounded(observation, MaxObservation)) != inputs.ObservationSha256)
                throw new InvalidDataException("Graph/observation changed during capture.");

            List<JsonObject> rows = inputs.Entries.Select(e => new JsonObject
            {
                ["nodeId"] = e.NodeId, ["kind"] = e.Kind, ["role"] = e.Role,
                ["logicalPath"] = e.LogicalPath, ["path"] = e.Path, ["sha256"] = e.Sha256, ["bytes"] = e.Bytes,
                ["chunks"] = new JsonArray(e.Chunks!.Select(c => (JsonNode?)new JsonObject
                { ["name"] = c.Name, ["bytes"] = c.Bytes, ["sha256"] = c.Sha256 }).ToArray())
            }).ToList();
            List<JsonObject> commands = inputs.Commands.Select(c => new JsonObject
            {
                ["nodeId"] = c.NodeId, ["projectRole"] = c.ProjectRole, ["argvSha256"] = c.ArgvSha256,
                ["argv"] = JsonNode.Parse(c.Argv.GetRawText()),
                ["globalProperties"] = JsonNode.Parse(c.GlobalProperties.GetRawText()),
                ["workingDirectory"] = c.WorkingDirectory,
                ["generatorOutputDirectory"] = c.GeneratorOutputDirectory
            }).ToList();
            var payload = new JsonObject
            {
                ["schema"] = Schema,
                ["disposition"] = "unqualified-software-observation",
                ["graphSha256"] = inputs.GraphSha256,
                ["observationSha256"] = inputs.ObservationSha256,
                ["chunkSizeLimit"] = ChunkSize,
                ["commands"] = new JsonArray(commands.Select(x => (JsonNode?)x).ToArray()),
                ["entries"] = new JsonArray(rows.Select(x => (JsonNode?)x).ToArray())
            };
            string closure = Sha(Canonical(payload));
            payload["closureRootSha256"] = closure;
            byte[] index = Canonical(payload).Concat([(byte)'\n']).ToArray();
            if (index.Length > MaxIndex) throw new InvalidDataException("V2 index exceeds size limit.");
            WriteNew(Path.Combine(output, "index.json"), index);
            VerifyOutput(output, inputs.GraphSha256, inputs.Commands.Select(x => x.NodeId).ToArray());
            CheckTime(clock, timeout);
            Console.WriteLine($"HB-INT-02 C# candidate raw closure sha256={closure} indexSha256={Sha(index)} entries={inputs.Entries.Count}");
            return 0;
        }
        catch
        {
            // Keep partial TEMP evidence for inspection; never recursively delete a path here.
            throw;
        }
    }

    private static Inputs BuildInputs(string graphPath, string observationPath, string goldenRoot,
        Stopwatch clock, double timeout)
    {
        byte[] graphBytes = ReadBounded(graphPath, MaxGraph);
        byte[] observationBytes = ReadBounded(observationPath, MaxObservation);
        using JsonDocument graphDoc = ParseStrict(graphBytes);
        using JsonDocument observationDoc = ParseStrict(observationBytes);
        JsonElement graph = graphDoc.RootElement, observation = observationDoc.RootElement;
        RequireKind(graph, JsonValueKind.Object, "graph");
        RequireKind(observation, JsonValueKind.Object, "observation");
        if (Text(graph, "schema") != GraphSchema || Text(observation, "schema") != ObservationSchema ||
            Text(observation, "graphSha256") != Sha(graphBytes))
            throw new InvalidDataException("Graph/observation schema or graph hash mismatch.");
        JsonElement nodesElement = graph.GetProperty("nodes");
        if (nodesElement.ValueKind != JsonValueKind.Array || nodesElement.GetArrayLength() is < 1 or > 100)
            throw new InvalidDataException("Evaluated graph is empty or oversized.");
        string rootProject = Text(graph, "root");
        var nodes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        bool rootFound = false;
        foreach (JsonElement node in nodesElement.EnumerateArray())
        {
            string id = Text(node, "id");
            if (!nodes.TryAdd(id, node)) throw new InvalidDataException("Duplicate evaluated node identity.");
            if (Text(node, "project") == rootProject) rootFound = true;
        }
        if (!rootFound) throw new InvalidDataException("Graph root project is missing.");
        JsonElement projects = observation.GetProperty("projects");
        if (projects.ValueKind != JsonValueKind.Array || projects.GetArrayLength() != nodes.Count)
            throw new InvalidDataException("Csc observations do not cover the evaluated graph.");

        var entries = new List<Entry>();
        var commands = new List<Command>();
        var originals = new List<FileIdentity>();
        var seenNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement project in projects.EnumerateArray())
        {
            string nodeId = Text(project, "nodeId");
            if (!seenNodes.Add(nodeId) || !nodes.TryGetValue(nodeId, out JsonElement node))
                throw new InvalidDataException("Duplicate or unknown observed project identity.");
            string role = Text(project, "role");
            string nodeProject = Text(node, "project");
            if (!nodeId.StartsWith(nodeProject + "|", PathComparison))
                throw new InvalidDataException("Observed node identity is not bound to its evaluated project path.");
            if (role is not ("primary" or "owned-body" or "metadata-only") ||
                (nodeProject == rootProject) != (role == "primary"))
                throw new InvalidDataException("Inconsistent root/owned/metadata project role.");
            string cwd = ValidatePath(Text(project, "workingDirectory"), isDirectory: true);
            string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!IsUnder(cwd, tempRoot) || PathEquals(cwd, tempRoot) || IsUnder(cwd, goldenRoot))
                throw new InvalidDataException("Csc observation directory must be isolated under TEMP.");
            JsonElement argv = project.GetProperty("argv");
            string argvSha = Text(project, "argvSha256");
            if (Sha(Canonical(argv)) != argvSha) throw new InvalidDataException("Changed Csc option/argument vector.");
            (List<string> sources, List<FileArgument> artifacts, string? generatorDir) = CommandFiles(argv, cwd);
            JsonElement graphCompile = node.GetProperty("compileFiles");
            var expectedOriginals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement compile in graphCompile.EnumerateArray())
            {
                string p = CompilerInputPath(compile.GetString()!, Path.GetDirectoryName(nodeProject)!, isDirectory: false);
                if (!expectedOriginals.Add(p)) throw new InvalidDataException("Duplicate evaluated Compile path.");
            }
            JsonElement sourceRows = project.GetProperty("sources");
            if (sourceRows.ValueKind != JsonValueKind.Array || sourceRows.GetArrayLength() != sources.Count)
                throw new InvalidDataException("Missing or extra Csc source map.");
            var sourceMap = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement source in sourceRows.EnumerateArray())
            {
                string path = CompilerInputPath(Text(source, "path"), cwd, isDirectory: false);
                if (!sourceMap.TryAdd(path, source)) throw new InvalidDataException("Duplicate Csc source map path.");
            }
            if (sourceMap.Count != sources.Count || !sourceMap.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(sources))
                throw new InvalidDataException("Csc source map differs from argv.");

            var seenOriginals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string sourcePath in sources)
            {
                JsonElement source = sourceMap[sourcePath];
                string origin = Text(source, "origin");
                string? original = OptionalText(source, "originalPath");
                if (origin == "evaluated")
                {
                    if (original is null) throw new InvalidDataException("Evaluated source has no original path.");
                    original = ValidatePath(original, isDirectory: false);
                    if (!expectedOriginals.Contains(original) || !seenOriginals.Add(original))
                        throw new InvalidDataException("Unexpected or duplicate evaluated source.");
                }
                else if (origin == "msbuild-generated" && original is null) { }
                else throw new InvalidDataException("Unsupported Csc source origin.");
                string logical = Logical(Text(source, "logicalPath"));
                string hash = ShaPin(Text(source, "sha256"));
                long length = VerifyFile(sourcePath, hash, null);
                if (original is not null)
                {
                    if (!IsUnder(original, goldenRoot) || VerifyFile(original, hash, length) != length)
                        throw new InvalidDataException("Golden original and isolated Csc source bytes differ.");
                    if (AreSameFile(original, sourcePath))
                        throw new InvalidDataException("Csc source is an alias/hardlink of Golden source.");
                    originals.Add(new(original, logical, "csc-source", role, hash, length));
                }
                AddEntry(entries, nodeId, "csc-source", role, logical, sourcePath, hash, length, original);
            }
            if (!seenOriginals.SetEquals(expectedOriginals))
                throw new InvalidDataException("Csc invocation misses one or more evaluated Compile items.");

            CaptureGenerated(project, nodeId, role, generatorDir, entries, clock, timeout);
            CaptureFileArguments(project, nodeId, artifacts, entries);
            string compilerPath = ValidatePath(Text(project.GetProperty("compiler"), "path"), isDirectory: false);
            AddEntry(entries, nodeId, "csc-binary", "metadata-only", "tools/csc", compilerPath,
                ShaPin(Text(project.GetProperty("compiler"), "sha256")), null);
            string projectPath = ValidatePath(nodeProject, isDirectory: false);
            string projectSha = ShaPin(Text(project, "projectSha256"));
            if (nodeProject == rootProject && graph.TryGetProperty("rootProjectSha256", out JsonElement rootProjectSha) &&
                rootProjectSha.ValueKind == JsonValueKind.String && rootProjectSha.GetString() != projectSha)
                throw new InvalidDataException("Root evaluated project changed since graph observation.");
            AddEntry(entries, nodeId, "project", "metadata-only", "msbuild/project", projectPath, projectSha, null);
            JsonElement assets = node.GetProperty("projectAssets");
            if (assets.ValueKind == JsonValueKind.Object)
                AddEntry(entries, nodeId, "assets", "metadata-only", "msbuild/assets",
                    ValidatePath(Text(assets, "path"), isDirectory: false), ShaPin(Text(assets, "sha256")), null);

            JsonElement globalProperties = node.GetProperty("globalProperties");
            if (globalProperties.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Project node global properties must be an object.");
            commands.Add(new(nodeId, role, argvSha, argv.Clone(), globalProperties.Clone(), cwd, generatorDir));
        }
        if (seenNodes.Count != nodes.Count) throw new InvalidDataException("Csc observations omit graph nodes.");
        if (entries.Count > MaxFiles || entries.Sum(x => x.Bytes) > MaxBytes)
            throw new InvalidDataException("Source closure exceeds file or byte budget.");
        entries.Sort((a, b) => ComparePythonStrings((a.NodeId, a.Kind, a.LogicalPath),
            (b.NodeId, b.Kind, b.LogicalPath)));
        for (int i = 1; i < entries.Count; i++)
            if (StringComparer.OrdinalIgnoreCase.Equals(entries[i - 1].NodeId, entries[i].NodeId) &&
                entries[i - 1].Kind == entries[i].Kind &&
                StringComparer.OrdinalIgnoreCase.Equals(entries[i - 1].LogicalPath, entries[i].LogicalPath))
                throw new InvalidDataException("Duplicate ordered closure identity.");
        commands.Sort((a, b) => ComparePythonStrings(a.NodeId, b.NodeId));
        return new(entries, commands, originals, Sha(graphBytes), Sha(observationBytes));
    }

    private static void CaptureGenerated(JsonElement project, string nodeId, string role,
        string? generatorDir, List<Entry> entries, Stopwatch clock, double timeout)
    {
        JsonElement generated = project.GetProperty("generatorSources");
        if (generated.ValueKind != JsonValueKind.Array || generated.GetArrayLength() > 5000)
            throw new InvalidDataException("Invalid generated-source inventory.");
        if (generatorDir is null && generated.GetArrayLength() != 0)
            throw new InvalidDataException("Generator outputs exist without /generatedfilesout.");
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement source in generated.EnumerateArray())
        {
            string path = CompilerInputPath(Text(source, "path"), generatorDir!, isDirectory: false);
            if (!declared.Add(path)) throw new InvalidDataException("Duplicate generated source path.");
        }
        if (generatorDir is not null)
        {
            var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in EnumerateFilesWithoutReparse(generatorDir, clock, timeout))
            {
                ValidatePath(file, isDirectory: false);
                if (Path.GetExtension(file) == ".cs") actual.Add(Path.GetFullPath(file));
                if (actual.Count > 5000) throw new InvalidDataException("Excessive generator source inventory.");
            }
            if (!actual.SetEquals(declared)) throw new InvalidDataException("Missing or extra emitted generator source.");
        }
        foreach (JsonElement source in generated.EnumerateArray())
        {
            string path = CompilerInputPath(Text(source, "path"), generatorDir!, isDirectory: false);
            string logical = Logical(Text(source, "logicalPath"));
            string sha = ShaPin(Text(source, "sha256"));
            long bytes = VerifyFile(path, sha, null);
            AddEntry(entries, nodeId, "generator-source", role, logical, path, sha, bytes);
        }
    }

    private static IEnumerable<string> EnumerateFilesWithoutReparse(string root, Stopwatch clock, double timeout)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        int visited = 0;
        while (pending.Count != 0)
        {
            CheckTime(clock, timeout);
            string directory = pending.Pop();
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                CheckTime(clock, timeout);
                if (++visited > MaxFiles)
                    throw new InvalidDataException("Generator output tree exceeds the bounded path count.");
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked generator output path refused: " + path);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else yield return path;
            }
        }
    }

    private static void CaptureFileArguments(JsonElement project, string nodeId,
        List<FileArgument> artifacts, List<Entry> entries)
    {
        JsonElement pins = project.GetProperty("filePins");
        if (pins.ValueKind != JsonValueKind.Array || pins.GetArrayLength() != artifacts.Count)
            throw new InvalidDataException("Missing Csc reference/analyzer byte pins.");
        var pinMap = new Dictionary<(string Kind, string Path), string>();
        foreach (JsonElement pin in pins.EnumerateArray())
        {
            string kind = Text(pin, "kind");
            string path = CompilerInputPath(Text(pin, "path"), Text(project, "workingDirectory"), isDirectory: false);
            if (!pinMap.TryAdd((kind, path), ShaPin(Text(pin, "sha256"))))
                throw new InvalidDataException("Duplicate Csc input file pin.");
        }
        foreach (FileArgument artifact in artifacts)
        {
            if (!pinMap.TryGetValue((artifact.Kind, artifact.Path), out string? pin))
                throw new InvalidDataException("Csc input pin set differs from argv.");
            string name = Path.GetFileName(artifact.Path);
            string logical = Logical($"metadata/{artifact.Kind}/{Logical(name)}/{pin}");
            AddEntry(entries, nodeId, artifact.Kind, "metadata-only", logical, artifact.Path, pin, null);
        }
        if (pinMap.Count != artifacts.Count) throw new InvalidDataException("Extra Csc input file pin.");
    }

    private static (List<string> Sources, List<FileArgument> Artifacts, string? GeneratorDir)
        CommandFiles(JsonElement argv, string cwd)
    {
        if (argv.ValueKind != JsonValueKind.Array || argv.GetArrayLength() is < 1 or > 8192)
            throw new InvalidDataException("Invalid full Csc argument vector.");
        string[] tokens = argv.EnumerateArray().Select(x =>
        {
            if (x.ValueKind != JsonValueKind.String) throw new InvalidDataException("Csc argv tokens must be strings.");
            string s = x.GetString()!;
            if (s.Length is 0 or > 8192 || s.Contains('\0')) throw new InvalidDataException("Invalid Csc argv token.");
            return s;
        }).ToArray();
        if (tokens.Any(x => x.StartsWith('@'))) throw new InvalidDataException("Unexpanded response file; exact Csc argv unavailable.");
        foreach (string prefix in new[] { "/langversion:", "/define:", "/nullable:", "/target:", "/out:" })
            if (tokens.Count(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidDataException("Missing or duplicate explicit Csc option " + prefix);
        if (!tokens.Any(x => x.Equals("/target:library", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Unsupported Csc output kind.");

        var sourceList = new List<string>();
        var artifactList = new List<FileArgument>();
        string? generatorDir = null;
        (string Prefix, string Kind)[] filePrefixes =
        [
            ("/reference:", "reference"), ("/r:", "reference"), ("/analyzer:", "analyzer"),
            ("/analyzerconfig:", "analyzer-config"), ("/additionalfile:", "additional-file"),
            ("/sourcelink:", "source-link"), ("/appconfig:", "app-config"), ("/embed:", "embedded")
        ];
        string[] outputPrefixes = ["/out:", "/pdb:", "/refout:", "/doc:", "/errorlog:"];
        string[] scalarPrefixes =
        [
            "/langversion:", "/define:", "/nullable:", "/target:", "/debug:", "/platform:",
            "/warn:", "/nowarn:", "/warnaserror:", "/warnaserror+:", "/warnaserror-:",
            "/features:", "/pathmap:", "/usings:", "/moduleassemblyname:", "/main:",
            "/baseaddress:", "/filealign:", "/preferreduilang:", "/instrument:",
            "/checksumalgorithm:", "/errorendlocation:"
        ];
        HashSet<string> scalarFlags = new(StringComparer.OrdinalIgnoreCase)
        {
            "/errorreport:prompt", "/noconfig", "/fullpaths", "/utf8output", "/nostdlib", "/nostdlib+",
            "/nostdlib-", "/checked", "/checked+", "/checked-", "/unsafe", "/unsafe+", "/unsafe-",
            "/optimize", "/optimize+", "/optimize-", "/deterministic", "/deterministic+", "/deterministic-",
            "/parallel+", "/parallel-", "/publicsign+", "/publicsign-", "/warnaserror+", "/warnaserror-",
            "/debug", "/debug+", "/debug-", "/highentropyva+", "/highentropyva-"
        };
        foreach (string token in tokens)
        {
            string lower = token.ToLowerInvariant();
            bool isSource = lower.EndsWith(".cs", StringComparison.Ordinal) &&
                (!token.StartsWith('/') && !token.StartsWith('-') ||
                 Path.IsPathFullyQualified(token) && !System.Text.RegularExpressions.Regex.IsMatch(lower, "^/[a-z][a-z0-9+-]*:"));
            if (isSource) { sourceList.Add(CompilerInputPath(token, cwd, false)); continue; }
            (string Prefix, string Kind)? matched = filePrefixes
                .Where(x => lower.StartsWith(x.Prefix, StringComparison.Ordinal))
                .Select(x => ((string Prefix, string Kind)?)x).FirstOrDefault();
            if (matched is { } file)
            {
                string pathToken = token[(file.Prefix.Length)..];
                if (file.Kind == "reference" && pathToken.Contains('='))
                    throw new InvalidDataException("Reference aliases require a separate versioned contract.");
                artifactList.Add(new(file.Kind, CompilerInputPath(pathToken, cwd, false)));
                continue;
            }
            const string generatedPrefix = "/generatedfilesout:";
            if (lower.StartsWith(generatedPrefix, StringComparison.Ordinal))
            {
                if (generatorDir is not null) throw new InvalidDataException("Duplicate generatedfilesout.");
                generatorDir = CompilerInputPath(token[generatedPrefix.Length..], cwd, true);
                continue;
            }
            string? outputPrefix = outputPrefixes.FirstOrDefault(x => lower.StartsWith(x, StringComparison.Ordinal));
            if (outputPrefix is not null)
            {
                string path = token[outputPrefix.Length..];
                string full = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(cwd, path));
                if (!IsUnder(full, Path.GetFullPath(Path.GetTempPath())))
                    throw new InvalidDataException("Csc output path is not isolated in TEMP.");
                continue;
            }
            if (scalarFlags.Contains(token) || scalarPrefixes.Any(x => lower.StartsWith(x, StringComparison.Ordinal))) continue;
            throw new InvalidDataException("Unsupported/unclassified Csc argument: " + token[..Math.Min(80, token.Length)]);
        }
        if (sourceList.Count == 0 || sourceList.Distinct(PathComparer()).Count() != sourceList.Count)
            throw new InvalidDataException("Missing or duplicate Csc source arguments.");
        if (artifactList.Select(x => x.Kind + "\0" + PathKey(x.Path)).Distinct(StringComparer.Ordinal).Count() != artifactList.Count)
            throw new InvalidDataException("Duplicate Csc file arguments.");
        if (artifactList.Any(x => x.Kind == "analyzer") && generatorDir is null)
            throw new InvalidDataException("Generator/analyzer output capture missing.");
        return (sourceList, artifactList, generatorDir);
    }

    private static void AddEntry(List<Entry> entries, string nodeId, string kind, string role,
        string logical, string path, string sha, long? bytes, string? original = null)
    {
        if (!InputKinds.Contains(kind)) throw new InvalidDataException("Unknown input kind.");
        if (role is not ("primary" or "owned-body" or "metadata-only")) throw new InvalidDataException("Unknown ownership role.");
        if (kind is not ("csc-source" or "generator-source") && role != "metadata-only")
            throw new InvalidDataException("Metadata input promoted to executable body.");
        long actualBytes = VerifyFile(path, ShaPin(sha), bytes);
        entries.Add(new(nodeId, kind, role, Logical(logical), path, sha, actualBytes));
        if (entries.Count > MaxFiles) throw new InvalidDataException("Source closure exceeds file count budget.");
    }

    private sealed record FileState(long Length, DateTime LastWriteUtc, DateTime CreationUtc, FileAttributes Attributes);
    private static FileStream OpenStable(string path, out FileState state)
    {
        string checkedPath = ValidatePath(path, false);
        FileInfo before = new(checkedPath);
        state = new(before.Length, before.LastWriteTimeUtc, before.CreationTimeUtc, before.Attributes);
        return new FileStream(checkedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            ChunkSize, FileOptions.SequentialScan);
    }
    private static void EnsureStable(FileStream stream, string path, FileState before)
    {
        FileInfo after = new(path);
        FileState fd = new(stream.Length, after.LastWriteTimeUtc, after.CreationTimeUtc, after.Attributes);
        if (!after.Exists || (after.Attributes & FileAttributes.ReparsePoint) != 0 || fd != before)
            throw new InvalidDataException("Concurrent file mutation/replacement: " + path);
    }
    private static long VerifyFile(string path, string expectedSha, long? expectedBytes)
    {
        using FileStream stream = OpenStable(path, out FileState before);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[ChunkSize];
        long total = 0;
        while (true)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total += read;
            if (total > MaxFile) throw new InvalidDataException("Oversized compiler input: " + path);
            hash.AppendData(buffer, 0, read);
        }
        EnsureStable(stream, path, before);
        string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!StringComparer.Ordinal.Equals(actual, expectedSha) || expectedBytes is long expected && total != expected)
            throw new InvalidDataException("Changed/stale file hash: " + path);
        return total;
    }

    private static string CompilerInputPath(string value, string cwd, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0')) throw new InvalidDataException("Invalid compiler input path.");
        if (!Path.IsPathFullyQualified(value))
        {
            if (Path.IsPathRooted(value)) throw new InvalidDataException("Rooted compiler path lacks an absolute drive/share.");
            value = Path.Combine(cwd, value);
        }
        if (OperatingSystem.IsWindows())
        {
            value = value.Replace('/', '\\');
            if (value.StartsWith("\\\\?\\", StringComparison.Ordinal) || value.StartsWith("\\\\.\\", StringComparison.Ordinal))
                throw new InvalidDataException("Device compiler input path refused.");
        }
        string? root = Path.GetPathRoot(value);
        if (root is null) throw new InvalidDataException("Compiler path has no root.");
        string[] rawParts = value[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>();
        for (int i = 0; i < rawParts.Length; i++)
        {
            string part = rawParts[i];
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new InvalidDataException("Compiler path traverses above its volume/share root.");
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            if (OperatingSystem.IsWindows() && (part.EndsWith(' ') || part.EndsWith('.')))
                throw new InvalidDataException("Ambiguous compiler path component.");
            string candidate = Path.Combine(root, Path.Combine(parts.Append(part).ToArray()));
            FileAttributes attributes = File.GetAttributes(candidate);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse compiler input path refused: " + candidate);
            bool laterComponent = rawParts.Skip(i + 1).Any(x => x.Length != 0 && x != ".");
            if (laterComponent && (attributes & FileAttributes.Directory) == 0)
                throw new InvalidDataException("Non-directory compiler input path component.");
            parts.Add(part);
        }
        string full = parts.Count == 0 ? root : Path.Combine(root, Path.Combine(parts.ToArray()));
        ValidatePath(full, isDirectory);
        return full;
    }
    private static string ValidatePath(string value, bool isDirectory)
    {
        if (!Path.IsPathFullyQualified(value) || value.Contains('\0') || !Path.GetFullPath(value).Equals(value, PathComparison))
            throw new InvalidDataException("Absolute canonical path required: " + value);
        if (OperatingSystem.IsWindows() && (value.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            value.StartsWith("\\\\.\\", StringComparison.Ordinal) || value.Split('\\').Any(x => x.EndsWith(' ') || x.EndsWith('.'))))
            throw new InvalidDataException("Ambiguous/device path refused: " + value);
        string? current = Path.GetPathRoot(value);
        if (current is null) throw new InvalidDataException("Path has no root.");
        string[] parts = value[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string assembled = current;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] is "." or "..") throw new InvalidDataException("Traversing path refused.");
            assembled = Path.Combine(assembled, parts[i]);
            FileAttributes attrs = File.GetAttributes(assembled);
            if ((attrs & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse path refused: " + assembled);
            bool last = i == parts.Length - 1;
            if (!last && (attrs & FileAttributes.Directory) == 0) throw new InvalidDataException("Non-directory path component.");
        }
        FileAttributes final = File.GetAttributes(value);
        if (isDirectory != ((final & FileAttributes.Directory) != 0)) throw new InvalidDataException("Unexpected input path type.");
        if (!isDirectory && new FileInfo(value).Length > MaxFile) throw new InvalidDataException("Oversized compiler input.");
        return value;
    }

    private static bool AreSameFile(string a, string b)
    {
        if (PathEquals(a, b)) return true;
        if (!OperatingSystem.IsWindows()) return false;
        using SafeFileHandle first = File.OpenHandle(a, FileMode.Open, FileAccess.Read, FileShare.Read);
        using SafeFileHandle second = File.OpenHandle(b, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!GetFileInformationByHandle(first, out ByHandleFileInformation x) ||
            !GetFileInformationByHandle(second, out ByHandleFileInformation y))
            throw new IOException("Could not establish file identity for a Csc source.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        return x.VolumeSerialNumber == y.VolumeSerialNumber && x.FileIndexHigh == y.FileIndexHigh && x.FileIndexLow == y.FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes, CreationTimeLow, CreationTimeHigh, LastAccessTimeLow, LastAccessTimeHigh,
            LastWriteTimeLow, LastWriteTimeHigh, VolumeSerialNumber, FileSizeHigh, FileSizeLow,
            NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDirectoryW")]
    private static extern bool CreateDirectoryWindows(string path, IntPtr securityAttributes);

    private static void CreateNewDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateDirectoryWindows(path, IntPtr.Zero))
                throw new IOException("Could not exclusively create output directory.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
        else
        {
            if (Directory.Exists(path) || File.Exists(path))
                throw new IOException("Output directory already exists.");
            Directory.CreateDirectory(path);
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Output directory is a reparse point.");
    }

    private static byte[] ReadBounded(string path, int max)
    {
        using FileStream stream = OpenStable(path, out FileState before);
        if (stream.Length > max) throw new InvalidDataException("Oversized input during bounded read.");
        using MemoryStream memory = new();
        stream.CopyTo(memory);
        if (memory.Length > max) throw new InvalidDataException("Oversized input during bounded read.");
        EnsureStable(stream, path, before);
        return memory.ToArray();
    }
    private static JsonDocument ParseStrict(byte[] bytes)
    {
        using JsonDocument doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        CheckDuplicates(doc.RootElement);
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
    }
    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property: " + property.Name);
                CheckDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) CheckDuplicates(item);
    }

    private static byte[] Canonical(JsonElement element)
    {
        using MemoryStream stream = new();
        WriteCanonical(stream, element);
        return stream.ToArray();
    }
    private static byte[] Canonical(JsonNode node)
    {
        using JsonDocument doc = JsonDocument.Parse(node.ToJsonString());
        return Canonical(doc.RootElement);
    }
    private static void WriteCanonical(Stream output, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                output.WriteByte((byte)'{');
                bool first = true;
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (!first) output.WriteByte((byte)','); first = false;
                    WriteString(output, property.Name); output.WriteByte((byte)':'); WriteCanonical(output, property.Value);
                }
                output.WriteByte((byte)'}'); break;
            case JsonValueKind.Array:
                output.WriteByte((byte)'[');
                for (int i = 0; i < element.GetArrayLength(); i++)
                { if (i > 0) output.WriteByte((byte)','); WriteCanonical(output, element[i]); }
                output.WriteByte((byte)']'); break;
            case JsonValueKind.String: WriteString(output, element.GetString()!); break;
            case JsonValueKind.Number:
                byte[] number = Encoding.ASCII.GetBytes(element.GetRawText()); output.Write(number); break;
            case JsonValueKind.True: output.Write("true"u8); break;
            case JsonValueKind.False: output.Write("false"u8); break;
            case JsonValueKind.Null: output.Write("null"u8); break;
            default: throw new InvalidDataException("Unsupported JSON token in canonical serializer.");
        }
    }
    private static void WriteString(Stream output, string value)
    {
        output.WriteByte((byte)'"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': output.Write("\\\""u8); break;
                case '\\': output.Write("\\\\"u8); break;
                case '\b': output.Write("\\b"u8); break;
                case '\f': output.Write("\\f"u8); break;
                case '\n': output.Write("\\n"u8); break;
                case '\r': output.Write("\\r"u8); break;
                case '\t': output.Write("\\t"u8); break;
                default:
                    if (c < 0x20 || c > 0x7e)
                    {
                        byte[] escaped = Encoding.ASCII.GetBytes("\\u" + ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                        output.Write(escaped);
                    }
                    else output.WriteByte((byte)c);
                    break;
            }
        }
        output.WriteByte((byte)'"');
    }

    private static void VerifyOutput(string output, string graphSha, string[] commandIds)
    {
        string indexPath = Path.Combine(output, "index.json");
        JsonDocument indexDoc = ParseStrict(ReadBounded(indexPath, MaxIndex));
        using (indexDoc)
        {
            JsonElement index = indexDoc.RootElement;
            if (Text(index, "schema") != Schema || Text(index, "disposition") != "unqualified-software-observation" ||
                Text(index, "graphSha256") != graphSha || index.GetProperty("chunkSizeLimit").GetInt32() != ChunkSize)
                throw new InvalidDataException("C# V2 output self-check failed schema/pins.");
            JsonObject node = JsonNode.Parse(index.GetRawText())!.AsObject();
            string root = Text(index, "closureRootSha256"); node.Remove("closureRootSha256");
            if (Sha(Canonical(node)) != root) throw new InvalidDataException("C# V2 output closure self-check failed.");
            string[] actualIds = index.GetProperty("commands").EnumerateArray().Select(x => Text(x, "nodeId")).ToArray();
            if (!actualIds.SequenceEqual(commandIds, StringComparer.Ordinal)) throw new InvalidDataException("C# V2 output command order mismatch.");
            int chunkNo = 0;
            foreach (JsonElement entry in index.GetProperty("entries").EnumerateArray())
            {
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long size = 0;
                foreach (JsonElement chunk in entry.GetProperty("chunks").EnumerateArray())
                {
                    string expectedName = $"chunk-{chunkNo:D8}.bin";
                    if (Text(chunk, "name") != expectedName) throw new InvalidDataException("C# V2 output chunk order mismatch.");
                    byte[] bytes = ReadBounded(Path.Combine(output, expectedName), ChunkSize);
                    if (bytes.Length != chunk.GetProperty("bytes").GetInt32() || Sha(bytes) != Text(chunk, "sha256"))
                        throw new InvalidDataException("C# V2 output chunk changed.");
                    hash.AppendData(bytes); size += bytes.Length; chunkNo++;
                }
                if (size != entry.GetProperty("bytes").GetInt64() ||
                    Convert.ToHexStringLower(hash.GetHashAndReset()) != Text(entry, "sha256"))
                    throw new InvalidDataException("C# V2 output entry changed.");
            }
            int actualChunkCount = Directory.EnumerateFiles(output, "chunk-*.bin", SearchOption.TopDirectoryOnly).Count();
            if (actualChunkCount != chunkNo) throw new InvalidDataException("C# V2 output has extra/missing chunks.");
        }
    }

    private static string Logical(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Contains('\\') || value.Contains(':') ||
            value.Any(c => c < 0x20) || value.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.Trim() != p))
            throw new InvalidDataException("Invalid/noncanonical logical path.");
        return value;
    }
    private static string ShaPin(string value)
    {
        if (value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("Invalid lowercase SHA-256 pin.");
        return value;
    }
    private static string RequireAbsolute(string value, string label) =>
        Path.IsPathFullyQualified(value) ? value : throw new ArgumentException(label + " path must be absolute.");
    private static bool PathEquals(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), PathComparison);
    private static StringComparer PathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static string PathKey(string path) => OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path);
    private static bool IsUnder(string path, string root)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return PathEquals(fullPath, fullRoot) || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison);
    }
    private static int ComparePythonStrings(string a, string b)
    {
        int[] x = a.EnumerateRunes().Select(r => r.Value).ToArray();
        int[] y = b.EnumerateRunes().Select(r => r.Value).ToArray();
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return x.Length.CompareTo(y.Length);
    }
    private static int ComparePythonStrings((string Node, string Kind, string Logical) a,
        (string Node, string Kind, string Logical) b)
    {
        int order = ComparePythonStrings(a.Node, b.Node);
        if (order != 0) return order;
        order = ComparePythonStrings(a.Kind, b.Kind);
        return order != 0 ? order : ComparePythonStrings(a.Logical, b.Logical);
    }
    private static void CheckTime(Stopwatch clock, double seconds)
    { if (clock.Elapsed.TotalSeconds >= seconds) throw new TimeoutException("Bounded capture timeout."); }
    private static string Text(JsonElement element, string property) =>
        element.GetProperty(property).ValueKind == JsonValueKind.String ? element.GetProperty(property).GetString()! :
        throw new InvalidDataException("Missing/non-string JSON field: " + property);
    private static string? OptionalText(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new InvalidDataException("Malformed optional string: " + property);
    }
    private static void RequireKind(JsonElement element, JsonValueKind kind, string label)
    { if (element.ValueKind != kind) throw new InvalidDataException(label + " has wrong JSON type."); }
    private static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void WriteNew(string path, byte[] bytes)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(flushToDisk: true);
    }
}
