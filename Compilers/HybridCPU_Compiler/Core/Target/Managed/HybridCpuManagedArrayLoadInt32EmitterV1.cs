using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedArrayLoadInt64EmitterV1
{
    public const string Symbol = "__hybridcpu_managed_array_load_i8";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.array-load-i8/v1";

    public static HybridCpuObjectArtifactV1 EmitObject() =>
        HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject(Symbol,
            HybridCpuManagedRuntimeEcallContractV1.ArrayLoadInt64Operation, 2);
}

public static class HybridCpuManagedArrayLoadInt32EmitterV1
{
    public const string Symbol = "__hybridcpu_managed_array_load_i4";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.array-load-i4/v1";

    public static HybridCpuObjectArtifactV1 EmitObject() =>
        HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject(Symbol,
            HybridCpuManagedRuntimeEcallContractV1.ArrayLoadInt32Operation, 2);
}
