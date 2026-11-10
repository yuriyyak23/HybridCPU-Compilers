using System.Reflection;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU_ISE.Tests.TestHelpers;
using YAKSys_Hybrid_CPU.Core.Pipeline;
using YAKSys_Hybrid_CPU.Core.Pipeline.MicroOps;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class Rf066dCompilerCompatibilityReconstructionRemovalTests
{
    [Fact]
    public void UnpublishedCompatibilityPredicates_MatchRemovedInternalKindReconstruction()
    {
        Type semantics = typeof(HybridCpuHazardModel).Assembly.GetType(
            "HybridCPU.Compiler.Core.IR.HybridCpuOpcodeSemantics",
            throwOnError: true)!;
        MethodInfo control = RequireMethod(semantics, "TryResolveRetainedCompatibilityControlFlowKind");
        MethodInfo memory = RequireMethod(semantics, "TryResolveRetainedCompatibilityScalarMemoryDirection");

        foreach (InstructionsEnum opcode in Enum.GetValues<InstructionsEnum>())
        {
            MethodInfo getInfo = RequireMethod(semantics, "GetOpcodeInfo");
            if (getInfo.Invoke(null, new object?[] { opcode }) is not null)
            {
                continue;
            }

            bool expectedControl = false;
            bool expectedMemory = false;
            try
            {
                InternalOpKind kind = InternalOpBuilder.MapToKind(unchecked((ushort)opcode));
                expectedControl = kind == InternalOpKind.InterruptReturn;
                expectedMemory = kind is InternalOpKind.Load or InternalOpKind.Store;
                Assert.DoesNotContain(
                    kind,
                    new[] { InternalOpKind.Interrupt, InternalOpKind.InterruptReturn, InternalOpKind.Load, InternalOpKind.Store });
            }
            catch (ArgumentOutOfRangeException)
            {
            }

            object?[] controlArgs = { opcode, IrControlFlowKind.None };
            object?[] memoryArgs = { opcode, false };
            Assert.Equal(expectedControl, Assert.IsType<bool>(control.Invoke(null, controlArgs)));
            Assert.Equal(expectedMemory, Assert.IsType<bool>(memory.Invoke(null, memoryArgs)));
            Assert.Equal(IrControlFlowKind.None, controlArgs[1]);
            Assert.Equal(false, memoryArgs[1]);
        }
    }

    [Fact]
    public void CompilerSources_NoLongerConsumeRuntimeInternalKindReconstruction()
    {
        string root = CompatFreezeScanner.FindCompilerRepoRoot();
        string compilerRoot = Path.Combine(root, "Compilers", "HybridCPU_Compiler");
        string[] sources = Directory.GetFiles(compilerRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        Assert.DoesNotContain(sources, path =>
            File.ReadAllText(path).Contains("InternalOpBuilder.MapToKind(", StringComparison.Ordinal));
        string semantics = File.ReadAllText(Path.Combine(
            compilerRoot,
            "Core",
            "IR",
            "Hazards",
            "HybridCpuOpcodeSemantics.cs"));
        Assert.DoesNotContain("InternalOpKind", semantics, StringComparison.Ordinal);
        Assert.DoesNotContain("TryResolveRetainedCompatibilityInternalKind", semantics, StringComparison.Ordinal);
    }

    private static MethodInfo RequireMethod(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"{type.FullName}.{name} was not found.");
}
