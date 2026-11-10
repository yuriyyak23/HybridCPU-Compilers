using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

internal static class EnumSwitchChecks
{
    public static void Run()
    {
        ReadOnlyMemory<byte>[] references = [File.ReadAllBytes(typeof(object).Assembly.Location),
            File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
            File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "netstandard.dll")),
            File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        const string source = """
            using HybridCPU.Compiler.Profiles;
            public enum Choice : int { First = 0, Second = 1, Third = 2 }
            public static class Fixture {
                [HybridCpuKernelNoHeap] public static int Read(Choice value) => value switch {
                    Choice.First => 11, Choice.Second => 22, Choice.Third => 33, _ => -7
                };
            }
            """;
        CompanionSourceRequestV1 request = new CompanionSourceRequestV1("EnumSwitch", [new("/src/EnumSwitch.cs", source)], references);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1("Fixture", "Read");
        ManagedSourceCompanionResultV1 Compile(string text) => ManagedSourceCompanionV1.Compile(
            request with { Documents = [new("/src/EnumSwitch.cs", text)] }, importer, selector);
        ManagedSourceCompanionResultV1 current = Compile(source);
        ManagedSourceCompanionResultV1 repeat = Compile(source);
        if (current.Imported?.Status != RestrictedCilImportStatusV1.Success || current.SourceMap is null ||
            current.SourceMap.Rows.All(r => r.Document is null) || current.SourceMap.Digest != repeat.SourceMap?.Digest ||
            !current.Pe!.SequenceEqual(repeat.Pe!) || !current.Pdb!.SequenceEqual(repeat.Pdb!) ||
            current.SourceInputDigest != repeat.SourceInputDigest || current.ArtifactIdentity?.Digest != repeat.ArtifactIdentity?.Digest)
            throw new Exception("Enum switch companion ingress failed: " + string.Join(';', current.Diagnostics) + "; " +
                string.Join(';', current.Imported?.Diagnostics ?? []));

        const string oldBody = """
            { if (value == Choice.First) return 11; if (value == Choice.Second) return 22;
              if (value == Choice.Third) return 33; return -7; }
            """;
        string oracleSource = source[..source.IndexOf("=> value switch", StringComparison.Ordinal)] + oldBody + " }";
        CSharpCompilation oracleCompilation = CSharpCompilation.Create(request.AssemblyName,
            [CSharpSyntaxTree.ParseText(SourceText.From(oracleSource, Encoding.UTF8), new CSharpParseOptions(LanguageVersion.CSharp13), "/src/EnumSwitch.cs")],
            references.Select(r => MetadataReference.CreateFromImage(r.ToArray())),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, deterministic: true));
        using MemoryStream pe = new MemoryStream(); using MemoryStream pdb = new MemoryStream();
        EmitResult emit = oracleCompilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb,
            pdbFilePath: "/symbols/companion.pdb"));
        if (!emit.Success) throw new Exception(string.Join(';', emit.Diagnostics));
        byte[] oraclePe = pe.ToArray();
        if (importer.ImportImage(oraclePe, selector).Status != RestrictedCilImportStatusV1.Success)
            throw new Exception("Independent enum if/else oracle import failed");

        // Host CLR evaluates the independently emitted fixtures; this is not guest execution.
        int[] values = [int.MinValue, -1, 0, 1, 2, 3, int.MaxValue];
        int[] Evaluate(byte[] image) {
            AssemblyLoadContext context = new AssemblyLoadContext("enum-switch-oracle", isCollectible: true);
            try {
                using MemoryStream stream = new MemoryStream(image);
                Assembly assembly = context.LoadFromStream(stream);
                MethodInfo method = assembly.GetType("Fixture")!.GetMethod("Read")!;
                Type enumType = assembly.GetType("Choice")!;
                return values.Select(value => (int)method.Invoke(null, [Enum.ToObject(enumType, value)])!).ToArray();
            }
            finally { context.Unload(); }
        }
        int[] expected = [-7, -7, 11, 22, 33, -7, -7];
        if (!Evaluate(current.Pe!).SequenceEqual(expected) || !Evaluate(oraclePe).SequenceEqual(expected))
            throw new Exception("Enum switch/if-else emitted-CIL semantic oracle diverged");

        ScalarControlFlowV2LinkedProgramV1 Link(byte[] image) {
            ManagedCallGraphCompilationV1 graph = importer.ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
                image, "enum-switch-source", [selector], []));
            if (graph.Status != RestrictedCilImportStatusV1.Success) throw new Exception(string.Join(';', graph.Diagnostics));
            ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(graph, graph.Graph!.RootIdentities.Single());
            if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success || linked.RestrictedImage is null || linked.MethodObjects.Count != 1)
                throw new Exception("Enum switch backend/object/link/image failed: " + string.Join(';', linked.Diagnostics));
            return linked;
        }
        ScalarControlFlowV2LinkedProgramV1 linked = Link(current.Pe!); ScalarControlFlowV2LinkedProgramV1 linkedRepeat = Link(repeat.Pe!); ScalarControlFlowV2LinkedProgramV1 oracleLinked = Link(oraclePe);
        if (!linked.RestrictedImage!.PackageBytes.SequenceEqual(linkedRepeat.RestrictedImage!.PackageBytes) ||
            linked.MethodObjects.Single().CodeSha256 != linkedRepeat.MethodObjects.Single().CodeSha256 ||
            linked.MethodObjects.Single().AllocationWitnessDigest != linkedRepeat.MethodObjects.Single().AllocationWitnessDigest)
            throw new Exception("Enum source backend bytes/allocation witness repeat failed");

        void Denied(string text, string id) {
            ManagedSourceCompanionResultV1 denied = Compile(text);
            if (denied.Pe is not null || denied.Pdb is not null || denied.Imported is not null || denied.SourceMap is not null ||
                denied.ArtifactIdentity is not null || !denied.Diagnostics.Any(d => d.Id == id && d.Severity == "Error"))
                throw new Exception("Enum switch pre-emit denial failed: " + id + "; " + string.Join(';', denied.Diagnostics));
        }
        Denied(source.Replace("_ => -7", "_ => missing"), "CS0103");
        Denied(source.Replace("_ => -7", "_ => new int[1].Length"), "HC0004");
        ManagedSourceCompanionResultV1 unsupported = Compile(source.Replace("_ => -7", "_ => (int)((double)value + 0.5)"));
        if (unsupported.Pe is null || unsupported.Imported is null || unsupported.Imported.Program is not null || unsupported.SourceMap is not null ||
            unsupported.Imported.Status == RestrictedCilImportStatusV1.Success || !unsupported.Imported.Diagnostics.Any(d => d.Code == "HCCIL1001"))
            throw new Exception("Enum switch unsupported emitted IL did not fail closed");
        ManagedSourceCompanionResultV1 changed = Compile(source.Replace("Choice.Third => 33", "Choice.Third => 34"));
        if (changed.Imported?.Status != RestrictedCilImportStatusV1.Success || changed.SourceInputDigest == current.SourceInputDigest ||
            changed.ArtifactIdentity?.Digest == current.ArtifactIdentity?.Digest || Evaluate(changed.Pe!)[4] != 34)
            throw new Exception("Enum switch changed-arm divergence failed");
        bool cancelled = false;
        try { ManagedSourceCompanionV1.Compile(request, importer, selector, new CancellationToken(true)); }
        catch (OperationCanceledException) { cancelled = true; }
        if (!cancelled || !Compile(source).Pe!.SequenceEqual(current.Pe!)) throw new Exception("Enum source cancellation/reuse failed");
        Console.WriteLine("PASS NoHeap enum switch source/PE/PDB/map repeat; independent CSharp13 if/else semantic oracle and source/NoHeap/unsupported-IL/cancel/reuse/divergence controls");
        Console.WriteLine("PASS enum source and independent oracle -> existing BodyWorld/shared backend/object/link/image; repeated image/code/witness equality (no guest execution)");
        Console.WriteLine($"IDENTITY enum input={current.SourceInputDigest}; artifact={current.ArtifactIdentity!.Digest}; map={current.SourceMap.Digest}; code={linked.MethodObjects.Single().CodeSha256}; imageBytes={linked.RestrictedImage.PackageBytes.Length}; oracleCode={oracleLinked.MethodObjects.Single().CodeSha256}");
        string partialSource = source.Replace(", _ => -7", "");
        Denied(partialSource, "HC0004");
        Denied(source.Replace("_ => -7", "_ when value == Choice.First => -7"), "HC0004");
        Denied("#pragma warning disable HC0004\n" + partialSource, "HC0004");
        ManagedSourceCompanionResultV1 unprofiled = Compile(partialSource.Replace("[HybridCpuKernelNoHeap]", ""));
        if (unprofiled.Pe is null || unprofiled.Diagnostics.Any(d => d.Id == "HC0004"))
            throw new Exception("Unprofiled nonexhaustive switch source behavior changed");
        ManagedSourceCompanionResultV1 boolean = Compile(source.Replace("Read(Choice value)", "Read(bool value)")
            .Replace("Choice.First => 11, Choice.Second => 22, Choice.Third => 33, _ => -7", "true => 11, false => 22"));
        if (boolean.Imported?.Status != RestrictedCilImportStatusV1.Success || boolean.Diagnostics.Any(d => d.Id == "HC0004"))
            throw new Exception("Exhaustive bool switch without discard was rejected");
        Console.WriteLine("PASS nonexhaustive/guarded/suppressed enum switch HC0004 before emit; exhaustive bool and unprofiled controls; admission v7");
    }
}
