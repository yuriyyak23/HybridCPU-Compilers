using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Threading;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Descriptors;

namespace MinimalAsmApp.Examples.InstructionClosure;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class Lane7AccelSubmitDescriptorIntentExample : ICpuExample
{
    public string Name => "lane7-accel-submit-intent";

    public string Description => "Emits the scoped ACCEL_SUBMIT descriptor-intent carrier through CompileAcceleratorSubmit.";

    public string Category => "13_InstructionClosure";

    public CpuExampleResult Run()
    {
        AcceleratorCommandDescriptor descriptor =
            NonVmxCompilerExampleSupport.CreateAcceleratorSubmitDescriptor();
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(
            checked((byte)descriptor.OwnerBinding.OwnerVirtualThreadId))
        {
            DomainTag = descriptor.OwnerBinding.DomainTag
        };

        CompilerAcceleratorLoweringDecision decision =
            context.CompileAcceleratorSubmit(
                IrAcceleratorIntent.ForMatMul(
                    descriptor,
                    tokenDestinationRegister: 9),
                CompilerAcceleratorCapabilityModel.ReferenceMatMul);
        if (!decision.EmitsAcceleratorSubmit)
        {
            throw new InvalidOperationException(decision.Reason);
        }

        VLIW_Instruction[] instructions = context.GetCompiledInstructions().ToArray();
        if (instructions.Length != 1 ||
            (Instruction)instructions[0].OpCode != Instruction.ACCEL_SUBMIT)
        {
            throw new InvalidOperationException("Expected one scoped ACCEL_SUBMIT carrier.");
        }

        if (!VLIW_Instruction.TryUnpackArchRegs(instructions[0].Word1, out byte rd, out byte rs1, out byte rs2) ||
            rd != 9 ||
            rs1 != VLIW_Instruction.NoArchReg ||
            rs2 != VLIW_Instruction.NoArchReg)
        {
            throw new InvalidOperationException("ACCEL_SUBMIT carrier did not preserve the token destination register ABI.");
        }

        VliwBundleAnnotations annotations = context.GetBundleAnnotations();
        if (!annotations.TryGetInstructionSlotMetadata(0, out InstructionSlotMetadata metadata) ||
            metadata.AcceleratorCommandDescriptor is null ||
            metadata.DmaStreamComputeDescriptor is not null)
        {
            throw new InvalidOperationException("ACCEL_SUBMIT descriptor sideband was not preserved on the compiler carrier.");
        }

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = compiledProgram.BundleLayout.Program.Instructions.Single();
        if (ir.AcceleratorCommandDescriptor is null ||
            ir.DmaStreamComputeDescriptor is not null)
        {
            throw new InvalidOperationException("Expected ACCEL_SUBMIT IR to carry only Lane7 accelerator descriptor sideband.");
        }

        return CpuExampleResult.Ok(
            "Compiler emitted the approved ACCEL_SUBMIT descriptor-intent carrier; direct accelerator-control helpers remain closed.",
            notes:
            [
                $"lowering decision = {decision.Mode}",
                $"descriptor operation sideband = {descriptor.Operation}",
                $"descriptor owner domain = 0x{descriptor.OwnerBinding.DomainTag:X}",
                $"token destination register = x{rd}",
                .. NonVmxCompilerExampleSupport.DescribeInstruction("ACCEL_SUBMIT carrier:", in instructions[0]),
                $"canonical compile accepted {compiledProgram.BundleLayout.Program.Instructions.Count} instruction into {compiledProgram.BundleCount} lowered bundle(s)",
                $"typed-slot agreement valid = {compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid}"
            ]);
    }
}
