using HybridCPU_ISE.Arch;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Registers;

namespace MinimalAsmApp.Examples.Smt;

public sealed class FspStealableSlotsExample : ICpuExample
{
    public string Name => "fsp-stealable-slots";

    public string Description => "Builds FSP slot metadata where free slots are explicitly stealable for fine-grained slot pilfering.";

    public string Category => "12_Smt/01_FineGrainedSlotPilfering";

    public CpuExampleResult Run()
    {
        InstructionSlotMetadata[] slots =
        [
            new(VtId.Create(0), SlotMetadata.Default),
            new(VtId.Create(1), SlotMetadata.Default),
            new(VtId.Create(2), SlotMetadata.Default),
            new(VtId.Create(3), SlotMetadata.Default)
        ];

        int stealableCount = slots.Count(slot =>
            slot.SlotMetadata.StealabilityPolicy == StealabilityPolicy.Stealable);
        if (stealableCount != slots.Length)
        {
            throw new InvalidOperationException("All default FSP slots should be stealable.");
        }

        return CpuExampleResult.Ok(
            "Expected all default slots to allow FSP pilfering.",
            notes: SmtExampleDescriber.DescribeSlots(slots));
    }
}
