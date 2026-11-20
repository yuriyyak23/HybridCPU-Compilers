// R0-01: the real compiler graph and on-disk PE metadata, never synthetic DTO stand-ins.
// This self-test is executable only after building the opt-in production Companion.
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.HardwareBridge.CompilerFacts;
using HybridCPU.Compiler.Roslyn;

internal static class CompanionBuildContractTests
{
    public static void Run()
    {
        Type[] requiredCompilerTypes =
        [
            typeof(CompilerEmissionPackage),
            typeof(CompilerSidebandEnvelope),
            typeof(DescriptorEnvelope),
            typeof(TypedSlotFactsEnvelope),
            typeof(CompilerEvidenceEnvelope)
        ];

        Assembly core = requiredCompilerTypes[0].Assembly;
        RequireRealPe(core, "HybridCPU.Compiler.Core", requiredCompilerTypes);
        RequireRealPe(typeof(RestrictedCilImporterV1).Assembly, "HybridCPU.Compiler.Cil",
            [typeof(RestrictedCilImporterV1)]);
        RequireRealPe(typeof(ManagedSourceCompanionV1).Assembly, "HybridCPU.Compiler.Roslyn",
            [typeof(ManagedSourceCompanionV1)]);
        RequireRealPe(typeof(CompanionBridgeProjectionV1).Assembly, "HardwareBridge.Companion",
            [typeof(CompanionBridgeProjectionV1)]);
    }

    private static void RequireRealPe(Assembly assembly, string expectedName, Type[] requiredTypes)
    {
        if (assembly.IsDynamic || assembly.GetName().Name != expectedName ||
            string.IsNullOrWhiteSpace(assembly.Location) || !File.Exists(assembly.Location))
            throw new InvalidOperationException("Required real compiled assembly is missing: " + expectedName);

        using var input = new FileStream(assembly.Location, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length == 0)
            throw new InvalidOperationException("Empty compiled PE: " + expectedName);

        using var pe = new PEReader(input);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null ||
            (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) == 0)
            throw new InvalidOperationException("Not a managed DLL PE: " + expectedName);

        MetadataReader metadata = pe.GetMetadataReader();
        foreach (Type type in requiredTypes)
        {
            if (!type.IsPublic || type.Assembly != assembly)
                throw new InvalidOperationException("Required public production type has the wrong assembly.");
            bool existsInPe = metadata.TypeDefinitions.Any(handle =>
            {
                TypeDefinition definition = metadata.GetTypeDefinition(handle);
                return metadata.GetString(definition.Name) == type.Name &&
                       metadata.GetString(definition.Namespace) == type.Namespace;
            });
            if (!existsInPe)
                throw new InvalidOperationException("Required type definition is absent from the actual PE: " + type.FullName);
        }
    }
}
