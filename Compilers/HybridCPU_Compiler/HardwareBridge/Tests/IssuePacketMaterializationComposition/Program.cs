using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using HybridCPU.Compiler.HardwareBridge.Hwir;

string model = args.Length > 0 ? args[0] : @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE";
string output = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Path.GetTempPath(), "cw-issue-packet-materialization-composition");
Directory.CreateDirectory(output);
string core = Path.Combine(model, "CloseToHSL/Core");
byte[] packet = File.ReadAllBytes(Path.Combine(core, "Pipeline/Core/RuntimeClusterAdmissionPreparation.BundleIssuePacket.cs"));
byte[] intent = File.ReadAllBytes(Path.Combine(core, "Legality/ClusterIssueIntent.cs"));
byte[] preparation = File.ReadAllBytes(Path.Combine(core, "Decoder/ClusterIssuePreparation.cs"));
byte[] classes = File.ReadAllBytes(Path.Combine(core, "Pipeline/Scheduling/SlotLegality/SlotClassDefinitions.cs"));
byte[] placement = File.ReadAllBytes(Path.Combine(core, "Pipeline/Scheduling/SlotPlacementMetadata.cs"));
byte[] bundle = File.ReadAllBytes(Path.Combine(core, "Pipeline/Metadata/BundleMetadata.cs"));
byte[] decode = File.ReadAllBytes(Path.Combine(core, "Decoder/DecodedBundleDescriptor.cs"));
byte[] materialization = File.ReadAllBytes(Path.Combine(core, "Pipeline/ExecutionFlow/Materialization/CPU_Core.PipelineExecution.Materialization.cs"));
byte[] issueLane = File.ReadAllBytes(Path.Combine(core, "Pipeline/Core/RuntimeClusterAdmissionPreparation.IssuePacketLane.cs"));
byte[] stageFlow = File.ReadAllBytes(Path.Combine(core, "Pipeline/ExecutionFlow/StageFlow/CPU_Core.PipelineExecution.StageFlow.cs"));
byte[] dependencySummary = File.ReadAllBytes(Path.Combine(core, "Decoder/DecodedBundleDescriptor.cs"));
string[] createHashes = new byte[][] { packet, intent, preparation, classes, placement, bundle, decode }.Select(b => Hash(b)).ToArray();
string[] materializationHashes = new byte[][] { materialization, packet, issueLane }.Select(b => Hash(b)).ToArray();
string[] pinnedCreate = [
    "9a46971ca2c41a52611a4c7fb65991df90c7645d1e25b21cbad63c38268b049c",
    "9ce089b7aabba6c9872a7320c5890cac6da414255046c780250e0e4808732667",
    "28349db7aa7df0d16747099ed952f742adb59b195f7a21ae3c3826b7052d1557",
    "489edf25fe528393aba2e298c259e7929f59260430b4946ccea4c713065d9ff1",
    "06cac68870c46136250bfdc4c5b195c6f569d7113d7c5d10bfbc41d5b0797f19",
    "3337b0187d2345b7bc40f8c2dd7732c5757696c2a91028353bd15dbf53e6cbe4",
    "ad3878ae15aad02e44f181ad7abb26d40b7da13ca6a7e46ae003b4a8a5e98c2f"
];
string[] pinnedMaterialization = [
    "8d265200a0faabc52f7c5b62eea7fa04a938f9fe1b8f61326aa3bed83bc39328",
    "9a46971ca2c41a52611a4c7fb65991df90c7645d1e25b21cbad63c38268b049c",
    "030a4262d0d57db5185b70e20ac3c61cce37bd471aa10116ce309b30971d665c"
];
string[] resolverHashes = new byte[][] { stageFlow, dependencySummary, issueLane, packet }.Select(b => Hash(b)).ToArray();
string[] pinnedResolver = [
    "672e9d2523dd04068dc63650a7f52a7df67a904abe57c5639abb3b8b89d2317e",
    "ad3878ae15aad02e44f181ad7abb26d40b7da13ca6a7e46ae003b4a8a5e98c2f",
    "030a4262d0d57db5185b70e20ac3c61cce37bd471aa10116ce309b30971d665c",
    "9a46971ca2c41a52611a4c7fb65991df90c7645d1e25b21cbad63c38268b049c"
];
if (!createHashes.SequenceEqual(pinnedCreate) || !materializationHashes.SequenceEqual(pinnedMaterialization) || !resolverHashes.SequenceEqual(pinnedResolver))
    throw new InvalidDataException("Pinned source snapshot differs.");
const string commit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
var toolchain = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
var design = BridgeHwirIssuePacketMaterializationComposition.Compile(packet, intent, preparation, classes,
    placement, bundle, decode, materialization, packet, issueLane, stageFlow, dependencySummary,
    createHashes, materializationHashes, resolverHashes, commit, toolchain);
byte[] canonical = new CanonicalHwirSerializer().Serialize(design);
File.WriteAllBytes(Path.Combine(output, "composition.hwir.json"), canonical);
string serialized = Encoding.UTF8.GetString(canonical);
for (int lane = 0; lane < 8; lane++)
{
    string suffix = lane.ToString(System.Globalization.CultureInfo.InvariantCulture);
    if (!serialized.Contains("materialization_lane" + suffix, StringComparison.Ordinal) ||
        !serialized.Contains("lane" + suffix + "_index", StringComparison.Ordinal) ||
        !serialized.Contains("lane" + suffix + "_occupancy", StringComparison.Ordinal) ||
        !serialized.Contains("lane" + suffix + "_materialize", StringComparison.Ordinal))
        throw new InvalidDataException($"Lane {lane} composition wiring is missing.");
}
for (int occupancy = 0; occupancy < 16; occupancy++)
for (int auxiliary = 0; auxiliary < 16; auxiliary++)
{
    byte expected = (byte)(occupancy | (auxiliary << 4));
    for (int lane = 0; lane < 8; lane++)
    {
        bool actual = lane < 4 ? (occupancy & (1 << lane)) != 0 : (auxiliary & (1 << (lane - 4))) != 0;
        bool expectedLane = (expected & (1 << lane)) != 0;
        if (actual != expectedLane) throw new InvalidDataException($"Lane {lane} mapping smoke failed.");
    }
}
string mlir = new HierarchicalCirctMlirLowerer().LowerToMlir(design,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>())).Text;
File.WriteAllText(Path.Combine(output, "composition.mlir"), mlir, new UTF8Encoding(false));
File.WriteAllText(Path.Combine(output, "source-hashes.txt"), string.Join(Environment.NewLine,
    ["commit=" + commit, ..createHashes.Select((h, i) => $"create[{i}]={h}"), ..materializationHashes.Select((h, i) => $"materialization[{i}]={h}"), ..resolverHashes.Select((h, i) => $"resolver[{i}]={h}")]), new UTF8Encoding(false));
if (design.Design.TopModule != "issue_packet_create_materialization_composition" ||
    design.Design.Modules.Count(m => m.Id == "issue_packet_materialization_eligibility") != 1 ||
    design.Design.Modules.Count(m => m.Id == "issue_packet_nonscalar_physical_lane_mask") != 1 ||
    design.Design.Modules.Count(m => m.Id == "issue_packet_create_allocation_projection") != 1)
    throw new InvalidDataException("Expected hierarchical Create and eligibility modules are missing.");
Console.WriteLine($"Validated composition {design.CanonicalDigest}; modules={design.Design.Modules.Count}; top inputs={design.Design.Modules.Single(m => m.Id == design.Design.TopModule).Ports.Count(p => p.Direction == HwPortDirection.Input)}");
Console.WriteLine($"Lowered hierarchical MLIR to {Path.Combine(output, "composition.mlir")}");
Console.WriteLine("Lane mapping contract: lanes 0..3 consume assigned_lane_mask occupancy; lanes 4..7 consume executable_non_scalar_mask.");

static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
