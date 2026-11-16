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
/// Source-pinned software call-event projection from ExecuteUnparkedPipelineCycle
/// into the already-built CsrFile HardwareWrite event. This is not a physical clock.
/// </summary>
public static class BridgeHwirStageFlowCycleCsr
{
    public const string StageFlowSha256 = "672e9d2523dd04068dc63650a7f52a7df67a904abe57c5639abb3b8b89d2317e";
    public const string PipelineControlSha256 = "9be70cbfb72a355bcf5ca4692d422031b5d7b078da54919ac28f2af95f4fb7ed";
    public const string TelemetryStateSha256 = "a020111f94dc136082ad7476b5fd8310f11589b87fdc9ebfef78622b91c22496";
    public const string AddressesSha256 = "a2be00ba7d3248bc0aa4e3a49e238a5e0afdd919b5cf1c0d9d2e0bfa01893ef9";

    public static HwValidatedDesign Compile(byte[] stageFlowSource, byte[] pipelineControlSource,
        byte[] telemetryStateSource, byte[] addressesSource, HwValidatedDesign csrStorage,
        string baseCommit)
    {
        ArgumentNullException.ThrowIfNull(stageFlowSource);
        ArgumentNullException.ThrowIfNull(pipelineControlSource);
        ArgumentNullException.ThrowIfNull(telemetryStateSource);
        ArgumentNullException.ThrowIfNull(addressesSource);
        ArgumentNullException.ThrowIfNull(csrStorage);
        if (Hash(stageFlowSource) != StageFlowSha256 || Hash(pipelineControlSource) != PipelineControlSha256 ||
            Hash(telemetryStateSource) != TelemetryStateSha256 || Hash(addressesSource) != AddressesSha256)
            throw new InvalidDataException("StageFlow/Cycle CSR source bytes changed; rebuild the crosswalk.");
        if (baseCommit != "1bfe41a49cf11c80fa6c6aee87accb54b78b331f" ||
            csrStorage.Design.Toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("Pinned active model identity and combinational storage candidate required.");

        static SyntaxNode Parse(byte[] bytes, string label)
        {
            var tree = CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Invalid " + label + " syntax.");
            return tree.GetRoot();
        }
        var stageRoot = Parse(stageFlowSource, "StageFlow");
        var controlRoot = Parse(pipelineControlSource, "PipelineControl");
        var telemetryRoot = Parse(telemetryStateSource, "TelemetryState");
        var addressRoot = Parse(addressesSource, "CsrAddresses");
        var cpu = stageRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(x => x.Identifier.ValueText == "CPU_Core");
        var method = cpu.Members.OfType<MethodDeclarationSyntax>()
            .Single(x => x.Identifier.ValueText == "ExecuteUnparkedPipelineCycle");
        bool enabledEarlyReturn = method.DescendantNodes().OfType<IfStatementSyntax>().Any(x =>
            x.Condition.ToString() == "!pipeCtrl.Enabled" && x.Statement is ReturnStatementSyntax);
        PostfixUnaryExpressionSyntax? increment = method.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>()
            .SingleOrDefault(x => x.ToString() == "pipeCtrl.CycleCount++");
        InvocationExpressionSyntax? hardwareWrite = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .SingleOrDefault(x => x.Expression.ToString() == "Csr.HardwareWrite");
        if (!enabledEarlyReturn || increment is null || hardwareWrite is null || hardwareWrite.ArgumentList.Arguments.Count != 2 ||
            hardwareWrite.ArgumentList.Arguments[0].Expression.ToString() != "CsrAddresses.Cycle" ||
            hardwareWrite.ArgumentList.Arguments[1].Expression.ToString() != "pipeCtrl.CycleCount" ||
            increment.SpanStart >= hardwareWrite.SpanStart || method.ToString().IndexOf("Csr.HardwareWrite", StringComparison.Ordinal) >=
                method.ToString().IndexOf("GetBoundMemorySubsystem", StringComparison.Ordinal))
            throw new InvalidDataException("StageFlow cycle increment/HardwareWrite order or enable guard changed.");

        var pipelineControl = controlRoot.DescendantNodes().OfType<StructDeclarationSyntax>()
            .Single(x => x.Identifier.ValueText == "PipelineControl");
        var cycleProperty = pipelineControl.Members.OfType<PropertyDeclarationSyntax>()
            .SingleOrDefault(x => x.Identifier.ValueText == "CycleCount");
        if (cycleProperty?.Type.ToString() != "ulong" ||
            !cycleProperty.AccessorList!.Accessors.Any(x => x.Keyword.ValueText == "get" &&
                (x.ExpressionBody?.Expression.ToString() == "Telemetry.CycleCount" ||
                 x.Body?.Statements.ToString().Contains("Telemetry.CycleCount", StringComparison.Ordinal) == true)) ||
            !pipelineControl.Members.OfType<FieldDeclarationSyntax>().Any(x => x.Declaration.Type.ToString() == "bool" &&
                x.Declaration.Variables.Any(v => v.Identifier.ValueText == "Enabled")))
            throw new InvalidDataException("PipelineControl CycleCount/Enabled source contract changed.");
        var telemetry = telemetryRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(x => x.Identifier.ValueText == "TelemetryState");
        if (!telemetry.Members.OfType<FieldDeclarationSyntax>().Any(x => x.Declaration.Type.ToString() == "ulong" &&
            x.Declaration.Variables.Any(v => v.Identifier.ValueText == "CycleCount")))
            throw new InvalidDataException("TelemetryState.CycleCount is no longer ulong.");
        var cycleAddress = addressRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(x => x.Identifier.ValueText == "CsrAddresses").Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(x => x.Declaration.Variables).Single(x => x.Identifier.ValueText == "Cycle");
        if (cycleAddress.Initializer?.Value is not LiteralExpressionSyntax cycleLiteral || cycleLiteral.Token.Value is null)
            throw new InvalidDataException("CsrAddresses.Cycle is not a literal constant.");
        ushort cycleCsrAddress = Convert.ToUInt16(cycleLiteral.Token.Value, System.Globalization.CultureInfo.InvariantCulture);

        HwModule storage = csrStorage.Design.Modules.Single(m => m.Id == csrStorage.Design.TopModule);
        HwSourceOrigin origin = new("https://github.com/yuriyyak23/HybridCPU-v2", baseCommit,
            "HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.PipelineExecution.StageFlow.cs",
            "CircuitWeave.StageFlowCycleCsrWrite", "YAKSys_Hybrid_CPU.Processor.CPU_Core",
            "ExecuteUnparkedPipelineCycle", Hash(Encoding.UTF8.GetBytes(string.Join("|", new[]
                { StageFlowSha256, PipelineControlSha256, TelemetryStateSha256, AddressesSha256, csrStorage.CanonicalDigest }))),
            HwSourceRole.Primary, hardwareWrite.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            hardwareWrite.Expression.GetLocation().GetLineSpan().StartLinePosition.Character + 1);

        var bit = new HwBitVectorType(1); var u3 = new HwBitVectorType(3); var u16 = new HwBitVectorType(16); var u64 = new HwBitVectorType(64);
        List<HwPortContract> ports = [];
        List<HwNode> nodes = [];
        List<HwInstanceNode> instances = [];
        void Input(string name, HwBitVectorType type) => ports.Add(new(name, HwPortDirection.Input, type));
        void Output(string name, HwBitVectorType type) => ports.Add(new(name, HwPortDirection.Output, type));
        string Const(string id, ulong value, HwBitVectorType type)
        {
            string pattern = Convert.ToString(unchecked((long)value), 2).PadLeft(type.Width, '0');
            string nodeId = "const_" + id; nodes.Add(new HwConstantNode(nodeId, type, pattern[^type.Width..], origin)); return nodeId;
        }
        Input("pipeline_call_reached_unparked_body", bit);
        Input("pipeline_enabled", bit);
        Input("storage_reservation_acquired", bit);
        Input("cycle_count_before", u64);
        foreach (HwPortContract port in storage.Ports.Where(p => p.Direction == HwPortDirection.Input && p.Name.StartsWith("current_", StringComparison.Ordinal)))
            Input(port.Name, new HwBitVectorType(port.Type.Width));
        string callReached = "pipeline_call_reached_unparked_body";
        string enabled = "pipeline_enabled";
        string reservationAcquired = "storage_reservation_acquired";
        string cycleEvent = "n_cycle_update_event";
        string incremented = "n_cycle_count_incremented";
        string cycleNext = "n_cycle_count_next";
        string one = Const("one64", 1, u64);
        nodes.Add(new HwOperationNode(incremented, "add", ["cycle_count_before", one], u64, Overflow: HwOverflowMode.Wrap, Origin: origin));
        nodes.Add(new HwOperationNode(cycleEvent, "and", [callReached, enabled], bit, Origin: origin));
        nodes.Add(new HwMuxNode(cycleNext, cycleEvent, incremented, "cycle_count_before", u64, origin));
        string reservationFailed = "n_storage_reservation_failed";
        string notAcquired = "n_storage_reservation_not_acquired";
        string oneBit = Const("one_bit", 1, bit);
        nodes.Add(new HwOperationNode(notAcquired, "xor", [reservationAcquired, oneBit], bit, Origin: origin));
        nodes.Add(new HwOperationNode(reservationFailed, "and", [cycleEvent, notAcquired], bit, Origin: origin));
        string storageEvent = "n_csr_hardware_write_event";
        nodes.Add(new HwOperationNode(storageEvent, "and", [cycleEvent, reservationAcquired], bit, Origin: origin));
        Output("cycle_count_next", u64); Output("cycle_update_event", bit); Output("storage_reservation_failed", bit);
        nodes.Add(new HwOutputDriveNode("drive_cycle_count_next", "cycle_count_next", cycleNext, origin));
        nodes.Add(new HwOutputDriveNode("drive_cycle_event", "cycle_update_event", cycleEvent, origin));
        nodes.Add(new HwOutputDriveNode("drive_reservation_failed", "storage_reservation_failed", reservationFailed, origin));

        var connections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (HwPortContract port in storage.Ports)
        {
            if (port.Direction == HwPortDirection.Input)
            {
                string local = port.Name switch
                {
                    "event_valid" => storageEvent,
                    "operation" => Const("operation_hardware_write", 2, u3),
                    "address" => Const("cycle_address", cycleCsrAddress, u16),
                    "write_data" => incremented,
                    "privilege" => Const("privilege_unused", 0, new HwBitVectorType(port.Type.Width)),
                    _ when port.Name.StartsWith("current_", StringComparison.Ordinal) => port.Name,
                    _ => throw new InvalidDataException("Unexpected CSR storage input port: " + port.Name)
                };
                connections.Add(port.Name, local);
            }
            else
            {
                string local = port.Name.StartsWith("next_", StringComparison.Ordinal) ? port.Name : "csr_" + port.Name;
                Output(local, new HwBitVectorType(port.Type.Width));
                string internalWire = "wire_internal_" + local;
                nodes.Add(new HwWireNode(internalWire, new HwBitVectorType(port.Type.Width), origin));
                nodes.Add(new HwOutputDriveNode("drive_" + local, local, internalWire, origin));
                connections.Add(port.Name, internalWire);
            }
        }
        instances.Add(new HwInstanceNode("csr_storage_event", storage.Id, connections,
            new Dictionary<string, string>(StringComparer.Ordinal), Origin: origin));
        var topPorts = ports.ToArray();
        const string moduleName = "stageflow_cycle_to_csr_hardware_write_projection";
        var top = new HwModule(moduleName, moduleName, topPorts, nodes, instances,
            new HwHardwareContract("processor.stageflow-cycle.csr-hardware-write.call-event.unqualified.v1",
                HwModuleKind.Combinational, topPorts, new HwTimingContract(0, 1)), origin);
        HwModule[] modules = [top, .. csrStorage.Design.Modules];
        var result = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", moduleName, modules,
            csrStorage.Design.Toolchain, []));
        return result.Validated ?? throw new InvalidDataException("StageFlow→CSR HWIR refused: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
