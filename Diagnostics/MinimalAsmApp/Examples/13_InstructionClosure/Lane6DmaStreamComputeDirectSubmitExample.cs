using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Threading;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute;

namespace MinimalAsmApp.Examples.InstructionClosure;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class Lane6DmaStreamComputeDirectSubmitExample : ICpuExample
{
    public string Name => "lane6-dma-stream-direct-submit";

    public string Description => "Emits the scoped DmaStreamCompute descriptor carrier through CompileDmaStreamCompute.";

    public string Category => "13_InstructionClosure";

    public CpuExampleResult Run()
    {
        DmaStreamComputeDescriptor descriptor =
            NonVmxCompilerExampleSupport.CreateDmaStreamComputeDescriptor();
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(
            checked((byte)descriptor.OwnerBinding.OwnerVirtualThreadId))
        {
            DomainTag = descriptor.OwnerBinding.OwnerDomainTag
        };

        context.CompileDmaStreamCompute(
            descriptor,
            DmaStreamComputeCompilerAdoptionMode.Strict);

        VLIW_Instruction[] instructions = context.GetCompiledInstructions().ToArray();
        if (instructions.Length != 1 ||
            (Instruction)instructions[0].OpCode != Instruction.DmaStreamCompute)
        {
            throw new InvalidOperationException("Expected one scoped DmaStreamCompute direct-submit carrier.");
        }

        VliwBundleAnnotations annotations = context.GetBundleAnnotations();
        if (!annotations.TryGetInstructionSlotMetadata(0, out InstructionSlotMetadata metadata) ||
            !ReferenceEquals(metadata.DmaStreamComputeDescriptor, descriptor) ||
            !metadata.DmaStreamComputeDescriptorReference.Equals(descriptor.DescriptorReference) ||
            metadata.AcceleratorCommandDescriptor is not null)
        {
            throw new InvalidOperationException("DmaStreamCompute direct-submit descriptor sideband was not preserved.");
        }

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = compiledProgram.BundleLayout.Program.Instructions.Single();
        if (!ReferenceEquals(ir.DmaStreamComputeDescriptor, descriptor) ||
            ir.AcceleratorCommandDescriptor is not null)
        {
            throw new InvalidOperationException("Expected direct-submit DmaStreamCompute IR to carry only Lane6 descriptor sideband.");
        }

        return CpuExampleResult.Ok(
            "Compiler emitted the approved DmaStreamCompute direct descriptor carrier; descriptor sub-op helpers remain closed.",
            notes:
            [
                "compiler entrypoint = HybridCpuThreadCompilerContext.CompileDmaStreamCompute",
                $"adoption mode = {DmaStreamComputeCompilerAdoptionMode.Strict}",
                $"descriptor operation sideband = {descriptor.Operation}",
                $"descriptor shape sideband = {descriptor.Shape}",
                $"descriptor reference address = 0x{descriptor.DescriptorReference.DescriptorAddress:X}",
                $"descriptor owner domain = 0x{descriptor.OwnerBinding.OwnerDomainTag:X}",
                .. NonVmxCompilerExampleSupport.DescribeInstruction("DmaStreamCompute direct-submit carrier:", in instructions[0]),
                $"canonical compile accepted {compiledProgram.BundleLayout.Program.Instructions.Count} instruction into {compiledProgram.BundleCount} lowered bundle(s)",
                $"typed-slot agreement valid = {compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid}"
            ]);
    }
}
