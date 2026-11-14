#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>
/// Source-bound initial-only state candidate for MicroOpScheduler.GlobalCycleCounter.
/// The rising edge represents one PackBundleIntraCoreSmt invocation attempt; valid
/// is the source guard (non-null bundle with eight slots). No runtime reset is
/// inferred; target-specific implementation of the field initializer is unqualified.
/// </summary>
public sealed record SchedulerCycleCounterCandidate(
    HwValidatedDesign Design, string SchedulerSha256, string MethodSha256,
    int MethodLine, int MethodColumn, int SmtWays, int TdmPeriod, string Scope);

public static class BridgeHwirSchedulerCycleCounter
{
    public const string SchedulerSha256 = "c7e675d76bb4eed9ecccb0b20c13c973895cce556e93c21457d966add731d8bf";
    public const string MethodSha256 = "4ecfa34a01d71033b9d6c0b3f2ce752cb50ff8d6004b3a2ba3d6f1c5710e340d";

    public static SchedulerCycleCounterCandidate Compile(byte[] schedulerBytes, byte[] methodBytes,
        string baseCommit, HwToolchainIdentity toolchain, bool checkedOverflow)
    {
        ArgumentNullException.ThrowIfNull(schedulerBytes);
        ArgumentNullException.ThrowIfNull(methodBytes);
        SchedulerCounterCallCandidate prefix = BridgeHwirSchedulerCounterCall.Compile(
            schedulerBytes, methodBytes, SchedulerSha256, MethodSha256, baseCommit,
            toolchain with { HardwareLanguageProfile = "hwp-hwir-comb-v1" }, checkedOverflow);

        string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var schedulerTree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(schedulerBytes), options);
        var methodTree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(methodBytes), options);
        var owner = schedulerTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "MicroOpScheduler" &&
                         c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core" &&
                         c.Modifiers.ToString().Contains("partial", StringComparison.Ordinal));
        var methodOwner = methodTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "MicroOpScheduler" &&
                         c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core" &&
                         c.Modifiers.ToString().Contains("partial", StringComparison.Ordinal));
        var method = methodOwner.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "PackBundleIntraCoreSmt" && m.ParameterList.Parameters.Count == 5);
        string compactMethod = string.Concat(method.DescendantTokens().Select(t => t.Text));
        int increment = compactMethod.IndexOf("GlobalCycleCounter++", StringComparison.Ordinal);
        int tdmDecode = compactMethod.IndexOf("GlobalCycleCounter%TDM_PERIOD)==0", StringComparison.Ordinal);
        if (increment < 0 || tdmDecode <= increment ||
            !compactMethod.Contains("bundle==null||bundle.Count!=8", StringComparison.Ordinal) ||
            !compactMethod.Contains("forceTdm=isTdmSlot&&nominationState.HasReadyBackgroundCandidates", StringComparison.Ordinal) ||
            !compactMethod.Contains("if(CreditFairnessEnabled)", StringComparison.Ordinal))
            throw new InvalidDataException("Scheduler TDM counter guard, update order, or consumers changed.");

        FieldDeclarationSyntax periodField = owner.Members.OfType<FieldDeclarationSyntax>()
            .Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "TDM_PERIOD"));
        FieldDeclarationSyntax waysField = owner.Members.OfType<FieldDeclarationSyntax>()
            .Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "SMT_WAYS"));
        if (periodField.Declaration.Type.ToString() != "int" ||
            periodField.Declaration.Variables.Single().Initializer?.Value.ToString() != "64" ||
            waysField.Declaration.Type.ToString() != "int" ||
            waysField.Declaration.Variables.Single().Initializer?.Value.ToString() != "4")
            throw new InvalidDataException("Scheduler TDM period or SMT-way source constants changed.");

        if (Hash(schedulerBytes) != SchedulerSha256 || Hash(methodBytes) != MethodSha256)
            throw new InvalidDataException("Scheduler counter sources are stale.");

        string sourceDigest = Hash(Encoding.UTF8.GetBytes("scheduler-cycle-counter-initial-enable/v1|" + SchedulerSha256 + "|" + MethodSha256));
        var location = method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", baseCommit,
            "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/Smt/MicroOpScheduler.SMT.cs",
            "CircuitWeave.SchedulerCycleCounter", "YAKSys_Hybrid_CPU.Core.MicroOpScheduler",
            "PackBundleIntraCoreSmt", sourceDigest, HwSourceRole.Primary, location.Line + 1, location.Character + 1);

        var bit = new HwBitVectorType(1);
        var u32 = new HwBitVectorType(32);
        var u64 = new HwBitVectorType(64);
        HwPortContract[] ports =
        [
            new("call_clock", HwPortDirection.Input, bit, HwSignalKind.Clock, "call_clock"),
            new("bundle_present", HwPortDirection.Input, bit),
            new("bundle_count", HwPortDirection.Input, u32),
            new("cycle_count", HwPortDirection.Output, u64)
        ];
        HwNode[] nodes =
        [
            new HwConstantNode("one", u64, new string('0', 63) + "1", origin),
            new HwConstantNode("eight", u32, new string('0', 28) + "1000", origin),
            new HwOperationNode("count_is_eight", "eq", ["bundle_count", "eight"], bit, Origin: origin),
            new HwOperationNode("valid_call", "and", ["bundle_present", "count_is_eight"], bit, Origin: origin),
            new HwOperationNode("count_next", "add", ["count_q", "one"], u64, Origin: origin),
            new HwRegisterNode("count_q", u64, "call_clock", "count_next", "valid_call", null, origin,
                EnablePolicy: null, InitialValue: new string('0', 64)),
            new HwOutputDriveNode("drive_count", "cycle_count", "count_q", origin)
        ];
        var contract = new HwHardwareContract("hybridcpu.scheduler.global-cycle-counter.initial-enable.v1",
            HwModuleKind.Clocked, ports, new HwTimingContract(1, 1, "call_clock"));
        var module = new HwModule("scheduler_cycle_counter", "scheduler_cycle_counter", ports, nodes, [], contract,
            origin, ClockDomains: [new HwClockDomain("call_clock", HwClockEdge.Rising, null)]);
        var design = new HwDesign("hwir/v0.1", module.Id, [module],
            toolchain with { HardwareLanguageProfile = "hwp-clocked-init-enable-v1" }, []);
        HwValidationResult result = new FailClosedHardwareIrValidator().Validate(design);
        if (!result.Accepted || result.Validated is null)
            throw new InvalidDataException("Scheduler initialized-counter HWIR refused: " + string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
        return new(result.Validated, SchedulerSha256, MethodSha256, location.Line + 1, location.Character + 1,
            4, 64, "One rising call_clock edge per scheduler invocation attempt; state advances iff bundle_present and bundle_count equals 8, matching the source early-return guard. Initial value is field initializer 0; source has no runtime reset. TDM/forceTdm consumers are not included; physical clock and power-up initialization remain unqualified.");
    }
}
#endif
