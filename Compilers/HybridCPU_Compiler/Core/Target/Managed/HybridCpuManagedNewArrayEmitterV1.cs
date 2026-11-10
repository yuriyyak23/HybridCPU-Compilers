using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedNewArrayEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_newarr";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.newarr/v1";
    public static HybridCpuObjectArtifactV1 EmitObject() =>
        HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject(Symbol,
            HybridCpuManagedRuntimeEcallContractV1.NewArrayOperation, 2);
}
