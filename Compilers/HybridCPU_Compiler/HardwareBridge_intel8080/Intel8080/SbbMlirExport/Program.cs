using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;

const string artifactsRoot = @"C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080";
if (args.Length != 2 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]))
    throw new ArgumentException("Usage: <absolute-sbb-behavior-projection.json> <new-absolute-output-directory>");
string projectionPath = Path.GetFullPath(args[0]);
string outputDirectory = Path.GetFullPath(args[1]);
string allowedPrefix = Path.GetFullPath(artifactsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
if (!projectionPath.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) ||
    !outputDirectory.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Projection input and outputs must remain under artifacts/8080.");
if (!File.Exists(projectionPath)) throw new FileNotFoundException("SBB behavior projection is missing.", projectionPath);
foreach (string candidate in new[] { projectionPath, outputDirectory })
    for (DirectoryInfo? current = new(Path.GetDirectoryName(candidate)!); current is not null; current = current.Parent)
        if (Directory.Exists(current.FullName) && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked input/output path refused: " + current.FullName);
if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
    throw new IOException("Existing output path refused: " + outputDirectory);

using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(projectionPath));
JsonElement root = document.RootElement;
if (root.GetProperty("schema").GetString() != "hardwarebridge.intel8080.sbb-hwir/v1")
    throw new InvalidDataException("Unsupported SBB projection schema.");
string expectedFormula = "(A - operand - CY_in) mod 65536";
JsonElement resultEquation = root.GetProperty("equations").EnumerateArray()
    .Single(e => e.GetProperty("Name").GetString() == "result16");
if (resultEquation.GetProperty("Formula").GetString() != expectedFormula)
    throw new InvalidDataException("SBB 16-bit wrapped result equation changed; refusing CircuitWeave lowering.");
JsonElement sbbTrace = root.GetProperty("sourceTrace").EnumerateArray()
    .Single(e => e.GetProperty("symbol").GetString() == "SBB");
string sourceFile = sbbTrace.GetProperty("file").GetString()!;
string sourceSha = sbbTrace.GetProperty("sha256").GetString()!;
int sourceLine = sbbTrace.GetProperty("startLine").GetInt32();
if (!File.Exists(sourceFile) || sourceLine < 1 || !StringComparer.Ordinal.Equals(
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceFile))).ToLowerInvariant(), sourceSha))
    throw new InvalidDataException("The source-bound SBB file no longer matches the projection hash.");

var origin = new HwSourceOrigin(
    "unversioned-local-source-snapshot", new string('0', 40), sourceFile, "emu8080.Core",
    "Ops", "SBB", sourceSha, HwSourceRole.Primary, sourceLine, 1);
var bit1 = new HwBitVectorType(1, HwSignedness.Unsigned, false);
var bit8 = new HwBitVectorType(8, HwSignedness.Unsigned, false);
var bit16 = new HwBitVectorType(16, HwSignedness.Unsigned, false);
var ports = new HwPortContract[]
{
    new("A", HwPortDirection.Input, bit8),
    new("operand", HwPortDirection.Input, bit8),
    new("CY_in", HwPortDirection.Input, bit1),
    new("result16_out", HwPortDirection.Output, bit16)
};
var nodes = new HwNode[]
{
    new HwOperationNode("a16", "zext", ["A"], bit16, HwOverflowMode.Wrap, HwFaultSemantics.None, origin),
    new HwOperationNode("operand16", "zext", ["operand"], bit16, HwOverflowMode.Wrap, HwFaultSemantics.None, origin),
    new HwOperationNode("borrow16", "zext", ["CY_in"], bit16, HwOverflowMode.Wrap, HwFaultSemantics.None, origin),
    new HwOperationNode("diff_ab", "sub", ["a16", "operand16"], bit16, HwOverflowMode.Wrap, HwFaultSemantics.None, origin),
    new HwOperationNode("result16", "sub", ["diff_ab", "borrow16"], bit16, HwOverflowMode.Wrap, HwFaultSemantics.None, origin),
    new HwOutputDriveNode("drive_result16", "result16_out", "result16", origin)
};
var contract = new HwHardwareContract("intel8080.ops.sbb.wrapped-result",
    HwModuleKind.Combinational, ports, new HwTimingContract(0, 1),
    Overflow: HwOverflowMode.Wrap, Faults: HwFaultSemantics.None);
var module = new HwModule("sbb_result16", "sbb_result16", ports, nodes, [], contract, origin);
var design = new HwDesign("hwir/v0.1", "sbb_result16", [module],
    new HwToolchainIdentity("11.0.100", "15.0", "hwp-scalar-v1", "circt-cli"), []);
var validation = new ScalarCombinationalHardwareIrValidator().Validate(design);
if (!validation.Accepted || validation.Validated is null)
    throw new InvalidDataException("CircuitWeave scalar HWIR validation refused SBB result: " +
        string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));
var mlir = new ScalarCirctMlirLowerer().LowerToMlir(validation.Validated,
    new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>()));
var hwirSerializer = new CanonicalHwirSerializer();
string canonicalHwirDigest = hwirSerializer.ComputeSha256(design);
byte[] canonicalHwir = hwirSerializer.Serialize(new HwValidatedDesign(design, canonicalHwirDigest, []));

Directory.CreateDirectory(outputDirectory);
File.WriteAllBytes(Path.Combine(outputDirectory, "sbb-result16.hwir.json"), canonicalHwir);
File.WriteAllText(Path.Combine(outputDirectory, "sbb-result16.mlir"), mlir.Text, new UTF8Encoding(false));
string report = $"""
# CircuitWeave scalar SBB-result slice

This single-output combinational HWIR lowers only the source helper's wrapped 16-bit subtraction result. It zero-extends A, operand and CY_in to 16 bits, then performs two subtract operations.

- Source: `{sourceFile}:{sourceLine}`
- Source content SHA-256: `{sourceSha}`
- Local source snapshot is unversioned; `HwSourceOrigin.Commit` is the documented zero sentinel, not a Git commit.
- HWIR canonical digest: `{canonicalHwirDigest}`
- Scalar-admission digest: `{validation.Validated.CanonicalDigest}`
- MLIR SHA-256: `{mlir.ContentDigest}`
- Equation: `result16_out[15:0] = (A - operand - CY_in) mod 65536`
- The source's AC equation and Intel-selected RTL AC equation differ for some borrow-in cases; neither is lowered here.
- No opcode decode, flag outputs, memory access, reset, or sequential CPU state is included.
- Candidate artifact only; not complete SBB instruction RTL or a verified CPU.
""";
File.WriteAllText(Path.Combine(outputDirectory, "README.md"), report, new UTF8Encoding(false));
Console.WriteLine($"PASS scalar SBB result HWIR admitted; canonical={validation.Validated.CanonicalDigest}; MLIR={mlir.ContentDigest}; outputs={outputDirectory}");
