// R2-03 exclusively owned synthetic contract matrix. Uses the actual production v2 codec.
// No compiler invocation, Core importer, source qualification or Golden checkout.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;

internal static class R203WireMatrix
{
    private static readonly BridgeRootSelectorV1 Root =
        new("Parity", "Run", null, Array.Empty<string>(), Array.Empty<string>());

    public static int Run()
    {
        try
        {
            foreach (int count in new[] { 1, 63, 64, 65, 1001, 8192 })
            {
                BridgeV2Input[] inputs = Enumerable.Range(0, count).Select(i =>
                    new BridgeV2Input("inputs/" + i.ToString("D5",
                        System.Globalization.CultureInfo.InvariantCulture) + ".cs",
                        "source", Encoding.UTF8.GetBytes("// Ω/漢/🚀 " + i + "\n"))).ToArray();
                BridgeV2Transport transport = BridgeSegmentIndexV2.Write(new[] { Root }, inputs);
                CheckCanonical(transport, inputs);
                if (transport.Chunks.Count != (count + 63) / 64)
                    throw new InvalidOperationException("Incorrect 64-row boundary: " + count);
                Console.WriteLine("R203_MATRIX rows=" + count + " chunks=" + transport.Chunks.Count +
                    " indexSHA=" + BridgeSegmentIndexV2.Hash(transport.IndexUtf8) +
                    " closureSHA=" + transport.ClosureSha256);
            }

            var baseline = new[] { new BridgeV2Input("src/A.cs", "source",
                Encoding.UTF8.GetBytes("class A {}\n")) };
            BridgeV2Transport good = BridgeSegmentIndexV2.Write(new[] { Root }, baseline);
            CheckCanonical(good, baseline);
            ExpectRefused(() => BridgeSegmentIndexV2.Write(new[] { Root, Root }, baseline),
                "duplicate root");
            var second = new BridgeRootSelectorV1("Parity", "Other", 9,
                Array.Empty<string>(), Array.Empty<string>());
            BridgeV2Transport ordered = BridgeSegmentIndexV2.Write(new[] { Root, second }, baseline);
            ExpectRefused(() => BridgeSegmentIndexV2.ValidateAgainstActualBytes(
                ordered.IndexUtf8, ordered.Chunks, new[] { second, Root }, baseline),
                "root order altered");
            ExpectRefused(() => BridgeSegmentIndexV2.Write(
                new[] { new BridgeRootSelectorV1("Parity", "Run", null,
                    new[] { "T" }, Array.Empty<string>()) }, baseline), "untokened generic root");
            ExpectRefused(() => BridgeSegmentIndexV2.Write(new[] { Root }, new[]
            {
                baseline[0], new BridgeV2Input("SRC/a.CS", "source", new byte[] { 1 })
            }), "case-fold alias");
            foreach (string path in new[] { "../bad.cs", "/absolute.cs", "src\\bad.cs",
                                          "src//bad.cs", "src/./bad.cs", "src/../bad.cs",
                                          "src/bad:.cs", "src/\u0065\u0301.cs" })
                ExpectRefused(() => BridgeSegmentIndexV2.Write(new[] { Root },
                    new[] { new BridgeV2Input(path, "source", new byte[] { 1 }) }),
                    "unsafe relative path");
            ExpectRefused(() => BridgeSegmentIndexV2.Write(new[] { Root },
                new[] { new BridgeV2Input("src/A.cs", "unknown", new byte[] { 1 }) }),
                "unknown file role");
            ExpectRefused(() => BridgeSegmentIndexV2.Write(new[] { Root },
                new[] { new BridgeV2Input("meta/only.bin", "metadata-only", new byte[] { 1 }) }),
                "no primary source");
            ExpectRefused(() => BridgeSegmentIndexV2.Write(new[] { Root },
                Enumerable.Range(0, 8193).Select(i => new BridgeV2Input(
                    "src/" + i.ToString("D5") + ".cs", "source", new byte[] { 1 })).ToArray()),
                "8193 rows");
            ExpectRefused(() => BridgeSegmentIndexV2.Write(new[] { Root }, new[]
            {
                new BridgeV2Input("src/A.cs", "source", new byte[2_097_153])
            }), "source >2MiB");
            ExpectRefused(() => BridgeSegmentIndexV2.Write(new[] { Root }, new[]
            {
                baseline[0], new BridgeV2Input("meta/large.bin", "metadata-only",
                    new byte[33_554_433])
            }), "metadata >32MiB");

            ExpectRefused(() => BridgeSegmentIndexV2.ValidateAgainstActualBytes(
                good.IndexUtf8.Concat(new byte[] { (byte)' ' }).ToArray(),
                good.Chunks, new[] { Root }, baseline), "changed JSON whitespace");
            ExpectRefused(() => BridgeSegmentIndexV2.ValidateAgainstActualBytes(
                new byte[] { 0xEF, 0xBB, 0xBF }.Concat(good.IndexUtf8).ToArray(),
                good.Chunks, new[] { Root }, baseline), "UTF-8 BOM");
            ExpectRefused(() => BridgeSegmentIndexV2.ValidateAgainstActualBytes(
                new byte[] { 0xFF }, good.Chunks, new[] { Root }, baseline),
                "non-UTF8 index");
            string json = Encoding.UTF8.GetString(good.IndexUtf8);
            foreach (string candidate in new[]
            {
                json.Replace("\"Schema\":", "\"Unknown\":1,\"Schema\":", StringComparison.Ordinal),
                json.Replace("circuitweave.bridge-source-closure-index/v2",
                    "circuitweave.bridge-source-closure-index/v3", StringComparison.Ordinal),
                json.Replace("\"ClosureSha256\":", "\"ClosureSha256\":\""
                    + new string('0', 64) + "\",\"ClosureSha256\":", StringComparison.Ordinal)
            })
                ExpectRefused(() => BridgeSegmentIndexV2.ValidateAgainstActualBytes(
                    Encoding.UTF8.GetBytes(candidate), good.Chunks, new[] { Root }, baseline),
                    "unknown version/duplicate JSON property");
            byte[] corrupted = (byte[])good.Chunks[0].Bytes.Clone();
            corrupted[0] ^= 1;
            ExpectRefused(() => BridgeSegmentIndexV2.ValidateAgainstActualBytes(
                good.IndexUtf8, new[] { new BridgeV2ChunkContent(good.Chunks[0].Path, corrupted) },
                new[] { Root }, baseline), "changed chunk SHA/content");
            ExpectRefused(() => BridgeSegmentIndexV2.ValidateAgainstActualBytes(
                good.IndexUtf8, good.Chunks, new[] { Root },
                new[] { new BridgeV2Input("src/A.cs", "source", new byte[] { 2 }) }),
                "stale original source SHA");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            ExpectRefused(() => BridgeSegmentIndexV2.ValidateAgainstActualBytes(
                good.IndexUtf8, good.Chunks, new[] { Root }, baseline, cancelled.Token),
                "cancelled validation");

            Console.WriteLine("PASS R2-03 production codec canonical/negative matrix (synthetic only).");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL R2-03 matrix: " + error);
            return 1;
        }
    }

    private static void ExpectRefused(Action action, string label)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or BridgeV2RefusalException
                                   or OperationCanceledException) { return; }
        throw new InvalidOperationException("Unexpected admission of " + label);
    }

    private static void CheckCanonical(BridgeV2Transport value, BridgeV2Input[] original)
    {
        if (value.IndexUtf8.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            throw new InvalidOperationException("BOM on v2 index");
        _ = new UTF8Encoding(false, true).GetString(value.IndexUtf8);
        using JsonDocument index = JsonDocument.Parse(value.IndexUtf8);
        JsonElement doc = index.RootElement;
        string[] propertyOrder = { "Schema", "Roots", "Chunks", "ClosureSha256" };
        if (!doc.EnumerateObject().Select(x => x.Name).SequenceEqual(propertyOrder) ||
            doc.GetProperty("Schema").GetString() != BridgeSegmentIndexV2.SchemaId ||
            doc.GetProperty("ClosureSha256").GetString() != value.ClosureSha256 ||
            !BridgeSegmentIndexV2.IsSha(value.ClosureSha256))
            throw new InvalidOperationException("Wrong canonical index fields/hash case");
        if (value.Inputs.Count != original.Length ||
            !value.Inputs.Select(x => x.Path).SequenceEqual(
                value.Inputs.Select(x => x.Path).OrderBy(x => x, StringComparer.Ordinal)))
            throw new InvalidOperationException("Noncanonical input ordering");
        for (int i = 0; i < original.Length; i++)
        {
            BridgeV2FileRow row = value.Inputs.Single(x => x.Path == original[i].Path);
            if (row.Role != original[i].Role || row.Bytes != original[i].Bytes.Length ||
                row.Sha256 != BridgeSegmentIndexV2.Hash(original[i].Bytes.Span))
                throw new InvalidOperationException("Changed original input bytes/role/digest");
        }
        byte[] combined = value.Chunks.SelectMany(x => x.Bytes).ToArray();
        if (BridgeSegmentIndexV2.Hash(combined) != value.ClosureSha256 ||
            doc.GetProperty("Chunks").GetArrayLength() != value.Chunks.Count)
            throw new InvalidOperationException("Changed concatenated JSONL closure");
        for (int c = 0; c < value.Chunks.Count; c++)
        {
            BridgeV2ChunkContent chunk = value.Chunks[c];
            JsonElement descriptor = doc.GetProperty("Chunks")[c];
            if (chunk.Bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ||
                chunk.Bytes.Length == 0 || chunk.Bytes[^1] != (byte)'\n' ||
                chunk.Bytes.Contains((byte)'\r') ||
                descriptor.GetProperty("Path").GetString() != chunk.Path ||
                descriptor.GetProperty("Sha256").GetString() != BridgeSegmentIndexV2.Hash(chunk.Bytes) ||
                descriptor.GetProperty("Bytes").GetInt64() != chunk.Bytes.Length)
                throw new InvalidOperationException("Noncanonical JSONL chunk/descriptor");
            string raw = new UTF8Encoding(false, true).GetString(chunk.Bytes);
            string[] lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length != descriptor.GetProperty("Rows").GetInt32() ||
                lines.Length != Math.Min(64, original.Length - 64*c))
                throw new InvalidOperationException("Incorrect raw row count");
            foreach (string line in lines)
            {
                using JsonDocument parsed = JsonDocument.Parse(line);
                if (!parsed.RootElement.EnumerateObject().Select(x => x.Name).SequenceEqual(
                        new[] { "Path", "Role", "Sha256", "Bytes" }))
                    throw new InvalidOperationException("Wrong JSONL field order");
            }
        }
        BridgeSegmentIndexV2.ValidateAgainstActualBytes(value.IndexUtf8, value.Chunks,
            new[] { Root }, original);
    }
}
