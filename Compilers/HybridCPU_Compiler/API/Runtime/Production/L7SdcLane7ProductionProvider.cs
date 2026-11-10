using System;
using System.Collections.Generic;
using System.Linq;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Auth;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Descriptors;
using YAKSys_Hybrid_CPU.Core.Registers;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU.Compiler.Core.IR.Lowering.Production;

/// <summary>
/// Explicit descriptor-backed provider for the lane-7 L7-SDC contour.
/// Descriptor, owner guard, submit guard, footprint, and token-destination
/// values are structural evidence only; token lifecycle, backend protocol,
/// completion, publication, commit, and retire remain runtime-owned.
/// </summary>
public sealed class L7SdcLane7ProductionProvider : IContourProductionLoweringProvider
{
    private const string ProviderSurface = "L7SdcLane7ProductionProvider";

    private static readonly CompilerRuntimeAuthorityDependency RuntimeAuthorityPending =
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired |
        CompilerRuntimeAuthorityDependency.RuntimeCommitRequired |
        CompilerRuntimeAuthorityDependency.RuntimeRetireRequired |
        CompilerRuntimeAuthorityDependency.RuntimePublicationRequired;

    public static L7SdcLane7ProductionProvider Instance { get; } = new();

    public ExecutionContourKind ContourKind => ExecutionContourKind.L7SdcLane7;

    public CompilerProductionLoweringGateResult EvaluateProductionGates(
        CompilerSemanticIntent intent,
        ContourAnalysisReport analysis,
        CompilerProductionLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(context);

        return CompilerProductionLoweringGateEvaluator.Evaluate(
            context,
            BuildGateRequest(intent, analysis, context));
    }

    public CompilerProductionLoweringResult TryProduce(
        CompilerSemanticIntent intent,
        ContourAnalysisReport analysis,
        CompilerProductionLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(context);

        CompilerProductionLoweringGateResult gate =
            EvaluateProductionGates(intent, analysis, context);
        CompilerProductionLoweringResult? preflight =
            CompilerProductionLoweringProviderContract.PreflightBeforeProduce(
                ContourKind,
                intent,
                analysis,
                context,
                gate);
        if (preflight is not null)
        {
            return preflight;
        }

        if (context.CandidatePackage is not { } candidate)
        {
            return CompilerProductionLoweringResult.FutureGated(
                gate,
                "L7-SDC lane7 production requires an explicit descriptor-backed carrier package for shadow comparison.");
        }

        if (!TryValidateCandidate(
                candidate,
                intent,
                out string failureReason,
                out AcceleratorCommandDescriptor? descriptor,
                out byte tokenDestinationRegister,
                out IReadOnlyList<string> telemetryFacts))
        {
            return CompilerProductionLoweringResult.Rejected(gate, failureReason);
        }

        NoFallbackProof noFallbackProof = NoFallbackProof.Forbidden(
            $"l7-sdc-lane7-production:{candidate.Identity.PackageId:N}",
            "L7-SDC production accepts only descriptor-backed lane7 ACCEL_SUBMIT; DSC, stream/vector, scalar, VMX, system, branch, and descriptorless fallback are forbidden.");

        IReadOnlyList<string> telemetryEvidence =
        [
            $"contour={ContourKind}",
            $"intent={intent.Kind}",
            $"opcode-family={intent.OpcodeFamily}",
            $"producer-surface={ProviderSurface}",
            $"artifact-id={candidate.Identity.PackageId:N}",
            "golden-artifact=verified",
            "descriptor-identity-reference=verified",
            "normalized-footprint=verified",
            "ise-lane7-decode-parity=verified",
            $"descriptor-facts={string.Join(";", telemetryFacts)}",
            $"token-destination-structural-register={tokenDestinationRegister}",
            "owner-domain-guard=observed-accepted-evidence-only",
            "submit-guard=observed-accepted-evidence-only",
            "future-runtime-gates=token-lifecycle,backend-protocol,completion-route",
            "runtime-authority=LegalityA,LegalityB,Execution,Publication,Commit,Retire-pending"
        ];

        CompilerEvidenceEnvelope evidence = candidate.Evidence with
        {
            EvidenceArtifacts = candidate.Evidence.EvidenceArtifacts
                .Contains(CompilerArtifactKind.TelemetrySnapshot)
                ? candidate.Evidence.EvidenceArtifacts
                : [.. candidate.Evidence.EvidenceArtifacts, CompilerArtifactKind.TelemetrySnapshot],
            Reason = "Descriptor-backed L7-SDC lane7 evidence is structural and runtime-authority-pending."
        };

        CompilerEmissionPackage productionPackage = candidate with
        {
            Identity = candidate.Identity with
            {
                ContourKind = ContourKind,
                IntentKind = SemanticIntentKind.ExternalAcceleratorCommand,
                ProducerSurface = ProviderSurface,
                Reason = "Explicit L7-SDC lane7 descriptor-backed carrier package; token and completion authority remain runtime-owned."
            },
            Evidence = evidence
        };

        _ = descriptor;
        return CompilerProductionLoweringResult.RuntimePending(
            gate,
            productionPackage,
            noFallbackProof,
            telemetryEvidence,
            "Descriptor-backed L7-SDC lane7 carrier package accepted; token lifecycle, backend protocol, completion, publication, commit, and retire remain runtime-owned.");
    }

    private CompilerProductionLoweringGateRequest BuildGateRequest(
        CompilerSemanticIntent intent,
        ContourAnalysisReport analysis,
        CompilerProductionLoweringContext context) =>
        new()
        {
            IntentKind = intent.Kind,
            ContourKind = ContourKind,
            ClassifiedContourKind = analysis.ContourKind,
            SourceKind = CompilerProductionLoweringSourceKind.ExplicitProvider,
            IntentClassifierComplete = IsExactL7Intent(intent),
            PresentArtifacts = PresentArtifacts(context.CandidatePackage),
            DeclaredRuntimeDependencies = RuntimeAuthorityPending,
            NoFallbackProofPresent = IsExactL7Intent(intent) && analysis.ContourKind == ContourKind,
            IseDecodeParityPresent = context.Readiness.GoldenArtifactCoverage &&
                                     context.Readiness.IseDecodeParityPresent,
            TelemetryComplete = context.Readiness.TelemetryComplete,
            EvidenceComplete = context.Readiness.EvidenceComplete
        };

    private static IReadOnlySet<CompilerProductionArtifactEnvelopeKind> PresentArtifacts(
        CompilerEmissionPackage? package)
    {
        if (package is null)
        {
            return new HashSet<CompilerProductionArtifactEnvelopeKind>();
        }

        HashSet<CompilerProductionArtifactEnvelopeKind> artifacts = new HashSet<CompilerProductionArtifactEnvelopeKind>
        {
            CompilerProductionArtifactEnvelopeKind.Evidence
        };
        if (package.Carrier is not null) artifacts.Add(CompilerProductionArtifactEnvelopeKind.Carrier);
        if (package.Sideband is not null) artifacts.Add(CompilerProductionArtifactEnvelopeKind.Sideband);
        if (package.Descriptor is not null) artifacts.Add(CompilerProductionArtifactEnvelopeKind.Descriptor);
        if (package.TypedSlotFacts is not null) artifacts.Add(CompilerProductionArtifactEnvelopeKind.TypedSlotFacts);
        if (package.RuntimeBridgeInput is not null) artifacts.Add(CompilerProductionArtifactEnvelopeKind.RuntimeBridge);
        return artifacts;
    }

    private static bool TryValidateCandidate(
        CompilerEmissionPackage package,
        CompilerSemanticIntent intent,
        out string reason,
        out AcceleratorCommandDescriptor? descriptor,
        out byte tokenDestinationRegister,
        out IReadOnlyList<string> telemetryFacts)
    {
        reason = string.Empty;
        descriptor = null;
        tokenDestinationRegister = HybridCpuInstructionWord.NoArchReg;
        telemetryFacts = Array.Empty<string>();

        if (!IsExactL7Intent(intent))
        {
            reason = "L7-SDC lane7 provider accepts descriptor-backed ACCEL_SUBMIT intent only; no contour fallback is permitted.";
            return false;
        }

        if (package.Identity.ContourKind != ExecutionContourKind.L7SdcLane7 ||
            package.Identity.IntentKind != SemanticIntentKind.ExternalAcceleratorCommand)
        {
            reason = "L7-SDC candidate identity does not match the exact lane7 contour.";
            return false;
        }

        if (package.Carrier is not { } carrier ||
            package.Sideband is not { Requirement: SidebandRequirement.RequiredForDescriptorSubmit } ||
            package.Descriptor is not
            {
                ContourKind: ExecutionContourKind.L7SdcLane7,
                Status: DescriptorAbiStatus.ValidTransportDescriptor,
                Descriptors.Count: 1
            } descriptorEnvelope ||
            package.StructuralAgreement is not { RuntimeLegalityStillRequired: true } ||
            package.TypedSlotFacts is not { StructuralEvidenceOnly: true, RuntimeLegalityStillRequired: true } ||
            package.Evidence.Header.ExecutionClaim != CompilerExecutionClaim.NoExecutionClaim ||
            package.RuntimeBridgeInput is not
            {
                RuntimeLegalityAStillRequired: true,
                RuntimeLegalityBStillRequired: true,
                RuntimeCommitStillRequired: true,
                RuntimeRetireStillRequired: true,
                RuntimePublicationStillRequired: true
            })
        {
            reason = "L7-SDC candidate lacks descriptor-backed envelopes or complete runtime dependency map.";
            return false;
        }

        if (!package.SeparationProof.CarrierSeparatedFromSideband ||
            !package.SeparationProof.DescriptorSeparatedFromAuthority ||
            !package.SeparationProof.TypedSlotFactsSeparatedFromLegality ||
            !package.SeparationProof.EvidenceSeparatedFromProductionLowering ||
            !package.SeparationProof.BridgeSeparatedFromExecution)
        {
            reason = "L7-SDC candidate failed artifact separation proof; descriptor evidence cannot grant runtime authority.";
            return false;
        }

        if (descriptorEnvelope.Descriptors[0] is not AcceleratorCommandDescriptor l7Descriptor)
        {
            reason = "L7-SDC descriptor envelope contains a non-L7 descriptor; DSC descriptor routing is forbidden.";
            return false;
        }

        if (package.RuntimeBridgeInput.Descriptor is not
            {
                ContourKind: ExecutionContourKind.L7SdcLane7,
                Status: DescriptorAbiStatus.ValidTransportDescriptor,
                Descriptors.Count: 1
            } bridgeDescriptorEnvelope ||
            bridgeDescriptorEnvelope.Descriptors[0] is not AcceleratorCommandDescriptor bridgeDescriptor ||
            !Equals(bridgeDescriptor, l7Descriptor))
        {
            reason = "L7-SDC descriptor envelope is not consistent across compiler and runtime-bridge evidence.";
            return false;
        }

        if (!TryValidateDescriptor(l7Descriptor, out string descriptorReason, out telemetryFacts))
        {
            reason = descriptorReason;
            return false;
        }

        byte[] image = carrier.Image.SerializedImage;
        if (image.Length == 0 || image.Length % HybridCpuBundleSerializer.BundleSizeBytes != 0)
        {
            reason = "L7-SDC carrier image is empty or not bundle aligned.";
            return false;
        }

        byte[] serialized = new HybridCpuBundleSerializer().SerializeProgram(carrier.Image.Bundles);
        if (!serialized.AsSpan().SequenceEqual(image))
        {
            reason = "L7-SDC carrier bytes do not match compiler bundle serialization.";
            return false;
        }

        int occupiedCount = 0;
        int bundleCount = image.Length / HybridCpuBundleSerializer.BundleSizeBytes;
        if (package.TypedSlotFacts.Facts.Count < bundleCount)
        {
            reason = "L7-SDC candidate does not provide structural facts for every carrier bundle.";
            return false;
        }

        for (int bundleIndex = 0; bundleIndex < bundleCount; bundleIndex++)
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            if (!bundle.TryReadBytes(image, bundleIndex * HybridCpuBundleSerializer.BundleSizeBytes))
            {
                reason = $"L7-SDC carrier bundle {bundleIndex} failed ISE-compatible decoding.";
                return false;
            }

            IrTypedSlotBundleFacts facts = package.TypedSlotFacts.Facts[bundleIndex];
            for (int slotIndex = 0; slotIndex < BundleMetadata.BundleSlotCount; slotIndex++)
            {
                HybridCpuInstructionWord instruction = bundle.GetInstruction(slotIndex);
                if (instruction.OpCode == 0) continue;
                if ((HybridCpuOpcode)instruction.OpCode != HybridCpuOpcode.ACCEL_SUBMIT || slotIndex != 7)
                {
                    reason = "L7-SDC carrier contains an opcode outside the bounded lane7 ACCEL_SUBMIT subset.";
                    return false;
                }

                if (facts.GetSlotClass(slotIndex) != IrSlotClass.SystemSingleton ||
                    !facts.IsSlotPinned(slotIndex))
                {
                    reason = $"L7-SDC carrier lacks the hard-pinned lane7 structural fact at {bundleIndex}:{slotIndex}.";
                    return false;
                }

                if (!HybridCpuInstructionWord.TryUnpackArchRegs(
                        instruction.Word1,
                        out byte destination,
                        out byte source1,
                        out byte source2) ||
                    destination > ArchRegId.MaxValue ||
                    source1 != HybridCpuInstructionWord.NoArchReg ||
                    source2 != HybridCpuInstructionWord.NoArchReg ||
                    instruction.Src2Pointer != 0 ||
                    instruction.StreamLength != 0 ||
                    instruction.Stride != 0 ||
                    instruction.RowStride != 0 ||
                    instruction.Indexed ||
                    instruction.Is2D ||
                    instruction.Reduction ||
                    instruction.Reserved != 0)
                {
                    reason = "L7-SDC carrier token destination is not a bounded structural register fact or contains non-descriptor operands.";
                    return false;
                }

                tokenDestinationRegister = destination;
                occupiedCount++;
            }
        }

        if (occupiedCount != 1)
        {
            reason = "L7-SDC production requires exactly one descriptor-backed lane7 ACCEL_SUBMIT carrier opcode.";
            return false;
        }

        descriptor = l7Descriptor;
        return true;
    }

    private static bool TryValidateDescriptor(
        AcceleratorCommandDescriptor descriptor,
        out string reason,
        out IReadOnlyList<string> telemetryFacts)
    {
        reason = string.Empty;
        telemetryFacts = Array.Empty<string>();

        if (descriptor.AbiVersion != AcceleratorDescriptorParser.CurrentAbiVersion ||
            descriptor.HeaderSize != AcceleratorDescriptorParser.CurrentHeaderSize ||
            descriptor.DescriptorSize < AcceleratorDescriptorParser.CurrentHeaderSize ||
            descriptor.Header.AbiVersion != descriptor.AbiVersion ||
            descriptor.Header.HeaderSize != descriptor.HeaderSize ||
            descriptor.Header.DescriptorSize != descriptor.DescriptorSize)
        {
            reason = "L7-SDC descriptor ABI/header is not accepted for the lane7 provider.";
            return false;
        }

        if (descriptor.DescriptorReference.DescriptorSize != 0 &&
            descriptor.DescriptorReference.DescriptorSize < descriptor.DescriptorSize)
        {
            reason = "L7-SDC descriptor reference does not cover the accepted payload.";
            return false;
        }

        if (descriptor.DescriptorReference.DescriptorIdentityHash != 0 &&
            descriptor.DescriptorReference.DescriptorIdentityHash != descriptor.Identity.DescriptorIdentityHash)
        {
            reason = "L7-SDC descriptor identity hash does not match its reference.";
            return false;
        }

        if (descriptor.Identity.DescriptorIdentityHash == 0 ||
            descriptor.Identity.DescriptorIdentityHash != descriptor.Header.Identity.DescriptorIdentityHash ||
            descriptor.Identity.NormalizedFootprintHash == 0 ||
            descriptor.Identity.NormalizedFootprintHash != descriptor.NormalizedFootprint.Hash)
        {
            reason = "L7-SDC descriptor identity or normalized footprint hash is missing or inconsistent.";
            return false;
        }

        if (descriptor.AcceleratorClass != AcceleratorClassId.Matrix ||
            descriptor.AcceleratorId != AcceleratorDeviceId.ReferenceMatMul ||
            descriptor.Operation != AcceleratorOperationKind.MatMul ||
            descriptor.Datatype != AcceleratorDatatype.Float32 ||
            descriptor.Shape != AcceleratorShapeKind.Matrix2D ||
            descriptor.ShapeRank != 2 ||
            descriptor.ElementCount == 0 ||
            descriptor.PartialCompletionPolicy != AcceleratorPartialCompletionPolicy.AllOrNone)
        {
            reason = "L7-SDC descriptor contains unsupported class, device, operation, datatype, shape, or completion semantics.";
            return false;
        }

        if (descriptor.Header.AcceleratorClass != descriptor.AcceleratorClass ||
            descriptor.Header.AcceleratorId != descriptor.AcceleratorId ||
            descriptor.Header.Operation != descriptor.Operation ||
            descriptor.Header.Datatype != descriptor.Datatype ||
            descriptor.Header.Shape != descriptor.Shape ||
            descriptor.Header.ShapeRank != descriptor.ShapeRank ||
            descriptor.Header.PartialCompletionPolicy != descriptor.PartialCompletionPolicy ||
            !descriptor.Header.OwnerBinding.Equals(descriptor.OwnerBinding))
        {
            reason = "L7-SDC descriptor header facts do not match the accepted descriptor envelope.";
            return false;
        }

        if (!AcceleratorOwnerDomainGuard.Default.IsDescriptorGuardBacked(
                descriptor,
                out string ownerGuardReason) ||
            descriptor.OwnerGuardDecision.Surface != AcceleratorGuardSurface.DescriptorAcceptance)
        {
            reason = $"L7-SDC descriptor lacks an accepted owner/domain guard observation; {ownerGuardReason}";
            return false;
        }

        AcceleratorGuardDecision submitGuard =
            AcceleratorOwnerDomainGuard.Default.EnsureBeforeSubmit(
                descriptor,
                descriptor.OwnerGuardDecision.Evidence);
        if (!submitGuard.IsAllowed ||
            submitGuard.Surface != AcceleratorGuardSurface.SubmitAdmission ||
            submitGuard.DescriptorOwnerBinding is null ||
            !submitGuard.DescriptorOwnerBinding.Equals(descriptor.OwnerBinding) ||
            submitGuard.Evidence?.Source != AcceleratorGuardEvidenceSource.GuardPlane)
        {
            reason = "L7-SDC descriptor lacks an accepted submit-guard observation; submit authority remains runtime-owned.";
            return false;
        }

        IReadOnlyList<AcceleratorMemoryRange> normalizedSource =
            AcceleratorDescriptorParser.NormalizeMemoryRanges(descriptor.SourceRanges);
        IReadOnlyList<AcceleratorMemoryRange> normalizedDestination =
            AcceleratorDescriptorParser.NormalizeMemoryRanges(descriptor.DestinationRanges);
        IReadOnlyList<AcceleratorMemoryRange> normalizedScratch =
            AcceleratorDescriptorParser.NormalizeMemoryRanges(descriptor.ScratchRanges);
        ulong computedFootprint = AcceleratorDescriptorParser.ComputeNormalizedFootprintHash(
            descriptor.AcceleratorClass,
            descriptor.AcceleratorId,
            descriptor.Operation,
            descriptor.Datatype,
            descriptor.Shape,
            descriptor.ShapeRank,
            descriptor.ElementCount,
            descriptor.PartialCompletionPolicy,
            descriptor.Alignment,
            normalizedSource,
            normalizedDestination,
            normalizedScratch);

        if (!HasNonEmptyRanges(descriptor.SourceRanges) ||
            !HasNonEmptyRanges(descriptor.DestinationRanges) ||
            !HasNonEmptyRanges(descriptor.NormalizedFootprint.SourceRanges) ||
            !HasNonEmptyRanges(descriptor.NormalizedFootprint.DestinationRanges) ||
            !normalizedSource.SequenceEqual(descriptor.NormalizedFootprint.SourceRanges) ||
            !normalizedDestination.SequenceEqual(descriptor.NormalizedFootprint.DestinationRanges) ||
            !normalizedScratch.SequenceEqual(descriptor.NormalizedFootprint.ScratchRanges) ||
            computedFootprint != descriptor.NormalizedFootprint.Hash ||
            (descriptor.ScratchRequirement.RequiredBytes != 0 && !HasNonEmptyRanges(descriptor.ScratchRanges)))
        {
            reason = "L7-SDC descriptor lacks non-empty normalized source/destination footprint evidence.";
            return false;
        }

        telemetryFacts =
        [
            $"abi={descriptor.AbiVersion}/{descriptor.HeaderSize}/{descriptor.DescriptorSize}",
            $"class={descriptor.AcceleratorClass}",
            $"device={descriptor.AcceleratorId}",
            $"operation={descriptor.Operation}",
            $"datatype={descriptor.Datatype}",
            $"shape={descriptor.Shape}/rank{descriptor.ShapeRank}",
            $"completion={descriptor.PartialCompletionPolicy}",
            $"footprint-hash=0x{descriptor.NormalizedFootprint.Hash:X}",
            $"source-ranges={descriptor.NormalizedFootprint.SourceRanges.Count}",
            $"destination-ranges={descriptor.NormalizedFootprint.DestinationRanges.Count}"
        ];
        return true;
    }

    private static bool IsExactL7Intent(CompilerSemanticIntent intent) =>
        intent.Kind == SemanticIntentKind.ExternalAcceleratorCommand &&
        intent.RequiresDescriptor &&
        intent.RequiresSideband &&
        intent.RequiresToken &&
        intent.RequiresRuntimeLegality &&
        !intent.IsCompatibilityProjection &&
        !intent.IsPolicyAdmissionOnly &&
        !intent.IsHelperAbiOnly &&
        !intent.IsParserOnly &&
        string.Equals(intent.OpcodeFamily, "ACCEL_SUBMIT", StringComparison.Ordinal);

    private static bool HasNonEmptyRanges(IReadOnlyList<AcceleratorMemoryRange>? ranges)
    {
        if (ranges is null || ranges.Count == 0) return false;
        for (int index = 0; index < ranges.Count; index++)
        {
            AcceleratorMemoryRange range = ranges[index];
            if (range.Length == 0 || range.Address > ulong.MaxValue - range.Length) return false;
        }
        return true;
    }
}
