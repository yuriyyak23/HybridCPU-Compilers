using System.Text;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

internal static class CompanionIngressChecks
{
    public static void Run()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(SourceText.From(
            "public static class CompanionFixture { public static int Read() { return 42; } public static int Choose(int value) { if (value > 0) return value + 1; return value - 1; } }",
            Encoding.UTF8), new CSharpParseOptions(LanguageVersion.CSharp13), path: "/src/CompanionFixture.cs");
        CSharpCompilation compilation = CSharpCompilation.Create("CompanionFixture", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true,
                optimizationLevel: OptimizationLevel.Release));
        (byte[] Pe, byte[] Pdb) Emit()
        {
            using MemoryStream pe = new MemoryStream();
            using MemoryStream pdb = new MemoryStream();
            EmitResult result = compilation.Emit(pe, pdb, options: new EmitOptions(
                debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: "/symbols/companion.pdb"));
            if (!result.Success) throw new Exception(string.Join(';', result.Diagnostics));
            return (pe.ToArray(), pdb.ToArray());
        }
        (byte[] Pe, byte[] Pdb) first = Emit();
        CompanionSourceRequestV1 sourceRequest = new HybridCPU.Compiler.Roslyn.CompanionSourceRequestV1("CompanionFixture",
            [new("/src/CompanionFixture.cs", tree.GetText().ToString())],
            [File.ReadAllBytes(typeof(object).Assembly.Location)]);
        HybridCPU.Compiler.Roslyn.ManagedSourceCompanionResultV1 Owned(HybridCPU.Compiler.Roslyn.CompanionSourceRequestV1 request) =>
            HybridCPU.Compiler.Roslyn.ManagedSourceCompanionV1.Compile(request,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), new("CompanionFixture", "Choose"));
        ManagedSourceCompanionResultV1 owned = Owned(sourceRequest);
        ManagedSourceCompanionResultV1 ownedRepeat = Owned(sourceRequest);
        ManagedSourceCompanionResultV1 changedSource = Owned(sourceRequest with { Documents = [new("/src/CompanionFixture.cs", tree.GetText() + "\n// changed")] });
        ManagedSourceCompanionResultV1 changedReference = Owned(sourceRequest with { ReferenceImages =
            [File.ReadAllBytes(typeof(object).Assembly.Location), File.ReadAllBytes(typeof(CompanionIngressChecks).Assembly.Location)] });
        ManagedSourceCompanionResultV1 changedAssembly = Owned(sourceRequest with { AssemblyName = "CompanionRenamed" });
        ManagedSourceCompanionResultV1 changedPath = Owned(sourceRequest with { Documents = [new("/src/Renamed.cs", tree.GetText().ToString())] });
        if (owned.Imported?.Status != RestrictedCilImportStatusV1.Success || owned.SourceInputDigest is null ||
            owned.SourceInputDigest != ownedRepeat.SourceInputDigest || !owned.Pe!.SequenceEqual(ownedRepeat.Pe!) ||
            owned.SourceInputDigest == changedSource.SourceInputDigest || owned.SourceInputDigest == changedReference.SourceInputDigest ||
            owned.SourceInputDigest == changedAssembly.SourceInputDigest || owned.SourceInputDigest == changedPath.SourceInputDigest)
            throw new Exception("Owned source/reference identity drift.");
        Console.WriteLine("PASS explicit source/reference snapshot identity repeat and invalidation");
        ReadOnlyMemory<byte>[] profileReferences = new ReadOnlyMemory<byte>[] {
            File.ReadAllBytes(typeof(object).Assembly.Location),
            File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
            File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "netstandard.dll")),
            File.ReadAllBytes(typeof(HybridCPU.Compiler.Profiles.HybridCpuKernelNoHeapAttribute).Assembly.Location) };
        HybridCPU.Compiler.Roslyn.ManagedSourceCompanionResultV1 Profile(string body) =>
            HybridCPU.Compiler.Roslyn.ManagedSourceCompanionV1.Compile(new HybridCPU.Compiler.Roslyn.CompanionSourceRequestV1(
                "ProfileCompanion", [new("/src/Profile.cs", "using HybridCPU.Compiler.Profiles; public class ProfileFixture { [HybridCpuKernelNoHeap] public static int Read() { " + body + " } }")],
                profileReferences), new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), new("ProfileFixture", "Read"));
        ManagedSourceCompanionResultV1 profilePositive = Profile("return 42;");
        ManagedSourceCompanionResultV1 profileNegative = Profile("object value = new(); return 42;");
        if (profilePositive.Imported?.Status != RestrictedCilImportStatusV1.Success ||
            profileNegative.Pe is not null || profileNegative.Imported is not null || profileNegative.ArtifactIdentity is not null ||
            !profileNegative.Diagnostics.Any(d => d.Id == "HC0004"))
            throw new Exception("Companion NoHeap admission boundary drift.");
        Console.WriteLine("PASS companion NoHeap positive ingress and HC0004 pre-emit rejection");
        ManagedSourceCompanionResultV1 suppressedProfile = Profile("\n#pragma warning disable HC0004\nobject value = new(); return 42;\n#pragma warning restore HC0004\n");
        if (suppressedProfile.Pe is not null || suppressedProfile.Imported is not null ||
            !suppressedProfile.Diagnostics.Any(d => d.Id == "HC0004"))
            throw new Exception("Source pragma bypassed companion NoHeap admission.");
        (byte[] Pe, byte[] Pdb) second = Emit();
        ManagedSourceCompanionResultV1 companion = HybridCPU.Compiler.Roslyn.ManagedSourceCompanionV1.Compile(compilation,
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), new("CompanionFixture", "Choose"));
        ManagedSourceCompanionResultV1 companionRepeat = HybridCPU.Compiler.Roslyn.ManagedSourceCompanionV1.Compile(compilation,
            new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), new("CompanionFixture", "Choose"));
        if (companion.Imported?.Status != RestrictedCilImportStatusV1.Success || companion.SourceMap is null ||
            !companion.Pe!.SequenceEqual(companionRepeat.Pe!) || !companion.Pdb!.SequenceEqual(companionRepeat.Pdb!) ||
            companion.SourceMap.Digest != companionRepeat.SourceMap?.Digest)
            throw new Exception("Production companion ingress drift.");
        bool companionCancelled = false;
        try
        {
            HybridCPU.Compiler.Roslyn.ManagedSourceCompanionV1.Compile(compilation,
                new RestrictedCilImporterV1(), new("CompanionFixture", "Read"), new CancellationToken(true));
        }
        catch (OperationCanceledException) { companionCancelled = true; }
        if (!companionCancelled) throw new Exception("Companion pre-cancellation ignored.");
        CompanionArtifactIdentityV1? identity = companion.ArtifactIdentity!;
        if (identity is null || identity.Digest != companionRepeat.ArtifactIdentity?.Digest ||
            !identity.Matches(companion.Pe!, companion.Pdb!) ||
            identity.Matches(new byte[] { 1, 2, 3 }, companion.Pdb!) ||
            identity.Matches(companion.Pe!, new byte[] { 1, 2, 3 }))
            throw new Exception("Companion artifact identity/rejection drift.");
        if (!first.Pe.SequenceEqual(second.Pe) || !first.Pdb.SequenceEqual(second.Pdb))
            throw new Exception("Companion PE/PDB determinism drift.");
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        foreach (string method in new[] { "Read", "Choose" })
        {
            RestrictedCilImportResultV1 imported = importer.ImportImage(first.Pe, new("CompanionFixture", method), sourceIdentity: "roslyn-companion/v1");
            if (imported.Status != RestrictedCilImportStatusV1.Success)
                throw new Exception(string.Join(';', imported.Diagnostics));
            if (method == "Choose" && imported.Program!.BasicBlocks.Count < 3)
                throw new Exception("Conditional fixture lost its CFG branch.");
            ManagedSourceMapV1 mapped = ManagedSourceMapV1.Create(imported, first.Pe, first.Pdb);
            ManagedSourceCompanionResultV1 adapter = HybridCPU.Compiler.Roslyn.ManagedSourceCompanionV1.Compile(compilation,
                new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2), new("CompanionFixture", method));
            if (adapter.Imported?.Status != RestrictedCilImportStatusV1.Success || adapter.SourceMap is null ||
                !first.Pe.SequenceEqual(adapter.Pe!) || !first.Pdb.SequenceEqual(adapter.Pdb!) ||
                HybridCPU.Compiler.Core.IR.Telemetry.CompilerScheduleFingerprintV1.HashProgramInput(imported.Program!) !=
                HybridCPU.Compiler.Core.IR.Telemetry.CompilerScheduleFingerprintV1.HashProgramInput(adapter.Imported.Program!) ||
                !mapped.Rows.SequenceEqual(adapter.SourceMap.Rows))
                throw new Exception("Adapter differs from independently emitted CIL oracle: " + method);
            RestrictedCilImportResultV1 repeated = importer.ImportImage(second.Pe, new("CompanionFixture", method), sourceIdentity: "roslyn-companion/v1");
            ManagedSourceMapV1 secondMap = ManagedSourceMapV1.Create(repeated, second.Pe, second.Pdb);
            if (mapped.Rows.Count == 0 || mapped.Rows.All(r => r.Document is null) || mapped.Digest != secondMap.Digest)
                throw new Exception("Companion PE/PDB→CIL source-map drift.");
            if (HybridCPU.Compiler.Core.IR.Telemetry.CompilerScheduleFingerprintV1.HashProgramInput(imported.Program!) !=
                HybridCPU.Compiler.Core.IR.Telemetry.CompilerScheduleFingerprintV1.HashProgramInput(repeated.Program!))
                throw new Exception("Repeated emitted PE changed imported IR.");
        }
        SyntaxTree changedTree = CSharpSyntaxTree.ParseText(SourceText.From(tree.GetText().ToString().Replace("return 42;", "return 43;"), Encoding.UTF8),
            new CSharpParseOptions(LanguageVersion.CSharp13), path: "/src/CompanionFixture.cs");
        ManagedSourceCompanionResultV1 changed = HybridCPU.Compiler.Roslyn.ManagedSourceCompanionV1.Compile(compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(changedTree),
            importer, new("CompanionFixture", "Read"));
        RestrictedCilImportResultV1 original = importer.ImportImage(first.Pe, new("CompanionFixture", "Read"), sourceIdentity: "roslyn-companion/v1");
        if (changed.Imported?.Status != RestrictedCilImportStatusV1.Success ||
            HybridCPU.Compiler.Core.IR.Telemetry.CompilerScheduleFingerprintV1.HashProgramInput(changed.Imported.Program!) ==
            HybridCPU.Compiler.Core.IR.Telemetry.CompilerScheduleFingerprintV1.HashProgramInput(original.Program!))
            throw new Exception("Emitted-CIL oracle failed source-change divergence control.");
        SyntaxTree invalidTree = CSharpSyntaxTree.ParseText("public class Broken { public int Read() => missing; }",
            new CSharpParseOptions(LanguageVersion.CSharp13), path: "/src/Broken.cs");
        CSharpCompilation invalid = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(invalidTree);
        string[] Errors() => invalid.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Id + ":" + d.Location.SourceSpan).ToArray();
        if (!Errors().SequenceEqual(Errors()) || !Errors().Any(e => e.StartsWith("CS0103:", StringComparison.Ordinal)))
            throw new Exception("Source diagnostic drift.");
        using MemoryStream rejectedPe = new MemoryStream();
        if (invalid.Emit(rejectedPe).Success || rejectedPe.Length != 0)
            throw new Exception("Source error produced admissible PE.");
        ManagedSourceCompanionResultV1 denied = HybridCPU.Compiler.Roslyn.ManagedSourceCompanionV1.Compile(invalid,
            importer, new("Broken", "Read"));
        if (denied.Pe is not null || denied.Imported is not null || denied.ArtifactIdentity is not null || !denied.Diagnostics.Any(d => d.Id == "CS0103"))
            throw new Exception("Companion source diagnostic crossed ingress.");
        Console.WriteLine("PASS explicit production companion adapter repeat and source-error stop");
        Console.WriteLine("PASS deterministic companion PE/PDB -> existing CIL ingress and source-map repeat; Qualification: no");
        Console.WriteLine("PASS companion conditional branch and deterministic source-error rejection");
        Console.WriteLine("PASS adapter versus independent emitted-CIL oracle: Read/Choose IR and source-map rows equal");
    }
}
