namespace HybridCPU.RefPlan7.Phase00.Corpus;

public interface IDispatchCorpus
{
    int Invoke(int value);
}

public class DispatchBase : IDispatchCorpus
{
    public virtual int Invoke(int value) => value + 1;
}

public sealed class DispatchDerived : DispatchBase
{
    public override int Invoke(int value) => value + 2;
}

public sealed class DispatchOther : IDispatchCorpus
{
    public int Invoke(int value) => value + 3;
}

public static class DispatchCaller
{
    public static int VirtualCall(DispatchBase receiver, int value) => receiver.Invoke(value);

    public static int InterfaceCall(IDispatchCorpus receiver, int value) => receiver.Invoke(value);

    public static int DispatchWithRecursionRoot(DispatchBase receiver) => receiver.Invoke(1) + Countdown(3);

    public static int Countdown(int count) => count == 0 ? 0 : count + Countdown(count - 1);

    public static DispatchDerived Cast(DispatchBase receiver) => (DispatchDerived)receiver;

    public static DispatchDerived? Test(DispatchBase receiver) => receiver as DispatchDerived;
}
