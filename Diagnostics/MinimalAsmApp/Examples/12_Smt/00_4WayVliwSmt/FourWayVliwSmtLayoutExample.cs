using HybridCPU_ISE.Arch;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Registers;

namespace MinimalAsmApp.Examples.Smt;

public sealed class FourWayVliwSmtLayoutExample : ICpuExample
{
    public string Name => "smt-4way-vliw-layout";

    public string Description => "Builds one annotated 8-slot VLIW bundle that maps slots across the four SMT virtual threads.";

    public string Category => "12_Smt/00_4WayVliwSmt";

    public CpuExampleResult Run()
    {
        SmtExampleDescriber.ExpectSmtWayCount(VtId.SmtWayCount);

        InstructionSlotMetadata[] slots =
        [
            new(VtId.Create(0), SlotMetadata.Default),
            new(VtId.Create(1), SlotMetadata.Default),
            new(VtId.Create(2), SlotMetadata.Default),
            new(VtId.Create(3), SlotMetadata.Default),
            new(VtId.Create(0), SlotMetadata.Default),
            new(VtId.Create(1), SlotMetadata.Default),
            new(VtId.Create(2), SlotMetadata.Default),
            new(VtId.Create(3), SlotMetadata.Default)
        ];

        VliwBundleAnnotations annotations = new VliwBundleAnnotations(slots);
        if (annotations.Count != slots.Length || slots.Select(slot => slot.VirtualThreadId.Value).Distinct().Count() != 4)
        {
            throw new InvalidOperationException("4-way SMT slot annotation layout was not built correctly.");
        }

        return CpuExampleResult.Ok(
            "Expected one VLIW bundle with 8 slots distributed over vt0..vt3.",
            notes: SmtExampleDescriber.DescribeSlots(slots));
    }
}
