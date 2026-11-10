using System.Buffers.Binary;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase24BStaticLinkerTests
{
    private static readonly HybridCpuObjectWriterV1 Writer = new();
    private static readonly HybridCpuStaticLinkerV1 Linker = new();

    [Fact]
    public void CrossObjectCallAndDataRelocations_AreResolvedDeterministically()
    {
        HybridCpuLinkInputV1[] inputs = CrossObjectInputs();
        HybridCpuStaticLinkArtifactV1 first = Linker.Link(inputs);
        HybridCpuStaticLinkArtifactV1 second = Linker.Link(inputs.Reverse().ToArray());

        Assert.Equal(HybridCpuLinkStatusV1.Success, first.Status);
        Assert.Equal(first.ImageBytes, second.ImageBytes);
        Assert.Equal(first.ImageSha256, second.ImageSha256);
        Assert.Equal(first.LinkMapDigest, second.LinkMapDigest);
        Assert.Equal(HybridCpuStaticLinkOptionsV1.Production.OptionsDigest, first.OptionsDigest);
        HybridCpuAppliedRelocationV1 relative = first.AppliedRelocations.Single(static row => row.Kind == HybridCpuRelocationKind.PcRelative32);
        HybridCpuAppliedRelocationV1 absolute = first.AppliedRelocations.Single(static row => row.Kind == HybridCpuRelocationKind.Absolute64);
        Assert.Equal((uint)relative.EncodedValue, BinaryPrimitives.ReadUInt32LittleEndian(
            first.ImageBytes.AsSpan(checked((int)(relative.PlaceAddress - first.ImageBase)))));
        Assert.Equal(absolute.EncodedValue, BinaryPrimitives.ReadUInt64LittleEndian(
            first.ImageBytes.AsSpan(checked((int)(absolute.PlaceAddress - first.ImageBase)))));
        Assert.Equal(new ulong[] { 0x10000, 0x10020, 0x11000 }, first.Sections.Select(static section => section.Address));
        Assert.Equal(2, first.AppliedRelocations.Count);
    }

    [Fact]
    public void LocalSymbols_AreScopedPerModuleAndDoNotCollide()
    {
        byte[] first = Object(
            [Text()],
            [Definition("L", HybridCpuSymbolBinding.Local, ".text", 0, 32)],
            []);
        byte[] second = Object(
            [Text()],
            [Definition("L", HybridCpuSymbolBinding.Local, ".text", 0, 32)],
            []);

        HybridCpuStaticLinkArtifactV1 result = Linker.Link([new("a", first), new("b", second)]);

        Assert.Equal(HybridCpuLinkStatusV1.Success, result.Status);
        Assert.Equal(2, result.Symbols.Count(static symbol => symbol.Name == "L"));
        Assert.Equal(new ulong[] { 0x10000, 0x10020 }, result.Symbols.Select(static symbol => symbol.Address));
    }

    [Fact]
    public void DuplicateGlobalDefinitions_FailBeforeImageEmission()
    {
        byte[] first = Object([Text()], [Definition("same", HybridCpuSymbolBinding.Global, ".text", 0, 32)], []);
        byte[] second = Object([Text()], [Definition("same", HybridCpuSymbolBinding.Global, ".text", 0, 32)], []);

        HybridCpuStaticLinkArtifactV1 result = Linker.Link([new("a", first), new("b", second)]);

        Assert.Equal(HybridCpuLinkStatusV1.Invalid, result.Status);
        Assert.Equal("HCLINK1002", Assert.Single(result.Diagnostics).Code);
        Assert.Empty(result.ImageBytes);
    }

    [Fact]
    public void UndefinedGlobal_FailsBeforeImageEmission()
    {
        byte[] source = Object(
            [Text()],
            [Definition("main", HybridCpuSymbolBinding.Global, ".text", 0, 32), Declaration("missing")],
            [new(".text", 0, HybridCpuRelocationKind.PcRelative32, "missing", 0)]);

        HybridCpuStaticLinkArtifactV1 result = Linker.Link([new("a", source)]);

        Assert.Equal(HybridCpuLinkStatusV1.Invalid, result.Status);
        Assert.Equal("HCLINK1003", Assert.Single(result.Diagnostics).Code);
        Assert.Empty(result.ImageBytes);
    }

    [Fact]
    public void SectionClassTransitions_ArePageAlignedAndBssIsZeroFilled()
    {
        byte[] source = Object(
            [
                Text(),
                new(".rodata", HybridCpuObjectSectionKind.ReadOnlyData, 8, [1, 2, 3, 4], 4),
                new(".data", HybridCpuObjectSectionKind.WritableData, 16, [5, 6, 7, 8], 4),
                new(".bss", HybridCpuObjectSectionKind.ZeroFill, 64, [], 96)
            ],
            [Definition("main", HybridCpuSymbolBinding.Global, ".text", 0, 32)],
            []);

        HybridCpuStaticLinkArtifactV1 result = Linker.Link([new("module", source)]);

        Assert.Equal(HybridCpuLinkStatusV1.Success, result.Status);
        Assert.Equal(new ulong[] { 0x10000, 0x11000, 0x12000, 0x13000 }, result.Sections.Select(static section => section.Address));
        HybridCpuLinkedSectionV1 bss = result.Sections.Single(static section => section.Kind == HybridCpuObjectSectionKind.ZeroFill);
        int bssOffset = checked((int)(bss.Address - result.ImageBase));
        Assert.All(result.ImageBytes.AsSpan(bssOffset, checked((int)bss.Size)).ToArray(), static value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CorruptAndHostObjects_AreRejected(bool corruptCanonical)
    {
        byte[] input;
        if (corruptCanonical)
        {
            input = CrossObjectInputs()[0].ObjectBytes.ToArray();
            input[^1] ^= 1;
        }
        else
        {
            input = "\u007fELF-host-object"u8.ToArray();
        }

        HybridCpuStaticLinkArtifactV1 result = Linker.Link([new("bad", input)]);

        Assert.Equal(HybridCpuLinkStatusV1.CorruptInput, result.Status);
        Assert.Equal("HCLINK2001", Assert.Single(result.Diagnostics).Code);
        Assert.Empty(result.ImageBytes);
    }

    [Fact]
    public void ModuleIdentityAndInputBudgets_AreClosedAndDeterministic()
    {
        byte[] source = Object([Text()], [Definition("main", HybridCpuSymbolBinding.Global, ".text", 0, 32)], []);
        HybridCpuStaticLinkArtifactV1 duplicate = Linker.Link([new("same", source), new("same", source)]);
        HybridCpuLinkInputV1[] tooMany = Enumerable.Range(0, HybridCpuStaticLinkOptionsV1.Production.MaximumInputs + 1)
            .Select(index => new HybridCpuLinkInputV1($"m{index:D3}", source)).ToArray();

        Assert.Equal("HCLINK0002", Assert.Single(duplicate.Diagnostics).Code);
        Assert.Equal("HCLINK0001", Assert.Single(Linker.Link(tooMany).Diagnostics).Code);
    }

    [Fact]
    public void OptionsSkew_IsRejectedBeforeObjectInspection()
    {
        HybridCpuStaticLinkOptionsV1 skewed = HybridCpuStaticLinkOptionsV1.Production with { ImageBase = 0 };

        HybridCpuStaticLinkArtifactV1 result = Linker.Link([new("bad", "not-an-object"u8.ToArray())], skewed);

        Assert.Equal(HybridCpuLinkStatusV1.VersionSkew, result.Status);
        Assert.Equal("HCLINK1001", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void SuccessfulLinkHasNoRuntimeExecutionOrPublicationAuthority()
    {
        HybridCpuStaticLinkArtifactV1 result = Linker.Link(CrossObjectInputs());

        Assert.True(result.LinkerResolvedSymbols);
        Assert.False(result.HasRuntimeAuthority);
        Assert.False(result.HasExecutionAuthority);
        Assert.False(result.HasPublicationAuthority);
    }

    private static HybridCpuLinkInputV1[] CrossObjectInputs()
    {
        byte[] caller = Object(
            [new(".text", HybridCpuObjectSectionKind.Code, 32, new byte[64], 64)],
            [
                Definition("main", HybridCpuSymbolBinding.Global, ".text", 0, 64),
                Declaration("funcB"),
                Declaration("dataB")
            ],
            [
                new(".text", 0, HybridCpuRelocationKind.PcRelative32, "funcB", -4),
                new(".text", 8, HybridCpuRelocationKind.Absolute64, "dataB", 8)
            ]);
        byte[] callee = Object(
            [Text(), new(".data", HybridCpuObjectSectionKind.WritableData, 8, new byte[16], 16)],
            [
                Definition("funcB", HybridCpuSymbolBinding.Global, ".text", 0, 32),
                Definition("dataB", HybridCpuSymbolBinding.Global, ".data", 0, 16)
            ],
            []);
        return [new("caller", caller), new("callee", callee)];
    }

    private static HybridCpuObjectSectionV1 Text() =>
        new(".text", HybridCpuObjectSectionKind.Code, 32, new byte[32], 32);

    private static HybridCpuObjectSymbolV1 Definition(
        string name,
        HybridCpuSymbolBinding binding,
        string section,
        ulong offset,
        ulong size) =>
        new(name, binding, HybridCpuSymbolVisibility.Default, section, offset, size, IsDefinition: true);

    private static HybridCpuObjectSymbolV1 Declaration(string name) =>
        new(name, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, null, 0, 0, IsDefinition: false);

    private static byte[] Object(
        IReadOnlyList<HybridCpuObjectSectionV1> sections,
        IReadOnlyList<HybridCpuObjectSymbolV1> symbols,
        IReadOnlyList<HybridCpuObjectRelocationV1> relocations)
    {
        HybridCpuObjectArtifactV1 artifact = Writer.Write(new(
            sections, symbols, relocations,
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
        Assert.Equal(HybridCpuObjectStatusV1.Success, artifact.Status);
        return artifact.Bytes;
    }
}
