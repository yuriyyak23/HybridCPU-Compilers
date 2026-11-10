using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedArrayLoadUInt8EmitterV1
{
    public const string Symbol = "__hybridcpu_managed_array_load_u1";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.array-load-u1/v1";

    public static HybridCpuObjectArtifactV1 EmitObject() =>
        HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject(Symbol,
            HybridCpuManagedRuntimeEcallContractV1.ArrayLoadUInt8Operation, 2);
}
