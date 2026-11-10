using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    // Resolves declarations only. In particular, a reference-pack PE is never added to
    // the executable body world. VAR substitution here does not widen the call ABI.
    private sealed class ClosedInterfaceResolver(IReadOnlyList<ManagedModuleContext> modules)
    {
        public Dictionary<string, HybridCpuManagedTypeDescriptorV1> Descriptors { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ManagedClosedInterfacePlanV1> Plans { get; } = new(StringComparer.Ordinal);

        public HybridCpuManagedTypeDescriptorV1? Resolve(ManagedModuleContext source, EntityHandle handle)
        {
            try
            {
                if (handle.Kind != HandleKind.TypeSpecification || Descriptors.Count >= 4096) return null;
                BlobHandle signature = source.Metadata.GetTypeSpecification((TypeSpecificationHandle)handle).Signature;
                BlobReader reader = source.Metadata.GetBlobReader(signature);
                if (reader.ReadByte() != 0x15 || reader.ReadByte() != 0x12 ||
                    !ResolveNamed(source, ReadTypeDefOrRefEncoded(ref reader), out ManagedModuleContext? definitionModule, out TypeDefinitionHandle definition)) return null;
                MetadataReader metadata = definitionModule.Metadata;
                TypeDefinition type = metadata.GetTypeDefinition(definition);
                int count = reader.ReadCompressedInteger();
                if (count < 1 || count > 16 || type.GetGenericParameters().Count != count ||
                    !type.Attributes.HasFlag(TypeAttributes.Interface) || !type.BaseType.IsNil ||
                    type.GetFields().Count != 0 || type.GetInterfaceImplementations().Count != 0) return null;
                // Variance is recorded by the definition digest, not used as an assignability rule.
                // Constraint checking and inherited generic interfaces need their own qualification.
                foreach (GenericParameterHandle parameter in type.GetGenericParameters())
                {
                    GenericParameter row = metadata.GetGenericParameter(parameter);
                    // .NET 10 IComparable<T> allows ref structs; this is permission, not
                    // a requirement. We still reject every byref-like argument below.
                    const GenericParameterAttributes admitted = GenericParameterAttributes.VarianceMask | GenericParameterAttributes.AllowByRefLike;
                    if ((row.Attributes & ~admitted) != 0 ||
                        (row.Attributes & GenericParameterAttributes.VarianceMask) == GenericParameterAttributes.VarianceMask ||
                        row.GetConstraints().Count != 0) return null;
                }
                string[] arguments = new string[count];
                for (int i = 0; i < count; i++) arguments[i] = ReadExactType(source, ref reader, [], false);
                if (reader.RemainingBytes != 0) return null;
                string identity = $"[{definitionModule.AssemblyName}]{FullTypeName(metadata, definition)}<{string.Join(',', arguments)}>";
                if (Descriptors.TryGetValue(identity, out HybridCpuManagedTypeDescriptorV1? existing)) return existing;
                List<string> declarations = new List<string>();
                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                    const MethodAttributes required = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Abstract | MethodAttributes.NewSlot;
                    if ((method.Attributes & required) != required ||
                        (method.Attributes & (MethodAttributes.Static | MethodAttributes.PinvokeImpl)) != 0 ||
                        method.RelativeVirtualAddress != 0 || method.GetGenericParameters().Count != 0 || declarations.Count >= 256) return null;
                    BlobReader body = metadata.GetBlobReader(method.Signature);
                    if (body.ReadByte() != 0x20) return null;
                    int arity = body.ReadCompressedInteger();
                    if (arity < 0 || arity > 64) return null;
                    string returns = ReadExactType(definitionModule, ref body, arguments, true);
                    string[] parameters = new string[arity];
                    for (int i = 0; i < arity; i++) parameters[i] = ReadExactType(definitionModule, ref body, arguments, false);
                    if (body.RemainingBytes != 0) return null;
                    declarations.Add($"{identity}::{metadata.GetString(method.Name)}instance({string.Join(',', parameters)}):{returns}");
                }
                if (declarations.Distinct(StringComparer.Ordinal).Count() != declarations.Count) return null;
                string[] ordered = declarations.Order(StringComparer.Ordinal).ToArray();
                ManagedClosedInterfacePlanV1 plan = new ManagedClosedInterfacePlanV1(identity, definitionModule.ImageDigest,
                    MetadataTokens.GetToken(definition), arguments, ordered,
                    Hash($"hybridcpu.closed-interface-declarations/v1|{identity}|{definitionModule.ImageDigest}|{MetadataTokens.GetToken(definition)}|{string.Join('|', ordered)}"));
                HybridCpuManagedTypeDescriptorV1 draft = new HybridCpuManagedTypeDescriptorV1(
                    HybridCpuManagedTypeDescriptorContractV1.SchemaId, HybridCpuManagedTypeDescriptorContractV1.SchemaMajor,
                    HybridCpuManagedTypeDescriptorContractV1.SchemaMinor, MetadataTypeId(identity), identity,
                    HybridCpuManagedTypeKindV1.Interface, null, [], 0, 1, [], new(0, 1, [], []), [], [], string.Empty);
                HybridCpuManagedTypeDescriptorV1 descriptor = draft with { DescriptorDigest = HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(draft) };
                Descriptors.Add(identity, descriptor);
                Plans.Add(identity, plan);
                return descriptor;
            }
            catch (Exception e) when (e is BadImageFormatException or ArgumentOutOfRangeException or NotSupportedException)
            { return null; }
        }

        private string ReadExactType(ManagedModuleContext source, ref BlobReader reader, IReadOnlyList<string> arguments, bool allowVoid)
        {
            byte code = reader.ReadByte();
            if (code == 0x13)
            {
                int index = reader.ReadCompressedInteger();
                if ((uint)index >= arguments.Count) throw new BadImageFormatException("Unbound type parameter.");
                return arguments[index];
            }
            if (code is 0x11 or 0x12)
            {
                if (!ResolveNamed(source, ReadTypeDefOrRefEncoded(ref reader), out ManagedModuleContext? module, out TypeDefinitionHandle handle))
                    throw new NotSupportedException("Exact type definition is missing.");
                TypeDefinition definition = module.Metadata.GetTypeDefinition(handle);
                bool value = ExactTypeHandleName(module.Metadata, definition.BaseType) is "System.ValueType" or "System.Enum";
                if (value != (code == 0x11) || definition.GetGenericParameters().Count != 0)
                    throw new BadImageFormatException("Type kind mismatch or open argument.");
                if (definition.GetCustomAttributes().Any(attribute => DescribeMethodReference(module.Metadata,
                    MetadataTokens.GetToken(module.Metadata.GetCustomAttribute(attribute).Constructor)) ==
                    "System.Runtime.CompilerServices.IsByRefLikeAttribute..ctor"))
                    throw new NotSupportedException("Byref-like interface arguments are not qualified.");
                string name = FullTypeName(module.Metadata, handle);
                if (module.AssemblyName == "System.Private.CoreLib" && name is "System.Boolean" or "System.Char" or
                    "System.SByte" or "System.Byte" or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" or
                    "System.Int64" or "System.UInt64" or "System.Single" or "System.Double" or "System.String" or
                    "System.IntPtr" or "System.UIntPtr" or "System.Object") return name;
                return $"[{module.AssemblyName}]{name}";
            }
            // Primitive encodings are ECMA well-known identities, not name-based type aliases.
            return code switch
            {
                0x01 when allowVoid => "System.Void", 0x02 => "System.Boolean", 0x03 => "System.Char",
                0x04 => "System.SByte", 0x05 => "System.Byte", 0x06 => "System.Int16", 0x07 => "System.UInt16",
                0x08 => "System.Int32", 0x09 => "System.UInt32", 0x0a => "System.Int64", 0x0b => "System.UInt64",
                0x0c => "System.Single", 0x0d => "System.Double", 0x0e => "System.String",
                0x18 => "System.IntPtr", 0x19 => "System.UIntPtr", 0x1c => "System.Object",
                _ => throw new NotSupportedException("Open, byref, modified, array or nested generic declaration type.")
            };
        }

        public bool ResolveNamed(ManagedModuleContext source, EntityHandle handle,
            out ManagedModuleContext module, out TypeDefinitionHandle definition)
        {
            module = source; definition = default;
            if (handle.Kind == HandleKind.TypeDefinition) { definition = (TypeDefinitionHandle)handle; return true; }
            if (handle.Kind != HandleKind.TypeReference) return false;
            TypeReferenceHandle reference = (TypeReferenceHandle)handle;
            string identity = TypeReferenceName(source.Metadata, reference);
            string? assembly = ReferencedAssemblyName(source.Metadata, reference);
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            while (assembly is not null && visited.Count < 16 && visited.Add(assembly))
            {
                ManagedModuleContext[] candidates = modules.Where(m => m.AssemblyName == assembly).ToArray();
                if (candidates.Length != 1) return false;
                module = candidates[0];
                MetadataReader metadata = module.Metadata;
                TypeDefinitionHandle[] definitions = metadata.TypeDefinitions.Where(h => FullTypeName(metadata, h) == identity).ToArray();
                if (definitions.Length == 1) { definition = definitions[0]; return true; }
                if (definitions.Length != 0) return false;
                ExportedType[] exports = metadata.ExportedTypes.Select(metadata.GetExportedType).Where(row =>
                    (string.IsNullOrEmpty(metadata.GetString(row.Namespace)) ? "" : metadata.GetString(row.Namespace) + ".") +
                    metadata.GetString(row.Name) == identity).ToArray();
                if (exports.Length != 1 || !exports[0].IsForwarder || exports[0].Implementation.Kind != HandleKind.AssemblyReference) return false;
                assembly = module.Metadata.GetString(module.Metadata.GetAssemblyReference((AssemblyReferenceHandle)exports[0].Implementation).Name);
            }
            return false;
        }
    }

    private static IReadOnlyList<ManagedClosedInterfacePlanV1> RequiredClosedInterfacePlans(
        ManagedMetadataBindingSet metadata, IEnumerable<string> owners)
    {
        Dictionary<ulong, HybridCpuManagedTypeDescriptorV1> byId = metadata.Descriptors.Values.ToDictionary(d => d.TypeId);
        HashSet<ulong> needed = new HashSet<ulong>();
        void Visit(ulong id)
        {
            if (!needed.Add(id) || !byId.TryGetValue(id, out HybridCpuManagedTypeDescriptorV1? descriptor)) return;
            if (descriptor.BaseTypeId is ulong parent) Visit(parent);
            foreach (ulong child in descriptor.InterfaceTypeIds) Visit(child);
        }
        foreach (string owner in owners.Distinct(StringComparer.Ordinal))
            if (metadata.Descriptors.TryGetValue(owner, out HybridCpuManagedTypeDescriptorV1? descriptor)) Visit(descriptor.TypeId);
        return metadata.ClosedInterfaces.Where(plan => needed.Contains(MetadataTypeId(plan.StableIdentity))).ToArray();
    }

    private static string MetadataFieldOwner(MetadataReader metadata, int token)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.FieldDefinition)
            return FullTypeName(metadata, metadata.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType());
        if (handle.Kind == HandleKind.MemberReference)
            return ExactTypeHandleName(metadata, metadata.GetMemberReference((MemberReferenceHandle)handle).Parent);
        return string.Empty;
    }
}
