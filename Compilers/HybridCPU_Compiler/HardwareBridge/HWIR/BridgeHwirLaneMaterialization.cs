#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Stage-B decision for class-admitted inputs; scheduler state and Stage A remain external.</summary>
public static class BridgeHwirLaneMaterialization
{
    public static HwValidatedDesign Compile(byte[] admission,byte[] laneId,byte[] classes,byte[] chooser,string[] hashes,string commit,HwToolchainIdentity toolchain)
    {
        static string Hash(byte[] b)=>Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        var sources=new[]{admission,laneId,classes,chooser};
        if(hashes.Length!=4 || sources.Where((b,i)=>b.Length>2_000_000||Hash(b)!=hashes[i]).Any() || commit.Length!=40 || commit.Any(c=>!Uri.IsHexDigit(c))) throw new InvalidDataException("Stage B source identity differs.");
        static SyntaxNode Parse(byte[] b)=>CSharpSyntaxTree.ParseText(new UTF8Encoding(false,true).GetString(b)).GetRoot();
        static bool Same(SyntaxNode a,SyntaxNode b)=>a.DescendantTokens().Select(t=>t.Text).SequenceEqual(b.DescendantTokens().Select(t=>t.Text));
        var root=Parse(admission); var owner=root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c=>c.Identifier.ValueText=="MicroOpScheduler" && c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString()=="YAKSys_Hybrid_CPU.Core");
        var method=owner.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="TryMaterializeLane");
        const string expected="""
        { rejectReason = TypedSlotRejectReason.None; selectedLane = -1;
          if (candidate.Placement.PinningKind == SlotPinningKind.HardPinned) {
            if (!LaneId.TryCreate(candidate.Placement.PinnedLaneId, out LaneId laneId)) { rejectReason = TypedSlotRejectReason.InvalidPinnedLane; return false; }
            int lane = laneId;
            if ((bundleOccupancy & (1 << lane)) != 0 || (permittedLanes & (1 << lane)) == 0) { rejectReason = TypedSlotRejectReason.PinnedLaneConflict; return false; }
            selectedLane = lane; return true; }
          byte classMask = SlotClassLaneMap.GetLaneMask(candidate.Placement.RequiredSlotClass);
          byte freeLanes = (byte)(classMask & ~bundleOccupancy & permittedLanes);
          if (role == LaneMaterializationRole.Opportunistic && _currentReplayPhase.HasStableDonorStructure) { freeLanes &= _currentReplayPhase.StableDonorMask; }
          if (freeLanes == 0) { rejectReason = TypedSlotRejectReason.LateBindingConflict; return false; }
          selectedLane = DeterministicLaneChooser.SelectWithReplayHint(freeLanes, _currentReplayPhase.IsActive, GetPreviousPhaseLane(candidate.Placement.RequiredSlotClass));
          if (_currentReplayPhase.IsActive) { int prevLane = GetPreviousPhaseLane(candidate.Placement.RequiredSlotClass); if (prevLane >= 0 && selectedLane == prevLane) LaneReuseHits++; else if (prevLane >= 0) LaneReuseMisses++; }
          return true; }
        """;
        if(method.Body is null || !Same(method.Body,SyntaxFactory.ParseStatement(expected))) throw new InvalidDataException("Stage B materialization body changed.");
        var laneType=Parse(laneId).DescendantNodes().OfType<RecordDeclarationSyntax>().Single(c=>c.Identifier.ValueText=="LaneId");
        var represent=laneType.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="IsRepresentable");
        var create=laneType.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="TryCreate");
        if(represent.ExpressionBody is null || !Same(represent.ExpressionBody.Expression,SyntaxFactory.ParseExpression("(uint)value < LaneCount")) ||
           create.Body is null || !Same(create.Body,SyntaxFactory.ParseStatement("{ if (IsRepresentable(value)) { laneId = new LaneId((byte)value, skipValidation: true); return true; } laneId = default; return false; }")) ||
           !laneType.Members.OfType<FieldDeclarationSyntax>().Any(f=>f.Declaration.Variables.Any(v=>v.Identifier.ValueText=="LaneCount" && v.Initializer?.Value.ToString()=="8"))) throw new InvalidDataException("LaneId representability changed.");
        var enumRoot=Parse(classes);
        int EnumValue(string type,string member)=>Convert.ToInt32(((LiteralExpressionSyntax)enumRoot.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(e=>e.Identifier.ValueText==type).Members.Single(m=>m.Identifier.ValueText==member).EqualsValue!.Value).Token.Value);
        if(EnumValue("SlotPinningKind","HardPinned")!=1 || EnumValue("TypedSlotRejectReason","None")!=0 || EnumValue("TypedSlotRejectReason","PinnedLaneConflict")!=11 || EnumValue("TypedSlotRejectReason","LateBindingConflict")!=12 || EnumValue("TypedSlotRejectReason","InvalidPinnedLane")!=13) throw new InvalidDataException("Stage B enum encoding changed.");
        var mask=BridgeHwirSlotClassMask.Compile(classes,hashes[2],commit,toolchain); var select=BridgeHwirLaneChooser.Compile(chooser,hashes[3],commit,toolchain);
        var pos=method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        var origin=new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2",commit,"HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/Admission/MicroOpScheduler.Admission.cs",
            "CircuitWeave.StageB.Projection","YAKSys_Hybrid_CPU.Core.MicroOpScheduler","TryMaterializeLane",Hash(Encoding.UTF8.GetBytes("stage-b/v1|"+string.Join("|",hashes))),HwSourceRole.Primary,pos.Line+1,pos.Character+1);
        var u1=new HwBitVectorType(1);var u3=new HwBitVectorType(3);var u4=new HwBitVectorType(4);var u8=new HwBitVectorType(8);var u32=new HwBitVectorType(32);
        HwPortContract[] ports=[new("slot_class",HwPortDirection.Input,u8),new("pinning_kind",HwPortDirection.Input,u8),new("pinned_lane",HwPortDirection.Input,u8),new("occupancy",HwPortDirection.Input,u8),new("permitted_lanes",HwPortDirection.Input,u8),new("opportunistic",HwPortDirection.Input,u1),new("stable_donor",HwPortDirection.Input,u1),new("donor_mask",HwPortDirection.Input,u8),new("replay_active",HwPortDirection.Input,u1),new("previous_lane",HwPortDirection.Input,u32),new("defined",HwPortDirection.Output,u1),new("accepted",HwPortDirection.Output,u1),new("selected_lane",HwPortDirection.Output,u3),new("reject_reason",HwPortDirection.Output,u4)];
        var nodes=new List<HwNode>{new HwConstantNode("zero8",u8,"00000000",origin),new HwConstantNode("one8",u8,"00000001",origin),new HwConstantNode("one1",u1,"1",origin),new HwConstantNode("zero1",u1,"0",origin),new HwConstantNode("zero3",u3,"000",origin),new HwConstantNode("zero4",u4,"0000",origin),new HwConstantNode("conflict4",u4,"1011",origin),new HwConstantNode("late4",u4,"1100",origin),new HwConstantNode("invalid4",u4,"1101",origin),
            new HwWireNode("class_valid",u1,origin),new HwWireNode("class_mask",u8,origin),new HwWireNode("choice_valid",u1,origin),new HwWireNode("choice_lane",u3,origin),new HwWireNode("choice_reused",u1,origin),
            new HwOperationNode("hard","eq",["pinning_kind","one8"],u1,Origin:origin),new HwOperationNode("not_occupancy","xor",["occupancy","all8"],u8,Origin:origin),new HwConstantNode("all8",u8,"11111111",origin),
            new HwOperationNode("class_free","and",["class_mask","not_occupancy"],u8,Origin:origin),new HwOperationNode("permitted_free","and",["class_free","permitted_lanes"],u8,Origin:origin),new HwOperationNode("donor_required","and",["opportunistic","stable_donor"],u1,Origin:origin),new HwOperationNode("donor_free","and",["permitted_free","donor_mask"],u8,Origin:origin),new HwMuxNode("free_lanes","donor_required","donor_free","permitted_free",u8,origin)};
        string pinValid="zero1",pinAvailable="zero1",pinLane="zero3";
        for(int i=0;i<8;i++) { string p="pin"+i+"_"; nodes.Add(new HwConstantNode(p+"byte",u8,Convert.ToString(i,2).PadLeft(8,'0'),origin));nodes.Add(new HwConstantNode(p+"id",u3,Convert.ToString(i,2).PadLeft(3,'0'),origin));nodes.Add(new HwConstantNode(p+"mask",u8,Convert.ToString(1<<i,2).PadLeft(8,'0'),origin));nodes.Add(new HwOperationNode(p+"match","eq",["pinned_lane",p+"byte"],u1,Origin:origin));nodes.Add(new HwOperationNode(p+"free_mask","and",["not_occupancy","permitted_lanes"],u8,Origin:origin));nodes.Add(new HwOperationNode(p+"free_bit","and",[p+"free_mask",p+"mask"],u8,Origin:origin));nodes.Add(new HwOperationNode(p+"clear","eq",[p+"free_bit","zero8"],u1,Origin:origin));nodes.Add(new HwOperationNode(p+"free","xor",[p+"clear","one1"],u1,Origin:origin));nodes.Add(new HwOperationNode(p+"available","and",[p+"match",p+"free"],u1,Origin:origin));nodes.Add(new HwOperationNode(p+"valid_or","or",[pinValid,p+"match"],u1,Origin:origin));nodes.Add(new HwOperationNode(p+"available_or","or",[pinAvailable,p+"available"],u1,Origin:origin));nodes.Add(new HwMuxNode(p+"lane",p+"match",p+"id",pinLane,u3,origin));pinValid=p+"valid_or";pinAvailable=p+"available_or";pinLane=p+"lane"; }
        nodes.AddRange([new HwMuxNode("defined_result","hard","one1","class_valid",u1,origin),new HwMuxNode("accepted_result","hard",pinAvailable,"choice_valid",u1,origin),new HwMuxNode("lane_result","hard",pinLane,"choice_lane",u3,origin),
            new HwMuxNode("pin_conflict_reason",pinAvailable,"zero4","conflict4",u4,origin),new HwMuxNode("pin_reason",pinValid,"pin_conflict_reason","invalid4",u4,origin),new HwMuxNode("flex_reason","choice_valid","zero4","late4",u4,origin),new HwMuxNode("reason_result","hard","pin_reason","flex_reason",u4,origin),
            new HwOutputDriveNode("drive_defined","defined","defined_result",origin),new HwOutputDriveNode("drive_accepted","accepted","accepted_result",origin),new HwOutputDriveNode("drive_lane","selected_lane","lane_result",origin),new HwOutputDriveNode("drive_reason","reject_reason","reason_result",origin)]);
        var children=new[]{new HwInstanceNode("class_mask_lookup","slot_class_mask_projection",new Dictionary<string,string>{{"slot_class","slot_class"},{"valid","class_valid"},{"lane_mask","class_mask"}},new Dictionary<string,string>(),Origin:origin),new HwInstanceNode("lane_chooser","replay_lane_chooser_projection",new Dictionary<string,string>{{"free_lanes","free_lanes"},{"replay_active","replay_active"},{"previous_lane","previous_lane"},{"valid","choice_valid"},{"selected_lane","choice_lane"},{"reused","choice_reused"}},new Dictionary<string,string>(),Origin:origin)};
        var top=new HwModule("stage_b_lane_materialization_projection","stage_b_lane_materialization_projection",ports,nodes,children,new HwHardwareContract("scheduler.stage-b-lane-materialization.unqualified.v1",HwModuleKind.Combinational,ports,new HwTimingContract(0,1)),origin);
        var result=new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",top.Id,[top,mask.Design.Modules.Single(),select.Design.Modules.Single()],toolchain,[]));
        return result.Validated ?? throw new InvalidDataException("Stage B HWIR refused: "+string.Join("; ",result.Diagnostics.Select(d=>d.Message)));
    }
}
#endif
