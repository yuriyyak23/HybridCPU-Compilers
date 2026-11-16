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
using StudioDocumentTrust = CircuitWeave.Studio.Contracts.StudioDocumentTrust;
using StudioGraphNodeKind = CircuitWeave.Studio.Contracts.StudioGraphNodeKind;

const string modelRoot = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE";
const string bridgeRoot = @"C:\Users\Yuriy\Desktop\HybridCPU Compilers\Compilers\HybridCPU_Compiler\HardwareBridge";
const string commit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
const string admissionRel = @"CloseToHSL\Core\Pipeline\Scheduling\Admission\MicroOpScheduler.Admission.cs";
const string laneRel = @"CloseToHSL\Core\Pipeline\Scheduling\LaneId.cs";
const string classRel = @"CloseToHSL\Core\Pipeline\Scheduling\SlotLegality\SlotClassDefinitions.cs";
const string chooserRel = @"CloseToHSL\Core\Pipeline\Scheduling\LaneChoice\DeterministicLaneChooser.cs";
if (args.Length == 3 && args[0] == "--publish") { await Publish(args[1], args[2]); return; }
int emitIndex = Array.IndexOf(args, "--emit");
string? output = emitIndex >= 0 && emitIndex + 1 < args.Length ? Path.GetFullPath(args[emitIndex + 1]) : null;
if (output is not null && !output.StartsWith(bridgeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Output must stay under HardwareBridge.");
byte[][] source = await Task.WhenAll(new[] { admissionRel, laneRel, classRel, chooserRel }.Select(p => File.ReadAllBytesAsync(Path.Combine(modelRoot, p))));
string[] hashes = source.Select(Hash).ToArray();
var toolchain = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
HwValidatedDesign design = BridgeHwirLaneMaterialization.Compile(source[0], source[1], source[2], source[3], hashes, commit, toolchain);
HwModule top = design.Design.Modules.Single(m => m.Id == design.Design.TopModule);
HwModule chooser = design.Design.Modules.Single(m => m.Id == "replay_lane_chooser_projection");
var modules = design.Design.Modules.ToDictionary(m => m.Id, StringComparer.Ordinal);
Dictionary<byte, byte> masks = ExtractClassMasks(source[2]);
int vectors = 0;
for (int occupancy = 0; occupancy < 256; occupancy++)
for (int permitted = 0; permitted < 256; permitted++)
    CheckTop(new() { ["slot_class"] = 2, ["pinning_kind"] = 1, ["pinned_lane"] = 6, ["occupancy"] = (byte)occupancy, ["permitted_lanes"] = (byte)permitted },
        defined: 1, accepted: ((occupancy & 64) == 0 && (permitted & 64) != 0) ? 1 : 0,
        lane: 6, reason: ((occupancy & 64) == 0 && (permitted & 64) != 0) ? 0 : 11);
for (int lane = 0; lane < 8; lane++)
for (int sample = 0; sample < 256; sample++)
    CheckTop(new() { ["slot_class"] = 2, ["pinning_kind"] = 1, ["pinned_lane"] = lane, ["occupancy"] = sample, ["permitted_lanes"] = (sample * 73 + lane * 19) & 255 },
        defined: 1, accepted: ((sample & (1 << lane)) == 0 && (((sample * 73 + lane * 19) & 255 & (1 << lane)) != 0)) ? 1 : 0,
        lane: lane, reason: ((sample & (1 << lane)) == 0 && (((sample * 73 + lane * 19) & 255 & (1 << lane)) != 0)) ? 0 : 11);
for (int lane = 8; lane < 256; lane++)
    CheckTop(new() { ["slot_class"] = 2, ["pinning_kind"] = 1, ["pinned_lane"] = lane }, defined: 1, accepted: 0, lane: 0, reason: 13);
foreach (var (slotClass, classMask) in masks)
for (int occupancy = 0; occupancy < 256; occupancy++)
{
    int permitted = (occupancy * 73 + slotClass * 29 + 41) & 255;
    bool opportunistic = (occupancy & 1) != 0, stable = (occupancy & 2) != 0, replay = (occupancy & 4) != 0;
    int donor = (occupancy * 37 + 85) & 255, previous = (occupancy % 10) == 9 ? -1 : occupancy % 10;
    int free = classMask & (~occupancy & 255) & permitted;
    if (opportunistic && stable) free &= donor;
    int expectedLane = Choose(free, replay, previous);
    CheckTop(new() { ["slot_class"] = slotClass, ["pinning_kind"] = 0, ["occupancy"] = occupancy, ["permitted_lanes"] = permitted,
        ["opportunistic"] = opportunistic ? 1 : 0, ["stable_donor"] = stable ? 1 : 0, ["donor_mask"] = donor,
        ["replay_active"] = replay ? 1 : 0, ["previous_lane"] = previous < 0 ? uint.MaxValue : (uint)previous },
        defined: 1, accepted: expectedLane >= 0 ? 1 : 0, lane: Math.Max(expectedLane, 0), reason: expectedLane >= 0 ? 0 : 12);
}
for (int free = 0; free < 256; free++)
for (int replay = 0; replay < 2; replay++)
for (int previous = -1; previous < 9; previous++)
{
    var result = Evaluate(chooser, new() { ["free_lanes"] = free, ["replay_active"] = replay, ["previous_lane"] = previous < 0 ? uint.MaxValue : (uint)previous });
    int lane = Choose(free, replay != 0, previous);
    Assert(result["valid"] == (lane >= 0 ? 1 : 0) && result["selected_lane"] == Math.Max(lane, 0) && result["reused"] == (lane >= 0 && replay != 0 && lane == previous ? 1 : 0), "chooser exhaustive mismatch");
    vectors++;
}
Console.WriteLine($"Stage B lane materialization HWIR PASS: {design.CanonicalDigest}; independent vectors={vectors}; HardPinned lane 6 exhaustive occupancy/permitted masks=65536; all pin IDs, all class masks, all occupancy masks sampled against replay/donor rules; chooser exhaustive=5120.");
if (output is not null) { await Emit(output, design, vectors, source, hashes); Console.WriteLine(Path.Combine(output, "stage-b-lane.mlir")); }

void CheckTop(Dictionary<string, BigInteger> inputs, int defined, int accepted, int lane, int reason)
{
    var values = Evaluate(top, inputs, modules);
    Assert(values["defined"] == defined && values["accepted"] == accepted && values["selected_lane"] == lane && values["reject_reason"] == reason,
        $"Stage B mismatch: {string.Join(',', inputs.Select(x => x.Key + '=' + x.Value))}; got defined={values["defined"]}, accepted={values["accepted"]}, lane={values["selected_lane"]}, reason={values["reject_reason"]}; expected={defined}/{accepted}/{lane}/{reason}");
    vectors++;
}
static int Choose(int free, bool replay, int previous) => free == 0 ? -1 : replay && previous >= 0 && previous < 8 && (free & (1 << previous)) != 0 ? previous : BitOperations.TrailingZeroCount((uint)free);
static void Assert(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static Dictionary<byte, byte> ExtractClassMasks(byte[] source)
{
    var root = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(source)).GetRoot();
    var ns = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Single(n => n.Name.ToString() == "YAKSys_Hybrid_CPU.Core");
    var enumType = ns.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(e => e.Identifier.ValueText == "SlotClass");
    var type = ns.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "SlotClassLaneMap");
    var codes = enumType.Members.ToDictionary(m => "SlotClass." + m.Identifier.ValueText, m => Convert.ToByte(((LiteralExpressionSyntax)m.EqualsValue!.Value).Token.Value));
    var fields = type.Members.OfType<FieldDeclarationSyntax>().SelectMany(f => f.Declaration.Variables).ToDictionary(v => v.Identifier.ValueText, v => Convert.ToByte(((LiteralExpressionSyntax)v.Initializer!.Value).Token.Value));
    var sw = (SwitchExpressionSyntax)type.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "GetLaneMask").ExpressionBody!.Expression;
    return sw.Arms.Where(a => a.Pattern is not DiscardPatternSyntax).ToDictionary(a => codes[a.Pattern.ToString()], a => fields[((IdentifierNameSyntax)a.Expression).Identifier.ValueText]);
}
static Dictionary<string, BigInteger> Evaluate(HwModule module, Dictionary<string, BigInteger> inputs, IReadOnlyDictionary<string, HwModule>? modules = null)
{
    modules ??= new Dictionary<string, HwModule>();
    var values = new Dictionary<string, BigInteger>(inputs, StringComparer.Ordinal);
    foreach (var input in module.Ports.Where(p => p.Direction == HwPortDirection.Input)) values.TryAdd(input.Name, BigInteger.Zero);
    var pending = module.Nodes.Where(n => n is HwConstantNode or HwOperationNode or HwMuxNode).Concat(module.Instances).ToList();
    foreach (var constant in pending.OfType<HwConstantNode>().ToArray())
    {
        BigInteger value = 0; foreach (char bit in constant.BitPattern) value = (value << 1) | (bit == '1' ? BigInteger.One : BigInteger.Zero);
        values[constant.Id] = value; pending.Remove(constant);
    }
    while (pending.Count > 0)
    {
        bool changed = false;
        foreach (HwNode node in pending.ToArray())
        {
            try
            {
                if (node is HwOperationNode operation)
                {
                    var args = operation.Operands.Select(x => values[x]).ToArray(); BigInteger a = args[0], b = args.Length > 1 ? args[1] : 0;
                    BigInteger raw = operation.Opcode switch { "eq" => a == b ? 1 : 0, "and" => a & b, "or" => a | b, "xor" => a ^ b, _ => throw new InvalidDataException("Unsupported Stage B opcode " + operation.Opcode) };
                    values[operation.Id] = raw & ((BigInteger.One << operation.ResultType.Width) - 1);
                }
                else if (node is HwMuxNode mux) values[mux.Id] = values[mux.Condition] != 0 ? values[mux.WhenTrue] : values[mux.WhenFalse];
                else if (node is HwInstanceNode instance)
                {
                    HwModule child = modules.Values.SingleOrDefault(m => m.Id == instance.ModuleReference) ?? throw new KeyNotFoundException(instance.ModuleReference);
                    var childInputs = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
                    foreach (HwPortContract port in child.Ports.Where(p => p.Direction == HwPortDirection.Input)) childInputs[port.Name] = values[instance.PortConnections[port.Name]];
                    var childOutputs = Evaluate(child, childInputs, modules);
                    foreach (HwPortContract port in child.Ports.Where(p => p.Direction == HwPortDirection.Output)) values[instance.PortConnections[port.Name]] = childOutputs[port.Name];
                }
                pending.Remove(node); changed = true;
            }
            catch (KeyNotFoundException) { }
        }
        if (!changed) throw new InvalidDataException("Stage B HWIR dependency cycle or unresolved net: " + string.Join(',', pending.Select(n => n.Id)) + "; missing=" + string.Join(',', pending.SelectMany(n => n switch { HwOperationNode o => o.Operands, HwMuxNode m => new[] { m.Condition,m.WhenTrue,m.WhenFalse }, _ => Array.Empty<string>() }).Where(n => !values.ContainsKey(n)).Distinct()));
    }
    foreach (HwOutputDriveNode drive in module.Nodes.OfType<HwOutputDriveNode>()) values[drive.OutputPort] = values[drive.Value];
    return values;
}

static async Task Emit(string directory, HwValidatedDesign design, int vectors, byte[][] source, string[] hashes)
{
    Directory.CreateDirectory(directory);
    var lowerer = new HierarchicalCirctMlirLowerer();
    var mlir = lowerer.LowerToMlir(design, new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>()));
    await File.WriteAllBytesAsync(Path.Combine(directory, "stage-b-lane.hwir.json"), new CanonicalHwirSerializer().Serialize(design));
    await File.WriteAllTextAsync(Path.Combine(directory, "stage-b-lane.mlir"), mlir.Text, new UTF8Encoding(false));
    var module = design.Design.Modules.Single(m => m.Id == design.Design.TopModule);
    await File.WriteAllTextAsync(Path.Combine(directory, "stage-b-lane.tb.sv"), BuildTb(), new UTF8Encoding(false));
    var captures = new[] { admissionRel, laneRel, classRel, chooserRel }.Select((relative, i) => new { relative, path = Path.Combine(modelRoot, relative), bytes = source[i], sha256 = hashes[i] }).ToArray();
    var pinBytes = JsonSerializer.SerializeToUtf8Bytes(new { schema="circuitweave.stage-b-lane-source-pins/v1",modelCommit=commit,currentModelHead=commit,modelTreeState="dirty; listed working bytes captured exactly; base revision not substituted",hwirSha256=design.CanonicalDigest,mlirSha256=mlir.ContentDigest,vectors,sourceFiles=captures.Select(x=>new{path="HybridCPU_ISE/"+x.relative.Replace('\\','/'),x.sha256,length=x.bytes.LongLength}),scope="Stage B lane materialization only; Stage A, state mutation/commit, lane-reuse counter, memory/other rejection paths are outside this projection." },new JsonSerializerOptions{WriteIndented=true});
    var semanticBytes=JsonSerializer.SerializeToUtf8Bytes(new { schema="circuitweave.stage-b-lane-crosscheck/v1",status="passed",vectors,exhaustiveHardPinnedLane6Masks=65536,exhaustiveReplayChooserCases=5120,scope="Independent scalar evaluator over validated HWIR, source-extracted slot-class map and separately coded lane chooser oracle; no CPU timeline or Golden equivalence." },new JsonSerializerOptions{WriteIndented=true});
    byte[] inventoryBytes=BuildInventory(captures[0]);
    byte[] capturedBytes=JsonSerializer.SerializeToUtf8Bytes(new { schema="circuitweave.captured-sources/v1",inventorySha256=Hash(inventoryBytes),files=captures.Select(x=>new{path=x.path,bytes=Convert.ToBase64String(x.bytes)}) },new JsonSerializerOptions{WriteIndented=true});
    await File.WriteAllBytesAsync(Path.Combine(directory,"stage-b-lane-source-pins.json"),pinBytes);
    await File.WriteAllBytesAsync(Path.Combine(directory,"stage-b-lane-semantic-crosscheck.json"),semanticBytes);
    await File.WriteAllBytesAsync(Path.Combine(directory,"stage-b-lane-source-inventory.json"),inventoryBytes);
    await File.WriteAllBytesAsync(Path.Combine(directory,"stage-b-lane-captured-sources.json"),capturedBytes);

    byte[] BuildInventory(dynamic captured)
    {
        string path=(string)captured.path; byte[] bytes=(byte[])captured.bytes;
        var root=CSharpSyntaxTree.ParseText(new UTF8Encoding(false,true).GetString(bytes)).GetRoot();
        var owner=root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c=>c.Identifier.ValueText=="MicroOpScheduler");
        var method=owner.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="TryMaterializeLane");
        var span=method.Identifier.GetLocation().GetLineSpan().StartLinePosition; int line=span.Line+1,column=span.Character+1;
        var typeSpan=owner.Identifier.GetLocation().GetLineSpan().StartLinePosition; int typeLine=typeSpan.Line+1,typeColumn=typeSpan.Character+1;
        return JsonSerializer.SerializeToUtf8Bytes(new { schema="circuitweave.source-coverage-index/v1",disposition="incomplete-unqualified",semanticReportSha256=Hash(semanticBytes),project=Path.Combine(modelRoot,"HybridCPU_ISE.csproj"),configuration="Debug",targetFramework="net11.0",defines=new[]{"TRACE","TESTING","DEBUG"},scope="Selected Stage B materialization method and four exact source files; not a complete Processor/project inventory. The Admission source contains other dirty working-tree edits outside this member.",captureVerification=new{schema="circuitweave.source-capture-check/v1",status="producer-reported-listed-inputs-match",semanticReportSha256=Hash(semanticBytes),sourceCaptureSha256=Hash(JsonSerializer.SerializeToUtf8Bytes(captures.Select(x=>new{path=x.path,x.sha256,length=x.bytes.LongLength}))),projectGraphSha256=Hash(File.ReadAllBytes(Path.Combine(modelRoot,"HybridCPU_ISE.csproj"))),scope="Listed source bytes only; root project file digest captured, evaluated dependency graph closure not claimed.",checkedFiles=captures.Length},capturedSourceFiles=captures.Select(x=>new{path=x.path,x.sha256,length=x.bytes.LongLength}),symbols=new[]{new{symbol="global::YAKSys_Hybrid_CPU.Core.MicroOpScheduler",assembly="HybridCPU_ISE",kind="Class",accessibility="Public",memberCount=1,blockingReason="Selected member projection only; all other partial declarations and transitive dependencies are not inventoried.",coverage=new{inventory="partial-semantic",hardwareRole="unclassified",hwir="not-established",rtl="not-established",simulation="not-run",independentVerification="not-run",studio="not-published"},declarations=new[]{new{location=new{file=path,startLine=typeLine,startColumn=typeColumn},memberIds=new[]{"M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.TryMaterializeLane(System.ReadOnlySpan{System.Byte},System.Int32@,YAKSys_Hybrid_CPU.Core.TypedSlotRejectReason@)"}}},dependencies=Array.Empty<string>(),members=new[]{new{id="M:YAKSys_Hybrid_CPU.Core.MicroOpScheduler.TryMaterializeLane(System.ReadOnlySpan{System.Byte},System.Int32@,YAKSys_Hybrid_CPU.Core.TypedSlotRejectReason@)",kind="Method",locations=new[]{new{file=path,startLine=line,startColumn=column}}}}}}},new JsonSerializerOptions{WriteIndented=true});
    }
    string BuildTb()
    {
        var b=new StringBuilder(); b.AppendLine("module stage_b_lane_directed_tb;");
        foreach(var p in module.Ports)b.AppendLine($"{(p.Direction==HwPortDirection.Input?"logic":"wire")} [{p.Type.Width-1}:0] {p.Name};");
        b.AppendLine($"{module.Id} dut (.*); initial begin $dumpfile(\"stage-b-lane.vcd\"); $dumpvars(0,stage_b_lane_directed_tb);");
        void Set(string n,int v)=>b.AppendLine($"{n}={module.Ports.Single(p=>p.Name==n).Type.Width}'d{v};");
        void Base(int kind,int lane,int occ,int allowed,int cls=0){Set("slot_class",cls);Set("pinning_kind",kind);Set("pinned_lane",lane);Set("occupancy",occ);Set("permitted_lanes",allowed);Set("opportunistic",0);Set("stable_donor",0);Set("donor_mask",255);Set("replay_active",0);Set("previous_lane",0);}
        void Expect(int def,int ok,int lane,int reason,string label){b.AppendLine("#1;");b.AppendLine($"if(defined!==1'd{def}||accepted!==1'd{ok}||selected_lane!==3'd{lane}||reject_reason!==4'd{reason}) $fatal(1,\"{label}\");");b.AppendLine($"$display(\"PASS {label}\");");}
        Base(1,6,0,64);Expect(1,1,6,0,"HardPinned DMA carrier lane6 accepted");
        Base(1,6,64,64);Expect(1,0,6,11,"HardPinned lane6 occupied conflict");
        Base(1,6,0,32);Expect(1,0,6,11,"HardPinned lane6 outside permitted mask");
        Base(1,8,0,255);Expect(1,0,0,13,"HardPinned lane8 invalid");
        Base(0,0,1,15);Expect(1,1,1,0,"flexible ALU picks lowest free lane");
        Base(0,0,0,0);Expect(1,0,0,12,"flexible lane exhaustion");
        Base(0,0,0,15);Set("replay_active",1);Set("previous_lane",3);Expect(1,1,3,0,"replay hint reuses eligible lane");
        b.AppendLine("$display(\"PASS all directed Stage B RTL scenarios\");$finish;end endmodule"); return b.ToString();
    }
}

static async Task Publish(string run,string target)
{
    byte[] Read(string name)=>File.ReadAllBytes(Path.Combine(run,name));
    string H(byte[] b)=>Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    HwDesign raw=new CanonicalHwirSerializer().Deserialize(Read("stage-b-lane.hwir.json"));
    var validation=new FailClosedHardwareIrValidator().Validate(raw); var design=validation.Validated??throw new InvalidDataException("HWIR refused at publication: "+string.Join(";",validation.Diagnostics.Select(d=>d.Message)));
    var artifacts=new (string id,StudioArtifactKind kind,string file)[]{("stage-b-lane-rtl",StudioArtifactKind.SystemVerilog,"stage-b-lane.sv"),("stage-b-lane-waveform",StudioArtifactKind.VcdWaveform,"stage-b-lane.vcd"),("stage-b-lane-testbench",StudioArtifactKind.SystemVerilog,"stage-b-lane.tb.sv"),("stage-b-lane-mlir",StudioArtifactKind.Mlir,"stage-b-lane.mlir"),("stage-b-lane-crosscheck",StudioArtifactKind.CandidateRunProvenance,"stage-b-lane-semantic-crosscheck.json"),("stage-b-lane-receipt",StudioArtifactKind.CandidateRunProvenance,"stage-b-lane-simulation-receipt.json"),("stage-b-lane-source-pins",StudioArtifactKind.CandidateRunProvenance,"stage-b-lane-source-pins.json"),("stage-b-lane-source-inventory",StudioArtifactKind.SourceInventory,"stage-b-lane-source-inventory.json"),("stage-b-lane-captured-sources",StudioArtifactKind.CapturedSources,"stage-b-lane-captured-sources.json"),("stage-b-lane-tool-hashes",StudioArtifactKind.CandidateRunProvenance,"tool-hashes.txt"),("stage-b-lane-iverilog-log",StudioArtifactKind.CandidateRunProvenance,"iverilog-compile.log"),("stage-b-lane-vvp-log",StudioArtifactKind.CandidateRunProvenance,"vvp-simulation.log")}.Where(a=>File.Exists(Path.Combine(run,a.file))&&new FileInfo(Path.Combine(run,a.file)).Length>0).Select(a=>new StudioSupplementArtifact(a.id,a.kind,a.file,Read(a.file),H(Read(a.file)))).ToArray();
    var stages=new[]{new CompilationStageRecord(CompilationStage.SourceAdmission,CompilationStageStatus.NotRun,"One selected Stage B method plus four byte-pinned files; no project-wide compilation/inventory."),new CompilationStageRecord(CompilationStage.HardwareIrValidation,CompilationStageStatus.Completed,"Source-shape guards and independent Stage B HWIR oracle passed."),new CompilationStageRecord(CompilationStage.RtlEmission,CompilationStageStatus.Completed,"Pinned CIRCT emitted combinational Stage B RTL."),new CompilationStageRecord(CompilationStage.IndependentVerification,CompilationStageStatus.Completed,"Independent HWIR evaluator and directed Icarus scenarios passed; not CPU cycle or Golden proof."),new CompilationStageRecord(CompilationStage.EvidencePromotion,CompilationStageStatus.NotRun,"StructuralOnly; Stage A, mutation commit, replay telemetry and architecture-level evidence remain open.")};
    byte[] pins=Read("stage-b-lane-source-pins.json");
    var request=new StudioCompilerPublicationRequest("hybridcpu-stage-b-lane","HybridCPU Stage B lane materialization", "stageblane"+design.CanonicalDigest[..16],design,new HwTargetDescription("CIRCT generic","combinational","circt-cli"),stages,Supplements:artifacts,SourceDigest:H(pins));
    var receipt=await new StudioSnapshotPublisher().PublishAsync(request,target); Console.WriteLine(JsonSerializer.Serialize(receipt,new JsonSerializerOptions{WriteIndented=true}));
    var opened=await new StudioWorkspaceLoader().OpenAsync(Path.Combine(target,"studio.project.json")); if(!opened.Success||opened.Value is null)throw new InvalidDataException("Studio load failed: "+opened.Error);
    var session=opened.Value; var nav=GraphNavigationIndex.Create(session.Graph);
    if(session.Graph.Trust!=StudioDocumentTrust.StructuralOnly||session.Graph.Nodes.Count(n=>n.Kind==StudioGraphNodeKind.Module)!=3||nav.GetRoots(0,8).Items.Length==0)throw new InvalidDataException("Studio Stage B graph/hierarchy check failed.");
    if(SourceInventorySearch.Find(session.SourceInventory?.Symbols??Array.Empty<StudioSourceInventorySymbol>(),"TryMaterializeLane").Count==0)throw new InvalidDataException("Studio Stage B source search failed.");
    var map=session.SourceMap?.Mappings.FirstOrDefault(m=>m.Source.Commit==commit&&m.Source.RelativePath.EndsWith("MicroOpScheduler.Admission.cs",StringComparison.Ordinal))??throw new InvalidDataException("Stage B source map missing.");
    if(!nav.TryGetNode(map.GraphNodeId,out _)||string.IsNullOrWhiteSpace(CapturedNodeSource.Excerpt(session,map.Source)))throw new InvalidDataException("Stage B captured source navigation failed.");
    if(session.Artifacts.Any(a=>!a.HasDeclaredDigest||a.Artifact.Sha256!=a.ActualSha256||a.Artifact.ByteLength!=a.ActualSize))throw new InvalidDataException("Studio artifact integrity mismatch.");
    Console.WriteLine($"Stage B Studio API PASS: modules={session.Graph.Nodes.Count(n=>n.Kind==StudioGraphNodeKind.Module)} nodes={session.Graph.Nodes.Count} edges={session.Graph.Edges.Count} artifacts={session.Artifacts.Count} trust={session.Graph.Trust} diagnostics={session.Diagnostics.Count}.");
}
