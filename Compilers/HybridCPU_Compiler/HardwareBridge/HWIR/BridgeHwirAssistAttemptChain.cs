#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>
/// Four ordered Assist candidate-control attempts. Stage outcomes, port facts,
/// reservation decisions and rank values are sampled; this models retry/stop
/// control only and does not claim to evaluate admission or reservation state.
/// </summary>
public static class BridgeHwirAssistAttemptChain
{
    public const string AssistSourceSha256 = "8515039c3d770382fd448439d2453c01bd42397f3b525b413d411161bf8035a8";
    public const int AssistBundleQuota = 1;

    public static HwValidatedDesign Compile(byte[] assistSource, string commit, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(assistSource);
        string sourceHash = Hash(assistSource);
        if (sourceHash != AssistSourceSha256 || commit != "1bfe41a49cf11c80fa6c6aee87accb54b78b331f")
            throw new InvalidDataException("Assist attempt-chain source bytes or model identity changed.");

        SyntaxNode root = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(assistSource)).GetRoot();
        ClassDeclarationSyntax owner = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c =>
            c.Identifier.ValueText == "MicroOpScheduler" &&
            c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core");
        FieldDeclarationSyntax quota = owner.Members.OfType<FieldDeclarationSyntax>().Single(f =>
            f.Declaration.Variables.Any(v => v.Identifier.ValueText == "AssistBundleQuota"));
        if (quota.Declaration.Type.ToString() != "int" ||
            quota.Declaration.Variables.Single(v => v.Identifier.ValueText == "AssistBundleQuota").Initializer?.Value.ToString() != "1")
            throw new InvalidDataException("AssistBundleQuota no longer equals one.");

        MethodDeclarationSyntax method = owner.Members.OfType<MethodDeclarationSyntax>().Single(m =>
            m.Identifier.ValueText == "TryInjectAssistCandidates");
        BlockSyntax body = method.Body ?? throw new InvalidDataException("Assist injection body is missing.");
        ForStatementSyntax loop = body.Statements.OfType<ForStatementSyntax>().Single(f =>
            f.Condition?.ToString() == "orderIndex < SMT_WAYS && assistInjectedCount < AssistBundleQuota");
        string loopTokens = string.Concat(loop.DescendantTokens().Select(t => t.Text));
        string[] order =
        [
            "intvtId=vtOrder[orderIndex];",
            "vtId==ownerVirtualThreadId",
            "!_smtAssistPortValid[vtId]",
            "_smtAssistPorts[vtId]==null",
            "TryClassAdmission(",
            "RecordAssistReject(candidate,rejectA);continue;",
            "TryMaterializeLane(",
            "RecordAssistReject(candidate,rejectB);continue;",
            "TryReserveAssistBackpressure(",
            "TryReserveAssistQuota(",
            "assistInjectedCount++"
        ];
        int prior = -1;
        foreach (string fact in order)
        {
            int at = loopTokens.IndexOf(fact, prior + 1, StringComparison.Ordinal);
            if (at <= prior) throw new InvalidDataException("Assist ordered candidate gate/commit sequence changed at " + fact);
            prior = at;
        }

        string schedulerText = string.Concat(body.DescendantTokens().Select(t => t.Text));
        if (!schedulerText.Contains("if(!AssistInjectionEnabled){ClearAssistNominationPorts();return0;}", StringComparison.Ordinal) ||
            !schedulerText.Contains("if(ResolveNextInjectableSlot(opportunityState,0)<0){ClearAssistNominationPorts();return0;}", StringComparison.Ordinal) ||
            !schedulerText.Contains("if(IsSmtBundleBlockedByBoundaryGuard(bundleCert,bundleMetadata,boundaryGuard)){AssistBoundaryRejects+=CountReadyAssistCandidates(ownerVirtualThreadId);ClearAssistNominationPorts();return0;}", StringComparison.Ordinal))
            throw new InvalidDataException("Assist outer entry gates changed.");

        FileLinePositionSpan line = method.Identifier.GetLocation().GetLineSpan();
        HwSourceOrigin origin = new("https://github.com/yuriyyak23/HybridCPU-v2", commit,
            "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Assist/Runtime/MicroOpScheduler.Assist.cs",
            "CircuitWeave.AssistAttemptChain", "YAKSys_Hybrid_CPU.Core.MicroOpScheduler", "TryInjectAssistCandidates",
            Hash(Encoding.UTF8.GetBytes("assist-attempt-chain/v1|" + sourceHash)), HwSourceRole.Primary,
            line.StartLinePosition.Line + 1, line.StartLinePosition.Character + 1);

        HwBitVectorType bit = new(1), u2 = new(2), u32 = new(32), u64 = new(64);
        var ports = new List<HwPortContract>
        {
            new("assist_enabled", HwPortDirection.Input, bit),
            new("slot_available", HwPortDirection.Input, bit),
            new("boundary_blocked", HwPortDirection.Input, bit),
            new("typed_slot_enabled", HwPortDirection.Input, bit),
            new("owner_vt", HwPortDirection.Input, u32),
            new("injection_count", HwPortDirection.Output, u2),
            new("final_scan_active", HwPortDirection.Output, bit),
            new("rank3_unqualified_reached", HwPortDirection.Output, bit),
            new("rank3_out_of_range_reached", HwPortDirection.Output, bit)
        };
        for (int i = 0; i < 4; i++)
        {
            ports.Add(new($"rank{i}_defined", HwPortDirection.Input, bit));
            ports.Add(new($"rank{i}_vt", HwPortDirection.Input, u32));
            ports.Add(new($"rank{i}_port_valid", HwPortDirection.Input, bit));
            ports.Add(new($"rank{i}_port_nonnull", HwPortDirection.Input, bit));
            ports.Add(new($"rank{i}_stage_a_pass", HwPortDirection.Input, bit));
            ports.Add(new($"rank{i}_stage_b_pass", HwPortDirection.Input, bit));
            ports.Add(new($"rank{i}_is_assist_op", HwPortDirection.Input, bit));
            ports.Add(new($"rank{i}_backpressure_pass", HwPortDirection.Input, bit));
            ports.Add(new($"rank{i}_quota_pass", HwPortDirection.Input, bit));
            ports.Add(new($"rank{i}_legacy_legal", HwPortDirection.Input, bit));
            ports.Add(new($"rank{i}_legacy_dma_sensitive", HwPortDirection.Input, bit));
            ports.Add(new($"rank{i}_reached", HwPortDirection.Output, bit));
            ports.Add(new($"rank{i}_candidate_valid", HwPortDirection.Output, bit));
            ports.Add(new($"rank{i}_committed", HwPortDirection.Output, bit));
            ports.Add(new($"rank{i}_stage_a_rejected", HwPortDirection.Output, bit));
            ports.Add(new($"rank{i}_stage_b_rejected", HwPortDirection.Output, bit));
            ports.Add(new($"rank{i}_backpressure_rejected", HwPortDirection.Output, bit));
            ports.Add(new($"rank{i}_quota_rejected", HwPortDirection.Output, bit));
            ports.Add(new($"rank{i}_legacy_rejected", HwPortDirection.Output, bit));
        }

        var nodes = new List<HwNode>
        {
            new HwConstantNode("one", bit, "1", origin),
            new HwConstantNode("zero", bit, "0", origin),
            new HwConstantNode("one_u2", u2, "01", origin),
            new HwConstantNode("four64", u64, Convert.ToString(4, 2).PadLeft(64, '0'), origin),
            new HwConstantNode("initial_injection_count", u2, "00", origin)
        };
        nodes.Add(new HwOperationNode("not_boundary", "xor", ["boundary_blocked", "one"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("entry_enabled", "and", ["assist_enabled", "slot_available"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("entry_open", "and", ["entry_enabled", "not_boundary"], bit, Origin: origin));
        string active = "entry_open";
        string count = "initial_injection_count";
        string[] commits = new string[4];
        for (int i = 0; i < 4; i++)
        {
            string prefix = $"rank{i}_";
            nodes.Add(new HwOperationNode(prefix + "vt64", "zext", [prefix + "vt"], u64, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "rank_in_range", "ult64", [prefix + "vt64", "four64"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "is_owner", "eq", [prefix + "vt", "owner_vt"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "not_owner", "xor", [prefix + "is_owner", "one"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "port_ready", "and", [prefix + "port_valid", prefix + "port_nonnull"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "rank_usable", "and", [prefix + "defined", prefix + "rank_in_range"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "candidate_valid_wire", "and", [prefix + "rank_usable", prefix + "not_owner"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "candidate_port_valid", "and", [prefix + "candidate_valid_wire", prefix + "port_ready"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "reached_wire", "and", [active, prefix + "defined"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "candidate_reached", "and", [prefix + "reached_wire", prefix + "candidate_port_valid"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "typed_stage_a_reject", "and", [prefix + "candidate_reached", "typed_slot_enabled"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "stage_a_fail", "xor", [prefix + "stage_a_pass", "one"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "stage_a_reject", "and", [prefix + "typed_stage_a_reject", prefix + "stage_a_fail"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "stage_b_input", "and", [prefix + "candidate_reached", "typed_slot_enabled"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "stage_b_fail", "xor", [prefix + "stage_b_pass", "one"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "stage_b_reject_after_a", "and", [prefix + "stage_b_input", prefix + "stage_a_pass"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "stage_b_reject", "and", [prefix + "stage_b_reject_after_a", prefix + "stage_b_fail"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "assist_backpressure_gate", "and", [prefix + "candidate_reached", prefix + "is_assist_op"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "not_assist_op", "xor", [prefix + "is_assist_op", "one"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "bp_fail", "xor", [prefix + "backpressure_pass", "one"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "bp_after_a", "and", [prefix + "assist_backpressure_gate", prefix + "stage_a_pass"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "bp_after_ab", "and", [prefix + "bp_after_a", prefix + "stage_b_pass"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "bp_reject", "and", [prefix + "bp_after_ab", prefix + "bp_fail"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "quota_fail", "xor", [prefix + "quota_pass", "one"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "quota_after_bp", "and", [prefix + "bp_after_ab", prefix + "backpressure_pass"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "quota_gate", "and", [prefix + "quota_after_bp", prefix + "quota_fail"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_mode", "xor", ["typed_slot_enabled", "one"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_legal_gate", "and", [prefix + "candidate_reached", prefix + "legacy_mode"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_not_legal", "xor", [prefix + "legacy_legal", "one"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_assist_gate", "and", [prefix + "legacy_legal_gate", prefix + "is_assist_op"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_dma_reject", "and", [prefix + "legacy_assist_gate", prefix + "legacy_dma_sensitive"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_legality_reject", "and", [prefix + "legacy_legal_gate", prefix + "legacy_not_legal"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_reservation_gate", "and", [prefix + "legacy_legal_gate", prefix + "legacy_legal"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_no_dma_reject", "xor", [prefix + "legacy_dma_reject", "one"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_bp_gate_after_dma", "and", [prefix + "legacy_reservation_gate", prefix + "legacy_no_dma_reject"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_bp_gate", "and", [prefix + "legacy_bp_gate_after_dma", prefix + "is_assist_op"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_bp_ok", "or", [prefix + "not_assist_op", prefix + "backpressure_pass"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_quota_ok", "or", [prefix + "not_assist_op", prefix + "quota_pass"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_bp_reject", "and", [prefix + "legacy_bp_gate", prefix + "bp_fail"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_quota_gate", "and", [prefix + "legacy_bp_gate", prefix + "backpressure_pass"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_quota_reject", "and", [prefix + "legacy_quota_gate", prefix + "quota_fail"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_reservation_reject", "or", [prefix + "legacy_bp_reject", prefix + "legacy_quota_reject"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "typed_bp_reject", "and", [prefix + "bp_reject", "typed_slot_enabled"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "typed_quota_reject", "and", [prefix + "quota_gate", "typed_slot_enabled"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "any_bp_reject", "or", [prefix + "typed_bp_reject", prefix + "legacy_bp_reject"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "any_quota_reject", "or", [prefix + "typed_quota_reject", prefix + "legacy_quota_reject"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_reject", "or", [prefix + "legacy_dma_reject", prefix + "legacy_legality_reject"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_reject_any", "or", [prefix + "legacy_reject", prefix + "legacy_reservation_reject"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "typed_reject_ab", "or", [prefix + "stage_a_reject", prefix + "stage_b_reject"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "typed_reject_reservation", "or", [prefix + "bp_reject", prefix + "quota_gate"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "typed_reject_any", "or", [prefix + "typed_reject_ab", prefix + "typed_reject_reservation"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_reject_mode", "and", [prefix + "legacy_reject_any", prefix + "legacy_mode"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "typed_reject_mode", "and", [prefix + "typed_reject_any", "typed_slot_enabled"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "reject_any", "or", [prefix + "legacy_reject_mode", prefix + "typed_reject_mode"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "typed_bp_ok", "or", [prefix + "not_assist_op", prefix + "backpressure_pass"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "typed_quota_ok", "or", [prefix + "not_assist_op", prefix + "quota_pass"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "typed_stage_ok", "and", [prefix + "stage_a_pass", prefix + "stage_b_pass"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "typed_reservation_ok", "and", [prefix + "typed_bp_ok", prefix + "typed_quota_ok"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "typed_accept", "and", [prefix + "typed_stage_ok", prefix + "typed_reservation_ok"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "typed_accept_gate", "and", [prefix + "candidate_reached", "typed_slot_enabled"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "typed_commit", "and", [prefix + "typed_accept_gate", prefix + "typed_accept"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_commit_after_bp", "and", [prefix + "legacy_reservation_gate", prefix + "legacy_no_dma_reject"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_commit_candidate", "and", [prefix + "legacy_commit_after_bp", prefix + "legacy_bp_ok"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "legacy_commit", "and", [prefix + "legacy_commit_candidate", prefix + "legacy_quota_ok"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "commit", "or", [prefix + "typed_commit", prefix + "legacy_commit"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "count_inc", "add", [count, "one_u2"], u2, Origin: origin));
            string nextCount = prefix + "count_next";
            nodes.Add(new HwMuxNode(nextCount, prefix + "commit", prefix + "count_inc", count, u2, origin));
            count = nextCount;
            string nextActive = prefix + "active_next";
            nodes.Add(new HwOperationNode(prefix + "not_commit", "xor", [prefix + "commit", "one"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(nextActive, "and", [active, prefix + "not_commit"], bit, Origin: origin));
            active = nextActive;
            commits[i] = prefix + "commit";
            nodes.Add(new HwOutputDriveNode(prefix + "drive_reached", prefix + "reached", prefix + "reached_wire", origin));
            nodes.Add(new HwOutputDriveNode(prefix + "drive_candidate_valid", prefix + "candidate_valid", prefix + "candidate_reached", origin));
            nodes.Add(new HwOutputDriveNode(prefix + "drive_commit", prefix + "committed", prefix + "commit", origin));
            nodes.Add(new HwOutputDriveNode(prefix + "drive_stage_a_reject", prefix + "stage_a_rejected", prefix + "stage_a_reject", origin));
            nodes.Add(new HwOutputDriveNode(prefix + "drive_stage_b_reject", prefix + "stage_b_rejected", prefix + "stage_b_reject", origin));
            nodes.Add(new HwOutputDriveNode(prefix + "drive_bp_reject", prefix + "backpressure_rejected", prefix + "any_bp_reject", origin));
            nodes.Add(new HwOutputDriveNode(prefix + "drive_quota_reject", prefix + "quota_rejected", prefix + "any_quota_reject", origin));
            nodes.Add(new HwOutputDriveNode(prefix + "drive_legacy_reject", prefix + "legacy_rejected", prefix + "legacy_reject_any", origin));
        }
        nodes.Add(new HwOutputDriveNode("drive_injection_count", "injection_count", count, origin));
        nodes.Add(new HwOutputDriveNode("drive_final_active", "final_scan_active", active, origin));
        nodes.Add(new HwOperationNode("rank3_undefined", "xor", ["rank3_defined", "one"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("rank3_unqualified", "and", ["rank3_active_next", "rank3_undefined"], bit, Origin: origin));
        nodes.Add(new HwOutputDriveNode("drive_rank3_unqualified", "rank3_unqualified_reached", "rank3_unqualified", origin));
        nodes.Add(new HwOperationNode("rank3_out_of_range", "xor", ["rank3_rank_in_range", "one"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("rank3_oob_without_owner", "and", ["rank3_out_of_range", "rank3_not_owner"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("rank3_oob", "and", ["rank3_reached_wire", "rank3_oob_without_owner"], bit, Origin: origin));
        nodes.Add(new HwOutputDriveNode("drive_rank3_oob", "rank3_out_of_range_reached", "rank3_oob", origin));

        var module = new HwModule("assist_ordered_attempt_chain", "assist_ordered_attempt_chain", ports, nodes, [],
            new HwHardwareContract("hybridcpu.assist.ordered-attempt-chain.sampled-outcomes.v1", HwModuleKind.Combinational,
                ports, new HwTimingContract(0, 1)), origin);
        var validation = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", module.Id, [module], toolchain, []));
        return validation.Validated ?? throw new InvalidDataException("Assist ordered attempt chain HWIR refused: " +
            string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
