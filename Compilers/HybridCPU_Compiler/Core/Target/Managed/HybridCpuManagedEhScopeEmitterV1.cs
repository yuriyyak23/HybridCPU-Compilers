using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedEhScopeEmitterV1
{
    public const string RethrowSymbol = "__hybridcpu_managed_rethrow";
    public const string LeaveCatchSymbol = "__hybridcpu_managed_eh_leave_catch";
    public const string RethrowModuleIdentity = "hybridcpu.managed-runtime.rethrow/v1";
    public const string LeaveModuleIdentity = "hybridcpu.managed-runtime.leave-catch/v1";

    public static byte[] EmitRethrow()
    {
        List<HybridCpuInstructionWord> code = new List<HybridCpuInstructionWord>();
        Op(HybridCpuOpcode.ADDI, 28, 3, immediate: HybridCpuManagedEhDispatchIndexV1.ExceptionStateOffset);
        Op(HybridCpuOpcode.LD, 28, 28);
        Op(HybridCpuOpcode.LD, 10, 28);
        Op(HybridCpuOpcode.ADDI, 5, 3, immediate: HybridCpuManagedEhDispatchIndexV1.CatchDispatchHelperOffset);
        Op(HybridCpuOpcode.LD, 5, 5);
        Op(HybridCpuOpcode.JALR, 0, 5); // tail-dispatch; preserves the rethrow call PC in x1
        return Serialize(code);
        void Op(HybridCpuOpcode opcode, byte rd, byte rs1, byte rs2 = HybridCpuInstructionWord.NoArchReg, short immediate = 0) =>
            code.Add(Word(opcode, rd, rs1, rs2, immediate));
    }

    public static byte[] EmitLeaveCatch()
    {
        List<HybridCpuInstructionWord> code = new List<HybridCpuInstructionWord>();
        code.Add(Word(HybridCpuOpcode.ADDI, 28, 3, immediate: HybridCpuManagedEhDispatchIndexV1.ExceptionStateOffset));
        code.Add(Word(HybridCpuOpcode.LD, 28, 28));
        code.Add(Word(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 28, 0));
        code.Add(Word(HybridCpuOpcode.ADDI, 28, 28, immediate: 8));
        code.Add(Word(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 28, 0));
        code.Add(Word(HybridCpuOpcode.JALR, 0, 1, immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes));
        return Serialize(code);
    }

    public static HybridCpuObjectArtifactV1 EmitRethrowObject() => Object(RethrowSymbol, EmitRethrow());
    public static HybridCpuObjectArtifactV1 EmitLeaveCatchObject() => Object(LeaveCatchSymbol, EmitLeaveCatch());

    private static HybridCpuInstructionWord Word(HybridCpuOpcode opcode, byte rd, byte rs1,
        byte rs2 = HybridCpuInstructionWord.NoArchReg, short immediate = 0) => new()
    {
        OpCode = (uint)opcode, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
        Word1 = HybridCpuInstructionWord.PackArchRegs(rd, rs1, rs2), Immediate = unchecked((ushort)immediate)
    };
    private static byte[] Serialize(IEnumerable<HybridCpuInstructionWord> code) =>
        new HybridCpuBundleSerializer().SerializeProgram(code.Select(instruction =>
        { HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle(); bundle.SetInstruction(0, instruction); return bundle; }).ToArray());
    private static HybridCpuObjectArtifactV1 Object(string symbol, byte[] code) => new HybridCpuObjectWriterV1().Write(new(
        [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes, code, (ulong)code.Length)],
        [new(symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, ".text", 0, (ulong)code.Length, true)], [],
        HybridCpuTargetPlatformContractV1.Default.ContractDigest, HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
}
