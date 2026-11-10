using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedStringNotEqualsEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_string_not_equals";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.string-not-equals/v1";

    public static HybridCpuObjectArtifactV1 EmitObject() =>
        HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject(Symbol,
            HybridCpuManagedRuntimeEcallContractV1.StringNotEqualsOperation, 2);
}
