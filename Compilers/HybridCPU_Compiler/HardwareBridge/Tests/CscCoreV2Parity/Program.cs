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
        if (map.GetProperty("Schema").GetString() !=
                "hybridcpu.hardwarebridge.csc-to-core-map/v1" ||
            map.GetProperty("Disposition").GetString() != "unqualified-software-observation")
            throw new InvalidOperationException("Unknown/requalified reviewed staging mapping.");

        byte[] rootBytes = File.ReadAllBytes(Path.Combine(root, "roots-manifest.json"));
        string rootSha = Convert.ToHexString(SHA256.HashData(rootBytes)).ToLowerInvariant();
        if (map.GetProperty("RootManifestSha256").GetString() != rootSha)
            throw new InvalidOperationException("Reviewed root manifest bytes changed.");
        using JsonDocument rootDoc = JsonDocument.Parse(rootBytes);
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
        if (!indexBytes.AsSpan().SequenceEqual(actual.IndexUtf8) ||
            map.GetProperty("CoreClosureSha256").GetString() != actual.ClosureSha256 ||
            indexDoc.RootElement.GetProperty("ClosureSha256").GetString() != actual.ClosureSha256)
            throw new InvalidOperationException("Python index differs from production .NET serializer.");
        if (indexDoc.RootElement.GetProperty("Chunks").GetArrayLength() != actual.Chunks.Count)
            throw new InvalidOperationException("Chunk descriptor count mismatch.");
        foreach (BridgeV2ChunkContent chunk in actual.Chunks)
        {
            byte[] actualChunk = File.ReadAllBytes(Path.Combine(root,
                chunk.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!actualChunk.AsSpan().SequenceEqual(chunk.Bytes))
                throw new InvalidOperationException("Python raw JSONL differs from .NET production codec.");
        }
        BridgeSegmentIndexV2.ValidateAgainstActualBytes(indexBytes, actual.Chunks, roots, inputs);
        Console.WriteLine("PASS: reviewed Python staged bytes == production .NET v2 index + every JSONL chunk; data-only.");
        Console.WriteLine("IndexSha256=" + Convert.ToHexString(SHA256.HashData(indexBytes)) +
            " ClosureSha256=" + actual.ClosureSha256 + " Inputs=" + inputs.Count +
            " Chunks=" + actual.Chunks.Count + " RootManifestSha256=" + rootSha);
        return 0;
    }

}
