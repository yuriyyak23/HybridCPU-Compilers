using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Transitional compatibility conversion kept explicit while runtime-facing adapters
/// are moved out of the standalone compiler core.
/// </summary>
public static class HybridCpuOpcodeRuntimeMapping
{
    public static InstructionsEnum ToRuntime(HybridCpuOpcode opcode) =>
        (InstructionsEnum)(ushort)opcode;

    public static HybridCpuOpcode FromRuntime(InstructionsEnum opcode) =>
        (HybridCpuOpcode)(ushort)opcode;
}
