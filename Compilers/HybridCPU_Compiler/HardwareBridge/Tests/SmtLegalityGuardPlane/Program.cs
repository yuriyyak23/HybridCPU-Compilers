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
using ArtifactKind = CircuitWeave.Visualization.StudioArtifactKind;
using GraphNodeKind = CircuitWeave.Studio.Contracts.StudioGraphNodeKind;
using DocumentTrust = CircuitWeave.Studio.Contracts.StudioDocumentTrust;

const string ModelRoot = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE";
const string BridgeRoot = @"C:\Users\Yuriy\Desktop\HybridCPU Compilers\Compilers\HybridCPU_Compiler\HardwareBridge";
const string Commit = BridgeHwirSmtLegalityGuardPlane.Commit;
const string LegalityRel = @"CloseToHSL\Core\Pipeline\Safety\SafetyVerifier.SmtLegality.cs";
const string GuardsRel = @"CloseToHSL\Core\Pipeline\Safety\SafetyVerifier.Guards.cs";
if (args.Length == 3 && args[0] == "--publish") { await Publish(args[1], args[2]); return; }
if (args.Length != 1) throw new ArgumentException("Usage: SmtLegalityGuardPlane <existing-output-directory-under-HardwareBridge>");
string output = Path.GetFullPath(args[0]);
if (!output.StartsWith(BridgeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(output))
    throw new InvalidOperationException("Output must be an existing directory under HardwareBridge.");
byte[] legality = await File.ReadAllBytesAsync(Path.Combine(ModelRoot, LegalityRel));
byte[] guards = await File.ReadAllBytesAsync(Path.Combine(ModelRoot, GuardsRel));
var toolchain = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
HwValidatedDesign design = BridgeHwirSmtLegalityGuardPlane.Compile(legality, guards, toolchain);
var vectors = BuildVectors();
int passed = Crosscheck(design, vectors);
byte[] mlir = Encoding.UTF8.GetBytes(new HierarchicalCirctMlirLowerer().LowerToMlir(design,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>())).Text);
byte[] hwir = new CanonicalHwirSerializer().Serialize(design);
byte[] tb = Encoding.UTF8.GetBytes(BuildTestbench(vectors));
var pins = new
{
    schema = "circuitweave.smt-legality-guard-plane-pins/v1", baseCommit = Commit,
    treeState = "dirty-model-worktree; exact listed input bytes captured; commit is base identity, not asserted byte source",
    sources = new[]
    {
        new { path = Path.Combine(ModelRoot, LegalityRel), relativePath = "HybridCPU_ISE/" + LegalityRel.Replace('\\','/'), sha256 = Hash(legality), bytes = legality.LongLength },
        new { path = Path.Combine(ModelRoot, GuardsRel), relativePath = "HybridCPU_ISE/" + GuardsRel.Replace('\\','/'), sha256 = Hash(guards), bytes = guards.LongLength }
    },
    hwirSha256 = design.CanonicalDigest, hwirArtifactSha256 = Hash(hwir), mlirSha256 = Hash(mlir), testbenchSha256 = Hash(tb), vectors = passed,
    assumptions = new[] { "phase_key_valid and phase_template_matches are explicit inputs; cache-key equality and cache refresh are not modeled", "selected_certificate_allowed is the explicit CanInjectWithReason result for the selected replay or structural certificate", "no clock/reset or scheduler-state timing contract is claimed" }
};
byte[] pinBytes = JsonSerializer.SerializeToUtf8Bytes(pins, new JsonSerializerOptions { WriteIndented = true });
byte[] captureIndex = JsonSerializer.SerializeToUtf8Bytes(new
{
    schema = "circuitweave.assist-source-capture/v1", status = "two-listed-dirty-worktree-files-hash-checked",
    baseCommit = Commit, sourceTreeState = pins.treeState, scope = "Exact listed SafetyVerifier source bytes; this is not project closure.",
    files = pins.sources.Select(s => new { path = s.path, relativePath = s.relativePath, sha256 = s.sha256, length = s.bytes }).ToArray()
}, new JsonSerializerOptions { WriteIndented = true });
string captureHash = Hash(captureIndex);
var semantic = new { schema = "circuitweave.smt-legality-guard-plane-crosscheck/v1", status = "passed", vectors = passed,
    oracle = "independently coded C# guard equations against evaluated HWIR", scope = "boundary precedence; known-owner context mismatch; kernel/domain certificate behavior; replay-vs-structural certificate selection gates; certificate allow result is supplied input" };
byte[] semanticBytes = JsonSerializer.SerializeToUtf8Bytes(semantic, new JsonSerializerOptions { WriteIndented = true });
byte[] sourceInventory = JsonSerializer.SerializeToUtf8Bytes(new
{
    schema = "circuitweave.source-coverage-index/v1", disposition = "incomplete-unqualified",
    project = "HybridCPU_ISE/HybridCPU_ISE.csproj", configuration = "Debug", defines = new[] { "TRACE", "TESTING", "DEBUG" },
    scope = "SafetyVerifier.EvaluateSmtLegality guard order plus domain-isolation probe; certificate content and runtime cache lifecycle are abstract inputs.",
    semanticReportSha256 = Hash(semanticBytes), checkedFiles = 2, commit = Commit, modelTreeState = pins.treeState, symbols = new[]
    {
        new { symbol = "global::YAKSys_Hybrid_CPU.Core.SafetyVerifier", assembly = "CircuitWeave.SmtLegalityGuardPlane", kind = "Class", memberCount = 4,
            blockingReason = "Source-bounded guard plane only; replay cache refresh and certificate content remain abstract; no full type inventory.",
            declarations = new[]
            {
                new { location = new { file = Path.Combine(ModelRoot, LegalityRel), startLine = Locate(legality, "partial class SafetyVerifier").Line, startColumn = Locate(legality, "partial class SafetyVerifier").Column }, memberIds = new[] { "M:YAKSys_Hybrid_CPU.Core.SafetyVerifier.EvaluateSmtLegality(" } },
                new { location = new { file = Path.Combine(ModelRoot, GuardsRel), startLine = Locate(guards, "partial class SafetyVerifier").Line, startColumn = Locate(guards, "partial class SafetyVerifier").Column }, memberIds = new[] { "M:YAKSys_Hybrid_CPU.Core.SafetyVerifier.EvaluateSmtBoundaryGuard(", "M:YAKSys_Hybrid_CPU.Core.SafetyVerifier.TryRejectSmtOwnerDomainGuard(", "M:YAKSys_Hybrid_CPU.Core.SafetyVerifier.EvaluateDomainIsolationProbe(" } }
            },
            members = new[]
            {
                new { id = "M:YAKSys_Hybrid_CPU.Core.SafetyVerifier.EvaluateSmtLegality(", kind = "Method", locations = new[] { new { file = Path.Combine(ModelRoot, LegalityRel), startLine = Locate(legality, "EvaluateSmtLegality(").Line, startColumn = Locate(legality, "EvaluateSmtLegality(").Column } } },
                new { id = "M:YAKSys_Hybrid_CPU.Core.SafetyVerifier.EvaluateSmtBoundaryGuard(", kind = "Method", locations = new[] { new { file = Path.Combine(ModelRoot, GuardsRel), startLine = Locate(guards, "internal LegalityDecision EvaluateSmtBoundaryGuard(").Line, startColumn = Locate(guards, "internal LegalityDecision EvaluateSmtBoundaryGuard(").Column } } },
                new { id = "M:YAKSys_Hybrid_CPU.Core.SafetyVerifier.TryRejectSmtOwnerDomainGuard(", kind = "Method", locations = new[] { new { file = Path.Combine(ModelRoot, GuardsRel), startLine = Locate(guards, "TryRejectSmtOwnerDomainGuard(").Line, startColumn = Locate(guards, "TryRejectSmtOwnerDomainGuard(").Column } } },
                new { id = "M:YAKSys_Hybrid_CPU.Core.SafetyVerifier.EvaluateDomainIsolationProbe(", kind = "Method", locations = new[] { new { file = Path.Combine(ModelRoot, GuardsRel), startLine = Locate(guards, "private DomainIsolationProbeResult EvaluateDomainIsolationProbe(ulong").Line, startColumn = Locate(guards, "private DomainIsolationProbeResult EvaluateDomainIsolationProbe(ulong").Column } } }
            }
        }
    },
    captureVerification = new { schema = "circuitweave.source-capture-check/v1", status = "producer-reported-listed-inputs-match", semanticReportSha256 = Hash(semanticBytes), sourceCaptureSha256 = captureHash, projectGraphSha256 = "d1545a671ebd657cb5d6e3db4c6ccfff75eaa1cbba3bf5e184f865b0cb39c0d0", checkedFiles = 2, rootProject = "not-captured", scope = "Project graph SHA is the separately audited Debug compile closure; SHA and capture index attest these two exact dirty-worktree inputs only, not full project closure." },
    capturedSourceFiles = pins.sources.Select(s => new { path = s.path, sha256 = s.sha256, length = s.bytes }).ToArray(), sourcePinsSha256 = Hash(pinBytes)
}, new JsonSerializerOptions { WriteIndented = true });
string inventoryHash = Hash(sourceInventory);
byte[] captured = JsonSerializer.SerializeToUtf8Bytes(new
{
    schema = "circuitweave.captured-sources/v1", inventorySha256 = inventoryHash,
    files = new[]
    {
        new { path = Path.Combine(ModelRoot, LegalityRel), bytes = Convert.ToBase64String(legality) },
        new { path = Path.Combine(ModelRoot, GuardsRel), bytes = Convert.ToBase64String(guards) }
    }
}, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllBytesAsync(Path.Combine(output, "smt-legality-guard-plane.hwir.json"), hwir);
await File.WriteAllBytesAsync(Path.Combine(output, "smt-legality-guard-plane.mlir"), mlir);
await File.WriteAllBytesAsync(Path.Combine(output, "smt-legality-guard-plane.tb.sv"), tb);
await File.WriteAllBytesAsync(Path.Combine(output, "smt-legality-guard-plane-source-pins.json"), pinBytes);
await File.WriteAllBytesAsync(Path.Combine(output, "smt-legality-guard-plane-source-inventory.json"), sourceInventory);
await File.WriteAllBytesAsync(Path.Combine(output, "smt-legality-guard-plane-captured-sources.json"), captured);
await File.WriteAllBytesAsync(Path.Combine(output, "smt-legality-guard-plane-source-capture-index.json"), captureIndex);
await File.WriteAllBytesAsync(Path.Combine(output, "smt-legality-guard-plane-semantic-crosscheck.json"), semanticBytes);
Console.WriteLine($"SMT legality guard-plane HWIR PASS: {design.CanonicalDigest}; independent oracle PASS: {passed} vectors; no certificate or scheduler lifecycle proof.");

static List<Case> BuildVectors()
{
    ulong[] domains = [0, 1, 2, 3, 4, 8, 0x8000000000000000, ulong.MaxValue];
    var result = new List<Case>();
    foreach (int boundary in Bits()) foreach (int known in Bits()) foreach (int equal in Bits())
    foreach (int kernel in Bits()) foreach (ulong candidateDomain in domains) foreach (ulong pod in domains)
    foreach (int phaseValid in Bits()) foreach (int matches in Bits()) foreach (int certAllowed in Bits())
        result.Add(new(boundary, known, equal != 0 ? 9u : 10u, 9u, candidateDomain, pod, kernel, phaseValid, matches, certAllowed));
    // Exercise every pair of distinct 64-bit one-hot domains in the actual mask predicate.
    for (int i = 0; i < 64; i++) for (int j = 0; j < 64; j++)
        result.Add(new(0, 0, 0, 0, 1UL << i, 1UL << j, 1, 1, 1, 1));
    return result;
}

static int[] Bits() => [0, 1];

static int Crosscheck(HwValidatedDesign design, IReadOnlyList<Case> vectors)
{
    HwModule module = design.Design.Modules.Single(m => m.Id == design.Design.TopModule);
    var drives = module.Nodes.OfType<HwOutputDriveNode>().ToDictionary(n => n.OutputPort, n => n.Value, StringComparer.Ordinal);
    foreach (Case c in vectors)
    {
        Dictionary<string, BigInteger> values = Evaluate(module, c);
        bool contextMismatch = c.OwnerKnown != 0 && c.OwnerContext != c.CandidateContext;
        bool domainMismatch;
        if (c.CandidateDomain == 0) domainMismatch = c.KernelIsolation != 0 && c.OwnerDomain != 0;
        else domainMismatch = c.OwnerDomain != 0 && (c.CandidateDomain & c.OwnerDomain) == 0;
        bool ownerReject = c.Boundary == 0 && contextMismatch;
        bool domainReject = c.Boundary == 0 && !contextMismatch && domainMismatch;
        bool checkedCertificate = c.Boundary == 0 && !contextMismatch && !domainMismatch;
        bool replaySelected = checkedCertificate && c.PhaseValid != 0 && c.TemplateMatches != 0;
        var expected = new Dictionary<string, BigInteger>
        {
            ["boundary_reject"] = c.Boundary, ["owner_context_reject"] = ownerReject ? 1 : 0,
            ["domain_reject"] = domainReject ? 1 : 0, ["certificate_checked"] = checkedCertificate ? 1 : 0,
            ["replay_certificate_selected"] = replaySelected ? 1 : 0,
            ["structural_certificate_selected"] = checkedCertificate && !replaySelected ? 1 : 0,
            ["legality_allowed"] = checkedCertificate && c.CertificateAllowed != 0 ? 1 : 0
        };
        foreach (var pair in expected)
            if (values[drives[pair.Key]] != pair.Value)
                throw new InvalidOperationException($"Oracle mismatch at {pair.Key}; vector {c} actual={values[drives[pair.Key]]} expected={pair.Value}.");
    }
    return vectors.Count;
}

static Dictionary<string, BigInteger> Evaluate(HwModule module, Case c)
{
    var v = new Dictionary<string, BigInteger>(StringComparer.Ordinal)
    {
        ["boundary_blocked"] = c.Boundary, ["owner_context_known"] = c.OwnerKnown,
        ["owner_context"] = c.OwnerContext, ["candidate_owner_context"] = c.CandidateContext,
        ["candidate_domain"] = c.CandidateDomain, ["owner_domain_certificate"] = c.OwnerDomain,
        ["kernel_domain_isolation"] = c.KernelIsolation, ["phase_key_valid"] = c.PhaseValid,
        ["phase_template_matches"] = c.TemplateMatches, ["selected_certificate_allowed"] = c.CertificateAllowed
    };
    foreach (HwNode node in module.Nodes)
    {
        if (node is HwConstantNode k) v[node.Id] = ParseBits(k.BitPattern);
        else if (node is HwOperationNode op)
        {
            BigInteger a = v[op.Operands[0]], b = op.Operands.Count > 1 ? v[op.Operands[1]] : BigInteger.Zero;
            v[node.Id] = op.Opcode switch { "and" => a & b, "or" => a | b, "xor" => a ^ b, "eq" => a == b ? 1 : 0,
                _ => throw new InvalidDataException("Unsupported evaluator op " + op.Opcode) };
        }
        else if (node is HwMuxNode mux) v[node.Id] = v[mux.Condition] != 0 ? v[mux.WhenTrue] : v[mux.WhenFalse];
    }
    return v;
}

static string BuildTestbench(IReadOnlyList<Case> vectors)
{
    var b = new StringBuilder();
    b.AppendLine("module smt_legality_guard_plane_tb;");
    b.AppendLine("reg boundary_blocked, owner_context_known, kernel_domain_isolation, phase_key_valid, phase_template_matches, selected_certificate_allowed;");
    b.AppendLine("reg [31:0] owner_context, candidate_owner_context;"); b.AppendLine("reg [63:0] candidate_domain, owner_domain_certificate;");
    b.AppendLine("wire boundary_reject, owner_context_reject, domain_reject, certificate_checked, replay_certificate_selected, structural_certificate_selected, legality_allowed;");
    b.AppendLine("smt_legality_guard_plane_projection dut(.*);"); b.AppendLine("integer cases; initial begin cases=0;");
    foreach (Case c in vectors)
    {
        b.Append("boundary_blocked=1'b").Append(c.Boundary).Append("; owner_context_known=1'b").Append(c.OwnerKnown)
         .Append("; owner_context=32'h").Append(c.OwnerContext.ToString("x8")).Append("; candidate_owner_context=32'h").Append(c.CandidateContext.ToString("x8"))
         .Append("; candidate_domain=64'h").Append(c.CandidateDomain.ToString("x16")).Append("; owner_domain_certificate=64'h").Append(c.OwnerDomain.ToString("x16"))
         .Append("; kernel_domain_isolation=1'b").Append(c.KernelIsolation).Append("; phase_key_valid=1'b").Append(c.PhaseValid)
         .Append("; phase_template_matches=1'b").Append(c.TemplateMatches).Append("; selected_certificate_allowed=1'b").Append(c.CertificateAllowed).AppendLine("; #1;");
        b.Append("if ({boundary_reject,owner_context_reject,domain_reject,certificate_checked,replay_certificate_selected,structural_certificate_selected,legality_allowed} !== 7'b")
         .Append(ExpectedBits(c)).AppendLine(") $fatal(1); cases=cases+1;");
    }
    b.Append("$display(\"PASS SMT legality guard-plane vectors: %0d\", cases); $finish; end endmodule");
    return b.ToString();
}

static string ExpectedBits(Case c)
{
    bool ctx = c.OwnerKnown != 0 && c.OwnerContext != c.CandidateContext;
    bool dom = c.CandidateDomain == 0 ? c.KernelIsolation != 0 && c.OwnerDomain != 0 : c.OwnerDomain != 0 && (c.CandidateDomain & c.OwnerDomain) == 0;
    bool owner = c.Boundary == 0 && ctx, domain = c.Boundary == 0 && !ctx && dom;
    bool checkedCert = c.Boundary == 0 && !ctx && !dom, replay = checkedCert && c.PhaseValid != 0 && c.TemplateMatches != 0;
    bool structural = checkedCert && !replay, allowed = checkedCert && c.CertificateAllowed != 0;
    return string.Concat(new[] { c.Boundary != 0, owner, domain, checkedCert, replay, structural, allowed }.Select(x => x ? '1' : '0'));
}

static async Task Publish(string runDirectory, string workspace)
{
    string run = Path.GetFullPath(runDirectory), target = Path.GetFullPath(workspace);
    if (!Directory.Exists(run) || !Directory.Exists(target)) throw new DirectoryNotFoundException("Run and existing workspace required.");
    byte[] hwir = await File.ReadAllBytesAsync(Path.Combine(run, "smt-legality-guard-plane.hwir.json"));
    HwDesign design = new CanonicalHwirSerializer().Deserialize(hwir);
    HwValidatedDesign candidate = new FailClosedHardwareIrValidator().Validate(design).Validated ?? throw new InvalidDataException("Published HWIR failed validation.");
    byte[] Read(string name) => File.ReadAllBytes(Path.Combine(run, name));
    string Sha(byte[] data) => Hash(data);
    var supplements = new[]
    {
        ("smt-legality-guard-plane-mlir", ArtifactKind.Mlir, "smt-legality-guard-plane.mlir"),
        ("smt-legality-guard-plane-rtl", ArtifactKind.SystemVerilog, "smt-legality-guard-plane.sv"),
        ("smt-legality-guard-plane-testbench", ArtifactKind.SystemVerilog, "smt-legality-guard-plane.tb.sv"),
        ("smt-legality-guard-plane-simulation", ArtifactKind.CandidateRunProvenance, "smt-legality-guard-plane-simulation-receipt.json"),
        ("smt-legality-guard-plane-firtool-version", ArtifactKind.CandidateRunProvenance, "firtool-version.txt"),
        ("smt-legality-guard-plane-firtool-log", ArtifactKind.CandidateRunProvenance, "firtool-export.log"),
        ("smt-legality-guard-plane-iverilog-version", ArtifactKind.CandidateRunProvenance, "iverilog-version.txt"),
        ("smt-legality-guard-plane-vvp-version", ArtifactKind.CandidateRunProvenance, "vvp-version.txt"),
        ("smt-legality-guard-plane-iverilog-compile-log", ArtifactKind.CandidateRunProvenance, "iverilog-compile.log"),
        ("smt-legality-guard-plane-crosscheck", ArtifactKind.CandidateRunProvenance, "smt-legality-guard-plane-semantic-crosscheck.json"),
        ("smt-legality-guard-plane-source-inventory", ArtifactKind.SourceInventory, "smt-legality-guard-plane-source-inventory.json"),
        ("smt-legality-guard-plane-captured-sources", ArtifactKind.CapturedSources, "smt-legality-guard-plane-captured-sources.json"),
        ("smt-legality-guard-plane-capture-index", ArtifactKind.CandidateRunProvenance, "smt-legality-guard-plane-source-capture-index.json"),
        ("smt-legality-guard-plane-source-pins", ArtifactKind.CandidateRunProvenance, "smt-legality-guard-plane-source-pins.json"),
        ("smt-legality-guard-plane-sim-log", ArtifactKind.CandidateRunProvenance, "vvp-simulation.log"),
        ("smt-legality-guard-plane-tool-hashes", ArtifactKind.CandidateRunProvenance, "tool-hashes.txt")
    }.Where(x => File.Exists(Path.Combine(run, x.Item3)) && new FileInfo(Path.Combine(run, x.Item3)).Length > 0)
     .Select(x => { byte[] data = Read(x.Item3); return new StudioSupplementArtifact(x.Item1, x.Item2, x.Item3, data, Sha(data)); }).ToArray();
    bool rtlSimulationPassed = File.Exists(Path.Combine(run, "smt-legality-guard-plane-simulation-receipt.json"));
    if (rtlSimulationPassed)
    {
        using JsonDocument receipt = JsonDocument.Parse(Read("smt-legality-guard-plane-simulation-receipt.json"));
        JsonElement root = receipt.RootElement, model = root.GetProperty("model"), tools = root.GetProperty("tools"), commands = root.GetProperty("commands"), artifacts = root.GetProperty("artifacts");
        string Get(JsonElement e, string n) => e.GetProperty(n).GetString() ?? throw new InvalidDataException("Null receipt field " + n);
        if (Get(root,"schema") != "circuitweave.smt-legality-guard-plane-rtl-simulation/v1" || Get(root,"status") != "candidate-rtl-simulation-passed" ||
            Get(model,"hwirSha256") != candidate.CanonicalDigest || Get(model,"hwirArtifactSha256") != Sha(Read("smt-legality-guard-plane.hwir.json")) ||
            Get(model,"mlirSha256") != Sha(Read("smt-legality-guard-plane.mlir")) ||
            Get(artifacts,"rtlSha256") != Sha(Read("smt-legality-guard-plane.sv")) || Get(artifacts,"testbenchSha256") != Sha(Read("smt-legality-guard-plane.tb.sv")) ||
            Get(artifacts,"simulationLogSha256") != Sha(Read("vvp-simulation.log")) ||
            Get(tools,"circtExecutableSha256") != "187c0c0f0f1464a58997a3e99ca96560cc002b97276cc38c12c28addf0112c4e" ||
            Get(tools,"iverilogExecutableSha256") != "a6681e0b577dcfc4ea83d1591a5714710b0b3535ebb36491ee8177553bab9631" ||
            Get(tools,"vvpExecutableSha256") != "e0a087a4906d5a9cd1b0b85c9b4c2081704c2c9bcd94f224437fc56b15fc440d" ||
            commands.GetProperty("rtlExportExit").GetInt32() != 0 || commands.GetProperty("compileExit").GetInt32() != 0 || commands.GetProperty("simulationExit").GetInt32() != 0 ||
            !Get(artifacts,"simulationOutput").Contains("PASS SMT legality guard-plane vectors: 12288",StringComparison.Ordinal))
            throw new InvalidDataException("RTL simulation receipt is stale or disagrees with emitted artifacts/tool pins.");
    }
    byte[] pins = Read("smt-legality-guard-plane-source-pins.json");
    var stages = new[]
    {
        new CompilationStageRecord(CompilationStage.BindingAuthorization, CompilationStageStatus.NotRun, "No binding authorization stage was executed."),
        new CompilationStageRecord(CompilationStage.SymbolResolution, CompilationStageStatus.NotRun, "Only explicit SafetyVerifier members were source-mapped."),
        new CompilationStageRecord(CompilationStage.SourceAdmission, CompilationStageStatus.Completed, "Two exact active dirty-worktree source files are hash-pinned and captured."),
        new CompilationStageRecord(CompilationStage.HardwareIrValidation, CompilationStageStatus.Completed, "Fail-closed validation passed for source-bound guard-plane projection."),
        new CompilationStageRecord(CompilationStage.RtlEmission, rtlSimulationPassed ? CompilationStageStatus.Completed : CompilationStageStatus.NotRun, rtlSimulationPassed ? "Pinned CIRCT export receipt validated against source-bound MLIR and emitted RTL." : "No validated RTL simulation receipt."),
        new CompilationStageRecord(CompilationStage.IndependentVerification, rtlSimulationPassed ? CompilationStageStatus.Completed : CompilationStageStatus.NotRun, rtlSimulationPassed ? "Independent C# oracle passed 12,288 HWIR vectors and pinned Icarus passed 12,288 RTL vectors; certificate content/cache refresh and Golden/cycle proof remain outside scope." : "Independent C# HWIR oracle passed; no validated RTL simulation receipt."),
        new CompilationStageRecord(CompilationStage.EvidencePromotion, CompilationStageStatus.NotRun, "Candidate remains StructuralOnly.")
    };
    var request = new StudioCompilerPublicationRequest("hybridcpu-smt-legality-guard-plane", "HybridCPU SMT legality guard plane", "smtguard" + candidate.CanonicalDigest[..16], candidate,
        new HwTargetDescription("CIRCT generic", "combinational", "circt-cli"), stages, Supplements: supplements, SourceDigest: Sha(pins));
    await new StudioSnapshotPublisher().PublishAsync(request, target);
    var opened = await new StudioWorkspaceLoader().OpenAsync(Path.Combine(target, "studio.project.json"));
    if (!opened.Success || opened.Value is null) throw new InvalidDataException("Studio rejected guard-plane publication: " + opened.Error);
    var session = opened.Value;
    if (session.Graph.Nodes.Count(n => n.Kind == GraphNodeKind.Module) != 1 || session.Graph.Trust != DocumentTrust.StructuralOnly)
        throw new InvalidDataException("Unexpected Studio guard-plane module count or trust.");
    var nav = GraphNavigationIndex.Create(session.Graph);
    if (nav.GetRoots(0, 8).TotalCount == 0) throw new InvalidDataException("Studio graph navigation root missing.");
    var map = session.SourceMap?.Mappings.FirstOrDefault(m => m.Source.Commit == Commit && m.Source.RelativePath.Contains("SafetyVerifier.SmtLegality.cs", StringComparison.Ordinal))
        ?? throw new InvalidDataException("Studio source-map lost active legality source.");
    string? excerpt = CapturedNodeSource.Excerpt(session, map.Source);
    if (excerpt?.Contains("EvaluateSmtLegality", StringComparison.Ordinal) != true)
        throw new InvalidDataException("Captured legality source excerpt failed to resolve. Mapping=" + map.Source.RelativePath + ":" + map.Source.Line + "; excerpt=" + (excerpt ?? "<null>"));
    Console.WriteLine($"Studio API PASS: 1 module, {session.Graph.Nodes.Count} nodes, {session.Graph.Edges.Count} edges; captured source and navigation; StructuralOnly");
}

static BigInteger ParseBits(string bits) => new BigInteger(Convert.ToUInt64(bits, 2));
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static (int Line, int Column) Locate(byte[] source, string marker)
{
    string text = new UTF8Encoding(false, true).GetString(source);
    int index = text.IndexOf(marker, StringComparison.Ordinal);
    if (index < 0) throw new InvalidDataException("Source inventory location marker missing: " + marker);
    int line = 1, column = 1;
    for (int i = 0; i < index; i++) { if (text[i] == '\n') { line++; column = 1; } else column++; }
    return (line, column);
}
readonly record struct Case(int Boundary, int OwnerKnown, uint OwnerContext, uint CandidateContext, ulong CandidateDomain, ulong OwnerDomain, int KernelIsolation, int PhaseValid, int TemplateMatches, int CertificateAllowed);
