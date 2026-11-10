namespace HybridCPU.RefPlan7.Phase00.Corpus;

public sealed class InstanceCarrierCorpus
{
    public int DirectNestedCall(int value) => AddOne(value);

    private int AddOne(int value) => value + 1;

    public int FourExplicitArguments(int a, int b, int c, int d) => a + b + c + d;
}
