using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace HardwareBridge.CscLaunchProbe;

internal static class Program
{
    private const int ProbeFailureExitCode = 125;
    private const int PinMismatchExitCode = 126;

    public static int Main(string[] args)
    {
        FileStream? pinnedExecutable = null;
        FileStream? pinnedResponse = null;
        FileStream? log = null;
        var childStarted = false;
        var logFinalized = false;
        int? childExitCode = null;
        string? failure = null;
        string? logPath = null;
        string? responsePath = null;
        string? responseCopyPath = null;
        string? responseSha256 = null;
        long? responseLength = null;
        Dictionary<string, object?>? startRecord = null;

        try
        {
            var cwd = Environment.CurrentDirectory;
            var targetPathRaw = RequiredEnvironment("CW_CSC_PROBE_TARGET_PATH");
            var expectedPathRaw = RequiredEnvironment("CW_CSC_PROBE_EXPECTED_PATH");
            var expectedSha = NormalizeSha256(RequiredEnvironment("CW_CSC_PROBE_EXPECTED_SHA256"));
            var logDirectory = Path.GetFullPath(RequiredEnvironment("CW_CSC_PROBE_LOG_DIRECTORY"));
            var targetPath = Path.GetFullPath(targetPathRaw);
            var expectedPath = Path.GetFullPath(expectedPathRaw);
            var commandArguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
            var ownPid = Environment.ProcessId;
            var timestamp = DateTimeOffset.UtcNow;
            var responseDirectoryRaw = Environment.GetEnvironmentVariable("CW_CSC_PROBE_RSP_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(responseDirectoryRaw))
            {
                var responseTokens = commandArguments.Where(argument => argument.StartsWith('@')).ToArray();
                if (responseTokens.Length != 1 || responseTokens[0].Length < 2)
                    throw new InvalidOperationException("Response capture requires exactly one nonempty @response argument.");
                responsePath = Path.GetFullPath(responseTokens[0][1..], cwd);
                responseCopyPath = Path.Combine(Path.GetFullPath(responseDirectoryRaw), $"csc-launch-{ownPid}.rsp");
            }
            var pathMatches = string.Equals(targetPath, expectedPath, StringComparison.OrdinalIgnoreCase);
            string? observedSha = null;
            string? hashFailure = null;

            if (pathMatches)
            {
                try
                {
                    // Keep this read-only handle open through child execution so Windows will
                    // refuse another writer/delete opener after the pin has been checked.
                    pinnedExecutable = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    observedSha = Convert.ToHexString(SHA256.HashData(pinnedExecutable)).ToLowerInvariant();
                    pinnedExecutable.Position = 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException)
                {
                    hashFailure = ex.GetType().Name + ": " + ex.Message;
                }
            }

            Directory.CreateDirectory(logDirectory);
            logPath = Path.Combine(logDirectory, $"csc-launch-{ownPid}.jsonl");
            log = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, FileOptions.WriteThrough);

            startRecord = new Dictionary<string, object?>
            {
                ["schema"] = "hybridcpu.csc-launch-probe/v1",
                ["record"] = "launch",
                ["timestampUtc"] = timestamp,
                ["probePid"] = ownPid,
                ["workingDirectory"] = cwd,
                ["targetExecutablePath"] = targetPath,
                ["expectedExecutablePath"] = expectedPath,
                ["targetPathPinMatched"] = pathMatches,
                ["targetExecutableSha256"] = observedSha,
                ["expectedExecutableSha256"] = expectedSha,
                ["targetHashPinMatched"] = observedSha is not null &&
                    string.Equals(observedSha, expectedSha, StringComparison.Ordinal),
                ["targetHashFailure"] = hashFailure,
                ["argumentCount"] = commandArguments.Length,
                ["arguments"] = commandArguments,
                ["responseFilePath"] = responsePath,
                ["responseCopyPath"] = responseCopyPath,
                ["stdio"] = "inherited",
                ["environment"] = "inherited",
                ["childWorkingDirectory"] = cwd,
                ["readBuffersClaimed"] = false
            };
            AppendRecord(log, startRecord);

            if (responsePath is not null)
            {
                var attributes = File.GetAttributes(responsePath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Response input is a reparse point.");
                pinnedResponse = new FileStream(responsePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (pinnedResponse.Length > 2 * 1024 * 1024)
                    throw new IOException("Response file exceeds the 2 MiB capture limit.");
                responseLength = pinnedResponse.Length;
                responseSha256 = Convert.ToHexString(SHA256.HashData(pinnedResponse)).ToLowerInvariant();
                pinnedResponse.Position = 0;
                using (var responseCopy = new FileStream(responseCopyPath!, FileMode.CreateNew,
                           FileAccess.Write, FileShare.Read, bufferSize: 65536, FileOptions.WriteThrough))
                {
                    pinnedResponse.CopyTo(responseCopy);
                    responseCopy.Flush(flushToDisk: true);
                }
                pinnedResponse.Position = 0;
                AppendRecord(log, new Dictionary<string, object?>
                {
                    ["schema"] = "hybridcpu.csc-launch-probe/v1",
                    ["record"] = "response-snapshot",
                    ["timestampUtc"] = DateTimeOffset.UtcNow,
                    ["responseFilePath"] = responsePath,
                    ["responseCopyPath"] = responseCopyPath,
                    ["responseBytes"] = responseLength,
                    ["responseSha256"] = responseSha256,
                    ["sourceHeldReadOnlyUntilChildExit"] = true
                });
                log.Flush(flushToDisk: true);
            }
            log.Flush(flushToDisk: true);

            if (!pathMatches || observedSha is null ||
                !string.Equals(observedSha, expectedSha, StringComparison.Ordinal))
            {
                AppendRecord(log, new Dictionary<string, object?>
                {
                    ["schema"] = "hybridcpu.csc-launch-probe/v1",
                    ["record"] = "result",
                    ["timestampUtc"] = DateTimeOffset.UtcNow,
                    ["status"] = "PinnedExecutableMismatch",
                    ["childStarted"] = false,
                    ["childExitCode"] = null
                });
                log.Flush(flushToDisk: true);
                logFinalized = true;
                return PinMismatchExitCode;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = targetPath,
                WorkingDirectory = cwd,
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };
            foreach (var argument in commandArguments)
                startInfo.ArgumentList.Add(argument);

            using var child = new Process { StartInfo = startInfo };
            if (!child.Start())
                throw new InvalidOperationException("Process.Start returned false.");

            childStarted = true;
            var childPid = child.Id;
            var childStartedUtc = DateTimeOffset.UtcNow;
            child.WaitForExit();
            childExitCode = child.ExitCode;
            AppendRecord(log, new Dictionary<string, object?>
            {
                ["schema"] = "hybridcpu.csc-launch-probe/v1",
                ["record"] = "result",
                ["timestampUtc"] = DateTimeOffset.UtcNow,
                ["status"] = "Exited",
                ["childStarted"] = true,
                ["childPid"] = childPid,
                ["childStartedUtc"] = childStartedUtc,
                ["responseCopyPath"] = responseCopyPath,
                ["responseBytes"] = responseLength,
                ["responseSha256"] = responseSha256,
                ["childExitCode"] = childExitCode
            });
            log.Flush(flushToDisk: true);
            logFinalized = true;
            return childExitCode.Value;
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            if (log is not null && !logFinalized)
            {
                try
                {
                    AppendRecord(log, new Dictionary<string, object?>
                    {
                        ["schema"] = "hybridcpu.csc-launch-probe/v1",
                        ["record"] = "result",
                        ["timestampUtc"] = DateTimeOffset.UtcNow,
                        ["status"] = "ProbeFailure",
                        ["childStarted"] = childStarted,
                        ["childExitCode"] = childExitCode,
                        ["failure"] = failure
                    });
                    log.Flush(flushToDisk: true);
                    logFinalized = true;
                }
                catch
                {
                    // Preserve the launch record if it exists; never replace a logging failure
                    // with a claimed child result.
                }
            }

            Console.Error.WriteLine($"CscLaunchProbe failure (childStarted={childStarted}, log={logPath ?? "unavailable"}): {failure}");
            return ProbeFailureExitCode;
        }
        finally
        {
            log?.Dispose();
            pinnedResponse?.Dispose();
            pinnedExecutable?.Dispose();
        }
    }

    private static string RequiredEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Required environment variable {name} is missing.");
        return value;
    }

    private static string NormalizeSha256(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("CW_CSC_PROBE_EXPECTED_SHA256 must be 64 hexadecimal characters.");
        return normalized;
    }

    private static void AppendRecord(Stream stream, object value)
    {
        JsonSerializer.Serialize(stream, value);
        stream.WriteByte((byte)'\n');
        stream.Flush();
    }
}
