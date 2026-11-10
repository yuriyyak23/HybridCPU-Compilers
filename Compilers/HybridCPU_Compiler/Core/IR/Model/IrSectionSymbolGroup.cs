using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Groups section ownership with the symbols and blocks projected into that section.
    /// </summary>
    public sealed record IrSectionSymbolGroup(
        IrSection Section,
        IReadOnlyList<IrBasicBlock> Blocks,
        IReadOnlyList<IrFunction> Functions,
        IReadOnlyList<IrProgramLabel> Labels,
        IReadOnlyList<IrEntryPointMetadata> EntryPoints);
}
