using HybridCPU_ISE.CloseToHSL.Memory.MMU;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Memory;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests;

public sealed class ExplicitPacketAssistLaneTests
{
    [Fact]
    public void ExplicitPacketAssistLane_WhenAssistExecutes_ThenPublishesAssistCarrierMemoryFacts()
    {
        const ulong baseAddress = 0x200;
        const byte elementSize = 4;

        InitializeCpuMainMemoryIdentityMap();

        var core = new Processor.CPU_Core(0);
        var assist = new AssistMicroOp(
            AssistKind.Ldsa,
            AssistExecutionMode.CachePrefetch,
            AssistCarrierKind.LsuHosted,
            baseAddress,
            prefetchLength: 32,
            elementSize,
            elementCount: 8,
            new AssistOwnerBinding(
                carrierVirtualThreadId: 0,
                donorVirtualThreadId: 0,
                targetVirtualThreadId: 0,
                ownerContextId: 0,
                domainTag: 0,
                replayEpochId: 0,
                assistEpochId: 0,
                LocalityHint.None));

        core.TestExecuteExplicitPacketLaneMicroOp(
            laneIndex: 4,
            assist,
            pc: 0x2600);

        Processor.CPU_Core.ExecuteStage executeStage = core.GetExecuteStage();
        Processor.CPU_Core.ScalarExecuteLaneState lane = executeStage.Lane4;

        Assert.True(executeStage.Valid);
        Assert.True(lane.IsOccupied);
        Assert.Same(assist, lane.MicroOp);
        Assert.True(lane.IsMemoryOp);
        Assert.True(lane.IsLoad);
        Assert.Equal(baseAddress, lane.MemoryAddress);
        Assert.Equal(elementSize, lane.MemoryAccessSize);
        Assert.True(lane.ResultReady);
        Assert.True(lane.VectorComplete);
        Assert.Equal(0UL, lane.ResultValue);
        Assert.Null(lane.GeneratedEvent);
        Assert.Null(lane.GeneratedCsrEffect);
        Assert.Null(lane.GeneratedAtomicEffect);
        Assert.Null(lane.GeneratedVmxEffect);
        Assert.Equal(0, lane.GeneratedRetireRecordCount);

        Processor.CPU_Core.PipelineControl control = core.GetPipelineControl();
        Assert.Equal(0UL, control.InstructionsRetired);
        Assert.Equal(0UL, control.ScalarLanesRetired);
        Assert.Equal(0UL, control.NonScalarLanesRetired);
    }

    private static void InitializeCpuMainMemoryIdentityMap()
    {
        IOMMU.Initialize();
        IOMMU.RegisterDevice(0);
        IOMMU.Map(
            deviceID: 0,
            ioVirtualAddress: 0,
            physicalAddress: 0,
            size: 0x100000000UL,
            permissions: IOMMUAccessPermissions.ReadWrite);
    }
}
