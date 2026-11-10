using System;
using System.Linq;
using HybridCPU.Compiler.Core.API.Facade;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerFacadeControlFlowEmissionTests
{

    [Fact]
    public void AppJump_EmitsRelocationAwareJalThroughCanonicalImmediateOnly()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        AsmControlTarget target = facade.DefineEntryPoint("target");
        facade.Jump(target);
        facade.Nop();
        facade.MarkEntryPoint(target);
        facade.Nop();
#pragma warning restore CS0618

        VLIW_Instruction raw = context.GetCompiledInstructions()[0];
        Assert.Equal(InstructionsEnum.JAL, (InstructionsEnum)raw.OpCode);
        Assert.Equal(0, raw.Immediate);
        Assert.Equal(0UL, raw.Src2Pointer);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(0, rawRd);
        Assert.Equal(VLIW_Instruction.NoArchReg, rawRs1);
        Assert.Equal(VLIW_Instruction.NoArchReg, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = compiledProgram.BundleLayout.Program.Instructions[0];
        Assert.Equal("target", ir.Annotation.BranchTargetSymbolName);
        Assert.Equal(2, ir.Annotation.ResolvedBranchTargetInstructionIndex);
        Assert.Equal(IrControlFlowKind.UnconditionalBranch, ir.Annotation.ControlFlowKind);

        LocatedInstruction source = LocateInstruction(compiledProgram, instructionIndex: 0);
        LocatedInstruction destination = LocateInstruction(compiledProgram, instructionIndex: 2);
        short expectedDisplacement = ComputeExpectedDisplacement(source, destination);

        Assert.Equal(7, source.SlotIndex);
        Assert.Equal(unchecked((ushort)expectedDisplacement), source.Instruction.Immediate);
        Assert.Equal(0UL, source.Instruction.Src2Pointer);

        BranchMicroOp branch = DecodeBranch(compiledProgram, source);
        Assert.False(branch.IsConditional);
        Assert.Equal((ushort)0, branch.DestRegID);
        Assert.Equal(VLIW_Instruction.NoReg, branch.Reg1ID);
        Assert.Equal(VLIW_Instruction.NoReg, branch.Reg2ID);
        Assert.True(branch.HasRelativeTargetDisplacement);
        Assert.Equal(expectedDisplacement, branch.RelativeTargetDisplacement);
        Assert.Empty(branch.ReadRegisters);
        Assert.Empty(branch.WriteRegisters);
    }

    [Fact]
    public void AppJumpIfNotEqual_EmitsCanonicalBneWithImmediateRelocation()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        AsmControlTarget target = facade.DefineEntryPoint("not.equal.target");
        facade.JumpIfNotEqual(
            target,
            new AsmRegister(9),
            new AsmRegister(5),
            new AsmRegister(6),
            hint: 11);
        facade.Nop();
        facade.MarkEntryPoint(target);
        facade.Nop();
#pragma warning restore CS0618

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        LocatedInstruction source = LocateInstruction(compiledProgram, instructionIndex: 0);
        LocatedInstruction destination = LocateInstruction(compiledProgram, instructionIndex: 2);
        short expectedDisplacement = ComputeExpectedDisplacement(source, destination);

        Assert.Equal(InstructionsEnum.BNE, (InstructionsEnum)source.Instruction.OpCode);
        Assert.Equal(unchecked((ushort)expectedDisplacement), source.Instruction.Immediate);
        Assert.Equal(0UL, source.Instruction.Src2Pointer);

        IrInstruction ir = compiledProgram.BundleLayout.Program.Instructions[0];
        Assert.Equal("not.equal.target", ir.Annotation.BranchTargetSymbolName);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 5UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs2" && operand.Value == 6UL);
        Assert.DoesNotContain(ir.Annotation.Uses, operand => operand.Value == 9UL);

        BranchMicroOp branch = DecodeBranch(compiledProgram, source);
        Assert.True(branch.IsConditional);
        Assert.Equal((ushort)5, branch.Reg1ID);
        Assert.Equal((ushort)6, branch.Reg2ID);
        Assert.Equal(new[] { 5, 6 }, branch.ReadRegisters);
        Assert.Empty(branch.WriteRegisters);
        Assert.Equal(expectedDisplacement, branch.RelativeTargetDisplacement);
    }

    [Fact]
    public void AppJumpIfBelow_EmitsCanonicalBltuWithImmediateRelocation()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        AsmControlTarget target = facade.DefineEntryPoint("below.target");
        facade.JumpIfBelow(
            target,
            new AsmRegister(9),
            new AsmRegister(3),
            new AsmRegister(4),
            hint: 13);
        facade.Nop();
        facade.MarkEntryPoint(target);
        facade.Nop();
#pragma warning restore CS0618

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        LocatedInstruction source = LocateInstruction(compiledProgram, instructionIndex: 0);
        LocatedInstruction destination = LocateInstruction(compiledProgram, instructionIndex: 2);
        short expectedDisplacement = ComputeExpectedDisplacement(source, destination);

        Assert.Equal(InstructionsEnum.BLTU, (InstructionsEnum)source.Instruction.OpCode);
        Assert.Equal(unchecked((ushort)expectedDisplacement), source.Instruction.Immediate);
        Assert.Equal(0UL, source.Instruction.Src2Pointer);

        IrInstruction ir = compiledProgram.BundleLayout.Program.Instructions[0];
        Assert.Equal("below.target", ir.Annotation.BranchTargetSymbolName);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 3UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs2" && operand.Value == 4UL);
        Assert.DoesNotContain(ir.Annotation.Uses, operand => operand.Value == 9UL);

        BranchMicroOp branch = DecodeBranch(compiledProgram, source);
        Assert.True(branch.IsConditional);
        Assert.Equal((ushort)3, branch.Reg1ID);
        Assert.Equal((ushort)4, branch.Reg2ID);
        Assert.Equal(new[] { 3, 4 }, branch.ReadRegisters);
        Assert.Empty(branch.WriteRegisters);
        Assert.Equal(expectedDisplacement, branch.RelativeTargetDisplacement);
    }

    [Fact]
    public void AppJumpIfAbove_EmitsCanonicalBltuWithSwappedOperandsAndImmediateRelocation()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        AsmControlTarget target = facade.DefineEntryPoint("above.target");
        facade.JumpIfAbove(
            target,
            new AsmRegister(9),
            new AsmRegister(3),
            new AsmRegister(4),
            hint: 17);
        facade.Nop();
        facade.MarkEntryPoint(target);
        facade.Nop();
#pragma warning restore CS0618

        VLIW_Instruction raw = context.GetCompiledInstructions()[0];
        Assert.Equal(InstructionsEnum.BLTU, (InstructionsEnum)raw.OpCode);
        Assert.Equal(0, raw.Immediate);
        Assert.Equal(0UL, raw.Src2Pointer);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(VLIW_Instruction.NoArchReg, rawRd);
        Assert.Equal(4, rawRs1);
        Assert.Equal(3, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = compiledProgram.BundleLayout.Program.Instructions[0];
        Assert.Equal(InstructionsEnum.BLTU, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal("above.target", ir.Annotation.BranchTargetSymbolName);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 4UL);
        Assert.Contains(ir.Annotation.Uses, operand => operand.Name == "rs2" && operand.Value == 3UL);
        Assert.DoesNotContain(ir.Annotation.Uses, operand => operand.Value == 9UL);

        LocatedInstruction source = LocateInstruction(compiledProgram, instructionIndex: 0);
        LocatedInstruction destination = LocateInstruction(compiledProgram, instructionIndex: 2);
        short expectedDisplacement = ComputeExpectedDisplacement(source, destination);

        Assert.Equal(unchecked((ushort)expectedDisplacement), source.Instruction.Immediate);
        Assert.Equal(0UL, source.Instruction.Src2Pointer);

        BranchMicroOp branch = DecodeBranch(compiledProgram, source);
        Assert.True(branch.IsConditional);
        Assert.Equal((ushort)4, branch.Reg1ID);
        Assert.Equal((ushort)3, branch.Reg2ID);
        Assert.Equal(new[] { 4, 3 }, branch.ReadRegisters);
        Assert.Empty(branch.WriteRegisters);
        Assert.Equal(expectedDisplacement, branch.RelativeTargetDisplacement);
    }

    [Fact]
    public void AppCall_EmitsCanonicalJalLinkRegisterAndSymbolicImmediateRelocation()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        AsmControlTarget target = facade.DefineEntryPoint("call.target");
        facade.Call(target, new AsmRegister(1), hint: 0);
        facade.Nop();
        facade.MarkEntryPoint(target);
        facade.Nop();
#pragma warning restore CS0618

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        LocatedInstruction source = LocateInstruction(compiledProgram, instructionIndex: 0);
        LocatedInstruction destination = LocateInstruction(compiledProgram, instructionIndex: 2);
        short expectedDisplacement = ComputeExpectedDisplacement(source, destination);

        Assert.Equal(InstructionsEnum.JAL, (InstructionsEnum)source.Instruction.OpCode);
        Assert.Equal(unchecked((ushort)expectedDisplacement), source.Instruction.Immediate);
        Assert.Equal(0UL, source.Instruction.Src2Pointer);

        BranchMicroOp branch = DecodeBranch(compiledProgram, source);
        Assert.False(branch.IsConditional);
        Assert.Equal((ushort)1, branch.DestRegID);
        Assert.Equal(new[] { 1 }, branch.WriteRegisters);
        Assert.Empty(branch.ReadRegisters);
        Assert.Equal(expectedDisplacement, branch.RelativeTargetDisplacement);
    }

    [Fact]
    public void AppReturn_EmitsCanonicalJalrRegisterReturnWithoutTargetSideband()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        facade.Return(facade.DefineEntryPoint("return.contract"), new AsmRegister(1));
#pragma warning restore CS0618

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(InstructionsEnum.JALR, (InstructionsEnum)raw.OpCode);
        Assert.Equal(0, raw.Immediate);
        Assert.Equal(0UL, raw.Src2Pointer);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rawRd, out byte rawRs1, out byte rawRs2));
        Assert.Equal(0, rawRd);
        Assert.Equal(1, rawRs1);
        Assert.Equal(VLIW_Instruction.NoArchReg, rawRs2);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Null(ir.Annotation.BranchTargetSymbolName);
        Assert.Null(ir.Annotation.ResolvedBranchTargetInstructionIndex);

        LocatedInstruction source = LocateInstruction(compiledProgram, instructionIndex: 0);
        BranchMicroOp branch = DecodeBranch(compiledProgram, source);
        Assert.False(branch.IsConditional);
        Assert.Equal((ushort)0, branch.DestRegID);
        Assert.Equal((ushort)1, branch.Reg1ID);
        Assert.Equal(VLIW_Instruction.NoReg, branch.Reg2ID);
        Assert.Equal(new[] { 1 }, branch.ReadRegisters);
        Assert.Empty(branch.WriteRegisters);
        Assert.True(branch.HasRelativeTargetDisplacement);
        Assert.Equal(0, branch.RelativeTargetDisplacement);
    }

    [Fact]
    public void AppJump_WithUnmarkedSymbolicTarget_FailsClosedBeforeLowering()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

#pragma warning disable CS0618
        AppAsmFacade facade = new AppAsmFacade(0, context);
        facade.Jump(facade.DefineEntryPoint("missing.target"));
#pragma warning restore CS0618

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => context.CompileProgram());

        Assert.Contains("Unresolved symbolic control-flow target 'missing.target'", exception.Message, StringComparison.Ordinal);
    }

    private static BranchMicroOp DecodeBranch(
        HybridCpuCompiledProgram compiledProgram,
        LocatedInstruction locatedInstruction)
    {
        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[locatedInstruction.BundleIndex],
            compiledProgram.LoweredBundleAnnotations[locatedInstruction.BundleIndex],
            locatedInstruction.SlotIndex);
        return Assert.IsType<BranchMicroOp>(carrier);
    }

    private static short ComputeExpectedDisplacement(
        LocatedInstruction source,
        LocatedInstruction destination)
    {
        long displacement = (long)destination.BundleBaseAddress - (long)source.BundleBaseAddress;
        Assert.InRange(displacement, short.MinValue, short.MaxValue);
        return (short)displacement;
    }

    private static LocatedInstruction LocateInstruction(
        HybridCpuCompiledProgram compiledProgram,
        int instructionIndex)
    {
        int bundleIndex = 0;
        foreach (IrBasicBlockBundlingResult blockResult in compiledProgram.BundleLayout.BlockResults)
        {
            foreach (IrMaterializedBundle bundle in blockResult.Bundles)
            {
                if (bundle.TryGetSlotForInstruction(instructionIndex, out IrMaterializedBundleSlot? slot) &&
                    slot is not null)
                {
                    ulong bundleBaseAddress =
                        (ulong)bundleIndex * (ulong)HybridCpuBundleSerializer.BundleSizeBytes;
                    return new LocatedInstruction(
                        bundleIndex,
                        slot.SlotIndex,
                        bundleBaseAddress,
                        NativeTransportRuntimeAdapter.ToRuntime(
                            compiledProgram.LoweredBundles[bundleIndex].GetInstruction(slot.SlotIndex)));
                }

                bundleIndex++;
            }
        }

        throw new InvalidOperationException($"Instruction {instructionIndex} was not materialized.");
    }

    private sealed record LocatedInstruction(
        int BundleIndex,
        int SlotIndex,
        ulong BundleBaseAddress,
        VLIW_Instruction Instruction);
}
