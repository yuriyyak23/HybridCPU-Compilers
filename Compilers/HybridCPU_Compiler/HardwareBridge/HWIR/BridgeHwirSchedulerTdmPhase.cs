#if HARDWAREBRIDGE_HWIR
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>
/// Source-bound TDM phase predicates driven by the post-increment scheduler
/// counter. Outer pipeline/boundary decisions, raw ready-port state, and the FSM runnable mask
/// remain per-invocation inputs; normalization/intersection are in HWIR.
/// </summary>
public sealed record SchedulerTdmPhaseCandidate(
    HwValidatedDesign Design, string SchedulerSha256, string MethodSha256, string AdmissionSha256, string EligibilitySha256,
    int SmtWays, int TdmPeriod,
    string Scope);

public static class BridgeHwirSchedulerTdmPhase
{
    public const string AdmissionSha256 = "6cdd37f43a0a58c0b887f9e8bffe9d2d17abb5d404f7ace70fead05db4edbe25";
    public const string EligibilitySha256 = "2540ff3070faed29d565ca90ad231991087328bef623a6caa9c4e420521d0c52";

    public static SchedulerTdmPhaseCandidate Compile(byte[] schedulerBytes, byte[] methodBytes, byte[] admissionBytes, byte[] eligibilityBytes,
        string baseCommit, HwToolchainIdentity toolchain, bool checkedOverflow)
    {
        ArgumentNullException.ThrowIfNull(admissionBytes);
        ArgumentNullException.ThrowIfNull(eligibilityBytes);
        SchedulerCycleCounterCandidate counter = BridgeHwirSchedulerCycleCounter.Compile(
            schedulerBytes, methodBytes, baseCommit, toolchain, checkedOverflow);
        string admissionHash = Convert.ToHexString(SHA256.HashData(admissionBytes)).ToLowerInvariant();
        if (admissionHash != AdmissionSha256)
            throw new InvalidDataException("Scheduler admission sources are stale.");
        string eligibilityHash = Convert.ToHexString(SHA256.HashData(eligibilityBytes)).ToLowerInvariant();
        if (eligibilityHash != EligibilitySha256)
            throw new InvalidDataException("Scheduler eligibility sources are stale.");
        var eligibilityTree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(eligibilityBytes));
        if (eligibilityTree.GetDiagnostics().Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
            throw new InvalidDataException("Scheduler eligibility source does not parse.");
        MethodDeclarationSyntax normalizeMethod = eligibilityTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "NormalizeEligibleVirtualThreadMask");
        MethodDeclarationSyntax stateMethod = eligibilityTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "CreateEligibleSmtNominationState");
        var stateProperty = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(schedulerBytes)).GetRoot()
            .DescendantNodes().OfType<PropertyDeclarationSyntax>().Single(p => p.Identifier.ValueText == "HasReadyBackgroundCandidates");
        PropertyDeclarationSyntax allEligibleProperty = eligibilityTree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "AllEligibleVirtualThreadMask");
        AssignmentExpressionSyntax[] eligibilityAssignments = stateMethod.DescendantNodes().OfType<AssignmentExpressionSyntax>().ToArray();
        if (normalizeMethod.Body?.Statements.SingleOrDefault()?.NormalizeWhitespace().ToFullString() !=
                "return (byte)(eligibleVirtualThreadMask & AllEligibleVirtualThreadMask);" ||
            allEligibleProperty.ExpressionBody?.Expression.NormalizeWhitespace().ToFullString() != "(byte)((1 << SMT_WAYS) - 1)" ||
            !stateMethod.DescendantNodes().OfType<IfStatementSyntax>().Any(i => i.Condition.ToString() == "_smtPortValid[vt] && _smtPorts[vt] is not null") ||
            !stateMethod.DescendantNodes().OfType<IfStatementSyntax>().Any(i => i.Condition.ToString() == "(normalizedEligibleMask & vtMask) != 0") ||
            !stateMethod.DescendantNodes().OfType<ForStatementSyntax>().Any(f => f.Condition?.ToString() == "vt < SMT_WAYS") ||
            !stateMethod.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().Any(l => l.ToString().Contains("byte vtMask = (byte)(1 << vt);", StringComparison.Ordinal)) ||
            !eligibilityAssignments.Any(a => a.ToString() == "readyPortMask |= vtMask") ||
            !eligibilityAssignments.Any(a => a.ToString() == "visibleReadyMask |= vtMask") ||
            !stateMethod.DescendantNodes().OfType<ReturnStatementSyntax>().Any(r => r.Expression?.ToString() == "new SmtNominationState(visibleReadyMask)") ||
            stateProperty.ExpressionBody?.Expression.NormalizeWhitespace().ToFullString() != "(ReadyMask & 0b1110) != 0")
            throw new InvalidDataException("SMT eligible ready-mask construction or background predicate changed.");
        var admissionTree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(admissionBytes));
        if (admissionTree.GetDiagnostics().Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
            throw new InvalidDataException("Scheduler admission source does not parse.");
        var admissionOwner = admissionTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "MicroOpScheduler" &&
                c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core");
        MethodDeclarationSyntax rankMethod = admissionOwner.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "GetRankedVirtualThread");
        if (rankMethod.ReturnType.ToString() != "int" || rankMethod.ParameterList.Parameters.Count != 2 ||
            !rankMethod.Modifiers.ToString().Contains("static", StringComparison.Ordinal) ||
            rankMethod.ParameterList.Parameters[0].Type?.ToString() != "int" ||
            rankMethod.ParameterList.Parameters[0].Identifier.ValueText != "scanIndex" ||
            rankMethod.ParameterList.Parameters[1].Type?.ToString() != "bool" ||
            rankMethod.ParameterList.Parameters[1].Identifier.ValueText != "forceTdm" ||
            rankMethod.Body?.Statements is not [ReturnStatementSyntax rankReturn] ||
            rankReturn.NormalizeWhitespace().ToFullString() != "return forceTdm ? ((scanIndex + 1) % SMT_WAYS) : scanIndex;")
            throw new InvalidDataException("Legacy four-way rank formula or signature changed.");
        var smtTree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(methodBytes));
        var schedulerMethod = smtTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "PackBundleIntraCoreSmt" && m.ParameterList.Parameters.Count == 5);
        var schedulerTree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(schedulerBytes));
        var schedulerOwner = schedulerTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "MicroOpScheduler" &&
                c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core");
        EnumDeclarationSyntax fspStages = schedulerOwner.Members.OfType<EnumDeclarationSyntax>()
            .Single(e => e.Identifier.ValueText == "FspPipelineStage");
        if (fspStages.Members.Count != 2 || fspStages.Members[0].Identifier.ValueText != "SCHED1" ||
            fspStages.Members[0].EqualsValue?.Value.ToString() != "0" ||
            fspStages.Members[1].Identifier.ValueText != "SCHED2" ||
            fspStages.Members[1].EqualsValue?.Value.ToString() != "1")
            throw new InvalidDataException("FSP stage enum no longer covers the two expected early-return stages.");
        bool ReturnsBundle(IfStatementSyntax statement) => statement.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Any(r => r.Expression?.ToString() == "result");
        var sched2 = schedulerMethod.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.NormalizeWhitespace().ToFullString() == "PipelinedFspEnabled && _fspCurrentStage == FspPipelineStage.SCHED2");
        var sched1 = schedulerMethod.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.NormalizeWhitespace().ToFullString() == "PipelinedFspEnabled && _fspCurrentStage == FspPipelineStage.SCHED1");
        var boundary = schedulerMethod.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "IsSmtBundleBlockedByBoundaryGuard(bundleCert, bundleMetadata, boundaryGuard)");
        if (!ReturnsBundle(sched2) || !ReturnsBundle(sched1) || !ReturnsBundle(boundary) ||
            schedulerMethod.DescendantNodes().OfType<IfStatementSyntax>().ToList().IndexOf(sched2) >=
            schedulerMethod.DescendantNodes().OfType<IfStatementSyntax>().ToList().IndexOf(sched1) ||
            schedulerMethod.DescendantNodes().OfType<IfStatementSyntax>().ToList().IndexOf(sched1) >=
            schedulerMethod.DescendantNodes().OfType<IfStatementSyntax>().ToList().IndexOf(boundary))
            throw new InvalidDataException("Scheduler FSP or boundary early-return gate changed.");
        IfStatementSyntax rankingBranch = schedulerMethod.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "CreditFairnessEnabled" && i.Else is not null &&
                i.Statement.ToString().Contains("GetCreditRankedOrder", StringComparison.Ordinal));
        const string expectedBranch = "if (CreditFairnessEnabled) { GetCreditRankedOrder(ownerVirtualThreadId, vtOrder); } else { for (int i = 0; i < SMT_WAYS; i++) vtOrder[i] = GetRankedVirtualThread(i, forceTdm); }";
        static bool SameTokens(SyntaxNode left, SyntaxNode right) =>
            left.DescendantTokens().Select(t => t.Text).SequenceEqual(right.DescendantTokens().Select(t => t.Text));
        if (!SameTokens(rankingBranch, SyntaxFactory.ParseStatement(expectedBranch)))
            throw new InvalidDataException("Credit/legacy ranking branch or call site changed.");
        HwDesign sourceDesign = counter.Design.Design;
        HwModule sourceModule = sourceDesign.Modules.Single();
        HwRegisterNode register = sourceModule.Nodes.OfType<HwRegisterNode>().Single();
        HwSourceOrigin counterOrigin = sourceModule.Origin
            ?? throw new InvalidDataException("Scheduler counter module must retain source provenance.");
        string sourceDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "scheduler-postincrement-tdm-phase/v1|" + counter.SchedulerSha256 + "|" + counter.MethodSha256)))
            .ToLowerInvariant();
        HwSourceOrigin origin = counterOrigin with
        {
            IdentityDigest = sourceDigest,
            Assembly = "CircuitWeave.SchedulerTdmPhase",
            Type = "YAKSys_Hybrid_CPU.Core.MicroOpScheduler",
            Member = "PackBundleIntraCoreSmt"
        };
        var bit = new HwBitVectorType(1);
        var u64 = new HwBitVectorType(64);
        var u2 = new HwBitVectorType(2);
        var ports = sourceModule.Ports.Where(p => p.Name != "cycle_count").ToList();
        ports.Add(new("pipelined_fsp_enabled", HwPortDirection.Input, bit));
        ports.Add(new("boundary_guard_blocked", HwPortDirection.Input, bit));
        ports.Add(new("smt_port_valid_mask", HwPortDirection.Input, new HwBitVectorType(8)));
        ports.Add(new("smt_port_nonnull_mask", HwPortDirection.Input, new HwBitVectorType(8)));
        ports.Add(new("eligible_virtual_thread_mask", HwPortDirection.Input, new HwBitVectorType(8)));
        ports.Add(new("credit_fairness_enabled", HwPortDirection.Input, bit));
        ports.Add(new("cycle_count", HwPortDirection.Output, u64));
        ports.Add(new("is_tdm_slot", HwPortDirection.Output, bit));
        ports.Add(new("force_tdm", HwPortDirection.Output, bit));
        ports.Add(new("legacy_tdm_rotate", HwPortDirection.Output, bit));
        for (int i = 0; i < 4; i++) ports.Add(new($"legacy_rank_{i}", HwPortDirection.Output, u2));

        HwConstantNode[] constants = sourceModule.Nodes.OfType<HwConstantNode>().ToArray();
        HwOperationNode[] operations = sourceModule.Nodes.OfType<HwOperationNode>().ToArray();
        HwNode[] nodes =
        [
            .. constants.Select(c => c with { Origin = origin }),
            new HwConstantNode("period_mask", u64, new string('0', 58) + new string('1', 6), origin),
            new HwConstantNode("zero_u64", u64, new string('0', 64), origin),
            new HwConstantNode("eligible_mask", new HwBitVectorType(8), "00001111", origin),
            new HwConstantNode("background_mask", new HwBitVectorType(8), "00001110", origin),
            new HwConstantNode("zero_u8", new HwBitVectorType(8), "00000000", origin),
            .. Enumerable.Range(0, 4).Select(i => new HwConstantNode($"rank{i}_base", u2,
                Convert.ToString(i, 2).PadLeft(2, '0'), origin)),
            .. Enumerable.Range(0, 4).Select(i => new HwConstantNode($"rank{i}_rotated", u2,
                Convert.ToString((i + 1) % 4, 2).PadLeft(2, '0'), origin)),
            .. operations.Select(o => o with { Origin = origin }),
            new HwOperationNode("count_phase", "and", [register.Id, "period_mask"], u64, Origin: origin),
            new HwOperationNode("phase_zero", "eq", ["count_phase", "zero_u64"], bit, Origin: origin),
            new HwOperationNode("pipeline_disabled", "not", ["pipelined_fsp_enabled"], bit, Origin: origin),
            new HwOperationNode("boundary_clear", "not", ["boundary_guard_blocked"], bit, Origin: origin),
            new HwOperationNode("outer_gates_clear", "and", ["pipeline_disabled", "boundary_clear"], bit, Origin: origin),
            new HwOperationNode("decode_enabled", "and", ["valid_call", "outer_gates_clear"], bit, Origin: origin),
            new HwOperationNode("tdm_slot_value", "and", ["decode_enabled", "phase_zero"], bit, Origin: origin),
            new HwOperationNode("ready_port_mask", "and", ["smt_port_valid_mask", "smt_port_nonnull_mask"], new HwBitVectorType(8), Origin: origin),
            new HwOperationNode("eligible_normalized", "and", ["eligible_virtual_thread_mask", "eligible_mask"], new HwBitVectorType(8), Origin: origin),
            new HwOperationNode("visible_ready_mask", "and", ["ready_port_mask", "eligible_normalized"], new HwBitVectorType(8), Origin: origin),
            new HwOperationNode("background_candidates", "and", ["visible_ready_mask", "background_mask"], new HwBitVectorType(8), Origin: origin),
            new HwOperationNode("background_none", "eq", ["background_candidates", "zero_u8"], bit, Origin: origin),
            new HwOperationNode("background_ready", "not", ["background_none"], bit, Origin: origin),
            new HwOperationNode("force_tdm_value", "and", ["tdm_slot_value", "background_ready"], bit, Origin: origin),
            new HwOperationNode("credit_disabled", "not", ["credit_fairness_enabled"], bit, Origin: origin),
            new HwOperationNode("legacy_rotate_value", "and", ["force_tdm_value", "credit_disabled"], bit, Origin: origin),
            .. Enumerable.Range(0, 4).Select(i => new HwOperationNode($"legacy_rank_value{i}", "mux",
                ["legacy_rotate_value", $"rank{i}_rotated", $"rank{i}_base"], u2, Origin: origin)),
            .. sourceModule.Nodes.OfType<HwRegisterNode>().Select(r => r with { Origin = origin }),
            new HwOutputDriveNode("drive_count", "cycle_count", register.Id, origin),
            new HwOutputDriveNode("drive_tdm_slot", "is_tdm_slot", "tdm_slot_value", origin),
            new HwOutputDriveNode("drive_force_tdm", "force_tdm", "force_tdm_value", origin),
            new HwOutputDriveNode("drive_legacy_tdm_rotate", "legacy_tdm_rotate", "legacy_rotate_value", origin),
            .. Enumerable.Range(0, 4).Select(i => new HwOutputDriveNode($"drive_legacy_rank_{i}",
                $"legacy_rank_{i}", $"legacy_rank_value{i}", origin))
        ];
        var contract = new HwHardwareContract("hybridcpu.scheduler.tdm-legacy-rank.initial-enable.v1",
            HwModuleKind.Clocked, ports, new HwTimingContract(1, 1, "call_clock"));
        var module = sourceModule with
        {
            Id = "scheduler_tdm_legacy_rank",
            Name = "scheduler_tdm_legacy_rank",
            Ports = ports,
            Nodes = nodes,
            Contract = contract,
            Origin = origin
        };
        var design = sourceDesign with
        {
            TopModule = module.Id,
            Modules = [module],
            Toolchain = sourceDesign.Toolchain with { HardwareLanguageProfile = "hwp-clocked-init-enable-phase-output-v1" }
        };
        HwValidationResult validation = new FailClosedHardwareIrValidator().Validate(design);
        if (!validation.Accepted || validation.Validated is null)
            throw new InvalidDataException("Scheduler TDM phase HWIR refused: " +
                string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));

        return new(validation.Validated, counter.SchedulerSha256, counter.MethodSha256,
            admissionHash, eligibilityHash, counter.SmtWays, counter.TdmPeriod,
            "The phase is enabled only for a valid call when PipelinedFspEnabled is false and the current boundary guard allows the bundle. This collapses the checked exhaustive SCHED1/SCHED2 early-return enum; boundary_guard_blocked remains an explicit per-call input from the runtime legality service. smt_port_valid_mask and smt_port_nonnull_mask represent the per-VT source conditions _smtPortValid[vt] and _smtPorts[vt] != null; HWIR intersects them, normalizes the FSM runnable mask to SMT_WAYS=4, intersects readiness with eligibility, then tests non-owner bits 1..3 for background readiness. Four two-bit rank outputs encode GetRankedVirtualThread(i, forceTdm) for the consumed legacy branch; credit ranking ignores these legacy ranks. Credit-ranked order and admission, boundary decision logic, physical reset/clock mapping, and target initialization qualification remain outside this candidate.");
    }
}
#endif
