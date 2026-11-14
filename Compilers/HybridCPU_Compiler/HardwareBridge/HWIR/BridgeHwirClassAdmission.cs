#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Stage A control projection: capacity, legality and outer-cap decisions are external inputs.</summary>
public static class BridgeHwirClassAdmission
{
    public static HwValidatedDesign Compile(byte[] admission,byte[] scheduler,byte[] classes,string[] hashes,string commit,HwToolchainIdentity toolchain)
    {
        static string Hash(byte[] b)=>Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        var inputs=new[]{admission,scheduler,classes};
        if(hashes.Length!=3||inputs.Where((b,i)=>b.Length>2_000_000||Hash(b)!=hashes[i]).Any()||commit.Length!=40||commit.Any(c=>!Uri.IsHexDigit(c))) throw new InvalidDataException("Stage A source identity differs.");
        static SyntaxNode Parse(byte[] b)=>CSharpSyntaxTree.ParseText(new UTF8Encoding(false,true).GetString(b)).GetRoot();
        static bool Same(SyntaxNode a,SyntaxNode b)=>a.DescendantTokens().Select(t=>t.Text).SequenceEqual(b.DescendantTokens().Select(t=>t.Text));
        var root=Parse(admission);var owner=root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c=>c.Identifier.ValueText=="MicroOpScheduler"&&c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString()=="YAKSys_Hybrid_CPU.Core");
        var method=owner.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="TryClassAdmission");
        const string expected="""
        { rejectReason = TypedSlotRejectReason.None; _lastCertificateRejectDetail = CertificateRejectDetail.None; _lastSmtLegalityRejectKind = RejectKind.None; _lastSmtLegalityAuthoritySource = LegalityAuthoritySource.StructuralCertificate;
          if (candidate.Placement.RequiredSlotClass != SlotClass.Unclassified) { bool capacityOk = false; if (templateState.TryConsumeFastPathBudget(candidate.Placement.RequiredSlotClass)) { TypedSlotFastPathAccepts++; capacityOk = true; } if (!capacityOk && !_classCapacity.HasFreeCapacity(candidate.Placement.RequiredSlotClass)) { rejectReason = currentPassInjections == 0 ? TypedSlotRejectReason.StaticClassOvercommit : TypedSlotRejectReason.DynamicClassExhaustion; return false; } }
          LegalityDecision legalityDecision = _runtimeLegalityService.EvaluateSmtLegality(useReplayCertificate ? _currentReplayPhase : default, cert4Way, bundleMetadata, boundaryGuard, candidate);
          _lastCertificateRejectDetail = legalityDecision.CertificateDetail; _lastSmtLegalityRejectKind = legalityDecision.RejectKind; _lastSmtLegalityAuthoritySource = legalityDecision.AuthoritySource;
          if (!legalityDecision.IsAllowed) { rejectReason = TypedSlotRejectReason.ResourceConflict; return false; }
          if (candidate is not AssistMicroOp) { int candidateBankId = projectedBankId ?? (candidate is LoadStoreMicroOp ls ? ls.MemoryBankId : -1); if (!TryPassOuterCap(candidate, candidate.VirtualThreadId, candidate.IsMemoryOp, candidateBankId, ownerVirtualThreadId, out rejectReason)) { return false; } }
          return true; }
        """;
        if(method.Body is null||!Same(method.Body,SyntaxFactory.ParseStatement(expected)))throw new InvalidDataException("Stage A control flow changed.");
        var schedulerRoot=Parse(scheduler);var template=schedulerRoot.DescendantNodes().OfType<StructDeclarationSyntax>().Single(s=>s.Identifier.ValueText=="ClassTemplateAdmissionState");
        var consume=template.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="TryConsumeFastPathBudget");
        if(consume.Body is null||!Same(consume.Body,SyntaxFactory.ParseStatement("{ if (!TemplateHit) return false; if (_templateBudget.GetRemaining(slotClass) <= 0) return false; _templateBudget.Decrement(slotClass); return true; }")))throw new InvalidDataException("Template consumption changed.");
        var enums=Parse(classes);
        int Value(string type,string member)=>Convert.ToInt32(((LiteralExpressionSyntax)enums.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(e=>e.Identifier.ValueText==type).Members.Single(m=>m.Identifier.ValueText==member).EqualsValue!.Value).Token.Value);
        if(Value("SlotClass","Unclassified")!=7||Value("TypedSlotRejectReason","None")!=0||Value("TypedSlotRejectReason","StaticClassOvercommit")!=1||Value("TypedSlotRejectReason","DynamicClassExhaustion")!=2||Value("TypedSlotRejectReason","ResourceConflict")!=3)throw new InvalidDataException("Stage A enum encoding changed.");
        var pos=method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        var origin=new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2",commit,"HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/Admission/MicroOpScheduler.Admission.cs","CircuitWeave.StageA.Projection","YAKSys_Hybrid_CPU.Core.MicroOpScheduler","TryClassAdmission",Hash(Encoding.UTF8.GetBytes("stage-a-control/v1|"+string.Join("|",hashes))),HwSourceRole.Primary,pos.Line+1,pos.Character+1);
        var u1=new HwBitVectorType(1);var u8=new HwBitVectorType(8);var u32=new HwBitVectorType(32);
        HwPortContract[] ports=[new("slot_class",HwPortDirection.Input,u8),new("template_hit",HwPortDirection.Input,u1),new("template_remaining",HwPortDirection.Input,u8),new("capacity_free",HwPortDirection.Input,u1),new("current_injections",HwPortDirection.Input,u32),new("legality_allowed",HwPortDirection.Input,u1),new("is_assist",HwPortDirection.Input,u1),new("outer_allowed",HwPortDirection.Input,u1),new("outer_reason",HwPortDirection.Input,u8),new("accepted",HwPortDirection.Output,u1),new("reject_reason",HwPortDirection.Output,u8),new("template_next",HwPortDirection.Output,u8),new("fast_consumed",HwPortDirection.Output,u1),new("legality_evaluated",HwPortDirection.Output,u1),new("outer_evaluated",HwPortDirection.Output,u1)];
        var nodes=new List<HwNode>{new HwConstantNode("zero1",u1,"0",origin),new HwConstantNode("one1",u1,"1",origin),new HwConstantNode("zero8",u8,"00000000",origin),new HwConstantNode("one8",u8,"00000001",origin),new HwConstantNode("two8",u8,"00000010",origin),new HwConstantNode("three8",u8,"00000011",origin),new HwConstantNode("unclassified8",u8,"00000111",origin),new HwConstantNode("zero32",u32,new string('0',32),origin),
            new HwOperationNode("unclassified","eq",["slot_class","unclassified8"],u1,Origin:origin),new HwOperationNode("classified","xor",["unclassified","one1"],u1,Origin:origin),new HwOperationNode("empty_budget","eq",["template_remaining","zero8"],u1,Origin:origin),new HwOperationNode("budget_available","xor",["empty_budget","one1"],u1,Origin:origin),new HwOperationNode("hit_budget","and",["template_hit","budget_available"],u1,Origin:origin),new HwOperationNode("consume","and",["classified","hit_budget"],u1,Origin:origin),new HwOperationNode("budget_decrement","sub",["template_remaining","one8"],u8,Origin:origin),new HwMuxNode("budget_result","consume","budget_decrement","template_remaining",u8,origin),
            new HwOperationNode("capacity_or_fast","or",["capacity_free","consume"],u1,Origin:origin),new HwOperationNode("capacity_pass","or",["unclassified","capacity_or_fast"],u1,Origin:origin),new HwOperationNode("capacity_fail","xor",["capacity_pass","one1"],u1,Origin:origin),new HwOperationNode("initial_pass","eq",["current_injections","zero32"],u1,Origin:origin),new HwMuxNode("capacity_reason","initial_pass","one8","two8",u8,origin),new HwOperationNode("illegal","xor",["legality_allowed","one1"],u1,Origin:origin),new HwOperationNode("legality_pass","and",["capacity_pass","legality_allowed"],u1,Origin:origin),new HwOperationNode("not_assist","xor",["is_assist","one1"],u1,Origin:origin),new HwOperationNode("outer_eval","and",["legality_pass","not_assist"],u1,Origin:origin),new HwOperationNode("outer_denied","xor",["outer_allowed","one1"],u1,Origin:origin),new HwOperationNode("outer_fail","and",["outer_eval","outer_denied"],u1,Origin:origin),new HwMuxNode("outer_or_zero","outer_fail","outer_reason","zero8",u8,origin),new HwMuxNode("legality_reason","illegal","three8","outer_or_zero",u8,origin),new HwMuxNode("reason","capacity_fail","capacity_reason","legality_reason",u8,origin),new HwOperationNode("reason_zero","eq",["reason","zero8"],u1,Origin:origin),new HwOperationNode("not_outer_fail","xor",["outer_fail","one1"],u1,Origin:origin),new HwOperationNode("accepted_result","and",["legality_pass","not_outer_fail"],u1,Origin:origin),
            new HwOutputDriveNode("drive_accepted","accepted","accepted_result",origin),new HwOutputDriveNode("drive_reason","reject_reason","reason",origin),new HwOutputDriveNode("drive_budget","template_next","budget_result",origin),new HwOutputDriveNode("drive_consumed","fast_consumed","consume",origin),new HwOutputDriveNode("drive_legality","legality_evaluated","capacity_pass",origin),new HwOutputDriveNode("drive_outer","outer_evaluated","outer_eval",origin)};
        var top=new HwModule("stage_a_control_projection","stage_a_control_projection",ports,nodes,[],new HwHardwareContract("scheduler.stage-a-control.unqualified.v1",HwModuleKind.Combinational,ports,new HwTimingContract(0,1)),origin);
        var result=new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",top.Id,[top],toolchain,[]));return result.Validated??throw new InvalidDataException("Stage A HWIR refused: "+string.Join("; ",result.Diagnostics.Select(d=>d.Message)));
    }
}
#endif
