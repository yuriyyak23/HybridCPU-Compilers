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
/// Source-bounded projection of BundleLegalityAnalyzer.HasCoarseMemoryConflict.
/// This models the conservative pair classifier only. It does not model Store
/// address generation, memory writes, arbitration, or paired admission.
/// </summary>
public static class BridgeHwirCoarseMemoryConflict
{
    public const string SourceRelativePath = "HybridCPU_ISE/CloseToHSL/Core/Legality/BundleLegalityAnalyzer.cs";
    public const string SourceSha256 = "24b1170ccf39102cd7eaa288c33fbbeb55669fd5d09bf2c681b0020467125514";

    public static HwValidatedDesign Compile(byte[] sourceBytes, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        ArgumentNullException.ThrowIfNull(toolchain);
        if (Hash(sourceBytes) != SourceSha256 || toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("Coarse memory conflict source bytes or HWIR profile differ from the pinned active inputs.");

        var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(sourceBytes).TrimStart('\uFEFF'));
        if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException("BundleLegalityAnalyzer source contains C# syntax errors.");
        SyntaxNode root = tree.GetRoot();
        MethodDeclarationSyntax method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "HasCoarseMemoryConflict");
        string tokens = string.Concat(method.DescendantTokens().Select(token => token.Text));
        foreach (string fact in new[]
        {
            "if(!left.IsMemoryOp||!right.IsMemoryOp)returnfalse;",
            "if(left.MemoryBankIntent<0||right.MemoryBankIntent<0)returntrue;",
            "returnleft.MemoryBankIntent==right.MemoryBankIntent;"
        })
            if (!tokens.Contains(fact, StringComparison.Ordinal))
                throw new InvalidDataException("Coarse memory conflict source contract changed: " + fact);

        string GitBlobId()
        {
            byte[] header = Encoding.ASCII.GetBytes($"blob {sourceBytes.Length}\0");
            byte[] body = new byte[header.Length + sourceBytes.Length];
            Buffer.BlockCopy(header, 0, body, 0, header.Length);
            Buffer.BlockCopy(sourceBytes, 0, body, header.Length, sourceBytes.Length);
            return Convert.ToHexString(SHA1.HashData(body)).ToLowerInvariant();
        }

        var line = method.GetLocation().GetLineSpan().StartLinePosition;
        string identity = Hash(Encoding.UTF8.GetBytes("HasCoarseMemoryConflict|" + SourceSha256 + "|" + tokens));
        var origin = new HwSourceOrigin(
            "urn:circuitweave:dirty-source-capture:sha256:" + SourceSha256,
            GitBlobId(), SourceRelativePath, "CircuitWeave.CoarseMemoryConflict",
            "YAKSys_Hybrid_CPU.Core.Legality.BundleLegalityAnalyzer", "HasCoarseMemoryConflict",
            identity, HwSourceRole.Primary, line.Line + 1, line.Character + 1);

        var bit = new HwBitVectorType(1);
        var signed64 = new HwBitVectorType(64, HwSignedness.Signed);
        var ports = new List<HwPortContract>
        {
            new("left_is_memory", HwPortDirection.Input, bit),
            new("right_is_memory", HwPortDirection.Input, bit),
            new("left_bank_intent_sext64", HwPortDirection.Input, signed64),
            new("right_bank_intent_sext64", HwPortDirection.Input, signed64),
            new("memory_pair_conflict", HwPortDirection.Output, bit)
        };
        var zero = new HwConstantNode("zero_s64", signed64, new string('0', 64), origin);
        var one = new HwConstantNode("one_bit", bit, "1", origin);
        var nodes = new List<HwNode>
        {
            zero,
            one,
            new HwOperationNode("left_intent_unknown", "slt64", ["left_bank_intent_sext64", "zero_s64"], bit, Origin: origin),
            new HwOperationNode("right_intent_unknown", "slt64", ["right_bank_intent_sext64", "zero_s64"], bit, Origin: origin),
            new HwOperationNode("left_intent_known", "xor", ["left_intent_unknown", "one_bit"], bit, Origin: origin),
            new HwOperationNode("right_intent_known", "xor", ["right_intent_unknown", "one_bit"], bit, Origin: origin),
            new HwOperationNode("both_intents_known", "and", ["left_intent_known", "right_intent_known"], bit, Origin: origin),
            new HwOperationNode("bank_intents_equal", "eq", ["left_bank_intent_sext64", "right_bank_intent_sext64"], bit, Origin: origin),
            new HwOperationNode("bank_intents_differ", "xor", ["bank_intents_equal", "one_bit"], bit, Origin: origin),
            new HwOperationNode("known_banks_differ", "and", ["both_intents_known", "bank_intents_differ"], bit, Origin: origin),
            new HwOperationNode("unknown_or_equal_bank", "xor", ["known_banks_differ", "one_bit"], bit, Origin: origin),
            new HwOperationNode("both_memory", "and", ["left_is_memory", "right_is_memory"], bit, Origin: origin),
            new HwOperationNode("conflict", "and", ["both_memory", "unknown_or_equal_bank"], bit, Origin: origin),
            new HwOutputDriveNode("drive_memory_pair_conflict", "memory_pair_conflict", "conflict", origin)
        };
        var module = new HwModule("coarse_memory_conflict_pair", "coarse_memory_conflict_pair", ports, nodes, [],
            new HwHardwareContract("bundle-legality.coarse-memory-pair-conflict.v1",
                HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var design = new HwDesign("hwir/v0.1", module.Id, [module], toolchain, []);
        var validation = new FailClosedHardwareIrValidator().Validate(design);
        return validation.Validated ?? throw new InvalidDataException(
            "Coarse memory conflict HWIR refused: " + string.Join("; ", validation.Diagnostics.Select(d => d.Message)));
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
