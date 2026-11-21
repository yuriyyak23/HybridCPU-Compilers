// Opt-in HB-INT-03 companion receipt. Does not change sidecar/v1 or provenance-index/v2.
// The supplied execution event is an assertion by the capture owner, NOT proof that Csc ran.
// The Core consumer must independently bind the executed task-time observation and raw bytes.
using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
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
    InvalidPin, Cancelled, StaleMethodGraph
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
            facts.SourceMaps.Any(row => row is null) ||
            facts.Methods.Count is < 1 or > 256 ||
            facts.Methods.Any(method => method is null) ||
            facts.RootIdentities.Count != snapshot.Roots.Count)
            throw Refuse(BridgeCscLinkRefusal.UnsupportedFacts,
                "Only exact primary-module Candidate software facts are eligible.");
        if (facts.PeSha256 != Hash(e.PeImage) || facts.PdbSha256 != Hash(e.PdbImage))
            throw Refuse(BridgeCscLinkRefusal.WrongPePdb,
                "Companion PE/PDB hashes do not identify observed output bytes.");

        if (string.IsNullOrWhiteSpace(e.SdkVersion) || e.SdkVersion.Length > 128 ||
            e.SdkVersion.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(e.CompilerPath) ||
            !ExactCscPath(e.CompilerPath) ||
            !ExactCscPath(e.OutputPePath) ||
            !ExactCscPath(e.OutputPdbPath) ||
            string.Equals(e.CompilerPath, e.OutputPePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.CompilerPath, e.OutputPdbPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.OutputPePath, e.OutputPdbPath, StringComparison.OrdinalIgnoreCase) ||
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
            args.Count(a => a == "/out:" + e.OutputPePath) != 1 ||
            args.Count(a => a == "/pdb:" + e.OutputPdbPath) != 1 ||
            args.Count(a => a == "/debug:portable") != 1 ||
            args.Count(a => a == "/deterministic+") != 1 ||
            args.Skip(1).Any(a => a.StartsWith('@') ||
                a.StartsWith("/debug:", StringComparison.OrdinalIgnoreCase) &&
                    a != "/debug:portable" ||
                a.StartsWith("/out:", StringComparison.OrdinalIgnoreCase) &&
                    a != "/out:" + e.OutputPePath ||
                a.StartsWith("/pdb:", StringComparison.OrdinalIgnoreCase) &&
                    a != "/pdb:" + e.OutputPdbPath))
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
        var seenPhysical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (e.Sources.Any(item => item is null) ||
            e.References.Any(item => item is null))
            throw Refuse(BridgeCscLinkRefusal.StaleSource,
                "Null executed Csc source or reference inventory entry.");
        foreach (BridgeCscCapturedInputV1 item in e.Sources.OrderBy(s => s.VirtualPath, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (item is null || !BridgeSegmentIndexV2.SafeRelativePath(item.VirtualPath) ||
                !ExactCscPath(item.CscPath) || !seen.Add(item.VirtualPath) ||
                !seenPhysical.Add(item.CscPath) ||
                item.Role != "source" || item.Bytes is null ||
                item.Bytes.Length > BridgeSourceSnapshotV1.MaxDocumentUtf8Bytes ||
                args.Count(a => a == item.CscPath) != 1)
                throw Refuse(BridgeCscLinkRefusal.StaleSource, "Source not bound to executed argv.");
            BridgeDocumentDigestV1? doc = snapshot.Documents.FirstOrDefault(d => d.VirtualPath == item.VirtualPath);
            if (doc is null || doc.Sha256 != Hash(item.Bytes) || doc.Utf8Bytes != item.Bytes.Length)
                throw Refuse(BridgeCscLinkRefusal.StaleSource, "Changed source document bytes.");
            try { _ = Utf8.GetString(item.Bytes); }
            catch (DecoderFallbackException)
            {
                throw Refuse(BridgeCscLinkRefusal.StaleSource, "Non-UTF8 executed Csc source.");
            }
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
                args.Count(a => a == "/reference:" + item.CscPath) != 1 ||
                !seenPhysical.Add(item.CscPath))
                throw Refuse(BridgeCscLinkRefusal.StaleReference,
                    "Reference image/role is not an exact Csc input.");
            Pin("reference", "captured-executed-csc-input", item.Role, item.VirtualPath, item.Bytes);
        }
        if (args.Count(a => a.StartsWith("/reference:", StringComparison.Ordinal)) !=
                e.References.Count ||
            args.Any(a => a.StartsWith("/reference:", StringComparison.Ordinal) &&
                !e.References.Any(r => "/reference:" + r.CscPath == a)) ||
            args.Any(a => a.StartsWith('@')))
            throw Refuse(BridgeCscLinkRefusal.WrongArguments,
                "Uncaptured references or response-file indirection.");
        if (seenPhysical.Contains(e.CompilerPath) ||
            seenPhysical.Contains(e.OutputPePath) ||
            seenPhysical.Contains(e.OutputPdbPath))
            throw Refuse(BridgeCscLinkRefusal.WrongArguments,
                "Compiler, input, and output paths must never alias.");
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
            // The PDB content ID is 20 bytes: CodeView GUID AND stamp, not only GUID.
            // MVID is read from the actual PE, never from a caller-supplied DTO.
            Guid mvid = md.GetGuid(md.GetModuleDefinition().Mvid);
            var codeviews = pe.ReadDebugDirectory().Where(d => d.Type == DebugDirectoryEntryType.CodeView).ToArray();
            if (mvid == Guid.Empty || codeviews.Length != 1 ||
                !codeviews[0].IsPortableCodeView || pdb.DebugMetadataHeader is not { } pdbHeader ||
                pdbHeader.Id.Length != 20)
                throw Refuse(BridgeCscLinkRefusal.WrongPePdb,
                    "Missing MVID or a unique portable CodeView/PDB identity.");
            var cv = pe.ReadCodeViewDebugDirectoryData(codeviews[0]);
            Span<byte> expectedPdbId = stackalloc byte[20];
            cv.Guid.TryWriteBytes(expectedPdbId);
            BinaryPrimitives.WriteUInt32LittleEndian(expectedPdbId[16..], codeviews[0].Stamp);
            if (!pdbHeader.Id.SequenceEqual(expectedPdbId.ToArray()) || cv.Age != 1)
                throw Refuse(BridgeCscLinkRefusal.WrongPePdb,
                    "Full 20-byte portable PDB ID, CodeView age, or stamp differs.");

            // Duplicate PDB paths, forged hashes and uncaptured #line documents are
            // refused even when no Companion map happens to mention them.
            if (pdb.Documents.Count is < 1 or > 128)
                throw Refuse(BridgeCscLinkRefusal.Oversized, "PDB document budget exceeded.");
            var pdbPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (DocumentHandle documentHandle in pdb.Documents)
            {
                ct.ThrowIfCancellationRequested();
                Document document = pdb.GetDocument(documentHandle);
                string pdbPath = pdb.GetString(document.Name);
                if (!pdbPaths.Add(pdbPath) ||
                    pdb.GetGuid(document.HashAlgorithm) != Sha256DocumentAlgorithm ||
                    !e.Sources.Any(source => source.CscPath == pdbPath &&
                        pdb.GetBlobBytes(document.Hash).AsSpan().SequenceEqual(
                            SHA256.HashData(source.Bytes))))
                    throw Refuse(BridgeCscLinkRefusal.WrongSourceMap,
                        "PDB document is duplicate, foreign, or checksum-forged.");
            }

            var usedTokens = new HashSet<int>();
            var ilCalls = new Dictionary<string, List<(int Offset, int Token)>>();
            var stableIds = new HashSet<string>(StringComparer.Ordinal);
            var links = new List<BridgeCscMethodLinkV1>();
            foreach (BridgeMethodFactV1 fact in facts.Methods.OrderBy(m => m.StableIdentity, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (fact is null || !IsSha(fact.IdentityDigest) ||
                    fact.ImportStatus != "Success" || fact.PeSha256 != facts.PeSha256 ||
                    fact.AssemblyIdentity != snapshot.AssemblyName ||
                    fact.ModuleName != md.GetString(md.GetModuleDefinition().Name) ||
                    fact.DirectCallees is null || fact.DirectCallees.Count > 256 ||
                    fact.DirectCallees.Distinct(StringComparer.Ordinal).Count() != fact.DirectCallees.Count ||
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
                if (type.GetGenericParameters().Count != 0 ||
                    method.GetGenericParameters().Count != 0)
                    throw Refuse(BridgeCscLinkRefusal.GenericInstantiation,
                        "Generic declaring types/method bodies are not supported by v1.");
                if (PrimitiveSignature(md, method) != fact.CanonicalSignature)
                    throw Refuse(BridgeCscLinkRefusal.WrongMethodToken,
                        "Companion canonical signature differs from actual PE MethodDef.");
                if (method.RelativeVirtualAddress == 0)
                    throw Refuse(BridgeCscLinkRefusal.WrongMethodToken, "Method has no IL body.");
                byte[]? il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                if (il is null || il.Length == 0)
                    throw Refuse(BridgeCscLinkRefusal.WrongMethodToken, "Empty IL body.");
                if (il.Length > 1_048_576)
                    throw Refuse(BridgeCscLinkRefusal.Oversized, "Method IL budget exceeded.");
                ilCalls.Add(fact.StableIdentity, ParseIlCalls(il));

                BridgeMapRowV1[] rows = facts.SourceMaps.Where(r =>
                    r.MethodIdentity == fact.StableIdentity).ToArray();
                if (rows.Length is < 1 or > 2048 ||
                    rows.Select(row => (row.IlOffset, row.IrIndex, row.Document))
                        .Distinct().Count() != rows.Length)
                    throw Refuse(BridgeCscLinkRefusal.WrongSourceMap,
                        "Primary method needs a bounded PDB source map.");
                MethodDebugInformation debug = pdb.GetMethodDebugInformation(
                    MetadataTokens.MethodDebugInformationHandle(MetadataTokens.GetRowNumber(handle)));
                var points = debug.GetSequencePoints().Where(p => !p.IsHidden).ToArray();
                if (points.Length == 0)
                    throw Refuse(BridgeCscLinkRefusal.WrongSourceMap, "No visible PDB sequence points.");
                if (points.Length > 2048)
                    throw Refuse(BridgeCscLinkRefusal.Oversized, "Method PDB point budget exceeded.");
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
            // Require exact intra-module call edges; neither a stale graph DTO nor
            // a misleading DirectCallees array can substitute for the actual IL.
            if (facts.Edges is null || facts.Edges.Count > 4096 ||
                facts.Edges.Any(edge => edge is null ||
                    string.IsNullOrWhiteSpace(edge.StableId)) ||
                facts.Edges.Select(edge => edge.StableId)
                    .Distinct(StringComparer.Ordinal).Count() != facts.Edges.Count)
                throw Refuse(BridgeCscLinkRefusal.StaleMethodGraph, "Invalid bounded edge graph.");
            var factsByToken = facts.Methods.ToDictionary(f => f.MetadataToken);
            foreach (BridgeMethodFactV1 fact in facts.Methods)
            {
                ct.ThrowIfCancellationRequested();
                var expected = new List<(int Offset, int Token, string Callee)>();
                foreach (var call in ilCalls[fact.StableIdentity])
                {
                    // v1 has no independently resolved body for MemberRef or
                    // MethodSpec: refuse instead of silently dropping the call.
                    if ((call.Token >> 24) != 0x06)
                        throw Refuse(BridgeCscLinkRefusal.StaleMethodGraph,
                            "Foreign or generic IL call needs separate Core qualification.");
                    if (!factsByToken.TryGetValue(call.Token, out BridgeMethodFactV1? callee))
                        throw Refuse(BridgeCscLinkRefusal.StaleMethodGraph,
                            "Observed local MethodDef call has no owned-body fact.");
                    expected.Add((call.Offset, call.Token, callee.StableIdentity));
                }
                BridgeEdgeFactV1[] claimed = facts.Edges.Where(edge =>
                    edge.Caller == fact.StableIdentity).ToArray();
                if (claimed.Length != expected.Count ||
                    claimed.Select(edge => (edge.IlOffset, edge.MetadataToken, edge.Callee))
                        .Distinct().Count() != claimed.Length ||
                    claimed.Any(edge => !expected.Any(x => x.Offset == edge.IlOffset &&
                        x.Token == edge.MetadataToken && x.Callee == edge.Callee)) ||
                    !fact.DirectCallees.OrderBy(x => x, StringComparer.Ordinal)
                        .SequenceEqual(expected.Select(x => x.Callee)
                            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)))
                    throw Refuse(BridgeCscLinkRefusal.StaleMethodGraph,
                        "Companion call edges differ from actual local IL calls.");
            }
            if (facts.Edges.Any(edge => !stableIds.Contains(edge.Caller) ||
                !stableIds.Contains(edge.Callee)))
                throw Refuse(BridgeCscLinkRefusal.StaleMethodGraph, "Foreign graph node.");
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

    private static readonly IReadOnlyDictionary<int, OperandType> IlOperands =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .GroupBy(op => (int)unchecked((ushort)op.Value))
            .ToDictionary(group => group.Key, group => group.First().OperandType);

    internal static List<(int Offset, int Token)> ParseIlCalls(byte[] il)
    {
        var result = new List<(int Offset, int Token)>();
        int offset = 0;
        while (offset < il.Length)
        {
            int instructionOffset = offset;
            int op = il[offset++];
            if (op == 0xFE)
            {
                if (offset == il.Length)
                    throw Refuse(BridgeCscLinkRefusal.StaleMethodGraph, "Truncated IL opcode.");
                op = 0xFE00 | il[offset++];
            }
            if (!IlOperands.TryGetValue(op, out OperandType operand))
                throw Refuse(BridgeCscLinkRefusal.StaleMethodGraph, "Unknown IL opcode.");
            int bytes = operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineI or OperandType.ShortInlineBrTarget or
                    OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI or OperandType.InlineBrTarget or
                    OperandType.InlineField or OperandType.InlineMethod or
                    OperandType.InlineSig or OperandType.InlineString or
                    OperandType.InlineTok or OperandType.InlineType or
                    OperandType.ShortInlineR => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => ReadSwitchBytes(il, offset),
                _ => throw Refuse(BridgeCscLinkRefusal.StaleMethodGraph, "Unsupported IL operand.")
            };
            if (bytes > il.Length - offset)
                throw Refuse(BridgeCscLinkRefusal.StaleMethodGraph, "Truncated IL operand.");
            // The v1 graph models only direct call/callvirt/newobj MethodDef edges.
            // Indirect transfers and method-pointer construction are NOT a proof
            // of a closed owned-body graph. Refuse them even if no graph edge
            // was asserted by Companion; never silently drop an executable edge.
            if (op is 0x27 or 0x29 or 0xFE06 or 0xFE07)
                throw Refuse(BridgeCscLinkRefusal.StaleMethodGraph,
                    "jmp/calli/ldftn/ldvirtftn require a separately qualified call graph.");
            // ldtoken can manufacture an executable MethodDef/MemberRef/MethodSpec
            // handle without an immediate call opcode. Type/field tokens alone
            // are not method edges and remain outside this narrow refusal.
            if (op == 0xD0)
            {
                int tokenTable = (int)((uint)BinaryPrimitives.ReadInt32LittleEndian(
                    il.AsSpan(offset, 4)) >> 24);
                if (tokenTable is 0x06 or 0x0A or 0x2B)
                    throw Refuse(BridgeCscLinkRefusal.StaleMethodGraph,
                        "Unresolved ldtoken method handle has no v1 owned-body edge.");
            }
            if (op is 0x28 or 0x6F or 0x73)
                result.Add((instructionOffset, BinaryPrimitives.ReadInt32LittleEndian(
                    il.AsSpan(offset, 4))));
            offset += bytes;
        }
        return result;
    }

    private static int ReadSwitchBytes(byte[] il, int offset)
    {
        if (il.Length - offset < 4)
            throw Refuse(BridgeCscLinkRefusal.StaleMethodGraph, "Truncated IL switch.");
        int cases = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));
        if (cases < 0 || (long)cases * 4 + 4 > il.Length - offset)
            throw Refuse(BridgeCscLinkRefusal.StaleMethodGraph, "Oversized IL switch.");
        return checked(4 + cases * 4);
    }

    // v1 deliberately admits only non-generic primitive signatures.
    // Unsupported signature shapes are not guessed from Companion's string.
    private static string PrimitiveSignature(MetadataReader md, MethodDefinition method)
    {
        BlobReader reader = md.GetBlobReader(method.Signature);
        SignatureHeader header = reader.ReadSignatureHeader();
        if (header.Kind != SignatureKind.Method || header.IsGeneric)
            throw Refuse(BridgeCscLinkRefusal.GenericInstantiation,
                "Unsupported method signature header.");
        int count = reader.ReadCompressedInteger();
        if (count is < 0 or > 32)
            throw Refuse(BridgeCscLinkRefusal.UnsupportedFacts,
                "Method parameter budget exceeded.");
        string returns = PrimitiveType(ref reader);
        var parameters = new string[count];
        for (int i = 0; i < count; i++)
            parameters[i] = PrimitiveType(ref reader);
        if (reader.RemainingBytes != 0)
            throw Refuse(BridgeCscLinkRefusal.UnsupportedFacts,
                "Noncanonical primitive signature bytes.");
        return returns + "(" + string.Join(",", parameters) + ")";
    }

    private static string PrimitiveType(ref BlobReader reader) =>
        reader.ReadSignatureTypeCode() switch
        {
            SignatureTypeCode.Void => "System.Void",
            SignatureTypeCode.Boolean => "System.Boolean",
            SignatureTypeCode.Char => "System.Char",
            SignatureTypeCode.SByte => "System.SByte",
            SignatureTypeCode.Byte => "System.Byte",
            SignatureTypeCode.Int16 => "System.Int16",
            SignatureTypeCode.UInt16 => "System.UInt16",
            SignatureTypeCode.Int32 => "System.Int32",
            SignatureTypeCode.UInt32 => "System.UInt32",
            SignatureTypeCode.Int64 => "System.Int64",
            SignatureTypeCode.UInt64 => "System.UInt64",
            SignatureTypeCode.Single => "System.Single",
            SignatureTypeCode.Double => "System.Double",
            SignatureTypeCode.String => "System.String",
            SignatureTypeCode.Object => "System.Object",
            SignatureTypeCode.IntPtr => "System.IntPtr",
            SignatureTypeCode.UIntPtr => "System.UIntPtr",
            _ => throw Refuse(BridgeCscLinkRefusal.UnsupportedFacts,
                "Complex signatures require Core-owned independent verifier.")
        };

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
