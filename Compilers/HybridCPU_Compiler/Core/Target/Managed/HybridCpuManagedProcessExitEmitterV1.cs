using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>
/// Restricted-image process boundary. x10 is the canonical int32 exit code. The helper
/// emits the exact user-mode ProcessExit ECALL envelope; the retire-time ISE bridge owns
/// the terminal RuntimeKernel transition. A returned ECALL loops fail closed.
/// </summary>
public static class HybridCpuManagedProcessExitEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_process_exit_unhandled";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.process-exit/v1";

    public static byte[] Emit()
    {
        List<HybridCpuInstructionWord> code = new List<HybridCpuInstructionWord>();
        void Op(HybridCpuOpcode opcode, byte rd, byte rs1, byte rs2 = HybridCpuInstructionWord.NoArchReg,
            short immediate = 0) => code.Add(new()
        {
            OpCode = (uint)opcode, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(rd, rs1, rs2), Immediate = unchecked((ushort)immediate)
        });
        void Constant(byte destination, ulong value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            int high = Array.FindLastIndex(bytes, static item => item != 0);
            Op(HybridCpuOpcode.ADDI, destination, 0, immediate: high < 0 ? (short)0 : bytes[high]);
            for (int index = high - 1; index >= 0; index--)
            {
                Op(HybridCpuOpcode.SLLI, destination, destination, immediate: 8);
                if (bytes[index] != 0) Op(HybridCpuOpcode.ORI, destination, destination, immediate: bytes[index]);
            }
        }
        Op(HybridCpuOpcode.ADDI, 16, 10); // preserve exit code as argument0
        Constant(17, HybridCpuExternalServiceEcallContractV1.EcallNumber);
        Op(HybridCpuOpcode.ADDI, 10, 0, immediate: (short)HybridCpuHostServiceV1.ProcessExit);
        Op(HybridCpuOpcode.ADDI, 11, 0); // operation
        Op(HybridCpuOpcode.ADDI, 12, 0); // buffer
        Op(HybridCpuOpcode.ADDI, 13, 0); // length
        Op(HybridCpuOpcode.ADDI, 14, 0, immediate: (short)HybridCpuHostBufferAccessV1.None);
        Op(HybridCpuOpcode.ADDI, 15, 0, immediate: 1);
        Op(HybridCpuOpcode.ADDI, 18, 0);
        Op(HybridCpuOpcode.ADDI, 19, 0);
        Op(HybridCpuOpcode.ECALL, HybridCpuInstructionWord.NoArchReg, HybridCpuInstructionWord.NoArchReg);
        Op(HybridCpuOpcode.JAL, 0, HybridCpuInstructionWord.NoArchReg, immediate: 0); // terminal ECALL must not return
        return new HybridCpuBundleSerializer().SerializeProgram(code.Select(instruction =>
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle(); bundle.SetInstruction(0, instruction); return bundle;
        }).ToArray());
    }

    public static HybridCpuObjectArtifactV1 EmitObject()
    {
        byte[] code = Emit();
        return new HybridCpuObjectWriterV1().Write(new(
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes, code, (ulong)code.Length)],
            [new(Symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, ".text", 0, (ulong)code.Length, true)], [],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest, HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
    }
}
