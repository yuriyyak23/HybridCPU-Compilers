// HB-INT-04: structural / adversarial contract tests (synthetic data, never a Golden proof).
#if HARDWAREBRIDGE_TESTS
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;
using HybridCPU.Compiler.HardwareBridge.GoldenOracle;

internal static class GoldenRawTraceTests
{
    private static int _assertions;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("HB-INT-04: " + message);
        _assertions++;
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { _assertions++; return; }
        throw new InvalidOperationException("HB-INT-04 expected " + typeof(T).Name + ": " + message);
    }

    public static void Run()
    {
        const string text = "public static class Fixture { public static int Step(int a) => unchecked(a + 1); }";
        byte[] sourceBytes = Encoding.UTF8.GetBytes(text);
        // Deliberately opaque fixture bytes; NO compiled PE and NO real Golden runtime.
        byte[] compiledFixture = Encoding.UTF8.GetBytes("non-executable-assembly-hash-fixture");
        var request = new BridgeSourceRequestV1("TraceFixture", "CSharp15",
            new[] { new BridgeSourceTextV1("golden/Fixture.cs", text) },
            new ReadOnlyMemory<byte>[] { new byte[] { 1, 2, 3, 4 } },
            new[] { new BridgeRootSelectorV1("Fixture", "Step", null,
                Array.Empty<string>(), Array.Empty<string>()) },
            Array.Empty<int>(), Array.Empty<int>());
        BridgeSourceSnapshotV1 source = BridgeSourceSnapshotV1.Capture(
            request, "test-only-source-policy", "fixture-sdk", "fixture-roslyn");
        string assemblyHash = Convert.ToHexString(SHA256.HashData(compiledFixture));
        var initial = new[] { new GoldenTraceCellV1("pc", new(32, true, 0xFFFFFFFF)) };
        var next = new[] { new GoldenTraceCellV1("pc", new(32, true, 0)) };
        var final = new[] { new GoldenTraceCellV1("pc", new(32, true, 1)) };
        var inputs = new[] { new GoldenTraceCellV1("operand", new(32, true, 0x80000000)) };
        var outputs = new[] { new GoldenTraceCellV1("value", new(32, true, 0x80000000)) };
        var effects0 = new[]
        {
            new GoldenTraceEventV1(0, "MemoryWrite", 0, 1, new(64, false, 0x100), new(32, true, 0xFFFFFFFF), null),
            new GoldenTraceEventV1(1, "Retire", 0, 1, null, null, "instruction.0")
        };
        var effects1 = new[]
        {
            new GoldenTraceEventV1(0, "MemoryRead", 1, 0, new(64, false, 0x100), new(32, true, 0xFFFFFFFF), null)
        };
        var choices = new[] { new GoldenTraceChoiceV1(
            "schedule", new(8, false, 1),
            new GoldenWordV1[] { new(8, false, 0), new(8, false, 1) }) };
        var first = new GoldenTraceStepV1(0, 10, "host.step", 0, 1,
            initial, next, inputs, outputs, effects0, effects0.Length,
            GoldenRawTraceCodecV1.ComputeEventsSha256(effects0), choices);
        var second = new GoldenTraceStepV1(1, 12, "host.step", 1, 0,
            next, final, inputs, outputs, effects1, effects1.Length,
            GoldenRawTraceCodecV1.ComputeEventsSha256(effects1),
            Array.Empty<GoldenTraceChoiceV1>());
        GoldenTraceStepV1[] steps = { first, second };

        GoldenRawTraceV1 Make(
            IReadOnlyList<GoldenTraceStepV1>? rows = null,
            IReadOnlyList<GoldenTraceCellV1>? state = null,
            string scope = GoldenRawTraceV1.RawScope,
            string clockDomain = "host.step") =>
            GoldenRawTraceCodecV1.Capture(source,
                new ReadOnlyMemory<byte>[] { sourceBytes }, compiledFixture,
                0, clockDomain, 2, 4, state ?? initial, rows ?? steps, scope);

        GoldenRawTraceV1 observed = Make();
        Require(observed.Schema == GoldenRawTraceV1.SchemaId &&
                observed.Scope == GoldenRawTraceV1.RawScope && observed.Status == "Observed" &&
                observed.Diagnostic is null && observed.Steps.Count == 2,
            "Raw observation was not serialized.");
        Require(observed.InitialState[0].Word.ToSigned64() == -1 &&
                observed.Steps[0].After[0].Word.Bits == 0 &&
                observed.Steps[0].Inputs[0].Word.ToSigned64() == int.MinValue,
            "Two's complement lost.");
        byte[] encoded = observed.ToJsonUtf8();
        Require(encoded.SequenceEqual(Make().ToJsonUtf8()), "Nondeterministic replay bytes.");
        GoldenRawTraceV1 roundtrip = GoldenRawTraceCodecV1.ReadAndValidate(encoded, source, assemblyHash);
        Require(roundtrip.ReplayIdentity == observed.ReplayIdentity &&
                roundtrip.OriginalDocuments[0].Sha256 == source.Documents[0].Sha256,
            "Source-bound replay identity changed.");

        Require(Make(state: new[] { new GoldenTraceCellV1("pc", new(32, true, 5)) })
            .Status == "Blocked", "Changed initial state did not block.");
        Require(Make(new[] { first with { Before = final }, second }).Status == "Blocked",
            "Bad state continuity did not block.");
        Require(Make(new[] { first with { ClockDomain = "unowned.domain" }, second })
            .Status == "Blocked", "Wrong clock domain did not block.");
        Require(Make(clockDomain: "bad/domain").Status == "Blocked",
            "Noncanonical clock domain did not block.");
        Require(Make(new[] { first, second with { LogicalClock = 10 } }).Status == "Blocked",
            "Non-monotonic logical clock did not block.");
        Require(Make(new[] { first with { Context = 2 }, second }).Status == "Blocked",
            "Wrong context did not block.");
        Require(Make(new[] { first with { Lane = 4 }, second }).Status == "Blocked",
            "Wrong lane did not block.");
        Require(Make(new[] { first with { Events = effects0.Reverse().ToArray() }, second })
            .Status == "Blocked", "Reordered memory/retire effects did not block.");
        Require(Make(new[] { first with { Events = effects0.Take(1).ToArray() }, second })
            .Status == "Blocked", "Truncated event list did not block.");
        Require(Make(new[] { first with { ExpectedEventsSha256 = new string('0', 64) }, second })
            .Status == "Blocked", "Mutated effect digest did not block.");
        Require(Make(new[] { first with { Choices = new[] {
            new GoldenTraceChoiceV1("schedule", new(8, false, 2),
                new GoldenWordV1[] { new(8, false, 0), new(8, false, 1) }) } }, second })
            .Status == "Blocked", "Unrecorded nondeterministic value did not block.");
        Require(Make(scope: "cpu-cycle").Status == "Blocked" &&
                Make(scope: "cpu-cycle").Diagnostic!.Contains("Core contract", StringComparison.Ordinal),
            "Missing reset/clock/memory arbitration contract did not block.");
        Require(Make(scope: "rank4").Status == "Blocked",
            "Invented rank4 semantics did not block.");

        GoldenRawTraceV1 mutated = observed with
        {
            Steps = new[] { first with { Outputs = new[] {
                new GoldenTraceCellV1("value", new(32, true, 7)) } }, second }
        };
        Throws<ArgumentException>(() => GoldenRawTraceCodecV1.ReadAndValidate(
            mutated.ToJsonUtf8(), source, assemblyHash), "Replay mutation with stale digest.");
        Throws<ArgumentException>(() => GoldenRawTraceCodecV1.ReadAndValidate(
            encoded, source, new string('0', 64)), "Dangling assembly digest.");
        Throws<ArgumentException>(() => GoldenRawTraceCodecV1.ReadAndValidate(
            encoded, source with { SnapshotDigest = new string('0', 64) }, assemblyHash),
            "Dangling original source snapshot.");
        string encodedText = Encoding.UTF8.GetString(encoded);
        Throws<ArgumentException>(() => GoldenRawTraceCodecV1.ReadAndValidate(
            Encoding.UTF8.GetBytes(encodedText.Replace("\"Scope\":", "\"Delegate\":\"Evil.Run\",\"Scope\":",
                StringComparison.Ordinal)), source, assemblyHash),
            "JSON-requested arbitrary delegate.");
        Throws<ArgumentException>(() => GoldenRawTraceCodecV1.ReadAndValidate(
            Encoding.UTF8.GetBytes(encodedText.Replace("\"Scope\":", "\"Scope\":\"raw-host-steps\",\"Scope\":",
                StringComparison.Ordinal)), source, assemblyHash), "Duplicate JSON property.");
        Throws<ArgumentException>(() => GoldenRawTraceCodecV1.ReadAndValidate(
            Encoding.UTF8.GetBytes(encodedText + " "), source, assemblyHash),
            "Noncanonical serialization.");
        Throws<ArgumentException>(() => GoldenRawTraceCodecV1.Capture(
            source, new ReadOnlyMemory<byte>[] { Encoding.UTF8.GetBytes(text + " ") },
            compiledFixture, 0, "host.step", 2, 4, initial, steps),
            "Mutated original source bytes.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Throws<OperationCanceledException>(() => GoldenRawTraceCodecV1.Capture(
            source, new ReadOnlyMemory<byte>[] { sourceBytes },
            compiledFixture, 0, "host.step", 2, 4, initial, steps,
            cancellationToken: cancelled.Token), "Cancellation.");

        // Synthetic fixture checks only structural integrity; no independent oracle is executed.
        Console.WriteLine("HB-INT-04 fixture replay identity SHA256: " + observed.ReplayIdentity);
        Console.WriteLine("HB-INT-04 fixture canonical JSON SHA256: " + Convert.ToHexString(SHA256.HashData(encoded)));
        Console.WriteLine("HB-INT-04 actual assertions passed: " + _assertions);
    }
}
#endif
