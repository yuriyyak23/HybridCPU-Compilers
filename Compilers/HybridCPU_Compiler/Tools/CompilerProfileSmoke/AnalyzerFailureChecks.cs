using System.Collections.Immutable;
using System.Reflection;
using HybridCPU.Compiler.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

internal static class AnalyzerFailureChecks
{
    public static void Run()
    {
        CSharpCompilation compilation = CSharpCompilation.Create("fault", [CSharpSyntaxTree.ParseText("class Fixture {}")]);
        MethodInfo helper = typeof(ManagedSourceCompanionV1).GetMethod("AnalyzeAdmission", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool failed = false;
        try { helper.Invoke(null, [compilation, ImmutableArray.Create<DiagnosticAnalyzer>(new ThrowingAnalyzer()), CancellationToken.None]); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException failure &&
            failure.InnerException is InvalidOperationException { Message: "controlled-analyzer-fault" }) { failed = true; }
        if (!failed) throw new Exception("Analyzer failure admitted by companion helper.");
        Console.WriteLine("PASS controlled analyzer fault fails actual companion admission helper");
    }

    private sealed class ThrowingAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
            new DiagnosticDescriptor("TEST0001", "fault", "fault", "Test", DiagnosticSeverity.Error, true));
        public override void Initialize(AnalysisContext context) => throw new InvalidOperationException("controlled-analyzer-fault");
    }
}
