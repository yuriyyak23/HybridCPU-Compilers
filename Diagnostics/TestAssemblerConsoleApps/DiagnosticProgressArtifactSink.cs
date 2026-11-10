using System.Text;
using System.Text.Json;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed class DiagnosticProgressArtifactSink
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        IncludeFields = true
    };

    private readonly DiagnosticArtifactWriter _writer;
    private readonly object _sync = new();
    private readonly DiagnosticTelemetryLogMode _telemetryLogMode;

    public DiagnosticProgressArtifactSink(string artifactDirectory, DiagnosticTelemetryLogMode telemetryLogMode)
    {
        _writer = new DiagnosticArtifactWriter(artifactDirectory);
        _writer.EnsureDirectory();
        _telemetryLogMode = telemetryLogMode;
    }

    public void WriteCheckpoint(DiagnosticExecutionCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        lock (_sync)
        {
            _writer.WriteJson("heartbeat.json", checkpoint);
            _writer.WriteText("heartbeat_summary.txt", BuildSummary(checkpoint));

            if (_telemetryLogMode != DiagnosticTelemetryLogMode.Extended)
            {
                return;
            }

            AppendJsonLine("heartbeat.ndjson", checkpoint);

            if (checkpoint.TelemetryProfile is not null)
            {
                _writer.WriteJson("telemetry_profile.partial.json", checkpoint.TelemetryProfile);
            }

            if (!string.IsNullOrWhiteSpace(checkpoint.ReplayTokenJson))
            {
                _writer.WriteText("replay_token.partial.json", checkpoint.ReplayTokenJson);
            }
        }
    }

    private void AppendJsonLine(string fileName, DiagnosticExecutionCheckpoint checkpoint)
    {
        string path = _writer.GetPath(fileName);
        string json = JsonSerializer.Serialize(checkpoint, CompactJsonOptions);
        File.AppendAllText(path, json + Environment.NewLine, Encoding.UTF8);
    }

    private static string BuildSummary(DiagnosticExecutionCheckpoint checkpoint)
    {
        StringBuilder summary = new StringBuilder();
        summary.AppendLine($"Profile: {checkpoint.ProfileId}");
        summary.AppendLine($"Captured: {checkpoint.CapturedUtc:O}");
        summary.AppendLine($"Stage: {checkpoint.Stage}");
        summary.AppendLine($"Reason: {checkpoint.Reason}");
        summary.AppendLine($"Mode: {checkpoint.Mode}");
        summary.AppendLine($"Frontend: {checkpoint.FrontendProfile}");
        summary.AppendLine($"Program variant: {checkpoint.ProgramVariant}");

        if (checkpoint.ObservedCycleCount.HasValue)
        {
            summary.AppendLine($"Observed cycles: {checkpoint.ObservedCycleCount.Value}");
        }

        if (checkpoint.ObservedInstructionsRetired.HasValue)
        {
            summary.AppendLine($"Observed retired instructions: {checkpoint.ObservedInstructionsRetired.Value}");
        }

        if (checkpoint.ActiveVirtualThreadId.HasValue)
        {
            summary.AppendLine($"Active VT: {checkpoint.ActiveVirtualThreadId.Value}");
        }

        if (checkpoint.ActiveLivePc.HasValue)
        {
            summary.AppendLine($"Active PC: 0x{checkpoint.ActiveLivePc.Value:X}");
        }

        if (!string.IsNullOrWhiteSpace(checkpoint.CurrentState))
        {
            summary.AppendLine($"Current state: {checkpoint.CurrentState}");
        }

        if (checkpoint.HardCycleLimit.HasValue)
        {
            summary.AppendLine($"Hard cycle limit: {checkpoint.HardCycleLimit.Value}");
        }

        if (checkpoint.RetirementTarget.HasValue)
        {
            summary.AppendLine($"Retirement target: {checkpoint.RetirementTarget.Value}");
        }

        if (!string.IsNullOrWhiteSpace(checkpoint.ReplayTokenCaptureFailure))
        {
            summary.AppendLine($"Replay token capture failure: {checkpoint.ReplayTokenCaptureFailure}");
        }

        return summary.ToString();
    }
}
