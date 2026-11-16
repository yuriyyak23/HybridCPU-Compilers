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
/// Composes the active CSR_CLEAR decode, micro-op effect, retire-batch application
/// event, and the already source-bound architectural counter transition. The batch
/// application input is a software call-event boundary; it is not a clock signal.
/// </summary>
public static class BridgeHwirCsrClearRetirePath
{
    private const string ActiveCommit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
    private const string GoldenCommit = "6946ccab9d888f7dae0a4d8e18480f436138acd9";
    private const string ActiveDecodeSha = "5ce02ff9939265ddacec5f962eb6fd5490e8558e37716877ecdf1a6eac1d6a36";
    private const string GoldenDecodeSha = "1b195602596485bbb857fc95c9c0e66f052d74c33950b62322b509bd301c865d";
    private const string ActiveOpcodeSha = "4ad1d049514cbb2f95290c7aeb16466d99fec5d7fa3950cf38f8322783185fa6";
    private const string GoldenOpcodeSha = "5495d5da14893e4bb586af29bd9233223646753a406805d1b0a796fe7dceb8cd";
    private const string ActiveClassifierSha = "cde1c9eca1c67be378a8476ab8f83d131a1bf0aea4a672a4a1fc42b3a66d3902";
    private const string GoldenClassifierSha = "d03b5b2d1435940bf2ac1d5768e5529cd5f1c0304f9cc9b8b8d1464ad8439a26";
    private const string ActiveControlSha = "a9f311292cef6cf97ace5516ffa9601bd3e1a272cce56d7cf229dba04b7a9d80";
    private const string GoldenControlSha = "47f53ec0db804c0badf84cb92a152eb8bb9869535c4d2571af7db3a8ad016c0c";
    private const string ActiveRetireSha = "cfdb51be0e465e485ce1c314dcc957e88cd537e3bd5c5fc374eb4a46eda8d184";
    private const string GoldenRetireSha = "c98abf4d8133a38c6270de8f20e595a662d3e71731c6771aea056ea220bb6e81";
    private const string ActiveStateSha = "424c6b27d64b133331c3733b39aea9c7993520f74851c4b369675efe8efb9c5a";
    private const string GoldenStateSha = "1a91fd218dcfb0005562dc1fbcb705f08a941a391bf366da3f3f775cf4dbfa11";

    public static HwValidatedDesign Compile(byte[] activeDecode, byte[] activeOpcode, byte[] activeClassifier, byte[] activeControl,
        byte[] activeRetire, byte[] activeState, byte[] goldenDecode, byte[] goldenOpcode,
        byte[] goldenClassifier, byte[] goldenControl, byte[] goldenRetire, byte[] goldenState,
        HwDesign counterDesign, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(counterDesign);
        ArgumentNullException.ThrowIfNull(toolchain);
        if (Hash(activeDecode) != ActiveDecodeSha || Hash(activeOpcode) != ActiveOpcodeSha ||
            Hash(activeClassifier) != ActiveClassifierSha || Hash(goldenClassifier) != GoldenClassifierSha ||
            Hash(activeControl) != ActiveControlSha || Hash(activeRetire) != ActiveRetireSha ||
            Hash(activeState) != ActiveStateSha || Hash(goldenDecode) != GoldenDecodeSha ||
            Hash(goldenOpcode) != GoldenOpcodeSha || Hash(goldenControl) != GoldenControlSha ||
            Hash(goldenRetire) != GoldenRetireSha || Hash(goldenState) != GoldenStateSha ||
            toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("CSR_CLEAR retire path source bytes or HWIR profile differ from pinned inputs: " +
                string.Join(",", new[] { Hash(activeDecode), Hash(activeOpcode), Hash(activeControl), Hash(activeRetire), Hash(activeState),
                    Hash(goldenDecode), Hash(goldenOpcode), Hash(goldenClassifier), Hash(goldenControl), Hash(goldenRetire), Hash(goldenState), toolchain.HardwareLanguageProfile }));

        static SyntaxNode Parse(byte[] bytes)
        {
            var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("CSR_CLEAR retire path contains C# syntax errors.");
            return tree.GetRoot();
        }
        static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));
        static MethodDeclarationSyntax Method(SyntaxNode root, string name) => root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);
        static HwSourceOrigin Origin(SyntaxNode node, string path, string owner, string member, string identity)
        {
            string syntaxOwner = string.Join(".", node.Ancestors().OfType<TypeDeclarationSyntax>().Reverse()
                .Select(type => type.Identifier.ValueText));
            if (!owner.EndsWith("." + syntaxOwner, StringComparison.Ordinal))
                throw new InvalidDataException($"CSR_CLEAR source origin owner {owner} does not match {syntaxOwner}.");
            FileLinePositionSpan span = node.GetLocation().GetLineSpan();
            return new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", ActiveCommit,
                path, "CircuitWeave.CsrClearRetirePath", owner, member,
                Hash(Encoding.UTF8.GetBytes(identity)), HwSourceRole.Primary,
                span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
        }
        static SwitchSectionSyntax ClearSection(MethodDeclarationSyntax method) => method.DescendantNodes()
            .OfType<SwitchSectionSyntax>().Single(s => s.Labels.Any(l => l.ToString().Contains("CSR_CLEAR", StringComparison.Ordinal)));
        static ClassDeclarationSyntax ClearMicroOp(SyntaxNode root) => root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "CsrClearMicroOp");
        static IfStatementSyntax ClearEffectBranch(MethodDeclarationSyntax method) => method.Body!.DescendantNodes()
            .OfType<IfStatementSyntax>().Single(i => Tokens(i.Condition).EndsWith("ClearsArchitecturalExceptionState", StringComparison.Ordinal));

        SyntaxNode ad = Parse(activeDecode), ao = Parse(activeOpcode), acl = Parse(activeClassifier), ac = Parse(activeControl), ar = Parse(activeRetire), ast = Parse(activeState);
        SyntaxNode gd = Parse(goldenDecode), go = Parse(goldenOpcode), gcl = Parse(goldenClassifier), gc = Parse(goldenControl), gr = Parse(goldenRetire), gst = Parse(goldenState);
        MethodDeclarationSyntax decode = Method(ad, "TryCreatePublishedCsrMicroOp");
        MethodDeclarationSyntax goldenDecodeMethod = Method(gd, "TryCreatePublishedCsrMicroOp");
        SwitchSectionSyntax decodeCase = ClearSection(decode), goldenDecodeCase = ClearSection(goldenDecodeMethod);
        SwitchExpressionArmSyntax classifierArm = acl.DescendantNodes().OfType<SwitchExpressionArmSyntax>()
            .Single(a => Tokens(a).Contains("CSR_CLEAR", StringComparison.Ordinal) &&
                Tokens(a).EndsWith("InstructionClass.Csr", StringComparison.Ordinal));
        SwitchExpressionArmSyntax goldenClassifierArm = gcl.DescendantNodes().OfType<SwitchExpressionArmSyntax>()
            .Single(a => Tokens(a).Contains("CSR_CLEAR", StringComparison.Ordinal) &&
                Tokens(a).EndsWith("InstructionClass.Csr", StringComparison.Ordinal));
        ClassDeclarationSyntax microOp = ClearMicroOp(ac), goldenMicroOp = ClearMicroOp(gc);
        MethodDeclarationSyntax effect = Method(ac, "CreateRetireEffect"), goldenEffect = Method(gc, "CreateRetireEffect");
        IfStatementSyntax effectBranch = ClearEffectBranch(effect), goldenEffectBranch = ClearEffectBranch(goldenEffect);
        if (Tokens(decodeCase) != Tokens(goldenDecodeCase) || Tokens(classifierArm) != Tokens(goldenClassifierArm) ||
            !Tokens(classifierArm).EndsWith("InstructionClass.Csr", StringComparison.Ordinal) ||
            Tokens(microOp) != Tokens(goldenMicroOp) ||
            Tokens(effectBranch) != Tokens(goldenEffectBranch) ||
            !Tokens(decodeCase).Contains("newCsrClearMicroOp", StringComparison.Ordinal) ||
            !Tokens(decodeCase).Contains("OpCode=opcode", StringComparison.Ordinal) ||
            !Tokens(decodeCase).Contains("InitializeMetadata", StringComparison.Ordinal) ||
            !Tokens(microOp).Contains("ReadsCsr=>false", StringComparison.Ordinal) ||
            !Tokens(microOp).Contains("ClearsArchitecturalExceptionState=>true", StringComparison.Ordinal) ||
            !Tokens(effectBranch).Contains("CsrRetireEffect.ClearExceptionCounters()", StringComparison.Ordinal))
            throw new InvalidDataException("Active and pinned Golden CSR_CLEAR decode/effect contracts differ.");

        int opcode = ReadConstant(ao, "CSR_CLEAR");
        if (opcode != 149 || ReadConstant(go, "CSR_CLEAR") != opcode)
            throw new InvalidDataException("Pinned CSR_CLEAR opcode is not 149 in active and Golden generated opcode sources.");

        MethodDeclarationSyntax materialize = Method(ar, "MaterializeLaneCsrEffect");
        MethodDeclarationSyntax goldenMaterialize = Method(gr, "MaterializeLaneCsrEffect");
        MethodDeclarationSyntax materializeCsr = Method(ar, "MaterializeCsrEffectWithStableCoreIdentity");
        MethodDeclarationSyntax goldenMaterializeCsr = Method(gr, "MaterializeCsrEffectWithStableCoreIdentity");
        MethodDeclarationSyntax prevalidate = Method(ar, "PrevalidateCsrEffect");
        MethodDeclarationSyntax goldenPrevalidate = Method(gr, "PrevalidateCsrEffect");
        MethodDeclarationSyntax captureApply = Method(ar, "ApplyCapturedRetireWindowBatch");
        MethodDeclarationSyntax goldenCaptureApply = Method(gr, "ApplyCapturedRetireWindowBatch");
        MethodDeclarationSyntax immediateApply = Method(ar, "ApplyRetireBatchImmediateEffects");
        MethodDeclarationSyntax goldenImmediateApply = Method(gr, "ApplyRetireBatchImmediateEffects");
        MethodDeclarationSyntax applyEffect = Method(ar, "ApplyRetiredCsrEffect");
        MethodDeclarationSyntax goldenApplyEffect = Method(gr, "ApplyRetiredCsrEffect");
        MethodDeclarationSyntax clearState = Method(ast, "ClearExceptionCounters");
        MethodDeclarationSyntax goldenClearState = Method(gst, "ClearExceptionCounters");
        IfStatementSyntax applyClearBranch = ClearEffectBranch(applyEffect);
        IfStatementSyntax goldenApplyClearBranch = goldenApplyEffect.Body!.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => Tokens(i.Condition) == "csrEffect.ClearsArchitecturalExceptionState");
        string captureApplyTokens = Tokens(captureApply), immediateApplyTokens = Tokens(immediateApply);
        string goldenCaptureApplyTokens = Tokens(goldenCaptureApply), goldenImmediateApplyTokens = Tokens(goldenImmediateApply);
        bool[] pathChecks = [
            Tokens(materialize).Contains("MaterializeCsrEffectWithStableCoreIdentity", StringComparison.Ordinal),
            Tokens(materialize).Contains("ClearsArchitecturalExceptionState", StringComparison.Ordinal),
            Tokens(materialize) == Tokens(goldenMaterialize),
            Tokens(materializeCsr).Contains("CreateRetireEffect", StringComparison.Ordinal),
            Tokens(materializeCsr) == Tokens(goldenMaterializeCsr),
            Tokens(prevalidate).Contains("ClearsArchitecturalExceptionState", StringComparison.Ordinal),
            Tokens(captureApply).Contains("PrevalidateRetireWindowBatchForPublication", StringComparison.Ordinal),
            Tokens(captureApply).Contains("ApplyRetireBatchImmediateEffects", StringComparison.Ordinal),
            captureApplyTokens.IndexOf("PrevalidateRetireWindowBatchForPublication", StringComparison.Ordinal) <
                captureApplyTokens.IndexOf("ApplyRetireBatchImmediateEffects", StringComparison.Ordinal),
            Tokens(immediateApply).Contains("RetireCoordinator.Retire", StringComparison.Ordinal),
            Tokens(immediateApply).Contains("ApplyRetiredCsrEffect", StringComparison.Ordinal),
            immediateApplyTokens.IndexOf("RetireCoordinator.Retire", StringComparison.Ordinal) <
                immediateApplyTokens.IndexOf("ApplyRetiredCsrEffect", StringComparison.Ordinal),
            goldenCaptureApplyTokens.IndexOf("PrevalidateRetireWindowBatchForPublication", StringComparison.Ordinal) <
                goldenCaptureApplyTokens.IndexOf("ApplyRetireBatchImmediateEffects", StringComparison.Ordinal),
            goldenImmediateApplyTokens.IndexOf("RetireCoordinator.Retire", StringComparison.Ordinal) <
                goldenImmediateApplyTokens.IndexOf("ApplyRetiredCsrEffect", StringComparison.Ordinal),
            Tokens(applyClearBranch).Contains("ClearExceptionCounters", StringComparison.Ordinal),
            Tokens(goldenApplyClearBranch).Contains("ClearExceptionCounters", StringComparison.Ordinal),
            new[] { "OverflowCount", "UnderflowCount", "DivByZeroCount", "InvalidOpCount", "InexactCount" }
                .All(field => clearState.Body!.ToString().Contains(field + " = 0", StringComparison.Ordinal)),
            Tokens(clearState) == Tokens(goldenClearState)];
        if (pathChecks.Any(passed => !passed))
            throw new InvalidDataException("CSR_CLEAR retire-to-state source path checks failed: " +
                string.Join(",", pathChecks.Select((passed, index) => $"{index}={passed}")));

        string decodePath = "HybridCPU_ISE/CloseToHSL/Core/Diagnostics/InstructionRegistry.Helpers.Csr.cs";
        string controlPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/MicroOps/Control/MicroOp.Control.cs";
        string retirePath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.PipelineExecution.Retire.cs";
        string decodeIdentity = string.Join("|", "csr-clear-decode/v1", ActiveDecodeSha, GoldenDecodeSha,
            ActiveOpcodeSha, GoldenOpcodeSha, ActiveClassifierSha, GoldenClassifierSha);
        string effectIdentity = string.Join("|", "csr-clear-effect/v1", ActiveControlSha, GoldenControlSha);
        string retireIdentity = string.Join("|", "csr-clear-retire-application/v1", ActiveRetireSha, GoldenRetireSha,
            ActiveControlSha, GoldenControlSha, ActiveStateSha, GoldenStateSha);
        HwSourceOrigin decodeOrigin = Origin(decodeCase, decodePath, "YAKSys_Hybrid_CPU.Core.InstructionRegistry",
            "TryCreatePublishedCsrMicroOp.CSR_CLEAR", decodeIdentity);
        HwSourceOrigin effectOrigin = Origin(effectBranch, controlPath, "YAKSys_Hybrid_CPU.Core.CSRMicroOp",
            "CreateRetireEffect", effectIdentity);
        HwSourceOrigin retireOrigin = Origin(captureApply, retirePath, "YAKSys_Hybrid_CPU.Processor.CPU_Core",
            "ApplyCapturedRetireWindowBatch", retireIdentity);

        HwModule decoder = BuildDecoder(decodeOrigin, opcode);
        HwModule microEffect = BuildMicroEffect(effectOrigin);
        HwModule top = BuildTop(retireOrigin, decoder, microEffect,
            counterDesign.Modules.Single(m => m.Id == "retired_csr_exception_counter_transition"));
        HwDesign design = new("hwir/v0.1", top.Id,
            counterDesign.Modules.Concat([decoder, microEffect, top]).ToArray(), toolchain, []);
        HwValidationResult result = new FailClosedHardwareIrValidator().Validate(design);
        return result.Validated ?? throw new InvalidDataException("CSR_CLEAR retire path HWIR refused: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    private static int ReadConstant(SyntaxNode root, string name)
    {
        VariableDeclaratorSyntax field = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == name);
        string text = field.Initializer!.Value.ToString();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt32(text[2..], 16) : int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static HwModule BuildDecoder(HwSourceOrigin origin, int opcode)
    {
        const string id = "csr_clear_instruction_decode";
        HwBitVectorType bit = new(1), opType = new(16);
        HwPortContract[] ports = [new("instruction_valid", HwPortDirection.Input, bit),
            new("canonical_opcode", HwPortDirection.Input, opType), new("csr_clear_decoded", HwPortDirection.Output, bit)];
        HwNode[] nodes = [new HwConstantNode("csr_clear_opcode_const", opType,
                Convert.ToString(opcode, 2).PadLeft(16, '0'), origin),
            new HwOperationNode("opcode_matches_csr_clear", "eq", ["canonical_opcode", "csr_clear_opcode_const"], bit, Origin: origin),
            new HwOperationNode("decoded_valid_csr_clear", "and", ["instruction_valid", "opcode_matches_csr_clear"], bit, Origin: origin),
            new HwOutputDriveNode("drive_csr_clear_decoded", "csr_clear_decoded", "decoded_valid_csr_clear", origin)];
        return new HwModule(id, id, ports, nodes, [], new HwHardwareContract(
            "processor.csr-clear.instruction-decode.source-projection.v1", HwModuleKind.Combinational, ports,
            new HwTimingContract(0, 1)), origin);
    }

    private static HwModule BuildMicroEffect(HwSourceOrigin origin)
    {
        const string id = "csr_clear_microop_retire_effect";
        HwBitVectorType bit = new(1);
        HwPortContract[] ports = [new("microop_present", HwPortDirection.Input, bit),
            new("effect_present", HwPortDirection.Output, bit),
            new("clears_architectural_exception_state", HwPortDirection.Output, bit)];
        HwNode[] nodes = [new HwOutputDriveNode("drive_effect_present", "effect_present", "microop_present", origin),
            new HwOutputDriveNode("drive_clear_flag", "clears_architectural_exception_state", "microop_present", origin)];
        return new HwModule(id, id, ports, nodes, [], new HwHardwareContract(
            "processor.csr-clear.microop-effect.source-projection.v1", HwModuleKind.Combinational, ports,
            new HwTimingContract(0, 1)), origin);
    }

    private static HwModule BuildTop(HwSourceOrigin origin, HwModule decoder, HwModule effect, HwModule transition)
    {
        const string id = "processor_csr_clear_retire_event_projection";
        string[] names = ["overflow_count", "underflow_count", "div_by_zero_count", "invalid_op_count", "inexact_count"];
        HwBitVectorType bit = new(1), word = new(32), opcode = new(16);
        List<HwPortContract> ports = [new("instruction_valid", HwPortDirection.Input, bit),
            new("canonical_opcode", HwPortDirection.Input, opcode),
            new("retire_batch_apply_event", HwPortDirection.Input, bit)];
        List<HwNode> nodes = [];
        foreach (string name in names)
        {
            ports.Add(new("current_" + name, HwPortDirection.Input, word));
            ports.Add(new("next_" + name, HwPortDirection.Output, word));
        }
        nodes.Add(new HwWireNode("decoded_csr_clear", bit, origin));
        nodes.Add(new HwWireNode("effect_present", bit, origin));
        nodes.Add(new HwWireNode("clears_exception_state", bit, origin));
        nodes.Add(new HwOperationNode("effect_applied_in_batch", "and", ["effect_present", "retire_batch_apply_event"], bit, Origin: origin));
        Dictionary<string, string> transitionConnections = new(StringComparer.Ordinal)
        {
            ["effect_present"] = "effect_applied_in_batch",
            ["clears_architectural_exception_state"] = "clears_exception_state"
        };
        foreach (string name in names)
        {
            transitionConnections.Add("current_" + name, "current_" + name);
            nodes.Add(new HwWireNode("transition_next_" + name, word, origin));
            transitionConnections.Add("next_" + name, "transition_next_" + name);
            nodes.Add(new HwOutputDriveNode("drive_next_" + name, "next_" + name, "transition_next_" + name, origin));
        }
        HwInstanceNode transitionInstance = new("retired_counter_transition", transition.Id, transitionConnections,
            new Dictionary<string, string>(StringComparer.Ordinal), Origin: origin);
        HwPortContract[] contractPorts = ports.ToArray();
        return new HwModule(id, id, contractPorts, nodes, [
            new HwInstanceNode("csr_clear_decode", decoder.Id, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instruction_valid"] = "instruction_valid", ["canonical_opcode"] = "canonical_opcode",
                ["csr_clear_decoded"] = "decoded_csr_clear"
            }, new Dictionary<string, string>(StringComparer.Ordinal), Origin: origin),
            new HwInstanceNode("csr_clear_effect", effect.Id, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["microop_present"] = "decoded_csr_clear", ["effect_present"] = "effect_present",
                ["clears_architectural_exception_state"] = "clears_exception_state"
            }, new Dictionary<string, string>(StringComparer.Ordinal), Origin: origin),
            transitionInstance],
            new HwHardwareContract("processor.csr-clear.retire-counter-call-event-projection.v1",
                HwModuleKind.Combinational, contractPorts, new HwTimingContract(0, 1)), origin);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
