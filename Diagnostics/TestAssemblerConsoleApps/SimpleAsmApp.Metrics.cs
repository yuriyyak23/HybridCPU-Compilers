using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR;
using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed partial class SimpleAsmApp
{
    private SimpleAsmAppMetrics CreateMetrics(
        Processor.CPU_Core.PipelineControl pipeline,
        PerformanceReport performance,
        string compilerStage,
        string decoderStage,
        string likelyFailingStage,
        string failureMessage,
        uint firstOpcode,
        bool firstOpcodeRegistered,
        int bundleCount,
        FrontendMode frontendMode,
        bool frontendSupported,
        SimpleAsmProgramVariant programVariant,
        CompilerPackingDiagnostics compilerPacking,
        ShowcaseRuntimeReport showcaseRuntime)
    {
        return new SimpleAsmAppMetrics(
            pipeline.CycleCount == 0 ? 0.0 : pipeline.GetIPC(),
            pipeline.InstructionsRetired,
            pipeline.CycleCount,
            pipeline.StallCycles,
            pipeline.DataHazards,
            pipeline.MemoryStalls,
            pipeline.LoadUseBubbles,
            pipeline.WAWHazards,
            pipeline.ControlHazards,
            pipeline.BranchMispredicts,
            pipeline.FrontendStalls,
            GetScalarIssueWidthHistogramValue(pipeline, 0),
            GetScalarIssueWidthHistogramValue(pipeline, 1),
            GetScalarIssueWidthHistogramValue(pipeline, 2),
            GetScalarIssueWidthHistogramValue(pipeline, 3),
            GetScalarIssueWidthHistogramValue(pipeline, 4),
            Convert.ToUInt64(performance.TotalBursts),
            Convert.ToUInt64(performance.TotalBytesTransferred),
            Convert.ToUInt64(performance.NopAvoided),
            Convert.ToUInt64(performance.NopDueToNoClassCapacity),
            Convert.ToUInt64(performance.NopDueToPinnedConstraint),
            Convert.ToUInt64(performance.NopDueToResourceConflict),
            Convert.ToUInt64(performance.NopDueToDynamicState),
            Convert.ToUInt64(performance.ClassFlexibleInjects),
            Convert.ToUInt64(performance.HardPinnedInjects),
            Convert.ToUInt64(performance.EligibilityMaskedCycles),
            Convert.ToUInt64(performance.EligibilityMaskedReadyCandidates),
            unchecked((byte)performance.LastEligibilityRequestedMask),
            unchecked((byte)performance.LastEligibilityNormalizedMask),
            unchecked((byte)performance.LastEligibilityReadyPortMask),
            unchecked((byte)performance.LastEligibilityVisibleReadyMask),
            unchecked((byte)performance.LastEligibilityMaskedReadyMask),
            pipeline.MultiLaneExecuteCount,
            pipeline.ClusterPreparedExecutionChoiceCount,
            pipeline.WidePathSuccessCount,
            pipeline.PartialWidthIssueCount,
            pipeline.DecoderPreparedScalarGroupCount,
            pipeline.VTSpreadPerBundle,
            pipeline.IssuePacketPreparedLaneCountSum,
            pipeline.IssuePacketMaterializedLaneCountSum,
            pipeline.IssuePacketPreparedPhysicalLaneCountSum,
            pipeline.IssuePacketMaterializedPhysicalLaneCountSum,
            pipeline.IssuePacketWidthDropCount,
            0,
            ComputeAverageActiveVtPerWindow(),
            ComputeMaxActiveVtPerWindow(),
            CountNonZeroVtInstructions(),
            _emittedVirtualThreadIds.Count,
            ComputeAverageActiveVtPerWindow(),
            ComputeMaxActiveVtPerWindow(),
            CountNonZeroVtInstructions(),
            compilerStage,
            decoderStage,
            likelyFailingStage,
            failureMessage,
            _emittedVirtualThreadIds.Count,
            bundleCount,
            firstOpcode,
            firstOpcodeRegistered,
            frontendMode.ToString(),
            frontendSupported,
            programVariant.ToString(),
            compilerPacking.EmittedDistinctVirtualThreadCount,
            compilerPacking.IrDistinctVirtualThreadCount,
            compilerPacking.ScheduleCycleGroupCount,
            compilerPacking.ScheduleCrossVtCycleGroupCount,
            compilerPacking.ScheduleAverageWidth,
            compilerPacking.ScheduleAverageVtSpread,
            compilerPacking.ScheduleMaxVtSpread,
            compilerPacking.BundleCount,
            compilerPacking.BundleCrossVtCount,
            compilerPacking.BundleAverageVtSpread,
            compilerPacking.BundleMaxVtSpread,
            pipeline.NopElisionSkipCount,
            pipeline.ScalarLanesRetired,
            pipeline.NonScalarLanesRetired,
            pipeline.RetireCycleCount,
            showcaseRuntime.Executed,
            showcaseRuntime.CoversFsp,
            showcaseRuntime.CoversTypedSlot,
            showcaseRuntime.CoversAdmission,
            showcaseRuntime.CoversSurfaceContract,
            showcaseRuntime.CoversVector,
            showcaseRuntime.CoversStream,
            showcaseRuntime.CoversCsr,
            showcaseRuntime.CoversSystem,
            showcaseRuntime.CoversVmx,
            showcaseRuntime.CoversObservability,
            showcaseRuntime.AssistRuntimeStatus,
            showcaseRuntime.TraceEventCount,
            showcaseRuntime.PipelineEventCount,
            showcaseRuntime.FsmTransitionCount,
            showcaseRuntime.DirectTelemetryInstrRetired,
            showcaseRuntime.DirectBarrierCount,
            showcaseRuntime.DirectVmExitCount,
            showcaseRuntime.FinalPipelineState,
            Convert.ToUInt64(performance.PhaseCertificateReadyHits),
            Convert.ToUInt64(performance.PhaseCertificateReadyMisses),
            Convert.ToUInt64(performance.EstimatedPhaseCertificateChecksSaved),
            Convert.ToUInt64(performance.PhaseCertificateInvalidations),
            Convert.ToUInt64(performance.PhaseCertificateMutationInvalidations),
            Convert.ToUInt64(performance.PhaseCertificatePhaseMismatchInvalidations),
            Convert.ToUInt64(performance.L1BypassHits),
            Convert.ToUInt64(performance.ForegroundWarmAttempts),
            Convert.ToUInt64(performance.ForegroundWarmSuccesses),
            Convert.ToUInt64(performance.ForegroundWarmReuseHits),
            Convert.ToUInt64(performance.ForegroundBypassHits),
            Convert.ToUInt64(performance.AssistWarmAttempts),
            Convert.ToUInt64(performance.AssistWarmSuccesses),
            Convert.ToUInt64(performance.AssistWarmReuseHits),
            Convert.ToUInt64(performance.AssistBypassHits),
            Convert.ToUInt64(performance.StreamWarmTranslationRejects),
            Convert.ToUInt64(performance.StreamWarmBackendRejects),
            Convert.ToUInt64(performance.AssistWarmResidentBudgetRejects),
            Convert.ToUInt64(performance.AssistWarmLoadingBudgetRejects),
            Convert.ToUInt64(performance.AssistWarmNoVictimRejects),
            Convert.ToUInt64(performance.SmtOwnerContextGuardRejects),
            Convert.ToUInt64(performance.SmtDomainGuardRejects),
            Convert.ToUInt64(performance.SmtBoundaryGuardRejects),
            Convert.ToUInt64(performance.SmtSharedResourceCertificateRejects),
            Convert.ToUInt64(performance.SmtRegisterGroupCertificateRejects),
            Convert.ToUInt64(performance.SmtLegalityRejectByAluClass),
            Convert.ToUInt64(performance.SmtLegalityRejectByLsuClass),
            Convert.ToUInt64(performance.SmtLegalityRejectByDmaStreamClass),
            Convert.ToUInt64(performance.SmtLegalityRejectByBranchControl),
            Convert.ToUInt64(performance.SmtLegalityRejectBySystemSingleton),
            performance.LastSmtLegalityRejectKind.ToString(),
            performance.LastSmtLegalityAuthoritySource.ToString(),
            _requestedWorkloadIterations,
            _loopBodyInstructionCount,
            _dynamicRetirementTarget != 0 ? _dynamicRetirementTarget : (ulong)_emittedVirtualThreadIds.Count,
            _workloadShape,
            _sliceExecutionCount,
            _referenceSliceIterations,
            performance.MemoryCycleTelemetrySchemaVersion,
            performance.MemoryCycleTelemetryAvailable,
            Convert.ToUInt64(performance.MemoryControllerCycles),
            Convert.ToUInt64(performance.MemoryReadServiceCycles),
            Convert.ToUInt64(performance.MemoryStoreReadinessServiceCycles),
            Convert.ToUInt64(performance.MemoryCompletionPublicationCycles),
            Convert.ToUInt64(performance.MemoryAcceptedRequests),
            Convert.ToUInt64(performance.MemoryCompletedRequests),
            Convert.ToUInt64(performance.DataReadAcceptedRequests),
            Convert.ToUInt64(performance.DataReadCompletedRequests),
            Convert.ToUInt64(performance.DataWriteAcceptedRequests),
            Convert.ToUInt64(performance.DataWriteCompletedRequests),
            Convert.ToUInt64(performance.DataReadBytes),
            Convert.ToUInt64(performance.CommittedDataWriteBytes),
            performance.InstructionFetchReadBytesTelemetryAvailable,
            Convert.ToUInt64(performance.InstructionFetchReadBytes),
            performance.InstructionFetchRequestTelemetryAvailable,
            Convert.ToUInt64(performance.MemoryQueueFullRejects),
            performance.MemoryBankConflictRejectTelemetryAvailable,
            Convert.ToUInt64(performance.MemoryBankConflictRejects),
            Convert.ToUInt64(performance.MemoryTelemetryBaselineOutstandingRequests),
            Convert.ToUInt64(performance.MemoryCanceledRequests),
            Convert.ToUInt64(performance.MemoryConsumedCompletions),
            Convert.ToUInt64(performance.MemoryOutstandingRequests));
    }

    private CompilerPackingDiagnostics AnalyzeCompilerPacking(HybridCpuCompiledProgram compiledProgram)
    {
        ArgumentNullException.ThrowIfNull(compiledProgram);

        int emittedDistinctVirtualThreadCount = CountDistinctEmittedVirtualThreads();
        int irDistinctVirtualThreadCount = CountDistinctInstructionVirtualThreads(compiledProgram.ProgramSchedule.Program.Instructions);
        PackingAggregate scheduleAggregate = AnalyzeSchedulePacking(compiledProgram.ProgramSchedule);
        PackingAggregate bundleAggregate = AnalyzeBundlePacking(compiledProgram.BundleLayout);

        return new CompilerPackingDiagnostics(
            emittedDistinctVirtualThreadCount,
            irDistinctVirtualThreadCount,
            scheduleAggregate.ItemCount,
            scheduleAggregate.CrossVtItemCount,
            scheduleAggregate.AverageWidth,
            scheduleAggregate.AverageVtSpread,
            scheduleAggregate.MaxVtSpread,
            bundleAggregate.ItemCount,
            bundleAggregate.CrossVtItemCount,
            bundleAggregate.AverageVtSpread,
            bundleAggregate.MaxVtSpread);
    }

    private static PackingAggregate AnalyzeSchedulePacking(IrProgramSchedule programSchedule)
    {
        ArgumentNullException.ThrowIfNull(programSchedule);

        int itemCount = 0;
        int crossVtItemCount = 0;
        int widthSum = 0;
        int vtSpreadSum = 0;
        int maxVtSpread = 0;

        foreach (IrBasicBlockSchedule blockSchedule in programSchedule.BlockSchedules)
        {
            foreach (IrScheduleCycleGroup cycleGroup in blockSchedule.CycleGroups)
            {
                int width = cycleGroup.Instructions.Count;
                int vtSpread = CountDistinctInstructionVirtualThreads(cycleGroup.Instructions);
                itemCount++;
                widthSum += width;
                vtSpreadSum += vtSpread;
                maxVtSpread = Math.Max(maxVtSpread, vtSpread);
                if (vtSpread > 1)
                {
                    crossVtItemCount++;
                }
            }
        }

        return new PackingAggregate(itemCount, crossVtItemCount, widthSum, vtSpreadSum, maxVtSpread);
    }

    private static PackingAggregate AnalyzeBundlePacking(IrProgramBundlingResult bundleLayout)
    {
        ArgumentNullException.ThrowIfNull(bundleLayout);

        int itemCount = 0;
        int crossVtItemCount = 0;
        int widthSum = 0;
        int vtSpreadSum = 0;
        int maxVtSpread = 0;

        foreach (IrBasicBlockBundlingResult blockResult in bundleLayout.BlockResults)
        {
            foreach (IrMaterializedBundle bundle in blockResult.Bundles)
            {
                int width = bundle.IssuedInstructionCount;
                int vtSpread = CountDistinctVirtualThreads(bundle);
                itemCount++;
                widthSum += width;
                vtSpreadSum += vtSpread;
                maxVtSpread = Math.Max(maxVtSpread, vtSpread);
                if (vtSpread > 1)
                {
                    crossVtItemCount++;
                }
            }
        }

        return new PackingAggregate(itemCount, crossVtItemCount, widthSum, vtSpreadSum, maxVtSpread);
    }

    private int CountDistinctEmittedVirtualThreads()
    {
        bool vt0 = false;
        bool vt1 = false;
        bool vt2 = false;
        bool vt3 = false;

        for (int index = 0; index < _emittedVirtualThreadIds.Count; index++)
        {
            MarkVirtualThread(_emittedVirtualThreadIds[index], ref vt0, ref vt1, ref vt2, ref vt3);
        }

        return CountMarkedVirtualThreads(vt0, vt1, vt2, vt3);
    }

    private static int CountDistinctInstructionVirtualThreads(IReadOnlyList<IrInstruction> instructions)
    {
        bool vt0 = false;
        bool vt1 = false;
        bool vt2 = false;
        bool vt3 = false;

        for (int index = 0; index < instructions.Count; index++)
        {
            MarkVirtualThread(instructions[index].VirtualThreadId, ref vt0, ref vt1, ref vt2, ref vt3);
        }

        return CountMarkedVirtualThreads(vt0, vt1, vt2, vt3);
    }

    private static int CountDistinctVirtualThreads(IrMaterializedBundle bundle)
    {
        bool vt0 = false;
        bool vt1 = false;
        bool vt2 = false;
        bool vt3 = false;

        for (int slotIndex = 0; slotIndex < bundle.Slots.Count; slotIndex++)
        {
            IrInstruction? instruction = bundle.Slots[slotIndex].Instruction;
            if (instruction is null)
            {
                continue;
            }

            MarkVirtualThread(instruction.VirtualThreadId, ref vt0, ref vt1, ref vt2, ref vt3);
        }

        return CountMarkedVirtualThreads(vt0, vt1, vt2, vt3);
    }

    private static void MarkVirtualThread(int virtualThreadId, ref bool vt0, ref bool vt1, ref bool vt2, ref bool vt3)
    {
        switch (virtualThreadId)
        {
            case 0:
                vt0 = true;
                break;
            case 1:
                vt1 = true;
                break;
            case 2:
                vt2 = true;
                break;
            case 3:
                vt3 = true;
                break;
        }
    }

    private static int CountMarkedVirtualThreads(bool vt0, bool vt1, bool vt2, bool vt3)
    {
        return (vt0 ? 1 : 0) + (vt1 ? 1 : 0) + (vt2 ? 1 : 0) + (vt3 ? 1 : 0);
    }

    internal readonly record struct PackingAggregate(
        int ItemCount,
        int CrossVtItemCount,
        int WidthSum,
        int VtSpreadSum,
        int MaxVtSpread)
    {
        public double AverageWidth => ItemCount == 0 ? 0.0 : (double)WidthSum / ItemCount;

        public double AverageVtSpread => ItemCount == 0 ? 0.0 : (double)VtSpreadSum / ItemCount;
    }

    internal readonly record struct CompilerPackingDiagnostics(
        int EmittedDistinctVirtualThreadCount,
        int IrDistinctVirtualThreadCount,
        int ScheduleCycleGroupCount,
        int ScheduleCrossVtCycleGroupCount,
        double ScheduleAverageWidth,
        double ScheduleAverageVtSpread,
        int ScheduleMaxVtSpread,
        int BundleCount,
        int BundleCrossVtCount,
        double BundleAverageVtSpread,
        int BundleMaxVtSpread)
    {
        public static CompilerPackingDiagnostics Empty => new(0, 0, 0, 0, 0.0, 0.0, 0, 0, 0, 0.0, 0);
    }

    private static ulong GetScalarIssueWidthHistogramValue(Processor.CPU_Core.PipelineControl pipeline, int width)
    {
        return pipeline.ScalarIssueWidthHistogram is { Length: > 0 } histogram && width >= 0 && width < histogram.Length
            ? histogram[width]
            : 0;
    }

    private int CountNonZeroVtInstructions()
    {
        int count = 0;
        for (int index = 0; index < _emittedVirtualThreadIds.Count; index++)
        {
            if (_emittedVirtualThreadIds[index] != 0)
            {
                count++;
            }
        }

        return count;
    }

    private double ComputeAverageActiveVtPerWindow()
    {
        if (_emittedVirtualThreadIds.Count == 0)
        {
            return 0.0;
        }

        int windowCount = 0;
        int activeVtSum = 0;
        for (int start = 0; start < _emittedVirtualThreadIds.Count; start += 8)
        {
            activeVtSum += CountActiveVirtualThreadsInWindow(start);
            windowCount++;
        }

        return windowCount == 0 ? 0.0 : (double)activeVtSum / windowCount;
    }

    private int ComputeMaxActiveVtPerWindow()
    {
        int maxActiveVt = 0;
        for (int start = 0; start < _emittedVirtualThreadIds.Count; start += 8)
        {
            int activeVt = CountActiveVirtualThreadsInWindow(start);
            if (activeVt > maxActiveVt)
            {
                maxActiveVt = activeVt;
            }
        }

        return maxActiveVt;
    }

    private int CountActiveVirtualThreadsInWindow(int start)
    {
        bool vt0 = false;
        bool vt1 = false;
        bool vt2 = false;
        bool vt3 = false;
        int end = Math.Min(start + 8, _emittedVirtualThreadIds.Count);

        for (int index = start; index < end; index++)
        {
            switch (_emittedVirtualThreadIds[index])
            {
                case 0:
                    vt0 = true;
                    break;
                case 1:
                    vt1 = true;
                    break;
                case 2:
                    vt2 = true;
                    break;
                case 3:
                    vt3 = true;
                    break;
            }
        }

        return (vt0 ? 1 : 0) + (vt1 ? 1 : 0) + (vt2 ? 1 : 0) + (vt3 ? 1 : 0);
    }
}
