using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using HybridCPU_ISE.Arch;

namespace MinimalAsmApp.Examples.Memory;

using static CpuInstructions;
using Instruction = YAKSys_Hybrid_CPU.Processor.CPU_Core.InstructionsEnum;

public sealed class ByteHalfwordMemoryExample : ICpuExample
{
    public string Name => "byte-halfword-memory";

    public string Description => "Runs LB, LBU, LH instructions.";

    public string Category => "03_Memory";

    public CpuExampleResult Run()
    {
        ulong address = 0x2000;
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, unchecked((short)0x1234)),
            StoreDoubleword(1, address),
            new VLIW_Instruction
            {
                OpCode = (uint)Instruction.LB,
                DataTypeValue = DataTypeEnum.INT64,
                DestSrc1Pointer = VLIW_Instruction.PackArchRegs(2, VLIW_Instruction.NoArchReg, VLIW_Instruction.NoArchReg),
                Src2Pointer = address
            },
            new VLIW_Instruction
            {
                OpCode = (uint)Instruction.LBU,
                DataTypeValue = DataTypeEnum.INT64,
                DestSrc1Pointer = VLIW_Instruction.PackArchRegs(3, VLIW_Instruction.NoArchReg, VLIW_Instruction.NoArchReg),
                Src2Pointer = address
            },
            new VLIW_Instruction
            {
                OpCode = (uint)Instruction.LH,
                DataTypeValue = DataTypeEnum.INT64,
                DestSrc1Pointer = VLIW_Instruction.PackArchRegs(4, VLIW_Instruction.NoArchReg, VLIW_Instruction.NoArchReg),
                Src2Pointer = address
            }
        );

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions { RegisterDump = [2, 3, 4] });

        return CpuExampleResult.Ok(
            "Expected correct loading of byte, unsigned byte, and halfword.",
            execution.Registers);
    }
}
