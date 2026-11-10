using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedStaticStoreReferenceEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_static_store_ref";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.static-store-ref/v1";

    public static HybridCpuObjectArtifactV1 EmitObject() =>
        HybridCpuManagedStaticStoreInt32EmitterV1.EmitObject(Symbol,
            HybridCpuManagedRuntimeEcallContractV1.StaticStoreReferenceOperation);
}
