using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedAllocateObjectEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_alloc";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.alloc/v1";

    public static HybridCpuObjectArtifactV1 EmitObject() =>
        HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject(Symbol,
            HybridCpuManagedRuntimeEcallContractV1.AllocateObjectOperation, 1);
}
