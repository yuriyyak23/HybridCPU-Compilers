namespace HybridCPU.Compiler.Core.IR;

/// <summary>Compiler-owned structural facts for one exact W=8 bundle.</summary>
public readonly record struct IrTypedSlotBundleFacts
{
    public IrSlotClass Slot0Class { get; init; }
    public IrSlotClass Slot1Class { get; init; }
    public IrSlotClass Slot2Class { get; init; }
    public IrSlotClass Slot3Class { get; init; }
    public IrSlotClass Slot4Class { get; init; }
    public IrSlotClass Slot5Class { get; init; }
    public IrSlotClass Slot6Class { get; init; }
    public IrSlotClass Slot7Class { get; init; }
    public byte PinningKindMask { get; init; }
    public int FlexibleOpCount { get; init; }
    public int PinnedOpCount { get; init; }
    public byte AluCount { get; init; }
    public byte LsuCount { get; init; }
    public byte DmaStreamCount { get; init; }
    public byte MatrixTileStreamCount { get; init; }
    public byte BranchControlCount { get; init; }
    public byte SystemSingletonCount { get; init; }

    public bool IsEmpty => FlexibleOpCount == 0 && PinnedOpCount == 0 &&
        AluCount == 0 && LsuCount == 0 && DmaStreamCount == 0 &&
        MatrixTileStreamCount == 0 && BranchControlCount == 0 && SystemSingletonCount == 0;

    public IrSlotClass GetSlotClass(int index) => index switch
    {
        0 => Slot0Class,
        1 => Slot1Class,
        2 => Slot2Class,
        3 => Slot3Class,
        4 => Slot4Class,
        5 => Slot5Class,
        6 => Slot6Class,
        7 => Slot7Class,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "Slot index must be 0-7.")
    };

    public bool IsSlotPinned(int index) =>
        index is >= 0 and < 8
            ? (PinningKindMask & (1 << index)) != 0
            : throw new ArgumentOutOfRangeException(nameof(index), index, "Slot index must be 0-7.");
}
