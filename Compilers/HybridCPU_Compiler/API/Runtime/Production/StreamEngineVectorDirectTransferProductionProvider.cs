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
/// Explicit scoped production boundary for direct one-dimensional VLOAD/VSTORE
/// carriers. The helper ABI remains a candidate source; vector transfer
/// execution, memory publication, commit, and retire remain runtime-owned.
/// </summary>
public sealed class StreamEngineVectorDirectTransferProductionProvider : IContourProductionLoweringProvider
{
    private const string ProviderSurface = "StreamEngineVectorDirectTransferProductionProvider";

    private static readonly CompilerRuntimeAuthorityDependency RuntimeAuthorityPending =
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired |
        CompilerRuntimeAuthorityDependency.RuntimeCommitRequired |
        CompilerRuntimeAuthorityDependency.RuntimeRetireRequired |
        CompilerRuntimeAuthorityDependency.RuntimePublicationRequired;

    public static StreamEngineVectorDirectTransferProductionProvider Instance { get; } = new();

    public ExecutionContourKind ContourKind => ExecutionContourKind.StreamEngineVector;

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
                "StreamEngineVector direct-transfer production requires an explicit helper carrier package for shadow comparison.");
        }

        if (!TryValidateCandidate(
                candidate,
                intent,
                out string failureReason,
                out IReadOnlyList<string> opcodeFamilies,
                out IReadOnlyList<string> transferFacts))
        {
            return CompilerProductionLoweringResult.Rejected(gate, failureReason);
        }

        NoFallbackProof noFallbackProof = NoFallbackProof.Forbidden(
            $"stream-engine-vector-direct-transfer-production:{candidate.Identity.PackageId:N}",
            "StreamEngineVector production is limited to direct VLOAD/VSTORE 1D transfer; scalar, general vector, MatrixTile, DSC, L7, VMX, and backend fallback are forbidden.");

        IReadOnlyList<string> telemetryEvidence =
        [
            $"contour={ContourKind}",
            $"intent={intent.Kind}",
            $"opcode-family={string.Join(",", opcodeFamilies)}",
            $"producer-surface={ProviderSurface}",
            $"gate-id={CompilerProductionLoweringGateIds.StreamEngineVectorDirectTransferProduction}",
            $"artifact-id={candidate.Identity.PackageId:N}",
            "golden-artifact=verified",
            "helper-carrier-shadow-compare=verified",
            "ise-decode-parity=verified",
            $"transfer-facts={string.Join(";", transferFacts)}",
            "excluded-alternatives=scalar,general-vector,indexed,2D,transpose,segment,widening-fma,MatrixTile,DSC,L7,VMX",
            "runtime-authority=LegalityA,LegalityB,Execution,Publication,Commit,Retire-pending"
        ];

        CompilerEvidenceEnvelope evidence = candidate.Evidence with
        {
            EvidenceArtifacts = candidate.Evidence.EvidenceArtifacts
                .Contains(CompilerArtifactKind.TelemetrySnapshot)
                ? candidate.Evidence.EvidenceArtifacts
                : [.. candidate.Evidence.EvidenceArtifacts, CompilerArtifactKind.TelemetrySnapshot],
            Reason = "Direct StreamEngineVector transfer evidence is structural and runtime-authority-pending."
        };

        CompilerEmissionPackage productionPackage = candidate with
        {
            Identity = candidate.Identity with
            {
                ContourKind = ContourKind,
                IntentKind = SemanticIntentKind.VectorStream,
                ProducerSurface = ProviderSurface,
                Reason = "Explicit StreamEngineVector direct 1D transfer carrier package; vector transfer authority remains runtime-owned."
            },
            Evidence = evidence
        };

        return CompilerProductionLoweringResult.RuntimePending(
            gate,
            productionPackage,
            noFallbackProof,
            telemetryEvidence,
            "Direct StreamEngineVector transfer carrier package accepted; runtime vector legality, execution, publication, commit, and retire remain runtime-owned.");
    }

    private CompilerProductionLoweringGateRequest BuildGateRequest(
        CompilerSemanticIntent intent,
        ContourAnalysisReport analysis,
        CompilerProductionLoweringContext context)
    {
        bool directTransferIntent = IsExactDirectTransferIntent(intent);
        bool readinessComplete =
            context.Readiness.GoldenArtifactCoverage &&
            context.Readiness.IseDecodeParityPresent;

        return new()
        {
            IntentKind = intent.Kind,
            ContourKind = ContourKind,
            ClassifiedContourKind = analysis.ContourKind,
            SourceKind = CompilerProductionLoweringSourceKind.ExplicitProvider,
            IntentClassifierComplete = directTransferIntent,
            PresentArtifacts = PresentArtifacts(context.CandidatePackage),
            DeclaredRuntimeDependencies = RuntimeAuthorityPending,
            NoFallbackProofPresent = directTransferIntent && analysis.ContourKind == ContourKind,
            IseDecodeParityPresent = readinessComplete,
            TelemetryComplete = context.Readiness.TelemetryComplete,
            EvidenceComplete = context.Readiness.EvidenceComplete,
            DirectTransferScopeComplete = directTransferIntent
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
        out IReadOnlyList<string> opcodeFamilies,
        out IReadOnlyList<string> transferFacts)
    {
        List<string> families = new List<string>();
        List<string> factsEvidence = new List<string>();
        reason = string.Empty;

        if (!IsExactDirectTransferIntent(intent))
        {
            reason = "StreamEngineVector direct-transfer provider accepts VectorStream direct-transfer intent only; general vector and helper fallback are forbidden.";
            opcodeFamilies = Array.Empty<string>();
            transferFacts = Array.Empty<string>();
            return false;
        }

        if (package.Identity.ContourKind != ExecutionContourKind.StreamEngineVector ||
            package.Identity.IntentKind != SemanticIntentKind.VectorStream)
        {
            reason = "StreamEngineVector candidate identity does not match the exact direct-transfer contour.";
            opcodeFamilies = Array.Empty<string>();
            transferFacts = Array.Empty<string>();
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
            reason = "StreamEngineVector candidate lacks the separated structural envelopes or complete runtime dependency map.";
            opcodeFamilies = Array.Empty<string>();
            transferFacts = Array.Empty<string>();
            return false;
        }

        if (!package.SeparationProof.CarrierSeparatedFromSideband ||
            !package.SeparationProof.DescriptorSeparatedFromAuthority ||
            !package.SeparationProof.TypedSlotFactsSeparatedFromLegality ||
            !package.SeparationProof.EvidenceSeparatedFromProductionLowering ||
            !package.SeparationProof.BridgeSeparatedFromExecution)
        {
            reason = "StreamEngineVector candidate failed artifact separation proof; vector transfer authority cannot be inferred.";
            opcodeFamilies = Array.Empty<string>();
            transferFacts = Array.Empty<string>();
            return false;
        }

        byte[] image = carrier.Image.SerializedImage;
        if (image.Length == 0 || image.Length % HybridCpuBundleSerializer.BundleSizeBytes != 0)
        {
            reason = "StreamEngineVector candidate carrier image is empty or not bundle aligned.";
            opcodeFamilies = Array.Empty<string>();
            transferFacts = Array.Empty<string>();
            return false;
        }

        byte[] serialized = new HybridCpuBundleSerializer().SerializeProgram(carrier.Image.Bundles);
        if (!serialized.AsSpan().SequenceEqual(image))
        {
            reason = "StreamEngineVector candidate carrier bytes do not match compiler bundle serialization.";
            opcodeFamilies = Array.Empty<string>();
            transferFacts = Array.Empty<string>();
            return false;
        }

        int occupiedCount = 0;
        int bundleCount = image.Length / HybridCpuBundleSerializer.BundleSizeBytes;
        if (package.TypedSlotFacts.Facts.Count < bundleCount)
        {
            reason = "StreamEngineVector candidate does not provide structural facts for every carrier bundle.";
            opcodeFamilies = Array.Empty<string>();
            transferFacts = Array.Empty<string>();
            return false;
        }

        for (int bundleIndex = 0; bundleIndex < bundleCount; bundleIndex++)
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            if (!bundle.TryReadBytes(
                    image,
                    bundleIndex * HybridCpuBundleSerializer.BundleSizeBytes))
            {
                reason = $"StreamEngineVector candidate carrier bundle {bundleIndex} failed ISE-compatible decoding.";
                opcodeFamilies = Array.Empty<string>();
                transferFacts = Array.Empty<string>();
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
                if (!IsSupportedDirectTransferOpcode(opcode))
                {
                    reason = $"Opcode {opcode} is outside the bounded StreamEngineVector direct-transfer production subset.";
                    opcodeFamilies = Array.Empty<string>();
                    transferFacts = Array.Empty<string>();
                    return false;
                }

                if (!string.Equals(
                        intent.OpcodeFamily,
                        opcode.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"Intent opcode family '{intent.OpcodeFamily}' does not exactly match carrier opcode '{opcode}'.";
                    opcodeFamilies = Array.Empty<string>();
                    transferFacts = Array.Empty<string>();
                    return false;
                }

                if (facts.GetSlotClass(slotIndex) != IrSlotClass.LsuClass ||
                    facts.IsSlotPinned(slotIndex))
                {
                    reason = $"Vector transfer opcode {opcode} lacks flexible LSU structural slot facts at {bundleIndex}:{slotIndex}.";
                    opcodeFamilies = Array.Empty<string>();
                    transferFacts = Array.Empty<string>();
                    return false;
                }

                if (!TryReadDirectTransferFacts(
                        opcode,
                        instruction,
                        out string factReason,
                        out string factEvidence))
                {
                    reason = factReason;
                    opcodeFamilies = Array.Empty<string>();
                    transferFacts = Array.Empty<string>();
                    return false;
                }

                occupiedCount++;
                if (!families.Contains(opcode.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    families.Add(opcode.ToString());
                }

                factsEvidence.Add($"{bundleIndex}:{slotIndex}:{factEvidence}");
            }
        }

        if (occupiedCount == 0)
        {
            reason = "StreamEngineVector direct-transfer production requires at least one VLOAD or VSTORE carrier opcode.";
            opcodeFamilies = Array.Empty<string>();
            transferFacts = Array.Empty<string>();
            return false;
        }

        opcodeFamilies = families;
        transferFacts = factsEvidence;
        return true;
    }

    private static bool IsExactDirectTransferIntent(CompilerSemanticIntent intent) =>
        intent.Kind == SemanticIntentKind.VectorStream &&
        intent.RequiresRuntimeLegality &&
        !intent.RequiresDescriptor &&
        !intent.RequiresSideband &&
        !intent.RequiresToken &&
        !intent.IsCompatibilityProjection &&
        !intent.IsPolicyAdmissionOnly &&
        !intent.IsHelperAbiOnly &&
        !intent.IsParserOnly &&
        intent.OpcodeFamily is "VLOAD" or "VSTORE";

    private static bool IsSupportedDirectTransferOpcode(HybridCpuOpcode opcode) =>
        opcode is HybridCpuOpcode.VLOAD or HybridCpuOpcode.VSTORE;

    private static bool TryReadDirectTransferFacts(
        HybridCpuOpcode opcode,
        HybridCpuInstructionWord instruction,
        out string reason,
        out string evidence)
    {
        reason = string.Empty;
        evidence = string.Empty;

        if (instruction.DestSrc1Pointer == 0 || instruction.Src2Pointer == 0)
        {
            reason = $"Vector transfer opcode {opcode} lacks explicit non-zero source/destination address facts.";
            return false;
        }

        if (instruction.StreamLength == 0)
        {
            reason = $"Vector transfer opcode {opcode} has StreamLength == 0; empty transfer shapes fail closed.";
            return false;
        }

        if (instruction.Stride == 0)
        {
            reason = $"Vector transfer opcode {opcode} has Stride == 0; direct 1D transfer requires explicit non-zero stride.";
            return false;
        }

        ushort elementSize;
        try
        {
            elementSize = checked((ushort)HybridCpuDataTypes.SizeOf(instruction.DataTypeValue));
        }
        catch (ArgumentOutOfRangeException)
        {
            reason = $"Vector transfer opcode {opcode} has an unknown element data type.";
            return false;
        }

        if (instruction.Stride < elementSize)
        {
            reason = $"Vector transfer opcode {opcode} has Stride smaller than the encoded element size.";
            return false;
        }

        if (instruction.RowStride != 0 ||
            instruction.Indexed ||
            instruction.Is2D ||
            instruction.Reduction ||
            instruction.TailAgnostic ||
            instruction.MaskAgnostic)
        {
            reason = $"Vector transfer opcode {opcode} carries indexed/2D/reduction/masked fields outside the direct 1D transfer subset.";
            return false;
        }

        evidence =
            $"opcode={opcode},element-type={instruction.DataTypeValue},count={instruction.StreamLength},stride={instruction.Stride}," +
            $"dest-src1=0x{instruction.DestSrc1Pointer:X},src2-dest=0x{instruction.Src2Pointer:X}";
        return true;
    }
}
