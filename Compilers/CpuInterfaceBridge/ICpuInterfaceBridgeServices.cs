namespace CpuInterfaceBridge;

/// <summary>
/// Контракт сервиса компиляции для UI-моста.
/// </summary>
public interface ICompilerService
{
    /// <summary>
    /// Запускает компиляцию и возвращает структурированный результат.
    /// </summary>
    Task<CompileResult> CompileAsync(SourceCode source, CompileOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Запускает НОВУЮ компиляцию и возвращает её логи; не является пассивной подпиской.
    /// </summary>
    IAsyncEnumerable<CompilerMessage> CompileLogStream(SourceCode source, CompileOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// Контракт сервиса эмуляции для UI-моста.
/// </summary>
public interface IEmulatorService
{
    /// <summary>
    /// Запускает фоновую эмуляцию.
    /// </summary>
    Task<EmulationResult> EmulateAsync(EmulationRequest request, IProgress<CoreStateSnapshot>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Выполняет один цикл эмуляции.
    /// </summary>
    Task<CoreStateSnapshot> StepAsync(EmulationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Приостанавливает текущую эмуляцию.
    /// </summary>
    Task PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Останавливает текущую эмуляцию.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Поток событий эмуляции для подписчиков UI.
    /// </summary>
    IAsyncEnumerable<EmulationEvent> StreamEventsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Контракт чтения снимков состояния.
/// </summary>
public interface ICoreStateService
{
    /// <summary>
    /// Читает потокобезопасный снимок состояния ядра.
    /// </summary>
    CoreStateSnapshot GetCoreState(int coreId);

    /// <summary>
    /// Читает фрагмент памяти.
    /// </summary>
    byte[] ReadMemory(ulong address, int length);

    /// <summary>
    /// Возвращает число доступных ядер.
    /// </summary>
    int GetTotalCores();
}
