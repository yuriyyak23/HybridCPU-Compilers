using HybridCPU.Compiler.Roslyn;
using HybridCPU.Compiler.Cil;

internal static class SourceCliLanguageChecks
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "source-cli-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "source.hcexe"), pe = Path.Combine(root, "source.dll"), pdb = Path.Combine(root, "source.pdb");
        string coreDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string[] referencePaths = [typeof(object).Assembly.Location, Path.Combine(coreDirectory, "System.Runtime.dll"),
            Path.Combine(coreDirectory, "netstandard.dll"), typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location];
        string[] Args(string fixture) => ["compile-source-image", "--source", Path.Combine(AppContext.BaseDirectory, fixture),
            .. referencePaths.SelectMany(path => new[] { "--reference", path }), "--assembly-name", "LanguageCli",
            "--type", "Fixture", "--method", "Read", "--out", target, "--emit-pe", pe, "--emit-pdb", pdb];
        (int Exit, string Output, string Error) Run(string[] args, CancellationToken token = default) {
            using StringWriter output = new StringWriter(); using StringWriter error = new StringWriter();
            int exit = ManagedSourceCommandLineV1.Run(args, output, error, token);
            return (exit, output.ToString(), error.ToString());
        }
        (int Exit, string Output, string Error) defaultDenied = Run(Args("CliLanguageFixture.txt"));
        if (defaultDenied.Exit != 3 || File.Exists(target) || File.Exists(pe) || File.Exists(pdb))
            throw new Exception("CLI implicit CSharp13 admitted labeled continue");
        bool sdkHost = typeof(ManagedSourceCompanionV1).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Any(a => a.Key == "CompanionSdkRoslynHost");
        string[] selected = [.. Args("CliLanguageFixture.txt"), "--language", "csharp15"];
        (int Exit, string Output, string Error) current = Run(selected);
        if (!sdkHost) {
            if (current.Exit != 2 || !current.Error.Contains("HCSOURCE1001", StringComparison.Ordinal) || File.Exists(target) || File.Exists(pe))
                throw new Exception("CLI package host admitted CSharp15");
            Console.WriteLine("PASS CLI CSharp13 default/source rejection and explicit CSharp15 package-host refusal before output");
            return;
        }
        if (current.Exit != 0 || !File.Exists(target) || !File.Exists(pe) || !File.Exists(pdb))
            throw new Exception("SDK CLI nested-loop image consumer failed: " + current.Error);
        byte[] imageBytes = File.ReadAllBytes(target), emittedPe = File.ReadAllBytes(pe), emittedPdb = File.ReadAllBytes(pdb);
        CompanionSourceRequestV1 request = new CompanionSourceRequestV1("LanguageCli",
            [new(Path.GetFullPath(selected[2]).Replace('\\', '/'), File.ReadAllText(selected[2]))],
            referencePaths.Select(path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path)).ToArray()) { Language = CompanionSourceLanguageV1.CSharp15 };
        ManagedSourceBodyWorldResultV1 api = ManagedSourceCompanionV1.CompileBodyWorld(request,
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), [new("Fixture", "Read")]);
        if (api.Compilation?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("SDK language API oracle failed");
        ScalarControlFlowV2LinkedProgramV1 budgetLink = new ScalarControlFlowV2ObjectLinkerV1().Link(api.Compilation, api.Compilation.Graph!.RootIdentities.Single());
        if (budgetLink.Status != ScalarControlFlowV2LinkStatusV1.Success || budgetLink.RestrictedImage is null ||
            !imageBytes.SequenceEqual(budgetLink.RestrictedImage.PackageBytes) ||
            !emittedPe.SequenceEqual(api.Source.Pe!) || !emittedPdb.SequenceEqual(api.Source.Pdb!) ||
            Run(selected).Output != current.Output || !imageBytes.SequenceEqual(File.ReadAllBytes(target)))
            throw new Exception("API nested-loop exact PE/PDB/image/repeat oracle mismatch");
        ManagedCallGraphCompilationV1 strictGraph = api.Compilation with { Methods = api.Compilation.Methods.Select(method => method with {
            Import = method.Import with { Program = method.Import.Program! with {
                Contract = method.Import.Program.Contract with { RequiredCapabilities = method.Import.Program.Contract.RequiredCapabilities
                    .Where(capability => capability != "optimization.loop-mii-optional-ordinary-fallback/v1").ToArray() }
            } }
        }).ToArray() };
        ScalarControlFlowV2LinkedProgramV1 strictLink = new ScalarControlFlowV2ObjectLinkerV1().Link(strictGraph, strictGraph.Graph!.RootIdentities.Single());
        if (strictLink.Status == ScalarControlFlowV2LinkStatusV1.Success || strictLink.RestrictedImage is not null ||
            !strictLink.Diagnostics.Any(d => d.Message.Contains("Unsupported", StringComparison.Ordinal)))
            throw new Exception("Nested-loop MII shape bypassed explicit ordinary-fallback contract");
        Console.WriteLine($"IDENTITY nested-loop artifact={api.Source.ArtifactIdentity!.Digest}; graph={api.Compilation.Graph!.GraphDigest}; image={budgetLink.RestrictedImage.PackageSha256}; code={budgetLink.MethodObjects.Single().CodeSha256}; allocation={budgetLink.MethodObjects.Single().AllocationWitnessDigest}");
        foreach ((string? fixture, int exit, string? code) in new[] { ("CliLanguageError.txt", 3, "CS9394"),
            ("CliLanguageNoHeap.txt", 3, "HC0004"), ("CliLanguageUnsupported.txt", 4, "HCCIL1001") }) {
            (int Exit, string Output, string Error) denied = Run([.. Args(fixture), "--language", "csharp15"]);
            if (denied.Exit != exit || !denied.Error.Contains(code, StringComparison.Ordinal) || denied.Output.Length != 0 ||
                !imageBytes.SequenceEqual(File.ReadAllBytes(target)) || !emittedPe.SequenceEqual(File.ReadAllBytes(pe)) ||
                !emittedPdb.SequenceEqual(File.ReadAllBytes(pdb)))
                throw new Exception("SDK CLI language negative failed: " + code + "; " + denied.Error);
        }
        if (Run([.. Args("CliLanguageFixture.txt"), "--language", "preview"]).Exit != 2 ||
            Run(selected, new CancellationToken(true)).Exit != 130) throw new Exception("Language option/cancel gate failed");
        Console.WriteLine("PASS SDK CLI original nested-loop CSharp15 source/PE/PDB/graph/image and API exact bytes/repeat oracle; source/NoHeap/IL/language/cancellation negatives");
    }
}
