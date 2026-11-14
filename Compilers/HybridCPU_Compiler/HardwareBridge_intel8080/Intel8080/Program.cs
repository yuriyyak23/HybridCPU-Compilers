using System.Text.Json;
using HardwareBridge.Intel8080;

if (args.Length != 3 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[2]))
    throw new ArgumentException("Usage: <source-root> <selected-files.json> <new-output.json> (all absolute paths)");
var root=Path.GetFullPath(args[0]); var selectionPath=Path.GetFullPath(args[1]); var output=Path.GetFullPath(args[2]);
if(!Directory.Exists(root)||!File.Exists(selectionPath)) throw new FileNotFoundException("Source root or selected-file list is missing.");
var allowedRoot=Path.GetFullPath(@"C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080").TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
static bool IsUnder(string path,string allowed)=>path.StartsWith(allowed,StringComparison.OrdinalIgnoreCase);
if(!IsUnder(output,allowedRoot)||!IsUnder(selectionPath,allowedRoot)) throw new InvalidOperationException("8080 analysis outputs and selection must remain inside CircuitWeave-main/artifacts/8080.");
foreach(var candidate in new[]{Path.GetDirectoryName(selectionPath)!,Path.GetDirectoryName(output)!})
    for(var current=new DirectoryInfo(candidate);current is not null;current=current.Parent)
        if(Directory.Exists(current.FullName)&&(current.Attributes&FileAttributes.ReparsePoint)!=0) throw new IOException("Linked 8080 artifact path refused: "+current.FullName);
var selected=JsonSerializer.Deserialize<string[]>(File.ReadAllText(selectionPath))??throw new InvalidDataException("Selection must be a JSON string array.");
var irPath=Path.Combine(Path.GetDirectoryName(output)!,"opcode-ir-candidate.json");
var methodIrPath=Path.Combine(Path.GetDirectoryName(output)!,"ops-method-ir.json");
foreach(var candidate in new[]{output,irPath,methodIrPath}) if(File.Exists(candidate)||Directory.Exists(candidate)) throw new IOException("Output already exists; refusing overwrite: "+candidate);
var model=Analyzer.Analyze(root,selected);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using(var stream=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None))
    JsonSerializer.Serialize(stream,model,new JsonSerializerOptions{WriteIndented=true});
using(var irStream=new FileStream(irPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
    JsonSerializer.Serialize(irStream,new { schema="hardwarebridge.intel8080.opcode-ir-candidate/v1", disposition="editable source-mapped candidate; not lowered hardware", sourceInventory=output, entries=Analyzer.BuildOpcodeIr(model) },new JsonSerializerOptions{WriteIndented=true});
var methodIr=Analyzer.BuildMethodIr(model,selected);
using(var methodStream=new FileStream(methodIrPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
    JsonSerializer.Serialize(methodStream,new { schema="hardwarebridge.intel8080.ops-method-ir/v1", disposition="source-mapped structural C# syntax projection; not executable hardware semantics", methods=methodIr },new JsonSerializerOptions{WriteIndented=true});
Console.WriteLine($"Analyzed {model.Files.Count} files, {model.Types.Count} types, {model.Opcodes.Count} opcode entries, {methodIr.Count} Ops methods; IR candidates: {irPath}, {methodIrPath}. Sources were not built or run.");
