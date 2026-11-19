// HB-INT-03: opt-in byte transport matching the proposed CW-INT-03 Core v2 reader.
// No IO, execution, HWIR, verification, or v1 serialization changes.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HybridCPU.Compiler.HardwareBridge.SourceSnapshot;

public sealed record BridgeV2Input(string Path, string Role, ReadOnlyMemory<byte> Bytes);
public sealed record BridgeV2FileRow(string Path, string Role, string Sha256, long Bytes);
public sealed record BridgeV2ChunkDescriptor(string Path, string Sha256, long Bytes, int Rows);
public sealed record BridgeV2ChunkContent(string Path, byte[] Bytes);
public sealed record BridgeV2SourceIndex(string Schema, IReadOnlyList<BridgeRootSelectorV1> Roots,
    IReadOnlyList<BridgeV2ChunkDescriptor> Chunks, string ClosureSha256);
public sealed record BridgeV2Transport(byte[] IndexUtf8, IReadOnlyList<BridgeV2ChunkContent> Chunks,
    IReadOnlyList<BridgeV2FileRow> Inputs, string ClosureSha256);

/// <summary>
/// Produces the *exact* compact JSON index and sorted canonical JSONL chunk layout
/// read by CircuitWeave CW-INT-03 PR #3. Caller owns publishing into a separate
/// immutable workspace; this codec does not write, reparse, or follow filesystem paths.
/// </summary>
public static class BridgeSegmentIndexV2
{
    public const string SchemaId = "circuitweave.bridge-source-closure-index/v2";
    public const int MaxIndexBytes = 1_048_576;
    public const int MaxChunkBytes = 4_194_304;
    public const int MaxChunks = 256;
    public const int MaxRows = 8192;
    public const long MaxWorkspaceBytes = 536_870_912;
    public const long MaxChunkManifestBytes = 67_108_864;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static BridgeV2Transport Write(IReadOnlyList<BridgeRootSelectorV1> roots,
        IReadOnlyList<BridgeV2Input> inputs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(inputs);
        cancellationToken.ThrowIfCancellationRequested();
        if (roots.Count is < 1 or > 32 || inputs.Count is < 1 or > MaxRows)
            throw new ArgumentException("HBINT03_V2_COUNT: root/input limit.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var selectedRoots = new List<BridgeRootSelectorV1>(roots.Count);
        foreach (BridgeRootSelectorV1 root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (root is null || !Identity(root.TypeName) || !Identity(root.MethodName) ||
                root.MetadataToken is < 0 || root.TypeArguments is null || root.MethodArguments is null ||
                root.TypeArguments.Count > 8 || root.MethodArguments.Count > 8 ||
                root.TypeArguments.Any(a => !Identity(a, 256)) ||
                root.MethodArguments.Any(a => !Identity(a, 256)) ||
                root.MetadataToken is null &&
                (root.TypeArguments.Count != 0 || root.MethodArguments.Count != 0))
                throw new ArgumentException("HBINT03_V2_ROOT: unsafe or ambiguous root.");
            var copy = new BridgeRootSelectorV1(root.TypeName, root.MethodName, root.MetadataToken,
                Array.AsReadOnly(root.TypeArguments.ToArray()),
                Array.AsReadOnly(root.MethodArguments.ToArray()));
            string key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(copy)));
            if (!keys.Add(key) || root.MetadataToken is null &&
                selectedRoots.Any(x => x.TypeName == root.TypeName && x.MethodName == root.MethodName))
                throw new ArgumentException("HBINT03_V2_ROOT: duplicate/ambiguous selector.");
            selectedRoots.Add(copy);
        }
        if (selectedRoots.Any(x => x.MetadataToken is null &&
            selectedRoots.Count(y => y.TypeName == x.TypeName && y.MethodName == x.MethodName) != 1))
            throw new ArgumentException("HBINT03_V2_ROOT: untokened overload.");

        var rows = new List<BridgeV2FileRow>(inputs.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        int sources = 0;
        foreach (BridgeV2Input input in inputs.OrderBy(x => x.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input is null || !SafeRelativePath(input.Path) || !seen.Add(input.Path) ||
                input.Role is not ("source" or "owned-body" or "metadata-only" or
                                   "unselected" or "compiler-pe" or "compiler-pdb"))
                throw new ArgumentException("HBINT03_V2_PATH_ROLE: duplicate, traversing, or unknown role.");
            long size = input.Bytes.Length;
            if (size < (input.Role == "source" ? 0 : 1) ||
                size > (input.Role == "source" ? 2_097_152 : 33_554_432) ||
                (total = checked(total + size)) > MaxWorkspaceBytes)
                throw new ArgumentException("HBINT03_V2_SIZE: file or aggregate budget.");
            if (input.Role == "source") sources++;
            rows.Add(new(input.Path, input.Role, Hash(input.Bytes.Span), size));
        }
        if (sources == 0)
            throw new ArgumentException("HBINT03_V2_SOURCE: at least one source is required.");

        var chunks = new List<BridgeV2ChunkContent>();
        var descriptors = new List<BridgeV2ChunkDescriptor>();
        using var closure = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var buffer = new MemoryStream();
        int chunkRows = 0;
        long chunkManifestBytes = 0;
        void Flush()
        {
            if (chunkRows == 0) return;
            if (chunks.Count >= MaxChunks)
                throw new ArgumentException("HBINT03_V2_CHUNKS: chunk budget.");
            string name = "bridge-index/chunk-" + chunks.Count.ToString("D4",
                System.Globalization.CultureInfo.InvariantCulture) + ".jsonl";
            if (seen.Contains(name)) throw new ArgumentException("HBINT03_V2_COLLISION: chunk/file path.");
            byte[] bytes = buffer.ToArray();
            chunkManifestBytes = checked(chunkManifestBytes + bytes.Length);
            if (chunkManifestBytes > MaxChunkManifestBytes)
                throw new ArgumentException("HBINT03_V2_CHUNK_SIZE: manifest budget.");
            closure.AppendData(bytes);
            chunks.Add(new(name, bytes));
            descriptors.Add(new(name, Hash(bytes), bytes.Length, chunkRows));
            buffer.SetLength(0);
            chunkRows = 0;
        }
        foreach (BridgeV2FileRow row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] line = JsonSerializer.SerializeToUtf8Bytes(row);
            if (line.Length + 1 > MaxChunkBytes)
                throw new ArgumentException("HBINT03_V2_ROW_SIZE: row cannot fit into chunk.");
            if (chunkRows == 64 || buffer.Length + line.Length + 1 > MaxChunkBytes) Flush();
            buffer.Write(line);
            buffer.WriteByte((byte)'\n');
            chunkRows++;
        }
        Flush();
        string digest = Convert.ToHexString(closure.GetHashAndReset());
        byte[] index = JsonSerializer.SerializeToUtf8Bytes(new BridgeV2SourceIndex(SchemaId,
            Array.AsReadOnly(selectedRoots.ToArray()), Array.AsReadOnly(descriptors.ToArray()), digest));
        if (index.Length > MaxIndexBytes)
            throw new ArgumentException("HBINT03_V2_INDEX_SIZE: index budget.");
        cancellationToken.ThrowIfCancellationRequested();
        return new(index, chunks.AsReadOnly(), rows.AsReadOnly(), digest);
    }

    public static bool SafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 ||
            path[0] == '/' || path.Contains('\\') || path.Contains(':') ||
            path.Any(char.IsControl) || !path.IsNormalized(NormalizationForm.FormC))
            return false;
        return path.Split('/').All(x => x.Length > 0 && x is not ("." or "..") &&
            !x.StartsWith(' ') && !x.EndsWith(' '));
    }

    private static bool Identity(string? value, int max = 512) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static bool IsSha(string? hash) => hash is { Length: 64 } &&
        hash.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
}
