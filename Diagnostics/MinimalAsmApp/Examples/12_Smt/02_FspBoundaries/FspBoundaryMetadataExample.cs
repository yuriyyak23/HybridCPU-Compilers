using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU.Core;

namespace MinimalAsmApp.Examples.Smt;

public sealed class FspBoundaryMetadataExample : ICpuExample
{
    public string Name => "fsp-boundary-metadata";

    public string Description => "Creates a bundle-level FSP boundary that replaces the old FSP_FENCE pseudo-op.";

    public string Category => "12_Smt/02_FspBoundaries";

    public CpuExampleResult Run()
    {
        BundleMetadata bundle = BundleMetadata.CreateFspFence();

        bool allProtected = Enumerable.Range(0, BundleMetadata.BundleSlotCount)
            .All(slot => bundle.GetSlotMetadata(slot).StealabilityPolicy == StealabilityPolicy.NotStealable);
        if (!bundle.FspBoundary || !allProtected)
        {
            throw new InvalidOperationException("FSP fence bundle must set boundary=true and protect every slot.");
        }

        return CpuExampleResult.Ok(
            "Expected FSP boundary=true and all slots NotStealable.",
            notes: SmtExampleDescriber.DescribeBundle(bundle));
    }
}
