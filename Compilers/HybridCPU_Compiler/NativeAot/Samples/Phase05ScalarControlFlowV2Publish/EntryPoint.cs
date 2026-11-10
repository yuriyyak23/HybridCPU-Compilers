namespace Phase05ScalarControlFlowV2Publish;

public static class EntryPoint
{
    public static int Root() => Loop(5) + Left(3) + Right(3);

    public static int Loop(int limit)
    {
        int sum = 0;
        for (int index = 0; index < limit; index++)
            sum += index;
        return sum;
    }

    public static int Shared(int value) => value + 7;
    public static int Left(int value) => Shared(value) + 1;
    public static int Right(int value) => Shared(value) - 1;
}
