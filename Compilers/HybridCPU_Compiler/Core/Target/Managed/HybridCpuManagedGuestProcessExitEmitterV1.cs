using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>x10=service receiver, x11=exit code. Tail-enters the non-trap restricted process boundary.</summary>
public static class HybridCpuManagedGuestProcessExitEmitterV1
{
    public const string Symbol="__hybridcpu_managed_guest_process_exit";
    public const string ModuleIdentity="hybridcpu.managed-runtime.guest-process-exit/v1";
    public static byte[] Emit()
    {
        HybridCpuInstructionWord Word(HybridCpuOpcode op,byte rd,byte rs1,byte rs2=HybridCpuInstructionWord.NoArchReg,short imm=0)=>new(){OpCode=(uint)op,DataTypeValue=HybridCpuDataType.INT64,PredicateMask=byte.MaxValue,Word1=HybridCpuInstructionWord.PackArchRegs(rd,rs1,rs2),Immediate=unchecked((ushort)imm)};
        HybridCpuInstructionWord[] code=[
            Word(HybridCpuOpcode.ADDIW,10,11),
            Word(HybridCpuOpcode.ADDI,5,3,imm:HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset),
            Word(HybridCpuOpcode.LD,5,5),
            Word(HybridCpuOpcode.JALR,0,5)];
        return new HybridCpuBundleSerializer().SerializeProgram(code.Select(w=>{ HybridCpuInstructionBundle b =new HybridCpuInstructionBundle();b.SetInstruction(0,w);return b;}).ToArray());
    }
    public static HybridCpuObjectArtifactV1 EmitObject(){byte[] code=Emit();return new HybridCpuObjectWriterV1().Write(new([new(".text",HybridCpuObjectSectionKind.Code,HybridCpuBundleSerializer.BundleSizeBytes,code,(ulong)code.Length)],[new(Symbol,HybridCpuSymbolBinding.Global,HybridCpuSymbolVisibility.Hidden,".text",0,(ulong)code.Length,true)],[],HybridCpuTargetPlatformContractV1.Default.ContractDigest,HybridCpuManagedAbiFamilyV1.Default.ContractDigest));}
}
