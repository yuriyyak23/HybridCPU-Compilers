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
/// Source-bound abstract control projection for a duplicate entry in legalVts.
/// Admission and lane outcomes are explicit inputs; this is not full scheduler RTL.
/// </summary>
public static class BridgeHwirSmtDuplicateRetry
{
    public const string SmtSourceSha256 = "4ecfa34a01d71033b9d6c0b3f2ce752cb50ff8d6004b3a2ba3d6f1c5710e340d";
    public const string AdmissionSourceSha256 = "6cdd37f43a0a58c0b887f9e8bffe9d2d17abb5d404f7ace70fead05db4edbe25";

    public static HwValidatedDesign Compile(byte[] smt, byte[] admission, string commit, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(smt);
        ArgumentNullException.ThrowIfNull(admission);
        if (commit != "1bfe41a49cf11c80fa6c6aee87accb54b78b331f" || Hash(smt) != SmtSourceSha256 || Hash(admission) != AdmissionSourceSha256)
            throw new InvalidDataException("Active SMT/admission bytes or base model identity changed.");

        var smtRoot = Parse(smt);
        var scheduler = smtRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c =>
            c.Identifier.ValueText == "MicroOpScheduler" &&
            c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core");
        var method = scheduler.Members.OfType<MethodDeclarationSyntax>().Single(m =>
            m.Identifier.ValueText == "PackBundleIntraCoreSmt" && m.ParameterList.Parameters.Count == 5);
        var loop = method.DescendantNodes().OfType<ForStatementSyntax>().Single(f =>
            f.Condition?.ToString() == "i < legalCount" && f.ToString().Contains("legalVts[i]", StringComparison.Ordinal));
        string loopText = loop.ToString();
        RequireOrdered(loopText, "MicroOp candidate = _smtPorts[vt];", "if (candidate == null) continue;",
            "TryClassAdmission(candidate", "TryMaterializeLane(candidate", "_smtPorts[vt] = null;", "nominationState = nominationState.WithoutCandidate(vt);");
        if (!loopText.Contains("if (TypedSlotEnabled)", StringComparison.Ordinal))
            throw new InvalidDataException("SMT typed-slot branch changed.");

        var admissionRoot = Parse(admission);
        var admissionOwner = admissionRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c =>
            c.Identifier.ValueText == "MicroOpScheduler" &&
            c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core");
        var admissionMethod = admissionOwner.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "TryClassAdmission");
        string admissionText = admissionMethod.ToString();
        RequireOrdered(admissionText, "templateState.TryConsumeFastPathBudget(", "_classCapacity.HasFreeCapacity(",
            "_runtimeLegalityService.EvaluateSmtLegality(", "TryPassOuterCap(");

        var smtPosition = method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        string digest = Hash(Encoding.UTF8.GetBytes("smt-duplicate-retry/v1|" + Hash(smt) + "|" + Hash(admission)));
        var smtOrigin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", commit,
            "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/Smt/MicroOpScheduler.SMT.cs",
            "CircuitWeave.SmtDuplicateRetry", "YAKSys_Hybrid_CPU.Core.MicroOpScheduler", "PackBundleIntraCoreSmt",
            digest, HwSourceRole.Primary, smtPosition.Line + 1, smtPosition.Character + 1);
        var u1 = new HwBitVectorType(1);
        var u2 = new HwBitVectorType(2);
        var ports = new List<HwPortContract>
        {
            new("duplicate_entry_present", HwPortDirection.Input, u1),
            new("rank3_candidate_present", HwPortDirection.Input, u1),
            new("attempt0_class_admitted", HwPortDirection.Input, u1),
            new("attempt0_lane_materialized", HwPortDirection.Input, u1),
            new("attempt1_class_admitted", HwPortDirection.Input, u1),
            new("attempt1_lane_materialized", HwPortDirection.Input, u1),
            new("attempt0_template_budget_consumed", HwPortDirection.Input, u1),
            new("attempt1_template_budget_consumed", HwPortDirection.Input, u1),
            new("first_attempt_reached", HwPortDirection.Output, u1),
            new("duplicate_retry_reached", HwPortDirection.Output, u1),
            new("first_attempt_committed", HwPortDirection.Output, u1),
            new("retry_attempt_committed", HwPortDirection.Output, u1),
            new("port_cleared_after_loop", HwPortDirection.Output, u1),
            new("nomination_cleared_after_loop", HwPortDirection.Output, u1),
            new("attempt0_rejected", HwPortDirection.Output, u1),
            new("attempt1_rejected", HwPortDirection.Output, u1),
            new("rejection_events", HwPortDirection.Output, u2),
            new("template_budget_consumptions", HwPortDirection.Output, u2)
        };
        var nodes = new List<HwNode>();
        void C(string id, HwBitVectorType type, int value, HwSourceOrigin origin = null!) => nodes.Add(new HwConstantNode(id, type, Convert.ToString(value, 2).PadLeft(type.Width, '0'), origin ?? smtOrigin));
        void Op(string id, string opcode, string[] operands, HwBitVectorType type, HwSourceOrigin origin = null!) => nodes.Add(new HwOperationNode(id, opcode, operands, type, Origin: origin ?? smtOrigin));
        void Drive(string output, string value) => nodes.Add(new HwOutputDriveNode("drive_" + output, output, value, smtOrigin));
        C("one", u1, 1); C("zero", u1, 0); C("zero2", u2, 0);
        Op("attempt0_reached", "and", ["duplicate_entry_present", "rank3_candidate_present"], u1);
        Op("attempt0_success", "and", ["attempt0_class_admitted", "attempt0_lane_materialized"], u1);
        Op("retry_candidate", "xor", ["attempt0_success", "one"], u1);
        Op("retry_reached", "and", ["attempt0_reached", "duplicate_entry_present"], u1);
        Op("retry_reached_final", "and", ["retry_reached", "retry_candidate"], u1);
        Op("attempt1_success_raw", "and", ["attempt1_class_admitted", "attempt1_lane_materialized"], u1);
        Op("attempt1_success", "and", ["retry_reached_final", "attempt1_success_raw"], u1);
        Op("any_success", "or", ["attempt0_success", "attempt1_success"], u1);
        Op("attempt0_failed_raw", "xor", ["attempt0_success", "one"], u1);
        Op("attempt0_failed", "and", ["attempt0_reached", "attempt0_failed_raw"], u1);
        Op("attempt1_failed_raw", "xor", ["attempt1_success_raw", "one"], u1);
        Op("attempt1_failed", "and", ["retry_reached_final", "attempt1_failed_raw"], u1);
        Op("reject0_u2", "zext", ["attempt0_failed"], u2);
        Op("reject1_u2", "zext", ["attempt1_failed"], u2);
        Op("reject_total", "add", ["reject0_u2", "reject1_u2"], u2);
        Op("budget0_reached", "and", ["attempt0_reached", "attempt0_template_budget_consumed"], u1);
        Op("budget1_reached", "and", ["retry_reached_final", "attempt1_template_budget_consumed"], u1);
        Op("budget0_u2", "zext", ["budget0_reached"], u2);
        Op("budget1_u2", "zext", ["budget1_reached"], u2);
        Op("budget_total", "add", ["budget0_u2", "budget1_u2"], u2);
        foreach (var pair in new[]
        {
            ("first_attempt_reached", "attempt0_reached"), ("duplicate_retry_reached", "retry_reached_final"),
            ("first_attempt_committed", "attempt0_success"), ("retry_attempt_committed", "attempt1_success"),
            ("port_cleared_after_loop", "any_success"), ("nomination_cleared_after_loop", "any_success"),
            ("attempt0_rejected", "attempt0_failed"), ("attempt1_rejected", "attempt1_failed"),
            ("rejection_events", "reject_total"), ("template_budget_consumptions", "budget_total")
        }) Drive(pair.Item1, pair.Item2);

        var module = new HwModule("smt_duplicate_retry_projection", "smt_duplicate_retry_projection", ports, nodes, [],
            new HwHardwareContract("scheduler.smt-duplicate-retry.typed-slot-path.abstract.v1", HwModuleKind.Combinational, ports,
                new HwTimingContract(0, 1)), smtOrigin);
        var design = new HwDesign("hwir/v0.1", module.Id, [module], toolchain, []);
        var validation = new FailClosedHardwareIrValidator().Validate(design);
        return validation.Validated ?? throw new InvalidDataException("Duplicate retry HWIR failed validation: " + string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    private static SyntaxNode Parse(byte[] bytes)
    {
        var root = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes)).GetRoot();
        if (root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) throw new InvalidDataException("Pinned source does not parse.");
        return root;
    }

    private static void RequireOrdered(string text, params string[] fragments)
    {
        int previous = -1;
        foreach (string fragment in fragments)
        {
            int position = text.IndexOf(fragment, StringComparison.Ordinal);
            if (position <= previous) throw new InvalidDataException("Source control-flow fragment missing or reordered: " + fragment);
            previous = position;
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
