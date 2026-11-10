using System.Collections.ObjectModel;
using System.Security.Cryptography;
using HybridCPU.Compiler.Cil;

namespace HybridCPU.Compiler.Roslyn;

/// <summary>Emitted source artifacts plus existing closed graph and exact primary-module maps. No runtime authority.</summary>
public sealed record ManagedSourceBodyWorldResultV1(
    ManagedSourceCompanionResultV1 Source,
    ManagedCallGraphCompilationV1? Compilation,
    IReadOnlyDictionary<string, ManagedSourceMapV1> SourceMaps);

public static partial class ManagedSourceCompanionV1
{
    /// <summary>
    /// Explicit StandaloneRestrictedModule graph ingress. Selected owned reference images supply bodies;
    /// other Roslyn references grant no body-world authority. Caller keeps input lists stable until snapshot.
    /// SourceInputDigest identifies source/emit inputs; existing graph/import identities bind roots/closure/options.
    /// </summary>
    public static ManagedSourceBodyWorldResultV1 CompileBodyWorld(
        CompanionSourceRequestV1 request, RestrictedCilImporterV1 importer,
        IReadOnlyList<RestrictedCilMethodSelectorV1> roots,
        IReadOnlyList<int>? dependencyReferenceIndices = null, CancellationToken cancellationToken = default)
        => CompileBodyWorld(request, importer, roots, dependencyReferenceIndices, [], cancellationToken);

    /// <summary>Explicit disjoint body and declaration-only selections from the same owned reference closure.</summary>
    public static ManagedSourceBodyWorldResultV1 CompileBodyWorld(
        CompanionSourceRequestV1 request, RestrictedCilImporterV1 importer,
        IReadOnlyList<RestrictedCilMethodSelectorV1> roots,
        IReadOnlyList<int>? dependencyReferenceIndices, IReadOnlyList<int> metadataReferenceIndices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(roots);
        cancellationToken.ThrowIfCancellationRequested();
        RestrictedCilMethodSelectorV1[] selectedRoots = roots.Select(root => root is null
            ? throw new ArgumentException("Non-null source roots required.", nameof(roots))
            : root with { GenericTypeArguments = root.GenericTypeArguments?.ToArray(),
                GenericMethodArguments = root.GenericMethodArguments?.ToArray() }).ToArray();
        if (selectedRoots.Length == 0) throw new ArgumentException("At least one source root required.", nameof(roots));
        int[] indices = dependencyReferenceIndices?.ToArray() ?? [];
        ArgumentNullException.ThrowIfNull(metadataReferenceIndices);
        int[] metadataIndices = metadataReferenceIndices.ToArray();
        SourceSnapshot input = SnapshotSource(request, cancellationToken);
        if (indices.Any(index => index < 0 || index >= input.Images.Length) || indices.Distinct().Count() != indices.Length)
            throw new ArgumentException("Unique indices into the owned source reference closure required.", nameof(dependencyReferenceIndices));
        if (metadataIndices.Any(index => index < 0 || index >= input.Images.Length) ||
            metadataIndices.Distinct().Count() != metadataIndices.Length || metadataIndices.Intersect(indices).Any())
            throw new ArgumentException("Unique metadata indices disjoint from body indices required.", nameof(metadataReferenceIndices));
        Dictionary<Microsoft.CodeAnalysis.IAssemblySymbol, HashSet<(string TypeName, Microsoft.CodeAnalysis.SpecialType ParameterType)>> qualified = new Dictionary<Microsoft.CodeAnalysis.IAssemblySymbol,
            HashSet<(string TypeName, Microsoft.CodeAnalysis.SpecialType ParameterType)>>(Microsoft.CodeAnalysis.SymbolEqualityComparer.Default);
        Microsoft.CodeAnalysis.MetadataReference[] compilationReferences = input.Compilation.References.ToArray();
        foreach (int index in indices) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                IReadOnlyList<(string TypeName, RestrictedCilTypeV1 ParameterType)> types = RestrictedCilImporterV1.QualifySourceScalarConstructors(input.Images[index], cancellationToken);
                if (types.Count != 0 && input.Compilation.GetAssemblyOrModuleSymbol(compilationReferences[index]) is Microsoft.CodeAnalysis.IAssemblySymbol assembly)
                    qualified.Add(assembly, types.Select(fact => (fact.TypeName, fact.ParameterType == RestrictedCilTypeV1.Int64
                        ? Microsoft.CodeAnalysis.SpecialType.System_Int64 : Microsoft.CodeAnalysis.SpecialType.System_Int32)).ToHashSet());
            }
            catch (BadImageFormatException) { }
            catch (InvalidOperationException) { }
        }
        bool OwnedConstructor(Microsoft.CodeAnalysis.IMethodSymbol method) =>
            method.ContainingType is { TypeKind: Microsoft.CodeAnalysis.TypeKind.Struct, Arity: 0, ContainingType: null, IsRefLikeType: false } &&
            method.Parameters is [{ RefKind: Microsoft.CodeAnalysis.RefKind.None }] &&
            qualified.TryGetValue(method.ContainingAssembly, out HashSet<(string TypeName, Microsoft.CodeAnalysis.SpecialType ParameterType)>? types) &&
            types.Contains((method.ContainingType.ToDisplayString(Microsoft.CodeAnalysis.SymbolDisplayFormat.CSharpErrorMessageFormat),
                method.Parameters[0].Type.SpecialType));
        string admissionDigest = qualified.Count == 0 ? input.Digest :
            HybridCPU.Compiler.Core.IR.Contracts.CompilerContractCanonicalJson.Hash(
                HybridCPU.Compiler.Core.IR.Contracts.CompilerContractCanonicalJson.Serialize(new {
                    Schema = "hybridcpu.companion-owned-constructor-input/v1", Source = input.Digest,
                    BodyReferenceIndices = indices.Order().ToArray()
                }));
        ManagedSourceCompanionResultV1 source = AdmitAndEmit(input.Compilation, cancellationToken, OwnedConstructor) with { SourceInputDigest = admissionDigest };
        Dictionary<string, ManagedSourceMapV1> maps = new Dictionary<string, ManagedSourceMapV1>(StringComparer.Ordinal);
        ReadOnlyDictionary<string, ManagedSourceMapV1> ownedMaps = new ReadOnlyDictionary<string, ManagedSourceMapV1>(maps);
        if (source.Pe is null) return new(source, null, ownedMaps);
        ManagedBodyWorldModuleV1[] dependencies = indices.Select(index => new ManagedBodyWorldModuleV1(input.Images[index],
            "roslyn-reference/v1:" + Convert.ToHexString(SHA256.HashData(input.Images[index])).ToLowerInvariant())).ToArray();
        ManagedBodyWorldModuleV1[] metadataModules = metadataIndices.Select(index => new ManagedBodyWorldModuleV1(input.Images[index],
            "roslyn-metadata/v1:" + Convert.ToHexString(SHA256.HashData(input.Images[index])).ToLowerInvariant())).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            source.Pe, "roslyn-companion/v1", selectedRoots, [], DependencyModules: dependencies,
            MetadataOnlyModules: metadataModules), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (graph.Status == RestrictedCilImportStatusV1.Success)
        {
            string primaryHash = Convert.ToHexString(SHA256.HashData(source.Pe)).ToLowerInvariant();
            foreach (ManagedCompiledMethodV1 method in graph.Methods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A primary PDB never maps dependency method tokens, even when token numbers coincide.
                if (method.Import.Provenance?.PeSha256 == primaryHash)
                    maps.Add(method.Identity.StableIdentity, ManagedSourceMapV1.Create(method.Import, source.Pe, source.Pdb!));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(source, graph, ownedMaps);
    }
}
