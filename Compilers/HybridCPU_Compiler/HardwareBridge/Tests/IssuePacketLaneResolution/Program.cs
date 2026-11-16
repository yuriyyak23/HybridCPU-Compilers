using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.HardwareBridge.Hwir;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;

var model = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE";
var paths = new[]
{
    Path.Combine(model, "CloseToHSL/Core/Pipeline/Core/RuntimeClusterAdmissionPreparation.BundleIssuePacket.cs"),
    Path.Combine(model, "CloseToHSL/Core/Legality/ClusterIssueIntent.cs"),
    Path.Combine(model, "CloseToHSL/Core/Decoder/ClusterIssuePreparation.cs"),
    Path.Combine(model, "CloseToHSL/Core/Pipeline/Scheduling/SlotLegality/SlotClassDefinitions.cs"),
    Path.Combine(model, "CloseToHSL/Core/Pipeline/Scheduling/SlotPlacementMetadata.cs"),
    Path.Combine(model, "CloseToHSL/Core/Pipeline/Metadata/BundleMetadata.cs"),
    Path.Combine(model, "CloseToHSL/Core/Decoder/DecodedBundleDescriptor.cs")
};
var bytes = paths.Select(File.ReadAllBytes).ToArray();
string[] hashes = bytes.Select(b => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant()).ToArray();
var toolchain = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
var design = BridgeHwirIssuePacketLaneResolution.Compile(bytes[0], bytes[3], bytes[5], [hashes[0], hashes[3], hashes[5]],
    "1bfe41a49cf11c80fa6c6aee87accb54b78b331f", toolchain);
Console.WriteLine($"Validated candidate: {design.CanonicalDigest}; inputs={string.Join(',', hashes)}");
var createDesign = BridgeHwirIssuePacketCreateAllocation.Compile(
    bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], hashes,
    "1bfe41a49cf11c80fa6c6aee87accb54b78b331f", toolchain);
Console.WriteLine($"Validated sequential Create projection: {createDesign.CanonicalDigest}; modules={createDesign.Design.Modules.Count}");
var hwirPath = Path.Combine(Path.GetTempPath(), "cw-issue-packet-create-allocation.hwir.json");
File.WriteAllBytes(hwirPath, new CanonicalHwirSerializer().Serialize(createDesign));
Console.WriteLine($"Serialized sequential HWIR: {hwirPath}");
var mlir = new HierarchicalCirctMlirLowerer().LowerToMlir(createDesign,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>())).Text;
var mlirPath = Path.Combine(Path.GetTempPath(), "cw-issue-packet-create-allocation.mlir");
File.WriteAllText(mlirPath, mlir, new UTF8Encoding(false));
Console.WriteLine($"Lowered sequential MLIR: {mlirPath}");
var widthMutation = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes[1]).Replace("ScalarIssueWidthCeiling = 4", "ScalarIssueWidthCeiling = 5", StringComparison.Ordinal));
if (widthMutation.AsSpan().SequenceEqual(bytes[1])) throw new InvalidOperationException("Builder width mutation target was not found.");
try
{
    byte[][] mutatedBytes = [bytes[0], widthMutation, bytes[2], bytes[3], bytes[4], bytes[5], bytes[6]];
    var mutatedHashes = mutatedBytes.Select(b => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant()).ToArray();
    _ = BridgeHwirIssuePacketCreateAllocation.Compile(bytes[0], widthMutation, bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], mutatedHashes,
        "1bfe41a49cf11c80fa6c6aee87accb54b78b331f", toolchain);
    throw new InvalidOperationException("Changed scalar builder width was accepted.");
}
catch (InvalidDataException) { Console.WriteLine("ScalarIssueWidthCeiling mutation refused."); }
var mutation = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes[0]).Replace("lane < 0", "lane < 1", StringComparison.Ordinal));
if (mutation.AsSpan().SequenceEqual(bytes[0])) throw new InvalidOperationException("Resolver mutation target was not found.");
try
{
    _ = BridgeHwirIssuePacketLaneResolution.Compile(mutation, bytes[3], bytes[5],
        [Convert.ToHexString(SHA256.HashData(mutation)).ToLowerInvariant(), hashes[3], hashes[5]],
        "1bfe41a49cf11c80fa6c6aee87accb54b78b331f", toolchain);
    throw new InvalidOperationException("Changed resolver body was accepted.");
}
catch (InvalidDataException) { Console.WriteLine("Resolver body mutation refused."); }
