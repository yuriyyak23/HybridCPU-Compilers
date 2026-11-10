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
/// Phase 06 tests for the first explicit production provider. The provider
/// only packages an already-built candidate for shadow comparison; all ISE
/// execution and runtime authority remain outside the compiler result.
/// </summary>
public sealed class CompilerPhase06NativeVliwScalarProductionProviderTests
{
    [Theory]
    [InlineData(InstructionsEnum.ADD, 0)]
    [InlineData(InstructionsEnum.SUB, 0)]
    [InlineData(InstructionsEnum.MUL, 0)]
    [InlineData(InstructionsEnum.ADDI, 7)]
    public void SupportedScalarCarriersProduceRuntimePendingPackages(
        InstructionsEnum opcode,
        int immediate)
    {
        CompilerEmissionPackage candidate = CreateScalarPackage(opcode, immediate);
        CompilerSemanticIntent intent = CreateScalarIntent(opcode);
        CompilerProductionLoweringContext context = CreateProductionContext(candidate);
        ContourAnalysisReport analysis = Analyze(intent);
        IContourProductionLoweringProvider provider = ResolveProvider(context);

        CompilerProductionLoweringResult result =
            provider.TryProduce(intent, analysis, context);

        Assert.True(
            result.ResultKind == CompilerProductionLoweringResultKind.RuntimeAuthorityPending,
            $"Phase 06 provider rejected {opcode}: {result.Reason}");
        Assert.True(result.GateResult.IsSatisfied);
        Assert.NotNull(result.Package);
        Assert.Equal(
            "NativeVliwScalarProductionProvider",
            result.Package!.Identity.ProducerSurface);
        Assert.Equal(
            candidate.Carrier!.Image.SerializedImage,
            result.Package.Carrier!.Image.SerializedImage);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
        Assert.Contains("contour=NativeVliwScalar", result.TelemetryEvidence);
        Assert.Contains("producer-surface=NativeVliwScalarProductionProvider", result.TelemetryEvidence);
        Assert.Contains(result.TelemetryEvidence, entry => entry.StartsWith("artifact-id=", StringComparison.Ordinal));

        CompilerToIseParitySnapshot parity =
            CompilerToIseParityHarness.AssertContourAndOpcode(
                result.Package,
                ExecutionContourKind.NativeVliwScalar,
                opcode);
        Assert.Equal(parity.CarrierBytesHash, parity.ReencodedBytesHash);
        CompilerToIseParityHarness.AssertRuntimeAuthorityPending(result.Package);

        if (opcode == InstructionsEnum.ADD)
        {
            string repoRoot = CompatFreezeScanner.FindRepoRoot();
            CompilerGoldenArtifactManifest manifest =
                CompilerGoldenArtifactHarness.LoadManifest(
                    repoRoot,
                    "HybridCPU_ISE.Tests/TestData/CompilerGoldenArtifacts/positive-manifest.json");
            CompilerGoldenArtifactEntry golden = Assert.Single(
                manifest.Entries,
                entry => entry.ArtifactId == "scalar-vliw-add-carrier");
            Assert.Equal(golden.CarrierWordsOrBytesHash, parity.CarrierBytesHash);
        }
    }

    [Fact]
    public void ExplicitScalarProfileResolvesProviderButCompatibilityProfileDoesNot()
    {
        CompilerEmissionPackage candidate = CreateScalarPackage(InstructionsEnum.ADD, 0);

        CompilerProductionLoweringContext compatibility =
            CreateProductionContext(candidate) with
            {
                ProductionProfile = CreateProfile(
                    CompilerProductionLoweringProfileMode.CompatibilityOnly)
            };
        Assert.Null(
            RuntimeContourLoweringProviderRegistry.Instance.ResolveProductionProvider(
                ExecutionContourKind.NativeVliwScalar,
                compatibility));

        CompilerProductionLoweringContext explicitProfile = CreateProductionContext(candidate);
        Assert.Same(
            NativeVliwScalarProductionProvider.Instance,
            RuntimeContourLoweringProviderRegistry.Instance.ResolveProductionProvider(
                ExecutionContourKind.NativeVliwScalar,
                explicitProfile));
    }

    [Fact]
    public void MissingProfileGateRemainsFutureGated()
    {
        CompilerEmissionPackage candidate = CreateScalarPackage(InstructionsEnum.ADD, 0);
        CompilerSemanticIntent intent = CreateScalarIntent(InstructionsEnum.ADD);
        CompilerProductionLoweringContext context = CreateProductionContext(candidate) with
        {
            ProductionProfile = new CompilerProductionLoweringProfile(
                "phase06-missing-profile-gate",
                CompilerProductionLoweringProfileMode.ExplicitlyEnabled,
                new HashSet<ExecutionContourKind> { ExecutionContourKind.NativeVliwScalar },
                CreateGateSetWithout(CompilerProductionLoweringGateIds.Profile))
        };

        CompilerProductionLoweringResult result =
            NativeVliwScalarProductionProvider.Instance.TryProduce(
                intent,
                Analyze(intent),
                context);

        Assert.Equal(CompilerProductionLoweringResultKind.FutureGated, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains(CompilerProductionLoweringGateIds.Profile, result.GateResult.MissingGates);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Fact]
    public void MissingGoldenOrParityReadinessRemainsFutureGated()
    {
        CompilerEmissionPackage candidate = CreateScalarPackage(InstructionsEnum.ADD, 0);
        CompilerSemanticIntent intent = CreateScalarIntent(InstructionsEnum.ADD);
        CompilerProductionLoweringContext context = CreateProductionContext(candidate) with
        {
            Readiness = new CompilerProductionLoweringReadiness(
                GoldenArtifactCoverage: false,
                IseDecodeParityPresent: true,
                TelemetryComplete: true,
                EvidenceComplete: true)
        };

        CompilerProductionLoweringResult result =
            NativeVliwScalarProductionProvider.Instance.TryProduce(
                intent,
                Analyze(intent),
                context);

        Assert.Equal(CompilerProductionLoweringResultKind.FutureGated, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains(CompilerProductionLoweringGateIds.Parity, result.GateResult.MissingGates);
    }

    [Theory]
    [InlineData(SemanticIntentKind.LoadStore, ExecutionContourKind.NativeVliwLoadStore)]
    [InlineData(SemanticIntentKind.BranchControl, ExecutionContourKind.NativeVliwBranchControl)]
    [InlineData(SemanticIntentKind.VectorStream, ExecutionContourKind.StreamEngineVector)]
    [InlineData(SemanticIntentKind.MatrixTile, ExecutionContourKind.MatrixTileHelperOnly)]
    [InlineData(SemanticIntentKind.DmaStreamCompute, ExecutionContourKind.DmaStreamComputeLane6)]
    [InlineData(SemanticIntentKind.ExternalAcceleratorCommand, ExecutionContourKind.L7SdcLane7)]
    [InlineData(SemanticIntentKind.VmxCompatibilityProjection, ExecutionContourKind.VmxProjectionOnly)]
    [InlineData(SemanticIntentKind.SecureComputeAdmission, ExecutionContourKind.SecureComputePolicyAdmissionOnly)]
    public void NonScalarContoursAreRejectedWithoutFallback(
        SemanticIntentKind intentKind,
        ExecutionContourKind contourKind)
    {
        CompilerEmissionPackage candidate = CreateScalarPackage(InstructionsEnum.ADD, 0);
        CompilerSemanticIntent intent = CreateNonScalarIntent(intentKind);
        CompilerProductionLoweringContext context = CreateProductionContext(candidate);
        ContourAnalysisReport analysis =
            DefaultContourLoweringProviderRegistry.Instance
                .ResolveAnalyzer(contourKind)
                .Analyze(
                    intent,
                    new CompilerLoweringContext(
                        new CompilerTargetProfile(
                            "phase06-negative-analysis",
                            AllowsCarrierEmission: true,
                            AllowsBackendEmission: true),
                        "CompilerPhase06NativeVliwScalarProductionProviderTests"));

        CompilerProductionLoweringResult result =
            NativeVliwScalarProductionProvider.Instance.TryProduce(
                intent,
                analysis,
                context);

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
        Assert.Contains("cross-contour", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CandidateWithWrongContourIdentityIsRejected()
    {
        CompilerEmissionPackage candidate = CreateScalarPackage(InstructionsEnum.ADD, 0) with
        {
            Identity = CreateScalarPackage(InstructionsEnum.ADD, 0).Identity with
            {
                ContourKind = ExecutionContourKind.NativeVliwLoadStore
            }
        };
        CompilerSemanticIntent intent = CreateScalarIntent(InstructionsEnum.ADD);

        CompilerProductionLoweringResult result =
            NativeVliwScalarProductionProvider.Instance.TryProduce(
                intent,
                Analyze(intent),
                CreateProductionContext(candidate));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains("exact scalar contour", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static IContourProductionLoweringProvider ResolveProvider(
        CompilerProductionLoweringContext context) =>
        RuntimeContourLoweringProviderRegistry.Instance.ResolveProductionProvider(
            ExecutionContourKind.NativeVliwScalar,
            context) ?? throw new InvalidOperationException("Phase 06 scalar provider was not resolved.");

    private static ContourAnalysisReport Analyze(CompilerSemanticIntent intent) =>
        DefaultContourLoweringProviderRegistry.Instance
            .ResolveAnalyzer(ExecutionContourKind.NativeVliwScalar)
            .Analyze(
                intent,
                new CompilerLoweringContext(
                    new CompilerTargetProfile(
                        "phase06-scalar-analysis",
                        AllowsCarrierEmission: true,
                        AllowsBackendEmission: true),
                    "CompilerPhase06NativeVliwScalarProductionProviderTests"));

    private static CompilerProductionLoweringContext CreateProductionContext(
        CompilerEmissionPackage candidate) =>
        new(
            new CompilerTargetProfile(
                "phase06-scalar-production",
                AllowsCarrierEmission: true,
                AllowsBackendEmission: true),
            "CompilerPhase06NativeVliwScalarProductionProviderTests",
            CreateProfile(CompilerProductionLoweringProfileMode.ExplicitlyEnabled))
        {
            CandidatePackage = candidate,
            Readiness = CompilerProductionLoweringReadiness.Complete
        };

    private static CompilerProductionLoweringProfile CreateProfile(
        CompilerProductionLoweringProfileMode mode) =>
        new(
            "phase06-native-vliw-scalar-profile",
            mode,
            new HashSet<ExecutionContourKind> { ExecutionContourKind.NativeVliwScalar },
            CompilerProductionLoweringGateIds.AllFor(ExecutionContourKind.NativeVliwScalar));

    private static IReadOnlySet<string> CreateGateSetWithout(string gateId)
    {
        HashSet<string> gates = new HashSet<string>(
            CompilerProductionLoweringGateIds.AllFor(ExecutionContourKind.NativeVliwScalar),
            StringComparer.Ordinal);
        gates.Remove(gateId);
        return gates;
    }

    private static CompilerSemanticIntent CreateScalarIntent(InstructionsEnum opcode) =>
        new(
            SemanticIntentKind.ScalarAlu,
            opcode.ToString(),
            RequiresDescriptor: false,
            RequiresSideband: false,
            RequiresToken: false,
            RequiresRuntimeLegality: true,
            IsCompatibilityProjection: false,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: false,
            IsParserOnly: false,
            "Phase 06 explicit scalar production intent.");

    private static CompilerSemanticIntent CreateNonScalarIntent(
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
            RequiresRuntimeLegality: intentKind is not
                (SemanticIntentKind.VmxCompatibilityProjection or
                 SemanticIntentKind.SecureComputeAdmission),
            IsCompatibilityProjection: intentKind == SemanticIntentKind.VmxCompatibilityProjection,
            IsPolicyAdmissionOnly: intentKind == SemanticIntentKind.SecureComputeAdmission,
            IsHelperAbiOnly: intentKind is SemanticIntentKind.VectorStream or SemanticIntentKind.MatrixTile,
            IsParserOnly: false,
            "Phase 06 negative contour boundary intent.");

    private static CompilerEmissionPackage CreateScalarPackage(
        InstructionsEnum opcode,
        int immediate)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        context.CompileInstruction(
            (uint)opcode,
            (byte)DataTypeEnum.INT32,
            predicate: 0,
            immediate: checked((ushort)immediate),
            destSrc1: VLIW_Instruction.PackArchRegs(1, 2, 0),
            src2: 0,
            streamLength: 0,
            stride: 0,
            StealabilityPolicy.NotStealable);

        HybridCpuCompiledProgram compiled = context.CompileProgram();
        return HybridCpuCompiledProgramEnvelopeAdapter.Instance.Project(
            compiled,
            new CompilerArtifactProjectionOptions(
                SemanticIntentKind.ScalarAlu,
                ExecutionContourKind.NativeVliwScalar,
                "HybridCpuThreadCompilerContext.CompileInstruction",
                "Phase 06 scalar shadow candidate."));
    }
}
