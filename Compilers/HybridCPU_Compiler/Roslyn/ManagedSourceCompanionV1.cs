using HybridCPU.Compiler.Cil;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using HybridCPU.Compiler.Analyzers;
using System.Collections.Concurrent;

namespace HybridCPU.Compiler.Roslyn;

public sealed record CompanionDiagnosticV1(string Id, string Severity, string Message, string? Document, int Start, int Length);

/// <summary>Owned emitted bytes and existing ingress result; no execution/publication authority.</summary>
public sealed record ManagedSourceCompanionResultV1(
    IReadOnlyList<CompanionDiagnosticV1> Diagnostics, byte[]? Pe, byte[]? Pdb,
    RestrictedCilImportResultV1? Imported, ManagedSourceMapV1? SourceMap)
{
    public CompanionArtifactIdentityV1? ArtifactIdentity { get; init; }
    /// <summary>Available only for the explicit owned source/reference request lane; not an importer cache key.</summary>
    public string? SourceInputDigest { get; init; }
}

/// <summary>Explicit Roslyn boundary. Caller owns immutable compilation/reference closure and serializes importer access.</summary>
public static partial class ManagedSourceCompanionV1
{
    public const string SourceAdmissionPolicy = "companion-noheap-admission/v14";
    public static ManagedSourceCompanionResultV1 Compile(
        CSharpCompilation compilation, RestrictedCilImporterV1 importer,
        RestrictedCilMethodSelectorV1 selector, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(selector);
        ManagedSourceCompanionResultV1 source = AdmitAndEmit(compilation, cancellationToken);
        if (source.Pe is null) return source;
        RestrictedCilImportResultV1 imported = importer.ImportImage(source.Pe, selector, cancellationToken, "roslyn-companion/v1");
        cancellationToken.ThrowIfCancellationRequested();
        ManagedSourceMapV1? map = imported.Status == RestrictedCilImportStatusV1.Success ? ManagedSourceMapV1.Create(imported, source.Pe, source.Pdb!) : null;
        cancellationToken.ThrowIfCancellationRequested();
        return source with { Imported = imported, SourceMap = map };
    }

    private static ManagedSourceCompanionResultV1 AdmitAndEmit(CSharpCompilation compilation, CancellationToken cancellationToken,
        Func<IMethodSymbol, bool>? ownedConstructorAdmission = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        cancellationToken.ThrowIfCancellationRequested();
        LanguageVersion[] languages = compilation.SyntaxTrees.Select(t => t.Options is CSharpParseOptions options
            ? options.LanguageVersion : LanguageVersion.Default).Distinct().ToArray();
        if (languages.Length > 1 || languages.Any(language => !IsAdmittedSourceLanguage(language)))
            throw new ArgumentException("Companion requires one explicit admitted source language.", nameof(compilation));
        compilation = compilation.WithOptions(compilation.Options.WithDeterministic(true)
            .WithOutputKind(OutputKind.DynamicallyLinkedLibrary).WithOptimizationLevel(OptimizationLevel.Release));
        ImmutableArray<Diagnostic> sourceDiagnostics = compilation.GetDiagnostics(cancellationToken);
        if (sourceDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return new(Project(sourceDiagnostics), null, null, null, null);
        ImmutableArray<Diagnostic> profileDiagnostics = AnalyzeAdmission(compilation,
            ImmutableArray.Create<DiagnosticAnalyzer>(ownedConstructorAdmission is null ? new KernelNoHeapAnalyzer() :
                new KernelNoHeapAnalyzer(ownedConstructorAdmission)), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (profileDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error || d.Id == "AD0001"))
            return new(Project(sourceDiagnostics.Concat(profileDiagnostics)), null, null, null, null);
        using MemoryStream pe = new MemoryStream();
        using MemoryStream pdb = new MemoryStream();
        EmitResult emitted = compilation.Emit(pe, pdb, options: new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: "/symbols/companion.pdb"),
            cancellationToken: cancellationToken);
        IReadOnlyList<CompanionDiagnosticV1> diagnostics = Project(emitted.Diagnostics.Concat(profileDiagnostics));
        cancellationToken.ThrowIfCancellationRequested();
        if (!emitted.Success) return new(diagnostics, null, null, null, null);
        byte[] peBytes = pe.ToArray(), pdbBytes = pdb.ToArray();
        return new(diagnostics, peBytes, pdbBytes, null, null)
        { ArtifactIdentity = CompanionArtifactIdentityV1.Create(peBytes, pdbBytes) };
    }

    private static bool IsAdmittedSourceLanguage(LanguageVersion language)
    {
        if (language == LanguageVersion.CSharp13) return true;
        try { return language == ResolveSourceLanguage(CompanionSourceLanguageV1.CSharp15); }
        catch (NotSupportedException) { return false; }
    }

    private static ImmutableArray<Diagnostic> AnalyzeAdmission(CSharpCompilation compilation,
        ImmutableArray<DiagnosticAnalyzer> analyzers, CancellationToken cancellationToken)
    {
        // Admission rules cannot be disabled by caller diagnostic preferences or source pragmas.
        ImmutableDictionary<string, ReportDiagnostic> admissionOptions = compilation.Options.SpecificDiagnosticOptions
            .SetItem("HC0004", ReportDiagnostic.Error).SetItem("HC0005", ReportDiagnostic.Error).SetItem("HC0006", ReportDiagnostic.Error);
        CSharpCompilation admissionCompilation = compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions(admissionOptions));
        ConcurrentQueue<Exception> faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        CompilationWithAnalyzersOptions analyzerOptions = new CompilationWithAnalyzersOptions(new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty),
            onAnalyzerException: (exception, analyzer, diagnostic) => faults.Enqueue(exception),
            concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: true);
        ImmutableArray<Diagnostic> result = admissionCompilation.WithAnalyzers(analyzers, analyzerOptions)
            .GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        if (faults.TryPeek(out Exception? fault))
            throw new InvalidOperationException("Companion profile admission analyzer failed.", fault);
        return result;
    }

    private static IReadOnlyList<CompanionDiagnosticV1> Project(IEnumerable<Diagnostic> values) => Array.AsReadOnly(
        values.OrderBy(d => d.Location.SourceTree?.FilePath, StringComparer.Ordinal)
            .ThenBy(d => d.Location.SourceSpan.Start).ThenBy(d => d.Id, StringComparer.Ordinal)
            .Select(d => new CompanionDiagnosticV1(d.Id, d.Severity.ToString(),
                d.GetMessage(System.Globalization.CultureInfo.InvariantCulture), d.Location.SourceTree?.FilePath,
                d.Location.SourceSpan.Start, d.Location.SourceSpan.Length)).ToArray());
}
