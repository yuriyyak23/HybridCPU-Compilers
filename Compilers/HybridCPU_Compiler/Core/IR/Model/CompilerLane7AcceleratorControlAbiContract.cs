using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR;

public enum CompilerLane7AcceleratorControlAbiClass : byte
{
    AcceleratorAbiQuery = 0,
    AcceleratorTopologyQuery = 1,
    AcceleratorLifecycle = 2,
    AcceleratorQueueBinding = 3
}

/// <summary>
/// Compiler-visible no-emission ABI audit for Lane7 accelerator control rows.
/// </summary>
public sealed class CompilerLane7AcceleratorControlAbiContract
{
    private static readonly string[] RequiredCompilerEvidenceGates =
    [
        "ExplicitCompilerHelperAbi",
        "PublishedOpcodeEncodingAbi",
        "MetadataCapabilityAuthority",
        "RuntimeOwnedLegalityEvidence",
        "RetireReplayGoldenConformance",
        "NoHostEvidenceLeak",
        "NoBackendFallbackBoundary"
    ];

    private CompilerLane7AcceleratorControlAbiContract(
        CompilerLane7DeferredAbiContract deferredContract,
        CompilerLane7AcceleratorControlAbiClass abiClass,
        string helperBoundary,
        bool requiresAcceleratorAbiQueryContract = false,
        bool requiresAcceleratorTopologyAbi = false,
        bool requiresOpenCloseLifecycleAbi = false,
        bool requiresBindUnbindQueueAbi = false)
    {
        DeferredContract = deferredContract;
        AbiClass = abiClass;
        HelperBoundary = helperBoundary;
        RequiresAcceleratorAbiQueryContract = requiresAcceleratorAbiQueryContract;
        RequiresAcceleratorTopologyAbi = requiresAcceleratorTopologyAbi;
        RequiresOpenCloseLifecycleAbi = requiresOpenCloseLifecycleAbi;
        RequiresBindUnbindQueueAbi = requiresBindUnbindQueueAbi;
    }

    public static CompilerLane7AcceleratorControlAbiContract AcceleratorQueryAbi { get; } =
        new(
            CompilerLane7DeferredAbiContract.AcceleratorQueryAbi,
            CompilerLane7AcceleratorControlAbiClass.AcceleratorAbiQuery,
            "NoCompilerHelperUntilAcceleratorAbiCapabilityAuthorityResultScrubbingReplayRetireAbi",
            requiresAcceleratorAbiQueryContract: true);

    public static CompilerLane7AcceleratorControlAbiContract AcceleratorQueryTopology { get; } =
        new(
            CompilerLane7DeferredAbiContract.AcceleratorQueryTopology,
            CompilerLane7AcceleratorControlAbiClass.AcceleratorTopologyQuery,
            "NoCompilerHelperUntilAcceleratorTopologyCapabilityAuthorityResultScrubbingReplayRetireAbi",
            requiresAcceleratorTopologyAbi: true);

    public static CompilerLane7AcceleratorControlAbiContract AcceleratorOpen { get; } =
        CreateAcceleratorLifecycleRow(
            CompilerLane7DeferredAbiContract.AcceleratorOpen,
            "NoCompilerHelperUntilAcceleratorOpenRuntimeDeviceTokenHandleLifecycleReplayRetireAbi");

    public static CompilerLane7AcceleratorControlAbiContract AcceleratorClose { get; } =
        CreateAcceleratorLifecycleRow(
            CompilerLane7DeferredAbiContract.AcceleratorClose,
            "NoCompilerHelperUntilAcceleratorCloseRuntimeDeviceTokenHandleLifecycleReplayRetireAbi");

    public static CompilerLane7AcceleratorControlAbiContract AcceleratorBindQueue { get; } =
        CreateAcceleratorQueueBindingRow(
            CompilerLane7DeferredAbiContract.AcceleratorBindQueue,
            "NoCompilerHelperUntilAcceleratorBindQueueRuntimeQueueTokenLane6OwnershipReplayRetireAbi");

    public static CompilerLane7AcceleratorControlAbiContract AcceleratorUnbindQueue { get; } =
        CreateAcceleratorQueueBindingRow(
            CompilerLane7DeferredAbiContract.AcceleratorUnbindQueue,
            "NoCompilerHelperUntilAcceleratorUnbindQueueRuntimeQueueTokenLane6OwnershipReplayRetireAbi");

    public static IReadOnlyList<CompilerLane7AcceleratorControlAbiContract> AllAcceleratorControlRows { get; } =
    [
        AcceleratorQueryAbi,
        AcceleratorQueryTopology,
        AcceleratorOpen,
        AcceleratorClose,
        AcceleratorBindQueue,
        AcceleratorUnbindQueue
    ];

    public CompilerLane7DeferredAbiContract DeferredContract { get; }
    public CompilerLane7AcceleratorControlAbiClass AbiClass { get; }
    public string Mnemonic => DeferredContract.Mnemonic;
    public string ExtensionName => DeferredContract.ExtensionName;
    public string EvidenceBoundary => DeferredContract.EvidenceBoundary;
    public string AbiDecision => DeferredContract.AbiDecision;
    public string OperandShape => DeferredContract.OperandShape;
    public string DataSemantics => DeferredContract.DataSemantics;
    public string ResultSemantics => DeferredContract.ResultSemantics;
    public IReadOnlyList<string> RequiredPolicyDecisions => DeferredContract.RequiredPolicyDecisions;
    public IReadOnlyList<string> RequiredEvidenceGates => RequiredCompilerEvidenceGates;
    public string HelperBoundary { get; }
    public bool RequiresAcceleratorAbiQueryContract { get; }
    public bool RequiresAcceleratorTopologyAbi { get; }
    public bool RequiresOpenCloseLifecycleAbi { get; }
    public bool RequiresBindUnbindQueueAbi { get; }
    public bool RequiresExplicitCompilerHelperAbi => true;
    public bool RequiresPublishedOpcodeEncodingAbi => true;
    public bool RequiresMetadataCapabilityAuthority => true;
    public bool RequiresRuntimeOwnedLegalityEvidence => true;
    public bool RequiresRetireReplayGoldenConformance => true;
    public bool IsAcceleratorCapabilityQuery => DeferredContract.IsAcceleratorCapabilityQuery;
    public bool IsAcceleratorLifecycleControl => DeferredContract.IsAcceleratorLifecycleControl;
    public bool IsAcceleratorQueueBindingControl => DeferredContract.IsAcceleratorQueueBindingControl;
    public bool RequiresCapabilityAuthority => DeferredContract.RequiresCapabilityAuthority;
    public bool RequiresBoundedCapabilityResultFootprint => DeferredContract.RequiresBoundedCapabilityResultFootprint;
    public bool RequiresBoundedTopologyResultFootprint => DeferredContract.RequiresBoundedTopologyResultFootprint;
    public bool RequiresResultScrubbingPolicy => DeferredContract.RequiresResultScrubbingPolicy;
    public bool RequiresOwnerDomainGuard => DeferredContract.RequiresOwnerDomainGuard;
    public bool RequiresCommandQueueSemantics => DeferredContract.RequiresCommandQueueSemantics;
    public bool RequiresRetireOwnedPublication => DeferredContract.RequiresRetireOwnedPublication;
    public bool RequiresReplayStableCapabilityModel => DeferredContract.RequiresReplayStableCapabilityModel;
    public bool RequiresMigrationCheckpointPolicy => DeferredContract.RequiresMigrationCheckpointPolicy;
    public bool RequiresFutureVirtualizationBoundaryPolicy => DeferredContract.RequiresFutureVirtualizationBoundaryPolicy;
    public bool RequiresBackendCapabilityAuthority => DeferredContract.RequiresBackendCapabilityAuthority;
    public bool RequiresGuestVisibleCapabilityPolicy => DeferredContract.RequiresGuestVisibleCapabilityPolicy;
    public bool RequiresAcceleratorRuntimeAuthority => DeferredContract.RequiresAcceleratorRuntimeAuthority;
    public bool RequiresDeviceAuthority => DeferredContract.RequiresDeviceAuthority;
    public bool RequiresTokenAuthority => DeferredContract.RequiresTokenAuthority;
    public bool RequiresHandleNamespaceAbi => DeferredContract.RequiresHandleNamespaceAbi;
    public bool RequiresReplayStableLifecycleModel => DeferredContract.RequiresReplayStableLifecycleModel;
    public bool RequiresQueueAuthority => DeferredContract.RequiresQueueAuthority;
    public bool RequiresLane6TokenAuthorityGate => DeferredContract.RequiresLane6TokenAuthorityGate;
    public bool RequiresQueueOwnershipModel => DeferredContract.RequiresQueueOwnershipModel;
    public bool RequiresReplayStableQueueBindingModel => DeferredContract.RequiresReplayStableQueueBindingModel;
    public bool RequiresQueueBindUnbindOrderingModel => DeferredContract.RequiresQueueBindUnbindOrderingModel;
    public bool RequiresRetireOwnedSideEffectPublication => DeferredContract.RequiresRetireOwnedSideEffectPublication;
    public bool RequiresNoHostEvidenceLeak => DeferredContract.RequiresNoHostEvidenceLeak;
    public bool NoGenericSystemOpFallback => DeferredContract.NoGenericSystemOpFallback;
    public bool VmxCapabilityEvidenceIsInsufficient => DeferredContract.VmxCapabilityEvidenceIsInsufficient;
    public bool VmxBackendAuthorityEvidenceIsInsufficient => DeferredContract.VmxBackendAuthorityEvidenceIsInsufficient;
    public bool VmxMigrationCheckpointEvidenceIsInsufficient => DeferredContract.VmxMigrationCheckpointEvidenceIsInsufficient;
    public bool ExistingLane6DmaEvidenceIsInsufficient => DeferredContract.ExistingLane6DmaEvidenceIsInsufficient;
    public bool ExistingAccelSubmitEvidenceIsInsufficient => DeferredContract.ExistingAccelSubmitEvidenceIsInsufficient;
    public bool ExistingAccelQueryCapsEvidenceIsInsufficient => DeferredContract.ExistingAccelQueryCapsEvidenceIsInsufficient;
    public bool ExistingTopologyQueueTaxonomyEvidenceIsInsufficient => DeferredContract.ExistingTopologyQueueTaxonomyEvidenceIsInsufficient;
    public bool ExistingLane7ControlPlaneEvidenceIsInsufficient => DeferredContract.ExistingLane7ControlPlaneEvidenceIsInsufficient;
    public bool NoLane6DmaFallback => DeferredContract.NoLane6DmaFallback;
    public bool NoLane7SubmitFallback => DeferredContract.NoLane7SubmitFallback;
    public bool NoExternalBackendFallback => DeferredContract.NoExternalBackendFallback;
    public bool NoCapabilityPublicationBeforeAuthority => DeferredContract.NoCapabilityPublicationBeforeAuthority;
    public bool NoLifecycleStatePublicationBeforeRetire => DeferredContract.NoLifecycleStatePublicationBeforeRetire;
    public bool NoBackendAdmissionBeforeAuthority => DeferredContract.NoBackendAdmissionBeforeAuthority;
    public bool NoQueueBindingPublicationBeforeRetire => DeferredContract.NoQueueBindingPublicationBeforeRetire;
    public bool NoQueueBindingBeforeTokenAuthority => DeferredContract.NoQueueBindingBeforeTokenAuthority;
    public bool NoHostEvidenceLeak => DeferredContract.NoHostEvidenceLeak;
    public bool NoHiddenScalarLowering => DeferredContract.NoHiddenScalarLowering;
    public bool NoMultiOpEmission => DeferredContract.NoMultiOpEmission;
    public bool HasOpcodeAllocation => false;
    public bool IsExecutable => false;
    public bool CompilerEmissionAllowed => false;
    public bool CompilerHelperAllowed => false;

    public void RequireCompilerEmissionAuthority()
    {
        string requiredDecisions = AbiClass switch
        {
            CompilerLane7AcceleratorControlAbiClass.AcceleratorAbiQuery =>
                "accelerator ABI query contract, capability authority, bounded result footprint, result scrubbing, replay, retire-owned publication, and no host/backend fallback ABI decisions",
            CompilerLane7AcceleratorControlAbiClass.AcceleratorTopologyQuery =>
                "accelerator topology contract, capability authority, bounded topology result footprint, result scrubbing, replay, retire-owned publication, and no host/backend fallback ABI decisions",
            CompilerLane7AcceleratorControlAbiClass.AcceleratorLifecycle =>
                "accelerator runtime, device, token, handle namespace, lifecycle, replay, retire-owned side-effect publication, and no backend admission fallback ABI decisions",
            CompilerLane7AcceleratorControlAbiClass.AcceleratorQueueBinding =>
                "accelerator runtime, queue, token, Lane6 token gate, ownership, ordering, replay, retire-owned side-effect publication, and no Lane6/backend fallback ABI decisions",
            _ => "required Lane7 accelerator control ABI decisions"
        };

        throw new InvalidOperationException(
            $"{Mnemonic} compiler emission is blocked until {requiredDecisions} are explicit.");
    }

    private static CompilerLane7AcceleratorControlAbiContract CreateAcceleratorLifecycleRow(
        CompilerLane7DeferredAbiContract deferredContract,
        string helperBoundary) =>
        new(
            deferredContract,
            CompilerLane7AcceleratorControlAbiClass.AcceleratorLifecycle,
            helperBoundary,
            requiresOpenCloseLifecycleAbi: true);

    private static CompilerLane7AcceleratorControlAbiContract CreateAcceleratorQueueBindingRow(
        CompilerLane7DeferredAbiContract deferredContract,
        string helperBoundary) =>
        new(
            deferredContract,
            CompilerLane7AcceleratorControlAbiClass.AcceleratorQueueBinding,
            helperBoundary,
            requiresBindUnbindQueueAbi: true);
}
