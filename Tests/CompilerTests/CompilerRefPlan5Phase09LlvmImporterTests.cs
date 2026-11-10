using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Llvm;
using HybridCPU_ISE.Tests.TestHelpers;
using LLVMSharp.Interop;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase09LlvmImporterTests
{
    [Fact]
    public void ToolchainContract_IsPinnedAndBoundToVerifiedTargetContracts()
    {
        LlvmToolchainContractV1 contract = LlvmToolchainContractV1.Default;
        Assert.Equal("20.1.2", LlvmToolchainContractV1.LlvmRelease);
        Assert.Equal("20.1.2", LlvmToolchainContractV1.LlvmSharpPackageVersion);
        Assert.Equal("e-p:64:64-i8:8-i16:16-i32:32-i64:64-n8:16:32:64-S64", LlvmToolchainContractV1.HybridCpuLlvmDataLayout);
        Assert.Equal(40, LlvmToolchainContractV1.LlvmSharpRepositoryCommit.Length);
        Assert.Equal(HybridCpuTargetMachineContractV1.Default.ContractDigest, contract.CoreTargetContractDigest);
        Assert.Equal(HybridCpuTargetPlatformContractV1.Default.ContractDigest, contract.PlatformContractDigest);
        Assert.False(contract.HasBackendAuthority);
        Assert.False(contract.UsesWallClockBudget);
        Assert.Equal("7d2419c43e609a997257d5d4f540b84100c9a368e21474119b41890f9ede4541", contract.ContractDigest);
        Assert.Equal("2677b9cb3fcc2d6955aaedf9c635ae39b82de68971811263f16941a26ec237ec", contract.OptionsDigest);
        Assert.Equal("1369ddbef3d965397138e892953d3b34053bd70a95d7c03ddbbda77f33199cf1", LlvmToolchainContractV1.NativeLibrarySha256);
        Assert.Equal(contract.ContractDigest, LlvmToolchainContractV1.Default.ContractDigest);
    }

    [Fact]
    public void PublicCApiSurface_IsUniqueMachineReadableAndVersionBounded()
    {
        LlvmToolchainContractV1 contract = LlvmToolchainContractV1.Default;
        Assert.Equal(66, contract.ApiSurface.Count);
        Assert.Equal(contract.ApiSurface.Count,
            contract.ApiSurface.Select(static item => item.CApiSymbol).Distinct(StringComparer.Ordinal).Count());
        Assert.All(contract.ApiSurface, static entry =>
        {
            Assert.StartsWith("LLVM", entry.CApiSymbol, StringComparison.Ordinal);
            Assert.Equal(20, entry.MinimumLlvmMajor);
            Assert.Equal(20, entry.MaximumLlvmMajor);
            Assert.Equal(LlvmAdapterSupport.Supported, entry.Support);
            Assert.False(string.IsNullOrWhiteSpace(entry.LlvmSharpBinding));
            Assert.False(string.IsNullOrWhiteSpace(entry.FailClosedBehavior));
        });
    }

    [Fact]
    public void ApprovedCApiSurface_IsExportedByPinnedNativeLibraryAndSurfacedByBindings()
    {
        if (!PinnedRuntimeAvailable()) return;
        string nativeLibraryPath = Path.Combine(AppContext.BaseDirectory, "libLLVM.dll");
        nint library = NativeLibrary.Load(nativeLibraryPath);
        try
        {
            foreach (LlvmCApiSurfaceEntryV1 entry in LlvmToolchainContractV1.Default.ApiSurface)
            {
                Assert.True(NativeLibrary.TryGetExport(library, entry.CApiSymbol, out _),
                    $"Pinned native library does not export {entry.CApiSymbol}.");
                string[] binding = entry.LlvmSharpBinding.Split('.', 2);
                Type bindingType = typeof(LLVM).Assembly.GetType($"LLVMSharp.Interop.{binding[0]}", throwOnError: true)!;
                Assert.NotEmpty(bindingType.GetMember(binding[1],
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance));
            }
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    [Theory]
    [InlineData(20, 1, 2, LlvmAdapterSupport.Supported)]
    [InlineData(19, 1, 2, LlvmAdapterSupport.Unsupported)]
    [InlineData(21, 1, 2, LlvmAdapterSupport.Unsupported)]
    [InlineData(20, 0, 2, LlvmAdapterSupport.Unsupported)]
    [InlineData(20, 1, 1, LlvmAdapterSupport.Unsupported)]
    [InlineData(20, 1, 3, LlvmAdapterSupport.Unsupported)]
    [InlineData(0, 0, 0, LlvmAdapterSupport.Unknown)]
    public void ProducerVersionPolicy_FailsClosed(int major, int minor, int patch, LlvmAdapterSupport expected) =>
        Assert.Equal(expected, LlvmToolchainContractV1.Default.ValidateProducerVersion(major, minor, patch));

    [Theory]
    [InlineData("", "e-p:64:64-i8:8-i16:16-i32:32-i64:64-n8:16:32:64-S64", "LLVM 20.1.2", LlvmImportStatus.UnknownSemantics, "HCLL0009")]
    [InlineData("x86_64-pc-windows", "e-p:64:64-i8:8-i16:16-i32:32-i64:64-n8:16:32:64-S64", "LLVM 20.1.2", LlvmImportStatus.Unsupported, "HCLL0010")]
    [InlineData("hybridcpuv2-unknown-none", "", "LLVM 20.1.2", LlvmImportStatus.UnknownSemantics, "HCLL0011")]
    [InlineData("hybridcpuv2-unknown-none", "e-p:32:32", "LLVM 20.1.2", LlvmImportStatus.Unsupported, "HCLL0012")]
    [InlineData("hybridcpuv2-unknown-none", "e-p:64:64-i8:8-i16:16-i32:32-i64:64-n8:16:32:64-S64", "", LlvmImportStatus.UnknownSemantics, "HCLL0020")]
    [InlineData("hybridcpuv2-unknown-none", "e-p:64:64-i8:8-i16:16-i32:32-i64:64-n8:16:32:64-S64", "LLVM 21.1.0", LlvmImportStatus.Unsupported, "HCLL0021")]
    public void HeaderValidation_RejectsHostDefaultsAndVersionSkew(
        string triple, string dataLayout, string producer, LlvmImportStatus status, string code)
    {
        LlvmImportResultV1 result = new LlvmModuleImporterV1().ValidateHeader(new(triple, dataLayout, producer), "module.ll");
        Assert.Equal(status, result.Status);
        Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
        Assert.Null(result.Module);
    }

    [Fact]
    public void HeaderValidation_AcceptsOnlyExactPinnedContractDeterministically()
    {
        LlvmModuleHeaderV1 header = new LlvmModuleHeaderV1(
            HybridCpuTargetMachineContractV1.TargetTriple,
            LlvmToolchainContractV1.HybridCpuLlvmDataLayout,
            "clang version 20.1.2");
        LlvmModuleImporterV1 importer = new LlvmModuleImporterV1();
        Assert.Equal(importer.ValidateHeader(header), importer.ValidateHeader(header));
        Assert.Equal(LlvmImportStatus.Success, importer.ValidateHeader(header).Status);
    }

    [Fact]
    public void LlvmPackagesAndTypes_AreConfinedToOptionalAdapterProject()
    {
        string root = CompatFreezeScanner.FindCompilerRepoRoot();
        string core = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "Core", "HybridCPU.Compiler.Core.csproj"));
        string native = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "Native", "HybridCPU.Compiler.Native.csproj"));
        XDocument legacy = XDocument.Load(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "HybridCPU_Compiler.csproj"));
        XDocument adapter = XDocument.Load(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "LLVM", "HybridCPU.Compiler.Llvm.csproj"));

        Assert.DoesNotContain("LLVMSharp", core, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LLVMSharp", native, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(legacy.Descendants("PackageReference"), static reference =>
            ((string?)reference.Attribute("Include"))?.Contains("LLVM", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(legacy.Descendants("Compile"), static item =>
            string.Equals((string?)item.Attribute("Remove"), @"LLVM\**\*.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["LLVMSharp", "LLVMSharp.Interop"], adapter.Descendants("PackageReference")
            .Select(static reference => (string)reference.Attribute("Include")!)
            .OrderBy(static value => value, StringComparer.Ordinal).ToArray());
        XElement reference = Assert.Single(adapter.Descendants("ProjectReference"));
        Assert.Equal(@"..\Core\HybridCPU.Compiler.Core.csproj", (string?)reference.Attribute("Include"));
    }

    [Theory]
    [InlineData(null, LlvmInputKind.TextIr, "HCLL0001")]
    [InlineData("module.bc", LlvmInputKind.TextIr, "HCLL0004")]
    [InlineData("module.ll", LlvmInputKind.Bitcode, "HCLL0004")]
    [InlineData("missing.ll", LlvmInputKind.TextIr, "HCLL0005")]
    public void InvalidIngress_IsRejectedBeforeNativeRuntimeProbe(string? path, LlvmInputKind kind, string code)
    {
        LlvmImportResultV1 result = new LlvmModuleImporterV1().ImportFile(path!, kind);
        Assert.Equal(LlvmImportStatus.InvalidInput, result.Status);
        Assert.Null(result.Module);
        Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void RuntimeProbe_IsDeterministicAndNeverThrowsWhenOptionalRuntimeIsAbsent()
    {
        LlvmModuleImporterV1 importer = new LlvmModuleImporterV1();
        LlvmRuntimeAvailabilityV1 first = importer.ProbeRuntime();
        LlvmRuntimeAvailabilityV1 second = importer.ProbeRuntime();
        Assert.Equal(first, second);
        Assert.Equal(first.IsAvailable ? "HCLL1000" : "HCLL0002", first.DiagnosticCode);
    }

    [Fact]
    public void PinnedTextIr_ImportsReproduciblyThroughRealLlvmCApi()
    {
        if (!PinnedRuntimeAvailable()) return;
        string path = CreateTempPath("ll");
        try
        {
            File.WriteAllText(path, SupportedModuleText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            LlvmModuleImporterV1 importer = new LlvmModuleImporterV1();
            LlvmImportResultV1 first = importer.ImportFile(path, LlvmInputKind.TextIr);
            LlvmImportResultV1 second = importer.ImportFile(path, LlvmInputKind.TextIr);

            Assert.Equal(first.Status, second.Status);
            Assert.Equal(first.Module, second.Module);
            Assert.Equal(first.Diagnostics, second.Diagnostics);
            Assert.Equal(LlvmImportStatus.Success, first.Status);
            LlvmModuleSummaryV1 module = Assert.IsType<LlvmModuleSummaryV1>(first.Module);
            Assert.Empty(first.Diagnostics);
            Assert.Equal("LLVM 20.1.2", module.ModuleProducer);
            Assert.Equal(HybridCpuTargetMachineContractV1.TargetTriple, module.TargetTriple);
            Assert.Equal(LlvmToolchainContractV1.HybridCpuLlvmDataLayout, module.DataLayout);
            Assert.Equal(1, module.FunctionCount);
            Assert.Equal(1, module.BasicBlockCount);
            Assert.Equal(1, module.InstructionCount);
            Assert.Equal(LlvmToolchainContractV1.Default.ContractDigest, module.Provenance.ToolchainContractDigest);
            Assert.Equal(LlvmToolchainContractV1.Default.OptionsDigest, module.Provenance.OptionsDigest);
            Assert.Equal(LlvmToolchainContractV1.NativeLibrarySha256, module.Provenance.NativeLibrarySha256);
            Assert.Equal(module.InputDigest, module.Provenance.InputDigest);
            Assert.Equal(module.ModuleProducer, module.Provenance.ModuleProducer);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PinnedBitcode_ImportsWithStructuralParityToTextIr()
    {
        if (!PinnedRuntimeAvailable()) return;
        string textPath = CreateTempPath("ll");
        string bitcodePath = CreateTempPath("bc");
        try
        {
            File.WriteAllText(textPath, SupportedModuleText, new UTF8Encoding(false));
            AssembleBitcode(textPath, bitcodePath);
            LlvmModuleImporterV1 importer = new LlvmModuleImporterV1();
            LlvmImportResultV1 textResult = importer.ImportFile(textPath, LlvmInputKind.TextIr);
            LlvmImportResultV1 bitcodeResult = importer.ImportFile(bitcodePath, LlvmInputKind.Bitcode);

            Assert.Equal(LlvmImportStatus.Success, bitcodeResult.Status);
            LlvmModuleSummaryV1 text = Assert.IsType<LlvmModuleSummaryV1>(textResult.Module);
            LlvmModuleSummaryV1 bitcode = Assert.IsType<LlvmModuleSummaryV1>(bitcodeResult.Module);
            Assert.NotEqual(text.InputDigest, bitcode.InputDigest);
            Assert.Equal(text with
            {
                InputDigest = bitcode.InputDigest,
                Provenance = text.Provenance with { InputDigest = bitcode.InputDigest }
            }, bitcode);
        }
        finally
        {
            File.Delete(textPath);
            File.Delete(bitcodePath);
        }
    }

    [Theory]
    [MemberData(nameof(RealLlvmNegativeModules))]
    public void RealLlvmValidation_FailsClosedWithStableDiagnostic(string moduleText, string expectedCode)
    {
        if (!PinnedRuntimeAvailable()) return;
        string path = CreateTempPath("ll");
        try
        {
            File.WriteAllText(path, moduleText, new UTF8Encoding(false));
            LlvmModuleImporterV1 importer = new LlvmModuleImporterV1();
            LlvmImportResultV1 first = importer.ImportFile(path, LlvmInputKind.TextIr);
            LlvmImportResultV1 second = importer.ImportFile(path, LlvmInputKind.TextIr);
            Assert.Equal(first.Status, second.Status);
            Assert.Equal(first.Module, second.Module);
            Assert.Equal(first.Diagnostics.Select(static item => (item.Status, item.Code, item.Message, item.StableSourceIdentity)),
                second.Diagnostics.Select(static item => (item.Status, item.Code, item.Message, item.StableSourceIdentity)));
            Assert.NotEqual(LlvmImportStatus.Success, first.Status);
            Assert.Null(first.Module);
            Assert.Equal(expectedCode, Assert.Single(first.Diagnostics).Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static TheoryData<string, string> RealLlvmNegativeModules => new()
    {
        { "not valid llvm ir", "HCLL0007" },
        { SupportedModuleText.Replace(HybridCpuTargetMachineContractV1.TargetTriple, "x86_64-pc-windows", StringComparison.Ordinal), "HCLL0010" },
        { SupportedModuleText.Replace(LlvmToolchainContractV1.HybridCpuLlvmDataLayout, "e-p:32:32", StringComparison.Ordinal), "HCLL0012" },
        { SupportedModuleText.Replace("LLVM 20.1.2", "LLVM 21.1.0", StringComparison.Ordinal), "HCLL0021" },
        { SupportedModuleText.Replace("LLVM 20.1.2", "LLVM 20.1.20", StringComparison.Ordinal), "HCLL0021" },
        { SupportedModuleText + "\n!unknown.metadata = !{!0}\n", "HCLL0018" },
        { SupportedModuleText.Replace("ret void", "%x = add i32 1, 2\n  ret void", StringComparison.Ordinal), "HCLL0014" },
        { SupportedModuleText.Replace("define void @kernel()", "declare void @llvm.donothing()\n\ndefine void @kernel()", StringComparison.Ordinal).Replace("ret void", "call void @llvm.donothing()\n  ret void", StringComparison.Ordinal), "HCLL0016" },
        { SupportedModuleText.Replace("define void @kernel()", "define void @kernel(i32 %arg)", StringComparison.Ordinal), "HCLL0023" },
        { SupportedModuleText.Replace("define void @kernel()", "define void @kernel(ptr addrspace(1) %arg)", StringComparison.Ordinal), "HCLL0024" },
        { SupportedModuleText.Replace("define void @kernel()", "define void @kernel(i24 %arg)", StringComparison.Ordinal), "HCLL0026" },
        { SupportedModuleText.Replace("define void @kernel()", "define void @kernel() nounwind", StringComparison.Ordinal), "HCLL0023" },
        { SupportedModuleText.Replace("define void @kernel()", "@g = global i32 0\n\ndefine void @kernel()", StringComparison.Ordinal), "HCLL0022" }
    };

    [Fact]
    public void ExistingFile_FailsUnavailableWithoutCreatingCanonicalIrWhenRuntimeIsAbsent()
    {
        LlvmRuntimeAvailabilityV1 availability = new LlvmModuleImporterV1().ProbeRuntime();
        if (availability.IsAvailable) return;

        string path = Path.Combine(Path.GetTempPath(), $"hybridcpu-phase09-{Guid.NewGuid():N}.ll");
        try
        {
            File.WriteAllText(path, "target triple = \"hybridcpuv2-unknown-none\"\n");
            LlvmImportResultV1 result = new LlvmModuleImporterV1().ImportFile(path, LlvmInputKind.TextIr);
            Assert.Equal(LlvmImportStatus.Unavailable, result.Status);
            Assert.Null(result.Module);
            Assert.Equal("HCLL0002", Assert.Single(result.Diagnostics).Code);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private const string SupportedModuleText = """
        target datalayout = "e-p:64:64-i8:8-i16:16-i32:32-i64:64-n8:16:32:64-S64"
        target triple = "hybridcpuv2-unknown-none"

        define void @kernel() {
        entry:
          ret void
        }

        !llvm.ident = !{!0}
        !0 = !{!"LLVM 20.1.2"}
        """;

    private static string CreateTempPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"hybridcpu-phase09-{Guid.NewGuid():N}.{extension}");

    private static bool PinnedRuntimeAvailable()
    {
        LlvmRuntimeAvailabilityV1 availability = new LlvmModuleImporterV1().ProbeRuntime();
        if (!availability.IsAvailable)
            return false; // Optional native runtime absence is covered by unavailable-runtime tests.
        Assert.Equal((20, 1, 2), (availability.Major, availability.Minor, availability.Patch));
        Assert.Equal(LlvmToolchainContractV1.NativeLibrarySha256, availability.NativeLibrarySha256);
        return true;
    }

    private static unsafe void AssembleBitcode(string textPath, string bitcodePath)
    {
        byte[] pathBytes = Encoding.UTF8.GetBytes(Path.GetFullPath(textPath) + '\0');
        LLVMContextRef context = default;
        LLVMMemoryBufferRef buffer = default;
        LLVMModuleRef module = default;
        sbyte* message = null;
        try
        {
            fixed (byte* pathPointer = pathBytes)
            {
                LLVMOpaqueMemoryBuffer* rawBuffer = null;
                Assert.Equal(0, LLVM.CreateMemoryBufferWithContentsOfFile((sbyte*)pathPointer, &rawBuffer, &message));
                buffer = rawBuffer;
            }
            context = LLVMContextRef.Create();
            bool parsed = context.TryParseIR(buffer, out module, out string parseMessage);
            buffer = default;
            Assert.True(parsed, parseMessage);
            Assert.Equal(0, module.WriteBitcodeToFile(bitcodePath));
        }
        finally
        {
            if (message != null) LLVM.DisposeMessage(message);
            if (module != default) module.Dispose();
            if (context != default) context.Dispose();
            if (buffer != default) LLVM.DisposeMemoryBuffer(buffer);
        }
    }
}
