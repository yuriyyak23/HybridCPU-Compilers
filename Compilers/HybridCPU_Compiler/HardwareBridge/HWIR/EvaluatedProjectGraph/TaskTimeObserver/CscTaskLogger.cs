using System.Text.Json;
using Microsoft.Build.Framework;

namespace HardwareBridge.CscObservation;

// MSBuild live event subscriber, not a custom Csc task, SDK targets, or compiler.
public sealed class CscTaskLogger : ILogger
{
    public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Diagnostic;
    public string? Parameters { get; set; }

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> events = new(StringComparer.Ordinal);
    private string? output;
    private string? error;

    private sealed class Entry
    {
        public string Id { get; set; } = "";
        public string ProjectFile { get; set; } = "";
        public string StartUtc { get; set; } = "";
        public string? FinishUtc { get; set; }
        public string? CommandLine { get; set; }
        public bool? Succeeded { get; set; }
        public int CommandEvents { get; set; }
    }

    private static string Identity(BuildEventContext? context)
    {
        if (context is null) return "missing-context";
        return context.NodeId + ":" + context.ProjectContextId + ":" + context.TaskId;
    }

    private static bool SafeDestination(string path)
    {
        if (!Path.IsPathFullyQualified(path) || File.Exists(path) || Directory.Exists(path))
            return false;
        string full = Path.GetFullPath(path);
        string temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(temp) + Path.DirectorySeparatorChar,
                             StringComparison.Ordinal))
            return false;
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(full)!); dir != null; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                return false;
        return true;
    }

    public void Initialize(IEventSource source)
    {
        output = Environment.GetEnvironmentVariable("HB_CSC_OBSERVER_LOG");
        if (string.IsNullOrWhiteSpace(output) || !SafeDestination(output))
            throw new InvalidOperationException("Csc event log must be a NEW nonlinked TEMP file");
        source.TaskStarted += (_, e) =>
        {
            if (!String.Equals(e.TaskName, "Csc", StringComparison.OrdinalIgnoreCase)) return;
            lock (gate)
            {
                string id = Identity(e.BuildEventContext);
                if (id == "missing-context" || events.ContainsKey(id) || events.Count >= 32)
                {
                    error = "duplicate/oversized/missing Csc task identity";
                    return;
                }
                events.Add(id, new Entry {
                    Id = id, ProjectFile = e.ProjectFile ?? "",
                    StartUtc = DateTimeOffset.UtcNow.ToString("O")
                });
            }
        };
        source.MessageRaised += (_, e) =>
        {
            if (e is not TaskCommandLineEventArgs command ||
                !String.Equals(command.TaskName, "Csc", StringComparison.OrdinalIgnoreCase)) return;
            lock (gate)
            {
                if (!events.TryGetValue(Identity(command.BuildEventContext), out var item))
                {
                    error = "orphan Csc command line event";
                    return;
                }
                item.CommandEvents++;
                if (item.CommandEvents != 1 || command.CommandLine.Length is < 1 or > 262144)
                {
                    error = "ambiguous/oversized Csc command line";
                    return;
                }
                item.CommandLine = command.CommandLine;
            }
        };
        source.TaskFinished += (_, e) =>
        {
            if (!String.Equals(e.TaskName, "Csc", StringComparison.OrdinalIgnoreCase)) return;
            lock (gate)
            {
                if (!events.TryGetValue(Identity(e.BuildEventContext), out var item) || item.FinishUtc != null)
                {
                    error = "orphan/duplicate Csc finish event";
                    return;
                }
                item.FinishUtc = DateTimeOffset.UtcNow.ToString("O");
                item.Succeeded = e.Succeeded;
            }
        };
    }

    public void Shutdown()
    {
        if (output is null) throw new InvalidOperationException("Csc observer not initialized");
        lock (gate)
        {
            // Always write a receipt, including error, even for failed/incomplete builds.
            // The verifier MUST reject every error / incomplete / absent Csc event.
            using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(file, new {
                schema = "hybridcpu.hardwarebridge.msbuild-csc-task-events/v1",
                disposition = "unqualified-software-observation",
                error,
                events = events.Values.OrderBy(x => x.ProjectFile, StringComparer.Ordinal)
                               .ThenBy(x => x.Id, StringComparer.Ordinal).ToArray()
            });
        }
    }
}
