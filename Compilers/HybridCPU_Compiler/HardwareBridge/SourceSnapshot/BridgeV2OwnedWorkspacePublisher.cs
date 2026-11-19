// HB-INT-02/03/06: opt-in staging of exact source-closure bytes for the Core v2 reader.
// This writes ONLY to a newly created owned OS-temp directory, never Golden.
// Neither SHA-256 nor a successful stage qualifies a method, RTL, or physical design.
using System.Buffers;
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

    public static BridgeV2PublishedWorkspace Publish(
        BridgeV2Transport transport,
        IReadOnlyList<BridgeV2Input> inputs,
        IReadOnlyList<BridgeRootSelectorV1> roots,
        IReadOnlyList<BridgeV2LiveSourcePin> liveSources,
        string goldenRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

        BridgeSegmentIndexV2.ValidateAgainstActualBytes(transport.IndexUtf8,
            transport.Chunks, roots, inputs, cancellationToken);
        if (transport.ClosureSha256 !=
                Convert.ToHexString(ConcatenatedChunkBytes(transport.Chunks, cancellationToken)))
            Refuse(BridgeV2Refusal.InvalidPin, "Transport closure digest changed.");

        var rows = transport.Inputs.Where(x => x.Role == "source").ToArray();
        if (rows.Length != liveSources.Count || rows.Length == 0)
            Refuse(BridgeV2Refusal.MissingInput, "Every source needs an exact current file pin.");

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pins = new List<(string Full, string Hash, long Length)>(rows.Length);
        int goldenCount = 0;
        foreach (BridgeV2LiveSourcePin pin in liveSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        foreach (BridgeV2Input input in inputs)
            if (!occupied.Add(input.Path))
                Refuse(BridgeV2Refusal.InvalidRole,
                    "Input overlaps reserved index or a duplicate file path.");
        foreach (BridgeV2ChunkContent chunk in transport.Chunks)
            if (chunk is null || !BridgeSegmentIndexV2.SafeRelativePath(chunk.Path) ||
                !occupied.Add(chunk.Path))
                Refuse(BridgeV2Refusal.InvalidRole,
                    "Chunk overlaps source or reserved index path.");

        string? output = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            output = Directory.CreateTempSubdirectory(Prefix).FullName;
            if (!Inside(output, temp) || Inside(output, golden))
                Refuse(BridgeV2Refusal.InvalidRole, "New owned temp output is not isolated.");
            AssertNoReparse(output);

            // FileMode.CreateNew and a private fresh directory: no preexisting file overwritten.
            WriteNew(output, IndexFileName, transport.IndexUtf8, cancellationToken);
            foreach (BridgeV2ChunkContent chunk in transport.Chunks)
                WriteNew(output, chunk.Path, chunk.Bytes, cancellationToken);
            foreach (BridgeV2Input input in inputs)
                WriteNew(output, input.Path, input.Bytes, cancellationToken);

            CheckFileBytes(Path.Combine(output, IndexFileName),
                BridgeSegmentIndexV2.Hash(transport.IndexUtf8), transport.IndexUtf8.Length,
                cancellationToken);
            foreach (BridgeV2ChunkContent chunk in transport.Chunks)
                CheckFileBytes(Path.Combine(output, chunk.Path),
                    BridgeSegmentIndexV2.Hash(chunk.Bytes), chunk.Bytes.Length, cancellationToken);
            foreach (BridgeV2FileRow row in transport.Inputs)
                CheckFileBytes(Path.Combine(output, row.Path), row.Sha256, row.Bytes,
                    cancellationToken);

            // Detect changes in the actual read-only Golden source while publishing.
            foreach (var pin in pins)
                CheckFileBytes(pin.Full, pin.Hash, pin.Length, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(output, Path.Combine(output, IndexFileName),
                BridgeSegmentIndexV2.Hash(transport.IndexUtf8), transport.ClosureSha256);
        }
        catch
        {
            if (output is not null)
            {
                // Only the exclusively created directory may be removed after a refusal.
                // Retain the failure; never fall back to a mutable or Golden directory.
                try
                {
                    if (Directory.Exists(output) && !IsReparse(output))
                        Directory.Delete(output, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    private static byte[] ConcatenatedChunkBytes(
        IReadOnlyList<BridgeV2ChunkContent> chunks, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var chunk in chunks)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(chunk.Bytes);
        }
        return hash.GetHashAndReset();
    }

    private static void WriteNew(string workspace, string relative,
        ReadOnlyMemory<byte> data, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
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
            token.ThrowIfCancellationRequested();
            int take = Math.Min(CopyBlock, data.Length - offset);
            file.Write(data.Span.Slice(offset, take));
            offset += take;
        }
        file.Flush(flushToDisk: true);
    }

    private static void CheckFileBytes(string full, string hash, long bytes,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        full = ExistingFile(full);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read,
            FileShare.Read, CopyBlock, FileOptions.SequentialScan);
        if (stream.Length != bytes)
            Refuse(BridgeV2Refusal.StaleSource, "Actual file size changed: " + full);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBlock);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, CopyBlock)) != 0)
            {
                token.ThrowIfCancellationRequested();
                digest.AppendData(buffer.AsSpan(0, read));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        if (Convert.ToHexString(digest.GetHashAndReset()) != hash ||
            stream.Length != bytes)
            Refuse(BridgeV2Refusal.StaleSource, "Actual file SHA/size changed: " + full);
    }

    private static string ExistingFile(string? path)
    {
        string full = FullAbsolute(path);
        AssertNoReparse(full);
        if (!File.Exists(full))
            Refuse(BridgeV2Refusal.MissingInput, "Missing source: " + full);
        return full;
    }

    private static string ExistingDirectory(string? path)
    {
        string full = FullAbsolute(path);
        AssertNoReparse(full);
        if (!Directory.Exists(full))
            Refuse(BridgeV2Refusal.MissingInput, "Missing directory: " + full);
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
                Refuse(BridgeV2Refusal.InvalidRole, "Symlink or reparse point forbidden: " + p);
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

    private static void Refuse(BridgeV2Refusal code, string message) =>
        throw new BridgeV2RefusalException(code, message);
}
