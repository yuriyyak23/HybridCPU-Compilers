using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;

namespace MinimalAsmApp.Examples.Smt;

internal static class SmtExampleDescriber
{
    public static IReadOnlyList<string> DescribeSlots(IReadOnlyList<InstructionSlotMetadata> slots)
    {
        List<string> lines = new List<string>(slots.Count);
        for (int slot = 0; slot < slots.Count; slot++)
        {
            SlotMetadata metadata = slots[slot].SlotMetadata;
            lines.Add(
                $"slot[{slot}] vt{slots[slot].VirtualThreadId.Value}, " +
                $"steal={metadata.StealabilityPolicy}, " +
                $"donorHint={FormatHint(metadata.DonorVtHint)}, " +
                $"preferredVt={FormatHint(metadata.PreferredVt)}");
        }

        return lines;
    }

    public static IReadOnlyList<string> DescribeBundle(BundleMetadata bundle)
    {
        List<string> lines = new List<string>
        {
            $"bundleSlots = {BundleMetadata.BundleSlotCount}",
            $"fspBoundary = {bundle.FspBoundary}",
            $"bundleThermalHint = {bundle.BundleThermalHint}",
            $"isReplayAnchor = {bundle.IsReplayAnchor}",
            $"diagnosticsTag = {bundle.DiagnosticsTag ?? "<none>"}"
        };

        for (int slot = 0; slot < BundleMetadata.BundleSlotCount; slot++)
        {
            SlotMetadata metadata = bundle.GetSlotMetadata(slot);
            lines.Add(
                $"slot[{slot}] steal={metadata.StealabilityPolicy}, " +
                $"donorHint={FormatHint(metadata.DonorVtHint)}, " +
                $"preferredVt={FormatHint(metadata.PreferredVt)}");
        }

        return lines;
    }

    public static IReadOnlyList<string> DescribeOpcodes(params Processor.CPU_Core.InstructionsEnum[] opcodes)
    {
        List<string> lines = new List<string>(opcodes.Length);
        foreach (Processor.CPU_Core.InstructionsEnum opcode in opcodes)
        {
            (InstructionClass instructionClass, SerializationClass serialization) =
                InstructionClassifier.Classify(opcode);

            lines.Add($"{opcode}: class={instructionClass}, serialization={serialization}");
        }

        return lines;
    }

    public static void ExpectSmtWayCount(int actual)
    {
        if (actual != Processor.CPU_Core.SmtWays)
        {
            throw new InvalidOperationException(
                $"Expected {Processor.CPU_Core.SmtWays} SMT ways, got {actual}.");
        }
    }

    public static void ExpectValidInstruction(in VLIW_Instruction instruction)
    {
        if (!InstructionEncoder.ValidateInstruction(in instruction))
        {
            throw new InvalidOperationException(
                $"Invalid SMT instruction carrier for opcode 0x{instruction.OpCode:X}.");
        }
    }

    private static string FormatHint(byte value) =>
        value == 0xFF ? "<none>" : $"vt{value}";
}
