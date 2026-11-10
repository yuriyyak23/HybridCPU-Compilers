using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Top-level IR container for one virtual-thread instruction stream.
    /// </summary>
    public sealed record IrProgram(
        byte VirtualThreadId,
        IReadOnlyList<IrInstruction> Instructions,
        ControlFlowGraph ControlFlowGraph,
        IReadOnlyList<IrProgramLabel> Labels,
        IReadOnlyList<IrEntryPointMetadata> EntryPoints,
        IReadOnlyList<IrSection> Sections,
        IReadOnlyList<IrFunction> Functions,
        IrProgramSymbols Symbols)
    {
        public IrCanonicalProgramContractV1 Contract { get; init; } = IrCanonicalProgramContractV1.NativeV1;

        public IrValueFlowGraphV1 ValueFlow { get; init; } = IrValueFlowGraphV1.Empty;

        /// <summary>Frontend-neutral facts; never frontend handles or target-legality overrides.</summary>
        public IrFrontendAnalysisEvidenceSetV1 FrontendEvidence { get; init; } =
            IrFrontendAnalysisEvidenceSetV1.Empty;

        /// <summary>
        /// Basic blocks projected from the program control-flow graph.
        /// </summary>
        public IReadOnlyList<IrBasicBlock> BasicBlocks => ControlFlowGraph.Blocks;
    }
}
