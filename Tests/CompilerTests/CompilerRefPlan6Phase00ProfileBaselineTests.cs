using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;
using System.Text.Json;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan6Phase00ProfileBaselineTests
{
    [Fact]
    public void Profile_IsVersionedDeterministicDefaultOffAndNonAuthoritative()
    {
        ScalarControlFlowV2ProfileContractV1 profile = ScalarControlFlowV2ProfileContractV1.Default;

        Assert.Equal("HybridCPU.DotNetAot.ScalarControlFlowV2", ScalarControlFlowV2ProfileContractV1.ProfileId);
        Assert.Equal("hybridcpu.dotnetaot.scalar-control-flow-v2/v1", ScalarControlFlowV2ProfileContractV1.SchemaId);
        Assert.Equal(64, profile.ContractDigest.Length);
        Assert.Equal(profile.ContractDigest, ScalarControlFlowV2ProfileContractV1.Default.ContractDigest);
        Assert.False(profile.DefaultEnabled);
        Assert.False(profile.AllowsHostFallback);
        Assert.False(profile.AllowsLlvmFallback);
        Assert.False(profile.HasRuntimeAuthority);
    }

    [Fact]
    public void ScalarMatrix_SeparatesGuaranteedConditionalMissingAndRejected()
    {
        IReadOnlyList<ScalarControlFlowV2ScalarRow> rows = ScalarControlFlowV2ProfileContractV1.Default.Scalars;

        Assert.Equal(15, rows.Count);
        Assert.Equal(rows.OrderBy(static row => row.CilType), rows);
        Assert.Contains(rows, static row => row.CilType == RestrictedCilTypeV1.Int32 && row.Disposition == ScalarControlFlowV2Disposition.Guaranteed);
        Assert.Contains(rows, static row => row.CilType == RestrictedCilTypeV1.ObjectReference && row.Disposition == ScalarControlFlowV2Disposition.Guaranteed);
        Assert.Contains(rows, static row => row.CilType == RestrictedCilTypeV1.Boolean && row.Disposition == ScalarControlFlowV2Disposition.Conditional && row.EvaluationStackBits == 32);
        Assert.Contains(rows, static row => row.CilType == RestrictedCilTypeV1.NativeInt && row.Disposition == ScalarControlFlowV2Disposition.Missing);
        Assert.Contains(rows, static row => row.CilType == RestrictedCilTypeV1.UnsupportedManaged && row.Disposition == ScalarControlFlowV2Disposition.Rejected);
        Assert.Contains(rows, static row => row.CilType == RestrictedCilTypeV1.ManagedByRef && row.Disposition == ScalarControlFlowV2Disposition.Rejected);
        Assert.True(ScalarControlFlowV2ProfileContractV1.Default.IsGuaranteedScalar(RestrictedCilTypeV1.Int32));
        Assert.False(ScalarControlFlowV2ProfileContractV1.Default.IsGuaranteedScalar(RestrictedCilTypeV1.Int64));
    }

    [Fact]
    public void FeatureMatrix_SeparatesImplementedConditionalAndRejectedCapabilities()
    {
        IReadOnlyList<ScalarControlFlowV2FeatureRow> rows = ScalarControlFlowV2ProfileContractV1.Default.Features;

        Assert.Contains(rows, static row => row.Feature == "forward-branch" && row.Disposition == ScalarControlFlowV2Disposition.Guaranteed);
        Assert.Contains(rows, static row => row.Feature == "backward-branch" && row.Disposition == ScalarControlFlowV2Disposition.Guaranteed);
        Assert.Contains(rows, static row => row.Feature == "reducible-for-while-do-while" && row.Disposition == ScalarControlFlowV2Disposition.Guaranteed);
        Assert.Contains(rows, static row => row.Feature == "direct-static-managed-calls" && row.Disposition == ScalarControlFlowV2Disposition.Guaranteed);
        Assert.Contains(rows, static row => row.Feature == "multi-method-symbol-relocation-link" && row.Disposition == ScalarControlFlowV2Disposition.Guaranteed);
        Assert.Contains(rows, static row => row.Feature == "recursion" && row.Disposition == ScalarControlFlowV2Disposition.Conditional);
        Assert.Contains(rows, static row => row.Feature == "bounded-countdown-recursion-capability" && row.Disposition == ScalarControlFlowV2Disposition.Guaranteed);
        Assert.Contains(rows, static row => row.Feature == "host-jit-native-llvm-fallback" && row.Disposition == ScalarControlFlowV2Disposition.Rejected);
    }

    [Fact]
    public void Budgets_AreDeterministicAndBoundEveryRequiredDimension()
    {
        ScalarControlFlowV2Budgets budgets = ScalarControlFlowV2ProfileContractV1.Default.Budgets;

        Assert.True(budgets.IsValid);
        Assert.Equal(16384, budgets.MaximumIlInstructionsPerMethod);
        Assert.Equal(1024, budgets.MaximumBasicBlocksPerMethod);
        Assert.Equal(4096, budgets.MaximumCfgEdgesPerMethod);
        Assert.Equal(4096, budgets.MaximumPhiValuesPerMethod);
        Assert.Equal(32, budgets.MaximumLoopNestingDepth);
        Assert.Equal(4096, budgets.MaximumReachableMethods);
        Assert.Equal(64, budgets.MaximumAcyclicCallDepth);
        Assert.Equal(1024 * 1024, budgets.MaximumFrameBytes);
        Assert.Equal(HybridCpuObjectFormatContractV1.MaximumSymbols, budgets.MaximumSymbols);
        Assert.Equal(HybridCpuObjectFormatContractV1.MaximumRelocations, budgets.MaximumRelocations);
        Assert.Equal(HybridCpuStaticLinkOptionsV1.Production.MaximumImageBytes, budgets.MaximumImageBytes);
    }

    [Fact]
    public void Diagnostics_AreDistinctStableAndFailBeforeArtifacts()
    {
        IReadOnlyList<ScalarControlFlowV2DiagnosticFamily> rows = ScalarControlFlowV2ProfileContractV1.Default.Diagnostics;

        Assert.Equal(rows.Count, rows.Select(static row => row.Family).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(rows.Count, rows.Select(static row => row.CodePrefix).Distinct(StringComparer.Ordinal).Count());
        Assert.All(rows, static row =>
        {
            Assert.StartsWith("HCSCF-", row.CodePrefix, StringComparison.Ordinal);
            Assert.Equal("method-identity,cil-offset,code", row.StableOrderingKey);
            Assert.Contains("no HCO or HCEXE", row.NoArtifactRule, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Assumptions_BlockOnlyTheirDeclaredDownstreamGates()
    {
        ScalarControlFlowV2ProfileContractV1 profile = ScalarControlFlowV2ProfileContractV1.Default;

        Assert.False(profile.HasPhase1Blocker);
        Assert.DoesNotContain(profile.Assumptions, static row => row.ConsumingPhase <= 1 && row.Status != ScalarControlFlowV2AssumptionStatus.Verified);
        Assert.Contains(profile.Assumptions, static row => row.Identity == "nested-call-execution" && row.Status == ScalarControlFlowV2AssumptionStatus.Verified && row.ConsumingPhase == 3 && !row.BlocksConsumingPhase);
        Assert.Contains(profile.Assumptions, static row => row.Identity == "direct-call-relocation" && row.Status == ScalarControlFlowV2AssumptionStatus.Verified && row.ConsumingPhase == 4 && !row.BlocksConsumingPhase);
        Assert.Contains(profile.Assumptions, static row => row.Identity == "compiler-ise-fingerprint" && row.Status == ScalarControlFlowV2AssumptionStatus.Verified && row.ConsumingPhase == 6 && !row.BlocksConsumingPhase);
    }

    [Fact]
    public void FrozenContracts_MatchExistingAuthoritativeSources()
    {
        Assert.Equal(HybridCpuTargetMachineContractV1.TargetTriple, ScalarControlFlowV2ProfileContractV1.TargetTriple);
        Assert.Equal(64, ScalarControlFlowV2ProfileContractV1.AddressWidthBits);
        Assert.Equal("hybridcpu.native-abi", HybridCpuNativeAbiContractV2.SchemaId);
        Assert.Equal(2, HybridCpuNativeAbiContractV2.SchemaMajor);
        Assert.Equal([10, 11, 12, 13, 14, 15, 16, 17], HybridCpuNativeAbiContractV2.Default.ArgumentRegisters);
        Assert.Equal(2, HybridCpuNativeAbiContractV2.StackPointerRegister);
        Assert.Equal(1, HybridCpuNativeAbiContractV2.ReturnAddressRegister);
        Assert.Equal(16, HybridCpuNativeAbiContractV2.StackAlignmentBytes);
        Assert.Equal("hybridcpu.object/hco-v1", HybridCpuObjectFormatContractV1.SchemaId);
        Assert.Equal("hybridcpu.static-link/v1", HybridCpuStaticLinkOptionsV1.Production.SchemaId);
        Assert.Equal("hybridcpu.restricted-startup/v1", HybridCpuRestrictedStartupOptionsV1.Production.SchemaId);
    }

    [Fact]
    public void ProjectGraph_KeepsScalarControlFlowPathFreeOfLlvmAndRuntimeAuthority()
    {
        string root = FindRepositoryRoot();
        string core = Read(root, "Compilers", "HybridCPU_Compiler", "Core", "HybridCPU.Compiler.Core.csproj");
        string cil = Read(root, "Compilers", "HybridCPU_Compiler", "Cil", "HybridCPU.Compiler.Cil.csproj");
        string native = Read(root, "Compilers", "HybridCPU_Compiler", "Native", "HybridCPU.Compiler.Native.csproj");
        string adapter = Read(root, "Compilers", "HybridCPU_Compiler", "NativeAot", "HybridCPU.Compiler.NativeAot.Adapter.csproj");
        string llvm = Read(root, "Compilers", "HybridCPU_Compiler", "LLVM", "HybridCPU.Compiler.Llvm.csproj");

        Assert.Contains("HybridCPU_Platform.Contracts\\HybridCPU.Platform.Contracts.csproj", core, StringComparison.Ordinal);
        Assert.Equal(1, System.Xml.Linq.XDocument.Parse(core).Descendants("ProjectReference").Count());
        Assert.Contains("..\\Core\\HybridCPU.Compiler.Core.csproj", cil, StringComparison.Ordinal);
        Assert.Contains("..\\Core\\HybridCPU.Compiler.Core.csproj", native, StringComparison.Ordinal);
        Assert.Contains("..\\Cil\\HybridCPU.Compiler.Cil.csproj", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("LLVM", core + cil + native + adapter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LLVMSharp", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("HybridCPU_ISE", core + cil + native + adapter, StringComparison.OrdinalIgnoreCase);
    }



    private static string Read(string root, params string[] path) => File.ReadAllText(Path.Combine([root, .. path]));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "Compilers", "HybridCPU_Compiler")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
