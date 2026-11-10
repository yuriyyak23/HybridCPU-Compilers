using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR;

public enum CompilerLane7DeferredAbiClass : byte
{
    SchedulingHint = 0,
    TranslationFence = 1,
    CacheMaintenance = 2,
    IommuMaintenance = 3,
    AcceleratorCapability = 4,
    AcceleratorLifecycle = 5,
    AcceleratorQueueBinding = 6
}

/// <summary>
/// Compiler-visible Lane7 contract for reserved rows that must remain no-emission.
/// </summary>
public sealed class CompilerLane7DeferredAbiContract
{
    private static readonly string[] PauseRequiredPolicyDecisions =
    [
        "HintEncodingAbi",
        "SchedulerFairnessPolicy",
        "ProgressGuaranteePolicy",
        "ReplayRollbackPolicy",
        "NoArchitecturalStateLeakage",
        "NoSynchronizationPrimitiveSemantics"
    ];

    private static readonly string[] SfenceVmaRequiredPolicyDecisions =
    [
        "PrivilegeAndAdmissionPolicy",
        "AddressSpaceSelectorAbi",
        "TranslationStateOwnershipModel",
        "TlbShootdownPolicy",
        "CrossCoreShootdownPolicy",
        "ReplayStableInvalidationModel",
        "RetireOwnedSideEffectPublication",
        "NoVmxEptVpidNptSemanticAlias"
    ];

    private static readonly string[] InstructionCacheRequiredPolicyDecisions =
    [
        "CacheMaintenanceAbi",
        "InstructionFetchCoherencyModel",
        "CacheHierarchyAuthorityModel",
        "AddressRangeScopeAbi",
        "PrivilegeAndAdmissionPolicy",
        "ReplayStableInvalidationModel",
        "RetireOwnedSideEffectPublication",
        "NoFenceIFallback"
    ];

    private static readonly string[] DataCacheRequiredPolicyDecisions =
    [
        "CacheMaintenanceAbi",
        "DataCacheCoherencyModel",
        "CacheHierarchyAuthorityModel",
        "AddressRangeScopeAbi",
        "DirtyLineOwnershipModel",
        "MemoryOrderingIntegration",
        "PrivilegeAndAdmissionPolicy",
        "ReplayStableInvalidationModel",
        "RetireOwnedSideEffectPublication",
        "NoFenceIFallback"
    ];

    private static readonly string[] IotlbRequiredPolicyDecisions =
    [
        "IommuMaintenanceAbi",
        "IotlbInvalidationModel",
        "DeviceDomainAuthority",
        "DmaVisibilityModel",
        "Lane6TokenAuthorityGate",
        "ExternalDeviceQuiescencePolicy",
        "PrivilegeAndAdmissionPolicy",
        "ReplayStableInvalidationModel",
        "RetireOwnedSideEffectPublication",
        "NoVmxEptVpidNptSemanticAlias",
        "NoLane6DmaFallback",
        "NoLane7AcceleratorFallback",
        "NoExternalBackendFallback"
    ];

    private static readonly string[] IommuFenceRequiredPolicyDecisions =
    [
        "IommuMaintenanceAbi",
        "IommuFenceCompletionModel",
        "DeviceDomainAuthority",
        "DmaVisibilityModel",
        "Lane6TokenAuthorityGate",
        "ExternalDeviceQuiescencePolicy",
        "PrivilegeAndAdmissionPolicy",
        "ReplayStableInvalidationModel",
        "RetireOwnedSideEffectPublication",
        "NoVmxEptVpidNptSemanticAlias",
        "NoLane6DmaFallback",
        "NoLane7AcceleratorFallback",
        "NoExternalBackendFallback"
    ];

    private static readonly string[] AcceleratorAbiQueryRequiredPolicyDecisions =
    [
        "CapabilityAuthority",
        "AcceleratorAbiQueryContract",
        "BoundedCapabilityResultFootprint",
        "ResultScrubbingPolicy",
        "OwnerDomainGuard",
        "CommandQueueSemantics",
        "RetireOwnedPublication",
        "ReplayStableCapabilityModel",
        "BackendCapabilityAuthority",
        "GuestVisibleCapabilityPolicy",
        "MigrationCheckpointPolicy",
        "FutureVirtualizationBoundaryPolicy",
        "NoCapabilityPublicationBeforeAuthority",
        "NoGenericSystemOpFallback",
        "NoLane7SubmitFallback",
        "NoExternalBackendFallback"
    ];

    private static readonly string[] AcceleratorTopologyRequiredPolicyDecisions =
    [
        "CapabilityAuthority",
        "AcceleratorTopologyAbi",
        "BoundedTopologyResultFootprint",
        "ResultScrubbingPolicy",
        "OwnerDomainGuard",
        "CommandQueueSemantics",
        "RetireOwnedPublication",
        "ReplayStableCapabilityModel",
        "BackendCapabilityAuthority",
        "GuestVisibleCapabilityPolicy",
        "MigrationCheckpointPolicy",
        "FutureVirtualizationBoundaryPolicy",
        "NoCapabilityPublicationBeforeAuthority",
        "NoGenericSystemOpFallback",
        "NoLane7SubmitFallback",
        "NoExternalBackendFallback"
    ];

    private static readonly string[] AcceleratorLifecycleRequiredPolicyDecisions =
    [
        "AcceleratorRuntimeAuthority",
        "DeviceAuthority",
        "TokenAuthority",
        "OwnerDomainGuard",
        "HandleNamespaceAbi",
        "OpenCloseLifecycleAbi",
        "CommandQueueSemantics",
        "ReplayStableLifecycleModel",
        "RetireOwnedSideEffectPublication",
        "MigrationCheckpointPolicy",
        "FutureVirtualizationBoundaryPolicy",
        "NoLifecycleStatePublicationBeforeRetire",
        "NoBackendAdmissionBeforeAuthority",
        "NoGenericSystemOpFallback",
        "NoLane7SubmitFallback",
        "NoExternalBackendFallback"
    ];

    private static readonly string[] AcceleratorQueueBindingRequiredPolicyDecisions =
    [
        "AcceleratorRuntimeAuthority",
        "QueueAuthority",
        "TokenAuthority",
        "Lane6TokenAuthorityGate",
        "OwnerDomainGuard",
        "BindUnbindQueueAbi",
        "QueueOwnershipModel",
        "CommandQueueSemantics",
        "ReplayStableQueueBindingModel",
        "QueueBindUnbindOrderingModel",
        "RetireOwnedSideEffectPublication",
        "MigrationCheckpointPolicy",
        "FutureVirtualizationBoundaryPolicy",
        "NoQueueBindingPublicationBeforeRetire",
        "NoQueueBindingBeforeTokenAuthority",
        "NoGenericSystemOpFallback",
        "NoLane6DmaFallback",
        "NoLane7SubmitFallback",
        "NoExternalBackendFallback"
    ];

    private CompilerLane7DeferredAbiContract(
        string mnemonic,
        CompilerLane7DeferredAbiClass abiClass,
        string extensionName,
        string evidenceBoundary,
        string abiDecision,
        string operandShape,
        string dataSemantics,
        string resultSemantics,
        IReadOnlyList<string> requiredPolicyDecisions,
        bool requiresHintEncodingAbi = false,
        bool requiresProgressFairnessPolicy = false,
        bool noArchitecturalProgressGuarantee = false,
        bool requiresNoArchitecturalStateLeakage = false,
        bool rejectSynchronizationPrimitiveSemantics = false,
        bool requiresReplayRollbackEvidence = false,
        bool requiresPrivilegeAndAdmissionPolicy = false,
        bool requiresAddressSpaceSelectorAbi = false,
        bool requiresTlbShootdownPolicy = false,
        bool requiresCrossCoreShootdownPolicy = false,
        bool requiresTranslationStateOwnershipModel = false,
        bool requiresPageTableWalkOwnershipModel = false,
        bool requiresCacheMaintenanceAbi = false,
        bool requiresInstructionFetchCoherencyModel = false,
        bool requiresDataCacheCoherencyModel = false,
        bool requiresCacheHierarchyAuthorityModel = false,
        bool requiresAddressRangeScopeAbi = false,
        bool requiresDirtyLineOwnershipModel = false,
        bool requiresMemoryOrderingIntegration = false,
        bool requiresIommuMaintenanceAbi = false,
        bool requiresIotlbInvalidationModel = false,
        bool requiresIommuFenceCompletionModel = false,
        bool requiresDeviceDomainAuthority = false,
        bool requiresDmaVisibilityModel = false,
        bool requiresLane6TokenAuthorityGate = false,
        bool requiresExternalDeviceQuiescencePolicy = false,
        bool isAcceleratorCapabilityQuery = false,
        bool isAcceleratorLifecycleControl = false,
        bool isAcceleratorQueueBindingControl = false,
        bool requiresCapabilityAuthority = false,
        bool requiresAcceleratorAbiQueryContract = false,
        bool requiresAcceleratorTopologyAbi = false,
        bool requiresBoundedCapabilityResultFootprint = false,
        bool requiresBoundedTopologyResultFootprint = false,
        bool requiresResultScrubbingPolicy = false,
        bool requiresOwnerDomainGuard = false,
        bool requiresCommandQueueSemantics = false,
        bool requiresRetireOwnedPublication = false,
        bool requiresReplayStableCapabilityModel = false,
        bool requiresMigrationCheckpointPolicy = false,
        bool requiresFutureVirtualizationBoundaryPolicy = false,
        bool requiresBackendCapabilityAuthority = false,
        bool requiresGuestVisibleCapabilityPolicy = false,
        bool requiresAcceleratorRuntimeAuthority = false,
        bool requiresDeviceAuthority = false,
        bool requiresTokenAuthority = false,
        bool requiresHandleNamespaceAbi = false,
        bool requiresOpenCloseLifecycleAbi = false,
        bool requiresReplayStableLifecycleModel = false,
        bool requiresQueueAuthority = false,
        bool requiresBindUnbindQueueAbi = false,
        bool requiresQueueOwnershipModel = false,
        bool requiresReplayStableQueueBindingModel = false,
        bool requiresQueueBindUnbindOrderingModel = false,
        bool requiresReplayStableInvalidationModel = false,
        bool requiresRetireOwnedSideEffectPublication = false,
        bool requiresNoHostEvidenceLeak = false,
        bool noGenericFenceFallback = false,
        bool noGenericSystemOpFallback = false,
        bool noFenceIFallback = false,
        bool noVmxEptVpidNptSemanticAlias = false,
        bool vmxCacheEvidenceIsInsufficient = false,
        bool vmxIommuEvidenceIsInsufficient = false,
        bool vmxCapabilityEvidenceIsInsufficient = false,
        bool vmxBackendAuthorityEvidenceIsInsufficient = false,
        bool vmxMigrationCheckpointEvidenceIsInsufficient = false,
        bool existingLane6DmaEvidenceIsInsufficient = false,
        bool existingAccelSubmitEvidenceIsInsufficient = false,
        bool existingAccelQueryCapsEvidenceIsInsufficient = false,
        bool existingTopologyQueueTaxonomyEvidenceIsInsufficient = false,
        bool existingLane7ControlPlaneEvidenceIsInsufficient = false,
        bool noLane6DmaFallback = false,
        bool noLane7AcceleratorFallback = false,
        bool noLane7SubmitFallback = false,
        bool noExternalBackendFallback = false,
        bool noCapabilityPublicationBeforeAuthority = false,
        bool noLifecycleStatePublicationBeforeRetire = false,
        bool noBackendAdmissionBeforeAuthority = false,
        bool noQueueBindingPublicationBeforeRetire = false,
        bool noQueueBindingBeforeTokenAuthority = false,
        bool noHostEvidenceLeak = false,
        bool noHiddenScalarLowering = false,
        bool noMultiOpEmission = false)
    {
        Mnemonic = mnemonic;
        AbiClass = abiClass;
        ExtensionName = extensionName;
        EvidenceBoundary = evidenceBoundary;
        AbiDecision = abiDecision;
        OperandShape = operandShape;
        DataSemantics = dataSemantics;
        ResultSemantics = resultSemantics;
        RequiredPolicyDecisions = requiredPolicyDecisions;
        RequiresHintEncodingAbi = requiresHintEncodingAbi;
        RequiresProgressFairnessPolicy = requiresProgressFairnessPolicy;
        NoArchitecturalProgressGuarantee = noArchitecturalProgressGuarantee;
        RequiresNoArchitecturalStateLeakage = requiresNoArchitecturalStateLeakage;
        RejectSynchronizationPrimitiveSemantics = rejectSynchronizationPrimitiveSemantics;
        RequiresReplayRollbackEvidence = requiresReplayRollbackEvidence;
        RequiresPrivilegeAndAdmissionPolicy = requiresPrivilegeAndAdmissionPolicy;
        RequiresAddressSpaceSelectorAbi = requiresAddressSpaceSelectorAbi;
        RequiresTlbShootdownPolicy = requiresTlbShootdownPolicy;
        RequiresCrossCoreShootdownPolicy = requiresCrossCoreShootdownPolicy;
        RequiresTranslationStateOwnershipModel = requiresTranslationStateOwnershipModel;
        RequiresPageTableWalkOwnershipModel = requiresPageTableWalkOwnershipModel;
        RequiresCacheMaintenanceAbi = requiresCacheMaintenanceAbi;
        RequiresInstructionFetchCoherencyModel = requiresInstructionFetchCoherencyModel;
        RequiresDataCacheCoherencyModel = requiresDataCacheCoherencyModel;
        RequiresCacheHierarchyAuthorityModel = requiresCacheHierarchyAuthorityModel;
        RequiresAddressRangeScopeAbi = requiresAddressRangeScopeAbi;
        RequiresDirtyLineOwnershipModel = requiresDirtyLineOwnershipModel;
        RequiresMemoryOrderingIntegration = requiresMemoryOrderingIntegration;
        RequiresIommuMaintenanceAbi = requiresIommuMaintenanceAbi;
        RequiresIotlbInvalidationModel = requiresIotlbInvalidationModel;
        RequiresIommuFenceCompletionModel = requiresIommuFenceCompletionModel;
        RequiresDeviceDomainAuthority = requiresDeviceDomainAuthority;
        RequiresDmaVisibilityModel = requiresDmaVisibilityModel;
        RequiresLane6TokenAuthorityGate = requiresLane6TokenAuthorityGate;
        RequiresExternalDeviceQuiescencePolicy = requiresExternalDeviceQuiescencePolicy;
        IsAcceleratorCapabilityQuery = isAcceleratorCapabilityQuery;
        IsAcceleratorLifecycleControl = isAcceleratorLifecycleControl;
        IsAcceleratorQueueBindingControl = isAcceleratorQueueBindingControl;
        RequiresCapabilityAuthority = requiresCapabilityAuthority;
        RequiresAcceleratorAbiQueryContract = requiresAcceleratorAbiQueryContract;
        RequiresAcceleratorTopologyAbi = requiresAcceleratorTopologyAbi;
        RequiresBoundedCapabilityResultFootprint = requiresBoundedCapabilityResultFootprint;
        RequiresBoundedTopologyResultFootprint = requiresBoundedTopologyResultFootprint;
        RequiresResultScrubbingPolicy = requiresResultScrubbingPolicy;
        RequiresOwnerDomainGuard = requiresOwnerDomainGuard;
        RequiresCommandQueueSemantics = requiresCommandQueueSemantics;
        RequiresRetireOwnedPublication = requiresRetireOwnedPublication;
        RequiresReplayStableCapabilityModel = requiresReplayStableCapabilityModel;
        RequiresMigrationCheckpointPolicy = requiresMigrationCheckpointPolicy;
        RequiresFutureVirtualizationBoundaryPolicy = requiresFutureVirtualizationBoundaryPolicy;
        RequiresBackendCapabilityAuthority = requiresBackendCapabilityAuthority;
        RequiresGuestVisibleCapabilityPolicy = requiresGuestVisibleCapabilityPolicy;
        RequiresAcceleratorRuntimeAuthority = requiresAcceleratorRuntimeAuthority;
        RequiresDeviceAuthority = requiresDeviceAuthority;
        RequiresTokenAuthority = requiresTokenAuthority;
        RequiresHandleNamespaceAbi = requiresHandleNamespaceAbi;
        RequiresOpenCloseLifecycleAbi = requiresOpenCloseLifecycleAbi;
        RequiresReplayStableLifecycleModel = requiresReplayStableLifecycleModel;
        RequiresQueueAuthority = requiresQueueAuthority;
        RequiresBindUnbindQueueAbi = requiresBindUnbindQueueAbi;
        RequiresQueueOwnershipModel = requiresQueueOwnershipModel;
        RequiresReplayStableQueueBindingModel = requiresReplayStableQueueBindingModel;
        RequiresQueueBindUnbindOrderingModel = requiresQueueBindUnbindOrderingModel;
        RequiresReplayStableInvalidationModel = requiresReplayStableInvalidationModel;
        RequiresRetireOwnedSideEffectPublication = requiresRetireOwnedSideEffectPublication;
        RequiresNoHostEvidenceLeak = requiresNoHostEvidenceLeak;
        NoGenericFenceFallback = noGenericFenceFallback;
        NoGenericSystemOpFallback = noGenericSystemOpFallback;
        NoFenceIFallback = noFenceIFallback;
        NoVmxEptVpidNptSemanticAlias = noVmxEptVpidNptSemanticAlias;
        VmxCacheEvidenceIsInsufficient = vmxCacheEvidenceIsInsufficient;
        VmxIommuEvidenceIsInsufficient = vmxIommuEvidenceIsInsufficient;
        VmxCapabilityEvidenceIsInsufficient = vmxCapabilityEvidenceIsInsufficient;
        VmxBackendAuthorityEvidenceIsInsufficient = vmxBackendAuthorityEvidenceIsInsufficient;
        VmxMigrationCheckpointEvidenceIsInsufficient = vmxMigrationCheckpointEvidenceIsInsufficient;
        ExistingLane6DmaEvidenceIsInsufficient = existingLane6DmaEvidenceIsInsufficient;
        ExistingAccelSubmitEvidenceIsInsufficient = existingAccelSubmitEvidenceIsInsufficient;
        ExistingAccelQueryCapsEvidenceIsInsufficient = existingAccelQueryCapsEvidenceIsInsufficient;
        ExistingTopologyQueueTaxonomyEvidenceIsInsufficient = existingTopologyQueueTaxonomyEvidenceIsInsufficient;
        ExistingLane7ControlPlaneEvidenceIsInsufficient = existingLane7ControlPlaneEvidenceIsInsufficient;
        NoLane6DmaFallback = noLane6DmaFallback;
        NoLane7AcceleratorFallback = noLane7AcceleratorFallback;
        NoLane7SubmitFallback = noLane7SubmitFallback;
        NoExternalBackendFallback = noExternalBackendFallback;
        NoCapabilityPublicationBeforeAuthority = noCapabilityPublicationBeforeAuthority;
        NoLifecycleStatePublicationBeforeRetire = noLifecycleStatePublicationBeforeRetire;
        NoBackendAdmissionBeforeAuthority = noBackendAdmissionBeforeAuthority;
        NoQueueBindingPublicationBeforeRetire = noQueueBindingPublicationBeforeRetire;
        NoQueueBindingBeforeTokenAuthority = noQueueBindingBeforeTokenAuthority;
        NoHostEvidenceLeak = noHostEvidenceLeak;
        NoHiddenScalarLowering = noHiddenScalarLowering;
        NoMultiOpEmission = noMultiOpEmission;
    }

    public static CompilerLane7DeferredAbiContract PauseHint { get; } =
        new(
            "PAUSE",
            CompilerLane7DeferredAbiClass.SchedulingHint,
            "ScalarSystemCounter",
            "Lane7HintNoExecutionGuarantee",
            "NoAllocationUntilHintEncodingProgressFairnessNoStateAbi",
            "no operands or approved hint immediate",
            "Scheduling-hint encoding and fairness semantics are not selected.",
            "No architectural state, no register writeback, and no progress guarantee may be inferred.",
            PauseRequiredPolicyDecisions,
            requiresHintEncodingAbi: true,
            requiresProgressFairnessPolicy: true,
            noArchitecturalProgressGuarantee: true,
            requiresNoArchitecturalStateLeakage: true,
            rejectSynchronizationPrimitiveSemantics: true,
            requiresReplayRollbackEvidence: true);

    public static CompilerLane7DeferredAbiContract TranslationFence { get; } =
        new(
            "SFENCE.VMA",
            CompilerLane7DeferredAbiClass.TranslationFence,
            "CacheTlbCoherency",
            "Lane7TranslationFenceDeferred",
            "NoAllocationUntilPrivilegeAddressSpaceTlbShootdownRetirePublicationAbi",
            "rs1(addr), rs2(asid), or future canonical zero-payload subset",
            "Privilege, address-space selector, and TLB shootdown domains are not selected.",
            "Future execution may publish translation-maintenance effects only through retire-owned side-effect ABI.",
            SfenceVmaRequiredPolicyDecisions,
            requiresPrivilegeAndAdmissionPolicy: true,
            requiresAddressSpaceSelectorAbi: true,
            requiresTlbShootdownPolicy: true,
            requiresCrossCoreShootdownPolicy: true,
            requiresTranslationStateOwnershipModel: true,
            requiresPageTableWalkOwnershipModel: true,
            requiresReplayStableInvalidationModel: true,
            requiresRetireOwnedSideEffectPublication: true,
            noGenericFenceFallback: true,
            noVmxEptVpidNptSemanticAlias: true,
            noHostEvidenceLeak: true,
            noHiddenScalarLowering: true,
            noMultiOpEmission: true);

    public static CompilerLane7DeferredAbiContract InstructionCacheInvalidate { get; } =
        new(
            "ICACHE_INVAL",
            CompilerLane7DeferredAbiClass.CacheMaintenance,
            "CacheTlbCoherency",
            "Lane7CacheMaintenanceDeferred",
            "NoAllocationUntilInstructionFetchCoherencyCacheHierarchyScopePrivilegeRetirePublicationAbi",
            "address range scope or future canonical full-instruction-cache subset",
            "Instruction-fetch coherency scope, cache hierarchy authority, and address-range ABI are not selected.",
            "Future execution may publish instruction-cache invalidation only through retire-owned maintenance side-effect ABI.",
            InstructionCacheRequiredPolicyDecisions,
            requiresPrivilegeAndAdmissionPolicy: true,
            requiresCacheMaintenanceAbi: true,
            requiresInstructionFetchCoherencyModel: true,
            requiresCacheHierarchyAuthorityModel: true,
            requiresAddressRangeScopeAbi: true,
            requiresReplayStableInvalidationModel: true,
            requiresRetireOwnedSideEffectPublication: true,
            noGenericFenceFallback: true,
            noFenceIFallback: true,
            vmxCacheEvidenceIsInsufficient: true,
            noHostEvidenceLeak: true,
            noHiddenScalarLowering: true,
            noMultiOpEmission: true);

    public static CompilerLane7DeferredAbiContract DataCacheClean { get; } =
        CreateDataCacheMaintenanceRow(
            "DCACHE_CLEAN",
            "NoAllocationUntilDataCacheCleanDirtyLineScopeOrderingRetirePublicationAbi",
            "Dirty-line ownership, writeback visibility, ordering, and address-range ABI are not selected.",
            "Future execution may publish data-cache clean/writeback effects only through retire-owned maintenance side-effect ABI.");

    public static CompilerLane7DeferredAbiContract DataCacheInvalidate { get; } =
        CreateDataCacheMaintenanceRow(
            "DCACHE_INVAL",
            "NoAllocationUntilDataCacheInvalidateDirtyLineScopeOrderingRetirePublicationAbi",
            "Dirty-line ownership, invalidation scope, ordering, and address-range ABI are not selected.",
            "Future execution may publish data-cache invalidation effects only through retire-owned maintenance side-effect ABI.");

    public static CompilerLane7DeferredAbiContract DataCacheFlush { get; } =
        CreateDataCacheMaintenanceRow(
            "DCACHE_FLUSH",
            "NoAllocationUntilDataCacheFlushDirtyLineScopeOrderingRetirePublicationAbi",
            "Dirty-line ownership, clean+invalidate scope, ordering, and address-range ABI are not selected.",
            "Future execution may publish data-cache flush effects only through retire-owned maintenance side-effect ABI.");

    public static CompilerLane7DeferredAbiContract IotlbInvalidate { get; } =
        new(
            "IOTLB_INV",
            CompilerLane7DeferredAbiClass.IommuMaintenance,
            "CacheTlbCoherency",
            "Lane7IommuMaintenanceDeferred",
            "NoAllocationUntilIotlbDeviceDomainDmaVisibilityLane6TokenQuiescenceRetirePublicationAbi",
            "IOMMU domain descriptor, IOVA range, device scope, or future canonical all-domain subset",
            "IOTLB invalidation scope, device-domain authority, DMA visibility, and Lane6 token authority are not selected.",
            "Future execution may publish IOTLB invalidation effects only through retire-owned IOMMU maintenance side-effect ABI.",
            IotlbRequiredPolicyDecisions,
            requiresPrivilegeAndAdmissionPolicy: true,
            requiresIommuMaintenanceAbi: true,
            requiresIotlbInvalidationModel: true,
            requiresDeviceDomainAuthority: true,
            requiresDmaVisibilityModel: true,
            requiresLane6TokenAuthorityGate: true,
            requiresExternalDeviceQuiescencePolicy: true,
            requiresReplayStableInvalidationModel: true,
            requiresRetireOwnedSideEffectPublication: true,
            noGenericFenceFallback: true,
            noVmxEptVpidNptSemanticAlias: true,
            vmxIommuEvidenceIsInsufficient: true,
            existingLane6DmaEvidenceIsInsufficient: true,
            existingLane7ControlPlaneEvidenceIsInsufficient: true,
            noLane6DmaFallback: true,
            noLane7AcceleratorFallback: true,
            noExternalBackendFallback: true,
            noHostEvidenceLeak: true,
            noHiddenScalarLowering: true,
            noMultiOpEmission: true);

    public static CompilerLane7DeferredAbiContract IommuFence { get; } =
        new(
            "IOMMU_FENCE",
            CompilerLane7DeferredAbiClass.IommuMaintenance,
            "CacheTlbCoherency",
            "Lane7IommuMaintenanceDeferred",
            "NoAllocationUntilIommuFenceCompletionDeviceDomainDmaVisibilityLane6TokenQuiescenceRetirePublicationAbi",
            "IOMMU domain descriptor, completion fence scope, device scope, or future canonical all-domain subset",
            "IOMMU fence completion, device-domain authority, DMA visibility, and Lane6 token authority are not selected.",
            "Future execution may publish IOMMU fence completion effects only through retire-owned IOMMU maintenance side-effect ABI.",
            IommuFenceRequiredPolicyDecisions,
            requiresPrivilegeAndAdmissionPolicy: true,
            requiresIommuMaintenanceAbi: true,
            requiresIommuFenceCompletionModel: true,
            requiresDeviceDomainAuthority: true,
            requiresDmaVisibilityModel: true,
            requiresLane6TokenAuthorityGate: true,
            requiresExternalDeviceQuiescencePolicy: true,
            requiresReplayStableInvalidationModel: true,
            requiresRetireOwnedSideEffectPublication: true,
            noGenericFenceFallback: true,
            noVmxEptVpidNptSemanticAlias: true,
            vmxIommuEvidenceIsInsufficient: true,
            existingLane6DmaEvidenceIsInsufficient: true,
            existingLane7ControlPlaneEvidenceIsInsufficient: true,
            noLane6DmaFallback: true,
            noLane7AcceleratorFallback: true,
            noExternalBackendFallback: true,
            noHostEvidenceLeak: true,
            noHiddenScalarLowering: true,
            noMultiOpEmission: true);

    public static CompilerLane7DeferredAbiContract AcceleratorQueryAbi { get; } =
        CreateAcceleratorCapabilityRow(
            "ACCEL_QUERY_ABI",
            "NoAllocationUntilAcceleratorAbiQueryCapabilityAuthorityResultScrubbingReplayPublicationAbi",
            "Accelerator ABI query contract, capability authority, result footprint, and result scrubbing are not selected.",
            "Future execution may publish accelerator ABI capability results only after capability authority and retire-owned publication.",
            AcceleratorAbiQueryRequiredPolicyDecisions,
            requiresAcceleratorAbiQueryContract: true,
            requiresBoundedCapabilityResultFootprint: true);

    public static CompilerLane7DeferredAbiContract AcceleratorQueryTopology { get; } =
        CreateAcceleratorCapabilityRow(
            "ACCEL_QUERY_TOPOLOGY",
            "NoAllocationUntilAcceleratorTopologyCapabilityAuthorityResultScrubbingReplayPublicationAbi",
            "Accelerator topology ABI, capability authority, result footprint, and result scrubbing are not selected.",
            "Future execution may publish accelerator topology results only after capability authority and retire-owned publication.",
            AcceleratorTopologyRequiredPolicyDecisions,
            requiresAcceleratorTopologyAbi: true,
            requiresBoundedTopologyResultFootprint: true);

    public static CompilerLane7DeferredAbiContract AcceleratorOpen { get; } =
        CreateAcceleratorLifecycleRow(
            "ACCEL_OPEN",
            "NoAllocationUntilAcceleratorOpenRuntimeDeviceTokenHandleLifecycleRetirePublicationAbi",
            "Accelerator runtime authority, device authority, token authority, handle namespace, and open lifecycle ABI are not selected.",
            "Future execution may publish accelerator open lifecycle state only through retire-owned side-effect ABI.");

    public static CompilerLane7DeferredAbiContract AcceleratorClose { get; } =
        CreateAcceleratorLifecycleRow(
            "ACCEL_CLOSE",
            "NoAllocationUntilAcceleratorCloseRuntimeDeviceTokenHandleLifecycleRetirePublicationAbi",
            "Accelerator runtime authority, device authority, token authority, handle namespace, and close lifecycle ABI are not selected.",
            "Future execution may publish accelerator close lifecycle state only through retire-owned side-effect ABI.");

    public static CompilerLane7DeferredAbiContract AcceleratorBindQueue { get; } =
        CreateAcceleratorQueueBindingRow(
            "ACCEL_BIND_QUEUE",
            "NoAllocationUntilAcceleratorBindQueueRuntimeQueueTokenLane6OwnershipRetirePublicationAbi",
            "Accelerator runtime authority, queue authority, token authority, Lane6 token gate, and bind queue ABI are not selected.",
            "Future execution may publish accelerator queue binding state only through retire-owned side-effect ABI.");

    public static CompilerLane7DeferredAbiContract AcceleratorUnbindQueue { get; } =
        CreateAcceleratorQueueBindingRow(
            "ACCEL_UNBIND_QUEUE",
            "NoAllocationUntilAcceleratorUnbindQueueRuntimeQueueTokenLane6OwnershipRetirePublicationAbi",
            "Accelerator runtime authority, queue authority, token authority, Lane6 token gate, and unbind queue ABI are not selected.",
            "Future execution may publish accelerator queue unbinding state only through retire-owned side-effect ABI.");

    public static IReadOnlyList<CompilerLane7DeferredAbiContract> AllDeferredLane7Rows { get; } =
    [
        PauseHint,
        TranslationFence,
        InstructionCacheInvalidate,
        DataCacheClean,
        DataCacheInvalidate,
        DataCacheFlush,
        IotlbInvalidate,
        IommuFence,
        AcceleratorQueryAbi,
        AcceleratorQueryTopology,
        AcceleratorOpen,
        AcceleratorClose,
        AcceleratorBindQueue,
        AcceleratorUnbindQueue
    ];

    public static IReadOnlyList<CompilerLane7DeferredAbiContract> CacheMaintenanceRows { get; } =
    [
        InstructionCacheInvalidate,
        DataCacheClean,
        DataCacheInvalidate,
        DataCacheFlush
    ];

    public static IReadOnlyList<CompilerLane7DeferredAbiContract> IommuMaintenanceRows { get; } =
    [
        IotlbInvalidate,
        IommuFence
    ];

    public static IReadOnlyList<CompilerLane7DeferredAbiContract> AcceleratorControlRows { get; } =
    [
        AcceleratorQueryAbi,
        AcceleratorQueryTopology,
        AcceleratorOpen,
        AcceleratorClose,
        AcceleratorBindQueue,
        AcceleratorUnbindQueue
    ];

    private static CompilerLane7DeferredAbiContract CreateDataCacheMaintenanceRow(
        string mnemonic,
        string abiDecision,
        string dataSemantics,
        string resultSemantics) =>
        new(
            mnemonic,
            CompilerLane7DeferredAbiClass.CacheMaintenance,
            "CacheTlbCoherency",
            "Lane7CacheMaintenanceDeferred",
            abiDecision,
            "address range scope or future canonical full-data-cache subset",
            dataSemantics,
            resultSemantics,
            DataCacheRequiredPolicyDecisions,
            requiresPrivilegeAndAdmissionPolicy: true,
            requiresCacheMaintenanceAbi: true,
            requiresDataCacheCoherencyModel: true,
            requiresCacheHierarchyAuthorityModel: true,
            requiresAddressRangeScopeAbi: true,
            requiresDirtyLineOwnershipModel: true,
            requiresMemoryOrderingIntegration: true,
            requiresReplayStableInvalidationModel: true,
            requiresRetireOwnedSideEffectPublication: true,
            noGenericFenceFallback: true,
            noFenceIFallback: true,
            vmxCacheEvidenceIsInsufficient: true,
            noHostEvidenceLeak: true,
            noHiddenScalarLowering: true,
            noMultiOpEmission: true);

    private static CompilerLane7DeferredAbiContract CreateAcceleratorCapabilityRow(
        string mnemonic,
        string abiDecision,
        string dataSemantics,
        string resultSemantics,
        IReadOnlyList<string> requiredPolicyDecisions,
        bool requiresAcceleratorAbiQueryContract = false,
        bool requiresAcceleratorTopologyAbi = false,
        bool requiresBoundedCapabilityResultFootprint = false,
        bool requiresBoundedTopologyResultFootprint = false) =>
        new(
            mnemonic,
            CompilerLane7DeferredAbiClass.AcceleratorCapability,
            "Lane7TopologyQueue",
            "Lane7AcceleratorControlDeferred",
            abiDecision,
            "rd plus future accelerator capability selector or canonical zero-payload subset",
            dataSemantics,
            resultSemantics,
            requiredPolicyDecisions,
            isAcceleratorCapabilityQuery: true,
            requiresCapabilityAuthority: true,
            requiresAcceleratorAbiQueryContract: requiresAcceleratorAbiQueryContract,
            requiresAcceleratorTopologyAbi: requiresAcceleratorTopologyAbi,
            requiresBoundedCapabilityResultFootprint: requiresBoundedCapabilityResultFootprint,
            requiresBoundedTopologyResultFootprint: requiresBoundedTopologyResultFootprint,
            requiresResultScrubbingPolicy: true,
            requiresOwnerDomainGuard: true,
            requiresCommandQueueSemantics: true,
            requiresRetireOwnedPublication: true,
            requiresReplayStableCapabilityModel: true,
            requiresMigrationCheckpointPolicy: true,
            requiresFutureVirtualizationBoundaryPolicy: true,
            requiresBackendCapabilityAuthority: true,
            requiresGuestVisibleCapabilityPolicy: true,
            requiresNoHostEvidenceLeak: true,
            noGenericSystemOpFallback: true,
            vmxCapabilityEvidenceIsInsufficient: true,
            vmxMigrationCheckpointEvidenceIsInsufficient: true,
            existingAccelSubmitEvidenceIsInsufficient: true,
            existingAccelQueryCapsEvidenceIsInsufficient: true,
            existingTopologyQueueTaxonomyEvidenceIsInsufficient: true,
            existingLane7ControlPlaneEvidenceIsInsufficient: true,
            noLane6DmaFallback: true,
            noLane7SubmitFallback: true,
            noExternalBackendFallback: true,
            noCapabilityPublicationBeforeAuthority: true,
            noHostEvidenceLeak: true,
            noHiddenScalarLowering: true,
            noMultiOpEmission: true);

    private static CompilerLane7DeferredAbiContract CreateAcceleratorLifecycleRow(
        string mnemonic,
        string abiDecision,
        string dataSemantics,
        string resultSemantics) =>
        new(
            mnemonic,
            CompilerLane7DeferredAbiClass.AcceleratorLifecycle,
            "Lane7TopologyQueue",
            "Lane7AcceleratorControlDeferred",
            abiDecision,
            "accelerator handle, token, or future canonical lifecycle descriptor",
            dataSemantics,
            resultSemantics,
            AcceleratorLifecycleRequiredPolicyDecisions,
            isAcceleratorLifecycleControl: true,
            requiresAcceleratorRuntimeAuthority: true,
            requiresDeviceAuthority: true,
            requiresTokenAuthority: true,
            requiresOwnerDomainGuard: true,
            requiresHandleNamespaceAbi: true,
            requiresOpenCloseLifecycleAbi: true,
            requiresCommandQueueSemantics: true,
            requiresReplayStableLifecycleModel: true,
            requiresMigrationCheckpointPolicy: true,
            requiresFutureVirtualizationBoundaryPolicy: true,
            requiresNoHostEvidenceLeak: true,
            requiresRetireOwnedSideEffectPublication: true,
            noGenericSystemOpFallback: true,
            vmxBackendAuthorityEvidenceIsInsufficient: true,
            vmxMigrationCheckpointEvidenceIsInsufficient: true,
            existingAccelSubmitEvidenceIsInsufficient: true,
            existingAccelQueryCapsEvidenceIsInsufficient: true,
            existingTopologyQueueTaxonomyEvidenceIsInsufficient: true,
            existingLane7ControlPlaneEvidenceIsInsufficient: true,
            noLane6DmaFallback: true,
            noLane7SubmitFallback: true,
            noExternalBackendFallback: true,
            noLifecycleStatePublicationBeforeRetire: true,
            noBackendAdmissionBeforeAuthority: true,
            noHostEvidenceLeak: true,
            noHiddenScalarLowering: true,
            noMultiOpEmission: true);

    private static CompilerLane7DeferredAbiContract CreateAcceleratorQueueBindingRow(
        string mnemonic,
        string abiDecision,
        string dataSemantics,
        string resultSemantics) =>
        new(
            mnemonic,
            CompilerLane7DeferredAbiClass.AcceleratorQueueBinding,
            "Lane7TopologyQueue",
            "Lane7AcceleratorControlDeferred",
            abiDecision,
            "accelerator handle, queue token, Lane6 token, or future canonical queue descriptor",
            dataSemantics,
            resultSemantics,
            AcceleratorQueueBindingRequiredPolicyDecisions,
            requiresLane6TokenAuthorityGate: true,
            isAcceleratorQueueBindingControl: true,
            requiresAcceleratorRuntimeAuthority: true,
            requiresQueueAuthority: true,
            requiresTokenAuthority: true,
            requiresOwnerDomainGuard: true,
            requiresBindUnbindQueueAbi: true,
            requiresQueueOwnershipModel: true,
            requiresCommandQueueSemantics: true,
            requiresReplayStableQueueBindingModel: true,
            requiresQueueBindUnbindOrderingModel: true,
            requiresMigrationCheckpointPolicy: true,
            requiresFutureVirtualizationBoundaryPolicy: true,
            requiresNoHostEvidenceLeak: true,
            requiresRetireOwnedSideEffectPublication: true,
            noGenericSystemOpFallback: true,
            vmxBackendAuthorityEvidenceIsInsufficient: true,
            vmxMigrationCheckpointEvidenceIsInsufficient: true,
            existingLane6DmaEvidenceIsInsufficient: true,
            existingAccelSubmitEvidenceIsInsufficient: true,
            existingAccelQueryCapsEvidenceIsInsufficient: true,
            existingTopologyQueueTaxonomyEvidenceIsInsufficient: true,
            existingLane7ControlPlaneEvidenceIsInsufficient: true,
            noLane6DmaFallback: true,
            noLane7SubmitFallback: true,
            noExternalBackendFallback: true,
            noQueueBindingPublicationBeforeRetire: true,
            noQueueBindingBeforeTokenAuthority: true,
            noHostEvidenceLeak: true,
            noHiddenScalarLowering: true,
            noMultiOpEmission: true);

    public string Mnemonic { get; }
    public CompilerLane7DeferredAbiClass AbiClass { get; }
    public string ExtensionName { get; }
    public string EvidenceBoundary { get; }
    public string AbiDecision { get; }
    public string OperandShape { get; }
    public string DataSemantics { get; }
    public string ResultSemantics { get; }
    public IReadOnlyList<string> RequiredPolicyDecisions { get; }
    public bool RequiresHintEncodingAbi { get; }
    public bool RequiresProgressFairnessPolicy { get; }
    public bool NoArchitecturalProgressGuarantee { get; }
    public bool RequiresNoArchitecturalStateLeakage { get; }
    public bool RejectSynchronizationPrimitiveSemantics { get; }
    public bool RequiresReplayRollbackEvidence { get; }
    public bool RequiresPrivilegeAndAdmissionPolicy { get; }
    public bool RequiresAddressSpaceSelectorAbi { get; }
    public bool RequiresTlbShootdownPolicy { get; }
    public bool RequiresCrossCoreShootdownPolicy { get; }
    public bool RequiresTranslationStateOwnershipModel { get; }
    public bool RequiresPageTableWalkOwnershipModel { get; }
    public bool RequiresCacheMaintenanceAbi { get; }
    public bool RequiresInstructionFetchCoherencyModel { get; }
    public bool RequiresDataCacheCoherencyModel { get; }
    public bool RequiresCacheHierarchyAuthorityModel { get; }
    public bool RequiresAddressRangeScopeAbi { get; }
    public bool RequiresDirtyLineOwnershipModel { get; }
    public bool RequiresMemoryOrderingIntegration { get; }
    public bool RequiresIommuMaintenanceAbi { get; }
    public bool RequiresIotlbInvalidationModel { get; }
    public bool RequiresIommuFenceCompletionModel { get; }
    public bool RequiresDeviceDomainAuthority { get; }
    public bool RequiresDmaVisibilityModel { get; }
    public bool RequiresLane6TokenAuthorityGate { get; }
    public bool RequiresExternalDeviceQuiescencePolicy { get; }
    public bool IsAcceleratorCapabilityQuery { get; }
    public bool IsAcceleratorLifecycleControl { get; }
    public bool IsAcceleratorQueueBindingControl { get; }
    public bool RequiresCapabilityAuthority { get; }
    public bool RequiresAcceleratorAbiQueryContract { get; }
    public bool RequiresAcceleratorTopologyAbi { get; }
    public bool RequiresBoundedCapabilityResultFootprint { get; }
    public bool RequiresBoundedTopologyResultFootprint { get; }
    public bool RequiresResultScrubbingPolicy { get; }
    public bool RequiresOwnerDomainGuard { get; }
    public bool RequiresCommandQueueSemantics { get; }
    public bool RequiresRetireOwnedPublication { get; }
    public bool RequiresReplayStableCapabilityModel { get; }
    public bool RequiresMigrationCheckpointPolicy { get; }
    public bool RequiresFutureVirtualizationBoundaryPolicy { get; }
    public bool RequiresBackendCapabilityAuthority { get; }
    public bool RequiresGuestVisibleCapabilityPolicy { get; }
    public bool RequiresAcceleratorRuntimeAuthority { get; }
    public bool RequiresDeviceAuthority { get; }
    public bool RequiresTokenAuthority { get; }
    public bool RequiresHandleNamespaceAbi { get; }
    public bool RequiresOpenCloseLifecycleAbi { get; }
    public bool RequiresReplayStableLifecycleModel { get; }
    public bool RequiresQueueAuthority { get; }
    public bool RequiresBindUnbindQueueAbi { get; }
    public bool RequiresQueueOwnershipModel { get; }
    public bool RequiresReplayStableQueueBindingModel { get; }
    public bool RequiresQueueBindUnbindOrderingModel { get; }
    public bool RequiresReplayStableInvalidationModel { get; }
    public bool RequiresRetireOwnedSideEffectPublication { get; }
    public bool RequiresNoHostEvidenceLeak { get; }
    public bool NoGenericFenceFallback { get; }
    public bool NoGenericSystemOpFallback { get; }
    public bool NoFenceIFallback { get; }
    public bool NoVmxEptVpidNptSemanticAlias { get; }
    public bool VmxCacheEvidenceIsInsufficient { get; }
    public bool VmxIommuEvidenceIsInsufficient { get; }
    public bool VmxCapabilityEvidenceIsInsufficient { get; }
    public bool VmxBackendAuthorityEvidenceIsInsufficient { get; }
    public bool VmxMigrationCheckpointEvidenceIsInsufficient { get; }
    public bool ExistingLane6DmaEvidenceIsInsufficient { get; }
    public bool ExistingAccelSubmitEvidenceIsInsufficient { get; }
    public bool ExistingAccelQueryCapsEvidenceIsInsufficient { get; }
    public bool ExistingTopologyQueueTaxonomyEvidenceIsInsufficient { get; }
    public bool ExistingLane7ControlPlaneEvidenceIsInsufficient { get; }
    public bool NoLane6DmaFallback { get; }
    public bool NoLane7AcceleratorFallback { get; }
    public bool NoLane7SubmitFallback { get; }
    public bool NoExternalBackendFallback { get; }
    public bool NoCapabilityPublicationBeforeAuthority { get; }
    public bool NoLifecycleStatePublicationBeforeRetire { get; }
    public bool NoBackendAdmissionBeforeAuthority { get; }
    public bool NoQueueBindingPublicationBeforeRetire { get; }
    public bool NoQueueBindingBeforeTokenAuthority { get; }
    public bool NoHostEvidenceLeak { get; }
    public bool NoHiddenScalarLowering { get; }
    public bool NoMultiOpEmission { get; }
    public bool HasOpcodeAllocation => false;
    public bool IsExecutable => false;
    public bool CompilerEmissionAllowed => false;

    public void RequireCompilerEmissionAuthority()
    {
        string requiredDecisions = AbiClass switch
        {
            CompilerLane7DeferredAbiClass.SchedulingHint =>
                "hint encoding, progress, fairness, replay, and no-state-leakage ABI decisions",
            CompilerLane7DeferredAbiClass.TranslationFence =>
                "privilege, address-space selector, TLB shootdown, replay, and retire-owned publication ABI decisions",
            CompilerLane7DeferredAbiClass.CacheMaintenance =>
                "cache hierarchy, address-range scope, coherency, replay, and retire-owned publication ABI decisions",
            CompilerLane7DeferredAbiClass.IommuMaintenance =>
                "IOMMU maintenance, device-domain authority, DMA visibility, Lane6 token, quiescence, replay, and retire-owned publication ABI decisions",
            CompilerLane7DeferredAbiClass.AcceleratorCapability =>
                "accelerator capability authority, result footprint, scrubbing, replay, and retire-owned publication ABI decisions",
            CompilerLane7DeferredAbiClass.AcceleratorLifecycle =>
                "accelerator runtime, device, token, handle namespace, lifecycle, replay, and retire-owned publication ABI decisions",
            CompilerLane7DeferredAbiClass.AcceleratorQueueBinding =>
                "accelerator runtime, queue, token, Lane6 token, ownership, ordering, replay, and retire-owned publication ABI decisions",
            _ => "required Lane7 ABI decisions"
        };

        throw new InvalidOperationException(
            $"{Mnemonic} compiler emission is blocked until {requiredDecisions} are explicit.");
    }
}
