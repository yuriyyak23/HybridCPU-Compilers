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
/// Combinational projection of the materialized WB/MEM/EX fault winner resolver.
/// Lane occupancy, fault, and slot-index facts are sampled from stage state; this is not sequential state.
/// </summary>
public static class BridgeHwirStageAwareFaultWinner
{
    private const string ActiveSha = "e1fd930195f41a1a79694f36868cb44325db7c2970c5893ea686ec649d71cb8e";
    private const string GoldenSha = "e599b81db714cbb2a169b28f3eac4bcffa8e58141814624dc1df3891a9af45e7";
    private const string ActiveStageEnumSha = "f84c179da79ff204159a006aceb006e8eae865060d0c0e40db8a774d75b6bfa4";
    private const string GoldenStageEnumSha = "5a9e7287988f863e67cbeb29bed1376204746ab84590515387c02c7d0f3606c8";
    private const string ActiveCommit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
    private const string HelperPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.Pipeline.Helpers.cs";

    public static HwValidatedDesign Compile(byte[] activeHelpers, byte[] goldenHelpers,
        byte[] activeStageEnum, byte[] goldenStageEnum, HwToolchainIdentity toolchain)
    {
        if (Hash(activeHelpers) != ActiveSha || Hash(goldenHelpers) != GoldenSha ||
            Hash(activeStageEnum) != ActiveStageEnumSha || Hash(goldenStageEnum) != GoldenStageEnumSha ||
            toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("Winner projection requires exact active and pinned-Golden helper bytes.");
        static SyntaxNode Parse(byte[] bytes)
        {
            var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Winner helper source has C# syntax errors.");
            return tree.GetRoot();
        }
        static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));
        SyntaxNode active = Parse(activeHelpers), golden = Parse(goldenHelpers);
        SyntaxNode activeStages = Parse(activeStageEnum), goldenStages = Parse(goldenStageEnum);
        static int StageValue(SyntaxNode root, string member)
        {
            EnumDeclarationSyntax declaration = root.DescendantNodes().OfType<EnumDeclarationSyntax>()
                .Single(e => e.Identifier.ValueText == "PipelineStage");
            EnumMemberDeclarationSyntax item = declaration.Members.Single(m => m.Identifier.ValueText == member);
            if (item.EqualsValue is null || !int.TryParse(item.EqualsValue.Value.ToString(),
                    System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value))
                throw new InvalidDataException("PipelineStage must use explicit integer values: " + member);
            return value;
        }
        int writeBackStage = StageValue(activeStages, "WriteBack");
        int memoryStage = StageValue(activeStages, "Memory");
        int executeStage = StageValue(activeStages, "Execute");
        if (writeBackStage != 5 || memoryStage != 4 || executeStage != 3 ||
            Tokens(activeStages.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(e => e.Identifier.ValueText == "PipelineStage")) !=
            Tokens(goldenStages.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(e => e.Identifier.ValueText == "PipelineStage")))
            throw new InvalidDataException("Active and pinned-Golden PipelineStage enum differs or has unexpected selected values.");
        string[] methods = ["TryResolveStageAwareExceptionWinner", "ResolveWriteBackFaultMask", "ResolveMemoryFaultMask",
            "ResolveExecuteFaultMask", "TryResolveOldestOrderedWriteBackFaultLane", "TryResolveOldestOrderedMemoryFaultLane",
            "TryResolveOldestOrderedExecuteFaultLane", "CompareWriteBackLaneOrder", "CompareMemoryLaneOrder", "CompareExecuteLaneOrder",
            "TryResolveWriteBackLaneOrderKey", "TryResolveMemoryLaneOrderKey", "TryResolveExecuteLaneOrderKey"];
        foreach (string name in methods)
        {
            MethodDeclarationSyntax[] a = active.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.ValueText == name).ToArray();
            MethodDeclarationSyntax[] g = golden.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.ValueText == name).ToArray();
            // The winner name has two overloads: compare the full lane-state overload only.
            if (name == "TryResolveStageAwareExceptionWinner")
            {
                a = a.Where(m => m.ParameterList.Parameters.FirstOrDefault()?.Type?.ToString().EndsWith("WriteBackStage", StringComparison.Ordinal) == true).ToArray();
                g = g.Where(m => m.ParameterList.Parameters.FirstOrDefault()?.Type?.ToString().EndsWith("WriteBackStage", StringComparison.Ordinal) == true).ToArray();
            }
            if (a.Length != 1 || g.Length != 1 || Tokens(a[0]) != Tokens(g[0]))
                throw new InvalidDataException("Active and Golden arbitration method differs: " + name);
        }
        MethodDeclarationSyntax resolver = active.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "TryResolveStageAwareExceptionWinner" &&
                m.ParameterList.Parameters.FirstOrDefault()?.Type?.ToString().EndsWith("WriteBackStage", StringComparison.Ordinal) == true);
        string body = Tokens(resolver);
        if (!(body.IndexOf("TryResolveOldestOrderedWriteBackFaultLane", StringComparison.Ordinal) <
              body.IndexOf("TryResolveOldestOrderedMemoryFaultLane", StringComparison.Ordinal) &&
              body.IndexOf("TryResolveOldestOrderedMemoryFaultLane", StringComparison.Ordinal) <
              body.IndexOf("TryResolveOldestOrderedExecuteFaultLane", StringComparison.Ordinal)))
            throw new InvalidDataException("Winner stage priority is not WB then MEM then EX.");

        HwBitVectorType bit = new(1), lane = new(8), stage = new(3), key = new(32, HwSignedness.Signed), signedWide = new(64, HwSignedness.Signed);
        List<HwPortContract> ports = [];
        List<HwNode> nodes = [];
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", ActiveCommit,
            HelperPath, "CircuitWeave.StageAwareFaultWinner", "YAKSys_Hybrid_CPU.Processor.CPU_Core",
            "TryResolveStageAwareExceptionWinner", Hash(Encoding.UTF8.GetBytes("stage-aware-winner/v1|" + ActiveSha + "|" + GoldenSha + "|" + ActiveStageEnumSha + "|" + GoldenStageEnumSha)),
            HwSourceRole.Primary, 813, 13);
        string Const(string id, HwBitVectorType type, int value)
        {
            nodes.Add(new HwConstantNode(id, type, Convert.ToString(value, 2).PadLeft(type.Width, '0'), origin));
            return id;
        }
        string Port(string name, HwBitVectorType type)
        {
            ports.Add(new(name, HwPortDirection.Input, type)); return name;
        }
        int next = 0;
        string Op(string opcode, string left, string right, HwBitVectorType type)
        {
            string id = "n" + next++.ToString(System.Globalization.CultureInfo.InvariantCulture);
            nodes.Add(new HwOperationNode(id, opcode, [left, right], type, Origin: origin)); return id;
        }
        string And(string a, string b) => Op("and", a, b, bit);
        string Or(string a, string b) => Op("or", a, b, bit);
        string Eq(string a, string b) => Op("eq", a, b, bit);
        string Sext(string value)
        {
            string id = "n" + next++.ToString(System.Globalization.CultureInfo.InvariantCulture);
            nodes.Add(new HwOperationNode(id, "sext", [value], signedWide, Origin: origin)); return id;
        }
        string Mux(string cond, string a, string b, HwBitVectorType type)
        {
            string id = "n" + next++.ToString(System.Globalization.CultureInfo.InvariantCulture);
            nodes.Add(new HwMuxNode(id, cond, a, b, type, origin)); return id;
        }
        string Invert(string value, string zeroValue) => Eq(value, zeroValue);

        string[] stages = ["wb", "mem", "ex"];
        string[] hasWinnerByStage = new string[3], laneByStage = new string[3];
        string zero = Const("zero_bit", bit, 0);
        string laneNone = Const("no_lane_255", lane, 255), stageNone = Const("stage_none", stage, 0);
        for (int s = 0; s < 3; s++)
        {
            string prefix = stages[s];
            string explicitKeys = Port(prefix + "_uses_explicit_packet_lanes", bit);
            string[] eligible = new string[8], keysUsable = new string[8], keys = new string[8];
            for (int i = 0; i < 8; i++)
            {
                string occupied = Port($"{prefix}_lane{i}_occupied", bit);
                string fault = Port($"{prefix}_lane{i}_has_fault", bit);
                keys[i] = Port($"{prefix}_lane{i}_slot_index", key);
                eligible[i] = And(occupied, fault);
                string wideKey = Sext(keys[i]);
                string lessThanEight = Op("slt64", wideKey, Const($"{prefix}_slot_index_eight_{i}", signedWide, 8), bit);
                keysUsable[i] = And(explicitKeys, And(occupied, lessThanEight));
            }
            string found = zero, winnerLane = laneNone, currentKey = Const(prefix + "_initial_key", signedWide, 0);
            string currentKeyUsable = zero;
            for (int i = 0; i < 8; i++)
            {
                string candidateWide = Sext(keys[i]);
                string keyLess = Op("slt64", candidateWide, currentKey, bit);
                string bothKeys = And(keysUsable[i], currentKeyUsable);
                // Candidates are visited in ascending physical lane order. If either key is unusable,
                // the source comparator falls back to physical lane order and the candidate cannot win.
                string replace = And(eligible[i], Or(Invert(found, zero), And(bothKeys, keyLess)));
                found = Or(found, eligible[i]);
                winnerLane = Mux(replace, Const($"{prefix}_lane_index_{i}", lane, i), winnerLane, lane);
                currentKey = Mux(replace, candidateWide, currentKey, signedWide);
                currentKeyUsable = Mux(replace, keysUsable[i], currentKeyUsable, bit);
            }
            hasWinnerByStage[s] = found;
            laneByStage[s] = winnerLane;
        }
        string winnerValid = Or(hasWinnerByStage[0], Or(hasWinnerByStage[1], hasWinnerByStage[2]));
        string winnerStage = Mux(hasWinnerByStage[0], Const("stage_writeback", stage, writeBackStage),
            Mux(hasWinnerByStage[1], Const("stage_memory", stage, memoryStage),
                Mux(hasWinnerByStage[2], Const("stage_execute", stage, executeStage), stageNone, stage), stage), stage);
        string winnerLaneFinal = Mux(hasWinnerByStage[0], laneByStage[0],
            Mux(hasWinnerByStage[1], laneByStage[1], laneByStage[2], lane), lane);
        ports.Add(new("winner_valid", HwPortDirection.Output, bit));
        ports.Add(new("winner_stage", HwPortDirection.Output, stage));
        ports.Add(new("winner_lane", HwPortDirection.Output, lane));
        nodes.Add(new HwOutputDriveNode("drive_winner_valid", "winner_valid", winnerValid, origin));
        nodes.Add(new HwOutputDriveNode("drive_winner_stage", "winner_stage", winnerStage, origin));
        nodes.Add(new HwOutputDriveNode("drive_winner_lane", "winner_lane", winnerLaneFinal, origin));
        HwModule module = new("stage_aware_fault_winner", "stage_aware_fault_winner", ports, nodes, [],
            new HwHardwareContract("processor.stage-aware-fault-winner.source-projection.v1", HwModuleKind.Combinational,
                ports, new HwTimingContract(0, 1)), origin);
        HwDesign design = new("hwir/v0.1", module.Id, [module], toolchain, []);
        HwValidationResult validation = new FailClosedHardwareIrValidator().Validate(design);
        return validation.Validated ?? throw new InvalidDataException("Winner HWIR refused: " +
            string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
