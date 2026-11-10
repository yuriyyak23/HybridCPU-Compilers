namespace CpuInterfaceBridge.Diagnostics;

/// <summary>Ready-to-call passive integration example. The caller explicitly opens/provides both streams.</summary>
public static class CompilerToolExample
{
    public static FailureCard ReadFailure(Stream runnerJson, string reportSource, Stream? packageBytes = null)
    {
        DiagnosticSessionV1 session = RunnerReportImporter.Import(runnerJson, Guid.NewGuid(), reportSource, packageBytes);
        return DiagnosticQueries.Failure(session);
    }
}
