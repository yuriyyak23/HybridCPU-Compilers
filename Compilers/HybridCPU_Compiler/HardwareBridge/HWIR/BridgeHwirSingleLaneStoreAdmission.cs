#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>
/// Source-bounded projection of the single-lane scalar-store admission result.
/// It models only size/data-length rejection and the fixed outstanding-capacity
/// decision. It does not model request identity allocation, queue contents,
/// cancellation state, store service/completion, or a physical RAM interface.
/// </summary>
public static class BridgeHwirSingleLaneStoreAdmission
{
    public const string SourceRelativePath = "HybridCPU_ISE/CloseToHSL/Memory/Timing/MemoryCycleController.cs";
    public const string SourceSha256 = "6d7a0d7b72f2594701dd49bedb6a5a6d0eb500ea19091d1024c1d98cb556d551";

    public static HwValidatedDesign Compile(byte[] sourceBytes, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        ArgumentNullException.ThrowIfNull(toolchain);
        if (Hash(sourceBytes) != SourceSha256 || toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("Single-lane store admission source bytes or HWIR profile differ from pinned active inputs.");

        var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(sourceBytes).TrimStart('\uFEFF'));
        if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException("MemoryCycleController source contains C# syntax errors.");
        MethodDeclarationSyntax method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "TryAcceptScalarStore" && node.ParameterList.Parameters.Count == 6);
        string tokens = string.Concat(method.DescendantTokens().Select(token => token.Text));
        string[] facts =
        [
            "if(sizeisnot(1or2or4or8))",
            "if(data.Length<size)",
            "requestClass==ScalarStoreRequestClass.ExplicitPacket?_outstandingExplicitPacketScalarStores:_outstandingSingleLaneScalarStores",
            "requestClass==ScalarStoreRequestClass.ExplicitPacket?ExplicitPacketScalarStoreCapacity:SingleLaneScalarStoreCapacity",
            "if(outstandingForClass>=capacity)",
            "returnMemoryAdmissionResult.Backpressured();",
            "MemoryRequestIdrequestId=AllocateRequestId();",
            "_scalarStoreQueue.Enqueue(requestId);"
        ];
        int last = -1;
        foreach (string fact in facts)
        {
            int next = tokens.IndexOf(fact, StringComparison.Ordinal);
            if (next <= last)
                throw new InvalidDataException("Single-lane store admission source order/contract changed: " + fact);
            last = next;
        }

        string blobId()
        {
            byte[] header = Encoding.ASCII.GetBytes($"blob {sourceBytes.Length}\0");
            byte[] body = new byte[header.Length + sourceBytes.Length];
            Buffer.BlockCopy(header, 0, body, 0, header.Length);
            Buffer.BlockCopy(sourceBytes, 0, body, header.Length, sourceBytes.Length);
            return Convert.ToHexString(SHA1.HashData(body)).ToLowerInvariant();
        }

        var start = method.GetLocation().GetLineSpan().StartLinePosition;
        string identity = Hash(Encoding.UTF8.GetBytes("TryAcceptScalarStore|" + SourceSha256 + "|" + tokens));
        var origin = new HwSourceOrigin(
            "urn:circuitweave:dirty-source-capture:sha256:" + SourceSha256,
            blobId(), SourceRelativePath, "CircuitWeave.SingleLaneStoreAdmission",
            "HybridCPU_ISE.CloseToHSL.Memory.Timing.MemoryCycleController", "TryAcceptScalarStore",
            identity, HwSourceRole.Primary, start.Line + 1, start.Character + 1);

        var bit = new HwBitVectorType(1);
        var u64 = new HwBitVectorType(64);
        var ports = new List<HwPortContract>
        {
            new("size_u64", HwPortDirection.Input, u64),
            new("data_length_u64", HwPortDirection.Input, u64),
            new("outstanding_single_lane_stores_u64", HwPortDirection.Input, u64),
            new("accepted", HwPortDirection.Output, bit),
            new("backpressured", HwPortDirection.Output, bit),
            new("rejected", HwPortDirection.Output, bit),
            new("request_id_valid", HwPortDirection.Output, bit)
        };
        var nodes = new List<HwNode>();
        foreach ((string id, ulong value) in new[]
        {
            ("one_u64", 1UL), ("two_u64", 2UL), ("four_u64", 4UL),
            ("eight_u64", 8UL), ("one_bit", 1UL)
        })
            nodes.Add(new HwConstantNode(id, id.EndsWith("bit", StringComparison.Ordinal) ? bit : u64,
                Convert.ToString((long)value, 2).PadLeft(id.EndsWith("bit", StringComparison.Ordinal) ? 1 : 64, '0'), origin));

        foreach (string size in new[] { "one", "two", "four", "eight" })
            nodes.Add(new HwOperationNode("size_is_" + size, "eq", ["size_u64", size + "_u64"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("size_is_1_or_2", "or", ["size_is_one", "size_is_two"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("size_is_4_or_8", "or", ["size_is_four", "size_is_eight"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("size_is_supported", "or", ["size_is_1_or_2", "size_is_4_or_8"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("data_short", "ult64", ["data_length_u64", "size_u64"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("data_sufficient", "xor", ["data_short", "one_bit"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("input_valid", "and", ["size_is_supported", "data_sufficient"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("outstanding_below_capacity", "ult64", ["outstanding_single_lane_stores_u64", "eight_u64"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("accepted_value", "and", ["input_valid", "outstanding_below_capacity"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("backpressured_value", "and", ["input_valid", "outstanding_full"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("input_invalid", "xor", ["input_valid", "one_bit"], bit, Origin: origin));
        nodes.Add(new HwOperationNode("outstanding_full", "xor", ["outstanding_below_capacity", "one_bit"], bit, Origin: origin));
        nodes.Add(new HwOutputDriveNode("drive_accepted", "accepted", "accepted_value", origin));
        nodes.Add(new HwOutputDriveNode("drive_backpressured", "backpressured", "backpressured_value", origin));
        nodes.Add(new HwOutputDriveNode("drive_rejected", "rejected", "input_invalid", origin));
        nodes.Add(new HwOutputDriveNode("drive_request_id_valid", "request_id_valid", "accepted_value", origin));

        var module = new HwModule("single_lane_scalar_store_admission", "single_lane_scalar_store_admission",
            ports, nodes, [], new HwHardwareContract("memory-controller.single-lane-scalar-store-admission.v1",
                HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), origin);
        var design = new HwDesign("hwir/v0.1", module.Id, [module], toolchain, []);
        var validation = new FailClosedHardwareIrValidator().Validate(design);
        return validation.Validated ?? throw new InvalidDataException(
            "Single-lane store admission HWIR refused: " + string.Join("; ", validation.Diagnostics.Select(d => d.Message)));
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
