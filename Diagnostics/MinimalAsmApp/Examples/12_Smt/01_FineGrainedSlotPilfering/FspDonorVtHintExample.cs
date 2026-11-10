using HybridCPU_ISE.Arch;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Registers;

namespace MinimalAsmApp.Examples.Smt;

public sealed class FspDonorVtHintExample : ICpuExample
{
    public string Name => "fsp-donor-vt-hints";

    public string Description => "Adds donor VT hints that guide which virtual thread should fill a stealable free slot.";

    public string Category => "12_Smt/01_FineGrainedSlotPilfering";

    public CpuExampleResult Run()
    {
        InstructionSlotMetadata[] slots =
        [
            new(VtId.Create(0), SlotMetadata.Default with { DonorVtHint = 1 }),
            new(VtId.Create(1), SlotMetadata.Default with { DonorVtHint = 2 }),
            new(VtId.Create(2), SlotMetadata.Default with { DonorVtHint = 3 }),
            new(VtId.Create(3), SlotMetadata.Default with { DonorVtHint = 0 })
        ];

        bool allHintsValid = slots.All(slot => slot.SlotMetadata.DonorVtHint < VtId.SmtWayCount);
        if (!allHintsValid)
        {
            throw new InvalidOperationException("FSP donor VT hints must point to vt0..vt3.");
        }

        return CpuExampleResult.Ok(
            "Expected every stealable slot to carry a valid donor VT hint.",
            notes: SmtExampleDescriber.DescribeSlots(slots));
    }
}
