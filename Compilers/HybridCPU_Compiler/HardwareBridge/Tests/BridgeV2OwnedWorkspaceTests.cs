#if HARDWAREBRIDGE_TESTS
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;

internal static class BridgeV2OwnedWorkspaceTests
{
    private static void Check(bool assertion, string message)
    {
        if (!assertion) throw new InvalidOperationException("HB-INT v2 workspace: " + message);
    }

    private static void Refuse(BridgeV2Refusal code, Action action)
    {
        try { action(); }
        catch (BridgeV2RefusalException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException("Expected typed workspace refusal " + code);
    }

    public static void Run()
    {
        // Files under a synthetic Golden tree: never the real Golden repository.
        string golden = Directory.CreateTempSubdirectory("hb-int-test-golden-").FullName;
        string? published = null;
        try
        {
            string sourcePath = Path.Combine(golden, "Fixture.cs");
            string sourceText = "public static class Fixture { public static int F() => unchecked(1 + 2); }";
            File.WriteAllText(sourcePath, sourceText, new UTF8Encoding(false));
            byte[] current = File.ReadAllBytes(sourcePath);
            string initialSha = Convert.ToHexString(SHA256.HashData(current));
            byte[] metadata = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
            var inputs = new[] {
                new BridgeV2Input("source/Fixture.cs", "source", current),
                new BridgeV2Input("references/metadata.bin", "metadata-only", metadata)
            };
            var roots = new[] {
                new BridgeRootSelectorV1("Fixture", "F", null, Array.Empty<string>(), Array.Empty<string>())
            };
            BridgeV2Transport index = BridgeSegmentIndexV2.Write(roots, inputs);
            var live = new[] {
                new BridgeV2LiveSourcePin("source/Fixture.cs", "original", sourcePath)
            };
            BridgeV2PublishedWorkspace staged = BridgeV2OwnedWorkspacePublisher.Publish(
                index, inputs, roots, live, golden);
            published = staged.Root;
            Check(Directory.Exists(staged.Root) && File.Exists(staged.IndexPath),
                "index.json not staged");
            Check(!Path.GetFullPath(staged.Root).StartsWith(
                Path.GetFullPath(golden) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
                "published inside original Golden root");
            Check(File.ReadAllBytes(staged.IndexPath).SequenceEqual(index.IndexUtf8),
                "index bytes changed");
            Check(staged.IndexSha256 == BridgeSegmentIndexV2.Hash(index.IndexUtf8) &&
                staged.ClosureSha256 == index.ClosureSha256, "transport identity changed");
            foreach (var chunk in index.Chunks)
                Check(File.ReadAllBytes(Path.Combine(staged.Root, chunk.Path))
                    .SequenceEqual(chunk.Bytes), "chunk bytes changed");
            foreach (var entry in inputs)
                Check(File.ReadAllBytes(Path.Combine(staged.Root, entry.Path))
                    .SequenceEqual(entry.Bytes.ToArray()), "source/reference bytes changed");
            Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))) ==
                initialSha, "test Golden source was modified");

            // Source changed between capture and publish: must refuse.
            File.WriteAllText(sourcePath, sourceText + " ", new UTF8Encoding(false));
            Refuse(BridgeV2Refusal.StaleSource, () =>
                BridgeV2OwnedWorkspacePublisher.Publish(index, inputs, roots, live, golden));
            File.WriteAllText(sourcePath, sourceText, new UTF8Encoding(false));

            Refuse(BridgeV2Refusal.MissingInput, () =>
                BridgeV2OwnedWorkspacePublisher.Publish(index, inputs, roots,
                    Array.Empty<BridgeV2LiveSourcePin>(), golden));
            Refuse(BridgeV2Refusal.InvalidRole, () =>
                BridgeV2OwnedWorkspacePublisher.Publish(index, inputs, roots,
                    new[] { live[0] with { Origin = "metadata-only" } }, golden));
            Refuse(BridgeV2Refusal.InvalidRole, () =>
                BridgeV2OwnedWorkspacePublisher.Publish(index, inputs, roots,
                    new[] { live[0] with { Origin = "generated" } }, golden));
            Refuse(BridgeV2Refusal.InvalidPin, () =>
                BridgeV2OwnedWorkspacePublisher.Publish(index,
                    new[] { inputs[0] with { Bytes = Encoding.UTF8.GetBytes(sourceText + "x") },
                        inputs[1] }, roots, live, golden));
            Refuse(BridgeV2Refusal.InvalidRole, () =>
                BridgeV2OwnedWorkspacePublisher.Publish(
                    BridgeSegmentIndexV2.Write(roots, new[] {
                        new BridgeV2Input("index.json", "source", current), inputs[1]
                    }), new[] { new BridgeV2Input("index.json", "source", current), inputs[1] },
                    roots, new[] { live[0] with { Path = "index.json" } }, golden));

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Refuse(BridgeV2Refusal.Cancelled, () =>
                BridgeV2OwnedWorkspacePublisher.Publish(index, inputs, roots,
                    live, golden, cancelled.Token));

            string missingPath = Path.Combine(golden, "missing.cs");
            Refuse(BridgeV2Refusal.MissingInput, () =>
                BridgeV2OwnedWorkspacePublisher.Publish(index, inputs, roots,
                    new[] { live[0] with { AbsolutePath = missingPath } }, golden));

            // Symlink/reparse test is only executable when the host supports symlink creation.
            if (!OperatingSystem.IsWindows())
            {
                string link = Path.Combine(golden, "symlink.cs");
                File.CreateSymbolicLink(link, sourcePath);
                Refuse(BridgeV2Refusal.InvalidRole, () =>
                    BridgeV2OwnedWorkspacePublisher.Publish(index, inputs, roots,
                        new[] { live[0] with { AbsolutePath = link } }, golden));
                File.Delete(link);
            }
            else
                Console.WriteLine("NOTRUN: symlink creation test requires non-Windows host permissions.");

            Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))) ==
                initialSha, "source bytes after negative cases differed");
            Console.WriteLine("PASS: HB-INT-02/03/06 owned v2 workspace byte publisher (synthetic Golden fixture only).");
        }
        finally
        {
            if (published is not null && Directory.Exists(published))
                Directory.Delete(published, true);
            if (Directory.Exists(golden))
                Directory.Delete(golden, true);
        }
    }
}
#endif
