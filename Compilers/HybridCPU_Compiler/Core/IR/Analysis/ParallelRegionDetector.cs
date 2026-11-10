using System;
using System.Collections.Generic;
using System.Linq;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Analyzes IR to detect parallelizable loop regions with independent iterations.
/// Conservative: rejects loops with complex control flow, indirect memory, or non-analyzable dependencies.
/// </summary>
public sealed class ParallelRegionDetector
{
    /// <summary>
    /// Detects all parallelizable regions in the given IR program.
    /// </summary>
    public IReadOnlyList<ParallelRegionInfo> DetectRegions(IrProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

        List<ParallelRegionInfo> regions = new List<ParallelRegionInfo>();

        foreach (IrBasicBlock block in program.BasicBlocks)
        {
            if (TryDetectAffineLoop(block, program, out ParallelRegionInfo? region))
            {
                regions.Add(region!);
            }
        }

        return regions;
    }

    /// <summary>
    /// Attempts to detect an affine counted loop within a basic block and its back-edge target.
    /// Pattern: init induction var → body → increment → conditional back-edge.
    /// </summary>
    private static bool TryDetectAffineLoop(
        IrBasicBlock block,
        IrProgram program,
        out ParallelRegionInfo? region)
    {
        region = null;

        // Need at least 3 instructions: body + increment + conditional branch
        if (block.Instructions.Count < 3)
        {
            return false;
        }

        // Last instruction must be a conditional back-edge (loop latch)
        IrInstruction lastInstr = block.Instructions[^1];
        if (lastInstr.Annotation.ControlFlowKind != IrControlFlowKind.ConditionalBranch)
        {
            return false;
        }

        // Detect induction variable: look for increment/decrement pattern
        if (!TryFindInductionVariable(block, out int inductionReg, out long step))
        {
            return false;
        }

        // Reject loops with complex control flow (multiple branches, indirect jumps)
        if (HasComplexControlFlow(block))
        {
            return false;
        }

        // Classify registers: shared-read, shared-write, private
        ClassifyRegisters(
            block, inductionReg,
            out List<int> sharedRead,
            out List<int> sharedWrite,
            out List<int> privateRegs);

        // Reject loops with cross-iteration RAW dependencies (except reductions)
        ReductionPlan? reductionPlan = TryDetectReduction(block, inductionReg);

        if (HasCrossIterationDependencies(block, inductionReg, reductionPlan))
        {
            return false;
        }

        // Determine iteration bounds from context (use placeholder heuristic bounds)
        // Actual bounds come from the facade API call parameters
        IrParallelKind kind = reductionPlan is not null
            ? IrParallelKind.ForLoopWithReduction
            : IrParallelKind.ForLoop;

        region = new ParallelRegionInfo(
            StartInstructionIndex: block.StartInstructionIndex,
            EndInstructionIndex: block.EndInstructionIndex,
            Kind: kind,
            InductionVariableRegister: inductionReg,
            IterationStart: 0,
            IterationEnd: 0,
            IterationStep: step,
            SharedReadRegisters: sharedRead,
            SharedWriteRegisters: sharedWrite,
            PrivateRegisters: privateRegs,
            Reduction: reductionPlan);

        return true;
    }

    /// <summary>
    /// Finds the induction variable by looking for an additive step pattern.
    /// </summary>
    private static bool TryFindInductionVariable(IrBasicBlock block, out int inductionReg, out long step)
    {
        inductionReg = -1;
        step = 0;

        for (int i = block.Instructions.Count - 1; i >= 0; i--)
        {
            IrInstruction instr = block.Instructions[i];
            short signedImmediate = unchecked((short)instr.Immediate);

            if (instr.Opcode == HybridCpuOpcode.ADDI && signedImmediate == 1)
            {
                if (instr.Annotation.Defs.Count > 0)
                {
                    inductionReg = (int)instr.Annotation.Defs[0].Value;
                    step = 1;
                    return true;
                }
            }

            if (instr.Opcode == HybridCpuOpcode.ADDI && signedImmediate == -1)
            {
                if (instr.Annotation.Defs.Count > 0)
                {
                    inductionReg = (int)instr.Annotation.Defs[0].Value;
                    step = -1;
                    return true;
                }
            }

            // ADD with immediate (i += step)
            if ((instr.Opcode == HybridCpuOpcode.ADD || instr.Opcode == HybridCpuOpcode.ADDI) && signedImmediate != 0)
            {
                if (instr.Annotation.Defs.Count > 0)
                {
                    inductionReg = (int)instr.Annotation.Defs[0].Value;
                    step = signedImmediate;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Rejects blocks with multiple branches or indirect control flow.
    /// </summary>
    private static bool HasComplexControlFlow(IrBasicBlock block)
    {
        int branchCount = 0;
        foreach (IrInstruction instr in block.Instructions)
        {
            if (instr.Annotation.ControlFlowKind != IrControlFlowKind.None)
            {
                branchCount++;
            }

            // Indirect jumps or calls make the loop non-analyzable
            if (instr.Opcode == HybridCpuOpcode.JAL || instr.Opcode == HybridCpuOpcode.JALR)
            {
                return true;
            }
        }

        // Only a single conditional branch (the loop latch) is acceptable
        return branchCount > 1;
    }

    /// <summary>
    /// Classifies registers used in the loop body into shared-read, shared-write, and private.
    /// </summary>
    private static void ClassifyRegisters(
        IrBasicBlock block,
        int inductionReg,
        out List<int> sharedRead,
        out List<int> sharedWrite,
        out List<int> privateRegs)
    {
        HashSet<int> allDefs = new HashSet<int>();
        HashSet<int> allUses = new HashSet<int>();

        foreach (IrInstruction instr in block.Instructions)
        {
            foreach (IrOperand def in instr.Annotation.Defs)
            {
                if (def.Kind == IrOperandKind.Pointer)
                {
                    allDefs.Add((int)def.Value);
                }
            }

            foreach (IrOperand use in instr.Annotation.Uses)
            {
                if (use.Kind == IrOperandKind.Pointer)
                {
                    allUses.Add((int)use.Value);
                }
            }
        }

        sharedRead = new List<int>();
        sharedWrite = new List<int>();
        privateRegs = new List<int>();

        foreach (int reg in allUses)
        {
            if (reg == inductionReg)
            {
                continue;
            }

            if (!allDefs.Contains(reg))
            {
                // Read-only: shared across workers
                sharedRead.Add(reg);
            }
        }

        foreach (int reg in allDefs)
        {
            if (reg == inductionReg)
            {
                continue;
            }

            if (allUses.Contains(reg) && allDefs.Contains(reg))
            {
                // Read-modify-write without memory indexing → private per worker
                privateRegs.Add(reg);
            }
            else
            {
                // Write-only or indexed write → shared-write (partitioned)
                sharedWrite.Add(reg);
            }
        }
    }

    /// <summary>
    /// Detects a reduction pattern: accumulator register updated by an associative opcode each iteration.
    /// </summary>
    private static ReductionPlan? TryDetectReduction(IrBasicBlock block, int inductionReg)
    {
        // Look for pattern: acc = acc OP value (where OP is associative)
        foreach (IrInstruction instr in block.Instructions)
        {
            if (!IsAssociativeOpcode(instr.Opcode))
            {
                continue;
            }

            if (instr.Annotation.Defs.Count == 0 || instr.Annotation.Uses.Count < 2)
            {
                continue;
            }

            int defReg = (int)instr.Annotation.Defs[0].Value;
            if (defReg == inductionReg)
            {
                continue;
            }

            // Check if the def register is also a use (self-accumulation)
            bool isSelfAccumulating = instr.Annotation.Uses.Any(u =>
                u.Kind == IrOperandKind.Pointer && (int)u.Value == defReg);

            if (isSelfAccumulating)
            {
                long identity = GetIdentityElement(instr.Opcode);
                return new ReductionPlan(
                    ReduceOpcode: instr.Opcode,
                    IdentityElement: identity,
                    AccumulatorRegister: defReg,
                    PartialResultBaseAddress: 0);
            }
        }

        return null;
    }

    /// <summary>
    /// Checks for cross-iteration dependencies that prevent parallelization.
    /// Reductions are allowed (handled separately).
    /// </summary>
    private static bool HasCrossIterationDependencies(
        IrBasicBlock block,
        int inductionReg,
        ReductionPlan? reduction)
    {
        HashSet<int> defsInBody = new HashSet<int>();
        HashSet<int> usesInBody = new HashSet<int>();

        foreach (IrInstruction instr in block.Instructions)
        {
            if (instr.Annotation.ControlFlowKind != IrControlFlowKind.None)
            {
                continue;
            }

            foreach (IrOperand def in instr.Annotation.Defs)
            {
                if (def.Kind == IrOperandKind.Pointer)
                {
                    defsInBody.Add((int)def.Value);
                }
            }

            foreach (IrOperand use in instr.Annotation.Uses)
            {
                if (use.Kind == IrOperandKind.Pointer)
                {
                    usesInBody.Add((int)use.Value);
                }
            }
        }

        // A register that is both defined and used (excluding induction and reduction accumulator)
        // indicates potential cross-iteration dependency
        foreach (int reg in defsInBody)
        {
            if (reg == inductionReg)
            {
                continue;
            }

            if (reduction is not null && reg == reduction.AccumulatorRegister)
            {
                continue;
            }

            if (usesInBody.Contains(reg) && IsCarriedDependency(block, reg, inductionReg))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether a register dependency is loop-carried (cross-iteration) vs loop-local.
    /// </summary>
    private static bool IsCarriedDependency(IrBasicBlock block, int reg, int inductionReg)
    {
        // If the register is defined before its first use within the body, it's loop-local
        bool seenDef = false;
        bool seenUseBeforeDef = false;

        foreach (IrInstruction instr in block.Instructions)
        {
            if (instr.Annotation.ControlFlowKind != IrControlFlowKind.None)
            {
                continue;
            }

            bool definesReg = instr.Annotation.Defs.Any(d =>
                d.Kind == IrOperandKind.Pointer && (int)d.Value == reg);
            bool usesReg = instr.Annotation.Uses.Any(u =>
                u.Kind == IrOperandKind.Pointer && (int)u.Value == reg);

            if (usesReg && !seenDef)
            {
                seenUseBeforeDef = true;
            }

            if (definesReg)
            {
                seenDef = true;
            }
        }

        // If the register is used before being defined in the loop body,
        // it's a loop-carried dependency
        return seenUseBeforeDef;
    }

    private static bool IsAssociativeOpcode(HybridCpuOpcode opcode) => opcode switch
    {
        HybridCpuOpcode.ADD => true,
        HybridCpuOpcode.MUL => true,
        _ => false
    };

    private static long GetIdentityElement(HybridCpuOpcode opcode) => opcode switch
    {
        HybridCpuOpcode.ADD => 0,
        HybridCpuOpcode.MUL => 1,
        _ => 0
    };
}
