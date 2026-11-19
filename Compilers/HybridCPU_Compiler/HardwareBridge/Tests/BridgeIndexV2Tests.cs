#if HARDWAREBRIDGE_TESTS
using System.Text;
using System.Text.Json;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;
using HybridCPU.Compiler.HardwareBridge.CompilerFacts;

internal static class BridgeIndexV2Tests
{
    private static void Check(bool condition, string why)
    {
        if (!condition) throw new InvalidOperationException("HB-INT-03 regression: " + why);
    }
    private static void Refuse(BridgeV2Refusal expected, Action act)
    {
        try { act(); }
        catch (BridgeV2RefusalException error) when (error.Code == expected) { return; }
        throw new InvalidOperationException("HB-INT-03 expected refusal: " + expected);
    }
    private static void RejectWrite(Action act)
    {
        try { act(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("HB-INT-03 writer unexpectedly admitted invalid input.");
    }

    public static void Run()
    {
        const string sdk = "11.0.100-rc.2.26504.105"; // test handshake, not an installed SDK claim
        const string sourceText = "public static class Fixture { public static int F() => 1; }";
        byte[] sourceBytes = Encoding.UTF8.GetBytes(sourceText);
        byte[] reference = Enumerable.Range(0, 2048).Select(i => (byte)(i * 17)).ToArray();
        var roots = new[] {
            new BridgeRootSelectorV1("Fixture", "F", null, Array.Empty<string>(), Array.Empty<string>())
        };
        BridgeSourceSnapshotV1 snapshot = BridgeSourceSnapshotV1.Capture(
            new BridgeSourceRequestV1("HBINT03Fixture", "CSharp13",
                new[] { new BridgeSourceTextV1("src/Fixture.cs", sourceText) },
                new ReadOnlyMemory<byte>[] { reference }, roots, Array.Empty<int>(), new[] { 0 }),
            "test-only-policy", "test-only-compiler", "test-only-roslyn");
        BridgeCompilerFactsV1 rejected =
            BridgeCompilerFactsV1.Rejected(snapshot.SnapshotDigest, "HBINT03_FIXTURE", "no software compiler");
        // Existing writer/reader and exact digest rule are not changed or replaced.
        byte[] oldBytes = BridgeSidecarCodecV1.Write(snapshot, rejected);
        using (JsonDocument oldReader = BridgeSidecarCodecV1.ReadAndValidate(oldBytes, snapshot.SnapshotDigest)) { }
        Check(oldBytes.SequenceEqual(BridgeSidecarCodecV1.Write(snapshot, rejected)), "v1 deterministic bytes");

        var inputs = new[] {
            new BridgeV2Input("src/Fixture.cs", "source", sourceBytes),
            new BridgeV2Input("refs/0000.bin", "metadata-only", reference)
        };
        var evidence = new BridgeV2Evidence(sdk,
            Encoding.UTF8.GetBytes("TEST-ONLY-CSC-IMAGE-NOT-A-PRODUCTION-PIN"),
            Encoding.UTF8.GetBytes("/deterministic /langversion:preview test-only"),
            ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty,
            new[] { new BridgeV2SourceRole("src/Fixture.cs", "original") });
        BridgeV2Transport transport = BridgeSegmentIndexV2.Write(roots, inputs);
        Check(transport.IndexUtf8.SequenceEqual(BridgeSegmentIndexV2.Write(roots, inputs).IndexUtf8),
            "unstable Core-compatible index");
        using (JsonDocument index = JsonDocument.Parse(transport.IndexUtf8))
        {
            Check(index.RootElement.GetProperty("Schema").GetString() ==
                BridgeSegmentIndexV2.SchemaId, "Core reader wire schema");
            Check(index.RootElement.GetProperty("Chunks").GetArrayLength() == 1, "chunk count");
            Check(index.RootElement.GetProperty("ClosureSha256").GetString() ==
                transport.ClosureSha256, "closure checksum");
        }

        BridgeSegmentIndexV2.ValidateAgainstActualBytes(transport.IndexUtf8,
            transport.Chunks, roots, inputs);
        Refuse(BridgeV2Refusal.InvalidPin, () =>
            BridgeSegmentIndexV2.ValidateAgainstActualBytes(transport.IndexUtf8,
                Array.Empty<BridgeV2ChunkContent>(), roots, inputs));
        var truncatedChunk = new[] { new BridgeV2ChunkContent(transport.Chunks[0].Path,
            transport.Chunks[0].Bytes[..^1]) };
        Refuse(BridgeV2Refusal.InvalidPin, () =>
            BridgeSegmentIndexV2.ValidateAgainstActualBytes(transport.IndexUtf8,
                truncatedChunk, roots, inputs));
        byte[] encoded = BridgeProvenanceCodecV2.Write(snapshot, rejected, inputs, evidence);
        BridgeV2ProvenancePayload decoded = BridgeProvenanceCodecV2.ReadAndValidate(
            encoded, snapshot, rejected, inputs, evidence, sdk);
        Check(decoded.SoftwareStatus == "Rejected" && decoded.CompanionSourceInputDigest is null &&
            decoded.PeSha256 is null && decoded.PdbSha256 is null, "unqualified status/pins");
        Check(encoded.SequenceEqual(BridgeProvenanceCodecV2.Write(snapshot, rejected, inputs, evidence)),
            "deterministic provenance byte preimage");

        string json = Encoding.UTF8.GetString(encoded);
        string dupe = json.Replace("\"Schema\":\"" + BridgeProvenanceCodecV2.SchemaId + "\",",
            "\"Schema\":\"" + BridgeProvenanceCodecV2.SchemaId +
            "\",\"Schema\":\"" + BridgeProvenanceCodecV2.SchemaId + "\",",
            StringComparison.Ordinal);
        Refuse(BridgeV2Refusal.DuplicateProperty, () =>
            BridgeProvenanceCodecV2.ReadAndValidate(Encoding.UTF8.GetBytes(dupe),
                snapshot, rejected, inputs, evidence, sdk));
        Refuse(BridgeV2Refusal.NonCanonical, () =>
            BridgeProvenanceCodecV2.ReadAndValidate(Encoding.UTF8.GetBytes(
                json.Replace("\"PayloadSha256\":", "\"Verified\":true,\"PayloadSha256\":",
                    StringComparison.Ordinal)), snapshot, rejected, inputs, evidence, sdk));
        Refuse(BridgeV2Refusal.NonCanonical, () =>
            BridgeProvenanceCodecV2.ReadAndValidate(Encoding.UTF8.GetBytes(
                json.Replace(",\"Payload\":", ", \"Payload\":", StringComparison.Ordinal)),
                snapshot, rejected, inputs, evidence, sdk));
        Refuse(BridgeV2Refusal.InvalidPin, () =>
            BridgeProvenanceCodecV2.ReadAndValidate(Encoding.UTF8.GetBytes(
                json.Replace("\"original\"", "\"working\"", StringComparison.Ordinal)),
                snapshot, rejected, inputs, evidence, sdk));
        Refuse(BridgeV2Refusal.InvalidPin, () =>
            BridgeProvenanceCodecV2.ReadAndValidate(encoded, snapshot, rejected, inputs,
                evidence, "different-sdk-pin"));
        Refuse(BridgeV2Refusal.InvalidPin, () =>
            BridgeProvenanceCodecV2.ReadAndValidate(encoded, snapshot, rejected,
                ChangedSourceInputs(), evidence, sdk));
        Refuse(BridgeV2Refusal.InvalidPin, () =>
            BridgeProvenanceCodecV2.ReadAndValidate(encoded, snapshot, rejected,
                inputs.Take(1).ToArray(), evidence, sdk));
        Refuse(BridgeV2Refusal.InvalidPin, () =>
            BridgeProvenanceCodecV2.ReadAndValidate(encoded, snapshot, rejected,
                new[] { inputs[0], inputs[1] with { Role = "owned-body" } }, evidence, sdk));
        Refuse(BridgeV2Refusal.InvalidPin, () =>
            BridgeProvenanceCodecV2.ReadAndValidate(encoded, snapshot, rejected, inputs,
                evidence with { CscImage = Encoding.UTF8.GetBytes("changed csc") }, sdk));
        Refuse(BridgeV2Refusal.Oversized, () =>
            BridgeProvenanceCodecV2.ReadAndValidate(new byte[BridgeProvenanceCodecV2.MaxEnvelopeBytes + 1],
                snapshot, rejected, inputs, evidence, sdk));
        Refuse(BridgeV2Refusal.MalformedJson, () =>
            BridgeProvenanceCodecV2.ReadAndValidate(Encoding.UTF8.GetBytes(
                new string('[', 50) + new string(']', 50)), snapshot, rejected, inputs, evidence, sdk));
        Refuse(BridgeV2Refusal.UnsupportedSchema, () =>
            BridgeProvenanceCodecV2.ReadAndValidate(Encoding.UTF8.GetBytes(
                json.Replace(BridgeProvenanceCodecV2.SchemaId, "unknown-version/v9",
                    StringComparison.Ordinal)), snapshot, rejected, inputs, evidence, sdk));

        var stale = BridgeSourceSnapshotV1.Capture(new BridgeSourceRequestV1("HBINT03Fixture",
            "CSharp13", new[] { new BridgeSourceTextV1("src/Fixture.cs", sourceText + " ") },
            new ReadOnlyMemory<byte>[] { reference }, roots, Array.Empty<int>(), new[] { 0 }),
            "test-only-policy", "test-only-compiler", "test-only-roslyn");
        Refuse(BridgeV2Refusal.StaleSource, () =>
            BridgeProvenanceCodecV2.ReadAndValidate(encoded, stale, rejected, inputs, evidence, sdk));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Refuse(BridgeV2Refusal.Cancelled, () =>
            BridgeProvenanceCodecV2.ReadAndValidate(encoded, snapshot, rejected,
                inputs, evidence, sdk, cancelled.Token));
        RejectWrite(() => BridgeSegmentIndexV2.Write(roots,
            new[] { inputs[0] with { Path = "../escape.cs" } }));
        RejectWrite(() => BridgeSegmentIndexV2.Write(roots,
            new[] { inputs[0], inputs[0] with { Path = "src/fixture.cs" } }));
        RejectWrite(() => BridgeSegmentIndexV2.Write(roots,
            new[] { inputs[0] with { Role = "Verified" } }));
        RejectWrite(() => BridgeProvenanceCodecV2.Write(snapshot, rejected,
            new[] { inputs[0], inputs[1] with { Role = "owned-body" } }, evidence));
        // Pure transport stress: 1,200 synthetic sources. This is NOT a Golden capture.
        var bulk = Enumerable.Range(0, 1200).Select(i => new BridgeV2Input(
            "src/" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".cs",
            "source", Encoding.UTF8.GetBytes("class C" + i + " {}"))).ToArray();
        BridgeV2Transport large = BridgeSegmentIndexV2.Write(roots, bulk);
        Check(large.Inputs.Count == 1200 && large.Chunks.Count > 1 &&
            large.Chunks.All(c => c.Bytes.Length <= BridgeSegmentIndexV2.MaxChunkBytes),
            "segmented large inventory");

        // Synthetic PE/PDB fixtures exercise the *format gate*, not a real Csc compilation.
        byte[] pe = Encoding.UTF8.GetBytes("TEST-ONLY-PE");
        byte[] pdb = Encoding.UTF8.GetBytes("TEST-ONLY-PDB");
        string peHash = BridgeSegmentIndexV2.Hash(pe), pdbHash = BridgeSegmentIndexV2.Hash(pdb);
        var candidate = rejected with {
            Status = "Candidate", SourceInputDigest = BridgeSegmentIndexV2.Hash(sourceBytes),
            PeSha256 = peHash, PdbSha256 = pdbHash, ImportStatus = "Success",
            GraphDigest = BridgeSegmentIndexV2.Hash(sourceBytes),
            GraphContractDigest = BridgeSegmentIndexV2.Hash(reference),
            RootIdentities = new[] { "Fixture::F" },
            Methods = new[] { new BridgeMethodFactV1("Fixture::F", "test-only-assembly",
                "test-module", "Fixture", "F", "int F()", 0x06000001,
                BridgeSegmentIndexV2.Hash(sourceBytes), peHash, "Success", Array.Empty<string>()) }
        };
        var candidateInputs = inputs.Concat(new[] {
            new BridgeV2Input("outputs/test.dll", "compiler-pe", pe),
            new BridgeV2Input("outputs/test.pdb", "compiler-pdb", pdb)
        }).ToArray();
        var candidateEvidence = evidence with { PeImage = pe, PdbImage = pdb };
        byte[] candidateBytes = BridgeProvenanceCodecV2.Write(snapshot, candidate,
            candidateInputs, candidateEvidence);
        Check(BridgeProvenanceCodecV2.ReadAndValidate(candidateBytes, snapshot, candidate,
            candidateInputs, candidateEvidence, sdk).SoftwareStatus == "Candidate",
            "synthetic candidate format");
        byte[] wrongPdb = pdb.ToArray(); wrongPdb[0] ^= 0x40;
        Refuse(BridgeV2Refusal.InvalidPin, () => BridgeProvenanceCodecV2.ReadAndValidate(
            candidateBytes, snapshot, candidate, candidateInputs,
            candidateEvidence with { PdbImage = wrongPdb }, sdk));

        Check(oldBytes.SequenceEqual(BridgeSidecarCodecV1.Write(snapshot, rejected)),
            "v1 bytes changed after v2 exercise");
        Console.WriteLine("PASS: HB-INT-03 strict v2 contract/unit mutations (test-only software facts).");
        // Filesystem publishing is a separate opt-in contract and is exercised by
        // the standalone v2 host; do not treat this in-memory PASS as staging proof.

        BridgeV2Input[] ChangedSourceInputs() =>
            new[] { inputs[0] with { Bytes = Encoding.UTF8.GetBytes(sourceText + " ") }, inputs[1] };
    }
}
#endif
