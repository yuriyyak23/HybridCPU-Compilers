using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedStaticLoadReferenceEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_static_load_ref";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.static-load-ref/v1";

    public static HybridCpuObjectArtifactV1 EmitObject() =>
        HybridCpuManagedStaticLoadInt32EmitterV1.EmitObject(Symbol,
            HybridCpuManagedRuntimeEcallContractV1.StaticLoadReferenceOperation);
}
