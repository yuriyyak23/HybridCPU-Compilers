using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

/// <summary>
/// Projects pinned live MSBuild Csc task events into the existing Windows command projection.
/// This records task arguments only; it does not attest process argv, input reads, provenance,
/// or hardware qualification.
/// </summary>
public static class CscWindowsTaskEventProjector
{
    public const string InputSchema = "hybridcpu.hardwarebridge.msbuild-csc-task-events/v1";
    public const string OutputSchema = "hybridcpu.hardwarebridge.windows-task-command-projection/v1";
    private const int MaximumEvents = 32;
    private const int MaximumCommandCharacters = 262144;
    private const int MaximumTokens = 8194;
    private const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private sealed record ProjectedToken(int ordinal, string token, string kind);
    private sealed record WorkingDirectory(string status, string? value);
    private sealed record ProjectedCommand(
        string taskId,
        string projectFile,
        string taskStartUtc,
        string taskFinishUtc,
        string compilerToken,
        string compilerTokenSha256,
        string commandLineSha256,
        string argvSha256,
        ProjectedToken[] argv,
        WorkingDirectory workingDirectory,
        string executionClaim);
    private sealed record Claims(string Execution, string Provenance, string Admission);
    private sealed record Projection(
        string schema,
        string disposition,
        string inputEventsSha256,
        string parser,
        string argvMeaning,
        ProjectedCommand[] commands,
        Claims claims);

    private sealed record Summary(string status, int events, string projectionSha256,
        string execution, string provenance, string admission);

    public static byte[] ProjectBytes(ReadOnlySpan<byte> rawEvents, string expectedEventsSha256)
    {
        if (!IsSha256(expectedEventsSha256))
            throw new InvalidDataException("External task-event SHA-256 pin is required.");
        if (rawEvents.Length > MaximumBytes)
            throw new InvalidDataException("Task-event envelope exceeds size bound.");
        string digest = Hash(rawEvents);
        if (!FixedHex(digest, expectedEventsSha256))
            throw new InvalidDataException("Task-event bytes differ from the external SHA-256 pin.");

        byte[] jsonBytes = rawEvents.ToArray();
        if (jsonBytes.Length >= 3 && jsonBytes[0] == 0xEF && jsonBytes[1] == 0xBB && jsonBytes[2] == 0xBF)
            jsonBytes = jsonBytes[3..];
        using JsonDocument document = JsonDocument.Parse(jsonBytes, new JsonDocumentOptions
        {
            MaxDepth = 64,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });
        RejectDuplicateProperties(document.RootElement);
        JsonElement root = document.RootElement;
        RequireObjectFields(root, "schema", "disposition", "error", "events");
        if (String(root, "schema") != InputSchema ||
            String(root, "disposition") != "unqualified-software-observation" ||
            root.GetProperty("error").ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("Unsupported or failed task-event observation.");

        JsonElement eventRows = root.GetProperty("events");
        if (eventRows.ValueKind != JsonValueKind.Array || eventRows.GetArrayLength() is < 1 or > MaximumEvents)
            throw new InvalidDataException("Missing or excessive Csc task events.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var commands = new List<ProjectedCommand>(eventRows.GetArrayLength());
        int totalCommandCharacters = 0;
        foreach (JsonElement row in eventRows.EnumerateArray())
        {
            RequireObjectFields(row, "Id", "ProjectFile", "StartUtc", "FinishUtc", "CommandLine", "Succeeded", "CommandEvents");
            string id = String(row, "Id");
            string project = String(row, "ProjectFile");
            string started = String(row, "StartUtc");
            string finished = String(row, "FinishUtc");
            string commandLine = String(row, "CommandLine");
            JsonElement success = row.GetProperty("Succeeded");
            JsonElement count = row.GetProperty("CommandEvents");
            if (id.Length == 0 || !seen.Add(id) || !Path.IsPathFullyQualified(project) ||
                commandLine.Length == 0 || success.ValueKind != JsonValueKind.True ||
                count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out int commandCount) || commandCount != 1)
                throw new InvalidDataException("Duplicate identity or incomplete/failed Csc event.");
            totalCommandCharacters = checked(totalCommandCharacters + ScalarCount(commandLine));
            if (ScalarCount(commandLine) > MaximumCommandCharacters || totalCommandCharacters > MaximumBytes)
                throw new InvalidDataException("Csc command text exceeds bound.");

            string[] allArguments = ParseWindowsCommandLine(commandLine);
            string compiler = allArguments[0];
            if (!Path.IsPathFullyQualified(compiler) ||
                !Path.GetFileName(compiler).Equals("csc.exe", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(compiler).Equals("csc.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Task command does not start with an absolute Roslyn Csc executable.");
            if (allArguments.Any(value => value.IndexOfAny(['\0', '\r', '\n']) >= 0))
                throw new InvalidDataException("Control character in parsed command.");

            string[] arguments = allArguments[1..];
            var projectedTokens = new ProjectedToken[arguments.Length];
            for (int i = 0; i < arguments.Length; i++)
                projectedTokens[i] = new(i, arguments[i], Classify(arguments[i]));
            byte[] argvBytes = JsonSerializer.SerializeToUtf8Bytes(allArguments, JsonOptions);
            commands.Add(new(
                id,
                project,
                started,
                finished,
                compiler,
                Hash(StrictUtf8.GetBytes(compiler)),
                Hash(StrictUtf8.GetBytes(commandLine)),
                Hash(argvBytes),
                projectedTokens,
                new("NotRecorded", null),
                "TaskEventOnly"));
        }

        var output = new Projection(
            OutputSchema,
            "unqualified-software-observation",
            digest,
            "Windows shell32 CommandLineToArgvW",
            "Projection of MSBuild TaskCommandLineEventArgs; not OS process argv.",
            commands.ToArray(),
            new("NotEstablished", "NotEstablished", "NotAdmitted"));
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(output, JsonOptions);
        if (body.Length + 1 > MaximumBytes)
            throw new InvalidDataException("Projected command inventory exceeds bound.");
        byte[] result = new byte[body.Length + 1];
        body.CopyTo(result, 0);
        result[^1] = (byte)'\n';
        return result;
    }

    public static string ProjectFile(string eventPath, string expectedEventsSha256, string outputPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidDataException("Windows command-line projection requires Windows.");
        string input = ValidateExistingInput(eventPath);
        string output = Path.GetFullPath(outputPath);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Input and output paths must differ.");
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string parent = Path.GetDirectoryName(output) ?? throw new InvalidDataException("Output parent is missing.");
        if (!IsUnder(parent, temp) || !Directory.Exists(parent))
            throw new InvalidDataException("Output must be a new file below an existing TEMP directory.");
        RejectReparseAncestors(parent, temp);
        if (File.Exists(output) || Directory.Exists(output))
            throw new InvalidDataException("Output path already exists.");

        byte[] raw;
        using (var stream = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length > MaximumBytes) throw new InvalidDataException("Task-event envelope exceeds size bound.");
            raw = new byte[checked((int)stream.Length)];
            stream.ReadExactly(raw);
        }
        byte[] projection = ProjectBytes(raw, expectedEventsSha256);
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(projection);
            stream.Flush(flushToDisk: true);
        }
        return JsonSerializer.Serialize(new Summary("Projected",
            JsonDocument.Parse(projection).RootElement.GetProperty("commands").GetArrayLength(),
            Hash(projection), "NotEstablished", "NotEstablished", "NotAdmitted"), JsonOptions);
    }

    public static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new InvalidDataException("Windows command-line projection requires Windows.");
            if (args.Length != 6 || args[0] != "--events" || args[2] != "--events-sha256" || args[4] != "--output")
                throw new InvalidDataException("Usage: --events <path> --events-sha256 <sha256> --output <new-temp-file>");
            Console.WriteLine(ProjectFile(args[1], args[3], args[5]));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or JsonException or FormatException or OverflowException or
            DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Console.Error.WriteLine("REFUSED: " + ex.Message);
            return 2;
        }
    }

    private static string[] ParseWindowsCommandLine(string commandLine)
    {
        int count;
        nint allocation = CommandLineToArgvW(commandLine, out count);
        if (allocation == 0) throw new InvalidDataException("CommandLineToArgvW failed: " + Marshal.GetLastWin32Error());
        try
        {
            if (count is < 1 or > MaximumTokens)
                throw new InvalidDataException("Empty or oversized Windows argv.");
            var result = new string[count];
            for (int i = 0; i < count; i++)
            {
                nint value = Marshal.ReadIntPtr(allocation, checked(i * IntPtr.Size));
                result[i] = Marshal.PtrToStringUni(value) ?? throw new InvalidDataException("Windows argv contains a null token.");
            }
            return result;
        }
        finally { _ = LocalFree(allocation); }
    }

    private static string Classify(string token)
    {
        if (token.StartsWith('@'))
            throw new InvalidDataException("Response-file bytes were not captured by this projector.");
        string lower = token.ToLowerInvariant();
        foreach ((string prefix, string kind) in new[]
        {
            ("/reference:", "reference"), ("/r:", "reference"),
            ("/analyzer:", "analyzer"), ("/analyzerconfig:", "analyzer-config"),
            ("/additionalfile:", "additional-file"), ("/sourcelink:", "source-link"),
            ("/appconfig:", "app-config"), ("/embed:", "embedded"),
            ("/out:", "pe-output"), ("/pdb:", "pdb-output"),
            ("/refout:", "refout-output"), ("/generatedfilesout:", "generated-output-dir")
        })
            if (lower.StartsWith(prefix, StringComparison.Ordinal)) return kind;
        if (!token.StartsWith('/') && !token.StartsWith('-') && lower.EndsWith(".cs", StringComparison.Ordinal))
            return "source";
        return "switch-or-value";
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON member: " + property.Name);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static void RequireObjectFields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a JSON object.");
        var expected = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!expected.Remove(property.Name)) throw new InvalidDataException("Unexpected JSON member: " + property.Name);
        if (expected.Count != 0) throw new InvalidDataException("Missing JSON member: " + expected.First());
    }

    private static string String(JsonElement element, string property)
    {
        JsonElement value = element.GetProperty(property);
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Expected string field: " + property);
        return value.GetString() ?? throw new InvalidDataException("Null string field: " + property);
    }

    private static string ValidateExistingInput(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Task-event input path must be absolute.");
        string full = Path.GetFullPath(path);
        if (!File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Task-event input must be an existing ordinary file.");
        string root = Path.GetPathRoot(full) ?? throw new InvalidDataException("Task-event input root is missing.");
        RejectReparseAncestors(Path.GetDirectoryName(full)!, root);
        return full;
    }

    private static void RejectReparseAncestors(string path, string stopAt)
    {
        string stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopAt));
        string current = Path.GetFullPath(path);
        if (!IsUnder(current, stop) && !string.Equals(current, stop, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Path is outside its approved root.");
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked path component is not permitted.");
            if (string.Equals(Path.TrimEndingDirectorySeparator(current), stop, StringComparison.OrdinalIgnoreCase)) break;
            current = Directory.GetParent(current)?.FullName ?? throw new InvalidDataException("Path root traversal failed.");
        }
    }

    private static bool IsUnder(string path, string root)
    {
        string fullPath = Path.GetFullPath(path);
        string fullRoot = Path.GetFullPath(root);
        if (string.Equals(Path.TrimEndingDirectorySeparator(fullPath),
                Path.TrimEndingDirectorySeparator(fullRoot), StringComparison.OrdinalIgnoreCase)) return true;
        if (!Path.EndsInDirectorySeparator(fullRoot)) fullRoot += Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static int ScalarCount(string value) => value.EnumerateRunes().Count();
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool FixedHex(string observed, string expected) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(observed), Convert.FromHexString(expected));

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint memory);
}
