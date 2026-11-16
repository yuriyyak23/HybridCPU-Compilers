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
/// Source-pinned per-effect projection for the retire path that clears the five
/// VectorExceptionStatus counters. It excludes canonical-core reservation,
/// batch ordering, physical clocking and all other CSR side effects.
/// </summary>
public static class BridgeHwirVectorRetireCounterClear
{
    public const string ActiveRetireSha256 = "cfdb51be0e465e485ce1c314dcc957e88cd537e3bd5c5fc374eb4a46eda8d184";
    public const string GoldenRetireSha256 = "c98abf4d8133a38c6270de8f20e595a662d3e71731c6771aea056ea220bb6e81";
    private const string StateSha256 = "424c6b27d64b133331c3733b39aea9c7993520f74851c4b369675efe8efb9c5a";
    private const string GoldenStateSha256 = "1a91fd218dcfb0005562dc1fbcb705f08a941a391bf366da3f3f775cf4dbfa11";
    private const string ControlSha256 = "a9f311292cef6cf97ace5516ffa9601bd3e1a272cce56d7cf229dba04b7a9d80";
    private const string GoldenControlSha256 = "47f53ec0db804c0badf84cb92a152eb8bb9869535c4d2571af7db3a8ad016c0c";
    private const string ActiveCommit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
    private const string GoldenCommit = "6946ccab9d888f7dae0a4d8e18480f436138acd9";

    public static HwValidatedDesign Compile(byte[] activeState, byte[] activeControl, byte[] activeRetire,
        byte[] goldenState, byte[] goldenControl, byte[] goldenRetire, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(activeState);
        ArgumentNullException.ThrowIfNull(activeControl);
        ArgumentNullException.ThrowIfNull(activeRetire);
        ArgumentNullException.ThrowIfNull(goldenState);
        ArgumentNullException.ThrowIfNull(goldenControl);
        ArgumentNullException.ThrowIfNull(goldenRetire);
        ArgumentNullException.ThrowIfNull(toolchain);
        if (Hash(activeState) != StateSha256 || Hash(activeControl) != ControlSha256 ||
            Hash(activeRetire) != ActiveRetireSha256 || Hash(goldenState) != GoldenStateSha256 ||
            Hash(goldenControl) != GoldenControlSha256 || Hash(goldenRetire) != GoldenRetireSha256 ||
            toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("Vector retire counter clear input bytes or HWIR profile differ from the pinned inputs.");

        static SyntaxNode Parse(byte[] bytes)
        {
            var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Vector retire counter clear source has C# syntax errors.");
            return tree.GetRoot();
        }
        static MethodDeclarationSyntax Method(SyntaxNode root, string name) => root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);
        static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));

        SyntaxNode activeStateRoot = Parse(activeState), activeControlRoot = Parse(activeControl), activeRetireRoot = Parse(activeRetire);
        SyntaxNode goldenStateRoot = Parse(goldenState), goldenControlRoot = Parse(goldenControl), goldenRetireRoot = Parse(goldenRetire);
        MethodDeclarationSyntax clearActive = Method(activeStateRoot, "ClearExceptionCounters");
        MethodDeclarationSyntax clearGolden = Method(goldenStateRoot, "ClearExceptionCounters");
        MethodDeclarationSyntax effectFactoryActive = Method(activeControlRoot, "ClearExceptionCounters");
        MethodDeclarationSyntax effectFactoryGolden = Method(goldenControlRoot, "ClearExceptionCounters");
        MethodDeclarationSyntax applyActive = Method(activeRetireRoot, "ApplyRetiredCsrEffect");
        MethodDeclarationSyntax applyGolden = Method(goldenRetireRoot, "ApplyRetiredCsrEffect");
        if (Tokens(clearActive) != Tokens(clearGolden) ||
            Tokens(effectFactoryActive) != Tokens(effectFactoryGolden) ||
            clearActive.Body is null || !new[] { "OverflowCount", "UnderflowCount", "DivByZeroCount", "InvalidOpCount", "InexactCount" }
                .All(field => clearActive.Body.ToString().Contains(field + " = 0", StringComparison.Ordinal)) ||
            !ClearBranchMatches(applyActive, "Runtime.Architectural.VectorExceptionStatus.ClearExceptionCounters()") ||
            !ClearBranchMatches(applyGolden, "ExceptionStatus.ClearExceptionCounters()"))
            throw new InvalidDataException("Active and pinned Golden retire counter-clear source contract differs.");

        string statePath = "HybridCPU_ISE/CloseToHSL/Core/Architecture/State/Architectural/CPU_Core.State.cs";
        string retirePath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.PipelineExecution.Retire.cs";
        HwSourceOrigin stateOrigin = Origin(clearActive, statePath,
            "YAKSys_Hybrid_CPU.Processor.CPU_Core.VectorExceptionStatus", "ClearExceptionCounters",
            "vector-retire-clear-action/v1|" + StateSha256 + "|" + GoldenStateSha256);
        HwSourceOrigin retireOrigin = Origin(applyActive, retirePath,
            "YAKSys_Hybrid_CPU.Processor.CPU_Core", "ApplyRetiredCsrEffect",
            "vector-retire-clear-transition/v1|" + ActiveRetireSha256 + "|" + GoldenRetireSha256 + "|" +
            ControlSha256 + "|" + GoldenControlSha256 + "|" + StateSha256 + "|" + GoldenStateSha256);

        HwModule action = BuildClearAction(stateOrigin);
        HwModule transition = BuildRetireTransition(retireOrigin, action);
        HwDesign design = new HwDesign("hwir/v0.1", transition.Id, [transition, action], toolchain, []);
        HwValidationResult validation = new FailClosedHardwareIrValidator().Validate(design);
        return validation.Validated ?? throw new InvalidDataException("Retire counter clear HWIR refused: " +
            string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    private static bool ClearBranchMatches(MethodDeclarationSyntax method, string expectedCall)
    {
        if (method.Body is null) return false;
        IfStatementSyntax? clear = method.Body.DescendantNodes().OfType<IfStatementSyntax>()
            .FirstOrDefault(i => Tokens(i.Condition) == "csrEffect.ClearsArchitecturalExceptionState");
        return clear is not null && Tokens(clear.Condition) == "csrEffect.ClearsArchitecturalExceptionState" &&
            clear.Statement.ToString().Contains(expectedCall, StringComparison.Ordinal) &&
            clear.Statement.ToString().Contains("ClearExceptionCounters", StringComparison.Ordinal);
    }

    private static HwSourceOrigin Origin(MethodDeclarationSyntax method, string path, string owner, string member, string identity)
    {
        string syntaxOwner = string.Join(".", method.Ancestors().OfType<TypeDeclarationSyntax>().Reverse()
            .Select(type => type.Identifier.ValueText));
        if (!owner.EndsWith("." + syntaxOwner, StringComparison.Ordinal))
            throw new InvalidDataException($"Retire source origin owner {owner} does not match syntax owner {syntaxOwner}.");
        var span = method.GetLocation().GetLineSpan();
        return new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", ActiveCommit, path,
            "CircuitWeave.VectorRetireCounterClear", owner, member,
            Hash(Encoding.UTF8.GetBytes(identity)), HwSourceRole.Primary,
            span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    private static HwModule BuildClearAction(HwSourceOrigin origin)
    {
        const string id = "vector_exception_counter_clear_action";
        string[] names = ["overflow_count", "underflow_count", "div_by_zero_count", "invalid_op_count", "inexact_count"];
        HwBitVectorType type = new(32);
        List<HwPortContract> ports = [];
        List<HwNode> nodes = [];
        foreach (string name in names)
        {
            ports.Add(new("zero_" + name, HwPortDirection.Output, type));
            nodes.Add(new HwConstantNode("zero_" + name + "_const", type, new string('0', 32), origin));
            nodes.Add(new HwOutputDriveNode("drive_zero_" + name, "zero_" + name,
                "zero_" + name + "_const", origin));
        }
        HwPortContract[] contractPorts = ports.ToArray();
        return new HwModule(id, id, contractPorts, nodes, [],
            new HwHardwareContract("vector.exception-counter.clear-action.source-projection.v1",
                HwModuleKind.Combinational, contractPorts, new HwTimingContract(0, 1)), origin);
    }

    private static HwModule BuildRetireTransition(HwSourceOrigin origin, HwModule action)
    {
        const string id = "retired_csr_exception_counter_transition";
        string[] names = ["overflow_count", "underflow_count", "div_by_zero_count", "invalid_op_count", "inexact_count"];
        List<HwPortContract> ports = [];
        List<HwNode> nodes = [];
        HwBitVectorType bit = new(1), word = new(32);
        string Input(string name, HwBitVectorType type) { ports.Add(new(name, HwPortDirection.Input, type)); return name; }
        string Output(string name, string signal) { ports.Add(new(name, HwPortDirection.Output, word)); nodes.Add(new HwOutputDriveNode("drive_" + name, name, signal, origin)); return name; }
        string present = Input("effect_present", bit);
        string clears = Input("clears_architectural_exception_state", bit);
        string condition = "effect_clear_applied";
        nodes.Add(new HwOperationNode(condition, "and", [present, clears], bit, Origin: origin));
        Dictionary<string, string> connections = new(StringComparer.Ordinal);
        Dictionary<string, string> instanceWires = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            Input("current_" + name, word);
            string output = "zero_" + name;
            string wire = "clear_action_" + name;
            nodes.Add(new HwWireNode(wire, word, origin));
            connections.Add(output, wire);
            instanceWires.Add(name, wire);
        }
        foreach (string name in names)
        {
            string mux = "next_" + name + "_mux";
            nodes.Add(new HwMuxNode(mux, condition, instanceWires[name], "current_" + name, word, origin));
            Output("next_" + name, mux);
        }
        HwPortContract[] contractPorts = ports.ToArray();
        return new HwModule(id, id, contractPorts, nodes, [new HwInstanceNode("clear_action", action.Id,
                connections, new Dictionary<string, string>(), Origin: origin)],
            new HwHardwareContract("vector.retired-csr.exception-counter.next-state-projection.v1",
                HwModuleKind.Combinational, contractPorts, new HwTimingContract(0, 1)), origin);
    }

    private static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
