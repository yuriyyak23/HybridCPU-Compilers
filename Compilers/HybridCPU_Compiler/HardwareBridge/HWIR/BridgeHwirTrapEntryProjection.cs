#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>
/// Source-pinned event projection for one selected TrapEntryEvent at its retire
/// boundary. It describes software call-event behavior; it is not clocked RTL.
/// </summary>
public static class BridgeHwirTrapEntryProjection
{
    private const string ActiveCommit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
    private const string GoldenCommit = "6946ccab9d888f7dae0a4d8e18480f436138acd9";

    private const string ActiveMicroOpSha = "cb1c4ffe3323c47882382aaad97a9aafe79949c37da0033faa18614c1459925c";
    private const string GoldenMicroOpBlob = "357eeaf3c1b8e23727201b7d7356fbe787d371c3";
    private const string GoldenMicroOpMaterializedSha = "bdf2fa64457a5e4c1afe12b46688ec21a8286d8a6cb21cfabf78affb951eabda";
    private const string ActiveEventSha = "5f14d6cbae337d71daccb8e72557deaaf27bf9420e46dd4f1a1505806af0be60";
    private const string GoldenEventBlob = "75b90b36ffa424ff134252fec5297561b6ee0813";
    private const string GoldenEventMaterializedSha = "aab083592af9a3ff89c34998d3146dc6409640c93e7c461470f43d9c7432cd53";
    private const string ActiveHandlerSha = "1c4d085524e123cb3b67cbec1d5af491b6f668222f400dfc812093b8b001fcca";
    private const string GoldenHandlerBlob = "5ae5434fcad260fc2bdb635f04f21cbe126fed98";
    private const string GoldenHandlerMaterializedSha = "369ed2f953917879b433b6252b819ad6e586d7c5e7682c0cc11c07647ce249c3";
    private const string ActiveRetireSha = "cfdb51be0e465e485ce1c314dcc957e88cd537e3bd5c5fc374eb4a46eda8d184";
    private const string GoldenRetireBlob = "3bb2f1687532c136714f3c08b81355d80fd8289a";
    private const string GoldenRetireMaterializedSha = "aa37387ba5664a00f4def1640ce1b31734d39662508ce27b28de1c9ccc1f13a9";
    private const string ActivePipelineSha = "d4090d898beebe889fa826efb07ab4d0519586c8d1c95d7a682f425569b319a1";
    private const string GoldenPipelineBlob = "eb94983349a3904f5b822df548890962f4483f7d";
    private const string GoldenPipelineMaterializedSha = "ec957c193d58db9b983d3851f744d8bc6a24b9ae1b49ff2d2c618faed2a47305";
    private const string ActiveAdapterSha = "8d06ca16d9a64fe0022237bd94e5851e195eb4557e8168b62538b0f48cf32aa6";
    private const string GoldenAdapterBlob = "2f9274a442b6be6b10560c6e3c806bf5add59b31";
    private const string GoldenAdapterMaterializedSha = "30e6a1df02f5e9e2cc2f0b2e99a7f49d142f92aecf0f1886a4df466009fe6498";
    private const string ActivePipelineStateSha = "a58565f075b976bd90c31b2cd30e44ba8111cb40fb204ca7ab49122423899588";
    private const string GoldenPipelineStateBlob = "95ad3b3557b51dd706e7574ab07f9870d594d095";
    private const string GoldenPipelineStateMaterializedSha = "e66bd1d5df5243b48a6cd841e4b1b5cafb13104f355f6e23b5a87a0be988ca8c";

    public static HwValidatedDesign Compile(
        byte[] activeMicroOp, byte[] activeEvent, byte[] activeHandler,
        byte[] activeRetire, byte[] activePipeline, byte[] activeAdapter,
        byte[] goldenMicroOp, byte[] goldenEvent, byte[] goldenHandler,
        byte[] goldenRetire, byte[] goldenPipeline, byte[] goldenAdapter,
        byte[] activePipelineState, byte[] goldenPipelineState,
        HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(toolchain);
        if (Hash(activeMicroOp) != ActiveMicroOpSha || Hash(activeEvent) != ActiveEventSha ||
            Hash(activeHandler) != ActiveHandlerSha || Hash(activeRetire) != ActiveRetireSha ||
            Hash(activePipeline) != ActivePipelineSha || Hash(activeAdapter) != ActiveAdapterSha ||
            Hash(activePipelineState) != ActivePipelineStateSha ||
            Hash(goldenMicroOp) != GoldenMicroOpMaterializedSha || Hash(goldenEvent) != GoldenEventMaterializedSha ||
            Hash(goldenHandler) != GoldenHandlerMaterializedSha || Hash(goldenRetire) != GoldenRetireMaterializedSha ||
            Hash(goldenPipeline) != GoldenPipelineMaterializedSha || Hash(goldenAdapter) != GoldenAdapterMaterializedSha ||
            Hash(goldenPipelineState) != GoldenPipelineStateMaterializedSha ||
            toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("Trap-entry projection inputs differ from active source bytes, the pinned Golden Git blobs, or the bounded combinational profile.");

        static SyntaxNode Parse(byte[] bytes)
        {
            var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Trap-entry source contains C# syntax errors.");
            return tree.GetRoot();
        }

        static MethodDeclarationSyntax Method(SyntaxNode root, string typeName, string methodName) => root
            .DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == methodName && m.Ancestors().OfType<TypeDeclarationSyntax>()
                .Any(t => t.Identifier.ValueText == typeName));

        static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));

        static HwSourceOrigin Origin(SyntaxNode node, string path, string owner, string member, string identity)
        {
            FileLinePositionSpan span = node.GetLocation().GetLineSpan();
            return new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", ActiveCommit,
                path, "CircuitWeave.TrapEntryProjection", owner, member,
                Hash(Encoding.UTF8.GetBytes(identity)), HwSourceRole.Primary,
                span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
        }

        SyntaxNode activeMicroRoot = Parse(activeMicroOp);
        SyntaxNode activeEventRoot = Parse(activeEvent);
        SyntaxNode activeHandlerRoot = Parse(activeHandler);
        SyntaxNode activeRetireRoot = Parse(activeRetire);
        SyntaxNode activePipelineRoot = Parse(activePipeline);
        SyntaxNode activeAdapterRoot = Parse(activeAdapter);
        SyntaxNode activePipelineStateRoot = Parse(activePipelineState);
        SyntaxNode goldenMicroRoot = Parse(goldenMicroOp);
        SyntaxNode goldenEventRoot = Parse(goldenEvent);
        SyntaxNode goldenHandlerRoot = Parse(goldenHandler);
        SyntaxNode goldenRetireRoot = Parse(goldenRetire);
        SyntaxNode goldenPipelineRoot = Parse(goldenPipeline);
        SyntaxNode goldenAdapterRoot = Parse(goldenAdapter);
        SyntaxNode goldenPipelineStateRoot = Parse(goldenPipelineState);

        MethodDeclarationSyntax activeCreateEvent = Method(activeMicroRoot, "TrapMicroOp", "CreatePipelineEvent");
        MethodDeclarationSyntax goldenCreateEvent = Method(goldenMicroRoot, "TrapMicroOp", "CreatePipelineEvent");
        MethodDeclarationSyntax activeExecute = Method(activeMicroRoot, "TrapMicroOp", "Execute");
        MethodDeclarationSyntax goldenExecute = Method(goldenMicroRoot, "TrapMicroOp", "Execute");
        ConstructorDeclarationSyntax activeTrapConstructor = activeMicroRoot.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
            .Single(constructor => constructor.Identifier.ValueText == "TrapMicroOp");
        ConstructorDeclarationSyntax goldenTrapConstructor = goldenMicroRoot.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
            .Single(constructor => constructor.Identifier.ValueText == "TrapMicroOp");
        MethodDeclarationSyntax activeTrapHandlerMethod = Method(activeHandlerRoot, "PipelineFsmEventHandler", "HandleTrapEntry");
        MethodDeclarationSyntax goldenHandlerMethod = Method(goldenHandlerRoot, "PipelineFsmEventHandler", "HandleTrapEntry");
        MethodDeclarationSyntax activeRetireApply = Method(activeRetireRoot, "CPU_Core", "ApplyPipelineEventToVirtualThread");
        MethodDeclarationSyntax activeCapture = Method(activeRetireRoot, "CPU_Core", "CaptureRetiredWriteBackLaneEffects");
        MethodDeclarationSyntax goldenCapture = Method(goldenRetireRoot, "CPU_Core", "CaptureRetiredWriteBackLaneEffects");
        MethodDeclarationSyntax activeOrderResolver = Method(activeRetireRoot, "CPU_Core", "ResolveRetiredSystemEventOrderGuaranteeForTesting");
        MethodDeclarationSyntax goldenOrderResolver = Method(goldenRetireRoot, "CPU_Core", "ResolveRetiredSystemEventOrderGuaranteeForTesting");
        MethodDeclarationSyntax activeRetireBoundary = Method(activeRetireRoot, "CPU_Core", "HandleRetiredPipelineEventBoundary");
        MethodDeclarationSyntax goldenRetireApply = Method(goldenRetireRoot, "CPU_Core", "ApplyPipelineEventToVirtualThread");
        MethodDeclarationSyntax goldenRetireBoundary = Method(goldenRetireRoot, "CPU_Core", "HandleRetiredPipelineEventBoundary");
        MethodDeclarationSyntax activeRequiresFlush = Method(activeRetireRoot, "CPU_Core", "RequiresRetiredSystemEventPipelineFlush");
        MethodDeclarationSyntax goldenRequiresFlush = Method(goldenRetireRoot, "CPU_Core", "RequiresRetiredSystemEventPipelineFlush");
        MethodDeclarationSyntax activeAssistReason = Method(activeRetireRoot, "CPU_Core", "ResolveAssistInvalidationReasonForRetiredPipelineEvent");
        MethodDeclarationSyntax goldenAssistReason = Method(goldenRetireRoot, "CPU_Core", "ResolveAssistInvalidationReasonForRetiredPipelineEvent");
        MethodDeclarationSyntax activeFlush = Method(activePipelineRoot, "CPU_Core", "FlushPipelineUnderSecureGate");
        MethodDeclarationSyntax activeReplayReason = Method(activePipelineRoot, "CPU_Core", "ResolveReplayPhaseInvalidationReasonForPipelineFlush");
        MethodDeclarationSyntax goldenFlush = Method(goldenPipelineRoot, "CPU_Core", "FlushPipeline");
        MethodDeclarationSyntax goldenReplayReason = Method(goldenPipelineRoot, "CPU_Core", "ResolveReplayPhaseInvalidationReasonForPipelineFlush");
        EnumDeclarationSyntax activePipelineStateEnum = activePipelineStateRoot.DescendantNodes().OfType<EnumDeclarationSyntax>()
            .Single(declaration => declaration.Identifier.ValueText == "PipelineState");
        EnumDeclarationSyntax goldenPipelineStateEnum = goldenPipelineStateRoot.DescendantNodes().OfType<EnumDeclarationSyntax>()
            .Single(declaration => declaration.Identifier.ValueText == "PipelineState");

        string activeCreateTokens = Tokens(activeCreateEvent);
        string goldenCreateTokens = Tokens(goldenCreateEvent);
        string activeMicroTypeTokens = Tokens(activeMicroRoot);
        string goldenMicroTypeTokens = Tokens(goldenMicroRoot);
        string activeExecuteTokens = Tokens(activeExecute);
        string goldenExecuteTokens = Tokens(goldenExecute);
        string activeConstructorTokens = Tokens(activeTrapConstructor);
        string goldenConstructorTokens = Tokens(goldenTrapConstructor);
        string activeHandlerTokens = Tokens(activeTrapHandlerMethod);
        string goldenHandlerTokens = Tokens(goldenHandlerMethod);
        string activeApplyTokens = Tokens(activeRetireApply);
        string activeCaptureTokens = Tokens(activeCapture);
        string goldenCaptureTokens = Tokens(goldenCapture);
        string activeOrderResolverTokens = Tokens(activeOrderResolver);
        string goldenOrderResolverTokens = Tokens(goldenOrderResolver);
        string goldenApplyTokens = Tokens(goldenRetireApply);
        string activeBoundaryTokens = Tokens(activeRetireBoundary);
        string goldenBoundaryTokens = Tokens(goldenRetireBoundary);
        string activeRequiresFlushTokens = Tokens(activeRequiresFlush);
        string goldenRequiresFlushTokens = Tokens(goldenRequiresFlush);
        string activeAssistReasonTokens = Tokens(activeAssistReason);
        string goldenAssistReasonTokens = Tokens(goldenAssistReason);
        string activeFlushTokens = Tokens(activeFlush);
        string goldenFlushTokens = Tokens(goldenFlush);
        string activeReplayTokens = Tokens(activeReplayReason);
        string goldenReplayTokens = Tokens(goldenReplayReason);
        string activeAdapterTokens = Tokens(activeAdapterRoot);
        string goldenAdapterTokens = Tokens(goldenAdapterRoot);

        static bool HasAll(string text, params string[] tokens) => tokens.All(text.Contains);
        bool eventChecks = HasAll(activeCreateTokens, "TrapEntryEvent", "CauseCode", "IllegalInstructionCauseCode", "FaultAddress", "HasFaultAddress") &&
            HasAll(goldenCreateTokens, "TrapEntryEvent", "CauseCode", "IllegalInstructionCauseCode", "FaultAddress", "HasFaultAddress") &&
            activeCreateTokens.Contains("FaultAddress=0", StringComparison.Ordinal) &&
            goldenCreateTokens.Contains("FaultAddress=0", StringComparison.Ordinal) &&
            activeMicroTypeTokens.Contains("IllegalInstructionCauseCode=2", StringComparison.Ordinal) &&
            goldenMicroTypeTokens.Contains("IllegalInstructionCauseCode=2", StringComparison.Ordinal) &&
            activeExecuteTokens.Contains("returntrue", StringComparison.Ordinal) &&
            goldenExecuteTokens.Contains("returntrue", StringComparison.Ordinal) &&
            activeConstructorTokens.Contains("SerializationClass=Arch.SerializationClass.FullSerial", StringComparison.Ordinal) &&
            goldenConstructorTokens.Contains("SerializationClass=Arch.SerializationClass.FullSerial", StringComparison.Ordinal);
        bool handlerChecks = HasAll(activeHandlerTokens, "Mepc", "Mcause", "Mtval", "Mtvec", "CauseCode", "FaultAddress", "WritePc", "returncurrent") &&
            HasAll(goldenHandlerTokens, "Mepc", "Mcause", "Mtval", "Mtvec", "CauseCode", "FaultAddress", "WritePc", "returncurrent");
        bool applicationChecks = HasAll(activeApplyTokens, "CreateRetiredPipelineEvent(pipelineEvent,normalizedVtId)",
                "liveState.WritePc((byte)normalizedVtId,retiredPc.Value)", "eventHandler.Handle", "ApplyLiveCpuStateWithStableCoreIdentity") &&
            HasAll(goldenApplyTokens, "CreateRetiredPipelineEvent(pipelineEvent,normalizedVtId)",
                "liveState.WritePc((byte)normalizedVtId,retiredPc.Value)", "eventHandler.Handle", "ApplyLiveCpuStateWithStableCoreIdentity") &&
            activeApplyTokens.IndexOf("liveState.WritePc((byte)normalizedVtId,retiredPc.Value)", StringComparison.Ordinal) <
                activeApplyTokens.IndexOf("eventHandler.Handle", StringComparison.Ordinal) &&
            goldenApplyTokens.IndexOf("liveState.WritePc((byte)normalizedVtId,retiredPc.Value)", StringComparison.Ordinal) <
                goldenApplyTokens.IndexOf("eventHandler.Handle", StringComparison.Ordinal);
        bool orderGuaranteeChecks = HasAll(activeCaptureTokens, "lane.GeneratedEvent", "ResolveRetiredSystemEventOrderGuaranteeForTesting(lane.GeneratedEvent)") &&
            HasAll(goldenCaptureTokens, "lane.GeneratedEvent", "ResolveRetiredSystemEventOrderGuaranteeForTesting(lane.GeneratedEvent)") &&
            HasAll(activeOrderResolverTokens, "TrapEntryEvent", "FullSerialTrapBoundary") &&
            HasAll(goldenOrderResolverTokens, "TrapEntryEvent", "FullSerialTrapBoundary");
        bool boundaryChecks = activeBoundaryTokens.IndexOf("FlushPipeline", StringComparison.Ordinal) < activeBoundaryTokens.IndexOf("ApplyRetiredPipelineEvent", StringComparison.Ordinal) &&
            goldenBoundaryTokens.IndexOf("FlushPipeline", StringComparison.Ordinal) < goldenBoundaryTokens.IndexOf("ApplyRetiredPipelineEvent", StringComparison.Ordinal) &&
            HasAll(activeRequiresFlushTokens, "FullSerialTrapBoundary") &&
            HasAll(goldenRequiresFlushTokens, "FullSerialTrapBoundary") &&
            HasAll(activeAssistReasonTokens, "FullSerialTrapBoundary", "AssistInvalidationReason.Trap") &&
            HasAll(goldenAssistReasonTokens, "FullSerialTrapBoundary", "AssistInvalidationReason.Trap");
        bool replayChecks = HasAll(activeFlushTokens, "InvalidateAssistRuntime", "AdvanceReplayCodeGenerationEpoch", "_loopBuffer.Invalidate", "SetReplayPhaseContext") &&
            HasAll(goldenFlushTokens, "InvalidateAssistRuntime", "AdvanceReplayCodeGenerationEpoch", "_loopBuffer.Invalidate", "SetReplayPhaseContext") &&
            HasAll(activeReplayTokens, "AssistInvalidationReason.Trap", "SerializingEvent") &&
            HasAll(goldenReplayTokens, "AssistInvalidationReason.Trap", "SerializingEvent");
        bool adapterChecks = HasAll(activeAdapterTokens, "WriteEventCsr", "ReserveAccess", "BeginHandler", "ApplyTo", "WritePc") &&
            HasAll(goldenAdapterTokens, "ApplyTo", "WritePc");
        bool pipelineStateChecks = (activePipelineStateEnum.BaseList is null || activePipelineStateEnum.BaseList.Types.Count == 1 &&
            activePipelineStateEnum.BaseList.Types[0].Type.ToString() is "int" or "System.Int32") &&
            (goldenPipelineStateEnum.BaseList is null || goldenPipelineStateEnum.BaseList.Types.Count == 1 &&
            goldenPipelineStateEnum.BaseList.Types[0].Type.ToString() is "int" or "System.Int32");
        if (!eventChecks || !handlerChecks || !applicationChecks || !orderGuaranteeChecks || !boundaryChecks || !replayChecks || !adapterChecks || !pipelineStateChecks)
            throw new InvalidDataException($"Trap-entry active/Golden source path refused: event={eventChecks}, handler={handlerChecks}, application={applicationChecks}, orderGuarantee={orderGuaranteeChecks}, boundary={boundaryChecks}, replay={replayChecks}, adapter={adapterChecks}, pipelineState={pipelineStateChecks}.");

        string microPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/MicroOps/Types/MicroOp.IO.cs";
        string handlerPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/PipelineFsmEventHandler.cs";
        string retirePath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.PipelineExecution.Retire.cs";
        string pipelinePath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Stages/Issue/CPU_Core.Pipeline.cs";
        string microIdentity = string.Join("|", "trap-entry-event/v1", ActiveMicroOpSha, GoldenMicroOpBlob,
            Hash(activeEvent), GoldenEventBlob);
        string handlerIdentity = string.Join("|", "trap-entry-architectural-commit/v1", ActiveHandlerSha, GoldenHandlerBlob,
            ActiveAdapterSha, GoldenAdapterBlob);
        string boundaryIdentity = string.Join("|", "trap-entry-retire-boundary-replay/v1", ActiveRetireSha, GoldenRetireBlob,
            ActivePipelineSha, GoldenPipelineBlob, ActivePipelineStateSha, GoldenPipelineStateBlob);
        HwSourceOrigin eventOrigin = Origin(activeCreateEvent, microPath, "YAKSys_Hybrid_CPU.Core.TrapMicroOp",
            "CreatePipelineEvent", microIdentity);
        HwSourceOrigin handlerOrigin = Origin(activeTrapHandlerMethod, handlerPath, "YAKSys_Hybrid_CPU.Core.Pipeline.PipelineFsmEventHandler",
            "HandleTrapEntry", handlerIdentity);
        HwSourceOrigin boundaryOrigin = Origin(activeRetireBoundary, retirePath, "YAKSys_Hybrid_CPU.Processor.CPU_Core",
            "HandleRetiredPipelineEventBoundary", boundaryIdentity);
        HwSourceOrigin retireApplyOrigin = Origin(activeRetireApply, retirePath, "YAKSys_Hybrid_CPU.Processor.CPU_Core",
            "ApplyPipelineEventToVirtualThread", boundaryIdentity);
        HwSourceOrigin flushOrigin = Origin(activeFlush, pipelinePath, "YAKSys_Hybrid_CPU.Processor.CPU_Core",
            "FlushPipelineUnderSecureGate", boundaryIdentity);

        HwBitVectorType bit = new(1), u64 = new(64), pipelineState = new(32, HwSignedness.Signed);
        HwModule eventConstructor = BuildEventConstructor(bit, u64, eventOrigin);
        HwModule architecturalCommit = BuildArchitecturalCommit(bit, u64, pipelineState, handlerOrigin);
        HwModule retireBoundary = BuildRetireBoundary(bit, boundaryOrigin);
        HwModule replayFlush = BuildReplayFlush(bit, flushOrigin);
        HwModule top = BuildTop(bit, u64, pipelineState, eventConstructor, architecturalCommit,
            retireBoundary, replayFlush, retireApplyOrigin);
        HwDesign design = new("hwir/v0.1", top.Id,
            [eventConstructor, architecturalCommit, retireBoundary, replayFlush, top], toolchain, []);
        HwValidationResult validation = new FailClosedHardwareIrValidator().Validate(design);
        return validation.Validated ?? throw new InvalidDataException("Trap-entry projection HWIR refused: " +
            string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    private static HwModule BuildEventConstructor(HwBitVectorType bit, HwBitVectorType u64, HwSourceOrigin origin)
    {
        const string id = "trap_microop_event_construction";
        HwPortContract[] ports =
        [
            new("trap_microop_present", HwPortDirection.Input, bit),
            new("execute_attempt", HwPortDirection.Input, bit),
            new("event_valid", HwPortDirection.Output, bit),
            new("cause_code", HwPortDirection.Output, u64),
            new("fault_address", HwPortDirection.Output, u64),
            new("has_fault_address", HwPortDirection.Output, bit),
            new("full_serial_boundary", HwPortDirection.Output, bit)
        ];
        HwNode[] nodes =
        [
            new HwConstantNode("illegal_instruction_cause", u64, Convert.ToString(2, 2).PadLeft(64, '0'), origin),
            new HwConstantNode("zero_fault_address", u64, new string('0', 64), origin),
            new HwConstantNode("fault_address_absent", bit, "0", origin),
            new HwOperationNode("trap_event_valid", "and", ["trap_microop_present", "execute_attempt"], bit, Origin: origin),
            new HwOutputDriveNode("drive_event_valid", "event_valid", "trap_event_valid", origin),
            new HwOutputDriveNode("drive_cause_code", "cause_code", "illegal_instruction_cause", origin),
            new HwOutputDriveNode("drive_fault_address", "fault_address", "zero_fault_address", origin),
            new HwOutputDriveNode("drive_has_fault_address", "has_fault_address", "fault_address_absent", origin),
            new HwOutputDriveNode("drive_full_serial_boundary", "full_serial_boundary", "trap_event_valid", origin)
        ];
        return new HwModule(id, "trap_microop_event_construction", ports, nodes, [],
            new HwHardwareContract("processor.trap-microop.event-construction.v1", HwModuleKind.Combinational,
                ports, new HwTimingContract(0, 1)), origin);
    }

    private static HwModule BuildArchitecturalCommit(
        HwBitVectorType bit, HwBitVectorType u64, HwBitVectorType pipelineState, HwSourceOrigin origin)
    {
        const string id = "trap_entry_architectural_commit";
        HwPortContract[] ports =
        [
            new("event_apply", HwPortDirection.Input, bit),
            new("cause_code", HwPortDirection.Input, u64),
            new("fault_address", HwPortDirection.Input, u64),
            new("faulting_pc", HwPortDirection.Input, u64),
            new("mtvec", HwPortDirection.Input, u64),
            new("mepc_current", HwPortDirection.Input, u64),
            new("mcause_current", HwPortDirection.Input, u64),
            new("mtval_current", HwPortDirection.Input, u64),
            new("pc_current", HwPortDirection.Input, u64),
            new("pipeline_state_current", HwPortDirection.Input, pipelineState),
            new("mepc_next", HwPortDirection.Output, u64),
            new("mcause_next", HwPortDirection.Output, u64),
            new("mtval_next", HwPortDirection.Output, u64),
            new("pc_next", HwPortDirection.Output, u64),
            new("pipeline_state_next", HwPortDirection.Output, pipelineState)
        ];
        HwNode[] nodes =
        [
            new HwConstantNode("cause_zero", u64, new string('0', 64), origin),
            new HwConstantNode("cause_illegal_instruction", u64, Convert.ToString(2, 2).PadLeft(64, '0'), origin),
            new HwOperationNode("cause_is_zero", "eq", ["cause_code", "cause_zero"], bit, Origin: origin),
            new HwMuxNode("cause_normalized", "cause_is_zero", "cause_illegal_instruction", "cause_code", u64, origin),
            new HwMuxNode("mepc_selected", "event_apply", "faulting_pc", "mepc_current", u64, origin),
            new HwMuxNode("mcause_selected", "event_apply", "cause_normalized", "mcause_current", u64, origin),
            new HwMuxNode("mtval_selected", "event_apply", "fault_address", "mtval_current", u64, origin),
            new HwMuxNode("pc_selected", "event_apply", "mtvec", "pc_current", u64, origin),
            new HwOutputDriveNode("drive_mepc_next", "mepc_next", "mepc_selected", origin),
            new HwOutputDriveNode("drive_mcause_next", "mcause_next", "mcause_selected", origin),
            new HwOutputDriveNode("drive_mtval_next", "mtval_next", "mtval_selected", origin),
            new HwOutputDriveNode("drive_pc_next", "pc_next", "pc_selected", origin),
            new HwOutputDriveNode("drive_pipeline_state_next", "pipeline_state_next", "pipeline_state_current", origin)
        ];
        return new HwModule(id, "trap_entry_architectural_commit", ports, nodes, [],
            new HwHardwareContract("processor.trap-entry.architectural-call-event.v1", HwModuleKind.Combinational,
                ports, new HwTimingContract(0, 1)), origin);
    }

    private static HwModule BuildRetireBoundary(HwBitVectorType bit, HwSourceOrigin origin)
    {
        const string id = "trap_entry_full_serial_retire_boundary";
        HwPortContract[] ports =
        [
            new("event_apply", HwPortDirection.Input, bit),
            new("full_serial_boundary", HwPortDirection.Input, bit),
            new("pipeline_flush", HwPortDirection.Output, bit),
            new("assist_invalidation_trap", HwPortDirection.Output, bit)
        ];
        HwNode[] nodes =
        [
            new HwOperationNode("selected_full_serial_trap", "and", ["event_apply", "full_serial_boundary"], bit, Origin: origin),
            new HwOutputDriveNode("drive_pipeline_flush", "pipeline_flush", "selected_full_serial_trap", origin),
            new HwOutputDriveNode("drive_assist_invalidation_trap", "assist_invalidation_trap", "selected_full_serial_trap", origin)
        ];
        return new HwModule(id, "trap_entry_retire_boundary", ports, nodes, [],
            new HwHardwareContract("processor.trap-entry.full-serial-retire-boundary.v1", HwModuleKind.Combinational,
                ports, new HwTimingContract(0, 1)), origin);
    }

    private static HwModule BuildReplayFlush(HwBitVectorType bit, HwSourceOrigin origin)
    {
        const string id = "trap_entry_replay_flush_projection";
        HwPortContract[] ports =
        [
            new("pipeline_flush", HwPortDirection.Input, bit),
            new("replay_phase_active_current", HwPortDirection.Input, bit),
            new("replay_phase_invalidate", HwPortDirection.Output, bit),
            new("replay_phase_active_next", HwPortDirection.Output, bit)
        ];
        HwNode[] nodes =
        [
            new HwConstantNode("bit_zero", bit, "0", origin),
            new HwMuxNode("replay_phase_active_selected", "pipeline_flush", "bit_zero", "replay_phase_active_current", bit, origin),
            new HwOutputDriveNode("drive_replay_phase_invalidate", "replay_phase_invalidate", "pipeline_flush", origin),
            new HwOutputDriveNode("drive_replay_phase_active_next", "replay_phase_active_next", "replay_phase_active_selected", origin)
        ];
        return new HwModule(id, "trap_entry_replay_phase_flush", ports, nodes, [],
            new HwHardwareContract("processor.trap-entry.replay-flush-call-event.v1", HwModuleKind.Combinational,
                ports, new HwTimingContract(0, 1)), origin);
    }

    private static HwModule BuildTop(
        HwBitVectorType bit, HwBitVectorType u64, HwBitVectorType pipelineState,
        HwModule eventConstructor, HwModule architecturalCommit, HwModule retireBoundary,
        HwModule replayFlush, HwSourceOrigin origin)
    {
        const string id = "processor_trap_entry_retire_projection";
        HwPortContract[] ports =
        [
            new("trap_microop_present", HwPortDirection.Input, bit),
            new("execute_attempt", HwPortDirection.Input, bit),
            new("retire_event_apply", HwPortDirection.Input, bit),
            new("faulting_pc", HwPortDirection.Input, u64),
            new("mtvec", HwPortDirection.Input, u64),
            new("mepc_current", HwPortDirection.Input, u64),
            new("mcause_current", HwPortDirection.Input, u64),
            new("mtval_current", HwPortDirection.Input, u64),
            new("pc_current", HwPortDirection.Input, u64),
            new("pipeline_state_current", HwPortDirection.Input, pipelineState),
            new("replay_phase_active_current", HwPortDirection.Input, bit),
            new("event_valid", HwPortDirection.Output, bit),
            new("event_cause_code", HwPortDirection.Output, u64),
            new("event_fault_address", HwPortDirection.Output, u64),
            new("event_has_fault_address", HwPortDirection.Output, bit),
            new("full_serial_boundary", HwPortDirection.Output, bit),
            new("event_apply", HwPortDirection.Output, bit),
            new("mepc_next", HwPortDirection.Output, u64),
            new("mcause_next", HwPortDirection.Output, u64),
            new("mtval_next", HwPortDirection.Output, u64),
            new("pc_next", HwPortDirection.Output, u64),
            new("pipeline_state_next", HwPortDirection.Output, pipelineState),
            new("pipeline_flush", HwPortDirection.Output, bit),
            new("assist_invalidation_trap", HwPortDirection.Output, bit),
            new("replay_phase_invalidate", HwPortDirection.Output, bit),
            new("replay_phase_active_next", HwPortDirection.Output, bit)
        ];
        string[] wireNames =
        [
            "event_valid_net", "event_cause_net", "event_fault_address_net", "event_has_fault_net", "full_serial_boundary_net",
            "mepc_next_net", "mcause_next_net", "mtval_next_net", "pc_next_net", "pipeline_state_next_net",
            "pipeline_flush_net", "assist_invalidation_net", "replay_invalidate_net", "replay_active_next_net"
        ];
        HwNode[] nodes =
        [
            .. wireNames.Select(name => new HwWireNode(name,
                name is "event_cause_net" or "event_fault_address_net" or "mepc_next_net" or "mcause_next_net" or "mtval_next_net" or "pc_next_net"
                    ? u64 : name == "pipeline_state_next_net" ? pipelineState : bit, origin)),
            new HwOperationNode("selected_event_apply", "and", ["retire_event_apply", "event_valid_net"], bit, Origin: origin),
            new HwOutputDriveNode("drive_event_valid", "event_valid", "event_valid_net", origin),
            new HwOutputDriveNode("drive_event_cause", "event_cause_code", "event_cause_net", origin),
            new HwOutputDriveNode("drive_event_fault_address", "event_fault_address", "event_fault_address_net", origin),
            new HwOutputDriveNode("drive_event_has_fault_address", "event_has_fault_address", "event_has_fault_net", origin),
            new HwOutputDriveNode("drive_full_serial_boundary", "full_serial_boundary", "full_serial_boundary_net", origin),
            new HwOutputDriveNode("drive_event_apply", "event_apply", "selected_event_apply", origin),
            new HwOutputDriveNode("drive_mepc_next", "mepc_next", "mepc_next_net", origin),
            new HwOutputDriveNode("drive_mcause_next", "mcause_next", "mcause_next_net", origin),
            new HwOutputDriveNode("drive_mtval_next", "mtval_next", "mtval_next_net", origin),
            new HwOutputDriveNode("drive_pc_next", "pc_next", "pc_next_net", origin),
            new HwOutputDriveNode("drive_pipeline_state_next", "pipeline_state_next", "pipeline_state_next_net", origin),
            new HwOutputDriveNode("drive_pipeline_flush", "pipeline_flush", "pipeline_flush_net", origin),
            new HwOutputDriveNode("drive_assist_invalidation", "assist_invalidation_trap", "assist_invalidation_net", origin),
            new HwOutputDriveNode("drive_replay_invalidate", "replay_phase_invalidate", "replay_invalidate_net", origin),
            new HwOutputDriveNode("drive_replay_active_next", "replay_phase_active_next", "replay_active_next_net", origin)
        ];
        IReadOnlyDictionary<string, string> Map(params (string Port, string Signal)[] pairs) =>
            pairs.ToDictionary(pair => pair.Port, pair => pair.Signal, StringComparer.Ordinal);
        HwInstanceNode[] instances =
        [
            new("trap_microop_event", eventConstructor.Id, Map(
                ("trap_microop_present", "trap_microop_present"), ("execute_attempt", "execute_attempt"),
                ("event_valid", "event_valid_net"), ("cause_code", "event_cause_net"),
                ("fault_address", "event_fault_address_net"), ("has_fault_address", "event_has_fault_net"),
                ("full_serial_boundary", "full_serial_boundary_net")), new Dictionary<string, string>(), Origin: origin),
            new("trap_architectural_commit", architecturalCommit.Id, Map(
                ("event_apply", "selected_event_apply"), ("cause_code", "event_cause_net"),
                ("fault_address", "event_fault_address_net"), ("faulting_pc", "faulting_pc"), ("mtvec", "mtvec"),
                ("mepc_current", "mepc_current"), ("mcause_current", "mcause_current"),
                ("mtval_current", "mtval_current"), ("pc_current", "pc_current"),
                ("pipeline_state_current", "pipeline_state_current"), ("mepc_next", "mepc_next_net"),
                ("mcause_next", "mcause_next_net"), ("mtval_next", "mtval_next_net"), ("pc_next", "pc_next_net"),
                ("pipeline_state_next", "pipeline_state_next_net")), new Dictionary<string, string>(), Origin: origin),
            new("trap_retire_boundary", retireBoundary.Id, Map(
                ("event_apply", "selected_event_apply"), ("full_serial_boundary", "full_serial_boundary_net"),
                ("pipeline_flush", "pipeline_flush_net"), ("assist_invalidation_trap", "assist_invalidation_net")),
                new Dictionary<string, string>(), Origin: origin),
            new("trap_replay_flush", replayFlush.Id, Map(
                ("pipeline_flush", "pipeline_flush_net"), ("replay_phase_active_current", "replay_phase_active_current"),
                ("replay_phase_invalidate", "replay_invalidate_net"), ("replay_phase_active_next", "replay_active_next_net")),
                new Dictionary<string, string>(), Origin: origin)
        ];
        return new HwModule(id, "processor_trap_entry_retire_projection", ports, nodes, instances,
            new HwHardwareContract("processor.trap-entry.retire-event-projection.v1", HwModuleKind.Combinational,
                ports, new HwTimingContract(0, 1)), origin);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

}
#endif
