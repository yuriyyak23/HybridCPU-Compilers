// HB-INT-02/03/06: opt-in staging of exact source-closure bytes for the Core v2 reader.
// This writes ONLY to a newly created owned OS-temp directory, never Golden.
// Neither SHA-256 nor a successful stage qualifies a method, RTL, or physical design.
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace HybridCPU.Compiler.HardwareBridge.SourceSnapshot;

public sealed record BridgeV2LiveSourcePin(string Path, string Origin, string AbsolutePath);

/// <summary>Path and hashes of *unqualified* immutable-byte transport; not a proof receipt.</summary>
public sealed record BridgeV2PublishedWorkspace(
    string Root, string IndexPath, string IndexSha256, string ClosureSha256);

/// <summary>
/// Stages a prevalidated v2 index, raw chunks and actual source/reference/compiler
/// bytes in an exclusively new OS-temp workspace for Core-owned independent import.
/// A live Golden/worktree source is read twice and never written. No Csc, Roslyn,
/// dynamic loading, target execution, external tools or independent verifier here.
/// </summary>
public static class BridgeV2OwnedWorkspacePublisher
{
    public const string IndexFileName = "index.json";
    private const string Prefix = "hb-int-v2-";
    private const int CopyBlock = 64 * 1024;
#if HARDWAREBRIDGE_TESTS
    // Disk-mutation seams are excluded from production assemblies.
    internal static Action? AfterSourcePreflightForTests;
    internal static Action<string>? AfterStageWriteForTests;
    internal static Action<string, int>? AfterCopyBlockForTests;
    internal static Action<string>? AfterSealForTests;
#endif

    public static BridgeV2PublishedWorkspace Publish(
        BridgeV2Transport transport,
        IReadOnlyList<BridgeV2Input> inputs,
        IReadOnlyList<BridgeRootSelectorV1> roots,
        IReadOnlyList<BridgeV2LiveSourcePin> liveSources,
        string goldenRoot,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            throw new BridgeV2RefusalException(BridgeV2Refusal.Cancelled,
                "Publication cancelled before any output.");
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(liveSources);

        // Check existing Golden root and OS temp ancestry before allocating any output.
        string golden = ExistingDirectory(goldenRoot);
        string temp = ExistingDirectory(Path.GetTempPath());
        if (Inside(temp, golden))
            Refuse(BridgeV2Refusal.InvalidRole,
                "OS temp is inside the Golden root; publication is not isolated.");

        // Freeze untrusted caller-owned byte arrays once, then validate and write
        // ONLY those same bytes. Transport.Inputs must match regenerated row roles.
        byte[] index = (byte[])transport.IndexUtf8.Clone();
        BridgeV2ChunkContent[] chunks = transport.Chunks.Select(c =>
            c is null ? throw new BridgeV2RefusalException(BridgeV2Refusal.InvalidPin,
                "Null chunk.") : new BridgeV2ChunkContent(c.Path, (byte[])c.Bytes.Clone())).ToArray();
        BridgeV2Input[] frozenInputs = inputs.Select(i =>
            i is null ? throw new BridgeV2RefusalException(BridgeV2Refusal.InvalidPin,
                "Null input.") : new BridgeV2Input(i.Path, i.Role, i.Bytes.ToArray())).ToArray();
        BridgeSegmentIndexV2.ValidateAgainstActualBytes(index, chunks, roots,
            frozenInputs, cancellationToken);
        BridgeV2Transport canonical = BridgeSegmentIndexV2.Write(roots, frozenInputs,
            cancellationToken);
        if (transport.Inputs is null || transport.Inputs.Count != canonical.Inputs.Count ||
            !transport.Inputs.SequenceEqual(canonical.Inputs) ||
            transport.ClosureSha256 != canonical.ClosureSha256 ||
            transport.ClosureSha256 !=
                Convert.ToHexString(ConcatenatedChunkBytes(chunks, cancellationToken)))
            Refuse(BridgeV2Refusal.InvalidPin, "Transport row/closure identity changed.");

        var rows = canonical.Inputs.Where(x => x.Role == "source").ToArray();
        if (rows.Length != liveSources.Count || rows.Length == 0)
            Refuse(BridgeV2Refusal.MissingInput, "Every source needs an exact current file pin.");

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pins = new List<(string Full, string Hash, long Length)>(rows.Length);
        int goldenCount = 0;
        foreach (BridgeV2LiveSourcePin pin in liveSources)
        {
            ThrowIfCancelled(cancellationToken);
            if (pin is null || !BridgeSegmentIndexV2.SafeRelativePath(pin.Path) ||
                !unique.Add(pin.Path) ||
                pin.Origin is not ("original" or "working" or "generated"))
                Refuse(BridgeV2Refusal.InvalidRole, "Missing/duplicate/unknown source path or origin.");
            BridgeV2FileRow? row = rows.SingleOrDefault(x => x.Path == pin.Path);
            if (row is null)
                Refuse(BridgeV2Refusal.MissingInput, "Live source not present in transport.");
            string full = ExistingFile(pin.AbsolutePath);
            bool inGolden = Inside(full, golden);
            if (pin.Origin is "original" or "working")
            {
                if (!inGolden)
                    Refuse(BridgeV2Refusal.InvalidRole,
                        "Original/working source must be under pinned Golden root.");
                goldenCount++;
            }
            else if (inGolden)
                Refuse(BridgeV2Refusal.InvalidRole,
                    "Generator output must be external to the Golden root.");
            // Check exact currently readable bytes, not Git HEAD or caller assertion.
            CheckFileBytes(full, row.Sha256, row.Bytes, cancellationToken);
            pins.Add((full, row.Sha256, row.Bytes));
        }
        if (goldenCount == 0)
            Refuse(BridgeV2Refusal.InvalidRole, "At least one Golden/worktree source pin required.");

        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { IndexFileName };
        foreach (BridgeV2Input input in frozenInputs)
            if (!occupied.Add(input.Path))
                Refuse(BridgeV2Refusal.InvalidRole,
                    "Input overlaps reserved index or a duplicate file path.");
        foreach (BridgeV2ChunkContent chunk in chunks)
            if (!BridgeSegmentIndexV2.SafeRelativePath(chunk.Path) ||
                !occupied.Add(chunk.Path))
                Refuse(BridgeV2Refusal.InvalidRole,
                    "Chunk overlaps source or reserved index path.");
        var expectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in occupied)
        {
            for (int slash = relative.IndexOf('/'); slash >= 0;
                slash = relative.IndexOf('/', slash + 1))
            {
                string dir = relative[..slash];
                if (occupied.Contains(dir))
                    Refuse(BridgeV2Refusal.InvalidRole, "File/directory collision.");
                expectedDirs.Add(dir);
            }
        }
#if HARDWAREBRIDGE_TESTS
        AfterSourcePreflightForTests?.Invoke();
#endif
        string? output = null;
        try
        {
            ThrowIfCancelled(cancellationToken);
            output = Directory.CreateTempSubdirectory(Prefix).FullName;
            if (!Inside(output, temp) || Inside(output, golden))
                Refuse(BridgeV2Refusal.InvalidRole, "New owned temp output is not isolated.");
            AssertNoReparse(output);

            // FileMode.CreateNew and a private fresh directory: no preexisting file overwritten.
            WriteNew(output, IndexFileName, index, cancellationToken);
            foreach (BridgeV2ChunkContent chunk in chunks)
                WriteNew(output, chunk.Path, chunk.Bytes, cancellationToken);
            foreach (BridgeV2Input input in frozenInputs)
                WriteNew(output, input.Path, input.Bytes, cancellationToken);
#if HARDWAREBRIDGE_TESTS
            AfterStageWriteForTests?.Invoke(output);
#endif
            VerifyStaged(output, occupied, expectedDirs, index, chunks, canonical.Inputs,
                cancellationToken);
            foreach (var pin in pins)
                CheckFileBytes(pin.Full, pin.Hash, pin.Length, cancellationToken);
            ThrowIfCancelled(cancellationToken);
            SealStaged(output, occupied, expectedDirs);
#if HARDWAREBRIDGE_TESTS
            AfterSealForTests?.Invoke(output);
#endif
            VerifyStaged(output, occupied, expectedDirs, index, chunks, canonical.Inputs,
                cancellationToken);
            VerifySealed(output, occupied, expectedDirs);
            foreach (var pin in pins)
                CheckFileBytes(pin.Full, pin.Hash, pin.Length, cancellationToken);
            ThrowIfCancelled(cancellationToken);
            return new(output, Path.Combine(output, IndexFileName),
                BridgeSegmentIndexV2.Hash(index), canonical.ClosureSha256);
        }
        catch (UnauthorizedAccessException)
        {
            CleanupOwned(output, occupied, expectedDirs);
            throw new BridgeV2RefusalException(BridgeV2Refusal.InvalidPin,
                "Unreadable staged workspace.");
        }
        catch (IOException)
        {
            CleanupOwned(output, occupied, expectedDirs);
            throw new BridgeV2RefusalException(BridgeV2Refusal.InvalidPin,
                "Staged workspace I/O failure.");
        }
        catch
        {
            CleanupOwned(output, occupied, expectedDirs);
            throw;
        }
    }

    private static void ThrowIfCancelled(CancellationToken token)
    {
        if (token.IsCancellationRequested)
            Refuse(BridgeV2Refusal.Cancelled, "Publication cancelled.");
    }

    private static string StagedPath(string output, string relative) =>
        Path.Combine(output, relative.Replace('/', Path.DirectorySeparatorChar));

    private static void VerifyStaged(string output, HashSet<string> expectedFiles,
        HashSet<string> expectedDirs, byte[] index,
        IReadOnlyList<BridgeV2ChunkContent> chunks,
        IReadOnlyList<BridgeV2FileRow> rows, CancellationToken token)
    {
        var remaining = new HashSet<string>(expectedFiles, StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(expectedDirs, StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(output);
        while (pending.Count != 0)
        {
            ThrowIfCancelled(token);
            string directory = pending.Pop();
            AssertNoReparse(directory);
            foreach (string child in Directory.EnumerateFileSystemEntries(directory))
            {
                AssertNoReparse(child);
                string relative = Path.GetRelativePath(output, child)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!BridgeSegmentIndexV2.SafeRelativePath(relative))
                    Refuse(BridgeV2Refusal.InvalidRole, "Unsafe staged descendant.");
                FileAttributes attrs = File.GetAttributes(child);
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    if (!dirs.Remove(relative))
                        Refuse(BridgeV2Refusal.InvalidPin, "Untracked staged directory.");
                    pending.Push(child);
                }
                else if (!remaining.Remove(relative))
                    Refuse(BridgeV2Refusal.InvalidPin, "Untracked staged file.");
            }
        }
        if (remaining.Count != 0 || dirs.Count != 0)
            Refuse(BridgeV2Refusal.InvalidPin, "Missing staged file/directory.");
        CheckFileBytes(StagedPath(output, IndexFileName),
            BridgeSegmentIndexV2.Hash(index), index.Length, token);
        foreach (var chunk in chunks)
            CheckFileBytes(StagedPath(output, chunk.Path),
                BridgeSegmentIndexV2.Hash(chunk.Bytes), chunk.Bytes.Length, token);
        foreach (var row in rows)
            CheckFileBytes(StagedPath(output, row.Path), row.Sha256, row.Bytes, token);
    }

    private static void SealStaged(string output, HashSet<string> expectedFiles,
        HashSet<string> expectedDirs)
    {
        foreach (string relative in expectedFiles)
        {
            string path = StagedPath(output, relative);
            AssertNoReparse(path);
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }
        // Advisory on Windows: Core rehash remains mandatory. On Unix also
        // revoke ordinary directory writes, excluding hostile same-user chmod.
        if (!OperatingSystem.IsWindows())
        {
            foreach (string relative in expectedDirs.OrderByDescending(x => x.Length))
            {
                string path = StagedPath(output, relative);
                AssertNoReparse(path);
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            AssertNoReparse(output);
            File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    // Verify the sealing contract rather than inferring it from SetAttributes success.
    // These modes are discretionary protections only; Core must rehash independently.
    private static void VerifySealed(string output, HashSet<string> expectedFiles,
        HashSet<string> expectedDirs)
    {
        AssertNoReparse(output);
        foreach (string relative in expectedFiles)
        {
            string path = StagedPath(output, relative);
            AssertNoReparse(path);
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) == 0)
                Refuse(BridgeV2Refusal.InvalidPin, "Staged file lost its read-only seal.");
        }
        if (!OperatingSystem.IsWindows())
        {
            foreach (string directory in expectedDirs.Select(x => StagedPath(output, x))
                .Append(output))
            {
                AssertNoReparse(directory);
                if ((File.GetUnixFileMode(directory) & UnixFileMode.UserWrite) != 0)
                    Refuse(BridgeV2Refusal.InvalidPin,
                        "Owned Unix workspace directory remains writable.");
            }
        }
    }

    private static void CleanupOwned(string? output, HashSet<string> expectedFiles,
        HashSet<string> expectedDirs)
    {
        if (output is null) return;
        try
        {
            if (!Directory.Exists(output) || IsReparse(output)) return;
            // Never use recursive Delete on an untrusted/replaced descendant tree.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(output, UnixFileMode.UserRead |
                    UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (string relative in expectedDirs.OrderBy(x => x.Length))
            {
                string path = StagedPath(output, relative);
                if (Directory.Exists(path) && !IsReparse(path) && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead |
                        UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            foreach (string relative in expectedFiles)
            {
                string path = StagedPath(output, relative);
                if (!File.Exists(path) || IsReparse(path)) continue;
                AssertNoReparse(path);
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            foreach (string relative in expectedDirs.OrderByDescending(x => x.Length))
            {
                string path = StagedPath(output, relative);
                if (Directory.Exists(path) && !IsReparse(path))
                    Directory.Delete(path, recursive: false);
            }
            Directory.Delete(output, recursive: false);
        }
        catch (IOException) { /* Unknown descendant is preserved, never traversed. */ }
        catch (UnauthorizedAccessException) { /* Fail closed: do not delete foreign paths. */ }
        catch (BridgeV2RefusalException) { /* Reparse replacement: do not delete. */ }
    }

    private static byte[] ConcatenatedChunkBytes(
        IReadOnlyList<BridgeV2ChunkContent> chunks, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var chunk in chunks)
        {
            ThrowIfCancelled(token);
            hash.AppendData(chunk.Bytes);
        }
        return hash.GetHashAndReset();
    }

    private static void WriteNew(string workspace, string relative,
        ReadOnlyMemory<byte> data, CancellationToken token)
    {
        ThrowIfCancelled(token);
        if (!BridgeSegmentIndexV2.SafeRelativePath(relative))
            Refuse(BridgeV2Refusal.InvalidRole, "Unsafe staged file path.");
        string full = Path.GetFullPath(Path.Combine(workspace, relative.Replace('/',
            Path.DirectorySeparatorChar)));
        if (!Inside(full, workspace) || full == workspace)
            Refuse(BridgeV2Refusal.InvalidRole, "Path escaped the exclusively owned workspace.");
        string parent = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(parent);
        AssertNoReparse(parent);
        using var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, CopyBlock, FileOptions.SequentialScan);
        for (int offset = 0; offset < data.Length;)
        {
            ThrowIfCancelled(token);
            int take = Math.Min(CopyBlock, data.Length - offset);
            file.Write(data.Span.Slice(offset, take));
            offset += take;
#if HARDWAREBRIDGE_TESTS
            AfterCopyBlockForTests?.Invoke(relative, offset);
#endif
        }
        file.Flush(flushToDisk: true);
    }

    private static void CheckFileBytes(string full, string hash, long bytes,
        CancellationToken token)
    {
        ThrowIfCancelled(token);
        full = ExistingFile(full);
        DateTime initialWrite = File.GetLastWriteTimeUtc(full);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read,
            FileShare.Read, CopyBlock, FileOptions.SequentialScan);
        if (stream.Length != bytes)
            Refuse(BridgeV2Refusal.StaleSource, "Actual file size changed.");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBlock);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, CopyBlock)) != 0)
            {
                ThrowIfCancelled(token);
                digest.AppendData(buffer.AsSpan(0, read));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        AssertNoReparse(full);
        if (Convert.ToHexString(digest.GetHashAndReset()) != hash ||
            stream.Length != bytes || File.GetLastWriteTimeUtc(full) != initialWrite)
            Refuse(BridgeV2Refusal.StaleSource, "Actual file SHA/size/time changed.");
    }

    private static string ExistingFile(string? path)
    {
        string full = FullAbsolute(path);
        AssertNoReparse(full);
        if (!File.Exists(full))
            Refuse(BridgeV2Refusal.MissingInput, "Missing source.");
        return full;
    }

    private static string ExistingDirectory(string? path)
    {
        string full = FullAbsolute(path);
        AssertNoReparse(full);
        if (!Directory.Exists(full))
            Refuse(BridgeV2Refusal.MissingInput, "Missing directory.");
        return full;
    }

    private static string FullAbsolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            Refuse(BridgeV2Refusal.InvalidRole, "Absolute filesystem path required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!));
    }

    private static void AssertNoReparse(string full)
    {
        for (string? p = full; p is not null; p = Path.GetDirectoryName(p))
        {
            if (IsReparse(p))
                Refuse(BridgeV2Refusal.InvalidRole, "Symlink or reparse point forbidden.");
            string parent = Path.GetDirectoryName(p) ?? "";
            if (parent == p) break;
        }
    }

    private static bool IsReparse(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool Inside(string path, string root)
    {
        string? relative = Path.GetRelativePath(root, path);
        if (relative == ".") return true;
        return relative.Length > 0 && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    [DoesNotReturn]
    private static void Refuse(BridgeV2Refusal code, string message) =>
        throw new BridgeV2RefusalException(code, message);
}
