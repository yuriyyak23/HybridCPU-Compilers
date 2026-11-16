using System.Security.Cryptography;
using System.Text.Json;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using HybridCPU.Compiler.HardwareBridge.Hwir;

const string source = @"C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE\CloseToHSL\Core\Execution\DmaStreamCompute\VmxDmaDescriptorValidator.cs";
const string output = @"C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\processor-20261114\vmx-validation-current-v1";
byte[] bytes = File.ReadAllBytes(source);
var toolchain = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
HwValidatedDesign design = BridgeHwirVmxValidationCurrent.Compile(bytes,
    "1bfe41a49cf11c80fa6c6aee87accb54b78b331f", toolchain);
byte[] hwir = new CanonicalHwirSerializer().Serialize(design);
Directory.CreateDirectory(output);
File.WriteAllBytes(Path.Combine(output, "vmx-validation-current.hwir.json"), hwir);
bool mutationRejected = false;
byte[] mutant = (byte[])bytes.Clone(); mutant[^1] ^= 1;
try { BridgeHwirVmxValidationCurrent.Compile(mutant, "1bfe41a49cf11c80fa6c6aee87accb54b78b331f", toolchain); }
catch (InvalidDataException) { mutationRejected = true; }
if (!mutationRejected) throw new InvalidDataException("Mutated source was accepted.");
var receipt = new
{
    schema = "circuitweave.vmx-validation-current-structural-receipt/v1",
    status = "STRUCTURAL_ONLY_NO_RTL_OR_GOLDEN_RUNTIME_COMPARISON",
    activeSource = source,
    activeSourceSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
    goldenSource = "not present at pinned commit 6946ccab9d888f7dae0a4d8e18480f436138acd9",
    hwirSha256 = Convert.ToHexString(SHA256.HashData(hwir)).ToLowerInvariant(),
    sourceMutationRejected = mutationRejected,
    designDigest = design.CanonicalDigest,
    modules = design.Design.Modules.Count,
    module = design.Design.Modules.Single().Name,
    ports = design.Design.Modules.Single().Ports.Select(p => new { p.Name, direction = p.Direction.ToString(), type = p.Type.ToString() }),
    open = new[] { "binding_found abstracts TryResolveBinding lookup over IoDomainTag/DomainTag/DeviceId", "no independent runtime/RTL vectors", "no DMA queue/arbitration or CPU integration", "no Golden file at pinned revision" }
};
File.WriteAllBytes(Path.Combine(output, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(receipt, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"VMX validation-current HWIR structural PASS: {design.CanonicalDigest}; mutationRejected={mutationRejected}; no Golden comparison");
