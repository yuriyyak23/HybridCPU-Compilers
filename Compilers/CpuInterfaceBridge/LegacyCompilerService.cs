using System.Diagnostics;
using System.Threading.Channels;
using HybridCPU.Compiler.Core.IR;

namespace CpuInterfaceBridge;

/// <summary>
/// Адаптер компиляции поверх текущего compiler-контура и CompilerResultStore.
/// </summary>
public sealed class LegacyCompilerService : ICompilerService
{
    private static readonly ActivitySource ActivitySource = new("CpuInterfaceBridge.Compiler");
    private readonly Func<SourceCode, CompileOptions, CancellationToken, ValueTask> _compileAction;
    private Func<SourceCode, CompileOptions, CancellationToken, ValueTask<IReadOnlyList<CompiledProgram>>>? _ownedResults;

    /// <summary>Creates an explicit command adapter whose results belong to this invocation, without a global cache.</summary>
    public static LegacyCompilerService WithOwnedResults(
        Func<SourceCode, CompileOptions, CancellationToken, ValueTask<IReadOnlyList<CompiledProgram>>> compile)
    {
        ArgumentNullException.ThrowIfNull(compile);
        return new LegacyCompilerService { _ownedResults = compile };
    }

    /// <summary>
    /// Создаёт адаптер компиляции.
    /// </summary>
    /// <param name="compileAction">Legacy-действие компиляции, которое заполняет CompilerResultStore.</param>
    public LegacyCompilerService(Func<SourceCode, CompileOptions, CancellationToken, ValueTask>? compileAction = null)
    {
        _compileAction = compileAction ?? ((_, _, _) => throw new InvalidOperationException("No compilation action configured."));
    }

    /// <inheritdoc />
    public async Task<CompileResult> CompileAsync(SourceCode source, CompileOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(source.Text))
        {
            throw new ArgumentException("Source text must not be empty.", nameof(source));
        }

        using CancellationTokenSource? timeoutCts = options.Timeout is { } timeout
            ? new CancellationTokenSource(timeout)
            : null;
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts?.Token ?? CancellationToken.None);

        CancellationToken effectiveToken = linkedCts.Token;
        Stopwatch stopwatch = Stopwatch.StartNew();
        using Activity? activity = ActivitySource.StartActivity("Compile");
        activity?.SetTag("cpu.bridge.source", source.SourceName);
        SessionContext session = options.Session ?? new SessionContext
        {
            CorrelationId = activity?.TraceId.ToString() ?? string.Empty
        };

        try
        {
            effectiveToken.ThrowIfCancellationRequested();
            if (_ownedResults is null)
            {
                await _compileAction(source, options, effectiveToken).ConfigureAwait(false);
                effectiveToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Legacy action completed, but CompilerResultStore cannot prove invocation-owned freshness. Use WithOwnedResults.");
            }
            List<CompiledProgram> programs = (await _ownedResults(source, options, effectiveToken).ConfigureAwait(false)).ToList();
            effectiveToken.ThrowIfCancellationRequested();
            IReadOnlyList<string> errors = programs.Count > 0
                ? Array.Empty<string>()
                : ["Compilation action returned no invocation-owned artifacts."];

            return new CompileResult(
                programs,
                new CompilationStatistics
                {
                    Elapsed = stopwatch.Elapsed,
                    CompiledVirtualThreadCount = programs.Count,
                    ErrorCount = errors.Count
                },
                programs.Count > 0,
                errors,
                session);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CompileResult(
                Array.Empty<CompiledProgram>(),
                new CompilationStatistics
                {
                    Elapsed = stopwatch.Elapsed,
                    CompiledVirtualThreadCount = 0,
                    ErrorCount = 1
                },
                false,
                [ex.Message],
                session);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<CompilerMessage> CompileLogStream(SourceCode source, CompileOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        Channel<CompilerMessage> channel = Channel.CreateUnbounded<CompilerMessage>();

        _ = Task.Run(async () =>
        {
            try
            {
                await channel.Writer.WriteAsync(new CompilerMessage(
                    CompilerMessageLevel.Info,
                    "Compilation started.",
                    TimestampUtc: DateTimeOffset.UtcNow,
                    ActivityId: Activity.Current?.Id), cancellationToken).ConfigureAwait(false);
                CompileResult result = await CompileAsync(source, options, cancellationToken).ConfigureAwait(false);

                if (result.Success)
                {
                    await channel.Writer.WriteAsync(
                        new CompilerMessage(
                            CompilerMessageLevel.Info,
                            $"Compilation finished. Artifacts: {result.Programs.Count}.",
                            TimestampUtc: DateTimeOffset.UtcNow,
                            ActivityId: Activity.Current?.Id),
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    foreach (string error in result.Errors)
                    {
                        await channel.Writer.WriteAsync(new CompilerMessage(
                            CompilerMessageLevel.Error,
                            error,
                            TimestampUtc: DateTimeOffset.UtcNow,
                            ActivityId: Activity.Current?.Id), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await channel.Writer.WriteAsync(new CompilerMessage(
                    CompilerMessageLevel.Warning,
                    "Compilation canceled.",
                    TimestampUtc: DateTimeOffset.UtcNow,
                    ActivityId: Activity.Current?.Id), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await channel.Writer.WriteAsync(new CompilerMessage(
                    CompilerMessageLevel.Error,
                    ex.Message,
                    TimestampUtc: DateTimeOffset.UtcNow,
                    ActivityId: Activity.Current?.Id), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    private static List<CompiledProgram> ReadPrograms(CompileOptions options)
    {
        IEnumerable<int> requestedVtIds = options.RequestedVirtualThreadIds?.Distinct() ?? Enumerable.Range(0, Math.Max(1, options.VirtualThreadCount));

        List<CompiledProgram> programs = new List<CompiledProgram>();
        foreach (int vtId in requestedVtIds)
        {
            if (vtId < 0)
            {
                continue;
            }

            if (CompilerResultStore.TryGetResult(vtId, out HybridCpuCompiledProgram? program))
            {
                programs.Add(new CompiledProgram(vtId, program));
            }
        }

        return programs;
    }
}
