using System.Reflection;
using System.Runtime.InteropServices;

namespace HybridCpuAotAddDemo;

// Source-level RefPlan7 corpus. These are normal C# constructs: RefPlan7 adds
// component contracts for them without inventing managed-specific CPU opcodes.
public static class LanguageShowcase
{
    public static async Task<int> AsyncAndCancellationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        return GenericIdentity(20) + 22;
    }

    public static T GenericIdentity<T>(T value) => value;

    // The methods above are static scenario roots so the harness can invoke them
    // deterministically. The object model below is deliberately instance-based:
    // constructors, fields, properties, inheritance, overrides and method groups.
    public static int InstanceObjectModel()
    {
        CounterBase counter = new ScalingCounter(seed: 5, scale: 2);
        int direct = counter.Add(4);                  // virtual override: 13
        ITransform transform = (ITransform)counter;
        int throughInterface = transform.Apply(3);   // interface dispatch: 19
        Func<int, int> closedInstance = counter.Add; // closed-instance delegate
        int throughDelegate = closedInstance(2);     // 23
        return direct + throughInterface + throughDelegate;
    }

    public static string GenericInstanceObject()
    {
        Box<Pair> box = new Box<Pair>(new Pair(11, 31));
        return box.Map(static pair => pair.Left + pair.Right).ToString();
    }

    public static int StaticInitialization() => StaticState.Value;

    public static int ArraysStringsValuesAndDelegates()
    {
        int[] values = [3, 5, 7];
        Pair pair = new(values[0], values[^1]);
        Func<int, int> addPair = value => value + pair.Left + pair.Right;
        string text = "RefPlan7";
        return addPair(values[1]) + text.Length;
    }

    public static int VirtualInterfaceDispatch(ITransform transform, int value) => transform.Apply(value);

    public static int ExceptionAndFinally(bool fail, out bool finallyRan)
    {
        finallyRan = false;
        try
        {
            if (fail) throw new DemoManagedException("managed failure");
            return 7;
        }
        catch (DemoManagedException)
        {
            return -7;
        }
        finally
        {
            finallyRan = true;
        }
    }

    public static string BoundedReflection() =>
        typeof(Pair).GetProperty(nameof(Pair.Left), BindingFlags.Public | BindingFlags.Instance)!.Name;

    public static int ThreadingTlsAndSynchronization()
    {
        ThreadLocal<int> local = new ThreadLocal<int>(() => 4);
        object gate = new();
        int value = 0;
        lock (gate) value = Interlocked.Add(ref value, local.Value + 3);
        local.Dispose();
        return value;
    }

    // Metadata-only P/Invoke declaration for the bounded blittable interop shape.
    // It is deliberately never invoked by the host demo.
    [DllImport("hc.demo", EntryPoint = "add_i8", CallingConvention = CallingConvention.Cdecl)]
    public static extern long NativeAdd(long left, long right);

    public readonly record struct Pair(int Left, int Right);
    public interface ITransform { int Apply(int value); }
    public sealed class Doubler : ITransform { public int Apply(int value) => value * 2; }
    public sealed class DemoManagedException(string message) : Exception(message);

    public class CounterBase
    {
        private int _value;

        public CounterBase(int seed) => _value = seed;
        public int Value => _value;
        public virtual int Add(int delta) => _value += delta;
    }

    public sealed class ScalingCounter : CounterBase, ITransform
    {
        public ScalingCounter(int seed, int scale) : base(seed) => Scale = scale;
        public int Scale { get; }
        public override int Add(int delta) => base.Add(delta * Scale);
        public int Apply(int value) => Add(value);
    }

    public sealed class Box<T>
    {
        public Box(T value) => Value = value;
        public T Value { get; }
        public TResult Map<TResult>(Func<T, TResult> selector) => selector(Value);
    }

    private static class StaticState
    {
        public static readonly int Value = Initialize();
        private static int Initialize() => 42;
    }
}
