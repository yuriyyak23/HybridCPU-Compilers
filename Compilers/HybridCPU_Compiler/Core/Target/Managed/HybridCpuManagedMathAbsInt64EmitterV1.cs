using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedMathAbsInt64EmitterV1
{
    public const string Symbol = "__hybridcpu_managed_math_abs_i8";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.math-abs-i8-checked/v1";

    public static HybridCpuObjectArtifactV1 EmitObject() =>
        HybridCpuManagedArrayStoreInt32EmitterV1.EmitObject(Symbol,
            HybridCpuManagedRuntimeEcallContractV1.MathAbsInt64CheckedOperation, 1);
}
