using System.Text;
using System.Text.Json;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;

namespace HybridCPU.Compiler.Roslyn;

/// <summary>Explicit file ingress into owned source and existing graph/link stages. No publish authority.</summary>
public static class ManagedSourceCommandLineV1
{
    private static int Main(string[] args)
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try { return Run(args, Console.Out, Console.Error, cancellation.Token); }
        finally { Console.CancelKeyPress -= handler; }
    }

    public static int Run(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (args.Length == 0 || args[0] != "compile-source-image" || (args.Length - 1) % 2 != 0)
                return Usage(error);
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            List<string> sources = new List<string>();
            List<(string Path, string Role)> references = new List<(string Path, string Role)>();
            List<RestrictedCilGenericArgumentV1> methodArguments = new List<RestrictedCilGenericArgumentV1>();
            List<RestrictedCilGenericArgumentV1> typeArguments = new List<RestrictedCilGenericArgumentV1>();
            for (int index = 1; index < args.Length; index += 2)
            {
                string key = args[index], value = args[index + 1];
                if (string.IsNullOrWhiteSpace(value)) return Usage(error);
                if (key == "--source") sources.Add(value);
                else if (key is "--method-argument" or "--type-argument")
                {
                    List<RestrictedCilGenericArgumentV1> arguments = key == "--method-argument" ? methodArguments : typeArguments;
                    if (arguments.Count >= 8) return Usage(error);
                    arguments.Add(value switch {
                        "int32" => new("System.Int32", RestrictedCilTypeV1.Int32),
                        "int64" => new("System.Int64", RestrictedCilTypeV1.Int64),
                        _ => throw new ArgumentException("Generic argument must be explicit int32 or int64.")
                    });
                }
                else if (key is "--reference" or "--body-reference" or "--metadata-reference") references.Add((value, key));
                else if (key is "--assembly-name" or "--type" or "--method" or "--out" or "--emit-pe" or "--emit-pdb" or "--emit-map" or "--language")
                { if (!values.TryAdd(key, value)) return Usage(error); }
                else return Usage(error);
            }
            if (sources.Count == 0 || sources.Count > 64 || references.Count == 0 || references.Count > 64 ||
                new[] { "--assembly-name", "--type", "--method", "--out" }.Any(key => !values.ContainsKey(key)))
                return Usage(error);
            CompanionSourceLanguageV1 language = values.GetValueOrDefault("--language", "csharp13") switch {
                "csharp13" => CompanionSourceLanguageV1.CSharp13,
                "csharp15" => CompanionSourceLanguageV1.CSharp15,
                _ => throw new ArgumentException("Explicit source language must be csharp13 or csharp15.")
            };
            string[] inputPaths = sources.Concat(references.Select(r => r.Path)).Select(Path.GetFullPath).ToArray();
            string outputPath = Path.GetFullPath(values["--out"]);
            string[] outputPaths = new[] { outputPath }.Concat(new[] { "--emit-pe", "--emit-pdb", "--emit-map" }
                .Where(values.ContainsKey).Select(key => Path.GetFullPath(values[key]))).ToArray();
            if (inputPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != inputPaths.Length ||
                outputPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputPaths.Length ||
                outputPaths.Any(path => inputPaths.Contains(path, StringComparer.OrdinalIgnoreCase)))
                throw new ArgumentException("Unique source/reference paths and a distinct output path required.");
            byte[] ReadBounded(string path, int limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using FileStream stream = File.OpenRead(path);
                if (stream.Length > limit) throw new ArgumentException("Input file exceeds source CLI byte budget: " + path);
                byte[] bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
                cancellationToken.ThrowIfCancellationRequested();
                return bytes;
            }
            long total = 0;
            CompanionSourceDocumentV1[] documents = sources.Select(path => {
                byte[] bytes = ReadBounded(path, 4 * 1024 * 1024);
                total += bytes.Length;
                if (total > 16 * 1024 * 1024) throw new ArgumentException("Source closure exceeds 16 MiB.");
                string text = new UTF8Encoding(false, true).GetString(bytes);
                if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
                return new CompanionSourceDocumentV1(Path.GetFullPath(path).Replace('\\', '/'), text);
            }).ToArray();
            total = 0;
            ReadOnlyMemory<byte>[] images = references.Select(reference => {
                byte[] bytes = ReadBounded(reference.Path, 32 * 1024 * 1024);
                total += bytes.Length;
                if (total > 64 * 1024 * 1024) throw new ArgumentException("Reference closure exceeds 64 MiB.");
                return (ReadOnlyMemory<byte>)bytes;
            }).ToArray();
            CompanionSourceRequestV1 request = new CompanionSourceRequestV1(values["--assembly-name"], documents, images) { Language = language };
            ManagedSourceBodyWorldResultV1 imported = ManagedSourceCompanionV1.CompileBodyWorld(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2),
                [new(values["--type"], values["--method"], GenericTypeArguments: typeArguments.ToArray(),
                    GenericMethodArguments: methodArguments.ToArray())],
                references.Select((r, i) => (r, i)).Where(row => row.r.Role == "--body-reference").Select(row => row.i).ToArray(),
                references.Select((r, i) => (r, i)).Where(row => row.r.Role == "--metadata-reference").Select(row => row.i).ToArray(),
                cancellationToken);
            foreach (CompanionDiagnosticV1 diagnostic in imported.Source.Diagnostics)
                error.WriteLine($"{diagnostic.Id}: {diagnostic.Severity}: {diagnostic.Document}:{diagnostic.Start}: {diagnostic.Message}");
            if (imported.Source.Pe is null) return 3;
            if (imported.Compilation?.Status != RestrictedCilImportStatusV1.Success)
            {
                foreach (IrFrontendDiagnosticV1 diagnostic in imported.Compilation?.Diagnostics ?? []) error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
                return 4;
            }
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(cancellationToken, imported.Compilation,
                imported.Compilation.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || linked.RestrictedImage is null)
            {
                foreach (ScalarControlFlowV2LinkDiagnosticV1 diagnostic in linked.Diagnostics) error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
                return 5;
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Existing synchronous file boundary; no atomic-output or crash-recovery claim.
            byte[]? mapBytes = values.ContainsKey("--emit-map") ? JsonSerializer.SerializeToUtf8Bytes(
                imported.SourceMaps.OrderBy(row => row.Key, StringComparer.Ordinal)
                    .ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal)) : null;
            cancellationToken.ThrowIfCancellationRequested();
            if (values.TryGetValue("--emit-pe", out string? pePath)) File.WriteAllBytes(pePath, imported.Source.Pe);
            if (values.TryGetValue("--emit-pdb", out string? pdbPath)) File.WriteAllBytes(pdbPath, imported.Source.Pdb!);
            if (values.TryGetValue("--emit-map", out string? mapPath)) File.WriteAllBytes(mapPath, mapBytes!);
            File.WriteAllBytes(outputPath, linked.RestrictedImage.PackageBytes);
            output.WriteLine($"source={imported.Source.SourceInputDigest}; artifact={imported.Source.ArtifactIdentity!.Digest}; graph={imported.Compilation.Graph.GraphDigest}; image={linked.RestrictedImage.PackageSha256}; Qualification: no");
            return 0;
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken == cancellationToken)
        { error.WriteLine("HCSOURCE2003: compilation cancelled before output write."); return 130; }
        catch (NotSupportedException exception)
        { error.WriteLine("HCSOURCE1001: " + exception.Message); return 2; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or BadImageFormatException)
        { error.WriteLine("HCSOURCE0002: " + exception.Message); return 2; }
    }

    private static int Usage(TextWriter error)
    {
        error.WriteLine("HCSOURCE0001: compile-source-image --source UTF8.cs --reference PE [--body-reference PE|--metadata-reference PE] --assembly-name A --type T --method M [--type-argument int32|int64 --method-argument int32|int64] --out IMAGE [--emit-pe PE --emit-pdb PDB --emit-map JSON --language csharp13|csharp15]; repeat source/reference/generic-argument options; csharp13 default, csharp15 requires SDK host opt-in.");
        return 2;
    }
}
