using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Smt;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class SmtSyncOpcodeEncodingExample : ICpuExample
{
    public string Name => "smt-sync-opcode-encoding";

    public string Description => "Encodes SMT/VT synchronization opcodes and reports their instruction/serialization classes.";

    public string Category => "12_Smt/03_SmtSynchronization";

    public CpuExampleResult Run()
    {
        Instruction[] opcodes =
        [
            Instruction.YIELD,
            Instruction.WFE,
            Instruction.SEV,
            Instruction.POD_BARRIER,
            Instruction.VT_BARRIER
        ];

        foreach (Instruction opcode in opcodes)
        {
            VLIW_Instruction instruction = new()
            {
                OpCode = (uint)opcode,
                PredicateMask = 0
            };

            SmtExampleDescriber.ExpectValidInstruction(in instruction);
        }

        return CpuExampleResult.Ok(
            "Expected YIELD to be free and WFE/SEV/POD_BARRIER/VT_BARRIER to be full-serial SMT/VT synchronization carriers.",
            notes:
            [
                .. SmtExampleDescriber.DescribeOpcodes(opcodes),
                "Raw instruction fields are valid, but MinimalAsmApp does not execute privileged SMT sync flows as runnable programs."
            ]);
    }
}
