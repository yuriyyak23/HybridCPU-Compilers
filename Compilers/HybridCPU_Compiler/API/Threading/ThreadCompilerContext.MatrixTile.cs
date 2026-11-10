using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Matrix;
using HybridCPU.Compiler.Core.IR.Lowering;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU.Compiler.Core.Threading
{
    public partial class HybridCpuThreadCompilerContext
    {
        [Obsolete(
            "This compatibility facade returns a raw MatrixTile plan artifact. Use CompileMtileLoadWithDecision for CompilerLoweringDecision no-authority metadata.",
            false)]
        public CompilerMatrixTileEmissionPlan CompileMtileLoad(
            CompilerMatrixTileTileOperand destinationTile,
            CompilerMatrixTileDescriptorAbi descriptor,
            CompilerMatrixTileMemoryFaultAbiInputs memoryFaultAbi,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            return CompileMtileLoadWithDecision(
                    destinationTile,
                    descriptor,
                    memoryFaultAbi,
                    stealabilityPolicy)
                .Plan;
        }

        public CompilerPositiveEmissionResult<CompilerMatrixTileEmissionPlan> CompileMtileLoadWithDecision(
            CompilerMatrixTileTileOperand destinationTile,
            CompilerMatrixTileDescriptorAbi descriptor,
            CompilerMatrixTileMemoryFaultAbiInputs memoryFaultAbi,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            CompilerPositiveEmissionResult<CompilerMatrixTileEmissionPlan> result =
                CompilerMatrixTileEmissionLowerer.LowerWithDecision(
                    CompilerMatrixTileEmissionRequest.MtileLoad(
                        destinationTile,
                        descriptor,
                        memoryFaultAbi));
            AppendMatrixTileInstruction(result.Plan, stealabilityPolicy);
            return result;
        }

        [Obsolete(
            "This compatibility facade returns a raw MatrixTile plan artifact. Use CompileMtileStoreWithDecision for CompilerLoweringDecision no-authority metadata.",
            false)]
        public CompilerMatrixTileEmissionPlan CompileMtileStore(
            CompilerMatrixTileTileOperand sourceTile,
            CompilerMatrixTileDescriptorAbi descriptor,
            CompilerMatrixTileMemoryFaultAbiInputs memoryFaultAbi,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            return CompileMtileStoreWithDecision(
                    sourceTile,
                    descriptor,
                    memoryFaultAbi,
                    stealabilityPolicy)
                .Plan;
        }

        public CompilerPositiveEmissionResult<CompilerMatrixTileEmissionPlan> CompileMtileStoreWithDecision(
            CompilerMatrixTileTileOperand sourceTile,
            CompilerMatrixTileDescriptorAbi descriptor,
            CompilerMatrixTileMemoryFaultAbiInputs memoryFaultAbi,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            CompilerPositiveEmissionResult<CompilerMatrixTileEmissionPlan> result =
                CompilerMatrixTileEmissionLowerer.LowerWithDecision(
                    CompilerMatrixTileEmissionRequest.MtileStore(
                        sourceTile,
                        descriptor,
                        memoryFaultAbi));
            AppendMatrixTileInstruction(result.Plan, stealabilityPolicy);
            return result;
        }

        [Obsolete(
            "This compatibility facade returns a raw MatrixTile plan artifact. Use CompileMtileMaccWithDecision for CompilerLoweringDecision no-authority metadata.",
            false)]
        public CompilerMatrixTileEmissionPlan CompileMtileMacc(
            CompilerMatrixTileTileOperand leftSourceTile,
            CompilerMatrixTileTileOperand rightSourceTile,
            CompilerMatrixTileTileOperand accumulatorTile,
            CompilerMatrixTileDescriptorAbi leftSourceDescriptor,
            CompilerMatrixTileAccumulatorPolicyAbi accumulatorPolicyAbi,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            return CompileMtileMaccWithDecision(
                    leftSourceTile,
                    rightSourceTile,
                    accumulatorTile,
                    leftSourceDescriptor,
                    accumulatorPolicyAbi,
                    stealabilityPolicy)
                .Plan;
        }

        public CompilerPositiveEmissionResult<CompilerMatrixTileEmissionPlan> CompileMtileMaccWithDecision(
            CompilerMatrixTileTileOperand leftSourceTile,
            CompilerMatrixTileTileOperand rightSourceTile,
            CompilerMatrixTileTileOperand accumulatorTile,
            CompilerMatrixTileDescriptorAbi leftSourceDescriptor,
            CompilerMatrixTileAccumulatorPolicyAbi accumulatorPolicyAbi,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            CompilerPositiveEmissionResult<CompilerMatrixTileEmissionPlan> result =
                CompilerMatrixTileEmissionLowerer.LowerWithDecision(
                    CompilerMatrixTileEmissionRequest.MtileMacc(
                        leftSourceTile,
                        rightSourceTile,
                        accumulatorTile,
                        leftSourceDescriptor,
                        accumulatorPolicyAbi));
            AppendMatrixTileInstruction(result.Plan, stealabilityPolicy);
            return result;
        }

        [Obsolete(
            "This compatibility facade returns a raw MatrixTile plan artifact. Use CompileMtransposeWithDecision for CompilerLoweringDecision no-authority metadata.",
            false)]
        public CompilerMatrixTileEmissionPlan CompileMtranspose(
            CompilerMatrixTileTileOperand sourceTile,
            CompilerMatrixTileTileOperand destinationTile,
            CompilerMatrixTileDescriptorAbi sourceDescriptor,
            CompilerMatrixTileTransposePolicyAbi transposePolicyAbi,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            return CompileMtransposeWithDecision(
                    sourceTile,
                    destinationTile,
                    sourceDescriptor,
                    transposePolicyAbi,
                    stealabilityPolicy)
                .Plan;
        }

        public CompilerPositiveEmissionResult<CompilerMatrixTileEmissionPlan> CompileMtransposeWithDecision(
            CompilerMatrixTileTileOperand sourceTile,
            CompilerMatrixTileTileOperand destinationTile,
            CompilerMatrixTileDescriptorAbi sourceDescriptor,
            CompilerMatrixTileTransposePolicyAbi transposePolicyAbi,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            CompilerPositiveEmissionResult<CompilerMatrixTileEmissionPlan> result =
                CompilerMatrixTileEmissionLowerer.LowerWithDecision(
                    CompilerMatrixTileEmissionRequest.Mtranspose(
                        sourceTile,
                        destinationTile,
                        sourceDescriptor,
                        transposePolicyAbi));
            AppendMatrixTileInstruction(result.Plan, stealabilityPolicy);
            return result;
        }

        private void AppendMatrixTileInstruction(
            CompilerMatrixTileEmissionPlan plan,
            StealabilityPolicy stealabilityPolicy)
        {
            EnsureInstructionCapacity();

            VLIW_Instruction instruction = Runtime.NativeTransportRuntimeAdapter.ToRuntime(plan.EncodedInstruction);
            instruction.VirtualThreadId = _virtualThreadId.Value;

            _instructions[_instructionCount] = instruction;
            _instructionSlotMetadata[_instructionCount] = new InstructionSlotMetadata(
                _virtualThreadId,
                BuildSlotMetadata((uint)plan.Request.Opcode, stealabilityPolicy, _domainTag))
            {
                MatrixTileNumericPolicy = plan.MatrixTileNumericPolicy is { } numericPolicy
                    ? Runtime.NativeTransportRuntimeAdapter.ToRuntime(numericPolicy)
                    : null,
                MatrixTileLayoutPolicy = plan.MatrixTileLayoutPolicy is { } layoutPolicy
                    ? Runtime.NativeTransportRuntimeAdapter.ToRuntime(layoutPolicy)
                    : null
            };
            _instructionCount++;
            InvalidateCanonicalCompileCache();
        }

        private static void ValidateNoDirectMatrixTileEmission(uint opCode)
        {
            if (CompilerMatrixTilePositiveEmissionAbiContract.IsMatrixTilePositiveOpcode(opCode))
            {
                throw new InvalidOperationException(
                    "MTILE compiler emission requires the typed matrix/tile helper ABI; raw CompileInstruction/EmitRawInstruction transport is not emission authority.");
            }
        }
    }
}
