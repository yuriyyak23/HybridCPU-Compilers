using HybridCPU.Compiler.Core.IR;

namespace CpuInterfaceBridge;

/// <summary>
/// Исходный код для запуска компиляции.
/// </summary>
/// <param name="Text">Текст исходного кода.</param>
/// <param name="SourceName">Логическое имя источника (файл/буфер).</param>
public sealed record SourceCode(string Text, string SourceName = "InMemory");

/// <summary>
/// Контекст сессии операций CpuInterfaceBridge.
/// </summary>
public sealed record SessionContext
{
    /// <summary>
    /// Уникальный идентификатор сессии.
    /// </summary>
    public Guid SessionId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Время старта сессии в UTC.
    /// </summary>
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Корреляционный идентификатор для трассировки.
    /// </summary>
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>
    /// Дополнительные метаданные сессии.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Опции компиляции в контуре CpuInterfaceBridge.
/// </summary>
public sealed record CompileOptions
{
    /// <summary>
    /// Количество виртуальных потоков для чтения результатов.
    /// </summary>
    public int VirtualThreadCount { get; init; } = 4;

    /// <summary>
    /// Идентификаторы VT, результаты которых нужно вернуть.
    /// Если null, используются 0..VirtualThreadCount-1.
    /// </summary>
    public IReadOnlyList<int>? RequestedVirtualThreadIds { get; init; }

    /// <summary>
    /// Максимальное ожидание компиляции на стороне адаптера.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Контекст вызывающей сессии.
    /// </summary>
    public SessionContext? Session { get; init; }
}

/// <summary>
/// Уровень сообщения компиляции.
/// </summary>
public enum CompilerMessageLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// Сообщение потока компиляции.
/// </summary>
public sealed record CompilerMessage(
    CompilerMessageLevel Level,
    string Text,
    int? VirtualThreadId = null,
    string? ErrorCode = null,
    int? Line = null,
    int? Column = null,
    DateTimeOffset? TimestampUtc = null,
    string? ActivityId = null);

/// <summary>
/// Статистика компиляции.
/// </summary>
public sealed record CompilationStatistics
{
    /// <summary>
    /// Время компиляции.
    /// </summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Количество успешно прочитанных VT-результатов.
    /// </summary>
    public int CompiledVirtualThreadCount { get; init; }

    /// <summary>
    /// Количество ошибок компиляции.
    /// </summary>
    public int ErrorCount { get; init; }
}

/// <summary>
/// Канонический артефакт компиляции для конкретного VT.
/// </summary>
public sealed record CompiledProgram(int VirtualThreadId, HybridCpuCompiledProgram Program);

/// <summary>
/// Итог компиляции в мосте.
/// </summary>
public sealed record CompileResult(
    IReadOnlyList<CompiledProgram> Programs,
    CompilationStatistics Statistics,
    bool Success,
    IReadOnlyList<string> Errors,
    SessionContext? Session = null);
