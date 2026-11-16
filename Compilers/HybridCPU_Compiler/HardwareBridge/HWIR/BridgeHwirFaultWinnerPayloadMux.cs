#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Selects the scalar and flattened translation-fault payload carried by a resolved stage/lane winner.</summary>
public static class BridgeHwirFaultWinnerPayloadMux
{
    private const string ActiveSha = "e1fd930195f41a1a79694f36868cb44325db7c2970c5893ea686ec649d71cb8e";
    private const string GoldenSha = "e599b81db714cbb2a169b28f3eac4bcffa8e58141814624dc1df3891a9af45e7";
    private const string ActiveStageSha = "f84c179da79ff204159a006aceb006e8eae865060d0c0e40db8a774d75b6bfa4";
    private const string GoldenStageSha = "5a9e7287988f863e67cbeb29bed1376204746ab84590515387c02c7d0f3606c8";
    private const string ActiveWriteBackLaneSha = "688c042479b25e2e55c2b0207cf3b36ae5c562243aac61e0a37fea30435ff09b";
    private const string GoldenWriteBackLaneSha = "68f985d63790ef0ee4962ff28049fe964a14ab05ed8c4a4e6d9e1a429c982afb";
    private const string ActiveMemoryLaneSha = "96ae1c994e270bd695f54a9ede789f1c62a0c99c1687f989bd4eba6ccdd09975";
    private const string GoldenMemoryLaneSha = "dbbe9079ce6f5a4f8d0636cad8a4b1a59b440baa5bd39d75311c71fd9390e49f";
    private const string ActiveExecuteLaneSha = "f94d43185d565e28071d41a07de906b0283775b0bbccf211b60dae1781843000";
    private const string GoldenExecuteLaneSha = "cfb959ce4b202494346de8e91fef43a6c2cf28bf8222ac2feb9810d0b028c90b";
    private const string ActiveTranslationFaultSha = "ccf38f3a0d76ae605b30405209b00d314fa1d99ecbe52c87dcaf9bf5b62f9157";
    private const string GoldenTranslationFaultSha = "e970474a7da42603b5253927db577488dfe7bba2a8661f904744bd0e8f2c4b82";
    private const string ActiveCpuCoreStateSha = "096a6b65837411332de085bdda4e8dd90a5673e4cda4b9cd7728a938b4e6d82b";
    private const string GoldenCpuCoreStateSha = "a0832a230baa9e0cebf5e6ffcf9c1068c1365ead2aa3bfd4836d3d614ad7d94d";
    private const string Commit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
    private const string Path = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.Pipeline.Helpers.cs";
    private const string TranslationFaultPath = "HybridCPU_ISE/CloseToHSL/Core/Runtime/Memory/Translation/CpuInstructionTranslationContour.cs";
    private const string CpuCoreStatePath = "HybridCPU_ISE/CloseToHSL/Core/Architecture/State/Architectural/CPU_Core.StateData.cs";

    public static HwValidatedDesign Compile(byte[] activeHelpers, byte[] goldenHelpers,
        byte[] activeStageEnum, byte[] goldenStageEnum,
        byte[] activeWriteBackLane, byte[] goldenWriteBackLane,
        byte[] activeMemoryLane, byte[] goldenMemoryLane,
        byte[] activeExecuteLane, byte[] goldenExecuteLane,
        byte[] activeTranslationFault, byte[] goldenTranslationFault,
        byte[] activeCpuCoreState, byte[] goldenCpuCoreState,
        HwToolchainIdentity toolchain)
    {
        if (Hash(activeHelpers) != ActiveSha || Hash(goldenHelpers) != GoldenSha ||
            Hash(activeStageEnum) != ActiveStageSha || Hash(goldenStageEnum) != GoldenStageSha ||
            Hash(activeWriteBackLane) != ActiveWriteBackLaneSha || Hash(goldenWriteBackLane) != GoldenWriteBackLaneSha ||
            Hash(activeMemoryLane) != ActiveMemoryLaneSha || Hash(goldenMemoryLane) != GoldenMemoryLaneSha ||
            Hash(activeExecuteLane) != ActiveExecuteLaneSha || Hash(goldenExecuteLane) != GoldenExecuteLaneSha ||
            Hash(activeTranslationFault) != ActiveTranslationFaultSha || Hash(goldenTranslationFault) != GoldenTranslationFaultSha ||
            Hash(activeCpuCoreState) != ActiveCpuCoreStateSha || Hash(goldenCpuCoreState) != GoldenCpuCoreStateSha ||
            toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("Fault payload mux requires exact active and pinned-Golden source bytes.");
        static SyntaxNode Parse(byte[] bytes)
        {
            var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Fault payload source contains C# syntax errors.");
            return tree.GetRoot();
        }
        static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));
        SyntaxNode active = Parse(activeHelpers), golden = Parse(goldenHelpers);
        foreach (string name in new[] { "BuildWriteBackWinnerMetadata", "BuildMemoryWinnerMetadata", "BuildExecuteWinnerMetadata" })
        {
            MethodDeclarationSyntax a = active.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);
            MethodDeclarationSyntax g = golden.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);
            if (Tokens(a) != Tokens(g)) throw new InvalidDataException("Active/Golden winner metadata builder differs: " + name);
        }
        (byte[] Active, byte[] Golden, string TypeName)[] laneSources =
        [
            (activeWriteBackLane, goldenWriteBackLane, "ScalarWriteBackLaneState"),
            (activeMemoryLane, goldenMemoryLane, "ScalarMemoryLaneState"),
            (activeExecuteLane, goldenExecuteLane, "ScalarExecuteLaneState")
        ];
        foreach ((byte[] activeBytes, byte[] goldenBytes, string typeName) in laneSources)
        {
            StructDeclarationSyntax activeLane = Parse(activeBytes).DescendantNodes().OfType<StructDeclarationSyntax>()
                .Single(s => s.Identifier.ValueText == typeName);
            StructDeclarationSyntax goldenLane = Parse(goldenBytes).DescendantNodes().OfType<StructDeclarationSyntax>()
                .Single(s => s.Identifier.ValueText == typeName);
            if (Tokens(activeLane) != Tokens(goldenLane))
                throw new InvalidDataException("Active/Golden lane metadata declaration differs: " + typeName);
            foreach ((string field, string type) in new[]
                { ("OwnerThreadId", "int"), ("VirtualThreadId", "int"), ("PC", "ulong"), ("CpuTranslationFault", "CpuInstructionTranslationFault?") })
            {
                FieldDeclarationSyntax declaration = activeLane.Members.OfType<FieldDeclarationSyntax>()
                    .Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == field));
                if (declaration.Declaration.Type.ToString() != type)
                    throw new InvalidDataException($"Lane field type changed: {typeName}.{field}");
            }
        }
        RecordDeclarationSyntax activeFaultRecord = Parse(activeTranslationFault).DescendantNodes().OfType<RecordDeclarationSyntax>()
            .Single(r => r.Identifier.ValueText == "CpuInstructionTranslationFault");
        RecordDeclarationSyntax goldenFaultRecord = Parse(goldenTranslationFault).DescendantNodes().OfType<RecordDeclarationSyntax>()
            .Single(r => r.Identifier.ValueText == "CpuInstructionTranslationFault");
        if (Tokens(activeFaultRecord.ParameterList!) != Tokens(goldenFaultRecord.ParameterList!))
            throw new InvalidDataException("Active/Golden CpuInstructionTranslationFault positional contract differs.");
        static void RequireParameters(SyntaxNode root, string recordName, params (string Name, string Type)[] expected)
        {
            RecordDeclarationSyntax record = root.DescendantNodes().OfType<RecordDeclarationSyntax>()
                .Single(r => r.Identifier.ValueText == recordName);
            foreach ((string name, string type) in expected)
            {
                ParameterSyntax parameter = record.ParameterList!.Parameters.Single(p => p.Identifier.ValueText == name);
                if (parameter.Type?.ToString() != type)
                    throw new InvalidDataException($"Translation-fault field type changed: {recordName}.{name}");
            }
        }
        static void RequireProperty(SyntaxNode root, string recordName, string name, string type)
        {
            RecordDeclarationSyntax record = root.DescendantNodes().OfType<RecordDeclarationSyntax>()
                .Single(r => r.Identifier.ValueText == name || r.Identifier.ValueText == recordName);
            PropertyDeclarationSyntax property = record.Members.OfType<PropertyDeclarationSyntax>()
                .Single(p => p.Identifier.ValueText == name);
            if (property.Type.ToString() != type)
                throw new InvalidDataException($"Translation-fault property type changed: {recordName}.{name}");
        }
        (string Name, string Type)[] faultParameters =
        [
            ("Reason", "CpuInstructionTranslationFaultReason"), ("Request", "CpuInstructionTranslationRequest"),
            ("OwnerEpoch", "ulong"), ("Qualification", "ulong"), ("Auxiliary", "ulong"),
            ("FaultStage", "CpuInstructionTranslationFaultStage"), ("HasGuestPhysicalAddress", "bool"),
            ("GuestPhysicalAddress", "ulong"), ("MemoryDomainOwnerEpoch", "ulong"),
            ("AddressSpaceGeneration", "ulong"), ("AddressSpaceIdentity", "ulong")
        ];
        RequireParameters(Parse(activeTranslationFault), "CpuInstructionTranslationFault", faultParameters);
        RequireParameters(Parse(goldenTranslationFault), "CpuInstructionTranslationFault", faultParameters);
        foreach (byte[] source in new[] { activeTranslationFault, goldenTranslationFault })
        {
            SyntaxNode root = Parse(source);
            RequireParameters(root, "CpuInstructionTranslationScope", ("DomainId", "ulong"),
                ("ContextId", "int"), ("VirtualThreadId", "int"));
            RequireParameters(root, "CpuInstructionTranslationOperationIdentity", ("AttemptId", "ulong"),
                ("EventId", "ulong"), ("MemoryOperationId", "ulong"));
            RequireParameters(root, "CpuInstructionTranslationRequest", ("Scope", "CpuInstructionTranslationScope"),
                ("AccessKind", "CpuInstructionTranslationAccessKind"), ("VirtualAddress", "ulong"),
                ("AccessSize", "ushort"), ("OperationIdentity", "CpuInstructionTranslationOperationIdentity"));
        }
        SyntaxNode activeFaultRoot = Parse(activeTranslationFault);
        RequireProperty(activeFaultRoot, "CpuInstructionTranslationFault", "FailureOffset", "ushort");
        RequireProperty(activeFaultRoot, "CpuInstructionTranslationFault", "TranslationGeneration", "ulong");
        RequireProperty(activeFaultRoot, "CpuInstructionTranslationFault", "RequestStartGuestPhysicalAddress", "ulong?");
        PropertyDeclarationSyntax activeFaultValidity = activeFaultRoot.DescendantNodes().OfType<RecordDeclarationSyntax>()
            .Single(r => r.Identifier.ValueText == "CpuInstructionTranslationFault").Members.OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "IsValid");
        PropertyDeclarationSyntax goldenFaultValidity = Parse(goldenTranslationFault).DescendantNodes().OfType<RecordDeclarationSyntax>()
            .Single(r => r.Identifier.ValueText == "CpuInstructionTranslationFault").Members.OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "IsValid");
        string activeValidityTokens = Tokens(activeFaultValidity), goldenValidityTokens = Tokens(goldenFaultValidity);
        foreach (string required in new[] { "Reason!=CpuInstructionTranslationFaultReason.None", "Request.IsValid",
                     "FailureOffset<Request.AccessSize", "OwnerEpoch!=0", "FaultStage!=CpuInstructionTranslationFaultStage.SecondStage",
                     "HasGuestPhysicalAddress", "MemoryDomainOwnerEpoch!=0", "AddressSpaceGeneration!=0", "AddressSpaceIdentity!=0" })
            if (!activeValidityTokens.Contains(required, StringComparison.Ordinal))
                throw new InvalidDataException("Active translation-fault IsValid source contract changed: " + required);
        foreach (string required in new[] { "Reason!=CpuInstructionTranslationFaultReason.None", "Request.IsValid",
                     "OwnerEpoch!=0", "FaultStage!=CpuInstructionTranslationFaultStage.SecondStage",
                     "HasGuestPhysicalAddress", "MemoryDomainOwnerEpoch!=0", "AddressSpaceGeneration!=0", "AddressSpaceIdentity!=0" })
            if (!goldenValidityTokens.Contains(required, StringComparison.Ordinal))
                throw new InvalidDataException("Golden translation-fault IsValid source contract changed: " + required);
        PropertyDeclarationSyntax activeFailingAddress = activeFaultRoot.DescendantNodes().OfType<RecordDeclarationSyntax>()
            .Single(r => r.Identifier.ValueText == "CpuInstructionTranslationFault").Members.OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "FailingVirtualAddress");
        if (!Tokens(activeFailingAddress).Contains("checked(Request.VirtualAddress+FailureOffset)", StringComparison.Ordinal))
            throw new InvalidDataException("Active checked failing-virtual-address source contract changed.");
        SyntaxNode activeEnumRoot = Parse(activeStageEnum), goldenEnumRoot = Parse(goldenStageEnum);
        EnumDeclarationSyntax activeEnum = activeEnumRoot.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(e => e.Identifier.ValueText == "PipelineStage");
        EnumDeclarationSyntax goldenEnum = goldenEnumRoot.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(e => e.Identifier.ValueText == "PipelineStage");
        if (Tokens(activeEnum) != Tokens(goldenEnum)) throw new InvalidDataException("Active/Golden PipelineStage enum differs.");
        int EnumValue(string name)
        {
            EnumMemberDeclarationSyntax item = activeEnum.Members.Single(m => m.Identifier.ValueText == name);
            if (item.EqualsValue is null || !int.TryParse(item.EqualsValue.Value.ToString(), out int value))
                throw new InvalidDataException("PipelineStage numeric value is not explicit: " + name);
            return value;
        }
        int wbValue = EnumValue("WriteBack"), memValue = EnumValue("Memory"), exValue = EnumValue("Execute");
        if (wbValue != 5 || memValue != 4 || exValue != 3) throw new InvalidDataException("Pinned PipelineStage values changed.");

        MethodDeclarationSyntax metadataResolver = active.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "TryResolveStageAwareExceptionWinnerMetadata");
        FileLinePositionSpan metadataSpan = metadataResolver.Identifier.GetLocation().GetLineSpan();
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", Commit, Path,
            "CircuitWeave.FaultWinnerPayloadMux", "YAKSys_Hybrid_CPU.Processor.CPU_Core",
            "TryResolveStageAwareExceptionWinnerMetadata", Hash(Encoding.UTF8.GetBytes(string.Join("|", "winner-metadata/v3",
                ActiveSha, GoldenSha, ActiveStageSha, GoldenStageSha,
                ActiveWriteBackLaneSha, GoldenWriteBackLaneSha, ActiveMemoryLaneSha, GoldenMemoryLaneSha,
                ActiveExecuteLaneSha, GoldenExecuteLaneSha, ActiveTranslationFaultSha, GoldenTranslationFaultSha,
                ActiveCpuCoreStateSha, GoldenCpuCoreStateSha))),
            HwSourceRole.Primary, metadataSpan.StartLinePosition.Line + 1, metadataSpan.StartLinePosition.Character + 1);
        SyntaxNode faultRootForOrigins = Parse(activeTranslationFault);
        HwSourceOrigin RecordConstructorOrigin(string recordName)
        {
            RecordDeclarationSyntax record = faultRootForOrigins.DescendantNodes().OfType<RecordDeclarationSyntax>()
                .Single(r => r.Identifier.ValueText == recordName);
            FileLinePositionSpan span = record.Identifier.GetLocation().GetLineSpan();
            string type = "YAKSys_Hybrid_CPU.Core.Memory." + recordName;
            string member = "#ctor";
            string digest = Hash(Encoding.UTF8.GetBytes(string.Join("|", "winner-translation-fault-origin/v2",
                ActiveTranslationFaultSha, GoldenTranslationFaultSha, recordName, member)));
            return new("https://github.com/yuriyyak23/HybridCPU-v2", Commit, TranslationFaultPath,
                "CircuitWeave.FaultWinnerPayloadMux", type, member, digest, HwSourceRole.Primary,
                span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
        }
        HwSourceOrigin FaultFieldOrigin(string field) => field switch
        {
            // Current Studio captured-source selection resolves callable member
            // identities, but not synthesized property getters. Anchor these
            // init-only properties to their containing record constructor until
            // Studio supports property-symbol source navigation.
            "failure_offset" or "translation_generation" or "has_request_start_guest_physical_address" or
                "request_start_guest_physical_address" => RecordConstructorOrigin("CpuInstructionTranslationFault"),
            "scope_domain_id" or "scope_context_id" or "scope_virtual_thread_id" =>
                RecordConstructorOrigin("CpuInstructionTranslationScope"),
            "access_kind" or "virtual_address" or "access_size" =>
                RecordConstructorOrigin("CpuInstructionTranslationRequest"),
            "operation_attempt_id" or "operation_event_id" or "memory_operation_id" =>
                RecordConstructorOrigin("CpuInstructionTranslationOperationIdentity"),
            _ => RecordConstructorOrigin("CpuInstructionTranslationFault")
        };
        HwSourceOrigin FaultSemanticOrigin(string propertyName)
        {
            PropertyDeclarationSyntax property = activeFaultRecord.Members.OfType<PropertyDeclarationSyntax>()
                .Single(p => p.Identifier.ValueText == propertyName);
            HwSourceOrigin recordOrigin = RecordConstructorOrigin("CpuInstructionTranslationFault");
            string digest = Hash(Encoding.UTF8.GetBytes(string.Join("|", "winner-translation-fault-derived/v1",
                ActiveTranslationFaultSha, GoldenTranslationFaultSha, ActiveCpuCoreStateSha, GoldenCpuCoreStateSha,
                propertyName, Tokens(property), "SmtWays=4")));
            return recordOrigin with { IdentityDigest = digest };
        }
        HwBitVectorType bit = new(1), stage = new(3), lane = new(8), address = new(64), signed32 = new(32, HwSignedness.Signed);
        List<HwPortContract> ports = [new("winner_valid", HwPortDirection.Input, bit),
            new("winner_stage", HwPortDirection.Input, stage), new("winner_lane", HwPortDirection.Input, lane)];
        List<HwNode> nodes = [];
        int id = 0;
        string Const(string name, HwBitVectorType type, int value)
        { nodes.Add(new HwConstantNode(name, type, Convert.ToString(value, 2).PadLeft(type.Width, '0'), origin)); return name; }
        string Port(string name, HwBitVectorType type) { ports.Add(new(name, HwPortDirection.Input, type)); return name; }
        string Op(string opcode, IReadOnlyList<string> operands, HwBitVectorType type)
        { string n = "n" + id++; nodes.Add(new HwOperationNode(n, opcode, operands, type, Origin: origin)); return n; }
        string Eq(string a, string b) => Op("eq", [a, b], bit);
        string And(string a, string b) => Op("and", [a, b], bit);
        string Mux(string c, string a, string b, HwBitVectorType type, HwSourceOrigin? muxOrigin = null)
        { string n = "n" + id++; nodes.Add(new HwMuxNode(n, c, a, b, type, muxOrigin ?? origin)); return n; }
        string wb = Const("stage_writeback", stage, wbValue), mem = Const("stage_memory", stage, memValue), ex = Const("stage_execute", stage, exValue);
        string zeroBit = Const("zero_bit", bit, 0), zeroAddr = Const("zero_address", address, 0);
        string wbSel = Eq("winner_stage", wb), memSel = Eq("winner_stage", mem), exSel = Eq("winner_stage", ex);
        string zeroSigned = Const("zero_signed32", signed32, 0);
        var selected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fault_address"] = zeroAddr, ["fault_is_write"] = zeroBit,
            ["owner_thread_id"] = zeroSigned, ["virtual_thread_id"] = zeroSigned,
            ["pc"] = zeroAddr, ["has_cpu_translation_fault"] = zeroBit
        };
        var fieldTypes = new Dictionary<string, HwBitVectorType>(StringComparer.Ordinal)
        {
            ["fault_address"] = address, ["fault_is_write"] = bit,
            ["owner_thread_id"] = signed32, ["virtual_thread_id"] = signed32,
            ["pc"] = address, ["has_cpu_translation_fault"] = bit
        };
        // Flatten every positional/init field of CpuInstructionTranslationFault,
        // including nested value records. The active-only fields are explicitly
        // carried as part of the active model; Golden source pins do not declare
        // them, so this is payload transport rather than active/Golden semantic
        // equivalence. Nullable GPA is represented by a separate presence bit.
        var translationFaultFields = new Dictionary<string, HwBitVectorType>(StringComparer.Ordinal)
        {
            ["reason"] = new(8), ["scope_domain_id"] = new(64),
            ["scope_context_id"] = signed32, ["scope_virtual_thread_id"] = signed32,
            ["access_kind"] = new(8), ["virtual_address"] = new(64),
            ["access_size"] = new(16), ["operation_attempt_id"] = new(64),
            ["operation_event_id"] = new(64), ["memory_operation_id"] = new(64),
            ["owner_epoch"] = new(64), ["qualification"] = new(64), ["auxiliary"] = new(64),
            ["fault_stage"] = new(8), ["has_guest_physical_address"] = bit,
            ["guest_physical_address"] = new(64), ["memory_domain_owner_epoch"] = new(64),
            ["address_space_generation"] = new(64), ["address_space_identity"] = new(64),
            ["failure_offset"] = new(16), ["translation_generation"] = new(64),
            ["has_request_start_guest_physical_address"] = bit,
            ["request_start_guest_physical_address"] = new(64)
        };
        foreach ((string field, HwBitVectorType type) in translationFaultFields)
        {
            string prefix = "cpu_translation_fault_" + field;
            string zero = Const("zero_" + prefix, type, 0);
            selected[prefix] = zero;
            fieldTypes[prefix] = type;
        }
        foreach (string stageName in new[] { "wb", "mem", "ex" })
        {
            string stageSelect = stageName switch { "wb" => wbSel, "mem" => memSel, _ => exSel };
            for (int laneIndex = 0; laneIndex < 8; laneIndex++)
            {
                string laneConstant = Const($"lane_index_{stageName}_{laneIndex}", lane, laneIndex);
                string laneSelect = Eq("winner_lane", laneConstant);
                string condition = And("winner_valid", And(stageSelect, laneSelect));
                foreach (string field in selected.Keys.ToArray())
                {
                    string laneField = Port($"{stageName}_lane{laneIndex}_{field}", fieldTypes[field]);
                    selected[field] = Mux(condition, laneField, selected[field], fieldTypes[field],
                        field.StartsWith("cpu_translation_fault_", StringComparison.Ordinal)
                            ? FaultFieldOrigin(field["cpu_translation_fault_".Length..]) : origin);
                }
            }
        }
        string faultPrefix = "cpu_translation_fault_";
        string Fault(string name) => selected[faultPrefix + name];
        string oneBit = Const("one_bit", bit, 1);
        string Not(string value) => Op("xor", [value, oneBit], bit);
        string Or(string a, string b) => Op("or", [a, b], bit);
        string Nonzero(string value, HwBitVectorType type) => Not(Eq(value, Const("zero_check_" + id++, type, 0)));
        HwBitVectorType u64 = address, signed64 = new(64, HwSignedness.Signed);
        string domainNonzero = Nonzero(Fault("scope_domain_id"), u64);
        string context64 = Op("sext", [Fault("scope_context_id")], signed64);
        string contextPositive = Op("slt64", [Const("zero_ctx64", signed64, 0), context64], bit);
        string vtid64 = Op("sext", [Fault("scope_virtual_thread_id")], signed64);
        string vtidNonnegative = Not(Op("slt64", [vtid64, Const("zero_vtid64", signed64, 0)], bit));
        SyntaxNode activeStateRoot = Parse(activeCpuCoreState), goldenStateRoot = Parse(goldenCpuCoreState);
        FieldDeclarationSyntax ActiveSmtWaysField(SyntaxNode root) => root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "SmtWays"));
        FieldDeclarationSyntax activeSmtWays = ActiveSmtWaysField(activeStateRoot), goldenSmtWays = ActiveSmtWaysField(goldenStateRoot);
        string activeSmtWaysValue = activeSmtWays.Declaration.Variables.Single(v => v.Identifier.ValueText == "SmtWays").Initializer?.Value.ToString() ?? "";
        string goldenSmtWaysValue = goldenSmtWays.Declaration.Variables.Single(v => v.Identifier.ValueText == "SmtWays").Initializer?.Value.ToString() ?? "";
        if (activeSmtWaysValue != "4" || goldenSmtWaysValue != "4" || Tokens(activeSmtWays) != Tokens(goldenSmtWays))
            throw new InvalidDataException("Pinned active/Golden CPU_Core.SmtWays contract changed.");
        FileLinePositionSpan smtWaysSpan = activeSmtWays.Declaration.Variables.Single(v => v.Identifier.ValueText == "SmtWays").Identifier.GetLocation().GetLineSpan();
        var smtWaysOrigin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", Commit, CpuCoreStatePath,
            "CircuitWeave.FaultWinnerPayloadMux", "YAKSys_Hybrid_CPU.Processor.CPU_Core", "SmtWays",
            Hash(Encoding.UTF8.GetBytes(string.Join("|", "fault-winner-smtways/v1", ActiveCpuCoreStateSha, GoldenCpuCoreStateSha, "4"))),
            HwSourceRole.Primary, smtWaysSpan.StartLinePosition.Line + 1, smtWaysSpan.StartLinePosition.Character + 1);
        string smtWays = "pinned_smt_ways";
        nodes.Add(new HwConstantNode(smtWays, signed64, Convert.ToString(4, 2).PadLeft(64, '0'), smtWaysOrigin));
        string vtidInRange = Op("slt64", [vtid64, smtWays], bit);
        string scopeValid = And(domainNonzero, And(contextPositive, And(vtidNonnegative, vtidInRange)));
        string opAttemptNonzero = Nonzero(Fault("operation_attempt_id"), u64);
        string opEventNonzero = Nonzero(Fault("operation_event_id"), u64);
        string memoryOperationNonzero = Nonzero(Fault("memory_operation_id"), u64);
        string accessKindNonzero = Nonzero(Fault("access_kind"), new(8));
        string accessSizeNonzero = Nonzero(Fault("access_size"), new(16));
        string accessSize64 = Op("zext", [Fault("access_size")], u64);
        string failureOffset64 = Op("zext", [Fault("failure_offset")], u64);
        string offsetWithinAccess = Op("ult64", [failureOffset64, accessSize64], bit);
        string requestValid = And(scopeValid, And(accessKindNonzero,
            And(accessSizeNonzero, And(opAttemptNonzero, And(opEventNonzero, memoryOperationNonzero)))));
        string reasonNonzero = Nonzero(Fault("reason"), new(8));
        string ownerEpochNonzero = Nonzero(Fault("owner_epoch"), u64);
        string secondStage = Eq(Fault("fault_stage"), Const("fault_stage_second_stage", new(8), 2));
        string hasGpa = Fault("has_guest_physical_address");
        string secondStageValid = And(hasGpa, And(Nonzero(Fault("memory_domain_owner_epoch"), u64),
            And(Nonzero(Fault("address_space_generation"), u64), Nonzero(Fault("address_space_identity"), u64))));
        string stageValid = Or(Not(secondStage), secondStageValid);
        string hasFaultValue = selected["has_cpu_translation_fault"];
        string isValid = And(hasFaultValue, And(reasonNonzero, And(requestValid,
            And(offsetWithinAccess, And(ownerEpochNonzero, stageValid)))));
        ports.Add(new("winner_cpu_translation_fault_is_valid", HwPortDirection.Output, bit));
        nodes.Add(new HwOutputDriveNode("drive_winner_cpu_translation_fault_is_valid", "winner_cpu_translation_fault_is_valid", isValid, FaultSemanticOrigin("IsValid")));
        string failingAddress = Op("add", [Fault("virtual_address"), failureOffset64], u64);
        string addressOverflow = Op("ult64", [failingAddress, Fault("virtual_address")], bit);
        string presentFailingAddress = Mux(hasFaultValue, failingAddress, zeroAddr, u64);
        string presentAddressOverflow = And(hasFaultValue, addressOverflow);
        ports.Add(new("winner_cpu_translation_fault_failing_virtual_address_wrapped_sum", HwPortDirection.Output, address));
        nodes.Add(new HwOutputDriveNode("drive_winner_cpu_translation_fault_failing_virtual_address_wrapped_sum", "winner_cpu_translation_fault_failing_virtual_address_wrapped_sum", presentFailingAddress, FaultSemanticOrigin("FailingVirtualAddress")));
        ports.Add(new("winner_cpu_translation_fault_failing_virtual_address_overflow", HwPortDirection.Output, bit));
        nodes.Add(new HwOutputDriveNode("drive_winner_cpu_translation_fault_failing_virtual_address_overflow", "winner_cpu_translation_fault_failing_virtual_address_overflow", presentAddressOverflow, FaultSemanticOrigin("FailingVirtualAddress")));
        foreach ((string field, string expression) in selected)
        {
            string output = "winner_" + field;
            ports.Add(new(output, HwPortDirection.Output, fieldTypes[field]));
            nodes.Add(new HwOutputDriveNode("drive_" + output, output, expression,
                field.StartsWith("cpu_translation_fault_", StringComparison.Ordinal)
                    ? FaultFieldOrigin(field["cpu_translation_fault_".Length..]) : origin));
        }
        const string moduleId = "fault_winner_payload_mux";
        HwModule module = new(moduleId, moduleId, ports, nodes, [],
            new HwHardwareContract("processor.fault-winner-payload-mux.source-projection.v1", HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        HwDesign design = new("hwir/v0.1", module.Id, [module], toolchain, []);
        HwValidationResult validation = new FailClosedHardwareIrValidator().Validate(design);
        return validation.Validated ?? throw new InvalidDataException("Fault winner payload HWIR refused: " +
            string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
