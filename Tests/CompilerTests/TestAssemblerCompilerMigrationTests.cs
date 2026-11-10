using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Tests.TestHelpers;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile;
using RuntimeMatrixTileDescriptor = YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile.MatrixTileCanonicalDescriptorAbi;
using RuntimeMatrixTileLayoutPolicy = YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile.MatrixTileLayoutPolicy;
using RuntimeMatrixTileNumericPolicy = YAKSys_Hybrid_CPU.CloseToHSL.Core.ISA.Instructions.NonVmx.Lanes00_03Vector.MatrixTile.MatrixTileNumericPolicy;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class TestAssemblerCompilerMigrationTests
{
    [Fact]
    public void ConsoleSources_UseExplicitRuntimeBoundaryAndNoRemovedEmissionApi()
    {
        string root = CompatFreezeScanner.FindCompilerRepoRoot();
        string simpleAssembler = File.ReadAllText(
            Path.Combine(root, "Diagnostics", "TestAssemblerConsoleApps", "SimpleAsmApp.cs"));
        string matrixTileSuite = File.ReadAllText(
            Path.Combine(root, "Diagnostics", "TestAssemblerConsoleApps", "MatrixTileSpecSuite.cs"));

        Assert.Contains("NativeTransportRuntimeAdapter.CompileProgram", simpleAssembler, StringComparison.Ordinal);
        Assert.Contains("NativeTransportRuntimeAdapter.EmitProgram", simpleAssembler, StringComparison.Ordinal);
        Assert.Contains("NativeTransportRuntimeAdapter.ToCore", matrixTileSuite, StringComparison.Ordinal);
        Assert.Contains("NativeTransportRuntimeAdapter.ToRuntime", matrixTileSuite, StringComparison.Ordinal);
        Assert.Contains("context.CompileMtileLoadWithDecision", matrixTileSuite, StringComparison.Ordinal);
        Assert.Contains("context.CompileMtileStoreWithDecision", matrixTileSuite, StringComparison.Ordinal);
        Assert.Contains("context.CompileMtileMaccWithDecision", matrixTileSuite, StringComparison.Ordinal);
        Assert.Contains("context.CompileMtransposeWithDecision", matrixTileSuite, StringComparison.Ordinal);
        Assert.DoesNotContain("HybridCpuCanonicalCompiler.EmitProgram", simpleAssembler, StringComparison.Ordinal);
        Assert.DoesNotContain("EmitVliwBundleImage", matrixTileSuite, StringComparison.Ordinal);
        Assert.DoesNotContain("context.CompileMtileLoad(", matrixTileSuite, StringComparison.Ordinal);
        Assert.DoesNotContain("context.CompileMtileStore(", matrixTileSuite, StringComparison.Ordinal);
        Assert.DoesNotContain("context.CompileMtileMacc(", matrixTileSuite, StringComparison.Ordinal);
        Assert.DoesNotContain("context.CompileMtranspose(", matrixTileSuite, StringComparison.Ordinal);
    }

    [Fact]
    public void MatrixTileVocabulary_RoundTripsExactlyAcrossRuntimeAdapter()
    {
        RuntimeMatrixTileDescriptor descriptor = RuntimeMatrixTileDescriptor.Create(3, 2, 1, 4);
        RuntimeMatrixTileNumericPolicy numericPolicy =
            MatrixTileNumericPolicyAbi.CreateSupportedPolicy(MatrixTileNumericProfileId.SignedInt8ToInt32);
        RuntimeMatrixTileLayoutPolicy layoutPolicy = MatrixTileLayoutPolicyAbi.CreateTransposePolicy();

        Assert.Equal(
            descriptor,
            NativeTransportRuntimeAdapter.ToRuntime(
                NativeTransportRuntimeAdapter.ToCore(descriptor)));
        Assert.Equal(
            numericPolicy,
            NativeTransportRuntimeAdapter.ToRuntime(
                NativeTransportRuntimeAdapter.ToCore(numericPolicy)));
        Assert.Equal(
            layoutPolicy,
            NativeTransportRuntimeAdapter.ToRuntime(
                NativeTransportRuntimeAdapter.ToCore(layoutPolicy)));
        Assert.Equal(
            DataTypeEnum.INT8,
            NativeTransportRuntimeAdapter.ToRuntime(
                NativeTransportRuntimeAdapter.ToCore(DataTypeEnum.INT8)));
    }

    [Fact]
    public void RuntimeEmission_RejectsUnalignedAddressFailClosed()
    {
        HybridCpuCompiledProgram program =
            NativeTransportRuntimeAdapter.CompileProgram(0, CreateRuntimeProgram());

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => NativeTransportRuntimeAdapter.EmitProgram(program, baseAddress: 1));

        Assert.Contains("256-byte aligned", exception.Message, StringComparison.Ordinal);
        Assert.Null(program.EmissionBaseAddress);
    }

    private static VLIW_Instruction[] CreateRuntimeProgram() =>
    [
        new VLIW_Instruction
        {
            OpCode = (uint)Processor.CPU_Core.InstructionsEnum.ADDI,
            DataTypeValue = DataTypeEnum.INT32,
            PredicateMask = byte.MaxValue,
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(1, 0, VLIW_Instruction.NoArchReg),
            Src2Pointer = 7,
            VirtualThreadId = 0
        }
    ];
}
