#if HARDWAREBRIDGE_INVENTORY
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

// This is an inventory projection, not a hardware elaborator. It reads only the
// caller's byte-pinned MSBuild evaluation; it never builds or writes the model.
if (args.Length is not (2 or 4 or 6) || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]) ||
    (args.Length >= 4 && (args[2] != "--project-graph" || !Path.IsPathFullyQualified(args[3]))) ||
    (args.Length == 6 && (args[4] != "--sdk-json-generator" || !Path.IsPathFullyQualified(args[5]))))
{
    Console.Error.WriteLine("Usage: HardwareBridge.SemanticInventory <absolute-byte-inventory.json> <absolute-output.json> [--project-graph <absolute-graph.json> [--sdk-json-generator <absolute-generator-manifest.json>]]");
    return 2;
}
try
{
    var inputBytes = File.ReadAllBytes(args[0]);
    ReadOnlyMemory<byte> jsonInput = inputBytes.AsMemory();
    if (inputBytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) jsonInput = jsonInput[3..];
    using var doc = JsonDocument.Parse(jsonInput);
    var root = doc.RootElement;
    if (root.GetProperty("schema").GetString() != "circuitweave.msbuild-source-byte-inventory.v1")
        throw new InvalidDataException("Unsupported input schema.");
    var projectPath = Path.GetFullPath(root.GetProperty("project").GetString() ?? throw new InvalidDataException("Missing project path."));
    var sourceRoot = Directory.GetParent(Path.GetDirectoryName(projectPath)!)?.FullName
        ?? throw new InvalidDataException("Cannot identify source root.");
    var destination = Path.GetFullPath(args[1]);
    if (destination.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase) ||
        destination.StartsWith(Path.TrimEndingDirectorySeparator(sourceRoot) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Output inside model repository is forbidden.");
    for (var directory = new DirectoryInfo(Path.GetDirectoryName(destination)!); directory is not null; directory = directory.Parent)
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked output directory or ancestor is unsupported.");
    var properties = root.GetProperty("properties");
    var defines = (properties.GetProperty("DefineConstants").GetString() ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var language = properties.TryGetProperty("LangVersion", out var lang) ? lang.GetString() : null;
    if (string.IsNullOrWhiteSpace(language)) throw new InvalidDataException("MSBuild language version is absent.");
    var languageExact = LanguageVersionFacts.TryParse(language, out var version);
    if (!languageExact) version = LanguageVersion.Preview;
    var parse = new CSharpParseOptions(version, preprocessorSymbols: defines);
    var trees = new List<SyntaxTree>();
    var files = new List<object>();
    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in root.GetProperty("files").EnumerateArray())
    {
        var path = item.GetProperty("path").GetString() ?? throw new InvalidDataException("Missing path.");
        if (!Path.IsPathFullyQualified(path) || !paths.Add(Path.GetFullPath(path)) ||
            item.GetProperty("status").GetString() != "captured")
            throw new InvalidDataException("Missing, duplicate or invalid input path: " + path);
        var bytes = File.ReadAllBytes(path);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (hash != item.GetProperty("sha256").GetString() || bytes.LongLength != item.GetProperty("length").GetInt64())
            throw new InvalidDataException("Stale source bytes: " + path);
        using var stream = new MemoryStream(bytes, writable: false);
        var source = SourceText.From(stream, encoding: null, checksumAlgorithm: SourceHashAlgorithm.Sha256);
        trees.Add(CSharpSyntaxTree.ParseText(source, parse, path));
        files.Add(new { path, sha256 = hash, length = bytes.Length });
    }
    var tpa = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
    var referenceIdentities = tpa.Select(p => new { path = p,
        sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant() }).ToArray();
    var refs = tpa.Select(p => MetadataReference.CreateFromFile(p)).ToArray();
    var usingLines = new List<string>();
    foreach (var item in root.GetProperty("globalUsings").EnumerateArray())
    {
        var name = item.GetProperty("identity").GetString() ?? "";
        var alias = item.GetProperty("alias").ValueKind == JsonValueKind.String ? item.GetProperty("alias").GetString() : null;
        var isStatic = item.GetProperty("static").ValueKind == JsonValueKind.String &&
            string.Equals(item.GetProperty("static").GetString(), "true", StringComparison.OrdinalIgnoreCase);
        if (SyntaxFactory.ParseName(name).ContainsDiagnostics ||
            alias is { Length: > 0 } && !SyntaxFacts.IsValidIdentifier(alias))
            throw new InvalidDataException("Invalid MSBuild global using.");
        usingLines.Add("global using " + (isStatic ? "static " : "") +
            (string.IsNullOrEmpty(alias) ? "" : alias + " = ") + name + ";");
    }
    var generatedUsings = CSharpSyntaxTree.ParseText(string.Join("\n", usingLines), parse,
        "<MSBuild-evaluated-global-usings>");
    var nullable = properties.TryGetProperty("Nullable", out var nullableProperty) && nullableProperty.GetString() == "enable"
        ? NullableContextOptions.Enable : NullableContextOptions.Disable;
    // Source project references are separately evaluated snapshots, not presumed
    // current binaries. Their own project graph is built in dependency order.
    var referenceCompilations = new Dictionary<string, CSharpCompilation>(StringComparer.OrdinalIgnoreCase);
    var projectTrees = new Dictionary<SyntaxTree, CSharpCompilation>();
    var projectDiagnostics = new List<object>();
    string? graphDigest = null;
    if (args.Length >= 4)
    {
        var graphBytes = File.ReadAllBytes(args[3]);
        graphDigest = Convert.ToHexString(SHA256.HashData(graphBytes)).ToLowerInvariant();
        using var graphDoc = JsonDocument.Parse(graphBytes);
        var graph = graphDoc.RootElement;
        if (graph.GetProperty("schema").GetString() != "hardwarebridge.msbuild-project-graph/v1" ||
            graph.GetProperty("rootInventorySha256").GetString() != Convert.ToHexString(SHA256.HashData(inputBytes)).ToLowerInvariant())
            throw new InvalidDataException("Project graph is invalid or belongs to another root inventory.");
        var entries = graph.GetProperty("projects").EnumerateArray().ToDictionary(
            p => Path.GetFullPath(p.GetProperty("project").GetString()!), p => p.Clone(), StringComparer.OrdinalIgnoreCase);
        var inProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CSharpCompilation BuildProject(string path)
        {
            path = Path.GetFullPath(path);
            if (referenceCompilations.TryGetValue(path, out var built)) return built;
            if (!entries.TryGetValue(path, out var entry) || !inProgress.Add(path))
                throw new InvalidDataException("Missing or cyclic project reference: " + path);
            var projectHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (projectHash != entry.GetProperty("projectSha256").GetString())
                throw new InvalidDataException("Stale project file: " + path);
            var props = entry.GetProperty("properties");
            var projectLanguage = props.GetProperty("LangVersion").GetString();
            if (!LanguageVersionFacts.TryParse(projectLanguage, out var projectVersion))
                throw new InvalidDataException("Unsupported referenced project language: " + projectLanguage);
            var projectDefines = (props.GetProperty("DefineConstants").GetString() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var projectParse = new CSharpParseOptions(projectVersion, preprocessorSymbols: projectDefines);
            var projectSources = new List<SyntaxTree>();
            foreach (var file in entry.GetProperty("files").EnumerateArray())
            {
                var filePath = file.GetProperty("path").GetString()!;
                var bytes = File.ReadAllBytes(filePath);
                if (bytes.LongLength != file.GetProperty("length").GetInt64() ||
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != file.GetProperty("sha256").GetString())
                    throw new InvalidDataException("Stale referenced source: " + filePath);
                using var stream = new MemoryStream(bytes, writable: false);
                projectSources.Add(CSharpSyntaxTree.ParseText(SourceText.From(stream, encoding: null, checksumAlgorithm: SourceHashAlgorithm.Sha256), projectParse, filePath));
            }
            var projectUsings = new List<string>();
            foreach (var item in entry.GetProperty("globalUsings").EnumerateArray())
            {
                var name = item.GetProperty("identity").GetString() ?? "";
                var alias = item.GetProperty("alias").ValueKind == JsonValueKind.String ? item.GetProperty("alias").GetString() : null;
                var isStatic = item.GetProperty("static").ValueKind == JsonValueKind.String && string.Equals(item.GetProperty("static").GetString(), "true", StringComparison.OrdinalIgnoreCase);
                if (SyntaxFactory.ParseName(name).ContainsDiagnostics || alias is { Length: > 0 } && !SyntaxFacts.IsValidIdentifier(alias))
                    throw new InvalidDataException("Invalid referenced global using.");
                projectUsings.Add("global using " + (isStatic ? "static " : "") + (string.IsNullOrEmpty(alias) ? "" : alias + " = ") + name + ";");
            }
            var dependencies = entry.GetProperty("projectReferences").EnumerateArray()
                .Select(x => BuildProject(x.GetString()!).ToMetadataReference()).ToArray();
            var usingTree = CSharpSyntaxTree.ParseText(string.Join("\n", projectUsings), projectParse, "<MSBuild-evaluated-global-usings>");
            var projectNullable = props.GetProperty("Nullable").GetString() == "enable" ? NullableContextOptions.Enable : NullableContextOptions.Disable;
            var nameOfAssembly = props.GetProperty("AssemblyName").GetString();
            built = CSharpCompilation.Create(string.IsNullOrWhiteSpace(nameOfAssembly) ? Path.GetFileNameWithoutExtension(path) : nameOfAssembly,
                projectSources.Append(usingTree), refs.Cast<MetadataReference>().Concat(dependencies),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: projectNullable));
            referenceCompilations[path] = built;
            foreach (var tree in projectSources) projectTrees.Add(tree, built);
            var errors = built.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
                .GroupBy(d => d.Id).Select(g => new { id = g.Key, count = g.Count(), example = g.First().ToString() }).ToArray();
            projectDiagnostics.Add(new { project = path, sourceCount = projectSources.Count, diagnostics = errors });
            inProgress.Remove(path);
            return built;
        }
        foreach (var reference in root.GetProperty("projectReferences").EnumerateArray()) BuildProject(reference.GetString()!);
    }
    // SDK-style projects expose transitive ProjectReference assemblies to the
    // root compilation. They remain separate compilations and retain provenance.
    var directReferences = referenceCompilations.Values.Select(c => c.ToMetadataReference());
    var rootAssembly = properties.TryGetProperty("AssemblyName", out var assemblyProperty) &&
        !string.IsNullOrWhiteSpace(assemblyProperty.GetString()) ? assemblyProperty.GetString()! : "HardwareBridge.Inventory";
    var compilation = CSharpCompilation.Create(rootAssembly, trees.Append(generatedUsings),
        refs.Cast<MetadataReference>().Concat(directReferences),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: nullable));
    object? generatorProvenance = null;
    if (args.Length == 6)
    {
        // This opt-in runs only the named, byte-pinned framework generator in the
        // installed .NET reference pack, not arbitrary project analyzers.
        var manifestBytes = File.ReadAllBytes(args[5]);
        using var manifestDoc = JsonDocument.Parse(manifestBytes);
        var generatorManifest = manifestDoc.RootElement;
        if (generatorManifest.GetProperty("schema").GetString() != "hardwarebridge.sdk-json-generator/v1")
            throw new InvalidDataException("Invalid generator manifest.");
        var generatorPath = Path.GetFullPath(generatorManifest.GetProperty("path").GetString()!);
        var packRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "packs", "Microsoft.NETCore.App.Ref"));
        if (!generatorPath.StartsWith(packRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(generatorPath), "System.Text.Json.SourceGeneration.dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only the explicitly pinned installed SDK JSON generator is allowed.");
        var generatorBytes = File.ReadAllBytes(generatorPath);
        var generatorHash = Convert.ToHexString(SHA256.HashData(generatorBytes)).ToLowerInvariant();
        if (generatorHash != generatorManifest.GetProperty("sha256").GetString())
            throw new InvalidDataException("Stale SDK generator bytes.");
        var reference = new AnalyzerFileReference(generatorPath, new SdkAnalyzerLoader());
        var generators = reference.GetGenerators(LanguageNames.CSharp);
        if (generators.Length == 0) throw new InvalidDataException("SDK JSON generator is not loadable.");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, parseOptions: parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var driverDiagnostics);
        var run = driver.GetRunResult();
        var generated = run.Results.SelectMany(result => result.GeneratedSources.Select(source => new {
            generator = result.Generator.GetType().FullName,
            hintName = source.HintName,
            path = source.SyntaxTree.FilePath,
            sha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source.SourceText.ToString()))).ToLowerInvariant(),
            text = source.SourceText.ToString()
        })).ToArray();
        var generatorDiagnostics = driverDiagnostics.Concat(run.Diagnostics).Select(d => d.ToString()).Distinct().ToArray();
        compilation = (CSharpCompilation)updated;
        foreach (var tree in compilation.SyntaxTrees.Except(trees).Where(t => t != generatedUsings))
            projectTrees.Add(tree, compilation);
        generatorProvenance = new { manifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
            path = generatorPath, sha256 = generatorHash, status = "opt-in-unqualified", generated, diagnostics = generatorDiagnostics };
    }
    foreach (var tree in trees) projectTrees.Add(tree, compilation);
    var groups = new Dictionary<INamedTypeSymbol, List<TypeDeclarationSyntax>>(SymbolEqualityComparer.Default);
    foreach (var (tree, owner) in projectTrees)
    {
        var model = owner.GetSemanticModel(tree);
        foreach (var decl in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(decl) is not INamedTypeSymbol symbol ||
                symbol.TypeKind is not (TypeKind.Class or TypeKind.Struct)) continue;
            if (!groups.TryGetValue(symbol, out var list)) groups[symbol] = list = [];
            list.Add(decl);
        }
    }
    object Position(Location location)
    {
        var line = location.GetLineSpan();
        return new { file = line.Path, startLine = line.StartLinePosition.Line + 1,
            startColumn = line.StartLinePosition.Character + 1,
            endLine = line.EndLinePosition.Line + 1, endColumn = line.EndLinePosition.Character + 1 };
    }
    string SymbolName(ISymbol s) => s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    string SymbolId(ISymbol s) => s.GetDocumentationCommentId() ??
        (s.Kind + ":" + s.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
    object[] MemberDependencies(ISymbol member)
    {
        var edges = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var reference in member.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax();
            if (!projectTrees.TryGetValue(syntax.SyntaxTree, out var owner)) continue;
            var model = owner.GetSemanticModel(syntax.SyntaxTree);
            // Type declarations have their own inventory entry. Do not attribute
            // an entire nested type's references to its containing member/type.
            if (member is INamedTypeSymbol) continue;
            foreach (var node in syntax.DescendantNodesAndSelf(n => n == syntax || n is not TypeDeclarationSyntax))
            {
                string? kind = node switch
                {
                    InvocationExpressionSyntax => "invocation",
                    ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax => "construction",
                    IdentifierNameSyntax => "member-reference",
                    _ => null
                };
                if (kind is null) continue;
                var target = model.GetSymbolInfo(node).Symbol;
                if (target is IMethodSymbol method && kind is "invocation" or "construction")
                    target = (method.ReducedFrom ?? method).OriginalDefinition;
                else if (kind == "member-reference" && target is IFieldSymbol or IPropertySymbol or IEventSymbol)
                    target = target.OriginalDefinition;
                else continue;
                if (target is null || SymbolEqualityComparer.Default.Equals(target, member)) continue;
                var id = SymbolId(target);
                var assembly = target.ContainingAssembly?.Identity.ToString() ?? "";
                var key = kind + "|" + assembly + "|" + id;
                edges.TryAdd(key, new { kind, target = id, targetDisplay = SymbolName(target),
                    targetAssembly = assembly, firstLocation = Position(node.GetLocation()) });
            }
        }
        return edges.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Value).ToArray();
    }
    var results = new List<object>();
    foreach (var (symbol, declarations) in groups.OrderBy(p => SymbolName(p.Key), StringComparer.Ordinal))
    {
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decl in declarations)
        {
            var model = projectTrees[decl.SyntaxTree].GetSemanticModel(decl.SyntaxTree);
            foreach (var syntax in decl.DescendantNodes(n => n == decl || n is not TypeDeclarationSyntax).OfType<TypeSyntax>())
            {
                var type = model.GetTypeInfo(syntax).Type;
                if (type is INamedTypeSymbol named && named.TypeKind != TypeKind.Error &&
                    !SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, symbol.OriginalDefinition))
                    dependencies.Add(SymbolName(named));
            }
        }
        var attributes = symbol.GetAttributes().Select(a => new {
            type = a.AttributeClass is null ? null : SymbolName(a.AttributeClass),
            text = a.ApplicationSyntaxReference?.GetSyntax().ToString(),
            location = a.ApplicationSyntaxReference is null ? null : Position(a.ApplicationSyntaxReference.GetSyntax().GetLocation())
        }).ToArray();
        var members = symbol.GetMembers().Where(m => !m.IsImplicitlyDeclared).Select(m => new {
            id = SymbolId(m), symbol = SymbolName(m), kind = m.Kind.ToString(), accessibility = m.DeclaredAccessibility.ToString(),
            isStatic = m.IsStatic, dependencies = MemberDependencies(m), locations = m.Locations.Where(l => l.IsInSource).Select(Position).ToArray(),
            attributes = m.GetAttributes().Select(a => a.ApplicationSyntaxReference?.GetSyntax().ToString() ?? a.AttributeClass?.ToDisplayString()).ToArray()
        }).OrderBy(m => m.symbol, StringComparer.Ordinal).ToArray();
        results.Add(new {
            symbol = SymbolName(symbol), assembly = symbol.ContainingAssembly.Name, metadataName = symbol.MetadataName, kind = symbol.TypeKind.ToString(),
            @namespace = symbol.ContainingNamespace.ToDisplayString(),
            containingType = symbol.ContainingType is null ? null : SymbolName(symbol.ContainingType),
            accessibility = symbol.DeclaredAccessibility.ToString(), attributes, members,
            declarations = declarations.Select(d => new { location = Position(d.Identifier.GetLocation()),
                modifiers = d.Modifiers.ToString(), attributes = d.AttributeLists.Select(a => a.ToString()).ToArray(),
                memberIds = symbol.GetMembers().Where(m => !m.IsImplicitlyDeclared && m.DeclaringSyntaxReferences.Any(r =>
                    r.SyntaxTree == d.SyntaxTree && d.Span.Contains(r.Span) &&
                    r.GetSyntax().Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Span == d.Span))
                    .Select(SymbolId).Order(StringComparer.Ordinal).ToArray() }).ToArray(),
            dependencies = dependencies.Order(StringComparer.Ordinal).ToArray(),
            coverage = new { inventory = "partial-semantic", hardwareRole = "unclassified", hwir = "not-established", rtl = "not-established", simulation = "not-run", independentVerification = "not-run", studio = "not-published" }
        });
    }
    var diagnostics = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
        .GroupBy(d => d.Id).Select(g => new { id = g.Key, count = g.Count(), example = g.First().ToString() })
        .OrderBy(d => d.id, StringComparer.Ordinal).ToArray();
    var output = new {
        schema = "hardwarebridge.semantic-inventory/v1", disposition = "incomplete-unqualified",
        limitations = new[] { "ProjectReference source evaluations are optional, independently evaluated and may not match inherited MSBuild global properties; only an explicitly selected SDK JSON generator may run; the full generator set is not resolved.",
            "Framework references come from the executing runtime, not the evaluated project reference graph.",
            "Compilation errors can leave semantic dependencies and attributes unresolved; no hardware roles are inferred.",
            "TypeSyntax and selected bound member references are observed; dynamic, indirect, implicit and dataflow edges are not a complete closure.",
            "When languageVersionExact is false, preview parsing is an approximation, not the evaluated compiler language version." },
        sourceInventorySha256 = Convert.ToHexString(SHA256.HashData(inputBytes)).ToLowerInvariant(),
        project = root.GetProperty("project").GetString(), configuration = properties.GetProperty("Configuration").GetString(),
        targetFramework = properties.GetProperty("TargetFramework").GetString(), rootAssembly, languageVersion = language, roslynLanguageVersion = version.ToString(), languageVersionExact = languageExact, roslynVersion = typeof(CSharpCompilation).Assembly.GetName().Version?.ToString(), defines, globalUsings = root.GetProperty("globalUsings").Clone(),
        projectReferences = root.GetProperty("projectReferences").Clone(), projectGraphSha256 = graphDigest, generatorProvenance, projectDiagnostics, runtimeMetadataReferences = referenceIdentities, files, diagnostics, types = results
    };
    var json = JsonSerializer.SerializeToUtf8Bytes(output, new JsonSerializerOptions { WriteIndented = true });
    // Refuse to overwrite any input, including the model's files. The caller selects a new output.
    if (paths.Contains(destination) || string.Equals(destination, Path.GetFullPath(args[0]), StringComparison.OrdinalIgnoreCase) ||
        args.Length >= 4 && string.Equals(destination, Path.GetFullPath(args[3]), StringComparison.OrdinalIgnoreCase) ||
        args.Length == 6 && string.Equals(destination, Path.GetFullPath(args[5]), StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Output overlaps input.");
    using (var outputFile = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) outputFile.Write(json);
    Console.WriteLine($"Inventoried {results.Count} class/struct symbols from {trees.Count} byte-verified trees; {diagnostics.Sum(d => d.count)} compilation errors. INCOMPLETE/UNQUALIFIED.");
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
{
    Console.Error.WriteLine("Inventory refused: " + ex.Message);
    return 2;
}
#endif
