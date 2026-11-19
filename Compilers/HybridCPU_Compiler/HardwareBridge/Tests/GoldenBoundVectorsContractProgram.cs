// HB-INT-04 software fact protocol tests; never a general Golden/RTL runner.
internal static class GoldenBoundVectorsContractProgram
{
    public static int Main()
    {
        try
        {
            GoldenBoundVectorsTests.Run();
            Console.WriteLine("HB-INT-04 BoundVectors contract PASS (unqualified, no Golden source execution).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("HB-INT-04 BoundVectors contract FAIL: " + exception);
            return 1;
        }
    }
}
