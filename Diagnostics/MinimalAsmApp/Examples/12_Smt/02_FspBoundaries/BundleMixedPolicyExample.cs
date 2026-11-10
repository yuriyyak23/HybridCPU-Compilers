using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU.Core;

namespace MinimalAsmApp.Examples.Smt;

public sealed class BundleMixedPolicyExample : ICpuExample
{
    public string Name => "fsp-mixed-bundle-policy";

    public string Description => "Builds one bundle with mixed stealable/protected slots and a diagnostics tag.";

    public string Category => "12_Smt/02_FspBoundaries";

    public CpuExampleResult Run()
    {
        SlotMetadata[] slots =
        [
            SlotMetadata.Default,
            SlotMetadata.Default with { PreferredVt = 1 },
            SlotMetadata.NotStealable,
            SlotMetadata.Default with { DonorVtHint = 2 },
            SlotMetadata.NotStealable,
            SlotMetadata.Default,
            SlotMetadata.Default with { PreferredVt = 3 },
            SlotMetadata.Default
        ];

        BundleMetadata bundle = new BundleMetadata
        {
            SlotMetadata = slots,
            DiagnosticsTag = "mixed-fsp-policy"
        };

        int protectedCount = Enumerable.Range(0, BundleMetadata.BundleSlotCount)
            .Count(slot => bundle.GetSlotMetadata(slot).StealabilityPolicy == StealabilityPolicy.NotStealable);
        if (protectedCount != 2 || bundle.FspBoundary)
        {
            throw new InvalidOperationException("Mixed FSP bundle should protect exactly two slots without creating a boundary.");
        }

        return CpuExampleResult.Ok(
            "Expected two protected slots, six stealable slots, and no FSP boundary.",
            notes: SmtExampleDescriber.DescribeBundle(bundle));
    }
}
