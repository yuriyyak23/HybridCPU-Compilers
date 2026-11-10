using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using System.Text.Json;

internal static class HelperCallChecks
{
    public static void Run()
    {
        ReadOnlyMemory<byte>[] references = [File.ReadAllBytes(typeof(object).Assembly.Location),
            File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
            File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "netstandard.dll")),
            File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        const string source = """
            using HybridCPU.Compiler.Profiles;
            public static class Fixture {
                [HybridCpuKernelNoHeap] public static int Read(int value) => Add(value);
                [HybridCpuKernelNoHeap] private static int Add(int value) => value + 2;
            }
            """;
        CompanionSourceRequestV1 request = new CompanionSourceRequestV1("HelperCall", [new("/src/HelperCall.cs", source)], references);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1("Fixture", "Read");
        ManagedSourceCompanionResultV1 single = ManagedSourceCompanionV1.Compile(request, importer, selector);
        if (single.Pe is null) throw new Exception("Helper-call repro source failed");
        ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
            single.Pe, "roslyn-companion/v1", [selector], []));
        if (graph.Status != RestrictedCilImportStatusV1.Success || graph.Methods.Count != 2)
            throw new Exception("Helper-call existing BodyWorld path failed: " + string.Join(';', graph.Diagnostics));
        if (single.Imported!.Status == RestrictedCilImportStatusV1.Success || !single.Imported.Diagnostics.Any(d => d.Code == "HCCIL1009"))
            throw new Exception("Legacy single-method helper boundary changed");
        ManagedSourceBodyWorldResultV1 Compile(CompanionSourceRequestV1 input, IReadOnlyList<int>? indices = null) =>
            ManagedSourceCompanionV1.CompileBodyWorld(input, importer, [selector], indices);
        ManagedSourceBodyWorldResultV1 result = Compile(request); ManagedSourceBodyWorldResultV1 repeat = Compile(request);
        if (result.Compilation?.Status != RestrictedCilImportStatusV1.Success || result.SourceMaps.Count != 2 ||
            !single.Pe.SequenceEqual(result.Source.Pe!) || !single.Pdb!.SequenceEqual(result.Source.Pdb!) ||
            single.SourceInputDigest != result.Source.SourceInputDigest ||
            JsonSerializer.Serialize(graph) != JsonSerializer.Serialize(result.Compilation) ||
            JsonSerializer.Serialize(result) != JsonSerializer.Serialize(repeat) ||
            result.SourceMaps.Values.Any(m => m.Rows.All(r => r.Document is null)))
            throw new Exception("Source graph/manual emitted-CIL oracle or map/repeat mismatch");
        ManagedSourceBodyWorldResultV1 leaf = ManagedSourceCompanionV1.CompileBodyWorld(request, importer, [new("Fixture", "Add")]);
        if (leaf.Compilation?.Methods.Count != 1 || leaf.Compilation.Graph!.GraphDigest == result.Compilation.Graph!.GraphDigest ||
            leaf.Source.SourceInputDigest != result.Source.SourceInputDigest)
            throw new Exception("Source identity and selected-root graph identity scopes mixed");
        ScalarControlFlowV2LinkedProgramV1 Link(ManagedSourceBodyWorldResultV1 input) {
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(input.Compilation!, input.Compilation!.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || linked.RestrictedImage is null)
                throw new Exception("Source helper graph link/image failed: " + string.Join(';', linked.Diagnostics));
            return linked;
        }
        ScalarControlFlowV2LinkedProgramV1 linked = Link(result); ScalarControlFlowV2LinkedProgramV1 linkedRepeat = Link(repeat);
        if (!linked.RestrictedImage!.PackageBytes.SequenceEqual(linkedRepeat.RestrictedImage!.PackageBytes))
            throw new Exception("Source helper image repeat failed");
        void Denied(string text, string id) {
            ManagedSourceBodyWorldResultV1 denied = Compile(request with { Documents = [new("/src/HelperCall.cs", text)] });
            if (denied.Source.Pe is not null || denied.Source.Pdb is not null || denied.Compilation is not null ||
                denied.Source.ArtifactIdentity is not null || denied.SourceMaps.Count != 0 ||
                !denied.Source.Diagnostics.Any(d => d.Id == id && d.Severity == "Error"))
                throw new Exception("Source graph pre-emit rejection failed: " + id);
        }
        Denied(source.Replace("value + 2", "missing"), "CS0103");
        Denied(source.Replace("value + 2", "new int[1].Length"), "HC0004");
        ManagedSourceBodyWorldResultV1 unsupported = Compile(request with { Documents = [new("/src/HelperCall.cs", source.Replace("value + 2", "(int)((double)value + 0.5)"))] });
        if (unsupported.Source.Pe is null || unsupported.Compilation is null || unsupported.Compilation.Graph is not null ||
            unsupported.Compilation.Status == RestrictedCilImportStatusV1.Success || unsupported.SourceMaps.Count != 0)
            throw new Exception("Unsupported reachable helper returned a successful graph/maps");
        bool cancelled = false;
        try { ManagedSourceCompanionV1.CompileBodyWorld(request, importer, [selector], cancellationToken: new CancellationToken(true)); }
        catch (OperationCanceledException) { cancelled = true; }
        if (!cancelled || JsonSerializer.Serialize(Compile(request)) != JsonSerializer.Serialize(result))
            throw new Exception("Source graph cancellation/reuse failed");

        byte[] Dependency(int add) {
            CSharpCompilation compilation = CSharpCompilation.Create("HelperDependency",
                [CSharpSyntaxTree.ParseText($"public static class HelperDependency {{ public static int Add(int value) => value + {add}; }}",
                    new CSharpParseOptions(LanguageVersion.CSharp13))],
                [MetadataReference.CreateFromImage(references[0].ToArray())],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, deterministic: true));
            using MemoryStream stream = new MemoryStream(); EmitResult emitted = compilation.Emit(stream);
            if (!emitted.Success) throw new Exception(string.Join(';', emitted.Diagnostics));
            return stream.ToArray();
        }
        CompanionSourceRequestV1 externalRequest = request with { AssemblyName = "ExternalHelperCall", Documents = [new("/src/HelperCall.cs",
            source.Replace("=> Add(value)", "=> HelperDependency.Add(value)"))], ReferenceImages = [.. references, Dependency(2)] };
        ManagedSourceBodyWorldResultV1 external = Compile(externalRequest, [4]);
        if (external.Compilation?.Status != RestrictedCilImportStatusV1.Success || external.Compilation.Methods.Count != 2 ||
            external.SourceMaps.Count != 1 || external.SourceMaps.Keys.Single() != external.Compilation.Graph!.RootIdentities.Single())
            throw new Exception("Explicit owned dependency graph/source-map module filtering failed: " + string.Join(';', external.Compilation?.Diagnostics ?? []));
        Link(external);
        ManagedSourceBodyWorldResultV1 omitted = Compile(externalRequest);
        if (omitted.Source.Pe is null || omitted.Compilation?.Status == RestrictedCilImportStatusV1.Success || omitted.SourceMaps.Count != 0)
            throw new Exception("Unselected Roslyn reference was granted body authority");
        ManagedSourceBodyWorldResultV1 changedReference = Compile(externalRequest with { ReferenceImages = [.. references, Dependency(3)] }, [4]);
        if (changedReference.Compilation?.Status != RestrictedCilImportStatusV1.Success ||
            changedReference.Source.SourceInputDigest == external.Source.SourceInputDigest ||
            JsonSerializer.Serialize(changedReference.Compilation) == JsonSerializer.Serialize(external.Compilation))
            throw new Exception("Owned body reference replacement failed identity invalidation");
        foreach (int[] indices in new[] { new[] { -1 }, new[] { 5 }, new[] { 4, 4 } }) {
            bool rejected = false;
            try { Compile(externalRequest, indices); } catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new Exception("Invalid dependency indices admitted");
        }
        RestrictedCilImporterV1 limitedImporter = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2,
            graphBudgets: ScalarControlFlowV2ProfileContractV1.Default.Budgets with { MaximumReachableMethods = 1 });
        ManagedSourceBodyWorldResultV1 limited = ManagedSourceCompanionV1.CompileBodyWorld(request, limitedImporter, [selector]);
        if (limited.Source.Pe is null || limited.Compilation?.Status != RestrictedCilImportStatusV1.BudgetExhausted ||
            limited.Compilation.Graph is not null || limited.SourceMaps.Count != 0 ||
            ManagedSourceCompanionV1.CompileBodyWorld(request, limitedImporter, [new("Fixture", "Add")]).Compilation?.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Source graph method budget/fail-closed/reuse failed");
        CompanionSourceRequestV1 genericRequest = request with { Documents = [new("/src/Generic.cs",
            "public static class Fixture { public static T Identity<T>(T value) => value; }")] };
        RestrictedCilMethodSelectorV1 i32Root = new RestrictedCilMethodSelectorV1("Fixture", "Identity",
            GenericMethodArguments: [new("System.Int32", RestrictedCilTypeV1.Int32)]);
        RestrictedCilMethodSelectorV1 i64Root = i32Root with { GenericMethodArguments = [new("System.Int64", RestrictedCilTypeV1.Int64)] };
        ManagedSourceBodyWorldResultV1 i32 = ManagedSourceCompanionV1.CompileBodyWorld(genericRequest, importer, [i32Root]);
        ManagedSourceBodyWorldResultV1 i64 = ManagedSourceCompanionV1.CompileBodyWorld(genericRequest, importer, [i64Root]);
        ManagedSourceBodyWorldResultV1 open = ManagedSourceCompanionV1.CompileBodyWorld(genericRequest, importer,
            [i32Root with { GenericMethodArguments = [] }]);
        if (i32.Compilation?.Status != RestrictedCilImportStatusV1.Success || i64.Compilation?.Status != RestrictedCilImportStatusV1.Success ||
            i32.SourceMaps.Count != 1 || i64.SourceMaps.Count != 1 ||
            i32.Source.SourceInputDigest != i64.Source.SourceInputDigest || i32.SourceMaps.Keys.Single() == i64.SourceMaps.Keys.Single() ||
            i32.Compilation.Graph!.GraphDigest == i64.Compilation.Graph!.GraphDigest ||
            open.Compilation?.Status == RestrictedCilImportStatusV1.Success || open.SourceMaps.Count != 0)
            throw new Exception("Source graph exact-generic root/map/identity/arity gates failed");
        Link(i32); Link(i64);
        Console.WriteLine("PASS source helper graph ingress/manual emitted-CIL oracle; maps/identities/root scopes; graph/link/image repeat; source/NoHeap/unsupported/cancel/reuse negatives");
        Console.WriteLine("PASS explicit owned body-reference selection, omitted/change/invalid-index controls and primary-PDB dependency token filtering");
        Console.WriteLine("PASS source graph method-budget/reuse and exact Int32/Int64 generic-root/map/identity/open-arity controls");
        Console.WriteLine($"IDENTITY source-bodyworld input={result.Source.SourceInputDigest}; artifact={result.Source.ArtifactIdentity!.Digest}; graph={result.Compilation.Graph!.GraphDigest}; image={linked.RestrictedImage.PackageSha256}; externalGraph={external.Compilation.Graph!.GraphDigest}");
    }
}
