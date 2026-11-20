// HB-INT-06: compile against the real software compiler assemblies, not local facsimiles.
// This remains inside the opt-in self-test project and cannot qualify hardware.
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
        foreach (Type type in requiredCompilerTypes)
        {
            if (!type.IsPublic || type.Assembly.GetName().Name != "HybridCPU.Compiler.Core")
                throw new InvalidOperationException(
                    $"Required compiler-owned type {type.FullName} is not exported by the real Core assembly.");
        }

        if (typeof(RestrictedCilImporterV1).Assembly.GetName().Name != "HybridCPU.Compiler.Cil")
            throw new InvalidOperationException("Actual CIL importer assembly is missing.");
        if (typeof(ManagedSourceCompanionV1).Assembly.GetName().Name != "HybridCPU.Compiler.Roslyn")
            throw new InvalidOperationException("Actual Roslyn Companion assembly is missing.");
        if (typeof(CompanionBridgeProjectionV1).Assembly.GetName().Name != "HardwareBridge.Companion")
            throw new InvalidOperationException("Actual opt-in HardwareBridge Companion assembly is missing.");
    }
}
