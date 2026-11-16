#if HARDWAREBRIDGE_HWIR
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Hierarchical composition of source-bounded packet allocation and per-lane eligibility.</summary>
public static class BridgeHwirIssuePacketMaterializationComposition
{
    public static HwValidatedDesign Compile(byte[] packet, byte[] intent, byte[] preparation,
        byte[] classes, byte[] slotPlacement, byte[] bundle, byte[] decodeSource,
        byte[] materializationSource, byte[] materializationPacketSource, byte[] issueLaneSource,
        byte[] stageFlowSource, byte[] dependencySummarySource,
        string[] createExpectedSha256, string[] materializationExpectedSha256, string[] resolverExpectedSha256, string commit,
        HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(createExpectedSha256);
        ArgumentNullException.ThrowIfNull(materializationExpectedSha256);
        ArgumentNullException.ThrowIfNull(resolverExpectedSha256);
        ArgumentNullException.ThrowIfNull(toolchain);
        if (createExpectedSha256.Length != 7 || materializationExpectedSha256.Length != 3 ||
            !StringComparer.Ordinal.Equals(Hash(packet), materializationExpectedSha256[1]) ||
            !StringComparer.Ordinal.Equals(Hash(issueLaneSource), materializationExpectedSha256[2]))
            throw new InvalidDataException("Composition source identity differs or is incomplete.");

        HwValidatedDesign create = BridgeHwirIssuePacketCreateAllocation.Compile(packet, intent, preparation,
            classes, slotPlacement, bundle, decodeSource, createExpectedSha256, commit, toolchain);
        HwValidatedDesign eligibility = BridgeHwirIssuePacketMaterializationEligibility.Compile(
            materializationSource, materializationPacketSource, issueLaneSource,
            materializationExpectedSha256, commit, toolchain);
        HwValidatedDesign resolver = BridgeHwirIssuePacketNonScalarPhysicalLaneMask.Compile(
            stageFlowSource, dependencySummarySource, issueLaneSource, materializationPacketSource,
            resolverExpectedSha256, commit, toolchain);
        HwModule createTop = create.Design.Modules.Single(m => m.Id == create.Design.TopModule)!;
        HwModule eligibilityTop = eligibility.Design.Modules.Single(m => m.Id == eligibility.Design.TopModule)!;
        HwModule resolverTop = resolver.Design.Modules.Single(m => m.Id == resolver.Design.TopModule)!;
        HwSourceOrigin origin = createTop.Origin!;
        var ports = createTop.Ports.ToList();
        if (ports.Any(p => p.Name == "materializable_lane_mask" || p.Name == "dependency_summary_present"))
            throw new InvalidDataException("Composition port collides with Create allocation contract.");
        var u8 = new HwBitVectorType(8); var bit = new HwBitVectorType(1);
        foreach (HwPortContract p in resolverTop.Ports.Where(p => p.Direction == HwPortDirection.Input && p.Name != "scalar_issue_mask"))
        {
            if (ports.Any(existing => existing.Name == p.Name)) throw new InvalidDataException("Resolver input collides with Create allocation contract: " + p.Name);
            ports.Add(p);
        }
        ports.Add(new HwPortContract("materializable_lane_mask", HwPortDirection.Output, u8));

        var nodes = new List<HwNode>();
        var createConnections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (HwPortContract p in createTop.Ports)
        {
            if (p.Direction == HwPortDirection.Input) createConnections.Add(p.Name, p.Name);
            else
            {
                if (p.Name == "assigned_lane_mask")
                {
                    nodes.Add(new HwWireNode("create_assigned_lane_mask", u8, origin));
                    createConnections.Add(p.Name, "create_assigned_lane_mask");
                    nodes.Add(new HwOutputDriveNode("drive_passthrough_assigned_lane_mask", p.Name,
                        "create_assigned_lane_mask", origin));
                    continue;
                }
                string wire = "create_out_" + p.Name;
                nodes.Add(new HwWireNode(wire, p.Type, origin));
                createConnections.Add(p.Name, wire);
                nodes.Add(new HwOutputDriveNode("drive_passthrough_" + p.Name, p.Name, wire, origin));
            }
        }
        // Occupancy is bound exclusively to allocation's assigned-lane result.
        var instances = new List<HwInstanceNode>
        {
            new("create_allocation", createTop.Id, createConnections, new Dictionary<string,string>(), Origin: origin)
        };
        nodes.Add(new HwWireNode("resolved_executable_non_scalar_mask", u8, origin));
        var resolverConnections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (HwPortContract p in resolverTop.Ports)
        {
            if (p.Direction == HwPortDirection.Input)
                // scalar_issue_mask is a slot-domain mask shared with Create's input. Create's
                // assigned_lane_mask is a distinct physical-lane-domain output used for occupancy.
                resolverConnections.Add(p.Name, p.Name);
            else
                resolverConnections.Add(p.Name, "resolved_executable_non_scalar_mask");
        }
        instances.Add(new HwInstanceNode("resolve_executable_non_scalar_lanes", resolverTop.Id,
            resolverConnections, new Dictionary<string, string>(), Origin: origin));
        nodes.Add(new HwConstantNode("lane_zero8", u8, "00000000", origin));
        for (byte lane = 0; lane < 8; lane++)
        {
            string laneIndex = "lane" + lane + "_index";
            string laneBit = "lane" + lane + "_bit";
            string occupancy = "lane" + lane + "_occupied";
            string result = "lane" + lane + "_materialize";
            nodes.Add(new HwConstantNode(laneIndex, u8, Convert.ToString(lane, 2).PadLeft(8, '0'), origin));
            nodes.Add(new HwConstantNode(laneBit, u8, Convert.ToString(1 << lane, 2).PadLeft(8, '0'), origin));
        nodes.Add(new HwOperationNode("lane" + lane + "_assigned_bits", "and",
                ["create_assigned_lane_mask", laneBit], u8, Origin: origin));
            nodes.Add(new HwOperationNode("lane" + lane + "_assigned_nonzero", "eq",
                ["lane" + lane + "_assigned_bits", "lane_zero8"], bit, Origin: origin));
            nodes.Add(new HwConstantNode("lane" + lane + "_one", bit, "1", origin));
            nodes.Add(new HwOperationNode("lane" + lane + "_occupancy", "xor",
                ["lane" + lane + "_assigned_nonzero", "lane" + lane + "_one"], bit, Origin: origin));
            occupancy = "lane" + lane + "_occupancy";
            nodes.Add(new HwWireNode(result, bit, origin));
            var eligibilityConnections = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["lane_index"] = laneIndex,
                ["occupied"] = occupancy,
                ["executable_non_scalar_mask"] = "resolved_executable_non_scalar_mask",
                ["materialize"] = result
            };
            instances.Add(new HwInstanceNode("materialization_lane" + lane, eligibilityTop.Id,
                eligibilityConnections, new Dictionary<string,string>(), Origin: origin));
        }
        // Pack the independently computed lane decisions into the declared 8-bit result.
        for (byte lane = 0; lane < 8; lane++)
        {
            nodes.Add(new HwConstantNode("lane" + lane + "_onehot", u8,
                Convert.ToString(1 << lane, 2).PadLeft(8, '0'), origin));
            nodes.Add(new HwMuxNode("lane" + lane + "_placed", "lane" + lane + "_materialize",
                "lane" + lane + "_onehot", "lane_zero8", u8, origin));
        }
        string packed = "lane0_placed";
        for (byte lane = 1; lane < 8; lane++)
        {
            string next = "packed_lanes_" + lane;
            nodes.Add(new HwOperationNode(next, "or", [packed, "lane" + lane + "_placed"], u8, Origin: origin));
            packed = next;
        }
        nodes.Add(new HwOutputDriveNode("drive_materializable_lane_mask", "materializable_lane_mask", packed, origin));

        var top = new HwModule("issue_packet_create_materialization_composition",
            "issue_packet_create_materialization_composition", ports, nodes, instances,
            new HwHardwareContract("scheduler.issue-packet.create-materialization-composition.source-bounded.v1",
                HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var modules = create.Design.Modules.Where(m => m.Id != createTop.Id)
            .Concat(eligibility.Design.Modules.Where(m => m.Id != eligibilityTop.Id))
            .Concat(resolver.Design.Modules.Where(m => m.Id != resolverTop.Id))
            .Append(createTop).Append(eligibilityTop).Append(resolverTop).Append(top)
            .GroupBy(m => m.Id, StringComparer.Ordinal).Select(g => g.First()).ToArray();
        var design = new HwDesign("hwir/v0.1", top.Id, modules, toolchain, []);
        var validation = new FailClosedHardwareIrValidator().Validate(design);
        return validation.Validated ?? throw new InvalidDataException(
            "Issue-packet composition HWIR refused: " + string.Join("; ", validation.Diagnostics.Select(d => d.Message)));
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
