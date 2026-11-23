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
            [typeof(ManagedSourceCompanionV1)], expectDll: false);
        RequireRealPe(typeof(CompanionBridgeProjectionV1).Assembly, "HardwareBridge.Companion",
            [typeof(CompanionBridgeProjectionV1)]);

        // Mutations exercise the very same on-disk metadata gate used for production.
        Reject(() => RequireRealPe(core, "Wrong.Module", requiredCompilerTypes), "wrong-module-name");
        Reject(() => RequireRealPe(core, "HybridCPU.Compiler.Core",
            [typeof(ManagedSourceCompanionV1)]), "wrong-type-assembly");
        Reject(() => RequireRealPe(core, "HybridCPU.Compiler.Core", requiredCompilerTypes,
            typeof(RestrictedCilImporterV1).Assembly.Location), "swapped-valid-DLL");

        string bogusPath = Path.Combine(AppContext.BaseDirectory, "R0_01_non_pe_mutation.dll");
        using (var fake = new FileStream(bogusPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fake.Write(new byte[64]);
            fake.Flush(true);
        }
        try
        {
            Reject(() => RequireRealPe(core, "HybridCPU.Compiler.Core", requiredCompilerTypes, bogusPath),
                "fake-PE");
        }
        finally
        {
            File.Delete(bogusPath);
        }
    }

    private static void Reject(Action probe, string label)
    {
        try { probe(); }
        catch (InvalidOperationException) { return; }
        catch (BadImageFormatException) { return; }
        throw new InvalidOperationException("R0_01 mutation unexpectedly accepted: " + label);
    }

    private static void RequireRealPe(Assembly assembly, string expectedName, Type[] requiredTypes,
        string? fileOverride = null, bool expectDll = true)
    {
        if (assembly.IsDynamic || assembly.GetName().Name != expectedName ||
            string.IsNullOrWhiteSpace(assembly.Location) || !File.Exists(assembly.Location))
            throw new InvalidOperationException("Required real compiled assembly is missing: " + expectedName);

        string file = fileOverride ?? assembly.Location;
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length == 0)
            throw new InvalidOperationException("Empty compiled PE: " + expectedName);

        using var pe = new PEReader(input);
        bool isDll = (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) != 0;
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null || isDll != expectDll)
            throw new InvalidOperationException("Unexpected managed PE kind: " + expectedName);

        MetadataReader metadata = pe.GetMetadataReader();
        if (!metadata.IsAssembly ||
            metadata.GetString(metadata.GetAssemblyDefinition().Name) != expectedName ||
            metadata.GetString(metadata.GetModuleDefinition().Name) != Path.GetFileName(assembly.Location) ||
            metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != assembly.ManifestModule.ModuleVersionId)
            throw new InvalidOperationException("Swapped DLL or wrong module/MVID origin: " + expectedName);
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
