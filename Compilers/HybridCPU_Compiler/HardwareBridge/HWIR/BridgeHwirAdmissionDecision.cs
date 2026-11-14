#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Single-candidate A/B decision only; caller supplies state and commits changes.</summary>
public static class BridgeHwirAdmissionDecision
{
    public static HwValidatedDesign Compile(byte[] smt,string smtHash,HwValidatedDesign stageA,HwValidatedDesign stageB,string commit,byte[]? capacitySource=null,string? capacityHash=null)
    {
        static string Hash(byte[] b)=>Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        if(smt.Length>2_000_000||Hash(smt)!=smtHash||commit.Length!=40||commit.Any(c=>!Uri.IsHexDigit(c))||stageA.Design.Toolchain!=stageB.Design.Toolchain||
            stageA.Design.TopModule!="stage_a_capacity_projection"||stageB.Design.TopModule!="stage_b_lane_materialization_projection")throw new InvalidDataException("Admission decision identity differs.");
        var root=CSharpSyntaxTree.ParseText(new UTF8Encoding(false,true).GetString(smt)).GetRoot();
        var owner=root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c=>c.Identifier.ValueText=="MicroOpScheduler"&&c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString()=="YAKSys_Hybrid_CPU.Core");
        var method=owner.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="PackBundleIntraCoreSmt"&&m.ParameterList.Parameters.Count==5);
        var blocks=method.DescendantNodes().OfType<BlockSyntax>().Where(b=>b.Statements.Count>=2 && b.Statements[0] is IfStatementSyntax && b.Statements[1] is IfStatementSyntax).ToArray();
        const string guardA="if (!TryClassAdmission(candidate, ref bundleCert, bundleMetadata, boundaryGuard, ref templateState, ownerVirtualThreadId, realInjectedCount, out TypedSlotRejectReason rejectA)) { RecordTypedSlotReject(rejectA, candidate); continue; }";
        const string guardB="if (!TryMaterializeLane(candidate, bundleOccupancy, out int lane, out TypedSlotRejectReason rejectB)) { RecordTypedSlotReject(rejectB, candidate); continue; }";
        static bool Same(SyntaxNode a,SyntaxNode b)=>a.DescendantTokens().Select(t=>t.Text).SequenceEqual(b.DescendantTokens().Select(t=>t.Text));
        if(blocks.Count(b=>Same(b.Statements[0],SyntaxFactory.ParseStatement(guardA))&&Same(b.Statements[1],SyntaxFactory.ParseStatement(guardB)))!=1)throw new InvalidDataException("SMT admission gate order changed.");
        if(capacitySource is not null)
        {
            if(capacitySource.Length>2_000_000||Hash(capacitySource)!=capacityHash)throw new InvalidDataException("Capacity update source identity differs.");
            var capRoot=CSharpSyntaxTree.ParseText(new UTF8Encoding(false,true).GetString(capacitySource)).GetRoot();
            var capType=capRoot.DescendantNodes().OfType<StructDeclarationSyntax>().Single(s=>s.Identifier.ValueText=="SlotClassCapacityState"&&s.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString()=="YAKSys_Hybrid_CPU.Core");
            var increment=capType.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="IncrementOccupancy");
            const string expectedIncrement="{ switch (slotClass) { case SlotClass.AluClass: AluOccupied++; break; case SlotClass.LsuClass: LsuOccupied++; break; case SlotClass.DmaStreamClass: DmaStreamOccupied++; break; case SlotClass.MatrixTileStreamClass: MatrixTileStreamOccupied++; break; case SlotClass.BranchControl: BranchControlOccupied++; break; case SlotClass.SystemSingleton: SystemSingletonOccupied++; break; } }";
            if(increment.Body is null||!Same(increment.Body,SyntaxFactory.ParseStatement(expectedIncrement)))throw new InvalidDataException("Capacity increment behavior changed.");
            var gate=blocks.Single(b=>Same(b.Statements[0],SyntaxFactory.ParseStatement(guardA))&&Same(b.Statements[1],SyntaxFactory.ParseStatement(guardB)));
            if(gate.Statements.OfType<ExpressionStatementSyntax>().Count(s=>s.Expression.ToString()=="_classCapacity.IncrementOccupancy(candidate.Placement.RequiredSlotClass)")!=1 ||
               !gate.Statements.OfType<ExpressionStatementSyntax>().Any(s=>s.Expression.ToString()=="bundleOccupancy |= (byte)(1 << lane)"))throw new InvalidDataException("SMT occupancy commit changed.");
        }
        var pos=method.Identifier.GetLocation().GetLineSpan().StartLinePosition;var origin=new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2",commit,"HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/Smt/MicroOpScheduler.SMT.cs","CircuitWeave.AdmissionDecision.Projection","YAKSys_Hybrid_CPU.Core.MicroOpScheduler","PackBundleIntraCoreSmt",Hash(Encoding.UTF8.GetBytes("admission-decision/v2|"+smtHash+"|"+stageA.CanonicalDigest+"|"+stageB.CanonicalDigest+"|"+(capacityHash??"none"))),HwSourceRole.Primary,pos.Line+1,pos.Character+1);
        var a=stageA.Design.Modules.Single(m=>m.Id==stageA.Design.TopModule);var b=stageB.Design.Modules.Single(m=>m.Id==stageB.Design.TopModule);var u1=new HwBitVectorType(1);var u3=new HwBitVectorType(3);var u4=new HwBitVectorType(4);var u8=new HwBitVectorType(8);
        var ports=new List<HwPortContract>();void Input(HwPortContract p){var old=ports.SingleOrDefault(x=>x.Name==p.Name);if(old is null)ports.Add(p);else if(old!=p)throw new InvalidDataException("Admission input contract differs: "+p.Name);}
        foreach(var p in a.Ports.Concat(b.Ports).Where(p=>p.Direction==HwPortDirection.Input))Input(p);
        ports.AddRange([new("defined",HwPortDirection.Output,u1),new("accepted",HwPortDirection.Output,u1),new("selected_lane",HwPortDirection.Output,u3),new("reject_reason",HwPortDirection.Output,u8),new("stage_b_evaluated",HwPortDirection.Output,u1)]);
        foreach(var p in a.Ports.Where(p=>p.Direction==HwPortDirection.Output&&p.Name is not ("accepted" or "reject_reason")))ports.Add(p);
        if(capacitySource is not null){ports.Add(new("next_occupancy",HwPortDirection.Output,u8));for(int i=0;i<6;i++)ports.Add(new("next_occupied"+i,HwPortDirection.Output,u8));}
        var nodes=new List<HwNode>{new HwWireNode("a_accepted",u1,origin),new HwWireNode("a_reason",u8,origin),new HwWireNode("b_defined",u1,origin),new HwWireNode("b_accepted",u1,origin),new HwWireNode("b_lane",u3,origin),new HwWireNode("b_reason",u4,origin),new HwConstantNode("one1",u1,"1",origin),new HwConstantNode("zero3",u3,"000",origin),new HwOperationNode("b_reason_wide","zext",["b_reason"],u8,Origin:origin),new HwMuxNode("defined_result","a_accepted","b_defined","one1",u1,origin),new HwOperationNode("accepted_result","and",["a_accepted","b_accepted"],u1,Origin:origin),new HwMuxNode("lane_result","accepted_result","b_lane","zero3",u3,origin),new HwMuxNode("reason_result","a_accepted","b_reason_wide","a_reason",u8,origin),new HwOutputDriveNode("drive_defined","defined","defined_result",origin),new HwOutputDriveNode("drive_accepted","accepted","accepted_result",origin),new HwOutputDriveNode("drive_lane","selected_lane","lane_result",origin),new HwOutputDriveNode("drive_reason","reject_reason","reason_result",origin),new HwOutputDriveNode("drive_stage_b","stage_b_evaluated","a_accepted",origin)};
        var aConnections=a.Ports.ToDictionary(p=>p.Name,p=>p.Name switch{"accepted"=>"a_accepted","reject_reason"=>"a_reason",_=>p.Name},StringComparer.Ordinal);
        var bConnections=b.Ports.ToDictionary(p=>p.Name,p=>p.Name switch{"defined"=>"b_defined","accepted"=>"b_accepted","selected_lane"=>"b_lane","reject_reason"=>"b_reason",_=>p.Name},StringComparer.Ordinal);
        var instances=new[]{new HwInstanceNode("stage_a","stage_a_capacity_projection",aConnections,new Dictionary<string,string>(),Origin:origin),new HwInstanceNode("stage_b","stage_b_lane_materialization_projection",bConnections,new Dictionary<string,string>(),Origin:origin)};
        if(capacitySource is not null)
        {
            nodes.Add(new HwConstantNode("commit_one8",u8,"00000001",origin));string laneMask="commit_zero8";nodes.Add(new HwConstantNode(laneMask,u8,"00000000",origin));
            for(int i=0;i<8;i++){string p="commit_lane"+i+"_";nodes.Add(new HwConstantNode(p+"id",u3,Convert.ToString(i,2).PadLeft(3,'0'),origin));nodes.Add(new HwConstantNode(p+"mask",u8,Convert.ToString(1<<i,2).PadLeft(8,'0'),origin));nodes.Add(new HwOperationNode(p+"match","eq",["b_lane",p+"id"],u1,Origin:origin));nodes.Add(new HwMuxNode(p+"select",p+"match",p+"mask",laneMask,u8,origin));laneMask=p+"select";}
            nodes.Add(new HwOperationNode("commit_occupancy_set","or",["occupancy",laneMask],u8,Origin:origin));nodes.Add(new HwMuxNode("commit_occupancy_next","accepted_result","commit_occupancy_set","occupancy",u8,origin));nodes.Add(new HwOutputDriveNode("drive_next_occupancy","next_occupancy","commit_occupancy_next",origin));
            for(int i=0;i<6;i++){string p="commit_class"+i+"_";nodes.Add(new HwConstantNode(p+"id",u8,Convert.ToString(i,2).PadLeft(8,'0'),origin));nodes.Add(new HwOperationNode(p+"match","eq",["slot_class",p+"id"],u1,Origin:origin));nodes.Add(new HwOperationNode(p+"enable","and",["accepted_result",p+"match"],u1,Origin:origin));nodes.Add(new HwOperationNode(p+"increment","add",["occupied"+i,"commit_one8"],u8,Origin:origin));nodes.Add(new HwMuxNode(p+"next",p+"enable",p+"increment","occupied"+i,u8,origin));nodes.Add(new HwOutputDriveNode(p+"drive","next_occupied"+i,p+"next",origin));}
        }
        var top=new HwModule("smt_candidate_admission_projection","smt_candidate_admission_projection",ports,nodes,instances,new HwHardwareContract("scheduler.smt-candidate-admission.unqualified.v1",HwModuleKind.Combinational,ports,new HwTimingContract(0,1)),origin);
        var modules=stageA.Design.Modules.Concat(stageB.Design.Modules).ToArray();if(modules.Select(m=>m.Id).Distinct(StringComparer.Ordinal).Count()!=modules.Length)throw new InvalidDataException("Duplicate child modules.");
        var result=new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",top.Id,[top,..modules],stageA.Design.Toolchain,[]));return result.Validated??throw new InvalidDataException("Admission decision HWIR refused: "+string.Join("; ",result.Diagnostics.Select(d=>d.Message)));
    }
}
#endif
