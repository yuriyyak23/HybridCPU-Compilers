using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Support;

using Instruction = YAKSys_Hybrid_CPU.Processor.CPU_Core.InstructionsEnum;

public static class CpuInstructions
{
    public static VLIW_Instruction AddImmediate(int destinationRegister, int sourceRegister, short immediate) =>
        BinaryImmediate(Instruction.ADDI, destinationRegister, sourceRegister, immediate);

    public static VLIW_Instruction BinaryImmediate(
        Instruction opcode,
        int destinationRegister,
        int sourceRegister,
        short immediate)
    {
        return new VLIW_Instruction
        {
            OpCode = (uint)opcode,
            DataTypeValue = DataTypeEnum.INT64,
            PredicateMask = 0,
            Immediate = unchecked((ushort)immediate),
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(
                checked((byte)destinationRegister),
                checked((byte)sourceRegister),
                VLIW_Instruction.NoArchReg),
        };
    }

    public static VLIW_Instruction Move(int destinationRegister, int sourceRegister) =>
        AddImmediate(destinationRegister, sourceRegister, 0);

    public static VLIW_Instruction ScalarUnary(
        Instruction opcode,
        int destinationRegister,
        int sourceRegister,
        DataTypeEnum dataType = DataTypeEnum.INT32)
    {
        return InstructionEncoder.EncodeScalarUnary(
            (uint)opcode,
            dataType,
            checked((ushort)destinationRegister),
            checked((ushort)sourceRegister));
    }

    public static VLIW_Instruction SignExtendWord(int destinationRegister, int sourceRegister) =>
        ScalarUnary(Instruction.SEXT_W, destinationRegister, sourceRegister);

    public static VLIW_Instruction ZeroExtendWord(int destinationRegister, int sourceRegister) =>
        ScalarUnary(Instruction.ZEXT_W, destinationRegister, sourceRegister);

    public static VLIW_Instruction Binary(
        Instruction opcode,
        int destinationRegister,
        int sourceRegister1,
        int sourceRegister2)
    {
        return new VLIW_Instruction
        {
            OpCode = (uint)opcode,
            DataTypeValue = DataTypeEnum.INT64,
            PredicateMask = 0,
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(
                checked((byte)destinationRegister),
                checked((byte)sourceRegister1),
                checked((byte)sourceRegister2))
        };
    }

    public static VLIW_Instruction Branch(
        Instruction opcode,
        int sourceRegister1,
        int sourceRegister2,
        short relativeOffset)
    {
        return new VLIW_Instruction
        {
            OpCode = (uint)opcode,
            DataTypeValue = DataTypeEnum.INT64,
            PredicateMask = 0,
            Immediate = unchecked((ushort)relativeOffset),
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(
                VLIW_Instruction.NoArchReg,
                checked((byte)sourceRegister1),
                checked((byte)sourceRegister2))
        };
    }

    public static VLIW_Instruction LoadDoubleword(int destinationRegister, ulong address)
    {
        return new VLIW_Instruction
        {
            OpCode = (uint)Instruction.LD,
            DataTypeValue = DataTypeEnum.INT64,
            PredicateMask = 0,
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(
                checked((byte)destinationRegister),
                VLIW_Instruction.NoArchReg,
                VLIW_Instruction.NoArchReg),
            Src2Pointer = address
        };
    }

    public static VLIW_Instruction StoreDoubleword(int sourceRegister, ulong address)
    {
        return new VLIW_Instruction
        {
            OpCode = (uint)Instruction.SD,
            DataTypeValue = DataTypeEnum.INT64,
            PredicateMask = 0,
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(
                VLIW_Instruction.NoArchReg,
                VLIW_Instruction.NoArchReg,
                checked((byte)sourceRegister)),
            Src2Pointer = address
        };
    }

    public static VLIW_Instruction Atomic(
        Instruction opcode,
        DataTypeEnum dataType,
        int destinationRegister,
        int addressRegister,
        int sourceRegister = 0,
        bool acquire = false,
        bool release = false)
    {
        return new VLIW_Instruction
        {
            OpCode = (uint)opcode,
            DataTypeValue = dataType,
            Acquire = acquire,
            Release = release,
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(
                checked((byte)destinationRegister),
                checked((byte)addressRegister),
                checked((byte)sourceRegister))
        };
    }

    public static VLIW_Instruction AtomicWord(
        Instruction opcode,
        int destinationRegister,
        int addressRegister,
        int sourceRegister = 0,
        bool acquire = false,
        bool release = false) =>
        Atomic(
            opcode,
            DataTypeEnum.INT32,
            destinationRegister,
            addressRegister,
            sourceRegister,
            acquire,
            release);

    public static VLIW_Instruction AtomicDoubleword(
        Instruction opcode,
        int destinationRegister,
        int addressRegister,
        int sourceRegister = 0,
        bool acquire = false,
        bool release = false) =>
        Atomic(
            opcode,
            DataTypeEnum.INT64,
            destinationRegister,
            addressRegister,
            sourceRegister,
            acquire,
            release);

    public static VLIW_Instruction Vector1D(
        Instruction opcode,
        ulong destinationAndSource1Address,
        ulong source2Address,
        ulong elementCount,
        ushort stride = sizeof(ulong))
    {
        return InstructionEncoder.EncodeVector1D(
            (uint)opcode,
            DataTypeEnum.UINT64,
            destinationAndSource1Address,
            source2Address,
            elementCount,
            stride);
    }

    public static VLIW_Instruction VectorReduction(
        Instruction opcode,
        ulong sourceAddress,
        int destinationRegister,
        ulong elementCount,
        ushort stride = sizeof(ulong))
    {
        return InstructionEncoder.EncodeVectorReduction(
            (uint)opcode,
            DataTypeEnum.UINT64,
            sourceAddress,
            checked((ushort)destinationRegister),
            elementCount,
            stride);
    }

    public static VLIW_Instruction Fence()
    {
        return new VLIW_Instruction
        {
            OpCode = (uint)Instruction.FENCE,
            PredicateMask = 0
        };
    }

    public static VLIW_Instruction FenceI()
    {
        return new VLIW_Instruction
        {
            OpCode = (uint)Instruction.FENCE_I,
            PredicateMask = 0
        };
    }

    public static VLIW_Instruction[] WithFences(params VLIW_Instruction[] instructions)
    {
        List<VLIW_Instruction> result = new List<VLIW_Instruction>(instructions.Length * 2);
        foreach (VLIW_Instruction instruction in instructions)
        {
            result.Add(instruction);
            result.Add(Fence());
        }

        return result.ToArray();
    }
}
