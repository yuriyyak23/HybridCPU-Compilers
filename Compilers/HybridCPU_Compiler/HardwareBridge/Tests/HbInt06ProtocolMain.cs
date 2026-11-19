#if HARDWAREBRIDGE_TESTS
// No Roslyn/CIL/Core dependency: this is solely a v1 sidecar/source DTO regression host.
// The Companion/PE-PDB test remains a separate required gate; this is NOT its substitute.
internal static class HbInt06ProtocolMain
{
    public static int Main()
    {
        try
        {
            HbInt06ProtocolTests.Run();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL HB-INT-06 protocol-only: " + exception);
            return 1;
        }
    }
}
#endif
