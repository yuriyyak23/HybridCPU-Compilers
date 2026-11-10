using System.ComponentModel;
using HybridCPU.Compiler.Core.IR;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU.Compiler.Core.API.Facade;

/// <summary>
/// Expert backend facade — raw VLIW emission + typed-slot diagnostic (read-only).
/// Not a stable API contract — internal tooling only.
/// </summary>
[Obsolete(AsmFacadeDeprecation.Message)]
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IExpertBackendFacade : IPlatformAsmFacade
{
    // ── Raw VLIW emission (current Add_VLIW_Instruction API) ──
    void EmitRawInstruction(uint opCode, byte dataType, byte predicate,
        ushort immediate, ulong destSrc1, ulong src2, ulong streamLength, ushort stride);

    // ── Diagnostic: typed-slot inspection (read-only) ──
    SlotClass GetInstructionSlotClass(Processor.CPU_Core.IsaOpcode opcode);
    IrSlotBindingKind GetInstructionBindingKind(Processor.CPU_Core.IsaOpcode opcode);
}
