using System.Diagnostics;

namespace CpuInterfaceBridge.Diagnostics;

public enum HostObservationSourceKind { Unknown, LiveCore, Snapshot, LegacyGlobal, Unavailable }
public enum HostConnectionState { Connected, Detached, HostClosed }
public enum HostCaptureStatus { Observed, ProviderFault, InvalidSnapshot }

/// <summary>Host instance identity, never a guest image/run identity or an IPC credential.</summary>
public sealed record HostInstanceIdentity(Guid InstanceId, int ProcessId, DateTimeOffset ProcessStartedAtUtc, string Name);
public sealed record HostObservationDescriptor(HostInstanceIdentity Identity, string Source, HostObservationSourceKind SourceKind)
{
    public const string CurrentSchema = "hybridcpu.bridge.host-observation/v1";
    public string Schema => CurrentSchema;
    public string Transport => "ExplicitInProcessReference";
    public bool SupportsRemoteAttach => false;
    public bool SupportsExecutionControl => false;
    public bool SupportsMemoryReads => false;
    public string Consistency => "ProviderSnapshot; not a guest stop or complete retire trace";
}
public sealed record HostObservationFrame(Guid ConnectionId, HostInstanceIdentity Host, DateTimeOffset CapturedAtUtc,
    string Source, HostObservationSourceKind SourceKind, CoreStateSnapshot Snapshot)
{
    public Evidence<string> GuestRunIdentity => Evidence<string>.Missing("No runtime-owned run binding supplied by observation API");
    public Evidence<string> ImageIdentity => Evidence<string>.Missing("Core observations do not bind an HCEXE image");
    public Evidence<string> Qualification => Evidence<string>.Missing("Observation is not qualification evidence");
}
public sealed record HostCaptureResult(HostCaptureStatus Status, HostObservationFrame? Frame, string Reason);

/// <summary>
/// Explicit host-owned endpoint. Construct inside the host with its existing read-only observer.
/// No process discovery, remote attach, CPU commands, background polling or implicit captures.
/// </summary>
public sealed class HostObservationEndpoint : IDisposable
{
    private readonly object gate = new();
    private readonly Func<int, CoreStateSnapshot> readCore;
    private readonly HashSet<HostObservationConnection> connections = [];
    private readonly int maximumConnections;
    private string? closedReason;
    public HostObservationDescriptor Descriptor { get; }

    public HostObservationEndpoint(string name, string source, HostObservationSourceKind sourceKind,
        Func<int, CoreStateSnapshot> readCore, int maximumConnections = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(readCore);
        if (!Enum.IsDefined(sourceKind) || sourceKind is HostObservationSourceKind.Unavailable or HostObservationSourceKind.Unknown)
            throw new ArgumentException("An available, explicit observation provenance is required.", nameof(sourceKind));
        if (maximumConnections is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maximumConnections));
        using Process process = Process.GetCurrentProcess();
        Descriptor = new(new(Guid.NewGuid(), Environment.ProcessId, process.StartTime.ToUniversalTime(), name), source, sourceKind);
        this.readCore = readCore;
        this.maximumConnections = maximumConnections;
    }

    public HostObservationConnection Connect(HostInstanceIdentity expectedHost, string expectedSchema = HostObservationDescriptor.CurrentSchema)
    {
        lock (gate)
        {
            if (closedReason is not null) throw new InvalidOperationException(closedReason);
            if (expectedSchema != Descriptor.Schema) throw new NotSupportedException("Host observation schema mismatch.");
            if (expectedHost != Descriptor.Identity) throw new InvalidOperationException("Host identity mismatch; PID alone does not identify a host instance.");
            if (connections.Count >= maximumConnections) throw new InvalidOperationException("Host connection capacity exceeded.");
            HostObservationConnection connection = new HostObservationConnection(this);
            connections.Add(connection);
            return connection;
        }
    }

    internal HostCaptureResult Capture(HostObservationConnection connection, int coreId, CancellationToken token)
    {
        if (coreId < 0) throw new ArgumentOutOfRangeException(nameof(coreId));
        lock (gate)
        {
            EnsureConnected(connection);
            token.ThrowIfCancellationRequested();
            CoreStateSnapshot snapshot;
            try { snapshot = readCore(coreId); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return new(HostCaptureStatus.ProviderFault, null, ex.Message);
            }
            // A synchronous provider cannot be interrupted; canceled captures are never published.
            token.ThrowIfCancellationRequested();
            EnsureConnected(connection); // A host callback may have closed/detached reentrantly.
            if (snapshot is null || snapshot.CoreId != coreId || snapshot.ActiveVirtualThreadId < 0 ||
                snapshot.VirtualThreadLivePcs is null || snapshot.VirtualThreadCommittedPcs is null ||
                snapshot.ActiveVirtualThreadRegisters is null || snapshot.DecodePublicationCertificate is null ||
                snapshot.ExecuteCompletionCertificate is null || snapshot.RetireVisibilityCertificate is null)
                return new(HostCaptureStatus.InvalidSnapshot, null, "Provider returned an absent or inconsistent core snapshot.");
            if ((snapshot.VirtualThreadLivePcs.Count != 0 && snapshot.ActiveVirtualThreadId >= snapshot.VirtualThreadLivePcs.Count) ||
                (snapshot.VirtualThreadCommittedPcs.Count != 0 && snapshot.ActiveVirtualThreadId >= snapshot.VirtualThreadCommittedPcs.Count))
                return new(HostCaptureStatus.InvalidSnapshot, null, "Active VT is outside supplied PC tables.");
            if (snapshot.VirtualThreadLivePcs.Count > 4096 || snapshot.VirtualThreadCommittedPcs.Count > 4096 ||
                snapshot.ActiveVirtualThreadRegisters.Count > 4096)
                return new(HostCaptureStatus.InvalidSnapshot, null, "Snapshot exceeds the diagnostic limit of 4096 entries per register/PC table.");
            if (snapshot.VirtualThreadLivePcs.Count != 0 && snapshot.VirtualThreadLivePcs[snapshot.ActiveVirtualThreadId] != snapshot.LiveInstructionPointer)
                return new(HostCaptureStatus.InvalidSnapshot, null, "Active PC contradicts the supplied VT PC table.");
            // Never expose provider-owned mutable lists to consumers or other subscribers.
            CoreStateSnapshot copy = snapshot with
            {
                VirtualThreadLivePcs = Array.AsReadOnly(snapshot.VirtualThreadLivePcs.ToArray()),
                VirtualThreadCommittedPcs = Array.AsReadOnly(snapshot.VirtualThreadCommittedPcs.ToArray()),
                ActiveVirtualThreadRegisters = Array.AsReadOnly(snapshot.ActiveVirtualThreadRegisters.ToArray())
            };
            HostObservationFrame frame = new HostObservationFrame(connection.ConnectionId, Descriptor.Identity, DateTimeOffset.UtcNow,
                Descriptor.Source, Descriptor.SourceKind, copy);
            connection.Publish(frame);
            return new(HostCaptureStatus.Observed, frame, string.Empty);
        }
    }

    internal DiagnosticSubscription<HostObservationFrame> Subscribe(HostObservationConnection connection, int capacity)
    {
        lock (gate) { EnsureConnected(connection); return connection.SubscribeCore(capacity); }
    }
    internal void Detach(HostObservationConnection connection)
    {
        lock (gate)
            if (connections.Remove(connection)) connection.Finish(HostConnectionState.Detached, "Observer detached; host execution is unaffected.");
    }
    private void EnsureConnected(HostObservationConnection connection)
    {
        if (closedReason is not null) throw new InvalidOperationException(closedReason);
        if (!connections.Contains(connection)) throw new ObjectDisposedException(nameof(HostObservationConnection));
    }
    public void Close(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (gate)
        {
            if (closedReason is not null) return;
            closedReason = reason;
            foreach (HostObservationConnection connection in connections) connection.Finish(HostConnectionState.HostClosed, reason);
            connections.Clear();
        }
    }
    public void Dispose() => Close("Host observation endpoint disposed; no guest completion inferred.");
}

public sealed class HostObservationConnection : IDiagnosticEventSource<HostObservationFrame>, IDisposable
{
    private readonly HostObservationEndpoint endpoint;
    private readonly DiagnosticEventHub<HostObservationFrame> events;
    private (HostConnectionState State, string? Reason) termination = (HostConnectionState.Connected, null);
    private readonly object stateGate = new();
    public Guid ConnectionId { get; } = Guid.NewGuid();
    public HostObservationDescriptor Descriptor => endpoint.Descriptor;
    public HostConnectionState State { get { lock (stateGate) return termination.State; } }
    public string? CompletionReason { get { lock (stateGate) return termination.Reason; } }
    internal HostObservationConnection(HostObservationEndpoint endpoint)
    {
        this.endpoint = endpoint;
        events = new(ConnectionId);
    }
    public HostCaptureResult CaptureOnce(int coreId, CancellationToken cancellationToken = default) => endpoint.Capture(this, coreId, cancellationToken);
    public DiagnosticSubscription<HostObservationFrame> Subscribe(int capacity = 128) => endpoint.Subscribe(this, capacity);
    internal DiagnosticSubscription<HostObservationFrame> SubscribeCore(int capacity) => events.Subscribe(capacity);
    internal void Publish(HostObservationFrame frame) => events.Publish(frame);
    internal void Finish(HostConnectionState state, string reason)
    {
        lock (stateGate) termination = (state, reason);
        events.Complete();
    }
    public void Dispose() => endpoint.Detach(this);
}
