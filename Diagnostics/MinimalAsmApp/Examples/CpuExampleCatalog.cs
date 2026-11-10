using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Arithmetic;
using MinimalAsmApp.Examples.Branching;
using MinimalAsmApp.Examples.DebugTrace;
using MinimalAsmApp.Examples.Flags;
using MinimalAsmApp.Examples.HelloCpu;
using MinimalAsmApp.Examples.Loops;
using MinimalAsmApp.Examples.Memory;
using MinimalAsmApp.Examples.Programs;
using MinimalAsmApp.Examples.Registers;
using MinimalAsmApp.Examples.StackNotSupported;
using MinimalAsmApp.Examples.InstructionClosure;
using MinimalAsmApp.Examples.Stream;
using MinimalAsmApp.Examples.Smt;
using MinimalAsmApp.Examples.Vector;

namespace MinimalAsmApp.Examples;

public static class CpuExampleCatalog
{
    public static CpuExampleRunner CreateRunner()
    {
        ICpuExample[] examples =
        [
            new HelloCpuExample(),
            new Arithmetic.ImmediateShiftExample(),
            new Arithmetic.ImmediateLogicExample(),
            new Arithmetic.RemainderExample(),
            new Arithmetic.CanonicalRemainderExample(),
            new Registers.UpperImmediateExample(),
            new LoadImmediateExample(),
            new RegisterMoveExample(),
            new AddSubExample(),
            new MulDivExample(),
            new BitwiseExample(),
            new MemoryLoadStoreExample(),
            new MemoryCopyExample(),
            new CompareSetExample(),
            new ConditionalBranchNotTakenExample(),
            new JumpNotSupportedExample(),
            new CounterLoopNotSupportedExample(),
            new SumLoopNotSupportedExample(),
            new StackNotSupportedExample(),
            new ZeroFlagAlternativeExample(),
            new CarryFlagNotSupportedExample(),
            new SumOneToNExample(),
            new FactorialExample(),
            new FibonacciExample(),
            new ArraySumExample(),
            new MaxOfTwoNotSupportedExample(),
            new StepByStepTraceExample(),
            new RegisterDumpExample(),
            new MemoryDumpExample(),
            new VectorAddExample(),
            new VectorMulExample(),
            new VectorMinMaxExample(),
            new VectorReductionSumExample(),
            new VectorCompareMaskExample(),
            new Vector2DAddressingExample(),
            new VectorTransferEncodingExample(),
            new StreamControlEncodingExample(),
            new StreamControlNotSupportedExample(),
            new DmaStreamComputeNotSupportedExample(),
            new FourWayVliwSmtLayoutExample(),
            new VirtualThreadIdRangeExample(),
            new FspStealableSlotsExample(),
            new FspProtectedSlotsExample(),
            new FspDonorVtHintExample(),
            new FspBoundaryMetadataExample(),
            new BundleMixedPolicyExample(),
            new SmtSyncOpcodeEncodingExample(),
            new SmtObservationSurfaceExample(),
            new ExecutableSmtProgramNotSupportedExample(),
            new ScalarExtensionClosureExample(),
            new NonVmxBitCountExample(),
            new NonVmxConditionalZeroExample(),
            new NonVmxRotateExample(),
            new NonVmxBooleanInvertExample(),
            new NonVmxScalarMinMaxExample(),
            new NonVmxByteBitReverseExample(),
            new NonVmxScalarExtensionExample(),
            new NonVmxBitfieldExample(),
            new NonVmxAddressGenerationExample(),
            new NonVmxCarrylessMultiplyExample(),
            new NonVmxRdcycleExample(),
            new Lane6DmaStreamComputeDescriptorSubmitExample(),
            new Lane6DmaStreamComputeDirectSubmitExample(),
            new Lane7AccelSubmitDescriptorIntentExample(),
            new AtomicWordClosureExample(),
            new AtomicDoublewordClosureExample(),
            new FenceOrderingClosureExample(),
            new Arithmetic.WordArithmeticExample(),
            new Arithmetic.UpperMultiplyDivideExample(),
            new Arithmetic.RegisterShiftExample(),
            new Memory.ByteHalfwordMemoryExample(),
            new Memory.TypedWordMemoryEncodingExample(),
            
            new ControlFlow.JumpExample(),
            new ControlFlow.BranchExample(),
            
            new System.VSetVlExample(),
            new System.CsrAccessExample(),
            new System.AccelControlExample(),
            
            new Privileged.TrapsAndReturnsExample(),
            new Privileged.VmxBoundaryExample(),

            new Vector.VectorShiftExample(),
            new Vector.VectorBitManipExample(),
            new Vector.VectorCompareExtendedExample(),
            new Vector.VectorMaskLogicExample(),
            new Vector.VectorArithmeticExtendedEncodingExample(),
            new Vector.VectorFmaEncodingExample(),
            new Vector.VectorReductionExtendedEncodingExample(),
            new Vector.VectorPrefixMovementEncodingExample(),
            new Vector.VectorPermutationExtendedEncodingExample(),
            new Vector.MatrixTileEncodingExample(),
            new Vector.MatrixTileLoadCompilerExample(),
            new Vector.MatrixTileStoreCompilerExample(),
            new Vector.MatrixTileMaccCompilerExample(),
            new Vector.MatrixTileTransposeCompilerExample(),
            new Stream.DscControlEncodingExample(),
            
            new StackNotSupportedExample(),
            new ZeroFlagAlternativeExample(),
            new CarryFlagNotSupportedExample(),
            new SumOneToNExample(),
            new FactorialExample(),
            new FibonacciExample(),
            new ArraySumExample(),
            new MaxOfTwoNotSupportedExample(),
            new StepByStepTraceExample(),
            new RegisterDumpExample(),
            new MemoryDumpExample(),
            new VectorAddExample(),
            new VectorMulExample(),
            new VectorMinMaxExample(),
            new VectorReductionSumExample(),
            new VectorCompareMaskExample(),
            new Vector2DAddressingExample(),
            new VectorTransferEncodingExample(),
            new StreamControlEncodingExample(),
            new StreamControlNotSupportedExample(),
            new DmaStreamComputeNotSupportedExample(),
            new FourWayVliwSmtLayoutExample(),
            new VirtualThreadIdRangeExample(),
            new FspStealableSlotsExample(),
            new FspProtectedSlotsExample(),
            new FspDonorVtHintExample(),
            new FspBoundaryMetadataExample(),
            new BundleMixedPolicyExample(),
            new SmtSyncOpcodeEncodingExample(),
            new SmtObservationSurfaceExample(),
            new ExecutableSmtProgramNotSupportedExample(),
            new ScalarExtensionClosureExample(),
            new NonVmxBitCountExample(),
            new NonVmxConditionalZeroExample(),
            new NonVmxRotateExample(),
            new NonVmxBooleanInvertExample(),
            new NonVmxScalarMinMaxExample(),
            new NonVmxByteBitReverseExample(),
            new NonVmxScalarExtensionExample(),
            new NonVmxBitfieldExample(),
            new NonVmxAddressGenerationExample(),
            new NonVmxCarrylessMultiplyExample(),
            new NonVmxRdcycleExample(),
            new Lane6DmaStreamComputeDescriptorSubmitExample(),
            new Lane6DmaStreamComputeDirectSubmitExample(),
            new Lane7AccelSubmitDescriptorIntentExample(),
            new AtomicWordClosureExample(),
            new AtomicDoublewordClosureExample(),
            new FenceOrderingClosureExample()
        ];

        return new CpuExampleRunner(examples);
    }
}
