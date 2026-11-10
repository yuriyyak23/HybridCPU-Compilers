namespace HybridCPU.Compiler.Core.IR;

public readonly record struct IrInstructionSlotMetadata(
    byte VirtualThreadId,
    IrSlotClass RequiredSlotClass,
    IrSlotBindingKind BindingKind,
    byte PinnedLaneId,
    ulong DomainTag,
    bool Stealable)
{
    public static IrInstructionSlotMetadata Default { get; } = new(
        0, IrSlotClass.Unclassified, IrSlotBindingKind.ClassFlexible, 0, 0, false);

    public object? DmaStreamComputeDescriptor { get; init; }
    public object? AcceleratorCommandDescriptor { get; init; }
    public IrExternalDescriptorEvidenceV1? ExternalDescriptorEvidence { get; init; }
    public object? MatrixTileNumericPolicy { get; init; }
    public object? MatrixTileLayoutPolicy { get; init; }
}

public sealed class IrBundleAnnotations
{
    private readonly IrInstructionSlotMetadata[] _slots;

    public static IrBundleAnnotations Empty { get; } = new([]);

    public IrBundleAnnotations(IrInstructionSlotMetadata[] slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        _slots = (IrInstructionSlotMetadata[])slots.Clone();
    }

    public int Count => _slots.Length;

    public bool TryGetInstructionSlotMetadata(int instructionIndex, out IrInstructionSlotMetadata metadata)
    {
        if ((uint)instructionIndex < (uint)_slots.Length)
        {
            metadata = _slots[instructionIndex];
            return true;
        }
        metadata = default;
        return false;
    }
}
