using HybridCPU_ISE.Arch;
using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Memory;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class TypedWordMemoryEncodingExample : ICpuExample
{
    private const ulong BaseAddress = 0x2300;

    public string Name => "typed-word-memory-encoding";

    public string Description => "Encodes LHU, LW, LWU, SB, SH and SW typed memory carriers.";

    public string Category => "03_Memory";

    public CpuExampleResult Run()
    {
        VLIW_Instruction lhu = Load(Instruction.LHU, destinationRegister: 5, BaseAddress);
        VLIW_Instruction lw = Load(Instruction.LW, destinationRegister: 6, BaseAddress + 4);
        VLIW_Instruction lwu = Load(Instruction.LWU, destinationRegister: 7, BaseAddress + 8);
        VLIW_Instruction sb = Store(Instruction.SB, sourceRegister: 8, BaseAddress + 12);
        VLIW_Instruction sh = Store(Instruction.SH, sourceRegister: 9, BaseAddress + 16);
        VLIW_Instruction sw = Store(Instruction.SW, sourceRegister: 10, BaseAddress + 20);

        VLIW_Instruction[] instructions = [lhu, lw, lwu, sb, sh, sw];
        foreach (VLIW_Instruction instruction in instructions)
        {
            CpuInstructionDescriber.ExpectValid(in instruction);
        }

        return CpuExampleResult.Ok(
            "Encoded typed word/halfword memory carriers without claiming a wider executable memory backend.",
            notes:
            [
                "LHU:",
                .. CpuInstructionDescriber.Describe(in lhu),
                "LW:",
                .. CpuInstructionDescriber.Describe(in lw),
                "LWU:",
                .. CpuInstructionDescriber.Describe(in lwu),
                "SB:",
                .. CpuInstructionDescriber.Describe(in sb),
                "SH:",
                .. CpuInstructionDescriber.Describe(in sh),
                "SW:",
                .. CpuInstructionDescriber.Describe(in sw)
            ]);
    }

    private static VLIW_Instruction Load(
        Instruction opcode,
        int destinationRegister,
        ulong address) =>
        new()
        {
            OpCode = (uint)opcode,
            DataTypeValue = DataTypeEnum.INT64,
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(
                checked((byte)destinationRegister),
                VLIW_Instruction.NoArchReg,
                VLIW_Instruction.NoArchReg),
            Src2Pointer = address
        };

    private static VLIW_Instruction Store(
        Instruction opcode,
        int sourceRegister,
        ulong address) =>
        new()
        {
            OpCode = (uint)opcode,
            DataTypeValue = DataTypeEnum.INT64,
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(
                VLIW_Instruction.NoArchReg,
                VLIW_Instruction.NoArchReg,
                checked((byte)sourceRegister)),
            Src2Pointer = address
        };
}
