using CpuInterfaceBridge;
using CpuInterfaceBridge.Diagnostics;

internal static class HostObservationTests
{
    public static async Task Run(Action<bool, string> check)
    {
        void Reject(Action action, string name)
        {
            try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
            { check(true, name); return; }
            throw new Exception("Accepted " + name);
        }
        int calls = 0;
        ulong[] registers = [42];
        CoreStateSnapshot snapshot = new()
        {
            CoreId = 0, LiveInstructionPointer = 256, ActiveVirtualThreadId = 0,
            ActiveVirtualThreadRegisters = registers, VirtualThreadLivePcs = new ulong[] { 256 },
            RetireVisibilityCertificate = new() { IsPublished = true, Pc = 128, Kind = PipelineContourKind.RetireVisibility }
        };
        Func<int, CoreStateSnapshot> provider = _ => { calls++; return snapshot; };
        using HostObservationEndpoint host = new HostObservationEndpoint("fixture host", "explicit synthetic source", HostObservationSourceKind.Snapshot, id => provider(id), 2);
        HostObservationDescriptor descriptor = host.Descriptor;
        check(descriptor.Identity.ProcessId == Environment.ProcessId && !descriptor.SupportsRemoteAttach && !descriptor.SupportsExecutionControl && !descriptor.SupportsMemoryReads, "host identity and honest capabilities");
        Reject(() => host.Connect(descriptor.Identity with { InstanceId = Guid.NewGuid() }), "host rejects reused PID with different instance");
        Reject(() => host.Connect(descriptor.Identity with { ProcessStartedAtUtc = descriptor.Identity.ProcessStartedAtUtc.AddSeconds(1) }), "host rejects process start identity mismatch");
        Reject(() => host.Connect(descriptor.Identity, "v99"), "host rejects protocol mismatch");
        using HostObservationConnection connection = host.Connect(descriptor.Identity);
        using HostObservationConnection other = host.Connect(descriptor.Identity);
        Reject(() => host.Connect(descriptor.Identity), "host connection limit");
        using DiagnosticSubscription<HostObservationFrame> slow = connection.Subscribe(1);
        using DiagnosticSubscription<HostObservationFrame> fast = connection.Subscribe(4);
        using DiagnosticSubscription<HostObservationFrame> isolated = other.Subscribe(1);
        check(calls == 0, "connect and subscribe never capture implicitly");
        HostCaptureResult result = connection.CaptureOnce(0);
        check(result.Status == HostCaptureStatus.Observed && result.Frame!.Snapshot.RetireVisibilityCertificate.Pc == 128 &&
            result.Frame.Snapshot.LiveInstructionPointer == 256 && result.Frame.Qualification.State == EvidenceState.Unavailable &&
            result.Frame.GuestRunIdentity.State == EvidenceState.Unavailable, "host snapshot distinguishes live PC certificates and unavailable guest identity");
        registers[0] = 99;
        check(result.Frame!.Snapshot.ActiveVirtualThreadRegisters[0] == 42, "snapshot defensive copy");
        try { ((IList<ulong>)result.Frame.Snapshot.ActiveVirtualThreadRegisters)[0] = 1; throw new Exception("Mutable snapshot"); }
        catch (NotSupportedException) { check(true, "snapshot lists cannot mutate another subscriber payload"); }
        connection.CaptureOnce(0);
        check(slow.DroppedEventCount == 1 && fast.DroppedEventCount == 0 && isolated.DroppedEventCount == 0, "host independent subscribers");
        using CancellationTokenSource canceled = new CancellationTokenSource(); canceled.Cancel();
        int before = calls;
        try { connection.CaptureOnce(0, canceled.Token); throw new Exception("Ignored cancellation"); }
        catch (OperationCanceledException) { check(calls == before, "canceled capture never touches provider"); }
        provider = _ => throw new InvalidOperationException("provider unavailable: exact reason");
        result = connection.CaptureOnce(0);
        check(result.Status == HostCaptureStatus.ProviderFault && result.Frame is null && result.Reason == "provider unavailable: exact reason", "provider failure is not stale successful snapshot");
        provider = _ => snapshot with { CoreId = 1 };
        check(connection.CaptureOnce(0).Status == HostCaptureStatus.InvalidSnapshot, "host rejects wrong core identity");
        provider = _ => snapshot with { ActiveVirtualThreadId = 2 };
        check(connection.CaptureOnce(0).Status == HostCaptureStatus.InvalidSnapshot, "host rejects invalid VT range");
        provider = _ => snapshot with { LiveInstructionPointer = 999 };
        check(connection.CaptureOnce(0).Status == HostCaptureStatus.InvalidSnapshot, "host rejects contradictory active PC");
        using CancellationTokenSource during = new CancellationTokenSource();
        provider = _ => { during.Cancel(); return snapshot; };
        try { connection.CaptureOnce(0, during.Token); throw new Exception("Ignored cancellation after read"); }
        catch (OperationCanceledException) { check(slow.DroppedEventCount == 1, "canceled in-flight capture is not published"); }
        connection.Dispose();
        List<DiagnosticEvent<HostObservationFrame>> events = new List<DiagnosticEvent<HostObservationFrame>>();
        await foreach (DiagnosticEvent<HostObservationFrame> item in fast.ReadAllAsync()) events.Add(item);
        check(events.Count == 2 && events[0].Sequence == 1 && events[1].Sequence == 2 &&
            events.All(e => e.SessionId == connection.ConnectionId) && connection.State == HostConnectionState.Detached, "detach drains events with connection identity and sequence");
        Reject(() => connection.CaptureOnce(0), "detached connection cannot capture");
        Reject(() => connection.Subscribe(), "detached connection cannot subscribe");
        check(other.State == HostConnectionState.Connected, "detach leaves other observer connected");
        using HostObservationConnection replacement = host.Connect(descriptor.Identity);
        check(replacement.ConnectionId != connection.ConnectionId, "reconnect has new connection identity");
        host.Close("host owner closed observer");
        check(other.State == HostConnectionState.HostClosed && other.CompletionReason == "host owner closed observer", "host close preserves reason");
        int isolatedCount = 0; await foreach (DiagnosticEvent<HostObservationFrame> item in isolated.ReadAllAsync()) isolatedCount++;
        check(isolatedCount == 0, "other connection does not steal or receive explicit captures");
        Reject(() => host.Connect(descriptor.Identity), "closed host cannot accept observer");
        Reject(() => new HostObservationEndpoint("x", "null", HostObservationSourceKind.Unavailable, _ => snapshot), "unavailable source cannot pretend to attach");
        DiagnosticEventHub<int> boundedHub = new DiagnosticEventHub<int>(Guid.NewGuid(), 1);
        using DiagnosticSubscription<int> first = boundedHub.Subscribe();
        Reject(() => boundedHub.Subscribe(), "subscriber count bounded");
        first.Dispose(); using DiagnosticSubscription<int> next = boundedHub.Subscribe();
        Reject(() => boundedHub.Subscribe(65537), "buffer size bounded");
    }
}
