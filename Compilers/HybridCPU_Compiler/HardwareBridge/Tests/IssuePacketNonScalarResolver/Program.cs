using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using HybridCPU.Compiler.HardwareBridge.Hwir;

if (args.Length is not (7 or 8)) throw new ArgumentException("Usage: <StageFlow.cs> <DecodedBundleDescriptor.cs> <IssuePacketLane.cs> <BundleIssuePacket.cs> <oracle.jsonl> <output-dir> <model-root> [create-oracle.jsonl]");
string[] expected =
[
    "672e9d2523dd04068dc63650a7f52a7df67a904abe57c5639abb3b8b89d2317e",
    "ad3878ae15aad02e44f181ad7abb26d40b7da13ca6a7e46ae003b4a8a5e98c2f",
    "030a4262d0d57db5185b70e20ac3c61cce37bd471aa10116ce309b30971d665c",
    "9a46971ca2c41a52611a4c7fb65991df90c7645d1e25b21cbad63c38268b049c"
];
string[] sourcePaths = args.Take(4).Select(Path.GetFullPath).ToArray();
string[] sourceHashes = sourcePaths.Select(HashFile).ToArray();
if (!sourceHashes.SequenceEqual(expected, StringComparer.Ordinal)) throw new InvalidDataException("Pinned resolver source bytes differ.");
string oraclePath = Path.GetFullPath(args[4]);
string output = Path.GetFullPath(args[5]);
string modelRoot = Path.GetFullPath(args[6]);
string createOraclePath = args.Length > 7 ? Path.GetFullPath(args[7]) : "";
Directory.CreateDirectory(output);
byte[] sourceBytes(int index) => File.ReadAllBytes(sourcePaths[index]);
var toolchain = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
var design = BridgeHwirIssuePacketNonScalarPhysicalLaneMask.Compile(sourceBytes(0), sourceBytes(1),
    sourceBytes(2), sourceBytes(3), expected, "1bfe41a49cf11c80fa6c6aee87accb54b78b331f", toolchain);
HwModule top = design.Design.Modules.Single(m => m.Id == design.Design.TopModule);
HwPortContract[] inputs = top.Ports.Where(p => p.Direction == HwPortDirection.Input).ToArray();
HwPortContract outputPort = top.Ports.Single(p => p.Direction == HwPortDirection.Output);
if (top.Nodes.Count > 2048 || inputs.Length != 69 || outputPort.Name != "executable_non_scalar_physical_lane_mask")
    throw new InvalidDataException($"Resolver shape exceeds bounded contract: nodes={top.Nodes.Count}, inputs={inputs.Length}.");
byte[] canonical = new CanonicalHwirSerializer().Serialize(design);
string mlir = new HierarchicalCirctMlirLowerer().LowerToMlir(design,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>())).Text;
File.WriteAllBytes(Path.Combine(output, "resolver.hwir.json"), canonical);
File.WriteAllText(Path.Combine(output, "resolver.mlir"), mlir, new UTF8Encoding(false));
string core = Path.Combine(modelRoot, "CloseToHSL", "Core");
byte[] ReadModel(string relative) => File.ReadAllBytes(Path.Combine(core, relative));
byte[] createPacket = ReadModel("Pipeline/Core/RuntimeClusterAdmissionPreparation.BundleIssuePacket.cs");
byte[] createIntent = ReadModel("Legality/ClusterIssueIntent.cs");
byte[] createPreparation = ReadModel("Decoder/ClusterIssuePreparation.cs");
byte[] createClasses = ReadModel("Pipeline/Scheduling/SlotLegality/SlotClassDefinitions.cs");
byte[] createPlacement = ReadModel("Pipeline/Scheduling/SlotPlacementMetadata.cs");
byte[] createBundle = ReadModel("Pipeline/Metadata/BundleMetadata.cs");
byte[] createDecode = sourceBytes(1);
byte[] materialization = ReadModel("Pipeline/ExecutionFlow/Materialization/CPU_Core.PipelineExecution.Materialization.cs");
string[] createHashes = new byte[][] { createPacket, createIntent, createPreparation, createClasses, createPlacement, createBundle, createDecode }.Select(HashBytes).ToArray();
string[] materializationHashes = new byte[][] { materialization, createPacket, sourceBytes(2) }.Select(HashBytes).ToArray();
string[] resolverHashes = sourceBytesArr(sourceBytes);
string[] pinnedCreate = [
    "9a46971ca2c41a52611a4c7fb65991df90c7645d1e25b21cbad63c38268b049c", "9ce089b7aabba6c9872a7320c5890cac6da414255046c780250e0e4808732667",
    "28349db7aa7df0d16747099ed952f742adb59b195f7a21ae3c3826b7052d1557", "489edf25fe528393aba2e298c259e7929f59260430b4946ccea4c713065d9ff1",
    "06cac68870c46136250bfdc4c5b195c6f569d7113d7c5d10bfbc41d5b0797f19", "3337b0187d2345b7bc40f8c2dd7732c5757696c2a91028353bd15dbf53e6cbe4",
    "ad3878ae15aad02e44f181ad7abb26d40b7da13ca6a7e46ae003b4a8a5e98c2f"
];
string[] pinnedMaterialization = ["8d265200a0faabc52f7c5b62eea7fa04a938f9fe1b8f61326aa3bed83bc39328", "9a46971ca2c41a52611a4c7fb65991df90c7645d1e25b21cbad63c38268b049c", "030a4262d0d57db5185b70e20ac3c61cce37bd471aa10116ce309b30971d665c"];
if (!createHashes.SequenceEqual(pinnedCreate) || !materializationHashes.SequenceEqual(pinnedMaterialization))
    throw new InvalidDataException("Create/materialization source inputs differ from the pinned Processor model snapshot.");
var compositionDesign = BridgeHwirIssuePacketMaterializationComposition.Compile(createPacket, createIntent, createPreparation,
    createClasses, createPlacement, createBundle, createDecode, materialization, createPacket, sourceBytes(2),
    sourceBytes(0), sourceBytes(1), createHashes, materializationHashes, expected,
    "1bfe41a49cf11c80fa6c6aee87accb54b78b331f", toolchain);
HwModule compositionTopForBuild = compositionDesign.Design.Modules.Single(m => m.Id == compositionDesign.Design.TopModule);
File.WriteAllBytes(Path.Combine(output, "composition.hwir.json"), new CanonicalHwirSerializer().Serialize(compositionDesign));
string compositionMlirText = new HierarchicalCirctMlirLowerer().LowerToMlir(compositionDesign,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>())).Text;
File.WriteAllText(Path.Combine(output, "composition.mlir"), compositionMlirText, new UTF8Encoding(false));
string[] createSourceNames = [
    "Pipeline/Core/RuntimeClusterAdmissionPreparation.BundleIssuePacket.cs", "Legality/ClusterIssueIntent.cs",
    "Decoder/ClusterIssuePreparation.cs", "Pipeline/Scheduling/SlotLegality/SlotClassDefinitions.cs",
    "Pipeline/Scheduling/SlotPlacementMetadata.cs", "Pipeline/Metadata/BundleMetadata.cs", "Decoder/DecodedBundleDescriptor.cs"
];
string[] materializationSourceNames = [
    "Pipeline/ExecutionFlow/Materialization/CPU_Core.PipelineExecution.Materialization.cs",
    "Pipeline/Core/RuntimeClusterAdmissionPreparation.BundleIssuePacket.cs", "Pipeline/Core/RuntimeClusterAdmissionPreparation.IssuePacketLane.cs"
];
string[] resolverSourceNames = [
    "Pipeline/ExecutionFlow/StageFlow/CPU_Core.PipelineExecution.StageFlow.cs", "Decoder/DecodedBundleDescriptor.cs",
    "Pipeline/Core/RuntimeClusterAdmissionPreparation.IssuePacketLane.cs", "Pipeline/Core/RuntimeClusterAdmissionPreparation.BundleIssuePacket.cs"
];
File.WriteAllText(Path.Combine(output, "source-hashes.txt"), string.Join(Environment.NewLine,
    ["commit=1bfe41a49cf11c80fa6c6aee87accb54b78b331f",
        .. createHashes.Select((h, i) => $"create[{i}]={createSourceNames[i]}|{h}"),
        .. materializationHashes.Select((h, i) => $"materialization[{i}]={materializationSourceNames[i]}|{h}"),
        .. resolverHashes.Select((h, i) => $"resolver[{i}]={resolverSourceNames[i]}|{h}")]), new UTF8Encoding(false));

JsonDocument header = JsonDocument.Parse(File.ReadLines(oraclePath).First());
if (header.RootElement.GetProperty("schema").GetString() != "circuitweave.issue-packet-nonscalar-materialization-oracle/v1" ||
    header.RootElement.GetProperty("modelCommit").GetString() != "1bfe41a49cf11c80fa6c6aee87accb54b78b331f" ||
    !header.RootElement.GetProperty("sourceInputs").EnumerateArray().Select(x => x.GetProperty("sha256").GetString()).SequenceEqual(expected, StringComparer.Ordinal))
    throw new InvalidDataException("Exact-source oracle header differs from current pinned inputs.");

var createInputsByScalarIssueMask = new Dictionary<byte, JsonElement>();
if (createOraclePath.Length > 0)
{
    using JsonDocument createHeader = JsonDocument.Parse(File.ReadLines(createOraclePath).First());
    if (createHeader.RootElement.GetProperty("schema").GetString() != "circuitweave.bundle-issue-packet-create-oracle/v1" ||
        createHeader.RootElement.GetProperty("modelCommit").GetString() != "1bfe41a49cf11c80fa6c6aee87accb54b78b331f" ||
        createHeader.RootElement.GetProperty("sources").EnumerateArray().Select(x => x.GetProperty("sha256").GetString()).SequenceEqual(createHashes.Take(1).Concat([createHashes[3], createHashes[4], createHashes[2], createHashes[1]]), StringComparer.Ordinal) == false)
        throw new InvalidDataException("Create exact-source oracle header differs from pinned Create inputs.");
    foreach (string line in File.ReadLines(createOraclePath).Skip(1))
    {
        using JsonDocument record = JsonDocument.Parse(line);
        JsonElement row = record.RootElement;
        byte assignedLaneMask = 0;
        JsonElement[] laneSlots = row.GetProperty("outputs").GetProperty("laneSlots").EnumerateArray().ToArray();
        for (int lane = 0; lane < laneSlots.Length; lane++) if (laneSlots[lane].GetInt32() >= 0) assignedLaneMask |= (byte)(1 << lane);
        byte scalarIssueMask = row.GetProperty("inputs").GetProperty("scalarMask").GetByte();
        if (assignedLaneMask != 0) createInputsByScalarIssueMask.TryAdd(scalarIssueMask, row.Clone());
    }
    if (createInputsByScalarIssueMask.Count == 0) throw new InvalidDataException("Create oracle produced no nonzero assigned-lane masks.");
    File.Copy(createOraclePath, Path.Combine(output, "create-oracle.jsonl"), overwrite: true);
}

static ulong Number(JsonElement item) => item.ValueKind switch
{
    JsonValueKind.True => 1,
    JsonValueKind.False => 0,
    JsonValueKind.Number => item.GetUInt64(),
    JsonValueKind.String => ulong.Parse(item.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
    _ => throw new InvalidDataException("Unsupported oracle vector value.")
};
static bool Kind(JsonElement lane, string kind) => lane.GetProperty("kind").GetString() == kind;
static ulong Fact(JsonElement lane, string port)
{
    string kind = port["lane0_".Length..];
    return kind switch
    {
        "microop_present" => Kind(lane, "Trap") || Kind(lane, "LoadStore") || Kind(lane, "MatrixTile") || Kind(lane, "Branch") || Kind(lane, "SysEvent") || Kind(lane, "StreamControl") || Kind(lane, "CSR") || Kind(lane, "VMX") || Kind(lane, "VConfig") || Kind(lane, "Other") ? 1UL : 0UL,
        "is_trap" => Kind(lane, "Trap") ? 1UL : 0UL,
        "is_load_store" => Kind(lane, "LoadStore") ? 1UL : 0UL,
        "is_assist" => Number(lane.GetProperty("isAssist")),
        "is_matrix_tile" => Kind(lane, "MatrixTile") ? 1UL : 0UL,
        "is_branch" => Kind(lane, "Branch") ? 1UL : 0UL,
        "is_sys_event" => Kind(lane, "SysEvent") ? 1UL : 0UL,
        "is_stream_control" => Kind(lane, "StreamControl") ? 1UL : 0UL,
        "is_csr" => Kind(lane, "CSR") ? 1UL : 0UL,
        "is_vmx" => Kind(lane, "VMX") ? 1UL : 0UL,
        "is_vconfig" => Kind(lane, "VConfig") ? 1UL : 0UL,
        _ => throw new InvalidDataException("Unknown lane type fact: " + kind)
    };
}

var vectorMemory = new StringBuilder();
var compositionVectorMemory = new StringBuilder();
int vectorCount = 0;
int compositionVectorCount = 0;
int compositionDifferentMaskCount = 0;
int bitOffset = 0;
int expectedOffset = inputs.Sum(p => p.Type.Width);
int memoryWidth = (expectedOffset + outputPort.Type.Width + 3) / 4;
foreach (string line in File.ReadLines(oraclePath).Skip(1))
{
    using JsonDocument row = JsonDocument.Parse(line);
    JsonElement v = row.RootElement;
    var values = new Dictionary<string, ulong>(StringComparer.Ordinal)
    {
        ["dependency_summary_present"] = Number(v.GetProperty("summaryPresent")),
        ["scalar_issue_mask"] = Number(v.GetProperty("scalarIssueMask"))
    };
    foreach (string matrix in new[] { "raw", "waw", "war", "control", "memory", "system", "pinned" })
        values[matrix + "_dependency_mask"] = Number(v.GetProperty(matrix));
    JsonElement[] lanes = v.GetProperty("lanes").EnumerateArray().ToArray();
    if (lanes.Length != 4) throw new InvalidDataException("Oracle lane count differs.");
    for (int i = 0; i < 4; i++)
    {
        JsonElement lane = lanes[i];
        int laneIndex = i + 4;
        string p = "lane" + laneIndex + "_";
        values[p + "physical_lane_index"] = Number(lane.GetProperty("physicalLaneIndex"));
        values[p + "slot_index"] = Number(lane.GetProperty("slotIndex"));
        values[p + "occupied"] = Number(lane.GetProperty("occupied"));
        values[p + "counts_toward_scalar_projection"] = Number(lane.GetProperty("countsScalar"));
        foreach (string fact in new[] { "microop_present", "is_trap", "is_load_store", "is_assist", "is_matrix_tile", "is_branch", "is_sys_event", "is_stream_control", "is_csr", "is_vmx", "is_vconfig" })
            values[p + fact] = Fact(lane, "lane0_" + fact);
    }
    BigInteger packed = BigInteger.Zero;
    bitOffset = 0;
    foreach (HwPortContract port in inputs)
    {
        if (!values.TryGetValue(port.Name, out ulong value)) throw new InvalidDataException("Missing oracle value for port " + port.Name);
        packed |= new BigInteger(value) << bitOffset;
        bitOffset += port.Type.Width;
    }
    packed |= new BigInteger(v.GetProperty("expectedPhysicalLaneMask").GetByte()) << bitOffset;
    vectorMemory.AppendLine(packed.ToString("x", CultureInfo.InvariantCulture).PadLeft(memoryWidth, '0'));
    HwModule compositionTop = compositionDesign.Design.Modules.Single(m => m.Id == compositionDesign.Design.TopModule);
    HwPortContract[] compositionInputs = compositionTop.Ports.Where(p => p.Direction == HwPortDirection.Input).ToArray();
    HwPortContract compositionOutput = compositionTop.Ports.Single(p => p.Direction == HwPortDirection.Output && p.Name == "materializable_lane_mask");
    BigInteger compositionPacked = BigInteger.Zero;
    int compositionOffset = 0;
    byte expectedAssignedLaneMask = 0, expectedAssignedSlotMask = 0, expectedScalarLaneMask = 0;
    byte resolverScalarIssueMask = v.GetProperty("scalarIssueMask").GetByte();
    bool hasNonzeroCreateMatch = createInputsByScalarIssueMask.TryGetValue(resolverScalarIssueMask, out JsonElement createRow);
    Dictionary<string, ulong> compositionValues = new(values, StringComparer.Ordinal);
    if (hasNonzeroCreateMatch)
    {
        JsonElement createInput = createRow.GetProperty("inputs"), createOutput = createRow.GetProperty("outputs");
        int entryCount = createInput.GetProperty("entryCount").GetInt32();
        compositionValues["scalar_count"] = (ulong)entryCount;
        compositionValues["scalar_issue_mask"] = Number(createInput.GetProperty("scalarMask"));
        compositionValues["auxiliary_reservation_mask"] = Number(createInput.GetProperty("auxMask"));
        compositionValues["auxiliary_slot_count"] = 8;
        for (int i = 0; i < 4; i++)
        {
            bool exists = i < entryCount;
            JsonElement[] entrySlots = createInput.GetProperty("entrySlots").EnumerateArray().ToArray();
            JsonElement[] scalarClasses = createInput.GetProperty("scalarClasses").EnumerateArray().ToArray();
            JsonElement[] hardPinned = createInput.GetProperty("scalarHardPinned").EnumerateArray().ToArray();
            JsonElement[] pinnedLanes = createInput.GetProperty("scalarPinnedLanes").EnumerateArray().ToArray();
            JsonElement[] attemptPresent = createInput.GetProperty("attemptPresent").EnumerateArray().ToArray();
            JsonElement[] attemptCurrent = createInput.GetProperty("attemptCurrent").EnumerateArray().ToArray();
            JsonElement[] attemptLanes = createInput.GetProperty("attemptLanes").EnumerateArray().ToArray();
            string prefix = $"scalar{i}_";
            compositionValues[prefix + "slot"] = exists ? Number(entrySlots[i]) : 255;
            compositionValues[prefix + "microop_present"] = exists ? 1UL : 0UL;
            compositionValues[prefix + "issued_present"] = exists && i < attemptPresent.Length && attemptPresent[i].GetBoolean() ? 1UL : 0UL;
            compositionValues[prefix + "issued_current"] = exists && i < attemptCurrent.Length && attemptCurrent[i].GetBoolean() ? 1UL : 0UL;
            compositionValues[prefix + "issued_lane_signed_bits"] = exists && i < attemptLanes.Length ? unchecked((uint)attemptLanes[i].GetInt32()) : 0UL;
            compositionValues[prefix + "hard_pinned"] = exists && hardPinned[i].GetBoolean() ? 1UL : 0UL;
            compositionValues[prefix + "pinned_lane_id"] = exists ? Number(pinnedLanes[i]) : 0UL;
            compositionValues[prefix + "slot_class"] = exists ? Number(scalarClasses[i]) : 0UL;
        }
        JsonElement[] auxValid = createInput.GetProperty("auxValid").EnumerateArray().ToArray();
        JsonElement[] auxHardPinned = createInput.GetProperty("auxHardPinned").EnumerateArray().ToArray();
        JsonElement[] auxPinnedLanes = createInput.GetProperty("auxPinnedLanes").EnumerateArray().ToArray();
        JsonElement[] auxClasses = createInput.GetProperty("auxClasses").EnumerateArray().ToArray();
        for (int i = 0; i < 8; i++)
        {
            string prefix = $"aux{i}_";
            compositionValues[prefix + "valid"] = auxValid[i].GetBoolean() ? 1UL : 0UL;
            compositionValues[prefix + "hard_pinned"] = auxHardPinned[i].GetBoolean() ? 1UL : 0UL;
            compositionValues[prefix + "pinned_lane_id"] = Number(auxPinnedLanes[i]);
            compositionValues[prefix + "slot_class"] = Number(auxClasses[i]);
        }
        JsonElement[] laneSlots = createOutput.GetProperty("laneSlots").EnumerateArray().ToArray();
        JsonElement[] laneScalar = createOutput.GetProperty("laneScalar").EnumerateArray().ToArray();
        for (int i = 0; i < laneSlots.Length; i++)
        {
            if (laneSlots[i].GetInt32() >= 0) { expectedAssignedLaneMask |= (byte)(1 << i); expectedAssignedSlotMask |= (byte)(1 << laneSlots[i].GetInt32()); }
            if (laneScalar[i].GetBoolean()) expectedScalarLaneMask |= (byte)(1 << i);
        }
        if (createInput.GetProperty("scalarMask").GetByte() != resolverScalarIssueMask)
            throw new InvalidDataException("Create input scalar slot mask differs from the resolver's shared scalar slot mask.");
        if (expectedAssignedLaneMask != resolverScalarIssueMask) compositionDifferentMaskCount++;
    }
    foreach (HwPortContract port in compositionInputs)
    {
        // Join on the shared slot-domain scalar mask. assigned_lane_mask remains an independent
        // physical-lane-domain Create result and is checked separately below.
        ulong value = hasNonzeroCreateMatch && compositionValues.TryGetValue(port.Name, out ulong oracleValue) ? oracleValue : 0;
        compositionPacked |= new BigInteger(value) << compositionOffset;
        compositionOffset += port.Type.Width;
    }
    byte expectedMaterializableMask = (byte)((expectedAssignedLaneMask & 0x0F) |
        (expectedAssignedLaneMask & v.GetProperty("expectedPhysicalLaneMask").GetByte() & 0xF0));
    compositionPacked |= new BigInteger(expectedMaterializableMask) << compositionOffset;
    compositionPacked |= new BigInteger(expectedAssignedLaneMask) << (compositionOffset + 8);
    compositionPacked |= new BigInteger(expectedAssignedSlotMask) << (compositionOffset + 16);
    compositionPacked |= new BigInteger(expectedScalarLaneMask) << (compositionOffset + 24);
    int compositionMemoryWidth = (compositionOffset + 32) / 4;
    if (hasNonzeroCreateMatch)
    {
        compositionVectorMemory.AppendLine(compositionPacked.ToString("x", CultureInfo.InvariantCulture).PadLeft(compositionMemoryWidth, '0'));
        compositionVectorCount++;
    }
    vectorCount++;
}
if (vectorCount != header.RootElement.GetProperty("cases").GetInt32() || vectorCount < 5000 || compositionVectorCount == 0 || compositionDifferentMaskCount == 0)
    throw new InvalidDataException($"Resolver/composition oracle coverage is incomplete: resolver={vectorCount}, composition={compositionVectorCount}, differing slot/lane masks={compositionDifferentMaskCount}.");
File.WriteAllText(Path.Combine(output, "oracle-vectors.mem"), vectorMemory.ToString(), new UTF8Encoding(false));
File.WriteAllText(Path.Combine(output, "composition-oracle-vectors.mem"), compositionVectorMemory.ToString(), new UTF8Encoding(false));
File.Copy(oraclePath, Path.Combine(output, "oracle.jsonl"), overwrite: true);

string declarations = string.Join(Environment.NewLine, inputs.Select(p => $"  reg [{p.Type.Width - 1}:0] {p.Name};")) +
    Environment.NewLine + $"  wire [{outputPort.Type.Width - 1}:0] {outputPort.Name};";
string connections = string.Join("," + Environment.NewLine, inputs.Select(p => $"    .{p.Name}({p.Name})")) + "," + Environment.NewLine +
    $"    .{outputPort.Name}({outputPort.Name})";
int offset = 0;
var assignments = new List<string>();
foreach (HwPortContract p in inputs)
{
    assignments.Add($"      {p.Name} = vectors[i][{offset} +: {p.Type.Width}];");
    offset += p.Type.Width;
}
string testbench = $$"""
module tb;
{{declarations}}
  issue_packet_nonscalar_physical_lane_mask dut (
{{connections}}
  );
  reg [{{expectedOffset + outputPort.Type.Width - 1}}:0] vectors [0:{{vectorCount - 1}}];
  integer i;
  initial begin
    $readmemh("oracle-vectors.mem", vectors);
    for (i = 0; i < {{vectorCount}}; i = i + 1) begin
{{string.Join(Environment.NewLine, assignments)}}
      #1;
      if ({{outputPort.Name}} !== vectors[i][{{expectedOffset}} +: {{outputPort.Type.Width}}]) begin
        $display("MISMATCH row=%0d actual=%02x expected=%02x", i, {{outputPort.Name}}, vectors[i][{{expectedOffset}} +: {{outputPort.Type.Width}}]);
        $fatal(1);
      end
    end
    $display("PASS exact-source non-scalar resolver vectors={{vectorCount}}");
    $finish;
  end
endmodule
""";
File.WriteAllText(Path.Combine(output, "resolver_tb.sv"), testbench, new UTF8Encoding(false));

HwModule compositionModule = compositionDesign.Design.Modules.Single(m => m.Id == compositionDesign.Design.TopModule);
HwPortContract[] compositionPorts = compositionModule.Ports.ToArray();
HwPortContract[] compositionInputsForTb = compositionPorts.Where(p => p.Direction == HwPortDirection.Input).ToArray();
HwPortContract compositionOutputForTb = compositionPorts.Single(p => p.Direction == HwPortDirection.Output && p.Name == "materializable_lane_mask");
int compositionExpectedOffset = compositionInputsForTb.Sum(p => p.Type.Width);
int compositionVectorWidth = compositionExpectedOffset + compositionOutputForTb.Type.Width;
string compositionDeclarations = string.Join(Environment.NewLine, compositionPorts.Select(p =>
    $"  {(p.Direction == HwPortDirection.Input ? "reg" : "wire")} [{p.Type.Width - 1}:0] {p.Name};"));
string compositionConnections = string.Join("," + Environment.NewLine, compositionPorts.Select(p => $"    .{p.Name}({p.Name})"));
int compositionBitOffset = 0;
var compositionAssignments = new List<string>();
foreach (HwPortContract p in compositionInputsForTb)
{
    compositionAssignments.Add($"      {p.Name} = composition_vectors[i][{compositionBitOffset} +: {p.Type.Width}];");
    compositionBitOffset += p.Type.Width;
}
string compositionTestbench = $$"""
module composition_tb;
{{compositionDeclarations}}
  issue_packet_create_materialization_composition dut (
{{compositionConnections}}
  );
  reg [{{compositionVectorWidth + 31}}:0] composition_vectors [0:{{compositionVectorCount - 1}}];
  integer i;
  initial begin
    $readmemh("composition-oracle-vectors.mem", composition_vectors);
    for (i = 0; i < {{compositionVectorCount}}; i = i + 1) begin
{{string.Join(Environment.NewLine, compositionAssignments)}}
      #1;
      if ({{compositionOutputForTb.Name}} !== composition_vectors[i][{{compositionExpectedOffset}} +: {{compositionOutputForTb.Type.Width}}]) begin
        $display("COMPOSITION MISMATCH row=%0d actual=%02x expected=%02x", i, {{compositionOutputForTb.Name}}, composition_vectors[i][{{compositionExpectedOffset}} +: {{compositionOutputForTb.Type.Width}}]);
        $fatal(1);
      end
      if (assigned_lane_mask !== composition_vectors[i][{{compositionExpectedOffset + 8}} +: 8] ||
          assigned_slot_mask !== composition_vectors[i][{{compositionExpectedOffset + 16}} +: 8] ||
          scalar_lane_mask !== composition_vectors[i][{{compositionExpectedOffset + 24}} +: 8]) begin
        $display("CREATE MISMATCH row=%0d lane=%02x/%02x slot=%02x/%02x scalar=%02x/%02x", i,
          assigned_lane_mask, composition_vectors[i][{{compositionExpectedOffset + 8}} +: 8],
          assigned_slot_mask, composition_vectors[i][{{compositionExpectedOffset + 16}} +: 8],
          scalar_lane_mask, composition_vectors[i][{{compositionExpectedOffset + 24}} +: 8]);
        $fatal(1);
      end
    end
    $display("PASS integrated nonzero Create-resolver-materialization hierarchy vectors={{compositionVectorCount}} differing_slot_lane_masks={{compositionDifferentMaskCount}}");
    $finish;
  end
endmodule
""";
File.WriteAllText(Path.Combine(output, "composition_tb.sv"), compositionTestbench, new UTF8Encoding(false));

var zeroInputs = inputs.Select(p => $"input wire [{p.Type.Width - 1}:0] {p.Name}").Append($"output wire [{outputPort.Type.Width - 1}:0] {outputPort.Name}");
string zeroModule = "module issue_packet_nonscalar_physical_lane_mask(" + string.Join(",\n", zeroInputs) + ");\n" +
    $"assign {outputPort.Name} = {outputPort.Type.Width}'h00;\nendmodule\n";
File.WriteAllText(Path.Combine(output, "resolver_zero_output.sv"), zeroModule, new UTF8Encoding(false));
File.WriteAllText(Path.Combine(output, "lowering-receipt.json"), JsonSerializer.Serialize(new
{
    schema = "circuitweave.issue-packet-nonscalar-resolver-hwir/v1",
    modelCommit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f",
    designDigest = design.CanonicalDigest,
    modules = design.Design.Modules.Count,
    inputs = inputs.Length,
    nodes = top.Nodes.Count,
    vectors = vectorCount,
    oracleSha256 = HashFile(oraclePath)
}, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
Console.WriteLine($"Validated source-bounded non-scalar resolver HWIR digest={design.CanonicalDigest}; nodes={top.Nodes.Count}; inputs={inputs.Length}; vectors={vectorCount}.");

static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static string[] sourceBytesArr(Func<int, byte[]> get) => [HashBytes(get(0)), HashBytes(get(1)), HashBytes(get(2)), HashBytes(get(3))];
