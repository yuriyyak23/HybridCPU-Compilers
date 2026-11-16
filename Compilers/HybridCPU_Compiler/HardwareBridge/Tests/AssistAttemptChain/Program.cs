using System.Numerics;
using System.Text;
using System.Text.Json;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using CircuitWeave.Orchestration;
using CircuitWeave.Studio.GraphNavigation;
using CircuitWeave.Studio.Integration;
using CircuitWeave.Visualization;
using CircuitWeave.Visualization.Publishing;
using HybridCPU.Compiler.HardwareBridge.Hwir;

const string model = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE\CloseToHSL\Core\Pipeline\Assist\Runtime\MicroOpScheduler.Assist.cs";
const string commit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
if (args.Length == 3 && args[0] == "--publish") { await PublishCandidate(args[1], args[2]); return; }
if (args.Length != 1) throw new ArgumentException("Pass an existing output directory under HardwareBridge, or --publish <run> <workspace>.");
string output = Path.GetFullPath(args[0]);
string bridgeRoot = @"C:\Users\Yuriy\Desktop\HybridCPU Compilers\Compilers\HybridCPU_Compiler\HardwareBridge";
if (!output.StartsWith(bridgeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(output))
    throw new InvalidOperationException("Output must be an existing directory under HardwareBridge.");
byte[] source = await File.ReadAllBytesAsync(model);
var toolchain = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
HwValidatedDesign design = BridgeHwirAssistAttemptChain.Compile(source, commit, toolchain);
HwModule module = design.Design.Modules.Single(m => m.Id == design.Design.TopModule);
byte[] mlir = Encoding.UTF8.GetBytes(new HierarchicalCirctMlirLowerer().LowerToMlir(design,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>())).Text);
await File.WriteAllBytesAsync(Path.Combine(output, "assist-attempt-chain.mlir"), mlir);
await File.WriteAllBytesAsync(Path.Combine(output, "assist-attempt-chain.hwir.json"), new CanonicalHwirSerializer().Serialize(design));

var rng = new Random(0xA771);
int vectors = 0;
for (int n = 0; n < 4096; n++)
{
    var input = Base();
    input["assist_enabled"] = (ulong)rng.Next(2);
    input["slot_available"] = (ulong)rng.Next(2);
    input["boundary_blocked"] = (ulong)rng.Next(2);
    input["typed_slot_enabled"] = (ulong)rng.Next(2);
    input["owner_vt"] = (ulong)rng.Next(4);
    int[] ranks = [0, 1, 2];
    rng.Shuffle(ranks);
    for (int i = 0; i < 4; i++)
    {
        input[$"rank{i}_defined"] = i == 3 ? (ulong)rng.Next(2) : 1;
        input[$"rank{i}_vt"] = (ulong)(i == 3 && rng.Next(8) == 0 ? rng.Next(8) : i == 3 ? rng.Next(4) : ranks[i]);
        foreach (string suffix in new[] { "port_valid", "port_nonnull", "stage_a_pass", "stage_b_pass", "is_assist_op", "backpressure_pass", "quota_pass", "legacy_legal", "legacy_dma_sensitive" })
            input[$"rank{i}_{suffix}"] = (ulong)rng.Next(2);
    }
    Check(input); vectors++;
}

var directed = new List<(string Name, Dictionary<string, ulong> Inputs)>
{
    ("Stage A reject retries next rank", Candidate(0, stageA: false, success: false, nextSuccess: true)),
    ("Stage B reject retries next rank", Candidate(1, stageB: false, success: false, nextSuccess: true)),
    ("Backpressure reject retries next rank", Candidate(2, backpressure: false, success: false, nextSuccess: true)),
    ("Quota reject retries next rank", Candidate(3, quota: false, success: false, nextSuccess: true)),
    ("Success stops before later ranks", Candidate(0, success: true, nextSuccess: false)),
    ("Owner and invalid port are skipped", SkipThenCommit()),
    ("No slot prevents all attempts", EntryBlocked(slot: true, boundary: false)),
    ("Boundary gate prevents all attempts", EntryBlocked(slot: false, boundary: true)),
    ("Undefined rank 3 remains unqualified", UndefinedRank3()),
    ("Defined out-of-range rank 3 is reported", OutOfRangeRank3()),
    ("Legacy DMA-sensitive Assist retries", LegacyDmaRetry()),
    ("Legacy non-Assist bypasses reservation inputs", LegacyNonAssist())
};
foreach (var item in directed) Check(item.Inputs);
var lines = BuildTestbench(directed);
await File.WriteAllTextAsync(Path.Combine(output, "assist-attempt-chain.tb.sv"), lines);
await File.WriteAllTextAsync(Path.Combine(output, "assist-attempt-chain-semantic.json"), JsonSerializer.Serialize(new
{
    status = "passed", vectors, directedCases = directed.Count,
    oracle = "independent imperative per-rank retry/stop model; stage legality, backpressure, quota results, ranks and runtime port facts are sampled inputs",
    sourceSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)).ToLowerInvariant(),
    scope = "Source-pinned four-rank Assist attempt control under AssistBundleQuota=1. Does not derive admission predicates or chain quota/backpressure mutation state. Rank3 credit-order source defect remains explicit as rank3_defined/rank3_out_of_range_reached."
}, new JsonSerializerOptions { WriteIndented = true }));
await WriteSourceEvidence(output, source, design);
Console.WriteLine($"Assist attempt-chain HWIR PASS: {design.CanonicalDigest}; independent evaluator {vectors} random + {directed.Count} directed cases; emitted MLIR and RTL testbench.");

async Task WriteSourceEvidence(string dir, byte[] captured, HwValidatedDesign validated)
{
    var method = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(captured)).GetRoot()
        .DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
        .Single(m => m.Identifier.ValueText == "TryInjectAssistCandidates");
    var pos = method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
    string sourcePath = Path.GetFullPath(model);
    string producerRoot = @"C:\Users\Yuriy\Desktop\CircuitWeave-main";
    string projectGraphPath = Path.Combine(producerRoot, "artifacts/processor-20261114/publication-inputs/hybridcpu-debug-project-graph-v3.json");
    byte[] projectGraph = await File.ReadAllBytesAsync(projectGraphPath);
    byte[] pins = JsonSerializer.SerializeToUtf8Bytes(new { schema="circuitweave.assist-attempt-chain-source-pins/v1", modelCommit=commit, sourceSha256=BridgeHwirAssistAttemptChain.AssistSourceSha256, sourcePath, length=captured.LongLength }, new JsonSerializerOptions { WriteIndented=true });
    byte[] captureIndex = JsonSerializer.SerializeToUtf8Bytes(new { schema="circuitweave.assist-attempt-chain-source-capture/v1", modelCommit=commit, sourceTreeState="dirty-listed-input-captured", files=new[] { new { path=sourcePath, sha256=Hash(captured), length=captured.LongLength } }, projectGraphPath, projectGraphSha256=Hash(projectGraph) }, new JsonSerializerOptions { WriteIndented=true });
    byte[] semanticBytes = await File.ReadAllBytesAsync(Path.Combine(dir, "assist-attempt-chain-semantic.json"));
    byte[] inventory = JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema="circuitweave.source-coverage-index/v1", disposition="incomplete-unqualified", project="HybridCPU_ISE/HybridCPU_ISE.csproj", configuration="Debug", targetFramework="net11.0",
        scope="One exact active dirty-worktree Assist source file and a source-bound four-rank retry/stop method; outcome legality and runtime state are sampled.",
        semanticReportSha256=Hash(semanticBytes),
        captureVerification=new { schema="circuitweave.source-capture-check/v1", status="producer-reported-listed-inputs-match", semanticReportSha256=Hash(semanticBytes), sourceCaptureSha256=Hash(captureIndex), projectGraphSha256=Hash(projectGraph), checkedFiles=1, rootProject="not-captured", scope="One exact listed Assist file; project graph is a selected Debug project-graph artifact, not proof of complete source closure." },
        capturedSourceFiles=new[] { new { path=sourcePath, sha256=Hash(captured), length=captured.LongLength } },
        symbols=new[] { new { symbol="global::YAKSys_Hybrid_CPU.Core.MicroOpScheduler", assembly="CircuitWeave.AssistAttemptChain", kind="Class", memberCount=1,
            declarations=new[] { new { location=new { file=sourcePath, startLine=pos.Line+1, startColumn=pos.Character+1 }, memberIds=new[] { "M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.TryInjectAssistCandidates()" } } },
            members=new[] { new { id="M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.TryInjectAssistCandidates()", kind="Method", locations=new[] { new { file=sourcePath, startLine=pos.Line+1, startColumn=pos.Character+1 } } } },
            blockingReason="Selected Assist method only; not full Processor symbol closure. Stage outcomes, reservation updates and credit rank facts remain sampled." } }
    }, new JsonSerializerOptions { WriteIndented=true });
    byte[] capturedBytes = JsonSerializer.SerializeToUtf8Bytes(new { schema="circuitweave.captured-sources/v1", inventorySha256=Hash(inventory), files=new[] { new { path=sourcePath, bytes=Convert.ToBase64String(captured) } } }, new JsonSerializerOptions { WriteIndented=true });
    await File.WriteAllBytesAsync(Path.Combine(dir,"assist-attempt-chain-source-pins.json"),pins);
    await File.WriteAllBytesAsync(Path.Combine(dir,"assist-attempt-chain-source-capture-index.json"),captureIndex);
    await File.WriteAllBytesAsync(Path.Combine(dir,"assist-attempt-chain-project-graph.json"),projectGraph);
    await File.WriteAllBytesAsync(Path.Combine(dir,"assist-attempt-chain-source-inventory.json"),inventory);
    await File.WriteAllBytesAsync(Path.Combine(dir,"assist-attempt-chain-captured-sources.json"),capturedBytes);
}

Dictionary<string, ulong> Base()
{
    var values = module.Ports.Where(p => p.Direction == HwPortDirection.Input)
        .ToDictionary(p => p.Name, _ => 0UL, StringComparer.Ordinal);
    values["assist_enabled"] = 1; values["slot_available"] = 1; values["typed_slot_enabled"] = 1;
    for (int i = 0; i < 4; i++)
    {
        values[$"rank{i}_defined"] = 1; values[$"rank{i}_vt"] = (ulong)i;
        values[$"rank{i}_port_valid"] = 1; values[$"rank{i}_port_nonnull"] = 1;
        values[$"rank{i}_stage_a_pass"] = 1; values[$"rank{i}_stage_b_pass"] = 1;
        values[$"rank{i}_backpressure_pass"] = 1; values[$"rank{i}_quota_pass"] = 1;
        values[$"rank{i}_legacy_legal"] = 1;
    }
    return values;
}

Dictionary<string, ulong> Candidate(int owner, bool stageA = true, bool stageB = true, bool backpressure = true,
    bool quota = true, bool success = true, bool nextSuccess = false)
{
    var x = Base(); x["owner_vt"] = (ulong)owner;
    x["rank0_stage_a_pass"] = stageA ? 1UL : 0UL; x["rank0_stage_b_pass"] = stageB ? 1UL : 0UL;
    x["rank0_backpressure_pass"] = backpressure ? 1UL : 0UL; x["rank0_quota_pass"] = quota ? 1UL : 0UL;
    x["rank0_vt"] = (ulong)((owner + 1) % 4); x["rank0_is_assist_op"] = 1;
    if (nextSuccess) { x["rank1_vt"] = (ulong)((owner + 2) % 4); x["rank1_is_assist_op"] = 1; }
    else if (!success) x["rank0_port_valid"] = 0;
    return x;
}

Dictionary<string, ulong> SkipThenCommit()
{
    var x = Base(); x["owner_vt"] = 0; x["rank0_vt"] = 0; x["rank1_port_valid"] = 0;
    x["rank2_vt"] = 2; return x;
}

Dictionary<string, ulong> EntryBlocked(bool slot, bool boundary)
{
    var x = Base(); x["slot_available"] = slot ? 0UL : 1UL; x["boundary_blocked"] = boundary ? 1UL : 0UL; return x;
}

Dictionary<string, ulong> UndefinedRank3()
{
    var x = Base(); x["owner_vt"] = 0; x["rank0_stage_a_pass"] = 0; x["rank1_stage_a_pass"] = 0;
    x["rank2_stage_a_pass"] = 0; x["rank3_defined"] = 0; return x;
}

Dictionary<string, ulong> OutOfRangeRank3()
{
    var x = UndefinedRank3(); x["rank3_defined"] = 1; x["rank3_vt"] = 7; return x;
}

Dictionary<string, ulong> LegacyDmaRetry()
{
    var x = Candidate(0, success: false, nextSuccess: true); x["typed_slot_enabled"] = 0;
    x["rank0_legacy_dma_sensitive"] = 1; x["rank1_legacy_dma_sensitive"] = 0; return x;
}

Dictionary<string, ulong> LegacyNonAssist()
{
    var x = Candidate(0); x["typed_slot_enabled"] = 0; x["rank0_is_assist_op"] = 0;
    x["rank0_backpressure_pass"] = 0; x["rank0_quota_pass"] = 0; return x;
}

void Check(Dictionary<string, ulong> input)
{
    Dictionary<string, ulong> expected = Oracle(input);
    Dictionary<string, ulong> actual = Evaluate(module, input);
    foreach (var pair in expected)
        if (actual[pair.Key] != pair.Value) throw new InvalidOperationException($"Attempt-chain mismatch {pair.Key}: actual={actual[pair.Key]} expected={pair.Value}; input={JsonSerializer.Serialize(input)}; actualOutputs={JsonSerializer.Serialize(actual)}; expectedOutputs={JsonSerializer.Serialize(expected)}");
}

Dictionary<string, ulong> Oracle(Dictionary<string, ulong> x)
{
    var result = module.Ports.Where(p => p.Direction == HwPortDirection.Output).ToDictionary(p => p.Name, _ => 0UL, StringComparer.Ordinal);
    bool active = x["assist_enabled"] != 0 && x["slot_available"] != 0 && x["boundary_blocked"] == 0;
    bool typed = x["typed_slot_enabled"] != 0; int count = 0;
    for (int i = 0; i < 4 && active && count < BridgeHwirAssistAttemptChain.AssistBundleQuota; i++)
    {
        bool defined = x[$"rank{i}_defined"] != 0;
        result[$"rank{i}_reached"] = defined ? 1UL : 0UL;
        if (i == 3 && !defined) result["rank3_unqualified_reached"] = 1;
        if (!defined) continue;
        ulong vt = x[$"rank{i}_vt"];
        if (i == 3 && vt >= 4 && vt != x["owner_vt"]) result["rank3_out_of_range_reached"] = 1;
        if (vt >= 4 || vt == x["owner_vt"] || x[$"rank{i}_port_valid"] == 0 || x[$"rank{i}_port_nonnull"] == 0) continue;
        result[$"rank{i}_candidate_valid"] = 1;
        bool isAssist = x[$"rank{i}_is_assist_op"] != 0;
        bool commit;
        if (typed)
        {
            if (x[$"rank{i}_stage_a_pass"] == 0) { result[$"rank{i}_stage_a_rejected"] = 1; continue; }
            if (x[$"rank{i}_stage_b_pass"] == 0) { result[$"rank{i}_stage_b_rejected"] = 1; continue; }
            if (isAssist && x[$"rank{i}_backpressure_pass"] == 0) { result[$"rank{i}_backpressure_rejected"] = 1; continue; }
            if (isAssist && x[$"rank{i}_quota_pass"] == 0) { result[$"rank{i}_quota_rejected"] = 1; continue; }
            commit = true;
        }
        else
        {
            if (x[$"rank{i}_legacy_legal"] == 0 || (isAssist && x[$"rank{i}_legacy_dma_sensitive"] != 0))
            { result[$"rank{i}_legacy_rejected"] = 1; continue; }
            if (isAssist && x[$"rank{i}_backpressure_pass"] == 0) { result[$"rank{i}_legacy_rejected"] = 1; result[$"rank{i}_backpressure_rejected"] = 1; continue; }
            if (isAssist && x[$"rank{i}_quota_pass"] == 0) { result[$"rank{i}_legacy_rejected"] = 1; result[$"rank{i}_quota_rejected"] = 1; continue; }
            commit = true;
        }
        if (commit) { result[$"rank{i}_committed"] = 1; count++; active = false; }
    }
    result["injection_count"] = (ulong)count; result["final_scan_active"] = active ? 1UL : 0UL;
    return result;
}

Dictionary<string, ulong> Evaluate(HwModule m, Dictionary<string, ulong> input)
{
    var map = m.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
    var drives = m.Nodes.OfType<HwOutputDriveNode>().ToDictionary(n => n.OutputPort, n => n.Value, StringComparer.Ordinal);
    var cache = new Dictionary<string, ulong>(input, StringComparer.Ordinal);
    ulong Signal(string name)
    {
        if (cache.TryGetValue(name, out ulong value)) return value;
        HwNode n = map[name];
        ulong result = n switch
        {
            HwConstantNode c => Convert.ToUInt64(c.BitPattern, 2),
            HwMuxNode mux => Signal(mux.Condition) != 0 ? Signal(mux.WhenTrue) : Signal(mux.WhenFalse),
            HwOperationNode op => Apply(op),
            _ => throw new InvalidDataException("Unexpected node " + n.GetType().Name)
        };
        int width = n switch { HwConstantNode c => c.Type.Width, HwMuxNode x => x.ResultType.Width, HwOperationNode x => x.ResultType.Width, _ => 64 };
        return cache[name] = result & ((1UL << Math.Min(width, 63)) - 1UL | (width == 64 ? 1UL << 63 : 0));
    }
    ulong Apply(HwOperationNode op)
    {
        ulong a = Signal(op.Operands[0]), b = op.Operands.Count > 1 ? Signal(op.Operands[1]) : 0;
        return op.Opcode switch
        {
            "and" => a & b, "or" => a | b, "xor" => a ^ b, "eq" => a == b ? 1UL : 0UL,
            "ult64" => a < b ? 1UL : 0UL, "add" => a + b, "zext" => a, _ => throw new InvalidDataException("Unexpected opcode " + op.Opcode)
        };
    }
    return m.Ports.Where(p => p.Direction == HwPortDirection.Output).ToDictionary(p => p.Name, p => Signal(drives[p.Name]), StringComparer.Ordinal);
}

string BuildTestbench(List<(string Name, Dictionary<string, ulong> Inputs)> cases)
{
    var inputPorts = module.Ports.Where(p => p.Direction == HwPortDirection.Input).ToArray();
    var outputPorts = module.Ports.Where(p => p.Direction == HwPortDirection.Output).ToArray();
    var b = new StringBuilder("`timescale 1ns/1ps\nmodule assist_attempt_chain_tb;\n");
    foreach (var p in inputPorts) b.AppendLine(p.Type.Width == 1 ? $"logic {p.Name};" : $"logic [{p.Type.Width - 1}:0] {p.Name};");
    foreach (var p in outputPorts) b.AppendLine(p.Type.Width == 1 ? $"wire {p.Name};" : $"wire [{p.Type.Width - 1}:0] {p.Name};");
    b.AppendLine("assist_ordered_attempt_chain dut (.*);\ninitial begin");
    int index = 0;
    foreach (var item in cases)
    {
        foreach (var p in inputPorts) b.AppendLine($"  {p.Name} = {p.Type.Width}'d{item.Inputs[p.Name]};");
        b.AppendLine("  #1;");
        var expected = Oracle(item.Inputs);
        foreach (var p in outputPorts)
            b.AppendLine($"  if ({p.Name} !== {p.Type.Width}'d{expected[p.Name]}) $fatal(1, \"case {index} {p.Name} expected {expected[p.Name]} got %0d\", {p.Name});");
        b.AppendLine($"  $display(\"PASS {item.Name}\");"); index++;
    }
    b.AppendLine("  $display(\"PASS all Assist attempt-chain scenarios\"); $finish; end endmodule");
    return b.ToString();
}

static async Task PublishCandidate(string runDirectory, string workspace)
{
    string run = Path.GetFullPath(runDirectory), target = Path.GetFullPath(workspace);
    if (!Directory.Exists(run)) throw new DirectoryNotFoundException(run);
    Directory.CreateDirectory(target);
    byte[] hwir = await File.ReadAllBytesAsync(Path.Combine(run, "assist-attempt-chain.hwir.json"));
    HwDesign sourceDesign = new CanonicalHwirSerializer().Deserialize(hwir);
    HwValidatedDesign design = new FailClosedHardwareIrValidator().Validate(sourceDesign).Validated
        ?? throw new InvalidDataException("Assist attempt-chain HWIR no longer validates.");
    byte[] Read(string name) => File.ReadAllBytes(Path.Combine(run, name));
    var supplements = new[]
    {
        ("assist-attempt-chain-mlir", StudioArtifactKind.Mlir, "assist-attempt-chain.mlir"),
        ("assist-attempt-chain-rtl", StudioArtifactKind.SystemVerilog, "assist-attempt-chain.sv"),
        ("assist-attempt-chain-testbench", StudioArtifactKind.SystemVerilog, "assist-attempt-chain.tb.sv"),
        ("assist-attempt-chain-receipt", StudioArtifactKind.CandidateRunProvenance, "assist-attempt-chain-rtl-receipt.json"),
        ("assist-attempt-chain-semantic", StudioArtifactKind.CandidateRunProvenance, "assist-attempt-chain-semantic.json"),
        ("assist-attempt-chain-source-inventory", StudioArtifactKind.SourceInventory, "assist-attempt-chain-source-inventory.json"),
        ("assist-attempt-chain-captured-sources", StudioArtifactKind.CapturedSources, "assist-attempt-chain-captured-sources.json"),
        ("assist-attempt-chain-source-pins", StudioArtifactKind.CandidateRunProvenance, "assist-attempt-chain-source-pins.json"),
        ("assist-attempt-chain-capture-index", StudioArtifactKind.CandidateRunProvenance, "assist-attempt-chain-source-capture-index.json"),
        ("assist-attempt-chain-project-graph", StudioArtifactKind.CandidateRunProvenance, "assist-attempt-chain-project-graph.json"),
        ("assist-attempt-chain-firtool-log", StudioArtifactKind.CandidateRunProvenance, "firtool-export.log"),
        ("assist-attempt-chain-compile-log", StudioArtifactKind.CandidateRunProvenance, "iverilog-compile.log"),
        ("assist-attempt-chain-simulation-log", StudioArtifactKind.CandidateRunProvenance, "vvp-simulation.log"),
        ("assist-attempt-chain-tool-hashes", StudioArtifactKind.CandidateRunProvenance, "tool-hashes.txt")
    }.Where(x => File.Exists(Path.Combine(run, x.Item3)) && new FileInfo(Path.Combine(run, x.Item3)).Length > 0).Select(x =>
    {
        byte[] bytes = Read(x.Item3);
        return new StudioSupplementArtifact(x.Item1, x.Item2, x.Item3, bytes,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
    }).ToArray();
    byte[] pins = Read("assist-attempt-chain-source-pins.json");
    var stages = new[]
    {
        new CompilationStageRecord(CompilationStage.SourceAdmission, CompilationStageStatus.NotRun, "One exact dirty-worktree Assist method is captured; project-wide source admission was not run."),
        new CompilationStageRecord(CompilationStage.HardwareIrValidation, CompilationStageStatus.Completed, "Source-pinned ordered four-rank retry/stop control passed fail-closed HWIR validation."),
        new CompilationStageRecord(CompilationStage.RtlEmission, CompilationStageStatus.Completed, "Pinned CIRCT emitted combinational RTL and Icarus ran the directed retry/stop scenarios."),
        new CompilationStageRecord(CompilationStage.IndependentVerification, CompilationStageStatus.Completed, "Independent imperative oracle matched 4,096 randomized plus 12 directed HWIR cases and directed Icarus RTL checks; outcomes remain sampled."),
        new CompilationStageRecord(CompilationStage.EvidencePromotion, CompilationStageStatus.NotRun, "StructuralOnly: no legality derivation, reservation-state cascade, cycle lifecycle, or Golden equivalence.")
    };
    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pins)).ToLowerInvariant();
    var request = new StudioCompilerPublicationRequest("hybridcpu-assist-attempt-chain-v1",
        "HybridCPU Assist ordered candidate retry/commit control", "assistchain" + design.CanonicalDigest[..16], design,
        new HwTargetDescription("CIRCT generic", "combinational", "circt-cli"), stages,
        Supplements: supplements, SourceDigest: hash);
    StudioPublicationReceipt receipt = await new StudioSnapshotPublisher().PublishAsync(request, target);
    Console.WriteLine(JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
    var opened = await new StudioWorkspaceLoader().OpenAsync(Path.Combine(target, "studio.project.json"));
    if (!opened.Success || opened.Value is null) throw new InvalidDataException("Studio API refused Assist attempt-chain publication: " + opened.Error);
    var session = opened.Value;
    var nav = GraphNavigationIndex.Create(session.Graph);
    if (session.Graph.Nodes.Count(n => n.Kind == CircuitWeave.Studio.Contracts.StudioGraphNodeKind.Module) != 1 ||
        session.Graph.Trust != CircuitWeave.Studio.Contracts.StudioDocumentTrust.StructuralOnly ||
        nav.GetRoots(0, 8).Items.Length == 0 || nav.GetChildren(nav.TopModuleId, 0, 16).Items.Length == 0)
        throw new InvalidDataException("Assist attempt-chain Studio hierarchy or trust check failed.");
    var search = SourceInventorySearch.Find(session.SourceInventory?.Symbols ?? Array.Empty<StudioSourceInventorySymbol>(), "TryInjectAssistCandidates");
    var mapping = session.SourceMap?.Mappings.FirstOrDefault(m => m.Source.Commit == "1bfe41a49cf11c80fa6c6aee87accb54b78b331f" &&
        m.Source.RelativePath.EndsWith("MicroOpScheduler.Assist.cs", StringComparison.Ordinal));
    if (search.Count == 0 || mapping is null || !nav.TryGetNode(mapping.GraphNodeId, out _) ||
        string.IsNullOrWhiteSpace(CapturedNodeSource.Excerpt(session, mapping.Source)))
        throw new InvalidDataException("Assist source search, selection or captured excerpt did not resolve.");
    if (session.Artifacts.Count < supplements.Length || session.Artifacts.Any(a => !a.HasDeclaredDigest ||
        a.Artifact.Sha256 != a.ActualSha256 || a.Artifact.ByteLength != a.ActualSize))
        throw new InvalidDataException("Assist attempt-chain artifact digest/length verification failed.");
    Console.WriteLine("Assist attempt-chain Studio diagnostics: " + JsonSerializer.Serialize(session.Diagnostics));
    Console.WriteLine($"Assist attempt-chain Studio API PASS: modules=1 nodes={session.Graph.Nodes.Count} edges={session.Graph.Edges.Count} artifacts={session.Artifacts.Count}; hierarchy/search/source excerpt passed; trust={session.Graph.Trust}; diagnostics={session.Diagnostics.Count}.");
}

static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

