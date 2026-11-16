#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Source-bounded eligibility predicate for one packet lane before execute-state side effects.</summary>
public static class BridgeHwirIssuePacketMaterializationEligibility
{
    private const string MaterializationPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/Materialization/CPU_Core.PipelineExecution.Materialization.cs";

    public static HwValidatedDesign Compile(byte[] materializationSource, byte[] packetSource, byte[] issueLaneSource,
        string[] expectedSha256, string commit, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(expectedSha256);
        ArgumentNullException.ThrowIfNull(toolchain);
        if (materializationSource is null || packetSource is null || issueLaneSource is null || expectedSha256.Length != 3 ||
            Hash(materializationSource) != expectedSha256[0] || Hash(packetSource) != expectedSha256[1] ||
            Hash(issueLaneSource) != expectedSha256[2] ||
            commit.Length != 40 || commit.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Issue-packet materialization source identity differs.");

        SyntaxNode Parse(byte[] bytes)
        {
            var root = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes)).GetRoot();
            if (root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Issue-packet materialization source contains syntax errors.");
            return root;
        }
        static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));
        SyntaxNode matRoot = Parse(materializationSource), packetRoot = Parse(packetSource), laneRoot = Parse(issueLaneSource);
        MethodDeclarationSyntax method = matRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "ShouldMaterializeIssuePacketLane");
        string body = Tokens(method);
        if (!body.Contains("laneIndex<4", StringComparison.Ordinal) ||
            !body.Contains("GetPhysicalLane(laneIndex).IsOccupied", StringComparison.Ordinal) ||
            !body.Contains("executableNonScalarPhysicalLaneMask&(1<<laneIndex)", StringComparison.Ordinal) ||
            !body.Contains("return", StringComparison.Ordinal))
            throw new InvalidDataException("ShouldMaterializeIssuePacketLane source contract changed.");
        StructDeclarationSyntax packet = packetRoot.DescendantNodes().OfType<StructDeclarationSyntax>()
            .Single(t => t.Identifier.ValueText == "BundleIssuePacket");
        StructDeclarationSyntax laneType = laneRoot.DescendantNodes().OfType<StructDeclarationSyntax>()
            .Single(t => t.Identifier.ValueText == "IssuePacketLane");
        if (!packet.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText == "GetPhysicalLane") ||
            !laneType.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.ValueText == "IsOccupied"))
            throw new InvalidDataException("IssuePacket lane occupancy source members missing.");

        var span = method.Identifier.GetLocation().GetLineSpan();
        string identity = Hash(Encoding.UTF8.GetBytes("issue-packet-materialization-eligibility/v1|" +
            expectedSha256[0] + "|" + Tokens(method)));
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", commit,
            MaterializationPath, "CircuitWeave.Captured.IssuePacketMaterializationEligibility",
            "YAKSys_Hybrid_CPU.CPU_Core", "ShouldMaterializeIssuePacketLane", identity,
            HwSourceRole.Primary, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
        var bit = new HwBitVectorType(1); var u8 = new HwBitVectorType(8);
        var ports = new List<HwPortContract>
        {
            new("lane_index", HwPortDirection.Input, u8),
            new("occupied", HwPortDirection.Input, bit),
            new("executable_non_scalar_mask", HwPortDirection.Input, u8),
            new("materialize", HwPortDirection.Output, bit)
        };
        var nodes = new List<HwNode>();
        void C(string id, HwBitVectorType type, uint value) => nodes.Add(new HwConstantNode(id, type,
            Convert.ToString(value, 2).PadLeft(type.Width, '0'), origin));
        C("zero8", u8, 0); C("one1", bit, 1); C("zero1", bit, 0);
        C("scalar_lane_mask", u8, 252);
        nodes.Add(new HwOperationNode("lane_high_bits", "and", ["lane_index", "scalar_lane_mask"], u8, Origin: origin));
        nodes.Add(new HwOperationNode("is_scalar_lane", "eq", ["lane_high_bits", "zero8"], bit, Origin: origin));
        C("shift_mask", u8, 31);
        nodes.Add(new HwOperationNode("masked_shift_index", "and", ["lane_index", "shift_mask"], u8, Origin: origin));
        string auxEligible = "zero1";
        for (byte lane = 0; lane < 8; lane++)
        {
            C($"lane{lane}_index", u8, lane); C($"lane{lane}_bit", u8, (uint)(1 << lane));
            nodes.Add(new HwOperationNode($"lane{lane}_matches", "eq", ["masked_shift_index", $"lane{lane}_index"], bit, Origin: origin));
            nodes.Add(new HwOperationNode($"lane{lane}_selected_bits", "and", ["executable_non_scalar_mask", $"lane{lane}_bit"], u8, Origin: origin));
            nodes.Add(new HwOperationNode($"lane{lane}_selected_zero", "eq", [$"lane{lane}_selected_bits", "zero8"], bit, Origin: origin));
            nodes.Add(new HwOperationNode($"lane{lane}_selected", "xor", [$"lane{lane}_selected_zero", "one1"], bit, Origin: origin));
            nodes.Add(new HwOperationNode($"lane{lane}_eligible", "and", [$"lane{lane}_matches", $"lane{lane}_selected"], bit, Origin: origin));
            nodes.Add(new HwOperationNode($"aux_or_{lane}", "or", [auxEligible, $"lane{lane}_eligible"], bit, Origin: origin));
            auxEligible = $"aux_or_{lane}";
        }
        nodes.Add(new HwMuxNode("lane_class_eligibility", "is_scalar_lane", "one1", auxEligible, bit, origin));
        nodes.Add(new HwOperationNode("eligibility", "and", ["occupied", "lane_class_eligibility"], bit,
            Origin: origin));
        nodes.Add(new HwOutputDriveNode("drive_materialize", "materialize", "eligibility", origin));
        var module = new HwModule("issue_packet_materialization_eligibility",
            "issue_packet_materialization_eligibility", ports, nodes, [],
            new HwHardwareContract("scheduler.issue-packet.materialization-eligibility.source-bounded.v2",
                HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var result = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", module.Id,
            [module], toolchain, []));
        return result.Validated ?? throw new InvalidDataException("Issue-packet materialization eligibility HWIR refused: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
