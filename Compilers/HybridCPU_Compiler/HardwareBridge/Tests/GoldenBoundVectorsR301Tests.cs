// R3-01 tests: an actual call to an explicitly compiled fixture delegate, never Golden CPU.
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.HardwareBridge.GoldenOracle;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;

internal static class R301ApprovedFixture
{
    public static uint AddWord(uint a, uint b) => unchecked(a + b);
    public static uint WrongMethod(uint a, uint b) => unchecked(a - b);
    public static readonly ManualResetEventSlim Entered = new(false);
    public static readonly ManualResetEventSlim Continue = new(false);
    public static uint PausingAddWord(uint a, uint b)
    {
        Entered.Set();
        if (!Continue.Wait(TimeSpan.FromSeconds(4)))
            throw new TimeoutException("Test synchronization timed out.");
        return unchecked(a + b);
    }
    public static uint SlowAddWord(uint a, uint b)
    {
        Thread.Sleep(30); // timeout refusal fixture only; execution cannot be preempted
        return unchecked(a + b);
    }
}

internal static class GoldenBoundVectorsR301Tests
{
    private static int _assertions;
    private static void Require(bool value, string test)
    {
        if (!value) throw new InvalidOperationException("R3-01 assertion: " + test);
        _assertions++;
    }
    private static void Reject<T>(Action action, string test) where T : Exception
    {
        try { action(); }
        catch (T) { _assertions++; return; }
        throw new InvalidOperationException("R3-01 did not reject " + test);
    }
    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    public static void Run()
    {
        Assembly hostAssembly = typeof(R301ApprovedFixture).Assembly;
        byte[] pe = File.ReadAllBytes(hostAssembly.Location);
        string hostSha = Hash(pe);
        string text = "internal static class R301ApprovedFixture { public static uint AddWord(uint a,uint b) => unchecked(a+b); }";
        byte[] sourceBytes = Encoding.UTF8.GetBytes(text);
        MethodInfo approved = typeof(R301ApprovedFixture).GetMethod(
            nameof(R301ApprovedFixture.AddWord))!;
        MethodInfo slow = typeof(R301ApprovedFixture).GetMethod(
            nameof(R301ApprovedFixture.SlowAddWord))!;
        MethodInfo pausing = typeof(R301ApprovedFixture).GetMethod(
            nameof(R301ApprovedFixture.PausingAddWord))!;
        BridgeSourceSnapshotV1 source = BridgeSourceSnapshotV1.Capture(
            new BridgeSourceRequestV1(hostAssembly.GetName().Name!, "CSharp15",
                new[] { new BridgeSourceTextV1("fixture/R301ApprovedFixture.cs", text) },
                Array.Empty<ReadOnlyMemory<byte>>(),
                new[] {
                    new BridgeRootSelectorV1(typeof(R301ApprovedFixture).FullName!,
                        approved.Name, approved.MetadataToken,
                        Array.Empty<string>(), Array.Empty<string>()),
                    new BridgeRootSelectorV1(typeof(R301ApprovedFixture).FullName!,
                        slow.Name, slow.MetadataToken,
                        Array.Empty<string>(), Array.Empty<string>()),
                    new BridgeRootSelectorV1(typeof(R301ApprovedFixture).FullName!,
                        pausing.Name, pausing.MetadataToken,
                        Array.Empty<string>(), Array.Empty<string>())
                }, Array.Empty<int>(), Array.Empty<int>()),
            "fixture-only-unqualified", "fixture-csc-unqualified", "fixture-roslyn-unqualified");
        ReadOnlyMemory<byte>[] docs = { sourceBytes };
        var vectors = new GoldenVectorV1[]
        {
            new(new(32, false, 0xFFFFFFFF), new(32, false, 1)),
            new(new(32, false, 0x7FFFFFFF), new(32, false, 1)),
            new(new(32, false, 0x80000000), new(32, false, 0x80000000))
        };
        const string label = "r301-explicit-compiled-fixture-not-golden";
        Func<uint, uint, uint> fn = R301ApprovedFixture.AddWord;

        R301ScalarSessionV2 Capture(
            IReadOnlyList<GoldenVectorV1>? req = null,
            ReadOnlyMemory<byte>? assembly = null,
            BridgeSourceSnapshotV1? expectedSource = null,
            ReadOnlyMemory<byte>? originalBytes = null,
            int root = 0,
            Func<uint, uint, uint>? method = null,
            TimeSpan? budget = null,
            CancellationToken cancellationToken = default) =>
            R301ScalarSessionCodecV2.ObserveAndCapture(
                expectedSource ?? source,
                new ReadOnlyMemory<byte>[] { originalBytes ?? sourceBytes },
                assembly ?? pe, root, req ?? vectors, method ?? fn, label,
                budget ?? TimeSpan.FromSeconds(5), cancellationToken);

        R301ScalarSessionV2 observed = Capture();
        byte[] json = observed.ToJsonUtf8();
        Require(observed.Status == "Observed" &&
                observed.Scope == R301ScalarSessionV2.ScalarScope &&
                observed.BoundVectors.Observation.Status == "Observed" &&
                observed.BoundVectors.Observation.Cases.Count == vectors.Length,
            "Executed scalar fixture receipts not Observed.");
        Require(observed.BoundVectors.Observation.Cases[0].Output!.Bits == 0 &&
                observed.BoundVectors.Observation.Cases[1].Output!.Bits == 0x80000000 &&
                observed.BoundVectors.Observation.Cases[2].Output!.Bits == 0,
            "Executed AddWord32 outputs.");
        Require(observed.HostAssemblySha256 == hostSha &&
                observed.HostMethodToken == approved.MetadataToken &&
                observed.HostModuleVersionId == approved.Module.ModuleVersionId.ToString("D"),
            "Actually loaded PE and method identity.");
        var parsed = R301ScalarSessionCodecV2.ReadAndValidate(
            json, source, pe, 0, label, vectors, fn);
        Require(parsed.ToJsonUtf8().SequenceEqual(json) &&
                parsed.ReplayIdentity == observed.ReplayIdentity,
            "Strict immutable roundtrip.");
        Require(Capture().ToJsonUtf8().SequenceEqual(json), "Deterministic output.");

        Reject<ArgumentException>(() => Capture(
            req: new[] { vectors[0], vectors[0] }), "duplicate vector request");
        Reject<ArgumentException>(() => Capture(req: new[]
        {
            new GoldenVectorV1(new(16, false, 1), new(32, false, 1))
        }), "illegal scalar bit width");
        Reject<ArgumentException>(() => Capture(
            req: Enumerable.Repeat(vectors[0], 513).ToArray()), "unbounded vectors");
        Reject<ArgumentException>(() => Capture(
            originalBytes: Encoding.UTF8.GetBytes(text + " ")), "mutated source byte");
        Reject<ArgumentException>(() => Capture(
            expectedSource: source with { SnapshotDigest = new string('0', 64) }),
            "mutated source digest");
        byte[] alteredPe = (byte[])pe.Clone();
        alteredPe[alteredPe.Length - 1] ^= 1;
        Reject<ArgumentException>(() => Capture(assembly: alteredPe), "altered assembly SHA");
        Reject<ArgumentException>(() => Capture(root: 1), "switched method root");
        Reject<ArgumentException>(() => Capture(method: R301ApprovedFixture.WrongMethod),
            "wrong executable root");
        Reject<ArgumentException>(() => Capture(budget: TimeSpan.Zero), "zero time budget");
        Reject<ArgumentException>(() => Capture(budget: TimeSpan.FromSeconds(31)),
            "unbounded time budget");
        Reject<TimeoutException>(() => Capture(root: 1,
            method: R301ApprovedFixture.SlowAddWord,
            budget: TimeSpan.FromMilliseconds(1)), "deadline after non-preemptive delegate");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Reject<OperationCanceledException>(() => Capture(cancellationToken: cancelled.Token),
            "cancelled observation");

        Reject<ArgumentException>(() => R301ScalarSessionCodecV2.ReadAndValidate(
            json, source, alteredPe, 0, label, vectors, fn), "replay altered PE");
        Reject<ArgumentException>(() => R301ScalarSessionCodecV2.ReadAndValidate(
            json, source, pe, 1, label, vectors, fn), "replay switched root");
        Reject<ArgumentException>(() => R301ScalarSessionCodecV2.ReadAndValidate(
            json, source, pe, 0, label, vectors.Reverse().ToArray(), fn),
            "replay reordered vector");
        Reject<ArgumentException>(() => R301ScalarSessionCodecV2.ReadAndValidate(
            json, source, pe, 0, "forged-reviewed-host", vectors, fn),
            "replay wrong reviewer label");
        string raw = Encoding.UTF8.GetString(json);
        Reject<ArgumentException>(() => R301ScalarSessionCodecV2.ReadAndValidate(
            Encoding.UTF8.GetBytes(raw.Replace("\"Scope\":",
                "\"Scope\":\"evil\",\"Scope\":", StringComparison.Ordinal)),
            source, pe, 0, label, vectors, fn), "duplicate field");
        Reject<ArgumentException>(() => R301ScalarSessionCodecV2.ReadAndValidate(
            Encoding.UTF8.GetBytes(raw.Replace("\"Scope\":",
                "\"ArbitraryAssemblyLoad\":true,\"Scope\":", StringComparison.Ordinal)),
            source, pe, 0, label, vectors, fn), "arbitrary executable field");
        Reject<ArgumentException>(() => R301ScalarSessionCodecV2.ReadAndValidate(
            Encoding.UTF8.GetBytes(raw.Replace("\"Status\":\"Observed\"",
                "\"Status\":\"Verified\"", StringComparison.Ordinal)),
            source, pe, 0, label, vectors, fn), "fake Verified status");
        Reject<ArgumentException>(() => R301ScalarSessionCodecV2.ReadAndValidate(
            Encoding.UTF8.GetBytes(raw.Replace("\"Status\":\"Observed\"",
                "\"Status\":\"PASS\"", StringComparison.Ordinal)),
            source, pe, 0, label, vectors, fn), "fake PASS status");
        Reject<ArgumentException>(() => R301ScalarSessionCodecV2.ReadAndValidate(
            Encoding.UTF8.GetBytes(raw + " "), source, pe, 0, label, vectors, fn),
            "noncanonical whitespace");
        Reject<ArgumentException>(() => R301ScalarSessionCodecV2.ReadAndValidate(
            json[..^8], source, pe, 0, label, vectors, fn), "truncated receipt");
        var forgedCase = observed with
        {
            BoundVectors = observed.BoundVectors with
            {
                Observation = observed.BoundVectors.Observation with
                {
                    Cases = observed.BoundVectors.Observation.Cases.Select((c, i) =>
                        i == 0 ? c with { Output = new(32, false, 17) } : c).ToArray()
                }
            }
        };
        Reject<ArgumentException>(() => R301ScalarSessionCodecV2.ReadAndValidate(
            forgedCase.ToJsonUtf8(), source, pe, 0, label, vectors, fn),
            "post-hoc output mutation without new digest");
        var callerList = vectors.ToList();
        byte[] before = Capture(req: callerList).ToJsonUtf8();
        callerList[0] = new(new(32, false, 42), new(32, false, 23));
        Require(Capture().ToJsonUtf8().SequenceEqual(before),
            "Original receipt independent of mutated caller list");

        // Concurrent caller mutation after the first real delegate entry must not
        // replace the pre-execution frozen independent vector request.
        var mutableRequest = vectors.ToList();
        R301ApprovedFixture.Entered.Reset();
        R301ApprovedFixture.Continue.Reset();
        Task<R301ScalarSessionV2> pending = Task.Run(() => Capture(
            req: mutableRequest, root: 2, method: R301ApprovedFixture.PausingAddWord));
        bool entered = R301ApprovedFixture.Entered.Wait(TimeSpan.FromSeconds(3));
        try
        {
            Require(entered, "Pausing fixture host must have entered first call.");
            mutableRequest[0] = new(new(32, false, 77), new(32, false, 88));
        }
        finally { R301ApprovedFixture.Continue.Set(); }
        R301ScalarSessionV2 frozenWhileExecuting = pending.GetAwaiter().GetResult();
        Require(frozenWhileExecuting.BoundVectors.RequestedVectors[0] == vectors[0] &&
                frozenWhileExecuting.BoundVectors.Observation.Cases[0].A == vectors[0].A &&
                frozenWhileExecuting.BoundVectors.Observation.Cases[0].B == vectors[0].B,
            "Concurrent input mutation changed an already-executing observation.");

        // Synthetic fixture receipt only: create atomically in runner-owned TEMP, mark read-only.
        // No Golden checkout or raw Golden artifact is read/written/uploaded.
        string receiptPath = Path.Combine(Path.GetTempPath(),
            "r301-fixture-" + Guid.NewGuid().ToString("N") + ".json");
        using (var output = new FileStream(
            receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            output.Write(json);
            output.Flush(flushToDisk: true);
        }
        File.SetAttributes(receiptPath, FileAttributes.ReadOnly);
        byte[] persistedFixture = File.ReadAllBytes(receiptPath);
        Require(persistedFixture.AsSpan().SequenceEqual(json) &&
                Hash(persistedFixture) == Hash(json),
            "Immutable fixture receipt file bytes differ from captured JSON");

        Console.WriteLine("R3-01 HostMethod: " + observed.HostMethodId);
        Console.WriteLine("R3-01 Host module MVID: " + observed.HostModuleVersionId);
        Console.WriteLine("R3-01 Host method token: " + observed.HostMethodToken);
        Console.WriteLine("R3-01 Synthetic receipt file SHA256: " + Hash(persistedFixture));
        Console.WriteLine("R3-01 Executed fixture host PE SHA256: " + observed.HostAssemblySha256);
        Console.WriteLine("R3-01 Executed fixture receipt JSON SHA256: " + Hash(json));
        Console.WriteLine("R3-01 Receipt replay SHA256: " + observed.ReplayIdentity);
        Console.WriteLine("R3-01 Golden actual: NotRun; Core evidence authority: Blocked");
        Console.WriteLine("R3-01 scalar negative/positive assertions passed: " + _assertions);
    }
}

internal static class GoldenBoundVectorsR301Program
{
    public static int Main()
    {
        try { GoldenBoundVectorsR301Tests.Run(); return 0; }
        catch (Exception ex)
        {
            Console.Error.WriteLine("R3-01 tests failed: " + ex);
            return 1;
        }
    }
}
