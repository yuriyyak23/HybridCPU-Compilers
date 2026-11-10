using System.Threading.Channels;

namespace CpuInterfaceBridge.Diagnostics;

public sealed record DiagnosticEvent<T>(Guid SessionId, long Sequence, T Payload);
public interface IDiagnosticEventSource<T>
{
    DiagnosticSubscription<T> Subscribe(int capacity = 128);
}
/// <summary>Independent bounded subscribers; explicit DropNewest policy. No replay, execution, IPC or attach.</summary>
public sealed class DiagnosticEventHub<T> : IDiagnosticEventSource<T>
{
    private readonly object gate = new();
    private readonly HashSet<DiagnosticSubscription<T>> subscribers = [];
    private readonly Guid sessionId;
    private long sequence;
    private bool completed;
    private Exception? completionError;
    private readonly int maximumSubscribers;
    public DiagnosticEventHub(Guid sessionId) : this(sessionId, 128) { }
    public DiagnosticEventHub(Guid sessionId, int maximumSubscribers)
    {
        this.sessionId = sessionId != Guid.Empty ? sessionId : throw new ArgumentException("Session required");
        if (maximumSubscribers is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumSubscribers));
        this.maximumSubscribers = maximumSubscribers;
    }
    public DiagnosticSubscription<T> Subscribe(int capacity = 128)
    {
        if (capacity is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(capacity));
        lock (gate)
        {
            if (!completed && subscribers.Count >= maximumSubscribers) throw new InvalidOperationException("Subscriber capacity exceeded.");
            DiagnosticSubscription<T> subscriber = new DiagnosticSubscription<T>(capacity, Remove);
            if (completed) subscriber.Complete(completionError); else subscribers.Add(subscriber);
            return subscriber;
        }
    }
    public void Publish(T payload)
    {
        lock (gate)
        {
            if (completed) throw new InvalidOperationException("Source completed");
            DiagnosticEvent<T> value = new DiagnosticEvent<T>(sessionId, checked(++sequence), payload);
            foreach (DiagnosticSubscription<T> subscriber in subscribers) subscriber.Publish(value);
        }
    }
    public void Complete(Exception? error = null)
    {
        lock (gate)
        {
            if (completed) return;
            completed = true;
            completionError = error;
            foreach (DiagnosticSubscription<T> subscriber in subscribers) subscriber.Complete(error);
            subscribers.Clear();
        }
    }
    private void Remove(DiagnosticSubscription<T> subscriber)
    {
        lock (gate) { subscribers.Remove(subscriber); subscriber.Complete(null); }
    }
}
public sealed class DiagnosticSubscription<T> : IDisposable
{
    private readonly Channel<DiagnosticEvent<T>> channel;
    private readonly Action<DiagnosticSubscription<T>> remove;
    private long dropped;
    private int reading;
    internal DiagnosticSubscription(int capacity, Action<DiagnosticSubscription<T>> remove)
    {
        this.remove = remove;
        channel = Channel.CreateBounded<DiagnosticEvent<T>>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait });
    }
    public string OverflowPolicy => "DropNewest";
    public long DroppedEventCount => Interlocked.Read(ref dropped);
    public Task Completion => channel.Reader.Completion;
    internal void Publish(DiagnosticEvent<T> value) { if (!channel.Writer.TryWrite(value)) Interlocked.Increment(ref dropped); }
    internal void Complete(Exception? error) => channel.Writer.TryComplete(error);
    public async IAsyncEnumerable<DiagnosticEvent<T>> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref reading, 1) != 0) throw new InvalidOperationException("One reader per subscription; subscribe separately for broadcast.");
        try { await foreach (DiagnosticEvent<T>? item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return item; }
        finally { Dispose(); }
    }
    public void Dispose() => remove(this);
}
