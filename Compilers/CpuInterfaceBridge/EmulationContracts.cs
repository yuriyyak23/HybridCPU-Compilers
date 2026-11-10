namespace CpuInterfaceBridge;

/// <summary>
/// Запрос эмуляции.
/// </summary>
public sealed record EmulationRequest
{
    /// <summary>
    /// Идентификатор ядра процессора.
    /// </summary>
    public int CoreId { get; init; }

    /// <summary>
    /// Начальный адрес исполнения.
    /// </summary>
    public ulong StartAddress { get; init; }

    /// <summary>
    /// Максимальное число циклов эмуляции.
    /// Если null, выполняется до внешней остановки/отмены.
    /// </summary>
    public ulong? MaxCycles { get; init; }

    /// <summary>
    /// Период публикации прогресса по циклам.
    /// </summary>
    public int ProgressEveryNCycles { get; init; } = 1;

    /// <summary>
    /// Задержка между циклами для step/slow mode.
    /// </summary>
    public TimeSpan? CycleDelay { get; init; }

    /// <summary>
    /// Набор адресов останова.
    /// </summary>
    public IReadOnlySet<ulong> Breakpoints { get; init; } = new HashSet<ulong>();

    /// <summary>
    /// Контекст вызывающей сессии.
    /// </summary>
    public SessionContext? Session { get; init; }
}

/// <summary>
/// Причина завершения эмуляции.
/// </summary>
public enum EmulationStopReason
{
    Completed,
    Canceled,
    Paused,
    Stopped,
    BreakpointHit,
    Faulted,
    BudgetExhausted
}

/// <summary>
/// Тип события потока эмуляции.
/// </summary>
public enum EmulationEventType
{
    Started,
    CycleCompleted,
    BreakpointHit,
    Completed,
    Canceled,
    Paused,
    Stopped,
    Faulted,
    BudgetExhausted
}

/// <summary>
/// Событие потока эмуляции для подписчиков UI.
/// </summary>
public sealed record EmulationEvent(
    EmulationEventType EventType,
    CoreStateSnapshot Snapshot,
    ulong ExecutedCycles,
    string? Message = null,
    DateTimeOffset? TimestampUtc = null);

/// <summary>
/// Итог выполнения эмуляции.
/// </summary>
public sealed record EmulationResult(
    EmulationStopReason StopReason,
    ulong ExecutedCycles,
    CoreStateSnapshot LastSnapshot,
    ulong? BreakpointAddress = null,
    SessionContext? Session = null)
{
    public string? FailureReason { get; init; }
}

public enum DecodedBundleStateKind : byte
{
    Empty = 0,
    Canonical = 1,
    ForegroundMutated = 2,
    Replay = 3,
    DecodeFault = 4
}

public enum DecodedBundleStateOrigin : byte
{
    None = 0,
    Reset = 1,
    CanonicalDecode = 2,
    DecodeFallbackTrap = 3,
    ReplayBundleLoad = 4,
    ForegroundBundlePublication = 5,
    SingleSlotMutation = 7,
    ClearMaskMutation = 8,
    FspPacking = 9
}

public enum DecodedBundleStateOwnerKind : byte
{
    None = 0,
    BaseRuntimePublication = 1,
    DerivedIssuePlanPublication = 2
}

public enum PipelineContourKind : byte
{
    None = 0,
    DecodePublication = 1,
    ExecuteCompletion = 2,
    RetireVisibility = 3
}

public enum PipelineContourOwner : byte
{
    None = 0,
    DecodedBundleTransportPublication = 1,
    SingleLaneMicroOpExecution = 2,
    ExplicitPacketExecution = 3,
    ReferenceExecution = 4,
    WriteBackRetireWindow = 5
}

public enum PipelineContourVisibilityStage : byte
{
    None = 0,
    Decode = 1,
    Execute = 2,
    Memory = 3,
    WriteBack = 4,
    DirectRetire = 5
}

public sealed record PipelineContourCertificate
{
    public PipelineContourKind Kind { get; init; }
    public PipelineContourOwner Owner { get; init; }
    public PipelineContourVisibilityStage VisibilityStage { get; init; }
    public ulong Pc { get; init; }
    public byte SlotMask { get; init; }
    public bool IsPublished { get; init; }
}

/// <summary>
/// Снимок состояния ядра для UI-контракта моста.
/// </summary>
public sealed record CoreStateSnapshot
{
    /// <summary>
    /// Идентификатор ядра.
    /// </summary>
    public int CoreId { get; init; }

    /// <summary>
    /// Текущий указатель инструкции.
    /// </summary>
    public ulong LiveInstructionPointer { get; init; }

    /// <summary>
    /// Счётчик циклов.
    /// </summary>
    public ulong CycleCount { get; init; }

    /// <summary>Per-core finalized write-back lanes since telemetry reset; null for older providers. Not CIL instructions.</summary>
    public ulong? PipelineInstructionsRetired { get; init; }
    public bool HasExceptions { get; init; }
    public double PipelineIPC { get; init; }
    public double PipelineEfficiency { get; init; }
    public ulong PipelineStallCycles { get; init; }
    public ulong PipelineDataHazards { get; init; }
    public ulong PipelineMemoryStalls { get; init; }

    /// <summary>
    /// Текущее состояние процессорного ядра.
    /// </summary>
    public string CurrentState { get; init; } = string.Empty;

    /// <summary>
    /// Текущее power-state состояние ядра.
    /// </summary>
    public string CurrentPowerState { get; init; } = string.Empty;

    /// <summary>
    /// Текущий performance-level ядра.
    /// </summary>
    public uint CurrentPerformanceLevel { get; init; }

    /// <summary>
    /// Признак stall.
    /// </summary>
    public bool IsStalled { get; init; }

    /// <summary>
    /// Активный виртуальный поток.
    /// </summary>
    public int ActiveVirtualThreadId { get; init; }

    /// <summary>
    /// Live PC для всех VT.
    /// </summary>
    public IReadOnlyList<ulong> VirtualThreadLivePcs { get; init; } = Array.Empty<ulong>();

    /// <summary>
    /// Committed PC для всех VT.
    /// </summary>
    public IReadOnlyList<ulong> VirtualThreadCommittedPcs { get; init; } = Array.Empty<ulong>();

    /// <summary>
    /// Значения регистров активного VT.
    /// </summary>
    public IReadOnlyList<ulong> ActiveVirtualThreadRegisters { get; init; } = Array.Empty<ulong>();
    public DecodedBundleStateOwnerKind DecodedBundleStateOwnerKind { get; init; }
    public ulong DecodedBundleStateEpoch { get; init; }
    public ulong DecodedBundleStateVersion { get; init; }
    public DecodedBundleStateKind DecodedBundleStateKind { get; init; }
    public DecodedBundleStateOrigin DecodedBundleStateOrigin { get; init; }
    public ulong DecodedBundlePc { get; init; }
    public byte DecodedBundleValidMask { get; init; }
    public byte DecodedBundleNopMask { get; init; }
    public bool DecodedBundleHasCanonicalDecode { get; init; }
    public bool DecodedBundleHasCanonicalLegality { get; init; }
    public bool DecodedBundleHasDecodeFault { get; init; }
    public PipelineContourCertificate DecodePublicationCertificate { get; init; } = new();
    public PipelineContourCertificate ExecuteCompletionCertificate { get; init; } = new();
    public PipelineContourCertificate RetireVisibilityCertificate { get; init; } = new();
}
