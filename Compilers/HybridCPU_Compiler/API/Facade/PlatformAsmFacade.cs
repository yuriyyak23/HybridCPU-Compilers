using System.ComponentModel;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU.Compiler.Core.API.Facade;

/// <summary>
/// Compatibility platform-level assembly facade implementation for OS bring-up and system programming.
/// Extends <see cref="AppAsmFacade"/> with system-level operations: Vector (RVV) and canonical CSR/system control.
/// Does NOT expose typed-slot internals.
/// </summary>
[Obsolete(AsmFacadeDeprecation.Message)]
[EditorBrowsable(EditorBrowsableState.Never)]
public class PlatformAsmFacade : AppAsmFacade, IPlatformAsmFacade
{
    /// <summary>
    /// Creates a PlatformAsmFacade bound to a specific core and thread compiler context.
    /// </summary>
    public PlatformAsmFacade(int coreId, HybridCpuThreadCompilerContext context)
        : base(coreId, context)
    {
    }

    /// <summary>
    /// Creates a PlatformAsmFacade with an explicit compiler-visible scalar capability model.
    /// </summary>
    public PlatformAsmFacade(
        int coreId,
        HybridCpuThreadCompilerContext context,
        CompilerNonVmxScalarCapabilityModel scalarCapabilities)
        : base(coreId, context, scalarCapabilities)
    {
    }

    public void ReadSystemCycleCounter(AsmRegister dest)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarSystemCounter, "RDCYCLE");
        Context.CompileInstruction(
            (uint)Processor.CPU_Core.InstructionsEnum.RDCYCLE,
            (byte)DataTypeEnum.UINT64,
            0,
            0,
            VLIW_Instruction.PackArchRegs(
                Resolve(dest).Value,
                0,
                0),
            0,
            0,
            0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    public void CsrRead(AsmRegister dest, ushort csrAddr)
    {
        // Lower assembler-style csrr to canonical CSRRS rd, csr, x0.
        Context.CompileInstruction(
            (uint)Processor.CPU_Core.InstructionsEnum.CSRRS, 0, 0, csrAddr,
            VLIW_Instruction.PackArchRegs(
                Resolve(dest).Value,
                0,
                VLIW_Instruction.NoArchReg),
            0, 0, 0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    public void CsrWrite(ushort csrAddr, AsmRegister src)
    {
        // Lower assembler-style csrw to canonical CSRRW x0, csr, rs1.
        Context.CompileInstruction(
            (uint)Processor.CPU_Core.InstructionsEnum.CSRRW, 0, 0, csrAddr,
            VLIW_Instruction.PackArchRegs(
                VLIW_Instruction.NoArchReg,
                Resolve(src).Value,
                VLIW_Instruction.NoArchReg),
            0, 0, 0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    public void CsrClear()
    {
        Context.CompileInstruction(
            (uint)Processor.CPU_Core.InstructionsEnum.CSR_CLEAR, 0, 0, 0,
            0, 0, 0, 0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    public void VectorOp(
        Processor.CPU_Core.IsaOpcode op,
        DataTypeEnum dataType,
        ulong dest,
        ulong src,
        uint streamLength,
        ushort stride)
    {
        ValidateNotVectorTransferOpcode(op);
        Context.CompileInstruction(
            (uint)op, (byte)dataType, 0, 0,
            dest, src, streamLength, stride,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    public void VectorOpImm(
        Processor.CPU_Core.IsaOpcode op,
        DataTypeEnum dataType,
        ushort immediate,
        ulong dest,
        ulong src,
        uint streamLength,
        ushort stride)
    {
        ValidateNotVectorTransferOpcode(op);
        Context.CompileInstruction(
            (uint)op, (byte)dataType, 0, immediate,
            dest, src, streamLength, stride,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    public void VLoad(
        CompilerVectorTransferMemoryAddressAbi destination,
        CompilerVectorTransferMemoryAddressAbi source,
        CompilerVectorTransferShapeAbi shape)
    {
        Context.CompileVloadWithDecision(destination, source, shape);
    }

    public void VStore(
        CompilerVectorTransferMemoryAddressAbi source,
        CompilerVectorTransferMemoryAddressAbi destination,
        CompilerVectorTransferShapeAbi shape)
    {
        Context.CompileVstoreWithDecision(source, destination, shape);
    }

    public void VSetVli(AsmRegister vlReg, AsmRegister avlReg, DataTypeEnum dataType)
    {
        Context.CompileInstruction(
            (uint)Processor.CPU_Core.InstructionsEnum.VSETVLI, (byte)dataType, 0, 0,
            VLIW_Instruction.PackArchRegs(
                Resolve(vlReg).Value,
                Resolve(avlReg).Value,
                VLIW_Instruction.NoArchReg),
            0, 1, 0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    private static void ValidateNotVectorTransferOpcode(Processor.CPU_Core.IsaOpcode op)
    {
        if (op.ToInstructionsEnum() is Processor.CPU_Core.InstructionsEnum.VLOAD or Processor.CPU_Core.InstructionsEnum.VSTORE)
        {
            throw new InvalidOperationException(
                "VLOAD/VSTORE require the explicit typed vector load/store helper ABI; VectorOp transport is not emission authority.");
        }
    }
}
