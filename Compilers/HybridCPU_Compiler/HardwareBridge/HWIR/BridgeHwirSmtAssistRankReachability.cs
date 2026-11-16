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
/// Source-bound Assist rank3 loop-entry/reachability projection. Earlier admission
/// commits are abstract inputs; this module does not model class, lane or quota policies.
/// </summary>
public static class BridgeHwirSmtAssistRankReachability
{
    public const string AssistSourceSha256 = "8515039c3d770382fd448439d2453c01bd42397f3b525b413d411161bf8035a8";
    public const int AssistBundleQuota = 1;

    public static HwValidatedDesign Compile(byte[] assistSource, string commit, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(assistSource);
        string source = new UTF8Encoding(false, true).GetString(assistSource);
        string sourceHash = Hash(assistSource);
        if (sourceHash != AssistSourceSha256 || commit != "1bfe41a49cf11c80fa6c6aee87accb54b78b331f")
            throw new InvalidDataException("Active Assist bytes or base model identity changed.");
        SyntaxNode root = CSharpSyntaxTree.ParseText(source).GetRoot();
        ClassDeclarationSyntax owner = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "MicroOpScheduler" &&
                c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core");
        FieldDeclarationSyntax quota = owner.Members.OfType<FieldDeclarationSyntax>()
            .Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "AssistBundleQuota"));
        if (quota.Declaration.Type.ToString() != "int" ||
            quota.Declaration.Variables.Single(v => v.Identifier.ValueText == "AssistBundleQuota").Initializer?.Value.ToString() != "1")
            throw new InvalidDataException("AssistBundleQuota is no longer one.");
        MethodDeclarationSyntax method = owner.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "TryInjectAssistCandidates");
        BlockSyntax body = method.Body ?? throw new InvalidDataException("Assist body missing.");
        string[] entryGuards =
        [
            "if (!AssistInjectionEnabled) { ClearAssistNominationPorts(); return 0; }",
            "if (ResolveNextInjectableSlot(opportunityState, 0) < 0) { ClearAssistNominationPorts(); return 0; }",
            "if (IsSmtBundleBlockedByBoundaryGuard(bundleCert, bundleMetadata, boundaryGuard)) { AssistBoundaryRejects += CountReadyAssistCandidates(ownerVirtualThreadId); ClearAssistNominationPorts(); return 0; }"
        ];
        int previous = -1;
        foreach (string expected in entryGuards)
        {
            IfStatementSyntax guard = body.Statements.OfType<IfStatementSyntax>()
                .SingleOrDefault(i => Normalize(i, SyntaxFactory.ParseStatement(expected)))
                ?? throw new InvalidDataException("Assist early-return gate changed: " + expected);
            int at = body.Statements.IndexOf(guard);
            if (at <= previous) throw new InvalidDataException("Assist early-return order changed.");
            previous = at;
        }
        if (body.Statements.OfType<LocalDeclarationStatementSyntax>().SingleOrDefault(s =>
                s.Declaration.Variables.Any(v => v.Identifier.ValueText == "assistInjectedCount")) is not { } countDecl ||
            countDecl.Declaration.Variables.Single(v => v.Identifier.ValueText == "assistInjectedCount").Initializer?.Value.ToString() != "0")
            throw new InvalidDataException("Assist injection counter initialization changed.");
        ForStatementSyntax loop = body.Statements.OfType<ForStatementSyntax>().Single(f => f.Condition?.ToString() ==
            "orderIndex < SMT_WAYS && assistInjectedCount < AssistBundleQuota");
        int loopIndex = body.Statements.IndexOf(loop);
        if (loopIndex <= previous || loop.ToString().IndexOf("int vtId = vtOrder[orderIndex];", StringComparison.Ordinal) < 0 ||
            !loop.DescendantNodes().OfType<IfStatementSyntax>().Any(i =>
                string.Concat(i.Condition?.DescendantTokens().Select(t => t.Text) ?? []) ==
                "vtId==ownerVirtualThreadId||!_smtAssistPortValid[vtId]||_smtAssistPorts[vtId]==null"))
            throw new InvalidDataException("Assist rank read, loop condition, or short-circuit owner/index guard changed.");
        int commitCounters = loop.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>()
            .Count(p => p.ToString() == "assistInjectedCount++");
        if (commitCounters != 2) throw new InvalidDataException("Expected success-counter increment in both typed and legacy commit branches.");

        FileLinePositionSpan methodLocation = method.Identifier.GetLocation().GetLineSpan();
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", commit,
            "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Assist/Runtime/MicroOpScheduler.Assist.cs",
            "CircuitWeave.SmtAssistRankReachability", "YAKSys_Hybrid_CPU.Core.MicroOpScheduler", "TryInjectAssistCandidates",
            Hash(Encoding.UTF8.GetBytes("assist-rank3-reachability/v1|" + sourceHash)), HwSourceRole.Primary,
            methodLocation.StartLinePosition.Line + 1, methodLocation.StartLinePosition.Character + 1);

        var bit = new HwBitVectorType(1); var u32 = new HwBitVectorType(32); var u64 = new HwBitVectorType(64);
        var ports = new List<HwPortContract>
        {
            new("assist_enabled", HwPortDirection.Input, bit),
            new("slot_available", HwPortDirection.Input, bit),
            new("boundary_blocked", HwPortDirection.Input, bit),
            new("prior_rank0_committed", HwPortDirection.Input, bit),
            new("prior_rank1_committed", HwPortDirection.Input, bit),
            new("prior_rank2_committed", HwPortDirection.Input, bit),
            new("owner_vt", HwPortDirection.Input, u32),
            new("rank3_vt", HwPortDirection.Input, u32),
            new("rank3_iteration_reached", HwPortDirection.Output, bit),
            new("rank3_index_oob_exception_condition", HwPortDirection.Output, bit)
        };
        var nodes = new List<HwNode>();
        nodes.Add(new HwConstantNode("one", bit, "1", origin));
        nodes.Add(new HwConstantNode("four32", u32, Convert.ToString(4, 2).PadLeft(32, '0'), origin));
        nodes.Add(new HwConstantNode("four64", u64, Convert.ToString(4, 2).PadLeft(64, '0'), origin));
        nodes.Add(new HwOperationNode("entry_assist_slot", "and", ["assist_enabled", "slot_available"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("not_boundary_blocked", "xor", ["boundary_blocked", "one"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("entry_open", "and", ["entry_assist_slot", "not_boundary_blocked"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("prior_commit01", "or", ["prior_rank0_committed", "prior_rank1_committed"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("prior_commit_any", "or", ["prior_commit01", "prior_rank2_committed"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("no_prior_commit", "xor", ["prior_commit_any", "one"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("rank3_reached", "and", ["entry_open", "no_prior_commit"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("rank3_u64", "zext", ["rank3_vt"], u64, Origin: origin));
        nodes.Add(new HwOperationNode("four64_u64", "zext", ["four32"], u64, Origin: origin));
        nodes.Add(new HwOperationNode("rank3_in_range", "ult64", ["rank3_u64", "four64_u64"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("not_rank3_in_range", "xor", ["rank3_in_range", "one"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("rank3_is_owner", "eq", ["rank3_vt", "owner_vt"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("not_rank3_owner", "xor", ["rank3_is_owner", "one"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("oob_without_owner_guard", "and", ["not_rank3_in_range", "not_rank3_owner"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("oob_if_reached", "and", ["rank3_reached", "oob_without_owner_guard"], bit, Origin: origin));
        nodes.Add(new HwOutputDriveNode("drive_rank3_reached", "rank3_iteration_reached", "rank3_reached", origin));
        nodes.Add(new HwOutputDriveNode("drive_rank3_oob", "rank3_index_oob_exception_condition", "oob_if_reached", origin));
        var module = new HwModule("smt_assist_rank_reachability_projection", "smt_assist_rank_reachability_projection",
            ports, nodes, [], new HwHardwareContract("scheduler.smt-assist-rank-reachability.unqualified.v1",
                HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var result = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", module.Id, [module], toolchain, []));
        return result.Validated ?? throw new InvalidDataException("Assist rank reachability HWIR refused: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    private static bool Normalize(SyntaxNode a, SyntaxNode b) =>
        !a.DescendantTrivia(descendIntoTrivia: true).Any(t => t.IsDirective) &&
        a.DescendantTokens().Select(t => t.Text).SequenceEqual(b.DescendantTokens().Select(t => t.Text));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
