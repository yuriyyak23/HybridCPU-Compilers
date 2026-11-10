using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    /// <summary>Source-admission facts only. Existing importer independently verifies the emitted caller and constructor.</summary>
    public static IReadOnlyList<string> QualifySourceInt32Constructors(ReadOnlyMemory<byte> image,
        CancellationToken cancellationToken = default)
        => Array.AsReadOnly(QualifySourceScalarConstructors(image, cancellationToken)
            .Where(fact => fact.ParameterType == RestrictedCilTypeV1.Int32).Select(fact => fact.TypeName).ToArray());

    /// <summary>Exact pure-copy constructor parameter facts; names alone do not authorize another overload.</summary>
    public static IReadOnlyList<(string TypeName, RestrictedCilTypeV1 ParameterType)> QualifySourceScalarConstructors(
        ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (image.IsEmpty || image.Length > RestrictedCilImportBudgetsV1.Production.MaximumPeBytes) return [];
        using ManagedModuleContext module = new ManagedModuleContext(image.ToArray(), "source-constructor-admission/v1");
        ManagedMetadataBindingSet bindings = BuildMetadataBindings([module]);
        List<(string TypeName, RestrictedCilTypeV1 ParameterType)> result = new List<(string TypeName, RestrictedCilTypeV1 ParameterType)>();
        foreach (RestrictedCilAllocationBindingV1? binding in bindings.AllocationsByAssembly.GetValueOrDefault(module.AssemblyName) ?? []) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!binding.IsScalarValueProjection ||
                binding.ScalarValueType is not (RestrictedCilTypeV1.Int32 or RestrictedCilTypeV1.Int64) ||
                binding.ScalarArgumentType != binding.ScalarValueType || !IsExactScalarCarrierLayout(binding)) continue;
            MethodDefinition method = module.Metadata.GetMethodDefinition((System.Reflection.Metadata.MethodDefinitionHandle)MetadataTokens.EntityHandle(binding.ConstructorMetadataToken));
            TypeDefinition type = module.Metadata.GetTypeDefinition(method.GetDeclaringType());
            if (!type.GetDeclaringType().IsNil || type.GetGenericParameters().Count != 0 ||
                type.Attributes.HasFlag(TypeAttributes.ExplicitLayout) ||
                type.GetMethods().Any(h => module.Metadata.GetString(module.Metadata.GetMethodDefinition(h).Name) == ".cctor")) continue;
            result.Add((FullTypeName(module.Metadata, method.GetDeclaringType()), binding.ScalarArgumentType));
        }
        return Array.AsReadOnly(result.Distinct().OrderBy(fact => fact.TypeName, StringComparer.Ordinal)
            .ThenBy(fact => fact.ParameterType).ToArray());
    }
}
