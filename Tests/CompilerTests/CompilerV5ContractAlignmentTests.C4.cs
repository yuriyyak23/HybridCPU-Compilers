using System.Linq;
using System.Reflection;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using static YAKSys_Hybrid_CPU.Processor;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;
using SlotClass = HybridCPU.Compiler.Core.IR.IrSlotClass;
using SlotClassLaneMap = HybridCPU.Compiler.Core.Runtime.RuntimeSlotClassLaneMapAdapter;
using TypedSlotBundleFacts = HybridCPU.Compiler.Core.IR.IrTypedSlotBundleFacts;

namespace HybridCPU_ISE.Tests.CompilerTests;

public partial class CompilerV5ContractAlignmentTests
{
    #region C4 — DmaStream Handoff Invariants

    [Fact]
    public void WhenDmaStreamThenSlotClassMappingIsDmaStreamClass()
    {
        // C4 contract: DmaStream resource class maps to SlotClass.DmaStreamClass.
        SlotClass mapped = IrSlotClassMapping.ToSlotClass(IrResourceClass.DmaStream);

        Assert.Equal(SlotClass.DmaStreamClass, mapped);
    }

    [Fact]
    public void WhenDmaStreamThenBindingKindIsSingletonClass()
    {
        // C4 contract: DmaStream resource class derives IrSlotBindingKind.SingletonClass.
        IrSlotBindingKind bindingKind = IrSlotClassMapping.DerivePinningKind(
            IrResourceClass.DmaStream,
            IrSerializationKind.None);

        Assert.Equal(IrSlotBindingKind.SingletonClass, bindingKind);
    }

    [Fact]
    public void WhenSingletonClassThenRuntimePinningKindIsClassFlexible()
    {
        // C4 contract: SingletonClass maps to ClassFlexible in runtime pinning model.
        // Singleton constraint is enforced by SlotClassLaneMap topology, not pinning metadata.
        SlotPinningKind runtimeKind = NativeTransportRuntimeAdapter.ToRuntimePinningKind(
            IrSlotBindingKind.SingletonClass);

        Assert.Equal(SlotPinningKind.ClassFlexible, runtimeKind);
    }

    [Fact]
    public void WhenDmaStreamClassThenLaneMapCapacityIsOne()
    {
        // C4 contract: DmaStreamClass has capacity=1 (lane 6 only).
        int capacity = SlotClassLaneMap.GetClassCapacity(SlotClass.DmaStreamClass);

        Assert.Equal(1, capacity);
    }

    [Fact]
    public void WhenDmaStreamClassThenLaneMaskIsLane6Only()
    {
        // C4 contract: DmaStreamClass lane mask is 0b_0100_0000 (lane 6 only).
        byte mask = SlotClassLaneMap.GetLaneMask(SlotClass.DmaStreamClass);

        Assert.Equal(0b_0100_0000, mask);
    }

    [Fact]
    public void WhenDmaStreamResourceClassThenFullHandoffChainIsCorrect()
    {
        // C4 contract end-to-end: DmaStream → DmaStreamClass → SingletonClass → ClassFlexible.
        // Validates the complete compiler→runtime handoff chain without opcode-level integration.
        SlotClass slotClass = IrSlotClassMapping.ToSlotClass(IrResourceClass.DmaStream);
        IrSlotBindingKind bindingKind = IrSlotClassMapping.DerivePinningKind(
            IrResourceClass.DmaStream, IrSerializationKind.None);
        SlotPinningKind runtimePinning = NativeTransportRuntimeAdapter.ToRuntimePinningKind(bindingKind);

        Assert.Equal(SlotClass.DmaStreamClass, slotClass);
        Assert.Equal(IrSlotBindingKind.SingletonClass, bindingKind);
        Assert.Equal(SlotPinningKind.ClassFlexible, runtimePinning);
        Assert.Equal(1, SlotClassLaneMap.GetClassCapacity(slotClass));
    }

    [Theory]
    [InlineData(InstructionsEnum.STREAM_SETUP)]
    [InlineData(InstructionsEnum.STREAM_START)]
    [InlineData(InstructionsEnum.STREAM_WAIT)]
    public void WhenPublishedStreamControlProfileComputedThenClassifiedAsSystemNotDmaStream(
        InstructionsEnum opcode)
    {
        // Document actual compiler behavior: published stream-control contours are classified
        // as System (not DmaStream) because canonical OpcodeRegistry class information wins.
        // The DmaStream→SingletonClass→ClassFlexible mapping chain is structurally
        // correct and validated by direct mapping tests above.
        IrOpcodeExecutionProfile profile = HybridCpuHazardModel.GetExecutionProfile(
            NativeTransportRuntimeAdapter.ToCore(opcode));

        Assert.Equal(IrResourceClass.System, profile.ResourceClass);
        Assert.Equal(SlotClass.SystemSingleton, profile.DerivedSlotClass);
    }

    [Theory]
    [InlineData(InstructionsEnum.ADDI, IrResourceClass.ScalarAlu)]
    [InlineData(InstructionsEnum.LB, IrResourceClass.LoadStore)]
    [InlineData(InstructionsEnum.JAL, IrResourceClass.ControlFlow)]
    [InlineData(InstructionsEnum.CSRRW, IrResourceClass.System)]
    [InlineData(InstructionsEnum.VMAND, IrResourceClass.VectorAlu)]
    public void WhenPublishedOpcodeProfileComputedThenResourceClassFollowsCanonicalDescriptor(
        InstructionsEnum opcode,
        IrResourceClass expectedResourceClass)
    {
        IrOpcodeExecutionProfile profile = HybridCpuHazardModel.GetExecutionProfile(
            NativeTransportRuntimeAdapter.ToCore(opcode));

        Assert.Equal(expectedResourceClass, profile.ResourceClass);
    }

    [Fact]
    public void WhenRetainedVexcpMaskProfileComputedThenResourceClassUsesSystemFallbackInsteadOfVectorPrefixHeuristic()
    {
        IrOpcodeExecutionProfile profile = HybridCpuHazardModel.GetExecutionProfile(
            NativeTransportRuntimeAdapter.ToCore(InstructionsEnum.VSETVEXCPMASK));

        Assert.Equal(IrResourceClass.System, profile.ResourceClass);
    }

    [Theory]
    [InlineData(InstructionsEnum.VGATHER)]
    [InlineData(InstructionsEnum.VSCATTER)]
    public void WhenPublishedVectorMemoryOpcodeProfileComputedThenResourceClassUsesCanonicalMemoryDescriptor(
        InstructionsEnum opcode)
    {
        IrOpcodeExecutionProfile profile = HybridCpuHazardModel.GetExecutionProfile(
            NativeTransportRuntimeAdapter.ToCore(opcode));

        Assert.Equal(IrResourceClass.LoadStore, profile.ResourceClass);
        Assert.Equal(SlotClass.LsuClass, profile.DerivedSlotClass);
    }

    [Theory]
    [InlineData(InstructionsEnum.VGATHER, IrStructuralResource.LoadDataPort, IrStructuralResource.StoreDataPort)]
    [InlineData(InstructionsEnum.VSCATTER, IrStructuralResource.StoreDataPort, IrStructuralResource.LoadDataPort)]
    public void WhenPublishedVectorMemoryOpcodeProfileComputedThenStructuralPortsFollowCanonicalMemoryDirection(
        InstructionsEnum opcode,
        IrStructuralResource expectedPort,
        IrStructuralResource unexpectedPort)
    {
        IrOpcodeExecutionProfile profile = HybridCpuHazardModel.GetExecutionProfile(
            NativeTransportRuntimeAdapter.ToCore(opcode));

        Assert.True((profile.StructuralResources & IrStructuralResource.AddressGenerationUnit) != 0);
        Assert.True((profile.StructuralResources & expectedPort) != 0);
        Assert.True((profile.StructuralResources & unexpectedPort) == 0);
    }

    [Theory]
    [InlineData("IsLoadStoreOpcode", InstructionsEnum.LB)]
    [InlineData("IsLoadStoreOpcode", InstructionsEnum.VGATHER)]
    [InlineData("IsVectorInstruction", InstructionsEnum.VADD)]
    [InlineData("IsSystemInstruction", InstructionsEnum.VSETVL)]
    [InlineData("IsSystemInstruction", InstructionsEnum.CSR_CLEAR)]
    [InlineData("IsSystemInstruction", InstructionsEnum.ECALL)]
    [InlineData("IsSystemInstruction", InstructionsEnum.YIELD)]
    [InlineData("IsSystemInstruction", InstructionsEnum.VMREAD)]
    public void WhenPublishedOpcodeSemanticHelperInvokedWithoutExplicitDescriptorThenRegistryRemainsAuthority(
        string methodName,
        InstructionsEnum opcode)
    {
        MethodInfo method = typeof(HybridCpuHazardModel).Assembly.GetType(
                "HybridCPU.Compiler.Core.IR.HybridCpuOpcodeSemantics",
                throwOnError: true)!
            .GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"HybridCpuOpcodeSemantics.{methodName} was not found.");

        object? result = method.Invoke(null, new object?[] { opcode, null });

        Assert.True(Assert.IsType<bool>(result),
            $"HybridCpuOpcodeSemantics.{methodName} must use published OpcodeRegistry semantics for {opcode} even without an explicit OpcodeInfo argument.");
    }

    [Theory]
    [InlineData("IsVectorInstruction", InstructionsEnum.VLOAD, true)]
    [InlineData("IsVectorInstruction", InstructionsEnum.VSETVEXCPMASK, false)]
    [InlineData("IsSystemInstruction", InstructionsEnum.VSETVEXCPMASK, true)]
    [InlineData("IsSystemInstruction", InstructionsEnum.VSETVEXCPPRI, true)]
    public void WhenRetainedOpcodeSemanticHelperInvokedWithoutExplicitDescriptorThenFallbackContoursRemainNarrowed(
        string methodName,
        InstructionsEnum opcode,
        bool expected)
    {
        MethodInfo method = typeof(HybridCpuHazardModel).Assembly.GetType(
                "HybridCPU.Compiler.Core.IR.HybridCpuOpcodeSemantics",
                throwOnError: true)!
            .GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"HybridCpuOpcodeSemantics.{methodName} was not found.");

        object? result = method.Invoke(null, new object?[] { opcode, null });

        Assert.Equal(expected, Assert.IsType<bool>(result));
    }

    [Theory]
    [InlineData(InstructionsEnum.FENCE, true)]
    [InlineData(InstructionsEnum.FENCE_I, true)]
    [InlineData(InstructionsEnum.LR_W, true)]
    [InlineData(InstructionsEnum.SC_D, true)]
    [InlineData(InstructionsEnum.AMOADD_W, false)]

    public void WhenBarrierLikeSemanticHelperInvokedThenPublishedBarrierContoursUseCanonicalDescriptorWithRetainedFallback(
        InstructionsEnum opcode,
        bool expected)
    {
        MethodInfo method = typeof(HybridCpuHazardModel).Assembly.GetType(
                "HybridCPU.Compiler.Core.IR.HybridCpuOpcodeSemantics",
                throwOnError: true)!
            .GetMethod(
                "IsBarrierLike",
                BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("HybridCpuOpcodeSemantics.IsBarrierLike was not found.");

        object? result = method.Invoke(null, new object?[] { opcode });

        Assert.Equal(expected, Assert.IsType<bool>(result));
    }

    [Theory]
    [InlineData("UsesLoadStoreReadPath", InstructionsEnum.VGATHER, true)]
    [InlineData("UsesLoadStoreWritePath", InstructionsEnum.VGATHER, false)]
    [InlineData("UsesLoadStoreReadPath", InstructionsEnum.VSCATTER, false)]
    [InlineData("UsesLoadStoreWritePath", InstructionsEnum.VSCATTER, true)]
    public void WhenPublishedVectorMemoryHelperInvokedWithoutExplicitDescriptorThenDirectionFollowsCanonicalContour(
        string methodName,
        InstructionsEnum opcode,
        bool expected)
    {
        MethodInfo method = typeof(HybridCpuHazardModel).Assembly.GetType(
                "HybridCPU.Compiler.Core.IR.HybridCpuOpcodeSemantics",
                throwOnError: true)!
            .GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"HybridCpuOpcodeSemantics.{methodName} was not found.");

        object? result = method.Invoke(null, new object?[] { opcode, null });

        Assert.Equal(expected, Assert.IsType<bool>(result));
    }

    [Theory]
    [InlineData("UsesLoadStoreReadPath", InstructionsEnum.LB, true)]
    [InlineData("UsesLoadStoreWritePath", InstructionsEnum.LB, false)]
    [InlineData("UsesLoadStoreReadPath", InstructionsEnum.AMOADD_W, true)]
    [InlineData("UsesLoadStoreWritePath", InstructionsEnum.AMOADD_W, true)]
    [InlineData("UsesLoadStoreReadPath", InstructionsEnum.VLOAD, true)]
    [InlineData("UsesLoadStoreWritePath", InstructionsEnum.VSTORE, true)]
    public void WhenScalarAtomicOrRetainedLoadStoreHelperInvokedWithoutExplicitDescriptorThenDirectionRemainsCanonicalOrRetained(
        string methodName,
        InstructionsEnum opcode,
        bool expected)
    {
        MethodInfo method = typeof(HybridCpuHazardModel).Assembly.GetType(
                "HybridCPU.Compiler.Core.IR.HybridCpuOpcodeSemantics",
                throwOnError: true)!
            .GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"HybridCpuOpcodeSemantics.{methodName} was not found.");

        object? result = method.Invoke(null, new object?[] { opcode, null });

        Assert.Equal(expected, Assert.IsType<bool>(result));
    }

    [Theory]
    [InlineData(InstructionsEnum.JAL, IrControlFlowKind.UnconditionalBranch)]
    [InlineData(InstructionsEnum.BEQ, IrControlFlowKind.ConditionalBranch)]
    [InlineData(InstructionsEnum.JALR, IrControlFlowKind.Return)]
    [InlineData(InstructionsEnum.WFI, IrControlFlowKind.Stop)]
    [InlineData(InstructionsEnum.EBREAK, IrControlFlowKind.Stop)]
    [InlineData(InstructionsEnum.FENCE, IrControlFlowKind.None)]

    public void WhenIrBuilderControlFlowHelperInvokedThenPublishedContoursUseCanonicalDescriptorWithRetainedFallback(
        InstructionsEnum opcode,
        IrControlFlowKind expected)
    {
        MethodInfo method = typeof(HybridCpuIrBuilder).GetMethod(
                "ClassifyControlFlow",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("HybridCpuIrBuilder.ClassifyControlFlow was not found.");

        object? result = method.Invoke(null, new object?[] { opcode });

        Assert.Equal(expected, Assert.IsType<IrControlFlowKind>(result));
    }

    [Fact]
    public void WhenIrBuilderControlFlowHelperInvokedOnProhibitedJumpWrapperThenNoCanonicalBranchContourIsPublished()
    {
        const InstructionsEnum prohibitedJumpWrapperOpcode = (InstructionsEnum)18;

        MethodInfo method = typeof(HybridCpuIrBuilder).GetMethod(
                "ClassifyControlFlow",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("HybridCpuIrBuilder.ClassifyControlFlow was not found.");

        object? result = method.Invoke(null, new object?[] { prohibitedJumpWrapperOpcode });

        Assert.Equal(IrControlFlowKind.None, Assert.IsType<IrControlFlowKind>(result));
    }

    [Theory]
    [InlineData("ReadsPrimaryOperand", InstructionsEnum.LW, true)]
    [InlineData("ReadsPrimaryOperand", InstructionsEnum.CSRRS, false)]
    [InlineData("WritesPrimaryOperand", InstructionsEnum.SW, true)]
    [InlineData("WritesPrimaryOperand", InstructionsEnum.CSRRW, false)]
    public void WhenIrBuilderPrimaryPointerFallbackHelperInvokedThenPublishedTypedMemoryNoLongerOwnsLegacyCarveOuts(
        string methodName,
        InstructionsEnum opcode,
        bool expected)
    {
        MethodInfo method = typeof(HybridCpuIrBuilder).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"HybridCpuIrBuilder.{methodName} was not found.");

        object? result = method.Invoke(null, new object?[] { opcode });

        Assert.Equal(expected, Assert.IsType<bool>(result));
    }

    [Fact]
    public void WhenIrBuilderBuildsPublishedTypedLoadThenDecodedRegistersRemainAuthorityInsteadOfPrimaryPointerFallback()
    {
        IrInstruction instruction = Assert.Single(BuildProgram(
            new VLIW_Instruction
            {
                OpCode = (uint)InstructionsEnum.LW,
                DataTypeValue = DataTypeEnum.INT32,
                PredicateMask = 0xFF,
                DestSrc1Pointer = Pack(dest: 9, src1: 1, src2: 0),
                Src2Pointer = 0x40,
                StreamLength = 0,
                Stride = 0,
                VirtualThreadId = 0
            }).Instructions);

        Assert.Collection(
            instruction.Annotation.Defs,
            operand =>
            {
                Assert.Equal("rd", operand.Name);
                Assert.Equal(9UL, operand.Value);
            });
        Assert.DoesNotContain(instruction.Annotation.Defs, operand => operand.Name == "destsrc1");
        Assert.Contains(instruction.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 1UL);
        Assert.Contains(instruction.Annotation.Uses, operand => operand.Name == "src2" && operand.Value == 0x40UL);
        Assert.DoesNotContain(instruction.Annotation.Uses, operand => operand.Name == "destsrc1");
    }

    [Fact]
    public void WhenIrBuilderBuildsPublishedTypedStoreThenDecodedRegistersRemainAuthorityInsteadOfPrimaryPointerFallback()
    {
        IrInstruction instruction = Assert.Single(BuildProgram(
            new VLIW_Instruction
            {
                OpCode = (uint)InstructionsEnum.SW,
                DataTypeValue = DataTypeEnum.INT32,
                PredicateMask = 0xFF,
                DestSrc1Pointer = Pack(dest: 0, src1: 1, src2: 2),
                Src2Pointer = 0x40,
                StreamLength = 0,
                Stride = 0,
                VirtualThreadId = 0
            }).Instructions);

        Assert.Empty(instruction.Annotation.Defs);
        Assert.Contains(instruction.Annotation.Uses, operand => operand.Name == "rs1" && operand.Value == 1UL);
        Assert.Contains(instruction.Annotation.Uses, operand => operand.Name == "rs2" && operand.Value == 2UL);
        Assert.Contains(instruction.Annotation.Uses, operand => operand.Name == "src2" && operand.Value == 0x40UL);
        Assert.DoesNotContain(instruction.Annotation.Uses, operand => operand.Name == "destsrc1");
    }

    [Theory]
    [InlineData(InstructionsEnum.JAL, true)]
    [InlineData(InstructionsEnum.JALR, false)]
    [InlineData(InstructionsEnum.BEQ, false)]
    [InlineData(InstructionsEnum.DIV, true)]
    [InlineData(InstructionsEnum.DIVU, false)]
    [InlineData(InstructionsEnum.REM, false)]
    public void WhenIrBuilderMayTrapHelperInvokedThenPublishedTrappingContoursUseCanonicalDescriptorWithoutChangingRetainedFallback(
        InstructionsEnum opcode,
        bool expected)
    {
        MethodInfo method = typeof(HybridCpuIrBuilder).GetMethod(
                "MayTrap",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("HybridCpuIrBuilder.MayTrap was not found.");

        object? result = method.Invoke(null, new object?[] { opcode, null, null });

        Assert.Equal(expected, Assert.IsType<bool>(result));
    }

    [Theory]
    [InlineData(InstructionsEnum.LW, true)]
    [InlineData(InstructionsEnum.SW, false)]
    [InlineData(InstructionsEnum.CSRRW, true)]
    [InlineData(InstructionsEnum.CSR_CLEAR, false)]
    public void WhenIrBuilderWritesDecodedDestinationHelperInvokedThenPublishedStoreAndCsrContoursFollowCanonicalDescriptor(
        InstructionsEnum opcode,
        bool expected)
    {
        MethodInfo method = typeof(HybridCpuIrBuilder).GetMethod(
                "WritesDecodedDestination",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("HybridCpuIrBuilder.WritesDecodedDestination was not found.");

        object? result = method.Invoke(null, new object?[] { opcode });

        Assert.Equal(expected, Assert.IsType<bool>(result));
    }

    [Theory]
    [InlineData(InstructionsEnum.VREDSUM, true)]
    [InlineData(InstructionsEnum.VDOT_FP8, true)]
    [InlineData(InstructionsEnum.VPOPC, false)]
    public void WhenVectorReductionProfileComputedThenReductionUnitFollowsCanonicalPublishedMaskManipExclusion(
        InstructionsEnum opcode,
        bool expectedReductionUnit)
    {
        IrOpcodeExecutionProfile profile = HybridCpuHazardModel.GetExecutionProfile(
            NativeTransportRuntimeAdapter.ToCore(opcode));

        Assert.Equal(
            expectedReductionUnit,
            (profile.StructuralResources & IrStructuralResource.ReductionUnit) != 0);
    }

    [Theory]
    [InlineData(InstructionsEnum.VREDSUM, true)]
    [InlineData(InstructionsEnum.VDOT_FP8, true)]
    [InlineData(InstructionsEnum.VPOPC, false)]
    public void WhenStructuralReductionHelperInvokedThenPublishedContoursUseCanonicalDescriptorFlags(
        InstructionsEnum opcode,
        bool expected)
    {
        MethodInfo method = typeof(HybridCpuHazardModel).Assembly.GetType(
                "HybridCPU.Compiler.Core.IR.HybridCpuStructuralResourceModel",
                throwOnError: true)!
            .GetMethod(
                "SupportsReduction",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("HybridCpuStructuralResourceModel.SupportsReduction was not found.");

        HybridCpuOpcode coreOpcode = NativeTransportRuntimeAdapter.ToCore(opcode);
        object? result = method.Invoke(
            null,
            new object?[] { coreOpcode, HybridCpuOpcodeCatalog.GetInfo(coreOpcode) });

        Assert.Equal(expected, Assert.IsType<bool>(result));
    }

    [Theory]
    [InlineData(InstructionsEnum.JAL, true)]
    [InlineData(InstructionsEnum.BEQ, true)]
    [InlineData(InstructionsEnum.FENCE, false)]
    public void WhenHazardModelControlFlowHelperInvokedThenPublishedContoursUseCanonicalDescriptorWithRetainedFallback(
        InstructionsEnum opcode,
        bool expected)
    {
        MethodInfo method = typeof(HybridCpuHazardModel).GetMethod(
                "IsControlFlowInstruction",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("HybridCpuHazardModel.IsControlFlowInstruction was not found.");

        HybridCpuOpcode coreOpcode = NativeTransportRuntimeAdapter.ToCore(opcode);
        object? result = method.Invoke(
            null,
            new object?[] { coreOpcode, HybridCpuOpcodeCatalog.GetInfo(coreOpcode) });

        Assert.Equal(expected, Assert.IsType<bool>(result));
    }

    [Theory]
    [InlineData(InstructionsEnum.CSRRW)]
    [InlineData(InstructionsEnum.CSR_CLEAR)]
    public void WhenPublishedCsrProfileComputedThenStructuralResourcesUseCanonicalCsrDescriptor(
        InstructionsEnum opcode)
    {
        IrOpcodeExecutionProfile profile = HybridCpuHazardModel.GetExecutionProfile(
            NativeTransportRuntimeAdapter.ToCore(opcode));

        Assert.True((profile.StructuralResources & IrStructuralResource.CsrPort) != 0);
    }

    [Theory]
    [InlineData(InstructionsEnum.CSRRW, true)]
    [InlineData(InstructionsEnum.VSETVEXCPMASK, true)]
    public void WhenCsrStructuralHelperInvokedThenPublishedAndUnpublishedContoursRemainNarrowlySupported(
        InstructionsEnum opcode,
        bool expected)
    {
        MethodInfo method = typeof(HybridCpuHazardModel).Assembly.GetType(
                "HybridCPU.Compiler.Core.IR.HybridCpuStructuralResourceModel",
                throwOnError: true)!
            .GetMethod(
                "UsesCsrPort",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("HybridCpuStructuralResourceModel.UsesCsrPort was not found.");

        object? result = method.Invoke(null, new object?[] { opcode, null });

        Assert.Equal(expected, Assert.IsType<bool>(result));
    }

    [Theory]
    [InlineData(InstructionsEnum.VPERMUTE, true)]
    [InlineData(InstructionsEnum.VRGATHER, true)]
    [InlineData(InstructionsEnum.VEXPAND, true)]
    [InlineData(InstructionsEnum.VSLIDEUP, true)]
    [InlineData(InstructionsEnum.VGATHER, false)]
    [InlineData(InstructionsEnum.VSCATTER, false)]
    [InlineData(InstructionsEnum.VADD, false)]
    public void WhenPermuteStructuralHelperInvokedThenPublishedCrossbarUseFollowsCanonicalDescriptor(
        InstructionsEnum opcode,
        bool expected)
    {
        MethodInfo method = typeof(HybridCpuHazardModel).Assembly.GetType(
                "HybridCPU.Compiler.Core.IR.HybridCpuStructuralResourceModel",
                throwOnError: true)!
            .GetMethod(
                "UsesPermuteCrossbar",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("HybridCpuStructuralResourceModel.UsesPermuteCrossbar was not found.");

        object? result = method.Invoke(null, new object?[] { opcode, null });

        Assert.Equal(expected, Assert.IsType<bool>(result));
    }

    [Theory]
    [InlineData(InstructionsEnum.VPERMUTE, true)]
    [InlineData(InstructionsEnum.VADD, false)]
    public void WhenPublishedVectorProfileComputedThenPermuteCrossbarFollowsCanonicalDescriptor(
        InstructionsEnum opcode,
        bool expectedCrossbar)
    {
        IrOpcodeExecutionProfile profile = HybridCpuHazardModel.GetExecutionProfile(
            NativeTransportRuntimeAdapter.ToCore(opcode));

        Assert.Equal(
            expectedCrossbar,
            (profile.StructuralResources & IrStructuralResource.VectorPermuteCrossbar) != 0);
    }

    [Fact]
    public void WhenDmaStreamFactsValidatedThenCapacityOneEnforced()
    {
        // C4 contract: typed-slot facts validation rejects DmaStreamCount > 1 (capacity=1).
        TypedSlotBundleFacts overCapacityFacts = new TypedSlotBundleFacts
        {
            Slot0Class = SlotClass.DmaStreamClass,
            Slot1Class = SlotClass.DmaStreamClass,
            DmaStreamCount = 2,
            FlexibleOpCount = 2,
            PinnedOpCount = 0,
            AluCount = 0,
            LsuCount = 0,
            BranchControlCount = 0,
            SystemSingletonCount = 0
        };

        Assert.False(
            HybridCpuTypedSlotFactsEmitter.ValidateEmittedFacts(overCapacityFacts),
            "Validation must reject DmaStreamCount > 1 (DmaStreamClass capacity=1).");
    }

    [Fact]
    public void WhenSingleDmaStreamFactsThenValidationPasses()
    {
        // C4 contract: exactly one DmaStream within capacity passes validation.
        TypedSlotBundleFacts validFacts = new TypedSlotBundleFacts
        {
            Slot6Class = SlotClass.DmaStreamClass,
            DmaStreamCount = 1,
            FlexibleOpCount = 1,
            PinnedOpCount = 0,
            AluCount = 0,
            LsuCount = 0,
            BranchControlCount = 0,
            SystemSingletonCount = 0
        };

        Assert.True(
            HybridCpuTypedSlotFactsEmitter.ValidateEmittedFacts(validFacts),
            "Single DmaStream instruction within capacity must pass validation.");
    }

    [Fact]
    public void WhenDmaStreamClassThenPhysicalLane6AliasIsExplicit()
    {
        Assert.True(SlotClassLaneMap.HasAliasedLanes(SlotClass.DmaStreamClass));
        Assert.Equal(
            new[] { SlotClass.MatrixTileStreamClass },
            SlotClassLaneMap.GetAliasedClasses(SlotClass.DmaStreamClass).ToArray());
    }

    [Fact]
    public void WhenDmaStreamAnnotatedBundleEmitsFactsThenDmaStreamCountIsOne()
    {
        IrInstruction sourceInstruction = Assert.Single(BuildProgram(
            CreateDmaStreamInstruction()).Instructions);
        IrInstruction dmaInstruction = WithAnnotation(
            sourceInstruction,
            resourceClass: IrResourceClass.DmaStream,
            requiredSlotClass: SlotClass.DmaStreamClass,
            bindingKind: IrSlotBindingKind.SingletonClass);

        TypedSlotBundleFacts facts = EmitFactsFromManualBundle(
            (dmaInstruction, 6, IrIssueSlotMask.Slot6));

        Assert.Equal(1, facts.DmaStreamCount);
        // Phase 10 lane6 parity: DSC remains compiler evidence, but its
        // runtime-facing structural fact is hard-pinned to canonical lane 6.
        Assert.Equal(0, facts.FlexibleOpCount);
        Assert.Equal(1, facts.PinnedOpCount);
        Assert.True(HybridCpuTypedSlotFactsEmitter.ValidateEmittedFacts(facts));
    }

    [Fact]
    public void WhenTwoDmaStreamAnnotatedInstructionsEmitFactsThenValidationRejectsCapacityTwo()
    {
        IrProgram program = BuildProgram(
            CreateDmaStreamInstruction(),
            CreateDmaStreamInstruction());
        IrInstruction first = WithAnnotation(
            program.Instructions[0],
            resourceClass: IrResourceClass.DmaStream,
            requiredSlotClass: SlotClass.DmaStreamClass,
            bindingKind: IrSlotBindingKind.SingletonClass);
        IrInstruction second = WithAnnotation(
            program.Instructions[1],
            resourceClass: IrResourceClass.DmaStream,
            requiredSlotClass: SlotClass.DmaStreamClass,
            bindingKind: IrSlotBindingKind.SingletonClass);

        TypedSlotBundleFacts facts = EmitFactsFromManualBundle(
            (first, 6, IrIssueSlotMask.Slot6),
            (second, 5, IrIssueSlotMask.Slot5));

        Assert.Equal(2, facts.DmaStreamCount);
        Assert.False(HybridCpuTypedSlotFactsEmitter.ValidateEmittedFacts(facts));
    }

    #endregion
}
