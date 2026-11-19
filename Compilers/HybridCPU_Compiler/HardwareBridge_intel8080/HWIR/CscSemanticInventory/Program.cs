using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

// Diagnostic semantic replay from an isolated build observation. No targets are
// executed and no source-tree files are changed by this tool.
if (args.Length != 3 || args.Any(p => !Path.IsPathFullyQualified(p)))
    throw new ArgumentException("Expected absolute evaluated graph, isolated Csc audit, and new TEMP output paths.");
string output = Path.GetFullPath(args[2]);
string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
if (!output.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || File.Exists(output))
    throw new InvalidDataException("Output must be new and inside TEMP.");
for (var parent = new DirectoryInfo(Path.GetDirectoryName(output)!); parent is not null; parent = parent.Parent)
    if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked output ancestor unsupported.");
static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
static byte[] Bounded(string path, int max)
{
    var file = new FileInfo(path);
    if (!file.Exists || file.Length > max || (file.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException("Missing, linked or oversized input: " + path);
    return File.ReadAllBytes(path);
}
byte[] graphBytes = Bounded(args[0], 4 * 1024 * 1024); byte[] auditBytes = Bounded(args[1], 8 * 1024 * 1024);
using var graphDoc = JsonDocument.Parse(graphBytes); using var auditDoc = JsonDocument.Parse(auditBytes);
var graph = graphDoc.RootElement; var audit = auditDoc.RootElement;
if (graph.GetProperty("schema").GetString() != "hardwarebridge.evaluated-project-graph/v2" ||
    audit.GetProperty("schema").GetString() != "hardwarebridge.isolated-csc-audit/v1" || audit.GetProperty("graphSha256").GetString() != Hash(graphBytes))
    throw new InvalidDataException("Graph and Csc observation identities differ.");
var graphNodes = graph.GetProperty("nodes").EnumerateArray().ToDictionary(n => Path.GetFullPath(n.GetProperty("project").GetString()!), StringComparer.OrdinalIgnoreCase);
var projects = audit.GetProperty("projects").EnumerateArray().ToArray();
if (projects.Length != graphNodes.Count || projects.Length > 100) throw new InvalidDataException("Compiler observations do not cover the evaluated graph.");
var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var referenceCache = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
static byte[] ReadPinned(JsonElement entry, int limit)
{
    string path = entry.GetProperty("path").GetString()!; if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Relative compiler input.");
    byte[] bytes = Bounded(path, limit); if (Hash(bytes) != entry.GetProperty("sha256").GetString()) throw new InvalidDataException("Compiler input changed: " + path);
    return bytes;
}
static object Location(Location location)
{
    var line = location.GetLineSpan(); return new { file = line.Path, line = line.StartLinePosition.Line + 1, column = line.StartLinePosition.Character + 1 };
}
static string SymbolId(ISymbol s) => s.GetDocumentationCommentId() ?? (s.Kind + ":" + s.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
var results = new List<object>();
foreach (var project in projects)
{
    string projectPath = Path.GetFullPath(project.GetProperty("project").GetString()!);
    if (!seenProjects.Add(projectPath) || !graphNodes.TryGetValue(projectPath, out var node)) throw new InvalidDataException("Unknown compiler project.");
    if (project.GetProperty("missingEvaluatedSources").GetArrayLength() != 0 || project.GetProperty("changedCopiedSources").GetArrayLength() != 0 || project.GetProperty("missingEvaluatedDefines").GetArrayLength() != 0 || !project.GetProperty("generatorOutputRequested").GetBoolean())
        throw new InvalidDataException("Compiler observation has incomplete or changed inputs: " + projectPath);
    if (!LanguageVersionFacts.TryParse(project.GetProperty("languageVersion").GetString(), out var language)) throw new InvalidDataException("Unsupported C# language version.");
    string[] defines = project.GetProperty("compilerDefines").EnumerateArray().Select(x => x.GetString()!).ToArray();
    var parse = new CSharpParseOptions(language, preprocessorSymbols: defines);
    var trees = new List<SyntaxTree>(); var identities = new List<object>(); var treePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    void AddSource(JsonElement entry, string kind)
    {
        byte[] bytes = ReadPinned(entry, 2 * 1024 * 1024); string path = entry.GetProperty("path").GetString()!;
        string logical = entry.TryGetProperty("originalPath", out var original) && original.ValueKind == JsonValueKind.String ? original.GetString()! : path;
        if (!treePaths.Add(logical)) throw new InvalidDataException("Duplicate source tree identity: " + logical);
        using var stream = new MemoryStream(bytes, false);
        trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(stream, encoding: null, checksumAlgorithm: SourceHashAlgorithm.Sha256), parse, logical));
        identities.Add(new { path = logical, capturedPath = path, sha256 = Hash(bytes), length = bytes.Length, origin = kind });
    }
    foreach (var entry in project.GetProperty("compilerSourceFiles").EnumerateArray())
        AddSource(entry, entry.GetProperty("originalPath").ValueKind == JsonValueKind.String ? "evaluated-compile" : "isolated-msbuild-generated");
    foreach (var entry in project.GetProperty("emittedGeneratorOutput").EnumerateArray()) AddSource(entry, "isolated-analyzer-generated");
    var references = new List<MetadataReference>();
    foreach (var entry in project.GetProperty("references").EnumerateArray())
    {
        string path = entry.GetProperty("path").GetString()!; string hash = entry.GetProperty("sha256").GetString()!; string cacheKey = path + "|" + hash;
        if (!referenceCache.TryGetValue(cacheKey, out var reference))
        { byte[] bytes = ReadPinned(entry, 128 * 1024 * 1024); reference = MetadataReference.CreateFromImage(ImmutableArray.Create(bytes), filePath: path); referenceCache.Add(cacheKey, reference); }
        references.Add(reference);
    }
    var nullable = project.GetProperty("nullable").GetString() switch { "enable" => NullableContextOptions.Enable, "disable" => NullableContextOptions.Disable, "annotations" => NullableContextOptions.Annotations, "warnings" => NullableContextOptions.Warnings, _ => throw new InvalidDataException("Unsupported nullable mode.") };
    string? assemblyName = node.GetProperty("properties").GetProperty("assemblyName").GetString();
    var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: project.GetProperty("allowUnsafe").GetBoolean(), checkOverflow: project.GetProperty("checkOverflow").GetBoolean(), nullableContextOptions: nullable);
    var compilation = CSharpCompilation.Create(assemblyName, trees, references, options);
    var diagnostics = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).GroupBy(d => d.Id)
        .Select(g => new { id = g.Key, count = g.Count(), example = g.First().ToString() }).ToArray();
    var symbols = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
    foreach (var tree in trees)
    {
        var model = compilation.GetSemanticModel(tree);
        foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            if (model.GetDeclaredSymbol(declaration) is INamedTypeSymbol symbol && symbol.TypeKind is TypeKind.Class or TypeKind.Struct) symbols.Add(symbol);
    }
    object[] Dependencies(ISymbol member)
    {
        var edges = new Dictionary<string, object>(StringComparer.Ordinal);
        if (member is INamedTypeSymbol) return [];
        foreach (var reference in member.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax(); var model = compilation.GetSemanticModel(syntax.SyntaxTree);
            foreach (var current in syntax.DescendantNodesAndSelf(n => n == syntax || n is not TypeDeclarationSyntax))
            {
                string? kind = current switch { InvocationExpressionSyntax => "invocation", ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax => "construction", IdentifierNameSyntax => "member-reference", _ => null };
                if (kind is null) continue; var target = model.GetSymbolInfo(current).Symbol;
                if (target is IMethodSymbol method && kind is "invocation" or "construction") target = (method.ReducedFrom ?? method).OriginalDefinition;
                else if (kind == "member-reference" && target is IFieldSymbol or IPropertySymbol or IEventSymbol) target = target.OriginalDefinition;
                else continue;
                if (target is null || SymbolEqualityComparer.Default.Equals(target, member)) continue;
                string assembly = target.ContainingAssembly?.Identity.ToString() ?? ""; string id = SymbolId(target);
                edges.TryAdd(kind + "|" + assembly + "|" + id, new { kind, target = id, targetAssembly = assembly, firstLocation = Location(current.GetLocation()) });
            }
        }
        return edges.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value).ToArray();
    }
    var types = symbols.OrderBy(s => s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal).Select(s => new {
        symbol = s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), kind = s.TypeKind.ToString(), assembly = s.ContainingAssembly.Identity.ToString(),
        containingType = s.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), accessibility = s.DeclaredAccessibility.ToString(),
        baseType = s.BaseType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), interfaces = s.Interfaces.Select(x => x.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).ToArray(),
        attributes = s.GetAttributes().Select(a => new { type = a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), text = a.ApplicationSyntaxReference?.GetSyntax().ToString() }).ToArray(),
        declarations = s.DeclaringSyntaxReferences.Select(r => (TypeDeclarationSyntax)r.GetSyntax()).Select(d => new { location = Location(d.Identifier.GetLocation()), modifiers = d.Modifiers.ToString(), attributes = d.AttributeLists.Select(a => a.ToString()).ToArray(),
            memberIds = s.GetMembers().Where(m => !m.IsImplicitlyDeclared && m.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == d.SyntaxTree && d.Span.Contains(r.Span) && r.GetSyntax().Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Span == d.Span)).Select(SymbolId).Order(StringComparer.Ordinal).ToArray() }).ToArray(),
        members = s.GetMembers().Where(m => !m.IsImplicitlyDeclared && m.Locations.Any(l => l.IsInSource)).Select(m => new { id = SymbolId(m), kind = m.Kind.ToString(), display = m.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), accessibility = m.DeclaredAccessibility.ToString(), isStatic = m.IsStatic,
            attributes = m.GetAttributes().Select(a => a.ApplicationSyntaxReference?.GetSyntax().ToString() ?? a.AttributeClass?.ToDisplayString()).ToArray(), locations = m.Locations.Where(l => l.IsInSource).Select(Location).ToArray(), dependencies = Dependencies(m) }).ToArray()
    }).ToArray();
    var resolvedReferences = references.Select(r => new { path = r.Display, identity = compilation.GetAssemblyOrModuleSymbol(r)?.ContainingAssembly?.Identity.ToString() }).ToArray();
    results.Add(new { project = projectPath, assembly = compilation.Assembly.Identity.ToString(), compilerDefines = defines, sourceFiles = identities, resolvedReferences, diagnostics, types });
    Console.WriteLine($"{Path.GetFileName(projectPath)}: {types.Length} classes/structs, {trees.Count} source trees, {diagnostics.Sum(d => d.count)} errors.");
}
using var outputStream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
JsonSerializer.Serialize(outputStream, new { schema = "hardwarebridge.csc-semantic-inventory/v1", disposition = "isolated-unqualified", graphSha256 = Hash(graphBytes), cscAuditSha256 = Hash(auditBytes), projects = results,
    scope = "Diagnostic Roslyn replay from caller-supplied isolated compiler inputs and emitted generator files; metadata project references use isolated build assemblies. Analyzer configs and generators are not rerun; missing implicit/dynamic dependencies, concurrency and original-tree compiler equivalence are not excluded. No hardware elaboration claim." });
