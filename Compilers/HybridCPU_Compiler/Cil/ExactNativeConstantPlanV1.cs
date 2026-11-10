using HybridCPU.Compiler.Core.IR;

namespace HybridCPU.Compiler.Cil;

public sealed record ExactNativeConstantStepV1(HybridCpuOpcode Opcode, ushort Immediate);

/// <summary>Compatibility facade over the compiler-owned exact bit-pattern materialization plan.</summary>
public static class ExactNativeConstantPlanV1
{
    public static IReadOnlyList<ExactNativeConstantStepV1> Create(ulong value) =>
        HybridCpuConstantMaterializationPlanV1.Create(value)
            .Select(static step => new ExactNativeConstantStepV1(step.Opcode, step.Immediate)).ToArray();

    public static bool FitsSignedImmediate(ulong value) =>
        HybridCpuConstantMaterializationPlanV1.FitsSignedImmediate(value);
}
