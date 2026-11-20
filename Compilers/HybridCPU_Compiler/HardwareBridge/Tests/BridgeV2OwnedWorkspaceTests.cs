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

    private static void RemoveSyntheticOutput(string? root)
    {
        if (root is null || !Directory.Exists(root)) return;
        // Test-only cleanup of paths created in a synthetic isolated temp fixture.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            foreach (string dir in Directory.EnumerateDirectories(root, "*",
                SearchOption.AllDirectories).OrderBy(x => x.Length))
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
        }
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
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
            Check((File.GetAttributes(staged.IndexPath) & FileAttributes.ReadOnly) != 0,
                "published index not marked read-only");
            if (!OperatingSystem.IsWindows())
                Check((File.GetUnixFileMode(staged.Root) & UnixFileMode.UserWrite) == 0,
                    "published root should be write-restricted on Unix");
            BridgeV2PublishedWorkspace second = BridgeV2OwnedWorkspacePublisher.Publish(
                index, inputs, roots, live, golden);
            Check(second.Root != staged.Root && second.IndexSha256 == staged.IndexSha256,
                "reentrant publish must allocate independent identical byte transport");
            RemoveSyntheticOutput(second.Root);

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

            // Actual on-disk mutations strictly between the first source rehash
            // and the output rehash, not merely mutation of in-memory DTOs.
            string? failed = null;
            try
            {
                BridgeV2OwnedWorkspacePublisher.AfterSourcePreflightForTests = () =>
                    File.AppendAllText(sourcePath, " // changed after pin");
                Refuse(BridgeV2Refusal.StaleSource, () =>
                    BridgeV2OwnedWorkspacePublisher.Publish(index, inputs, roots, live, golden));
            }
            finally
            {
                BridgeV2OwnedWorkspacePublisher.AfterSourcePreflightForTests = null;
                File.WriteAllBytes(sourcePath, current);
            }

            try
            {
                BridgeV2OwnedWorkspacePublisher.AfterStageWriteForTests = path => {
                    failed = path;
                    File.AppendAllText(sourcePath, " // changed during staging");
                };
                Refuse(BridgeV2Refusal.StaleSource, () =>
                    BridgeV2OwnedWorkspacePublisher.Publish(index, inputs, roots, live, golden));
                Check(failed is not null && !Directory.Exists(failed),
                    "source mutation failure must clean only owned temp");
            }
            finally
            {
                BridgeV2OwnedWorkspacePublisher.AfterStageWriteForTests = null;
                File.WriteAllBytes(sourcePath, current);
                RemoveSyntheticOutput(failed);
            }

            using (var midCancel = new CancellationTokenSource())
            {
                failed = null;
                try
                {
                    BridgeV2OwnedWorkspacePublisher.AfterStageWriteForTests = path => {
                        failed = path;
                        midCancel.Cancel();
                    };
                    Refuse(BridgeV2Refusal.Cancelled, () =>
                        BridgeV2OwnedWorkspacePublisher.Publish(index, inputs, roots,
                            live, golden, midCancel.Token));
                    Check(failed is not null && !Directory.Exists(failed),
                        "interrupted publication left owned partial temp");
                }
                finally
                {
                    BridgeV2OwnedWorkspacePublisher.AfterStageWriteForTests = null;
                    RemoveSyntheticOutput(failed);
                }
            }

            // No untracked path may be reported to Core. Unowned files must not be
            // recursively deleted by producer cleanup: test removes it explicitly.
            failed = null;
            try
            {
                BridgeV2OwnedWorkspacePublisher.AfterStageWriteForTests = path => {
                    failed = path;
                    File.WriteAllText(Path.Combine(path, "untracked.txt"), "unowned");
                };
                Refuse(BridgeV2Refusal.InvalidPin, () =>
                    BridgeV2OwnedWorkspacePublisher.Publish(index, inputs, roots, live, golden));
                Check(failed is not null &&
                    File.Exists(Path.Combine(failed, "untracked.txt")),
                    "cleanup must preserve unknown injected file");
            }
            finally
            {
                BridgeV2OwnedWorkspacePublisher.AfterStageWriteForTests = null;
                RemoveSyntheticOutput(failed);
            }

            failed = null;
            try
            {
                BridgeV2OwnedWorkspacePublisher.AfterStageWriteForTests = path => {
                    failed = path;
                    File.AppendAllText(Path.Combine(path, "source", "Fixture.cs"), "// mutated");
                };
                Refuse(BridgeV2Refusal.StaleSource, () =>
                    BridgeV2OwnedWorkspacePublisher.Publish(index, inputs, roots, live, golden));
                Check(failed is not null && !Directory.Exists(failed),
                    "altered staged source not cleaned");
            }
            finally
            {
                BridgeV2OwnedWorkspacePublisher.AfterStageWriteForTests = null;
                RemoveSyntheticOutput(failed);
            }

            failed = null;
            try
            {
                BridgeV2OwnedWorkspacePublisher.AfterStageWriteForTests = path => {
                    failed = path;
                    File.Delete(Path.Combine(path, index.Chunks[0].Path));
                };
                Refuse(BridgeV2Refusal.InvalidPin, () =>
                    BridgeV2OwnedWorkspacePublisher.Publish(index, inputs, roots, live, golden));
                Check(failed is not null && !Directory.Exists(failed),
                    "missing staged chunk not cleaned");
            }
            finally
            {
                BridgeV2OwnedWorkspacePublisher.AfterStageWriteForTests = null;
                RemoveSyntheticOutput(failed);
            }

            // Rename changes the exact staged filename even though the original
            // chunk bytes remain valid. It must not qualify the published tree.
            failed = null;
            try
            {
                BridgeV2OwnedWorkspacePublisher.AfterStageWriteForTests = path => {
                    failed = path;
                    File.Move(Path.Combine(path, index.Chunks[0].Path),
                        Path.Combine(path, "bridge-index", "renamed.jsonl"));
                };
                Refuse(BridgeV2Refusal.InvalidPin, () =>
                    BridgeV2OwnedWorkspacePublisher.Publish(index, inputs, roots, live, golden));
                Check(failed is not null &&
                    File.Exists(Path.Combine(failed, "bridge-index", "renamed.jsonl")),
                    "renamed/unowned output must not be recursively removed");
            }
            finally
            {
                BridgeV2OwnedWorkspacePublisher.AfterStageWriteForTests = null;
                RemoveSyntheticOutput(failed);
            }

            if (!OperatingSystem.IsWindows())
            {
                failed = null;
                try
                {
                    BridgeV2OwnedWorkspacePublisher.AfterStageWriteForTests = path => {
                        failed = path;
                        File.SetUnixFileMode(Path.Combine(path, "source", "Fixture.cs"),
                            UnixFileMode.None);
                    };
                    Refuse(BridgeV2Refusal.InvalidPin, () =>
                        BridgeV2OwnedWorkspacePublisher.Publish(index, inputs, roots, live, golden));
                    Check(failed is not null && !Directory.Exists(failed),
                        "unreadable output must clean its owned staging tree");
                }
                finally
                {
                    BridgeV2OwnedWorkspacePublisher.AfterStageWriteForTests = null;
                    RemoveSyntheticOutput(failed);
                }
            }
            else
                Console.WriteLine("NOTRUN: Unix unreadable chmod mutation on Windows; Windows ACL is separate.");

            // Index must be canonical: duplicate JSON, invented claims and changed SHA.
            byte[] duplicateJson = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(index.IndexUtf8).Replace(
                    "{\"Schema\":", "{\"Schema\":\"duplicate\",\"Schema\":"));
            Refuse(BridgeV2Refusal.InvalidPin, () =>
                BridgeV2OwnedWorkspacePublisher.Publish(
                    index with { IndexUtf8 = duplicateJson }, inputs, roots, live, golden));
            byte[] forged = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(index.IndexUtf8).Replace(
                    "{\"Schema\":", "{\"Status\":\"Verified\",\"Schema\":"));
            Refuse(BridgeV2Refusal.InvalidPin, () =>
                BridgeV2OwnedWorkspacePublisher.Publish(
                    index with { IndexUtf8 = forged }, inputs, roots, live, golden));
            Refuse(BridgeV2Refusal.InvalidPin, () =>
                BridgeV2OwnedWorkspacePublisher.Publish(
                    index with { Inputs = new[] { index.Inputs[0] with { Sha256 = new string('0', 64) }, index.Inputs[1] } },
                    inputs, roots, live, golden));
            Refuse(BridgeV2Refusal.InvalidPin, () =>
                BridgeV2OwnedWorkspacePublisher.Publish(index,
                    new[] { inputs[0] with { Role = "metadata-only" }, inputs[1] },
                    roots, live, golden));
            Refuse(BridgeV2Refusal.InvalidPin, () =>
                BridgeV2OwnedWorkspacePublisher.Publish(index,
                    new[] { inputs[0] with { Path = "../escape" }, inputs[1] },
                    roots, live, golden));
            Refuse(BridgeV2Refusal.InvalidPin, () =>
                BridgeV2OwnedWorkspacePublisher.Publish(index, inputs,
                    new[] { new BridgeRootSelectorV1("", "F", null,
                        Array.Empty<string>(), Array.Empty<string>()) }, live, golden));
            Refuse(BridgeV2Refusal.InvalidPin, () =>
                BridgeV2OwnedWorkspacePublisher.Publish(index, inputs,
                    new[] { roots[0], roots[0] }, live, golden));

            var conflictInputs = new[] {
                new BridgeV2Input("references", "source", current), inputs[1]
            };
            var conflictTransport = BridgeSegmentIndexV2.Write(roots, conflictInputs);
            Refuse(BridgeV2Refusal.InvalidRole, () =>
                BridgeV2OwnedWorkspacePublisher.Publish(conflictTransport, conflictInputs,
                    roots, new[] { live[0] with { Path = "references" } }, golden));
            // A swapped file symlink is refused even when its target has identical bytes.
            if (!OperatingSystem.IsWindows())
            {
                string external = Path.Combine(golden, "identical.cs");
                File.WriteAllBytes(external, current);
                try
                {
                    BridgeV2OwnedWorkspacePublisher.AfterSourcePreflightForTests = () => {
                        File.Delete(sourcePath);
                        File.CreateSymbolicLink(sourcePath, external);
                    };
                    Refuse(BridgeV2Refusal.InvalidRole, () =>
                        BridgeV2OwnedWorkspacePublisher.Publish(index, inputs, roots, live, golden));
                }
                finally
                {
                    BridgeV2OwnedWorkspacePublisher.AfterSourcePreflightForTests = null;
                    if (File.Exists(sourcePath)) File.Delete(sourcePath);
                    File.WriteAllBytes(sourcePath, current);
                    File.Delete(external);
                }
            }

            Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))) ==
                initialSha, "source bytes after negative cases differed");
            Console.WriteLine("PASS: HB-INT-02/03/06 owned v2 workspace byte publisher (synthetic Golden fixture only).");
        }
        finally
        {
            BridgeV2OwnedWorkspacePublisher.AfterSourcePreflightForTests = null;
            BridgeV2OwnedWorkspacePublisher.AfterStageWriteForTests = null;
            RemoveSyntheticOutput(published);
            if (Directory.Exists(golden))
                Directory.Delete(golden, true);
        }
    }
}
#endif
