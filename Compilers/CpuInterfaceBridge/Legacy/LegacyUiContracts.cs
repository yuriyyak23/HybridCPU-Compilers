namespace CpuInterfaceBridge.Legacy;

/// <summary>
/// Снимок состояния ядра в формате legacy-контракта UI.
/// </summary>
/// <remarks>
/// Frozen by REF-11. Use <see cref="CpuInterfaceBridge.CoreStateSnapshot"/> via
/// <see cref="CpuInterfaceBridge.CpuInterfaceBridge"/> for all new UI integrations.
/// </remarks>
[Obsolete("Frozen legacy contract (REF-11). Use CoreStateSnapshot via CpuInterfaceBridge for new UI code.")]
public struct CoreStatInfo
{
    public int CoreId;

    public ulong LiveInstructionPointer;
    public ulong CycleCount;
    public string CurrentState;
    public bool IsStalled;
    public int ActiveVirtualThreadId;
    public ulong[] VirtualThreadLivePcs;
    public ulong[] VirtualThreadCommittedPcs;
    public bool[] VirtualThreadStalled;
    public string[] VirtualThreadStallReasons;

    public string CacheL1VliwLoad;
    public string CacheL1DataLoad;
    public string CacheL2VliwLoad;
    public string CacheL2DataLoad;

    public string[] IntRegistersValues;
    public string[] VectorRegistersValues;

    public bool IsEilp;
    public bool IsStreamMode;

    public bool PipelineEnabled;
    public double PipelineIpc;
    public double PipelineEfficiency;
    public ulong PipelineCycleCount;
    public ulong PipelineStallCycles;
    public ulong PipelineInstructionsRetired;
    public ulong PipelineBranchMispredicts;
    public ulong PipelineDataHazards;
    public ulong PipelineMemoryStalls;
    public ulong PipelineForwardingEvents;
    public ulong PipelineControlHazards;

    public ulong PipelineWawHazards;
    public ulong PipelineLoadUseBubbles;

    public ulong BurstReadCycles;
    public ulong BurstWriteCycles;
    public ulong ComputeCycles;
    public ulong OverlappedCycles;

    public bool PipelineVariantB;
    public string CurrentOpCodeName;
    public byte ActiveBufferSet;
    public byte PipelineBundleSlotIndex;

    public byte RoundingMode;
    public byte ExceptionMode;
    public byte VectorEnabled;
    public bool VectorContextValid;
    public ulong VectorContextFaultingPc;
    public uint VectorContextFaultingLane;
    public uint VectorContextFaultingOpCode;
}

/// <summary>
/// Legacy-события CPU-моста для UI-подписчиков.
/// </summary>
/// <remarks>
/// Frozen by REF-11. Use <see cref="CpuInterfaceBridge.IEmulatorService.StreamEventsAsync"/> via
/// <see cref="CpuInterfaceBridge.CpuInterfaceBridge"/> for all new UI event subscriptions.
/// </remarks>
[Obsolete("Frozen legacy contract (REF-11). Use CpuInterfaceBridge.StreamEmulationEventsAsync for new UI code.")]
public static class HybridCpuEvents
{
    /// <summary>
    /// Событие изменения статистики/состояния ядра.
    /// </summary>
    public static event Action<CoreStatInfo>? CoreStatInfoChanged;

    /// <summary>
    /// Событие завершения эмуляции.
    /// </summary>
    public static event Action? EmulationComplete;

    /// <summary>
    /// Публикует обновление состояния ядра.
    /// </summary>
    public static void OnCoreStatInfoChanged(CoreStatInfo coreInfo)
    {
        CoreStatInfoChanged?.Invoke(coreInfo);
    }

    /// <summary>
    /// Публикует завершение эмуляции.
    /// </summary>
    public static void OnEmulationComplete()
    {
        EmulationComplete?.Invoke();
    }
}
