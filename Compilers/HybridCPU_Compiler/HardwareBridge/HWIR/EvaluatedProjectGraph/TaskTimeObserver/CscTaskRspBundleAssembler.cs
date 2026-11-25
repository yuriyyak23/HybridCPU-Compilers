using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// C# assembler for the existing Windows task/process/response observation schema.
/// A copied response file is never reported as consumed by Csc.
/// </summary>
internal static class CscTaskRspBundleAssembler
{
    private const string OutputSchema = "hybridcpu.hardwarebridge.windows-csc-task-rsp-bundle/v1";
    private const string TaskSchema = "hybridcpu.hardwarebridge.msbuild-csc-task-events/v1";
    private const string ProcessSchema = "hybridcpu.windows-csc-process-poll/v1";
    private const string CrosswalkSchema = "hybridcpu.csc-task-response-argument-crosswalk/v1";
    private const string ReceiptSchema = "hybridcpu.golden-windows-csc-process-and-response-capture/v1";
    private const int ExpectedProcesses = 5;
    private const int MaxJsonBytes = 16 * 1024 * 1024;
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxTotalResponseBytes = 8 * 1024 * 1024;
    private const int MaxCommandChars = 262144;
    private const int MaxTokens = 8194;
    private const int MaxTotalTokens = 40000;
    private static readonly Regex ShaPattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex OidPattern = new("^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant);

    private sealed record PinnedJson(byte[] Raw, JsonElement Root, string Sha);
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 15 || args[0] != "assemble")
                throw new InvalidDataException("Usage: assemble <events> <events-sha> <process> <process-sha> <crosswalk> <crosswalk-sha> <receipt> <receipt-sha> <response-dir> <golden-commit> <tracked-tree> <compiler> <compiler-sha> <new-output>");
            byte[] output = Assemble(args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8],
                args[9], args[10], args[11], args[12], args[13]);
            string destination = args[14];
            if (!Path.IsPathFullyQualified(destination)) throw new InvalidDataException("Absolute output path required.");
            string full = Path.GetFullPath(destination);
            string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!SamePath(Path.GetDirectoryName(full) ?? "", temp) || File.Exists(full) || Directory.Exists(full))
                throw new InvalidDataException("Output must be a new direct TEMP file.");
            SafePath(temp, directory: true);
            using (FileStream stream = new(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(output);
                stream.Flush(flushToDisk: true);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { status = "ProjectedUnqualified", commands = ExpectedProcesses,
                outputSha256 = Hash(output), responseBytesConsumed = "NotEstablished", provenance = "NotAdmitted",
                independentCoreAdmission = "NotAdmitted" }));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
                                      FormatException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine("REFUSED: " + ex);
            return 2;
        }
    }

    private static byte[] Assemble(string eventsPath, string eventsSha, string processPath, string processSha,
        string crosswalkPath, string crosswalkSha, string receiptPath, string receiptSha, string responseRoot,
        string expectedCommit, string expectedTree, string compilerPath, string compilerSha)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidDataException("Windows task/RSP assembly requires Windows.");
        RequireOid(expectedCommit, "Golden commit");
        RequireOid(expectedTree, "tracked tree");
        RequireSha(compilerSha, "compiler");
        string compiler = SafePath(compilerPath, directory: false);
        if (new FileInfo(compiler).Length > 16 * 1024 * 1024 || HashFile(compiler) != compilerSha.ToLowerInvariant())
            throw new InvalidDataException("Compiler bytes differ from external compiler SHA-256 pin.");

        PinnedJson eventsInput = ReadPinnedJson(eventsPath, eventsSha, "task events");
        PinnedJson processInput = ReadPinnedJson(processPath, processSha, "process observation");
        PinnedJson crosswalkInput = ReadPinnedJson(crosswalkPath, crosswalkSha, "response/task crosswalk");
        PinnedJson receiptInput = ReadPinnedJson(receiptPath, receiptSha, "capture receipt");
        JsonElement events = eventsInput.Root, process = processInput.Root, crosswalk = crosswalkInput.Root, receipt = receiptInput.Root;

        ExactKeys(events, "schema", "disposition", "error", "events");
        if (Str(events, "schema") != TaskSchema || Str(events, "disposition") != "unqualified-software-observation" ||
            events.GetProperty("error").ValueKind != JsonValueKind.Null || Array(events, "events").GetArrayLength() != ExpectedProcesses)
            throw new InvalidDataException("Task event envelope is failed, unsupported or not exactly five events.");
        ExactKeys(process, "schema", "pinnedHead", "trackedTreeBefore", "workingTreeBefore", "buildExit", "projectRoot", "sdk",
            "pollIntervalMilliseconds", "observedCscProcesses", "observedCscCount", "taskEventsCaptured", "trackedTreeAfter",
            "headAfter", "workingTreeAfter", "processObservation");
        if (Str(process, "schema") != ProcessSchema || Int(process, "buildExit") != 0 || Str(process, "pinnedHead") != expectedCommit ||
            Str(process, "headAfter") != expectedCommit || Str(process, "trackedTreeBefore") != expectedTree ||
            Str(process, "trackedTreeAfter") != expectedTree || !Null(process, "workingTreeBefore") || !Null(process, "workingTreeAfter") ||
            Bool(process, "taskEventsCaptured") != true || Int(process, "observedCscCount") != ExpectedProcesses ||
            Array(process, "observedCscProcesses").GetArrayLength() != ExpectedProcesses)
            throw new InvalidDataException("Process observation does not match successful pinned five-process capture.");
        ExactKeys(receipt, "schema", "status", "pinnedGoldenCommit", "trackedTreeBefore", "trackedTreeAfter", "workingTreeAfter",
            "sdk", "buildExit", "taskEventFileSha256", "taskEventCount", "successfulTaskEventCount", "processObservationMethod",
            "processCount", "processes", "claims", "limits", "responseTaskCrosswalk", "captureFiles");
        if (Str(receipt, "schema") != ReceiptSchema || Str(receipt, "status") != "ObservedLocalUnqualified" ||
            Str(receipt, "pinnedGoldenCommit") != expectedCommit || Str(receipt, "trackedTreeBefore") != expectedTree ||
            Str(receipt, "trackedTreeAfter") != expectedTree || !Null(receipt, "workingTreeAfter") || Int(receipt, "buildExit") != 0 ||
            Int(receipt, "taskEventCount") != ExpectedProcesses || Int(receipt, "successfulTaskEventCount") != ExpectedProcesses ||
            Int(receipt, "processCount") != ExpectedProcesses || Array(receipt, "processes").GetArrayLength() != ExpectedProcesses)
            throw new InvalidDataException("Receipt is failed, unsupported or not bound to the pinned five-process run.");

        JsonElement claims = Obj(receipt, "claims");
        if (!True(claims, "ActualCscProcessObserved") || !True(claims, "ActualResponseFileBytesCaptured") ||
            !True(claims, "ResponseTokensCrosswalkedToTaskEvent") || !True(claims, "TaskExecutionSucceeded") ||
            !False(claims, "CscFileReadBuffersObserved") || !False(claims, "IndependentCoreAdmission") || !False(claims, "TrustedHandoff"))
            throw new InvalidDataException("Receipt claims exceed the supplied observation boundary.");
        if (Str(receipt, "taskEventFileSha256") != eventsInput.Sha) throw new InvalidDataException("Receipt task-event pin is stale.");

        ExactKeys(crosswalk, "schema", "parser", "taskEventsSha256", "processObservationSha256", "crosswalkCount",
            "allOrderedArgumentsEqual", "crosswalks", "limitations");
        if (Str(crosswalk, "schema") != CrosswalkSchema || Str(crosswalk, "taskEventsSha256") != eventsInput.Sha ||
            Str(crosswalk, "processObservationSha256") != processInput.Sha || Int(crosswalk, "crosswalkCount") != ExpectedProcesses ||
            !True(crosswalk, "allOrderedArgumentsEqual") || Array(crosswalk, "crosswalks").GetArrayLength() != ExpectedProcesses)
            throw new InvalidDataException("Crosswalk is stale, incomplete or not an exact ordered match.");
        JsonElement receiptCross = Obj(receipt, "responseTaskCrosswalk");
        if (Str(receiptCross, "sha256") != crosswalkInput.Sha || Int(receiptCross, "crosswalkCount") != ExpectedProcesses ||
            !True(receiptCross, "allOrderedArgumentsEqual")) throw new InvalidDataException("Receipt does not bind crosswalk bytes.");

        var eventById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonElement row in Array(events, "events").EnumerateArray())
        {
            ExactKeys(row, "Id", "ProjectFile", "StartUtc", "FinishUtc", "CommandLine", "Succeeded", "CommandEvents");
            string id = Str(row, "Id");
            if (string.IsNullOrWhiteSpace(id) || !Path.IsPathFullyQualified(Str(row, "ProjectFile")) ||
                !Bool(row, "Succeeded") || Int(row, "CommandEvents") != 1 || Str(row, "CommandLine").Length is < 1 or > MaxCommandChars ||
                !eventById.TryAdd(id, row)) throw new InvalidDataException("Duplicate, failed or incomplete task event.");
        }

        var processByPid = new Dictionary<int, JsonElement>();
        foreach (JsonElement row in Array(process, "observedCscProcesses").EnumerateArray())
        {
            ExactKeys(row, "observedUtc", "pid", "parentPid", "processName", "executablePath", "commandLine", "responseFilePath",
                "responseCopy", "responseBytes", "responseSha256", "responseCaptureError");
            int pid = Int(row, "pid");
            if (pid <= 0 || Str(row, "processName").ToLowerInvariant() != "csc.exe" || !Null(row, "responseCaptureError") ||
                !processByPid.TryAdd(pid, row)) throw new InvalidDataException("Duplicate PID or invalid sampled Csc process.");
        }
        var receiptByPid = new Dictionary<int, JsonElement>();
        foreach (JsonElement row in Array(receipt, "processes").EnumerateArray())
        {
            ExactKeys(row, "pid", "parentPid", "processName", "executablePath", "executableSha256", "commandLine", "responsePath",
                "responseCopy", "responseLength", "responseSha256");
            int pid = Int(row, "pid");
            if (pid <= 0 || Str(row, "executableSha256").ToLowerInvariant() != compilerSha.ToLowerInvariant() ||
                !SamePath(Str(row, "executablePath"), compiler) || !receiptByPid.TryAdd(pid, row))
                throw new InvalidDataException("Duplicate PID or mismatched compiler receipt.");
        }
        if (!processByPid.Keys.ToHashSet().SetEquals(receiptByPid.Keys)) throw new InvalidDataException("Sampled/receipt PID sets differ.");

        var crossByPid = new Dictionary<int, JsonElement>();
        var crossByEvent = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in Array(crosswalk, "crosswalks").EnumerateArray())
        {
            ExactKeys(row, "pid", "responseSha256", "outputToken", "taskEventId", "projectFile", "taskSucceeded",
                "taskArgumentCount", "responseArgumentCount", "orderedArgumentsEqual", "differences");
            int pid = Int(row, "pid"); string eventId = Str(row, "taskEventId");
            if (!eventById.ContainsKey(eventId) || !True(row, "taskSucceeded") || !True(row, "orderedArgumentsEqual") ||
                Array(row, "differences").GetArrayLength() != 0 || Int(row, "taskArgumentCount") != Int(row, "responseArgumentCount") ||
                !crossByPid.TryAdd(pid, row) || !crossByEvent.Add(eventId))
                throw new InvalidDataException("Crosswalk has duplicate, missing, failed or unequal task/process relation.");
        }
        if (!crossByPid.Keys.ToHashSet().SetEquals(processByPid.Keys) || !crossByEvent.SetEquals(eventById.Keys))
            throw new InvalidDataException("Task/process/crosswalk identities are not exactly five by five by five.");

        string safeResponseRoot = SafePath(responseRoot, directory: true);
        var responseFiles = Directory.EnumerateFileSystemEntries(safeResponseRoot).ToArray();
        var expectedNames = processByPid.Keys.Select(pid => $"csc-{pid}.rsp").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (responseFiles.Length != ExpectedProcesses || !responseFiles.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedNames))
            throw new InvalidDataException("Response directory has missing or extra response copies.");
        foreach (string file in responseFiles) SafePath(file, directory: false, MaxResponseBytes);

        int totalResponseBytes = 0, totalTokens = 0;
        var commandRows = new List<JsonObject>();
        var seenResponsePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (int pid in processByPid.Keys.Order())
        {
            JsonElement sampled = processByPid[pid], receiptRow = receiptByPid[pid], cross = crossByPid[pid];
            string executable = Str(sampled, "executablePath"), responsePath = Str(sampled, "responseFilePath");
            if (Int(sampled, "parentPid") != Int(receiptRow, "parentPid") || Str(sampled, "processName") != Str(receiptRow, "processName") ||
                !SamePath(executable, Str(receiptRow, "executablePath")) || Str(sampled, "commandLine") != Str(receiptRow, "commandLine") ||
                !SamePath(responsePath, Str(receiptRow, "responsePath")) || Path.GetFileName(Str(sampled, "responseCopy")) != Str(receiptRow, "responseCopy") ||
                Int(sampled, "responseBytes") != Int(receiptRow, "responseLength") ||
                Str(sampled, "responseSha256").ToLowerInvariant() != Str(receiptRow, "responseSha256").ToLowerInvariant() ||
                Str(cross, "responseSha256").ToLowerInvariant() != Str(receiptRow, "responseSha256").ToLowerInvariant())
                throw new InvalidDataException($"Sampled process, receipt and crosswalk disagree for PID {pid}.");
            string eventId = Str(cross, "taskEventId"); JsonElement taskEvent = eventById[eventId];
            if (!SamePath(Str(taskEvent, "ProjectFile"), Str(cross, "projectFile"))) throw new InvalidDataException("Task project differs from crosswalk.");
            DateTimeOffset start = Timestamp(Str(taskEvent, "StartUtc")), finish = Timestamp(Str(taskEvent, "FinishUtc")), observed = Timestamp(Str(sampled, "observedUtc"));
            if (finish < start || observed < start.AddSeconds(-2) || observed > finish.AddSeconds(2))
                throw new InvalidDataException("Sampled process is outside the matching Csc task interval.");

            string[] osArgv = CommandLineToArgvW(Str(sampled, "commandLine"));
            string[] taskArgv = CommandLineToArgvW(Str(taskEvent, "CommandLine"));
            if (osArgv.Length != 3 || !osArgv[1].Equals("/noconfig", StringComparison.OrdinalIgnoreCase) || !osArgv[2].StartsWith('@') ||
                !Path.GetFileName(osArgv[0]).Equals("csc.exe", StringComparison.OrdinalIgnoreCase) || !SamePath(osArgv[0], executable) ||
                !seenResponsePaths.Add(NormalizePath(osArgv[2][1..])))
                throw new InvalidDataException("Sampled Csc command is not exactly executable /noconfig @response.");
            if (!SamePath(osArgv[2][1..], responsePath)) throw new InvalidDataException("Sampled process response path differs from command line.");
            if (taskArgv.Length < 3 || !Path.GetFileName(taskArgv[0]).Equals("csc.exe", StringComparison.OrdinalIgnoreCase) ||
                !SamePath(taskArgv[0], executable) || !taskArgv[1].Equals("/noconfig", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Task event command does not use the observed pinned compiler.");
            string[] taskArgs = taskArgv.Skip(2).ToArray();
            string responseName = $"csc-{pid}.rsp";
            if (Str(receiptRow, "responseCopy") != responseName || Path.GetFileName(Str(sampled, "responseCopy")) != responseName)
                throw new InvalidDataException("Response copy name is not uniquely bound to PID.");
            string sampledCopy = SafePath(Str(sampled, "responseCopy"), directory: false, MaxResponseBytes);
            if (!SamePath(Path.GetDirectoryName(sampledCopy)!, safeResponseRoot) || Path.GetFileName(sampledCopy) != responseName)
                throw new InvalidDataException("Sampled response copy escapes response root.");
            byte[] responseBytes = ReadBounded(sampledCopy, MaxResponseBytes);
            if (responseBytes.Length != Int(receiptRow, "responseLength") || Hash(responseBytes) != Str(receiptRow, "responseSha256").ToLowerInvariant())
                throw new InvalidDataException("Response copy length/SHA-256 differs from process receipt.");
            string responseText;
            try { responseText = new UTF8Encoding(false, true).GetString(responseBytes); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Response copy is not valid UTF-8.", ex); }
            if (responseText.Length > 0 && responseText[0] == '\uFEFF') responseText = responseText[1..];
            string[] responseArgs = CommandLineToArgvW(responseText);
            if (responseArgs.Any(x => x.StartsWith('@'))) throw new InvalidDataException("Nested response-file token is unsupported.");
            totalResponseBytes += responseBytes.Length; totalTokens += responseArgs.Length;
            if (totalResponseBytes > MaxTotalResponseBytes || totalTokens > MaxTotalTokens) throw new InvalidDataException("Response/token budget exceeded.");
            if (!responseArgs.SequenceEqual(taskArgs, StringComparer.Ordinal) || responseArgs.Length != Int(cross, "responseArgumentCount") ||
                taskArgs.Length != Int(cross, "taskArgumentCount")) throw new InvalidDataException("Captured response tokens differ in order from task command.");
            string[] outputs = responseArgs.Where(x => x.StartsWith("/out:", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (outputs.Length != 1 || outputs[0] != Str(cross, "outputToken")) throw new InvalidDataException("Crosswalk output token differs from response arguments.");

            commandRows.Add(new JsonObject
            {
                ["pid"] = pid, ["taskEventId"] = eventId, ["projectFile"] = Str(taskEvent, "ProjectFile"),
                ["processObservedUtc"] = Str(sampled, "observedUtc"), ["compilerPath"] = executable,
                ["compilerSha256"] = compilerSha.ToLowerInvariant(), ["osCommandLineSha256"] = Hash(Encoding.UTF8.GetBytes(Str(sampled, "commandLine"))),
                ["taskCommandLineSha256"] = Hash(Encoding.UTF8.GetBytes(Str(taskEvent, "CommandLine"))),
                ["responsePath"] = responsePath, ["responseCopy"] = responseName, ["responseBytes"] = responseBytes.Length,
                ["responseSha256"] = Hash(responseBytes), ["argvSha256"] = Hash(Canonical(new JsonArray(responseArgs.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()))),
                ["argv"] = new JsonArray(responseArgs.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["workingDirectory"] = new JsonObject { ["status"] = "NotRecorded", ["value"] = null },
                ["executionClaim"] = "ObservedProcessWithResponseCopyNotReadProof"
            });
        }

        JsonObject result = new()
        {
            ["schema"] = OutputSchema, ["disposition"] = "unqualified-software-observation",
            ["goldenCommit"] = expectedCommit.ToLowerInvariant(), ["trackedTree"] = expectedTree.ToLowerInvariant(),
            ["taskEventsSha256"] = eventsInput.Sha, ["processObservationSha256"] = processInput.Sha,
            ["crosswalkSha256"] = crosswalkInput.Sha, ["captureReceiptSha256"] = receiptInput.Sha,
            ["compilerPath"] = compiler, ["compilerSha256"] = compilerSha.ToLowerInvariant(),
            ["parser"] = "Win32 CommandLineToArgvW",
            ["argumentRelation"] = "captured response tokens compared with MSBuild task command tokens; not Roslyn parser/read attestation",
            ["commands"] = new JsonArray(commandRows.Select(x => (JsonNode?)x).ToArray()),
            ["claims"] = new JsonObject { ["CscProcessObserved"] = true, ["ResponseBytesCopiedAndHashed"] = true,
                ["ResponseBytesConsumed"] = "NotEstablished", ["WorkingDirectory"] = "NotRecorded",
                ["InputByteClosure"] = "NotEstablished", ["Provenance"] = "NotAdmitted",
                ["IndependentCoreAdmission"] = "NotAdmitted", ["HardwareVerification"] = "NotRun" }
        };
        byte[] encoded = Canonical(result).Concat([(byte)'\n']).ToArray();
        if (encoded.Length > MaxJsonBytes) throw new InvalidDataException("Projected bundle exceeds output budget.");
        return encoded;
    }

    private static string[] CommandLineToArgvW(string command)
    {
        if (string.IsNullOrEmpty(command) || command.Length > MaxCommandChars || command.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new InvalidDataException("Empty, oversized or controlled Windows command line.");
        IntPtr argv = NativeCommandLineToArgvW(command, out int argc);
        if (argv == IntPtr.Zero) throw new InvalidDataException("CommandLineToArgvW failed.");
        try
        {
            if (argc < 1 || argc > MaxTokens) throw new InvalidDataException("Empty or oversized Windows argv.");
            var values = new string[argc];
            for (int i = 0; i < argc; i++)
            {
                IntPtr pointer = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                values[i] = Marshal.PtrToStringUni(pointer) ?? throw new InvalidDataException("Invalid Windows argv token.");
                if (values[i].Length == 0 || values[i].IndexOfAny(['\0', '\r', '\n']) >= 0)
                    throw new InvalidDataException("Empty token or control character in Windows argv.");
            }
            return values;
        }
        finally { _ = LocalFree(argv); }
    }
    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr NativeCommandLineToArgvW(string commandLine, out int argc);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr LocalFree(IntPtr memory);

    private static PinnedJson ReadPinnedJson(string path, string expectedSha, string label)
    {
        RequireSha(expectedSha, label);
        string safe = SafePath(path, directory: false, MaxJsonBytes);
        byte[] raw = ReadBounded(safe, MaxJsonBytes);
        string actual = Hash(raw);
        if (actual != expectedSha.ToLowerInvariant()) throw new InvalidDataException(label + " bytes differ from external SHA-256 pin.");
        JsonDocument doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 64, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        try { CheckDuplicateProperties(doc.RootElement); return new(raw, doc.RootElement.Clone(), actual); }
        finally { doc.Dispose(); }
    }
    private static void CheckDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property: " + property.Name); CheckDuplicateProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (JsonElement item in value.EnumerateArray()) CheckDuplicateProperties(item);
    }
    private static byte[] ReadBounded(string path, int max)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > max) throw new InvalidDataException("Oversized bounded input.");
        using MemoryStream memory = new(); stream.CopyTo(memory);
        if (memory.Length > max) throw new InvalidDataException("Oversized bounded input.");
        return memory.ToArray();
    }
    private static string SafePath(string path, bool directory, long? maxBytes = null)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0') || !Path.GetFullPath(path).Equals(path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Absolute canonical input path required.");
        string? root = Path.GetPathRoot(path); if (root is null) throw new InvalidDataException("Input has no volume root.");
        string current = root;
        foreach (string part in path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')) throw new InvalidDataException("Ambiguous/traversing input path.");
            current = Path.Combine(current, part);
            FileAttributes attrs = File.GetAttributes(current);
            if ((attrs & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked/reparse input path refused.");
        }
        FileAttributes final = File.GetAttributes(path);
        if (directory != ((final & FileAttributes.Directory) != 0)) throw new InvalidDataException("Unexpected input path type.");
        if (!directory && maxBytes is long max && new FileInfo(path).Length > max) throw new InvalidDataException("Input exceeds byte limit.");
        return path;
    }

    private static byte[] Canonical(JsonNode node)
    {
        using JsonDocument document = JsonDocument.Parse(node.ToJsonString());
        using MemoryStream stream = new(); WriteCanonical(stream, document.RootElement); return stream.ToArray();
    }
    private static void WriteCanonical(Stream stream, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                stream.WriteByte((byte)'{'); bool first = true;
                foreach (JsonProperty item in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { if (!first) stream.WriteByte((byte)','); first = false; WriteString(stream, item.Name); stream.WriteByte((byte)':'); WriteCanonical(stream, item.Value); }
                stream.WriteByte((byte)'}'); break;
            case JsonValueKind.Array:
                stream.WriteByte((byte)'['); for (int i = 0; i < value.GetArrayLength(); i++) { if (i > 0) stream.WriteByte((byte)','); WriteCanonical(stream, value[i]); }
                stream.WriteByte((byte)']'); break;
            case JsonValueKind.String: WriteString(stream, value.GetString()!); break;
            case JsonValueKind.Number: stream.Write(Encoding.ASCII.GetBytes(value.GetRawText())); break;
            case JsonValueKind.True: stream.Write("true"u8); break;
            case JsonValueKind.False: stream.Write("false"u8); break;
            case JsonValueKind.Null: stream.Write("null"u8); break;
            default: throw new InvalidDataException("Unsupported JSON value.");
        }
    }
    private static void WriteString(Stream stream, string value)
    {
        stream.WriteByte((byte)'"');
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (!rune.IsAscii) { stream.Write(Encoding.UTF8.GetBytes(rune.ToString())); continue; }
            char c = (char)rune.Value;
            switch (c)
            {
                case '"': stream.Write("\\\""u8); break;
                case '\\': stream.Write("\\\\"u8); break;
                case '\b': stream.Write("\\b"u8); break; case '\f': stream.Write("\\f"u8); break;
                case '\n': stream.Write("\\n"u8); break; case '\r': stream.Write("\\r"u8); break; case '\t': stream.Write("\\t"u8); break;
                default:
                    if (c < 0x20) stream.Write(Encoding.ASCII.GetBytes("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture)));
                    else stream.WriteByte((byte)c);
                    break;
            }
        }
        stream.WriteByte((byte)'"');
    }
    private static string HashFile(string path) { using FileStream stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string NormalizePath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
    private static bool SamePath(string a, string b) => NormalizePath(a) == NormalizePath(b);
    private static void RequireSha(string value, string label) { if (!ShaPattern.IsMatch(value)) throw new InvalidDataException("External SHA-256 pin required for " + label + "."); }
    private static void RequireOid(string value, string label) { if (!OidPattern.IsMatch(value)) throw new InvalidDataException("Pinned commit/tree identity required for " + label + "."); }
    private static void ExactKeys(JsonElement value, params string[] keys)
    { if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(keys)) throw new InvalidDataException("Input fields differ from versioned contract."); }
    private static JsonElement Obj(JsonElement parent, string name) { JsonElement e = parent.GetProperty(name); if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException(name + " must be object."); return e; }
    private static JsonElement Array(JsonElement parent, string name) { JsonElement e = parent.GetProperty(name); if (e.ValueKind != JsonValueKind.Array) throw new InvalidDataException(name + " must be array."); return e; }
    private static string Str(JsonElement parent, string name) { JsonElement e = parent.GetProperty(name); return e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new InvalidDataException(name + " must be string."); }
    private static int Int(JsonElement parent, string name) { JsonElement e = parent.GetProperty(name); return e.TryGetInt32(out int n) ? n : throw new InvalidDataException(name + " must be integer."); }
    private static bool Bool(JsonElement parent, string name) => parent.GetProperty(name).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new InvalidDataException(name + " must be boolean.") };
    private static bool Null(JsonElement parent, string name) => parent.GetProperty(name).ValueKind == JsonValueKind.Null;
    private static bool True(JsonElement parent, string name) => parent.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.True;
    private static bool False(JsonElement parent, string name) => parent.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.False;
    private static DateTimeOffset Timestamp(string value) => Regex.IsMatch(value, "(?:Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset t)
        ? t : throw new InvalidDataException("Invalid offset-aware timestamp.");
}
