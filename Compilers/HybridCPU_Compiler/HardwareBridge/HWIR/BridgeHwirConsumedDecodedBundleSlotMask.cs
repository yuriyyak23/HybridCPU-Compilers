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
/// Source-bounded combinational projection of ResolveConsumedDecodedBundleSlotMask.
/// Lane and foreground transport descriptor facts are explicit input boundaries;
/// this module does not prove that both fact sets came from the same runtime cycle.
/// </summary>
public static class BridgeHwirConsumedDecodedBundleSlotMask
{
    private static readonly string[] RelativePaths =
    [
        "HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.PipelineExecution.StageFlow.cs",
        "HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/Materialization/CPU_Core.PipelineExecution.Materialization.cs",
        "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Core/RuntimeClusterAdmissionPreparation.BundleIssuePacket.cs",
        "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Core/RuntimeClusterAdmissionPreparation.IssuePacketLane.cs",
        "HybridCPU_ISE/CloseToHSL/Core/Decoder/DecodedBundleDescriptor.cs"
    ];

    private static readonly string[] ExpectedHashes =
    [
        "672e9d2523dd04068dc63650a7f52a7df67a904abe57c5639abb3b8b89d2317e",
        "8d265200a0faabc52f7c5b62eea7fa04a938f9fe1b8f61326aa3bed83bc39328",
        "9a46971ca2c41a52611a4c7fb65991df90c7645d1e25b21cbad63c38268b049c",
        "030a4262d0d57db5185b70e20ac3c61cce37bd471aa10116ce309b30971d665c",
        "ad3878ae15aad02e44f181ad7abb26d40b7da13ca6a7e46ae003b4a8a5e98c2f"
    ];

    public static HwValidatedDesign Compile(byte[][] sourceBytes, string commit, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        ArgumentNullException.ThrowIfNull(toolchain);
        if (sourceBytes.Length != ExpectedHashes.Length || commit.Length != 40 || commit.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Consumed-slot projection source set or commit is invalid.");

        string[] hashes = sourceBytes.Select(bytes => Hash(bytes)).ToArray();
        if (!hashes.SequenceEqual(ExpectedHashes, StringComparer.Ordinal))
            throw new InvalidDataException("Consumed-slot projection source bytes differ from pinned inputs.");

        SyntaxNode[] roots = sourceBytes.Select(bytes =>
        {
            var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Consumed-slot projection input contains C# syntax errors.");
            return tree.GetRoot();
        }).ToArray();
        static MethodDeclarationSyntax Method(SyntaxNode root, string name) => root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);
        static PropertyDeclarationSyntax Property(SyntaxNode root, string name) => root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>().Single(p => p.Identifier.ValueText == name);

        MethodDeclarationSyntax consume = Method(roots[0], "ResolveConsumedDecodedBundleSlotMask");
        MethodDeclarationSyntax shouldMaterialize = Method(roots[1], "ShouldMaterializeIssuePacketLane");
        MethodDeclarationSyntax getPhysicalLane = Method(roots[2], "GetPhysicalLane");
        PropertyDeclarationSyntax laneOccupied = Property(roots[3], "IsOccupied");
        PropertyDeclarationSyntax laneSlotIndex = Property(roots[3], "SlotIndex");
        PropertyDeclarationSyntax descriptorValid = Property(roots[4], "IsValid");
        MethodDeclarationSyntax descriptorEmptyNop = Method(roots[4], "GetRuntimeExecutionIsEmptyOrNop");

        string consumeTokens = Tokens(consume);
        foreach (string required in new[] { "ReadCurrentForegroundDecodedBundleTransportFacts", "ShouldMaterializeIssuePacketLane",
                     "GetPhysicalLane", "IsOccupied", "SlotIndex", "transportFacts.Slots.Length", "IsValid",
                     "GetRuntimeExecutionIsEmptyOrNop", "consumedSlotMask|=(byte)(1<<lane.SlotIndex)" })
            if (!consumeTokens.Contains(required, StringComparison.Ordinal))
                throw new InvalidDataException("Consumed-slot source contract changed: missing " + required);
        string materializeTokens = Tokens(shouldMaterialize);
        if (!materializeTokens.Contains("laneIndex<4", StringComparison.Ordinal) ||
            !materializeTokens.Contains("issuePacket.GetPhysicalLane(laneIndex).IsOccupied", StringComparison.Ordinal) ||
            !materializeTokens.Contains("executableNonScalarPhysicalLaneMask&(1<<laneIndex)", StringComparison.Ordinal))
            throw new InvalidDataException("Issue-packet lane materialization helper contract changed.");
        if (!Tokens(descriptorValid).Contains("MicroOp!=null", StringComparison.Ordinal) ||
            !Tokens(descriptorEmptyNop).Contains("MicroOp==null", StringComparison.Ordinal) ||
            !Tokens(descriptorEmptyNop).Contains("MicroOpisNopMicroOp", StringComparison.Ordinal))
            throw new InvalidDataException("Decoded-slot runtime validity/empty-NOP contract changed.");
        _ = getPhysicalLane;
        _ = laneOccupied;
        _ = laneSlotIndex;

        HwSourceOrigin Origin(int source, SyntaxNode node, string symbol, string member)
        {
            Location declaration = node switch
            {
                MethodDeclarationSyntax method => method.Identifier.GetLocation(),
                PropertyDeclarationSyntax property => property.Identifier.GetLocation(),
                _ => node.GetLocation()
            };
            var line = declaration.GetLineSpan().StartLinePosition;
            string identity = Hash(Encoding.UTF8.GetBytes("consumed-decoded-slot-mask/v1|" + hashes[source] + "|" + Tokens(node)));
            return new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", commit, RelativePaths[source],
                "CircuitWeave.Captured.ResolveConsumedDecodedBundleSlotMask", symbol, member, identity,
                HwSourceRole.Primary, line.Line + 1, line.Character + 1);
        }

        HwSourceOrigin consumeOrigin = Origin(0, consume, "YAKSys_Hybrid_CPU.Processor.CPU_Core", "ResolveConsumedDecodedBundleSlotMask");
        HwSourceOrigin materializeOrigin = Origin(1, shouldMaterialize, "YAKSys_Hybrid_CPU.Processor.CPU_Core", "ShouldMaterializeIssuePacketLane");
        HwSourceOrigin laneOrigin = Origin(2, getPhysicalLane, "YAKSys_Hybrid_CPU.Core.BundleIssuePacket", "GetPhysicalLane");
        HwSourceOrigin occupiedOrigin = Origin(3, laneOccupied, "YAKSys_Hybrid_CPU.Core.IssuePacketLane", "IsOccupied");
        HwSourceOrigin slotIndexOrigin = Origin(3, laneSlotIndex, "YAKSys_Hybrid_CPU.Core.IssuePacketLane", "SlotIndex");
        HwSourceOrigin validOrigin = Origin(4, descriptorValid, "YAKSys_Hybrid_CPU.Core.DecodedBundleSlotDescriptor", "IsValid");
        HwSourceOrigin emptyNopOrigin = Origin(4, descriptorEmptyNop, "YAKSys_Hybrid_CPU.Core.DecodedBundleSlotDescriptor", "GetRuntimeExecutionIsEmptyOrNop");

        var bit = new HwBitVectorType(1);
        var u8 = new HwBitVectorType(8);
        var u32 = new HwBitVectorType(32);
        var u64 = new HwBitVectorType(64);
        var ports = new List<HwPortContract>
        {
            new("executable_non_scalar_physical_lane_mask", HwPortDirection.Input, u8)
        };
        for (int lane = 0; lane < 8; lane++)
        {
            ports.Add(new HwPortContract($"lane{lane}_occupied", HwPortDirection.Input, bit));
            ports.Add(new HwPortContract($"lane{lane}_slot_index", HwPortDirection.Input, u8));
        }
        ports.Add(new HwPortContract("transport_slots_length", HwPortDirection.Input, u32));
        ports.Add(new HwPortContract("transport_slot_valid_mask", HwPortDirection.Input, u8));
        ports.Add(new HwPortContract("transport_slot_runtime_empty_or_nop_mask", HwPortDirection.Input, u8));
        ports.Add(new HwPortContract("consumed_decoded_bundle_slot_mask", HwPortDirection.Output, u8));

        var nodes = new List<HwNode>();
        void Constant(string id, HwBitVectorType type, ulong value, HwSourceOrigin origin) =>
            nodes.Add(new HwConstantNode(id, type, Convert.ToString((long)value, 2).PadLeft(type.Width, '0'), consumeOrigin));
        Constant("zero_bit", bit, 0, consumeOrigin);
        Constant("one_bit", bit, 1, consumeOrigin);
        Constant("zero8", u8, 0, consumeOrigin);
        nodes.Add(new HwOperationNode("slot_length64", "zext", ["transport_slots_length"], u64, Origin: consumeOrigin));

        string[] slotEnabled = new string[8];
        for (int slot = 0; slot < 8; slot++)
        {
            string suffix = slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string mask = "slot" + suffix + "_mask";
            string slot64 = "slot" + suffix + "_index64";
            string length = "slot" + suffix + "_inside_transport";
            Constant(mask, u8, (ulong)(1 << slot), validOrigin);
            Constant(slot64, u64, (ulong)slot, consumeOrigin);
            nodes.Add(new HwOperationNode(length, "ult64", [slot64, "slot_length64"], bit, Origin: consumeOrigin));

            string validBits = "slot" + suffix + "_valid_bits";
            string validZero = "slot" + suffix + "_valid_zero";
            string valid = "slot" + suffix + "_valid";
            nodes.Add(new HwOperationNode(validBits, "and", ["transport_slot_valid_mask", mask], u8, Origin: consumeOrigin));
            nodes.Add(new HwOperationNode(validZero, "eq", [validBits, "zero8"], bit, Origin: consumeOrigin));
            nodes.Add(new HwOperationNode(valid, "xor", [validZero, "one_bit"], bit, Origin: consumeOrigin));

            string emptyBits = "slot" + suffix + "_empty_nop_bits";
            string nonempty = "slot" + suffix + "_runtime_nonempty";
            nodes.Add(new HwOperationNode(emptyBits, "and", ["transport_slot_runtime_empty_or_nop_mask", mask], u8, Origin: consumeOrigin));
            nodes.Add(new HwOperationNode(nonempty, "eq", [emptyBits, "zero8"], bit, Origin: consumeOrigin));
            string validAndNonempty = "slot" + suffix + "_valid_nonempty";
            slotEnabled[slot] = "slot" + suffix + "_enabled";
            nodes.Add(new HwOperationNode(validAndNonempty, "and", [valid, nonempty], bit, Origin: consumeOrigin));
            nodes.Add(new HwOperationNode(slotEnabled[slot], "and", [validAndNonempty, length], bit, Origin: consumeOrigin));
        }

        string[] materialized = new string[8];
        for (int lane = 0; lane < 8; lane++)
        {
            string laneText = lane.ToString(System.Globalization.CultureInfo.InvariantCulture);
            materialized[lane] = "lane" + laneText + "_materialized";
            if (lane < 4)
                materialized[lane] = "lane" + laneText + "_occupied";
            else
            {
                string laneMask = "lane" + laneText + "_mask";
                string selectedBits = "lane" + laneText + "_selected_bits";
                string selectedZero = "lane" + laneText + "_selected_zero";
                string selected = "lane" + laneText + "_selected";
                Constant(laneMask, u8, (ulong)(1 << lane), materializeOrigin);
                nodes.Add(new HwOperationNode(selectedBits, "and", ["executable_non_scalar_physical_lane_mask", laneMask], u8, Origin: consumeOrigin));
                nodes.Add(new HwOperationNode(selectedZero, "eq", [selectedBits, "zero8"], bit, Origin: consumeOrigin));
                nodes.Add(new HwOperationNode(selected, "xor", [selectedZero, "one_bit"], bit, Origin: consumeOrigin));
                nodes.Add(new HwOperationNode(materialized[lane], "and", ["lane" + laneText + "_occupied", selected], bit, Origin: consumeOrigin));
            }
        }

        string packed = "zero8";
        for (int slot = 0; slot < 8; slot++)
        {
            string suffix = slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string slotIndex = "slot" + suffix + "_index8";
            Constant(slotIndex, u8, (ulong)slot, slotIndexOrigin);
            string accumulated = "zero_bit";
            for (int lane = 0; lane < 8; lane++)
            {
                string laneText = lane.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string matches = "slot" + suffix + "_lane" + laneText + "_matches";
                string selected = "slot" + suffix + "_lane" + laneText + "_selected";
                nodes.Add(new HwOperationNode(matches, "eq", ["lane" + laneText + "_slot_index", slotIndex], bit, Origin: consumeOrigin));
                nodes.Add(new HwOperationNode(selected, "and", [materialized[lane], matches], bit, Origin: consumeOrigin));
                string eligible = "slot" + suffix + "_lane" + laneText + "_eligible";
                nodes.Add(new HwOperationNode(eligible, "and", [selected, slotEnabled[slot]], bit, Origin: consumeOrigin));
                string next = "slot" + suffix + "_lane_union_" + laneText;
                nodes.Add(new HwOperationNode(next, "or", [accumulated, eligible], bit, Origin: consumeOrigin));
                accumulated = next;
            }
            string mask = "slot" + suffix + "_mask";
            string placed = "slot" + suffix + "_placed";
            nodes.Add(new HwMuxNode(placed, accumulated, mask, "zero8", u8, consumeOrigin));
            string nextPacked = "consumed_slot_mask_after_" + suffix;
            nodes.Add(new HwOperationNode(nextPacked, "or", [packed, placed], u8, Origin: consumeOrigin));
            packed = nextPacked;
        }
        nodes.Add(new HwOutputDriveNode("drive_consumed_decoded_bundle_slot_mask",
            "consumed_decoded_bundle_slot_mask", packed, consumeOrigin));

        var top = new HwModule("consumed_decoded_bundle_slot_mask", "consumed_decoded_bundle_slot_mask",
            ports, nodes, [],
            new HwHardwareContract("pipeline.decoded-bundle.consumed-slot-mask.source-bounded.v1",
                HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), consumeOrigin);
        var design = new HwDesign("hwir/v0.1", top.Id, [top], toolchain, []);
        var validation = new FailClosedHardwareIrValidator().Validate(design);
        return validation.Validated ?? throw new InvalidDataException(
            "Consumed decoded-bundle slot-mask HWIR refused: " + string.Join("; ", validation.Diagnostics.Select(d => d.Message)));
    }

    private static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
