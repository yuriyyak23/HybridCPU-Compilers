namespace HybridCPU.RefPlan7.Phase00.Corpus;

public delegate int IntUnaryDelegate(int value);

public class DelegateTarget
{
    public int Offset;

    public virtual int Add(int value) => value + Offset;

    public virtual int Identity(int value) => value;
}

public static unsafe class DelegateCorpus
{
    public static int PlusOne(int value) => value + 1;

    public static IntUnaryDelegate CreateStatic() => new(PlusOne);

    public static IntUnaryDelegate CreateClosed(DelegateTarget target) => target.Add;

    public static IntUnaryDelegate CreateClosedIdentity(DelegateTarget target) => target.Identity;

    public static int Invoke(IntUnaryDelegate callback, int value) => callback(value);

    public static int InvokeManagedPointer(int value) =>
        ((delegate* managed<int, int>)&PlusOne)(value);

    public static int InvokePassedManagedPointer(delegate* managed<int, int> pointer, int value) =>
        pointer(value);
}
