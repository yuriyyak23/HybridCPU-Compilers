using HybridCPU.Compiler.Roslyn;
using HybridCPU.Compiler.Cil;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text.Json;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis.Emit;
using System.Reflection;

internal static class SourceCliChecks
{
    public static void Run()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "CliFixture.txt");
        string root = Path.Combine(Path.GetTempPath(), "source-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "source.hcexe");
        string[] Args(string method, string path) => ["compile-source-image", "--source", fixture,
            "--reference", typeof(object).Assembly.Location, "--assembly-name", "CliFixture", "--type", "CliFixture",
            "--method", method, "--out", path];
        (int Exit, string Output, string Error) Run(string[] args, CancellationToken token = default) {
            using StringWriter output = new StringWriter(); using StringWriter error = new StringWriter();
            int exit = ManagedSourceCommandLineV1.Run(args, output, error, token);
            return (exit, output.ToString(), error.ToString());
        }
        (int Exit, string Output, string Error) first = Run(Args("Read", target));
        if (first.Exit != 0 || !File.Exists(target)) throw new Exception("Source CLI positive failed: " + first.Error);
        byte[] bytes = File.ReadAllBytes(target);
        (int Exit, string Output, string Error) repeat = Run(Args("Read", target));
        if (repeat.Exit != 0 || first.Output != repeat.Output || !bytes.SequenceEqual(File.ReadAllBytes(target)))
            throw new Exception("Source CLI deterministic output/identity failed");
        foreach (string method in new[] { "Missing", "Unsupported" }) {
            (int Exit, string Output, string Error) rejected = Run(Args(method, target));
            if (rejected.Exit == 0 || rejected.Output.Length != 0 || !bytes.SequenceEqual(File.ReadAllBytes(target)))
                throw new Exception("Rejected source CLI modified existing image: " + method);
        }
        (int Exit, string Output, string Error) cancelled = Run(Args("Read", target), new CancellationToken(true));
        if (cancelled.Exit != 130 || !bytes.SequenceEqual(File.ReadAllBytes(target)))
            throw new Exception("Source CLI cancellation changed output");
        (int Exit, string Output, string Error) invalid = Run([.. Args("Read", target), "--unknown", "value"]);
        (int Exit, string Output, string Error) overlap = Run(Args("Read", fixture));
        if (invalid.Exit != 2 || overlap.Exit != 2) throw new Exception("Source CLI option/path gate failed");
        string absent = Path.Combine(root, "absent.hcexe");
        foreach ((string? file, string? code) in new[] { ("CliErrorFixture.txt", "CS0103"), ("CliNoHeapFixture.txt", "HC0004") }) {
            string[] arguments = Args("Read", target);
            arguments[2] = Path.Combine(AppContext.BaseDirectory, file);
            (int Exit, string Output, string Error) denied = Run([.. arguments, "--reference", Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll"),
                "--reference", Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "netstandard.dll"),
                "--reference", typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location]);
            if (denied.Exit != 3 || !denied.Error.Contains(code, StringComparison.Ordinal) ||
                denied.Output.Length != 0 || !bytes.SequenceEqual(File.ReadAllBytes(target)))
                throw new Exception("Source CLI diagnostics precedence/output gate failed: " + code + "; " + denied.Error);
        }
        if (Run(Args("Missing", absent)).Exit == 0 || File.Exists(absent))
            throw new Exception("Rejected source CLI created output");
        Console.WriteLine("PASS production source CLI helper -> image, repeat identities/bytes; unsupported/missing/unknown/path/cancel gates preserve or omit output");
        byte[] core = File.ReadAllBytes(typeof(object).Assembly.Location);
        byte[] Emit(string name, string text, params byte[][] references) {
            CSharpCompilation compilation = CSharpCompilation.Create(name,
                [CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.CSharp13))],
                references.Select(image => MetadataReference.CreateFromImage(image)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true, optimizationLevel: OptimizationLevel.Release));
            using MemoryStream stream = new MemoryStream(); EmitResult emitted = compilation.Emit(stream);
            if (!emitted.Success) throw new Exception(string.Join(';', emitted.Diagnostics));
            return stream.ToArray();
        }
        byte[] declaration = Emit("CliDeclaration", "public enum ExternalChoice : int { First = 40 }", core);
        byte[] helper = Emit("CliHelper", "public static class ExternalHelper { public static int Add(ExternalChoice value) => (int)value + 2; }", core, declaration);
        string declarationPath = Path.Combine(root, "declaration.dll"), helperPath = Path.Combine(root, "helper.dll");
        File.WriteAllBytes(declarationPath, declaration); File.WriteAllBytes(helperPath, helper);
        string externalFixture = Path.Combine(AppContext.BaseDirectory, "CliReferenceFixture.txt");
        string[] ExternalArgs(string bodyRole, string metadataRole) => ["compile-source-image", "--source", externalFixture,
            "--reference", typeof(object).Assembly.Location, bodyRole, helperPath, metadataRole, declarationPath,
            "--assembly-name", "CliReference", "--type", "CliFixture", "--method", "Read", "--out", target];
        (int Exit, string Output, string Error) external = Run(ExternalArgs("--body-reference", "--metadata-reference"));
        if (external.Exit != 0) throw new Exception("CLI role consumer failed: " + external.Error);
        byte[] externalBytes = File.ReadAllBytes(target);
        CompanionSourceRequestV1 request = new CompanionSourceRequestV1("CliReference", [new(Path.GetFullPath(externalFixture).Replace('\\', '/'), File.ReadAllText(externalFixture))],
            [core, helper, declaration]);
        ManagedSourceBodyWorldResultV1 graph = ManagedSourceCompanionV1.CompileBodyWorld(request,
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("CliFixture", "Read")], [1], [2]);
        if (graph.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("CLI API oracle graph failed");
        ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(graph.Compilation, graph.Compilation.Graph!.RootIdentities.Single());
        if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success ||
            !externalBytes.SequenceEqual(linked.RestrictedImage!.PackageBytes) ||
            !external.Output.Contains(graph.Source.SourceInputDigest!, StringComparison.Ordinal))
        {
            File.WriteAllBytes(Path.Combine(root, "cli-oracle.hcexe"), externalBytes);
            if (linked.RestrictedImage is not null) File.WriteAllBytes(Path.Combine(root, "api-oracle.hcexe"), linked.RestrictedImage.PackageBytes);
            throw new Exception("CLI/API source/graph image byte oracle mismatch: link=" + linked.Status +
                "; diagnostics=" + string.Join(';', linked.Diagnostics) + "; CLI=" + external.Output +
                "; API input=" + graph.Source.SourceInputDigest + "; evidence=" + root);
        }
        foreach ((string, string) roles in new[] { ("--metadata-reference", "--metadata-reference"), ("--reference", "--metadata-reference"),
            ("--body-reference", "--reference") }) {
            (int Exit, string Output, string Error) denied = Run(ExternalArgs(roles.Item1, roles.Item2));
            if (denied.Exit != 4 || denied.Output.Length != 0 || !externalBytes.SequenceEqual(File.ReadAllBytes(target)))
                throw new Exception("CLI omitted/swapped body/metadata role failed closed: " + roles + "; " + denied.Error);
        }
        (int Exit, string Output, string Error) externalRepeat = Run(ExternalArgs("--body-reference", "--metadata-reference"));
        if (externalRepeat.Exit != 0 || externalRepeat.Output != external.Output || !externalBytes.SequenceEqual(File.ReadAllBytes(target)))
            throw new Exception("CLI explicit-reference repeat mismatch");
        Console.WriteLine("PASS source CLI cross-assembly helper/enum metadata roles; API image bytes/input identity oracle; omitted/swapped-role rejection and repeat");
        string pePath = Path.Combine(root, "source.dll"), pdbPath = Path.Combine(root, "source.pdb"), mapPath = Path.Combine(root, "source-map.json");
        string[] exportArgs = [.. ExternalArgs("--body-reference", "--metadata-reference"), "--emit-pe", pePath,
            "--emit-pdb", pdbPath, "--emit-map", mapPath];
        (int Exit, string Output, string Error) exported = Run(exportArgs);
        if (exported.Exit != 0 || !File.ReadAllBytes(pePath).SequenceEqual(graph.Source.Pe!) ||
            !File.ReadAllBytes(pdbPath).SequenceEqual(graph.Source.Pdb!))
            throw new Exception("CLI exact debug artifact export differs from owned source API");
        byte[] mapBytes = File.ReadAllBytes(mapPath);
        Dictionary<string, ManagedSourceMapV1> maps = JsonSerializer.Deserialize<Dictionary<string, ManagedSourceMapV1>>(mapBytes)!;
        if (maps.Count != graph.SourceMaps.Count || maps.Any(row => row.Value.Digest != graph.SourceMaps[row.Key].Digest))
            throw new Exception("CLI exact PE/PDB source-map export mismatch");
        if (Run(exportArgs).Exit != 0 || !mapBytes.SequenceEqual(File.ReadAllBytes(mapPath)))
            throw new Exception("CLI deterministic source-map file repeat failed");
        (int Exit, string Output, string Error) badExport = Run([.. exportArgs, "--emit-pe", target]);
        string[] overlapping = (string[])exportArgs.Clone(); overlapping[^5] = target;
        if (badExport.Exit != 2 || Run(overlapping).Exit != 2 ||
            Run(exportArgs, new CancellationToken(true)).Exit != 130 || !mapBytes.SequenceEqual(File.ReadAllBytes(mapPath)))
            throw new Exception("CLI debug export duplicate/overlap/cancellation gate failed");
        Console.WriteLine("PASS source CLI optional exact PE/PDB/map export matches API, deterministic repeat and distinct-output/cancellation guards");
        string[] genericArgs = Args("Identity", target);
        genericArgs[2] = Path.Combine(AppContext.BaseDirectory, "CliGenericFixture.txt");
        (int Exit, string Output, string Error) openRoot = Run(genericArgs);
        if (openRoot.Exit != 4 || !openRoot.Error.Contains("HCCIL1012", StringComparison.Ordinal))
            throw new Exception("Open CLI generic root did not fail closed");
        CompanionSourceRequestV1 genericRequest = new CompanionSourceRequestV1("CliFixture",
            [new(Path.GetFullPath(genericArgs[2]).Replace('\\', '/'), File.ReadAllText(genericArgs[2]))], [core]);
        string? previousGraph = null;
        foreach ((string? name, string? identity, RestrictedCilTypeV1 carrier) in new[] { ("int32", "System.Int32", RestrictedCilTypeV1.Int32),
            ("int64", "System.Int64", RestrictedCilTypeV1.Int64) }) {
            (int Exit, string Output, string Error) exact = Run([.. genericArgs, "--method-argument", name]);
            ManagedSourceBodyWorldResultV1 api = ManagedSourceCompanionV1.CompileBodyWorld(genericRequest,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2),
                [new("CliFixture", "Identity", GenericMethodArguments: [new(identity, carrier)])]);
            if (api.Compilation?.Status != RestrictedCilImportStatusV1.Success || exact.Exit != 0)
                throw new Exception("Exact CLI generic root failed: " + name + "; " + exact.Error);
            ScalarControlFlowV2LinkedProgramV1 native = new ScalarControlFlowV2ObjectLinkerV1().Link(api.Compilation, api.Compilation.Graph!.RootIdentities.Single());
            if (native.Status != ScalarControlFlowV2LinkStatusV1.Success ||
                !File.ReadAllBytes(target).SequenceEqual(native.RestrictedImage!.PackageBytes) ||
                previousGraph == api.Compilation.Graph.GraphDigest)
                throw new Exception("CLI generic exact-context/API image oracle failed");
            previousGraph = api.Compilation.Graph.GraphDigest;
            byte[] exactBytes = File.ReadAllBytes(target);
            if (Run([.. genericArgs, "--method-argument", name]).Output != exact.Output ||
                !exactBytes.SequenceEqual(File.ReadAllBytes(target))) throw new Exception("Exact CLI generic repeat failed");
            (int Exit, string Output, string Error) wrongArity = Run([.. genericArgs, "--method-argument", name, "--method-argument", name]);
            if (wrongArity.Exit != 4 || !exactBytes.SequenceEqual(File.ReadAllBytes(target)))
                throw new Exception("CLI generic arity failure modified output");
        }
        if (Run([.. genericArgs, "--method-argument", "object"]).Exit != 2)
            throw new Exception("Unqualified generic CLI type admitted");
        byte[] genericHelper = Emit("ExternalGeneric", "public static class ExternalGeneric { public static T Identity<T>(T value) => value; }", core);
        string genericHelperPath = Path.Combine(root, "generic-helper.dll"); File.WriteAllBytes(genericHelperPath, genericHelper);
        string closedFixture = Path.Combine(AppContext.BaseDirectory, "CliClosedCallFixture.txt");
        string[] closedArgs = Args("Read", target); closedArgs[2] = closedFixture;
        (int Exit, string Output, string Error) closed = Run([.. closedArgs, "--body-reference", genericHelperPath]);
        ManagedSourceBodyWorldResultV1 closedApi = ManagedSourceCompanionV1.CompileBodyWorld(new("CliFixture",
            [new(Path.GetFullPath(closedFixture).Replace('\\', '/'), File.ReadAllText(closedFixture))], [core, genericHelper]),
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("CliFixture", "Read")], [1]);
        if (closed.Exit != 0 || closedApi.Compilation?.Status != RestrictedCilImportStatusV1.Success ||
            closedApi.Compilation.Graph!.ExactGenericInstantiationCount != 1)
            throw new Exception("CLI closed external MethodSpec call failed: " + closed.Error);
        ScalarControlFlowV2LinkedProgramV1 closedImage = new ScalarControlFlowV2ObjectLinkerV1().Link(closedApi.Compilation, closedApi.Compilation.Graph.RootIdentities.Single());
        byte[] closedBytes = File.ReadAllBytes(target);
        if (closedImage.Status != ScalarControlFlowV2LinkStatusV1.Success || !closedBytes.SequenceEqual(closedImage.RestrictedImage!.PackageBytes) ||
            Run([.. closedArgs, "--metadata-reference", genericHelperPath]).Exit != 4 || !closedBytes.SequenceEqual(File.ReadAllBytes(target)))
            throw new Exception("CLI MethodSpec image/metadata-only authority control failed");
        Console.WriteLine("PASS CLI closed external MethodSpec/API image oracle and explicit int32/int64 generic roots; identity/repeat/open/arity/type/body-authority negatives");
        const string constructedLibrary = "public static class Carrier<T> { public static T Identity(T value) => value; public static U Other<U>(U value) => value; }";
        byte[] constructedBody = Emit("ConstructedLibrary", constructedLibrary, core);
        byte[] foreignBody = Emit("ForeignConstructed", constructedLibrary, core);
        string constructedPath = Path.Combine(root, "constructed.dll"); File.WriteAllBytes(constructedPath, constructedBody);
        string foreignPath = Path.Combine(root, "foreign-constructed.dll"); File.WriteAllBytes(foreignPath, foreignBody);
        foreach (string type in new[] { "int", "long" }) foreach (bool methodContext in new[] { false, true }) {
            string expression = methodContext ? "Carrier<int>.Other<long>(value)" : "Carrier<" + type + ">.Identity(value)";
            string valueType = methodContext ? "long" : type;
            string source = "public static class CliFixture { public static " + valueType + " Read(" + valueType + " value) => " + expression + "; }";
            string document = Path.Combine(root, "constructed.cs"); File.WriteAllText(document, source);
            CompanionSourceRequestV1 constructedRequest = new CompanionSourceRequestV1("CliFixture", [new(document.Replace('\\', '/'), source)], [core, constructedBody]);
            ManagedSourceBodyWorldResultV1 CompileConstructed(CompanionSourceRequestV1 input, int[]? bodies) => ManagedSourceCompanionV1.CompileBodyWorld(input,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("CliFixture", "Read")], bodies);
            ManagedSourceBodyWorldResultV1 api = CompileConstructed(constructedRequest, [1]); ManagedSourceBodyWorldResultV1 again = CompileConstructed(constructedRequest, [1]);
            ManagedSourceBodyWorldResultV1 single = CompileConstructed(constructedRequest with { Documents = [new(document.Replace('\\', '/'), source + constructedLibrary)], ReferenceImages = [core] }, null);
            if (api.Compilation?.Status != RestrictedCilImportStatusV1.Success || single.Compilation?.Status != RestrictedCilImportStatusV1.Success)
                throw new Exception("External constructed TypeSpec failed: " + string.Join(';', api.Source.Diagnostics) + "; " + string.Join(';', api.Compilation?.Diagnostics ?? []) + "; single=" + string.Join(';', single.Compilation?.Diagnostics ?? []));
            ScalarControlFlowV2LinkedProgramV1 Image(ManagedSourceBodyWorldResultV1 value) => new ScalarControlFlowV2ObjectLinkerV1().Link(value.Compilation!, value.Compilation!.Graph!.RootIdentities.Single());
            ScalarControlFlowV2LinkedProgramV1 image = Image(api); ScalarControlFlowV2LinkedProgramV1 singleImage = Image(single);
            if (image.Status != ScalarControlFlowV2LinkStatusV1.Success || singleImage.Status != ScalarControlFlowV2LinkStatusV1.Success ||
                !image.MethodObjects.Select(m => m.CodeSha256).Order().SequenceEqual(singleImage.MethodObjects.Select(m => m.CodeSha256).Order()) ||
                !api.Source.Pe!.SequenceEqual(again.Source.Pe!) || !api.Source.Pdb!.SequenceEqual(again.Source.Pdb!) ||
                !image.RestrictedImage!.PackageBytes.SequenceEqual(Image(again).RestrictedImage!.PackageBytes) ||
                api.SourceMaps.Any(p => again.SourceMaps[p.Key].Digest != p.Value.Digest) ||
                api.Compilation.Graph!.ExactGenericInstantiationCount != 1)
                throw new Exception("Constructed external single-module/physical-code/repeat/maps/image oracle");
            foreach (bool wrongBody in new[] { false, true }) {
                ManagedCallGraphCompilationV1 denied = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                    ManagedBodyWorldModeV1.StandaloneRestrictedModule, api.Source.Pe!, "constructed-negative", [new("CliFixture", "Read")], [],
                    DependencyModules: wrongBody ? [new(foreignBody, "foreign-body")] : [],
                    MetadataOnlyModules: [new(core, "core"), new(constructedBody, "declaration")]));
                if (denied.Status == RestrictedCilImportStatusV1.Success || denied.Diagnostics.Count == 0)
                    throw new Exception("Constructed generic body authority/foreign assembly gate");
            }
            string[] cliArgs = Args("Read", target); cliArgs[2] = document;
            (int Exit, string Output, string Error) cli = Run([.. cliArgs, "--body-reference", constructedPath]);
            if (cli.Exit != 0 || !File.ReadAllBytes(target).SequenceEqual(image.RestrictedImage.PackageBytes))
                throw new Exception("External constructed CLI/API image oracle: " + cli.Error);
            AssemblyLoadContext context = new System.Runtime.Loader.AssemblyLoadContext("constructed-generic", isCollectible: true);
            try {
                context.LoadFromStream(new MemoryStream(constructedBody));
                MethodInfo method = context.LoadFromStream(new MemoryStream(api.Source.Pe!)).GetType("CliFixture")!.GetMethod("Read")!;
                foreach (long boundary in new[] { long.MinValue, (long)int.MinValue, -1L, 0L, 1L, int.MaxValue, long.MaxValue }) {
                    object value = valueType == "int" ? (object)unchecked((int)boundary) : boundary;
                    if (!Equals(value, method.Invoke(null, [value]))) throw new Exception("Constructed generic host boundary oracle");
                }
            } finally { context.Unload(); }
        }
        Console.WriteLine("PASS selected external constructed generic type int32/int64 and VAR/MVAR methods -> source/CLI/image, single-module physical code, repeat PE/PDB/maps/image; metadata-only/foreign-body authority negatives");
        string typeFixture = Path.Combine(AppContext.BaseDirectory, "CliGenericTypeFixture.txt");
        string[] typeArgs = Args("Identity", target); typeArgs[2] = typeFixture; typeArgs[8] = "CliGenericType`1";
        CompanionSourceRequestV1 typeRequest = new CompanionSourceRequestV1("CliFixture",
            [new(Path.GetFullPath(typeFixture).Replace('\\', '/'), File.ReadAllText(typeFixture))], [core]);
        string? priorIdentity = null;
        foreach ((string? name, string? identity, RestrictedCilTypeV1 carrier) in new[] { ("int32", "System.Int32", RestrictedCilTypeV1.Int32),
            ("int64", "System.Int64", RestrictedCilTypeV1.Int64) }) {
            (int Exit, string Output, string Error) cli = Run([.. typeArgs, "--type-argument", name]);
            ManagedSourceBodyWorldResultV1 api = ManagedSourceCompanionV1.CompileBodyWorld(typeRequest,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2),
                [new("CliGenericType`1", "Identity", GenericTypeArguments: [new(identity, carrier)])]);
            if (cli.Exit != 0 || api.Compilation?.Status != RestrictedCilImportStatusV1.Success)
                throw new Exception("CLI generic declaring-type failed: " + cli.Error);
            ScalarControlFlowV2LinkedProgramV1 native = new ScalarControlFlowV2ObjectLinkerV1().Link(api.Compilation, api.Compilation.Graph!.RootIdentities.Single());
            byte[] actualBytes = File.ReadAllBytes(target);
            (int Exit, string Output, string Error) typeRepeat =Run([.. typeArgs, "--type-argument", name]);
            if (native.Status != ScalarControlFlowV2LinkStatusV1.Success || !actualBytes.SequenceEqual(native.RestrictedImage!.PackageBytes) ||
                priorIdentity == api.Compilation.Graph.GraphDigest || typeRepeat.Output != cli.Output)
                throw new Exception("CLI type-context/API image/identity/repeat mismatch: "+name+" native="+native.Status+
                    " bytes="+(native.RestrictedImage is not null&&actualBytes.SequenceEqual(native.RestrictedImage.PackageBytes))+
                    " identityDistinct="+(priorIdentity!=api.Compilation.Graph.GraphDigest)+" repeatExit="+typeRepeat.Exit+
                    " first="+cli.Output+" repeat="+typeRepeat.Output+" error="+typeRepeat.Error);
            priorIdentity = api.Compilation.Graph.GraphDigest;
            foreach (string[] badArgs in new[] { typeArgs, [.. typeArgs, "--method-argument", name],
                [.. typeArgs, "--type-argument", name, "--type-argument", name] })
                if (Run(badArgs).Exit != 4 || !actualBytes.SequenceEqual(File.ReadAllBytes(target)))
                    throw new Exception("CLI type-arity/scope failure modified image");
        }
        string[] combined = (string[])typeArgs.Clone(); combined[10] = "Method";
        (int Exit, string Output, string Error) both = Run([.. combined, "--type-argument", "int32", "--method-argument", "int64"]);
        ManagedSourceBodyWorldResultV1 combinedApi = ManagedSourceCompanionV1.CompileBodyWorld(typeRequest,
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2),
            [new("CliGenericType`1", "Method", GenericTypeArguments: [new("System.Int32", RestrictedCilTypeV1.Int32)],
                GenericMethodArguments: [new("System.Int64", RestrictedCilTypeV1.Int64)])]);
        if (both.Exit != 0 || combinedApi.Compilation?.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Combined VAR/MVAR CLI context failed: " + both.Error);
        ScalarControlFlowV2LinkedProgramV1 combinedImage = new ScalarControlFlowV2ObjectLinkerV1().Link(combinedApi.Compilation, combinedApi.Compilation.Graph!.RootIdentities.Single());
        if (combinedImage.Status != ScalarControlFlowV2LinkStatusV1.Success ||
            !File.ReadAllBytes(target).SequenceEqual(combinedImage.RestrictedImage!.PackageBytes) ||
            Run([.. typeArgs, "--type-argument", "object"]).Exit != 2)
            throw new Exception("Combined CLI context/API image/type gate failed");
        Console.WriteLine("PASS CLI declaring-type int32/int64 and combined VAR/MVAR exact contexts, API image oracle; open/arity/scope/type/repeat controls");
        string[] constrained = (string[])typeArgs.Clone(); constrained[8] = "CliConstrained`1";
        byte[] beforeConstraint = File.ReadAllBytes(target);
        (int Exit, string Output, string Error) constraintDenied = Run([.. constrained, "--type-argument", "int32"]);
        if (constraintDenied.Exit != 4 || !constraintDenied.Error.Contains("Generic constraints", StringComparison.Ordinal) ||
            !beforeConstraint.SequenceEqual(File.ReadAllBytes(target)))
            throw new Exception("CLI exact type context bypassed generic constraint admission: " + constraintDenied.Error);
    }
}
