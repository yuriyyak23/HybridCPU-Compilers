using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;
using HybridCPU_ISE.Arch;
using HybridCPU_ISE.CloseToHSL.Core.Runtime.Managed;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Decoder;
using YAKSys_Hybrid_CPU.Core.Memory;
using YAKSys_Hybrid_CPU.Core.Registers;
using YAKSys_Hybrid_CPU.Memory;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests;

public sealed class PageFaultDeliveryRuntimeProbeTests
{
    [Fact]
    public void MemoryDelivery_ExecuteFaultOnly_EmitsSelectedWinnerAndThrownPayload()
    {
        Run("memory-over-execute", PipelineStage.None, 0, 0, false, 0xA100, false, 0xA200, true);
    }

    [Fact]
    public void MemoryDelivery_WriteBackFaultWins_EmitsWriteBackPayload()
    {
        Run("writeback-over-memory", PipelineStage.WriteBack, 7, 0xB100, true, 0xB200, false, 0xB300, true);
    }

    [Fact]
    public void MemoryDelivery_OlderMemoryCarrierWins_RecordsInputPayloadMismatch()
    {
        Run("dual-memory-winner", PipelineStage.None, 0, 0, false, 0xC100, false, 0xC200, true,
            competingMemoryAddress: 0xC050, competingMemoryLane: 2);
    }

    [Fact]
    public void ExecutePipelineCycle_MemoryReadFault_PropagatesAndFlushes()
    {
        Processor.MainMemoryArea originalMemory = Processor.MainMemory;
        MemorySubsystem? originalSubsystem = Processor.Memory;
        try
        {
            Processor.MainMemory = new Processor.MultiBankMemoryArea(4, 0x1000UL);
            Processor.Memory = null;
            Processor.CPU_Core core = new(0);
            core.InitializePipeline();
            core.Runtime.Execution.Control.Enabled = true;
            core.TestSeedSingleLaneExecuteForMemoryFollowThrough(
                isMemoryOp: true,
                isLoad: true,
                writesRegister: true,
                destRegId: 9,
                memoryAddress: 0xF000,
                memoryAccessSize: 8,
                pc: 0xD000,
                opCode: (uint)InstructionsEnum.LD);

            PageFaultException? delivered = null;
            try
            {
                core.ExecutePipelineCycle();
            }
            catch (PageFaultException exception)
            {
                delivered = exception;
            }
            Assert.NotNull(delivered);
            var row = new
            {
                scenario = "execute-pipeline-cycle-memory-read-fault",
                address = delivered!.FaultAddress,
                isWrite = delivered.IsWrite,
                memValidAfter = core.GetMemoryStage().Valid,
                exValidAfter = core.GetExecuteStage().Valid,
                retired = core.GetPipelineControl().InstructionsRetired
            };
            string trace = Environment.GetEnvironmentVariable("CW_PAGEFAULT_DELIVERY_TRACE")
                ?? throw new InvalidOperationException("CW_PAGEFAULT_DELIVERY_TRACE is required.");
            File.AppendAllText(trace, JsonSerializer.Serialize(row) + Environment.NewLine);
        }
        finally
        {
            Processor.MainMemory = originalMemory;
            Processor.Memory = originalSubsystem;
        }
    }

    [Fact]
    public void ManagedGuestRunner_MemoryReadFault_EscapesPublicApi()
    {
        const ulong imageBase = 0x1000;
        const ulong imageSize = 0x1000;
        const ulong entry = imageBase;
        const ulong stackBase = 0x2000;
        const ulong stackSize = 0x1000;
        const ulong returnSentinel = imageBase + 0x100;
        const ulong faultAddress = 0xF000;

        Processor.MultiBankMemoryArea memory = new(4, 0x1000UL);
        VLIW_Instruction load = InstructionEncoder.EncodeScalarImmediate(
            (uint)InstructionsEnum.LD,
            DataTypeEnum.INT64,
            destReg: 10,
            srcReg: 11,
            immediate: 0);
        VLIW_Bundle bundle = new();
        bundle.SetInstruction(0, load);
        VLIW_Instruction[] rawSlots = new VLIW_Instruction[BundleMetadata.BundleSlotCount];
        rawSlots[0] = load;
        DecodedInstructionBundle decoded = new VliwDecoderV4().DecodeInstructionBundle(rawSlots, bundleAddress: entry);
        Assert.Equal((ushort)10, decoded.GetDecodedSlot(0).Instruction!.Rd);
        Assert.Equal((ushort)11, decoded.GetDecodedSlot(0).Instruction!.Rs1);
        byte[] imageBundle = new byte[256];
        Assert.True(bundle.TryWriteBytes(imageBundle));
        Assert.True(memory.TryWritePhysicalRange(entry, imageBundle));

        const string imageDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        DeterministicRuntimeKernelV1 kernel = new();
        HybridCpuKernelBootResultV1 boot = kernel.Boot(new HybridCpuBootInfoV1(
            HybridCpuPlatformContractV1.ContractDigest,
            imageDigest,
            imageBase,
            imageSize,
            entry,
            stackBase,
            stackSize,
            InitialVirtualThreadId: 0));
        Assert.True(boot.IsSuccess);
        HybridCpuIseManagedImageLoadResultV1 loadResult = new(
            HybridCpuIseManagedImageLoadStatusV1.Success,
            string.Empty,
            boot.Context,
            TypeSystem: null,
            Heap: null,
            Strings: null,
            Gc: null,
            Bootstrap: null);
        HybridCpuIseManagedGuestExecutionRequestV1 request = new(
            imageBase,
            entry,
            returnSentinel,
            StackPointerRegister: 2,
            FramePointerRegister: 8,
            ThreadPointerRegister: 4,
            ReturnAddressRegister: 1,
            ReturnValueRegister: 10,
            GlobalPointerRegister: 3,
            StackPointer: stackBase + stackSize,
            FramePointer: stackBase + stackSize,
            ThreadPointer: 0,
            ReturnAddress: returnSentinel,
            GlobalPointer: 0,
            LoadResult: loadResult,
            VirtualThreadId: 0,
            MaximumPipelineCycles: 32,
            RunPendingInitializers: false,
            TreatReturnSentinelAsProcessExit: false,
            AdditionalRegisterSeeds: [new HybridCpuIseGuestRegisterSeedV1(11, faultAddress)]);

        PageFaultException? delivered = null;
        HybridCpuIseManagedGuestExecutionResultV1? runnerResult = null;
        try
        {
            runnerResult = new HybridCpuIseManagedGuestExecutionRunnerV1().Execute(request, memory, kernel);
        }
        catch (PageFaultException exception)
        {
            delivered = exception;
        }
        Assert.True(delivered is not null,
            $"Public runner returned {runnerResult?.Status}: {runnerResult?.Reason}; finalPc=0x{runnerResult?.FinalProgramCounter:x}; retireSeq={runnerResult?.LastRetireSequence}; lastBundle=0x{runnerResult?.LastRetiredBundlePc:x}.");
        Assert.Equal(faultAddress, delivered!.FaultAddress);
        Assert.False(delivered.IsWrite);
        string trace = Environment.GetEnvironmentVariable("CW_PAGEFAULT_DELIVERY_TRACE")
            ?? throw new InvalidOperationException("CW_PAGEFAULT_DELIVERY_TRACE is required.");
        File.AppendAllText(trace, JsonSerializer.Serialize(new
        {
            scenario = "managed-guest-runner-memory-read-fault-escapes",
            address = delivered.FaultAddress,
            isWrite = delivered.IsWrite,
            exceptionType = delivered.GetType().Name,
            escapedPublicApi = true
        }) + Environment.NewLine);
    }

    private static void Run(
        string scenario,
        PipelineStage wbStage,
        byte wbLaneIndex,
        ulong wbAddress,
        bool wbIsWrite,
        ulong incomingAddress,
        bool incomingIsWrite,
        ulong executeAddress,
        bool executeFault,
        ulong competingMemoryAddress = 0,
        byte competingMemoryLane = 0)
    {
        Processor.CPU_Core core = new(0);
        core.InitializePipeline();

        MemoryStage mem = new();
        mem.Clear();
        mem.Valid = true;
        mem.ActiveLaneIndex = 5;
        mem.UsesExplicitPacketLanes = true;
        mem.MaterializedPhysicalLaneCount = competingMemoryAddress == 0 ? 1 : 2;
        ScalarMemoryLaneState active = MakeMemoryLane(5, 5, incomingAddress, incomingIsWrite, fault: false);
        mem.SetLane(5, active);
        if (competingMemoryAddress != 0)
            mem.SetLane(competingMemoryLane, MakeMemoryLane(competingMemoryLane, 0, competingMemoryAddress, false, fault: true));

        WriteBackStage wb = new();
        wb.Clear();
        if (wbStage == PipelineStage.WriteBack)
        {
            wb.Valid = true;
            wb.ActiveLaneIndex = wbLaneIndex;
            wb.UsesExplicitPacketLanes = true;
            wb.MaterializedPhysicalLaneCount = 1;
            ScalarWriteBackLaneState lane = new();
            lane.Clear(wbLaneIndex);
            lane.IsOccupied = true;
            lane.SlotIndex = 0;
            lane.PC = 0x9000;
            lane.OwnerThreadId = 200 + wbLaneIndex;
            lane.VirtualThreadId = wbLaneIndex % 4;
            lane.CpuTranslationFault = default(CpuInstructionTranslationFault);
            lane.HasFault = true;
            lane.FaultAddress = wbAddress;
            lane.FaultIsWrite = wbIsWrite;
            wb.SetLane(wbLaneIndex, lane);
        }

        ExecuteStage ex = new();
        ex.Clear();
        if (executeFault)
        {
            ex.Valid = true;
            ex.ActiveLaneIndex = 1;
            ScalarExecuteLaneState lane = new();
            lane.Clear(1);
            lane.IsOccupied = true;
            lane.SlotIndex = 0;
            lane.PC = 0xA000;
            lane.OwnerThreadId = 300 + 1;
            lane.VirtualThreadId = 1;
            lane.HasFault = true;
            lane.FaultAddress = executeAddress;
            lane.FaultIsWrite = false;
            ex.SetLane(1, lane);
        }

        core.Runtime.MemoryPipeline.Memory = mem;
        core.Runtime.MemoryPipeline.WriteBack = wb;
        core.Runtime.Execution.Execute = ex;

        MemoryStage deliveryMem = mem;
        ScalarMemoryLaneState deliveredLane = deliveryMem.GetLane(deliveryMem.ActiveLaneIndex);
        deliveredLane.HasFault = true;
        deliveredLane.FaultAddress = incomingAddress;
        deliveredLane.FaultIsWrite = incomingIsWrite;
        deliveryMem.SetLane(deliveryMem.ActiveLaneIndex, deliveredLane);
        Assert.True(Processor.CPU_Core.TryResolveStageAwareExceptionWinnerMetadata(
            wb, deliveryMem, ex, out StageAwareExceptionWinnerMetadata winner));

        MethodInfo delivery = typeof(Processor.CPU_Core).GetMethod(
            "DeliverStageAwareMemoryPageFault", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MEM page-fault delivery method not found.");
        PageFaultException? delivered = null;
        try
        {
            delivery.Invoke(core, [new PageFaultException(incomingAddress, incomingIsWrite)]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is PageFaultException pageFault)
        {
            delivered = pageFault;
        }

        Assert.NotNull(delivered);
        var row = new
        {
            scenario,
            winnerStage = winner.WinnerStage.ToString(),
            winnerLane = winner.WinnerLaneIndex,
            winnerAddress = winner.FaultAddress,
            winnerIsWrite = winner.FaultIsWrite,
            winnerOwnerThreadId = winner.OwnerThreadId,
            winnerVirtualThreadId = winner.VirtualThreadId,
            winnerPc = winner.PC,
            winnerCpuTranslationFaultHasValue = winner.CpuTranslationFault.HasValue,
            inputAddress = incomingAddress,
            inputIsWrite = incomingIsWrite,
            thrownAddress = delivered!.FaultAddress,
            thrownIsWrite = delivered.IsWrite,
            memStageFlushed = !core.GetMemoryStage().Valid
        };
        string trace = Environment.GetEnvironmentVariable("CW_PAGEFAULT_DELIVERY_TRACE")
            ?? throw new InvalidOperationException("CW_PAGEFAULT_DELIVERY_TRACE is required.");
        File.AppendAllText(trace, JsonSerializer.Serialize(row) + Environment.NewLine);
    }

    private static ScalarMemoryLaneState MakeMemoryLane(byte laneIndex, byte slot, ulong address, bool isWrite, bool fault)
    {
        ScalarMemoryLaneState lane = new();
        lane.Clear(laneIndex);
        lane.IsOccupied = true;
        lane.SlotIndex = slot;
        lane.PC = 0x8000UL + laneIndex * 4UL;
        lane.OwnerThreadId = 100 + laneIndex;
        lane.VirtualThreadId = laneIndex % 4;
        lane.HasFault = fault;
        lane.FaultAddress = address;
        lane.FaultIsWrite = isWrite;
        return lane;
    }
}
