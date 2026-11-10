using HybridCPU.Compiler.Core.Target.Managed;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase25CManagedFeatureSetTests
{
    private static readonly HybridCpuManagedFeatureSetV1 Features = HybridCpuManagedFeatureSetV1.Default;

    [Fact]
    public void IndependentWorkstreamsHaveUniqueSchemasAndDigests()
    {
        Assert.Equal("hybridcpu.managed-feature-set/v1", HybridCpuManagedFeatureSetV1.SchemaId);
        Assert.Equal(20, Features.Workstreams.Count);
        Assert.Equal(20, Features.Workstreams.Select(static item => item.Identity).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(20, Features.Workstreams.Select(static item => item.SchemaId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Features.Workstreams, static item => Assert.Equal(64, item.Digest.Length));
        Assert.Equal(64, Features.ContractDigest.Length);
    }

    [Fact]
    public void OnlyProvenComponentWorkstreamsAreQualifiedDefaultOff()
    {
        string[] qualified = Features.Workstreams
            .Where(static item => item.Support == HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff)
            .Select(static item => item.Identity).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["async-runtime-libraries", "blittable-value-boxing", "bounded-public-reflection", "delegates-function-pointers", "deterministic-allocation",
            "eh-unwind", "exact-aot-generics", "fault-trap-integration", "gc-maps-safepoints", "managed-unmanaged-interop", "metadata-runtime-lookup",
            "static-type-initialization", "synchronization-memory-model", "szarray-core", "tls-thread-runtime-state", "type-layout-object-references", "utf16-string-literals",
            "virtual-interface-dispatch"], qualified);
        Assert.All(Features.Workstreams.Where(static item => item.Support ==
            HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff), static item => Assert.True(item.RuntimeConsumerRequired));
    }

    [Theory]
    [InlineData("read-write-barriers")]
    [InlineData("debug-source-mapping")]
    public void UnfinishedWorkstreamsAreExplicitlyUnsupported(string identity)
    {
        HybridCpuManagedWorkstreamV1 row = Features.Workstreams.Single(item => item.Identity == identity);

        Assert.Equal(HybridCpuManagedWorkstreamSupportV1.Unsupported, row.Support);
        Assert.NotEmpty(row.Scope);
        Assert.NotEmpty(row.Reason);
    }

    [Fact]
    public void RequirementCheckAcceptsOnlyExactQualifiedSetAndIsOrderDeterministic()
    {
        HybridCpuManagedFeatureRequirementResultV1 first = Features.CheckRequirements(
            ["metadata-runtime-lookup", "gc-maps-safepoints"]);
        HybridCpuManagedFeatureRequirementResultV1 second = Features.CheckRequirements(
            ["gc-maps-safepoints", "metadata-runtime-lookup"]);
        HybridCpuManagedFeatureRequirementResultV1 unsupported = Features.CheckRequirements(
            ["gc-maps-safepoints", "eh-unwind", "unknown"]);

        Assert.True(first.Supported);
        Assert.Equal(first.Digest, second.Digest);
        Assert.False(unsupported.Supported);
        Assert.Equal(["unknown"], unsupported.MissingOrUnsupported);
    }

    [Fact]
    public void ManagedSafetyLevelExcludesFspAndVdsaAndCarriesNoAuthority()
    {
        Assert.Equal(HybridCpuManagedSafetyLevelV1.BasicBlockObjectReferences, Features.ManagedSafetyLevel);
        Assert.False(Features.FspAllowed);
        Assert.False(Features.VdsaAllowed);
        Assert.False(Features.RuntimeAuthority);
        Assert.False(Features.PublicationAuthority);
        Assert.DoesNotContain(Features.Workstreams, static item =>
            item.Identity.Contains("umbrella", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MalformedRequirementFailsClosed()
    {
        HybridCpuManagedFeatureRequirementResultV1 result = Features.CheckRequirements([" "]);

        Assert.False(result.Supported);
        Assert.Single(result.MissingOrUnsupported);
    }
}
