// HB-INT-04 continuation: real managed transport tests, NOT a Golden/source/RTL proof.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;
using HybridCPU.Compiler.HardwareBridge.GoldenOracle;

internal static class GoldenBoundVectorsTests
{
    private static int _assertions;
    private static void Require(bool condition, string context)
    {
        if (!condition) throw new InvalidOperationException("HB-INT-04 vectors: " + context);
        _assertions++;
    }
    private static void Reject<T>(Action action, string context) where T : Exception
    {
        try { action(); }
        catch (T) { _assertions++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + context);
    }

    public static void Run()
    {
        const string goldenText =
            "public static class Fixture { public static uint AddWord(uint a,uint b) => unchecked(a+b); }";
        byte[] original = Encoding.UTF8.GetBytes(goldenText);
        byte[] assemblyBytes = Encoding.UTF8.GetBytes("unqualified-fixture-assembly-bytes-not-a-real-pe");
        string assemblyHash = Convert.ToHexString(SHA256.HashData(assemblyBytes));
        BridgeSourceSnapshotV1 source = BridgeSourceSnapshotV1.Capture(
            new BridgeSourceRequestV1("Fixture", "CSharp15",
                new[] { new BridgeSourceTextV1("golden/Fixture.cs", goldenText) },
                new ReadOnlyMemory<byte>[] { new byte[] { 1, 2, 3 } },
                new[] {
                    new BridgeRootSelectorV1("Fixture", "AddWord", null,
                        Array.Empty<string>(), Array.Empty<string>()),
                    new BridgeRootSelectorV1("Fixture", "OtherRootFixtureOnly", null,
                        Array.Empty<string>(), Array.Empty<string>())
                },
                Array.Empty<int>(), Array.Empty<int>()),
            "fixture-only-policy", "fixture-csc", "fixture-roslyn");

        var vectors = new GoldenVectorV1[]
        {
            new(new(32, true, 0xFFFFFFFF), new(32, true, 1)),
            new(new(32, true, 0x7FFFFFFF), new(32, true, 1)),
            new(new(32, true, 0x80000000), new(32, true, 0x80000000)),
        };
        const string reviewedLabel = "explicit-fixture-addword-v1";
        TrustedAddWordOracleV1 adapter = TrustedAddWordOracleV1.RegisterExplicitlyTrusted(
            (a, b) => unchecked(a + b), reviewedLabel, signed: true);
        GoldenObservationV1 observed = adapter.Observe(
            TrustedAddWordOracleV1.AllowlistedAdapterId, vectors);
        Require(observed.Status == "Observed", "Original explicitly registered v1 adapter changed.");
        Require(observed.Cases[0].Output!.Bits == 0 &&
            observed.Cases[1].Output!.Bits == 0x80000000 &&
            observed.Cases[2].Output!.Bits == 0, "Modulo/two's complement broken.");

        GoldenBoundVectorsV1 Capture(
            IReadOnlyList<GoldenVectorV1>? request = null,
            GoldenObservationV1? response = null,
            int rootIndex = 0,
            ReadOnlyMemory<byte>? originalBytes = null,
            ReadOnlyMemory<byte>? compiledBytes = null) =>
            GoldenBoundVectorsCodecV1.Capture(source,
                new ReadOnlyMemory<byte>[] { originalBytes ?? original },
                compiledBytes ?? assemblyBytes, rootIndex, request ?? vectors,
                response ?? observed);

        GoldenBoundVectorsV1 valid = Capture();
        byte[] bytes = valid.ToJsonUtf8();
        Require(bytes.SequenceEqual(Capture().ToJsonUtf8()), "Non-deterministic JSON.");
        Require(valid.Observation.Cases.Count == 3 &&
            valid.AdapterId == TrustedAddWordOracleV1.AllowlistedAdapterId &&
            valid.ReviewedSourceLabel == reviewedLabel, "Original AddWord identity lost.");
        Require(valid.RequestedVectors[0].A.ToSigned64() == -1 &&
            valid.Observation.Cases[1].Output!.ToSigned64() == int.MinValue,
            "Signed bit-pattern reinterpretation lost.");
        GoldenBoundVectorsV1 parsed = GoldenBoundVectorsCodecV1.ReadAndValidate(
            bytes, source, assemblyHash, 0, reviewedLabel, vectors);
        Require(parsed.ToJsonUtf8().SequenceEqual(bytes) &&
            parsed.ReplayIdentity == valid.ReplayIdentity, "Canonical roundtrip changed.");
        Require(parsed.OriginalDocuments[0].Sha256 == source.Documents[0].Sha256,
            "Original source digest not retained.");

        // This replay reference comes from independent inputs (not from the JSON being parsed).
        Reject<ArgumentException>(() => GoldenBoundVectorsCodecV1.ReadAndValidate(
            bytes, source, assemblyHash, 0, reviewedLabel,
            vectors.Reverse().ToArray()), "Independently requested vector order mismatch.");
        Reject<ArgumentException>(() => GoldenBoundVectorsCodecV1.ReadAndValidate(
            bytes, source, new string('0', 64), 0, reviewedLabel, vectors),
            "Dangling compiled assembly.");
        Reject<ArgumentException>(() => GoldenBoundVectorsCodecV1.ReadAndValidate(
            bytes, source, assemblyHash, 0, "unreviewed-adapter", vectors),
            "Wrong independent reviewed label.");
        Reject<ArgumentException>(() => GoldenBoundVectorsCodecV1.ReadAndValidate(
            bytes, source with { SnapshotDigest = new string('0', 64) },
            assemblyHash, 0, reviewedLabel, vectors), "Stale source snapshot.");
        Reject<ArgumentException>(() => Capture(originalBytes:
            Encoding.UTF8.GetBytes(goldenText + " ")), "Modified source bytes.");
        Reject<ArgumentException>(() => Capture(rootIndex: 8), "Wrong root index.");
        Reject<ArgumentException>(() => GoldenBoundVectorsCodecV1.ReadAndValidate(
            bytes, source, assemblyHash, 1, reviewedLabel, vectors),
            "Independently expected root index mismatch.");
        Reject<ArgumentException>(() => GoldenBoundVectorsCodecV1.ReadAndValidate(
            bytes, source, assemblyHash, -1, reviewedLabel, vectors),
            "Negative external root index.");
        Reject<ArgumentException>(() => GoldenBoundVectorsCodecV1.ReadAndValidate(
            new byte[GoldenBoundVectorsCodecV1.MaxJsonBytes + 1], source, assemblyHash,
            0, reviewedLabel, vectors), "JSON byte budget.");

        Reject<ArgumentException>(() => Capture(compiledBytes: ReadOnlyMemory<byte>.Empty),
            "Empty assembly evidence.");
        Reject<ArgumentException>(() => Capture(compiledBytes: new byte[
            GoldenBoundVectorsCodecV1.MaxAssemblyBytes + 1]), "Oversized assembly.");

        var alteredOutput = observed with
        {
            Cases = observed.Cases.Select((c, i) =>
                i == 0 ? c with { Output = new GoldenWordV1(32, true, 17) } : c).ToArray()
        };
        // This is ONLY replay checksum integrity: a forged, re-signed output still needs Core attestation.
        GoldenBoundVectorsV1 mutated = valid with { Observation = alteredOutput };
        Reject<ArgumentException>(() => GoldenBoundVectorsCodecV1.ReadAndValidate(
            mutated.ToJsonUtf8(), source, assemblyHash, 0, reviewedLabel, vectors),
            "Unre-signed output mutation.");
        Reject<ArgumentException>(() => Capture(
            response: observed with { AdapterId = "arbitrary-delegate-selected-by-json" }),
            "Sidecar-defined arbitrary delegate/adapter.");
        Reject<ArgumentException>(() => Capture(
            response: observed with { TrustedSourceLabel = "" }), "Empty reviewed label.");
        Reject<ArgumentException>(() => Capture(
            response: observed with { Status = "Verified" }), "Hardware promotion.");
        Reject<ArgumentException>(() => Capture(response: observed with
        {
            Cases = observed.Cases.Take(2).ToArray()
        }), "Truncated case list.");
        Reject<ArgumentException>(() => Capture(response: observed with
        {
            Cases = observed.Cases.Reverse().ToArray()
        }), "Reordered observed cases.");
        Reject<ArgumentException>(() => Capture(response: observed with
        {
            Cases = observed.Cases.Select((c, i) => i == 1 ?
                c with { Output = new GoldenWordV1(32, false, c.Output!.Bits) } : c).ToArray()
        }), "Output signedness mutation.");
        Reject<ArgumentException>(() => Capture(response: observed with
        {
            Cases = observed.Cases.Select((c, i) => i == 0 ?
                c with { Output = null } : c).ToArray()
        }), "Silent missing output.");
        Reject<ArgumentException>(() => Capture(response: observed with
        {
            Cases = observed.Cases.Select((c, i) => i == 0 ?
                c with { Output = new GoldenWordV1(32, true, 0x1_0000_0000) } : c).ToArray()
        }), "Noncanonical high bits.");
        Reject<ArgumentException>(() => Capture(
            request: vectors.Select((v, i) => i == 0
                ? new GoldenVectorV1(new GoldenWordV1(16, true, 1), v.B) : v).ToArray()),
            "Invalid requested scalar width.");
        Reject<ArgumentException>(() => Capture(
            request: vectors.Select((v, i) => i == 0
                ? new GoldenVectorV1(new GoldenWordV1(32, false, 1), v.B) : v).ToArray()),
            "Mixed signedness.");
        Reject<ArgumentException>(() => Capture(
            request: Enumerable.Repeat(vectors[0],
                GoldenBoundVectorsCodecV1.MaxVectors + 1).ToArray()),
            "Vector budget.");

        // Existing v1 adapter can produce a Blocked *case* while preserving original input order.
        var throwsAdapter = TrustedAddWordOracleV1.RegisterExplicitlyTrusted(
            (uint a, uint b) => throw new InvalidOperationException("fixture"), reviewedLabel, signed: true);
        GoldenObservationV1 blocked = throwsAdapter.Observe(
            TrustedAddWordOracleV1.AllowlistedAdapterId, vectors);
        GoldenBoundVectorsV1 blockedBatch = Capture(response: blocked);
        Require(blockedBatch.Observation.Status == "Blocked" &&
            blockedBatch.Observation.Cases.All(c => c.Output is null),
            "Bound exception must remain Blocked, never hardware PASS.");
        Require(GoldenBoundVectorsCodecV1.ReadAndValidate(
            blockedBatch.ToJsonUtf8(), source, assemblyHash, 0, reviewedLabel, vectors)
            .Observation.Status == "Blocked", "Blocked v1 case compatibility.");
        Reject<ArgumentException>(() => Capture(
            response: blocked with { Cases = new[] { blocked.Cases[0] } }),
            "Truncated Blocked sentinel is not an observed vector batch.");

        string compact = Encoding.UTF8.GetString(bytes);
        Reject<ArgumentException>(() => GoldenBoundVectorsCodecV1.ReadAndValidate(
            Encoding.UTF8.GetBytes(compact.Replace("\"AdapterId\":",
                "\"Delegate\":\"Evil.Run\",\"AdapterId\":", StringComparison.Ordinal)),
            source, assemblyHash, 0, reviewedLabel, vectors), "Injected arbitrary delegate JSON.");
        Reject<ArgumentException>(() => GoldenBoundVectorsCodecV1.ReadAndValidate(
            Encoding.UTF8.GetBytes(compact.Replace("\"AdapterId\":",
                "\"AdapterId\":\"explicit-trusted-addword32/v1\",\"AdapterId\":",
                StringComparison.Ordinal)), source, assemblyHash, 0, reviewedLabel, vectors),
            "Duplicate JSON member.");
        Reject<ArgumentException>(() => GoldenBoundVectorsCodecV1.ReadAndValidate(
            Encoding.UTF8.GetBytes(compact + " "), source, assemblyHash, 0, reviewedLabel, vectors),
            "Noncanonical whitespace.");
        Reject<ArgumentException>(() => GoldenBoundVectorsCodecV1.ReadAndValidate(
            Encoding.UTF8.GetBytes(compact.Replace(GoldenBoundVectorsV1.SchemaId,
                "unknown/vector/v2", StringComparison.Ordinal)),
            source, assemblyHash, 0, reviewedLabel, vectors), "Unknown schema.");
        Reject<ArgumentException>(() => GoldenBoundVectorsCodecV1.ReadAndValidate(
            bytes[..^5], source, assemblyHash, 0, reviewedLabel, vectors), "Truncated JSON.");

        // Defensive copy: changing a caller's list after capture never changes persisted batch bytes.
        var callerOwned = vectors.ToList();
        GoldenBoundVectorsV1 immutable = Capture(request: callerOwned);
        byte[] stable = immutable.ToJsonUtf8();
        callerOwned[0] = new(new(32, true, 42), new(32, true, 42));
        Require(immutable.ToJsonUtf8().SequenceEqual(stable), "Caller vector list retained.");

        // Independently supplied request cannot be silently re-signed into an accepted replay.
        var reOrdered = valid with
        {
            RequestedVectors = valid.RequestedVectors.Reverse().ToArray()
        };
        string forgedDigest = Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            reOrdered.Schema, reOrdered.SourceSnapshotDigest, reOrdered.OriginalDocuments,
            reOrdered.OriginalSourceSetSha256, reOrdered.CompiledAssemblySha256,
            reOrdered.RootIndex, reOrdered.AdapterId, reOrdered.ReviewedSourceLabel,
            reOrdered.RequestedVectors, reOrdered.Observation
        }));
        reOrdered = reOrdered with { ReplayIdentity = forgedDigest };
        Reject<ArgumentException>(() => GoldenBoundVectorsCodecV1.ReadAndValidate(
            reOrdered.ToJsonUtf8(), source, assemblyHash, 0, reviewedLabel, vectors),
            "Re-signed reorder must not bypass independent requested-vector binding.");

        // Both roots exist in the same snapshot: a checksum-valid switch to root #1
        // MUST fail if the independent immutable session actually requested root #0.
        GoldenBoundVectorsV1 switchedRoot = valid with { RootIndex = 1 };
        switchedRoot = switchedRoot with { ReplayIdentity = Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            switchedRoot.Schema, switchedRoot.SourceSnapshotDigest, switchedRoot.OriginalDocuments,
            switchedRoot.OriginalSourceSetSha256, switchedRoot.CompiledAssemblySha256,
            switchedRoot.RootIndex, switchedRoot.AdapterId, switchedRoot.ReviewedSourceLabel,
            switchedRoot.RequestedVectors, switchedRoot.Observation
        })) };
        Reject<ArgumentException>(() => GoldenBoundVectorsCodecV1.ReadAndValidate(
            switchedRoot.ToJsonUtf8(), source, assemblyHash, 0, reviewedLabel, vectors),
            "Checksum-valid switch between independently declared roots.");


        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Reject<OperationCanceledException>(() => GoldenBoundVectorsCodecV1.Capture(
            source, new ReadOnlyMemory<byte>[] { original }, assemblyBytes, 0,
            vectors, observed, cancelled.Token), "Cancelled capture.");
        Reject<OperationCanceledException>(() => GoldenBoundVectorsCodecV1.ReadAndValidate(
            bytes, source, assemblyHash, 0, reviewedLabel, vectors, cancelled.Token),
            "Cancelled read.");

        Require(source.SnapshotDigest == BridgeSourceSnapshotV1.Capture(
            new BridgeSourceRequestV1("Fixture", "CSharp15",
                new[] { new BridgeSourceTextV1("golden/Fixture.cs", goldenText) },
                new ReadOnlyMemory<byte>[] { new byte[] { 1, 2, 3 } },
                new[] {
                    new BridgeRootSelectorV1("Fixture", "AddWord", null,
                        Array.Empty<string>(), Array.Empty<string>()),
                    new BridgeRootSelectorV1("Fixture", "OtherRootFixtureOnly", null,
                        Array.Empty<string>(), Array.Empty<string>())
                },
                Array.Empty<int>(), Array.Empty<int>()),
            "fixture-only-policy", "fixture-csc", "fixture-roslyn").SnapshotDigest,
            "Capture mutated input source.");

        Console.WriteLine("HB-INT-04 BoundVectors source SHA256: " + source.Documents[0].Sha256);
        Console.WriteLine("HB-INT-04 BoundVectors replay SHA256: " + valid.ReplayIdentity);
        Console.WriteLine("HB-INT-04 BoundVectors JSON SHA256: " + Hash(bytes));
        Console.WriteLine("HB-INT-04 BoundVectors assertions passed: " + _assertions);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));
}
