using System.Reflection;
using YAKSys_Hybrid_CPU;

namespace HybridCPU_ISE.Tests.Architecture;

public sealed class Rf1121AssistStateExtractionTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    [Fact]
    public void RuntimeContainsExactTwoFieldAssistState()
    {
        Type runtime = Required("YAKSys_Hybrid_CPU.Core.CoreRuntimeState");
        Type assist = Required("YAKSys_Hybrid_CPU.Core.AssistState");
        Assert.Single(runtime.GetFields(Flags), field => field.FieldType == assist);
        Assert.Equal(new[] { "LastInvalidationReason", "RuntimeEpoch" }, assist.GetFields(Flags).Select(field => field.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.DoesNotContain(assist.GetMethods(Flags), method => method.Name is "Execute" or "Commit" or "Rollback" or "Publish");
    }

    [Fact]
    public void LegacyFieldsAreRemovedAndForwardByRef()
    {
        Type core = typeof(Processor.CPU_Core);
        foreach (string name in new[] { "_assistRuntimeEpoch", "_lastAssistInvalidationReason" })
        {
            Assert.Null(core.GetField(name, Flags));
            Assert.True((core.GetProperty(name, Flags) ?? throw new InvalidOperationException(name)).PropertyType.IsByRef);
        }
    }

    [Fact]
    public void CopiesShareAssistIdentity()
    {
#pragma warning disable CS0618
        var core = new Processor.CPU_Core(0);
#pragma warning restore CS0618
        Processor.CPU_Core copy = core;
        core.Runtime.Assist.RuntimeEpoch = 9;
        core.Runtime.Assist.LastInvalidationReason = YAKSys_Hybrid_CPU.Core.AssistInvalidationReason.Replay;
        Assert.Equal(9UL, copy.Runtime.Assist.RuntimeEpoch);
        Assert.Equal(YAKSys_Hybrid_CPU.Core.AssistInvalidationReason.Replay, copy.Runtime.Assist.LastInvalidationReason);
    }

    [Fact]
    public void ResetInvalidateAndValidationOrderRemainInPlace()
    {
        string root = FindRoot();
        string assist = Read(root, "HybridCPU_ISE", "CloseToHSL", "Core", "Pipeline", "Assist", "Runtime", "CPU_Core.Assist.cs");
        AssertOrder(assist, "_assistRuntimeEpoch = 0;", "_lastAssistInvalidationReason = Core.AssistInvalidationReason.None;");
        AssertOrder(assist, "_assistRuntimeEpoch++;", "Runtime.Telemetry.AssistInvalidationCount++;", "_lastAssistInvalidationReason = reason;", "_fspScheduler?.InvalidateAssistNominationState(reason);");
        Assert.Contains("assistMicroOp.AssistEpochId != _assistRuntimeEpoch", assist, StringComparison.Ordinal);
    }

    [Fact]
    public void SchedulerReplayRetireAndTelemetryOwnersRemainSeparate()
    {
        Type assist = Required("YAKSys_Hybrid_CPU.Core.AssistState");
        Assert.DoesNotContain(assist.GetFields(Flags), field => field.FieldType.Name.Contains("Scheduler", StringComparison.Ordinal) || field.FieldType.Name.Contains("Replay", StringComparison.Ordinal) || field.FieldType.Name.Contains("Retire", StringComparison.Ordinal));
        Type telemetry = Required("YAKSys_Hybrid_CPU.Core.TelemetryState");
        Assert.Contains(telemetry.GetFields(Flags), field => field.Name == "AssistInvalidationCount");
    }

    [Fact]
    public void LedgerAndEvidenceCloseOnlyAssistIdentityStorage()
    {
        string root = FindRoot();
        string ledger = Read(root, "Documentation", "Documentation", "ArchitectureAuthorityRefactor", "10_RF11", "00_CURRENT_STATUS_AND_LEDGER.md");
        string evidence = Read(root, "Documentation", "Documentation", "ArchitectureAuthorityRefactor", "Evidence", "RF11", "rf11.21-assist-state-extraction.md");
        Assert.Contains("RF-11.21 | closed AssistState", ledger, StringComparison.Ordinal);
        Assert.Contains("exactly two", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RF-11.22 ScratchState", ledger, StringComparison.Ordinal);
    }

    private static Type Required(string name) => typeof(Processor.CPU_Core).Assembly.GetType(name) ?? throw new InvalidOperationException(name);
    private static void AssertOrder(string text, params string[] markers) { int prior = -1; foreach (string marker in markers) { int current = text.IndexOf(marker, prior + 1, StringComparison.Ordinal); Assert.True(current > prior, $"Expected '{marker}' after prior marker."); prior = current; } }
    private static string Read(string root, params string[] parts) => File.ReadAllText(parts.Aggregate(root, Path.Combine));
    private static string FindRoot() { string? current = AppContext.BaseDirectory; while (current != null) { if (File.Exists(Path.Combine(current, "HybridCPU v2.slnx"))) return current; current = Directory.GetParent(current)?.FullName; } throw new DirectoryNotFoundException(); }
}
