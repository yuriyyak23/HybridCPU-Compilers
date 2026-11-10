using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedRemainderInt32EmitterV1
{
    public const string Symbol = "__hybridcpu_managed_remainder_i4_checked";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.remainder-i4-checked/v1";

    public static HybridCpuObjectArtifactV1 EmitObject() =>
        HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject(Symbol,
            HybridCpuManagedRuntimeEcallContractV1.RemainderInt32CheckedOperation, 2);
}
