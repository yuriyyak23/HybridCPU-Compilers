using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    // Null StorageKind means an aggregate containing GC slots, never blittable data.
    private sealed record ManagedFieldStorage(int Size, int Alignment, HybridCpuManagedStorageKindV1? StorageKind,
        IReadOnlyList<int> ObjectReferenceOffsets, string TypeIdentity);

    private sealed class ManagedStorageResolver(IReadOnlyList<ManagedModuleContext> modules)
    {
        private sealed record StorageType(string Identity, bool IsValue, ManagedModuleContext? Module,
            TypeDefinitionHandle Definition, IReadOnlyList<StorageType> Arguments, ManagedFieldStorage? Intrinsic = null);
        private readonly ClosedInterfaceResolver _names = new(modules);
        private readonly Dictionary<string, ManagedFieldStorage> _layouts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _active = new(StringComparer.Ordinal);

        public ManagedFieldStorage? Field(ManagedModuleContext module, BlobHandle signature)
        {
            try
            {
                BlobReader reader = module.Metadata.GetBlobReader(signature);
                if (reader.ReadByte() != 0x06) return null;
                StorageType type = Read(module, ref reader, [], 0);
                return reader.RemainingBytes == 0 ? Layout(type) : null;
            }
            catch (Exception e) when (e is BadImageFormatException or ArgumentOutOfRangeException or NotSupportedException or OverflowException)
            { return null; }
        }

        private StorageType Read(ManagedModuleContext module, ref BlobReader reader, IReadOnlyList<StorageType> arguments, int depth)
        {
            if (depth >= 64) throw new NotSupportedException("Storage signature nesting budget.");
            byte code = reader.ReadByte();
            if (code == 0x13)
            {
                int index = reader.ReadCompressedInteger();
                if ((uint)index >= arguments.Count) throw new NotSupportedException("Unbound storage type parameter.");
                return arguments[index];
            }
            if (code is 0x11 or 0x12 or 0x15)
            {
                byte kind = code == 0x15 ? reader.ReadByte() : code;
                if (kind is not (0x11 or 0x12)) throw new BadImageFormatException("Invalid generic storage kind.");
                EntityHandle handle = ReadTypeDefOrRefEncoded(ref reader);
                if (handle.Kind is not (HandleKind.TypeDefinition or HandleKind.TypeReference))
                    throw new NotSupportedException("Storage requires a named exact definition.");
                ManagedModuleContext? definitionModule = null;
                TypeDefinitionHandle definition = default;
                string name;
                if (_names.ResolveNamed(module, handle, out ManagedModuleContext? resolved, out TypeDefinitionHandle resolvedType))
                {
                    definitionModule = resolved; definition = resolvedType;
                    TypeDefinition type = resolved.Metadata.GetTypeDefinition(definition);
                    bool value = !type.BaseType.IsNil &&
                        ExactTypeHandleName(resolved.Metadata, type.BaseType) is "System.ValueType" or "System.Enum";
                    if (value != (kind == 0x11) || type.GetCustomAttributes().Any(attribute => DescribeMethodReference(resolved.Metadata,
                        MetadataTokens.GetToken(resolved.Metadata.GetCustomAttribute(attribute).Constructor)) ==
                        "System.Runtime.CompilerServices.IsByRefLikeAttribute..ctor"))
                        throw new NotSupportedException("Storage type kind mismatch or byref-like type.");
                    string definitionName = StorageDefinitionName(resolved.Metadata, definition);
                    if (handle.Kind == HandleKind.TypeReference && StorageReferenceName(module.Metadata, (TypeReferenceHandle)handle) != definitionName)
                        throw new NotSupportedException("Nested storage type resolution is not exact.");
                    name = $"[{resolved.AssemblyName}]{definitionName}";
                }
                else
                {
                    if (handle.Kind != HandleKind.TypeReference) throw new NotSupportedException("Unresolved storage definition.");
                    string? assembly = ReferencedAssemblyName(module.Metadata, (TypeReferenceHandle)handle);
                    if (assembly is null) throw new NotSupportedException("Unscoped storage reference.");
                    name = $"[{assembly}]{StorageReferenceName(module.Metadata, (TypeReferenceHandle)handle)}";
                }
                List<StorageType> instantiation = new List<StorageType>();
                if (code == 0x15)
                {
                    int count = reader.ReadCompressedInteger();
                    if (count < 1 || count > 32) throw new NotSupportedException("Storage generic arity budget.");
                    for (int i = 0; i < count; i++) instantiation.Add(Read(module, ref reader, arguments, depth + 1));
                }
                if (definitionModule is not null && definitionModule.Metadata.GetTypeDefinition(definition).GetGenericParameters().Count != instantiation.Count)
                    throw new BadImageFormatException("Storage generic arity mismatch.");
                return new(name + (instantiation.Count == 0 ? "" : "<" + string.Join(',', instantiation.Select(a => a.Identity)) + ">"),
                    kind == 0x11, definitionModule, definition, instantiation);
            }
            if (code == 0x1d)
            {
                // Node[] has a reference carrier: do not recursively size Node's payload.
                StorageType element = Read(module, ref reader, arguments, depth + 1);
                return Reference(element.Identity + "[]");
            }
            if (code is 0x0e or 0x1c) return Reference(code == 0x0e ? "System.String" : "System.Object");
            int size = code switch
            {
                0x02 or 0x04 or 0x05 => 1, 0x03 or 0x06 or 0x07 => 2,
                0x08 or 0x09 or 0x0c => 4, 0x0a or 0x0b or 0x0d or 0x18 or 0x19 => 8,
                _ => throw new NotSupportedException("Byref, pointer, open or extended storage signature.")
            };
            string primitive = "ecma:" + code.ToString("x2");
            return new(primitive, true, null, default, [], new(size, size, HybridCpuManagedStorageKindV1.Primitive, [], primitive));
        }

        private static StorageType Reference(string identity) => new(identity, false, null, default, [],
            new(8, 8, HybridCpuManagedStorageKindV1.ObjectReference, [0], identity));

        private static string StorageDefinitionName(MetadataReader metadata, TypeDefinitionHandle handle, int depth = 0)
        {
            if (depth >= 64) throw new NotSupportedException("Nested definition identity budget.");
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            return type.GetDeclaringType().IsNil ? FullTypeName(metadata, handle) :
                StorageDefinitionName(metadata, type.GetDeclaringType(), depth + 1) + "+" + metadata.GetString(type.Name);
        }

        private static string StorageReferenceName(MetadataReader metadata, TypeReferenceHandle handle, int depth = 0)
        {
            if (depth >= 64) throw new NotSupportedException("Nested reference identity budget.");
            TypeReference type = metadata.GetTypeReference(handle);
            return type.ResolutionScope.Kind != HandleKind.TypeReference ? TypeReferenceName(metadata, handle) :
                StorageReferenceName(metadata, (TypeReferenceHandle)type.ResolutionScope, depth + 1) + "+" + metadata.GetString(type.Name);
        }

        private ManagedFieldStorage? Layout(StorageType node)
        {
            if (node.Intrinsic is not null) return node.Intrinsic;
            if (!node.IsValue) return new(8, 8, HybridCpuManagedStorageKindV1.ObjectReference, [0], node.Identity);
            if (node.Module is null) return null;
            if (_layouts.TryGetValue(node.Identity, out ManagedFieldStorage? cached)) return cached;
            if (_active.Count >= 128 || _layouts.Count >= 8192 || !_active.Add(node.Identity)) return null;
            try
            {
                MetadataReader metadata = node.Module.Metadata;
                TypeDefinition type = metadata.GetTypeDefinition(node.Definition);
                bool isEnum = ExactTypeHandleName(metadata, type.BaseType) == "System.Enum";
                if ((!isEnum && (type.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.SequentialLayout) ||
                    !type.GetLayout().IsDefault || !ValidArguments(node, type)) return null;
                int extent = 0, alignment = 1, count = 0;
                List<int> references = new List<int>();
                ManagedFieldStorage? enumValue = null;
                foreach (FieldDefinitionHandle handle in type.GetFields())
                {
                    FieldDefinition field = metadata.GetFieldDefinition(handle);
                    if ((field.Attributes & (FieldAttributes.Static | FieldAttributes.Literal)) != 0) continue;
                    if (++count > HybridCpuPlatformContractV1.MaximumManagedFieldsPerType) return null;
                    BlobReader reader = metadata.GetBlobReader(field.Signature);
                    if (reader.ReadByte() != 0x06) return null;
                    StorageType fieldType = Read(node.Module, ref reader, node.Arguments, 0);
                    if (reader.RemainingBytes != 0) return null;
                    ManagedFieldStorage? storage = Layout(fieldType);
                    if (storage is null) return null;
                    if (isEnum && (metadata.GetString(field.Name) != "value__" || storage.StorageKind != HybridCpuManagedStorageKindV1.Primitive)) return null;
                    enumValue = storage;
                    extent = AlignMetadata(extent, storage.Alignment);
                    references.AddRange(storage.ObjectReferenceOffsets.Select(offset => checked(extent + offset)));
                    extent = checked(extent + storage.Size);
                    if (extent > 1_048_576 || references.Count > HybridCpuPlatformContractV1.MaximumManagedFieldsPerType) return null;
                    alignment = Math.Max(alignment, storage.Alignment);
                }
                if (isEnum && count != 1) return null;
                ManagedFieldStorage layout = isEnum ? enumValue! : new ManagedFieldStorage(AlignMetadata(Math.Max(1, extent), alignment), alignment,
                    references.Count == 0 ? HybridCpuManagedStorageKindV1.BlittableValue : null, references.ToArray(), node.Identity);
                _layouts.Add(node.Identity, layout);
                return layout;
            }
            finally { _active.Remove(node.Identity); }
        }

        private bool ValidArguments(StorageType node, TypeDefinition type)
        {
            if (type.GetGenericParameters().Count != node.Arguments.Count) return false;
            foreach (GenericParameterHandle handle in type.GetGenericParameters())
            {
                GenericParameter parameter = node.Module!.Metadata.GetGenericParameter(handle);
                if ((uint)parameter.Index >= node.Arguments.Count) return false;
                StorageType argument = node.Arguments[parameter.Index];
                GenericParameterAttributes flags = parameter.Attributes;
                const GenericParameterAttributes allowed = GenericParameterAttributes.ReferenceTypeConstraint |
                    GenericParameterAttributes.NotNullableValueTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint;
                if ((flags & ~allowed) != 0) return false;
                if (flags.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint) && argument.IsValue) return false;
                if (flags.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint) &&
                    (!argument.IsValue || argument.Identity.StartsWith("[System.Private.CoreLib]System.Nullable`1<", StringComparison.Ordinal) ||
                        argument.Intrinsic is null && argument.Module is null)) return false;
                // Reference-type default constructors/interface constraints need a fuller type verifier.
                if (flags.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint) && !argument.IsValue) return false;
                foreach (GenericParameterConstraintHandle constraintHandle in parameter.GetConstraints())
                {
                    GenericParameterConstraint constraint = node.Module.Metadata.GetGenericParameterConstraint(constraintHandle);
                    if (!_names.ResolveNamed(node.Module, constraint.Type, out ManagedModuleContext? module, out TypeDefinitionHandle definition) ||
                        module.AssemblyName != "System.Private.CoreLib" || FullTypeName(module.Metadata, definition) != "System.ValueType" ||
                        !argument.IsValue) return false;
                }
            }
            return true;
        }
    }

    private static void AppendPhysicalStorage(List<HybridCpuManagedFieldLayoutV1> fields, string identity,
        ManagedFieldStorage storage, int offset, int ordinal)
    {
        if (storage.StorageKind is { } kind)
        {
            fields.Add(new(identity, kind, offset, storage.Size, storage.Alignment));
            return;
        }
        int cursor = 0;
        foreach (int reference in storage.ObjectReferenceOffsets)
        {
            if (reference < cursor || reference % 8 != 0 || reference > storage.Size - 8)
                throw new BadImageFormatException("Nested GC slot extent/alignment is invalid.");
            if (reference > cursor)
                fields.Add(new($"@physical:{ordinal}:{cursor}:bytes", HybridCpuManagedStorageKindV1.BlittableValue,
                    offset + cursor, reference - cursor, cursor == 0 ? storage.Alignment : 1));
            fields.Add(new($"@physical:{ordinal}:{reference}:ref", HybridCpuManagedStorageKindV1.ObjectReference,
                offset + reference, 8, 8));
            cursor = reference + 8;
        }
        if (cursor < storage.Size)
            fields.Add(new($"@physical:{ordinal}:{cursor}:bytes", HybridCpuManagedStorageKindV1.BlittableValue,
                offset + cursor, storage.Size - cursor, cursor == 0 ? storage.Alignment : 1));
    }
}
