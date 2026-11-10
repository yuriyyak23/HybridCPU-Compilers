using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedDivideUInt32EmitterV1
{
    public const string Symbol = "__hybridcpu_managed_divide_u4_checked";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.divide-u4-checked/v1";

    public static HybridCpuObjectArtifactV1 EmitObject() =>
        HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject(Symbol,
            HybridCpuManagedRuntimeEcallContractV1.DivideUInt32CheckedOperation, 2);
}
