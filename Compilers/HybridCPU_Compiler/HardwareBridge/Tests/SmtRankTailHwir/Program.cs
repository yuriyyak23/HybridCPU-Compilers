using System.Security.Cryptography;
using System.Numerics;
using System.Text;
using System.Text.Json;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using CircuitWeave.Orchestration;
using CircuitWeave.Visualization.Publishing;
using CircuitWeave.Visualization;
using CircuitWeave.Studio.Integration;
using CircuitWeave.Studio.GraphNavigation;
using HybridCPU.Compiler.HardwareBridge.Hwir;

const string ModelRoot = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE";
const string BridgeRoot = @"C:\Users\Yuriy\Desktop\HybridCPU Compilers\Compilers\HybridCPU_Compiler\HardwareBridge";
const string Commit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
const string AdmissionRel = @"CloseToHSL\Core\Pipeline\Scheduling\Admission\MicroOpScheduler.Admission.cs";
const string ScoreboardRel = @"CloseToHSL\Core\Pipeline\Scheduling\Scoreboard\MicroOpScheduler.Scoreboard.cs";
const string InfrastructureRel = @"CloseToHSL\Core\Pipeline\Scheduling\MicroOpScheduler.Infrastructure.cs";
const string SchedulerRel = @"CloseToHSL\Core\Pipeline\Scheduling\MicroOpScheduler.cs";
const string SmtRel = @"CloseToHSL\Core\Pipeline\Scheduling\Smt\MicroOpScheduler.SMT.cs";
const string AssistRel = @"CloseToHSL\Core\Pipeline\Assist\Runtime\MicroOpScheduler.Assist.cs";
const string GuardRel = @"CloseToHSL\Core\Pipeline\Safety\SafetyVerifier.SmtLegality.cs";
const string GuardsRel = @"CloseToHSL\Core\Pipeline\Safety\SafetyVerifier.Guards.cs";
const string MicroOpRel = @"CloseToHSL\Core\Pipeline\MicroOps\Types\MicroOp.cs";
const string PlacementRel = @"CloseToHSL\Core\Pipeline\Scheduling\SlotPlacementMetadata.cs";
const string CertificateRel = @"CloseToHSL\Core\Pipeline\Certificates\BundleResourceCertificate4Way.cs";
const string AdmissionMetadataRel = @"CloseToHSL\Core\Pipeline\Metadata\MicroOpAdmissionMetadata.cs";
const string LoadStoreRel = @"CloseToHSL\Core\Pipeline\MicroOps\Memory\MicroOp.LoadStore.cs";
const string AtomicRel = @"CloseToHSL\Core\Pipeline\MicroOps\Types\MicroOp.Misc.cs";
const string OccupancyRel = @"CloseToHSL\Core\Memory\IHardwareOccupancyInput.cs";
if (args.Length == 3 && args[0] == "--publish")
{
    await PublishCandidate(args[1], args[2]);
    return;
}
if (args.Length != 1) throw new ArgumentException("Usage: SmtRankTailHwir <existing-output-directory-inside-HardwareBridge>");
string output = Path.GetFullPath(args[0]);
if (!output.StartsWith(BridgeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(output))
    throw new InvalidOperationException("Output must be an existing directory under HardwareBridge.");
byte[] admission = File.ReadAllBytes(Path.Combine(ModelRoot, AdmissionRel));
byte[] scoreboard = File.ReadAllBytes(Path.Combine(ModelRoot, ScoreboardRel));
byte[] infrastructure = File.ReadAllBytes(Path.Combine(ModelRoot, InfrastructureRel));
byte[] scheduler = File.ReadAllBytes(Path.Combine(ModelRoot, SchedulerRel));
byte[] smt = File.ReadAllBytes(Path.Combine(ModelRoot, SmtRel));
byte[] assist = File.ReadAllBytes(Path.Combine(ModelRoot, AssistRel));
byte[] guardSource = File.ReadAllBytes(Path.Combine(ModelRoot, GuardRel));
byte[] guardsSource = File.ReadAllBytes(Path.Combine(ModelRoot, GuardsRel));
byte[] loadStoreSource = File.ReadAllBytes(Path.Combine(ModelRoot, LoadStoreRel));
byte[] atomicSource = File.ReadAllBytes(Path.Combine(ModelRoot, AtomicRel));
byte[] occupancySource = File.ReadAllBytes(Path.Combine(ModelRoot, OccupancyRel));
byte[] Read(string relative) => File.ReadAllBytes(Path.Combine(ModelRoot, relative));
string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
string Git(params string[] gitArgs)
{
    var start = new System.Diagnostics.ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (string arg in gitArgs) start.ArgumentList.Add(arg);
    using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
    string stdout = process.StandardOutput.ReadToEnd(); string stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException("git failed: " + stderr);
    return stdout.TrimEnd('\r', '\n');
}
string modelGitHead = Git("-C", ModelRoot, "rev-parse", "HEAD");
if (modelGitHead != Commit) throw new InvalidDataException($"Model HEAD {modelGitHead} differs from pinned base {Commit}.");
string dirtyInputStatus = Git("-C", ModelRoot, "status", "--short", "--", AdmissionRel, SchedulerRel, SmtRel, AssistRel,
    ScoreboardRel, InfrastructureRel,
    @"CloseToHSL\Core\Pipeline\Scheduling\SlotLegality\SlotClassDefinitions.cs", @"CloseToHSL\Core\Pipeline\Scheduling\SlotLegality\SlotClassCapacity.cs",
    @"CloseToHSL\Core\Pipeline\Scheduling\LaneId.cs", @"CloseToHSL\Core\Pipeline\Scheduling\LaneChoice\DeterministicLaneChooser.cs",
    GuardRel, GuardsRel, MicroOpRel, PlacementRel, CertificateRel, AdmissionMetadataRel, LoadStoreRel, AtomicRel, OccupancyRel);
string[] dirtyInputPaths = dirtyInputStatus.Split('\n', StringSplitOptions.RemoveEmptyEntries);
string modelTreeState = dirtyInputPaths.Length == 0 ? "listed-inputs-clean-at-base-commit" : "dirty-working-tree-listed-inputs-captured";
var tc = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
string[] sourceHashes = new[] { Hash(smt), Hash(scheduler), Hash(admission) };
byte[] classes = Read(@"CloseToHSL\Core\Pipeline\Scheduling\SlotLegality\SlotClassDefinitions.cs");
byte[] capacitySource = Read(@"CloseToHSL\Core\Pipeline\Scheduling\SlotLegality\SlotClassCapacity.cs");
byte[] laneIdSource = Read(@"CloseToHSL\Core\Pipeline\Scheduling\LaneId.cs");
byte[] chooserSource = Read(@"CloseToHSL\Core\Pipeline\Scheduling\LaneChoice\DeterministicLaneChooser.cs");
var pinnedSources = new (string Name, string RelativePath, byte[] Bytes)[]
{
    ("admission", AdmissionRel, admission), ("scoreboard", ScoreboardRel, scoreboard), ("infrastructure", InfrastructureRel, infrastructure), ("scheduler", SchedulerRel, scheduler), ("smt", SmtRel, smt),
    ("assist", AssistRel, assist), ("classes", @"CloseToHSL\Core\Pipeline\Scheduling\SlotLegality\SlotClassDefinitions.cs", classes),
    ("capacity", @"CloseToHSL\Core\Pipeline\Scheduling\SlotLegality\SlotClassCapacity.cs", capacitySource),
    ("laneId", @"CloseToHSL\Core\Pipeline\Scheduling\LaneId.cs", laneIdSource),
    ("laneChooser", @"CloseToHSL\Core\Pipeline\Scheduling\LaneChoice\DeterministicLaneChooser.cs", chooserSource),
    ("smtLegality", GuardRel, guardSource), ("guards", GuardsRel, guardsSource), ("loadStore", LoadStoreRel, loadStoreSource), ("atomic", AtomicRel, atomicSource), ("occupancy", OccupancyRel, occupancySource),
    ("microOp", MicroOpRel, Read(MicroOpRel)), ("slotPlacement", PlacementRel, Read(PlacementRel)),
    ("bundleResourceCertificate", CertificateRel, Read(CertificateRel)), ("admissionMetadata", AdmissionMetadataRel, Read(AdmissionMetadataRel))
};
var capturedFileIdentities = pinnedSources.Select(s => new { path = Path.GetFullPath(Path.Combine(ModelRoot, s.RelativePath)), sha256 = Hash(s.Bytes), length = s.Bytes.LongLength }).ToArray();
string sourceReportHash = Hash(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { modelGitHead, modelTreeState, dirtyInputPaths, files = capturedFileIdentities }));
byte[] sourceCaptureIndexBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { schema = "circuitweave.smt-source-capture-index/v1", modelCommit = Commit, modelGitHead, modelTreeState, dirtyInputPaths, files = capturedFileIdentities }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
string sourceCaptureHash = Hash(sourceCaptureIndexBytes);
File.WriteAllBytes(Path.Combine(output, "smt-rank-tail-source-capture-index.json"), sourceCaptureIndexBytes);
HwValidatedDesign stageA = BridgeHwirClassAdmission.Compile(admission, scheduler, classes,
    [Hash(admission), Hash(scheduler), Hash(classes)], Commit, tc);
HwValidatedDesign capacity = BridgeHwirClassCapacity.Compile(capacitySource, classes,
    [Hash(capacitySource), Hash(classes)], Commit, tc);
stageA = BridgeHwirClassCapacity.AttachToStageA(stageA, capacity);
HwValidatedDesign stageB = BridgeHwirLaneMaterialization.Compile(admission, laneIdSource, classes, chooserSource,
    [Hash(admission), Hash(laneIdSource), Hash(classes), Hash(chooserSource)], Commit, tc);
HwValidatedDesign guardPlane = BridgeHwirSmtLegalityGuardPlane.Compile(guardSource, guardsSource, tc);
HwValidatedDesign certificatePredicate = BridgeHwirSmtCertificatePredicate.Compile(Read(CertificateRel), Read(AdmissionMetadataRel), tc);
HwValidatedDesign outerGate = BridgeHwirSmtOuterGate.Compile(admission, classes, scoreboard, infrastructure, loadStoreSource, atomicSource, occupancySource, scheduler, [Hash(admission), Hash(classes), Hash(scoreboard), Hash(infrastructure), Hash(loadStoreSource), Hash(atomicSource), Hash(occupancySource), Hash(scheduler)], Commit, tc);
HwValidatedDesign decision = BridgeHwirAdmissionDecision.Compile(smt, Hash(smt), stageA, stageB, guardPlane, certificatePredicate, outerGate, Commit, capacitySource, Hash(capacitySource));
HwValidatedDesign fourCandidateAdmission = BridgeHwirTwoCandidateAdmission.CompileFour(smt, scheduler, capacitySource, classes,
    [Hash(smt), Hash(scheduler)], decision, Commit, trackVt: true);
HwValidatedDesign order = BridgeHwirPressureOrder.Compile(smt, scheduler, admission, sourceHashes, Commit, tc);
HwValidatedDesign ordered = BridgeHwirOrderedAdmission.Compile(order, fourCandidateAdmission, guardPlane, perAttemptGates: true);
HwValidatedDesign pressure = BridgeHwirBankPressure.Compile(smt, scheduler, admission, sourceHashes, Commit, tc);
HwValidatedDesign scored = BridgeHwirBankPressure.AttachToOrderedAdmission(smt, scheduler, admission, Commit, pressure, ordered);
HwValidatedDesign collected = BridgeHwirCandidateCollection.Compile(smt, scheduler, admission, Commit, scored);
HwValidatedDesign candidate = BridgeHwirSmtRanking.Compile(smt, scheduler, admission, assist, Commit, collected, includeCollectedHierarchy: true);
HwValidatedDesign projectedMemoryState = BridgeHwirProjectedMemoryState.Compile(admission, scoreboard, scheduler, occupancySource, Commit, tc);
candidate = BridgeHwirProjectedMemoryState.Attach(candidate, projectedMemoryState);
string FullCapturedPath(string sourcePath)
{
    var match = pinnedSources.SingleOrDefault(s => ("HybridCPU_ISE/" + s.RelativePath.Replace('\\', '/')).EndsWith(sourcePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
    if (match.Bytes is null) throw new InvalidDataException("No captured source input for HWIR origin " + sourcePath);
    return Path.GetFullPath(Path.Combine(ModelRoot, match.RelativePath));
}
var sourceOrigins = candidate.Design.Modules.SelectMany(m => m.Nodes.Select(n => n.Origin).Append(m.Origin))
    .Where(o => o is not null && o.Commit == Commit && pinnedSources.Any(s => ("HybridCPU_ISE/" + s.RelativePath.Replace('\\', '/')).EndsWith(o.Path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
    .Select(o => o!).GroupBy(o => o.Type + "." + o.Member, StringComparer.Ordinal).ToArray();
object[] sourceSymbols = sourceOrigins.Select(group =>
{
    var origins = group.GroupBy(o => (FullCapturedPath(o.Path), o.Line, o.Column)).Select(g => g.First()).ToArray();
    string symbol = group.Key;
    string memberId = "M:" + symbol + "()";
    var locations = origins.Select(o => new { file = FullCapturedPath(o.Path), startLine = o.Line, startColumn = o.Column }).ToArray();
    return (object)new
    {
        symbol = "global::" + symbol, assembly = origins[0].Assembly, kind = "Class", memberCount = 1,
        declarations = new[] { new { location = new { file = locations[0].file, startLine = locations[0].startLine, startColumn = locations[0].startColumn }, memberIds = new[] { memberId } } },
        members = new[] { new { id = memberId, kind = "Method", observedDependencyCount = 0, locations } },
        blockingReason = "Only source-mapped HWIR members are listed; not a complete Roslyn type/member inventory."
    };
}).ToArray();
var sourceInventory = new
{
    schema = "circuitweave.source-coverage-index/v1", disposition = "incomplete-unqualified",
    project = "HybridCPU_ISE/HybridCPU_ISE.csproj", configuration = "Debug", targetFramework = "net11.0",
    defines = new[] { "TRACE", "TESTING", "DEBUG" }, semanticReportSha256 = sourceReportHash,
    scope = "Twelve exact inputs and source-mapped HWIR members for this SMT combinational composition; listed inputs are captured from the current dirty worktree where marked; base commit is not asserted as their byte source; not project symbol closure.",
    symbols = sourceSymbols,
    captureVerification = new { schema = "circuitweave.source-capture-check/v1", status = "producer-reported-listed-inputs-match", semanticReportSha256 = sourceReportHash, sourceCaptureSha256 = sourceCaptureHash, projectGraphSha256 = "d1545a671ebd657cb5d6e3db4c6ccfff75eaa1cbba3bf5e184f865b0cb39c0d0", scope = "Exact listed hashes recomputed from current worktree bytes; working-tree state is recorded; listed member mapping does not establish complete Roslyn inventory.", checkedFiles = pinnedSources.Length, rootProject = "not-captured" },
    capturedSourceFiles = capturedFileIdentities
};
byte[] sourceInventoryBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(sourceInventory, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
string sourceInventoryHash = Hash(sourceInventoryBytes);
File.WriteAllBytes(Path.Combine(output, "smt-rank-tail-source-inventory.json"), sourceInventoryBytes);
File.WriteAllBytes(Path.Combine(output, "smt-rank-tail-captured-sources.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
{
    schema = "circuitweave.captured-sources/v1", inventorySha256 = sourceInventoryHash,
    files = pinnedSources.Select(s => new { path = Path.GetFullPath(Path.Combine(ModelRoot, s.RelativePath)), bytes = Convert.ToBase64String(s.Bytes) }).ToArray()
}, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
var mlir = new HierarchicalCirctMlirLowerer().LowerToMlir(candidate,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>()));
File.WriteAllBytes(Path.Combine(output, "smt-rank-tail.hwir.json"), new CanonicalHwirSerializer().Serialize(candidate));
File.WriteAllText(Path.Combine(output, "smt-rank-tail.tb.sv"), BuildDirectedRtlTestbench(candidate), new UTF8Encoding(false));
File.WriteAllText(Path.Combine(output, "smt-rank-tail.mlir"), mlir.Text, new UTF8Encoding(false));
var semantic = RunCompositionVectors(candidate, fourCandidateAdmission, outerGate);
File.WriteAllText(Path.Combine(output, "smt-rank-tail-semantic-crosscheck.json"),
    System.Text.Json.JsonSerializer.Serialize(new { vectors = semantic.Vectors, oracle = semantic.Oracle, status = semantic.Status, scope = semantic.Scope }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
    new UTF8Encoding(false));
File.WriteAllText(Path.Combine(output, "smt-rank-tail-source-pins.json"), System.Text.Json.JsonSerializer.Serialize(new
{
    modelCommit = Commit,
    modelGitHead,
    modelTreeState,
    dirtyInputPaths,
    module = candidate.Design.TopModule,
    hwirSha256 = candidate.CanonicalDigest,
    mlirSha256 = mlir.ContentDigest,
    sources = new { admission = Hash(admission), scheduler = Hash(scheduler), smt = Hash(smt), assist = Hash(assist), classes = Hash(classes), capacity = Hash(capacitySource), laneId = Hash(laneIdSource), laneChooser = Hash(chooserSource), smtLegality = Hash(guardSource), guards = Hash(guardsSource), microOp = Hash(Read(MicroOpRel)), slotPlacement = Hash(Read(PlacementRel)) },
    semanticCrosscheck = new { file = "smt-rank-tail-semantic-crosscheck.json", sha256 = Hash(File.ReadAllBytes(Path.Combine(output, "smt-rank-tail-semantic-crosscheck.json"))), vectors = semantic.Vectors, status = semantic.Status, scope = semantic.Scope },
    sourceInventorySha256 = sourceInventoryHash,
    outputs = new[] { "ranked_vt0", "ranked_vt1", "ranked_vt2", "ranked_vt3", "main_rank3_duplicate_snapshot_condition", "assist_rank3_index_oob_if_reached", "c0-c3 accepted/reject_reason/selected_lane/stage_b_evaluated", "c0-c3 guard_boundary_reject/owner_context_reject/domain_reject/certificate_checked/replay_certificate_selected/structural_certificate_selected/legality_allowed", "initial_occupancy", "initial_empty_slot_mask", "initial_reference_opportunity_mask", "initial_class_occupied0..5 from same eight slot presence/class tuples", "final_next_occupancy", "final_empty_slot_mask", "final_reference_opportunity_mask", "final_next_occupied0..5", "final_budget0..7", "final_injections" },
    testbenchSha256 = Hash(File.ReadAllBytes(Path.Combine(output, "smt-rank-tail.tb.sv"))),
    classification = "combinational SMT rank/collection with four-candidate Stage A/B admission; initial lane occupancy, empty slots, reference opportunities and six typed class counts are derived from eight sampled original-bundle presence/class facts and updated on accepted candidates; slot facts are not bound to live MicroOp object identity, template/replay state and some A3 facts remain explicit inputs; Assist OOB is conditional on loop reach; not a clocked scheduler model"
}, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
Console.WriteLine($"Composed rank→collect→score→ordered admission HWIR={candidate.CanonicalDigest} MLIR={mlir.ContentDigest} modules={candidate.Design.Modules.Count} top={candidate.Design.TopModule}");
Console.WriteLine($"Independent combinational composition crosscheck PASS: {semantic.Vectors} vectors; 4,704 rank/collection plus 1,024 eight-slot opportunity-mask vectors and 57 directed retry/certificate/replay/guard/A3/projected-state/budget-init/budget-cascade/counter cases; no cycle/RTL equivalence.");
Console.WriteLine(Path.Combine(output, "smt-rank-tail.mlir"));

static (long Vectors, string Oracle, string Status, string Scope) RunCompositionVectors(HwValidatedDesign design, HwValidatedDesign fourCandidateAdmission, HwValidatedDesign outerGate)
{
    var modules = design.Design.Modules.ToDictionary(m => m.Id, StringComparer.Ordinal);
    var top = modules[design.Design.TopModule];
    long cases = 0;
    int[][] creditPatterns =
    [
        [0, 0, 0, 0], [16, 16, 16, 16], [9, 9, 2, 1], [3, 8, 8, 2],
        [0, 16, 0, 16], [1, 2, 3, 4], [16, 0, 8, 4]
    ];
    int[] tails = [0, 1, 2, 3, 4, unchecked((int)0xfffffffe), unchecked((int)0xffffffff)];
    foreach (uint owner in new uint[] { 0, 1, 2, 3, 4, uint.MaxValue })
    for (int pattern = 0; pattern < 16; pattern++)
    for (int seed = 0; seed < creditPatterns.Length; seed++)
    for (int tailIx = 0; tailIx < tails.Length; tailIx++)
    {
        var inputs = top.Ports.Where(p => p.Direction == HwPortDirection.Input)
            .ToDictionary(p => p.Name, _ => BigInteger.Zero, StringComparer.Ordinal);
        inputs["owner_vt"] = owner;
        inputs["fairness_enabled"] = BigInteger.One;
        inputs["cycle"] = 1;
        inputs["unwritten_tail"] = (uint)tails[tailIx];
        for (int vt = 0; vt < 4; vt++)
        {
            uint credit = (uint)creditPatterns[seed][vt];
            inputs[$"credit{vt}"] = credit;
            bool readyPresent = (pattern & (1 << vt)) != 0;
            inputs[$"vt{vt}_ready"] = readyPresent ? BigInteger.One : BigInteger.Zero;
            inputs[$"vt{vt}_present"] = readyPresent ? BigInteger.One : BigInteger.Zero;
            inputs[$"vt{vt}_is_loadstore"] = (vt & 1) == 0 ? BigInteger.One : BigInteger.Zero;
            inputs[$"vt{vt}_bank_id"] = (uint)(vt + seed);
        }
        var actual = EvaluateModule(top, inputs);
        int[] ranked = Enumerable.Range(0, 4).Where(v => owner >= 4 || v != (int)owner).ToArray();
        Array.Sort(ranked, (a, b) =>
        {
            uint ca = (uint)inputs[$"credit{a}"], cb = (uint)inputs[$"credit{b}"];
            int byCredit = cb.CompareTo(ca);
            return byCredit != 0 ? byCredit : a.CompareTo(b);
        });
        int tail = owner < 4 ? tails[tailIx] : ranked[3];
        int expectedCount = 0;
        foreach (int vt in ranked.Take(3).Append(tail))
            if ((uint)vt < 4 && vt != (int)owner && (pattern & (1 << vt)) != 0) expectedCount++;
        bool expectedDuplicate = (uint)tail < 4 && tail != (int)owner && ranked.Take(3).Contains(tail) && (pattern & (1 << tail)) != 0;
        bool expectedOobCondition = (uint)tail >= 4 && tail != (int)owner;
        if (actual["ranked_vt0"] != ranked[0] || actual["ranked_vt1"] != ranked[1] || actual["ranked_vt2"] != ranked[2] || actual["ranked_vt3"] != (uint)tail ||
            actual["collected_count"] != expectedCount ||
            actual["main_rank3_duplicate_snapshot_condition"] != (expectedDuplicate ? 1 : 0) ||
            actual["assist_rank3_index_oob_if_reached"] != (expectedOobCondition ? 1 : 0))
            throw new InvalidOperationException($"Composition oracle mismatch vector {cases}: owner={owner}, credits=[{string.Join(',', Enumerable.Range(0,4).Select(v => inputs[$"credit{v}"].ToString()))}], mask={pattern:X}, rank3={tail:X8}, rank=[{actual["ranked_vt0"]},{actual["ranked_vt1"]},{actual["ranked_vt2"]},{actual["ranked_vt3"]}]/[{ranked[0]},{ranked[1]},{ranked[2]},{tail}], count={actual["collected_count"]}/{expectedCount}, duplicate={actual["main_rank3_duplicate_snapshot_condition"]}/{expectedDuplicate}, oob={actual["assist_rank3_index_oob_if_reached"]}/{expectedOobCondition}.");
        cases++;
    }
    RunBundleOpportunityStateCases();
    RunDuplicateRetryCases();
    return (cases, "separately coded fair-rank + collection + four-candidate certificate/A3/A/B retry-state contract", "passed", "4,704 ranking/collection vectors, 1,024 eight-slot opportunity-mask and typed class-count vectors, and 57 directed retry, shared/register certificate, replay select, guard, scoreboard-derived per-VT bank-pending, bundle-local Store bank mask, sampled SuppressLsu predicate, projected-memory state, source-derived A3 memory-budget predicate, accepted-candidate budget cascade, original-bundle budget initialization, six independently derived A3 speculation-predicate truth-table cases, four source-ordered speculation-exhaustion event counter cases, two standalone direct-gate owner cases, and two SmtRejectionsCount accounting cases. Initial occupancy and empty/reference-opportunity masks follow BundleOpportunityState.Create from eight sampled slot-presence bits and sampled packed NOP/stealable masks. Six typed class occupancy counts follow SlotClassCapacity.ComputeFromBundle from the same eight sampled presence/class tuples; accepted candidates update counts only on commit. These facts are not bound to the runtime result array or MicroOp object identity, and replay template state remains sampled. Assist quota/backpressure, legacy untyped admission, FSP and scheduler runtime identity remain outside this counter projection. No clocked lifecycle or Golden equivalence");

    void RunBundleOpportunityStateCases()
    {
        for (int mode = 0; mode < 4; mode++)
        for (int pattern = 0; pattern < 256; pattern++)
        {
            var inputs = top.Ports.Where(p => p.Direction == HwPortDirection.Input)
                .ToDictionary(p => p.Name, _ => BigInteger.Zero, StringComparer.Ordinal);
            int slotPresenceMask = mode == 0 ? pattern : 255;
            int nopMask = mode switch { 0 => 0, 1 or 2 => pattern, _ => 255 };
            int stealableMask = mode switch { 1 => pattern, 3 => pattern, _ => 0 };
            int expectedOccupancy = slotPresenceMask, expectedEmpty = 255 ^ slotPresenceMask;
            int expectedReference = expectedEmpty | (nopMask & stealableMask);
            int[] expectedClassCounts = new int[6];
            for (int slot = 0; slot < 8; slot++)
            {
                bool present = (slotPresenceMask & (1 << slot)) != 0;
                inputs[$"bundle_slot{slot}_present"] = present ? BigInteger.One : BigInteger.Zero;
                int slotClass = (slot + pattern + mode) & 7;
                inputs[$"bundle_slot{slot}_class"] = slotClass;
                if (present && slotClass < expectedClassCounts.Length) expectedClassCounts[slotClass]++;
            }
            inputs["bundle_is_nop_mask"] = nopMask;
            inputs["bundle_stealable_mask"] = stealableMask;
            var actual = EvaluateModule(top, inputs);
            if (actual["initial_occupancy"] != expectedOccupancy || actual["initial_empty_slot_mask"] != expectedEmpty ||
                actual["initial_reference_opportunity_mask"] != expectedReference || actual["final_next_occupancy"] != expectedOccupancy ||
                actual["final_empty_slot_mask"] != expectedEmpty || actual["final_reference_opportunity_mask"] != expectedReference)
                throw new InvalidOperationException($"BundleOpportunityState.Create mismatch mode={mode}, pattern=0x{pattern:X2}: occupancy={actual["initial_occupancy"]}/{expectedOccupancy}, empty={actual["initial_empty_slot_mask"]}/{expectedEmpty}, reference={actual["initial_reference_opportunity_mask"]}/{expectedReference}.");
            for (int classIndex = 0; classIndex < expectedClassCounts.Length; classIndex++)
                if (actual[$"initial_class_occupied{classIndex}"] != expectedClassCounts[classIndex])
                    throw new InvalidOperationException($"SlotClassCapacity.ComputeFromBundle mismatch mode={mode}, pattern=0x{pattern:X2}, class={classIndex}: actual={actual[$"initial_class_occupied{classIndex}"]}, expected={expectedClassCounts[classIndex]}.");
            cases++;
        }
    }

    void RunDuplicateRetryCases()
    {
        Dictionary<string, BigInteger> Inputs(bool firstLegal, byte initialOccupancy)
        {
            var x = top.Ports.Where(p => p.Direction == HwPortDirection.Input)
                .ToDictionary(p => p.Name, _ => BigInteger.Zero, StringComparer.Ordinal);
            x["owner_vt"] = 3; x["fairness_enabled"] = 1; x["cycle"] = 1; x["unwritten_tail"] = 0;
            x["credit0"] = 16; x["credit1"] = 8; x["credit2"] = 4; x["credit3"] = 0;
            x["sort_enabled"] = 0; x["template_hit"] = 1; x["budget2"] = 1;
            x["current_injections"] = 0;
            x["donor_mask"] = 0; x["replay_active"] = 0; x["stable_donor"] = 0;
            x["boundary_blocked"] = 0; x["owner_context_known"] = 1; x["owner_context"] = 7;
            x["owner_domain_certificate"] = 0; x["kernel_domain_isolation"] = 0;
            x["memory_bank_budget_at_least_one_mask"] = ushort.MaxValue; x["memory_bank_budget_at_least_two_mask"] = ushort.MaxValue;
            x["read_bank_budget_at_least_one_mask"] = ushort.MaxValue; x["read_bank_budget_at_least_two_mask"] = ushort.MaxValue;
            x["write_bank_budget_at_least_one_mask"] = ushort.MaxValue; x["write_bank_budget_at_least_two_mask"] = ushort.MaxValue;
            x["snapshot_memory_issue_budget"] = 2; x["snapshot_read_issue_budget"] = 2; x["snapshot_write_issue_budget"] = 2;
            for (int vt = 0; vt < 4; vt++)
                for (int slot = 0; slot < 8; slot++)
                {
                    x[$"score_vt{vt}_slot{slot}_value"] = uint.MaxValue;
                    x[$"score_vt{vt}_slot{slot}_type"] = 0;
                    x[$"score_vt{vt}_slot{slot}_bank_id"] = uint.MaxValue;
                }
            for (int slot = 0; slot < 8; slot++)
            {
                x[$"bundle_slot{slot}_present"] = (initialOccupancy & (1 << slot)) != 0 ? 1 : 0;
                x[$"bundle_slot{slot}_class"] = 7;
                x[$"bundle_slot{slot}_is_loadstore"] = 0;
                x[$"bundle_slot{slot}_is_load"] = 0;
                x[$"bundle_slot{slot}_vt"] = 0;
            }
            if (!firstLegal) { x["bundle_shared_low"] = 1; x["vt0_candidate_shared_low"] = 1; }
            for (int i = 0; i < 4; i++)
            {
                bool ready = i < 3;
                x[$"vt{i}_ready"] = ready ? 1 : 0; x[$"vt{i}_present"] = ready ? 1 : 0;
                x[$"vt{i}_is_loadstore"] = 0; x[$"vt{i}_bank_id"] = 0;
                x[$"vt{i}_is_load"] = 0; x[$"vt{i}_is_store"] = 0;
                x[$"vt{i}_slot_class"] = 2; x[$"vt{i}_pinning_kind"] = 1;
                x[$"vt{i}_pinned_lane"] = 6; x[$"vt{i}_permitted_lanes"] = 1 << 6;
                x[$"vt{i}_opportunistic"] = 0; x[$"vt{i}_is_assist"] = 0; x[$"vt{i}_is_memory"] = 0; x[$"vt{i}_is_loadstore"] = 0;
                x[$"vt{i}_owner_context_id"] = 7; x[$"vt{i}_domain_tag"] = 0;
                x[$"attempt{i}_phase_key_valid"] = 0; x[$"attempt{i}_phase_template_matches"] = 0;
                x[$"vt{i}_outer_gate_condition_facts"] = 0;
            }
            return x;
        }
        static BigInteger RejectedAttemptCount(IReadOnlyDictionary<string, BigInteger> actual) =>
            Enumerable.Range(0, 4).Count(i => actual[$"c{i}_reject_reason"] != BigInteger.Zero);

        var successInputs = Inputs(firstLegal: true, initialOccupancy: 0);
        var success = EvaluateModule(top, successInputs);
        Require(success, "ranked_vt0", 0); Require(success, "ranked_vt3", 0); Require(success, "collected_count", 4);
        Require(success, "c0_accepted", 1); Require(success, "c0_selected_lane", 6); Require(success, "c0_stage_b_evaluated", 1);
        Require(success, "c3_accepted", 0); Require(success, "c3_stage_b_evaluated", 0);
        Require(success, "final_next_occupancy", 64); Require(success, "final_next_occupied2", 1);
        Require(success, "final_budget2", 0); Require(success, "final_injections", 1);
        cases++;

        var sharedCascade = Inputs(firstLegal: true, initialOccupancy: 0);
        sharedCascade["snapshot_memory_issue_budget"] = 1;
        sharedCascade["budget2"] = 2;
        foreach (int vt in new[] { 0, 1 }) { sharedCascade[$"vt{vt}_is_memory"] = 1; sharedCascade[$"vt{vt}_is_loadstore"] = 1; sharedCascade[$"vt{vt}_is_load"] = 1; sharedCascade[$"vt{vt}_bank_id"] = 6; }
        sharedCascade["vt1_pinned_lane"] = 5; sharedCascade["vt1_permitted_lanes"] = 1 << 5;
        var sharedCascadeResult = EvaluateModule(top, sharedCascade);
        Require(sharedCascadeResult, "c0_accepted", 1); Require(sharedCascadeResult, "c1_accepted", 0); Require(sharedCascadeResult, "c1_reject_reason", 7);
        Require(sharedCascadeResult, "final_remaining_memory_issue_budget", 0); Require(sharedCascadeResult, "final_remaining_read_issue_budget", 1);
        Require(sharedCascadeResult, "final_consumed_memory_budget_by_bank", 1 << 12); Require(sharedCascadeResult, "final_consumed_read_budget_by_bank", 1 << 12);
        cases++;

        var failedStageBCascade = Inputs(firstLegal: true, initialOccupancy: 64);
        failedStageBCascade["snapshot_memory_issue_budget"] = 1; failedStageBCascade["vt0_is_memory"] = 1; failedStageBCascade["vt0_is_loadstore"] = 1; failedStageBCascade["vt0_is_load"] = 1; failedStageBCascade["vt0_bank_id"] = 6;
        var failedStageBCascadeResult = EvaluateModule(top, failedStageBCascade);
        Require(failedStageBCascadeResult, "c0_accepted", 0); Require(failedStageBCascadeResult, "c0_reject_reason", 11);
        Require(failedStageBCascadeResult, "final_remaining_memory_issue_budget", 1); Require(failedStageBCascadeResult, "final_consumed_memory_budget_by_bank", 0);
        cases++;

        var bundleBudgetSeed = Inputs(firstLegal: true, initialOccupancy: 0);
        bundleBudgetSeed["vt0_is_memory"] = 1; bundleBudgetSeed["vt0_is_loadstore"] = 1; bundleBudgetSeed["vt0_is_load"] = 1; bundleBudgetSeed["vt0_bank_id"] = 6;
        bundleBudgetSeed["bundle_slot0_present"] = 1; bundleBudgetSeed["bundle_slot0_is_loadstore"] = 1; bundleBudgetSeed["bundle_slot0_is_load"] = 1; bundleBudgetSeed["bundle_slot0_bank_id"] = 6;
        bundleBudgetSeed["bundle_slot1_present"] = 1; bundleBudgetSeed["bundle_slot1_is_loadstore"] = 1; bundleBudgetSeed["bundle_slot1_is_store"] = 1; bundleBudgetSeed["bundle_slot1_bank_id"] = 6;
        bundleBudgetSeed["bundle_slot2_present"] = 1; bundleBudgetSeed["bundle_slot2_is_loadstore"] = 1; bundleBudgetSeed["bundle_slot2_is_load"] = 1; bundleBudgetSeed["bundle_slot2_bank_id"] = 6;
        bundleBudgetSeed["bundle_slot3_present"] = 1; bundleBudgetSeed["bundle_slot3_is_loadstore"] = 1; bundleBudgetSeed["bundle_slot3_is_load"] = 1; bundleBudgetSeed["bundle_slot3_bank_id"] = 6;
        var bundleBudgetSeedResult = EvaluateModule(top, bundleBudgetSeed);
        Require(bundleBudgetSeedResult, "c0_accepted", 0); Require(bundleBudgetSeedResult, "c0_reject_reason", 7);
        Require(bundleBudgetSeedResult, "final_remaining_memory_issue_budget", 0); Require(bundleBudgetSeedResult, "final_remaining_read_issue_budget", 0); Require(bundleBudgetSeedResult, "final_remaining_write_issue_budget", 1);
        Require(bundleBudgetSeedResult, "final_consumed_memory_budget_by_bank", 3 << 12); Require(bundleBudgetSeedResult, "final_consumed_read_budget_by_bank", 3 << 12); Require(bundleBudgetSeedResult, "final_consumed_write_budget_by_bank", 1 << 12);
        cases++;

        var legalityFailureInputs = Inputs(firstLegal: false, initialOccupancy: 64);
        legalityFailureInputs["bundle_slot6_class"] = 2;
        var legalityFailure = EvaluateModule(top, legalityFailureInputs);
        Require(legalityFailure, "c0_accepted", 0); Require(legalityFailure, "c0_reject_reason", 3);
        Require(legalityFailure, "c3_accepted", 0); Require(legalityFailure, "c3_stage_b_evaluated", 0);
        Require(legalityFailure, "c3_reject_reason", 1); Require(legalityFailure, "final_budget2", 0);
        Require(legalityFailure, "final_next_occupancy", 64); Require(legalityFailure, "final_next_occupied2", 1);
        Require(legalityFailure, "final_injections", 0);
        cases++;

        var laneConflictInputs = Inputs(firstLegal: true, initialOccupancy: 64);
        var laneConflict = EvaluateModule(top, laneConflictInputs);
        Require(laneConflict, "c0_accepted", 0); Require(laneConflict, "c0_reject_reason", 11);
        Require(laneConflict, "c0_stage_b_evaluated", 1); Require(laneConflict, "c3_accepted", 0);
        Require(laneConflict, "c3_stage_b_evaluated", 1); Require(laneConflict, "c3_reject_reason", 11);
        Require(laneConflict, "final_budget2", 0); Require(laneConflict, "final_next_occupancy", 64);
        Require(laneConflict, "final_next_occupied2", 0); Require(laneConflict, "final_injections", 0);
        cases++;

        var boundaryInputs = Inputs(firstLegal: true, initialOccupancy: 0); boundaryInputs["boundary_blocked"] = 1;
        var boundary = EvaluateModule(top, boundaryInputs);
        Require(boundary, "c0_accepted", 0); cases++;

        var ownerInputs = Inputs(firstLegal: true, initialOccupancy: 0); ownerInputs["vt0_owner_context_id"] = 8;
        var owner = EvaluateModule(top, ownerInputs);
        Require(owner, "c0_accepted", 0); cases++;

        var domainInputs = Inputs(firstLegal: true, initialOccupancy: 0); domainInputs["vt0_domain_tag"] = 2; domainInputs["owner_domain_certificate"] = 1;
        var domain = EvaluateModule(top, domainInputs);
        Require(domain, "c0_accepted", 0); cases++;

        var rarInputs = Inputs(firstLegal: true, initialOccupancy: 0); rarInputs["vt0_candidate_register_hazard_mask"] = 1; rarInputs["bundle_vt0_mask"] = 1;
        var rar = EvaluateModule(top, rarInputs); Require(rar, "c0_accepted", 1); cases++;
        foreach (var (candidateMask, bundleMask) in new[] { (1, 65536), (65536, 1), (65536, 65536) })
        {
            var hazardInputs = Inputs(firstLegal: true, initialOccupancy: 0); hazardInputs["vt0_candidate_register_hazard_mask"] = candidateMask; hazardInputs["bundle_vt0_mask"] = bundleMask;
            var hazard = EvaluateModule(top, hazardInputs); Require(hazard, "c0_accepted", 0); Require(hazard, "c0_certificate_reject_detail", 2); cases++;
        }
        var sharedCommitInputs = Inputs(firstLegal: true, initialOccupancy: 0); sharedCommitInputs["vt0_candidate_shared_low"] = 1; sharedCommitInputs["vt1_candidate_shared_low"] = 1;
        var sharedCommit = EvaluateModule(top, sharedCommitInputs); Require(sharedCommit, "c0_accepted", 1); Require(sharedCommit, "c1_accepted", 0); Require(sharedCommit, "c1_certificate_reject_detail", 1); cases++;
        var replayMissInputs = Inputs(firstLegal: true, initialOccupancy: 0); replayMissInputs["vt0_candidate_shared_low"] = 1; replayMissInputs["bundle_shared_low"] = 1; replayMissInputs["attempt0_phase_key_valid"] = 1; replayMissInputs["attempt0_phase_template_matches"] = 0;
        var replayMiss = EvaluateModule(top, replayMissInputs); Require(replayMiss, "c0_accepted", 0); cases++;
        var replayHitInputs = Inputs(firstLegal: true, initialOccupancy: 0); replayHitInputs["vt0_candidate_shared_low"] = 1; replayHitInputs["attempt0_phase_key_valid"] = 1; replayHitInputs["attempt0_phase_template_matches"] = 1; replayHitInputs["attempt0_phase_shared_low"] = 1;
        var replayHit = EvaluateModule(top, replayHitInputs); Require(replayHit, "c0_accepted", 0); cases++;

        // A3 bit 5 uses explicit sampled state ports in the composed projection.
        // The rank/collection path only nominates non-owner VTs, so same-owner
        // bypass is checked in the independent predicate truth table below.
        foreach (var (enabled, memory, ownerVt, candidateVt, budget, expectedReject) in new[]
        {
            (true,  true,  3, 0,  0, true),
            (true,  true,  3, 0,  1, false),
            (true,  true,  0, 0,  0, false),
            (true,  true, -1, 0,  0, false),
            (false, true,  3, 0,  0, false),
            (true, false,  3, 0,  0, false)
        })
        {
            bool sourcePredicate = enabled && memory && ownerVt >= 0 && candidateVt != ownerVt && budget <= 0;
            if (sourcePredicate != expectedReject)
                throw new InvalidOperationException("Independent A3 speculation predicate oracle mismatch.");
            var speculationInputs = Inputs(firstLegal: true, initialOccupancy: 0);
            speculationInputs["owner_vt"] = ownerVt < 0 ? uint.MaxValue : ownerVt;
            speculationInputs[$"vt{candidateVt}_is_memory"] = memory ? 1 : 0;
            speculationInputs["speculation_budget_enabled"] = enabled ? 1 : 0;
            speculationInputs["speculation_budget"] = unchecked((uint)budget);
            var speculationResult = EvaluateModule(top, speculationInputs);
            Require(speculationResult, "c0_accepted", expectedReject ? 0 : 1);
            if (expectedReject) Require(speculationResult, "c0_reject_reason", 8);
            cases++;
        }
        var directGate = outerGate.Design.Modules.Single(m => m.Id == outerGate.Design.TopModule);
        var directGateInputs = directGate.Ports.Where(p => p.Direction == HwPortDirection.Input)
            .ToDictionary(p => p.Name, _ => BigInteger.Zero, StringComparer.Ordinal);
        directGateInputs["candidate_virtual_thread_id"] = 2;
        directGateInputs["owner_vt"] = 2;
        directGateInputs["is_memory"] = 1;
        directGateInputs["speculation_budget_enabled"] = 1;
        directGateInputs["speculation_budget"] = 0;
        directGateInputs["projected_capacity_vt2"] = 1;
        var sameOwnerGate = EvaluateModule(directGate, directGateInputs);
        Require(sameOwnerGate, "allowed", 1); Require(sameOwnerGate, "reject_reason", 0); cases++;
        directGateInputs["owner_vt"] = 3;
        var crossOwnerGate = EvaluateModule(directGate, directGateInputs);
        Require(crossOwnerGate, "allowed", 0); Require(crossOwnerGate, "reject_reason", 8); cases++;
        var speculationCounterInputs = Inputs(firstLegal: true, initialOccupancy: 0);
        speculationCounterInputs["speculation_budget_enabled"] = 1; speculationCounterInputs["speculation_budget"] = 0;
        speculationCounterInputs["speculation_budget_exhaustion_events"] = 40;
        speculationCounterInputs["budget1"] = 2; speculationCounterInputs["previous1"] = uint.MaxValue;
        for (int vt = 0; vt < 3; vt++)
        {
            speculationCounterInputs[$"vt{vt}_is_memory"] = 1; speculationCounterInputs[$"vt{vt}_is_loadstore"] = 1; speculationCounterInputs[$"vt{vt}_is_load"] = 1; speculationCounterInputs[$"vt{vt}_bank_id"] = 1;
            speculationCounterInputs[$"vt{vt}_slot_class"] = 1; speculationCounterInputs[$"vt{vt}_pinned_lane"] = 4; speculationCounterInputs[$"vt{vt}_permitted_lanes"] = 48;
        }
        var speculationCounter = EvaluateModule(top, speculationCounterInputs);
        Require(speculationCounter, "c0_reject_reason", 8); Require(speculationCounter, "c1_reject_reason", 8);
        Require(speculationCounter, "final_speculation_budget_exhaustion_events", 44);
        Require(speculationCounter, "final_smt_rejections_count", RejectedAttemptCount(speculationCounter)); cases++;
        var directFourModule = fourCandidateAdmission.Design.Modules.Single(m => m.Id == fourCandidateAdmission.Design.TopModule);
        var directFourInputs = directFourModule.Ports.Where(p => p.Direction == HwPortDirection.Input).ToDictionary(p => p.Name, _ => BigInteger.Zero, StringComparer.Ordinal);
        directFourInputs["template_hit"] = 1; directFourInputs["owner_vt"] = 3; directFourInputs["owner_context_known"] = 1; directFourInputs["owner_context"] = 7;
        directFourInputs["speculation_budget_enabled"] = 1; directFourInputs["speculation_budget"] = 0; directFourInputs["speculation_budget_exhaustion_events"] = 40;
        directFourInputs["smt_rejections_count"] = 40;
        directFourInputs["budget1"] = 2; directFourInputs["previous1"] = uint.MaxValue;
        directFourInputs["remaining_memory_issue_budget"] = 2; directFourInputs["remaining_read_issue_budget"] = 2;
        directFourInputs["memory_bank_budget_at_least_one_mask"] = ushort.MaxValue; directFourInputs["memory_bank_budget_at_least_two_mask"] = ushort.MaxValue;
        directFourInputs["read_bank_budget_at_least_one_mask"] = ushort.MaxValue; directFourInputs["read_bank_budget_at_least_two_mask"] = ushort.MaxValue;
        for (int vt = 0; vt < 4; vt++) { directFourInputs[$"projected_count_vt{vt}"] = 0; directFourInputs[$"projected_capacity_vt{vt}"] = 8; }
        for (int i = 0; i < 2; i++)
        {
            directFourInputs[$"c{i}_present"] = 1; directFourInputs[$"c{i}_vt"] = i; directFourInputs[$"c{i}_slot_class"] = 1;
            directFourInputs[$"c{i}_owner_context_id"] = 7; directFourInputs[$"c{i}_domain_tag"] = 0; directFourInputs[$"c{i}_is_memory"] = 1; directFourInputs[$"c{i}_is_loadstore"] = 1; directFourInputs[$"c{i}_is_load"] = 1; directFourInputs[$"c{i}_bank_id"] = 1;
            directFourInputs[$"c{i}_pinning_kind"] = 1; directFourInputs[$"c{i}_pinned_lane"] = 4; directFourInputs[$"c{i}_permitted_lanes"] = 48;
        }
        var directFourCounter = EvaluateModule(directFourModule, directFourInputs);
        Require(directFourCounter, "c0_reject_reason", 8); Require(directFourCounter, "c1_reject_reason", 8);
        Require(directFourCounter, "final_speculation_budget_exhaustion_events", 42); Require(directFourCounter, "final_smt_rejections_count", 40 + RejectedAttemptCount(directFourCounter)); cases++;
        var earlierGateCounterInputs = Inputs(firstLegal: true, initialOccupancy: 0);
        earlierGateCounterInputs["speculation_budget_enabled"] = 1; earlierGateCounterInputs["speculation_budget"] = 0;
        earlierGateCounterInputs["speculation_budget_exhaustion_events"] = 100; earlierGateCounterInputs["smt_rejections_count"] = 100; earlierGateCounterInputs["suppress_lsu"] = 1; earlierGateCounterInputs["vt0_is_memory"] = 1;
        var earlierGateCounter = EvaluateModule(top, earlierGateCounterInputs);
        Require(earlierGateCounter, "c0_reject_reason", 5); Require(earlierGateCounter, "final_speculation_budget_exhaustion_events", 100); Require(earlierGateCounter, "final_smt_rejections_count", 100 + RejectedAttemptCount(earlierGateCounter)); cases++;
        var assistCounterInputs = Inputs(firstLegal: true, initialOccupancy: 0);
        assistCounterInputs["speculation_budget_enabled"] = 1; assistCounterInputs["speculation_budget"] = 0;
        assistCounterInputs["speculation_budget_exhaustion_events"] = 7; assistCounterInputs["smt_rejections_count"] = 7; assistCounterInputs["vt0_is_memory"] = 1; assistCounterInputs["vt0_is_assist"] = 1;
        var assistCounter = EvaluateModule(top, assistCounterInputs);
        Require(assistCounter, "final_speculation_budget_exhaustion_events", 7); Require(assistCounter, "final_smt_rejections_count", 7 + RejectedAttemptCount(assistCounter)); cases++;
        var certificateCounterInputs = Inputs(firstLegal: true, initialOccupancy: 0);
        certificateCounterInputs["smt_rejections_count"] = 50; certificateCounterInputs["vt0_candidate_shared_low"] = 1; certificateCounterInputs["bundle_shared_low"] = 1;
        var certificateCounter = EvaluateModule(top, certificateCounterInputs);
        Require(certificateCounter, "c0_reject_reason", 3); Require(certificateCounter, "final_smt_rejections_count", 50 + RejectedAttemptCount(certificateCounter)); cases++;
        var stageBCounterInputs = Inputs(firstLegal: true, initialOccupancy: 64);
        stageBCounterInputs["smt_rejections_count"] = 60;
        var stageBCounter = EvaluateModule(top, stageBCounterInputs);
        Require(stageBCounter, "c0_reject_reason", 11); Require(stageBCounter, "final_smt_rejections_count", 60 + RejectedAttemptCount(stageBCounter)); cases++;
        var bankPendingInputs = Inputs(firstLegal: true, initialOccupancy: 0); bankPendingInputs["vt0_is_memory"] = 1; bankPendingInputs["vt0_is_loadstore"] = 1; bankPendingInputs["vt0_bank_id"] = 6;
        bankPendingInputs["score_vt0_slot0_value"] = 0; bankPendingInputs["score_vt0_slot0_type"] = 2; bankPendingInputs["score_vt0_slot0_bank_id"] = 6;
        var bankPending = EvaluateModule(top, bankPendingInputs); Require(bankPending, "c0_accepted", 0); Require(bankPending, "c0_reject_reason", 6); cases++;
        var otherVtBankInputs = Inputs(firstLegal: true, initialOccupancy: 0); otherVtBankInputs["vt0_is_memory"] = 1; otherVtBankInputs["vt0_is_loadstore"] = 1; otherVtBankInputs["vt0_bank_id"] = 6;
        otherVtBankInputs["score_vt1_slot0_value"] = 0; otherVtBankInputs["score_vt1_slot0_type"] = 2; otherVtBankInputs["score_vt1_slot0_bank_id"] = 6;
        var otherVtBank = EvaluateModule(top, otherVtBankInputs); Require(otherVtBank, "c0_accepted", 1); cases++;
        var otherBankInputs = Inputs(firstLegal: true, initialOccupancy: 0); otherBankInputs["vt0_is_memory"] = 1; otherBankInputs["vt0_is_loadstore"] = 1; otherBankInputs["vt0_bank_id"] = 6;
        otherBankInputs["score_vt0_slot0_value"] = 0; otherBankInputs["score_vt0_slot0_type"] = 2; otherBankInputs["score_vt0_slot0_bank_id"] = 5;
        var otherBank = EvaluateModule(top, otherBankInputs); Require(otherBank, "c0_accepted", 1); cases++;
        var invalidBankInputs = Inputs(firstLegal: true, initialOccupancy: 0); invalidBankInputs["vt0_is_memory"] = 1; invalidBankInputs["vt0_is_loadstore"] = 1; invalidBankInputs["vt0_bank_id"] = uint.MaxValue;
        invalidBankInputs["score_vt0_slot0_value"] = 0; invalidBankInputs["score_vt0_slot0_type"] = 2; invalidBankInputs["score_vt0_slot0_bank_id"] = uint.MaxValue;
        var invalidBank = EvaluateModule(top, invalidBankInputs); Require(invalidBank, "c0_accepted", 1); cases++;
        var bundleStoreInputs = Inputs(firstLegal: true, initialOccupancy: 0); bundleStoreInputs["vt0_is_memory"] = 1; bundleStoreInputs["vt0_is_loadstore"] = 1; bundleStoreInputs["vt0_is_store"] = 1; bundleStoreInputs["vt0_bank_id"] = 6;
        bundleStoreInputs["bundle_slot0_present"] = 1; bundleStoreInputs["bundle_slot0_is_loadstore"] = 1; bundleStoreInputs["bundle_slot0_is_store"] = 1; bundleStoreInputs["bundle_slot0_bank_id"] = 6;
        var bundleStore = EvaluateModule(top, bundleStoreInputs); Require(bundleStore, "c0_accepted", 0); Require(bundleStore, "c0_reject_reason", 6); cases++;
        var bundleDifferentBankInputs = Inputs(firstLegal: true, initialOccupancy: 0); bundleDifferentBankInputs["vt0_is_memory"] = 1; bundleDifferentBankInputs["vt0_is_loadstore"] = 1; bundleDifferentBankInputs["vt0_is_store"] = 1; bundleDifferentBankInputs["vt0_bank_id"] = 6;
        bundleDifferentBankInputs["bundle_slot0_present"] = 1; bundleDifferentBankInputs["bundle_slot0_is_loadstore"] = 1; bundleDifferentBankInputs["bundle_slot0_is_store"] = 1; bundleDifferentBankInputs["bundle_slot0_bank_id"] = 5;
        var bundleDifferentBank = EvaluateModule(top, bundleDifferentBankInputs); Require(bundleDifferentBank, "c0_accepted", 1); cases++;
        var bundleLoadInputs = Inputs(firstLegal: true, initialOccupancy: 0); bundleLoadInputs["vt0_is_memory"] = 1; bundleLoadInputs["vt0_is_loadstore"] = 1; bundleLoadInputs["vt0_is_store"] = 1; bundleLoadInputs["vt0_bank_id"] = 6;
        bundleLoadInputs["bundle_slot0_present"] = 1; bundleLoadInputs["bundle_slot0_is_loadstore"] = 1; bundleLoadInputs["bundle_slot0_bank_id"] = 6;
        var bundleLoad = EvaluateModule(top, bundleLoadInputs); Require(bundleLoad, "c0_accepted", 1); cases++;
        var candidateLoadInputs = Inputs(firstLegal: true, initialOccupancy: 0); candidateLoadInputs["vt0_is_memory"] = 1; candidateLoadInputs["vt0_is_loadstore"] = 1; candidateLoadInputs["vt0_bank_id"] = 6;
        candidateLoadInputs["bundle_slot0_present"] = 1; candidateLoadInputs["bundle_slot0_is_loadstore"] = 1; candidateLoadInputs["bundle_slot0_is_store"] = 1; candidateLoadInputs["bundle_slot0_bank_id"] = 6;
        var candidateLoad = EvaluateModule(top, candidateLoadInputs); Require(candidateLoad, "c0_accepted", 1); cases++;
        var bundleOutOfRangeInputs = Inputs(firstLegal: true, initialOccupancy: 0); bundleOutOfRangeInputs["vt0_is_memory"] = 1; bundleOutOfRangeInputs["vt0_is_loadstore"] = 1; bundleOutOfRangeInputs["vt0_is_store"] = 1; bundleOutOfRangeInputs["vt0_bank_id"] = 16;
        bundleOutOfRangeInputs["bundle_slot0_present"] = 1; bundleOutOfRangeInputs["bundle_slot0_is_loadstore"] = 1; bundleOutOfRangeInputs["bundle_slot0_is_store"] = 1; bundleOutOfRangeInputs["bundle_slot0_bank_id"] = 16;
        var bundleOutOfRange = EvaluateModule(top, bundleOutOfRangeInputs); Require(bundleOutOfRange, "c0_accepted", 1); cases++;
        var ignoredBundleFactInputs = Inputs(firstLegal: true, initialOccupancy: 0); ignoredBundleFactInputs["vt0_outer_gate_condition_facts"] = 1 << 3;
        var ignoredBundleFact = EvaluateModule(top, ignoredBundleFactInputs); Require(ignoredBundleFact, "c0_accepted", 1); cases++;
        var capacityFullInputs = Inputs(firstLegal: true, initialOccupancy: 0); capacityFullInputs["vt0_is_memory"] = 1;
        for (int slot = 0; slot < 8; slot++) { capacityFullInputs[$"score_vt0_slot{slot}_value"] = 0; capacityFullInputs[$"score_vt0_slot{slot}_type"] = 2; }
        var capacityFull = EvaluateModule(top, capacityFullInputs); Require(capacityFull, "c0_accepted", 0); Require(capacityFull, "c0_reject_reason", 5); cases++;
        var capacityRoomInputs = Inputs(firstLegal: true, initialOccupancy: 0); capacityRoomInputs["vt0_is_memory"] = 1; capacityRoomInputs["vt0_is_loadstore"] = 1;
        for (int slot = 0; slot < 7; slot++) { capacityRoomInputs[$"score_vt0_slot{slot}_value"] = 0; capacityRoomInputs[$"score_vt0_slot{slot}_type"] = 2; }
        capacityRoomInputs["score_vt0_slot7_value"] = uint.MaxValue;
        capacityRoomInputs["vt0_outer_gate_condition_facts"] = 1 << 1;
        var capacityRoom = EvaluateModule(top, capacityRoomInputs); Require(capacityRoom, "c0_accepted", 1); Require(capacityRoom, "final_projected_count_vt0", 8); cases++;
        var seededBundleInputs = Inputs(firstLegal: true, initialOccupancy: 0); seededBundleInputs["vt0_is_memory"] = 1; seededBundleInputs["vt0_is_loadstore"] = 1;
        seededBundleInputs["bundle_slot0_present"] = 1; seededBundleInputs["bundle_slot0_is_loadstore"] = 1; seededBundleInputs["bundle_slot0_vt"] = 0;
        var seededBundle = EvaluateModule(top, seededBundleInputs); Require(seededBundle, "c0_accepted", 1); Require(seededBundle, "final_projected_count_vt0", 2); cases++;
        var nonMemoryCapacityInputs = Inputs(firstLegal: true, initialOccupancy: 0); for (int slot = 0; slot < 8; slot++) { nonMemoryCapacityInputs[$"score_vt0_slot{slot}_value"] = 0; nonMemoryCapacityInputs[$"score_vt0_slot{slot}_type"] = 2; }
        var nonMemoryCapacity = EvaluateModule(top, nonMemoryCapacityInputs); Require(nonMemoryCapacity, "c0_accepted", 1); cases++;
        var nonLoadStoreMemoryInputs = Inputs(firstLegal: true, initialOccupancy: 0); nonLoadStoreMemoryInputs["vt0_is_memory"] = 1;
        for (int slot = 0; slot < 7; slot++) { nonLoadStoreMemoryInputs[$"score_vt0_slot{slot}_value"] = 0; nonLoadStoreMemoryInputs[$"score_vt0_slot{slot}_type"] = 2; }
        nonLoadStoreMemoryInputs["score_vt0_slot7_value"] = uint.MaxValue;
        var nonLoadStoreMemory = EvaluateModule(top, nonLoadStoreMemoryInputs); Require(nonLoadStoreMemory, "c0_accepted", 1); Require(nonLoadStoreMemory, "final_projected_count_vt0", 7); cases++;
        var outerPriorityInputs = Inputs(firstLegal: true, initialOccupancy: 0); outerPriorityInputs["suppress_lsu"] = 1; outerPriorityInputs["vt0_is_memory"] = 1; outerPriorityInputs["vt0_is_loadstore"] = 1; outerPriorityInputs["snapshot_memory_issue_budget"] = 0; outerPriorityInputs["vt0_outer_gate_condition_facts"] = 0;
        var outerPriority = EvaluateModule(top, outerPriorityInputs); Require(outerPriority, "c0_accepted", 0); Require(outerPriority, "c0_reject_reason", 5); cases++;
        var assistBypassInputs = Inputs(firstLegal: true, initialOccupancy: 0); assistBypassInputs["vt0_is_assist"] = 1; assistBypassInputs["vt0_outer_gate_condition_facts"] = 1;
        var assistBypass = EvaluateModule(top, assistBypassInputs); Require(assistBypass, "c0_accepted", 1); cases++;
        var lsuSuppressInputs = Inputs(firstLegal: true, initialOccupancy: 0); lsuSuppressInputs["suppress_lsu"] = 1; lsuSuppressInputs["vt0_is_memory"] = 1;
        var lsuSuppress = EvaluateModule(top, lsuSuppressInputs); Require(lsuSuppress, "c0_accepted", 0); Require(lsuSuppress, "c0_reject_reason", 5); cases++;
        var lsuSuppressNonMemoryInputs = Inputs(firstLegal: true, initialOccupancy: 0); lsuSuppressNonMemoryInputs["suppress_lsu"] = 1;
        var lsuSuppressNonMemory = EvaluateModule(top, lsuSuppressNonMemoryInputs); Require(lsuSuppressNonMemory, "c0_accepted", 1); cases++;
        var lsuEnabledInputs = Inputs(firstLegal: true, initialOccupancy: 0); lsuEnabledInputs["vt0_is_memory"] = 1;
        var lsuEnabled = EvaluateModule(top, lsuEnabledInputs); Require(lsuEnabled, "c0_accepted", 1); cases++;
        var ignoredLsuFactInputs = Inputs(firstLegal: true, initialOccupancy: 0); ignoredLsuFactInputs["vt0_is_memory"] = 1; ignoredLsuFactInputs["vt0_outer_gate_condition_facts"] = 1;
        var ignoredLsuFact = EvaluateModule(top, ignoredLsuFactInputs); Require(ignoredLsuFact, "c0_accepted", 1); cases++;

        var sharedBudgetInputs = Inputs(firstLegal: true, initialOccupancy: 0); sharedBudgetInputs["vt0_is_memory"] = 1; sharedBudgetInputs["vt0_is_loadstore"] = 1; sharedBudgetInputs["snapshot_memory_issue_budget"] = 0;
        var sharedBudget = EvaluateModule(top, sharedBudgetInputs); Require(sharedBudget, "c0_accepted", 0); Require(sharedBudget, "c0_reject_reason", 7); cases++;
        var readBudgetInputs = Inputs(firstLegal: true, initialOccupancy: 0); readBudgetInputs["vt0_is_memory"] = 1; readBudgetInputs["vt0_is_loadstore"] = 1; readBudgetInputs["vt0_is_load"] = 1; readBudgetInputs["snapshot_read_issue_budget"] = 0;
        var readBudget = EvaluateModule(top, readBudgetInputs); Require(readBudget, "c0_accepted", 0); Require(readBudget, "c0_reject_reason", 7); cases++;
        var storeBudgetInputs = Inputs(firstLegal: true, initialOccupancy: 0); storeBudgetInputs["vt0_is_memory"] = 1; storeBudgetInputs["vt0_is_loadstore"] = 1; storeBudgetInputs["vt0_is_store"] = 1; storeBudgetInputs["snapshot_read_issue_budget"] = 0;
        var storeBudget = EvaluateModule(top, storeBudgetInputs); Require(storeBudget, "c0_accepted", 1); cases++;
        var atomicBudgetInputs = Inputs(firstLegal: true, initialOccupancy: 0); atomicBudgetInputs["vt0_is_memory"] = 1; atomicBudgetInputs["vt0_is_loadstore"] = 1; atomicBudgetInputs["snapshot_read_issue_budget"] = 0; atomicBudgetInputs["snapshot_write_issue_budget"] = 0;
        var atomicBudget = EvaluateModule(top, atomicBudgetInputs); Require(atomicBudget, "c0_accepted", 1); cases++;
        var bankBudgetInputs = Inputs(firstLegal: true, initialOccupancy: 0); bankBudgetInputs["vt0_is_memory"] = 1; bankBudgetInputs["vt0_is_loadstore"] = 1; bankBudgetInputs["vt0_is_load"] = 1; bankBudgetInputs["vt0_bank_id"] = 6; bankBudgetInputs["memory_bank_budget_at_least_two_mask"] = ushort.MaxValue & ~(1 << 6); bankBudgetInputs["bundle_slot0_present"] = 1; bankBudgetInputs["bundle_slot0_is_loadstore"] = 1; bankBudgetInputs["bundle_slot0_is_load"] = 1; bankBudgetInputs["bundle_slot0_bank_id"] = 6;
        var bankBudget = EvaluateModule(top, bankBudgetInputs); Require(bankBudget, "c0_accepted", 0); Require(bankBudget, "c0_reject_reason", 7); cases++;
        var invalidBankBudgetInputs = Inputs(firstLegal: true, initialOccupancy: 0); invalidBankBudgetInputs["vt0_is_memory"] = 1; invalidBankBudgetInputs["vt0_is_loadstore"] = 1; invalidBankBudgetInputs["vt0_is_load"] = 1; invalidBankBudgetInputs["vt0_bank_id"] = uint.MaxValue; invalidBankBudgetInputs["memory_bank_budget_at_least_one_mask"] = 0; invalidBankBudgetInputs["memory_bank_budget_at_least_two_mask"] = 0; invalidBankBudgetInputs["read_bank_budget_at_least_one_mask"] = 0; invalidBankBudgetInputs["read_bank_budget_at_least_two_mask"] = 0;
        var invalidBankBudget = EvaluateModule(top, invalidBankBudgetInputs); Require(invalidBankBudget, "c0_accepted", 1); cases++;

        static void Require(IReadOnlyDictionary<string, BigInteger> actual, string port, BigInteger expected)
        {
            if (actual[port] != expected) throw new InvalidOperationException($"Duplicate retry directed oracle: {port}={actual[port]}, expected {expected}.");
        }
    }

    Dictionary<string, BigInteger> EvaluateModule(HwModule module, IReadOnlyDictionary<string, BigInteger> portInputs)
    {
        var values = new Dictionary<string, BigInteger>(portInputs, StringComparer.Ordinal);
        var nodeMap = module.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var outputDrivers = module.Nodes.OfType<HwOutputDriveNode>().ToDictionary(n => n.OutputPort, n => n.Value, StringComparer.Ordinal);
        var wireDrivers = new Dictionary<string, (HwInstanceNode Instance, string Port)>(StringComparer.Ordinal);
        foreach (var instance in module.Instances)
        {
            HwModule child = modules[instance.ModuleReference];
            foreach (var childPort in child.Ports.Where(p => p.Direction == HwPortDirection.Output))
                if (instance.PortConnections.TryGetValue(childPort.Name, out string? parentSignal))
                    wireDrivers.Add(parentSignal, (instance, childPort.Name));
        }
        var childOutputs = new Dictionary<string, Dictionary<string, BigInteger>>(StringComparer.Ordinal);
        BigInteger Signal(string name)
        {
            if (values.TryGetValue(name, out BigInteger known)) return known;
            if (!nodeMap.TryGetValue(name, out HwNode? node))
            {
                if (!wireDrivers.TryGetValue(name, out var driver)) throw new InvalidDataException($"No combinational driver for {module.Id}.{name}.");
                if (!childOutputs.TryGetValue(driver.Instance.Id, out var outputs))
                {
                    HwModule child = modules[driver.Instance.ModuleReference];
                    var childInputs = child.Ports.Where(p => p.Direction == HwPortDirection.Input).ToDictionary(
                        p => p.Name, p => Signal(driver.Instance.PortConnections[p.Name]), StringComparer.Ordinal);
                    outputs = EvaluateModule(child, childInputs);
                    childOutputs.Add(driver.Instance.Id, outputs);
                }
                return values[name] = outputs[driver.Port];
            }
            BigInteger Mask(int width) => (BigInteger.One << width) - 1;
            BigInteger[] Ops(HwOperationNode op) => op.Operands.Select(Signal).ToArray();
            BigInteger InstanceSignal(string wireName)
            {
                if (!wireDrivers.TryGetValue(wireName, out var driver)) throw new InvalidDataException($"Undriven wire {module.Id}.{wireName}.");
                if (!childOutputs.TryGetValue(driver.Instance.Id, out var outputs))
                {
                    HwModule child = modules[driver.Instance.ModuleReference];
                    var childInputs = child.Ports.Where(p => p.Direction == HwPortDirection.Input).ToDictionary(
                        p => p.Name, p => Signal(driver.Instance.PortConnections[p.Name]), StringComparer.Ordinal);
                    outputs = EvaluateModule(child, childInputs);
                    childOutputs.Add(driver.Instance.Id, outputs);
                }
                return values[wireName] = outputs[driver.Port];
            }
            BigInteger result = node switch
            {
                HwConstantNode constant => Convert.ToInt64(constant.BitPattern, 2),
                HwMuxNode mux => Signal(mux.Condition) != 0 ? Signal(mux.WhenTrue) : Signal(mux.WhenFalse),
                HwOperationNode op => Apply(op, Ops(op)),
                HwWireNode => InstanceSignal(name),
                _ => throw new InvalidDataException($"Unsupported combinational node {node.GetType().Name} in {module.Id}.")
            };
            return values[name] = result & Mask(node switch { HwConstantNode c => c.Type.Width, HwMuxNode m => m.ResultType.Width, HwOperationNode o => o.ResultType.Width, _ => 64 });
        }
        BigInteger Apply(HwOperationNode op, BigInteger[] a)
        {
            int shift = a.Length > 1 ? (int)(a[1] & (op.ResultType.Width - 1)) : 0;
            return op.Opcode switch
            {
                "add" => a[0] + a[1], "sub" => a[0] - a[1], "mul" => a[0] * a[1],
                "and" => a[0] & a[1], "or" => a[0] | a[1], "xor" => a[0] ^ a[1], "not" => ~a[0],
                "eq" => a[0] == a[1] ? 1 : 0, "ult64" => a[0] < a[1] ? 1 : 0, "slt64" => a[0] < a[1] ? 1 : 0,
                "mux" => a[0] != 0 ? a[1] : a[2], "trunc" or "zext" or "sext" or "bitcast" => a[0],
                "shl32_mask5" or "shl64_mask6" => a[0] << shift,
                "lshr32_mask5" or "lshr64_mask6" => a[0] >> shift,
                "ashr32_mask5" or "ashr64_mask6" => a[0] >> shift,
                _ => throw new InvalidDataException($"Unsupported opcode {op.Opcode}.")
            };
        }
        return module.Ports.Where(p => p.Direction == HwPortDirection.Output).ToDictionary(
            p => p.Name, p => Signal(outputDrivers.TryGetValue(p.Name, out string? signal) ? signal : p.Name), StringComparer.Ordinal);
    }
}

static string BuildDirectedRtlTestbench(HwValidatedDesign design)
{
    HwModule top = design.Design.Modules.Single(m => m.Id == design.Design.TopModule);
    HwModule outerGate = design.Design.Modules.Single(m => m.Id == "smt_outer_gate_projection");
    var inputs = top.Ports.Where(p => p.Direction == HwPortDirection.Input).ToArray();
    var outputs = top.Ports.Where(p => p.Direction == HwPortDirection.Output).ToArray();
    var lines = new List<string> { "`timescale 1ns/1ps", "module smt_rank_tail_directed_tb;", "integer expected_rejections;" };
    foreach (var p in inputs) lines.Add(p.Type.Width == 1 ? $"logic {p.Name};" : $"logic [{p.Type.Width - 1}:0] {p.Name};");
    foreach (var p in outputs) lines.Add(p.Type.Width == 1 ? $"wire {p.Name};" : $"wire [{p.Type.Width - 1}:0] {p.Name};");
    lines.Add($"{top.Name} dut (.*);");
    foreach (var p in outerGate.Ports)
    {
        string name = "og_" + p.Name;
        if (p.Direction == HwPortDirection.Input) lines.Add(p.Type.Width == 1 ? $"logic {name};" : $"logic [{p.Type.Width - 1}:0] {name};");
        else lines.Add(p.Type.Width == 1 ? $"wire {name};" : $"wire [{p.Type.Width - 1}:0] {name};");
    }
    lines.Add("smt_outer_gate_projection outer_gate_dut (" + string.Join(", ", outerGate.Ports.Select(p => $".{p.Name}(og_{p.Name})")) + ");");
    lines.Add("task automatic set_base; begin");
    foreach (var p in inputs) lines.Add($"  {p.Name} = '0;");
    void Set(string name, long value)
    {
        var p = inputs.SingleOrDefault(x => x.Name == name) ?? throw new InvalidDataException("RTL testbench input missing: " + name);
        lines.Add($"  {name} = {p.Type.Width}'d{value};");
    }
    Set("owner_vt", 3); Set("fairness_enabled", 1); Set("cycle", 1); Set("unwritten_tail", 0);
    Set("credit0", 16); Set("credit1", 8); Set("credit2", 4); Set("credit3", 0);
    Set("sort_enabled", 0); Set("template_hit", 1); Set("budget2", 1);
        Set("current_injections", 0);
    for (int slot = 0; slot < 8; slot++) Set($"bundle_slot{slot}_class", 7);
    Set("donor_mask", 0); Set("replay_active", 0); Set("stable_donor", 0);
    Set("boundary_blocked", 0); Set("owner_context_known", 1); Set("owner_context", 7);
    Set("owner_domain_certificate", 0); Set("kernel_domain_isolation", 0);
    for (int vt = 0; vt < 4; vt++)
    {
        Set($"vt{vt}_is_load", 0); Set($"vt{vt}_is_store", 0); Set($"vt{vt}_bank_id", 0);
    }
    Set("snapshot_memory_issue_budget", 2); Set("snapshot_read_issue_budget", 2); Set("snapshot_write_issue_budget", 2);
    Set("memory_bank_budget_at_least_one_mask", 65535); Set("memory_bank_budget_at_least_two_mask", 65535);
    Set("read_bank_budget_at_least_one_mask", 65535); Set("read_bank_budget_at_least_two_mask", 65535);
    Set("write_bank_budget_at_least_one_mask", 65535); Set("write_bank_budget_at_least_two_mask", 65535);
    for (int vt = 0; vt < 4; vt++) for (int slot = 0; slot < 8; slot++)
    {
        Set($"score_vt{vt}_slot{slot}_value", 4294967295L);
        Set($"score_vt{vt}_slot{slot}_type", 0);
        Set($"score_vt{vt}_slot{slot}_bank_id", 4294967295L);
    }
    for (int i = 0; i < 4; i++)
    {
        Set($"vt{i}_ready", i < 3 ? 1 : 0); Set($"vt{i}_present", i < 3 ? 1 : 0);
        Set($"vt{i}_is_loadstore", 0); Set($"vt{i}_bank_id", 0); Set($"vt{i}_slot_class", 2);
        Set($"vt{i}_pinning_kind", 1); Set($"vt{i}_pinned_lane", 6); Set($"vt{i}_permitted_lanes", 64);
        Set($"vt{i}_opportunistic", 0); Set($"vt{i}_is_assist", 0); Set($"vt{i}_is_memory", 0); Set($"vt{i}_is_loadstore", 0);
        Set($"vt{i}_owner_context_id", 7); Set($"vt{i}_domain_tag", 0);
        Set($"attempt{i}_phase_key_valid", 0); Set($"attempt{i}_phase_template_matches", 0);
        Set($"vt{i}_outer_gate_condition_facts", 0);
    }
    lines.Add("end endtask");
    void Expect(string name, int value)
    {
        var p = outputs.SingleOrDefault(x => x.Name == name) ?? throw new InvalidDataException("RTL testbench output missing: " + name);
        lines.Add($"  if ({name} !== {p.Type.Width}'d{value}) $fatal(1, \"{name} expected {value}, got %0d\", {name});");
    }
    void ExpectCounterTotal(int baseline)
    {
        lines.Add("  expected_rejections = 0;");
        for (int i = 0; i < 4; i++) lines.Add($"  if (c{i}_reject_reason != 0) expected_rejections = expected_rejections + 1;");
        lines.Add($"  if (final_smt_rejections_count !== 64'd{baseline} + expected_rejections) $fatal(1, \"aggregate rejection counter mismatch: %0d\", final_smt_rejections_count);");
    }
    lines.Add("initial begin");
    lines.Add("  $dumpfile(\"smt-rank-tail.vcd\"); $dumpvars(0, smt_rank_tail_directed_tb);");
    foreach (var p in outerGate.Ports.Where(p => p.Direction == HwPortDirection.Input)) lines.Add($"  og_{p.Name} = '0;");
    lines.Add("  og_candidate_virtual_thread_id = 32'd2; og_owner_vt = 32'd2; og_is_memory = 1'd1; og_speculation_budget_enabled = 1'd1; og_speculation_budget = 32'd0; og_projected_capacity_vt2 = 8'd1; #1;");
    lines.Add("  if (og_allowed !== 1'd1 || og_reject_reason !== 8'd0) $fatal(1, \"same-owner direct outer gate must bypass exhausted speculation budget\");");
    lines.Add("  $display(\"PASS standalone same-owner A3 bit5 bypass\");");
    lines.Add("  og_owner_vt = 32'd3; #1;");
    lines.Add("  if (og_allowed !== 1'd0 || og_reject_reason !== 8'd8) $fatal(1, \"cross-owner direct outer gate must reject exhausted speculation budget\");");
    lines.Add("  $display(\"PASS standalone cross-owner A3 bit5 rejection\");");
    lines.Add("  set_base(); #1;");
    foreach (var (name, value) in new[] { ("ranked_vt0",0), ("ranked_vt3",0), ("collected_count",4), ("c0_accepted",1), ("c0_selected_lane",6), ("c0_stage_b_evaluated",1), ("c3_accepted",0), ("c3_stage_b_evaluated",0), ("initial_occupancy",0), ("initial_empty_slot_mask",255), ("initial_reference_opportunity_mask",255), ("final_next_occupancy",64), ("final_empty_slot_mask",191), ("final_reference_opportunity_mask",191), ("final_next_occupied2",1), ("final_budget2",0), ("final_injections",1) }) Expect(name, value);
    lines.Add("  $display(\"PASS duplicate success suppresses retry; HardPinned lane6 committed once\");");
    lines.Add("  set_base(); vt0_ready = 0; vt1_ready = 0; vt2_ready = 0; vt3_ready = 0; vt0_present = 0; vt1_present = 0; vt2_present = 0; vt3_present = 0; bundle_slot0_present = 1'd1; bundle_slot2_present = 1'd1; bundle_is_nop_mask = 8'd4; bundle_stealable_mask = 8'd4; #1;");
    foreach (var (name, value) in new[] { ("initial_occupancy",5), ("initial_empty_slot_mask",250), ("initial_reference_opportunity_mask",254), ("initial_class_occupied0",0), ("initial_class_occupied5",0), ("final_next_occupancy",5), ("final_empty_slot_mask",250), ("final_reference_opportunity_mask",254) }) Expect(name, value);
    lines.Add("  $display(\"PASS null, ordinary and stealable NOP opportunities map to source masks\");");
    lines.Add("  set_base(); vt0_ready = 0; vt1_ready = 0; vt2_ready = 0; vt3_ready = 0; vt0_present = 0; vt1_present = 0; vt2_present = 0; vt3_present = 0; bundle_slot0_present = 1'd1; bundle_slot2_present = 1'd1; bundle_is_nop_mask = 8'd4; #1;");
    foreach (var (name, value) in new[] { ("initial_occupancy",5), ("initial_empty_slot_mask",250), ("initial_reference_opportunity_mask",250), ("final_next_occupancy",5), ("final_empty_slot_mask",250), ("final_reference_opportunity_mask",250) }) Expect(name, value);
    lines.Add("  $display(\"PASS non-stealable NOP remains occupied without reference opportunity\");");
    lines.Add("  set_base(); vt0_ready = 0; vt1_ready = 0; vt2_ready = 0; vt3_ready = 0; vt0_present = 0; vt1_present = 0; vt2_present = 0; vt3_present = 0; bundle_slot0_present = 1'd1; bundle_slot0_class = 3'd0; bundle_slot2_present = 1'd1; bundle_slot2_class = 3'd5; #1;");
    Expect("initial_class_occupied0", 1); Expect("initial_class_occupied5", 1); Expect("final_next_occupied0", 1); Expect("final_next_occupied5", 1);
    lines.Add("  $display(\"PASS eight-slot typed class occupancy derives from sampled slot tags\");");
    lines.Add("  set_base(); bundle_slot6_present = 1'd1; bundle_slot6_class = 3'd2; bundle_shared_low = 64'd1; vt0_candidate_shared_low = 64'd1; #1;");
    foreach (var (name, value) in new[] { ("c0_accepted",0), ("c0_reject_reason",3), ("c0_certificate_reject_detail",1), ("c3_accepted",0), ("c3_reject_reason",1), ("final_budget2",0), ("initial_occupancy",64), ("final_next_occupancy",64), ("final_empty_slot_mask",191), ("final_reference_opportunity_mask",191), ("initial_class_occupied2",1), ("final_next_occupied2",1), ("final_injections",0) }) Expect(name, value);
    lines.Add("  $display(\"PASS selected bundle shared certificate blocks candidate\");");
    lines.Add("  set_base(); bundle_slot6_present = 1'd1; #1;");
    foreach (var (name, value) in new[] { ("c0_accepted",0), ("c0_reject_reason",11), ("c0_stage_b_evaluated",1), ("c3_accepted",0), ("c3_stage_b_evaluated",1), ("c3_reject_reason",11), ("final_budget2",0), ("initial_occupancy",64), ("initial_empty_slot_mask",191), ("initial_reference_opportunity_mask",191), ("final_next_occupancy",64), ("final_empty_slot_mask",191), ("final_reference_opportunity_mask",191), ("final_next_occupied2",0), ("final_injections",0) }) Expect(name, value);
    lines.Add("  $display(\"PASS HardPinned lane6 conflict retries without committing occupancy\");");
    lines.Add("  set_base(); vt0_candidate_register_hazard_mask = 32'd1; bundle_vt0_mask = 32'd1; #1;");
    Expect("c0_accepted",1); Expect("c0_certificate_reject_detail",0);
    lines.Add("  $display(\"PASS RAR register access remains admissible\");");
    lines.Add("  set_base(); vt0_candidate_register_hazard_mask = 32'd1; bundle_vt0_mask = 32'd65536; #1;");
    Expect("c0_accepted",0); Expect("c0_certificate_reject_detail",2);
    lines.Add("  $display(\"PASS RAW register conflict is rejected\");");
    lines.Add("  set_base(); vt0_candidate_register_hazard_mask = 32'd65536; bundle_vt0_mask = 32'd1; #1;");
    Expect("c0_accepted",0); Expect("c0_certificate_reject_detail",2);
    lines.Add("  $display(\"PASS WAR register conflict is rejected\");");
    lines.Add("  set_base(); vt0_candidate_register_hazard_mask = 32'd65536; bundle_vt0_mask = 32'd65536; #1;");
    Expect("c0_accepted",0); Expect("c0_certificate_reject_detail",2);
    lines.Add("  $display(\"PASS WAW register conflict is rejected\");");
    lines.Add("  set_base(); vt0_candidate_shared_low = 64'd1; vt1_candidate_shared_low = 64'd1; #1;");
    Expect("c0_accepted",1); Expect("c1_accepted",0); Expect("c1_certificate_reject_detail",1); Expect("final_injections",1);
    lines.Add("  $display(\"PASS accepted candidate commits shared mask; next candidate observes conflict\");");
    lines.Add("  set_base(); snapshot_memory_issue_budget = 8'd1; budget2 = 8'd2; vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; vt0_is_load = 1'd1; vt0_bank_id = 32'd6; vt1_is_memory = 1'd1; vt1_is_loadstore = 1'd1; vt1_is_load = 1'd1; vt1_bank_id = 32'd6; vt1_pinned_lane = 3'd5; vt1_permitted_lanes = 8'd32; #1;");
    Expect("c0_accepted",1); Expect("c1_accepted",0); Expect("c1_reject_reason",7); Expect("final_remaining_memory_issue_budget",0); Expect("final_remaining_read_issue_budget",1); Expect("final_consumed_memory_budget_by_bank",4096); Expect("final_consumed_read_budget_by_bank",4096);
    lines.Add("  $display(\"PASS accepted Load consumes shared/read budget before next candidate retry\");");
    lines.Add("  set_base(); bundle_slot6_present = 1'd1; snapshot_memory_issue_budget = 8'd1; vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; vt0_is_load = 1'd1; vt0_bank_id = 32'd6; #1;");
    Expect("c0_accepted",0); Expect("c0_reject_reason",11); Expect("final_remaining_memory_issue_budget",1); Expect("final_consumed_memory_budget_by_bank",0);
    lines.Add("  $display(\"PASS Stage B lane rejection preserves memory budget state\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; vt0_is_load = 1'd1; vt0_bank_id = 32'd6; bundle_slot0_present = 1'd1; bundle_slot0_is_loadstore = 1'd1; bundle_slot0_is_load = 1'd1; bundle_slot0_bank_id = 32'd6; bundle_slot1_present = 1'd1; bundle_slot1_is_loadstore = 1'd1; bundle_slot1_is_store = 1'd1; bundle_slot1_bank_id = 32'd6; bundle_slot2_present = 1'd1; bundle_slot2_is_loadstore = 1'd1; bundle_slot2_is_load = 1'd1; bundle_slot2_bank_id = 32'd6; bundle_slot3_present = 1'd1; bundle_slot3_is_loadstore = 1'd1; bundle_slot3_is_load = 1'd1; bundle_slot3_bank_id = 32'd6; #1;");
    Expect("c0_accepted",0); Expect("c0_reject_reason",7); Expect("final_remaining_memory_issue_budget",0); Expect("final_remaining_read_issue_budget",0); Expect("final_remaining_write_issue_budget",1); Expect("final_consumed_memory_budget_by_bank",12288); Expect("final_consumed_read_budget_by_bank",12288); Expect("final_consumed_write_budget_by_bank",4096);
    lines.Add("  $display(\"PASS ordered original bundle seeds saturated shared/read/write budget tuples\");");
    lines.Add("  set_base(); vt0_candidate_shared_low = 64'd1; bundle_shared_low = 64'd1; attempt0_phase_key_valid = 1'd1; attempt0_phase_template_matches = 1'd0; #1;");
    Expect("c0_accepted",0); Expect("c0_certificate_reject_detail",1);
    lines.Add("  $display(\"PASS replay certificate ignored without complete key and template match\");");
    lines.Add("  set_base(); vt0_candidate_shared_low = 64'd1; attempt0_phase_key_valid = 1'd1; attempt0_phase_template_matches = 1'd1; attempt0_phase_shared_low = 64'd1; #1;");
    Expect("c0_accepted",0); Expect("c0_certificate_reject_detail",1);
    lines.Add("  $display(\"PASS matching replay certificate tuple is selected\");");
    lines.Add("  set_base(); speculation_budget_enabled = 1'd1; speculation_budget = 32'd0; vt0_is_memory = 1'd1; #1;");
    Expect("c0_accepted",0); Expect("c0_reject_reason",8);
    lines.Add("  $display(\"PASS exhausted speculation budget blocks cross-VT memory issue\");");
    lines.Add("  set_base(); speculation_budget_enabled = 1'd1; speculation_budget = 32'd1; vt0_is_memory = 1'd1; #1;");
    Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS positive speculation budget admits cross-VT memory issue\");");
    lines.Add("  set_base(); speculation_budget_enabled = 1'd1; speculation_budget = 32'hffffffff; vt0_is_memory = 1'd1; #1;");
    Expect("c0_accepted",0); Expect("c0_reject_reason",8);
    lines.Add("  $display(\"PASS negative signed speculation budget is nonpositive and rejects\");");
    lines.Add("  set_base(); vt0_outer_gate_condition_facts = 32'd32; #1;"); Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS external legacy speculation fact is ignored\");");
    lines.Add("  set_base(); speculation_budget_enabled = 1'd1; speculation_budget = 32'd0; speculation_budget_exhaustion_events = 64'd40; smt_rejections_count = 64'd40; budget1 = 8'd2; previous1 = 32'hffffffff; vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; vt0_is_load = 1'd1; vt0_bank_id = 32'd1; vt0_slot_class = 8'd1; vt0_pinned_lane = 8'd4; vt0_permitted_lanes = 8'd48; vt1_is_memory = 1'd1; vt1_is_loadstore = 1'd1; vt1_is_load = 1'd1; vt1_bank_id = 32'd1; vt1_slot_class = 8'd1; vt1_pinned_lane = 8'd4; vt1_permitted_lanes = 8'd48; #1;");
    Expect("c0_reject_reason",8); Expect("c1_reject_reason",8); Expect("final_speculation_budget_exhaustion_events",43); ExpectCounterTotal(40);
    lines.Add("  $display(\"PASS speculation exhaustion diagnostics cascade across two failed candidate attempts\");");
    lines.Add("  set_base(); speculation_budget_enabled = 1'd1; speculation_budget = 32'd0; speculation_budget_exhaustion_events = 64'd100; smt_rejections_count = 64'd100; suppress_lsu = 1'd1; vt0_is_memory = 1'd1; #1;");
    Expect("c0_reject_reason",5); Expect("final_speculation_budget_exhaustion_events",100); ExpectCounterTotal(100);
    lines.Add("  $display(\"PASS earlier A3 rejection does not increment speculation exhaustion diagnostic\");");
    lines.Add("  set_base(); speculation_budget_enabled = 1'd1; speculation_budget = 32'd0; speculation_budget_exhaustion_events = 64'd7; smt_rejections_count = 64'd7; vt0_is_memory = 1'd1; vt0_is_assist = 1'd1; #1;");
    Expect("final_speculation_budget_exhaustion_events",7); ExpectCounterTotal(7);
    lines.Add("  $display(\"PASS Assist bypass does not increment speculation exhaustion diagnostic\");");
    lines.Add("  set_base(); smt_rejections_count = 64'd50; vt0_candidate_shared_low = 64'd1; bundle_shared_low = 64'd1; #1;");
    Expect("c0_reject_reason",3); ExpectCounterTotal(50);
    lines.Add("  $display(\"PASS Stage A ResourceConflict increments aggregate SMT rejection count once\");");
    lines.Add("  set_base(); bundle_slot6_present = 1'd1; smt_rejections_count = 64'd60; #1;");
    Expect("c0_reject_reason",11); ExpectCounterTotal(60);
    lines.Add("  $display(\"PASS Stage B lane rejection increments aggregate SMT rejection count once\");");
    lines.Add("  set_base(); suppress_lsu = 1'd1; vt0_is_memory = 1'd1; #1;");
    Expect("c0_accepted",0); Expect("c0_reject_reason",5);
    lines.Add("  $display(\"PASS sampled SuppressLsu blocks memory candidate\");");
    lines.Add("  set_base(); suppress_lsu = 1'd1; #1;"); Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS sampled SuppressLsu bypasses non-memory candidate\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; #1;"); Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS memory candidate passes when SuppressLsu is clear\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; snapshot_memory_issue_budget = 8'd0; #1;");
    Expect("c0_accepted",0); Expect("c0_reject_reason",7);
    lines.Add("  $display(\"PASS shared memory issue exhaustion derives A3 HardwareBudgetReject\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; vt0_is_load = 1'd1; snapshot_read_issue_budget = 8'd0; #1;");
    Expect("c0_accepted",0); Expect("c0_reject_reason",7);
    lines.Add("  $display(\"PASS read directional exhaustion rejects Load\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; vt0_is_store = 1'd1; snapshot_read_issue_budget = 8'd0; snapshot_write_issue_budget = 8'd1; #1;");
    Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS Store uses write budget and ignores exhausted read budget\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; snapshot_read_issue_budget = 8'd0; snapshot_write_issue_budget = 8'd0; #1;");
    Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS Atomic-like LoadStore subtype consumes shared gate without directional gate\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; vt0_is_load = 1'd1; vt0_bank_id = 32'hffffffff; memory_bank_budget_at_least_one_mask = 16'd0; memory_bank_budget_at_least_two_mask = 16'd0; read_bank_budget_at_least_one_mask = 16'd0; read_bank_budget_at_least_two_mask = 16'd0; #1;");
    Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS invalid bank bypasses per-bank limits while global budgets remain positive\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; vt0_is_load = 1'd1; vt0_bank_id = 32'd6; memory_bank_budget_at_least_two_mask = 16'd65471; bundle_slot0_present = 1'd1; bundle_slot0_is_loadstore = 1'd1; bundle_slot0_is_load = 1'd1; bundle_slot0_bank_id = 32'd6; #1;");
    Expect("c0_accepted",0); Expect("c0_reject_reason",7);
    lines.Add("  $display(\"PASS packed two-bit per-bank consumption blocks at the sampled cap\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_outer_gate_condition_facts = 32'd1; #1;"); Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS legacy LSU condition bit is ignored\");");
    lines.Add("  set_base(); vt0_outer_gate_condition_facts = 32'd8; #1;");
    Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS legacy bundle-store condition bit is ignored\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; vt0_is_store = 1'd1; vt0_bank_id = 32'd6; bundle_slot0_present = 1'd1; bundle_slot0_is_loadstore = 1'd1; bundle_slot0_is_store = 1'd1; bundle_slot0_bank_id = 32'd6; #1;");
    Expect("c0_accepted",0); Expect("c0_reject_reason",6);
    lines.Add("  $display(\"PASS same-bank bundle Store rejects source-ordered A3\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; vt0_is_store = 1'd1; vt0_bank_id = 32'd6; bundle_slot0_present = 1'd1; bundle_slot0_is_loadstore = 1'd1; bundle_slot0_is_store = 1'd1; bundle_slot0_bank_id = 32'd5; #1;");
    Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS different-bank bundle Store does not block\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; vt0_is_store = 1'd1; vt0_bank_id = 32'd6; bundle_slot0_present = 1'd1; bundle_slot0_is_loadstore = 1'd1; bundle_slot0_bank_id = 32'd6; #1;");
    Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS same-bank bundle Load does not set Store mask\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; vt0_bank_id = 32'd6; bundle_slot0_present = 1'd1; bundle_slot0_is_loadstore = 1'd1; bundle_slot0_is_store = 1'd1; bundle_slot0_bank_id = 32'd6; #1;");
    Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS same-bank original-bundle Store does not block candidate Load\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; vt0_is_store = 1'd1; vt0_bank_id = 32'd16; bundle_slot0_present = 1'd1; bundle_slot0_is_loadstore = 1'd1; bundle_slot0_is_store = 1'd1; bundle_slot0_bank_id = 32'd16; #1;");
    Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS bundle Store bank outside 0..15 does not set mask\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; vt0_bank_id = 32'd6; score_vt0_slot0_value = 32'd0; score_vt0_slot0_type = 8'd2; score_vt0_slot0_bank_id = 32'd6; #1;");
    Expect("c0_accepted",0); Expect("c0_reject_reason",6);
    lines.Add("  $display(\"PASS per-VT same-bank outstanding memory rejects from scoreboard\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; vt0_bank_id = 32'd6; score_vt1_slot0_value = 32'd0; score_vt1_slot0_type = 8'd2; score_vt1_slot0_bank_id = 32'd6; #1;");
    Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS another VT same-bank outstanding entry does not block this VT\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; vt0_bank_id = 32'd6; score_vt0_slot0_value = 32'd0; score_vt0_slot0_type = 8'd2; score_vt0_slot0_bank_id = 32'd5; #1;");
    Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS different-bank outstanding entry does not block candidate\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; vt0_bank_id = 32'hffffffff; score_vt0_slot0_value = 32'd0; score_vt0_slot0_type = 8'd2; score_vt0_slot0_bank_id = 32'hffffffff; #1;");
    Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS negative candidate bank id bypasses pending-bank check\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1;");
    for (int slot = 0; slot < 8; slot++) lines.Add($"  score_vt0_slot{slot}_value = 32'd0; score_vt0_slot{slot}_type = 8'd2;");
    lines.Add("  #1;");
    Expect("c0_accepted",0); Expect("c0_reject_reason",5);
    lines.Add("  $display(\"PASS projected memory capacity rejects a full per-VT count\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1;");
    for (int slot = 0; slot < 7; slot++) lines.Add($"  score_vt0_slot{slot}_value = 32'd0; score_vt0_slot{slot}_type = 8'd2;");
    lines.Add("  vt0_outer_gate_condition_facts = 32'd2; #1;");
    Expect("c0_accepted",1); Expect("final_projected_count_vt0",8);
    lines.Add("  $display(\"PASS projected count has priority over legacy fact bit and commits after success\");");
    lines.Add("  set_base();"); for (int slot = 0; slot < 8; slot++) lines.Add($"  score_vt0_slot{slot}_value = 32'd0; score_vt0_slot{slot}_type = 8'd2;"); lines.Add("  #1;");
    Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS non-memory candidate bypasses projected memory capacity\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1;"); for (int slot = 0; slot < 7; slot++) lines.Add($"  score_vt0_slot{slot}_value = 32'd0; score_vt0_slot{slot}_type = 8'd2;"); lines.Add("  #1;");
    Expect("c0_accepted",1); Expect("final_projected_count_vt0",7);
    lines.Add("  $display(\"PASS non-LoadStore memory-marked candidate does not consume projected scoreboard count\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1;");
    for (int slot = 0; slot < 8; slot++) lines.Add($"  score_vt0_slot{slot}_value = 32'hffffffff; score_vt0_slot{slot}_type = 8'd2;");
    lines.Add("  #1;"); Expect("c0_accepted",0); Expect("c0_reject_reason",5);
    lines.Add("  $display(\"PASS outstanding Load entries count independently of free allocator values\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1;");
    for (int slot = 0; slot < 8; slot++) lines.Add($"  score_vt0_slot{slot}_value = 32'd0; score_vt0_slot{slot}_type = 8'd0;");
    lines.Add("  #1;"); Expect("c0_accepted",0); Expect("c0_reject_reason",5);
    lines.Add("  $display(\"PASS non-free allocator values independently exhaust projected capacity\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1; vt0_is_loadstore = 1'd1; bundle_slot0_present = 1'd1; bundle_slot0_is_loadstore = 1'd1; bundle_slot0_vt = 32'd0; #1;");
    Expect("c0_accepted",1); Expect("final_projected_count_vt0",2);
    lines.Add("  $display(\"PASS original bundle seeds projected count before a new accepted LoadStore\");");
    lines.Add("  set_base(); vt0_is_memory = 1'd1;");
    for (int slot = 0; slot < 8; slot++) lines.Add($"  bundle_slot{slot}_present = 1'd1; bundle_slot{slot}_is_loadstore = 1'd1; bundle_slot{slot}_vt = 32'd0;");
    lines.Add("  #1;"); Expect("c0_accepted",0); Expect("c0_reject_reason",5);
    lines.Add("  $display(\"PASS eight original bundle LoadStores saturate the projected count\");");
    lines.Add("  set_base(); suppress_lsu = 1'd1; vt0_is_memory = 1'd1; vt0_outer_gate_condition_facts = 32'd16; #1;");
    Expect("c0_accepted",0); Expect("c0_reject_reason",5);
    lines.Add("  $display(\"PASS outer gate selects the first source-order rejection\");");
    lines.Add("  set_base(); vt0_is_assist = 1'd1; vt0_outer_gate_condition_facts = 32'd1; #1;");
    Expect("c0_accepted",1);
    lines.Add("  $display(\"PASS Assist bypasses the A3 outer-cap gate\");");
    lines.Add("  $display(\"PASS all directed RTL scenarios\"); $finish;");
    lines.Add("end"); lines.Add("endmodule");
    return string.Join(Environment.NewLine, lines) + Environment.NewLine;
}


static async Task PublishCandidate(string runDirectory, string workspace)
{
    string run = Path.GetFullPath(runDirectory);
    string target = Path.GetFullPath(workspace);
    if (!Directory.Exists(run) || !Directory.Exists(target))
        throw new DirectoryNotFoundException("Candidate run and existing publication workspace are required.");
    byte[] hwir = await File.ReadAllBytesAsync(Path.Combine(run, "smt-rank-tail.hwir.json"));
    var serializer = new CanonicalHwirSerializer();
    HwDesign design = serializer.Deserialize(hwir);
    var validation = new FailClosedHardwareIrValidator().Validate(design);
    HwValidatedDesign candidate = validation.Validated ?? throw new InvalidDataException("HWIR refused: " + string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));
    byte[] Read(string name) => File.ReadAllBytes(Path.Combine(run, name));
    string Sha(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    byte[] pins = Read("smt-rank-tail-source-pins.json");
    var supplements = new[]
    {
        ("smt-rank-tail-rtl", StudioArtifactKind.SystemVerilog, "smt-rank-tail.sv"),
        ("smt-rank-tail-testbench", StudioArtifactKind.SystemVerilog, "smt-rank-tail.tb.sv"),
        ("smt-rank-tail-mlir", StudioArtifactKind.Mlir, "smt-rank-tail.mlir"),
        ("smt-rank-tail-source-inventory", StudioArtifactKind.SourceInventory, "smt-rank-tail-source-inventory.json"),
        ("smt-rank-tail-captured-sources", StudioArtifactKind.CapturedSources, "smt-rank-tail-captured-sources.json"),
        ("smt-rank-tail-source-capture-index", StudioArtifactKind.CandidateRunProvenance, "smt-rank-tail-source-capture-index.json"),
        ("smt-rank-tail-simulation", StudioArtifactKind.CandidateRunProvenance, "smt-rank-tail-simulation-receipt.json"),
        ("smt-rank-tail-reference", StudioArtifactKind.CandidateRunProvenance, "smt-rank-tail-reference-v1.json"),
        ("smt-rank-tail-semantic-crosscheck", StudioArtifactKind.CandidateRunProvenance, "smt-rank-tail-semantic-crosscheck.json"),
        ("smt-rank-tail-firtool-version", StudioArtifactKind.CandidateRunProvenance, "firtool-version.txt"),
        ("smt-rank-tail-firtool-log", StudioArtifactKind.CandidateRunProvenance, "firtool-export.log"),
        ("smt-rank-tail-iverilog-version", StudioArtifactKind.CandidateRunProvenance, "iverilog-version.txt"),
        ("smt-rank-tail-vvp-version", StudioArtifactKind.CandidateRunProvenance, "vvp-version.txt"),
        ("smt-rank-tail-iverilog-compile-log", StudioArtifactKind.CandidateRunProvenance, "iverilog-compile.log"),
        ("smt-rank-tail-vvp-simulation-log", StudioArtifactKind.CandidateRunProvenance, "vvp-simulation.log"),
        ("smt-rank-tail-vcd", StudioArtifactKind.CandidateRunProvenance, "smt-rank-tail.vcd"),
        ("smt-rank-tail-tool-hashes", StudioArtifactKind.CandidateRunProvenance, "tool-hashes.txt")
    }.Where(x => File.Exists(Path.Combine(run, x.Item3)) && new FileInfo(Path.Combine(run, x.Item3)).Length > 0)
        .Select(x => { byte[] bytes = Read(x.Item3); return new StudioSupplementArtifact(x.Item1, x.Item2, x.Item3, bytes, Sha(bytes)); }).ToArray();
    bool rtlSimulationPassed = File.Exists(Path.Combine(run, "smt-rank-tail-simulation-receipt.json"));
    if (rtlSimulationPassed)
    {
        using JsonDocument simulationDoc = JsonDocument.Parse(Read("smt-rank-tail-simulation-receipt.json"));
        JsonElement root = simulationDoc.RootElement;
        string Get(JsonElement parent, string name) => parent.GetProperty(name).GetString() ?? throw new InvalidDataException("Null RTL receipt field " + name);
        JsonElement source = root.GetProperty("source"), tools = root.GetProperty("tools"), commands = root.GetProperty("commands"), artifacts = root.GetProperty("artifacts");
        if (Get(root, "schema") != "circuitweave.smt-rank-tail-rtl-simulation/v3" || Get(root, "status") != "candidate-rtl-simulation-passed" ||
            Get(source, "hwirSha256") != candidate.CanonicalDigest || Get(source, "mlirSha256") != Sha(Read("smt-rank-tail.mlir")) ||
            Get(artifacts, "rtlSha256") != Sha(Read("smt-rank-tail.sv")) || Get(artifacts, "testbenchSha256") != Sha(Read("smt-rank-tail.tb.sv")) ||
            Get(artifacts, "simulationLogSha256") != Sha(Read("vvp-simulation.log")) ||
            Get(artifacts, "vcdSha256") != Sha(Read("smt-rank-tail.vcd")) ||
            Get(tools, "circtExecutableSha256") != "187c0c0f0f1464a58997a3e99ca96560cc002b97276cc38c12c28addf0112c4e" ||
            Get(tools, "iverilogExecutableSha256") != "a6681e0b577dcfc4ea83d1591a5714710b0b3535ebb36491ee8177553bab9631" ||
            Get(tools, "vvpExecutableSha256") != "e0a087a4906d5a9cd1b0b85c9b4c2081704c2c9bcd94f224437fc56b15fc440d" ||
            commands.GetProperty("rtlExportExit").GetInt32() != 0 || commands.GetProperty("compileExit").GetInt32() != 0 || commands.GetProperty("simulationExit").GetInt32() != 0 ||
            !Get(artifacts, "simulationOutput").Contains("PASS all directed RTL scenarios", StringComparison.Ordinal))
            throw new InvalidDataException("RTL simulation receipt is stale, incomplete or does not match emitted inputs/outputs.");
    }
    string digest = candidate.CanonicalDigest;
    var stages = new[]
    {
        new CompilationStageRecord(CompilationStage.BindingAuthorization, CompilationStageStatus.NotRun, "No binding lock or authorization stage was executed."),
        new CompilationStageRecord(CompilationStage.SymbolResolution, CompilationStageStatus.NotRun, "No producer symbol-resolution stage was executed."),
        new CompilationStageRecord(CompilationStage.SourceAdmission, CompilationStageStatus.NotRun, "Source admission was not executed."),
        new CompilationStageRecord(CompilationStage.HardwareIrValidation, CompilationStageStatus.Completed, "Fail-closed HWIR validation passed for source-bound rank, collection, pressure, four-attempt guards, certificate predicate, source-ordered A3 gate, sampled scoreboard/original-bundle projected-memory state, current A3 bit4 budget predicate, A3 bit5 cross-VT speculation predicate, and accepted-candidate c0..c3 shared/read/write budget and packed-bank counter cascade. Initial budget tuples remain external inputs."),
        new CompilationStageRecord(CompilationStage.RtlEmission, rtlSimulationPassed ? CompilationStageStatus.Completed : CompilationStageStatus.NotRun, rtlSimulationPassed ? "Pinned CIRCT firtool-1.160.0 exported RTL from the exact source-pinned MLIR; RTL, tool pin and export log are attached. This is candidate RTL." : "MLIR lowered; no qualified CIRCT RTL export was attached."),
        new CompilationStageRecord(CompilationStage.IndependentVerification, CompilationStageStatus.Completed, rtlSimulationPassed ? "Independent combinational HWIR oracle passed 4,704 rank/collection plus 57 directed retry, guard, certificate, replay, A3, projected-state, original-bundle budget initialization, memory-budget cascade and diagnostic-counter cases; pinned Icarus 12.0 compiled emitted RTL and passed directed A3 speculation scenarios and the remaining existing scenarios. Ordered original Load/Store facts seed snapshot credits and saturating bank counters; later candidates observe accepted prior LoadStore updates; Stage B rejection leaves state unchanged. Snapshot values and typed bundle facts remain explicit sampled inputs; runtime candidate identity is unbound. SmtRejectionsCount is modeled only for the typed non-Assist four-candidate projection; Assist quota/backpressure, legacy path, FSP, replay refresh and temporal lifecycle remain unmodeled; this is not Golden equivalence." : "Independent combinational HWIR evaluator matched 4,704 rank/collection vectors plus 57 directed retry, guard, certificate, replay, A3, projected-state, original-bundle budget initialization, budget-cascade and diagnostic-counter vectors. Snapshot values and typed bundle facts remain explicit sampled inputs; runtime object identity is unbound. SmtRejectionsCount is modeled only for the typed non-Assist four-candidate projection. Assist quota/backpressure, legacy path, FSP and temporal behavior remain unmodeled. No RTL or Golden equivalence."),
        new CompilationStageRecord(CompilationStage.EvidencePromotion, CompilationStageStatus.NotRun, "Candidate remains StructuralOnly.")
    };
    var request = new StudioCompilerPublicationRequest("hybridcpu-smt-rank-collected", "HybridCPU SMT rank and candidate collection composition", "rankcollect" + digest[..16], candidate,
        new HwTargetDescription("CIRCT generic", "combinational", "circt-cli"), stages, Supplements: supplements, SourceDigest: Sha(pins));
    StudioPublicationReceipt receipt = await new StudioSnapshotPublisher().PublishAsync(request, target);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(receipt, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    var opened = await new StudioWorkspaceLoader().OpenAsync(Path.Combine(target, "studio.project.json"));
    if (!opened.Success || opened.Value is null) throw new InvalidDataException("Studio loader refused composed SMT snapshot: " + opened.Error);
    var session = opened.Value;
    int moduleCount = session.Graph.Nodes.Count(n => n.Kind == CircuitWeave.Studio.Contracts.StudioGraphNodeKind.Module);
    int instanceCount = session.Graph.Nodes.Count(n => n.Kind == CircuitWeave.Studio.Contracts.StudioGraphNodeKind.Instance);
    if (moduleCount != 21 || instanceCount != 26 || session.Graph.Trust != CircuitWeave.Studio.Contracts.StudioDocumentTrust.StructuralOnly)
        throw new InvalidDataException($"Unexpected Studio graph/modules/instances/trust: {moduleCount}/{instanceCount}/{session.Graph.Trust}");
    var navigation = GraphNavigationIndex.Create(session.Graph);
    var roots = navigation.GetRoots(0, 16);
    var page1 = navigation.GetChildren(navigation.TopModuleId, 0, 12);
    var page2 = navigation.GetChildren(navigation.TopModuleId, 12, 12);
    if (roots.TotalCount == 0 || page1.TotalCount <= 12 || page2.Items.Length == 0)
        throw new InvalidDataException("Studio hierarchy root/children paging failed.");
    var tree = GraphTreeProjection.Create(navigation).SetExpanded(navigation.TopModuleId, true);
    var treeFirst = tree.GetWindow(0, 12);
    var treeSecond = tree.GetWindow(12, 12);
    if (treeFirst.TotalCount <= 12 || treeSecond.Items.Length == 0)
        throw new InvalidDataException("Studio visible hierarchy row paging failed.");
    var sourceMatches = SourceInventorySearch.Find(session.SourceInventory?.Symbols ?? Array.Empty<StudioSourceInventorySymbol>(), "MicroOpScheduler");
    if (sourceMatches.Count == 0 || !sourceMatches.Any(m => m.Symbol.Symbol.Contains("PackBundleIntraCoreSmt", StringComparison.Ordinal)))
        throw new InvalidDataException("Studio source-only inventory search did not find the captured scheduler member.");
    var mappedSource = session.SourceMap?.Mappings.FirstOrDefault(m => m.Source.Commit == Commit && m.Source.RelativePath.Contains("MicroOpScheduler.SMT.cs", StringComparison.Ordinal))
        ?? throw new InvalidDataException("Studio source-map did not retain source-bound SMT locations.");
    if (!navigation.TryGetNode(mappedSource.GraphNodeId, out _) || navigation.GetPath(mappedSource.GraphNodeId).IsDefaultOrEmpty)
        throw new InvalidDataException("Studio source-mapped graph node could not be selected/navigated.");
    var selected = GraphNavigationState.Initial(navigation).Select(navigation, mappedSource.GraphNodeId);
    if (selected.SelectedNodeId != mappedSource.GraphNodeId || tree.GetWindow(0, 12, selected).Items.Length == 0)
        throw new InvalidDataException("Studio source-mapped node selection did not bind to the paged hierarchy.");
    string? capturedExcerpt = CapturedNodeSource.Excerpt(session, mappedSource.Source);
    if (capturedExcerpt?.Contains("PackBundleIntraCoreSmt", StringComparison.Ordinal) != true)
        throw new InvalidDataException("Studio could not resolve the mapped SMT excerpt from captured snapshot bytes.");
    if (session.Artifacts.Count < 9 || session.Artifacts.Any(a => !a.HasDeclaredDigest || a.Artifact.Sha256 != a.ActualSha256 || a.Artifact.ByteLength != a.ActualSize))
        throw new InvalidDataException("Studio artifact inspection found missing digest or length mismatches: artifacts=" + session.Artifacts.Count);
    Console.WriteLine($"Studio API PASS: {moduleCount} modules, {instanceCount} instances, {session.Graph.Nodes.Count} nodes, {session.Graph.Edges.Count} edges; hierarchy paging, source inventory search, selected-node navigation, captured SMT excerpt and {session.Artifacts.Count} hash/length-checked artifacts resolved; trust={session.Graph.Trust}; diagnostics={session.Diagnostics.Count}");
    foreach (string diagnostic in session.Diagnostics) Console.WriteLine("Studio diagnostic: " + diagnostic);
}
