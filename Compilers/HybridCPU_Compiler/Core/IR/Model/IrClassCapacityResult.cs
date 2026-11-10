using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Result of class-level capacity analysis for a candidate instruction group.
/// Each pair (XxxCount, XxxCapacity) describes how many instructions of that class
/// are present vs. the maximum number of lanes available.
/// </summary>
public sealed record IrClassCapacityResult(
    int AluCount, int AluCapacity,
    int LsuCount, int LsuCapacity,
    int DmaStreamCount, int DmaStreamCapacity,
    int BranchControlCount, int BranchControlCapacity,
    int SystemSingletonCount, int SystemSingletonCapacity,
    bool HasAliasedLaneConflict,
    bool IsWithinCapacity)
{
    /// <summary>
    /// Returns the slot classes whose instruction count exceeds capacity.
    /// </summary>
    public IReadOnlyList<IrSlotClass> GetOvercommittedClasses()
    {
        List<IrSlotClass> result = new List<IrSlotClass>();
        if (AluCount > AluCapacity) result.Add(IrSlotClass.AluClass);
        if (LsuCount > LsuCapacity) result.Add(IrSlotClass.LsuClass);
        if (DmaStreamCount > DmaStreamCapacity) result.Add(IrSlotClass.DmaStreamClass);
        if (BranchControlCount > BranchControlCapacity) result.Add(IrSlotClass.BranchControl);
        if (SystemSingletonCount > SystemSingletonCapacity) result.Add(IrSlotClass.SystemSingleton);
        return result;
    }
}
