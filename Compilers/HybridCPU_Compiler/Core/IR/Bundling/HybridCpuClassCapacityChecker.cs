using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR.Resources;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Validates that a candidate instruction group respects class-level capacity
/// before attempting physical slot search.
/// Stateless — mirrors ISE-side <see cref="SlotClassCapacityState"/> logic
/// but operates on static IR, not runtime MicroOp.
/// </summary>
public static class HybridCpuClassCapacityChecker
{
    /// <summary>
    /// Check if a candidate group fits within class-level capacity bounds.
    /// Unclassified ops are excluded from typed capacity counting.
    /// </summary>
    public static IrClassCapacityResult CheckCapacity(IReadOnlyList<IrInstruction> instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);

        int aluCount = 0;
        int lsuCount = 0;
        int dmaStreamCount = 0;
        int branchControlCount = 0;
        int systemSingletonCount = 0;

        foreach (IrInstruction instruction in instructions)
        {
            switch (instruction.Annotation.RequiredSlotClass)
            {
                case IrSlotClass.AluClass: aluCount++; break;
                case IrSlotClass.LsuClass: lsuCount++; break;
                case IrSlotClass.DmaStreamClass: dmaStreamCount++; break;
                case IrSlotClass.BranchControl: branchControlCount++; break;
                case IrSlotClass.SystemSingleton: systemSingletonCount++; break;
                    // Unclassified ops do not consume typed capacity
            }
        }

        HybridCpuMachineDescriptionV1 machine = HybridCpuMachineDescriptionV1.Default;
        int aluCapacity = machine.GetSlotClassCapacity(IrSlotClass.AluClass);
        int lsuCapacity = machine.GetSlotClassCapacity(IrSlotClass.LsuClass);
        int dmaStreamCapacity = machine.GetSlotClassCapacity(IrSlotClass.DmaStreamClass);
        int branchControlCapacity = machine.GetSlotClassCapacity(IrSlotClass.BranchControl);
        int systemSingletonCapacity = machine.GetSlotClassCapacity(IrSlotClass.SystemSingleton);

        bool hasAliasConflict = HasAliasedLaneConflict(
            branchControlCount, systemSingletonCount);

        bool isWithinCapacity =
            aluCount <= aluCapacity &&
            lsuCount <= lsuCapacity &&
            dmaStreamCount <= dmaStreamCapacity &&
            branchControlCount <= branchControlCapacity &&
            systemSingletonCount <= systemSingletonCapacity &&
            !hasAliasConflict;

        return new IrClassCapacityResult(
            AluCount: aluCount, AluCapacity: aluCapacity,
            LsuCount: lsuCount, LsuCapacity: lsuCapacity,
            DmaStreamCount: dmaStreamCount, DmaStreamCapacity: dmaStreamCapacity,
            BranchControlCount: branchControlCount, BranchControlCapacity: branchControlCapacity,
            SystemSingletonCount: systemSingletonCount, SystemSingletonCapacity: systemSingletonCapacity,
            HasAliasedLaneConflict: hasAliasConflict,
            IsWithinCapacity: isWithinCapacity);
    }

    /// <summary>
    /// Check aliased-lane mutual exclusion.
    /// Uses <see cref="SlotClassLaneMap.GetAliasedClasses"/> for generic detection.
    /// </summary>
    public static bool HasAliasedLaneConflict(IReadOnlyList<IrInstruction> instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);

        int branchControlCount = 0;
        int systemSingletonCount = 0;

        foreach (IrInstruction instruction in instructions)
        {
            switch (instruction.Annotation.RequiredSlotClass)
            {
                case IrSlotClass.BranchControl: branchControlCount++; break;
                case IrSlotClass.SystemSingleton: systemSingletonCount++; break;
            }
        }

        return HasAliasedLaneConflict(branchControlCount, systemSingletonCount);
    }

    /// <summary>
    /// Determines if the given per-class counts produce an aliased-lane conflict.
    /// Two classes conflict when both have nonzero counts and share physical lanes
    /// (per <see cref="SlotClassLaneMap.GetAliasedClasses"/>).
    /// </summary>
    private static bool HasAliasedLaneConflict(int branchControlCount, int systemSingletonCount)
    {
        // BranchControl and SystemSingleton share lane 7.
        // If both are present, they cannot coexist — alias conflict.
        if (branchControlCount > 0 && systemSingletonCount > 0 && HybridCpuMachineDescriptionV1.Default.HasAliasedLanes(IrSlotClass.BranchControl))
            return true;

        // Also check combined occupancy against the shared lane capacity.
        // Lane 7 has capacity 1, so even if only one class is present,
        // it cannot exceed 1. This is already covered by per-class capacity
        // checks, but aliased-lane logic from ISE also considers cross-class
        // occupancy reduction. For compiler pre-check the explicit co-presence
        // test above is sufficient.
        return false;
    }
}
