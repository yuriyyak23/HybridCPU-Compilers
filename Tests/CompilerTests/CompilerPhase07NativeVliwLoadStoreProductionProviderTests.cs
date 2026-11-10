using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.IR.Lowering;
using HybridCPU.Compiler.Core.IR.Lowering.Production;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Tests.TestHelpers;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

/// <summary>
/// Phase 07 tests for the explicit native LSU production provider. Memory
/// publication, ordering, fault resolution, commit, and retire stay runtime-owned.
/// </summary>
public sealed class CompilerPhase07NativeVliwLoadStoreProductionProviderTests
{
    [Theory]
    [InlineData(InstructionsEnum.LB, 0x100)]
    [InlineData(InstructionsEnum.LH, 0x100)]
    [InlineData(InstructionsEnum.LW, 0x100)]
    [InlineData(InstructionsEnum.LD, 0x100)]
    [InlineData(InstructionsEnum.SB, 0x100)]
    [InlineData(InstructionsEnum.SH, 0x100)]
    [InlineData(InstructionsEnum.SW, 0x100)]
    [InlineData(InstructionsEnum.SD, 0x100)]
    public void SupportedLoadStoreCarriersProduceRuntimePendingPackages(
        InstructionsEnum opcode,
        ulong address)
    {
        CompilerEmissionPackage candidate = CreateLoadStorePackage(opcode, address);
        CompilerSemanticIntent intent = CreateLoadStoreIntent(opcode);
        CompilerProductionLoweringContext context = CreateProductionContext(candidate);
        CompilerProductionLoweringResult result =
            NativeVliwLoadStoreProductionProvider.Instance.TryProduce(
                intent,
                Analyze(intent),
                context);

        Assert.True(
            result.ResultKind == CompilerProductionLoweringResultKind.RuntimeAuthorityPending,
            $"Phase 07 provider rejected {opcode}: {result.Reason}");
        Assert.True(result.GateResult.IsSatisfied);
        Assert.NotNull(result.Package);
        Assert.Equal(
            "NativeVliwLoadStoreProductionProvider",
            result.Package!.Identity.ProducerSurface);
        Assert.Equal(
            candidate.Carrier!.Image.SerializedImage,
            result.Package.Carrier!.Image.SerializedImage);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
        Assert.Contains("contour=NativeVliwLoadStore", result.TelemetryEvidence);
        Assert.Contains("memory-order-and-fault=runtime-owned", result.TelemetryEvidence);
        Assert.Contains(
            result.TelemetryEvidence,
            entry => entry.StartsWith("artifact-id=", StringComparison.Ordinal));

        CompilerToIseParitySnapshot parity =
            CompilerToIseParityHarness.AssertContourAndOpcode(
                result.Package,
                ExecutionContourKind.NativeVliwLoadStore,
                opcode);
        Assert.Equal(ExpectedGoldenHash(opcode), parity.CarrierBytesHash);
        Assert.Equal([4], parity.OccupiedSlots);
        Assert.Equal(SlotClass.LsuClass, Assert.Single(parity.Slots).SlotClass);
        byte[] encoded = result.Package.Carrier.Image.SerializedImage;
        bool store = opcode is InstructionsEnum.SB or InstructionsEnum.SH or InstructionsEnum.SW or InstructionsEnum.SD;
        Assert.Equal(store ? 0x0009_0001_0000UL : 0x0001_0009UL,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(4 * 32 + 8)));
        Assert.Equal(address,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(4 * 32 + 16)));
        Assert.Equal(parity.CarrierBytesHash, parity.ReencodedBytesHash);
        CompilerToIseParityHarness.AssertRuntimeAuthorityPending(result.Package);
    }

    [Fact]
    public void ExplicitLoadStoreProfileResolvesProviderButCompatibilityProfileDoesNot()
    {
        CompilerEmissionPackage candidate = CreateLoadStorePackage(InstructionsEnum.LW, 0x100);
        CompilerProductionLoweringContext compatibility =
            CreateProductionContext(candidate) with
            {
                ProductionProfile = CreateProfile(
                    CompilerProductionLoweringProfileMode.CompatibilityOnly)
            };

        Assert.Null(
            RuntimeContourLoweringProviderRegistry.Instance.ResolveProductionProvider(
                ExecutionContourKind.NativeVliwLoadStore,
                compatibility));

        CompilerProductionLoweringContext explicitProfile = CreateProductionContext(candidate);
        Assert.Same(
            NativeVliwLoadStoreProductionProvider.Instance,
            RuntimeContourLoweringProviderRegistry.Instance.ResolveProductionProvider(
                ExecutionContourKind.NativeVliwLoadStore,
                explicitProfile));
    }

    [Fact]
    public void MissingMemoryOrderingAndFaultGateRemainsFutureGated()
    {
        CompilerEmissionPackage candidate = CreateLoadStorePackage(InstructionsEnum.LW, 0x100);
        CompilerSemanticIntent intent = CreateLoadStoreIntent(InstructionsEnum.LW);
        CompilerProductionLoweringContext context = CreateProductionContext(candidate) with
        {
            Readiness = CompilerProductionLoweringReadiness.Complete
        };

        CompilerProductionLoweringResult result =
            NativeVliwLoadStoreProductionProvider.Instance.TryProduce(
                intent,
                Analyze(intent),
                context);

        Assert.Equal(CompilerProductionLoweringResultKind.FutureGated, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains(
            CompilerProductionLoweringGateIds.MemoryFaultRuntimeDependency,
            result.GateResult.MissingGates);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Fact]
    public void ZeroAddressFailsClosedBeforeProductionPackage()
    {
        CompilerEmissionPackage candidate = CreateLoadStorePackage(InstructionsEnum.LW, 0);
        CompilerSemanticIntent intent = CreateLoadStoreIntent(InstructionsEnum.LW);

        CompilerProductionLoweringResult result =
            NativeVliwLoadStoreProductionProvider.Instance.TryProduce(
                intent,
                Analyze(intent),
                CreateProductionContext(candidate));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains("explicit 64-bit memory address", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StreamOrIndexedFieldsFailClosedBeforeProductionPackage()
    {
        HybridCpuThreadCompilerContext compiler = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        compiler.CompileInstruction(
            (uint)InstructionsEnum.LW,
            (byte)DataTypeEnum.INT32,
            predicate: 0,
            immediate: 0,
            destSrc1: VLIW_Instruction.PackArchRegs(9, 1, 0),
            src2: 0x100,
            streamLength: 1,
            stride: 0,
            StealabilityPolicy.NotStealable);
        CompilerEmissionPackage candidate = Project(compiler);
        CompilerSemanticIntent intent = CreateLoadStoreIntent(InstructionsEnum.LW);

        CompilerProductionLoweringResult result =
            NativeVliwLoadStoreProductionProvider.Instance.TryProduce(
                intent,
                Analyze(intent),
                CreateProductionContext(candidate));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains("stream/indexed/2D", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SemanticIntentKind.ScalarAlu, ExecutionContourKind.NativeVliwScalar)]
    [InlineData(SemanticIntentKind.BranchControl, ExecutionContourKind.NativeVliwBranchControl)]
    [InlineData(SemanticIntentKind.VectorStream, ExecutionContourKind.StreamEngineVector)]
    [InlineData(SemanticIntentKind.MatrixTile, ExecutionContourKind.MatrixTileHelperOnly)]
    [InlineData(SemanticIntentKind.DmaStreamCompute, ExecutionContourKind.DmaStreamComputeLane6)]
    [InlineData(SemanticIntentKind.ExternalAcceleratorCommand, ExecutionContourKind.L7SdcLane7)]
    public void NonLsuContoursAreRejectedWithoutFallback(
        SemanticIntentKind intentKind,
        ExecutionContourKind contourKind)
    {
        CompilerEmissionPackage candidate = CreateLoadStorePackage(InstructionsEnum.LW, 0x100);
        CompilerSemanticIntent intent = CreateNonLoadStoreIntent(intentKind);
        CompilerProductionLoweringResult result =
            NativeVliwLoadStoreProductionProvider.Instance.TryProduce(
                intent,
                DefaultContourLoweringProviderRegistry.Instance
                    .ResolveAnalyzer(contourKind)
                    .Analyze(
                        intent,
                        new CompilerLoweringContext(
                            new CompilerTargetProfile(
                                "phase07-negative-analysis",
                                AllowsCarrierEmission: true,
                                AllowsBackendEmission: true),
                            "CompilerPhase07NativeVliwLoadStoreProductionProviderTests")),
                CreateProductionContext(candidate));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
        Assert.Contains("cross-contour", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static CompilerProductionLoweringContext CreateProductionContext(
        CompilerEmissionPackage candidate) =>
        new(
            new CompilerTargetProfile(
                "phase07-native-vliw-load-store-production",
                AllowsCarrierEmission: true,
                AllowsBackendEmission: true),
            "CompilerPhase07NativeVliwLoadStoreProductionProviderTests",
            CreateProfile(CompilerProductionLoweringProfileMode.ExplicitlyEnabled))
        {
            CandidatePackage = candidate,
            Readiness = CompilerProductionLoweringReadiness.CompleteLoadStore
        };

    private static CompilerProductionLoweringProfile CreateProfile(
        CompilerProductionLoweringProfileMode mode) =>
        new(
            "phase07-native-vliw-load-store-profile",
            mode,
            new HashSet<ExecutionContourKind> { ExecutionContourKind.NativeVliwLoadStore },
            CompilerProductionLoweringGateIds.AllFor(ExecutionContourKind.NativeVliwLoadStore));

    private static ContourAnalysisReport Analyze(CompilerSemanticIntent intent) =>
        DefaultContourLoweringProviderRegistry.Instance
            .ResolveAnalyzer(ExecutionContourKind.NativeVliwLoadStore)
            .Analyze(
                intent,
                new CompilerLoweringContext(
                    new CompilerTargetProfile(
                        "phase07-load-store-analysis",
                        AllowsCarrierEmission: true,
                        AllowsBackendEmission: true),
                    "CompilerPhase07NativeVliwLoadStoreProductionProviderTests"));

    private static CompilerSemanticIntent CreateLoadStoreIntent(InstructionsEnum opcode) =>
        new(
            SemanticIntentKind.LoadStore,
            opcode.ToString(),
            RequiresDescriptor: false,
            RequiresSideband: false,
            RequiresToken: false,
            RequiresRuntimeLegality: true,
            IsCompatibilityProjection: false,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: false,
            IsParserOnly: false,
            "Phase 07 explicit native LSU production intent.");

    private static CompilerSemanticIntent CreateNonLoadStoreIntent(
        SemanticIntentKind intentKind) =>
        new(
            intentKind,
            intentKind.ToString(),
            RequiresDescriptor: intentKind is
                SemanticIntentKind.DmaStreamCompute or
                SemanticIntentKind.ExternalAcceleratorCommand,
            RequiresSideband: intentKind is
                SemanticIntentKind.VectorStream or
                SemanticIntentKind.MatrixTile or
                SemanticIntentKind.DmaStreamCompute or
                SemanticIntentKind.ExternalAcceleratorCommand,
            RequiresToken: intentKind is
                SemanticIntentKind.DmaStreamCompute or
                SemanticIntentKind.ExternalAcceleratorCommand,
            RequiresRuntimeLegality: true,
            IsCompatibilityProjection: false,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: intentKind is SemanticIntentKind.VectorStream or SemanticIntentKind.MatrixTile,
            IsParserOnly: false,
            "Phase 07 negative contour boundary intent.");

    private static CompilerEmissionPackage CreateLoadStorePackage(
        InstructionsEnum opcode,
        ulong address)
    {
        HybridCpuThreadCompilerContext compiler = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        compiler.CompileInstruction(
            (uint)opcode,
            (byte)DataTypeEnum.INT32,
            predicate: 0,
            immediate: 0,
            destSrc1: opcode is InstructionsEnum.SB or InstructionsEnum.SH or InstructionsEnum.SW or InstructionsEnum.SD
                ? VLIW_Instruction.PackArchRegs(0, 1, 9)
                : VLIW_Instruction.PackArchRegs(9, 1, 0),
            src2: address,
            streamLength: 0,
            stride: 0,
            StealabilityPolicy.NotStealable);
        return Project(compiler);
    }

    private static string ExpectedGoldenHash(InstructionsEnum opcode) =>
        opcode switch
        {
            InstructionsEnum.LB => "e63144d57d7726f545f8360a244ddb20a23f53871fd019d7099d02257412dce5",
            InstructionsEnum.LH => "62984e531cb8a52280c053e3a443b95380894c464efe8533f115a83fbefe301d",
            InstructionsEnum.LW => "9ee22c35e6c53a4ba1285898d7addee939fe4fb123f9d94f8392cff785b7bd7d",
            InstructionsEnum.LD => "ffb7253bce5a2cedf763fc8f76fb54785ac84c24c88f80b960a70f972fef277e",
            InstructionsEnum.SB => "37a214dfc7aa521ca2298df7ade3b3af2947bee516bcc8409ac8d8a8e501f302",
            InstructionsEnum.SH => "2c36646ae677592176beff38f6dba0d87122a46fb8f4504bae3445b0c413dd83",
            InstructionsEnum.SW => "36efd560b259c5c15a951891ce1d4f38d0ee643f2b21b0991918c5bbde5a5782",
            InstructionsEnum.SD => "e8ef7bc1f5fa2abb31c75b26f9fb0185e40998a79c10695689349e677c033a56",
            _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Missing Phase 07 load/store golden hash.")
        };

    private static CompilerEmissionPackage Project(
        HybridCpuThreadCompilerContext compiler)
    {
        HybridCpuCompiledProgram compiled = compiler.CompileProgram();
        return HybridCpuCompiledProgramEnvelopeAdapter.Instance.Project(
            compiled,
            new CompilerArtifactProjectionOptions(
                SemanticIntentKind.LoadStore,
                ExecutionContourKind.NativeVliwLoadStore,
                "HybridCpuThreadCompilerContext.CompileInstruction",
                "Phase 07 native LSU shadow candidate."));
    }
}
