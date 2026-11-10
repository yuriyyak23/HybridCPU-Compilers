namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Authority-free, compiler-owned projection of an already validated external descriptor.
/// It carries exact immutable footprint evidence only; it is not submission, completion,
/// capability, ownership, or runtime-legality authority.
/// </summary>
public sealed record IrExternalDescriptorEvidenceV1(
    string SchemaId,
    ulong DescriptorIdentityHash,
    bool AllOrNone,
    IReadOnlyList<IrMemoryRegion> ReadRegions,
    IReadOnlyList<IrMemoryRegion> WriteRegions);
