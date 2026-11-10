using System;
using System.Collections.Generic;
using System.Linq;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCpuCanonicalCompiler = HybridCPU.Compiler.Core.Runtime.NativeTransportRuntimeAdapter;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Tests.MemoryAccelerators;
using HybridCPU_ISE.Tests.TestHelpers;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Decoder;
using YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Descriptors;
using YAKSys_Hybrid_CPU.Core.Pipeline.MicroOps;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;
using TypedSlotBundleFacts = HybridCPU.Compiler.Core.IR.IrTypedSlotBundleFacts;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class L7SdcCompilerEmissionTests
{
    [Fact]
    public void CoarseMatMulIntent_EmitsLane7AccelSubmitWithDescriptorSideband()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        HybridCpuThreadCompilerContext context = CreateContextForDescriptor(descriptor);

        CompilerAcceleratorLoweringDecision decision =
            context.CompileAcceleratorSubmit(
                IrAcceleratorIntent.ForMatMul(descriptor, tokenDestinationRegister: 9),
                CompilerAcceleratorCapabilityModel.ReferenceMatMul);

        Assert.True(decision.EmitsAcceleratorSubmit);
        Assert.Equal(1, context.InstructionCount);

        VLIW_Instruction raw = Assert.Single(context.GetCompiledInstructions().ToArray());
        Assert.Equal(InstructionsEnum.ACCEL_SUBMIT, (InstructionsEnum)raw.OpCode);
        Assert.Equal(0, raw.Reserved);
        Assert.Equal(0, raw.VirtualThreadId);
        Assert.Equal(0UL, raw.Src2Pointer);
        Assert.True(VLIW_Instruction.TryUnpackArchRegs(raw.Word1, out byte rd, out byte rs1, out byte rs2));
        Assert.Equal(9, rd);
        Assert.Equal(VLIW_Instruction.NoArchReg, rs1);
        Assert.Equal(VLIW_Instruction.NoArchReg, rs2);

        VliwBundleAnnotations sourceAnnotations = context.GetBundleAnnotations();
        Assert.True(sourceAnnotations.TryGetInstructionSlotMetadata(0, out InstructionSlotMetadata sourceMetadata));
        Assert.Same(descriptor, sourceMetadata.AcceleratorCommandDescriptor);
        Assert.Null(sourceMetadata.DmaStreamComputeDescriptor);
        Assert.Equal(SlotClass.SystemSingleton, (SlotClass)(byte)sourceMetadata.SlotMetadata.AdmissionMetadata.Placement.RequiredSlotClass);
        Assert.Equal(SlotPinningKind.HardPinned, sourceMetadata.SlotMetadata.AdmissionMetadata.Placement.PinningKind);
        Assert.Equal(7, sourceMetadata.SlotMetadata.AdmissionMetadata.Placement.PinnedLaneId);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(InstructionsEnum.ACCEL_SUBMIT, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(InstructionClass.System, (InstructionClass)(byte)ir.InstructionClass);
        Assert.Equal(SerializationClass.MemoryOrdered, (SerializationClass)(byte)ir.SerializationClass);
        Assert.Equal(IrResourceClass.System, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.SystemSingleton, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Equal(IrIssueSlotMask.Slot7, ir.Annotation.LegalSlots);
        Assert.Same(descriptor, ir.AcceleratorCommandDescriptor);
        Assert.Null(ir.DmaStreamComputeDescriptor);

        IrMaterializedBundle materializedBundle =
            Assert.Single(compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles));
        Assert.True(materializedBundle.TryGetSlotForInstruction(ir.Index, out IrMaterializedBundleSlot? slot));
        Assert.NotNull(slot);
        Assert.Equal(7, slot!.SlotIndex);

        TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(materializedBundle);
        Assert.Equal(1, facts.SystemSingletonCount);
        Assert.Equal(0, facts.BranchControlCount);
        Assert.Equal(0, facts.DmaStreamCount);
        Assert.True(compiledProgram.AdmissibilityAgreement.AllTypedSlotFactsValid);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(7);
        Assert.Equal(InstructionsEnum.ACCEL_SUBMIT, (InstructionsEnum)lowered.OpCode);
        Assert.Equal(0, lowered.Reserved);
        Assert.Equal(0, lowered.VirtualThreadId);
        Assert.Equal(0UL, lowered.Src2Pointer);

        IrBundleAnnotations loweredAnnotations = Assert.Single(compiledProgram.LoweredBundleAnnotations);
        Assert.True(loweredAnnotations.TryGetInstructionSlotMetadata(7, out IrInstructionSlotMetadata loweredMetadata));
        Assert.Same(descriptor, loweredMetadata.AcceleratorCommandDescriptor);

        MicroOp projected = DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            loweredAnnotations,
            slotIndex: 7);
        AcceleratorSubmitMicroOp submit = Assert.IsType<AcceleratorSubmitMicroOp>(projected);
        Assert.Same(descriptor, submit.CommandDescriptor);
        Assert.Equal(SlotClass.SystemSingleton, (SlotClass)(byte)submit.Placement.RequiredSlotClass);
        Assert.Equal(SlotPinningKind.HardPinned, submit.Placement.PinningKind);
        Assert.Equal(7, submit.Placement.PinnedLaneId);
        Assert.True(submit.WritesRegister);
        Assert.Equal(new[] { 9 }, submit.WriteRegisters);

        _ = new Processor(ProcessorMode.Emulation);
        _ = NativeTransportRuntimeAdapter.EmitProgram(compiledProgram, baseAddress: 0);
        Assert.True(Processor.MainMemory.TryReadVliwBundleAnnotations(
            0,
            out VliwBundleAnnotations? emittedAnnotations));
        Assert.NotNull(emittedAnnotations);
        Assert.True(emittedAnnotations!.TryGetInstructionSlotMetadata(
            7,
            out InstructionSlotMetadata emittedMetadata));
        Assert.Same(descriptor, emittedMetadata.AcceleratorCommandDescriptor);
    }

    [Fact]
    public void ExplicitIntentOnly_DirectCompilerEmissionOfAccelOpcodeRejects()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => context.CompileInstruction(
                opCode: (uint)InstructionsEnum.ACCEL_SUBMIT,
                dataType: 0,
                predicate: 0,
                immediate: 0,
                destSrc1: VLIW_Instruction.PackArchRegs(
                    VLIW_Instruction.NoArchReg,
                    VLIW_Instruction.NoArchReg,
                    VLIW_Instruction.NoArchReg),
                src2: 0,
                streamLength: 0,
                stride: 0,
                stealabilityPolicy: StealabilityPolicy.NotStealable));

        Assert.Contains("explicit accelerator intent", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, context.InstructionCount);
    }

    [Theory]
    [InlineData(InstructionsEnum.ACCEL_QUERY_CAPS)]
    [InlineData(InstructionsEnum.ACCEL_SUBMIT)]
    [InlineData(InstructionsEnum.ACCEL_POLL)]
    [InlineData(InstructionsEnum.ACCEL_WAIT)]
    [InlineData(InstructionsEnum.ACCEL_CANCEL)]
    [InlineData(InstructionsEnum.ACCEL_FENCE)]
    [InlineData(InstructionsEnum.ACCEL_STATUS)]
    public void DirectSystemDeviceCommandCompilerEmission_RejectsBeforeCarrierEmission(
        InstructionsEnum opcode)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => context.CompileInstruction(
                opCode: (uint)opcode,
                dataType: 0,
                predicate: 0,
                immediate: 0,
                destSrc1: VLIW_Instruction.PackArchRegs(
                    VLIW_Instruction.NoArchReg,
                    VLIW_Instruction.NoArchReg,
                    VLIW_Instruction.NoArchReg),
                src2: 0,
                streamLength: 0,
                stride: 0,
                stealabilityPolicy: StealabilityPolicy.NotStealable));

        Assert.Contains("explicit accelerator intent", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, context.InstructionCount);
    }

    [Fact]
    public void UnsupportedCapability_ChoosesCpuLoweringBeforeAccelSubmitEmission()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        HybridCpuThreadCompilerContext context = CreateContextForDescriptor(descriptor);

        CompilerAcceleratorLoweringDecision decision =
            context.CompileAcceleratorSubmit(
                IrAcceleratorIntent.ForMatMul(descriptor),
                CompilerAcceleratorCapabilityModel.Disabled);

        Assert.True(decision.UsesNonAcceleratorLowering);
        Assert.False(decision.EmitsAcceleratorSubmit);
        Assert.Equal(0, context.InstructionCount);
    }

    [Fact]
    public void UnsupportedProviderShape_ChoosesCpuLoweringBeforeAccelSubmitEmission()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        ulong oversizedElementCount = MatMulDescriptorValidator.MaxOutputElements + 1;
        AcceleratorCommandDescriptor oversizedDescriptor = descriptor with
        {
            ElementCount = oversizedElementCount,
            Header = descriptor.Header with { ElementCount = oversizedElementCount }
        };
        HybridCpuThreadCompilerContext context = CreateContextForDescriptor(oversizedDescriptor);

        CompilerAcceleratorLoweringDecision decision =
            context.CompileAcceleratorSubmit(
                IrAcceleratorIntent.ForMatMul(oversizedDescriptor),
                CompilerAcceleratorCapabilityModel.ReferenceMatMul);

        Assert.True(decision.UsesNonAcceleratorLowering);
        Assert.False(decision.EmitsAcceleratorSubmit);
        Assert.Equal(0, context.InstructionCount);
    }

    [Fact]
    public void MalformedAcceleratorIntent_RejectsBeforeEmission()
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);
        IrAcceleratorIntent intent = new IrAcceleratorIntent
        {
            Operation = AcceleratorOperationKind.MatMul,
            DescriptorSideband = null!
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => context.CompileAcceleratorSubmit(
                intent,
                CompilerAcceleratorCapabilityModel.ReferenceMatMul));

        Assert.Contains("descriptor sideband", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, context.InstructionCount);
    }

    [Fact]
    public void InvalidTokenDestinationRegister_RejectsBeforeEmission()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        HybridCpuThreadCompilerContext context = CreateContextForDescriptor(descriptor);
        IrAcceleratorIntent intent =
            IrAcceleratorIntent.ForMatMul(descriptor, tokenDestinationRegister: 32);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => context.CompileAcceleratorSubmit(
                intent,
                CompilerAcceleratorCapabilityModel.ReferenceMatMul));
        Assert.Equal(0, context.InstructionCount);
    }

    [Fact]
    public void UnknownAcceleratorLoweringMode_RejectsBeforeEmission()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        HybridCpuThreadCompilerContext context = CreateContextForDescriptor(descriptor);
        IrAcceleratorIntent intent =
            IrAcceleratorIntent.ForMatMul(descriptor) with
            {
                RequestedMode = (AcceleratorLoweringMode)0x7F
            };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => context.CompileAcceleratorSubmit(
                intent,
                CompilerAcceleratorCapabilityModel.ReferenceMatMul));
        Assert.Equal(0, context.InstructionCount);
    }

    [Fact]
    public void RegularStreamIntent_RemainsLane6DmaStreamCompute()
    {
        DmaStreamComputeDescriptor descriptor =
            DmaStreamComputeTestDescriptorFactory.CreateDescriptor();
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);

        context.CompileDmaStreamCompute(descriptor);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IrInstruction ir = Assert.Single(compiledProgram.BundleLayout.Program.Instructions);
        Assert.Equal(InstructionsEnum.DmaStreamCompute, (InstructionsEnum)(uint)ir.Opcode);
        Assert.Equal(IrResourceClass.DmaStream, ir.Annotation.ResourceClass);
        Assert.Equal(SlotClass.DmaStreamClass, (SlotClass)(byte)ir.Annotation.RequiredSlotClass);
        Assert.Same(descriptor, ir.DmaStreamComputeDescriptor);
        Assert.Null(ir.AcceleratorCommandDescriptor);

        HybridCpuInstructionWord lowered = compiledProgram.LoweredBundles[0].GetInstruction(6);
        Assert.Equal(InstructionsEnum.DmaStreamCompute, (InstructionsEnum)lowered.OpCode);
        Assert.True(compiledProgram.LoweredBundleAnnotations[0]
            .TryGetInstructionSlotMetadata(6, out IrInstructionSlotMetadata metadata));
        Assert.Same(descriptor, metadata.DmaStreamComputeDescriptor);
        Assert.Null(metadata.AcceleratorCommandDescriptor);
    }

    [Fact]
    public void DmaStreamSidebandOnNonDmaOpcode_RejectsAtIrBuild()
    {
        DmaStreamComputeDescriptor descriptor =
            DmaStreamComputeTestDescriptorFactory.CreateDescriptor();
        VLIW_Instruction[] raw = new[]
        {
            new VLIW_Instruction
            {
                OpCode = (uint)InstructionsEnum.ADD,
                DataTypeValue = DataTypeEnum.INT32,
                Word1 = VLIW_Instruction.PackArchRegs(1, 2, 3)
            }
        };
        VliwBundleAnnotations annotations = new VliwBundleAnnotations(new[]
        {
            InstructionSlotMetadata.Default with
            {
                DmaStreamComputeDescriptor = descriptor
            }
        });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => HybridCpuCanonicalCompiler.CompileProgram(
                virtualThreadId: 0,
                instructions: raw,
                bundleAnnotations: annotations));

        Assert.Contains("DmaStreamCompute descriptor sideband", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescriptorSidebandOnEmptySlot_RejectsAtDecoder()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        InstructionSlotMetadata[] slotMetadata = Enumerable
            .Repeat(InstructionSlotMetadata.Default, BundleMetadata.BundleSlotCount)
            .ToArray();
        slotMetadata[7] = InstructionSlotMetadata.Default.WithAcceleratorDescriptor(descriptor);
        VliwBundleAnnotations annotations = new VliwBundleAnnotations(slotMetadata);
        VLIW_Instruction[] rawSlots = new VLIW_Instruction[BundleMetadata.BundleSlotCount];

        InvalidOpcodeException ex = Assert.Throws<InvalidOpcodeException>(
            () => new VliwDecoderV4().DecodeInstructionBundle(
                rawSlots,
                annotations,
                bundleAddress: 0xD000,
                bundleSerial: 4));

        Assert.Contains("empty/NOP", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectorRejectsDmaStreamSidebandOnNonDmaInstructionIr()
    {
        DmaStreamComputeDescriptor descriptor =
            DmaStreamComputeTestDescriptorFactory.CreateDescriptor();
        VLIW_Instruction[] rawSlots = new VLIW_Instruction[BundleMetadata.BundleSlotCount];
        rawSlots[0] = new VLIW_Instruction
        {
            OpCode = (uint)InstructionsEnum.ADD,
            DataTypeValue = DataTypeEnum.INT32,
            Word1 = VLIW_Instruction.PackArchRegs(1, 2, 3)
        };
        InstructionIR instruction = new InstructionIR
        {
            CanonicalOpcode = InstructionsEnum.ADD,
            Class = InstructionClass.ScalarAlu,
            SerializationClass = SerializationClass.Free,
            Rd = 1,
            Rs1 = 2,
            Rs2 = 3,
            Imm = 0,
            DmaStreamComputeDescriptor = descriptor,
            DmaStreamComputeDescriptorReference = descriptor.DescriptorReference
        };
        DecodedInstructionBundle decoded = new DecodedInstructionBundle(
            bundleAddress: 0xD100,
            bundleSerial: 5,
            slots: new[]
            {
                DecodedInstruction.CreateOccupied(0, instruction)
            });

        MicroOp?[] carriers =
            DecodedBundleTransportProjector.BuildCanonicalCarrierBundleForTesting(rawSlots, decoded);

        TrapMicroOp trap = Assert.IsType<TrapMicroOp>(carriers[0]);
        Assert.Contains("non-DmaStreamCompute", trap.TrapReason, StringComparison.OrdinalIgnoreCase);
    }

    internal static HybridCpuThreadCompilerContext CreateContextForDescriptor(
        AcceleratorCommandDescriptor descriptor) =>
        new(checked((byte)descriptor.OwnerBinding.OwnerVirtualThreadId))
        {
            DomainTag = descriptor.OwnerBinding.DomainTag
        };

    internal static MicroOp DecodeAndProjectSingleCarrier(
        HybridCpuInstructionBundle bundle,
        IrBundleAnnotations annotations,
        int slotIndex)
    {
        VLIW_Bundle runtimeBundle = NativeTransportRuntimeAdapter.ToRuntime(in bundle);
        return DecodeAndProjectSingleCarrier(
            runtimeBundle,
            NativeTransportRuntimeAdapter.ToRuntime(annotations),
            slotIndex);
    }

    [Fact]
    public void ExternalOperationIntent_IsPreservedThroughLane7LoweringDecision()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        IrExternalOperationIntent semantic = new IrExternalOperationIntent
        {
            ExecutionRequirement = IrExternalExecutionRequirement.Required,
            ExecutionContour = IrExternalExecutionContour.SystemExternalAccelerator,
            MemoryRoles = IrExternalMemoryRegionRole.ReadWrite,
            Publication = IrExternalPublicationIntent.StagedOutputPreferred,
            Coherence = IrExternalCoherenceRequirement.Preferred,
            Effect = IrExternalEffectRequirement.Idempotent,
            Cancellation = IrExternalCancellationRequirement.ExactAcknowledgement,
            Ordering = IrExternalOrderingRequirement.Ordered
        };

        CompilerAcceleratorLoweringDecision decision =
            CompilerAcceleratorCapabilityModel.ReferenceMatMul.Decide(
                IrAcceleratorIntent.ForMatMul(descriptor) with { ExternalOperation = semantic });

        Assert.True(decision.EmitsAcceleratorSubmit);
        Assert.NotNull(decision.Command);
        Assert.Equal(semantic, decision.Command!.ExternalOperation);
        Assert.Equal(IrExternalPublicationIntent.StagedOutputPreferred,
            decision.Command.ExternalOperation.Publication);
    }

    [Fact]
    public void ExternalOperationIntent_IsPreservedInCompiledProgramOutsideTransportAnnotations()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        IrExternalOperationIntent semantic = new IrExternalOperationIntent
        {
            Publication = IrExternalPublicationIntent.StagedOutputPreferred,
            Coherence = IrExternalCoherenceRequirement.Preferred,
            Effect = IrExternalEffectRequirement.Idempotent
        };
        HybridCpuThreadCompilerContext context = CreateContextForDescriptor(descriptor);

        _ = context.CompileAcceleratorSubmit(
            IrAcceleratorIntent.ForMatMul(descriptor) with { ExternalOperation = semantic },
            CompilerAcceleratorCapabilityModel.ReferenceMatMul);
        HybridCpuCompiledProgram compiled = context.CompileProgram();

        Assert.Single(compiled.ExternalOperationIntents);
        Assert.Equal(semantic, compiled.ExternalOperationIntents[0]);
        IrBundleAnnotations annotations = Assert.Single(compiled.LoweredBundleAnnotations);
        Assert.True(annotations.TryGetInstructionSlotMetadata(7, out IrInstructionSlotMetadata slot));
        Assert.Same(descriptor, slot.AcceleratorCommandDescriptor);
        Assert.DoesNotContain("ExternalOperation", string.Join('|', slot.GetType().GetProperties().Select(static property => property.Name)));
    }

    [Fact]
    public void ExternalOperationIntent_IsShiftedWithSourceInstructionInsertion()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        IrExternalOperationIntent semantic = new IrExternalOperationIntent
        {
            Publication = IrExternalPublicationIntent.StagedOutputPreferred
        };
        HybridCpuThreadCompilerContext context = CreateContextForDescriptor(descriptor);
        _ = context.CompileAcceleratorSubmit(
            IrAcceleratorIntent.ForMatMul(descriptor) with { ExternalOperation = semantic },
            CompilerAcceleratorCapabilityModel.ReferenceMatMul);

        context.InsertInstruction(
            instructionIndex: 0,
            opCode: (uint)InstructionsEnum.ADD,
            dataType: 0,
            predicate: 0,
            immediate: 0,
            destSrc1: 0,
            src2: 0,
            streamLength: 0,
            stride: 0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);

        HybridCpuCompiledProgram compiled = context.CompileProgram();
        Assert.Equal(2, compiled.ExternalOperationIntents.Count);
        Assert.Null(compiled.ExternalOperationIntents[0]);
        Assert.Equal(semantic, compiled.ExternalOperationIntents[1]);
        Assert.Null(compiled.ExternalOperationMetadata[0]);
        Assert.Equal(1, compiled.ExternalOperationMetadata[1]!.SourceInstructionIndex);
    }

    [Fact]
    public void ExternalOperationLoweringMetadata_IsVersionedCorrelatedAndOutsideVliwPayload()
    {
        AcceleratorCommandDescriptor descriptor = L7SdcTestDescriptorFactory.ParseValidDescriptor();
        HybridCpuThreadCompilerContext context = CreateContextForDescriptor(descriptor);
        _ = context.CompileAcceleratorSubmit(
            IrAcceleratorIntent.ForMatMul(descriptor),
            CompilerAcceleratorCapabilityModel.ReferenceMatMul);

        HybridCpuCompiledProgram compiled = context.CompileProgram();
        IrExternalOperationLoweringMetadata metadata = Assert.IsType<IrExternalOperationLoweringMetadata>(
            Assert.Single(compiled.ExternalOperationMetadata));

        Assert.Equal(IrExternalOperationSemanticContract.Version, metadata.SemanticContractVersion);
        Assert.Equal(0, metadata.SourceInstructionIndex);
        Assert.Equal(descriptor.DescriptorReference.DescriptorIdentityHash, metadata.DescriptorIdentity.Value);
        Assert.False(metadata.FallbackPolicy.AllowsCpuFallbackBeforeSubmit);
        Assert.True(metadata.FallbackPolicy.AllowsStagedPublication);
        Assert.False(metadata.FallbackPolicy.RequiresCoherentAccess);

        IrBundleAnnotations annotations = Assert.Single(compiled.LoweredBundleAnnotations);
        Assert.True(annotations.TryGetInstructionSlotMetadata(7, out IrInstructionSlotMetadata slot));
        Assert.DoesNotContain("SemanticContract", string.Join('|', slot.GetType().GetProperties().Select(static property => property.Name)));
    }

    [Fact]
    public void DirectCoherentOutputRequired_IsRejectedWithoutRuntimeProofBoundary()
    {
        AcceleratorCommandDescriptor descriptor = L7SdcTestDescriptorFactory.ParseValidDescriptor();
        CompilerAcceleratorLoweringDecision decision = CompilerAcceleratorCapabilityModel.ReferenceMatMul.Decide(
            IrAcceleratorIntent.ForMatMul(descriptor) with
            {
                ExternalOperation = new IrExternalOperationIntent
                {
                    Publication = IrExternalPublicationIntent.DirectCoherentOutputRequired,
                    Coherence = IrExternalCoherenceRequirement.Required
                }
            });

        Assert.Equal(AcceleratorLoweringMode.Reject, decision.Mode);
        Assert.Contains("proof", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OptionalExternalExecution_EncodesCpuFallbackBeforeSubmitOnly()
    {
        AcceleratorCommandDescriptor descriptor = L7SdcTestDescriptorFactory.ParseValidDescriptor();
        IrExternalOperationFallbackPolicy policy = IrExternalOperationFallbackPolicy.From(
            new IrExternalOperationIntent { ExecutionRequirement = IrExternalExecutionRequirement.Optional });

        Assert.True(policy.AllowsCpuFallbackBeforeSubmit);
        Assert.False(policy.RequiresCoherentAccess);
        Assert.True(policy.AllowsStagedPublication);
        Assert.Equal(
            AcceleratorLoweringMode.CpuOrNonAccelerator,
            CompilerAcceleratorCapabilityModel.Disabled.Decide(
                IrAcceleratorIntent.ForMatMul(descriptor) with
                {
                    ExternalOperation = new IrExternalOperationIntent
                    {
                        ExecutionRequirement = IrExternalExecutionRequirement.Optional
                    }
                }).Mode);
    }

    [Fact]
    public void ExternalOperationLoweringMetadata_RejectsUnknownSemanticVersionAndMissingIdentity()
    {
        IrExternalOperationLoweringMetadata unknownVersion = new IrExternalOperationLoweringMetadata
        {
            SemanticContractVersion = IrExternalOperationSemanticContract.Version + 1,
            SourceInstructionIndex = 0,
            DescriptorIdentity = IrExternalOperationDescriptorIdentity.Require(1, "identity"),
            Intent = IrExternalOperationIntent.Lane7StagedNonRetryable
        };
        Assert.Throws<NotSupportedException>(unknownVersion.Validate);

        IrExternalOperationLoweringMetadata missingIdentity = new IrExternalOperationLoweringMetadata
        {
            SemanticContractVersion = IrExternalOperationSemanticContract.Version,
            SourceInstructionIndex = 0,
            DescriptorIdentity = default,
            Intent = IrExternalOperationIntent.Lane7StagedNonRetryable
        };
        Assert.Throws<InvalidOperationException>(missingIdentity.Validate);
    }

    [Fact]
    public void SemanticMemoryPlacementHints_ArePreservedOutsideTheStableCarrierImage()
    {
        AcceleratorCommandDescriptor descriptor = L7SdcTestDescriptorFactory.ParseValidDescriptor();
        HybridCpuThreadCompilerContext baseline = CreateContextForDescriptor(descriptor);
        HybridCpuThreadCompilerContext hinted = CreateContextForDescriptor(descriptor);
        _ = baseline.CompileAcceleratorSubmit(
            IrAcceleratorIntent.ForMatMul(descriptor), CompilerAcceleratorCapabilityModel.ReferenceMatMul);
        IrExternalOperationIntent semantic = new IrExternalOperationIntent
        {
            MemoryPlacement = IrExternalMemoryPlacementIntent.CapacityPreferred |
                IrExternalMemoryPlacementIntent.PersistentMemoryRequired |
                IrExternalMemoryPlacementIntent.ExternalDeviceAccessRequired |
                IrExternalMemoryPlacementIntent.CoherentSharedAccessPreferred
        };
        _ = hinted.CompileAcceleratorSubmit(
            IrAcceleratorIntent.ForMatMul(descriptor) with { ExternalOperation = semantic },
            CompilerAcceleratorCapabilityModel.ReferenceMatMul);

        HybridCpuCompiledProgram baselineProgram = baseline.CompileProgram();
        HybridCpuCompiledProgram hintedProgram = hinted.CompileProgram();
        Assert.Equal(baselineProgram.ProgramImage, hintedProgram.ProgramImage);
        Assert.Equal(semantic.MemoryPlacement, hintedProgram.ExternalOperationMetadata[0]!.Intent.MemoryPlacement);
        IrBundleAnnotations annotations = Assert.Single(hintedProgram.LoweredBundleAnnotations);
        Assert.True(annotations.TryGetInstructionSlotMetadata(7, out IrInstructionSlotMetadata slot));
        Assert.DoesNotContain("PlacementIntent", string.Join('|', slot.GetType().GetProperties().Select(static property => property.Name)));
    }

    [Fact]
    public void UnknownSemanticMemoryPlacementHint_IsRejectedBeforeCarrierEmission()
    {
        AcceleratorCommandDescriptor descriptor = L7SdcTestDescriptorFactory.ParseValidDescriptor();
        CompilerAcceleratorLoweringDecision decision = CompilerAcceleratorCapabilityModel.ReferenceMatMul.Decide(
            IrAcceleratorIntent.ForMatMul(descriptor) with
            {
                ExternalOperation = new IrExternalOperationIntent
                {
                    MemoryPlacement = (IrExternalMemoryPlacementIntent)0x80
                }
            });
        Assert.Equal(AcceleratorLoweringMode.Reject, decision.Mode);
    }

    [Fact]
    public void SecureVirtualSemanticIntent_IsPreservedOutsideTheLane7Carrier()
    {
        AcceleratorCommandDescriptor descriptor = L7SdcTestDescriptorFactory.ParseValidDescriptor();
        IrExternalOperationIntent semantic = new IrExternalOperationIntent
        {
            ExecutionDomain = IrExternalExecutionDomainRequirement.SecureVirtualized,
            SecureEvidence = IrExternalSecureEvidenceRequirement.Required,
            MinimumAssurance = IrExternalAssuranceRequirement.High,
            VirtualDomainBinding = IrExternalVirtualDomainBindingRequirement.Required,
            VirtualIo = IrExternalVirtualIoRequirement.BoundedRequired,
            Containment = IrExternalContainmentRequirement.CancellationOrContainmentRequired,
            DeviceAccess = IrExternalDeviceAccessIntent.DeviceReadable |
                IrExternalDeviceAccessIntent.DeviceWritable |
                IrExternalDeviceAccessIntent.CoherentOptional
        };
        HybridCpuThreadCompilerContext baseline = CreateContextForDescriptor(descriptor);
        HybridCpuThreadCompilerContext secured = CreateContextForDescriptor(descriptor);
        _ = baseline.CompileAcceleratorSubmit(
            IrAcceleratorIntent.ForMatMul(descriptor), CompilerAcceleratorCapabilityModel.ReferenceMatMul);
        _ = secured.CompileAcceleratorSubmit(
            IrAcceleratorIntent.ForMatMul(descriptor) with { ExternalOperation = semantic },
            CompilerAcceleratorCapabilityModel.ReferenceMatMul);

        HybridCpuCompiledProgram baselineProgram = baseline.CompileProgram();
        HybridCpuCompiledProgram securedProgram = secured.CompileProgram();

        Assert.Equal(baselineProgram.ProgramImage, securedProgram.ProgramImage);
        Assert.Equal(semantic, securedProgram.ExternalOperationMetadata[0]!.Intent);
    }

    [Fact]
    public void IncompleteSecureVirtualOrContradictoryDeviceAccess_IsRejectedBeforeCarrierEmission()
    {
        AcceleratorCommandDescriptor descriptor = L7SdcTestDescriptorFactory.ParseValidDescriptor();
        CompilerAcceleratorLoweringDecision incomplete = CompilerAcceleratorCapabilityModel.ReferenceMatMul.Decide(
            IrAcceleratorIntent.ForMatMul(descriptor) with
            {
                ExternalOperation = new IrExternalOperationIntent
                {
                    ExecutionDomain = IrExternalExecutionDomainRequirement.SecureVirtualized
                }
            });
        CompilerAcceleratorLoweringDecision contradictory = CompilerAcceleratorCapabilityModel.ReferenceMatMul.Decide(
            IrAcceleratorIntent.ForMatMul(descriptor) with
            {
                ExternalOperation = new IrExternalOperationIntent
                {
                    DeviceAccess = IrExternalDeviceAccessIntent.CoherentOptional |
                        IrExternalDeviceAccessIntent.CoherentRequired
                }
            });

        Assert.Equal(AcceleratorLoweringMode.Reject, incomplete.Mode);
        Assert.Equal(AcceleratorLoweringMode.Reject, contradictory.Mode);
    }

    [Theory]
    [InlineData(IrExternalExecutionDomainRequirement.Virtualized, IrExternalSecureEvidenceRequirement.NotRequired,
        IrExternalVirtualDomainBindingRequirement.NotRequired, IrExternalVirtualIoRequirement.BoundedRequired,
        IrExternalEffectRequirement.Idempotent, IrExternalContainmentRequirement.CancellationOrContainmentRequired)]
    [InlineData(IrExternalExecutionDomainRequirement.Secure, IrExternalSecureEvidenceRequirement.NotRequired,
        IrExternalVirtualDomainBindingRequirement.NotRequired, IrExternalVirtualIoRequirement.NotRequired,
        IrExternalEffectRequirement.Idempotent, IrExternalContainmentRequirement.CancellationOrContainmentRequired)]
    [InlineData(IrExternalExecutionDomainRequirement.SecureVirtualized, IrExternalSecureEvidenceRequirement.Required,
        IrExternalVirtualDomainBindingRequirement.Required, IrExternalVirtualIoRequirement.NotRequired,
        IrExternalEffectRequirement.Idempotent, IrExternalContainmentRequirement.CancellationOrContainmentRequired)]
    [InlineData(IrExternalExecutionDomainRequirement.Host, IrExternalSecureEvidenceRequirement.NotRequired,
        IrExternalVirtualDomainBindingRequirement.NotRequired, IrExternalVirtualIoRequirement.NotRequired,
        IrExternalEffectRequirement.NonRetryable, IrExternalContainmentRequirement.None)]
    public void IncompleteAuthorityOrContainmentIntent_IsRejectedBeforeCarrierEmission(
        IrExternalExecutionDomainRequirement domain,
        IrExternalSecureEvidenceRequirement evidence,
        IrExternalVirtualDomainBindingRequirement virtualBinding,
        IrExternalVirtualIoRequirement virtualIo,
        IrExternalEffectRequirement effect,
        IrExternalContainmentRequirement containment)
    {
        AcceleratorCommandDescriptor descriptor = L7SdcTestDescriptorFactory.ParseValidDescriptor();
        CompilerAcceleratorLoweringDecision decision = CompilerAcceleratorCapabilityModel.ReferenceMatMul.Decide(
            IrAcceleratorIntent.ForMatMul(descriptor) with
            {
                ExternalOperation = new IrExternalOperationIntent
                {
                    ExecutionDomain = domain,
                    SecureEvidence = evidence,
                    VirtualDomainBinding = virtualBinding,
                    VirtualIo = virtualIo,
                    Effect = effect,
                    Containment = containment
                }
            });

        Assert.Equal(AcceleratorLoweringMode.Reject, decision.Mode);
        Assert.False(decision.EmitsAcceleratorSubmit);
    }

    [Fact]
    public void ExternalOperationSemanticIntent_ExcludesTopologyProviderAndRuntimeIdentity()
    {
        string[] forbidden = ["Cxl", "Fabric", "Topology", "Endpoint", "Hdm", "Dpa", "Bdf", "Switch", "Port", "Address", "Provider", "Handle", "Generation"];
        Type[] semanticTypes = [typeof(IrExternalOperationIntent), typeof(IrExternalOperationLoweringMetadata),
            typeof(IrExternalOperationDescriptorIdentity)];

        Assert.All(semanticTypes, type => Assert.DoesNotContain(type.GetProperties(), property =>
            forbidden.Any(fragment => property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))));
    }

    [Fact]
    public void DirectCoherentExternalIntent_RequiresExplicitCoherenceWithoutProviderClaim()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        IrAcceleratorIntent intent = IrAcceleratorIntent.ForMatMul(descriptor) with
        {
            ExternalOperation = new IrExternalOperationIntent
            {
                Publication = IrExternalPublicationIntent.DirectCoherentOutputRequested,
                Coherence = IrExternalCoherenceRequirement.NotRequired
            }
        };

        CompilerAcceleratorLoweringDecision decision =
            CompilerAcceleratorCapabilityModel.ReferenceMatMul.Decide(intent);

        Assert.Equal(AcceleratorLoweringMode.Reject, decision.Mode);
        Assert.Contains("coherence", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(decision.EmitsAcceleratorSubmit);
    }

    [Fact]
    public void DmaSemanticContour_CannotBeLoweredAsLane7AcceleratorSubmit()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        IrAcceleratorIntent intent = IrAcceleratorIntent.ForMatMul(descriptor) with
        {
            ExternalOperation = new IrExternalOperationIntent
            {
                ExecutionContour = IrExternalExecutionContour.DmaStreaming
            }
        };

        CompilerAcceleratorLoweringDecision decision =
            CompilerAcceleratorCapabilityModel.ReferenceMatMul.Decide(intent);

        Assert.Equal(AcceleratorLoweringMode.Reject, decision.Mode);
        Assert.Contains("lane7", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    internal static MicroOp DecodeAndProjectSingleCarrier(
        VLIW_Bundle bundle,
        VliwBundleAnnotations annotations,
        int slotIndex)
    {
        VLIW_Instruction[] rawSlots = ToRawSlots(bundle);
        DecodedInstructionBundle decoded = new VliwDecoderV4().DecodeInstructionBundle(
            rawSlots,
            annotations,
            bundleAddress: 0xC000,
            bundleSerial: 12);
        MicroOp?[] carriers =
            DecodedBundleTransportProjector.BuildCanonicalCarrierBundleForTesting(rawSlots, decoded);
        return carriers[slotIndex] ?? throw new InvalidOperationException("Expected occupied projected carrier.");
    }

    internal static VLIW_Instruction[] ToRawSlots(VLIW_Bundle bundle)
    {
        VLIW_Instruction[] rawSlots = new VLIW_Instruction[BundleMetadata.BundleSlotCount];
        for (int slotIndex = 0; slotIndex < rawSlots.Length; slotIndex++)
        {
            rawSlots[slotIndex] = bundle.GetInstruction(slotIndex);
        }

        return rawSlots;
    }
}

public sealed class L7SdcCompilerNoRuntimeFallbackTests
{
    [Fact]
    public void RuntimeRejectAfterCompilerEmission_RemainsSystemDeviceRejectWithoutFallbackCarrier()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        HybridCpuThreadCompilerContext context =
            L7SdcCompilerEmissionTests.CreateContextForDescriptor(descriptor);
        context.CompileAcceleratorSubmit(
            IrAcceleratorIntent.ForMatMul(descriptor),
            CompilerAcceleratorCapabilityModel.ReferenceMatMul);
        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();

        MicroOp carrier = L7SdcCompilerEmissionTests.DecodeAndProjectSingleCarrier(
            compiledProgram.LoweredBundles[0],
            compiledProgram.LoweredBundleAnnotations[0],
            slotIndex: 7);

        AcceleratorSubmitMicroOp submit = Assert.IsType<AcceleratorSubmitMicroOp>(carrier);
        Assert.IsNotType<DmaStreamComputeMicroOp>(submit);
        Assert.IsNotType<GenericMicroOp>(submit);
        Assert.IsNotType<TrapMicroOp>(submit);

        Processor.MainMemoryArea previousMemory = Processor.MainMemory;
        try
        {
            if (Processor.MainMemory is null)
            {
                Processor.MainMemory = new Processor.MultiBankMemoryArea(1, 0x10000);
            }

            Processor.MainMemory.TryWritePhysicalRange(0x1000, Enumerable.Repeat((byte)0x29, 0x40).ToArray());
            Processor.MainMemory.TryWritePhysicalRange(0x9000, Enumerable.Repeat((byte)0x81, 0x40).ToArray());

            Processor.CPU_Core core = new Processor.CPU_Core(0);
            Assert.True(submit.Execute(ref core));
            Assert.NotNull(submit.LastSubmitAdmission);
            Assert.True(submit.LastSubmitAdmission!.IsAccepted, submit.LastSubmitAdmission.Message);
            Assert.False(submit.UsedLegacyCustomAcceleratorFallback);
            Assert.False(submit.UsedArithmeticExecutionPlane);
            Assert.True(submit.TryGetPrimaryWriteBackResult(out ulong tokenHandle));
            Assert.NotEqual(0UL, tokenHandle);
        }
        finally
        {
            Processor.MainMemory = previousMemory;
            Processor.Memory = null;
        }
    }

    [Fact]
    public void RuntimeFallbackPromiseInIntent_RejectsBeforeEmission()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        HybridCpuThreadCompilerContext context =
            L7SdcCompilerEmissionTests.CreateContextForDescriptor(descriptor);
        IrAcceleratorIntent intent =
            IrAcceleratorIntent.ForMatMul(descriptor) with
            {
                AllowRuntimeFallbackAfterSubmit = true
            };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => context.CompileAcceleratorSubmit(
                intent,
                CompilerAcceleratorCapabilityModel.ReferenceMatMul));

        Assert.Contains("runtime fallback", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, context.InstructionCount);
    }
}

public sealed class L7SdcCompilerLane7PressureTests
{
    [Fact]
    public void RepeatedSubmitIntent_DoesNotPackDenseLane7StormIntoOneBundle()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        HybridCpuThreadCompilerContext context =
            L7SdcCompilerEmissionTests.CreateContextForDescriptor(descriptor);

        context.CompileAcceleratorSubmit(
            IrAcceleratorIntent.ForMatMul(descriptor),
            CompilerAcceleratorCapabilityModel.ReferenceMatMul);
        context.CompileAcceleratorSubmit(
            IrAcceleratorIntent.ForMatMul(descriptor),
            CompilerAcceleratorCapabilityModel.ReferenceMatMul);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IReadOnlyList<IrMaterializedBundle> bundles =
            compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles).ToArray();

        Assert.Equal(2, bundles.Count);
        foreach (IrMaterializedBundle bundle in bundles)
        {
            TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(bundle);
            Assert.Equal(1, facts.SystemSingletonCount);
            Assert.Equal(0, facts.BranchControlCount);
            Assert.True(HybridCpuTypedSlotFactsEmitter.ValidateEmittedFacts(facts));
        }
    }

    [Fact]
    public void BranchAndSubmit_DoNotShareAliasedLane7BundleWindow()
    {
        AcceleratorCommandDescriptor descriptor =
            L7SdcTestDescriptorFactory.ParseValidDescriptor();
        HybridCpuThreadCompilerContext context =
            L7SdcCompilerEmissionTests.CreateContextForDescriptor(descriptor);

        context.CompileAcceleratorSubmit(
            IrAcceleratorIntent.ForMatMul(descriptor),
            CompilerAcceleratorCapabilityModel.ReferenceMatMul);
        context.CompileInstruction(
            opCode: (uint)InstructionsEnum.JAL,
            dataType: 0,
            predicate: 0,
            immediate: 4,
            destSrc1: VLIW_Instruction.PackArchRegs(
                2,
                VLIW_Instruction.NoArchReg,
                VLIW_Instruction.NoArchReg),
            src2: 0,
            streamLength: 0,
            stride: 0,
            stealabilityPolicy: StealabilityPolicy.NotStealable);

        HybridCpuCompiledProgram compiledProgram = context.CompileProgram();
        IReadOnlyList<IrMaterializedBundle> bundles =
            compiledProgram.BundleLayout.BlockResults.SelectMany(block => block.Bundles).ToArray();

        Assert.Equal(2, bundles.Count);
        Assert.DoesNotContain(
            bundles,
            bundle =>
            {
                TypedSlotBundleFacts facts = HybridCpuBundleLowerer.EmitFactsForBundle(bundle);
                return facts.SystemSingletonCount > 0 && facts.BranchControlCount > 0;
            });
        Assert.Contains(bundles, bundle => HybridCpuBundleLowerer.EmitFactsForBundle(bundle).SystemSingletonCount == 1);
        Assert.Contains(bundles, bundle => HybridCpuBundleLowerer.EmitFactsForBundle(bundle).BranchControlCount == 1);
    }
}
