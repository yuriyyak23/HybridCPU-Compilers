using HybridCPU_ISE.Arch;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Registers;

namespace MinimalAsmApp.Examples.Smt;

public sealed class FspProtectedSlotsExample : ICpuExample
{
    public string Name => "fsp-protected-slots";

    public string Description => "Marks control/system-like slots as NotStealable so FSP cannot pilfer them.";

    public string Category => "12_Smt/01_FineGrainedSlotPilfering";

    public CpuExampleResult Run()
    {
        InstructionSlotMetadata[] slots =
        [
            new(VtId.Create(0), SlotMetadata.Default),
            new(VtId.Create(0), SlotMetadata.NotStealable),
            new(VtId.Create(1), SlotMetadata.Default),
            new(VtId.Create(1), SlotMetadata.NotStealable)
        ];

        int protectedCount = slots.Count(slot =>
            slot.SlotMetadata.StealabilityPolicy == StealabilityPolicy.NotStealable);
        if (protectedCount != 2)
        {
            throw new InvalidOperationException("Expected two protected FSP slots.");
        }

        return CpuExampleResult.Ok(
            "Expected two stealable slots and two protected NotStealable slots.",
            notes: SmtExampleDescriber.DescribeSlots(slots));
    }
}
