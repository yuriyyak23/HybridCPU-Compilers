using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    private static bool TryBindClosedVirtual(IReadOnlyList<ManagedModuleContext> modules,
        ManagedModuleContext caller, int token, int maximumResolutionSteps, ref int resolutionSteps,
        out RestrictedCilDispatchBindingV1 binding, out string declaringType)
    {
        binding = null!;
        declaringType = string.Empty;
        if (!TryResolveManagedDefinition(modules, caller, token, null, null,
            out ManagedModuleContext? declarationModule, out TypeDefinitionHandle declarationType, out MethodDefinitionHandle declarationMethod, out _, out _, out _)) return false;
        MethodDefinition declaration = declarationModule.Metadata.GetMethodDefinition(declarationMethod);
        TypeDefinition owner = declarationModule.Metadata.GetTypeDefinition(declarationType);
        if (owner.Attributes.HasFlag(TypeAttributes.Interface) || owner.GetGenericParameters().Count != 0 ||
            !declaration.Attributes.HasFlag(MethodAttributes.Virtual) || declaration.Attributes.HasFlag(MethodAttributes.Static) ||
            declaration.GetGenericParameters().Count != 0) return false;
        MethodSignature signature = ParseMethodSignature(declarationModule.Metadata, declaration.Signature);
        if (signature.Status != SignatureStatus.Success || !signature.HasThis) return false;
        string name = declarationModule.Metadata.GetString(declaration.Name);
        // Metadata tokens are local to each module. Match nominal assembly/type
        // identities before ABI erasure, through the same bounded decoder as calls.
        ExactSignatureNames signatures = new ExactSignatureNames([]);
        string exactSignature = signatures.Method(declarationModule.Metadata, declaration.Signature);
        List<RestrictedCilDispatchCandidateV1> candidates = new List<RestrictedCilDispatchCandidateV1>();
        foreach (ManagedModuleContext module in modules)
        foreach (TypeDefinitionHandle handle in module.Metadata.TypeDefinitions)
        {
                List<(ManagedModuleContext Module, TypeDefinitionHandle Type)> chain = new List<(ManagedModuleContext Module, TypeDefinitionHandle Type)>();
                ManagedModuleContext currentModule = module; TypeDefinitionHandle current = handle;
                HashSet<(string, int)> seen = new HashSet<(string, int)>(); bool found = false;
            while (seen.Add((currentModule.AssemblyName, MetadataTokens.GetToken(current))))
            {
                if (++resolutionSteps > maximumResolutionSteps) return false;
                chain.Add((currentModule, current));
                if (currentModule.AssemblyName == declarationModule.AssemblyName && current == declarationType)
                { found = true; break; }
                    TypeDefinition type = currentModule.Metadata.GetTypeDefinition(current);
                if (type.BaseType.IsNil || !TryResolveType(modules, currentModule, type.BaseType, out ManagedModuleContext? parentModule, out TypeDefinitionHandle parent)) break;
                currentModule = parentModule; current = parent;
            }
            if (!found) continue;
                TypeDefinition runtimeType = module.Metadata.GetTypeDefinition(handle);
            if (runtimeType.GetGenericParameters().Count != 0) return false;
            if (runtimeType.Attributes.HasFlag(TypeAttributes.Abstract)) continue;
                ManagedModuleContext implementationModule = declarationModule; MethodDefinitionHandle implementation = declarationMethod;
            bool final = declaration.Attributes.HasFlag(MethodAttributes.Final);
            bool hiddenByNewSlot = false;
            foreach ((ManagedModuleContext Module, TypeDefinitionHandle Type) row in chain.AsEnumerable().Reverse().Skip(1))
            {
                    TypeDefinition type = row.Module.Metadata.GetTypeDefinition(row.Type);
                // Explicit MethodImpl/generic slot substitutions need their own exact plan.
                if (type.GetMethodImplementations().Count != 0 || type.GetGenericParameters().Count != 0) return false;
                    List<MethodDefinitionHandle> overrides = new List<MethodDefinitionHandle>();
                    List<MethodDefinitionHandle> newSlots = new List<MethodDefinitionHandle>();
                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    if (++resolutionSteps > maximumResolutionSteps) return false;
                        MethodDefinition method = row.Module.Metadata.GetMethodDefinition(methodHandle);
                    if (method.Attributes.HasFlag(MethodAttributes.Virtual) &&
                        row.Module.Metadata.GetString(method.Name) == name &&
                        signatures.Method(row.Module.Metadata, method.Signature) == exactSignature)
                    {
                        if (method.Attributes.HasFlag(MethodAttributes.NewSlot)) newSlots.Add(methodHandle);
                        else overrides.Add(methodHandle);
                    }
                }
                if (newSlots.Count + overrides.Count > 1) return false;
                // A descendant reuse-slot method now belongs to this new declaration,
                // not the original slot whose implementation we are resolving.
                if (newSlots.Count != 0) hiddenByNewSlot = true;
                if (hiddenByNewSlot) continue;
                if (overrides.Count > 1 || final && overrides.Count != 0) return false;
                if (overrides.Count == 1)
                {
                    implementationModule = row.Module; implementation = overrides[0];
                    final = row.Module.Metadata.GetMethodDefinition(implementation).Attributes.HasFlag(MethodAttributes.Final);
                }
            }
                MethodDefinition body = implementationModule.Metadata.GetMethodDefinition(implementation);
            if (body.Attributes.HasFlag(MethodAttributes.Abstract) || body.Attributes.HasFlag(MethodAttributes.Static) ||
                body.GetGenericParameters().Count != 0 || body.RelativeVirtualAddress == 0) return false;
            candidates.Add(new(MetadataTokens.GetToken(implementation), [], [], implementationModule.AssemblyName,
                FullTypeName(module.Metadata, handle)));
        }
        if (candidates.Count == 0 || candidates.Count > HybridCPU.Platform.Contracts.HybridCpuPlatformContractV1.MaximumManagedTypes) return false;
        string identity = $"{declarationModule.AssemblyName}:{FullTypeName(declarationModule.Metadata, declarationType)}.{name}:{exactSignature}";
        ulong slot = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes("hybridcpu.closed-virtual-slot/v1|" + identity)));
        if (slot == 0) return false;
        declaringType = FullTypeName(declarationModule.Metadata, declarationType);
        binding = new(token, identity, signature.Parameters.Select(StackType).ToArray(), StackType(signature.ReturnType),
            RestrictedCilDispatchKindV1.Virtual, slot, ExactGenericCandidates: candidates.OrderBy(static row => row.RuntimeTypeIdentity, StringComparer.Ordinal).ToArray());
        return true;
    }
}
