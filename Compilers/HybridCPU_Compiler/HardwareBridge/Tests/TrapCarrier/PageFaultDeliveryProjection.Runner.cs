using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using HybridCPU.Compiler.HardwareBridge.Hwir;

if (args.Length != 3)
    throw new ArgumentException("Usage: PageFaultDeliveryProjection.Runner <active-ise-root> <golden-ise-root> <new-output-directory>");

string activeRoot = Path.GetFullPath(args[0]);
string goldenRoot = Path.GetFullPath(args[1]);
string output = Path.GetFullPath(args[2]);
if (!Directory.Exists(activeRoot) || !Directory.Exists(goldenRoot) || Directory.Exists(output))
    throw new InvalidOperationException("Active and Golden source roots must exist and output must be new.");

const string exceptionRelative = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/Faults/CPU_Core.PipelineExecution.Exceptions.cs";
const string helperRelative = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.Pipeline.Helpers.cs";
const string stageEnumRelative = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.Pipeline.Helpers.PipelineStage.cs";
byte[] Read(string root, string relative) => File.ReadAllBytes(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
byte[] activeExceptions = Read(activeRoot, exceptionRelative);
byte[] activeHelpers = Read(activeRoot, helperRelative);
byte[] activeStageEnum = Read(activeRoot, stageEnumRelative);
byte[] goldenExceptions = Read(goldenRoot, exceptionRelative);
byte[] goldenHelpers = Read(goldenRoot, helperRelative);
byte[] goldenStageEnum = Read(goldenRoot, stageEnumRelative);
string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

var toolchain = new HwToolchainIdentity("11.0.100", "15.0", "hwp-hwir-comb-v1", "circt-cli");
HwValidatedDesign candidate = BridgeHwirPageFaultDeliveryProjection.Compile(
    activeExceptions, activeHelpers, activeStageEnum, goldenExceptions, goldenHelpers, goldenStageEnum, toolchain);
byte[] hwir = new CanonicalHwirSerializer().Serialize(candidate);
HardwareMlirModule mlir = new HierarchicalCirctMlirLowerer().LowerToMlir(candidate,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>()));
Directory.CreateDirectory(output);
File.WriteAllBytes(Path.Combine(output, "page-fault-delivery.hwir.json"), hwir);
File.WriteAllText(Path.Combine(output, "page-fault-delivery.mlir"), mlir.Text, new UTF8Encoding(false));
var manifest = new
{
    schema = "circuitweave.processor-page-fault-delivery-hwir/v1",
    producerHead = "e4e2faeda14184ed9832f3bdab70bcdf9aeb0fad",
    modelCommit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f",
    goldenCommit = "6946ccab9d888f7dae0a4d8e18480f436138acd9",
    toolchain = new { sdk = "11.0.100", languageVersion = "15.0", profile = "hwp-hwir-comb-v1", backend = "circt-cli" },
    inputs = new[]
    {
        new { path = exceptionRelative, activeSha256 = Sha(activeExceptions), goldenMaterializedSha256 = Sha(goldenExceptions), goldenGitBlob = "d4bdff84b4c634bfc41ab34289bec80bd48d62af" },
        new { path = helperRelative, activeSha256 = Sha(activeHelpers), goldenMaterializedSha256 = Sha(goldenHelpers), goldenGitBlob = "c17ea0c5930abbf9002868be225b68291ecb446d" },
        new { path = stageEnumRelative, activeSha256 = Sha(activeStageEnum), goldenMaterializedSha256 = Sha(goldenStageEnum), goldenGitBlob = "771660b2c55167e12fc7b8fa2a4393353e20cb4a" }
    },
    hwirSha256 = Sha(hwir),
    mlirSha256 = Sha(Encoding.UTF8.GetBytes(mlir.Text)),
    moduleCount = candidate.Design.Modules.Count,
    nodeCount = candidate.Design.Modules.Sum(module => module.Nodes.Count),
    trust = "StructuralOnly",
    claim = "Combinational delivery branch projection after source-resolved winner metadata; no arbitration RTL, clock/reset, CSR/mtvec trap entry, or sequential equivalence."
};
File.WriteAllText(Path.Combine(output, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(manifest,
    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(manifest));
