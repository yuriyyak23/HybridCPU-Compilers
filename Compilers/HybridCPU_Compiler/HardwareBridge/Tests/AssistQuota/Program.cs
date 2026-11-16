using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using CircuitWeave.Orchestration;
using CircuitWeave.Studio.Contracts;
using CircuitWeave.Studio.GraphNavigation;
using CircuitWeave.Studio.Integration;
using CircuitWeave.Visualization;
using CircuitWeave.Visualization.Publishing;
using HybridCPU.Compiler.HardwareBridge.Hwir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using StudioArtifactKind = CircuitWeave.Visualization.StudioArtifactKind;
using StudioGraphNodeKind = CircuitWeave.Studio.Contracts.StudioGraphNodeKind;
using StudioDocumentTrust = CircuitWeave.Studio.Contracts.StudioDocumentTrust;

const string model = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE\CloseToHSL\Core\Pipeline\Assist\Runtime\AssistRuntime.cs";
const string assistModel = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE\CloseToHSL\Core\Pipeline\MicroOps\Assist\AssistMicroOp.cs";
const string schedulerModel = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE\CloseToHSL\Core\Pipeline\Assist\Runtime\MicroOpScheduler.Assist.cs";
const string admissionModel = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE\CloseToHSL\Core\Pipeline\Scheduling\Admission\MicroOpScheduler.Admission.cs";
const string reasonEnumModel = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE\CloseToHSL\Core\Pipeline\Scheduling\SlotLegality\SlotClassDefinitions.cs";
const string schedulerCoreModel = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE\CloseToHSL\Core\Pipeline\Scheduling\MicroOpScheduler.cs";
const string safetyTypesModel = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE\CloseToHSL\Core\Pipeline\Safety\SafetyVerifier.Types.cs";
const string certificateModel = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE\CloseToHSL\Core\Pipeline\Certificates\BundleResourceCertificate4Way.cs";
const string backpressureModel = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE\CloseToHSL\Core\Pipeline\Scheduling\Backpressure\MicroOpScheduler.AssistBackpressure.cs";
const string srfModel = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE\CloseToHSL\Memory\Registers\StreamRegisterFile.Assist.cs";
const string smtModel = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE\CloseToHSL\Core\Pipeline\Scheduling\Smt\MicroOpScheduler.SMT.cs";
const string modelRoot = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE";
const string bridgeRoot = @"C:\Users\Yuriy\Desktop\HybridCPU Compilers\Compilers\HybridCPU_Compiler\HardwareBridge";
const string commit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
if (args.Length == 3 && args[0] == "--publish")
{
    await PublishCandidate(args[1], args[2]);
    return;
}
int emitIndex = Array.IndexOf(args, "--emit");
string? emitDirectory = emitIndex >= 0 && emitIndex + 1 < args.Length ? Path.GetFullPath(args[emitIndex + 1]) : null;
if (emitIndex >= 0 && emitDirectory is null) throw new ArgumentException("--emit requires an output directory.");
if (emitDirectory is not null && !emitDirectory.StartsWith(bridgeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Assist candidate output must stay under the HardwareBridge workspace.");
byte[] source = await File.ReadAllBytesAsync(model);
byte[] assistSource = await File.ReadAllBytesAsync(assistModel);
byte[] schedulerSource = await File.ReadAllBytesAsync(schedulerModel);
byte[] admissionSource = await File.ReadAllBytesAsync(admissionModel);
byte[] reasonEnumSource = await File.ReadAllBytesAsync(reasonEnumModel);
byte[] schedulerCoreSource = await File.ReadAllBytesAsync(schedulerCoreModel);
byte[] backpressureSource = await File.ReadAllBytesAsync(backpressureModel);
byte[] srfSource = await File.ReadAllBytesAsync(srfModel);
byte[] smtSource = await File.ReadAllBytesAsync(smtModel);
byte[] safetyTypesSource = await File.ReadAllBytesAsync(safetyTypesModel);
byte[] certificateSource = await File.ReadAllBytesAsync(certificateModel);
var toolchain = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
HwValidatedDesign design = BridgeHwirAssistQuota.Compile(source, assistSource, schedulerSource, admissionSource, reasonEnumSource, schedulerCoreSource, backpressureSource, srfSource, smtSource, safetyTypesSource, certificateSource, "1bfe41a49cf11c80fa6c6aee87accb54b78b331f", toolchain);
HwModule m = design.Design.Modules.Single(x => x.Id == design.Design.TopModule);
var drivers = m.Nodes.OfType<HwOutputDriveNode>().ToDictionary(x => x.OutputPort, x => x.Value, StringComparer.Ordinal);
int vectors = 0;
bool quick = args.Contains("--quick", StringComparer.Ordinal);
ulong[] lengths = quick ? [0,33,ulong.MaxValue] : [0,1,31,32,33,63,64,65,255,256,ulong.MaxValue-31,ulong.MaxValue];
foreach (ulong length in lengths)
foreach (int hot in quick ? new[] { 0,2 } : new[] { 0,1,2,4 })
foreach (int cold in quick ? new[] { 0,3 } : new[] { 0,1,3,5 })
foreach (bool isCold in quick ? new[] { false,true } : new[] { false,true })
for (int bpCase = 0; bpCase < 21; bpCase++)
for (int targetVt = 0; targetVt < 4; targetVt++)
for (int issue = 0; issue <= 2; issue++)
for (int lines = 0; lines <= (quick ? 3 : 7); lines++)
{
    int selectedCount = bpCase is 1 or 9 ? 2 : bpCase == 7 ? 7 : bpCase == 8 ? 8 : 0;
    int selectedCapacity = bpCase == 7 ? 8 : 2;
    int bankId = bpCase == 6 ? 16 : bpCase == 3 ? 0 : (lines * 2 + issue) % 16;
    uint packedInput = bpCase == 3 ? 1u : bpCase == 11 ? 1u << (bankId * 2) : 0u;
    int bankMask = bankId < 16 ? 1 << bankId : 0;
    bool isDma = bpCase is 5 or 10 or 12 or 13 or 14 or 15 or 16 or 17 or 20;
    int earlyMode = vectors % 9;
    byte stageAReason = earlyMode is >= 1 and <= 4 ? (byte)earlyMode : earlyMode == 8 ? (byte)3 : (byte)0;
    byte stageBReason = earlyMode switch { 5 => (byte)11, 6 => (byte)12, 7 => (byte)13, 8 => (byte)13, _ => (byte)0 };
    byte legalityRejectKind = (byte)((vectors / 9) % 4);
    byte certificateRejectDetail = (byte)((vectors / 27) % 3);
    byte candidateSlotClass = (byte)(vectors % 6);
    var values = new Dictionary<string, BigInteger>(StringComparer.Ordinal)
    {
        ["remaining_issue_credits"] = issue, ["remaining_line_credits"] = lines, ["prefetch_length"] = length,
        ["stage_a_reject_reason"] = stageAReason, ["stage_b_reject_reason"] = stageBReason, ["candidate_vt"] = (ulong)targetVt,
        ["last_legality_reject_kind"] = legalityRejectKind, ["last_certificate_reject_detail"] = certificateRejectDetail, ["candidate_slot_class"] = candidateSlotClass,
        ["hot_line_cap"] = hot, ["cold_line_cap"] = cold, ["locality_cold"] = isCold ? 1 : 0,
        ["bp_state_valid"] = bpCase is 4 or 10 ? 1 : 0, ["bp_shared_current"] = bpCase is 4 or 10 ? 2 : 9,
        ["lsu_carrier_current"] = bpCase == 4 ? 0 : bpCase == 10 ? 2 : 9, ["dma_carrier_current"] = bpCase == 4 ? 2 : bpCase == 10 ? 0 : 9,
        ["remaining_memory_issue_budget"] = bpCase == 2 ? 2 : bpCase == 9 ? 0 : 2,
        ["remaining_load_issue_budget"] = bpCase is 2 or 9 ? 0 : 2,
        ["policy_shared_outer_cap_credits"] = bpCase == 19 ? 0 : 2,
        ["policy_lsu_carrier_credits"] = bpCase == 19 ? 0 : 2,
        ["policy_dma_carrier_credits"] = bpCase == 20 ? 0 : 2,
        ["bank_id"] = bankId,
        ["consumed_shared_read_packed"] = packedInput, ["read_budget_at_least_one"] = bankMask, ["read_budget_at_least_two"] = bpCase == 11 ? bankMask : 0,
        ["is_dma_carrier"] = isDma ? 1 : 0,
        ["srf_file_present"] = bpCase == 17 ? 0 : 1,
        ["srf_reuse_match"] = bpCase == 12 ? 1 : 0,
        ["srf_loading_count"] = bpCase is 5 or 10 or 12 or 13 or 17 or 18 ? 1 : 0,
        ["srf_resident_count"] = bpCase is 12 or 14 or 15 or 16 or 17 or 18 ? 2 : 0,
        ["srf_has_empty_slot"] = bpCase is 12 or 15 or 17 or 18 ? 0 : 1,
        ["srf_has_assist_victim"] = bpCase is 14 or 15 or 17 or 18 ? 1 : 0,
        ["srf_resident_budget"] = 2, ["srf_loading_budget"] = 1,
        ["lsu_carrier_credits"] = bpCase == 4 ? 0 : 2, ["dma_carrier_credits"] = bpCase == 10 ? 0 : 2, ["target_vt"] = targetVt,
        ["outstanding_count_vt0"] = targetVt == 0 ? selectedCount : 0, ["outstanding_count_vt1"] = targetVt == 1 ? selectedCount : 0, ["outstanding_count_vt2"] = targetVt == 2 ? selectedCount : 0, ["outstanding_count_vt3"] = targetVt == 3 ? selectedCount : 0,
        ["outstanding_capacity_vt0"] = targetVt == 0 ? selectedCapacity : 2, ["outstanding_capacity_vt1"] = targetVt == 1 ? selectedCapacity : 2, ["outstanding_capacity_vt2"] = targetVt == 2 ? selectedCapacity : 2, ["outstanding_capacity_vt3"] = targetVt == 3 ? selectedCapacity : 2
        , ["smt_rejections_count"] = ulong.MaxValue, ["assist_rejects"] = ulong.MaxValue - 1,
        ["assist_backpressure_rejects"] = ulong.MaxValue, ["assist_backpressure_outer_rejects"] = 100,
        ["assist_backpressure_mshr_rejects"] = 200, ["assist_backpressure_dma_srf_rejects"] = 300,
        ["assist_quota_rejects"] = 400, ["assist_quota_issue_rejects"] = 500, ["assist_quota_line_rejects"] = 600
    };
    foreach (HwPortContract input in m.Ports.Where(p => p.Direction == HwPortDirection.Input)) values.TryAdd(input.Name, BigInteger.Zero);
    foreach (HwConstantNode c in m.Nodes.OfType<HwConstantNode>()) values[c.Id] = ParseBits(c.BitPattern);
    var pending = m.Nodes.Where(n => n is HwOperationNode or HwMuxNode).ToList();
    while (pending.Count != 0)
    {
        bool changed = false;
        foreach (HwNode node in pending.ToArray())
        {
            try
            {
                if (node is HwOperationNode o)
                {
                    BigInteger a = values[o.Operands[0]], b = o.Operands.Count > 1 ? values[o.Operands[1]] : BigInteger.Zero;
                    BigInteger raw = o.Opcode switch { "eq" => a == b ? 1 : 0, "ult64" => a < b ? 1 : 0, "zext" => a, "trunc" => a & 65535, "lshr64_mask6" => a >> ((int)b & 63), "shl32_mask5" => (a << ((int)b & 31)) & uint.MaxValue, "lshr32_mask5" => a >> ((int)b & 31), "add" => a+b, "or" => a | b, "xor" => a ^ b, "and" => a & b, "sub" => a - b, "mul" => a*b, _ => throw new InvalidDataException(o.Opcode) };
                    values[o.Id] = o.Opcode is "add" or "sub" or "mul" or "shl32_mask5" ? raw & ((BigInteger.One << o.ResultType.Width)-1) : raw;
                }
                else if (node is HwMuxNode mux) values[mux.Id] = values[mux.Condition] != 0 ? values[mux.WhenTrue] : values[mux.WhenFalse];
                pending.Remove(node); changed = true;
            }
            catch (KeyNotFoundException) { }
        }
        if (!changed) throw new InvalidDataException("HWIR evaluator could not resolve node dependencies: " + string.Join("; ", pending.Select(n => n.Id + "<-" + (n is HwOperationNode op ? string.Join(",",op.Operands) : n is HwMuxNode mx ? mx.Condition+","+mx.WhenTrue+","+mx.WhenFalse : "?"))));
    }
    ulong normLength = length == 0 ? 32 : length;
    ulong requested = Math.Max(1UL, unchecked(normLength + 31UL) / 32UL);
    int hotNorm = hot == 0 ? 1 : hot, coldNorm = cold == 0 ? hotNorm : cold;
    uint demand = (uint)Math.Min(requested, (ulong)(isCold ? coldNorm : hotNorm));
    byte earlyReason = stageAReason != 0 ? stageAReason : stageBReason;
    bool preRejected = earlyReason != 0;
    bool bpPass = bpCase is 0 or 6 or 7 or 11 or 12 or 15 or 17 or 18 or 19 or 20;
    bool bpAccept = !preRejected && bpPass;
    bool accepted = issue > 0 && lines >= demand && bpAccept;
    int rejectIssue = issue == 0 ? 1 : 0, rejectLines = issue > 0 && lines < demand ? 1 : 0;
    rejectIssue = bpAccept ? rejectIssue : 0; rejectLines = bpAccept ? rejectLines : 0;
    bool bpRejectEvent = !preRejected && !bpPass, quotaRejectEvent = bpAccept && (issue == 0 || lines < demand), assistRejectEvent = preRejected || bpRejectEvent || quotaRejectEvent;
    uint expectedPacked = packedInput;
    if (bpAccept && bankId < 16) { uint oldCount = (expectedPacked >> (2 * bankId)) & 3; uint nextCount = Math.Min(3u, oldCount + 1); uint shift = (uint)(2 * bankId); expectedPacked = (expectedPacked & ~(3u << (int)shift)) | (nextCount << (int)shift); }
    bool countMatches = Enumerable.Range(0,4).All(vt => values[drivers[$"next_outstanding_count_vt{vt}"]] == (vt != targetVt ? 0 : bpAccept && selectedCount < 8 ? selectedCount + 1 : selectedCount));
    if (values[drivers["backpressure_accepted"]] != (bpAccept ? 1 : 0) || values[drivers["reject_outstanding"]] != (!preRejected && (bpCase is 1 or 8 or 9) ? 1 : 0) ||
        values[drivers["reject_shared_outer"]] != (!preRejected && (bpCase is 2 or 3 or 4 or 10) ? 1 : 0) || values[drivers["reject_dma_srf"]] != (!preRejected && (bpCase is 5 or 13 or 14 or 16) ? 1 : 0) ||
        values[drivers["next_bp_shared_credits"]] != (bpAccept ? (bpCase == 19 ? 0 : 1) : (bpCase is 2 or 9 ? 0 : bpCase == 19 ? 1 : bpCase is 4 or 10 ? 2 : 2)) || values[drivers["next_lsu_carrier_credits"]] != (bpAccept && !isDma ? (bpCase == 19 ? 0 : 1) : bpCase == 4 ? 0 : bpCase == 19 ? 1 : 2) || values[drivers["next_dma_carrier_credits"]] != (bpAccept && isDma ? (bpCase == 20 ? 0 : 1) : bpCase == 10 ? 0 : bpCase == 20 ? 1 : 2) ||
        !countMatches || values[drivers["next_consumed_shared_read_packed"]] != expectedPacked ||
        values[drivers["reserved_line_demand"]] != demand || values[drivers["accept"]] != (accepted ? 1 : 0) || values[drivers["reject_issue"]] != rejectIssue ||
        values[drivers["reject_lines"]] != rejectLines || values[drivers["next_issue_credits"]] != (accepted ? issue - 1 : issue) ||
        values[drivers["next_line_credits"]] != (accepted ? lines - demand : lines))
        throw new InvalidOperationException($"Assist reservation mismatch issue={issue}, lines={lines}, demand={demand}, bpCase={bpCase}, vt={targetVt}, bank={bankId}: " + string.Join(",", new[] { "backpressure_accepted", "reject_outstanding", "reject_shared_outer", "reject_dma_srf", "next_bp_shared_credits", "next_lsu_carrier_credits", "next_dma_carrier_credits", "next_consumed_shared_read_packed", "accept", "reject_issue", "reject_lines", "next_issue_credits", "next_line_credits" }.Select(n => n+"="+values[drivers[n]])));
    bool countersMatch = values[drivers["next_smt_rejections_count"]] == Next(ulong.MaxValue,assistRejectEvent) &&
        values[drivers["next_assist_rejects"]] == Next(ulong.MaxValue-1,assistRejectEvent) &&
        values[drivers["next_assist_backpressure_rejects"]] == Next(ulong.MaxValue,bpRejectEvent) &&
        values[drivers["next_assist_backpressure_outer_rejects"]] == Next(100,!preRejected && (bpCase is 2 or 3 or 4 or 10)) &&
        values[drivers["next_assist_backpressure_mshr_rejects"]] == Next(200,!preRejected && (bpCase is 1 or 8 or 9)) &&
        values[drivers["next_assist_backpressure_dma_srf_rejects"]] == Next(300,!preRejected && (bpCase is 5 or 13 or 14 or 16)) &&
        values[drivers["next_assist_quota_rejects"]] == Next(400,quotaRejectEvent) &&
        values[drivers["next_assist_quota_issue_rejects"]] == Next(500,rejectIssue != 0) &&
        values[drivers["next_assist_quota_line_rejects"]] == Next(600,rejectLines != 0) &&
        values[drivers["next_class_capacity_rejects"]] == Next(0,earlyReason is 1 or 2) &&
        values[drivers["next_static_class_overcommit_rejects"]] == Next(0,earlyReason == 1) &&
        values[drivers["next_dynamic_class_exhaustion_rejects"]] == Next(0,earlyReason == 2) &&
        values[drivers["next_nop_due_to_no_class_capacity"]] == Next(0,earlyReason is 1 or 2) &&
        values[drivers["next_typed_slot_resource_conflict_rejects"]] == Next(0,earlyReason == 3) &&
        values[drivers["next_nop_due_to_resource_conflict"]] == Next(0,earlyReason == 3) &&
        values[drivers["next_typed_slot_domain_rejects"]] == Next(0,earlyReason == 4) &&
        values[drivers["next_lane_conflict_rejects"]] == Next(0,earlyReason is 11 or 12) &&
        values[drivers["next_pinned_lane_conflicts"]] == Next(0,earlyReason == 11) &&
        values[drivers["next_late_binding_conflicts"]] == Next(0,earlyReason == 12) &&
        values[drivers["next_nop_due_to_pinned_constraint"]] == Next(0,earlyReason == 11) &&
        values[drivers["next_invalid_pinned_lane_rejects"]] == Next(0,earlyReason == 13) &&
        values[drivers["next_rejections_vt0"]] == Next(0,preRejected && targetVt == 0) &&
        values[drivers["next_rejections_vt1"]] == Next(0,preRejected && targetVt == 1) &&
        values[drivers["next_rejections_vt2"]] == Next(0,preRejected && targetVt == 2) &&
        values[drivers["next_rejections_vt3"]] == Next(0,preRejected && targetVt == 3) &&
        values[drivers["next_smt_owner_context_guard_rejects"]] == Next(0,earlyReason == 3 && legalityRejectKind == 2) &&
        values[drivers["next_smt_domain_guard_rejects"]] == Next(0,earlyReason == 3 && legalityRejectKind == 3) &&
        values[drivers["next_smt_boundary_guard_rejects"]] == Next(0,earlyReason == 3 && legalityRejectKind == 1) &&
        values[drivers["next_smt_shared_resource_certificate_rejects"]] == Next(0,earlyReason == 3 && certificateRejectDetail == 1) &&
        values[drivers["next_smt_register_group_certificate_rejects"]] == Next(0,earlyReason == 3 && certificateRejectDetail == 2) &&
        values[drivers["next_smt_legality_reject_by_alu_class"]] == Next(0,earlyReason == 3 && candidateSlotClass == 0) &&
        values[drivers["next_smt_legality_reject_by_lsu_class"]] == Next(0,earlyReason == 3 && candidateSlotClass == 1) &&
        values[drivers["next_smt_legality_reject_by_dma_stream_class"]] == Next(0,earlyReason == 3 && candidateSlotClass == 2) &&
        values[drivers["next_smt_legality_reject_by_matrix_tile_stream_class"]] == Next(0,earlyReason == 3 && candidateSlotClass == 5) &&
        values[drivers["next_smt_legality_reject_by_branch_control"]] == Next(0,earlyReason == 3 && candidateSlotClass == 3) &&
        values[drivers["next_smt_legality_reject_by_system_singleton"]] == Next(0,earlyReason == 3 && candidateSlotClass == 4) &&
        values[drivers["next_certificate_reject_by_alu_class"]] == Next(0,earlyReason == 3 && certificateRejectDetail != 0 && candidateSlotClass == 0) &&
        values[drivers["next_certificate_reject_by_lsu_class"]] == Next(0,earlyReason == 3 && certificateRejectDetail != 0 && candidateSlotClass == 1) &&
        values[drivers["next_certificate_reject_by_dma_stream_class"]] == Next(0,earlyReason == 3 && certificateRejectDetail != 0 && candidateSlotClass == 2) &&
        values[drivers["next_certificate_reject_by_matrix_tile_stream_class"]] == Next(0,earlyReason == 3 && certificateRejectDetail != 0 && candidateSlotClass == 5) &&
        values[drivers["next_certificate_reject_by_branch_control"]] == Next(0,earlyReason == 3 && certificateRejectDetail != 0 && candidateSlotClass == 3) &&
        values[drivers["next_certificate_reject_by_system_singleton"]] == Next(0,earlyReason == 3 && certificateRejectDetail != 0 && candidateSlotClass == 4) &&
        values[drivers["next_reg_group_conflicts_vt0"]] == Next(0,earlyReason == 3 && certificateRejectDetail == 2 && targetVt == 0) &&
        values[drivers["next_reg_group_conflicts_vt1"]] == Next(0,earlyReason == 3 && certificateRejectDetail == 2 && targetVt == 1) &&
        values[drivers["next_reg_group_conflicts_vt2"]] == Next(0,earlyReason == 3 && certificateRejectDetail == 2 && targetVt == 2) &&
        values[drivers["next_reg_group_conflicts_vt3"]] == Next(0,earlyReason == 3 && certificateRejectDetail == 2 && targetVt == 3);
    if (!countersMatch) throw new InvalidOperationException($"Assist rejection counter mismatch: bpCase={bpCase}, issue={issue}, lines={lines}, earlyReason={earlyReason}, vt={targetVt}, BP={bpRejectEvent}, quota={quotaRejectEvent}. " + string.Join(",", new[] {"next_smt_rejections_count","next_assist_rejects","next_assist_backpressure_rejects","next_class_capacity_rejects","next_static_class_overcommit_rejects","next_dynamic_class_exhaustion_rejects","next_nop_due_to_no_class_capacity","next_typed_slot_resource_conflict_rejects","next_nop_due_to_resource_conflict","next_typed_slot_domain_rejects","next_lane_conflict_rejects","next_pinned_lane_conflicts","next_late_binding_conflicts","next_nop_due_to_pinned_constraint","next_invalid_pinned_lane_rejects","next_rejections_vt0","next_rejections_vt1","next_rejections_vt2","next_rejections_vt3"}.Select(n=>n+"="+values[drivers[n]])));
    vectors++;
}
Console.WriteLine($"Assist reservation HWIR PASS: {design.CanonicalDigest}; {vectors} independent demand/locality/quota/backpressure vectors across four VTs and all 16 banks plus invalid bank. Runtime SHA-256 {Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant()}, AssistMicroOp SHA-256 {Convert.ToHexString(SHA256.HashData(assistSource)).ToLowerInvariant()}. Scheduler budget seeds and SRF inventory summaries are sampled inputs; policy min/clamps and reservation equations are derived.");
if (emitDirectory is not null)
{
    await EmitCandidate(emitDirectory, design, vectors, source, assistSource, schedulerSource, admissionSource, reasonEnumSource, schedulerCoreSource, backpressureSource, srfSource, smtSource, safetyTypesSource, certificateSource);
    Console.WriteLine(Path.Combine(emitDirectory, "assist-reservation.mlir"));
}

static BigInteger ParseBits(string bits) { BigInteger value = 0; foreach (char bit in bits) value = (value << 1) | (bit == '1' ? BigInteger.One : BigInteger.Zero); return value; }
static ulong Next(ulong current, bool increment) => increment ? unchecked(current + 1) : current;
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static async Task EmitCandidate(string output, HwValidatedDesign design, int vectors, params byte[][] sourceBytes)
{
    Directory.CreateDirectory(output);
    HwModule module = design.Design.Modules.Single(x => x.Id == design.Design.TopModule);
    var mlir = new HierarchicalCirctMlirLowerer().LowerToMlir(design,
        new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>()));
    byte[] hwirBytes = new CanonicalHwirSerializer().Serialize(design);
    byte[] semantic = JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = "circuitweave.assist-reservation-crosscheck/v3", status = "passed",
        vectors, oracle = "separately coded evaluator for prefetch demand, locality caps, quota/backpressure gates, snapshot/policy seed, SRF inventory summary, state cascade, reject priorities and Stage A/B typed and post-Stage-B counter updates",
        scope = "Combinational sampled-state projection. Not dynamic scheduler execution, full SRF register-array elaboration, RTL equivalence to Golden, or cycle/reset validation."
    }, new JsonSerializerOptions { WriteIndented = true });
    string[] relative = [
        @"CloseToHSL\Core\Pipeline\Assist\Runtime\AssistRuntime.cs",
        @"CloseToHSL\Core\Pipeline\MicroOps\Assist\AssistMicroOp.cs",
        @"CloseToHSL\Core\Pipeline\Assist\Runtime\MicroOpScheduler.Assist.cs",
        @"CloseToHSL\Core\Pipeline\Scheduling\Admission\MicroOpScheduler.Admission.cs",
        @"CloseToHSL\Core\Pipeline\Scheduling\SlotLegality\SlotClassDefinitions.cs",
        @"CloseToHSL\Core\Pipeline\Scheduling\MicroOpScheduler.cs",
        @"CloseToHSL\Core\Pipeline\Scheduling\Backpressure\MicroOpScheduler.AssistBackpressure.cs",
        @"CloseToHSL\Memory\Registers\StreamRegisterFile.Assist.cs",
        @"CloseToHSL\Core\Pipeline\Scheduling\Smt\MicroOpScheduler.SMT.cs",
        @"CloseToHSL\Core\Pipeline\Safety\SafetyVerifier.Types.cs",
        @"CloseToHSL\Core\Pipeline\Certificates\BundleResourceCertificate4Way.cs"];
    if (sourceBytes.Length != relative.Length) throw new InvalidOperationException("Assist source capture count differs from pin list.");
    var captures = relative.Select((r, i) => new { path = Path.GetFullPath(Path.Combine(modelRoot, r)), relativePath = "HybridCPU_ISE/" + r.Replace('\\', '/'), bytes = sourceBytes[i], sha256 = Hash(sourceBytes[i]) }).ToArray();
    FileLinePositionSpan location = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(sourceBytes[2])).GetRoot()
        .DescendantNodes().OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "TryInjectAssistCandidates")
        .Identifier.GetLocation().GetLineSpan();
    int line = location.StartLinePosition.Line + 1, column = location.StartLinePosition.Character + 1;
    var supportingSymbols = new List<object>();
    void AddMethodSymbol(int captureIndex, string methodName, int parameterCount, string symbolId)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(sourceBytes[captureIndex])).GetRoot();
        MethodDeclarationSyntax method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == methodName && x.ParameterList.Parameters.Count == parameterCount);
        FileLinePositionSpan span = method.Identifier.GetLocation().GetLineSpan();
        supportingSymbols.Add(new { id = symbolId, kind = "Method", disposition = "source-inventory-only; sampled contract input", location = new { file = captures[captureIndex].path, startLine = span.StartLinePosition.Line + 1, startColumn = span.StartLinePosition.Character + 1 }, blockingReason = "Method body is pinned and call-order guarded; this candidate samples the method result and does not elaborate its full logic." });
    }
    AddMethodSymbol(3, "TryClassAdmission", 10, "M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.TryClassAdmission(MicroOp,BundleResourceCertificate4Way&,SmtBundleMetadata4Way,BoundaryGuardState,ClassTemplateAdmissionState&,Int32,Int32,TypedSlotRejectReason&,Boolean,Nullable<Int32>)");
    AddMethodSymbol(3, "TryMaterializeLane", 6, "M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.TryMaterializeLane(MicroOp,Byte,Int32&,TypedSlotRejectReason&,LaneMaterializationRole,Byte)");
    AddMethodSymbol(2, "RecordAssistReject", 2, "M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.RecordAssistReject(MicroOp,TypedSlotRejectReason)");
    AddMethodSymbol(8, "RecordTypedSlotReject", 1, "M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.RecordTypedSlotReject(TypedSlotRejectReason)");
    AddMethodSymbol(8, "RecordTypedSlotReject", 2, "M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.RecordTypedSlotReject(TypedSlotRejectReason,MicroOp)");
    AddMethodSymbol(8, "RecordPerVtRejection", 1, "M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.RecordPerVtRejection(Int32)");
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(sourceBytes[4])).GetRoot();
        EnumDeclarationSyntax enumeration = root.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(x => x.Identifier.ValueText == "TypedSlotRejectReason");
        FileLinePositionSpan span = enumeration.Identifier.GetLocation().GetLineSpan();
        supportingSymbols.Add(new { id = "T:YAKSys_Hybrid_CPU.Core.TypedSlotRejectReason", kind = "Enum", disposition = "source-inventory-only; values source-pinned", location = new { file = captures[4].path, startLine = span.StartLinePosition.Line + 1, startColumn = span.StartLinePosition.Character + 1 }, blockingReason = "Stage A/B reason values are enumerated for decoding, while the stage legality equations remain sampled outcomes." });
    }
    void AddEnumSymbol(int captureIndex, string enumName, string symbolId, string blockingReason)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(sourceBytes[captureIndex])).GetRoot();
        EnumDeclarationSyntax enumeration = root.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(x => x.Identifier.ValueText == enumName);
        FileLinePositionSpan span = enumeration.Identifier.GetLocation().GetLineSpan();
        supportingSymbols.Add(new { id = symbolId, kind = "Enum", disposition = "source-inventory-only; values source-pinned", location = new { file = captures[captureIndex].path, startLine = span.StartLinePosition.Line + 1, startColumn = span.StartLinePosition.Character + 1 }, blockingReason });
    }
    AddEnumSymbol(9, "RejectKind", "T:YAKSys_Hybrid_CPU.Core.RejectKind", "Legality reject-kind result is sampled; RuntimeLegalityService evaluation is not elaborated.");
    AddEnumSymbol(10, "CertificateRejectDetail", "T:YAKSys_Hybrid_CPU.Core.CertificateRejectDetail", "Certificate detail result is sampled; BundleResourceCertificate4Way.CanInject logic is not elaborated.");
    foreach (string propertyName in new[] { "ClassCapacityRejects", "StaticClassOvercommitRejects", "DynamicClassExhaustionRejects", "NopDueToNoClassCapacity", "TypedSlotResourceConflictRejects", "NopDueToResourceConflict", "TypedSlotDomainRejects", "LaneConflictRejects", "PinnedLaneConflicts", "LateBindingConflicts", "NopDueToPinnedConstraint", "InvalidPinnedLaneRejects", "RejectionsVT0", "RejectionsVT1", "RejectionsVT2", "RejectionsVT3", "SmtOwnerContextGuardRejects", "SmtDomainGuardRejects", "SmtBoundaryGuardRejects", "SmtSharedResourceCertificateRejects", "SmtRegisterGroupCertificateRejects", "SmtLegalityRejectByAluClass", "SmtLegalityRejectByLsuClass", "SmtLegalityRejectByDmaStreamClass", "SmtLegalityRejectByMatrixTileStreamClass", "SmtLegalityRejectByBranchControl", "SmtLegalityRejectBySystemSingleton", "CertificateRejectByAluClass", "CertificateRejectByLsuClass", "CertificateRejectByDmaStreamClass", "CertificateRejectByMatrixTileStreamClass", "CertificateRejectByBranchControl", "CertificateRejectBySystemSingleton", "RegGroupConflictsVT0", "RegGroupConflictsVT1", "RegGroupConflictsVT2", "RegGroupConflictsVT3" })
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(sourceBytes[5])).GetRoot();
        PropertyDeclarationSyntax property = root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Single(x => x.Identifier.ValueText == propertyName);
        FileLinePositionSpan span = property.Identifier.GetLocation().GetLineSpan();
        supportingSymbols.Add(new { id = "P:YAKSys_Hybrid_CPU.Core.MicroOpScheduler." + propertyName, kind = "Property", disposition = "counter inventory; sampled baseline", location = new { file = captures[5].path, startLine = span.StartLinePosition.Line + 1, startColumn = span.StartLinePosition.Character + 1 }, blockingReason = "Counter next-value is modeled; no physical register clock/reset lifecycle is established." });
    }
    byte[] inventory = JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = "circuitweave.source-coverage-index/v1", disposition = "incomplete-unqualified",
        semanticReportSha256 = Hash(semantic), project = Path.Combine(modelRoot, "HybridCPU_ISE.csproj"),
        configuration = "Debug", targetFramework = "net11.0", defines = new[] { "TRACE", "TESTING", "DEBUG" },
        scope = "Roslyn located the Assist scheduler entry method in eleven byte-pinned source files. Additional Stage A/B methods, typed reason and detail enums, reject recorders and counter properties are source-guarded inputs; this remains a selected Assist projection, not complete Processor project closure or semantic dependency inventory.",
        captureVerification = new { schema = "circuitweave.source-capture-check/v1", status = "producer-reported-listed-inputs-match", semanticReportSha256 = Hash(semantic), sourceCaptureSha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(captures.Select(c => new { c.path, c.sha256, length = c.bytes.LongLength }))), projectGraphSha256 = Hash(await File.ReadAllBytesAsync(Path.Combine(modelRoot, "HybridCPU_ISE.csproj"))), scope = "Selected byte-pinned files only; no transitive project graph closure claimed.", checkedFiles = captures.Length },
        capturedSourceFiles = captures.Select(c => new { path = c.path, sha256 = c.sha256, length = c.bytes.LongLength }).ToArray(),
        supportingSourceInventory = supportingSymbols,
        symbols = new[] { new {
            symbol = "global::YAKSys_Hybrid_CPU.Core.MicroOpScheduler", assembly = "HybridCPU_ISE", kind = "Class", accessibility = "Public",
            memberCount = 1, blockingReason = "Selected Assist call-path projection only; containing project and transitive dependencies are not inventoried.",
            coverage = new { inventory = "partial-semantic", hardwareRole = "unclassified", hwir = "not-established", rtl = "not-established", simulation = "not-run", independentVerification = "not-run", studio = "not-published" },
            declarations = new[] { new { location = new { file = captures[2].path, startLine = line, startColumn = column }, memberIds = new[] { "M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.TryInjectAssistCandidates()" } } },
            dependencies = new[] { "M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.TryClassAdmission()", "M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.TryMaterializeLane()", "M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.RecordAssistReject()", "M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.RecordTypedSlotReject(TypedSlotRejectReason)", "M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.RecordPerVtRejection(Int32)", "T:YAKSys_Hybrid_CPU.Core.TypedSlotRejectReason" },
            members = new[] { new { id = "M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.TryInjectAssistCandidates()", kind = "Method", locations = new[] { new { file = captures[2].path, startLine = line, startColumn = column } } } }
        }}
    }, new JsonSerializerOptions { WriteIndented = true });
    byte[] capturedSources = JsonSerializer.SerializeToUtf8Bytes(new {
        schema = "circuitweave.captured-sources/v1", inventorySha256 = Hash(inventory),
        files = captures.Select(c => new { path = c.path, bytes = Convert.ToBase64String(c.bytes) }).ToArray()
    }, new JsonSerializerOptions { WriteIndented = true });
    byte[] pins = JsonSerializer.SerializeToUtf8Bytes(new {
        schema = "circuitweave.assist-reservation-source-pins/v3", modelCommit = commit,
        currentModelHead = commit, modelTreeState = "dirty; exact listed working-tree bytes were captured; base commit is not asserted as the byte source",
        hwirSha256 = design.CanonicalDigest, mlirSha256 = mlir.ContentDigest, sourceInventorySha256 = Hash(inventory),
        sources = captures.Select(c => new { path = c.relativePath, sha256 = c.sha256, length = c.bytes.LongLength }).ToArray(),
        outputs = module.Ports.Where(p => p.Direction == HwPortDirection.Output).Select(p => p.Name).ToArray(),
        assumptions = new[] { "Scheduler memory/load budget seeds and policy values are sampled ports; min/clamp initialization is derived.", "SRF per-register arrays are reduced to reuse/count/empty/victim summary ports; array scan itself is not elaborated.", "Stage A/B reason codes, ResourceConflict RejectKind, CertificateRejectDetail, candidate SlotClass, and diagnostic counter baselines are sampled outputs; RuntimeLegalityService and BundleResourceCertificate4Way evaluation are not elaborated.", "No physical clock/reset exists for this pass-local reservation state." }
    }, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllBytesAsync(Path.Combine(output, "assist-reservation.hwir.json"), hwirBytes);
    await File.WriteAllTextAsync(Path.Combine(output, "assist-reservation.mlir"), mlir.Text, new UTF8Encoding(false));
    await File.WriteAllBytesAsync(Path.Combine(output, "assist-reservation-semantic-crosscheck.json"), semantic);
    await File.WriteAllBytesAsync(Path.Combine(output, "assist-reservation-source-inventory.json"), inventory);
    await File.WriteAllBytesAsync(Path.Combine(output, "assist-reservation-captured-sources.json"), capturedSources);
    await File.WriteAllBytesAsync(Path.Combine(output, "assist-reservation-source-pins.json"), pins);
    await File.WriteAllTextAsync(Path.Combine(output, "assist-reservation.tb.sv"), BuildTestbench(module), new UTF8Encoding(false));
}

static string BuildTestbench(HwModule module)
{
    var b = new StringBuilder();
    b.AppendLine("module assist_reservation_directed_tb;");
    foreach (HwPortContract p in module.Ports)
    {
        string range = $"[{p.Type.Width - 1}:0]";
        b.AppendLine($"  {(p.Direction == HwPortDirection.Input ? "logic" : "wire")} {range} {p.Name};");
    }
    b.AppendLine($"  {module.Id} dut (.*);");
    b.AppendLine("  initial begin");
    b.AppendLine("    $dumpfile(\"assist-reservation.vcd\"); $dumpvars(0, assist_reservation_directed_tb);");
    void Setup(Dictionary<string, ulong> values)
    {
        foreach (HwPortContract p in module.Ports.Where(p => p.Direction == HwPortDirection.Input))
        {
            ulong value = values.TryGetValue(p.Name, out ulong found) ? found : 0;
            string literal = p.Type.Width == 64 ? $"64'h{value:x16}" : $"{p.Type.Width}'d{value}";
            b.AppendLine($"    {p.Name} = {literal};");
        }
    }
    static string Eq(string name, int width, ulong value) => $"({name} !== {width}'d{value})";
    void Expect(string message, params (string Name, int Width, ulong Value)[] outputs)
    {
        b.AppendLine("    #1;");
        b.AppendLine("    if (" + string.Join(" || ", outputs.Select(x => Eq(x.Name, x.Width, x.Value))) + $") $fatal(1, \"{message}\");");
        b.AppendLine($"    $display(\"PASS {message}\");");
    }
    Dictionary<string, ulong> Base(ulong issue, ulong lineCredits, bool dma, bool srfFile, ulong srfLoading)
    {
        var d = new Dictionary<string, ulong>(StringComparer.Ordinal) {
            ["remaining_issue_credits"] = issue, ["remaining_line_credits"] = lineCredits, ["prefetch_length"] = 32,
            ["hot_line_cap"] = 1, ["cold_line_cap"] = 1, ["bp_state_valid"] = 0,
            ["bp_shared_current"] = 9, ["lsu_carrier_current"] = 9, ["dma_carrier_current"] = 9,
            ["remaining_memory_issue_budget"] = 2, ["remaining_load_issue_budget"] = 2,
            ["policy_shared_outer_cap_credits"] = 2, ["policy_lsu_carrier_credits"] = 1, ["policy_dma_carrier_credits"] = 1,
            ["bank_id"] = 0, ["read_budget_at_least_one"] = 1, ["read_budget_at_least_two"] = 0,
            ["is_dma_carrier"] = dma ? 1UL : 0UL, ["srf_file_present"] = srfFile ? 1UL : 0UL,
            ["srf_loading_count"] = srfLoading, ["srf_loading_budget"] = 1, ["srf_resident_budget"] = 2,
            ["srf_has_empty_slot"] = 1, ["lsu_carrier_credits"] = 1, ["dma_carrier_credits"] = 1,
            ["target_vt"] = 0, ["outstanding_capacity_vt0"] = 1,
            ["smt_rejections_count"] = 9, ["assist_rejects"] = 10, ["assist_backpressure_rejects"] = 20,
            ["assist_backpressure_outer_rejects"] = 30, ["assist_backpressure_mshr_rejects"] = 40,
            ["assist_backpressure_dma_srf_rejects"] = 50, ["assist_quota_rejects"] = 60,
            ["assist_quota_issue_rejects"] = 70, ["assist_quota_line_rejects"] = 80
        };
        return d;
    }
    Setup(Base(1, 2, false, false, 0));
    Expect("Assist reservation success", ("backpressure_accepted",1,1), ("accept",1,1), ("reserved_line_demand",16,1),
        ("next_bp_shared_credits",8,1), ("next_lsu_carrier_credits",8,0), ("next_outstanding_count_vt0",8,1),
        ("next_consumed_shared_read_packed",32,1), ("next_issue_credits",8,0), ("next_line_credits",16,1), ("next_smt_rejections_count",64,9));
    Setup(Base(0, 2, false, false, 0));
    Expect("BP success then quota reject retains BP state", ("backpressure_accepted",1,1), ("accept",1,0), ("reject_issue",1,1),
        ("next_bp_shared_credits",8,1), ("next_lsu_carrier_credits",8,0), ("next_outstanding_count_vt0",8,1),
        ("next_assist_rejects",64,11), ("next_smt_rejections_count",64,10), ("next_assist_backpressure_rejects",64,20),
        ("next_assist_quota_rejects",64,61), ("next_assist_quota_issue_rejects",64,71));
    Setup(Base(1, 2, true, true, 1));
    Expect("DMA SRF loading budget rejects before quota", ("backpressure_accepted",1,0), ("reject_dma_srf",1,1),
        ("reject_issue",1,0), ("next_bp_shared_credits",8,2), ("next_dma_carrier_credits",8,1),
        ("next_outstanding_count_vt0",8,0), ("next_smt_rejections_count",64,10), ("next_assist_rejects",64,11),
        ("next_assist_backpressure_rejects",64,21), ("next_assist_backpressure_dma_srf_rejects",64,51), ("next_assist_quota_rejects",64,60));
    var earlyA = Base(1, 2, false, false, 0); earlyA["stage_a_reject_reason"] = 3; earlyA["stage_b_reject_reason"] = 13; earlyA["candidate_vt"] = 2; earlyA["last_legality_reject_kind"] = 2; earlyA["last_certificate_reject_detail"] = 2; earlyA["candidate_slot_class"] = 2; Setup(earlyA);
    Expect("Stage A rejection dominates Stage B and skips reservations", ("backpressure_accepted",1,0), ("accept",1,0),
        ("next_bp_shared_credits",8,2), ("next_lsu_carrier_credits",8,1), ("next_outstanding_count_vt0",8,0),
        ("next_issue_credits",8,1), ("next_line_credits",16,2), ("reject_outstanding",1,0), ("reject_issue",1,0),
        ("next_smt_rejections_count",64,10), ("next_assist_rejects",64,11), ("next_assist_backpressure_rejects",64,20),
        ("next_assist_quota_rejects",64,60), ("next_typed_slot_resource_conflict_rejects",64,1),
        ("next_nop_due_to_resource_conflict",64,1), ("next_rejections_vt2",64,1), ("next_invalid_pinned_lane_rejects",64,0),
        ("next_smt_owner_context_guard_rejects",64,1), ("next_smt_register_group_certificate_rejects",64,1),
        ("next_smt_legality_reject_by_dma_stream_class",64,1), ("next_certificate_reject_by_dma_stream_class",64,1),
        ("next_reg_group_conflicts_vt2",64,1));
    var unrecognizedTelemetry = Base(1, 2, false, false, 0);
    unrecognizedTelemetry["stage_a_reject_reason"] = 3;
    unrecognizedTelemetry["candidate_vt"] = 2;
    unrecognizedTelemetry["last_legality_reject_kind"] = 7;
    unrecognizedTelemetry["last_certificate_reject_detail"] = 7;
    unrecognizedTelemetry["candidate_slot_class"] = 6;
    Setup(unrecognizedTelemetry);
    Expect("Unrecognized ResourceConflict telemetry holds detail counters", ("accept",1,0),
        ("next_typed_slot_resource_conflict_rejects",64,1), ("next_smt_rejections_count",64,10),
        ("next_rejections_vt2",64,1), ("next_smt_owner_context_guard_rejects",64,0),
        ("next_smt_domain_guard_rejects",64,0), ("next_smt_boundary_guard_rejects",64,0),
        ("next_smt_shared_resource_certificate_rejects",64,0), ("next_smt_register_group_certificate_rejects",64,0),
        ("next_smt_legality_reject_by_alu_class",64,0), ("next_smt_legality_reject_by_lsu_class",64,0),
        ("next_smt_legality_reject_by_dma_stream_class",64,0), ("next_smt_legality_reject_by_matrix_tile_stream_class",64,0),
        ("next_smt_legality_reject_by_branch_control",64,0), ("next_smt_legality_reject_by_system_singleton",64,0),
        ("next_certificate_reject_by_alu_class",64,0), ("next_certificate_reject_by_lsu_class",64,0),
        ("next_certificate_reject_by_dma_stream_class",64,0), ("next_certificate_reject_by_matrix_tile_stream_class",64,0),
        ("next_certificate_reject_by_branch_control",64,0), ("next_certificate_reject_by_system_singleton",64,0),
        ("next_reg_group_conflicts_vt2",64,0));
    var earlyB = Base(1, 2, false, false, 0); earlyB["stage_b_reject_reason"] = 13; earlyB["candidate_vt"] = 3; Setup(earlyB);
    Expect("Stage B rejection skips reservations", ("backpressure_accepted",1,0), ("accept",1,0),
        ("next_bp_shared_credits",8,2), ("next_lsu_carrier_credits",8,1), ("next_outstanding_count_vt0",8,0),
        ("next_smt_rejections_count",64,10), ("next_assist_rejects",64,11), ("next_assist_backpressure_rejects",64,20),
        ("next_assist_quota_rejects",64,60), ("next_invalid_pinned_lane_rejects",64,1), ("next_rejections_vt3",64,1));
    var staticReject = Base(1, 2, false, false, 0); staticReject["stage_a_reject_reason"] = 1; Setup(staticReject);
    Expect("Static class overcommit counters", ("next_class_capacity_rejects",64,1), ("next_static_class_overcommit_rejects",64,1),
        ("next_dynamic_class_exhaustion_rejects",64,0), ("next_nop_due_to_no_class_capacity",64,1),
        ("next_smt_rejections_count",64,10), ("next_assist_rejects",64,11), ("next_rejections_vt0",64,1));
    var dynamicReject = Base(1, 2, false, false, 0); dynamicReject["stage_a_reject_reason"] = 2; dynamicReject["candidate_vt"] = 1; Setup(dynamicReject);
    Expect("Dynamic class exhaustion counters", ("next_class_capacity_rejects",64,1), ("next_static_class_overcommit_rejects",64,0),
        ("next_dynamic_class_exhaustion_rejects",64,1), ("next_nop_due_to_no_class_capacity",64,1),
        ("next_smt_rejections_count",64,10), ("next_rejections_vt1",64,1));
    var domainReject = Base(1, 2, false, false, 0); domainReject["stage_a_reject_reason"] = 4; Setup(domainReject);
    Expect("Domain rejection counters", ("next_typed_slot_domain_rejects",64,1), ("next_smt_rejections_count",64,10),
        ("next_assist_rejects",64,11), ("next_rejections_vt0",64,1));
    var pinnedReject = Base(1, 2, false, false, 0); pinnedReject["stage_b_reject_reason"] = 11; Setup(pinnedReject);
    Expect("Pinned lane conflict counters", ("next_lane_conflict_rejects",64,1), ("next_pinned_lane_conflicts",64,1),
        ("next_nop_due_to_pinned_constraint",64,1), ("next_smt_rejections_count",64,10), ("next_rejections_vt0",64,1));
    var lateReject = Base(1, 2, false, false, 0); lateReject["stage_b_reject_reason"] = 12; Setup(lateReject);
    Expect("Late binding conflict counters", ("next_lane_conflict_rejects",64,1), ("next_pinned_lane_conflicts",64,0),
        ("next_late_binding_conflicts",64,1), ("next_nop_due_to_pinned_constraint",64,0),
        ("next_smt_rejections_count",64,10), ("next_rejections_vt0",64,1));
    b.AppendLine("    $display(\"PASS all directed Assist reservation RTL scenarios\"); $finish;");
    b.AppendLine("  end");
    b.AppendLine("endmodule");
    return b.ToString();
}

static async Task PublishCandidate(string runDirectory, string workspace)
{
    string run = Path.GetFullPath(runDirectory), target = Path.GetFullPath(workspace);
    if (!Directory.Exists(run)) throw new DirectoryNotFoundException(run);
    Directory.CreateDirectory(target);
    byte[] hwir = await File.ReadAllBytesAsync(Path.Combine(run, "assist-reservation.hwir.json"));
    HwDesign sourceDesign = new CanonicalHwirSerializer().Deserialize(hwir);
    HwValidatedDesign design = new FailClosedHardwareIrValidator().Validate(sourceDesign).Validated
        ?? throw new InvalidDataException("Published Assist HWIR no longer validates.");
    byte[] Read(string name) => File.ReadAllBytes(Path.Combine(run, name));
    var supplements = new[] {
        ("assist-reservation-mlir",StudioArtifactKind.Mlir,"assist-reservation.mlir"),
        ("assist-reservation-rtl",StudioArtifactKind.SystemVerilog,"assist-reservation.sv"),
        ("assist-reservation-testbench",StudioArtifactKind.SystemVerilog,"assist-reservation.tb.sv"),
        ("assist-reservation-waveform",StudioArtifactKind.VcdWaveform,"assist-reservation.vcd"),
        ("assist-reservation-simulation-receipt",StudioArtifactKind.CandidateRunProvenance,"assist-reservation-simulation-receipt.json"),
        ("assist-reservation-semantic-crosscheck",StudioArtifactKind.CandidateRunProvenance,"assist-reservation-semantic-crosscheck.json"),
        ("assist-reservation-source-inventory",StudioArtifactKind.SourceInventory,"assist-reservation-source-inventory.json"),
        ("assist-reservation-captured-sources",StudioArtifactKind.CapturedSources,"assist-reservation-captured-sources.json"),
        ("assist-reservation-source-pins",StudioArtifactKind.CandidateRunProvenance,"assist-reservation-source-pins.json"),
        ("assist-reservation-firtool-log",StudioArtifactKind.CandidateRunProvenance,"firtool-export.log"),
        ("assist-reservation-iverilog-log",StudioArtifactKind.CandidateRunProvenance,"iverilog-compile.log"),
        ("assist-reservation-vvp-log",StudioArtifactKind.CandidateRunProvenance,"vvp-simulation.log"),
        ("assist-reservation-tool-hashes",StudioArtifactKind.CandidateRunProvenance,"tool-hashes.txt")
    }.Where(x => File.Exists(Path.Combine(run,x.Item3)) && new FileInfo(Path.Combine(run,x.Item3)).Length>0)
      .Select(x => { byte[] data=Read(x.Item3); return new StudioSupplementArtifact(x.Item1,x.Item2,x.Item3,data,Hash(data)); }).ToArray();
    bool simulated = File.Exists(Path.Combine(run,"assist-reservation-simulation-receipt.json"));
    var stages = new[] {
        new CompilationStageRecord(CompilationStage.SourceAdmission,CompilationStageStatus.NotRun,"Only eleven explicit Assist reservation inputs were captured; no project-wide admission ran."),
        new CompilationStageRecord(CompilationStage.HardwareIrValidation,CompilationStageStatus.Completed,"Source-pinned sampled-state reservation passed fail-closed HWIR validation and the independent vector evaluator; vector count is recorded in the semantic crosscheck artifact."),
        new CompilationStageRecord(CompilationStage.RtlEmission,simulated?CompilationStageStatus.Completed:CompilationStageStatus.NotRun,simulated?"Pinned CIRCT emitted candidate combinational RTL and Icarus ran directed reservation scenarios; no cycle or Golden equivalence is implied.":"No RTL receipt supplied."),
        new CompilationStageRecord(CompilationStage.IndependentVerification,simulated?CompilationStageStatus.Completed:CompilationStageStatus.NotRun,simulated?"Independent evaluator plus directed Icarus RTL scenarios passed; this is not processor architectural verification.":"No RTL simulation receipt supplied."),
        new CompilationStageRecord(CompilationStage.EvidencePromotion,CompilationStageStatus.NotRun,"StructuralOnly; runtime identity, SRF inventory array, and temporal contracts remain sampled or open.")
    };
    byte[] pins=Read("assist-reservation-source-pins.json");
    var request = new StudioCompilerPublicationRequest("hybridcpu-assist-reservation","HybridCPU Assist reservation, backpressure, SRF summary and quota", "assistreserve"+design.CanonicalDigest[..16], design,
        new HwTargetDescription("CIRCT generic","combinational","circt-cli"),stages,Supplements:supplements,SourceDigest:Hash(pins));
    StudioPublicationReceipt receipt=await new StudioSnapshotPublisher().PublishAsync(request,target);
    Console.WriteLine(JsonSerializer.Serialize(receipt,new JsonSerializerOptions{WriteIndented=true}));
    var opened=await new StudioWorkspaceLoader().OpenAsync(Path.Combine(target,"studio.project.json"));
    if(!opened.Success||opened.Value is null) throw new InvalidDataException("Studio API refused Assist reservation publication: "+opened.Error);
    var session=opened.Value; var nav=GraphNavigationIndex.Create(session.Graph);
    if(session.Graph.Nodes.Count(n=>n.Kind==StudioGraphNodeKind.Module)!=1||session.Graph.Trust!=StudioDocumentTrust.StructuralOnly)
        throw new InvalidDataException("Assist publication must load as one StructuralOnly module.");
    if(nav.GetRoots(0,8).Items.Length==0||nav.GetChildren(nav.TopModuleId,0,16).Items.Length==0)
        throw new InvalidDataException("Assist Studio hierarchy paging failed.");
    var search=SourceInventorySearch.Find(session.SourceInventory?.Symbols??Array.Empty<StudioSourceInventorySymbol>(),"TryInjectAssistCandidates");
    if(search.Count==0) throw new InvalidDataException("Studio source inventory search missed the Assist scheduler call-path symbol.");
    var mapping=session.SourceMap?.Mappings.FirstOrDefault(m=>m.Source.Commit==commit&&m.Source.RelativePath.EndsWith("MicroOpScheduler.Assist.cs",StringComparison.Ordinal))
        ??throw new InvalidDataException("Studio source map omitted the captured Assist scheduler entry method.");
    if(!nav.TryGetNode(mapping.GraphNodeId,out _)||string.IsNullOrWhiteSpace(CapturedNodeSource.Excerpt(session,mapping.Source)))
        throw new InvalidDataException("Assist source map selection or captured excerpt failed.");
    if(session.Artifacts.Count<10||session.Artifacts.Any(a=>!a.HasDeclaredDigest||a.Artifact.Sha256!=a.ActualSha256||a.Artifact.ByteLength!=a.ActualSize))
        throw new InvalidDataException("Assist Studio artifact digest/length inspection failed.");
    Console.WriteLine("Assist Studio diagnostics: " + JsonSerializer.Serialize(session.Diagnostics));
    Console.WriteLine($"Assist Studio API PASS: modules=1 nodes={session.Graph.Nodes.Count} edges={session.Graph.Edges.Count} artifacts={session.Artifacts.Count} trust={session.Graph.Trust} diagnostics={session.Diagnostics.Count}; hierarchy, partial inventory search, source selection and captured excerpt passed.");
}
