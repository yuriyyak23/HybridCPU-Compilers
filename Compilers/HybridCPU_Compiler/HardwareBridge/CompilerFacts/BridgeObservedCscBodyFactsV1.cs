#if HARDWAREBRIDGE_COMPANION
using System.Security.Cryptography;
using HybridCPU.Compiler.Cil;

namespace HybridCPU.Compiler.HardwareBridge.CompilerFacts;

public sealed record BridgeObservedCscRootV1(string TypeName, string MethodName, int MethodToken);

public sealed record BridgeObservedCscMethodV1(
    ManagedMethodIdentityV1 Identity, ManagedSourceMapV1 SourceMap);

public sealed record BridgeObservedCscBodyFactsV1(
    string Schema, string CaptureSha256, string PeSha256, string PdbSha256,
    ManagedCallGraphEvidenceV1 CallGraph, IReadOnlyList<BridgeObservedCscMethodV1> Methods);

/// <summary>Imports a selected primary-module body closure from caller-pinned Csc output.
/// CaptureSha256 identifies external evidence. This importer provides no attestation
/// of execution, Companion source admission, or hardware verification.
/// </summary>
public static class BridgeObservedCscBodyImporterV1
{
    public const string Schema = "hybridcpu.hardwarebridge.observed-csc-body-facts/v1";

    public static BridgeObservedCscBodyFactsV1 Import(
        ReadOnlyMemory<byte> pe, ReadOnlyMemory<byte> portablePdb,
        string captureSha256, string expectedPeSha256, string expectedPdbSha256,
        IReadOnlyList<BridgeObservedCscRootV1> roots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSha(captureSha256) || !IsSha(expectedPeSha256) || !IsSha(expectedPdbSha256))
            throw new ArgumentException("Expected lowercase SHA-256 capture, PE and PDB pins.");
        if (pe.Length == 0 || pe.Length > RestrictedCilImportBudgetsV1.Production.MaximumPeBytes ||
            portablePdb.Length is 0 or > 32 * 1024 * 1024)
            throw new ArgumentException("Observed Csc PE or portable PDB exceeds the import byte bounds.");
        if (roots.Count is < 1 or > 32)
            throw new ArgumentException("One to 32 exact MethodDef roots are required.", nameof(roots));

        var selectors = new List<RestrictedCilMethodSelectorV1>(roots.Count);
        var tokens = new HashSet<int>();
        foreach (BridgeObservedCscRootV1 root in roots)
        {
            if (root is null || string.IsNullOrWhiteSpace(root.TypeName) ||
                string.IsNullOrWhiteSpace(root.MethodName) ||
                root.MethodToken is <= 0x06000000 or > 0x06ffffff || !tokens.Add(root.MethodToken))
                throw new ArgumentException("Unique named MethodDef roots are required.", nameof(roots));
            selectors.Add(new(root.TypeName, root.MethodName, root.MethodToken));
        }

        byte[] ownedPe = pe.ToArray();
        byte[] ownedPdb = portablePdb.ToArray();
        if (Hash(ownedPe) != expectedPeSha256 || Hash(ownedPdb) != expectedPdbSha256)
            throw new InvalidDataException("Observed Csc PE or portable PDB differs from its expected pin.");

        var importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        ManagedCallGraphCompilationV1 compiled = importer.ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            ownedPe, Schema + ":" + captureSha256, selectors, []), cancellationToken);
        if (compiled.Status != RestrictedCilImportStatusV1.Success || compiled.Graph is null)
            throw new InvalidDataException("Observed Csc body import refused: " +
                string.Join(" | ", compiled.Diagnostics.Select(d => d.Code + " " + d.Message)));

        var methods = new List<BridgeObservedCscMethodV1>(compiled.Methods.Count);
        foreach (ManagedCompiledMethodV1 method in compiled.Methods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManagedSourceMapV1 map = ManagedSourceMapV1.Create(method.Import, ownedPe, ownedPdb);
            if (!map.Rows.Any(row => row.DocumentChecksum is not null && row.Line is not null))
                throw new InvalidDataException("Observed Csc owned method has no visible PDB source location.");
            methods.Add(new(method.Identity, map));
        }
        return new(Schema, captureSha256, expectedPeSha256, expectedPdbSha256,
            compiled.Graph, methods.AsReadOnly());
    }

    private static bool IsSha(string value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
