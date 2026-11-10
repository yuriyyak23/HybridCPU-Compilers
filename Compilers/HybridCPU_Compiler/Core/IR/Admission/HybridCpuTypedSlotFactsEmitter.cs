using HybridCPU.Compiler.Core.IR.Resources;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Emits <see cref="IrTypedSlotBundleFacts"/> from a compiler-side materialized bundle.
/// Uses <see cref="IrInstructionAnnotation.RequiredSlotClass"/> (ISE vocabulary)
/// and compiler-owned binding metadata for coordinate translation.
/// Compiler preflight emits facts eagerly even though the current runtime staging
/// surface still reports <see cref="TypedSlotFactMode.ValidationOnly"/>.
/// </summary>
/// <remarks>
/// <c>DmaStreamClass</c> instructions are reported as hard-pinned lane 6 in the
/// runtime-facing facts. The compiler keeps <see cref="IrSlotBindingKind.SingletonClass"/>
/// as its structural binding kind, while the ISE lane-6 contour requires the
/// corresponding hard-pinned handoff metadata for parity.
/// </remarks>
public static class HybridCpuTypedSlotFactsEmitter
{
    /// <summary>
    /// Emit typed-slot facts from a materialized bundle.
    /// Works on both legacy and class-first bundler paths.
    /// </summary>
    public static IrTypedSlotBundleFacts EmitFacts(IrMaterializedBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        Span<IrSlotClass> slotClasses = stackalloc IrSlotClass[8];
        byte pinningMask = 0;
        int flexibleCount = 0;
        int pinnedCount = 0;
        byte aluCount = 0, lsuCount = 0, dmaCount = 0, matrixCount = 0, branchCount = 0, sysCount = 0;

        foreach (IrMaterializedBundleSlot slot in bundle.Slots)
        {
            int i = slot.SlotIndex;

            if (slot.Instruction is not { } instruction)
            {
                slotClasses[i] = IrSlotClass.Unclassified;
                continue;
            }

            IrSlotClass slotClass = instruction.Annotation.RequiredSlotClass;
            slotClasses[i] = slotClass;

            bool isPinned = instruction.Annotation.RequiredSlotClass is IrSlotClass.DmaStreamClass or IrSlotClass.MatrixTileStreamClass ||
                instruction.Annotation.BindingKind == IrSlotBindingKind.HardPinned;
            if (isPinned)
            {
                pinningMask |= (byte)(1 << i);
                pinnedCount++;
            }
            else
            {
                flexibleCount++;
            }

            switch (slotClass)
            {
                case IrSlotClass.AluClass: aluCount++; break;
                case IrSlotClass.LsuClass: lsuCount++; break;
                case IrSlotClass.DmaStreamClass: dmaCount++; break;
                case IrSlotClass.MatrixTileStreamClass: matrixCount++; break;
                case IrSlotClass.BranchControl: branchCount++; break;
                case IrSlotClass.SystemSingleton: sysCount++; break;
            }
        }

        return new IrTypedSlotBundleFacts
        {
            Slot0Class = slotClasses[0],
            Slot1Class = slotClasses[1],
            Slot2Class = slotClasses[2],
            Slot3Class = slotClasses[3],
            Slot4Class = slotClasses[4],
            Slot5Class = slotClasses[5],
            Slot6Class = slotClasses[6],
            Slot7Class = slotClasses[7],
            PinningKindMask = pinningMask,
            FlexibleOpCount = flexibleCount,
            PinnedOpCount = pinnedCount,
            AluCount = aluCount,
            LsuCount = lsuCount,
            DmaStreamCount = dmaCount,
            MatrixTileStreamCount = matrixCount,
            BranchControlCount = branchCount,
            SystemSingletonCount = sysCount
        };
    }

    /// <summary>
    /// Compiler-side validation of emitted facts (subset of ISE checks).
    /// Does not require <c>MicroOp[]</c> — validates internal consistency only.
    /// Under the current <see cref="TypedSlotFactMode.ValidationOnly"/> staging,
    /// empty facts still remain acceptable for canonical runtime execution.
    /// </summary>
    public static bool ValidateEmittedFacts(IrTypedSlotBundleFacts facts)
    {
        // Current ValidationOnly mainline keeps missing facts as an allowed state.
        if (facts.IsEmpty)
            return true;

        // Check 1: per-class counts within capacity bounds
        HybridCpuMachineDescriptionV1 machine = HybridCpuMachineDescriptionV1.Default;
        if (facts.AluCount > machine.GetSlotClassCapacity(IrSlotClass.AluClass)
            || facts.LsuCount > machine.GetSlotClassCapacity(IrSlotClass.LsuClass)
            || facts.DmaStreamCount > machine.GetSlotClassCapacity(IrSlotClass.DmaStreamClass)
            || facts.MatrixTileStreamCount > machine.GetSlotClassCapacity(IrSlotClass.MatrixTileStreamClass)
            || facts.BranchControlCount > machine.GetSlotClassCapacity(IrSlotClass.BranchControl)
            || facts.SystemSingletonCount > machine.GetSlotClassCapacity(IrSlotClass.SystemSingleton))
        {
            return false;
        }

        // Check 2: total ops within bundle width
        if (facts.PinnedOpCount + facts.FlexibleOpCount > 8)
            return false;

        // Check 3: aliased-lane constraint — BranchControl and SystemSingleton share lane 7
        if (facts.BranchControlCount > 0 && facts.SystemSingletonCount > 0 && machine.HasAliasedLanes(IrSlotClass.BranchControl))
            return false;

        // Check 4: DMA and MatrixTile transport share one physical lane.
        if (facts.DmaStreamCount + facts.MatrixTileStreamCount > machine.GetSlotClassCapacity(IrSlotClass.DmaStreamClass))
            return false;

        // Check 5: class count totals match pinned + flexible totals
        int classTotal = facts.AluCount + facts.LsuCount + facts.DmaStreamCount + facts.MatrixTileStreamCount
                         + facts.BranchControlCount + facts.SystemSingletonCount;
        if (classTotal != facts.PinnedOpCount + facts.FlexibleOpCount)
            return false;

        return true;
    }
}
