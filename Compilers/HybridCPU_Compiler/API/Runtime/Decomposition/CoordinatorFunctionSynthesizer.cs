using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU.Core;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;


namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Synthesizes the coordinator function that sets up workers, launches them, and collects results.
/// Coordinator runs on VT0 and manages chunk distribution + barrier + reduction merge.
/// </summary>
public sealed class CoordinatorFunctionSynthesizer
{
    /// <summary>
    /// Synthesizes a coordinator IR program that manages parallel worker execution.
    /// </summary>
    /// <param name="region">The parallel region being decomposed.</param>
    /// <param name="plan">The chunk plan describing worker distribution.</param>
    /// <returns>Coordinator IR program.</returns>
    public IrProgram SynthesizeCoordinator(
        ParallelRegionInfo region,
        ChunkPlan plan)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(plan);

        HybridCpuThreadCompilerContext context = new HybridCPU.Compiler.Core.Threading.HybridCpuThreadCompilerContext((byte)plan.CoordinatorVtId);

        // Phase 1: Store chunk boundaries to worker-accessible memory
        EmitChunkBoundarySetup(context, region, plan);

        // Phase 2: Launch workers (using existing VT primitives)
        EmitWorkerLaunch(context, plan);

        // Phase 3: Barrier — wait for all workers to complete
        EmitBarrier(context, plan);

        // Phase 4: For reductions — collect partial results and merge
        if (region.Reduction is not null)
        {
            EmitReductionMerge(context, region, plan);
        }

        return context.BuildIrProgram();
    }

    /// <summary>
    /// Stores chunk start/end values to memory locations accessible by workers.
    /// </summary>
    // Register assignments in distinct safety-mask groups:
    // Group = (regId / 4) & 0xF. Different groups avoid write-write conflicts.
    private const int SetupReg  = 8;   // group 2 — chunk boundary setup
    private const int AccumReg  = 20;  // group 5 — reduction accumulator
    private const int PartialReg = 24; // group 6 — partial result temp

    private static void EmitChunkBoundarySetup(
        HybridCPU.Compiler.Core.Threading.HybridCpuThreadCompilerContext context,
        ParallelRegionInfo region,
        ChunkPlan plan)
    {
        for (int i = 0; i < plan.WorkerCount; i++)
        {
            (long start, long end) = PartitionPlanner.GetWorkerRange(plan, i, region);

            // Store chunk start at base + vtId * 16
            ulong chunkInfoAddr = (ulong)(0x10000 + plan.WorkerVtIds[i] * 16);
            EmitLoadImmediate(context, SetupReg, (ulong)start);
            EmitStore(context, SetupReg, chunkInfoAddr);

            // Store chunk end at base + vtId * 16 + 8
            EmitLoadImmediate(context, SetupReg, (ulong)end);
            EmitStore(context, SetupReg, chunkInfoAddr + 8);
        }
    }

    /// <summary>
    /// Launches worker VTs using the ISA v2 syscall path.
    /// </summary>
    private static void EmitWorkerLaunch(
        HybridCPU.Compiler.Core.Threading.HybridCpuThreadCompilerContext context,
        ChunkPlan plan)
    {
        for (int i = 0; i < plan.WorkerCount; i++)
        {
            // ECALL-based worker launch request with VT id encoded in the immediate field.
            context.CompileInstruction(
                opCode: (uint)HybridCpuOpcode.ECALL,
                dataType: (byte)HybridCpuDataType.INT32,
                predicate: 0xFF,
                immediate: (ushort)plan.WorkerVtIds[i],
                destSrc1: 0,
                src2: 0,
                streamLength: 0,
                stride: 0,
                stealabilityPolicy: StealabilityPolicy.NotStealable);
        }
    }

    /// <summary>
    /// Emits a barrier using ISA v2 fence operations to synchronize with workers.
    /// </summary>
    private static void EmitBarrier(
        HybridCPU.Compiler.Core.Threading.HybridCpuThreadCompilerContext context,
        ChunkPlan plan)
    {
        // Preserve the two-phase barrier shape with explicit fences.
        context.CompileInstruction(
            opCode: (uint)HybridCpuOpcode.FENCE,
            dataType: 0,
            predicate: 0,
            immediate: 0,
            destSrc1: 0,
            src2: 0,
            streamLength: 0,
            stride: 0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);

        context.CompileInstruction(
            opCode: (uint)HybridCpuOpcode.FENCE,
            dataType: 0,
            predicate: 0,
            immediate: 0,
            destSrc1: 0,
            src2: 0,
            streamLength: 0,
            stride: 0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    /// <summary>
    /// Collects partial results from workers and applies the final reduction.
    /// </summary>
    private static void EmitReductionMerge(
        HybridCPU.Compiler.Core.Threading.HybridCpuThreadCompilerContext context,
        ParallelRegionInfo region,
        ChunkPlan plan)
    {
        ReductionPlan reduction = region.Reduction!;

        // Initialize final accumulator with identity element (group 5)
        EmitLoadImmediate(context, AccumReg, (ulong)reduction.IdentityElement);

        // Load and reduce each worker's partial result
        for (int i = 0; i < plan.WorkerCount; i++)
        {
            ulong partialAddr = (ulong)(reduction.PartialResultBaseAddress + plan.WorkerVtIds[i] * 8);

            // Load partial result into distinct group (group 6)
            EmitLoad(context, PartialReg, partialAddr);

            // Reduce: accumulator = accumulator OP partial
            // Use proper register ID (not PackRegisters) for destSrc1
            context.CompileInstruction(
                opCode: (uint)reduction.ReduceOpcode,
                dataType: (byte)HybridCpuDataType.INT32,
                predicate: 0xFF,
                immediate: 0,
                destSrc1: AccumReg,
                src2: PartialReg,
                streamLength: 1,
                stride: 1,
                stealabilityPolicy: StealabilityPolicy.NotStealable);
        }
    }

    private static void EmitLoadImmediate(
        HybridCPU.Compiler.Core.Threading.HybridCpuThreadCompilerContext context,
        ulong reg, ulong value)
    {
        if (value > (ulong)short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Coordinator synthesis currently supports canonical ADDI rd, x0, imm16 emission only.");
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

    private static void EmitStore(
        HybridCPU.Compiler.Core.Threading.HybridCpuThreadCompilerContext context,
        int sourceReg, ulong address)
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

    private static void EmitLoad(
        HybridCPU.Compiler.Core.Threading.HybridCpuThreadCompilerContext context,
        int destReg, ulong address)
    {
        context.CompileInstruction(
            opCode: (uint)HybridCpuOpcode.LD,
            dataType: (byte)HybridCpuDataType.INT64,
            predicate: 0xFF,
            immediate: 0,
            destSrc1: HybridCpuInstructionWord.PackArchRegs(
                (byte)destReg,
                HybridCpuInstructionWord.NoArchReg,
                HybridCpuInstructionWord.NoArchReg),
            src2: address,
            streamLength: 0,
            stride: 0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }
}
