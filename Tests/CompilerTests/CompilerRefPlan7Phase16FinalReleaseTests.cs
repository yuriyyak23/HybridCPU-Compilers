using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.NativeAot;
using HybridCPU.Compiler.Release;
using HybridCPU_ISE.Tests.TestHelpers;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase16FinalReleaseTests
{
    private const string Commit = "96a5a8b2907e36c1d5638fc00eb5994defde5542";

    [Fact]
    public void Manifest_BindsExactComponentsAbisEvidenceFeaturesAndPackDeterministically()
    {
        HybridCpuRefPlan7FinalReleaseManifestV1 first = Manifest();
        HybridCpuRefPlan7FinalReleaseManifestV1 second = Manifest();
        Assert.True(HybridCpuRefPlan7FinalReleaseV1.Validate(first).IsValid);
        Assert.Equal(first.DeterministicDigest, second.DeterministicDigest);
        Assert.Equal(HybridCpuRefPlan7FinalReleaseV1.Serialize(first),
            HybridCpuRefPlan7FinalReleaseV1.Serialize(second));
        Assert.Equal(Commit, first.Components.CompilerSha);
        Assert.Equal(Commit, first.Components.ManagedRuntimeSha);
        Assert.Equal(Commit, first.Components.RuntimeKernelSha);
        Assert.Equal(Commit, first.Components.IseSha);
        Assert.Equal(16, first.EvidenceArtifacts.Count);
        Assert.Matches("^[0-9a-f]{64}$", first.RuntimePackDigest);
    }

    [Fact]
    public void MissingIncompleteOrTamperedEvidence_FailsClosed()
    {
        HybridCpuRefPlan7FinalReleaseManifestV1 manifest = Manifest();
        Assert.Equal("HCR7L1011", HybridCpuRefPlan7FinalReleaseV1.Validate(
            manifest with { EvidenceArtifacts = manifest.EvidenceArtifacts.Skip(1).ToArray() }).Code);
        HybridCpuRefPlan7EvidenceArtifactV1[] incomplete = manifest.EvidenceArtifacts.ToArray();
        incomplete[15] = incomplete[15] with { Disposition = "Eligible" };
        Assert.Equal("HCR7L1011", HybridCpuRefPlan7FinalReleaseV1.Validate(
            manifest with { EvidenceArtifacts = incomplete }).Code);
    }

    [Fact]
    public void CleanBuildImageMismatchAndDigestTamper_FailClosed()
    {
        HybridCpuRefPlan7FinalReleaseManifestV1 manifest = Manifest();
        Assert.Equal("HCR7L1005", HybridCpuRefPlan7FinalReleaseV1.Validate(manifest with
        {
            ImageQualification = manifest.ImageQualification with { BuildBSha256 = new string('f', 64) }
        }).Code);
        Assert.Equal("HCR7L1010", HybridCpuRefPlan7FinalReleaseV1.Validate(
            manifest with { DeterministicDigest = new string('0', 64) }).Code);
    }

    [Fact]
    public void ManagedFeatureBits_AreEvidenceQualifiedButNoneAreDefaultEnabled()
    {
        HybridCpuRefPlan7FinalReleaseManifestV1 manifest = Manifest();
        string[] expected = HybridCpuManagedFeatureSetV1.Default.Workstreams
            .Where(static row => row.Support == HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff)
            .Select(static row => row.Identity).Order(StringComparer.Ordinal).ToArray();
        Assert.Empty(manifest.EnabledFeatureBits);
        Assert.Equal(expected, manifest.QualifiedDefaultOffFeatureBits);
        Assert.DoesNotContain("read-write-barriers", manifest.QualifiedDefaultOffFeatureBits);
        Assert.DoesNotContain("debug-source-mapping", manifest.QualifiedDefaultOffFeatureBits);
    }

    [Fact]
    public void RuntimeDeterminismCategories_KeepExternalInputsExplicit()
    {
        HybridCpuRefPlan7FinalReleaseManifestV1 manifest = Manifest();
        Assert.Equal([
            "deterministic-compilation", "deterministic-image-bytes", "deterministic-single-context-runtime",
            "deterministic-cooperative-scheduler", "external-input-scheduling-event-replay"
        ], manifest.RuntimeDeterminism.Select(static row => row.Category));
        Assert.Equal(HybridCpuRuntimeDeterminismDispositionV1.DeterministicWhenReplayed,
            manifest.RuntimeDeterminism[^1].Disposition);
        Assert.Equal(["wall-clock", "host-filesystem", "host-network", "host-randomness", "host-scheduling"],
            manifest.NondeterministicInputs);
    }

    [Fact]
    public void ProjectAndSourceDependencies_PreserveFourAuthorityBoundaries()
    {
        string root = CompatFreezeScanner.FindCompilerRepoRoot();
        string platform = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Platform.Contracts", "HybridCPU.Platform.Contracts.csproj"));
        Assert.DoesNotContain("ProjectReference", platform, StringComparison.Ordinal);

        string kernel = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_RuntimeKernel", "HybridCPU.RuntimeKernel.csproj"));
        Assert.Single(XDocument.Parse(kernel).Descendants("ProjectReference"));
        Assert.DoesNotContain("Compiler", kernel, StringComparison.Ordinal);
        Assert.DoesNotContain("HybridCPU_ISE", kernel, StringComparison.Ordinal);

        string managed = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_ManagedRuntime", "HybridCPU.ManagedRuntime.csproj"));
        Assert.Equal(2, XDocument.Parse(managed).Descendants("ProjectReference").Count());
        Assert.DoesNotContain("HybridCPU_ISE", managed, StringComparison.Ordinal);
        foreach (string source in Directory.EnumerateFiles(Path.Combine(root, "Compilers", "HybridCPU_ManagedRuntime"), "*.cs", SearchOption.TopDirectoryOnly))
            Assert.DoesNotContain("YAKSys_Hybrid_CPU", File.ReadAllText(source), StringComparison.Ordinal);

        foreach (string relative in new[]
        {
            "Core/HybridCPU.Compiler.Core.csproj", "Cil/HybridCPU.Compiler.Cil.csproj",
            "NativeAot/HybridCPU.Compiler.NativeAot.Adapter.csproj",
            "ReleaseQualification/HybridCPU.Compiler.Release.csproj"
        })
            Assert.DoesNotContain("HybridCPU_ISE", File.ReadAllText(Path.Combine(root,
                "Compilers", "HybridCPU_Compiler", relative)), StringComparison.Ordinal);
        string iseProject = File.ReadAllText(Path.Combine(CompatFreezeScanner.FindRepoRoot(), "HybridCPU_ISE", "HybridCPU_ISE.csproj"));
        Assert.Contains("HybridCPU.ManagedRuntime", iseProject, StringComparison.Ordinal);
        Assert.DoesNotContain("HybridCPU.Compiler", iseProject, StringComparison.Ordinal);
        Assert.All(Manifest().AuthorityBoundaries, static boundary => Assert.True(boundary.Passed));
    }

    [Fact]
    public void FinalMatrix_CoversEveryRequiredLayerAndFinalLoweredProof()
    {
        HybridCpuRefPlan7FinalReleaseManifestV1 manifest = Manifest();
        string[] required =
        [
            "Compiler/CIL/CFG/SSA/phi", "ABI/frame/stack-walk", "object/type/static-init", "stack-maps/GC",
            "dispatch/delegates/generics", "managed-EH", "trap/MMU", "threads/TLS",
            "memory-order/synchronization", "interop", "async/reflection", "ISE execution",
            "positive/negative/property/fuzz", "cross-layer end-to-end"
        ];
        Assert.Equal(required, manifest.FinalMatrix.Select(static row => row.Capability));
        Assert.All(manifest.FinalMatrix, static row =>
        {
            Assert.True(row.Qualified);
            Assert.False(string.IsNullOrWhiteSpace(row.FinalRepresentation));
        });
        Assert.Contains("post-RA", manifest.FinalMatrix.Single(static row =>
            row.Capability == "ABI/frame/stack-walk").FinalRepresentation, StringComparison.Ordinal);
        Assert.Contains("final-PC", manifest.FinalMatrix.Single(static row =>
            row.Capability == "managed-EH").FinalRepresentation, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureDomains_RemainDistinctAndIntegrityNeverBecomesManagedException()
    {
        HybridCpuRefPlan7FinalReleaseManifestV1 manifest = Manifest();
        Assert.Equal(5, manifest.FailureDomains.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("managed-exception-or-oom", manifest.FailureDomains);
        Assert.Contains("runtime-fail-fast", manifest.FailureDomains);
        Assert.Contains("kernel-or-service-error", manifest.FailureDomains);
        Assert.Contains("architectural-illegal-privilege-protection-page-alignment-trap", manifest.FailureDomains);
        Assert.Equal("integrity-failure", manifest.FailureDomains[^1]);
    }

    private static HybridCpuRefPlan7FinalReleaseManifestV1 Manifest() =>
        HybridCpuRefPlan7FinalReleaseV1.Create(Commit, Evidence(), new(
            Hash("phase16-source"), Hash("dotnet-sdk-10.0.204"), Hash("phase16-options"),
            true, Hash("identical-hcexe"), true, Hash("identical-hcexe")));

    private static readonly Lazy<HybridCpuRefPlan7EvidenceArtifactV1[]> EvidenceArtifacts = new(CreateEvidence);

    private static HybridCpuRefPlan7EvidenceArtifactV1[] Evidence() => EvidenceArtifacts.Value;

    private static HybridCpuRefPlan7EvidenceArtifactV1[] CreateEvidence()
    {
        string[] schemas =
        [
            "HybridCPU.RefPlan7.Phase00ConformanceEvidenceV1",
            "HybridCPU.RefPlan7.Phase01BootstrapEvidenceV1",
            "HybridCPU.RefPlan7.Phase02ManagedReferencesEvidenceV1",
            "HybridCPU.RefPlan7.Phase03.ManagedAllocationEvidenceV1",
            "HybridCPU.RefPlan7.Phase04.ManagedCoreShapesEvidenceV1",
            "HybridCPU.RefPlan7.Phase05.NonMovingGcEvidenceV1",
            "HybridCPU.RefPlan7.Phase06.ManagedDispatchEvidenceV1",
            "HybridCPU.RefPlan7.Phase07.DelegatesEvidenceV1",
            "HybridCPU.RefPlan7.Phase08.ExactAotGenericsEvidenceV1",
            "HybridCPU.RefPlan7.Phase09.ManagedEhEvidenceV1",
            "HybridCPU.RefPlan7.Phase10.FaultTrapEvidenceV1",
            "HybridCPU.RefPlan7.Phase11.ThreadingTlsEvidenceV1",
            "HybridCPU.RefPlan7.Phase12.SynchronizationEvidenceV1",
            "HybridCPU.RefPlan7.Phase13.ManagedInteropEvidenceV1",
            "HybridCPU.RefPlan7.Phase14.ManagedReflectionEvidenceV1",
            "HybridCPU.RefPlan7.Phase15.ManagedAsyncEvidenceV1"
        ];
        string root = Path.Combine(AppContext.BaseDirectory, "refplan7-final-release-test-evidence");
        Directory.CreateDirectory(root);
        return Enumerable.Range(0, 16).Select(phase =>
        {
            string phaseText = phase.ToString("00");
            string path = Path.Combine(root, $"phase{phaseText}.json");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = schemas[phase],
                phase = phaseText,
                status = HybridCpuRefPlan7FinalReleaseV1.EvidenceClosed,
                componentRevisions = new { compiler = Commit, ise = Commit, managedRuntime = Commit, runtimeKernel = Commit },
                contracts = new
                {
                    managedAbiDigest = HybridCpuManagedAbiFamilyV1.Default.ContractDigest,
                    nativeAbiDigest = HybridCpuNativeAbiContractV2.Default.ContractDigest,
                    targetPlatformContractDigest = HybridCpuTargetPlatformContractV1.Default.ContractDigest,
                    managedFeatureSetDigest = HybridCpuManagedFeatureSetV1.Default.ContractDigest,
                    runtimePackDigest = HybridCpuSdkPackContractV1.CreateManifest().ContractDigest
                }
            });
            File.WriteAllBytes(path, bytes);
            return new HybridCpuRefPlan7EvidenceArtifactV1(phaseText, path,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                HybridCpuRefPlan7FinalReleaseV1.EvidenceClosed);
        }).ToArray();
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
