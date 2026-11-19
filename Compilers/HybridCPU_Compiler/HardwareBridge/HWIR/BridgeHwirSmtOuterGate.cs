#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Source-bound fail-fast ordering for the non-Assist SMT A3 outer-cap gate.</summary>
public static class BridgeHwirSmtOuterGate
{
    public const string AdmissionSha256 = "6cdd37f43a0a58c0b887f9e8bffe9d2d17abb5d404f7ace70fead05db4edbe25";
    public const string SlotDefinitionsSha256 = "489edf25fe528393aba2e298c259e7929f59260430b4946ccea4c713065d9ff1";
    public const string ScoreboardSha256 = "19f17c5417a4b981af5ebb6fa33da2a48ec980dad86a0e8291553205945497de";
    public const string InfrastructureSha256 = "0151477bee4622e64b4f6e63b38cb671becfaca87acc1eacbc2687feb2ee2f8a";
    public const string LoadStoreSha256 = "5dac6c801751a25234c49c7f905dd56a6472bef8bcdc9de2e30cba872026193f";
    public const string AtomicSha256 = "8dd8cfb04d4c7fc7554cceeab0daf8a8f6db54ee281cc102b4a5ee68c606c5cd";
    public const string OccupancySha256 = "fbf74223e62df20d1c74963925e063d25b00080deb5a3ffba15c2a008acc8e7d";
    public const string SchedulerSha256 = "c7e675d76bb4eed9ecccb0b20c13c973895cce556e93c21457d966add731d8bf";
    public const string Commit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";

    public static HwValidatedDesign Compile(byte[] admission, byte[] slotDefinitions, byte[] scoreboard, byte[] infrastructure, byte[] loadStore, byte[] atomic, byte[] occupancyContract, byte[] scheduler, string[] hashes, string commit, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(admission); ArgumentNullException.ThrowIfNull(slotDefinitions); ArgumentNullException.ThrowIfNull(scoreboard); ArgumentNullException.ThrowIfNull(infrastructure); ArgumentNullException.ThrowIfNull(loadStore); ArgumentNullException.ThrowIfNull(atomic); ArgumentNullException.ThrowIfNull(occupancyContract); ArgumentNullException.ThrowIfNull(scheduler);
        if (admission.Length > 2_000_000 || slotDefinitions.Length > 2_000_000 || scoreboard.Length > 2_000_000 || infrastructure.Length > 2_000_000 || loadStore.Length > 2_000_000 || atomic.Length > 2_000_000 || occupancyContract.Length > 2_000_000 || scheduler.Length > 2_000_000 || hashes.Length != 8 ||
            Hash(admission) != AdmissionSha256 || Hash(slotDefinitions) != SlotDefinitionsSha256 || Hash(scoreboard) != ScoreboardSha256 || Hash(infrastructure) != InfrastructureSha256 || Hash(loadStore) != LoadStoreSha256 || Hash(atomic) != AtomicSha256 || Hash(occupancyContract) != OccupancySha256 || Hash(scheduler) != SchedulerSha256 ||
            hashes[0] != AdmissionSha256 || hashes[1] != SlotDefinitionsSha256 || hashes[2] != ScoreboardSha256 || hashes[3] != InfrastructureSha256 || hashes[4] != LoadStoreSha256 || hashes[5] != AtomicSha256 || hashes[6] != OccupancySha256 || hashes[7] != SchedulerSha256 || commit != Commit)
            throw new InvalidDataException("Pinned SMT outer-gate source identity differs.");
        SyntaxNode root = Parse(admission); SyntaxNode definitionsRoot = Parse(slotDefinitions); SyntaxNode scoreboardRoot = Parse(scoreboard); SyntaxNode infrastructureRoot = Parse(infrastructure); SyntaxNode loadStoreRoot = Parse(loadStore); SyntaxNode atomicRoot = Parse(atomic); SyntaxNode occupancyRoot = Parse(occupancyContract); SyntaxNode schedulerRoot = Parse(scheduler);
        MethodDeclarationSyntax method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "TryPassOuterCap" && m.ParameterList.Parameters.Count == 6);
        ClassDeclarationSyntax schedulerType = schedulerRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "MicroOpScheduler" && c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core");
        if (!schedulerType.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.ValueText == "SpeculationBudgetEnabled" && p.Type.ToString() == "bool" && p.Initializer?.Value.ToString() == "true") ||
            !schedulerType.Members.OfType<FieldDeclarationSyntax>().Any(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "_speculationBudget") && f.Declaration.Type.ToString() == "int") ||
            !schedulerType.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.ValueText == "SpeculationBudgetExhaustionEvents" && p.Type.ToString() == "long" && p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true && p.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) && a.Modifiers.Any(SyntaxKind.PrivateKeyword))))
            throw new InvalidDataException("Speculation-budget sampled state contract changed.");
        string tokens = Tokens(method);
        string[] orderedChecks =
        [
            "if(SuppressLsu&&isMemoryOp){MemoryWallSuppressionsCount++;SmtRejectionsCount++;rejectReason=TypedSlotRejectReason.ScoreboardReject;returnfalse;}",
            "if(!HasProjectedOutstandingMemoryCapacity(isMemoryOp,virtualThreadId)){MshrScoreboardStalls++;SmtRejectionsCount++;rejectReason=TypedSlotRejectReason.ScoreboardReject;returnfalse;}",
            "if(isMemoryOp&&memoryBankId>=0&&IsBankPendingForVT(memoryBankId,virtualThreadId)){RecordBankPendingReject(memoryBankId);MshrScoreboardStalls++;SmtRejectionsCount++;rejectReason=TypedSlotRejectReason.BankPendingReject;returnfalse;}",
            "if(candidateisStoreMicroOp&&memoryBankId>=0&&HasBundleLocalOutstandingStoreBankPending(memoryBankId)){RecordBankPendingReject(memoryBankId);MshrScoreboardStalls++;SmtRejectionsCount++;rejectReason=TypedSlotRejectReason.BankPendingReject;returnfalse;}",
            "if(!HasHardwareMemoryBudget(candidate,memoryBankId)){TypedSlotHardwareBudgetRejects++;SmtRejectionsCount++;rejectReason=TypedSlotRejectReason.HardwareBudgetReject;returnfalse;}",
            "if(SpeculationBudgetEnabled&&isMemoryOp&&bundleOwnerVtId>=0&&virtualThreadId!=bundleOwnerVtId){if(_speculationBudget<=0){SpeculationBudgetExhaustionEvents++;SmtRejectionsCount++;rejectReason=TypedSlotRejectReason.SpeculationBudgetReject;returnfalse;}}"
        ];
        int last = -1;
        foreach (string check in orderedChecks)
        {
            int at = tokens.IndexOf(check, StringComparison.Ordinal);
            if (at <= last) throw new InvalidDataException("TryPassOuterCap guard order or side effects changed: " + check);
            last = at;
        }
        PropertyDeclarationSyntax suppressLsu = infrastructureRoot.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "SuppressLsu" && p.Type.ToString() == "bool");
        if (suppressLsu.AccessorList is null || !suppressLsu.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ||
            !suppressLsu.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)))
            throw new InvalidDataException("SuppressLsu sampled property contract changed.");
        var loadStoreTypes = loadStoreRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(c => c.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        if (!loadStoreTypes.Contains("LoadStoreMicroOp") || !loadStoreTypes.Contains("LoadMicroOp") || !loadStoreTypes.Contains("StoreMicroOp"))
            throw new InvalidDataException("Load/store subtype definitions changed; re-establish budget classification.");
        ClassDeclarationSyntax LoadStoreClass(string name) => loadStoreRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == name);
        bool DerivesFrom(ClassDeclarationSyntax type, string parent) => type.BaseList?.Types.Any(t => t.Type.ToString() == parent) == true;
        if (!DerivesFrom(LoadStoreClass("LoadMicroOp"), "LoadStoreMicroOp") || !DerivesFrom(LoadStoreClass("StoreMicroOp"), "LoadStoreMicroOp"))
            throw new InvalidDataException("Load/Store subtype inheritance changed.");
        ClassDeclarationSyntax atomicType = atomicRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "AtomicMicroOp");
        if (!atomicType.Modifiers.Any(SyntaxKind.SealedKeyword) || !DerivesFrom(atomicType, "LoadStoreMicroOp"))
            throw new InvalidDataException("Atomic LoadStore subtype inheritance changed.");
        MethodDeclarationSyntax BudgetMethod(string name, int parameterCount) => root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name && m.ParameterList.Parameters.Count == parameterCount);
        string hasHardwareBudget = Tokens(BudgetMethod("HasHardwareMemoryBudget", 2).Body ?? throw new InvalidDataException("Hardware-budget method body missing."));
        if (hasHardwareBudget != "{if(candidateisnotLoadStoreMicroOp){returntrue;}returnHasSharedHardwareMemoryBudget(memoryBankId)&&HasDirectionalHardwareMemoryBudget(candidate,memoryBankId);}")
            throw new InvalidDataException("LoadStore-vs-nonmemory hardware-budget gate changed.");
        string sharedBudget = Tokens(BudgetMethod("HasSharedHardwareMemoryBudget", 1).Body ?? throw new InvalidDataException("Shared budget method body missing."));
        if (sharedBudget != "{if(_remainingHardwareMemoryIssueBudget==0){returnfalse;}if((uint)memoryBankId>=16){returntrue;}returnGetConsumedHardwareMemoryBudget(memoryBankId)<_hardwareOccupancySnapshot.GetMemoryBudgetForBank(memoryBankId);}")
            throw new InvalidDataException("Shared budget or invalid-bank bypass contract changed.");
        string directionalBudget = Tokens(BudgetMethod("HasDirectionalHardwareMemoryBudget", 2).Body ?? throw new InvalidDataException("Directional budget method body missing."));
        if (!directionalBudget.Contains("if(candidateisLoadMicroOp){returnHasDirectionalHardwareBudget(_remainingHardwareLoadIssueBudget,_consumedHardwareLoadBudgetByBank,_hardwareOccupancySnapshot.GetReadBudgetForBank(memoryBankId),memoryBankId);}", StringComparison.Ordinal) ||
            !directionalBudget.Contains("if(candidateisStoreMicroOp){returnHasDirectionalHardwareBudget(_remainingHardwareStoreIssueBudget,_consumedHardwareStoreBudgetByBank,_hardwareOccupancySnapshot.GetWriteBudgetForBank(memoryBankId),memoryBankId);}", StringComparison.Ordinal) ||
            !directionalBudget.EndsWith("returntrue;}", StringComparison.Ordinal))
            throw new InvalidDataException("Load/Store/other-LoadStore directional budget contract changed.");
        string directionalHelper = Tokens(BudgetMethod("HasDirectionalHardwareBudget", 4).Body ?? throw new InvalidDataException("Directional helper body missing."));
        if (directionalHelper != "{if(remainingDirectionalIssueBudget==0){returnfalse;}if((uint)memoryBankId>=16){returntrue;}returnGetPackedConsumedHardwareBudget(consumedDirectionalBudgetByBank,memoryBankId)<bankBudget;}")
            throw new InvalidDataException("Directional budget or invalid-bank bypass contract changed.");
        MethodDeclarationSyntax capacityMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "HasProjectedOutstandingMemoryCapacity" && m.ParameterList.Parameters.Count == 2);
        const string capacityContract = "{if(!isMemoryOp){returntrue;}if((uint)virtualThreadId>=SMT_WAYS){returnfalse;}return_projectedOutstandingMemoryCountByVt[virtualThreadId]<_projectedOutstandingMemoryCapacityByVt[virtualThreadId];}";
        if (capacityMethod.Body is null || Tokens(capacityMethod.Body) != capacityContract)
            throw new InvalidDataException("Projected memory capacity predicate changed.");
        MethodDeclarationSyntax bankPending = scoreboardRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "IsBankPendingForVT" && m.ParameterList.Parameters.Count == 2);
        const string bankPendingContract = "{if((uint)virtualThreadId>=SMT_WAYS)returnfalse;for(inti=0;i<SCOREBOARD_SLOTS;i++){if(_smtScoreboard[virtualThreadId,i]!=-1&&_smtScoreboardBankId[virtualThreadId,i]==bankId&&(_smtScoreboardType[virtualThreadId,i]==ScoreboardEntryType.OutstandingLoad||_smtScoreboardType[virtualThreadId,i]==ScoreboardEntryType.OutstandingStore)){returntrue;}}returnfalse;}";
        if (Tokens(bankPending.Body ?? throw new InvalidDataException("Per-VT bank-pending body missing.")) != bankPendingContract)
            throw new InvalidDataException("Per-VT bank-pending scoreboard predicate changed.");
        MethodDeclarationSyntax consumeProjected = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "ConsumeProjectedMemoryIssueStateIfNeeded" && m.ParameterList.Parameters.Count == 2);
        string consumeTokens = Tokens(consumeProjected.Body ?? throw new InvalidDataException("Projected memory commit body missing."));
        string[] commitChecks =
        [
            "if(candidateisnotLoadStoreMicroOploadStoreCandidate){return;}",
            "IncrementProjectedOutstandingMemoryCount(_projectedOutstandingMemoryCountByVt,loadStoreCandidate.VirtualThreadId);"
        ];
        int commitAt = -1;
        foreach (string check in commitChecks)
        {
            int at = consumeTokens.IndexOf(check, StringComparison.Ordinal);
            if (at <= commitAt) throw new InvalidDataException("Projected memory commit type guard or update order changed: " + check);
            commitAt = at;
        }
        EnumDeclarationSyntax reasons = definitionsRoot.DescendantNodes().OfType<EnumDeclarationSyntax>()
            .Single(e => e.Identifier.ValueText == "TypedSlotRejectReason");
        int EnumValue(string name) => int.Parse(reasons.Members.Single(m => m.Identifier.ValueText == name)
            .EqualsValue!.Value.ToString(), System.Globalization.CultureInfo.InvariantCulture);
        if (EnumValue("None") != 0 || EnumValue("ScoreboardReject") != 5 || EnumValue("BankPendingReject") != 6 ||
            EnumValue("HardwareBudgetReject") != 7 || EnumValue("SpeculationBudgetReject") != 8)
            throw new InvalidDataException("Typed outer-gate reject encoding changed.");
        MethodDeclarationSyntax bankBudgetDecoder = occupancyRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "GetBudgetForBank" && m.ParameterList.Parameters.Count == 3);
        string bankDecoderTokens = Tokens(bankBudgetDecoder.Body ?? throw new InvalidDataException("Snapshot bank-budget decoder body missing."));
        if (bankDecoderTokens != "{if((uint)bankId>=16){return0;}ushortbankMask=(ushort)(1<<bankId);if((bankBudgetAtLeastTwoMask&bankMask)!=0){return2;}return(bankBudgetAtLeastOneMask&bankMask)!=0?(byte)1:(byte)0;}")
            throw new InvalidDataException("Snapshot 0/1/2 bank-budget mask decoder changed.");
        var pos = method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        string originDigest = Hash(Encoding.UTF8.GetBytes("smt-outer-gate/v8-sampled-memory-budget-predicate-atomic|" + AdmissionSha256 + "|" + SlotDefinitionsSha256 + "|" + ScoreboardSha256 + "|" + InfrastructureSha256 + "|" + LoadStoreSha256 + "|" + AtomicSha256 + "|" + OccupancySha256));
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", commit,
            "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/Admission/MicroOpScheduler.Admission.cs",
            "CircuitWeave.SmtOuterGate", "YAKSys_Hybrid_CPU.Core.MicroOpScheduler", "TryPassOuterCap",
            originDigest, HwSourceRole.Primary, pos.Line + 1, pos.Character + 1);
        var u1 = new HwBitVectorType(1); var u8 = new HwBitVectorType(8); var u32 = new HwBitVectorType(32);
        var u64 = new HwBitVectorType(64);
        var ports = new List<HwPortContract>
        {
            new("is_assist", HwPortDirection.Input, u1),
            new("is_memory", HwPortDirection.Input, u1),
            new("is_loadstore", HwPortDirection.Input, u1),
            new("is_load", HwPortDirection.Input, u1),
            new("is_store", HwPortDirection.Input, u1),
            new("bank_id", HwPortDirection.Input, u32),
            new("suppress_lsu", HwPortDirection.Input, u1),
            new("candidate_virtual_thread_id", HwPortDirection.Input, u32),
            new("owner_vt", HwPortDirection.Input, u32),
            new("speculation_budget_enabled", HwPortDirection.Input, u1),
            new("speculation_budget", HwPortDirection.Input, u32),
            new("bank_pending", HwPortDirection.Input, u1),
            new("bundle_store_bank_pending", HwPortDirection.Input, u1),
            new("outer_gate_condition_facts", HwPortDirection.Input, u32),
            new("remaining_memory_issue_budget", HwPortDirection.Input, u8),
            new("remaining_read_issue_budget", HwPortDirection.Input, u8),
            new("remaining_write_issue_budget", HwPortDirection.Input, u8),
            new("consumed_memory_budget_by_bank", HwPortDirection.Input, u32),
            new("consumed_read_budget_by_bank", HwPortDirection.Input, u32),
            new("consumed_write_budget_by_bank", HwPortDirection.Input, u32),
            new("memory_bank_budget_at_least_one_mask", HwPortDirection.Input, new HwBitVectorType(16)),
            new("memory_bank_budget_at_least_two_mask", HwPortDirection.Input, new HwBitVectorType(16)),
            new("read_bank_budget_at_least_one_mask", HwPortDirection.Input, new HwBitVectorType(16)),
            new("read_bank_budget_at_least_two_mask", HwPortDirection.Input, new HwBitVectorType(16)),
            new("write_bank_budget_at_least_one_mask", HwPortDirection.Input, new HwBitVectorType(16)),
            new("write_bank_budget_at_least_two_mask", HwPortDirection.Input, new HwBitVectorType(16)),
            new("allowed", HwPortDirection.Output, u1),
            new("reject_reason", HwPortDirection.Output, u8)
        };
        for (int vt = 0; vt < 4; vt++)
        {
            ports.Add(new($"projected_count_vt{vt}", HwPortDirection.Input, u8));
            ports.Add(new($"projected_capacity_vt{vt}", HwPortDirection.Input, u8));
        }
        var nodes = new List<HwNode>
        {
            new HwConstantNode("one1", u1, "1", origin), new HwConstantNode("zero1", u1, "0", origin),
            new HwConstantNode("zero8", u8, new string('0', 8), origin),
            new HwConstantNode("one8", u8, Convert.ToString(1, 2).PadLeft(8, '0'), origin),
            new HwConstantNode("two8", u8, Convert.ToString(2, 2).PadLeft(8, '0'), origin),
            new HwConstantNode("one32", u32, Convert.ToString(1, 2).PadLeft(32, '0'), origin),
            new HwConstantNode("zero32", u32, new string('0', 32), origin),
            new HwConstantNode("two32", u32, Convert.ToString(2, 2).PadLeft(32, '0'), origin),
            new HwConstantNode("three32", u32, Convert.ToString(3, 2).PadLeft(32, '0'), origin),
            new HwConstantNode("sixteen64", u64, Convert.ToString(16, 2).PadLeft(64, '0'), origin)
        };
        nodes.Add(new HwOperationNode("budget_bank64", "zext", ["bank_id"], u64, Origin: origin));
        nodes.Add(new HwOperationNode("budget_bank_valid", "ult64", ["budget_bank64", "sixteen64"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("budget_bank_invalid", "xor", ["budget_bank_valid", "one1"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("budget_bank_shift", "add", ["bank_id", "bank_id"], u32, Origin: origin));
        nodes.Add(new HwConstantNode("sign32", u32, "10000000000000000000000000000000", origin));
        nodes.Add(new HwOperationNode("spec_owner_sign", "and", ["owner_vt", "sign32"], u32, Origin: origin));
        nodes.Add(new HwOperationNode("spec_owner_sign_set", "eq", ["spec_owner_sign", "sign32"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("spec_owner_ge_zero", "xor", ["spec_owner_sign_set", "one1"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("spec_same_vt", "eq", ["candidate_virtual_thread_id", "owner_vt"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("spec_cross_vt", "xor", ["spec_same_vt", "one1"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("spec_budget_zero", "eq", ["speculation_budget", "zero32"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("spec_budget_sign", "and", ["speculation_budget", "sign32"], u32, Origin: origin));
        nodes.Add(new HwOperationNode("spec_budget_sign_set", "eq", ["spec_budget_sign", "sign32"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("spec_budget_nonpositive", "or", ["spec_budget_zero", "spec_budget_sign_set"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("spec_enabled_memory", "and", ["speculation_budget_enabled", "is_memory"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("spec_owner_cross", "and", ["spec_owner_ge_zero", "spec_cross_vt"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("spec_guard_context", "and", ["spec_enabled_memory", "spec_owner_cross"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("speculation_budget_blocked", "and", ["spec_guard_context", "spec_budget_nonpositive"], u1, Origin: origin));
        string BankBudget(string prefix, string oneMaskName, string twoMaskName)
        {
            nodes.Add(new HwOperationNode(prefix + "one_mask32", "zext", [oneMaskName], u32, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "two_mask32", "zext", [twoMaskName], u32, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "one_shifted", "lshr32_mask5", [prefix + "one_mask32", "bank_id"], u32, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "two_shifted", "lshr32_mask5", [prefix + "two_mask32", "bank_id"], u32, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "one_bit", "and", [prefix + "one_shifted", "one32"], u32, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "two_bit", "and", [prefix + "two_shifted", "one32"], u32, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "one_present", "eq", [prefix + "one_bit", "one32"], u1, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "two_present", "eq", [prefix + "two_bit", "one32"], u1, Origin: origin));
            nodes.Add(new HwMuxNode(prefix + "at_least_one", prefix + "one_present", "one8", "zero8", u8, origin));
            nodes.Add(new HwMuxNode(prefix + "budget", prefix + "two_present", "two8", prefix + "at_least_one", u8, origin));
            return prefix + "budget";
        }
        string memoryBankBudget = BankBudget("shared_bank_", "memory_bank_budget_at_least_one_mask", "memory_bank_budget_at_least_two_mask");
        string readBankBudget = BankBudget("read_bank_", "read_bank_budget_at_least_one_mask", "read_bank_budget_at_least_two_mask");
        string writeBankBudget = BankBudget("write_bank_", "write_bank_budget_at_least_one_mask", "write_bank_budget_at_least_two_mask");
        void PackedCell(string prefix, string packedName, string limitName, string resultName)
        {
            nodes.Add(new HwOperationNode(prefix + "shifted", "lshr32_mask5", [packedName, "budget_bank_shift"], u32, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "cell", "and", [prefix + "shifted", "three32"], u32, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "cell64", "zext", [prefix + "cell"], u64, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "limit64", "zext", [limitName], u64, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "below", "ult64", [prefix + "cell64", prefix + "limit64"], u1, Origin: origin));
            nodes.Add(new HwOperationNode(resultName, "or", ["budget_bank_invalid", prefix + "below"], u1, Origin: origin));
        }
        void Positive(string prefix, string remaining, string result)
        {
            nodes.Add(new HwOperationNode(prefix + "zero", "eq", [remaining, "zero8"], u1, Origin: origin));
            nodes.Add(new HwOperationNode(result, "xor", [prefix + "zero", "one1"], u1, Origin: origin));
        }
        PackedCell("shared_bank_budget_", "consumed_memory_budget_by_bank", memoryBankBudget, "shared_bank_budget_ok");
        PackedCell("read_bank_budget_", "consumed_read_budget_by_bank", readBankBudget, "read_bank_budget_ok");
        PackedCell("write_bank_budget_", "consumed_write_budget_by_bank", writeBankBudget, "write_bank_budget_ok");
        Positive("shared_remaining_", "remaining_memory_issue_budget", "shared_remaining_ok");
        Positive("read_remaining_", "remaining_read_issue_budget", "read_remaining_ok");
        Positive("write_remaining_", "remaining_write_issue_budget", "write_remaining_ok");
        nodes.Add(new HwOperationNode("shared_budget_ok", "and", ["shared_remaining_ok", "shared_bank_budget_ok"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("read_budget_ok", "and", ["read_remaining_ok", "read_bank_budget_ok"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("write_budget_ok", "and", ["write_remaining_ok", "write_bank_budget_ok"], u1, Origin: origin));
        nodes.Add(new HwMuxNode("directional_budget_after_store", "is_store", "write_budget_ok", "one1", u1, origin));
        nodes.Add(new HwMuxNode("directional_budget_ok", "is_load", "read_budget_ok", "directional_budget_after_store", u1, origin));
        nodes.Add(new HwOperationNode("loadstore_budget_ok", "and", ["shared_budget_ok", "directional_budget_ok"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("loadstore_budget_not_ok", "xor", ["loadstore_budget_ok", "one1"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("hardware_budget_blocked", "and", ["is_loadstore", "loadstore_budget_not_ok"], u1, Origin: origin));
        string selectedCount = "zero8", selectedCapacity = "zero8", validVt = "zero1";
        for (int vt = 0; vt < 4; vt++)
        {
            string q = "capacity_vt" + vt + "_";
            nodes.Add(new HwConstantNode(q + "id", u32, Convert.ToString(vt, 2).PadLeft(32, '0'), origin));
            nodes.Add(new HwOperationNode(q + "match", "eq", ["candidate_virtual_thread_id", q + "id"], u1, Origin: origin));
            nodes.Add(new HwOperationNode(q + "valid", "or", [validVt, q + "match"], u1, Origin: origin));
            string count = q + "selected_count", capacity = q + "selected_capacity";
            nodes.Add(new HwMuxNode(count, q + "match", $"projected_count_vt{vt}", selectedCount, u8, origin));
            nodes.Add(new HwMuxNode(capacity, q + "match", $"projected_capacity_vt{vt}", selectedCapacity, u8, origin));
            selectedCount = count; selectedCapacity = capacity; validVt = q + "valid";
        }
        nodes.Add(new HwOperationNode("capacity_count64", "zext", [selectedCount], u64, Origin: origin));
        nodes.Add(new HwOperationNode("capacity_limit64", "zext", [selectedCapacity], u64, Origin: origin));
        nodes.Add(new HwOperationNode("capacity_count_below", "ult64", ["capacity_count64", "capacity_limit64"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("capacity_not_below", "xor", ["capacity_count_below", "one1"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("capacity_invalid_vt", "xor", [validVt, "one1"], u1, Origin: origin));
        nodes.Add(new HwOperationNode("capacity_reject", "or", ["capacity_invalid_vt", "capacity_not_below"], u1, Origin: origin));
        nodes.Add(new HwMuxNode("capacity_condition1", "is_memory", "capacity_reject", "zero1", u1, origin));
        string allowed = "one1", reason = "zero8";
        for (int i = 5; i >= 0; i--)
        {
            string p = "condition" + i + "_"; string shiftValue = Convert.ToString(i, 2).PadLeft(32, '0');
            nodes.Add(new HwConstantNode(p + "shift", u32, shiftValue, origin));
            nodes.Add(new HwConstantNode(p + "reason", u8, Convert.ToString(i < 2 ? 5 : i < 4 ? 6 : i == 4 ? 7 : 8, 2).PadLeft(8, '0'), origin));
            nodes.Add(new HwOperationNode(p + "shifted", "lshr32_mask5", ["outer_gate_condition_facts", p + "shift"], u32, Origin: origin));
            nodes.Add(new HwOperationNode(p + "masked", "and", [p + "shifted", "one32"], u32, Origin: origin));
            nodes.Add(new HwOperationNode(p + "fact_hit", "eq", [p + "masked", "one32"], u1, Origin: origin));
            if (i == 0)
                nodes.Add(new HwOperationNode(p + "hit", "and", ["suppress_lsu", "is_memory"], u1, Origin: origin));
            else if (i == 1)
                nodes.Add(new HwMuxNode(p + "hit", "is_memory", "capacity_condition1", "zero1", u1, origin));
            else if (i == 2)
                nodes.Add(new HwMuxNode(p + "hit", "is_memory", "bank_pending", "zero1", u1, origin));
            else if (i == 3)
                nodes.Add(new HwOperationNode(p + "hit", "or", ["bundle_store_bank_pending", "zero1"], u1, Origin: origin));
            else if (i == 4)
                nodes.Add(new HwOperationNode(p + "hit", "or", ["hardware_budget_blocked", "zero1"], u1, Origin: origin));
            else
                nodes.Add(new HwOperationNode(p + "hit", "or", ["speculation_budget_blocked", "zero1"], u1, Origin: origin));
            string nextAllowed = p + "allowed"; string nextReason = p + "selected_reason";
            nodes.Add(new HwMuxNode(nextAllowed, p + "hit", "zero1", allowed, u1, origin));
            nodes.Add(new HwMuxNode(nextReason, p + "hit", p + "reason", reason, u8, origin));
            allowed = nextAllowed; reason = nextReason;
        }
        nodes.Add(new HwMuxNode("assist_bypass_allowed", "is_assist", "one1", allowed, u1, origin));
        nodes.Add(new HwMuxNode("assist_bypass_reason", "is_assist", "zero8", reason, u8, origin));
        nodes.Add(new HwOutputDriveNode("drive_allowed", "allowed", "assist_bypass_allowed", origin));
        nodes.Add(new HwOutputDriveNode("drive_reason", "reject_reason", "assist_bypass_reason", origin));
        var module = new HwModule("smt_outer_gate_projection", "smt_outer_gate_projection", ports, nodes, [],
            new HwHardwareContract("scheduler.smt-outer-gate.unqualified.v2", HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var result = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", module.Id, [module], toolchain, []));
        return result.Validated ?? throw new InvalidDataException("SMT outer-gate HWIR refused: " + string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    private static SyntaxNode Parse(byte[] source)
    {
        var root = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(source)).GetRoot();
        if (root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) throw new InvalidDataException("SMT outer-gate source syntax is invalid.");
        return root;
    }
    private static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
