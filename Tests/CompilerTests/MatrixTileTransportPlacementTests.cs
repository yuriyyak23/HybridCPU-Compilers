using System.Buffers.Binary;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Contours;
using HybridCPU.Compiler.Core.IR.Intent;
using HybridCPU.Compiler.Core.IR.Matrix;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.Threading;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.Tests.TestHelpers;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class MatrixTileTransportPlacementTests
{
    [Theory]
    [InlineData(HybridCpuOpcode.MTILE_LOAD)]
    [InlineData(HybridCpuOpcode.MTILE_STORE)]
    public void TileMemory_UsesPinnedSharedTransportWithoutScalarLsuPorts(HybridCpuOpcode opcode)
    {
        IrOpcodeExecutionProfile profile = HybridCpuHazardModel.GetExecutionProfile(opcode);
        Assert.Equal(IrIssueSlotMask.Slot6, profile.StructurallyAllowedSlots);
        Assert.Equal(IrSlotClass.MatrixTileStreamClass, profile.DerivedSlotClass);
        Assert.Equal(IrSlotBindingKind.HardPinned, profile.DerivedBindingKind);
        Assert.Equal(IrLatencyClass.LoadUse, profile.LatencyClass);
        Assert.Equal(IrStructuralResource.None, profile.StructuralResources &
            (IrStructuralResource.AddressGenerationUnit | IrStructuralResource.LoadDataPort | IrStructuralResource.StoreDataPort));
        Assert.False(HybridCpuSlotModel.SearchStructuralAssignments(
            [profile.StructurallyAllowedSlots, IrIssueSlotMask.Slot6]).HasStructuralPlacement);
    }

    [Theory]
    [InlineData(HybridCpuOpcode.LD)]
    [InlineData(HybridCpuOpcode.SD)]
    [InlineData(HybridCpuOpcode.VLOAD)]
    [InlineData(HybridCpuOpcode.VSTORE)]
    public void OrdinaryMemory_RetainsLsuPlacement(HybridCpuOpcode opcode)
    {
        IrOpcodeExecutionProfile profile = HybridCpuHazardModel.GetExecutionProfile(opcode);
        Assert.Equal(IrIssueSlotMask.Slot4 | IrIssueSlotMask.Slot5, profile.StructurallyAllowedSlots);
        Assert.Equal(IrSlotClass.LsuClass, profile.DerivedSlotClass);
    }

    [Fact]
    public void MatrixFacts_AreCountedAndCannotOvercommitDmaAlias()
    {
        IrTypedSlotBundleFacts facts = new IrTypedSlotBundleFacts
        {
            Slot6Class = IrSlotClass.MatrixTileStreamClass,
            MatrixTileStreamCount = 1, PinnedOpCount = 1, PinningKindMask = 0x40
        };
        Assert.True(HybridCpuTypedSlotFactsEmitter.ValidateEmittedFacts(facts));
        Assert.False(HybridCpuTypedSlotFactsEmitter.ValidateEmittedFacts(facts with
            { MatrixTileStreamCount = 2, PinnedOpCount = 2 }));
        Assert.False(HybridCpuTypedSlotFactsEmitter.ValidateEmittedFacts(facts with
            { DmaStreamCount = 1, PinnedOpCount = 2 }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HelperBytesAndFacts_AgreeWithRuntimeAndRepeat(bool store)
    {
        CompilerEmissionPackage first = Build(store);
        CompilerEmissionPackage second = Build(store);
        InstructionsEnum opcode = store ? InstructionsEnum.MTILE_STORE : InstructionsEnum.MTILE_LOAD;
        CompilerToIseParitySnapshot parity = CompilerToIseParityHarness.AssertContourAndOpcode(
            first, ExecutionContourKind.MatrixTileHelperOnly, opcode);
        Assert.Equal([6], parity.OccupiedSlots);
        Assert.Equal(SlotClass.MatrixTileStreamClass, Assert.Single(parity.Slots).SlotClass);
        Assert.Equal(SlotPinningKind.HardPinned, Assert.Single(parity.Slots).PinningKind);
        byte[] bytes = first.Carrier!.Image.SerializedImage;
        Assert.Equal(256, bytes.Length);
        Assert.Equal((ushort)opcode, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6 * 32 + 6)));
        Assert.All(bytes.AsSpan(0, 6 * 32).ToArray(), value => Assert.Equal(0, value));
        Assert.All(bytes.AsSpan(7 * 32, 32).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(bytes, second.Carrier!.Image.SerializedImage);
        Assert.Equal(parity.TypedSlotFactFingerprint,
            CompilerToIseParityHarness.Capture(second).TypedSlotFactFingerprint);
        CompilerToIseParityHarness.AssertRuntimeAuthorityPending(first);
    }

    private static CompilerEmissionPackage Build(bool store)
    {
        HybridCpuThreadCompilerContext context = new HybridCpuThreadCompilerContext(0);
        CompilerMatrixTileTileOperand tile = CompilerMatrixTileTileOperand.Create(1);
        CompilerMatrixTileDescriptorAbi descriptor = CompilerMatrixTileDescriptorAbi.Create(2, 2, HybridCpuDataType.INT8);
        CompilerMatrixTileMemoryFaultAbiInputs memory = CompilerMatrixTileMemoryFaultAbiInputs.Create(0x100);
        if (store) context.CompileMtileStoreWithDecision(tile, descriptor, memory);
        else context.CompileMtileLoadWithDecision(tile, descriptor, memory);
        return HybridCpuCompiledProgramEnvelopeAdapter.Instance.Project(context.CompileProgram(),
            new CompilerArtifactProjectionOptions(SemanticIntentKind.MatrixTile,
                ExecutionContourKind.MatrixTileHelperOnly, "matrix-lane6-regression", "No runtime authority."));
    }
}
