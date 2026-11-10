using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedArrayLoadReferenceEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_array_load_ref";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.array-load-ref/v1";

    public static HybridCpuObjectArtifactV1 EmitObject() =>
        HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject(Symbol,
            HybridCpuManagedRuntimeEcallContractV1.ArrayLoadReferenceOperation, 2);
}
