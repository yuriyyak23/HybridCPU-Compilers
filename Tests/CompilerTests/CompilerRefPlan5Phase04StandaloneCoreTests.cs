using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Native;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Tests.TestHelpers;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase04StandaloneCoreTests
{
    private static readonly string[] ForbiddenCoreDependencies =
    {
        "HybridCPU_ISE",
        "HybridCPU_Compiler",
        "LLVMSharp",
        "Microsoft.CodeAnalysis",
        "ILCompiler",
        "NativeAOT"
    };

    [Fact]
    public void CoreProject_HasNoPackageOrRuntimeProjectDependency_AndLegacyPointsInward()
    {
        string root = CompatFreezeScanner.FindCompilerRepoRoot();
        string coreProjectPath = Path.Combine(
            root,
            "Compilers",
            "HybridCPU_Compiler",
            "Core",
            "HybridCPU.Compiler.Core.csproj");
        string legacyProjectPath = Path.Combine(root, "Compilers", "HybridCPU_Compiler", "HybridCPU_Compiler.csproj");
        string nativeProjectPath = Path.Combine(
            root,
            "Compilers",
            "HybridCPU_Compiler",
            "Native",
            "HybridCPU.Compiler.Native.csproj");

        XDocument coreProject = XDocument.Load(coreProjectPath);
        Assert.Empty(coreProject.Descendants("PackageReference"));
        Assert.Single(coreProject.Descendants("ProjectReference"), reference => string.Equals(
            (string?)reference.Attribute("Include"),
            @"..\..\HybridCPU_Platform.Contracts\HybridCPU.Platform.Contracts.csproj",
            StringComparison.OrdinalIgnoreCase));

        string coreProjectText = File.ReadAllText(coreProjectPath);
        Assert.All(
            ForbiddenCoreDependencies,
            dependency => Assert.DoesNotContain(dependency, coreProjectText, StringComparison.OrdinalIgnoreCase));

        XDocument legacyProject = XDocument.Load(legacyProjectPath);
        Assert.Contains(
            legacyProject.Descendants("ProjectReference"),
            reference => string.Equals(
                (string?)reference.Attribute("Include"),
                @"Core\HybridCPU.Compiler.Core.csproj",
                StringComparison.OrdinalIgnoreCase));

        XDocument nativeProject = XDocument.Load(nativeProjectPath);
        Assert.Empty(nativeProject.Descendants("PackageReference"));
        XElement nativeReference = Assert.Single(nativeProject.Descendants("ProjectReference"));
        Assert.Equal(
            @"..\Core\HybridCPU.Compiler.Core.csproj",
            (string?)nativeReference.Attribute("Include"));
    }

    [Fact]
    public void CoreAssembly_ReferencesOnlyFrameworkAssemblies_AndLoadsInIsolation()
    {
        Assembly coreAssembly = typeof(HybridCpuCanonicalCompiler).Assembly;
        string[] references = coreAssembly.GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.All(
            ForbiddenCoreDependencies,
            dependency => Assert.DoesNotContain(
                references,
                reference => reference.Contains(dependency, StringComparison.OrdinalIgnoreCase)));

        AssemblyLoadContext loadContext = new AssemblyLoadContext("RefPlan5-Phase04-Core-Isolation", isCollectible: true);
        try
        {
            Assembly isolated = loadContext.LoadFromAssemblyPath(coreAssembly.Location);
            Assert.Equal("HybridCPU.Compiler.Core", isolated.GetName().Name);
            Assert.NotNull(isolated.GetType("HybridCPU.Compiler.Core.HybridCpuCanonicalCompiler", throwOnError: true));
            Assert.NotNull(isolated.GetType("HybridCPU.Compiler.Core.IR.HybridCpuInstructionWord", throwOnError: true));
            Assert.NotNull(isolated.GetType("HybridCPU.Compiler.Core.IR.HybridCpuCompiledProgram", throwOnError: true));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Fact]
    public void NativeApi_CompilesAllStandaloneArtifacts_InForbiddenDependencyLoadContext()
    {
        Assembly nativeAssembly = typeof(NativeAssemblyFrontend).Assembly;
        string nativeDirectory = Path.GetDirectoryName(nativeAssembly.Location)!;
        StandaloneNativeLoadContext loadContext = new StandaloneNativeLoadContext(nativeDirectory);
        try
        {
            Assembly isolated = loadContext.LoadFromAssemblyPath(nativeAssembly.Location);
            Type requestType = isolated.GetType("HybridCPU.Compiler.Native.NativeFrontendRequest", throwOnError: true)!;
            Type frontendType = isolated.GetType("HybridCPU.Compiler.Native.NativeAssemblyFrontend", throwOnError: true)!;
            object request = Activator.CreateInstance(
                requestType,
                [
                    (byte)0,
                    "ADDI r1, r0, 7\nADD r2, r1, r1",
                    null,
                    "phase04-isolated"
                ])!;
            object frontend = Activator.CreateInstance(frontendType)!;
            object result = frontendType.GetMethod("Compile", [requestType])!.Invoke(frontend, [request])!;

            Assert.Equal("Success", result.GetType().GetProperty("Status")!.GetValue(result)!.ToString());
            object artifacts = result.GetType().GetProperty("Artifacts")!.GetValue(result)!;
            Assert.NotNull(artifacts.GetType().GetProperty("CanonicalIr")!.GetValue(artifacts));
            Assert.NotNull(artifacts.GetType().GetProperty("Schedule")!.GetValue(artifacts));
            Assert.NotNull(artifacts.GetType().GetProperty("Bundles")!.GetValue(artifacts));
            Assert.NotEmpty((byte[])artifacts.GetType().GetProperty("BinaryImage")!.GetValue(artifacts)!);
            Assert.NotNull(artifacts.GetType().GetProperty("SchedulingReport")!.GetValue(artifacts));
            Assert.NotNull(artifacts.GetType().GetProperty("Provenance")!.GetValue(artifacts));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Fact]
    public void NativeProfile_IsDeterministic_AndRejectsUnknownFrontendFailClosed()
    {
        HybridCpuInstructionWord[] instructions = CreateCoreProgram();

        HybridCpuCompiledProgram first = HybridCpuCanonicalCompiler.CompileProgram(0, instructions);
        HybridCpuCompiledProgram second = HybridCpuCanonicalCompiler.CompileProgram(0, instructions);

        Assert.Equal(first.ProgramImage, second.ProgramImage);
        Assert.Equal(
            CompilerScheduleFingerprintV1.HashSchedule(first.ProgramSchedule),
            CompilerScheduleFingerprintV1.HashSchedule(second.ProgramSchedule));
        Assert.Equal(
            CompilerScheduleFingerprintV1.HashBundles(first.BundleLayout),
            CompilerScheduleFingerprintV1.HashBundles(second.BundleLayout));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HybridCpuCanonicalCompiler.CompileProgram(
                0,
                instructions,
                frontendMode: (NativeFrontendMode)byte.MaxValue));
    }

    [Fact]
    public void LegacyNativeTransportAdapter_ProducesExactCoreArtifactParity()
    {
        VLIW_Instruction[] runtimeInstructions = CreateRuntimeProgram();
        HybridCpuInstructionWord[] coreInstructions = NativeTransportRuntimeAdapter.ToCore(runtimeInstructions);

        HybridCpuCompiledProgram core = HybridCpuCanonicalCompiler.CompileProgram(0, coreInstructions);
        HybridCpuCompiledProgram adapted = NativeTransportRuntimeAdapter.CompileProgram(0, runtimeInstructions);

        Assert.Equal(core.ProgramImage, adapted.ProgramImage);
        Assert.Equal(core.ContractVersion, adapted.ContractVersion);
        Assert.Equal(
            CompilerScheduleFingerprintV1.HashSchedule(core.ProgramSchedule),
            CompilerScheduleFingerprintV1.HashSchedule(adapted.ProgramSchedule));
        Assert.Equal(
            CompilerScheduleFingerprintV1.HashBundles(core.BundleLayout),
            CompilerScheduleFingerprintV1.HashBundles(adapted.BundleLayout));
    }

    [Fact]
    public void NativeAssemblyFrontend_ExposesCompleteDeterministicArtifactProfile()
    {
        NativeFrontendRegistry registry = new NativeFrontendRegistry([new NativeCarrierFrontend(), new NativeAssemblyFrontend()]);
        NativeFrontendRequest request = new NativeFrontendRequest(
            0,
            AssemblySource: "ADDI r1, r0, 7\nADD r2, r1, r1",
            OptionsIdentity: "phase04-native-defaults");

        NativeCompilationResult first = registry.Compile("native-asm-v1", request);
        NativeCompilationResult second = registry.Compile("native-asm-v1", request);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotNull(first.Artifacts);
        Assert.NotNull(second.Artifacts);
        Assert.Equal(first.Artifacts.BinaryImage, second.Artifacts.BinaryImage);
        Assert.Equal(first.Artifacts.SchedulingReport, second.Artifacts.SchedulingReport);
        Assert.Equal(first.Artifacts.Provenance, second.Artifacts.Provenance);
        Assert.Equal("ADDI r1, r0, 7\nADD r2, r1, r1", first.Artifacts.NormalizedAssembly);
        Assert.All(
            NativeArtifactMatrix.Rows.Where(static row => row.Kind != NativeArtifactKind.ObjectFile),
            row => Assert.Equal(NativeArtifactDisposition.Available, row.Disposition));
        Assert.Equal(
            NativeArtifactDisposition.Unsupported,
            Assert.Single(NativeArtifactMatrix.Rows, static row => row.Kind == NativeArtifactKind.ObjectFile).Disposition);
    }

    [Fact]
    public void NativeFrontendRegistry_ReportsUnsupportedInvalidAndBudgetOutcomesFailClosed()
    {
        NativeFrontendRegistry registry = new NativeFrontendRegistry([new NativeCarrierFrontend(), new NativeAssemblyFrontend()]);

        Assert.Equal(
            NativeCompilationStatus.Unsupported,
            registry.Compile("missing", new NativeFrontendRequest(0)).Status);
        Assert.Equal(
            NativeCompilationStatus.Unsupported,
            registry.Compile(
                "native-asm-v1",
                new NativeFrontendRequest(0, AssemblySource: "NOT_AN_OPCODE r1, r2, r3")).Status);
        Assert.Equal(
            NativeCompilationStatus.InvalidInput,
            registry.Compile(
                "native-asm-v1",
                new NativeFrontendRequest(0, AssemblySource: "ADD r99, r1, r2")).Status);
        Assert.Equal(
            NativeCompilationStatus.BudgetExhausted,
            registry.Compile(
                "native-carrier-v1",
                new NativeFrontendRequest(
                    0,
                    InstructionWords: new HybridCpuInstructionWord[NativeCompilerFrontendBase.MaxInstructions + 1])).Status);
    }

    [Fact]
    public void CorePublicSurface_DoesNotExposeRuntimeImplementationOrPublicationTypes()
    {
        Assembly coreAssembly = typeof(HybridCpuCanonicalCompiler).Assembly;
        Type[] exportedTypes = coreAssembly.GetExportedTypes();

        Assert.DoesNotContain(
            exportedTypes,
            type => type.Namespace?.StartsWith("YAKSys_Hybrid_CPU", StringComparison.Ordinal) == true ||
                    type.Namespace?.StartsWith("HybridCPU_ISE", StringComparison.Ordinal) == true ||
                    type.Namespace?.Equals("HybridCPU.Compiler.Core.Runtime", StringComparison.Ordinal) == true);

        MethodInfo[] compilerMethods = typeof(HybridCpuCanonicalCompiler).GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(compilerMethods, method => method.Name.Contains("Emit", StringComparison.Ordinal));
        Assert.DoesNotContain(compilerMethods, method => method.Name.Contains("Publish", StringComparison.Ordinal));
        Assert.DoesNotContain(compilerMethods, method => method.Name.Contains("Commit", StringComparison.Ordinal));
        Assert.DoesNotContain(compilerMethods, method => method.Name.Contains("Retire", StringComparison.Ordinal));
    }

    private static HybridCpuInstructionWord[] CreateCoreProgram() =>
    [
        new HybridCpuInstructionWord
        {
            OpCode = (uint)HybridCpuOpcode.ADDI,
            DataTypeValue = HybridCpuDataType.INT32,
            PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(1, 0, HybridCpuInstructionWord.NoArchReg),
            Src2Pointer = 7,
            VirtualThreadId = 0
        },
        new HybridCpuInstructionWord
        {
            OpCode = (uint)HybridCpuOpcode.ADD,
            DataTypeValue = HybridCpuDataType.INT32,
            PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(2, 1, 1),
            VirtualThreadId = 0
        }
    ];

    private static VLIW_Instruction[] CreateRuntimeProgram() =>
    [
        new VLIW_Instruction
        {
            OpCode = (uint)Processor.CPU_Core.InstructionsEnum.ADDI,
            DataTypeValue = DataTypeEnum.INT32,
            PredicateMask = byte.MaxValue,
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(1, 0, VLIW_Instruction.NoArchReg),
            Src2Pointer = 7,
            VirtualThreadId = 0
        },
        new VLIW_Instruction
        {
            OpCode = (uint)Processor.CPU_Core.InstructionsEnum.ADD,
            DataTypeValue = DataTypeEnum.INT32,
            PredicateMask = byte.MaxValue,
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(2, 1, 1),
            VirtualThreadId = 0
        }
    ];

    private sealed class StandaloneNativeLoadContext(string nativeDirectory)
        : AssemblyLoadContext("RefPlan5-Phase04-Native-Isolation", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string name = assemblyName.Name ?? string.Empty;
            if (ForbiddenCoreDependencies.Any(
                    dependency => name.Contains(dependency, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FileNotFoundException($"Forbidden optional dependency '{name}' was requested.");
            }

            string candidate = Path.Combine(nativeDirectory, name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}
