using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>x10=receiver, x11=target Doom tic. A Parked ECALL must not be resumed before kernel wake.</summary>
public static class HybridCpuManagedDoomWaitEmitterV1
{
    public const string Symbol="__hybridcpu_managed_guest_wait_until_doom_tic";
    public const string ModuleIdentity="hybridcpu.managed-runtime.guest-doom-wait/v1";
    public static byte[] Emit()
    {
        List<HybridCpuInstructionWord> code =new List<HybridCpuInstructionWord>();
        void Op(HybridCpuOpcode op,byte rd,byte rs1,byte rs2=HybridCpuInstructionWord.NoArchReg,short immediate=0)=>code.Add(new(){OpCode=(uint)op,DataTypeValue=HybridCpuDataType.INT64,PredicateMask=byte.MaxValue,Word1=HybridCpuInstructionWord.PackArchRegs(rd,rs1,rs2),Immediate=unchecked((ushort)immediate)});
        void Constant(byte rd,ulong value){byte[] b=BitConverter.GetBytes(value);int h=Array.FindLastIndex(b,static x=>x!=0);if(h<0){Op(HybridCpuOpcode.ADDI,rd,0);return;}Op(HybridCpuOpcode.ADDI,rd,0,immediate:b[h]);for(int i=h-1;i>=0;i--){Op(HybridCpuOpcode.SLLI,rd,rd,immediate:8);if(b[i]!=0)Op(HybridCpuOpcode.ORI,rd,rd,immediate:b[i]);}}
        Op(HybridCpuOpcode.ADDIW,16,11); Constant(17,HybridCpuExternalServiceEcallContractV1.EcallNumber);
        Constant(10,(ulong)HybridCpuHostServiceV1.Clock); Constant(11,HybridCpuVirtualClockServiceContractV1.WaitUntilDoomTicOperation);
        Op(HybridCpuOpcode.ADDI,12,0);Op(HybridCpuOpcode.ADDI,13,0);Op(HybridCpuOpcode.ADDI,14,0);Op(HybridCpuOpcode.ADDI,15,0,immediate:1);
        Op(HybridCpuOpcode.ECALL,HybridCpuInstructionWord.NoArchReg,HybridCpuInstructionWord.NoArchReg);
        int branch=code.Count;Op(HybridCpuOpcode.BEQ,HybridCpuInstructionWord.NoArchReg,11,0,1);
        Op(HybridCpuOpcode.ADDI,10,0,immediate:255);Op(HybridCpuOpcode.ADDI,5,3,immediate:HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset);Op(HybridCpuOpcode.LD,5,5);Op(HybridCpuOpcode.JALR,0,5);
        int success=code.Count;Op(HybridCpuOpcode.JALR,0,1,immediate:HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);
        HybridCpuInstructionWord word =code[branch];word.Immediate=unchecked((ushort)checked((short)((success-branch)*HybridCpuBundleSerializer.BundleSizeBytes)));code[branch]=word;
        return new HybridCpuBundleSerializer().SerializeProgram(code.Select(w=>{ HybridCpuInstructionBundle bundle =new HybridCpuInstructionBundle();bundle.SetInstruction(0,w);return bundle;}).ToArray());
    }
    public static HybridCpuObjectArtifactV1 EmitObject(){byte[] code=Emit();return new HybridCpuObjectWriterV1().Write(new([new(".text",HybridCpuObjectSectionKind.Code,HybridCpuBundleSerializer.BundleSizeBytes,code,(ulong)code.Length)],[new(Symbol,HybridCpuSymbolBinding.Global,HybridCpuSymbolVisibility.Hidden,".text",0,(ulong)code.Length,true)],[],HybridCpuTargetPlatformContractV1.Default.ContractDigest,HybridCpuManagedAbiFamilyV1.Default.ContractDigest));}
}
