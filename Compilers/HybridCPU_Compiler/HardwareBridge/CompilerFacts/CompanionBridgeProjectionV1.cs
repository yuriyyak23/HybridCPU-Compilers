// Typed projection of the existing Roslyn Companion/CIL body world. Built only in the opt-in Companion project.
#if HARDWAREBRIDGE_COMPANION
using System.Security.Cryptography;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Roslyn;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;
using Microsoft.CodeAnalysis.CSharp;

namespace HybridCPU.Compiler.HardwareBridge.CompilerFacts;

/// <summary>Result of a single owned Companion invocation; candidate-only even on admission success.</summary>
public sealed record BridgeCompanionExportV1(BridgeSourceSnapshotV1 Source, BridgeCompilerFactsV1 Facts);

/// <summary>
/// Calls existing Companion once on caller-owned immutable copies; does not import CIL itself,
/// infer a source root, load assemblies, execute PE bytes or qualify hardware.
/// </summary>
public static class CompanionBridgeProjectionV1
{
    public static BridgeCompanionExportV1 CompileBodyWorld(
        CompanionSourceRequestV1 request, RestrictedCilImporterV1 importer,
        IReadOnlyList<RestrictedCilMethodSelectorV1> roots,
        IReadOnlyList<int> bodyReferenceIndices, IReadOnlyList<int> metadataReferenceIndices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(bodyReferenceIndices);
        ArgumentNullException.ThrowIfNull(metadataReferenceIndices);
        cancellationToken.ThrowIfCancellationRequested();
        // Validate the original request before allocating any extra reference copies.
        var original = Describe(request, roots, bodyReferenceIndices, metadataReferenceIndices);
        BridgeSourceSnapshotV1 first = Snapshot(original, cancellationToken);
        // Snapshot all inputs a second time, now into bridge-owned buffers. An intervening
        // caller mutation must be detected rather than claimed as matching Companion input.
        CompanionSourceDocumentV1[] documents = request.Documents
            .Select(d => new CompanionSourceDocumentV1(d.VirtualPath, d.Text)).ToArray();
        ReadOnlyMemory<byte>[] images = request.ReferenceImages
            .Select(i => (ReadOnlyMemory<byte>)i.ToArray()).ToArray();
        RestrictedCilMethodSelectorV1[] selected = roots.Select(r => r with
        {
            GenericTypeArguments = r.GenericTypeArguments?.ToArray(),
            GenericMethodArguments = r.GenericMethodArguments?.ToArray()
        }).ToArray();
        int[] body = bodyReferenceIndices.ToArray(), metadata = metadataReferenceIndices.ToArray();
        var ownedRequest = new CompanionSourceRequestV1(request.AssemblyName,
            Array.AsReadOnly(documents), Array.AsReadOnly(images)) { Language = request.Language };
        BridgeSourceSnapshotV1 owned = Snapshot(Describe(ownedRequest, selected, body, metadata), cancellationToken);
        if (!string.Equals(first.SnapshotDigest, owned.SnapshotDigest, StringComparison.Ordinal))
            throw new ArgumentException("Source/reference inputs changed during the immutable ownership transfer.");
        cancellationToken.ThrowIfCancellationRequested();
        ManagedSourceBodyWorldResultV1 compiled = ManagedSourceCompanionV1.CompileBodyWorld(
            ownedRequest, importer, Array.AsReadOnly(selected), body, metadata, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(owned, Project(owned, compiled, cancellationToken));
    }

    private static BridgeSourceRequestV1 Describe(
        CompanionSourceRequestV1 request, IReadOnlyList<RestrictedCilMethodSelectorV1> roots,
        IReadOnlyList<int> body, IReadOnlyList<int> metadata)
    {
        return new(request.AssemblyName, request.Language.ToString(),
            request.Documents.Select(d => new BridgeSourceTextV1(d.VirtualPath, d.Text)).ToArray(),
            request.ReferenceImages,
            roots.Select(r => new BridgeRootSelectorV1(r.TypeName, r.MethodName, r.MetadataToken,
                (r.GenericTypeArguments ?? []).Select(GenericName).ToArray(),
                (r.GenericMethodArguments ?? []).Select(GenericName).ToArray())).ToArray(),
            body, metadata);
    }

    private static string GenericName(RestrictedCilGenericArgumentV1 arg) =>
        arg.StableTypeIdentity + ":" + arg.CarrierType;

    private static BridgeSourceSnapshotV1 Snapshot(BridgeSourceRequestV1 input, CancellationToken ct) =>
        BridgeSourceSnapshotV1.Capture(input, ManagedSourceCompanionV1.SourceAdmissionPolicy,
            HybridCPU.Compiler.Core.IR.HybridCpuCompilerContract.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            typeof(CSharpCompilation).Assembly.GetName().Version!.ToString(), ct);

    /// <summary>Rejects stale source maps, unowned dependency PE, partial graphs and ambiguous root binding.</summary>
    public static BridgeCompilerFactsV1 Project(BridgeSourceSnapshotV1 snapshot,
        ManagedSourceBodyWorldResultV1 compiled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(compiled);
        cancellationToken.ThrowIfCancellationRequested();
        ManagedSourceCompanionResultV1 source = compiled.Source;
        var diagnostics = new List<BridgeDiagnosticV1>();
        if (source is null) return BridgeCompilerFactsV1.Rejected(snapshot.SnapshotDigest,
            "HB2000", "Companion source result is absent.");
        diagnostics.AddRange(source.Diagnostics.Select(d => new BridgeDiagnosticV1("Roslyn", d.Id, d.Severity,
            d.Message, d.Document, d.Start, d.Length)));
        if (compiled.Compilation is { } reported)
            diagnostics.AddRange(reported.Diagnostics.Select(d => new BridgeDiagnosticV1("CIL", d.Code, "Diagnostic",
                d.Message, d.StableSourceIdentity)));
        BridgeCompilerFactsV1 Fail(string code, string message)
        {
            var rejected = BridgeCompilerFactsV1.Rejected(snapshot.SnapshotDigest, code, message);
            return rejected with { Diagnostics = Array.AsReadOnly(diagnostics.Concat(rejected.Diagnostics)
                .OrderBy(d => d.Layer, StringComparer.Ordinal).ThenBy(d => d.Code, StringComparer.Ordinal)
                .ThenBy(d => d.Message, StringComparer.Ordinal).ToArray()) };
        }
        if (!snapshot.HasConsistentDigest() ||
            snapshot.Documents.Count == 0 || snapshot.Roots.Count == 0)
            return Fail("HB2001", "Unknown or empty source snapshot.");
        if (source.Pe is null || source.Pdb is null || source.ArtifactIdentity is null ||
            source.SourceInputDigest is null || !IsSha(source.SourceInputDigest))
            return Fail("HB2002", "Companion emission, SourceInputDigest or exact artifact identity missing.");
        if (!source.ArtifactIdentity.Matches(source.Pe, source.Pdb) ||
            source.ArtifactIdentity.Schema != CompanionArtifactIdentityV1.SchemaV1)
            return Fail("HB2003", "Companion PE/PDB identity mismatch.");
        if (compiled.Compilation is not { Status: RestrictedCilImportStatusV1.Success, Graph: not null } graph ||
            graph.Graph.BodyWorldMode != ManagedBodyWorldModeV1.StandaloneRestrictedModule)
            return Fail("HB2004", "Closed StandaloneRestrictedModule call graph was not admitted.");
        if (string.IsNullOrWhiteSpace(graph.Graph.GraphDigest) ||
            string.IsNullOrWhiteSpace(graph.Graph.ContractDigest) ||
            graph.Methods.Count is < 1 or > 256 || graph.Graph.Edges.Count > 16384 ||
            graph.Graph.RootIdentities.Count != snapshot.Roots.Count ||
            graph.Graph.MethodIdentities.Count != graph.Methods.Count)
            return Fail("HB2005", "Incomplete graph or bridge method/edge budget exceeded.");
        if (graph.Graph.RootIdentities.Distinct(StringComparer.Ordinal).Count() != graph.Graph.RootIdentities.Count ||
            graph.Graph.MethodIdentities.Distinct(StringComparer.Ordinal).Count() != graph.Graph.MethodIdentities.Count)
            return Fail("HB2006", "Duplicate graph identity.");
        if (graph.Methods.Select(m => m.Identity.StableIdentity).Distinct(StringComparer.Ordinal).Count() != graph.Methods.Count)
            return Fail("HB2006", "Duplicate imported method identity.");
        var methods = graph.Methods.ToDictionary(m => m.Identity.StableIdentity, StringComparer.Ordinal);
        if (
            graph.Graph.MethodIdentities.Any(id => !methods.ContainsKey(id)) ||
            graph.Graph.RootIdentities.Any(id => !methods.ContainsKey(id)) ||
            graph.Graph.Edges.Any(e => !methods.ContainsKey(e.CallerIdentity) ||
                                       !methods.ContainsKey(e.CalleeIdentity)))
            return Fail("HB2007", "Call graph is not closed over its declared method identities.");
        foreach (BridgeRootSelectorV1 root in snapshot.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = graph.Graph.RootIdentities.Select(id => methods[id].Identity).Where(id =>
                id.DeclaringType == root.TypeName && id.MethodName == root.MethodName &&
                (root.MetadataToken is null || id.MetadataToken == root.MetadataToken) &&
                (id.GenericTypeArguments ?? []).Select(GenericName).SequenceEqual(root.TypeArguments) &&
                (id.GenericMethodArguments ?? []).Select(GenericName).SequenceEqual(root.MethodArguments)).ToArray();
            if (matches.Length != 1)
                return Fail("HB2008", "Root type/name/token/generic selection is absent or ambiguous.");
        }
        string primary = source.ArtifactIdentity.PeSha256;
        var owned = snapshot.References.Where(r => r.Role == "owned-body")
            .Select(r => r.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projected = new List<BridgeMethodFactV1>();
        bool foundPrimaryBody = false;
        int _rows = 0;
        var mapRows = new List<BridgeMapRowV1>();
        foreach (ManagedCompiledMethodV1 method in graph.Methods.OrderBy(m => m.Identity.StableIdentity, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provenance = method.Import.Provenance;
            if (method.Import.Status != RestrictedCilImportStatusV1.Success ||
                method.Import.Program is null || provenance is null || !IsSha(provenance.PeSha256) ||
                ( !string.Equals(provenance.PeSha256, primary, StringComparison.OrdinalIgnoreCase) &&
                 !owned.Contains(provenance.PeSha256)) ||
                !string.Equals(method.Identity.StableIdentity, provenance.MethodIdentity, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(method.Identity.CanonicalSignature) ||
                string.IsNullOrWhiteSpace(method.Identity.IdentityDigest))
                return Fail("HB2009", "Method status, exact signature or body PE provenance is invalid/unowned.");
            if (method.DirectCalleeIdentities.Any(c => !methods.ContainsKey(c)))
                return Fail("HB2010", "Direct callee has no imported body.");
            projected.Add(new(method.Identity.StableIdentity, method.Identity.AssemblyIdentity,
                method.Identity.ModuleName, method.Identity.DeclaringType, method.Identity.MethodName,
                method.Identity.CanonicalSignature, method.Identity.MetadataToken,
                method.Identity.IdentityDigest, provenance.PeSha256, method.Import.Status.ToString(),
                Array.AsReadOnly(method.DirectCalleeIdentities.Order(StringComparer.Ordinal).ToArray())));
            diagnostics.AddRange(method.Import.Diagnostics.Select(d => new BridgeDiagnosticV1("CIL-method",
                d.Code, "Diagnostic", d.Message, d.StableSourceIdentity)));
            if (!string.Equals(provenance.PeSha256, primary, StringComparison.OrdinalIgnoreCase))
            {
                // The primary PDB must never be applied to a dependency method.
                if (compiled.SourceMaps.ContainsKey(method.Identity.StableIdentity))
                    return Fail("HB2011", "Dependency method was incorrectly mapped with the primary PDB.");
                continue;
            }
            foundPrimaryBody = true;
            if (!compiled.SourceMaps.TryGetValue(method.Identity.StableIdentity, out ManagedSourceMapV1? map) ||
                map is null || !string.Equals(map.PeSha256, primary, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(map.PdbSha256, source.ArtifactIdentity.PdbSha256, StringComparison.OrdinalIgnoreCase))
                return Fail("HB2012", "Primary method source-map PE/PDB identity is missing or stale.");
            ManagedSourceMapV1 fresh;
            try { fresh = ManagedSourceMapV1.Create(method.Import, source.Pe, source.Pdb); }
            catch (ArgumentException) { return Fail("HB2013", "Portable PDB is malformed or does not match the primary PE."); }
            if (!string.Equals(map.Digest, fresh.Digest, StringComparison.Ordinal))
                return Fail("HB2014", "Existing primary source-map content does not match source/PE/PDB.");
            foreach (ManagedSourceMapRowV1 row in map.Rows.OrderBy(r => r.IrIndex).ThenBy(r => r.IlOffset))
            {
                if (++_rows > 131072) return Fail("HB2015", "Source-map row budget exceeded.");
                mapRows.Add(new(method.Identity.StableIdentity, map.PeSha256, map.PdbSha256!,
                    row.MethodToken, row.IlOffset, row.IrIndex, row.Document, row.DocumentChecksum,
                    row.Line, row.Column));
            }
        }
        if (!foundPrimaryBody) return Fail("HB2017", "No primary-module method body was imported.");
        if (compiled.SourceMaps.Keys.Any(key => !methods.ContainsKey(key)) ||
            graph.Graph.Edges.Any(e => !methods[e.CallerIdentity].DirectCalleeIdentities.Contains(e.CalleeIdentity)))
            return Fail("HB2016", "Unexpected source-map owner or disagreement between edges and direct callees.");
        var edges = graph.Graph.Edges.OrderBy(e => e.StableId, StringComparer.Ordinal)
            .Select(e => new BridgeEdgeFactV1(e.StableId, e.CallerIdentity, e.CalleeIdentity,
                e.CallerIlOffset, e.MetadataToken)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new(BridgeCompilerFactsV1.SchemaId, "Candidate", snapshot.SnapshotDigest,
            source.SourceInputDigest, source.ArtifactIdentity.Schema, source.ArtifactIdentity.PeSha256,
            source.ArtifactIdentity.PdbSha256, graph.Graph.GraphDigest, graph.Graph.ContractDigest,
            graph.Status.ToString(), Array.AsReadOnly(graph.Graph.RootIdentities.Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(projected.ToArray()), Array.AsReadOnly(edges), Array.AsReadOnly(mapRows.ToArray()),
            Array.AsReadOnly(diagnostics.OrderBy(d => d.Layer, StringComparer.Ordinal)
                .ThenBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal).ToArray()));

    }

    private static bool IsSha(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}
#endif
