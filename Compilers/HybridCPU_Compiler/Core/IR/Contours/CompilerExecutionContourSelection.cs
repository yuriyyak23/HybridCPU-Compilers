using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Intent;

namespace HybridCPU.Compiler.Core.IR.Contours;

public enum ExecutionContourKind
{
    None = 0,
    NativeVliwScalar,
    NativeVliwLoadStore,
    NativeVliwBranchControl,
    StreamEngineVector,
    MatrixTileHelperOnly,
    DmaStreamComputeLane6,
    L7SdcLane7,
    VmxProjectionOnly,
    SecureComputePolicyAdmissionOnly,
    ParserOnly,
    NoEmission,
    FutureGated,
    UnknownRejected
}

public enum CompilerSidebandRequirement
{
    NotRequired = 0,
    StructuralEvidenceOnly,
    DescriptorSidebandRequired,
    PolicySidebandRequired,
    ProjectionEvidenceOnly
}

/// <summary>
/// Compiler-side contour selection. This is not a lowering decision and does
/// not create carrier, sideband, descriptor, typed-slot facts, or runtime authority.
/// </summary>
public sealed record CompilerExecutionContourSelection(
    ExecutionContourKind Kind,
    bool IsKnownContour,
    bool IsProviderAvailable,
    bool IsEmissionForbidden,
    bool RequiresSideband,
    bool RequiresDescriptor,
    bool RequiresRuntimeLegalityA,
    bool RequiresRuntimeLegalityB,
    CompilerSidebandRequirement SidebandRequirement,
    CompilerRuntimeAuthorityDependency RuntimeDependency,
    bool IsFallbackForbidden,
    string NoFallbackReason,
    string SelectionReason,
    IReadOnlyList<string> MissingInputs)
{
    public static CompilerExecutionContourSelection UnknownRejected(string reason) =>
        Create(
            ExecutionContourKind.UnknownRejected,
            isKnownContour: false,
            isProviderAvailable: false,
            isEmissionForbidden: true,
            requiresSideband: false,
            requiresDescriptor: false,
            sidebandRequirement: CompilerSidebandRequirement.NotRequired,
            runtimeDependency: CompilerRuntimeAuthorityDependency.NoRuntimeActionBecauseNoEmission,
            noFallbackReason: "Unknown contour fails closed; scalar fallback is forbidden.",
            selectionReason: reason,
            missingInputs: Array.Empty<string>());

    public static CompilerExecutionContourSelection NoEmission(
        ExecutionContourKind contour,
        CompilerSidebandRequirement sidebandRequirement,
        string reason) =>
        Create(
            contour,
            isKnownContour: true,
            isProviderAvailable: false,
            isEmissionForbidden: true,
            requiresSideband: sidebandRequirement != CompilerSidebandRequirement.NotRequired,
            requiresDescriptor: false,
            sidebandRequirement,
            CompilerRuntimeAuthorityDependency.NoRuntimeActionBecauseNoEmission,
            noFallbackReason: "No-emission contour cannot fall back into an executable contour.",
            selectionReason: reason,
            missingInputs: Array.Empty<string>());

    public static CompilerExecutionContourSelection RuntimeScoped(
        ExecutionContourKind contour,
        bool requiresSideband,
        bool requiresDescriptor,
        CompilerSidebandRequirement sidebandRequirement,
        string reason) =>
        Create(
            contour,
            isKnownContour: true,
            isProviderAvailable: false,
            isEmissionForbidden: false,
            requiresSideband,
            requiresDescriptor,
            sidebandRequirement,
            CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
            CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
            CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired,
            noFallbackReason: "Cross-contour fallback is forbidden by default.",
            selectionReason: reason,
            missingInputs: Array.Empty<string>());

    private static CompilerExecutionContourSelection Create(
        ExecutionContourKind contour,
        bool isKnownContour,
        bool isProviderAvailable,
        bool isEmissionForbidden,
        bool requiresSideband,
        bool requiresDescriptor,
        CompilerSidebandRequirement sidebandRequirement,
        CompilerRuntimeAuthorityDependency runtimeDependency,
        string noFallbackReason,
        string selectionReason,
        IReadOnlyList<string> missingInputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noFallbackReason);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectionReason);

        IReadOnlyList<string> inputs = missingInputs.Count == 0
            ? Array.Empty<string>()
            : [.. missingInputs];

        return new(
            contour,
            isKnownContour,
            isProviderAvailable,
            isEmissionForbidden,
            requiresSideband,
            requiresDescriptor,
            runtimeDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired),
            runtimeDependency.HasFlag(CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired),
            sidebandRequirement,
            runtimeDependency,
            IsFallbackForbidden: true,
            noFallbackReason,
            selectionReason,
            inputs);
    }
}

public interface IExecutionContourSelector
{
    CompilerExecutionContourSelection SelectContour(CompilerSemanticIntent intent);
}

public sealed class CompilerDefaultExecutionContourSelector : IExecutionContourSelector
{
    public static CompilerDefaultExecutionContourSelector Instance { get; } = new();

    private CompilerDefaultExecutionContourSelector()
    {
    }

    public CompilerExecutionContourSelection SelectContour(CompilerSemanticIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        return intent.Kind switch
        {
            SemanticIntentKind.ScalarAlu => CompilerExecutionContourSelection.RuntimeScoped(
                ExecutionContourKind.NativeVliwScalar,
                requiresSideband: false,
                requiresDescriptor: false,
                CompilerSidebandRequirement.NotRequired,
                "Scalar ALU intent selects native scalar VLIW contour; runtime Legality A/B still required."),

            SemanticIntentKind.LoadStore => CompilerExecutionContourSelection.RuntimeScoped(
                ExecutionContourKind.NativeVliwLoadStore,
                requiresSideband: false,
                requiresDescriptor: false,
                CompilerSidebandRequirement.NotRequired,
                "Load/store intent selects native load/store VLIW contour; runtime Legality A/B still required."),

            SemanticIntentKind.BranchControl => CompilerExecutionContourSelection.RuntimeScoped(
                ExecutionContourKind.NativeVliwBranchControl,
                requiresSideband: false,
                requiresDescriptor: false,
                CompilerSidebandRequirement.NotRequired,
                "Branch/control intent selects native branch-control VLIW contour; runtime commit/retire remain runtime-owned."),

            SemanticIntentKind.VectorStream => CompilerExecutionContourSelection.RuntimeScoped(
                ExecutionContourKind.StreamEngineVector,
                requiresSideband: true,
                requiresDescriptor: false,
                CompilerSidebandRequirement.StructuralEvidenceOnly,
                "Vector stream intent selects stream-engine vector contour only; scalar fallback is forbidden."),

            SemanticIntentKind.MatrixTile => CompilerExecutionContourSelection.RuntimeScoped(
                ExecutionContourKind.MatrixTileHelperOnly,
                requiresSideband: true,
                requiresDescriptor: false,
                CompilerSidebandRequirement.PolicySidebandRequired,
                "MatrixTile intent is helper ABI only; scalar/vector/Stream fallback is forbidden."),

            SemanticIntentKind.DmaStreamCompute => CompilerExecutionContourSelection.RuntimeScoped(
                ExecutionContourKind.DmaStreamComputeLane6,
                requiresSideband: true,
                requiresDescriptor: true,
                CompilerSidebandRequirement.DescriptorSidebandRequired,
                "DmaStreamCompute intent selects lane6 DSC contour only; L7/Stream/scalar fallback is forbidden."),

            SemanticIntentKind.ExternalAcceleratorCommand => CompilerExecutionContourSelection.RuntimeScoped(
                ExecutionContourKind.L7SdcLane7,
                requiresSideband: true,
                requiresDescriptor: true,
                CompilerSidebandRequirement.DescriptorSidebandRequired,
                "External accelerator command selects L7-SDC lane7 only; descriptorless submit fails closed."),

            SemanticIntentKind.VmxCompatibilityProjection => CompilerExecutionContourSelection.NoEmission(
                ExecutionContourKind.VmxProjectionOnly,
                CompilerSidebandRequirement.ProjectionEvidenceOnly,
                "VMX compatibility intent is projection/no-emission only; VMCS is not compiler-owned state."),

            SemanticIntentKind.SecureComputeAdmission => CompilerExecutionContourSelection.NoEmission(
                ExecutionContourKind.SecureComputePolicyAdmissionOnly,
                CompilerSidebandRequirement.StructuralEvidenceOnly,
                "SecureCompute intent is policy/admission/evidence-only; secure backend execution is forbidden."),

            SemanticIntentKind.RuntimeAssist => CompilerExecutionContourSelection.NoEmission(
                ExecutionContourKind.FutureGated,
                CompilerSidebandRequirement.StructuralEvidenceOnly,
                "Runtime assist intent is future-gated until explicit runtime-owned gates exist."),

            SemanticIntentKind.NonExecutable => CompilerExecutionContourSelection.NoEmission(
                ExecutionContourKind.NoEmission,
                CompilerSidebandRequirement.NotRequired,
                "Non-executable intent cannot select an emission contour."),

            _ => CompilerExecutionContourSelection.UnknownRejected(intent.Reason)
        };
    }
}
