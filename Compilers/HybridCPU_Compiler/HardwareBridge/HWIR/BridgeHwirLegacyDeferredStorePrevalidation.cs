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
/// Combinational projection of the shared legacy no-selected-layout deferred-store
/// retirement prevalidation path. SelectedStoreLayout, typed attempt identity,
/// physical publication, and RAM timing are explicitly outside this module.
/// </summary>
public static class BridgeHwirLegacyDeferredStorePrevalidation
{
    public const string RetirePath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.PipelineExecution.Retire.cs";
    public const string MemoryPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Stages/Memory/CPU_Core.PipelineExecution.Memory.cs";
    public const string ActiveRetireSha256 = "cfdb51be0e465e485ce1c314dcc957e88cd537e3bd5c5fc374eb4a46eda8d184";
    public const string GoldenRetireSha256 = "c98abf4d8133a38c6270de8f20e595a662d3e71731c6771aea056ea220bb6e81";
    public const string ActiveMemorySha256 = "4b62e108e296c6a6733daa0292079e2ca18581a816888f80562c63ec65d0b1db";
    public const string GoldenMemorySha256 = "8fca698f38631ffb0db5a4803140b8d342f3413412cd8a61ad7890b514b3fa14";

    public static HwValidatedDesign Compile(byte[] activeRetire, byte[] goldenRetire,
        byte[] activeMemory, byte[] goldenMemory, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(activeRetire);
        ArgumentNullException.ThrowIfNull(goldenRetire);
        ArgumentNullException.ThrowIfNull(activeMemory);
        ArgumentNullException.ThrowIfNull(goldenMemory);
        ArgumentNullException.ThrowIfNull(toolchain);
        if (Hash(activeRetire) != ActiveRetireSha256 || Hash(goldenRetire) != GoldenRetireSha256 ||
            Hash(activeMemory) != ActiveMemorySha256 || Hash(goldenMemory) != GoldenMemorySha256 ||
            toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("Legacy deferred-store prevalidation requires exact active and pinned-Golden source bytes.");

        static SyntaxNode Parse(byte[] bytes)
        {
            var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Store prevalidation source contains C# syntax errors.");
            return tree.GetRoot();
        }
        static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));
        static MethodDeclarationSyntax Method(SyntaxNode root, string name) => root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);
        static void RequireInOrder(string tokens, params string[] facts)
        {
            int last = -1;
            foreach (string fact in facts)
            {
                int next = tokens.IndexOf(fact, StringComparison.Ordinal);
                if (next <= last) throw new InvalidDataException("Store prevalidation contract source changed: " + fact);
                last = next;
            }
        }
        SyntaxNode aRet = Parse(activeRetire), gRet = Parse(goldenRetire);
        SyntaxNode aMem = Parse(activeMemory), gMem = Parse(goldenMemory);
        string activeCollection = Tokens(Method(aRet, "PrevalidateRetireWindowBatchForPublication"));
        string goldenCollection = Tokens(Method(gRet, "PrevalidateRetireWindowBatchForPublication"));
        const string dispatch = "caseRetireWindowEffectKind.DeferredStoreCommit:PrevalidateDeferredStoreEffect(retireEffect.DeferredStoreLaneIndex);break;";
        RequireInOrder(activeCollection, dispatch);
        RequireInOrder(goldenCollection, dispatch);

        string activeCarrier = Tokens(Method(aRet, "PrevalidateDeferredStoreEffect"));
        string goldenCarrier = Tokens(Method(gRet, "PrevalidateDeferredStoreEffect"));
        const string laneIndexCheck = "if(laneIndex>=8)";
        const string carrierCheck = "if(!lane.IsOccupied||!lane.DefersStoreCommitToWriteBack||!lane.IsMemoryOp||lane.IsLoad)";
        RequireInOrder(activeCarrier, laneIndexCheck, carrierCheck);
        RequireInOrder(goldenCarrier, laneIndexCheck, carrierCheck);
        RequireInOrder(goldenCarrier, "PrevalidateScalarStoreEffect(lane.MemoryAddress,lane.MemoryAccessSize");
        RequireInOrder(activeCarrier, "lane.MicroOp", "Core.StoreMicroOp", "SelectedStoreLayout", "layout",
            "PrevalidateScalarStoreEffect(lane.MemoryAddress,lane.MemoryAccessSize");

        string[] activeRetireFacts = [Tokens(Method(aRet, "PrevalidateScalarStoreEffect"))];
        string[] goldenRetireFacts = [Tokens(Method(gRet, "PrevalidateScalarStoreEffect"))];
        const string scalarPayloadCheck = "if(accessSizeisnot(1or2or4or8)||!IsScalarMemoryAccessInBounds(memoryAddress,accessSize))";
        RequireInOrder(activeRetireFacts[0], "byteaccessSize=NormalizeScalarMemoryAccessSize(memoryAccessSize);", scalarPayloadCheck);
        RequireInOrder(goldenRetireFacts[0], "byteaccessSize=NormalizeScalarMemoryAccessSize(memoryAccessSize);", scalarPayloadCheck);
        const string normalize = "memoryAccessSize==0?(byte)8:memoryAccessSize";
        RequireInOrder(Tokens(Method(aMem, "NormalizeScalarMemoryAccessSize")), normalize);
        RequireInOrder(Tokens(Method(gMem, "NormalizeScalarMemoryAccessSize")), normalize);
        const string bounds = "normalizedAccessSize<=mainMemoryLength&&address<=mainMemoryLength-normalizedAccessSize";
        RequireInOrder(Tokens(Method(aMem, "IsScalarMemoryAccessInBounds")), bounds);
        RequireInOrder(Tokens(Method(gMem, "IsScalarMemoryAccessInBounds")), bounds);

        MethodDeclarationSyntax sourceMethod = Method(aRet, "PrevalidateDeferredStoreEffect");
        var span = sourceMethod.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        string identity = Hash(Encoding.UTF8.GetBytes("LegacyDeferredStorePrevalidation|" +
            ActiveRetireSha256 + "|" + GoldenRetireSha256 + "|" + ActiveMemorySha256 + "|" + GoldenMemorySha256));
        var origin = new HwSourceOrigin("urn:circuitweave:dirty-source-capture:sha256:" + ActiveRetireSha256,
            GitBlob(activeRetire), RetirePath, "CircuitWeave.LegacyDeferredStorePrevalidation",
            "YAKSys_Hybrid_CPU.Processor.CPU_Core", "PrevalidateDeferredStoreEffect", identity,
            HwSourceRole.Primary, span.Line + 1, span.Character + 1);

        var bit = new HwBitVectorType(1);
        var u4 = new HwBitVectorType(4);
        var u64 = new HwBitVectorType(64);
        var ports = new List<HwPortContract>
        {
            new("deferred_store_effect", HwPortDirection.Input, bit),
            new("selected_layout_present", HwPortDirection.Input, bit),
            new("lane_index_u4", HwPortDirection.Input, u4),
            new("lane_occupied", HwPortDirection.Input, bit),
            new("lane_defers_store", HwPortDirection.Input, bit),
            new("lane_is_memory_op", HwPortDirection.Input, bit),
            new("lane_is_load", HwPortDirection.Input, bit),
            new("memory_access_size_u64", HwPortDirection.Input, u64),
            new("memory_address_u64", HwPortDirection.Input, u64),
            new("main_memory_length_u64", HwPortDirection.Input, u64),
            new("prevalidation_pass", HwPortDirection.Output, bit),
            new("prevalidation_reject", HwPortDirection.Output, bit),
            new("selected_layout_out_of_scope", HwPortDirection.Output, bit)
        };
        var nodes = new List<HwNode>();
        void C(string id, HwBitVectorType type, ulong value, int width) => nodes.Add(new HwConstantNode(id, type,
            Convert.ToString((long)value, 2).PadLeft(width, '0'), origin));
        void O(string id, string op, HwBitVectorType type, params string[] args) =>
            nodes.Add(new HwOperationNode(id, op, args, type, Origin: origin));
        void M(string id, string select, string whenTrue, string whenFalse, HwBitVectorType type) =>
            nodes.Add(new HwMuxNode(id, select, whenTrue, whenFalse, type, origin));

        C("zero_u4", u4, 0, 4);
        C("zero_u64", u64, 0, 64); C("one_u64", u64, 1, 64);
        C("two_u64", u64, 2, 64); C("four_u64", u64, 4, 64); C("eight_u64", u64, 8, 64);
        C("one_bit", bit, 1, 1);
        O("lane_index_u64", "zext", u64, "lane_index_u4");
        O("lane_index_valid", "ult64", bit, "lane_index_u64", "eight_u64");
        O("not_load", "xor", bit, "lane_is_load", "one_bit");
        O("live_store_carrier_0", "and", bit, "lane_occupied", "lane_defers_store");
        O("live_store_carrier_1", "and", bit, "lane_is_memory_op", "not_load");
        O("live_store_carrier", "and", bit, "live_store_carrier_0", "live_store_carrier_1");
        O("size_is_zero", "eq", bit, "memory_access_size_u64", "zero_u64");
        O("size_is_one", "eq", bit, "memory_access_size_u64", "one_u64");
        O("size_is_two", "eq", bit, "memory_access_size_u64", "two_u64");
        O("size_is_four", "eq", bit, "memory_access_size_u64", "four_u64");
        O("size_is_eight", "eq", bit, "memory_access_size_u64", "eight_u64");
        O("size_1_or_2", "or", bit, "size_is_one", "size_is_two");
        O("size_4_or_8", "or", bit, "size_is_four", "size_is_eight");
        O("size_nonzero_valid", "or", bit, "size_1_or_2", "size_4_or_8");
        O("size_valid", "or", bit, "size_is_zero", "size_nonzero_valid");
        M("normalized_size_u64", "size_is_zero", "eight_u64", "memory_access_size_u64", u64);
        O("memory_too_short", "ult64", bit, "main_memory_length_u64", "normalized_size_u64");
        O("memory_long_enough", "xor", bit, "memory_too_short", "one_bit");
        O("last_legal_start_u64", "sub", u64, "main_memory_length_u64", "normalized_size_u64");
        O("address_too_high", "ult64", bit, "last_legal_start_u64", "memory_address_u64");
        O("address_in_bounds", "xor", bit, "address_too_high", "one_bit");
        O("range_in_bounds", "and", bit, "memory_long_enough", "address_in_bounds");
        O("payload_valid", "and", bit, "size_valid", "range_in_bounds");
        O("selected_layout_absent", "xor", bit, "selected_layout_present", "one_bit");
        O("effect_applicable", "and", bit, "deferred_store_effect", "selected_layout_absent");
        O("carrier_and_range_valid", "and", bit, "live_store_carrier", "payload_valid");
        O("legacy_prevalidation_pass_value", "and", bit, "effect_applicable", "carrier_and_range_valid");
        O("prevalidation_pass_value", "and", bit, "lane_index_valid", "legacy_prevalidation_pass_value");
        O("prevalidation_fail_value", "xor", bit, "prevalidation_pass_value", "one_bit");
        O("prevalidation_reject_value", "and", bit, "effect_applicable", "prevalidation_fail_value");
        O("selected_layout_out_of_scope_value", "and", bit, "deferred_store_effect", "selected_layout_present");
        nodes.Add(new HwOutputDriveNode("drive_prevalidation_pass", "prevalidation_pass", "prevalidation_pass_value", origin));
        nodes.Add(new HwOutputDriveNode("drive_prevalidation_reject", "prevalidation_reject", "prevalidation_reject_value", origin));
        nodes.Add(new HwOutputDriveNode("drive_selected_layout_out_of_scope", "selected_layout_out_of_scope", "selected_layout_out_of_scope_value", origin));

        var module = new HwModule("legacy_deferred_store_prevalidation", "legacy_deferred_store_prevalidation",
            ports, nodes, [], new HwHardwareContract("processor.legacy-deferred-store-prevalidation.source-projection.v1",
                HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var design = new HwDesign("hwir/v0.1", module.Id, [module], toolchain, []);
        var validation = new FailClosedHardwareIrValidator().Validate(design);
        return validation.Validated ?? throw new InvalidDataException("Store prevalidation HWIR refused: " +
            string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string GitBlob(byte[] bytes)
    {
        byte[] prefix = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        byte[] data = new byte[prefix.Length + bytes.Length];
        Buffer.BlockCopy(prefix, 0, data, 0, prefix.Length);
        Buffer.BlockCopy(bytes, 0, data, prefix.Length, bytes.Length);
        return Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
    }
}
#endif
