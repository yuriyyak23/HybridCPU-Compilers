using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using HybridCPU.Compiler.Core.IR.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed record ManagedSourceMapRowV1(string Symbol, int MethodToken, int IlOffset, int IrIndex,
    string? Document, string? DocumentChecksum, int? Line, int? Column);

/// <summary>Diagnostic sidecar only; source locations carry no lowering/link/runtime authority.</summary>
public sealed record ManagedSourceMapV1(string PeSha256, string? PdbSha256, IReadOnlyList<ManagedSourceMapRowV1> Rows)
{
    public const string SchemaId = "hybridcpu.managed-source-map/v1";
    public string Digest => CompilerContractCanonicalJson.Hash(CompilerContractCanonicalJson.Serialize(new
    { SchemaId, PeSha256, PdbSha256, Rows }));

    public static ManagedSourceMapV1 Create(RestrictedCilImportResultV1 imported, ReadOnlyMemory<byte> peImage,
        ReadOnlyMemory<byte> portablePdb = default)
    {
        ArgumentNullException.ThrowIfNull(imported);
        if (imported.Status != RestrictedCilImportStatusV1.Success || imported.Program is null || imported.Provenance is null)
            throw new ArgumentException("Source mapping requires an admitted method.", nameof(imported));
        string peHash = Convert.ToHexString(SHA256.HashData(peImage.Span)).ToLowerInvariant();
        if (peHash != imported.Provenance.PeSha256) throw new ArgumentException("Source-map PE identity mismatch.", nameof(peImage));
        int token = int.Parse(imported.Provenance.CilMethodToken.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        MetadataReaderProvider? provider = null;
        try
        {
            MetadataReader? pdb = null;
            if (!portablePdb.IsEmpty)
            {
                if (portablePdb.Length > 32 * 1024 * 1024) throw new ArgumentException("Portable PDB exceeds source-map budget.");
                provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(portablePdb.ToArray(), writable: false));
                pdb = provider.GetMetadataReader();
                using PEReader pe = new PEReader(new MemoryStream(peImage.ToArray(), writable: false));
                ImmutableArray<byte> id = pdb.DebugMetadataHeader!.Id;
                bool matches = pe.ReadDebugDirectory().Where(e => e.Type == DebugDirectoryEntryType.CodeView)
                    .Any(e => pe.ReadCodeViewDebugDirectoryData(e).Guid == new Guid(id.AsSpan(0, 16)) &&
                        e.Stamp == System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(id.AsSpan(16, 4)));
                if (!matches) throw new ArgumentException("Portable PDB does not match the PE debug identity.");
            }
            SequencePoint[] points = pdb is null ? [] : pdb.GetMethodDebugInformation(MetadataTokens.MethodDefinitionHandle(token & 0xffffff))
                .GetSequencePoints().OrderBy(p => p.Offset).ToArray();
            ManagedSourceMapRowV1[] rows = imported.Program.Instructions.OrderBy(i => i.Index).Where(i => i.SourceSpan is not null).Select(i =>
            {
                int offset = i.SourceSpan!.StartOffset;
                SequencePoint[] candidates = points.Where(p => p.Offset <= offset).ToArray();
                string? document = null, checksum = null; int? line = null, column = null;
                if (pdb is not null && candidates.Length != 0 && !candidates[^1].IsHidden)
                {
                    SequencePoint point = candidates[^1];
                    DocumentHandle handle = point.Document.IsNil
                        ? pdb.GetMethodDebugInformation(MetadataTokens.MethodDefinitionHandle(token & 0xffffff)).Document : point.Document;
                    if (!handle.IsNil)
                    {
                        Document doc = pdb.GetDocument(handle); document = pdb.GetString(doc.Name);
                        checksum = Convert.ToHexString(pdb.GetBlobBytes(doc.Hash)); line = point.StartLine; column = point.StartColumn;
                    }
                }
                return new ManagedSourceMapRowV1(imported.Provenance.MethodIdentity, token, offset, i.Index,
                    document, checksum, line, column);
            }).ToArray();
            return new(peHash, portablePdb.IsEmpty ? null : Convert.ToHexString(SHA256.HashData(portablePdb.Span)).ToLowerInvariant(), Array.AsReadOnly(rows));
        }
        catch (BadImageFormatException e) { throw new ArgumentException("Malformed Portable PDB source-map input.", nameof(portablePdb), e); }
        finally { provider?.Dispose(); }
    }
}
