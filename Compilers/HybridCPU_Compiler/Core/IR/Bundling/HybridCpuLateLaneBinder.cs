using System;
using System.Collections.Generic;
using System.Numerics;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Deterministic late-lane binder for class-level admissible bundles.
/// Mirrors ISE <see cref="DeterministicLaneChooser"/> rule: lowest free lane within class mask.
/// Stateless — produces identical output for identical input.
/// </summary>
public static class HybridCpuLateLaneBinder
{
    /// <summary>
    /// Bind instructions to physical lanes using deterministic lowest-free rule.
    /// Instructions are processed in most-constrained-first order:
    /// <list type="number">
    /// <item><see cref="IrSlotBindingKind.HardPinned"/> — fixed lane</item>
    /// <item><see cref="IrSlotBindingKind.SingletonClass"/> — single lane per class</item>
    /// <item><see cref="IrSlotBindingKind.ClassFlexible"/> — multiple free lanes</item>
    /// </list>
    /// Within same binding kind, classes with fewer lanes (lower capacity) are placed first.
    /// Within same class, original instruction order is preserved.
    /// </summary>
    public static IrLateLaneBindingResult BindLanes(
        IReadOnlyList<IrInstruction> instructions,
        IrClassCapacityResult capacityResult)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(capacityResult);

        if (instructions.Count == 0)
        {
            return new IrLateLaneBindingResult(
                BindingSuccess: true,
                AssignedLanes: [],
                BindingKinds: [],
                OccupiedLaneMask: 0,
                FailureReason: null);
        }

        int[] assignedLanes = new int[instructions.Count];
        IrSlotBindingKind[] bindingKinds = new IrSlotBindingKind[instructions.Count];
        Array.Fill(assignedLanes, -1);

        // Build sorted processing order: most-constrained-first
        int[] processingOrder = BuildConstraintPriorityOrder(instructions);

        byte occupiedLaneMask = 0;

        for (int i = 0; i < processingOrder.Length; i++)
        {
            int originalIndex = processingOrder[i];
            IrInstruction instruction = instructions[originalIndex];
            IrSlotBindingKind bindingKind = instruction.Annotation.BindingKind;
            bindingKinds[originalIndex] = bindingKind;

            // Use compiler-side structural slot facts (not ISE-side SlotClassLaneMap) to stay
            // compatible with IrMaterializedBundle.IsStructuralPlacement validation.
            // The deterministic lowest-free algorithm is the same — only the
            // coordinate system differs (compiler slots vs ISE lanes).
            byte structurallyAllowedSlotMask = (byte)instruction.Annotation.StructurallyAllowedSlots;
            byte freeLanes = (byte)(structurallyAllowedSlotMask & ~occupiedLaneMask);

            int lane = freeLanes == 0 ? -1 : BitOperations.TrailingZeroCount(freeLanes);

            if (lane == -1)
            {
                return new IrLateLaneBindingResult(
                    BindingSuccess: false,
                    AssignedLanes: assignedLanes,
                    BindingKinds: bindingKinds,
                    OccupiedLaneMask: occupiedLaneMask,
                    FailureReason: $"No free slot for instruction {originalIndex} " +
                                   $"(class={instruction.Annotation.RequiredSlotClass}, binding={bindingKind}, " +
                                  $"structurallyAllowedSlots=0b_{Convert.ToString(structurallyAllowedSlotMask, 2).PadLeft(8, '0')}, " +
                                   $"occupied=0b_{Convert.ToString(occupiedLaneMask, 2).PadLeft(8, '0')})");
            }

            assignedLanes[originalIndex] = lane;
            occupiedLaneMask |= (byte)(1 << lane);
        }

        return new IrLateLaneBindingResult(
            BindingSuccess: true,
            AssignedLanes: assignedLanes,
            BindingKinds: bindingKinds,
            OccupiedLaneMask: occupiedLaneMask,
            FailureReason: null);
    }

    /// <summary>
    /// Builds processing order sorted by constraint priority (most constrained first).
    /// </summary>
    private static int[] BuildConstraintPriorityOrder(IReadOnlyList<IrInstruction> instructions)
    {
        int[] order = new int[instructions.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            IrInstructionAnnotation annotA = instructions[a].Annotation;
            IrInstructionAnnotation annotB = instructions[b].Annotation;

            // Primary: binding kind (HardPinned=1 first, SingletonClass=2 second, ClassFlexible=0 last)
            int priorityA = GetBindingPriority(annotA.BindingKind);
            int priorityB = GetBindingPriority(annotB.BindingKind);
            int cmp = priorityA.CompareTo(priorityB);
            if (cmp != 0) return cmp;

            // Secondary: structural slot count ascending (fewest allowed slots = most constrained first)
            int capA = BitOperations.PopCount((uint)annotA.StructurallyAllowedSlots);
            int capB = BitOperations.PopCount((uint)annotB.StructurallyAllowedSlots);
            cmp = capA.CompareTo(capB);
            if (cmp != 0) return cmp;

            // Tertiary: preserve original instruction order
            return a.CompareTo(b);
        });

        return order;
    }

    /// <summary>
    /// Maps binding kind to sort priority (lower = placed first).
    /// </summary>
    private static int GetBindingPriority(IrSlotBindingKind kind) => kind switch
    {
        IrSlotBindingKind.HardPinned => 0,
        IrSlotBindingKind.SingletonClass => 1,
        IrSlotBindingKind.ClassFlexible => 2,
        _ => 3
    };
}
