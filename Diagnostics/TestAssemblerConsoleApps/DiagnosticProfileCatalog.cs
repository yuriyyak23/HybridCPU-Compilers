using System.Collections.ObjectModel;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal static class DiagnosticProfileCatalog
{
    private static readonly IReadOnlyDictionary<string, DiagnosticRunProfile> Profiles = BuildProfiles();
    private static readonly IReadOnlyDictionary<string, string> AliasToProfileId = BuildAliases();

    public static IReadOnlyList<DiagnosticRunProfile> DefaultMatrix => MatrixSpec;

    public static IReadOnlyList<DiagnosticRunProfile> MatrixSmoke { get; } =
    [
        GetRequired("safety"),
        GetRequired("replay-reuse"),
        GetRequired("assistant")
    ];

    public static IReadOnlyList<DiagnosticRunProfile> MatrixRuntime { get; } =
    [
        GetRequired("showcase"),
        GetRequired("alu"),
        GetRequired("vt"),
        GetRequired("novt"),
        GetRequired("max"),
        GetRequired("replay"),
        GetRequired("safety"),
        GetRequired("replay-reuse"),
        GetRequired("assistant"),
        GetRequired("stream-vector"),
        GetRequired("matrix-tile")
    ];

    public static IReadOnlyList<DiagnosticRunProfile> MatrixMemory { get; } =
    [
        GetRequired("lk"),
        GetRequired("bnmcz")
    ];

    public static IReadOnlyList<DiagnosticRunProfile> MatrixFull { get; } =
    [
        GetRequired("showcase"),
        GetRequired("vt"),
        GetRequired("novt"),
        GetRequired("alu"),
        GetRequired("max"),
        GetRequired("lk"),
        GetRequired("bnmcz"),
        GetRequired("replay"),
        GetRequired("safety"),
        GetRequired("replay-reuse"),
        GetRequired("assistant"),
        GetRequired("stream-vector"),
        GetRequired("matrix-tile")
    ];

    public static IReadOnlyList<DiagnosticRunProfile> MatrixSpec { get; } =
    [
        GetRequired("alu"),
        GetRequired("novt"),
        GetRequired("vt"),
        GetRequired("max"),
        GetRequired("lk"),
        GetRequired("bnmcz"),
        GetRequired("replay"),
        GetRequired("safety"),
        GetRequired("replay-reuse"),
        GetRequired("assistant"),
        GetRequired("stream-vector"),
        GetRequired("matrix-tile")
    ];

    public static IReadOnlyList<DiagnosticRunProfile> MatrixWide { get; } =
    [
        GetRequired("showcase-long"),
        GetRequired("lk-long"),
        GetRequired("bnmcz-long"),
        GetRequired("replay")
    ];

    public static IReadOnlyList<DiagnosticRunProfile> WhiteBookSmoke { get; } =
    [
        GetRequired("whitebook-contract")
    ];

    public static IReadOnlyList<DiagnosticRunProfile> WhiteBookFull { get; } =
    [
        GetRequired("whitebook-contract"),
        GetRequired("safety"),
        GetRequired("replay-reuse"),
        GetRequired("assistant"),
        GetRequired("stream-vector")
    ];

    public static DiagnosticRunProfile Resolve(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            throw new ArgumentException("Mode argument cannot be empty.", nameof(arg));
        }

        string normalized = Normalize(arg);
        if (!AliasToProfileId.TryGetValue(normalized, out string? profileId))
        {
            throw new ArgumentException(
                "Expected one of: showcase, showcase-long, full, native, vt, with-vt, vt-fsp, spec-rate, novt, no-vt, without-vt, single-thread-vector, spec-vector, alu, scalar-baseline, spec-int, max, max-ipc, throughput-max, spec-mix, lk, bank-pressure-lh, lk-long, spec-mem-lh, bnmcz, bank-pressure-bnmcz, bnmcz-long, spec-mem-bank, replay, replay-phase, replay-pair, safety, replay-reuse, assistant, stream-vector, matrix-tile, whitebook-contract.",
                nameof(arg));
        }

        return GetRequired(profileId);
    }

    public static IReadOnlyList<DiagnosticRunProfile> ResolveProfiles(params string[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return ids.Select(GetRequired).ToArray();
    }

    private static DiagnosticRunProfile GetRequired(string id)
    {
        if (!Profiles.TryGetValue(id, out DiagnosticRunProfile? profile))
        {
            throw new InvalidOperationException($"Diagnostic profile '{id}' is not registered.");
        }

        return profile;
    }

    private static IReadOnlyDictionary<string, DiagnosticRunProfile> BuildProfiles()
    {
        Dictionary<string, DiagnosticRunProfile> profiles = new Dictionary<string, DiagnosticRunProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["showcase"] = DiagnosticRunProfile.CreateSimple("showcase", "Showcase Runtime Audit (SPEC-like control/runtime slice)", SimpleAsmAppMode.RefactorShowcase, wallClockTimeoutMs: 30000, heartbeatIntervalMs: 1000, heartbeatCycleStride: 2048),
            ["showcase-long"] = DiagnosticRunProfile.CreateSimple("showcase-long", "Showcase Runtime Audit (SPEC-like control/runtime slice, Long Budget)", SimpleAsmAppMode.RefactorShowcase, wallClockTimeoutMs: 120000, heartbeatIntervalMs: 1000, heartbeatCycleStride: 2048),
            ["native"] = DiagnosticRunProfile.CreateSimple("native", "Primary Native Diagnostics", SimpleAsmAppMode.PackedMixedEnvelope, wallClockTimeoutMs: 30000),
            ["vt"] = DiagnosticRunProfile.CreateSimple("vt", "VT + FSP Packed Scalar (SPEC-like rate slice)", SimpleAsmAppMode.WithVirtualThreads, wallClockTimeoutMs: 30000, heartbeatIntervalMs: 1000, heartbeatCycleStride: 2048),
            ["novt"] = DiagnosticRunProfile.CreateSimple("novt", "Single-Thread Vector Probe (SPEC-like vector slice)", SimpleAsmAppMode.WithoutVirtualThreads, wallClockTimeoutMs: 30000),
            ["alu"] = DiagnosticRunProfile.CreateSimple("alu", "Scalar Baseline (SPEC-like integer slice)", SimpleAsmAppMode.SingleThreadNoVector, wallClockTimeoutMs: 30000),
            ["max"] = DiagnosticRunProfile.CreateSimple("max", "Packed Mixed Envelope + Vector Probe (same steady-state ceiling as VT, not an absolute maximum)", SimpleAsmAppMode.PackedMixedEnvelope, wallClockTimeoutMs: 30000),
            ["lk"] = DiagnosticRunProfile.CreateSimple("lk", "Bank Pressure Latency-Hiding Load Kernel (SPEC-like memory slice)", SimpleAsmAppMode.Lk, wallClockTimeoutMs: 30000, heartbeatIntervalMs: 1000, heartbeatCycleStride: 2048),
            ["lk-long"] = DiagnosticRunProfile.CreateSimple("lk-long", "Bank Pressure Latency-Hiding Load Kernel (SPEC-like memory slice, Long Budget)", SimpleAsmAppMode.Lk, wallClockTimeoutMs: 90000, heartbeatIntervalMs: 1000, heartbeatCycleStride: 2048),
            ["bnmcz"] = DiagnosticRunProfile.CreateSimple("bnmcz", "Bank Pressure No-Conflict Mixed Zoo (SPEC-like bank-rotated memory slice)", SimpleAsmAppMode.Bnmcz, wallClockTimeoutMs: 30000, heartbeatIntervalMs: 1000, heartbeatCycleStride: 2048),
            ["bnmcz-long"] = DiagnosticRunProfile.CreateSimple("bnmcz-long", "Bank Pressure No-Conflict Mixed Zoo (SPEC-like bank-rotated memory slice, Long Budget)", SimpleAsmAppMode.Bnmcz, wallClockTimeoutMs: 90000, heartbeatIntervalMs: 1000, heartbeatCycleStride: 2048),
            ["replay"] = DiagnosticRunProfile.CreateReplayPair(displayName: "Replay Phase Pair (SPEC-like scheduler certificate slice)"),
            ["safety"] = DiagnosticRunProfile.CreateArchitectural("safety", "SafetyVerifier Negative Controls", DiagnosticWorkloadKind.SafetyVerifierNegativeControls),
            ["replay-reuse"] = DiagnosticRunProfile.CreateArchitectural("replay-reuse", "Replay Template Reuse Diagnostics", DiagnosticWorkloadKind.ReplayReuseDiagnostics),
            ["assistant"] = DiagnosticRunProfile.CreateArchitectural("assistant", "Assistant Decision Matrix", DiagnosticWorkloadKind.AssistantDecisionMatrix),
            ["stream-vector"] = DiagnosticRunProfile.CreateArchitectural("stream-vector", "Stream/Vector SPEC-like Suite (SGEMM/DSP/compress/crypto/stencil + lane6 token)", DiagnosticWorkloadKind.StreamVectorSpecSuite, wallClockTimeoutMs: 60000, workloadIterations: 250UL),
            ["matrix-tile"] = DiagnosticRunProfile.CreateArchitectural("matrix-tile", "MatrixTile SPEC-like Pressure Suite (transport, compute, retire, replay, fail-closed)", DiagnosticWorkloadKind.MatrixTileSpecSuite, wallClockTimeoutMs: 60000, workloadIterations: 500UL),
            ["whitebook-contract"] = DiagnosticRunProfile.CreateArchitectural("whitebook-contract", "Stream WhiteBook Contract Diagnostics", DiagnosticWorkloadKind.WhiteBookContractDiagnostics, wallClockTimeoutMs: 20000, workloadIterations: 200UL)
        };

        return new ReadOnlyDictionary<string, DiagnosticRunProfile>(profiles);
    }

    private static IReadOnlyDictionary<string, string> BuildAliases()
    {
        Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["showcase"] = "showcase",
            ["full"] = "showcase",
            ["showcase-runtime"] = "showcase",
            ["showcase-runtime-long"] = "showcase-long",
            ["showcase-long"] = "showcase-long",
            ["native"] = "native",
            ["vt"] = "vt",
            ["with-vt"] = "vt",
            ["vt-fsp"] = "vt",
            ["spec-rate"] = "vt",
            ["novt"] = "novt",
            ["no-vt"] = "novt",
            ["without-vt"] = "novt",
            ["single-thread-vector"] = "novt",
            ["vector-single"] = "novt",
            ["spec-vector"] = "novt",
            ["alu"] = "alu",
            ["single-thread-no-vector"] = "alu",
            ["scalar-baseline"] = "alu",
            ["spec-int"] = "alu",
            ["max"] = "max",
            ["max-ipc"] = "max",
            ["throughput-max"] = "max",
            ["packed-vector"] = "max",
            ["spec-mix"] = "max",
            ["lk"] = "lk",
            ["lk-long"] = "lk-long",
            ["bank-pressure-lh"] = "lk",
            ["bank-pressure-lh-long"] = "lk-long",
            ["spec-mem-lh"] = "lk",
            ["bnmcz"] = "bnmcz",
            ["bnmcz-long"] = "bnmcz-long",
            ["bank-pressure-bnmcz"] = "bnmcz",
            ["bank-pressure-bnmcz-long"] = "bnmcz-long",
            ["spec-mem-bank"] = "bnmcz",
            ["replay"] = "replay",
            ["replay-pair"] = "replay",
            ["replay-phase"] = "replay",
            ["safety"] = "safety",
            ["safety-negative"] = "safety",
            ["safety-verifier"] = "safety",
            ["negative-controls"] = "safety",
            ["replay-reuse"] = "replay-reuse",
            ["replay-template"] = "replay-reuse",
            ["replay-templates"] = "replay-reuse",
            ["assistant"] = "assistant",
            ["assistant-matrix"] = "assistant",
            ["assist-matrix"] = "assistant",
            ["stream-vector"] = "stream-vector",
            ["stream-suite"] = "stream-vector",
            ["vector-suite"] = "stream-vector",
            ["spec-stream"] = "stream-vector",
            ["spec-vector-suite"] = "stream-vector",
            ["spec-stream-vector"] = "stream-vector",
            ["matrix-tile"] = "matrix-tile",
            ["mtile"] = "matrix-tile",
            ["spec-matrix-tile"] = "matrix-tile",
            ["matrix-tile-pressure"] = "matrix-tile",
            ["whitebook-contract"] = "whitebook-contract",
            ["whitebook"] = "whitebook-contract",
            ["stream-whitebook"] = "whitebook-contract",
            ["stream-whitebook-contract"] = "whitebook-contract",
            ["whitebook-boundaries"] = "whitebook-contract"
        };

        return new ReadOnlyDictionary<string, string>(aliases);
    }

    private static string Normalize(string arg)
    {
        string trimmed = arg.Trim();
        while (trimmed.StartsWith("-", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..];
        }

        return trimmed.ToLowerInvariant();
    }
}
