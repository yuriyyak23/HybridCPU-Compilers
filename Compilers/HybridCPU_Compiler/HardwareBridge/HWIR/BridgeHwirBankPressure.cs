#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Combinational projection of GetBankPressureScore over a caller-supplied scoreboard snapshot.</summary>
public static class BridgeHwirBankPressure
{
    /// <summary>Connects scoreboard pressure to the ordered admission projection for an already collected prefix.</summary>
    public static HwValidatedDesign AttachToOrderedAdmission(byte[] smt, byte[] scheduler, byte[] admission, string commit, HwValidatedDesign pressure, HwValidatedDesign ordered)
    {
        if (pressure.Design.TopModule != "smt_bank_pressure_projection" || ordered.Design.TopModule != "smt_ordered_admission_projection" || pressure.Design.Toolchain != ordered.Design.Toolchain)
            throw new InvalidDataException("Pressure composition child identity differs.");
        static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        string[] hashes = [Hash(smt), Hash(scheduler), Hash(admission)];
        if (Compile(smt, scheduler, admission, hashes, commit, pressure.Design.Toolchain).CanonicalDigest != pressure.CanonicalDigest)
            throw new InvalidDataException("Score source identity differs.");
        var expectedOrder = BridgeHwirPressureOrder.Compile(smt, scheduler, admission, hashes, commit, pressure.Design.Toolchain);
        if (ordered.Design.Modules.Single(m => m.Id == "smt_pressure_order_projection").Origin?.IdentityDigest != expectedOrder.Design.Modules.Single().Origin?.IdentityDigest)
            throw new InvalidDataException("Ordering source identity differs.");
        var root = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(smt)).GetRoot();
        var method = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "MicroOpScheduler" && c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core").Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "PackBundleIntraCoreSmt" && m.ParameterList.Parameters.Count == 5);
        var condition = method.DescendantNodes().OfType<IfStatementSyntax>().Single(i => i.Condition.ToString() == "BankPressureTieBreakEnabled && candidate.IsMemoryOp && candidateBankId >= 0");
        var expected = SyntaxFactory.ParseStatement("if (BankPressureTieBreakEnabled && candidate.IsMemoryOp && candidateBankId >= 0) { pressure = GetBankPressureScore(candidateBankId); }");
        if (condition.DescendantTrivia(descendIntoTrivia: true).Any(t => t.IsDirective) || !condition.DescendantTokens().Select(t => t.Text).SequenceEqual(expected.DescendantTokens().Select(t => t.Text))) throw new InvalidDataException("Candidate pressure gate changed.");
        var score = pressure.Design.Modules.Single(m => m.Id == pressure.Design.TopModule); var child = ordered.Design.Modules.Single(m => m.Id == ordered.Design.TopModule);
        var baseOrigin = child.Origin ?? throw new InvalidDataException("Ordered admission source origin missing.");
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("score-ordered-admission/v1|" + pressure.CanonicalDigest + "|" + ordered.CanonicalDigest))).ToLowerInvariant();
        var origin = baseOrigin with { IdentityDigest = digest };
        var u1 = new HwBitVectorType(1); var u6 = new HwBitVectorType(6); var u32 = new HwBitVectorType(32);
        var ports = new List<HwPortContract>(); var nodes = new List<HwNode>(); var instances = new List<HwInstanceNode>();
        foreach (var p in child.Ports.Where(p => !(p.Direction == HwPortDirection.Input && p.Name.Length == 9 && p.Name.StartsWith("pressure", StringComparison.Ordinal)))) ports.Add(p);
        foreach (var p in score.Ports.Where(p => p.Direction == HwPortDirection.Input && p.Name != "bank_id")) ports.Add(p);
        nodes.Add(new HwConstantNode("zero_score", u6, "000000", origin));
        for (int i = 0; i < 4; i++)
        {
            ports.Add(new("input" + i + "_bank_id", HwPortDirection.Input, u32));
            ports.Add(new("input" + i + "_is_memory", HwPortDirection.Input, u1));
            nodes.Add(new HwWireNode("raw_pressure" + i, u6, origin));
            nodes.Add(new HwOperationNode("pressure_gate" + i, "and", ["sort_enabled", "input" + i + "_is_memory"], u1, Origin: origin));
            nodes.Add(new HwMuxNode("pressure" + i, "pressure_gate" + i, "raw_pressure" + i, "zero_score", u6, origin));
            var connections = new Dictionary<string, string>();
            foreach (var p in score.Ports) connections.Add(p.Name, p.Name switch { "bank_id" => "input" + i + "_bank_id", "score" => "raw_pressure" + i, _ => p.Name });
            instances.Add(new HwInstanceNode("score" + i, score.Id, connections, new Dictionary<string, string>(), Origin: origin));
        }
        var childConnections = new Dictionary<string, string>();
        foreach (var p in child.Ports)
        {
            string signal = p.Name;
            if (p.Direction == HwPortDirection.Output) { signal = "child_" + p.Name; nodes.Add(new HwWireNode(signal, p.Type, origin)); nodes.Add(new HwOutputDriveNode("drive_" + p.Name, p.Name, signal, origin)); }
            childConnections.Add(p.Name, signal);
        }
        instances.Add(new HwInstanceNode("ordered_admission", child.Id, childConnections, new Dictionary<string, string>(), Origin: origin));
        var modules = pressure.Design.Modules.Concat(ordered.Design.Modules).ToArray();
        if (modules.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != modules.Length) throw new InvalidDataException("Duplicate pressure composition module.");
        var top = new HwModule("smt_scored_ordered_admission_projection", "smt_scored_ordered_admission_projection", ports, nodes, instances, new HwHardwareContract("scheduler.smt-scored-ordered-admission.unqualified.v1", HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var result = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", top.Id, [top, .. modules], pressure.Design.Toolchain, []));
        return result.Validated ?? throw new InvalidDataException("Scored admission HWIR refused: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }

    public static HwValidatedDesign Compile(byte[] smt, byte[] scheduler, byte[] admission, string[] hashes, string commit, HwToolchainIdentity toolchain)
    {
        // This also fail-closes on changes to the score method, dimensions and source identities.
        _ = BridgeHwirPressureOrder.Compile(smt, scheduler, admission, hashes, commit, toolchain);
        var root = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(scheduler)).GetRoot();
        var e = root.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(x => x.Identifier.ValueText == "ScoreboardEntryType" && x.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core");
        var expected = SyntaxFactory.ParseMemberDeclaration("public enum ScoreboardEntryType : byte { Free = 0, Dma = 1, OutstandingLoad = 2, OutstandingStore = 3 }")!;
        if (!e.DescendantTokens().Select(t => t.Text).SequenceEqual(expected.DescendantTokens().Select(t => t.Text)))
            throw new InvalidDataException("Scoreboard entry type encoding changed.");
        var method = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(admission)).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "GetBankPressureScore");
        var pos = method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("bank-pressure/v1|" + string.Join("|", hashes)))).ToLowerInvariant();
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", commit, "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/Admission/MicroOpScheduler.Admission.cs", "CircuitWeave.BankPressure.Projection", "YAKSys_Hybrid_CPU.Core.MicroOpScheduler", "GetBankPressureScore", digest, HwSourceRole.Primary, pos.Line + 1, pos.Character + 1);
        var u1 = new HwBitVectorType(1); var u6 = new HwBitVectorType(6); var u8 = new HwBitVectorType(8); var u32 = new HwBitVectorType(32); var u64 = new HwBitVectorType(64);
        var ports = new List<HwPortContract> { new("bank_id", HwPortDirection.Input, u32), new("score", HwPortDirection.Output, u6) };
        var nodes = new List<HwNode> {
            new HwConstantNode("minus_one", u32, new string('1', 32), origin),
            new HwConstantNode("load_type", u8, "00000010", origin), new HwConstantNode("store_type", u8, "00000011", origin),
            new HwConstantNode("zero", u6, "000000", origin), new HwConstantNode("one", u6, "000001", origin),
            new HwConstantNode("sign_limit", u64, Convert.ToString(1L << 31, 2).PadLeft(64, '0'), origin),
            new HwOperationNode("bank64", "zext", ["bank_id"], u64, Origin: origin),
            new HwOperationNode("bank_nonnegative", "ult64", ["bank64", "sign_limit"], u1, Origin: origin)
        };
        string sum = "zero";
        for (int i = 0; i < 32; i++)
        {
            string n = "entry" + i;
            ports.Add(new(n + "_value", HwPortDirection.Input, u32));
            ports.Add(new(n + "_bank", HwPortDirection.Input, u32));
            ports.Add(new(n + "_type", HwPortDirection.Input, u8));
            nodes.Add(new HwOperationNode(n + "_free", "eq", [n + "_value", "minus_one"], u1, Origin: origin));
            nodes.Add(new HwOperationNode(n + "_bank_match", "eq", [n + "_bank", "bank_id"], u1, Origin: origin));
            nodes.Add(new HwOperationNode(n + "_load", "eq", [n + "_type", "load_type"], u1, Origin: origin));
            nodes.Add(new HwOperationNode(n + "_store", "eq", [n + "_type", "store_type"], u1, Origin: origin));
            nodes.Add(new HwOperationNode(n + "_memory", "or", [n + "_load", n + "_store"], u1, Origin: origin));
            nodes.Add(new HwOperationNode(n + "_match", "and", [n + "_bank_match", n + "_memory"], u1, Origin: origin));
            nodes.Add(new HwMuxNode(n + "_active", n + "_free", "zero", "one", u6, origin));
            nodes.Add(new HwMuxNode(n + "_inc", n + "_match", n + "_active", "zero", u6, origin));
            nodes.Add(new HwOperationNode(n + "_sum", "add", [sum, n + "_inc"], u6, Origin: origin));
            sum = n + "_sum";
        }
        nodes.Add(new HwMuxNode("result", "bank_nonnegative", sum, "zero", u6, origin));
        nodes.Add(new HwOutputDriveNode("drive_score", "score", "result", origin));
        var top = new HwModule("smt_bank_pressure_projection", "smt_bank_pressure_projection", ports, nodes, [], new HwHardwareContract("scheduler.smt-bank-pressure.unqualified.v1", HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var result = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", top.Id, [top], toolchain, []));
        return result.Validated ?? throw new InvalidDataException("Bank pressure HWIR refused: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }
}
#endif
