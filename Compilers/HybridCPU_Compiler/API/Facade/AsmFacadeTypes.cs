using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core.Registers;

namespace HybridCPU.Compiler.Core.API.Facade;

internal static class AsmFacadeDeprecation
{
    internal const string Message =
        "Compatibility facade surface (REF-B2). Use HybridCpuThreadCompilerContext, HybridCpuCanonicalCompiler, or HybridCpuMultithreadedCompiler for new compiler integrations.";
}

/// <summary>Facade-level register wrapper over flat architectural register identity.</summary>
public readonly record struct AsmRegister(ArchRegId ArchRegisterId)
{
    public AsmRegister(int archRegisterId)
        : this(ArchRegId.Create(archRegisterId))
    {
    }
}

/// <summary>Compiler-native symbolic control target used by facade-level metadata helpers.</summary>
public enum AsmControlTargetKind
{
    ProgramEntry,
    EntryPoint,
    CallTarget,
    InterruptHandler
}

/// <summary>Named symbolic control target understood by the compiler metadata pipeline.</summary>
public readonly record struct AsmControlTarget
{
    public AsmControlTarget(string name, AsmControlTargetKind kind = AsmControlTargetKind.EntryPoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Kind = kind;
    }

    public string Name { get; }

    public AsmControlTargetKind Kind { get; }

    public bool IsDefined => !string.IsNullOrWhiteSpace(Name);
}

/// <summary>Informational resource class hint (not slot-level, not typed-slot).</summary>
public enum ResourceClassHint
{
    Scalar,
    Vector,
    Memory,
    System,
    Unknown
}
