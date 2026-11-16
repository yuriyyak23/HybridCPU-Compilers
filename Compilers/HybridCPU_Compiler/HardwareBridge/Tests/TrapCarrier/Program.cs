using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using CircuitWeave.Orchestration;
using CircuitWeave.Visualization;
using CircuitWeave.Visualization.Publishing;
using HybridCPU.Compiler.HardwareBridge.Hwir;

if (args.Length != 6 || args[0] != "prepare")
    throw new ArgumentException("Usage: TrapCarrier.Runner prepare <active-golden-runtime-root> <new-output-directory> <source-inventory.json> <captured-sources.json> <trap-carrier-rtl-root>");

string runtimeRoot = Path.GetFullPath(args[1]);
string output = Path.GetFullPath(args[2]);
string inventoryPath = Path.GetFullPath(args[3]);
string capturePath = Path.GetFullPath(args[4]);
string rtlRoot = Path.GetFullPath(args[5]);
if (!Directory.Exists(runtimeRoot) || !Directory.Exists(output) ||
    Directory.EnumerateFileSystemEntries(output).Any())
    throw new InvalidOperationException("Runtime input must exist and output must be a new, empty directory.");
if (!File.Exists(inventoryPath) || !File.Exists(capturePath) || !Directory.Exists(rtlRoot))
    throw new InvalidOperationException("Source inventory, captured sources, and completed RTL run are required.");

const string activeCommit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
const string goldenCommit = "6946ccab9d888f7dae0a4d8e18480f436138acd9";
const string iseRoot = @"C:\Users\Yuriy\Desktop\HybridCPU ISE";
using JsonDocument comparisonDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(runtimeRoot, "comparison.json")));
JsonElement comparison = comparisonDoc.RootElement;
if (comparison.GetProperty("schema").GetString() != "circuitweave.trap-carrier-runtime-differential/v1" ||
    comparison.GetProperty("activeModelCommit").GetString() != activeCommit ||
    comparison.GetProperty("activeModelGitHead").GetString() != activeCommit ||
    comparison.GetProperty("goldenCommit").GetString() != goldenCommit ||
    comparison.GetProperty("eventCount").GetInt32() != 8 ||
    !comparison.GetProperty("tracesEqual").GetBoolean())
    throw new InvalidDataException("Trap-carrier active/Golden runtime receipt is not the expected exact differential.");
string goldenRoot = Path.Combine(
    comparison.GetProperty("isolatedBuildRoot").GetString() ?? throw new InvalidDataException("Missing isolated Golden root."),
    "golden", "source");

string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
byte[] ReadSource(string root, string relative) => File.ReadAllBytes(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

string[] hwirPaths =
[
    "HybridCPU_ISE/CloseToHSL/Core/Pipeline/MicroOps/Types/MicroOp.IO.cs",
    "HybridCPU_ISE/CloseToHSL/Core/Pipeline/PipelineEvent.cs",
    "HybridCPU_ISE/CloseToHSL/Core/Pipeline/PipelineFsmEventHandler.cs",
    "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.PipelineExecution.Retire.cs",
    "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Stages/Issue/CPU_Core.Pipeline.cs",
    "HybridCPU_ISE/CloseToHSL/Core/State/LiveCpuStateAdapter.cs",
    "HybridCPU_ISE/CloseToHSL/Core/Architecture/State/Pipeline/PipelineState.cs"
];
// Validate each path against the run receipt before invoking the compiler's strict pins.
JsonElement[] sourcePins = comparison.GetProperty("sourcePins").EnumerateArray().ToArray();
var expectedGoldenBlobs = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["HybridCPU_ISE/CloseToHSL/Core/Pipeline/MicroOps/Types/MicroOp.IO.cs"] = "357eeaf3c1b8e23727201b7d7356fbe787d371c3",
    ["HybridCPU_ISE/CloseToHSL/Core/Pipeline/PipelineEvent.cs"] = "75b90b36ffa424ff134252fec5297561b6ee0813",
    ["HybridCPU_ISE/CloseToHSL/Core/Pipeline/PipelineFsmEventHandler.cs"] = "5ae5434fcad260fc2bdb635f04f21cbe126fed98",
    ["HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.PipelineExecution.Retire.cs"] = "3bb2f1687532c136714f3c08b81355d80fd8289a",
    ["HybridCPU_ISE/CloseToHSL/Core/Pipeline/Stages/Issue/CPU_Core.Pipeline.cs"] = "eb94983349a3904f5b822df548890962f4483f7d",
    ["HybridCPU_ISE/CloseToHSL/Core/State/LiveCpuStateAdapter.cs"] = "2f9274a442b6be6b10560c6e3c806bf5add59b31",
    ["HybridCPU_ISE/CloseToHSL/Core/Architecture/State/Pipeline/PipelineState.cs"] = "95ad3b3557b51dd706e7574ab07f9870d594d095"
};
foreach (string relative in hwirPaths)
{
    JsonElement pin = sourcePins.SingleOrDefault(p => p.GetProperty("path").GetString() == relative);
    byte[] active = ReadSource(iseRoot, relative);
    byte[] golden = ReadSource(goldenRoot, relative);
    if (pin.ValueKind != JsonValueKind.Undefined &&
        (Sha(active) != pin.GetProperty("activeSha256").GetString() ||
         Sha(golden) != pin.GetProperty("goldenMaterializedSha256").GetString() ||
         pin.GetProperty("goldenGitBlob").GetString() != expectedGoldenBlobs[relative]))
        throw new InvalidDataException("Runtime evidence source bytes are stale: " + relative);
}

byte[] Active(string relative) => ReadSource(iseRoot, relative);
byte[] Golden(string relative) => ReadSource(goldenRoot, relative);
string micro = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/MicroOps/Types/MicroOp.IO.cs";
string evt = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/PipelineEvent.cs";
string handler = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/PipelineFsmEventHandler.cs";
string retire = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.PipelineExecution.Retire.cs";
string pipeline = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Stages/Issue/CPU_Core.Pipeline.cs";
string adapter = "HybridCPU_ISE/CloseToHSL/Core/State/LiveCpuStateAdapter.cs";
var toolchain = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
HwValidatedDesign candidate = BridgeHwirTrapEntryProjection.Compile(
    Active(micro), Active(evt), Active(handler), Active(retire), Active(pipeline), Active(adapter),
    Golden(micro), Golden(evt), Golden(handler), Golden(retire), Golden(pipeline), Golden(adapter),
    Active(hwirPaths[^1]), Golden(hwirPaths[^1]), toolchain);
byte[] hwir = new CanonicalHwirSerializer().Serialize(candidate);
WriteNew("trap-entry-projection.hwir.json", hwir);
HardwareMlirModule mlir = new HierarchicalCirctMlirLowerer().LowerToMlir(candidate,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>()));
WriteNew("trap-entry-projection.mlir", Encoding.UTF8.GetBytes(mlir.Text));

using JsonDocument activeTraceDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(runtimeRoot, "active-trace.json")));
JsonElement[] activeEvents = activeTraceDoc.RootElement.EnumerateArray()
    .Where(x => x.GetProperty("stage").GetString() == "trap-retire-boundary").ToArray();
if (activeEvents.Length != 2) throw new InvalidDataException("Expected one retire-boundary observation for each opcode.");
List<object> vectors = [];
foreach (JsonElement item in activeEvents)
{
    string opcode = item.GetProperty("opcode").GetInt32() == 213 ? "VGATHER" : "VSCATTER";
    BigInteger mepc = item.GetProperty("mepc").GetUInt64();
    BigInteger mcause = item.GetProperty("mcause").GetUInt64();
    BigInteger mtval = item.GetProperty("mtval").GetUInt64();
    BigInteger pc = item.GetProperty("committedPc").GetUInt64();
    if (mepc != 0x7c00 || mcause != 2 || mtval != 0 || pc != 0x1800 ||
        item.GetProperty("activeLivePc").GetUInt64() != 0x1800 ||
        item.GetProperty("assistInvalidationReason").GetString() != "Trap" ||
        item.GetProperty("coreReplayActive").GetBoolean() ||
        item.GetProperty("coreReplayReason").GetString() != "SerializingEvent")
        throw new InvalidDataException("Runtime trap observation does not match the independently stated source equation: " + opcode);
    vectors.Add(Vector("runtime-trap-retire-" + opcode,
        Inputs(1, 1, 2, 0, 0x7c00, 0x1800, 0, 0, 0, 0x7c00, 1),
        Outputs(mepc, mcause, mtval, pc, 1, 1, 1, 1, 0)));
}

vectors.Add(Vector("equation-invalid-event-holds-state",
    Inputs(0, 1, 0, 0xabc, 0x1234, 0x9876, 0x111, 0x222, 0x333, 0x444, 1),
    Outputs(0x111, 0x222, 0x333, 0x444, 0, 0, 0, 0, 1)));
vectors.Add(Vector("equation-zero-cause-normalizes-illegal-instruction",
    Inputs(1, 1, 0, 0x55, 0x777, 0x888, 0x11, 0x22, 0x33, 0x44, 1),
    Outputs(0x777, 2, 0, 0x888, 1, 1, 1, 1, 0)));

byte[] vectorBytes = Encoding.UTF8.GetBytes(string.Join("\n", vectors.Select(v => JsonSerializer.Serialize(v))) + "\n");
WriteNew("trap-entry-event-vectors.jsonl", vectorBytes);
foreach (string name in new[] { "comparison.json", "active-trace.json", "golden-trace.json", "active-test.log", "golden-test.log" })
    WriteNew(name, File.ReadAllBytes(Path.Combine(runtimeRoot, name)));
WriteNew("trap-carrier-runtime-receipt.json", File.ReadAllBytes(Path.Combine(runtimeRoot, "comparison.json")));
var receipt = new
{
    schema = "circuitweave.trap-entry-retire-candidate/v1",
    status = "SOURCE_PINNED_ACTIVE_GOLDEN_RUNTIME_TO_HWIR_EQUATION_CANDIDATE",
    activeModelHead = activeCommit,
    goldenCommit,
    runtimeComparisonSha256 = Sha(File.ReadAllBytes(Path.Combine(runtimeRoot, "comparison.json"))),
    runtimeTraceSha256 = comparison.GetProperty("activeTraceSha256").GetString(),
    sourcePaths = hwirPaths,
    sourcePins = hwirPaths.Select(path => new
    {
        path,
        activeSha256 = Sha(Active(path)),
        goldenMaterializedSha256 = Sha(Golden(path)),
        goldenGitBlob = expectedGoldenBlobs[path],
        activeGoldenRuntimeReceiptPin = sourcePins.Any(p => p.GetProperty("path").GetString() == path)
    }).ToArray(),
    eventCount = vectors.Count,
    runtimeDerivedEvents = activeEvents.Length,
    equationCases = 2,
    hwirSha256 = Sha(hwir),
    mlirSha256 = mlir.ContentDigest,
    scope = "One selected TrapEntryEvent software retire boundary. No clock/reset/stall timing, concurrent-event priority, multi-VT arbitration, all-trap-class or CPU-top equivalence.",
    trust = "StructuralOnly"
};
WriteNew("trap-entry-projection-receipt.json", JsonSerializer.SerializeToUtf8Bytes(receipt, new JsonSerializerOptions { WriteIndented = true }));
byte[] inventoryBytes = File.ReadAllBytes(inventoryPath);
byte[] capturedSourcesBytes = File.ReadAllBytes(capturePath);
byte[] rtlBytes = File.ReadAllBytes(Path.Combine(rtlRoot, "trap_event_projection.sv"));
byte[] rtlReceiptBytes = File.ReadAllBytes(Path.Combine(rtlRoot, "trap-carrier-rtl-receipt.json"));
byte[] rtlLogBytes = File.ReadAllBytes(Path.Combine(rtlRoot, "trap-carrier-rtl.log"));
byte[] rtlVectorsBytes = File.ReadAllBytes(Path.Combine(rtlRoot, "trap-carrier-rtl-vectors.hex"));
byte[] rtlTestbenchBytes = File.ReadAllBytes(Path.Combine(rtlRoot, "trap-carrier-rtl.tb.sv"));
byte[] observationBytes = File.ReadAllBytes(Path.Combine(rtlRoot, "trap-carrier-rtl-observations.json"));
using (JsonDocument inventoryDoc = JsonDocument.Parse(inventoryBytes))
using (JsonDocument captureDoc = JsonDocument.Parse(capturedSourcesBytes))
{
    if (inventoryDoc.RootElement.GetProperty("schema").GetString() != "circuitweave.source-coverage-index/v1" ||
        inventoryDoc.RootElement.GetProperty("disposition").GetString() != "incomplete-unqualified" ||
        captureDoc.RootElement.GetProperty("schema").GetString() != "circuitweave.captured-sources/v1" ||
        captureDoc.RootElement.GetProperty("inventorySha256").GetString() != Sha(inventoryBytes) ||
        captureDoc.RootElement.GetProperty("files").GetArrayLength() != 1135)
        throw new InvalidDataException("Full project inventory/capture identity or declared incomplete disposition changed.");
}
StudioSupplementArtifact[] supplements =
[
    Supplement("source-inventory", StudioArtifactKind.SourceInventory, "source-inventory.json", inventoryBytes),
    Supplement("captured-sources", StudioArtifactKind.CapturedSources, "captured-sources.json", capturedSourcesBytes),
    Supplement("trap-entry-mlir", StudioArtifactKind.Mlir, "trap-entry-projection.mlir", Encoding.UTF8.GetBytes(mlir.Text)),
    Supplement("trap-entry-hwir", StudioArtifactKind.CandidateRunProvenance, "trap-entry-projection.hwir.json", hwir),
    Supplement("trap-entry-vectors", StudioArtifactKind.CandidateRunProvenance, "trap-entry-event-vectors.jsonl", vectorBytes),
    Supplement("trap-entry-runtime-comparison", StudioArtifactKind.CandidateRunProvenance, "trap-carrier-runtime-comparison.json", File.ReadAllBytes(Path.Combine(runtimeRoot, "comparison.json"))),
    Supplement("trap-entry-runtime-active-trace", StudioArtifactKind.CandidateRunProvenance, "trap-carrier-active-trace.json", File.ReadAllBytes(Path.Combine(runtimeRoot, "active-trace.json"))),
    Supplement("trap-entry-runtime-golden-trace", StudioArtifactKind.CandidateRunProvenance, "trap-carrier-golden-trace.json", File.ReadAllBytes(Path.Combine(runtimeRoot, "golden-trace.json"))),
    Supplement("trap-entry-rtl-receipt", StudioArtifactKind.CandidateRunProvenance, "trap-carrier-rtl-receipt.json", rtlReceiptBytes),
    Supplement("trap-entry-rtl-observations", StudioArtifactKind.CandidateRunProvenance, "trap-carrier-rtl-observations.json", observationBytes),
    Supplement("trap-entry-rtl-log", StudioArtifactKind.CandidateRunProvenance, "trap-carrier-rtl.log", rtlLogBytes),
    Supplement("trap-entry-rtl-vectors", StudioArtifactKind.CandidateRunProvenance, "trap-carrier-rtl-vectors.hex", rtlVectorsBytes),
    Supplement("trap-entry-rtl-testbench", StudioArtifactKind.CandidateRunProvenance, "trap-carrier-rtl.tb.sv", rtlTestbenchBytes),
    Supplement("trap-entry-systemverilog", StudioArtifactKind.SystemVerilog, "trap-entry-projection.sv", rtlBytes)
];
CompilationStageRecord[] stages = Enum.GetValues<CompilationStage>().Select(stage => new CompilationStageRecord(stage,
    stage switch
    {
        CompilationStage.HardwareIrValidation or CompilationStage.RtlEmission or CompilationStage.IndependentVerification => CompilationStageStatus.Completed,
        _ => CompilationStageStatus.NotRun
    },
    stage switch
    {
        CompilationStage.HardwareIrValidation => "Source-pinned five-module HWIR composition passed fail-closed validation.",
        CompilationStage.RtlEmission => "CIRCT emitted the composed combinational top-level SystemVerilog.",
        CompilationStage.IndependentVerification => "Icarus candidate equation vectors passed; no sequential or active/Golden RTL equivalence.",
        _ => "Not run for this bounded source-projection candidate."
    })).ToArray();
StudioCompilerPublicationRequest publication = new("hybridcpu-trap-entry-retire",
    "HybridCPU TrapEntryEvent retire boundary candidate (STRUCTURAL ONLY)",
    "trap-entry-v191-" + candidate.CanonicalDigest[..12], candidate,
    new HwTargetDescription("combinational", "generic", "circt", "HWIR comb candidate"), stages,
    Supplements: supplements, SourceDigest: Sha(inventoryBytes));
StudioPublicationReceipt published = await new StudioSnapshotPublisher().PublishAsync(publication, output);
StudioSupplementArtifact Supplement(string id, StudioArtifactKind kind, string name, byte[] bytes) =>
    new(id, kind, name, bytes, Sha(bytes));
Console.WriteLine($"TrapEntryEvent HWIR prepared: {candidate.Design.Modules.Count} module, {vectors.Count} equation vectors ({activeEvents.Length} runtime-derived), HWIR {candidate.CanonicalDigest}");
Console.WriteLine($"Studio candidate manifest published: {published.StableManifestPath}; snapshot={published.SnapshotDigest}; status={published.Status}");

Dictionary<string, BigInteger> Inputs(int apply, int serial, ulong cause, ulong address, ulong faultPc, ulong mtvec,
    ulong mepc, ulong mcause, ulong mtval, ulong pc, int replayActive) => new()
{
    ["execute_attempt"] = 1, ["trap_microop_present"] = apply, ["retire_event_apply"] = 1,
    ["faulting_pc"] = faultPc, ["mtvec"] = mtvec,
    ["mepc_current"] = mepc, ["mcause_current"] = mcause, ["mtval_current"] = mtval,
    ["pc_current"] = pc, ["pipeline_state_current"] = 0, ["replay_phase_active_current"] = replayActive
};

Dictionary<string, BigInteger> Outputs(BigInteger mepc, BigInteger mcause, BigInteger mtval, BigInteger pc,
    int applied, int flush, int assist, int replayInvalidate, int replayActiveNext) => new()
{
    ["mepc_next"] = mepc, ["mcause_next"] = mcause, ["mtval_next"] = mtval, ["pc_next"] = pc,
    ["pipeline_flush"] = flush,
    ["assist_invalidation_trap"] = assist, ["replay_phase_invalidate"] = replayInvalidate,
    ["replay_phase_active_next"] = replayActiveNext, ["event_apply"] = applied,
    ["event_cause_code"] = 2,
    ["event_fault_address"] = BigInteger.Zero, ["event_has_fault_address"] = BigInteger.Zero,
    ["event_valid"] = applied, ["full_serial_boundary"] = applied,
    ["pipeline_state_next"] = BigInteger.Zero
};

object Vector(string scenario, Dictionary<string, BigInteger> inputs, Dictionary<string, BigInteger> expected) => new
{
    scenario,
    origin = scenario.StartsWith("runtime-", StringComparison.Ordinal) ? "active-and-pinned-Golden-runtime-trace" : "independent-source-equation-case",
    inputs = inputs.ToDictionary(p => p.Key, p => checked((ulong)p.Value), StringComparer.Ordinal),
    expected = expected.ToDictionary(p => p.Key, p => checked((ulong)p.Value), StringComparer.Ordinal)
};

void WriteNew(string name, byte[] bytes)
{
    using var stream = new FileStream(Path.Combine(output, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
    stream.Write(bytes);
}


