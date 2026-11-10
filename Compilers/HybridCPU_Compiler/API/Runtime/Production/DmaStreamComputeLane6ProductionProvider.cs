using System;
using System.Collections.Generic;
using System.Linq;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU.Compiler.Core.IR.Lowering.Production;

/// <summary>
/// Explicit descriptor-backed provider for the lane-6 DmaStreamCompute contour.
/// Descriptor and owner-guard values are compiler evidence only; queueing,
/// ordering, fault handling, completion, publication, commit, and retire stay
/// runtime-owned.
/// </summary>
public sealed class DmaStreamComputeLane6ProductionProvider : IContourProductionLoweringProvider
{
    private const string ProviderSurface = "DmaStreamComputeLane6ProductionProvider";

    private static readonly CompilerRuntimeAuthorityDependency RuntimeAuthorityPending =
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired |
        CompilerRuntimeAuthorityDependency.RuntimeCommitRequired |
        CompilerRuntimeAuthorityDependency.RuntimeRetireRequired |
        CompilerRuntimeAuthorityDependency.RuntimePublicationRequired;

    public static DmaStreamComputeLane6ProductionProvider Instance { get; } = new();

    public ExecutionContourKind ContourKind => ExecutionContourKind.DmaStreamComputeLane6;

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
                "DmaStreamCompute lane6 production requires an explicit descriptor-backed carrier package for shadow comparison.");
        }

        if (!TryValidateCandidate(
                candidate,
                intent,
                out string failureReason,
                out DmaStreamComputeDescriptor? descriptor,
                out IReadOnlyList<string> telemetryFacts))
        {
            return CompilerProductionLoweringResult.Rejected(gate, failureReason);
        }

        NoFallbackProof noFallbackProof = NoFallbackProof.Forbidden(
            $"dma-stream-compute-lane6-production:{candidate.Identity.PackageId:N}",
            "DmaStreamCompute production accepts only the descriptor-backed lane6 contour; L7, StreamEngineVector, MatrixTile, scalar, and VMX fallback are forbidden.");

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
            "ise-lane6-decode-parity=verified",
            $"descriptor-facts={string.Join(";", telemetryFacts)}",
            "owner-domain-guard=observed-accepted-evidence-only",
            "future-runtime-gates=queue-token-fence,order-cache-fault,completion-route",
            "runtime-authority=LegalityA,LegalityB,Execution,Publication,Commit,Retire-pending"
        ];

        CompilerEvidenceEnvelope evidence = candidate.Evidence with
        {
            EvidenceArtifacts = candidate.Evidence.EvidenceArtifacts
                .Contains(CompilerArtifactKind.TelemetrySnapshot)
                ? candidate.Evidence.EvidenceArtifacts
                : [.. candidate.Evidence.EvidenceArtifacts, CompilerArtifactKind.TelemetrySnapshot],
            Reason = "Descriptor-backed DmaStreamCompute lane6 evidence is structural and runtime-authority-pending."
        };

        CompilerEmissionPackage productionPackage = candidate with
        {
            Identity = candidate.Identity with
            {
                ContourKind = ContourKind,
                IntentKind = SemanticIntentKind.DmaStreamCompute,
                ProducerSurface = ProviderSurface,
                Reason = "Explicit DmaStreamCompute lane6 descriptor-backed carrier package; queue and completion authority remain runtime-owned."
            },
            Evidence = evidence
        };

        _ = descriptor;
        return CompilerProductionLoweringResult.RuntimePending(
            gate,
            productionPackage,
            noFallbackProof,
            telemetryEvidence,
            "Descriptor-backed DmaStreamCompute lane6 carrier package accepted; runtime queueing, ordering, fault handling, completion, publication, commit, and retire remain runtime-owned.");
    }

    private CompilerProductionLoweringGateRequest BuildGateRequest(
        CompilerSemanticIntent intent,
        ContourAnalysisReport analysis,
        CompilerProductionLoweringContext context)
    {
        bool exactDscIntent = IsExactDscIntent(intent);
        bool readinessComplete =
            context.Readiness.GoldenArtifactCoverage &&
            context.Readiness.IseDecodeParityPresent;

        return new()
        {
            IntentKind = intent.Kind,
            ContourKind = ContourKind,
            ClassifiedContourKind = analysis.ContourKind,
            SourceKind = CompilerProductionLoweringSourceKind.ExplicitProvider,
            IntentClassifierComplete = exactDscIntent,
            PresentArtifacts = PresentArtifacts(context.CandidatePackage),
            DeclaredRuntimeDependencies = RuntimeAuthorityPending,
            NoFallbackProofPresent = exactDscIntent && analysis.ContourKind == ContourKind,
            IseDecodeParityPresent = readinessComplete,
            TelemetryComplete = context.Readiness.TelemetryComplete,
            EvidenceComplete = context.Readiness.EvidenceComplete
        };
    }

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

        if (package.Carrier is not null)
        {
            artifacts.Add(CompilerProductionArtifactEnvelopeKind.Carrier);
        }

        if (package.Sideband is not null)
        {
            artifacts.Add(CompilerProductionArtifactEnvelopeKind.Sideband);
        }

        if (package.Descriptor is not null)
        {
            artifacts.Add(CompilerProductionArtifactEnvelopeKind.Descriptor);
        }

        if (package.TypedSlotFacts is not null)
        {
            artifacts.Add(CompilerProductionArtifactEnvelopeKind.TypedSlotFacts);
        }

        if (package.RuntimeBridgeInput is not null)
        {
            artifacts.Add(CompilerProductionArtifactEnvelopeKind.RuntimeBridge);
        }

        return artifacts;
    }

    private static bool TryValidateCandidate(
        CompilerEmissionPackage package,
        CompilerSemanticIntent intent,
        out string reason,
        out DmaStreamComputeDescriptor? descriptor,
        out IReadOnlyList<string> telemetryFacts)
    {
        descriptor = null;
        reason = string.Empty;
        telemetryFacts = Array.Empty<string>();

        if (!IsExactDscIntent(intent))
        {
            reason = "DmaStreamCompute lane6 provider accepts descriptor-backed DmaStreamCompute intent only; no contour fallback is permitted.";
            return false;
        }

        if (package.Identity.ContourKind != ExecutionContourKind.DmaStreamComputeLane6 ||
            package.Identity.IntentKind != SemanticIntentKind.DmaStreamCompute)
        {
            reason = "DmaStreamCompute candidate identity does not match the exact lane6 contour.";
            return false;
        }

        if (package.Carrier is not { } carrier ||
            package.Sideband is not { Requirement: SidebandRequirement.RequiredForDescriptorSubmit } ||
            package.Descriptor is not
            {
                ContourKind: ExecutionContourKind.DmaStreamComputeLane6,
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
            reason = "DmaStreamCompute candidate lacks the descriptor-backed envelopes or complete runtime dependency map.";
            return false;
        }

        if (!package.SeparationProof.CarrierSeparatedFromSideband ||
            !package.SeparationProof.DescriptorSeparatedFromAuthority ||
            !package.SeparationProof.TypedSlotFactsSeparatedFromLegality ||
            !package.SeparationProof.EvidenceSeparatedFromProductionLowering ||
            !package.SeparationProof.BridgeSeparatedFromExecution)
        {
            reason = "DmaStreamCompute candidate failed artifact separation proof; descriptor evidence cannot grant runtime authority.";
            return false;
        }

        if (descriptorEnvelope.Descriptors[0] is not DmaStreamComputeDescriptor dscDescriptor)
        {
            reason = "DmaStreamCompute descriptor envelope contains a non-DSC descriptor; L7 descriptor routing is forbidden.";
            return false;
        }

        if (package.RuntimeBridgeInput.Descriptor is not
            {
                ContourKind: ExecutionContourKind.DmaStreamComputeLane6,
                Status: DescriptorAbiStatus.ValidTransportDescriptor,
                Descriptors.Count: 1
            } bridgeDescriptorEnvelope ||
            bridgeDescriptorEnvelope.Descriptors[0] is not DmaStreamComputeDescriptor bridgeDescriptor ||
            !Equals(bridgeDescriptor, dscDescriptor))
        {
            reason = "DmaStreamCompute descriptor envelope is not consistent across compiler and runtime-bridge evidence.";
            return false;
        }

        if (!TryValidateDescriptor(dscDescriptor, out string descriptorReason, out telemetryFacts))
        {
            reason = descriptorReason;
            return false;
        }

        byte[] image = carrier.Image.SerializedImage;
        if (image.Length == 0 || image.Length % HybridCpuBundleSerializer.BundleSizeBytes != 0)
        {
            reason = "DmaStreamCompute carrier image is empty or not bundle aligned.";
            return false;
        }

        byte[] serialized = new HybridCpuBundleSerializer().SerializeProgram(carrier.Image.Bundles);
        if (!serialized.AsSpan().SequenceEqual(image))
        {
            reason = "DmaStreamCompute carrier bytes do not match compiler bundle serialization.";
            return false;
        }

        int occupiedCount = 0;
        int bundleCount = image.Length / HybridCpuBundleSerializer.BundleSizeBytes;
        if (package.TypedSlotFacts.Facts.Count < bundleCount)
        {
            reason = "DmaStreamCompute candidate does not provide structural facts for every carrier bundle.";
            return false;
        }

        for (int bundleIndex = 0; bundleIndex < bundleCount; bundleIndex++)
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            if (!bundle.TryReadBytes(
                    image,
                    bundleIndex * HybridCpuBundleSerializer.BundleSizeBytes))
            {
                reason = $"DmaStreamCompute carrier bundle {bundleIndex} failed ISE-compatible decoding.";
                return false;
            }

            IrTypedSlotBundleFacts facts = package.TypedSlotFacts.Facts[bundleIndex];
            for (int slotIndex = 0; slotIndex < BundleMetadata.BundleSlotCount; slotIndex++)
            {
                HybridCpuInstructionWord instruction = bundle.GetInstruction(slotIndex);
                if (instruction.OpCode == 0)
                {
                    continue;
                }

                if ((HybridCpuOpcode)instruction.OpCode != HybridCpuOpcode.DmaStreamCompute)
                {
                    reason = $"Opcode {(HybridCpuOpcode)instruction.OpCode} is outside the bounded DmaStreamCompute lane6 subset.";
                    return false;
                }

                if (facts.GetSlotClass(slotIndex) != IrSlotClass.DmaStreamClass ||
                    !facts.IsSlotPinned(slotIndex) ||
                    slotIndex != 6)
                {
                    reason = $"DmaStreamCompute carrier lacks the hard-pinned lane6 structural fact at {bundleIndex}:{slotIndex}.";
                    return false;
                }

                if (instruction.DestSrc1Pointer != 0 ||
                    instruction.Src2Pointer != 0 ||
                    instruction.StreamLength != 0 ||
                    instruction.Stride != 0 ||
                    instruction.Indexed ||
                    instruction.Is2D ||
                    instruction.Reduction)
                {
                    reason = "DmaStreamCompute carrier contains non-descriptor operand fields; descriptor sideband must remain separate from carrier authority.";
                    return false;
                }

                occupiedCount++;
            }
        }

        if (occupiedCount != 1)
        {
            reason = "DmaStreamCompute production requires exactly one descriptor-backed lane6 carrier opcode.";
            return false;
        }

        descriptor = dscDescriptor;
        return true;
    }

    private static bool TryValidateDescriptor(
        DmaStreamComputeDescriptor descriptor,
        out string reason,
        out IReadOnlyList<string> telemetryFacts)
    {
        List<string> facts = new List<string>();
        reason = string.Empty;

        if (descriptor.AbiVersion != DmaStreamComputeDescriptorParser.CurrentAbiVersion ||
            descriptor.HeaderSize != DmaStreamComputeDescriptorParser.CurrentHeaderSize ||
            descriptor.TotalSize < DmaStreamComputeDescriptorParser.CurrentHeaderSize)
        {
            reason = "DmaStreamCompute descriptor ABI/header is not accepted for the lane6 provider.";
            telemetryFacts = Array.Empty<string>();
            return false;
        }

        if (descriptor.DescriptorReference.DescriptorSize != 0 &&
            descriptor.DescriptorReference.DescriptorSize < descriptor.TotalSize)
        {
            reason = "DmaStreamCompute descriptor reference does not cover the accepted payload.";
            telemetryFacts = Array.Empty<string>();
            return false;
        }

        if (descriptor.DescriptorReference.DescriptorIdentityHash != 0 &&
            descriptor.DescriptorReference.DescriptorIdentityHash != descriptor.DescriptorIdentityHash)
        {
            reason = "DmaStreamCompute descriptor identity hash does not match its reference.";
            telemetryFacts = Array.Empty<string>();
            return false;
        }

        if (descriptor.OwnerBinding.DeviceId != DmaStreamComputeDescriptor.CanonicalLane6DeviceId)
        {
            reason = "DmaStreamCompute descriptor owner binding does not identify canonical lane6 device 6.";
            telemetryFacts = Array.Empty<string>();
            return false;
        }

        if (descriptor.OwnerGuardDecision.DescriptorOwnerBinding is null ||
            !descriptor.OwnerGuardDecision.DescriptorOwnerBinding.Equals(descriptor.OwnerBinding) ||
            !descriptor.OwnerGuardDecision.IsAllowed)
        {
            reason = "DmaStreamCompute descriptor lacks an accepted matching owner/domain guard observation; guard data remains evidence only.";
            telemetryFacts = Array.Empty<string>();
            return false;
        }

        DmaStreamComputeOwnerGuardContext guardContext = descriptor.OwnerGuardDecision.RuntimeOwnerContext;
        if (guardContext.OwnerVirtualThreadId != descriptor.OwnerBinding.OwnerVirtualThreadId ||
            guardContext.OwnerContextId != descriptor.OwnerBinding.OwnerContextId ||
            guardContext.OwnerCoreId != descriptor.OwnerBinding.OwnerCoreId ||
            guardContext.OwnerPodId != descriptor.OwnerBinding.OwnerPodId ||
            guardContext.OwnerDomainTag != descriptor.OwnerBinding.OwnerDomainTag ||
            guardContext.DeviceId != descriptor.OwnerBinding.DeviceId)
        {
            reason = "DmaStreamCompute owner/domain guard context does not match descriptor owner binding.";
            telemetryFacts = Array.Empty<string>();
            return false;
        }

        if (!IsSupportedOperation(descriptor.Operation) ||
            !IsSupportedElementType(descriptor.ElementType) ||
            !IsSupportedShape(descriptor.Operation, descriptor.Shape) ||
            descriptor.RangeEncoding != DmaStreamComputeRangeEncoding.InlineContiguous ||
            descriptor.PartialCompletionPolicy != DmaStreamComputePartialCompletionPolicy.AllOrNone)
        {
            reason = "DmaStreamCompute descriptor contains unsupported operation, element type, shape, range, or completion semantics.";
            telemetryFacts = Array.Empty<string>();
            return false;
        }

        if (!HasNonEmptyRanges(descriptor.ReadMemoryRanges) ||
            !HasNonEmptyRanges(descriptor.NormalizedReadMemoryRanges) ||
            !HasNonEmptyRanges(descriptor.WriteMemoryRanges) ||
            !HasNonEmptyRanges(descriptor.NormalizedWriteMemoryRanges) ||
            descriptor.NormalizedFootprintHash == 0)
        {
            reason = "DmaStreamCompute descriptor lacks non-empty normalized read/write footprint evidence.";
            telemetryFacts = Array.Empty<string>();
            return false;
        }

        facts.Add($"abi={descriptor.AbiVersion}/{descriptor.HeaderSize}/{descriptor.TotalSize}");
        facts.Add($"operation={descriptor.Operation}");
        facts.Add($"element={descriptor.ElementType}");
        facts.Add($"shape={descriptor.Shape}");
        facts.Add($"range={descriptor.RangeEncoding}");
        facts.Add($"completion={descriptor.PartialCompletionPolicy}");
        facts.Add($"footprint-hash=0x{descriptor.NormalizedFootprintHash:X}");
        facts.Add($"read-ranges={descriptor.NormalizedReadMemoryRanges.Count}");
        facts.Add($"write-ranges={descriptor.NormalizedWriteMemoryRanges.Count}");
        telemetryFacts = facts;
        return true;
    }

    private static bool IsExactDscIntent(CompilerSemanticIntent intent) =>
        intent.Kind == SemanticIntentKind.DmaStreamCompute &&
        intent.RequiresDescriptor &&
        intent.RequiresSideband &&
        intent.RequiresToken &&
        intent.RequiresRuntimeLegality &&
        !intent.IsCompatibilityProjection &&
        !intent.IsPolicyAdmissionOnly &&
        !intent.IsHelperAbiOnly &&
        !intent.IsParserOnly &&
        string.Equals(intent.OpcodeFamily, "DmaStreamCompute", StringComparison.Ordinal);

    private static bool IsSupportedOperation(DmaStreamComputeOperationKind operation) =>
        operation is
            DmaStreamComputeOperationKind.Copy or
            DmaStreamComputeOperationKind.Add or
            DmaStreamComputeOperationKind.Mul or
            DmaStreamComputeOperationKind.Fma or
            DmaStreamComputeOperationKind.Reduce;

    private static bool IsSupportedElementType(DmaStreamComputeElementType elementType) =>
        elementType is
            DmaStreamComputeElementType.UInt8 or
            DmaStreamComputeElementType.UInt16 or
            DmaStreamComputeElementType.UInt32 or
            DmaStreamComputeElementType.UInt64 or
            DmaStreamComputeElementType.Float32 or
            DmaStreamComputeElementType.Float64;

    private static bool IsSupportedShape(
        DmaStreamComputeOperationKind operation,
        DmaStreamComputeShapeKind shape) =>
        shape == DmaStreamComputeShapeKind.Contiguous1D ||
        (operation == DmaStreamComputeOperationKind.Reduce &&
         shape == DmaStreamComputeShapeKind.FixedReduce);

    private static bool HasNonEmptyRanges(
        IReadOnlyList<DmaStreamComputeMemoryRange>? ranges)
    {
        if (ranges is null || ranges.Count == 0)
        {
            return false;
        }

        for (int index = 0; index < ranges.Count; index++)
        {
            DmaStreamComputeMemoryRange range = ranges[index];
            if (range.Length == 0 || range.Address > ulong.MaxValue - range.Length)
            {
                return false;
            }
        }

        return true;
    }
}
