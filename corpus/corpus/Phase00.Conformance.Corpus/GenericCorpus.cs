namespace HybridCPU.RefPlan7.Phase00.Corpus;

public static class GenericCorpus
{
    public static T Identity<T>(T value) => value;

    public static T Nested<T>(T value) => Identity<T>(value);

    public static int ExactIntProgram() => Nested<int>(37);

    public static long ExactLongProgram(long value) => Nested<long>(value);

    public static GenericReference ExactReferenceProgram(GenericReference value) => Nested<GenericReference>(value);

    public static T Constrained<T>(T value) where T : struct => value;

    public static int UnsupportedConstraintProgram() => Constrained<int>(43);

    public static int ConstructedTypeProgram() => GenericBox<int>.Identity(47);

    public static int GenericVirtualProgram(GenericBase<int> target) => target.Invoke(53);

    public static int GenericInterfaceProgram(IGenericContract<int> target) => target.Invoke(59);
}

public static class GenericBox<T>
{
    public static T Identity(T value) => value;
}

public interface IGenericContract<T>
{
    T Invoke(T value);
}

public class GenericBase<T>
{
    public virtual T Invoke(T value) => value;
}

public sealed class GenericImplementation<T> : GenericBase<T>, IGenericContract<T>
{
    public override T Invoke(T value) => value;
}

public sealed class GenericReference
{
}
