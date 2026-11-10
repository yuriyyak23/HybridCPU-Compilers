using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    private static RestrictedCilDispatchBindingV1 BindPrimitiveInterfaceIds(
        ManagedModuleContext source, int token, ManagedMetadataBindingSet metadata, IReadOnlyList<ManagedModuleContext> modules,
        RestrictedCilDispatchBindingV1 binding, IReadOnlyList<RestrictedCilDispatchCandidateV1> candidates)
    {
        // Only exact token-free MethodDef discovery (including STRING and SZARRAY<U1>) admits this identity rule.
        // This binds image-owned implementation symbols, not publication/execution authority.
        if (candidates.Count == 0 || MetadataTokens.EntityHandle(token).Kind != HandleKind.MethodDefinition)
            return binding;
        MethodDefinition method = source.Metadata.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.EntityHandle(token));
        string owner = FullTypeName(source.Metadata,
            FindDeclaringTypeHandle(source.Metadata, (MethodDefinitionHandle)MetadataTokens.EntityHandle(token)));
        if (!metadata.Descriptors.TryGetValue(owner, out HybridCpuManagedTypeDescriptorV1? descriptor) ||
            descriptor.Kind != HybridCPU.Platform.Contracts.HybridCpuManagedTypeKindV1.Interface)
            return binding;
        foreach (RestrictedCilDispatchCandidateV1 candidate in candidates)
        {
            ManagedModuleContext? module = modules.SingleOrDefault(module => module.AssemblyName == candidate.AssemblyName);
            if (module is null) return binding;
            string type = candidate.RuntimeTypeIdentity ?? FullTypeName(module.Metadata, FindDeclaringTypeHandle(module.Metadata,
                (MethodDefinitionHandle)MetadataTokens.EntityHandle(candidate.MethodMetadataToken)));
            if (!metadata.Descriptors.TryGetValue(type, out HybridCpuManagedTypeDescriptorV1? receiver) ||
                receiver.Kind != HybridCPU.Platform.Contracts.HybridCpuManagedTypeKindV1.Class ||
                !receiver.InterfaceTypeIds.Contains(descriptor.TypeId)) return binding;
        }
        byte[] signature = source.Metadata.GetBlobBytes(method.Signature);
        if (!TryTokenFreeInterfaceSignature(signature, out string returnType, out string[] parameterTypes)) return binding;
        string declaration = owner + "." + source.Metadata.GetString(method.Name);
        string canonical = $"instance({string.Join(',', parameterTypes)}):{returnType}";
        ulong slot = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"hybridcpu.managed-slot-id/v1|{descriptor.TypeId}|{declaration}|{canonical}")));
        return binding with { InterfaceTypeId = descriptor.TypeId, SlotId = slot == 0 ? 1 : slot,
            ExactGenericCandidates = candidates, RuntimeExternal = false };
    }

    // Candidate reachability only. This does not turn metadata row numbers into runtime
    // TypeIds/slot IDs or authorize a dispatch table. Unsupported shapes retain the gate.
    private static IReadOnlyList<RestrictedCilDispatchCandidateV1> FindPrimitiveInterfaceCandidates(
        IReadOnlyList<ManagedModuleContext> modules, IReadOnlyList<ManagedModuleContext> metadataModules,
        ManagedModuleContext source, int token)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind != HandleKind.MethodDefinition) return [];
        MethodDefinition declaration = source.Metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
        TypeDefinitionHandle owner = FindDeclaringTypeHandle(source.Metadata, (MethodDefinitionHandle)handle);
        TypeDefinition iface = source.Metadata.GetTypeDefinition(owner);
        if (!iface.Attributes.HasFlag(TypeAttributes.Interface) || iface.GetGenericParameters().Count != 0 ||
            declaration.GetGenericParameters().Count != 0) return [];
        byte[] signature = source.Metadata.GetBlobBytes(declaration.Signature);
        // Token-free built-ins and SZARRAY<U1> compare identically across module scopes.
        // STRING/byte[] remain distinct from OBJECT and from every other SZARRAY element type.
        if (!TryTokenFreeInterfaceSignature(signature, out _, out _)) return [];
        string name = source.Metadata.GetString(declaration.Name);
        bool hasDefaultBody = declaration.RelativeVirtualAddress != 0 &&
            !declaration.Attributes.HasFlag(MethodAttributes.Abstract) &&
            declaration.Attributes.HasFlag(MethodAttributes.Public) &&
            declaration.Attributes.HasFlag(MethodAttributes.Virtual) &&
            declaration.Attributes.HasFlag(MethodAttributes.NewSlot) &&
            !declaration.Attributes.HasFlag(MethodAttributes.Static) &&
            !declaration.Attributes.HasFlag(MethodAttributes.PinvokeImpl);
        // Any derived interface (including metadata-only definitions) opens an inherited
        // implementation path not covered by this direct leaf-class rule. Reject the
        // entire candidate set, even if direct implementations were also discovered.
        foreach (ManagedModuleContext module in metadataModules)
            foreach (TypeDefinitionHandle definition in module.Metadata.TypeDefinitions)
            {
                TypeDefinition derived = module.Metadata.GetTypeDefinition(definition);
                if (!derived.Attributes.HasFlag(TypeAttributes.Interface)) continue;
                if (derived.GetInterfaceImplementations().Any(implementation =>
                    TryResolveType(metadataModules, module, module.Metadata.GetInterfaceImplementation(implementation).Interface,
                        out ManagedModuleContext? parentModule, out TypeDefinitionHandle parentType) && parentModule.AssemblyName == source.AssemblyName && parentType == owner))
                    return [];
            }
        List<RestrictedCilDispatchCandidateV1> candidates = new List<RestrictedCilDispatchCandidateV1>();
        foreach (ManagedModuleContext module in modules.OrderBy(static item => item.AssemblyName, StringComparer.Ordinal))
            foreach (TypeDefinitionHandle typeHandle in module.Metadata.TypeDefinitions)
            {
                TypeDefinition type = module.Metadata.GetTypeDefinition(typeHandle);
                bool implements = type.GetInterfaceImplementations().Any(implementation =>
                    TryResolveType(metadataModules, module, module.Metadata.GetInterfaceImplementation(implementation).Interface,
                        out ManagedModuleContext? interfaceModule, out TypeDefinitionHandle interfaceType) &&
                    interfaceModule.AssemblyName == source.AssemblyName && interfaceType == owner);
                if (!implements) continue;
                // Never publish a partial candidate set when one implementor needs a wider rule.
                // A non-sealed definition is admitted only as a proven leaf of this closed
                // image/metadata world. Any descendant needs inherited/override mapping.
                bool hasDescendant = !type.Attributes.HasFlag(TypeAttributes.Sealed) && metadataModules.Any(other =>
                    other.Metadata.TypeDefinitions.Any(child =>
                    {
                        EntityHandle baseHandle = other.Metadata.GetTypeDefinition(child).BaseType;
                        return !baseHandle.IsNil && TryResolveType(metadataModules, other, baseHandle,
                            out ManagedModuleContext? parentModule, out TypeDefinitionHandle parent) && parentModule.AssemblyName == module.AssemblyName && parent == typeHandle;
                    }));
                if (hasDescendant || type.Attributes.HasFlag(TypeAttributes.Abstract) ||
                    type.GetGenericParameters().Count != 0 || type.GetMethodImplementations().Count != 0 ||
                    !TryResolveType(metadataModules, module, type.BaseType, out ManagedModuleContext? baseModule, out TypeDefinitionHandle baseType) ||
                    FullTypeName(baseModule.Metadata, baseType) != "System.Object") return [];
                MethodDefinitionHandle[] matches = type.GetMethods().Where(methodHandle =>
                {
                    MethodDefinition method = module.Metadata.GetMethodDefinition(methodHandle);
                    return module.Metadata.GetString(method.Name) == name &&
                        (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public &&
                        !method.Attributes.HasFlag(MethodAttributes.Static) &&
                        !method.Attributes.HasFlag(MethodAttributes.Abstract) && method.RelativeVirtualAddress != 0 &&
                        method.GetGenericParameters().Count == 0 &&
                        module.Metadata.GetBlobBytes(method.Signature).AsSpan().SequenceEqual(signature);
                }).ToArray();
                if (matches.Length > 1 || (matches.Length == 0 && !hasDefaultBody)) return [];
                string runtimeType = FullTypeName(module.Metadata, typeHandle);
                candidates.Add(matches.Length == 1
                    ? new(MetadataTokens.GetToken(matches[0]), [], [], module.AssemblyName, runtimeType)
                    : new(token, [], [], source.AssemblyName, runtimeType));
            }
        return candidates;
    }

    private static bool TryTokenFreeInterfaceSignature(byte[] signature,out string returnType,out string[] parameterTypes)
    {
        returnType=string.Empty;parameterTypes=[];
        if(signature.Length<3||signature[0]!=0x20||signature[1]>=0x80)return false;
        int offset=2;
        bool Type(out string identity)
        {
            identity=string.Empty;if(offset>=signature.Length)return false;byte code=signature[offset++];
            identity=code switch {0x01=>"System.Void",0x02=>"System.Boolean",0x03=>"System.Char",0x04=>"System.SByte",
                0x05=>"System.Byte",0x06=>"System.Int16",0x07=>"System.UInt16",0x08=>"System.Int32",
                0x09=>"System.UInt32",0x0a=>"System.Int64",0x0b=>"System.UInt64",0x0e=>"System.String",
                0x18=>"System.IntPtr",0x19=>"System.UIntPtr",_=>string.Empty};
            if(identity.Length!=0)return true;
            if(code==0x1d&&offset<signature.Length&&signature[offset++]==0x05){identity="System.Byte[]";return true;}
            return false;
        }
        if(!Type(out returnType))return false;
        string[] parameters =new string[signature[1]];
        for(int index=0;index<parameters.Length;index++)if(!Type(out parameters[index]))return false;
        if(offset!=signature.Length)return false;
        parameterTypes=parameters;return true;
    }
}
