using HybridCPU_ISE;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Diagnostics;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed partial class SimpleAsmApp
{
    private readonly IseObservationService _observationService =
        HybridCPU_ISE.Legacy.LegacyObservationServiceFactory.CreateLegacyGlobalCompat(
            HybridCPU_ISE.Legacy.LegacyProcessorMachineStateSource.SharedSyncRoot);
    private DiagnosticProgressArtifactSink? _progressSink;
    private DiagnosticRunProfile? _attachedRunProfile;
    private DateTimeOffset _lastCheckpointUtc;
    private ulong _lastCheckpointCycleCount;
    private long _checkpointSequence;

    public void AttachProgressSink(DiagnosticRunProfile runProfile, DiagnosticProgressArtifactSink progressSink)
    {
        ArgumentNullException.ThrowIfNull(runProfile);
        ArgumentNullException.ThrowIfNull(progressSink);

        _attachedRunProfile = runProfile;
        _progressSink = progressSink;
        ResetProgressState();
    }

    private void ResetProgressState()
    {
        _lastCheckpointUtc = DateTimeOffset.MinValue;
        _lastCheckpointCycleCount = 0;
        _checkpointSequence = 0;
    }

    private void PublishLifecycleCheckpoint(
        string stage,
        string reason,
        bool force = true,
        ulong? retirementTarget = null,
        ulong? hardCycleLimit = null)
    {
        if (_progressSink is null || _attachedRunProfile is null)
        {
            return;
        }

        DateTimeOffset capturedUtc = DateTimeOffset.UtcNow;
        ulong cycleCount = TryReadCycleCount();
        bool shouldWrite = force ||
                           _lastCheckpointUtc == DateTimeOffset.MinValue ||
                           (capturedUtc - _lastCheckpointUtc).TotalMilliseconds >= _attachedRunProfile.HeartbeatIntervalMs ||
                           cycleCount >= (_lastCheckpointCycleCount + _attachedRunProfile.HeartbeatCycleStride);

        if (!shouldWrite)
        {
            return;
        }

        DiagnosticExecutionCheckpoint checkpoint = CaptureCheckpoint(
            stage,
            reason,
            capturedUtc,
            retirementTarget,
            hardCycleLimit);

        _progressSink.WriteCheckpoint(checkpoint);
        _lastCheckpointUtc = checkpoint.CapturedUtc;
        if (checkpoint.ObservedCycleCount.HasValue)
        {
            _lastCheckpointCycleCount = checkpoint.ObservedCycleCount.Value;
        }
    }

    private DiagnosticExecutionCheckpoint CaptureCheckpoint(
        string stage,
        string reason,
        DateTimeOffset capturedUtc,
        ulong? retirementTarget,
        ulong? hardCycleLimit)
    {
        CoreStateSnapshot? coreState = null;
        StackFlagsSnapshot? stackFlags = null;
        PerformanceReport? performance = null;
        ReplayPhaseMetrics? replayPhaseMetrics = null;
        SchedulerPhaseMetrics? schedulerPhaseMetrics = null;
        TypedSlotTelemetryProfile? telemetryProfile = null;
        string? replayTokenJson = null;
        string? replayTokenCaptureFailure = null;
        bool hasLiveObservation = false;

        try
        {
            coreState = _observationService.GetCoreState(_coreId);
            hasLiveObservation = true;
        }
        catch
        {
            // Partial-dump capture must stay best-effort.
        }

        if (hasLiveObservation && ShouldCaptureExtendedTelemetry())
        {
            try
            {
                stackFlags = _observationService.GetStackFlagsSnapshot(_coreId);
            }
            catch
            {
                // Partial-dump capture must stay best-effort.
            }

            try
            {
                performance = _observationService.GetPerformanceReport();
            }
            catch
            {
                // Partial-dump capture must stay best-effort.
            }

            try
            {
                replayPhaseMetrics = _observationService.GetReplayPhaseMetrics(_coreId);
                schedulerPhaseMetrics = _observationService.GetSchedulerPhaseMetrics(_coreId);
            }
            catch
            {
                // Partial-dump capture must stay best-effort.
            }

            try
            {
                telemetryProfile = _observationService.GetTypedSlotTelemetryProfile(_coreId, BuildProgramHash());
            }
            catch
            {
                // Partial-dump capture must stay best-effort.
            }

            try
            {
                replayTokenJson = _observationService.GetReplayToken();
            }
            catch (Exception ex)
            {
                replayTokenCaptureFailure = ex.Message;
            }
        }

        return new DiagnosticExecutionCheckpoint
        {
            ProfileId = _attachedRunProfile?.Id ?? string.Empty,
            DisplayName = _attachedRunProfile?.DisplayName ?? string.Empty,
            Mode = _attachedRunProfile?.SimpleAsmMode?.ToString() ?? _programVariant.ToString(),
            FrontendProfile = _frontendMode.ToString(),
            ProgramVariant = _programVariant.ToString(),
            ObservationSourceProvenance = _observationService.SourceProvenance.ToString(),
            Stage = stage,
            Reason = reason,
            Sequence = ++_checkpointSequence,
            CapturedUtc = capturedUtc,
            RetirementTarget = retirementTarget,
            HardCycleLimit = hardCycleLimit,
            ObservedCycleCount = coreState?.PipelineCycleCount,
            ObservedInstructionsRetired = coreState?.PipelineInstructionsRetired,
            ObservedStallCycles = coreState?.PipelineStallCycles,
            ActiveVirtualThreadId = coreState?.ActiveVirtualThreadId,
            ActiveLivePc = coreState?.LiveInstructionPointer,
            CurrentState = coreState?.CurrentState,
            CoreState = coreState,
            StackFlags = stackFlags,
            Performance = performance,
            ReplayPhaseMetrics = replayPhaseMetrics,
            SchedulerPhaseMetrics = schedulerPhaseMetrics,
            TelemetryProfile = telemetryProfile,
            ReplayTokenJson = replayTokenJson,
            ReplayTokenCaptureFailure = replayTokenCaptureFailure
        };
    }

    private bool ShouldCaptureExtendedTelemetry()
    {
        return _attachedRunProfile?.TelemetryLogMode == DiagnosticTelemetryLogMode.Extended;
    }

    private ulong TryReadCycleCount()
    {
        try
        {
            return _observationService.GetCoreState(_coreId).PipelineCycleCount;
        }
        catch
        {
            return 0UL;
        }
    }

    private string BuildProgramHash()
    {
        return $"{_programVariant}:{_frontendMode}:{_instructionCount}:{_emittedVirtualThreadIds.Count}:{_requestedWorkloadIterations}:{_loopBodyInstructionCount}:{_workloadShape}";
    }
}
