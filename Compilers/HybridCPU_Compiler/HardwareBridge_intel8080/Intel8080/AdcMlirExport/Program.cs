using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;

const string artifactsRoot = @"C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080";
if (args.Length != 2 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]))
    throw new ArgumentException("Usage: <absolute-adc-behavior-projection.json> <new-absolute-output-directory>");
string projectionPath = Path.GetFullPath(args[0]);
string outputDirectory = Path.GetFullPath(args[1]);
string allowedPrefix = Path.GetFullPath(artifactsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
if (!projectionPath.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) ||
    !outputDirectory.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Projection input and outputs must remain under artifacts/8080.");
if (!File.Exists(projectionPath)) throw new FileNotFoundException("ADC behavior projection is missing.", projectionPath);
foreach (string candidate in new[] { projectionPath, outputDirectory })
    for (DirectoryInfo? current = new(Path.GetDirectoryName(candidate)!); current is not null; current = current.Parent)
        if (Directory.Exists(current.FullName) && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked input/output path refused: " + current.FullName);
if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
    throw new IOException("Existing output path refused: " + outputDirectory);

using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(projectionPath));
JsonElement root = document.RootElement;
if (root.GetProperty("schema").GetString() != "hardwarebridge.intel8080.adc-hwir/v1")
    throw new InvalidDataException("Unsupported ADC projection schema.");
JsonElement sumEquation = root.GetProperty("equations").EnumerateArray()
    .Single(e => e.GetProperty("Name").GetString() == "sum9");
if (sumEquation.GetProperty("Formula").GetString() != "zero_extend_1(A) + zero_extend_1(operand) + zero_extend_8(CY_in)")
    throw new InvalidDataException("ADC widened-sum equation changed; refusing the CircuitWeave slice.");
JsonElement adcTrace = root.GetProperty("sourceTrace").EnumerateArray()
    .Single(e => e.GetProperty("symbol").GetString() == "ADC");
string sourceFile = adcTrace.GetProperty("file").GetString()!;
string sourceSha = adcTrace.GetProperty("sha256").GetString()!;
int sourceLine = adcTrace.GetProperty("startLine").GetInt32();
if (!File.Exists(sourceFile) || sourceLine < 1 || !StringComparer.Ordinal.Equals(
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceFile))).ToLowerInvariant(), sourceSha))
    throw new InvalidDataException("The source-bound ADC file no longer matches the projection hash.");

var origin = new HwSourceOrigin(
    "unversioned-local-source-snapshot", new string('0', 40), sourceFile, "emu8080.Core",
    "Ops", "ADC", sourceSha, HwSourceRole.Primary, sourceLine, 1);
var bit1 = new HwBitVectorType(1, HwSignedness.Unsigned, false);
var bit8 = new HwBitVectorType(8, HwSignedness.Unsigned, false);
var bit9 = new HwBitVectorType(9, HwSignedness.Unsigned, false);
var ports = new HwPortContract[]
{
    new("A", HwPortDirection.Input, bit8),
    new("operand", HwPortDirection.Input, bit8),
    new("CY_in", HwPortDirection.Input, bit1),
    new("sum9_out", HwPortDirection.Output, bit9)
};
var nodes = new HwNode[]
{
    new HwOperationNode("a9", "zext", ["A"], bit9, HwOverflowMode.Wrap, HwFaultSemantics.None, origin),
    new HwOperationNode("operand9", "zext", ["operand"], bit9, HwOverflowMode.Wrap, HwFaultSemantics.None, origin),
    new HwOperationNode("carry9", "zext", ["CY_in"], bit9, HwOverflowMode.Wrap, HwFaultSemantics.None, origin),
    new HwOperationNode("sum_ab", "add", ["a9", "operand9"], bit9, HwOverflowMode.Wrap, HwFaultSemantics.None, origin),
    new HwOperationNode("sum9", "add", ["sum_ab", "carry9"], bit9, HwOverflowMode.Wrap, HwFaultSemantics.None, origin),
    new HwOutputDriveNode("drive_sum9", "sum9_out", "sum9", origin)
};
var contract = new HwHardwareContract("intel8080.ops.adc.widened-sum",
    HwModuleKind.Combinational, ports, new HwTimingContract(0, 1),
    Overflow: HwOverflowMode.Wrap, Faults: HwFaultSemantics.None);
var module = new HwModule("adc_sum9", "adc_sum9", ports, nodes, [], contract, origin);
var design = new HwDesign("hwir/v0.1", "adc_sum9", [module],
    new HwToolchainIdentity("11.0.100", "15.0", "hwp-scalar-v1", "circt-cli"), []);
var validation = new ScalarCombinationalHardwareIrValidator().Validate(design);
if (!validation.Accepted || validation.Validated is null)
    throw new InvalidDataException("CircuitWeave scalar HWIR validation refused ADC sum: " +
        string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));
var mlir = new ScalarCirctMlirLowerer().LowerToMlir(validation.Validated,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>()));
var hwirSerializer = new CanonicalHwirSerializer();
string canonicalHwirDigest = hwirSerializer.ComputeSha256(design);
byte[] canonicalHwir = hwirSerializer.Serialize(new HwValidatedDesign(design, canonicalHwirDigest, []));

Directory.CreateDirectory(outputDirectory);
File.WriteAllBytes(Path.Combine(outputDirectory, "adc-sum9.hwir.json"), canonicalHwir);
File.WriteAllText(Path.Combine(outputDirectory, "adc-sum9.mlir"), mlir.Text, new UTF8Encoding(false));
string report = $"""
# CircuitWeave scalar ADC-sum slice

This single-output combinational HWIR lowers only the source-backed `sum9` equation. It zero-extends A, operand and explicit carry-in to 9 bits, then performs two additions.

- Source: `{sourceFile}:{sourceLine}`
- Source content SHA-256: `{sourceSha}`
- Local source snapshot is unversioned; `HwSourceOrigin.Commit` is the documented zero sentinel, not a Git commit.
- HWIR canonical digest: `{canonicalHwirDigest}`
- Scalar-admission digest: `{validation.Validated.CanonicalDigest}`
- MLIR SHA-256: `{mlir.ContentDigest}`
- Equation: `sum9_out[8:0] = zero_extend(A) + zero_extend(operand) + zero_extend(CY_in)`
- Flags, PC update, opcode decode, memory access and sequential CPU behavior are outside this module.
- CIRCT CLI binaries were unavailable in WSL during the corresponding ADD export; SystemVerilog was not emitted from MLIR.
- Candidate artifact only; not complete ADC instruction RTL or a verified CPU.
""";
File.WriteAllText(Path.Combine(outputDirectory, "README.md"), report, new UTF8Encoding(false));
Console.WriteLine($"PASS scalar ADC sum HWIR admitted; canonical={validation.Validated.CanonicalDigest}; MLIR={mlir.ContentDigest}; outputs={outputDirectory}");
