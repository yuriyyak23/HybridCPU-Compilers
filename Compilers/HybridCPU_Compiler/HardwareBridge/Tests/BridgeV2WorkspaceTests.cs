#if HARDWAREBRIDGE_TESTS
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;

/// <summary>Real filesystem contract tests; fixture bytes are synthetic, not Golden.</summary>
internal static class BridgeV2WorkspaceTests
{
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("HB-INT-03 workspace: " + description);
    }

    private static void Refuse(Action operation, string description)
    {
        try { operation(); }
        catch (Exception error) when (error is ArgumentException or IOException or
            BridgeV2RefusalException or OperationCanceledException)
        {
            return;
        }
        throw new Exception("HB-INT-03 workspace accepted " + description);
    }

    public static void Run()
    {
        string temporary = Path.Combine(Path.GetTempPath(),
            "hb-int-03-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        string sourceRoot = Path.Combine(temporary, "golden-readonly");
        string outputParent = Path.Combine(temporary, "workspace");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputParent);
        try
        {
            byte[] source = Encoding.UTF8.GetBytes("static class M { static int F() => 42; }");
            string golden = Path.Combine(sourceRoot, "M.cs");
            File.WriteAllBytes(golden, source);
            string sourceHash = BridgeSegmentIndexV2.Hash(File.ReadAllBytes(golden));
            byte[] reference = [1, 2, 3, 4, 5];
            var roots = new[] { new BridgeRootSelectorV1("M", "F", null, [], []) };
            var inputs = new[]
            {
                new BridgeV2Input("src/M.cs", "source", source),
                new BridgeV2Input("refs/0000.bin", "metadata-only", reference)
            };
            var captured = BridgeSegmentIndexV2.Write(roots, inputs);
            string destination = Path.Combine(outputParent, "capture-001");
            BridgeV2WorkspacePublication receipt = BridgeV2WorkspacePublisher.Publish(
                destination, [sourceRoot], captured, roots, inputs);
            Check(receipt.WorkspacePath == Path.GetFullPath(destination) &&
                  receipt.FileCount == 2 && receipt.ChunkCount == 1 &&
                  receipt.InputBytes == source.LongLength + reference.LongLength,
                "reported transport scope");
            Check(receipt.IndexSha256 == BridgeSegmentIndexV2.Hash(captured.IndexUtf8) &&
                  receipt.ClosureSha256 == captured.ClosureSha256, "reported bytes SHA-256");
            Check(File.ReadAllBytes(Path.Combine(destination, "src", "M.cs")).SequenceEqual(source),
                "published source bytes");
            Check(File.ReadAllBytes(Path.Combine(destination, "refs", "0000.bin")).SequenceEqual(reference),
                "published reference bytes");
            Check(File.ReadAllBytes(Path.Combine(destination, "bridge-index", "index.json"))
                  .SequenceEqual(captured.IndexUtf8), "published exact Core-compatible index");
            Check(File.ReadAllBytes(Path.Combine(destination, "bridge-index", "chunk-0000.jsonl"))
                  .SequenceEqual(captured.Chunks[0].Bytes), "published exact raw JSONL");
            Check(BridgeSegmentIndexV2.Hash(File.ReadAllBytes(golden)) == sourceHash,
                "Golden fixture changed during publication");

            // Pre-existing output must never be merged/replaced.
            Refuse(() => BridgeV2WorkspacePublisher.Publish(destination, [sourceRoot],
                captured, roots, inputs), "existing destination");
            Check(File.ReadAllBytes(Path.Combine(destination, "src", "M.cs")).SequenceEqual(source),
                "pre-existing published source was overwritten");

            // A linked or overlapping model tree must never be written through.
            Refuse(() => BridgeV2WorkspacePublisher.Publish(
                Path.Combine(sourceRoot, "capture"), [sourceRoot], captured, roots, inputs),
                "output below source root");
            Refuse(() => BridgeV2WorkspacePublisher.Publish(
                Path.Combine(outputParent, "capture-without-roots"), [],
                captured, roots, inputs), "missing forbidden source roots");

            // In-memory source must be immutable for the whole transfer, not
            // merely represented by a caller-assigned hash label.
            byte[] changed = source.ToArray();
            changed[0] ^= 1;
            var changedInputs = new[]
            {
                inputs[0] with { Bytes = changed }, inputs[1]
            };
            Refuse(() => BridgeV2WorkspacePublisher.Publish(
                Path.Combine(outputParent, "changed-source"), [sourceRoot],
                captured, roots, changedInputs), "changed source bytes after capture");

            var staleChunk = captured.Chunks.Select(c =>
                new BridgeV2ChunkContent(c.Path, c.Bytes.ToArray())).ToArray();
            staleChunk[0].Bytes[^1] ^= 1;
            Refuse(() => BridgeV2WorkspacePublisher.Publish(
                Path.Combine(outputParent, "changed-index"), [sourceRoot],
                captured with { Chunks = staleChunk }, roots, inputs),
                "mutated raw v2 chunk bytes");

            // Existing index output name is reserved even when the pure v2
            // serializer accepts it as a source name.
            var collidingInputs = new[]
            {
                new BridgeV2Input("bridge-index/index.json", "source", source)
            };
            var collidingTransport = BridgeSegmentIndexV2.Write(roots, collidingInputs);
            Refuse(() => BridgeV2WorkspacePublisher.Publish(
                Path.Combine(outputParent, "colliding-name"), [sourceRoot],
                collidingTransport, roots, collidingInputs), "reserved index path");

            // The codec allows both paths as independent logical entries but
            // their disk interpretation cannot be safe. Rollback leaves no
            // partially published directory.
            var conflictingInputs = new[]
            {
                new BridgeV2Input("a", "source", source),
                new BridgeV2Input("a/b.bin", "metadata-only", reference)
            };
            var conflicting = BridgeSegmentIndexV2.Write(roots, conflictingInputs);
            string incomplete = Path.Combine(outputParent, "partial-capture");
            Refuse(() => BridgeV2WorkspacePublisher.Publish(incomplete, [sourceRoot],
                conflicting, roots, conflictingInputs), "file/directory prefix collision");
            Check(!Directory.Exists(incomplete) && !Directory.EnumerateDirectories(
                outputParent, ".hb-v2-stage-*", SearchOption.TopDirectoryOnly).Any(),
                "partial stage was not rolled back");

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Refuse(() => BridgeV2WorkspacePublisher.Publish(
                Path.Combine(outputParent, "cancelled"), [sourceRoot],
                captured, roots, inputs, cts.Token), "pre-cancelled write");
            Check(!Directory.Exists(Path.Combine(outputParent, "cancelled")),
                "cancellation exposed a destination");

            // On hosts permitting symlinks, reject existing linked output
            // ancestors. Unavailable privilege is reported as NotRun.
            string symlink = Path.Combine(temporary, "linked-workspace");
            try
            {
                Directory.CreateSymbolicLink(symlink, outputParent);
                Refuse(() => BridgeV2WorkspacePublisher.Publish(
                    Path.Combine(symlink, "escape"), [sourceRoot],
                    captured, roots, inputs), "linked output ancestor");
                Check(!Directory.Exists(Path.Combine(outputParent, "escape")),
                    "output followed linked directory");
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or
                PlatformNotSupportedException or IOException)
            {
                Console.WriteLine("NotRun: linked-directory probe unavailable: " +
                    exception.GetType().Name);
            }

            Check(BridgeSegmentIndexV2.Hash(File.ReadAllBytes(golden)) == sourceHash,
                "Golden fixture bytes changed after negative mutations");
            Console.WriteLine("PASS: HB-INT-03 v2 owned-workspace filesystem contract; " +
                "synthetic inputs only, no Core/RTL/Golden qualification.");
            Console.WriteLine("HBINT03_WORKSPACE_INDEX_SHA256=" + receipt.IndexSha256);
            Console.WriteLine("HBINT03_WORKSPACE_CLOSURE_SHA256=" + receipt.ClosureSha256);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }
}
#endif
