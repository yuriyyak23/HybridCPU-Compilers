using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

var probe = Required("CW_CSC_PROBE_EXE");
var fixture = Required("CW_CSC_FIXTURE_EXE");
var root = Path.Combine(Path.GetTempPath(), "cw-csc-launch-probe-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var cwd = Path.Combine(root, "cwd with spaces Ω Ж");
    Directory.CreateDirectory(cwd);
    var logDirectory = Path.Combine(root, "logs");
    Directory.CreateDirectory(logDirectory);
    var capture = Path.Combine(root, "fixture.json");
    var argvCases = new[] { "", "with spaces", "embedded\"quote", "trailing\\", "Юникод Ω", "@opaque.rsp", "-define:ONE;TWO" };

    var success = RunProbe(probe, fixture, cwd, logDirectory, capture, argvCases, 0, stdinText: "stdin sentinel");
    Equal(0, success.ExitCode, "success exit");
    True(success.Stdout.Contains("fixture stdout", StringComparison.Ordinal), "stdout inheritance");
    True(success.Stderr.Contains("fixture stderr", StringComparison.Ordinal), "stderr inheritance");
    var fixtureRecord = JsonDocument.Parse(File.ReadAllText(capture)).RootElement;
    SequenceEqual(argvCases, fixtureRecord.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()!).ToArray(), "argv preservation");
    Equal(cwd, fixtureRecord.GetProperty("currentDirectory").GetString(), "Unicode/spaces cwd");
    Equal("stdin sentinel", fixtureRecord.GetProperty("standardInput").GetString(), "stdin inheritance");
    var log = ReadLog(success.LogPath!);
    Equal("launch", log[0].GetProperty("record").GetString(), "launch log record");
    True(log[0].GetProperty("probePid").GetInt32() > 0, "probe PID present");
    Equal(cwd, log[0].GetProperty("workingDirectory").GetString(), "logged cwd");
    Equal(fixtureRecord.GetProperty("pid").GetInt32(), log[1].GetProperty("childPid").GetInt32(), "child PID crosswalk");
    Equal(0, log[1].GetProperty("childExitCode").GetInt32(), "logged child exit");
    SequenceEqual(argvCases, log[0].GetProperty("arguments").EnumerateArray().Select(x => x.GetString()!).ToArray(), "logged argv");
    Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))).ToLowerInvariant(),
        log[0].GetProperty("targetExecutableSha256").GetString(), "target hash");
    Equal(false, log[0].GetProperty("readBuffersClaimed").GetBoolean(), "read buffers not claimed");
    Equal(fixtureRecord.GetProperty("pid").GetInt32(), log[^1].GetProperty("childPid").GetInt32(), "child PID crosswalk");
    Equal(0, log[^1].GetProperty("childExitCode").GetInt32(), "logged child exit");

    var rspSource = Path.Combine(root, "captured response Ω.rsp");
    var rspBytes = System.Text.Encoding.UTF8.GetBytes("/noconfig\r\n@input with spaces.cs\r\n");
    File.WriteAllBytes(rspSource, rspBytes);
    var rspCopyDirectory = Path.Combine(root, "rsp copies");
    Directory.CreateDirectory(rspCopyDirectory);
    var rspRun = RunProbe(probe, fixture, cwd, logDirectory, Path.Combine(root, "rsp-fixture.json"),
        new[] { "@" + rspSource }, 0, responseDirectory: rspCopyDirectory);
    Equal(0, rspRun.ExitCode, "response snapshot child exit");
    var rspLog = ReadLog(rspRun.LogPath!);
    var rspSnapshot = rspLog.Single(row => row.GetProperty("record").GetString() == "response-snapshot");
    var rspCopy = rspSnapshot.GetProperty("responseCopyPath").GetString()!;
    True(rspBytes.SequenceEqual(File.ReadAllBytes(rspCopy)), "raw response snapshot bytes");
    Equal(Convert.ToHexString(SHA256.HashData(rspBytes)).ToLowerInvariant(),
        rspSnapshot.GetProperty("responseSha256").GetString(), "response snapshot hash");
    Equal(true, rspSnapshot.GetProperty("sourceHeldReadOnlyUntilChildExit").GetBoolean(), "response held read-only");

    var missingRspDirectory = Path.Combine(root, "missing-rsp-copy");
    Directory.CreateDirectory(missingRspDirectory);
    var missingRsp = RunProbe(probe, fixture, cwd, logDirectory, Path.Combine(root, "missing-rsp.json"),
        new[] { "@" + Path.Combine(root, "does-not-exist.rsp") }, 0, responseDirectory: missingRspDirectory);
    Equal(125, missingRsp.ExitCode, "missing response fails closed");
    True(!File.Exists(Path.Combine(root, "missing-rsp.json")), "missing response never launches child");
    Equal(false, ReadLog(missingRsp.LogPath!)[^1].GetProperty("childStarted").GetBoolean(),
        "missing response log says no child");

    var nonzeroCapture = Path.Combine(root, "nonzero.json");
    var nonzero = RunProbe(probe, fixture, cwd, logDirectory, nonzeroCapture, new[] { "nonzero" }, 37);
    Equal(37, nonzero.ExitCode, "exact child nonzero exit");

    var mismatchCapture = Path.Combine(root, "path-mismatch.json");
    var pathMismatch = RunProbe(probe, fixture, cwd, logDirectory, mismatchCapture, Array.Empty<string>(),
        0, expectedPath: fixture + ".different");
    Equal(126, pathMismatch.ExitCode, "path mismatch code");
    True(!File.Exists(mismatchCapture), "path mismatch did not launch target");
    Equal("PinnedExecutableMismatch", ReadLog(pathMismatch.LogPath!)[1].GetProperty("status").GetString(), "path mismatch status");

    var hashMismatchCapture = Path.Combine(root, "hash-mismatch.json");
    var hashMismatch = RunProbe(probe, fixture, cwd, logDirectory, hashMismatchCapture, Array.Empty<string>(),
        0, expectedSha: new string('0', 64));
    Equal(126, hashMismatch.ExitCode, "hash mismatch code");
    True(!File.Exists(hashMismatchCapture), "hash mismatch did not launch target");

    var logFailureCapture = Path.Combine(root, "log-failure.json");
    var blocker = Path.Combine(root, "not-a-directory");
    File.WriteAllText(blocker, "block");
    var logFailure = RunProbe(probe, fixture, cwd, blocker, logFailureCapture, Array.Empty<string>(), 0);
    Equal(125, logFailure.ExitCode, "log failure code");
    True(!File.Exists(logFailureCapture), "log failure did not launch target");

    Console.WriteLine("PASS: cwd/argv/stdout/stderr, nonzero exit, path/hash pin rejection, log failure no-launch");
    return 0;
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { /* temp fixture cleanup is best effort */ }
}

static (int ExitCode, string Stdout, string Stderr, string? LogPath) RunProbe(
    string probe, string fixture, string cwd, string logDirectory, string capture, string[] args,
    int fixtureExit, string? expectedPath = null, string? expectedSha = null, string? stdinText = null,
    string? responseDirectory = null)
{
    var psi = new ProcessStartInfo { FileName = probe, WorkingDirectory = cwd, UseShellExecute = false,
        RedirectStandardInput = stdinText is not null, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in args) psi.ArgumentList.Add(arg);
    psi.Environment["CW_CSC_PROBE_TARGET_PATH"] = fixture;
    psi.Environment["CW_CSC_PROBE_EXPECTED_PATH"] = expectedPath ?? fixture;
    psi.Environment["CW_CSC_PROBE_EXPECTED_SHA256"] = expectedSha ??
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))).ToLowerInvariant();
    psi.Environment["CW_CSC_PROBE_LOG_DIRECTORY"] = logDirectory;
    if (responseDirectory is not null)
        psi.Environment["CW_CSC_PROBE_RSP_DIRECTORY"] = responseDirectory;
    psi.Environment["CW_CSC_FIXTURE_CAPTURE"] = capture;
    psi.Environment["CW_CSC_FIXTURE_EXIT_CODE"] = fixtureExit.ToString();
    psi.Environment["CW_CSC_FIXTURE_READ_STDIN"] = stdinText is null ? "0" : "1";
    using var process = Process.Start(psi) ?? throw new InvalidOperationException("Probe start failed");
    if (stdinText is not null) { process.StandardInput.WriteLine(stdinText); process.StandardInput.Close(); }
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    var log = Directory.Exists(logDirectory)
        ? Directory.GetFiles(logDirectory, $"csc-launch-{process.Id}.jsonl").SingleOrDefault()
        : null;
    return (process.ExitCode, stdout, stderr, log);
}

static JsonElement[] ReadLog(string path) => File.ReadAllLines(path)
    .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
static string Required(string key) => Environment.GetEnvironmentVariable(key)
    ?? throw new InvalidOperationException($"{key} is required");
static void True(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); }
static void Equal<T>(T expected, T actual, string label)
{ if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"FAIL: {label}: expected <{expected}>, got <{actual}>"); }
static void SequenceEqual(string[] expected, string[] actual, string label)
{ if (!expected.SequenceEqual(actual, StringComparer.Ordinal)) throw new Exception($"FAIL: {label}: vectors differ"); }
