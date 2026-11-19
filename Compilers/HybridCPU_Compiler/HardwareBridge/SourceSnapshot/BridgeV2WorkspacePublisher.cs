// HB-INT-03: opt-in, read-only-input staging for the existing Core-compatible v2
// source index. No hardware authority, source compilation, Golden execution or
// changes to sidecar/v1, provenance/v2 or the Core v2 JSON contract.
using System.Security.Cryptography;

namespace HybridCPU.Compiler.HardwareBridge.SourceSnapshot;

public sealed record BridgeV2WorkspacePublication(
    string WorkspacePath, string IndexSha256, string ClosureSha256,
    int FileCount, int ChunkCount, long InputBytes);

/// <summary>
/// Materializes an already constructed, byte-checked v2 transport in a fresh,
/// caller-owned directory, then atomically renames it into place. Input bytes
/// are borrowed and rechecked before/after each copy; they are never modified.
/// The caller MUST supply all model/compiler/Core roots to exclude and must
/// independently verify that the captured input bytes represent the actual
/// upstream Csc invocation. A publication is unqualified software facts only.
/// </summary>
public static class BridgeV2WorkspacePublisher
{
    private const string IndexPath = "bridge-index/index.json";

    public static BridgeV2WorkspacePublication Publish(
        string outputDirectory, IReadOnlyList<string> forbiddenSourceRoots,
        BridgeV2Transport transport, IReadOnlyList<BridgeRootSelectorV1> roots,
        IReadOnlyList<BridgeV2Input> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(forbiddenSourceRoots);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(inputs);
        cancellationToken.ThrowIfCancellationRequested();
        if (forbiddenSourceRoots.Count == 0 || forbiddenSourceRoots.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("HBINT03_WORKSPACE_ROOTS: explicit source exclusion roots required.");

        // Verify the exact bytes against the existing production v2 codec first.
        // The codec refuses ambiguous inputs, path collisions, unsupported roles
        // and changed indexed root/chunk identities.
        BridgeSegmentIndexV2.ValidateAgainstActualBytes(transport.IndexUtf8,
            transport.Chunks, roots, inputs, cancellationToken);

        string target = Path.GetFullPath(outputDirectory);
        string? parent = Path.GetDirectoryName(target);
        if (parent is null || !Directory.Exists(parent) ||
            Path.GetFileName(target) is "" or "." or ".." ||
            File.Exists(target) || Directory.Exists(target) ||
            !IsPhysicalPath(parent))
            throw new ArgumentException("HBINT03_WORKSPACE_PATH: output must be a NEW child of an existing, non-linked directory.");

        var excluded = new List<string>(forbiddenSourceRoots.Count);
        foreach (string root in forbiddenSourceRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = Path.GetFullPath(root);
            if (!Directory.Exists(normalized) || !IsPhysicalPath(normalized))
                throw new ArgumentException("HBINT03_WORKSPACE_ROOTS: source exclusions must be existing, non-linked directories.");
            excluded.Add(normalized);
            if (UnderOrEqual(target, normalized) || UnderOrEqual(normalized, target))
                throw new ArgumentException("HBINT03_WORKSPACE_PATH: workspace overlaps a protected source tree.");
        }

        var expectedPaths = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (BridgeV2Input input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input is null || !BridgeSegmentIndexV2.SafeRelativePath(input.Path) ||
                !expectedPaths.Add(input.Path))
                throw new ArgumentException("HBINT03_WORKSPACE_INPUT: duplicate or unsafe file path.");
        }
        if (!expectedPaths.Add(IndexPath))
            throw new ArgumentException("HBINT03_WORKSPACE_COLLISION: source overwrites the index.");
        foreach (BridgeV2ChunkContent chunk in transport.Chunks)
        {
            if (chunk is null || !BridgeSegmentIndexV2.SafeRelativePath(chunk.Path) ||
                !expectedPaths.Add(chunk.Path))
                throw new ArgumentException("HBINT03_WORKSPACE_COLLISION: source overwrites a v2 chunk.");
        }

        // The random staging directory is a direct sibling, on the same
        // filesystem as target; only a completed stage becomes visible there.
        string stage = Path.Combine(parent, ".hb-v2-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        bool committed = false;
        try
        {
            if (!IsPhysicalPath(stage))
                throw new ArgumentException("HBINT03_WORKSPACE_LINK: linked staging directory.");
            cancellationToken.ThrowIfCancellationRequested();

            // Write raw captured source, references and compiler output in their
            // exact logical paths; no writes are directed into protected trees.
            long total = 0;
            foreach (BridgeV2Input input in inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteExactly(stage, input.Path, input.Bytes.Span,
                    BridgeSegmentIndexV2.Hash(input.Bytes.Span), cancellationToken);
                total = checked(total + input.Bytes.Length);
            }
            WriteExactly(stage, IndexPath, transport.IndexUtf8,
                BridgeSegmentIndexV2.Hash(transport.IndexUtf8), cancellationToken);
            foreach (BridgeV2ChunkContent chunk in transport.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteExactly(stage, chunk.Path, chunk.Bytes,
                    BridgeSegmentIndexV2.Hash(chunk.Bytes), cancellationToken);
            }

            // Re-evaluate the mutable caller buffers and all original expected
            // rows after staging, rather than declaring completion on copy exit.
            BridgeSegmentIndexV2.ValidateAgainstActualBytes(transport.IndexUtf8,
                transport.Chunks, roots, inputs, cancellationToken);
            foreach (BridgeV2FileRow row in transport.Inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string disk = InStage(stage, row.Path);
                using var file = File.OpenRead(disk);
                if (file.Length != row.Bytes || Digest(file) != row.Sha256)
                    throw new IOException("HBINT03_WORKSPACE_MUTATED: staged input bytes changed.");
            }

            // Fail closed on concurrent output creation, linked ancestor or
            // cancellation. Move is atomic on the same volume if it succeeds.
            if (!IsPhysicalPath(parent) || Directory.Exists(target) ||
                File.Exists(target))
                throw new IOException("HBINT03_WORKSPACE_RACE: destination or ancestor changed.");
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stage, target);
            committed = true;
            return new BridgeV2WorkspacePublication(target,
                BridgeSegmentIndexV2.Hash(transport.IndexUtf8),
                transport.ClosureSha256, inputs.Count, transport.Chunks.Count, total);
        }
        finally
        {
            // Best-effort removal of our *own* incomplete staging directory.
            // Never remove the destination or files outside this temp child.
            if (!committed && Directory.Exists(stage) && IsPhysicalPath(stage))
                Directory.Delete(stage, recursive: true);
        }
    }

    private static void WriteExactly(string root, string relative,
        ReadOnlySpan<byte> bytes, string expectedSha, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string path = InStage(root, relative);
        string parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        if (!IsPhysicalPath(parent))
            throw new IOException("HBINT03_WORKSPACE_LINK: linked output parent.");
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("HBINT03_WORKSPACE_COLLISION: output already exists.");
        if (BridgeSegmentIndexV2.Hash(bytes) != expectedSha)
            throw new IOException("HBINT03_WORKSPACE_MUTATED: caller bytes changed before write.");
        using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            target.Write(bytes);
            target.Flush(flushToDisk: true);
        }
        if (BridgeSegmentIndexV2.Hash(bytes) != expectedSha)
            throw new IOException("HBINT03_WORKSPACE_MUTATED: caller bytes changed during write.");
        using var disk = File.OpenRead(path);
        if (disk.Length != bytes.Length || Digest(disk) != expectedSha)
            throw new IOException("HBINT03_WORKSPACE_MUTATED: written bytes mismatch.");
    }

    private static string InStage(string root, string path)
    {
        if (!BridgeSegmentIndexV2.SafeRelativePath(path))
            throw new ArgumentException("HBINT03_WORKSPACE_PATH: unsafe relative path.");
        string full = Path.GetFullPath(Path.Combine(root,
            path.Replace('/', Path.DirectorySeparatorChar)));
        if (!UnderOrEqual(full, root) || full == root)
            throw new ArgumentException("HBINT03_WORKSPACE_PATH: path escapes staging.");
        return full;
    }

    private static string Digest(Stream file) =>
        Convert.ToHexString(SHA256.HashData(file));

    private static bool IsPhysicalPath(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                return false;
        }
        return true;
    }

    private static bool UnderOrEqual(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(path, root, comparison) ||
            path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison);
    }
}
