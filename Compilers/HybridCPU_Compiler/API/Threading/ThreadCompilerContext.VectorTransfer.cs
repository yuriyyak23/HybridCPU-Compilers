using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Lowering;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU.Compiler.Core.Threading
{
    public partial class HybridCpuThreadCompilerContext
    {
        [Obsolete(
            "This compatibility facade returns a raw VectorTransfer plan artifact. Use CompileVloadWithDecision for CompilerLoweringDecision no-authority metadata.",
            false)]
        public CompilerVectorTransferEmissionPlan CompileVload(
            CompilerVectorTransferMemoryAddressAbi destination,
            CompilerVectorTransferMemoryAddressAbi source,
            CompilerVectorTransferShapeAbi shape,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            return CompileVloadWithDecision(
                    destination,
                    source,
                    shape,
                    stealabilityPolicy)
                .Plan;
        }

        public CompilerPositiveEmissionResult<CompilerVectorTransferEmissionPlan> CompileVloadWithDecision(
            CompilerVectorTransferMemoryAddressAbi destination,
            CompilerVectorTransferMemoryAddressAbi source,
            CompilerVectorTransferShapeAbi shape,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            CompilerPositiveEmissionResult<CompilerVectorTransferEmissionPlan> result =
                CompilerVectorTransferEmissionLowerer.LowerWithDecision(
                    CompilerVectorTransferEmissionRequest.Vload(
                        destination,
                        source,
                        shape));
            AppendVectorTransferInstruction(result.Plan, stealabilityPolicy);
            return result;
        }

        [Obsolete(
            "This compatibility facade returns a raw VectorTransfer plan artifact. Use CompileVstoreWithDecision for CompilerLoweringDecision no-authority metadata.",
            false)]
        public CompilerVectorTransferEmissionPlan CompileVstore(
            CompilerVectorTransferMemoryAddressAbi source,
            CompilerVectorTransferMemoryAddressAbi destination,
            CompilerVectorTransferShapeAbi shape,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            return CompileVstoreWithDecision(
                    source,
                    destination,
                    shape,
                    stealabilityPolicy)
                .Plan;
        }

        public CompilerPositiveEmissionResult<CompilerVectorTransferEmissionPlan> CompileVstoreWithDecision(
            CompilerVectorTransferMemoryAddressAbi source,
            CompilerVectorTransferMemoryAddressAbi destination,
            CompilerVectorTransferShapeAbi shape,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            CompilerPositiveEmissionResult<CompilerVectorTransferEmissionPlan> result =
                CompilerVectorTransferEmissionLowerer.LowerWithDecision(
                    CompilerVectorTransferEmissionRequest.Vstore(
                        source,
                        destination,
                        shape));
            AppendVectorTransferInstruction(result.Plan, stealabilityPolicy);
            return result;
        }

        private void AppendVectorTransferInstruction(
            CompilerVectorTransferEmissionPlan plan,
            StealabilityPolicy stealabilityPolicy)
        {
            EnsureInstructionCapacity();

            VLIW_Instruction instruction = Runtime.NativeTransportRuntimeAdapter.ToRuntime(plan.EncodedInstruction);
            instruction.VirtualThreadId = _virtualThreadId.Value;

            _instructions[_instructionCount] = instruction;
            _instructionSlotMetadata[_instructionCount] = new InstructionSlotMetadata(
                _virtualThreadId,
                BuildSlotMetadata((uint)plan.Request.Opcode, stealabilityPolicy, _domainTag));
            _instructionCount++;
            InvalidateCanonicalCompileCache();
        }
    }
}
