using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Threading;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute;

namespace MinimalAsmApp.Examples.InstructionClosure;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class Lane6DmaStreamComputeDescriptorSubmitExample : ICpuExample
{
    public string Name => "lane6-dma-stream-descriptor-submit";

    public string Description => "Emits the scoped DmaStreamCompute descriptor carrier through CompileDmaStreamComputeDescriptor.";

    public string Category => "13_InstructionClosure";

    public CpuExampleResult Run()
    {
        DmaStreamComputeCompilerDescriptorInput input =
            NonVmxCompilerExampleSupport.CreateDmaStreamComputeDescriptorInput();
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(virtualThreadId: 0)
        {
            DomainTag = input.GuardDecision.RuntimeOwnerContext.OwnerDomainTag
        };

        DmaStreamComputeDescriptor descriptor =
            context.CompileDmaStreamComputeDescriptor(
                input.DescriptorBytes,
                input.GuardDecision,
                input.Reference);

        VLIW_Instruction[] instructions = context.GetCompiledInstructions().ToArray();
        if (instructions.Length != 1 ||
            (Instruction)instructions[0].OpCode != Instruction.DmaStreamCompute)
        {
            throw new InvalidOperationException("Expected one scoped DmaStreamCompute descriptor carrier.");
        }

        VliwBundleAnnotations annotations = context.GetBundleAnnotations();
        if (!annotations.TryGetInstructionSlotMetadata(0, out InstructionSlotMetadata metadata) ||
            metadata.DmaStreamComputeDescriptor is null ||
            !metadata.DmaStreamComputeDescriptorReference.Equals(input.Reference))
        {
            throw new InvalidOperationException("DmaStreamCompute descriptor sideband was not preserved on the compiler carrier.");
        }

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = compiledProgram.BundleLayout.Program.Instructions.Single();
        if (ir.DmaStreamComputeDescriptor is null ||
            ir.AcceleratorCommandDescriptor is not null)
        {
            throw new InvalidOperationException("Expected DmaStreamCompute IR to carry only Lane6 descriptor sideband.");
        }

        return CpuExampleResult.Ok(
            "Compiler emitted the approved DmaStreamCompute descriptor carrier; descriptor sub-op helpers remain closed.",
            notes:
            [
                $"descriptor operation sideband = {descriptor.Operation}",
                $"descriptor shape sideband = {descriptor.Shape}",
                $"descriptor owner domain = 0x{descriptor.OwnerBinding.OwnerDomainTag:X}",
                .. NonVmxCompilerExampleSupport.DescribeInstruction("DmaStreamCompute carrier:", in instructions[0]),
                $"canonical compile accepted {compiledProgram.BundleLayout.Program.Instructions.Count} instruction into {compiledProgram.BundleCount} lowered bundle(s)",
                $"typed-slot agreement valid = {compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid}"
            ]);
    }
}
