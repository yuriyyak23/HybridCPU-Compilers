using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed partial class RestrictedCilImporterV1
{
    // This is deliberately an evidence-only admission seam.  It is opt-in, names one
    // cross-assembly call site family, and proves its entire receiver closure before
    // returning a binding.  It neither changes normal compilation nor grants runtime
    // authority outside the supplied image.
    private const string TestOnlyClosedWorldIConsoleEnvironment = "HYBRIDCPU_TEST_CLOSED_WORLD_ICONSOLE_V1";

    private static bool TryCreateTestOnlyClosedWorldIConsoleBinding(
        IReadOnlyList<ManagedModuleContext> modules, IReadOnlyList<ManagedModuleContext> metadataModules,
        ManagedModuleContext caller, int callToken, ManagedMetadataBindingSet metadata,
        out RestrictedCilDispatchBindingV1 binding)
    {
        binding = null!;
        if (Environment.GetEnvironmentVariable(TestOnlyClosedWorldIConsoleEnvironment) != "1" ||
            caller.AssemblyName != "DoomSharp.HybridCpu.Guest" ||
            MetadataTokens.EntityHandle(callToken).Kind != HandleKind.MemberReference ||
            !TryResolveManagedDefinition(modules, caller, callToken, null, null, out ManagedModuleContext declarationModule,
                out TypeDefinitionHandle declarationType, out MethodDefinitionHandle declarationMethod, out _, out _, out _))
            return false;

        MetadataReader declarationMetadata = declarationModule.Metadata;
        MethodDefinition declaration = declarationMetadata.GetMethodDefinition(declarationMethod);
        if (declarationModule.AssemblyName != "DoomSharp.Core" ||
            FullTypeName(declarationMetadata, declarationType) != "DoomSharp.Core.IConsole" ||
            declarationMetadata.GetString(declaration.Name) != "Write" ||
            !declarationMetadata.GetTypeDefinition(declarationType).Attributes.HasFlag(TypeAttributes.Interface))
            return false;

        MethodSignature signature = ParseMethodSignature(declarationMetadata, declaration.Signature);
        RestrictedCilTypeV1[] parameters = signature.Parameters.Select(StackType).ToArray();
        if (signature.Status != SignatureStatus.Success || !signature.HasThis ||
            !parameters.SequenceEqual([RestrictedCilTypeV1.ObjectReference, RestrictedCilTypeV1.ObjectReference]) ||
            StackType(signature.ReturnType) != RestrictedCilTypeV1.Void ||
            !metadata.Descriptors.TryGetValue("DoomSharp.Core.IConsole", out HybridCpuManagedTypeDescriptorV1? iface) ||
            iface.Kind != HybridCpuManagedTypeKindV1.Interface ||
            !metadata.Descriptors.TryGetValue("DoomSharp.HybridCpu.Guest.HybridCpuConsole", out HybridCpuManagedTypeDescriptorV1? receiver) ||
            receiver.Kind != HybridCpuManagedTypeKindV1.Class || !receiver.InterfaceTypeIds.Contains(iface.TypeId))
            return false;

        TypeDefinitionHandle[] implementations = caller.Metadata.TypeDefinitions.Where(handle =>
            FullTypeName(caller.Metadata, handle) == "DoomSharp.HybridCpu.Guest.HybridCpuConsole").ToArray();
        if (implementations.Length != 1) return false;
        TypeDefinition implementationType = caller.Metadata.GetTypeDefinition(implementations[0]);
        if (!implementationType.Attributes.HasFlag(TypeAttributes.Sealed) || implementationType.Attributes.HasFlag(TypeAttributes.Abstract) ||
            !TryResolveType(metadataModules, caller, implementationType.BaseType, out ManagedModuleContext baseModule,
                out TypeDefinitionHandle baseType) || FullTypeName(baseModule.Metadata, baseType) != "System.Object")
            return false;
        bool directInterface = implementationType.GetInterfaceImplementations().Any(handle =>
            TryResolveType(metadataModules, caller, caller.Metadata.GetInterfaceImplementation(handle).Interface,
                out ManagedModuleContext interfaceModule, out TypeDefinitionHandle interfaceType) &&
            interfaceModule.AssemblyName == declarationModule.AssemblyName && interfaceType == declarationType);
        if (!directInterface || metadataModules.Any(module => module.Metadata.TypeDefinitions.Any(child =>
            !module.Metadata.GetTypeDefinition(child).BaseType.IsNil &&
            TryResolveType(metadataModules, module, module.Metadata.GetTypeDefinition(child).BaseType,
                out ManagedModuleContext parentModule, out TypeDefinitionHandle parent) &&
            parentModule.AssemblyName == caller.AssemblyName && parent == implementations[0])))
            return false;

        byte[] declarationSignature = declarationMetadata.GetBlobBytes(declaration.Signature);
        MethodDefinitionHandle[] matches = implementationType.GetMethods().Where(handle =>
        {
            MethodDefinition method = caller.Metadata.GetMethodDefinition(handle);
            return caller.Metadata.GetString(method.Name) == "Write" &&
                (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public &&
                !method.Attributes.HasFlag(MethodAttributes.Static) && !method.Attributes.HasFlag(MethodAttributes.Abstract) &&
                method.RelativeVirtualAddress != 0 && method.GetGenericParameters().Count == 0 &&
                caller.Metadata.GetBlobBytes(method.Signature).AsSpan().SequenceEqual(declarationSignature);
        }).ToArray();
        if (matches.Length != 1) return false;

        ulong slot = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"hybridcpu.managed-slot-id/v1|{iface.TypeId}|DoomSharp.Core.IConsole.Write|instance(System.String):System.Void")));
        binding = new(callToken, "test-only-closed-world:DoomSharp.Core.IConsole.Write(instance(System.String):System.Void)",
            parameters, RestrictedCilTypeV1.Void, RestrictedCilDispatchKindV1.Interface, slot == 0 ? 1 : slot, iface.TypeId,
            ExactGenericCandidates: [new(MetadataTokens.GetToken(matches[0]), [], [], caller.AssemblyName,
                "DoomSharp.HybridCpu.Guest.HybridCpuConsole")], RuntimeExternal: false);
        return true;
    }
}
