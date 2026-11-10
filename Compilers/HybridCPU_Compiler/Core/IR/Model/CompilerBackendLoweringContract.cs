using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR;

public enum CompilerBackendCapabilityState : byte
{
    Unavailable = 0,
    DescriptorOnly = 1,
    ParserOnly = 2,
    ModelOnly = 3,
    ExecutableExperimental = 4,
    ProductionExecutable = 5
}

public enum CompilerBackendLoweringSurface : byte
{
    Lane6DmaStreamCompute = 0,
    Lane7SystemDeviceCommand = 1
}

[Flags]
public enum CompilerBackendLoweringRequirement : ulong
{
    None = 0,
    ExecutableCarrier = 1UL << 0,
    BackendAddressSpace = 1UL << 1,
    OrderCacheFaultContract = 1UL << 2,
    AllOrNoneRetirePublication = 1UL << 3,
    ResultPublication = 1UL << 4,
    ProductionBackendProtocol = 1UL << 5,
    QueueTokenFenceContract = 1UL << 6,
    StagedCommitBoundary = 1UL << 7,
    VirtualHandleContract = 1UL << 8
}

public sealed record CompilerBackendLoweringRequest
{
    public required CompilerBackendLoweringSurface Surface { get; init; }

    public required CompilerBackendCapabilityState State { get; init; }

    public CompilerBackendLoweringRequirement AvailableRequirements { get; init; } =
        CompilerBackendLoweringRequirement.None;

    public bool UsesDescriptorEvidenceOnly { get; init; }

    public bool UsesParserValidationOnly { get; init; }

    public bool UsesModelOrTestHelper { get; init; }

    public bool UsesRuntimeTokenOrVirtualHandleEvidence { get; init; }

    public bool AssumesHardwareCoherence { get; init; }

    public bool AssumesSuccessfulPartialCompletion { get; init; }
}

public sealed record CompilerBackendLoweringDecision
{
    private readonly bool _isAllowedObservation;

    private CompilerBackendLoweringDecision(
        bool isAllowed,
        CompilerBackendLoweringRequirement missingRequirements,
        string reason)
    {
        _isAllowedObservation = isAllowed;
        MissingRequirements = missingRequirements;
        Reason = string.IsNullOrWhiteSpace(reason)
            ? "No compiler/backend lowering decision reason was supplied."
            : reason;
    }

    [Obsolete(
        "Backend lowering IsAllowed is a legacy compiler observation only; adapt with CompilerLoweringDecision.FromLegacyBackendLoweringDecision before crossing public lowering boundaries.",
        false)]
    public bool IsAllowed => _isAllowedObservation;

    internal bool IsAllowedObservation => _isAllowedObservation;

    public CompilerBackendLoweringRequirement MissingRequirements { get; }

    public string Reason { get; }

    public static CompilerBackendLoweringDecision Allow(string reason) =>
        new(true, CompilerBackendLoweringRequirement.None, reason);

    public static CompilerBackendLoweringDecision Reject(
        CompilerBackendLoweringRequirement missingRequirements,
        string reason) =>
        new(false, missingRequirements, reason);
}

public static class CompilerBackendLoweringContract
{
    private static readonly IReadOnlyList<CompilerBackendCapabilityState> s_capabilityStates =
        Array.AsReadOnly(new[]
        {
            CompilerBackendCapabilityState.Unavailable,
            CompilerBackendCapabilityState.DescriptorOnly,
            CompilerBackendCapabilityState.ParserOnly,
            CompilerBackendCapabilityState.ModelOnly,
            CompilerBackendCapabilityState.ExecutableExperimental,
            CompilerBackendCapabilityState.ProductionExecutable
        });

    public static IReadOnlyList<CompilerBackendCapabilityState> CapabilityStates => s_capabilityStates;

    public static CompilerBackendLoweringRequirement FutureDscRequiredRequirements =>
        CompilerBackendLoweringRequirement.ExecutableCarrier |
        CompilerBackendLoweringRequirement.BackendAddressSpace |
        CompilerBackendLoweringRequirement.QueueTokenFenceContract |
        CompilerBackendLoweringRequirement.OrderCacheFaultContract |
        CompilerBackendLoweringRequirement.AllOrNoneRetirePublication |
        CompilerBackendLoweringRequirement.StagedCommitBoundary;

    public static CompilerBackendLoweringRequirement FutureL7RequiredRequirements =>
        CompilerBackendLoweringRequirement.ExecutableCarrier |
        CompilerBackendLoweringRequirement.ResultPublication |
        CompilerBackendLoweringRequirement.ProductionBackendProtocol |
        CompilerBackendLoweringRequirement.QueueTokenFenceContract |
        CompilerBackendLoweringRequirement.VirtualHandleContract |
        CompilerBackendLoweringRequirement.OrderCacheFaultContract |
        CompilerBackendLoweringRequirement.StagedCommitBoundary;

    [Obsolete(
        "Capability helpers are compiler observations only; they are not capability authority.",
        false)]
    public static bool CanSelectFeature(CompilerBackendCapabilityState state) =>
        state != CompilerBackendCapabilityState.Unavailable;

    [Obsolete(
        "Descriptor evidence helpers are compiler observations only; descriptor ABI validity is not execution authority.",
        false)]
    public static bool AllowsDescriptorEvidence(CompilerBackendCapabilityState state) =>
        state is
            CompilerBackendCapabilityState.DescriptorOnly or
            CompilerBackendCapabilityState.ExecutableExperimental or
            CompilerBackendCapabilityState.ProductionExecutable;

    [Obsolete(
        "Parser validation helpers are compiler observations only; parser success is not production lowering.",
        false)]
    public static bool AllowsParserValidation(CompilerBackendCapabilityState state) =>
        state is
            CompilerBackendCapabilityState.ParserOnly or
            CompilerBackendCapabilityState.ExecutableExperimental or
            CompilerBackendCapabilityState.ProductionExecutable;

    [Obsolete(
        "Model/helper helpers are compiler observations only; helper success is not production lowering.",
        false)]
    public static bool AllowsModelOrTestHelper(CompilerBackendCapabilityState state) =>
        state == CompilerBackendCapabilityState.ModelOnly;

    [Obsolete(
        "Production-selection bool is a legacy capability observation only; use a CompilerLoweringDecision adapter before new lowering boundaries.",
        false)]
    public static bool CanSelectForProductionLowering(CompilerBackendCapabilityState state) =>
        IsProductionExecutableState(state);

    public static CompilerBackendLoweringDecision EvaluateProductionDscLowering(
        CompilerBackendLoweringRequest request)
    {
        ValidateSurface(request, CompilerBackendLoweringSurface.Lane6DmaStreamCompute);
        return EvaluateProductionLowering(
            request,
            FutureDscRequiredRequirements,
            "lane6 DSC");
    }

    public static CompilerBackendLoweringDecision EvaluateProductionL7Lowering(
        CompilerBackendLoweringRequest request)
    {
        ValidateSurface(request, CompilerBackendLoweringSurface.Lane7SystemDeviceCommand);
        return EvaluateProductionLowering(
            request,
            FutureL7RequiredRequirements,
            "L7 ACCEL_*");
    }

    private static CompilerBackendLoweringDecision EvaluateProductionLowering(
        CompilerBackendLoweringRequest request,
        CompilerBackendLoweringRequirement requiredRequirements,
        string surfaceName)
    {
        if (!IsProductionExecutableState(request.State))
        {
            return CompilerBackendLoweringDecision.Reject(
                requiredRequirements,
                $"{surfaceName} production lowering requires ProductionExecutable state; {request.State} is non-production.");
        }

        if (request.UsesDescriptorEvidenceOnly || request.UsesParserValidationOnly)
        {
            return CompilerBackendLoweringDecision.Reject(
                requiredRequirements,
                $"{surfaceName} descriptor or parser evidence cannot be used as executable production lowering.");
        }

        if (request.UsesModelOrTestHelper)
        {
            return CompilerBackendLoweringDecision.Reject(
                requiredRequirements,
                $"{surfaceName} production lowering cannot route through model or test helper surfaces.");
        }

        if (request.UsesRuntimeTokenOrVirtualHandleEvidence)
        {
            CompilerBackendLoweringRequirement tokenHandleRequirements =
                requiredRequirements &
                (CompilerBackendLoweringRequirement.QueueTokenFenceContract |
                 CompilerBackendLoweringRequirement.VirtualHandleContract);
            if (tokenHandleRequirements == CompilerBackendLoweringRequirement.None)
            {
                tokenHandleRequirements = CompilerBackendLoweringRequirement.QueueTokenFenceContract;
            }

            return CompilerBackendLoweringDecision.Reject(
                tokenHandleRequirements,
                $"{surfaceName} production lowering cannot treat runtime token or virtual-handle evidence as compiler ABI authority.");
        }

        if (request.AssumesHardwareCoherence)
        {
            return CompilerBackendLoweringDecision.Reject(
                CompilerBackendLoweringRequirement.OrderCacheFaultContract,
                $"{surfaceName} production lowering cannot assume hardware coherence without an approved cache/order contract.");
        }

        if (request.AssumesSuccessfulPartialCompletion)
        {
            return CompilerBackendLoweringDecision.Reject(
                CompilerBackendLoweringRequirement.AllOrNoneRetirePublication,
                $"{surfaceName} production lowering cannot assume successful partial completion.");
        }

        CompilerBackendLoweringRequirement missing =
            requiredRequirements & ~request.AvailableRequirements;
        if (missing != CompilerBackendLoweringRequirement.None)
        {
            return CompilerBackendLoweringDecision.Reject(
                missing,
                $"{surfaceName} production lowering is missing required capability gates: {missing}.");
        }

        return CompilerBackendLoweringDecision.Allow(
            $"{surfaceName} production lowering is capability-complete.");
    }

    private static bool IsProductionExecutableState(CompilerBackendCapabilityState state) =>
        state == CompilerBackendCapabilityState.ProductionExecutable;

    private static void ValidateSurface(
        CompilerBackendLoweringRequest request,
        CompilerBackendLoweringSurface expectedSurface)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Surface != expectedSurface)
        {
            throw new ArgumentException(
                $"Expected {expectedSurface} lowering request, got {request.Surface}.",
                nameof(request));
        }
    }
}
