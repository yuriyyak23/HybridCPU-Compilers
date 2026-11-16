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
/// Source-bound SMT legality guard-plane projection. Boundary, owner-context and
/// domain guards are evaluated in source order before the selected certificate gate.
/// Replay-template matching and the certificate predicate remain explicit inputs.
/// </summary>
public static class BridgeHwirSmtLegalityGuardPlane
{
    public const string SmtLegalitySha256 = "89cb7e9f737815d9526bed00186c62f324ebaf805c1520a2ed5ebd1349b529a4";
    public const string GuardsSha256 = "f87ba64048e2d0a22642b254fe3eebd696553d3b8b4355b831b32ca77acbbf16";
    public const string Commit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";

    public static HwValidatedDesign Compile(byte[] smtLegality, byte[] guards, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(smtLegality);
        ArgumentNullException.ThrowIfNull(guards);
        string legalityHash = Hash(smtLegality), guardsHash = Hash(guards);
        if (legalityHash != SmtLegalitySha256 || guardsHash != GuardsSha256)
            throw new InvalidDataException("Pinned active SMT guard-plane source bytes changed.");
        SyntaxNode legalityRoot = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(smtLegality)).GetRoot();
        SyntaxNode guardsRoot = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(guards)).GetRoot();
        MethodDeclarationSyntax legality = legalityRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "EvaluateSmtLegality" && m.Modifiers.Any(SyntaxKind.InternalKeyword));
        string legalityTokens = Tokens(legality);
        string[] orderedStatements =
        [
            "LegalityDecision boundaryDecision = EvaluateSmtBoundaryGuard(liveTemplateKey);",
            "if (!boundaryDecision.IsAllowed) return boundaryDecision;",
            "if (TryRejectSmtOwnerDomainGuard(liveTemplateKey.BundleMetadata, candidate, out LegalityDecision guardReject)) return guardReject;",
            "bool attemptedReplayReuse = liveTemplateKey.PhaseKey.IsValid;",
            "if (attemptedReplayReuse && phaseTemplate.Matches(liveTemplateKey)) { return EvaluateSmtCertificate(phaseTemplate.Certificate, candidate, LegalityAuthoritySource.ReplayPhaseCertificate, attemptedReplayReuse); }",
            "return EvaluateSmtCertificate(bundleCertificate, candidate, LegalityAuthoritySource.StructuralCertificate, attemptedReplayReuse);"
        ];
        int last = -1;
        foreach (string statement in orderedStatements)
        {
            string normalized = string.Concat(SyntaxFactory.ParseStatement(statement).DescendantTokens().Select(t => t.Text));
            int at = legalityTokens.IndexOf(normalized, StringComparison.Ordinal);
            if (at <= last) throw new InvalidDataException("SMT legality decision order changed at: " + statement);
            last = at;
        }

        MethodDeclarationSyntax boundary = guardsRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "EvaluateSmtBoundaryGuard" && m.ExplicitInterfaceSpecifier is null);
        string boundaryTokens = Tokens(boundary);
        if (!boundaryTokens.Contains("liveTemplateKey.BoundaryGuard.BlocksSmtInjection", StringComparison.Ordinal) ||
            !boundaryTokens.Contains("RejectKind.Boundary", StringComparison.Ordinal))
            throw new InvalidDataException("SMT boundary guard condition changed.");
        MethodDeclarationSyntax ownerDomain = guardsRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "TryRejectSmtOwnerDomainGuard");
        string ownerTokens = Tokens(ownerDomain);
        if (!ownerTokens.Contains("bundleMetadata.HasKnownOwnerContext&&candidate.OwnerContextId!=bundleMetadata.OwnerContextId", StringComparison.Ordinal) ||
            !ownerTokens.Contains("bundleMetadata.HasDomainRestriction", StringComparison.Ordinal) ||
            !ownerTokens.Contains("EvaluateDomainIsolationProbe(candidate,bundleMetadata.OwnerDomainTag).IsAllowed", StringComparison.Ordinal))
            throw new InvalidDataException("SMT owner/domain guard conditions changed.");
        MethodDeclarationSyntax domainProbe = guardsRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "EvaluateDomainIsolationProbe" && m.ParameterList.Parameters.Count == 2 &&
                m.ParameterList.Parameters[0].Type?.ToString() == "ulong");
        string domainTokens = Tokens(domainProbe);
        foreach (string token in new[] { "domainTag==0", "KernelDomainIsolation&&podDomainCert!=0", "podDomainCert==0", "(domainTag&podDomainCert)!=0" })
            if (!domainTokens.Contains(token, StringComparison.Ordinal)) throw new InvalidDataException("Domain probe rule changed: " + token);

        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", Commit,
            "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.SmtLegality.cs",
            "CircuitWeave.SmtLegalityGuardPlane", "YAKSys_Hybrid_CPU.Core.SafetyVerifier", "EvaluateSmtLegality",
            Hash(Encoding.UTF8.GetBytes("smt-legality-guard-plane/v1|" + legalityHash + "|" + guardsHash)),
            HwSourceRole.Primary, legality.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            legality.GetLocation().GetLineSpan().StartLinePosition.Character + 1);

        var bit = new HwBitVectorType(1); var u32 = new HwBitVectorType(32); var u64 = new HwBitVectorType(64);
        var ports = new List<HwPortContract>
        {
            new("boundary_blocked", HwPortDirection.Input, bit),
            new("owner_context_known", HwPortDirection.Input, bit),
            new("owner_context", HwPortDirection.Input, u32),
            new("candidate_owner_context", HwPortDirection.Input, u32),
            new("candidate_domain", HwPortDirection.Input, u64),
            new("owner_domain_certificate", HwPortDirection.Input, u64),
            new("kernel_domain_isolation", HwPortDirection.Input, bit),
            new("phase_key_valid", HwPortDirection.Input, bit),
            new("phase_template_matches", HwPortDirection.Input, bit),
            new("selected_certificate_allowed", HwPortDirection.Input, bit),
            new("boundary_reject", HwPortDirection.Output, bit),
            new("owner_context_reject", HwPortDirection.Output, bit),
            new("domain_reject", HwPortDirection.Output, bit),
            new("certificate_checked", HwPortDirection.Output, bit),
            new("replay_certificate_selected", HwPortDirection.Output, bit),
            new("structural_certificate_selected", HwPortDirection.Output, bit),
            new("legality_allowed", HwPortDirection.Output, bit)
        };
        var one = new HwConstantNode("one", bit, "1", origin);
        var zero64 = new HwConstantNode("zero64", u64, new string('0', 64), origin);
        var nodes = new List<HwNode> { one, zero64 };
        void Op(string id, string opcode, string[] operands, HwBitVectorType type) => nodes.Add(new HwOperationNode(id, opcode, operands, type, Origin: origin));
        void Mux(string id, string condition, string whenTrue, string whenFalse, HwBitVectorType type) => nodes.Add(new HwMuxNode(id, condition, whenTrue, whenFalse, type, origin));
        void Drive(string id, string output, string value) => nodes.Add(new HwOutputDriveNode(id, output, value, origin));

        Op("owner_context_equal", "eq", ["owner_context", "candidate_owner_context"], bit);
        Op("owner_context_not_equal", "xor", ["owner_context_equal", "one"], bit);
        Op("owner_context_mismatch", "and", ["owner_context_known", "owner_context_not_equal"], bit);
        Op("domain_is_kernel", "eq", ["candidate_domain", "zero64"], bit);
        Op("pod_domain_is_user", "eq", ["owner_domain_certificate", "zero64"], bit);
        Op("pod_domain_nonzero", "xor", ["pod_domain_is_user", "one"], bit);
        Op("kernel_domain_rejected", "and", ["domain_is_kernel", "kernel_domain_isolation"], bit);
        Op("kernel_user_domain_rejected", "and", ["kernel_domain_rejected", "pod_domain_nonzero"], bit);
        Op("domain_intersection", "and", ["candidate_domain", "owner_domain_certificate"], u64);
        Op("domain_intersection_is_zero", "eq", ["domain_intersection", "zero64"], bit);
        Op("domain_rejected_for_tagged_candidate", "and", ["pod_domain_nonzero", "domain_intersection_is_zero"], bit);
        Op("domain_rejected_for_kernel_candidate", "and", ["pod_domain_nonzero", "kernel_domain_rejected"], bit);
        Mux("domain_reject_value", "domain_is_kernel", "domain_rejected_for_kernel_candidate", "domain_rejected_for_tagged_candidate", bit);

        Op("not_boundary", "xor", ["boundary_blocked", "one"], bit);
        Mux("owner_reject_after_boundary", "not_boundary", "owner_context_mismatch", "one", bit);
        Op("no_boundary_or_owner_reject", "xor", ["owner_reject_after_boundary", "one"], bit);
        Op("owner_reject", "and", ["not_boundary", "owner_context_mismatch"], bit);
        Op("domain_reject_after_prior_guards", "and", ["no_boundary_or_owner_reject", "domain_reject_value"], bit);
        Op("guard_reject_any", "or", ["boundary_blocked", "owner_reject"], bit);
        Op("all_guard_rejects", "or", ["guard_reject_any", "domain_reject_after_prior_guards"], bit);
        Op("guard_clear", "xor", ["all_guard_rejects", "one"], bit);
        Op("certificate_checked_value", "and", ["guard_clear", "one"], bit);
        Op("replay_key_match", "and", ["phase_key_valid", "phase_template_matches"], bit);
        Op("replay_selected_value", "and", ["certificate_checked_value", "replay_key_match"], bit);
        Op("not_replay_key_match", "xor", ["replay_key_match", "one"], bit);
        Op("structural_selected_value", "and", ["certificate_checked_value", "not_replay_key_match"], bit);
        Op("certificate_accept", "and", ["certificate_checked_value", "selected_certificate_allowed"], bit);
        Drive("drive_boundary_reject", "boundary_reject", "boundary_blocked");
        Drive("drive_owner_reject", "owner_context_reject", "owner_reject");
        Drive("drive_domain_reject", "domain_reject", "domain_reject_after_prior_guards");
        Drive("drive_certificate_checked", "certificate_checked", "certificate_checked_value");
        Drive("drive_replay_selected", "replay_certificate_selected", "replay_selected_value");
        Drive("drive_structural_selected", "structural_certificate_selected", "structural_selected_value");
        Drive("drive_legality_allowed", "legality_allowed", "certificate_accept");

        var module = new HwModule("smt_legality_guard_plane_projection", "smt_legality_guard_plane_projection", ports,
            nodes, [], new HwHardwareContract("scheduler.smt-legality-guard-plane.unqualified.v1", HwModuleKind.Combinational,
                ports, new HwTimingContract(0, 1)), origin);
        var result = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", module.Id, [module], toolchain, []));
        return result.Validated ?? throw new InvalidDataException("SMT legality guard plane HWIR refused: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    private static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
