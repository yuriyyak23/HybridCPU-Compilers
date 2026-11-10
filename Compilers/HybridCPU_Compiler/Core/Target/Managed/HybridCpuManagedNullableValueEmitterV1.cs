using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedNullableValueEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_nullable_value_i8_checked";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.nullable-value-i8-checked/v1";
    public static HybridCpuObjectArtifactV1 EmitObject() => HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject(
        Symbol, HybridCpuManagedRuntimeEcallContractV1.NullableValueInt64CheckedOperation, 2);
}
