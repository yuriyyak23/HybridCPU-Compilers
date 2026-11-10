using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;
namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedPaletteEmitterV1
{
    public const string Symbol="__hybridcpu_managed_guest_update_palette";
    public const string ModuleIdentity="hybridcpu.managed-runtime.guest-palette/v1";
    public static byte[] Emit()
    {
        List<HybridCpuInstructionWord> code =new List<HybridCpuInstructionWord>(); Dictionary<string, int> labels =new Dictionary<string,int>(); List<(int, string)> fixups =new List<(int,string)>();
        void Op(HybridCpuOpcode op,byte rd,byte rs1,byte rs2=HybridCpuInstructionWord.NoArchReg,short imm=0)=>code.Add(new(){OpCode=(uint)op,DataTypeValue=HybridCpuDataType.INT64,PredicateMask=byte.MaxValue,Word1=HybridCpuInstructionWord.PackArchRegs(rd,rs1,rs2),Immediate=unchecked((ushort)imm)});
        void Mark(string l)=>labels.Add(l,code.Count);void Branch(HybridCpuOpcode op,byte a,byte b,string l){fixups.Add((code.Count,l));Op(op,HybridCpuInstructionWord.NoArchReg,a,b,1);}
        void Constant(byte rd,ulong v){byte[] b=BitConverter.GetBytes(v);int h=Array.FindLastIndex(b,static x=>x!=0);if(h<0){Op(HybridCpuOpcode.ADDI,rd,0);return;}Op(HybridCpuOpcode.ADDI,rd,0,imm:b[h]);for(int i=h-1;i>=0;i--){Op(HybridCpuOpcode.SLLI,rd,rd,imm:8);if(b[i]!=0)Op(HybridCpuOpcode.ORI,rd,rd,imm:b[i]);}}
        Op(HybridCpuOpcode.ADDI,28,11);Branch(HybridCpuOpcode.BEQ,28,0,"failure");Op(HybridCpuOpcode.ADDI,29,28,imm:HybridCpuManagedFramebufferPresentEmitterV1.ArrayLengthOffsetBytes);Op(HybridCpuOpcode.LW,29,29);
        Constant(30,HybridCpuFramebufferServiceContractV1.PaletteBytes);Branch(HybridCpuOpcode.BNE,29,30,"failure");
        Op(HybridCpuOpcode.ADDI,12,28,imm:HybridCpuManagedFramebufferPresentEmitterV1.ArrayDataOffsetBytes);Op(HybridCpuOpcode.ADDI,13,29);Op(HybridCpuOpcode.ADDI,14,0,imm:(short)HybridCpuHostBufferAccessV1.Read);
        Constant(17,HybridCpuExternalServiceEcallContractV1.EcallNumber);Constant(10,(ulong)HybridCpuHostServiceV1.Graphics);Constant(11,HybridCpuFramebufferServiceContractV1.UpdatePaletteOperation);Op(HybridCpuOpcode.ADDI,15,0);Op(HybridCpuOpcode.ADDI,16,0);
        Op(HybridCpuOpcode.ECALL,HybridCpuInstructionWord.NoArchReg,HybridCpuInstructionWord.NoArchReg);Branch(HybridCpuOpcode.BEQ,11,0,"success");
        Mark("failure");Op(HybridCpuOpcode.ADDI,10,0,imm:255);Op(HybridCpuOpcode.ADDI,5,3,imm:HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset);Op(HybridCpuOpcode.LD,5,5);Op(HybridCpuOpcode.JALR,0,5);
        Mark("success");Op(HybridCpuOpcode.JALR,0,1,imm:HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);
        foreach((int i,string l)in fixups){ HybridCpuInstructionWord w =code[i];w.Immediate=unchecked((ushort)checked((short)((labels[l]-i)*HybridCpuBundleSerializer.BundleSizeBytes)));code[i]=w;}
        return new HybridCpuBundleSerializer().SerializeProgram(code.Select(w=>{ HybridCpuInstructionBundle b =new HybridCpuInstructionBundle();b.SetInstruction(0,w);return b;}).ToArray());
    }
    public static HybridCpuObjectArtifactV1 EmitObject(){byte[] c=Emit();return new HybridCpuObjectWriterV1().Write(new([new(".text",HybridCpuObjectSectionKind.Code,HybridCpuBundleSerializer.BundleSizeBytes,c,(ulong)c.Length)],[new(Symbol,HybridCpuSymbolBinding.Global,HybridCpuSymbolVisibility.Hidden,".text",0,(ulong)c.Length,true)],[],HybridCpuTargetPlatformContractV1.Default.ContractDigest,HybridCpuManagedAbiFamilyV1.Default.ContractDigest));}
}
