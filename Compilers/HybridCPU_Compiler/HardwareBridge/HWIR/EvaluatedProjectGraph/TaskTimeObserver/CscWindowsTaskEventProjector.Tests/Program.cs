using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}

static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

static void Refuses(Action action, string message)
{
    try { action(); }
    catch (InvalidDataException) { Console.WriteLine("PASS " + message); return; }
    throw new InvalidOperationException("Expected refusal: " + message);
}

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("The Windows task command-line projection fixture requires Windows.");

const string projectSchema = "hybridcpu.hardwarebridge.msbuild-csc-task-events/v1";
const string disposition = "unqualified-software-observation";
const string projectionSchema = "hybridcpu.hardwarebridge.windows-task-command-projection/v1";
string root = Path.Combine(Path.GetTempPath(), "hb-csharp-task-projector-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string compiler = Path.Combine(root, "sdk home", "Roslyn", "bincore", "csc.dll");
    string project = Path.Combine(root, "sample project", "Sample.csproj");
    string source = Path.Combine(root, "source dir", "A file.cs");
    string reference = Path.Combine(root, "reference dir", "A.dll");
    string command = $"\"{compiler}\" /noconfig /reference:\"{reference}\" \"{source}\" /out:\"{Path.Combine(root, "obj", "out.dll")}\"";
    var eventObject = new
    {
        Id = "0:1:2",
        ProjectFile = project,
        StartUtc = "2026-11-24T12:00:00.0000000+00:00",
        FinishUtc = "2026-11-24T12:00:01.0000000+00:00",
        CommandLine = command,
        Succeeded = true,
        CommandEvents = 1
    };
    var input = new { schema = projectSchema, disposition, error = (string?)null, events = new[] { eventObject } };
    byte[] synthetic = JsonSerializer.SerializeToUtf8Bytes(input, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    string syntheticPin = Sha(synthetic);
    byte[] projected = CscWindowsTaskEventProjector.ProjectBytes(synthetic, syntheticPin);
    using (JsonDocument parsed = JsonDocument.Parse(projected))
    {
        JsonElement doc = parsed.RootElement;
        Check(doc.GetProperty("schema").GetString() == projectionSchema, "synthetic task events project to the existing V1 schema");
        Check(doc.GetProperty("claims").GetProperty("Execution").GetString() == "NotEstablished" &&
              doc.GetProperty("claims").GetProperty("Admission").GetString() == "NotAdmitted", "projection preserves unqualified claims");
        JsonElement commandProjection = doc.GetProperty("commands")[0];
        Check(commandProjection.GetProperty("workingDirectory").GetProperty("status").GetString() == "NotRecorded", "working directory remains explicitly unrecorded");
        JsonElement[] tokens = commandProjection.GetProperty("argv").EnumerateArray().ToArray();
        Check(tokens.Any(token => token.GetProperty("kind").GetString() == "reference") &&
              tokens.Any(token => token.GetProperty("kind").GetString() == "source"), "Windows argv quoting and input token roles are preserved");
    }

    Refuses(() => CscWindowsTaskEventProjector.ProjectBytes(synthetic, new string('0', 64)), "wrong external SHA-256 is refused");
    Refuses(() => CscWindowsTaskEventProjector.ProjectBytes(synthetic, ""), "missing external SHA-256 is refused");

    string utf8 = Encoding.UTF8.GetString(synthetic);
    byte[] duplicate = Encoding.UTF8.GetBytes(utf8.Replace("\"schema\":", "\"schema\":\"shadow\",\"schema\":", StringComparison.Ordinal));
    Refuses(() => CscWindowsTaskEventProjector.ProjectBytes(duplicate, Sha(duplicate)), "duplicate JSON properties are refused");
    byte[] unknown = Encoding.UTF8.GetBytes(utf8.Replace("\"error\":null", "\"unknown\":1,\"error\":null", StringComparison.Ordinal));
    Refuses(() => CscWindowsTaskEventProjector.ProjectBytes(unknown, Sha(unknown)), "unknown JSON members are refused");

    byte[] responseFile = JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = projectSchema,
        disposition,
        error = (string?)null,
        events = new[] { new { eventObject.Id, eventObject.ProjectFile, eventObject.StartUtc, eventObject.FinishUtc,
            CommandLine = $"\"{compiler}\" @\"{Path.Combine(root, "response file.rsp")}\"", eventObject.Succeeded, eventObject.CommandEvents } }
    }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    Refuses(() => CscWindowsTaskEventProjector.ProjectBytes(responseFile, Sha(responseFile)), "uncaptured response files are refused");

    string eventPath = Path.Combine(root, "events.json");
    string projectionPath = Path.Combine(root, "projection.json");
    File.WriteAllBytes(eventPath, synthetic);
    string summary = CscWindowsTaskEventProjector.ProjectFile(eventPath, syntheticPin, projectionPath);
    Check(File.ReadAllBytes(projectionPath).SequenceEqual(projected), "safe TEMP file projection equals the in-memory projection bytes");
    Check(summary.Contains("NotEstablished", StringComparison.Ordinal) && summary.Contains("NotAdmitted", StringComparison.Ordinal), "CLI summary does not upgrade execution or admission");
    Refuses(() => CscWindowsTaskEventProjector.ProjectFile(eventPath, syntheticPin, projectionPath), "existing output is not overwritten");

    if (args.Length == 4)
    {
        byte[] goldenEvents = File.ReadAllBytes(args[0]);
        string goldenPin = args[1];
        byte[] expectedProjection = File.ReadAllBytes(args[2]);
        Check(Sha(expectedProjection) == args[3], "frozen Windows Golden projection matches its external SHA-256 pin");
        byte[] actualProjection = CscWindowsTaskEventProjector.ProjectBytes(goldenEvents, goldenPin);
        Check(actualProjection.SequenceEqual(expectedProjection), "C# projection is byte-identical to saved Windows Golden projection");
        Console.WriteLine($"GOLDEN_PARITY inputBytes={goldenEvents.Length} outputBytes={actualProjection.Length} inputSha256={Sha(goldenEvents)} outputSha256={Sha(actualProjection)}");
    }
    else if (args.Length != 0)
    {
        throw new ArgumentException("Optional args: <Golden task-events.json> <events sha256> <saved projection.json> <projection sha256>.");
    }

    Console.WriteLine("CSC_WINDOWS_TASK_EVENT_PROJECTOR_TESTS PASS");
}
finally
{
    Directory.Delete(root, recursive: true);
}
