using HybridCPU.Compiler.Core.IR;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Vector;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class MatrixTileStoreCompilerExample : ICpuExample
{
    public string Name => "matrix-tile-store-compiler";

    public string Description => "Emits MTILE_STORE through the typed compiler helper and verifies that compute policy sidebands stay absent.";

    public string Category => "10_Vector";

    public CpuExampleResult Run() =>
        MatrixTileCompilerExampleSupport.Run(
            "MTILE_STORE emitted through CompileMtileStore/AppAsmFacade.MtileStore.",
            "CompileMtileStore",
            Instruction.MTILE_STORE,
            facade => facade.MtileStore(
                CompilerMatrixTileTileOperand.Create(1),
                MatrixTileCompilerExampleSupport.CreateDescriptor(),
                MatrixTileCompilerExampleSupport.CreateMemoryFault(0x7100)),
            expectedNumericPolicy: null,
            expectedLayoutPolicy: null);
}
