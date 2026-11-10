using System;
using System.Collections.Generic;
using System.Linq;
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
/// Phase 09 tests for the scoped direct VLOAD/VSTORE provider. The existing
/// typed helper remains the candidate source; these tests do not turn helper
/// success into general vector or stream authority.
/// </summary>
public sealed class CompilerPhase09StreamEngineVectorDirectTransferProductionProviderTests
{
    [Theory]
    [InlineData(InstructionsEnum.VLOAD)]
    [InlineData(InstructionsEnum.VSTORE)]
    public void DirectTransferCarriersProduceRuntimePendingPackages(InstructionsEnum opcode)
    {
        CompilerEmissionPackage candidate = CreateVectorPackage(opcode);
        CompilerSemanticIntent intent = CreateDirectTransferIntent(opcode);
        CompilerProductionLoweringResult result =
            StreamEngineVectorDirectTransferProductionProvider.Instance.TryProduce(
                intent,
                Analyze(intent),
                CreateProductionContext(candidate));

        Assert.True(
            result.ResultKind == CompilerProductionLoweringResultKind.RuntimeAuthorityPending,
            $"Phase 09 provider rejected {opcode}: {result.Reason}");
        Assert.True(result.GateResult.IsSatisfied);
        Assert.NotNull(result.Package);
        Assert.Equal(
            "StreamEngineVectorDirectTransferProductionProvider",
            result.Package!.Identity.ProducerSurface);
        Assert.Equal(
            candidate.Carrier!.Image.SerializedImage,
            result.Package.Carrier!.Image.SerializedImage);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
        Assert.Contains(
            $"gate-id={CompilerProductionLoweringGateIds.StreamEngineVectorDirectTransferProduction}",
            result.TelemetryEvidence);
        Assert.Contains("helper-carrier-shadow-compare=verified", result.TelemetryEvidence);
        Assert.Contains(
            result.TelemetryEvidence,
            entry => entry.StartsWith("transfer-facts=", StringComparison.Ordinal));
        Assert.Contains(
            "excluded-alternatives=scalar,general-vector,indexed,2D,transpose,segment,widening-fma,MatrixTile,DSC,L7,VMX",
            result.TelemetryEvidence);

        CompilerToIseParitySnapshot parity =
            CompilerToIseParityHarness.AssertContourAndOpcode(
                result.Package,
                ExecutionContourKind.StreamEngineVector,
                opcode);
        Assert.Equal(ExpectedGoldenHash(opcode), parity.CarrierBytesHash);
        Assert.Equal([4], parity.OccupiedSlots);
        Assert.Equal(SlotClass.LsuClass, Assert.Single(parity.Slots).SlotClass);
        Assert.Equal(parity.CarrierBytesHash, parity.ReencodedBytesHash);
        CompilerToIseParityHarness.AssertRuntimeAuthorityPending(result.Package);
    }

    [Fact]
    public void ExplicitDirectTransferProfileResolvesProviderButCompatibilityProfileDoesNot()
    {
        CompilerEmissionPackage candidate = CreateVectorPackage(InstructionsEnum.VLOAD);
        CompilerProductionLoweringContext compatibility =
            CreateProductionContext(candidate) with
            {
                ProductionProfile = CreateProfile(
                    CompilerProductionLoweringProfileMode.CompatibilityOnly)
            };

        Assert.Null(
            RuntimeContourLoweringProviderRegistry.Instance.ResolveProductionProvider(
                ExecutionContourKind.StreamEngineVector,
                compatibility));

        CompilerProductionLoweringContext explicitProfile = CreateProductionContext(candidate);
        Assert.Same(
            StreamEngineVectorDirectTransferProductionProvider.Instance,
            RuntimeContourLoweringProviderRegistry.Instance.ResolveProductionProvider(
                ExecutionContourKind.StreamEngineVector,
                explicitProfile));
    }

    [Fact]
    public void MissingDirectTransferGateRemainsFutureGated()
    {
        CompilerEmissionPackage candidate = CreateVectorPackage(InstructionsEnum.VLOAD);
        CompilerSemanticIntent intent = CreateDirectTransferIntent(InstructionsEnum.VLOAD);
        CompilerProductionLoweringContext context = CreateProductionContext(candidate) with
        {
            ProductionProfile = CreateProfileWithout(
                CompilerProductionLoweringGateIds.StreamEngineVectorDirectTransferProduction)
        };

        CompilerProductionLoweringResult result =
            StreamEngineVectorDirectTransferProductionProvider.Instance.TryProduce(
                intent,
                Analyze(intent),
                context);

        Assert.Equal(CompilerProductionLoweringResultKind.FutureGated, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains(
            CompilerProductionLoweringGateIds.StreamEngineVectorDirectTransferProduction,
            result.GateResult.MissingGates);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Theory]
    [InlineData(0u, 4, false, false, "StreamLength == 0")]
    [InlineData(4u, 0, false, false, "Stride == 0")]
    [InlineData(4u, 4, true, false, "indexed/2D/reduction/masked")]
    [InlineData(4u, 4, false, true, "indexed/2D/reduction/masked")]
    [InlineData(4u, 4, false, false, "non-zero source/destination")]
    public void UnsupportedDirectTransferShapeFailsClosed(
        uint streamLength,
        ushort stride,
        bool indexed,
        bool is2D,
        string expectedReason)
    {
        CompilerEmissionPackage candidate = TamperVectorPackage(
            CreateVectorPackage(InstructionsEnum.VLOAD),
            instruction => instruction with
            {
                StreamLength = streamLength,
                Stride = stride,
                Indexed = indexed,
                Is2D = is2D,
                DestSrc1Pointer = expectedReason == "non-zero source/destination"
                    ? 0UL
                    : instruction.DestSrc1Pointer
            });
        CompilerSemanticIntent intent = CreateDirectTransferIntent(InstructionsEnum.VLOAD);

        CompilerProductionLoweringResult result =
            StreamEngineVectorDirectTransferProductionProvider.Instance.TryProduce(
                intent,
                Analyze(intent),
                CreateProductionContext(candidate));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains(expectedReason, result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Theory]
    [InlineData(InstructionsEnum.ADD)]
    [InlineData(InstructionsEnum.VMXON)]
    public void NonDirectOpcodesCannotBeRelabeledAsVectorTransfer(InstructionsEnum opcode)
    {
        CompilerEmissionPackage candidate = CreateRawRelabeledPackage(opcode);
        CompilerSemanticIntent intent = CreateDirectTransferIntent(InstructionsEnum.VLOAD);

        CompilerProductionLoweringResult result =
            StreamEngineVectorDirectTransferProductionProvider.Instance.TryProduce(
                intent,
                Analyze(intent),
                CreateProductionContext(candidate));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains("outside the bounded", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Theory]
    [InlineData(SemanticIntentKind.ScalarAlu, ExecutionContourKind.NativeVliwScalar)]
    [InlineData(SemanticIntentKind.LoadStore, ExecutionContourKind.NativeVliwLoadStore)]
    [InlineData(SemanticIntentKind.MatrixTile, ExecutionContourKind.MatrixTileHelperOnly)]
    [InlineData(SemanticIntentKind.DmaStreamCompute, ExecutionContourKind.DmaStreamComputeLane6)]
    [InlineData(SemanticIntentKind.ExternalAcceleratorCommand, ExecutionContourKind.L7SdcLane7)]
    [InlineData(SemanticIntentKind.VmxCompatibilityProjection, ExecutionContourKind.VmxProjectionOnly)]
    public void OtherContoursAreRejectedWithoutFallback(
        SemanticIntentKind intentKind,
        ExecutionContourKind contourKind)
    {
        CompilerEmissionPackage candidate = CreateVectorPackage(InstructionsEnum.VLOAD);
        CompilerSemanticIntent intent = CreateNonVectorIntent(intentKind);
        CompilerProductionLoweringResult result =
            StreamEngineVectorDirectTransferProductionProvider.Instance.TryProduce(
                intent,
                DefaultContourLoweringProviderRegistry.Instance
                    .ResolveAnalyzer(contourKind)
                    .Analyze(
                        intent,
                        new CompilerLoweringContext(
                            new CompilerTargetProfile(
                                "phase09-negative-analysis",
                                AllowsCarrierEmission: true,
                                AllowsBackendEmission: true),
                            "CompilerPhase09StreamEngineVectorDirectTransferProductionProviderTests")),
                CreateProductionContext(candidate));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains("cross-contour", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Fact]
    public void HelperOnlyIntentCannotSatisfyDirectTransferGate()
    {
        CompilerEmissionPackage candidate = CreateVectorPackage(InstructionsEnum.VLOAD);
        CompilerSemanticIntent intent = CreateDirectTransferIntent(InstructionsEnum.VLOAD) with
        {
            IsHelperAbiOnly = true
        };

        CompilerProductionLoweringResult result =
            StreamEngineVectorDirectTransferProductionProvider.Instance.TryProduce(
                intent,
                Analyze(intent),
                CreateProductionContext(candidate));

        Assert.Equal(CompilerProductionLoweringResultKind.FutureGated, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains(CompilerProductionLoweringGateIds.Intent, result.GateResult.MissingGates);
    }

    private static CompilerProductionLoweringContext CreateProductionContext(
        CompilerEmissionPackage candidate) =>
        new(
            new CompilerTargetProfile(
                "phase09-stream-engine-vector-direct-transfer-production",
                AllowsCarrierEmission: true,
                AllowsBackendEmission: true),
            "CompilerPhase09StreamEngineVectorDirectTransferProductionProviderTests",
            CreateProfile(CompilerProductionLoweringProfileMode.ExplicitlyEnabled))
        {
            CandidatePackage = candidate,
            Readiness = CompilerProductionLoweringReadiness.Complete
        };

    private static CompilerProductionLoweringProfile CreateProfile(
        CompilerProductionLoweringProfileMode mode) =>
        new(
            "phase09-stream-engine-vector-direct-transfer-profile",
            mode,
            new HashSet<ExecutionContourKind> { ExecutionContourKind.StreamEngineVector },
            CompilerProductionLoweringGateIds.AllFor(ExecutionContourKind.StreamEngineVector));

    private static CompilerProductionLoweringProfile CreateProfileWithout(string gateId)
    {
        HashSet<string> gateIds = new(
            CompilerProductionLoweringGateIds.AllFor(ExecutionContourKind.StreamEngineVector));
        gateIds.Remove(gateId);
        return new(
            "phase09-stream-engine-vector-direct-transfer-profile-without-gate",
            CompilerProductionLoweringProfileMode.ExplicitlyEnabled,
            new HashSet<ExecutionContourKind> { ExecutionContourKind.StreamEngineVector },
            gateIds);
    }

    private static ContourAnalysisReport Analyze(CompilerSemanticIntent intent) =>
        DefaultContourLoweringProviderRegistry.Instance
            .ResolveAnalyzer(ExecutionContourKind.StreamEngineVector)
            .Analyze(
                intent,
                new CompilerLoweringContext(
                    new CompilerTargetProfile(
                        "phase09-stream-engine-vector-analysis",
                        AllowsCarrierEmission: true,
                        AllowsBackendEmission: true),
                    "CompilerPhase09StreamEngineVectorDirectTransferProductionProviderTests"));

    private static CompilerSemanticIntent CreateDirectTransferIntent(InstructionsEnum opcode) =>
        new(
            SemanticIntentKind.VectorStream,
            opcode.ToString(),
            RequiresDescriptor: false,
            RequiresSideband: false,
            RequiresToken: false,
            RequiresRuntimeLegality: true,
            IsCompatibilityProjection: false,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: false,
            IsParserOnly: false,
            "Phase 09 explicit direct VLOAD/VSTORE production intent.");

    private static CompilerSemanticIntent CreateNonVectorIntent(
        SemanticIntentKind intentKind) =>
        new(
            intentKind,
            intentKind.ToString(),
            RequiresDescriptor: intentKind is
                SemanticIntentKind.DmaStreamCompute or
                SemanticIntentKind.ExternalAcceleratorCommand,
            RequiresSideband: intentKind is
                SemanticIntentKind.MatrixTile or
                SemanticIntentKind.DmaStreamCompute or
                SemanticIntentKind.ExternalAcceleratorCommand,
            RequiresToken: intentKind is
                SemanticIntentKind.DmaStreamCompute or
                SemanticIntentKind.ExternalAcceleratorCommand,
            RequiresRuntimeLegality: true,
            IsCompatibilityProjection: intentKind == SemanticIntentKind.VmxCompatibilityProjection,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: intentKind == SemanticIntentKind.MatrixTile,
            IsParserOnly: false,
            "Phase 09 negative contour boundary intent.");

    private static CompilerEmissionPackage CreateVectorPackage(InstructionsEnum opcode)
    {
        HybridCpuThreadCompilerContext compiler = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        CompilerVectorTransferShapeAbi shape =
            CompilerVectorTransferShapeAbi.CreateContiguous(HybridCpuDataType.INT32, 4);

        if (opcode == InstructionsEnum.VLOAD)
        {
            compiler.CompileVloadWithDecision(
                CompilerVectorTransferMemoryAddressAbi.Create(0x200),
                CompilerVectorTransferMemoryAddressAbi.Create(0x300),
                shape);
        }
        else
        {
            compiler.CompileVstoreWithDecision(
                CompilerVectorTransferMemoryAddressAbi.Create(0x300),
                CompilerVectorTransferMemoryAddressAbi.Create(0x200),
                shape);
        }

        return Project(compiler);
    }

    private static CompilerEmissionPackage CreateRawRelabeledPackage(InstructionsEnum opcode)
    {
        HybridCpuThreadCompilerContext compiler = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        compiler.CompileInstruction(
            (uint)opcode,
            (byte)DataTypeEnum.INT32,
            predicate: 0,
            immediate: 0,
            destSrc1: 0x200,
            src2: 0x300,
            streamLength: opcode is InstructionsEnum.VLOAD or InstructionsEnum.VSTORE ? 4UL : 0UL,
            stride: opcode is InstructionsEnum.VLOAD or InstructionsEnum.VSTORE ? (ushort)4 : (ushort)0,
            StealabilityPolicy.NotStealable);
        return Project(compiler);
    }

    private static CompilerEmissionPackage TamperVectorPackage(
        CompilerEmissionPackage package,
        Func<VLIW_Instruction, VLIW_Instruction> mutate)
    {
        byte[] image = package.Carrier!.Image.SerializedImage.ToArray();
        VLIW_Bundle bundle = new VLIW_Bundle();
        Assert.True(bundle.TryReadBytes(image, 0));
        int slotIndex = Enumerable.Range(0, BundleMetadata.BundleSlotCount)
            .Single(index => bundle.GetInstruction(index).OpCode != 0);
        bundle.SetInstruction(slotIndex, mutate(bundle.GetInstruction(slotIndex)));
        Assert.True(bundle.TryWriteBytes(image));

        return package with
        {
            Carrier = package.Carrier with
            {
                Image = package.Carrier.Image with
                {
                    Bundles = [NativeTransportRuntimeAdapter.ToCore(in bundle)],
                    SerializedImage = image
                }
            }
        };
    }

    private static CompilerEmissionPackage Project(
        HybridCpuThreadCompilerContext compiler)
    {
        HybridCpuCompiledProgram compiled = compiler.CompileProgram();
        return HybridCpuCompiledProgramEnvelopeAdapter.Instance.Project(
            compiled,
            new CompilerArtifactProjectionOptions(
                SemanticIntentKind.VectorStream,
                ExecutionContourKind.StreamEngineVector,
                "HybridCpuThreadCompilerContext.CompileVloadWithDecision/CompileVstoreWithDecision",
                "Phase 09 direct vector-transfer helper shadow candidate."));
    }

    private static string ExpectedGoldenHash(InstructionsEnum opcode) =>
        opcode switch
        {
            InstructionsEnum.VLOAD => "bb4787a06301ea80eba382e483f52f8a752ac4fa3eaf0a9ed43a0211d6504d6e",
            InstructionsEnum.VSTORE => "81bbef2dc1b53662003a42e42dc23b0cb8deddddf155ce02d8171a0d98f589d8",
            _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Missing Phase 09 vector-transfer golden hash.")
        };
}
