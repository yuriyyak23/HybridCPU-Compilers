using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    // Member resolution precedes ABI erasure: enum E is not Int32 and OuterA+Point
    // is not OuterB+Point. This key grants no body, layout or lowering authority.
    private sealed class ExactSignatureNames(IReadOnlyList<RestrictedCilGenericArgumentV1> typeArguments)
        : ISignatureTypeProvider<string, object?>
    {
        private int _specDepth;
        public string Method(MetadataReader metadata, BlobHandle blob)
        {
            BlobReader reader = BoundedReader(metadata, blob);
            MethodSignature<string> signature = new SignatureDecoder<string, object?>(this, metadata, null).DecodeMethodSignature(ref reader);
            if (reader.RemainingBytes != 0) throw new BadImageFormatException("Trailing signature bytes.");
            return MethodKey(signature);
        }
        private static BlobReader BoundedReader(MetadataReader metadata, BlobHandle blob)
        {
            BlobReader reader = metadata.GetBlobReader(blob);
            // Also bounds recursive decoder depth; malformed TypeSpec cycles have a
            // separate bound below. Oversized signatures remain explicitly closed.
            if (reader.Length == 0 || reader.Length > 256) throw new BadImageFormatException("Exact signature byte budget.");
            return reader;
        }
        private static string Key(params string[] components) => string.Concat(components.Select(s => s.Length + ":" + s));
        private static string MethodKey(System.Reflection.Metadata.MethodSignature<string> signature) =>
            Key("method", signature.Header.RawValue.ToString(), signature.GenericParameterCount.ToString(),
                signature.RequiredParameterCount.ToString(), signature.ReturnType, Key(signature.ParameterTypes.ToArray()));
        private static string TypePath(MetadataReader metadata, EntityHandle handle, int depth = 0)
        {
            if (depth >= 64 || handle.IsNil) throw new BadImageFormatException("Exact type path budget.");
            if (handle.Kind == HandleKind.TypeDefinition)
            {
                TypeDefinition type = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
                return type.GetDeclaringType().IsNil ? Key("top", metadata.GetString(type.Namespace), metadata.GetString(type.Name)) :
                    Key("nested", TypePath(metadata, type.GetDeclaringType(), depth + 1), metadata.GetString(type.Name));
            }
            TypeReference reference = metadata.GetTypeReference((TypeReferenceHandle)handle);
            return reference.ResolutionScope.Kind != HandleKind.TypeReference ? Key("top", metadata.GetString(reference.Namespace), metadata.GetString(reference.Name)) :
                Key("nested", TypePath(metadata, reference.ResolutionScope, depth + 1), metadata.GetString(reference.Name));
        }
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => Key("primitive", typeCode.ToString());
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            Key("named", rawTypeKind.ToString(), reader.GetString(reader.GetAssemblyDefinition().Name), TypePath(reader, handle));
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            string name = TypePath(reader, handle); // Bound the scope chain first.
            string? assembly = ReferencedAssemblyName(reader, handle);
            if (assembly is null) throw new BadImageFormatException("Unscoped exact signature type.");
            return Key("named", rawTypeKind.ToString(), assembly, name);
        }
        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            if (++_specDepth > 16) throw new BadImageFormatException("Exact TypeSpec recursion budget.");
            try
            {
                BlobReader blob = BoundedReader(reader, reader.GetTypeSpecification(handle).Signature);
                string result = new SignatureDecoder<string, object?>(this, reader, null).DecodeType(ref blob);
                if (blob.RemainingBytes != 0) throw new BadImageFormatException("Trailing TypeSpec bytes.");
                return result;
            }
            finally { _specDepth--; }
        }
        public string GetGenericTypeParameter(object? context, int index) => index < typeArguments.Count
            ? Key("exact-argument", typeArguments[index].StableTypeIdentity) : Key("VAR", index.ToString());
        public string GetGenericMethodParameter(object? context, int index) => Key("MVAR", index.ToString());
        public string GetSZArrayType(string elementType) => Key("SZARRAY", elementType);
        public string GetArrayType(string elementType, ArrayShape shape) =>
            Key("ARRAY", elementType, shape.Rank.ToString(), string.Join(',', shape.Sizes), string.Join(',', shape.LowerBounds));
        public string GetByReferenceType(string elementType) => Key("BYREF", elementType);
        public string GetPointerType(string elementType) => Key("PTR", elementType);
        public string GetPinnedType(string elementType) => Key("PINNED", elementType);
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
            Key("MOD", isRequired.ToString(), modifier, unmodifiedType);
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            Key("GENERICINST", genericType, Key(typeArguments.ToArray()));
        public string GetFunctionPointerType(System.Reflection.Metadata.MethodSignature<string> signature) =>
            Key("FNPTR", MethodKey(signature));
    }
}
