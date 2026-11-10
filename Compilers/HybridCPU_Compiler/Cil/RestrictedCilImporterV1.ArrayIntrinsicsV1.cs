using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    private HelperResolution ResolveArrayEmptyHelper(MetadataReader metadata, MethodSpecificationHandle handle)
    {
        MethodSpecification specification = metadata.GetMethodSpecification(handle);
        if (_mode != RestrictedCilImportModeV1.ScalarControlFlowV2 ||
            specification.Method.Kind != HandleKind.MemberReference)
            return Unsupported();
        MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)specification.Method);
        if (member.GetKind() != MemberReferenceKind.Method || member.Parent.Kind != HandleKind.TypeReference ||
            ParentTypeName(metadata, member.Parent) != "System.Array" || metadata.GetString(member.Name) != "Empty" ||
            ReferencedAssemblyName(metadata, (TypeReferenceHandle)member.Parent) is not
                ("System.Runtime" or "System.Private.CoreLib" or "mscorlib") ||
            !metadata.GetBlobBytes(member.Signature).AsSpan().SequenceEqual(new byte[] { 0x10, 1, 0, 0x1d, 0x1e, 0 }))
            return Unsupported();

        int? valueToken = null;
        string? valueIdentity = null;
        MetadataStorage? valueStorage = null;
        string elementIdentity;
        if (TryParseMethodSpecificationArguments(metadata, handle, [], [], out IReadOnlyList<RestrictedCilGenericArgumentV1>? arguments, out _) && arguments.Count == 1)
            elementIdentity = arguments[0].StableTypeIdentity;
        else
        {
            // Array.Empty never passes T through the call ABI. Parse this intrinsic's value
            // element independently, without widening general MethodSpec/scalar-generic support.
            BlobReader reader = metadata.GetBlobReader(specification.Signature);
            if (reader.RemainingBytes < 3 || reader.ReadByte() != 0x0a || reader.ReadCompressedInteger() != 1 ||
                reader.ReadByte() != 0x11) return Unsupported();
            EntityHandle element = ReadTypeDefOrRefEncoded(ref reader);
            if (reader.RemainingBytes != 0 || element.Kind != HandleKind.TypeDefinition || element.IsNil) return Unsupported();
            TypeDefinition type = metadata.GetTypeDefinition((TypeDefinitionHandle)element);
            if (type.GetGenericParameters().Count != 0 || ExactTypeHandleName(metadata, type.BaseType) is not
                    ("System.ValueType" or "System.Enum") ||
                type.GetCustomAttributes().Any(attribute => DescribeMethodReference(metadata,
                    MetadataTokens.GetToken(metadata.GetCustomAttribute(attribute).Constructor)) ==
                    "System.Runtime.CompilerServices.IsByRefLikeAttribute..ctor")) return Unsupported();
            elementIdentity = $"[{metadata.GetString(metadata.GetAssemblyDefinition().Name)}]{ExactTypeHandleName(metadata, element)}";
            valueToken = MetadataTokens.GetToken(element);
            valueIdentity = elementIdentity;
            valueStorage = new InlineStorageResolver([]).Value(metadata, (TypeDefinitionHandle)element);
            if (valueStorage is null) return Unsupported();
        }
        string arrayIdentity = elementIdentity + "[]";
        string identity = $"System.Array.Empty<{elementIdentity}>():{arrayIdentity}";
        RestrictedCilHelperContractV1 contract = new RestrictedCilHelperContractV1(identity, [], RestrictedCilTypeV1.ObjectReference,
            "__hybridcpu_managed_array_empty", "runtime-helper", IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
            IrArchitecturalEffectKind.Control);
        return new(new(identity, contract, false, EmptyArrayTypeIdentity: arrayIdentity,
            EmptyValueElementToken: valueToken, EmptyValueElementIdentity: valueIdentity, EmptyValueStorage: valueStorage), null);

        static HelperResolution Unsupported() => new(null, Reject(RestrictedCilImportStatusV1.Unsupported,
            "HCCIL1461", "The MethodSpec is not an exact closed System.Array.Empty<T>() intrinsic."));
    }

    private RestrictedCilArrayTypeBindingV1? ResolveEmptyArrayBinding(ResolvedHelper helper)
    {
        RestrictedCilArrayTypeBindingV1[] matches = _arrayBindings.Values.Where(binding => binding.TypeDescriptor.StableIdentity == helper.EmptyArrayTypeIdentity &&
                ValidArrayBinding(binding) && (helper.EmptyValueElementToken is not int token ||
                    ValidEmptyValueElement(binding, token, helper.EmptyValueElementIdentity!, helper.EmptyValueStorage!)))
            .DistinctBy(static binding => (binding.TypeHandle, binding.TypeDescriptor.DescriptorDigest)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool ValidEmptyValueElement(RestrictedCilArrayTypeBindingV1 binding, int token, string identity, MetadataStorage storage) =>
        binding.ElementTypeMetadataToken == token && binding.ElementTypeDescriptor is { } element &&
        ValidDescriptor(element) && element.Kind == HybridCpuManagedTypeKindV1.ValueType &&
        element.StableIdentity == identity && element.TypeId == MetadataTypeId(identity) &&
        element.ValueTypeShape is { ObjectReferenceOffsets.Count: 0 } value &&
        value.PayloadSizeBytes == storage.Size && value.PayloadAlignmentBytes == storage.Alignment &&
        element.InstanceFields.All(static field => field.StorageKind != HybridCpuManagedStorageKindV1.ObjectReference) &&
        binding.TypeDescriptor.ArrayShape is { ElementStorageKind: HybridCpuManagedStorageKindV1.BlittableValue,
            RequiresReferenceStoreCheck: false } array && array.ElementTypeId == element.TypeId &&
        array.ElementSizeBytes == value.PayloadSizeBytes && array.ElementAlignmentBytes == value.PayloadAlignmentBytes;
}
