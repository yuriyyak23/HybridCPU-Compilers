
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ObserverFixture;

[Generator]
public sealed class AnswerGenerator : IIncrementalGenerator
{
    public void Initialize(
        IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(
            static postInitializationContext =>
            {
                const string source =
                    "namespace ObserverFixture; " +
                    "public static class GeneratedAnswer " +
                    "{ public const int Value = 42; }";

                postInitializationContext.AddSource(
                    "GeneratedAnswer.g.cs",
                    SourceText.From(source, Encoding.UTF8));
            });
    }
}
