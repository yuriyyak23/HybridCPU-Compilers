// Standalone opt-in assertions; excluded from parent recursive Compile unless HARDWAREBRIDGE_TESTS.
#if HARDWAREBRIDGE_TESTS
using System.Text;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Roslyn;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;
using HybridCPU.Compiler.HardwareBridge.CompilerFacts;
using HybridCPU.Compiler.HardwareBridge.GoldenOracle;

internal static class BridgeSelfTests
{
    private static void Require(bool predicate, string message)
    {
        if (!predicate) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + message);
    }

    private static BridgeSourceRequestV1 Request(byte[] image, string path = "src/Fixture.cs",
        string text = "public static class Fixture { public static int AddWord(int a, int b) => unchecked(a + b); }")
    {
        return new("BridgeFixture", "CSharp13",
            new[] { new BridgeSourceTextV1(path, text) },
            new ReadOnlyMemory<byte>[] { image },
            new[] { new BridgeRootSelectorV1("Fixture", "AddWord", null, Array.Empty<string>(), Array.Empty<string>()) },
            Array.Empty<int>(), Array.Empty<int>());
    }

    private static BridgeSourceSnapshotV1 Capture(BridgeSourceRequestV1 request) =>
        BridgeSourceSnapshotV1.Capture(request, ManagedSourceCompanionV1.SourceAdmissionPolicy,
            "fixture-compiler", "fixture-roslyn");

    public static int Main()
    {
        try
        {
            // An explicit, known runtime reference is a test fixture, not filesystem discovery by the bridge.
            byte[] reference = File.ReadAllBytes(typeof(object).Assembly.Location);
            BridgeSourceRequestV1 source = Request(reference);
            BridgeSourceSnapshotV1 snapshot = Capture(source);
            byte[] json = snapshot.ToJsonUtf8();
            Require(json.SequenceEqual(snapshot.ToJsonUtf8()), "Unstable source JSON bytes.");
            string digest = snapshot.SnapshotDigest;
            reference[0] ^= 0xFF;
            Require(snapshot.SnapshotDigest == digest && snapshot.ToJsonUtf8().SequenceEqual(json),
                "Caller mutation affected a captured reference digest.");
            Require(Capture(Request(reference)).SnapshotDigest != digest,
                "Changed reference bytes did not change snapshot identity.");
            Throws<ArgumentException>(() => Capture(Request(reference, "../escape.cs")), "Traversal path");
            Throws<ArgumentException>(() => Capture(Request(reference, "C:\\drive.cs")), "Absolute Windows path");
            Throws<ArgumentException>(() => Capture(Request(reference, "")), "Empty path");
            var duplicate = source with
            {
                Documents = new[] { new BridgeSourceTextV1("src/X.cs", "x"),
                                    new BridgeSourceTextV1("src/x.cs", "y") }
            };
            Throws<ArgumentException>(() => Capture(duplicate), "Case-aliased paths");
            var duplicateRef = source with { ReferenceImages = new ReadOnlyMemory<byte>[] { reference, reference } };
            Throws<ArgumentException>(() => Capture(duplicateRef), "Duplicate reference images");
            var both = source with { BodyReferenceIndices = new[] { 0 }, MetadataReferenceIndices = new[] { 0 } };
            Throws<ArgumentException>(() => Capture(both), "Body and metadata roles overlap");
            var badRoot = source with { Roots = new[] { new BridgeRootSelectorV1(
                "Fixture", "AddWord", null, new[] { "" }, Array.Empty<string>()) } };
            Throws<ArgumentException>(() => Capture(badRoot), "Empty generic argument");
            Throws<ArgumentException>(() => Capture(Request(reference, text:
                new string('z', BridgeSourceSnapshotV1.MaxDocumentUtf8Bytes + 1))), "Source budget");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Throws<OperationCanceledException>(() => BridgeSourceSnapshotV1.Capture(source,
                "policy", "version", "roslyn", cancelled.Token), "Source cancellation");

            var trusted = TrustedAddWordOracleV1.RegisterExplicitlyTrusted(
                (a, b) => unchecked(a + b), "test-only-reviewed-addword", signed: true);
            var vectors = new[]
            {
                Pair(0, 0), Pair(0xFFFFFFFF, 1), Pair(0x80000000, 0x80000000),
                Pair(0x7FFFFFFF, 1), Pair(0xFFFFFFFF, 0xFFFFFFFF),
                Pair(0x80000000, 0x7FFFFFFF), Pair(1, 0xFFFFFFFF)
            };
            GoldenObservationV1 observed = trusted.Observe(TrustedAddWordOracleV1.AllowlistedAdapterId, vectors);
            Require(observed.Status == "Observed" && observed.Cases.Count == vectors.Length,
                "Trusted AddWord fixture did not observe all cases.");
            Require(observed.Cases[1].Output!.Bits == 0 &&
                    observed.Cases[2].Output!.Bits == 0 &&
                    observed.Cases[3].Output!.Bits == 0x80000000 &&
                    observed.Cases[4].Output!.Bits == 0xFFFFFFFE,
                "Unchecked 32-bit modulo boundary mismatch.");
            Require(new GoldenWordV1(32, true, 0xFFFFFFFF).ToSigned64() == -1 &&
                    new GoldenWordV1(32, true, 0x80000000).ToSigned64() == int.MinValue,
                "Signed extension lost exact bits.");
            Require(trusted.Observe("arbitrary-json-named-adapter", vectors).Status == "Blocked",
                "Unknown adapter executed.");
            Require(trusted.Observe(TrustedAddWordOracleV1.AllowlistedAdapterId,
                new[] { new GoldenVectorV1(new(32, true, 0x0000000100000000), new(32, true, 1)) })
                .Status == "Blocked", "High 32-bit input silently truncated.");
            Require(trusted.Observe(TrustedAddWordOracleV1.AllowlistedAdapterId,
                new[] { new GoldenVectorV1(new(32, false, 1), new(32, false, 1)) })
                .Status == "Blocked", "Unsigned input accepted by signed contract.");
            var throwing = TrustedAddWordOracleV1.RegisterExplicitlyTrusted(
                (uint a, uint b) => throw new InvalidOperationException("controlled"), "test-exception", true);
            Require(throwing.Observe(TrustedAddWordOracleV1.AllowlistedAdapterId, vectors).Status == "Blocked",
                "Golden exception treated as scalar output.");
            Throws<OperationCanceledException>(() => trusted.Observe(
                TrustedAddWordOracleV1.AllowlistedAdapterId, vectors, cancelled.Token), "Oracle cancellation");

            byte[] sidecar = BridgeSidecarCodecV1.Write(snapshot,
                BridgeCompilerFactsV1.Rejected(snapshot.SnapshotDigest, "HBTEST", "no compiler attached"), observed);
            Require(sidecar.SequenceEqual(BridgeSidecarCodecV1.Write(snapshot,
                BridgeCompilerFactsV1.Rejected(snapshot.SnapshotDigest, "HBTEST", "no compiler attached"), observed)),
                "Sidecar nondeterministic.");
            using (JsonDocumentHolder ignored = new(BridgeSidecarCodecV1.ReadAndValidate(sidecar, digest))) { }
            Throws<ArgumentException>(() => { using var _ = BridgeSidecarCodecV1.ReadAndValidate(sidecar, new string('0', 64)); },
                "Stale sidecar");
            byte[] tampered = sidecar.ToArray(); tampered[tampered.Length - 2] ^= 1;
            Throws<ArgumentException>(() => { using var _ = BridgeSidecarCodecV1.ReadAndValidate(tampered, digest); },
                "Tampered payload");
            Throws<ArgumentException>(() => { using var _ = BridgeSidecarCodecV1.ReadAndValidate(
                Encoding.UTF8.GetBytes("{not-json"), digest); }, "Malformed sidecar");

            // An actual Companion/CIL vertical slice: no dynamic Assembly.Load, invocation or EDA.
            byte[] runtime = File.ReadAllBytes(typeof(object).Assembly.Location);
            var request = new CompanionSourceRequestV1("BridgeFixture",
                new[] { new CompanionSourceDocumentV1("src/Fixture.cs",
                    "public static class Fixture { public static int AddWord(int a,int b) => unchecked(a+b); }") },
                new ReadOnlyMemory<byte>[] { runtime });
            var importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
            var selectors = new[] { new RestrictedCilMethodSelectorV1("Fixture", "AddWord") };
            BridgeCompanionExportV1 export = CompanionBridgeProjectionV1.CompileBodyWorld(request,
                importer, selectors, Array.Empty<int>(), Array.Empty<int>());
            Require(export.Facts.Status == "Candidate", "Real Companion CIL projection refused: " +
                string.Join(" | ", export.Facts.Diagnostics.Select(d => d.Code + ":" + d.Message)));
            Require(export.Facts.Methods.Count == 1 && export.Facts.PeSha256 is not null &&
                export.Facts.PdbSha256 is not null && export.Facts.SourceInputDigest is not null &&
                export.Facts.SourceMaps.All(row => row.PeSha256.Equals(export.Facts.PeSha256,
                    StringComparison.OrdinalIgnoreCase)), "Primary source map or PE/PDB facts absent.");
            ManagedSourceBodyWorldResultV1 raw = ManagedSourceCompanionV1.CompileBodyWorld(request,
                importer, selectors, Array.Empty<int>(), Array.Empty<int>());
            byte[] corruptedPdb = raw.Source.Pdb!.ToArray();
            corruptedPdb[corruptedPdb.Length - 1] ^= 0x01;
            var wrongPair = raw with { Source = raw.Source with { Pdb = corruptedPdb } };
            Require(CompanionBridgeProjectionV1.Project(export.Source, wrongPair).Status == "Rejected",
                "Wrong PE/PDB pair was not rejected.");
            Require(CompanionBridgeProjectionV1.Project(export.Source, raw with { Compilation = null }).Status == "Rejected",
                "Missing call graph silently passed.");
            BridgeSourceSnapshotV1 mismatchedRoot = export.Source with
            {
                Roots = Array.AsReadOnly(new[] { new BridgeRootSelectorV1(
                    "Fixture", "AddWord", 0x06000099, Array.Empty<string>(), Array.Empty<string>()) })
            };
            Require(!mismatchedRoot.HasConsistentDigest() &&
                CompanionBridgeProjectionV1.Project(mismatchedRoot, raw).Status == "Rejected",
                "Stale or mismatched selector identity admitted.");
            BridgeSourceSnapshotV1 badGeneric = export.Source with
            {
                Roots = Array.AsReadOnly(new[] { new BridgeRootSelectorV1(
                    "Fixture", "AddWord", null, new[] { "System.Int64:Int64" }, Array.Empty<string>()) })
            };
            Require(CompanionBridgeProjectionV1.Project(badGeneric, raw).Status == "Rejected",
                "Generic argument mismatch admitted.");
            BridgeSourceSnapshotV1 wrongRole = export.Source with
            {
                References = Array.AsReadOnly(export.Source.References.Select(r =>
                    r with { Role = "owned-body" }).ToArray())
            };
            Require(CompanionBridgeProjectionV1.Project(wrongRole, raw).Status == "Rejected",
                "Changed body vs metadata/unselected reference role admitted.");
            Require(Capture(source with { BodyReferenceIndices = new[] { 0 } }).SnapshotDigest !=
                    Capture(source with { MetadataReferenceIndices = new[] { 0 } }).SnapshotDigest,
                "Body/metadata reference selection not bound into snapshot.");
            Console.WriteLine("PASS: HardwareBridge self-tests (software candidate-only).");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
    }

    private static GoldenVectorV1 Pair(uint a, uint b) =>
        new(new(32, true, a), new(32, true, b));

    private sealed class JsonDocumentHolder : IDisposable
    {
        private readonly System.Text.Json.JsonDocument _document;
        public JsonDocumentHolder(System.Text.Json.JsonDocument document) => _document = document;
        public void Dispose() => _document.Dispose();
    }
}
#endif
