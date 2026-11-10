using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedArgumentOutOfRangeCtorEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_argument_out_of_range_ctor_param_name";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.argument-out-of-range-ctor-param-name/v1";

    public static HybridCpuObjectArtifactV1 EmitObject() =>
        HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject(Symbol,
            HybridCpuManagedRuntimeEcallContractV1.ArgumentOutOfRangeCtorParamNameOperation, 2);
}
