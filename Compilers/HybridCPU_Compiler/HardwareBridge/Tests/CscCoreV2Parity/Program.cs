// Tests cross-language byte parity with the real Bridge v2 codec.
// This is a synthetic captured-Csc wire regression, NOT Core source admission or RTL proof.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--wire-limits")
                return CompareProductionWireLimits();
            if (args.Length == 1 && args[0] == "--r203-wire-matrix")
                return R203WireMatrix.Run();
            if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]))
                throw new ArgumentException("Explicit absolute synthetic transport workspace required.");
            string root = Path.GetFullPath(args[0]);
            if (File.Exists(Path.Combine(root, "capture-map.json")))
                return CompareReviewedStager(root);
            using JsonDocument doc = JsonDocument.Parse(
                File.ReadAllBytes(Path.Combine(root, "capture-crosswalk.json")));
            JsonElement link = doc.RootElement;
            if (link.GetProperty("Schema").GetString() !=
                    "hybridcpu.hardwarebridge.csc-core-transport-link/v1" ||
                link.GetProperty("Disposition").GetString() != "unqualified-software-observation")
                throw new InvalidOperationException("Unknown source transport receipt or authority promotion.");
            byte[] indexBytes = File.ReadAllBytes(Path.Combine(root, "index.json"));
            using JsonDocument indexDoc = JsonDocument.Parse(indexBytes);
            BridgeRootSelectorV1[] roots = indexDoc.RootElement.GetProperty("Roots")
                .Deserialize<BridgeRootSelectorV1[]>() ??
                throw new InvalidOperationException("Missing exact explicit roots.");

            var inputs = new List<BridgeV2Input>();
            foreach (JsonElement row in link.GetProperty("Inputs").EnumerateArray())
            {
                string relative = row.GetProperty("Path").GetString() ??
                    throw new InvalidOperationException("Missing staged path.");
                if (!Regex.IsMatch(relative, @"^(src/[0-9]{5}\.cs|metadata/[0-9]{5}\.bin)$",
                    RegexOptions.CultureInvariant))
                    throw new InvalidOperationException("Unsafe/ambiguous staged input path.");
                string kind = row.GetProperty("Kind").GetString() ??
                    throw new InvalidOperationException("Missing observed Csc kind.");
                string role = (kind is "csc-source" or "generator-source") ? "source" : "metadata-only";
                if ((role == "source") != relative.StartsWith("src/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Crosswalk promoted metadata or discarded source ownership.");
                byte[] bytes = File.ReadAllBytes(Path.Combine(root,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
                if (row.GetProperty("Sha256").GetString() != Convert.ToHexString(SHA256.HashData(bytes)) ||
                    row.GetProperty("Bytes").GetInt64() != bytes.LongLength)
                    throw new InvalidOperationException("Stale or tampered staged source/metadata bytes.");
                inputs.Add(new(relative, role, bytes));
            }
            BridgeV2Transport fresh = BridgeSegmentIndexV2.Write(roots, inputs);
            if (!indexBytes.AsSpan().SequenceEqual(fresh.IndexUtf8) ||
                link.GetProperty("CoreIndexSha256").GetString() !=
                    Convert.ToHexString(SHA256.HashData(indexBytes)) ||
                link.GetProperty("CoreClosureSha256").GetString() != fresh.ClosureSha256)
                throw new InvalidOperationException(
                    "Python staged index differs from production .NET Bridge v2 serialization.");
            foreach (BridgeV2ChunkContent chunk in fresh.Chunks)
            {
                byte[] actual = File.ReadAllBytes(Path.Combine(root,
                    chunk.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!actual.AsSpan().SequenceEqual(chunk.Bytes))
                    throw new InvalidOperationException("Python staged JSONL differs from .NET production codec.");
            }
            BridgeSegmentIndexV2.ValidateAgainstActualBytes(
                indexBytes, fresh.Chunks, roots, inputs);
            Console.WriteLine("PASS: HB-INT-02/03 production Bridge v2 index/JSONL byte parity; unqualified only.");
            Console.WriteLine("IndexSha256=" + Convert.ToHexString(SHA256.HashData(indexBytes)) +
                " ClosureSha256=" + fresh.ClosureSha256 +
                " Inputs=" + inputs.Count + " Chunks=" + fresh.Chunks.Count);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: HB-INT-02/03 production codec parity: " + error);
            return 1;
        }
    }
    // Real stage_core_v2.py output, checked against the production .NET
    // BridgeSegmentIndexV2.Write, not a second hand-written serializer.
    private static int CompareReviewedStager(string root)
    {
        using JsonDocument mapDoc = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(root, "capture-map.json")));
        JsonElement map = mapDoc.RootElement;
        R203WireMatrix.CheckMapContract(map);
        if (map.GetProperty("Schema").GetString() !=
                "hybridcpu.hardwarebridge.csc-to-core-map/v1" ||
            map.GetProperty("Disposition").GetString() != "unqualified-software-observation")
            throw new InvalidOperationException("Unknown/requalified reviewed staging mapping.");

        byte[] rootBytes = File.ReadAllBytes(Path.Combine(root, "roots-manifest.json"));
        string rootSha = Convert.ToHexString(SHA256.HashData(rootBytes)).ToLowerInvariant();
        if (map.GetProperty("RootManifestSha256").GetString() != rootSha)
            throw new InvalidOperationException("Reviewed root manifest bytes changed.");
        using JsonDocument rootDoc = JsonDocument.Parse(rootBytes);
        R203WireMatrix.CheckRootsContract(rootDoc.RootElement);
        if (rootDoc.RootElement.GetProperty("Schema").GetString() !=
                "hybridcpu.hardwarebridge.root-selectors/v1")
            throw new InvalidOperationException("Unknown root review contract.");
        BridgeRootSelectorV1[] roots = rootDoc.RootElement.GetProperty("Roots")
            .Deserialize<BridgeRootSelectorV1[]>() ??
            throw new InvalidOperationException("Missing reviewed selectors.");

        byte[] indexBytes = File.ReadAllBytes(Path.Combine(root, "bridge-index.json"));
        using JsonDocument indexDoc = JsonDocument.Parse(indexBytes);
        var inputs = new List<BridgeV2Input>();
        int ordinal = 0;
        foreach (JsonElement row in map.GetProperty("Rows").EnumerateArray())
        {
            string path = row.GetProperty("Path").GetString() ??
                throw new InvalidOperationException("Missing staged input path.");
            string kind = row.GetProperty("Kind").GetString() ??
                throw new InvalidOperationException("Missing captured kind.");
            string original = row.GetProperty("SourceRole").GetString() ??
                throw new InvalidOperationException("Missing captured project role.");
            bool sourceKind = kind is "csc-source" or "generator-source";
            string role;
            if (sourceKind && original == "primary") role = "source";
            else if (sourceKind && original == "owned-body") role = "owned-body";
            else if (original == "metadata-only") role = "metadata-only";
            else throw new InvalidOperationException("Captured metadata promoted to executable owner.");
            string expected = "inputs/" + ordinal.ToString("D5",
                System.Globalization.CultureInfo.InvariantCulture) +
                (role is "source" or "owned-body" ? ".cs" : ".bin");
            if (path != expected || !Regex.IsMatch(path, @"^inputs/[0-9]{5}\.(cs|bin)$",
                    RegexOptions.CultureInvariant))
                throw new InvalidOperationException("Unexpected staged input path/order.");
            byte[] bytes = File.ReadAllBytes(Path.Combine(root,
                path.Replace('/', Path.DirectorySeparatorChar)));
            if (row.GetProperty("Sha256").GetString() !=
                    Convert.ToHexString(SHA256.HashData(bytes)) ||
                row.GetProperty("Bytes").GetInt64() != bytes.LongLength)
                throw new InvalidOperationException("Staged file bytes differ from capture mapping.");
            inputs.Add(new BridgeV2Input(path, role, bytes));
            ordinal++;
        }
        BridgeV2Transport actual = BridgeSegmentIndexV2.Write(roots, inputs);
        R203WireMatrix.AssertSameBytes("index", indexBytes, actual.IndexUtf8);
        if (map.GetProperty("CoreClosureSha256").GetString() != actual.ClosureSha256 ||
            indexDoc.RootElement.GetProperty("ClosureSha256").GetString() != actual.ClosureSha256)
            throw new InvalidOperationException("Python index differs from production .NET serializer.");
        if (indexDoc.RootElement.GetProperty("Chunks").GetArrayLength() != actual.Chunks.Count)
            throw new InvalidOperationException("Chunk descriptor count mismatch.");
        foreach (BridgeV2ChunkContent chunk in actual.Chunks)
        {
            byte[] actualChunk = File.ReadAllBytes(Path.Combine(root,
                chunk.Path.Replace('/', Path.DirectorySeparatorChar)));
            R203WireMatrix.AssertSameBytes(chunk.Path, actualChunk, chunk.Bytes);
        }
        BridgeSegmentIndexV2.ValidateAgainstActualBytes(indexBytes, actual.Chunks, roots, inputs);
        Console.WriteLine("PASS: reviewed Python staged bytes == production .NET v2 index + every JSONL chunk; data-only.");
        Console.WriteLine("IndexSha256=" + Convert.ToHexString(SHA256.HashData(indexBytes)) +
            " ClosureSha256=" + actual.ClosureSha256 + " Inputs=" + inputs.Count +
            " Chunks=" + actual.Chunks.Count + " RootManifestSha256=" + rootSha);
        return 0;
    }

    // Actual production codec tested at the precise in-memory wire limits.
    // Deliberately shares one immutable 32 MiB input across rows so the
    // 512 MiB aggregate test does not allocate a second 512 MiB array.
    private static int CompareProductionWireLimits()
    {
        var roots = new[] { new BridgeRootSelectorV1("Fixture", "F", null,
            Array.Empty<string>(), Array.Empty<string>()) };
        void ExpectRejected(Action action, string label)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Production codec accepted " + label);
        }
        byte[] source = new byte[2_097_152];
        BridgeV2Transport sourceAtLimit = BridgeSegmentIndexV2.Write(roots,
            new[] { new BridgeV2Input("src/00000.cs", "source", source) });
        if (sourceAtLimit.Inputs[0].Bytes != source.LongLength)
            throw new InvalidOperationException("2 MiB source boundary mismatch.");
        ExpectRejected(() => BridgeSegmentIndexV2.Write(roots,
            new[] { new BridgeV2Input("src/00000.cs", "source",
                new byte[2_097_153]) }), "2 MiB + 1 source");
        byte[] metadata = new byte[33_554_432];
        var empty = new BridgeV2Input("src/00000.cs", "source", Array.Empty<byte>());
        var at512 = new List<BridgeV2Input> { empty };
        for (int i = 0; i < 16; i++)
            at512.Add(new BridgeV2Input("meta/" + i.ToString("D5") + ".bin",
                "metadata-only", metadata));
        BridgeV2Transport full = BridgeSegmentIndexV2.Write(roots, at512);
        if (full.Inputs.Sum(x => x.Bytes) != BridgeSegmentIndexV2.MaxWorkspaceBytes)
            throw new InvalidOperationException("512 MiB aggregate limit is not exact.");
        BridgeSegmentIndexV2.ValidateAgainstActualBytes(full.IndexUtf8,
            full.Chunks, roots, at512);
        ExpectRejected(() => BridgeSegmentIndexV2.Write(roots, at512.Append(
            new BridgeV2Input("meta/99999.bin", "metadata-only", metadata)).ToArray()),
            "512 MiB + 32 MiB aggregate");
        ExpectRejected(() => BridgeSegmentIndexV2.Write(roots,
            new[] { empty, new BridgeV2Input("meta/excess.bin",
                "metadata-only", new byte[33_554_433]) }), "32 MiB + 1 metadata");
        foreach (int count in new[] { 1, 64, 65, 1001 })
        {
            var rows = Enumerable.Range(0, count).Select(i =>
                new BridgeV2Input("src/" + i.ToString("D5") + ".cs",
                    "source", new byte[] { (byte)(i & 255) })).ToArray();
            BridgeV2Transport t = BridgeSegmentIndexV2.Write(roots, rows);
            BridgeSegmentIndexV2.ValidateAgainstActualBytes(t.IndexUtf8,
                t.Chunks, roots, rows);
            if (t.Inputs.Count != count || t.Chunks.Count != (count + 63) / 64)
                throw new InvalidOperationException("Wrong 1/64/65/1001 chunk boundary: " + count);
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            BridgeSegmentIndexV2.Write(roots, new[] { empty }, cancelled.Token);
            throw new InvalidOperationException("Cancelled writer unexpectedly succeeded.");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS: actual production .NET v2 codec 1/64/65/1001, 2MiB, 32MiB, 512MiB, overflow, cancellation.");
        Console.WriteLine("512MiBIndexSha256=" + BridgeSegmentIndexV2.Hash(full.IndexUtf8) +
            " ClosureSha256=" + full.ClosureSha256 +
            " Chunks=" + full.Chunks.Count);
        return 0;
    }

}
