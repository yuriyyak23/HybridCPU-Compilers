using System.Collections.Immutable;
using HybridCPU.Compiler.Analyzers;
using HybridCPU.Compiler.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

static class Program
{
    public static async Task Main(string[] args)
    {
        try {
            if (args.Length == 1 && args[0] == "--external-nullable-enum") ExternalValueChecks.RunNullableEnum();
            else if (args.Length == 1 && args[0] == "--source-cli") SourceCliChecks.Run();
            else if (args.Length == 1 && args[0] == "--nullable-parameter") NullableInt32Checks.RunParameter();
            else if (args.Length == 1 && args[0] == "--nullable-return") NullableInt32Checks.RunReturn();
            else if (args.Length == 1 && args[0] == "--nullable-binary-return") NullableInt32Checks.RunBinaryReturn();
            else if (args.Length == 1 && args[0] == "--nullable-return-helper") NullableInt32Checks.RunReturnHelper();
            else if (args.Length == 1 && args[0] == "--nullable-map") { NullableInt32Checks.RunMap(); NullableInt32Checks.RunMapMixed(); }
            else if (args.Length == 1 && args[0] == "--nullable-struct-scalar") NullableInt32Checks.RunStructScalarDefault();
            else if (args.Length == 1 && args[0] == "--nullable-struct-scalar-method") NullableInt32Checks.RunStructScalarDefault(genericMethod:true);
            else if (args.Length == 1 && args[0] == "--nullable-struct-scalar-method-i8") NullableInt32Checks.RunStructScalarDefault(genericMethod:true,wideMethod:true);
            else if (args.Length == 1 && args[0] == "--nullable-struct-scalar-binary") {NullableInt32Checks.RunStructScalarDefault(genericMethod:true,binaryMethod:true);NullableInt32Checks.RunStructScalarDefault(genericMethod:true,wideMethod:true,binaryMethod:true);}
            else if (args.Length == 1 && args[0] == "--nullable-struct-scalar-arity") NullableInt32Checks.RunStructScalarArity();
            else if (args.Length == 1 && args[0] == "--nullable-struct-scalar-zero") NullableInt32Checks.RunStructScalarArity(genericZero: true);
            else if (args.Length == 1 && args[0] == "--nullable-generic-default") { NullableInt32Checks.RunGenericDefault(); }
            else if (args.Length == 1 && args[0] == "--nullable-return-composition") { NullableInt32Checks.RunReturnHelper(true); NullableInt32Checks.RunMixedReturn(true); }
            else if (args.Length == 1 && args[0] == "--nullable-mixed-return") NullableInt32Checks.RunMixedReturn();
            else if (args.Length == 1 && args[0] == "--nullable-presence-parameter") NullableInt32Checks.RunParameter(presence:true);
            else if (args.Length == 1 && args[0] == "--nullable-default-parameter") NullableInt32Checks.RunParameter(explicitDefault:-7);
            else if (args.Length == 1 && args[0] == "--nullable-dynamic-default-parameter") NullableInt32Checks.RunParameter(dynamicDefault:true);
            else if (args.Length == 1 && args[0] == "--nullable-computed-default-parameter") NullableInt32Checks.RunComputedParameters();
            else if (args.Length == 1 && args[0] == "--nullable-helper-default-parameter") NullableInt32Checks.RunParameter(computedDefault:"Adjust(fallback)");
            else if (args.Length == 1 && args[0] == "--nullable-external-default") NullableInt32Checks.RunExternalDefault();
            else if (args.Length == 1 && args[0] == "--nullable-external-constant") NullableInt32Checks.RunExternalDefault(constantOnly:true);
            else if (args.Length == 1 && args[0] == "--nullable-lazy-default") NullableInt32Checks.RunExternalDefault(lazyOnly:true);
            else if (args.Length == 1 && args[0] == "--nullable-i8-helper") NullableInt32Checks.RunExternalDefault(lazyOnly:true,wideHelperOnly:true);
            else if (args.Length == 1 && args[0] == "--nullable-affine-helper") NullableInt32Checks.RunExternalDefault(lazyOnly:true,wideHelperOnly:true,onlyHelperKind:23);
            else if (args.Length == 1 && args[0] == "--nullable-scaled-helper") NullableInt32Checks.RunExternalDefault(lazyOnly:true,wideHelperOnly:true,onlyHelperKind:24);
            else if (args.Length == 1 && args[0] == "--nullable-retained") ExternalValueChecks.RunNullableRetained();
            else if (args.Length == 1 && args[0] == "--nullable-local") NullableInt32Checks.Run();
            else if (args.Length == 1 && args[0] == "--value-construction") ValueConstructionChecks.Run();
            else if (args.Length == 0) await Run();
            else throw new ArgumentException("Use no arguments for full smoke, --value-construction or --nullable-local for a bounded consumer.");
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); Environment.ExitCode = 1; }
    }

    private static async Task Run()
    {
        MetadataReference[] references =
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)
        ];
        const string source = """
            using HybridCPU.Compiler.Profiles;
            [HybridCpuStaticContract]
            interface IClock
            {
                void Zed(long payload);
                int Read(int channel);
                byte Alpha();
            }
            class Kernel
            {
                [HybridCpuKernelNoHeap]
                static void Tick() { var values = new int[4]; System.GC.Collect(); }
            }
            """;
        CSharpCompilation compilation = CSharpCompilation.Create("profile-smoke",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp13))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        KernelNoHeapAnalyzer analyzer = new KernelNoHeapAnalyzer();
        ImmutableArray<Diagnostic> diagnostics = await compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(analyzer)).GetAnalyzerDiagnosticsAsync();
        if (!diagnostics.Select(static diagnostic => diagnostic.Id).OrderBy(static id => id)
                .SequenceEqual(new[] { "HC0004", "HC0005" }))
            throw new InvalidOperationException("Kernel.NoHeap analyzer must reject allocation and GC APIs deterministically.");
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new StaticContractGenerator().AsSourceGenerator()], parseOptions: new CSharpParseOptions(LanguageVersion.CSharp13));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation generated, out ImmutableArray<Diagnostic> generationDiagnostics);
        GeneratorDriver repeatedDriver = CSharpGeneratorDriver.Create([new StaticContractGenerator().AsSourceGenerator()], parseOptions: new CSharpParseOptions(LanguageVersion.CSharp13));
        repeatedDriver = repeatedDriver.RunGeneratorsAndUpdateCompilation(compilation, out _, out ImmutableArray<Diagnostic> repeatedDiagnostics);
        string generatedText = driver.GetRunResult().GeneratedTrees.Single().GetText().ToString();
        string repeatedText = repeatedDriver.GetRunResult().GeneratedTrees.Single().GetText().ToString();
        if (generationDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) ||
            repeatedDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) ||
            generated.SyntaxTrees.Count() != compilation.SyntaxTrees.Count() + 1 ||
            !string.Equals(generatedText, repeatedText, StringComparison.Ordinal) ||
            !generatedText.Contains("ContractDigest", StringComparison.Ordinal) ||
            !generatedText.Contains("ChannelManifestRows", StringComparison.Ordinal) ||
            !generatedText.Contains("serializer=unqualified:deserializer=unqualified:dispatch=declaration-only:ownership=unqualified", StringComparison.Ordinal) ||
            !generatedText.Contains("WireIds", StringComparison.Ordinal) ||
            generatedText.IndexOf("::Alpha(", StringComparison.Ordinal) >= generatedText.IndexOf("::Read(", StringComparison.Ordinal) ||
            generatedText.IndexOf("::Read(", StringComparison.Ordinal) >= generatedText.IndexOf("::Zed(", StringComparison.Ordinal))
            throw new InvalidOperationException("Static contract generator must emit one deterministic contract manifest without errors.");
        Console.WriteLine("PASS HybridCPU source profile analyzer and static contract generator");
        using CancellationTokenSource generatorCancellation = new CancellationTokenSource();
        generatorCancellation.Cancel();
        bool generatorCancelled = false;
        GeneratorDriver cancellationDriver = CSharpGeneratorDriver.Create([new StaticContractGenerator().AsSourceGenerator()],
            parseOptions: new CSharpParseOptions(LanguageVersion.CSharp13));
        try { cancellationDriver.RunGenerators(compilation, generatorCancellation.Token); }
        catch (OperationCanceledException) { generatorCancelled = true; }
        if (!generatorCancelled) throw new Exception("Pre-cancelled generator request completed.");
        GeneratorDriver reused = cancellationDriver.RunGenerators(compilation, CancellationToken.None);
        if (reused.GetRunResult().GeneratedTrees.Single().GetText().ToString() != generatedText)
            throw new Exception("Cancelled generator request changed reuse output.");
        Console.WriteLine("PASS generator pre-cancellation and deterministic reuse output");
        CompanionIngressChecks.Run();
        await AllocationIngressChecks.Run();
        ContractIdentityChecks.Run();
        AnalyzerFailureChecks.Run();
        LanguageLaneChecks.Run();
        EnumSwitchChecks.Run();
        HelperCallChecks.Run();
        ExternalEnumChecks.Run();
        SourceCliChecks.Run();
        SourceCliLanguageChecks.Run();
        RecurrenceParallelChecks.Run();
        StaticPropertySourceChecks.Run();
        NullableInt32Checks.Run();
        NullableInt32Checks.RunReturn();
        NullableInt32Checks.RunBinaryReturn();
        NullableInt32Checks.RunMixedReturn();
        NullableInt32Checks.RunReturnHelper();
        NullableInt32Checks.RunMap();
        NullableInt32Checks.RunMapMixed();
        NullableInt32Checks.RunReturnHelper(true);
        NullableInt32Checks.RunMixedReturn(true);
        ValueConstructionChecks.Run();
    }
}
