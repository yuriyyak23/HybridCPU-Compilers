
namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Maps compiler-side <see cref="IrResourceClass"/> to ISE-side <see cref="IrSlotClass"/>.
/// Single source of truth for the cross-boundary vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// This mapper supplies resource-class metadata; it does not relocate encoded
/// instructions or grant runtime admission. Structural masks and final placement
/// must independently agree with the target topology.
/// </para>
/// <para>
/// Resource mappings:
/// <list type="bullet">
/// <item><c>VectorAlu</c> → <c>AluClass</c>; legacy vector masks require their own contour checks.</item>
/// <item><c>LoadStore</c> → <c>LsuClass</c>, with structural lanes 4–5.</item>
/// </list>
/// </para>
/// </remarks>
public static class IrSlotClassMapping
{
    /// <summary>
    /// Maps compiler resource class to ISE slot class.
    /// </summary>
    public static IrSlotClass ToSlotClass(IrResourceClass resourceClass) => resourceClass switch
    {
        IrResourceClass.ScalarAlu => IrSlotClass.AluClass,
        IrResourceClass.VectorAlu => IrSlotClass.AluClass,
        IrResourceClass.LoadStore => IrSlotClass.LsuClass,
        IrResourceClass.ControlFlow => IrSlotClass.BranchControl,
        IrResourceClass.System => IrSlotClass.SystemSingleton,
        IrResourceClass.DmaStream => IrSlotClass.DmaStreamClass,
        IrResourceClass.Unknown => IrSlotClass.Unclassified,
        _ => IrSlotClass.Unclassified
    };

    /// <summary>
    /// Derives compiler-side binding kind from resource class and serialization.
    /// </summary>
    public static IrSlotBindingKind DerivePinningKind(
        IrResourceClass resourceClass,
        IrSerializationKind serialization) => resourceClass switch
        {
            IrResourceClass.ControlFlow => IrSlotBindingKind.HardPinned,
            IrResourceClass.System => IrSlotBindingKind.HardPinned,
            IrResourceClass.DmaStream => IrSlotBindingKind.SingletonClass,
            _ => (serialization & IrSerializationKind.ExclusiveCycle) != 0
                ? IrSlotBindingKind.HardPinned
                : IrSlotBindingKind.ClassFlexible
        };

}
