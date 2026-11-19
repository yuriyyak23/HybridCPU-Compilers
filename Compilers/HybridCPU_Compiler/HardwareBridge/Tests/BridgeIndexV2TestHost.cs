#if HARDWAREBRIDGE_TESTS
// Isolated production codec contract test host; never runs Csc/Golden or hardware authority.
internal static class BridgeIndexV2TestHost
{
    public static int Main()
    {
        try
        {
            BridgeIndexV2Tests.Run();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: HB-INT-03 isolated contract tests: " + error);
            return 1;
        }
    }
}
#endif
