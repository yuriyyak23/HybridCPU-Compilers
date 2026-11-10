using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>x10=receiver, x11=width, x12=height. Emits the exact synchronous FBIN service envelope.</summary>
public static class HybridCpuManagedFramebufferEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_guest_initialize_framebuffer";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.guest-framebuffer-init/v1";

    public static byte[] Emit()
    {
        List<HybridCpuInstructionWord> code = new List<HybridCpuInstructionWord>();
        void Op(HybridCpuOpcode opcode, byte rd, byte rs1, byte rs2 = HybridCpuInstructionWord.NoArchReg,
            short immediate = 0) => code.Add(new()
            { OpCode=(uint)opcode, DataTypeValue=HybridCpuDataType.INT64, PredicateMask=byte.MaxValue,
              Word1=HybridCpuInstructionWord.PackArchRegs(rd,rs1,rs2), Immediate=unchecked((ushort)immediate) });
        void Constant(byte rd, ulong value)
        {
            byte[] bytes=BitConverter.GetBytes(value); int high=Array.FindLastIndex(bytes, static b=>b!=0);
            if (high<0) { Op(HybridCpuOpcode.ADDI,rd,0); return; }
            Op(HybridCpuOpcode.ADDI,rd,0,immediate:bytes[high]);
            for (int i=high-1;i>=0;i--) { Op(HybridCpuOpcode.SLLI,rd,rd,immediate:8); if(bytes[i]!=0) Op(HybridCpuOpcode.ORI,rd,rd,immediate:bytes[i]); }
        }
        // Preserve exact low 32-bit dimension bit patterns in the sole scalar payload register.
        Constant(28, uint.MaxValue); Op(HybridCpuOpcode.AND,16,11,28);
        Op(HybridCpuOpcode.SLLI,29,12,immediate:32); Op(HybridCpuOpcode.OR,16,16,29);
        Constant(17,HybridCpuExternalServiceEcallContractV1.EcallNumber);
        Constant(10,(ulong)HybridCpuHostServiceV1.Graphics);
        Constant(11,HybridCpuFramebufferServiceContractV1.InitializeOperation);
        for(byte register=12;register<=14;register++) Op(HybridCpuOpcode.ADDI,register,0);
        Op(HybridCpuOpcode.ADDI,15,0,immediate:1);
        Op(HybridCpuOpcode.ECALL,HybridCpuInstructionWord.NoArchReg,HybridCpuInstructionWord.NoArchReg);
        int branch=code.Count; Op(HybridCpuOpcode.BEQ,HybridCpuInstructionWord.NoArchReg,11,0,1);
        Op(HybridCpuOpcode.ADDI,10,0,immediate:255);
        Op(HybridCpuOpcode.ADDI,5,3,immediate:HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset);
        Op(HybridCpuOpcode.LD,5,5); Op(HybridCpuOpcode.JALR,0,5);
        int success=code.Count; Op(HybridCpuOpcode.JALR,0,1,immediate:HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);
        HybridCpuInstructionWord word =code[branch]; word.Immediate=unchecked((ushort)checked((short)((success-branch)*HybridCpuBundleSerializer.BundleSizeBytes))); code[branch]=word;
        return new HybridCpuBundleSerializer().SerializeProgram(code.Select(w=>{ HybridCpuInstructionBundle b =new HybridCpuInstructionBundle();b.SetInstruction(0,w);return b;}).ToArray());
    }

    public static HybridCpuObjectArtifactV1 EmitObject()
    {
        byte[] code=Emit();
        return new HybridCpuObjectWriterV1().Write(new(
            [new(".text",HybridCpuObjectSectionKind.Code,HybridCpuBundleSerializer.BundleSizeBytes,code,(ulong)code.Length)],
            [new(Symbol,HybridCpuSymbolBinding.Global,HybridCpuSymbolVisibility.Hidden,".text",0,(ulong)code.Length,true)],[],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
    }
}
