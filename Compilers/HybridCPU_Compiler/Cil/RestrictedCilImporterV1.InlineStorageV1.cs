using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    private sealed record MetadataStorage(HybridCpuManagedStorageKindV1 Kind, int Size, int Alignment);

    // Storage is independent of methods, interfaces and static initialization. In particular,
    // Fixed.Zero must not recursively require Fixed's own static layout to size its payload.
    // Reference-containing aggregates need a nested GC map contract; never mark them blittable.
    private sealed class InlineStorageResolver(IReadOnlyList<ManagedModuleContext> modules)
    {
        private readonly Dictionary<(string, int), MetadataStorage> _cache = new();
        private readonly HashSet<(string, int)> _active = new();

        public MetadataStorage? Field(ManagedModuleContext module, BlobHandle signature) => Field(module.Metadata, signature);

        public MetadataStorage? Field(MetadataReader metadata, BlobHandle signature)
        {
            BlobReader reader = metadata.GetBlobReader(signature);
            if (reader.RemainingBytes < 2 || reader.ReadByte() != 0x06) return null;
            if (reader.ReadByte() == 0x11)
            {
                EntityHandle handle = ReadTypeDefOrRefEncoded(ref reader);
                if (reader.RemainingBytes != 0 || handle.IsNil) return null;
                if (handle.Kind == HandleKind.TypeDefinition) return Value(metadata, (TypeDefinitionHandle)handle);
                if (handle.Kind != HandleKind.TypeReference) return null;
                TypeReferenceHandle reference = (TypeReferenceHandle)handle;
                string? assembly = ReferencedAssemblyName(metadata, reference);
                string identity = TypeReferenceName(metadata, reference);
                (MetadataReader Metadata, TypeDefinitionHandle Type)[] matches = modules.Where(module => module.AssemblyName == assembly)
                    .SelectMany(module => module.Metadata.TypeDefinitions.Where(type => FullTypeName(module.Metadata, type) == identity)
                        .Select(type => (module.Metadata, Type: type))).ToArray();
                return matches.Length == 1 ? Value(matches[0].Metadata, matches[0].Type) : null;
            }
            return TryStorage(ParseMetadataFieldType(metadata, signature), out HybridCpuManagedStorageKindV1 kind, out int size, out int alignment)
                ? new(kind, size, alignment) : null;
        }

        public MetadataStorage? Value(MetadataReader metadata, TypeDefinitionHandle handle)
        {
            (string, int) key = (metadata.GetString(metadata.GetAssemblyDefinition().Name), MetadataTokens.GetToken(handle));
            if (_cache.TryGetValue(key, out MetadataStorage? cached)) return cached;
            if (_active.Count >= 128 || !_active.Add(key)) return null;
            try
            {
                TypeDefinition type = metadata.GetTypeDefinition(handle);
                string parent = ExactTypeHandleName(metadata, type.BaseType);
                bool isEnum = parent == "System.Enum";
                if ((!isEnum && parent != "System.ValueType") || type.GetGenericParameters().Count != 0 ||
                    (!isEnum && (type.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.SequentialLayout) ||
                    !type.GetLayout().IsDefault) return null;
                int extent = 0, alignment = 1, count = 0;
                MetadataStorage? enumStorage = null;
                foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
                {
                    FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
                    if ((field.Attributes & (FieldAttributes.Static | FieldAttributes.Literal)) != 0) continue;
                    if (++count > 8192) return null;
                    MetadataStorage? storage = Field(metadata, field.Signature);
                    if (storage is null || storage.Kind == HybridCpuManagedStorageKindV1.ObjectReference) return null;
                    if (isEnum && (metadata.GetString(field.Name) != "value__" ||
                        storage.Kind != HybridCpuManagedStorageKindV1.Primitive)) return null;
                    enumStorage = storage;
                    extent = checked(AlignMetadata(extent, storage.Alignment) + storage.Size);
                    if (extent > 1_048_576) return null;
                    alignment = Math.Max(alignment, storage.Alignment);
                }
                if (isEnum && count != 1) return null;
                MetadataStorage result = isEnum ? enumStorage! : new MetadataStorage(
                    HybridCpuManagedStorageKindV1.BlittableValue, AlignMetadata(Math.Max(1, extent), alignment), alignment);
                _cache.Add(key, result);
                return result;
            }
            finally { _active.Remove(key); }
        }
    }
}
