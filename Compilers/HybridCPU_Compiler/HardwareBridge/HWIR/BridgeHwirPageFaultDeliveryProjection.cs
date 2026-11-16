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
/// Projects the already-resolved stage winner through the MEM PageFault delivery
/// branch. Winner arbitration remains owned by TryResolveStageAwareExceptionWinnerMetadata.
/// This is a combinational decision projection, not clocked trap state or CSR delivery.
/// </summary>
public static class BridgeHwirPageFaultDeliveryProjection
{
    private const string ActiveCommit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
    private const string GoldenCommit = "6946ccab9d888f7dae0a4d8e18480f436138acd9";
    private const string ActiveExceptionsSha = "7be87fb6ab1db964e1b4ef8fd17e8bbaf010662c31d173c99684dca6bb2b400f";
    private const string GoldenExceptionsSha = "fad6980d48ea651b237f2df350cbfcbee64ba31bba4503827de9cbfa7e6cb7e8";
    private const string ActiveHelpersSha = "e1fd930195f41a1a79694f36868cb44325db7c2970c5893ea686ec649d71cb8e";
    private const string GoldenHelpersSha = "e599b81db714cbb2a169b28f3eac4bcffa8e58141814624dc1df3891a9af45e7";
    private const string ActiveStageEnumSha = "f84c179da79ff204159a006aceb006e8eae865060d0c0e40db8a774d75b6bfa4";
    private const string GoldenStageEnumSha = "5a9e7287988f863e67cbeb29bed1376204746ab84590515387c02c7d0f3606c8";

    public static HwValidatedDesign Compile(
        byte[] activeExceptions, byte[] activeHelpers,
        byte[] activeStageEnum, byte[] goldenExceptions, byte[] goldenHelpers, byte[] goldenStageEnum,
        HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(toolchain);
        if (Hash(activeExceptions) != ActiveExceptionsSha || Hash(activeHelpers) != ActiveHelpersSha ||
            Hash(goldenExceptions) != GoldenExceptionsSha || Hash(goldenHelpers) != GoldenHelpersSha ||
            Hash(activeStageEnum) != ActiveStageEnumSha || Hash(goldenStageEnum) != GoldenStageEnumSha ||
            toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("Page-fault delivery inputs differ from the pinned active bytes, exact Golden materialization, or bounded combinational profile.");

        static SyntaxNode Parse(byte[] bytes)
        {
            var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Page-fault delivery source contains C# syntax errors.");
            return tree.GetRoot();
        }

        static MethodDeclarationSyntax Method(SyntaxNode root, string name) => root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == name);
        static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(token => token.Text));
        static HwSourceOrigin Origin(SyntaxNode node, string path, string owner, string member, string identity)
        {
            Location sourceLocation = node switch
            {
                MethodDeclarationSyntax method => method.Identifier.GetLocation(),
                EnumDeclarationSyntax declaration => declaration.Identifier.GetLocation(),
                _ => node.GetLocation()
            };
            FileLinePositionSpan span = sourceLocation.GetLineSpan();
            return new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", ActiveCommit,
                path, "CircuitWeave.PageFaultDeliveryProjection", owner, member,
                Hash(Encoding.UTF8.GetBytes(identity)), HwSourceRole.Primary,
                span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
        }

        SyntaxNode activeExceptionRoot = Parse(activeExceptions), goldenExceptionRoot = Parse(goldenExceptions);
        SyntaxNode activeHelperRoot = Parse(activeHelpers), goldenHelperRoot = Parse(goldenHelpers);
        SyntaxNode activeStageRoot = Parse(activeStageEnum), goldenStageRoot = Parse(goldenStageEnum);
        MethodDeclarationSyntax activeMark = Method(activeExceptionRoot, "MarkActiveMemoryLanePageFault");
        MethodDeclarationSyntax goldenMark = Method(goldenExceptionRoot, "MarkActiveMemoryLanePageFault");
        MethodDeclarationSyntax activeDelivery = Method(activeExceptionRoot, "DeliverStageAwareMemoryPageFault");
        MethodDeclarationSyntax goldenDelivery = Method(goldenExceptionRoot, "DeliverStageAwareMemoryPageFault");
        MethodDeclarationSyntax activeWinner = activeHelperRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "TryResolveStageAwareExceptionWinner" &&
                method.ParameterList.Parameters.FirstOrDefault()?.Type?.ToString().EndsWith("WriteBackStage", StringComparison.Ordinal) == true);
        MethodDeclarationSyntax goldenWinner = goldenHelperRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "TryResolveStageAwareExceptionWinner" &&
                method.ParameterList.Parameters.FirstOrDefault()?.Type?.ToString().EndsWith("WriteBackStage", StringComparison.Ordinal) == true);
        EnumDeclarationSyntax activePipelineStage = activeStageRoot.DescendantNodes().OfType<EnumDeclarationSyntax>()
            .Single(declaration => declaration.Identifier.ValueText == "PipelineStage");
        EnumDeclarationSyntax goldenPipelineStage = goldenStageRoot.DescendantNodes().OfType<EnumDeclarationSyntax>()
            .Single(declaration => declaration.Identifier.ValueText == "PipelineStage");
        string markTokens = Tokens(activeMark), deliveryTokens = Tokens(activeDelivery);
        string winnerTokens = Tokens(activeWinner);
        string goldenMarkTokens = Tokens(goldenMark), goldenDeliveryTokens = Tokens(goldenDelivery);
        string goldenWinnerTokens = Tokens(goldenWinner);
        string stageEnumTokens = Tokens(activePipelineStage), goldenStageEnumTokens = Tokens(goldenPipelineStage);
        static int EnumValue(EnumDeclarationSyntax declaration, string name)
        {
            EnumMemberDeclarationSyntax member = declaration.Members.Single(item => item.Identifier.ValueText == name);
            if (member.EqualsValue is null || !int.TryParse(member.EqualsValue.Value.ToString(),
                    System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value))
                throw new InvalidDataException("PipelineStage member must have one explicit integer value: " + name);
            return value;
        }
        int writeBackStageValue = EnumValue(activePipelineStage, "WriteBack");
        int memoryStageValue = EnumValue(activePipelineStage, "Memory");
        int executeStageValue = EnumValue(activePipelineStage, "Execute");
        bool[] sourceChecks =
        [
            markTokens == goldenMarkTokens,
            deliveryTokens == goldenDeliveryTokens,
            winnerTokens == goldenWinnerTokens,
            markTokens.Contains("pageFaultException.FaultAddress", StringComparison.Ordinal),
            markTokens.Contains("pageFaultException.IsWrite", StringComparison.Ordinal),
            deliveryTokens.Contains("TryResolveStageAwareExceptionWinnerMetadata", StringComparison.Ordinal),
            deliveryTokens.Contains("winnerMetadata.WinnerStage==PipelineStage.WriteBack", StringComparison.Ordinal),
            deliveryTokens.Contains("winnerMetadata.FaultAddress", StringComparison.Ordinal),
            deliveryTokens.Contains("winnerMetadata.FaultIsWrite", StringComparison.Ordinal),
            deliveryTokens.Contains("throwolderStageFault", StringComparison.Ordinal),
            deliveryTokens.Contains("winnerMetadata.WinnerStage!=PipelineStage.Memory", StringComparison.Ordinal),
            deliveryTokens.Contains("pipeMEM.ActiveLaneIndex=winnerMetadata.WinnerLaneIndex", StringComparison.Ordinal),
            deliveryTokens.Contains("throwpageFaultException", StringComparison.Ordinal),
            winnerTokens.Contains("PipelineStage.WriteBack", StringComparison.Ordinal),
            winnerTokens.Contains("PipelineStage.Memory", StringComparison.Ordinal),
            winnerTokens.Contains("PipelineStage.Execute", StringComparison.Ordinal),
            winnerTokens.IndexOf("TryResolveOldestOrderedWriteBackFaultLane", StringComparison.Ordinal) <
                winnerTokens.IndexOf("TryResolveOldestOrderedMemoryFaultLane", StringComparison.Ordinal) &&
            winnerTokens.IndexOf("TryResolveOldestOrderedMemoryFaultLane", StringComparison.Ordinal) <
                winnerTokens.IndexOf("TryResolveOldestOrderedExecuteFaultLane", StringComparison.Ordinal),
            stageEnumTokens == goldenStageEnumTokens,
            writeBackStageValue == 5 && memoryStageValue == 4 && executeStageValue == 3
        ];
        if (sourceChecks.Any(check => !check))
            throw new InvalidDataException("Page-fault delivery branch or WB→MEM→EX winner contract differs from exact active/Golden source: " +
                string.Join(",", sourceChecks.Select((passed, index) => $"{index}={passed}")));

        const string exceptionPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/Faults/CPU_Core.PipelineExecution.Exceptions.cs";
        const string helperPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.Pipeline.Helpers.cs";
        const string stageEnumPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.Pipeline.Helpers.PipelineStage.cs";
        string identity = string.Join("|", "page-fault-delivery-after-stage-winner/v1",
            ActiveExceptionsSha, GoldenExceptionsSha, ActiveHelpersSha, GoldenHelpersSha,
            ActiveStageEnumSha, GoldenStageEnumSha, writeBackStageValue, memoryStageValue, executeStageValue);
        HwSourceOrigin deliveryOrigin = Origin(activeDelivery, exceptionPath,
            "YAKSys_Hybrid_CPU.Processor.CPU_Core", "DeliverStageAwareMemoryPageFault", identity);
        _ = Origin(activeMark, exceptionPath,
            "YAKSys_Hybrid_CPU.Processor.CPU_Core", "MarkActiveMemoryLanePageFault", identity);
        _ = Origin(activeWinner, helperPath,
            "YAKSys_Hybrid_CPU.Processor.CPU_Core", "TryResolveStageAwareExceptionWinner", identity);
        _ = Origin(activePipelineStage, stageEnumPath,
            "YAKSys_Hybrid_CPU.Processor.CPU_Core", "PipelineStage", identity);

        HwBitVectorType bit = new(1), stageType = new(3), laneType = new(3), addressType = new(64);
        const string moduleId = "page_fault_delivery_after_winner";
        HwPortContract[] ports =
        [
            new("incoming_fault_valid", HwPortDirection.Input, bit),
            new("incoming_fault_address", HwPortDirection.Input, addressType),
            new("incoming_fault_is_write", HwPortDirection.Input, bit),
            new("winner_valid", HwPortDirection.Input, bit),
            new("winner_stage", HwPortDirection.Input, stageType),
            new("winner_lane", HwPortDirection.Input, laneType),
            new("winner_fault_address", HwPortDirection.Input, addressType),
            new("winner_fault_is_write", HwPortDirection.Input, bit),
            new("incoming_active_mem_lane", HwPortDirection.Input, laneType),
            new("delivery_valid", HwPortDirection.Output, bit),
            new("delivery_rejected", HwPortDirection.Output, bit),
            new("trap_flush", HwPortDirection.Output, bit),
            new("selected_active_mem_lane", HwPortDirection.Output, laneType),
            new("thrown_valid", HwPortDirection.Output, bit),
            new("thrown_address", HwPortDirection.Output, addressType),
            new("thrown_is_write", HwPortDirection.Output, bit)
        ];
        HwConstantNode wb = new("stage_writeback", stageType, Convert.ToString(writeBackStageValue, 2).PadLeft(3, '0'), deliveryOrigin);
        HwConstantNode mem = new("stage_memory", stageType, Convert.ToString(memoryStageValue, 2).PadLeft(3, '0'), deliveryOrigin);
        HwConstantNode zero = new("zero_bit", bit, "0", deliveryOrigin);
        HwOperationNode isWb = new("winner_is_writeback", "eq", ["winner_stage", wb.Id], bit, Origin: deliveryOrigin);
        HwOperationNode isMem = new("winner_is_memory", "eq", ["winner_stage", mem.Id], bit, Origin: deliveryOrigin);
        HwOperationNode allowedStage = new("winner_is_deliverable_stage", "or", [isWb.Id, isMem.Id], bit, Origin: deliveryOrigin);
        HwOperationNode hasWinner = new("winner_exists", "and", ["winner_valid", allowedStage.Id], bit, Origin: deliveryOrigin);
        HwOperationNode deliveryValid = new("delivery_valid_value", "and", ["incoming_fault_valid", hasWinner.Id], bit, Origin: deliveryOrigin);
        HwOperationNode selectMemoryWinner = new("select_memory_winner", "and", [deliveryValid.Id, isMem.Id], bit, Origin: deliveryOrigin);
        HwOperationNode noWinner = new("winner_missing", "eq", ["winner_valid", zero.Id], bit, Origin: deliveryOrigin);
        HwOperationNode invalidStage = new("winner_stage_invalid", "eq", [allowedStage.Id, zero.Id], bit, Origin: deliveryOrigin);
        HwOperationNode invalidWinner = new("winner_rejected", "or", [noWinner.Id, invalidStage.Id], bit, Origin: deliveryOrigin);
        HwOperationNode deliveryRejected = new("delivery_rejected_value", "and", [invalidWinner.Id, "incoming_fault_valid"], bit, Origin: deliveryOrigin);
        HwMuxNode winnerThrowAddress = new("throw_winner_address", isWb.Id, "winner_fault_address", "incoming_fault_address", addressType, deliveryOrigin);
        HwMuxNode winnerThrowWrite = new("throw_winner_write", isWb.Id, "winner_fault_is_write", "incoming_fault_is_write", bit, deliveryOrigin);
        HwMuxNode selectedLane = new("selected_mem_lane", selectMemoryWinner.Id, "winner_lane", "incoming_active_mem_lane", laneType, deliveryOrigin);
        HwNode[] nodes =
        [
            wb, mem, zero, isWb, isMem, allowedStage, hasWinner, deliveryValid, selectMemoryWinner, noWinner, invalidStage,
            invalidWinner, deliveryRejected, winnerThrowAddress, winnerThrowWrite, selectedLane,
            new HwOutputDriveNode("drive_delivery_valid", "delivery_valid", deliveryValid.Id, deliveryOrigin),
            new HwOutputDriveNode("drive_delivery_rejected", "delivery_rejected", deliveryRejected.Id, deliveryOrigin),
            new HwOutputDriveNode("drive_trap_flush", "trap_flush", deliveryValid.Id, deliveryOrigin),
            new HwOutputDriveNode("drive_selected_mem_lane", "selected_active_mem_lane", selectedLane.Id, deliveryOrigin),
            new HwOutputDriveNode("drive_thrown_valid", "thrown_valid", deliveryValid.Id, deliveryOrigin),
            new HwOutputDriveNode("drive_thrown_address", "thrown_address", winnerThrowAddress.Id, deliveryOrigin),
            new HwOutputDriveNode("drive_thrown_is_write", "thrown_is_write", winnerThrowWrite.Id, deliveryOrigin)
        ];
        HwModule module = new(moduleId, moduleId, ports, nodes, [],
            new HwHardwareContract("processor.page-fault-delivery.after-winner.source-projection.v1",
                HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), deliveryOrigin);
        HwDesign design = new("hwir/v0.1", module.Id, [module], toolchain, []);
        HwValidationResult validation = new FailClosedHardwareIrValidator().Validate(design);
        return validation.Validated ?? throw new InvalidDataException("Page-fault delivery HWIR refused: " +
            string.Join("; ", validation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
