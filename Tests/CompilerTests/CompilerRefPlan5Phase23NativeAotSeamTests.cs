using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Matrix;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU.Compiler.NativeAot;
using HybridCPU_ISE.Tests.TestHelpers;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Descriptors;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase23NativeAotSeamTests
{
    private static readonly string FixtureAssembly = typeof(RestrictedCilCSharpFixtures).Assembly.Location;
    private const string FixtureType = "HybridCPU_ISE.Tests.CompilerTests.RestrictedCilCSharpFixtures";

    [Fact]
    public void BaselineAndPatch_AreExactAndDriftChecked()
    {
        string root = FindRepositoryRoot();
        string patch = Path.Combine(root, "Compilers", "HybridCPU_Compiler", "NativeAot", "Patches", "0001-hybridcpu-phase23-single-method-seam.patch");
        string map = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "NativeAot", "PHASE23_TARGET_ENABLEMENT_MAP.md"));

        Assert.Equal("94ea82652cdd4e0f8046b5bd5becbd11461482ca", NativeAotSeamBaselineV1.SourceCommit);
        Assert.Equal("10.0.105", NativeAotSeamBaselineV1.SdkVersion);
        Assert.Equal(NativeAotSeamBaselineV1.PatchDigest,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(patch))).ToLowerInvariant());
        for (int item = 1; item <= 10; item++) Assert.Contains($"## {item}.", map, StringComparison.Ordinal);
        Assert.Contains("ComputeMarkedNodes", map, StringComparison.Ordinal);
        Assert.Contains("ObjectWriter", map, StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterProject_DependsOnlyOnCilAndCore_AndCoreHasNoIlCompilerLeak()
    {
        string root = FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "NativeAot", "HybridCPU.Compiler.NativeAot.Adapter.csproj"));
        string core = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "Core", "HybridCPU.Compiler.Core.csproj"));
        string legacy = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "HybridCPU_Compiler.csproj"));

        Assert.Contains("..\\Cil\\HybridCPU.Compiler.Cil.csproj", project, StringComparison.Ordinal);
        Assert.Contains("..\\Core\\HybridCPU.Compiler.Core.csproj", project, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);
        Assert.DoesNotContain("ILCompiler", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Internal.TypeSystem", project, StringComparison.Ordinal);
        Assert.DoesNotContain("ILCompiler", core, StringComparison.Ordinal);
        Assert.Contains("Compile Remove=\"NativeAot\\**\\*.cs\"", legacy, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(IrProgram).Assembly.GetReferencedAssemblies(),
            static reference => reference.Name?.Contains("ILCompiler", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void QualifiedMethod_ProducesDeterministicTargetPureArtifactAndManifest()
    {
        using AdapterRun first = RunAdapter(nameof(RestrictedCilCSharpFixtures.Add));
        using AdapterRun second = RunAdapter(nameof(RestrictedCilCSharpFixtures.Add));

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(first.Code, second.Code);
        Assert.NotEmpty(first.Code);
        Assert.Equal(0, first.Code.Length % 32);
        Assert.Equal(JsonSerializer.Serialize(first.Artifact), JsonSerializer.Serialize(second.Artifact));
        Assert.Equal(NativeAotSeamStatusV1.Success, first.Artifact!.Status);
        Assert.Empty(first.Artifact.Relocations);
        Assert.Empty(first.Artifact.FrameRecords);
        Assert.Null(first.Artifact.GcInfo);
        Assert.Null(first.Artifact.EhInfo);
        Assert.False(first.Artifact.HasRuntimeAuthority);
        Assert.False(first.Artifact.HasPublicationAuthority);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(first.Code)).ToLowerInvariant(), first.Artifact.CodeSha256);

        RestrictedCilImportResultV1 imported = new RestrictedCilImporterV1().ImportFile(FixtureAssembly,
            new(FixtureType, nameof(RestrictedCilCSharpFixtures.Add)));
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(Assert.IsType<IrProgram>(imported.Program));
        byte[] direct = new HybridCpuBundleSerializer().SerializeProgram(
            new HybridCpuBundleLowerer().LowerProgram(new HybridCpuBundleFormer().BundleProgram(schedule)));
        Assert.Equal(direct, first.Code);
    }

    [Fact]
    public void QualifiedInlineHelper_UsesTheExistingRestrictedCilAndCorePipeline()
    {
        using AdapterRun result = RunAdapter(nameof(RestrictedCilCSharpFixtures.CallIdentity));

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.Code);
        Assert.Equal(NativeAotSeamStatusV1.Success, result.Artifact!.Status);
        Assert.Contains(nameof(RestrictedCilCSharpFixtures.CallIdentity), result.Artifact.MethodIdentity, StringComparison.Ordinal);
        Assert.Empty(result.Artifact.Relocations);
    }

    [Theory]
    [InlineData(nameof(RestrictedCilCSharpFixtures.GenericIdentity), "HCCIL1012")]
    [InlineData(nameof(RestrictedCilCSharpFixtures.TryCatch), "HCCIL1011")]
    [InlineData(nameof(RestrictedCilCSharpFixtures.StringLength), "HCCIL1001")]
    [InlineData(nameof(RestrictedCilCSharpFixtures.CallUnknown), "HCCIL1474")]
    public void UnsupportedNodeFamilies_FailClosedBeforeArtifactEmission(string method, string diagnostic)
    {
        using AdapterRun result = RunAdapter(method);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains(diagnostic, result.StandardError, StringComparison.Ordinal);
        Assert.Empty(result.Code);
        Assert.Null(result.Artifact);
    }

    [Fact]
    public void VersionSkew_FailsDeterministicallyBeforeReadingMethod()
    {
        using AdapterRun first = RunAdapter(nameof(RestrictedCilCSharpFixtures.Add), sourceCommit: new string('0', 40));
        using AdapterRun second = RunAdapter(nameof(RestrictedCilCSharpFixtures.Add), sourceCommit: new string('0', 40));

        Assert.Equal(3, first.ExitCode);
        Assert.Equal(first.ExitCode, second.ExitCode);
        Assert.Equal(first.StandardError, second.StandardError);
        Assert.Contains("HCNAOT1001", first.StandardError, StringComparison.Ordinal);
        Assert.Empty(first.Code);
    }

    [Fact]
    public void PatchExplicitlyBypassesHostObjectWriterOnlyInClosedSeamMode()
    {
        string root = FindRepositoryRoot();
        string patch = File.ReadAllText(Path.Combine(root, "Compilers", "HybridCPU_Compiler", "NativeAot", "Patches", "0001-hybridcpu-phase23-single-method-seam.patch"));

        Assert.Contains("CompileHybridCpuMethod(methodCodeNodeNeedingCode)", patch, StringComparison.Ordinal);
        Assert.Contains("methodNode.SetCode", patch, StringComparison.Ordinal);
        Assert.Contains("File.WriteAllBytes(outputFile, _hybridCpuCode)", patch, StringComparison.Ordinal);
        Assert.Contains("if (_hybridCpuSeam != null)", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("-            ObjectWriter.ObjectWriter.EmitObject", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopwatch", patch, StringComparison.Ordinal);
    }

    [Fact]
    public void SingNextPclMaterializer_DerivesExactDisjointMemoryFactsFromValidatedCanonicalIr()
    {
        using PclMaterializationRun run = MaterializePcl(ExactMemoryProgram());

        Assert.True(run.Success, run.Diagnostic);
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(run.ManifestPath));
        JsonElement root = manifest.RootElement;
        JsonElement[] footprints = root.GetProperty("Footprints").EnumerateArray().ToArray();
        Assert.Equal(2, footprints.Length);
        Assert.Contains(footprints, static row => row.GetProperty("Symbol").GetString() == "memory-0-0000000000001000-8" &&
            row.GetProperty("Access").GetByte() == 1);
        Assert.Contains(footprints, static row => row.GetProperty("Symbol").GetString() == "memory-0-0000000000002000-8" &&
            row.GetProperty("Access").GetByte() == 2);
        JsonElement alias = Assert.Single(root.GetProperty("AliasFacts").EnumerateArray());
        Assert.True(alias.GetProperty("Disjoint").GetBoolean());
        Assert.True(File.Exists(run.InputIrPath));
    }

    [Fact]
    public void SingNextPclMaterializer_IncompleteMemoryFootprintFailsClosedAndDeletesInputs()
    {
        IrProgram program = ExactMemoryProgram();
        IrInstruction read = program.Instructions[0] with
        {
            Annotation = program.Instructions[0].Annotation with { MemoryReadRegion = null },
            SideEffects = program.Instructions[0].SideEffects with
            {
                Memory = program.Instructions[0].SideEffects.Memory with { ReadRegion = null }
            }
        };
        using PclMaterializationRun run = MaterializePcl(program with
        {
            Instructions = [read, .. program.Instructions.Skip(1)]
        });

        Assert.False(run.Success);
        Assert.Contains("HCNAOT4102", run.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("complete exact read/write regions", run.Diagnostic, StringComparison.Ordinal);
        Assert.False(File.Exists(run.ManifestPath));
        Assert.False(File.Exists(run.InputIrPath));
    }

    [Fact]
    public void SingNextPclMaterializer_DescriptorBearingIrFailsClosedAndDeletesInputs()
    {
        IrProgram program = ExactMemoryProgram();
        IrInstruction descriptor = program.Instructions[0] with { DmaStreamComputeDescriptor = new object() };
        using PclMaterializationRun run = MaterializePcl(program with
        {
            Instructions = [descriptor, .. program.Instructions.Skip(1)]
        });

        Assert.False(run.Success);
        Assert.Contains("HCNAOT4102", run.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("descriptor-bearing IR lacks", run.Diagnostic, StringComparison.Ordinal);
        Assert.False(File.Exists(run.ManifestPath));
        Assert.False(File.Exists(run.InputIrPath));
    }

    [Fact]
    public void SingNextPclMaterializer_SnapshotsValidatedDscExactFootprintProjection()
    {
        using PclMaterializationRun run = MaterializePcl(DscDescriptorProgram());

        Assert.True(run.Success, run.Diagnostic);
        AssertDescriptorEvidence(run, "hybridcpu.dsc-exact-footprint/v1");
    }

    [Fact]
    public void SingNextPclMaterializer_SnapshotsValidatedL7ExactFootprintProjection()
    {
        using PclMaterializationRun run = MaterializePcl(L7DescriptorProgram());

        Assert.True(run.Success, run.Diagnostic);
        AssertDescriptorEvidence(run, "hybridcpu.l7-exact-footprint/v1");
    }

    [Fact]
    public void SingNextPclMaterializer_TamperedDescriptorProjectionFailsClosed()
    {
        IrProgram program = DscDescriptorProgram();
        IrInstruction instruction = Assert.Single(program.Instructions);
        IrExternalDescriptorEvidenceV1 evidence = Assert.IsType<IrExternalDescriptorEvidenceV1>(
            instruction.ExternalDescriptorEvidence);

        using PclMaterializationRun run = MaterializePcl(program with
        {
            Instructions = [instruction with
            {
                ExternalDescriptorEvidence = evidence with { AllOrNone = false }
            }]
        });

        Assert.False(run.Success);
        Assert.Contains("HCNAOT4102", run.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("exact all-or-none compiler projection", run.Diagnostic, StringComparison.Ordinal);
        Assert.False(File.Exists(run.ManifestPath));
        Assert.False(File.Exists(run.InputIrPath));
    }

    [Fact]
    public void SingNextPclMaterializer_DescriptorRangeSubstitutionChangesStaticFacts()
    {
        IrProgram program = DscDescriptorProgram();
        IrInstruction instruction = Assert.Single(program.Instructions);
        IrExternalDescriptorEvidenceV1 evidence = Assert.IsType<IrExternalDescriptorEvidenceV1>(
            instruction.ExternalDescriptorEvidence);
        IrMemoryRegion firstRead = evidence.ReadRegions[0];
        Assert.True(firstRead.Length > 1);
        using PclMaterializationRun original = MaterializePcl(program);
        using PclMaterializationRun substituted = MaterializePcl(program with
        {
            Instructions = [instruction with
            {
                ExternalDescriptorEvidence = evidence with
                {
                    ReadRegions = [firstRead with { Length = firstRead.Length - 1 },
                        .. evidence.ReadRegions.Skip(1)]
                }
            }]
        });

        Assert.True(original.Success, original.Diagnostic);
        Assert.True(substituted.Success, substituted.Diagnostic);
        Assert.NotEqual(File.ReadAllText(original.ManifestPath),
            File.ReadAllText(substituted.ManifestPath));
        Assert.NotEqual(File.ReadAllText(original.InputIrPath),
            File.ReadAllText(substituted.InputIrPath));
    }

    [Fact]
    public void SingNextPclMaterializer_SnapshotsValidatedVectorTransferLoweringSideband()
    {
        IrProgram program = VectorTransferProgram();

        using PclMaterializationRun first = MaterializePcl(program);
        using PclMaterializationRun second = MaterializePcl(program);

        Assert.True(first.Success, first.Diagnostic);
        Assert.True(second.Success, second.Diagnostic);
        Assert.Equal(File.ReadAllBytes(first.ManifestPath), File.ReadAllBytes(second.ManifestPath));
        Assert.Equal(File.ReadAllBytes(first.InputIrPath), File.ReadAllBytes(second.InputIrPath));
        using JsonDocument snapshot = JsonDocument.Parse(File.ReadAllBytes(first.InputIrPath));
        JsonElement sideband = snapshot.RootElement.GetProperty("Program").GetProperty("Instructions")[0]
            .GetProperty("TypedLoweringSideband");
        Assert.Equal("hybridcpu.vector-transfer-typed-lowering/v1",
            sideband.GetProperty("SchemaId").GetString());
        Assert.False(sideband.GetProperty("UsesFallbackPath").GetBoolean());
    }

    [Fact]
    public void SingNextPclMaterializer_SnapshotsValidatedMatrixTileMemorySideband()
    {
        IrProgram program = MatrixTileMemoryProgram();

        using PclMaterializationRun first = MaterializePcl(program);
        using PclMaterializationRun second = MaterializePcl(program);

        Assert.True(first.Success, first.Diagnostic);
        Assert.True(second.Success, second.Diagnostic);
        Assert.Equal(File.ReadAllBytes(first.ManifestPath), File.ReadAllBytes(second.ManifestPath));
        Assert.Equal(File.ReadAllBytes(first.InputIrPath), File.ReadAllBytes(second.InputIrPath));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(first.ManifestPath));
        JsonElement[] footprints = manifest.RootElement.GetProperty("Footprints").EnumerateArray().ToArray();
        Assert.Contains(footprints, static row => row.GetProperty("Access").GetByte() == 1);
        Assert.Contains(footprints, static row => row.GetProperty("Access").GetByte() == 2);
        using JsonDocument snapshot = JsonDocument.Parse(File.ReadAllBytes(first.InputIrPath));
        JsonElement[] instructions = snapshot.RootElement.GetProperty("Program").GetProperty("Instructions")
            .EnumerateArray().ToArray();
        Assert.Equal(4, instructions.Length);
        Assert.All(instructions,
            static instruction => Assert.Equal("hybridcpu.matrix-tile-typed-lowering/v1",
                instruction.GetProperty("TypedLoweringSideband").GetProperty("SchemaId").GetString()));
        Assert.Equal(2, instructions.Count(static instruction =>
            instruction.GetProperty("SideEffects").GetProperty("Memory").GetProperty("Kind").GetByte() == 0));
    }

    [Fact]
    public void SingNextPclMaterializer_TamperedMatrixTileExactMemoryFailsClosed()
    {
        IrProgram program = MatrixTileMemoryProgram();
        IrInstruction load = program.Instructions[0];
        IrMemoryRegion region = Assert.IsType<IrMemoryRegion>(load.Annotation.MemoryReadRegion);
        IrInstruction tampered = load with
        {
            Annotation = load.Annotation with { MemoryReadRegion = region with { Address = region.Address + 1 } },
            SideEffects = load.SideEffects with
            {
                Memory = load.SideEffects.Memory with { ReadRegion = region with { Address = region.Address + 1 } }
            }
        };

        using PclMaterializationRun run = MaterializePcl(program with
        {
            Instructions = [tampered, .. program.Instructions.Skip(1)]
        });

        Assert.False(run.Success);
        Assert.Contains("HCNAOT4102", run.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("matrix-tile memory lowering sideband", run.Diagnostic, StringComparison.Ordinal);
        Assert.False(File.Exists(run.ManifestPath));
        Assert.False(File.Exists(run.InputIrPath));
    }

    [Fact]
    public void SingNextPclMaterializer_MatrixTileComputeWithExternalRegionFailsClosed()
    {
        IrProgram program = MatrixTileMemoryProgram();
        IrInstruction macc = program.Instructions[2];
        IrMemoryRegion injected = new IrMemoryRegion(0x6000, 4, false);
        IrInstruction tampered = macc with
        {
            Annotation = macc.Annotation with { MemoryReadRegion = injected },
            SideEffects = macc.SideEffects with
            {
                Memory = macc.SideEffects.Memory with { ReadRegion = injected }
            }
        };

        using PclMaterializationRun run = MaterializePcl(program with
        {
            Instructions = [.. program.Instructions.Take(2), tampered, program.Instructions[3]]
        });

        Assert.False(run.Success);
        Assert.Contains("HCNAOT4102", run.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("unexpectedly carries external memory regions", run.Diagnostic, StringComparison.Ordinal);
        Assert.False(File.Exists(run.ManifestPath));
        Assert.False(File.Exists(run.InputIrPath));
    }

    [Fact]
    public void SingNextPclMaterializer_TamperedTypedLoweringSidebandFailsClosedAndDeletesInputs()
    {
        IrProgram program = VectorTransferProgram();
        IrInstruction instruction = Assert.Single(program.Instructions);
        CompilerVectorTransferEmissionPlan vector = Assert.IsType<CompilerVectorTransferEmissionPlan>(
            instruction.VectorTransferEmission);
        IrProgram tampered = program with
        {
            Instructions = [instruction with
            {
                VectorTransferEmission = vector with { UsesFallbackPath = true }
            }]
        };

        using PclMaterializationRun run = MaterializePcl(tampered);

        Assert.False(run.Success);
        Assert.Contains("HCNAOT4102", run.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("vector-transfer typed lowering sideband", run.Diagnostic, StringComparison.Ordinal);
        Assert.False(File.Exists(run.ManifestPath));
        Assert.False(File.Exists(run.InputIrPath));
    }

    [Fact]
    public void SingNextPclGraphMaterializer_NamespacesFactsAndAggregatesBoundsDeterministically()
    {
        ManagedCallGraphCompilationV1 graph = ImportManagedGraph();
        Assert.Equal(RestrictedCilImportStatusV1.Success, graph.Status);
        Assert.NotNull(graph.Graph);
        Assert.Equal(5, graph.Methods.Count);

        using PclMaterializationRun first = MaterializePclGraph(graph.Methods, graph.Graph!);
        using PclMaterializationRun second = MaterializePclGraph(
            graph.Methods.Reverse().ToArray(), graph.Graph!);

        Assert.True(first.Success, first.Diagnostic);
        Assert.True(second.Success, second.Diagnostic);
        Assert.Equal(File.ReadAllBytes(first.ManifestPath), File.ReadAllBytes(second.ManifestPath));
        Assert.Equal(File.ReadAllBytes(first.InputIrPath), File.ReadAllBytes(second.InputIrPath));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(first.ManifestPath));
        JsonElement root = manifest.RootElement;
        Assert.Equal("hybridcpu-nativeaot-canonical-ir-graph-v1",
            root.GetProperty("CompilerContractVersion").GetString());
        JsonElement[] safePoints = root.GetProperty("SafePointMap").EnumerateArray().ToArray();
        Assert.NotEmpty(safePoints);
        Assert.All(safePoints, static fact =>
            Assert.StartsWith("m-", fact.GetProperty("SafePointIdentity").GetString(), StringComparison.Ordinal));
        Assert.Equal(safePoints.Length, safePoints.Select(static fact =>
            fact.GetProperty("SafePointIdentity").GetString()).Distinct(StringComparer.Ordinal).Count());
        JsonElement[] estimates = root.GetProperty("StaticResourceEstimates").EnumerateArray().ToArray();
        Assert.Equal(3, estimates.Length);
        Assert.All(estimates, static estimate => Assert.True(estimate.GetProperty("UpperBound").GetUInt64() > 0));

        using JsonDocument snapshot = JsonDocument.Parse(File.ReadAllBytes(first.InputIrPath));
        Assert.Equal("hybridcpu.singnext-pcl-canonical-ir-graph/v1",
            snapshot.RootElement.GetProperty("SchemaId").GetString());
        Assert.Equal(5, snapshot.RootElement.GetProperty("Methods").GetArrayLength());
        Assert.Equal(graph.Graph!.GraphDigest.ToLowerInvariant(),
            snapshot.RootElement.GetProperty("GraphDigest").GetString());
    }

    [Fact]
    public void SingNextPclGraphMaterializer_DescriptorBearingMethodFailsClosedAndDeletesInputs()
    {
        ManagedCallGraphCompilationV1 graph = ImportManagedGraph();
        ManagedCompiledMethodV1 target = graph.Methods[0];
        IrProgram program = Assert.IsType<IrProgram>(target.Import.Program);
        IrInstruction descriptor = program.Instructions[0] with { DmaStreamComputeDescriptor = new object() };
        ManagedCompiledMethodV1 mutated = target with
        {
            Import = target.Import with
            {
                Program = program with { Instructions = [descriptor, .. program.Instructions.Skip(1)] }
            }
        };
        ManagedCompiledMethodV1[] methods = [mutated, .. graph.Methods.Skip(1)];

        using PclMaterializationRun run = MaterializePclGraph(methods, graph.Graph!);

        Assert.False(run.Success);
        Assert.Contains("HCNAOT4103", run.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("descriptor-bearing IR", run.Diagnostic, StringComparison.Ordinal);
        Assert.False(File.Exists(run.ManifestPath));
        Assert.False(File.Exists(run.InputIrPath));
    }

    private static IrProgram ExactMemoryProgram()
    {
        HybridCpuInstructionWord load = default;
        load.OpCode = (uint)HybridCpuOpcode.LD;
        load.DataTypeValue = HybridCpuDataType.INT64;
        load.PredicateMask = byte.MaxValue;
        load.Word1 = HybridCpuInstructionWord.PackArchRegs(1, 2, HybridCpuInstructionWord.NoArchReg);
        HybridCpuInstructionWord store = default;
        store.OpCode = (uint)HybridCpuOpcode.SD;
        store.DataTypeValue = HybridCpuDataType.INT64;
        store.PredicateMask = byte.MaxValue;
        store.Word1 = HybridCpuInstructionWord.PackArchRegs(HybridCpuInstructionWord.NoArchReg, 2, 1);
        IrProgram program = new HybridCpuIrBuilder().BuildProgram(0, [load, store]);
        IrMemoryRegion readRegion = new IrMemoryRegion(0x1000, 8, false);
        IrMemoryRegion writeRegion = new IrMemoryRegion(0x2000, 8, true);
        IrInstruction read = program.Instructions[0] with
        {
            Annotation = program.Instructions[0].Annotation with { MemoryReadRegion = readRegion },
            SideEffects = program.Instructions[0].SideEffects with
            {
                Memory = new(IrMemoryEffectKind.Read, IrAddressSpaceIdentity.Generic,
                    IrMemoryOrdering.NotAtomic, readRegion, null)
            }
        };
        IrInstruction write = program.Instructions[1] with
        {
            Annotation = program.Instructions[1].Annotation with { MemoryWriteRegion = writeRegion },
            SideEffects = program.Instructions[1].SideEffects with
            {
                Memory = new(IrMemoryEffectKind.Write, IrAddressSpaceIdentity.Generic,
                    IrMemoryOrdering.NotAtomic, null, writeRegion)
            }
        };
        return program with { Instructions = [read, write] };
    }

    private static IrProgram VectorTransferProgram()
    {
        HybridCpuInstructionWord instruction = default;
        instruction.OpCode = (uint)HybridCpuOpcode.VLOAD;
        instruction.DataTypeValue = HybridCpuDataType.INT32;
        instruction.PredicateMask = 0x0f;
        instruction.DestSrc1Pointer = 0x200;
        instruction.Src2Pointer = 0x300;
        instruction.StreamLength = 4;
        instruction.Stride = 4;
        return new HybridCpuIrBuilder().BuildProgram(0, [instruction]);
    }

    private static IrProgram MatrixTileMemoryProgram()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        CompilerMatrixTileDescriptorAbi descriptor = new CompilerMatrixTileDescriptorAbi(
            MatrixTileCanonicalDescriptorAbi.Create(2, 2, 1, 2),
            HybridCpuDataType.INT8);
        context.CompileMtileLoadWithDecision(
            CompilerMatrixTileTileOperand.Create(1),
            descriptor,
            CompilerMatrixTileMemoryFaultAbiInputs.Create(0x4000));
        context.CompileMtileStoreWithDecision(
            CompilerMatrixTileTileOperand.Create(1),
            descriptor,
            CompilerMatrixTileMemoryFaultAbiInputs.Create(0x5000));
        context.CompileMtileMaccWithDecision(
            CompilerMatrixTileTileOperand.Create(2),
            CompilerMatrixTileTileOperand.Create(3),
            CompilerMatrixTileTileOperand.Create(4),
            descriptor,
            CompilerMatrixTileAccumulatorPolicyAbi.CreateForRuntimeDerivedFootprint(
                descriptor.CanonicalDescriptor));
        context.CompileMtransposeWithDecision(
            CompilerMatrixTileTileOperand.Create(4),
            CompilerMatrixTileTileOperand.Create(5),
            descriptor,
            CompilerMatrixTileTransposePolicyAbi.CreateForRuntimeDerivedDestination(
                descriptor.CanonicalDescriptor));
        return context.CompileProgram().ProgramSchedule.Program;
    }

    private static IrProgram DscDescriptorProgram()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        context.CompileDmaStreamCompute(DmaStreamComputeTestDescriptorFactory.CreateDescriptor());
        return context.CompileProgram().ProgramSchedule.Program;
    }

    private static IrProgram L7DescriptorProgram()
    {
        AcceleratorCommandDescriptor descriptor = L7SdcTestDescriptorFactory.ParseValidDescriptor();
        HybridCpuThreadCompilerContext context = L7SdcCompilerEmissionTests.CreateContextForDescriptor(descriptor);
        context.CompileAcceleratorSubmit(
            IrAcceleratorIntent.ForMatMul(descriptor, tokenDestinationRegister: 9),
            CompilerAcceleratorCapabilityModel.ReferenceMatMul);
        return context.CompileProgram().ProgramSchedule.Program;
    }

    private static void AssertDescriptorEvidence(PclMaterializationRun run, string schemaId)
    {
        using JsonDocument snapshot = JsonDocument.Parse(File.ReadAllBytes(run.InputIrPath));
        JsonElement evidence = snapshot.RootElement.GetProperty("Program").GetProperty("Instructions")[0]
            .GetProperty("ExternalDescriptorEvidence");
        Assert.Equal(schemaId, evidence.GetProperty("SchemaId").GetString());
        Assert.True(evidence.GetProperty("AllOrNone").GetBoolean());
        Assert.NotEmpty(evidence.GetProperty("ReadRegions").EnumerateArray());
        Assert.NotEmpty(evidence.GetProperty("WriteRegions").EnumerateArray());
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(run.ManifestPath));
        JsonElement[] footprints = manifest.RootElement.GetProperty("Footprints").EnumerateArray().ToArray();
        Assert.Contains(footprints, static row => (row.GetProperty("Access").GetByte() & 1) != 0);
        Assert.Contains(footprints, static row => (row.GetProperty("Access").GetByte() & 2) != 0);
    }

    private static PclMaterializationRun MaterializePcl(IrProgram program)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"hybridcpu-pcl-materializer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string manifest = Path.Combine(directory, "facts.json");
        string input = Path.Combine(directory, "input.ir.json");
        SingNextPclEmissionContextV1 context = new SingNextPclEmissionContextV1(
            manifest, input, Path.Combine(directory, "output.bin"), typeof(NativeAotSeamArtifactV1).Assembly.Location,
            typeof(NativeAotSeamArtifactV1).Assembly.Location, Path.Combine(directory, "output.pcl"), null, true);
        bool success = SingNextPclCanonicalIrV1.TryMaterialize(context, program, out string? diagnostic);
        return new(directory, manifest, input, success, diagnostic);
    }

    private static ManagedCallGraphCompilationV1 ImportManagedGraph()
    {
        byte[] image = File.ReadAllBytes(typeof(ManagedCallGraphFixtures).Assembly.Location);
        ManagedBodyWorldV1 world = new ManagedBodyWorldV1(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            image, "phase23-pcl-graph-fixture.dll",
            [new(typeof(ManagedCallGraphFixtures).FullName!, nameof(ManagedCallGraphFixtures.ParameterlessRoot))], []);
        return new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(world);
    }

    private static PclMaterializationRun MaterializePclGraph(
        IReadOnlyList<ManagedCompiledMethodV1> methods,
        ManagedCallGraphEvidenceV1 graph)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"hybridcpu-pcl-graph-materializer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string manifest = Path.Combine(directory, "facts.json");
        string input = Path.Combine(directory, "input.ir.json");
        SingNextPclEmissionContextV1 context = new SingNextPclEmissionContextV1(
            manifest, input, Path.Combine(directory, "output.bin"), typeof(NativeAotSeamArtifactV1).Assembly.Location,
            typeof(NativeAotSeamArtifactV1).Assembly.Location, Path.Combine(directory, "output.pcl"), null, true);
        bool success = SingNextPclCanonicalIrV1.TryMaterializeGraph(context, methods,
            graph.GraphDigest, Assert.Single(graph.RootIdentities), out string? diagnostic);
        return new(directory, manifest, input, success, diagnostic);
    }

    private static AdapterRun RunAdapter(string method, string? sourceCommit = null)
    {
        string output = Path.Combine(Path.GetTempPath(), $"hybridcpu-naot-test-{Guid.NewGuid():N}.bin");
        ProcessStartInfo start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(NativeAotSeamArtifactV1).Assembly.Location);
        start.ArgumentList.Add("compile");
        AddPair(start, "--assembly", FixtureAssembly);
        AddPair(start, "--type", FixtureType);
        AddPair(start, "--method", method);
        AddPair(start, "--out", output);
        AddPair(start, "--source-commit", sourceCommit ?? NativeAotSeamBaselineV1.SourceCommit);
        AddPair(start, "--patch-digest", NativeAotSeamBaselineV1.PatchDigest);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Adapter process did not start.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        byte[] code = File.Exists(output) ? File.ReadAllBytes(output) : Array.Empty<byte>();
        NativeAotSeamArtifactV1? artifact = File.Exists(output + ".json")
            ? JsonSerializer.Deserialize<NativeAotSeamArtifactV1>(File.ReadAllText(output + ".json"))
            : null;
        return new(process.ExitCode, stdout, stderr, output, code, artifact);
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

    private sealed record AdapterRun(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        string OutputPath,
        byte[] Code,
        NativeAotSeamArtifactV1? Artifact) : IDisposable
    {
        public void Dispose()
        {
            if (File.Exists(OutputPath)) File.Delete(OutputPath);
            if (File.Exists(OutputPath + ".json")) File.Delete(OutputPath + ".json");
        }
    }

    private sealed record PclMaterializationRun(
        string DirectoryPath,
        string ManifestPath,
        string InputIrPath,
        bool Success,
        string? Diagnostic) : IDisposable
    {
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
