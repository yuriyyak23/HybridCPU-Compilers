using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;

namespace MinimalAsmApp.Examples.Stream;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class StreamControlEncodingExample : ICpuExample
{
    private const ulong DescriptorAddress = 0x6000;

    public string Name => "stream-control-encoding";

    public string Description => "Encodes STREAM_SETUP, STREAM_START and STREAM_WAIT system carriers.";

    public string Category => "11_Stream";

    public CpuExampleResult Run()
    {
        VLIW_Instruction setup = InstructionEncoder.EncodeSystem(
            (uint)Instruction.STREAM_SETUP,
            reg1: 1,
            param1: DescriptorAddress,
            param2: 4);

        VLIW_Instruction start = InstructionEncoder.EncodeSystem(
            (uint)Instruction.STREAM_START,
            reg1: 1,
            param1: DescriptorAddress,
            param2: 0);

        VLIW_Instruction wait = InstructionEncoder.EncodeSystem(
            (uint)Instruction.STREAM_WAIT,
            reg1: 1,
            param1: DescriptorAddress,
            param2: 0);

        CpuInstructionDescriber.ExpectValid(in setup);
        CpuInstructionDescriber.ExpectValid(in start);
        CpuInstructionDescriber.ExpectValid(in wait);

        return CpuExampleResult.Ok(
            "Encoded stream control carriers for descriptor 0x6000. Execution remains fail-closed in the current dispatcher.",
            notes:
            [
                "STREAM_SETUP:",
                .. CpuInstructionDescriber.Describe(in setup),
                "STREAM_START:",
                .. CpuInstructionDescriber.Describe(in start),
                "STREAM_WAIT:",
                .. CpuInstructionDescriber.Describe(in wait)
            ]);
    }
}
