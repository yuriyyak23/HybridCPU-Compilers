using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HybridCPU.Compiler.Core.API.Facade;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Tests.TestHelpers;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Pipeline.MicroOps;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;
using TypedSlotBundleFacts = HybridCPU.Compiler.Core.IR.IrTypedSlotBundleFacts;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerEmissionInventoryTests
{
    private static readonly (string Mnemonic, string FacadeHelperFragment)[] CompilerDeferredScalarRows =
    [
        ("CSEL", "Csel"),
        ("CRC32", "Crc32"),
        ("CRC64", "Crc64"),
        ("ADC", "Adc"),
        ("SBC", "Sbc"),
        ("ADDC", "Addc"),
        ("SUBC", "Subc")
    ];

    private static readonly (string Mnemonic, string RuntimeOpcodeCandidate, string ContractMetadataFragment, string FacadeHelperFragment)[] CompilerRuntimeExecutableHelperClosedVectorRows =
    [
        ("VGATHER", "VGATHER", "VGATHER", "VGather"),
        ("VSCATTER", "VSCATTER", "VSCATTER", "VScatter"),
        ("VMSBF", "VMSBF", "VMSBF", "Vmsbf"),
        ("VZEXT", "VZEXT", "VZEXT", "Vzext"),
        ("VSCAN.SUM", "VSCAN_SUM", "VSCAN.SUM", "VscanSum"),
        ("VADD.SAT", "VADD", "VADD.SAT", "VaddSat"),
        ("VSLIDE1UP", "VSLIDE1UP", "VSLIDE1UP", "Vslide1Up"),
        ("VSLIDE1DOWN", "VSLIDE1DOWN", "VSLIDE1DOWN", "Vslide1Down"),
        ("VPERM2", "VPERM2", "VPERM2", "Vperm2"),
        ("VTRANSPOSE", "VTRANSPOSE", "VTRANSPOSE", "Vtranspose"),
        ("VDOT.WIDE", "VDOT_WIDE", "VDOT.WIDE", "VdotWide")
    ];

    private static IReadOnlyList<CompilerFailClosedEmissionRow> CompilerVectorVlmBlockedRows =>
        CompilerFailClosedEmissionInventory.VectorVlmBlockedRows;

    private static IReadOnlyList<CompilerFailClosedEmissionRow> CompilerMatrixTileOptionalDisabledRows =>
        CompilerFailClosedEmissionInventory.MatrixTileOptionalDisabledRows;

    private static IReadOnlyList<CompilerFailClosedEmissionRow> CompilerMatrixTilePositiveEmissionRows =>
        CompilerFailClosedEmissionInventory.MatrixTilePositiveEmissionRows;

    public static TheoryData<InstructionsEnum, uint, string> CompilerScalarWordBinaryTailContours => new()
    {
        { InstructionsEnum.DIVW, 311u, nameof(AppAsmFacade.DivideWord) },
        { InstructionsEnum.DIVUW, 312u, nameof(AppAsmFacade.DivideUnsignedWord) },
        { InstructionsEnum.REMW, 313u, nameof(AppAsmFacade.RemainderWord) },
        { InstructionsEnum.REMUW, 314u, nameof(AppAsmFacade.RemainderUnsignedWord) },
    };

    public static TheoryData<InstructionsEnum, uint, string> CompilerScalarWordUnaryTailContours => new()
    {
        { InstructionsEnum.SEXT_W, 320u, nameof(AppAsmFacade.SignExtendWord) },
        { InstructionsEnum.ZEXT_W, 321u, nameof(AppAsmFacade.ZeroExtendWord) },
    };

    public static TheoryData<InstructionsEnum, uint, string> CompilerScalarBitmanipUnaryContours => new()
    {
        { InstructionsEnum.SEXT_B, 60u, nameof(AppAsmFacade.SignExtendByte) },
        { InstructionsEnum.SEXT_H, 61u, nameof(AppAsmFacade.SignExtendHalf) },
        { InstructionsEnum.ZEXT_H, 62u, nameof(AppAsmFacade.ZeroExtendHalf) },
        { InstructionsEnum.REV8, 331u, nameof(AppAsmFacade.ReverseByteOrder) },
        { InstructionsEnum.BREV8, 332u, nameof(AppAsmFacade.ReverseBitsInEachByte) },
    };

    public static TheoryData<InstructionsEnum, uint, string, CompilerNonVmxScalarFeature> CompilerScalarRegisterBinaryContours => new()
    {
        { InstructionsEnum.CZERO_EQZ, 53u, nameof(AppAsmFacade.ZeroIfConditionEqualZero), CompilerNonVmxScalarFeature.ScalarSelectCzero },
        { InstructionsEnum.CZERO_NEZ, 333u, nameof(AppAsmFacade.ZeroIfConditionNotEqualZero), CompilerNonVmxScalarFeature.ScalarSelectCzero },
        { InstructionsEnum.ROL, 63u, nameof(AppAsmFacade.RotateLeftRegister), CompilerNonVmxScalarFeature.ScalarBitmanipCore },
        { InstructionsEnum.ROR, 64u, nameof(AppAsmFacade.RotateRightRegister), CompilerNonVmxScalarFeature.ScalarBitmanipCore },
        { InstructionsEnum.ANDN, 65u, nameof(AppAsmFacade.AndWithInvertedSecond), CompilerNonVmxScalarFeature.ScalarBitmanipCore },
        { InstructionsEnum.ORN, 66u, nameof(AppAsmFacade.OrWithInvertedSecond), CompilerNonVmxScalarFeature.ScalarBitmanipCore },
        { InstructionsEnum.XNOR, 67u, nameof(AppAsmFacade.ExclusiveNor), CompilerNonVmxScalarFeature.ScalarBitmanipCore },
        { InstructionsEnum.MIN, 327u, nameof(AppAsmFacade.ScalarMinSigned), CompilerNonVmxScalarFeature.ScalarBitmanipCore },
        { InstructionsEnum.MAX, 328u, nameof(AppAsmFacade.ScalarMaxSigned), CompilerNonVmxScalarFeature.ScalarBitmanipCore },
        { InstructionsEnum.MINU, 329u, nameof(AppAsmFacade.ScalarMinUnsigned), CompilerNonVmxScalarFeature.ScalarBitmanipCore },
        { InstructionsEnum.MAXU, 330u, nameof(AppAsmFacade.ScalarMaxUnsigned), CompilerNonVmxScalarFeature.ScalarBitmanipCore },
        { InstructionsEnum.CLMUL, 58u, nameof(AppAsmFacade.BinaryPolynomialProductLow), CompilerNonVmxScalarFeature.ScalarCarryLessChecksum },
        { InstructionsEnum.CLMULH, 352u, nameof(AppAsmFacade.BinaryPolynomialProductHigh), CompilerNonVmxScalarFeature.ScalarCarryLessChecksum },
        { InstructionsEnum.CLMULR, 353u, nameof(AppAsmFacade.BinaryPolynomialProductReverse), CompilerNonVmxScalarFeature.ScalarCarryLessChecksum },
        { InstructionsEnum.SH1ADD, 56u, nameof(AppAsmFacade.ShiftLeftOneAndAdd), CompilerNonVmxScalarFeature.ScalarAddressGeneration },
        { InstructionsEnum.SH2ADD, 345u, nameof(AppAsmFacade.ShiftLeftTwoAndAdd), CompilerNonVmxScalarFeature.ScalarAddressGeneration },
        { InstructionsEnum.SH3ADD, 346u, nameof(AppAsmFacade.ShiftLeftThreeAndAdd), CompilerNonVmxScalarFeature.ScalarAddressGeneration },
        { InstructionsEnum.ADD_UW, 347u, nameof(AppAsmFacade.AddUnsignedWord), CompilerNonVmxScalarFeature.ScalarAddressGeneration },
        { InstructionsEnum.SH1ADD_UW, 348u, nameof(AppAsmFacade.ShiftLeftOneAndAddUnsignedWord), CompilerNonVmxScalarFeature.ScalarAddressGeneration },
        { InstructionsEnum.SH2ADD_UW, 349u, nameof(AppAsmFacade.ShiftLeftTwoAndAddUnsignedWord), CompilerNonVmxScalarFeature.ScalarAddressGeneration },
        { InstructionsEnum.SH3ADD_UW, 350u, nameof(AppAsmFacade.ShiftLeftThreeAndAddUnsignedWord), CompilerNonVmxScalarFeature.ScalarAddressGeneration },
        { InstructionsEnum.BSET, 337u, nameof(AppAsmFacade.SetBitRegister), CompilerNonVmxScalarFeature.ScalarBitfield },
        { InstructionsEnum.BCLR, 338u, nameof(AppAsmFacade.ClearBitRegister), CompilerNonVmxScalarFeature.ScalarBitfield },
        { InstructionsEnum.BINV, 339u, nameof(AppAsmFacade.InvertBitRegister), CompilerNonVmxScalarFeature.ScalarBitfield },
        { InstructionsEnum.BEXT, 340u, nameof(AppAsmFacade.ExtractBitRegister), CompilerNonVmxScalarFeature.ScalarBitfield },
    };

    public static TheoryData<InstructionsEnum, uint, string, CompilerNonVmxScalarFeature> CompilerScalarRotateImmediateContours => new()
    {
        { InstructionsEnum.ROLI, 335u, nameof(AppAsmFacade.RotateLeftByImmediate), CompilerNonVmxScalarFeature.ScalarBitmanipCore },
        { InstructionsEnum.RORI, 336u, nameof(AppAsmFacade.RotateRightByImmediate), CompilerNonVmxScalarFeature.ScalarBitmanipCore },
    };

    public static TheoryData<InstructionsEnum, uint, string, CompilerNonVmxScalarFeature> CompilerScalarAddressGenerationImmediateContours => new()
    {
        { InstructionsEnum.SLLI_UW, 351u, nameof(AppAsmFacade.ShiftLeftUnsignedWordByImmediate), CompilerNonVmxScalarFeature.ScalarAddressGeneration },
    };

    public static TheoryData<InstructionsEnum, uint, string, CompilerNonVmxScalarFeature> CompilerScalarBitfieldImmediateContours => new()
    {
        { InstructionsEnum.BSETI, 341u, nameof(AppAsmFacade.SetBitImmediate), CompilerNonVmxScalarFeature.ScalarBitfield },
        { InstructionsEnum.BCLRI, 342u, nameof(AppAsmFacade.ClearBitImmediate), CompilerNonVmxScalarFeature.ScalarBitfield },
        { InstructionsEnum.BINVI, 343u, nameof(AppAsmFacade.InvertBitImmediate), CompilerNonVmxScalarFeature.ScalarBitfield },
        { InstructionsEnum.BEXTI, 344u, nameof(AppAsmFacade.ExtractBitImmediate), CompilerNonVmxScalarFeature.ScalarBitfield },
    };

    [Fact]
    public void AppShiftRightArithmetic_EmitsCanonicalSraRuntimeClosedGate()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        facade.ShiftRightArithmetic(
            new AsmRegister(7),
            new AsmRegister(5),
            new AsmRegister(6));
#pragma warning restore CS0618

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(InstructionsEnum.SRA, (InstructionsEnum)raw.OpCode);
        Assert.Equal(0, raw.PredicateMask);
        Assert.Equal(0, raw.Immediate);
        Assert.Equal(1u, raw.StreamLength);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(7, rawRd);
        Assert.Equal(5, rawRs1);
        Assert.Equal(6, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(InstructionsEnum.SRA, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)ir.SerializationClass);
        Assert.Equal(IrResourceClass.ScalarAlu, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.AluClass, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Equal(IrSlotBindingKind.ClassFlexible, ir.Annotation.BindingKind);
        Assert.Contains(ir.Annotation.Defs, operand => operand.Name == "rd" && operand.Value == 7UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 5UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs2" && operand.Value == 6UL);
        Assert.Null(ir.Annotation.MemoryReadRegion);
        Assert.Null(ir.Annotation.MemoryWriteRegion);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);

        TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(materializedBundle);
        Assert.Equal(1, facts.FlexibleOpCount);
        Assert.Equal(0, facts.SystemSingletonCount);
        Assert.True(compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(materializedSlot!.SlotIndex);
        Assert.Equal(InstructionsEnum.SRA, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(0, lowered.VirtualThreadId);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(lowered.Word1, out byte loweredRd, out byte loweredRs1, out byte loweredRs2));
        Assert.Equal(7, loweredRd);
        Assert.Equal(5, loweredRs1);
        Assert.Equal(6, loweredRs2);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            materializedSlot.SlotIndex);

        ScalarALUMicroOp scalar = Assert.IsType<ScalarALUMicroOp>(carrier);
        Assert.Equal(InstructionsEnum.SRA, (InstructionsEnum)scalar.OpCode);
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)scalar.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)scalar.SerializationClass);
        Assert.Equal(CanonicalDecodePublicationMode.SelfPublishes, scalar.CanonicalDecodePublication);
        Assert.Equal(SlotClass.AluClass, (SlotClass)(byte)scalar.AdmissionMetadata.Placement.RequiredSlotClass);
        Assert.Equal(SlotPinningKind.ClassFlexible, scalar.AdmissionMetadata.Placement.PinningKind);
        Assert.Equal(new[] { 5, 6 }, scalar.ReadRegisters);
        Assert.Equal(new[] { 7 }, scalar.WriteRegisters);
        Assert.Empty(scalar.ReadMemoryRanges);
        Assert.Empty(scalar.WriteMemoryRanges);
        Assert.True(scalar.ResourceMask.IsNonZero);
        Assert.NotEqual(0U, scalar.AdmissionMetadata.RegisterHazardMask);
        Assert.True(scalar.AdmissionMetadata.CertificateMask.IsNonZero);
    }

    [Fact]
    public void AppShiftRightArithmeticWordImmediate_EmitsCanonicalSraiwRuntimeClosedGate()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        facade.ShiftRightArithmeticWordImmediate(
            new AsmRegister(7),
            new AsmRegister(5),
            -1);
#pragma warning restore CS0618

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(InstructionsEnum.SRAIW, (InstructionsEnum)raw.OpCode);
        Assert.Equal(DataTypeEnum.INT32, raw.DataTypeValue);
        Assert.Equal(0, raw.PredicateMask);
        Assert.Equal(unchecked((ushort)-1), raw.Immediate);
        Assert.Equal(1u, raw.StreamLength);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(7, rawRd);
        Assert.Equal(5, rawRs1);
        Assert.Equal(0, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(InstructionsEnum.SRAIW, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)ir.SerializationClass);
        Assert.Equal(IrResourceClass.ScalarAlu, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.AluClass, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Equal(IrSlotBindingKind.ClassFlexible, ir.Annotation.BindingKind);
        Assert.Contains(ir.Annotation.Defs, operand => operand.Name == "rd" && operand.Value == 7UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 5UL);
        Assert.DoesNotContain(ir.Annotation.Uses, operand => operand.Name == "rs2");

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);

        TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(materializedBundle);
        Assert.Equal(1, facts.FlexibleOpCount);
        Assert.True(compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(materializedSlot!.SlotIndex);
        Assert.Equal(InstructionsEnum.SRAIW, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(DataTypeEnum.INT32, (DataTypeEnum)(byte)lowered.DataTypeValue);
        Assert.Equal(unchecked((ushort)-1), lowered.Immediate);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(lowered.Word1, out byte loweredRd, out byte loweredRs1, out byte loweredRs2));
        Assert.Equal(7, loweredRd);
        Assert.Equal(5, loweredRs1);
        Assert.Equal(0, loweredRs2);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            materializedSlot.SlotIndex);

        ScalarALUMicroOp scalar = Assert.IsType<ScalarALUMicroOp>(carrier);
        Assert.Equal(InstructionsEnum.SRAIW, (InstructionsEnum)scalar.OpCode);
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)scalar.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)scalar.SerializationClass);
        Assert.Equal(CanonicalDecodePublicationMode.SelfPublishes, scalar.CanonicalDecodePublication);
        Assert.True(scalar.UsesImmediate);
        Assert.Equal(ulong.MaxValue, scalar.Immediate);
        Assert.Equal(new[] { 5 }, scalar.ReadRegisters);
        Assert.Equal(new[] { 7 }, scalar.WriteRegisters);
        Assert.Empty(scalar.ReadMemoryRanges);
        Assert.Empty(scalar.WriteMemoryRanges);
        Assert.True(scalar.ResourceMask.IsNonZero);
        Assert.NotEqual(0U, scalar.AdmissionMetadata.RegisterHazardMask);
        Assert.True(scalar.AdmissionMetadata.CertificateMask.IsNonZero);
    }

    [Fact]
    public void AppMultiplyWord_EmitsCanonicalMulwRuntimeClosedGate()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        facade.MultiplyWord(
            new AsmRegister(7),
            new AsmRegister(5),
            new AsmRegister(6));
#pragma warning restore CS0618

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(310u, raw.OpCode);
        Assert.Equal(InstructionsEnum.MULW, (InstructionsEnum)raw.OpCode);
        Assert.Equal(DataTypeEnum.INT32, raw.DataTypeValue);
        Assert.Equal(0, raw.PredicateMask);
        Assert.Equal(0, raw.Immediate);
        Assert.Equal(1u, raw.StreamLength);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(7, rawRd);
        Assert.Equal(5, rawRs1);
        Assert.Equal(6, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(InstructionsEnum.MULW, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)ir.SerializationClass);
        Assert.Equal(IrResourceClass.ScalarAlu, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.AluClass, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Equal(IrSlotBindingKind.ClassFlexible, ir.Annotation.BindingKind);
        Assert.Contains(ir.Annotation.Defs, operand => operand.Name == "rd" && operand.Value == 7UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 5UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs2" && operand.Value == 6UL);
        Assert.Null(ir.Annotation.MemoryReadRegion);
        Assert.Null(ir.Annotation.MemoryWriteRegion);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);

        TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(materializedBundle);
        Assert.Equal(1, facts.FlexibleOpCount);
        Assert.Equal(0, facts.SystemSingletonCount);
        Assert.True(compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(materializedSlot!.SlotIndex);
        Assert.Equal(InstructionsEnum.MULW, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(DataTypeEnum.INT32, (DataTypeEnum)(byte)lowered.DataTypeValue);
        Assert.Equal(0, lowered.VirtualThreadId);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(lowered.Word1, out byte loweredRd, out byte loweredRs1, out byte loweredRs2));
        Assert.Equal(7, loweredRd);
        Assert.Equal(5, loweredRs1);
        Assert.Equal(6, loweredRs2);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            materializedSlot.SlotIndex);

        ScalarALUMicroOp scalar = Assert.IsType<ScalarALUMicroOp>(carrier);
        Assert.Equal(InstructionsEnum.MULW, (InstructionsEnum)scalar.OpCode);
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)scalar.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)scalar.SerializationClass);
        Assert.Equal(CanonicalDecodePublicationMode.SelfPublishes, scalar.CanonicalDecodePublication);
        Assert.False(scalar.UsesImmediate);
        Assert.Equal(new[] { 5, 6 }, scalar.ReadRegisters);
        Assert.Equal(new[] { 7 }, scalar.WriteRegisters);
        Assert.Empty(scalar.ReadMemoryRanges);
        Assert.Empty(scalar.WriteMemoryRanges);
        Assert.True(scalar.ResourceMask.IsNonZero);
        Assert.NotEqual(0U, scalar.AdmissionMetadata.RegisterHazardMask);
        Assert.True(scalar.AdmissionMetadata.CertificateMask.IsNonZero);
    }

    [Theory]
    [MemberData(nameof(CompilerScalarWordBinaryTailContours))]
    public void AppScalarWordBinaryTail_EmitsCanonicalRuntimeClosedGate(
        InstructionsEnum opcode,
        uint expectedOpcode,
        string helperName)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        EmitScalarWordBinaryTail(facade, opcode, new AsmRegister(7), new AsmRegister(5), new AsmRegister(6));
#pragma warning restore CS0618

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(expectedOpcode, raw.OpCode);
        Assert.Equal(opcode, (InstructionsEnum)raw.OpCode);
        Assert.Equal(DataTypeEnum.INT32, raw.DataTypeValue);
        Assert.Equal(0, raw.PredicateMask);
        Assert.Equal(0, raw.Immediate);
        Assert.Equal(1u, raw.StreamLength);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(7, rawRd);
        Assert.Equal(5, rawRs1);
        Assert.Equal(6, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(opcode, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)ir.SerializationClass);
        Assert.Equal(IrResourceClass.ScalarAlu, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.AluClass, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Equal(IrSlotBindingKind.ClassFlexible, ir.Annotation.BindingKind);
        Assert.Contains(ir.Annotation.Defs, operand => operand.Name == "rd" && operand.Value == 7UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 5UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs2" && operand.Value == 6UL);
        Assert.Null(ir.Annotation.MemoryReadRegion);
        Assert.Null(ir.Annotation.MemoryWriteRegion);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);

        TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(materializedBundle);
        Assert.Equal(1, facts.FlexibleOpCount);
        Assert.Equal(0, facts.SystemSingletonCount);
        Assert.True(compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(materializedSlot!.SlotIndex);
        Assert.Equal(opcode, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(DataTypeEnum.INT32, (DataTypeEnum)(byte)lowered.DataTypeValue);
        Assert.Equal(0, lowered.VirtualThreadId);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(lowered.Word1, out byte loweredRd, out byte loweredRs1, out byte loweredRs2));
        Assert.Equal(7, loweredRd);
        Assert.Equal(5, loweredRs1);
        Assert.Equal(6, loweredRs2);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            materializedSlot.SlotIndex);

        ScalarALUMicroOp scalar = Assert.IsType<ScalarALUMicroOp>(carrier);
        Assert.Equal(opcode, (InstructionsEnum)scalar.OpCode);
        Assert.Equal(helperName, ResolveCompilerTailHelperName(opcode));
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)scalar.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)scalar.SerializationClass);
        Assert.Equal(CanonicalDecodePublicationMode.SelfPublishes, scalar.CanonicalDecodePublication);
        Assert.False(scalar.UsesImmediate);
        Assert.Equal(new[] { 5, 6 }, scalar.ReadRegisters);
        Assert.Equal(new[] { 7 }, scalar.WriteRegisters);
        Assert.Empty(scalar.ReadMemoryRanges);
        Assert.Empty(scalar.WriteMemoryRanges);
        Assert.True(scalar.ResourceMask.IsNonZero);
        Assert.NotEqual(0U, scalar.AdmissionMetadata.RegisterHazardMask);
        Assert.True(scalar.AdmissionMetadata.CertificateMask.IsNonZero);
    }

    [Theory]
    [MemberData(nameof(CompilerScalarRegisterBinaryContours))]
    public void AppScalarRegisterBinary_EmitsCanonicalPhase02RegisterRegisterContour(
        InstructionsEnum opcode,
        uint expectedOpcode,
        string helperName,
        CompilerNonVmxScalarFeature requiredFeature)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        EmitScalarRegisterBinary(facade, opcode, new AsmRegister(7), new AsmRegister(5), new AsmRegister(6));
#pragma warning restore CS0618

        Assert.True(CompilerNonVmxScalarCapabilityModel.Default.Supports(requiredFeature));

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(expectedOpcode, raw.OpCode);
        Assert.Equal(opcode, (InstructionsEnum)raw.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, raw.DataTypeValue);
        Assert.Equal(0, raw.PredicateMask);
        Assert.Equal(0, raw.Immediate);
        Assert.Equal(0u, raw.StreamLength);
        Assert.Equal(0, raw.Stride);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(7, rawRd);
        Assert.Equal(5, rawRs1);
        Assert.Equal(6, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(opcode, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)ir.DataType);
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)ir.SerializationClass);
        Assert.Equal(IrResourceClass.ScalarAlu, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.AluClass, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Equal(IrSlotBindingKind.ClassFlexible, ir.Annotation.BindingKind);
        Assert.Contains(ir.Annotation.Defs, operand => operand.Name == "rd" && operand.Value == 7UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 5UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs2" && operand.Value == 6UL);
        Assert.Null(ir.Annotation.MemoryReadRegion);
        Assert.Null(ir.Annotation.MemoryWriteRegion);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);

        TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(materializedBundle);
        Assert.Equal(1, facts.FlexibleOpCount);
        Assert.Equal(0, facts.SystemSingletonCount);
        Assert.True(compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(materializedSlot!.SlotIndex);
        Assert.Equal(opcode, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)lowered.DataTypeValue);
        Assert.Equal(0, lowered.Immediate);
        Assert.Equal(0, lowered.VirtualThreadId);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(lowered.Word1, out byte loweredRd, out byte loweredRs1, out byte loweredRs2));
        Assert.Equal(7, loweredRd);
        Assert.Equal(5, loweredRs1);
        Assert.Equal(6, loweredRs2);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            materializedSlot.SlotIndex);

        ScalarALUMicroOp scalar = Assert.IsType<ScalarALUMicroOp>(carrier);
        Assert.Equal(opcode, (InstructionsEnum)scalar.OpCode);
        Assert.Equal(helperName, ResolveCompilerOptionalHelperName(opcode));
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)scalar.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)scalar.SerializationClass);
        Assert.Equal(CanonicalDecodePublicationMode.SelfPublishes, scalar.CanonicalDecodePublication);
        Assert.False(scalar.UsesImmediate);
        Assert.Equal(new[] { 5, 6 }, scalar.ReadRegisters);
        Assert.Equal(new[] { 7 }, scalar.WriteRegisters);
        Assert.Empty(scalar.ReadMemoryRanges);
        Assert.Empty(scalar.WriteMemoryRanges);
        Assert.True(scalar.ResourceMask.IsNonZero);
        Assert.NotEqual(0U, scalar.AdmissionMetadata.RegisterHazardMask);
        Assert.True(scalar.AdmissionMetadata.CertificateMask.IsNonZero);
    }

    [Theory]
    [MemberData(nameof(CompilerScalarRegisterBinaryContours))]
    public void AppScalarRegisterBinary_MissingScalarCapabilityFailsBeforeCarrierEmission(
        InstructionsEnum opcode,
        uint expectedOpcode,
        string helperName,
        CompilerNonVmxScalarFeature requiredFeature)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(
            0,
            context,
            CompilerNonVmxScalarCapabilityModel.Disabled);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => EmitScalarRegisterBinary(
                facade,
                opcode,
                new AsmRegister(7),
                new AsmRegister(5),
                new AsmRegister(6)));
#pragma warning restore CS0618

        Assert.NotEqual(0u, expectedOpcode);
        Assert.Equal(helperName, ResolveCompilerOptionalHelperName(opcode));
        Assert.Contains(
            $"{ResolveCompilerOptionalMnemonic(opcode)} compiler emission requires Non-VMX scalar capability {requiredFeature}",
            exception.Message);
        Assert.Equal(0, context.InstructionCount);
    }

    [Theory]
    [MemberData(nameof(CompilerScalarRotateImmediateContours))]
    public void AppScalarRotateImmediate_EmitsCanonicalPhase02Imm6Contour(
        InstructionsEnum opcode,
        uint expectedOpcode,
        string helperName,
        CompilerNonVmxScalarFeature requiredFeature)
    {
        const int immediate6 = 4;
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        EmitScalarRotateImmediate(facade, opcode, new AsmRegister(7), new AsmRegister(5), immediate6);
#pragma warning restore CS0618

        Assert.True(CompilerNonVmxScalarCapabilityModel.Default.Supports(requiredFeature));

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(expectedOpcode, raw.OpCode);
        Assert.Equal(opcode, (InstructionsEnum)raw.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, raw.DataTypeValue);
        Assert.Equal(0, raw.PredicateMask);
        Assert.Equal(immediate6, raw.Immediate);
        Assert.Equal(0u, raw.StreamLength);
        Assert.Equal(0, raw.Stride);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(7, rawRd);
        Assert.Equal(5, rawRs1);
        Assert.Equal(0, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(opcode, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)ir.DataType);
        Assert.Equal(immediate6, ir.Immediate);
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)ir.SerializationClass);
        Assert.Equal(IrResourceClass.ScalarAlu, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.AluClass, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Equal(IrSlotBindingKind.ClassFlexible, ir.Annotation.BindingKind);
        Assert.Contains(ir.Annotation.Defs, operand => operand.Name == "rd" && operand.Value == 7UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 5UL);
        Assert.DoesNotContain(ir.Annotation.Uses, operand => operand.Name == "rs2");
        Assert.Null(ir.Annotation.MemoryReadRegion);
        Assert.Null(ir.Annotation.MemoryWriteRegion);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);

        TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(materializedBundle);
        Assert.Equal(1, facts.FlexibleOpCount);
        Assert.Equal(0, facts.SystemSingletonCount);
        Assert.True(compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(materializedSlot!.SlotIndex);
        Assert.Equal(opcode, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)lowered.DataTypeValue);
        Assert.Equal(immediate6, lowered.Immediate);
        Assert.Equal(0, lowered.VirtualThreadId);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(lowered.Word1, out byte loweredRd, out byte loweredRs1, out byte loweredRs2));
        Assert.Equal(7, loweredRd);
        Assert.Equal(5, loweredRs1);
        Assert.Equal(0, loweredRs2);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            materializedSlot.SlotIndex);

        ScalarALUMicroOp scalar = Assert.IsType<ScalarALUMicroOp>(carrier);
        Assert.Equal(opcode, (InstructionsEnum)scalar.OpCode);
        Assert.Equal(helperName, ResolveCompilerOptionalHelperName(opcode));
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)scalar.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)scalar.SerializationClass);
        Assert.Equal(CanonicalDecodePublicationMode.SelfPublishes, scalar.CanonicalDecodePublication);
        Assert.True(scalar.UsesImmediate);
        Assert.Equal((ulong)immediate6, scalar.Immediate);
        Assert.Equal(new[] { 5 }, scalar.ReadRegisters);
        Assert.DoesNotContain(0, scalar.ReadRegisters);
        Assert.Equal(new[] { 7 }, scalar.WriteRegisters);
        Assert.Empty(scalar.ReadMemoryRanges);
        Assert.Empty(scalar.WriteMemoryRanges);
        Assert.True(scalar.ResourceMask.IsNonZero);
        Assert.NotEqual(0U, scalar.AdmissionMetadata.RegisterHazardMask);
        Assert.True(scalar.AdmissionMetadata.CertificateMask.IsNonZero);
    }

    [Theory]
    [MemberData(nameof(CompilerScalarRotateImmediateContours))]
    public void AppScalarRotateImmediate_RejectsOutOfRangeImm6BeforeCarrierEmission(
        InstructionsEnum opcode,
        uint expectedOpcode,
        string helperName,
        CompilerNonVmxScalarFeature requiredFeature)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => EmitScalarRotateImmediate(
                facade,
                opcode,
                new AsmRegister(7),
                new AsmRegister(5),
                64));
#pragma warning restore CS0618

        Assert.NotEqual(0u, expectedOpcode);
        Assert.Equal(helperName, ResolveCompilerOptionalHelperName(opcode));
        Assert.True(CompilerNonVmxScalarCapabilityModel.Default.Supports(requiredFeature));
        Assert.Equal("immediate6", exception.ParamName);
        Assert.Equal(0, context.InstructionCount);
    }

    [Theory]
    [MemberData(nameof(CompilerScalarRotateImmediateContours))]
    public void AppScalarRotateImmediate_MissingScalarBitmanipCapabilityFailsBeforeCarrierEmission(
        InstructionsEnum opcode,
        uint expectedOpcode,
        string helperName,
        CompilerNonVmxScalarFeature requiredFeature)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(
            0,
            context,
            CompilerNonVmxScalarCapabilityModel.Disabled);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => EmitScalarRotateImmediate(
                facade,
                opcode,
                new AsmRegister(7),
                new AsmRegister(5),
                4));
#pragma warning restore CS0618

        Assert.NotEqual(0u, expectedOpcode);
        Assert.Equal(helperName, ResolveCompilerOptionalHelperName(opcode));
        Assert.Contains(
            $"{ResolveCompilerOptionalMnemonic(opcode)} compiler emission requires Non-VMX scalar capability {requiredFeature}",
            exception.Message);
        Assert.Equal(0, context.InstructionCount);
    }

    [Theory]
    [MemberData(nameof(CompilerScalarAddressGenerationImmediateContours))]
    public void AppScalarAddressGenerationImmediate_EmitsCanonicalPhase02Imm6Contour(
        InstructionsEnum opcode,
        uint expectedOpcode,
        string helperName,
        CompilerNonVmxScalarFeature requiredFeature)
    {
        const int immediate6 = 4;
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        EmitScalarAddressGenerationImmediate(facade, opcode, new AsmRegister(7), new AsmRegister(5), immediate6);
#pragma warning restore CS0618

        Assert.True(CompilerNonVmxScalarCapabilityModel.Default.Supports(requiredFeature));

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(expectedOpcode, raw.OpCode);
        Assert.Equal(opcode, (InstructionsEnum)raw.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, raw.DataTypeValue);
        Assert.Equal(0, raw.PredicateMask);
        Assert.Equal(immediate6, raw.Immediate);
        Assert.Equal(0u, raw.StreamLength);
        Assert.Equal(0, raw.Stride);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(7, rawRd);
        Assert.Equal(5, rawRs1);
        Assert.Equal(0, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(opcode, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)ir.DataType);
        Assert.Equal(immediate6, ir.Immediate);
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)ir.SerializationClass);
        Assert.Equal(IrResourceClass.ScalarAlu, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.AluClass, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Equal(IrSlotBindingKind.ClassFlexible, ir.Annotation.BindingKind);
        Assert.Contains(ir.Annotation.Defs, operand => operand.Name == "rd" && operand.Value == 7UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 5UL);
        Assert.DoesNotContain(ir.Annotation.Uses, operand => operand.Name == "rs2");
        Assert.Null(ir.Annotation.MemoryReadRegion);
        Assert.Null(ir.Annotation.MemoryWriteRegion);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);

        TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(materializedBundle);
        Assert.Equal(1, facts.FlexibleOpCount);
        Assert.Equal(0, facts.SystemSingletonCount);
        Assert.True(compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(materializedSlot!.SlotIndex);
        Assert.Equal(opcode, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)lowered.DataTypeValue);
        Assert.Equal(immediate6, lowered.Immediate);
        Assert.Equal(0, lowered.VirtualThreadId);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(lowered.Word1, out byte loweredRd, out byte loweredRs1, out byte loweredRs2));
        Assert.Equal(7, loweredRd);
        Assert.Equal(5, loweredRs1);
        Assert.Equal(0, loweredRs2);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            materializedSlot.SlotIndex);

        ScalarALUMicroOp scalar = Assert.IsType<ScalarALUMicroOp>(carrier);
        Assert.Equal(opcode, (InstructionsEnum)scalar.OpCode);
        Assert.Equal(helperName, ResolveCompilerOptionalHelperName(opcode));
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)scalar.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)scalar.SerializationClass);
        Assert.Equal(CanonicalDecodePublicationMode.SelfPublishes, scalar.CanonicalDecodePublication);
        Assert.True(scalar.UsesImmediate);
        Assert.Equal((ulong)immediate6, scalar.Immediate);
        Assert.Equal(new[] { 5 }, scalar.ReadRegisters);
        Assert.DoesNotContain(0, scalar.ReadRegisters);
        Assert.Equal(new[] { 7 }, scalar.WriteRegisters);
        Assert.Empty(scalar.ReadMemoryRanges);
        Assert.Empty(scalar.WriteMemoryRanges);
        Assert.True(scalar.ResourceMask.IsNonZero);
        Assert.NotEqual(0U, scalar.AdmissionMetadata.RegisterHazardMask);
        Assert.True(scalar.AdmissionMetadata.CertificateMask.IsNonZero);
    }

    [Theory]
    [MemberData(nameof(CompilerScalarAddressGenerationImmediateContours))]
    public void AppScalarAddressGenerationImmediate_RejectsOutOfRangeImm6BeforeCarrierEmission(
        InstructionsEnum opcode,
        uint expectedOpcode,
        string helperName,
        CompilerNonVmxScalarFeature requiredFeature)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => EmitScalarAddressGenerationImmediate(
                facade,
                opcode,
                new AsmRegister(7),
                new AsmRegister(5),
                64));
#pragma warning restore CS0618

        Assert.NotEqual(0u, expectedOpcode);
        Assert.Equal(helperName, ResolveCompilerOptionalHelperName(opcode));
        Assert.True(CompilerNonVmxScalarCapabilityModel.Default.Supports(requiredFeature));
        Assert.Equal("immediate6", exception.ParamName);
        Assert.Equal(0, context.InstructionCount);
    }

    [Theory]
    [MemberData(nameof(CompilerScalarAddressGenerationImmediateContours))]
    public void AppScalarAddressGenerationImmediate_MissingScalarAddressGenerationCapabilityFailsBeforeCarrierEmission(
        InstructionsEnum opcode,
        uint expectedOpcode,
        string helperName,
        CompilerNonVmxScalarFeature requiredFeature)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(
            0,
            context,
            CompilerNonVmxScalarCapabilityModel.Disabled);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => EmitScalarAddressGenerationImmediate(
                facade,
                opcode,
                new AsmRegister(7),
                new AsmRegister(5),
                4));
#pragma warning restore CS0618

        Assert.NotEqual(0u, expectedOpcode);
        Assert.Equal(helperName, ResolveCompilerOptionalHelperName(opcode));
        Assert.Contains(
            $"{ResolveCompilerOptionalMnemonic(opcode)} compiler emission requires Non-VMX scalar capability {requiredFeature}",
            exception.Message);
        Assert.Equal(0, context.InstructionCount);
    }

    [Theory]
    [MemberData(nameof(CompilerScalarBitfieldImmediateContours))]
    public void AppScalarBitfieldImmediate_EmitsCanonicalPhase02Imm6Contour(
        InstructionsEnum opcode,
        uint expectedOpcode,
        string helperName,
        CompilerNonVmxScalarFeature requiredFeature)
    {
        const int immediate6 = 4;
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        EmitScalarBitfieldImmediate(facade, opcode, new AsmRegister(7), new AsmRegister(5), immediate6);
#pragma warning restore CS0618

        Assert.True(CompilerNonVmxScalarCapabilityModel.Default.Supports(requiredFeature));

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(expectedOpcode, raw.OpCode);
        Assert.Equal(opcode, (InstructionsEnum)raw.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, raw.DataTypeValue);
        Assert.Equal(0, raw.PredicateMask);
        Assert.Equal(immediate6, raw.Immediate);
        Assert.Equal(0u, raw.StreamLength);
        Assert.Equal(0, raw.Stride);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(7, rawRd);
        Assert.Equal(5, rawRs1);
        Assert.Equal(0, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(opcode, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)ir.DataType);
        Assert.Equal(immediate6, ir.Immediate);
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)ir.SerializationClass);
        Assert.Equal(IrResourceClass.ScalarAlu, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.AluClass, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Equal(IrSlotBindingKind.ClassFlexible, ir.Annotation.BindingKind);
        Assert.Contains(ir.Annotation.Defs, operand => operand.Name == "rd" && operand.Value == 7UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 5UL);
        Assert.DoesNotContain(ir.Annotation.Uses, operand => operand.Name == "rs2");
        Assert.Null(ir.Annotation.MemoryReadRegion);
        Assert.Null(ir.Annotation.MemoryWriteRegion);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);

        TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(materializedBundle);
        Assert.Equal(1, facts.FlexibleOpCount);
        Assert.Equal(0, facts.SystemSingletonCount);
        Assert.True(compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(materializedSlot!.SlotIndex);
        Assert.Equal(opcode, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)lowered.DataTypeValue);
        Assert.Equal(immediate6, lowered.Immediate);
        Assert.Equal(0, lowered.VirtualThreadId);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(lowered.Word1, out byte loweredRd, out byte loweredRs1, out byte loweredRs2));
        Assert.Equal(7, loweredRd);
        Assert.Equal(5, loweredRs1);
        Assert.Equal(0, loweredRs2);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            materializedSlot.SlotIndex);

        ScalarALUMicroOp scalar = Assert.IsType<ScalarALUMicroOp>(carrier);
        Assert.Equal(opcode, (InstructionsEnum)scalar.OpCode);
        Assert.Equal(helperName, ResolveCompilerOptionalHelperName(opcode));
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)scalar.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)scalar.SerializationClass);
        Assert.Equal(CanonicalDecodePublicationMode.SelfPublishes, scalar.CanonicalDecodePublication);
        Assert.True(scalar.UsesImmediate);
        Assert.Equal((ulong)immediate6, scalar.Immediate);
        Assert.Equal(new[] { 5 }, scalar.ReadRegisters);
        Assert.DoesNotContain(0, scalar.ReadRegisters);
        Assert.Equal(new[] { 7 }, scalar.WriteRegisters);
        Assert.Empty(scalar.ReadMemoryRanges);
        Assert.Empty(scalar.WriteMemoryRanges);
        Assert.True(scalar.ResourceMask.IsNonZero);
        Assert.NotEqual(0U, scalar.AdmissionMetadata.RegisterHazardMask);
        Assert.True(scalar.AdmissionMetadata.CertificateMask.IsNonZero);
    }

    [Theory]
    [MemberData(nameof(CompilerScalarBitfieldImmediateContours))]
    public void AppScalarBitfieldImmediate_RejectsOutOfRangeImm6BeforeCarrierEmission(
        InstructionsEnum opcode,
        uint expectedOpcode,
        string helperName,
        CompilerNonVmxScalarFeature requiredFeature)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => EmitScalarBitfieldImmediate(
                facade,
                opcode,
                new AsmRegister(7),
                new AsmRegister(5),
                64));
#pragma warning restore CS0618

        Assert.NotEqual(0u, expectedOpcode);
        Assert.Equal(helperName, ResolveCompilerOptionalHelperName(opcode));
        Assert.True(CompilerNonVmxScalarCapabilityModel.Default.Supports(requiredFeature));
        Assert.Equal("immediate6", exception.ParamName);
        Assert.Equal(0, context.InstructionCount);
    }

    [Theory]
    [MemberData(nameof(CompilerScalarBitfieldImmediateContours))]
    public void AppScalarBitfieldImmediate_MissingScalarBitfieldCapabilityFailsBeforeCarrierEmission(
        InstructionsEnum opcode,
        uint expectedOpcode,
        string helperName,
        CompilerNonVmxScalarFeature requiredFeature)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(
            0,
            context,
            CompilerNonVmxScalarCapabilityModel.Disabled);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => EmitScalarBitfieldImmediate(
                facade,
                opcode,
                new AsmRegister(7),
                new AsmRegister(5),
                4));
#pragma warning restore CS0618

        Assert.NotEqual(0u, expectedOpcode);
        Assert.Equal(helperName, ResolveCompilerOptionalHelperName(opcode));
        Assert.Contains(
            $"{ResolveCompilerOptionalMnemonic(opcode)} compiler emission requires Non-VMX scalar capability {requiredFeature}",
            exception.Message);
        Assert.Equal(0, context.InstructionCount);
    }

    [Theory]
    [MemberData(nameof(CompilerScalarWordUnaryTailContours))]
    public void AppScalarWordUnaryTail_EmitsCanonicalRuntimeClosedGate(
        InstructionsEnum opcode,
        uint expectedOpcode,
        string helperName)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        EmitScalarWordUnaryTail(facade, opcode, new AsmRegister(7), new AsmRegister(5));
#pragma warning restore CS0618

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(expectedOpcode, raw.OpCode);
        Assert.Equal(opcode, (InstructionsEnum)raw.OpCode);
        Assert.Equal(DataTypeEnum.INT32, raw.DataTypeValue);
        Assert.Equal(0, raw.PredicateMask);
        Assert.Equal(0, raw.Immediate);
        Assert.Equal(1u, raw.StreamLength);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(7, rawRd);
        Assert.Equal(5, rawRs1);
        Assert.Equal(0, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(opcode, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)ir.SerializationClass);
        Assert.Equal(IrResourceClass.ScalarAlu, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.AluClass, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Equal(IrSlotBindingKind.ClassFlexible, ir.Annotation.BindingKind);
        Assert.Contains(ir.Annotation.Defs, operand => operand.Name == "rd" && operand.Value == 7UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 5UL);
        Assert.DoesNotContain(ir.Annotation.Uses, operand => operand.Name == "rs2");
        Assert.Null(ir.Annotation.MemoryReadRegion);
        Assert.Null(ir.Annotation.MemoryWriteRegion);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);

        TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(materializedBundle);
        Assert.Equal(1, facts.FlexibleOpCount);
        Assert.Equal(0, facts.SystemSingletonCount);
        Assert.True(compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(materializedSlot!.SlotIndex);
        Assert.Equal(opcode, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(DataTypeEnum.INT32, (DataTypeEnum)(byte)lowered.DataTypeValue);
        Assert.Equal(0, lowered.Immediate);
        Assert.Equal(0, lowered.VirtualThreadId);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(lowered.Word1, out byte loweredRd, out byte loweredRs1, out byte loweredRs2));
        Assert.Equal(7, loweredRd);
        Assert.Equal(5, loweredRs1);
        Assert.Equal(0, loweredRs2);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            materializedSlot.SlotIndex);

        ScalarALUMicroOp scalar = Assert.IsType<ScalarALUMicroOp>(carrier);
        Assert.Equal(opcode, (InstructionsEnum)scalar.OpCode);
        Assert.Equal(helperName, ResolveCompilerTailHelperName(opcode));
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)scalar.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)scalar.SerializationClass);
        Assert.Equal(CanonicalDecodePublicationMode.SelfPublishes, scalar.CanonicalDecodePublication);
        Assert.False(scalar.UsesImmediate);
        Assert.Equal(new[] { 5 }, scalar.ReadRegisters);
        Assert.DoesNotContain(0, scalar.ReadRegisters);
        Assert.Equal(new[] { 7 }, scalar.WriteRegisters);
        Assert.Empty(scalar.ReadMemoryRanges);
        Assert.Empty(scalar.WriteMemoryRanges);
        Assert.True(scalar.ResourceMask.IsNonZero);
        Assert.NotEqual(0U, scalar.AdmissionMetadata.RegisterHazardMask);
        Assert.True(scalar.AdmissionMetadata.CertificateMask.IsNonZero);
    }

    [Fact]
    public void AppCountLeadingZeros_EmitsCanonicalClzRuntimeClosedGate()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        facade.CountLeadingZeros(new AsmRegister(7), new AsmRegister(5));
#pragma warning restore CS0618

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(54u, raw.OpCode);
        Assert.Equal(InstructionsEnum.CLZ, (InstructionsEnum)raw.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, raw.DataTypeValue);
        Assert.Equal(0, raw.PredicateMask);
        Assert.Equal(0, raw.Immediate);
        Assert.Equal(0u, raw.StreamLength);
        Assert.Equal(0, raw.Stride);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(7, rawRd);
        Assert.Equal(5, rawRs1);
        Assert.Equal(0, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(InstructionsEnum.CLZ, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)ir.DataType);
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)ir.SerializationClass);
        Assert.Equal(IrResourceClass.ScalarAlu, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.AluClass, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Equal(IrSlotBindingKind.ClassFlexible, ir.Annotation.BindingKind);
        Assert.Contains(ir.Annotation.Defs, operand => operand.Name == "rd" && operand.Value == 7UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 5UL);
        Assert.DoesNotContain(ir.Annotation.Uses, operand => operand.Name == "rs2");
        Assert.Null(ir.Annotation.MemoryReadRegion);
        Assert.Null(ir.Annotation.MemoryWriteRegion);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);

        TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(materializedBundle);
        Assert.Equal(1, facts.FlexibleOpCount);
        Assert.Equal(0, facts.SystemSingletonCount);
        Assert.True(compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(materializedSlot!.SlotIndex);
        Assert.Equal(InstructionsEnum.CLZ, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)lowered.DataTypeValue);
        Assert.Equal(0, lowered.Immediate);
        Assert.Equal(0, lowered.VirtualThreadId);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(lowered.Word1, out byte loweredRd, out byte loweredRs1, out byte loweredRs2));
        Assert.Equal(7, loweredRd);
        Assert.Equal(5, loweredRs1);
        Assert.Equal(0, loweredRs2);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            materializedSlot.SlotIndex);

        ScalarALUMicroOp scalar = Assert.IsType<ScalarALUMicroOp>(carrier);
        Assert.Equal(InstructionsEnum.CLZ, (InstructionsEnum)scalar.OpCode);
        Assert.Equal(nameof(AppAsmFacade.CountLeadingZeros), ResolveCompilerOptionalHelperName(InstructionsEnum.CLZ));
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)scalar.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)scalar.SerializationClass);
        Assert.Equal(CanonicalDecodePublicationMode.SelfPublishes, scalar.CanonicalDecodePublication);
        Assert.False(scalar.UsesImmediate);
        Assert.Equal(new[] { 5 }, scalar.ReadRegisters);
        Assert.DoesNotContain(0, scalar.ReadRegisters);
        Assert.Equal(new[] { 7 }, scalar.WriteRegisters);
        Assert.Empty(scalar.ReadMemoryRanges);
        Assert.Empty(scalar.WriteMemoryRanges);
        Assert.True(scalar.ResourceMask.IsNonZero);
        Assert.NotEqual(0U, scalar.AdmissionMetadata.RegisterHazardMask);
        Assert.True(scalar.AdmissionMetadata.CertificateMask.IsNonZero);
    }

    [Fact]
    public void AppCountTrailingZeros_EmitsCanonicalCtzPhase02UnaryContour()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        facade.CountTrailingZeros(new AsmRegister(7), new AsmRegister(5));
#pragma warning restore CS0618

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(59u, raw.OpCode);
        Assert.Equal(InstructionsEnum.CTZ, (InstructionsEnum)raw.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, raw.DataTypeValue);
        Assert.Equal(0, raw.PredicateMask);
        Assert.Equal(0, raw.Immediate);
        Assert.Equal(0u, raw.StreamLength);
        Assert.Equal(0, raw.Stride);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(7, rawRd);
        Assert.Equal(5, rawRs1);
        Assert.Equal(0, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(InstructionsEnum.CTZ, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)ir.DataType);
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)ir.SerializationClass);
        Assert.Equal(IrResourceClass.ScalarAlu, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.AluClass, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Equal(IrSlotBindingKind.ClassFlexible, ir.Annotation.BindingKind);
        Assert.Contains(ir.Annotation.Defs, operand => operand.Name == "rd" && operand.Value == 7UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 5UL);
        Assert.DoesNotContain(ir.Annotation.Uses, operand => operand.Name == "rs2");
        Assert.Null(ir.Annotation.MemoryReadRegion);
        Assert.Null(ir.Annotation.MemoryWriteRegion);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);

        TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(materializedBundle);
        Assert.Equal(1, facts.FlexibleOpCount);
        Assert.Equal(0, facts.SystemSingletonCount);
        Assert.True(compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(materializedSlot!.SlotIndex);
        Assert.Equal(InstructionsEnum.CTZ, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)lowered.DataTypeValue);
        Assert.Equal(0, lowered.Immediate);
        Assert.Equal(0, lowered.VirtualThreadId);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(lowered.Word1, out byte loweredRd, out byte loweredRs1, out byte loweredRs2));
        Assert.Equal(7, loweredRd);
        Assert.Equal(5, loweredRs1);
        Assert.Equal(0, loweredRs2);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            materializedSlot.SlotIndex);

        ScalarALUMicroOp scalar = Assert.IsType<ScalarALUMicroOp>(carrier);
        Assert.Equal(InstructionsEnum.CTZ, (InstructionsEnum)scalar.OpCode);
        Assert.Equal(nameof(AppAsmFacade.CountTrailingZeros), ResolveCompilerOptionalHelperName(InstructionsEnum.CTZ));
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)scalar.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)scalar.SerializationClass);
        Assert.Equal(CanonicalDecodePublicationMode.SelfPublishes, scalar.CanonicalDecodePublication);
        Assert.False(scalar.UsesImmediate);
        Assert.Equal(new[] { 5 }, scalar.ReadRegisters);
        Assert.DoesNotContain(0, scalar.ReadRegisters);
        Assert.Equal(new[] { 7 }, scalar.WriteRegisters);
        Assert.Empty(scalar.ReadMemoryRanges);
        Assert.Empty(scalar.WriteMemoryRanges);
        Assert.True(scalar.ResourceMask.IsNonZero);
        Assert.NotEqual(0U, scalar.AdmissionMetadata.RegisterHazardMask);
        Assert.True(scalar.AdmissionMetadata.CertificateMask.IsNonZero);
    }

    [Fact]
    public void AppCountTrailingZeros_MissingScalarBitmanipCapabilityFailsBeforeCarrierEmission()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(
            0,
            context,
            CompilerNonVmxScalarCapabilityModel.Disabled);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => facade.CountTrailingZeros(new AsmRegister(7), new AsmRegister(5)));
#pragma warning restore CS0618

        Assert.Contains("CTZ compiler emission requires Non-VMX scalar capability ScalarBitmanipCore", exception.Message);
        Assert.Equal(0, context.InstructionCount);
    }

    [Fact]
    public void AppCountSetBits_EmitsCanonicalCpopPhase02UnaryContour()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        facade.CountSetBits(new AsmRegister(7), new AsmRegister(5));
#pragma warning restore CS0618

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(334u, raw.OpCode);
        Assert.Equal(InstructionsEnum.CPOP, (InstructionsEnum)raw.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, raw.DataTypeValue);
        Assert.Equal(0, raw.PredicateMask);
        Assert.Equal(0, raw.Immediate);
        Assert.Equal(0u, raw.StreamLength);
        Assert.Equal(0, raw.Stride);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(7, rawRd);
        Assert.Equal(5, rawRs1);
        Assert.Equal(0, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(InstructionsEnum.CPOP, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)ir.DataType);
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)ir.SerializationClass);
        Assert.Equal(IrResourceClass.ScalarAlu, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.AluClass, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Equal(IrSlotBindingKind.ClassFlexible, ir.Annotation.BindingKind);
        Assert.Contains(ir.Annotation.Defs, operand => operand.Name == "rd" && operand.Value == 7UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 5UL);
        Assert.DoesNotContain(ir.Annotation.Uses, operand => operand.Name == "rs2");
        Assert.Null(ir.Annotation.MemoryReadRegion);
        Assert.Null(ir.Annotation.MemoryWriteRegion);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);

        TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(materializedBundle);
        Assert.Equal(1, facts.FlexibleOpCount);
        Assert.Equal(0, facts.SystemSingletonCount);
        Assert.True(compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(materializedSlot!.SlotIndex);
        Assert.Equal(InstructionsEnum.CPOP, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)lowered.DataTypeValue);
        Assert.Equal(0, lowered.Immediate);
        Assert.Equal(0, lowered.VirtualThreadId);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(lowered.Word1, out byte loweredRd, out byte loweredRs1, out byte loweredRs2));
        Assert.Equal(7, loweredRd);
        Assert.Equal(5, loweredRs1);
        Assert.Equal(0, loweredRs2);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            materializedSlot.SlotIndex);

        ScalarALUMicroOp scalar = Assert.IsType<ScalarALUMicroOp>(carrier);
        Assert.Equal(InstructionsEnum.CPOP, (InstructionsEnum)scalar.OpCode);
        Assert.Equal(nameof(AppAsmFacade.CountSetBits), ResolveCompilerOptionalHelperName(InstructionsEnum.CPOP));
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)scalar.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)scalar.SerializationClass);
        Assert.Equal(CanonicalDecodePublicationMode.SelfPublishes, scalar.CanonicalDecodePublication);
        Assert.False(scalar.UsesImmediate);
        Assert.Equal(new[] { 5 }, scalar.ReadRegisters);
        Assert.DoesNotContain(0, scalar.ReadRegisters);
        Assert.Equal(new[] { 7 }, scalar.WriteRegisters);
        Assert.Empty(scalar.ReadMemoryRanges);
        Assert.Empty(scalar.WriteMemoryRanges);
        Assert.True(scalar.ResourceMask.IsNonZero);
        Assert.NotEqual(0U, scalar.AdmissionMetadata.RegisterHazardMask);
        Assert.True(scalar.AdmissionMetadata.CertificateMask.IsNonZero);
    }

    [Fact]
    public void AppCountSetBits_MissingScalarBitmanipCapabilityFailsBeforeCarrierEmission()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(
            0,
            context,
            CompilerNonVmxScalarCapabilityModel.Disabled);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => facade.CountSetBits(new AsmRegister(7), new AsmRegister(5)));
#pragma warning restore CS0618

        Assert.Contains("CPOP compiler emission requires Non-VMX scalar capability ScalarBitmanipCore", exception.Message);
        Assert.Equal(0, context.InstructionCount);
    }

    [Theory]
    [MemberData(nameof(CompilerScalarBitmanipUnaryContours))]
    public void AppScalarBitmanipUnary_EmitsCanonicalPhase02UnaryContour(
        InstructionsEnum opcode,
        uint expectedOpcode,
        string helperName)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        EmitScalarBitmanipUnary(facade, opcode, new AsmRegister(7), new AsmRegister(5));
#pragma warning restore CS0618

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(expectedOpcode, raw.OpCode);
        Assert.Equal(opcode, (InstructionsEnum)raw.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, raw.DataTypeValue);
        Assert.Equal(0, raw.PredicateMask);
        Assert.Equal(0, raw.Immediate);
        Assert.Equal(0u, raw.StreamLength);
        Assert.Equal(0, raw.Stride);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(7, rawRd);
        Assert.Equal(5, rawRs1);
        Assert.Equal(0, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(opcode, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)ir.DataType);
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)ir.SerializationClass);
        Assert.Equal(IrResourceClass.ScalarAlu, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.AluClass, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Equal(IrSlotBindingKind.ClassFlexible, ir.Annotation.BindingKind);
        Assert.Contains(ir.Annotation.Defs, operand => operand.Name == "rd" && operand.Value == 7UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 5UL);
        Assert.DoesNotContain(ir.Annotation.Uses, operand => operand.Name == "rs2");
        Assert.Null(ir.Annotation.MemoryReadRegion);
        Assert.Null(ir.Annotation.MemoryWriteRegion);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);

        TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(materializedBundle);
        Assert.Equal(1, facts.FlexibleOpCount);
        Assert.Equal(0, facts.SystemSingletonCount);
        Assert.True(compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(materializedSlot!.SlotIndex);
        Assert.Equal(opcode, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)lowered.DataTypeValue);
        Assert.Equal(0, lowered.Immediate);
        Assert.Equal(0, lowered.VirtualThreadId);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(lowered.Word1, out byte loweredRd, out byte loweredRs1, out byte loweredRs2));
        Assert.Equal(7, loweredRd);
        Assert.Equal(5, loweredRs1);
        Assert.Equal(0, loweredRs2);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            materializedSlot.SlotIndex);

        ScalarALUMicroOp scalar = Assert.IsType<ScalarALUMicroOp>(carrier);
        Assert.Equal(opcode, (InstructionsEnum)scalar.OpCode);
        Assert.Equal(helperName, ResolveCompilerOptionalHelperName(opcode));
        Assert.Equal(InstructionClass.ScalarAlu, (InstructionClass)(byte)scalar.InstructionClass);
        Assert.Equal(SerializationClass.Free, (SerializationClass)(byte)scalar.SerializationClass);
        Assert.Equal(CanonicalDecodePublicationMode.SelfPublishes, scalar.CanonicalDecodePublication);
        Assert.False(scalar.UsesImmediate);
        Assert.Equal(new[] { 5 }, scalar.ReadRegisters);
        Assert.DoesNotContain(0, scalar.ReadRegisters);
        Assert.Equal(new[] { 7 }, scalar.WriteRegisters);
        Assert.Empty(scalar.ReadMemoryRanges);
        Assert.Empty(scalar.WriteMemoryRanges);
        Assert.True(scalar.ResourceMask.IsNonZero);
        Assert.NotEqual(0U, scalar.AdmissionMetadata.RegisterHazardMask);
        Assert.True(scalar.AdmissionMetadata.CertificateMask.IsNonZero);
    }

    [Theory]
    [MemberData(nameof(CompilerScalarBitmanipUnaryContours))]
    public void AppScalarBitmanipUnary_MissingScalarBitmanipCapabilityFailsBeforeCarrierEmission(
        InstructionsEnum opcode,
        uint expectedOpcode,
        string helperName)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(
            0,
            context,
            CompilerNonVmxScalarCapabilityModel.Disabled);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => EmitScalarBitmanipUnary(facade, opcode, new AsmRegister(7), new AsmRegister(5)));
#pragma warning restore CS0618

        Assert.NotEqual(0u, expectedOpcode);
        Assert.Equal(helperName, ResolveCompilerOptionalHelperName(opcode));
        Assert.Contains(
            $"{ResolveCompilerOptionalMnemonic(opcode)} compiler emission requires Non-VMX scalar capability ScalarBitmanipCore",
            exception.Message);
        Assert.Equal(0, context.InstructionCount);
    }

    [Fact]
    public void AppCountLeadingZeros_LoweredCarrierRetiresAndRollsBackThroughRuntimeChain()
    {
        const byte vtId = 2;
        const ushort sourceRegister = 5;
        const ushort destinationRegister = 7;
        const ulong pc = 0x6600UL;
        const ulong sourceValue = 0x0000_0000_0000_0010UL;
        const ulong originalDestinationValue = 0x1111_2222_3333_4444UL;
        const ulong expectedResult = 59UL;

        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(vtId);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        facade.CountLeadingZeros(
            new AsmRegister(destinationRegister),
            new AsmRegister(sourceRegister));
#pragma warning restore CS0618

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(InstructionsEnum.CLZ, (InstructionsEnum)(uint)ir.Opcode);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(materializedSlot!.SlotIndex);
        Assert.Equal(InstructionsEnum.CLZ, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(vtId, lowered.VirtualThreadId);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(lowered.Word1, out byte loweredRd, out byte loweredRs1, out byte loweredRs2));
        Assert.Equal(destinationRegister, loweredRd);
        Assert.Equal(sourceRegister, loweredRs1);
        Assert.Equal(0, loweredRs2);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            materializedSlot.SlotIndex);

        ScalarALUMicroOp scalar = Assert.IsType<ScalarALUMicroOp>(carrier);
        Assert.Equal(InstructionsEnum.CLZ, (InstructionsEnum)scalar.OpCode);
        Assert.Equal(new[] { (int)sourceRegister }, scalar.ReadRegisters);
        Assert.Equal(new[] { (int)destinationRegister }, scalar.WriteRegisters);
        scalar.OwnerThreadId = vtId;
        scalar.VirtualThreadId = vtId;
        scalar.OwnerContextId = vtId;
        scalar.RefreshAdmissionMetadata();

        Processor.CPU_Core core = new Processor.CPU_Core(0);
        core.InitializePipeline();
        core.PrepareExecutionStart(pc, activeVtId: vtId);
        core.WriteCommittedPc(vtId, pc);
        core.WriteCommittedArch(vtId, sourceRegister, sourceValue);
        core.WriteCommittedArch(vtId, destinationRegister, originalDestinationValue);

        HybridCPU_ISE.Core.ReplayToken rollbackToken = scalar.CreateRollbackToken(vtId);
        rollbackToken.CaptureRegisterState(ref core, [(int)destinationRegister]);

        core.TestRunExecuteStageWithDecodedInstruction(
            NativeTransportRuntimeAdapter.ToRuntime(in lowered),
            scalar,
            writesRegister: true,
            reg1Id: lowered.Reg1ID,
            reg2Id: lowered.Reg2ID,
            reg3Id: lowered.Reg3ID,
            pc: pc,
            admissionExecutionMode: RuntimeClusterAdmissionExecutionMode.ClusterPrepared);
        core.TestRunMemoryStageFromCurrentExecuteState();
        core.TestLatchMemoryToWriteBackTransferState();
        core.TestRunWriteBackStage();

        Assert.Equal(expectedResult, core.ReadArch(vtId, destinationRegister));
        Assert.Equal(sourceValue, core.ReadArch(vtId, sourceRegister));
        Assert.Equal(0UL, core.ReadArch(vtId, 0));
        Assert.Equal(1UL, core.GetPipelineControl().InstructionsRetired);

        rollbackToken.Rollback(ref core);
        Assert.Equal(originalDestinationValue, core.ReadArch(vtId, destinationRegister));
        Assert.Equal(sourceValue, core.ReadArch(vtId, sourceRegister));
    }

    [Fact]
    public void PlatformVSetVli_EmitsCanonicalVsetvliRuntimeClosedContour()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        PlatformAsmFacade facade = new PlatformAsmFacade(0, context);
        facade.VSetVli(new AsmRegister(5), new AsmRegister(6), DataTypeEnum.INT32);
#pragma warning restore CS0618

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(InstructionsEnum.VSETVLI, (InstructionsEnum)raw.OpCode);
        Assert.Equal(DataTypeEnum.INT32, raw.DataTypeValue);
        Assert.Equal(0, raw.PredicateMask);
        Assert.Equal(0, raw.Immediate);
        Assert.Equal(1u, raw.StreamLength);
        Assert.Equal(0, raw.Stride);
        Assert.Equal(0UL, raw.Src2Pointer);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(5, rawRd);
        Assert.Equal(6, rawRs1);
        Assert.Equal(VLIW_Instruction.NoArchReg, rawRs2);

        VliwBundleAnnotations sourceAnnotations = context.GetBundleAnnotations();
        Assert.True(sourceAnnotations.TryGetInstructionSlotMetadata(0, out InstructionSlotMetadata sourceMetadata));
        Assert.Null(sourceMetadata.DmaStreamComputeDescriptor);
        Assert.Null(sourceMetadata.AcceleratorCommandDescriptor);
        Assert.Equal(SlotClass.SystemSingleton, (SlotClass)(byte)sourceMetadata.SlotMetadata.AdmissionMetadata.Placement.RequiredSlotClass);
        Assert.Equal(SlotPinningKind.HardPinned, sourceMetadata.SlotMetadata.AdmissionMetadata.Placement.PinningKind);
        Assert.Equal(7, sourceMetadata.SlotMetadata.AdmissionMetadata.Placement.PinnedLaneId);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(InstructionsEnum.VSETVLI, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(InstructionClass.System, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(SerializationClass.FullSerial, (SerializationClass)(byte)ir.SerializationClass);
        Assert.Equal(IrResourceClass.System, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.SystemSingleton, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Equal(IrSlotBindingKind.HardPinned, ir.Annotation.BindingKind);
        Assert.Equal(IrIssueSlotMask.Slot7, ir.Annotation.LegalSlots);
        Assert.Null(ir.Annotation.MemoryReadRegion);
        Assert.Null(ir.Annotation.MemoryWriteRegion);
        Assert.Collection(
            ir.Annotation.Defs,
            operand =>
            {
                Assert.Equal("rd", operand.Name);
                Assert.Equal(5UL, operand.Value);
            });
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 6UL);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);
        Assert.Equal(7, materializedSlot!.SlotIndex);

        TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(materializedBundle);
        Assert.Equal(1, facts.SystemSingletonCount);
        Assert.Equal(0, facts.BranchControlCount);
        Assert.Equal(0, facts.DmaStreamCount);
        Assert.True(compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(7);
        Assert.Equal(InstructionsEnum.VSETVLI, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(DataTypeEnum.INT32, (DataTypeEnum)(byte)lowered.DataTypeValue);
        Assert.Equal(0, lowered.VirtualThreadId);
        Assert.Equal(0UL, lowered.Src2Pointer);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(lowered.Word1, out byte loweredRd, out byte loweredRs1, out byte loweredRs2));
        Assert.Equal(5, loweredRd);
        Assert.Equal(6, loweredRs1);
        Assert.Equal(VLIW_Instruction.NoArchReg, loweredRs2);

        IrBundleAnnotations loweredAnnotations = Assert.Single(compiledProgram.LoweredBundleAnnotations);
        Assert.True(loweredAnnotations.TryGetInstructionSlotMetadata(7, out IrInstructionSlotMetadata loweredMetadata));
        Assert.Null(loweredMetadata.DmaStreamComputeDescriptor);
        Assert.Null(loweredMetadata.AcceleratorCommandDescriptor);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            loweredAnnotations,
            slotIndex: 7);

        VConfigMicroOp vConfig = Assert.IsType<VConfigMicroOp>(carrier);
        Assert.Equal(InstructionsEnum.VSETVLI, (InstructionsEnum)vConfig.OpCode);
        Assert.Equal(VectorConfigOperationKind.Vsetvli, vConfig.OperationKind);
        Assert.Equal(InstructionClass.System, (InstructionClass)(byte)vConfig.InstructionClass);
        Assert.Equal(SerializationClass.FullSerial, (SerializationClass)(byte)vConfig.SerializationClass);
        Assert.Equal(CanonicalDecodePublicationMode.SelfPublishes, vConfig.CanonicalDecodePublication);
        Assert.Equal(SlotClass.SystemSingleton, (SlotClass)(byte)vConfig.Placement.RequiredSlotClass);
        Assert.Equal(SlotPinningKind.HardPinned, vConfig.Placement.PinningKind);
        Assert.Equal(7, vConfig.Placement.PinnedLaneId);
        Assert.True(vConfig.WritesRegister);
        Assert.Equal(new[] { 6 }, vConfig.ReadRegisters);
        Assert.Equal(new[] { 5 }, vConfig.WriteRegisters);
        Assert.Empty(vConfig.ReadMemoryRanges);
        Assert.Empty(vConfig.WriteMemoryRanges);
        Assert.False(vConfig.ResourceMask.IsZero);
        Assert.False(vConfig.SafetyMask.IsZero);
    }

    [Fact]
    public void PlatformReadSystemCycleCounter_EmitsCanonicalRdcycleSystemCounterContour()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        PlatformAsmFacade facade = new PlatformAsmFacade(0, context);
        facade.ReadSystemCycleCounter(new AsmRegister(7));
#pragma warning restore CS0618

        CompilerSystemCounterAbiContract contract = CompilerSystemCounterAbiContract.CycleCounter;
        Assert.Equal("RDCYCLE", contract.Mnemonic);
        Assert.Equal("ScalarSystemCounter", contract.ExtensionName);
        Assert.Equal(CsrAddresses.Cycle, contract.CsrAddress);
        Assert.Equal(CompilerSystemCounterReplayPolicy.RetireOrderedReplayStable, contract.ReplayPolicy);
        Assert.True(contract.CompilerEmissionAllowed);
        Assert.True(contract.HasOpcodeAllocation);
        Assert.True(contract.IsExecutable);
        Assert.True(contract.ReplayDeterminismPolicyResolved);
        Assert.True(contract.RetirePublicationPolicyResolved);
        Assert.Contains("ReplayRollbackRestoresArchitecturalTruth", contract.RequiredPolicyDecisions);
        contract.RequireCompilerEmissionAuthority();

        Assert.True(CompilerNonVmxScalarCapabilityModel.Default.Supports(CompilerNonVmxScalarFeature.ScalarSystemCounter));

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(57u, raw.OpCode);
        Assert.Equal(InstructionsEnum.RDCYCLE, (InstructionsEnum)raw.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, raw.DataTypeValue);
        Assert.Equal(0, raw.PredicateMask);
        Assert.Equal(0, raw.Immediate);
        Assert.Equal(0u, raw.StreamLength);
        Assert.Equal(0, raw.Stride);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(7, rawRd);
        Assert.Equal(0, rawRs1);
        Assert.Equal(0, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(InstructionsEnum.RDCYCLE, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)ir.DataType);
        Assert.Equal(0, ir.Immediate);
        Assert.Equal(InstructionClass.Csr, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(SerializationClass.CsrOrdered, (SerializationClass)(byte)ir.SerializationClass);
        Assert.Equal(IrResourceClass.System, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.SystemSingleton, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Equal(IrSlotBindingKind.HardPinned, ir.Annotation.BindingKind);
        Assert.Equal(IrIssueSlotMask.Slot7, ir.Annotation.LegalSlots);
        Assert.Contains(ir.Annotation.Defs, operand => operand.Name == "rd" && operand.Value == 7UL);
        Assert.DoesNotContain(ir.Annotation.Uses, operand => operand.Name == "rs1");
        Assert.DoesNotContain(ir.Annotation.Uses, operand => operand.Name == "rs2");
        Assert.Null(ir.Annotation.MemoryReadRegion);
        Assert.Null(ir.Annotation.MemoryWriteRegion);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);
        Assert.Equal(7, materializedSlot!.SlotIndex);

        TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(materializedBundle);
        Assert.Equal(1, facts.SystemSingletonCount);
        Assert.Equal(0, facts.FlexibleOpCount);
        Assert.True(compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(7);
        Assert.Equal(InstructionsEnum.RDCYCLE, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(DataTypeEnum.UINT64, (DataTypeEnum)(byte)lowered.DataTypeValue);
        Assert.Equal(0, lowered.Immediate);
        Assert.Equal(0, lowered.VirtualThreadId);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(lowered.Word1, out byte loweredRd, out byte loweredRs1, out byte loweredRs2));
        Assert.Equal(7, loweredRd);
        Assert.Equal(0, loweredRs1);
        Assert.Equal(0, loweredRs2);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            slotIndex: 7);

        CsrReadCounterMicroOp counter = Assert.IsType<CsrReadCounterMicroOp>(carrier);
        Assert.Equal(InstructionsEnum.RDCYCLE, (InstructionsEnum)counter.OpCode);
        Assert.Equal((ulong)CsrAddresses.Cycle, counter.CSRAddress);
        Assert.Equal(7, counter.DestRegID);
        Assert.Equal(InstructionClass.Csr, (InstructionClass)(byte)counter.InstructionClass);
        Assert.Equal(SerializationClass.CsrOrdered, (SerializationClass)(byte)counter.SerializationClass);
        Assert.Equal(CanonicalDecodePublicationMode.SelfPublishes, counter.CanonicalDecodePublication);
        Assert.Empty(counter.ReadRegisters);
        Assert.Equal(new[] { 7 }, counter.WriteRegisters);
        Assert.Equal(SlotClass.SystemSingleton, (SlotClass)(byte)counter.Placement.RequiredSlotClass);
        Assert.Equal(SlotPinningKind.HardPinned, counter.Placement.PinningKind);
        Assert.Equal(7, counter.Placement.PinnedLaneId);
    }

    [Fact]
    public void PlatformReadSystemCycleCounter_MissingSystemCounterCapabilityFailsBeforeCarrierEmission()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        PlatformAsmFacade facade = new PlatformAsmFacade(
            0,
            context,
            CompilerNonVmxScalarCapabilityModel.Disabled);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => facade.ReadSystemCycleCounter(new AsmRegister(7)));
#pragma warning restore CS0618

        Assert.Contains(
            "RDCYCLE compiler emission requires Non-VMX scalar capability ScalarSystemCounter",
            exception.Message);
        Assert.Equal(0, context.InstructionCount);
    }

    [Fact]
    public void ThreadCompileInstruction_RawVmxFuncTransportReachesVmxMicroOpWithoutHelperAuthority()
    {
        Assert.True(CompilerVmxAuthority.TryGetOpcodeAuthority(
            InstructionsEnum.VMFUNC,
            out CompilerVmxOpcodeAuthority authority));
        Assert.True(authority.RuntimeExecutable);
        Assert.False(authority.CompilerHelperEmittable);
        Assert.True(authority.RawTransportOnly);
        Assert.Equal(CompilerVmxAuthorityKind.RuntimeExecutable, authority.Authority);

        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);
        context.CompileInstruction(
            (uint)InstructionsEnum.VMFUNC,
            dataType: 0,
            predicate: 0,
            immediate: 0,
            destSrc1: VLIW_Instruction.PackArchRegs(5, 1, 2),
            src2: 0,
            streamLength: 0,
            stride: 0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(InstructionsEnum.VMFUNC, (InstructionsEnum)raw.OpCode);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(5, rawRd);
        Assert.Equal(1, rawRs1);
        Assert.Equal(2, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(InstructionsEnum.VMFUNC, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(InstructionClass.Vmx, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(SerializationClass.VmxSerial, (SerializationClass)(byte)ir.SerializationClass);
        Assert.Equal(IrResourceClass.System, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.SystemSingleton, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Equal(IrSlotBindingKind.HardPinned, ir.Annotation.BindingKind);
        Assert.Equal(IrIssueSlotMask.Slot7, ir.Annotation.LegalSlots);
        Assert.Null(ir.DmaStreamComputeDescriptor);
        Assert.Null(ir.AcceleratorCommandDescriptor);
        Assert.Contains(ir.Annotation.Defs, operand => operand.Name == "rd" && operand.Value == 5UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 1UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs2" && operand.Value == 2UL);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? materializedSlot));
        Assert.NotNull(materializedSlot);
        Assert.Equal(7, materializedSlot!.SlotIndex);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(7);
        Assert.Equal(InstructionsEnum.VMFUNC, (InstructionsEnum)lowered.OpCode);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(lowered.Word1, out byte loweredRd, out byte loweredRs1, out byte loweredRs2));
        Assert.Equal(5, loweredRd);
        Assert.Equal(1, loweredRs1);
        Assert.Equal(2, loweredRs2);

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            slotIndex: 7);

        VmxMicroOp vmx = Assert.IsType<VmxMicroOp>(carrier);
        Assert.Equal(InstructionsEnum.VMFUNC, (InstructionsEnum)vmx.OpCode);
        Assert.Equal(5, vmx.Rd);
        Assert.Equal(1, vmx.Rs1);
        Assert.Equal(2, vmx.Rs2);
        Assert.Equal(InstructionClass.Vmx, (InstructionClass)(byte)vmx.InstructionClass);
        Assert.Equal(SerializationClass.VmxSerial, (SerializationClass)(byte)vmx.SerializationClass);
        Assert.Equal(SlotClass.SystemSingleton, (SlotClass)(byte)vmx.Placement.RequiredSlotClass);
        Assert.Equal(SlotPinningKind.HardPinned, vmx.Placement.PinningKind);
        Assert.Equal(7, vmx.Placement.PinnedLaneId);
        Assert.Equal(new[] { 1, 2 }, vmx.ReadRegisters);
        Assert.Equal(new[] { 5 }, vmx.WriteRegisters);
    }

    [Fact]
    public void CurrentCompilerSources_ScopeSystemCounterAbiContractsAndKeepTimeCountersDeferred()
    {
        Assert.Equal(
            ["RDCYCLE", "RDINSTRET", "RDTIME"],
            CompilerSystemCounterAbiContract.AllSystemCounterRows
                .Select(static row => row.Mnemonic)
                .Order(StringComparer.Ordinal)
                .ToArray());

        CompilerSystemCounterAbiContract cycle = CompilerSystemCounterAbiContract.CycleCounter;
        Assert.True(cycle.CompilerEmissionAllowed);
        Assert.True(cycle.HasOpcodeAllocation);
        Assert.True(cycle.IsExecutable);
        Assert.True(cycle.ReplayDeterminismPolicyResolved);
        Assert.True(cycle.RetirePublicationPolicyResolved);
        Assert.Equal(CsrAddresses.Cycle, cycle.CsrAddress);
        cycle.RequireCompilerEmissionAuthority();

        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();
        Assert.Contains("InstructionsEnum.RDCYCLE", compilerSource, StringComparison.Ordinal);
        Assert.Contains(nameof(IPlatformAsmFacade.ReadSystemCycleCounter), compilerSource, StringComparison.Ordinal);
        Assert.Contains("ScalarSystemCounter", compilerSource, StringComparison.Ordinal);

        foreach (CompilerSystemCounterAbiContract contract in CompilerSystemCounterAbiContract.DeferredSystemCounterRows)
        {
            Assert.False(contract.CompilerEmissionAllowed);
            Assert.False(contract.HasOpcodeAllocation);
            Assert.False(contract.IsExecutable);
            Assert.False(contract.ReplayDeterminismPolicyResolved);
            Assert.False(contract.RetirePublicationPolicyResolved);
            Assert.True(contract.RequiresCounterSourceAbi);
            Assert.True(contract.RequiresReplayStableCounterModel);
            Assert.True(contract.RequiresPrivilegeVirtualizationPolicy);
            Assert.True(contract.RequiresRetireOwnedPublication);
            Assert.True(contract.RejectHostEvidenceLeak);
            Assert.True(contract.SeparateFromCycleCounter);
            Assert.Equal("Lane7CounterReplayDeferred", contract.EvidenceBoundary);
            Assert.Contains("RetireOwnedPublication", contract.RequiredPolicyDecisions);

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                contract.RequireCompilerEmissionAuthority);
            Assert.Contains($"{contract.Mnemonic} compiler emission is blocked", exception.Message, StringComparison.Ordinal);

            Assert.DoesNotContain(
                $"InstructionsEnum.{contract.Mnemonic}",
                compilerSource,
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                $"IsaOpcodeValues.{contract.Mnemonic}",
                compilerSource,
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                $"OpcodeValues.{contract.Mnemonic}",
                compilerSource,
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain("CompileRdTime", compilerSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EmitRdTime", compilerSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CompileRdInstret", compilerSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EmitRdInstret", compilerSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReadSystemTimeCounter", compilerSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReadRetiredInstructionCounter", compilerSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CurrentCompilerSources_ScopeLane7CounterHintNoEmissionAuditContractsWithoutHelpers()
    {
        Assert.Equal(
            ["PAUSE", "RDINSTRET", "RDTIME"],
            CompilerLane7CounterHintAbiContract.AllCounterHintRows
                .Select(static row => row.Mnemonic)
                .Order(StringComparer.Ordinal)
                .ToArray());

        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();
        string[] publicFacadeMethods =
        [
            .. typeof(IAppAsmFacade).GetMethods().Select(static method => method.Name),
            .. typeof(AppAsmFacade).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name),
            .. typeof(IPlatformAsmFacade).GetMethods().Select(static method => method.Name),
            .. typeof(PlatformAsmFacade).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name)
        ];

        foreach (CompilerLane7CounterHintAbiContract contract in CompilerLane7CounterHintAbiContract.AllCounterHintRows)
        {
            Assert.False(contract.CompilerEmissionAllowed);
            Assert.False(contract.CompilerHelperAllowed);
            Assert.False(contract.HasOpcodeAllocation);
            Assert.False(contract.IsExecutable);
            Assert.True(contract.NoGenericSystemOpFallback);
            Assert.True(contract.NoHiddenScalarLowering);
            Assert.True(contract.NoMultiOpEmission);
            Assert.False(string.IsNullOrWhiteSpace(contract.OperandShape));
            Assert.False(string.IsNullOrWhiteSpace(contract.DataSemantics));
            Assert.False(string.IsNullOrWhiteSpace(contract.ResultSemantics));

            switch (contract.AbiClass)
            {
                case CompilerLane7CounterHintAbiClass.ReplayStableTimeCounter:
                case CompilerLane7CounterHintAbiClass.RetireAccountingCounter:
                    CompilerSystemCounterAbiContract counterContract = Assert.Single(
                        CompilerSystemCounterAbiContract.DeferredSystemCounterRows,
                        row => row.Mnemonic == contract.Mnemonic);

                    Assert.Equal(counterContract.ExtensionName, contract.ExtensionName);
                    Assert.Equal(counterContract.EvidenceBoundary, contract.EvidenceBoundary);
                    Assert.Equal(counterContract.AbiDecision, contract.AbiDecision);
                    Assert.Equal(counterContract.OperandShape, contract.OperandShape);
                    Assert.Equal(counterContract.DataSemantics, contract.DataSemantics);
                    Assert.Equal(counterContract.ResultSemantics, contract.ResultSemantics);
                    Assert.True(contract.RequiresCounterSourceAbi);
                    Assert.True(contract.RequiresReplayStableCounterModel);
                    Assert.True(contract.RequiresPrivilegeVirtualizationPolicy);
                    Assert.True(contract.RequiresRetireOwnedPublication);
                    Assert.True(contract.RejectHostEvidenceLeak);
                    Assert.True(contract.SeparateFromCycleCounter);
                    Assert.Contains("RetireOwnedPublication", contract.RequiredPolicyDecisions);
                    if (contract.AbiClass == CompilerLane7CounterHintAbiClass.RetireAccountingCounter)
                    {
                        Assert.True(contract.RequiresRetireAccountingModel);
                        Assert.Contains("RetireAccountingModel", contract.RequiredPolicyDecisions);
                    }

                    break;
                case CompilerLane7CounterHintAbiClass.SchedulingHint:
                    CompilerLane7DeferredAbiContract hintContract = CompilerLane7DeferredAbiContract.PauseHint;
                    Assert.Equal(hintContract.Mnemonic, contract.Mnemonic);
                    Assert.Equal(hintContract.ExtensionName, contract.ExtensionName);
                    Assert.Equal(hintContract.EvidenceBoundary, contract.EvidenceBoundary);
                    Assert.Equal(hintContract.AbiDecision, contract.AbiDecision);
                    Assert.Equal(hintContract.OperandShape, contract.OperandShape);
                    Assert.Equal(hintContract.DataSemantics, contract.DataSemantics);
                    Assert.Equal(hintContract.ResultSemantics, contract.ResultSemantics);
                    Assert.True(contract.RequiresHintEncodingAbi);
                    Assert.True(contract.RequiresProgressFairnessPolicy);
                    Assert.True(contract.NoArchitecturalProgressGuarantee);
                    Assert.True(contract.RequiresNoArchitecturalStateLeakage);
                    Assert.True(contract.RejectSynchronizationPrimitiveSemantics);
                    Assert.True(contract.RequiresReplayRollbackEvidence);
                    Assert.Contains("NoSynchronizationPrimitiveSemantics", contract.RequiredPolicyDecisions);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(contract.AbiClass), contract.AbiClass, "Unsupported Lane7 counter/hint ABI class.");
            }

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                contract.RequireCompilerEmissionAuthority);
            Assert.Contains($"{contract.Mnemonic} compiler emission is blocked", exception.Message, StringComparison.Ordinal);

            string enumCandidate = contract.Mnemonic.Replace(".", "_", StringComparison.Ordinal);
            Assert.DoesNotContain($"InstructionsEnum.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"IsaOpcodeValues.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"OpcodeValues.{enumCandidate}", compilerSource, StringComparison.Ordinal);
        }

        foreach (string helperFragment in new[] { "RdTime", "RdInstret", "Pause", "SystemTimeCounter", "RetiredInstructionCounter" })
        {
            Assert.DoesNotContain($"Compile{helperFragment}", compilerSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"Emit{helperFragment}", compilerSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(publicFacadeMethods, methodName =>
                methodName.Contains(helperFragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void CurrentCompilerSources_ScopeLane7CacheTlbIommuNoEmissionAuditContractsWithoutHelpers()
    {
        Assert.Equal(
            CompilerFailClosedEmissionInventory.Lane7CacheTlbIommuRows.Select(static row => row.Mnemonic).Order(StringComparer.Ordinal),
            CompilerLane7CacheTlbIommuAbiContract.AllCacheTlbIommuRows
                .Select(static row => row.Mnemonic)
                .Order(StringComparer.Ordinal));

        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();
        string[] publicFacadeMethods =
        [
            .. typeof(IAppAsmFacade).GetMethods().Select(static method => method.Name),
            .. typeof(AppAsmFacade).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name),
            .. typeof(IPlatformAsmFacade).GetMethods().Select(static method => method.Name),
            .. typeof(PlatformAsmFacade).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name)
        ];

        foreach ((string mnemonic, string enumCandidate, string facadeHelperFragment) in CompilerFailClosedEmissionInventory.Lane7CacheTlbIommuRows)
        {
            CompilerLane7CacheTlbIommuAbiContract contract = Assert.Single(
                CompilerLane7CacheTlbIommuAbiContract.AllCacheTlbIommuRows,
                row => row.Mnemonic == mnemonic);

            CompilerLane7DeferredAbiContract deferredContract = Assert.Single(
                CompilerLane7DeferredAbiContract.AllDeferredLane7Rows,
                row => row.Mnemonic == mnemonic);

            Assert.Same(deferredContract, contract.DeferredContract);
            Assert.Equal(deferredContract.ExtensionName, contract.ExtensionName);
            Assert.Equal(deferredContract.EvidenceBoundary, contract.EvidenceBoundary);
            Assert.Equal(deferredContract.AbiDecision, contract.AbiDecision);
            Assert.Equal(deferredContract.OperandShape, contract.OperandShape);
            Assert.Equal(deferredContract.DataSemantics, contract.DataSemantics);
            Assert.Equal(deferredContract.ResultSemantics, contract.ResultSemantics);
            Assert.Equal(deferredContract.RequiredPolicyDecisions, contract.RequiredPolicyDecisions);
            Assert.False(contract.CompilerEmissionAllowed);
            Assert.False(contract.CompilerHelperAllowed);
            Assert.False(contract.HasOpcodeAllocation);
            Assert.False(contract.IsExecutable);
            Assert.True(contract.RequiresExplicitCompilerHelperAbi);
            Assert.True(contract.RequiresPublishedOpcodeEncodingAbi);
            Assert.True(contract.RequiresMetadataCapabilityAuthority);
            Assert.True(contract.RequiresRuntimeOwnedLegalityEvidence);
            Assert.True(contract.RequiresRetireReplayGoldenConformance);
            Assert.Contains("ExplicitCompilerHelperAbi", contract.RequiredEvidenceGates);
            Assert.Contains("PublishedOpcodeEncodingAbi", contract.RequiredEvidenceGates);
            Assert.Contains("RuntimeOwnedLegalityEvidence", contract.RequiredEvidenceGates);
            Assert.Contains("RetireReplayGoldenConformance", contract.RequiredEvidenceGates);
            Assert.True(contract.RequiresPrivilegeAndAdmissionPolicy);
            Assert.True(contract.RequiresReplayStableInvalidationModel);
            Assert.True(contract.RequiresRetireOwnedSideEffectPublication);
            Assert.True(contract.NoGenericFenceFallback);
            Assert.True(contract.NoHostEvidenceLeak);
            Assert.True(contract.NoHiddenScalarLowering);
            Assert.True(contract.NoMultiOpEmission);
            Assert.False(string.IsNullOrWhiteSpace(contract.HelperBoundary));

            switch (contract.AbiClass)
            {
                case CompilerLane7CacheTlbIommuAbiClass.TranslationFence:
                    Assert.Equal(CompilerLane7DeferredAbiClass.TranslationFence, deferredContract.AbiClass);
                    Assert.Equal("Lane7TranslationFenceDeferred", contract.EvidenceBoundary);
                    Assert.True(contract.RequiresTranslationFenceAbi);
                    Assert.True(contract.RequiresAddressSpaceSelectorAbi);
                    Assert.True(contract.RequiresTlbShootdownPolicy);
                    Assert.True(contract.RequiresCrossCoreShootdownPolicy);
                    Assert.True(contract.RequiresTranslationStateOwnershipModel);
                    Assert.True(contract.RequiresPageTableWalkOwnershipModel);
                    Assert.True(contract.NoVmxEptVpidNptSemanticAlias);
                    Assert.Contains("TlbShootdownPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoVmxEptVpidNptSemanticAlias", contract.RequiredPolicyDecisions);
                    break;
                case CompilerLane7CacheTlbIommuAbiClass.InstructionCacheMaintenance:
                    Assert.Equal(CompilerLane7DeferredAbiClass.CacheMaintenance, deferredContract.AbiClass);
                    Assert.Equal("Lane7CacheMaintenanceDeferred", contract.EvidenceBoundary);
                    Assert.True(contract.RequiresCacheMaintenanceAbi);
                    Assert.True(contract.RequiresInstructionFetchCoherencyModel);
                    Assert.True(contract.RequiresCacheHierarchyAuthorityModel);
                    Assert.True(contract.RequiresAddressRangeScopeAbi);
                    Assert.True(contract.NoFenceIFallback);
                    Assert.True(contract.VmxCacheEvidenceIsInsufficient);
                    Assert.Contains("InstructionFetchCoherencyModel", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoFenceIFallback", contract.RequiredPolicyDecisions);
                    break;
                case CompilerLane7CacheTlbIommuAbiClass.DataCacheMaintenance:
                    Assert.Equal(CompilerLane7DeferredAbiClass.CacheMaintenance, deferredContract.AbiClass);
                    Assert.Equal("Lane7CacheMaintenanceDeferred", contract.EvidenceBoundary);
                    Assert.True(contract.RequiresCacheMaintenanceAbi);
                    Assert.True(contract.RequiresDataCacheCoherencyModel);
                    Assert.True(contract.RequiresCacheHierarchyAuthorityModel);
                    Assert.True(contract.RequiresAddressRangeScopeAbi);
                    Assert.True(contract.RequiresDirtyLineOwnershipModel);
                    Assert.True(contract.RequiresMemoryOrderingIntegration);
                    Assert.True(contract.NoFenceIFallback);
                    Assert.True(contract.VmxCacheEvidenceIsInsufficient);
                    Assert.Contains("DataCacheCoherencyModel", contract.RequiredPolicyDecisions);
                    Assert.Contains("DirtyLineOwnershipModel", contract.RequiredPolicyDecisions);
                    Assert.Contains("MemoryOrderingIntegration", contract.RequiredPolicyDecisions);
                    break;
                case CompilerLane7CacheTlbIommuAbiClass.IotlbMaintenance:
                    Assert.Equal(CompilerLane7DeferredAbiClass.IommuMaintenance, deferredContract.AbiClass);
                    Assert.Equal("Lane7IommuMaintenanceDeferred", contract.EvidenceBoundary);
                    Assert.True(contract.RequiresIommuMaintenanceAbi);
                    Assert.True(contract.RequiresIotlbInvalidationModel);
                    Assert.False(contract.RequiresIommuFenceCompletionModel);
                    Assert.True(contract.RequiresDeviceDomainAuthority);
                    Assert.True(contract.RequiresDmaVisibilityModel);
                    Assert.True(contract.RequiresLane6TokenAuthorityGate);
                    Assert.True(contract.RequiresExternalDeviceQuiescencePolicy);
                    Assert.True(contract.NoVmxEptVpidNptSemanticAlias);
                    Assert.True(contract.VmxIommuEvidenceIsInsufficient);
                    Assert.True(contract.ExistingLane6DmaEvidenceIsInsufficient);
                    Assert.True(contract.ExistingLane7ControlPlaneEvidenceIsInsufficient);
                    Assert.True(contract.NoLane6DmaFallback);
                    Assert.True(contract.NoLane7AcceleratorFallback);
                    Assert.True(contract.NoExternalBackendFallback);
                    Assert.Contains("IotlbInvalidationModel", contract.RequiredPolicyDecisions);
                    break;
                case CompilerLane7CacheTlbIommuAbiClass.IommuFenceMaintenance:
                    Assert.Equal(CompilerLane7DeferredAbiClass.IommuMaintenance, deferredContract.AbiClass);
                    Assert.Equal("Lane7IommuMaintenanceDeferred", contract.EvidenceBoundary);
                    Assert.True(contract.RequiresIommuMaintenanceAbi);
                    Assert.False(contract.RequiresIotlbInvalidationModel);
                    Assert.True(contract.RequiresIommuFenceCompletionModel);
                    Assert.True(contract.RequiresDeviceDomainAuthority);
                    Assert.True(contract.RequiresDmaVisibilityModel);
                    Assert.True(contract.RequiresLane6TokenAuthorityGate);
                    Assert.True(contract.RequiresExternalDeviceQuiescencePolicy);
                    Assert.True(contract.NoVmxEptVpidNptSemanticAlias);
                    Assert.True(contract.VmxIommuEvidenceIsInsufficient);
                    Assert.True(contract.ExistingLane6DmaEvidenceIsInsufficient);
                    Assert.True(contract.ExistingLane7ControlPlaneEvidenceIsInsufficient);
                    Assert.True(contract.NoLane6DmaFallback);
                    Assert.True(contract.NoLane7AcceleratorFallback);
                    Assert.True(contract.NoExternalBackendFallback);
                    Assert.Contains("IommuFenceCompletionModel", contract.RequiredPolicyDecisions);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(contract.AbiClass), contract.AbiClass, "Unsupported Lane7 cache/TLB/IOMMU ABI class.");
            }

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                contract.RequireCompilerEmissionAuthority);
            Assert.Contains($"{contract.Mnemonic} compiler emission is blocked", exception.Message, StringComparison.Ordinal);

            Assert.Contains(mnemonic, compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"InstructionsEnum.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"IsaOpcodeValues.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"OpcodeValues.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"Compile{facadeHelperFragment}", compilerSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"Emit{facadeHelperFragment}", compilerSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(publicFacadeMethods, methodName =>
                methodName.Contains(facadeHelperFragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void CurrentCompilerSources_ScopeLane7AcceleratorControlNoEmissionAuditContractsWithoutHelpers()
    {
        Assert.Equal(
            CompilerFailClosedEmissionInventory.Lane7AcceleratorControlRows.Select(static row => row.Mnemonic).Order(StringComparer.Ordinal),
            CompilerLane7AcceleratorControlAbiContract.AllAcceleratorControlRows
                .Select(static row => row.Mnemonic)
                .Order(StringComparer.Ordinal));

        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();
        string[] publicFacadeMethods =
        [
            .. typeof(IAppAsmFacade).GetMethods().Select(static method => method.Name),
            .. typeof(AppAsmFacade).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name),
            .. typeof(IPlatformAsmFacade).GetMethods().Select(static method => method.Name),
            .. typeof(PlatformAsmFacade).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name)
        ];

        foreach ((string mnemonic, string enumCandidate, string facadeHelperFragment) in CompilerFailClosedEmissionInventory.Lane7AcceleratorControlRows)
        {
            CompilerLane7AcceleratorControlAbiContract contract = Assert.Single(
                CompilerLane7AcceleratorControlAbiContract.AllAcceleratorControlRows,
                row => row.Mnemonic == mnemonic);

            CompilerLane7DeferredAbiContract deferredContract = Assert.Single(
                CompilerLane7DeferredAbiContract.AcceleratorControlRows,
                row => row.Mnemonic == mnemonic);

            Assert.Same(deferredContract, contract.DeferredContract);
            Assert.Equal(deferredContract.ExtensionName, contract.ExtensionName);
            Assert.Equal(deferredContract.EvidenceBoundary, contract.EvidenceBoundary);
            Assert.Equal(deferredContract.AbiDecision, contract.AbiDecision);
            Assert.Equal(deferredContract.OperandShape, contract.OperandShape);
            Assert.Equal(deferredContract.DataSemantics, contract.DataSemantics);
            Assert.Equal(deferredContract.ResultSemantics, contract.ResultSemantics);
            Assert.Equal(deferredContract.RequiredPolicyDecisions, contract.RequiredPolicyDecisions);
            Assert.False(contract.CompilerEmissionAllowed);
            Assert.False(contract.CompilerHelperAllowed);
            Assert.False(contract.HasOpcodeAllocation);
            Assert.False(contract.IsExecutable);
            Assert.True(contract.RequiresExplicitCompilerHelperAbi);
            Assert.True(contract.RequiresPublishedOpcodeEncodingAbi);
            Assert.True(contract.RequiresMetadataCapabilityAuthority);
            Assert.True(contract.RequiresRuntimeOwnedLegalityEvidence);
            Assert.True(contract.RequiresRetireReplayGoldenConformance);
            Assert.Contains("ExplicitCompilerHelperAbi", contract.RequiredEvidenceGates);
            Assert.Contains("PublishedOpcodeEncodingAbi", contract.RequiredEvidenceGates);
            Assert.Contains("RuntimeOwnedLegalityEvidence", contract.RequiredEvidenceGates);
            Assert.Contains("RetireReplayGoldenConformance", contract.RequiredEvidenceGates);
            Assert.Contains("NoHostEvidenceLeak", contract.RequiredEvidenceGates);
            Assert.True(contract.RequiresOwnerDomainGuard);
            Assert.True(contract.RequiresCommandQueueSemantics);
            Assert.True(contract.RequiresNoHostEvidenceLeak);
            Assert.True(contract.RequiresMigrationCheckpointPolicy);
            Assert.True(contract.RequiresFutureVirtualizationBoundaryPolicy);
            Assert.True(contract.VmxMigrationCheckpointEvidenceIsInsufficient);
            Assert.True(contract.ExistingAccelSubmitEvidenceIsInsufficient);
            Assert.True(contract.ExistingAccelQueryCapsEvidenceIsInsufficient);
            Assert.True(contract.ExistingTopologyQueueTaxonomyEvidenceIsInsufficient);
            Assert.True(contract.ExistingLane7ControlPlaneEvidenceIsInsufficient);
            Assert.True(contract.NoGenericSystemOpFallback);
            Assert.True(contract.NoLane6DmaFallback);
            Assert.True(contract.NoLane7SubmitFallback);
            Assert.True(contract.NoExternalBackendFallback);
            Assert.True(contract.NoHostEvidenceLeak);
            Assert.True(contract.NoHiddenScalarLowering);
            Assert.True(contract.NoMultiOpEmission);
            Assert.False(string.IsNullOrWhiteSpace(contract.HelperBoundary));

            switch (contract.AbiClass)
            {
                case CompilerLane7AcceleratorControlAbiClass.AcceleratorAbiQuery:
                    Assert.Equal(CompilerLane7DeferredAbiClass.AcceleratorCapability, deferredContract.AbiClass);
                    Assert.True(contract.IsAcceleratorCapabilityQuery);
                    Assert.False(contract.IsAcceleratorLifecycleControl);
                    Assert.False(contract.IsAcceleratorQueueBindingControl);
                    Assert.True(contract.RequiresCapabilityAuthority);
                    Assert.True(contract.RequiresAcceleratorAbiQueryContract);
                    Assert.False(contract.RequiresAcceleratorTopologyAbi);
                    Assert.True(contract.RequiresBoundedCapabilityResultFootprint);
                    Assert.False(contract.RequiresBoundedTopologyResultFootprint);
                    Assert.True(contract.RequiresResultScrubbingPolicy);
                    Assert.True(contract.RequiresRetireOwnedPublication);
                    Assert.True(contract.RequiresReplayStableCapabilityModel);
                    Assert.True(contract.RequiresBackendCapabilityAuthority);
                    Assert.True(contract.RequiresGuestVisibleCapabilityPolicy);
                    Assert.True(contract.VmxCapabilityEvidenceIsInsufficient);
                    Assert.True(contract.NoCapabilityPublicationBeforeAuthority);
                    Assert.Contains("AcceleratorAbiQueryContract", contract.RequiredPolicyDecisions);
                    Assert.Contains("BoundedCapabilityResultFootprint", contract.RequiredPolicyDecisions);
                    break;
                case CompilerLane7AcceleratorControlAbiClass.AcceleratorTopologyQuery:
                    Assert.Equal(CompilerLane7DeferredAbiClass.AcceleratorCapability, deferredContract.AbiClass);
                    Assert.True(contract.IsAcceleratorCapabilityQuery);
                    Assert.False(contract.IsAcceleratorLifecycleControl);
                    Assert.False(contract.IsAcceleratorQueueBindingControl);
                    Assert.True(contract.RequiresCapabilityAuthority);
                    Assert.False(contract.RequiresAcceleratorAbiQueryContract);
                    Assert.True(contract.RequiresAcceleratorTopologyAbi);
                    Assert.False(contract.RequiresBoundedCapabilityResultFootprint);
                    Assert.True(contract.RequiresBoundedTopologyResultFootprint);
                    Assert.True(contract.RequiresResultScrubbingPolicy);
                    Assert.True(contract.RequiresRetireOwnedPublication);
                    Assert.True(contract.RequiresReplayStableCapabilityModel);
                    Assert.True(contract.RequiresBackendCapabilityAuthority);
                    Assert.True(contract.RequiresGuestVisibleCapabilityPolicy);
                    Assert.True(contract.VmxCapabilityEvidenceIsInsufficient);
                    Assert.True(contract.NoCapabilityPublicationBeforeAuthority);
                    Assert.Contains("AcceleratorTopologyAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("BoundedTopologyResultFootprint", contract.RequiredPolicyDecisions);
                    break;
                case CompilerLane7AcceleratorControlAbiClass.AcceleratorLifecycle:
                    Assert.Equal(CompilerLane7DeferredAbiClass.AcceleratorLifecycle, deferredContract.AbiClass);
                    Assert.False(contract.IsAcceleratorCapabilityQuery);
                    Assert.True(contract.IsAcceleratorLifecycleControl);
                    Assert.False(contract.IsAcceleratorQueueBindingControl);
                    Assert.True(contract.RequiresAcceleratorRuntimeAuthority);
                    Assert.True(contract.RequiresDeviceAuthority);
                    Assert.True(contract.RequiresTokenAuthority);
                    Assert.True(contract.RequiresHandleNamespaceAbi);
                    Assert.True(contract.RequiresOpenCloseLifecycleAbi);
                    Assert.True(contract.RequiresReplayStableLifecycleModel);
                    Assert.True(contract.RequiresRetireOwnedSideEffectPublication);
                    Assert.True(contract.VmxBackendAuthorityEvidenceIsInsufficient);
                    Assert.True(contract.NoLifecycleStatePublicationBeforeRetire);
                    Assert.True(contract.NoBackendAdmissionBeforeAuthority);
                    Assert.Contains("OpenCloseLifecycleAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoLifecycleStatePublicationBeforeRetire", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoBackendAdmissionBeforeAuthority", contract.RequiredPolicyDecisions);
                    break;
                case CompilerLane7AcceleratorControlAbiClass.AcceleratorQueueBinding:
                    Assert.Equal(CompilerLane7DeferredAbiClass.AcceleratorQueueBinding, deferredContract.AbiClass);
                    Assert.False(contract.IsAcceleratorCapabilityQuery);
                    Assert.False(contract.IsAcceleratorLifecycleControl);
                    Assert.True(contract.IsAcceleratorQueueBindingControl);
                    Assert.True(contract.RequiresAcceleratorRuntimeAuthority);
                    Assert.True(contract.RequiresQueueAuthority);
                    Assert.True(contract.RequiresTokenAuthority);
                    Assert.True(contract.RequiresLane6TokenAuthorityGate);
                    Assert.True(contract.RequiresBindUnbindQueueAbi);
                    Assert.True(contract.RequiresQueueOwnershipModel);
                    Assert.True(contract.RequiresReplayStableQueueBindingModel);
                    Assert.True(contract.RequiresQueueBindUnbindOrderingModel);
                    Assert.True(contract.RequiresRetireOwnedSideEffectPublication);
                    Assert.True(contract.VmxBackendAuthorityEvidenceIsInsufficient);
                    Assert.True(contract.ExistingLane6DmaEvidenceIsInsufficient);
                    Assert.True(contract.NoQueueBindingPublicationBeforeRetire);
                    Assert.True(contract.NoQueueBindingBeforeTokenAuthority);
                    Assert.Contains("BindUnbindQueueAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("QueueOwnershipModel", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoQueueBindingPublicationBeforeRetire", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoQueueBindingBeforeTokenAuthority", contract.RequiredPolicyDecisions);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(contract.AbiClass), contract.AbiClass, "Unsupported Lane7 accelerator-control ABI class.");
            }

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                contract.RequireCompilerEmissionAuthority);
            Assert.Contains($"{contract.Mnemonic} compiler emission is blocked", exception.Message, StringComparison.Ordinal);

            Assert.Contains(mnemonic, compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"InstructionsEnum.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"IsaOpcodeValues.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"OpcodeValues.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"Compile{facadeHelperFragment}", compilerSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"Emit{facadeHelperFragment}", compilerSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(publicFacadeMethods, methodName =>
                methodName.Contains(facadeHelperFragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void CurrentCompilerSources_ScopeLane6DeferredAbiContractsWithoutEmissionAuthority()
    {
        Assert.Equal(
            CompilerFailClosedEmissionInventory.Lane6DeferredRows.Select(static row => row.Mnemonic).Order(StringComparer.Ordinal),
            CompilerLane6DeferredAbiContract.AllDeferredLane6Rows.Select(static row => row.Mnemonic).Order(StringComparer.Ordinal));

        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();
        string[] publicCompilerMethods =
        [
            .. typeof(IAppAsmFacade).GetMethods().Select(static method => method.Name),
            .. typeof(AppAsmFacade).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name),
            .. typeof(IPlatformAsmFacade).GetMethods().Select(static method => method.Name),
            .. typeof(PlatformAsmFacade).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name),
            .. typeof(HybridCpuThreadCompilerContext).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
        ];

        foreach ((string mnemonic, string enumCandidate, string contractMetadataFragment, string facadeHelperFragment) in CompilerFailClosedEmissionInventory.Lane6DeferredRows)
        {
            CompilerLane6DeferredAbiContract contract = Assert.Single(
                CompilerLane6DeferredAbiContract.AllDeferredLane6Rows,
                row => row.Mnemonic == mnemonic);

            Assert.False(contract.CompilerEmissionAllowed);
            Assert.False(contract.CompilerHelperAllowed);
            Assert.False(contract.HasOpcodeAllocation);
            Assert.False(contract.HasScalarOpcodeAllocation);
            Assert.False(contract.IsExecutable);
            Assert.False(string.IsNullOrWhiteSpace(contract.OperandShape));
            Assert.False(string.IsNullOrWhiteSpace(contract.DataSemantics));
            Assert.False(string.IsNullOrWhiteSpace(contract.ResultSemantics));
            Assert.True(contract.NoHostEvidenceLeak);
            Assert.True(contract.NoHiddenScalarLowering);
            Assert.True(contract.NoHiddenVectorLowering);
            Assert.True(contract.NoMultiOpEmission);
            Assert.True(contract.NoCompilerHelperEmission);
            Assert.True(contract.NoRuntimeAdmissionPublication);
            Assert.True(contract.NoRetireCommitPublication);
            Assert.True(contract.NoLane7Fallback);
            Assert.True(contract.NoExternalBackendFallback);
            Assert.True(contract.NoVmxSpecificPath);

            switch (contract.AbiClass)
            {
                case CompilerLane6DeferredAbiClass.QueueControl:
                    Assert.Equal("Lane6QueueControl", contract.ExtensionName);
                    Assert.Equal("Lane6QueueControlNoExecution", contract.EvidenceBoundary);
                    Assert.True(contract.IsQueueControl);
                    Assert.False(contract.IsCapabilityQuery);
                    Assert.False(contract.IsParserOnly);
                    Assert.True(contract.RequiresDecoderEncoderAbi);
                    Assert.True(contract.RequiresInstructionIrProjection);
                    Assert.True(contract.RequiresRegistryMaterializer);
                    Assert.True(contract.RequiresSchedulerLaneBinding);
                    Assert.True(contract.RequiresQueueAuthority);
                    Assert.True(contract.RequiresTokenNamespaceAbi);
                    Assert.True(contract.RequiresQueueHandleAbi);
                    Assert.True(contract.RequiresTokenLifecycleAbi);
                    Assert.True(contract.RequiresQueueOwnershipModel);
                    Assert.True(contract.RequiresQueueStateModel);
                    Assert.True(contract.RequiresQueueRollbackJournal);
                    Assert.True(contract.RequiresQueueRuntimeAdmission);
                    Assert.True(contract.RequiresQueueCommandEncoding);
                    Assert.True(contract.RequiresRetireOwnedPublication);
                    Assert.True(contract.RequiresRetireOwnedSideEffectPublication);
                    Assert.True(contract.RequiresReplayRollbackConformance);
                    Assert.True(contract.RequiresGoldenArtifacts);
                    Assert.True(contract.RequiresFutureVirtualizationBoundaryPolicy);
                    Assert.True(contract.RequiresNoHostEvidenceLeak);
                    Assert.True(contract.NoRetirePublicationBeforeQueueAuthority);
                    Assert.True(contract.ExistingDmaStreamComputeEvidenceIsInsufficient);
                    Assert.True(contract.ExistingDscStatusEvidenceIsInsufficient);
                    Assert.True(contract.ExistingDscQueryCapsEvidenceIsInsufficient);
                    Assert.True(contract.Dsc2ParserEvidenceIsInsufficient);
                    Assert.True(contract.NoDmaStreamComputeFallback);
                    Assert.True(contract.NoDscStatusFallback);
                    Assert.True(contract.NoDscQueryCapsFallback);
                    Assert.True(contract.NoDsc2Fallback);
                    Assert.Contains("QueueAuthority", contract.RequiredPolicyDecisions);
                    Assert.Contains("TokenLifecycleAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("RetireOwnedSideEffectPublication", contract.RequiredPolicyDecisions);
                    Assert.Contains("ReplayRollbackConformance", contract.RequiredPolicyDecisions);

                    switch (contract.Mnemonic)
                    {
                        case "DSC_POLL":
                            Assert.True(contract.RequiresDscPollCompletionSemantics);
                            Assert.False(contract.RequiresCommandScopeAbi);
                            Assert.False(contract.RequiresQueueOrderingAbi);
                            Assert.False(contract.RequiresStagedCommitAuthority);
                            Assert.Contains("DscPollCompletionSemantics", contract.RequiredPolicyDecisions);
                            break;
                        case "DSC_WAIT":
                            Assert.True(contract.RequiresCommandScopeAbi);
                            Assert.True(contract.RequiresWaitProgressFairnessPolicy);
                            Assert.Contains("CommandScopeAbi", contract.RequiredPolicyDecisions);
                            Assert.Contains("WaitProgressFairnessPolicy", contract.RequiredPolicyDecisions);
                            break;
                        case "DSC_CANCEL":
                            Assert.True(contract.RequiresCommandScopeAbi);
                            Assert.True(contract.RequiresCancelRollbackJournal);
                            Assert.Contains("CancelRollbackJournal", contract.RequiredPolicyDecisions);
                            break;
                        case "DSC_FENCE":
                            Assert.True(contract.RequiresQueueOrderingAbi);
                            Assert.True(contract.RequiresFenceCompletionOrderingModel);
                            Assert.Contains("QueueOrderingAbi", contract.RequiredPolicyDecisions);
                            break;
                        case "DSC_COMMIT":
                            Assert.True(contract.RequiresStagedCommitAuthority);
                            Assert.True(contract.RequiresCommitPublicationModel);
                            Assert.Contains("StagedCommitAuthority", contract.RequiredPolicyDecisions);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(contract.Mnemonic), contract.Mnemonic, "Unsupported Lane6 queue-control row.");
                    }

                    break;
                case CompilerLane6DeferredAbiClass.CapabilityQuery:
                    Assert.Equal("Lane6DscQuery", contract.ExtensionName);
                    Assert.Equal("Lane6CapabilityQueryNoExecution", contract.EvidenceBoundary);
                    Assert.False(contract.IsQueueControl);
                    Assert.True(contract.IsCapabilityQuery);
                    Assert.True(contract.IsReadOnlyQuery);
                    Assert.True(contract.RequiresDecoderEncoderAbi);
                    Assert.True(contract.RequiresInstructionIrProjection);
                    Assert.True(contract.RequiresRegistryMaterializer);
                    Assert.True(contract.RequiresSchedulerLaneBinding);
                    Assert.True(contract.RequiresCapabilityQueryAbi);
                    Assert.True(contract.RequiresQuerySelectorAbi);
                    Assert.True(contract.RequiresCapabilityResultAbi);
                    Assert.True(contract.RequiresBoundedResultFootprint);
                    Assert.True(contract.RequiresResultScrubbingPolicy);
                    Assert.True(contract.RequiresRetireOwnedPublication);
                    Assert.True(contract.RequiresReplayStableResult);
                    Assert.True(contract.RequiresReplayRollbackConformance);
                    Assert.True(contract.RequiresGoldenArtifacts);
                    Assert.True(contract.RequiresFutureVirtualizationBoundaryPolicy);
                    Assert.True(contract.RequiresNoHostEvidenceLeak);
                    Assert.True(contract.NoRetirePublicationBeforeQueryAuthority);
                    Assert.True(contract.NoDscQueryCapsFallback);
                    Assert.Contains("CapabilityQueryAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("BoundedResultFootprint", contract.RequiredPolicyDecisions);
                    Assert.Contains("ResultScrubbingPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("ReplayStableResult", contract.RequiredPolicyDecisions);
                    if (contract.Mnemonic == "DSC_QUERY_BACKEND")
                    {
                        Assert.True(contract.RequiresBackendCapabilityAbi);
                        Assert.False(contract.RequiresShapeQueryAbi);
                        Assert.Contains("BackendCapabilityAbi", contract.RequiredPolicyDecisions);
                    }
                    else
                    {
                        Assert.False(contract.RequiresBackendCapabilityAbi);
                        Assert.True(contract.RequiresShapeQueryAbi);
                        Assert.Contains("ShapeQueryAbi", contract.RequiredPolicyDecisions);
                    }

                    break;
                case CompilerLane6DeferredAbiClass.DescriptorOp:
                    Assert.Equal("Lane6DescriptorOp", contract.ExtensionName);
                    Assert.Equal("Lane6DescriptorOwnedNoExecution", contract.EvidenceBoundary);
                    Assert.True(contract.IsDescriptorOwned);
                    Assert.True(contract.IsDescriptorOnly);
                    Assert.True(contract.IsDescriptorParserOnlyBoundary);
                    Assert.True(contract.IsDescriptorOp);
                    Assert.False(contract.IsDescriptorShape);
                    Assert.True(contract.RequiresDescriptorOpAbi);
                    Assert.False(contract.RequiresShapeAbi);
                    Assert.True(contract.RequiresDescriptorPayloadAbi);
                    Assert.True(contract.RequiresTypedDescriptorProjection);
                    Assert.True(contract.RequiresDescriptorMaterializer);
                    Assert.True(contract.RequiresDescriptorParserValidation);
                    Assert.True(contract.RequiresDescriptorOpTypeAbi);
                    Assert.True(contract.RequiresBackendRuntimeAdmission);
                    Assert.True(contract.RuntimeExecutionEvidenceAbsent);
                    Assert.True(contract.RequiresRuntimeAdmission);
                    Assert.True(contract.RequiresRetireCommitAuthority);
                    Assert.True(contract.RequiresReplayDeterminism);
                    Assert.True(contract.RequiresReplayRollbackConformance);
                    Assert.True(contract.RequiresGoldenArtifacts);
                    Assert.True(contract.RequiresRetireReplayGoldenEvidence);
                    Assert.True(contract.RuntimeOwnedLegalityIsFinal);
                    Assert.True(contract.NoDmaStreamComputeFallback);
                    Assert.True(contract.NoGenericDmaStreamComputeFallbackAsAuthority);
                    Assert.True(contract.NoDsc2Fallback);
                    Assert.True(contract.NoQueueRuntimeFallback);
                    Assert.Contains("DescriptorOpAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("RuntimeExecutionEvidenceAbsent", contract.RequiredPolicyDecisions);
                    Assert.Contains("RuntimeOwnedLegalityFinal", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoGenericDmaStreamComputeFallbackAsAuthority", contract.RequiredPolicyDecisions);

                    switch (contract.Mnemonic)
                    {
                        case "DmaStreamCompute.SUB":
                            Assert.True(contract.RequiresArithmeticPolicyAbi);
                            Assert.Contains("ArithmeticPolicyAbi", contract.RequiredPolicyDecisions);
                            break;
                        case "DmaStreamCompute.MIN":
                        case "DmaStreamCompute.MAX":
                            Assert.True(contract.RequiresSignednessTypePolicyAbi);
                            Assert.Contains("SignednessTypePolicyAbi", contract.RequiredPolicyDecisions);
                            break;
                        case "DmaStreamCompute.ABSDIFF":
                            Assert.True(contract.RequiresOverflowPolicyAbi);
                            Assert.Contains("OverflowPolicyAbi", contract.RequiredPolicyDecisions);
                            break;
                        case "DmaStreamCompute.CLAMP":
                            Assert.True(contract.RequiresBoundsPolicyAbi);
                            Assert.Contains("BoundsPolicyAbi", contract.RequiredPolicyDecisions);
                            break;
                        case "DmaStreamCompute.CONVERT":
                            Assert.True(contract.RequiresConversionPolicyAbi);
                            Assert.True(contract.RequiresRoundingSaturationTrapPolicy);
                            Assert.Contains("ConversionPolicyAbi", contract.RequiredPolicyDecisions);
                            Assert.Contains("RoundingSaturationTrapPolicy", contract.RequiredPolicyDecisions);
                            break;
                        case "DmaStreamCompute.COMPARE":
                            Assert.True(contract.RequiresPredicateFootprintAbi);
                            Assert.Contains("PredicateFootprintAbi", contract.RequiredPolicyDecisions);
                            break;
                        case "DmaStreamCompute.SELECT":
                            Assert.True(contract.RequiresPredicateFootprintAbi);
                            Assert.True(contract.RequiresSelectResultFootprintAbi);
                            Assert.Contains("SelectResultFootprintAbi", contract.RequiredPolicyDecisions);
                            break;
                        case "DmaStreamCompute.REDUCE_SUM":
                        case "DmaStreamCompute.REDUCE_MIN":
                        case "DmaStreamCompute.REDUCE_MAX":
                        case "DmaStreamCompute.REDUCE_AND":
                        case "DmaStreamCompute.REDUCE_OR":
                        case "DmaStreamCompute.REDUCE_XOR":
                            Assert.True(contract.RequiresReductionResultFootprintAbi);
                            Assert.True(contract.RequiresScalarOrSurfaceResultPolicy);
                            Assert.Contains("ReductionResultFootprintAbi", contract.RequiredPolicyDecisions);
                            Assert.Contains("ScalarOrSurfaceResultPolicy", contract.RequiredPolicyDecisions);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(contract.Mnemonic), contract.Mnemonic, "Unsupported Lane6 descriptor-op row.");
                    }

                    break;
                case CompilerLane6DeferredAbiClass.DescriptorShape:
                    Assert.Equal("Lane6DescriptorShape", contract.ExtensionName);
                    Assert.Equal("Lane6ShapeContourNoExecution", contract.EvidenceBoundary);
                    Assert.True(contract.IsDescriptorOwned);
                    Assert.True(contract.IsDescriptorOnly);
                    Assert.True(contract.IsDescriptorParserOnlyBoundary);
                    Assert.False(contract.IsDescriptorOp);
                    Assert.True(contract.IsDescriptorShape);
                    Assert.False(contract.RequiresDescriptorOpAbi);
                    Assert.True(contract.RequiresShapeAbi);
                    Assert.True(contract.RequiresDescriptorPayloadAbi);
                    Assert.True(contract.RequiresTypedDescriptorProjection);
                    Assert.True(contract.RequiresDescriptorMaterializer);
                    Assert.True(contract.RequiresShapeEnumAbi);
                    Assert.True(contract.RequiresShapeParserManifest);
                    Assert.True(contract.RequiresShapeFaultModel);
                    Assert.True(contract.RequiresAliasOverlapPolicy);
                    Assert.True(contract.RequiresBackendRuntimeAdmission);
                    Assert.True(contract.RuntimeExecutionEvidenceAbsent);
                    Assert.True(contract.RequiresRuntimeAdmission);
                    Assert.True(contract.RequiresRetireCommitAuthority);
                    Assert.True(contract.RequiresReplayDeterminism);
                    Assert.True(contract.RequiresReplayRollbackConformance);
                    Assert.True(contract.RequiresGoldenArtifacts);
                    Assert.True(contract.RequiresRetireReplayGoldenEvidence);
                    Assert.True(contract.RuntimeOwnedLegalityIsFinal);
                    Assert.True(contract.NoDmaStreamComputeFallback);
                    Assert.True(contract.NoGenericDmaStreamComputeFallbackAsAuthority);
                    Assert.True(contract.NoDsc2Fallback);
                    Assert.True(contract.NoQueueRuntimeFallback);
                    Assert.Contains("ShapeAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("ShapeParserManifest", contract.RequiredPolicyDecisions);
                    Assert.Contains("RuntimeExecutionEvidenceAbsent", contract.RequiredPolicyDecisions);
                    Assert.Contains("RuntimeOwnedLegalityFinal", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoGenericDmaStreamComputeFallbackAsAuthority", contract.RequiredPolicyDecisions);

                    switch (contract.Mnemonic)
                    {
                        case "DSC_SHAPE_STRIDED":
                            Assert.True(contract.RequiresStrideAbi);
                            Assert.Contains("StrideAbi", contract.RequiredPolicyDecisions);
                            break;
                        case "DSC_SHAPE_TILED":
                            Assert.True(contract.RequiresTileShapeAbi);
                            Assert.Contains("TileShapeAbi", contract.RequiredPolicyDecisions);
                            break;
                        case "DSC_SHAPE_SCATTER_GATHER":
                            Assert.True(contract.RequiresIndexSurfaceAbi);
                            Assert.Contains("IndexSurfaceAbi", contract.RequiredPolicyDecisions);
                            break;
                        case "DSC_SHAPE_2D":
                            Assert.True(contract.RequiresTwoDimensionalShapeAbi);
                            Assert.Contains("2DShapeAbi", contract.RequiredPolicyDecisions);
                            break;
                        case "DSC_SHAPE_MULTI_RANGE":
                            Assert.True(contract.RequiresMultiRangeAbi);
                            Assert.Contains("MultiRangeAbi", contract.RequiredPolicyDecisions);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(contract.Mnemonic), contract.Mnemonic, "Unsupported Lane6 descriptor-shape row.");
                    }

                    break;
                case CompilerLane6DeferredAbiClass.DescriptorParserV2:
                    Assert.Equal("Lane6DSC", contract.ExtensionName);
                    Assert.Equal("ParserOnlyCarrierNoExecution", contract.EvidenceBoundary);
                    Assert.False(contract.IsQueueControl);
                    Assert.False(contract.IsCapabilityQuery);
                    Assert.True(contract.IsDescriptorOwned);
                    Assert.True(contract.IsCarrierOnly);
                    Assert.True(contract.IsParserOnly);
                    Assert.True(contract.RequiresDescriptorV2Adr);
                    Assert.True(contract.RequiresDescriptorV2ParserManifest);
                    Assert.True(contract.RequiresBackwardCompatibleDecoder);
                    Assert.True(contract.RequiresDescriptorV2ExecutionPolicy);
                    Assert.True(contract.RequiresDescriptorV2AdmissionPolicy);
                    Assert.True(contract.RequiresRuntimeAdmission);
                    Assert.True(contract.RequiresRetireCommitAuthority);
                    Assert.True(contract.RequiresDescriptorV2RetireReplayPolicy);
                    Assert.True(contract.RequiresReplayDeterminism);
                    Assert.True(contract.RequiresParserOnlyConformance);
                    Assert.True(contract.RequiresDescriptorV2GoldenArtifacts);
                    Assert.True(contract.NoDsc2ExecutionBeforeAdr);
                    Assert.True(contract.ParserAcceptanceIsNotExecutionEvidence);
                    Assert.True(contract.NoParserToExecutionPromotion);
                    Assert.True(contract.Phase10DescriptorOpEvidenceIsInsufficient);
                    Assert.True(contract.NoExecutableDecoderEncoderAbiPublication);
                    Assert.True(contract.NoRuntimeAdmissionPublication);
                    Assert.True(contract.NoRetireCommitPublication);
                    Assert.True(contract.NoQueueRuntimeFallback);
                    Assert.Contains("DescriptorV2Adr", contract.RequiredPolicyDecisions);
                    Assert.Contains("DescriptorV2ParserManifest", contract.RequiredPolicyDecisions);
                    Assert.Contains("RuntimeAdmission", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoParserToExecutionPromotion", contract.RequiredPolicyDecisions);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(contract.AbiClass), contract.AbiClass, "Unsupported deferred Lane6 ABI class.");
            }

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                contract.RequireCompilerEmissionAuthority);
            Assert.Contains($"{mnemonic} compiler emission is blocked", exception.Message, StringComparison.Ordinal);

            Assert.Contains(contractMetadataFragment, compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"InstructionsEnum.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"IsaOpcodeValues.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"OpcodeValues.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"Compile{facadeHelperFragment}", compilerSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"Emit{facadeHelperFragment}", compilerSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(publicCompilerMethods, methodName =>
                methodName.Contains(facadeHelperFragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void CurrentCompilerSources_ScopeLane7DeferredAbiContractsWithoutEmissionAuthority()
    {
        Assert.Equal(
            CompilerFailClosedEmissionInventory.Lane7DeferredRows.Select(static row => row.Mnemonic).Order(StringComparer.Ordinal),
            CompilerLane7DeferredAbiContract.AllDeferredLane7Rows.Select(static row => row.Mnemonic).Order(StringComparer.Ordinal));

        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();
        string[] publicFacadeMethods =
        [
            .. typeof(IAppAsmFacade).GetMethods().Select(static method => method.Name),
            .. typeof(AppAsmFacade).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name),
            .. typeof(IPlatformAsmFacade).GetMethods().Select(static method => method.Name),
            .. typeof(PlatformAsmFacade).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name)
        ];

        foreach ((string mnemonic, string enumCandidate, string facadeHelperFragment) in CompilerFailClosedEmissionInventory.Lane7DeferredRows)
        {
            CompilerLane7DeferredAbiContract contract = Assert.Single(
                CompilerLane7DeferredAbiContract.AllDeferredLane7Rows,
                row => row.Mnemonic == mnemonic);

            Assert.False(contract.CompilerEmissionAllowed);
            Assert.False(contract.HasOpcodeAllocation);
            Assert.False(contract.IsExecutable);
            Assert.False(string.IsNullOrWhiteSpace(contract.OperandShape));
            Assert.False(string.IsNullOrWhiteSpace(contract.DataSemantics));
            Assert.False(string.IsNullOrWhiteSpace(contract.ResultSemantics));

            switch (contract.AbiClass)
            {
                case CompilerLane7DeferredAbiClass.SchedulingHint:
                    Assert.Equal("Lane7HintNoExecutionGuarantee", contract.EvidenceBoundary);
                    Assert.True(contract.RequiresHintEncodingAbi);
                    Assert.True(contract.RequiresProgressFairnessPolicy);
                    Assert.True(contract.NoArchitecturalProgressGuarantee);
                    Assert.True(contract.RequiresNoArchitecturalStateLeakage);
                    Assert.True(contract.RejectSynchronizationPrimitiveSemantics);
                    Assert.True(contract.RequiresReplayRollbackEvidence);
                    Assert.Contains("SchedulerFairnessPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoSynchronizationPrimitiveSemantics", contract.RequiredPolicyDecisions);
                    break;
                case CompilerLane7DeferredAbiClass.TranslationFence:
                    Assert.Equal("Lane7TranslationFenceDeferred", contract.EvidenceBoundary);
                    Assert.True(contract.RequiresPrivilegeAndAdmissionPolicy);
                    Assert.True(contract.RequiresAddressSpaceSelectorAbi);
                    Assert.True(contract.RequiresTlbShootdownPolicy);
                    Assert.True(contract.RequiresCrossCoreShootdownPolicy);
                    Assert.True(contract.RequiresReplayStableInvalidationModel);
                    Assert.True(contract.RequiresRetireOwnedSideEffectPublication);
                    Assert.True(contract.NoGenericFenceFallback);
                    Assert.True(contract.NoVmxEptVpidNptSemanticAlias);
                    Assert.True(contract.NoHostEvidenceLeak);
                    Assert.True(contract.NoHiddenScalarLowering);
                    Assert.True(contract.NoMultiOpEmission);
                    Assert.Contains("TlbShootdownPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoVmxEptVpidNptSemanticAlias", contract.RequiredPolicyDecisions);
                    break;
                case CompilerLane7DeferredAbiClass.CacheMaintenance:
                    Assert.Equal("Lane7CacheMaintenanceDeferred", contract.EvidenceBoundary);
                    Assert.True(contract.RequiresPrivilegeAndAdmissionPolicy);
                    Assert.True(contract.RequiresCacheMaintenanceAbi);
                    Assert.True(contract.RequiresCacheHierarchyAuthorityModel);
                    Assert.True(contract.RequiresAddressRangeScopeAbi);
                    Assert.True(contract.RequiresReplayStableInvalidationModel);
                    Assert.True(contract.RequiresRetireOwnedSideEffectPublication);
                    Assert.True(contract.NoGenericFenceFallback);
                    Assert.True(contract.NoFenceIFallback);
                    Assert.True(contract.VmxCacheEvidenceIsInsufficient);
                    Assert.True(contract.NoHostEvidenceLeak);
                    Assert.True(contract.NoHiddenScalarLowering);
                    Assert.True(contract.NoMultiOpEmission);
                    Assert.Contains("CacheMaintenanceAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("AddressRangeScopeAbi", contract.RequiredPolicyDecisions);
                    if (contract.Mnemonic == "ICACHE_INVAL")
                    {
                        Assert.True(contract.RequiresInstructionFetchCoherencyModel);
                        Assert.False(contract.RequiresDataCacheCoherencyModel);
                        Assert.False(contract.RequiresDirtyLineOwnershipModel);
                        Assert.Contains("InstructionFetchCoherencyModel", contract.RequiredPolicyDecisions);
                    }
                    else
                    {
                        Assert.True(contract.RequiresDataCacheCoherencyModel);
                        Assert.True(contract.RequiresDirtyLineOwnershipModel);
                        Assert.True(contract.RequiresMemoryOrderingIntegration);
                        Assert.Contains("DataCacheCoherencyModel", contract.RequiredPolicyDecisions);
                        Assert.Contains("DirtyLineOwnershipModel", contract.RequiredPolicyDecisions);
                        Assert.Contains("MemoryOrderingIntegration", contract.RequiredPolicyDecisions);
                    }

                    break;
                case CompilerLane7DeferredAbiClass.IommuMaintenance:
                    Assert.Equal("Lane7IommuMaintenanceDeferred", contract.EvidenceBoundary);
                    Assert.True(contract.RequiresPrivilegeAndAdmissionPolicy);
                    Assert.True(contract.RequiresIommuMaintenanceAbi);
                    Assert.True(contract.RequiresDeviceDomainAuthority);
                    Assert.True(contract.RequiresDmaVisibilityModel);
                    Assert.True(contract.RequiresLane6TokenAuthorityGate);
                    Assert.True(contract.RequiresExternalDeviceQuiescencePolicy);
                    Assert.True(contract.RequiresReplayStableInvalidationModel);
                    Assert.True(contract.RequiresRetireOwnedSideEffectPublication);
                    Assert.True(contract.NoGenericFenceFallback);
                    Assert.True(contract.NoVmxEptVpidNptSemanticAlias);
                    Assert.True(contract.VmxIommuEvidenceIsInsufficient);
                    Assert.True(contract.ExistingLane6DmaEvidenceIsInsufficient);
                    Assert.True(contract.ExistingLane7ControlPlaneEvidenceIsInsufficient);
                    Assert.True(contract.NoLane6DmaFallback);
                    Assert.True(contract.NoLane7AcceleratorFallback);
                    Assert.True(contract.NoExternalBackendFallback);
                    Assert.True(contract.NoHostEvidenceLeak);
                    Assert.True(contract.NoHiddenScalarLowering);
                    Assert.True(contract.NoMultiOpEmission);
                    Assert.Contains("IommuMaintenanceAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("DeviceDomainAuthority", contract.RequiredPolicyDecisions);
                    Assert.Contains("DmaVisibilityModel", contract.RequiredPolicyDecisions);
                    Assert.Contains("Lane6TokenAuthorityGate", contract.RequiredPolicyDecisions);
                    Assert.Contains("ExternalDeviceQuiescencePolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("PrivilegeAndAdmissionPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("ReplayStableInvalidationModel", contract.RequiredPolicyDecisions);
                    Assert.Contains("RetireOwnedSideEffectPublication", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoVmxEptVpidNptSemanticAlias", contract.RequiredPolicyDecisions);
                    if (contract.Mnemonic == "IOTLB_INV")
                    {
                        Assert.True(contract.RequiresIotlbInvalidationModel);
                        Assert.False(contract.RequiresIommuFenceCompletionModel);
                        Assert.Contains("IotlbInvalidationModel", contract.RequiredPolicyDecisions);
                    }
                    else
                    {
                        Assert.False(contract.RequiresIotlbInvalidationModel);
                        Assert.True(contract.RequiresIommuFenceCompletionModel);
                        Assert.Contains("IommuFenceCompletionModel", contract.RequiredPolicyDecisions);
                    }

                    break;
                case CompilerLane7DeferredAbiClass.AcceleratorCapability:
                    Assert.Equal("Lane7AcceleratorControlDeferred", contract.EvidenceBoundary);
                    Assert.True(contract.IsAcceleratorCapabilityQuery);
                    Assert.False(contract.IsAcceleratorLifecycleControl);
                    Assert.False(contract.IsAcceleratorQueueBindingControl);
                    Assert.True(contract.RequiresCapabilityAuthority);
                    Assert.True(contract.RequiresResultScrubbingPolicy);
                    Assert.True(contract.RequiresOwnerDomainGuard);
                    Assert.True(contract.RequiresCommandQueueSemantics);
                    Assert.True(contract.RequiresRetireOwnedPublication);
                    Assert.True(contract.RequiresReplayStableCapabilityModel);
                    Assert.True(contract.RequiresMigrationCheckpointPolicy);
                    Assert.True(contract.RequiresFutureVirtualizationBoundaryPolicy);
                    Assert.True(contract.RequiresBackendCapabilityAuthority);
                    Assert.True(contract.RequiresGuestVisibleCapabilityPolicy);
                    Assert.True(contract.RequiresNoHostEvidenceLeak);
                    Assert.True(contract.NoGenericSystemOpFallback);
                    Assert.True(contract.VmxCapabilityEvidenceIsInsufficient);
                    Assert.True(contract.VmxMigrationCheckpointEvidenceIsInsufficient);
                    Assert.True(contract.ExistingAccelSubmitEvidenceIsInsufficient);
                    Assert.True(contract.ExistingAccelQueryCapsEvidenceIsInsufficient);
                    Assert.True(contract.ExistingTopologyQueueTaxonomyEvidenceIsInsufficient);
                    Assert.True(contract.ExistingLane7ControlPlaneEvidenceIsInsufficient);
                    Assert.True(contract.NoLane6DmaFallback);
                    Assert.True(contract.NoLane7SubmitFallback);
                    Assert.True(contract.NoExternalBackendFallback);
                    Assert.True(contract.NoCapabilityPublicationBeforeAuthority);
                    Assert.True(contract.NoHostEvidenceLeak);
                    Assert.True(contract.NoHiddenScalarLowering);
                    Assert.True(contract.NoMultiOpEmission);
                    Assert.Contains("CapabilityAuthority", contract.RequiredPolicyDecisions);
                    Assert.Contains("ResultScrubbingPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("OwnerDomainGuard", contract.RequiredPolicyDecisions);
                    Assert.Contains("CommandQueueSemantics", contract.RequiredPolicyDecisions);
                    Assert.Contains("RetireOwnedPublication", contract.RequiredPolicyDecisions);
                    Assert.Contains("ReplayStableCapabilityModel", contract.RequiredPolicyDecisions);
                    Assert.Contains("BackendCapabilityAuthority", contract.RequiredPolicyDecisions);
                    Assert.Contains("GuestVisibleCapabilityPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoCapabilityPublicationBeforeAuthority", contract.RequiredPolicyDecisions);
                    if (contract.Mnemonic == "ACCEL_QUERY_ABI")
                    {
                        Assert.True(contract.RequiresAcceleratorAbiQueryContract);
                        Assert.False(contract.RequiresAcceleratorTopologyAbi);
                        Assert.True(contract.RequiresBoundedCapabilityResultFootprint);
                        Assert.False(contract.RequiresBoundedTopologyResultFootprint);
                        Assert.Contains("AcceleratorAbiQueryContract", contract.RequiredPolicyDecisions);
                        Assert.Contains("BoundedCapabilityResultFootprint", contract.RequiredPolicyDecisions);
                    }
                    else
                    {
                        Assert.False(contract.RequiresAcceleratorAbiQueryContract);
                        Assert.True(contract.RequiresAcceleratorTopologyAbi);
                        Assert.False(contract.RequiresBoundedCapabilityResultFootprint);
                        Assert.True(contract.RequiresBoundedTopologyResultFootprint);
                        Assert.Contains("AcceleratorTopologyAbi", contract.RequiredPolicyDecisions);
                        Assert.Contains("BoundedTopologyResultFootprint", contract.RequiredPolicyDecisions);
                    }

                    break;
                case CompilerLane7DeferredAbiClass.AcceleratorLifecycle:
                    Assert.Equal("Lane7AcceleratorControlDeferred", contract.EvidenceBoundary);
                    Assert.False(contract.IsAcceleratorCapabilityQuery);
                    Assert.True(contract.IsAcceleratorLifecycleControl);
                    Assert.False(contract.IsAcceleratorQueueBindingControl);
                    Assert.True(contract.RequiresAcceleratorRuntimeAuthority);
                    Assert.True(contract.RequiresDeviceAuthority);
                    Assert.True(contract.RequiresTokenAuthority);
                    Assert.True(contract.RequiresOwnerDomainGuard);
                    Assert.True(contract.RequiresHandleNamespaceAbi);
                    Assert.True(contract.RequiresOpenCloseLifecycleAbi);
                    Assert.True(contract.RequiresCommandQueueSemantics);
                    Assert.True(contract.RequiresReplayStableLifecycleModel);
                    Assert.True(contract.RequiresMigrationCheckpointPolicy);
                    Assert.True(contract.RequiresFutureVirtualizationBoundaryPolicy);
                    Assert.True(contract.RequiresNoHostEvidenceLeak);
                    Assert.True(contract.RequiresRetireOwnedSideEffectPublication);
                    Assert.True(contract.NoGenericSystemOpFallback);
                    Assert.True(contract.VmxBackendAuthorityEvidenceIsInsufficient);
                    Assert.True(contract.VmxMigrationCheckpointEvidenceIsInsufficient);
                    Assert.True(contract.ExistingAccelSubmitEvidenceIsInsufficient);
                    Assert.True(contract.ExistingAccelQueryCapsEvidenceIsInsufficient);
                    Assert.True(contract.ExistingTopologyQueueTaxonomyEvidenceIsInsufficient);
                    Assert.True(contract.ExistingLane7ControlPlaneEvidenceIsInsufficient);
                    Assert.True(contract.NoLane6DmaFallback);
                    Assert.True(contract.NoLane7SubmitFallback);
                    Assert.True(contract.NoExternalBackendFallback);
                    Assert.True(contract.NoLifecycleStatePublicationBeforeRetire);
                    Assert.True(contract.NoBackendAdmissionBeforeAuthority);
                    Assert.True(contract.NoHostEvidenceLeak);
                    Assert.True(contract.NoHiddenScalarLowering);
                    Assert.True(contract.NoMultiOpEmission);
                    Assert.Contains("AcceleratorRuntimeAuthority", contract.RequiredPolicyDecisions);
                    Assert.Contains("DeviceAuthority", contract.RequiredPolicyDecisions);
                    Assert.Contains("TokenAuthority", contract.RequiredPolicyDecisions);
                    Assert.Contains("HandleNamespaceAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("OpenCloseLifecycleAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("ReplayStableLifecycleModel", contract.RequiredPolicyDecisions);
                    Assert.Contains("RetireOwnedSideEffectPublication", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoLifecycleStatePublicationBeforeRetire", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoBackendAdmissionBeforeAuthority", contract.RequiredPolicyDecisions);
                    break;
                case CompilerLane7DeferredAbiClass.AcceleratorQueueBinding:
                    Assert.Equal("Lane7AcceleratorControlDeferred", contract.EvidenceBoundary);
                    Assert.False(contract.IsAcceleratorCapabilityQuery);
                    Assert.False(contract.IsAcceleratorLifecycleControl);
                    Assert.True(contract.IsAcceleratorQueueBindingControl);
                    Assert.True(contract.RequiresAcceleratorRuntimeAuthority);
                    Assert.True(contract.RequiresQueueAuthority);
                    Assert.True(contract.RequiresTokenAuthority);
                    Assert.True(contract.RequiresLane6TokenAuthorityGate);
                    Assert.True(contract.RequiresOwnerDomainGuard);
                    Assert.True(contract.RequiresBindUnbindQueueAbi);
                    Assert.True(contract.RequiresQueueOwnershipModel);
                    Assert.True(contract.RequiresCommandQueueSemantics);
                    Assert.True(contract.RequiresReplayStableQueueBindingModel);
                    Assert.True(contract.RequiresQueueBindUnbindOrderingModel);
                    Assert.True(contract.RequiresMigrationCheckpointPolicy);
                    Assert.True(contract.RequiresFutureVirtualizationBoundaryPolicy);
                    Assert.True(contract.RequiresNoHostEvidenceLeak);
                    Assert.True(contract.RequiresRetireOwnedSideEffectPublication);
                    Assert.True(contract.NoGenericSystemOpFallback);
                    Assert.True(contract.VmxBackendAuthorityEvidenceIsInsufficient);
                    Assert.True(contract.VmxMigrationCheckpointEvidenceIsInsufficient);
                    Assert.True(contract.ExistingLane6DmaEvidenceIsInsufficient);
                    Assert.True(contract.ExistingAccelSubmitEvidenceIsInsufficient);
                    Assert.True(contract.ExistingAccelQueryCapsEvidenceIsInsufficient);
                    Assert.True(contract.ExistingTopologyQueueTaxonomyEvidenceIsInsufficient);
                    Assert.True(contract.ExistingLane7ControlPlaneEvidenceIsInsufficient);
                    Assert.True(contract.NoLane6DmaFallback);
                    Assert.True(contract.NoLane7SubmitFallback);
                    Assert.True(contract.NoExternalBackendFallback);
                    Assert.True(contract.NoQueueBindingPublicationBeforeRetire);
                    Assert.True(contract.NoQueueBindingBeforeTokenAuthority);
                    Assert.True(contract.NoHostEvidenceLeak);
                    Assert.True(contract.NoHiddenScalarLowering);
                    Assert.True(contract.NoMultiOpEmission);
                    Assert.Contains("AcceleratorRuntimeAuthority", contract.RequiredPolicyDecisions);
                    Assert.Contains("QueueAuthority", contract.RequiredPolicyDecisions);
                    Assert.Contains("TokenAuthority", contract.RequiredPolicyDecisions);
                    Assert.Contains("Lane6TokenAuthorityGate", contract.RequiredPolicyDecisions);
                    Assert.Contains("BindUnbindQueueAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("QueueOwnershipModel", contract.RequiredPolicyDecisions);
                    Assert.Contains("ReplayStableQueueBindingModel", contract.RequiredPolicyDecisions);
                    Assert.Contains("QueueBindUnbindOrderingModel", contract.RequiredPolicyDecisions);
                    Assert.Contains("RetireOwnedSideEffectPublication", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoQueueBindingPublicationBeforeRetire", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoQueueBindingBeforeTokenAuthority", contract.RequiredPolicyDecisions);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(contract.AbiClass), contract.AbiClass, "Unsupported deferred Lane7 ABI class.");
            }

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                contract.RequireCompilerEmissionAuthority);
            Assert.Contains($"{mnemonic} compiler emission is blocked", exception.Message, StringComparison.Ordinal);

            Assert.DoesNotContain($"InstructionsEnum.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"IsaOpcodeValues.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"OpcodeValues.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"Compile{facadeHelperFragment}", compilerSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"Emit{facadeHelperFragment}", compilerSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(publicFacadeMethods, methodName =>
                methodName.Contains(facadeHelperFragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void CurrentCompilerSources_ScopeRuntimeExecutableVectorHelperClosedContractsWithoutTypedHelpers()
    {
        Assert.Equal(
            CompilerRuntimeExecutableHelperClosedVectorRows.Select(static row => row.Mnemonic).Order(StringComparer.Ordinal),
            CompilerVectorHelperClosedAbiContract.AllHelperClosedVectorRows.Select(static row => row.Mnemonic).Order(StringComparer.Ordinal));

        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();
        string[] publicCompilerMethods =
        [
            .. typeof(IAppAsmFacade).GetMethods().Select(static method => method.Name),
            .. typeof(AppAsmFacade).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name),
            .. typeof(IPlatformAsmFacade).GetMethods().Select(static method => method.Name),
            .. typeof(PlatformAsmFacade).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name),
            .. typeof(HybridCpuThreadCompilerContext).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
        ];

        foreach ((string mnemonic, string runtimeOpcodeCandidate, string contractMetadataFragment, string facadeHelperFragment) in CompilerRuntimeExecutableHelperClosedVectorRows)
        {
            CompilerVectorHelperClosedAbiContract contract = Assert.Single(
                CompilerVectorHelperClosedAbiContract.AllHelperClosedVectorRows,
                row => row.Mnemonic == mnemonic);

            Assert.Equal(runtimeOpcodeCandidate, contract.RuntimeOpcodeCandidate);
            Assert.True(contract.RuntimeExecutable);
            Assert.True(contract.HasRuntimeOpcodeAllocation);
            Assert.True(contract.HasRuntimeConformanceEvidence);
            Assert.True(contract.HasVectorLegalityMatrixEvidence);
            Assert.True(contract.RawVectorTransportAllowed);
            Assert.False(contract.RawVectorTransportIsHelperAuthority);
            Assert.False(contract.CompilerEmissionAllowed);
            Assert.False(contract.CompilerHelperAllowed);
            Assert.False(contract.TypedFacadeAllowed);
            Assert.False(contract.TypedHelperAllowed);
            Assert.True(contract.RequiresVectorHelperAbi);
            Assert.True(contract.RequiresOperandPayloadConstructionAbi);
            Assert.True(contract.RequiresCapabilityGatingAbi);
            Assert.True(contract.RequiresRetireReplayGoldenHelperEvidence);
            Assert.True(contract.RuntimeOwnedLegalityIsFinal);
            Assert.True(contract.NoRawVectorTransportPromotion);
            Assert.True(contract.NoTypedFacadeHelperEmission);
            Assert.True(contract.NoHiddenScalarLowering);
            Assert.True(contract.NoHiddenLane6Lowering);
            Assert.True(contract.NoLane7Fallback);
            Assert.True(contract.NoExternalBackendFallback);
            Assert.False(string.IsNullOrWhiteSpace(contract.OperandShape));
            Assert.False(string.IsNullOrWhiteSpace(contract.DataSemantics));
            Assert.False(string.IsNullOrWhiteSpace(contract.ResultSemantics));
            Assert.Contains("RuntimeOpcodeEvidenceIsNotHelperAuthority", contract.RequiredPolicyDecisions);
            Assert.Contains("VectorLegalityMatrixIsFinalRuntimeAuthority", contract.RequiredPolicyDecisions);
            Assert.Contains("NoRawVectorTransportPromotion", contract.RequiredPolicyDecisions);

            InstructionSupportStatus status = InstructionSupportStatusCatalog.GetStatus(mnemonic);
            Assert.Equal(IsaInstructionStatus.OptionalEnabled, status.Status);
            Assert.Equal(RuntimeInstructionEvidence.ConformanceTested, status.RuntimeEvidence);
            Assert.True(status.IsExecutableClaim, mnemonic);
            Assert.True(status.HasNumericOpcode, mnemonic);
            Assert.True(status.HasRuntimeOpcodeMetadata, mnemonic);
            Assert.True(status.HasCanonicalDecoderAcceptance, mnemonic);
            Assert.True(status.HasRegistryFactory, mnemonic);
            Assert.True(status.HasExecutionSemantics, mnemonic);

            InstructionsEnum runtimeOpcode = Enum.Parse<InstructionsEnum>(runtimeOpcodeCandidate);
            Assert.True(
                VectorLegalityMatrix.TryGetRow(runtimeOpcode, out VectorLegalityMatrixRow? legalityRow),
                runtimeOpcodeCandidate);
            Assert.NotNull(legalityRow);

            switch (contract.AbiClass)
            {
                case CompilerVectorHelperClosedAbiClass.IndexedMemory:
                    Assert.Equal("VectorIndexedMemory", contract.ExtensionName);
                    Assert.True(contract.IsIndexedMemory);
                    Assert.True(contract.RequiresIndexedMemoryHelperAbi);
                    Assert.True(contract.RequiresIndexedDescriptorPayloadAbi);
                    Assert.True(contract.RequiresFaultReplayPublicationPolicy);
                    Assert.True(contract.NoScalarMemoryFallback);
                    Assert.True(contract.NoLane6DmaFallback);
                    Assert.True(contract.NoDsc2Fallback);
                    Assert.Contains("IndexedMemoryHelperAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("FaultReplayPublicationPolicy", contract.RequiredPolicyDecisions);
                    Assert.NotEqual(contract.IsGather, contract.IsScatter);
                    break;
                case CompilerVectorHelperClosedAbiClass.MaskPrefix:
                    Assert.Equal("VectorMaskPrefixPublication", contract.ExtensionName);
                    Assert.True(contract.IsMaskPrefix);
                    Assert.True(contract.RequiresMaskPrefixHelperAbi);
                    Assert.True(contract.RequiresPredicateOnlyDestinationPolicy);
                    Assert.True(contract.RequiresTailMaskBehaviorPolicy);
                    Assert.True(contract.RejectsVmsifVmsofAlias);
                    Assert.True(contract.RejectsVectorSelectMergeAlias);
                    Assert.Contains("NoVmsifVmsofAlias", contract.RequiredPolicyDecisions);
                    break;
                case CompilerVectorHelperClosedAbiClass.ZeroExtend:
                    Assert.Equal("VectorZeroExtendPublication", contract.ExtensionName);
                    Assert.True(contract.IsZeroExtend);
                    Assert.True(contract.RequiresVectorSourceWidthHelperAbi);
                    Assert.True(contract.RequiresUnsignedExtensionPolicy);
                    Assert.True(contract.RejectsVsextAlias);
                    Assert.True(contract.RejectsWidenNarrowConvertAlias);
                    Assert.Contains("VectorSourceWidthHelperAbi", contract.RequiredPolicyDecisions);
                    break;
                case CompilerVectorHelperClosedAbiClass.PrefixScan:
                    Assert.Equal("VectorScanPrefixPublication", contract.ExtensionName);
                    Assert.True(contract.IsPrefixScan);
                    Assert.True(contract.RequiresPrefixScanHelperAbi);
                    Assert.True(contract.RequiresInclusiveExclusivePolicy);
                    Assert.True(contract.RequiresActiveVlTailBehaviorPolicy);
                    Assert.True(contract.RequiresReplayStablePrefixPublication);
                    Assert.True(contract.RejectsScanMinMaxAlias);
                    Assert.Contains("PrefixScanHelperAbi", contract.RequiredPolicyDecisions);
                    break;
                case CompilerVectorHelperClosedAbiClass.SaturatingArithmetic:
                    Assert.Equal("VectorSaturatingAddPolicy", contract.ExtensionName);
                    Assert.True(contract.IsSaturatingArithmetic);
                    Assert.True(contract.RequiresSaturatingAddHelperAbi);
                    Assert.True(contract.RequiresSignednessWidthClampPolicy);
                    Assert.True(contract.RejectsSaturatingSubMulShiftAlias);
                    Assert.True(contract.RejectsAverageClipAlias);
                    Assert.Contains("SaturatingAddHelperAbi", contract.RequiredPolicyDecisions);
                    break;
                case CompilerVectorHelperClosedAbiClass.MovementPermutation:
                    Assert.Equal("VectorMovementPermutation", contract.ExtensionName);
                    Assert.True(contract.IsMovementPermutation);
                    Assert.True(contract.RequiresMovementPermutationHelperAbi);
                    Assert.True(contract.RequiresPayloadCanonicalization);
                    Assert.True(contract.RequiresFixedLaneShapePolicy);
                    Assert.True(contract.RejectsStructureMovementAlias);
                    Assert.True(contract.RejectsDescriptorShapeFallback);
                    Assert.Contains("MovementPermutationHelperAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("PayloadCanonicalization", contract.RequiredPolicyDecisions);
                    break;
                case CompilerVectorHelperClosedAbiClass.DotProductWide:
                    Assert.Equal("VectorDotProductWideScalarFootprint", contract.ExtensionName);
                    Assert.True(contract.IsDotProductWide);
                    Assert.True(contract.RequiresDotTileHelperAbi);
                    Assert.True(contract.RequiresAccumulatorResultFootprintAbi);
                    Assert.True(contract.RequiresDeterministicOrderingReplayPolicy);
                    Assert.True(contract.RejectsBlockscaleAccumI16I32Alias);
                    Assert.True(contract.NoLane6DescriptorFallback);
                    Assert.Contains("DotTileHelperAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoBlockscaleAccumI16I32Alias", contract.RequiredPolicyDecisions);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(contract.AbiClass), contract.AbiClass, "Unsupported vector helper-closed ABI class.");
            }

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                contract.RequireCompilerHelperAuthority);
            Assert.Contains($"{mnemonic} typed compiler helper emission is blocked", exception.Message, StringComparison.Ordinal);

            string enumCandidate = mnemonic.Replace(".", "_", StringComparison.Ordinal);
            Assert.Contains(contractMetadataFragment, compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"InstructionsEnum.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"IsaOpcodeValues.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"OpcodeValues.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"Compile{facadeHelperFragment}", compilerSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"Emit{facadeHelperFragment}", compilerSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(publicCompilerMethods, methodName =>
                methodName.Contains(facadeHelperFragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void CurrentCompilerSources_ScopeVectorVlmBlockedNoEmissionAuditContractsWithoutTypedHelpers()
    {
        Assert.Equal(
            CompilerVectorVlmBlockedRows.Select(static row => row.Mnemonic).Order(StringComparer.Ordinal),
            CompilerVectorVlmBlockedAbiContract.AllVlmBlockedRows.Select(static row => row.Mnemonic).Order(StringComparer.Ordinal));

        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();
        string[] publicCompilerMethods =
        [
            .. typeof(IAppAsmFacade).GetMethods().Select(static method => method.Name),
            .. typeof(AppAsmFacade).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name),
            .. typeof(IPlatformAsmFacade).GetMethods().Select(static method => method.Name),
            .. typeof(PlatformAsmFacade).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name),
            .. typeof(HybridCpuThreadCompilerContext).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
        ];

        foreach ((string mnemonic, string enumCandidate, string facadeHelperFragment) in CompilerVectorVlmBlockedRows)
        {
            CompilerVectorVlmBlockedAbiContract contract = Assert.Single(
                CompilerVectorVlmBlockedAbiContract.AllVlmBlockedRows,
                row => row.Mnemonic == mnemonic);

            Assert.False(contract.RuntimeExecutable);
            Assert.False(contract.HasRuntimeOpcodeAllocation);
            Assert.False(contract.HasRuntimeConformanceEvidence);
            Assert.False(contract.HasVectorLegalityMatrixEvidence);
            Assert.False(contract.HasRuntimeMaterializerEvidence);
            Assert.False(contract.RawVectorTransportAllowedForThisContour);
            Assert.False(contract.RawVectorTransportIsHelperAuthority);
            Assert.False(contract.CompilerEmissionAllowed);
            Assert.False(contract.CompilerHelperAllowed);
            Assert.False(contract.TypedFacadeAllowed);
            Assert.False(contract.TypedHelperAllowed);
            Assert.True(contract.RequiresVectorHelperAbi);
            Assert.True(contract.RequiresVlmLegalityEvidence);
            Assert.True(contract.RequiresRuntimeMaterializerEvidence);
            Assert.True(contract.RequiresMaskTailPolicy);
            Assert.True(contract.RequiresResultFootprintAbi);
            Assert.True(contract.RequiresRetireReplayGoldenConformance);
            Assert.True(contract.RuntimeOwnedLegalityIsFinal);
            Assert.True(contract.NoRawVectorTransportPromotion);
            Assert.True(contract.NoTypedFacadeHelperEmission);
            Assert.True(contract.NoHiddenScalarLowering);
            Assert.True(contract.NoHiddenLane6Lowering);
            Assert.True(contract.NoLane7Fallback);
            Assert.True(contract.NoExternalBackendFallback);
            Assert.False(string.IsNullOrWhiteSpace(contract.OperandShape));
            Assert.False(string.IsNullOrWhiteSpace(contract.DataSemantics));
            Assert.False(string.IsNullOrWhiteSpace(contract.ResultSemantics));
            Assert.Contains("VlmLegalityEvidence", contract.RequiredPolicyDecisions);
            Assert.Contains("RuntimeMaterializerEvidence", contract.RequiredPolicyDecisions);
            Assert.Contains("NoRawVectorTransportPromotion", contract.RequiredPolicyDecisions);
            Assert.Contains("NoTypedFacadeHelperEmission", contract.RequiredPolicyDecisions);

            InstructionSupportStatus status = InstructionSupportStatusCatalog.GetStatus(mnemonic);
            Assert.Equal(IsaInstructionStatus.Reserved, status.Status);
            Assert.Equal(RuntimeInstructionEvidence.None, status.RuntimeEvidence);
            Assert.False(status.IsExecutableClaim, mnemonic);
            Assert.False(status.HasNumericOpcode, mnemonic);
            Assert.False(status.HasRuntimeOpcodeMetadata, mnemonic);
            Assert.False(status.HasCanonicalDecoderAcceptance, mnemonic);
            Assert.False(status.HasRegistryFactory, mnemonic);
            Assert.False(status.HasExecutionSemantics, mnemonic);
            Assert.DoesNotContain(Enum.GetNames<InstructionsEnum>(), name => name == enumCandidate);
            Assert.DoesNotContain(
                VectorLegalityMatrix.Rows,
                row => row.FamilyName.Contains(mnemonic, StringComparison.Ordinal));

            switch (contract.AbiClass)
            {
                case CompilerVectorVlmBlockedAbiClass.PredicateSelectMerge:
                    Assert.Equal("VectorPredicateSelectMergeVlmBlocked", contract.ExtensionName);
                    Assert.Equal("VectorContourFailClosed", contract.EvidenceBoundary);
                    Assert.True(contract.IsPredicateSelectMerge);
                    Assert.True(contract.RequiresPredicatePolarityPolicy);
                    Assert.True(contract.RequiresSelectMergeResultAbi);
                    Assert.True(contract.RequiresPredicateSidebandAbi);
                    Assert.True(contract.RejectsCzeroAliasPromotion);
                    Assert.True(contract.RejectsMaskPrefixAliasPromotion);
                    Assert.Contains("PredicatePolarityPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoCzeroAliasPromotion", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoMaskPrefixAliasPromotion", contract.RequiredPolicyDecisions);
                    break;
                case CompilerVectorVlmBlockedAbiClass.PredicateScalarSummary:
                    Assert.Equal("VectorPredicateScalarSummaryVlmBlocked", contract.ExtensionName);
                    Assert.Equal("VectorScalarResultContourFailClosed", contract.EvidenceBoundary);
                    Assert.True(contract.IsPredicateScalarSummary);
                    Assert.True(contract.RequiresScalarPredicateSummaryFootprint);
                    Assert.True(contract.RequiresNoActiveElementSentinelPolicy);
                    Assert.True(contract.RequiresActiveVlTailSemantics);
                    Assert.True(contract.RequiresRetireOwnedScalarPublication);
                    Assert.True(contract.RejectsScalarReductionAliasPromotion);
                    Assert.Contains("ScalarPredicateSummaryFootprint", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoActiveElementSentinelPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("RetireOwnedScalarPublication", contract.RequiredPolicyDecisions);
                    break;
                case CompilerVectorVlmBlockedAbiClass.PredicateMaskPrefix:
                    Assert.Equal("VectorPredicateMaskPrefixVlmBlocked", contract.ExtensionName);
                    Assert.Equal("VectorPredicateOnlyContourFailClosed", contract.EvidenceBoundary);
                    Assert.True(contract.IsPredicateMaskPrefix);
                    Assert.True(contract.RequiresPredicateOnlyDestinationPolicy);
                    Assert.True(contract.RequiresIncludingFirstOnlyFirstPolicy);
                    Assert.True(contract.RequiresStagedMaskPublication);
                    Assert.True(contract.RequiresRollbackEvidence);
                    Assert.True(contract.RejectsVmsbfAliasPromotion);
                    Assert.True(contract.RejectsSelectMergeAliasPromotion);
                    Assert.Contains("PredicateOnlyDestinationPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("IncludingFirstOnlyFirstPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoVmsbfAliasPromotion", contract.RequiredPolicyDecisions);
                    break;
                case CompilerVectorVlmBlockedAbiClass.SignedExtend:
                    Assert.Equal("VectorSignedExtendVlmBlocked", contract.ExtensionName);
                    Assert.Equal("VectorSignedExtendVlmRuntimeBlocked", contract.EvidenceBoundary);
                    Assert.True(contract.IsSignedExtend);
                    Assert.True(contract.RequiresVectorSourceWidthAbi);
                    Assert.True(contract.RequiresSignedExtensionPolicy);
                    Assert.True(contract.RequiresSignednessSeparationPolicy);
                    Assert.True(contract.RejectsVzextAliasPromotion);
                    Assert.True(contract.RejectsWidenNarrowConvertAliasPromotion);
                    Assert.Contains("VectorSourceWidthAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("SignedExtensionPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoVzextAliasPromotion", contract.RequiredPolicyDecisions);
                    break;
                case CompilerVectorVlmBlockedAbiClass.FixedPointSaturation:
                    bool isSaturatingShift = mnemonic is "VSLL.SAT" or "VSRL.SAT" or "VSRA.SAT";
                    bool mayRemainReserved = mnemonic is "VSRL.SAT" or "VSRA.SAT";
                    Assert.Equal("VectorFixedPointSaturationVlmBlocked", contract.ExtensionName);
                    Assert.Equal("VectorFixedPointSaturatingFailClosed", contract.EvidenceBoundary);
                    Assert.True(contract.IsFixedPointSaturation);
                    Assert.True(contract.RequiresSaturatingPolicyAbi);
                    Assert.True(contract.RequiresSignednessWidthClampPolicy);
                    Assert.True(contract.RequiresElementWidthLmulVlAbi);
                    Assert.True(contract.RequiresSignednessAbi);
                    Assert.True(contract.RequiresOverflowPolicyAbi);
                    Assert.True(contract.RequiresVlmMaterializationPolicy);
                    Assert.True(contract.RequiresStagedPublicationRetirePolicy);
                    Assert.True(contract.RequiresReplayRollbackGoldenEvidence);
                    Assert.Equal(isSaturatingShift, contract.RequiresShiftOperandAbi);
                    Assert.Equal(isSaturatingShift, contract.RequiresSaturatingShiftPolicyAbi);
                    Assert.Equal(isSaturatingShift, contract.RequiresSaturatingShiftMeaningDecision);
                    Assert.Equal(mayRemainReserved, contract.MayRemainReservedIfNonMeaningful);
                    Assert.True(contract.SeparateFromClosedVaddSat);
                    Assert.True(contract.NoVaddSatFallback);
                    Assert.True(contract.NoBaseVectorArithmeticFallback);
                    Assert.True(contract.NoBaseVectorShiftFallback);
                    Assert.True(contract.NoScalarHelperFallback);
                    Assert.True(contract.NoLane6StreamFallback);
                    Assert.True(contract.NoLane7AcceleratorFallback);
                    Assert.True(contract.NoVmxSpecificPathFallback);
                    Assert.True(contract.NoExecutableRowAliasPromotion);
                    Assert.True(contract.RejectsSaturatingAddAliasPromotion);
                    Assert.True(contract.RejectsClipAverageAliasPromotion);
                    Assert.True(contract.RejectsAverageClipAliasPromotion);
                    Assert.True(contract.RejectsBaseArithmeticAliasPromotion);
                    Assert.Equal(isSaturatingShift, contract.RejectsBaseShiftAliasPromotion);
                    Assert.True(contract.RejectsBaseArithmeticOrShiftAliasPromotion);
                    Assert.Contains("SaturatingPolicyAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("SignednessWidthClampPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("OverflowPolicyAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("VlmMaterializationPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("StagedPublicationRetirePolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("ReplayRollbackGoldenEvidence", contract.RequiredPolicyDecisions);
                    Assert.Contains("SeparateFromClosedVaddSat", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoVaddSatFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoBaseVectorArithmeticFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoBaseVectorShiftFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoScalarHelperFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoLane6StreamFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoLane7AcceleratorFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoVmxSpecificPathFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoExecutableRowAliasPromotion", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoSaturatingAddAliasPromotion", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoAverageClipAliasPromotion", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoBaseArithmeticOrShiftAliasPromotion", contract.RequiredPolicyDecisions);
                    if (isSaturatingShift)
                    {
                        Assert.Contains("SaturatingShiftPolicyAbi", contract.RequiredPolicyDecisions);
                    }
                    break;
                case CompilerVectorVlmBlockedAbiClass.FixedPointAverageClip:
                    bool isAverage = mnemonic is "VAVG" or "VAVG.R";
                    bool isRoundedAverage = mnemonic is "VAVG.R";
                    bool isClip = mnemonic is "VCLIP";
                    Assert.Equal("VectorFixedPointAverageClipVlmBlocked", contract.ExtensionName);
                    Assert.Equal("VectorFixedPointSaturatingFailClosed", contract.EvidenceBoundary);
                    Assert.True(contract.IsFixedPointAverageClip);
                    Assert.Equal(isAverage, contract.IsFixedPointAverage);
                    Assert.Equal(isRoundedAverage, contract.IsRoundedFixedPointAverage);
                    Assert.Equal(isClip, contract.IsFixedPointClip);
                    Assert.True(contract.RequiresElementWidthLmulVlAbi);
                    Assert.True(contract.RequiresSignednessAbi);
                    Assert.True(contract.RequiresRoundingTruncationPolicyAbi);
                    Assert.True(contract.RequiresOverflowPolicyAbi);
                    Assert.True(contract.RequiresVlmMaterializationPolicy);
                    Assert.True(contract.RequiresStagedPublicationRetirePolicy);
                    Assert.True(contract.RequiresReplayRollbackGoldenEvidence);
                    Assert.Equal(isAverage, contract.RequiresAveragePolicyAbi);
                    Assert.Equal(isRoundedAverage, contract.RequiresRoundingPolicyAbi);
                    Assert.Equal(isClip, contract.RequiresClipBoundsAbi);
                    Assert.Equal(isClip, contract.RequiresNarrowingPolicyAbi);
                    Assert.Equal(isClip, contract.RequiresResultWidthPolicyAbi);
                    Assert.True(contract.NoVaddSatFallback);
                    Assert.True(contract.NoFixedPointSaturationFallback);
                    Assert.True(contract.NoBaseVectorArithmeticFallback);
                    Assert.True(contract.NoBaseVectorShiftFallback);
                    Assert.True(contract.NoNarrowWidenConvertFallback);
                    Assert.True(contract.NoScalarHelperFallback);
                    Assert.True(contract.NoLane6StreamFallback);
                    Assert.True(contract.NoLane7AcceleratorFallback);
                    Assert.True(contract.NoVmxSpecificPathFallback);
                    Assert.True(contract.NoExecutableRowAliasPromotion);
                    Assert.True(contract.RejectsAverageClipAliasPromotion);
                    Assert.True(contract.RejectsBaseArithmeticAliasPromotion);
                    Assert.True(contract.RejectsBaseShiftAliasPromotion);
                    Assert.True(contract.RejectsBaseArithmeticOrShiftAliasPromotion);
                    Assert.True(contract.RejectsFixedPointSaturationAliasPromotion);
                    Assert.Contains("RoundingTruncationPolicyAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("VlmMaterializationPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("StagedPublicationRetirePolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("ReplayRollbackGoldenEvidence", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoVaddSatFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoFixedPointSaturationFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoBaseVectorArithmeticFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoBaseVectorShiftFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoNarrowWidenConvertFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoScalarHelperFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoLane6StreamFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoLane7AcceleratorFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoVmxSpecificPathFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoExecutableRowAliasPromotion", contract.RequiredPolicyDecisions);
                    if (isAverage)
                    {
                        Assert.Contains("AveragePolicyAbi", contract.RequiredPolicyDecisions);
                    }

                    if (isRoundedAverage)
                    {
                        Assert.Contains("RoundingPolicyAbi", contract.RequiredPolicyDecisions);
                    }

                    if (isClip)
                    {
                        Assert.Contains("ClipBoundsAbi", contract.RequiredPolicyDecisions);
                        Assert.Contains("NarrowingPolicyAbi", contract.RequiredPolicyDecisions);
                        Assert.Contains("ResultWidthPolicyAbi", contract.RequiredPolicyDecisions);
                    }

                    break;
                case CompilerVectorVlmBlockedAbiClass.DotTileVariant:
                    bool isBlockscale = mnemonic == "VDOT.BLOCKSCALE";
                    bool isAccumulator = mnemonic == "VDOT.ACCUM";
                    bool isWideInteger = mnemonic is "VDOT.WIDE.I16" or "VDOT.WIDE.I32";
                    Assert.Equal("VectorDotTileVariantVlmBlocked", contract.ExtensionName);
                    Assert.Equal("VectorDotMatrixDeferredNoExecution", contract.EvidenceBoundary);
                    Assert.True(contract.IsDotTileVariant);
                    Assert.Equal(isBlockscale, contract.IsDotBlockscaleVariant);
                    Assert.Equal(isAccumulator, contract.IsDotAccumulatorVariant);
                    Assert.Equal(isWideInteger, contract.IsDotWideIntegerVariant);
                    Assert.True(contract.RequiresDotVariantAbi);
                    Assert.True(contract.RequiresDotTileHelperAbi);
                    Assert.True(contract.RequiresAccumulatorFootprintAbi);
                    Assert.True(contract.RequiresAccumulatorPrecisionPolicy);
                    Assert.True(contract.RequiresAccumulatorPrecisionAbi);
                    Assert.True(contract.RequiresAccumulatorResultFootprintAbi);
                    Assert.Equal(isBlockscale, contract.RequiresScaleMetadataAbi);
                    Assert.Equal(!isWideInteger, contract.RequiresSeparateResultSurfaceAbi);
                    Assert.Equal(isWideInteger, contract.RequiresWiderIntegerContourAbi);
                    Assert.True(contract.RequiresDeterministicOrderingReplayPolicy);
                    Assert.True(contract.RequiresVlmMaterializationPolicy);
                    Assert.True(contract.RequiresStagedPublicationRetirePolicy);
                    Assert.True(contract.RequiresReplayRollbackGoldenEvidence);
                    Assert.True(contract.SeparateFromScopedVdotWide);
                    Assert.True(contract.NoScopedVdotWideFallback);
                    Assert.True(contract.NoNameOnlyVdotWideExtension);
                    Assert.True(contract.NoBaseDotProductFallback);
                    Assert.True(contract.NoWideningFmaFallback);
                    Assert.True(contract.NoLane6DescriptorFallback);
                    Assert.True(contract.NoMatrixTileFallback);
                    Assert.True(contract.NoScalarHelperFallback);
                    Assert.True(contract.NoLane6StreamFallback);
                    Assert.True(contract.NoLane7AcceleratorFallback);
                    Assert.True(contract.NoVmxSpecificPathFallback);
                    Assert.True(contract.NoExecutableRowAliasPromotion);
                    Assert.True(contract.NoHostOwnedEvidencePublication);
                    Assert.True(contract.RejectsDotProductAliasPromotion);
                    Assert.Contains("DotVariantAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("DotTileHelperAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("AccumulatorPrecisionAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("AccumulatorResultFootprintAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("DeterministicOrderingReplayPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("VlmMaterializationPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("StagedPublicationRetirePolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("ReplayRollbackGoldenEvidence", contract.RequiredPolicyDecisions);
                    Assert.Contains("SeparateFromScopedVdotWide", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoScopedVdotWideFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoNameOnlyVdotWideExtension", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoBaseDotProductFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoWideningFmaFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoLane6DescriptorFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoMatrixTileFallback", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoHostOwnedEvidencePublication", contract.RequiredPolicyDecisions);
                    if (isBlockscale)
                    {
                        Assert.Contains("ScaleMetadataAbi", contract.RequiredPolicyDecisions);
                    }

                    if (!isWideInteger)
                    {
                        Assert.Contains("SeparateResultSurfaceAbi", contract.RequiredPolicyDecisions);
                    }

                    if (isWideInteger)
                    {
                        Assert.Contains("WiderIntegerContourAbi", contract.RequiredPolicyDecisions);
                    }

                    break;
                case CompilerVectorVlmBlockedAbiClass.WideningArithmetic:
                    Assert.Equal("VectorWideningArithmeticVlmBlocked", contract.ExtensionName);
                    Assert.Equal("VectorWidenNarrowConvertFailClosed", contract.EvidenceBoundary);
                    Assert.True(contract.IsWideningArithmetic);
                    Assert.False(contract.IsWideningMultiplyAccumulate);
                    Assert.True(contract.RequiresSourceDestinationWidthSideband);
                    Assert.True(contract.RequiresElementWidthLmulVlAbi);
                    Assert.True(contract.RequiresSignednessAbi);
                    Assert.True(contract.RequiresWideningOverflowPolicyAbi);
                    Assert.False(contract.RequiresAccumulatorFootprintAbi);
                    Assert.False(contract.RequiresAccumulatorPrecisionPolicy);
                    Assert.True(contract.SeparateFromBaseVectorArithmetic);
                    Assert.True(contract.RejectsVzextVsextAliasPromotion);
                    Assert.True(contract.RejectsBaseArithmeticAliasPromotion);
                    Assert.False(contract.RejectsDotProductAliasPromotion);
                    Assert.Contains("SourceDestinationWidthSideband", contract.RequiredPolicyDecisions);
                    Assert.Contains("WideningOverflowPolicyAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoBaseArithmeticAliasPromotion", contract.RequiredPolicyDecisions);
                    break;
                case CompilerVectorVlmBlockedAbiClass.WideningMultiplyAccumulate:
                    Assert.Equal("VectorWideningMultiplyAccumulateVlmBlocked", contract.ExtensionName);
                    Assert.Equal("VectorWidenNarrowConvertFailClosed", contract.EvidenceBoundary);
                    Assert.True(contract.IsWideningArithmetic);
                    Assert.True(contract.IsWideningMultiplyAccumulate);
                    Assert.True(contract.RequiresSourceDestinationWidthSideband);
                    Assert.True(contract.RequiresElementWidthLmulVlAbi);
                    Assert.True(contract.RequiresSignednessAbi);
                    Assert.True(contract.RequiresWideningOverflowPolicyAbi);
                    Assert.True(contract.RequiresAccumulatorFootprintAbi);
                    Assert.True(contract.RequiresAccumulatorPrecisionPolicy);
                    Assert.True(contract.SeparateFromBaseVectorArithmetic);
                    Assert.True(contract.RejectsVzextVsextAliasPromotion);
                    Assert.True(contract.RejectsBaseArithmeticAliasPromotion);
                    Assert.True(contract.RejectsDotProductAliasPromotion);
                    Assert.Contains("AccumulatorFootprintAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("AccumulatorPrecisionPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoDotProductAliasPromotion", contract.RequiredPolicyDecisions);
                    break;
                case CompilerVectorVlmBlockedAbiClass.NarrowingShift:
                    Assert.Equal("VectorNarrowingShiftVlmBlocked", contract.ExtensionName);
                    Assert.Equal("VectorWidenNarrowConvertFailClosed", contract.EvidenceBoundary);
                    Assert.True(contract.IsNarrowingShift);
                    Assert.True(contract.RequiresSourceDestinationWidthSideband);
                    Assert.True(contract.RequiresElementWidthLmulVlAbi);
                    Assert.True(contract.RequiresNarrowingPolicyAbi);
                    Assert.True(contract.RequiresShiftOperandAbi);
                    Assert.True(contract.RequiresRoundingSaturationTrapPolicy);
                    Assert.True(contract.RequiresTruncationPublicationPolicy);
                    Assert.True(contract.RejectsClipAverageAliasPromotion);
                    Assert.True(contract.RejectsBaseShiftAliasPromotion);
                    Assert.Contains("NarrowingPolicyAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("ShiftOperandAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("TruncationPublicationPolicy", contract.RequiredPolicyDecisions);
                    break;
                case CompilerVectorVlmBlockedAbiClass.Conversion:
                    Assert.Equal("VectorConversionVlmBlocked", contract.ExtensionName);
                    Assert.Equal("VectorWidenNarrowConvertFailClosed", contract.EvidenceBoundary);
                    Assert.True(contract.IsConversion);
                    Assert.True(contract.RequiresConversionPolicyAbi);
                    Assert.True(contract.RequiresConversionTypeDomainAbi);
                    Assert.True(contract.RequiresElementWidthLmulVlAbi);
                    Assert.True(contract.RequiresRoundingSaturationTrapPolicy);
                    Assert.True(contract.RequiresNanPolicyAbi);
                    Assert.True(contract.RequiresConversionResultFootprintAbi);
                    Assert.True(contract.RejectsVzextVsextAliasPromotion);
                    Assert.True(contract.RejectsWidenNarrowArithmeticAliasPromotion);
                    Assert.True(contract.RejectsScalarConversionAliasPromotion);
                    Assert.Contains("ConversionPolicyAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("ConversionTypeDomainAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("NanPolicyAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("ConversionResultFootprintAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoScalarConversionAliasPromotion", contract.RequiredPolicyDecisions);
                    break;
                case CompilerVectorVlmBlockedAbiClass.PrefixScanMinMax:
                    Assert.Equal("VectorPrefixScanMinMaxVlmBlocked", contract.ExtensionName);
                    Assert.Equal("VectorScanContourFailClosed", contract.EvidenceBoundary);
                    Assert.True(contract.IsPrefixScanMinMax);
                    Assert.True(contract.RequiresPrefixScanPolicyAbi);
                    Assert.True(contract.RequiresPrefixMinMaxOrderingPolicy);
                    Assert.True(contract.RequiresInclusiveExclusivePolicy);
                    Assert.True(contract.RequiresActiveVlTailBehaviorPolicy);
                    Assert.True(contract.RequiresElementTypeSideband);
                    Assert.True(contract.RequiresSignednessAbi);
                    Assert.True(contract.RequiresReplayDeterministicPrefixPublication);
                    Assert.True(contract.RejectsVscanSumAliasPromotion);
                    Assert.True(contract.RejectsReductionAliasPromotion);
                    Assert.True(contract.RejectsSegmentMovementAliasPromotion);
                    Assert.Contains("PrefixScanPolicyAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("PrefixMinMaxOrderingPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("ActiveVlTailBehaviorPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoVscanSumAliasPromotion", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoSegmentMovementAliasPromotion", contract.RequiredPolicyDecisions);
                    break;
                case CompilerVectorVlmBlockedAbiClass.StructureMovement:
                    Assert.Equal("VectorStructureMovementVlmBlocked", contract.ExtensionName);
                    Assert.Equal("VectorStructureMovementFailClosed", contract.EvidenceBoundary);
                    Assert.True(contract.IsStructureMovement);
                    Assert.True(contract.RequiresStructureShapeAbi);
                    Assert.True(contract.RequiresShapeOrderingPolicy);
                    Assert.True(contract.RequiresPayloadCanonicalization);
                    Assert.True(contract.RequiresElementOrderPolicy);
                    Assert.True(contract.RequiresActiveVlTailBehaviorPolicy);
                    Assert.True(contract.RequiresStagedVectorPublication);
                    Assert.True(contract.RejectsMovementPermutationAliasPromotion);
                    Assert.True(contract.RejectsSegmentMemoryAliasPromotion);
                    Assert.True(contract.RejectsHiddenStreamEngineFallback);
                    Assert.Contains("StructureShapeAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("ShapeOrderingPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("PayloadCanonicalization", contract.RequiredPolicyDecisions);
                    Assert.Contains("StagedVectorPublication", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoMovementPermutationAliasPromotion", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoHiddenStreamEngineFallback", contract.RequiredPolicyDecisions);
                    break;
                case CompilerVectorVlmBlockedAbiClass.SegmentMemory:
                    Assert.Equal("VectorSegmentMemoryVlmBlocked", contract.ExtensionName);
                    Assert.Equal("VectorSegmentMemoryFailClosed", contract.EvidenceBoundary);
                    Assert.True(contract.IsSegmentMemory);
                    Assert.NotEqual(contract.IsSegmentLoad, contract.IsSegmentStore);
                    Assert.Contains(contract.SegmentCount, new[] { 2, 4, 8 });
                    Assert.True(contract.RequiresSegmentMemoryShapeAbi);
                    Assert.True(contract.RequiresSegmentCountAbi);
                    Assert.True(contract.RequiresFaultReplayPolicy);
                    Assert.True(contract.RequiresByteOrderingPolicy);
                    Assert.True(contract.RequiresAlignmentFaultPolicy);
                    Assert.True(contract.RequiresSegmentOrderingPolicy);
                    Assert.True(contract.RejectsBaseMemoryOpcodeDuplication);
                    Assert.True(contract.RejectsStructureMovementAliasPromotion);
                    Assert.True(contract.RejectsIndexedOr2DMemoryAliasPromotion);
                    Assert.Equal(contract.IsSegmentLoad, contract.RequiresRetireStagedPublication);
                    Assert.Equal(contract.IsSegmentStore, contract.RequiresRetireStagedCommit);
                    Assert.Contains("SegmentMemoryShapeAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("SegmentCountAbi", contract.RequiredPolicyDecisions);
                    Assert.Contains("FaultReplayPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("ByteOrderingPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("AlignmentFaultPolicy", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoBaseMemoryOpcodeDuplication", contract.RequiredPolicyDecisions);
                    Assert.Contains("NoIndexedOr2DMemoryAliasPromotion", contract.RequiredPolicyDecisions);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(contract.AbiClass), contract.AbiClass, "Unsupported vector VLM-blocked ABI class.");
            }

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                contract.RequireCompilerHelperAuthority);
            Assert.Contains($"{mnemonic} typed compiler helper emission is blocked", exception.Message, StringComparison.Ordinal);

            Assert.Contains(mnemonic, compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"InstructionsEnum.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"IsaOpcodeValues.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"OpcodeValues.{enumCandidate}", compilerSource, StringComparison.Ordinal);
            Assert.DoesNotContain($"Compile{facadeHelperFragment}", compilerSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"Emit{facadeHelperFragment}", compilerSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(publicCompilerMethods, methodName =>
                methodName.Contains(facadeHelperFragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void CurrentCompilerSources_OpenMatrixTilePositiveEmissionFromPhase13Handoff()
    {
        Assert.Empty(CompilerMatrixTileOptionalDisabledRows);
        Assert.Equal(
            CompilerMatrixTilePositiveEmissionRows.Select(static row => row.Mnemonic).Order(StringComparer.Ordinal),
            CompilerMatrixTilePositiveEmissionAbiContract.Rows.Select(static row => row.Mnemonic).Order(StringComparer.Ordinal));

        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();
        string compilerEmissionSurfaceSource = CompilerSourceScanner.ReadCompilerEmissionSurfaceSource();
        string[] publicCompilerMethods =
        [
            .. typeof(IAppAsmFacade).GetMethods().Select(static method => method.Name),
            .. typeof(AppAsmFacade).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name),
            .. typeof(IPlatformAsmFacade).GetMethods().Select(static method => method.Name),
            .. typeof(PlatformAsmFacade).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name),
            .. typeof(HybridCpuThreadCompilerContext).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
        ];

        Assert.True(CompilerMatrixTilePositiveEmissionAbiContract.HasCurrentCompilerImplementation);
        Assert.True(CompilerMatrixTilePositiveEmissionAbiContract.HasCurrentCompilerHelper);
        Assert.True(CompilerMatrixTilePositiveEmissionAbiContract.HasCurrentCompilerEmission);
        Assert.True(CompilerMatrixTilePositiveEmissionAbiContract.UsesPhase13RuntimeHandoff);
        Assert.True(CompilerMatrixTilePositiveEmissionAbiContract.RuntimeOwnedLegalityIsFinal);
        Assert.False(CompilerMatrixTilePositiveEmissionAbiContract.AllowsCompilerToOverrideRuntimeLegality);
        Assert.False(CompilerMatrixTilePositiveEmissionAbiContract.UsesOldOptionalDisabledMetadataAsAuthority);
        Assert.False(CompilerMatrixTilePositiveEmissionAbiContract.UsesFallbackPath);
        Assert.False(CompilerMatrixTilePositiveEmissionAbiContract.UsesAliasPromotion);
        Assert.True(CompilerMatrixTilePositiveEmissionAbiContract.EmitsDirectMatrixTileOpcodes);

        Assert.Contains(
            CompilerMatrixTilePositiveEmissionAbiContract.CompilerPositiveEmissionDecision,
            compilerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            CompilerMatrixTilePositiveEmissionAbiContract.RuntimeHandoffAuthorityDecision,
            compilerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            CompilerMatrixTilePositiveEmissionAbiContract.LegacyOptionalDisabledBoundaryDecision,
            compilerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            CompilerMatrixTilePositiveEmissionAbiContract.NoFallbackDecision,
            compilerSource,
            StringComparison.Ordinal);

        foreach ((string mnemonic, string enumCandidate, string contractMetadataFragment, string facadeHelperFragment) in CompilerMatrixTilePositiveEmissionRows)
        {
            CompilerMatrixTilePositiveEmissionRow row = Assert.Single(
                CompilerMatrixTilePositiveEmissionAbiContract.Rows,
                row => row.Mnemonic == mnemonic);

            Assert.Equal(Enum.Parse<InstructionsEnum>(enumCandidate), (InstructionsEnum)(uint)row.Opcode);
            Assert.Equal((ushort)row.Opcode, row.NumericOpcode);
            Assert.Equal(contractMetadataFragment, CompilerMatrixTilePositiveEmissionAbiContract.CompilerPositiveEmissionDecision);
            Assert.True(row.UsesPhase13RuntimeHandoff);
            Assert.True(row.RuntimeOwnedLegalityIsFinal);
            Assert.True(row.EmitsDirectMatrixTileOpcode);
            Assert.False(row.UsesFallbackPath);
            Assert.False(row.UsesAliasPromotion);
            Assert.False(string.IsNullOrWhiteSpace(row.RequiredTypedOperandContract));
            CompilerMatrixTilePositiveEmissionAbiContract.RequireRuntimeHandoffAuthority(mnemonic);
            Assert.Equal(
                row.Opcode,
                (HybridCpuOpcode)(uint)MatrixTileCompilerEmissionHandoffPackage.GetRow(mnemonic).Opcode);

            InstructionSupportStatus status = InstructionSupportStatusCatalog.GetStatus(mnemonic);
            Assert.Equal(IsaInstructionStatus.OptionalEnabled, status.Status);
            Assert.Equal(RuntimeInstructionEvidence.ConformanceTested, status.RuntimeEvidence);
            Assert.Equal("XMatrix", status.ExtensionName);
            Assert.True(status.HasNumericOpcode, mnemonic);
            Assert.True(status.HasRuntimeOpcodeMetadata, mnemonic);
            Assert.True(status.HasCanonicalDecoderAcceptance, mnemonic);
            Assert.True(status.HasRegistryFactory, mnemonic);
            Assert.True(status.HasExecutionSemantics, mnemonic);
            Assert.True(status.IsExecutableClaim, mnemonic);
            Assert.Contains(Enum.GetNames<InstructionsEnum>(), name => name == enumCandidate);
            Assert.NotNull(typeof(Processor.CPU_Core.IsaOpcodeValues).GetField(enumCandidate, BindingFlags.Public | BindingFlags.Static));
            Assert.True(VectorLegalityMatrix.TryGetRow(
                Enum.Parse<InstructionsEnum>(enumCandidate),
                out VectorLegalityMatrixRow? vlmRow));
            Assert.Equal("XMatrix", vlmRow.FamilyName);
            Assert.Equal(VectorContourLegalityStatus.DescriptorOnly, vlmRow.DescriptorBacked);
            Assert.DoesNotContain(VectorContourLegalityStatus.Executable, new[]
            {
                vlmRow.OneDimensional,
                vlmRow.IndexedAddressing,
                vlmRow.TwoDimensionalAddressing,
                vlmRow.Masked,
                vlmRow.TailMaskPolicy,
                vlmRow.Reduction,
                vlmRow.DescriptorBacked
            });

            Assert.Contains(contractMetadataFragment, compilerSource, StringComparison.Ordinal);
            Assert.Contains($"HybridCpuOpcode.{enumCandidate}", compilerEmissionSurfaceSource, StringComparison.Ordinal);
            Assert.Contains($"Compile{facadeHelperFragment}", compilerEmissionSurfaceSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"Emit{facadeHelperFragment}", compilerEmissionSurfaceSource, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(facadeHelperFragment, publicCompilerMethods);
            Assert.Contains(row.HelperName, publicCompilerMethods);
        }

        Assert.All(CompilerMatrixTileOptionalDisabledAbiContract.AllOptionalDisabledRows, static contract =>
        {
            Assert.False(contract.CompilerEmissionAllowed);
            Assert.False(contract.CompilerHelperAllowed);
        });
    }

    [Fact]
    public void CurrentCompilerSources_ScopeDeferredScalarAbiContractsWithoutEmissionAuthority()
    {
        Assert.Equal(
            CompilerDeferredScalarRows.Select(static row => row.Mnemonic).Order(StringComparer.Ordinal),
            CompilerDeferredScalarAbiContract.AllDeferredScalarRows.Select(static row => row.Mnemonic).Order(StringComparer.Ordinal));

        string compilerSource = CompilerSourceScanner.ReadAllCompilerSource();

        foreach ((string mnemonic, string facadeHelperFragment) in CompilerDeferredScalarRows)
        {
            CompilerDeferredScalarAbiContract contract = Assert.Single(
                CompilerDeferredScalarAbiContract.AllDeferredScalarRows,
                row => row.Mnemonic == mnemonic);

            Assert.False(contract.CompilerEmissionAllowed);
            Assert.False(contract.HasOpcodeAllocation);
            Assert.False(string.IsNullOrWhiteSpace(contract.OperandShape));
            Assert.False(string.IsNullOrWhiteSpace(contract.DataSemantics));
            Assert.False(string.IsNullOrWhiteSpace(contract.ResultSemantics));

            if (contract.AbiClass == CompilerDeferredScalarAbiClass.ScalarSelectCarrier)
            {
                Assert.True(contract.RequiresFourRegisterCarrierAbi);
                Assert.True(contract.RequiresExternalCarrierAbi);
                Assert.True(contract.RequiresConditionRegisterAbi);
                Assert.True(contract.RequiresSelectResultAbi);
                Assert.True(contract.RequiresNoCzeroAliasPolicy);
                Assert.True(contract.FourSourceCarrierDecisionClosed);
                Assert.False(contract.ApprovedFourSourceCarrier);
                Assert.False(contract.CurrentPackedScalarIrSupportsCarrier);
                Assert.True(contract.RejectCzeroAliasLowering);
                Assert.True(contract.RejectHiddenMultiOpSelectLowering);
                Assert.Contains("FourRegisterCarrierAbi", contract.RequiredPolicyDecisions);
                Assert.Contains("ConditionRegisterTransport", contract.RequiredPolicyDecisions);
                Assert.Contains("SelectResultSemantics", contract.RequiredPolicyDecisions);
                Assert.Contains("NoCzeroAliasLowering", contract.RequiredPolicyDecisions);
            }
            else if (contract.AbiClass == CompilerDeferredScalarAbiClass.ScalarCrcChecksum)
            {
                Assert.True(contract.RequiresPolynomialAbi);
                Assert.True(contract.RequiresReflectionAbi);
                Assert.True(contract.RequiresSeedFinalXorAbi);
                Assert.True(contract.RequiresEndianPolicyAbi);
                Assert.True(contract.RequiresDataWidthAbi);
                Assert.True(contract.RequiresResultSemanticsAbi);
                Assert.Contains("Polynomial", contract.RequiredPolicyDecisions);
                Assert.Contains("ResultWidthAndExtension", contract.RequiredPolicyDecisions);
            }
            else
            {
                Assert.Equal(CompilerDeferredScalarAbiClass.ScalarMultiPrecision, contract.AbiClass);
                Assert.True(contract.RequiresCarryBorrowPublicationAbi);
                Assert.True(contract.RequiresRetireOwnedPublicationAbi);
                Assert.True(contract.NoImplicitFlags);
                Assert.True(contract.RejectHiddenArchitecturalFlags);
                Assert.Contains("RetireOwnedCarryBorrowPublication", contract.RequiredPolicyDecisions);
                Assert.Contains("NoImplicitFlags", contract.RequiredPolicyDecisions);
            }

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                contract.RequireCompilerEmissionAuthority);
            Assert.Contains($"{mnemonic} compiler emission is blocked", exception.Message, StringComparison.Ordinal);

            Assert.DoesNotContain(
                $"InstructionsEnum.{mnemonic}",
                compilerSource,
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                $"IsaOpcodeValues.{mnemonic}",
                compilerSource,
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                $"OpcodeValues.{mnemonic}",
                compilerSource,
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                $"Compile{facadeHelperFragment}",
                compilerSource,
                StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain(
                $"Emit{facadeHelperFragment}",
                compilerSource,
                StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain(
                facadeHelperFragment,
                typeof(IAppAsmFacade).GetMethods().Select(static method => method.Name));

            Assert.DoesNotContain(
                facadeHelperFragment,
                typeof(AppAsmFacade)
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Select(static method => method.Name));
        }
    }

#pragma warning disable CS0618
    private static void EmitScalarWordBinaryTail(
        AppAsmFacade facade,
        InstructionsEnum opcode,
        AsmRegister dest,
        AsmRegister src1,
        AsmRegister src2)
    {
        switch (opcode)
        {
            case InstructionsEnum.DIVW:
                facade.DivideWord(dest, src1, src2);
                break;
            case InstructionsEnum.DIVUW:
                facade.DivideUnsignedWord(dest, src1, src2);
                break;
            case InstructionsEnum.REMW:
                facade.RemainderWord(dest, src1, src2);
                break;
            case InstructionsEnum.REMUW:
                facade.RemainderUnsignedWord(dest, src1, src2);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Unsupported binary scalar tail opcode.");
        }
    }

    private static void EmitScalarWordUnaryTail(
        AppAsmFacade facade,
        InstructionsEnum opcode,
        AsmRegister dest,
        AsmRegister src)
    {
        switch (opcode)
        {
            case InstructionsEnum.SEXT_W:
                facade.SignExtendWord(dest, src);
                break;
            case InstructionsEnum.ZEXT_W:
                facade.ZeroExtendWord(dest, src);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Unsupported unary scalar tail opcode.");
        }
    }

    private static void EmitScalarBitmanipUnary(
        AppAsmFacade facade,
        InstructionsEnum opcode,
        AsmRegister dest,
        AsmRegister src)
    {
        switch (opcode)
        {
            case InstructionsEnum.REV8:
                facade.ReverseByteOrder(dest, src);
                break;
            case InstructionsEnum.BREV8:
                facade.ReverseBitsInEachByte(dest, src);
                break;
            case InstructionsEnum.SEXT_B:
                facade.SignExtendByte(dest, src);
                break;
            case InstructionsEnum.SEXT_H:
                facade.SignExtendHalf(dest, src);
                break;
            case InstructionsEnum.ZEXT_H:
                facade.ZeroExtendHalf(dest, src);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Unsupported scalar bitmanip unary opcode.");
        }
    }

    private static void EmitScalarRegisterBinary(
        AppAsmFacade facade,
        InstructionsEnum opcode,
        AsmRegister dest,
        AsmRegister src1,
        AsmRegister src2)
    {
        switch (opcode)
        {
            case InstructionsEnum.CZERO_EQZ:
                facade.ZeroIfConditionEqualZero(dest, src1, src2);
                break;
            case InstructionsEnum.CZERO_NEZ:
                facade.ZeroIfConditionNotEqualZero(dest, src1, src2);
                break;
            case InstructionsEnum.ROL:
                facade.RotateLeftRegister(dest, src1, src2);
                break;
            case InstructionsEnum.ROR:
                facade.RotateRightRegister(dest, src1, src2);
                break;
            case InstructionsEnum.ANDN:
                facade.AndWithInvertedSecond(dest, src1, src2);
                break;
            case InstructionsEnum.ORN:
                facade.OrWithInvertedSecond(dest, src1, src2);
                break;
            case InstructionsEnum.XNOR:
                facade.ExclusiveNor(dest, src1, src2);
                break;
            case InstructionsEnum.MIN:
                facade.ScalarMinSigned(dest, src1, src2);
                break;
            case InstructionsEnum.MAX:
                facade.ScalarMaxSigned(dest, src1, src2);
                break;
            case InstructionsEnum.MINU:
                facade.ScalarMinUnsigned(dest, src1, src2);
                break;
            case InstructionsEnum.MAXU:
                facade.ScalarMaxUnsigned(dest, src1, src2);
                break;
            case InstructionsEnum.CLMUL:
                facade.BinaryPolynomialProductLow(dest, src1, src2);
                break;
            case InstructionsEnum.CLMULH:
                facade.BinaryPolynomialProductHigh(dest, src1, src2);
                break;
            case InstructionsEnum.CLMULR:
                facade.BinaryPolynomialProductReverse(dest, src1, src2);
                break;
            case InstructionsEnum.SH1ADD:
                facade.ShiftLeftOneAndAdd(dest, src1, src2);
                break;
            case InstructionsEnum.SH2ADD:
                facade.ShiftLeftTwoAndAdd(dest, src1, src2);
                break;
            case InstructionsEnum.SH3ADD:
                facade.ShiftLeftThreeAndAdd(dest, src1, src2);
                break;
            case InstructionsEnum.ADD_UW:
                facade.AddUnsignedWord(dest, src1, src2);
                break;
            case InstructionsEnum.SH1ADD_UW:
                facade.ShiftLeftOneAndAddUnsignedWord(dest, src1, src2);
                break;
            case InstructionsEnum.SH2ADD_UW:
                facade.ShiftLeftTwoAndAddUnsignedWord(dest, src1, src2);
                break;
            case InstructionsEnum.SH3ADD_UW:
                facade.ShiftLeftThreeAndAddUnsignedWord(dest, src1, src2);
                break;
            case InstructionsEnum.BSET:
                facade.SetBitRegister(dest, src1, src2);
                break;
            case InstructionsEnum.BCLR:
                facade.ClearBitRegister(dest, src1, src2);
                break;
            case InstructionsEnum.BINV:
                facade.InvertBitRegister(dest, src1, src2);
                break;
            case InstructionsEnum.BEXT:
                facade.ExtractBitRegister(dest, src1, src2);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Unsupported scalar register-register opcode.");
        }
    }

    private static void EmitScalarBitfieldImmediate(
        AppAsmFacade facade,
        InstructionsEnum opcode,
        AsmRegister dest,
        AsmRegister src,
        int index)
    {
        switch (opcode)
        {
            case InstructionsEnum.BSETI:
                facade.SetBitImmediate(dest, src, index);
                break;
            case InstructionsEnum.BCLRI:
                facade.ClearBitImmediate(dest, src, index);
                break;
            case InstructionsEnum.BINVI:
                facade.InvertBitImmediate(dest, src, index);
                break;
            case InstructionsEnum.BEXTI:
                facade.ExtractBitImmediate(dest, src, index);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Unsupported scalar bitfield-immediate opcode.");
        }
    }

    private static void EmitScalarAddressGenerationImmediate(
        AppAsmFacade facade,
        InstructionsEnum opcode,
        AsmRegister dest,
        AsmRegister src,
        int shift)
    {
        switch (opcode)
        {
            case InstructionsEnum.SLLI_UW:
                facade.ShiftLeftUnsignedWordByImmediate(dest, src, shift);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Unsupported scalar address-generation-immediate opcode.");
        }
    }

    private static void EmitScalarRotateImmediate(
        AppAsmFacade facade,
        InstructionsEnum opcode,
        AsmRegister dest,
        AsmRegister src,
        int shift)
    {
        switch (opcode)
        {
            case InstructionsEnum.ROLI:
                facade.RotateLeftByImmediate(dest, src, shift);
                break;
            case InstructionsEnum.RORI:
                facade.RotateRightByImmediate(dest, src, shift);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Unsupported scalar rotate-immediate opcode.");
        }
    }
#pragma warning restore CS0618

    private static string ResolveCompilerTailHelperName(InstructionsEnum opcode)
    {
        return opcode switch
        {
            InstructionsEnum.DIVW => nameof(AppAsmFacade.DivideWord),
            InstructionsEnum.DIVUW => nameof(AppAsmFacade.DivideUnsignedWord),
            InstructionsEnum.REMW => nameof(AppAsmFacade.RemainderWord),
            InstructionsEnum.REMUW => nameof(AppAsmFacade.RemainderUnsignedWord),
            InstructionsEnum.SEXT_W => nameof(AppAsmFacade.SignExtendWord),
            InstructionsEnum.ZEXT_W => nameof(AppAsmFacade.ZeroExtendWord),
            _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Unsupported compiler tail opcode.")
        };
    }

    private static string ResolveCompilerOptionalHelperName(InstructionsEnum opcode)
    {
        return opcode switch
        {
            InstructionsEnum.CLZ => nameof(AppAsmFacade.CountLeadingZeros),
            InstructionsEnum.CTZ => nameof(AppAsmFacade.CountTrailingZeros),
            InstructionsEnum.CPOP => nameof(AppAsmFacade.CountSetBits),
            InstructionsEnum.CZERO_EQZ => nameof(AppAsmFacade.ZeroIfConditionEqualZero),
            InstructionsEnum.CZERO_NEZ => nameof(AppAsmFacade.ZeroIfConditionNotEqualZero),
            InstructionsEnum.ROL => nameof(AppAsmFacade.RotateLeftRegister),
            InstructionsEnum.ROR => nameof(AppAsmFacade.RotateRightRegister),
            InstructionsEnum.ANDN => nameof(AppAsmFacade.AndWithInvertedSecond),
            InstructionsEnum.ORN => nameof(AppAsmFacade.OrWithInvertedSecond),
            InstructionsEnum.XNOR => nameof(AppAsmFacade.ExclusiveNor),
            InstructionsEnum.MIN => nameof(AppAsmFacade.ScalarMinSigned),
            InstructionsEnum.MAX => nameof(AppAsmFacade.ScalarMaxSigned),
            InstructionsEnum.MINU => nameof(AppAsmFacade.ScalarMinUnsigned),
            InstructionsEnum.MAXU => nameof(AppAsmFacade.ScalarMaxUnsigned),
            InstructionsEnum.CLMUL => nameof(AppAsmFacade.BinaryPolynomialProductLow),
            InstructionsEnum.CLMULH => nameof(AppAsmFacade.BinaryPolynomialProductHigh),
            InstructionsEnum.CLMULR => nameof(AppAsmFacade.BinaryPolynomialProductReverse),
            InstructionsEnum.SH1ADD => nameof(AppAsmFacade.ShiftLeftOneAndAdd),
            InstructionsEnum.SH2ADD => nameof(AppAsmFacade.ShiftLeftTwoAndAdd),
            InstructionsEnum.SH3ADD => nameof(AppAsmFacade.ShiftLeftThreeAndAdd),
            InstructionsEnum.ADD_UW => nameof(AppAsmFacade.AddUnsignedWord),
            InstructionsEnum.SH1ADD_UW => nameof(AppAsmFacade.ShiftLeftOneAndAddUnsignedWord),
            InstructionsEnum.SH2ADD_UW => nameof(AppAsmFacade.ShiftLeftTwoAndAddUnsignedWord),
            InstructionsEnum.SH3ADD_UW => nameof(AppAsmFacade.ShiftLeftThreeAndAddUnsignedWord),
            InstructionsEnum.SLLI_UW => nameof(AppAsmFacade.ShiftLeftUnsignedWordByImmediate),
            InstructionsEnum.ROLI => nameof(AppAsmFacade.RotateLeftByImmediate),
            InstructionsEnum.RORI => nameof(AppAsmFacade.RotateRightByImmediate),
            InstructionsEnum.BSET => nameof(AppAsmFacade.SetBitRegister),
            InstructionsEnum.BCLR => nameof(AppAsmFacade.ClearBitRegister),
            InstructionsEnum.BINV => nameof(AppAsmFacade.InvertBitRegister),
            InstructionsEnum.BEXT => nameof(AppAsmFacade.ExtractBitRegister),
            InstructionsEnum.BSETI => nameof(AppAsmFacade.SetBitImmediate),
            InstructionsEnum.BCLRI => nameof(AppAsmFacade.ClearBitImmediate),
            InstructionsEnum.BINVI => nameof(AppAsmFacade.InvertBitImmediate),
            InstructionsEnum.BEXTI => nameof(AppAsmFacade.ExtractBitImmediate),
            InstructionsEnum.REV8 => nameof(AppAsmFacade.ReverseByteOrder),
            InstructionsEnum.BREV8 => nameof(AppAsmFacade.ReverseBitsInEachByte),
            InstructionsEnum.SEXT_B => nameof(AppAsmFacade.SignExtendByte),
            InstructionsEnum.SEXT_H => nameof(AppAsmFacade.SignExtendHalf),
            InstructionsEnum.ZEXT_H => nameof(AppAsmFacade.ZeroExtendHalf),
            _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Unsupported compiler optional opcode.")
        };
    }

    private static string ResolveCompilerOptionalMnemonic(InstructionsEnum opcode)
    {
        return opcode switch
        {
            InstructionsEnum.CLZ => "CLZ",
            InstructionsEnum.CTZ => "CTZ",
            InstructionsEnum.CPOP => "CPOP",
            InstructionsEnum.CZERO_EQZ => "CZERO.EQZ",
            InstructionsEnum.CZERO_NEZ => "CZERO.NEZ",
            InstructionsEnum.ROL => "ROL",
            InstructionsEnum.ROR => "ROR",
            InstructionsEnum.ANDN => "ANDN",
            InstructionsEnum.ORN => "ORN",
            InstructionsEnum.XNOR => "XNOR",
            InstructionsEnum.MIN => "MIN",
            InstructionsEnum.MAX => "MAX",
            InstructionsEnum.MINU => "MINU",
            InstructionsEnum.MAXU => "MAXU",
            InstructionsEnum.CLMUL => "CLMUL",
            InstructionsEnum.CLMULH => "CLMULH",
            InstructionsEnum.CLMULR => "CLMULR",
            InstructionsEnum.SH1ADD => "SH1ADD",
            InstructionsEnum.SH2ADD => "SH2ADD",
            InstructionsEnum.SH3ADD => "SH3ADD",
            InstructionsEnum.ADD_UW => "ADD.UW",
            InstructionsEnum.SH1ADD_UW => "SH1ADD.UW",
            InstructionsEnum.SH2ADD_UW => "SH2ADD.UW",
            InstructionsEnum.SH3ADD_UW => "SH3ADD.UW",
            InstructionsEnum.SLLI_UW => "SLLI.UW",
            InstructionsEnum.ROLI => "ROLI",
            InstructionsEnum.RORI => "RORI",
            InstructionsEnum.BSET => "BSET",
            InstructionsEnum.BCLR => "BCLR",
            InstructionsEnum.BINV => "BINV",
            InstructionsEnum.BEXT => "BEXT",
            InstructionsEnum.BSETI => "BSETI",
            InstructionsEnum.BCLRI => "BCLRI",
            InstructionsEnum.BINVI => "BINVI",
            InstructionsEnum.BEXTI => "BEXTI",
            InstructionsEnum.REV8 => "REV8",
            InstructionsEnum.BREV8 => "BREV8",
            InstructionsEnum.SEXT_B => "SEXT.B",
            InstructionsEnum.SEXT_H => "SEXT.H",
            InstructionsEnum.ZEXT_H => "ZEXT.H",
            _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Unsupported compiler optional opcode.")
        };
    }

}
