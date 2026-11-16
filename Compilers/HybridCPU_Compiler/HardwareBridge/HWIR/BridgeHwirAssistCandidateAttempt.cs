#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>
/// Composes source-bound typed admission and Assist reservation for one candidate.
/// This is a combinational candidate attempt; it does not cascade state across ranks.
/// </summary>
public static class BridgeHwirAssistCandidateAttempt
{
    public static HwValidatedDesign Compile(
        HwValidatedDesign admission,
        HwValidatedDesign assistQuota,
        HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(assistQuota);
        ArgumentNullException.ThrowIfNull(toolchain);

        HwModule admissionTop = admission.Design.Modules.Single(m => m.Id == admission.Design.TopModule);
        HwModule quotaTop = assistQuota.Design.Modules.Single(m => m.Id == assistQuota.Design.TopModule);
        if (admissionTop.Id != "smt_candidate_admission_projection" ||
            quotaTop.Id != "assist_memory_quota_reservation")
            throw new InvalidDataException("Assist candidate composition received unexpected child designs.");
        if (!Equals(admission.Design.Toolchain, assistQuota.Design.Toolchain) ||
            !Equals(admission.Design.Toolchain, toolchain))
            throw new InvalidDataException("Assist candidate child toolchain identities differ.");

        HwPortContract AdmissionPort(string name, HwPortDirection direction) => admissionTop.Ports.Single(p => p.Name == name && p.Direction == direction);
        HwPortContract QuotaPort(string name, HwPortDirection direction) => quotaTop.Ports.Single(p => p.Name == name && p.Direction == direction);
        HwSourceOrigin childOrigin = admissionTop.Origin ?? quotaTop.Origin ??
            throw new InvalidDataException("Assist candidate child source origin is missing.");
        string identity = Hash(Encoding.UTF8.GetBytes("assist-candidate-attempt/v1|" + admission.CanonicalDigest + "|" + assistQuota.CanonicalDigest));
        HwSourceOrigin origin = childOrigin with
        {
            Assembly = "CircuitWeave.AssistCandidateAttempt",
            Type = "AssistCandidateAttempt",
            Member = "ComposeAdmissionAndReservation",
            IdentityDigest = identity
        };

        var ports = new List<HwPortContract>();
        var nodes = new List<HwNode>();
        var bit = new HwBitVectorType(1);
        var attemptEnable = new HwPortContract("candidate_attempt_enable", HwPortDirection.Input, bit);
        var isAssistCandidate = new HwPortContract("is_assist_candidate", HwPortDirection.Input, bit);
        ports.Add(attemptEnable);
        ports.Add(isAssistCandidate);
        nodes.Add(new HwConstantNode("attempt_zero", bit, "0", origin));
        nodes.Add(new HwConstantNode("attempt_one", bit, "1", origin));
        nodes.Add(new HwOperationNode("attempt_not_assist", "xor", ["is_assist_candidate", "attempt_one"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("assist_reservation_enable", "and", ["candidate_attempt_enable", "is_assist_candidate"], bit, Origin: origin));
        var admissionConnections = new Dictionary<string, string>(StringComparer.Ordinal);
        var quotaConnections = new Dictionary<string, string>(StringComparer.Ordinal);
        var admissionOutputSignals = new Dictionary<string, string>(StringComparer.Ordinal);
        var quotaOutputSignals = new Dictionary<string, string>(StringComparer.Ordinal);
        var deferredAdmissionStateOutputs = new List<(string Output, string Signal, string Current, HwBitVectorType Type)>();

        foreach (HwPortContract p in admissionTop.Ports.Where(p => p.Direction == HwPortDirection.Input))
        {
            if (p.Name == "is_assist")
            {
                if (!Equals(p.Type, isAssistCandidate.Type))
                    throw new InvalidDataException("Admission is_assist input does not match the candidate type-test control.");
                admissionConnections.Add(p.Name, "is_assist_candidate");
                continue;
            }
            string parentName = "admission_" + p.Name;
            ports.Add(p with { Name = parentName });
            admissionConnections.Add(p.Name, parentName);
        }

        foreach (HwPortContract p in admissionTop.Ports.Where(p => p.Direction == HwPortDirection.Output))
        {
            string signal = "admission_internal_" + p.Name;
            nodes.Add(new HwWireNode(signal, p.Type, origin));
            admissionOutputSignals.Add(p.Name, signal);
            admissionConnections.Add(p.Name, signal);
            string currentState = p.Name switch
            {
                "next_occupancy" => "occupancy",
                _ when p.Name.StartsWith("next_", StringComparison.Ordinal) => p.Name[5..],
                _ => string.Empty
            };
            bool deferredState = currentState is "occupancy" or "remaining_memory_issue_budget" or
                "remaining_read_issue_budget" or "remaining_write_issue_budget" or
                "consumed_memory_budget_by_bank" or "consumed_read_budget_by_bank" or
                "consumed_write_budget_by_bank" ||
                currentState.StartsWith("occupied", StringComparison.Ordinal);
            if (deferredState)
            {
                if (!admissionTop.Ports.Any(input => input.Name == currentState && input.Direction == HwPortDirection.Input))
                    throw new InvalidDataException("Admission deferred state has no matching current-state input: " + p.Name);
                string deferredName = "committed_" + p.Name;
                ports.Add(p with { Name = deferredName });
                deferredAdmissionStateOutputs.Add((deferredName, signal, "admission_" + currentState, p.Type));
                continue;
            }
            string parentName = "admission_" + p.Name;
            ports.Add(p with { Name = parentName });
            if (p.Name == "template_next")
            {
                string current = "admission_template_remaining";
                if (!admissionTop.Ports.Any(input => input.Name == "template_remaining" && input.Direction == HwPortDirection.Input))
                    throw new InvalidDataException("Admission template next output has no current-state input.");
                nodes.Add(new HwMuxNode("gate_" + parentName, "candidate_attempt_enable", signal, current, p.Type, origin));
                nodes.Add(new HwOutputDriveNode("drive_" + parentName, parentName, "gate_" + parentName, origin));
            }
            else if (p.Type.Width == 1)
            {
                nodes.Add(new HwMuxNode("gate_" + parentName, "candidate_attempt_enable", signal, "attempt_zero", p.Type, origin));
                nodes.Add(new HwOutputDriveNode("drive_" + parentName, parentName, "gate_" + parentName, origin));
            }
            else
            {
                string zero = "attempt_zero_" + parentName;
                nodes.Add(new HwConstantNode(zero, p.Type, new string('0', p.Type.Width), origin));
                nodes.Add(new HwMuxNode("gate_" + parentName, "candidate_attempt_enable", signal, zero, p.Type, origin));
                nodes.Add(new HwOutputDriveNode("drive_" + parentName, parentName, "gate_" + parentName, origin));
            }
        }

        HwPortContract stageAEarlyReject = QuotaPort("stage_a_reject_reason", HwPortDirection.Input);
        HwPortContract stageBEarlyReject = QuotaPort("stage_b_reject_reason", HwPortDirection.Input);
        HwPortContract candidateVt = QuotaPort("candidate_vt", HwPortDirection.Input);
        HwPortContract admissionVt = AdmissionPort("candidate_virtual_thread_id", HwPortDirection.Input);
        HwPortContract quotaSlotClass = QuotaPort("candidate_slot_class", HwPortDirection.Input);
        HwPortContract admissionSlotClass = AdmissionPort("slot_class", HwPortDirection.Input);
        if (stageAEarlyReject.Type.Width != stageBEarlyReject.Type.Width)
            throw new InvalidDataException("Assist reservation early-reject port widths differ.");
        if (!Equals(quotaSlotClass.Type, admissionSlotClass.Type))
            throw new InvalidDataException("Admission and Assist reservation slot-class types differ.");

        foreach (HwPortContract p in quotaTop.Ports.Where(p => p.Direction == HwPortDirection.Input))
        {
            if (p.Name is "stage_a_reject_reason" or "stage_b_reject_reason" or "candidate_vt" or "candidate_slot_class")
                continue;
            string parentName = "quota_" + p.Name;
            ports.Add(p with { Name = parentName });
            quotaConnections.Add(p.Name, parentName);
        }
        quotaConnections.Add("candidate_slot_class", "admission_slot_class");

        nodes.Add(new HwConstantNode("zero_stage_a_reject", stageAEarlyReject.Type, new string('0', stageAEarlyReject.Type.Width), origin));
        nodes.Add(new HwConstantNode("zero_stage_b_reject", stageBEarlyReject.Type, new string('0', stageBEarlyReject.Type.Width), origin));
        nodes.Add(new HwMuxNode("quota_stage_a_reject_reason", "admission_internal_stage_b_evaluated",
            "zero_stage_a_reject", "admission_internal_reject_reason", stageAEarlyReject.Type, origin));
        nodes.Add(new HwMuxNode("quota_stage_b_reject_reason", "admission_internal_stage_b_evaluated",
            "admission_internal_reject_reason", "zero_stage_b_reject", stageBEarlyReject.Type, origin));
        HwBitVectorType admissionReasonType = AdmissionPort("reject_reason", HwPortDirection.Output).Type;
        if (admissionReasonType.Width != stageAEarlyReject.Type.Width)
            throw new InvalidDataException("Admission and Assist reservation reject reason widths differ.");
        nodes.Add(new HwConstantNode("attempt_reject_zero", admissionReasonType, new string('0', admissionReasonType.Width), origin));
        nodes.Add(new HwOperationNode("admission_early_reject_is_zero", "eq",
            ["admission_internal_reject_reason", "attempt_reject_zero"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("admission_early_reject", "xor",
            ["admission_early_reject_is_zero", "attempt_one"], bit, Origin: origin));
        quotaConnections.Add("stage_a_reject_reason", "quota_stage_a_reject_reason");
        quotaConnections.Add("stage_b_reject_reason", "quota_stage_b_reject_reason");
        string quotaVtSignal = "admission_candidate_virtual_thread_id";
        if (!Equals(candidateVt.Type, admissionVt.Type))
        {
            if (candidateVt.Type.Signedness != admissionVt.Type.Signedness || candidateVt.Type.FourState != admissionVt.Type.FourState)
                throw new InvalidDataException("Assist reservation and admission VT signedness/four-state differ.");
            string converted = "quota_candidate_vt_converted";
            if (candidateVt.Type.Width < admissionVt.Type.Width)
            {
                if (admissionVt.Type.Width > 64)
                    throw new InvalidDataException("Assist reservation candidate VT is wider than the supported range guard.");
                nodes.Add(new HwOperationNode(converted, "trunc", [quotaVtSignal], candidateVt.Type, Origin: origin));
                var u64 = new HwBitVectorType(64);
                nodes.Add(new HwOperationNode("candidate_vt_u64", "zext", [quotaVtSignal], u64, Origin: origin));
                nodes.Add(new HwConstantNode("candidate_vt_four64", u64, new string('0', 61) + "100", origin));
                nodes.Add(new HwOperationNode("candidate_vt_in_range", "ult64", ["candidate_vt_u64", "candidate_vt_four64"], new HwBitVectorType(1), Origin: origin));
                string sentinel = new string('1', candidateVt.Type.Width);
                nodes.Add(new HwConstantNode("candidate_vt_invalid_sentinel", candidateVt.Type, sentinel, origin));
                nodes.Add(new HwMuxNode("quota_candidate_vt_checked", "candidate_vt_in_range", converted,
                    "candidate_vt_invalid_sentinel", candidateVt.Type, origin));
                converted = "quota_candidate_vt_checked";
            }
            else
            {
                nodes.Add(new HwOperationNode(converted, "zext", [quotaVtSignal], candidateVt.Type, Origin: origin));
            }
            quotaVtSignal = converted;
        }
        quotaConnections.Add("candidate_vt", quotaVtSignal);

        foreach (HwPortContract p in quotaTop.Ports.Where(p => p.Direction == HwPortDirection.Output))
        {
            string signal = "quota_internal_" + p.Name;
            nodes.Add(new HwWireNode(signal, p.Type, origin));
            quotaOutputSignals.Add(p.Name, signal);
            quotaConnections.Add(p.Name, signal);
            string parentName = "quota_" + p.Name;
            ports.Add(p with { Name = parentName });
            string visibleSignal = signal;
            if (p.Name == "accept")
            {
                nodes.Add(new HwMuxNode("quota_accept_for_type", "is_assist_candidate", signal, "attempt_one", p.Type, origin));
                nodes.Add(new HwMuxNode("quota_accept_for_enable", "candidate_attempt_enable", "quota_accept_for_type", "attempt_zero", p.Type, origin));
                visibleSignal = "quota_accept_for_enable";
            }
            else if (IsReservationEvent(p.Name))
            {
                string zero = "attempt_event_zero_" + p.Name;
                nodes.Add(new HwConstantNode(zero, p.Type, new string('0', p.Type.Width), origin));
                nodes.Add(new HwMuxNode("gate_" + parentName, "assist_reservation_enable", signal, zero, p.Type, origin));
                visibleSignal = "gate_" + parentName;
            }
            else if (p.Name.StartsWith("next_", StringComparison.Ordinal))
            {
                string stateInput = CurrentQuotaStateName(p.Name);
                if (!quotaTop.Ports.Any(input => input.Name == stateInput && input.Direction == HwPortDirection.Input))
                    throw new InvalidDataException("Assist quota next output has no matching current state: " + p.Name);
                string condition;
                if (IsAssistReservationCounter(p.Name) || IsAssistReservationState(p.Name))
                    condition = "assist_reservation_enable";
                else if (p.Name is "next_smt_rejections_count" or "next_assist_rejects")
                {
                    nodes.Add(new HwOperationNode("assist_or_early_" + p.Name, "or",
                        ["is_assist_candidate", "admission_early_reject"], bit, Origin: origin));
                    condition = "advance_early_counters_" + p.Name;
                    nodes.Add(new HwOperationNode(condition, "and", ["candidate_attempt_enable", "assist_or_early_" + p.Name], bit, Origin: origin));
                }
                else
                    condition = "candidate_attempt_enable";
                string current = "quota_" + stateInput;
                nodes.Add(new HwMuxNode("gate_" + parentName, condition, signal, current, p.Type, origin));
                visibleSignal = "gate_" + parentName;
            }
            else
            {
                string zero = "attempt_event_zero_" + p.Name;
                nodes.Add(new HwConstantNode(zero, p.Type, new string('0', p.Type.Width), origin));
                nodes.Add(new HwMuxNode("gate_" + parentName, "assist_reservation_enable", signal, zero, p.Type, origin));
                visibleSignal = "gate_" + parentName;
            }
            nodes.Add(new HwOutputDriveNode("drive_" + parentName, parentName, visibleSignal, origin));
        }

        var finalCommitType = new HwBitVectorType(1);
        nodes.Add(new HwOperationNode("final_commit_calc", "and",
            ["admission_accepted_effective", "quota_accept_for_enable"], finalCommitType, Origin: origin));
        nodes.Add(new HwOperationNode("admission_accepted_effective", "and",
            ["candidate_attempt_enable", admissionOutputSignals["accepted"]], finalCommitType, Origin: origin));
        ports.Add(new HwPortContract("final_commit", HwPortDirection.Output, finalCommitType));
        nodes.Add(new HwOutputDriveNode("drive_final_commit", "final_commit", "final_commit_calc", origin));
        foreach (var state in deferredAdmissionStateOutputs)
        {
            string selected = "commit_" + state.Output;
            nodes.Add(new HwMuxNode(selected, "final_commit_calc", state.Signal, state.Current, state.Type, origin));
            nodes.Add(new HwOutputDriveNode("drive_" + state.Output, state.Output, selected, origin));
        }

        var instances = new[]
        {
            new HwInstanceNode("admission", admissionTop.Id, admissionConnections, new Dictionary<string, string>(), Origin: origin),
            new HwInstanceNode("assist_quota", quotaTop.Id, quotaConnections, new Dictionary<string, string>(), Origin: origin)
        };
        var childModules = admission.Design.Modules.Concat(assistQuota.Design.Modules).ToArray();
        if (childModules.Select(m => m.Id).Append("assist_candidate_attempt").Distinct(StringComparer.Ordinal).Count() != childModules.Length + 1)
            throw new InvalidDataException("Assist candidate composition contains duplicate module IDs.");
        HwModule top = new("assist_candidate_attempt", "assist_candidate_attempt", ports, nodes, instances,
            new HwHardwareContract("hybridcpu.assist.candidate-attempt.stage-a-b-backpressure-quota.v1",
                HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var result = new FailClosedHardwareIrValidator().Validate(
            new HwDesign("hwir/v0.1", top.Id, [top, .. childModules], toolchain, []));
        return result.Validated ?? throw new InvalidDataException("Assist candidate attempt HWIR refused: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));

    }

    private static bool IsAssistReservationCounter(string name) => name is
        "next_assist_backpressure_rejects" or "next_assist_backpressure_outer_rejects" or
        "next_assist_backpressure_mshr_rejects" or "next_assist_backpressure_dma_srf_rejects" or
        "next_assist_quota_rejects" or "next_assist_quota_issue_rejects" or "next_assist_quota_line_rejects";

    private static bool IsAssistReservationState(string name) => name is
        "next_issue_credits" or "next_line_credits" or "next_consumed_shared_read_packed" or
        "next_outstanding_count_vt0" or "next_outstanding_count_vt1" or
        "next_outstanding_count_vt2" or "next_outstanding_count_vt3" ||
        name.StartsWith("next_bp_", StringComparison.Ordinal) ||
        name.StartsWith("next_lsu_carrier_", StringComparison.Ordinal) ||
        name.StartsWith("next_dma_carrier_", StringComparison.Ordinal);

    private static string CurrentQuotaStateName(string output) => output switch
    {
        "next_bp_shared_credits" => "bp_shared_current",
        "next_lsu_carrier_credits" => "lsu_carrier_current",
        "next_dma_carrier_credits" => "dma_carrier_current",
        "next_consumed_shared_read_packed" => "consumed_shared_read_packed",
        "next_issue_credits" => "remaining_issue_credits",
        "next_line_credits" => "remaining_line_credits",
        _ when output.StartsWith("next_outstanding_count_", StringComparison.Ordinal) => output[5..],
        _ => output[5..]
    };

    private static bool IsReservationEvent(string name) => name is
        "backpressure_accepted" or "reject_outstanding" or "reject_shared_outer" or "reject_dma_srf" or
        "reject_issue" or "reject_lines" or "reserved_line_demand";

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
