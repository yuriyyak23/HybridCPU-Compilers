#if HARDWAREBRIDGE_TESTS
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;
using HybridCPU.Compiler.HardwareBridge.CompilerFacts;

// Additional wire regression tests; no source filesystem access or execution authority.
internal static class HbInt06ProtocolTests
{
    private static void Reject(Action action, string label)
    {
        try { action(); }
        catch (ArgumentException) { Console.WriteLine("PASS negative: " + label); return; }
        throw new InvalidOperationException("FAIL: protocol mutation was admitted: " + label);
    }

    public static void Run()
    {
        byte[] reference = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var root = new BridgeRootSelectorV1("Fixture", "AddWord", null,
            Array.Empty<string>(), Array.Empty<string>());
        var request = new BridgeSourceRequestV1("HB06", "CSharp13",
            new[] { new BridgeSourceTextV1("src/Fixture.cs",
                "public static class Fixture { public static int AddWord(int x, int y) => x + y; }") },
            new ReadOnlyMemory<byte>[] { reference },
            new[] { root }, Array.Empty<int>(), new[] { 0 });
        BridgeSourceSnapshotV1 snapshot = BridgeSourceSnapshotV1.Capture(
            request, "fixture-policy", "fixture-compiler", "fixture-roslyn");
        byte[] v1 = BridgeSidecarCodecV1.Write(snapshot,
            BridgeCompilerFactsV1.Rejected(snapshot.SnapshotDigest, "HB06", "no import"));
        using (BridgeSidecarCodecV1.ReadAndValidate(v1, snapshot.SnapshotDigest)) { }
        Console.WriteLine("PASS protocol: v1 diagnostic sidecar exact expected source digest");
        string json = Encoding.UTF8.GetString(v1);
        Reject(() =>
        {
            using var _ = BridgeSidecarCodecV1.ReadAndValidate(
                Encoding.UTF8.GetBytes(json.Replace(
                    "hybridcpu.hardwarebridge.sidecar/v1",
                    "hybridcpu.hardwarebridge.sidecar/v2", StringComparison.Ordinal)),
                snapshot.SnapshotDigest);
        }, "unknown v2 schema (not silently downgraded)");
        // Duplicate envelope keys used to be accepted by JsonDocument.TryGetProperty.
        // The payload checksum is unchanged: this is a parser ambiguity attack.
        string marker = "\"Schema\":\"" + BridgeSidecarCodecV1.SchemaId + "\",";
        string duplicateKey = json.Insert(json.IndexOf(marker, StringComparison.Ordinal) + marker.Length, marker);
        if (duplicateKey == json) throw new InvalidOperationException("Envelope fixture did not contain Schema");
        Reject(() =>
        {
            using var _ = BridgeSidecarCodecV1.ReadAndValidate(
                Encoding.UTF8.GetBytes(duplicateKey), snapshot.SnapshotDigest);
        }, "duplicate envelope JSON members");
        Reject(() => BridgeSourceSnapshotV1.Capture(request with { Roots = new[] { root, root } },
            "fixture-policy", "fixture-compiler", "fixture-roslyn"), "duplicate roots");
        // A malicious reader can reconstruct the preimage and checksum. Duplicate
        // roots must still fail structural validation; a hash is no authority.
        var duplicateRoots = snapshot with { Roots = new[] { root, root } };
        byte[] forgedPreimage = JsonSerializer.SerializeToUtf8Bytes(new {
            duplicateRoots.Schema, duplicateRoots.AssemblyName, duplicateRoots.LanguageHostMode,
            duplicateRoots.AdmissionPolicy, duplicateRoots.CompilerVersion, duplicateRoots.RoslynVersion,
            duplicateRoots.Documents, duplicateRoots.References, duplicateRoots.Roots,
            duplicateRoots.BodyReferenceIndices, duplicateRoots.MetadataReferenceIndices
        });
        var forgedDigest = duplicateRoots with {
            SnapshotDigest = Convert.ToHexString(SHA256.HashData(forgedPreimage))
        };
        if (forgedDigest.HasConsistentDigest())
            throw new InvalidOperationException("Duplicate roots with recomputed checksum admitted");
        Reject(() => BridgeSourceSnapshotV1.Capture(request with { BodyReferenceIndices = new[] { 0 } },
            "fixture-policy", "fixture-compiler", "fixture-roslyn"), "overlapping reference roles");
        Reject(() => BridgeSourceSnapshotV1.Capture(request with
        {
            Documents = new[] { new BridgeSourceTextV1("../escaped.cs", "x") }
        }, "fixture-policy", "fixture-compiler", "fixture-roslyn"), "relative path traversal");
        var roleMutation = snapshot with
        {
            References = snapshot.References.Select(r => r with { Role = "unrecognized-v2-role" }).ToArray()
        };
        if (roleMutation.HasConsistentDigest()) throw new InvalidOperationException("Unknown reference role admitted");
        Reject(() => BridgeSidecarCodecV1.Write(roleMutation), "unsupported reference role");
        var staleSource = BridgeSourceSnapshotV1.Capture(request with
        {
            Documents = new[] { new BridgeSourceTextV1("src/Fixture.cs", "class Changed{}") }
        }, "fixture-policy", "fixture-compiler", "fixture-roslyn");
        Reject(() =>
        {
            using var _ = BridgeSidecarCodecV1.ReadAndValidate(v1, staleSource.SnapshotDigest);
        }, "source changed between capture and validation");
        Console.WriteLine("PASS: HB-INT-06 strict v1/unknown-v2 regression and mutation tests (software only).");
    }
}
#endif
