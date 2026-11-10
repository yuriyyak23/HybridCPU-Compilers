using HybridCPU.Compiler.Core.IR;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Vector;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class MatrixTileMaccCompilerExample : ICpuExample
{
    public string Name => "matrix-tile-macc-compiler";

    public string Description => "Emits MTILE_MACC through the typed compiler helper with explicit runtime numeric/layout policy sidebands.";

    public string Category => "10_Vector";

    public CpuExampleResult Run() =>
        MatrixTileCompilerExampleSupport.Run(
            "MTILE_MACC emitted through CompileMtileMacc/AppAsmFacade.MtileMacc with explicit policy sidebands.",
            "CompileMtileMacc",
            Instruction.MTILE_MACC,
            facade => facade.MtileMacc(
                CompilerMatrixTileTileOperand.Create(1),
                CompilerMatrixTileTileOperand.Create(2),
                CompilerMatrixTileTileOperand.Create(3),
                MatrixTileCompilerExampleSupport.CreateDescriptor(),
                MatrixTileCompilerExampleSupport.CreateMaccPolicy()),
            MatrixTileCompilerExampleSupport.CreateExpectedMaccNumericPolicy(),
            MatrixTileCompilerExampleSupport.CreateExpectedMaccLayoutPolicy());
}
