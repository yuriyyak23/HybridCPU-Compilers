namespace CpuInterfaceBridge;

/// <summary>
/// Thread-safe adapter that reads ISE state through an injected observation service.
/// </summary>
public sealed class IseCoreStateService : ICoreStateService
{
    private readonly HybridCPU_ISE.IseObservationService _observationService;

    public IseCoreStateService(HybridCPU_ISE.IseObservationService observationService)
    {
        _observationService = observationService ?? throw new ArgumentNullException(nameof(observationService));
    }

    /// <inheritdoc />
    public CoreStateSnapshot GetCoreState(int coreId)
    {
        HybridCPU_ISE.CoreStateSnapshot source = _observationService.GetCoreState(coreId);
        return Map(source);
    }

    /// <inheritdoc />
    public byte[] ReadMemory(ulong address, int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Length must be non-negative.");
        }

        return _observationService.ReadMemory(address, length);
    }

    /// <inheritdoc />
    public int GetTotalCores()
    {
        return _observationService.GetTotalCores();
    }

    internal static CoreStateSnapshot Map(HybridCPU_ISE.CoreStateSnapshot source)
    {
        return new CoreStateSnapshot
        {
            CoreId = source.CoreId,
            LiveInstructionPointer = source.LiveInstructionPointer,
            CycleCount = source.CycleCount,
            PipelineInstructionsRetired = source.PipelineInstructionsRetired,
            HasExceptions = source.HasExceptions,
            PipelineIPC = source.PipelineIPC,
            PipelineEfficiency = source.PipelineEfficiency,
            PipelineStallCycles = source.PipelineStallCycles,
            PipelineDataHazards = source.PipelineDataHazards,
            PipelineMemoryStalls = source.PipelineMemoryStalls,
            CurrentState = source.CurrentState,
            CurrentPowerState = source.CurrentPowerState.ToString(),
            CurrentPerformanceLevel = source.CurrentPerformanceLevel,
            IsStalled = source.IsStalled,
            ActiveVirtualThreadId = source.ActiveVirtualThreadId,
            VirtualThreadLivePcs = source.VirtualThreadLivePcs ?? Array.Empty<ulong>(),
            VirtualThreadCommittedPcs = source.VirtualThreadCommittedPcs ?? Array.Empty<ulong>(),
            ActiveVirtualThreadRegisters = source.ActiveVirtualThreadRegisters ?? Array.Empty<ulong>(),
            DecodedBundleStateOwnerKind = (DecodedBundleStateOwnerKind)source.DecodedBundleStateOwnerKind,
            DecodedBundleStateEpoch = source.DecodedBundleStateEpoch,
            DecodedBundleStateVersion = source.DecodedBundleStateVersion,
            DecodedBundleStateKind = (DecodedBundleStateKind)source.DecodedBundleStateKind,
            DecodedBundleStateOrigin = (DecodedBundleStateOrigin)source.DecodedBundleStateOrigin,
            DecodedBundlePc = source.DecodedBundlePc,
            DecodedBundleValidMask = source.DecodedBundleValidMask,
            DecodedBundleNopMask = source.DecodedBundleNopMask,
            DecodedBundleHasCanonicalDecode = source.DecodedBundleHasCanonicalDecode,
            DecodedBundleHasCanonicalLegality = source.DecodedBundleHasCanonicalLegality,
            DecodedBundleHasDecodeFault = source.DecodedBundleHasDecodeFault,
            DecodePublicationCertificate = Map(source.DecodePublicationCertificate),
            ExecuteCompletionCertificate = Map(source.ExecuteCompletionCertificate),
            RetireVisibilityCertificate = Map(source.RetireVisibilityCertificate)
        };
    }

    private static PipelineContourCertificate Map(
        YAKSys_Hybrid_CPU.Core.PipelineContourCertificate source)
    {
        return new PipelineContourCertificate
        {
            Kind = (PipelineContourKind)source.Kind,
            Owner = (PipelineContourOwner)source.Owner,
            VisibilityStage = (PipelineContourVisibilityStage)source.VisibilityStage,
            Pc = source.Pc,
            SlotMask = source.SlotMask,
            IsPublished = source.IsPublished
        };
    }
}
