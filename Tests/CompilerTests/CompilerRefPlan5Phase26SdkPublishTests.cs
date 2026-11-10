using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HybridCPU.Compiler.NativeAot;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase26SdkPublishTests
{
    private static readonly string FixtureAssembly = typeof(RestrictedCilCSharpFixtures).Assembly.Location;
    private const string FixtureType = "HybridCPU_ISE.Tests.CompilerTests.RestrictedCilCSharpFixtures";

    [Fact]
    public void PackManifest_IsClosedVersionedAndTargetSpecific()
    {
        HybridCpuRuntimePackManifestV1 manifest = HybridCpuSdkPackContractV1.CreateManifest();

        Assert.Equal("hybridcpu", manifest.TargetRid);
        Assert.Equal("controlled-bare-metal-simulator-v1", manifest.TargetEnvironment);
        Assert.Equal(["win-x64"], manifest.SupportedHostRids);
        Assert.Equal(["10.0.204"], manifest.SupportedPublishSdkVersions);
        Assert.Equal(NativeAotSeamBaselineV1.SourceCommit, manifest.SourceCommit);
        Assert.Equal(NativeAotSeamBaselineV1.PatchDigest, manifest.PatchDigest);
        Assert.Equal(HybridCpuSdkPackContractV1.PublishPatchDigest, manifest.PublishPatchDigest);
        Assert.Equal(HybridCpuSdkPackContractV1.PatchSetDigest, manifest.PatchSetDigest);
        Assert.False(manifest.RequiresLlvm);
        Assert.Matches("^[0-9a-f]{64}$", manifest.ContractDigest);
    }

    [Fact]
    public void PackDescriptor_DoesNotOverclaimComponentQualificationAsPublishSupport()
    {
        HybridCpuRuntimePackManifestV1 manifest = HybridCpuSdkPackContractV1.CreateManifest();

        Assert.Equal(["async-runtime-libraries", "blittable-value-boxing", "bounded-public-reflection", "delegates-function-pointers", "deterministic-allocation",
            "eh-unwind", "exact-aot-generics", "fault-trap-integration", "gc-maps-safepoints", "managed-unmanaged-interop", "metadata-runtime-lookup",
            "static-type-initialization", "synchronization-memory-model", "szarray-core", "tls-thread-runtime-state", "type-layout-object-references", "utf16-string-literals",
            "virtual-interface-dispatch"],
            manifest.QualifiedWorkstreams);
        Assert.Equal(HybridCpuSdkPackContractV1.PublishQualifiedWorkstreams,
            manifest.PublishQualifiedWorkstreams);
        Assert.DoesNotContain("eh-unwind", manifest.PublishQualifiedWorkstreams);
        Assert.DoesNotContain("fault-trap-integration", manifest.PublishQualifiedWorkstreams);
        Assert.Contains("eh-unwind", manifest.QualifiedWorkstreams);
        Assert.Contains("fault-trap-integration", manifest.QualifiedWorkstreams);
        Assert.DoesNotContain("managed-unmanaged-interop", manifest.UnsupportedWorkstreams);
        Assert.False(manifest.FspAllowed);
        Assert.False(manifest.VdsaAllowed);
    }

    [Fact]
    public void ExactRidAndHost_AreAcceptedForValueOnlyPublish()
    {
        string? diagnostic = HybridCpuSdkPackContractV1.Validate(
            HybridCpuSdkPackContractV1.CreateManifest(), "hybridcpu", "win-x64", "10.0.204", []);

        Assert.Null(diagnostic);
    }

    [Theory]
    [InlineData("win-x64", "HCPUB1001")]
    [InlineData("linux-x64", "HCPUB1002")]
    public void RidOrHostMismatch_FailsClosed(string hostRid, string diagnostic)
    {
        string targetRid = hostRid == "win-x64" ? "win-x64" : "hybridcpu";
        string? result = HybridCpuSdkPackContractV1.Validate(
            HybridCpuSdkPackContractV1.CreateManifest(), targetRid, hostRid, "10.0.204", []);

        Assert.StartsWith(diagnostic, result, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishSdkVersionSkew_FailsBeforeCodegen()
    {
        string? result = HybridCpuSdkPackContractV1.Validate(
            HybridCpuSdkPackContractV1.CreateManifest(), "hybridcpu", "win-x64", "10.0.999", []);

        Assert.StartsWith("HCPUB1007", result, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedManagedRequirement_FailsBeforeCodegen()
    {
        string? result = HybridCpuSdkPackContractV1.Validate(
            HybridCpuSdkPackContractV1.CreateManifest(), "hybridcpu", "win-x64", "10.0.204", ["eh-unwind"]);

        Assert.StartsWith("HCPUB1004", result, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishQualifiedGcComponent_IsAcceptedByPublishPack()
    {
        string? result = HybridCpuSdkPackContractV1.Validate(
            HybridCpuSdkPackContractV1.CreateManifest(), "hybridcpu", "win-x64", "10.0.204", ["gc-maps-safepoints"]);

        Assert.Null(result);
    }

    [Fact]
    public void StalePackDigest_FailsBeforeCodegen()
    {
        HybridCpuRuntimePackManifestV1 stale = HybridCpuSdkPackContractV1.CreateManifest() with
        {
            ContractDigest = new string('0', 64)
        };

        Assert.StartsWith("HCPUB1003", HybridCpuSdkPackContractV1.Validate(stale, "hybridcpu", "win-x64", "10.0.204", []),
            StringComparison.Ordinal);
    }

    [Fact]
    public void PackagingTargets_HaveNoHostOrLlvmFallback()
    {
        string root = FindRepositoryRoot();
        string props = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "NativeAot", "Packaging", "HybridCPU.Sdk.props"));
        string targets = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "NativeAot", "Packaging", "HybridCPU.Sdk.targets"));

        Assert.Contains("RuntimeIdentifierGraphPath", props, StringComparison.Ordinal);
        Assert.Contains("UseAppHost>false", props, StringComparison.Ordinal);
        Assert.Contains("HybridCpuCompilerPath", targets, StringComparison.Ordinal);
        Assert.Contains("publish-graph", targets, StringComparison.Ordinal);
        Assert.Contains("HybridCpuUsePinnedGraph", targets, StringComparison.Ordinal);
        Assert.Contains("HCPUB1102", targets, StringComparison.Ordinal);
        Assert.DoesNotContain("LLVM", props + targets, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RuntimeIdentifier=win", props + targets, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublisherWithoutPostLinkEvidence_FailsClosedDeterministically()
    {
        using PublishRun first = RunPublisher([]);
        using PublishRun second = RunPublisher([]);

        Assert.Equal(25, first.ExitCode);
        Assert.Equal(25, second.ExitCode);
        Assert.Equal(first.Image, second.Image);
        Assert.Empty(first.Image);
        Assert.Empty(second.Image);
        Assert.Equal(first.StandardError, second.StandardError);
        Assert.Contains("HCPUB1011", first.StandardError, StringComparison.Ordinal);
        Assert.Null(first.Provenance);
        Assert.Null(second.Provenance);
    }

    [Fact]
    public void PublisherRejectsUnsupportedRequirementWithoutImage()
    {
        using PublishRun run = RunPublisher(["eh-unwind"]);

        Assert.Equal(25, run.ExitCode);
        Assert.Contains("HCPUB1004", run.StandardError, StringComparison.Ordinal);
        Assert.Empty(run.Image);
        Assert.Null(run.Provenance);
    }

    [Fact]
    public void CheckedInManifest_EqualsProductionContract()
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(root, "Compilers", "HybridCPU_Compiler", "NativeAot", "Packaging", "hybridcpu.runtime-pack.json");
        HybridCpuRuntimePackManifestV1 actual = Assert.IsType<HybridCpuRuntimePackManifestV1>(
            JsonSerializer.Deserialize<HybridCpuRuntimePackManifestV1>(File.ReadAllText(path)));

        Assert.Equal(JsonSerializer.Serialize(HybridCpuSdkPackContractV1.CreateManifest()), JsonSerializer.Serialize(actual));
    }

    [Fact]
    public void Phase26Patch_IsExactAndAddsOnlyClosedImageModeAndSidecar()
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(root, "Compilers", "HybridCPU_Compiler", "NativeAot", "Patches", "0002-hybridcpu-phase26-publish-image.patch");
        byte[] bytes = File.ReadAllBytes(path);
        string patch = File.ReadAllText(path);

        Assert.Equal(HybridCpuSdkPackContractV1.PublishPatchDigest,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Contains("HybridCpuOutputKind", patch, StringComparison.Ordinal);
        Assert.Contains("compile-image", patch, StringComparison.Ordinal);
        Assert.Contains("outputFile + \".json\"", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("ObjectWriter.EmitObject", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("LLVM", patch, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerOwnsExactCompilerRuntimeSdkAndPatchSetInputs()
    {
        string root = FindRepositoryRoot();
        string installer = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "NativeAot", "Packaging", "Install-HybridCpuSdkPack.ps1"));

        Assert.Contains("IlCompilerDirectory", installer, StringComparison.Ordinal);
        Assert.Contains("RuntimeReferenceDirectory", installer, StringComparison.Ordinal);
        Assert.Contains("0002-hybridcpu-phase26-publish-image.patch", installer, StringComparison.Ordinal);
        Assert.Contains("versioned packs are never overwritten", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("LLVM", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstalledPackFileManifest_IsVerifiedBeforeToolSelection()
    {
        string root = Path.Combine(Path.GetTempPath(), $"hybridcpu-pack-files-{Guid.NewGuid():N}");
        string ilcDirectory = Path.Combine(root, "ilcompiler");
        string references = Path.Combine(root, "references");
        Directory.CreateDirectory(ilcDirectory);
        Directory.CreateDirectory(references);
        string adapter = Path.Combine(root, "HybridCPU.Compiler.NativeAot.Adapter.dll");
        string ilc = Path.Combine(ilcDirectory, "ilc.dll");
        string reference = Path.Combine(references, "System.Private.CoreLib.dll");
        File.WriteAllBytes(adapter, [1, 2, 3]);
        File.WriteAllBytes(ilc, [4, 5]);
        File.WriteAllBytes(reference, [6]);
        string manifest = Path.Combine(root, "hybridcpu.pack-files.json");
        var files = new[] { adapter, ilc, reference }.Select(path => new
        {
            name = Path.GetRelativePath(root, path).Replace('\\', '/'),
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            bytes = new FileInfo(path).Length
        }).ToArray();
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            schema = "hybridcpu.installed-pack-files/v1",
            packVersion = HybridCpuSdkPackContractV1.PackVersion,
            targetRid = HybridCpuSdkPackContractV1.TargetRid,
            files
        }));
        try
        {
            Assert.Null(HybridCpuSdkPackContractV1.ValidateInstalledPackFiles(manifest, adapter, ilc, references));
            File.WriteAllBytes(adapter, [9, 9, 9]);
            Assert.StartsWith("HCPUB1008", HybridCpuSdkPackContractV1.ValidateInstalledPackFiles(manifest, adapter, ilc, references),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static PublishRun RunPublisher(IReadOnlyList<string> requirements)
    {
        string root = FindRepositoryRoot();
        string output = Path.Combine(Path.GetTempPath(), $"hybridcpu-publish-{Guid.NewGuid():N}.hcexe");
        // Exercise the requested validation boundary with a current pack. The separate
        // CheckedInManifest test diagnoses repository packaging drift independently.
        string manifest = output + ".pack.json";
        File.WriteAllText(manifest, JsonSerializer.Serialize(HybridCpuSdkPackContractV1.CreateManifest()));
        ProcessStartInfo start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(NativeAotSeamArtifactV1).Assembly.Location);
        start.ArgumentList.Add("publish-bringup");
        AddPair(start, "--assembly", FixtureAssembly);
        AddPair(start, "--type", FixtureType);
        AddPair(start, "--method", nameof(RestrictedCilCSharpFixtures.Add));
        AddPair(start, "--out", output);
        AddPair(start, "--manifest", manifest);
        AddPair(start, "--target-rid", "hybridcpu");
        AddPair(start, "--host-rid", "win-x64");
        AddPair(start, "--sdk-version", "10.0.204");
        AddPair(start, "--requirements", string.Join(',', requirements));
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Publisher did not start.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        byte[] image = File.Exists(output) ? File.ReadAllBytes(output) : [];
        string imageManifest = File.Exists(output + ".json") ? File.ReadAllText(output + ".json") : string.Empty;
        string provenanceJson = File.Exists(output + ".provenance.json") ? File.ReadAllText(output + ".provenance.json") : string.Empty;
        HybridCpuPublishProvenanceV1? provenance = string.IsNullOrEmpty(provenanceJson)
            ? null
            : JsonSerializer.Deserialize<HybridCpuPublishProvenanceV1>(provenanceJson);
        return new(process.ExitCode, stdout, stderr, output, image, imageManifest, provenanceJson, provenance);
    }

    private static void AddPair(ProcessStartInfo start, string name, string value)
    {
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(value);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "Compilers", "HybridCPU_Compiler"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed record PublishRun(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        string OutputPath,
        byte[] Image,
        string ImageManifestJson,
        string ProvenanceJson,
        HybridCpuPublishProvenanceV1? Provenance) : IDisposable
    {
        public void Dispose()
        {
            foreach (string path in new[] { OutputPath, OutputPath + ".json", OutputPath + ".provenance.json", OutputPath + ".pack.json" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}
