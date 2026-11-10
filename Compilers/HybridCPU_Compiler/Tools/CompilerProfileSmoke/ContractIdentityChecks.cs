using System.Collections.Immutable;
using HybridCPU.Compiler.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class ContractIdentityChecks
{
    public static void Run()
    {
        string Generate(bool reverse, string parameterName = "T", string? ownerParameter = null, bool nested = false, string returnModifier = "")
        {
            string[] methods = ["void Read(int value);", "void Read(ref int value);", "void Read<T>();", "void Read<T,U>();"];
            methods = methods.Append($"{parameterName}[] Echo<{parameterName}>({parameterName}[] value);").ToArray();
            methods = methods.Append($"{returnModifier}int Return();").ToArray();
            if (ownerParameter is not null) methods = methods.Append($"{ownerParameter}[] Owner({ownerParameter}[] value);").ToArray();
            if (nested) methods = methods.Concat(new[] { $"void Context({ownerParameter} value);", $"void Context({ownerParameter}Outer value);" }).ToArray();
            if (reverse) Array.Reverse(methods);
            SyntaxTree tree = CSharpSyntaxTree.ParseText("using HybridCPU.Compiler.Profiles; " +
                (nested ? $"class Outer<{ownerParameter}Outer> {{ " : "") + "[HybridCpuStaticContract] interface IRef" +
                (ownerParameter is null ? "" : "<" + ownerParameter + ">") + " { " +
                string.Join(" ", methods) + " }" + (nested ? " }" : ""), new CSharpParseOptions(LanguageVersion.CSharp13));
            CSharpCompilation compilation = CSharpCompilation.Create("contract-identity", [tree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
                    MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "netstandard.dll")),
                    MetadataReference.CreateFromFile(typeof(HybridCPU.Compiler.Profiles.HybridCpuStaticContractAttribute).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            GeneratorDriver driver = CSharpGeneratorDriver.Create([new StaticContractGenerator().AsSourceGenerator()],
                parseOptions: new CSharpParseOptions(LanguageVersion.CSharp13));
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation? generated, out ImmutableArray<Diagnostic> generatorDiagnostics);
            Diagnostic[] errors = generated.GetDiagnostics().Concat(generatorDiagnostics).Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0) throw new Exception("Generated contract compilation failed: " + string.Join(';', errors.Select(d => d.ToString())));
            return driver.GetRunResult().GeneratedTrees.Single().GetText().ToString();
        }
        string text = Generate(false);
        string[] wires = text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("0x", StringComparison.Ordinal)).ToArray();
        if (wires.Length != 6 || wires.Distinct().Count() != 6) throw new Exception("Generic arity overload wire identity collision.");
        if (text != Generate(true)) throw new Exception("Declaration ordering changed contract manifest.");
        if (text != Generate(false, "Renamed")) throw new Exception("Generic parameter rename changed contract identity.");
        if (Generate(false, ownerParameter: "OwnerT") != Generate(true, ownerParameter: "RenamedOwner"))
            throw new Exception("Generic owner parameter rename changed manifest identity.");
        string nestedText = Generate(false, ownerParameter: "InnerT", nested: true);
        string[] nestedWires = nestedText.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("0x", StringComparison.Ordinal)).ToArray();
        if (nestedWires.Length != 9 || nestedWires.Distinct().Count() != 9 ||
            nestedText != Generate(true, ownerParameter: "RenamedInner", nested: true))
            throw new Exception("Nested owner depth identity collision/rename drift.");
        string byRef = Generate(false, returnModifier: "ref ");
        string readOnly = Generate(false, returnModifier: "ref readonly ");
        if (text == byRef || byRef == readOnly || text == readOnly || byRef != Generate(true, returnModifier: "ref "))
            throw new Exception("By-ref return identity distinction/order drift.");
        Console.WriteLine("PASS generated contract compilation and value/ref/ref-readonly return identities");
        Console.WriteLine("PASS static contract value/ref/generic-arity IDs, declaration-order and method-parameter rename equality");
    }
}
