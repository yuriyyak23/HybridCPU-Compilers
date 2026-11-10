using System.Reflection.Metadata;
using HybridCPU.Compiler.Core.IR;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    private static RestrictedCilTypeV1 ReadAnalysisType(MetadataReader metadata, ref BlobReader reader,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? typeArguments,
        IReadOnlyList<RestrictedCilGenericArgumentV1>? methodArguments, bool allowAggregates, out string? aggregateIdentity)
    {
        aggregateIdentity = null;
        BlobReader start = reader;
        RestrictedCilTypeV1 carrier = ReadType(metadata, ref reader, typeArguments, methodArguments);
        if (!allowAggregates || carrier != RestrictedCilTypeV1.UnsupportedManaged || start.RemainingBytes == 0 ||
            start.ReadByte() != 0x11) return carrier;
        EntityHandle type = ReadTypeDefOrRefEncoded(ref start);
        aggregateIdentity = AggregateIdentity(metadata, type);
        return aggregateIdentity is null ? carrier : RestrictedCilTypeV1.Aggregate;
    }

    private static string? AggregateIdentity(MetadataReader metadata, EntityHandle handle)
    {
        if (handle.IsNil) return null;
        string name = NestedTypeName(metadata, handle); // Bound nested TypeRef chains before walking assembly scopes.
        string? assembly;
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            TypeDefinition type = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
            if (type.GetGenericParameters().Count != 0 || ExactTypeHandleName(metadata, type.BaseType) != "System.ValueType")
                return null;
            assembly = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        }
        else if (handle.Kind == HandleKind.TypeReference)
            assembly = ReferencedAssemblyName(metadata, (TypeReferenceHandle)handle);
        else return null; // Open/constructed generic payloads need exact substitution and layout.
        return string.IsNullOrWhiteSpace(assembly) ? null : $"[{assembly}]{name}";
    }

    // An aggregate's identity includes its enclosing types. Two Outer+Point definitions
    // must never unify just because legacy diagnostic owner names both say "Point".
    private static string NestedTypeName(MetadataReader metadata, EntityHandle handle, int depth = 0)
    {
        if (depth >= 64 || handle.IsNil) throw new BadImageFormatException("Nested type identity budget/handle.");
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            TypeDefinition type = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
            return type.GetDeclaringType().IsNil ? FullTypeName(metadata, (TypeDefinitionHandle)handle) :
                NestedTypeName(metadata, type.GetDeclaringType(), depth + 1) + "+" + metadata.GetString(type.Name);
        }
        if (handle.Kind == HandleKind.TypeReference)
        {
            TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)handle);
            return type.ResolutionScope.Kind != HandleKind.TypeReference ? TypeReferenceName(metadata, (TypeReferenceHandle)handle) :
                NestedTypeName(metadata, type.ResolutionScope, depth + 1) + "+" + metadata.GetString(type.Name);
        }
        throw new BadImageFormatException("Exact named type identity required.");
    }


    private static bool ExactStackCompatible(V2Value value, RestrictedCilTypeV1 expected, string? aggregateIdentity) =>
        StackCompatible(value.Type, expected) &&
        (expected != RestrictedCilTypeV1.Aggregate || aggregateIdentity is not null && value.AggregateIdentity == aggregateIdentity);

    private static bool LocalStackCompatible(V2Value value, RestrictedCilTypeV1 expected, string? aggregateIdentity) =>
        IsNullableLocal(aggregateIdentity) || IsNullableLocal(value.AggregateIdentity)
            ? IsNullableLocal(aggregateIdentity) && value.AggregateIdentity == aggregateIdentity && value.Type == expected &&
              (aggregateIdentity != NullableInt64Identity || value.NullableHasValue is not null)
            : ExactStackCompatible(value, expected, aggregateIdentity) ||
              value.Type == RestrictedCilTypeV1.UInt32 && expected == RestrictedCilTypeV1.Int32;

    private static bool CallStackCompatible(V2Value value, RestrictedCilTypeV1 expected, string? aggregateIdentity) =>
        IsNullableLocal(value.AggregateIdentity) || IsNullableLocal(aggregateIdentity)
            ? expected == RestrictedCilTypeV1.Int64 && value.Type == RestrictedCilTypeV1.Int64 &&
              aggregateIdentity == NullableInt32Identity && value.AggregateIdentity == NullableInt32Identity
            :
        (expected == RestrictedCilTypeV1.ManagedByRef
            ? IsExactReceiverLoanIdentity(aggregateIdentity, value.ReceiverIdentity) &&
              value.Type == RestrictedCilTypeV1.ManagedByRef
            : ExactStackCompatible(value, expected, aggregateIdentity)) ||
        value.Type == RestrictedCilTypeV1.UInt32 && expected == RestrictedCilTypeV1.Int32;

    // The managed signature denotes a receiver byref as "[Assembly]Type&", while
    // dataflow carries the owned value-type identity without the carrier suffix.
    // Keep that conversion local and exact: a general managed-byref must not enter
    // the aggregate path merely because both sides happen to be byrefs.
    private static bool IsExactReceiverLoanIdentity(string? signatureIdentity, string? receiverIdentity) =>
        signatureIdentity is { Length: > 1 } && signatureIdentity.EndsWith('&') &&
        receiverIdentity is not null &&
        string.Equals(signatureIdentity[..^1], receiverIdentity, StringComparison.Ordinal);

    private static bool IsNonNegativeI4Constant(V2Value value) =>
        value.Type == RestrictedCilTypeV1.Int32 &&
        value.Operand.Kind == IrOperandKind.Constant &&
        value.Operand.Value <= int.MaxValue;

    private static V2Value NormalizeLocalValue(V2Value value, RestrictedCilTypeV1 expected, string identity) =>
        value.Type == RestrictedCilTypeV1.UInt32 && expected == RestrictedCilTypeV1.Int32
            ? V2Value.Definition(RestrictedCilTypeV1.Int32, identity + ":i4-local")
            : value;

    private static V2Value AnalysisArgument(MethodSignature signature, int index, string method) =>
        V2Value.Argument(index, StackType(signature.Parameters[index]), method) with
        { AggregateIdentity = signature.AggregateParameters?.ElementAtOrDefault(index),
            ReceiverIdentity = index == 0 ? signature.Receiver?.ScopedTypeIdentity : null };
}
