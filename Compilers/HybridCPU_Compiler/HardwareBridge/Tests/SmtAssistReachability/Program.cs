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

const string ModelRoot = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE";
const string BridgeRoot = @"C:\Users\Yuriy\Desktop\HybridCPU Compilers\Compilers\HybridCPU_Compiler\HardwareBridge";
const string Commit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
const string RelativeSource = @"CloseToHSL\Core\Pipeline\Assist\Runtime\MicroOpScheduler.Assist.cs";
const string MemberId = "M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.TryInjectAssistCandidates()";
if (args.Length == 3 && args[0] == "--publish")
{
    await PublishCandidate(args[1], args[2]);
    return;
}
if (args.Length != 1) throw new ArgumentException("Usage: SmtAssistReachability <existing-output-directory-under-HardwareBridge>");
string output = Path.GetFullPath(args[0]);
if (!output.StartsWith(BridgeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(output))
    throw new InvalidOperationException("Output must be an existing directory under HardwareBridge.");
byte[] source = await File.ReadAllBytesAsync(Path.Combine(ModelRoot, RelativeSource));
string sourceHash = Hash(source);
var toolchain = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
HwValidatedDesign candidate = BridgeHwirSmtAssistRankReachability.Compile(source, Commit, toolchain);
var semantic = Crosscheck(candidate);
byte[] semanticBytes = JsonSerializer.SerializeToUtf8Bytes(new { vectors = semantic.Vectors, status = semantic.Status, oracle = semantic.Oracle, scope = semantic.Scope }, new JsonSerializerOptions { WriteIndented = true });
string sourcePath = Path.GetFullPath(Path.Combine(ModelRoot, RelativeSource));
(int line, int column) = Locate(source, "private int TryInjectAssistCandidates(", "TryInjectAssistCandidates(");
byte[] captureIndex = JsonSerializer.SerializeToUtf8Bytes(new
{
    schema = "circuitweave.assist-source-capture/v1", status = "one-listed-dirty-worktree-file-hash-checked",
    baseCommit = Commit, sourceTreeState = "active-Ise-worktree-dirty; exact listed bytes captured; base commit is not asserted as the byte source",
    scope = "TryInjectAssistCandidates rank3 early-gate and loop-quota projection; not full method/project symbol closure",
    files = new[] { new { path = sourcePath, relativePath = "HybridCPU_ISE/" + RelativeSource.Replace('\\', '/'), sha256 = sourceHash, length = source.LongLength } }
}, new JsonSerializerOptions { WriteIndented = true });
string captureHash = Hash(captureIndex);
byte[] sourceInventory = JsonSerializer.SerializeToUtf8Bytes(new
{
    schema = "circuitweave.source-coverage-index/v1", disposition = "incomplete-unqualified",
    project = "HybridCPU_ISE/HybridCPU_ISE.csproj", configuration = "Debug",
    scope = "One exact active dirty-worktree Assist source file and one source-mapped member; not project closure or full member inventory.",
    semanticReportSha256 = Hash(semanticBytes),
    symbols = new[]
    {
        new { symbol = "global::YAKSys_Hybrid_CPU.Core.MicroOpScheduler", assembly = "CircuitWeave.SmtAssistRankReachability", kind = "Class", memberCount = 1,
            blockingReason = "Single-method source-bound reachability slice only; full scheduler type inventory is published separately.",
            declarations = new[] { new { location = new { file = sourcePath, startLine = line, startColumn = column }, memberIds = new[] { MemberId } } },
            members = new[] { new { id = MemberId, kind = "Method", locations = new[] { new { file = sourcePath, startLine = line, startColumn = column } } } } }
    },
    captureVerification = new { schema = "circuitweave.source-capture-check/v1", status = "producer-reported-listed-inputs-match", semanticReportSha256 = Hash(semanticBytes), sourceCaptureSha256 = captureHash, projectGraphSha256 = "d1545a671ebd657cb5d6e3db4c6ccfff75eaa1cbba3bf5e184f865b0cb39c0d0", checkedFiles = 1, rootProject = "not-captured", scope = "The projectGraphSha256 references the separate selected Debug project graph audit context. SHA-256 and captured bytes cover exactly the listed active dirty-worktree Assist file; this is not project closure." },
    capturedSourceFiles = new[] { new { path = sourcePath, sha256 = sourceHash, length = source.LongLength } }
}, new JsonSerializerOptions { WriteIndented = true });
string inventoryHash = Hash(sourceInventory);
byte[] capturedSources = JsonSerializer.SerializeToUtf8Bytes(new
{
    schema = "circuitweave.captured-sources/v1", inventorySha256 = inventoryHash,
    files = new[] { new { path = sourcePath, bytes = Convert.ToBase64String(source) } }
}, new JsonSerializerOptions { WriteIndented = true });
byte[] mlir = Encoding.UTF8.GetBytes(new HierarchicalCirctMlirLowerer().LowerToMlir(candidate,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>())).Text);
byte[] hwir = new CanonicalHwirSerializer().Serialize(candidate);
await File.WriteAllBytesAsync(Path.Combine(output, "smt-assist-rank-reachability.hwir.json"), hwir);
await File.WriteAllBytesAsync(Path.Combine(output, "smt-assist-rank-reachability.mlir"), mlir);
await File.WriteAllBytesAsync(Path.Combine(output, "smt-assist-rank-reachability-semantic-crosscheck.json"), semanticBytes);
await File.WriteAllBytesAsync(Path.Combine(output, "smt-assist-source-capture-index.json"), captureIndex);
await File.WriteAllBytesAsync(Path.Combine(output, "smt-assist-source-inventory.json"), sourceInventory);
await File.WriteAllBytesAsync(Path.Combine(output, "smt-assist-captured-sources.json"), capturedSources);
await File.WriteAllTextAsync(Path.Combine(output, "smt-assist-source-pins.json"), JsonSerializer.Serialize(new
{
    baseCommit = Commit, worktreeState = "dirty-listed-input-captured", sourceSha256 = sourceHash,
    hwirSha256 = candidate.CanonicalDigest, mlirSha256 = Hash(mlir), semanticCrosscheckSha256 = Hash(semanticBytes),
    vectors = semantic.Vectors, outputs = new[] { "rank3_iteration_reached", "rank3_index_oob_exception_condition" },
    assumptions = new[] { "rank3_vt and prior rank commit bits are explicit inputs; this does not derive candidate class/lane/backpressure outcomes." },
    classification = "combinational source-bound control projection; no RTL/cycle or full Assist scheduler semantics"
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Assist rank3 reachability HWIR PASS: {candidate.CanonicalDigest}; independent gate/quota oracle PASS: {semantic.Vectors} vectors; not RTL/full admission.");

static (int Vectors, string Status, string Oracle, string Scope) Crosscheck(HwValidatedDesign design)
{
    HwModule module = design.Design.Modules.Single(m => m.Id == design.Design.TopModule);
    var drivers = module.Nodes.OfType<HwOutputDriveNode>().ToDictionary(n => n.OutputPort, n => n.Value, StringComparer.Ordinal);
    int vectors = 0;
    uint[] owners = [0, 1, 2, 3, 4, uint.MaxValue];
    foreach (uint owner in owners)
    foreach (uint tail in new uint[] { 0, 1, 3, 4, owner, uint.MaxValue })
    for (int gates = 0; gates < 8; gates++)
    {
        bool enabled = (gates & 1) != 0, slot = (gates & 2) != 0, blocked = (gates & 4) != 0;
        bool entryOpen = enabled && slot && !blocked;
        int[] priorMasks = entryOpen ? [0, 1, 2, 4] : [0];
        foreach (int priorMask in priorMasks)
        {
            var input = new Dictionary<string, BigInteger>(StringComparer.Ordinal)
            {
                ["assist_enabled"] = enabled ? 1 : 0, ["slot_available"] = slot ? 1 : 0,
                ["boundary_blocked"] = blocked ? 1 : 0,
                ["prior_rank0_committed"] = (priorMask & 1) != 0 ? 1 : 0,
                ["prior_rank1_committed"] = (priorMask & 2) != 0 ? 1 : 0,
                ["prior_rank2_committed"] = (priorMask & 4) != 0 ? 1 : 0,
                ["owner_vt"] = owner, ["rank3_vt"] = tail
            };
            var values = new Dictionary<string, BigInteger>(input, StringComparer.Ordinal);
            foreach (HwNode node in module.Nodes)
            {
                switch (node)
                {
                    case HwConstantNode constant:
                        values[node.Id] = Convert.ToInt64(constant.BitPattern, 2);
                        break;
                    case HwOperationNode op:
                        BigInteger a = values[op.Operands[0]];
                        BigInteger b = op.Operands.Count > 1 ? values[op.Operands[1]] : BigInteger.Zero;
                        values[node.Id] = op.Opcode switch
                        {
                            "and" => a & b, "or" => a | b, "xor" => a ^ b,
                            "eq" => a == b ? 1 : 0, "ult64" => a < b ? 1 : 0,
                            "zext" => a, _ => throw new InvalidDataException("Unsupported opcode: " + op.Opcode)
                        };
                        break;
                }
            }
            var actual = module.Ports.Where(p => p.Direction == HwPortDirection.Output)
                .ToDictionary(p => p.Name, p => values[drivers[p.Name]], StringComparer.Ordinal);
            bool expectedReach = entryOpen && priorMask == 0;
            bool expectedOob = expectedReach && tail >= 4 && tail != owner;
            if (actual["rank3_iteration_reached"] != (expectedReach ? 1 : 0) ||
                actual["rank3_index_oob_exception_condition"] != (expectedOob ? 1 : 0))
                throw new InvalidOperationException($"Assist rank3 oracle mismatch owner={owner} rank3={tail:X8} gates={gates} prior={priorMask:X} reach={actual["rank3_iteration_reached"]}/{expectedReach} oob={actual["rank3_index_oob_exception_condition"]}/{expectedOob}.");
            vectors++;
        }
    }
    return (vectors, "passed", "separately coded Assist entry/quota/owner-guard equation", "source-pinned three early returns and AssistBundleQuota=1; prior rank commit bits are abstract inputs constrained to zero or one-hot when loop entry is open; candidate admission outcomes are not modeled");
}

static async Task PublishCandidate(string runDirectory, string workspace)
{
    string run = Path.GetFullPath(runDirectory), target = Path.GetFullPath(workspace);
    if (!Directory.Exists(run)) throw new DirectoryNotFoundException(run);
    Directory.CreateDirectory(target);
    byte[] hwir = await File.ReadAllBytesAsync(Path.Combine(run, "smt-assist-rank-reachability.hwir.json"));
    HwDesign design = new CanonicalHwirSerializer().Deserialize(hwir);
    HwValidatedDesign candidate = new FailClosedHardwareIrValidator().Validate(design).Validated ?? throw new InvalidDataException("Published HWIR no longer validates.");
    byte[] Read(string name) => File.ReadAllBytes(Path.Combine(run, name));
    var supplements = new[]
    {
        ("smt-assist-rank-reachability-mlir", CircuitWeave.Visualization.StudioArtifactKind.Mlir, "smt-assist-rank-reachability.mlir"),
        ("smt-assist-semantic-crosscheck", CircuitWeave.Visualization.StudioArtifactKind.CandidateRunProvenance, "smt-assist-rank-reachability-semantic-crosscheck.json"),
        ("smt-assist-source-inventory", CircuitWeave.Visualization.StudioArtifactKind.SourceInventory, "smt-assist-source-inventory.json"),
        ("smt-assist-captured-sources", CircuitWeave.Visualization.StudioArtifactKind.CapturedSources, "smt-assist-captured-sources.json"),
        ("smt-assist-capture-index", CircuitWeave.Visualization.StudioArtifactKind.CandidateRunProvenance, "smt-assist-source-capture-index.json"),
        ("smt-assist-source-pins", CircuitWeave.Visualization.StudioArtifactKind.CandidateRunProvenance, "smt-assist-source-pins.json")
    }.Select(x => { byte[] b = Read(x.Item3); return new StudioSupplementArtifact(x.Item1, x.Item2, x.Item3, b, Hash(b)); }).ToArray();
    byte[] pins = Read("smt-assist-source-pins.json");
    var stages = new[]
    {
        new CompilationStageRecord(CompilationStage.SourceAdmission, CompilationStageStatus.NotRun, "No project-wide source admission was run."),
        new CompilationStageRecord(CompilationStage.HardwareIrValidation, CompilationStageStatus.Completed, "Standalone 1-module source-bound control projection passed fail-closed HWIR validation."),
        new CompilationStageRecord(CompilationStage.RtlEmission, CompilationStageStatus.NotRun, "MLIR was emitted; RTL export and simulation were not run."),
        new CompilationStageRecord(CompilationStage.IndependentVerification, CompilationStageStatus.Completed, "Independent boolean equation oracle matched the HWIR over constrained gate/quota vectors; no active method execution or RTL equivalence."),
        new CompilationStageRecord(CompilationStage.EvidencePromotion, CompilationStageStatus.NotRun, "StructuralOnly; source and candidate outcome assumptions remain explicit.")
    };
    var request = new StudioCompilerPublicationRequest("hybridcpu-smt-assist-rank-reachability",
        "HybridCPU SMT Assist rank3 loop reachability", "assistreach" + candidate.CanonicalDigest[..16], candidate,
        new HwTargetDescription("CIRCT generic", "combinational", "circt-cli"), stages, Supplements: supplements, SourceDigest: Hash(pins));
    StudioPublicationReceipt receipt = await new StudioSnapshotPublisher().PublishAsync(request, target);
    Console.WriteLine(JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
    var opened = await new StudioWorkspaceLoader().OpenAsync(Path.Combine(target, "studio.project.json"));
    if (!opened.Success || opened.Value is null) throw new InvalidDataException("Studio API refused Assist snapshot: " + opened.Error);
    var session = opened.Value;
    if (session.Graph.Nodes.Count(n => n.Kind == CircuitWeave.Studio.Contracts.StudioGraphNodeKind.Module) != 1 ||
        session.Graph.Trust != CircuitWeave.Studio.Contracts.StudioDocumentTrust.StructuralOnly)
        throw new InvalidDataException("Unexpected Assist Studio module count or trust.");
    var nav = GraphNavigationIndex.Create(session.Graph);
    if (nav.GetRoots(0, 8).Items.Length == 0 || nav.GetChildren(nav.TopModuleId, 0, 16).Items.Length == 0)
        throw new InvalidDataException("Assist Studio hierarchy/root paging failed.");
    var mapping = session.SourceMap?.Mappings.FirstOrDefault(m => m.Source.Commit == Commit && m.Source.RelativePath.EndsWith("MicroOpScheduler.Assist.cs", StringComparison.Ordinal))
        ?? throw new InvalidDataException("Assist source map is missing.");
    bool mappedNodeExists = nav.TryGetNode(mapping.GraphNodeId, out _);
    string? excerpt = CapturedNodeSource.Excerpt(session, mapping.Source);
    if (!mappedNodeExists || string.IsNullOrWhiteSpace(excerpt))
        throw new InvalidDataException($"Assist source mapping/captured excerpt failed: node={mappedNodeExists}, excerptLength={excerpt?.Length ?? 0}.");
    Console.WriteLine($"Studio API PASS: {session.Graph.Nodes.Count} nodes, {session.Graph.Edges.Count} edges; roots/children and captured Assist excerpt resolved; trust={session.Graph.Trust}");
}

static (int Line, int Column) Locate(byte[] source, string member, string columnToken)
{
    string[] lines = Encoding.UTF8.GetString(source).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    int index = Array.FindIndex(lines, line => line.Contains(member, StringComparison.Ordinal));
    if (index < 0) throw new InvalidDataException("Source member marker missing: " + member);
    return (index + 1, lines[index].IndexOf(columnToken, StringComparison.Ordinal) + 1);
}

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
