// REF-11: allow-listed legacy caller of frozen CoreStatInfo and HybridCpuEvents.
#pragma warning disable CS0612, CS0618



namespace CpuInterfaceBridge.Legacy;

/// <summary>
/// Публикует снимки состояния ядра в формате legacy-контрактов для UI.
/// </summary>
/// <remarks>
/// Frozen by REF-11. Use <see cref="CpuInterfaceBridge.ICoreStateService"/> via
/// <see cref="CpuInterfaceBridge.CpuInterfaceBridge"/> for all new UI integrations.
/// </remarks>
[Obsolete("Frozen legacy contract (REF-11). Use CpuInterfaceBridge for new UI code.")]
public static class LegacyHostCoreStateBridge
{
    private static readonly HybridCPU_ISE.IseObservationService ObservationService =
        HybridCPU_ISE.Legacy.LegacyObservationServiceFactory.CreateLegacyGlobalCompat(
            HybridCPU_ISE.Legacy.LegacyProcessorMachineStateSource.SharedSyncRoot);
    private static bool _emulateByStep;
    private static int _stepPeriod = 100;

    /// <summary>
    /// Признак step-by-step режима эмуляции.
    /// </summary>
    public static bool EmulateByStep
    {
        get => _emulateByStep;
        set => _emulateByStep = value;
    }

    /// <summary>
    /// Период шага эмуляции.
    /// </summary>
    public static int StepPeriod
    {
        get => _stepPeriod;
        set => _stepPeriod = value;
    }

    /// <summary>
    /// Формирует и публикует snapshot ядра в legacy-событие.
    /// </summary>
    public static void PublishCoreStatInfoChanged(int coreId)
    {
        HybridCPU_ISE.CoreStateSnapshot coreState = ObservationService.GetCoreState(coreId);
        CoreStatInfo coreInfo = new()
        {
            CoreId = coreId,
            LiveInstructionPointer = coreState.LiveInstructionPointer,
            CycleCount = coreState.CycleCount,
            CurrentState = coreState.CurrentState,
            IsStalled = coreState.IsStalled,
            ActiveVirtualThreadId = coreState.ActiveVirtualThreadId,
            VirtualThreadLivePcs = coreState.VirtualThreadLivePcs,
            VirtualThreadCommittedPcs = coreState.VirtualThreadCommittedPcs,
            VirtualThreadStalled = coreState.VirtualThreadStalled,
            VirtualThreadStallReasons = coreState.VirtualThreadStallReasons,
            CacheL1VliwLoad = coreState.L1VliwBundleCount.ToString(),
            CacheL1DataLoad = coreState.L1DataEntryCount.ToString(),
            CacheL2VliwLoad = coreState.L2VliwBundleCount.ToString(),
            CacheL2DataLoad = coreState.L2DataEntryCount.ToString(),
            IntRegistersValues = new string[32],
            VectorRegistersValues = Array.Empty<string>(),
            CurrentOpCodeName = string.Empty,
            PipelineEnabled = coreState.PipelineEnabled,
            PipelineIpc = coreState.PipelineIPC,
            PipelineEfficiency = coreState.PipelineEfficiency,
            PipelineCycleCount = coreState.PipelineCycleCount,
            PipelineStallCycles = coreState.PipelineStallCycles,
            PipelineInstructionsRetired = coreState.PipelineInstructionsRetired,
            PipelineBranchMispredicts = coreState.PipelineBranchMispredicts,
            PipelineDataHazards = coreState.PipelineDataHazards,
            PipelineMemoryStalls = coreState.PipelineMemoryStalls,
            PipelineForwardingEvents = coreState.PipelineForwardingEvents,
            PipelineControlHazards = coreState.PipelineControlHazards,
            PipelineWawHazards = coreState.PipelineWAWHazards,
            PipelineLoadUseBubbles = coreState.PipelineLoadUseBubbles,
            PipelineBundleSlotIndex = coreState.PipelineBundleSlotIndex,
        };

        for (int registerIndex = 0; registerIndex != coreInfo.IntRegistersValues.Length; registerIndex++)
        {
            coreInfo.IntRegistersValues[registerIndex] =
                coreState.ActiveVirtualThreadRegisters[registerIndex].ToString();
        }

        HybridCpuEvents.OnCoreStatInfoChanged(coreInfo);
    }
}

#pragma warning restore CS0612, CS0618
