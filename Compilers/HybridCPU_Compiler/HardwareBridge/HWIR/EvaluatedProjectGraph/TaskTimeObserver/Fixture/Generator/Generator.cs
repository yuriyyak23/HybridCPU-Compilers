using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ObserverFixture;

[Generator]
public sealed class AnswerGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context) { }

    public void Execute(GeneratorExecutionContext context)
    {
        context.AddSource("GeneratedAnswer.g.cs", SourceText.From(
            "namespace ObserverFixture; public static class GeneratedAnswer { public const int Value = 42; }",
            Encoding.UTF8));
    }
}
