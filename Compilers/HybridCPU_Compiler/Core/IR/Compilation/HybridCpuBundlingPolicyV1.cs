using HybridCPU.Compiler.Core.IR.Contracts;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>Immutable structural bundler settings; profile injection is a separate explicit contract.</summary>
public sealed record HybridCpuBundlingPolicyV1(
    bool UseClassFirstBinding,
    bool UseCertificateAwareCoalescingTieBreaks,
    int VirtualThreadId,
    bool TreatAsCoordinatorPath)
{
    public const string SchemaId = "hybridcpu.bundling-policy/v1";
    public const int MaximumWholeProgramPlacementBundles = 96;
    public const int MaximumBlockLookaheadBundles = 64;
    public static HybridCpuBundlingPolicyV1 Default { get; } = new(false, false, -1, false);

    public string Digest => CompilerContractCanonicalJson.Hash(CompilerContractCanonicalJson.Serialize(new
    {
        SchemaId, StructuralMasks = HybridCpuSlotModel.StructuralMaskContract,
        MaximumWholeProgramPlacementBundles, MaximumBlockLookaheadBundles,
        UseClassFirstBinding, UseCertificateAwareCoalescingTieBreaks, VirtualThreadId, TreatAsCoordinatorPath
    }));

    public HybridCpuBundleFormer CreateBundler() => new(UseClassFirstBinding)
    {
        UseCertificateAwareCoalescingTieBreaks = UseCertificateAwareCoalescingTieBreaks,
        VirtualThreadId = VirtualThreadId,
        TreatAsCoordinatorPath = TreatAsCoordinatorPath
    };
}

public sealed record HybridCpuCompilationPolicyV1(
    HybridCpuSchedulingPolicyV1 Scheduling, HybridCpuBundlingPolicyV1 Bundling)
{
    public const string SchemaId = "hybridcpu.compilation-policy/v1";
    public static HybridCpuCompilationPolicyV1 Default { get; } = new(
        HybridCpuSchedulingPolicyV1.Default, HybridCpuBundlingPolicyV1.Default);
    public string Digest => CompilerContractCanonicalJson.Hash(CompilerContractCanonicalJson.Serialize(new
    {
        SchemaId, Scheduling = Scheduling.Digest, Bundling = Bundling.Digest
    }));
}
