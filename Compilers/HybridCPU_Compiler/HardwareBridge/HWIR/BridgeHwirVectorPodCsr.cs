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
/// Source-pinned combinational read and write-next-state projections for the
/// VexcpMask/VexcpPri vector-pod CSR plane. Read and write are separate modules
/// because HWIR requires one source origin per module. No physical state, clock,
/// reset, arbitration, or reservation behavior is modeled.
/// </summary>
public static class BridgeHwirVectorPodCsr
{
    public const string ActiveBaseCommit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
    public const string GoldenCommit = "6946ccab9d888f7dae0a4d8e18480f436138acd9";
    public const string VectorConfigSha256 = "7106965a6adbd61eed397ef3501610b0f5e8cfc9af5d312409c05fe09b777c92";
    public const string MicroOpControlSha256 = "a9f311292cef6cf97ace5516ffa9601bd3e1a272cce56d7cf229dba04b7a9d80";
    public const string ArchitecturalStateSha256 = "424c6b27d64b133331c3733b39aea9c7993520f74851c4b369675efe8efb9c5a";
    public const string CsrAddressesSha256 = "a2be00ba7d3248bc0aa4e3a49e238a5e0afdd919b5cf1c0d9d2e0bfa01893ef9";
    public const string GoldenVectorConfigSha256 = "d0410f900ba0dfd29d69a59e4edd9663b595b26ab434f75c8c4f8b44a9b7062c";
    public const string GoldenMicroOpControlSha256 = "47f53ec0db804c0badf84cb92a152eb8bb9869535c4d2571af7db3a8ad016c0c";
    public const string GoldenArchitecturalStateSha256 = "1a91fd218dcfb0005562dc1fbcb705f08a941a391bf366da3f3f775cf4dbfa11";

    public static HwValidatedDesign Compile(byte[] vectorConfigBytes, byte[] microOpControlBytes,
        byte[] architecturalStateBytes, byte[] csrAddressesBytes, byte[] goldenVectorConfigBytes,
        byte[] goldenMicroOpControlBytes, byte[] goldenArchitecturalStateBytes,
        string activeBaseCommit, string goldenCommit, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(vectorConfigBytes);
        ArgumentNullException.ThrowIfNull(microOpControlBytes);
        ArgumentNullException.ThrowIfNull(architecturalStateBytes);
        ArgumentNullException.ThrowIfNull(csrAddressesBytes);
        ArgumentNullException.ThrowIfNull(goldenVectorConfigBytes);
        ArgumentNullException.ThrowIfNull(goldenMicroOpControlBytes);
        ArgumentNullException.ThrowIfNull(goldenArchitecturalStateBytes);
        ArgumentNullException.ThrowIfNull(toolchain);
        if (Hash(vectorConfigBytes) != VectorConfigSha256 ||
            Hash(microOpControlBytes) != MicroOpControlSha256 ||
            Hash(architecturalStateBytes) != ArchitecturalStateSha256 ||
            Hash(csrAddressesBytes) != CsrAddressesSha256 ||
            Hash(goldenVectorConfigBytes) != GoldenVectorConfigSha256 ||
            Hash(goldenMicroOpControlBytes) != GoldenMicroOpControlSha256 ||
            Hash(goldenArchitecturalStateBytes) != GoldenArchitecturalStateSha256)
            throw new InvalidDataException("Vector-pod CSR source bytes changed.");
        if (activeBaseCommit != ActiveBaseCommit || goldenCommit != GoldenCommit ||
            toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("Pinned active base, Golden revision, and combinational HWIR profile are required.");

        static SyntaxNode Parse(byte[] bytes)
        {
            var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Vector-pod CSR source has C# syntax errors.");
            return tree.GetRoot();
        }
        static MethodDeclarationSyntax SingleMethod(SyntaxNode root, string name) => root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);
        static string Tokens(MethodDeclarationSyntax method) => string.Concat(method.DescendantTokens().Select(t => t.Text));

        SyntaxNode vectorRoot = Parse(vectorConfigBytes);
        SyntaxNode controlRoot = Parse(microOpControlBytes);
        SyntaxNode stateRoot = Parse(architecturalStateBytes);
        SyntaxNode addressRoot = Parse(csrAddressesBytes);
        SyntaxNode goldenVectorRoot = Parse(goldenVectorConfigBytes);
        SyntaxNode goldenControlRoot = Parse(goldenMicroOpControlBytes);
        SyntaxNode goldenStateRoot = Parse(goldenArchitecturalStateBytes);

        MethodDeclarationSyntax writePlane = SingleMethod(vectorRoot, "WriteCanonicalVectorPodPlaneCsr");
        MethodDeclarationSyntax writePack = SingleMethod(vectorRoot, "WritePackedExceptionPriorities");
        MethodDeclarationSyntax readPlane = SingleMethod(controlRoot, "ReadVectorPodPlaneCsr");
        MethodDeclarationSyntax readPack = SingleMethod(controlRoot, "ReadPackedExceptionPriorities");
        MethodDeclarationSyntax setMask = SingleMethod(stateRoot, "SetMask");
        MethodDeclarationSyntax setPriority = SingleMethod(stateRoot, "SetPriority");
        MethodDeclarationSyntax getPriority = SingleMethod(stateRoot, "GetPriority");
        MethodDeclarationSyntax goldenWritePlane = SingleMethod(goldenControlRoot, "WriteVectorPodPlaneCsr");
        MethodDeclarationSyntax goldenWritePack = SingleMethod(goldenControlRoot, "WritePackedExceptionPriorities");
        MethodDeclarationSyntax goldenReadPlane = SingleMethod(goldenControlRoot, "ReadVectorPodPlaneCsr");
        MethodDeclarationSyntax goldenReadPack = SingleMethod(goldenControlRoot, "ReadPackedExceptionPriorities");
        MethodDeclarationSyntax goldenSetMask = SingleMethod(goldenStateRoot, "SetMask");
        MethodDeclarationSyntax goldenSetPriority = SingleMethod(goldenStateRoot, "SetPriority");
        MethodDeclarationSyntax goldenGetPriority = SingleMethod(goldenStateRoot, "GetPriority");

        if (Tokens(readPlane) != Tokens(goldenReadPlane) || Tokens(readPack) != Tokens(goldenReadPack) ||
            Tokens(setMask) != Tokens(goldenSetMask) || Tokens(setPriority) != Tokens(goldenSetPriority) ||
            Tokens(getPriority) != Tokens(goldenGetPriority) ||
            writePlane.Body is null || !writePlane.Body.ToString().Contains("CsrAddresses.VexcpMask", StringComparison.Ordinal) ||
            !writePlane.Body.ToString().Contains("SetMask((byte)value)", StringComparison.Ordinal) ||
            !writePlane.Body.ToString().Contains("CsrAddresses.VexcpPri", StringComparison.Ordinal) ||
            !writePlane.Body.ToString().Contains("WritePackedExceptionPriorities(value)", StringComparison.Ordinal) ||
            writePack.Body is null || !writePack.Body.ToString().Contains("i < 5", StringComparison.Ordinal) ||
            !writePack.Body.ToString().Contains("(rs1Value >> (i * 3)) & 0x7", StringComparison.Ordinal) ||
            !writePack.Body.ToString().Contains("status.SetPriority(i, pri)", StringComparison.Ordinal) ||
            goldenWritePlane.Body is null || !goldenWritePlane.Body.ToString().Contains("CsrAddresses.VexcpMask", StringComparison.Ordinal) ||
            !goldenWritePlane.Body.ToString().Contains("ExceptionStatus.SetMask((byte)value)", StringComparison.Ordinal) ||
            !goldenWritePlane.Body.ToString().Contains("CsrAddresses.VexcpPri", StringComparison.Ordinal) ||
            !goldenWritePlane.Body.ToString().Contains("WritePackedExceptionPriorities(ref core, value)", StringComparison.Ordinal) ||
            goldenWritePack.Body is null || !goldenWritePack.Body.ToString().Contains("i < 5", StringComparison.Ordinal) ||
            !goldenWritePack.Body.ToString().Contains("(value >> (i * 3)) & 0x7", StringComparison.Ordinal) ||
            !goldenWritePack.Body.ToString().Contains("SetPriority(i", StringComparison.Ordinal) ||
            readPlane.Body is null || !readPlane.Body.ToString().Contains("CsrAddresses.VexcpMask => core.ExceptionStatus.GetMask()", StringComparison.Ordinal) ||
            !readPlane.Body.ToString().Contains("CsrAddresses.VexcpPri => ReadPackedExceptionPriorities(ref core)", StringComparison.Ordinal) ||
            readPack.Body is null || !readPack.Body.ToString().Contains("i < 5", StringComparison.Ordinal) ||
            !readPack.Body.ToString().Contains("(core.ExceptionStatus.GetPriority(i) & 0x7) << (i * 3)", StringComparison.Ordinal) ||
            setMask.ExpressionBody?.Expression.ToString() != "_exceptionMask = (byte)(mask & 0x1F)" ||
            setPriority.Body is null || !setPriority.Body.ToString().Contains("priority &= 0x7", StringComparison.Ordinal) ||
            !setPriority.Body.ToString().Contains("_exceptionPriorityPacked &= (ushort)~(0x7 << shift)", StringComparison.Ordinal) ||
            !setPriority.Body.ToString().Contains("_exceptionPriorityPacked |= (ushort)(priority << shift)", StringComparison.Ordinal) ||
            getPriority.Body is null || !getPriority.Body.ToString().Contains("(_exceptionPriorityPacked >> shift) & 0x7", StringComparison.Ordinal))
            throw new InvalidDataException("VexcpMask/VexcpPri source behavior changed.");

        ClassDeclarationSyntax addresses = addressRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "CsrAddresses");
        int Address(string name)
        {
            VariableDeclaratorSyntax field = addresses.Members.OfType<FieldDeclarationSyntax>()
                .SelectMany(f => f.Declaration.Variables).Single(v => v.Identifier.ValueText == name);
            return Convert.ToInt32(field.Initializer!.Value.ToString(), 16);
        }
        if (Address("VexcpMask") != 0x900 || Address("VexcpPri") != 0x901)
            throw new InvalidDataException("Vector exception CSR addresses changed.");

        HwSourceOrigin writeOrigin = Origin(writePack, "HybridCPU_ISE/CloseToHSL/Core/Architecture/State/Architectural/CPU_Core.VectorConfig.cs",
            "YAKSys_Hybrid_CPU.Processor.CPU_Core", "WritePackedExceptionPriorities", string.Join("|", "vector-pod-csr-write/v1", ActiveBaseCommit,
                GoldenCommit, VectorConfigSha256, ArchitecturalStateSha256, CsrAddressesSha256,
                GoldenMicroOpControlSha256, GoldenArchitecturalStateSha256));
        HwSourceOrigin readOrigin = Origin(readPack, "HybridCPU_ISE/CloseToHSL/Core/Pipeline/MicroOps/Control/MicroOp.Control.cs",
            "YAKSys_Hybrid_CPU.Core.CSRMicroOp", "ReadPackedExceptionPriorities", string.Join("|", "vector-pod-csr-read/v1", ActiveBaseCommit,
                GoldenCommit, MicroOpControlSha256, ArchitecturalStateSha256, CsrAddressesSha256,
                GoldenMicroOpControlSha256, GoldenArchitecturalStateSha256));

        HwModule writeModule = BuildWriteProjection(writeOrigin);
        HwModule readModule = BuildReadProjection(readOrigin);
        const string topModule = "vector_pod_csr_read_projection";
        var design = new HwDesign("hwir/v0.1", topModule, [readModule, writeModule], toolchain, []);
        HwValidationResult validation = new FailClosedHardwareIrValidator().Validate(design);
        return validation.Validated ?? throw new InvalidDataException("Vector Pod CSR HWIR refused: " +
            string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    private static HwSourceOrigin Origin(MethodDeclarationSyntax method, string path, string containingType, string member, string identity)
    {
        string syntaxOwner = string.Join(".", method.Ancestors().OfType<TypeDeclarationSyntax>().Reverse()
            .Select(type => type.Identifier.ValueText));
        if (!containingType.EndsWith("." + syntaxOwner, StringComparison.Ordinal))
            throw new InvalidDataException($"Source origin owner {containingType} does not match syntax owner {syntaxOwner}.");
        FileLinePositionSpan span = method.GetLocation().GetLineSpan();
        return new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", ActiveBaseCommit,
            path, "CircuitWeave.VectorPodCsr", containingType, member,
            Hash(Encoding.UTF8.GetBytes(identity)), HwSourceRole.Primary,
            span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    private static HwModule BuildWriteProjection(HwSourceOrigin origin)
    {
        const string name = "vector_pod_csr_write_projection";
        var ports = new List<HwPortContract>();
        var nodes = new List<HwNode>();
        var constants = new Dictionary<(ulong Value, int Width), string>();
        var operations = new Dictionary<string, string>(StringComparer.Ordinal);
        int serial = 0;
        HwBitVectorType Type(int width) => new(width);
        string Input(string id, int width) { ports.Add(new(id, HwPortDirection.Input, Type(width))); return id; }
        string Constant(ulong value, int width)
        {
            if (constants.TryGetValue((value, width), out string? old)) return old;
            string id = "k" + serial++;
            string bits = Convert.ToString(unchecked((long)value), 2).PadLeft(width, '0')[^width..];
            nodes.Add(new HwConstantNode(id, Type(width), bits, origin)); constants.Add((value, width), id); return id;
        }
        string Op(string opcode, int width, params string[] args)
        {
            string key = opcode + "|" + width + "|" + string.Join("|", args);
            if (operations.TryGetValue(key, out string? old)) return old;
            string id = "n" + serial++;
            nodes.Add(new HwOperationNode(id, opcode, args, Type(width), Origin: origin)); operations.Add(key, id); return id;
        }
        string Mux(string condition, string yes, string no, int width)
        {
            if (yes == no) return yes;
            string key = "mux|" + width + "|" + condition + "|" + yes + "|" + no;
            if (operations.TryGetValue(key, out string? old)) return old;
            string id = "n" + serial++;
            nodes.Add(new HwMuxNode(id, condition, yes, no, Type(width), origin)); operations.Add(key, id); return id;
        }
        void Output(string id, string signal, int width)
        {
            ports.Add(new(id, HwPortDirection.Output, Type(width)));
            nodes.Add(new HwOutputDriveNode("drive_" + id, id, signal, origin));
        }

        string address = Input("csr_addr", 12);
        string writeEnable = Input("write_enable", 1);
        string writeValue = Input("write_value", 64);
        string stateMask = Input("state_mask", 8);
        string statePriority = Input("state_priority", 16);
        string isMask = Op("eq", 1, address, Constant(0x900, 12));
        string isPriority = Op("eq", 1, address, Constant(0x901, 12));
        string writeMask = Op("and", 1, writeEnable, isMask);
        string writePriority = Op("and", 1, writeEnable, isPriority);
        string writeValid = Op("or", 1, isMask, isPriority);

        string byteValue = Op("trunc", 8, writeValue);
        string maskedValue = Op("and", 8, byteValue, Constant(0x1F, 8));
        string nextMask = Mux(writeMask, maskedValue, stateMask, 8);
        string priorityLow15 = Op("and", 64, writeValue, Constant(0x7FFF, 64));
        string priorityLow16 = Op("trunc", 16, priorityLow15);
        string priorityReservedBit = Op("and", 16, statePriority, Constant(0x8000, 16));
        string priorityUpdated = Op("or", 16, priorityReservedBit, priorityLow16);
        string nextPriority = Mux(writePriority, priorityUpdated, statePriority, 16);
        Output("write_address_valid", writeValid, 1);
        Output("next_mask", nextMask, 8);
        Output("next_priority", nextPriority, 16);
        var modulePorts = ports.ToArray();
        return new HwModule(name, name, modulePorts, nodes, [],
            new HwHardwareContract("vector.pod-csr.write-next-state.source-projection.v1",
                HwModuleKind.Combinational, modulePorts, new HwTimingContract(0, 1)), origin);
    }

    private static HwModule BuildReadProjection(HwSourceOrigin origin)
    {
        const string name = "vector_pod_csr_read_projection";
        var ports = new List<HwPortContract>();
        var nodes = new List<HwNode>();
        var constants = new Dictionary<(ulong Value, int Width), string>();
        var operations = new Dictionary<string, string>(StringComparer.Ordinal);
        int serial = 0;
        HwBitVectorType Type(int width) => new(width);
        string Input(string id, int width) { ports.Add(new(id, HwPortDirection.Input, Type(width))); return id; }
        string Constant(ulong value, int width)
        {
            if (constants.TryGetValue((value, width), out string? old)) return old;
            string id = "k" + serial++;
            string bits = Convert.ToString(unchecked((long)value), 2).PadLeft(width, '0')[^width..];
            nodes.Add(new HwConstantNode(id, Type(width), bits, origin)); constants.Add((value, width), id); return id;
        }
        string Op(string opcode, int width, params string[] args)
        {
            string key = opcode + "|" + width + "|" + string.Join("|", args);
            if (operations.TryGetValue(key, out string? old)) return old;
            string id = "n" + serial++;
            nodes.Add(new HwOperationNode(id, opcode, args, Type(width), Origin: origin)); operations.Add(key, id); return id;
        }
        string Mux(string condition, string yes, string no, int width)
        {
            if (yes == no) return yes;
            string key = "mux|" + width + "|" + condition + "|" + yes + "|" + no;
            if (operations.TryGetValue(key, out string? old)) return old;
            string id = "n" + serial++;
            nodes.Add(new HwMuxNode(id, condition, yes, no, Type(width), origin)); operations.Add(key, id); return id;
        }
        void Output(string id, string signal, int width)
        {
            ports.Add(new(id, HwPortDirection.Output, Type(width)));
            nodes.Add(new HwOutputDriveNode("drive_" + id, id, signal, origin));
        }

        string address = Input("csr_addr", 12);
        string stateMask = Input("state_mask", 8);
        string statePriority = Input("state_priority", 16);
        string isMask = Op("eq", 1, address, Constant(0x900, 12));
        string isPriority = Op("eq", 1, address, Constant(0x901, 12));
        string readValid = Op("or", 1, isMask, isPriority);
        string maskRead = Op("zext", 64, stateMask);
        string priorityRead16 = Op("and", 16, statePriority, Constant(0x7FFF, 16));
        string priorityRead = Op("zext", 64, priorityRead16);
        string readWhenException = Mux(isMask, maskRead, priorityRead, 64);
        string readData = Mux(readValid, readWhenException, Constant(0, 64), 64);
        Output("read_address_valid", readValid, 1);
        Output("read_data", readData, 64);
        var modulePorts = ports.ToArray();
        return new HwModule(name, name, modulePorts, nodes, [],
            new HwHardwareContract("vector.pod-csr.read-value.source-projection.v1",
                HwModuleKind.Combinational, modulePorts, new HwTimingContract(0, 1)), origin);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
