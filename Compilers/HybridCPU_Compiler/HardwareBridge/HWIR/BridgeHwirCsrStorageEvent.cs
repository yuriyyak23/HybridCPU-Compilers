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
/// Abstract one-operation transition for the software CsrFile API. The input event
/// is already serialized and, where the instance has a reservation callback, already
/// acquired. This is not a clocked register file or a reservation/arbiter model.
/// </summary>
public static class BridgeHwirCsrStorageEvent
{
    public const string CsrFileSha256 = "2dc2c172d8248ad2fed0a3832b39008b0449f302e75280b8fef46b2d499ad470";
    public const string PolicySha256 = "4d899b1daa5442738b89730ff00d856cca381a81a7b7b1df65da008d78cc93d5";
    public const string AddressesSha256 = "a2be00ba7d3248bc0aa4e3a49e238a5e0afdd919b5cf1c0d9d2e0bfa01893ef9";
    public const string FsmSha256 = "43214e337d4640febb6877460df4478ad74bd7c63dfb71be6f410b1a7e05a54f";

    private sealed record Registration(string Name, ushort Address, uint Policy, ulong InitialValue, int Line);

    public sealed record SourceInventoryRow(string AddressMember, ushort Address, string Policy,
        uint PolicyValue, ulong InitialValue, string InitialExpression, int SourceLine);

    public static HwValidatedDesign Compile(byte[] csrFile, byte[] policySource, byte[] addressesSource,
        byte[] fsmSource, string baseCommit, HwToolchainIdentity toolchain,
        out IReadOnlyList<SourceInventoryRow> inventory)
    {
        ArgumentNullException.ThrowIfNull(csrFile);
        ArgumentNullException.ThrowIfNull(policySource);
        ArgumentNullException.ThrowIfNull(addressesSource);
        ArgumentNullException.ThrowIfNull(fsmSource);
        if (Hash(csrFile) != CsrFileSha256 || Hash(policySource) != PolicySha256 ||
            Hash(addressesSource) != AddressesSha256 || Hash(fsmSource) != FsmSha256)
            throw new InvalidDataException("CsrFile storage source bytes changed; rebuild the semantic crosswalk.");
        if (baseCommit != "1bfe41a49cf11c80fa6c6aee87accb54b78b331f" ||
            toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("Pinned active source identity and combinational projection profile required.");

        static SyntaxNode Parse(byte[] bytes, string label)
        {
            var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException(label + " source has C# syntax errors.");
            return tree.GetRoot();
        }
        SyntaxNode csrRoot = Parse(csrFile, "CsrFile");
        SyntaxNode policyRoot = Parse(policySource, "CsrAccessPolicy");
        SyntaxNode addressesRoot = Parse(addressesSource, "CsrAddresses");
        SyntaxNode fsmRoot = Parse(fsmSource, "CPU_Core.FSM");
        var csrType = csrRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(x => x.Identifier.ValueText == "CsrFile");
        var registerDefaults = csrType.Members.OfType<MethodDeclarationSyntax>()
            .Single(x => x.Identifier.ValueText == "RegisterDefaults");
        var registerMethod = csrType.Members.OfType<MethodDeclarationSyntax>()
            .Single(x => x.Identifier.ValueText == "Register" && x.ParameterList.Parameters.Count == 3);
        const string registerBody = "{ _policies[address] = policy; _values[address] = defaultValue; }";
        static bool Same(SyntaxNode a, SyntaxNode b) => a.DescendantTokens().Select(t => t.Text)
            .SequenceEqual(b.DescendantTokens().Select(t => t.Text));
        if (registerMethod.Body is null || !Same(registerMethod.Body, SyntaxFactory.ParseStatement(registerBody)))
            throw new InvalidDataException("CsrFile registration update semantics changed.");

        var addressType = addressesRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(x => x.Identifier.ValueText == "CsrAddresses");
        var addressMap = new Dictionary<string, ushort>(StringComparer.Ordinal);
        foreach (FieldDeclarationSyntax field in addressType.Members.OfType<FieldDeclarationSyntax>())
        {
            if (field.Declaration.Type.ToString() != "ushort" ||
                !field.Modifiers.Any(SyntaxKind.ConstKeyword)) continue;
            foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
            {
                if (variable.Initializer?.Value is not LiteralExpressionSyntax literal || literal.Token.Value is null)
                    throw new InvalidDataException("CsrAddresses contains a non-literal ushort constant: " + variable.Identifier.ValueText);
                addressMap.Add(variable.Identifier.ValueText, Convert.ToUInt16(literal.Token.Value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        var policyEnum = policyRoot.DescendantNodes().OfType<EnumDeclarationSyntax>()
            .Single(x => x.Identifier.ValueText == "CsrAccessPolicy");
        var policyMap = EnumValues(policyEnum);
        var powerEnum = fsmRoot.DescendantNodes().OfType<EnumDeclarationSyntax>()
            .Single(x => x.Identifier.ValueText == "CorePowerState");
        var powerMap = EnumValues(powerEnum);

        ulong DefaultValue(ArgumentSyntax argument, out string expression)
        {
            expression = argument.Expression.ToString();
            if (argument.Expression is LiteralExpressionSyntax literal && literal.Token.Value is not null)
                return Convert.ToUInt64(literal.Token.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (expression == "(ulong)Processor.CPU_Core.CorePowerState.C0_Active")
                return powerMap["C0_Active"];
            if (expression == "(ulong)Processor.CPU_Core.CorePowerState.P0_MaxPerformance")
                return powerMap["P0_MaxPerformance"];
            throw new InvalidDataException("CsrFile RegisterDefaults has an unresolved default expression: " + expression);
        }

        var calls = registerDefaults.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(x => x.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == "Register")
            .ToArray();
        if (calls.Length != 40) throw new InvalidDataException("Expected 40 active CsrFile registration calls; found " + calls.Length + ".");
        var registrations = new List<Registration>(calls.Length);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var numericAddresses = new HashSet<ushort>();
        var inventoryRows = new List<SourceInventoryRow>(calls.Length);
        foreach (InvocationExpressionSyntax call in calls)
        {
            if (call.ArgumentList.Arguments.Count is < 2 or > 3 ||
                call.ArgumentList.Arguments[0].Expression is not MemberAccessExpressionSyntax addressRef ||
                addressRef.Expression.ToString() != "CsrAddresses" ||
                call.ArgumentList.Arguments[1].Expression is not MemberAccessExpressionSyntax policyRef ||
                policyRef.Expression.ToString() != "CsrAccessPolicy")
                throw new InvalidDataException("Unrecognized CsrFile RegisterDefaults registration call.");
            string name = addressRef.Name.Identifier.ValueText;
            string policyName = policyRef.Name.Identifier.ValueText;
            if (!addressMap.TryGetValue(name, out ushort address) || !policyMap.TryGetValue(policyName, out ulong policyValue))
                throw new InvalidDataException("Unresolved CSR address or policy symbol: " + name + "/" + policyName);
            if (!names.Add(name) || !numericAddresses.Add(address))
                throw new InvalidDataException("Duplicate CSR address registration: " + name + " / " + address.ToString("X3"));
            string defaultExpression = "0UL";
            ulong initial = call.ArgumentList.Arguments.Count == 3
                ? DefaultValue(call.ArgumentList.Arguments[2], out defaultExpression)
                : 0UL;
            if (call.ArgumentList.Arguments.Count == 2) defaultExpression = "0UL";
            int line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            registrations.Add(new(name, address, checked((uint)policyValue), initial, line));
            inventoryRows.Add(new(name, address, policyName, checked((uint)policyValue), initial, defaultExpression, line));
        }
        if (names.Count != 40 || addressMap.Count != 42 || powerMap.GetValueOrDefault("C0_Active", ulong.MaxValue) != 0 ||
            powerMap.GetValueOrDefault("P0_MaxPerformance", ulong.MaxValue) != 100)
            throw new InvalidDataException("Source registration/default crosswalk changed.");

        var read = csrType.Members.OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "Read");
        var write = csrType.Members.OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "Write");
        var hardwareWrite = csrType.Members.OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "HardwareWrite");
        var directRead = csrType.Members.OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "DirectRead");
        var isRegistered = csrType.Members.OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == "IsRegistered");
        // Exact byte pins above bind these method bodies. The shape guards keep the event boundary explicit.
        if (!read.ToString().Contains("_reserveAccess?.Invoke(", StringComparison.Ordinal) ||
            !write.ToString().Contains("_reserveAccess?.Invoke(", StringComparison.Ordinal) ||
            !hardwareWrite.ToString().Contains("_reserveAccess?.Invoke(", StringComparison.Ordinal) ||
            !directRead.ToString().Contains("_reserveAccess?.Invoke(", StringComparison.Ordinal) ||
            isRegistered.ToString().Contains("_reserveAccess?.Invoke(", StringComparison.Ordinal))
            throw new InvalidDataException("CsrFile API reservation boundary changed.");
        inventory = inventoryRows;

        HwValidatedDesign access = BridgeHwirCsrAccess.Compile(csrFile, policySource, baseCommit, toolchain);
        HwModule child = access.Design.Modules.Single(m => m.Id == access.Design.TopModule);
        HwSourceOrigin origin = new("https://github.com/yuriyyak23/HybridCPU-v2", baseCommit,
            "HybridCPU_ISE/CloseToHSL/Core/Architecture/Registers/CSR/CsrFile.cs",
            "CircuitWeave.CsrStorageEvent", "YAKSys_Hybrid_CPU.Core.Registers.CsrFile",
            "RegisterDefaults", Hash(Encoding.UTF8.GetBytes(string.Join("|", new[] { CsrFileSha256, PolicySha256, AddressesSha256, FsmSha256 }))),
            HwSourceRole.Primary, registerDefaults.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            registerDefaults.Identifier.GetLocation().GetLineSpan().StartLinePosition.Character + 1);
        var u1 = new HwBitVectorType(1); var u2 = new HwBitVectorType(2); var u3 = new HwBitVectorType(3);
        var u16 = new HwBitVectorType(16); var u32 = new HwBitVectorType(32); var u64 = new HwBitVectorType(64);
        var ports = new List<HwPortContract>(); var nodes = new List<HwNode>(); var instances = new List<HwInstanceNode>();
        void Input(string name, HwBitVectorType type) => ports.Add(new(name, HwPortDirection.Input, type));
        void Output(string name, HwBitVectorType type) => ports.Add(new(name, HwPortDirection.Output, type));
        string Const(string id, ulong value, HwBitVectorType type)
        {
            string bits = Convert.ToString(unchecked((long)value), 2).PadLeft(type.Width, '0');
            nodes.Add(new HwConstantNode(id, type, bits[^type.Width..], origin)); return id;
        }
        int serial = 0;
        string Op(string opcode, HwBitVectorType type, params string[] arguments)
        { string id = "n" + serial++; nodes.Add(new HwOperationNode(id, opcode, arguments, type, Origin: origin)); return id; }
        string Eq(string signal, ulong value, HwBitVectorType type)
            => Op("eq", u1, signal, Const("k" + serial++, value, type));
        string Or(params string[] terms)
        {
            if (terms.Length == 0) return Const("false" + serial++, 0, u1);
            return terms.Skip(1).Aggregate(terms[0], (left, right) => Op("or", u1, left, right));
        }
        string And(params string[] terms)
        {
            if (terms.Length == 0) return Const("true" + serial++, 1, u1);
            return terms.Skip(1).Aggregate(terms[0], (left, right) => Op("and", u1, left, right));
        }
        string Not(string signal) => Op("xor", u1, signal, Const("not1" + serial++, 1, u1));
        string Mux(string id, string select, string yes, string no, HwBitVectorType type)
        { nodes.Add(new HwMuxNode(id, select, yes, no, type, origin)); return id; }
        string Drive(string id, string port, string signal)
        { nodes.Add(new HwOutputDriveNode(id, port, signal, origin)); return id; }

        Input("event_valid", u1); Input("operation", u3); Input("address", u16);
        Input("write_data", u64); Input("privilege", u32);
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Registration registration in registrations)
        {
            string port = "current_" + registration.Name;
            Input(port, u64); current.Add(registration.Name, port);
        }
        Output("operation_valid", u1); Output("operation_invalid", u1); Output("reservation_kind", u2);
        Output("is_registered", u1); Output("read_data", u64); Output("write_enable", u1);
        Output("unknown_address_fault", u1); Output("privilege_fault", u1); Output("fault_address", u16);
        Output("required_privilege", u32); Output("actual_privilege", u32); Output("is_write_fault", u1); Output("selected_policy", u32);
        var nextPorts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Registration registration in registrations)
        {
            string port = "next_" + registration.Name;
            Output(port, u64); nextPorts.Add(registration.Name, port);
        }
        string ev = "event_valid", opKind = "operation", addr = "address", inputData = "write_data", priv = "privilege";
        string opRead = Eq(opKind, 0, u3), opWrite = Eq(opKind, 1, u3), opHardwareWrite = Eq(opKind, 2, u3);
        string opDirectRead = Eq(opKind, 3, u3), opIsRegistered = Eq(opKind, 4, u3);
        string opValid = Or(opRead, opWrite, opHardwareWrite, opDirectRead, opIsRegistered);
        string active = And(ev, opValid); string invalid = And(ev, Not(opValid));
        string isAddressAccess = Or(opRead, opWrite, opHardwareWrite);
        string readKind = Or(opRead, opDirectRead); string writeKind = Or(opWrite, opHardwareWrite);
        string reserveKind = Mux("reserve_kind_write", writeKind, Const("reserve_write_code", 2, u2),
            Mux("reserve_kind_read", readKind, Const("reserve_read_code", 1, u2), Const("reserve_none", 0, u2), u2), u2);

        var matches = new Dictionary<string, string>(StringComparer.Ordinal);
        var policySignal = Const("policy_default", 0, u32); var readValue = Const("read_default", 0, u64);
        foreach (Registration registration in registrations)
        {
            string match = Eq(addr, registration.Address, u16); matches.Add(registration.Name, match);
            string selectedPolicy = Mux("policy_" + registration.Name, match,
                Const("policy_value_" + registration.Name, registration.Policy, u32), policySignal, u32);
            policySignal = selectedPolicy;
            readValue = Mux("read_" + registration.Name, match, current[registration.Name], readValue, u64);
        }
        string registered = Or(matches.Values.ToArray());
        
        nodes.Add(new HwWireNode("access_fault", u1, origin));
        nodes.Add(new HwWireNode("access_required", u32, origin));
        Drive("drive_policy_output", "selected_policy", policySignal);
        instances.Add(new HwInstanceNode("access_policy", child.Id,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["policy"] = policySignal, ["privilege"] = priv, ["is_write"] = opWrite,
                ["fault"] = "access_fault", ["required_level"] = "access_required"
            }, new Dictionary<string, string>(StringComparer.Ordinal), Origin: origin));
        string policyEvaluation = And(active, Or(opRead, opWrite), registered);
        string policyFault = And(policyEvaluation, "access_fault");
        string unknownFault = And(active, isAddressAccess, Not(registered));
        string anyFault = Or(unknownFault, policyFault);
        string success = And(active, Not(anyFault));
        string writeEnable = And(success, writeKind);
        string readEnable = And(success, readKind);
        string isRegisteredResult = And(active, opIsRegistered, registered);
        string readResult = Mux("read_result", readEnable, readValue, Const("read_output_zero", 0, u64), u64);
        string zero16 = Const("zero16", 0, u16); string zero32 = Const("zero32", 0, u32); string zero1 = Const("zero1", 0, u1);
        string faultAddress = Mux("fault_address_value", anyFault, addr, zero16, u16);
        string required = Mux("required_value", policyFault, "access_required", zero32, u32);
        string actual = Mux("actual_value", policyFault, priv, zero32, u32);
        string writeFault = And(policyFault, opWrite);
        Drive("drive_operation_valid", "operation_valid", opValid); Drive("drive_operation_invalid", "operation_invalid", invalid);
        Drive("drive_reservation_kind", "reservation_kind", reserveKind); Drive("drive_is_registered", "is_registered", isRegisteredResult);
        Drive("drive_read_data", "read_data", readResult); Drive("drive_write_enable", "write_enable", writeEnable);
        Drive("drive_unknown_fault", "unknown_address_fault", unknownFault); Drive("drive_privilege_fault", "privilege_fault", policyFault);
        Drive("drive_fault_address", "fault_address", faultAddress); Drive("drive_required_privilege", "required_privilege", required);
        Drive("drive_actual_privilege", "actual_privilege", actual); Drive("drive_is_write_fault", "is_write_fault", writeFault);
        foreach (Registration registration in registrations)
        {
            string writeThis = And(writeEnable, matches[registration.Name]);
            string next = Mux("next_value_" + registration.Name, writeThis, inputData, current[registration.Name], u64);
            Drive("drive_next_" + registration.Name, nextPorts[registration.Name], next);
        }
        var topPorts = ports.ToArray();
        const string moduleName = "csr_storage_serialized_event_projection";
        var top = new HwModule(moduleName, moduleName, topPorts, nodes, instances,
            new HwHardwareContract("csr.storage.serialized-event.unqualified.v1", HwModuleKind.Combinational,
                topPorts, new HwTimingContract(0, 1)), origin);
        HwModule[] modules = [top, .. access.Design.Modules];
        var result = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", moduleName, modules, toolchain, []));
        return result.Validated ?? throw new InvalidDataException("CSR storage-event HWIR refused: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    private static Dictionary<string, ulong> EnumValues(EnumDeclarationSyntax declaration)
    {
        ulong next = 0; var values = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (EnumMemberDeclarationSyntax member in declaration.Members)
        {
            ulong value = next;
            if (member.EqualsValue?.Value is LiteralExpressionSyntax literal && literal.Token.Value is not null)
                value = Convert.ToUInt64(literal.Token.Value, System.Globalization.CultureInfo.InvariantCulture);
            else if (member.EqualsValue is not null)
                throw new InvalidDataException("Nonliteral enum constant in " + declaration.Identifier.ValueText + ": " + member.Identifier.ValueText);
            values.Add(member.Identifier.ValueText, value); next = checked(value + 1);
        }
        return values;
    }
    private static Dictionary<string, int> EnumValues(EnumDeclarationSyntax declaration, bool signed = true)
    {
        ulong next = 0; var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (EnumMemberDeclarationSyntax member in declaration.Members)
        {
            ulong value = next;
            if (member.EqualsValue?.Value is LiteralExpressionSyntax literal && literal.Token.Value is not null)
                value = Convert.ToUInt64(literal.Token.Value, System.Globalization.CultureInfo.InvariantCulture);
            else if (member.EqualsValue is not null)
                throw new InvalidDataException("Nonliteral enum constant in " + declaration.Identifier.ValueText + ": " + member.Identifier.ValueText);
            values.Add(member.Identifier.ValueText, checked((int)value)); next = checked(value + 1);
        }
        return values;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
