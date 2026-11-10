using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU.Core;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// This is the most critical piece
/// Synthesizes worker function IR from a loop body and chunk assignment.
/// All IR construction goes through <see cref="HybridCpuIrBuilder"/> — never raw IR nodes.
/// </summary>
public sealed class WorkerFunctionSynthesizer
{
    // Safety-mask register-group boundaries:
    // Group = (regId / 4) & 0xF. Registers in the same group produce
    // write-write conflicts if bundled together. We assign pipeline-critical
    // registers to distinct groups.
    private const int InductionRegBase = 4;   // group 1
    private const int EndRegBase       = 8;   // group 2
    private const int BodyDestRegBase  = 12;  // group 3
    private const int BodySrcRegBase   = 16;  // group 4
    private const int AccumRegBase     = 20;  // group 5
    private const int TempRegBase      = 24;  // group 6

    /// <summary>
    /// Synthesizes a worker IR program for a specific chunk of a parallel region.
    /// The worker executes iterations [chunkStart, chunkEnd) with the given step.
    /// </summary>
    /// <param name="region">The parallel region being decomposed.</param>
    /// <param name="workerVtId">Virtual thread ID for this worker.</param>
    /// <param name="chunkStart">First iteration value for this worker.</param>
    /// <param name="chunkEnd">End iteration value (exclusive) for this worker.</param>
    /// <param name="bodyOpcodes">Opcodes forming the loop body (excluding induction variable management and branch).</param>
    /// <returns>Worker IR program ready for scheduling/bundling pipeline.</returns>
    public IrProgram SynthesizeWorker(
        ParallelRegionInfo region,
        byte workerVtId,
        long chunkStart,
        long chunkEnd,
        IReadOnlyList<HybridCpuOpcode> bodyOpcodes)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(bodyOpcodes);

        HybridCpuThreadCompilerContext context = new HybridCPU.Compiler.Core.Threading.HybridCpuThreadCompilerContext(workerVtId);

        // Map region's logical registers to non-conflicting safety-mask groups
        int inductionReg = InductionRegBase;
        int endReg = EndRegBase;
        int accumReg = region.Reduction is not null ? AccumRegBase : 0;

        // Initialize induction variable with chunk start
        EmitLoadImmediate(context, (ulong)inductionReg, (ulong)chunkStart);

        // Load chunk end into a temporary register for comparison
        EmitLoadImmediate(context, (ulong)endReg, (ulong)chunkEnd);

        // For reductions: initialize local accumulator with identity element
        if (region.Reduction is not null)
        {
            EmitLoadImmediate(context, (ulong)accumReg, (ulong)region.Reduction.IdentityElement);
        }

        // Emit loop body — each opcode uses distinct register groups
        foreach (HybridCpuOpcode opcode in bodyOpcodes)
        {
            EmitBodyInstruction(context, opcode);
        }

        // Increment induction variable
        EmitInductionIncrement(context, inductionReg, region.IterationStep);

        // Conditional back-edge: jump if induction < chunkEnd
        EmitConditionalBackEdge(context, inductionReg, endReg);

        // For reductions: store partial result to shared memory
        if (region.Reduction is not null)
        {
            int partialAddr = region.Reduction.PartialResultBaseAddress + workerVtId * 8;
            EmitStore(context, accumReg, (ulong)partialAddr);
        }

        return context.BuildIrProgram();
    }

    /// <summary>
    /// Synthesizes a minimal worker IR program from pre-built VLIW instructions.
    /// Used when the caller has already constructed the instruction sequence.
    /// </summary>
    public IrProgram SynthesizeWorkerFromInstructions(
        byte workerVtId,
        IReadOnlyList<(HybridCpuOpcode Opcode, ulong DestSrc1, ulong Src2, ushort Immediate)> instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);

        HybridCpuThreadCompilerContext context = new HybridCPU.Compiler.Core.Threading.HybridCpuThreadCompilerContext(workerVtId);

        foreach ((HybridCpuOpcode Opcode, ulong DestSrc1, ulong Src2, ushort Immediate) instr in instructions)
        {
            byte dataType = (byte)HybridCpuDataType.INT32;
            byte predicate = 0xFF;
            (HybridCpuOpcode opcode, ulong destSrc1) =
                HybridCpuOpcodeSemantics.NormalizeRetainedConditionalWrapperForEmission(
                    instr.Opcode,
                    instr.DestSrc1);

            if (UsesZeroLengthControlEncoding(opcode))
            {
                context.CompileInstruction(
                    (uint)opcode, dataType, predicate, instr.Immediate,
                    destSrc1, instr.Src2, 0, 0,
                    stealabilityPolicy: StealabilityPolicy.NotStealable);
            }
            else
            {
                context.CompileInstruction(
                    (uint)opcode, dataType, predicate, instr.Immediate,
                    destSrc1, instr.Src2, 1, 1,
                    stealabilityPolicy: StealabilityPolicy.NotStealable);
            }
        }

        return context.BuildIrProgram();
    }

    private static void EmitLoadImmediate(HybridCPU.Compiler.Core.Threading.HybridCpuThreadCompilerContext context, ulong reg, ulong value)
    {
        if (value > (ulong)short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Worker synthesis currently supports canonical ADDI rd, x0, imm16 emission only.");
        }

        context.CompileInstruction(
            opCode: (uint)HybridCpuOpcode.ADDI,
            dataType: (byte)HybridCpuDataType.INT64,
            predicate: 0xFF,
            immediate: checked((ushort)(short)value),
            destSrc1: HybridCpuInstructionWord.PackArchRegs(
                checked((byte)reg),
                HybridCpuInstructionWord.NoArchReg,
                HybridCpuInstructionWord.NoArchReg),
            src2: 0,
            streamLength: 0,
            stride: 0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    private static void EmitBodyInstruction(
        HybridCPU.Compiler.Core.Threading.HybridCpuThreadCompilerContext context,
        HybridCpuOpcode opcode)
    {
        // Body dest/src use distinct register groups (3 and 4) to avoid
        // safety-mask write-group collisions with induction/end registers.
        context.CompileInstruction(
            opCode: (uint)opcode,
            dataType: (byte)HybridCpuDataType.INT32,
            predicate: 0xFF,
            immediate: 0,
            destSrc1: BodyDestRegBase,
            src2: BodySrcRegBase,
            streamLength: 1,
            stride: 1,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    private static void EmitInductionIncrement(
        HybridCPU.Compiler.Core.Threading.HybridCpuThreadCompilerContext context,
        int inductionReg,
        long iterationStep)
    {
        if (iterationStep == 1)
        {
            context.CompileInstruction(
                opCode: (uint)HybridCpuOpcode.ADDI,
                dataType: (byte)HybridCpuDataType.INT32,
                predicate: 0xFF,
                immediate: 1,
                destSrc1: (ulong)inductionReg,
                src2: 0,
                streamLength: 0,
                stride: 0,
                stealabilityPolicy: StealabilityPolicy.NotStealable);
        }
        else
        {
            context.CompileInstruction(
                opCode: (uint)HybridCpuOpcode.ADD,
                dataType: (byte)HybridCpuDataType.INT32,
                predicate: 0xFF,
                immediate: (ushort)iterationStep,
                destSrc1: (ulong)inductionReg,
                src2: (ulong)iterationStep,
                streamLength: 1,
                stride: 1,
                stealabilityPolicy: StealabilityPolicy.NotStealable);
        }
    }

    private static void EmitConditionalBackEdge(
        HybridCPU.Compiler.Core.Threading.HybridCpuThreadCompilerContext context,
        int inductionReg,
        int endReg)
    {
        // Canonical unsigned back-edge keeps the compiler-side control-flow contract
        // aligned with the published branch surface instead of emitting a retained wrapper.
        context.CompileInstruction(
            opCode: (uint)HybridCpuOpcode.BLTU,
            dataType: (byte)HybridCpuDataType.INT32,
            predicate: 0xFF,
            immediate: 5,
            destSrc1: HybridCpuInstructionWord.PackArchRegs(
                HybridCpuInstructionWord.NoArchReg,
                (byte)inductionReg,
                (byte)endReg),
            src2: 0,
            streamLength: 0,
            stride: 0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    private static void EmitStore(
        HybridCPU.Compiler.Core.Threading.HybridCpuThreadCompilerContext context,
        int sourceReg,
        ulong address)
    {
        context.CompileInstruction(
            opCode: (uint)HybridCpuOpcode.SD,
            dataType: (byte)HybridCpuDataType.INT64,
            predicate: 0xFF,
            immediate: 0,
            destSrc1: HybridCpuInstructionWord.PackArchRegs(
                HybridCpuInstructionWord.NoArchReg,
                HybridCpuInstructionWord.NoArchReg,
                (byte)sourceReg),
            src2: address,
            streamLength: 0,
            stride: 0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    private static bool UsesZeroLengthControlEncoding(HybridCpuOpcode opcode)
    {
        return opcode is
            HybridCpuOpcode.JAL or
            HybridCpuOpcode.JALR or
            HybridCpuOpcode.BEQ or
            HybridCpuOpcode.BNE or
            HybridCpuOpcode.BLT or
            HybridCpuOpcode.BGE or
            HybridCpuOpcode.BLTU or
            HybridCpuOpcode.BGEU;
    }

    }
