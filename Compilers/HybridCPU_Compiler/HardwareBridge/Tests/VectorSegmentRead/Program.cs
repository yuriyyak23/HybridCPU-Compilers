using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CircuitWeave.Contracts;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.HWIR;
using CircuitWeave.Orchestration;
using CircuitWeave.Visualization;
using CircuitWeave.Visualization.Publishing;
using HybridCPU.Compiler.HardwareBridge.Hwir;

const string modelRoot = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE";
const string goldenCommit = "6946ccab9d888f7dae0a4d8e18480f436138acd9";
const string activeModelCommit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
string gitDir = Path.Combine(Path.GetTempPath(), "cw-golden-6946ccab-20261114");
string activePath = Path.Combine(modelRoot, "CloseToHSL", "Memory", "Timing", "MemoryCycleController.cs");
byte[] active = File.ReadAllBytes(activePath);
byte[] golden = ReadGoldenBlob(gitDir, goldenCommit, BridgeHwirVectorSegmentReadAdmission.RelativePath);
var toolchain = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
HwValidatedDesign design = BridgeHwirVectorSegmentReadAdmission.Compile(active, golden, activeModelCommit, toolchain, out var methodEvidence);
byte[] hwir = new CanonicalHwirSerializer().Serialize(design);
HwValidatedDesign lifecycle = BridgeHwirVectorSegmentReadAdmission.CompileLifecycle(active, golden, activeModelCommit, toolchain, out _);
byte[] lifecycleHwir = new CanonicalHwirSerializer().Serialize(lifecycle);
string lifecycleMlir = new HierarchicalCirctMlirLowerer().LowerToMlir(
    lifecycle,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>())).Text;
string oracleVectorsPath = Path.Combine(Path.GetTempPath(), "vector-segment-read-rtl-vectors.jsonl");
var oracleResult = VectorSegmentReadOracle.Crosscheck(lifecycle.Design.Modules.Single(), seed: 0x5eed182, maxEvents: 4096, oracleVectorsPath);

bool activeMutationRejected = RejectMutation(active, golden, goldenCommit, toolchain);
bool goldenMutationRejected = RejectMutation(active, golden, goldenCommit, toolchain, mutateGolden: true);
if (!activeMutationRejected || !goldenMutationRejected)
    throw new InvalidDataException("Active or Golden one-byte mutation was accepted.");

string output = Path.Combine(@"C:\Users\Yuriy\Desktop\CircuitWeave-main", "artifacts", "processor-20261114", "vector-segment-read-v1");
Directory.CreateDirectory(output);
File.WriteAllBytes(Path.Combine(output, "vector-segment-read-admission.hwir.json"), hwir);
File.WriteAllBytes(Path.Combine(output, "vector-segment-read-lifecycle.hwir.json"), lifecycleHwir);
File.WriteAllText(Path.Combine(output, "vector-segment-read-lifecycle.mlir"), lifecycleMlir, new UTF8Encoding(false));
File.Copy(oracleVectorsPath, Path.Combine(output, "vector-segment-read-rtl-vectors.jsonl"), overwrite: true);
string rtlPath = Path.Combine(output, "vector-segment-read-lifecycle.sv");
string rtlReceiptPath = Path.Combine(output, "vector-segment-read-rtl-receipt.json");
bool rtlVerified = File.Exists(rtlPath) && File.Exists(rtlReceiptPath);
byte[]? rtlBytes = null;
byte[]? rtlReceiptBytes = null;
if (rtlVerified)
{
    rtlBytes = File.ReadAllBytes(rtlPath);
    rtlReceiptBytes = File.ReadAllBytes(rtlReceiptPath);
    using JsonDocument rtlReceipt = JsonDocument.Parse(rtlReceiptBytes);
    JsonElement root = rtlReceipt.RootElement;
    if (root.GetProperty("status").GetString() != "PASS_STRUCTURAL_LIFECYCLE_RTL_VS_INDEPENDENT_ORACLE" ||
        root.GetProperty("vectors").GetInt32() != oracleResult.Vectors ||
        root.GetProperty("artifacts").GetProperty("vector-segment-read-lifecycle.mlir").GetString() != Sha(Encoding.UTF8.GetBytes(lifecycleMlir)) ||
        root.GetProperty("artifacts").GetProperty("vector-segment-read-lifecycle.hwir.json").GetString() != Sha(lifecycleHwir) ||
        root.GetProperty("artifacts").GetProperty("vector-segment-read-lifecycle.sv").GetString() != Sha(rtlBytes))
        throw new InvalidDataException("RTL receipt is stale or not bound to this lifecycle HWIR/MLIR and vector trace.");
}
string runtimeRoot = Environment.GetEnvironmentVariable("CW_VECTOR_SEGMENT_RUNTIME_ROOT") ??
    Path.Combine(Path.GetDirectoryName(output)!, "vector-segment-runtime-rf106-v2");
string runtimeComparisonPath = Path.Combine(runtimeRoot, "comparison.json");
bool runtimeTraceVerified = File.Exists(runtimeComparisonPath);
byte[]? runtimeComparisonBytes = null;
JsonElement runtimeComparison = default;
if (runtimeTraceVerified)
{
    runtimeComparisonBytes = File.ReadAllBytes(runtimeComparisonPath);
    using JsonDocument comparisonDocument = JsonDocument.Parse(runtimeComparisonBytes);
    runtimeComparison = comparisonDocument.RootElement.Clone();
    string activeTracePath = Path.Combine(runtimeRoot, "active-trace.json");
    string goldenTracePath = Path.Combine(runtimeRoot, "golden-trace.json");
    string activeLogPath = Path.Combine(runtimeRoot, "active-test.log");
    string goldenLogPath = Path.Combine(runtimeRoot, "golden-test.log");
    JsonElement comparison = runtimeComparison;
    if (comparison.GetProperty("activeModelGitHead").GetString() != activeModelCommit ||
        comparison.GetProperty("activeModelCommit").GetString() != activeModelCommit ||
        comparison.GetProperty("goldenCommit").GetString() != goldenCommit ||
        comparison.GetProperty("activeControllerSourceSha256").GetString() != Sha(active) ||
        comparison.GetProperty("goldenControllerGitBlob").GetString() != "74cd0f7bc7d06cd1715c3b78516d23a087197f4b" ||
        comparison.GetProperty("tracesEqual").GetBoolean() != true ||
        comparison.GetProperty("activeTraceSha256").GetString() != comparison.GetProperty("goldenTraceSha256").GetString() ||
        comparison.GetProperty("activeTraceSha256").GetString() != Sha(File.ReadAllBytes(activeTracePath)) ||
        comparison.GetProperty("goldenTraceSha256").GetString() != Sha(File.ReadAllBytes(goldenTracePath)) ||
        comparison.GetProperty("activeTraceTestLogSha256").GetString() != Sha(File.ReadAllBytes(activeLogPath)) ||
        comparison.GetProperty("goldenTraceTestLogSha256").GetString() != Sha(File.ReadAllBytes(goldenLogPath)) ||
        comparison.GetProperty("eventCount").GetInt32() != 46 ||
        comparison.GetProperty("scenarioCounts").GetProperty("fifo").GetInt32() != 13 ||
        comparison.GetProperty("scenarioCounts").GetProperty("capacityCancel").GetInt32() != 30 ||
        comparison.GetProperty("scenarioCounts").GetProperty("pipelineFlush").GetInt32() != 3)
        throw new InvalidDataException("Active/Golden Rf106 runtime trace receipt is stale, incomplete, or mismatched. Golden source bytes are tied to the pinned Git blob id; archive extraction may normalize line endings.");
}
var receipt = new
{
    schema = "circuitweave.vector-segment-read-admission-structural-receipt/v1",
    status = runtimeTraceVerified
        ? "STRUCTURAL_ONLY_ADMISSION_AND_LIFECYCLE_HWIR_WITH_BOUNDED_ACTIVE_GOLDEN_RUNTIME_TRACE;_NO_RTL_EQUIVALENCE"
        : "STRUCTURAL_ONLY_ADMISSION_AND_LIFECYCLE_HWIR_NO_RTL_OR_RUNTIME_EQUIVALENCE",
    activeSourceSha256 = Sha(active),
    goldenCommit,
    activeModelCommit,
    goldenSourcePath = BridgeHwirVectorSegmentReadAdmission.RelativePath,
    goldenSourceSha256 = Sha(golden),
    designDigest = design.CanonicalDigest,
    hwirSha256 = Sha(hwir),
    module = design.Design.Modules.Single().Name,
    lifecycleDesignDigest = lifecycle.CanonicalDigest,
    lifecycleHwirSha256 = Sha(lifecycleHwir),
    lifecycleModule = lifecycle.Design.Modules.Single().Name,
    independentOracle = oracleResult,
    activeGoldenRuntimeTrace = runtimeTraceVerified ? runtimeComparison : (JsonElement?)null,
    sourceGuardMethodEvidence = methodEvidence,
    activeMutationRejected,
    goldenMutationRejected,
    sourceScope = new[]
    {
        "HWIR models public vector-segment admission only: positive signed Int32 size and per-class capacity 8.",
        "Lifecycle source guards cover enqueue, one read service per controller edge, next-cycle publication, completion take and cancellation release in active and Golden sources.",
        "Lifecycle HWIR is an explicit-state one-event transition: accept, edge service, next-edge publish, take and cancel.",
        "Lifecycle transition equations are crosschecked against an independent software state-machine oracle.",
        runtimeTraceVerified ? "A bounded 46-event real Rf106 trace matches between the exact active checkout and pinned Golden; it covers cross-class FIFO service, vector capacity backpressure, cancellation/capacity release and pipeline flush. This does not establish RTL equivalence." : "No active/Golden source-runtime differential.",
        "No arbitrary vector segment payload or RAM data is represented."
    },
    open = new[]
    {
        runtimeTraceVerified ? "Runtime differential is limited to the recorded 46-event Rf106 scenario; reset, stall, replay, data-dependent faults and broader CPU paths remain unverified." : "No active/Golden source-runtime differential; the independent oracle validates generated HWIR equations only.",
        rtlVerified ? "No clocked cycle simulation; RTL is a combinational event-transition function." : "No generated RTL or cycle simulation.",
        "No clock/reset physical controller, RAM timing/ports, arbitration or bank conflict model.",
        "Translation, vector register writeback, retire/trap and Processor integration remain outside this candidate."
    }
};
byte[] receiptBytes = JsonSerializer.SerializeToUtf8Bytes(receipt, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllBytes(Path.Combine(output, "receipt.json"), receiptBytes);
var combinedDesign = new HwDesign("hwir/v0.1", lifecycle.Design.TopModule,
    [design.Design.Modules.Single(), lifecycle.Design.Modules.Single()], toolchain, []);
var combinedResult = new FailClosedHardwareIrValidator().Validate(combinedDesign);
HwValidatedDesign combined = combinedResult.Validated ?? throw new InvalidDataException("Combined vector segment design refused: " + string.Join("; ", combinedResult.Diagnostics.Select(d => d.Code + " " + d.Message)));
string semanticReportPath = Path.Combine(@"C:\Users\Yuriy\Desktop\CircuitWeave-main", "artifacts", "processor-20261114", "inventory-refresh-v1", "semantic-report-v2-fresh.json");
byte[] semanticReport = File.ReadAllBytes(semanticReportPath);
byte[] projectGraph = File.ReadAllBytes(Path.Combine(@"C:\Users\Yuriy\Desktop\CircuitWeave-main", "artifacts", "processor-20261114", "inventory-refresh-v1", "prepared-v1", "hybridcpu-debug-project-graph-v3.json"));
string sourceRelative = "HybridCPU_ISE/CloseToHSL/Memory/Timing/MemoryCycleController.cs";
string sourceSymbol = "HybridCPU_ISE.CloseToHSL.Memory.Timing.MemoryCycleController";
string[] methodNames = ["TryAcceptVectorSegmentLoad", "TryAcceptRead", "TickOneCycle", "TryTakeCompletion", "TryCancel"];
int[] methodLines = [333, 512, 748, 577, 601];
var inventoryMembers = methodNames.Select((name, i) => new
{
    id = "M:" + sourceSymbol + "." + name + "()",
    kind = "Method",
    locations = new[] { new { file = activePath, startLine = methodEvidence.Single(m => m.Name == name).ActiveContractMatches ? methodLines[i] : 1, startColumn = name == "TryAcceptVectorSegmentLoad" ? 34 : 5 } },
    blockingReason = "Captured source method only; signatures/dependencies are not a complete Processor symbol inventory."
}).ToArray();
var captureIndex = new
{
    schema = "circuitweave.source-capture-index/v1",
    status = "one-listed-active-source-file-byte-hash-checked",
    baseCommit = activeModelCommit,
    scope = "one MemoryCycleController.cs active source file only; no root project closure claim",
    files = new[] { new { path = activePath, relativePath = sourceRelative, sha256 = Sha(active), length = active.LongLength } }
};
byte[] captureIndexBytes = JsonSerializer.SerializeToUtf8Bytes(captureIndex, new JsonSerializerOptions { WriteIndented = true });
string captureIndexSha = Sha(captureIndexBytes);
var sourceInventory = new
{
    schema = "circuitweave.source-coverage-index/v1",
    disposition = "incomplete-unqualified",
    project = @"HybridCPU_ISE\HybridCPU_ISE.csproj",
    configuration = "Debug",
    semanticReportSha256 = Sha(semanticReport),
    symbols = new[]
    {
        new
        {
            symbol = sourceSymbol, assembly = "HybridCPU_ISE", kind = "Class", memberCount = methodNames.Length,
            blockingReason = "Excerpt-only inventory entry for the vector-segment lifecycle slice; not complete type/member coverage.",
            declarations = new[] { new { location = new { file = activePath, startLine = 333, startColumn = 34 }, memberIds = inventoryMembers.Select(m => m.id).ToArray() } },
            members = inventoryMembers
        }
    },
    captureVerification = new
    {
        schema = "circuitweave.source-capture-check/v1",
        status = "producer-reported-listed-inputs-match",
        semanticReportSha256 = Sha(semanticReport),
        sourceCaptureSha256 = captureIndexSha,
        projectGraphSha256 = Sha(projectGraph),
        scope = "current-byte-verified capture of the one listed MemoryCycleController.cs file; project graph is the separately captured Debug inventory graph and is not a claim of complete Csc source capture",
        checkedFiles = 1,
        rootProject = "not-captured"
    },
    capturedSourceFiles = new[] { new { path = activePath, sha256 = Sha(active), length = active.LongLength } }
};
byte[] sourceInventoryBytes = JsonSerializer.SerializeToUtf8Bytes(sourceInventory, new JsonSerializerOptions { WriteIndented = true });
byte[] capturedSourcesBytes = JsonSerializer.SerializeToUtf8Bytes(new
{
    schema = "circuitweave.captured-sources/v1",
    inventorySha256 = Sha(sourceInventoryBytes),
    files = new[] { new { path = activePath, bytes = Convert.ToBase64String(active) } }
}, new JsonSerializerOptions { WriteIndented = true });
StudioSupplementArtifact Supplement(string id, StudioArtifactKind kind, string file, byte[] bytes) => new(id, kind, file, bytes, Sha(bytes));
var supplements = new List<StudioSupplementArtifact>
{
    Supplement("vector-segment-source-inventory", StudioArtifactKind.SourceInventory, "vector-segment-source-inventory.json", sourceInventoryBytes),
    Supplement("vector-segment-captured-sources", StudioArtifactKind.CapturedSources, "vector-segment-captured-sources.json", capturedSourcesBytes),
    Supplement("vector-segment-capture-index", StudioArtifactKind.CandidateRunProvenance, "vector-segment-capture-index.json", captureIndexBytes),
    Supplement("vector-segment-semantic-report-reference", StudioArtifactKind.CandidateRunProvenance, "vector-segment-semantic-report-reference.json", JsonSerializer.SerializeToUtf8Bytes(new { path = semanticReportPath, sha256 = Sha(semanticReport), limitation = "Referenced report is an incomplete-unqualified Roslyn replay, not full semantic closure." })),
    Supplement("vector-segment-independent-oracle", StudioArtifactKind.CandidateRunProvenance, "vector-segment-receipt.json", receiptBytes),
    Supplement("vector-segment-admission-hwir", StudioArtifactKind.CandidateRunProvenance, "vector-segment-read-admission.hwir.json", hwir),
    Supplement("vector-segment-lifecycle-hwir", StudioArtifactKind.CandidateRunProvenance, "vector-segment-read-lifecycle.hwir.json", lifecycleHwir)
};
if (rtlVerified)
{
    supplements.Add(Supplement("vector-segment-lifecycle-mlir", StudioArtifactKind.Mlir, "vector-segment-read-lifecycle.mlir", Encoding.UTF8.GetBytes(lifecycleMlir)));
    supplements.Add(Supplement("vector-segment-lifecycle-rtl", StudioArtifactKind.SystemVerilog, "vector-segment-read-lifecycle.sv", rtlBytes!));
    supplements.Add(Supplement("vector-segment-rtl-vectors", StudioArtifactKind.CandidateRunProvenance, "vector-segment-read-rtl-vectors.jsonl", File.ReadAllBytes(Path.Combine(output, "vector-segment-read-rtl-vectors.jsonl"))));
    supplements.Add(Supplement("vector-segment-rtl-testbench", StudioArtifactKind.CandidateRunProvenance, "vector-segment-read-lifecycle.tb.sv", File.ReadAllBytes(Path.Combine(output, "vector-segment-read-lifecycle.tb.sv"))));
    supplements.Add(Supplement("vector-segment-rtl-receipt", StudioArtifactKind.CandidateRunProvenance, "vector-segment-read-rtl-receipt.json", rtlReceiptBytes!));
}
if (runtimeTraceVerified)
{
    supplements.Add(Supplement("vector-segment-runtime-comparison", StudioArtifactKind.CandidateRunProvenance, "vector-segment-runtime-comparison.json", runtimeComparisonBytes!));
    supplements.Add(Supplement("vector-segment-active-runtime-trace", StudioArtifactKind.CandidateRunProvenance, "vector-segment-active-runtime-trace.json", File.ReadAllBytes(Path.Combine(runtimeRoot, "active-trace.json"))));
    supplements.Add(Supplement("vector-segment-golden-runtime-trace", StudioArtifactKind.CandidateRunProvenance, "vector-segment-golden-runtime-trace.json", File.ReadAllBytes(Path.Combine(runtimeRoot, "golden-trace.json"))));
    supplements.Add(Supplement("vector-segment-active-runtime-test-log", StudioArtifactKind.CandidateRunProvenance, "vector-segment-active-runtime-test.log", File.ReadAllBytes(Path.Combine(runtimeRoot, "active-test.log"))));
    supplements.Add(Supplement("vector-segment-golden-runtime-test-log", StudioArtifactKind.CandidateRunProvenance, "vector-segment-golden-runtime-test.log", File.ReadAllBytes(Path.Combine(runtimeRoot, "golden-test.log"))));
}
var publicationRequest = new StudioCompilerPublicationRequest(
    "hybridcpu-vector-segment-read-v1",
    "HybridCPU vector-segment read lifecycle (StructuralOnly)",
    "vector-segment-read-20261116",
    combined,
    new HwTargetDescription("logical-memory-event", "generic", "circt-cli"),
    [
        new CompilationStageRecord(CompilationStage.SourceAdmission, CompilationStageStatus.Completed, "Exact active and pinned Golden source hashes and five method contracts were checked."),
        new CompilationStageRecord(CompilationStage.HardwareIrValidation, CompilationStageStatus.Completed, "Two-module admission/lifecycle candidate passed HWIR validation."),
        new CompilationStageRecord(CompilationStage.IndependentVerification, CompilationStageStatus.Completed, rtlVerified
            ? runtimeTraceVerified
                ? $"CIRCT RTL matched {oracleResult.Vectors} independently expected event vectors; active and pinned Golden Rf106 runtime traces also match for {runtimeComparison.GetProperty("eventCount").GetInt32()} events, including vector cancellation/capacity release and pipeline flush. Runtime trace is not RTL equivalence and does not model a physical sequential controller."
                : $"CIRCT RTL matched every output for {oracleResult.Vectors} independently expected event vectors. This does not compare active/Golden runtime and does not model a physical sequential controller."
            : $"Independent software state-machine oracle matched {oracleResult.Vectors} HWIR event transitions; active/Golden runtime and RTL equivalence were not checked."),
        new CompilationStageRecord(CompilationStage.RtlEmission, rtlVerified ? CompilationStageStatus.Completed : CompilationStageStatus.NotRun,
            rtlVerified ? "CIRCT emitted and Icarus simulated combinational lifecycle RTL; receipt binds HWIR, MLIR, RTL, vectors, testbench and tool hashes." : "No RTL was emitted for this publication.")
    ],
    Supplements: supplements,
    SourceDigest: Sha(active.Concat(golden).ToArray()));
var publication = await new StudioSnapshotPublisher().PublishAsync(publicationRequest, output);
Console.WriteLine($"Vector segment HWIR + software oracle PASS: admission={design.CanonicalDigest}; lifecycle={lifecycle.CanonicalDigest}; oracle={oracleResult.Vectors} vectors/{oracleResult.Mismatches} mismatches; guarded methods={methodEvidence.Count}; RTL={(rtlVerified ? "Icarus PASS" : "not run")}; active/Golden runtime={(runtimeTraceVerified ? "46-event Rf106 trace PASS" : "not run")}; Studio candidate={publication.StableManifestPath}; StructuralOnly, no RTL equivalence");
Console.WriteLine(output);

static bool RejectMutation(byte[] active, byte[] golden, string commit, HwToolchainIdentity toolchain, bool mutateGolden = false)
{
    byte[] a = (byte[])active.Clone(), g = (byte[])golden.Clone();
    (mutateGolden ? g : a)[^1] ^= 1;
    try { _ = BridgeHwirVectorSegmentReadAdmission.Compile(a, g, BridgeHwirVectorSegmentReadAdmission.ActiveModelCommit, toolchain, out _); return false; }
    catch (InvalidDataException) { return true; }
}

static byte[] ReadGoldenBlob(string gitDir, string commit, string path)
{
    var list = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    list.ArgumentList.Add("--git-dir=" + gitDir);
    list.ArgumentList.Add("ls-tree"); list.ArgumentList.Add(commit); list.ArgumentList.Add("--"); list.ArgumentList.Add(path);
    using Process ls = Process.Start(list) ?? throw new InvalidOperationException("Could not start git ls-tree.");
    string entry = ls.StandardOutput.ReadToEnd().Trim(); string lsError = ls.StandardError.ReadToEnd(); ls.WaitForExit();
    if (ls.ExitCode != 0) throw new InvalidOperationException("git ls-tree failed: " + lsError);
    string[] fields = entry.Split('\t', StringSplitOptions.RemoveEmptyEntries)[0]
        .Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (fields.Length < 3 || fields[1] != "blob") throw new InvalidDataException("Golden source blob not found: " + path);
    var cat = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    cat.ArgumentList.Add("--git-dir=" + gitDir); cat.ArgumentList.Add("cat-file"); cat.ArgumentList.Add("blob"); cat.ArgumentList.Add(fields[2]);
    using Process process = Process.Start(cat) ?? throw new InvalidOperationException("Could not start git cat-file.");
    using var bytes = new MemoryStream(); process.StandardOutput.BaseStream.CopyTo(bytes);
    string catError = process.StandardError.ReadToEnd(); process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException("git cat-file failed: " + catError);
    return bytes.ToArray();
}

static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
