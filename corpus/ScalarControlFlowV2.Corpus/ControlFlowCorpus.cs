namespace HybridCPU.RefPlan6.Corpus;

public static class ControlFlowCorpus
{
    public static int NestedIfElse(int value, int other)
    {
        if (value > 0)
            return other > 0 ? value + other : value - other;
        return other < 0 ? other - value : 0;
    }

    public static int ForAccumulator(int count)
    {
        int sum = 0;
        for (int index = 0; index < count; index++)
            sum += index;
        return sum;
    }

    public static int WhileAccumulator(int count)
    {
        int sum = 0;
        int index = 0;
        while (index < count)
        {
            sum += index;
            index++;
        }
        return sum;
    }

    public static int DoWhileAccumulator(int count)
    {
        int sum = 0;
        int index = 0;
        if (count <= 0)
            return sum;
        do
        {
            sum += index;
            index++;
        }
        while (index < count);
        return sum;
    }

    public static int NestedLoops(int outer, int inner)
    {
        int sum = 0;
        for (int x = 0; x < outer; x++)
            for (int y = 0; y < inner; y++)
                sum += x + y;
        return sum;
    }

    public static int ConditionalExit(int count, int stop)
    {
        int sum = 0;
        for (int index = 0; index < count; index++)
        {
            if (index == stop)
                break;
            if ((index & 1) == 0)
                continue;
            sum += index;
        }
        return sum;
    }
}
