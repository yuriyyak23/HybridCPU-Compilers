using HybridCPU_ISE.Tests.TestHelpers;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class MinimalAsmCompilerMigrationTests
{
    [Fact]
    public void MinimalAsmSources_UseExplicitCompilerRuntimeBoundary()
    {
        string root = CompatFreezeScanner.FindCompilerRepoRoot();
        string executor = File.ReadAllText(
            Path.Combine(root, "Diagnostics", "MinimalAsmApp", "Examples", "Support", "CpuProgramExecutor.cs"));
        string matrixSupport = File.ReadAllText(
            Path.Combine(root, "Diagnostics", "MinimalAsmApp", "Examples", "Matrix", "MatrixTileCompilerExampleSupport.cs"));

        Assert.Contains("NativeTransportRuntimeAdapter.CompileProgram", executor, StringComparison.Ordinal);
        Assert.Contains("NativeTransportRuntimeAdapter.EmitProgram", executor, StringComparison.Ordinal);
        Assert.DoesNotContain("HybridCpuCanonicalCompiler.CompileProgram", executor, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishBundleAnnotations", executor, StringComparison.Ordinal);

        Assert.Contains("NativeTransportRuntimeAdapter.ToCore(Tile2X2I8)", matrixSupport, StringComparison.Ordinal);
        Assert.Contains("NativeTransportRuntimeAdapter.ToCore(elementType)", matrixSupport, StringComparison.Ordinal);
        Assert.Contains("NativeTransportRuntimeAdapter.ToRuntime(in compilerBundle)", matrixSupport, StringComparison.Ordinal);
        Assert.Contains(
            "compiledProgram.LoweredBundleAnnotations[bundleIndex]",
            matrixSupport,
            StringComparison.Ordinal);
    }
}
