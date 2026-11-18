using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Pipeline;
using YAKSys_Hybrid_CPU.Core.Registers.Retire;

namespace HybridCPU_ISE.Tests;

public sealed class RetireCycleTraceTests
{
    private sealed class TraceScalarMicroOp : MicroOp
    {
        private readonly ulong _value;

        public TraceScalarMicroOp(ushort destination, ulong value)
        {
            _value = value;
            OpCode = (uint)Processor.CPU_Core.InstructionsEnum.ADDI;
            DestRegID = destination;
            WritesRegister = true;
            OwnerThreadId = 0;
            VirtualThreadId = 0;
            OwnerContextId = 0;
            Class = MicroOpClass.Alu;
            InstructionClass = InstructionClass.ScalarAlu;
            SerializationClass = SerializationClass.Free;
            SetClassFlexiblePlacement(SlotClass.AluClass);
            RefreshAdmissionMetadata();
        }

        public override bool Execute(ref Processor.CPU_Core core) => true;

        public override void EmitWriteBackRetireRecords(
            ref Processor.CPU_Core core,
            Span<RetireRecord> records,
            ref int count)
        {
            AppendWriteBackRetireRecord(records, ref count,
                RetireRecord.RegisterWrite(VirtualThreadId, DestRegID, _value));
        }

        public override bool TryGetPrimaryWriteBackResult(out ulong value)
        {
            value = _value;
            return true;
        }

        public override void CapturePrimaryWriteBackResult(ulong value) { }
        public override string GetDescription() => "Synthetic staged WB cycle trace";
    }

    private static object Snapshot(string scenario, Processor.CPU_Core core)
    {
        Processor.CPU_Core.PipelineControl c = core.GetPipelineControl();
        return new
        {
            scenario,
            enabled = c.Enabled,
            stalled = c.Stalled,
            stallReason = c.StallReason.ToString(),
            cycle = c.CycleCount,
            stallCycles = c.StallCycles,
            memoryStalls = c.MemoryStalls,
            instructionsRetired = c.InstructionsRetired,
            scalarLanesRetired = c.ScalarLanesRetired,
            retireCycles = c.RetireCycleCount,
            wbValid = core.GetWriteBackStage().Valid,
            exValid = core.GetExecuteStage().Valid,
            memValid = core.GetMemoryStage().Valid,
            committedPc = core.ReadCommittedPc(0),
            livePc = core.ReadActiveLivePc(),
            r7 = core.ReadArch(0, 7)
        };
    }

    [Fact]
    public void EmitsMainlineWriteBackCycleStallReplayAndResetTrace()
    {
        List<object> observations = new();
        Processor.CPU_Core core = new(0);
        core.PrepareExecutionStart(0x7000, 0);
        observations.Add(Snapshot("execution-start", core));

        const ulong value = 0xABCD;
        TraceScalarMicroOp op = new(7, value);
        Processor.CPU_Core.ScalarWriteBackLaneState lane = new();
        lane.Clear(0);
        lane.IsOccupied = true;
        lane.PC = 0x7000;
        lane.OpCode = op.OpCode;
        lane.ResultValue = value;
        lane.WritesRegister = true;
        lane.DestRegID = 7;
        lane.MicroOp = op;
        lane.OwnerThreadId = 0;
        lane.VirtualThreadId = 0;
        lane.OwnerContextId = 0;
        lane.MshrScoreboardSlot = -1;
        Processor.CPU_Core.WriteBackStage wb = new();
        wb.Clear();
        wb.Valid = true;
        wb.ActiveLaneIndex = 0;
        wb.MaterializedScalarLaneCount = 1;
        wb.MaterializedPhysicalLaneCount = 1;
        wb.Lane0 = lane;
        core.TestSetWriteBackStage(wb);

        core.ExecutePipelineCycle();
        observations.Add(Snapshot("after-execute-pipeline-cycle-mainline-writeback", core));
        Assert.Equal(value, core.ReadArch(0, 7));
        Assert.Equal(1UL, core.GetPipelineControl().InstructionsRetired);
        Assert.Equal(1UL, core.GetPipelineControl().CycleCount);

        core.TestStageFullCycleForExplicitExecuteLaneNotReadyHazard(0x6400, 0);
        core.ExecutePipelineCycle();
        observations.Add(Snapshot("after-explicit-memory-wait-stall", core));
        Assert.True(core.GetPipelineControl().Stalled);
        Assert.Equal(Processor.CPU_Core.PipelineStallKind.MemoryWait,
            core.GetPipelineControl().StallReason);

        core.FlushPipeline(AssistInvalidationReason.Replay);
        observations.Add(Snapshot("after-replay-flush", core));
        Assert.False(core.GetPipelineControl().Stalled);
        Assert.False(core.GetExecuteStage().Valid);

        core.PrepareExecutionStart(0x8000, 0);
        observations.Add(Snapshot("after-execution-start-reset", core));
        Assert.Equal(0UL, core.GetPipelineControl().CycleCount);
        Assert.Equal(0x8000UL, core.ReadActiveLivePc());

        // Directly stage the typed CSR effect, then let ExecutePipelineCycle's
        // production write-back/retire path capture, prevalidate, and apply it.
        // This is a staged-WB seam test, not fetch/decode/issue equivalence.
        core.ExceptionStatus.OverflowCount = 3;
        core.ExceptionStatus.InvalidOpCount = 2;
        CsrClearMicroOp clear = new()
        {
            OpCode = (uint)Processor.CPU_Core.InstructionsEnum.CSR_CLEAR,
            OwnerThreadId = 0,
            VirtualThreadId = 0,
            OwnerContextId = 0
        };
        clear.InitializeMetadata();
        Processor.CPU_Core.ScalarWriteBackLaneState clearLane = new();
        clearLane.Clear(7);
        clearLane.IsOccupied = true;
        clearLane.PC = 0x8000;
        clearLane.OpCode = clear.OpCode;
        clearLane.MicroOp = clear;
        clearLane.GeneratedCsrEffect = CsrRetireEffect.ClearExceptionCounters();
        Processor.CPU_Core.WriteBackStage clearWb = new();
        clearWb.Clear();
        clearWb.Valid = true;
        clearWb.ActiveLaneIndex = 7;
        clearWb.Lane7 = clearLane;
        core.TestSetWriteBackStage(clearWb);
        core.ExecutePipelineCycle();
        observations.Add(new
        {
            scenario = "mainline-wb-csr-clear-retire",
            cycle = core.GetPipelineControl().CycleCount,
            retired = core.GetPipelineControl().InstructionsRetired,
            overflow = core.ExceptionStatus.OverflowCount,
            invalid = core.ExceptionStatus.InvalidOpCount
        });
        Assert.Equal(0U, core.ExceptionStatus.OverflowCount);
        Assert.Equal(0U, core.ExceptionStatus.InvalidOpCount);
        core.TestStageFullCycleForExplicitExecuteLaneNotReadyHazard(0x8400, 0);
        core.ExecutePipelineCycle();
        observations.Add(new
        {
            scenario = "mainline-csr-clear-persists-after-stall-cycle",
            stalled = core.GetPipelineControl().Stalled,
            overflow = core.ExceptionStatus.OverflowCount,
            invalid = core.ExceptionStatus.InvalidOpCount
        });
        Assert.Equal(0U, core.ExceptionStatus.OverflowCount);
        Assert.Equal(0U, core.ExceptionStatus.InvalidOpCount);
        core.FlushPipeline(AssistInvalidationReason.Replay);
        Assert.Equal(0U, core.ExceptionStatus.OverflowCount);
        Assert.Equal(0U, core.ExceptionStatus.InvalidOpCount);
        core.PrepareExecutionStart(0x9000, 0);
        observations.Add(new
        {
            scenario = "mainline-csr-clear-persists-after-reset",
            cycle = core.GetPipelineControl().CycleCount,
            overflow = core.ExceptionStatus.OverflowCount,
            invalid = core.ExceptionStatus.InvalidOpCount
        });
        Assert.Equal(0U, core.ExceptionStatus.OverflowCount);
        Assert.Equal(0U, core.ExceptionStatus.InvalidOpCount);

        string? path = Environment.GetEnvironmentVariable("CW_WRITEBACK_FAULT_TRACE");
        Assert.False(string.IsNullOrWhiteSpace(path));
        File.AppendAllText(path!, JsonSerializer.Serialize(new
        {
            scenario = "retire-cycle-state-sequence",
            observations
        }) + Environment.NewLine);
    }
}
