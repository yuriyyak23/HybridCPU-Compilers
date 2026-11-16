using System.Security.Cryptography;
using System.Text.Json;
using CircuitWeave.Contracts;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.HWIR;
using CircuitWeave.Orchestration;
using CircuitWeave.Studio.Contracts;
using CircuitWeave.Studio.GraphNavigation;
using CircuitWeave.Studio.Integration;
using CircuitWeave.Visualization;
using CircuitWeave.Visualization.Publishing;
using HybridCPU.Compiler.HardwareBridge.Hwir;
const string model = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE";
const string producer = @"C:\Users\Yuriy\Desktop\CircuitWeave-main";
const string output = @"C:\Users\Yuriy\Desktop\HybridCPU Compilers\Compilers\HybridCPU_Compiler\HardwareBridge\.artifacts\csr-storage-event-v1";
bool prepareOnly = args.Length == 1 && args[0] == "--prepare-rtl-inputs";
bool publishWithRtl = args.Length == 1 && args[0] == "--publish-rtl";
if (args.Length > 1 || (args.Length == 1 && !prepareOnly && !publishWithRtl))
    throw new ArgumentException("Usage: CsrStorageEvent.Runner [--prepare-rtl-inputs|--publish-rtl]");
Directory.CreateDirectory(output);
byte[] Read(string p) => File.ReadAllBytes(Path.Combine(model, p));
const string csrRelativePath = @"CloseToHSL\Core\Architecture\Registers\CSR\CsrFile.cs";
byte[] csrSourceBytes = Read(csrRelativePath);
byte[] csrPolicyBytes = Read(@"CloseToHSL\Core\Architecture\Registers\CSR\CsrAccessPolicy.cs");
byte[] csrFsmBytes = Read(@"CloseToHSL\Core\Architecture\State\Pipeline\CPU_Core.FSM.cs");
byte[] stageFlowSourceBytes = Read(@"CloseToHSL\Core\Pipeline\ExecutionFlow\StageFlow\CPU_Core.PipelineExecution.StageFlow.cs");
byte[] pipelineControlSourceBytes = Read(@"CloseToHSL\Core\Pipeline\ExecutionFlow\StageFlow\CPU_Core.Pipeline.Helpers.PipelineControl.cs");
byte[] telemetrySourceBytes = Read(@"CloseToHSL\Core\State\TelemetryState.cs");
byte[] addressSourceBytes = Read(@"Arch\CsrAddresses.cs");
string csrAbsolutePath = Path.GetFullPath(Path.Combine(model, csrRelativePath));
var design = BridgeHwirCsrStorageEvent.Compile(
    csrSourceBytes,
    csrPolicyBytes,
    addressSourceBytes,
    csrFsmBytes,
    "1bfe41a49cf11c80fa6c6aee87accb54b78b331f",
    new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli"), out var inventory);
var stageFlowDesign = BridgeHwirStageFlowCycleCsr.Compile(
    stageFlowSourceBytes,
    pipelineControlSourceBytes,
    telemetrySourceBytes,
    addressSourceBytes, design,
    "1bfe41a49cf11c80fa6c6aee87accb54b78b331f");
var semanticCheck = RunCsrSemanticCrosscheck(design, stageFlowDesign, inventory);
byte[] stageFlowHwir = new CanonicalHwirSerializer().Serialize(stageFlowDesign);
var stageFlowMlirModule = new HierarchicalCirctMlirLowerer().LowerToMlir(stageFlowDesign,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>()));
File.WriteAllBytes(Path.Combine(output, "stageflow-cycle-csr.hwir.json"), stageFlowHwir);
File.WriteAllText(Path.Combine(output, "stageflow-cycle-csr.mlir"), stageFlowMlirModule.Text);
Console.WriteLine($"StageFlow→CSR structural PASS: modules={stageFlowDesign.Design.Modules.Count}, digest={stageFlowDesign.CanonicalDigest}; call event remains unqualified");
byte[] semanticCheckBytes = JsonSerializer.SerializeToUtf8Bytes(semanticCheck.Receipt, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllBytes(Path.Combine(output, "csr-semantic-crosscheck.json"), semanticCheckBytes);
string semanticReportSha = Convert.ToHexString(SHA256.HashData(semanticCheckBytes)).ToLowerInvariant();
string[] csrLines = System.Text.Encoding.UTF8.GetString(csrSourceBytes).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
(int Line, int Column) Locate(string marker, string? columnToken = null)
{
    int lineIndex = Array.FindIndex(csrLines, line => line.Contains(marker, StringComparison.Ordinal));
    if (lineIndex < 0) throw new InvalidDataException("Captured CSR source declaration not found: " + marker);
    int column = csrLines[lineIndex].IndexOf(columnToken ?? marker, StringComparison.Ordinal) + 1;
    return (lineIndex + 1, column);
}
var typeLocation = Locate("public sealed class CsrFile");
var readPolicyLocation = Locate("private static void CheckReadAccess(", "CheckReadAccess(");
var registerDefaultsLocation = Locate("private void RegisterDefaults()", "RegisterDefaults()");
string readPolicySymbol = "YAKSys_Hybrid_CPU.Core.Registers.CsrFile.CheckReadAccess";
string registerDefaultsSymbol = "YAKSys_Hybrid_CPU.Core.Registers.CsrFile.RegisterDefaults";
string readPolicyMemberId = "M:" + readPolicySymbol + "(ushort,YAKSys_Hybrid_CPU.Core.Registers.CsrAccessPolicy,YAKSys_Hybrid_CPU.Core.Registers.PrivilegeLevel)";
string registerDefaultsMemberId = "M:" + registerDefaultsSymbol + "()";
string sourceCaptureRelative = "HybridCPU_ISE/" + csrRelativePath.Replace('\\', '/');
byte[] captureIndexBytes = JsonSerializer.SerializeToUtf8Bytes(new
{
    schema = "circuitweave.csr-source-capture-index/v1",
    status = "one-listed-source-file-hash-checked",
    baseCommit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f",
    scope = "one active CsrFile.cs file only; not project or symbol closure",
    files = new[] { new { path = csrAbsolutePath, relativePath = sourceCaptureRelative, sha256 = BridgeHwirCsrStorageEvent.CsrFileSha256, length = csrSourceBytes.LongLength } }
}, new JsonSerializerOptions { WriteIndented = true });
string sourceCaptureSha = Convert.ToHexString(SHA256.HashData(captureIndexBytes)).ToLowerInvariant();
string projectGraphSha;
using (JsonDocument matrixContext = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(producer, "artifacts", "processor-20261114", "stage-matrix-v73.json"))))
    projectGraphSha = matrixContext.RootElement.GetProperty("projectGraphSha256").GetString()!;
var limitedSourceInventory = new
{
    schema = "circuitweave.source-coverage-index/v1",
    disposition = "incomplete-unqualified",
    project = "HybridCPU_ISE/HybridCPU_ISE.csproj",
    configuration = "Debug",
    semanticReportSha256 = semanticReportSha,
    symbols = new[]
    {
        new { symbol = "global::YAKSys_Hybrid_CPU.Core.Registers.CsrFile", assembly = "CircuitWeave.CsrAccess", kind = "Class", memberCount = 1, blockingReason = "Excerpt-only entry for source-mapped method; not complete type inventory.", declarations = new[] { new { location = new { file = csrAbsolutePath, startLine = typeLocation.Line, startColumn = typeLocation.Column }, memberIds = new[] { readPolicyMemberId } } }, members = new[] { new { id = readPolicyMemberId, kind = "Method", locations = new[] { new { file = csrAbsolutePath, startLine = readPolicyLocation.Line, startColumn = readPolicyLocation.Column } } } } },
        new { symbol = "global::YAKSys_Hybrid_CPU.Core.Registers.CsrFile", assembly = "CircuitWeave.CsrStorageEvent", kind = "Class", memberCount = 1, blockingReason = "Excerpt-only entry for source-mapped method; not complete type inventory.", declarations = new[] { new { location = new { file = csrAbsolutePath, startLine = typeLocation.Line, startColumn = typeLocation.Column }, memberIds = new[] { registerDefaultsMemberId } } }, members = new[] { new { id = registerDefaultsMemberId, kind = "Method", locations = new[] { new { file = csrAbsolutePath, startLine = registerDefaultsLocation.Line, startColumn = registerDefaultsLocation.Column } } } } }
    },
    captureVerification = new { schema = "circuitweave.source-capture-check/v1", status = "producer-reported-listed-inputs-match", semanticReportSha256 = semanticReportSha, sourceCaptureSha256 = sourceCaptureSha, projectGraphSha256 = projectGraphSha, scope = "Hash-checked capture of the single listed CsrFile.cs source; projectGraphSha256 references the separate selected Debug project-graph audit context, not a claim that this is a complete project source capture.", checkedFiles = 1, rootProject = "not-captured" },
    capturedSourceFiles = new[] { new { path = csrAbsolutePath, sha256 = BridgeHwirCsrStorageEvent.CsrFileSha256, length = csrSourceBytes.LongLength } }
};
byte[] sourceInventoryBytes = JsonSerializer.SerializeToUtf8Bytes(limitedSourceInventory, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
string sourceInventorySha = Convert.ToHexString(SHA256.HashData(sourceInventoryBytes)).ToLowerInvariant();
byte[] capturedSourcesBytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = "circuitweave.captured-sources/v1", inventorySha256 = sourceInventorySha, files = new[] { new { path = csrAbsolutePath, bytes = Convert.ToBase64String(csrSourceBytes) } } }, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllBytes(Path.Combine(output, "csr-source-capture-index.json"), captureIndexBytes);
File.WriteAllBytes(Path.Combine(output, "csr-source-inventory-studio.json"), sourceInventoryBytes);
File.WriteAllBytes(Path.Combine(output, "csr-captured-sources.json"), capturedSourcesBytes);
byte[] hwir = new CanonicalHwirSerializer().Serialize(design);
var mlirModule = new HierarchicalCirctMlirLowerer().LowerToMlir(design, new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>()));
byte[] mlir = System.Text.Encoding.UTF8.GetBytes(mlirModule.Text);
File.WriteAllBytes(Path.Combine(output, "csr-storage-event.mlir"), mlir);
File.WriteAllBytes(Path.Combine(output, "csr-storage-event.hwir.json"), hwir);
File.WriteAllText(Path.Combine(output, "csr-register-inventory.json"), JsonSerializer.Serialize(inventory, new JsonSerializerOptions { WriteIndented = true }) + "\n");
var manifest = new { schema = "circuitweave.csr-storage-event-artifact/v1", trust = "StructuralOnly", semanticStatus = "one serialized CsrFile operation projection; physical clock, reset, arbitration and RAM behavior unqualified", activeBaseCommit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f", sourceFiles = new[] { new { path = "CloseToHSL/Core/Architecture/Registers/CSR/CsrFile.cs", sha256 = BridgeHwirCsrStorageEvent.CsrFileSha256 }, new { path = "CloseToHSL/Core/Architecture/Registers/CSR/CsrAccessPolicy.cs", sha256 = BridgeHwirCsrStorageEvent.PolicySha256 }, new { path = "Arch/CsrAddresses.cs", sha256 = BridgeHwirCsrStorageEvent.AddressesSha256 }, new { path = "CloseToHSL/Core/Architecture/State/Pipeline/CPU_Core.FSM.cs", sha256 = BridgeHwirCsrStorageEvent.FsmSha256 } }, registrations = inventory.Count, hwirSha256 = Convert.ToHexString(SHA256.HashData(hwir)).ToLowerInvariant(), hwirBytes = hwir.Length, modules = design.Design.Modules.Select(m => m.Id).ToArray(), validated = true, mlirSha256 = Convert.ToHexString(SHA256.HashData(mlir)).ToLowerInvariant(), mlirBytes = mlir.Length, semanticCheckSha256 = Convert.ToHexString(SHA256.HashData(semanticCheckBytes)).ToLowerInvariant(), semanticCheckVectors = semanticCheck.Vectors, semanticCheckStatus = "HWIR interpreter vs independent software-semantics oracle; not RTL/Golden equivalence" };
File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine(JsonSerializer.Serialize(manifest));
if (prepareOnly) return;
byte[] rtlBytes = [];
byte[] rtlReceiptBytes = [];
byte[] runtimeProvenanceBytes = [];
if (publishWithRtl)
{
    string rtlArtifact = Environment.GetEnvironmentVariable("CSR_STORAGE_EVENT_RTL_ARTIFACT") ??
        Path.Combine(producer, "artifacts", "processor-20261114", "csr-storage-event-rtl-v4");
    rtlBytes = File.ReadAllBytes(Path.Combine(rtlArtifact, "csr_storage_event.sv"));
    rtlReceiptBytes = File.ReadAllBytes(Path.Combine(rtlArtifact, "csr-storage-event-rtl-receipt.json"));
    runtimeProvenanceBytes = File.ReadAllBytes(Path.Combine(rtlArtifact, "runtime-source-provenance.json"));
    using JsonDocument rtlEvidence = JsonDocument.Parse(rtlReceiptBytes);
    if (rtlEvidence.RootElement.GetProperty("status").GetString() != "PASS_SERIALIZED_SOFTWARE_EVENT_RTL_ONLY" ||
        rtlEvidence.RootElement.GetProperty("singleEventVectors").GetInt32() != semanticCheck.Vectors ||
        rtlEvidence.RootElement.GetProperty("chainedEventVectors").GetInt32() < inventory.Count * 20 ||
        rtlEvidence.RootElement.GetProperty("runtimeComparison").GetProperty("status").GetString() != "PASS")
        throw new InvalidDataException("RTL evidence must pass the source runtime and pinned-Golden chained recurrence.");
    using JsonDocument runtimeProvenance = JsonDocument.Parse(runtimeProvenanceBytes);
    if (runtimeProvenance.RootElement.GetProperty("goldenCommit").GetString() != "6946ccab9d888f7dae0a4d8e18480f436138acd9")
        throw new InvalidDataException("Runtime source provenance is not pinned to the required Golden commit.");
    string runtimeProvenanceSha = Convert.ToHexString(SHA256.HashData(runtimeProvenanceBytes)).ToLowerInvariant();
    if (rtlEvidence.RootElement.GetProperty("runtimeComparison").GetProperty("sourceProvenanceSha256").GetString() != runtimeProvenanceSha)
        throw new InvalidDataException("Runtime source provenance digest does not match the independently checked runtime receipt.");
}
var stages = new[] { new CompilationStageRecord(CompilationStage.SourceAdmission, CompilationStageStatus.NotRun, "Roslyn source admission not run."), new CompilationStageRecord(CompilationStage.HardwareIrValidation, CompilationStageStatus.Completed, "Source-bound HWIR parsed and fail-closed validated."), new CompilationStageRecord(CompilationStage.RtlEmission, CompilationStageStatus.NotRun, "No RTL lowering or emit performed."), new CompilationStageRecord(CompilationStage.IndependentVerification, CompilationStageStatus.NotRun, "No independent RTL/reference verification performed.") };
byte[] inventoryBytes = JsonSerializer.SerializeToUtf8Bytes(inventory, new JsonSerializerOptions { WriteIndented = true });
string sourceDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest.sourceFiles))).ToLowerInvariant();
if (publishWithRtl)
    stages = [new(CompilationStage.SourceAdmission, CompilationStageStatus.NotRun, "Roslyn source admission not run."), new(CompilationStage.HardwareIrValidation, CompilationStageStatus.Completed, "Source-bound HWIR parsed and fail-closed validated."), new(CompilationStage.RtlEmission, CompilationStageStatus.Completed, "CIRCT emitted combinational RTL for the serialized CsrFile software-event projection."), new(CompilationStage.IndependentVerification, CompilationStageStatus.Completed, "3,360 single-event and 812 chained vectors matched independent equations and exact active plus pinned-Golden CsrFile runtime traces; callback acquire/dispose order checked. No physical timing or CPU retire equivalence.")];
var supplements = new List<StudioSupplementArtifact> { new("csr-storage-event-mlir", CircuitWeave.Visualization.StudioArtifactKind.Mlir, "csr-storage-event.mlir", mlir, Convert.ToHexString(SHA256.HashData(mlir)).ToLowerInvariant()), new("csr-semantic-crosscheck", CircuitWeave.Visualization.StudioArtifactKind.CandidateRunProvenance, "csr-semantic-crosscheck.json", semanticCheckBytes, Convert.ToHexString(SHA256.HashData(semanticCheckBytes)).ToLowerInvariant()), new("csr-source-inventory", CircuitWeave.Visualization.StudioArtifactKind.SourceInventory, "csr-source-inventory-studio.json", sourceInventoryBytes, sourceInventorySha), new("csr-captured-source-bytes", CircuitWeave.Visualization.StudioArtifactKind.CapturedSources, "csr-captured-sources.json", capturedSourcesBytes, Convert.ToHexString(SHA256.HashData(capturedSourcesBytes)).ToLowerInvariant()), new("csr-source-capture-index", CircuitWeave.Visualization.StudioArtifactKind.CandidateRunProvenance, "csr-source-capture-index.json", captureIndexBytes, sourceCaptureSha) };
if (publishWithRtl)
{
    supplements.Add(new("csr-storage-event-systemverilog", CircuitWeave.Visualization.StudioArtifactKind.SystemVerilog, "csr-storage-event.sv", rtlBytes, Convert.ToHexString(SHA256.HashData(rtlBytes)).ToLowerInvariant()));
    supplements.Add(new("csr-storage-event-rtl-verification", CircuitWeave.Visualization.StudioArtifactKind.CandidateRunProvenance, "csr-storage-event-rtl-receipt.json", rtlReceiptBytes, Convert.ToHexString(SHA256.HashData(rtlReceiptBytes)).ToLowerInvariant()));
    supplements.Add(new("csr-storage-event-runtime-source-provenance", CircuitWeave.Visualization.StudioArtifactKind.CandidateRunProvenance, "csr-storage-event-runtime-source-provenance.json", runtimeProvenanceBytes, Convert.ToHexString(SHA256.HashData(runtimeProvenanceBytes)).ToLowerInvariant()));
}
var request = new StudioCompilerPublicationRequest("hybridcpu-csr-storage-event", "HybridCPU CSR serialized event", publishWithRtl ? "csr-storage-event-v4-runtime-rtl" : "csr-storage-event-v1", design, new HwTargetDescription("unqualified-comb-projection", "HybridCPU", "none"), stages, Supplements: supplements, SourceDigest: sourceDigest);
string studioOut = Path.Combine(producer, "artifacts", "processor-20261114", publishWithRtl ? "csr-storage-event-studio-v4" : "csr-storage-event-studio-v1");
Directory.CreateDirectory(studioOut);
var receipt = await new StudioSnapshotPublisher().PublishAsync(request, studioOut);
Console.WriteLine(JsonSerializer.Serialize(receipt));
var opened = await new StudioWorkspaceLoader().OpenAsync(Path.Combine(studioOut, "studio.project.json"));
if (!opened.Success || opened.Value is null) throw new InvalidDataException("Studio loader refused candidate: " + opened.Error);
var session = opened.Value;
int moduleNodes = session.Graph.Nodes.Count(n => n.Kind == CircuitWeave.Studio.Contracts.StudioGraphNodeKind.Module);
int instanceNodes = session.Graph.Nodes.Count(n => n.Kind == CircuitWeave.Studio.Contracts.StudioGraphNodeKind.Instance);
if (moduleNodes != 2 || instanceNodes != 1 || session.Graph.Trust != CircuitWeave.Studio.Contracts.StudioDocumentTrust.StructuralOnly)
    throw new InvalidDataException($"Unexpected Studio graph/modules/instances/trust: {moduleNodes}/{instanceNodes}/{session.Graph.Trust}");
var nav = GraphNavigationIndex.Create(session.Graph);
var roots = nav.GetRoots(0, 8);
if (roots.Items.Length == 0 || roots.TotalCount == 0) throw new InvalidDataException("Studio hierarchy roots missing.");
var topChildren = nav.GetChildren(nav.TopModuleId, 0, 12);
if (topChildren.TotalCount == 0) throw new InvalidDataException("Studio hierarchy children missing.");
var sourceOrigin = session.SourceMap?.Mappings.Single(m => m.GraphNodeId == "module:csr_storage_serialized_event_projection").Source
    ?? throw new InvalidDataException("Studio source-map entry for CSR storage module missing.");
string? sourceExcerpt = CapturedNodeSource.Excerpt(session, sourceOrigin);
if (sourceExcerpt?.Contains("RegisterDefaults", StringComparison.Ordinal) != true || !sourceExcerpt.Contains("captured snapshot bytes", StringComparison.Ordinal))
    throw new InvalidDataException("Studio captured-source excerpt did not resolve for CSR module.");
Console.WriteLine($"Studio API PASS: {moduleNodes} modules, {instanceNodes} instances, {session.Graph.Nodes.Count} nodes, {session.Graph.Edges.Count} edges; roots/children paging; captured CsrFile excerpt resolved; trust={session.Graph.Trust}");
var integratedSourceSet = new[]
{
    BridgeHwirStageFlowCycleCsr.StageFlowSha256,
    BridgeHwirStageFlowCycleCsr.PipelineControlSha256,
    BridgeHwirStageFlowCycleCsr.TelemetryStateSha256,
    BridgeHwirStageFlowCycleCsr.AddressesSha256,
    BridgeHwirCsrStorageEvent.CsrFileSha256,
    BridgeHwirCsrStorageEvent.PolicySha256,
    BridgeHwirCsrStorageEvent.FsmSha256
};
string integratedSourceDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(integratedSourceSet))).ToLowerInvariant();
var integratedFiles = new (string RelativePath, string AbsolutePath, byte[] Bytes, string Sha256)[]
{
    (csrRelativePath, csrAbsolutePath, csrSourceBytes, BridgeHwirCsrStorageEvent.CsrFileSha256),
    (@"CloseToHSL\Core\Architecture\Registers\CSR\CsrAccessPolicy.cs", Path.GetFullPath(Path.Combine(model, @"CloseToHSL\Core\Architecture\Registers\CSR\CsrAccessPolicy.cs")), csrPolicyBytes, BridgeHwirCsrStorageEvent.PolicySha256),
    (@"Arch\CsrAddresses.cs", Path.GetFullPath(Path.Combine(model, @"Arch\CsrAddresses.cs")), addressSourceBytes, BridgeHwirCsrStorageEvent.AddressesSha256),
    (@"CloseToHSL\Core\Architecture\State\Pipeline\CPU_Core.FSM.cs", Path.GetFullPath(Path.Combine(model, @"CloseToHSL\Core\Architecture\State\Pipeline\CPU_Core.FSM.cs")), csrFsmBytes, BridgeHwirCsrStorageEvent.FsmSha256),
    (@"CloseToHSL\Core\Pipeline\ExecutionFlow\StageFlow\CPU_Core.PipelineExecution.StageFlow.cs", Path.GetFullPath(Path.Combine(model, @"CloseToHSL\Core\Pipeline\ExecutionFlow\StageFlow\CPU_Core.PipelineExecution.StageFlow.cs")), stageFlowSourceBytes, BridgeHwirStageFlowCycleCsr.StageFlowSha256),
    (@"CloseToHSL\Core\Pipeline\ExecutionFlow\StageFlow\CPU_Core.Pipeline.Helpers.PipelineControl.cs", Path.GetFullPath(Path.Combine(model, @"CloseToHSL\Core\Pipeline\ExecutionFlow\StageFlow\CPU_Core.Pipeline.Helpers.PipelineControl.cs")), pipelineControlSourceBytes, BridgeHwirStageFlowCycleCsr.PipelineControlSha256),
    (@"CloseToHSL\Core\State\TelemetryState.cs", Path.GetFullPath(Path.Combine(model, @"CloseToHSL\Core\State\TelemetryState.cs")), telemetrySourceBytes, BridgeHwirStageFlowCycleCsr.TelemetryStateSha256)
};
foreach (var file in integratedFiles)
    if (Convert.ToHexString(SHA256.HashData(file.Bytes)).ToLowerInvariant() != file.Sha256)
        throw new InvalidDataException("Integrated Studio capture source hash mismatch: " + file.RelativePath);
var captureLocations = new Dictionary<string, (int Line, int Column)>(StringComparer.Ordinal);
(int Line, int Column) LocateSource(byte[] bytes, string marker, string? columnMarker = null)
{
    string[] lines = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    int line = Array.FindIndex(lines, x => x.Contains(marker, StringComparison.Ordinal));
    if (line < 0) throw new InvalidDataException("Integrated source inventory marker missing: " + marker);
    int col = lines[line].IndexOf(columnMarker ?? marker, StringComparison.Ordinal);
    return (line + 1, col + 1);
}
var checkReadLoc = LocateSource(csrSourceBytes, "private static void CheckReadAccess(", "CheckReadAccess(");
var registerDefaultsLoc = LocateSource(csrSourceBytes, "private void RegisterDefaults()", "RegisterDefaults()");
var csrTypeLoc = LocateSource(csrSourceBytes, "public sealed class CsrFile");
var stageMethodLoc = LocateSource(stageFlowSourceBytes, "Csr.HardwareWrite(CsrAddresses.Cycle,");
var coreTypeLoc = LocateSource(stageFlowSourceBytes, "public sealed partial class CPU_Core");
const string checkReadMember = "M:YAKSys_Hybrid_CPU.Core.Registers.CsrFile.CheckReadAccess(ushort,YAKSys_Hybrid_CPU.Core.Registers.CsrAccessPolicy,YAKSys_Hybrid_CPU.Core.Registers.PrivilegeLevel)";
const string registerDefaultsMember = "M:YAKSys_Hybrid_CPU.Core.Registers.CsrFile.RegisterDefaults()";
const string stageFlowMember = "M:YAKSys_Hybrid_CPU.Processor.CPU_Core.ExecuteUnparkedPipelineCycle()";
object[] integratedInventorySymbols =
[
    new { symbol = "global::YAKSys_Hybrid_CPU.Core.Registers.CsrFile", assembly = "CircuitWeave.CsrAccess", kind = "Class", memberCount = 1, blockingReason = "Source-map excerpt only; not full CsrFile type/member inventory.", declarations = new[] { new { location = new { file = integratedFiles[0].AbsolutePath, startLine = csrTypeLoc.Line, startColumn = csrTypeLoc.Column }, memberIds = new[] { checkReadMember } } }, members = new[] { new { id = checkReadMember, kind = "Method", locations = new[] { new { file = integratedFiles[0].AbsolutePath, startLine = checkReadLoc.Line, startColumn = checkReadLoc.Column } } } } },
    new { symbol = "global::YAKSys_Hybrid_CPU.Core.Registers.CsrFile", assembly = "CircuitWeave.CsrStorageEvent", kind = "Class", memberCount = 1, blockingReason = "Source-map excerpt only; not full CsrFile type/member inventory.", declarations = new[] { new { location = new { file = integratedFiles[0].AbsolutePath, startLine = csrTypeLoc.Line, startColumn = csrTypeLoc.Column }, memberIds = new[] { registerDefaultsMember } } }, members = new[] { new { id = registerDefaultsMember, kind = "Method", locations = new[] { new { file = integratedFiles[0].AbsolutePath, startLine = registerDefaultsLoc.Line, startColumn = registerDefaultsLoc.Column } } } } },
    new { symbol = "global::YAKSys_Hybrid_CPU.Processor.CPU_Core", assembly = "CircuitWeave.StageFlowCycleCsrWrite", kind = "Class", memberCount = 1, blockingReason = "Source-map excerpt only; not full Processor partial/member inventory.", declarations = new[] { new { location = new { file = integratedFiles[4].AbsolutePath, startLine = coreTypeLoc.Line, startColumn = coreTypeLoc.Column }, memberIds = new[] { stageFlowMember } } }, members = new[] { new { id = stageFlowMember, kind = "Method", locations = new[] { new { file = integratedFiles[4].AbsolutePath, startLine = stageMethodLoc.Line, startColumn = stageMethodLoc.Column } } } } }
];
byte[] integratedSourceInventoryBytes = JsonSerializer.SerializeToUtf8Bytes(new
{
    schema = "circuitweave.source-coverage-index/v1", disposition = "incomplete-unqualified", project = "HybridCPU_ISE/HybridCPU_ISE.csproj", configuration = "Debug",
    semanticReportSha256 = Convert.ToHexString(SHA256.HashData(semanticCheckBytes)).ToLowerInvariant(), symbols = integratedInventorySymbols,
    captureVerification = new { schema = "circuitweave.source-capture-check/v1", status = "producer-reported-listed-inputs-match", semanticReportSha256 = Convert.ToHexString(SHA256.HashData(semanticCheckBytes)).ToLowerInvariant(), sourceCaptureSha256 = integratedSourceDigest, projectGraphSha256 = projectGraphSha, scope = "Seven hash-checked source files used by CSR/StageFlow projection; not project closure or complete source inventory.", checkedFiles = integratedFiles.Length, rootProject = "not-captured" },
    capturedSourceFiles = integratedFiles.Select(f => new { path = f.AbsolutePath, sha256 = f.Sha256, length = f.Bytes.LongLength }).ToArray()
}, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
string integratedInventorySha = Convert.ToHexString(SHA256.HashData(integratedSourceInventoryBytes)).ToLowerInvariant();
byte[] integratedCapturedSourcesBytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = "circuitweave.captured-sources/v1", inventorySha256 = integratedInventorySha, files = integratedFiles.Select(f => new { path = f.AbsolutePath, bytes = Convert.ToBase64String(f.Bytes) }).ToArray() }, new JsonSerializerOptions { WriteIndented = true });
byte[] integratedCaptureIndexBytes = JsonSerializer.SerializeToUtf8Bytes(new
{
    schema = "circuitweave.csr-source-capture-index/v1", status = "seven-listed-source-files-hash-checked", baseCommit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f",
    scope = "Seven active source files used by CSR/StageFlow projection only; not project or symbol closure.",
    files = integratedFiles.Select(f => new { path = f.AbsolutePath, relativePath = "HybridCPU_ISE/" + f.RelativePath.Replace('\\', '/'), sha256 = f.Sha256, length = f.Bytes.LongLength }).ToArray()
}, new JsonSerializerOptions { WriteIndented = true });
byte[] integratedCaptureReceiptBytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = "circuitweave.stageflow-csr-source-capture-receipt/v1", sourceInventorySha256 = integratedInventorySha, sourceSetDigest = integratedSourceDigest, checkedFiles = integratedFiles.Length, sourceInventoryStatus = "partial excerpt-only" }, new JsonSerializerOptions { WriteIndented = true });
string integratedCaptureReceiptSha = Convert.ToHexString(SHA256.HashData(integratedCaptureReceiptBytes)).ToLowerInvariant();
var integratedRequest = new StudioCompilerPublicationRequest("hybridcpu-stageflow-cycle-csr", "HybridCPU StageFlow CycleCount to CSR HardwareWrite", "stageflow-cycle-csr-v1", stageFlowDesign,
    new HwTargetDescription("unqualified-comb-call-event-projection", "HybridCPU", "none"), stages,
    Supplements: [
        new StudioSupplementArtifact("stageflow-cycle-csr-mlir", CircuitWeave.Visualization.StudioArtifactKind.Mlir, "stageflow-cycle-csr.mlir", System.Text.Encoding.UTF8.GetBytes(stageFlowMlirModule.Text), Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stageFlowMlirModule.Text))).ToLowerInvariant()),
        new StudioSupplementArtifact("stageflow-cycle-csr-semantic-crosscheck", CircuitWeave.Visualization.StudioArtifactKind.CandidateRunProvenance, "stageflow-cycle-csr-semantic-crosscheck.json", semanticCheckBytes, Convert.ToHexString(SHA256.HashData(semanticCheckBytes)).ToLowerInvariant()),
        new StudioSupplementArtifact("stageflow-cycle-csr-source-inventory", CircuitWeave.Visualization.StudioArtifactKind.SourceInventory, "stageflow-cycle-csr-source-inventory.json", integratedSourceInventoryBytes, integratedInventorySha),
        new StudioSupplementArtifact("stageflow-cycle-csr-captured-sources", CircuitWeave.Visualization.StudioArtifactKind.CapturedSources, "stageflow-cycle-csr-captured-sources.json", integratedCapturedSourcesBytes, Convert.ToHexString(SHA256.HashData(integratedCapturedSourcesBytes)).ToLowerInvariant()),
        new StudioSupplementArtifact("stageflow-cycle-csr-capture-index", CircuitWeave.Visualization.StudioArtifactKind.CandidateRunProvenance, "stageflow-cycle-csr-source-capture-index.json", integratedCaptureIndexBytes, Convert.ToHexString(SHA256.HashData(integratedCaptureIndexBytes)).ToLowerInvariant()),
        new StudioSupplementArtifact("stageflow-cycle-csr-capture-receipt", CircuitWeave.Visualization.StudioArtifactKind.CandidateRunProvenance, "stageflow-cycle-csr-source-capture-receipt.json", integratedCaptureReceiptBytes, integratedCaptureReceiptSha)
    ], SourceDigest: integratedSourceDigest);
string integratedStudioOut = Path.Combine(producer, "artifacts", "processor-20261114", "stageflow-cycle-csr-studio-v1");
Directory.CreateDirectory(integratedStudioOut);
var integratedReceipt = await new StudioSnapshotPublisher().PublishAsync(integratedRequest, integratedStudioOut);
var integratedOpened = await new StudioWorkspaceLoader().OpenAsync(Path.Combine(integratedStudioOut, "studio.project.json"));
if (!integratedOpened.Success || integratedOpened.Value is null)
    throw new InvalidDataException("Studio loader refused StageFlow→CSR project: " + integratedOpened.Error);
var integratedSession = integratedOpened.Value;
var integratedNav = GraphNavigationIndex.Create(integratedSession.Graph);
var integratedRoots = integratedNav.GetRoots(0, 8);
var integratedChildren = integratedNav.GetChildren(integratedNav.TopModuleId, 0, 12);
if (integratedSession.Graph.Trust != CircuitWeave.Studio.Contracts.StudioDocumentTrust.StructuralOnly ||
    integratedSession.Graph.Nodes.Count(n => n.Kind == CircuitWeave.Studio.Contracts.StudioGraphNodeKind.Module) != 3 ||
    integratedSession.Graph.Nodes.Count(n => n.Kind == CircuitWeave.Studio.Contracts.StudioGraphNodeKind.Instance) != 2 ||
    integratedRoots.TotalCount == 0 || integratedChildren.TotalCount == 0)
    throw new InvalidDataException("Unexpected StageFlow→CSR Studio graph/hierarchy/trust.");
var stageFlowSourceOrigin = integratedSession.SourceMap?.Mappings.Single(m => m.GraphNodeId == "module:stageflow_cycle_to_csr_hardware_write_projection").Source
    ?? throw new InvalidDataException("StageFlow Studio module source-map entry missing.");
var csrStorageSourceOrigin = integratedSession.SourceMap?.Mappings.Single(m => m.GraphNodeId == "module:csr_storage_serialized_event_projection").Source
    ?? throw new InvalidDataException("CSR storage Studio module source-map entry missing.");
string? stageFlowExcerpt = CapturedNodeSource.Excerpt(integratedSession, stageFlowSourceOrigin);
string? csrStorageExcerpt = CapturedNodeSource.Excerpt(integratedSession, csrStorageSourceOrigin);
if (stageFlowExcerpt?.Contains("ExecuteUnparkedPipelineCycle", StringComparison.Ordinal) != true ||
    csrStorageExcerpt?.Contains("RegisterDefaults", StringComparison.Ordinal) != true)
    throw new InvalidDataException($"Captured StageFlow/CSR source excerpts did not resolve: stage={stageFlowExcerpt ?? "<null>"} csr={csrStorageExcerpt ?? "<null>"}; origins={stageFlowSourceOrigin.RelativePath}:{stageFlowSourceOrigin.Line}:{stageFlowSourceOrigin.Column} {stageFlowSourceOrigin.Symbol}; {csrStorageSourceOrigin.RelativePath}:{csrStorageSourceOrigin.Line}:{csrStorageSourceOrigin.Column} {csrStorageSourceOrigin.Symbol}");
Console.WriteLine($"Integrated Studio API PASS: modules=3 instances=2 nodes={integratedSession.Graph.Nodes.Count} edges={integratedSession.Graph.Edges.Count}; roots/children paging; captured StageFlow+CSR excerpts resolved; trust={integratedSession.Graph.Trust}; snapshot={integratedReceipt.SnapshotDigest}");

static (int Vectors, object Receipt) RunCsrSemanticCrosscheck(HwValidatedDesign candidate, HwValidatedDesign integratedCandidate,
    IReadOnlyList<BridgeHwirCsrStorageEvent.SourceInventoryRow> registrations)
{
    const string topName = "csr_storage_serialized_event_projection";
    var modules = integratedCandidate.Design.Modules.ToDictionary(m => m.Id, StringComparer.Ordinal);
    var top = modules[topName];
    var byAddress = registrations.ToDictionary(r => r.Address);
    ushort[] addresses = registrations.Select(r => r.Address)
        .Concat(new ushort[] { 0x0fff, 0xffff }.Where(a => !byAddress.ContainsKey(a)))
        .Distinct().ToArray();
    uint[] privileges = [0u, 1u, 2u, 3u, 4u, 0x7fffffffu, 0x80000000u, 0xffffffffu];
    const ulong writeData = 0xd3a59c71e4b2860fUL;
    int vectors = 0;

    ulong Mask(int width) => width == 64 ? ulong.MaxValue : (1UL << width) - 1;
    Dictionary<string, ulong> EvalModule(HwModule module, IReadOnlyDictionary<string, ulong> inputs)
    {
        var nodeMap = module.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var memo = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var instanceWires = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var outputs = new Dictionary<string, ulong>(StringComparer.Ordinal);
        ulong Value(string id)
        {
            if (inputs.TryGetValue(id, out ulong input)) return input;
            if (instanceWires.TryGetValue(id, out ulong wire)) return wire;
            if (memo.TryGetValue(id, out ulong cached)) return cached;
            if (!nodeMap.TryGetValue(id, out HwNode? node)) throw new InvalidDataException("Unresolved signal in independent HWIR interpreter: " + id);
            ulong result = node switch
            {
                HwConstantNode c => Convert.ToUInt64(c.BitPattern, 2),
                HwMuxNode m => Value(m.Condition) != 0 ? Value(m.WhenTrue) : Value(m.WhenFalse),
                HwOperationNode o => Operation(o),
                _ => throw new InvalidDataException("Unsupported node in independent HWIR interpreter: " + node.GetType().Name)
            };
            memo.Add(id, result);
            return result;

            ulong Operation(HwOperationNode op)
            {
                ulong[] args = op.Operands.Select(Value).ToArray();
                ulong value = op.Opcode switch
                {
                    "and" => args.Aggregate(ulong.MaxValue, (a, b) => a & b),
                    "or" => args.Aggregate(0UL, (a, b) => a | b),
                    "xor" => args.Aggregate(0UL, (a, b) => a ^ b),
                    "add" => unchecked(args[0] + args[1]),
                    "eq" => args[0] == args[1] ? 1UL : 0UL,
                    "mux" => args[0] != 0 ? args[1] : args[2],
                    _ => throw new InvalidDataException("Unsupported operation in independent HWIR interpreter: " + op.Opcode)
                };
                return value & Mask(op.ResultType.Width);
            }
        }

        foreach (HwInstanceNode instance in module.Instances)
        {
            HwModule child = modules[instance.ModuleReference];
            var childInputs = new Dictionary<string, ulong>(StringComparer.Ordinal);
            foreach (HwPortContract port in child.Ports.Where(p => p.Direction == HwPortDirection.Input))
                childInputs.Add(port.Name, Value(instance.PortConnections[port.Name]));
            var childOutputs = EvalModule(child, childInputs);
            foreach (HwPortContract port in child.Ports.Where(p => p.Direction == HwPortDirection.Output))
                instanceWires.Add(instance.PortConnections[port.Name], childOutputs[port.Name]);
        }
        foreach (HwOutputDriveNode drive in module.Nodes.OfType<HwOutputDriveNode>())
            outputs.Add(drive.OutputPort, Value(drive.Value));
        return outputs;
    }

    foreach (ushort address in addresses)
    foreach (uint privilege in privileges)
    for (int operation = 0; operation <= 4; operation++)
    for (int stateVariant = 0; stateVariant < 2; stateVariant++)
    {
        var current = registrations.ToDictionary(r => "current_" + r.AddressMember,
            r => stateVariant == 0 ? r.InitialValue : r.InitialValue ^ ((ulong)r.Address << 32) ^ 0xa55a_5aa5_0123_fedcUL, StringComparer.Ordinal);
        var inputs = new Dictionary<string, ulong>(current, StringComparer.Ordinal)
        {
            ["event_valid"] = 1, ["operation"] = (ulong)operation, ["address"] = address,
            ["write_data"] = writeData, ["privilege"] = privilege
        };
        IReadOnlyDictionary<string, ulong> actual = EvalModule(top, inputs);
        bool registered = byAddress.TryGetValue(address, out var row);
        int signedPrivilege = unchecked((int)privilege);
        bool privilegeFault = false;
        if (registered && operation == 0 && privilege != 3 && row!.Policy == "MachineReadWrite")
            privilegeFault = signedPrivilege < 3;
        if (registered && operation == 1)
            privilegeFault = row!.Policy switch
            {
                "MachineReadWrite" => signedPrivilege < 3,
                "ReadOnly" or "HardwareUpdated" => true,
                "SupervisorReadWrite" => signedPrivilege < 1,
                _ => throw new InvalidDataException("Unknown source policy " + row.Policy)
            };
        bool unknownFault = operation is 0 or 1 or 2 && !registered;
        bool anyFault = privilegeFault || unknownFault;
        ulong InputCurrent(BridgeHwirCsrStorageEvent.SourceInventoryRow r) => inputs["current_" + r.AddressMember];
        void Expect(string output, ulong expected)
        {
            if (!actual.TryGetValue(output, out ulong found) || found != expected)
                throw new InvalidDataException($"CSR HWIR/oracle mismatch op={operation} addr=0x{address:X3} priv=0x{privilege:X8} output={output} expected=0x{expected:X} actual={(actual.TryGetValue(output, out found) ? "0x" + found.ToString("X") : "<missing>")}");
        }
        Expect("operation_valid", 1); Expect("operation_invalid", 0);
        Expect("reservation_kind", operation is 0 or 3 ? 1UL : operation is 1 or 2 ? 2UL : 0UL);
        Expect("is_registered", operation == 4 && registered ? 1UL : 0UL);
        Expect("unknown_address_fault", unknownFault ? 1UL : 0UL);
        Expect("privilege_fault", privilegeFault ? 1UL : 0UL);
        Expect("fault_address", anyFault ? address : 0UL);
        uint selectedPolicy = registered ? row!.PolicyValue : 0;
        Expect("selected_policy", selectedPolicy);
        uint required = privilegeFault ? operation == 1 && row!.Policy == "SupervisorReadWrite" ? 1u : 3u : 0u;
        Expect("required_privilege", required); Expect("actual_privilege", privilegeFault ? privilege : 0UL);
        Expect("is_write_fault", privilegeFault && operation == 1 ? 1UL : 0UL);
        bool writeEnable = !anyFault && operation is 1 or 2;
        Expect("write_enable", writeEnable ? 1UL : 0UL);
        ulong readData = !anyFault && operation is 0 or 3 && registered ? InputCurrent(row!) : 0UL;
        Expect("read_data", readData);
        foreach (var r in registrations)
        {
            ulong before = InputCurrent(r);
            ulong expectedNext = writeEnable && address == r.Address ? writeData : before;
            Expect("next_" + r.AddressMember, expectedNext);
        }
        vectors++;
    }
    int integratedVectors = 0;
    foreach (ulong count in new[] { 0UL, 1UL, ulong.MaxValue })
    foreach (ulong callReached in new[] { 0UL, 1UL })
    foreach (ulong enabled in new[] { 0UL, 1UL })
    foreach (ulong reservationAcquired in new[] { 0UL, 1UL })
    {
        var inputs = registrations.ToDictionary(r => "current_" + r.AddressMember,
            r => r.AddressMember == "Cycle" ? count : r.InitialValue, StringComparer.Ordinal);
        inputs["pipeline_call_reached_unparked_body"] = callReached;
        inputs["pipeline_enabled"] = enabled;
        inputs["storage_reservation_acquired"] = reservationAcquired;
        inputs["cycle_count_before"] = count;
        IReadOnlyDictionary<string, ulong> actual = EvalModule(modules["stageflow_cycle_to_csr_hardware_write_projection"], inputs);
        ulong eventExpected = callReached & enabled;
        ulong nextCountExpected = eventExpected == 0 ? count : unchecked(count + 1);
        if (actual["cycle_update_event"] != eventExpected || actual["cycle_count_next"] != nextCountExpected ||
            actual["storage_reservation_failed"] != (eventExpected != 0 && reservationAcquired == 0 ? 1UL : 0UL))
            throw new InvalidDataException("StageFlow call-event output disagrees with independent event/count oracle.");
        ulong csrExpected = eventExpected != 0 && reservationAcquired != 0 ? nextCountExpected : count;
        var cycleRow = registrations.Single(r => r.Address == 0xC00);
        if (actual["next_" + cycleRow.AddressMember] != csrExpected)
            throw new InvalidDataException($"StageFlow→CSR oracle mismatch count={count} reached={callReached} enabled={enabled} acquired={reservationAcquired}: expected CSR Cycle {csrExpected}, got {actual["next_" + cycleRow.AddressMember]}.");
        foreach (var r in registrations.Where(r => r.Address != 0xC00))
            if (actual["next_" + r.AddressMember] != inputs["current_" + r.AddressMember])
                throw new InvalidDataException("StageFlow→CSR event modified unrelated CSR " + r.AddressMember);
        integratedVectors++;
    }
    return (vectors, (object)new
    {
        schema = "circuitweave.csr-hwir-software-semantics-crosscheck/v1",
        status = "passed",
        comparison = "independent HWIR interpreter vs separately coded CsrFile dictionary/policy oracle",
        vectors,
        registeredAddresses = registrations.Count,
        addressInputs = addresses.Length,
        privilegeValues = privileges.Length,
        operations = 5,
        stateVariants = 2,
        integratedCallEventVectors = integratedVectors,
        integratedCallEventResult = "passed for body gate, pipeline enable, abstract reservation success/failure and ulong wrap boundary",
        scope = "combinational one-event outputs and next-value muxes; no reservation timing, exception runtime, physical memory, clock, reset, RTL or Golden equivalence"
    });
}
