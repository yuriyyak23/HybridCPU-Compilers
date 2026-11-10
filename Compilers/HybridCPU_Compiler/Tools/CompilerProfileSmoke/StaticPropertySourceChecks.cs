using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Roslyn;

internal static class StaticPropertySourceChecks
{
    public static void Run()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "CliStaticPropertyFixture.txt");
        string directory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string[] referencePaths = [typeof(object).Assembly.Location, Path.Combine(directory, "System.Runtime.dll"),
            Path.Combine(directory, "netstandard.dll"), typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location];
        CompanionSourceRequestV1 request = new CompanionSourceRequestV1("StaticPropertySource", [new(Path.GetFullPath(path).Replace('\\', '/'), File.ReadAllText(path))],
            referencePaths.Select(p => (ReadOnlyMemory<byte>)File.ReadAllBytes(p)).ToArray());
        ManagedSourceBodyWorldResultV1 result = ManagedSourceCompanionV1.CompileBodyWorld(request,
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("StaticPropertyFixture", "Read")]);
        Console.WriteLine("REPRO static property source: emitted=" + (result.Source.Pe is not null) + "; status=" + result.Compilation?.Status +
            "; source=" + string.Join(';', result.Source.Diagnostics) + "; graph=" + string.Join(';', result.Compilation?.Diagnostics ?? []));
        if (result.Compilation?.Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Static scalar source property consumer failed");
        ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(result.Compilation, result.Compilation.Graph!.RootIdentities.Single());
        if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success)
            throw new Exception("Static scalar source link failed: " + string.Join(';', linked.Diagnostics));
        ManagedCompiledMethodV1 cctor = result.Compilation.Methods.Single(m => m.Identity.MethodName == ".cctor");
        if (!result.Compilation.Graph.Edges.Any(e => e.IsTypeInitializerTarget && e.CalleeIdentity == cctor.Identity.StableIdentity) ||
            result.Compilation.ProfileClosure?.RootProfiles.Contains(ManagedProfileClosureContractV1.KernelNoHeap) != true ||
            linked.RestrictedImage?.RuntimeBootstrap?.ModuleInitializers is not [{ } initializer] ||
            initializer.InitializerSymbol != cctor.Identity.StableIdentity ||
            !result.Compilation.TypeUniverse!.Rows.Any(t => t.StableIdentity == cctor.Identity.DeclaringType && t.TypeId == initializer.TypeId) ||
            result.SourceMaps.Values.Any(m => m.Rows.All(r => r.Document is null)))
            throw new Exception("Source auto-property lost cctor/profile/PDB/bootstrap identity");
        ManagedSourceBodyWorldResultV1 Compile(string text) => ManagedSourceCompanionV1.CompileBodyWorld(request with { Documents =
            [new(request.Documents[0].VirtualPath, text)] }, new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2),
            [new("StaticPropertyFixture", "Read")]);
        string source = request.Documents[0].Text;
        foreach (string text in new[] { source.Replace("= 42;", "= System.Array.Empty<int>().Length;"),
            source.Replace("= 42;", "= global::System.Array.Empty<byte>().Length;"),
            "using A = System.Array;\n" + source.Replace("= 42;", "= A.Empty<int>().Length;"),
            "#pragma warning disable HC0004\n" + source.Replace("= 42;", "= System.Array.Empty<int>().Length;") }) {
            ManagedSourceBodyWorldResultV1 denied = Compile(text);
            if (denied.Source.Pe is not null || denied.Source.Pdb is not null || denied.Compilation is not null ||
                denied.Source.ArtifactIdentity is not null || denied.SourceMaps.Count != 0 ||
                !denied.Source.Diagnostics.Any(d => d.Id == "HC0004" && d.Severity == "Error"))
                throw new Exception("NoHeap cached-array initializer crossed source admission");
        }
        ManagedSourceBodyWorldResultV1 unprofiled = Compile(source.Replace("[HybridCpuKernelNoHeap]", "").Replace("= 42;", "= System.Array.Empty<int>().Length;"));
        ManagedSourceBodyWorldResultV1 ownEmpty = Compile(source.Replace("= 42;", "= Own.Empty<int>();") +
            "public static class Own { public static int Empty<T>() => 42; }");
        if (unprofiled.Source.Pe is null || unprofiled.Source.Diagnostics.Any(d => d.Id == "HC0004") ||
            ownEmpty.Compilation?.Status != RestrictedCilImportStatusV1.Success || ownEmpty.Source.Diagnostics.Any(d => d.Id == "HC0004"))
            throw new Exception("Cached-array NoHeap rule affected unprofiled/custom Empty method");
        ManagedSourceBodyWorldResultV1 repeat = Compile(source);
        ScalarControlFlowV2LinkedProgramV1 repeatLink = new ScalarControlFlowV2ObjectLinkerV1().Link(repeat.Compilation!, repeat.Compilation!.Graph!.RootIdentities.Single());
        if (repeatLink.Status != ScalarControlFlowV2LinkStatusV1.Success ||
            !linked.RestrictedImage.PackageBytes.SequenceEqual(repeatLink.RestrictedImage!.PackageBytes) ||
            result.SourceMaps.Any(row => repeat.SourceMaps[row.Key].Digest != row.Value.Digest))
            throw new Exception("Static property source/map/image repeat failed");
        string cliRoot = Path.Combine(Path.GetTempPath(), "source-static-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cliRoot); string imagePath = Path.Combine(cliRoot, "static.hcexe");
        using StringWriter output = new StringWriter(); using StringWriter error = new StringWriter();
        int exit = ManagedSourceCommandLineV1.Run(["compile-source-image", "--source", path,
            .. referencePaths.SelectMany(p => new[] { "--reference", p }), "--assembly-name", request.AssemblyName,
            "--type", "StaticPropertyFixture", "--method", "Read", "--out", imagePath], output, error);
        if (exit != 0 || !File.ReadAllBytes(imagePath).SequenceEqual(linked.RestrictedImage.PackageBytes))
            throw new Exception("Static property CLI/API image oracle failed: " + error);
        Console.WriteLine("PASS NoHeap scalar auto-property source/graph/map/cctor/bootstrap/CLI image repeat; cached-array alias/global/suppression pre-emit and unprofiled/custom controls; policy v8");
        Console.WriteLine($"IDENTITY static-property artifact={result.Source.ArtifactIdentity!.Digest}; graph={result.Compilation.Graph.GraphDigest}; image={linked.RestrictedImage.PackageSha256}; cctor={cctor.Identity.StableIdentity}");
    }
}
