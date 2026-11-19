#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Collects ready non-owner candidates in an externally supplied VT ranking.</summary>
public static class BridgeHwirCandidateCollection
{
    public static HwValidatedDesign Compile(byte[] smt, byte[] scheduler, byte[] admission, string commit, HwValidatedDesign scored)
    {
        if (scored.Design.TopModule != "smt_scored_ordered_admission_projection") throw new InvalidDataException("Scored admission required.");
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var expectedOrder = BridgeHwirPressureOrder.Compile(smt, scheduler, admission, [Hash(smt), Hash(scheduler), Hash(admission)], commit, scored.Design.Toolchain);
        if (scored.Design.Modules.Single(m => m.Id == "smt_pressure_order_projection").Origin?.IdentityDigest != expectedOrder.Design.Modules.Single().Origin?.IdentityDigest) throw new InvalidDataException("Candidate collection source identity differs.");
        static SyntaxNode Parse(byte[] bytes) { var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes)); if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) throw new InvalidDataException("Invalid source syntax."); return tree.GetRoot(); }
        static bool Same(SyntaxNode a, SyntaxNode b) => !a.DescendantTrivia(descendIntoTrivia: true).Any(t => t.IsDirective) && a.DescendantTokens().Select(t => t.Text).SequenceEqual(b.DescendantTokens().Select(t => t.Text));
        var root = Parse(smt);
        var owner = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "MicroOpScheduler" && c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core");
        var method = owner.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "PackBundleIntraCoreSmt" && m.ParameterList.Parameters.Count == 5);
        var loop = method.DescendantNodes().OfType<ForStatementSyntax>().Single(f => f.ToString().Contains("legalVts[legalCount] = vt;", StringComparison.Ordinal));
        const string expectedLoop = "for (int i = 0; i < SMT_WAYS; i++) { int vt = vtOrder[i]; if (!nominationState.IsReadyNonOwnerCandidate(vt, ownerVirtualThreadId)) continue; MicroOp candidate = _smtPorts[vt]; if (candidate == null) continue; int candidateBankId = candidate is LoadStoreMicroOp lsCandidate ? lsCandidate.MemoryBankId : -1; int pressure = 0; if (BankPressureTieBreakEnabled && candidate.IsMemoryOp && candidateBankId >= 0) { pressure = GetBankPressureScore(candidateBankId); } legalVts[legalCount] = vt; legalPressure[legalCount] = pressure; legalCount++; }";
        if (!Same(loop, SyntaxFactory.ParseStatement(expectedLoop))) throw new InvalidDataException("Candidate collection changed.");
        var state = Parse(scheduler).DescendantNodes().OfType<StructDeclarationSyntax>().Single(s => s.Identifier.ValueText == "SmtNominationState" && s.Ancestors().OfType<ClassDeclarationSyntax>().Any(c => c.Identifier.ValueText == "MicroOpScheduler"));
        foreach (var pair in new[] { ("IsReadyCandidate", "{ if ((uint)virtualThreadId >= SMT_WAYS) return false; return (ReadyMask & (1 << virtualThreadId)) != 0; }"), ("IsReadyNonOwnerCandidate", "{ return virtualThreadId != ownerVirtualThreadId && IsReadyCandidate(virtualThreadId); }") })
        { var body = state.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == pair.Item1).Body; if (body is null || !Same(body, SyntaxFactory.ParseStatement(pair.Item2))) throw new InvalidDataException("Nomination readiness changed."); }
        var child = scored.Design.Modules.Single(m => m.Id == scored.Design.TopModule);
        var oldOrigin = child.Origin ?? throw new InvalidDataException("Scored admission origin missing.");
        var pos = method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("candidate-collection/v1|" + scored.CanonicalDigest + "|" + Convert.ToHexString(SHA256.HashData(smt)) + "|" + Convert.ToHexString(SHA256.HashData(scheduler))))).ToLowerInvariant();
        var origin = oldOrigin with { IdentityDigest = digest, Line = pos.Line + 1, Column = pos.Character + 1 };
        var u1 = new HwBitVectorType(1); var u2 = new HwBitVectorType(2); var u3 = new HwBitVectorType(3); var u32 = new HwBitVectorType(32);
        var ports = new List<HwPortContract>(); var nodes = new List<HwNode>(); var instances = new List<HwInstanceNode>();
        ports.Add(new("owner_vt", HwPortDirection.Input, u32)); ports.Add(new("collected_count", HwPortDirection.Output, u3));
        foreach (var p in child.Ports.Where(p => !(p.Direction == HwPortDirection.Input && (p.Name == "count" || p.Name == "owner_vt" || p.Name.StartsWith("input", StringComparison.Ordinal))) && !(p.Direction == HwPortDirection.Output && p.Name == "valid"))) ports.Add(p);
        ports.Add(new("valid", HwPortDirection.Output, u1));
        var fields = child.Ports.Where(p => p.Direction == HwPortDirection.Input && p.Name.StartsWith("input0_", StringComparison.Ordinal) && p.Name != "input0_vt").Select(p => (name: p.Name[7..], type: p.Type)).ToArray();
        for (int i = 0; i < 4; i++)
        {
            ports.Add(new("rank" + i, HwPortDirection.Input, u32)); ports.Add(new("vt" + i + "_ready", HwPortDirection.Input, u1)); ports.Add(new("vt" + i + "_present", HwPortDirection.Input, u1)); ports.Add(new("ordered_vt" + i, HwPortDirection.Output, u2));
            foreach (var field in fields) ports.Add(new("vt" + i + "_" + field.name, HwPortDirection.Input, field.type));
        }
        nodes.Add(new HwConstantNode("zero1", u1, "0", origin)); nodes.Add(new HwConstantNode("one1", u1, "1", origin)); nodes.Add(new HwConstantNode("zero3", u3, "000", origin)); nodes.Add(new HwConstantNode("one3", u3, "001", origin)); nodes.Add(new HwConstantNode("minus_one32", u32, new string('1', 32), origin));
        for (int i = 0; i < 4; i++) { nodes.Add(new HwConstantNode("v" + i, u2, Convert.ToString(i, 2).PadLeft(2, '0'), origin)); nodes.Add(new HwConstantNode("v32_" + i, u32, Convert.ToString(i, 2).PadLeft(32, '0'), origin)); nodes.Add(new HwConstantNode("p" + i, u3, Convert.ToString(i, 2).PadLeft(3, '0'), origin)); }
        var allFields = fields.Concat(new[] { (name: "ready", type: u1), (name: "present", type: u1) }).ToArray();
        var values = new Dictionary<(int, string), string>();
        foreach (var field in fields) for (int i = 0; i < 4; i++) { string zero = "initial_" + i + "_" + field.name; nodes.Add(new HwConstantNode(zero, field.type, new string('0', field.type.Width), origin)); values[(i, field.name)] = zero; }
        string[] collectedVt = ["v0", "v0", "v0", "v0"]; string count = "zero3";
        for (int rank = 0; rank < 4; rank++)
        {
            var selected = new Dictionary<string, string>();
            for (int i = 0; i < 4; i++) nodes.Add(new HwOperationNode($"rank{rank}_is{i}", "eq", ["rank" + rank, "v32_" + i], u1, Origin: origin));
            foreach (var field in allFields) { string value = "vt0_" + field.name; for (int i = 1; i < 4; i++) { string n = $"rank{rank}_{field.name}_{i}"; nodes.Add(new HwMuxNode(n, $"rank{rank}_is{i}", "vt" + i + "_" + field.name, value, field.type, origin)); value = n; } selected[field.name] = value; }
            string prefix = "r" + rank + "_"; nodes.Add(new HwOperationNode(prefix + "vt2", "trunc", ["rank" + rank], u2, Origin: origin)); nodes.Add(new HwOperationNode(prefix + "is_owner", "eq", ["rank" + rank, "owner_vt"], u1, Origin: origin)); nodes.Add(new HwOperationNode(prefix + "non_owner", "xor", [prefix + "is_owner", "one1"], u1, Origin: origin)); nodes.Add(new HwOperationNode(prefix + "range01", "or", [$"rank{rank}_is0", $"rank{rank}_is1"], u1, Origin: origin)); nodes.Add(new HwOperationNode(prefix + "range23", "or", [$"rank{rank}_is2", $"rank{rank}_is3"], u1, Origin: origin)); nodes.Add(new HwOperationNode(prefix + "in_range", "or", [prefix + "range01", prefix + "range23"], u1, Origin: origin)); nodes.Add(new HwOperationNode(prefix + "ready", "and", [selected["ready"], selected["present"]], u1, Origin: origin)); nodes.Add(new HwOperationNode(prefix + "non_owner_ready", "and", [prefix + "ready", prefix + "non_owner"], u1, Origin: origin)); nodes.Add(new HwOperationNode(prefix + "eligible", "and", [prefix + "non_owner_ready", prefix + "in_range"], u1, Origin: origin));
            nodes.Add(new HwMuxNode(prefix + "bank", selected["is_loadstore"], selected["bank_id"], "minus_one32", u32, origin)); selected["bank_id"] = prefix + "bank";
            for (int slot = 0; slot < 4; slot++)
            {
                string key = prefix + "slot" + slot; nodes.Add(new HwOperationNode(key + "_match", "eq", [count, "p" + slot], u1, Origin: origin)); nodes.Add(new HwOperationNode(key + "_write", "and", [key + "_match", prefix + "eligible"], u1, Origin: origin));
                foreach (var field in fields) { string n = key + "_" + field.name; nodes.Add(new HwMuxNode(n, key + "_write", selected[field.name], values[(slot, field.name)], field.type, origin)); values[(slot, field.name)] = n; }
                nodes.Add(new HwMuxNode(key + "_vt", key + "_write", prefix + "vt2", collectedVt[slot], u2, origin)); collectedVt[slot] = key + "_vt";
            }
            nodes.Add(new HwOperationNode(prefix + "next", "add", [count, "one3"], u3, Origin: origin)); nodes.Add(new HwMuxNode(prefix + "count", prefix + "eligible", prefix + "next", count, u3, origin)); count = prefix + "count";
        }
        nodes.Add(new HwOutputDriveNode("drive_collected_count", "collected_count", count, origin));
        var connections = new Dictionary<string, string>();
        foreach (var p in child.Ports)
        {
            string signal = p.Name;
            if (p.Direction == HwPortDirection.Output) { signal = "child_" + p.Name; nodes.Add(new HwWireNode(signal, p.Type, origin)); if (p.Name != "valid") nodes.Add(new HwOutputDriveNode("drive_" + p.Name, p.Name, signal, origin)); }
            else if (p.Name == "count") signal = count;
            else if (p.Name.StartsWith("input", StringComparison.Ordinal)) signal = p.Name[7..] == "vt" ? collectedVt[p.Name[5] - '0'] : values[(p.Name[5] - '0', p.Name[7..])];
            connections.Add(p.Name, signal);
        }
        instances.Add(new HwInstanceNode("scored_admission", child.Id, connections, new Dictionary<string, string>(), Origin: origin));
        nodes.Add(new HwOutputDriveNode("drive_valid", "valid", "child_valid", origin));
        for (int i = 0; i < 4; i++) { string value = collectedVt[0]; for (int j = 1; j < 4; j++) { string n = $"ordered_vt{i}_select{j}"; nodes.Add(new HwOperationNode(n + "_match", "eq", ["child_ordered_id" + i, "p" + j], u1, Origin: origin)); nodes.Add(new HwMuxNode(n, n + "_match", collectedVt[j], value, u2, origin)); value = n; } nodes.Add(new HwOutputDriveNode("drive_ordered_vt" + i, "ordered_vt" + i, value, origin)); }
        var top = new HwModule("smt_collected_scored_admission_projection", "smt_collected_scored_admission_projection", ports, nodes, instances, new HwHardwareContract("scheduler.smt-collected-scored-admission.unqualified.v1", HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var result = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", top.Id, [top, .. scored.Design.Modules], scored.Design.Toolchain, []));
        return result.Validated ?? throw new InvalidDataException("Candidate collection HWIR refused: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }
}
#endif
