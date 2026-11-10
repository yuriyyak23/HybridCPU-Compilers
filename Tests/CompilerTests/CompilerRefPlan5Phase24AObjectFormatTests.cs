using System.Diagnostics;
using System.Text.Json;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.NativeAot;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase24AObjectFormatTests
{
    private static readonly HybridCpuObjectWriterV1 Writer = new();
    private static readonly string FixtureAssembly = typeof(RestrictedCilCSharpFixtures).Assembly.Location;
    private const string FixtureType = "HybridCPU_ISE.Tests.CompilerTests.RestrictedCilCSharpFixtures";

    [Fact]
    public void Contract_IsVersionedAndDoesNotRewriteHistoricalPhase08BUnsupportedFacts()
    {
        Assert.Equal("hybridcpu.object/hco-v1", HybridCpuObjectFormatContractV1.SchemaId);
        Assert.Equal("HCOBJ001"u8.ToArray(), HybridCpuObjectFormatContractV1.Magic.ToArray());
        Assert.Equal(HybridCpuObjectFormat.None, HybridCpuTargetPlatformContractV1.Default.ObjectFormat);
        Assert.All(HybridCpuTargetPlatformContractV1.Default.ObjectWriterFeatures,
            static feature => Assert.Equal(HybridCpuPlatformFactStatus.Unsupported, feature.Support));
        Assert.Equal(64, HybridCpuObjectFormatContractV1.ContractDigest.Length);
        Assert.Equal(64, HybridCpuObjectFormatContractV1.OptionsDigest.Length);
    }

    [Fact]
    public void CanonicalObject_IsByteIdenticalUnderInputPermutationAndRoundTrips()
    {
        HybridCpuObjectRequestV1 request = ValidObject();
        HybridCpuObjectArtifactV1 first = Writer.Write(request);
        HybridCpuObjectArtifactV1 second = Writer.Write(request with
        {
            Sections = request.Sections.Reverse().ToArray(),
            Symbols = request.Symbols.Reverse().ToArray(),
            Relocations = request.Relocations.Reverse().ToArray()
        });

        Assert.Equal(HybridCpuObjectStatusV1.Success, first.Status);
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal(first.ObjectSha256, second.ObjectSha256);
        Assert.Equal(first.MetadataDigest, second.MetadataDigest);
        HybridCpuObjectArtifactV1 inspected = Writer.Inspect(first.Bytes);
        Assert.Equal(HybridCpuObjectStatusV1.Success, inspected.Status);
        Assert.Equal(first.Bytes, inspected.Bytes);
        Assert.Equal(new[] { ".text", ".rodata", ".data", ".bss" }, inspected.Sections.Select(static item => item.Name));
        Assert.Equal(new[] { "data", "entry", "external" }, inspected.Symbols.Select(static item => item.Name));
    }

    [Fact]
    public void NativeAotCompileObject_WrapsTheExactPhase23CodeWithoutSecondPipeline()
    {
        using AdapterRun raw = RunAdapter("compile", nameof(RestrictedCilCSharpFixtures.Add));
        using AdapterRun objectRun = RunAdapter("compile-object", nameof(RestrictedCilCSharpFixtures.Add));

        Assert.Equal(0, raw.ExitCode);
        Assert.Equal(0, objectRun.ExitCode);
        HybridCpuObjectArtifactV1 inspected = Writer.Inspect(objectRun.Bytes);
        Assert.Equal(HybridCpuObjectStatusV1.Success, inspected.Status);
        HybridCpuObjectSectionV1 text = Assert.Single(inspected.Sections);
        Assert.Equal(HybridCpuObjectSectionKind.Code, text.Kind);
        Assert.Equal(raw.Bytes, text.Data);
        NativeAotObjectArtifactV1 manifest = JsonSerializer.Deserialize<NativeAotObjectArtifactV1>(objectRun.Manifest)!;
        Assert.Equal(inspected.ObjectSha256, manifest.ObjectSha256);
        Assert.Equal(HybridCpuObjectFormatContractV1.ContractDigest, manifest.ObjectFormatContractDigest);
        Assert.False(manifest.HasLinkAuthority);
        Assert.False(manifest.HasRuntimeAuthority);
        Assert.False(manifest.HasPublicationAuthority);
    }

    [Theory]
    [InlineData(HybridCpuObjectSectionKind.ThreadLocalData)]
    [InlineData(HybridCpuObjectSectionKind.Debug)]
    public void FutureRuntimeSections_FailClosed(HybridCpuObjectSectionKind kind)
    {
        HybridCpuObjectRequestV1 request = ValidObject() with
        {
            Sections = [new(".future", kind, 8, [1], 1)]
        };

        HybridCpuObjectArtifactV1 result = Writer.Write(request with { Symbols = [], Relocations = [] });

        Assert.Equal(HybridCpuObjectStatusV1.Unsupported, result.Status);
        Assert.Equal("HCOBJ1002", Assert.Single(result.Diagnostics).Code);
        Assert.Empty(result.Bytes);
    }

    [Theory]
    [InlineData(HybridCpuRelocationKind.ThreadLocal, "HCOBJ1014")]
    [InlineData(HybridCpuRelocationKind.BundleRelativeSigned16, "HCOBJ1015")]
    [InlineData(HybridCpuRelocationKind.Unknown, "HCOBJ0013")]
    public void NonLinkerOwnedOrUnknownRelocations_FailClosed(HybridCpuRelocationKind kind, string diagnostic)
    {
        HybridCpuObjectRequestV1 request = ValidObject() with
        {
            Relocations = [new(".text", 0, kind, "external", 0)]
        };

        HybridCpuObjectArtifactV1 result = Writer.Write(request);

        Assert.NotEqual(HybridCpuObjectStatusV1.Success, result.Status);
        Assert.Equal(diagnostic, Assert.Single(result.Diagnostics).Code);
        Assert.Empty(result.Bytes);
    }

    [Theory]
    [InlineData(HybridCpuSymbolBinding.Weak, null)]
    [InlineData(HybridCpuSymbolBinding.Global, "same")]
    public void WeakAndComdatPolicies_RemainUnsupported(HybridCpuSymbolBinding binding, string? comdat)
    {
        HybridCpuObjectRequestV1 request = ValidObject();
        HybridCpuObjectSymbolV1 entry = request.Symbols.Single(static item => item.Name == "entry") with
        {
            Binding = binding,
            ComdatKey = comdat
        };

        HybridCpuObjectArtifactV1 result = Writer.Write(request with
        {
            Symbols = request.Symbols.Select(item => item.Name == "entry" ? entry : item).ToArray()
        });

        Assert.Equal(HybridCpuObjectStatusV1.Unsupported, result.Status);
        Assert.Equal("HCOBJ1003", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void RelocationMatrix_ComputesExactValuesAndRejectsOverflow()
    {
        HybridCpuRelocationEvaluationV1 absolute = HybridCpuObjectFormatContractV1.EvaluateRelocation(
            HybridCpuRelocationKind.Absolute64, 0x1000, 0, -8);
        HybridCpuRelocationEvaluationV1 relative = HybridCpuObjectFormatContractV1.EvaluateRelocation(
            HybridCpuRelocationKind.PcRelative32, 0x1100, 0x1000, -4);
        HybridCpuRelocationEvaluationV1 absoluteOverflow = HybridCpuObjectFormatContractV1.EvaluateRelocation(
            HybridCpuRelocationKind.Absolute64, ulong.MaxValue, 0, 1);
        HybridCpuRelocationEvaluationV1 positiveOverflow = HybridCpuObjectFormatContractV1.EvaluateRelocation(
            HybridCpuRelocationKind.PcRelative32, (ulong)int.MaxValue + 2, 0, 0);
        HybridCpuRelocationEvaluationV1 negativeOverflow = HybridCpuObjectFormatContractV1.EvaluateRelocation(
            HybridCpuRelocationKind.PcRelative32, 0, (ulong)int.MaxValue + 2, 0);

        Assert.Equal((ulong)0xff8, absolute.EncodedValue);
        Assert.Equal((ulong)0xfc, relative.EncodedValue);
        Assert.All(new[] { absoluteOverflow, positiveOverflow, negativeOverflow }, result =>
        {
            Assert.Equal(HybridCpuObjectStatusV1.Invalid, result.Status);
            Assert.Equal("HCOBJ1013", result.DiagnosticCode);
        });
    }

    [Fact]
    public void RelocationPatchLocations_RequireWidthAlignmentBoundsAndSingleOwnership()
    {
        HybridCpuObjectRequestV1 request = ValidObject();
        foreach (HybridCpuObjectRelocationV1 relocation in new HybridCpuObjectRelocationV1[]
        {
            new(".text", 2, HybridCpuRelocationKind.PcRelative32, "external", 0),
            new(".text", 64, HybridCpuRelocationKind.Absolute64, "external", 0),
            new(".bss", 0, HybridCpuRelocationKind.Absolute64, "external", 0)
        })
        {
            HybridCpuObjectArtifactV1 result = Writer.Write(request with { Relocations = [relocation] });
            Assert.Equal(HybridCpuObjectStatusV1.Invalid, result.Status);
            Assert.Empty(result.Bytes);
        }
        HybridCpuObjectArtifactV1 duplicate = Writer.Write(request with
        {
            Relocations =
            [
                new(".text", 0, HybridCpuRelocationKind.Absolute64, "external", 0),
                new(".text", 0, HybridCpuRelocationKind.PcRelative32, "entry", 0)
            ]
        });
        Assert.Equal("HCOBJ0014", Assert.Single(duplicate.Diagnostics).Code);
    }

    [Fact]
    public void VersionSkew_IsRejectedBeforeBytesAreEmitted()
    {
        HybridCpuObjectRequestV1 request = ValidObject() with { TargetContractDigest = new string('0', 64) };

        HybridCpuObjectArtifactV1 result = Writer.Write(request);

        Assert.Equal(HybridCpuObjectStatusV1.VersionSkew, result.Status);
        Assert.Equal("HCOBJ1001", Assert.Single(result.Diagnostics).Code);
        Assert.Empty(result.Bytes);
    }

    [Fact]
    public void CorruptionAndHostObjectContamination_AreRejected()
    {
        byte[] canonical = Writer.Write(ValidObject()).Bytes;
        List<byte[]> controls = new List<byte[]>
        {
            "\u007fELF"u8.ToArray(),
            new byte[] { 0x4d, 0x5a, 0x90, 0x00 },
            canonical[..^1],
            Corrupt(canonical, 0),
            Corrupt(canonical, 16),
            Corrupt(canonical, 75),
            Corrupt(canonical, canonical.Length - 1)
        };

        Assert.All(controls, bytes =>
        {
            HybridCpuObjectArtifactV1 result = Writer.Inspect(bytes);
            Assert.NotEqual(HybridCpuObjectStatusV1.Success, result.Status);
            Assert.Empty(result.Bytes);
        });
    }

    [Fact]
    public void ArtifactCarriesNoLinkRuntimeOrPublicationAuthority()
    {
        HybridCpuObjectArtifactV1 artifact = Writer.Write(ValidObject());

        Assert.Equal(HybridCpuObjectStatusV1.Success, artifact.Status);
        Assert.False(artifact.HasLinkAuthority);
        Assert.False(artifact.HasRuntimeAuthority);
        Assert.False(artifact.HasPublicationAuthority);
    }

    private static HybridCpuObjectRequestV1 ValidObject()
    {
        byte[] text = Enumerable.Range(0, 64).Select(static value => (byte)value).ToArray();
        return new(
            [
                new(".data", HybridCpuObjectSectionKind.WritableData, 8, new byte[16], 16),
                new(".bss", HybridCpuObjectSectionKind.ZeroFill, 16, [], 64),
                new(".text", HybridCpuObjectSectionKind.Code, 32, text, 64),
                new(".rodata", HybridCpuObjectSectionKind.ReadOnlyData, 8, [1, 2, 3, 4, 5, 6, 7, 8], 8)
            ],
            [
                new("external", HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, null, 0, 0, IsDefinition: false),
                new("entry", HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, ".text", 0, 64, IsDefinition: true),
                new("data", HybridCpuSymbolBinding.Local, HybridCpuSymbolVisibility.Hidden, ".data", 0, 8, IsDefinition: true)
            ],
            [
                new(".text", 8, HybridCpuRelocationKind.Absolute64, "data", 4),
                new(".text", 32, HybridCpuRelocationKind.PcRelative32, "external", -4)
            ],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest);
    }

    private static byte[] Corrupt(byte[] source, int index)
    {
        byte[] copy = source.ToArray();
        copy[index] ^= 0x5a;
        return copy;
    }

    private static AdapterRun RunAdapter(string command, string method)
    {
        string output = Path.Combine(Path.GetTempPath(), $"hybridcpu-24a-{Guid.NewGuid():N}.bin");
        ProcessStartInfo start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(NativeAotSeamArtifactV1).Assembly.Location);
        start.ArgumentList.Add(command);
        AddPair(start, "--assembly", FixtureAssembly);
        AddPair(start, "--type", FixtureType);
        AddPair(start, "--method", method);
        AddPair(start, "--out", output);
        AddPair(start, "--source-commit", NativeAotSeamBaselineV1.SourceCommit);
        AddPair(start, "--patch-digest", NativeAotSeamBaselineV1.PatchDigest);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Adapter process did not start.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        byte[] bytes = File.Exists(output) ? File.ReadAllBytes(output) : [];
        string manifest = File.Exists(output + ".json") ? File.ReadAllText(output + ".json") : string.Empty;
        return new(process.ExitCode, stdout, stderr, output, bytes, manifest);
    }

    private static void AddPair(ProcessStartInfo start, string name, string value)
    {
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(value);
    }

    private sealed record AdapterRun(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        string OutputPath,
        byte[] Bytes,
        string Manifest) : IDisposable
    {
        public void Dispose()
        {
            if (File.Exists(OutputPath)) File.Delete(OutputPath);
            if (File.Exists(OutputPath + ".json")) File.Delete(OutputPath + ".json");
        }
    }
}
