using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using HybridCPU.Compiler.HardwareBridge.Hwir;

string model = args.Length > 0 ? args[0] : @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE";
string output = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Path.GetTempPath(), "cw-issue-packet-materialize");
Directory.CreateDirectory(output);
string core = Path.Combine(model, "CloseToHSL/Core");
byte[] materialization = File.ReadAllBytes(Path.Combine(core, "Pipeline/ExecutionFlow/Materialization/CPU_Core.PipelineExecution.Materialization.cs"));
byte[] packet = File.ReadAllBytes(Path.Combine(core, "Pipeline/Core/RuntimeClusterAdmissionPreparation.BundleIssuePacket.cs"));
byte[] issueLane = File.ReadAllBytes(Path.Combine(core, "Pipeline/Core/RuntimeClusterAdmissionPreparation.IssuePacketLane.cs"));
string[] hashes = [Hash(materialization), Hash(packet), Hash(issueLane)];
string[] pinned = [
    "8d265200a0faabc52f7c5b62eea7fa04a938f9fe1b8f61326aa3bed83bc39328",
    "9a46971ca2c41a52611a4c7fb65991df90c7645d1e25b21cbad63c38268b049c",
    "030a4262d0d57db5185b70e20ac3c61cce37bd471aa10116ce309b30971d665c"
];
if (!hashes.SequenceEqual(pinned, StringComparer.Ordinal))
    throw new InvalidDataException("Issue-packet materialization source bytes differ from the pinned snapshot.");
var toolchain = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
var design = BridgeHwirIssuePacketMaterializationEligibility.Compile(materialization, packet, issueLane, hashes,
    "1bfe41a49cf11c80fa6c6aee87accb54b78b331f", toolchain);
File.WriteAllBytes(Path.Combine(output, "materialization-eligibility.hwir.json"), new CanonicalHwirSerializer().Serialize(design));
string mlir = new HierarchicalCirctMlirLowerer().LowerToMlir(design,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>())).Text;
File.WriteAllText(Path.Combine(output, "materialization-eligibility.mlir"), mlir, new UTF8Encoding(false));

string changed = Encoding.UTF8.GetString(materialization).Replace("laneIndex < 4", "laneIndex < 5", StringComparison.Ordinal);
if (changed == Encoding.UTF8.GetString(materialization)) throw new InvalidOperationException("Mutation target missing.");
try
{
    _ = BridgeHwirIssuePacketMaterializationEligibility.Compile(Encoding.UTF8.GetBytes(changed), packet, issueLane,
        [Hash(Encoding.UTF8.GetBytes(changed)), hashes[1], hashes[2]], "1bfe41a49cf11c80fa6c6aee87accb54b78b331f", toolchain);
    throw new InvalidOperationException("Changed lane boundary was accepted.");
}
catch (InvalidDataException) { Console.WriteLine("Source boundary mutation refused."); }
Console.WriteLine($"HWIR={design.CanonicalDigest}; modules={design.Design.Modules.Count}; inputs={string.Join(',', hashes)}");
Console.WriteLine($"Artifacts={output}");

static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
