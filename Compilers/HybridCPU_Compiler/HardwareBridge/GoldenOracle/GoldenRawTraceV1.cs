// HB-INT-04: bounded, non-executable Golden raw trace interchange. No hardware authority.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;

namespace HybridCPU.Compiler.HardwareBridge.GoldenOracle;

public sealed record GoldenTraceCellV1(string Name, GoldenWordV1 Word);
public sealed record GoldenTraceEventV1(
    int Order, string Kind, int Context, int Lane,
    GoldenWordV1? Address, GoldenWordV1? Value, string? RetireId);
public sealed record GoldenTraceChoiceV1(
    string Key, GoldenWordV1 Selected, IReadOnlyList<GoldenWordV1> Allowed);
public sealed record GoldenTraceStepV1(
    int Index, long LogicalClock, string ClockDomain, int Context, int Lane,
    IReadOnlyList<GoldenTraceCellV1> Before,
    IReadOnlyList<GoldenTraceCellV1> After,
    IReadOnlyList<GoldenTraceCellV1> Inputs,
    IReadOnlyList<GoldenTraceCellV1> Outputs,
    IReadOnlyList<GoldenTraceEventV1> Events,
    int ExpectedEventCount, string ExpectedEventsSha256,
    IReadOnlyList<GoldenTraceChoiceV1> Choices);

/// <summary>
/// A host-supplied raw observation, NOT an independently attested oracle or cycle-accurate CPU trace.
/// ReplayIdentity is an integrity checksum over these fields, not a signature or proof.
/// </summary>
public sealed record GoldenRawTraceV1(
    string Schema, string Scope, string Status, string? Diagnostic,
    string SourceSnapshotDigest, IReadOnlyList<BridgeDocumentDigestV1> OriginalDocuments,
    string OriginalSourceSetSha256, string CompiledAssemblySha256, int RootIndex,
    string ClockDomain, int ContextCount, int LaneCount,
    IReadOnlyList<GoldenTraceCellV1> InitialState,
    IReadOnlyList<GoldenTraceStepV1> Steps, string ReplayIdentity)
{
    public const string SchemaId = "hybridcpu.hardwarebridge.golden-raw-trace/v1";
    public const string RawScope = "raw-host-steps";
    public byte[] ToJsonUtf8() => JsonSerializer.SerializeToUtf8Bytes(this);
}

/// <summary>
/// Strict codec for explicitly supplied host observations. Never resolves/loads assemblies or invokes
/// delegates; a Core-owned trusted host and independent provenance/equivalence gates are prerequisites.
/// </summary>
public static class GoldenRawTraceCodecV1
{
    public const int MaxTraceBytes = 4 * 1024 * 1024;
    public const int MaxAssemblyBytes = 32 * 1024 * 1024;
    public const int MaxSteps = 512, MaxEventsPerStep = 128, MaxCells = 256, MaxChoices = 32;
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    /// <summary>
    /// Source and assembly bytes are provided by the caller; no disk or Golden process access.
    /// Bytes must match the already-captured source snapshot. CPU timing requests fail closed.
    /// A valid raw result means only that the caller supplied internally consistent observations.
    /// </summary>
    public static GoldenRawTraceV1 Capture(
        BridgeSourceSnapshotV1 source,
        IReadOnlyList<ReadOnlyMemory<byte>> originalDocumentBytes,
        ReadOnlyMemory<byte> compiledAssemblyBytes,
        int rootIndex, string clockDomain, int contextCount, int laneCount,
        IReadOnlyList<GoldenTraceCellV1> initialState,
        IReadOnlyList<GoldenTraceStepV1> recordedSteps,
        string requestedScope = GoldenRawTraceV1.RawScope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(originalDocumentBytes);
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(recordedSteps);
        if (!source.HasConsistentDigest() || originalDocumentBytes.Count != source.Documents.Count ||
            compiledAssemblyBytes.IsEmpty || compiledAssemblyBytes.Length > MaxAssemblyBytes ||
            rootIndex < 0 || rootIndex >= source.Roots.Count)
            throw new ArgumentException("HB4000: Missing or invalid source, assembly or root binding.");
        for (int i = 0; i < source.Documents.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = source.Documents[i];
            ReadOnlyMemory<byte> bytes = originalDocumentBytes[i];
            if (bytes.Length != document.Utf8Bytes ||
                !string.Equals(Hash(bytes.Span), document.Sha256, StringComparison.Ordinal))
                throw new ArgumentException("HB4000: Original source bytes differ from snapshot.");
        }
        // Budget and null preflight before copying caller-owned nested collections.
        if (initialState.Count > MaxCells || recordedSteps.Count > MaxSteps ||
            recordedSteps.Any(s => s is null || s.Before is null || s.After is null ||
                s.Inputs is null || s.Outputs is null || s.Events is null || s.Choices is null ||
                s.Before.Count > MaxCells || s.After.Count > MaxCells ||
                s.Inputs.Count > MaxCells || s.Outputs.Count > MaxCells ||
                s.Events.Count > MaxEventsPerStep || s.Choices.Count > MaxChoices ||
                s.Choices.Any(c => c is null || c.Allowed is null || c.Allowed.Count > 16)))
            throw new ArgumentException("HB4002: Raw trace collection budget or null field.");
        var documents = Array.AsReadOnly(source.Documents.ToArray());
        string sourceSetHash = Hash(JsonSerializer.SerializeToUtf8Bytes(documents));
        string assemblyHash = Hash(compiledAssemblyBytes.Span);
        var initial = FreezeCells(initialState);
        var steps = Array.AsReadOnly(recordedSteps.Select(FreezeStep).ToArray());
        // The Bridge MUST NOT infer reset polarity, physical cycles, memory arbitration or rank4.
        string? failure = requestedScope != GoldenRawTraceV1.RawScope
            ? "HB4001: Unsupported CPU cycle/reset/memory arbitration/rank4 semantics; external Core contract required."
            : null;

        GoldenRawTraceV1 candidate = new(GoldenRawTraceV1.SchemaId, GoldenRawTraceV1.RawScope,
            "Observed", null, source.SnapshotDigest, documents, sourceSetHash, assemblyHash,
            rootIndex, clockDomain, contextCount, laneCount, initial, steps, "");
        if (failure is null)
            failure = CheckSteps(candidate, cancellationToken);
        if (failure is not null)
            candidate = candidate with
            {
                Status = "Blocked", Diagnostic = failure,
                InitialState = Array.AsReadOnly(Array.Empty<GoldenTraceCellV1>()),
                Steps = Array.AsReadOnly(Array.Empty<GoldenTraceStepV1>())
            };
        candidate = candidate with { ReplayIdentity = Digest(candidate) };
        if (candidate.ToJsonUtf8().Length > MaxTraceBytes)
            throw new ArgumentException("HB4002: Raw trace byte budget exceeded.");
        cancellationToken.ThrowIfCancellationRequested();
        return candidate;
    }

    /// <summary>
    /// Reader needs independently obtained expected source snapshot and assembly digest.
    /// Neither stored digest nor checksum authenticates the host or proves Golden/RTL equivalence.
    /// </summary>
    public static GoldenRawTraceV1 ReadAndValidate(
        ReadOnlyMemory<byte> json,
        BridgeSourceSnapshotV1 expectedSource,
        string expectedCompiledAssemblySha256,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(expectedSource);
        if (json.IsEmpty || json.Length > MaxTraceBytes || !expectedSource.HasConsistentDigest() ||
            !IsHash(expectedCompiledAssemblySha256))
            throw new ArgumentException("HB4005: Invalid trace size or unbound expected identities.");
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            CheckNoDuplicateProperties(parsed.RootElement);
            GoldenRawTraceV1? trace = JsonSerializer.Deserialize<GoldenRawTraceV1>(
                json.Span, StrictJson);
            if (trace is null || !ValidIdentity(trace, expectedSource, expectedCompiledAssemblySha256) ||
                !string.Equals(trace.ReplayIdentity, Digest(trace), StringComparison.Ordinal) ||
                !json.Span.SequenceEqual(trace.ToJsonUtf8()))
                throw new ArgumentException("HB4006: Trace schema, source, assembly, checksum or canonical JSON mismatch.");
            cancellationToken.ThrowIfCancellationRequested();
            // Reject mutable lists escaping the deserializer.
            return FreezeTrace(trace);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("HB4006: Malformed or unknown raw trace JSON.", nameof(json), exception);
        }
    }

    /// <summary>
    /// Fail-closed v1 compatibility overload: the reader independently pins the requested
    /// root ordinal, rather than accepting any root in the included source snapshot.
    /// No change to the v1 wire; the original overload is retained for existing consumers.
    /// </summary>
    public static GoldenRawTraceV1 ReadAndValidate(
        ReadOnlyMemory<byte> json, BridgeSourceSnapshotV1 expectedSource,
        string expectedCompiledAssemblySha256, int expectedRootIndex,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedSource is null || !expectedSource.HasConsistentDigest() ||
            expectedRootIndex < 0 || expectedRootIndex >= expectedSource.Roots.Count)
            throw new ArgumentException("HB-R301: Missing independently expected root.");
        GoldenRawTraceV1 trace = ReadAndValidate(
            json, expectedSource, expectedCompiledAssemblySha256, cancellationToken);
        if (trace.RootIndex != expectedRootIndex)
            throw new ArgumentException("HB-R301: Raw trace root does not match independent request.");
        cancellationToken.ThrowIfCancellationRequested();
        return trace;
    }

    public static string ComputeEventsSha256(IReadOnlyList<GoldenTraceEventV1> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count > MaxEventsPerStep)
            throw new ArgumentException("HB4007: Effect count exceeds budget.");
        return Hash(JsonSerializer.SerializeToUtf8Bytes(events));
    }

    private static bool ValidIdentity(GoldenRawTraceV1 trace, BridgeSourceSnapshotV1 source, string assemblyHash)
    {
        if (trace.Schema != GoldenRawTraceV1.SchemaId || trace.Scope != GoldenRawTraceV1.RawScope ||
            trace.SourceSnapshotDigest != source.SnapshotDigest ||
            trace.CompiledAssemblySha256 != assemblyHash ||
            trace.OriginalDocuments is null ||
            !trace.OriginalDocuments.SequenceEqual(source.Documents) ||
            trace.OriginalSourceSetSha256 != Hash(JsonSerializer.SerializeToUtf8Bytes(trace.OriginalDocuments)) ||
            trace.RootIndex < 0 || trace.RootIndex >= source.Roots.Count ||
            !IsLabel(trace.ClockDomain) || trace.ContextCount is < 1 or > 64 ||
            trace.LaneCount is < 1 or > 64 || trace.InitialState is null || trace.Steps is null ||
            (trace.Status != "Observed" && trace.Status != "Blocked"))
            return false;
        if (trace.Status == "Blocked")
            return trace.InitialState.Count == 0 && trace.Steps.Count == 0 &&
                trace.Diagnostic is { Length: > 0 and <= 256 } reason &&
                reason.StartsWith("HB4", StringComparison.Ordinal);
        return trace.Diagnostic is null && CheckSteps(trace, CancellationToken.None) is null;
    }

    private static string? CheckSteps(GoldenRawTraceV1 trace, CancellationToken cancellationToken)
    {
        if (!IsLabel(trace.ClockDomain) || trace.ContextCount is < 1 or > 64 ||
            trace.LaneCount is < 1 or > 64 ||
            !ValidCells(trace.InitialState, requireNonempty: true) ||
            trace.Steps.Count is < 1 or > MaxSteps)
            return "HB4003: Invalid initial state, clock domain, context/lane bounds or empty trace.";
        IReadOnlyList<GoldenTraceCellV1> previous = trace.InitialState;
        long priorClock = -1;
        for (int i = 0; i < trace.Steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GoldenTraceStepV1 step = trace.Steps[i];
            if (step is null || step.Index != i || step.LogicalClock <= priorClock ||
                step.ClockDomain != trace.ClockDomain ||
                step.Context < 0 || step.Context >= trace.ContextCount ||
                step.Lane < 0 || step.Lane >= trace.LaneCount ||
                !ValidCells(step.Before, true) || !SameCells(previous, step.Before) ||
                !ValidCells(step.After, true) || !SameShape(step.Before, step.After) ||
                !ValidCells(step.Inputs, true) || !ValidCells(step.Outputs, true))
                return "HB4003: Invalid initial state continuity, step/clock/context/lane or word.";
            if (step.Events is null || step.Events.Count > MaxEventsPerStep ||
                step.ExpectedEventCount != step.Events.Count ||
                step.ExpectedEventsSha256 != ComputeEventsSha256(step.Events))
                return "HB4004: Truncated or reordered retire/memory effects.";
            for (int j = 0; j < step.Events.Count; j++)
            {
                GoldenTraceEventV1 effect = step.Events[j];
                if (effect is null || effect.Order != j || effect.Context != step.Context ||
                    effect.Lane != step.Lane || !ValidEffect(effect))
                    return "HB4004: Effect order, context, lane or contents mismatch.";
            }
            if (step.Choices is null || step.Choices.Count > MaxChoices ||
                step.Choices.Any(c => c is null || !IsLabel(c.Key) ||
                    c.Selected is null || !c.Selected.IsCanonical || c.Allowed is null ||
                    c.Allowed.Count is < 1 or > 16 ||
                    c.Allowed.Any(w => w is null || !w.IsCanonical) ||
                    !c.Allowed.Contains(c.Selected) ||
                    c.Allowed.Distinct().Count() != c.Allowed.Count) ||
                step.Choices.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != step.Choices.Count)
                return "HB4008: Uncontrolled or ambiguous nondeterminism.";
            previous = step.After;
            priorClock = step.LogicalClock;
        }
        return null;
    }

    private static bool ValidEffect(GoldenTraceEventV1 effect) =>
        effect.Kind switch
        {
            "MemoryRead" or "MemoryWrite" =>
                effect.Address is { IsCanonical: true } &&
                effect.Value is { IsCanonical: true } && effect.RetireId is null,
            "Retire" => effect.Address is null && effect.Value is null && IsLabel(effect.RetireId),
            _ => false
        };

    private static bool ValidCells(IReadOnlyList<GoldenTraceCellV1>? cells, bool requireNonempty) =>
        cells is not null && cells.Count <= MaxCells && (!requireNonempty || cells.Count > 0) &&
        cells.All(c => c is not null && IsLabel(c.Name) && c.Word is { IsCanonical: true }) &&
        cells.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() == cells.Count;

    private static bool SameCells(IReadOnlyList<GoldenTraceCellV1> a, IReadOnlyList<GoldenTraceCellV1> b) =>
        a.SequenceEqual(b);

    private static bool SameShape(IReadOnlyList<GoldenTraceCellV1> before, IReadOnlyList<GoldenTraceCellV1> after) =>
        before.Count == after.Count &&
        before.Zip(after).All(pair =>
            pair.First.Name == pair.Second.Name &&
            pair.First.Word.Width == pair.Second.Word.Width &&
            pair.First.Word.Signed == pair.Second.Word.Signed);

    private static bool IsLabel(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static string Digest(GoldenRawTraceV1 trace) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            trace.Schema, trace.Scope, trace.Status, trace.Diagnostic,
            trace.SourceSnapshotDigest, trace.OriginalDocuments, trace.OriginalSourceSetSha256,
            trace.CompiledAssemblySha256, trace.RootIndex, trace.ClockDomain,
            trace.ContextCount, trace.LaneCount, trace.InitialState, trace.Steps
        }));

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static IReadOnlyList<GoldenTraceCellV1> FreezeCells(IReadOnlyList<GoldenTraceCellV1> cells) =>
        Array.AsReadOnly(cells.ToArray());

    private static GoldenTraceStepV1 FreezeStep(GoldenTraceStepV1 step) =>
        step with
        {
            Before = FreezeCells(step.Before),
            After = FreezeCells(step.After),
            Inputs = FreezeCells(step.Inputs),
            Outputs = FreezeCells(step.Outputs),
            Events = Array.AsReadOnly(step.Events.ToArray()),
            Choices = Array.AsReadOnly(step.Choices.Select(c =>
                c with { Allowed = Array.AsReadOnly(c.Allowed.ToArray()) }).ToArray())
        };

    private static GoldenRawTraceV1 FreezeTrace(GoldenRawTraceV1 trace) =>
        trace with
        {
            OriginalDocuments = Array.AsReadOnly(trace.OriginalDocuments.ToArray()),
            InitialState = FreezeCells(trace.InitialState),
            Steps = Array.AsReadOnly(trace.Steps.Select(FreezeStep).ToArray())
        };

    private static void CheckNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ArgumentException("HB4006: Duplicate JSON property.");
                CheckNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
                CheckNoDuplicateProperties(child);
        }
    }
}
