using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedStringConcat3EmitterV1
{
    public const string Symbol = "__hybridcpu_managed_string_concat3";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.string-concat3/v1";

    public static HybridCpuObjectArtifactV1 EmitObject() =>
        HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject(Symbol,
            HybridCpuManagedRuntimeEcallContractV1.StringConcat3Operation, 3);
}
