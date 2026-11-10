namespace HybridCpuAotAddDemo;

public static class EntryPoint
{
    // The default parameterless startup root demonstrates scalar calls, branches
    // and deterministic CFG joins. Other roots select the loop/call/recursion corpora.
    public static int ControlFlowRoot()
    {
        return Add(Compute(3, 4), NestedBranch(2));
    }

    // An independently selectable scalar leaf.
    public static int Add(int left, int right) => left + right;

    // Primitive locals, arithmetic and a forward conditional branch.
    public static int Compute(int left, int right)
    {
        int product = left * right;
        int shifted = product + 7;

        if (left != 0)
        {
            return shifted - left;
        }

        return (right * 2) + 1;
    }

    // Shared direct static callee: Left and Right intentionally share this body.
    public static int SharedCallGraph(int value) => Left(value) + Right(value);
    public static int Shared(int value) => value + 7;
    public static int Left(int value) => Shared(value) + 1;
    public static int Right(int value) => Shared(value) - 1;

    public static int CallGraphRoot() => SharedCallGraph(3);

    // Reducible for, while and do/while loop forms with loop-carried scalar values.
    public static int SumFor(int limit)
    {
        int sum = 0;
        for (int index = 0; index < limit; index++) sum += index;
        return sum;
    }

    public static int SumWhile(int limit)
    {
        int sum = 0;
        int index = 0;
        while (index < limit)
        {
            sum += index;
            index++;
        }
        return sum;
    }

    public static int SumDoWhile(int limit)
    {
        int sum = 0;
        int index = 0;
        do
        {
            sum += index;
            index++;
        } while (index < limit);
        return sum;
    }

    public static int SumWithBreakContinue(int limit)
    {
        int sum = 0;
        for (int index = 0; index < limit; index++)
        {
            if (index == 1) continue;
            if (index == 4) break;
            sum += index;
        }
        return sum;
    }

    public static int LoopRoot() => SumFor(5) + SumWhile(4) + SumDoWhile(3) + SumWithBreakContinue(6);

    // Nested diamonds exercise deterministic CFG joins and scalar phi lowering.
    public static int NestedBranch(int value)
    {
        if (value > 0)
        {
            if (value > 1) return value + 10;
            return value + 20;
        }
        return value - 30;
    }

    // The default presented body world contains exactly one proven recursive SCC.
    public static int DirectRecursionRoot() => Countdown(5);
    public static int Countdown(int value) => value == 0 ? 1 : Countdown(value - 1) + value;

    // A separately selectable mutual-recursion corpus. Do not add it to the
    // default presented body world: V1 intentionally admits only one recursive SCC.
    public static int MutualRecursionRoot() => MutualA(6);
    public static int MutualA(int value) => value == 0 ? 0 : MutualB(value - 1) + 1;
    public static int MutualB(int value) => value == 0 ? 0 : MutualA(value - 1) + 1;

    // RefPlan7 Phase 08: exact closed generic instantiations become distinct
    // ordinary AOT bodies. Select GenericRoot with the documented body world.
    public static T Identity<T>(T value) => value;
    public static T NestedGeneric<T>(T value) => Identity<T>(value);
    public static int GenericRoot() => NestedGeneric<int>(37);
}
