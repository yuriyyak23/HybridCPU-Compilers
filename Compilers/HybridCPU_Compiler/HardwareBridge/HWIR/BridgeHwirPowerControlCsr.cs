#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Source-bound pure combinational projection of active PowerControlCsr helpers.</summary>
public static class BridgeHwirPowerControlCsr
{
    public const string HelperSha256 = "87f296e4674d2716cbfe51ff567c4a4b5799732da14ad30f4fdee0f5e1a307c6";
    public const string StateSha256 = "43214e337d4640febb6877460df4478ad74bd7c63dfb71be6f410b1a7e05a54f";
    public const string AddressSha256 = "a2be00ba7d3248bc0aa4e3a49e238a5e0afdd919b5cf1c0d9d2e0bfa01893ef9";

    public static HwValidatedDesign Compile(byte[] helperBytes, byte[] stateBytes, byte[] addressBytes,
        string baseCommit, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(helperBytes); ArgumentNullException.ThrowIfNull(stateBytes); ArgumentNullException.ThrowIfNull(addressBytes);
        if (Hash(helperBytes) != HelperSha256 || Hash(stateBytes) != StateSha256 || Hash(addressBytes) != AddressSha256)
            throw new InvalidDataException("PowerControlCsr source bytes changed.");
        if (baseCommit.Length != 40 || baseCommit.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("Pinned source commit and combinational profile required.");

        SyntaxNode Parse(byte[] bytes)
        {
            var tree = CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) throw new InvalidDataException("Invalid pinned C# syntax.");
            return tree.GetRoot();
        }
        var helperRoot = Parse(helperBytes); var stateRoot = Parse(stateBytes); var addressRoot = Parse(addressBytes);
        var helper = helperRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(x => x.Identifier.ValueText == "PowerControlCsr");
        MethodDeclarationSyntax Method(string name) => helper.Members.OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == name);
        foreach (string name in new[] { "IsPerformanceState", "ResolveTargetCsr", "EncodeState", "DecodePowerState", "DecodePerformanceLevel" }) _ = Method(name);
        var powerEnum = stateRoot.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(x => x.Identifier.ValueText == "CorePowerState");
        var enumValues = powerEnum.Members.ToDictionary(x => x.Identifier.ValueText, x => x.EqualsValue?.Value.ToString(), StringComparer.Ordinal);
        foreach (var pair in new[] { ("C0_Active", "0"), ("C1_Halt", "1"), ("C2_StopClock", "2"), ("C3_Sleep", "3"), ("C6_DeepPowerDown", "6"), ("P0_MaxPerformance", "100"), ("P1_HighPerformance", "101"), ("Pn_MinPerformance", "115"), ("ErrorState", "0xFFFFFFFF") })
            if (!enumValues.TryGetValue(pair.Item1, out string? value) || value != pair.Item2) throw new InvalidDataException("CorePowerState mapping changed: " + pair.Item1);
        var addressFields = addressRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(x => x.Identifier.ValueText == "CsrAddresses").Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables.Select(v => (v.Identifier.ValueText, Value: v.Initializer?.Value.ToString()))).ToDictionary(x => x.ValueText, x => x.Value, StringComparer.Ordinal);
        if (addressFields.GetValueOrDefault("MpowerState") != "0x345" || addressFields.GetValueOrDefault("MperfLevel") != "0x346") throw new InvalidDataException("Power CSR addresses changed.");

        string identity = string.Join("|", "power-control-csr/v1", Hash(helperBytes), Hash(stateBytes), Hash(addressBytes));
        string identityDigest = Hash(Encoding.UTF8.GetBytes(identity));
        var location = Method("ResolveTargetCsr").Identifier.GetLocation().GetLineSpan().StartLinePosition;
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", baseCommit,
            "HybridCPU_ISE/Arch/PowerControlCsr.cs", "CircuitWeave.PowerControlCsr",
            "YAKSys_Hybrid_CPU.Arch.PowerControlCsr", "ResolveTargetCsr", identityDigest,
            HwSourceRole.Primary, location.Line + 1, location.Character + 1);

        var ports = new List<HwPortContract>(); var nodes = new List<HwNode>(); int serial = 0;
        var constants = new Dictionary<(ulong, int), string>(); var operations = new Dictionary<string, string>(StringComparer.Ordinal);
        HwBitVectorType T(int width) => new(width);
        string Input(string name, int width) { ports.Add(new(name, HwPortDirection.Input, T(width))); return name; }
        string C(ulong value, int width)
        {
            if (constants.TryGetValue((value, width), out string? old)) return old;
            string id = "k" + serial++; string bits = Convert.ToString(unchecked((long)value), 2).PadLeft(width, '0')[^width..];
            nodes.Add(new HwConstantNode(id, T(width), bits, origin)); constants.Add((value, width), id); return id;
        }
        string Op(string opcode, int width, params string[] args)
        {
            string key = opcode + "|" + width + "|" + string.Join("|", args);
            if (operations.TryGetValue(key, out string? old)) return old;
            string id = "n" + serial++; nodes.Add(new HwOperationNode(id, opcode, args, T(width), Origin: origin)); operations.Add(key, id); return id;
        }
        string Mux(string condition, string yes, string no, int width)
        {
            if (yes == no) return yes;
            string key = "mux|" + width + "|" + condition + "|" + yes + "|" + no;
            if (operations.TryGetValue(key, out string? old)) return old;
            string id = "n" + serial++; nodes.Add(new HwMuxNode(id, condition, yes, no, T(width), origin)); operations.Add(key, id); return id;
        }
        void Output(string name, string value) { ports.Add(new(name, HwPortDirection.Output, T(name is "performance" ? 1 : name is "target_csr" ? 16 : name is "encoded_state" ? 64 : 32))); nodes.Add(new HwOutputDriveNode("drive_" + name, name, value, origin)); }

        string state = Input("state", 32), raw = Input("raw_value", 64);
        string s64 = Op("zext", 64, state), zero1 = C(0, 1), one1 = C(1, 1), zero64 = C(0, 64);
        string lowBound = Op("ult64", 1, s64, C(100, 64));
        string highBound = Op("ult64", 1, C(115, 64), s64);
        string performance = Op("and", 1, Op("eq", 1, lowBound, zero1), Op("eq", 1, highBound, zero1));
        Output("performance", performance);
        Output("target_csr", Mux(performance, C(0x346, 16), C(0x345, 16), 16));
        Output("encoded_state", s64);
        string decoded = C(uint.MaxValue, 32);
        foreach (uint value in new uint[] { 6, 3, 2, 1, 0 })
            decoded = Mux(Op("eq", 1, raw, C(value, 64)), C(value, 32), decoded, 32);
        Output("decoded_power_state", decoded);
        Output("performance_level", Op("trunc", 32, raw));

        const string moduleName = "power_control_csr";
        var module = new HwModule(moduleName, moduleName, ports, nodes, [],
            new HwHardwareContract("system.power-control-csr.source-projection.v1", HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var validation = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1", moduleName, [module], toolchain, []));
        return validation.Validated ?? throw new InvalidDataException("PowerControlCsr HWIR refused: " + string.Join("; ", validation.Diagnostics.Select(x => x.Code + " " + x.Message)));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
