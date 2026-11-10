using System;

namespace HybridCPU.Compiler.Core.IR.Lowering;

public sealed record CompilerHelperRecoveryResult<TPlan>(
    CompilerHelperRecoveryStatus Status,
    CompilerLoweringDecision Decision,
    TPlan? Plan,
    string SourceApi,
    string Reason)
    where TPlan : class
{
    public static CompilerHelperRecoveryResult<TPlan> Recovered(
        TPlan plan,
        CompilerLoweringDecision decision,
        string sourceApi,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceApi);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(CompilerHelperRecoveryStatus.HelperAbiRecovered, decision, plan, sourceApi, reason);
    }

    public static CompilerHelperRecoveryResult<TPlan> NotRecognized(
        CompilerLoweringDecision decision,
        string sourceApi,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceApi);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(CompilerHelperRecoveryStatus.NotRecognized, decision, Plan: null, sourceApi, reason);
    }

    public static CompilerHelperRecoveryResult<TPlan> Rejected(
        CompilerLoweringDecision decision,
        string sourceApi,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceApi);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(CompilerHelperRecoveryStatus.HelperAbiRejected, decision, Plan: null, sourceApi, reason);
    }
}
