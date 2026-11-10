using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Cil;

public sealed record ManagedCoreLibSubsetRowV1(string ProfileId, IReadOnlyList<string> ForbiddenTypePrefixes,
    IReadOnlyList<string> ForbiddenRuntimeHelperPrefixes, string MatrixDigest);

public sealed record ManagedProfileClosureEvidenceV1(string SchemaId, IReadOnlyList<string> RootProfiles,
    IReadOnlyList<string> ReachableMethodIdentities, IReadOnlyList<string> CoreLibMatrixDigests,
    string GraphDigest, string EvidenceDigest)
{
    public bool HasRuntimeAuthority => false;
}

public static class ManagedProfileClosureContractV1
{
    public const string SchemaId = "hybridcpu.managed-profile-closure/v1";
    public const string KernelNoHeap = "HybridCPU.Kernel.NoHeap";
    public const string Kernel = "HybridCPU.Kernel";
    public const string Sip = "HybridCPU.SIP";
    public const string Managed = "HybridCPU.Managed";

    private static readonly IReadOnlyDictionary<string, ManagedCoreLibSubsetRowV1> Matrices =
        new[]
        {
            Row(KernelNoHeap, ["System.Threading.", "System.Reflection.", "System.Globalization.", "System.Net.",
                "System.IO.Pipelines.", "System.Linq.", "System.Activator", "System.Delegate", "System.GC"],
                ["__hybridcpu_managed_alloc", "__hybridcpu_managed_newarr", "__hybridcpu_managed_string_concat", "__hybridcpu_managed_array_empty"]),
            Row(Kernel, ["System.Threading.ThreadPool", "System.Reflection.Emit.", "System.Globalization.", "System.Net.Sockets."], []),
            Row(Sip, ["System.Reflection.Emit.", "System.Net.Sockets.", "System.Threading.ThreadPool"], []),
            Row(Managed, ["System.Reflection.Emit."], [])
        }.ToDictionary(static row => row.ProfileId, StringComparer.Ordinal);

    public static IReadOnlyList<ManagedCoreLibSubsetRowV1> CoreLibMatrices =>
        Matrices.Values.OrderBy(static row => row.ProfileId, StringComparer.Ordinal).ToArray();

    public static (ManagedProfileClosureEvidenceV1? Evidence, string? Failure) Validate(
        IReadOnlyList<ManagedCompiledMethodV1> methods, IReadOnlyList<string> roots, string graphDigest)
    {
        string[] rootProfiles = methods.Where(method => roots.Contains(method.Identity.StableIdentity, StringComparer.Ordinal))
            .SelectMany(static method => method.Import.SourceProfileMarkers ?? [])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (rootProfiles.Length == 0) return (null, null);
        if (rootProfiles.Any(profile => !Matrices.ContainsKey(profile)))
            return (null, "HCPROFILE2001: reachable root contains an unknown source profile marker.");
        foreach (ManagedCompiledMethodV1 method in methods.OrderBy(static method => method.Identity.StableIdentity, StringComparer.Ordinal))
        foreach (string profile in rootProfiles)
        {
            ManagedCoreLibSubsetRowV1 matrix = Matrices[profile];
            if (matrix.ForbiddenTypePrefixes.Any(prefix => method.Identity.DeclaringType.StartsWith(prefix, StringComparison.Ordinal)))
                return (null, $"HCPROFILE2002: profile '{profile}' rejects reachable method '{method.Identity.StableIdentity}' by CoreLib subset matrix.");
            string? helper = method.Import.Program?.Instructions.Select(static instruction => instruction.Annotation.BranchTargetSymbolName)
                .FirstOrDefault(target => target is not null && matrix.ForbiddenRuntimeHelperPrefixes.Any(prefix => target.StartsWith(prefix, StringComparison.Ordinal)));
            if (helper is not null)
                return (null, $"HCPROFILE2003: profile '{profile}' rejects reachable runtime helper '{helper}' from '{method.Identity.StableIdentity}'.");
        }
        string[] identities = methods.Select(static method => method.Identity.StableIdentity).Order(StringComparer.Ordinal).ToArray();
        string[] matrixDigests = rootProfiles.Select(profile => Matrices[profile].MatrixDigest).ToArray();
        string digest = HybridCpuPlatformContractV1.Hash(string.Join('|', SchemaId, graphDigest,
            string.Join(',', rootProfiles), string.Join(';', identities), string.Join(',', matrixDigests)));
        return (new(SchemaId, rootProfiles, identities, matrixDigests, graphDigest, digest), null);
    }

    internal static IReadOnlyList<string> ReadMarkers(MetadataReader metadata,
        TypeDefinitionHandle typeHandle, MethodDefinitionHandle methodHandle)
    {
        SortedSet<string> result = new SortedSet<string>(StringComparer.Ordinal);
        Add(metadata.GetTypeDefinition(typeHandle).GetCustomAttributes());
        Add(metadata.GetMethodDefinition(methodHandle).GetCustomAttributes());
        return result.ToArray();
        void Add(CustomAttributeHandleCollection attributes)
        {
            foreach (CustomAttributeHandle handle in attributes)
            {
                EntityHandle constructor = metadata.GetCustomAttribute(handle).Constructor;
                string? owner = constructor.Kind switch
                {
                    HandleKind.MemberReference => AttributeOwner(
                        metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent),
                    HandleKind.MethodDefinition => AttributeOwner(
                        metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()),
                    _ => null
                };
                string? profile = owner switch
                {
                    "HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute" => KernelNoHeap,
                    "HybridCPU.Compiler.Profiles.HybridCpuKernelAttribute" => Kernel,
                    "HybridCPU.Compiler.Profiles.HybridCpuSipAttribute" => Sip,
                    _ => null
                };
                if (profile is not null) result.Add(profile);
            }
        }

        string? AttributeOwner(EntityHandle type) => type.Kind switch
        {
            HandleKind.TypeReference => ReferenceName(metadata.GetTypeReference((TypeReferenceHandle)type)),
            HandleKind.TypeDefinition => DefinitionName(metadata.GetTypeDefinition((TypeDefinitionHandle)type)),
            _ => null
        };
        string ReferenceName(TypeReference type) => Join(metadata.GetString(type.Namespace), metadata.GetString(type.Name));
        string DefinitionName(TypeDefinition type) => Join(metadata.GetString(type.Namespace), metadata.GetString(type.Name));
        static string Join(string ns, string name) => string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static ManagedCoreLibSubsetRowV1 Row(string profile, IReadOnlyList<string> types, IReadOnlyList<string> helpers)
    {
        string[] orderedTypes = types.Order(StringComparer.Ordinal).ToArray();
        string[] orderedHelpers = helpers.Order(StringComparer.Ordinal).ToArray();
        return new(profile, orderedTypes, orderedHelpers, HybridCpuPlatformContractV1.Hash(string.Join('|',
            "hybridcpu.corelib-subset/v1", profile, string.Join(',', orderedTypes), string.Join(',', orderedHelpers))));
    }
}
