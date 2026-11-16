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
/// Source-bounded combinational allocation projection of BundleIssuePacket.Create.
/// It projects only lane-to-source-slot allocation, assigned masks and unmapped
/// selected slots. It does not project packet payload, execution handoff, or time.
/// </summary>
public static class BridgeHwirIssuePacketCreateAllocation
{
    private const string PacketPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Core/RuntimeClusterAdmissionPreparation.BundleIssuePacket.cs";
    private const string IntentPath = "HybridCPU_ISE/CloseToHSL/Core/Legality/ClusterIssueIntent.cs";
    private const string PreparationPath = "HybridCPU_ISE/CloseToHSL/Core/Decoder/ClusterIssuePreparation.cs";
    private const string PlacementPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/SlotLegality/SlotClassDefinitions.cs";
    private const string SlotPlacementPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/SlotPlacementMetadata.cs";
    private const string BundlePath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Metadata/BundleMetadata.cs";

    /// <param name="packet">Exact BundleIssuePacket partial source bytes.</param>
    /// <param name="intent">Exact ClusterIssueIntent and builder source bytes.</param>
    /// <param name="preparation">Exact active ClusterIssuePreparation source bytes.</param>
    /// <param name="classes">Exact SlotClass and SlotClassLaneMap source bytes.</param>
    /// <param name="slotPlacement">Exact SlotPlacementMetadata source bytes.</param>
    /// <param name="bundle">Exact BundleMetadata source bytes.</param>
    public static HwValidatedDesign Compile(byte[] packet, byte[] intent, byte[] preparation,
        byte[] classes, byte[] slotPlacement, byte[] bundle, byte[] decodeSource, string[] expectedSha256,
        string commit, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(expectedSha256);
        ArgumentNullException.ThrowIfNull(toolchain);
        byte[][] sources = [packet, intent, preparation, classes, slotPlacement, bundle, decodeSource];
        static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (expectedSha256.Length != sources.Length || sources.Any(s => s is null || s.Length > 2_000_000) ||
            sources.Where((s, i) => Hash(s) != expectedSha256[i]).Any() ||
            commit.Length != 40 || commit.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Issue-packet Create source identity differs.");

        static SyntaxNode Parse(byte[] bytes)
        {
            var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Issue-packet Create source contains syntax errors.");
            return tree.GetRoot();
        }
        static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));
        SyntaxNode packetRoot = Parse(packet), intentRoot = Parse(intent), prepRoot = Parse(preparation);
        SyntaxNode classesRoot = Parse(classes), placementRoot = Parse(slotPlacement), bundleRoot = Parse(bundle);
        SyntaxNode decodeRoot = Parse(decodeSource);
        StructDeclarationSyntax packetType = packetRoot.DescendantNodes().OfType<StructDeclarationSyntax>()
            .Single(t => t.Identifier.ValueText == "BundleIssuePacket");
        MethodDeclarationSyntax create = packetType.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "Create" && m.ParameterList.Parameters.Count == 4);
        MethodDeclarationSyntax[] packetMethods =
        [
            packetType.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "TryAssignToPacket" && m.ParameterList.Parameters[0].Type?.ToString() == "ScalarClusterIssueEntry"),
            packetType.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "TryAssignToPacket" && m.ParameterList.Parameters[0].Type?.ToString() == "DecodedBundleSlotDescriptor"),
            packetType.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "TryResolvePacketLane" && m.ParameterList.Parameters[0].Type?.ToString() == "ScalarClusterIssueEntry"),
            packetType.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "TryResolvePacketLane" && m.ParameterList.Parameters[0].Type?.ToString() == "DecodedBundleSlotDescriptor"),
            packetType.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "SetLane")
        ];

        ClassDeclarationSyntax[] facts = intentRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(t => t.Identifier.ValueText == "ClusterIssueIntentBuilder").ToArray();
        ClassDeclarationSyntax builder = facts.Single();
        FieldDeclarationSyntax widthField = builder.Members.OfType<FieldDeclarationSyntax>().Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "ScalarIssueWidthCeiling"));
        VariableDeclaratorSyntax widthValue = widthField.Declaration.Variables.Single(v => v.Identifier.ValueText == "ScalarIssueWidthCeiling");
        if (widthValue.Initializer?.Value is not LiteralExpressionSyntax widthLiteral || widthLiteral.Token.ValueText != "4")
            throw new InvalidDataException("Active scalar issue width is not guarded at four.");
        MethodDeclarationSyntax buildIntent = builder.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "Build");
        string builderTokens = Tokens(buildIntent);
        if (!builderTokens.Contains("slots.Count<BundleMetadata.BundleSlotCount", StringComparison.Ordinal) ||
            !builderTokens.Contains("BundleMetadata.BundleSlotCount", StringComparison.Ordinal) ||
            !builderTokens.Contains("ScalarIssueWidthCeiling", StringComparison.Ordinal) ||
            !builderTokens.Contains("selectedVirtualThreadMask", StringComparison.Ordinal) ||
            builderTokens.Split("for(intslotIndex=0;slotIndex<BundleMetadata.BundleSlotCount&&count<entries.Length;slotIndex++)", StringSplitOptions.None).Length != 3)
            throw new InvalidDataException("ClusterIssueIntentBuilder active four-entry selection contract changed.");
        if (!classesRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Any(t => t.Identifier.ValueText == "SlotClassLaneMap") ||
            !placementRoot.DescendantNodes().OfType<StructDeclarationSyntax>().Any(t => t.Identifier.ValueText == "SlotPlacementMetadata"))
            throw new InvalidDataException("Placement or class-lane source types are missing.");
        var enumType = classesRoot.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(t => t.Identifier.ValueText == "SlotClass");
        var enumFacts = enumType.Members.ToDictionary(m => m.Identifier.ValueText,
            m => byte.Parse(m.EqualsValue!.Value.ToString(), System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var expectedClasses = new Dictionary<string, byte>(StringComparer.Ordinal)
        {
            ["AluClass"] = 0, ["LsuClass"] = 1, ["DmaStreamClass"] = 2,
            ["BranchControl"] = 3, ["SystemSingleton"] = 4,
            ["MatrixTileStreamClass"] = 5, ["Unclassified"] = 7
        };
        if (enumType.BaseList?.Types.Single().ToString() != "byte" || enumFacts.Count != expectedClasses.Count ||
            expectedClasses.Any(kv => !enumFacts.TryGetValue(kv.Key, out byte v) || v != kv.Value))
            throw new InvalidDataException("The supported SlotClass enum domain changed.");
        var decodeType = decodeRoot.DescendantNodes().OfType<TypeDeclarationSyntax>().Single(t => t.Identifier.ValueText == "DecodedBundleSlotDescriptor");
        MethodDeclarationSyntax nonEmptyGuard = decodeType.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "GetRuntimeExecutionIsEmptyOrNop");
        if (!Tokens(nonEmptyGuard).Contains("MicroOp==null)returntrue", StringComparison.Ordinal) ||
            !Tokens(nonEmptyGuard).Contains("returnMicroOpisNopMicroOp", StringComparison.Ordinal))
            throw new InvalidDataException("Scalar candidate null/empty rejection guard changed.");
        MethodDeclarationSyntax admissionCandidate = decodeRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "IsAuthoritativeScalarAdmissionCandidate");
        string candidateTokens = Tokens(admissionCandidate);
        if (!candidateTokens.Contains("GetRuntimeExecutionIsEmptyOrNop", StringComparison.Ordinal) ||
            !candidateTokens.Contains("placement.RequiredSlotClass==SlotClass.AluClass", StringComparison.Ordinal) ||
            !candidateTokens.Contains("placement.PinningKind==SlotPinningKind.ClassFlexible", StringComparison.Ordinal))
            throw new InvalidDataException("Active scalar admission must reject null/empty slots and require ALU class-flexible placement.");
        MethodDeclarationSyntax candidateMaskBuilder = decodeRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "ResolveAuthoritativeScalarCandidateMask");
        if (!Tokens(candidateMaskBuilder).Contains("IsAuthoritativeScalarAdmissionCandidate", StringComparison.Ordinal))
            throw new InvalidDataException("Authoritative scalar candidate mask no longer applies the null/class guard.");
        MethodDeclarationSyntax admissionPrepBuilder = decodeRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "BuildAdmissionPrep");
        if (!Tokens(admissionPrepBuilder).Contains("ResolveAuthoritativeScalarCandidateMask", StringComparison.Ordinal))
            throw new InvalidDataException("Active admission preparation no longer sanitizes the scalar candidate mask.");
        var bundleType = bundleRoot.DescendantNodes().OfType<TypeDeclarationSyntax>().Single(t => t.Identifier.ValueText == "BundleMetadata");
        var slotCount = bundleType.Members.OfType<FieldDeclarationSyntax>().SelectMany(f => f.Declaration.Variables)
            .Single(v => v.Identifier.ValueText == "BundleSlotCount");
        if (slotCount.Initializer?.Value.ToString() != "8") throw new InvalidDataException("BundleMetadata slot count is not eight.");
        MethodDeclarationSyntax prepCreate = prepRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(t => t.Identifier.ValueText == "ClusterIssuePreparation").Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "Create" && m.ParameterList.Parameters.Count == 4);
        if (!Tokens(prepCreate).Contains("ClusterIssueIntentBuilder.Build", StringComparison.Ordinal) ||
            !Tokens(prepCreate).Contains("ToScalarClusterIssueGroup", StringComparison.Ordinal))
            throw new InvalidDataException("ClusterIssuePreparation no longer uses the guarded active issue-intent builder.");
        // The Create method itself is the execution contract. This token guard fixes
        // its exact three-pass order; helper guards fix immediate occupancy commits.
        string createTokens = Tokens(create);
        string[] orderTokens = ["PostStageBIssuedAttempt", "for(intentryIndex=0;entryIndex<entries.Length;entryIndex++)", "for(byteslotIndex=0;slotIndex<8;slotIndex++)"];
        int issuedPass = createTokens.IndexOf("PostStageBIssuedAttempt", StringComparison.Ordinal);
        int scalarPass = createTokens.IndexOf("for(intentryIndex=0;entryIndex<entries.Length;entryIndex++)", StringComparison.Ordinal);
        int auxiliaryPass = createTokens.IndexOf("for(byteslotIndex=0;slotIndex<8;slotIndex++)", StringComparison.Ordinal);
        if (orderTokens.Any(t => !createTokens.Contains(t, StringComparison.Ordinal)) || !(issuedPass < scalarPass && scalarPass < auxiliaryPass) ||
            !createTokens.Contains("occupiedPhysicalLaneMask=0", StringComparison.Ordinal) ||
            !createTokens.Contains("unmappedSelectedSlotMask|=", StringComparison.Ordinal))
            throw new InvalidDataException("BundleIssuePacket.Create phase order or masks changed.");
        if (packetMethods.Any(m => m.Body is null)) throw new InvalidDataException("Packet allocation helper became expression-only or abstract.");
        if (packetMethods.Where(m => m.Identifier.ValueText == "TryAssignToPacket").Any(m =>
                !Tokens(m).Contains("occupiedPhysicalLaneMask|=", StringComparison.Ordinal)) ||
            packetMethods.Single(m => m.Identifier.ValueText == "SetLane").Body!.Statements.Count != 1)
            throw new InvalidDataException("Packet lane assignment must update occupancy and dispatch lanes as guarded.");

        // Keep the guarded source roles in the HWIR mapping. The dynamic allocation
        // module is built below; resolver module logic remains source-bounded by its
        // own Bridge projection and is instantiated once per ordered assignment.
        var createSpan = create.Identifier.GetLocation().GetLineSpan();
        string identity = Hash(Encoding.UTF8.GetBytes("issue-packet-create-allocation/v1|" + string.Join("|", expectedSha256) + "|" + createTokens));
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", commit, PacketPath,
            "CircuitWeave.Captured.IssuePacketCreateProjection", "YAKSys_Hybrid_CPU.Core.BundleIssuePacket",
            "Create(ClusterIssuePreparation, IReadOnlyList<DecodedBundleSlotDescriptor>, RuntimeClusterAdmissionCandidateView, RuntimeClusterAdmissionDecisionDraft)",
            identity, HwSourceRole.Primary, createSpan.StartLinePosition.Line + 1, createSpan.StartLinePosition.Character + 1);
        return BuildProjection(origin, toolchain, expectedSha256, packet, classes, bundle);
    }

    private static HwValidatedDesign BuildProjection(HwSourceOrigin origin, HwToolchainIdentity toolchain,
        string[] sourceHashes, byte[] packetSource, byte[] classSource, byte[] bundleSource)
    {
        HwValidatedDesign resolver = BridgeHwirIssuePacketLaneResolution.Compile(
            packetSource, classSource, bundleSource, [sourceHashes[0], sourceHashes[3], sourceHashes[5]],
            origin.Commit, toolchain);
        const string resolverId = "issue_packet_scalar_lane_resolution_projection";
        HwModule laneModule = resolver.Design.Modules.Single(m => m.Id == resolverId);
        var bit = new HwBitVectorType(1); var u3 = new HwBitVectorType(3); var u8 = new HwBitVectorType(8); var u32 = new HwBitVectorType(32);
        var u4 = new HwBitVectorType(4);
        var ports = new List<HwPortContract>
        {
            new("scalar_count", HwPortDirection.Input, u3), new("scalar_issue_mask", HwPortDirection.Input, u8),
            new("auxiliary_reservation_mask", HwPortDirection.Input, u8), new("auxiliary_slot_count", HwPortDirection.Input, u4),
            new("assigned_lane_mask", HwPortDirection.Output, u8), new("assigned_slot_mask", HwPortDirection.Output, u8),
            new("scalar_lane_mask", HwPortDirection.Output, u8), new("class_inputs_defined", HwPortDirection.Output, bit),
            new("source_adapter_valid", HwPortDirection.Output, bit), new("unmapped_selected_slot_mask", HwPortDirection.Output, u8),
            new("selected_slot_mask", HwPortDirection.Output, u8)
        };
        for (int i = 0; i < 8; i++) ports.Add(new($"lane{i}_slot", HwPortDirection.Output, u8));
        for (int i = 0; i < 4; i++)
        {
            ports.Add(new($"scalar{i}_slot", HwPortDirection.Input, u8));
            ports.Add(new($"scalar{i}_microop_present", HwPortDirection.Input, bit));
            ports.Add(new($"scalar{i}_issued_present", HwPortDirection.Input, bit));
            ports.Add(new($"scalar{i}_issued_current", HwPortDirection.Input, bit));
            ports.Add(new($"scalar{i}_issued_lane_signed_bits", HwPortDirection.Input, u32));
            ports.Add(new($"scalar{i}_hard_pinned", HwPortDirection.Input, bit));
            ports.Add(new($"scalar{i}_pinned_lane_id", HwPortDirection.Input, u8));
            ports.Add(new($"scalar{i}_slot_class", HwPortDirection.Input, u8));
        }
        for (int i = 0; i < 8; i++)
        {
            ports.Add(new($"aux{i}_valid", HwPortDirection.Input, bit));
            ports.Add(new($"aux{i}_hard_pinned", HwPortDirection.Input, bit));
            ports.Add(new($"aux{i}_pinned_lane_id", HwPortDirection.Input, u8));
            ports.Add(new($"aux{i}_slot_class", HwPortDirection.Input, u8));
        }
        var nodes = new List<HwNode>(); var instances = new List<HwInstanceNode>();
        var classDomainSignals = new List<string>();
        void Wire(string id, HwBitVectorType type) => nodes.Add(new HwWireNode(id, type, origin));
        void Const(string id, HwBitVectorType type, uint value) => nodes.Add(new HwConstantNode(id, type,
            Convert.ToString(value, 2).PadLeft(type.Width, '0'), origin));
        string Op(string id, string opcode, string[] args, HwBitVectorType type)
        { nodes.Add(new HwOperationNode(id, opcode, args, type, Origin: origin)); return id; }
        string Eq(string id, string a, string b) => Op(id, "eq", [a, b], bit);
        string And(string id, string a, string b) => Op(id, "and", [a, b], bit);
        string Or(string id, string a, string b) => Op(id, "or", [a, b], bit);
        string Not(string id, string a) => Op(id, "xor", [a, "one"], bit);
        string Mux(string id, string c, string a, string b, HwBitVectorType type)
        { nodes.Add(new HwMuxNode(id, c, a, b, type, origin)); return id; }
        Const("zero", bit, 0); Const("one", bit, 1); Const("zero8", u8, 0); Const("zero32", u32, 0);
        Const("slot_count_0", u3, 0); Const("slot_count_1", u3, 1); Const("slot_count_2", u3, 2); Const("slot_count_3", u3, 3); Const("slot_count_4", u3, 4);
        for (int i = 0; i < 8; i++) { Const($"slot_{i}", u8, (uint)i); Const($"slotbit_{i}", u8, 1u << i); Const($"lanebit_{i}", u8, 1u << i); Const($"aux_count_{i}", u4, (uint)i); }
        Const("aux_count_8", u4, 8);
        string occupancy = "zero8", assignedLane = "zero8", scalarLane = "zero8", assignedSlot = "zero8", unmapped = "zero8";
        string[] laneSlot = Enumerable.Range(0, 8).Select(i => $"empty_slot_{i}").ToArray();
        for (int i = 0; i < 8; i++) { Const($"empty_slot_{i}", u8, 255); }
        string selectedMask = Op("selected_slot_mask_value", "or", ["scalar_issue_mask", "auxiliary_reservation_mask"], u8);
        string AddStage(string label, string active, string slot, string issuedPresent, string issuedCurrent,
            string issuedLane, string hardPin, string pinLane, string slotClass, bool scalar)
        {
            string ok = "resolve_ok_" + label, lane = "resolve_lane_" + label;
            Wire(ok, bit); Wire(lane, u8);
            string inst = "resolver_" + label;
            var map = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["issued_present"] = issuedPresent, ["issued_current"] = issuedCurrent,
                ["issued_lane_signed_bits"] = issuedLane, ["hard_pinned"] = hardPin,
                ["pinned_lane_id"] = pinLane, ["slot_class"] = slotClass, ["occupied_lane_mask"] = occupancy,
                ["accepted"] = ok, ["physical_lane_index"] = lane,
                ["class_map_defined"] = "unused_defined_" + label, ["class_lane_mask"] = "unused_map_" + label
            };
            Wire("unused_defined_" + label, bit); Wire("unused_map_" + label, u8);
            classDomainSignals.Add("unused_defined_" + label);
            instances.Add(new HwInstanceNode(inst, resolverId, map, new Dictionary<string, string>(), Origin: origin));
            string success = And("success_" + label, active, ok);
            string fail = And("failed_" + label, active, Not("not_ok_" + label, ok));
            string bitValue = "zero8";
            for (int s = 0; s < 8; s++)
            {
                string isSlot = Eq($"slot_eq_{label}_{s}", slot, $"slot_{s}");
                bitValue = Mux($"slotbit_mux_{label}_{s}", isSlot, $"slotbit_{s}", bitValue, u8);
            }
            string slotBit = Op("slotbit_value_" + label, "or", [bitValue, "zero8"], u8);
            string failureSlots = Mux("failure_slot_mask_" + label, fail, slotBit, "zero8", u8);
            unmapped = Op("unmapped_after_" + label, "or", [unmapped, failureSlots], u8);
            string placedSlot = Mux("placed_slot_mask_" + label, success, slotBit, "zero8", u8);
            assignedSlot = Op("assigned_slot_after_" + label, "or", [assignedSlot, placedSlot], u8);
            string laneAdd = "zero8";
            for (int n = 0; n < 8; n++)
            {
                string laneMatch = Eq($"lane_match_{label}_{n}", lane, $"slot_{n}");
                string write = And($"lane_write_{label}_{n}", success, laneMatch);
                laneAdd = Mux($"lane_add_{label}_{n}", write, $"lanebit_{n}", laneAdd, u8);
                laneSlot[n] = Mux($"lane_slot_update_{label}_{n}", write, slot, laneSlot[n], u8);
            }
            assignedLane = Op("assigned_lanes_after_" + label, "or", [assignedLane, laneAdd], u8);
            if (scalar) scalarLane = Op("scalar_lanes_after_" + label, "or", [scalarLane, laneAdd], u8);
            occupancy = Op("occupancy_after_" + label, "or", [occupancy, laneAdd], u8);
            return success;
        }
        string[] scalarSlots = Enumerable.Range(0, 4).Select(i => $"scalar{i}_slot").ToArray();
        string[] scalarActive = new string[4], scalarCountActive = new string[4];
        for (int i = 0; i < 4; i++)
        {
            string[] matches = new string[8];
            for (int s = 0; s < 8; s++)
            {
                string eq = Eq($"scalar{i}_slot_eq_{s}", scalarSlots[i], $"slot_{s}");
                string maskBit = Op($"scalar{i}_mask_bit_{s}", "and", ["scalar_issue_mask", $"slotbit_{s}"], u8);
                string nonzero = Not($"scalar{i}_mask_nonzero_{s}", Eq($"scalar{i}_mask_zero_{s}", maskBit, "zero8"));
                matches[s] = And($"scalar{i}_selected_match_{s}", eq, nonzero);
            }
            string select = matches.Aggregate((a, b) => Or($"scalar{i}_selected_or_{Array.IndexOf(matches, b)}", a, b));
            string activeCount = "zero";
            for (int c = i + 1; c <= 4; c++) activeCount = Or($"count_{i}_{c}_or", activeCount, Eq($"count_{i}_{c}", "scalar_count", $"slot_count_{c}"));
            scalarCountActive[i] = activeCount;
            scalarActive[i] = And($"scalar{i}_count_active", select, activeCount);
        }
        for (int i = 0; i < 4; i++)
        {
            string active = And($"scalar{i}_issued_active", scalarActive[i], $"scalar{i}_issued_present");
            AddStage($"issued_{i}", active, scalarSlots[i], $"scalar{i}_issued_present", $"scalar{i}_issued_current",
                $"scalar{i}_issued_lane_signed_bits", $"scalar{i}_hard_pinned", $"scalar{i}_pinned_lane_id", $"scalar{i}_slot_class", true);
        }
        for (int i = 0; i < 4; i++)
        {
            string noIssued = Not($"scalar{i}_no_issued", $"scalar{i}_issued_present");
            string active = And($"scalar{i}_unissued_active", scalarActive[i], noIssued);
            AddStage($"unissued_{i}", active, scalarSlots[i], "zero", "zero", "zero32",
                $"scalar{i}_hard_pinned", $"scalar{i}_pinned_lane_id", $"scalar{i}_slot_class", true);
        }
        for (int i = 0; i < 8; i++)
        {
            string masked = Op($"aux{i}_mask_bit", "and", ["auxiliary_reservation_mask", $"slotbit_{i}"], u8);
            string selected = Not($"aux{i}_selected", Eq($"aux{i}_mask_zero", masked, "zero8"));
            string withinCount = "zero";
            for (int count = i + 1; count <= 8; count++)
            {
                string countConstant = count == 8 ? "aux_count_8" : $"aux_count_{count}";
                withinCount = Or($"aux{i}_within_count_{count}", withinCount,
                    Eq($"aux{i}_count_eq_{count}", "auxiliary_slot_count", countConstant));
            }
            string active = And($"aux{i}_selected_in_range", selected, withinCount);
            active = And($"aux{i}_active", active, $"aux{i}_valid");
            string notValid = Not($"aux{i}_not_valid", $"aux{i}_valid");
            string notInRange = Not($"aux{i}_not_in_range", withinCount);
            string invalidState = Or($"aux{i}_invalid_state", notInRange, notValid);
            string invalid = And($"aux{i}_invalid", selected, invalidState);
            unmapped = Op($"unmapped_invalid_aux_{i}", "or", [unmapped, Mux($"invalid_aux_mask_{i}", invalid, $"slotbit_{i}", "zero8", u8)], u8);
            AddStage($"aux_{i}", active, $"slot_{i}", "zero", "zero", "zero32",
                $"aux{i}_hard_pinned", $"aux{i}_pinned_lane_id", $"aux{i}_slot_class", false);
        }
        nodes.Add(new HwOutputDriveNode("drive_assigned_lane_mask", "assigned_lane_mask", assignedLane, origin));
        nodes.Add(new HwOutputDriveNode("drive_assigned_slot_mask", "assigned_slot_mask", assignedSlot, origin));
        nodes.Add(new HwOutputDriveNode("drive_scalar_lane_mask", "scalar_lane_mask", scalarLane, origin));
        string classDomain = classDomainSignals.Aggregate((a, b) => And("class_domain_and_" + Array.IndexOf(classDomainSignals.ToArray(), b), a, b));
        nodes.Add(new HwOutputDriveNode("drive_class_inputs_defined", "class_inputs_defined", classDomain, origin));
        string inputContract = "one";
        string scalarCountValid = "zero";
        for (int count = 0; count <= 4; count++)
            scalarCountValid = Or("scalar_count_valid_" + count, scalarCountValid, Eq("scalar_count_eq_" + count, "scalar_count", "slot_count_" + count));
        inputContract = And("input_contract_scalar_count", inputContract, scalarCountValid);
        string auxCountValid = "zero";
        for (int count = 0; count <= 8; count++)
            auxCountValid = Or("aux_count_valid_" + count, auxCountValid, Eq("aux_count_eq_" + count, "auxiliary_slot_count", count == 8 ? "aux_count_8" : "aux_count_" + count));
        inputContract = And("input_contract_aux_count", inputContract, auxCountValid);
        for (int i = 0; i < 4; i++)
        {
            string inactive = Not("scalar" + i + "_inactive", scalarActive[i]);
            string presentOrInactive = Or("scalar" + i + "_entry_contract", inactive, "scalar" + i + "_microop_present");
            inputContract = And("input_contract_scalar_entry_" + i, inputContract, presentOrInactive);
            string slotInRange = "zero";
            for (int s = 0; s < 8; s++)
                slotInRange = Or("scalar" + i + "_slot_range_or_" + s, slotInRange, Eq("scalar" + i + "_slot_range_" + s, scalarSlots[i], "slot_" + s));
            string slotRangeOrUnused = Or("scalar" + i + "_slot_range_contract", Not("scalar" + i + "_entry_unused", scalarCountActive[i]), slotInRange);
            inputContract = And("input_contract_scalar_slot_" + i, inputContract, slotRangeOrUnused);
            for (int j = i + 1; j < 4; j++)
            {
                string bothActive = And("scalar" + i + "_" + j + "_both_active", scalarCountActive[i], scalarCountActive[j]);
                string sameSlot = Eq("scalar" + i + "_" + j + "_same_slot", scalarSlots[i], scalarSlots[j]);
                string uniqueOrInactive = Or("scalar" + i + "_" + j + "_unique_contract", Not("scalar" + i + "_" + j + "_not_both", bothActive), Not("scalar" + i + "_" + j + "_not_same", sameSlot));
                inputContract = And("input_contract_scalar_unique_" + i + "_" + j, inputContract, uniqueOrInactive);
            }
        }
        nodes.Add(new HwOutputDriveNode("drive_source_adapter_valid", "source_adapter_valid", inputContract, origin));
        nodes.Add(new HwOutputDriveNode("drive_unmapped_mask", "unmapped_selected_slot_mask", unmapped, origin));
        nodes.Add(new HwOutputDriveNode("drive_selected_mask", "selected_slot_mask", selectedMask, origin));
        for (int i = 0; i < 8; i++) nodes.Add(new HwOutputDriveNode($"drive_lane{i}_slot", $"lane{i}_slot", laneSlot[i], origin));
        var top = new HwModule("issue_packet_create_allocation_projection", "issue_packet_create_allocation_projection",
            ports, nodes, instances, new HwHardwareContract("scheduler.issue-packet.create-allocation.source-bounded.v1",
            HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        HwDesign design = resolver.Design with { TopModule = top.Id, Modules = [.. resolver.Design.Modules, top] };
        var result = new FailClosedHardwareIrValidator().Validate(design);
        return result.Validated ?? throw new InvalidDataException("Issue-packet Create allocation HWIR refused: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }
}
#endif
