// Structural transport-only tests. No actual Golden execution or verification authority.
#if HARDWAREBRIDGE_TESTS
internal static class GoldenRawTraceContractProgram
{
    public static int Main()
    {
        try
        {
            GoldenRawTraceTests.Run();
            Console.WriteLine("HB-INT-04 raw trace codec: structural/mutation/negative tests PASS (no Golden runtime).");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("HB-INT-04 raw trace codec FAIL: " + error);
            return 1;
        }
    }
}
#endif
