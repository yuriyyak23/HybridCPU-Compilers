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
/// Source-bound abstract pipeline-step gate composed with the explicit scalar-load
/// logical-event transition. One enabled, already-admitted CPU_Core invocation permits
/// the memory logical edge before stall evaluation. This is not a physical clock/reset,
/// shared-core CWT arbitration, secure admission, physical RAM, or CPU equivalence model.
/// </summary>
public static class BridgeHwirPipelineMemoryLogicalEvent
{
    public const string StageFlowSha256 = "672e9d2523dd04068dc63650a7f52a7df67a904abe57c5639abb3b8b89d2317e";
    public const string StageFlowPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.PipelineExecution.StageFlow.cs";

    public static HwValidatedDesign Compile(byte[] controllerSource, byte[] bindingSource,
        byte[] stageFlowSource, string baseCommit, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(controllerSource);
        ArgumentNullException.ThrowIfNull(bindingSource);
        ArgumentNullException.ThrowIfNull(stageFlowSource);
        if (Hash(stageFlowSource) != StageFlowSha256)
            throw new InvalidDataException("Pipeline-step source bytes changed.");

        var flowRoot = Parse(stageFlowSource);
        MethodDeclarationSyntax flowMethod = flowRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "ExecuteUnparkedPipelineCycle");
        string flow = string.Concat(flowMethod.DescendantTokens().Select(t => t.Text));
        string[] orderedSourceFacts =
        [
            "if(!pipeCtrl.Enabled)return;",
            "pipeCtrl.CycleCount++;",
            "MemoryCyclePlatformOrchestrator.AdvanceCoreObservedPlatformEdge(memorySubsystem.CycleController,CoreID,pipeCtrl.CycleCount);",
            "RefreshInFlightExplicitMemoryProgress();",
            "PipelineStage_WriteBack();",
            "PipelineStage_Memory();",
            "PipelineCycleStallDecisionhazardStallDecision=ResolvePipelineHazardStallDecision();",
            "if(hazardStallDecision.ShouldStall)"
        ];
        int cursor = -1;
        foreach (string fact in orderedSourceFacts)
        {
            int next = flow.IndexOf(fact, cursor + 1, StringComparison.Ordinal);
            if (next < 0)
                throw new InvalidDataException("Pipeline enable/edge/stall order changed: " + fact);
            cursor = next;
        }
        if (Count(flow, "MemoryCyclePlatformOrchestrator.AdvanceCoreObservedPlatformEdge(") != 1 ||
            !flow.Contains("if(memorySubsystem!=null){MemoryCyclePlatformOrchestrator.AdvanceCoreObservedPlatformEdge(", StringComparison.Ordinal))
            throw new InvalidDataException("Memory edge is no longer guarded by a bound MemorySubsystem.");

        HwValidatedDesign memory = BridgeHwirExplicitScalarLoadLogicalEvent.Compile(
            controllerSource, bindingSource, baseCommit, toolchain);
        HwModule memorySource = memory.Design.Modules.Single();
        HwSourceOrigin memoryOrigin = memorySource.Origin! with
        {
            IdentityDigest = IdentityDigest(memory.Design.Toolchain, controllerSource, bindingSource, stageFlowSource)
        };
        HwSourceOrigin flowOrigin = new(
            "https://github.com/yuriyyak23/HybridCPU-v2", baseCommit, StageFlowPath,
            "HybridCPU_ISE", "YAKSys_Hybrid_CPU.Processor.CPU_Core",
            "ExecuteUnparkedPipelineCycle", IdentityDigest(memory.Design.Toolchain, stageFlowSource),
            HwSourceRole.Primary,
            flowMethod.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            flowMethod.Identifier.GetLocation().GetLineSpan().StartLinePosition.Character + 1);

        HwBitVectorType bit = new(1);
        HwPortContract edgeInput = new("pipeline_enabled", HwPortDirection.Input, bit);
        HwModule memoryStep = AddEnabledGate(memorySource, edgeInput, memoryOrigin);
        HwModule gate = BuildGate(memory.Design.Toolchain, flowOrigin, bit);
        HwModule top = BuildTop(memoryStep, gate, flowOrigin, bit);
        HwDesign design = memory.Design with
        {
            TopModule = top.Id,
            Modules = [memoryStep, gate, top]
        };
        HwValidationResult result = new FailClosedHardwareIrValidator().Validate(design);
        return result.Validated ?? throw new InvalidDataException(
            "Pipeline-step memory HWIR refused: " + string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    private static HwModule AddEnabledGate(HwModule source, HwPortContract enabledPort, HwSourceOrigin origin)
    {
        if (source.Nodes.OfType<HwOutputDriveNode>().SingleOrDefault(n => n.OutputPort == "valid") is not { } validDrive ||
            source.Nodes.OfType<HwOperationNode>().SingleOrDefault(n => n.Id == validDrive.Value) is null)
            throw new InvalidDataException("Memory event valid node/output changed.");
        string rawValidId = validDrive.Value;
        string gatedValidId = "pipeline_gate_valid";
        HwNode Rewire(HwNode node) => node switch
        {
            HwOperationNode op when op.Id == rawValidId => op with { Id = "request_valid", Origin = origin },
            HwOperationNode op => op with
            {
                Operands = op.Operands.Select(id => id == rawValidId ? gatedValidId : id).ToArray(),
                Origin = origin
            },
            HwMuxNode mux => mux with
            {
                Condition = mux.Condition == rawValidId ? gatedValidId : mux.Condition,
                WhenTrue = mux.WhenTrue == rawValidId ? gatedValidId : mux.WhenTrue,
                WhenFalse = mux.WhenFalse == rawValidId ? gatedValidId : mux.WhenFalse,
                Origin = origin
            },
            HwConstantNode c => c with { Origin = origin },
            HwOutputDriveNode d when d.OutputPort == "valid" => d with { Value = gatedValidId, Origin = origin },
            HwOutputDriveNode d => d with { Value = d.Value == rawValidId ? gatedValidId : d.Value, Origin = origin },
            _ => throw new InvalidDataException("Unexpected node in bounded memory event leaf: " + node.GetType().Name)
        };
        List<HwNode> nodes = source.Nodes.Select(Rewire).ToList();
        nodes.Add(new HwOperationNode(gatedValidId, "and", ["request_valid", "pipeline_enabled"],
            new HwBitVectorType(1), Origin: origin));
        HwPortContract[] ports = source.Ports.Append(enabledPort).ToArray();
        HwHardwareContract contract = source.Contract! with { Ports = ports };
        return source with
        {
            Id = "pipeline_gated_explicit_scalar_load_logical_event",
            Name = "pipeline_gated_explicit_scalar_load_logical_event",
            Ports = ports,
            Nodes = nodes,
            Contract = contract,
            Origin = origin
        };
    }

    private static HwModule BuildGate(HwToolchainIdentity toolchain, HwSourceOrigin origin, HwBitVectorType bit)
    {
        HwPortContract[] ports =
        [
            new("pipe_enabled", HwPortDirection.Input, bit),
            new("controller_edge_enable", HwPortDirection.Output, bit)
        ];
        return new HwModule("pipeline_step_gate", "pipeline_step_gate", ports,
            [new HwOutputDriveNode("enable_drive", "controller_edge_enable", "pipe_enabled", origin)], [],
            new HwHardwareContract("hybridcpu.pipeline-step.memory-edge.abstract.v1", HwModuleKind.Combinational,
                ports, new HwTimingContract(0, 1)), origin);
    }

    private static HwModule BuildTop(HwModule memory, HwModule gate, HwSourceOrigin origin, HwBitVectorType bit)
    {
        HwPortContract[] memoryInputs = memory.Ports.Where(p => p.Direction == HwPortDirection.Input && p.Name != "pipeline_enabled").ToArray();
        HwPortContract[] memoryOutputs = memory.Ports.Where(p => p.Direction == HwPortDirection.Output).ToArray();
        HwPortContract[] ports = memoryInputs
            .Append(new HwPortContract("pipeline_enabled", HwPortDirection.Input, bit))
            .Concat(memoryOutputs)
            .Append(new HwPortContract("pipeline_edge", HwPortDirection.Output, bit))
            .ToArray();
        List<HwNode> nodes = [];
        Dictionary<string, string> gatePorts = new(StringComparer.Ordinal)
        {
            ["pipe_enabled"] = "pipeline_enabled",
            ["controller_edge_enable"] = "pipeline_edge_net"
        };
        nodes.Add(new HwWireNode("pipeline_edge_net", bit, origin));
        Dictionary<string, string> memoryPorts = new(StringComparer.Ordinal)
        {
            ["pipeline_enabled"] = "pipeline_edge_net"
        };
        foreach (HwPortContract input in memoryInputs)
            memoryPorts[input.Name] = input.Name;
        foreach (HwPortContract output in memoryOutputs)
        {
            string wire = "memory_" + output.Name;
            nodes.Add(new HwWireNode(wire, output.Type, origin));
            memoryPorts[output.Name] = wire;
        }
        foreach (HwPortContract output in memoryOutputs)
            nodes.Add(new HwOutputDriveNode("drive_" + output.Name, output.Name, "memory_" + output.Name, origin));
        nodes.Add(new HwOutputDriveNode("drive_pipeline_edge", "pipeline_edge", "pipeline_edge_net", origin));
        HwInstanceNode gateInstance = new("gate_i", gate.Id, gatePorts,
            new Dictionary<string, string>(StringComparer.Ordinal), Origin: origin);
        HwInstanceNode memoryInstance = new("memory_event_i", memory.Id, memoryPorts,
            new Dictionary<string, string>(StringComparer.Ordinal), Origin: origin);
        return new HwModule("pipeline_memory_logical_step", "pipeline_memory_logical_step", ports, nodes,
            [gateInstance, memoryInstance],
            new HwHardwareContract("hybridcpu.pipeline-memory-logical-step.unqualified.v1", HwModuleKind.Combinational,
                ports, new HwTimingContract(0, 1)), origin);
    }

    private static SyntaxNode Parse(byte[] bytes)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')).GetRoot();
        if (root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException("Invalid pipeline-step source syntax.");
        return root;
    }

    private static int Count(string value, string needle)
    {
        int count = 0, start = 0;
        while ((start = value.IndexOf(needle, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += needle.Length;
        }
        return count;
    }

    private static string IdentityDigest(HwToolchainIdentity toolchain, params byte[][] sources)
    {
        string material = "pipeline-memory-logical-step/v1|" + toolchain.SdkVersion + "|" +
            toolchain.LanguageVersion + "|" + toolchain.HardwareLanguageProfile + "|" +
            string.Join('|', sources.Select(Hash));
        return Hash(Encoding.UTF8.GetBytes(material));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
