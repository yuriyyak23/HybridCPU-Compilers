using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Joins one completed, C#-probed Windows Csc build into the unqualified V2
/// crosswalk and the canonical build-metadata envelope consumed by the Bridge
/// observation assembler. It never reads an expected observation.
/// </summary>
public static class CscExecutionCrosswalkProducer
{
    private const string GraphSchema = "hardwarebridge.evaluated-project-graph/v2";
    private const string TaskSchema = "hybridcpu.hardwarebridge.msbuild-csc-task-events/v1";
    private const string ProbeSchema = "hybridcpu.csc-launch-probe/v1";
    private const string CrosswalkSchema = "circuitweave.golden-windows-csc-execution-crosswalk/v1";
    private const int MaxJsonBytes = 32 * 1024 * 1024;
    private const int MaxRspBytes = 2 * 1024 * 1024;
    private const int MaxCapturedFiles = 20_000;
    private const long MaxCapturedBytes = 2L * 1024 * 1024 * 1024;

    private sealed record Pinned(JsonDocument Document, string Sha) : IDisposable
    { public void Dispose() => Document.Dispose(); }
    private sealed record FileFact(string kind, string path, long bytes, string sha256);
    private sealed record GeneratedCompile(string buildPath, long bytes, string sha256);
    private sealed record GeneratedOutput(string path, long bytes, string sha256);
    private sealed record CrossProject(string project, string taskId, string taskStartUtc, string taskFinishUtc,
        bool taskSucceeded, int probePid, int childPid, int childExitCode, string probeUtc,
        string workingDirectory, bool taskArgvExpandedMatchesResponse, string responsePath,
        string responseCopyPath, long responseBytes, string responseSha256,
        bool responseSourceHandleLockedThroughChild, int cscArgvCount, int compileItemCount,
        int cscSourceCount, bool compileEqualsCscSources, int sourcesMatchingPinnedGolden,
        GeneratedCompile[] msbuildGeneratedCompileSources, FileFact[] fileInputs,
        string generatedSourceOutputDirectory, GeneratedOutput[] generatorSourceOutputs);
    private sealed record Crosswalk(string schema, string disposition, string goldenHead, string goldenTree,
        string isolatedBuildHead, string sdk, string cscSha256, int projectCount, int compileItemTotal,
        int cscSourceTotal, int generatorOutputTotal, int taskEvents, CrossProject[] projects,
        object claims);
    private sealed record CanonicalMetadata(string[] arguments, int exitCode, string started,
        string goldenHead, string goldenTree, string sdk, string cscSha256, string probeSha256,
        string generatedRoot, string gitExecutablePath, string gitExecutableSha256);
    private sealed record Probe(int Pid, JsonElement Launch, JsonElement Snapshot, JsonElement Result);
    private sealed record ProjectFact(string Project, string Directory, string TargetFramework,
        string Configuration, string[] CompileFiles, string[] ProjectReferences);

    /// <summary>
    /// Usage: produce &lt;baseline-graph&gt; &lt;baseline-graph-sha&gt; &lt;same-build-graph&gt;
    /// &lt;same-build-graph-sha&gt; &lt;task-events&gt; &lt;task-sha&gt; &lt;project-facts.tsv&gt; &lt;facts-sha&gt; &lt;build-metadata&gt; &lt;metadata-sha&gt; &lt;golden-root&gt;
    /// &lt;isolated-root&gt; &lt;generated-root&gt; &lt;compiler&gt; &lt;probe-exe&gt;
    /// &lt;probe-dir&gt; &lt;response-dir&gt; &lt;crosswalk-temp-output&gt; &lt;metadata-temp-output&gt;.
    /// </summary>
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 20 || args[0] != "produce")
                throw new InvalidDataException("Usage: produce <baseline-graph> <baseline-graph-sha> <same-build-graph> <same-build-graph-sha> <task-events> <task-sha> <project-facts.tsv> <facts-sha> <build-metadata> <metadata-sha> <golden-root> <isolated-root> <generated-root> <compiler> <probe-exe> <probe-dir> <response-dir> <crosswalk-temp-output> <metadata-temp-output>");
            Produce(args.Skip(1).ToArray());
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or ArgumentException or FormatException or OverflowException or DllNotFoundException or
            EntryPointNotFoundException or InvalidOperationException)
        {
            Console.Error.WriteLine("REFUSED: " + e.Message);
            return 2;
        }
    }

    private static void Produce(string[] a)
    {
        using Pinned graphIn = PinnedJson(a[0], a[1]);
        using Pinned sameBuildGraphIn = PinnedJson(a[2], a[3]);
        using Pinned taskIn = PinnedJson(a[4], a[5]);
        using Pinned rawMetadataIn = PinnedJson(a[8], a[9]);
        JsonElement graph = graphIn.Document.RootElement;
        JsonElement sameBuildGraph = sameBuildGraphIn.Document.RootElement;
        JsonElement taskRoot = taskIn.Document.RootElement;
        JsonElement rawMetadata = rawMetadataIn.Document.RootElement;
        Exact(graph, "schema", "disposition", "root", "rootProjectSha256", "globalProperties", "nodes", "scope");
        Exact(sameBuildGraph, "schema", "disposition", "root", "rootProjectSha256", "sdkDirectory", "globalProperties", "nodes", "inventoryAudit", "scope");
        Exact(taskRoot, "schema", "disposition", "error", "events");
        Exact(rawMetadata, "arguments", "exitCode", "started", "finished", "goldenHead", "goldenTree", "sdk", "cscPath", "cscSha256", "probePath", "probeSha256", "generatedRoot");
        if (S(graph, "schema") != GraphSchema || S(graph, "disposition") is "refused" or "failed" ||
            S(taskRoot, "schema") != TaskSchema || S(taskRoot, "disposition") != "unqualified-software-observation" ||
            taskRoot.GetProperty("error").ValueKind != JsonValueKind.Null || I(rawMetadata, "exitCode") != 0)
            throw new InvalidDataException("Graph/task/build evidence is refused, failed, or has an unsupported schema.");

        string goldenRoot = EvidenceRoot(a[10]);
        string isolatedRoot = EvidenceRoot(a[11]);
        string generatedRoot = EvidenceRoot(a[12]);
        string compiler = SafeFile(a[13]);
        string probeExe = SafeFile(a[14]);
        string probeDir = SafeDirectory(a[15]);
        string responseDir = SafeDirectory(a[16]);
        if (Same(goldenRoot, isolatedRoot) || Under(goldenRoot, isolatedRoot) || Under(isolatedRoot, goldenRoot) ||
            Under(generatedRoot, isolatedRoot) || Under(isolatedRoot, generatedRoot) ||
            Same(generatedRoot, goldenRoot) || Under(generatedRoot, goldenRoot) || Under(goldenRoot, generatedRoot) ||
            !Same(generatedRoot, EvidenceRoot(S(rawMetadata, "generatedRoot"))))
            throw new InvalidDataException("Golden/build/generated roots overlap or generated root differs from build metadata.");
        string goldenHead = S(rawMetadata, "goldenHead"), goldenTree = S(rawMetadata, "goldenTree");
        string gitExe = SafeFile(Environment.GetEnvironmentVariable("HB_PINNED_GIT_EXE") ?? throw new InvalidDataException("HB_PINNED_GIT_EXE is required."));
        string gitSha = LowerSha(Environment.GetEnvironmentVariable("HB_PINNED_GIT_SHA256") ?? throw new InvalidDataException("HB_PINNED_GIT_SHA256 is required."));
        if (!Eq(HashFile(gitExe), gitSha)) throw new InvalidDataException("Pinned Git executable SHA-256 mismatch.");
        string isolatedHead = Git(gitExe, goldenRoot, "rev-parse", "HEAD");
        string isolatedTree = Git(gitExe, goldenRoot, "rev-parse", "HEAD^{tree}");
        string buildHead = Git(gitExe, isolatedRoot, "rev-parse", "HEAD");
        string buildTree = Git(gitExe, isolatedRoot, "rev-parse", "HEAD^{tree}");
        if (!Eq(isolatedHead, goldenHead) || !Eq(isolatedTree, goldenTree) || !Eq(buildHead, goldenHead) || !Eq(buildTree, goldenTree) ||
            !CleanBuildRoot(gitExe, goldenRoot) || !CleanBuildRoot(gitExe, isolatedRoot))
            throw new InvalidDataException("Baseline/build clone identity or worktree inputs differ from the pinned Golden build.");

        string sdk = S(rawMetadata, "sdk"), compilerSha = LowerSha(S(rawMetadata, "cscSha256"));
        string probeSha = LowerSha(S(rawMetadata, "probeSha256"));
        if (!Same(compiler, S(rawMetadata, "cscPath")) || !Same(probeExe, S(rawMetadata, "probePath")) ||
            !Eq(HashFile(compiler), compilerSha) || !Eq(HashFile(probeExe), probeSha))
            throw new InvalidDataException("Compiler/probe executable paths or bytes disagree with build metadata.");
        if (!OperatingSystem.IsWindows()) throw new InvalidDataException("Csc execution crosswalk requires Windows argv semantics.");

        if (!Eq(HashFile(a[6]), LowerSha(a[7]))) throw new InvalidDataException("Project-facts SHA-256 pin mismatch.");
        Dictionary<string, ProjectFact> facts = ReadFacts(a[6]);
        JsonElement[] nodes = Arr(graph, "nodes").EnumerateArray().ToArray();
        JsonElement[] sameBuildNodes = Arr(sameBuildGraph, "nodes").EnumerateArray().ToArray();
        if (nodes.Length is < 1 or > 100) throw new InvalidDataException("Pinned graph node count is invalid.");
        if (S(sameBuildGraph, "schema") != GraphSchema || S(sameBuildGraph, "disposition") is "refused" or "failed" || sameBuildNodes.Length != nodes.Length)
            throw new InvalidDataException("Same-build evaluated graph differs in schema/disposition/node count.");
        string sameBuildRoot = Path.GetFullPath(S(sameBuildGraph, "root"));
        if (!Same(sameBuildRoot, Path.Combine(isolatedRoot, Path.GetRelativePath(goldenRoot, S(graph, "root")))))
            throw new InvalidDataException("Pinned baseline and same-build graph roots do not map by the supplied clone roots.");
        var sameNodes = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement n in sameBuildNodes)
        {
            string project = Path.GetFullPath(S(n, "project"));
            if (!Under(project, isolatedRoot) || !sameNodes.TryAdd(project, n)) throw new InvalidDataException("Same-build graph node path is outside/duplicated in isolated root.");
        }
        var nodeByBuildProject = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var compileByProject = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement n in nodes)
        {
            string originalProject = Path.GetFullPath(S(n, "project"));
            if (!Under(originalProject, goldenRoot)) throw new InvalidDataException("Graph project escapes the pinned baseline root.");
            string buildProject = Map(originalProject, goldenRoot, isolatedRoot);
            if (!nodeByBuildProject.TryAdd(buildProject, n)) throw new InvalidDataException("Ambiguous graph project identity.");
            if (!sameNodes.TryGetValue(buildProject, out JsonElement sameNode) ||
                !CompatibleGlobalIdentity(S(n, "id"), S(sameNode, "id"), goldenRoot, isolatedRoot) ||
                !Eq(HashFile(originalProject), HashFile(buildProject)))
                throw new InvalidDataException("Baseline and same-build project graph nodes do not bind by path/configuration/project bytes.");
            string[] baselineRefs = Arr(n, "references").EnumerateArray().Select(x => x.GetString() ??
                throw new InvalidDataException("Null baseline project-reference node.")).ToArray();
            string[] currentRefs = Arr(sameNode, "references").EnumerateArray().Select(x => x.GetString() ??
                throw new InvalidDataException("Null same-build project-reference node.")).ToArray();
            string[] mappedRefProjects = baselineRefs.Select(x => Map(NodeIdProject(x), goldenRoot, isolatedRoot)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            string[] currentRefProjects = currentRefs.Select(NodeIdProject).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!mappedRefProjects.SequenceEqual(currentRefProjects, StringComparer.OrdinalIgnoreCase) ||
                baselineRefs.Any(oldId => !currentRefs.Any(newId => Same(Map(NodeIdProject(oldId), goldenRoot, isolatedRoot), NodeIdProject(newId)) &&
                    CompatibleGlobalIdentity(oldId, newId, goldenRoot, isolatedRoot))))
                throw new InvalidDataException("Baseline and same-build project-reference edges differ.");
            string[] compile = Arr(n, "compileFiles").EnumerateArray().Select(x => Path.GetFullPath(x.GetString() ?? throw new InvalidDataException("Null graph Compile path."))).ToArray();
            if (compile.Distinct(StringComparer.OrdinalIgnoreCase).Count() != compile.Length) throw new InvalidDataException("Duplicate graph Compile path.");
            var freshCompile = Arr(sameNode, "compileFiles").EnumerateArray().Select(x => Path.GetFullPath(x.GetString() ?? throw new InvalidDataException("Null same-build Compile path."))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mappedCompile = compile.Select(x => Map(x, goldenRoot, isolatedRoot)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!freshCompile.SetEquals(mappedCompile)) throw new InvalidDataException("Same-build evaluated Compile set differs from the pinned baseline graph.");
            foreach (string source in compile) if (!Eq(HashFile(source), HashFile(Map(source, goldenRoot, isolatedRoot))))
                throw new InvalidDataException("Baseline/isolated graph source bytes differ.");
            compileByProject.Add(buildProject, compile.ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (!facts.ContainsKey(buildProject)) throw new InvalidDataException("Same-build project facts omit a pinned graph node.");
            string[] factRefs = facts[buildProject].ProjectReferences.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            string[] graphRefProjects = currentRefs.Select(NodeIdProject).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!factRefs.SequenceEqual(graphRefProjects, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Same-build ProjectReference facts differ from evaluated graph edges.");
        }
        if (facts.Count != nodes.Length) throw new InvalidDataException("Project facts and graph project counts differ.");

        string[] probeFiles = Directory.EnumerateFiles(probeDir, "csc-launch-*.jsonl", SearchOption.TopDirectoryOnly).Select(SafeFile).ToArray();
        var probes = new Dictionary<int, Probe>();
        foreach (string file in probeFiles)
        {
            Probe p = ReadProbe(file);
            if (!Path.GetFileName(file).Equals($"csc-launch-{p.Pid}.jsonl", StringComparison.OrdinalIgnoreCase) || !probes.TryAdd(p.Pid, p))
                throw new InvalidDataException("Probe PID/file identity is duplicate or inconsistent.");
        }
        JsonElement[] events = Arr(taskRoot, "events").EnumerateArray().ToArray();
        if (events.Length != nodes.Length || probes.Count != nodes.Length || probeFiles.Length != nodes.Length)
            throw new InvalidDataException("Task/probe/graph counts must match exactly.");
        var eventsByProject = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement ev in events)
        {
            Exact(ev, "Id", "ProjectFile", "StartUtc", "FinishUtc", "CommandLine", "Succeeded", "CommandEvents");
            string project = Path.GetFullPath(S(ev, "ProjectFile"));
            if (!B(ev, "Succeeded") || I(ev, "CommandEvents") != 1 || !eventsByProject.TryAdd(project, ev))
                throw new InvalidDataException("Task event failed, duplicated, or has ambiguous Csc command lines.");
        }

        var rows = new List<CrossProject>();
        int compileTotal = 0, generatedTotal = 0, capturedFiles = 0;
        long capturedBytes = 0;
        foreach (var kv in nodeByBuildProject.OrderBy(k => S(k.Value, "id"), StringComparer.Ordinal))
        {
            string buildProject = kv.Key;
            JsonElement node = kv.Value;
            if (!eventsByProject.TryGetValue(buildProject, out JsonElement ev)) throw new InvalidDataException("Csc task event is missing for graph project.");
            ProjectFact fact = facts[buildProject];
            if (!Same(fact.Project, buildProject) || !Same(fact.Directory, Path.GetDirectoryName(buildProject)!) ||
                !fact.Configuration.Equals("Release", StringComparison.OrdinalIgnoreCase) || !fact.TargetFramework.Equals("net11.0", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Project facts do not bind the release build project/cwd/framework.");
            string cwd = SafeDirectory(Path.GetDirectoryName(buildProject)!);
            Probe p = probes.Values.SingleOrDefault(x => Same(S(x.Launch, "workingDirectory"), cwd)) ??
                throw new InvalidDataException("No unique probe process matches task project working directory.");
            JsonElement launch = p.Launch, snap = p.Snapshot, result = p.Result;
            string[] command = CommandLineToArgvW(S(ev, "CommandLine"));
            if (command.Length < 3 || !Same(command[0], probeExe) || I(launch, "probePid") != p.Pid ||
                !B(launch, "targetPathPinMatched") || !B(launch, "targetHashPinMatched") ||
                !Same(S(launch, "targetExecutablePath"), compiler) || !Eq(S(launch, "targetExecutableSha256"), compilerSha) ||
                !Same(S(launch, "workingDirectory"), cwd) || !B(result, "childStarted") ||
                S(result, "status") != "Exited" || I(result, "childExitCode") != 0 ||
                !B(snap, "sourceHeldReadOnlyUntilChildExit"))
                throw new InvalidDataException("Probe process/child/compiler evidence is incomplete or mismatched.");
            string[] launchArgs = Arr(launch, "arguments").EnumerateArray().Select(x => x.GetString() ?? throw new InvalidDataException("Null launch argument.")).ToArray();
            if (launchArgs.Length != 2 || !launchArgs[0].Equals("/noconfig", StringComparison.OrdinalIgnoreCase) || !launchArgs[1].StartsWith('@'))
                throw new InvalidDataException("CscLaunchProbe did not receive the unique expected @response argument.");
            string responsePath = S(launch, "responseFilePath");
            if (!Same(responsePath, launchArgs[1][1..]) ||
                !Same(responsePath, S(snap, "responseFilePath"))) throw new InvalidDataException("Probe response path bindings disagree.");
            string responseCopy = SafeFile(S(launch, "responseCopyPath"));
            string expectedCopy = Path.Combine(responseDir, $"csc-launch-{p.Pid}.rsp");
            if (!Under(responseCopy, responseDir) || !Same(responseCopy, expectedCopy) || !Same(responseCopy, S(snap, "responseCopyPath")) ||
                !Same(responseCopy, S(result, "responseCopyPath"))) throw new InvalidDataException("Response copy path is ambiguous or outside the evidence directory.");
            byte[] responseBytes = ReadOrdinary(responseCopy, MaxRspBytes);
            string responseSha = Hash(responseBytes);
            if (responseBytes.LongLength != L(snap, "responseBytes") || !Eq(responseSha, S(snap, "responseSha256")) ||
                L(result, "responseBytes") != responseBytes.LongLength || !Eq(responseSha, S(result, "responseSha256")))
                throw new InvalidDataException("Response bytes differ from probe snapshot/result pins.");
            string responseText = new UTF8Encoding(false, true).GetString(responseBytes);
            if (responseText.StartsWith('\uFEFF')) responseText = responseText[1..];
            string[] argv = ParseResponse(responseText);
            if (command.Length != argv.Length + 2 || !command[1].Equals("/noconfig", StringComparison.OrdinalIgnoreCase) ||
                !command.Skip(2).SequenceEqual(argv, StringComparer.Ordinal))
                throw new InvalidDataException("MSBuild task argv does not exactly match parsed captured response bytes.");
            DateTimeOffset taskStart = DateTimeOffset.Parse(S(ev, "StartUtc"), System.Globalization.CultureInfo.InvariantCulture);
            DateTimeOffset taskFinish = DateTimeOffset.Parse(S(ev, "FinishUtc"), System.Globalization.CultureInfo.InvariantCulture);
            DateTimeOffset probeTime = DateTimeOffset.Parse(S(launch, "timestampUtc"), System.Globalization.CultureInfo.InvariantCulture);
            if (taskFinish < taskStart || probeTime < taskStart || probeTime > taskFinish)
                throw new InvalidDataException("Probe launch time does not fall inside the matching MSBuild Csc task interval.");

            var sources = new List<string>();
            var fileInputs = new List<FileFact>();
            string? generatedDir = null;
            foreach (string token in argv)
            {
                string lower = token.ToLowerInvariant();
                if (lower.EndsWith(".cs", StringComparison.Ordinal) && !lower.StartsWith('/') && !lower.StartsWith('-'))
                    sources.Add(ResolveFile(token, cwd));
                else if (lower.StartsWith("/generatedfilesout:", StringComparison.Ordinal))
                {
                    if (generatedDir is not null) throw new InvalidDataException("Duplicate /generatedfilesout switch.");
                    generatedDir = SafeDirectory(ResolveDirectory(token[(token.IndexOf(':') + 1)..], cwd));
                }
                else if (IsFileSwitch(lower))
                {
                    int colon = token.IndexOf(':');
                    if (colon <= 0) throw new InvalidDataException("Malformed file-valued Csc switch.");
                    string value = token[(colon + 1)..];
                    if (lower.StartsWith("/reference:") && value.Contains('=')) throw new InvalidDataException("Reference aliases are outside this V2 crosswalk contract.");
                    string path = ResolveFile(value, cwd);
                    string kind = FileKind(lower[..(colon + 1)]);
                    if (fileInputs.Any(x => Same(x.path, path))) throw new InvalidDataException("Duplicate Csc file input path.");
                    byte[] bytes = ReadOrdinary(path, 128 * 1024 * 1024);
                    capturedFiles = checked(capturedFiles + 1);
                    capturedBytes = checked(capturedBytes + bytes.LongLength);
                    if (capturedFiles > MaxCapturedFiles || capturedBytes > MaxCapturedBytes)
                        throw new InvalidDataException("Aggregate Csc file-input capture limit exceeded.");
                    fileInputs.Add(new FileFact(kind, path, bytes.LongLength, Hash(bytes)));
                }
                else if (!IsOutputOrScalar(lower)) throw new InvalidDataException("Unclassified Csc argv token: " + token);
            }
            if (generatedDir is null || !Under(generatedDir, generatedRoot)) throw new InvalidDataException("Missing or out-of-root generator output directory.");
            string[] projectSources = sources.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (projectSources.Length != sources.Count || projectSources.Length != fact.CompileFiles.Length)
                throw new InvalidDataException("Csc source inventory and same-build MSBuild Compile facts are incomplete/duplicated.");
            var cscSourceSet = projectSources.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!cscSourceSet.SetEquals(fact.CompileFiles)) throw new InvalidDataException("Csc source operands differ from same-build Compile facts.");
            capturedFiles = checked(capturedFiles + projectSources.Length);
            if (capturedFiles > MaxCapturedFiles) throw new InvalidDataException("Aggregate captured file-count limit exceeded.");
            foreach (string source in projectSources)
            {
                capturedBytes = checked(capturedBytes + new FileInfo(source).Length);
                if (capturedBytes > MaxCapturedBytes) throw new InvalidDataException("Aggregate captured-byte limit exceeded.");
            }
            HashSet<string> baselineCompile = compileByProject[buildProject];
            var generatedCompile = new List<GeneratedCompile>();
            int sourceMatchesBaseline = 0;
            foreach (string source in projectSources)
            {
                string original = Map(source, isolatedRoot, goldenRoot);
                string sourceSha = HashFile(source);
                if (baselineCompile.Contains(original))
                {
                    if (!Eq(sourceSha, HashFile(original))) throw new InvalidDataException("Golden-origin source bytes changed in isolated build clone.");
                    sourceMatchesBaseline++;
                }
                else
                {
                    var file = new FileInfo(source);
                    generatedCompile.Add(new GeneratedCompile(source, file.Length, sourceSha));
                }
            }
            string[] generatedFiles = EnumerateBoundedCs(generatedDir);
            capturedFiles = checked(capturedFiles + generatedFiles.Length);
            if (capturedFiles > MaxCapturedFiles) throw new InvalidDataException("Aggregate captured file-count limit exceeded.");
            foreach (string file in generatedFiles)
            {
                long length = new FileInfo(file).Length;
                capturedBytes = checked(capturedBytes + length);
                if (capturedBytes > MaxCapturedBytes) throw new InvalidDataException("Aggregate captured-byte limit exceeded.");
            }
            var generatorOutputs = generatedFiles.Select(path => new GeneratedOutput(path,
                new FileInfo(path).Length, HashFile(path))).ToArray();
            int childPid = I(result, "childPid");
            rows.Add(new CrossProject(buildProject, S(ev, "Id"), S(ev, "StartUtc"), S(ev, "FinishUtc"),
                B(ev, "Succeeded"), p.Pid, childPid, I(result, "childExitCode"), S(launch, "timestampUtc"),
                cwd, true, responsePath, responseCopy, responseBytes.LongLength, responseSha, true,
                argv.Length, projectSources.Length, projectSources.Length, true, sourceMatchesBaseline,
                generatedCompile.ToArray(), fileInputs.ToArray(), generatedDir, generatorOutputs));
            compileTotal += projectSources.Length;
            generatedTotal += generatorOutputs.Length;
        }
        if (rows.Count != nodes.Length || generatedTotal == 0)
            throw new InvalidDataException("Crosswalk rows are incomplete or no generator outputs were observed.");

        var crosswalk = new Crosswalk(CrosswalkSchema, "unqualified-software-observation", goldenHead,
            goldenTree, buildHead, sdk, compilerSha, rows.Count, compileTotal, compileTotal, generatedTotal,
            events.Length, rows.ToArray(), new
            {
                actualCscLaunchObserved = true,
                responseBytesStableAtPathWhileChildRan = true,
                gitExecutablePath = gitExe,
                gitExecutableSha256 = gitSha,
                cscConsumedExactResponseBytes = false,
                sourceAndAnalyzerReadBuffersProven = false,
                generatedSourceSnapshotOccursAfterBuild = true,
                independentCoreAdmission = "NotEstablished",
                trustedHandoff = "NotEstablished"
            });
        var metadata = new CanonicalMetadata(
            Arr(rawMetadata, "arguments").EnumerateArray().Select(x => x.GetString() ?? throw new InvalidDataException("Null build argument.")).ToArray(),
            I(rawMetadata, "exitCode"), S(rawMetadata, "started"), goldenHead, goldenTree, sdk,
            compilerSha, probeSha, generatedRoot, gitExe, gitSha);
        ValidateNewTemp(a[17]); ValidateNewTemp(a[18]);
        if (Same(a[17], a[18])) throw new InvalidDataException("Crosswalk and metadata outputs must be distinct.");
        WriteNewTemp(a[17], JsonSerializer.SerializeToUtf8Bytes(crosswalk, JsonOptions));
        WriteNewTemp(a[18], JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(new { status = "ObservedUnqualified", projectCount = rows.Count,
            sourceCount = compileTotal, generatorSourceCount = generatedTotal,
            crosswalkSha256 = HashFile(a[17]), metadataSha256 = HashFile(a[18]),
            cscReadConsumption = "NotEstablished", admission = "NotAdmitted" }));
    }

    private static ProjectFact[] ParseFacts(string path)
    {
        var projects = new Dictionary<string, (string Dir, string Tfm, string Config, List<string> Sources, List<string> Refs)>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(path))
        {
            string[] p = line.Split('|');
            if (p.Length == 5 && p[0] == "PROJECT")
            {
                string project = Path.GetFullPath(p[1]);
                if (!projects.TryAdd(project, (Path.GetFullPath(p[2]), p[3], p[4], new(), new()))) throw new InvalidDataException("Duplicate PROJECT facts row.");
            }
            else if (p.Length == 3 && p[0] is "COMPILE" or "PROJECTREFERENCE")
            {
                string project = Path.GetFullPath(p[1]);
                if (!projects.TryGetValue(project, out var row)) throw new InvalidDataException("Item fact precedes PROJECT row or names unknown project.");
                if (p[0] == "COMPILE") row.Sources.Add(Path.GetFullPath(p[2])); else row.Refs.Add(Path.GetFullPath(p[2]));
            }
            else throw new InvalidDataException("Malformed project-facts.tsv row.");
        }
        return projects.Select(p => new ProjectFact(p.Key, p.Value.Dir, p.Value.Tfm, p.Value.Config,
            p.Value.Sources.ToArray(), p.Value.Refs.ToArray())).ToArray();
    }

    private static Dictionary<string, ProjectFact> ReadFacts(string path)
    {
        string p = SafeFile(path);
        return ParseFacts(p).ToDictionary(x => x.Project, StringComparer.OrdinalIgnoreCase);
    }

    private static Probe ReadProbe(string path)
    {
        string[] lines = File.ReadAllLines(SafeFile(path));
        if (lines.Length != 3) throw new InvalidDataException("Each Csc launch probe must contain exactly launch/snapshot/result JSON records.");
        using JsonDocument d = JsonDocument.Parse("[" + string.Join(',', lines) + "]");
        JsonElement[] rows = d.RootElement.EnumerateArray().ToArray();
        if (rows.Any(x => S(x, "schema") != ProbeSchema) || S(rows[0], "record") != "launch" ||
            S(rows[1], "record") != "response-snapshot" || S(rows[2], "record") != "result")
            throw new InvalidDataException("Probe record schema/order is unsupported.");
        int pid = I(rows[0], "probePid");
        if (I(rows[0], "argumentCount") != 2 || I(rows[2], "childExitCode") != 0 || !B(rows[2], "childStarted"))
            throw new InvalidDataException("Probe launch/child did not complete successfully.");
        return new Probe(pid, rows[0].Clone(), rows[1].Clone(), rows[2].Clone());
    }

    private static void ValidateNewTemp(string path)
    {
        string full = Path.GetFullPath(path), temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!Same(Path.GetDirectoryName(full)!, temp) || File.Exists(full) || Directory.Exists(full))
            throw new InvalidDataException("Crosswalk/metadata outputs must be new direct TEMP files.");
    }
    private static void WriteNewTemp(string path, byte[] bytes)
    {
        ValidateNewTemp(path);
        string full = Path.GetFullPath(path);
        using var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        fs.Write(bytes); fs.Flush(true);
    }

    private static Pinned PinnedJson(string path, string sha)
    {
        byte[] bytes = ReadOrdinary(path, MaxJsonBytes);
        if (!Eq(Hash(bytes), LowerSha(sha))) throw new InvalidDataException("Input SHA-256 pin mismatch: " + path);
        JsonDocument d = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 96 });
        RejectDuplicates(d.RootElement);
        return new Pinned(d, Hash(bytes));
    }
    private static void RejectDuplicates(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty p in e.EnumerateObject())
            { if (!names.Add(p.Name)) throw new InvalidDataException("Duplicate JSON member."); RejectDuplicates(p.Value); }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (JsonElement x in e.EnumerateArray()) RejectDuplicates(x);
    }
    private static void Exact(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(names))
            throw new InvalidDataException("JSON fields differ from pinned contract.");
    }
    private static JsonElement Arr(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Array ? e.GetProperty(name) : throw new InvalidDataException(name + " must be an array.");
    private static string S(JsonElement e, string name) => e.GetProperty(name).GetString() ?? throw new InvalidDataException(name + " must be a string.");
    private static int I(JsonElement e, string name) => e.GetProperty(name).TryGetInt32(out int n) ? n : throw new InvalidDataException(name + " must be an int.");
    private static long L(JsonElement e, string name) => e.GetProperty(name).TryGetInt64(out long n) ? n : throw new InvalidDataException(name + " must be an int64.");
    private static bool B(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.True;
    private static string LowerSha(string value) => value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : throw new InvalidDataException("Invalid SHA-256 value.");

    private static string EvidenceRoot(string path)
    {
        string full = SafeDirectory(path), temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!Under(full, temp)) throw new InvalidDataException("Evidence roots must be descendants of TEMP.");
        return full;
    }
    private static string SafeDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Absolute directory path required.");
        string full = Path.GetFullPath(path); if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        NoLinks(full, Path.GetPathRoot(full)!); return full;
    }
    private static string SafeFile(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Absolute file path required.");
        string full = Path.GetFullPath(path);
        if (!File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new FileNotFoundException(full);
        NoLinks(Path.GetDirectoryName(full)!, Path.GetPathRoot(full)!); return full;
    }
    private static void NoLinks(string path, string root)
    {
        string current = Path.GetFullPath(path);
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse paths are refused.");
            if (Same(current, root)) break;
            current = Directory.GetParent(current)?.FullName ?? throw new InvalidDataException("Path parent traversal failed.");
        }
    }
    private static bool Under(string path, string root) => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool Same(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static string Map(string path, string from, string to)
    {
        if (!Under(path, from)) throw new InvalidDataException("Path escapes mapped root.");
        return Path.GetFullPath(Path.Combine(to, Path.GetRelativePath(from, path)));
    }
    private static string NodeIdProject(string id)
    {
        int separator = id.IndexOf('|');
        if (separator <= 0) throw new InvalidDataException("Malformed project-reference node identity.");
        return Path.GetFullPath(id[..separator]);
    }
    private static bool CompatibleGlobalIdentity(string baselineId, string sameBuildId, string goldenRoot, string isolatedRoot)
    {
        int baselineBar = baselineId.IndexOf('|'), buildBar = sameBuildId.IndexOf('|');
        if (baselineBar <= 0 || buildBar <= 0) throw new InvalidDataException("Graph node identity lacks a project/global-property separator.");
        if (!Same(Map(baselineId[..baselineBar], goldenRoot, isolatedRoot), sameBuildId[..buildBar])) return false;
        var baseline = ParseGlobalProperties(baselineId[(baselineBar + 1)..]);
        var actual = ParseGlobalProperties(sameBuildId[(buildBar + 1)..]);
        foreach (var item in baseline)
        {
            if (actual.TryGetValue(item.Key, out string? value))
            { if (!value.Equals(item.Value, StringComparison.OrdinalIgnoreCase)) return false; }
            else if (!(item.Key.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase) && item.Value.Equals("net11.0", StringComparison.OrdinalIgnoreCase))) return false;
        }
        foreach (var item in actual)
        {
            if (baseline.ContainsKey(item.Key)) continue;
            if (!((item.Key.Equals("EnableInternalTestHooks", StringComparison.OrdinalIgnoreCase) && item.Value.Equals("true", StringComparison.OrdinalIgnoreCase)) ||
                  (item.Key.Equals("IsGraphBuild", StringComparison.OrdinalIgnoreCase) && item.Value.Equals("true", StringComparison.OrdinalIgnoreCase)))) return false;
        }
        return true;
    }
    private static Dictionary<string, string> ParseGlobalProperties(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string item in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = item.IndexOf('=');
            if (equals < 1 || equals == item.Length - 1 || !result.TryAdd(item[..equals], item[(equals + 1)..]))
                throw new InvalidDataException("Malformed/duplicate graph global property identity.");
        }
        return result;
    }
    private static string ResolveFile(string path, string cwd) => SafeFile(Path.IsPathFullyQualified(path) ? path : Path.Combine(cwd, path));
    private static string[] EnumerateBoundedCs(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count != 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Generated-source tree contains a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else if (Path.GetExtension(entry).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(SafeFile(entry));
                    if (files.Count > MaxCapturedFiles) throw new InvalidDataException("Generated-source file-count limit exceeded.");
                }
            }
        }
        return files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string ResolveDirectory(string path, string cwd) => Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(cwd, path));
    private static byte[] ReadOrdinary(string path, int max)
    {
        string full = SafeFile(path); using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length > max) throw new InvalidDataException("Input exceeds byte limit.");
        byte[] b = new byte[checked((int)fs.Length)]; fs.ReadExactly(b); return b;
    }
    private static string HashFile(string path) => Hash(ReadOrdinary(path, 128 * 1024 * 1024));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool CleanBuildRoot(string gitExe, string root)
    {
        string[] lines = Git(gitExe, root, "status", "--porcelain", "--ignored=matching", "--untracked-files=all")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines)
        {
            if (line.Length < 4) return false;
            string state = line[..2], path = line[3..].Replace('\\', '/').Trim('"');
            if (state == "!!" && (path.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/obj/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/bin/", StringComparison.OrdinalIgnoreCase))) continue;
            return false;
        }
        return true;
    }

    private static string Git(string gitExe, string root, params string[] args)
    {
        using var p = new Process(); p.StartInfo.FileName = gitExe; p.StartInfo.WorkingDirectory = root;
        p.StartInfo.UseShellExecute = false; p.StartInfo.RedirectStandardOutput = true; p.StartInfo.RedirectStandardError = true;
        foreach (string key in p.StartInfo.Environment.Keys.Where(x => x.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            p.StartInfo.Environment.Remove(key);
        p.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; p.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        p.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0"; p.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        p.StartInfo.Environment["GIT_ATTR_NOSYSTEM"] = "1";
        p.StartInfo.ArgumentList.Add("-C"); p.StartInfo.ArgumentList.Add(root);
        foreach (string a in args) p.StartInfo.ArgumentList.Add(a);
        p.Start(); string output = p.StandardOutput.ReadToEnd(); string error = p.StandardError.ReadToEnd(); p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidDataException("git " + string.Join(' ', args) + " failed: " + error.Trim());
        return output.Trim();
    }
    private static string[] ParseResponse(string text) => CommandLineToArgvW("csc.exe " + text).Skip(1).ToArray();
    private static string[] CommandLineToArgvW(string text)
    {
        nint ptr = NativeArgv(text, out int count);
        if (ptr == 0 || count < 1 || count > 8194) throw new InvalidDataException("Win32 CommandLineToArgvW failed or argv bound exceeded.");
        try
        {
            var result = new string[count];
            for (int i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr, i * IntPtr.Size)) ?? throw new InvalidDataException("Null argv token.");
            return result;
        }
        finally { _ = LocalFree(ptr); }
    }
    private static bool IsFileSwitch(string x) => new[] { "/reference:", "/r:", "/analyzer:", "/analyzerconfig:", "/additionalfile:", "/sourcelink:", "/appconfig:", "/embed:" }.Any(x.StartsWith);
    private static string FileKind(string x) => x switch
    {
        "/reference:" or "/r:" => "reference", "/analyzer:" => "analyzer", "/analyzerconfig:" => "analyzer-config",
        "/additionalfile:" => "additional-file", "/sourcelink:" => "source-link", "/appconfig:" => "app-config",
        "/embed:" => "embedded", _ => throw new InvalidDataException("Unsupported Csc file-valued switch.")
    };
    private static bool IsOutputOrScalar(string x) => x.StartsWith("/out:") || x.StartsWith("/pdb:") || x.StartsWith("/refout:") || x.StartsWith("/doc:") || x.StartsWith("/errorlog:") || x.StartsWith("/langversion:") || x.StartsWith("/define:") || x.StartsWith("/nullable:") || x.StartsWith("/target:") || x.StartsWith("/debug:") || x.StartsWith("/platform:") || x.StartsWith("/warn:") || x.StartsWith("/nowarn:") || x.StartsWith("/warnaserror:") || x.StartsWith("/warnaserror+:") || x.StartsWith("/warnaserror-:") || x.StartsWith("/features:") || x.StartsWith("/pathmap:") || x.StartsWith("/usings:") || x.StartsWith("/moduleassemblyname:") || x.StartsWith("/main:") || x.StartsWith("/baseaddress:") || x.StartsWith("/filealign:") || x.StartsWith("/preferreduilang:") || x.StartsWith("/instrument:") || x.StartsWith("/checksumalgorithm:") || x.StartsWith("/errorendlocation:") || x.StartsWith("/errorreport:") || new[] { "/noconfig", "/fullpaths", "/utf8output", "/nostdlib", "/nostdlib+", "/nostdlib-", "/checked", "/checked+", "/checked-", "/unsafe", "/unsafe+", "/unsafe-", "/optimize", "/optimize+", "/optimize-", "/deterministic", "/deterministic+", "/deterministic-", "/parallel+", "/parallel-", "/publicsign+", "/publicsign-", "/warnaserror+", "/warnaserror-", "/debug", "/debug+", "/debug-", "/highentropyva+", "/highentropyva-" }.Contains(x);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CommandLineToArgvW")] private static extern nint NativeArgv(string commandLine, out int count);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint LocalFree(nint memory);
}
