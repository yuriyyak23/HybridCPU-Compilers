namespace CpuInterfaceBridge;

/// <summary>
/// Точка входа библиотеки-моста между GUI и текущими compiler/ISE сервисами.
/// </summary>
public sealed class CpuInterfaceBridge
{
    /// <summary>
    /// Инициализирует мост стандартными legacy-адаптерами.
    /// </summary>
    public CpuInterfaceBridge()
        : this(
            new LegacyCompilerService(),
            new LegacyEmulatorService(),
            new IseCoreStateService(
                HybridCPU_ISE.Legacy.LegacyObservationServiceFactory.CreateLegacyGlobalCompat(
                    HybridCPU_ISE.Legacy.LegacyProcessorMachineStateSource.SharedSyncRoot)))
    {
    }

    /// <summary>
    /// Инициализирует мост пользовательскими реализациями сервисов.
    /// </summary>
    public CpuInterfaceBridge(
        ICompilerService compilerService,
        IEmulatorService emulatorService,
        ICoreStateService coreStateService)
    {
        ArgumentNullException.ThrowIfNull(compilerService);
        ArgumentNullException.ThrowIfNull(emulatorService);
        ArgumentNullException.ThrowIfNull(coreStateService);

        Compiler = compilerService;
        Emulator = emulatorService;
        CoreState = coreStateService;
    }

    /// <summary>
    /// Сервис компиляции.
    /// </summary>
    public ICompilerService Compiler { get; }

    /// <summary>
    /// Сервис эмуляции.
    /// </summary>
    public IEmulatorService Emulator { get; }

    /// <summary>
    /// Сервис чтения состояния.
    /// </summary>
    public ICoreStateService CoreState { get; }

    /// <summary>
    /// Выполняет компиляцию через сервис моста.
    /// </summary>
    public Task<CompileResult> CompileAsync(SourceCode source, CompileOptions options, CancellationToken cancellationToken = default)
    {
        return Compiler.CompileAsync(source, options, cancellationToken);
    }

    /// <summary>
    /// Запускает НОВУЮ компиляцию и возвращает её сообщения; не наблюдает существующую операцию.
    /// </summary>
    public IAsyncEnumerable<CompilerMessage> CompileLogStream(SourceCode source, CompileOptions options, CancellationToken cancellationToken = default)
    {
        return Compiler.CompileLogStream(source, options, cancellationToken);
    }

    /// <summary>
    /// Запускает непрерывную эмуляцию.
    /// </summary>
    public Task<EmulationResult> EmulateAsync(EmulationRequest request, IProgress<CoreStateSnapshot>? progress, CancellationToken cancellationToken = default)
    {
        return Emulator.EmulateAsync(request, progress, cancellationToken);
    }

    /// <summary>
    /// Выполняет один шаг эмуляции.
    /// </summary>
    public Task<CoreStateSnapshot> StepAsync(EmulationRequest request, CancellationToken cancellationToken = default)
    {
        return Emulator.StepAsync(request, cancellationToken);
    }

    /// <summary>
    /// Приостанавливает эмуляцию.
    /// </summary>
    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        return Emulator.PauseAsync(cancellationToken);
    }

    /// <summary>
    /// Останавливает эмуляцию.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Emulator.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Возвращает поток событий эмуляции.
    /// </summary>
    public IAsyncEnumerable<EmulationEvent> StreamEmulationEventsAsync(CancellationToken cancellationToken = default)
    {
        return Emulator.StreamEventsAsync(cancellationToken);
    }

    /// <summary>
    /// Возвращает snapshot состояния ядра.
    /// </summary>
    public CoreStateSnapshot GetCoreState(int coreId)
    {
        return CoreState.GetCoreState(coreId);
    }

    /// <summary>
    /// Читает блок памяти через сервис состояния.
    /// </summary>
    public byte[] ReadMemory(ulong address, int length)
    {
        return CoreState.ReadMemory(address, length);
    }

    /// <summary>
    /// Возвращает количество доступных ядер.
    /// </summary>
    public int GetTotalCores()
    {
        return CoreState.GetTotalCores();
    }
}
