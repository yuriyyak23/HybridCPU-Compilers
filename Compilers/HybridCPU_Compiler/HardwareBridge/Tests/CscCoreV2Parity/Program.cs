// HB-INT-02: parity against the actual Bridge v2 codec, not a Core-owned verifier.
// Synthetic Csc fixture transport; never executes Golden, Csc, RTL or native tools.
using System.Security.Cryptography;
using System.Text.Json;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]))
                throw new ArgumentException("Expected explicit absolute synthetic transport workspace.");
            string root = Path.GetFullPath(args[0]);
            byte[] receiptBytes = File.ReadAllBytes(Path.Combine(root, "bridge-export-receipt.json"));
            using JsonDocument doc = JsonDocument.Parse(receiptBytes);
            JsonElement receipt = doc.RootElement;
            if (receipt.GetProperty("Schema").GetString() !=
                "hybridcpu.hardwarebridge.core-transport-receipt/v1" ||
                receipt.GetProperty("Disposition").GetString() != "unqualified-software-byte-transport")
                throw new InvalidOperationException("Unknown/qualified receipt.");
            BridgeRootSelectorV1[] roots = receipt.GetProperty("Roots")
                .Deserialize<BridgeRootSelectorV1[]>() ?? throw new InvalidOperationException("Missing roots.");
            var inputs = new List<BridgeV2Input>();
            foreach (JsonElement row in receipt.GetProperty("Inputs").EnumerateArray())
            {
                string relative = row.GetProperty("Path").GetString()
                    ?? throw new InvalidOperationException("Missing input.");
                string role = row.GetProperty("Role").GetString()
                    ?? throw new InvalidOperationException("Missing role.");
                if (!relative.StartsWith("inputs/", StringComparison.Ordinal) ||
                    relative.Contains("..", StringComparison.Ordinal) || relative.Contains('\\'))
                    throw new InvalidOperationException("Unsupported test input path.");
                byte[] bytes = File.ReadAllBytes(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (hash != row.GetProperty("Sha256").GetString() ||
                    bytes.LongLength != row.GetProperty("Bytes").GetInt64())
                    throw new InvalidOperationException("Stale source/metadata source bytes.");
                inputs.Add(new BridgeV2Input(relative, role, bytes));
            }
            BridgeV2Transport expected = BridgeSegmentIndexV2.Write(roots, inputs);
            byte[] index = File.ReadAllBytes(Path.Combine(root, "bridge-index.json"));
            if (!expected.IndexUtf8.SequenceEqual(index))
                throw new InvalidOperationException("Python adapter diverged from the production Bridge v2 wire index.");
            if (expected.ClosureSha256 != receipt.GetProperty("CoreClosureSha256").GetString() ||
                Convert.ToHexString(SHA256.HashData(index)) != receipt.GetProperty("CoreIndexSha256").GetString())
                throw new InvalidOperationException("Python adapter emitted stale Core index/closure SHA.");
            foreach (BridgeV2ChunkContent chunk in expected.Chunks)
            {
                byte[] actual = File.ReadAllBytes(Path.Combine(root,
                    chunk.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!actual.SequenceEqual(chunk.Bytes))
                    throw new InvalidOperationException("Python adapter diverged from production Bridge v2 JSONL bytes.");
            }
            BridgeSegmentIndexV2.ValidateAgainstActualBytes(
                index, expected.Chunks, roots, inputs);
            Console.WriteLine("PASS: HB-INT-02 Core v2 transport parity; source-only unqualified bytes.");
            Console.WriteLine("IndexSha256=" + Convert.ToHexString(SHA256.HashData(index)) +
                " ClosureSha256=" + expected.ClosureSha256 +
                " Inputs=" + inputs.Count + " Chunks=" + expected.Chunks.Count);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL HB-INT-02 wire parity: " + error);
            return 1;
        }
    }
}
