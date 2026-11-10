using System.ComponentModel;
using HybridCPU.Compiler.Core.IR;
using YAKSys_Hybrid_CPU;
using HybridCPU.Compiler.Core.Threading;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU.Compiler.Core.API.Facade;

/// <summary>
/// Expert backend facade — raw VLIW emission + typed-slot diagnostic (read-only).
/// Not a stable API contract — internal tooling only.
/// </summary>
[Obsolete(AsmFacadeDeprecation.Message)]
[EditorBrowsable(EditorBrowsableState.Never)]
public class ExpertBackendFacade : PlatformAsmFacade, IExpertBackendFacade
{
    /// <summary>
    /// Creates an ExpertBackendFacade bound to a specific core and thread compiler context.
    /// </summary>
    public ExpertBackendFacade(int coreId, HybridCpuThreadCompilerContext context)
        : base(coreId, context)
    {
    }

    // ── Raw VLIW emission ──

    public void EmitRawInstruction(uint opCode, byte dataType, byte predicate,
        ushort immediate, ulong destSrc1, ulong src2, ulong streamLength, ushort stride)
    {
        Context.CompileInstruction(
            opCode, dataType, predicate, immediate,
            destSrc1, src2, streamLength, stride,
            stealabilityPolicy: StealabilityPolicy.NotStealable);
    }

    // ── Diagnostic: typed-slot inspection (read-only) ──

    public SlotClass GetInstructionSlotClass(Processor.CPU_Core.IsaOpcode opcode)
    {
        IrOpcodeExecutionProfile profile = HybridCpuHazardModel.GetExecutionProfile(
            HybridCpuOpcodeRuntimeMapping.FromRuntime(opcode.ToInstructionsEnum()));
        return (SlotClass)(byte)IrSlotClassMapping.ToSlotClass(profile.ResourceClass);
    }

    public IrSlotBindingKind GetInstructionBindingKind(Processor.CPU_Core.IsaOpcode opcode)
    {
        IrOpcodeExecutionProfile profile = HybridCpuHazardModel.GetExecutionProfile(
            HybridCpuOpcodeRuntimeMapping.FromRuntime(opcode.ToInstructionsEnum()));
        return profile.DerivedBindingKind;
    }
}
