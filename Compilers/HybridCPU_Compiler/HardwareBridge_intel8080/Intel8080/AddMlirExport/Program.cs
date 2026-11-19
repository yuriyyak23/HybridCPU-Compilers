using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;

const string artifactsRoot = @"C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080";
if (args.Length != 2 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]))
    throw new ArgumentException("Usage: <absolute-add-behavior-projection.json> <new-absolute-output-directory>");
string projectionPath = Path.GetFullPath(args[0]);
string outputDirectory = Path.GetFullPath(args[1]);
string allowedPrefix = Path.GetFullPath(artifactsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
if (!projectionPath.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) ||
    !outputDirectory.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Projection input and outputs must remain under artifacts/8080.");
if (!File.Exists(projectionPath)) throw new FileNotFoundException("ADD behavior projection is missing.", projectionPath);
foreach (string candidate in new[] { projectionPath, outputDirectory })
    for (DirectoryInfo? current = new(Path.GetDirectoryName(candidate)!); current is not null; current = current.Parent)
        if (Directory.Exists(current.FullName) && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked input/output path refused: " + current.FullName);
if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
    throw new IOException("Existing output path refused: " + outputDirectory);

using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(projectionPath));
JsonElement root = document.RootElement;
if (root.GetProperty("schema").GetString() != "hardwarebridge.intel8080.add-hwir/v1")
    throw new InvalidDataException("Unsupported ADD projection schema.");
var expectedEquations = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["sum9"] = "zero_extend_1(A) + zero_extend_1(operand)",
    ["result8"] = "sum9[7:0]"
};
foreach (JsonElement equation in root.GetProperty("equations").EnumerateArray())
{
    string? name = equation.GetProperty("Name").GetString();
    if (name is not null && expectedEquations.TryGetValue(name, out string? formula) &&
        equation.GetProperty("Formula").GetString() != formula)
        throw new InvalidDataException("ADD sum formula changed; refusing the CircuitWeave slice.");
}
if (root.GetProperty("equations").EnumerateArray().Count(e => e.GetProperty("Name").GetString() == "sum9") != 1 ||
    root.GetProperty("equations").EnumerateArray().Count(e => e.GetProperty("Name").GetString() == "result8") != 1)
    throw new InvalidDataException("ADD sum equations are missing or duplicated.");

JsonElement addTrace = root.GetProperty("sourceTrace").EnumerateArray()
    .Single(e => e.GetProperty("symbol").GetString() == "ADD");
string sourceFile = addTrace.GetProperty("file").GetString()!;
string sourceSha = addTrace.GetProperty("sha256").GetString()!;
int sourceLine = addTrace.GetProperty("startLine").GetInt32();
if (!File.Exists(sourceFile) || sourceLine < 1 || !StringComparer.Ordinal.Equals(
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceFile))).ToLowerInvariant(), sourceSha))
    throw new InvalidDataException("The source-bound ADD file no longer matches the projection hash.");

// The source folder has no Git metadata. A zero commit is a deliberate sentinel,
// not a claimed VCS revision; exact content identity remains sourceSha.
var origin = new HwSourceOrigin(
    "unversioned-local-source-snapshot", new string('0', 40), sourceFile, "emu8080.Core",
    "Ops", "ADD", sourceSha, HwSourceRole.Primary, sourceLine, 1);
var bit8 = new HwBitVectorType(8, HwSignedness.Unsigned, false);
var bit9 = new HwBitVectorType(9, HwSignedness.Unsigned, false);
var ports = new HwPortContract[]
{
    new("A", HwPortDirection.Input, bit8),
    new("operand", HwPortDirection.Input, bit8),
    new("sum9_out", HwPortDirection.Output, bit9)
};
var nodes = new HwNode[]
{
    new HwOperationNode("a9", "zext", ["A"], bit9, HwOverflowMode.Wrap, HwFaultSemantics.None, origin),
    new HwOperationNode("operand9", "zext", ["operand"], bit9, HwOverflowMode.Wrap, HwFaultSemantics.None, origin),
    new HwOperationNode("sum9", "add", ["a9", "operand9"], bit9, HwOverflowMode.Wrap, HwFaultSemantics.None, origin),
    new HwOutputDriveNode("drive_sum9", "sum9_out", "sum9", origin)
};
var contract = new HwHardwareContract("intel8080.ops.add.widened-sum",
    HwModuleKind.Combinational, ports, new HwTimingContract(0, 1),
    Overflow: HwOverflowMode.Wrap, Faults: HwFaultSemantics.None);
var module = new HwModule("add_sum9", "add_sum9", ports, nodes, [], contract, origin);
var design = new HwDesign("hwir/v0.1", "add_sum9", [module],
    new HwToolchainIdentity("11.0.100", "15.0", "hwp-scalar-v1", "circt-cli"), []);
var validation = new ScalarCombinationalHardwareIrValidator().Validate(design);
if (!validation.Accepted || validation.Validated is null)
    throw new InvalidDataException("CircuitWeave scalar HWIR validation refused the ADD sum: " +
        string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));
var mlir = new ScalarCirctMlirLowerer().LowerToMlir(validation.Validated,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>()));
var hwirSerializer = new CanonicalHwirSerializer();
string canonicalHwirDigest = hwirSerializer.ComputeSha256(design);
// Canonical serialization identity is distinct from the scalar-admission digest.
byte[] canonicalHwir = hwirSerializer.Serialize(new HwValidatedDesign(design, canonicalHwirDigest, []));

Directory.CreateDirectory(outputDirectory);
File.WriteAllBytes(Path.Combine(outputDirectory, "add-sum9.hwir.json"), canonicalHwir);
File.WriteAllText(Path.Combine(outputDirectory, "add-sum9.mlir"), mlir.Text, new UTF8Encoding(false));
string report = $"""
# CircuitWeave scalar ADD-sum slice

This is a single-output combinational projection of the source-backed `sum9` equation only.
It was constructed as typed HWIR, admitted by `ScalarCombinationalHardwareIrValidator`, and lowered with `ScalarCirctMlirLowerer`.

- Source: `{sourceFile}:{sourceLine}`
- Source content SHA-256: `{sourceSha}`
- Local source snapshot is unversioned; `HwSourceOrigin.Commit` is the documented zero sentinel, not a Git commit.
- HWIR canonical digest: `{canonicalHwirDigest}`
- Scalar-admission digest: `{validation.Validated.CanonicalDigest}`
- MLIR SHA-256: `{mlir.ContentDigest}`
- Inputs: unsigned A[7:0], operand[7:0]
- Output: sum9_out[8:0] = zero_extend(A) + zero_extend(operand)
- CIRCT scope: one combinational output. Flags, PC update, opcode decode, memory, and CPU sequencing are outside this scalar module.
- No firtool/circt-opt executable was available in WSL during this run; SystemVerilog export from this MLIR was not performed.
- This is a candidate artifact, not Golden, verified RTL, or a complete ADD instruction implementation.
""";
File.WriteAllText(Path.Combine(outputDirectory, "README.md"), report, new UTF8Encoding(false));
Console.WriteLine($"PASS scalar ADD sum HWIR admitted; canonical={validation.Validated.CanonicalDigest}; MLIR={mlir.ContentDigest}; outputs={outputDirectory}");
