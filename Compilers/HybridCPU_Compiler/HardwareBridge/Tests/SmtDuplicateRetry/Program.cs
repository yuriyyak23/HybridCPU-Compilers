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
using CircuitWeave.Studio.GraphNavigation;
using CircuitWeave.Studio.Integration;
using CircuitWeave.Visualization.Publishing;
using HybridCPU.Compiler.HardwareBridge.Hwir;

const string ModelRoot = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE";
const string Commit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
const string SmtRel = @"CloseToHSL\Core\Pipeline\Scheduling\Smt\MicroOpScheduler.SMT.cs";
const string AdmissionRel = @"CloseToHSL\Core\Pipeline\Scheduling\Admission\MicroOpScheduler.Admission.cs";
string[] names = ["smt-duplicate-retry.hwir.json", "smt-duplicate-retry.mlir", "smt-duplicate-retry-crosscheck.json", "smt-duplicate-retry-source-inventory.json", "smt-duplicate-retry-captured-sources.json", "smt-duplicate-retry-source-capture-index.json", "smt-duplicate-retry-source-pins.json"];
if (args.Length == 3 && args[0] == "--publish") { await Publish(args[1], args[2]); return; }
if (args.Length != 1) throw new ArgumentException("Usage: SmtDuplicateRetry <existing-output-directory-under-HardwareBridge> | --publish <run-dir> <existing-workspace>");
string output = Path.GetFullPath(args[0]);
string bridgeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
if (!Directory.Exists(output) || !output.StartsWith(bridgeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Output must be an existing directory under HardwareBridge.");
byte[] smt = File.ReadAllBytes(Path.Combine(ModelRoot, SmtRel));
byte[] admission = File.ReadAllBytes(Path.Combine(ModelRoot, AdmissionRel));
var toolchain = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
HwValidatedDesign candidate = BridgeHwirSmtDuplicateRetry.Compile(smt, admission, Commit, toolchain);
var mlir = new HierarchicalCirctMlirLowerer().LowerToMlir(candidate, new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>()));
byte[] smtBytes = smt, admissionBytes = admission;
string smtPath = Path.Combine(ModelRoot, SmtRel), admissionPath = Path.Combine(ModelRoot, AdmissionRel);
var sourcePositions = FindMethodPositions(smtBytes, admissionBytes);
byte[] semantic = JsonSerializer.SerializeToUtf8Bytes(Check(candidate), new JsonSerializerOptions { WriteIndented = true });
string semanticHash = Hash(semantic);
string captureIndexJson = JsonSerializer.Serialize(new
{
    schema = "circuitweave.source-capture-check/v1", status = "listed-active-input-bytes-checked", commit = Commit,
    rootProject = "not-captured", scope = "Two exact active dirty-worktree source files; one SMT method is graph-mapped, admission method is syntax-guarded and its outcomes stay abstract.",
    checkedFiles = 2, smtSha256 = Hash(smt), admissionSha256 = Hash(admission)
}, new JsonSerializerOptions { WriteIndented = true });
byte[] captureIndex = Encoding.UTF8.GetBytes(captureIndexJson);
var sourceFiles = new[]
{
    new { path = smtPath, sha256 = Hash(smt), length = smt.LongLength },
    new { path = admissionPath, sha256 = Hash(admission), length = admission.LongLength }
};
var inventory = new
{
    schema = "circuitweave.source-coverage-index/v1", disposition = "incomplete-unqualified",
    project = "HybridCPU_ISE/HybridCPU_ISE.csproj", configuration = "Debug",
    scope = "Source-bound SMT loop and TryClassAdmission branch abstract projection; not Roslyn project closure or full scheduler semantics.",
    semanticReportSha256 = semanticHash,
    symbols = new[]
    {
        new { symbol = "global::YAKSys_Hybrid_CPU.Core.MicroOpScheduler", assembly = "CircuitWeave.SmtDuplicateRetry", kind = "Class", memberCount = 1,
            blockingReason = "One source-mapped scheduler method only; admission is a syntax-guarded abstract boundary and the full active symbol inventory is published separately.",
            declarations = new[] {
                new { location = new { file = smtPath, startLine = sourcePositions.SmtLine, startColumn = sourcePositions.SmtColumn }, memberIds = new[] { "M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.PackBundleIntraCoreSmt()" } }
            },
            members = new[] {
                new { id = "M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.PackBundleIntraCoreSmt()", kind = "Method", locations = new[] { new { file = smtPath, startLine = sourcePositions.SmtLine, startColumn = sourcePositions.SmtColumn } } }
            }
        }
    },
    captureVerification = new { schema = "circuitweave.source-capture-check/v1", status = "producer-reported-listed-inputs-match", semanticReportSha256 = semanticHash, sourceCaptureSha256 = Hash(captureIndex), projectGraphSha256 = new string('0', 64), checkedFiles = 2, rootProject = "not-captured", scope = "Exact byte hashes and captured bytes cover only listed active SMT and admission files; zero graph digest explicitly means project graph was not captured." },
    capturedSourceFiles = sourceFiles
};
byte[] inventoryBytes = JsonSerializer.SerializeToUtf8Bytes(inventory, new JsonSerializerOptions { WriteIndented = true });
string inventoryHash = Hash(inventoryBytes);
byte[] captured = JsonSerializer.SerializeToUtf8Bytes(new { schema = "circuitweave.captured-sources/v1", inventorySha256 = inventoryHash, files = new[] { new { path = smtPath, bytes = Convert.ToBase64String(smt) }, new { path = admissionPath, bytes = Convert.ToBase64String(admission) } } }, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllBytesAsync(Path.Combine(output, names[0]), new CanonicalHwirSerializer().Serialize(candidate));
await File.WriteAllTextAsync(Path.Combine(output, names[1]), mlir.Text, new UTF8Encoding(false));
await File.WriteAllBytesAsync(Path.Combine(output, names[2]), semantic);
await File.WriteAllBytesAsync(Path.Combine(output, names[3]), inventoryBytes);
await File.WriteAllBytesAsync(Path.Combine(output, names[4]), captured);
await File.WriteAllBytesAsync(Path.Combine(output, names[5]), captureIndex);
await File.WriteAllTextAsync(Path.Combine(output, names[6]), JsonSerializer.Serialize(new
{
    modelCommit = Commit, modelTreeState = "dirty-listed-inputs-captured", sourceHashes = new { smt = Hash(smt), admission = Hash(admission) },
    module = candidate.Design.TopModule, hwirSha256 = candidate.CanonicalDigest, mlirSha256 = mlir.ContentDigest,
    semanticCrosscheckSha256 = semanticHash, vectors = 256,
    inputs = new[] { "duplicate_entry_present", "rank3_candidate_present", "attempt0_class_admitted", "attempt0_lane_materialized", "attempt1_class_admitted", "attempt1_lane_materialized", "attempt0_template_budget_consumed", "attempt1_template_budget_consumed" },
    limits = "Fixed source-guarded TypedSlotEnabled branch; candidate admission and lane outcomes are explicit abstract inputs. Only typed-slot retry/clear ordering is source-bound. No legacy branch, runtime policy, clocked state, RTL, or Golden equivalence."
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"HWIR {candidate.CanonicalDigest}; MLIR {mlir.ContentDigest}; independent branch oracle PASS: 256 vectors; StructuralOnly source-bounded abstract control projection.");

static object Check(HwValidatedDesign design)
{
    HwModule module = design.Design.Modules.Single();
    var drives = module.Nodes.OfType<HwOutputDriveNode>().ToDictionary(n => n.OutputPort, n => n.Value, StringComparer.Ordinal);
    int vectors = 0;
    for (int bits = 0; bits < 256; bits++)
    {
        bool[] input = Enumerable.Range(0, 8).Select(i => (bits & (1 << i)) != 0).ToArray();
        var values = module.Ports.Where(p => p.Direction == HwPortDirection.Input).Select((p, i) => (p.Name, Value: new BigInteger(input[i] ? 1 : 0))).ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
        BigInteger Signal(string name)
        {
            if (values.TryGetValue(name, out var v)) return v;
            HwNode n = module.Nodes.Single(x => x.Id == name);
            BigInteger result = n switch
            {
                HwConstantNode c => Convert.ToInt64(c.BitPattern, 2),
                HwOutputDriveNode => throw new InvalidDataException("Unexpected node lookup."),
                HwOperationNode op => op.Opcode switch
                {
                    "and" => Signal(op.Operands[0]) & Signal(op.Operands[1]),
                    "or" => Signal(op.Operands[0]) | Signal(op.Operands[1]),
                    "xor" => Signal(op.Operands[0]) ^ Signal(op.Operands[1]),
                    "add" => Signal(op.Operands[0]) + Signal(op.Operands[1]),
                    "zext" or "trunc" => Signal(op.Operands[0]),
                    _ => throw new InvalidDataException("Unsupported opcode " + op.Opcode)
                },
                _ => throw new InvalidDataException("Unsupported HWIR node " + n.GetType().Name)
            };
            values[name] = result; return result;
        }
        bool duplicate = input[0], present = input[1];
        bool first = duplicate && present;
        bool commit0 = input[2] && input[3];
        bool retry = first && !commit0;
        bool commit1 = retry && input[4] && input[5];
        bool reject0 = first && !commit0;
        bool reject1 = retry && !commit1;
        int rejectCount = (reject0 ? 1 : 0) + (reject1 ? 1 : 0);
        int budgetCount = (first && input[6] ? 1 : 0) + (retry && input[7] ? 1 : 0);
        var expected = new Dictionary<string, BigInteger>(StringComparer.Ordinal)
        {
            ["first_attempt_reached"] = first ? 1 : 0, ["duplicate_retry_reached"] = retry ? 1 : 0,
            ["first_attempt_committed"] = commit0 ? 1 : 0, ["retry_attempt_committed"] = commit1 ? 1 : 0,
            ["port_cleared_after_loop"] = commit0 || commit1 ? 1 : 0, ["nomination_cleared_after_loop"] = commit0 || commit1 ? 1 : 0,
            ["attempt0_rejected"] = reject0 ? 1 : 0, ["attempt1_rejected"] = reject1 ? 1 : 0,
            ["rejection_events"] = rejectCount, ["template_budget_consumptions"] = budgetCount
        };
        foreach (var (name, value) in expected)
            if (Signal(drives[name]) != value) throw new InvalidOperationException($"Mismatch vector={bits}, output={name}, got={Signal(drives[name])}, expected={value}.");
        vectors++;
    }
    return new { vectors, oracle = "independent attempt-state equations over all boolean input combinations", status = "passed", scope = "Fixed source-guarded TypedSlotEnabled branch; abstract class admission/lane outcomes; first commit clears candidate and makes duplicate skip; failure preserves candidate for retry; budget and rejection effects gated by reached attempt. Not full policy/temporal/RTL/Golden equivalence." };
}

static async Task Publish(string runDirectory, string workspace)
{
    string run = Path.GetFullPath(runDirectory), target = Path.GetFullPath(workspace);
    if (!Directory.Exists(run) || !Directory.Exists(target)) throw new DirectoryNotFoundException("Run and existing artifact workspace are required.");
    var serializer = new CanonicalHwirSerializer();
    HwDesign design = serializer.Deserialize(await File.ReadAllBytesAsync(Path.Combine(run, "smt-duplicate-retry.hwir.json")));
    HwValidatedDesign candidate = new FailClosedHardwareIrValidator().Validate(design).Validated ?? throw new InvalidDataException("HWIR failed revalidation.");
    byte[] Read(string name) => File.ReadAllBytes(Path.Combine(run, name));
    var supplements = new[]
    {
        ("smt-duplicate-retry-mlir", StudioArtifactKind.Mlir, "smt-duplicate-retry.mlir"),
        ("smt-duplicate-retry-source-inventory", StudioArtifactKind.SourceInventory, "smt-duplicate-retry-source-inventory.json"),
        ("smt-duplicate-retry-captured-sources", StudioArtifactKind.CapturedSources, "smt-duplicate-retry-captured-sources.json"),
        ("smt-duplicate-retry-source-capture-index", StudioArtifactKind.CandidateRunProvenance, "smt-duplicate-retry-source-capture-index.json"),
        ("smt-duplicate-retry-crosscheck", StudioArtifactKind.CandidateRunProvenance, "smt-duplicate-retry-crosscheck.json"),
        ("smt-duplicate-retry-source-pins", StudioArtifactKind.CandidateRunProvenance, "smt-duplicate-retry-source-pins.json")
    }.Select(x => { byte[] b = Read(x.Item3); return new StudioSupplementArtifact(x.Item1, x.Item2, x.Item3, b, Hash(b)); }).ToArray();
    var stages = new[]
    {
        new CompilationStageRecord(CompilationStage.BindingAuthorization, CompilationStageStatus.NotRun, "No binding authorization stage."),
        new CompilationStageRecord(CompilationStage.SymbolResolution, CompilationStageStatus.NotRun, "No project-wide symbol closure."),
        new CompilationStageRecord(CompilationStage.SourceAdmission, CompilationStageStatus.NotRun, "Only listed active source files pinned."),
        new CompilationStageRecord(CompilationStage.HardwareIrValidation, CompilationStageStatus.Completed, "Fail-closed single-module HWIR validation passed."),
        new CompilationStageRecord(CompilationStage.RtlEmission, CompilationStageStatus.NotRun, "No RTL emitted."),
        new CompilationStageRecord(CompilationStage.IndependentVerification, CompilationStageStatus.Completed, "Independent combinational branch oracle passed 256 input assignments for the source-guarded TypedSlotEnabled path; abstract admission outcomes, no cycle/Golden equivalence."),
        new CompilationStageRecord(CompilationStage.EvidencePromotion, CompilationStageStatus.NotRun, "StructuralOnly candidate.")
    };
    var request = new StudioCompilerPublicationRequest("hybridcpu-smt-duplicate-retry", "HybridCPU SMT duplicate rank retry projection", "smtdupretry" + candidate.CanonicalDigest[..12], candidate,
        new HwTargetDescription("CIRCT generic", "combinational", "circt-cli"), stages, Supplements: supplements, SourceDigest: Hash(Read("smt-duplicate-retry-source-pins.json")));
    var receipt = await new StudioSnapshotPublisher().PublishAsync(request, target);
    var opened = await new StudioWorkspaceLoader().OpenAsync(Path.Combine(target, "studio.project.json"));
    if (!opened.Success || opened.Value is null) throw new InvalidDataException("Studio refused snapshot: " + opened.Error);
    var session = opened.Value;
    if (session.Graph.Trust != CircuitWeave.Studio.Contracts.StudioDocumentTrust.StructuralOnly) throw new InvalidDataException("Unexpected Studio trust.");
    var nav = GraphNavigationIndex.Create(session.Graph);
    var roots = nav.GetRoots(0, 8);
    var page1 = nav.GetChildren(nav.TopModuleId, 0, 20);
    var page2 = nav.GetChildren(nav.TopModuleId, 20, 20);
    if (roots.TotalCount == 0 || !page1.HasMore || page1.Items.Length != 20 || page2.Items.Length == 0)
        throw new InvalidDataException("Studio hierarchy navigation/pagination failed.");
    if (!session.Graph.Nodes.Any(n => n.DisplayName.Contains("retry_attempt_committed", StringComparison.Ordinal)))
        throw new InvalidDataException("Studio graph search term was not present.");
    var mapping = session.SourceMap?.Mappings.FirstOrDefault(m => m.Source.Commit == Commit && m.Source.RelativePath.Contains("MicroOpScheduler.SMT.cs", StringComparison.Ordinal)) ?? throw new InvalidDataException("SMT source-map missing.");
    if (CapturedNodeSource.Excerpt(session, mapping.Source)?.Contains("PackBundleIntraCoreSmt", StringComparison.Ordinal) != true) throw new InvalidDataException("Captured SMT source excerpt missing.");
    Console.WriteLine(JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Studio API PASS: {session.Graph.Nodes.Count(n => n.Kind == CircuitWeave.Studio.Contracts.StudioGraphNodeKind.Module)} module(s), {session.Graph.Nodes.Count} nodes, {session.Graph.Edges.Count} edges; source excerpt resolved; trust={session.Graph.Trust}.");
}

static (int SmtLine, int SmtColumn, int AdmissionLine, int AdmissionColumn) FindMethodPositions(byte[] smt, byte[] admission)
{
    var smtRoot = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(smt)).GetRoot();
    var admissionRoot = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(admission)).GetRoot();
    var smtPos = smtRoot.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "PackBundleIntraCoreSmt").Identifier.GetLocation().GetLineSpan().StartLinePosition;
    var admPos = admissionRoot.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "TryClassAdmission").Identifier.GetLocation().GetLineSpan().StartLinePosition;
    return (smtPos.Line + 1, smtPos.Character + 1, admPos.Line + 1, admPos.Character + 1);
}

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
