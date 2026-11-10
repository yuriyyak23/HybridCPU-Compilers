using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridCPU.Compiler.Cil;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace HybridCPU.Compiler.Roslyn;

public sealed record CompanionSourceDocumentV1(string VirtualPath, string Text);
public enum CompanionSourceLanguageV1 : byte { CSharp13 = 0, CSharp15 = 1 }

/// <summary>Caller keeps lists/reference buffers stable until entry snapshot completes. No filesystem reference discovery.</summary>
public sealed record CompanionSourceRequestV1(
    string AssemblyName, IReadOnlyList<CompanionSourceDocumentV1> Documents,
    IReadOnlyList<ReadOnlyMemory<byte>> ReferenceImages)
{
    public CompanionSourceLanguageV1 Language { get; init; } = CompanionSourceLanguageV1.CSharp13;
}

public static partial class ManagedSourceCompanionV1
{
    public static ManagedSourceCompanionResultV1 Compile(
        CompanionSourceRequestV1 request, RestrictedCilImporterV1 importer,
        RestrictedCilMethodSelectorV1 selector, CancellationToken cancellationToken = default)
    {
        SourceSnapshot input = SnapshotSource(request, cancellationToken);
        return Compile(input.Compilation, importer, selector, cancellationToken) with { SourceInputDigest = input.Digest };
    }

    private sealed record SourceSnapshot(CSharpCompilation Compilation, string Digest, byte[][] Images);

    private static SourceSnapshot SnapshotSource(CompanionSourceRequestV1 request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        LanguageVersion language = ResolveSourceLanguage(request.Language);
        if (string.IsNullOrWhiteSpace(request.AssemblyName) || request.Documents is null || request.ReferenceImages is null)
            throw new ArgumentException("Explicit assembly, source and reference closure required.", nameof(request));
        CompanionSourceDocumentV1[] documents = request.Documents.ToArray();
        if (documents.Any(d => d is null || string.IsNullOrWhiteSpace(d.VirtualPath) || d.Text is null) ||
            documents.Select(d => d.VirtualPath).Distinct(StringComparer.Ordinal).Count() != documents.Length)
            throw new ArgumentException("Unique virtual source paths and source text required.", nameof(request));
        List<byte[]> images = new List<byte[]>();
        foreach (ReadOnlyMemory<byte> image in request.ReferenceImages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            images.Add(image.ToArray());
        }
        string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        // Order is explicit: reference/source order can influence diagnostics and binding.
        var identity = new
        {
            Schema = request.Language == CompanionSourceLanguageV1.CSharp13
                ? "hybridcpu.companion-source-input/v2" : "hybridcpu.companion-source-input/v3-csharp15", request.AssemblyName,
            SourceAdmissionPolicy,
            RoslynVersion = typeof(CSharpCompilation).Assembly.GetName().Version!.ToString(),
            CompilerVersion = HybridCPU.Compiler.Core.IR.HybridCpuCompilerContract.Version,
            Policy = $"{language};UTF8;DLL;Release;deterministic;AnyCpu;unsafe-off;nullable-default;pdb=/symbols/companion.pdb",
            Documents = documents.Select(d => new { d.VirtualPath, Sha256 = Hash(Encoding.UTF8.GetBytes(d.Text)) }).ToArray(),
            References = images.Select(Hash).ToArray()
        };
        string digest = Hash(JsonSerializer.SerializeToUtf8Bytes(identity));
        if (request.Language == CompanionSourceLanguageV1.CSharp15)
        {
            string sdkHost = typeof(ManagedSourceCompanionV1).Assembly.GetCustomAttributes(
                typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "CompanionSdkRoslynHost").Value!;
            digest = Hash(JsonSerializer.SerializeToUtf8Bytes(new {
                Schema = "hybridcpu.companion-csharp15-host-input/v1", SourceDigest = digest, SdkHost = sdkHost
            }));
        }
        SyntaxTree[] trees = documents.Select(d => CSharpSyntaxTree.ParseText(SourceText.From(d.Text, Encoding.UTF8),
            new CSharpParseOptions(language), d.VirtualPath, cancellationToken: cancellationToken)).ToArray();
        PortableExecutableReference[] references = images.Select(bytes => MetadataReference.CreateFromImage(ImmutableArray.CreateRange(bytes))).ToArray();
        CSharpCompilation compilation = CSharpCompilation.Create(request.AssemblyName, trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true,
                optimizationLevel: OptimizationLevel.Release));
        return new(compilation, digest, images.ToArray());
    }

    private static LanguageVersion ResolveSourceLanguage(CompanionSourceLanguageV1 language)
    {
        if (language == CompanionSourceLanguageV1.CSharp13) return LanguageVersion.CSharp13;
        if (language != CompanionSourceLanguageV1.CSharp15)
            throw new ArgumentOutOfRangeException(nameof(language));
        bool sdkHost = typeof(ManagedSourceCompanionV1).Assembly.GetCustomAttributes(
            typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Any(a => a.Key == "CompanionSdkRoslynHost" && !string.IsNullOrWhiteSpace(a.Value));
        if (!sdkHost || !Enum.TryParse<LanguageVersion>("CSharp15", out LanguageVersion version))
            throw new NotSupportedException("CSharp15 requires the explicit build-derived SDK Roslyn companion host.");
        return version;
    }
}
