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
/// Combinational projection of the active selected-layout Store revalidation guard.
/// Translation outcome, generations, ordered segment tuples, and per-segment range/domain
/// authorization are sampled inputs. Segment tuples are checked against the exact
/// CpuPhysicalRangeLayout constructor invariants before outcomes are enabled. Physical
/// publication and ticket terminalization are out of scope.
/// </summary>
public static class BridgeHwirSelectedStoreRevalidation
{
    public const string MemoryPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Stages/Memory/CPU_Core.PipelineExecution.Memory.cs";
    public const string LayoutPath = "HybridCPU_ISE/CloseToHSL/Core/Runtime/Memory/Translation/CpuPhysicalRangeLayout.cs";
    public const string RetirePath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.PipelineExecution.Retire.cs";
    public const string MemorySha256 = "4b62e108e296c6a6733daa0292079e2ca18581a816888f80562c63ec65d0b1db";
    public const string LayoutSha256 = "85b7962ab2b94b01a6562b2cb64c0d826acbe3b3caf0c918d955f78081a96298";
    public const string RetireSha256 = "cfdb51be0e465e485ce1c314dcc957e88cd537e3bd5c5fc374eb4a46eda8d184";

    public static HwValidatedDesign Compile(byte[] memorySource, byte[] layoutSource, byte[] retireSource, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(memorySource);
        ArgumentNullException.ThrowIfNull(layoutSource);
        ArgumentNullException.ThrowIfNull(retireSource);
        ArgumentNullException.ThrowIfNull(toolchain);
        if (Hash(memorySource) != MemorySha256 || Hash(layoutSource) != LayoutSha256 || Hash(retireSource) != RetireSha256 ||
            toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("Selected-store revalidation requires exact active source bytes and the supported combinational HWIR profile.");

        static SyntaxNode Parse(byte[] bytes, string label)
        {
            var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException(label + " source contains C# syntax errors.");
            return tree.GetRoot();
        }
        static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));
        static MethodDeclarationSyntax Method(SyntaxNode root, string name) => root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);
        static void InOrder(string text, params string[] facts)
        {
            int cursor = -1;
            foreach (string fact in facts)
            {
                int next = text.IndexOf(fact, StringComparison.Ordinal);
                if (next <= cursor) throw new InvalidDataException("Selected-store source contract changed at: " + fact);
                cursor = next;
            }
        }

        SyntaxNode memoryRoot = Parse(memorySource, "Selected-store Memory");
        SyntaxNode layoutRoot = Parse(layoutSource, "Physical-range layout");
        SyntaxNode retireRoot = Parse(retireSource, "Selected-store Retire");
        MethodDeclarationSyntax guard = Method(memoryRoot, "RequireCurrentSelectedStoreLayout");
        MethodDeclarationSyntax match = Method(layoutRoot, "MatchesTranslation");
        ConstructorDeclarationSyntax layoutCtor = layoutRoot.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "CpuPhysicalRangeLayout");
        MethodDeclarationSyntax storePrevalidation = Method(retireRoot, "PrevalidateScalarStoreEffect");
        string guardTokens = Tokens(guard);
        string matchTokens = Tokens(match);
        InOrder(guardTokens,
            "TranslateRange(store.SelectedStoreTranslationRequest)",
            "if(!current.Succeeded)",
            "CpuInstructionTranslationFaultException(current.Fault??",
            "if(!layout.MatchesTranslation(current.Layout!))",
            "thrownewCore.Memory.CpuStoreTranslationRemappedException()",
            "if(GetBoundMainMemory()isglobal::HybridCPU_ISE.CloseToHSL.Memory.Banks.MultiBankMemoryAreabanked)",
            "foreach(CpuPhysicalRangeSegmentsegmentinlayout.Segments)",
            "!banked.HasPhysicalRangeDomainAccess(segment.PhysicalAddress,segment.Length,request.Scope.DomainId)",
            "thrownewCore.PageFaultException(request.VirtualAddress,isWrite:true)");
        InOrder(matchTokens,
            "TranslationGeneration==current.TranslationGeneration",
            "Segments.AsSpan().SequenceEqual(current.Segments.AsSpan())");
        InOrder(Tokens(layoutCtor),
            "if(accessSize<=0)",
            "if(segment.Length==0||segment.LogicalOffset!=cursor||",
            "segment.Length>accessSize-cursor||",
            "segment.PhysicalAddress>ulong.MaxValue-((ulong)segment.Length-1)",
            "if(cursor!=accessSize)");
        InOrder(Tokens(storePrevalidation),
            "byteaccessSize=NormalizeScalarMemoryAccessSize(memoryAccessSize);",
            "if(accessSizeisnot(1or2or4or8)||",
            "thrownewInvalidOperationException(");

        byte[] originBytes = Encoding.UTF8.GetBytes("SelectedStoreRevalidation|" + MemorySha256 + "|" + LayoutSha256 + "|" + RetireSha256 + "|" + guardTokens + "|" + matchTokens + "|" + Tokens(storePrevalidation));
        string identity = Hash(originBytes);
        var span = guard.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        byte[] blobHeader = Encoding.ASCII.GetBytes($"blob {memorySource.Length}\0");
        byte[] blobInput = new byte[blobHeader.Length + memorySource.Length];
        Buffer.BlockCopy(blobHeader, 0, blobInput, 0, blobHeader.Length);
        Buffer.BlockCopy(memorySource, 0, blobInput, blobHeader.Length, memorySource.Length);
        string blob = Convert.ToHexString(SHA1.HashData(blobInput)).ToLowerInvariant();
        var origin = new HwSourceOrigin(
            "urn:circuitweave:dirty-source-capture:sha256:" + MemorySha256,
            blob, MemoryPath, "CircuitWeave.SelectedStoreRevalidation",
            "YAKSys_Hybrid_CPU.Processor.CPU_Core", "RequireCurrentSelectedStoreLayout",
            identity, HwSourceRole.Primary, span.Line + 1, span.Character + 1);

        var bit = new HwBitVectorType(1);
        var u4 = new HwBitVectorType(4);
        var u16 = new HwBitVectorType(16);
        var u64 = new HwBitVectorType(64);
        var ports = new List<HwPortContract>
        {
            new("translation_succeeded", HwPortDirection.Input, bit),
            new("captured_generation_u64", HwPortDirection.Input, u64),
            new("current_generation_u64", HwPortDirection.Input, u64),
            new("store_access_size_u64", HwPortDirection.Input, u64),
            new("captured_segment_count_u64", HwPortDirection.Input, u64),
            new("current_segment_count_u64", HwPortDirection.Input, u64),
            new("bank_domain_check_enabled", HwPortDirection.Input, bit),
            new("layout_facts_invalid", HwPortDirection.Output, bit),
            new("translation_fault", HwPortDirection.Output, bit),
            new("layout_remapped", HwPortDirection.Output, bit),
            new("domain_fault", HwPortDirection.Output, bit),
            new("revalidation_allowed", HwPortDirection.Output, bit)
        };
        void AddSegmentPorts(string side)
        {
            for (int i = 0; i < 8; i++)
            {
                ports.Add(new($"{side}_segment_{i}_logical_offset_u64", HwPortDirection.Input, u64));
                ports.Add(new($"{side}_segment_{i}_physical_address_u64", HwPortDirection.Input, u64));
                ports.Add(new($"{side}_segment_{i}_length_u16", HwPortDirection.Input, u16));
            }
        }
        AddSegmentPorts("captured");
        AddSegmentPorts("current");
        for (int i = 0; i < 8; i++) ports.Add(new($"captured_segment_{i}_range_domain_authorized", HwPortDirection.Input, bit));

        var nodes = new List<HwNode>
        {
            new HwConstantNode("zero_u4", u4, "0000", origin),
            new HwConstantNode("zero_u64", u64, new string('0', 64), origin),
            new HwConstantNode("nine_u64", u64, Convert.ToString(9L, 2).PadLeft(64, '0'), origin),
            new HwConstantNode("max_u64", u64, new string('1', 64), origin),
            new HwConstantNode("zero_u16", u16, new string('0', 16), origin),
            new HwConstantNode("one_u16", u16, Convert.ToString(1, 2).PadLeft(16, '0'), origin),
            new HwConstantNode("one_bit", bit, "1", origin)
        };
        foreach (ulong size in new ulong[] { 1, 2, 4, 8 })
            nodes.Add(new HwConstantNode($"access_size_{size}_u64", u64, Convert.ToString((long)size, 2).PadLeft(64, '0'), origin));
        for (int i = 1; i < 8; i++)
            nodes.Add(new HwConstantNode($"segment_index_{i}_u64", u64, Convert.ToString(i, 2).PadLeft(64, '0'), origin));
        nodes.Add(new HwOperationNode("translation_failed_value", "xor", ["translation_succeeded", "one_bit"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("generation_equal", "eq", ["captured_generation_u64", "current_generation_u64"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("segment_count_equal", "eq", ["captured_segment_count_u64", "current_segment_count_u64"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("captured_count_in_range", "ult64", ["captured_segment_count_u64", "nine_u64"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("current_count_in_range", "ult64", ["current_segment_count_u64", "nine_u64"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("captured_count_nonzero", "eq", ["captured_segment_count_u64", "zero_u64"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("current_count_nonzero", "eq", ["current_segment_count_u64", "zero_u64"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("captured_count_present", "xor", ["captured_count_nonzero", "one_bit"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("current_count_present", "xor", ["current_count_nonzero", "one_bit"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("both_counts_in_range", "and", ["captured_count_in_range", "current_count_in_range"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("both_counts_present", "and", ["captured_count_present", "current_count_present"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("counts_in_range", "and", ["both_counts_in_range", "both_counts_present"], bit, Origin: origin));
        foreach (ulong size in new ulong[] { 1, 2, 4, 8 })
            nodes.Add(new HwOperationNode($"access_size_is_{size}", "eq", ["store_access_size_u64", $"access_size_{size}_u64"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("access_size_is_1_or_2", "or", ["access_size_is_1", "access_size_is_2"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("access_size_is_4_or_8", "or", ["access_size_is_4", "access_size_is_8"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("access_size_supported", "or", ["access_size_is_1_or_2", "access_size_is_4_or_8"], bit, Origin: origin));

        void AddLayoutShape(string side, int slot, string active, string offset, string physical,
            string length, ref string cursor, List<string> shapeChecks)
        {
            string prefix = $"{side}_slot_{slot}";
            nodes.Add(new HwOperationNode(prefix + "_length_is_zero", "eq", [length, "zero_u16"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "_length_nonzero", "xor", [prefix + "_length_is_zero", "one_bit"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "_offset_is_contiguous", "eq", [offset, cursor], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "_length_minus_one_u16", "sub", [length, "one_u16"], u16, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "_length_minus_one_u64", "zext", [prefix + "_length_minus_one_u16"], u64, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "_max_address_without_overflow", "sub", ["max_u64", prefix + "_length_minus_one_u64"], u64, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "_physical_overflow", "ult64", [prefix + "_max_address_without_overflow", physical], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "_physical_range_valid", "xor", [prefix + "_physical_overflow", "one_bit"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "_nonzero_and_contiguous", "and", [prefix + "_length_nonzero", prefix + "_offset_is_contiguous"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "_segment_shape_valid", "and", [prefix + "_nonzero_and_contiguous", prefix + "_physical_range_valid"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "_segment_or_inactive_valid", "or", [$"{side}_slot_{slot}_inactive", prefix + "_segment_shape_valid"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "_length_u64", "zext", [length], u64, Origin: origin));
            nodes.Add(new HwOperationNode(prefix + "_cursor_if_active", "add", [cursor, prefix + "_length_u64"], u64, Origin: origin));
            string nextCursor = prefix + "_cursor_next";
            nodes.Add(new HwMuxNode(nextCursor, active, prefix + "_cursor_if_active", cursor, u64, origin));
            cursor = nextCursor;
            shapeChecks.Add(prefix + "_segment_or_inactive_valid");
        }

        var perSlotAuthorized = new List<string>(8);
        var capturedShapeChecks = new List<string>(9);
        var currentShapeChecks = new List<string>(9);
        string capturedCursor = "zero_u64";
        string currentCursor = "zero_u64";
        for (int i = 0; i < 8; i++)
        {
            string ix = i == 0 ? "zero_u64" : $"segment_index_{i}_u64";
            string active = $"captured_slot_{i}_active";
            string activeCurrent = $"current_slot_{i}_active";
            string tupleEqual = $"segment_{i}_tuple_equal";
            string authorized = $"captured_slot_{i}_authorized_or_inactive";
            nodes.Add(new HwOperationNode(active, "ult64", [ix, "captured_segment_count_u64"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(activeCurrent, "ult64", [ix, "current_segment_count_u64"], bit, Origin: origin));
            string co = $"captured_segment_{i}_logical_offset_u64";
            string cp = $"captured_segment_{i}_physical_address_u64";
            string cl = $"captured_segment_{i}_length_u16";
            string no = $"current_segment_{i}_logical_offset_u64";
            string np = $"current_segment_{i}_physical_address_u64";
            string nl = $"current_segment_{i}_length_u16";
            nodes.Add(new HwOperationNode($"segment_{i}_offset_equal", "eq", [co, no], bit, Origin: origin));
            nodes.Add(new HwOperationNode($"segment_{i}_address_equal", "eq", [cp, np], bit, Origin: origin));
            nodes.Add(new HwOperationNode($"segment_{i}_length_equal", "eq", [cl, nl], bit, Origin: origin));
            nodes.Add(new HwOperationNode($"segment_{i}_offset_and_address_equal", "and", [$"segment_{i}_offset_equal", $"segment_{i}_address_equal"], bit, Origin: origin));
            nodes.Add(new HwOperationNode(tupleEqual, "and", [$"segment_{i}_offset_and_address_equal", $"segment_{i}_length_equal"], bit, Origin: origin));
            nodes.Add(new HwOperationNode($"segment_{i}_active_equal", "eq", [active, activeCurrent], bit, Origin: origin));
            nodes.Add(new HwOperationNode($"captured_slot_{i}_inactive", "xor", [active, "one_bit"], bit, Origin: origin));
            nodes.Add(new HwOperationNode($"current_slot_{i}_inactive", "xor", [activeCurrent, "one_bit"], bit, Origin: origin));
            nodes.Add(new HwOperationNode($"inactive_slot_{i}_match", "and", [$"captured_slot_{i}_inactive", $"current_slot_{i}_inactive"], bit, Origin: origin));
            nodes.Add(new HwOperationNode($"captured_slot_{i}_authorized_or_inactive", "or", [$"captured_slot_{i}_inactive", $"captured_segment_{i}_range_domain_authorized"], bit, Origin: origin));
            perSlotAuthorized.Add(authorized);

            AddLayoutShape("captured", i, active, co, cp, cl, ref capturedCursor, capturedShapeChecks);
            AddLayoutShape("current", i, activeCurrent, no, np, nl, ref currentCursor, currentShapeChecks);
        }
        for (int i = 0; i < 8; i++)
        {
            nodes.Add(new HwOperationNode($"captured_slot_{i}_tuple_or_inactive", "or", [$"segment_{i}_tuple_equal", $"inactive_slot_{i}_match"], bit, Origin: origin));
            nodes.Add(new HwOperationNode($"captured_slot_{i}_match_exact", "and", [$"segment_{i}_active_equal", $"captured_slot_{i}_tuple_or_inactive"], bit, Origin: origin));
        }
        string Reduce(string name, IReadOnlyList<string> terms)
        {
            string prior = terms[0];
            for (int i = 1; i < terms.Count; i++)
            {
                string next = name + "_and_" + i;
                nodes.Add(new HwOperationNode(next, "and", [prior, terms[i]], bit, Origin: origin));
                prior = next;
            }
            return prior;
        }
        string allSlotsMatch = Reduce("all_segment_slots_match", Enumerable.Range(0, 8).Select(i => $"captured_slot_{i}_match_exact").ToArray());
        string allAuthorized = Reduce("all_captured_segments_authorized", perSlotAuthorized);
        nodes.Add(new HwOperationNode("captured_complete_coverage", "eq", [capturedCursor, "store_access_size_u64"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("current_complete_coverage", "eq", [currentCursor, "store_access_size_u64"], bit, Origin: origin));
        capturedShapeChecks.Add("captured_complete_coverage");
        currentShapeChecks.Add("current_complete_coverage");
        string capturedShape = Reduce("captured_shape", capturedShapeChecks);
        string currentShape = Reduce("current_shape", currentShapeChecks);
        nodes.Add(new HwOperationNode("both_layout_shapes_valid", "and", [capturedShape, currentShape], bit, Origin: origin));
        nodes.Add(new HwOperationNode("layouts_and_access_size_valid", "and", ["both_layout_shapes_valid", "access_size_supported"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("all_layout_facts_valid", "and", ["layouts_and_access_size_valid", "counts_in_range"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("layout_shape_invalid_value", "xor", ["all_layout_facts_valid", "one_bit"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("layout_facts_invalid_value", "and", ["translation_succeeded", "layout_shape_invalid_value"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("layout_match", "and", ["generation_equal", "segment_count_equal"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("layout_match_and_segments", "and", ["layout_match", allSlotsMatch], bit, Origin: origin));
        nodes.Add(new HwOperationNode("layout_match_with_segments", "and", ["layout_match_and_segments", "all_layout_facts_valid"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("layout_mismatch", "xor", ["layout_match_with_segments", "one_bit"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("valid_layout_remap", "and", ["layout_mismatch", "all_layout_facts_valid"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("layout_remapped_value", "and", ["translation_succeeded", "valid_layout_remap"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("layout_and_translation_valid", "and", ["translation_succeeded", "layout_match_with_segments"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("domain_check_failed", "xor", [allAuthorized, "one_bit"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("domain_enabled_and_failed", "and", ["bank_domain_check_enabled", "domain_check_failed"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("domain_fault_value", "and", ["layout_and_translation_valid", "domain_enabled_and_failed"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("domain_check_disabled", "xor", ["bank_domain_check_enabled", "one_bit"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("domain_check_passed", "or", ["domain_check_disabled", allAuthorized], bit, Origin: origin));
        nodes.Add(new HwOperationNode("revalidation_allowed_value", "and", ["layout_and_translation_valid", "domain_check_passed"], bit, Origin: origin));
        nodes.Add(new HwOutputDriveNode("drive_layout_facts_invalid", "layout_facts_invalid", "layout_facts_invalid_value", origin));
        nodes.Add(new HwOutputDriveNode("drive_translation_fault", "translation_fault", "translation_failed_value", origin));
        nodes.Add(new HwOutputDriveNode("drive_layout_remapped", "layout_remapped", "layout_remapped_value", origin));
        nodes.Add(new HwOutputDriveNode("drive_domain_fault", "domain_fault", "domain_fault_value", origin));
        nodes.Add(new HwOutputDriveNode("drive_revalidation_allowed", "revalidation_allowed", "revalidation_allowed_value", origin));

        var module = new HwModule("selected_store_revalidation", "selected_store_revalidation", ports, nodes, [],
            new HwHardwareContract("selected-store-revalidation.active.v1", HwModuleKind.Combinational, ports,
                new HwTimingContract(0, 1)), origin);
        var design = new HwDesign("hwir/v0.1", module.Id, [module], toolchain, []);
        var validation = new FailClosedHardwareIrValidator().Validate(design);
        return validation.Validated ?? throw new InvalidDataException(
            "Selected-store revalidation HWIR refused: " + string.Join("; ", validation.Diagnostics.Select(d => d.Message)));
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
