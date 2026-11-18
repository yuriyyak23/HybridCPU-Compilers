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
/// Source-bounded combinational projection of the post-scheduler packet-owner
/// commit/rebuild. Scheduler admission and the granted source-slot mask are
/// explicit inputs; this module does not model Stage A/B or a hardware cycle.
/// </summary>
public static class BridgeHwirPacketOwnerPartialGrant
{
    private const string ActiveCommit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";
    private const string CallerPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/CPU_Core.PacketOwnerIssue.cs";
    private const string SchedulerPath = "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/Admission/MicroOpScheduler.PacketOwner.cs";
    private const string CallerSha256 = "801d1197e9e80b530143d770c9c9cf78ea1a83432f989f0b0d2eab92af402203";
    private const string SchedulerSha256 = "1c9194930d14c1f1521c4eab6dd63b10438f45513088960e57ab1800929bd95f";

    public static HwValidatedDesign Compile(byte[] callerBytes, byte[] schedulerBytes, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(callerBytes);
        ArgumentNullException.ThrowIfNull(schedulerBytes);
        ArgumentNullException.ThrowIfNull(toolchain);
        if (Hash(callerBytes) != CallerSha256 || Hash(schedulerBytes) != SchedulerSha256 ||
            toolchain.HardwareLanguageProfile != "hwp-hwir-comb-v1")
            throw new InvalidDataException("Packet-owner partial-grant source bytes or HWIR profile differ from the pinned active inputs.");

        static SyntaxNode Parse(byte[] bytes)
        {
            var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Packet-owner partial-grant source contains C# syntax errors.");
            return tree.GetRoot();
        }
        static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(token => token.Text));
        static MethodDeclarationSyntax Method(SyntaxNode root, string name) => root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == name);

        SyntaxNode callerRoot = Parse(callerBytes);
        SyntaxNode schedulerRoot = Parse(schedulerBytes);
        MethodDeclarationSyntax caller = Method(callerRoot, "TryPreparePacketOwnerScalarIssue");
        MethodDeclarationSyntax scheduler = Method(schedulerRoot, "TryIssuePacketOwnerScalars");
        string callerTokens = Tokens(callerRoot);
        string[] callerFacts =
        [
            "booladmitted=scheduler.TryIssuePacketOwnerScalars(packet,selected,executable,ReadActiveVirtualThreadId(),pipeID.SlotIndex,boundMemory,outTypedSlotRejectReasonreason,outbyteissuedMask);",
            "if(!admitted)returnfalse;",
            "byteselectedMask=0;",
            "selectedMask|=(byte)(1<<entry.WorkingSlotId.Value);",
            "if((selectedMask&(1<<pipeID.SlotIndex))!=0&&(issuedMask&(1<<pipeID.SlotIndex))==0)returnfalse;",
            "if(issuedMask!=selectedMask)",
            "byte grantedSources=(byte)(~selectedMask|issuedMask);",
            "RebuildPacketOwnerHandoff(slots,(byte)(pipeIDAdmissionDecisionDraft.ScalarIssueMask&grantedSources),(byte)(pipeIDAdmissionDecisionDraft.AuxiliaryReservationMask&grantedSources));",
            "if((issuedMask&(1<<entry.WorkingSlotId.Value))==0)entry.Carrier.PostStageBIdentityTemplate=null;"
        ];
        int previous = -1;
        foreach (string fact in callerFacts)
        {
            string compactFact = string.Concat(fact.Where(character => !char.IsWhiteSpace(character)));
            int index = callerTokens.IndexOf(compactFact, previous + 1, StringComparison.Ordinal);
            if (index <= previous)
                throw new InvalidDataException("Packet-owner caller commit/rebuild source contract changed: " + compactFact);
            previous = index;
        }

        string schedulerTokens = Tokens(scheduler);
        foreach (string fact in new[]
        {
            "selected.OrderBy(e=>(e.WorkingSlotId.Value-currentSlot+BundleMetadata.BundleSlotCount)%BundleMetadata.BundleSlotCount)",
            "TryClassAdmission(op,refadmissionCertificate,metadata,boundary,reftemplateState,ownerVt,0,outrejection,useReplayCertificate:!pairedStores,projectedBankId:bank)",
            "TryMaterializeLane(op,occupied,outintlane,outrejection,LaneMaterializationRole.Foreground,(byte)(1<<requested[0]))",
            "issuedWorkingMask|=(byte)(1<<entry.WorkingSlotId.Value)",
            "returnissuedWorkingMask!=0;"
        })
            if (!schedulerTokens.Contains(fact, StringComparison.Ordinal))
                throw new InvalidDataException("Packet-owner scheduler grant-source contract changed: " + fact);
        int stageBIndex = schedulerTokens.IndexOf("TryMaterializeLane(", StringComparison.Ordinal);
        int issuedIndex = schedulerTokens.IndexOf("issuedWorkingMask|=(byte)(1<<entry.WorkingSlotId.Value)", StringComparison.Ordinal);
        if (stageBIndex < 0 || issuedIndex <= stageBIndex)
            throw new InvalidDataException("Packet-owner issued mask is no longer updated after Stage B acceptance.");

        static string GitBlobId(byte[] bytes)
        {
            byte[] header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
            byte[] gitObject = new byte[header.Length + bytes.Length];
            Buffer.BlockCopy(header, 0, gitObject, 0, header.Length);
            Buffer.BlockCopy(bytes, 0, gitObject, header.Length, bytes.Length);
            return Convert.ToHexString(SHA1.HashData(gitObject)).ToLowerInvariant();
        }

        static HwSourceOrigin Origin(string path, string symbol, string member, string hash,
            string capturedGitBlobId, SyntaxNode node, string role)
        {
            var line = node.GetLocation().GetLineSpan().StartLinePosition;
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "|" + hash + "|" + Tokens(node)))).ToLowerInvariant();
            // These active files are untracked at ActiveCommit. Use an explicit
            // content-addressed capture namespace. Commit carries the Git blob OID,
            // not an unreachable repository commit; exact source bytes are captured
            // separately for Studio source navigation.
            return new HwSourceOrigin("urn:circuitweave:dirty-source-capture:sha256:" + hash, capturedGitBlobId, path,
                "CircuitWeave.PacketOwnerPartialGrant", symbol, member, identity, HwSourceRole.Primary,
                line.Line + 1, line.Character + 1);
        }

        HwSourceOrigin callerOrigin = Origin(CallerPath, "YAKSys_Hybrid_CPU.Processor.CPU_Core",
            "TryPreparePacketOwnerScalarIssue", CallerSha256, GitBlobId(callerBytes), caller, "packet-owner-caller");
        HwSourceOrigin schedulerOrigin = Origin(SchedulerPath, "YAKSys_Hybrid_CPU.Core.MicroOpScheduler",
            "TryIssuePacketOwnerScalars", SchedulerSha256, GitBlobId(schedulerBytes), scheduler, "packet-owner-scheduler");

        var bit = new HwBitVectorType(1);
        var u3 = new HwBitVectorType(3);
        var u8 = new HwBitVectorType(8);
        var u32 = new HwBitVectorType(32);
        var ports = new List<HwPortContract>
        {
            new("draft_scalar_mask", HwPortDirection.Input, u8),
            new("draft_auxiliary_mask", HwPortDirection.Input, u8),
            new("selected_working_mask", HwPortDirection.Input, u8),
            new("issued_working_mask", HwPortDirection.Input, u8),
            new("scheduler_admitted", HwPortDirection.Input, bit),
            new("current_slot", HwPortDirection.Input, u3),
            new("commit_allowed", HwPortDirection.Output, bit),
            new("partial_grant", HwPortDirection.Output, bit),
            new("rebuild_handoff", HwPortDirection.Output, bit),
            new("granted_source_mask", HwPortDirection.Output, u8),
            new("next_scalar_mask", HwPortDirection.Output, u8),
            new("next_auxiliary_mask", HwPortDirection.Output, u8),
            new("denied_suffix_mask", HwPortDirection.Output, u8),
            new("identity_template_clear_mask", HwPortDirection.Output, u8)
        };
        var nodes = new List<HwNode>
        {
            new HwConstantNode("one1", bit, "1", callerOrigin),
            new HwConstantNode("zero1", bit, "0", callerOrigin),
            new HwConstantNode("zero8", u8, "00000000", callerOrigin),
            new HwConstantNode("ones8", u8, "11111111", callerOrigin),
            new HwConstantNode("one32", u32, "00000000000000000000000000000001", callerOrigin),
            new HwOperationNode("current_slot32", "zext", ["current_slot"], u32, Origin: callerOrigin),
            new HwOperationNode("current_slot_bit32", "shl32_mask5", ["one32", "current_slot32"], u32, Origin: callerOrigin),
            new HwOperationNode("current_slot_bit", "trunc", ["current_slot_bit32"], u8, Origin: callerOrigin),
            new HwOperationNode("selected_cursor_bits", "and", ["selected_working_mask", "current_slot_bit"], u8, Origin: callerOrigin),
            new HwOperationNode("selected_cursor_absent", "eq", ["selected_cursor_bits", "zero8"], bit, Origin: callerOrigin),
            new HwOperationNode("issued_cursor_bits", "and", ["issued_working_mask", "current_slot_bit"], u8, Origin: schedulerOrigin),
            new HwOperationNode("issued_cursor_absent", "eq", ["issued_cursor_bits", "zero8"], bit, Origin: schedulerOrigin),
            new HwOperationNode("cursor_is_selected", "xor", ["selected_cursor_absent", "one1"], bit, Origin: callerOrigin),
            new HwOperationNode("cursor_selected_but_unissued", "and", ["cursor_is_selected", "issued_cursor_absent"], bit, Origin: callerOrigin),
            new HwOperationNode("cursor_allowed", "xor", ["cursor_selected_but_unissued", "one1"], bit, Origin: callerOrigin),
            new HwOperationNode("commit", "and", ["scheduler_admitted", "cursor_allowed"], bit, Origin: callerOrigin),
            new HwOperationNode("selected_equals_issued", "eq", ["selected_working_mask", "issued_working_mask"], bit, Origin: schedulerOrigin),
            new HwOperationNode("not_partial", "and", ["selected_equals_issued", "one1"], bit, Origin: callerOrigin),
            new HwOperationNode("partial", "xor", ["not_partial", "one1"], bit, Origin: callerOrigin),
            new HwOperationNode("rebuild", "and", ["commit", "partial"], bit, Origin: callerOrigin),
            new HwOperationNode("selected_complement", "xor", ["selected_working_mask", "ones8"], u8, Origin: callerOrigin),
            new HwOperationNode("grant_sources", "or", ["selected_complement", "issued_working_mask"], u8, Origin: callerOrigin),
            new HwOperationNode("scalar_granted_bits", "and", ["draft_scalar_mask", "grant_sources"], u8, Origin: callerOrigin),
            new HwMuxNode("next_scalar", "rebuild", "scalar_granted_bits", "draft_scalar_mask", u8, callerOrigin),
            new HwOperationNode("auxiliary_granted_bits", "and", ["draft_auxiliary_mask", "grant_sources"], u8, Origin: callerOrigin),
            new HwMuxNode("next_auxiliary", "rebuild", "auxiliary_granted_bits", "draft_auxiliary_mask", u8, callerOrigin),
            new HwOperationNode("issued_complement", "xor", ["issued_working_mask", "ones8"], u8, Origin: schedulerOrigin),
            new HwOperationNode("denied_bits", "and", ["selected_working_mask", "issued_complement"], u8, Origin: callerOrigin),
            new HwMuxNode("denied_suffix", "rebuild", "denied_bits", "zero8", u8, callerOrigin),
            new HwOutputDriveNode("drive_commit_allowed", "commit_allowed", "commit", callerOrigin),
            new HwOutputDriveNode("drive_partial_grant", "partial_grant", "rebuild", callerOrigin),
            new HwOutputDriveNode("drive_rebuild_handoff", "rebuild_handoff", "rebuild", callerOrigin),
            new HwOutputDriveNode("drive_granted_source_mask", "granted_source_mask", "grant_sources", callerOrigin),
            new HwOutputDriveNode("drive_next_scalar_mask", "next_scalar_mask", "next_scalar", callerOrigin),
            new HwOutputDriveNode("drive_next_auxiliary_mask", "next_auxiliary_mask", "next_auxiliary", callerOrigin),
            new HwOutputDriveNode("drive_denied_suffix_mask", "denied_suffix_mask", "denied_suffix", callerOrigin),
            new HwOutputDriveNode("drive_identity_clear_mask", "identity_template_clear_mask", "denied_suffix", callerOrigin)
        };
        var module = new HwModule("packet_owner_partial_grant", "packet_owner_partial_grant", ports, nodes, [],
            new HwHardwareContract("scheduler.packet-owner.partial-grant.source-bounded.v1",
                HwModuleKind.Combinational, ports, new HwTimingContract(0, 1)), callerOrigin);
        var design = new HwDesign("hwir/v0.1", module.Id, [module], toolchain, []);
        var validation = new FailClosedHardwareIrValidator().Validate(design);
        return validation.Validated ?? throw new InvalidDataException(
            "Packet-owner partial-grant HWIR refused: " + string.Join("; ", validation.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
