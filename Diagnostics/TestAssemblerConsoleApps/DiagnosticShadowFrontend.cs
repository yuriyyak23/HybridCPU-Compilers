namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

/// <summary>Host configuration for one isolated diagnostic worker, not CPU authority.</summary>
internal static class DiagnosticShadowFrontend
{
    public const string ArtifactFileName = "shadow_frontend.json";
    private static readonly List<WeakReference<Processor.CPU_Core>> Cores = new();
    private static ShadowFrontendMode _mode;

    public static ShadowFrontendMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "off" => ShadowFrontendMode.Off,
        "observe" => ShadowFrontendMode.Observe,
        "cold-warm" or "coldwarm" => ShadowFrontendMode.ColdWarm,
        _ => throw new ArgumentException("Expected '--shadow-frontend <off|observe|cold-warm>'.")
    };

    public static void BeginWorker(ShadowFrontendMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        _mode = mode;
        Cores.Clear();
    }

    public static Processor.CPU_Core ConfigureNewCore(Processor.CPU_Core core)
    {
        // Bootstrap/factories call this before execution. Re-registering an existing
        // core must not reset its cache lifetime or erase warm-reuse evidence.
        if (!Cores.Any(reference => reference.TryGetTarget(out var existing) && ReferenceEquals(existing, core)))
        {
            core.ConfigureShadowFrontend(_mode);
            Cores.Add(new(core));
        }
        return core;
    }

    public static void ConfigureBootstrappedCores()
    {
        foreach (var core in Processor.CPU_Cores)
            if (core is not null) ConfigureNewCore(core);
    }

    public static void WriteReport(DiagnosticArtifactWriter writer)
    {
        var snapshots = Cores.Select((reference, index) => new
        {
            Registration = index,
            Snapshot = reference.TryGetTarget(out var core) ? core.ReadShadowFrontendSnapshot() : null
        }).ToArray();
        writer.WriteJson(ArtifactFileName, new
        {
            Schema = "diagnostic-shadow-frontend/v1",
            RequestedMode = _mode.ToString(),
            Cores = snapshots,
            Scope = "End-of-worker snapshots of registered cores still alive. Null means collected, not zero activity. Direct micro-op probes may never enter fetch/decode. No HOT or full safety/performance claim."
        });
        Console.WriteLine($"ShadowFrontend: {_mode}; snapshots={snapshots.Count(item => item.Snapshot is not null)}/{snapshots.Length}; artifact={ArtifactFileName}");
    }
}
