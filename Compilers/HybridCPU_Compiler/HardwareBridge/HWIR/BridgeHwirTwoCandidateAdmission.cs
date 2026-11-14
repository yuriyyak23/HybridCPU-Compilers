#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Ordered candidate projections; unmodeled legality and side effects are supplied externally.</summary>
public static class BridgeHwirTwoCandidateAdmission
{
    public static HwValidatedDesign Compile(byte[] smt,byte[] scheduler,string[] hashes,HwValidatedDesign candidate,string commit)
        => CompileOrdered(smt,scheduler,hashes,candidate,commit,2,false);

    /// <summary>Four ordered candidate positions with explicit presence; not a complete SMT scheduler.</summary>
    public static HwValidatedDesign CompileFour(byte[] smt,byte[] scheduler,string[] hashes,HwValidatedDesign candidate,string commit,bool trackVt=false)
        => CompileOrdered(smt,scheduler,hashes,candidate,commit,4,trackVt);

    private static HwValidatedDesign CompileOrdered(byte[] smt,byte[] scheduler,string[] hashes,HwValidatedDesign candidate,string commit,int count,bool trackVt)
    {
        static string Hash(byte[] b)=>Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        if(hashes.Length!=2||Hash(smt)!=hashes[0]||Hash(scheduler)!=hashes[1]||smt.Length>2_000_000||scheduler.Length>2_000_000||candidate.Design.TopModule!="smt_candidate_admission_projection"||commit.Length!=40||commit.Any(c=>!Uri.IsHexDigit(c)))throw new InvalidDataException("Two-candidate source identity differs.");
        static SyntaxNode Parse(byte[] b){var tree=CSharpSyntaxTree.ParseText(new UTF8Encoding(false,true).GetString(b));if(tree.GetDiagnostics().Any(d=>d.Severity==DiagnosticSeverity.Error))throw new InvalidDataException("Two-candidate source contains syntax errors.");return tree.GetRoot();}
        static bool Same(SyntaxNode a,SyntaxNode b)=>a.DescendantTokens().Select(t=>t.Text).SequenceEqual(b.DescendantTokens().Select(t=>t.Text));
        var root=Parse(smt);var owner=root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c=>c.Identifier.ValueText=="MicroOpScheduler"&&c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString()=="YAKSys_Hybrid_CPU.Core");
        var method=owner.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="PackBundleIntraCoreSmt"&&m.ParameterList.Parameters.Count==5);
        var loop=method.DescendantNodes().OfType<ForStatementSyntax>().Single(f=>f.Declaration?.Variables.SingleOrDefault()?.Identifier.ValueText=="i"&&f.Condition?.ToString()=="i < legalCount");
        var init=((LocalDeclarationStatementSyntax)SyntaxFactory.ParseStatement("int i = 0;")).Declaration;
        if(loop.Declaration is null||!Same(loop.Declaration,init)||loop.Incrementors.Count!=1||!Same(loop.Incrementors[0],SyntaxFactory.ParseExpression("i++"))||
           loop.Statement is not BlockSyntax body||body.Statements.Count!=7||
           !Same(body.Statements[0],SyntaxFactory.ParseStatement("int vt = legalVts[i];"))||
           !Same(body.Statements[1],SyntaxFactory.ParseStatement("MicroOp candidate = _smtPorts[vt];"))||
           !Same(body.Statements[2],SyntaxFactory.ParseStatement("if (candidate == null) continue;"))||
           body.Statements[3] is not IfStatementSyntax typed||typed.Condition.ToString()!="TypedSlotEnabled"||
           !Same(body.Statements[4],SyntaxFactory.ParseStatement("_smtPorts[vt] = null;"))||
           !Same(body.Statements[5],SyntaxFactory.ParseStatement("_smtPortValid[vt] = false;"))||
           !Same(body.Statements[6],SyntaxFactory.ParseStatement("nominationState = nominationState.WithoutCandidate(vt);")))
            throw new InvalidDataException("SMT candidate iteration structure changed; re-establish the projection boundary.");
        // The projection assumes this complete typed branch, including the order of
        // updates and externally supplied side effects. Individual statement searches
        // are insufficient: an inserted continue or a reordered update changes the
        // behavior while leaving every searched expression present.
        const string expectedTypedBranch="""
        {
            if (!TryClassAdmission(candidate, ref bundleCert, bundleMetadata, boundaryGuard, ref templateState, ownerVirtualThreadId,
                                   realInjectedCount, out TypedSlotRejectReason rejectA))
            { RecordTypedSlotReject(rejectA, candidate); continue; }
            if (!TryMaterializeLane(candidate, bundleOccupancy, out int lane, out TypedSlotRejectReason rejectB))
            { RecordTypedSlotReject(rejectB, candidate); continue; }
            result[lane] = candidate;
            MaterializePostStageBIssuedAttempt(candidate, lane, bundleMetadata);
            candidate.IsFspInjected = true;
            bundleCert.AddOperation(candidate);
            opportunityState = opportunityState.WithOccupiedSlot(lane);
            bundleOccupancy |= (byte)(1 << lane);
            SmtInjectionsCount++;
            realInjectedCount++;
            wasInjected[vt] = true;
            _perVtInjections[vt]++;
            RecordTypedSlotInject(candidate, lane);
            bundleMetadata = bundleMetadata.WithOperation(candidate);
            boundaryGuard = boundaryGuard.WithOperation(candidate);
            _runtimeLegalityService.RefreshSmtAfterMutation(_currentReplayPhase, bundleCert, bundleMetadata, boundaryGuard);
            _classCapacity.IncrementOccupancy(candidate.Placement.RequiredSlotClass);
            ConsumeProjectedMemoryIssueStateIfNeeded(candidate);
            if (candidate.Placement.PinningKind == SlotPinningKind.ClassFlexible)
            { RecordPhaseLane(candidate.Placement.RequiredSlotClass, lane); }
            if (_currentReplayPhase.IsActive && !_classTemplateValid)
            { CaptureClassTemplate(templateDomainScopeId); }
        }
        """;
        if(typed.Statement.DescendantTrivia(descendIntoTrivia:true).Any(t=>t.IsDirective)||!Same(typed.Statement,SyntaxFactory.ParseStatement(expectedTypedBranch)))
            throw new InvalidDataException("SMT typed candidate branch changed; re-establish its full control flow before projection.");
        var schedulerRoot=Parse(scheduler);
        if(count==4){var schedulerOwner=schedulerRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c=>c.Identifier.ValueText=="MicroOpScheduler"&&c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString()=="YAKSys_Hybrid_CPU.Core");
            var ways=schedulerOwner.Members.OfType<FieldDeclarationSyntax>().SingleOrDefault(f=>f.Declaration.Variables.Any(v=>v.Identifier.ValueText=="SMT_WAYS"));
            if(ways is null||!Same(ways,SyntaxFactory.ParseMemberDeclaration("private const int SMT_WAYS = 4;")!))throw new InvalidDataException("SMT width differs from four.");
            foreach(string field in new[]{"private readonly MicroOp?[] _smtPorts = new MicroOp?[SMT_WAYS];","private readonly bool[] _smtPortValid = new bool[SMT_WAYS];"})
                if(schedulerOwner.Members.OfType<FieldDeclarationSyntax>().Count(f=>Same(f,SyntaxFactory.ParseMemberDeclaration(field)!))!=1)throw new InvalidDataException("SMT nomination port shape changed.");}
        var record=schedulerRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="RecordPhaseLane");
        if(record.Body is null||!Same(record.Body,SyntaxFactory.ParseStatement("{ byte idx = (byte)slotClass; if (idx < 8) _previousPhaseLane[idx] = lane; }")))throw new InvalidDataException("Replay lane record changed.");
        var child=candidate.Design.Modules.Single(m=>m.Id==candidate.Design.TopModule);if(!child.Ports.Any(p=>p.Name=="next_occupancy")||!child.Ports.Any(p=>p.Name=="next_occupied5"))throw new InvalidDataException("Admission state updates missing.");
        var pos=method.Identifier.GetLocation().GetLineSpan().StartLinePosition;var origin=new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2",commit,"HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/Smt/MicroOpScheduler.SMT.cs",count==2?"CircuitWeave.TwoCandidate.Projection":"CircuitWeave.FourCandidate.Projection","YAKSys_Hybrid_CPU.Core.MicroOpScheduler","PackBundleIntraCoreSmt",Hash(Encoding.UTF8.GetBytes((count==2?"two-candidate/v1|":trackVt?"four-candidate-vt/v1|":"four-candidate/v1|")+string.Join("|",hashes)+"|"+candidate.CanonicalDigest)),HwSourceRole.Primary,pos.Line+1,pos.Character+1);
        var u1=new HwBitVectorType(1);var u8=new HwBitVectorType(8);var u32=new HwBitVectorType(32);
        string[] shared=["template_hit","replay_active","stable_donor","donor_mask"];
        string[] state=["occupancy","current_injections","occupied0","occupied1","occupied2","occupied3","occupied4","occupied5"];
        string[] budgets=Enumerable.Range(0,8).Select(i=>"budget"+i).ToArray();string[] previous=Enumerable.Range(0,8).Select(i=>"previous"+i).ToArray();
        var ports=new List<HwPortContract>();var nodes=new List<HwNode>();var instances=new List<HwInstanceNode>();
        foreach(var p in child.Ports.Where(p=>p.Direction==HwPortDirection.Input&&shared.Contains(p.Name)))ports.Add(p);
        foreach(var p in child.Ports.Where(p=>p.Direction==HwPortDirection.Input&&(state.Contains(p.Name)||p.Name.StartsWith("total",StringComparison.Ordinal))))ports.Add(p);
        foreach(var name in budgets)nodes.Add(new HwConstantNode("state_id_"+name,u8,Convert.ToString(int.Parse(name[6..]),2).PadLeft(8,'0'),origin));
        foreach(var name in budgets)ports.Add(new(name,HwPortDirection.Input,u8));foreach(var name in previous)ports.Add(new(name,HwPortDirection.Input,u32));
        string[] perCandidate=["slot_class","legality_allowed","is_assist","outer_allowed","outer_reason","pinning_kind","pinned_lane","permitted_lanes","opportunistic"];
        for(int i=0;i<count;i++){if(count==4)ports.Add(new("c"+i+"_present",HwPortDirection.Input,u1));if(trackVt)ports.Add(new("c"+i+"_vt",HwPortDirection.Input,new HwBitVectorType(2)));foreach(var p in child.Ports.Where(p=>p.Direction==HwPortDirection.Input&&perCandidate.Contains(p.Name)))ports.Add(p with{Name="c"+i+"_"+p.Name});}
        ports.Add(new(count==2?"valid_pair":"valid_all",HwPortDirection.Output,u1));for(int i=0;i<count;i++)foreach(var p in child.Ports.Where(p=>p.Direction==HwPortDirection.Output&&p.Name is "defined" or "accepted" or "selected_lane" or "reject_reason" or "stage_b_evaluated"))ports.Add(p with{Name="c"+i+"_"+p.Name});
        foreach(var p in child.Ports.Where(p=>p.Direction==HwPortDirection.Output&&(p.Name=="next_occupancy"||p.Name.StartsWith("next_occupied",StringComparison.Ordinal))))ports.Add(p with{Name="final_"+p.Name});
        foreach(var name in budgets)ports.Add(new("final_"+name,HwPortDirection.Output,u8));foreach(var name in previous)ports.Add(new("final_"+name,HwPortDirection.Output,u32));ports.Add(new("final_injections",HwPortDirection.Output,u32));
        nodes.Add(new HwConstantNode("one32",u32,Convert.ToString(1,2).PadLeft(32,'0'),origin));nodes.Add(new HwConstantNode("zero8",u8,"00000000",origin));
        if(count==4){nodes.Add(new HwConstantNode("zero1",u1,"0",origin));nodes.Add(new HwConstantNode("one1",u1,"1",origin));nodes.Add(new HwConstantNode("zero3",new HwBitVectorType(3),"000",origin));}
        string[] currentBudgets=budgets.ToArray(),currentPrevious=previous.ToArray();string currentOccupancy="occupancy",currentCount="current_injections";string[] currentUsed=Enumerable.Range(0,6).Select(i=>"occupied"+i).ToArray();
        for(int c=0;c<count;c++)
        {
            string prefix="c"+c+"_", selectedBudget=prefix+"zero_budget",selectedPrevious=prefix+"minus_previous";nodes.Add(new HwConstantNode(selectedBudget,u8,"00000000",origin));nodes.Add(new HwConstantNode(selectedPrevious,u32,new string('1',32),origin));
            string active=prefix+"present";if(trackVt)for(int prior=0;prior<c;prior++){string q=prefix+"prior"+prior+"_";nodes.Add(new HwOperationNode(q+"same","eq",[prefix+"vt","c"+prior+"_vt"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"consumed","and",[q+"same","c"+prior+"_gated_accepted"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"available","xor",[q+"consumed","one1"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"active","and",[active,q+"available"],u1,Origin:origin));active=q+"active";}
            for(int i=0;i<8;i++){string q=prefix+"class"+i+"_";nodes.Add(new HwConstantNode(q+"id",u8,Convert.ToString(i,2).PadLeft(8,'0'),origin));nodes.Add(new HwOperationNode(q+"match","eq",[prefix+"slot_class",q+"id"],u1,Origin:origin));nodes.Add(new HwMuxNode(q+"budget",q+"match",currentBudgets[i],selectedBudget,u8,origin));nodes.Add(new HwMuxNode(q+"previous",q+"match",currentPrevious[i],selectedPrevious,u32,origin));selectedBudget=q+"budget";selectedPrevious=q+"previous";}
            var connections=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var p in child.Ports)
            {
                string signal;
                if(p.Direction==HwPortDirection.Input)signal=p.Name switch{"template_remaining"=>selectedBudget,"previous_lane"=>selectedPrevious,"occupancy"=>currentOccupancy,"current_injections"=>currentCount,_ when p.Name.StartsWith("occupied",StringComparison.Ordinal)=>currentUsed[int.Parse(p.Name[8..])],_ when perCandidate.Contains(p.Name)=>prefix+p.Name,_=>p.Name};
                else if(p.Name is "defined" or "accepted" or "selected_lane" or "reject_reason" or "stage_b_evaluated"){signal=prefix+"wire_"+p.Name;nodes.Add(new HwWireNode(signal,p.Type,origin));string output=signal;if(count==4){output=prefix+"gated_"+p.Name;string absent=p.Name=="defined"?"one1":p.Type.Width switch{1=>"zero1",3=>"zero3",8=>"zero8",_=>throw new InvalidDataException("Unexpected decision output width.")};nodes.Add(new HwMuxNode(output,active,signal,absent,p.Type,origin));}nodes.Add(new HwOutputDriveNode(prefix+"drive_"+p.Name,prefix+p.Name,output,origin));}
                else {signal=prefix+"wire_"+p.Name;nodes.Add(new HwWireNode(signal,p.Type,origin));}
                connections.Add(p.Name,signal);
            }
            instances.Add(new HwInstanceNode(prefix+"candidate","smt_candidate_admission_projection",connections,new Dictionary<string,string>(),Origin:origin));
            string accepted=prefix+(count==4?"gated_accepted":"wire_accepted");nodes.Add(new HwOperationNode(prefix+"count_inc","add",[currentCount,"one32"],u32,Origin:origin));nodes.Add(new HwMuxNode(prefix+"count_next",accepted,prefix+"count_inc",currentCount,u32,origin));currentCount=prefix+"count_next";
            if(count==4){nodes.Add(new HwMuxNode(prefix+"gated_next_occupancy",active,prefix+"wire_next_occupancy",currentOccupancy,u8,origin));currentOccupancy=prefix+"gated_next_occupancy";for(int i=0;i<6;i++){nodes.Add(new HwMuxNode(prefix+"gated_next_occupied"+i,active,prefix+"wire_next_occupied"+i,currentUsed[i],u8,origin));currentUsed[i]=prefix+"gated_next_occupied"+i;}nodes.Add(new HwOperationNode(prefix+"gated_fast_consumed","and",[active,prefix+"wire_fast_consumed"],u1,Origin:origin));}else{currentOccupancy=prefix+"wire_next_occupancy";for(int i=0;i<6;i++)currentUsed[i]=prefix+"wire_next_occupied"+i;}
            nodes.Add(new HwOperationNode(prefix+"flexible","eq",[prefix+"pinning_kind","zero8"],u1,Origin:origin));nodes.Add(new HwOperationNode(prefix+"record","and",[accepted,prefix+"flexible"],u1,Origin:origin));nodes.Add(new HwOperationNode(prefix+"lane32","zext",[prefix+"wire_selected_lane"],u32,Origin:origin));
            for(int i=0;i<8;i++){string q=prefix+"class"+i+"_";nodes.Add(new HwOperationNode(q+"budget_write","and",[q+"match",prefix+(count==4?"gated_fast_consumed":"wire_fast_consumed")],u1,Origin:origin));nodes.Add(new HwMuxNode(q+"budget_next",q+"budget_write",prefix+"wire_template_next",currentBudgets[i],u8,origin));nodes.Add(new HwOperationNode(q+"record","and",[prefix+"record",q+"match"],u1,Origin:origin));nodes.Add(new HwMuxNode(q+"previous_next",q+"record",prefix+"lane32",currentPrevious[i],u32,origin));currentBudgets[i]=q+"budget_next";currentPrevious[i]=q+"previous_next";}
        }
        if(count==2){nodes.Add(new HwOperationNode("valid_pair_result","and",["c0_wire_defined","c1_wire_defined"],u1,Origin:origin));nodes.Add(new HwOutputDriveNode("drive_valid_pair","valid_pair","valid_pair_result",origin));}
        else{string valid="c0_gated_defined";for(int i=1;i<count;i++){string next="valid_through_"+i;nodes.Add(new HwOperationNode(next,"and",[valid,"c"+i+"_gated_defined"],u1,Origin:origin));valid=next;}nodes.Add(new HwOutputDriveNode("drive_valid_all","valid_all",valid,origin));}
        nodes.Add(new HwOutputDriveNode("drive_final_occupancy","final_next_occupancy",currentOccupancy,origin));for(int i=0;i<6;i++)nodes.Add(new HwOutputDriveNode("drive_final_used"+i,"final_next_occupied"+i,currentUsed[i],origin));
        for(int i=0;i<8;i++){nodes.Add(new HwOutputDriveNode("drive_final_budget"+i,"final_budget"+i,currentBudgets[i],origin));nodes.Add(new HwOutputDriveNode("drive_final_previous"+i,"final_previous"+i,currentPrevious[i],origin));}nodes.Add(new HwOutputDriveNode("drive_final_count","final_injections",currentCount,origin));
        string topName=count==2?"smt_two_candidate_projection":"smt_four_candidate_projection";var top=new HwModule(topName,topName,ports,nodes,instances,new HwHardwareContract(count==2?"scheduler.smt-two-candidate.unqualified.v1":"scheduler.smt-four-candidate.unqualified.v1",HwModuleKind.Combinational,ports,new HwTimingContract(0,1)),origin);
        var result=new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",top.Id,[top,..candidate.Design.Modules],candidate.Design.Toolchain,[]));return result.Validated??throw new InvalidDataException("Two-candidate HWIR refused: "+string.Join("; ",result.Diagnostics.Select(d=>d.Message)));
    }
}
#endif
