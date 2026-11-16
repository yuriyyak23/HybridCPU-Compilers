#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Pass-local Assist quota reservation. Inputs are sampled facts; this is not clocked storage.</summary>
public static class BridgeHwirAssistQuota
{
    public const string RuntimeSha256 = "d6f5d75253714ab283015099f895c3f4c603c0f60070f53641ed040929438138";
    public const string AssistMicroOpSha256 = "6054413e07978e15e39d9634b798721140fb1f7d98ab2bdd25ae50e8bb011d3d";
    public static HwValidatedDesign Compile(byte[] runtimeSource, byte[] assistMicroOpSource, byte[] schedulerAssistSource, byte[] admissionSource, byte[] reasonEnumSource, byte[] schedulerCoreSource, byte[] backpressureSource, byte[] streamRegisterAssistSource, byte[] smtSchedulerSource, byte[] safetyTypesSource, byte[] certificateSource, string commit, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(runtimeSource);
        ArgumentNullException.ThrowIfNull(assistMicroOpSource);
        ArgumentNullException.ThrowIfNull(schedulerAssistSource);
        ArgumentNullException.ThrowIfNull(admissionSource);
        ArgumentNullException.ThrowIfNull(reasonEnumSource);
        ArgumentNullException.ThrowIfNull(schedulerCoreSource);
        ArgumentNullException.ThrowIfNull(backpressureSource);
        ArgumentNullException.ThrowIfNull(streamRegisterAssistSource);
        ArgumentNullException.ThrowIfNull(smtSchedulerSource);
        ArgumentNullException.ThrowIfNull(safetyTypesSource);
        ArgumentNullException.ThrowIfNull(certificateSource);
        const string schedulerAssistSha256 = "8515039c3d770382fd448439d2453c01bd42397f3b525b413d411161bf8035a8";
        const string admissionSha256 = "6cdd37f43a0a58c0b887f9e8bffe9d2d17abb5d404f7ace70fead05db4edbe25";
        const string reasonEnumSha256 = "489edf25fe528393aba2e298c259e7929f59260430b4946ccea4c713065d9ff1";
        const string schedulerCoreSha256 = "c7e675d76bb4eed9ecccb0b20c13c973895cce556e93c21457d966add731d8bf";
        const string backpressureSha256 = "1d6230b660f42be5f1f6bddc82f9a4e21cf8d283a53506b3d5734df346f137ad";
        const string streamRegisterAssistSha256 = "4398132c8f4a3ea315ac6c591fa6ae4ae8332ae99bb1d0fa5f59ab881d8d9e03";
        const string smtSchedulerSha256 = "4ecfa34a01d71033b9d6c0b3f2ce752cb50ff8d6004b3a2ba3d6f1c5710e340d";
        const string safetyTypesSha256 = "593ff47affbac1c9616b7398075a3089738ad958364d9aa9cb6cc860ae092bd5";
        const string certificateSha256 = "e539d874d9be20605539cda9bf31ed0aed4199b3fced5b35c6531408483ba781";
        if (commit != "1bfe41a49cf11c80fa6c6aee87accb54b78b331f" || Hash(runtimeSource) != RuntimeSha256 || Hash(assistMicroOpSource) != AssistMicroOpSha256 || Hash(schedulerAssistSource) != schedulerAssistSha256 || Hash(admissionSource) != admissionSha256 || Hash(reasonEnumSource) != reasonEnumSha256 || Hash(schedulerCoreSource) != schedulerCoreSha256 || Hash(backpressureSource) != backpressureSha256 || Hash(streamRegisterAssistSource) != streamRegisterAssistSha256 || Hash(smtSchedulerSource) != smtSchedulerSha256 || Hash(safetyTypesSource) != safetyTypesSha256 || Hash(certificateSource) != certificateSha256)
            throw new InvalidDataException("Assist runtime bytes or pinned model identity changed.");
        var root = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(runtimeSource)).GetRoot();
        var quotaState = root.DescendantNodes().OfType<StructDeclarationSyntax>().Single(x => x.Identifier.ValueText == "AssistMemoryQuotaState");
        var reserve = quotaState.Members.OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "TryReserve");
        string body = string.Concat(reserve.Body!.DescendantTokens().Select(t => t.Text));
        if (!body.Contains("_remainingIssueCredits==0", StringComparison.Ordinal) ||
            !body.Contains("_remainingLineCredits<reservedLineDemand", StringComparison.Ordinal) ||
            body.IndexOf("_remainingIssueCredits==0", StringComparison.Ordinal) > body.IndexOf("_remainingLineCredits<reservedLineDemand", StringComparison.Ordinal) ||
            !body.Contains("_remainingIssueCredits--", StringComparison.Ordinal) ||
            !body.Contains("_remainingLineCredits-=(ushort)reservedLineDemand", StringComparison.Ordinal))
            throw new InvalidDataException("Assist quota check or mutation order changed.");
        var quotaType = root.DescendantNodes().OfType<StructDeclarationSyntax>().Single(x => x.Identifier.ValueText == "AssistMemoryQuota");
        var quotaCtor = quotaType.Members.OfType<ConstructorDeclarationSyntax>().Single();
        string quotaCtorBody = string.Concat(quotaCtor.Body!.DescendantTokens().Select(t => t.Text));
        var capResolver = quotaType.Members.OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "ResolveLineCap");
        string capBody = string.Concat(capResolver.Body!.DescendantTokens().Select(t => t.Text));
        if (!quotaCtorBody.Contains("HotLineCap=hotLineCap==0?(byte)1:hotLineCap", StringComparison.Ordinal) ||
            !quotaCtorBody.Contains("ColdLineCap=coldLineCap==0?HotLineCap:coldLineCap", StringComparison.Ordinal) ||
            !capBody.Contains("localityHint==LocalityHint.Cold?ColdLineCap:HotLineCap", StringComparison.Ordinal))
            throw new InvalidDataException("Assist quota cap normalization or locality selection changed.");
        var srfPolicy = root.DescendantNodes().OfType<StructDeclarationSyntax>().Single(x => x.Identifier.ValueText == "AssistStreamRegisterPartitionPolicy");
        var srfPolicyCtor = srfPolicy.Members.OfType<ConstructorDeclarationSyntax>().Single();
        string srfPolicyBody = string.Concat(srfPolicyCtor.Body!.DescendantTokens().Select(t => t.Text));
        if (!srfPolicyBody.Contains("ResidentRegisterBudget=residentRegisterBudget==0?(byte)1:residentRegisterBudget", StringComparison.Ordinal) ||
            !srfPolicyBody.Contains("LoadingRegisterBudget=loadingRegisterBudget==0?(byte)1:loadingRegisterBudget", StringComparison.Ordinal))
            throw new InvalidDataException("Assist SRF policy zero-budget normalization changed.");
        var smtRoot = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(smtSchedulerSource)).GetRoot();
        var rejectRecorder = smtRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "RecordTypedSlotReject" && x.ParameterList.Parameters.Count == 1);
        string rejectBody = string.Concat(rejectRecorder.Body!.DescendantTokens().Select(t => t.Text));
        if (!rejectBody.Contains("caseTypedSlotRejectReason.AssistQuotaReject:TypedSlotAssistQuotaRejects++;SmtRejectionsCount++;", StringComparison.Ordinal) ||
            !rejectBody.Contains("caseTypedSlotRejectReason.AssistBackpressureReject:TypedSlotAssistBackpressureRejects++;SmtRejectionsCount++;", StringComparison.Ordinal))
            throw new InvalidDataException("Assist quota/backpressure typed reject counters changed.");
        var schedulerRoot = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(schedulerAssistSource)).GetRoot();
        var assistRejectRecorder = schedulerRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "RecordAssistReject");
        string assistRejectBody = string.Concat(assistRejectRecorder.Body!.DescendantTokens().Select(t => t.Text));
        if (!assistRejectBody.Contains("AssistRejects++", StringComparison.Ordinal) || !assistRejectBody.Contains("RecordTypedSlotReject(rejectReason,candidate)", StringComparison.Ordinal))
            throw new InvalidDataException("Assist aggregate reject counter routing changed.");
        var injectMethod = schedulerRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "TryInjectAssistCandidates");
        string injectBody = string.Concat(injectMethod.Body!.DescendantTokens().Select(t => t.Text));
        var admissionRoot = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(admissionSource)).GetRoot();
        if (!admissionRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(x => x.Identifier.ValueText == "TryClassAdmission") ||
            !admissionRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(x => x.Identifier.ValueText == "TryMaterializeLane"))
            throw new InvalidDataException("Assist Stage A/B admission methods changed.");
        var enumRoot = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(reasonEnumSource)).GetRoot();
        var rejectEnum = enumRoot.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(x => x.Identifier.ValueText == "TypedSlotRejectReason");
        string enumBody = string.Concat(rejectEnum.DescendantTokens().Select(t => t.Text));
        foreach (string pin in new[] { "StaticClassOvercommit=1", "DynamicClassExhaustion=2", "ResourceConflict=3", "DomainReject=4", "PinnedLaneConflict=11", "LateBindingConflict=12", "InvalidPinnedLane=13" })
            if (!enumBody.Contains(pin, StringComparison.Ordinal)) throw new InvalidDataException("Assist typed rejection enum changed: " + pin);
        var safetyRoot = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(safetyTypesSource)).GetRoot();
        var rejectKindEnum = safetyRoot.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(x => x.Identifier.ValueText == "RejectKind");
        string[] rejectKindNames = rejectKindEnum.Members.Select(x => x.Identifier.ValueText).ToArray();
        if (!rejectKindNames.Take(4).SequenceEqual(["None", "Boundary", "OwnerMismatch", "DomainMismatch"], StringComparer.Ordinal))
            throw new InvalidDataException("Assist legality reject-kind enum order changed.");
        var slotClassEnum = enumRoot.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(x => x.Identifier.ValueText == "SlotClass");
        string[] slotClassNames = slotClassEnum.Members.Select(x => x.Identifier.ValueText).ToArray();
        string slotClassBody = string.Concat(slotClassEnum.DescendantTokens().Select(t => t.Text));
        if (!slotClassNames.Take(6).SequenceEqual(["AluClass", "LsuClass", "DmaStreamClass", "BranchControl", "SystemSingleton", "MatrixTileStreamClass"], StringComparer.Ordinal) ||
            !new[] { "AluClass=0", "LsuClass=1", "DmaStreamClass=2", "BranchControl=3", "SystemSingleton=4", "MatrixTileStreamClass=5" }.All(slotClassBody.Contains))
            throw new InvalidDataException("Assist ResourceConflict slot-class enum changed.");
        var certificateRoot = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(certificateSource)).GetRoot();
        var certificateDetailEnum = certificateRoot.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(x => x.Identifier.ValueText == "CertificateRejectDetail");
        string certificateDetailBody = string.Concat(certificateDetailEnum.DescendantTokens().Select(t => t.Text));
        foreach (string pin in new[] { "SharedResourceConflict=1", "RegisterGroupConflict=2" })
            if (!certificateDetailBody.Contains(pin, StringComparison.Ordinal)) throw new InvalidDataException("Assist certificate detail enum changed: " + pin);
        var schedulerCoreRoot = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(schedulerCoreSource)).GetRoot();
        var schedulerCounterProperties = schedulerCoreRoot.DescendantNodes().OfType<PropertyDeclarationSyntax>().Select(x => x.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        foreach (string counter in new[] { "ClassCapacityRejects", "StaticClassOvercommitRejects", "DynamicClassExhaustionRejects", "NopDueToNoClassCapacity", "TypedSlotResourceConflictRejects", "NopDueToResourceConflict", "TypedSlotDomainRejects", "LaneConflictRejects", "PinnedLaneConflicts", "LateBindingConflicts", "NopDueToPinnedConstraint", "InvalidPinnedLaneRejects", "RejectionsVT0", "RejectionsVT1", "RejectionsVT2", "RejectionsVT3", "SmtOwnerContextGuardRejects", "SmtDomainGuardRejects", "SmtBoundaryGuardRejects", "SmtSharedResourceCertificateRejects", "SmtRegisterGroupCertificateRejects", "SmtLegalityRejectByAluClass", "SmtLegalityRejectByLsuClass", "SmtLegalityRejectByDmaStreamClass", "SmtLegalityRejectByMatrixTileStreamClass", "SmtLegalityRejectByBranchControl", "SmtLegalityRejectBySystemSingleton", "CertificateRejectByAluClass", "CertificateRejectByLsuClass", "CertificateRejectByDmaStreamClass", "CertificateRejectByMatrixTileStreamClass", "CertificateRejectByBranchControl", "CertificateRejectBySystemSingleton", "RegGroupConflictsVT0", "RegGroupConflictsVT1", "RegGroupConflictsVT2", "RegGroupConflictsVT3" })
            if (!schedulerCounterProperties.Contains(counter)) throw new InvalidDataException("Assist typed rejection counter property changed: " + counter);
        string stageACall = "TryClassAdmission(";
        string stageAReject = "RecordAssistReject(candidate,rejectA)";
        string stageBCall = "TryMaterializeLane(";
        string stageBReject = "RecordAssistReject(candidate,rejectB)";
        string bpCall = "TryReserveAssistBackpressure(typedAssistBackpressureCandidate,refassistBackpressureState,memSub)";
        string quotaCall = "TryReserveAssistQuota(typedAssistCandidate,refassistQuotaState)";
        string[] assistOrder = [stageACall, stageAReject, stageBCall, stageBReject, bpCall, quotaCall];
        int priorAssistAt = -1;
        foreach (string token in assistOrder) { int at = injectBody.IndexOf(token, StringComparison.Ordinal); if (at <= priorAssistAt) throw new InvalidDataException("Assist Stage A/B and reservation call order changed: " + token); priorAssistAt = at; }
        var bpState = root.DescendantNodes().OfType<StructDeclarationSyntax>().Single(x => x.Identifier.ValueText == "AssistBackpressureState");
        var bpMethod = bpState.Members.OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "TryReserve");
        string bpBody = string.Concat(bpMethod.Body!.DescendantTokens().Select(t => t.Text));
        string[] bpOrder = ["HasProjectedOutstandingCapacity(targetVirtualThreadId)", "HasSharedOuterCap(assistMicroOp.MemoryBankId)||!HasCarrierCredits(assistMicroOp.CarrierKind)", "assistMicroOp.CarrierKind==AssistCarrierKind.Lane6Dma&&!dmaSrfAvailable", "ConsumeSharedOuterCap(assistMicroOp.MemoryBankId)", "ConsumeCarrierCredits(assistMicroOp.CarrierKind)", "IncrementProjectedOutstandingCount(targetVirtualThreadId)"];
        int last = -1;
        foreach (string check in bpOrder) { int at = bpBody.IndexOf(check, StringComparison.Ordinal); if (at <= last) throw new InvalidDataException("Assist backpressure gate or mutation order changed: " + check); last = at; }
        var bpSchedulerRoot = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(backpressureSource)).GetRoot();
        var wrapper = bpSchedulerRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "TryReserveAssistBackpressure");
        string wrapperBody = string.Concat(wrapper.Body!.DescendantTokens().Select(t => t.Text));
        if (!wrapperBody.Contains("CanAllocateAssistDmaSrf", StringComparison.Ordinal) || !wrapperBody.Contains("assistBackpressureState.TryReserve", StringComparison.Ordinal))
            throw new InvalidDataException("Assist DMA SRF availability must be sampled before backpressure reservation.");
        var captureMethod = bpSchedulerRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "CaptureAssistBackpressureSnapshot");
        string captureBody = string.Concat(captureMethod.Body!.DescendantTokens().Select(t => t.Text));
        if (!captureBody.Contains("Math.Min(_remainingHardwareMemoryIssueBudget,_remainingHardwareLoadIssueBudget)", StringComparison.Ordinal) ||
            !captureBody.Contains("_consumedHardwareLoadBudgetByBank", StringComparison.Ordinal) ||
            !captureBody.Contains("ReadBankBudgetAtLeastOneMask", StringComparison.Ordinal) ||
            !captureBody.Contains("ReadBankBudgetAtLeastTwoMask", StringComparison.Ordinal))
            throw new InvalidDataException("Assist backpressure snapshot source fields changed.");
        var bpCtor = bpState.Members.OfType<ConstructorDeclarationSyntax>().Single(x => x.Identifier.ValueText == "AssistBackpressureState");
        string bpCtorBody = string.Concat(bpCtor.Body!.DescendantTokens().Select(t => t.Text));
        if (!bpCtorBody.Contains("Math.Min(policy.SharedOuterCapCredits,snapshot.SharedOuterCapCredits)", StringComparison.Ordinal) ||
            !bpCtorBody.Contains("_remainingLsuCarrierCredits=policy.LsuCarrierCredits", StringComparison.Ordinal) ||
            !bpCtorBody.Contains("_remainingDmaCarrierCredits=policy.DmaCarrierCredits", StringComparison.Ordinal))
            throw new InvalidDataException("Assist backpressure state initialization changed.");
        var bpPolicy = root.DescendantNodes().OfType<StructDeclarationSyntax>().Single(x => x.Identifier.ValueText == "AssistBackpressurePolicy");
        var bpPolicyCtor = bpPolicy.Members.OfType<ConstructorDeclarationSyntax>().Single();
        string bpPolicyBody = string.Concat(bpPolicyCtor.Body!.DescendantTokens().Select(t => t.Text));
        if (!bpPolicyBody.Contains("SharedOuterCapCredits=sharedOuterCapCredits==0?(byte)1:sharedOuterCapCredits", StringComparison.Ordinal) ||
            !bpPolicyBody.Contains("LsuCarrierCredits=lsuCarrierCredits==0?(byte)1:lsuCarrierCredits", StringComparison.Ordinal) ||
            !bpPolicyBody.Contains("DmaCarrierCredits=dmaCarrierCredits==0?(byte)1:dmaCarrierCredits", StringComparison.Ordinal))
            throw new InvalidDataException("Assist backpressure policy zero-credit normalization changed.");
        var srfRoot = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(streamRegisterAssistSource)).GetRoot();
        var srfType = srfRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(x => x.Identifier.ValueText == "StreamRegisterFile");
        var canAllocate = srfType.Members.OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "CanAllocateAssistRegister");
        var selectSlot = srfType.Members.OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "TrySelectAssistRegisterSlot");
        string canAllocateBody = string.Concat(canAllocate.Body!.DescendantTokens().Select(t => t.Text));
        string selectSlotBody = string.Concat(selectSlot.Body!.DescendantTokens().Select(t => t.Text));
        if (!canAllocateBody.Contains("TryFindAssistReuseRegister(sourceAddr,elementSize,elementCount:1,out_)", StringComparison.Ordinal) ||
            canAllocateBody.IndexOf("TryFindAssistReuseRegister", StringComparison.Ordinal) > canAllocateBody.IndexOf("TrySelectAssistRegisterSlot", StringComparison.Ordinal) ||
            !selectSlotBody.Contains("CountAssistOwnedRegisters(RegisterState.Loading)>=policy.LoadingRegisterBudget", StringComparison.Ordinal) ||
            !selectSlotBody.Contains("FindEmptyRegisterSlot()", StringComparison.Ordinal) || !selectSlotBody.Contains("FindAssistVictimRegister()", StringComparison.Ordinal))
            throw new InvalidDataException("Assist SRF reuse, loading budget, empty-slot, or victim order changed.");
        var assistRoot = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(assistMicroOpSource)).GetRoot();
        var assistType = assistRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(x => x.Identifier.ValueText == "AssistMicroOp");
        var lineSize = assistType.Members.OfType<FieldDeclarationSyntax>().Single(x => x.Declaration.Variables.Any(v => v.Identifier.ValueText == "CacheLineSize"));
        var demandMethod = assistType.Members.OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "EstimatePrefetchLineDemand");
        string demandBody = string.Concat(demandMethod.Body!.DescendantTokens().Select(t => t.Text));
        var ctor = assistType.Members.OfType<ConstructorDeclarationSyntax>().Single();
        string ctorBody = string.Concat(ctor.Body!.DescendantTokens().Select(t => t.Text));
        if (lineSize.Declaration.Variables.Single().Initializer?.Value.ToString() != "32" ||
            !ctorBody.Contains("PrefetchLength=prefetchLength==0?CacheLineSize:prefetchLength", StringComparison.Ordinal) ||
            !demandBody.Contains("Math.Max(1UL,(PrefetchLength+CacheLineSize-1UL)/CacheLineSize)", StringComparison.Ordinal) ||
            !demandBody.Contains("Math.Min(requestedLines,quota.ResolveLineCap(LocalityHint))", StringComparison.Ordinal))
            throw new InvalidDataException("Assist prefetch demand or locality cap changed.");

        FileLinePositionSpan sourceLocation = injectMethod.Identifier.GetLocation().GetLineSpan();
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", commit,
            "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Assist/Runtime/MicroOpScheduler.Assist.cs", "CircuitWeave.AssistReservationAndRejectProjection",
            "YAKSys_Hybrid_CPU.Core.MicroOpScheduler", "TryInjectAssistCandidates", Hash(schedulerAssistSource), HwSourceRole.Primary,
            sourceLocation.StartLinePosition.Line + 1, sourceLocation.StartLinePosition.Character + 1);
        HwSourceOrigin stageAOrigin = origin, stageBOrigin = origin, rejectCounterOrigin = origin, perVtOrigin = origin, reasonEnumOrigin = origin, assistRejectOrigin = origin, bpOrigin = origin;
        var bit = new HwBitVectorType(1); var u8 = new HwBitVectorType(8); var u16 = new HwBitVectorType(16); var u32 = new HwBitVectorType(32); var u64 = new HwBitVectorType(64);
        var ports = new List<HwPortContract> {
            new("remaining_issue_credits",HwPortDirection.Input,u8), new("remaining_line_credits",HwPortDirection.Input,u16),
            new("prefetch_length",HwPortDirection.Input,u64), new("locality_cold",HwPortDirection.Input,bit),
            new("hot_line_cap",HwPortDirection.Input,u8), new("cold_line_cap",HwPortDirection.Input,u8), new("accept",HwPortDirection.Output,bit),
            new("bp_state_valid",HwPortDirection.Input,bit), new("bp_shared_current",HwPortDirection.Input,u8),
            new("lsu_carrier_current",HwPortDirection.Input,u8), new("dma_carrier_current",HwPortDirection.Input,u8),
            new("remaining_memory_issue_budget",HwPortDirection.Input,u8), new("remaining_load_issue_budget",HwPortDirection.Input,u8),
            new("policy_shared_outer_cap_credits",HwPortDirection.Input,u8), new("policy_lsu_carrier_credits",HwPortDirection.Input,u8),
            new("policy_dma_carrier_credits",HwPortDirection.Input,u8), new("bank_id",HwPortDirection.Input,u8),
            new("consumed_shared_read_packed",HwPortDirection.Input,u32), new("read_budget_at_least_one",HwPortDirection.Input,u16),
            new("read_budget_at_least_two",HwPortDirection.Input,u16), new("is_dma_carrier",HwPortDirection.Input,bit),
            new("srf_file_present",HwPortDirection.Input,bit), new("srf_reuse_match",HwPortDirection.Input,bit),
            new("srf_loading_count",HwPortDirection.Input,u8), new("srf_resident_count",HwPortDirection.Input,u8),
            new("srf_has_empty_slot",HwPortDirection.Input,bit), new("srf_has_assist_victim",HwPortDirection.Input,bit),
            new("srf_resident_budget",HwPortDirection.Input,u8), new("srf_loading_budget",HwPortDirection.Input,u8),
            new("stage_a_reject_reason",HwPortDirection.Input,u8), new("stage_b_reject_reason",HwPortDirection.Input,u8), new("candidate_vt",HwPortDirection.Input,u8),
            new("target_vt",HwPortDirection.Input,u8), new("outstanding_count_vt0",HwPortDirection.Input,u8), new("outstanding_count_vt1",HwPortDirection.Input,u8),
            new("outstanding_count_vt2",HwPortDirection.Input,u8), new("outstanding_count_vt3",HwPortDirection.Input,u8),
            new("outstanding_capacity_vt0",HwPortDirection.Input,u8), new("outstanding_capacity_vt1",HwPortDirection.Input,u8),
            new("outstanding_capacity_vt2",HwPortDirection.Input,u8), new("outstanding_capacity_vt3",HwPortDirection.Input,u8),
            new("backpressure_accepted",HwPortDirection.Output,bit), new("reject_outstanding",HwPortDirection.Output,bit),
            new("reject_shared_outer",HwPortDirection.Output,bit), new("reject_dma_srf",HwPortDirection.Output,bit),
            new("next_bp_shared_credits",HwPortDirection.Output,u8), new("next_lsu_carrier_credits",HwPortDirection.Output,u8),
            new("next_dma_carrier_credits",HwPortDirection.Output,u8), new("next_consumed_shared_read_packed",HwPortDirection.Output,u32),
            new("next_outstanding_count_vt0",HwPortDirection.Output,u8), new("next_outstanding_count_vt1",HwPortDirection.Output,u8),
            new("next_outstanding_count_vt2",HwPortDirection.Output,u8), new("next_outstanding_count_vt3",HwPortDirection.Output,u8),
            new("reject_issue",HwPortDirection.Output,bit), new("reject_lines",HwPortDirection.Output,bit),
            new("next_issue_credits",HwPortDirection.Output,u8), new("next_line_credits",HwPortDirection.Output,u16),
            new("reserved_line_demand",HwPortDirection.Output,u16),
            new("smt_rejections_count",HwPortDirection.Input,u64), new("assist_rejects",HwPortDirection.Input,u64),
            new("assist_backpressure_rejects",HwPortDirection.Input,u64), new("assist_backpressure_outer_rejects",HwPortDirection.Input,u64),
            new("assist_backpressure_mshr_rejects",HwPortDirection.Input,u64), new("assist_backpressure_dma_srf_rejects",HwPortDirection.Input,u64),
            new("assist_quota_rejects",HwPortDirection.Input,u64), new("assist_quota_issue_rejects",HwPortDirection.Input,u64),
            new("assist_quota_line_rejects",HwPortDirection.Input,u64), new("next_smt_rejections_count",HwPortDirection.Output,u64),
            new("next_assist_rejects",HwPortDirection.Output,u64), new("next_assist_backpressure_rejects",HwPortDirection.Output,u64),
            new("next_assist_backpressure_outer_rejects",HwPortDirection.Output,u64), new("next_assist_backpressure_mshr_rejects",HwPortDirection.Output,u64),
            new("next_assist_backpressure_dma_srf_rejects",HwPortDirection.Output,u64), new("next_assist_quota_rejects",HwPortDirection.Output,u64),
            new("next_assist_quota_issue_rejects",HwPortDirection.Output,u64), new("next_assist_quota_line_rejects",HwPortDirection.Output,u64),
            new("class_capacity_rejects",HwPortDirection.Input,u64), new("next_class_capacity_rejects",HwPortDirection.Output,u64),
            new("static_class_overcommit_rejects",HwPortDirection.Input,u64), new("next_static_class_overcommit_rejects",HwPortDirection.Output,u64),
            new("dynamic_class_exhaustion_rejects",HwPortDirection.Input,u64), new("next_dynamic_class_exhaustion_rejects",HwPortDirection.Output,u64),
            new("nop_due_to_no_class_capacity",HwPortDirection.Input,u64), new("next_nop_due_to_no_class_capacity",HwPortDirection.Output,u64),
            new("typed_slot_resource_conflict_rejects",HwPortDirection.Input,u64), new("next_typed_slot_resource_conflict_rejects",HwPortDirection.Output,u64),
            new("nop_due_to_resource_conflict",HwPortDirection.Input,u64), new("next_nop_due_to_resource_conflict",HwPortDirection.Output,u64),
            new("typed_slot_domain_rejects",HwPortDirection.Input,u64), new("next_typed_slot_domain_rejects",HwPortDirection.Output,u64),
            new("lane_conflict_rejects",HwPortDirection.Input,u64), new("next_lane_conflict_rejects",HwPortDirection.Output,u64),
            new("pinned_lane_conflicts",HwPortDirection.Input,u64), new("next_pinned_lane_conflicts",HwPortDirection.Output,u64),
            new("late_binding_conflicts",HwPortDirection.Input,u64), new("next_late_binding_conflicts",HwPortDirection.Output,u64),
            new("nop_due_to_pinned_constraint",HwPortDirection.Input,u64), new("next_nop_due_to_pinned_constraint",HwPortDirection.Output,u64),
            new("invalid_pinned_lane_rejects",HwPortDirection.Input,u64), new("next_invalid_pinned_lane_rejects",HwPortDirection.Output,u64),
            new("rejections_vt0",HwPortDirection.Input,u64), new("next_rejections_vt0",HwPortDirection.Output,u64),
            new("rejections_vt1",HwPortDirection.Input,u64), new("next_rejections_vt1",HwPortDirection.Output,u64),
            new("rejections_vt2",HwPortDirection.Input,u64), new("next_rejections_vt2",HwPortDirection.Output,u64),
            new("rejections_vt3",HwPortDirection.Input,u64), new("next_rejections_vt3",HwPortDirection.Output,u64),
            new("last_legality_reject_kind",HwPortDirection.Input,u8), new("last_certificate_reject_detail",HwPortDirection.Input,u8), new("candidate_slot_class",HwPortDirection.Input,u8),
            new("smt_owner_context_guard_rejects",HwPortDirection.Input,u64), new("next_smt_owner_context_guard_rejects",HwPortDirection.Output,u64),
            new("smt_domain_guard_rejects",HwPortDirection.Input,u64), new("next_smt_domain_guard_rejects",HwPortDirection.Output,u64),
            new("smt_boundary_guard_rejects",HwPortDirection.Input,u64), new("next_smt_boundary_guard_rejects",HwPortDirection.Output,u64),
            new("smt_shared_resource_certificate_rejects",HwPortDirection.Input,u64), new("next_smt_shared_resource_certificate_rejects",HwPortDirection.Output,u64),
            new("smt_register_group_certificate_rejects",HwPortDirection.Input,u64), new("next_smt_register_group_certificate_rejects",HwPortDirection.Output,u64),
            new("smt_legality_reject_by_alu_class",HwPortDirection.Input,u64), new("next_smt_legality_reject_by_alu_class",HwPortDirection.Output,u64),
            new("smt_legality_reject_by_lsu_class",HwPortDirection.Input,u64), new("next_smt_legality_reject_by_lsu_class",HwPortDirection.Output,u64),
            new("smt_legality_reject_by_dma_stream_class",HwPortDirection.Input,u64), new("next_smt_legality_reject_by_dma_stream_class",HwPortDirection.Output,u64),
            new("smt_legality_reject_by_matrix_tile_stream_class",HwPortDirection.Input,u64), new("next_smt_legality_reject_by_matrix_tile_stream_class",HwPortDirection.Output,u64),
            new("smt_legality_reject_by_branch_control",HwPortDirection.Input,u64), new("next_smt_legality_reject_by_branch_control",HwPortDirection.Output,u64),
            new("smt_legality_reject_by_system_singleton",HwPortDirection.Input,u64), new("next_smt_legality_reject_by_system_singleton",HwPortDirection.Output,u64),
            new("certificate_reject_by_alu_class",HwPortDirection.Input,u64), new("next_certificate_reject_by_alu_class",HwPortDirection.Output,u64),
            new("certificate_reject_by_lsu_class",HwPortDirection.Input,u64), new("next_certificate_reject_by_lsu_class",HwPortDirection.Output,u64),
            new("certificate_reject_by_dma_stream_class",HwPortDirection.Input,u64), new("next_certificate_reject_by_dma_stream_class",HwPortDirection.Output,u64),
            new("certificate_reject_by_matrix_tile_stream_class",HwPortDirection.Input,u64), new("next_certificate_reject_by_matrix_tile_stream_class",HwPortDirection.Output,u64),
            new("certificate_reject_by_branch_control",HwPortDirection.Input,u64), new("next_certificate_reject_by_branch_control",HwPortDirection.Output,u64),
            new("certificate_reject_by_system_singleton",HwPortDirection.Input,u64), new("next_certificate_reject_by_system_singleton",HwPortDirection.Output,u64),
            new("reg_group_conflicts_vt0",HwPortDirection.Input,u64), new("next_reg_group_conflicts_vt0",HwPortDirection.Output,u64),
            new("reg_group_conflicts_vt1",HwPortDirection.Input,u64), new("next_reg_group_conflicts_vt1",HwPortDirection.Output,u64),
            new("reg_group_conflicts_vt2",HwPortDirection.Input,u64), new("next_reg_group_conflicts_vt2",HwPortDirection.Output,u64),
            new("reg_group_conflicts_vt3",HwPortDirection.Input,u64), new("next_reg_group_conflicts_vt3",HwPortDirection.Output,u64)
        };
        var nodes = new List<HwNode> {
            new HwConstantNode("zero8",u8,"00000000",origin), new HwConstantNode("zero16",u16,"0000000000000000",origin),
            new HwConstantNode("onebit",bit,"1",origin), new HwConstantNode("one8",u8,"00000001",origin),
            new HwConstantNode("reason_1",u8,"00000001",reasonEnumOrigin), new HwConstantNode("reason_2",u8,"00000010",reasonEnumOrigin),
            new HwConstantNode("reason_3",u8,"00000011",reasonEnumOrigin), new HwConstantNode("reason_4",u8,"00000100",reasonEnumOrigin),
            new HwConstantNode("reason_11",u8,"00001011",reasonEnumOrigin), new HwConstantNode("reason_12",u8,"00001100",reasonEnumOrigin),
            new HwConstantNode("reason_13",u8,"00001101",reasonEnumOrigin), new HwConstantNode("vt0_u8",u8,"00000000",perVtOrigin),
            new HwConstantNode("vt1_u8",u8,"00000001",perVtOrigin), new HwConstantNode("vt2_u8",u8,"00000010",perVtOrigin),
            new HwConstantNode("vt3_u8",u8,"00000011",perVtOrigin),
            new HwConstantNode("reject_kind_boundary",u8,"00000001",origin), new HwConstantNode("reject_kind_owner",u8,"00000010",origin),
            new HwConstantNode("reject_kind_domain",u8,"00000011",origin), new HwConstantNode("certificate_detail_shared",u8,"00000001",origin),
            new HwConstantNode("certificate_detail_register_group",u8,"00000010",origin),
            new HwConstantNode("slot_class_alu",u8,"00000000",origin), new HwConstantNode("slot_class_lsu",u8,"00000001",origin),
            new HwConstantNode("slot_class_dma",u8,"00000010",origin), new HwConstantNode("slot_class_matrix",u8,"00000101",origin),
            new HwConstantNode("slot_class_branch",u8,"00000011",origin), new HwConstantNode("slot_class_system",u8,"00000100",origin),
            new HwConstantNode("one16",u16,"0000000000000001",origin), new HwConstantNode("one64",u64,"0000000000000000000000000000000000000000000000000000000000000001",origin),
            new HwConstantNode("line_bias",u64,"0000000000000000000000000000000000000000000000000000000000011111",origin),
            new HwConstantNode("five",u64,"0000000000000000000000000000000000000000000000000000000000000101",origin),
            new HwOperationNode("input_length_is_zero","eq",["prefetch_length","zero64"],bit,Origin:origin),
            new HwMuxNode("normalized_length","input_length_is_zero","line_size_64","prefetch_length",u64,origin),
            new HwConstantNode("line_size_64",u64,"0000000000000000000000000000000000000000000000000000000000100000",origin),
            new HwOperationNode("rounded_length","add",["normalized_length","line_bias"],u64,Origin:origin),
            new HwOperationNode("requested_lines","lshr64_mask6",["rounded_length","five"],u64,Origin:origin),
            new HwOperationNode("requested_is_zero","eq",["requested_lines","zero64"],bit,Origin:origin),
            new HwMuxNode("at_least_one_line","requested_is_zero","one64","requested_lines",u64,origin),
            new HwMuxNode("normalized_hot_cap","hot_cap_is_zero","one8","hot_line_cap",u8,origin),
            new HwMuxNode("normalized_cold_cap","cold_cap_is_zero","normalized_hot_cap","cold_line_cap",u8,origin),
            new HwMuxNode("selected_cap","locality_cold","normalized_cold_cap","normalized_hot_cap",u8,origin),
            new HwOperationNode("demand_vs_cap","ult64",["at_least_one_line","selected_cap_u64"],bit,Origin:origin),
            new HwMuxNode("capped_demand","demand_vs_cap","at_least_one_line","selected_cap_u64",u64,origin),
            new HwOperationNode("demand_u16","trunc",["capped_demand"],u16,Origin:origin),
            new HwOperationNode("hot_cap_is_zero","eq",["hot_line_cap","zero8"],bit,Origin:origin),
            new HwOperationNode("cold_cap_is_zero","eq",["cold_line_cap","zero8"],bit,Origin:origin),
            new HwOperationNode("selected_cap_u64","zext",["selected_cap"],u64,Origin:origin),
            new HwConstantNode("zero64",u64,new string('0',64),origin),
            new HwOperationNode("memory_budget64","zext",["remaining_memory_issue_budget"],u64,Origin:origin),
            new HwOperationNode("load_budget64","zext",["remaining_load_issue_budget"],u64,Origin:origin),
            new HwOperationNode("memory_budget_lt_load","ult64",["memory_budget64","load_budget64"],bit,Origin:origin),
            new HwMuxNode("snapshot_shared_credits","memory_budget_lt_load","remaining_memory_issue_budget","remaining_load_issue_budget",u8,origin),
            new HwOperationNode("policy_shared_zero","eq",["policy_shared_outer_cap_credits","zero8"],bit,Origin:origin),
            new HwMuxNode("policy_shared_norm","policy_shared_zero","one8","policy_shared_outer_cap_credits",u8,origin),
            new HwOperationNode("policy_shared64","zext",["policy_shared_norm"],u64,Origin:origin),
            new HwOperationNode("snapshot_shared64","zext",["snapshot_shared_credits"],u64,Origin:origin),
            new HwOperationNode("policy_shared_lt_snapshot","ult64",["policy_shared64","snapshot_shared64"],bit,Origin:origin),
            new HwMuxNode("initial_shared_credits","policy_shared_lt_snapshot","policy_shared_norm","snapshot_shared_credits",u8,origin),
            new HwMuxNode("effective_shared_credits","bp_state_valid","bp_shared_current","initial_shared_credits",u8,origin),
            new HwOperationNode("policy_lsu_zero","eq",["policy_lsu_carrier_credits","zero8"],bit,Origin:origin),
            new HwMuxNode("policy_lsu_norm","policy_lsu_zero","one8","policy_lsu_carrier_credits",u8,origin),
            new HwOperationNode("policy_dma_zero","eq",["policy_dma_carrier_credits","zero8"],bit,Origin:origin),
            new HwMuxNode("policy_dma_norm","policy_dma_zero","one8","policy_dma_carrier_credits",u8,origin),
            new HwMuxNode("effective_lsu_credits","bp_state_valid","lsu_carrier_current","policy_lsu_norm",u8,origin),
            new HwMuxNode("effective_dma_credits","bp_state_valid","dma_carrier_current","policy_dma_norm",u8,origin),
            new HwOperationNode("issue_empty","eq",["remaining_issue_credits","zero8"],bit,Origin:origin),
            new HwOperationNode("line_credits_u64","zext",["remaining_line_credits"],u64,Origin:origin),
            new HwOperationNode("line_demand_u64","zext",["demand_u16"],u64,Origin:origin),
            new HwOperationNode("lines_short","ult64",["line_credits_u64","line_demand_u64"],bit,Origin:origin),
            new HwOperationNode("not_issue_empty","xor",["issue_empty","onebit"],bit,Origin:origin),
            new HwOperationNode("not_lines_short","xor",["lines_short","onebit"],bit,Origin:origin),
            new HwOperationNode("quota_accept_calc","and",["not_issue_empty","not_lines_short"],bit,Origin:origin),
            new HwOperationNode("stage_a_reason_none","eq",["stage_a_reject_reason","zero8"],bit,Origin:stageAOrigin),
            new HwOperationNode("stage_b_reason_none","eq",["stage_b_reject_reason","zero8"],bit,Origin:stageBOrigin),
            new HwOperationNode("stage_a_rejected","xor",["stage_a_reason_none","onebit"],bit,Origin:stageAOrigin),
            new HwMuxNode("effective_early_reject_reason","stage_a_rejected","stage_a_reject_reason","stage_b_reject_reason",u8,stageAOrigin),
            new HwOperationNode("stage_b_rejected","xor",["stage_b_reason_none","onebit"],bit,Origin:stageBOrigin),
            new HwOperationNode("stage_b_reject_effective","and",["stage_a_reason_none","stage_b_rejected"],bit,Origin:stageBOrigin),
            new HwOperationNode("pre_reservation_reject","or",["stage_a_rejected","stage_b_reject_effective"],bit,Origin:assistRejectOrigin),
            new HwOperationNode("pre_reservation_pass","xor",["pre_reservation_reject","onebit"],bit,Origin:assistRejectOrigin),
            new HwOperationNode("reason_static","eq",["effective_early_reject_reason","reason_1"],bit,Origin:reasonEnumOrigin),
            new HwOperationNode("reason_dynamic","eq",["effective_early_reject_reason","reason_2"],bit,Origin:reasonEnumOrigin),
            new HwOperationNode("reason_resource","eq",["effective_early_reject_reason","reason_3"],bit,Origin:reasonEnumOrigin),
            new HwOperationNode("reason_domain","eq",["effective_early_reject_reason","reason_4"],bit,Origin:reasonEnumOrigin),
            new HwOperationNode("reason_pinned","eq",["effective_early_reject_reason","reason_11"],bit,Origin:reasonEnumOrigin),
            new HwOperationNode("reason_late","eq",["effective_early_reject_reason","reason_12"],bit,Origin:reasonEnumOrigin),
            new HwOperationNode("reason_invalid_pinned","eq",["effective_early_reject_reason","reason_13"],bit,Origin:reasonEnumOrigin),
            new HwOperationNode("reason_class_capacity","or",["reason_static","reason_dynamic"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("reason_lane_conflict","or",["reason_pinned","reason_late"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("reject_kind_is_boundary","eq",["last_legality_reject_kind","reject_kind_boundary"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("reject_kind_is_owner","eq",["last_legality_reject_kind","reject_kind_owner"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("reject_kind_is_domain","eq",["last_legality_reject_kind","reject_kind_domain"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("detail_is_shared","eq",["last_certificate_reject_detail","certificate_detail_shared"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("detail_is_register_group","eq",["last_certificate_reject_detail","certificate_detail_register_group"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("class_is_alu","eq",["candidate_slot_class","slot_class_alu"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("class_is_lsu","eq",["candidate_slot_class","slot_class_lsu"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("class_is_dma","eq",["candidate_slot_class","slot_class_dma"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("class_is_matrix","eq",["candidate_slot_class","slot_class_matrix"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("class_is_branch","eq",["candidate_slot_class","slot_class_branch"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("class_is_system","eq",["candidate_slot_class","slot_class_system"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("detail_present","or",["detail_is_shared","detail_is_register_group"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("cert_detail_present_event","and",["reason_resource","detail_present"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("owner_detail_event","and",["reason_resource","reject_kind_is_owner"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("domain_detail_event","and",["reason_resource","reject_kind_is_domain"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("boundary_detail_event","and",["reason_resource","reject_kind_is_boundary"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("shared_certificate_event","and",["reason_resource","detail_is_shared"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("register_group_certificate_event","and",["reason_resource","detail_is_register_group"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("smt_alu_class_event","and",["reason_resource","class_is_alu"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("smt_lsu_class_event","and",["reason_resource","class_is_lsu"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("smt_dma_class_event","and",["reason_resource","class_is_dma"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("smt_matrix_class_event","and",["reason_resource","class_is_matrix"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("smt_branch_class_event","and",["reason_resource","class_is_branch"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("smt_system_class_event","and",["reason_resource","class_is_system"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("cert_alu_class_event","and",["cert_detail_present_event","class_is_alu"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("cert_lsu_class_event","and",["cert_detail_present_event","class_is_lsu"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("cert_dma_class_event","and",["cert_detail_present_event","class_is_dma"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("cert_matrix_class_event","and",["cert_detail_present_event","class_is_matrix"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("cert_branch_class_event","and",["cert_detail_present_event","class_is_branch"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("cert_system_class_event","and",["cert_detail_present_event","class_is_system"],bit,Origin:rejectCounterOrigin),
            new HwOperationNode("vt0_hit","eq",["candidate_vt","vt0_u8"],bit,Origin:perVtOrigin),
            new HwOperationNode("vt1_hit","eq",["candidate_vt","vt1_u8"],bit,Origin:perVtOrigin),
            new HwOperationNode("vt2_hit","eq",["candidate_vt","vt2_u8"],bit,Origin:perVtOrigin),
            new HwOperationNode("vt3_hit","eq",["candidate_vt","vt3_u8"],bit,Origin:perVtOrigin),
            new HwOperationNode("vt0_reject_event","and",["pre_reservation_reject","vt0_hit"],bit,Origin:perVtOrigin),
            new HwOperationNode("vt1_reject_event","and",["pre_reservation_reject","vt1_hit"],bit,Origin:perVtOrigin),
            new HwOperationNode("vt2_reject_event","and",["pre_reservation_reject","vt2_hit"],bit,Origin:perVtOrigin),
            new HwOperationNode("vt3_reject_event","and",["pre_reservation_reject","vt3_hit"],bit,Origin:perVtOrigin),
            new HwOperationNode("reg_group_vt0_event","and",["register_group_certificate_event","vt0_hit"],bit,Origin:perVtOrigin),
            new HwOperationNode("reg_group_vt1_event","and",["register_group_certificate_event","vt1_hit"],bit,Origin:perVtOrigin),
            new HwOperationNode("reg_group_vt2_event","and",["register_group_certificate_event","vt2_hit"],bit,Origin:perVtOrigin),
            new HwOperationNode("reg_group_vt3_event","and",["register_group_certificate_event","vt3_hit"],bit,Origin:perVtOrigin),
            new HwOperationNode("bp_accept_effective","and",["bp_accept_calc","pre_reservation_pass"],bit,Origin:origin),
            new HwOperationNode("accept_calc","and",["bp_accept_effective","quota_accept_calc"],bit,Origin:origin),
            new HwOperationNode("bp_reject_raw","xor",["bp_accept_calc","onebit"],bit,Origin:origin),
            new HwOperationNode("bp_reject_event","and",["bp_reject_raw","pre_reservation_pass"],bit,Origin:origin),
            new HwOperationNode("quota_reject_event","or",["reject_issue_calc","reject_lines_calc"],bit,Origin:origin),
            new HwOperationNode("assist_reject_post_event","or",["bp_reject_event","quota_reject_event"],bit,Origin:origin),
            new HwOperationNode("assist_reject_event","or",["pre_reservation_reject","assist_reject_post_event"],bit,Origin:origin),
            new HwOperationNode("reject_lines_raw","and",["not_issue_empty","lines_short"],bit,Origin:origin),
            new HwOperationNode("reject_lines_calc","and",["bp_accept_effective","reject_lines_raw"],bit,Origin:origin),
            new HwOperationNode("reject_issue_calc","and",["bp_accept_effective","issue_empty"],bit,Origin:origin),
            new HwOperationNode("next_issue_calc","sub",["remaining_issue_credits","one8"],u8,Origin:origin),
            new HwOperationNode("next_lines_calc","sub",["remaining_line_credits","demand_u16"],u16,Origin:origin),
            new HwMuxNode("next_issue","accept_calc","next_issue_calc","remaining_issue_credits",u8,origin),
            new HwMuxNode("next_lines","accept_calc","next_lines_calc","remaining_line_credits",u16,origin),
            new HwConstantNode("zero32",u32,new string('0',32),origin), new HwConstantNode("one32",u32,"00000000000000000000000000000001",origin),
            new HwConstantNode("two32",u32,"00000000000000000000000000000010",origin), new HwConstantNode("three32",u32,"00000000000000000000000000000011",origin),
            new HwConstantNode("sixteen64",u64,"0000000000000000000000000000000000000000000000000000000000010000",origin),
            new HwConstantNode("ones32",u32,new string('1',32),origin),
            new HwConstantNode("vt0",u8,"00000000",origin), new HwConstantNode("vt1",u8,"00000001",origin),
            new HwConstantNode("vt2",u8,"00000010",origin), new HwConstantNode("vt3",u8,"00000011",origin),
            new HwOperationNode("target0","eq",["target_vt","vt0"],bit,Origin:origin), new HwOperationNode("target1","eq",["target_vt","vt1"],bit,Origin:origin),
            new HwOperationNode("target2","eq",["target_vt","vt2"],bit,Origin:origin), new HwOperationNode("target3","eq",["target_vt","vt3"],bit,Origin:origin),
            new HwOperationNode("target01","or",["target0","target1"],bit,Origin:origin), new HwOperationNode("target23","or",["target2","target3"],bit,Origin:origin),
            new HwOperationNode("target_valid","or",["target01","target23"],bit,Origin:origin),
            new HwMuxNode("target_count_3","target3","outstanding_count_vt3","zero8",u8,origin),
            new HwMuxNode("target_count_2","target2","outstanding_count_vt2","target_count_3",u8,origin),
            new HwMuxNode("target_count_1","target1","outstanding_count_vt1","target_count_2",u8,origin),
            new HwMuxNode("target_count","target0","outstanding_count_vt0","target_count_1",u8,origin),
            new HwMuxNode("target_capacity_3","target3","outstanding_capacity_vt3","zero8",u8,origin),
            new HwMuxNode("target_capacity_2","target2","outstanding_capacity_vt2","target_capacity_3",u8,origin),
            new HwMuxNode("target_capacity_1","target1","outstanding_capacity_vt1","target_capacity_2",u8,origin),
            new HwMuxNode("target_capacity","target0","outstanding_capacity_vt0","target_capacity_1",u8,origin),
            new HwOperationNode("target_count64","zext",["target_count"],u64,Origin:origin), new HwOperationNode("target_capacity64","zext",["target_capacity"],u64,Origin:origin),
            new HwOperationNode("target_has_room","ult64",["target_count64","target_capacity64"],bit,Origin:origin),
            new HwOperationNode("outstanding_ok","and",["target_valid","target_has_room"],bit,Origin:origin),
            new HwOperationNode("shared_empty","eq",["effective_shared_credits","zero8"],bit,Origin:origin),
            new HwOperationNode("not_shared_empty","xor",["shared_empty","onebit"],bit,Origin:origin),
            new HwOperationNode("bank32","zext",["bank_id"],u32,Origin:origin), new HwOperationNode("bank64","zext",["bank_id"],u64,Origin:origin),
            new HwOperationNode("bank_valid","ult64",["bank64","sixteen64"],bit,Origin:origin),
            new HwOperationNode("read_mask","shl32_mask5",["one32","bank32"],u32,Origin:origin),
            new HwOperationNode("read_one_mask32","zext",["read_budget_at_least_one"],u32,Origin:origin),
            new HwOperationNode("read_two_mask32","zext",["read_budget_at_least_two"],u32,Origin:origin),
            new HwOperationNode("read_one_hit32","and",["read_mask","read_one_mask32"],u32,Origin:origin), new HwOperationNode("read_two_hit32","and",["read_mask","read_two_mask32"],u32,Origin:origin),
            new HwOperationNode("read_one_hit","eq",["read_one_hit32","zero32"],bit,Origin:origin), new HwOperationNode("read_two_hit","eq",["read_two_hit32","zero32"],bit,Origin:origin),
            new HwOperationNode("read_one_present","xor",["read_one_hit","onebit"],bit,Origin:origin), new HwOperationNode("read_two_present","xor",["read_two_hit","onebit"],bit,Origin:origin),
            new HwMuxNode("read_budget_one","read_one_present","one32","zero32",u32,origin), new HwMuxNode("read_budget_pick","read_two_present","two32","read_budget_one",u32,origin),
            new HwOperationNode("bank_shift","shl32_mask5",["bank32","one32"],u32,Origin:origin),
            new HwOperationNode("consumed_shifted","lshr32_mask5",["consumed_shared_read_packed","bank_shift"],u32,Origin:origin),
            new HwOperationNode("consumed_current","and",["consumed_shifted","three32"],u32,Origin:origin),
            new HwOperationNode("consumed64","zext",["consumed_current"],u64,Origin:origin), new HwOperationNode("budget64","zext",["read_budget_pick"],u64,Origin:origin),
            new HwOperationNode("bank_under_budget","ult64",["consumed64","budget64"],bit,Origin:origin),
            new HwMuxNode("bank_or_invalid_ok","bank_valid","bank_under_budget","onebit",bit,origin),
            new HwOperationNode("shared_ok","and",["not_shared_empty","bank_or_invalid_ok"],bit,Origin:origin),
            new HwMuxNode("selected_carrier_credits","is_dma_carrier","effective_dma_credits","effective_lsu_credits",u8,origin),
            new HwOperationNode("carrier_empty","eq",["selected_carrier_credits","zero8"],bit,Origin:origin),
            new HwOperationNode("carrier_ok","xor",["carrier_empty","onebit"],bit,Origin:origin),
            new HwOperationNode("not_dma","xor",["is_dma_carrier","onebit"],bit,Origin:origin),
            new HwOperationNode("srf_resident_budget_zero","eq",["srf_resident_budget","zero8"],bit,Origin:origin),
            new HwMuxNode("srf_resident_budget_norm","srf_resident_budget_zero","one8","srf_resident_budget",u8,origin),
            new HwOperationNode("srf_loading_budget_zero","eq",["srf_loading_budget","zero8"],bit,Origin:origin),
            new HwMuxNode("srf_loading_budget_norm","srf_loading_budget_zero","one8","srf_loading_budget",u8,origin),
            new HwOperationNode("srf_loading_count64","zext",["srf_loading_count"],u64,Origin:origin),
            new HwOperationNode("srf_loading_budget64","zext",["srf_loading_budget_norm"],u64,Origin:origin),
            new HwOperationNode("srf_resident_count64","zext",["srf_resident_count"],u64,Origin:origin),
            new HwOperationNode("srf_resident_budget64","zext",["srf_resident_budget_norm"],u64,Origin:origin),
            new HwOperationNode("srf_loading_room","ult64",["srf_loading_count64","srf_loading_budget64"],bit,Origin:origin),
            new HwOperationNode("srf_resident_room","ult64",["srf_resident_count64","srf_resident_budget64"],bit,Origin:origin),
            new HwOperationNode("srf_no_empty","xor",["srf_has_empty_slot","onebit"],bit,Origin:origin),
            new HwOperationNode("srf_empty_allowed","and",["srf_has_empty_slot","srf_resident_room"],bit,Origin:origin),
            new HwOperationNode("srf_victim_allowed","and",["srf_no_empty","srf_has_assist_victim"],bit,Origin:origin),
            new HwOperationNode("srf_slot_allowed_raw","or",["srf_empty_allowed","srf_victim_allowed"],bit,Origin:origin),
            new HwOperationNode("srf_slot_allowed","and",["srf_loading_room","srf_slot_allowed_raw"],bit,Origin:origin),
            new HwOperationNode("srf_no_file","xor",["srf_file_present","onebit"],bit,Origin:origin),
            new HwOperationNode("srf_without_file_or_slot","or",["srf_no_file","srf_slot_allowed"],bit,Origin:origin),
            new HwOperationNode("srf_allocatable","or",["srf_reuse_match","srf_without_file_or_slot"],bit,Origin:origin),
            new HwOperationNode("srf_unavailable","xor",["srf_allocatable","onebit"],bit,Origin:origin),
            new HwOperationNode("srf_reject_raw","and",["is_dma_carrier","srf_unavailable"],bit,Origin:origin),
            new HwOperationNode("prior_gates","and",["outstanding_ok","shared_ok"],bit,Origin:origin),
            new HwOperationNode("prior_gates_carrier","and",["prior_gates","carrier_ok"],bit,Origin:origin),
            new HwOperationNode("srf_reject","and",["prior_gates_carrier","srf_reject_raw"],bit,Origin:origin),
            new HwOperationNode("bp_accept01","and",["prior_gates_carrier","not_srf_reject_raw"],bit,Origin:origin),
            new HwOperationNode("not_srf_reject_raw","xor",["srf_reject_raw","onebit"],bit,Origin:origin),
            new HwOperationNode("reject_outstanding_calc","xor",["outstanding_ok","onebit"],bit,Origin:origin),
            new HwOperationNode("outer_and_carrier_ok","and",["shared_ok","carrier_ok"],bit,Origin:origin),
            new HwOperationNode("reject_shared_raw","xor",["outer_and_carrier_ok","onebit"],bit,Origin:origin),
            new HwOperationNode("reject_shared_calc","and",["outstanding_ok","reject_shared_raw"],bit,Origin:origin),
            new HwOperationNode("bp_accept_calc","and",["bp_accept01","target_valid"],bit,Origin:origin),
            new HwOperationNode("bp_shared_dec","sub",["effective_shared_credits","one8"],u8,Origin:origin),
            new HwOperationNode("lsu_carrier_dec","sub",["effective_lsu_credits","one8"],u8,Origin:origin),
            new HwOperationNode("dma_carrier_dec","sub",["effective_dma_credits","one8"],u8,Origin:origin),
            new HwOperationNode("consume_lsu","and",["bp_accept_effective","not_dma"],bit,Origin:origin), new HwOperationNode("consume_dma","and",["bp_accept_effective","is_dma_carrier"],bit,Origin:origin),
            new HwMuxNode("next_bp_shared","bp_accept_effective","bp_shared_dec","effective_shared_credits",u8,origin),
            new HwMuxNode("next_lsu_carrier","consume_lsu","lsu_carrier_dec","effective_lsu_credits",u8,origin),
            new HwMuxNode("next_dma_carrier","consume_dma","dma_carrier_dec","effective_dma_credits",u8,origin),
            new HwConstantNode("three8",u8,"00000011",origin),
            new HwConstantNode("eight8",u8,"00001000",origin), new HwConstantNode("eight64",u64,"0000000000000000000000000000000000000000000000000000000000001000",origin),
            new HwOperationNode("count_vt0_64","zext",["outstanding_count_vt0"],u64,Origin:origin), new HwOperationNode("count_vt1_64","zext",["outstanding_count_vt1"],u64,Origin:origin),
            new HwOperationNode("count_vt2_64","zext",["outstanding_count_vt2"],u64,Origin:origin), new HwOperationNode("count_vt3_64","zext",["outstanding_count_vt3"],u64,Origin:origin),
            new HwOperationNode("count_vt0_below8","ult64",["count_vt0_64","eight64"],bit,Origin:origin), new HwOperationNode("count_vt1_below8","ult64",["count_vt1_64","eight64"],bit,Origin:origin),
            new HwOperationNode("count_vt2_below8","ult64",["count_vt2_64","eight64"],bit,Origin:origin), new HwOperationNode("count_vt3_below8","ult64",["count_vt3_64","eight64"],bit,Origin:origin),
            new HwOperationNode("count_vt0_inc","add",["outstanding_count_vt0","one8"],u8,Origin:origin), new HwOperationNode("count_vt1_inc","add",["outstanding_count_vt1","one8"],u8,Origin:origin),
            new HwOperationNode("count_vt2_inc","add",["outstanding_count_vt2","one8"],u8,Origin:origin), new HwOperationNode("count_vt3_inc","add",["outstanding_count_vt3","one8"],u8,Origin:origin),
            new HwMuxNode("count_vt0_sat","count_vt0_below8","count_vt0_inc","outstanding_count_vt0",u8,origin), new HwMuxNode("count_vt1_sat","count_vt1_below8","count_vt1_inc","outstanding_count_vt1",u8,origin),
            new HwMuxNode("count_vt2_sat","count_vt2_below8","count_vt2_inc","outstanding_count_vt2",u8,origin), new HwMuxNode("count_vt3_sat","count_vt3_below8","count_vt3_inc","outstanding_count_vt3",u8,origin),
            new HwOperationNode("consume_vt0","and",["bp_accept_effective","target0"],bit,Origin:origin), new HwOperationNode("consume_vt1","and",["bp_accept_effective","target1"],bit,Origin:origin),
            new HwOperationNode("consume_vt2","and",["bp_accept_effective","target2"],bit,Origin:origin), new HwOperationNode("consume_vt3","and",["bp_accept_effective","target3"],bit,Origin:origin),
            new HwMuxNode("next_count_vt0","consume_vt0","count_vt0_sat","outstanding_count_vt0",u8,origin), new HwMuxNode("next_count_vt1","consume_vt1","count_vt1_sat","outstanding_count_vt1",u8,origin),
            new HwMuxNode("next_count_vt2","consume_vt2","count_vt2_sat","outstanding_count_vt2",u8,origin), new HwMuxNode("next_count_vt3","consume_vt3","count_vt3_sat","outstanding_count_vt3",u8,origin),
            new HwOperationNode("bank_field_mask","shl32_mask5",["three32","bank_shift"],u32,Origin:origin),
            new HwOperationNode("bank_clear_mask","xor",["bank_field_mask","ones32"],u32,Origin:origin),
            new HwOperationNode("packed_cleared","and",["consumed_shared_read_packed","bank_clear_mask"],u32,Origin:origin),
            new HwOperationNode("consumed_increment","add",["consumed_current","one32"],u32,Origin:origin),
            new HwOperationNode("consumed_is_three","eq",["consumed_current","three32"],bit,Origin:origin),
            new HwMuxNode("consumed_saturated","consumed_is_three","consumed_current","consumed_increment",u32,origin),
            new HwOperationNode("consumed_new_shifted","shl32_mask5",["consumed_saturated","bank_shift"],u32,Origin:origin),
            new HwOperationNode("packed_updated","or",["packed_cleared","consumed_new_shifted"],u32,Origin:origin),
            new HwOperationNode("update_bank_counter","and",["bp_accept_effective","bank_valid"],bit,Origin:origin),
            new HwMuxNode("next_packed","update_bank_counter","packed_updated","consumed_shared_read_packed",u32,origin),
            new HwOutputDriveNode("out_accept","accept","accept_calc",origin), new HwOutputDriveNode("out_issue_reject","reject_issue","reject_issue_calc",origin),
            new HwOutputDriveNode("out_line_reject","reject_lines","reject_lines_calc",origin), new HwOutputDriveNode("out_next_issue","next_issue_credits","next_issue",origin),
            new HwOutputDriveNode("out_next_lines","next_line_credits","next_lines",origin), new HwOutputDriveNode("out_demand","reserved_line_demand","demand_u16",origin),
            new HwOutputDriveNode("out_bp_accepted","backpressure_accepted","bp_accept_effective",origin),
            new HwOperationNode("outstanding_reject_effective","and",["reject_outstanding_calc","pre_reservation_pass"],bit,Origin:origin),
            new HwOperationNode("shared_reject_effective","and",["reject_shared_calc","pre_reservation_pass"],bit,Origin:origin),
            new HwOperationNode("srf_reject_effective","and",["srf_reject","pre_reservation_pass"],bit,Origin:origin),
            new HwOutputDriveNode("out_bp_reject_outstanding","reject_outstanding","outstanding_reject_effective",origin),
            new HwOutputDriveNode("out_bp_reject_shared","reject_shared_outer","shared_reject_effective",origin),
            new HwOutputDriveNode("out_bp_reject_srf","reject_dma_srf","srf_reject_effective",origin),
            new HwOutputDriveNode("out_bp_shared","next_bp_shared_credits","next_bp_shared",origin),
            new HwOutputDriveNode("out_bp_lsu","next_lsu_carrier_credits","next_lsu_carrier",origin),
            new HwOutputDriveNode("out_bp_dma","next_dma_carrier_credits","next_dma_carrier",origin),
            new HwOutputDriveNode("out_bp_packed","next_consumed_shared_read_packed","next_packed",origin),
            new HwOutputDriveNode("out_count0","next_outstanding_count_vt0","next_count_vt0",origin), new HwOutputDriveNode("out_count1","next_outstanding_count_vt1","next_count_vt1",origin),
            new HwOutputDriveNode("out_count2","next_outstanding_count_vt2","next_count_vt2",origin), new HwOutputDriveNode("out_count3","next_outstanding_count_vt3","next_count_vt3",origin)
        };
        (string Id, string Input, string Output, string Event)[] counterPaths = [
            ("smt_reject","smt_rejections_count","next_smt_rejections_count","assist_reject_event"),
            ("assist_reject","assist_rejects","next_assist_rejects","assist_reject_event"),
            ("bp_reject","assist_backpressure_rejects","next_assist_backpressure_rejects","bp_reject_event"),
            ("bp_outer_reject","assist_backpressure_outer_rejects","next_assist_backpressure_outer_rejects","shared_reject_effective"),
            ("bp_mshr_reject","assist_backpressure_mshr_rejects","next_assist_backpressure_mshr_rejects","outstanding_reject_effective"),
            ("bp_srf_reject","assist_backpressure_dma_srf_rejects","next_assist_backpressure_dma_srf_rejects","srf_reject_effective"),
            ("quota_reject","assist_quota_rejects","next_assist_quota_rejects","quota_reject_event"),
            ("quota_issue_reject","assist_quota_issue_rejects","next_assist_quota_issue_rejects","reject_issue_calc"),
            ("quota_line_reject","assist_quota_line_rejects","next_assist_quota_line_rejects","reject_lines_calc"),
            ("class_capacity","class_capacity_rejects","next_class_capacity_rejects","reason_class_capacity"),
            ("static_overcommit","static_class_overcommit_rejects","next_static_class_overcommit_rejects","reason_static"),
            ("dynamic_exhaustion","dynamic_class_exhaustion_rejects","next_dynamic_class_exhaustion_rejects","reason_dynamic"),
            ("nop_class_capacity","nop_due_to_no_class_capacity","next_nop_due_to_no_class_capacity","reason_class_capacity"),
            ("typed_resource","typed_slot_resource_conflict_rejects","next_typed_slot_resource_conflict_rejects","reason_resource"),
            ("nop_resource","nop_due_to_resource_conflict","next_nop_due_to_resource_conflict","reason_resource"),
            ("typed_domain","typed_slot_domain_rejects","next_typed_slot_domain_rejects","reason_domain"),
            ("lane_conflict","lane_conflict_rejects","next_lane_conflict_rejects","reason_lane_conflict"),
            ("pinned_conflict","pinned_lane_conflicts","next_pinned_lane_conflicts","reason_pinned"),
            ("late_conflict","late_binding_conflicts","next_late_binding_conflicts","reason_late"),
            ("nop_pinned","nop_due_to_pinned_constraint","next_nop_due_to_pinned_constraint","reason_pinned"),
            ("invalid_pinned","invalid_pinned_lane_rejects","next_invalid_pinned_lane_rejects","reason_invalid_pinned"),
            ("per_vt0","rejections_vt0","next_rejections_vt0","vt0_reject_event"),
            ("per_vt1","rejections_vt1","next_rejections_vt1","vt1_reject_event"),
            ("per_vt2","rejections_vt2","next_rejections_vt2","vt2_reject_event"),
            ("per_vt3","rejections_vt3","next_rejections_vt3","vt3_reject_event"),
            ("owner_guard","smt_owner_context_guard_rejects","next_smt_owner_context_guard_rejects","owner_detail_event"),
            ("domain_guard","smt_domain_guard_rejects","next_smt_domain_guard_rejects","domain_detail_event"),
            ("boundary_guard","smt_boundary_guard_rejects","next_smt_boundary_guard_rejects","boundary_detail_event"),
            ("shared_certificate","smt_shared_resource_certificate_rejects","next_smt_shared_resource_certificate_rejects","shared_certificate_event"),
            ("register_group_certificate","smt_register_group_certificate_rejects","next_smt_register_group_certificate_rejects","register_group_certificate_event"),
            ("legality_class_alu","smt_legality_reject_by_alu_class","next_smt_legality_reject_by_alu_class","smt_alu_class_event"),
            ("legality_class_lsu","smt_legality_reject_by_lsu_class","next_smt_legality_reject_by_lsu_class","smt_lsu_class_event"),
            ("legality_class_dma","smt_legality_reject_by_dma_stream_class","next_smt_legality_reject_by_dma_stream_class","smt_dma_class_event"),
            ("legality_class_matrix","smt_legality_reject_by_matrix_tile_stream_class","next_smt_legality_reject_by_matrix_tile_stream_class","smt_matrix_class_event"),
            ("legality_class_branch","smt_legality_reject_by_branch_control","next_smt_legality_reject_by_branch_control","smt_branch_class_event"),
            ("legality_class_system","smt_legality_reject_by_system_singleton","next_smt_legality_reject_by_system_singleton","smt_system_class_event"),
            ("certificate_class_alu","certificate_reject_by_alu_class","next_certificate_reject_by_alu_class","cert_alu_class_event"),
            ("certificate_class_lsu","certificate_reject_by_lsu_class","next_certificate_reject_by_lsu_class","cert_lsu_class_event"),
            ("certificate_class_dma","certificate_reject_by_dma_stream_class","next_certificate_reject_by_dma_stream_class","cert_dma_class_event"),
            ("certificate_class_matrix","certificate_reject_by_matrix_tile_stream_class","next_certificate_reject_by_matrix_tile_stream_class","cert_matrix_class_event"),
            ("certificate_class_branch","certificate_reject_by_branch_control","next_certificate_reject_by_branch_control","cert_branch_class_event"),
            ("certificate_class_system","certificate_reject_by_system_singleton","next_certificate_reject_by_system_singleton","cert_system_class_event"),
            ("reg_group_vt0","reg_group_conflicts_vt0","next_reg_group_conflicts_vt0","reg_group_vt0_event"),
            ("reg_group_vt1","reg_group_conflicts_vt1","next_reg_group_conflicts_vt1","reg_group_vt1_event"),
            ("reg_group_vt2","reg_group_conflicts_vt2","next_reg_group_conflicts_vt2","reg_group_vt2_event"),
            ("reg_group_vt3","reg_group_conflicts_vt3","next_reg_group_conflicts_vt3","reg_group_vt3_event")
        ];
        foreach (var c in counterPaths)
        {
            string event64 = c.Id + "_event64", increment = c.Id + "_increment", next = c.Id + "_next", drive = c.Id + "_drive";
            HwSourceOrigin counterOrigin = c.Id switch
            {
                "assist_reject" => assistRejectOrigin,
                "bp_reject" or "bp_outer_reject" or "bp_mshr_reject" or "bp_srf_reject" => bpOrigin,
                "per_vt0" or "per_vt1" or "per_vt2" or "per_vt3" or "reg_group_vt0" or "reg_group_vt1" or "reg_group_vt2" or "reg_group_vt3" => perVtOrigin,
                "quota_reject" or "quota_issue_reject" or "quota_line_reject" => origin,
                _ => rejectCounterOrigin
            };
            nodes.Add(new HwOperationNode(event64,"zext",[c.Event],u64,Origin:counterOrigin));
            nodes.Add(new HwOperationNode(increment,"add",[c.Input,"one64"],u64,Origin:counterOrigin));
            nodes.Add(new HwMuxNode(next,c.Event,increment,c.Input,u64,counterOrigin));
            nodes.Add(new HwOutputDriveNode(drive,c.Output,next,counterOrigin));
        }
        var module = new HwModule("assist_memory_quota_reservation","assist_memory_quota_reservation",ports,nodes,[],
            new HwHardwareContract("scheduler.assist-memory-quota.pass-local.v1",HwModuleKind.Combinational,ports,new HwTimingContract(0,1)),origin);
        var result = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",module.Id,[module],toolchain,[]));
        return result.Validated ?? throw new InvalidDataException("Assist quota HWIR refused: "+string.Join("; ",result.Diagnostics.Select(d=>d.Code+" "+d.Message)));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
