using System;
using System.ComponentModel;
using HybridCPU.Compiler.Core.IR;
using YAKSys_Hybrid_CPU;
using HybridCPU.Compiler.Core.Threading;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Registers;
using HybridCPU_ISE.Arch;

namespace HybridCPU.Compiler.Core.API.Facade;

/// <summary>
/// Compatibility application-level assembly facade implementation.
/// Uses canonical compiler emission where phase-02 flat architectural register flow is already available
/// and lowers scoped symbolic control-transfer wrappers through compiler-owned relocation metadata.
/// </summary>
[Obsolete(AsmFacadeDeprecation.Message)]
[EditorBrowsable(EditorBrowsableState.Never)]
public class AppAsmFacade : IAppAsmFacade
{
    private readonly int _coreId;
    private readonly HybridCpuThreadCompilerContext _context;
    private readonly CompilerNonVmxScalarCapabilityModel _scalarCapabilities;
    private const byte DefaultScalarPredicateMask = 0xFF;
    private const byte ScalarDataType = 0;
    private const byte ScalarPredicateMask = 0;
    private const byte ZeroArchReg = 0;
    private const ulong ScalarStreamLength = 1;
    private const ushort ScalarStride = 8;

    /// <summary>
    /// Creates an AppAsmFacade bound to a specific core and thread compiler context.
    /// </summary>
    public AppAsmFacade(int coreId, HybridCpuThreadCompilerContext context)
        : this(coreId, context, CompilerNonVmxScalarCapabilityModel.Default)
    {
    }

    /// <summary>
    /// Creates an AppAsmFacade with an explicit compiler-visible scalar capability model.
    /// </summary>
    public AppAsmFacade(
        int coreId,
        HybridCpuThreadCompilerContext context,
        CompilerNonVmxScalarCapabilityModel scalarCapabilities)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scalarCapabilities);
        _coreId = coreId;
        _context = context;
        _scalarCapabilities = scalarCapabilities;
    }

    /// <summary>Resolves facade register to flat architectural register identity.</summary>
    protected static ArchRegId Resolve(AsmRegister reg) => reg.ArchRegisterId;

    /// <summary>Provides access to the core reference for derived facades.</summary>
    protected ref Processor.CPU_Core Core => ref Processor.CPU_Cores[_coreId];

    /// <summary>Provides access to the thread compiler context for derived facades.</summary>
    protected HybridCpuThreadCompilerContext Context => _context;

    /// <summary>Provides the core ID for derived facades.</summary>
    protected int CoreId => _coreId;

    protected void RequireScalarFeature(CompilerNonVmxScalarFeature feature, string mnemonic) =>
        _scalarCapabilities.Require(feature, mnemonic);

    /// <summary>Emits a scalar immediate instruction using packed register operands.</summary>
    protected void EmitScalarImmediate(Processor.CPU_Core.InstructionsEnum opcode, AsmRegister dest, AsmRegister src, short immediate)
    {
        Context.CompileInstruction(
            (uint)opcode,
            ScalarDataType,
            ScalarPredicateMask,
            unchecked((ushort)immediate),
            VLIW_Instruction.PackArchRegs(
                Resolve(dest).Value,
                Resolve(src).Value,
                VLIW_Instruction.NoArchReg),
            0,
            ScalarStreamLength,
            ScalarStride,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    /// <summary>Emits a canonical scalar word-immediate instruction using packed architectural registers.</summary>
    protected void EmitScalarWordImmediate(Processor.CPU_Core.InstructionsEnum opcode, AsmRegister dest, AsmRegister src, short immediate)
    {
        Context.CompileInstruction(
            (uint)opcode,
            (byte)DataTypeEnum.INT32,
            ScalarPredicateMask,
            unchecked((ushort)immediate),
            VLIW_Instruction.PackArchRegs(
                Resolve(dest).Value,
                Resolve(src).Value,
                ZeroArchReg),
            0,
            ScalarStreamLength,
            ScalarStride,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    /// <summary>Emits a canonical scalar word register-register instruction using packed architectural registers.</summary>
    protected void EmitScalarWordBinary(Processor.CPU_Core.InstructionsEnum opcode, AsmRegister dest, AsmRegister src1, AsmRegister src2)
    {
        Context.CompileInstruction(
            (uint)opcode,
            (byte)DataTypeEnum.INT32,
            ScalarPredicateMask,
            0,
            VLIW_Instruction.PackArchRegs(
                Resolve(dest).Value,
                Resolve(src1).Value,
                Resolve(src2).Value),
            0,
            ScalarStreamLength,
            ScalarStride,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    /// <summary>Emits a canonical scalar word-unary instruction using packed architectural registers.</summary>
    protected void EmitScalarWordUnary(Processor.CPU_Core.InstructionsEnum opcode, AsmRegister dest, AsmRegister src)
    {
        Context.CompileInstruction(
            (uint)opcode,
            (byte)DataTypeEnum.INT32,
            ScalarPredicateMask,
            0,
            VLIW_Instruction.PackArchRegs(
                Resolve(dest).Value,
                Resolve(src).Value,
                ZeroArchReg),
            0,
            ScalarStreamLength,
            ScalarStride,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    /// <summary>Emits a canonical XLEN=64 scalar unary instruction using packed architectural registers.</summary>
    protected void EmitScalarXlenUnary(Processor.CPU_Core.InstructionsEnum opcode, AsmRegister dest, AsmRegister src)
    {
        Context.CompileInstruction(
            (uint)opcode,
            (byte)DataTypeEnum.UINT64,
            ScalarPredicateMask,
            0,
            VLIW_Instruction.PackArchRegs(
                Resolve(dest).Value,
                Resolve(src).Value,
                ZeroArchReg),
            0,
            0,
            0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    /// <summary>Emits a canonical XLEN=64 scalar register-register instruction using packed architectural registers.</summary>
    protected void EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum opcode, AsmRegister dest, AsmRegister src1, AsmRegister src2)
    {
        Context.CompileInstruction(
            (uint)opcode,
            (byte)DataTypeEnum.UINT64,
            ScalarPredicateMask,
            0,
            VLIW_Instruction.PackArchRegs(
                Resolve(dest).Value,
                Resolve(src1).Value,
                Resolve(src2).Value),
            0,
            0,
            0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    /// <summary>Emits a canonical XLEN=64 scalar imm6 instruction using packed architectural registers.</summary>
    protected void EmitScalarXlenImmediate6(Processor.CPU_Core.InstructionsEnum opcode, AsmRegister dest, AsmRegister src, int immediate6)
    {
        if ((uint)immediate6 > 0x3Fu)
        {
            throw new ArgumentOutOfRangeException(
                nameof(immediate6),
                immediate6,
                "Scalar immediate payload must fit imm6 [0, 63].");
        }

        Context.CompileInstruction(
            (uint)opcode,
            (byte)DataTypeEnum.UINT64,
            ScalarPredicateMask,
            (ushort)immediate6,
            VLIW_Instruction.PackArchRegs(
                Resolve(dest).Value,
                Resolve(src).Value,
                ZeroArchReg),
            0,
            0,
            0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    /// <summary>Emits a canonical scalar register-register instruction using packed architectural registers.</summary>
    protected void EmitScalarBinary(Processor.CPU_Core.InstructionsEnum opcode, AsmRegister dest, AsmRegister src1, AsmRegister src2)
    {
        Context.CompileInstruction(
            (uint)opcode,
            ScalarDataType,
            ScalarPredicateMask,
            0,
            VLIW_Instruction.PackArchRegs(
                Resolve(dest).Value,
                Resolve(src1).Value,
                Resolve(src2).Value),
            0,
            ScalarStreamLength,
            ScalarStride,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    /// <summary>Emits a canonical scalar register-only instruction using packed architectural registers.</summary>
    protected void EmitScalarUnary(Processor.CPU_Core.InstructionsEnum opcode, AsmRegister dest, AsmRegister src)
    {
        Context.CompileInstruction(
            (uint)opcode,
            ScalarDataType,
            ScalarPredicateMask,
            0,
            VLIW_Instruction.PackArchRegs(
                Resolve(dest).Value,
                Resolve(src).Value,
                VLIW_Instruction.NoArchReg),
            0,
            ScalarStreamLength,
            ScalarStride,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    protected static IrEntryPointKind Resolve(AsmControlTarget target)
    {
        return target.Kind switch
        {
            AsmControlTargetKind.ProgramEntry => IrEntryPointKind.ProgramEntry,
            AsmControlTargetKind.EntryPoint => IrEntryPointKind.EntryPoint,
            AsmControlTargetKind.CallTarget => IrEntryPointKind.CallTarget,
            AsmControlTargetKind.InterruptHandler => IrEntryPointKind.InterruptHandler,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target.Kind, "Unknown control-target kind.")
        };
    }

    protected static void Validate(AsmControlTarget target, string parameterName)
    {
        if (!target.IsDefined)
        {
            throw new ArgumentException("Control target must have a non-empty symbolic name.", parameterName);
        }
    }

    protected void EmitSymbolicControlTransfer(
        Processor.CPU_Core.InstructionsEnum opcode,
        AsmControlTarget target,
        byte rd,
        byte rs1,
        byte rs2,
        IrControlTransferKind transferKind)
    {
        Validate(target, nameof(target));
        Context.CompileSymbolicControlFlow(
            opcode,
            target.Name,
            rd,
            rs1,
            rs2,
            transferKind);
    }

    // ── Arithmetic ──

    public void Add(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarBinary(Processor.CPU_Core.InstructionsEnum.ADD, dest, src1, src2);

    public void AddWord(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarWordBinary(Processor.CPU_Core.InstructionsEnum.ADDW, dest, src1, src2);

    public void Sub(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarBinary(Processor.CPU_Core.InstructionsEnum.SUB, dest, src1, src2);

    public void SubWord(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarWordBinary(Processor.CPU_Core.InstructionsEnum.SUBW, dest, src1, src2);

    public void Mul(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarBinary(Processor.CPU_Core.InstructionsEnum.MUL, dest, src1, src2);

    public void MultiplyWord(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarWordBinary(Processor.CPU_Core.InstructionsEnum.MULW, dest, src1, src2);

    public void Div(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarBinary(Processor.CPU_Core.InstructionsEnum.DIV, dest, src1, src2);

    public void DivideWord(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarWordBinary(Processor.CPU_Core.InstructionsEnum.DIVW, dest, src1, src2);

    public void DivideUnsignedWord(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarWordBinary(Processor.CPU_Core.InstructionsEnum.DIVUW, dest, src1, src2);

    public void Mod(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarBinary(Processor.CPU_Core.InstructionsEnum.REM, dest, src1, src2);

    public void RemainderWord(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarWordBinary(Processor.CPU_Core.InstructionsEnum.REMW, dest, src1, src2);

    public void RemainderUnsignedWord(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarWordBinary(Processor.CPU_Core.InstructionsEnum.REMUW, dest, src1, src2);

    public void SetBitRegister(AsmRegister dest, AsmRegister src, AsmRegister index)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitfield, "BSET");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.BSET, dest, src, index);
    }

    public void ClearBitRegister(AsmRegister dest, AsmRegister src, AsmRegister index)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitfield, "BCLR");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.BCLR, dest, src, index);
    }

    public void InvertBitRegister(AsmRegister dest, AsmRegister src, AsmRegister index)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitfield, "BINV");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.BINV, dest, src, index);
    }

    public void ExtractBitRegister(AsmRegister dest, AsmRegister src, AsmRegister index)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitfield, "BEXT");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.BEXT, dest, src, index);
    }

    public void SetBitImmediate(AsmRegister dest, AsmRegister src, int index)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitfield, "BSETI");
        EmitScalarXlenImmediate6(Processor.CPU_Core.InstructionsEnum.BSETI, dest, src, index);
    }

    public void ClearBitImmediate(AsmRegister dest, AsmRegister src, int index)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitfield, "BCLRI");
        EmitScalarXlenImmediate6(Processor.CPU_Core.InstructionsEnum.BCLRI, dest, src, index);
    }

    public void InvertBitImmediate(AsmRegister dest, AsmRegister src, int index)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitfield, "BINVI");
        EmitScalarXlenImmediate6(Processor.CPU_Core.InstructionsEnum.BINVI, dest, src, index);
    }

    public void ExtractBitImmediate(AsmRegister dest, AsmRegister src, int index)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitfield, "BEXTI");
        EmitScalarXlenImmediate6(Processor.CPU_Core.InstructionsEnum.BEXTI, dest, src, index);
    }

    public void AndWithInvertedSecond(AsmRegister dest, AsmRegister src1, AsmRegister src2)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "ANDN");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.ANDN, dest, src1, src2);
    }

    public void OrWithInvertedSecond(AsmRegister dest, AsmRegister src1, AsmRegister src2)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "ORN");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.ORN, dest, src1, src2);
    }

    public void ExclusiveNor(AsmRegister dest, AsmRegister src1, AsmRegister src2)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "XNOR");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.XNOR, dest, src1, src2);
    }

    public void ScalarMinSigned(AsmRegister dest, AsmRegister src1, AsmRegister src2)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "MIN");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.MIN, dest, src1, src2);
    }

    public void ScalarMaxSigned(AsmRegister dest, AsmRegister src1, AsmRegister src2)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "MAX");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.MAX, dest, src1, src2);
    }

    public void ScalarMinUnsigned(AsmRegister dest, AsmRegister src1, AsmRegister src2)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "MINU");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.MINU, dest, src1, src2);
    }

    public void ScalarMaxUnsigned(AsmRegister dest, AsmRegister src1, AsmRegister src2)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "MAXU");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.MAXU, dest, src1, src2);
    }

    public void BinaryPolynomialProductLow(AsmRegister dest, AsmRegister src1, AsmRegister src2)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarCarryLessChecksum, "CLMUL");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.CLMUL, dest, src1, src2);
    }

    public void BinaryPolynomialProductHigh(AsmRegister dest, AsmRegister src1, AsmRegister src2)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarCarryLessChecksum, "CLMULH");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.CLMULH, dest, src1, src2);
    }

    public void BinaryPolynomialProductReverse(AsmRegister dest, AsmRegister src1, AsmRegister src2)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarCarryLessChecksum, "CLMULR");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.CLMULR, dest, src1, src2);
    }

    public void ZeroIfConditionEqualZero(AsmRegister dest, AsmRegister src, AsmRegister condition)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarSelectCzero, "CZERO.EQZ");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.CZERO_EQZ, dest, src, condition);
    }

    public void ZeroIfConditionNotEqualZero(AsmRegister dest, AsmRegister src, AsmRegister condition)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarSelectCzero, "CZERO.NEZ");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.CZERO_NEZ, dest, src, condition);
    }

    public void AddUnsignedWord(AsmRegister dest, AsmRegister src, AsmRegister addend)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarAddressGeneration, "ADD.UW");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.ADD_UW, dest, src, addend);
    }

    public void ShiftLeftOneAndAdd(AsmRegister dest, AsmRegister src, AsmRegister addend)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarAddressGeneration, "SH1ADD");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.SH1ADD, dest, src, addend);
    }

    public void ShiftLeftTwoAndAdd(AsmRegister dest, AsmRegister src, AsmRegister addend)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarAddressGeneration, "SH2ADD");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.SH2ADD, dest, src, addend);
    }

    public void ShiftLeftThreeAndAdd(AsmRegister dest, AsmRegister src, AsmRegister addend)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarAddressGeneration, "SH3ADD");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.SH3ADD, dest, src, addend);
    }

    public void ShiftLeftOneAndAddUnsignedWord(AsmRegister dest, AsmRegister src, AsmRegister addend)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarAddressGeneration, "SH1ADD.UW");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.SH1ADD_UW, dest, src, addend);
    }

    public void ShiftLeftTwoAndAddUnsignedWord(AsmRegister dest, AsmRegister src, AsmRegister addend)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarAddressGeneration, "SH2ADD.UW");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.SH2ADD_UW, dest, src, addend);
    }

    public void ShiftLeftThreeAndAddUnsignedWord(AsmRegister dest, AsmRegister src, AsmRegister addend)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarAddressGeneration, "SH3ADD.UW");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.SH3ADD_UW, dest, src, addend);
    }

    public void ShiftLeftUnsignedWordByImmediate(AsmRegister dest, AsmRegister src, int shift)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarAddressGeneration, "SLLI.UW");
        EmitScalarXlenImmediate6(Processor.CPU_Core.InstructionsEnum.SLLI_UW, dest, src, shift);
    }

    public void RotateLeftRegister(AsmRegister dest, AsmRegister src, AsmRegister shift)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "ROL");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.ROL, dest, src, shift);
    }

    public void RotateRightRegister(AsmRegister dest, AsmRegister src, AsmRegister shift)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "ROR");
        EmitScalarXlenBinary(Processor.CPU_Core.InstructionsEnum.ROR, dest, src, shift);
    }

    public void RotateLeftByImmediate(AsmRegister dest, AsmRegister src, int shift)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "ROLI");
        EmitScalarXlenImmediate6(Processor.CPU_Core.InstructionsEnum.ROLI, dest, src, shift);
    }

    public void RotateRightByImmediate(AsmRegister dest, AsmRegister src, int shift)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "RORI");
        EmitScalarXlenImmediate6(Processor.CPU_Core.InstructionsEnum.RORI, dest, src, shift);
    }

    public void CountLeadingZeros(AsmRegister dest, AsmRegister src) =>
        EmitScalarXlenUnary(Processor.CPU_Core.InstructionsEnum.CLZ, dest, src);

    public void CountTrailingZeros(AsmRegister dest, AsmRegister src)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "CTZ");
        EmitScalarXlenUnary(Processor.CPU_Core.InstructionsEnum.CTZ, dest, src);
    }

    public void CountSetBits(AsmRegister dest, AsmRegister src)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "CPOP");
        EmitScalarXlenUnary(Processor.CPU_Core.InstructionsEnum.CPOP, dest, src);
    }

    public void ReverseByteOrder(AsmRegister dest, AsmRegister src)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "REV8");
        EmitScalarXlenUnary(Processor.CPU_Core.InstructionsEnum.REV8, dest, src);
    }

    public void ReverseBitsInEachByte(AsmRegister dest, AsmRegister src)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "BREV8");
        EmitScalarXlenUnary(Processor.CPU_Core.InstructionsEnum.BREV8, dest, src);
    }

    public void SignExtendByte(AsmRegister dest, AsmRegister src)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "SEXT.B");
        EmitScalarXlenUnary(Processor.CPU_Core.InstructionsEnum.SEXT_B, dest, src);
    }

    public void SignExtendHalf(AsmRegister dest, AsmRegister src)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "SEXT.H");
        EmitScalarXlenUnary(Processor.CPU_Core.InstructionsEnum.SEXT_H, dest, src);
    }

    public void SignExtendWord(AsmRegister dest, AsmRegister src) =>
        EmitScalarWordUnary(Processor.CPU_Core.InstructionsEnum.SEXT_W, dest, src);

    public void ZeroExtendHalf(AsmRegister dest, AsmRegister src)
    {
        RequireScalarFeature(CompilerNonVmxScalarFeature.ScalarBitmanipCore, "ZEXT.H");
        EmitScalarXlenUnary(Processor.CPU_Core.InstructionsEnum.ZEXT_H, dest, src);
    }

    public void ZeroExtendWord(AsmRegister dest, AsmRegister src) =>
        EmitScalarWordUnary(Processor.CPU_Core.InstructionsEnum.ZEXT_W, dest, src);

    public void Sqrt(AsmRegister dest, AsmRegister src) =>
        Core.SquareRoot(Resolve(dest), Resolve(src));

    public void Fmac(AsmRegister acc, AsmRegister src1, AsmRegister src2) =>
        Core.FMAC(Resolve(acc), Resolve(src1), Resolve(src2));

    public void Inc(AsmRegister reg) =>
        EmitScalarImmediate(Processor.CPU_Core.InstructionsEnum.ADDI, reg, reg, 1);

    public void Dec(AsmRegister reg) =>
        EmitScalarImmediate(Processor.CPU_Core.InstructionsEnum.ADDI, reg, reg, -1);

    public void AddWordImmediate(AsmRegister dest, AsmRegister src, int immediate) =>
        EmitScalarWordImmediate(Processor.CPU_Core.InstructionsEnum.ADDIW, dest, src, checked((short)immediate));

    public void ShiftLeft(AsmRegister dest, AsmRegister src, int amount) =>
        EmitScalarImmediate(Processor.CPU_Core.InstructionsEnum.SLL, dest, src, checked((short)amount));

    public void ShiftLeftWordImmediate(AsmRegister dest, AsmRegister src, int amount) =>
        EmitScalarWordImmediate(Processor.CPU_Core.InstructionsEnum.SLLIW, dest, src, checked((short)amount));

    public void ShiftRight(AsmRegister dest, AsmRegister src, int amount) =>
        EmitScalarImmediate(Processor.CPU_Core.InstructionsEnum.SRL, dest, src, checked((short)amount));

    public void ShiftRightLogicalWordImmediate(AsmRegister dest, AsmRegister src, int amount) =>
        EmitScalarWordImmediate(Processor.CPU_Core.InstructionsEnum.SRLIW, dest, src, checked((short)amount));

    public void ShiftRightArithmeticWordImmediate(AsmRegister dest, AsmRegister src, int amount) =>
        EmitScalarWordImmediate(Processor.CPU_Core.InstructionsEnum.SRAIW, dest, src, checked((short)amount));

    public void ShiftRightArithmetic(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarBinary(Processor.CPU_Core.InstructionsEnum.SRA, dest, src1, src2);

    public void ShiftLeftWord(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarWordBinary(Processor.CPU_Core.InstructionsEnum.SLLW, dest, src1, src2);

    public void ShiftRightLogicalWord(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarWordBinary(Processor.CPU_Core.InstructionsEnum.SRLW, dest, src1, src2);

    public void ShiftRightArithmeticWord(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarWordBinary(Processor.CPU_Core.InstructionsEnum.SRAW, dest, src1, src2);

    public void Xor(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarBinary(Processor.CPU_Core.InstructionsEnum.XOR, dest, src1, src2);

    public void Or(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarBinary(Processor.CPU_Core.InstructionsEnum.OR, dest, src1, src2);

    public void And(AsmRegister dest, AsmRegister src1, AsmRegister src2) =>
        EmitScalarBinary(Processor.CPU_Core.InstructionsEnum.AND, dest, src1, src2);

    public void Not(AsmRegister dest, AsmRegister src) =>
        EmitScalarImmediate(Processor.CPU_Core.InstructionsEnum.XORI, dest, src, -1);

    public void LoadImm(AsmRegister dest, ulong value)
    {
        if (value > (ulong)short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "LoadImm currently supports canonical ADDI rd, x0, imm16 emission only.");
        }

        EmitScalarImmediate(
            Processor.CPU_Core.InstructionsEnum.ADDI,
            dest,
            new AsmRegister(0),
            checked((short)value));
    }

    public void Nop()
    {
        Context.CompileInstruction(
            (uint)Processor.CPU_Core.InstructionsEnum.Nope,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }


    // ── Memory ──

    public void Load(ulong address, AsmRegister dest)
    {
        Context.CompileInstruction(
            (uint)Processor.CPU_Core.InstructionsEnum.LD,
            (byte)DataTypeEnum.INT64,
            DefaultScalarPredicateMask,
            0,
            VLIW_Instruction.PackArchRegs(
                Resolve(dest).Value,
                VLIW_Instruction.NoArchReg,
                VLIW_Instruction.NoArchReg),
            address,
            0,
            0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    public void Store(AsmRegister src, ulong address)
    {
        Context.CompileInstruction(
            (uint)Processor.CPU_Core.InstructionsEnum.SD,
            (byte)DataTypeEnum.INT64,
            DefaultScalarPredicateMask,
            0,
            VLIW_Instruction.PackArchRegs(
                VLIW_Instruction.NoArchReg,
                VLIW_Instruction.NoArchReg,
                Resolve(src).Value),
            address,
            0,
            0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    public void Move(AsmRegister src, AsmRegister dest)
    {
        EmitScalarImmediate(Processor.CPU_Core.InstructionsEnum.ADDI, dest, src, 0);
    }

    public void MtileLoad(
        CompilerMatrixTileTileOperand destinationTile,
        CompilerMatrixTileDescriptorAbi descriptor,
        CompilerMatrixTileMemoryFaultAbiInputs memoryFaultAbi)
    {
        Context.CompileMtileLoadWithDecision(destinationTile, descriptor, memoryFaultAbi);
    }

    public void MtileStore(
        CompilerMatrixTileTileOperand sourceTile,
        CompilerMatrixTileDescriptorAbi descriptor,
        CompilerMatrixTileMemoryFaultAbiInputs memoryFaultAbi)
    {
        Context.CompileMtileStoreWithDecision(sourceTile, descriptor, memoryFaultAbi);
    }

    public void MtileMacc(
        CompilerMatrixTileTileOperand leftSourceTile,
        CompilerMatrixTileTileOperand rightSourceTile,
        CompilerMatrixTileTileOperand accumulatorTile,
        CompilerMatrixTileDescriptorAbi leftSourceDescriptor,
        CompilerMatrixTileAccumulatorPolicyAbi accumulatorPolicyAbi)
    {
        Context.CompileMtileMaccWithDecision(
            leftSourceTile,
            rightSourceTile,
            accumulatorTile,
            leftSourceDescriptor,
            accumulatorPolicyAbi);
    }

    public void Mtranspose(
        CompilerMatrixTileTileOperand sourceTile,
        CompilerMatrixTileTileOperand destinationTile,
        CompilerMatrixTileDescriptorAbi sourceDescriptor,
        CompilerMatrixTileTransposePolicyAbi transposePolicyAbi)
    {
        Context.CompileMtransposeWithDecision(
            sourceTile,
            destinationTile,
            sourceDescriptor,
            transposePolicyAbi);
    }

    // ── Control flow ──

    public AsmControlTarget DefineEntryPoint(string name)
    {
        return new AsmControlTarget(name);
    }

    public void MarkEntryPoint(AsmControlTarget ep)
    {
        Validate(ep, nameof(ep));
        Context.DeclareLabelAtCurrentPosition(ep.Name);
        Context.DeclareEntryPointAtCurrentPosition(ep.Name, Resolve(ep));
    }

    public void Jump(AsmControlTarget target) =>
        EmitSymbolicControlTransfer(
            Processor.CPU_Core.InstructionsEnum.JAL,
            target,
            0,
            VLIW_Instruction.NoArchReg,
            VLIW_Instruction.NoArchReg,
            IrControlTransferKind.Branch);

    public void JumpIfNotEqual(AsmControlTarget target, AsmRegister acc, AsmRegister op1, AsmRegister op2, int hint)
    {
        _ = Resolve(acc);
        _ = hint;
        EmitSymbolicControlTransfer(
            Processor.CPU_Core.InstructionsEnum.BNE,
            target,
            VLIW_Instruction.NoArchReg,
            Resolve(op1).Value,
            Resolve(op2).Value,
            IrControlTransferKind.ConditionalBranch);
    }

    public void JumpIfBelow(AsmControlTarget target, AsmRegister acc, AsmRegister op1, AsmRegister op2, int hint)
    {
        _ = Resolve(acc);
        _ = hint;
        EmitSymbolicControlTransfer(
            Processor.CPU_Core.InstructionsEnum.BLTU,
            target,
            VLIW_Instruction.NoArchReg,
            Resolve(op1).Value,
            Resolve(op2).Value,
            IrControlTransferKind.ConditionalBranch);
    }

    public void JumpIfAbove(AsmControlTarget target, AsmRegister acc, AsmRegister op1, AsmRegister op2, int hint)
    {
        _ = Resolve(acc);
        _ = hint;
        EmitSymbolicControlTransfer(
            Processor.CPU_Core.InstructionsEnum.BLTU,
            target,
            VLIW_Instruction.NoArchReg,
            Resolve(op2).Value,
            Resolve(op1).Value,
            IrControlTransferKind.ConditionalBranch);
    }

    public void Call(AsmControlTarget target, AsmRegister acc, int hint)
    {
        _ = hint;
        EmitSymbolicControlTransfer(
            Processor.CPU_Core.InstructionsEnum.JAL,
            target,
            Resolve(acc).Value,
            VLIW_Instruction.NoArchReg,
            VLIW_Instruction.NoArchReg,
            IrControlTransferKind.Call);
    }

    public void Return(AsmControlTarget target, AsmRegister acc)
    {
        Validate(target, nameof(target));
        Context.CompileRegisterReturn(Resolve(acc).Value);
    }

    // ── Parallel hints ──

    /// <summary>
    /// Hint: parallel-for decomposition. Compiler silently falls back to sequential if decomposition fails.
    /// </summary>
    public void ParallelFor(AsmRegister start, AsmRegister end, AsmRegister step, Action body)
    {
        // Phase 07: invoke body sequentially as fallback.
        // Full decomposition occurs at IR level via ParallelForCompiler when invoked through expert path.
        body();
    }

    /// <summary>
    /// Hint: reduction. Compiler silently falls back to sequential if decomposition fails.
    /// </summary>
    public void Reduce(AsmRegister accumulator, AsmRegister start, AsmRegister end, Action body)
    {
        // Phase 07: invoke body sequentially as fallback.
        body();
    }

    // ── Lifecycle ──

    public void Init(AsmRegister acc)
    {
    }
}
