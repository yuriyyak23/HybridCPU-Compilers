namespace Phase07BoundedRecursionPublish;

public static class EntryPoint
{
    public static int Root() => Countdown(5);
    public static int Countdown(int value) => value == 0 ? 1 : Countdown(value - 1) + value;
}
