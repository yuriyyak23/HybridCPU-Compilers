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
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Auth;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Descriptors;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

/// <summary>
/// Phase 11 boundary tests. L7 descriptor, owner/submit guard, normalized
/// footprint, and token-destination values are structural evidence only;
/// token lifecycle, backend protocol, completion and publication remain
/// runtime-owned.
/// </summary>
public sealed class CompilerPhase11L7SdcLane7ProductionProviderTests
{
    private const string ExpectedGoldenHash =
        "69a9371c9321d09fdc340015d8b5f7855285eed25bd9af531a7843ccba371fc9";

    [Fact]
    public void DescriptorBackedLane7CandidateProducesRuntimePendingPackage()
    {
        CompilerEmissionPackage candidate = CreateL7Package();
        CompilerSemanticIntent intent = CreateL7Intent();
        CompilerProductionLoweringResult result = Produce(
            candidate,
            intent,
            Analyze(ExecutionContourKind.L7SdcLane7, intent));

        Assert.True(
            result.ResultKind == CompilerProductionLoweringResultKind.RuntimeAuthorityPending,
            result.Reason);
        Assert.True(result.GateResult.IsSatisfied, result.GateResult.Reason);
        Assert.NotNull(result.Package);
        Assert.Equal("L7SdcLane7ProductionProvider", result.Package!.Identity.ProducerSurface);
        Assert.Equal(candidate.Carrier!.Image.SerializedImage, result.Package.Carrier!.Image.SerializedImage);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
        Assert.Contains("golden-artifact=verified", result.TelemetryEvidence);
        Assert.Contains("descriptor-identity-reference=verified", result.TelemetryEvidence);
        Assert.Contains("normalized-footprint=verified", result.TelemetryEvidence);
        Assert.Contains("ise-lane7-decode-parity=verified", result.TelemetryEvidence);
        Assert.Contains("owner-domain-guard=observed-accepted-evidence-only", result.TelemetryEvidence);
        Assert.Contains("submit-guard=observed-accepted-evidence-only", result.TelemetryEvidence);
        Assert.Contains("token-destination-structural-register=9", result.TelemetryEvidence);
        Assert.Contains(
            result.TelemetryEvidence,
            entry => entry.StartsWith("descriptor-facts=", StringComparison.Ordinal));

        CompilerToIseParitySnapshot parity = CompilerToIseParityHarness.AssertContourAndOpcode(
            result.Package,
            ExecutionContourKind.L7SdcLane7,
            InstructionsEnum.ACCEL_SUBMIT);
        Assert.Equal(ExpectedGoldenHash, parity.CarrierBytesHash);
        Assert.Equal(parity.CarrierBytesHash, parity.ReencodedBytesHash);
        CompilerToIseParityHarness.AssertRuntimeAuthorityPending(result.Package);
    }

    [Fact]
    public void ExplicitLane7ProfileResolvesProviderButCompatibilityProfileDoesNot()
    {
        CompilerEmissionPackage candidate = CreateL7Package();
        CompilerProductionLoweringContext compatibility = CreateContext(candidate) with
        {
            ProductionProfile = CreateProfile(CompilerProductionLoweringProfileMode.CompatibilityOnly)
        };

        Assert.Null(RuntimeContourLoweringProviderRegistry.Instance.ResolveProductionProvider(
            ExecutionContourKind.L7SdcLane7,
            compatibility));
        Assert.Same(
            L7SdcLane7ProductionProvider.Instance,
            RuntimeContourLoweringProviderRegistry.Instance.ResolveProductionProvider(
                ExecutionContourKind.L7SdcLane7,
                CreateContext(candidate)));
    }

    [Fact]
    public void MissingLane7ContourGateRemainsFutureGated()
    {
        CompilerEmissionPackage candidate = CreateL7Package();
        CompilerSemanticIntent intent = CreateL7Intent();
        CompilerProductionLoweringContext context = CreateContext(candidate) with
        {
            ProductionProfile = CreateProfileWithout(CompilerProductionLoweringGateIds.Contour(
                ExecutionContourKind.L7SdcLane7))
        };

        CompilerProductionLoweringResult result = Produce(
            candidate,
            intent,
            Analyze(ExecutionContourKind.L7SdcLane7, intent),
            context);

        Assert.Equal(CompilerProductionLoweringResultKind.FutureGated, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains(
            CompilerProductionLoweringGateIds.Contour(ExecutionContourKind.L7SdcLane7),
            result.GateResult.MissingGates);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Fact]
    public void DescriptorlessSubmitFailsClosed()
    {
        CompilerEmissionPackage candidate = CreateL7Package();
        DescriptorEnvelope descriptorless = candidate.Descriptor! with
        {
            Status = DescriptorAbiStatus.None,
            Descriptors = [],
            SidebandRequirement = SidebandRequirement.OptionalCompatibility
        };
        candidate = candidate with
        {
            Descriptor = descriptorless,
            RuntimeBridgeInput = candidate.RuntimeBridgeInput! with { Descriptor = descriptorless }
        };

        CompilerSemanticIntent intent = CreateL7Intent();
        CompilerProductionLoweringResult result = Produce(
            candidate,
            intent,
            Analyze(ExecutionContourKind.L7SdcLane7, intent));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains("descriptor-backed", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Theory]
    [InlineData("parser-only")]
    [InlineData("owner-guard")]
    [InlineData("submit-guard")]
    [InlineData("missing-footprint")]
    [InlineData("partial-completion")]
    public void DescriptorAndGuardAdmissionFactsFailClosed(string mutation)
    {
        CompilerEmissionPackage candidate = TamperDescriptor(CreateL7Package(), mutation);
        CompilerSemanticIntent intent = CreateL7Intent();
        CompilerProductionLoweringResult result = Produce(
            candidate,
            intent,
            Analyze(ExecutionContourKind.L7SdcLane7, intent));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Null(result.Package);
        Assert.NotEmpty(result.Reason);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Fact]
    public void DscDescriptorCannotBeRoutedIntoL7Lane7Provider()
    {
        CompilerEmissionPackage l7Package = CreateL7Package();
        DescriptorEnvelope dscEnvelope = l7Package.Descriptor! with
        {
            Descriptors = [DmaStreamComputeTestDescriptorFactory.CreateDescriptor()]
        };
        CompilerEmissionPackage candidate = l7Package with
        {
            Descriptor = dscEnvelope,
            RuntimeBridgeInput = l7Package.RuntimeBridgeInput! with { Descriptor = dscEnvelope }
        };
        CompilerSemanticIntent intent = CreateL7Intent();

        CompilerProductionLoweringResult result = Produce(
            candidate,
            intent,
            Analyze(ExecutionContourKind.L7SdcLane7, intent));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Contains("non-L7", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Theory]
    [InlineData(InstructionsEnum.DmaStreamCompute)]
    [InlineData(InstructionsEnum.VMXON)]
    public void NonLane7OpcodesFailClosedWithoutFallback(InstructionsEnum opcode)
    {
        CompilerEmissionPackage candidate = TamperCarrier(CreateL7Package(), instruction => instruction with
        {
            OpCode = (uint)opcode
        });
        CompilerSemanticIntent intent = CreateL7Intent();
        CompilerProductionLoweringResult result = Produce(
            candidate,
            intent,
            Analyze(ExecutionContourKind.L7SdcLane7, intent));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Contains("lane7", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    [Fact]
    public void DscAnalysisCannotSatisfyL7Provider()
    {
        CompilerSemanticIntent intent = CreateL7Intent();
        CompilerProductionLoweringResult result = Produce(
            CreateL7Package(),
            intent,
            Analyze(ExecutionContourKind.DmaStreamComputeLane6, intent));

        Assert.Equal(CompilerProductionLoweringResultKind.Rejected, result.ResultKind);
        Assert.Null(result.Package);
        Assert.Contains("cross-contour", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FallbackPolicyKind.Forbidden, result.NoFallbackProof.PolicyKind);
    }

    private static CompilerProductionLoweringResult Produce(
        CompilerEmissionPackage candidate,
        CompilerSemanticIntent intent,
        ContourAnalysisReport analysis,
        CompilerProductionLoweringContext? context = null) =>
        L7SdcLane7ProductionProvider.Instance.TryProduce(
            intent,
            analysis,
            context ?? CreateContext(candidate));

    private static CompilerProductionLoweringContext CreateContext(CompilerEmissionPackage candidate) =>
        new(
            new CompilerTargetProfile(
                "phase11-l7-sdc-lane7-production",
                AllowsCarrierEmission: true,
                AllowsBackendEmission: true),
            "CompilerPhase11L7SdcLane7ProductionProviderTests",
            CreateProfile(CompilerProductionLoweringProfileMode.ExplicitlyEnabled))
        {
            CandidatePackage = candidate,
            Readiness = CompilerProductionLoweringReadiness.Complete
        };

    private static CompilerProductionLoweringProfile CreateProfile(
        CompilerProductionLoweringProfileMode mode) =>
        new(
            "phase11-l7-sdc-lane7-profile",
            mode,
            new HashSet<ExecutionContourKind> { ExecutionContourKind.L7SdcLane7 },
            CompilerProductionLoweringGateIds.AllFor(ExecutionContourKind.L7SdcLane7));

    private static CompilerProductionLoweringProfile CreateProfileWithout(string gateId)
    {
        HashSet<string> gateIds = new(
            CompilerProductionLoweringGateIds.AllFor(ExecutionContourKind.L7SdcLane7));
        gateIds.Remove(gateId);
        return new(
            "phase11-l7-sdc-lane7-profile-without-gate",
            CompilerProductionLoweringProfileMode.ExplicitlyEnabled,
            new HashSet<ExecutionContourKind> { ExecutionContourKind.L7SdcLane7 },
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
                        "phase11-l7-sdc-analysis",
                        AllowsCarrierEmission: true,
                        AllowsBackendEmission: true),
                    "CompilerPhase11L7SdcLane7ProductionProviderTests"));

    private static CompilerSemanticIntent CreateL7Intent() =>
        new(
            SemanticIntentKind.ExternalAcceleratorCommand,
            "ACCEL_SUBMIT",
            RequiresDescriptor: true,
            RequiresSideband: true,
            RequiresToken: true,
            RequiresRuntimeLegality: true,
            IsCompatibilityProjection: false,
            IsPolicyAdmissionOnly: false,
            IsHelperAbiOnly: false,
            IsParserOnly: false,
            "Phase 11 explicit descriptor-backed L7-SDC lane7 production intent.");

    private static CompilerEmissionPackage CreateL7Package()
    {
        AcceleratorCommandDescriptor descriptor = L7SdcTestDescriptorFactory.ParseValidDescriptor();
        HybridCpuThreadCompilerContext compiler = L7SdcCompilerEmissionTests.CreateContextForDescriptor(descriptor);
        compiler.CompileAcceleratorSubmit(
            IrAcceleratorIntent.ForMatMul(descriptor, tokenDestinationRegister: 9),
            CompilerAcceleratorCapabilityModel.ReferenceMatMul);
        CompilerEmissionPackage package = HybridCpuCompiledProgramEnvelopeAdapter.Instance.Project(
            compiler.CompileProgram(),
            new CompilerArtifactProjectionOptions(
                SemanticIntentKind.ExternalAcceleratorCommand,
                ExecutionContourKind.L7SdcLane7,
                "HybridCpuThreadCompilerContext.CompileAcceleratorSubmit",
                "Phase 11 descriptor-backed L7-SDC lane7 shadow candidate."));

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

    private static CompilerEmissionPackage TamperDescriptor(
        CompilerEmissionPackage package,
        string mutation)
    {
        AcceleratorCommandDescriptor descriptor =
            (AcceleratorCommandDescriptor)package.Descriptor!.Descriptors.Single();
        AcceleratorCommandDescriptor changed = mutation switch
        {
            "parser-only" => descriptor,
            "owner-guard" or "submit-guard" => descriptor with
            {
                OwnerGuardDecision = default(AcceleratorGuardDecision)
            },
            "missing-footprint" => descriptor with
            {
                Identity = descriptor.Identity with { NormalizedFootprintHash = 0 },
                NormalizedFootprint = descriptor.NormalizedFootprint with
                {
                    SourceRanges = Array.Empty<AcceleratorMemoryRange>(),
                    Hash = 0
                }
            },
            "partial-completion" => descriptor with
            {
                PartialCompletionPolicy = (AcceleratorPartialCompletionPolicy)0
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null)
        };

        DescriptorAbiStatus status = mutation == "parser-only"
            ? DescriptorAbiStatus.ParserOnlyDescriptor
            : DescriptorAbiStatus.ValidTransportDescriptor;
        DescriptorEnvelope envelope = package.Descriptor with
        {
            Status = status,
            Descriptors = [changed]
        };
        return package with
        {
            Descriptor = envelope,
            RuntimeBridgeInput = package.RuntimeBridgeInput! with { Descriptor = envelope }
        };
    }

    private static CompilerEmissionPackage TamperCarrier(
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
}
