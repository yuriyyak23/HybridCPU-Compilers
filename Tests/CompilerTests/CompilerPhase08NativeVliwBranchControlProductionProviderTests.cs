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
/// Phase 08 tests for the explicit native branch/control provider. Branch
/// target facts are structural relocation evidence; control-flow legality,
/// execution, event interaction, publication, commit, and retire remain
/// runtime-owned.
/// </summary>
public sealed class CompilerPhase08NativeVliwBranchControlProductionProviderTests
{
    [Theory]
    [InlineData(InstructionsEnum.JAL, 0x20, 0UL)]
    [InlineData(InstructionsEnum.JALR, 0, 0UL)]
    [InlineData(InstructionsEnum.BNE, 0x20, 0UL)]
    [InlineData(InstructionsEnum.BLTU, 0x20, 0UL)]
    public void SupportedBranchControlCarriersProduceRuntimePendingPackages(
        InstructionsEnum opcode,
        int immediate,
        ulong targetField)
    {
        CompilerEmissionPackage candidate = CreateBranchPackage(opcode, immediate, targetField);
        CompilerSemanticIntent intent = CreateBranchIntent(opcode);
        CompilerProductionLoweringResult result =
            NativeVliwBranchControlProductionProvider.Instance.TryProduce(
                intent,
                Analyze(intent),
                CreateProductionContext(candidate));

        Assert.True(
            result.ResultKind == CompilerProductionLoweringResultKind.RuntimeAuthorityPending,
            $"Phase 08 provider rejected {opcode}: {result.Reason}");
        Assert.True(result.GateResult.IsSatisfied);
        Assert.NotNull(result.Package);
        Assert.Equal(
            "NativeVliwBranchControlProductionProvider",
            result.Package!.Identity.ProducerSurface);
        Assert.Equal(
            candidate.Carrier!.Image.SerializedImage,
            result.Package.Carrier!.Image.SerializedImage);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
        Assert.True(result.RuntimeAuthorityStillRequired.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired));
        Assert.True(result.RuntimeAuthorityStillRequired.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired));
        Assert.True(result.RuntimeAuthorityStillRequired.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired));
        Assert.True(result.RuntimeAuthorityStillRequired.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeCommitRequired));
        Assert.True(result.RuntimeAuthorityStillRequired.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeRetireRequired));
        Assert.True(result.RuntimeAuthorityStillRequired.HasFlag(CompilerRuntimeAuthorityDependency.RuntimePublicationRequired));
        Assert.Contains("branch-target-facts=structural-evidence-only", result.TelemetryEvidence);
        Assert.Contains(
            result.TelemetryEvidence,
            entry => entry.StartsWith("artifact-id=", StringComparison.Ordinal));

        CompilerToIseParitySnapshot parity =
            CompilerToIseParityHarness.AssertContourAndOpcode(
                result.Package,
                ExecutionContourKind.NativeVliwBranchControl,
                opcode);
        Assert.Equal(ExpectedGoldenHash(opcode), parity.CarrierBytesHash);
        Assert.Equal(parity.CarrierBytesHash, parity.ReencodedBytesHash);
        CompilerToIseParityHarness.AssertRuntimeAuthorityPending(result.Package);
    }

    [Fact]
    public void ExplicitBranchControlProfileResolvesProviderButCompatibilityProfileDoesNot()
    {
        CompilerEmissionPackage candidate = CreateBranchPackage(InstructionsEnum.JALR, 0, 0);
        CompilerProductionLoweringContext compatibility =
            CreateProductionContext(candidate) with
            {
                ProductionProfile = CreateProfile(
                    CompilerProductionLoweringProfileMode.CompatibilityOnly)
            };

        Assert.Null(
            RuntimeContourLoweringProviderRegistry.Instance.ResolveProductionProvider(
                ExecutionContourKind.NativeVliwBranchControl,
                compatibility));

        CompilerProductionLoweringContext explicitProfile = CreateProductionContext(candidate);
        Assert.Same(
            NativeVliwBranchControlProductionProvider.Instance,
            RuntimeContourLoweringProviderRegistry.Instance.ResolveProductionProvider(
                ExecutionContourKind.NativeVliwBranchControl,
                explicitProfile));
    }

    [Fact]
    public void MissingProductionGateRemainsFutureGated()
    {
        CompilerEmissionPackage candidate = CreateBranchPackage(InstructionsEnum.JALR, 0, 0);
        CompilerSemanticIntent intent = CreateBranchIntent(InstructionsEnum.JALR);
        CompilerProductionLoweringContext context = CreateProductionContext(candidate) with
        {
            Readiness = CompilerProductionLoweringReadiness.Missing
        };

        CompilerProductionLoweringResult result =
            NativeVliwBranchControlProductionProvider.Instance.TryProduce(
                intent,
                Analyze(intent),
                context);

        Assert.Equal(CompilerProductionLoweringResultKind.FutureGated, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains(
            CompilerProductionLoweringGateIds.Parity,
            result.GateResult.MissingGates);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Fact]
    public void MissingRelocationFactsFailClosedBeforeProductionPackage()
    {
        CompilerEmissionPackage candidate = CreateBranchPackage(InstructionsEnum.JAL, 0, 0);
        CompilerSemanticIntent intent = CreateBranchIntent(InstructionsEnum.JAL);

        CompilerProductionLoweringResult result =
            NativeVliwBranchControlProductionProvider.Instance.TryProduce(
                intent,
                Analyze(intent),
                CreateProductionContext(candidate));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains("target or relocation", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BranchAddressSidebandFailsClosedBeforeProductionPackage()
    {
        CompilerEmissionPackage candidate = CreateBranchPackage(InstructionsEnum.JALR, 0, 0x100);
        CompilerSemanticIntent intent = CreateBranchIntent(InstructionsEnum.JALR);

        CompilerProductionLoweringResult result =
            NativeVliwBranchControlProductionProvider.Instance.TryProduce(
                intent,
                Analyze(intent),
                CreateProductionContext(candidate));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains("target or relocation", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void HelperOrParserSuccessCannotSatisfyBranchProductionGate(
        bool helperOnly,
        bool parserOnly)
    {
        CompilerEmissionPackage candidate = CreateBranchPackage(InstructionsEnum.JALR, 0, 0);
        CompilerSemanticIntent intent = CreateBranchIntent(InstructionsEnum.JALR) with
        {
            IsHelperAbiOnly = helperOnly,
            IsParserOnly = parserOnly
        };

        CompilerProductionLoweringResult result =
            NativeVliwBranchControlProductionProvider.Instance.TryProduce(
                intent,
                Analyze(intent),
                CreateProductionContext(candidate));

        Assert.Equal(CompilerProductionLoweringResultKind.FutureGated, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains(CompilerProductionLoweringGateIds.Intent, result.GateResult.MissingGates);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Theory]
    [InlineData(SemanticIntentKind.ScalarAlu, ExecutionContourKind.NativeVliwScalar)]
    [InlineData(SemanticIntentKind.LoadStore, ExecutionContourKind.NativeVliwLoadStore)]
    [InlineData(SemanticIntentKind.VectorStream, ExecutionContourKind.StreamEngineVector)]
    [InlineData(SemanticIntentKind.MatrixTile, ExecutionContourKind.MatrixTileHelperOnly)]
    [InlineData(SemanticIntentKind.DmaStreamCompute, ExecutionContourKind.DmaStreamComputeLane6)]
    [InlineData(SemanticIntentKind.ExternalAcceleratorCommand, ExecutionContourKind.L7SdcLane7)]
    public void NonBranchContoursAreRejectedWithoutFallback(
        SemanticIntentKind intentKind,
        ExecutionContourKind contourKind)
    {
        CompilerEmissionPackage candidate = CreateBranchPackage(InstructionsEnum.JALR, 0, 0);
        CompilerSemanticIntent intent = CreateNonBranchIntent(intentKind);
        CompilerProductionLoweringResult result =
            NativeVliwBranchControlProductionProvider.Instance.TryProduce(
                intent,
                DefaultContourLoweringProviderRegistry.Instance
                    .ResolveAnalyzer(contourKind)
                    .Analyze(
                        intent,
                        new CompilerLoweringContext(
                            new CompilerTargetProfile(
                                "phase08-negative-analysis",
                                AllowsCarrierEmission: true,
                                AllowsBackendEmission: true),
                            "CompilerPhase08NativeVliwBranchControlProductionProviderTests")),
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
                "phase08-native-vliw-branch-control-production",
                AllowsCarrierEmission: true,
                AllowsBackendEmission: true),
            "CompilerPhase08NativeVliwBranchControlProductionProviderTests",
            CreateProfile(CompilerProductionLoweringProfileMode.ExplicitlyEnabled))
        {
            CandidatePackage = candidate,
            Readiness = CompilerProductionLoweringReadiness.Complete
        };

    private static CompilerProductionLoweringProfile CreateProfile(
        CompilerProductionLoweringProfileMode mode) =>
        new(
            "phase08-native-vliw-branch-control-profile",
            mode,
            new HashSet<ExecutionContourKind> { ExecutionContourKind.NativeVliwBranchControl },
            CompilerProductionLoweringGateIds.AllFor(ExecutionContourKind.NativeVliwBranchControl));

    private static ContourAnalysisReport Analyze(CompilerSemanticIntent intent) =>
        DefaultContourLoweringProviderRegistry.Instance
            .ResolveAnalyzer(ExecutionContourKind.NativeVliwBranchControl)
            .Analyze(
                intent,
                new CompilerLoweringContext(
                    new CompilerTargetProfile(
                        "phase08-branch-control-analysis",
                        AllowsCarrierEmission: true,
                        AllowsBackendEmission: true),
                    "CompilerPhase08NativeVliwBranchControlProductionProviderTests"));

    private static CompilerSemanticIntent CreateBranchIntent(InstructionsEnum opcode) =>
        new(
            SemanticIntentKind.BranchControl,
            opcode.ToString(),
            RequiresDescriptor: false,
            RequiresSideband: false,
            RequiresToken: false,
            RequiresRuntimeLegality: true,
            IsCompatibilityProjection: false,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: false,
            IsParserOnly: false,
            "Phase 08 explicit native branch/control production intent.");

    private static CompilerSemanticIntent CreateNonBranchIntent(
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
            "Phase 08 negative contour boundary intent.");

    private static CompilerEmissionPackage CreateBranchPackage(
        InstructionsEnum opcode,
        int immediate,
        ulong targetField)
    {
        HybridCpuThreadCompilerContext compiler = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        compiler.CompileInstruction(
            (uint)opcode,
            (byte)DataTypeEnum.INT32,
            predicate: 0,
            immediate: checked((ushort)immediate),
            destSrc1: opcode switch
            {
                InstructionsEnum.JAL => VLIW_Instruction.PackArchRegs(
                    0,
                    VLIW_Instruction.NoArchReg,
                    VLIW_Instruction.NoArchReg),
                InstructionsEnum.JALR => VLIW_Instruction.PackArchRegs(
                    0,
                    1,
                    VLIW_Instruction.NoArchReg),
                _ => VLIW_Instruction.PackArchRegs(VLIW_Instruction.NoArchReg, 5, 6)
            },
            src2: targetField,
            streamLength: 0,
            stride: 0,
            StealabilityPolicy.NotStealable);
        HybridCpuCompiledProgram compiled = compiler.CompileProgram();
        return HybridCpuCompiledProgramEnvelopeAdapter.Instance.Project(
            compiled,
            new CompilerArtifactProjectionOptions(
                SemanticIntentKind.BranchControl,
                ExecutionContourKind.NativeVliwBranchControl,
                "HybridCpuThreadCompilerContext.CompileInstruction",
                "Phase 08 native branch/control shadow candidate."));
    }

    private static string ExpectedGoldenHash(InstructionsEnum opcode) =>
        opcode switch
        {
            InstructionsEnum.JAL => "55e801fd8ac75fbd92608fada161e737a0a16ebd0820de7c933b5eb42ee836a2",
            InstructionsEnum.JALR => "1784840bf3883b85be5fdca4c627ebe7e2e1edaba70c4c2adf1231e722f516c6",
            InstructionsEnum.BNE => "73776fa2759e56c2ee5abf4ae14b62f137dd3320616f10081ab21da9bd9fb781",
            InstructionsEnum.BLTU => "f4c742f58177e4940fed47995be904b37e8f4cd03acd1f06b2769f915c5f98a7",
            _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Missing Phase 08 branch/control golden hash.")
        };
}
