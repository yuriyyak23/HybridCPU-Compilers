namespace MinimalAsmApp.Examples.Abstractions;

public interface ICpuExample
{
    string Name { get; }

    string Description { get; }

    string Category { get; }

    CpuExampleResult Run();
}
