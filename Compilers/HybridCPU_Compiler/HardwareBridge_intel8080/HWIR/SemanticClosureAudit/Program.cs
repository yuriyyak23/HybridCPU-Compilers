using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

if (args.Length != 4 || args.Any(p => !Path.IsPathFullyQualified(p)))
    throw new ArgumentException("Expected absolute source capture, isolated-reference receipt, generated directory and new output.");
var captureBytes = File.ReadAllBytes(args[0]);
using var capture = JsonDocument.Parse(captureBytes.AsMemory().Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? captureBytes.AsMemory(3) : captureBytes.AsMemory());
var receiptBytes = File.ReadAllBytes(args[1]);
using var receipt = JsonDocument.Parse(receiptBytes);
if (capture.RootElement.GetProperty("schema").GetString() != "circuitweave.msbuild-source-byte-inventory.v1" ||
    receipt.RootElement.GetProperty("schema").GetString() != "circuitweave.isolated-resolved-references/v1")
    throw new InvalidDataException("Unsupported input schema.");
var output = Path.GetFullPath(args[3]);
var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
foreach (var name in new[] { "HybridCPU ISE", "HybridCPU Compilers" })
{
    var root = Path.GetFullPath(Path.Combine(desktop, name));
    if (output.Equals(root, StringComparison.OrdinalIgnoreCase) || output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Output inside protected tree forbidden.");
}
for (var parent = new DirectoryInfo(Path.GetDirectoryName(output)!); parent is not null; parent = parent.Parent)
    if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked output ancestor forbidden.");
if (File.Exists(output)) throw new InvalidOperationException("Output already exists.");
var props = receipt.RootElement.GetProperty("properties");
var capturedProps = capture.RootElement.GetProperty("properties");
foreach (var key in new[] { "Configuration", "TargetFramework", "LangVersion", "Nullable", "ImplicitUsings", "EnableInternalTestHooks" })
    if (props.GetProperty(key).GetString() != capturedProps.GetProperty(key).GetString()) throw new InvalidDataException("Compilation property mismatch: " + key);
var targetDefines = props.GetProperty("DefineConstants").GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries);
var evaluatedDefines = capturedProps.GetProperty("DefineConstants").GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries);
if (evaluatedDefines.Except(targetDefines, StringComparer.Ordinal).Any()) throw new InvalidDataException("Evaluated defines absent after targets.");
if (!LanguageVersionFacts.TryParse(props.GetProperty("LangVersion").GetString(), out var language)) throw new InvalidDataException("Unknown language version.");
var parse = new CSharpParseOptions(language, preprocessorSymbols: targetDefines);
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static byte[] ReadPinned(JsonElement entry)
{
    var bytes = File.ReadAllBytes(entry.GetProperty("path").GetString()!);
    if (Hash(bytes) != entry.GetProperty("sha256").GetString() || bytes.LongLength != entry.GetProperty("length").GetInt64())
        throw new InvalidDataException("Stale pinned input: " + entry.GetProperty("path").GetString());
    return bytes;
}
var trees = new List<SyntaxTree>();
foreach (var file in capture.RootElement.GetProperty("files").EnumerateArray())
{
    var bytes = ReadPinned(file);
    using var stream = new MemoryStream(bytes, false);
    trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(stream, encoding: null, checksumAlgorithm: SourceHashAlgorithm.Sha256), parse, file.GetProperty("path").GetString()!));
}
var generated = new List<object>();
foreach (var file in receipt.RootElement.GetProperty("generatedCompileFiles").EnumerateArray())
{
    var bytes = ReadPinned(file); var path = file.GetProperty("path").GetString()!;
    using var stream = new MemoryStream(bytes, false);
    trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(stream, encoding: null), parse, path));
    generated.Add(new { path, length = bytes.Length, sha256 = Hash(bytes), origin = "isolated-msbuild-compile" });
}
var generatedRoot = Path.GetFullPath(args[2]);
var temp = Path.GetFullPath(Path.GetTempPath());
if (!generatedRoot.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Generated directory must be inside TEMP.");
foreach (var path in Directory.EnumerateFiles(generatedRoot, "*.cs", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
{
    var bytes = File.ReadAllBytes(path);
    using var stream = new MemoryStream(bytes, false);
    trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(stream, encoding: null), parse, path));
    generated.Add(new { path, length = bytes.Length, sha256 = Hash(bytes), origin = "isolated-emitted-generator-output" });
}
var references = new List<MetadataReference>();
foreach (var entry in receipt.RootElement.GetProperty("references").EnumerateArray())
    references.Add(MetadataReference.CreateFromImage(ReadPinned(entry)));
var compilation = CSharpCompilation.Create(capturedProps.GetProperty("AssemblyName").GetString(), trees, references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
var diagnostics = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
    .GroupBy(d => d.Id).Select(group => new { id = group.Key, count = group.Count(), example = group.First().ToString() }).ToArray();
var symbols = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
foreach (var tree in trees)
{
    var model = compilation.GetSemanticModel(tree);
    foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
        if (model.GetDeclaredSymbol(declaration) is INamedTypeSymbol symbol && symbol.TypeKind is TypeKind.Class or TypeKind.Struct)
            symbols.Add(symbol);
}
static object Location(Location l) { var s = l.GetLineSpan(); return new { file = s.Path, line = s.StartLinePosition.Line + 1, column = s.StartLinePosition.Character + 1 }; }
static string SymbolId(ISymbol s) => s.GetDocumentationCommentId() ?? (s.Kind + ":" + s.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
object[] MemberDependencies(ISymbol member)
{
    var edges = new Dictionary<string, object>(StringComparer.Ordinal);
    if (member is INamedTypeSymbol) return [];
    foreach (var reference in member.DeclaringSyntaxReferences)
    {
        var syntax = reference.GetSyntax();
        var model = compilation.GetSemanticModel(syntax.SyntaxTree);
        foreach (var node in syntax.DescendantNodesAndSelf(n => n == syntax || n is not TypeDeclarationSyntax))
        {
            string? kind = node switch { InvocationExpressionSyntax => "invocation", ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax => "construction", IdentifierNameSyntax => "member-reference", _ => null };
            if (kind is null) continue;
            var target = model.GetSymbolInfo(node).Symbol;
            if (target is IMethodSymbol method && kind is "invocation" or "construction") target = (method.ReducedFrom ?? method).OriginalDefinition;
            else if (kind == "member-reference" && target is IFieldSymbol or IPropertySymbol or IEventSymbol) target = target.OriginalDefinition;
            else continue;
            if (target is null || SymbolEqualityComparer.Default.Equals(target, member)) continue;
            string id = SymbolId(target), assembly = target.ContainingAssembly?.Identity.ToString() ?? "";
            edges.TryAdd(kind + "|" + assembly + "|" + id, new { kind, target = id, targetAssembly = assembly, firstLocation = Location(node.GetLocation()) });
        }
    }
    return edges.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Value).ToArray();
}
var types = symbols.OrderBy(s => s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal).Select(s => new {
    symbol = s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), kind = s.TypeKind.ToString(),
    assembly = s.ContainingAssembly.Identity.ToString(), containingType = s.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
    accessibility = s.DeclaredAccessibility.ToString(),
    attributes = s.GetAttributes().Select(a => new { type = a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        text = a.ApplicationSyntaxReference?.GetSyntax().ToString(), location = a.ApplicationSyntaxReference is null ? null : Location(a.ApplicationSyntaxReference.GetSyntax().GetLocation()) }).ToArray(),
    declarations = s.DeclaringSyntaxReferences.Select(r => (TypeDeclarationSyntax)r.GetSyntax()).Select(d => new {
        location = Location(d.Identifier.GetLocation()), modifiers = d.Modifiers.ToString(), attributes = d.AttributeLists.Select(a => a.ToString()).ToArray(),
        memberIds = s.GetMembers().Where(m => !m.IsImplicitlyDeclared && m.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == d.SyntaxTree && d.Span.Contains(r.Span) &&
            r.GetSyntax().Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Span == d.Span)).Select(SymbolId).Order(StringComparer.Ordinal).ToArray() }).ToArray(),
    members = s.GetMembers().Where(m => !m.IsImplicitlyDeclared && m.Locations.Any(l => l.IsInSource)).Select(m => new {
        id = SymbolId(m), kind = m.Kind.ToString(), display = m.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        accessibility = m.DeclaredAccessibility.ToString(), isStatic = m.IsStatic,
        attributes = m.GetAttributes().Select(a => a.ApplicationSyntaxReference?.GetSyntax().ToString() ?? a.AttributeClass?.ToDisplayString()).ToArray(),
        locations = m.Locations.Where(l => l.IsInSource).Select(Location).ToArray(), dependencies = MemberDependencies(m)
    }).ToArray()
}).ToArray();
using var outputStream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
JsonSerializer.Serialize(outputStream, new { schema = "hardwarebridge.isolated-semantic-audit/v1", disposition = "isolated-unqualified",
    sourceCaptureSha256 = Hash(captureBytes), referenceReceiptSha256 = Hash(receiptBytes), evaluatedDefines, targetDefines, generated, diagnostics, types,
    scope = "Root source bytes plus isolated build references and emitted generator outputs; project references are isolated binaries. Observed bound member edges omit dynamic, indirect and implicit dependencies. No original-tree build equivalence, full project source inventory or hardware elaboration claim." });
Console.WriteLine($"Audited {types.Length} source classes/structs and {generated.Count} generated source files; {diagnostics.Sum(d => d.count)} errors.");
