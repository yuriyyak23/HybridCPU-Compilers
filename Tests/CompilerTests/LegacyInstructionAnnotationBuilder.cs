using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Registers;

namespace HybridCPU_ISE.Tests.CompilerTests;

internal static class LegacyInstructionAnnotationBuilder
{
    public static VliwBundleAnnotations Build(VLIW_Instruction[] instructions)
    {
        if (instructions.Length == 0)
        {
            return VliwBundleAnnotations.Empty;
        }

        // Preserve legacy test semantics explicitly now that production IR construction
        // no longer reads VT / stealability policy from encoded instruction payload bits.
        InstructionSlotMetadata[] slotMetadata = new InstructionSlotMetadata[instructions.Length];

        for (int index = 0; index < instructions.Length; index++)
        {
            slotMetadata[index] = new InstructionSlotMetadata(
                VtId.Create(instructions[index].VirtualThreadId),
                HasLegacyCanBeStolenBit(instructions[index]) ? SlotMetadata.Default : SlotMetadata.NotStealable);
        }

        return new VliwBundleAnnotations(slotMetadata);
    }

    private static bool HasLegacyCanBeStolenBit(VLIW_Instruction instruction)
        => ((instruction.Word3 >> 50) & 0x1UL) != 0;
}

