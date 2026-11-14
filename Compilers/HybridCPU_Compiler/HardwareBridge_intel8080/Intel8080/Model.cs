using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HardwareBridge.Intel8080;

public sealed record SourceFile(string Path, string Sha256, long Length);
public sealed record Member(string Name, string Kind, string Signature, int Line, int Column);
public sealed record TypeModel(string Name, string Namespace, string Kind, string File, int Line,
    IReadOnlyList<Member> Members, IReadOnlyList<string> TypeReferences, IReadOnlyList<string> Warnings);
public sealed record Opcode(byte Code, string Method, bool Mapped, string File, int Line);
public sealed record OpcodeIr(byte Code, string Mnemonic, string SourceMethod, string SourceFile, int SourceLine,
    string BindingStatus, string HardwareStatus, IReadOnlyList<string> ManualTasks);
public sealed record MethodIr(string Method, string File, int Line, string[] Operations, string[] UnclassifiedSyntax);
public sealed record SemanticReference(string File, int Line, string Syntax, string? Symbol, string? TargetFile, int? TargetLine);
public sealed record MemberDependency(string Kind, string SourceMember, string File, int Line, string Syntax,
    string? TargetSymbol, string? TargetFile, int? TargetLine);
public sealed record ImportModel(string Schema, string Disposition, string SourceRoot,
    IReadOnlyList<SourceFile> Files, IReadOnlyList<TypeModel> Types, IReadOnlyList<Opcode> Opcodes,
    IReadOnlyList<SemanticReference> References, IReadOnlyList<MemberDependency> MemberDependencies,
    IReadOnlyList<string> CompilationDiagnostics,
    IReadOnlyList<string> HardwareDecisions, IReadOnlyList<string> UnsupportedFeatures);

public static class Analyzer
{
    public static IReadOnlyList<MethodIr> BuildMethodIr(ImportModel model, IReadOnlyCollection<string> selectedPaths)
    {
        var result=new List<MethodIr>();
        foreach(string path in selectedPaths.Where(p=>Path.GetFileName(p).Equals("Ops.cs",StringComparison.OrdinalIgnoreCase)))
        {
            var tree=CSharpSyntaxTree.ParseText(File.ReadAllText(path),new CSharpParseOptions(LanguageVersion.Preview),path);
            foreach(var method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var ops=new List<string>();var other=new List<string>();
                foreach(var node in method.DescendantNodes().Where(n=>n is StatementSyntax or ExpressionSyntax))
                {
                    string? op=node switch
                    {
                        AssignmentExpressionSyntax a => "assign:"+a.Left,
                        InvocationExpressionSyntax i => "call:"+i.Expression,
                        IfStatementSyntax => "branch:if",
                        SwitchStatementSyntax => "branch:switch",
                        ReturnStatementSyntax => "return",
                        PrefixUnaryExpressionSyntax p => "unary:"+p.OperatorToken.ValueText,
                        PostfixUnaryExpressionSyntax p => "unary:"+p.OperatorToken.ValueText,
                        BinaryExpressionSyntax b => "binary:"+b.OperatorToken.ValueText,
                        ElementAccessExpressionSyntax e => "index:"+e.Expression,
                        _ => null
                    };
                    if(op is null)continue;
                    var row=$"{op}@{tree.GetLineSpan(node.Span).StartLinePosition.Line+1}";
                    if(node is StatementSyntax or AssignmentExpressionSyntax or InvocationExpressionSyntax or BinaryExpressionSyntax or ElementAccessExpressionSyntax)ops.Add(row);
                    else other.Add(row);
                }
                int line=tree.GetLineSpan(method.Identifier.Span).StartLinePosition.Line+1;
                result.Add(new(method.Identifier.ValueText,path,line,ops.Distinct().ToArray(),other.Distinct().ToArray()));
            }
        }
        return result;
    }

    public static IReadOnlyList<OpcodeIr> BuildOpcodeIr(ImportModel model)
    {
        var opsFile=model.Files.FirstOrDefault(f=>Path.GetFileName(f.Path).Equals("Ops.cs",StringComparison.OrdinalIgnoreCase))?.Path;
        var methods=new Dictionary<string,(string File,int Line)>(StringComparer.Ordinal);
        foreach(var type in model.Types.Where(t=>Path.GetFileName(t.File).Equals("Ops.cs",StringComparison.OrdinalIgnoreCase)))
            foreach(var member in type.Members.Where(m=>m.Kind=="method")) methods.TryAdd(member.Name,(type.File,member.Line));
        return model.Opcodes.Select(op=>
        {
            methods.TryGetValue(op.Method,out var origin);
            return new OpcodeIr(op.Code,$"OP_{op.Code:X2}",op.Method,origin.File??opsFile??op.File,origin.Line,
                op.Mapped&&origin.File is not null?"Roslyn symbol resolved":"unresolved; inspect source",
                "TODO: manual operation semantics and timing; no automatic C# to hardware lowering",
                new[]{"Inspect method data/control effects and helper closure", "Define operand, flag, PC and memory effects", "Add an instruction-level hardware verification vector"});
        }).ToArray();
    }

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", "bin", "obj", "packages", "node_modules" };

    public static string[] FindSourceFiles(string root)
    {
        var result = new List<string>(); var pending = new Stack<string>(); pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(directory, "*.cs")) result.Add(Path.GetFullPath(file));
            foreach (string child in Directory.EnumerateDirectories(directory))
                if (!ExcludedDirectories.Contains(Path.GetFileName(child))) pending.Push(child);
        }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static ImportModel Analyze(string sourceRoot, IReadOnlyCollection<string> selectedPaths)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        var files = new List<SourceFile>(); var types = new List<TypeModel>();
        var syntaxTrees = new List<SyntaxTree>(); var declarations = new List<(string File, BaseTypeDeclarationSyntax Node)>();
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        foreach (string input in selectedPaths)
        {
            string path=Path.GetFullPath(input);
            if (!path.StartsWith(Path.TrimEndingDirectorySeparator(sourceRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Selected source is outside the declared 8080 source root: " + path);
            byte[] bytes=File.ReadAllBytes(path);
            var tree=CSharpSyntaxTree.ParseText(System.Text.Encoding.UTF8.GetString(bytes),parseOptions,path);
            syntaxTrees.Add(tree); files.Add(new(path,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),bytes.LongLength));
            var diagnostics=tree.GetDiagnostics().Where(d=>d.Severity==DiagnosticSeverity.Error).Select(d=>d.GetMessage()).Take(8).ToArray();
            foreach(var node in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                declarations.Add((path,node));
                var members=node is TypeDeclarationSyntax td?td.Members.Select(m=>Describe(m,tree)).ToArray():Array.Empty<Member>();
                var warnings=new List<string>(diagnostics);
                if(node.DescendantNodes().Any(n=>n is InvocationExpressionSyntax))warnings.Add("Method calls need semantic/hardware mapping.");
                if(node.DescendantNodes().Any(n=>n is ArrayTypeSyntax or ArrayCreationExpressionSyntax))warnings.Add("Array-backed state requires an explicit hardware memory choice.");
                if(node.DescendantNodes().Any(n=>n is EventFieldDeclarationSyntax or EventDeclarationSyntax))warnings.Add("Events need signal-level interpretation.");
                if(node.DescendantNodes().OfType<IdentifierNameSyntax>().Any(n=>n.Identifier.ValueText is "Dictionary" or "ConcurrentQueue" or "ILogger"))warnings.Add("Dictionary, queue, or logging is not directly hardware behavior.");
                var refs=node.DescendantNodes().OfType<TypeSyntax>().Select(t=>t.ToString()).Distinct().Order(StringComparer.Ordinal).ToArray();
                types.Add(new(node.Identifier.ValueText,Namespace(node),node.Kind().ToString(),path,tree.GetLineSpan(node.Identifier.Span).StartLinePosition.Line+1,members,refs,warnings.Distinct().ToArray()));
            }
        }
        var references=AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")?.ToString()?.Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries)
            .Select(path=>(MetadataReference)MetadataReference.CreateFromFile(path)).ToArray() ?? Array.Empty<MetadataReference>();
        var compilation=CSharpCompilation.Create("Intel8080.SourceAnalysis",syntaxTrees,references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,allowUnsafe:true));
        var compilationDiagnostics=compilation.GetDiagnostics().Where(d=>d.Severity==DiagnosticSeverity.Error)
            .Take(100).Select(d=>$"{d.Id}: {d.GetMessage()} ({d.Location.GetLineSpan().Path}:{d.Location.GetLineSpan().StartLinePosition.Line+1})").ToArray();
        var semanticReferences=new List<SemanticReference>();
        var memberDependencies=new List<MemberDependency>();
        foreach(var tree in syntaxTrees)
        {
            var semantic=compilation.GetSemanticModel(tree);
            foreach(var node in tree.GetRoot().DescendantNodes())
            {
                string? kind=null; ISymbol? symbol=null; SyntaxNode? referenceNode=null;
                switch(node)
                {
                    case InvocationExpressionSyntax invocation:
                        kind="method-call"; symbol=semantic.GetSymbolInfo(invocation).Symbol; referenceNode=invocation; break;
                    case ObjectCreationExpressionSyntax creation:
                        kind="object-creation"; symbol=semantic.GetSymbolInfo(creation).Symbol; referenceNode=creation; break;
                    case ImplicitObjectCreationExpressionSyntax creation:
                        kind="object-creation"; symbol=semantic.GetSymbolInfo(creation).Symbol; referenceNode=creation; break;
                    case IdentifierNameSyntax identifier:
                        symbol=semantic.GetSymbolInfo(identifier).Symbol;
                        if(symbol is IFieldSymbol)kind="field-reference";
                        else if(symbol is IPropertySymbol)kind="property-reference";
                        else if(symbol is IEventSymbol)kind="event-reference";
                        if(kind is not null)referenceNode=identifier;
                        break;
                }
                if(kind is null||referenceNode is null)continue;
                var location=symbol?.Locations.FirstOrDefault(l=>l.IsInSource);
                var source=tree.GetLineSpan(referenceNode.Span).StartLinePosition;
                var enclosing=referenceNode.Ancestors().FirstOrDefault(a=>a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax);
                string owner=enclosing switch
                {
                    MethodDeclarationSyntax m=>m.Identifier.ValueText,
                    ConstructorDeclarationSyntax c=>".ctor",
                    DestructorDeclarationSyntax d=>"~"+d.Identifier.ValueText,
                    OperatorDeclarationSyntax o=>"operator "+o.OperatorToken.ValueText,
                    ConversionOperatorDeclarationSyntax c=>"operator "+c.Type,
                    AccessorDeclarationSyntax a=>a.Keyword.ValueText,
                    PropertyDeclarationSyntax p=>p.Identifier.ValueText,
                    FieldDeclarationSyntax f=>string.Join(",",f.Declaration.Variables.Select(v=>v.Identifier.ValueText)),
                    _=>"<type-initializer-or-member>"
                };
                var ownerType=referenceNode.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
                if(ownerType is not null)owner=ownerType.Identifier.ValueText+"."+owner;
                var targetFile=location?.SourceTree?.FilePath;
                int? targetLine=location is null?null:location.SourceTree!.GetLineSpan(location.SourceSpan).StartLinePosition.Line+1;
                memberDependencies.Add(new(kind,owner,tree.FilePath,source.Line+1,referenceNode.ToString(),symbol?.ToDisplayString(),targetFile,targetLine));
                if(kind=="method-call")semanticReferences.Add(new(tree.FilePath,source.Line+1,referenceNode.ToString(),symbol?.ToDisplayString(),targetFile,targetLine));
            }
        }
        var opcodes = new List<Opcode>();
        foreach (var (file,node) in declarations.Where(x=>x.Node is ClassDeclarationSyntax && x.Node.Identifier.ValueText=="Cpu"))
        {
            var semantic=compilation.GetSemanticModel(node.SyntaxTree);
            var initializer=node.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .FirstOrDefault(a=>a.Left is IdentifierNameSyntax id && id.Identifier.ValueText=="_ops")?.Right as ObjectCreationExpressionSyntax;
            var dictionary=initializer?.Initializer;
            if(dictionary is null) continue;
            foreach(var entry in dictionary.Expressions.OfType<InitializerExpressionSyntax>().Where(x=>x.Expressions.Count==2))
            {
                var constant=semantic.GetConstantValue(entry.Expressions[0]);
                if(!constant.HasValue||constant.Value is not IConvertible convertible) continue;
                byte code; try { code=convertible.ToByte(System.Globalization.CultureInfo.InvariantCulture); } catch(OverflowException) { continue; }
                var target=semantic.GetSymbolInfo(entry.Expressions[1]).Symbol as IMethodSymbol;
                string method=target?.Name ?? entry.Expressions[1].ToString().Split('.').Last(); var line=entry.SyntaxTree.GetLineSpan(entry.Span).StartLinePosition.Line+1;
                opcodes.Add(new(code,method,target is not null,file,line));
            }
        }
        var decision = new[]{"8080 address width 16; data width 8 (architecture assumption)","clock/reset, memory, I/O and IRQ contracts are design decisions; emulator C# does not establish cycle timing","Roslyn semantic compilation is in-memory for symbol binding; no project build, emit or source execution"};
        var unsupported = new[]{"Dictionary delegates/control flow, queue interrupts, events, logger and byte-array memory require manual hardware mapping","No generic C# to MLIR/RTL lowering is performed by this analyzer","Opcode syntax scan is not functional instruction verification"};
        return new("hardwarebridge.intel8080.import/v2","Roslyn in-memory semantic compilation using SDK trusted platform assemblies; diagnostics retained; no emit, project build, source execution or hardware elaboration",sourceRoot,files,types,opcodes.OrderBy(x=>x.Code).ToArray(),semanticReferences,memberDependencies,compilationDiagnostics,decision,unsupported);
    }

    private static Member Describe(MemberDeclarationSyntax m, SyntaxTree tree)
    {
        var (name,kind,sig)=m switch
        {
            MethodDeclarationSyntax x=>(x.Identifier.ValueText,"method",$"{x.ReturnType} {x.Identifier}{x.TypeParameterList}{x.ParameterList}"),
            ConstructorDeclarationSyntax x=>(x.Identifier.ValueText,"constructor",$"{x.Identifier}{x.ParameterList}"),
            FieldDeclarationSyntax x=>(string.Join(",",x.Declaration.Variables.Select(v=>v.Identifier.ValueText)),"field",x.Declaration.ToString()),
            PropertyDeclarationSyntax x=>(x.Identifier.ValueText,"property",$"{x.Type} {x.Identifier}"),
            EventFieldDeclarationSyntax x=>(string.Join(",",x.Declaration.Variables.Select(v=>v.Identifier.ValueText)),"event",x.Declaration.ToString()),
            _=>(m.Kind().ToString(),"member",m.Kind().ToString())
        };
        var pos=tree.GetLineSpan(m.Span).StartLinePosition; return new(name,kind,sig,pos.Line+1,pos.Character+1);
    }
    private static string Namespace(SyntaxNode n)=>n.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString()??"";
}
