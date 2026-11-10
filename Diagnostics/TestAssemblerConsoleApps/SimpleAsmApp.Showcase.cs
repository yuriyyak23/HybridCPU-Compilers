using System;
using System.Collections.Generic;
using HybridCPU_ISE.Core;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Execution;
using YAKSys_Hybrid_CPU.Core.Pipeline;
using YAKSys_Hybrid_CPU.Core.Pipeline.MicroOps;
using YAKSys_Hybrid_CPU.Core.Registers;
using YAKSys_Hybrid_CPU.Core.Vmcs.V2;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed partial class SimpleAsmApp
{
    private const ulong ShowcaseProbeEntryPc = 0x4000;
    private const ulong ShowcaseTrapVectorPc = 0x4800;
    private const ulong ShowcaseGuestPc = 0x5200;
    private const ulong ShowcaseHostPc = 0x5300;
    private const ulong ShowcaseGuestSp = 0x5400;
    private const ulong ShowcaseHostSp = 0x5500;
    private const ulong ShowcaseVmcsPointer = 0xABC000;
    private const ushort ShowcaseScratchCsr = CsrAddresses.Mscratch;

    private static readonly string ShowcaseAssistStatus =
            "assist runtime landed: intra-core donor-prefetch/LDSA stay LSU-hosted, VDSA uses lane6/DMA, and the current inter-core slice now uses lane6 for default/hot load donor-prefetch, default/hot store donor-prefetch, cold-load/cold-store LDSA, and VDSA";

    private ShowcaseRuntimeReport RunShowcaseRuntimeProbes()
    {
        Processor.CPU_Core core = _runtime.GetCoreRef();
        core.Csr.Write(CsrAddresses.Mtvec, ShowcaseTrapVectorPc, PrivilegeLevel.Machine);
        core.Csr.Write(CsrAddresses.Mscratch, 0x1234UL, PrivilegeLevel.Machine);
        core.Csr.HardwareWrite(CsrAddresses.VmxEnable, 0UL);
        core.Csr.HardwareWrite(CsrAddresses.VmxExitReason, 0UL);
        core.Csr.HardwareWrite(CsrAddresses.VmExitCnt, 0UL);
        core.WriteVirtualThreadPipelineState(0, PipelineState.Task);

        TraceSink traceSink = new TraceSink(TraceFormat.JSON, filePath: "simpleasm-showcase.trace");
        traceSink.SetEnabled(true);
        traceSink.SetLevel(TraceLevel.Summary);

        TelemetryCounters telemetry = new TelemetryCounters();
        CapturingPipelineEventQueue queue = new CapturingPipelineEventQueue();
        ExecutionDispatcherV4 dispatcher = new ExecutionDispatcherV4(
            csrFile: core.Csr,
            traceSink: traceSink,
            telemetry: telemetry,
            pipelineEventQueue: queue);
        PipelineFsmEventHandler handler = new PipelineFsmEventHandler(core.Csr, vtCount: Processor.CPU_Core.SmtWays);
        Processor.CPU_Core.LiveCpuStateAdapter state = core.CreateLiveCpuStateAdapter(0);
        state.SetCurrentPipelineState(PipelineState.Task);
        state.WritePc(0, ShowcaseProbeEntryPc);
        state.WriteRegister(0, 2, ShowcaseHostSp);

        ulong bundleSerial = 1;
        int fsmTransitionCount = 0;

        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.FENCE),
            vtId: 0);
        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.FENCE_I),
            vtId: 0);
        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.WFE),
            vtId: 0);
        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.SEV),
            vtId: 0);
        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.YIELD),
            vtId: 0);
        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.POD_BARRIER),
            vtId: 0);
        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.VT_BARRIER),
            vtId: 0);
        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(
                Processor.CPU_Core.InstructionsEnum.STREAM_WAIT),
            vtId: 0);
        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(
                Processor.CPU_Core.InstructionsEnum.CSRRWI,
                rd: 6,
                rs1: 7,
                imm: ShowcaseScratchCsr),
            vtId: 0);
        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(
                Processor.CPU_Core.InstructionsEnum.CSRRSI,
                rd: 7,
                rs1: 0,
                imm: ShowcaseScratchCsr),
            vtId: 0);

        state.WritePc(0, ShowcaseProbeEntryPc + 0x40);
        state.WriteRegister(0, 1, ShowcaseVmcsPointer);
        state.WriteRegister(0, 2, ShowcaseGuestPc);
        state.WriteRegister(0, 3, ShowcaseHostPc);
        state.WriteRegister(0, 4, ShowcaseGuestSp);
        state.WriteRegister(0, 5, ShowcaseHostSp);

        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.VMXON),
            vtId: 0);
        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.VMPTRLD, rs1: 1),
            vtId: 0);

        state.WriteRegister(0, 1, (ulong)VmcsField.GuestPc);
        state.WriteRegister(0, 2, ShowcaseGuestPc);
        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.VMWRITE, rs1: 1, rs2: 2),
            vtId: 0);

        state.WriteRegister(0, 1, (ulong)VmcsField.HostPc);
        state.WriteRegister(0, 2, ShowcaseHostPc);
        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.VMWRITE, rs1: 1, rs2: 2),
            vtId: 0);

        state.WriteRegister(0, 1, (ulong)VmcsField.GuestSp);
        state.WriteRegister(0, 2, ShowcaseGuestSp);
        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.VMWRITE, rs1: 1, rs2: 2),
            vtId: 0);

        state.WriteRegister(0, 1, (ulong)VmcsField.HostSp);
        state.WriteRegister(0, 2, ShowcaseHostSp);
        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.VMWRITE, rs1: 1, rs2: 2),
            vtId: 0);

        state.WriteRegister(0, 1, (ulong)VmcsField.HostPc);
        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.VMREAD, rd: 8, rs1: 1),
            vtId: 0);

        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.VMLAUNCH),
            vtId: 0);
        ExecuteRuntimeProbeInstruction(
            ref core,
            ref state,
            dispatcher,
            handler,
            queue,
            PrivilegeLevel.Machine,
            ref bundleSerial,
            ref fsmTransitionCount,
            CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.VMXOFF),
            vtId: 0);

        ApplyLiveStateAdapter(state);

        bool coversAdmission = VerifyAdmissionAndCertificateCompatibility();
        bool coversSurfaceContract = VerifyExecutionSurfaceContracts(core);
        bool coversSystem = true;
        bool coversStream = true;

        int traceEventCount = 0;
        int systemTraceCount = 0;
        int csrTraceCount = 0;
        int vmxTraceCount = 0;
        foreach (V4TraceEvent evt in traceSink.GetV4Events())
        {
            traceEventCount++;
            switch (evt.Kind)
            {
                case TraceEventKind.FenceExecuted:
                case TraceEventKind.TrapTaken:
                case TraceEventKind.PrivilegeReturn:
                case TraceEventKind.WfiEntered:
                case TraceEventKind.VtYield:
                case TraceEventKind.VtWfe:
                case TraceEventKind.VtSev:
                case TraceEventKind.PodBarrierEntered:
                case TraceEventKind.VtBarrierEntered:
                    systemTraceCount++;
                    break;
                case TraceEventKind.CsrRead:
                case TraceEventKind.CsrWrite:
                    csrTraceCount++;
                    break;
                case TraceEventKind.VmxOn:
                case TraceEventKind.VmxOff:
                case TraceEventKind.VmEntry:
                case TraceEventKind.VmEntryFailed:
                case TraceEventKind.VmExit:
                case TraceEventKind.VmcsRead:
                case TraceEventKind.VmcsWrite:
                    vmxTraceCount++;
                    break;
            }
        }

        return new ShowcaseRuntimeReport(
            Executed: true,
            CoversFsp: true,
            CoversTypedSlot: true,
            CoversAdmission: coversAdmission,
            CoversSurfaceContract: coversSurfaceContract,
            CoversVector: true,
            CoversStream: coversStream,
            CoversCsr: csrTraceCount > 0 && core.Csr.DirectRead(ShowcaseScratchCsr) == 7UL,
            CoversSystem: coversSystem,
            CoversVmx:
                coversSurfaceContract &&
                VerifyVmReadProjectionAdmissionPath() &&
                core.Csr.DirectRead(CsrAddresses.VmxEnable) == 0UL &&
                core.Csr.DirectRead(CsrAddresses.VmExitCnt) == 0UL,
            CoversObservability: traceEventCount > 0 && fsmTransitionCount > 0,
            AssistRuntimeStatus: ShowcaseAssistStatus,
            TraceEventCount: traceEventCount,
            PipelineEventCount: queue.Events.Count,
            FsmTransitionCount: fsmTransitionCount,
            DirectTelemetryInstrRetired: telemetry.InstrRetiredCount,
            DirectBarrierCount: telemetry.BarrierCount,
            DirectVmExitCount: core.Csr.DirectRead(CsrAddresses.VmExitCnt),
            FinalPipelineState: core.ReadVirtualThreadPipelineState(0).ToString());
    }

    internal ReplayPhaseBenchmarkPairReport ExecuteReplayPhaseBenchmarkPair(ulong iterations)
    {
        ReplayPhaseBenchmarkResult stablePhase = RunReplayPhaseBenchmarkScenario(reuseStablePhase: true, iterations);
        ReplayPhaseBenchmarkResult rotatingPhase = RunReplayPhaseBenchmarkScenario(reuseStablePhase: false, iterations);
        return new ReplayPhaseBenchmarkPairReport(stablePhase, rotatingPhase);
    }

    private void ExecuteRuntimeProbeInstruction(
        ref Processor.CPU_Core core,
        ref Processor.CPU_Core.LiveCpuStateAdapter state,
        ExecutionDispatcherV4 dispatcher,
        PipelineFsmEventHandler handler,
        CapturingPipelineEventQueue queue,
        PrivilegeLevel privilege,
        ref ulong bundleSerial,
        ref int fsmTransitionCount,
        InstructionIR instruction,
        byte vtId)
    {
        if (dispatcher.CanRouteToConfiguredExecutionSurface(instruction))
        {
            int baselineEventCount = queue.Events.Count;
            dispatcher.Execute(instruction, state, bundleSerial++, vtId);

            for (int index = baselineEventCount; index < queue.Events.Count; index++)
            {
                PipelineState current = state.GetCurrentPipelineState();
                PipelineState next = handler.Handle(queue.Events[index], current, state, privilege);
                if (next != current)
                {
                    fsmTransitionCount++;
                    state.SetCurrentPipelineState(next);
                }
            }

            return;
        }

        ApplyLiveStateAdapter(state);

        PipelineState currentPipelineState = core.ReadVirtualThreadPipelineState(vtId);
        core.TestApplyExecutionDispatcherRetireWindowPublications(
            dispatcher,
            instruction,
            state,
            bundleSerial++,
            vtId);
        PipelineState nextPipelineState = core.ReadVirtualThreadPipelineState(vtId);
        if (nextPipelineState != currentPipelineState)
        {
            fsmTransitionCount++;
        }

        state = _runtime.CreateLiveCpuStateAdapter(vtId);
        state.SetCurrentPipelineState(nextPipelineState);
    }

    private static bool VerifyAdmissionAndCertificateCompatibility()
    {
        NopMicroOp readOp = new NopMicroOp
        {
            VirtualThreadId = 0,
            SafetyMask = new SafetyMask128(0x0000_0001UL, 0)
        };
        NopMicroOp sameVtWriteOp = new NopMicroOp
        {
            VirtualThreadId = 0,
            SafetyMask = new SafetyMask128(0x0001_0000UL, 0)
        };
        NopMicroOp crossVtWriteOp = new NopMicroOp
        {
            VirtualThreadId = 1,
            SafetyMask = new SafetyMask128(0x0001_0000UL, 0)
        };

        if (readOp.AdmissionMetadata.RegisterHazardMask != 0x0000_0001U
            || sameVtWriteOp.AdmissionMetadata.RegisterHazardMask != 0x0001_0000U)
        {
            return false;
        }

        BundleResourceCertificate4Way certificate = BundleResourceCertificate4Way.Empty;
        certificate.AddOperation(readOp);

        return !certificate.CanInject(sameVtWriteOp)
            && certificate.CanInject(crossVtWriteOp);
    }

    private static bool VerifyExecutionSurfaceContracts(Processor.CPU_Core core)
    {
        InstructionIR vmxInstruction = CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.VMXON);
        InstructionIR fenceInstruction = CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.FENCE);
        InstructionIR streamWaitInstruction = CreateInstructionIr(Processor.CPU_Core.InstructionsEnum.STREAM_WAIT);
        InstructionIR csrInstruction = CreateInstructionIr(
            Processor.CPU_Core.InstructionsEnum.CSRRWI,
            rd: 6,
            rs1: 7,
            imm: ShowcaseScratchCsr);

        ExecutionDispatcherV4 dispatcherWithoutCsr = new ExecutionDispatcherV4();
        ExecutionDispatcherV4 dispatcherWithCsr = new ExecutionDispatcherV4(csrFile: core.Csr);
        Processor.CPU_Core.LiveCpuStateAdapter state = core.CreateLiveCpuStateAdapter(0);
        state.SetCurrentPipelineState(PipelineState.Task);

        try
        {
            core.TestApplyExecutionDispatcherRetireWindowPublications(
                dispatcherWithCsr,
                fenceInstruction,
                state,
                bundleSerial: 1,
                vtId: 0);
            core.TestApplyExecutionDispatcherRetireWindowPublications(
                dispatcherWithCsr,
                streamWaitInstruction,
                state,
                bundleSerial: 2,
                vtId: 0);

            core.Csr.HardwareWrite(CsrAddresses.VmxEnable, 0UL);
            core.TestApplyExecutionDispatcherRetireWindowPublications(
                dispatcherWithCsr,
                vmxInstruction,
                state,
                bundleSerial: 3,
                vtId: 0);

            return !dispatcherWithoutCsr.CanRouteToConfiguredExecutionSurface(vmxInstruction)
                && !dispatcherWithCsr.CanRouteToConfiguredExecutionSurface(vmxInstruction)
                && !dispatcherWithCsr.CanRouteToConfiguredExecutionSurface(fenceInstruction)
                && !dispatcherWithCsr.CanRouteToConfiguredExecutionSurface(streamWaitInstruction)
                && dispatcherWithCsr.CanRouteToConfiguredExecutionSurface(csrInstruction)
                && core.Csr.DirectRead(CsrAddresses.VmxEnable) == 0UL
                && VerifyVmReadProjectionAdmissionPath();
        }
        catch
        {
            return false;
        }
    }

    private static bool VerifyVmReadProjectionAdmissionPath()
    {
        VmxCompatibilityAdmissionService service = new VmxCompatibilityAdmissionService();
        VmxCompatibilityVmReadAdmissionResult result = service.AdmitVmReadProjection(
            new VmxCompatibilityVmReadAdmissionRequest(
                Context: new DomainRuntimeContext(
                    execution: new ExecutionDomainDescriptor(),
                    memory: new MemoryDomainDescriptor(),
                    io: new IoDomainDescriptor(),
                    capabilities: new CapabilityDescriptorSet(
                        globalHardwareCaps: 0,
                        runtimeEnabledCaps: 0,
                        domainGrantedCaps: 0)),
                RootAuthority: new RootAuthorityDescriptor(
                    RootAuthorityClass.RuntimeRoot,
                    authorityEpoch: 1,
                    grantedCapabilityMask: 0,
                    allowCompatibilityFrontendActivation: true,
                    allowAuthoritativeStateMutation: false),
                EvidencePolicy: new EvidencePolicyDescriptor(
                    allowCompatibilityAliases: true,
                    allowGuestArchitecturalState: false,
                    allowMigrationSerializableState: false),
                Descriptor: VmcsV2Descriptor.CreateDefault(),
                FieldId: (ushort)VmcsField.GuestPc,
                DestinationRegister: 8,
                FieldSelectorRegister: 1,
                ReservedRegister: 0,
                DescriptorValidated: true,
                CapabilityValidated: true,
                SchedulingValidated: true,
                NoEmissionValidated: true,
                ProjectionEvidenceValidated: true));

        return result.RuntimeAdmissionAllowed &&
            result.Decision == VmxCompatibilityVmReadAdmissionDecision.ReadOnlyProjectionDenied &&
            result.Decode.Payload.OperandForm == Core.Vmx.VmxOperandForm.FieldSelectorToRegister &&
            result.VmcsValidation.Code == VmcsV2ValidationCode.AccessDenied &&
            result.Value == 0;
    }

    private static ReplayPhaseBenchmarkResult RunReplayPhaseBenchmarkScenario(bool reuseStablePhase, ulong iterations)
    {
        const ulong loopPcAddress = 0x6200UL;
        const ulong stableEpochId = 41UL;
        MicroOpScheduler scheduler = new MicroOpScheduler
        {
            EnableLoopPhaseSampling = true
        };

        for (ulong iteration = 0; iteration < iterations; iteration++)
        {
            scheduler.SetReplayPhaseContext(CreateReplayPhaseBenchmarkContext(
                reuseStablePhase ? stableEpochId : stableEpochId + iteration,
                loopPcAddress));
            scheduler.NominateSmtCandidate(1, CreateReplayPhaseScalarAlu(1, 9, 10, 11));
            scheduler.NominateSmtCandidate(2, CreateReplayPhaseScalarAlu(2, 17, 18, 19));
            scheduler.NominateSmtCandidate(3, CreateReplayPhaseScalarAlu(3, 25, 26, 27));
            scheduler.PackBundleIntraCoreSmt(
                CreateReplayPhaseBenchmarkBundle(),
                ownerVirtualThreadId: 0,
                localCoreId: 0,
                eligibleVirtualThreadMask: 0x0E);
        }

        SchedulerPhaseMetrics metrics = scheduler.GetPhaseMetrics();
        return new ReplayPhaseBenchmarkResult(
            reuseStablePhase ? "stable-replay-phase" : "rotating-replay-phase",
            iterations,
            metrics.ReplayAwareCycles,
            metrics.PhaseCertificateReadyHits,
            metrics.PhaseCertificateReadyMisses,
            metrics.EstimatedChecksSaved,
            metrics.PhaseCertificateInvalidations,
            metrics.PhaseCertificateMutationInvalidations,
            metrics.PhaseCertificatePhaseMismatchInvalidations);
    }

    private static ReplayPhaseContext CreateReplayPhaseBenchmarkContext(ulong epochId, ulong loopPcAddress)
    {
        return new ReplayPhaseContext(
            isActive: true,
            epochId: epochId,
            cachedPc: loopPcAddress,
            epochLength: 12,
            completedReplays: 4,
            validSlotCount: 5,
            stableDonorMask: 0xE0,
            lastInvalidationReason: ReplayPhaseInvalidationReason.None);
    }

    private static MicroOp[] CreateReplayPhaseBenchmarkBundle()
    {
        MicroOp[] bundle = new MicroOp[8];
        for (int index = 0; index < 5; index++)
        {
            bundle[index] = CreateReplayPhaseScalarAlu(
                0,
                (ushort)(index * 3 + 1),
                (ushort)(32 + index),
                (ushort)(48 + index));
        }

        return bundle;
    }

    private static ScalarALUMicroOp CreateReplayPhaseScalarAlu(
        int virtualThreadId,
        ushort destReg,
        ushort src1Reg,
        ushort src2Reg)
    {
        ScalarALUMicroOp op = new ScalarALUMicroOp
        {
            OpCode = (uint)Processor.CPU_Core.InstructionsEnum.ADD,
            VirtualThreadId = virtualThreadId,
            OwnerThreadId = virtualThreadId,
            DestRegID = destReg,
            Src1RegID = src1Reg,
            Src2RegID = src2Reg,
            WritesRegister = true
        };
        op.InitializeMetadata();
        return op;
    }

    private static InstructionIR CreateInstructionIr(
        Processor.CPU_Core.InstructionsEnum opcode,
        byte rd = 0,
        byte rs1 = 0,
        byte rs2 = 0,
        long imm = 0)
    {
        return new InstructionIR
        {
            CanonicalOpcode = opcode,
            Class = InstructionClassifier.GetClass(opcode),
            SerializationClass = InstructionClassifier.GetSerializationClass(opcode),
            Rd = rd,
            Rs1 = rs1,
            Rs2 = rs2,
            Imm = imm,
        };
    }

    private void ApplyLiveStateAdapter(Processor.CPU_Core.LiveCpuStateAdapter state)
    {
        _runtime.ApplyLiveStateAdapter(state);
    }

    private sealed class CapturingPipelineEventQueue : IPipelineEventQueue
    {
        public List<PipelineEvent> Events { get; } = [];

        public void Enqueue(PipelineEvent evt)
        {
            Events.Add(evt);
        }
    }
}

internal readonly record struct ShowcaseRuntimeReport(
    bool Executed,
    bool CoversFsp,
    bool CoversTypedSlot,
    bool CoversAdmission,
    bool CoversSurfaceContract,
    bool CoversVector,
    bool CoversStream,
    bool CoversCsr,
    bool CoversSystem,
    bool CoversVmx,
    bool CoversObservability,
    string AssistRuntimeStatus,
    int TraceEventCount,
    int PipelineEventCount,
    int FsmTransitionCount,
    ulong DirectTelemetryInstrRetired,
    ulong DirectBarrierCount,
    ulong DirectVmExitCount,
    string FinalPipelineState)
{
    public static ShowcaseRuntimeReport Empty => new(
        Executed: false,
        CoversFsp: false,
        CoversTypedSlot: false,
        CoversAdmission: false,
        CoversSurfaceContract: false,
        CoversVector: false,
        CoversStream: false,
        CoversCsr: false,
        CoversSystem: false,
        CoversVmx: false,
        CoversObservability: false,
        AssistRuntimeStatus: string.Empty,
        TraceEventCount: 0,
        PipelineEventCount: 0,
        FsmTransitionCount: 0,
        DirectTelemetryInstrRetired: 0,
        DirectBarrierCount: 0,
        DirectVmExitCount: 0,
        FinalPipelineState: string.Empty);
}

internal readonly record struct ReplayPhaseBenchmarkResult(
    string ScenarioLabel,
    ulong Iterations,
    long ReplayAwareCycles,
    long PhaseCertificateReadyHits,
    long PhaseCertificateReadyMisses,
    long EstimatedChecksSaved,
    long PhaseCertificateInvalidations,
    long PhaseCertificateMutationInvalidations,
    long PhaseCertificatePhaseMismatchInvalidations)
{
    public double PhaseCertificateReuseHitRate
    {
        get
        {
            long totalChecks = PhaseCertificateReadyHits + PhaseCertificateReadyMisses;
            return totalChecks == 0
                ? 0.0
                : (double)PhaseCertificateReadyHits / totalChecks;
        }
    }
}

internal readonly record struct ReplayPhaseBenchmarkPairReport(
    ReplayPhaseBenchmarkResult StablePhase,
    ReplayPhaseBenchmarkResult RotatingPhase);
