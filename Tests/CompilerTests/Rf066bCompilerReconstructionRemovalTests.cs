using System.Reflection;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Tests.TestHelpers;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core.Pipeline;
using YAKSys_Hybrid_CPU.Core.Pipeline.MicroOps;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class Rf066bCompilerReconstructionRemovalTests
{
    [Fact]
    public void SignedDivideTrapContour_MatchesRemovedRuntimeKindReconstructionForEveryScalarOpcode()
    {
        MethodInfo predicate = typeof(HybridCpuHazardModel).Assembly.GetType(
                "HybridCPU.Compiler.Core.IR.HybridCpuOpcodeSemantics",
                throwOnError: true)!
            .GetMethod(
                "IsSignedDivideTrapContour",
                BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "HybridCpuOpcodeSemantics.IsSignedDivideTrapContour was not found.");

        foreach (OpcodeInfo opcodeInfo in OpcodeRegistry.Opcodes
                     .Where(info => info.InstructionClass == InstructionClass.ScalarAlu))
        {
            InstructionsEnum opcode = (InstructionsEnum)opcodeInfo.OpCode;
            HybridCpuOpcode coreOpcode = NativeTransportRuntimeAdapter.ToCore(opcode);
            HybridCpuOpcodeInfo? coreOpcodeInfo = HybridCpuOpcodeCatalog.GetInfo(coreOpcode);
            bool legacy;
            try
            {
                legacy = InternalOpBuilder.MapToKind(checked((ushort)opcodeInfo.OpCode)) ==
                         InternalOpKind.Div;
            }
            catch (ArgumentOutOfRangeException)
            {
                TargetInvocationException rejection = Assert.Throws<TargetInvocationException>(() =>
                    predicate.Invoke(null, new object?[] { coreOpcode, coreOpcodeInfo }));
                Assert.IsType<ArgumentOutOfRangeException>(rejection.InnerException);
                continue;
            }

            bool canonicalCompilerPredicate = Assert.IsType<bool>(
                predicate.Invoke(null, new object?[] { coreOpcode, coreOpcodeInfo }));

            Assert.Equal(legacy, canonicalCompilerPredicate);
        }
    }

    [Fact]
    public void IrBuilder_NoLongerConsumesRuntimeInternalKindReconstruction()
    {
        string root = CompatFreezeScanner.FindCompilerRepoRoot();
        string builderSource = File.ReadAllText(Path.Combine(
            root,
            "Compilers",
            "HybridCPU_Compiler",
            "Core",
            "IR",
            "Construction",
            "HybridCpuIrBuilder.cs"));

        Assert.DoesNotContain("InternalOpBuilder", builderSource, StringComparison.Ordinal);
        Assert.Contains(
            "HybridCpuOpcodeSemantics.IsSignedDivideTrapContour(opcode, resolvedOpcodeInfo)",
            builderSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnyRemainingCompilerMapToKindConsumer_IsOnlyTheNamedCompatibilityHelper()
    {
        string root = CompatFreezeScanner.FindCompilerRepoRoot();
        string compilerRoot = Path.Combine(root, "Compilers", "HybridCPU_Compiler");
        string[] sources = Directory.GetFiles(compilerRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        string[] consumers = sources
            .Where(path => File.ReadAllText(path).Contains("InternalOpBuilder.MapToKind(", StringComparison.Ordinal))
            .ToArray();

        Assert.InRange(consumers.Length, 0, 1);
        Assert.All(consumers, consumer => Assert.EndsWith(
            Path.Combine("Core", "IR", "Hazards", "HybridCpuOpcodeSemantics.cs"),
            consumer,
            StringComparison.Ordinal));
    }
}
