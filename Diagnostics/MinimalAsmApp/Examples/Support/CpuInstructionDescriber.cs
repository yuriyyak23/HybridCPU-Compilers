using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;

namespace MinimalAsmApp.Examples.Support;

public static class CpuInstructionDescriber
{
    public static void ExpectValid(in VLIW_Instruction instruction)
    {
        if (!InstructionEncoder.ValidateInstruction(in instruction))
        {
            throw new InvalidOperationException(
                $"Instruction encoding is invalid for opcode 0x{instruction.OpCode:X}.");
        }
    }

    public static IReadOnlyList<string> Describe(in VLIW_Instruction instruction)
    {
        return
        [
            $"opcode = {ResolveOpcodeName(in instruction)} ({instruction.OpCode})",
            $"dataType = {instruction.DataTypeValue}",
            $"predicateMask = {instruction.PredicateMask}",
            $"acquire = {instruction.Acquire}",
            $"release = {instruction.Release}",
            $"saturating = {instruction.Saturating}",
            $"immediate = {instruction.Immediate}",
            $"dest/src1 pointer = 0x{instruction.DestSrc1Pointer:X}",
            $"src2 pointer = 0x{instruction.Src2Pointer:X}",
            $"streamLength = {instruction.StreamLength}",
            $"stride = {instruction.Stride}",
            $"rowStride = {instruction.RowStride}",
            $"indexed = {instruction.Indexed}",
            $"is2D = {instruction.Is2D}",
            $"reduction = {instruction.Reduction}"
        ];
    }

    private static string ResolveOpcodeName(in VLIW_Instruction instruction)
    {
        ushort opcode = checked((ushort)instruction.OpCode);
        return Enum.IsDefined(typeof(Processor.CPU_Core.InstructionsEnum), opcode)
            ? ((Processor.CPU_Core.InstructionsEnum)opcode).ToString()
            : "Unknown";
    }
}
