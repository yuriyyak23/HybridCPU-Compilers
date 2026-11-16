#if HARDWAREBRIDGE_HWIR
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>
/// Four ordered source-bound Assist candidate attempts for the typed-slot path.
/// The legacy slot-agnostic branch and live result-array object binding are outside
/// this module's contract.
/// </summary>
public static class BridgeHwirAssistRankedCandidateChain
{
    public const string TopModuleId = "assist_ordered_candidate_chain";

    public static HwValidatedDesign Compile(
        HwValidatedDesign control,
        HwValidatedDesign attempt,
        HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(toolchain);

        HwModule controlTop = control.Design.Modules.Single(m => m.Id == control.Design.TopModule);
        HwModule attemptTop = attempt.Design.Modules.Single(m => m.Id == attempt.Design.TopModule);
        if (controlTop.Id != "assist_ordered_attempt_chain" || attemptTop.Id != "assist_candidate_attempt")
            throw new InvalidDataException("Assist ranked composition received unexpected child designs.");
        if (!Equals(control.Design.Toolchain, attempt.Design.Toolchain) || !Equals(control.Design.Toolchain, toolchain))
            throw new InvalidDataException("Assist ranked child toolchain identities differ.");

        HwSourceOrigin childOrigin = controlTop.Origin ?? attemptTop.Origin ??
            throw new InvalidDataException("Assist ranked composition source origin is missing.");
        // Keep the real scheduler call origin. The composed bridge has no upstream
        // declaration of its own, so assigning it a made-up upstream symbol would
        // make Studio source navigation appear more authoritative than it is.
        HwSourceOrigin origin = childOrigin;

        var ports = new List<HwPortContract>();
        var nodes = new List<HwNode>
        {
            new HwConstantNode("typed_slot_enabled_one", new HwBitVectorType(1), "1", origin),
            new HwConstantNode("legacy_path_fact_zero", new HwBitVectorType(1), "0", origin),
            new HwConstantNode("assist_prior_count_zero", new HwBitVectorType(32), new string('0', 32), origin)
        };
        void AddPort(HwPortContract port)
        {
            HwPortContract? old = ports.SingleOrDefault(p => p.Name == port.Name);
            if (old is not null)
            {
                if (!Equals(old.Type, port.Type) || old.Direction != port.Direction)
                    throw new InvalidDataException("Assist ranked parent port contract collision: " + port.Name);
                return;
            }
            ports.Add(port);
        }

        HwPortContract ControlPort(string name, HwPortDirection direction) => controlTop.Ports.Single(p => p.Name == name && p.Direction == direction);
        HwPortContract AttemptPort(string name, HwPortDirection direction) => attemptTop.Ports.Single(p => p.Name == name && p.Direction == direction);
        var controlConnections = new Dictionary<string, string>(StringComparer.Ordinal);
        var controlOutputWires = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (HwPortContract p in controlTop.Ports.Where(p => p.Direction == HwPortDirection.Input))
        {
            if (p.Name == "typed_slot_enabled")
            {
                controlConnections.Add(p.Name, "typed_slot_enabled_one");
                continue;
            }
            if (p.Name.EndsWith("_stage_a_pass", StringComparison.Ordinal) || p.Name.EndsWith("_stage_b_pass", StringComparison.Ordinal) ||
                p.Name.EndsWith("_backpressure_pass", StringComparison.Ordinal) || p.Name.EndsWith("_quota_pass", StringComparison.Ordinal))
            {
                int rankIndex = RankIndex(p.Name);
                string source = p.Name.EndsWith("_stage_a_pass", StringComparison.Ordinal) ? $"attempt{rankIndex}_admission_stage_b_evaluated" :
                    p.Name.EndsWith("_stage_b_pass", StringComparison.Ordinal) ? $"attempt{rankIndex}_admission_accepted" :
                    p.Name.EndsWith("_backpressure_pass", StringComparison.Ordinal) ? $"attempt{rankIndex}_quota_backpressure_accepted" :
                    $"attempt{rankIndex}_quota_accept";
                controlConnections.Add(p.Name, source);
                continue;
            }
            if (p.Name.EndsWith("_legacy_legal", StringComparison.Ordinal) || p.Name.EndsWith("_legacy_dma_sensitive", StringComparison.Ordinal))
            {
                controlConnections.Add(p.Name, "legacy_path_fact_zero");
                continue;
            }
            AddPort(p);
            controlConnections.Add(p.Name, p.Name);
        }

        foreach (HwPortContract p in controlTop.Ports.Where(p => p.Direction == HwPortDirection.Output))
        {
            AddPort(p);
            HwOutputDriveNode drive = controlTop.Nodes.OfType<HwOutputDriveNode>().Single(n => n.OutputPort == p.Name);
            string source = ControlSignal(drive.Value);
            nodes.Add(new HwOutputDriveNode("drive_" + p.Name, p.Name, source, origin));
            controlOutputWires.Add(p.Name, source);
            controlConnections.Add(p.Name, source);
        }

        if (controlTop.Nodes.OfType<HwInstanceNode>().Any())
            throw new InvalidDataException("Assist control module contains nested instances and cannot be safely inlined.");
        foreach (HwNode node in controlTop.Nodes)
        {
            switch (node)
            {
                case HwOutputDriveNode:
                    break;
                case HwConstantNode constant:
                    nodes.Add(constant with { Id = ControlSignal(constant.Id), Origin = origin });
                    break;
                case HwWireNode wire:
                    nodes.Add(wire with { Id = ControlSignal(wire.Id), Origin = origin });
                    break;
                case HwOperationNode operation:
                    nodes.Add(operation with
                    {
                        Id = ControlSignal(operation.Id),
                        Operands = operation.Operands.Select(ControlSignal).ToArray(),
                        Origin = origin
                    });
                    break;
                case HwMuxNode mux:
                    nodes.Add(mux with
                    {
                        Id = ControlSignal(mux.Id),
                        Condition = ControlSignal(mux.Condition),
                        WhenTrue = ControlSignal(mux.WhenTrue),
                        WhenFalse = ControlSignal(mux.WhenFalse),
                        Origin = origin
                    });
                    break;
                default:
                    throw new InvalidDataException("Assist control module has unsupported node kind: " + node.GetType().Name);
            }
        }

        string ControlSignal(string signal) => controlConnections.TryGetValue(signal, out string? mapped)
            ? mapped
            : "control_" + signal;

        var attemptOutputWires = new Dictionary<(int Rank, string Port), string>();
        var attemptConnections = new List<IReadOnlyDictionary<string, string>>();
        var stateInputToOutput = BuildStateMap(attemptTop);
        var stateOutputType = new Dictionary<string, HwBitVectorType>(StringComparer.Ordinal);
        foreach (HwPortContract input in attemptTop.Ports.Where(p => p.Direction == HwPortDirection.Input))
        {
            if (!stateInputToOutput.TryGetValue(input.Name, out string? outputName)) continue;
            HwPortContract output = AttemptPort(outputName, HwPortDirection.Output);
            if (!Equals(input.Type, output.Type))
                throw new InvalidDataException($"Assist state width/type mismatch: {input.Name} <- {outputName}.");
            if (!stateOutputType.TryAdd(outputName, output.Type))
                throw new InvalidDataException("Assist state output maps more than one current-state input: " + outputName);
        }
        foreach (HwPortContract output in attemptTop.Ports.Where(p => p.Direction == HwPortDirection.Output && IsStateOutput(p.Name)))
        {
            if (!stateOutputType.ContainsKey(output.Name))
                throw new InvalidDataException("Assist next-state output has no matching current input: " + output.Name);
        }

        for (int rank = 0; rank < 4; rank++)
        {
            string prefix = $"rank{rank}_";
            var connections = new Dictionary<string, string>(StringComparer.Ordinal);
            int factBusIndex = 0;
            int factBusUsedBits = 0;
            foreach (HwPortContract p in attemptTop.Ports.Where(p => p.Direction == HwPortDirection.Input))
            {
                string parentSignal;
                if (p.Name == "candidate_attempt_enable")
                {
                    parentSignal = controlOutputWires[prefix + "candidate_valid"];
                }
                else if (p.Name == "is_assist_candidate")
                {
                    parentSignal = prefix + "is_assist_op";
                }
                else if (p.Name == "admission_candidate_virtual_thread_id")
                {
                    EnsureSameType(p, ControlPort(prefix + "vt", HwPortDirection.Input), "candidate VT");
                    parentSignal = prefix + "vt";
                }
                else if (p.Name == "admission_owner_vt")
                {
                    EnsureSameType(p, ControlPort("owner_vt", HwPortDirection.Input), "owner VT");
                    parentSignal = "owner_vt";
                }
                else if (p.Name == "admission_current_injections")
                {
                    string common = "assist_current_pass_injections";
                    AddPort(p with { Name = common });
                    if (rank == 0)
                    {
                        parentSignal = common;
                    }
                    else
                    {
                        var u32 = new HwBitVectorType(32);
                        string prior = "assist_prior_count_zero";
                        for (int earlier = 0; earlier < rank; earlier++)
                        {
                            string bitCount = $"rank{rank}_prior_rank{earlier}_count_bit";
                            string add = $"rank{rank}_prior_rank{earlier}_count_sum";
                            nodes.Add(new HwOperationNode(bitCount, "zext", [attemptOutputWires[(earlier, "final_commit")]], u32, Origin: origin));
                            nodes.Add(new HwOperationNode(add, "add", [prior, bitCount], u32, Origin: origin));
                            prior = add;
                        }
                        parentSignal = $"rank{rank}_current_injections";
                        nodes.Add(new HwOperationNode(parentSignal, "add", [common, prior], u32, Origin: origin));
                    }
                }
                else if (p.Name == "admission_defined")
                {
                    parentSignal = controlOutputWires[prefix + "candidate_valid"];
                }
                else if (stateInputToOutput.TryGetValue(p.Name, out string? priorOutput))
                {
                    if (rank == 0)
                    {
                        string initial = "initial_state_" + p.Name;
                        AddPort(p with { Name = initial });
                        parentSignal = initial;
                    }
                    else
                    {
                        parentSignal = attemptOutputWires[(rank - 1, priorOutput)];
                    }
                }
                else
                {
                    if (p.Type.Width > 64 || p.Type.Signedness != HwSignedness.Unsigned || p.Type.FourState)
                        throw new InvalidDataException("Assist rank fact cannot be packed into 64-bit source-order bundle: " + p.Name);
                    if (factBusUsedBits + p.Type.Width > 64)
                    {
                        factBusIndex++;
                        factBusUsedBits = 0;
                    }
                    string busPort = prefix + "facts_" + factBusIndex.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
                    AddPort(new HwPortContract(busPort, HwPortDirection.Input, new HwBitVectorType(64)));
                    int offset = factBusUsedBits;
                    factBusUsedBits += p.Type.Width;
                    string shifted = busPort;
                    if (offset != 0)
                    {
                        string shiftAmount = prefix + "fact_shift_" + p.Name;
                        string shiftResult = prefix + "fact_shifted_" + p.Name;
                        nodes.Add(new HwConstantNode(shiftAmount, new HwBitVectorType(64),
                            Convert.ToString(offset, 2).PadLeft(64, '0'), origin));
                        nodes.Add(new HwOperationNode(shiftResult, "lshr64_mask6", [busPort, shiftAmount],
                            new HwBitVectorType(64), Origin: origin));
                        shifted = shiftResult;
                    }
                    if (p.Type.Width == 64)
                    {
                        parentSignal = shifted;
                    }
                    else
                    {
                        parentSignal = prefix + "fact_value_" + p.Name;
                        nodes.Add(new HwOperationNode(parentSignal, "trunc", [shifted], p.Type, Origin: origin));
                    }
                }
                connections.Add(p.Name, parentSignal);
            }

            foreach (HwPortContract p in attemptTop.Ports.Where(p => p.Direction == HwPortDirection.Output))
            {
                string wire = $"attempt{rank}_{p.Name}";
                nodes.Add(new HwWireNode(wire, p.Type, origin));
                connections.Add(p.Name, wire);
                attemptOutputWires.Add((rank, p.Name), wire);
            }
            attemptConnections.Add(connections);

            AddAttemptTraceOutput(rank, "final_commit", "final_commit", new HwBitVectorType(1));
            AddAttemptTraceOutput(rank, "admission_stage_b_evaluated", "stage_a_pass", new HwBitVectorType(1));
            AddAttemptTraceOutput(rank, "admission_accepted", "stage_b_pass", new HwBitVectorType(1));
            AddAttemptTraceOutput(rank, "admission_selected_lane", "selected_lane", new HwBitVectorType(3));
            AddAttemptTraceOutput(rank, "admission_reject_reason", "reject_reason", new HwBitVectorType(8));
            AddAttemptTraceOutput(rank, "quota_backpressure_accepted", "backpressure_accepted", new HwBitVectorType(1));
            AddAttemptTraceOutput(rank, "quota_accept", "quota_accepted", new HwBitVectorType(1));

            void AddAttemptTraceOutput(int i, string childPort, string suffix, HwBitVectorType type)
            {
                string outputName = $"rank{i}_attempt_{suffix}";
                AddPort(new HwPortContract(outputName, HwPortDirection.Output, type));
                nodes.Add(new HwOutputDriveNode("drive_" + outputName, outputName, attemptOutputWires[(i, childPort)], origin));
            }
        }

        foreach (var state in stateInputToOutput)
        {
            string finalName = "final_state_" + state.Key;
            HwPortContract stateInput = AttemptPort(state.Key, HwPortDirection.Input);
            AddPort(stateInput with { Name = finalName, Direction = HwPortDirection.Output });
            nodes.Add(new HwOutputDriveNode("drive_" + finalName, finalName, attemptOutputWires[(3, state.Value)], origin));
        }

        var instances = new List<HwInstanceNode>();
        for (int rank = 0; rank < 4; rank++)
            instances.Add(new HwInstanceNode("attempt_rank" + rank, attemptTop.Id, attemptConnections[rank], new Dictionary<string, string>(), Origin: origin));

        var childModules = control.Design.Modules.Where(m => m.Id != controlTop.Id).Concat(attempt.Design.Modules).ToArray();
        if (childModules.Select(m => m.Id).Append(TopModuleId).Distinct(StringComparer.Ordinal).Count() != childModules.Length + 1)
            throw new InvalidDataException("Assist ranked composition has duplicate child module IDs.");

        var outputs = nodes.OfType<HwOutputDriveNode>().Select(n => n.OutputPort).ToHashSet(StringComparer.Ordinal);
        foreach (string state in stateInputToOutput.Keys)
        {
            for (int rank = 0; rank < 4; rank++)
            {
                if (!attemptOutputWires.ContainsKey((rank, stateInputToOutput[state])))
                    throw new InvalidDataException($"Assist rank {rank} state output is not wired for {state}.");
            }
        }
        if (ports.Any(p => p.Direction == HwPortDirection.Output && !outputs.Contains(p.Name)))
            throw new InvalidDataException("Assist ranked composition has an undriven output port.");
        string[] duplicateNodes = nodes.GroupBy(n => n.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        if (duplicateNodes.Length != 0)
            throw new InvalidDataException("Assist ranked composition duplicate node IDs: " + string.Join(", ", duplicateNodes));

        HwModule top = new(TopModuleId, TopModuleId, ports, nodes, instances,
            new HwHardwareContract("hybridcpu.assist.ordered-typed-candidate-chain.sampled-facts.v1",
                HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var result = new FailClosedHardwareIrValidator().Validate(
            new HwDesign("hwir/v0.1", top.Id, [top, .. childModules], toolchain, []));
        string portBreakdown = string.Join(",", Enumerable.Range(0, 4).Select(i => $"rank{i}Facts={top.Ports.Count(p => p.Name.StartsWith($"rank{i}_fact_", StringComparison.Ordinal))}")) +
            $",initialState={top.Ports.Count(p => p.Name.StartsWith("initial_state_", StringComparison.Ordinal))},control={top.Ports.Count(p => !p.Name.StartsWith("rank", StringComparison.Ordinal) && !p.Name.StartsWith("initial_state_", StringComparison.Ordinal) && !p.Name.StartsWith("final_state_", StringComparison.Ordinal))}";
        return result.Validated ?? throw new InvalidDataException($"Assist ranked candidate chain HWIR refused (modules={childModules.Length + 1}, ports={top.Ports.Count}, nodes={top.Nodes.Count}, instances={top.Instances.Count}; {portBreakdown}): " +
            string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));

        void EnsureSameType(HwPortContract attemptPort, HwPortContract controlPort, string role)
        {
            if (!Equals(attemptPort.Type, controlPort.Type))
                throw new InvalidDataException("Assist candidate and control " + role + " port types differ.");
        }
    }

    private static int RankIndex(string name)
    {
        int separator = name.IndexOf('_');
        if (separator <= 0 || !name.StartsWith("rank", StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected rank-scoped Assist control input: " + name);
        if (!int.TryParse(name.AsSpan(4, separator - 4), out int rank) || rank is < 0 or > 3)
            throw new InvalidDataException("Unexpected Assist rank index: " + name);
        return rank;
    }

    private static Dictionary<string, string> BuildStateMap(HwModule attemptTop)
    {
        var inputs = attemptTop.Ports.Where(p => p.Direction == HwPortDirection.Input).ToDictionary(p => p.Name, StringComparer.Ordinal);
        var outputs = attemptTop.Ports.Where(p => p.Direction == HwPortDirection.Output).ToDictionary(p => p.Name, StringComparer.Ordinal);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (HwPortContract input in inputs.Values)
        {
            string? nextOutput = input.Name switch
            {
                "admission_template_remaining" => "admission_template_next",
                "quota_bp_shared_current" => "quota_next_bp_shared_credits",
                "quota_lsu_carrier_current" => "quota_next_lsu_carrier_credits",
                "quota_dma_carrier_current" => "quota_next_dma_carrier_credits",
                "quota_consumed_shared_read_packed" => "quota_next_consumed_shared_read_packed",
                "quota_remaining_issue_credits" => "quota_next_issue_credits",
                "quota_remaining_line_credits" => "quota_next_line_credits",
                _ when input.Name.StartsWith("admission_", StringComparison.Ordinal) && IsAdmissionDeferredState(input.Name) =>
                    "committed_next_" + input.Name["admission_".Length..],
                _ when input.Name.StartsWith("quota_", StringComparison.Ordinal) &&
                       outputs.ContainsKey("quota_next_" + input.Name["quota_".Length..]) =>
                    "quota_next_" + input.Name["quota_".Length..],
                _ => null
            };
            if (nextOutput is null) continue;
            if (!outputs.TryGetValue(nextOutput, out HwPortContract? output))
                throw new InvalidDataException("Assist current-state input has a missing next-state output: " + input.Name + " -> " + nextOutput);
            if (!Equals(input.Type, output.Type))
                throw new InvalidDataException("Assist current/next state type mismatch: " + input.Name + " -> " + nextOutput);
            map.Add(input.Name, nextOutput);
        }
        foreach (HwPortContract output in outputs.Values.Where(p => IsStateOutput(p.Name)))
        {
            if (!map.ContainsValue(output.Name))
                throw new InvalidDataException("Assist next-state output has no current-state input: " + output.Name);
        }
        return map;
    }

    private static bool IsAdmissionDeferredState(string inputName) => inputName is
        "admission_occupancy" or "admission_remaining_memory_issue_budget" or "admission_remaining_read_issue_budget" or
        "admission_remaining_write_issue_budget" or "admission_consumed_memory_budget_by_bank" or
        "admission_consumed_read_budget_by_bank" or "admission_consumed_write_budget_by_bank" ||
        inputName.StartsWith("admission_occupied", StringComparison.Ordinal);

    private static bool IsStateOutput(string outputName) => outputName == "admission_template_next" ||
        outputName.StartsWith("committed_next_", StringComparison.Ordinal) || outputName.StartsWith("quota_next_", StringComparison.Ordinal);

}
#endif
