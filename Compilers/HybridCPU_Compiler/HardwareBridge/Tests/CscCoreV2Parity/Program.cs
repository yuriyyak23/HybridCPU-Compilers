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
}
