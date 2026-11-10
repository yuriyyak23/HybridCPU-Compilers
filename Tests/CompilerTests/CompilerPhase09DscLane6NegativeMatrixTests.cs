using System;
using System.Linq;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.Runtime;
using HybridCpuCanonicalCompiler = HybridCPU.Compiler.Core.Runtime.NativeTransportRuntimeAdapter;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Bridge;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.IR.Lowering;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Tests.TestHelpers;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Decoder;
using YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Descriptors;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerPhase09DscLane6NegativeMatrixTests
{
    [Fact]
    public void DscLane6ContourProviderRejectsLoweringWithoutL7StreamOrScalarFallback()
    {
        CompilerSemanticIntent intent = new CompilerSemanticIntent(
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
            "Phase09 DSC lane6 negative gate.");
        CompilerLoweringContext context = new CompilerLoweringContext(
            new CompilerTargetProfile("phase09-dsc-lane6"),
            "CompilerPhase09DscLane6NegativeMatrixTests");

        IContourAnalyzer analyzer =
            DefaultContourLoweringProviderRegistry.Instance.ResolveAnalyzer(ExecutionContourKind.DmaStreamComputeLane6);
        ContourAnalysisReport analysis = analyzer.Analyze(intent, context);
        IContourLoweringProvider provider =
            DefaultContourLoweringProviderRegistry.Instance.ResolveProvider(ExecutionContourKind.DmaStreamComputeLane6);
        CompilerLoweringDecision decision = provider.Lower(intent, analysis, context);

        Assert.True(analysis.ProviderAvailable);
        Assert.Equal(ExecutionContourKind.DmaStreamComputeLane6, analysis.ContourKind);
        Assert.Equal(CompilerCapabilityObservationState.ScopedRuntimeContour, analysis.CapabilityObservation.State);
        Assert.Contains("L7/Stream/scalar fallback is forbidden", analysis.CapabilityObservation.Reason, StringComparison.Ordinal);
        Assert.Equal(CompilerLoweringDecisionKind.Rejected, decision.DecisionKind);
        Assert.Equal(CompilerRejectReason.RuntimeAuthorityOwned, Assert.Single(decision.RejectReasons));
        Assert.Equal(CompilerEmissionClass.NoEmission, decision.EmissionClass);
        Assert.Equal(CompilerProductionLoweringStatus.Rejected, decision.ProductionLoweringStatus);
        Assert.False(decision.FallbackPolicy.AllowsCrossContourFallback);
        Assert.Equal(FallbackPolicyKind.Forbidden, decision.NoFallbackProof.PolicyKind);
    }

    [Fact]
    public void DscLane6DescriptorOnNonDscOpcodeRejectsAtCompilerIrBuild()
    {
        DmaStreamComputeDescriptor descriptor =
            DmaStreamComputeTestDescriptorFactory.CreateDescriptor();
        VLIW_Instruction[] raw = new[]
        {
            new VLIW_Instruction
            {
                OpCode = (uint)InstructionsEnum.ADD,
                DataTypeValue = DataTypeEnum.INT32,
                Word1 = VLIW_Instruction.PackArchRegs(1, 2, 3)
            }
        };
        VliwBundleAnnotations annotations = new VliwBundleAnnotations(new[]
        {
            InstructionSlotMetadata.Default with
            {
                DmaStreamComputeDescriptor = descriptor
            }
        });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => HybridCpuCanonicalCompiler.CompileProgram(
                virtualThreadId: 0,
                instructions: raw,
                bundleAnnotations: annotations));

        Assert.Contains("DmaStreamCompute descriptor sideband", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lane6", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compiler contour", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DscLane6AndL7Lane7DescriptorCollisionRejectsAtCompilerIrBuild()
    {
        DmaStreamComputeDescriptor dscDescriptor =
            DmaStreamComputeTestDescriptorFactory.CreateDescriptor();
        AcceleratorCommandDescriptor l7Descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        VLIW_Instruction[] raw = new[]
        {
            CreateNativeDscInstruction()
        };
        VliwBundleAnnotations annotations = new VliwBundleAnnotations(new[]
        {
            InstructionSlotMetadata.Default with
            {
                DmaStreamComputeDescriptor = dscDescriptor,
                AcceleratorCommandDescriptor = l7Descriptor
            }
        });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => HybridCpuCanonicalCompiler.CompileProgram(
                virtualThreadId: 0,
                instructions: raw,
                bundleAnnotations: annotations));

        Assert.Contains("lane6", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lane7", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same instruction", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DscLane6AndL7Lane7DescriptorCollisionRejectsAtDecoder()
    {
        DmaStreamComputeDescriptor dscDescriptor =
            DmaStreamComputeTestDescriptorFactory.CreateDescriptor();
        AcceleratorCommandDescriptor l7Descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        VLIW_Instruction[] rawSlots = new VLIW_Instruction[BundleMetadata.BundleSlotCount];
        rawSlots[6] = CreateNativeDscInstruction();
        InstructionSlotMetadata[] metadata = CreateDefaultInstructionSlotMetadata();
        metadata[6] = InstructionSlotMetadata.Default with
        {
            DmaStreamComputeDescriptor = dscDescriptor,
            AcceleratorCommandDescriptor = l7Descriptor
        };

        InvalidOpcodeException ex = Assert.Throws<InvalidOpcodeException>(
            () => new VliwDecoderV4().DecodeInstructionBundle(
                rawSlots,
                new VliwBundleAnnotations(metadata),
                bundleAddress: 0xD600,
                bundleSerial: 6));

        Assert.Contains("DmaStreamCompute", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("slot 6", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("illegal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DscDescriptorAbiValidityDoesNotPublishMemoryOrRegisterAuthority()
    {
        DmaStreamComputeDescriptor descriptor =
            DmaStreamComputeTestDescriptorFactory.CreateDescriptor();
        CompilerCoreResultHeader header = new CompilerCoreResultHeader(
            CompilerAuthorityClass.DescriptorAbiConstruction,
            CompilerAuthoritySourceKind.CompilerAbiValidator,
            CompilerEvidenceClass.DescriptorAbiEvidence,
            CompilerPublicationClass.DescriptorOnly,
            CompilerExecutionClaim.NoExecutionClaim,
            CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
            CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
            CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired |
            CompilerRuntimeAuthorityDependency.RuntimePublicationRequired);
        DescriptorEnvelope envelope = new DescriptorEnvelope(
            ExecutionContourKind.DmaStreamComputeLane6,
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

    private static VLIW_Instruction CreateNativeDscInstruction() =>
        new()
        {
            OpCode = (uint)InstructionsEnum.DmaStreamCompute,
            DataType = 0,
            PredicateMask = 0,
            Immediate = 0,
            DestSrc1Pointer = 0,
            Src2Pointer = 0,
            StreamLength = 0,
            Stride = 0,
            VirtualThreadId = 0
        };

    private static InstructionSlotMetadata[] CreateDefaultInstructionSlotMetadata() =>
        Enumerable.Repeat(InstructionSlotMetadata.Default, BundleMetadata.BundleSlotCount).ToArray();
}
