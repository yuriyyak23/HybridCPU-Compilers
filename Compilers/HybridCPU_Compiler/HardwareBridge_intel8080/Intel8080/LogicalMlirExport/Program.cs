using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;

const string artifactsRoot = @"C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080";
if (args.Length != 2 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]))
    throw new ArgumentException("Usage: <absolute-logical-behavior-projection.json> <new-absolute-output-directory>");
string projectionPath = Path.GetFullPath(args[0]), outputDirectory = Path.GetFullPath(args[1]);
string allowedPrefix = Path.GetFullPath(artifactsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
if (!projectionPath.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) || !outputDirectory.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Projection input and outputs must remain under artifacts/8080.");
foreach (string candidate in new[] { projectionPath, outputDirectory })
    for (DirectoryInfo? current = new(Path.GetDirectoryName(candidate)!); current is not null; current = current.Parent)
        if (Directory.Exists(current.FullName) && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked input/output path refused: " + current.FullName);
if (!File.Exists(projectionPath)) throw new FileNotFoundException("Logical projection is missing.", projectionPath);
if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory)) throw new IOException("Existing output path refused: " + outputDirectory);
using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(projectionPath));
JsonElement root = doc.RootElement;
if (root.GetProperty("schema").GetString() != "hardwarebridge.intel8080.logical-hwir/v1") throw new InvalidDataException("Unsupported logical projection schema.");
var trace = root.GetProperty("sourceTrace").EnumerateArray().ToArray();
foreach (JsonElement row in trace)
{
    string file = row.GetProperty("file").GetString()!, sha = row.GetProperty("sha256").GetString()!;
    if (!File.Exists(file) || !StringComparer.Ordinal.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant(), sha))
        throw new InvalidDataException("Source hash differs from logical projection: " + file);
}
var operations = new[] { (Name: "and", Source: "ANA"), (Name: "or", Source: "ORA_B"), (Name: "xor", Source: "XOR") };
var output = new List<object>();
var stagedFiles = new List<(string Name, byte[] Bytes)>();
var serializer = new CanonicalHwirSerializer();
foreach (var item in operations)
{
    JsonElement source = trace.Single(row => row.GetProperty("symbol").GetString() == item.Source);
    string file = source.GetProperty("file").GetString()!, sha = source.GetProperty("sha256").GetString()!;
    int line = source.GetProperty("startLine").GetInt32();
    var origin = new HwSourceOrigin("unversioned-local-source-snapshot", new string('0', 40), file, "emu8080.Core", "Ops", item.Source, sha, HwSourceRole.Primary, line, 1);
    var bit8 = new HwBitVectorType(8, HwSignedness.Unsigned, false);
    var ports = new HwPortContract[] { new("A", HwPortDirection.Input, bit8), new("operand", HwPortDirection.Input, bit8), new("result_out", HwPortDirection.Output, bit8) };
    var nodes = new HwNode[] { new HwOperationNode("result", item.Name, ["A", "operand"], bit8, HwOverflowMode.Wrap, HwFaultSemantics.None, origin), new HwOutputDriveNode("drive_result", "result_out", "result", origin) };
    var contract = new HwHardwareContract("intel8080.logical." + item.Name, HwModuleKind.Combinational, ports, new HwTimingContract(0, 1), Overflow: HwOverflowMode.Wrap, Faults: HwFaultSemantics.None);
    var module = new HwModule("logical_" + item.Name, "logical_" + item.Name, ports, nodes, [], contract, origin);
    var design = new HwDesign("hwir/v0.1", "logical_" + item.Name, [module], new HwToolchainIdentity("11.0.100", "15.0", "hwp-scalar-v1", "circt-cli"), []);
    var validated = new ScalarCombinationalHardwareIrValidator().Validate(design);
    if (!validated.Accepted || validated.Validated is null) throw new InvalidDataException($"CircuitWeave refused {item.Name}: " + string.Join("; ", validated.Diagnostics.Select(d => d.Code + " " + d.Message)));
    var mlir = new ScalarCirctMlirLowerer().LowerToMlir(validated.Validated, new HardwareBackendOptions("circt-cli", "", null, new Dictionary<string, string>()));
    string digest = serializer.ComputeSha256(design), stem = "logical-" + item.Name;
    stagedFiles.Add((stem + ".hwir.json", serializer.Serialize(new HwValidatedDesign(design, digest, []))));
    stagedFiles.Add((stem + ".mlir", new UTF8Encoding(false).GetBytes(mlir.Text)));
    output.Add(new { operation = item.Name, source = file, sourceLine = line, sourceSha256 = sha, hwirSha256 = digest, admissionSha256 = validated.Validated.CanonicalDigest, mlirSha256 = mlir.ContentDigest });
}
Directory.CreateDirectory(outputDirectory);
foreach (var file in stagedFiles) File.WriteAllBytes(Path.Combine(outputDirectory, file.Name), file.Bytes);
File.WriteAllText(Path.Combine(outputDirectory, "README.md"), "# CircuitWeave logical result slices\n\nThree separate single-output HWIR modules lower `A & operand`, `A | operand`, and `A ^ operand` to MLIR. Each output is source-pinned to the corresponding C# helper, but the result slices do not contain flags, PC updates, instruction decode or CPU control. Source `AOR` used by `ORI_D` is known to perform AND; the architectural OR module does not claim to reproduce that defect.\n\n" + JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
Console.WriteLine($"PASS CircuitWeave admitted and lowered AND/OR/XOR result slices: {outputDirectory}");
