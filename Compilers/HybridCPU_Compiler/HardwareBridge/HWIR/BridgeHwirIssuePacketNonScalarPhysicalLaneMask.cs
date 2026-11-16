#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>
/// Source-bounded combinational projection of ResolveExecutableNonScalarPhysicalLaneMask.
/// Lane type facts and the dependency summary matrices are explicit inputs because
/// their runtime producers have not yet been composed into this module.
/// </summary>
public static class BridgeHwirIssuePacketNonScalarPhysicalLaneMask
{
    private const string StagePath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.PipelineExecution.StageFlow.cs";
    private const string DependencyPath = "HybridCPU_ISE/CloseToHSL/Core/Decoder/DecodedBundleDescriptor.cs";

    private static readonly string[] ExpectedHashes =
    [
        "672e9d2523dd04068dc63650a7f52a7df67a904abe57c5639abb3b8b89d2317e",
        "ad3878ae15aad02e44f181ad7abb26d40b7da13ca6a7e46ae003b4a8a5e98c2f",
        "030a4262d0d57db5185b70e20ac3c61cce37bd471aa10116ce309b30971d665c",
        "9a46971ca2c41a52611a4c7fb65991df90c7645d1e25b21cbad63c38268b049c"
    ];

    private static readonly string[] MatrixNames = ["raw", "waw", "war", "control", "memory", "system", "pinned"];
    private static readonly string[] TypeFacts = ["is_trap", "is_load_store", "is_assist", "is_matrix_tile", "is_branch", "is_sys_event", "is_stream_control", "is_csr", "is_vmx", "is_vconfig"];

    public static HwValidatedDesign Compile(byte[] stageFlowSource, byte[] dependencySummarySource,
        byte[] issueLaneSource, byte[] packetSource, string[] expectedSha256, string commit,
        HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(expectedSha256);
        ArgumentNullException.ThrowIfNull(toolchain);
        if (stageFlowSource is null || dependencySummarySource is null || issueLaneSource is null || packetSource is null ||
            expectedSha256.Length != 4 || !Hash(stageFlowSource).Equals(expectedSha256[0], StringComparison.Ordinal) ||
            !Hash(dependencySummarySource).Equals(expectedSha256[1], StringComparison.Ordinal) ||
            !Hash(issueLaneSource).Equals(expectedSha256[2], StringComparison.Ordinal) ||
            !Hash(packetSource).Equals(expectedSha256[3], StringComparison.Ordinal) ||
            !expectedSha256.SequenceEqual(ExpectedHashes, StringComparer.Ordinal) ||
            commit != "1bfe41a49cf11c80fa6c6aee87accb54b78b331f")
            throw new InvalidDataException("Non-scalar resolver source bytes differ from the pinned model snapshot.");

        static SyntaxNode Parse(byte[] bytes)
        {
            SyntaxNode root = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes)).GetRoot();
            if (root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Pinned non-scalar resolver input has C# syntax errors.");
            return root;
        }
        static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));
        SyntaxNode stage = Parse(stageFlowSource), dependency = Parse(dependencySummarySource);
        SyntaxNode issueLane = Parse(issueLaneSource), packet = Parse(packetSource);
        MethodDeclarationSyntax Resolve = Method(stage, "ResolveExecutableNonScalarPhysicalLaneMask");
        MethodDeclarationSyntax HazardScan = Method(stage, "HasIssueLaneHazardAgainstSlotMask");
        MethodDeclarationSyntax Executable = Method(stage, "IsExecutableNonScalarIssueLane");
        MethodDeclarationSyntax Singleton = Method(stage, "IsExecutableSystemSingletonIssueLaneMicroOp");
        MethodDeclarationSyntax Pair = Method(dependency, "QueryPairHazard");
        MethodDeclarationSyntax Classify = Method(dependency, "ClassifyPairHazard");
        StructDeclarationSyntax laneType = issueLane.DescendantNodes().OfType<StructDeclarationSyntax>().Single(t => t.Identifier.ValueText == "IssuePacketLane");
        StructDeclarationSyntax packetType = packet.DescendantNodes().OfType<StructDeclarationSyntax>().Single(t => t.Identifier.ValueText == "BundleIssuePacket");
        StructDeclarationSyntax summaryType = dependency.DescendantNodes().OfType<StructDeclarationSyntax>().Single(t => t.Identifier.ValueText == "DecodedBundleDependencySummary");
        var shapeFailures = new List<string>();
        void Shape(bool condition, string label) { if (!condition) shapeFailures.Add(label); }
        Shape(laneType.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.ValueText == "IsOccupied"), "IssuePacketLane.IsOccupied");
        Shape(laneType.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.ValueText == "CountsTowardScalarProjection"), "IssuePacketLane.CountsTowardScalarProjection");
        Shape(laneType.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.ValueText == "PhysicalLaneIndex"), "IssuePacketLane.PhysicalLaneIndex");
        Shape(laneType.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.ValueText == "SlotIndex"), "IssuePacketLane.SlotIndex");
        Shape(packetType.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText == "GetPhysicalLane"), "BundleIssuePacket.GetPhysicalLane");
        Shape(summaryType.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.ValueText == "RawDependencyMask"), "DecodedBundleDependencySummary.RawDependencyMask");
        Shape(Tokens(Resolve).Contains("laneIndex=4;laneIndex<8;laneIndex++", StringComparison.Ordinal), "resolver lane scan");
        Shape(Tokens(HazardScan).Contains("!=Core.HazardTriageClass.Safe", StringComparison.Ordinal), "resolver fail-closed triage");
        Shape(Tokens(HazardScan).Contains("peerSlotIndex<8", StringComparison.Ordinal), "peer scan");
        Shape(Tokens(Executable).Contains("IsNonScalarSelection", StringComparison.Ordinal), "non-scalar selection");
        Shape(Tokens(Executable).Contains("PhysicalLaneIndexis>=4and<8", StringComparison.Ordinal), "trap physical range");
        Shape(Tokens(Singleton).Contains("VConfigMicroOp", StringComparison.Ordinal), "system singleton set");
        Shape(Tokens(Pair).Contains("pairBits", StringComparison.Ordinal), "directed pair lookup");
        Shape(Tokens(Classify).Contains("HazardTriageClass.NeedsRuntimeCheck", StringComparison.Ordinal), "runtime triage table");
        if (shapeFailures.Count != 0) throw new InvalidDataException("Pinned non-scalar resolver helper shape changed: " + string.Join(", ", shapeFailures));

        string[] hashes = [Hash(stageFlowSource), Hash(dependencySummarySource), Hash(issueLaneSource), Hash(packetSource)];
        string resolveTokens = Tokens(Resolve) + Tokens(HazardScan) + Tokens(Executable) + Tokens(Singleton);
        string pairTokens = Tokens(Pair) + Tokens(Classify);
        string rootIdentity = Hash(Encoding.UTF8.GetBytes("issue-packet-nonscalar-resolver/v1|" + string.Join("|", hashes) + "|" + resolveTokens));
        string pairIdentity = Hash(Encoding.UTF8.GetBytes("issue-packet-hazard-query/v1|" + hashes[1] + "|" + pairTokens));
        HwSourceOrigin rootOrigin = new("https://github.com/yuriyyak23/HybridCPU-v2", commit, StagePath,
            "CircuitWeave.Captured.IssuePacketNonScalarPhysicalLaneMask", "YAKSys_Hybrid_CPU.CPU_Core",
            "ResolveExecutableNonScalarPhysicalLaneMask", rootIdentity, HwSourceRole.Primary,
            Line(Resolve), Column(Resolve));
        HwSourceOrigin laneOrigin = rootOrigin with { Member = "IsExecutableNonScalarIssueLane", IdentityDigest = rootIdentity, Line = Line(Executable), Column = Column(Executable) };
        HwSourceOrigin singletonOrigin = rootOrigin with { Member = "IsExecutableSystemSingletonIssueLaneMicroOp", IdentityDigest = rootIdentity, Line = Line(Singleton), Column = Column(Singleton) };
        HwSourceOrigin hazardOrigin = rootOrigin with { Member = "HasIssueLaneHazardAgainstSlotMask", IdentityDigest = rootIdentity, Line = Line(HazardScan), Column = Column(HazardScan) };
        HwSourceOrigin dependencyOrigin = new("https://github.com/yuriyyak23/HybridCPU-v2", commit, DependencyPath,
            "CircuitWeave.Captured.DecodedBundleDependencySummary", "YAKSys_Hybrid_CPU.Core",
            "QueryPairHazard+ClassifyPairHazard", pairIdentity, HwSourceRole.Primary,
            Line(Pair), Column(Pair));

        var bit = new HwBitVectorType(1); var u8 = new HwBitVectorType(8); var u64 = new HwBitVectorType(64);
        var ports = new List<HwPortContract>
        {
            new("dependency_summary_present", HwPortDirection.Input, bit),
            new("scalar_issue_mask", HwPortDirection.Input, u8)
        };
        foreach (string matrix in MatrixNames) ports.Add(new(matrix + "_dependency_mask", HwPortDirection.Input, u64));
        for (byte lane = 4; lane < 8; lane++)
        {
            string prefix = "lane" + lane + "_";
            ports.Add(new(prefix + "physical_lane_index", HwPortDirection.Input, u8));
            ports.Add(new(prefix + "slot_index", HwPortDirection.Input, u8));
            ports.Add(new(prefix + "occupied", HwPortDirection.Input, bit));
            ports.Add(new(prefix + "counts_toward_scalar_projection", HwPortDirection.Input, bit));
            ports.Add(new(prefix + "microop_present", HwPortDirection.Input, bit));
            foreach (string fact in TypeFacts) ports.Add(new(prefix + fact, HwPortDirection.Input, bit));
        }
        ports.Add(new("executable_non_scalar_physical_lane_mask", HwPortDirection.Output, u8));

        var nodes = new List<HwNode>();
        void C(string id, HwBitVectorType type, ulong value, HwSourceOrigin? origin = null) =>
            nodes.Add(new HwConstantNode(id, type, Convert.ToString(unchecked((long)value), 2).TrimStart('-').PadLeft(type.Width, '0')[^type.Width..], rootOrigin));
        void O(string id, string opcode, string a, string b, HwBitVectorType type, HwSourceOrigin? origin = null) =>
            nodes.Add(new HwOperationNode(id, opcode, [a, b], type, Origin: rootOrigin));
        void U(string id, string opcode, string input, HwBitVectorType type, HwSourceOrigin? origin = null) =>
            nodes.Add(new HwOperationNode(id, opcode, [input], type, Origin: rootOrigin));
        void Mux(string id, string condition, string whenTrue, string whenFalse, HwBitVectorType type, HwSourceOrigin? origin = null) =>
            nodes.Add(new HwMuxNode(id, condition, whenTrue, whenFalse, type, rootOrigin));

        C("zero1", bit, 0); C("one1", bit, 1); C("zero8", u8, 0); C("one64", u64, 1);
        C("zero64", u64, 0); C("one64_constant", u64, 1); C("three64", u64, 3);
        C("four64", u64, 4); C("eight64", u64, 8); C("peer_group_shift", u64, 3);
        C("slot_limit64", u64, 8); C("all_mask", u8, 255);

        string[] matrixInputs = MatrixNames.Select(n => n + "_dependency_mask").ToArray();
        string aggregateMatrix = matrixInputs[0];
        for (int i = 1; i < matrixInputs.Length; i++)
        {
            string next = "all_hazards_" + i;
            O(next, "or", aggregateMatrix, matrixInputs[i], u64, dependencyOrigin);
            aggregateMatrix = next;
        }

        string acceptedSlotMask = "zero8";
        string outputMask = "zero8";
        for (byte lane = 4; lane < 8; lane++)
        {
            string p = "lane" + lane + "_";
            O(p + "not_scalar", "xor", p + "counts_toward_scalar_projection", "one1", bit, laneOrigin);
            O(p + "non_scalar_occupied", "and", p + "occupied", p + "not_scalar", bit, laneOrigin);
            O(p + "typed_trap", "and", p + "microop_present", p + "is_trap", bit, laneOrigin);
            string trapPhysical64 = p + "physical_lane_index64";
            U(trapPhysical64, "zext", p + "physical_lane_index", u64, laneOrigin);
            O(p + "trap_ge4", "ult64", trapPhysical64, "four64", bit, laneOrigin);
            O(p + "trap_not_ge4", "xor", p + "trap_ge4", "one1", bit, laneOrigin);
            O(p + "trap_lt8", "ult64", trapPhysical64, "slot_limit64", bit, laneOrigin);
            O(p + "trap_range", "and", p + "trap_not_ge4", p + "trap_lt8", bit, laneOrigin);

            string nonTrapType;
            if (lane is 4 or 5)
            {
                O(p + "typed_load_store", "and", p + "microop_present", p + "is_load_store", bit, laneOrigin);
                O(p + "typed_assist", "and", p + "microop_present", p + "is_assist", bit, laneOrigin);
                O(p + "load_store_or_assist", "or", p + "typed_load_store", p + "typed_assist", bit, laneOrigin);
                nonTrapType = p + "load_store_or_assist";
            }
            else if (lane == 6)
            {
                O(p + "typed_matrix_tile", "and", p + "microop_present", p + "is_matrix_tile", bit, laneOrigin);
                nonTrapType = p + "typed_matrix_tile";
            }
            else
            {
                string[] singletonFacts = ["is_branch", "is_sys_event", "is_stream_control", "is_csr", "is_vmx", "is_vconfig"];
                string combined = "zero1";
                foreach (string fact in singletonFacts)
                {
                    string typed = p + "typed_" + fact;
                    O(typed, "and", p + "microop_present", p + fact, bit, singletonOrigin);
                    string next = p + "singleton_types_" + fact;
                    O(next, "or", combined, typed, bit, singletonOrigin);
                    combined = next;
                }
                nonTrapType = combined;
            }
            Mux(p + "trap_or_nontrap_type", p + "typed_trap", p + "trap_range", nonTrapType, bit, laneOrigin);
            U(p + "slot_index64", "zext", p + "slot_index", u64, laneOrigin);
            O(p + "slot_in_range", "ult64", p + "slot_index64", "slot_limit64", bit, laneOrigin);
            O(p + "candidate_type_and_occupancy", "and", p + "trap_or_nontrap_type", p + "non_scalar_occupied", bit, laneOrigin);
            O(p + "candidate_valid0", "and", p + "candidate_type_and_occupancy", p + "slot_in_range", bit, laneOrigin);
            O(p + "candidate_valid", "and", "dependency_summary_present", p + "candidate_valid0", bit, rootOrigin);

            // The source queries all seven matrices in both directions. Since every
            // non-zero pair classifies as HardReject or NeedsRuntimeCheck, the exact
            // observable "triage != Safe" reduction is a single pair-bit intersection.
            string slotShift = p + "slot_times_eight";
            O(slotShift, "shl64_mask6", p + "slot_index64", "peer_group_shift", u64, dependencyOrigin);
            string peerHazards = "zero8";
            for (byte peer = 0; peer < 8; peer++)
            {
                string q = p + "peer" + peer + "_";
                C(q + "index8", u8, peer); C(q + "index64", u64, peer);
                C(q + "onehot8", u8, 1UL << peer);
                O(q + "peer_times_eight", "shl64_mask6", q + "index64", "peer_group_shift", u64, dependencyOrigin);
                O(q + "forward_index", "add", slotShift, q + "index64", u64, dependencyOrigin);
                O(q + "reverse_index", "add", q + "peer_times_eight", p + "slot_index64", u64, dependencyOrigin);
                O(q + "forward_bit", "shl64_mask6", "one64_constant", q + "forward_index", u64, dependencyOrigin);
                O(q + "reverse_bit", "shl64_mask6", "one64_constant", q + "reverse_index", u64, dependencyOrigin);
                O(q + "pair_bits", "or", q + "forward_bit", q + "reverse_bit", u64, dependencyOrigin);
                O(q + "hazard_bits", "and", aggregateMatrix, q + "pair_bits", u64, dependencyOrigin);
                O(q + "hazard_bits_zero", "eq", q + "hazard_bits", "zero64", bit, dependencyOrigin);
                O(q + "has_pair_hazard", "xor", q + "hazard_bits_zero", "one1", bit, dependencyOrigin);
                O(q + "same_slot", "eq", p + "slot_index", q + "index8", bit, hazardOrigin);
                O(q + "different_slot", "xor", q + "same_slot", "one1", bit, hazardOrigin);
                O(q + "not_self_hazard", "and", q + "has_pair_hazard", q + "different_slot", bit, hazardOrigin);
                Mux(q + "peer_hazard_bit", q + "not_self_hazard", q + "onehot8", "zero8", u8, hazardOrigin);
                string next = q + "peer_hazard_mask";
                O(next, "or", peerHazards, q + "peer_hazard_bit", u8, hazardOrigin);
                peerHazards = next;
            }

            O(p + "scalar_peer_hazards", "and", peerHazards, "scalar_issue_mask", u8, hazardOrigin);
            O(p + "scalar_peer_hazards_zero", "eq", p + "scalar_peer_hazards", "zero8", bit, hazardOrigin);
            O(p + "accepted_peer_hazards", "and", peerHazards, acceptedSlotMask, u8, hazardOrigin);
            O(p + "accepted_peer_hazards_zero", "eq", p + "accepted_peer_hazards", "zero8", bit, hazardOrigin);
            O(p + "hazards_clear", "and", p + "scalar_peer_hazards_zero", p + "accepted_peer_hazards_zero", bit, rootOrigin);
            O(p + "accepted", "and", p + "candidate_valid", p + "hazards_clear", bit, rootOrigin);
            O(p + "slot_onehot64", "shl64_mask6", "one64_constant", p + "slot_index64", u64, rootOrigin);
            U(p + "slot_onehot8", "trunc", p + "slot_onehot64", u8, rootOrigin);
            Mux(p + "accepted_slot_bits", p + "accepted", p + "slot_onehot8", "zero8", u8, rootOrigin);
            string nextAcceptedSlots = p + "accepted_slots_after";
            O(nextAcceptedSlots, "or", acceptedSlotMask, p + "accepted_slot_bits", u8, rootOrigin);
            acceptedSlotMask = nextAcceptedSlots;

            C(p + "lane_onehot", u8, 1UL << lane);
            Mux(p + "output_bit", p + "accepted", p + "lane_onehot", "zero8", u8, rootOrigin);
            string nextOutput = p + "output_after";
            O(nextOutput, "or", outputMask, p + "output_bit", u8, rootOrigin);
            outputMask = nextOutput;
        }
        nodes.Add(new HwOutputDriveNode("drive_executable_non_scalar_physical_lane_mask",
            "executable_non_scalar_physical_lane_mask", outputMask, rootOrigin));
        var module = new HwModule("issue_packet_nonscalar_physical_lane_mask",
            "issue_packet_nonscalar_physical_lane_mask", ports, nodes, [],
            new HwHardwareContract("scheduler.issue-packet.nonscalar-physical-lane-mask.exact-source.v1",
                HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), rootOrigin);
        var design = new HwDesign("hwir/v0.1", module.Id, [module], toolchain, []);
        var result = new FailClosedHardwareIrValidator().Validate(design);
        return result.Validated ?? throw new InvalidDataException("Non-scalar resolver HWIR refused: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }

    private static MethodDeclarationSyntax Method(SyntaxNode root, string name) => root.DescendantNodes()
        .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);
    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    private static int Column(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
