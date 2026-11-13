#if HARDWAREBRIDGE_HWIR
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Candidate opcode outcome only; descriptor contents and downstream legality are not elaborated.</summary>
public static class BridgeHwirOpcodeLookup
{
    /// <summary>Attach one classifier to each raw slot; it does not resolve occupied-slot legality.</summary>
    public static HwValidatedDesign AttachToRawBundle(HwValidatedDesign bundle, HwValidatedDesign lookup)
    {
        ArgumentNullException.ThrowIfNull(bundle); ArgumentNullException.ThrowIfNull(lookup);
        if (bundle.Design.Toolchain != lookup.Design.Toolchain || bundle.Design.TopModule != "raw_bundle_projection" ||
            lookup.Design.TopModule != "opcode_outcome_projection" || lookup.Design.Modules.Count != 1)
            throw new InvalidDataException("Incompatible opcode and bundle candidates.");
        var top = bundle.Design.Modules.Single(m => m.Id == bundle.Design.TopModule);
        if (top.Instances.Count != 25 || top.Contract is null || bundle.Design.Modules.Any(m => m.Id == "opcode_outcome_projection"))
            throw new InvalidDataException("Unsupported raw bundle topology.");
        var ports = top.Ports.ToList(); var instances = top.Instances.ToList(); var u3 = new HwBitVectorType(3);
        for (int i = 0; i < 8; i++)
        {
            var prefix = "slot" + i + "_"; string wire = prefix + "opcode_wire", output = prefix + "opcode_outcome";
            if (!top.Nodes.OfType<HwWireNode>().Any(n => n.Id == wire)) throw new InvalidDataException("Missing slot opcode wire.");
            ports.Add(new HwPortContract(output, HwPortDirection.Output, u3));
            instances.Add(new HwInstanceNode(prefix + "opcode_lookup", "opcode_outcome_projection",
                new Dictionary<string, string> { ["opcode"] = wire, ["outcome"] = output }, new Dictionary<string,string>(), Origin:top.Origin));
        }
        var combinedTop = top with { Ports = ports, Instances = instances, Contract = top.Contract with { Ports = ports } };
        var design = bundle.Design with { Modules = bundle.Design.Modules.Select(m => m.Id == top.Id ? combinedTop : m).Append(lookup.Design.Modules.Single()).ToArray() };
        var result = new FailClosedHardwareIrValidator().Validate(design);
        return result.Validated ?? throw new InvalidDataException("Combined bundle HWIR refused: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }

    public static HwValidatedDesign Compile(byte[] stages, byte[] catalog, byte[] surface, string stagesHash, string catalogHash,
        string surfaceHash, string commit, HwToolchainIdentity toolchain)
    {
        static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        if (new[] { stages, catalog, surface }.Any(b => b.Length > 4_000_000) || Hash(stages) != stagesHash || Hash(catalog) != catalogHash ||
            Hash(surface) != surfaceHash || commit.Length != 40 || commit.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Opcode source identity differs.");
        static SyntaxNode Parse(byte[] bytes) => CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes)).GetRoot();
        var stageRoot = Parse(stages); var catalogRoot = Parse(catalog); var surfaceRoot = Parse(surface);
        var lookup = stageRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "OpcodeDescriptorLookup" &&
            c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core.Decoder");
        var method = lookup.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "TryLookup");
        const string expectedLookup = """
        {
            if (slot.Opcode == 0) { descriptor = default; failure = null; return true; }
            if (IsaV4Surface.ProhibitedOpcodes.Contains(slot.Opcode.ToString(CultureInfo.InvariantCulture)))
            { descriptor = default; failure = DecodeFailure.Create(DecodeFailureCode.ProhibitedOpcode, slot.SlotIndex, "opcode", RawSlotBytes(in slot), $"Opcode 0x{slot.Opcode:X4} belongs to the prohibited ISA v4 decode surface."); return false; }
            if (UnsupportedOptionalContours.TryGetValue(slot.Opcode, out string? contour))
            { descriptor = default; failure = DecodeFailure.Create(DecodeFailureCode.UnsupportedOpcode, slot.SlotIndex, "opcode", RawSlotBytes(in slot), $"Opcode 0x{slot.Opcode:X4} belongs to the unsupported optional {contour} contour."); return false; }
            if (GeneratedIsaCatalog.TryGetDescriptor(slot.Opcode, out descriptor)) { failure = null; return true; }
            failure = DecodeFailure.Create(DecodeFailureCode.UnknownOpcode, slot.SlotIndex, "opcode", RawSlotBytes(in slot), $"Opcode 0x{slot.Opcode:X4} has no generated ISA descriptor."); return false;
        }
        """;
        if (method.Body?.NormalizeWhitespace().ToFullString() != SyntaxFactory.ParseStatement(expectedLookup).NormalizeWhitespace().ToFullString())
            throw new InvalidDataException("Opcode lookup body changed.");
        var branches = method.Body?.Statements.OfType<IfStatementSyntax>().ToArray() ?? [];
        string[] conditions = ["slot.Opcode == 0", "IsaV4Surface.ProhibitedOpcodes.Contains(slot.Opcode.ToString(CultureInfo.InvariantCulture))",
            "UnsupportedOptionalContours.TryGetValue(slot.Opcode, out string? contour)", "GeneratedIsaCatalog.TryGetDescriptor(slot.Opcode, out descriptor)"];
        if (method.Body?.Statements.Count != 6 || branches.Length != 4 || branches.Where((b, i) => b.Condition.NormalizeWhitespace().ToFullString() != conditions[i]).Any())
            throw new InvalidDataException("Opcode lookup branch order or conditions changed.");
        static bool Returns(IfStatementSyntax branch, bool value) => branch.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>()
            .Any(r => r.Expression?.IsKind(value ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression) == true);
        static bool HasCode(SyntaxNode branch, string code) => branch.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(x => x.ToString() == "DecodeFailureCode." + code);
        if (!Returns(branches[0], true) || !Returns(branches[1], false) || !HasCode(branches[1], "ProhibitedOpcode") ||
            !Returns(branches[2], false) || !HasCode(branches[2], "UnsupportedOpcode") || !Returns(branches[3], true) ||
            !HasCode(method.Body.Statements[4], "UnknownOpcode") || method.Body.Statements[5] is not ReturnStatementSyntax ret || !ret.Expression!.IsKind(SyntaxKind.FalseLiteralExpression))
            throw new InvalidDataException("Opcode lookup outcomes changed.");
        var optional = lookup.Members.OfType<FieldDeclarationSyntax>().Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "UnsupportedOptionalContours"));
        var optionalInit = ((ObjectCreationExpressionSyntax)optional.Declaration.Variables.Single().Initializer!.Value).Initializer!;
        int[] optionalCodes = optionalInit.Expressions.Cast<AssignmentExpressionSyntax>().Select(a => ((LiteralExpressionSyntax)((ImplicitElementAccessSyntax)a.Left).ArgumentList.Arguments.Single().Expression).Token.Value)
            .Select(Convert.ToInt32).ToArray();
        var catalogType = catalogRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "GeneratedIsaCatalog" &&
            c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Arch.Generated");
        var descriptorField = catalogType.Members.OfType<FieldDeclarationSyntax>().Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "Descriptors"));
        var collection = (CollectionExpressionSyntax)descriptorField.Declaration.Variables.Single().Initializer!.Value;
        int[] descriptorCodes = collection.Elements.Cast<ExpressionElementSyntax>().Select(e => (ImplicitObjectCreationExpressionSyntax)e.Expression)
            .Select(e => ((LiteralExpressionSyntax)e.ArgumentList.Arguments[0].Expression).Token.Value).Select(Convert.ToInt32).ToArray();
        var find = catalogType.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "TryGetDescriptor");
        const string findBody = "{ foreach (GeneratedIsaDescriptor candidate in Descriptors) if (candidate.Opcode == opcode) { descriptor = candidate; return true; } descriptor = default; return false; }";
        if (find.Body?.NormalizeWhitespace().ToFullString() != SyntaxFactory.ParseStatement(findBody).NormalizeWhitespace().ToFullString())
            throw new InvalidDataException("Catalog lookup behavior changed.");
        var policyMethod = catalogType.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "CreateStaticPolicies");
        var policiesField = catalogType.Members.OfType<FieldDeclarationSyntax>().Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "StaticPolicies"));
        var getPolicy = catalogType.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "GetStaticPolicy");
        const string expectedGetPolicy = "StaticPolicies.TryGetValue(policyId, out IReadOnlySet<string>? policy) ? policy : throw new ArgumentOutOfRangeException(nameof(policyId), policyId, \"Unknown generated ISA static policy.\")";
        if (policiesField.Declaration.Variables.Single().Initializer?.Value.ToString() != "CreateStaticPolicies()" ||
            getPolicy.ExpressionBody?.Expression.NormalizeWhitespace().ToFullString() != SyntaxFactory.ParseExpression(expectedGetPolicy).NormalizeWhitespace().ToFullString() ||
            policyMethod.ExpressionBody?.Expression is not ObjectCreationExpressionSyntax policyDictionary ||
            policyDictionary.ArgumentList?.Arguments.Single().Expression.ToString() != "StringComparer.Ordinal")
            throw new InvalidDataException("Static policy lookup binding changed.");
        var policyEntry = policyMethod.DescendantNodes().OfType<AssignmentExpressionSyntax>().Single(a => a.Left is ImplicitElementAccessSyntax i &&
            i.ArgumentList.Arguments.Single().Expression is LiteralExpressionSyntax l && l.Token.ValueText == "ProhibitedOpcodes");
        var policySet = (ObjectCreationExpressionSyntax)policyEntry.Right;
        if (policySet.Type.ToString() != "HashSet<string>" || policySet.ArgumentList?.Arguments.Single().Expression.ToString() != "StringComparer.Ordinal") throw new InvalidDataException("Policy set or comparer changed.");
        int[] prohibitedCodes = policySet.Initializer!.Expressions.Cast<LiteralExpressionSyntax>().Select(s => s.Token.ValueText)
            .Where(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n.ToString(CultureInfo.InvariantCulture) == s)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var surfaceType = surfaceRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "IsaV4Surface");
        var prohibitedField = surfaceType.Members.OfType<FieldDeclarationSyntax>().Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "ProhibitedOpcodes"));
        if (prohibitedField.Declaration.Variables.Single().Initializer?.Value.ToString() != "Policy(nameof(ProhibitedOpcodes))" ||
            !surfaceType.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText == "Policy" && m.ExpressionBody?.Expression.ToString() == "GeneratedIsaCatalog.GetStaticPolicy(policyId)"))
            throw new InvalidDataException("Prohibited policy binding changed.");
        if (descriptorCodes.Length == 0 || new[] { descriptorCodes, optionalCodes, prohibitedCodes }.Any(list => list.Any(x => x is < 0 or > 65535) || list.Distinct().Count() != list.Length))
            throw new InvalidDataException("Opcode inventory is invalid or ambiguous.");
        var position = method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", commit,
            "HybridCPU_ISE/CloseToHSL/Core/Decoder/DeclarativeDecoderStages.cs", "CircuitWeave.OpcodeLookup.Projection",
            "YAKSys_Hybrid_CPU.Core.Decoder.OpcodeDescriptorLookup", "TryLookup",
            Hash(Encoding.UTF8.GetBytes("opcode-outcome/v1|" + stagesHash + "|" + catalogHash + "|" + surfaceHash)), HwSourceRole.Primary, position.Line + 1, position.Character + 1);
        var u1 = new HwBitVectorType(1); var u3 = new HwBitVectorType(3); var u16 = new HwBitVectorType(16);
        HwPortContract[] ports = [new("opcode", HwPortDirection.Input, u16), new("outcome", HwPortDirection.Output, u3)];
        var nodes = new List<HwNode>();
        string Membership(string tag, int[] values)
        {
            var matches = new List<string>();
            foreach (var (value, index) in values.Order().Select((value, index) => (value, index)))
            {
                string c = tag + "_c" + index, eq = tag + "_eq" + index;
                nodes.Add(new HwConstantNode(c, u16, Convert.ToString(value, 2).PadLeft(16, '0'), origin));
                nodes.Add(new HwOperationNode(eq, "eq", ["opcode", c], u1, Origin: origin)); matches.Add(eq);
            }
            if (matches.Count == 0) { nodes.Add(new HwConstantNode(tag + "_none", u1, "0", origin)); return tag + "_none"; }
            while (matches.Count > 1)
            {
                var next = new List<string>();
                for (int i = 0; i < matches.Count; i += 2)
                {
                    if (i + 1 == matches.Count) { next.Add(matches[i]); continue; }
                    string id = tag + "_or" + nodes.Count; nodes.Add(new HwOperationNode(id, "or", [matches[i], matches[i + 1]], u1, Origin: origin)); next.Add(id);
                }
                matches = next;
            }
            return matches[0];
        }
        string known = Membership("known", descriptorCodes), prohibited = Membership("prohibited", prohibitedCodes), unsupported = Membership("unsupported", optionalCodes);
        nodes.AddRange([new HwConstantNode("zero_opcode",u16,new string('0',16),origin),new HwConstantNode("empty_code",u3,"000",origin),
            new HwConstantNode("known_code",u3,"001",origin),new HwConstantNode("prohibited_code",u3,"010",origin),new HwConstantNode("unsupported_code",u3,"011",origin),new HwConstantNode("unknown_code",u3,"100",origin),
            new HwOperationNode("empty","eq",["opcode","zero_opcode"],u1,Origin:origin),new HwMuxNode("known_or_unknown",known,"known_code","unknown_code",u3,origin),
            new HwMuxNode("unsupported_or_rest",unsupported,"unsupported_code","known_or_unknown",u3,origin),new HwMuxNode("prohibited_or_rest",prohibited,"prohibited_code","unsupported_or_rest",u3,origin),
            new HwMuxNode("result","empty","empty_code","prohibited_or_rest",u3,origin),new HwOutputDriveNode("drive_outcome","outcome","result",origin)]);
        var module = new HwModule("opcode_outcome_projection", "opcode_outcome_projection", ports, nodes, [],
            new HwHardwareContract("decoder.opcode-outcome.unqualified.v1", HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var result = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", module.Id, [module], toolchain, []));
        return result.Validated ?? throw new InvalidDataException("Opcode outcome HWIR refused: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }
}
#endif
