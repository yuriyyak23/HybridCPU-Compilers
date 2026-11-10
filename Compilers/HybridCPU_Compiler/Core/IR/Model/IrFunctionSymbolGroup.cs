using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Groups function ownership with the symbols and blocks projected into that function.
    /// </summary>
    public sealed record IrFunctionSymbolGroup(
        IrFunction Function,
        IReadOnlyList<IrBasicBlock> Blocks,
        IReadOnlyList<IrProgramLabel> Labels,
        IReadOnlyList<IrEntryPointMetadata> EntryPoints);
}
