using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedArrayLengthEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_array_length";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.array-length/v1";

    public static HybridCpuObjectArtifactV1 EmitObject() =>
        HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject(Symbol,
            HybridCpuManagedRuntimeEcallContractV1.ArrayLengthOperation, 1);
}
