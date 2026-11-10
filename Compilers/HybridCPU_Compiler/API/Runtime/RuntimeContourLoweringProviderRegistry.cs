using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Lowering.Production;

namespace HybridCPU.Compiler.Core.Runtime;

/// <summary>
/// Outward composition root for production providers that consume runtime vocabulary.
/// Core deliberately keeps its default registry empty so the standalone compiler cannot
/// acquire runtime dependencies through provider discovery.
/// </summary>
public static class RuntimeContourLoweringProviderRegistry
{
    public static IContourLoweringProviderRegistry Instance { get; } =
        new DefaultContourLoweringProviderRegistry(
        [
            NativeVliwScalarProductionProvider.Instance,
            NativeVliwLoadStoreProductionProvider.Instance,
            NativeVliwBranchControlProductionProvider.Instance,
            StreamEngineVectorDirectTransferProductionProvider.Instance,
            DmaStreamComputeLane6ProductionProvider.Instance,
            L7SdcLane7ProductionProvider.Instance
        ]);
}
