using System;
using System.Collections.Generic;
using System.Linq;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Intent;
using YAKSys_Hybrid_CPU.Core;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU.Compiler.Core.IR.Lowering.Production;

/// <summary>
/// Explicit production-provider boundary for the bounded native LSU subset.
/// The candidate package is supplied for shadow comparison. Address encoding,
/// memory ordering, fault resolution, publication, commit, and retire remain
/// runtime-owned dependencies.
/// </summary>
public sealed class NativeVliwLoadStoreProductionProvider : IContourProductionLoweringProvider
{
    private const string ProviderSurface = "NativeVliwLoadStoreProductionProvider";

    private static readonly CompilerRuntimeAuthorityDependency RuntimeAuthorityPending =
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired |
        CompilerRuntimeAuthorityDependency.RuntimeCommitRequired |
        CompilerRuntimeAuthorityDependency.RuntimeRetireRequired |
        CompilerRuntimeAuthorityDependency.RuntimePublicationRequired;

    public static NativeVliwLoadStoreProductionProvider Instance { get; } = new();

    public ExecutionContourKind ContourKind => ExecutionContourKind.NativeVliwLoadStore;

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
                "Native load/store production lowering requires an explicit candidate carrier package for shadow comparison.");
        }

        if (!TryValidateCandidate(
                candidate,
                intent,
                out string failureReason,
                out IReadOnlyList<string> opcodeFamilies))
        {
            return CompilerProductionLoweringResult.Rejected(gate, failureReason);
        }

        NoFallbackProof noFallbackProof = NoFallbackProof.Forbidden(
            $"native-vliw-load-store-production:{candidate.Identity.PackageId:N}",
            "Native load/store production accepts only the exact LSU contour; vector, stream, DSC, L7, and scalar fallback are forbidden.");

        IReadOnlyList<string> telemetryEvidence =
        [
            $"contour={ContourKind}",
            $"intent={intent.Kind}",
            $"opcode-family={string.Join(",", opcodeFamilies)}",
            $"producer-surface={ProviderSurface}",
            $"gate-id={CompilerProductionLoweringGateIds.ExplicitProvider}",
            $"memory-fault-gate={CompilerProductionLoweringGateIds.MemoryFaultRuntimeDependency}",
            $"artifact-id={candidate.Identity.PackageId:N}",
            "golden-artifact=verified",
            "ise-decode-parity=verified",
            "address-width=64-bit-carrier-field-checked",
            "memory-order-and-fault=runtime-owned",
            "runtime-authority=LegalityA,LegalityB,Execution,Publication,Commit,Retire-pending"
        ];

        CompilerEvidenceEnvelope evidence = candidate.Evidence with
        {
            EvidenceArtifacts = candidate.Evidence.EvidenceArtifacts
                .Contains(CompilerArtifactKind.TelemetrySnapshot)
                ? candidate.Evidence.EvidenceArtifacts
                : [.. candidate.Evidence.EvidenceArtifacts, CompilerArtifactKind.TelemetrySnapshot],
            Reason = "Native load/store production-provider evidence is structural and memory-authority-pending."
        };

        CompilerEmissionPackage productionPackage = candidate with
        {
            Identity = candidate.Identity with
            {
                ContourKind = ContourKind,
                IntentKind = SemanticIntentKind.LoadStore,
                ProducerSurface = ProviderSurface,
                Reason = "Explicit NativeVliwLoadStore production carrier package; memory authority remains runtime-owned."
            },
            Evidence = evidence
        };

        return CompilerProductionLoweringResult.RuntimePending(
            gate,
            productionPackage,
            noFallbackProof,
            telemetryEvidence,
            "Native load/store production carrier package accepted; runtime memory legality, ordering, fault resolution, publication, commit, and retire remain runtime-owned.");
    }

    private CompilerProductionLoweringGateRequest BuildGateRequest(
        CompilerSemanticIntent intent,
        ContourAnalysisReport analysis,
        CompilerProductionLoweringContext context)
    {
        bool exactLoadStoreIntent = IsExactLoadStoreIntent(intent);
        bool readinessComplete =
            context.Readiness.GoldenArtifactCoverage &&
            context.Readiness.IseDecodeParityPresent;

        return new()
        {
            IntentKind = intent.Kind,
            ContourKind = ContourKind,
            ClassifiedContourKind = analysis.ContourKind,
            SourceKind = CompilerProductionLoweringSourceKind.ExplicitProvider,
            IntentClassifierComplete = exactLoadStoreIntent,
            PresentArtifacts = PresentArtifacts(context.CandidatePackage),
            DeclaredRuntimeDependencies = RuntimeAuthorityPending,
            NoFallbackProofPresent = exactLoadStoreIntent && analysis.ContourKind == ContourKind,
            IseDecodeParityPresent = readinessComplete,
            TelemetryComplete = context.Readiness.TelemetryComplete,
            EvidenceComplete = context.Readiness.EvidenceComplete,
            MemoryOrderingAndFaultRuntimeRequired =
                context.Readiness.MemoryOrderingAndFaultRuntimeRequired
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
        out IReadOnlyList<string> opcodeFamilies)
    {
        List<string> families = new List<string>();
        reason = string.Empty;

        if (!IsExactLoadStoreIntent(intent))
        {
            reason = "NativeVliwLoadStore production provider accepts LoadStore intent only; no helper or contour fallback is permitted.";
            opcodeFamilies = Array.Empty<string>();
            return false;
        }

        if (package.Identity.ContourKind != ExecutionContourKind.NativeVliwLoadStore ||
            package.Identity.IntentKind != SemanticIntentKind.LoadStore)
        {
            reason = "NativeVliwLoadStore candidate identity does not match the exact LSU contour.";
            opcodeFamilies = Array.Empty<string>();
            return false;
        }

        if (package.Carrier is not { } carrier ||
            package.Sideband is not { Requirement: SidebandRequirement.OptionalCompatibility } ||
            package.Descriptor is not { Status: DescriptorAbiStatus.None, Descriptors.Count: 0 } ||
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
            reason = "Native load/store candidate lacks the separated structural envelopes or complete runtime dependency map.";
            opcodeFamilies = Array.Empty<string>();
            return false;
        }

        if (!package.SeparationProof.CarrierSeparatedFromSideband ||
            !package.SeparationProof.DescriptorSeparatedFromAuthority ||
            !package.SeparationProof.TypedSlotFactsSeparatedFromLegality ||
            !package.SeparationProof.EvidenceSeparatedFromProductionLowering ||
            !package.SeparationProof.BridgeSeparatedFromExecution)
        {
            reason = "Native load/store candidate failed artifact separation proof; memory authority cannot be inferred.";
            opcodeFamilies = Array.Empty<string>();
            return false;
        }

        byte[] image = carrier.Image.SerializedImage;
        if (image.Length == 0 || image.Length % HybridCpuBundleSerializer.BundleSizeBytes != 0)
        {
            reason = "Native load/store candidate carrier image is empty or not bundle aligned.";
            opcodeFamilies = Array.Empty<string>();
            return false;
        }

        byte[] serialized = new HybridCpuBundleSerializer().SerializeProgram(carrier.Image.Bundles);
        if (!serialized.AsSpan().SequenceEqual(image))
        {
            reason = "Native load/store candidate carrier bytes do not match compiler bundle serialization.";
            opcodeFamilies = Array.Empty<string>();
            return false;
        }

        int occupiedCount = 0;
        int bundleCount = image.Length / HybridCpuBundleSerializer.BundleSizeBytes;
        if (package.TypedSlotFacts.Facts.Count < bundleCount)
        {
            reason = "Native load/store candidate does not provide structural facts for every carrier bundle.";
            opcodeFamilies = Array.Empty<string>();
            return false;
        }

        for (int bundleIndex = 0; bundleIndex < bundleCount; bundleIndex++)
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            if (!bundle.TryReadBytes(
                    image,
                    bundleIndex * HybridCpuBundleSerializer.BundleSizeBytes))
            {
                reason = $"Native load/store candidate carrier bundle {bundleIndex} failed ISE-compatible decoding.";
                opcodeFamilies = Array.Empty<string>();
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

                HybridCpuOpcode opcode = (HybridCpuOpcode)instruction.OpCode;
                if (!IsSupportedLoadStoreOpcode(opcode))
                {
                    reason = $"Opcode {opcode} is outside the bounded NativeVliwLoadStore production subset.";
                    opcodeFamilies = Array.Empty<string>();
                    return false;
                }

                if (!string.Equals(
                        intent.OpcodeFamily,
                        opcode.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"Intent opcode family '{intent.OpcodeFamily}' does not exactly match carrier opcode '{opcode}'.";
                    opcodeFamilies = Array.Empty<string>();
                    return false;
                }

                if (facts.GetSlotClass(slotIndex) != IrSlotClass.LsuClass ||
                    facts.IsSlotPinned(slotIndex))
                {
                    reason = $"Load/store opcode {opcode} lacks flexible LSU structural slot facts at {bundleIndex}:{slotIndex}.";
                    opcodeFamilies = Array.Empty<string>();
                    return false;
                }

                if (instruction.Src2Pointer == 0)
                {
                    reason = $"Load/store opcode {opcode} has no explicit 64-bit memory address field.";
                    opcodeFamilies = Array.Empty<string>();
                    return false;
                }

                if (instruction.StreamLength != 0 ||
                    instruction.Stride != 0 ||
                    instruction.RowStride != 0 ||
                    instruction.Indexed ||
                    instruction.Is2D ||
                    instruction.Reduction)
                {
                    reason = $"Load/store opcode {opcode} carries stream/indexed/2D fields outside the native LSU subset.";
                    opcodeFamilies = Array.Empty<string>();
                    return false;
                }

                occupiedCount++;
                if (!families.Contains(opcode.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    families.Add(opcode.ToString());
                }
            }
        }

        if (occupiedCount == 0)
        {
            reason = "Native load/store production requires at least one supported LSU carrier opcode.";
            opcodeFamilies = Array.Empty<string>();
            return false;
        }

        opcodeFamilies = families;
        return true;
    }

    private static bool IsExactLoadStoreIntent(CompilerSemanticIntent intent) =>
        intent.Kind == SemanticIntentKind.LoadStore &&
        intent.RequiresRuntimeLegality &&
        !intent.RequiresDescriptor &&
        !intent.RequiresSideband &&
        !intent.RequiresToken &&
        !intent.IsCompatibilityProjection &&
        !intent.IsPolicyAdmissionOnly &&
        !intent.IsHelperAbiOnly &&
        !intent.IsParserOnly &&
        !string.IsNullOrWhiteSpace(intent.OpcodeFamily);

    private static bool IsSupportedLoadStoreOpcode(HybridCpuOpcode opcode) =>
        opcode is
            HybridCpuOpcode.LB or
            HybridCpuOpcode.LBU or
            HybridCpuOpcode.LH or
            HybridCpuOpcode.LHU or
            HybridCpuOpcode.LW or
            HybridCpuOpcode.LWU or
            HybridCpuOpcode.LD or
            HybridCpuOpcode.SB or
            HybridCpuOpcode.SH or
            HybridCpuOpcode.SW or
            HybridCpuOpcode.SD;
}
