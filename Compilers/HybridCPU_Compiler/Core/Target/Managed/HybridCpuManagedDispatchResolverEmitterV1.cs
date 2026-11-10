using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>Closed-world interface lookup. x10=receiver, x11=interface TypeId, x12=slot;
/// returns the exact linker-owned implementation address or zero for invalid/corrupt input.</summary>
public static class HybridCpuManagedDispatchResolverEmitterV1
{
    public const string VirtualSymbol = "__hybridcpu_managed_resolve_virtual";
    public const string InterfaceSymbol = "__hybridcpu_managed_resolve_interface";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.resolve-interface/v1";

    public static byte[] EmitInterface()
    {
        List<HybridCpuInstructionWord> code = new List<HybridCpuInstructionWord>();
        Dictionary<string, int> labels = new Dictionary<string, int>(StringComparer.Ordinal);
        List<(int, string)> branches = new List<(int, string)>();
        void Mark(string label) => labels.Add(label, code.Count);
        void Op(HybridCpuOpcode opcode, byte rd, byte rs1, byte rs2 = HybridCpuInstructionWord.NoArchReg, short immediate = 0) => code.Add(new()
        { OpCode=(uint)opcode, DataTypeValue=HybridCpuDataType.INT64, PredicateMask=byte.MaxValue,
          Word1=HybridCpuInstructionWord.PackArchRegs(rd,rs1,rs2), Immediate=unchecked((ushort)immediate) });
        void Branch(HybridCpuOpcode opcode, byte a, byte b, string label)
        { branches.Add((code.Count,label)); Op(opcode, HybridCpuInstructionWord.NoArchReg,a,b,1); }
        void ReturnNull() { Op(HybridCpuOpcode.ADDI,10,0); Op(HybridCpuOpcode.JALR,0,1, immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes); }
        void Load(byte destination, byte basis, short offset)
        {
            Op(HybridCpuOpcode.ADDI,30,basis, immediate:offset);
            Op(HybridCpuOpcode.LD,destination,30);
        }

        Branch(HybridCpuOpcode.BNE,10,0,"receiver"); ReturnNull();
        Mark("receiver"); Op(HybridCpuOpcode.LD,13,10); Branch(HybridCpuOpcode.BNE,13,0,"handle"); ReturnNull();
        Mark("handle");
        Op(HybridCpuOpcode.ADDI,14,3, immediate: HybridCpuManagedEhDispatchIndexV1.TypeCountOffset); Op(HybridCpuOpcode.LW,14,14);
        Branch(HybridCpuOpcode.BLTU,14,13,"invalid");
        Op(HybridCpuOpcode.ADDI,13,13, immediate:-1); Op(HybridCpuOpcode.SLLI,13,13, immediate:5);
        Op(HybridCpuOpcode.ADDI,15,3, immediate: HybridCpuManagedEhDispatchIndexV1.CountOffset); Op(HybridCpuOpcode.LW,15,15);
        // Skip fixed method rows: RowSize=360 = 256+64+32+8.
        Op(HybridCpuOpcode.SLLI,16,15, immediate:8); Op(HybridCpuOpcode.SLLI,17,15, immediate:6);
        Op(HybridCpuOpcode.ADD,16,16,17); Op(HybridCpuOpcode.SLLI,17,15, immediate:5); Op(HybridCpuOpcode.ADD,16,16,17);
        Op(HybridCpuOpcode.SLLI,17,15, immediate:3); Op(HybridCpuOpcode.ADD,16,16,17);
        Op(HybridCpuOpcode.ADD,16,3,16); Op(HybridCpuOpcode.ADDI,16,16, immediate: HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes);
        Op(HybridCpuOpcode.ADD,16,16,13); Load(13,16, HybridCpuManagedEhDispatchIndexV1.TypeIdOffset);
        Op(HybridCpuOpcode.ADDI,5,3, immediate: HybridCpuManagedEhDispatchIndexV1.DispatchMetadataOffset); Op(HybridCpuOpcode.LD,5,5);
        Branch(HybridCpuOpcode.BNE,5,0,"metadata"); ReturnNull();
        Mark("metadata");
        Op(HybridCpuOpcode.ADDI,6,5, immediate: HybridCpuManagedDispatchMetadataEmitterV1.MethodCountOffset); Op(HybridCpuOpcode.LW,6,6);
        Op(HybridCpuOpcode.ADDI,7,5, immediate: HybridCpuManagedDispatchMetadataEmitterV1.VirtualCountOffset); Op(HybridCpuOpcode.LW,7,7);
        Op(HybridCpuOpcode.ADDI,28,5, immediate: HybridCpuManagedDispatchMetadataEmitterV1.InterfaceCountOffset); Op(HybridCpuOpcode.LW,28,28);
        Op(HybridCpuOpcode.SLLI,29,6, immediate:4); Op(HybridCpuOpcode.SLLI,14,6, immediate:3); Op(HybridCpuOpcode.ADD,29,29,14);
        Op(HybridCpuOpcode.SLLI,14,7, immediate:5); Op(HybridCpuOpcode.ADD,29,29,14);
        Op(HybridCpuOpcode.ADD,29,5,29); Op(HybridCpuOpcode.ADDI,29,29, immediate: HybridCpuManagedDispatchMetadataEmitterV1.HeaderBytes);
        Mark("loop"); Branch(HybridCpuOpcode.BNE,28,0,"row"); ReturnNull();
        Mark("row"); Op(HybridCpuOpcode.LD,14,29); Branch(HybridCpuOpcode.BNE,14,13,"next");
        Load(14,29,8); Branch(HybridCpuOpcode.BNE,14,11,"next");
        Load(14,29,16); Branch(HybridCpuOpcode.BNE,14,12,"next");
        Load(10,29,32); Op(HybridCpuOpcode.JALR,0,1, immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);
        Mark("next"); Op(HybridCpuOpcode.ADDI,29,29, immediate: HybridCpuManagedDispatchMetadataEmitterV1.InterfaceRowBytes);
        Op(HybridCpuOpcode.ADDI,28,28, immediate:-1); Op(HybridCpuOpcode.JAL,0, HybridCpuInstructionWord.NoArchReg, immediate:1);
        branches.Add((code.Count-1,"loop"));
        Mark("invalid"); ReturnNull();
        foreach ((int index,string label) in branches)
        { int d=checked((labels[label]-index)*HybridCpuBundleSerializer.BundleSizeBytes); HybridCpuInstructionWord w =code[index]; w.Immediate=unchecked((ushort)checked((short)d)); code[index]=w; }
        return new HybridCpuBundleSerializer().SerializeProgram(code.Select(w=>{ HybridCpuInstructionBundle b =new HybridCpuInstructionBundle();b.SetInstruction(0,w);return b;}).ToArray());
    }

    public static HybridCpuObjectArtifactV1 EmitInterfaceObject()
    {
        byte[] code=EmitInterface();
        return new HybridCpuObjectWriterV1().Write(new(
            [new(".text",HybridCpuObjectSectionKind.Code,HybridCpuBundleSerializer.BundleSizeBytes,code,(ulong)code.Length)],
            [new(InterfaceSymbol,HybridCpuSymbolBinding.Global,HybridCpuSymbolVisibility.Default,".text",0,(ulong)code.Length,true)],[],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
    }

    /// <summary>x10=receiver, x11=slot; returns exact implementation address or zero.</summary>
    public static byte[] EmitVirtual()
    {
        List<HybridCpuInstructionWord> code =new List<HybridCpuInstructionWord>(); Dictionary<string, int> labels =new Dictionary<string,int>(); List<(int, string)> branches =new List<(int,string)>();
        void Mark(string l)=>labels.Add(l,code.Count);void Op(HybridCpuOpcode op,byte rd,byte a,byte b=HybridCpuInstructionWord.NoArchReg,short imm=0)=>code.Add(new(){OpCode=(uint)op,DataTypeValue=HybridCpuDataType.INT64,PredicateMask=byte.MaxValue,Word1=HybridCpuInstructionWord.PackArchRegs(rd,a,b),Immediate=unchecked((ushort)imm)});
        void Branch(HybridCpuOpcode op,byte a,byte b,string l){branches.Add((code.Count,l));Op(op,HybridCpuInstructionWord.NoArchReg,a,b,1);}void Null(){Op(HybridCpuOpcode.ADDI,10,0);Op(HybridCpuOpcode.JALR,0,1,imm:HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);}
        void Load(byte d,byte a,short o){Op(HybridCpuOpcode.ADDI,30,a,imm:o);Op(HybridCpuOpcode.LD,d,30);}
        Branch(HybridCpuOpcode.BNE,10,0,"receiver");Null();Mark("receiver");Op(HybridCpuOpcode.LD,13,10);Branch(HybridCpuOpcode.BNE,13,0,"handle");Null();Mark("handle");
        Op(HybridCpuOpcode.ADDI,14,3,imm:HybridCpuManagedEhDispatchIndexV1.TypeCountOffset);Op(HybridCpuOpcode.LW,14,14);Branch(HybridCpuOpcode.BLTU,14,13,"invalid");
        Op(HybridCpuOpcode.ADDI,13,13,imm:-1);Op(HybridCpuOpcode.SLLI,13,13,imm:5);Op(HybridCpuOpcode.ADDI,15,3,imm:HybridCpuManagedEhDispatchIndexV1.CountOffset);Op(HybridCpuOpcode.LW,15,15);
        Op(HybridCpuOpcode.SLLI,16,15,imm:8);Op(HybridCpuOpcode.SLLI,17,15,imm:6);Op(HybridCpuOpcode.ADD,16,16,17);Op(HybridCpuOpcode.SLLI,17,15,imm:5);Op(HybridCpuOpcode.ADD,16,16,17);Op(HybridCpuOpcode.SLLI,17,15,imm:3);Op(HybridCpuOpcode.ADD,16,16,17);
        Op(HybridCpuOpcode.ADD,16,3,16);Op(HybridCpuOpcode.ADDI,16,16,imm:HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes);Op(HybridCpuOpcode.ADD,16,16,13);Load(13,16,HybridCpuManagedEhDispatchIndexV1.TypeIdOffset);
        Op(HybridCpuOpcode.ADDI,5,3,imm:HybridCpuManagedEhDispatchIndexV1.DispatchMetadataOffset);Op(HybridCpuOpcode.LD,5,5);Branch(HybridCpuOpcode.BNE,5,0,"metadata");Null();Mark("metadata");
        Op(HybridCpuOpcode.ADDI,6,5,imm:HybridCpuManagedDispatchMetadataEmitterV1.MethodCountOffset);Op(HybridCpuOpcode.LW,6,6);Op(HybridCpuOpcode.ADDI,28,5,imm:HybridCpuManagedDispatchMetadataEmitterV1.VirtualCountOffset);Op(HybridCpuOpcode.LW,28,28);
        Op(HybridCpuOpcode.SLLI,29,6,imm:4);Op(HybridCpuOpcode.SLLI,14,6,imm:3);Op(HybridCpuOpcode.ADD,29,29,14);Op(HybridCpuOpcode.ADD,29,5,29);Op(HybridCpuOpcode.ADDI,29,29,imm:HybridCpuManagedDispatchMetadataEmitterV1.HeaderBytes);
        Mark("loop");Branch(HybridCpuOpcode.BNE,28,0,"row");Null();Mark("row");Op(HybridCpuOpcode.LD,14,29);Branch(HybridCpuOpcode.BNE,14,13,"next");Load(14,29,8);Branch(HybridCpuOpcode.BNE,14,11,"next");Load(10,29,24);Op(HybridCpuOpcode.JALR,0,1,imm:HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);
        Mark("next");Op(HybridCpuOpcode.ADDI,29,29,imm:HybridCpuManagedDispatchMetadataEmitterV1.VirtualRowBytes);Op(HybridCpuOpcode.ADDI,28,28,imm:-1);branches.Add((code.Count,"loop"));Op(HybridCpuOpcode.JAL,0,HybridCpuInstructionWord.NoArchReg,imm:1);Mark("invalid");Null();
        foreach((int i,string l)in branches){ HybridCpuInstructionWord w =code[i];w.Immediate=unchecked((ushort)checked((short)((labels[l]-i)*HybridCpuBundleSerializer.BundleSizeBytes)));code[i]=w;}return new HybridCpuBundleSerializer().SerializeProgram(code.Select(w=>{ HybridCpuInstructionBundle b =new HybridCpuInstructionBundle();b.SetInstruction(0,w);return b;}).ToArray());
    }
    public static HybridCpuObjectArtifactV1 EmitVirtualObject(){byte[] c=EmitVirtual();return new HybridCpuObjectWriterV1().Write(new([new(".text",HybridCpuObjectSectionKind.Code,HybridCpuBundleSerializer.BundleSizeBytes,c,(ulong)c.Length)],[new(VirtualSymbol,HybridCpuSymbolBinding.Global,HybridCpuSymbolVisibility.Default,".text",0,(ulong)c.Length,true)],[],HybridCpuTargetPlatformContractV1.Default.ContractDigest,HybridCpuManagedAbiFamilyV1.Default.ContractDigest));}
}
