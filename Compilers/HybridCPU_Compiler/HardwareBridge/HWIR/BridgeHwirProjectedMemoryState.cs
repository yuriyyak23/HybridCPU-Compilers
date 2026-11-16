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
/// Builds source-observed initial projected-memory count/capacity and A3 budget state
/// from sampled snapshot values, a 4x8 scoreboard, and the original eight-slot bundle.
/// Typed bundle facts remain explicit inputs; no candidate-object identity is claimed.
/// </summary>
public static class BridgeHwirProjectedMemoryState
{
    public static HwValidatedDesign Compile(byte[] admission, byte[] scoreboard, byte[] scheduler,
        byte[] occupancy, string commit, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(scoreboard);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(occupancy);
        static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        static SyntaxNode Parse(byte[] b)
        {
            var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(b));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Invalid projected-memory source syntax.");
            return tree.GetRoot();
        }
        static string Tokens(SyntaxNode n) => string.Concat(n.DescendantTokens().Select(t => t.Text));
        static ClassDeclarationSyntax Owner(SyntaxNode n) => n.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "MicroOpScheduler" &&
                c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core");

        var admissionOwner = Owner(Parse(admission));
        var scoreboardOwner = Owner(Parse(scoreboard));
        var schedulerOwner = Owner(Parse(scheduler));
        var occupancyRoot = Parse(occupancy);
        var occupancySnapshot = occupancyRoot.DescendantNodes().OfType<StructDeclarationSyntax>()
            .Single(s => s.Identifier.ValueText == "HardwareOccupancySnapshot128");
        foreach (string property in new[] { "public byte MemoryIssueBudget { get; }", "public byte ReadIssueBudget { get; }", "public byte WriteIssueBudget { get; }" })
            if (!occupancySnapshot.Members.OfType<PropertyDeclarationSyntax>().Any(p => Tokens(p) == Tokens(SyntaxFactory.ParseMemberDeclaration(property)!)))
                throw new InvalidDataException("Hardware occupancy snapshot budget field changed: " + property);
        var initialize = admissionOwner.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "InitializeProjectedOutstandingMemoryState");
        var prepare = admissionOwner.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "PrepareProjectedMemoryIssuePass");
        var capacity = admissionOwner.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "HasProjectedOutstandingMemoryCapacity");
        var outstanding = scoreboardOwner.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "GetOutstandingMemoryCount");
        var free = scoreboardOwner.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "GetFreeSmtScoreboardSlotCount");
        var consume = admissionOwner.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "ConsumeProjectedMemoryIssueStateIfNeeded");
        var classAdmission = admissionOwner.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "TryClassAdmission");
        var increment = admissionOwner.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "IncrementProjectedOutstandingMemoryCount");
        var saturation = admissionOwner.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "SaturateScoreboardCount");

        string initTokens = Tokens(initialize.Body ?? throw new InvalidDataException("Initialization body missing."));
        string[] initializationFacts =
        [
            "GetOutstandingMemoryCount(vt)", "GetFreeSmtScoreboardSlotCount(vt)",
            "SaturateScoreboardCount(liveOutstandingMemoryCount)",
            "SaturateScoreboardCount(liveOutstandingMemoryCount+liveFreeScoreboardSlots)"
        ];
        int at = -1;
        foreach (string fact in initializationFacts)
        {
            int next = initTokens.IndexOf(fact, StringComparison.Ordinal);
            if (next <= at) throw new InvalidDataException("Projected-state initialization order changed: " + fact);
            at = next;
        }
        string prepareTokens = Tokens(prepare.Body ?? throw new InvalidDataException("Projected pass preparation body missing."));
        string[] snapshotBudgetAssignments =
        [
            "_remainingHardwareMemoryIssueBudget=_hardwareOccupancySnapshot.MemoryIssueBudget;",
            "_consumedHardwareMemoryBudgetByBank=0;",
            "_remainingHardwareLoadIssueBudget=_hardwareOccupancySnapshot.ReadIssueBudget;",
            "_remainingHardwareStoreIssueBudget=_hardwareOccupancySnapshot.WriteIssueBudget;",
            "_consumedHardwareLoadBudgetByBank=0;",
            "_consumedHardwareStoreBudgetByBank=0;"
        ];
        int snapshotBudgetAt = -1;
        foreach (string fact in snapshotBudgetAssignments)
        {
            int next = prepareTokens.IndexOf(fact, StringComparison.Ordinal);
            if (next <= snapshotBudgetAt) throw new InvalidDataException("Snapshot budget initialization order changed: " + fact);
            snapshotBudgetAt = next;
        }
        string[] prepareOrder =
        [
            "InitializeProjectedOutstandingMemoryState();",
            "for(inti=0;i<bundle.Count;i++){ConsumeProjectedMemoryIssueStateIfNeeded(bundle[i]);}"
        ];
        at = -1;
        foreach (string fact in prepareOrder)
        {
            int next = prepareTokens.IndexOf(fact, StringComparison.Ordinal);
            if (next <= at) throw new InvalidDataException("Original-bundle projected-state preparation order changed: " + fact);
            at = next;
        }
        string outstandingTokens = Tokens(outstanding.Body ?? throw new InvalidDataException("Outstanding scan body missing."));
        if (!outstandingTokens.Contains("ScoreboardEntryType.OutstandingLoad", StringComparison.Ordinal) ||
            !outstandingTokens.Contains("ScoreboardEntryType.OutstandingStore", StringComparison.Ordinal))
            throw new InvalidDataException("Outstanding-memory type scan changed.");
        string freeTokens = Tokens(free.Body ?? throw new InvalidDataException("Free-slot scan body missing."));
        if (!freeTokens.Contains("_smtScoreboard[virtualThreadId,i]==-1", StringComparison.Ordinal))
            throw new InvalidDataException("Scoreboard allocator free-slot predicate changed.");
        string consumeTokens = Tokens(consume.Body ?? throw new InvalidDataException("Bundle issue-state consume body missing."));
        if (!consumeTokens.Contains("candidateisnotLoadStoreMicroOploadStoreCandidate", StringComparison.Ordinal) ||
            !consumeTokens.Contains("elseif(loadStoreCandidateisStoreMicroOp)", StringComparison.Ordinal) ||
            !consumeTokens.Contains("MarkBundleLocalOutstandingStoreBank(bankId);", StringComparison.Ordinal))
            throw new InvalidDataException("Bundle-local Store mask update changed.");
        foreach (string fact in new[] { "_remainingHardwareMemoryIssueBudget--;", "IncrementConsumedHardwareMemoryBudget(bankId);", "loadStoreCandidateisLoadMicroOp", "_remainingHardwareLoadIssueBudget--;", "IncrementConsumedHardwareLoadBudget(bankId);", "_remainingHardwareStoreIssueBudget--;", "IncrementConsumedHardwareStoreBudget(bankId);" })
            if (!consumeTokens.Contains(fact, StringComparison.Ordinal)) throw new InvalidDataException("Original-bundle budget update changed: " + fact);
        var prepareStores = admissionOwner.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "PrepareProjectedMemoryIssuePass");
        string prepareStoreTokens = Tokens(prepareStores.Body ?? throw new InvalidDataException("Bundle issue-state preparation body missing."));
        string[] prepareStoreOrder = ["_bundleLocalOutstandingStoreBankMask=0;", "for(inti=0;i<bundle.Count;i++){ConsumeProjectedMemoryIssueStateIfNeeded(bundle[i]);}"];
        int prepareStoreAt = -1;
        foreach (string fact in prepareStoreOrder)
        {
            int next = prepareStoreTokens.IndexOf(fact, StringComparison.Ordinal);
            if (next <= prepareStoreAt) throw new InvalidDataException("Bundle-local Store mask preparation order changed: " + fact);
            prepareStoreAt = next;
        }
        var hasBundleStore = admissionOwner.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "HasBundleLocalOutstandingStoreBankPending");
        if (Tokens(hasBundleStore.Body ?? throw new InvalidDataException("Bundle-local Store predicate body missing.")) !=
            "{return(uint)bankId<16&&(_bundleLocalOutstandingStoreBankMask&(1<<bankId))!=0;}")
            throw new InvalidDataException("Bundle-local Store bank membership predicate changed.");
        if (!consumeTokens.Contains("IncrementProjectedOutstandingMemoryCount(_projectedOutstandingMemoryCountByVt,loadStoreCandidate.VirtualThreadId)", StringComparison.Ordinal))
            throw new InvalidDataException("Original-bundle typed consume changed.");
        string incrementTokens = Tokens(increment.Body ?? throw new InvalidDataException("Increment body missing."));
        if (!incrementTokens.Contains("projectedOutstandingCounts[virtualThreadId]<SCOREBOARD_SLOTS", StringComparison.Ordinal) ||
            !incrementTokens.Contains("projectedOutstandingCounts[virtualThreadId]++", StringComparison.Ordinal))
            throw new InvalidDataException("Projected count increment/saturation changed.");
        if (!Tokens(saturation.Body ?? throw new InvalidDataException("Saturation body missing."))
            .Contains("value>=SCOREBOARD_SLOTS", StringComparison.Ordinal))
            throw new InvalidDataException("Scoreboard count saturation changed.");
        if (!Tokens(capacity.Body ?? throw new InvalidDataException("Capacity body missing."))
            .Contains("_projectedOutstandingMemoryCountByVt[virtualThreadId]<_projectedOutstandingMemoryCapacityByVt[virtualThreadId]", StringComparison.Ordinal))
            throw new InvalidDataException("Projected capacity predicate changed.");
        if (!Tokens(classAdmission.Body ?? throw new InvalidDataException("Class admission body missing."))
            .Contains("intcandidateBankId=projectedBankId??(candidateisLoadStoreMicroOpls?ls.MemoryBankId:-1);", StringComparison.Ordinal))
            throw new InvalidDataException("A3 candidate memory-bank identity fallback changed.");
        foreach (string text in new[] { "private const int SMT_WAYS = 4;", "private const int SCOREBOARD_SLOTS = 8;" })
            if (!schedulerOwner.Members.OfType<FieldDeclarationSyntax>().Any(f => Tokens(f) == Tokens(SyntaxFactory.ParseMemberDeclaration(text)!)))
                throw new InvalidDataException("Projected scoreboard dimensions changed.");
        var typeEnum = Parse(scheduler).DescendantNodes().OfType<EnumDeclarationSyntax>()
            .Single(e => e.Identifier.ValueText == "ScoreboardEntryType");
        int Value(string name) => int.Parse(typeEnum.Members.Single(m => m.Identifier.ValueText == name).EqualsValue!.Value.ToString(), System.Globalization.CultureInfo.InvariantCulture);
        if (Value("Free") != 0 || Value("OutstandingLoad") != 2 || Value("OutstandingStore") != 3)
            throw new InvalidDataException("ScoreboardEntryType values changed.");

        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", commit,
            "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/Admission/MicroOpScheduler.Admission.cs",
            "CircuitWeave.ProjectedMemoryState", "YAKSys_Hybrid_CPU.Core.MicroOpScheduler",
            "InitializeProjectedOutstandingMemoryState",
            Hash(Encoding.UTF8.GetBytes("projected-memory-state/v2-snapshot-bundle-budget-init|" + Hash(admission) + "|" + Hash(scoreboard) + "|" + Hash(scheduler) + "|" + Hash(occupancy))),
            HwSourceRole.Primary, initialize.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            initialize.Identifier.GetLocation().GetLineSpan().StartLinePosition.Character + 1);
        var u1 = new HwBitVectorType(1); var u2 = new HwBitVectorType(2); var u8 = new HwBitVectorType(8); var u32 = new HwBitVectorType(32); var u64 = new HwBitVectorType(64);
        var ports = new List<HwPortContract>(); var nodes = new List<HwNode>();
        nodes.Add(new HwConstantNode("zero1", u1, "0", origin));
        nodes.Add(new HwConstantNode("one1", u1, "1", origin));
        nodes.Add(new HwConstantNode("zero8", u8, "00000000", origin));
        nodes.Add(new HwConstantNode("one8", u8, "00000001", origin));
        nodes.Add(new HwConstantNode("eight8", u8, "00001000", origin));
        nodes.Add(new HwConstantNode("eight64", u64, Convert.ToString(8, 2).PadLeft(64, '0'), origin));
        nodes.Add(new HwConstantNode("minus_one32", u32, new string('1', 32), origin));
        nodes.Add(new HwConstantNode("zero32", u32, new string('0', 32), origin));
        nodes.Add(new HwConstantNode("one32", u32, Convert.ToString(1, 2).PadLeft(32, '0'), origin));
        nodes.Add(new HwConstantNode("three32", u32, Convert.ToString(3, 2).PadLeft(32, '0'), origin));
        nodes.Add(new HwConstantNode("three64", u64, Convert.ToString(3, 2).PadLeft(64, '0'), origin));
        nodes.Add(new HwConstantNode("bank_sign_shift", u32, Convert.ToString(31, 2).PadLeft(32, '0'), origin));
        nodes.Add(new HwConstantNode("load_type8", u8, "00000010", origin));
        nodes.Add(new HwConstantNode("store_type8", u8, "00000011", origin));
        nodes.Add(new HwConstantNode("sixteen64", u64, Convert.ToString(16, 2).PadLeft(64, '0'), origin));
        nodes.Add(new HwConstantNode("budget_all_ones32", u32, new string('1', 32), origin));
        ports.Add(new HwPortContract("snapshot_memory_issue_budget", HwPortDirection.Input, u8));
        ports.Add(new HwPortContract("snapshot_read_issue_budget", HwPortDirection.Input, u8));
        ports.Add(new HwPortContract("snapshot_write_issue_budget", HwPortDirection.Input, u8));
        for (int vt = 0; vt < 4; vt++)
        {
            string outstandingSum = "zero8", freeSum = "zero8";
            for (int slot = 0; slot < 8; slot++)
            {
                string p = $"score_vt{vt}_slot{slot}_";
                ports.Add(new HwPortContract(p + "value", HwPortDirection.Input, u32));
                ports.Add(new HwPortContract(p + "type", HwPortDirection.Input, u8));
                ports.Add(new HwPortContract(p + "bank_id", HwPortDirection.Input, u32));
                nodes.Add(new HwOperationNode(p + "is_load", "eq", [p + "type", "load_type8"], u1, Origin: origin));
                nodes.Add(new HwOperationNode(p + "is_store", "eq", [p + "type", "store_type8"], u1, Origin: origin));
                nodes.Add(new HwOperationNode(p + "is_memory", "or", [p + "is_load", p + "is_store"], u1, Origin: origin));
                nodes.Add(new HwMuxNode(p + "outstanding_inc", p + "is_memory", "one8", "zero8", u8, origin));
                nodes.Add(new HwOperationNode(p + "outstanding_sum", "add", [outstandingSum, p + "outstanding_inc"], u8, Origin: origin));
                outstandingSum = p + "outstanding_sum";
                nodes.Add(new HwOperationNode(p + "is_free", "eq", [p + "value", "minus_one32"], u1, Origin: origin));
                nodes.Add(new HwMuxNode(p + "free_inc", p + "is_free", "one8", "zero8", u8, origin));
                nodes.Add(new HwOperationNode(p + "free_sum", "add", [freeSum, p + "free_inc"], u8, Origin: origin));
                freeSum = p + "free_sum";
            }
            string capSum = $"vt{vt}_capacity_sum";
            nodes.Add(new HwOperationNode(capSum, "add", [outstandingSum, freeSum], u8, Origin: origin));
            string outstandingSaturated = $"vt{vt}_outstanding_saturated";
            nodes.Add(new HwOperationNode($"vt{vt}_outstanding_sum64", "zext", [outstandingSum], u64, Origin: origin));
            nodes.Add(new HwOperationNode($"vt{vt}_outstanding_below_eight", "ult64", [$"vt{vt}_outstanding_sum64", "eight64"], u1, Origin: origin));
            nodes.Add(new HwMuxNode(outstandingSaturated, $"vt{vt}_outstanding_below_eight", outstandingSum, "eight8", u8, origin));
            string capacitySaturated = $"vt{vt}_capacity_saturated";
            nodes.Add(new HwOperationNode($"vt{vt}_capacity_sum64", "zext", [capSum], u64, Origin: origin));
            nodes.Add(new HwOperationNode($"vt{vt}_capacity_below_eight", "ult64", [$"vt{vt}_capacity_sum64", "eight64"], u1, Origin: origin));
            nodes.Add(new HwMuxNode(capacitySaturated, $"vt{vt}_capacity_below_eight", capSum, "eight8", u8, origin));
            string current = outstandingSaturated;
            for (int slot = 0; slot < 8; slot++)
            {
                string p = $"bundle_slot{slot}_";
                if (vt == 0)
                {
                    ports.Add(new HwPortContract(p + "present", HwPortDirection.Input, u1));
                    ports.Add(new HwPortContract(p + "is_loadstore", HwPortDirection.Input, u1));
                    ports.Add(new HwPortContract(p + "is_load", HwPortDirection.Input, u1));
                    ports.Add(new HwPortContract(p + "is_store", HwPortDirection.Input, u1));
                    ports.Add(new HwPortContract(p + "bank_id", HwPortDirection.Input, u32));
                    ports.Add(new HwPortContract(p + "vt", HwPortDirection.Input, u32));
                }
                nodes.Add(new HwConstantNode(p + "vt" + vt + "_id", u32, Convert.ToString(vt, 2).PadLeft(32, '0'), origin));
                nodes.Add(new HwOperationNode(p + "vt" + vt + "_match", "eq", [p + "vt", p + "vt" + vt + "_id"], u1, Origin: origin));
                nodes.Add(new HwOperationNode(p + "active_base_vt" + vt, "and", [p + "present", p + "is_loadstore"], u1, Origin: origin));
                nodes.Add(new HwOperationNode(p + "active" + vt, "and", [p + "active_base_vt" + vt, p + "vt" + vt + "_match"], u1, Origin: origin));
                nodes.Add(new HwOperationNode(p + "inc" + vt, "add", [current, "one8"], u8, Origin: origin));
                nodes.Add(new HwOperationNode(p + "current64_" + vt, "zext", [current], u64, Origin: origin));
                nodes.Add(new HwOperationNode(p + "below8_" + vt, "ult64", [p + "current64_" + vt, "eight64"], u1, Origin: origin));
                nodes.Add(new HwMuxNode(p + "saturated_inc" + vt, p + "below8_" + vt, p + "inc" + vt, "eight8", u8, origin));
                string next = p + "count_vt" + vt;
                nodes.Add(new HwMuxNode(next, p + "active" + vt, p + "saturated_inc" + vt, current, u8, origin));
                current = next;
            }
            nodes.Add(new HwOutputDriveNode($"drive_projected_count_vt{vt}", $"projected_count_vt{vt}", current, origin));
            nodes.Add(new HwOutputDriveNode($"drive_projected_capacity_vt{vt}", $"projected_capacity_vt{vt}", capacitySaturated, origin));
            ports.Add(new HwPortContract($"projected_count_vt{vt}", HwPortDirection.Output, u8));
            ports.Add(new HwPortContract($"projected_capacity_vt{vt}", HwPortDirection.Output, u8));
        }
        string[] budgetStateNames = ["remaining_memory_issue_budget", "remaining_read_issue_budget", "remaining_write_issue_budget"];
        string[] packedStateNames = ["consumed_memory_budget_by_bank", "consumed_read_budget_by_bank", "consumed_write_budget_by_bank"];
        string[] initialState = ["snapshot_memory_issue_budget", "snapshot_read_issue_budget", "snapshot_write_issue_budget", "zero32", "zero32", "zero32"];
        for (int i = 0; i < 6; i++)
            ports.Add(new HwPortContract("initial_" + (i < 3 ? budgetStateNames[i] : packedStateNames[i - 3]), HwPortDirection.Output, i < 3 ? u8 : u32));
        string[] currentBudget = initialState.Take(3).ToArray();
        string[] currentPacked = initialState.Skip(3).ToArray();
        for (int slot = 0; slot < 8; slot++)
        {
            string p = $"bundle_slot{slot}_";
            string ls = p + "budget_loadstore", load = p + "budget_load", store = p + "budget_store";
            nodes.Add(new HwOperationNode(ls, "and", [p + "present", p + "is_loadstore"], u1, Origin: origin));
            nodes.Add(new HwOperationNode(load, "and", [ls, p + "is_load"], u1, Origin: origin));
            nodes.Add(new HwOperationNode(store, "and", [ls, p + "is_store"], u1, Origin: origin));
            for (int stateIndex = 0; stateIndex < 3; stateIndex++)
            {
                string prefix = p + "budget_state" + stateIndex + "_";
                string enable = stateIndex == 0 ? ls : stateIndex == 1 ? load : store;
                string current = currentBudget[stateIndex];
                nodes.Add(new HwOperationNode(prefix + "zero", "eq", [current, "zero8"], u1, Origin: origin));
                nodes.Add(new HwOperationNode(prefix + "positive", "xor", [prefix + "zero", "one1"], u1, Origin: origin));
                nodes.Add(new HwOperationNode(prefix + "decrement", "and", [enable, prefix + "positive"], u1, Origin: origin));
                nodes.Add(new HwOperationNode(prefix + "value", "sub", [current, "one8"], u8, Origin: origin));
                string next = prefix + "next";
                nodes.Add(new HwMuxNode(next, prefix + "decrement", prefix + "value", current, u8, origin));
                currentBudget[stateIndex] = next;
            }
            nodes.Add(new HwOperationNode(p + "budget_bank64", "zext", [p + "bank_id"], u64, Origin: origin));
            nodes.Add(new HwOperationNode(p + "budget_bank_valid", "ult64", [p + "budget_bank64", "sixteen64"], u1, Origin: origin));
            nodes.Add(new HwOperationNode(p + "budget_bank_shift", "add", [p + "bank_id", p + "bank_id"], u32, Origin: origin));
            for (int stateIndex = 0; stateIndex < 3; stateIndex++)
            {
                string prefix = p + "packed_state" + stateIndex + "_";
                string enable = stateIndex == 0 ? ls : stateIndex == 1 ? load : store;
                string current = currentPacked[stateIndex];
                nodes.Add(new HwOperationNode(prefix + "shifted", "lshr32_mask5", [current, p + "budget_bank_shift"], u32, Origin: origin));
                nodes.Add(new HwOperationNode(prefix + "cell", "and", [prefix + "shifted", "three32"], u32, Origin: origin));
                nodes.Add(new HwOperationNode(prefix + "cell64", "zext", [prefix + "cell"], u64, Origin: origin));
                nodes.Add(new HwOperationNode(prefix + "below_three", "ult64", [prefix + "cell64", "three64"], u1, Origin: origin));
                nodes.Add(new HwOperationNode(prefix + "incremented", "add", [prefix + "cell", "one32"], u32, Origin: origin));
                nodes.Add(new HwMuxNode(prefix + "saturated", prefix + "below_three", prefix + "incremented", prefix + "cell", u32, origin));
                nodes.Add(new HwOperationNode(prefix + "new_field", "shl32_mask5", [prefix + "saturated", p + "budget_bank_shift"], u32, Origin: origin));
                nodes.Add(new HwOperationNode(prefix + "field", "shl32_mask5", ["three32", p + "budget_bank_shift"], u32, Origin: origin));
                nodes.Add(new HwOperationNode(prefix + "field_clear_mask", "xor", [prefix + "field", "budget_all_ones32"], u32, Origin: origin));
                nodes.Add(new HwOperationNode(prefix + "cleared", "and", [current, prefix + "field_clear_mask"], u32, Origin: origin));
                nodes.Add(new HwOperationNode(prefix + "inserted", "or", [prefix + "cleared", prefix + "new_field"], u32, Origin: origin));
                nodes.Add(new HwOperationNode(prefix + "write_enable", "and", [enable, p + "budget_bank_valid"], u1, Origin: origin));
                string next = prefix + "next";
                nodes.Add(new HwMuxNode(next, prefix + "write_enable", prefix + "inserted", current, u32, origin));
                currentPacked[stateIndex] = next;
            }
        }
        for (int i = 0; i < 3; i++) nodes.Add(new HwOutputDriveNode("drive_initial_" + budgetStateNames[i], "initial_" + budgetStateNames[i], currentBudget[i], origin));
        for (int i = 0; i < 3; i++) nodes.Add(new HwOutputDriveNode("drive_initial_" + packedStateNames[i], "initial_" + packedStateNames[i], currentPacked[i], origin));
        for (int vt = 0; vt < 4; vt++)
        {
            string cp = $"vt{vt}_";
            ports.Add(new HwPortContract(cp + "bank_id", HwPortDirection.Input, u32));
            ports.Add(new HwPortContract(cp + "is_loadstore", HwPortDirection.Input, u1));
            ports.Add(new HwPortContract(cp + "is_memory", HwPortDirection.Input, u1));
            ports.Add(new HwPortContract(cp + "bank_pending", HwPortDirection.Output, u1));
            ports.Add(new HwPortContract(cp + "is_store", HwPortDirection.Input, u1));
            ports.Add(new HwPortContract(cp + "bundle_store_bank_pending", HwPortDirection.Output, u1));
            nodes.Add(new HwMuxNode(cp + "effective_bank_id", cp + "is_loadstore", cp + "bank_id", "minus_one32", u32, origin));
            nodes.Add(new HwOperationNode(cp + "bank_sign", "lshr32_mask5", [cp + "effective_bank_id", "bank_sign_shift"], u32, Origin: origin));
            nodes.Add(new HwOperationNode(cp + "bank_sign_bit", "and", [cp + "bank_sign", "one32"], u32, Origin: origin));
            nodes.Add(new HwOperationNode(cp + "bank_nonnegative", "eq", [cp + "bank_sign_bit", "zero32"], u1, Origin: origin));
            string any = "zero1";
            for (int slot = 0; slot < 8; slot++)
            {
                string sp = $"score_vt{vt}_slot{slot}_";
                string q = cp + $"slot{slot}_";
                nodes.Add(new HwOperationNode(q + "bank_match", "eq", [cp + "effective_bank_id", sp + "bank_id"], u1, Origin: origin));
                nodes.Add(new HwOperationNode(q + "occupied", "xor", [sp + "is_free", "one1"], u1, Origin: origin));
                nodes.Add(new HwOperationNode(q + "outstanding", "and", [sp + "is_memory", q + "occupied"], u1, Origin: origin));
                nodes.Add(new HwOperationNode(q + "bank_pending", "and", [q + "outstanding", q + "bank_match"], u1, Origin: origin));
                string next = cp + $"or_slot{slot}";
                nodes.Add(new HwOperationNode(next, "or", [any, q + "bank_pending"], u1, Origin: origin));
                any = next;
            }
            nodes.Add(new HwOperationNode(cp + "memory_nonnegative", "and", [cp + "is_memory", cp + "bank_nonnegative"], u1, Origin: origin));
            nodes.Add(new HwOperationNode(cp + "pending_result", "and", [any, cp + "memory_nonnegative"], u1, Origin: origin));
            nodes.Add(new HwOutputDriveNode(cp + "drive_bank_pending", cp + "bank_pending", cp + "pending_result", origin));
            nodes.Add(new HwOperationNode(cp + "candidate_bank64", "zext", [cp + "effective_bank_id"], u64, Origin: origin));
            nodes.Add(new HwOperationNode(cp + "candidate_bank_below16", "ult64", [cp + "candidate_bank64", "sixteen64"], u1, Origin: origin));
            nodes.Add(new HwOperationNode(cp + "candidate_store_nonnegative", "and", [cp + "is_store", cp + "bank_nonnegative"], u1, Origin: origin));
            nodes.Add(new HwOperationNode(cp + "candidate_store_bank_valid", "and", [cp + "candidate_store_nonnegative", cp + "candidate_bank_below16"], u1, Origin: origin));
            string storePending = "zero1";
            for (int slot = 0; slot < 8; slot++)
            {
                string bp = $"bundle_slot{slot}_";
                string q = cp + $"bundle_slot{slot}_";
                nodes.Add(new HwOperationNode(q + "bank64", "zext", [bp + "bank_id"], u64, Origin: origin));
                nodes.Add(new HwOperationNode(q + "bank_below16", "ult64", [q + "bank64", "sixteen64"], u1, Origin: origin));
                nodes.Add(new HwOperationNode(q + "bank_match", "eq", [cp + "effective_bank_id", bp + "bank_id"], u1, Origin: origin));
                nodes.Add(new HwOperationNode(q + "present_store", "and", [bp + "present", bp + "is_store"], u1, Origin: origin));
                nodes.Add(new HwOperationNode(q + "typed_store", "and", [q + "present_store", bp + "is_loadstore"], u1, Origin: origin));
                nodes.Add(new HwOperationNode(q + "valid_store", "and", [q + "typed_store", q + "bank_below16"], u1, Origin: origin));
                nodes.Add(new HwOperationNode(q + "same_candidate_bank", "and", [q + "valid_store", q + "bank_match"], u1, Origin: origin));
                string next = cp + $"bundle_store_or{slot}";
                nodes.Add(new HwOperationNode(next, "or", [storePending, q + "same_candidate_bank"], u1, Origin: origin));
                storePending = next;
            }
            nodes.Add(new HwOperationNode(cp + "bundle_store_pending_result", "and", [cp + "candidate_store_bank_valid", storePending], u1, Origin: origin));
            nodes.Add(new HwOutputDriveNode(cp + "drive_bundle_store_bank_pending", cp + "bundle_store_bank_pending", cp + "bundle_store_pending_result", origin));
        }
        var module = new HwModule("smt_projected_memory_state", "smt_projected_memory_state", ports, nodes, [],
            new HwHardwareContract("scheduler.projected-memory-state.unqualified.v1", HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var validation = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", module.Id, [module], toolchain, []));
        return validation.Validated ?? throw new InvalidDataException("Projected-memory state HWIR refused: " + string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    public static HwValidatedDesign Attach(HwValidatedDesign candidate, HwValidatedDesign state)
    {
        if (candidate.Design.TopModule != "smt_ranked_collected_admission_projection" || state.Design.TopModule != "smt_projected_memory_state")
            throw new InvalidDataException("Ranked SMT candidate and projected-memory state required.");
        var top = candidate.Design.Modules.Single(m => m.Id == candidate.Design.TopModule);
        var origin = top.Origin ?? throw new InvalidDataException("Candidate top origin missing.");
        var stateModule = state.Design.Modules.Single(m => m.Id == state.Design.TopModule);
        var topPorts = top.Ports.Where(p => !(p.Direction == HwPortDirection.Input &&
            (p.Name.StartsWith("projected_count_vt", StringComparison.Ordinal) || p.Name.StartsWith("projected_capacity_vt", StringComparison.Ordinal) ||
             p.Name is "remaining_memory_issue_budget" or "remaining_read_issue_budget" or "remaining_write_issue_budget" or
                 "consumed_memory_budget_by_bank" or "consumed_read_budget_by_bank" or "consumed_write_budget_by_bank" ||
             Enumerable.Range(0, 4).Any(i => p.Name == $"vt{i}_bank_pending" || p.Name == $"vt{i}_bundle_store_bank_pending")))).ToList();
        foreach (var port in stateModule.Ports.Where(p => p.Direction == HwPortDirection.Input && !topPorts.Any(x => x.Name == p.Name))) topPorts.Add(port);
        var nodes = new List<HwNode>();
        var stateConnections = new Dictionary<string, string>(StringComparer.Ordinal);
        var candidateConnections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var port in top.Ports)
        {
            if (port.Direction == HwPortDirection.Input && (port.Name.StartsWith("projected_count_vt", StringComparison.Ordinal) || port.Name.StartsWith("projected_capacity_vt", StringComparison.Ordinal) || Enumerable.Range(0, 4).Any(i => port.Name == $"vt{i}_bank_pending" || port.Name == $"vt{i}_bundle_store_bank_pending")))
                candidateConnections.Add(port.Name, "derived_" + port.Name);
            else if (port.Direction == HwPortDirection.Input && port.Name is "remaining_memory_issue_budget" or "remaining_read_issue_budget" or "remaining_write_issue_budget" or "consumed_memory_budget_by_bank" or "consumed_read_budget_by_bank" or "consumed_write_budget_by_bank")
                candidateConnections.Add(port.Name, "derived_initial_" + port.Name);
            else if (port.Direction == HwPortDirection.Output)
                candidateConnections.Add(port.Name, "ranked_" + port.Name);
            else candidateConnections.Add(port.Name, port.Name);
        }
        foreach (var port in stateModule.Ports)
        {
            if (port.Direction == HwPortDirection.Input) stateConnections.Add(port.Name, port.Name);
            else
            {
                string wire = "derived_" + port.Name;
                nodes.Add(new HwWireNode(wire, port.Type, origin));
                stateConnections.Add(port.Name, wire);
            }
        }
        var instances = new List<HwInstanceNode>();
        instances.Add(new HwInstanceNode("projected_memory_state", stateModule.Id, stateConnections, new Dictionary<string, string>(), Origin: origin));
        string coreId = "smt_ranked_collected_admission_core";
        instances.Add(new HwInstanceNode("ranked_candidate", coreId, candidateConnections, new Dictionary<string, string>(), Origin: origin));
        var wrapperPorts = topPorts;
        foreach (var p in top.Ports.Where(p => p.Direction == HwPortDirection.Output))
        {
            nodes.Add(new HwWireNode("ranked_" + p.Name, p.Type, origin));
            nodes.Add(new HwOutputDriveNode("drive_ranked_" + p.Name, p.Name, "ranked_" + p.Name, origin));
        }
        var wrapper = new HwModule("smt_ranked_collected_admission_projection", "smt_ranked_collected_admission_projection",
            wrapperPorts, nodes, instances,
            new HwHardwareContract("scheduler.smt-ranked-collected-admission-with-derived-memory-state.unqualified.v1", HwModuleKind.Combinational, wrapperPorts, new HwTimingContract(0, 1)), origin);
        var core = top with { Id = coreId, Name = coreId };
        var modules = candidate.Design.Modules.Where(m => m.Id != top.Id).Append(core).Concat(state.Design.Modules).Append(wrapper).ToArray();
        var oversized = modules.Where(m => m.Ports.Count > 512 || m.Nodes.Count > 2048 || m.Instances.Count > 128 || m.Nodes.Count + m.Instances.Count > 2048).ToArray();
        if (oversized.Length != 0) throw new InvalidDataException("Composed projected-memory state exceeds bounded module limits: " + string.Join(";", oversized.Select(m => $"{m.Id}: ports={m.Ports.Count}, nodes={m.Nodes.Count}, instances={m.Instances.Count}")));
        var validation = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", wrapper.Id, modules, candidate.Design.Toolchain, []));
        return validation.Validated ?? throw new InvalidDataException("Composed projected-memory state HWIR refused: " + string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }
}
#endif
