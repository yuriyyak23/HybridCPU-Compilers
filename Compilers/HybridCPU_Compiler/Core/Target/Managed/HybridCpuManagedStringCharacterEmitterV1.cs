using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;
namespace HybridCPU.Compiler.Core.Target.Managed;
public static class HybridCpuManagedStringCharacterEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_string_char";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.string-char/v1";
    public static HybridCpuObjectArtifactV1 EmitObject() => HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject(
        Symbol, HybridCpuManagedRuntimeEcallContractV1.StringCharacterOperation, 2);
}
