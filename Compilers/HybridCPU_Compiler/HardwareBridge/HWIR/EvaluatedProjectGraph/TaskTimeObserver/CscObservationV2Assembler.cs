using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Reconstructs an unqualified Csc observation from independently pinned task,
/// process-probe, response-file, evaluated-graph and source-byte evidence.
/// It does not read the expected observation or make a trust/admission claim.
/// </summary>
public static class CscObservationV2Assembler
{
    public const string OutputSchema = "hybridcpu.hardwarebridge.csc-observation/v2";
    private const int MaxJsonBytes = 32 * 1024 * 1024;
    private const int MaxRspBytes = 2 * 1024 * 1024;
    private const string TaskSchema = "hybridcpu.hardwarebridge.msbuild-csc-task-events/v1";
    private const string GraphSchema = "hardwarebridge.evaluated-project-graph/v2";
    private const string CrosswalkSchema = "circuitweave.golden-windows-csc-execution-crosswalk/v1";
    private const string ProbeSchema = "hybridcpu.csc-launch-probe/v1";

    private sealed record JsonInput(byte[] Bytes, JsonDocument Doc, string Sha) : IDisposable { public void Dispose() => Doc.Dispose(); }
    private sealed record ProjectOut(string nodeId, string role, string workingDirectory, CompilerOut compiler,
        string projectSha256, PinOut[] filePins, string[] argv, string argvSha256, SourceOut[] sources,
        GeneratorOut[] generatorSources);
    private sealed record CompilerOut(string path, string sha256);
    private sealed record PinOut(string kind, string path, string sha256);
    private sealed record SourceOut(string path, string logicalPath, string sha256, string origin, string? originalPath);
    private sealed record GeneratorOut(string path, string logicalPath, string sha256);
    private sealed record Output(string schema, string graphSha256, ProjectOut[] projects, string disposition, string scope);
    private const string OutputScope = "Task-time response/process observation with exact compiler input paths and hashes. The pinned source graph lists only Golden-origin Compile members; 15 target-generated obj Compile inputs are separately marked msbuild-generated in the observation and crosswalk.";

    /// <summary>CLI requires all raw input SHA pins and an externally pinned manifest.</summary>
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "prepare-manifest")
                return PrepareManifest(args.Skip(1).ToArray());
            if (args.Length != 15 || args[0] != "assemble")
                throw new InvalidDataException("Usage: assemble <graph> <graph-sha256> <task-events> <task-sha256> <crosswalk> <crosswalk-sha256> <metadata> <metadata-sha256> <manifest> <manifest-sha256> <response-dir> <probe-dir> <output-temp-file> <expected-output-sha256-or-dash>");
            byte[] bytes = Assemble(args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8],
                args[9], args[10], args[11], args[12]);
            string outputHash = Hash(bytes);
            if (args[14] != "-" && !Eq(outputHash, args[14]))
                throw new InvalidDataException("Assembled output SHA differs from expected-output pin.");
            string dest = Path.GetFullPath(args[13]);
            string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!Same(Path.GetDirectoryName(dest)!, temp) || File.Exists(dest) || Directory.Exists(dest))
                throw new InvalidDataException("Output must be a new direct TEMP file.");
            using (var fs = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { fs.Write(bytes); fs.Flush(true); }
            Console.WriteLine(JsonSerializer.Serialize(new { status = "ObservedUnqualified", outputSha256 = outputHash,
                trust = "NotAdmitted", independentAdmission = "NotAdmitted" }));
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
            ArgumentException or FormatException or OverflowException or DllNotFoundException or EntryPointNotFoundException)
        { Console.Error.WriteLine("REFUSED: " + e); return 2; }
    }

    private static int PrepareManifest(string[] args)
    {
        if (args.Length != 13)
            throw new InvalidDataException("Usage: prepare-manifest <graph> <graph-sha> <crosswalk> <crosswalk-sha> <metadata> <metadata-sha> <probe-dir> <golden-root> <isolated-root> <generated-root> <compiler-path> <primary-node-id> <new-temp-output>");
        using JsonInput graphIn = PinnedJson(args[0], args[1]);
        using JsonInput crossIn = PinnedJson(args[2], args[3]);
        using JsonInput metadataIn = PinnedJson(args[4], args[5]);
        JsonElement graph = graphIn.Doc.RootElement, cross = crossIn.Doc.RootElement, metadata = metadataIn.Doc.RootElement;
        Exact(graph, "schema", "disposition", "root", "rootProjectSha256", "globalProperties", "nodes", "scope");
        Exact(cross, "schema", "disposition", "goldenHead", "goldenTree", "isolatedBuildHead", "sdk", "cscSha256", "projectCount", "compileItemTotal", "cscSourceTotal", "generatorOutputTotal", "taskEvents", "projects", "claims");
        bool metadataHasGitPin = Has(metadata, "gitExecutablePath") || Has(metadata, "gitExecutableSha256");
        if (metadataHasGitPin) Exact(metadata, "arguments", "exitCode", "started", "goldenHead", "goldenTree", "sdk", "cscSha256", "probeSha256", "generatedRoot", "gitExecutablePath", "gitExecutableSha256");
        else Exact(metadata, "arguments", "exitCode", "started", "goldenHead", "goldenTree", "sdk", "cscSha256", "probeSha256", "generatedRoot");
        if (S(graph, "schema") != GraphSchema || S(cross, "schema") != CrosswalkSchema ||
            S(metadata, "goldenHead") != S(cross, "goldenHead") || S(metadata, "goldenTree") != S(cross, "goldenTree") ||
            S(metadata, "sdk") != S(cross, "sdk") || S(metadata, "cscSha256") != S(cross, "cscSha256") || I(metadata, "exitCode") != 0)
            throw new InvalidDataException("Pinned graph/crosswalk/metadata contract mismatch.");
        string probeDir = SafeDirectory(args[6]), goldenRoot = Root(args[7]), isolatedRoot = Root(args[8]), generatedRoot = Root(args[9]);
        if (!Same(generatedRoot, Root(S(metadata, "generatedRoot"))))
            throw new InvalidDataException("Generated-source root differs from pinned build metadata.");
        if (Same(goldenRoot, isolatedRoot) || Under(goldenRoot, isolatedRoot) || Under(isolatedRoot, goldenRoot) ||
            Same(generatedRoot, isolatedRoot) || Under(generatedRoot, isolatedRoot) || Under(isolatedRoot, generatedRoot) ||
            Same(generatedRoot, goldenRoot) || Under(generatedRoot, goldenRoot) || Under(goldenRoot, generatedRoot))
            throw new InvalidDataException("Golden, isolated and generated roots must be disjoint.");
        if (metadataHasGitPin)
        {
            string gitExe = SafeFile(S(metadata, "gitExecutablePath"));
            if (!Eq(HashFile(gitExe), LowerSha(S(metadata, "gitExecutableSha256")))) throw new InvalidDataException("Pinned Git executable bytes changed.");
        }
        string compilerPath = SafeFile(args[10]), compilerSha = HashFile(compilerPath);
        if (!Eq(compilerSha, S(cross, "cscSha256")) || !Eq(compilerSha, S(metadata, "cscSha256")))
            throw new InvalidDataException("Compiler bytes disagree with pinned capture metadata.");
        string primaryNodeId = args[11];
        JsonElement[] nodes = A(graph, "nodes").EnumerateArray().ToArray();
        if (nodes.Count(n => S(n, "id") == primaryNodeId && Same(S(n, "project"), S(graph, "root"))) != 1)
            throw new InvalidDataException("Primary role selector must exactly identify graph.root node.");
        JsonElement[] rows = A(cross, "projects").EnumerateArray().ToArray();
        if (rows.Length == 0 || rows.Length != I(cross, "projectCount"))
            throw new InvalidDataException("Crosswalk project inventory is empty or inconsistent.");
        var probes = new List<object>(); var seen = new HashSet<int>();
        foreach (JsonElement row in rows)
        {
            int pid = I(row, "probePid");
            if (!seen.Add(pid)) throw new InvalidDataException("Duplicate process probe PID.");
            string path = SafeFile(Path.Combine(probeDir, $"csc-launch-{pid}.jsonl"));
            _ = ReadProbeLines(path);
            probes.Add(new { pid, path, sha256 = HashFile(path) });
        }
        string output = Path.GetFullPath(args[12]);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!Same(Path.GetDirectoryName(output)!, temp) || File.Exists(output) || Directory.Exists(output))
            throw new InvalidDataException("Manifest output must be a new direct TEMP file.");
        var manifest = new
        {
            schema = "hardwarebridge.csc-observation-v2-assembly-inputs/v1",
            roleSelector = new { schema = "hardwarebridge.graph-role-selector/v1", graphSha256 = graphIn.Sha, primaryNodeId },
            goldenRoot, isolatedRoot, generatedRoot, compilerPath, compilerSha256 = compilerSha, probes
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        using (var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { fs.Write(bytes); fs.Flush(true); }
        Console.WriteLine(JsonSerializer.Serialize(new { status = "PreparedUnqualifiedAssemblyManifest", manifestSha256 = Hash(bytes), trust = "NotAdmitted" }));
        return 0;
    }

    public static byte[] Assemble(string graphPath, string graphSha, string taskPath, string taskSha,
        string crossPath, string crossSha, string metadataPath, string metadataSha,
        string manifestPath, string manifestSha, string responseDir, string probeDir)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidDataException("Csc observation V2 requires Windows argv semantics.");
        using JsonInput graphIn = PinnedJson(graphPath, graphSha);
        using JsonInput taskIn = PinnedJson(taskPath, taskSha);
        using JsonInput crossIn = PinnedJson(crossPath, crossSha);
        using JsonInput metaIn = PinnedJson(metadataPath, metadataSha);
        using JsonInput manifestIn = PinnedJson(manifestPath, manifestSha);
        JsonElement graph = graphIn.Doc.RootElement, tasks = taskIn.Doc.RootElement,
            cross = crossIn.Doc.RootElement, meta = metaIn.Doc.RootElement, manifest = manifestIn.Doc.RootElement;
        Exact(graph, "schema", "disposition", "root", "rootProjectSha256", "globalProperties", "nodes", "scope");
        if (S(graph, "schema") != GraphSchema || S(graph, "disposition") is "refused" or "failed") throw new InvalidDataException("Unsupported graph envelope.");
        Exact(tasks, "schema", "disposition", "error", "events");
        if (S(tasks, "schema") != TaskSchema || S(tasks, "disposition") != "unqualified-software-observation" || tasks.GetProperty("error").ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("Failed or unsupported task event input.");
        Exact(cross, "schema", "disposition", "goldenHead", "goldenTree", "isolatedBuildHead", "sdk", "cscSha256", "projectCount", "compileItemTotal", "cscSourceTotal", "generatorOutputTotal", "taskEvents", "projects", "claims");
        if (S(cross, "schema") != CrosswalkSchema || S(cross, "disposition") != "unqualified-software-observation") throw new InvalidDataException("Unsupported crosswalk.");
        JsonElement claims = O(cross, "claims");
        bool claimsHaveGitPin = Has(claims, "gitExecutablePath") || Has(claims, "gitExecutableSha256");
        if (claimsHaveGitPin) Exact(claims, "actualCscLaunchObserved", "responseBytesStableAtPathWhileChildRan", "gitExecutablePath", "gitExecutableSha256", "cscConsumedExactResponseBytes", "sourceAndAnalyzerReadBuffersProven", "generatedSourceSnapshotOccursAfterBuild", "independentCoreAdmission", "trustedHandoff");
        else Exact(claims, "actualCscLaunchObserved", "responseBytesStableAtPathWhileChildRan", "cscConsumedExactResponseBytes", "sourceAndAnalyzerReadBuffersProven", "generatedSourceSnapshotOccursAfterBuild", "independentCoreAdmission", "trustedHandoff");
        if (!B(claims, "actualCscLaunchObserved") || !B(claims, "responseBytesStableAtPathWhileChildRan") || B(claims, "cscConsumedExactResponseBytes") || B(claims, "sourceAndAnalyzerReadBuffersProven") || !B(claims, "generatedSourceSnapshotOccursAfterBuild") || S(claims, "independentCoreAdmission") != "NotEstablished" || S(claims, "trustedHandoff") != "NotEstablished") throw new InvalidDataException("Crosswalk claims exceed observed scope.");
        bool metaHasGitPin = Has(meta, "gitExecutablePath") || Has(meta, "gitExecutableSha256");
        if (metaHasGitPin) Exact(meta, "arguments", "exitCode", "started", "goldenHead", "goldenTree", "sdk", "cscSha256", "probeSha256", "generatedRoot", "gitExecutablePath", "gitExecutableSha256");
        else Exact(meta, "arguments", "exitCode", "started", "goldenHead", "goldenTree", "sdk", "cscSha256", "probeSha256", "generatedRoot");
        if (claimsHaveGitPin != metaHasGitPin) throw new InvalidDataException("Crosswalk and build metadata Git pin presence differs.");
        if (metaHasGitPin)
        {
            if (!Eq(S(claims, "gitExecutablePath"), S(meta, "gitExecutablePath")) || !Eq(S(claims, "gitExecutableSha256"), S(meta, "gitExecutableSha256"))) throw new InvalidDataException("Crosswalk and build metadata Git pins differ.");
            string gitExe = SafeFile(S(meta, "gitExecutablePath"));
            if (!Eq(HashFile(gitExe), LowerSha(S(meta, "gitExecutableSha256")))) throw new InvalidDataException("Pinned Git executable bytes changed.");
        }
        if (I(meta, "exitCode") != 0 || S(meta, "goldenHead") != S(cross, "goldenHead") || S(meta, "goldenTree") != S(cross, "goldenTree") || S(meta, "sdk") != S(cross, "sdk") || S(meta, "cscSha256") != S(cross, "cscSha256"))
            throw new InvalidDataException("Build metadata and crosswalk pins disagree or build failed.");
        Exact(manifest, "schema", "roleSelector", "goldenRoot", "isolatedRoot", "generatedRoot", "compilerPath", "compilerSha256", "probes");
        if (S(manifest, "schema") != "hardwarebridge.csc-observation-v2-assembly-inputs/v1") throw new InvalidDataException("Unsupported assembly manifest.");
        JsonElement selector = O(manifest, "roleSelector"); Exact(selector, "schema", "graphSha256", "primaryNodeId");
        if (S(selector, "schema") != "hardwarebridge.graph-role-selector/v1" || !Eq(S(selector, "graphSha256"), graphIn.Sha)) throw new InvalidDataException("Role selector is stale or unsupported.");

        string goldenRoot = Root(S(manifest, "goldenRoot")), isolatedRoot = Root(S(manifest, "isolatedRoot")), generatedRoot = Root(S(manifest, "generatedRoot"));
        if (!Same(generatedRoot, Root(S(meta, "generatedRoot")))) throw new InvalidDataException("Manifest generated-source root differs from pinned build metadata.");
        if (Same(goldenRoot, isolatedRoot) || Under(goldenRoot, isolatedRoot) || Under(isolatedRoot, goldenRoot) ||
            Same(generatedRoot, isolatedRoot) || Under(generatedRoot, isolatedRoot) || Under(isolatedRoot, generatedRoot) ||
            Same(generatedRoot, goldenRoot) || Under(generatedRoot, goldenRoot) || Under(goldenRoot, generatedRoot))
            throw new InvalidDataException("Golden, isolated and generated roots must be disjoint.");
        string compilerPath = SafeFile(S(manifest, "compilerPath")); string compilerSha = LowerSha(S(manifest, "compilerSha256"));
        if (!Eq(HashFile(compilerPath), compilerSha) || !Eq(compilerSha, S(cross, "cscSha256")) || !Eq(compilerSha, S(meta, "cscSha256"))) throw new InvalidDataException("Compiler bytes differ from crosswalk/metadata pins.");
        string toolPathArg = UniqueArgument(A(meta, "arguments"), "-p:CscToolPath=");
        string toolExeArg = UniqueArgument(A(meta, "arguments"), "-p:CscToolExe=");
        string probePath = SafeFile(Path.Combine(toolPathArg, toolExeArg));
        if (!Eq(HashFile(probePath), S(meta, "probeSha256"))) throw new InvalidDataException("Csc launch-probe executable differs from pinned build metadata.");

        var nodes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonElement n in A(graph, "nodes").EnumerateArray())
        {
            string id = S(n, "id"); if (!nodes.TryAdd(id, n)) throw new InvalidDataException("Duplicate graph node id.");
        }
        string rootProject = S(graph, "root");
        string primaryId = S(selector, "primaryNodeId");
        if (!nodes.TryGetValue(primaryId, out JsonElement primary) || !Same(S(primary, "project"), rootProject)) throw new InvalidDataException("Role selector primary node does not represent graph.root.");
        var reachable = new HashSet<string>(StringComparer.Ordinal) { primaryId };
        var queue = new Queue<string>(); queue.Enqueue(primaryId);
        while (queue.Count != 0)
        {
            JsonElement n = nodes[queue.Dequeue()];
            foreach (JsonElement r in A(n, "references").EnumerateArray())
            {
                string target = r.ValueKind == JsonValueKind.String ? r.GetString()! : throw new InvalidDataException("Graph reference edge must be a node id string.");
                if (!nodes.ContainsKey(target)) throw new InvalidDataException("Reference edge targets absent graph node.");
                if (reachable.Add(target)) queue.Enqueue(target);
            }
        }
        if (reachable.Count != nodes.Count) throw new InvalidDataException("Unreachable graph nodes lack explicit executable/metadata role semantics.");

        JsonElement events = A(tasks, "events"), crossRows = A(cross, "projects"), probeRows = A(manifest, "probes");
        if (events.GetArrayLength() != nodes.Count || crossRows.GetArrayLength() != nodes.Count || I(cross, "projectCount") != nodes.Count || probeRows.GetArrayLength() != nodes.Count)
            throw new InvalidDataException("Task, crosswalk, probe and graph row counts must match exactly.");
        var eventByProject = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement e in events.EnumerateArray())
        {
            Exact(e, "Id", "ProjectFile", "StartUtc", "FinishUtc", "CommandLine", "Succeeded", "CommandEvents");
            if (!B(e, "Succeeded") || I(e, "CommandEvents") != 1 || !eventByProject.TryAdd(Path.GetFullPath(S(e, "ProjectFile")), e)) throw new InvalidDataException("Duplicate/failed task event.");
        }
        var crossByProject = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement c in crossRows.EnumerateArray())
        {
            Exact(c, "project", "taskId", "taskStartUtc", "taskFinishUtc", "taskSucceeded", "probePid", "childPid", "childExitCode", "probeUtc", "workingDirectory", "taskArgvExpandedMatchesResponse", "responsePath", "responseCopyPath", "responseBytes", "responseSha256", "responseSourceHandleLockedThroughChild", "cscArgvCount", "compileItemCount", "cscSourceCount", "compileEqualsCscSources", "sourcesMatchingPinnedGolden", "msbuildGeneratedCompileSources", "fileInputs", "generatedSourceOutputDirectory", "generatorSourceOutputs");
            string p = Path.GetFullPath(S(c, "project"));
            if (!B(c, "taskSucceeded") || !B(c, "taskArgvExpandedMatchesResponse") || !B(c, "responseSourceHandleLockedThroughChild") || I(c, "childExitCode") != 0 || !crossByProject.TryAdd(p, c)) throw new InvalidDataException("Crosswalk row incomplete/duplicate.");
        }
        if (!eventByProject.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(crossByProject.Keys)) throw new InvalidDataException("Task and crosswalk project identities differ.");
        var probes = ReadProbeManifest(probeRows, probeDir);

        var outputs = new List<ProjectOut>();
        foreach (JsonElement crossRow in crossRows.EnumerateArray())
        {
            string projectBuild = Path.GetFullPath(S(crossRow, "project")); JsonElement c = crossRow, ev = eventByProject[projectBuild];
            string id = NodeIdForProject(nodes, projectBuild, isolatedRoot, goldenRoot); JsonElement node = nodes[id]; string role = reachable.Contains(id) ? (id == primaryId ? "primary" : "owned-body") : "metadata-only";
            if (S(ev, "Id") != S(c, "taskId") || S(ev, "StartUtc") != S(c, "taskStartUtc") || S(ev, "FinishUtc") != S(c, "taskFinishUtc") || !Same(S(ev, "ProjectFile"), projectBuild)) throw new InvalidDataException("Task/crosswalk project, identity or interval mismatch.");
            string projectOriginal = Map(projectBuild, isolatedRoot, goldenRoot);
            if (!Same(projectOriginal, S(node, "project")) || !Eq(HashFile(projectOriginal), HashFile(projectBuild))) throw new InvalidDataException("Project clone differs from graph project/source tree.");
            string cwd = SafeDirectory(S(c, "workingDirectory"));
            if (!Under(cwd, isolatedRoot)) throw new InvalidDataException("Csc cwd outside isolated root.");
            JsonElement probe = probes[I(c, "probePid")];
            JsonElement launch = probe.GetProperty("launch"), snap = probe.GetProperty("snapshot"), result = probe.GetProperty("result");
            string[] probeArgs = A(launch, "arguments").EnumerateArray().Select(x => x.GetString() ?? throw new InvalidDataException("Null probe arg.")).ToArray();
            if (S(launch, "record") != "launch" || S(snap, "record") != "response-snapshot" || S(result, "record") != "result" ||
                !Same(CommandLineToArgvW(S(ev, "CommandLine"))[0], probePath) || !Same(S(launch, "targetExecutablePath"), compilerPath) || !Eq(S(launch, "targetExecutableSha256"), compilerSha) || !B(launch, "targetHashPinMatched") ||
                S(launch, "workingDirectory") != cwd || I(launch, "probePid") != I(c, "probePid") || I(result, "childPid") != I(c, "childPid") ||
                I(result, "childExitCode") != I(c, "childExitCode") || S(result, "status") != "Exited" || !B(result, "childStarted") ||
                S(launch, "responseFilePath") != S(c, "responsePath") || probeArgs.Length != 2 || !probeArgs[0].Equals("/noconfig", StringComparison.OrdinalIgnoreCase) ||
                !probeArgs[1].StartsWith('@') || !Same(probeArgs[1][1..], S(c, "responsePath")) ||
                !B(snap, "sourceHeldReadOnlyUntilChildExit") || !B(c, "responseSourceHandleLockedThroughChild") ||
                S(launch, "timestampUtc") != S(c, "probeUtc")) throw new InvalidDataException("Probe launch/result does not bind crosswalk Csc process.");
            string response = SafeFile(S(c, "responseCopyPath"));
            if (!Under(response, responseDir) || !Same(response, Path.Combine(responseDir, $"csc-launch-{I(c, "probePid")}.rsp"))) throw new InvalidDataException("Response copy has ambiguous identity or escapes response directory.");
            byte[] rsp = ReadOrdinary(response, MaxRspBytes); string rspSha = Hash(rsp);
            if (rsp.Length != I(c, "responseBytes") || !Eq(rspSha, S(c, "responseSha256")) || !Eq(rspSha, S(snap, "responseSha256")) || !Eq(rspSha, S(result, "responseSha256")) || !Eq(response, S(launch, "responseCopyPath"))) throw new InvalidDataException("Response bytes differ from probe/crosswalk pins.");
            string rspText = new UTF8Encoding(false, true).GetString(rsp);
            if (rspText.Length > 0 && rspText[0] == '\uFEFF') rspText = rspText[1..];
            string[] argv = ParseWindows(rspText);
            if (S(ev, "Id") != S(c, "taskId")) throw new InvalidDataException("Task and crosswalk execution IDs differ.");
            ValidateTaskRsp(ev, S(c, "responsePath"), argv);
            if (argv.Length != I(c, "cscArgvCount")) throw new InvalidDataException("Csc argv count differs from crosswalk.");

            var sourceTokens = new List<string>(); var pins = new List<PinOut>(); string? generatorDir = null;
            var inputMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement f in A(c, "fileInputs").EnumerateArray())
            {
                string path = Resolve(S(f, "path"), cwd); string kind = S(f, "kind");
                if (!inputMap.TryAdd(path, kind)) throw new InvalidDataException("Duplicate Csc file input.");
                if (!Eq(HashFile(path), S(f, "sha256")) || new FileInfo(path).Length != L(f, "bytes")) throw new InvalidDataException("Csc file input bytes differ from crosswalk pin.");
                pins.Add(new(kind, path, LowerSha(S(f, "sha256"))));
            }
            var seenInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string token in argv)
            {
                string low = token.ToLowerInvariant();
                if (low.EndsWith(".cs", StringComparison.Ordinal) && !low.StartsWith('/') && !low.StartsWith('-')) sourceTokens.Add(Resolve(token, cwd));
                else if (low.StartsWith("/generatedfilesout:", StringComparison.Ordinal)) { if (generatorDir is not null) throw new InvalidDataException("Duplicate generatedfilesout switch."); generatorDir = ResolveDirectory(token[(token.IndexOf(':') + 1)..], cwd); }
                else if (IsFileSwitch(low))
                {
                    int colon = token.IndexOf(':'); if (colon < 0) throw new InvalidDataException("Malformed file-valued Csc switch.");
                    string kind = FileKind(low[..(colon + 1)]); string p = Resolve(token[(colon + 1)..], cwd);
                    if (kind == "reference" && token[(colon + 1)..].Contains('=')) throw new InvalidDataException("Reference aliases require a separate contract.");
                    if (!inputMap.TryGetValue(p, out string? declaredKind) || declaredKind != kind || !seenInputs.Add(p)) throw new InvalidDataException("Unbound or duplicate Csc file-valued argv input: " + kind);
                }
                else if (IsOutputOrScalar(low)) { }
                else throw new InvalidDataException("Unclassified Csc argv token: " + token);
            }
            if (sourceTokens.Count != I(c, "cscSourceCount") || sourceTokens.Count != I(c, "compileItemCount") || !B(c, "compileEqualsCscSources")) throw new InvalidDataException("Csc source count/inventory mismatch.");
            if (inputMap.Count != pins.Count || !seenInputs.SetEquals(inputMap.Keys)) throw new InvalidDataException("File pin inventory differs from exact Csc argv inputs.");
            var sourceRows = new List<SourceOut>(); var originalsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var expectedCompile = A(node, "compileFiles").EnumerateArray().Select(x => Path.GetFullPath(x.GetString()!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string src in sourceTokens)
            {
                string sha = HashFile(src); string original = Map(src, isolatedRoot, goldenRoot);
                if (expectedCompile.Contains(original))
                {
                    if (!originalsSeen.Add(original) || !Eq(sha, HashFile(original))) throw new InvalidDataException("Evaluated source is missing/duplicate or differs from pinned source clone.");
                    sourceRows.Add(new(src, "project/" + Rel(original, goldenRoot).Replace('\\', '/'), sha, "evaluated", original));
                }
                else
                {
                    JsonElement? generatedRow = A(c, "msbuildGeneratedCompileSources").EnumerateArray().Cast<JsonElement?>().FirstOrDefault(z => z.HasValue && Same(S(z.Value, "buildPath"), src));
                    if (generatedRow is null || !Eq(HashFile(src), S(generatedRow.Value, "sha256")) || new FileInfo(src).Length != L(generatedRow.Value, "bytes")) throw new InvalidDataException("Csc source is neither graph Compile nor crosswalk-pinned generated compile source.");
                    string projectRel = Rel(Path.GetDirectoryName(projectBuild)!, isolatedRoot); string srcRel = Rel(src, Path.GetDirectoryName(projectBuild)!);
                    sourceRows.Add(new(src, "msbuild-generated/" + (projectRel.Length == 0 ? "" : projectRel.Replace('\\','/') + "/") + srcRel.Replace('\\','/'), sha, "msbuild-generated", null));
                }
            }
            if (!originalsSeen.SetEquals(expectedCompile)) throw new InvalidDataException("Csc omitted evaluated graph Compile items.");
            if (generatorDir is null && A(c, "generatorSourceOutputs").GetArrayLength() != 0) throw new InvalidDataException("Generated source records lack generatedfilesout switch.");
            var generatorRows = new List<GeneratorOut>();
            if (generatorDir is not null)
            {
                if (!Under(generatorDir, generatedRoot)) throw new InvalidDataException("Generator output directory outside pinned generated root.");
                var declared = A(c, "generatorSourceOutputs").EnumerateArray().ToDictionary(x => Path.GetFullPath(S(x, "path")), StringComparer.OrdinalIgnoreCase);
                string[] actual = Directory.EnumerateFiles(generatorDir, "*.cs", SearchOption.AllDirectories).Select(SafeFile).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                if (!actual.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(declared.Keys)) throw new InvalidDataException("Missing or extra generator .cs output.");
                foreach (string f in actual)
                { JsonElement row = declared[f]; string h = HashFile(f); if (!Eq(h, S(row, "sha256")) || new FileInfo(f).Length != L(row, "bytes")) throw new InvalidDataException("Generated source bytes stale/altered."); generatorRows.Add(new(f, "generated/" + Rel(f, generatorDir).Replace('\\','/'), h)); }
            }
            string argvSha = Hash(CanonicalArray(argv));
            outputs.Add(new(id, role, cwd, new(compilerPath, compilerSha), HashFile(projectOriginal), pins.ToArray(), argv, argvSha, sourceRows.ToArray(), generatorRows.ToArray()));
        }
        if (outputs.Select(x => x.nodeId).Distinct(StringComparer.Ordinal).Count() != nodes.Count) throw new InvalidDataException("Not all graph nodes have one unique Csc task observation.");
        var output = new Output(OutputSchema, graphIn.Sha, outputs.ToArray(), "unqualified-captured-task-time-observation", OutputScope);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(output, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        // Python's frozen producer writes LF on Windows. Normalize serializer CRLF
        // without changing JSON semantics so byte parity remains an exact test.
        using var lf = new MemoryStream(json.Length);
        for (int i = 0; i < json.Length; i++) { if (json[i] == (byte)'\r' && i + 1 < json.Length && json[i + 1] == (byte)'\n') continue; lf.WriteByte(json[i]); }
        return lf.ToArray();
    }

    private static Dictionary<int, JsonElement> ReadProbeManifest(JsonElement rows, string probeDir)
    {
        var result = new Dictionary<int, JsonElement>();
        foreach (JsonElement row in rows.EnumerateArray())
        {
            Exact(row, "pid", "path", "sha256"); int pid = I(row, "pid"); string path = SafeFile(S(row, "path"));
            if (!Under(path, probeDir) || !Eq(HashFile(path), S(row, "sha256")) || !result.TryAdd(pid, ReadProbeLines(path))) throw new InvalidDataException("Probe file missing, stale or duplicate.");
        }
        return result;
    }
    private static JsonElement ReadProbeLines(string path)
    {
        using JsonDocument d = JsonDocument.Parse("[" + string.Join(',', File.ReadAllLines(path)) + "]");
        var rows = d.RootElement.EnumerateArray().ToArray();
        if (rows.Length != 3 || rows.Any(x => S(x, "schema") != ProbeSchema)) throw new InvalidDataException("Probe must contain exact launch/snapshot/result records.");
        using JsonDocument combined = JsonDocument.Parse(JsonSerializer.Serialize(new { launch = rows[0], snapshot = rows[1], result = rows[2] }));
        return combined.RootElement.Clone();
    }
    private static void ValidateTaskRsp(JsonElement ev, string responsePath, string[] responseArgs)
    {
        string[] task = CommandLineToArgvW(S(ev, "CommandLine"));
        if (task.Length != responseArgs.Length + 2 || !Path.GetFileName(task[0]).Equals("CscLaunchProbe.exe", StringComparison.OrdinalIgnoreCase) || !task[1].Equals("/noconfig", StringComparison.OrdinalIgnoreCase) || !task.Skip(2).SequenceEqual(responseArgs, StringComparer.Ordinal))
        {
            int i = Enumerable.Range(0, Math.Min(Math.Max(0,task.Length - 2), responseArgs.Length)).FirstOrDefault(k => task[k + 2] != responseArgs[k]);
            string got = i < task.Length - 2 ? task[i + 2] : "<missing>"; string want = i < responseArgs.Length ? responseArgs[i] : "<missing>";
            throw new InvalidDataException($"Task/RSP argv mismatch: task={task.Length - 1}, rsp={responseArgs.Length}, firstDiff={i}, taskToken={got}, rspToken={want}.");
        }
    }
    private static string[] ParseWindows(string text) => CommandLineToArgvW("csc.exe " + text).Skip(1).ToArray();
    private static string[] CommandLineToArgvW(string s)
    {
        nint p = NativeArgv(s, out int n); if (p == 0 || n < 1 || n > 8194) throw new InvalidDataException("CommandLineToArgvW failed or token bound exceeded.");
        try { string[] r = new string[n]; for (int i=0;i<n;i++) r[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i*IntPtr.Size)) ?? throw new InvalidDataException("Null argv token."); return r; }
        finally { _ = LocalFree(p); }
    }
    private static string UniqueArgument(JsonElement args, string prefix)
    {
        string[] matches = args.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].Length == prefix.Length) throw new InvalidDataException("Build metadata lacks unique " + prefix + " argument.");
        return matches[0][prefix.Length..];
    }
    private static bool IsFileSwitch(string x) => new[]{"/reference:","/r:","/analyzer:","/analyzerconfig:","/additionalfile:","/sourcelink:","/appconfig:","/embed:"}.Any(x.StartsWith);
    private static string FileKind(string x) => x switch { "/reference:" or "/r:" => "reference", "/analyzer:" => "analyzer", "/analyzerconfig:" => "analyzer-config", "/additionalfile:" => "additional-file", "/sourcelink:" => "source-link", "/appconfig:" => "app-config", "/embed:" => "embedded", _ => throw new InvalidDataException("Unknown Csc file switch.") };
    private static bool IsOutputOrScalar(string x) => x.StartsWith("/out:") || x.StartsWith("/pdb:") || x.StartsWith("/refout:") || x.StartsWith("/doc:") || x.StartsWith("/errorlog:") || x.StartsWith("/langversion:") || x.StartsWith("/define:") || x.StartsWith("/nullable:") || x.StartsWith("/target:") || x.StartsWith("/debug:") || x.StartsWith("/platform:") || x.StartsWith("/warn:") || x.StartsWith("/nowarn:") || x.StartsWith("/warnaserror:") || x.StartsWith("/warnaserror+:") || x.StartsWith("/warnaserror-:") || x.StartsWith("/features:") || x.StartsWith("/pathmap:") || x.StartsWith("/usings:") || x.StartsWith("/moduleassemblyname:") || x.StartsWith("/main:") || x.StartsWith("/baseaddress:") || x.StartsWith("/filealign:") || x.StartsWith("/preferreduilang:") || x.StartsWith("/instrument:") || x.StartsWith("/checksumalgorithm:") || x.StartsWith("/errorendlocation:") || x.StartsWith("/errorreport:") || new[]{"/noconfig","/fullpaths","/utf8output","/nostdlib","/nostdlib+","/nostdlib-","/checked","/checked+","/checked-","/unsafe","/unsafe+","/unsafe-","/optimize","/optimize+","/optimize-","/deterministic","/deterministic+","/deterministic-","/parallel+","/parallel-","/publicsign+","/publicsign-","/warnaserror+","/warnaserror-","/debug","/debug+","/debug-","/highentropyva+","/highentropyva-"}.Contains(x);
    private static string NodeIdForProject(Dictionary<string,JsonElement> nodes,string p,string isolated,string golden) => nodes.Values.Single(n=>Same(S(n,"project"),Map(p,isolated,golden))).GetProperty("id").GetString()!;
    private static string Map(string path,string from,string to) { if (!Under(path,from)) throw new InvalidDataException("Path outside mapped root: "+path); return Path.GetFullPath(Path.Combine(to,Path.GetRelativePath(from,path))); }
    private static string Rel(string path,string root) { if (!Under(path,root) && !Same(path,root)) throw new InvalidDataException("Path is outside root."); return Path.GetRelativePath(root,path); }
    private static string Resolve(string p,string cwd) => SafeFile(Path.IsPathFullyQualified(p)?p:Path.Combine(cwd,p));
    private static string ResolveDirectory(string p,string cwd) => SafeDirectory(Path.IsPathFullyQualified(p)?p:Path.Combine(cwd,p));
    private static string Root(string p) { string x=SafeDirectory(p); string t=Path.GetFullPath(Path.GetTempPath()); if (!Under(x,t)) throw new InvalidDataException("Evidence roots must be isolated TEMP descendants."); return x; }
    private static string SafeDirectory(string p) { if(!Path.IsPathFullyQualified(p)) throw new InvalidDataException("Absolute directory required."); string x=Path.GetFullPath(p); if(!Directory.Exists(x)) throw new FileNotFoundException(x); NoLinks(x,Path.GetPathRoot(x)!); return x; }
    private static string SafeFile(string p) { if(!Path.IsPathFullyQualified(p)) throw new InvalidDataException("Absolute file required."); string x=Path.GetFullPath(p); if(!File.Exists(x) || (File.GetAttributes(x)&FileAttributes.ReparsePoint)!=0) throw new FileNotFoundException(x); NoLinks(Path.GetDirectoryName(x)!,Path.GetPathRoot(x)!); return x; }
    private static void NoLinks(string p,string root) { string x=Path.GetFullPath(p); while(true){if((File.GetAttributes(x)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Reparse path not accepted."); if(Same(x,root))break; x=Directory.GetParent(x)?.FullName??throw new InvalidDataException("Path traversal failed.");} }
    private static bool Under(string x,string root) { string a=Path.GetFullPath(x),b=Path.GetFullPath(root); return a.StartsWith(Path.TrimEndingDirectorySeparator(b)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase); }
    private static bool Same(string a,string b) => string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);
    private static string HashFile(string p) => Hash(ReadOrdinary(p, 128*1024*1024));
    private static byte[] ReadOrdinary(string p,int max) { string f=SafeFile(p); using var s=new FileStream(f,FileMode.Open,FileAccess.Read,FileShare.Read); if(s.Length>max)throw new InvalidDataException("Input size limit exceeded."); byte[] b=new byte[(int)s.Length]; s.ReadExactly(b); return b; }
    private static JsonInput PinnedJson(string p,string sha) { byte[] b=ReadOrdinary(p,MaxJsonBytes); if(!Eq(Hash(b),sha))throw new InvalidDataException("Input SHA-256 pin mismatch: "+p); var d=JsonDocument.Parse(b,new JsonDocumentOptions{MaxDepth=96}); RejectDuplicates(d.RootElement); return new(b,d,Hash(b)); }
    private static void RejectDuplicates(JsonElement x) { if(x.ValueKind==JsonValueKind.Object){var s=new HashSet<string>(StringComparer.Ordinal); foreach(var p in x.EnumerateObject()){if(!s.Add(p.Name))throw new InvalidDataException("Duplicate JSON property.");RejectDuplicates(p.Value);}} else if(x.ValueKind==JsonValueKind.Array)foreach(var y in x.EnumerateArray())RejectDuplicates(y); }
    private static void Exact(JsonElement x,params string[] keys){if(x.ValueKind!=JsonValueKind.Object||!x.EnumerateObject().Select(p=>p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(keys))throw new InvalidDataException("JSON fields differ from pinned contract.");}
    private static bool Has(JsonElement x, string property) => x.ValueKind == JsonValueKind.Object && x.TryGetProperty(property, out _);
    private static JsonElement O(JsonElement x,string k)=>x.GetProperty(k).ValueKind==JsonValueKind.Object?x.GetProperty(k):throw new InvalidDataException(k+" must be object.");
    private static JsonElement A(JsonElement x,string k)=>x.GetProperty(k).ValueKind==JsonValueKind.Array?x.GetProperty(k):throw new InvalidDataException(k+" must be array.");
    private static string S(JsonElement x,string k)=>x.GetProperty(k).GetString()??throw new InvalidDataException(k+" must be string.");
    private static int I(JsonElement x,string k)=>x.GetProperty(k).TryGetInt32(out int n)?n:throw new InvalidDataException(k+" must be int.");
    private static long L(JsonElement x,string k)=>x.GetProperty(k).TryGetInt64(out long n)?n:throw new InvalidDataException(k+" must be long.");
    private static bool B(JsonElement x,string k)=>x.GetProperty(k).ValueKind==JsonValueKind.True;
    private static string LowerSha(string s)=>s.Length==64&&s.All(Uri.IsHexDigit)?s.ToLowerInvariant():throw new InvalidDataException("Invalid SHA-256.");
    private static string Hash(byte[] b)=>Convert.ToHexStringLower(SHA256.HashData(b));
    private static bool Eq(string a,string b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
    private static byte[] CanonicalArray(string[] a) => JsonSerializer.SerializeToUtf8Bytes(a,new JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CommandLineToArgvW")] private static extern nint NativeArgv(string s,out int n);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern nint LocalFree(nint p);
}
