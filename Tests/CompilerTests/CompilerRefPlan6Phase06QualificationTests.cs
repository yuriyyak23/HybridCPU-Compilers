using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Contracts;
using YAKSys_Hybrid_CPU.Core.Registers;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan6Phase06QualificationTests
{
    [Fact]
    public void FinalLanguageMatrix_IsExactConservativeAndDefaultOff()
    {
        ScalarControlFlowV2ProfileContractV1 profile = ScalarControlFlowV2ProfileContractV1.Default;
        string[] guaranteed =
        [
            "backward-branch", "break-continue-multiple-loop-exits", "direct-call-chains-shared-callees",
            "direct-static-managed-calls", "dotnet-publish-hybridcpu", "forward-branch",
            "helpers-with-cfg-loops", "loop-carried-values", "nested-if-else", "nested-reducible-loops",
            "primitive-int32-arguments-returns-void", "reducible-for-while-do-while",
            "scalar-int32-locals", "signed-int32-arithmetic-comparisons",
            "bounded-countdown-recursion-capability"
        ];

        Assert.All(guaranteed, feature => Assert.Contains(profile.Features,
            row => row.Feature == feature && row.Disposition == ScalarControlFlowV2Disposition.Guaranteed));
        Assert.Contains(profile.Features, static row => row.Feature == "unsigned-comparisons" && row.Disposition == ScalarControlFlowV2Disposition.Conditional);
        Assert.Contains(profile.Features, static row => row.Feature == "switch" && row.Disposition == ScalarControlFlowV2Disposition.Rejected);
        Assert.Contains(profile.Features, static row => row.Feature == "recursion" && row.Disposition == ScalarControlFlowV2Disposition.Conditional);
        Assert.Equal([RestrictedCilTypeV1.Void, RestrictedCilTypeV1.Int32, RestrictedCilTypeV1.ObjectReference],
            profile.Scalars.Where(static row => row.Disposition == ScalarControlFlowV2Disposition.Guaranteed)
                .Select(static row => row.CilType).ToArray());
        Assert.False(profile.DefaultEnabled);
        Assert.False(profile.AllowsHostFallback);
        Assert.False(profile.AllowsLlvmFallback);
    }

    [Fact]
    public void CompatibilityFingerprint_MatchesCompilerAndIseStructuralContracts()
    {
        ScalarControlFlowV2CompatibilityFingerprintV1 fingerprint = ScalarControlFlowV2CompatibilityFingerprintV1.Default;

        Assert.Equal(CompilerContract.Version, fingerprint.CompilerContractVersion);
        Assert.Equal(HybridCpuCompilerContract.Version, CompilerContract.Version);
        Assert.Equal(HybridCpuInstructionBundle.SlotCount, SlotId.SlotCount);
        Assert.Equal(HybridCpuTargetMachineContractV1.ArchitecturalRegisterCount, ArchRegId.RegisterCount);
        Assert.Equal(64, HybridCpuTargetMachineContractV1.ArchitecturalRegisterBitWidth);
        Assert.Equal(256, HybridCpuNativeCallControlContractV1.SequentialBundleStrideBytes);
        Assert.Equal(4, HybridCpuNativeCallControlContractV1.LinkIncrementBytes);
        Assert.Equal(252, HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);
        Assert.False(fingerprint.CompilerMetadataHasRuntimeAuthority);
        Assert.Equal(CompilerTypedSlotPolicyMode.CompatibilityValidation, CompilerContract.CurrentTypedSlotPolicy.Mode);
        Assert.Throws<InvalidOperationException>(() => CompilerContract.ThrowIfVersionMismatch(CompilerContract.Version - 1, "phase06-test"));
        Assert.Throws<InvalidOperationException>(() => HybridCpuCompilerContract.ThrowIfVersionMismatch(HybridCpuCompilerContract.Version - 1, "phase06-test"));
    }

    [Fact]
    public void ReleaseContract_AuthorizesOnlyExplicitOptInPreviewAndDefersUnprovedRecursion()
    {
        ScalarControlFlowV2ReleaseContractV1 release = ScalarControlFlowV2ReleaseContractV1.Default;

        Assert.True(release.Validate());
        Assert.Equal("VerifiedOptInPreview", release.Disposition);
        Assert.False(release.DefaultEnabled);
        Assert.False(release.AllowsFallback);
        Assert.False(release.HasRuntimeAuthority);
        Assert.Contains("unproved-or-dynamic-recursion", release.DeferredCapabilities);
        Assert.Contains("default-enablement", release.DeferredCapabilities);
        Assert.Equal(release.DeferredCapabilities.Order(StringComparer.Ordinal), release.DeferredCapabilities);
        Assert.Matches("^[0-9a-f]{64}$", release.ContractDigest);
    }

    [Fact]
    public void MutationAndStaleFactQualificationSurfaces_RemainPresent()
    {
        string root = FindRepositoryRoot();
        string importerContracts = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "Cil", "RestrictedCilContractsV1.cs"));
        string core = string.Join('\n', Directory.EnumerateFiles(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "Core", "IR"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));

        Assert.Contains("ProgramMutation", importerContracts, StringComparison.Ordinal);
        Assert.Contains("EnsureCurrentFor", importerContracts, StringComparison.Ordinal);
        Assert.Contains("generation", importerContracts, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("StaleProof", core, StringComparison.Ordinal);
        Assert.Contains("StaleWitness", core, StringComparison.Ordinal);
        Assert.Contains("StaleFactsRejected", core, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "Compilers", "HybridCPU_Compiler")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
