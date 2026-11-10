using System.Collections.Immutable;
using HybridCPU.Compiler.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

internal static class AllocationIngressChecks
{
    public static async Task Run()
    {
        const string source = """
            using HybridCPU.Compiler.Profiles;
            using System;
            class Fixture {
                [HybridCpuKernelNoHeap] static object Explicit() => new object();
                [HybridCpuKernelNoHeap] static object TargetTyped() => new();
                static object Unprofiled() => new();
                [HybridCpuKernelNoHeap] static int[] Collection() => [1, 2];
                static int[] UnprofiledCollection() => [1, 2];
                static int Helper() => 42;
                int Instance() => 42;
                static int Generic<T>() => 42;
                [HybridCpuKernelNoHeap] static Func<int> MethodGroup() => Helper;
                [HybridCpuKernelNoHeap] Func<int> InstanceGroup() => Instance;
                [HybridCpuKernelNoHeap] static Func<int> GenericGroup() => Generic<int>;
                [HybridCpuKernelNoHeap] static Func<int> CastGroup() => (Func<int>)Helper;
                [HybridCpuKernelNoHeap] static Func<int> ExplicitDelegate() => new Func<int>(Helper);
                [HybridCpuKernelNoHeap] static Func<int> Lambda() => () => 42;
                static Func<int> UnprofiledGroup() => Helper;
                [HybridCpuKernelNoHeap] static int DirectCall() => Helper();
            }
            """;
        CSharpCompilation compilation = CSharpCompilation.Create("allocation-smoke",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp13))],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!, "netstandard.dll"))],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        CompilationWithAnalyzers driver = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new KernelNoHeapAnalyzer()));
        ImmutableArray<Diagnostic> diagnostics = await driver.GetAnalyzerDiagnosticsAsync();
        if (compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new Exception("Allocation fixture has compilation errors.");
        if (diagnostics.Count(d => d.Id == "HC0004") != 8 ||
            diagnostics.Count(d => d.Id == "HC0006") != 1 || diagnostics.Length != 9)
            throw new Exception("NoHeap allocation diagnostics changed or method-group conversion bypassed admission.");
        Console.WriteLine("PASS NoHeap explicit/target-typed/collection/method-group allocation, lambda and unprofiled/direct controls");
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool cancelled = false;
        try { await driver.GetAnalyzerDiagnosticsAsync(cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        if (!cancelled) throw new Exception("Pre-cancelled analyzer request completed.");
        ImmutableArray<Diagnostic> repeated = await driver.GetAnalyzerDiagnosticsAsync(CancellationToken.None);
        string[] Identity(ImmutableArray<Diagnostic> values) => values.Select(d => d.Id + ":" + d.Location.SourceSpan)
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();
        if (!Identity(diagnostics).SequenceEqual(Identity(repeated))) throw new Exception("Cancellation changed analyzer reuse diagnostics.");
        Console.WriteLine("PASS analyzer pre-cancellation and post-cancel diagnostic reuse");
    }
}
