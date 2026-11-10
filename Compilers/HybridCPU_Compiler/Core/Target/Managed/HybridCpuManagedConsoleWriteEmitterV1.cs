using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>x10=receiver, x11=exact immutable String. Exposes only its bounded UTF-16 payload.</summary>
public static class HybridCpuManagedConsoleWriteEmitterV1
{
    public const string Symbol="__hybridcpu_managed_guest_console_write_utf16";
    public const string ModuleIdentity="hybridcpu.managed-runtime.guest-console-write/v1";

    public static byte[] Emit()=>EmitFor(HybridCpuConsoleServiceContractV1.WriteUtf16Operation);
    internal static byte[] EmitFor(ulong operation)
    {
        List<HybridCpuInstructionWord> code =new List<HybridCpuInstructionWord>(); Dictionary<string, int> labels =new Dictionary<string,int>(); List<(int, string)> fixups =new List<(int,string)>();
        void Op(HybridCpuOpcode op,byte rd,byte rs1,byte rs2=HybridCpuInstructionWord.NoArchReg,short immediate=0)=>code.Add(new(){OpCode=(uint)op,DataTypeValue=HybridCpuDataType.INT64,PredicateMask=byte.MaxValue,Word1=HybridCpuInstructionWord.PackArchRegs(rd,rs1,rs2),Immediate=unchecked((ushort)immediate)});
        void Mark(string name)=>labels.Add(name,code.Count);
        void Branch(HybridCpuOpcode op,byte a,byte b,string label){fixups.Add((code.Count,label));Op(op,HybridCpuInstructionWord.NoArchReg,a,b,1);}
        void Jump(string label){fixups.Add((code.Count,label));Op(HybridCpuOpcode.JAL,0,HybridCpuInstructionWord.NoArchReg,immediate:1);}
        void Constant(byte rd,ulong value){byte[] bytes=BitConverter.GetBytes(value);int high=Array.FindLastIndex(bytes,static b=>b!=0);if(high<0){Op(HybridCpuOpcode.ADDI,rd,0);return;}Op(HybridCpuOpcode.ADDI,rd,0,immediate:bytes[high]);for(int i=high-1;i>=0;i--){Op(HybridCpuOpcode.SLLI,rd,rd,immediate:8);if(bytes[i]!=0)Op(HybridCpuOpcode.ORI,rd,rd,immediate:bytes[i]);}}
        Op(HybridCpuOpcode.ADDI,28,11); Branch(HybridCpuOpcode.BEQ,28,0,"failure");
        Op(HybridCpuOpcode.ADDI,29,28,immediate:HybridCpuConsoleServiceContractV1.StringLengthOffsetBytes); Op(HybridCpuOpcode.LW,29,29);
        Branch(HybridCpuOpcode.BLT,29,0,"failure"); Constant(30,HybridCpuConsoleServiceContractV1.MaximumCodeUnits);
        Branch(HybridCpuOpcode.BLTU,30,29,"failure"); Branch(HybridCpuOpcode.BEQ,29,0,"empty");
        Op(HybridCpuOpcode.ADDI,12,28,immediate:HybridCpuConsoleServiceContractV1.StringDataOffsetBytes);
        Op(HybridCpuOpcode.SLLI,13,29,immediate:1); Op(HybridCpuOpcode.ADDI,14,0,immediate:(short)HybridCpuHostBufferAccessV1.Read); Jump("envelope");
        Mark("empty"); Op(HybridCpuOpcode.ADDI,12,0); Op(HybridCpuOpcode.ADDI,13,0); Op(HybridCpuOpcode.ADDI,14,0);
        Mark("envelope"); Constant(17,HybridCpuExternalServiceEcallContractV1.EcallNumber); Constant(10,(ulong)HybridCpuHostServiceV1.Console);
        Constant(11,operation); Op(HybridCpuOpcode.ADDI,15,0); Op(HybridCpuOpcode.ADDI,16,0);
        Op(HybridCpuOpcode.ECALL,HybridCpuInstructionWord.NoArchReg,HybridCpuInstructionWord.NoArchReg);
        Branch(HybridCpuOpcode.BEQ,11,0,"success");
        Mark("failure"); Op(HybridCpuOpcode.ADDI,10,0,immediate:255); Op(HybridCpuOpcode.ADDI,5,3,immediate:HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset); Op(HybridCpuOpcode.LD,5,5); Op(HybridCpuOpcode.JALR,0,5);
        Mark("success"); Op(HybridCpuOpcode.JALR,0,1,immediate:HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);
        foreach((int index,string label) in fixups){ HybridCpuInstructionWord word =code[index];word.Immediate=unchecked((ushort)checked((short)((labels[label]-index)*HybridCpuBundleSerializer.BundleSizeBytes)));code[index]=word;}
        return new HybridCpuBundleSerializer().SerializeProgram(code.Select(w=>{ HybridCpuInstructionBundle b =new HybridCpuInstructionBundle();b.SetInstruction(0,w);return b;}).ToArray());
    }

    public static HybridCpuObjectArtifactV1 EmitObject(){byte[] code=Emit();return new HybridCpuObjectWriterV1().Write(new([new(".text",HybridCpuObjectSectionKind.Code,HybridCpuBundleSerializer.BundleSizeBytes,code,(ulong)code.Length)],[new(Symbol,HybridCpuSymbolBinding.Global,HybridCpuSymbolVisibility.Hidden,".text",0,(ulong)code.Length,true)],[],HybridCpuTargetPlatformContractV1.Default.ContractDigest,HybridCpuManagedAbiFamilyV1.Default.ContractDigest));}
}
