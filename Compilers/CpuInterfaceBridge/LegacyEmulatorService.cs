using CpuInterfaceBridge.Legacy;
using System.Threading.Channels;
using YAKSys_Hybrid_CPU;

// REF-11: allow-listed legacy caller of frozen LegacyHostCoreStateBridge and HybridCpuEvents.
#pragma warning disable CS0612, CS0618

namespace CpuInterfaceBridge;

/// <summary>
/// Legacy-адаптер эмуляции поверх текущего runtime-контура Processor/ISE.
/// </summary>
public sealed class LegacyEmulatorService : IEmulatorService
{
    private readonly ICoreStateService _coreStateService;
    private readonly Channel<EmulationEvent> _eventsChannel = Channel.CreateUnbounded<EmulationEvent>();
    private readonly object _executionSync = new();
    private CancellationTokenSource? _executionCts;
    private volatile EmulationStopReason _requestedStopReason;

    /// <summary>
    /// Создаёт адаптер эмуляции.
    /// </summary>
    public LegacyEmulatorService(ICoreStateService? coreStateService = null)
    {
        _coreStateService = coreStateService ?? new IseCoreStateService(
            HybridCPU_ISE.Legacy.LegacyObservationServiceFactory.CreateLegacyGlobalCompat(
                HybridCPU_ISE.Legacy.LegacyProcessorMachineStateSource.SharedSyncRoot));
    }

    /// <inheritdoc />
    public async Task<EmulationResult> EmulateAsync(EmulationRequest request, IProgress<CoreStateSnapshot>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ProgressEveryNCycles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "ProgressEveryNCycles must be greater than zero.");
        }

        CancellationTokenSource executionCts;
        lock (_executionSync)
        {
            if (_executionCts is not null)
            {
                throw new InvalidOperationException("Emulation is already running.");
            }

            _requestedStopReason = EmulationStopReason.Canceled;
            _executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            executionCts = _executionCts;
        }

        try
        {
            return await RunEmulationLoopAsync(request, progress, executionCts.Token, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_executionSync)
            {
                _executionCts?.Dispose();
                _executionCts = null;
            }
        }
    }

    /// <inheritdoc />
    public Task<CoreStateSnapshot> StepAsync(EmulationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        EnsureCoreInitialized(request.CoreId, request.StartAddress);
        YAKSys_Hybrid_CPU.Processor.CPU_Cores[request.CoreId].ExecutePipelineCycle();
        LegacyHostCoreStateBridge.PublishCoreStatInfoChanged(request.CoreId);
        CoreStateSnapshot snapshot = _coreStateService.GetCoreState(request.CoreId);
        PublishEvent(EmulationEventType.CycleCompleted, snapshot, 1);
        return Task.FromResult(snapshot);
    }

    /// <inheritdoc />
    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_executionSync)
        {
            _requestedStopReason = EmulationStopReason.Paused;
            _executionCts?.Cancel();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<EmulationEvent> StreamEventsAsync(CancellationToken cancellationToken = default)
    {
        return _eventsChannel.Reader.ReadAllAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_executionSync)
        {
            _requestedStopReason = EmulationStopReason.Stopped;
            _executionCts?.Cancel();
        }

        return Task.CompletedTask;
    }

    private async Task<EmulationResult> RunEmulationLoopAsync(
        EmulationRequest request,
        IProgress<CoreStateSnapshot>? progress,
        CancellationToken executionToken,
        CancellationToken externalToken)
    {
        ulong executedCycles = 0;
        CoreStateSnapshot lastSnapshot = _coreStateService.GetCoreState(request.CoreId);
        SessionContext session = request.Session ?? new SessionContext();
        PublishEvent(EmulationEventType.Started, lastSnapshot, executedCycles);

        try
        {
            executionToken.ThrowIfCancellationRequested();
            EnsureCoreInitialized(request.CoreId, request.StartAddress);
            while (true)
            {
                executionToken.ThrowIfCancellationRequested();
                lastSnapshot = _coreStateService.GetCoreState(request.CoreId);
                if (request.Breakpoints.Contains(lastSnapshot.LiveInstructionPointer))
                {
                    PublishEvent(EmulationEventType.BreakpointHit, lastSnapshot, executedCycles, $"Breakpoint at 0x{lastSnapshot.LiveInstructionPointer:X}.");
                    return new EmulationResult(
                        EmulationStopReason.BreakpointHit,
                        executedCycles,
                        lastSnapshot,
                        lastSnapshot.LiveInstructionPointer,
                        session);
                }

                if (request.MaxCycles is { } maxCycles && executedCycles >= maxCycles)
                {
                    break;
                }

                YAKSys_Hybrid_CPU.Processor.CPU_Cores[request.CoreId].ExecutePipelineCycle();
                executedCycles++;
                LegacyHostCoreStateBridge.PublishCoreStatInfoChanged(request.CoreId);

                if (executedCycles % (ulong)request.ProgressEveryNCycles == 0)
                {
                    lastSnapshot = _coreStateService.GetCoreState(request.CoreId);
                    progress?.Report(lastSnapshot);
                    PublishEvent(EmulationEventType.CycleCompleted, lastSnapshot, executedCycles);
                }

                if (request.CycleDelay is { } delay && delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, executionToken).ConfigureAwait(false);
                }
            }

            lastSnapshot = _coreStateService.GetCoreState(request.CoreId);
            progress?.Report(lastSnapshot);
            executionToken.ThrowIfCancellationRequested();
            PublishEvent(EmulationEventType.BudgetExhausted, lastSnapshot, executedCycles);

            return new EmulationResult(EmulationStopReason.BudgetExhausted, executedCycles, lastSnapshot, null, session);
        }
        catch (OperationCanceledException) when (externalToken.IsCancellationRequested)
        {
            lastSnapshot = _coreStateService.GetCoreState(request.CoreId);
            PublishEvent(EmulationEventType.Canceled, lastSnapshot, executedCycles);
            return new EmulationResult(EmulationStopReason.Canceled, executedCycles, lastSnapshot, null, session);
        }
        catch (OperationCanceledException)
        {
            lastSnapshot = _coreStateService.GetCoreState(request.CoreId);
            PublishEvent(MapStopReasonToEventType(_requestedStopReason), lastSnapshot, executedCycles);
            return new EmulationResult(_requestedStopReason, executedCycles, lastSnapshot, null, session);
        }
        catch (Exception ex)
        {
            PublishEvent(EmulationEventType.Faulted, lastSnapshot, executedCycles, ex.Message);
            return new EmulationResult(EmulationStopReason.Faulted, executedCycles, lastSnapshot, null, session) { FailureReason = ex.Message };
        }
    }

    private void PublishEvent(EmulationEventType eventType, CoreStateSnapshot snapshot, ulong executedCycles, string? message = null)
    {
        _eventsChannel.Writer.TryWrite(new EmulationEvent(eventType, snapshot, executedCycles, message, DateTimeOffset.UtcNow));
    }

    private static EmulationEventType MapStopReasonToEventType(EmulationStopReason stopReason)
    {
        return stopReason switch
        {
            EmulationStopReason.Paused => EmulationEventType.Paused,
            EmulationStopReason.Stopped => EmulationEventType.Stopped,
            EmulationStopReason.Canceled => EmulationEventType.Canceled,
            EmulationStopReason.BreakpointHit => EmulationEventType.BreakpointHit,
            EmulationStopReason.Faulted => EmulationEventType.Faulted,
            _ => EmulationEventType.Completed,
        };
    }

    private static void EnsureCoreInitialized(int coreId, ulong startAddress)
    {
        if (coreId < 0 || coreId >= YAKSys_Hybrid_CPU.Processor.CPU_Cores.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(coreId));
        }

        Processor.CPU_Core.PipelineControl pipelineControl = YAKSys_Hybrid_CPU.Processor.CPU_Cores[coreId].GetPipelineControl();
        if (pipelineControl.Enabled)
        {
            return;
        }

        Processor.CPU_Core core = YAKSys_Hybrid_CPU.Processor.CPU_Cores[coreId];
        core.PrepareExecutionStart(startAddress);
        YAKSys_Hybrid_CPU.Processor.CPU_Cores[coreId] = core;
    }
}

#pragma warning restore CS0612, CS0618
