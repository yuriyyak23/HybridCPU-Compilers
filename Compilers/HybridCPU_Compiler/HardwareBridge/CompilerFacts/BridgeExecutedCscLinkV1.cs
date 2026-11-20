// Opt-in HB-INT-03 companion receipt. Does not change sidecar/v1 or provenance-index/v2.
// The supplied execution event is an assertion by the capture owner, NOT proof that Csc ran.
// The Core consumer must independently bind the executed task-time observation and raw bytes.
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;

namespace HybridCPU.Compiler.HardwareBridge.CompilerFacts;

public enum BridgeCscLinkRefusal
{
    MissingEvent, FailedCompilation, MissingPePdb, WrongArguments, StaleSource,
    StaleReference, WrongAssembly, WrongPePdb, WrongMethodToken,
    DuplicateMethod, GenericInstantiation, WrongSourceMap, UnsupportedFacts,
    Oversized, MalformedJson, DuplicateProperty, UnsupportedSchema, NonCanonical,
    InvalidPin, Cancelled
}

public sealed class BridgeCscLinkException(BridgeCscLinkRefusal code, string message)
    : Exception(message)
{
    public BridgeCscLinkRefusal Code { get; } = code;
}

// Paths are the actual argv paths captured by an isolated Csc executor; virtual paths
// are the source snapshot identities. Bytes must be read from that execution's inputs.
public sealed record BridgeCscCapturedInputV1(
    string CscPath, string VirtualPath, string Role, byte[] Bytes);

// An external trusted observer must supply this from a completed *executed* Csc process.
// A forged event is not authenticated by this in-process codec.
public sealed record BridgeCscExecutedEventV1(
    string SdkVersion, string CompilerPath, byte[] CompilerImage, byte[] ArgvJsonUtf8,
    int ExitCode, string OutputPePath, string OutputPdbPath,
    IReadOnlyList<BridgeCscCapturedInputV1> Sources,
    IReadOnlyList<BridgeCscCapturedInputV1> References,
    byte[] PeImage, byte[] PdbImage);

public sealed record BridgeCscBytePinV1(
    string Role, string Origin, string ConsumerRole, string Path, string Sha256, long Bytes);

public sealed record BridgeCscMethodLinkV1(
    string CompanionStableIdentity, string DeclaringType, string MethodName,
    int MetadataToken, string IlSha256, int IlBytes, string SourceMapAssertionSha256,
    int IndependentlyCheckedPdbRows);

public sealed record BridgeCscLinkPayloadV1(
    string Schema, string SoftwareStatus, string SourceSnapshotDigest,
    string CompanionSourceInputDigest, string CompanionFactsSha256,
    string SdkVersion, string CscInvocationDigest, string CompilerOutputPairDigest,
    IReadOnlyList<BridgeCscBytePinV1> Pins,
    IReadOnlyList<BridgeCscMethodLinkV1> Methods);

public sealed record BridgeCscLinkEnvelopeV1(
    string Schema, string PayloadSha256, BridgeCscLinkPayloadV1 Payload);

/// <summary>
/// Byte-level opt-in linkage for primary-module non-generic Companion Candidate methods.
/// PE metadata/token/IL and portable PDB sequence points are re-read independently;
/// Companion stable identity/signature/source-map IR index remain *assertions*, not proof.
/// Does NOT attest that the caller really observed Csc, authenticate a Git worktree,
/// authorize RTL, or produce a Core qualification verdict.
/// </summary>
public static class BridgeExecutedCscLinkV1
{
    public const string SchemaId = "hybridcpu.hardwarebridge.executed-csc-link/v1";
    public const int MaxEnvelopeBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Guid Sha256DocumentAlgorithm =
        new("8829d00f-11b8-4213-878b-770e8597ac16");

    private static BridgeCscLinkException Refuse(BridgeCscLinkRefusal code, string message) =>
        new(code, message);

    public static byte[] Write(BridgeSourceSnapshotV1 snapshot, BridgeCompilerFactsV1 facts,
        BridgeCscExecutedEventV1 executed, CancellationToken cancellationToken = default)
    {
        BridgeCscLinkPayloadV1 payload = Verify(snapshot, facts, executed, cancellationToken);
        byte[] payloadUtf8 = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[] envelope = JsonSerializer.SerializeToUtf8Bytes(
            new BridgeCscLinkEnvelopeV1(SchemaId, Hash(payloadUtf8), payload));
        if (envelope.Length > MaxEnvelopeBytes)
            throw Refuse(BridgeCscLinkRefusal.Oversized, "Link envelope exceeds 4 MiB.");
        return envelope;
    }

    public static BridgeCscLinkPayloadV1 ReadAndValidate(ReadOnlyMemory<byte> envelope,
        BridgeSourceSnapshotV1 currentSnapshot, BridgeCompilerFactsV1 currentFacts,
        BridgeCscExecutedEventV1 actualEvent, string expectedSdk,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (envelope.IsEmpty || envelope.Length > MaxEnvelopeBytes)
            throw Refuse(BridgeCscLinkRefusal.Oversized, "Missing or oversized linkage.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(envelope, new JsonDocumentOptions
            {
                MaxDepth = 24, CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            CheckDuplicates(document.RootElement, cancellationToken);
            if (!document.RootElement.TryGetProperty("Schema", out JsonElement schema) ||
                schema.GetString() != SchemaId)
                throw Refuse(BridgeCscLinkRefusal.UnsupportedSchema, "Unknown link schema.");
            BridgeCscLinkEnvelopeV1? parsed =
                JsonSerializer.Deserialize<BridgeCscLinkEnvelopeV1>(envelope.Span);
            if (parsed?.Payload is null || parsed.Payload.Schema != SchemaId)
                throw Refuse(BridgeCscLinkRefusal.UnsupportedSchema, "Unknown payload schema.");
            if (!envelope.Span.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(parsed)))
                throw Refuse(BridgeCscLinkRefusal.NonCanonical, "Unknown, reordered or noncanonical JSON.");
            if (parsed.PayloadSha256 != Hash(JsonSerializer.SerializeToUtf8Bytes(parsed.Payload)))
                throw Refuse(BridgeCscLinkRefusal.InvalidPin, "Link payload checksum differs.");
            if (actualEvent is null || actualEvent.SdkVersion != expectedSdk)
                throw Refuse(BridgeCscLinkRefusal.InvalidPin, "Executed SDK pin differs.");
            if (!envelope.Span.SequenceEqual(Write(currentSnapshot, currentFacts,
                actualEvent, cancellationToken)))
                throw Refuse(BridgeCscLinkRefusal.InvalidPin,
                    "Live source, invocation, PE, PDB, method, or fact bytes differ.");
            return parsed.Payload;
        }
        catch (BridgeCscLinkException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (JsonException ex)
        {
            throw Refuse(BridgeCscLinkRefusal.MalformedJson, ex.Message);
        }
    }

    private static BridgeCscLinkPayloadV1 Verify(BridgeSourceSnapshotV1 snapshot,
        BridgeCompilerFactsV1 facts, BridgeCscExecutedEventV1 e, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (e is null || e.Sources is null || e.References is null ||
            e.CompilerImage is null || e.ArgvJsonUtf8 is null)
            throw Refuse(BridgeCscLinkRefusal.MissingEvent, "No complete observed Csc event.");
        if (e.ExitCode != 0)
            throw Refuse(BridgeCscLinkRefusal.FailedCompilation, "Csc exit is nonzero.");
        if (e.PeImage is null || e.PdbImage is null || e.PeImage.Length == 0 ||
            e.PdbImage.Length == 0)
            throw Refuse(BridgeCscLinkRefusal.MissingPePdb, "Executed Csc did not supply PE + PDB.");
        if (e.PeImage.Length > 33_554_432 || e.PdbImage.Length > 33_554_432 ||
            e.CompilerImage.Length is < 1 or > 33_554_432 ||
            e.ArgvJsonUtf8.Length is < 1 or > 1_048_576 ||
            e.Sources.Count is < 1 or > 128 || e.References.Count > 128)
            throw Refuse(BridgeCscLinkRefusal.Oversized, "Compiler / file byte budget.");
        if (snapshot is null || !snapshot.HasConsistentDigest() ||
            facts is null || facts.Schema != BridgeCompilerFactsV1.SchemaId ||
            facts.Status != "Candidate" || facts.ImportStatus != "Success" ||
            facts.SnapshotDigest != snapshot.SnapshotDigest ||
            !IsSha(facts.SourceInputDigest) || !IsSha(facts.PeSha256) ||
            !IsSha(facts.PdbSha256) || facts.Methods is null || facts.RootIdentities is null ||
            facts.SourceMaps is null || facts.SourceMaps.Count > 131072 ||
            facts.Methods.Count is < 1 or > 256 ||
            facts.RootIdentities.Count != snapshot.Roots.Count)
            throw Refuse(BridgeCscLinkRefusal.UnsupportedFacts,
                "Only exact primary-module Candidate software facts are eligible.");
        if (facts.PeSha256 != Hash(e.PeImage) || facts.PdbSha256 != Hash(e.PdbImage))
            throw Refuse(BridgeCscLinkRefusal.WrongPePdb,
                "Companion PE/PDB hashes do not identify observed output bytes.");

        if (string.IsNullOrWhiteSpace(e.SdkVersion) || e.SdkVersion.Length > 128 ||
            e.SdkVersion.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(e.CompilerPath) ||
            !Path.IsPathFullyQualified(e.CompilerPath) ||
            !Path.IsPathFullyQualified(e.OutputPePath) ||
            !Path.IsPathFullyQualified(e.OutputPdbPath) ||
            e.CompilerPath.Length > 1024 || e.OutputPePath.Length > 1024 ||
            e.OutputPdbPath.Length > 1024)
            throw Refuse(BridgeCscLinkRefusal.WrongArguments, "Compiler or output path is not exact.");

        string[] args;
        try
        {
            args = JsonSerializer.Deserialize<string[]>(Utf8.GetString(e.ArgvJsonUtf8)) ??
                throw new JsonException("Null argv.");
            if (args.Length is < 4 or > 4096 || args.Any(a => a is null || a.Length > 4096) ||
                !e.ArgvJsonUtf8.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(args)))
                throw new JsonException("Noncanonical argv.");
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            throw Refuse(BridgeCscLinkRefusal.WrongArguments, "Invalid ordered argv: " + ex.Message);
        }
        if (args[0] != e.CompilerPath ||
            !args.Contains("/out:" + e.OutputPePath, StringComparer.Ordinal) ||
            !args.Contains("/pdb:" + e.OutputPdbPath, StringComparer.Ordinal) ||
            !args.Contains("/debug:portable", StringComparer.Ordinal) ||
            !args.Contains("/deterministic+", StringComparer.Ordinal))
            throw Refuse(BridgeCscLinkRefusal.WrongArguments,
                "Executed argv does not select the reported PE, PDB and portable debug mode.");

        var pins = new List<BridgeCscBytePinV1>();
        void Pin(string role, string origin, string consumer, string path, byte[] bytes)
        {
            pins.Add(new(role, origin, consumer, path, Hash(bytes), bytes.LongLength));
        }
        Pin("compiler-image", "executed-event-assertion", "CscIdentity", e.CompilerPath, e.CompilerImage);
        Pin("ordered-argv-json", "executed-event-assertion", "CscInvocation",
            e.CompilerPath, e.ArgvJsonUtf8);

        if (snapshot.Documents.Count != e.Sources.Count ||
            snapshot.References.Count != e.References.Count)
            throw Refuse(BridgeCscLinkRefusal.StaleSource, "Csc input inventory count differs.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BridgeCscCapturedInputV1 item in e.Sources.OrderBy(s => s.VirtualPath, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (item is null || !BridgeSegmentIndexV2.SafeRelativePath(item.VirtualPath) ||
                !ExactCscPath(item.CscPath) || !seen.Add(item.VirtualPath) ||
                item.Role != "source" || item.Bytes is null ||
                item.Bytes.Length > BridgeSourceSnapshotV1.MaxDocumentUtf8Bytes ||
                !args.Contains(item.CscPath, StringComparer.Ordinal))
                throw Refuse(BridgeCscLinkRefusal.StaleSource, "Source not bound to executed argv.");
            BridgeDocumentDigestV1? doc = snapshot.Documents.FirstOrDefault(d => d.VirtualPath == item.VirtualPath);
            if (doc is null || doc.Sha256 != Hash(item.Bytes) || doc.Utf8Bytes != item.Bytes.Length)
                throw Refuse(BridgeCscLinkRefusal.StaleSource, "Changed source document bytes.");
            Pin("source", "captured-executed-csc-input", "SourceAdmission", item.VirtualPath, item.Bytes);
        }
        for (int i = 0; i < e.References.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            BridgeCscCapturedInputV1 item = e.References[i];
            BridgeReferenceDigestV1 original = snapshot.References[i];
            if (item is null || !ExactCscPath(item.CscPath) || item.Bytes is null ||
                item.Bytes.Length is < 1 or > 33_554_432 ||
                item.VirtualPath != "refs/" + i.ToString("D4",
                    System.Globalization.CultureInfo.InvariantCulture) + ".bin" ||
                item.Role != original.Role || original.Sha256 != Hash(item.Bytes) ||
                original.Bytes != item.Bytes.Length ||
                !args.Contains("/reference:" + item.CscPath, StringComparer.Ordinal))
                throw Refuse(BridgeCscLinkRefusal.StaleReference,
                    "Reference image/role is not an exact Csc input.");
            Pin("reference", "captured-executed-csc-input", item.Role, item.VirtualPath, item.Bytes);
        }
        Pin("output-pe", "executed-csc-output-assertion", "PrimaryModule", e.OutputPePath, e.PeImage);
        Pin("output-portable-pdb", "executed-csc-output-assertion", "SourceMapCheck",
            e.OutputPdbPath, e.PdbImage);

        try
        {
            using var pe = new PEReader(new MemoryStream(e.PeImage, writable: false));
            if (!pe.HasMetadata)
                throw Refuse(BridgeCscLinkRefusal.WrongPePdb, "PE metadata missing.");
            MetadataReader md = pe.GetMetadataReader();
            if (!md.IsAssembly || md.GetString(md.GetAssemblyDefinition().Name) != snapshot.AssemblyName)
                throw Refuse(BridgeCscLinkRefusal.WrongAssembly, "Observed PE assembly is not selected source assembly.");
            using MetadataReaderProvider pdbProvider = MetadataReaderProvider.FromPortablePdbStream(
                new MemoryStream(e.PdbImage, writable: false));
            MetadataReader pdb = pdbProvider.GetMetadataReader();
            var codeviews = pe.ReadDebugDirectory().Where(d => d.Type == DebugDirectoryEntryType.CodeView).ToArray();
            if (codeviews.Length != 1 || pdb.DebugMetadataHeader is not { } pdbHeader ||
                !pdbHeader.Id.Take(16).SequenceEqual(
                    pe.ReadCodeViewDebugDirectoryData(codeviews[0]).Guid.ToByteArray()))
                throw Refuse(BridgeCscLinkRefusal.WrongPePdb,
                    "Portable PDB ID does not match this exact PE's CodeView identity.");

            var usedTokens = new HashSet<int>();
            var stableIds = new HashSet<string>(StringComparer.Ordinal);
            var links = new List<BridgeCscMethodLinkV1>();
            foreach (BridgeMethodFactV1 fact in facts.Methods.OrderBy(m => m.StableIdentity, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (fact is null || !IsSha(fact.IdentityDigest) ||
                    fact.ImportStatus != "Success" || fact.PeSha256 != facts.PeSha256 ||
                    string.IsNullOrWhiteSpace(fact.StableIdentity))
                    throw Refuse(BridgeCscLinkRefusal.UnsupportedFacts,
                        "Foreign, failed or unqualified method fact.");
                if (!stableIds.Add(fact.StableIdentity) || !usedTokens.Add(fact.MetadataToken))
                    throw Refuse(BridgeCscLinkRefusal.DuplicateMethod, "Duplicate method ID or metadata token.");
                if ((fact.MetadataToken >> 24) != 0x06 ||
                    (fact.MetadataToken & 0x00ffffff) is < 1 or > 0x00ffffff)
                    throw Refuse(BridgeCscLinkRefusal.WrongMethodToken, "Not a MethodDef token.");
                MethodDefinitionHandle handle = MetadataTokens.MethodDefinitionHandle(
                    fact.MetadataToken & 0x00ffffff);
                if (!md.MethodDefinitions.Contains(handle))
                    throw Refuse(BridgeCscLinkRefusal.WrongMethodToken, "MethodDef missing in observed PE.");
                MethodDefinition method = md.GetMethodDefinition(handle);
                TypeDefinition type = md.GetTypeDefinition(method.GetDeclaringType());
                string ns = md.GetString(type.Namespace);
                string typeName = (ns.Length == 0 ? "" : ns + ".") + md.GetString(type.Name);
                if (typeName != fact.DeclaringType || md.GetString(method.Name) != fact.MethodName)
                    throw Refuse(BridgeCscLinkRefusal.WrongMethodToken,
                        "PE token does not resolve to the asserted method/type.");
                if (method.RelativeVirtualAddress == 0)
                    throw Refuse(BridgeCscLinkRefusal.WrongMethodToken, "Method has no IL body.");
                byte[]? il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                if (il is null || il.Length == 0)
                    throw Refuse(BridgeCscLinkRefusal.WrongMethodToken, "Empty IL body.");

                BridgeMapRowV1[] rows = facts.SourceMaps.Where(r =>
                    r.MethodIdentity == fact.StableIdentity).ToArray();
                if (rows.Length is < 1 or > 131072)
                    throw Refuse(BridgeCscLinkRefusal.WrongSourceMap,
                        "Primary method needs a bounded PDB source map.");
                MethodDebugInformation debug = pdb.GetMethodDebugInformation(
                    MetadataTokens.MethodDebugInformationHandle(MetadataTokens.GetRowNumber(handle)));
                var points = debug.GetSequencePoints().Where(p => !p.IsHidden).ToArray();
                if (points.Length == 0)
                    throw Refuse(BridgeCscLinkRefusal.WrongSourceMap, "No visible PDB sequence points.");
                foreach (BridgeMapRowV1 row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    if (row is null || row.PeSha256 != facts.PeSha256 ||
                        row.PdbSha256 != facts.PdbSha256 || row.MethodToken != fact.MetadataToken ||
                        row.IlOffset < 0 || row.IlOffset >= il.Length || row.IrIndex < 0 ||
                        row.Document is null || row.Checksum is null ||
                        row.Line is null || row.Column is null)
                        throw Refuse(BridgeCscLinkRefusal.WrongSourceMap,
                            "Unowned, incomplete, or out-of-body source map.");
                    bool found = false;
                    foreach (SequencePoint point in points)
                    {
                        DocumentHandle document = point.Document.IsNil ? debug.Document : point.Document;
                        if (document.IsNil || point.Offset != row.IlOffset ||
                            point.StartLine != row.Line || point.StartColumn != row.Column)
                            continue;
                        Document pdbDocument = pdb.GetDocument(document);
                        string documentName = pdb.GetString(pdbDocument.Name);
                        BridgeCscCapturedInputV1? source = e.Sources.FirstOrDefault(s =>
                            s.CscPath == documentName || s.VirtualPath == documentName);
                        if (source is null || (row.Document != documentName &&
                            row.Document != source.VirtualPath) ||
                            pdb.GetGuid(pdbDocument.HashAlgorithm) != Sha256DocumentAlgorithm ||
                            !pdb.GetBlobBytes(pdbDocument.Hash).AsSpan().SequenceEqual(
                                SHA256.HashData(source.Bytes)) ||
                            row.Checksum != Hash(source.Bytes))
                            continue;
                        found = true;
                        break;
                    }
                    if (!found)
                        throw Refuse(BridgeCscLinkRefusal.WrongSourceMap,
                            "Source map does not match a PDB point and actual source checksum.");
                }
                links.Add(new(fact.StableIdentity, fact.DeclaringType, fact.MethodName,
                    fact.MetadataToken, Hash(il), il.Length,
                    Hash(JsonSerializer.SerializeToUtf8Bytes(rows)), rows.Length));
            }
            if (facts.RootIdentities.Distinct(StringComparer.Ordinal).Count() !=
                    facts.RootIdentities.Count ||
                facts.RootIdentities.Any(id => !stableIds.Contains(id)))
                throw Refuse(BridgeCscLinkRefusal.WrongMethodToken, "Unknown or duplicate root method.");
            foreach (BridgeRootSelectorV1 root in snapshot.Roots)
            {
                if (root.TypeArguments.Count != 0 || root.MethodArguments.Count != 0)
                    throw Refuse(BridgeCscLinkRefusal.GenericInstantiation,
                        "Generic-instantiated roots have no exact v1 method link.");
                if (facts.RootIdentities.Count(id =>
                    facts.Methods.Any(m => m.StableIdentity == id &&
                        m.DeclaringType == root.TypeName && m.MethodName == root.MethodName &&
                        (root.MetadataToken is null || m.MetadataToken == root.MetadataToken))) != 1)
                    throw Refuse(BridgeCscLinkRefusal.WrongMethodToken,
                        "Root selector does not identify one exact PE body.");
            }
            string invocation = Hash(JsonSerializer.SerializeToUtf8Bytes(new {
                e.SdkVersion, CompilerImageSha256 = Hash(e.CompilerImage),
                ArgvSha256 = Hash(e.ArgvJsonUtf8),
                Inputs = pins.Where(p => p.Role is "source" or "reference").ToArray()
            }));
            string outputPair = Hash(JsonSerializer.SerializeToUtf8Bytes(new {
                InvocationSha256 = invocation, PeSha256 = Hash(e.PeImage), PdbSha256 = Hash(e.PdbImage)
            }));
            return new(SchemaId, "Candidate", snapshot.SnapshotDigest,
                facts.SourceInputDigest!, Hash(facts.ToJsonUtf8()),
                e.SdkVersion, invocation, outputPair,
                pins.AsReadOnly(), links.AsReadOnly());
        }
        catch (BridgeCscLinkException) { throw; }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or
            ArgumentException or IndexOutOfRangeException)
        {
            throw Refuse(BridgeCscLinkRefusal.WrongPePdb,
                "Malformed PE/PDB metadata: " + ex.GetType().Name);
        }
    }

    private static bool ExactCscPath(string? path) =>
        path is { Length: > 0 and <= 1024 } && Path.IsPathFullyQualified(path) &&
        path == Path.GetFullPath(path);
    private static bool IsSha(string? value) => BridgeSegmentIndexV2.IsSha(value);
    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static void CheckDuplicates(JsonElement element, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw Refuse(BridgeCscLinkRefusal.DuplicateProperty, "Repeated JSON field.");
                CheckDuplicates(property.Value, ct);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
                CheckDuplicates(child, ct);
        }
    }
}
