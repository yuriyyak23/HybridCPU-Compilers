using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR;

public enum CompilerLane7CacheTlbIommuAbiClass : byte
{
    TranslationFence = 0,
    InstructionCacheMaintenance = 1,
    DataCacheMaintenance = 2,
    IotlbMaintenance = 3,
    IommuFenceMaintenance = 4
}

/// <summary>
/// Compiler-visible no-emission ABI audit for Lane7 cache, TLB, and IOMMU rows.
/// </summary>
public sealed class CompilerLane7CacheTlbIommuAbiContract
{
    private static readonly string[] RequiredCompilerEvidenceGates =
    [
        "ExplicitCompilerHelperAbi",
        "PublishedOpcodeEncodingAbi",
        "MetadataCapabilityAuthority",
        "RuntimeOwnedLegalityEvidence",
        "RetireReplayGoldenConformance",
        "NoFallbackBoundary"
    ];

    private CompilerLane7CacheTlbIommuAbiContract(
        CompilerLane7DeferredAbiContract deferredContract,
        CompilerLane7CacheTlbIommuAbiClass abiClass,
        string helperBoundary,
        bool requiresTranslationFenceAbi = false,
        bool requiresCacheMaintenanceAbi = false,
        bool requiresInstructionFetchCoherencyModel = false,
        bool requiresDataCacheCoherencyModel = false,
        bool requiresIommuMaintenanceAbi = false,
        bool requiresIotlbInvalidationModel = false,
        bool requiresIommuFenceCompletionModel = false)
    {
        DeferredContract = deferredContract;
        AbiClass = abiClass;
        HelperBoundary = helperBoundary;
        RequiresTranslationFenceAbi = requiresTranslationFenceAbi;
        RequiresCacheMaintenanceAbi = requiresCacheMaintenanceAbi;
        RequiresInstructionFetchCoherencyModel = requiresInstructionFetchCoherencyModel;
        RequiresDataCacheCoherencyModel = requiresDataCacheCoherencyModel;
        RequiresIommuMaintenanceAbi = requiresIommuMaintenanceAbi;
        RequiresIotlbInvalidationModel = requiresIotlbInvalidationModel;
        RequiresIommuFenceCompletionModel = requiresIommuFenceCompletionModel;
    }

    public static CompilerLane7CacheTlbIommuAbiContract TranslationFence { get; } =
        new(
            CompilerLane7DeferredAbiContract.TranslationFence,
            CompilerLane7CacheTlbIommuAbiClass.TranslationFence,
            "NoCompilerHelperUntilPrivilegeAddressSpaceTlbShootdownReplayRetireAbi",
            requiresTranslationFenceAbi: true);

    public static CompilerLane7CacheTlbIommuAbiContract InstructionCacheInvalidate { get; } =
        new(
            CompilerLane7DeferredAbiContract.InstructionCacheInvalidate,
            CompilerLane7CacheTlbIommuAbiClass.InstructionCacheMaintenance,
            "NoCompilerHelperUntilInstructionFetchCoherencyScopeReplayRetireAbi",
            requiresCacheMaintenanceAbi: true,
            requiresInstructionFetchCoherencyModel: true);

    public static CompilerLane7CacheTlbIommuAbiContract DataCacheClean { get; } =
        CreateDataCacheMaintenanceRow(
            CompilerLane7DeferredAbiContract.DataCacheClean,
            "NoCompilerHelperUntilDataCacheCleanDirtyLineOrderingReplayRetireAbi");

    public static CompilerLane7CacheTlbIommuAbiContract DataCacheInvalidate { get; } =
        CreateDataCacheMaintenanceRow(
            CompilerLane7DeferredAbiContract.DataCacheInvalidate,
            "NoCompilerHelperUntilDataCacheInvalidateDirtyLineOrderingReplayRetireAbi");

    public static CompilerLane7CacheTlbIommuAbiContract DataCacheFlush { get; } =
        CreateDataCacheMaintenanceRow(
            CompilerLane7DeferredAbiContract.DataCacheFlush,
            "NoCompilerHelperUntilDataCacheFlushDirtyLineOrderingReplayRetireAbi");

    public static CompilerLane7CacheTlbIommuAbiContract IotlbInvalidate { get; } =
        new(
            CompilerLane7DeferredAbiContract.IotlbInvalidate,
            CompilerLane7CacheTlbIommuAbiClass.IotlbMaintenance,
            "NoCompilerHelperUntilIotlbDeviceDomainDmaVisibilityLane6TokenQuiescenceReplayRetireAbi",
            requiresIommuMaintenanceAbi: true,
            requiresIotlbInvalidationModel: true);

    public static CompilerLane7CacheTlbIommuAbiContract IommuFence { get; } =
        new(
            CompilerLane7DeferredAbiContract.IommuFence,
            CompilerLane7CacheTlbIommuAbiClass.IommuFenceMaintenance,
            "NoCompilerHelperUntilIommuFenceCompletionDeviceDomainDmaVisibilityLane6TokenQuiescenceReplayRetireAbi",
            requiresIommuMaintenanceAbi: true,
            requiresIommuFenceCompletionModel: true);

    public static IReadOnlyList<CompilerLane7CacheTlbIommuAbiContract> AllCacheTlbIommuRows { get; } =
    [
        TranslationFence,
        InstructionCacheInvalidate,
        DataCacheClean,
        DataCacheInvalidate,
        DataCacheFlush,
        IotlbInvalidate,
        IommuFence
    ];

    public CompilerLane7DeferredAbiContract DeferredContract { get; }
    public CompilerLane7CacheTlbIommuAbiClass AbiClass { get; }
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
    public bool RequiresTranslationFenceAbi { get; }
    public bool RequiresCacheMaintenanceAbi { get; }
    public bool RequiresInstructionFetchCoherencyModel { get; }
    public bool RequiresDataCacheCoherencyModel { get; }
    public bool RequiresIommuMaintenanceAbi { get; }
    public bool RequiresIotlbInvalidationModel { get; }
    public bool RequiresIommuFenceCompletionModel { get; }
    public bool RequiresExplicitCompilerHelperAbi => true;
    public bool RequiresPublishedOpcodeEncodingAbi => true;
    public bool RequiresMetadataCapabilityAuthority => true;
    public bool RequiresRuntimeOwnedLegalityEvidence => true;
    public bool RequiresRetireReplayGoldenConformance => true;
    public bool RequiresPrivilegeAndAdmissionPolicy => DeferredContract.RequiresPrivilegeAndAdmissionPolicy;
    public bool RequiresAddressSpaceSelectorAbi => DeferredContract.RequiresAddressSpaceSelectorAbi;
    public bool RequiresTlbShootdownPolicy => DeferredContract.RequiresTlbShootdownPolicy;
    public bool RequiresCrossCoreShootdownPolicy => DeferredContract.RequiresCrossCoreShootdownPolicy;
    public bool RequiresTranslationStateOwnershipModel => DeferredContract.RequiresTranslationStateOwnershipModel;
    public bool RequiresPageTableWalkOwnershipModel => DeferredContract.RequiresPageTableWalkOwnershipModel;
    public bool RequiresCacheHierarchyAuthorityModel => DeferredContract.RequiresCacheHierarchyAuthorityModel;
    public bool RequiresAddressRangeScopeAbi => DeferredContract.RequiresAddressRangeScopeAbi;
    public bool RequiresDirtyLineOwnershipModel => DeferredContract.RequiresDirtyLineOwnershipModel;
    public bool RequiresMemoryOrderingIntegration => DeferredContract.RequiresMemoryOrderingIntegration;
    public bool RequiresDeviceDomainAuthority => DeferredContract.RequiresDeviceDomainAuthority;
    public bool RequiresDmaVisibilityModel => DeferredContract.RequiresDmaVisibilityModel;
    public bool RequiresLane6TokenAuthorityGate => DeferredContract.RequiresLane6TokenAuthorityGate;
    public bool RequiresExternalDeviceQuiescencePolicy => DeferredContract.RequiresExternalDeviceQuiescencePolicy;
    public bool RequiresReplayStableInvalidationModel => DeferredContract.RequiresReplayStableInvalidationModel;
    public bool RequiresRetireOwnedSideEffectPublication => DeferredContract.RequiresRetireOwnedSideEffectPublication;
    public bool NoGenericFenceFallback => DeferredContract.NoGenericFenceFallback;
    public bool NoFenceIFallback => DeferredContract.NoFenceIFallback;
    public bool NoVmxEptVpidNptSemanticAlias => DeferredContract.NoVmxEptVpidNptSemanticAlias;
    public bool VmxCacheEvidenceIsInsufficient => DeferredContract.VmxCacheEvidenceIsInsufficient;
    public bool VmxIommuEvidenceIsInsufficient => DeferredContract.VmxIommuEvidenceIsInsufficient;
    public bool ExistingLane6DmaEvidenceIsInsufficient => DeferredContract.ExistingLane6DmaEvidenceIsInsufficient;
    public bool ExistingLane7ControlPlaneEvidenceIsInsufficient => DeferredContract.ExistingLane7ControlPlaneEvidenceIsInsufficient;
    public bool NoLane6DmaFallback => DeferredContract.NoLane6DmaFallback;
    public bool NoLane7AcceleratorFallback => DeferredContract.NoLane7AcceleratorFallback;
    public bool NoExternalBackendFallback => DeferredContract.NoExternalBackendFallback;
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
            CompilerLane7CacheTlbIommuAbiClass.TranslationFence =>
                "privilege, address-space selector, TLB shootdown, replay, retire-owned publication, and no VMX/EPT/VPID/NPT alias ABI decisions",
            CompilerLane7CacheTlbIommuAbiClass.InstructionCacheMaintenance =>
                "instruction-fetch coherency, cache hierarchy authority, address-range scope, replay, retire-owned publication, and no FENCE.I fallback ABI decisions",
            CompilerLane7CacheTlbIommuAbiClass.DataCacheMaintenance =>
                "data-cache coherency, dirty-line ownership, memory ordering, cache hierarchy authority, address-range scope, replay, and retire-owned publication ABI decisions",
            CompilerLane7CacheTlbIommuAbiClass.IotlbMaintenance =>
                "IOTLB invalidation, device-domain authority, DMA visibility, Lane6 token, quiescence, replay, retire-owned publication, and no VMX/EPT/VPID/NPT alias ABI decisions",
            CompilerLane7CacheTlbIommuAbiClass.IommuFenceMaintenance =>
                "IOMMU fence completion, device-domain authority, DMA visibility, Lane6 token, quiescence, replay, retire-owned publication, and no VMX/EPT/VPID/NPT alias ABI decisions",
            _ => "required Lane7 cache/TLB/IOMMU ABI decisions"
        };

        throw new InvalidOperationException(
            $"{Mnemonic} compiler emission is blocked until {requiredDecisions} are explicit.");
    }

    private static CompilerLane7CacheTlbIommuAbiContract CreateDataCacheMaintenanceRow(
        CompilerLane7DeferredAbiContract deferredContract,
        string helperBoundary) =>
        new(
            deferredContract,
            CompilerLane7CacheTlbIommuAbiClass.DataCacheMaintenance,
            helperBoundary,
            requiresCacheMaintenanceAbi: true,
            requiresDataCacheCoherencyModel: true);
}
