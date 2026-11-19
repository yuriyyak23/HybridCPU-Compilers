using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CircuitWeave.Backend;
using CircuitWeave.Backend.Circt;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;

const string artifactsRoot = @"C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080";
if(args.Length!=2||!Path.IsPathFullyQualified(args[0])||!Path.IsPathFullyQualified(args[1]))
    throw new ArgumentException("Usage: <absolute-dad-behavior-projection.json> <new-absolute-output-directory>");
string projectionPath=Path.GetFullPath(args[0]), outputDirectory=Path.GetFullPath(args[1]);
string allowedPrefix=Path.GetFullPath(artifactsRoot).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
if(!projectionPath.StartsWith(allowedPrefix,StringComparison.OrdinalIgnoreCase)||!outputDirectory.StartsWith(allowedPrefix,StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Projection input and outputs must remain under artifacts/8080.");
if(!File.Exists(projectionPath))throw new FileNotFoundException("DAD behavior projection is missing.",projectionPath);
foreach(string candidate in new[]{projectionPath,outputDirectory})
    for(DirectoryInfo? current=new(Path.GetDirectoryName(candidate)!);current is not null;current=current.Parent)
        if(Directory.Exists(current.FullName)&&(current.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked input/output path refused: "+current.FullName);
if(Directory.Exists(outputDirectory)||File.Exists(outputDirectory))throw new IOException("Existing output path refused: "+outputDirectory);

using JsonDocument document=JsonDocument.Parse(File.ReadAllBytes(projectionPath));
JsonElement root=document.RootElement;
if(root.GetProperty("schema").GetString()!="hardwarebridge.intel8080.dad-hwir/v1")throw new InvalidDataException("Unsupported DAD projection schema.");
var expected=new Dictionary<string,string>(StringComparer.Ordinal)
{
    ["sum17"]="zero_extend_1(HL) + zero_extend_1(operand)",
    ["HL_next"]="sum17[15:0]"
};
var equations=root.GetProperty("equations").EnumerateArray().ToArray();
foreach(JsonElement equation in equations)
{
    string? name=equation.GetProperty("Name").GetString();
    if(name is not null&&expected.TryGetValue(name,out string? formula)&&equation.GetProperty("Formula").GetString()!=formula)
        throw new InvalidDataException("DAD sum formula changed; refusing CircuitWeave scalar lowering.");
}
if(equations.Count(e=>e.GetProperty("Name").GetString()=="sum17")!=1||equations.Count(e=>e.GetProperty("Name").GetString()=="HL_next")!=1)
    throw new InvalidDataException("DAD sum equations are missing or duplicated.");
var dispatch=root.GetProperty("dispatch").EnumerateArray().ToArray();
if(dispatch.Length!=4||!dispatch.Select(e=>e.GetProperty("opcode").GetString()).SequenceEqual(new[]{"0x09","0x19","0x29","0x39"}))
    throw new InvalidDataException("DAD source dispatch coverage changed.");

var trace=root.GetProperty("sourceTrace").EnumerateArray().ToArray();
var dadTrace=trace.Single(e=>e.GetProperty("symbol").GetString()=="DAD");
string sourceFile=dadTrace.GetProperty("file").GetString()!,sourceSha=dadTrace.GetProperty("sha256").GetString()!;
int sourceLine=dadTrace.GetProperty("startLine").GetInt32();
if(!File.Exists(sourceFile)||sourceLine<1||!StringComparer.Ordinal.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceFile))).ToLowerInvariant(),sourceSha))
    throw new InvalidDataException("Source-bound Registers.DAD no longer matches the projection hash.");
foreach(JsonElement row in trace)
{
    string file=row.GetProperty("file").GetString()!,sha=row.GetProperty("sha256").GetString()!;
    if(!File.Exists(file)||!StringComparer.Ordinal.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant(),sha))
        throw new InvalidDataException("A DAD dependency changed after source projection: "+file);
}

var origin=new HwSourceOrigin("unversioned-local-source-snapshot",new string('0',40),sourceFile,"emu8080.Core","Registers","DAD",sourceSha,HwSourceRole.Primary,sourceLine,1);
var bit16=new HwBitVectorType(16,HwSignedness.Unsigned,false); var bit17=new HwBitVectorType(17,HwSignedness.Unsigned,false);
var ports=new HwPortContract[]{new("HL",HwPortDirection.Input,bit16),new("operand",HwPortDirection.Input,bit16),new("sum17_out",HwPortDirection.Output,bit17)};
var nodes=new HwNode[]{
    new HwOperationNode("hl17","zext",["HL"],bit17,HwOverflowMode.Wrap,HwFaultSemantics.None,origin),
    new HwOperationNode("operand17","zext",["operand"],bit17,HwOverflowMode.Wrap,HwFaultSemantics.None,origin),
    new HwOperationNode("sum17","add",["hl17","operand17"],bit17,HwOverflowMode.Wrap,HwFaultSemantics.None,origin),
    new HwOutputDriveNode("drive_sum17","sum17_out","sum17",origin)
};
var contract=new HwHardwareContract("intel8080.ops.dad.widened-sum",HwModuleKind.Combinational,ports,new HwTimingContract(0,1),Overflow:HwOverflowMode.Wrap,Faults:HwFaultSemantics.None);
var module=new HwModule("dad_sum17","dad_sum17",ports,nodes,[],contract,origin);
var design=new HwDesign("hwir/v0.1","dad_sum17",[module],new HwToolchainIdentity("11.0.100","15.0","hwp-scalar-v1","circt-cli"),[]);
var validation=new ScalarCombinationalHardwareIrValidator().Validate(design);
if(!validation.Accepted||validation.Validated is null)throw new InvalidDataException("CircuitWeave refused DAD sum17: "+string.Join("; ",validation.Diagnostics.Select(d=>d.Code+" "+d.Message)));
var mlir=new ScalarCirctMlirLowerer().LowerToMlir(validation.Validated,new HardwareBackendOptions("circt-cli","",null,new Dictionary<string,string>()));
var serializer=new CanonicalHwirSerializer(); string canonicalDigest=serializer.ComputeSha256(design);
byte[] canonical=serializer.Serialize(new HwValidatedDesign(design,canonicalDigest,[]));
Directory.CreateDirectory(outputDirectory);
File.WriteAllBytes(Path.Combine(outputDirectory,"dad-sum17.hwir.json"),canonical);
File.WriteAllText(Path.Combine(outputDirectory,"dad-sum17.mlir"),mlir.Text,new UTF8Encoding(false));
string report=$"""
# CircuitWeave scalar DAD-sum slice

This is the one-output combinational projection of source-pinned DAD equation `sum17`.

- Primary source: `{sourceFile}:{sourceLine}`
- Source SHA-256: `{sourceSha}`; all traced source dependencies are re-hashed before lowering.
- Source opcodes: `09h DAD B`, `19h DAD D`, `29h DAD H`, `39h DAD SP`.
- HWIR: `{canonicalDigest}`; scalar admission: `{validation.Validated.CanonicalDigest}`.
- MLIR SHA-256: `{mlir.ContentDigest}`.
- Interface: unsigned `HL[15:0]`, `operand[15:0]`; output `sum17[16:0] = zext(HL) + zext(operand)`.
- `sum17[15:0]` is next HL; `sum17[16]` is CY. S/Z/P/AC preservation and PC increment are source-mapped in the separate projection but not part of this scalar module.
- Result range is 0 through 0x1FFFE. No firtool/CIRCT SystemVerilog conversion or whole-CPU sequencing is claimed.
- Candidate scalar artifact only; not a complete DAD instruction implementation or processor verification.
""";
File.WriteAllText(Path.Combine(outputDirectory,"README.md"),report,new UTF8Encoding(false));
Console.WriteLine($"PASS scalar DAD sum HWIR admitted; canonical={validation.Validated.CanonicalDigest}; MLIR={mlir.ContentDigest}; outputs={outputDirectory}");
