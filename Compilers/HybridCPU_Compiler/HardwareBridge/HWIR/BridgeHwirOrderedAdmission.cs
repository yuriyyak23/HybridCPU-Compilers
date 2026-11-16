#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Composes pressure ordering and four admissions for an already collected prefix.</summary>
public static class BridgeHwirOrderedAdmission
{
    public static HwValidatedDesign Compile(HwValidatedDesign order, HwValidatedDesign admission, HwValidatedDesign guardPlane, bool perAttemptGates=false)
    {
        if(order.Design.TopModule!="smt_pressure_order_projection"||admission.Design.TopModule!="smt_four_candidate_projection"||guardPlane.Design.TopModule!="smt_legality_guard_plane_projection"||order.Design.Toolchain!=admission.Design.Toolchain||order.Design.Toolchain!=guardPlane.Design.Toolchain)throw new InvalidDataException("Ordered admission child identity differs.");
        var sort=order.Design.Modules.Single(m=>m.Id==order.Design.TopModule);var four=admission.Design.Modules.Single(m=>m.Id==admission.Design.TopModule);
        var original=sort.Origin??throw new InvalidDataException("Ordering source origin missing.");string digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((perAttemptGates?"ordered-admission-attempt-gates/v1|":"ordered-admission/v1|")+order.CanonicalDigest+"|"+admission.CanonicalDigest))).ToLowerInvariant();var origin=original with{IdentityDigest=digest};
        var u1=new HwBitVectorType(1);var u3=new HwBitVectorType(3);var u64=new HwBitVectorType(64);var ports=new List<HwPortContract>();var nodes=new List<HwNode>();var instances=new List<HwInstanceNode>();
        ports.Add(new("sort_enabled",HwPortDirection.Input,u1));ports.Add(new("count",HwPortDirection.Input,u3));ports.Add(new("valid",HwPortDirection.Output,u1));ports.Add(new("swap_count",HwPortDirection.Output,u3));
        for(int i=0;i<4;i++){ports.Add(new("pressure"+i,HwPortDirection.Input,new HwBitVectorType(6)));ports.Add(new("ordered_id"+i,HwPortDirection.Output,u3));}
        foreach(var p in four.Ports.Where(p=>p.Direction==HwPortDirection.Input&&!(p.Name.Length>3&&p.Name[0]=='c'&&char.IsAsciiDigit(p.Name[1])&&p.Name[2]=='_')))ports.Add(p);
        foreach(var p in four.Ports.Where(p=>p.Direction==HwPortDirection.Output&&p.Name!="valid_all"))ports.Add(p);
        var guard=guardPlane.Design.Modules.Single(m=>m.Id==guardPlane.Design.TopModule);
        string[] attemptFields=["phase_key_valid","phase_template_matches","phase_shared_low","phase_shared_high","phase_vt0_mask","phase_vt1_mask","phase_vt2_mask","phase_vt3_mask"];
        var candidateFields=four.Ports.Where(p=>p.Direction==HwPortDirection.Input&&p.Name.StartsWith("c0_",StringComparison.Ordinal)&&p.Name!="c0_present"&&(!perAttemptGates||!attemptFields.Contains(p.Name[3..],StringComparer.Ordinal))).Select(p=>p.Name[3..]).ToArray();
        for(int i=0;i<4;i++)foreach(var field in candidateFields){var p=four.Ports.Single(p=>p.Name=="c0_"+field);ports.Add(p with{Name="input"+i+"_"+field});}
        if(perAttemptGates)for(int i=0;i<4;i++)foreach(var field in attemptFields){var p=four.Ports.Single(p=>p.Name=="c0_"+field);ports.Add(p with{Name="attempt"+i+"_"+field});}
        nodes.Add(new HwConstantNode("zero3",u3,"000",origin));nodes.Add(new HwOperationNode("count64","zext",["count"],u64,Origin:origin));for(int i=0;i<4;i++){nodes.Add(new HwConstantNode("slot"+i,u3,Convert.ToString(i,2).PadLeft(3,'0'),origin));nodes.Add(new HwConstantNode("slot64_"+i,u64,Convert.ToString(i,2).PadLeft(64,'0'),origin));}
        var sortConnections=new Dictionary<string,string>();foreach(var p in sort.Ports){string signal=p.Name switch{"enabled"=>"sort_enabled","count"=>"count",_ when p.Name.StartsWith("id",StringComparison.Ordinal)=>"slot"+p.Name[2..],_=>p.Name};if(p.Direction==HwPortDirection.Output){signal="sort_"+p.Name;nodes.Add(new HwWireNode(signal,p.Type,origin));if(p.Name=="swap_count"||p.Name.StartsWith("ordered_id",StringComparison.Ordinal))nodes.Add(new HwOutputDriveNode("drive_"+p.Name,p.Name,signal,origin));}sortConnections.Add(p.Name,signal);}instances.Add(new HwInstanceNode("pressure_order",sort.Id,sortConnections,new Dictionary<string,string>(),Origin:origin));
        var admissionConnections=new Dictionary<string,string>();foreach(var p in four.Ports){string signal=p.Name;if(p.Direction==HwPortDirection.Output){signal="admission_"+p.Name;nodes.Add(new HwWireNode(signal,p.Type,origin));if(p.Name!="valid_all")nodes.Add(new HwOutputDriveNode("drive_"+p.Name,p.Name,signal,origin));}else if(p.Name.Length>3&&p.Name[0]=='c'&&char.IsAsciiDigit(p.Name[1])&&p.Name[2]=='_'){int position=p.Name[1]-'0';string field=p.Name[3..];if(field=="present"){signal="present"+position;nodes.Add(new HwOperationNode(signal,"ult64",["slot64_"+position,"count64"],u1,Origin:origin));}else if(perAttemptGates&&attemptFields.Contains(field,StringComparer.Ordinal))signal="attempt"+position+"_"+field;else if(perAttemptGates&&field=="legality_allowed")signal=$"attempt{position}_guard_legality_allowed";else{signal="select"+position+"_"+field;string selected="input0_"+field;for(int source=1;source<4;source++){string q=$"mux{position}_{field}_{source}";nodes.Add(new HwOperationNode(q+"_match","eq",["sort_ordered_id"+position,"slot"+source],u1,Origin:origin));nodes.Add(new HwMuxNode(q,q+"_match","input"+source+"_"+field,selected,p.Type,origin));selected=q;}nodes.Add(new HwMuxNode(signal,"sort_valid",selected,"input0_"+field,p.Type,origin));}}admissionConnections.Add(p.Name,signal);}instances.Add(new HwInstanceNode("four_admissions",four.Id,admissionConnections,new Dictionary<string,string>(),Origin:origin));
        nodes.Add(new HwOperationNode("valid_result","and",["sort_valid","admission_valid_all"],u1,Origin:origin));nodes.Add(new HwOutputDriveNode("drive_valid","valid","valid_result",origin));
        var modules=order.Design.Modules.Concat(admission.Design.Modules).Concat(perAttemptGates?guardPlane.Design.Modules:[]).DistinctBy(m=>m.Id,StringComparer.Ordinal).ToArray();var top=new HwModule("smt_ordered_admission_projection","smt_ordered_admission_projection",ports,nodes,instances,new HwHardwareContract("scheduler.smt-ordered-admission.unqualified.v1",HwModuleKind.Combinational,ports,new HwTimingContract(0,1)),origin);var result=new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",top.Id,[top,..modules],order.Design.Toolchain,[]));return result.Validated??throw new InvalidDataException("Ordered admission HWIR refused: "+string.Join("; ",result.Diagnostics.Select(d=>d.Code+" "+d.Message)));
    }
}
#endif
