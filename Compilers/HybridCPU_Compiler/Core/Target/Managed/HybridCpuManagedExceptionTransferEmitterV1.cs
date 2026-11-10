using HybridCPU.Compiler.Core.IR;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>Fixed native runtime leaf fragment for an already validated HCET record in x10.
/// Caller-saved x5/x6/x7 are scratch; x3/x4 retain the current process/thread context.
/// This emits transport bytes only, not an executable image or EH publication authority.</summary>
public static class HybridCpuManagedExceptionTransferEmitterV1
{
    public static byte[] Emit()
    {
        List<HybridCpuInstructionBundle> bundles = new List<HybridCpuInstructionBundle>();
        void Instruction(HybridCpuOpcode opcode, byte destination, byte source, ushort immediate = 0)
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            bundle.SetInstruction(0, new HybridCpuInstructionWord
            {
                OpCode = (uint)opcode, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
                Word1 = HybridCpuInstructionWord.PackArchRegs(destination, source, HybridCpuInstructionWord.NoArchReg),
                Immediate = immediate
            });
            bundles.Add(bundle);
        }
        void Load(byte destination, int offset)
        {
            // LD uses a computed address, not a RISC-V-style base+immediate assumption.
            Instruction(HybridCpuOpcode.ADDI, 7, 5, checked((ushort)offset));
            Instruction(HybridCpuOpcode.LD, destination, 7);
        }
        Instruction(HybridCpuOpcode.ADDI, 5, 10);
        Load(6, HybridCpuManagedExceptionTransferV1.ProgramCounterOffset);
        Load(1, HybridCpuManagedExceptionTransferV1.RegisterOffset(1));
        foreach (int register in HybridCpuNativeAbiContractV2.Default.CalleeSavedRegisters)
            Load(checked((byte)register), HybridCpuManagedExceptionTransferV1.RegisterOffset(register));
        Load(10, HybridCpuManagedExceptionTransferV1.RegisterOffset(10));
        Load(2, HybridCpuManagedExceptionTransferV1.RegisterOffset(2));
        // No link/return to the throwing frame. SP and all handler inputs are committed
        // in earlier bundles, with no same-bundle producer/consumer dependency.
        Instruction(HybridCpuOpcode.JALR, 0, 6);
        return new HybridCpuBundleSerializer().SerializeProgram(bundles);
    }
}
