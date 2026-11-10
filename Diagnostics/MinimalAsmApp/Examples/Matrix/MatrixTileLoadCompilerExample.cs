using HybridCPU.Compiler.Core.IR;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Vector;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class MatrixTileLoadCompilerExample : ICpuExample
{
    public string Name => "matrix-tile-load-compiler";

    public string Description => "Emits MTILE_LOAD through the typed compiler helper and verifies that compute policy sidebands stay absent.";

    public string Category => "10_Vector";

    public CpuExampleResult Run() =>
        MatrixTileCompilerExampleSupport.Run(
            "MTILE_LOAD emitted through CompileMtileLoad/AppAsmFacade.MtileLoad.",
            "CompileMtileLoad",
            Instruction.MTILE_LOAD,
            facade => facade.MtileLoad(
                CompilerMatrixTileTileOperand.Create(1),
                MatrixTileCompilerExampleSupport.CreateDescriptor(),
                MatrixTileCompilerExampleSupport.CreateMemoryFault(0x7000)),
            expectedNumericPolicy: null,
            expectedLayoutPolicy: null);
}
