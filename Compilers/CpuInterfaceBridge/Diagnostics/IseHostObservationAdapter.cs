using HybridCPU_ISE;
using HybridCPU_ISE.Machine;

namespace CpuInterfaceBridge.Diagnostics;

public static class IseHostObservationAdapter
{
    /// <summary>Call inside an existing host with its observer; does not instantiate any global ISE source.</summary>
    public static HostObservationEndpoint Create(string hostName, IseObservationService observation, int maximumConnections = 8)
    {
        ArgumentNullException.ThrowIfNull(observation);
        HostObservationSourceKind source = observation.SourceProvenance switch
        {
            MachineStateSourceProvenance.LiveCore => HostObservationSourceKind.LiveCore,
            MachineStateSourceProvenance.Snapshot => HostObservationSourceKind.Snapshot,
            MachineStateSourceProvenance.LegacyGlobal => HostObservationSourceKind.LegacyGlobal,
            _ => HostObservationSourceKind.Unavailable
        };
        IseCoreStateService bridge = new IseCoreStateService(observation);
        return new(hostName, $"IseObservationService/{observation.SourceProvenance}", source, bridge.GetCoreState, maximumConnections);
    }
}
