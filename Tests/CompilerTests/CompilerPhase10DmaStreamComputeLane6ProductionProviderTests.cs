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
using YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

/// <summary>
/// Phase 10 boundary tests. DSC descriptor parsing, owner/domain guard
/// observation, footprint normalization, and lane facts are evidence for a
/// runtime-owned submission; they are not compiler execution authority.
/// </summary>
public sealed class CompilerPhase10DmaStreamComputeLane6ProductionProviderTests
{
    private const string ExpectedGoldenHash =
        "b23ff72f74709b974c96915ed4381620c552bd9a9d32004394b0d1256a5335e7";

    [Fact]
    public void DescriptorBackedLane6CandidateProducesRuntimePendingPackage()
    {
        CompilerEmissionPackage candidate = CreateDscPackage();
        CompilerProductionLoweringResult result = Produce(
            candidate,
            CreateDscIntent(),
            Analyze(ExecutionContourKind.DmaStreamComputeLane6, CreateDscIntent()));

        Assert.True(
            result.ResultKind == CompilerProductionLoweringResultKind.RuntimeAuthorityPending,
            result.Reason);
        Assert.True(result.GateResult.IsSatisfied, result.GateResult.Reason);
        Assert.NotNull(result.Package);
        Assert.Equal(
            "DmaStreamComputeLane6ProductionProvider",
            result.Package!.Identity.ProducerSurface);
        Assert.Equal(candidate.Carrier!.Image.SerializedImage, result.Package.Carrier!.Image.SerializedImage);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
        Assert.Contains("golden-artifact=verified", result.TelemetryEvidence);
        Assert.Contains("descriptor-identity-reference=verified", result.TelemetryEvidence);
        Assert.Contains("normalized-footprint=verified", result.TelemetryEvidence);
        Assert.Contains("ise-lane6-decode-parity=verified", result.TelemetryEvidence);
        Assert.Contains("owner-domain-guard=observed-accepted-evidence-only", result.TelemetryEvidence);
        Assert.Contains(
            result.TelemetryEvidence,
            entry => entry.StartsWith("descriptor-facts=", StringComparison.Ordinal));
        Assert.Contains(
            "future-runtime-gates=queue-token-fence,order-cache-fault,completion-route",
            result.TelemetryEvidence);

        CompilerToIseParitySnapshot parity = CompilerToIseParityHarness.AssertContourAndOpcode(
            result.Package,
            ExecutionContourKind.DmaStreamComputeLane6,
            InstructionsEnum.DmaStreamCompute);
        Assert.Equal(ExpectedGoldenHash, parity.CarrierBytesHash);
        Assert.Equal(parity.CarrierBytesHash, parity.ReencodedBytesHash);
        CompilerToIseParityHarness.AssertRuntimeAuthorityPending(result.Package);
    }

    [Fact]
    public void ExplicitLane6ProfileResolvesProviderButCompatibilityProfileDoesNot()
    {
        CompilerEmissionPackage candidate = CreateDscPackage();
        CompilerProductionLoweringContext compatibility = CreateContext(candidate) with
        {
            ProductionProfile = CreateProfile(CompilerProductionLoweringProfileMode.CompatibilityOnly)
        };

        Assert.Null(RuntimeContourLoweringProviderRegistry.Instance.ResolveProductionProvider(
            ExecutionContourKind.DmaStreamComputeLane6,
            compatibility));

        Assert.Same(
            DmaStreamComputeLane6ProductionProvider.Instance,
            RuntimeContourLoweringProviderRegistry.Instance.ResolveProductionProvider(
                ExecutionContourKind.DmaStreamComputeLane6,
                CreateContext(candidate)));
    }

    [Fact]
    public void MissingLane6ContourGateRemainsFutureGated()
    {
        CompilerEmissionPackage candidate = CreateDscPackage();
        CompilerProductionLoweringContext context = CreateContext(candidate) with
        {
            ProductionProfile = CreateProfileWithout(CompilerProductionLoweringGateIds.Contour(
                ExecutionContourKind.DmaStreamComputeLane6))
        };

        CompilerProductionLoweringResult result = Produce(
            candidate,
            CreateDscIntent(),
            Analyze(ExecutionContourKind.DmaStreamComputeLane6, CreateDscIntent()),
            context);

        Assert.Equal(CompilerProductionLoweringResultKind.FutureGated, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains(
            CompilerProductionLoweringGateIds.Contour(ExecutionContourKind.DmaStreamComputeLane6),
            result.GateResult.MissingGates);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Fact]
    public void DescriptorlessSubmitFailsClosed()
    {
        CompilerEmissionPackage candidate = CreateDescriptorlessDscPackage();
        CompilerProductionLoweringResult result = Produce(
            candidate,
            CreateDscIntent(),
            Analyze(ExecutionContourKind.DmaStreamComputeLane6, CreateDscIntent()));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains("descriptor-backed", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Theory]
    [InlineData("owner-guard")]
    [InlineData("wrong-device")]
    [InlineData("missing-footprint")]
    [InlineData("partial-completion")]
    [InlineData("unsupported-operation")]
    public void DescriptorAdmissionFactsFailClosed(string mutation)
    {
        CompilerEmissionPackage candidate = TamperDescriptor(CreateDscPackage(), mutation);
        CompilerProductionLoweringResult result = Produce(
            candidate,
            CreateDscIntent(),
            Analyze(ExecutionContourKind.DmaStreamComputeLane6, CreateDscIntent()));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
        Assert.NotEmpty(result.Reason);
    }

    [Fact]
    public void L7DescriptorCannotBeRoutedIntoDscLane6Provider()
    {
        CompilerEmissionPackage candidate = CreateDscPackage();
        DescriptorEnvelope l7Envelope = candidate.Descriptor! with
        {
            Descriptors = [new object()]
        };
        candidate = candidate with
        {
            Descriptor = l7Envelope,
            RuntimeBridgeInput = candidate.RuntimeBridgeInput! with { Descriptor = l7Envelope }
        };

        CompilerProductionLoweringResult result = Produce(
            candidate,
            CreateDscIntent(),
            Analyze(ExecutionContourKind.DmaStreamComputeLane6, CreateDscIntent()));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Contains("non-DSC", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Fact]
    public void L7AnalysisCannotSatisfyDscProvider()
    {
        CompilerSemanticIntent intent = CreateDscIntent();
        CompilerProductionLoweringResult result = Produce(
            CreateDscPackage(),
            intent,
            Analyze(ExecutionContourKind.L7SdcLane7, intent));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains("cross-contour", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Fact]
    public void ParserOrGuardObservationFlagsCannotBecomeProductionIntent()
    {
        CompilerSemanticIntent parserIntent = CreateDscIntent() with { IsParserOnly = true };
        CompilerProductionLoweringResult parserResult = Produce(
            CreateDscPackage(),
            parserIntent,
            Analyze(ExecutionContourKind.DmaStreamComputeLane6, parserIntent));

        Assert.Equal(CompilerProductionLoweringResultKind.FutureGated, parserResult.ResultKind);
        Assert.Null(parserResult.Package);

        CompilerProductionLoweringResult guardResult = Produce(
            TamperDescriptor(CreateDscPackage(), "owner-guard"),
            CreateDscIntent(),
            Analyze(ExecutionContourKind.DmaStreamComputeLane6, CreateDscIntent()));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, guardResult.ResultKind);
        Assert.Contains("owner/domain guard", guardResult.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static CompilerProductionLoweringResult Produce(
        CompilerEmissionPackage candidate,
        CompilerSemanticIntent intent,
        ContourAnalysisReport analysis,
        CompilerProductionLoweringContext? context = null) =>
        DmaStreamComputeLane6ProductionProvider.Instance.TryProduce(
            intent,
            analysis,
            context ?? CreateContext(candidate));

    private static CompilerProductionLoweringContext CreateContext(CompilerEmissionPackage candidate) =>
        new(
            new CompilerTargetProfile(
                "phase10-dsc-lane6-production",
                AllowsCarrierEmission: true,
                AllowsBackendEmission: true),
            "CompilerPhase10DmaStreamComputeLane6ProductionProviderTests",
            CreateProfile(CompilerProductionLoweringProfileMode.ExplicitlyEnabled))
        {
            CandidatePackage = candidate,
            Readiness = CompilerProductionLoweringReadiness.Complete
        };

    private static CompilerProductionLoweringProfile CreateProfile(
        CompilerProductionLoweringProfileMode mode) =>
        new(
            "phase10-dsc-lane6-profile",
            mode,
            new HashSet<ExecutionContourKind> { ExecutionContourKind.DmaStreamComputeLane6 },
            CompilerProductionLoweringGateIds.AllFor(ExecutionContourKind.DmaStreamComputeLane6));

    private static CompilerProductionLoweringProfile CreateProfileWithout(string gateId)
    {
        HashSet<string> gateIds = new(
            CompilerProductionLoweringGateIds.AllFor(ExecutionContourKind.DmaStreamComputeLane6));
        gateIds.Remove(gateId);
        return new(
            "phase10-dsc-lane6-profile-without-gate",
            CompilerProductionLoweringProfileMode.ExplicitlyEnabled,
            new HashSet<ExecutionContourKind> { ExecutionContourKind.DmaStreamComputeLane6 },
            gateIds);
    }

    private static ContourAnalysisReport Analyze(
        ExecutionContourKind contourKind,
        CompilerSemanticIntent intent) =>
        DefaultContourLoweringProviderRegistry.Instance
            .ResolveAnalyzer(contourKind)
            .Analyze(
                intent,
                new CompilerLoweringContext(
                    new CompilerTargetProfile(
                        "phase10-dsc-analysis",
                        AllowsCarrierEmission: true,
                        AllowsBackendEmission: true),
                    "CompilerPhase10DmaStreamComputeLane6ProductionProviderTests"));

    private static CompilerSemanticIntent CreateDscIntent() =>
        new(
            SemanticIntentKind.DmaStreamCompute,
            "DmaStreamCompute",
            RequiresDescriptor: true,
            RequiresSideband: true,
            RequiresToken: true,
            RequiresRuntimeLegality: true,
            IsCompatibilityProjection: false,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: false,
            IsParserOnly: false,
            "Phase 10 explicit descriptor-backed DSC lane6 production intent.");

    private static CompilerEmissionPackage CreateDscPackage()
    {
        HybridCpuThreadCompilerContext compiler = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        compiler.CompileDmaStreamCompute(DmaStreamComputeTestDescriptorFactory.CreateDescriptor());
        HybridCpuCompiledProgram compiled = compiler.CompileProgram();
        CompilerEmissionPackage package = HybridCpuCompiledProgramEnvelopeAdapter.Instance.Project(
            compiled,
            new CompilerArtifactProjectionOptions(
                SemanticIntentKind.DmaStreamCompute,
                ExecutionContourKind.DmaStreamComputeLane6,
                "HybridCpuThreadCompilerContext.CompileDmaStreamCompute",
                "Phase 10 descriptor-backed DSC lane6 shadow candidate."));
        CompilerSidebandEnvelope sideband = package.Sideband! with
        {
            Requirement = SidebandRequirement.RequiredForDescriptorSubmit,
            PreservationClass = SidebandPreservationClass.PreservedCompilerSideband,
            IsEmptyCompatibilitySideband = false
        };
        return package with
        {
            Sideband = sideband,
            RuntimeBridgeInput = package.RuntimeBridgeInput! with { Sideband = sideband }
        };
    }

    private static CompilerEmissionPackage CreateDescriptorlessDscPackage()
    {
        HybridCpuThreadCompilerContext compiler = new HybridCpuThreadCompilerContext(virtualThreadId: 0);
        compiler.CompileInstruction(
            (uint)InstructionsEnum.DmaStreamCompute,
            (byte)DataTypeEnum.INT32,
            predicate: 0,
            immediate: 0,
            destSrc1: 0,
            src2: 0,
            streamLength: 0,
            stride: 0,
            StealabilityPolicy.NotStealable);
        HybridCpuCompiledProgram compiled = compiler.CompileProgram();
        return HybridCpuCompiledProgramEnvelopeAdapter.Instance.Project(
            compiled,
            new CompilerArtifactProjectionOptions(
                SemanticIntentKind.DmaStreamCompute,
                ExecutionContourKind.DmaStreamComputeLane6,
                "HybridCpuThreadCompilerContext.CompileInstruction",
                "Phase 10 descriptorless DSC negative fixture."));
    }

    private static CompilerEmissionPackage TamperDescriptor(
        CompilerEmissionPackage package,
        string mutation)
    {
        DmaStreamComputeDescriptor descriptor =
            (DmaStreamComputeDescriptor)package.Descriptor!.Descriptors.Single();
        DmaStreamComputeDescriptor changed = mutation switch
        {
            "owner-guard" => descriptor with
            {
                OwnerGuardDecision = default(DmaStreamComputeOwnerGuardDecision)
            },
            "wrong-device" => descriptor with
            {
                OwnerBinding = descriptor.OwnerBinding with { DeviceId = 7 }
            },
            "missing-footprint" => descriptor with
            {
                NormalizedReadMemoryRanges = Array.Empty<DmaStreamComputeMemoryRange>(),
                NormalizedFootprintHash = 0
            },
            "partial-completion" => descriptor with
            {
                PartialCompletionPolicy = (DmaStreamComputePartialCompletionPolicy)0
            },
            "unsupported-operation" => descriptor with
            {
                Operation = (DmaStreamComputeOperationKind)0xffff
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null)
        };

        DescriptorEnvelope envelope = package.Descriptor with
        {
            Descriptors = [changed]
        };
        return package with
        {
            Descriptor = envelope,
            RuntimeBridgeInput = package.RuntimeBridgeInput! with { Descriptor = envelope }
        };
    }
}
