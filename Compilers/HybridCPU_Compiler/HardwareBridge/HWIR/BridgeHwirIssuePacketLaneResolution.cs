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
/// Source-bounded combinational projection of the active scalar-entry
/// BundleIssuePacket.TryResolvePacketLane overload. This is lane selection only:
/// it does not model Create ordering, lane writes, occupancy commits, or handoff.
/// </summary>
public static class BridgeHwirIssuePacketLaneResolution
{
    private const string PacketPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Core/RuntimeClusterAdmissionPreparation.BundleIssuePacket.cs";
    private const string ClassPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/SlotLegality/SlotClassDefinitions.cs";
    private const string BundlePath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Metadata/BundleMetadata.cs";

    public static HwValidatedDesign Compile(byte[] packetSource, byte[] classSource,
        byte[] bundleMetadataSource, string[] expectedSha256, string commit,
        HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(expectedSha256);
        ArgumentNullException.ThrowIfNull(toolchain);
        byte[][] sources = [packetSource, classSource, bundleMetadataSource];
        static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (expectedSha256.Length != sources.Length || sources.Any(s => s is null || s.Length > 2_000_000) ||
            sources.Where((s, i) => Hash(s) != expectedSha256[i]).Any() || commit.Length != 40 || commit.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Issue-packet lane resolver source identity differs.");
        static SyntaxNode Parse(byte[] bytes)
        {
            var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Issue-packet source has syntax errors.");
            return tree.GetRoot();
        }
        static bool Same(SyntaxNode left, SyntaxNode right) => left.DescendantTokens().Select(t => t.Text)
            .SequenceEqual(right.DescendantTokens().Select(t => t.Text), StringComparer.Ordinal);

        SyntaxNode packetRoot = Parse(packetSource);
        var packetType = packetRoot.DescendantNodes().OfType<StructDeclarationSyntax>()
            .Single(t => t.Identifier.ValueText == "BundleIssuePacket");
        var resolver = packetType.Members.OfType<MethodDeclarationSyntax>().Single(m =>
            m.Identifier.ValueText == "TryResolvePacketLane" && m.ParameterList.Parameters.Count == 3 &&
            m.ParameterList.Parameters[0].Type?.ToString() == "ScalarClusterIssueEntry");
        const string expectedResolver = """
        {
            physicalLaneIndex = byte.MaxValue;
            MicroOp microOp = entry.MicroOp
                ?? throw new InvalidOperationException("Prepared scalar-cluster entry must carry a live MicroOp.");
            SlotPlacementMetadata placement = microOp.AdmissionMetadata.Placement;

            if (microOp.PostStageBIssuedAttempt is { } issued)
            {
                int lane = issued.ScheduledOperation.PhysicalLane;
                if (!issued.IsCurrent || lane < 0 || lane >= BundleMetadata.BundleSlotCount ||
                    (occupiedPhysicalLaneMask & (1 << lane)) != 0) return false;
                physicalLaneIndex = (byte)lane;
                return true;
            }

            if (placement.PinningKind == SlotPinningKind.HardPinned)
            {
                byte pinnedLaneId = placement.PinnedLaneId;
                if (pinnedLaneId >= 8)
                    return false;

                byte pinnedLaneBit = (byte)(1 << pinnedLaneId);
                if ((occupiedPhysicalLaneMask & pinnedLaneBit) != 0)
                    return false;

                physicalLaneIndex = pinnedLaneId;
                return true;
            }

            if (placement.RequiredSlotClass == SlotClass.Unclassified)
                return false;

            byte eligibleLaneMask = SlotClassLaneMap.GetLaneMask(placement.RequiredSlotClass);
            if (eligibleLaneMask == 0)
                return false;

            for (byte laneIndex = 0; laneIndex < 8; laneIndex++)
            {
                byte laneBit = (byte)(1 << laneIndex);
                if ((eligibleLaneMask & laneBit) == 0 || (occupiedPhysicalLaneMask & laneBit) != 0)
                    continue;

                physicalLaneIndex = laneIndex;
                return true;
            }

            return false;
        }
        """;
        if (resolver.Body is null || !Same(resolver.Body, SyntaxFactory.ParseStatement(expectedResolver)))
            throw new InvalidDataException("Scalar issue-entry lane resolver body changed.");

        SyntaxNode classRoot = Parse(classSource);
        var classes = classRoot.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(e => e.Identifier.ValueText == "SlotClass");
        var enumValues = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var member in classes.Members)
        {
            if (member.EqualsValue?.Value is not LiteralExpressionSyntax literal || !byte.TryParse(literal.Token.ValueText, out byte value))
                throw new InvalidDataException("SlotClass must retain explicit byte values for lane projection.");
            enumValues.Add(member.Identifier.ValueText, value);
        }
        var mapType = classRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "SlotClassLaneMap");
        var getMask = mapType.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "GetLaneMask");
        const string expectedMap = "slotClass switch { SlotClass.AluClass => AluMask, SlotClass.LsuClass => LsuMask, SlotClass.DmaStreamClass => DmaStreamMask, SlotClass.MatrixTileStreamClass => MatrixTileStreamMask, SlotClass.BranchControl => BranchMask, SlotClass.SystemSingleton => SystemMask, SlotClass.Unclassified => UnclassifiedMask, _ => throw new ArgumentOutOfRangeException(nameof(slotClass), slotClass, \"Unknown SlotClass value.\") }";
        if (getMask.ExpressionBody is null || !Same(getMask.ExpressionBody.Expression, SyntaxFactory.ParseExpression(expectedMap)) ||
            classes.BaseList?.Types.Single().ToString() != "byte" || !enumValues.TryGetValue("Unclassified", out byte unclassified))
            throw new InvalidDataException("SlotClass.GetLaneMask or enum backing changed.");
        var constants = mapType.Members.OfType<FieldDeclarationSyntax>().SelectMany(f => f.Declaration.Variables.Select(v =>
            (Name: v.Identifier.ValueText, Value: v.Initializer?.Value.ToString().Replace("_", "", StringComparison.Ordinal))))
            .ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
        var classMasks = new Dictionary<byte, byte>();
        foreach (var pair in new[] { ("AluClass", "AluMask"), ("LsuClass", "LsuMask"), ("DmaStreamClass", "DmaStreamMask"),
                     ("MatrixTileStreamClass", "MatrixTileStreamMask"), ("BranchControl", "BranchMask"),
                     ("SystemSingleton", "SystemMask"), ("Unclassified", "UnclassifiedMask") })
        {
            if (!enumValues.TryGetValue(pair.Item1, out byte code) || !constants.TryGetValue(pair.Item2, out string? bits) ||
                !byte.TryParse(bits?.Replace("0b", "", StringComparison.Ordinal), System.Globalization.NumberStyles.BinaryNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out byte mask))
                throw new InvalidDataException("SlotClassLaneMap constants do not match the guarded switch.");
            classMasks.Add(code, mask);
        }
        SyntaxNode bundleRoot = Parse(bundleMetadataSource);
        var bundleType = bundleRoot.DescendantNodes().OfType<TypeDeclarationSyntax>().Single(c => c.Identifier.ValueText == "BundleMetadata");
        var widthProperty = bundleType.Members.OfType<FieldDeclarationSyntax>().SelectMany(f => f.Declaration.Variables)
            .Single(v => v.Identifier.ValueText == "BundleSlotCount");
        if (widthProperty.Initializer?.Value.ToString() != "8") throw new InvalidDataException("BundleMetadata.BundleSlotCount is not eight.");

        var span = resolver.Identifier.GetLocation().GetLineSpan();
        string identity = Hash(Encoding.UTF8.GetBytes("issue-packet-lane-resolve/scalar/v1|" + expectedSha256[0] + "|" +
            string.Concat(resolver.DescendantTokens().Select(t => t.Text))));
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", commit, PacketPath,
            "CircuitWeave.Captured.IssuePacketLaneProjection", "YAKSys_Hybrid_CPU.Core.BundleIssuePacket",
            "TryResolvePacketLane(ScalarClusterIssueEntry)", identity, HwSourceRole.Primary,
            span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
        var mapSpan = getMask.Identifier.GetLocation().GetLineSpan();
        string mapIdentity = Hash(Encoding.UTF8.GetBytes("issue-packet-slot-class-lane-map/v1|" + expectedSha256[1] + "|" +
            string.Concat(getMask.DescendantTokens().Select(t => t.Text))));
        var mapOrigin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", commit, ClassPath,
            "CircuitWeave.Captured.IssuePacketLaneProjection", "YAKSys_Hybrid_CPU.Core.SlotClassLaneMap",
            "GetLaneMask", mapIdentity, HwSourceRole.Primary,
            mapSpan.StartLinePosition.Line + 1, mapSpan.StartLinePosition.Character + 1);

        var bit = new HwBitVectorType(1); var u3 = new HwBitVectorType(3); var u8 = new HwBitVectorType(8); var u32 = new HwBitVectorType(32);
        var ports = new List<HwPortContract>
        {
            new("issued_present", HwPortDirection.Input, bit), new("issued_current", HwPortDirection.Input, bit),
            new("issued_lane_signed_bits", HwPortDirection.Input, u32), new("hard_pinned", HwPortDirection.Input, bit),
            new("pinned_lane_id", HwPortDirection.Input, u8), new("slot_class", HwPortDirection.Input, u8),
            new("occupied_lane_mask", HwPortDirection.Input, u8),
            new("accepted", HwPortDirection.Output, bit), new("physical_lane_index", HwPortDirection.Output, u8)
        };
        var nodes = new List<HwNode>();
        nodes.Add(new HwWireNode("class_lane_mask_internal", u8, origin));
        nodes.Add(new HwWireNode("class_map_defined_internal", bit, origin));
        void C(string id, HwBitVectorType type, uint value) => nodes.Add(new HwConstantNode(id, type,
            Convert.ToString(value, 2).PadLeft(type.Width, '0'), origin));
        C("zero1", bit, 0); C("one1", bit, 1); C("zero8", u8, 0); C("invalid_lane", u8, 255);
        C("unclassified_code", u8, unclassified);
        // Keep the class map in a separately-provenanced child module.
        string[] classNames = new[] { "AluClass", "LsuClass", "DmaStreamClass", "MatrixTileStreamClass", "BranchControl", "SystemSingleton", "Unclassified" };
        var mapPorts = new HwPortContract[]
        {
            new("slot_class", HwPortDirection.Input, u8),
            new("defined", HwPortDirection.Output, bit),
            new("lane_mask", HwPortDirection.Output, u8)
        };
        var mapNodes = new List<HwNode>();
        void MC(string id, HwBitVectorType type, uint value) => mapNodes.Add(new HwConstantNode(id, type,
            Convert.ToString(value, 2).PadLeft(type.Width, '0'), mapOrigin));
        MC("map_zero1", bit, 0); MC("map_zero8", u8, 0); MC("map_unclassified_mask", u8, classMasks[unclassified]);
        var classMatchNames = new Dictionary<string, string>(StringComparer.Ordinal);
        string mapDefined = "map_zero1", mapLaneMask = "map_unclassified_mask";
        foreach (string className in classNames)
        {
            byte code = enumValues[className];
            string classCode = "map_code_" + className;
            MC(classCode, u8, code);
            string match = "map_match_" + className;
            mapNodes.Add(new HwOperationNode(match, "eq", ["slot_class", classCode], bit, Origin: mapOrigin));
            mapNodes.Add(new HwMuxNode("map_mask_" + className, match, "map_value_" + className,
                mapLaneMask, u8, mapOrigin));
            MC("map_value_" + className, u8, classMasks[code]);
            mapLaneMask = "map_mask_" + className;
            mapNodes.Add(new HwOperationNode("map_defined_" + className, "or", [mapDefined, match], bit, Origin: mapOrigin));
            mapDefined = "map_defined_" + className;
            classMatchNames.Add(className, match);
        }
        mapNodes.Add(new HwOutputDriveNode("map_drive_defined", "defined", mapDefined, mapOrigin));
        mapNodes.Add(new HwOutputDriveNode("map_drive_lane_mask", "lane_mask", mapLaneMask, mapOrigin));
        var mapModule = new HwModule("issue_packet_slot_class_lane_map_projection", "issue_packet_slot_class_lane_map_projection",
            mapPorts, mapNodes, [], new HwHardwareContract("scheduler.slot-class-lane-map.source-bounded.v1",
            HwModuleKind.Combinational, mapPorts, new HwTimingContract(0, 1)), mapOrigin);
        string issuedValid = "zero1", pinnedValid = "zero1", issuedLaneOut = "invalid_lane", pinnedLaneOut = "invalid_lane";
        string choiceValid = "zero1", choiceLane = "invalid_lane";
        for (byte lane = 0; lane < 8; lane++)
        {
            C($"lane{lane}_id32", u32, lane); C($"lane{lane}_id8", u8, lane); C($"lane{lane}_bit", u8, (uint)(1 << lane));
            nodes.Add(new HwOperationNode($"issued_lane{lane}_match", "eq", ["issued_lane_signed_bits", $"lane{lane}_id32"], bit, Origin: origin));
            nodes.Add(new HwOperationNode($"pinned_lane{lane}_match", "eq", ["pinned_lane_id", $"lane{lane}_id8"], bit, Origin: origin));
            string occupied = $"lane{lane}_occupied";
            nodes.Add(new HwOperationNode(occupied, "and", ["occupied_lane_mask", $"lane{lane}_bit"], u8, Origin: origin));
            nodes.Add(new HwOperationNode($"lane{lane}_occupied_zero", "eq", [occupied, "zero8"], bit, Origin: origin));
            nodes.Add(new HwOperationNode($"issued_lane{lane}_clear", "and", [$"issued_lane{lane}_match", $"lane{lane}_occupied_zero"], bit, Origin: origin));
            nodes.Add(new HwOperationNode($"pinned_lane{lane}_clear", "and", [$"pinned_lane{lane}_match", $"lane{lane}_occupied_zero"], bit, Origin: origin));
            issuedValid = Or(issuedValid, $"issued_lane{lane}_clear", $"issued_valid_or{lane}");
            pinnedValid = Or(pinnedValid, $"pinned_lane{lane}_clear", $"pinned_valid_or{lane}");
            issuedLaneOut = Mux($"issued_lane{lane}_match", $"lane{lane}_id8", issuedLaneOut, $"issued_lane_out{lane}");
            pinnedLaneOut = Mux($"pinned_lane{lane}_match", $"lane{lane}_id8", pinnedLaneOut, $"pinned_lane_out{lane}");
            nodes.Add(new HwOperationNode($"class_lane{lane}_mask", "and", ["class_lane_mask_internal", $"lane{lane}_bit"], u8, Origin: origin));
            nodes.Add(new HwOperationNode($"class_lane{lane}_zero", "eq", [$"class_lane{lane}_mask", "zero8"], bit, Origin: origin));
            nodes.Add(new HwOperationNode($"class_lane{lane}_eligible", "xor", [$"class_lane{lane}_zero", "one1"], bit, Origin: origin));
            string eligible = $"class_lane{lane}_eligible";
            nodes.Add(new HwOperationNode($"class_lane{lane}_available", "and", [eligible, $"lane{lane}_occupied_zero"], bit, Origin: origin));
            string higherClear = "one1";
            for (byte higher = 0; higher < lane; higher++)
            {
                string noHigher = "higher" + higher + "_not_available_for_" + lane;
                nodes.Add(new HwOperationNode(noHigher, "xor", [$"class_lane{higher}_available", "one1"], bit, Origin: origin));
                higherClear = And(higherClear, noHigher, $"higher{higher}_{lane}_clear");
            }
            string first = And($"class_lane{lane}_available", higherClear, $"class_lane{lane}_first");
            choiceValid = Or(choiceValid, first, $"choice_valid_or{lane}");
            choiceLane = Mux(first, $"lane{lane}_id8", choiceLane, $"choice_lane{lane}");
        }
        // Source order: issued attempt first; otherwise hard pin; otherwise lowest free eligible lane.
        nodes.Add(new HwOperationNode("class_is_unclassified", "eq", ["slot_class", "unclassified_code"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("class_is_not_unclassified", "xor", ["class_is_unclassified", "one1"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("issued_accept", "and", ["issued_current", "issued_valid_or7"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("class_accept", "and", ["class_is_not_unclassified", "choice_valid_or7"], bit, Origin: origin));
        nodes.Add(new HwMuxNode("nonissued_accept", "hard_pinned", "pinned_valid_or7", "class_accept", bit, origin));
        nodes.Add(new HwMuxNode("accept_result", "issued_present", "issued_accept", "nonissued_accept", bit, origin));
        nodes.Add(new HwMuxNode("hard_lane_result", "hard_pinned", "pinned_lane_out7", "choice_lane7", u8, origin));
        nodes.Add(new HwMuxNode("lane_result", "issued_present", "issued_lane_out7", "hard_lane_result", u8, origin));
        nodes.Add(new HwMuxNode("final_lane_result", "accept_result", "lane_result", "invalid_lane", u8, origin));
        nodes.Add(new HwOutputDriveNode("drive_accepted", "accepted", "accept_result", origin));
        nodes.Add(new HwOutputDriveNode("drive_lane", "physical_lane_index", "final_lane_result", origin));

        var instances = new[]
        {
            new HwInstanceNode("slot_class_lane_map", mapModule.Id,
                new Dictionary<string, string> { ["slot_class"] = "slot_class", ["defined"] = "class_map_defined_internal", ["lane_mask"] = "class_lane_mask_internal" },
                new Dictionary<string, string>(), Origin: origin)
        };
        ports.Add(new HwPortContract("class_map_defined", HwPortDirection.Output, bit));
        ports.Add(new HwPortContract("class_lane_mask", HwPortDirection.Output, u8));
        nodes.Add(new HwOutputDriveNode("drive_class_map_defined", "class_map_defined", "class_map_defined_internal", origin));
        nodes.Add(new HwOutputDriveNode("drive_class_lane_mask", "class_lane_mask", "class_lane_mask_internal", origin));
        var module = new HwModule("issue_packet_scalar_lane_resolution_projection", "issue_packet_scalar_lane_resolution_projection",
            ports, nodes, instances, new HwHardwareContract("scheduler.issue-packet.scalar-lane-resolution.source-bounded.v1",
            HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var result = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", module.Id, [module, mapModule], toolchain, []));
        return result.Validated ?? throw new InvalidDataException("Issue-packet lane resolver HWIR refused: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        string Or(string a, string b, string id)
        {
            nodes.Add(new HwOperationNode(id, "or", [a, b], bit, Origin: origin)); return id;
        }
        string And(string a, string b, string id)
        {
            nodes.Add(new HwOperationNode(id, "and", [a, b], bit, Origin: origin)); return id;
        }
        string Mux(string condition, string yes, string no, string id)
        {
            nodes.Add(new HwMuxNode(id, condition, yes, no, u8, origin)); return id;
        }
    }
}
#endif
