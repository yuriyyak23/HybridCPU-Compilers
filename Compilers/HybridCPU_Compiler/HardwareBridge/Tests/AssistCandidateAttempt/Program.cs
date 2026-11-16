using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using CircuitWeave.Orchestration;
using CircuitWeave.Visualization;
using CircuitWeave.Visualization.Publishing;
using HybridCPU.Compiler.HardwareBridge.Hwir;

const string modelRoot = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE";
const string commit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
var tc = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
string Read(string relative) => Path.Combine(modelRoot, relative);
byte[] Bytes(string relative) => File.ReadAllBytes(Read(relative));
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

byte[] admission = Bytes(@"CloseToHSL\Core\Pipeline\Scheduling\Admission\MicroOpScheduler.Admission.cs");
byte[] scheduler = Bytes(@"CloseToHSL\Core\Pipeline\Scheduling\MicroOpScheduler.cs");
byte[] classes = Bytes(@"CloseToHSL\Core\Pipeline\Scheduling\SlotLegality\SlotClassDefinitions.cs");
byte[] laneId = Bytes(@"CloseToHSL\Core\Pipeline\Scheduling\LaneId.cs");
byte[] chooser = Bytes(@"CloseToHSL\Core\Pipeline\Scheduling\LaneChoice\DeterministicLaneChooser.cs");
byte[] smt = Bytes(@"CloseToHSL\Core\Pipeline\Scheduling\Smt\MicroOpScheduler.SMT.cs");
byte[] guardSource = Bytes(@"CloseToHSL\Core\Pipeline\Safety\SafetyVerifier.SmtLegality.cs");
byte[] guardsSource = Bytes(@"CloseToHSL\Core\Pipeline\Safety\SafetyVerifier.Guards.cs");
byte[] certificateSource = Bytes(@"CloseToHSL\Core\Pipeline\Certificates\BundleResourceCertificate4Way.cs");
byte[] scoreboard = Bytes(@"CloseToHSL\Core\Pipeline\Scheduling\Scoreboard\MicroOpScheduler.Scoreboard.cs");
byte[] infrastructure = Bytes(@"CloseToHSL\Core\Pipeline\Scheduling\MicroOpScheduler.Infrastructure.cs");
byte[] loadStore = Bytes(@"CloseToHSL\Core\Pipeline\MicroOps\Memory\MicroOp.LoadStore.cs");
byte[] atomic = Bytes(@"CloseToHSL\Core\Pipeline\MicroOps\Types\MicroOp.Misc.cs");
byte[] occupancy = Bytes(@"CloseToHSL\Core\Memory\IHardwareOccupancyInput.cs");

HwValidatedDesign stageA = BridgeHwirClassAdmission.Compile(admission, scheduler, classes,
    [Hash(admission), Hash(scheduler), Hash(classes)], commit, tc);
byte[] capacitySource = Bytes(@"CloseToHSL\Core\Pipeline\Scheduling\SlotLegality\SlotClassCapacity.cs");
HwValidatedDesign capacity = BridgeHwirClassCapacity.Compile(capacitySource, classes,
    [Hash(capacitySource), Hash(classes)], commit, tc);
stageA = BridgeHwirClassCapacity.AttachToStageA(stageA, capacity);
HwValidatedDesign stageB = BridgeHwirLaneMaterialization.Compile(admission, laneId, classes, chooser,
    [Hash(admission), Hash(laneId), Hash(classes), Hash(chooser)], commit, tc);
HwValidatedDesign guard = BridgeHwirSmtLegalityGuardPlane.Compile(guardSource, guardsSource, tc);
HwValidatedDesign predicate = BridgeHwirSmtCertificatePredicate.Compile(certificateSource,
    Bytes(@"CloseToHSL\Core\Pipeline\Metadata\MicroOpAdmissionMetadata.cs"), tc);
HwValidatedDesign outer = BridgeHwirSmtOuterGate.Compile(admission, classes, scoreboard, infrastructure,
    loadStore, atomic, occupancy, scheduler,
    [Hash(admission), Hash(classes), Hash(scoreboard), Hash(infrastructure), Hash(loadStore), Hash(atomic), Hash(occupancy), Hash(scheduler)], commit, tc);
HwValidatedDesign decision = BridgeHwirAdmissionDecision.Compile(smt, Hash(smt), stageA, stageB, guard, predicate, outer, commit, capacitySource, Hash(capacitySource));

byte[] assistRuntime = Bytes(@"CloseToHSL\Core\Pipeline\Assist\Runtime\AssistRuntime.cs");
byte[] assistOp = Bytes(@"CloseToHSL\Core\Pipeline\MicroOps\Assist\AssistMicroOp.cs");
byte[] assistScheduler = Bytes(@"CloseToHSL\Core\Pipeline\Assist\Runtime\MicroOpScheduler.Assist.cs");
byte[] reasonEnums = classes;
byte[] schedulerCore = scheduler;
byte[] backpressure = Bytes(@"CloseToHSL\Core\Pipeline\Scheduling\Backpressure\MicroOpScheduler.AssistBackpressure.cs");
byte[] srf = Bytes(@"CloseToHSL\Memory\Registers\StreamRegisterFile.Assist.cs");
byte[] safetyTypes = Bytes(@"CloseToHSL\Core\Pipeline\Safety\SafetyVerifier.Types.cs");
HwValidatedDesign quota = BridgeHwirAssistQuota.Compile(assistRuntime, assistOp, assistScheduler, admission,
    reasonEnums, schedulerCore, backpressure, srf, smt, safetyTypes, certificateSource, commit, tc);
HwValidatedDesign composed = BridgeHwirAssistCandidateAttempt.Compile(decision, quota, tc);
HwValidatedDesign assistControl = BridgeHwirAssistAttemptChain.Compile(assistScheduler, commit, tc);
HwValidatedDesign rankedComposition = BridgeHwirAssistRankedCandidateChain.Compile(assistControl, composed, tc);

HwModule top = composed.Design.Modules.Single(m => m.Id == composed.Design.TopModule);
HashSet<string> outputNames = top.Ports.Where(p => p.Direction == HwPortDirection.Output).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
if (!outputNames.Contains("final_commit") || !outputNames.Contains("admission_template_next") ||
    !outputNames.Contains("quota_next_bp_shared_credits") || !outputNames.Contains("quota_next_issue_credits") ||
    !outputNames.Contains("committed_next_occupancy") || !outputNames.Contains("committed_next_occupied0") ||
    !outputNames.Contains("committed_next_remaining_memory_issue_budget") ||
    !top.Ports.Any(p => p.Name == "candidate_attempt_enable" && p.Direction == HwPortDirection.Input) ||
    !top.Ports.Any(p => p.Name == "is_assist_candidate" && p.Direction == HwPortDirection.Input))
    throw new InvalidDataException("Required Assist attempt commit/template/BP/quota outputs are missing.");
if (outputNames.Any(n => n.StartsWith("admission_next_occupancy", StringComparison.Ordinal) ||
                         n.StartsWith("admission_next_occupied", StringComparison.Ordinal) ||
                         n.StartsWith("admission_next_remaining_memory", StringComparison.Ordinal)))
    throw new InvalidDataException("Admission intermediate state escaped as a committed Assist output.");
foreach (string name in new[] { "committed_next_occupancy", "committed_next_occupied0", "committed_next_remaining_memory_issue_budget" })
{
    HwMuxNode deferred = top.Nodes.OfType<HwMuxNode>().Single(n => n.Id == "commit_" + name);
    if (deferred.Condition != "final_commit_calc")
        throw new InvalidDataException("Deferred admission state is not committed with final Assist acceptance: " + name);
}
HwOperationNode final = top.Nodes.OfType<HwOperationNode>().Single(n => n.Id == "final_commit_calc");
if (!final.Operands.SequenceEqual(new[] { "admission_accepted_effective", "quota_accept_for_enable" }) ||
    !top.Nodes.OfType<HwOperationNode>().Any(n => n.Id == "admission_accepted_effective" &&
        n.Opcode == "and" && n.Operands.SequenceEqual(new[] { "candidate_attempt_enable", "admission_internal_accepted" })))
    throw new InvalidDataException("Final commit is not gated by attempt enable and A/B/reservation acceptance.");
HwMuxNode stageAReason = top.Nodes.OfType<HwMuxNode>().Single(n => n.Id == "quota_stage_a_reject_reason");
HwMuxNode stageBReason = top.Nodes.OfType<HwMuxNode>().Single(n => n.Id == "quota_stage_b_reject_reason");
if (stageAReason.Condition != "admission_internal_stage_b_evaluated" ||
    stageAReason.WhenTrue != "zero_stage_a_reject" || stageAReason.WhenFalse != "admission_internal_reject_reason" ||
    stageBReason.Condition != "admission_internal_stage_b_evaluated" ||
    stageBReason.WhenTrue != "admission_internal_reject_reason" || stageBReason.WhenFalse != "zero_stage_b_reject")
    throw new InvalidDataException("Stage A/B rejection reasons are not separated by the Stage B evaluated gate.");
if (composed.Design.Modules.Single(m => m.Id == "assist_candidate_attempt").Instances.Single(i => i.Id == "assist_quota")
        .PortConnections.GetValueOrDefault("candidate_slot_class") != "admission_slot_class")
    throw new InvalidDataException("Assist quota slot class is not bound to the admitted candidate class.");
HwInstanceNode admissionInstance = composed.Design.Modules.Single(m => m.Id == "assist_candidate_attempt").Instances.Single(i => i.Id == "admission");
if (admissionInstance.PortConnections.GetValueOrDefault("is_assist") != "is_assist_candidate" ||
    outputNames.Contains("admission_is_assist"))
    throw new InvalidDataException("Admission type-test input is not bound to the explicit candidate classification.");
HwMuxNode templateGate = top.Nodes.OfType<HwMuxNode>().Single(n => n.Id == "gate_admission_template_next");
if (templateGate.Condition != "candidate_attempt_enable" || templateGate.WhenFalse != "admission_template_remaining")
    throw new InvalidDataException("Disabled attempts do not hold Stage A template state.");
HwMuxNode bpHold = top.Nodes.OfType<HwMuxNode>().Single(n => n.Id == "gate_quota_next_bp_shared_credits");
if (bpHold.Condition != "assist_reservation_enable" || bpHold.WhenFalse != "quota_bp_shared_current")
    throw new InvalidDataException("BP reservation state is not held for non-Assist or disabled candidates.");
HwMuxNode quotaCounterHold = top.Nodes.OfType<HwMuxNode>().Single(n => n.Id == "gate_quota_next_assist_quota_rejects");
if (quotaCounterHold.Condition != "assist_reservation_enable" || quotaCounterHold.WhenFalse != "quota_assist_quota_rejects")
    throw new InvalidDataException("Quota reject counters are not held when the candidate is not Assist.");
HwMuxNode earlyCounterGate = top.Nodes.OfType<HwMuxNode>().Single(n => n.Id == "gate_quota_next_class_capacity_rejects");
if (earlyCounterGate.Condition != "candidate_attempt_enable" || earlyCounterGate.WhenFalse != "quota_class_capacity_rejects")
    throw new InvalidDataException("Stage A/B counters do not propagate independently of Assist reservation type.");
HwMuxNode assistCounterGate = top.Nodes.OfType<HwMuxNode>().Single(n => n.Id == "gate_quota_next_assist_rejects");
if (!top.Nodes.OfType<HwOperationNode>().Any(n => n.Id == "assist_or_early_next_assist_rejects" &&
        n.Opcode == "or" && n.Operands.SequenceEqual(new[] { "is_assist_candidate", "admission_early_reject" })) ||
    assistCounterGate.Condition != "advance_early_counters_next_assist_rejects")
    throw new InvalidDataException("AssistRejects does not preserve Stage A/B reject accounting for non-Assist MicroOps.");
Console.WriteLine($"Assist candidate attempt composition PASS: {composed.CanonicalDigest}; {top.Ports.Count} ports, {composed.Design.Modules.Count} modules.");

HwModule rankedTop = rankedComposition.Design.Modules.Single(m => m.Id == BridgeHwirAssistRankedCandidateChain.TopModuleId);
if (rankedComposition.Design.Modules.Count != assistControl.Design.Modules.Count - 1 + composed.Design.Modules.Count + 1 ||
    rankedTop.Instances.Count != 4 ||
    !rankedTop.Ports.Any(p => p.Name == "rank3_unqualified_reached" && p.Direction == HwPortDirection.Output) ||
    !rankedTop.Ports.Any(p => p.Name == "final_state_quota_remaining_issue_credits" && p.Direction == HwPortDirection.Output))
    throw new InvalidDataException("Assist ranked typed composition hierarchy, rank3 diagnostic, or final-state boundary is incomplete.");
HwSourceOrigin expectedRankedOrigin = assistControl.Design.Modules.Single(m => m.Id == assistControl.Design.TopModule).Origin!;
if (rankedTop.Origin is not { Repository: "https://github.com/yuriyyak23/HybridCPU-v2", Commit: commit,
        Path: "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Assist/Runtime/MicroOpScheduler.Assist.cs",
        Type: "YAKSys_Hybrid_CPU.Core.MicroOpScheduler", Member: "TryInjectAssistCandidates" } rankedOrigin ||
    !Equals(rankedOrigin, expectedRankedOrigin))
    throw new InvalidDataException("Ranked composition origin must retain the exact upstream Assist scheduler origin from the source-bound control design.");
if (!rankedTop.Nodes.OfType<HwOperationNode>().Any(n => n.Id.StartsWith("control_rank0_", StringComparison.Ordinal) && n.Operands.Contains("attempt0_admission_stage_b_evaluated")) ||
    !rankedTop.Nodes.OfType<HwOperationNode>().Any(n => n.Operands.Contains("typed_slot_enabled_one")) ||
    rankedTop.Instances.Any(i => i.Id == "attempt_control"))
    throw new InvalidDataException("Inlined four-rank control does not consume composed Stage A/B results and typed-slot mode.");
var rank0Instance = rankedTop.Instances.Single(i => i.Id == "attempt_rank0");
var rank1Instance = rankedTop.Instances.Single(i => i.Id == "attempt_rank1");
if (rank0Instance.PortConnections.GetValueOrDefault("candidate_attempt_enable") != "control_rank0_candidate_reached" ||
    rank1Instance.PortConnections.GetValueOrDefault("candidate_attempt_enable") != "control_rank1_candidate_reached" ||
    rank1Instance.PortConnections.GetValueOrDefault("admission_template_remaining") != rank0Instance.PortConnections.GetValueOrDefault("admission_template_next") ||
    rank1Instance.PortConnections.GetValueOrDefault("quota_bp_shared_current") != rank0Instance.PortConnections.GetValueOrDefault("quota_next_bp_shared_credits") ||
    rank1Instance.PortConnections.GetValueOrDefault("quota_remaining_issue_credits") != rank0Instance.PortConnections.GetValueOrDefault("quota_next_issue_credits") ||
    rank1Instance.PortConnections.GetValueOrDefault("admission_occupancy") != rank0Instance.PortConnections.GetValueOrDefault("committed_next_occupancy"))
    throw new InvalidDataException("Assist rank state cascade does not preserve the source mutation order.");
HwOperationNode rank1CurrentInjections = rankedTop.Nodes.OfType<HwOperationNode>().Single(n => n.Id == "rank1_current_injections");
HwOperationNode priorCommitCount = rankedTop.Nodes.OfType<HwOperationNode>().Single(n => n.Id == "rank1_prior_rank0_count_bit");
if (rank1CurrentInjections.Opcode != "add" || priorCommitCount.Opcode != "zext" ||
    !priorCommitCount.Operands.SequenceEqual(new[] { "attempt0_final_commit" }))
    throw new InvalidDataException("Assist currentPassInjections does not account for the prior committed candidate.");
Console.WriteLine($"Assist typed four-rank candidate chain PASS: {rankedComposition.CanonicalDigest}; {rankedComposition.Design.Modules.Count} modules, {rankedTop.Ports.Count} ports.");

if (args.Length != 0)
{
    if (args[0] == "--emit" && args.Length == 2)
    {
        string output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        var mlir = new HierarchicalCirctMlirLowerer().LowerToMlir(composed,
            new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>()));
        File.WriteAllBytes(Path.Combine(output, "assist-candidate-attempt.hwir.json"),
            new CanonicalHwirSerializer().Serialize(composed));
        File.WriteAllText(Path.Combine(output, "assist-candidate-attempt.mlir"), mlir.Text, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "assist-candidate-attempt-digest.txt"), composed.CanonicalDigest + Environment.NewLine, new UTF8Encoding(false));
        Console.WriteLine($"Assist candidate attempt MLIR emitted: {mlir.ContentDigest}");
        var rankedMlir = new HierarchicalCirctMlirLowerer().LowerToMlir(rankedComposition,
            new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>()));
        File.WriteAllBytes(Path.Combine(output, "assist-ranked-candidate-chain.hwir.json"),
            new CanonicalHwirSerializer().Serialize(rankedComposition));
        File.WriteAllText(Path.Combine(output, "assist-ranked-candidate-chain.mlir"), rankedMlir.Text, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "assist-ranked-candidate-chain-digest.txt"), rankedComposition.CanonicalDigest + Environment.NewLine, new UTF8Encoding(false));
        (string Testbench, object Receipt) rankedCrosscheck = BuildRankedRtlCrosscheck(rankedComposition, 32);
        File.WriteAllText(Path.Combine(output, "assist-ranked-candidate-chain.tb.sv"), rankedCrosscheck.Testbench, new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(output, "assist-ranked-candidate-chain-independent-evaluator.json"),
            JsonSerializer.SerializeToUtf8Bytes(rankedCrosscheck.Receipt, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Assist ranked candidate chain MLIR emitted: {rankedMlir.ContentDigest}");
        using (JsonDocument receipt = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(rankedCrosscheck.Receipt)))
            Console.WriteLine($"Assist ranked RTL independent hierarchical-HWIR crosscheck emitted: {receipt.RootElement.GetProperty("vectorCount").GetInt32()} vectors, all top outputs.");
    }
    else if (args[0] == "--publish" && args.Length == 3)
    {
        string artifactRoot = Path.GetFullPath(args[1]);
        string workspace = Path.GetFullPath(args[2]);
        byte[] rankedMlirBytes = File.ReadAllBytes(Path.Combine(artifactRoot, "assist-ranked-candidate-chain.mlir"));
        byte[] rtlBytes = File.ReadAllBytes(Path.Combine(artifactRoot, "assist-ranked-candidate-chain.sv"));
        byte[] testbenchBytes = File.ReadAllBytes(Path.Combine(artifactRoot, "assist-ranked-candidate-chain.tb.sv"));
        byte[] evaluatorReceiptBytes = File.ReadAllBytes(Path.Combine(artifactRoot, "assist-ranked-candidate-chain-independent-evaluator.json"));
        using JsonDocument evaluatorReceipt = JsonDocument.Parse(evaluatorReceiptBytes);
        int crosscheckVectors = evaluatorReceipt.RootElement.GetProperty("vectorCount").GetInt32();
        int crosscheckComparisons = evaluatorReceipt.RootElement.GetProperty("totalOutputComparisons").GetInt32();
        JsonElement commitWitness = evaluatorReceipt.RootElement.GetProperty("directedOutcomes").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "rank0_hardpinned_dma_commit");
        JsonElement commitAttempt = commitWitness.GetProperty("attempts")[0];
        if (crosscheckVectors < 1 || crosscheckComparisons < 1 || commitAttempt.GetProperty("stageA").GetInt32() != 1 ||
            commitAttempt.GetProperty("stageB").GetInt32() != 1 || commitAttempt.GetProperty("backpressure").GetInt32() != 1 ||
            commitAttempt.GetProperty("quota").GetInt32() != 1 || commitAttempt.GetProperty("committed").GetInt32() != 1 ||
            evaluatorReceipt.RootElement.GetProperty("rankCommitVectorCounts").GetProperty("rank0").GetInt32() < 1)
            throw new InvalidDataException("Ranked Assist crosscheck receipt lacks the directed Stage A/B and reservation commit witness.");
        byte[] waveformBytes = File.ReadAllBytes(Path.Combine(artifactRoot, "assist-ranked-candidate-chain.vcd"));
        byte[] simulationLogBytes = File.ReadAllBytes(Path.Combine(artifactRoot, "ranked-tb-simulation.log"));
        byte[] layoutBytes = File.ReadAllBytes(Path.Combine(artifactRoot, "fact-bus-layout.json"));
        byte[] receiptBytes = File.ReadAllBytes(Path.Combine(artifactRoot, "assist-candidate-attempt-rtl-compile-receipt.json"));
        (string Path, byte[] Bytes)[] sourceInputs =
        [
            ("CloseToHSL/Core/Pipeline/Scheduling/Admission/MicroOpScheduler.Admission.cs", admission),
            ("CloseToHSL/Core/Pipeline/Scheduling/MicroOpScheduler.cs", scheduler),
            ("CloseToHSL/Core/Pipeline/Scheduling/SlotLegality/SlotClassDefinitions.cs", classes),
            ("CloseToHSL/Core/Pipeline/Scheduling/LaneId.cs", laneId),
            ("CloseToHSL/Core/Pipeline/Scheduling/LaneChoice/DeterministicLaneChooser.cs", chooser),
            ("CloseToHSL/Core/Pipeline/Scheduling/Smt/MicroOpScheduler.SMT.cs", smt),
            ("CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.SmtLegality.cs", guardSource),
            ("CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Guards.cs", guardsSource),
            ("CloseToHSL/Core/Pipeline/Certificates/BundleResourceCertificate4Way.cs", certificateSource),
            ("CloseToHSL/Core/Pipeline/Scheduling/Scoreboard/MicroOpScheduler.Scoreboard.cs", scoreboard),
            ("CloseToHSL/Core/Pipeline/Scheduling/MicroOpScheduler.Infrastructure.cs", infrastructure),
            ("CloseToHSL/Core/Pipeline/MicroOps/Memory/MicroOp.LoadStore.cs", loadStore),
            ("CloseToHSL/Core/Pipeline/MicroOps/Types/MicroOp.Misc.cs", atomic),
            ("CloseToHSL/Core/Memory/IHardwareOccupancyInput.cs", occupancy),
            ("CloseToHSL/Core/Pipeline/Scheduling/SlotLegality/SlotClassCapacity.cs", capacitySource),
            ("CloseToHSL/Core/Pipeline/Metadata/MicroOpAdmissionMetadata.cs", Bytes(@"CloseToHSL\Core\Pipeline\Metadata\MicroOpAdmissionMetadata.cs")),
            ("CloseToHSL/Core/Pipeline/Assist/Runtime/AssistRuntime.cs", assistRuntime),
            ("CloseToHSL/Core/Pipeline/MicroOps/Assist/AssistMicroOp.cs", assistOp),
            ("CloseToHSL/Core/Pipeline/Assist/Runtime/MicroOpScheduler.Assist.cs", assistScheduler),
            ("CloseToHSL/Core/Pipeline/Scheduling/Backpressure/MicroOpScheduler.AssistBackpressure.cs", backpressure),
            ("CloseToHSL/Memory/Registers/StreamRegisterFile.Assist.cs", srf),
            ("CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs", safetyTypes)
        ];
        byte[] sourceInputsBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "circuitweave.hybridcpu-source-input-hashes/v1",
            repository = "https://github.com/yuriyyak23/HybridCPU-v2",
            commit,
            worktreeState = "dirty; exact bytes are read from the pinned local worktree; no clean-tree claim",
            files = sourceInputs.Select(x => new { path = "HybridCPU_ISE/" + x.Path, sha256 = Hash(x.Bytes), byteLength = x.Bytes.LongLength }).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllBytes(Path.Combine(artifactRoot, "source-inputs.json"), sourceInputsBytes);
        const string assistRelative = "CloseToHSL/Core/Pipeline/Assist/Runtime/MicroOpScheduler.Assist.cs";
        string assistAbsolute = Path.GetFullPath(Path.Combine(modelRoot, assistRelative));
        SyntaxNode assistRoot = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(assistScheduler)).GetRoot();
        ClassDeclarationSyntax assistOwner = assistRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c =>
            c.Identifier.ValueText == "MicroOpScheduler" &&
            c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core");
        FieldDeclarationSyntax assistQuota = assistOwner.Members.OfType<FieldDeclarationSyntax>().Single(f =>
            f.Declaration.Variables.Any(v => v.Identifier.ValueText == "AssistBundleQuota"));
        VariableDeclaratorSyntax quotaVariable = assistQuota.Declaration.Variables.Single(v => v.Identifier.ValueText == "AssistBundleQuota");
        MethodDeclarationSyntax assistMethod = assistOwner.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "TryInjectAssistCandidates");
        FileLinePositionSpan quotaLocation = quotaVariable.Identifier.GetLocation().GetLineSpan();
        FileLinePositionSpan methodLocation = assistMethod.Identifier.GetLocation().GetLineSpan();
        string quotaSymbol = "YAKSys_Hybrid_CPU.Core.MicroOpScheduler.AssistBundleQuota";
        string callSymbol = "YAKSys_Hybrid_CPU.Core.MicroOpScheduler.TryInjectAssistCandidates";
        string quotaMemberId = "F:" + quotaSymbol;
        string callMemberId = "M:" + callSymbol + "()";
        string projectGraphPath = Path.Combine(@"C:\Users\Yuriy\Desktop\CircuitWeave-main", "artifacts/processor-20261114/publication-inputs/hybridcpu-debug-project-graph-v3.json");
        byte[] projectGraphBytes = File.ReadAllBytes(projectGraphPath);
        byte[] semanticBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "circuitweave.assist-ranked-source-semantic/v1",
            source = new { path = assistAbsolute, sha256 = Hash(assistScheduler), byteLength = assistScheduler.LongLength },
            declarations = new[]
            {
                new { symbol = quotaSymbol, kind = "Field", line = quotaLocation.StartLinePosition.Line + 1, column = quotaLocation.StartLinePosition.Character + 1 },
                new { symbol = callSymbol, kind = "Method", line = methodLocation.StartLinePosition.Line + 1, column = methodLocation.StartLinePosition.Character + 1 }
            },
            scope = "Roslyn syntax declarations parsed from exact active dirty-worktree Assist source bytes; this is a two-member slice, not full project semantic closure."
        }, new JsonSerializerOptions { WriteIndented = true });
        byte[] captureIndexBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "circuitweave.assist-ranked-source-capture/v1",
            modelCommit = commit,
            sourceTreeState = "dirty-listed-input-captured",
            files = new[] { new { path = assistAbsolute, sha256 = Hash(assistScheduler), length = assistScheduler.LongLength } },
            projectGraphPath,
            projectGraphSha256 = Hash(projectGraphBytes),
            scope = "Exact listed Assist file only; project graph is separately captured Debug evaluation evidence and does not establish the full Processor closure."
        }, new JsonSerializerOptions { WriteIndented = true });
        byte[] inventoryBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "circuitweave.source-coverage-index/v1",
            disposition = "incomplete-unqualified",
            project = "HybridCPU_ISE/HybridCPU_ISE.csproj",
            configuration = "Debug",
            targetFramework = "net11.0",
            scope = "Exact active dirty-worktree Assist field and scheduler method used as ranked candidate provenance; not a full Processor inventory or semantic dependency closure.",
            semanticReportSha256 = Hash(semanticBytes),
            captureVerification = new
            {
                schema = "circuitweave.source-capture-check/v1",
                status = "producer-reported-listed-inputs-match",
                semanticReportSha256 = Hash(semanticBytes),
                sourceCaptureSha256 = Hash(captureIndexBytes),
                projectGraphSha256 = Hash(projectGraphBytes),
                checkedFiles = 1,
                rootProject = "not-captured",
                scope = "Exact Assist file bytes parsed by Roslyn; project graph reference is not claimed as complete compile closure."
            },
            capturedSourceFiles = new[] { new { path = assistAbsolute, sha256 = Hash(assistScheduler), length = assistScheduler.LongLength } },
            symbols = new[]
            {
                new
                {
                    symbol = "global::YAKSys_Hybrid_CPU.Core.MicroOpScheduler",
                    assembly = "CircuitWeave.AssistAttemptChain",
                    kind = "Class",
                    memberCount = 2,
                    declarations = new[]
                    {
                        new { location = new { file = assistAbsolute, startLine = quotaLocation.StartLinePosition.Line + 1, startColumn = quotaLocation.StartLinePosition.Character + 1 }, memberIds = new[] { quotaMemberId } },
                        new { location = new { file = assistAbsolute, startLine = methodLocation.StartLinePosition.Line + 1, startColumn = methodLocation.StartLinePosition.Character + 1 }, memberIds = new[] { callMemberId } }
                    },
                    blockingReason = "Only AssistBundleQuota and TryInjectAssistCandidates are inventoried; surrounding Processor partials, dependencies and temporal behavior remain outside this source slice.",
                    members = new object[]
                    {
                        new { id = quotaMemberId, kind = "Field", locations = new[] { new { file = assistAbsolute, startLine = quotaLocation.StartLinePosition.Line + 1, startColumn = quotaLocation.StartLinePosition.Character + 1 } } },
                        new { id = callMemberId, kind = "Method", locations = new[] { new { file = assistAbsolute, startLine = methodLocation.StartLinePosition.Line + 1, startColumn = methodLocation.StartLinePosition.Character + 1 } } }
                    }
                }
            }
        }, new JsonSerializerOptions { WriteIndented = true });
        byte[] capturedSourcesBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "circuitweave.captured-sources/v1",
            inventorySha256 = Hash(inventoryBytes),
            files = new[] { new { path = assistAbsolute, bytes = Convert.ToBase64String(assistScheduler) } }
        }, new JsonSerializerOptions { WriteIndented = true });
        byte[] canonicalHwirBytes = new CanonicalHwirSerializer().Serialize(rankedComposition);
        byte[] schedulerCallProvenanceBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "circuitweave.scheduler-call-provenance/v1",
            status = "candidate-unqualified",
            repository = "https://github.com/yuriyyak23/HybridCPU-v2",
            baseCommit = commit,
            sourceCaptureSha256 = Hash(captureIndexBytes),
            hwirSha256 = rankedComposition.CanonicalDigest,
            sources = new[]
            {
                new { path = "HybridCPU_ISE/" + assistRelative.Replace('\\', '/'), sha256 = Hash(assistScheduler), symbol = quotaSymbol, role = "field-declaration" },
                new { path = "HybridCPU_ISE/" + assistRelative.Replace('\\', '/'), sha256 = Hash(assistScheduler), symbol = callSymbol, role = "call-prefix" }
            },
            scope = "Exact captured AssistBundleQuota field and TryInjectAssistCandidates method; ranked HWIR is a candidate projection with sampled rank/legality/runtime facts and no equivalence claim."
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllBytes(Path.Combine(artifactRoot, "assist-ranked-source-semantic.json"), semanticBytes);
        File.WriteAllBytes(Path.Combine(artifactRoot, "assist-ranked-source-capture-index.json"), captureIndexBytes);
        File.WriteAllBytes(Path.Combine(artifactRoot, "assist-ranked-source-inventory.json"), inventoryBytes);
        File.WriteAllBytes(Path.Combine(artifactRoot, "assist-ranked-captured-sources.json"), capturedSourcesBytes);
        File.WriteAllBytes(Path.Combine(artifactRoot, "assist-ranked-scheduler-call-provenance.json"), schedulerCallProvenanceBytes);
        File.WriteAllBytes(Path.Combine(artifactRoot, "assist-ranked-canonical-hwir.json"), canonicalHwirBytes);
        string rtlText = new UTF8Encoding(false, true).GetString(rtlBytes);
        string toolchainJson = "{\"schema\":\"circuitweave.assist-ranked-toolchain/v1\",\"firtoolVersion\":\"firtool-1.160.0\",\"firtoolSha256\":\"187c0c0f0f1464a58997a3e99ca96560cc002b97276cc38c12c28addf0112c4e\",\"iverilogVersion\":\"Icarus 12.0\",\"iverilogSha256\":\"a6681e0b577dcfc4ea83d1591a5714710b0b3535ebb36491ee8177553bab9631\"}";
        byte[] toolchainBytes = Encoding.UTF8.GetBytes(toolchainJson);
        var target = new HwTargetDescription("combinational", "generic", "circt-cli", "hwp-hwir-comb-v1",
            Hash(toolchainBytes));
        var backend = new HardwareBackendResult(true,
            [new RtlSourceFile("assist-ranked-candidate-chain.sv", rtlText, Hash(rtlBytes))], [], Hash(rankedMlirBytes));
        StudioSupplementArtifact[] supplements =
        [
            new("assist-ranked-candidate-chain-mlir", StudioArtifactKind.Mlir, "assist-ranked-candidate-chain.mlir", rankedMlirBytes, Hash(rankedMlirBytes)),
            new("assist-ranked-candidate-chain-testbench", StudioArtifactKind.SystemVerilog, "assist-ranked-candidate-chain.tb.sv", testbenchBytes, Hash(testbenchBytes)),
            new("assist-ranked-candidate-chain-waveform", StudioArtifactKind.VcdWaveform, "assist-ranked-candidate-chain.vcd", waveformBytes, Hash(waveformBytes)),
            new("assist-ranked-independent-evaluator", StudioArtifactKind.CandidateRunProvenance, "assist-ranked-candidate-chain-independent-evaluator.json", evaluatorReceiptBytes, Hash(evaluatorReceiptBytes)),
            new("assist-ranked-simulation-log", StudioArtifactKind.CandidateRunProvenance, "ranked-tb-simulation.log", simulationLogBytes, Hash(simulationLogBytes)),
            new("assist-ranked-fact-bus-layout", StudioArtifactKind.CandidateRunProvenance, "fact-bus-layout.json", layoutBytes, Hash(layoutBytes)),
            new("assist-ranked-source-input-hashes", StudioArtifactKind.CandidateRunProvenance, "source-inputs.json", sourceInputsBytes, Hash(sourceInputsBytes)),
            new("assist-ranked-rtl-compile-receipt", StudioArtifactKind.CandidateRunProvenance, "assist-candidate-attempt-rtl-compile-receipt.json", receiptBytes, Hash(receiptBytes)),
            new("assist-ranked-source-inventory", StudioArtifactKind.SourceInventory, "assist-ranked-source-inventory.json", inventoryBytes, Hash(inventoryBytes)),
            new("assist-ranked-captured-sources", StudioArtifactKind.CapturedSources, "assist-ranked-captured-sources.json", capturedSourcesBytes, Hash(capturedSourcesBytes)),
            new("assist-ranked-source-semantic", StudioArtifactKind.CandidateRunProvenance, "assist-ranked-source-semantic.json", semanticBytes, Hash(semanticBytes)),
            new("assist-ranked-source-capture-index", StudioArtifactKind.CandidateRunProvenance, "assist-ranked-source-capture-index.json", captureIndexBytes, Hash(captureIndexBytes)),
            new("scheduler-call-provenance", StudioArtifactKind.CandidateRunProvenance, "assist-ranked-scheduler-call-provenance.json", schedulerCallProvenanceBytes, Hash(schedulerCallProvenanceBytes)),
            new("scheduler-canonical-hwir", StudioArtifactKind.CandidateRunProvenance, "assist-ranked-canonical-hwir.json", canonicalHwirBytes, Hash(canonicalHwirBytes)),
            new("assist-ranked-toolchain", StudioArtifactKind.ToolchainLock, "assist-ranked-toolchain.json", toolchainBytes, Hash(toolchainBytes))
        ];
        CompilationStageRecord[] stages =
        [
            new(CompilationStage.HardwareIrValidation, CompilationStageStatus.Completed, "FailClosedHardwareIrValidator accepted source-bound candidate HWIR."),
            new(CompilationStage.RtlEmission, CompilationStageStatus.Completed, "Pinned CIRCT emitted RTL; Icarus compiled and executed the generated crosscheck testbench and wrote the attached VCD."),
            new(CompilationStage.IndependentVerification, CompilationStageStatus.Completed, $"A separately implemented recursive HWIR interpreter matched {crosscheckComparisons} emitted RTL outputs over {crosscheckVectors} deterministic vectors in Icarus, including directed rank-0 Stage A, Stage B, backpressure, quota and commit acceptance. This checks combinational lowering only; the interpreter consumes the same HWIR and is not source-level, Golden, or cycle-level verification.")
        ];
        var request = new StudioCompilerPublicationRequest("hybridcpu-assist-ranked-candidate-chain",
            "HybridCPU Assist four-rank typed candidate chain", "assist-ranked-" + rankedComposition.CanonicalDigest[..16],
            rankedComposition, target, stages, backend, supplements, SourceDigest: null);
        StudioPublicationReceipt publication = await new StudioSnapshotPublisher().PublishAsync(request, workspace);
        Console.WriteLine($"Assist ranked Studio project published: {publication.StableManifestPath}; snapshot={publication.SnapshotDigest}; manifestSha256={publication.ManifestSha256}; status={publication.Status}");
    }
    else
    {
        throw new ArgumentException("Usage: AssistCandidateAttempt [--emit <directory> | --publish <artifact-directory> <workspace-directory>]");
    }
}

static (string Testbench, object Receipt) BuildRankedRtlCrosscheck(HwValidatedDesign design, int randomCases)
{
    HwModule top = design.Design.Modules.Single(m => m.Id == design.Design.TopModule);
    var inputPorts = top.Ports.Where(p => p.Direction == HwPortDirection.Input).ToArray();
    var outputPorts = top.Ports.Where(p => p.Direction == HwPortDirection.Output).ToArray();
    var cases = new List<(string Name, Dictionary<string, ulong> Inputs)>
    {
        ("all_zero", Vector()),
        ("assist_disabled", Vector(("rank0_defined", 1), ("rank0_port_valid", 1), ("rank0_port_nonnull", 1))),
        ("boundary_blocked", Vector(("assist_enabled", 1), ("slot_available", 1), ("boundary_blocked", 1), ("rank0_defined", 1), ("rank0_port_valid", 1), ("rank0_port_nonnull", 1))),
        ("entry_open_empty_facts", Vector(("assist_enabled", 1), ("slot_available", 1), ("owner_vt", 3),
            ("rank0_defined", 1), ("rank0_vt", 0), ("rank0_port_valid", 1), ("rank0_port_nonnull", 1),
            ("rank1_defined", 1), ("rank1_vt", 1), ("rank1_port_valid", 1), ("rank1_port_nonnull", 1),
            ("rank2_defined", 1), ("rank2_vt", 2), ("rank2_port_valid", 1), ("rank2_port_nonnull", 1),
            ("rank3_defined", 1), ("rank3_vt", 0), ("rank3_port_valid", 1), ("rank3_port_nonnull", 1))),
        ("entry_open_dense_facts", DenseFacts()),
        ("rank0_hardpinned_dma_commit", SourceSupportedRank0Commit())
    };
    var random = new Random(0x417131);
    for (int index = 0; index < randomCases; index++)
    {
        Dictionary<string, ulong> input = Vector();
        foreach (var port in inputPorts)
        {
            ulong value = ((ulong)(uint)random.Next() << 32) | (uint)random.Next();
            input[port.Name] = value & Mask(port.Type.Width);
        }
        cases.Add(("random_" + index.ToString("D2"), input));
    }

    var expectations = new List<Dictionary<string, ulong>>(cases.Count);
    foreach (var item in cases) expectations.Add(EvaluateDesign(design.Design, item.Inputs));
    var b = new StringBuilder("`timescale 1ns/1ps\nmodule assist_ranked_candidate_chain_tb;\n");
    foreach (var port in inputPorts) b.AppendLine(Declaration("logic", port.Name, port.Type.Width));
    foreach (var port in outputPorts) b.AppendLine(Declaration("wire", port.Name, port.Type.Width));
    b.AppendLine("assist_ordered_candidate_chain dut (.*);\ninitial begin");
    b.AppendLine("  $dumpfile(\"assist-ranked-candidate-chain.vcd\"); $dumpvars(0, assist_ranked_candidate_chain_tb);");
    for (int index = 0; index < cases.Count; index++)
    {
        foreach (var port in inputPorts)
            b.AppendLine($"  {port.Name} = {Literal(port.Type.Width, cases[index].Inputs[port.Name])};");
        b.AppendLine("  #1;");
        foreach (var port in outputPorts)
        {
            ulong expected = expectations[index][port.Name];
            string literal = Literal(port.Type.Width, expected);
            b.AppendLine($"  if ({port.Name} !== {literal}) $fatal(1, \"{cases[index].Name} {port.Name}: got %h expected {literal}\", {port.Name});");
        }
        b.AppendLine($"  $display(\"PASS {cases[index].Name}\");");
    }
    b.AppendLine("  $display(\"PASS all ranked RTL crosscheck vectors\"); $finish;\nend\nendmodule");
    var commitCounts = Enumerable.Range(0, 4).ToDictionary(rank => "rank" + rank, rank => expectations.Count(values => values[$"rank{rank}_attempt_final_commit"] != 0));
    return (b.ToString(), new
    {
        schema = "circuitweave.assist-ranked-hwir-rtl-crosscheck/v1",
        status = "generated-independent-executor-expected-values",
        trust = "StructuralOnly",
        designCanonicalDigest = design.CanonicalDigest,
        vectorCount = cases.Count,
        directedCases = cases.Count - randomCases,
        randomCases,
        outputsCheckedPerVector = outputPorts.Length,
        totalOutputComparisons = cases.Count * outputPorts.Length,
        executor = "Independent recursive HWIR interpreter evaluates child instances and parent combinational nodes; compared against pinned-CIRCT RTL in Icarus.",
        rankCommitVectorCounts = commitCounts,
        directedOutcomes = cases.Take(cases.Count - randomCases).Select((item, index) => new
        {
            name = item.Name,
            finalScanActive = expectations[index]["final_scan_active"],
            injectionCount = expectations[index]["injection_count"],
            attempts = Enumerable.Range(0, 4).Select(rank => new
            {
                reached = expectations[index][$"rank{rank}_reached"],
                candidateValid = expectations[index][$"rank{rank}_candidate_valid"],
                stageA = expectations[index][$"rank{rank}_attempt_stage_a_pass"],
                stageB = expectations[index][$"rank{rank}_attempt_stage_b_pass"],
                backpressure = expectations[index][$"rank{rank}_attempt_backpressure_accepted"],
                quota = expectations[index][$"rank{rank}_attempt_quota_accepted"],
                committed = expectations[index][$"rank{rank}_attempt_final_commit"],
                rejectReason = expectations[index][$"rank{rank}_attempt_reject_reason"]
            }).ToArray()
        }).ToArray(),
        scope = "Combinational lowering crosscheck only; not a source-level oracle, sequential timing/reset/stall/replay test or Golden equivalence."
    });

    Dictionary<string, ulong> Vector(params (string Name, ulong Value)[] overrides)
    {
        var result = inputPorts.ToDictionary(port => port.Name, _ => 0UL, StringComparer.Ordinal);
        foreach ((string name, ulong value) in overrides)
        {
            HwPortContract port = inputPorts.Single(candidate => candidate.Name == name);
            result[name] = value & Mask(port.Type.Width);
        }
        return result;
    }

    Dictionary<string, ulong> DenseFacts()
    {
        Dictionary<string, ulong> result = Vector(("assist_enabled", 1), ("slot_available", 1), ("owner_vt", 3),
            ("rank0_defined", 1), ("rank0_vt", 0), ("rank0_port_valid", 1), ("rank0_port_nonnull", 1),
            ("rank1_defined", 1), ("rank1_vt", 1), ("rank1_port_valid", 1), ("rank1_port_nonnull", 1),
            ("rank2_defined", 1), ("rank2_vt", 2), ("rank2_port_valid", 1), ("rank2_port_nonnull", 1),
            ("rank3_defined", 1), ("rank3_vt", 0), ("rank3_port_valid", 1), ("rank3_port_nonnull", 1));
        foreach (var port in inputPorts.Where(port => port.Name.StartsWith("rank", StringComparison.Ordinal) && port.Name.Contains("_facts_", StringComparison.Ordinal)))
            result[port.Name] = ulong.MaxValue;
        return result;
    }

    Dictionary<string, ulong> SourceSupportedRank0Commit()
    {
        // Exact fact-bus words were derived from the composed child-port packing and
        // independently probed against the emitted RTL; see source test vectors in
        // SmtRankTailHwir and AssistQuota plus the accompanying fact-bus layout.
        var result = Vector(("assist_enabled", 1), ("slot_available", 1), ("owner_vt", 3),
            ("rank0_defined", 1), ("rank0_vt", 0), ("rank0_port_valid", 1), ("rank0_port_nonnull", 1),
            ("rank0_is_assist_op", 1), ("initial_state_admission_template_remaining", 1),
            ("initial_state_quota_remaining_issue_credits", 1), ("initial_state_quota_remaining_line_credits", 16),
            ("initial_state_quota_bp_shared_current", 9), ("initial_state_quota_lsu_carrier_current", 9),
            ("initial_state_quota_dma_carrier_current", 9));
        result["rank0_facts_00"] = 0x02000102;
        result["rank0_facts_01"] = 0x00400601;
        result["rank0_facts_02"] = 0x0200000000;
        result["rank0_facts_03"] = 0x700000007;
        result["rank0_facts_24"] = 0x04040808080202;
        result["rank0_facts_25"] = 0x0800000000000100;
        result["rank0_facts_26"] = 0x01000102;
        return result;
    }

    static string Declaration(string direction, string name, int width) => width == 1 ? $"{direction} {name};" : $"{direction} [{width - 1}:0] {name};";
    static string Literal(int width, ulong value) => $"{width}'h{value.ToString("x", System.Globalization.CultureInfo.InvariantCulture).PadLeft((width + 3) / 4, '0')}";
    static ulong Mask(int width) => width >= 64 ? ulong.MaxValue : (1UL << width) - 1;
}

static Dictionary<string, ulong> EvaluateDesign(HwDesign design, Dictionary<string, ulong> inputs)
{
    var modules = design.Modules.ToDictionary(module => module.Id, StringComparer.Ordinal);
    return EvaluateModule(modules[design.TopModule], inputs, new HashSet<string>(StringComparer.Ordinal));

    Dictionary<string, ulong> EvaluateModule(HwModule module, IReadOnlyDictionary<string, ulong> input, HashSet<string> active)
    {
        var nodes = module.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var drives = module.Nodes.OfType<HwOutputDriveNode>().ToDictionary(node => node.OutputPort, node => node.Value, StringComparer.Ordinal);
        var cache = new Dictionary<string, ulong>(input, StringComparer.Ordinal);
        var moduleInstances = module.Instances.ToArray();
        var instanceOutputNets = new Dictionary<string, (HwInstanceNode Instance, HwPortContract Port)>(StringComparer.Ordinal);
        var evaluatedInstances = new Dictionary<string, Dictionary<string, ulong>>(StringComparer.Ordinal);
        foreach (HwInstanceNode instance in moduleInstances)
        {
            HwModule child = modules[instance.ModuleReference];
            foreach (HwPortContract output in child.Ports.Where(port => port.Direction == HwPortDirection.Output))
            {
                string net = instance.PortConnections[output.Name];
                if (!instanceOutputNets.TryAdd(net, (instance, output)))
                    throw new InvalidDataException($"Multiple hierarchical outputs drive {module.Id}.{net}.");
            }
        }

        ulong Signal(string name)
        {
            if (cache.TryGetValue(name, out ulong known)) return known;
            string activeKey = module.Id + ":" + name;
            if (!active.Add(activeKey)) throw new InvalidDataException("Hierarchical combinational cycle at " + activeKey);
            try
            {
                ulong value;
                int width;
                if (instanceOutputNets.TryGetValue(name, out var driver))
                {
                    HwModule child = modules[driver.Instance.ModuleReference];
                    if (!evaluatedInstances.TryGetValue(driver.Instance.Id, out Dictionary<string, ulong>? childOutputs))
                    {
                        var childInputs = new Dictionary<string, ulong>(StringComparer.Ordinal);
                        foreach (HwPortContract port in child.Ports.Where(port => port.Direction == HwPortDirection.Input))
                            childInputs.Add(port.Name, Signal(driver.Instance.PortConnections[port.Name]));
                        childOutputs = EvaluateModule(child, childInputs, active);
                        evaluatedInstances.Add(driver.Instance.Id, childOutputs);
                    }
                    if (!childOutputs.TryGetValue(driver.Port.Name, out value))
                        throw new InvalidDataException($"Hierarchical oracle could not resolve {child.Id}.{driver.Port.Name} for {module.Id}.{driver.Instance.Id}; outputs={string.Join(',', childOutputs.Keys)}.");
                    width = driver.Port.Type.Width;
                }
                else
                {
                    if (!nodes.TryGetValue(name, out HwNode? node)) throw new InvalidDataException($"Undriven hierarchical signal {module.Id}.{name}.");
                    (value, width) = node switch
                    {
                        HwConstantNode constant => (Convert.ToUInt64(constant.BitPattern, 2), constant.Type.Width),
                        HwWireNode wire => throw new InvalidDataException($"Undriven wire {module.Id}.{wire.Id}."),
                        HwMuxNode mux => (Signal(mux.Condition) != 0 ? Signal(mux.WhenTrue) : Signal(mux.WhenFalse), mux.ResultType.Width),
                        HwOperationNode operation => (Apply(operation), operation.ResultType.Width),
                        _ => throw new InvalidDataException("Unsupported combinational HWIR node " + node.GetType().Name)
                    };
                }
        value &= BitMask(width);
                cache[name] = value;
                return value;
            }
            finally { active.Remove(activeKey); }
        }

        ulong Apply(HwOperationNode operation)
        {
            ulong a = Signal(operation.Operands[0]);
            ulong b = operation.Operands.Count > 1 ? Signal(operation.Operands[1]) : 0;
            return operation.Opcode switch
            {
                "and" => a & b, "or" => a | b, "xor" => a ^ b, "eq" => a == b ? 1UL : 0UL,
                "ult64" => a < b ? 1UL : 0UL, "add" => a + b, "sub" => a - b, "zext" or "trunc" => a,
                "lshr64_mask6" => a >> (int)(b & 63), "lshr32_mask5" => a >> (int)(b & 31),
                "shl32_mask5" => a << (int)(b & 31), _ => throw new InvalidDataException("Unsupported HWIR operation " + operation.Opcode)
            };
        }

        var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (HwPortContract port in module.Ports.Where(port => port.Direction == HwPortDirection.Output))
        {
            string drivenSignal = drives.GetValueOrDefault(port.Name, port.Name);
            result.Add(port.Name, Signal(drivenSignal));
        }
        return result;
    }
}

static ulong BitMask(int width) => width >= 64 ? ulong.MaxValue : (1UL << width) - 1;
