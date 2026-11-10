using System;
using System.Linq;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Bridge;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Evidence;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.IR.Lowering;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Tests.MemoryAccelerators;
using HybridCPU_ISE.Tests.TestHelpers;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Decoder;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Capabilities;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Descriptors;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Telemetry;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;
using CompilerTypedSlotFactStaging = HybridCPU.Compiler.Core.IR.Bridge.TypedSlotFactStaging;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerPhase09L7SdcLane7NegativeMatrixTests
{
    [Fact]
    public void L7SdcDescriptorlessAccelSubmitFailsClosedAtDecoderIngress()
    {
        VLIW_Instruction[] rawSlots = new VLIW_Instruction[BundleMetadata.BundleSlotCount];
        rawSlots[7] = L7SdcPhase03TestCases.CreateNativeInstruction(InstructionsEnum.ACCEL_SUBMIT);

        InvalidOpcodeException ex = Assert.Throws<InvalidOpcodeException>(
            () => new VliwDecoderV4().DecodeInstructionBundle(
                rawSlots,
                bundleAddress: 0xE700,
                bundleSerial: 7));

        Assert.Contains("ACCEL_SUBMIT", ex.Message, StringComparison.Ordinal);
        Assert.Contains("typed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("illegal", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DmaStreamComputeMicroOp", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("StreamEngine", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Scalar", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void L7SdcDirectDescriptorlessCompilerEmissionRejectsBeforeCarrierOrFallback()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => context.CompileInstruction(
                opCode: (uint)InstructionsEnum.ACCEL_SUBMIT,
                dataType: 0,
                predicate: 0,
                immediate: 0,
                destSrc1: VLIW_Instruction.PackArchRegs(
                    VLIW_Instruction.NoArchReg,
                    VLIW_Instruction.NoArchReg,
                    VLIW_Instruction.NoArchReg),
                src2: 0,
                streamLength: 0,
                stride: 0,
                stealabilityPolicy: StealabilityPolicy.NotStealable));

        Assert.Contains("explicit accelerator intent", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, context.InstructionCount);
    }

    [Fact]
    public void L7SdcRejectedSubmitIntentHasNoDscStreamOrScalarFallbackAfterSubmit()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        HybridCpuThreadCompilerContext context =
            L7SdcCompilerEmissionTests.CreateContextForDescriptor(descriptor);
        IrAcceleratorIntent intent =
            IrAcceleratorIntent.ForMatMul(descriptor) with
            {
                AllowRuntimeFallbackAfterSubmit = true
            };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => context.CompileAcceleratorSubmit(
                intent,
                CompilerAcceleratorCapabilityModel.ReferenceMatMul));

        Assert.Contains("runtime fallback", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, context.InstructionCount);
    }

    [Fact]
    public void L7SdcLane7ContourProviderRejectsLoweringWithoutDscStreamOrScalarFallback()
    {
        CompilerSemanticIntent intent = new CompilerSemanticIntent(
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
            "Phase09 L7-SDC lane7 negative gate.");
        CompilerLoweringContext context = new CompilerLoweringContext(
            new CompilerTargetProfile("phase09-l7-sdc-lane7"),
            "CompilerPhase09L7SdcLane7NegativeMatrixTests");

        IContourAnalyzer analyzer =
            DefaultContourLoweringProviderRegistry.Instance.ResolveAnalyzer(ExecutionContourKind.L7SdcLane7);
        ContourAnalysisReport analysis = analyzer.Analyze(intent, context);
        IContourLoweringProvider provider =
            DefaultContourLoweringProviderRegistry.Instance.ResolveProvider(ExecutionContourKind.L7SdcLane7);
        CompilerLoweringDecision decision = provider.Lower(intent, analysis, context);

        Assert.True(analysis.ProviderAvailable);
        Assert.Equal(ExecutionContourKind.L7SdcLane7, analysis.ContourKind);
        Assert.Equal(CompilerCapabilityObservationState.ScopedRuntimeContour, analysis.CapabilityObservation.State);
        Assert.Contains("DSC/Stream/scalar fallback is forbidden", analysis.CapabilityObservation.Reason, StringComparison.Ordinal);
        Assert.Equal(CompilerLoweringDecisionKind.Rejected, decision.DecisionKind);
        Assert.Equal(CompilerRejectReason.RuntimeAuthorityOwned, Assert.Single(decision.RejectReasons));
        Assert.Equal(CompilerEmissionClass.NoEmission, decision.EmissionClass);
        Assert.Equal(CompilerProductionLoweringStatus.Rejected, decision.ProductionLoweringStatus);
        Assert.False(decision.FallbackPolicy.AllowsCrossContourFallback);
        Assert.Equal(FallbackPolicyKind.Forbidden, decision.NoFallbackProof.PolicyKind);
    }

    [Fact]
    public void L7SdcCapabilityTelemetryAndCompilerEvidenceRemainEvidenceOnly()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        AcceleratorTelemetry telemetry = new AcceleratorTelemetry();
        AcceleratorCapabilityRegistry registry = new AcceleratorCapabilityRegistry(telemetry);
        registry.RegisterProvider(new MatMulCapabilityProvider());

        AcceleratorCapabilityQueryResult query =
            registry.Query("matmul.fixture.v1");
        AcceleratorCapabilityAcceptanceResult noGuardAcceptance =
            registry.AcceptCapability(
                "matmul.fixture.v1",
                descriptor.OwnerBinding);
        AcceleratorTelemetrySnapshot telemetrySnapshot = telemetry.Snapshot();

        Assert.True(query.IsMetadataAvailable);
        Assert.True(noGuardAcceptance.IsRejected);
        Assert.False(noGuardAcceptance.GrantsDecodeAuthority);
        Assert.False(noGuardAcceptance.GrantsCommandSubmissionAuthority);
        Assert.False(noGuardAcceptance.GrantsExecutionAuthority);
        Assert.False(noGuardAcceptance.GrantsCommitAuthority);
        Assert.Equal(0, telemetrySnapshot.SubmitAccepted);

        CompilerEvidenceRecord evidenceRecord = new CompilerEvidenceRecord(
            CompilerEvidenceClass.RuntimeContractObservationEvidence,
            EvidenceOwnershipDomain.CompilerHostOwned,
            EvidenceAuthoritySemantics.EvidenceOnly,
            "phase09-l7-capability-observation",
            "Capability metadata, telemetry, token and evidence observations are not runtime authority.",
            IsAuthority: false,
            "compiler evidence only; runtime legality, execution, commit, retire and publication remain required");
        CompilerEvidenceSnapshot snapshot = new CompilerEvidenceSnapshot(
            Guid.NewGuid(),
            SemanticIntentKind.ExternalAcceleratorCommand,
            ExecutionContourKind.L7SdcLane7,
            CompilerCapabilityObservationState.ScopedRuntimeContour,
            new CompilerLoweringDecisionSummary(
                CompilerLoweringDecisionKind.Rejected,
                CompilerEmissionClass.NoEmission,
                CompilerProductionLoweringStatus.Rejected,
                CompilerRejectReason.RuntimeAuthorityOwned,
                CompilerExecutionClaim.NoExecutionClaim,
                CompilerPublicationClass.EvidenceOnly,
                CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
                CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
                CompilerRuntimeAuthorityDependency.RuntimeCommitRequired |
                CompilerRuntimeAuthorityDependency.RuntimeRetireRequired |
                CompilerRuntimeAuthorityDependency.RuntimePublicationRequired |
                CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired,
                CarrierEmitted: false,
                SidebandEmitted: false,
                DescriptorEmitted: false,
                TypedSlotFactsEmitted: false,
                StructuralAgreementEmitted: false,
                EvidenceEmitted: true,
                BridgeEnvelopePrepared: false,
                ProductionLoweringClaimed: false),
            CompilerAuthorityClass.CompilerEvidenceProduction,
            CompilerAuthoritySourceKind.CompilerStructuralModel,
            CompilerEvidenceClass.RuntimeContractObservationEvidence,
            EvidenceOwnershipDomain.CompilerHostOwned,
            EvidenceAuthoritySemantics.EvidenceOnly,
            SidebandRequirement.RequiredForDescriptorSubmit,
            DescriptorAbiStatus.ValidTransportDescriptor,
            "ObservedOnly",
            CompilerTypedSlotFactStaging.MissingCompatibility,
            BridgeIngressStatus.BridgeIngressRejected,
            FallbackPolicyKind.Forbidden,
            "phase09-l7-no-fallback",
            [evidenceRecord],
            ["runtime legality A", "runtime legality B", "runtime commit", "runtime retire", "runtime publication"],
            RuntimeLegalityAStillRequired: true,
            RuntimeLegalityBStillRequired: true,
            RuntimeCommitStillRequired: true,
            RuntimeRetireStillRequired: true,
            RuntimePublicationStillRequired: true,
            "L7-SDC compiler evidence is host/compiler-owned evidence only.");

        EvidenceIsolationValidationResult isolation =
            EvidenceIsolationValidator.Validate(snapshot);
        IReadOnlyDictionary<string, string> serialized =
            CompilerEvidenceSnapshotSerializer.Serialize(snapshot);

        Assert.True(isolation.IsEvidenceIsolated, string.Join(Environment.NewLine, isolation.Diagnostics));
        Assert.Equal("EvidenceOnly", serialized["evidence.authority_semantics"]);
        Assert.Equal("True", serialized["runtime_legality_a.required"]);
        Assert.Equal("True", serialized["runtime_publication.required"]);
        Assert.Equal("Forbidden", serialized["fallback.policy"]);
    }

    [Fact]
    public void L7SdcDescriptorAbiValidityDoesNotGrantExecutionPublicationCommitRetireOrRuntimeLegality()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        CompilerCoreResultHeader header = new CompilerCoreResultHeader(
            CompilerAuthorityClass.DescriptorAbiConstruction,
            CompilerAuthoritySourceKind.CompilerAbiValidator,
            CompilerEvidenceClass.DescriptorAbiEvidence,
            CompilerPublicationClass.DescriptorOnly,
            CompilerExecutionClaim.NoExecutionClaim,
            CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
            CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
            CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired |
            CompilerRuntimeAuthorityDependency.RuntimeCommitRequired |
            CompilerRuntimeAuthorityDependency.RuntimeRetireRequired |
            CompilerRuntimeAuthorityDependency.RuntimePublicationRequired);
        DescriptorEnvelope envelope = new DescriptorEnvelope(
            ExecutionContourKind.L7SdcLane7,
            DescriptorAbiStatus.ValidTransportDescriptor,
            [descriptor],
            SidebandRequirement.RequiredForDescriptorSubmit,
            Array.Empty<string>(),
            header);

        CompilerArtifactValidationResult validation =
            DescriptorEnvelopeValidator.Instance.Validate(envelope);
        BridgeAcceptanceReport bridgeReport =
            CompilerRuntimeBridge.Instance.AcceptDescriptor(envelope);

        Assert.True(validation.IsAuthorityScopedValidation);
        Assert.Equal(CompilerAuthorityClass.DescriptorAbiConstruction, validation.AuthorityClass);
        Assert.Equal(CompilerExecutionClaim.NoExecutionClaim, validation.ExecutionClaim);
        Assert.True(validation.RuntimeLegalityStillRequired);
        Assert.Equal(CompilerPublicationClass.DescriptorOnly, envelope.Header.PublicationClass);
        Assert.NotEqual(CompilerPublicationClass.CarrierBytesOnly, envelope.Header.PublicationClass);
        Assert.NotEqual(CompilerPublicationClass.RuntimeBridgeEnvelopeOnly, envelope.Header.PublicationClass);
        Assert.Equal(BridgeIngressStatus.BridgeIngressAccepted, bridgeReport.Status);
        Assert.True(bridgeReport.RuntimeLegalityAStillRequired);
        Assert.True(bridgeReport.RuntimeLegalityBStillRequired);
        Assert.True(bridgeReport.RuntimeCommitStillRequired);
        Assert.True(bridgeReport.RuntimeRetireStillRequired);
        Assert.True(bridgeReport.RuntimePublicationStillRequired);
        Assert.Contains("ABI evidence only", bridgeReport.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
