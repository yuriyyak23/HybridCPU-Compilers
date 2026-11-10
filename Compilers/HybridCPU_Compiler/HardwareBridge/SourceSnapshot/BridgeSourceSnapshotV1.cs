// Read-only, bounded source/reference identity for candidate diagnostics; not a binding or source proof.
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HybridCPU.Compiler.HardwareBridge.SourceSnapshot;

/// <summary>Caller-supplied virtual text; no filesystem lookup or source reconstruction.</summary>
public sealed record BridgeSourceTextV1(string VirtualPath, string Text);

/// <summary>Explicit inputs, with reference indices relative to this ordered list.</summary>
public sealed record BridgeSourceRequestV1(
    string AssemblyName, string LanguageHostMode,
    IReadOnlyList<BridgeSourceTextV1> Documents,
    IReadOnlyList<ReadOnlyMemory<byte>> ReferenceImages,
    IReadOnlyList<BridgeRootSelectorV1> Roots,
    IReadOnlyList<int> BodyReferenceIndices,
    IReadOnlyList<int> MetadataReferenceIndices);

/// <summary>Root selector, including exact metadata token and generic arguments when supplied.</summary>
public sealed record BridgeRootSelectorV1(
    string TypeName, string MethodName, int? MetadataToken,
    IReadOnlyList<string> TypeArguments, IReadOnlyList<string> MethodArguments);

public sealed record BridgeDocumentDigestV1(string VirtualPath, string Sha256, int Utf8Bytes);
public sealed record BridgeReferenceDigestV1(int Index, string Sha256, int Bytes, string Role);

/// <summary>
/// Immutable diagnostic identity of the explicit, ordered input. SnapshotDigest is BRIDGE-specific:
/// it must never be called the Companion SourceInputDigest or a hardware qualification.
/// </summary>
public sealed record BridgeSourceSnapshotV1(
    string Schema, string AssemblyName, string LanguageHostMode, string AdmissionPolicy,
    string CompilerVersion, string RoslynVersion,
    IReadOnlyList<BridgeDocumentDigestV1> Documents,
    IReadOnlyList<BridgeReferenceDigestV1> References,
    IReadOnlyList<BridgeRootSelectorV1> Roots,
    IReadOnlyList<int> BodyReferenceIndices,
    IReadOnlyList<int> MetadataReferenceIndices,
    string SnapshotDigest)
{
    public const string SchemaId = "hybridcpu.hardwarebridge.source-snapshot/v1";
    public const int MaxDocuments = 128, MaxReferences = 128, MaxRoots = 32;
    public const int MaxDocumentUtf8Bytes = 1_048_576, MaxReferenceBytes = 16_777_216;
    public const long MaxTotalBytes = 67_108_864;

    public byte[] ToJsonUtf8() => JsonSerializer.SerializeToUtf8Bytes(this);
    /// <summary>Re-check self-consistency; does not authenticate the caller or external compiler.</summary>
    public bool HasConsistentDigest()
    {
        if (Schema != SchemaId || Documents is null || References is null || Roots is null ||
            BodyReferenceIndices is null || MetadataReferenceIndices is null ||
            Documents.Count is < 1 or > MaxDocuments || References.Count > MaxReferences ||
            Roots.Count is < 1 or > MaxRoots ||
            !BodyReferenceIndices.All(i => i >= 0 && i < References.Count) ||
            !MetadataReferenceIndices.All(i => i >= 0 && i < References.Count) ||
            BodyReferenceIndices.Distinct().Count() != BodyReferenceIndices.Count ||
            MetadataReferenceIndices.Distinct().Count() != MetadataReferenceIndices.Count ||
            BodyReferenceIndices.Intersect(MetadataReferenceIndices).Any() ||
            Documents.Any(d => d is null || !SafeVirtualPath(d.VirtualPath) ||
                !IsHexHash(d.Sha256) || d.Utf8Bytes < 0 || d.Utf8Bytes > MaxDocumentUtf8Bytes) ||
            Documents.Select(d => d.VirtualPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Documents.Count ||
            References.Where((r, i) => r is null || r.Index != i ||
                !IsHexHash(r.Sha256) || r.Bytes is < 1 or > MaxReferenceBytes ||
                r.Role != (BodyReferenceIndices.Contains(i) ? "owned-body" :
                           MetadataReferenceIndices.Contains(i) ? "metadata-only" : "unselected")).Any() ||
            References.Select(r => r.Sha256).Distinct(StringComparer.Ordinal).Count() != References.Count ||
            Roots.Any(r => r is null || string.IsNullOrWhiteSpace(r.TypeName) ||
                string.IsNullOrWhiteSpace(r.MethodName) || r.TypeArguments is null ||
                r.MethodArguments is null || r.TypeArguments.Count > 8 || r.MethodArguments.Count > 8) ||
            Documents.Sum(d => (long)d.Utf8Bytes) + References.Sum(r => (long)r.Bytes) > MaxTotalBytes)
            return false;
        return string.Equals(SnapshotDigest, Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema, AssemblyName, LanguageHostMode, AdmissionPolicy, CompilerVersion, RoslynVersion,
            Documents, References, Roots, BodyReferenceIndices, MetadataReferenceIndices
        })), StringComparison.Ordinal);
    }

    private static bool IsHexHash(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);


    public static BridgeSourceSnapshotV1 Capture(
        BridgeSourceRequestV1 input, string admissionPolicy, string compilerVersion,
        string roslynVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        RequireIdentity(input.AssemblyName, nameof(input.AssemblyName));
        RequireIdentity(input.LanguageHostMode, nameof(input.LanguageHostMode));
        RequireIdentity(admissionPolicy, nameof(admissionPolicy));
        RequireIdentity(compilerVersion, nameof(compilerVersion));
        RequireIdentity(roslynVersion, nameof(roslynVersion));
        ArgumentNullException.ThrowIfNull(input.Documents);
        ArgumentNullException.ThrowIfNull(input.ReferenceImages);
        ArgumentNullException.ThrowIfNull(input.Roots);
        ArgumentNullException.ThrowIfNull(input.BodyReferenceIndices);
        ArgumentNullException.ThrowIfNull(input.MetadataReferenceIndices);
        if (input.Documents.Count is < 1 or > MaxDocuments || input.ReferenceImages.Count > MaxReferences ||
            input.Roots.Count is < 1 or > MaxRoots)
            throw new ArgumentException("HardwareBridge source/reference/root count budget exceeded.");

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var documents = new List<BridgeDocumentDigestV1>(input.Documents.Count);
        long totalBytes = 0;
        foreach (BridgeSourceTextV1 document in input.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document is null || !SafeVirtualPath(document.VirtualPath) ||
                !seenPaths.Add(document.VirtualPath) || document.Text is null)
                throw new ArgumentException("Empty, duplicate, non-canonical or traversing virtual path.");
            int size = Encoding.UTF8.GetByteCount(document.Text);
            if (size > MaxDocumentUtf8Bytes || (totalBytes += size) > MaxTotalBytes)
                throw new ArgumentException("Source byte budget exceeded.");
            // Strings are immutable; hash only the entry snapshot, never a later mutable list.
            documents.Add(new(document.VirtualPath, Hash(Encoding.UTF8.GetBytes(document.Text)), size));
        }

        int[] body = CopyIndices(input.BodyReferenceIndices, input.ReferenceImages.Count, "body");
        int[] metadata = CopyIndices(input.MetadataReferenceIndices, input.ReferenceImages.Count, "metadata");
        if (body.Intersect(metadata).Any())
            throw new ArgumentException("Body and metadata-only reference selections must be disjoint.");
        var seenImages = new HashSet<string>(StringComparer.Ordinal);
        var references = new List<BridgeReferenceDigestV1>(input.ReferenceImages.Count);
        for (int i = 0; i < input.ReferenceImages.Count; ++i)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlyMemory<byte> image = input.ReferenceImages[i];
            if (image.IsEmpty || image.Length > MaxReferenceBytes ||
                (totalBytes += image.Length) > MaxTotalBytes)
                throw new ArgumentException("Empty or oversized reference image.");
            // Copy before hashing; never retain caller-owned reference buffers.
            byte[] bytes = image.ToArray();
            string hash = Hash(bytes);
            if (!seenImages.Add(hash))
                throw new ArgumentException("Ambiguous duplicate reference image, including across body/metadata roles.");
            string role = body.Contains(i) ? "owned-body" : metadata.Contains(i) ? "metadata-only" : "unselected";
            references.Add(new(i, hash, bytes.Length, role));
        }

        var roots = new List<BridgeRootSelectorV1>(input.Roots.Count);
        var rootKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (BridgeRootSelectorV1 root in input.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (root is null) throw new ArgumentException("Null root.");
            RequireIdentity(root.TypeName, "root type");
            RequireIdentity(root.MethodName, "root method");
            if (root.MetadataToken is < 0 || root.TypeArguments is null || root.MethodArguments is null ||
                root.TypeArguments.Count > 8 || root.MethodArguments.Count > 8)
                throw new ArgumentException("Invalid root token or generic-argument budget.");
            string[] types = root.TypeArguments.ToArray(), methods = root.MethodArguments.ToArray();
            if (types.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 256 || v.Any(char.IsControl)) ||
                methods.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 256 || v.Any(char.IsControl)))
                throw new ArgumentException("Empty exact generic type identity.");
            BridgeRootSelectorV1 copy = new(root.TypeName, root.MethodName, root.MetadataToken,
                Array.AsReadOnly(types), Array.AsReadOnly(methods));
            if (!rootKeys.Add(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(copy)))))
                throw new ArgumentException("Duplicate exact root selector.");
            roots.Add(copy);
        }

        var sourceRows = Array.AsReadOnly(documents.ToArray());
        var refRows = Array.AsReadOnly(references.ToArray());
        var rootRows = Array.AsReadOnly(roots.ToArray());
        var bodyRows = Array.AsReadOnly(body);
        var metadataRows = Array.AsReadOnly(metadata);
        var preimage = new {
            Schema = SchemaId, input.AssemblyName, input.LanguageHostMode, AdmissionPolicy = admissionPolicy,
            CompilerVersion = compilerVersion, RoslynVersion = roslynVersion,
            Documents = sourceRows, References = refRows, Roots = rootRows,
            BodyReferenceIndices = bodyRows, MetadataReferenceIndices = metadataRows
        };
        cancellationToken.ThrowIfCancellationRequested();
        return new(SchemaId, input.AssemblyName, input.LanguageHostMode, admissionPolicy,
            compilerVersion, roslynVersion, sourceRows, refRows, rootRows,
            bodyRows, metadataRows, Hash(JsonSerializer.SerializeToUtf8Bytes(preimage)));
    }

    private static int[] CopyIndices(IReadOnlyList<int> indices, int count, string name)
    {
        if (indices.Count > MaxReferences) throw new ArgumentException("Too many " + name + " selections.");
        int[] values = indices.ToArray();
        if (values.Any(value => value < 0 || value >= count) || values.Distinct().Count() != values.Length)
            throw new ArgumentException("Invalid or duplicate " + name + " reference index.");
        return values;
    }

    private static bool SafeVirtualPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value[0] == '/' ||
            value.Contains('\\') || value.Contains(':') || value.Any(char.IsControl)) return false;
        string[] parts = value.Split('/');
        return parts.All(p => p.Length > 0 && p != "." && p != ".." &&
            !p.StartsWith(' ') && !p.EndsWith(' '));
    }

    private static void RequireIdentity(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("Missing or invalid identity: " + name);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
