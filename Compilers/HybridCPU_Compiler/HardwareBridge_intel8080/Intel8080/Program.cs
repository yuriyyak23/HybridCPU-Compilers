using System.Text.Json;
using HardwareBridge.Intel8080;

if (args.Length != 3 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[2]))
    throw new ArgumentException("Usage: <source-root> <selected-files.json> <new-output.json> (all absolute paths)");
string root =Path.GetFullPath(args[0]); string selectionPath =Path.GetFullPath(args[1]); string output =Path.GetFullPath(args[2]);
if(!Directory.Exists(root)||!File.Exists(selectionPath)) throw new FileNotFoundException("Source root or selected-file list is missing.");
string allowedRoot =Path.GetFullPath(@"C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080").TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
static bool IsUnder(string path,string allowed)=>path.StartsWith(allowed,StringComparison.OrdinalIgnoreCase);
if(!IsUnder(output,allowedRoot)||!IsUnder(selectionPath,allowedRoot)) throw new InvalidOperationException("8080 analysis outputs and selection must remain inside CircuitWeave-main/artifacts/8080.");
foreach(string? candidate in new[]{Path.GetDirectoryName(selectionPath)!,Path.GetDirectoryName(output)!})
    for(var current=new DirectoryInfo(candidate);current is not null;current=current.Parent)
        if(Directory.Exists(current.FullName)&&(current.Attributes&FileAttributes.ReparsePoint)!=0) throw new IOException("Linked 8080 artifact path refused: "+current.FullName);
string[] selected =JsonSerializer.Deserialize<string[]>(File.ReadAllText(selectionPath))??throw new InvalidDataException("Selection must be a JSON string array.");
string irPath =Path.Combine(Path.GetDirectoryName(output)!,"opcode-ir-candidate.json");
string methodIrPath =Path.Combine(Path.GetDirectoryName(output)!,"ops-method-ir.json");
string addBehaviorPath =Path.Combine(Path.GetDirectoryName(output)!,"add-behavior-projection.json");
string adcBehaviorPath =Path.Combine(Path.GetDirectoryName(output)!,"adc-behavior-projection.json");
string sbbBehaviorPath =Path.Combine(Path.GetDirectoryName(output)!,"sbb-behavior-projection.json");
string subBehaviorPath =Path.Combine(Path.GetDirectoryName(output)!,"sub-behavior-projection.json");
string dadBehaviorPath =Path.Combine(Path.GetDirectoryName(output)!,"dad-behavior-projection.json");
string logicalBehaviorPath =Path.Combine(Path.GetDirectoryName(output)!,"logical-behavior-projection.json");
foreach(string? candidate in new[]{output,irPath,methodIrPath,addBehaviorPath,adcBehaviorPath,sbbBehaviorPath,subBehaviorPath,dadBehaviorPath,logicalBehaviorPath}) if(File.Exists(candidate)||Directory.Exists(candidate)) throw new IOException("Output already exists; refusing overwrite: "+candidate);
var model=Analyzer.Analyze(root,selected);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using(var stream=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None))
    JsonSerializer.Serialize(stream,model,new JsonSerializerOptions{WriteIndented=true});
using(var irStream=new FileStream(irPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
    JsonSerializer.Serialize(irStream,new { schema="hardwarebridge.intel8080.opcode-ir-candidate/v1", disposition="editable source-mapped candidate; not lowered hardware", sourceInventory=output, entries=Analyzer.BuildOpcodeIr(model) },new JsonSerializerOptions{WriteIndented=true});
var methodIr=Analyzer.BuildMethodIr(model,selected);
using(var methodStream=new FileStream(methodIrPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
    JsonSerializer.Serialize(methodStream,new { schema="hardwarebridge.intel8080.ops-method-ir/v1", disposition="source-mapped structural C# syntax projection; not executable hardware semantics", methods=methodIr },new JsonSerializerOptions{WriteIndented=true});
using(var addStream=new FileStream(addBehaviorPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
    JsonSerializer.Serialize(addStream,Analyzer.BuildAddBehaviorProjection(model),new JsonSerializerOptions{WriteIndented=true});
using(var adcStream=new FileStream(adcBehaviorPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
    JsonSerializer.Serialize(adcStream,Analyzer.BuildAdcBehaviorProjection(model),new JsonSerializerOptions{WriteIndented=true});
using(var sbbStream=new FileStream(sbbBehaviorPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
    JsonSerializer.Serialize(sbbStream,Analyzer.BuildSbbBehaviorProjection(model),new JsonSerializerOptions{WriteIndented=true});
using(var subStream=new FileStream(subBehaviorPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
    JsonSerializer.Serialize(subStream,Analyzer.BuildSubBehaviorProjection(model),new JsonSerializerOptions{WriteIndented=true});
using(var dadStream=new FileStream(dadBehaviorPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
    JsonSerializer.Serialize(dadStream,Analyzer.BuildDadBehaviorProjection(model),new JsonSerializerOptions{WriteIndented=true});
using(var logicalStream=new FileStream(logicalBehaviorPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
    JsonSerializer.Serialize(logicalStream,Analyzer.BuildLogicalBehaviorProjection(model),new JsonSerializerOptions{WriteIndented=true});
Console.WriteLine($"Analyzed {model.Files.Count} files, {model.Types.Count} types, {model.Opcodes.Count} opcode entries, {methodIr.Count} Ops methods; projections: {irPath}, {methodIrPath}, {addBehaviorPath}, {adcBehaviorPath}, {sbbBehaviorPath}, {subBehaviorPath}, {dadBehaviorPath}, {logicalBehaviorPath}. Sources were not built or run.");
