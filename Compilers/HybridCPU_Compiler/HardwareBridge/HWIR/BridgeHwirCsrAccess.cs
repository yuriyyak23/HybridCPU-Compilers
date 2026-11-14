#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Source-bound pure combinational projection of the CsrFile privilege checks.</summary>
public static class BridgeHwirCsrAccess
{
    public const string FileSha256 = "2dc2c172d8248ad2fed0a3832b39008b0449f302e75280b8fef46b2d499ad470";
    public const string PolicySha256 = "4d899b1daa5442738b89730ff00d856cca381a81a7b7b1df65da008d78cc93d5";

    public static HwValidatedDesign Compile(byte[] csrFileBytes, byte[] policyBytes,
        string baseCommit, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(csrFileBytes);
        ArgumentNullException.ThrowIfNull(policyBytes);
        if (Hash(csrFileBytes) != FileSha256 || Hash(policyBytes) != PolicySha256)
            throw new InvalidDataException("CSR access source bytes changed.");
        if (baseCommit.Length != 40 || baseCommit.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("Pinned source commit and combinational profile required.");

        SyntaxNode Parse(byte[] bytes)
        {
            var tree = CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Invalid pinned C# syntax.");
            return tree.GetRoot();
        }
        var csrRoot = Parse(csrFileBytes);
        var policyRoot = Parse(policyBytes);
        var csr = csrRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(x => x.Identifier.ValueText == "CsrFile");
        MethodDeclarationSyntax Method(string name) => csr.Members.OfType<MethodDeclarationSyntax>()
            .Single(x => x.Identifier.ValueText == name);
        MethodDeclarationSyntax read = Method("CheckReadAccess");
        MethodDeclarationSyntax write = Method("CheckWriteAccess");

        var enums = policyRoot.DescendantNodes().OfType<EnumDeclarationSyntax>()
            .ToDictionary(x => x.Identifier.ValueText, StringComparer.Ordinal);
        static Dictionary<string, long> Values(EnumDeclarationSyntax e)
        {
            long next = 0;
            var values = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (EnumMemberDeclarationSyntax member in e.Members)
            {
                long value = member.EqualsValue is null
                    ? next
                    : Convert.ToInt64(Convert.ToString(member.EqualsValue.Value, System.Globalization.CultureInfo.InvariantCulture), 10);
                values.Add(member.Identifier.ValueText, value);
                next = checked(value + 1);
            }
            return values;
        }
        var policies = Values(enums["CsrAccessPolicy"]);
        var privileges = Values(enums["PrivilegeLevel"]);
        foreach (var pair in new[] { ("MachineReadWrite", 0L), ("ReadOnly", 1L), ("SupervisorReadWrite", 2L), ("HardwareUpdated", 3L) })
            if (policies.GetValueOrDefault(pair.Item1, long.MinValue) != pair.Item2)
                throw new InvalidDataException("CsrAccessPolicy enum mapping changed: " + pair.Item1);
        foreach (var pair in new[] { ("User", 0L), ("Supervisor", 1L), ("Machine", 3L) })
            if (privileges.GetValueOrDefault(pair.Item1, long.MinValue) != pair.Item2)
                throw new InvalidDataException("PrivilegeLevel enum mapping changed: " + pair.Item1);

        var line = read.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", baseCommit,
            "HybridCPU_ISE/CloseToHSL/Core/Architecture/Registers/CSR/CsrFile.cs",
            "CircuitWeave.CsrAccess", "YAKSys_Hybrid_CPU.Core.Registers.CsrFile",
            "CheckReadAccess", Hash(Encoding.UTF8.GetBytes(Hash(csrFileBytes) + "|" + Hash(policyBytes))),
            HwSourceRole.Primary, line.Line + 1, line.Character + 1);

        var ports = new List<HwPortContract>();
        var nodes = new List<HwNode>();
        int serial = 0;
        var constants = new Dictionary<(uint, int), string>();
        var ops = new Dictionary<string, string>(StringComparer.Ordinal);
        HwBitVectorType T(int width) => new(width);
        string Input(string name, int width)
        {
            ports.Add(new(name, HwPortDirection.Input, T(width)));
            return name;
        }
        string C(uint value, int width)
        {
            if (constants.TryGetValue((value, width), out string? old)) return old;
            string id = "k" + serial++;
            nodes.Add(new HwConstantNode(id, T(width), Convert.ToString(value, 2).PadLeft(width, '0')[^width..], origin));
            constants.Add((value, width), id);
            return id;
        }
        string Op(string opcode, int width, params string[] args)
        {
            string key = opcode + "|" + width + "|" + string.Join("|", args);
            if (ops.TryGetValue(key, out string? old)) return old;
            string id = "n" + serial++;
            nodes.Add(new HwOperationNode(id, opcode, args, T(width), Origin: origin));
            ops.Add(key, id);
            return id;
        }
        string Or(params string[] terms) => terms.Aggregate((a, b) => Op("or", 1, a, b));
        string Eq(string a, uint b) => Op("eq", 1, a, C(b, 32));

        string policy = Input("policy", 32);
        string privilege = Input("privilege", 32);
        string isWrite = Input("is_write", 1);
        string p0 = Eq(policy, 0), p1 = Eq(policy, 1), p2 = Eq(policy, 2), p3 = Eq(policy, 3);
        string machine = Eq(privilege, 3);
        string sign = Op("eq", 1, Op("and", 32, privilege, C(0x80000000, 32)), C(0x80000000, 32));
        // C# enum comparisons use signed Int32. Preserve every raw value, including
        // unnamed values and negative values, rather than restricting the port to named members.
        string belowMachine = Or(sign, Eq(privilege, 0), Eq(privilege, 1), Eq(privilege, 2));
        string belowSupervisor = Or(sign, Eq(privilege, 0));

        string readDeny = Op("and", 1, Op("eq", 1, machine, C(0, 1)), Op("and", 1, p0, belowMachine));
        string writeReadOnly = Or(p1, p3);
        string writeMachine = Op("and", 1, p0, belowMachine);
        string writeSupervisor = Op("and", 1, p2, belowSupervisor);
        string writeDeny = Or(writeReadOnly, writeMachine, writeSupervisor);
        string fault = Op("mux", 1, isWrite, writeDeny, readDeny);
        string required = Op("mux", 32, isWrite,
            Op("mux", 32, writeSupervisor, C(1, 32), C(3, 32)), C(3, 32));
        string requiredOnFault = Op("and", 32, required,
            Op("mux", 32, fault, C(uint.MaxValue, 32), C(0, 32)));
        ports.Add(new("fault", HwPortDirection.Output, T(1)));
        nodes.Add(new HwOutputDriveNode("drive_fault", "fault", fault, origin));
        ports.Add(new("required_level", HwPortDirection.Output, T(32)));
        nodes.Add(new HwOutputDriveNode("drive_required_level", "required_level", requiredOnFault, origin));

        const string moduleName = "csr_access_policy";
        var module = new HwModule(moduleName, moduleName, ports, nodes, [],
            new HwHardwareContract("csr.access-policy.source-projection.v1", HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var result = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", moduleName, [module], toolchain, []));
        return result.Validated ?? throw new InvalidDataException("CSR access HWIR refused: " +
            string.Join("; ", result.Diagnostics.Select(x => x.Code + " " + x.Message)));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
