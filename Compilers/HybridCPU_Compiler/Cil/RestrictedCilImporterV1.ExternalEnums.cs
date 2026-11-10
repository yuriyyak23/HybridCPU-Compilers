using System.Reflection.Metadata;
using HybridCPU.Compiler.Core.IR.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    private sealed record ExternalEnumCarrier(RestrictedCilTypeV1 Carrier, string DefinitionImageDigest, string? NullableLocalIdentity);
    // Only child importers created by a synchronous BodyWorld call receive this owned table.
    private IReadOnlyDictionary<string, ExternalEnumCarrier> _externalEnumCarriers =
        new Dictionary<string, ExternalEnumCarrier>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, ExternalEnumCarrier> CollectEnumCarriers(
        IEnumerable<ManagedModuleContext> modules, CancellationToken cancellationToken)
    {
        Dictionary<string, ExternalEnumCarrier> result = new Dictionary<string, ExternalEnumCarrier>(StringComparer.Ordinal);
        foreach (ManagedModuleContext module in modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (TypeDefinitionHandle handle in module.Metadata.TypeDefinitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (module.Metadata.GetTypeDefinition(handle).BaseType.IsNil) continue;
                RestrictedCilTypeV1 carrier = EnumCarrier(module.Metadata, handle);
                if (carrier == RestrictedCilTypeV1.UnsupportedManaged) continue;
                string identity = $"[{module.AssemblyName}]{NestedTypeName(module.Metadata, handle)}";
                if (!result.TryAdd(identity, new(carrier, module.ImageDigest,
                    carrier is RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.Int64 or RestrictedCilTypeV1.UInt64 or RestrictedCilTypeV1.UInt32 or RestrictedCilTypeV1.UInt16 or RestrictedCilTypeV1.Int16 or RestrictedCilTypeV1.UInt8 or RestrictedCilTypeV1.Int8 ? NullableEnumDefinitionIdentity(module.Metadata, handle) : null)))
                    throw new BadImageFormatException("Duplicate enum declaration identity.");
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, ExternalEnumCarrier> ReferencedEnumCarriers(
        MetadataReader metadata, IReadOnlyDictionary<string, ExternalEnumCarrier> declarations,
        CancellationToken cancellationToken)
    {
        Dictionary<string, ExternalEnumCarrier> result = new Dictionary<string, ExternalEnumCarrier>(StringComparer.Ordinal);
        foreach (TypeReferenceHandle handle in metadata.TypeReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? identity = AggregateIdentity(metadata, handle);
            if (identity is not null && declarations.TryGetValue(identity, out ExternalEnumCarrier? carrier)) result[identity] = carrier;
        }
        return result;
    }

    private string WithExternalEnumOptions(string original) => _externalEnumCarriers.Count == 0 ? original :
        CompilerContractCanonicalJson.Hash(CompilerContractCanonicalJson.Serialize(new {
            Schema = "hybridcpu.cil-external-enum-options/v2", Original = original,
            Bindings = _externalEnumCarriers.OrderBy(row => row.Key, StringComparer.Ordinal)
                .Select(row => new { Identity = row.Key, row.Value.Carrier, row.Value.DefinitionImageDigest, row.Value.NullableLocalIdentity }).ToArray()
        }));
}
