using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class LanguageLaneChecks
{
    public static void Run()
    {
        byte[] reference = File.ReadAllBytes(typeof(object).Assembly.Location);
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        RestrictedCilMethodSelectorV1 selector = new RestrictedCilMethodSelectorV1("Fixture", "Read");
        CompanionSourceRequestV1 request = new CompanionSourceRequestV1("LanguageLane", [new("/src/Fixture.cs",
            "public static class Fixture { public static int Read() => 42; }")], [reference]);
        ManagedSourceCompanionResultV1 legacy = ManagedSourceCompanionV1.Compile(request, importer, selector);
        if (legacy.Imported?.Status != RestrictedCilImportStatusV1.Success) throw new Exception("CSharp13 lane rejected");
        bool sdkHost = typeof(ManagedSourceCompanionV1).Assembly.GetCustomAttributes(
            typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Any(a => a.Key == "CompanionSdkRoslynHost");
        Console.WriteLine("IDENTITY Roslyn=" + typeof(CSharpCompilation).Assembly.GetName().Version + "; SDK-host=" + sdkHost);
        if (!sdkHost)
        {
            bool rejected = false;
            try { ManagedSourceCompanionV1.Compile(request with { Language = CompanionSourceLanguageV1.CSharp15 }, importer, selector); }
            catch (NotSupportedException) { rejected = true; }
            if (!rejected) throw new Exception("Default host admitted CSharp15");
            Console.WriteLine("PASS default CSharp13 lane; CSharp15 fails closed without SDK host opt-in");
            return;
        }
        ManagedSourceCompanionResultV1 current = ManagedSourceCompanionV1.Compile(request with { Language = CompanionSourceLanguageV1.CSharp15 }, importer, selector);
        if (current.Imported?.Status != RestrictedCilImportStatusV1.Success || legacy.SourceInputDigest == current.SourceInputDigest)
            throw new Exception("SDK language lane identity/admission mismatch");
        CompanionSourceRequestV1 labels = request with { Language = CompanionSourceLanguageV1.CSharp15, Documents = [new("/src/Fixture.cs",
            "public static class Fixture { public static int Read() { outer: for(int i=0;i<2;i++) { for(int j=0;j<2;j++) { break outer; } } return 42; } }")] };
        ManagedSourceCompanionResultV1 labelResult = ManagedSourceCompanionV1.Compile(labels, importer, selector);
        ManagedSourceCompanionResultV1 repeated = ManagedSourceCompanionV1.Compile(labels, importer, selector);
        if (labelResult.Imported?.Status != RestrictedCilImportStatusV1.Success || labelResult.Pe is null ||
            !labelResult.Pe.SequenceEqual(repeated.Pe!) || !labelResult.Pdb!.SequenceEqual(repeated.Pdb!))
            throw new Exception("SDK labeled-break consumer failed deterministic emit/import");
        ManagedSourceCompanionResultV1 labelRejected = ManagedSourceCompanionV1.Compile(labels with { Language = CompanionSourceLanguageV1.CSharp13 }, importer, selector);
        if (labelRejected.Pe is not null || !labelRejected.Diagnostics.Any(d => d.Severity == "Error"))
            throw new Exception("CSharp13 admitted CSharp15 labeled break");
        Console.WriteLine("PASS SDK CSharp13/CSharp15 identity separation, labeled-break consumer emit/import/repeat and old-lane rejection");
        ContinueConsumer(importer, selector);
    }

    private static void ContinueConsumer(RestrictedCilImporterV1 importer, RestrictedCilMethodSelectorV1 selector)
    {
        ReadOnlyMemory<byte>[] references = [File.ReadAllBytes(typeof(object).Assembly.Location),
            File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
            File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "netstandard.dll")),
            File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location)];
        const string source = """
            using HybridCPU.Compiler.Profiles;
            public static class Fixture {
                [HybridCpuKernelNoHeap] public static int Read() {
                    int sum = 0;
                    outer: for (int i = 0; i < 4; i++) {
                        for (int j = 0; j < 3; j++) {
                            sum += i + j;
                            if (j == 1) continue outer;
                        }
                        sum += 100;
                    }
                    return sum;
                }
            }
            """;
        CompanionSourceRequestV1 request = new CompanionSourceRequestV1("ContinueConsumer", [new("/src/Continue.cs", source)], references)
            { Language = CompanionSourceLanguageV1.CSharp15 };
        ManagedSourceCompanionResultV1 Compile(string text) => ManagedSourceCompanionV1.Compile(
            request with { Documents = [new("/src/Continue.cs", text)] }, importer, selector);
        ManagedSourceCompanionResultV1 result = Compile(source);
        ManagedSourceCompanionResultV1 repeat = Compile(source);
        if (result.Imported?.Status != RestrictedCilImportStatusV1.Success || result.SourceMap is null ||
            !result.SourceMap.Rows.Any(r => r.Document is not null) || result.SourceMap.Digest != repeat.SourceMap?.Digest ||
            !result.Pe!.SequenceEqual(repeat.Pe!) || !result.Pdb!.SequenceEqual(repeat.Pdb!) ||
            result.SourceInputDigest != repeat.SourceInputDigest || result.ArtifactIdentity?.Digest != repeat.ArtifactIdentity?.Digest)
            throw new Exception("Labeled continue deterministic source/PE/PDB/import/map failed: " + string.Join(';', result.Diagnostics));

        // Independent CSharp13 emitter: the label sits immediately before the outer for increment.
        string oldSource = source.Replace("outer: for", "for").Replace("continue outer;", "goto next;")
            .Replace("sum += 100;", "sum += 100; next: ;");
        CSharpCompilation oracle = CSharpCompilation.Create(request.AssemblyName,
            [CSharpSyntaxTree.ParseText(SourceText.From(oldSource, Encoding.UTF8), new CSharpParseOptions(LanguageVersion.CSharp13), "/src/Continue.cs")],
            references.Select(r => MetadataReference.CreateFromImage(r.ToArray())),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true, optimizationLevel: OptimizationLevel.Release));
        using MemoryStream pe = new MemoryStream();
        using MemoryStream pdb = new MemoryStream();
        EmitResult emitted = oracle.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb,
            pdbFilePath: "/symbols/companion.pdb"));
        if (!emitted.Success) throw new Exception(string.Join(';', emitted.Diagnostics));
        byte[] oraclePe = pe.ToArray();
        byte[] Body(byte[] bytes) {
            using PEReader reader = new PEReader(new MemoryStream(bytes));
            MetadataReader metadata = reader.GetMetadataReader();
            MethodDefinition method = metadata.MethodDefinitions.Select(metadata.GetMethodDefinition)
                .Single(m => metadata.GetString(m.Name) == "Read");
            return reader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
        }
        byte[] Locals(byte[] bytes) {
            using PEReader reader = new PEReader(new MemoryStream(bytes));
            MetadataReader metadata = reader.GetMetadataReader();
            MethodDefinition method = metadata.MethodDefinitions.Select(metadata.GetMethodDefinition)
                .Single(m => metadata.GetString(m.Name) == "Read");
            StandaloneSignatureHandle handle = reader.GetMethodBody(method.RelativeVirtualAddress).LocalSignature;
            return metadata.GetBlobBytes(metadata.GetStandaloneSignature(handle).Signature);
        }
        RestrictedCilImportResultV1 imported = importer.ImportImage(oraclePe, selector, sourceIdentity: "roslyn-companion/v1");
        if (imported.Status != RestrictedCilImportStatusV1.Success || !Body(result.Pe!).SequenceEqual(Body(oraclePe)) ||
            !Locals(result.Pe!).SequenceEqual(Locals(oraclePe)) ||
            !result.Imported.Program!.ControlFlowGraph.Edges.SequenceEqual(imported.Program!.ControlFlowGraph.Edges) ||
            !result.Imported.Program.Instructions.Select(i => (i.Index, i.Opcode, i.DataType, i.Immediate))
                .SequenceEqual(imported.Program.Instructions.Select(i => (i.Index, i.Opcode, i.DataType, i.Immediate))))
            throw new Exception("Labeled continue differs from independent CSharp13 emitted-CIL oracle: actual=" +
                Convert.ToHexString(Body(result.Pe!)) + "; oracle=" + Convert.ToHexString(Body(oraclePe)) + "; status=" + imported.Status);
        // Full fingerprints bind exact PE provenance; different source/PDB must keep distinct identities.
        ManagedSourceCompanionResultV1 changed = Compile(source.Replace("sum += i + j;", "sum += i + j + 1;"));
        if (changed.Imported?.Status != RestrictedCilImportStatusV1.Success || Body(result.Pe!).SequenceEqual(Body(changed.Pe!)) ||
            changed.SourceInputDigest == result.SourceInputDigest || changed.ArtifactIdentity?.Digest == result.ArtifactIdentity?.Digest)
            throw new Exception("Continue oracle failed changed-source divergence control");
        void Denied(ManagedSourceCompanionResultV1 denied, string? id) {
            if (denied.Pe is not null || denied.Pdb is not null || denied.Imported is not null || denied.SourceMap is not null ||
                denied.ArtifactIdentity is not null || !denied.Diagnostics.Any(d => (id is null || d.Id == id) && d.Severity == "Error"))
                throw new Exception("Labeled continue admission failed: " + id + "; " + string.Join(';', denied.Diagnostics));
            Console.WriteLine("DIAGNOSTICS continue denied=" + string.Join(';', denied.Diagnostics));
        }
        Denied(Compile(source.Replace("continue outer;", "continue missing;")), null);
        Denied(Compile(source.Replace("return sum;", "return missing;")), "CS0103");
        Denied(Compile(source.Replace("int sum = 0;", "object allocated = new object(); int sum = 0;")), "HC0004");
        ManagedSourceCompanionResultV1 oldDenied = ManagedSourceCompanionV1.Compile(request with { Language = CompanionSourceLanguageV1.CSharp13 }, importer, selector);
        if (oldDenied.Pe is not null || !oldDenied.Diagnostics.Any(d => d.Severity == "Error"))
            throw new Exception("CSharp13 admitted labeled continue");
        ManagedSourceCompanionResultV1 unsupported = Compile(source.Replace("return sum;", "double scalar = sum; return (int)(scalar + 0.5);"));
        if (unsupported.Pe is null || unsupported.Imported?.Status == RestrictedCilImportStatusV1.Success ||
            unsupported.Imported is null || unsupported.Imported.Program is not null || unsupported.SourceMap is not null)
            throw new Exception("Unsupported emitted IL crossed labeled continue ingress");
        bool cancelled = false;
        try { ManagedSourceCompanionV1.Compile(request, importer, selector, new CancellationToken(true)); }
        catch (OperationCanceledException) { cancelled = true; }
        if (!cancelled || !Compile(source).Pe!.SequenceEqual(result.Pe!)) throw new Exception("Continue cancellation/reuse failed");
        Console.WriteLine("PASS labeled continue NoHeap source -> deterministic PE/PDB/CIL/map; CSharp13 independent IL/locals/CFG/opcode oracle; source/NoHeap/unsupported-IL/cancel/reuse/divergence negatives");
        Console.WriteLine("IDENTITY continue input=" + result.SourceInputDigest + "; artifact=" + result.ArtifactIdentity!.Digest +
            "; map=" + result.SourceMap.Digest + "; IL=" + Convert.ToHexString(Body(result.Pe!)) +
            "; unsupported=" + string.Join(';', unsupported.Imported!.Diagnostics));
    }
}
