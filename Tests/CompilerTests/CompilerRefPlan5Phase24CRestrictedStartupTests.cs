using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase24CRestrictedStartupTests
{
    private static readonly HybridCpuObjectWriterV1 Writer = new();
    private static readonly HybridCpuStaticLinkerV1 Linker = new();
    private static readonly HybridCpuRestrictedImageBuilderV1 Builder = new();

    [Fact]
    public void RestrictedEntryPoint_ProducesDeterministicSelfDescribingPackage()
    {
        HybridCpuStaticLinkArtifactV1 link = LinkedProgram();

        HybridCpuRestrictedImageV1 first = Builder.Build(new(link, "main"));
        HybridCpuRestrictedImageV1 second = Builder.Build(new(link, "main"));
        HybridCpuRestrictedImageV1 inspected = Builder.Inspect(first.PackageBytes);

        Assert.Equal(HybridCpuStartupStatusV1.Success, first.Status);
        Assert.Equal(first.PackageBytes, second.PackageBytes);
        Assert.Equal(first.PackageSha256, second.PackageSha256);
        Assert.Equal(first.OptionsDigest, inspected.OptionsDigest);
        Assert.Equal(first.ImageBytes, inspected.ImageBytes);
        Assert.Equal(first.ImageSha256, inspected.ImageSha256);
        Assert.Equal(first.LinkMapDigest, inspected.LinkMapDigest);
        Assert.Equal(first.EntryAddress, inspected.EntryAddress);
        Assert.Equal(4096 + link.ImageBytes.Length, first.PackageBytes.Length);
    }

    [Fact]
    public void StartupRegisters_AreExactNativeAbiRolesAndHaveNoTlsContext()
    {
        HybridCpuRestrictedImageV1 result = Builder.Build(new(LinkedProgram(), "main"));

        HybridCpuStartupRegisterStateV1 registers = Assert.IsType<HybridCpuStartupRegisterStateV1>(result.InitialRegisters);
        Assert.Equal(HybridCpuNativeAbiContractV2.StackPointerRegister, registers.StackPointerRegister);
        Assert.Equal(HybridCpuNativeAbiContractV2.FramePointerRegister, registers.FramePointerRegister);
        Assert.Equal(HybridCpuNativeAbiContractV2.ThreadPointerRegister, registers.ThreadPointerRegister);
        Assert.Equal(HybridCpuNativeAbiContractV2.ReturnAddressRegister, registers.ReturnAddressRegister);
        Assert.Equal(HybridCpuNativeAbiContractV2.Default.ReturnRegisters[0], registers.ReturnValueRegister);
        Assert.Equal(0UL, registers.FramePointer);
        Assert.Equal(0UL, registers.ThreadPointer);
        Assert.Equal(0UL, registers.StackPointer % HybridCpuNativeAbiContractV2.StackAlignmentBytes);
        Assert.Equal(HybridCpuRestrictedStartupOptionsV1.Production.ReturnSentinel, registers.ReturnAddress);
    }

    [Fact]
    public void ZeroFillStaticData_IsTheOnlyQualifiedInitializationSubset()
    {
        HybridCpuStaticLinkArtifactV1 link = LinkedProgram(includeBss: true);

        HybridCpuRestrictedImageV1 result = Builder.Build(new(link, "main"));

        Assert.Equal(HybridCpuStartupStatusV1.Success, result.Status);
        HybridCpuLinkedSectionV1 bss = link.Sections.Single(static section => section.Kind == HybridCpuObjectSectionKind.ZeroFill);
        Assert.All(result.ImageBytes.AsSpan(checked((int)(bss.Address - result.ImageBase)), checked((int)bss.Size)).ToArray(),
            static value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData("missing", "HCSTART1002")]
    [InlineData("hidden_main", "HCSTART1003")]
    [InlineData("unaligned_main", "HCSTART1004")]
    public void InvalidEntrypoints_FailClosed(string entry, string code)
    {
        HybridCpuRestrictedImageV1 result = Builder.Build(new(LinkedProgram(withEntryVariants: true), entry));

        Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
        Assert.Empty(result.PackageBytes);
    }

    [Theory]
    [InlineData("__hybridcpu_managed_write_barrier")]
    [InlineData("__hybridcpu_managed_poll")]
    [InlineData("__hybridcpu_managed_throw")]
    public void ReservedUnimplementedRuntimeHelpers_AreRejected(string helper)
    {
        HybridCpuStaticLinkArtifactV1 link = LinkedProgram(extraGlobal: helper);

        HybridCpuRestrictedImageV1 result = Builder.Build(new(link, "main"));

        Assert.Equal(HybridCpuStartupStatusV1.Unsupported, result.Status);
        Assert.Equal("HCSTART1005", Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData("Type::.cctor")]
    [InlineData("__hybridcpu_static_init_0")]
    public void ExecutableStaticInitializers_AreRejected(string initializer)
    {
        HybridCpuRestrictedImageV1 result = Builder.Build(new(LinkedProgram(extraGlobal: initializer), "main"));

        Assert.Equal(HybridCpuStartupStatusV1.Unsupported, result.Status);
        Assert.Equal("HCSTART1006", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void CorruptLinkedImageAndCorruptPackage_AreRejected()
    {
        HybridCpuStaticLinkArtifactV1 link = LinkedProgram();
        HybridCpuStaticLinkArtifactV1 corruptLink = link with { ImageBytes = link.ImageBytes.ToArray() };
        corruptLink.ImageBytes[0] ^= 1;
        HybridCpuRestrictedImageV1 linkedFailure = Builder.Build(new(corruptLink, "main"));
        HybridCpuRestrictedImageV1 valid = Builder.Build(new(link, "main"));
        byte[] corruptPackage = valid.PackageBytes.ToArray();
        corruptPackage[^1] ^= 1;

        Assert.Equal("HCSTART2001", Assert.Single(linkedFailure.Diagnostics).Code);
        Assert.Equal("HCSTART2006", Assert.Single(Builder.Inspect(corruptPackage).Diagnostics).Code);
    }

    [Fact]
    public void StartupOptionsSkew_FailsBeforeLinkedImageInspection()
    {
        HybridCpuRestrictedStartupOptionsV1 skewed = HybridCpuRestrictedStartupOptionsV1.Production with { StackSize = 8 };
        HybridCpuStaticLinkArtifactV1 bad = LinkedProgram() with { Status = HybridCpuLinkStatusV1.Invalid };

        HybridCpuRestrictedImageV1 result = Builder.Build(new(bad, "main"), skewed);

        Assert.Equal(HybridCpuStartupStatusV1.VersionSkew, result.Status);
        Assert.Equal("HCSTART1001", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void SuccessfulPackageDoesNotAcquireRuntimeExecutionPublicationOrRetireAuthority()
    {
        HybridCpuRestrictedImageV1 result = Builder.Build(new(LinkedProgram(), "main"));

        Assert.True(result.StartupContractSatisfied);
        Assert.False(result.HasRuntimeAuthority);
        Assert.False(result.HasExecutionAuthority);
        Assert.False(result.HasPublicationAuthority);
        Assert.False(result.HasCommitOrRetireAuthority);
    }

    private static HybridCpuStaticLinkArtifactV1 LinkedProgram(
        bool includeBss = false,
        bool withEntryVariants = false,
        string? extraGlobal = null)
    {
        byte[] code = new byte[HybridCpuBundleSerializer.BundleSizeBytes * 4];
        List<HybridCpuObjectSectionV1> sections = new List<HybridCpuObjectSectionV1>
        {
            new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes,
                code, (ulong)code.Length)
        };
        if (includeBss)
            sections.Add(new(".bss", HybridCpuObjectSectionKind.ZeroFill, 16, [], 64));
        List<HybridCpuObjectSymbolV1> symbols = new List<HybridCpuObjectSymbolV1>
        {
            Definition("main", HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, 0, 256)
        };
        if (withEntryVariants)
        {
            symbols.Add(Definition("hidden_main", HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, 256, 256));
            symbols.Add(Definition("unaligned_main", HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, 32, 256));
        }
        if (extraGlobal is not null)
            symbols.Add(Definition(extraGlobal, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, 512, 256));
        HybridCpuObjectArtifactV1 artifact = Writer.Write(new(sections, symbols, [],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
        Assert.Equal(HybridCpuObjectStatusV1.Success, artifact.Status);
        HybridCpuStaticLinkArtifactV1 link = Linker.Link([new("managed-program", artifact.Bytes)]);
        Assert.Equal(HybridCpuLinkStatusV1.Success, link.Status);
        return link;
    }

    private static HybridCpuObjectSymbolV1 Definition(
        string name,
        HybridCpuSymbolBinding binding,
        HybridCpuSymbolVisibility visibility,
        ulong offset,
        ulong size) =>
        new(name, binding, visibility, ".text", offset, size, IsDefinition: true);
}
