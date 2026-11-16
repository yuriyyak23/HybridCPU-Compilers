#if HARDWAREBRIDGE_HWIR
using System.IO;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Composes lane-state arbitration, scalar winner metadata selection and MEM page-fault delivery into one HWIR top.</summary>
public static class BridgeHwirPageFaultDeliveryComposition
{
    public static HwValidatedDesign Compose(HwValidatedDesign winner, HwValidatedDesign payload, HwValidatedDesign delivery)
    {
        ArgumentNullException.ThrowIfNull(winner); ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(delivery);
        HwModule winnerModule = winner.Design.Modules.Single(m => m.Id == "stage_aware_fault_winner");
        HwModule payloadModule = payload.Design.Modules.Single(m => m.Id == "fault_winner_payload_mux");
        HwModule deliveryModule = delivery.Design.Modules.Single(m => m.Id == "page_fault_delivery_after_winner");
        HwSourceOrigin origin = winnerModule.Origin ?? throw new InvalidDataException("Winner module has no source origin.");
        List<HwPortContract> topPorts = [];
        topPorts.AddRange(winnerModule.Ports.Where(p => p.Direction == HwPortDirection.Input));
        topPorts.AddRange(payloadModule.Ports.Where(p => p.Direction == HwPortDirection.Input &&
            p.Name is not ("winner_valid" or "winner_stage" or "winner_lane")));
        topPorts.AddRange(payloadModule.Ports.Where(p => p.Direction == HwPortDirection.Output));
        topPorts.AddRange(deliveryModule.Ports.Where(p => p.Direction == HwPortDirection.Input && p.Name.StartsWith("incoming_", StringComparison.Ordinal)));
        topPorts.AddRange(deliveryModule.Ports.Where(p => p.Direction == HwPortDirection.Output));
        if (topPorts.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != topPorts.Count)
            throw new InvalidDataException("Composed top contains duplicate external port names.");

        List<HwNode> nodes = [];
        List<HwInstanceNode> instances = [];
        void Wire(string id, HwBitVectorType type) => nodes.Add(new HwWireNode(id, type, origin));
        Wire("winner_valid_net", new(1)); Wire("winner_stage_net", new(3)); Wire("winner_lane_net", new(8));
        Dictionary<string, string> payloadOutputNets = new(StringComparer.Ordinal);
        foreach (HwPortContract port in payloadModule.Ports.Where(p => p.Direction == HwPortDirection.Output))
        {
            string net = "winner_payload_" + port.Name["winner_".Length..] + "_net";
            payloadOutputNets.Add(port.Name, net);
            Wire(net, port.Type);
            HwSourceOrigin traceOrigin = payloadModule.Nodes.OfType<HwOutputDriveNode>()
                .Single(drive => drive.OutputPort == port.Name).Origin ?? origin;
            nodes.Add(new HwOutputDriveNode("drive_trace_" + port.Name, port.Name, net, traceOrigin));
        }
        HwOperationNode laneTrunc = new("delivery_lane_3bit", "trunc", ["winner_lane_net"], new(3), Origin: origin);
        nodes.Add(laneTrunc);
        string[] winnerOutputs = ["winner_valid", "winner_stage", "winner_lane"];
        Dictionary<string, string> winnerOutputNets = new(StringComparer.Ordinal)
        { ["winner_valid"] = "winner_valid_net", ["winner_stage"] = "winner_stage_net", ["winner_lane"] = "winner_lane_net" };
        var winnerConnections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (HwPortContract port in winnerModule.Ports)
            winnerConnections[port.Name] = port.Direction == HwPortDirection.Input ? port.Name : winnerOutputNets[port.Name];
        instances.Add(new("resolve_stage_winner", winnerModule.Id, winnerConnections, new Dictionary<string, string>(), Origin: origin));

        Dictionary<string, string> payloadConnections = new(StringComparer.Ordinal)
        { ["winner_valid"] = "winner_valid_net", ["winner_stage"] = "winner_stage_net", ["winner_lane"] = "winner_lane_net" };
        foreach ((string output, string net) in payloadOutputNets) payloadConnections[output] = net;
        foreach (HwPortContract port in payloadModule.Ports.Where(p => p.Direction == HwPortDirection.Input &&
                     p.Name is not ("winner_valid" or "winner_stage" or "winner_lane")))
            payloadConnections[port.Name] = port.Name;
        instances.Add(new("select_winner_payload", payloadModule.Id, payloadConnections, new Dictionary<string, string>(), Origin: origin));
        Dictionary<string, string> deliveryConnections = new(StringComparer.Ordinal);
        foreach (HwPortContract port in deliveryModule.Ports)
        {
            if (port.Direction == HwPortDirection.Input)
                deliveryConnections[port.Name] = port.Name switch
                {
                    "winner_valid" => "winner_valid_net",
                    "winner_stage" => "winner_stage_net",
                    "winner_lane" => "delivery_lane_3bit",
                    "winner_fault_address" => payloadOutputNets["winner_fault_address"],
                    "winner_fault_is_write" => payloadOutputNets["winner_fault_is_write"],
                    _ => port.Name
                };
            else
            {
                string net = "delivery_" + port.Name + "_net";
                Wire(net, port.Type);
                deliveryConnections[port.Name] = net;
                nodes.Add(new HwOutputDriveNode("drive_top_" + port.Name, port.Name, net, origin));
            }
        }
        instances.Add(new("deliver_mem_page_fault", deliveryModule.Id, deliveryConnections, new Dictionary<string, string>(), Origin: origin));
        const string topId = "processor_pagefault_delivery_top";
        HwModule top = new(topId, topId, topPorts, nodes, instances,
            new HwHardwareContract("processor.pagefault-delivery.composed-source-projection.v1", HwModuleKind.Combinational,
                topPorts, new HwTimingContract(0, 1)), origin);
        HwDesign design = new("hwir/v0.1", topId,
            [top, winnerModule, payloadModule, deliveryModule], winner.Design.Toolchain, []);
        HwValidationResult validation = new FailClosedHardwareIrValidator().Validate(design);
        return validation.Validated ?? throw new InvalidDataException("Composed page-fault delivery HWIR refused: " +
            string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }
}
#endif
