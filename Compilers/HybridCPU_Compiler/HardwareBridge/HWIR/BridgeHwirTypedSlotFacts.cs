#if HARDWAREBRIDGE_HWIR
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>
/// Source-bounded combinational projection of TypedSlotBundleFacts.FromBundle
/// for an eight-element bundle using the method's default width of eight.
/// The occupied/class/hard-pinned inputs are an explicit adapter boundary:
/// they do not prove that compiler facts are active admission gates. Pinned
/// lane IDs and lane allocation are deliberately outside this module.
/// </summary>
public static class BridgeHwirTypedSlotFacts
{
    private const string RelativePath =
        "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/SlotLegality/SlotClassDefinitions.cs";

    private static readonly (string Name, string Port)[] CountedClasses =
    [
        ("AluClass", "alu_count"),
        ("LsuClass", "lsu_count"),
        ("DmaStreamClass", "dma_stream_count"),
        ("MatrixTileStreamClass", "matrix_tile_stream_count"),
        ("BranchControl", "branch_control_count"),
        ("SystemSingleton", "system_singleton_count")
    ];

    /// <summary>
    /// Compile the exact captured SlotClassDefinitions.cs bytes into a candidate
    /// facts projection. Input occupied[i] means both i is below the effective
    /// source limit and bundle[i] is non-null; class[i] and hardPinned[i] are the
    /// corresponding runtime Placement values supplied by the adapter.
    /// </summary>
    public static HwValidatedDesign Compile(byte[] source, string expectedSha256,
        string commit, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(toolchain);
        static string Hash(ReadOnlySpan<byte> bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        if (source.Length > 2_000_000 || Hash(source) != expectedSha256 ||
            commit.Length != 40 || commit.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Typed-slot facts source identity differs.");

        string text = new UTF8Encoding(false, true).GetString(source);
        var tree = CSharpSyntaxTree.ParseText(text);
        if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException("Typed-slot facts source contains syntax errors.");
        SyntaxNode root = tree.GetRoot();
        static BaseNamespaceDeclarationSyntax NamespaceOf(SyntaxNode node) =>
            node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single(n =>
                n.Name.ToString() == "YAKSys_Hybrid_CPU.Core");

        StructDeclarationSyntax factsType = root.DescendantNodes()
            .OfType<StructDeclarationSyntax>()
            .Single(s => s.Identifier.ValueText == "TypedSlotBundleFacts" &&
                NamespaceOf(s) is not null);
        MethodDeclarationSyntax method = factsType.Members
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "FromBundle");
        GuardMethodSignature(method);
        GuardProperties(factsType);
        GuardFromBundleBody(method);

        EnumDeclarationSyntax slotClass = root.DescendantNodes()
            .OfType<EnumDeclarationSyntax>()
            .Single(e => e.Identifier.ValueText == "SlotClass" &&
                NamespaceOf(e) is not null);
        EnumDeclarationSyntax pinningKind = root.DescendantNodes()
            .OfType<EnumDeclarationSyntax>()
            .Single(e => e.Identifier.ValueText == "SlotPinningKind" &&
                NamespaceOf(e) is not null);
        Dictionary<string, byte> classCodes = ReadByteEnum(slotClass, "SlotClass");
        Dictionary<string, byte> pinningCodes = ReadByteEnum(pinningKind, "SlotPinningKind");
        if (slotClass.BaseList?.Types.Single().ToString() != "byte" ||
            pinningKind.BaseList?.Types.Single().ToString() != "byte" ||
            !pinningCodes.TryGetValue("HardPinned", out byte hardPinnedCode) ||
            !classCodes.TryGetValue("Unclassified", out byte unclassifiedCode) ||
            CountedClasses.Any(c => !classCodes.ContainsKey(c.Name)))
            throw new InvalidDataException("Typed-slot enum codes or byte backing changed.");

        var sourceMethodHash = Hash(Encoding.UTF8.GetBytes(
            "typed-slot-facts-from-bundle/v1|" + expectedSha256 + "|" +
            string.Concat(method.DescendantTokens().Select(t => t.Text))));
        var position = method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        var origin = new HwSourceOrigin(
            "https://github.com/yuriyyak23/HybridCPU-v2", commit, RelativePath,
            "CircuitWeave.Captured.TypedSlotFacts.Projection",
            "YAKSys_Hybrid_CPU.Core.TypedSlotBundleFacts", "FromBundle",
            sourceMethodHash, HwSourceRole.Primary, position.Line + 1,
            position.Character + 1);

        var bit = new HwBitVectorType(1);
        var byteType = new HwBitVectorType(8);
        var intType = new HwBitVectorType(32);
        var ports = new List<HwPortContract>(8 * 3 + 8 + 8);
        for (int i = 0; i < 8; i++)
        {
            ports.Add(new HwPortContract($"slot{i}_occupied", HwPortDirection.Input, bit));
            ports.Add(new HwPortContract($"slot{i}_class", HwPortDirection.Input, byteType));
            ports.Add(new HwPortContract($"slot{i}_hard_pinned", HwPortDirection.Input, bit));
        }
        for (int i = 0; i < 8; i++)
            ports.Add(new HwPortContract($"slot{i}_class_out", HwPortDirection.Output, byteType));
        ports.Add(new HwPortContract("pinning_kind_mask", HwPortDirection.Output, byteType));
        ports.Add(new HwPortContract("flexible_op_count", HwPortDirection.Output, intType));
        ports.Add(new HwPortContract("pinned_op_count", HwPortDirection.Output, intType));
        foreach (var countedClass in CountedClasses)
            ports.Add(new HwPortContract(countedClass.Port, HwPortDirection.Output, byteType));

        var nodes = new List<HwNode>();
        void Constant(string id, HwBitVectorType type, byte value) =>
            nodes.Add(new HwConstantNode(id, type,
                Convert.ToString(value, 2).PadLeft(type.Width, '0'), origin));
        Constant("zero_bit", bit, 0);
        Constant("one_bit", bit, 1);
        Constant("zero_byte", byteType, 0);
        Constant("unclassified_byte", byteType, unclassifiedCode);
        Constant("zero_int", intType, 0);
        for (int i = 0; i < 8; i++)
            Constant($"slot{i}_mask_bit", byteType, (byte)(1 << i));
        foreach (var countedClass in CountedClasses)
            Constant($"class_{countedClass.Name}", byteType, classCodes[countedClass.Name]);

        string pinMask = "zero_byte";
        string pinnedCount = "zero_int";
        string flexibleCount = "zero_int";
        var classCounts = CountedClasses.ToDictionary(c => c.Name, _ => "zero_byte",
            StringComparer.Ordinal);
        for (int i = 0; i < 8; i++)
        {
            string occupied = $"slot{i}_occupied";
            string classInput = $"slot{i}_class";
            string hardPinned = $"slot{i}_hard_pinned";
            nodes.Add(new HwMuxNode($"slot{i}_class_result", occupied, classInput,
                "unclassified_byte", byteType, origin));
            nodes.Add(new HwOutputDriveNode($"drive_slot{i}_class", $"slot{i}_class_out",
                $"slot{i}_class_result", origin));

            string pinned = $"slot{i}_pinned_present";
            nodes.Add(new HwOperationNode(pinned, "and", [occupied, hardPinned], bit,
                Origin: origin));
            string notPinned = $"slot{i}_not_hard_pinned";
            nodes.Add(new HwOperationNode(notPinned, "xor", [hardPinned, "one_bit"], bit,
                Origin: origin));
            string flexible = $"slot{i}_flexible_present";
            nodes.Add(new HwOperationNode(flexible, "and", [occupied, notPinned], bit,
                Origin: origin));

            string maskPart = $"slot{i}_pin_mask_part";
            nodes.Add(new HwMuxNode(maskPart, pinned, $"slot{i}_mask_bit", "zero_byte",
                byteType, origin));
            string maskNext = $"pin_mask_after_slot{i}";
            nodes.Add(new HwOperationNode(maskNext, "or", [pinMask, maskPart], byteType,
                Origin: origin));
            pinMask = maskNext;

            pinnedCount = Accumulate("pinned", i, pinned, intType, pinnedCount);
            flexibleCount = Accumulate("flexible", i, flexible, intType, flexibleCount);
            foreach (var countedClass in CountedClasses)
            {
                string match = $"slot{i}_{countedClass.Name}_match";
                nodes.Add(new HwOperationNode(match, "eq",
                    [classInput, $"class_{countedClass.Name}"], bit, Origin: origin));
                string present = $"slot{i}_{countedClass.Name}_present";
                nodes.Add(new HwOperationNode(present, "and", [occupied, match], bit,
                    Origin: origin));
                classCounts[countedClass.Name] = Accumulate(countedClass.Name, i, present,
                    byteType, classCounts[countedClass.Name]);
            }
        }

        nodes.Add(new HwOutputDriveNode("drive_pinning_kind_mask", "pinning_kind_mask",
            pinMask, origin));
        nodes.Add(new HwOutputDriveNode("drive_flexible_op_count", "flexible_op_count",
            flexibleCount, origin));
        nodes.Add(new HwOutputDriveNode("drive_pinned_op_count", "pinned_op_count",
            pinnedCount, origin));
        foreach (var countedClass in CountedClasses)
            nodes.Add(new HwOutputDriveNode("drive_" + countedClass.Port,
                countedClass.Port, classCounts[countedClass.Name], origin));

        string Accumulate(string stem, int slot, string increment, HwBitVectorType type,
            string previous)
        {
            string extended = $"{stem}_slot{slot}_increment";
            nodes.Add(new HwOperationNode(extended, "zext", [increment], type, Origin: origin));
            string next = $"{stem}_count_after_slot{slot}";
            nodes.Add(new HwOperationNode(next, "add", [previous, extended], type,
                Origin: origin));
            return next;
        }

        var module = new HwModule("typed_slot_bundle_facts_projection",
            "typed_slot_bundle_facts_projection", ports, nodes, [],
            new HwHardwareContract("scheduler.typed-slot-bundle-facts.source-bounded.v1",
                HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var result = new FailClosedHardwareIrValidator().Validate(
            new HwDesign("hwir/v0.1", module.Id, [module], toolchain, []));
        return result.Validated ?? throw new InvalidDataException(
            "Typed-slot facts HWIR refused: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }

    private static void GuardMethodSignature(MethodDeclarationSyntax method)
    {
        if (method.ReturnType.ToString() != "TypedSlotBundleFacts" ||
            !method.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            !method.Modifiers.Any(SyntaxKind.StaticKeyword) ||
            method.ParameterList.Parameters.Count != 2 ||
            method.ParameterList.Parameters[0].Identifier.ValueText != "bundle" ||
            method.ParameterList.Parameters[0].Type?.ToString() != "MicroOp?[]" ||
            method.ParameterList.Parameters[1].Identifier.ValueText != "bundleWidth" ||
            method.ParameterList.Parameters[1].Type?.ToString() != "int" ||
            method.ParameterList.Parameters[1].Default?.Value.ToString() != "8")
            throw new InvalidDataException("TypedSlotBundleFacts.FromBundle signature/default changed.");
    }

    private static void GuardProperties(StructDeclarationSyntax factsType)
    {
        static PropertyDeclarationSyntax Property(StructDeclarationSyntax type, string name) =>
            type.Members.OfType<PropertyDeclarationSyntax>().Single(p =>
                p.Identifier.ValueText == name);
        for (int i = 0; i < 8; i++)
            if (Property(factsType, $"Slot{i}Class").Type.ToString() != "SlotClass")
                throw new InvalidDataException($"Typed-slot class field Slot{i}Class type changed.");
        if (Property(factsType, "PinningKindMask").Type.ToString() != "byte" ||
            Property(factsType, "FlexibleOpCount").Type.ToString() != "int" ||
            Property(factsType, "PinnedOpCount").Type.ToString() != "int")
            throw new InvalidDataException("Typed-slot pinning or total-count field type changed.");
        foreach (var countedClass in CountedClasses)
        {
            string property = countedClass.Name switch
            {
                "AluClass" => "AluCount",
                "LsuClass" => "LsuCount",
                "DmaStreamClass" => "DmaStreamCount",
                "MatrixTileStreamClass" => "MatrixTileStreamCount",
                "BranchControl" => "BranchControlCount",
                "SystemSingleton" => "SystemSingletonCount",
                _ => throw new InvalidDataException("Unknown counted class guard.")
            };
            if (Property(factsType, property).Type.ToString() != "byte")
                throw new InvalidDataException($"Typed-slot count field {property} type changed.");
        }
    }

    private static void GuardFromBundleBody(MethodDeclarationSyntax method)
    {
        const string expected = """
        {
            ArgumentNullException.ThrowIfNull(bundle);
            int limit = bundle.Length < bundleWidth ? bundle.Length : bundleWidth;
            Span<SlotClass> slotClasses = stackalloc SlotClass[8];
            byte pinningMask = 0;
            int flexibleCount = 0;
            int pinnedCount = 0;
            byte aluCount = 0, lsuCount = 0, dmaCount = 0, matrixTileStreamCount = 0, branchCount = 0, sysCount = 0;
            for (int i = 0; i < 8; i++)
            {
                if (i < limit && bundle[i] is { } op)
                {
                    SlotPlacementMetadata placement = op.Placement;
                    slotClasses[i] = placement.RequiredSlotClass;
                    if (placement.PinningKind == SlotPinningKind.HardPinned)
                    {
                        pinningMask |= (byte)(1 << i);
                        pinnedCount++;
                    }
                    else
                    {
                        flexibleCount++;
                    }
                    switch (placement.RequiredSlotClass)
                    {
                        case SlotClass.AluClass: aluCount++; break;
                        case SlotClass.LsuClass: lsuCount++; break;
                        case SlotClass.DmaStreamClass: dmaCount++; break;
                        case SlotClass.MatrixTileStreamClass: matrixTileStreamCount++; break;
                        case SlotClass.BranchControl: branchCount++; break;
                        case SlotClass.SystemSingleton: sysCount++; break;
                    }
                }
                else
                {
                    slotClasses[i] = SlotClass.Unclassified;
                }
            }
            return new TypedSlotBundleFacts
            {
                Slot0Class = slotClasses[0],
                Slot1Class = slotClasses[1],
                Slot2Class = slotClasses[2],
                Slot3Class = slotClasses[3],
                Slot4Class = slotClasses[4],
                Slot5Class = slotClasses[5],
                Slot6Class = slotClasses[6],
                Slot7Class = slotClasses[7],
                PinningKindMask = pinningMask,
                FlexibleOpCount = flexibleCount,
                PinnedOpCount = pinnedCount,
                AluCount = aluCount,
                LsuCount = lsuCount,
                DmaStreamCount = dmaCount,
                MatrixTileStreamCount = matrixTileStreamCount,
                BranchControlCount = branchCount,
                SystemSingletonCount = sysCount
            };
        }
        """;
        if (method.Body is null || !TokensEqual(method.Body,
                SyntaxFactory.ParseStatement(expected)))
            throw new InvalidDataException("TypedSlotBundleFacts.FromBundle body changed.");

        ForStatementSyntax scan = method.Body.DescendantNodes()
            .OfType<ForStatementSyntax>().SingleOrDefault(f =>
                f.Condition?.ToString() == "i < 8")
            ?? throw new InvalidDataException("Typed-slot scan is no longer exactly eight slots.");
        if (scan.Declaration?.Variables.SingleOrDefault()?.Identifier.ValueText != "i" ||
            scan.Incrementors.Count != 1 || scan.Incrementors[0].ToString() != "i++")
            throw new InvalidDataException("Typed-slot scan bounds/increment changed.");
        IfStatementSyntax occupied = scan.Statement.DescendantNodes()
            .OfType<IfStatementSyntax>().SingleOrDefault(i =>
                i.Condition.ToString() == "i < limit && bundle[i] is { } op")
            ?? throw new InvalidDataException("Typed-slot occupied branch changed.");
        if (!occupied.Statement.DescendantNodes().OfType<VariableDeclarationSyntax>()
                .Any(v => v.ToString() == "SlotPlacementMetadata placement = op.Placement") ||
            !occupied.Statement.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(a => a.ToString() == "slotClasses[i] = placement.RequiredSlotClass") ||
            !occupied.Statement.DescendantNodes().OfType<IfStatementSyntax>()
                .Any(i => i.Condition.ToString() ==
                    "placement.PinningKind == SlotPinningKind.HardPinned"))
            throw new InvalidDataException("Typed-slot placement class/pinning projection changed.");

        SwitchStatementSyntax classSwitch = occupied.Statement.DescendantNodes()
            .OfType<SwitchStatementSyntax>().SingleOrDefault(s =>
                s.Expression.ToString() == "placement.RequiredSlotClass")
            ?? throw new InvalidDataException("Typed-slot per-class switch missing or duplicated.");
        const string expectedSwitch = """
        switch (placement.RequiredSlotClass)
        {
            case SlotClass.AluClass: aluCount++; break;
            case SlotClass.LsuClass: lsuCount++; break;
            case SlotClass.DmaStreamClass: dmaCount++; break;
            case SlotClass.MatrixTileStreamClass: matrixTileStreamCount++; break;
            case SlotClass.BranchControl: branchCount++; break;
            case SlotClass.SystemSingleton: sysCount++; break;
        }
        """;
        if (!TokensEqual(classSwitch, SyntaxFactory.ParseStatement(expectedSwitch)))
            throw new InvalidDataException("Typed-slot per-class count switch changed.");

        ReturnStatementSyntax result = method.Body.Statements.OfType<ReturnStatementSyntax>()
            .SingleOrDefault() ?? throw new InvalidDataException("Typed-slot return missing.");
        if (!TokensEqual(result, SyntaxFactory.ParseStatement(expected)
                .DescendantNodes().OfType<ReturnStatementSyntax>().Single()))
            throw new InvalidDataException("Typed-slot return fields/order changed.");
    }

    private static bool TokensEqual(SyntaxNode left, SyntaxNode right) =>
        left.DescendantTokens().Select(t => t.Text)
            .SequenceEqual(right.DescendantTokens().Select(t => t.Text));

    private static Dictionary<string, byte> ReadByteEnum(EnumDeclarationSyntax declaration,
        string expectedName)
    {
        if (declaration.Identifier.ValueText != expectedName || declaration.BaseList?.Types.Single().ToString() != "byte")
            throw new InvalidDataException($"{expectedName} enum backing changed.");
        var values = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (EnumMemberDeclarationSyntax member in declaration.Members)
        {
            if (member.EqualsValue?.Value is not LiteralExpressionSyntax literal ||
                !byte.TryParse(Convert.ToString(literal.Token.Value, CultureInfo.InvariantCulture),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out byte value))
                throw new InvalidDataException($"{expectedName}.{member.Identifier.ValueText} is not an explicit byte literal.");
            values.Add(member.Identifier.ValueText, value);
        }
        return values;
    }
}
#endif
