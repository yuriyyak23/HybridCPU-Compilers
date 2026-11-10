using HybridCPU.Compiler.Core.IR;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Vector;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class MatrixTileTransposeCompilerExample : ICpuExample
{
    public string Name => "matrix-tile-transpose-compiler";

    public string Description => "Emits MTRANSPOSE through the typed compiler helper with a layout sideband and no MACC numeric sideband.";

    public string Category => "10_Vector";

    public CpuExampleResult Run() =>
        MatrixTileCompilerExampleSupport.Run(
            "MTRANSPOSE emitted through CompileMtranspose/AppAsmFacade.Mtranspose with layout-only sideband.",
            "CompileMtranspose",
            Instruction.MTRANSPOSE,
            facade => facade.Mtranspose(
                CompilerMatrixTileTileOperand.Create(1),
                CompilerMatrixTileTileOperand.Create(2),
                MatrixTileCompilerExampleSupport.CreateDescriptor(),
                MatrixTileCompilerExampleSupport.CreateTransposePolicy()),
            expectedNumericPolicy: null,
            MatrixTileCompilerExampleSupport.CreateExpectedTransposeLayoutPolicy());
}
