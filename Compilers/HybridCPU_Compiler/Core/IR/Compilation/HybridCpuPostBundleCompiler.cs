namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Shared mechanical backend tail over the caller's final bundle placement.
/// Scheduling, allocation, agreement and object/image ownership remain with callers.
/// </summary>
public static class HybridCpuPostBundleCompiler
{
    public static IReadOnlyList<HybridCpuInstructionBundle> Lower(
        IrProgramBundlingResult bundleLayout, bool resolveControlFlowTargets)
        => Lower(CancellationToken.None, bundleLayout, resolveControlFlowTargets);

    public static IReadOnlyList<HybridCpuInstructionBundle> Lower(
        CancellationToken cancellationToken, IrProgramBundlingResult bundleLayout, bool resolveControlFlowTargets)
    {
        IReadOnlyList<HybridCpuInstructionBundle> lowered = new HybridCpuBundleLowerer().LowerProgram(cancellationToken, bundleLayout);
        IReadOnlyList<HybridCpuInstructionBundle> result = resolveControlFlowTargets
            ? HybridCpuControlFlowRelocationResolver.ApplyRelocations(cancellationToken, bundleLayout, lowered)
            : lowered;
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public static byte[] Serialize(IReadOnlyList<HybridCpuInstructionBundle> loweredBundles) =>
        Serialize(CancellationToken.None, loweredBundles);

    public static byte[] Serialize(CancellationToken cancellationToken, IReadOnlyList<HybridCpuInstructionBundle> loweredBundles) =>
        new HybridCpuBundleSerializer().SerializeProgram(cancellationToken, loweredBundles);

    public static (IReadOnlyList<HybridCpuInstructionBundle> LoweredBundles,
        IReadOnlyList<IrBundleAnnotations> LoweredBundleAnnotations, byte[] ProgramImage) Compile(
        IrProgramBundlingResult bundleLayout,
        bool resolveControlFlowTargets,
        bool emitAnnotations,
        Action<string, string>? progressObserver = null)
        => Compile(CancellationToken.None, bundleLayout, resolveControlFlowTargets, emitAnnotations, progressObserver);

    public static (IReadOnlyList<HybridCpuInstructionBundle> LoweredBundles,
        IReadOnlyList<IrBundleAnnotations> LoweredBundleAnnotations, byte[] ProgramImage) Compile(
        CancellationToken cancellationToken, IrProgramBundlingResult bundleLayout,
        bool resolveControlFlowTargets, bool emitAnnotations,
        Action<string, string>? progressObserver = null)
    {
        HybridCpuBundleLowerer lowerer = new HybridCpuBundleLowerer();
        progressObserver?.Invoke("LoweringStarting", "Lowering IR bundles to backend VLIW bundles.");
        IReadOnlyList<HybridCpuInstructionBundle> loweredBundles = Lower(cancellationToken, bundleLayout, resolveControlFlowTargets);
        IReadOnlyList<IrBundleAnnotations> annotations = emitAnnotations
            ? lowerer.EmitAnnotationsForProgram(cancellationToken, bundleLayout)
            : Array.Empty<IrBundleAnnotations>();
        progressObserver?.Invoke("Lowering", $"Lowered {loweredBundles.Count} backend bundle(s).");
        cancellationToken.ThrowIfCancellationRequested();

        progressObserver?.Invoke("SerializationStarting", "Serializing backend bundles into a fetch-ready image.");
        byte[] image = Serialize(cancellationToken, loweredBundles);
        progressObserver?.Invoke("Serialization", $"Serialized {image.Length} byte(s) of bundle image.");
        cancellationToken.ThrowIfCancellationRequested();
        return (loweredBundles, annotations, image);
    }
}
