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
/// Explicit production-provider boundary for the bounded native scalar VLIW
/// subset. The candidate package is supplied for shadow comparison; this
/// provider never executes it and never converts its result into runtime
/// legality, publication, commit, or retire authority.
/// </summary>
public sealed class NativeVliwScalarProductionProvider : IContourProductionLoweringProvider
{
    private const string ProviderSurface = "NativeVliwScalarProductionProvider";

    private static readonly CompilerRuntimeAuthorityDependency RuntimeAuthorityPending =
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired |
        CompilerRuntimeAuthorityDependency.RuntimeCommitRequired |
        CompilerRuntimeAuthorityDependency.RuntimeRetireRequired |
        CompilerRuntimeAuthorityDependency.RuntimePublicationRequired;

    public static NativeVliwScalarProductionProvider Instance { get; } = new();

    public ExecutionContourKind ContourKind => ExecutionContourKind.NativeVliwScalar;

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
                "Native scalar production lowering requires an explicit candidate carrier package for shadow comparison.");
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
            $"native-vliw-scalar-production:{candidate.Identity.PackageId:N}",
            "Native scalar production accepts only the exact scalar contour; cross-contour fallback is forbidden.");

        IReadOnlyList<string> telemetryEvidence =
        [
            $"contour={ContourKind}",
            $"intent={intent.Kind}",
            $"opcode-family={string.Join(",", opcodeFamilies)}",
            $"producer-surface={ProviderSurface}",
            $"gate-id={CompilerProductionLoweringGateIds.ExplicitProvider}",
            $"artifact-id={candidate.Identity.PackageId:N}",
            "golden-artifact=verified",
            "ise-decode-parity=verified",
            "runtime-authority=LegalityA,LegalityB,Execution,Publication,Commit,Retire-pending"
        ];

        CompilerEvidenceEnvelope evidence = candidate.Evidence with
        {
            EvidenceArtifacts = candidate.Evidence.EvidenceArtifacts
                .Contains(CompilerArtifactKind.TelemetrySnapshot)
                ? candidate.Evidence.EvidenceArtifacts
                : [.. candidate.Evidence.EvidenceArtifacts, CompilerArtifactKind.TelemetrySnapshot],
            Reason = "Native scalar production-provider evidence is structural and runtime-authority-pending."
        };

        CompilerEmissionPackage productionPackage = candidate with
        {
            Identity = candidate.Identity with
            {
                ContourKind = ContourKind,
                IntentKind = SemanticIntentKind.ScalarAlu,
                ProducerSurface = ProviderSurface,
                Reason = "Explicit NativeVliwScalar production carrier package; runtime authority remains pending."
            },
            Evidence = evidence
        };

        return CompilerProductionLoweringResult.RuntimePending(
            gate,
            productionPackage,
            noFallbackProof,
            telemetryEvidence,
            "Native scalar production carrier package accepted; runtime Legality A/B, execution, publication, commit, and retire remain runtime-owned.");
    }

    private CompilerProductionLoweringGateRequest BuildGateRequest(
        CompilerSemanticIntent intent,
        ContourAnalysisReport analysis,
        CompilerProductionLoweringContext context)
    {
        bool exactScalarIntent = IsExactScalarIntent(intent);
        bool readinessComplete =
            context.Readiness.GoldenArtifactCoverage &&
            context.Readiness.IseDecodeParityPresent;

        return new()
        {
            IntentKind = intent.Kind,
            ContourKind = ContourKind,
            ClassifiedContourKind = analysis.ContourKind,
            SourceKind = CompilerProductionLoweringSourceKind.ExplicitProvider,
            IntentClassifierComplete = exactScalarIntent,
            PresentArtifacts = PresentArtifacts(context.CandidatePackage),
            DeclaredRuntimeDependencies = RuntimeAuthorityPending,
            NoFallbackProofPresent = exactScalarIntent && analysis.ContourKind == ContourKind,
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

        if (!IsExactScalarIntent(intent))
        {
            reason = "NativeVliwScalar production provider accepts ScalarAlu intent only; no scalar fallback is permitted.";
            opcodeFamilies = Array.Empty<string>();
            return false;
        }

        if (package.Identity.ContourKind != ExecutionContourKind.NativeVliwScalar ||
            package.Identity.IntentKind != SemanticIntentKind.ScalarAlu)
        {
            reason = "NativeVliwScalar candidate identity does not match the exact scalar contour.";
            opcodeFamilies = Array.Empty<string>();
            return false;
        }

        if (package.Carrier is not { } carrier ||
            package.Sideband is not { Requirement: SidebandRequirement.OptionalCompatibility } ||
            package.Descriptor is not { Status: DescriptorAbiStatus.None, Descriptors.Count: 0 } ||
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
            reason = "Native scalar candidate lacks the separated structural envelopes or complete runtime dependency map.";
            opcodeFamilies = Array.Empty<string>();
            return false;
        }

        if (!package.SeparationProof.CarrierSeparatedFromSideband ||
            !package.SeparationProof.DescriptorSeparatedFromAuthority ||
            !package.SeparationProof.TypedSlotFactsSeparatedFromLegality ||
            !package.SeparationProof.EvidenceSeparatedFromProductionLowering ||
            !package.SeparationProof.BridgeSeparatedFromExecution)
        {
            reason = "Native scalar candidate failed artifact separation proof; production authority cannot be inferred.";
            opcodeFamilies = Array.Empty<string>();
            return false;
        }

        byte[] image = carrier.Image.SerializedImage;
        if (image.Length == 0 || image.Length % HybridCpuBundleSerializer.BundleSizeBytes != 0)
        {
            reason = "Native scalar candidate carrier image is empty or not bundle aligned.";
            opcodeFamilies = Array.Empty<string>();
            return false;
        }

        byte[] serialized = new HybridCpuBundleSerializer().SerializeProgram(carrier.Image.Bundles);
        if (!serialized.AsSpan().SequenceEqual(image))
        {
            reason = "Native scalar candidate carrier bytes do not match compiler bundle serialization.";
            opcodeFamilies = Array.Empty<string>();
            return false;
        }

        int occupiedCount = 0;
        int bundleCount = image.Length / HybridCpuBundleSerializer.BundleSizeBytes;
        if (package.TypedSlotFacts.Facts.Count < bundleCount)
        {
            reason = "Native scalar candidate does not provide structural facts for every carrier bundle.";
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
                reason = $"Native scalar candidate carrier bundle {bundleIndex} failed ISE-compatible decoding.";
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
                if (!IsSupportedScalarOpcode(opcode))
                {
                    reason = $"Opcode {opcode} is outside the bounded NativeVliwScalar production subset.";
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

                if (facts.GetSlotClass(slotIndex) != IrSlotClass.AluClass ||
                    facts.IsSlotPinned(slotIndex))
                {
                    reason = $"Scalar opcode {opcode} lacks flexible ALU structural slot facts at {bundleIndex}:{slotIndex}.";
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
            reason = "Native scalar production requires at least one supported scalar carrier opcode.";
            opcodeFamilies = Array.Empty<string>();
            return false;
        }

        opcodeFamilies = families;
        return true;
    }

    private static bool IsExactScalarIntent(CompilerSemanticIntent intent) =>
        intent.Kind == SemanticIntentKind.ScalarAlu &&
        intent.RequiresRuntimeLegality &&
        !intent.RequiresDescriptor &&
        !intent.RequiresSideband &&
        !intent.RequiresToken &&
        !intent.IsCompatibilityProjection &&
        !intent.IsPolicyAdmissionOnly &&
        !intent.IsHelperAbiOnly &&
        !intent.IsParserOnly &&
        !string.IsNullOrWhiteSpace(intent.OpcodeFamily);

    private static bool IsSupportedScalarOpcode(HybridCpuOpcode opcode) =>
        opcode is
            HybridCpuOpcode.ADD or
            HybridCpuOpcode.SUB or
            HybridCpuOpcode.MUL or
            HybridCpuOpcode.SLL or
            HybridCpuOpcode.SRL or
            HybridCpuOpcode.SRA or
            HybridCpuOpcode.XOR or
            HybridCpuOpcode.OR or
            HybridCpuOpcode.AND or
            HybridCpuOpcode.ADDI;
}
