
namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Represents one directional instruction dependence discovered by the Stage 4 analysis layer.
    /// </summary>
    public sealed record IrInstructionDependency(
        IrInstructionDependencyKind Kind,
        int ProducerInstructionIndex,
        int ConsumerInstructionIndex,
        byte MinimumLatencyCycles,
        IrOperandKind RelatedOperandKind = IrOperandKind.None,
        ulong RelatedOperandValue = 0,
        IrStructuralResource StructuralResources = IrStructuralResource.None,
        IrMemoryDependencyPrecision MemoryPrecision = IrMemoryDependencyPrecision.None,
        IrHazardEffectKind DominantEffectKind = IrHazardEffectKind.RegisterData);
}
