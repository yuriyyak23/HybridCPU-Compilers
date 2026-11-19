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
    public static HwValidatedDesign CompileFour(byte[] smt,byte[] scheduler,byte[] slotClassCapacity,byte[] slotClassDefinitions,string[] hashes,HwValidatedDesign candidate,string commit,bool trackVt=false)
        => CompileOrdered(smt,scheduler,hashes,candidate,commit,4,trackVt,slotClassCapacity,slotClassDefinitions);

    private static HwValidatedDesign CompileOrdered(byte[] smt,byte[] scheduler,string[] hashes,HwValidatedDesign candidate,string commit,int count,bool trackVt,byte[]? slotClassCapacity=null,byte[]? slotClassDefinitions=null)
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
        if(count==4){
            if(slotClassCapacity is null||slotClassDefinitions is null||Hash(slotClassCapacity)!="54dcc1597997bab5fbea400c2f2ab61f2da750b29db5592e2048a05adc49ade1"||Hash(slotClassDefinitions)!="489edf25fe528393aba2e298c259e7929f59260430b4946ccea4c713065d9ff1")throw new InvalidDataException("Typed class occupancy source bytes changed.");
            var capacityRoot=Parse(slotClassCapacity);var capacityType=capacityRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(x=>x.Identifier.ValueText=="SlotClassCapacity");
            var compute=capacityType.Members.OfType<MethodDeclarationSyntax>().Single(x=>x.Identifier.ValueText=="ComputeFromBundle");string computeTokens=string.Concat(compute.DescendantTokens().Select(t=>t.Text));
            foreach(string fact in new[]{"state.InitializeFromLaneMap()","for(inti=0;i<limit;i++)","if(bundle[i]isnotnull)","state.IncrementOccupancy(bundle[i]!.Placement.RequiredSlotClass)"})if(!computeTokens.Contains(fact,StringComparison.Ordinal))throw new InvalidDataException("SlotClassCapacity.ComputeFromBundle contract changed: "+fact);
            var definitionsRoot=Parse(slotClassDefinitions);var laneMap=definitionsRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(x=>x.Identifier.ValueText=="SlotClassLaneMap");var getCapacity=laneMap.Members.OfType<MethodDeclarationSyntax>().Single(x=>x.Identifier.ValueText=="GetClassCapacity");
            if(!string.Concat(getCapacity.DescendantTokens().Select(t=>t.Text)).Contains("returnBitOperations.PopCount(GetLaneMask(slotClass));",StringComparison.Ordinal))throw new InvalidDataException("SlotClassLaneMap class-capacity derivation changed.");
            var schedulerOwner=schedulerRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c=>c.Identifier.ValueText=="MicroOpScheduler"&&c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString()=="YAKSys_Hybrid_CPU.Core");
            var ways=schedulerOwner.Members.OfType<FieldDeclarationSyntax>().SingleOrDefault(f=>f.Declaration.Variables.Any(v=>v.Identifier.ValueText=="SMT_WAYS"));
            if(ways is null||!Same(ways,SyntaxFactory.ParseMemberDeclaration("private const int SMT_WAYS = 4;")!))throw new InvalidDataException("SMT width differs from four.");
            var scoreboardSlots=schedulerOwner.Members.OfType<FieldDeclarationSyntax>().SingleOrDefault(f=>f.Declaration.Variables.Any(v=>v.Identifier.ValueText=="SCOREBOARD_SLOTS"));
            if(scoreboardSlots is null||!Same(scoreboardSlots,SyntaxFactory.ParseMemberDeclaration("private const int SCOREBOARD_SLOTS = 8;")!))throw new InvalidDataException("Projected scoreboard count saturation width differs from eight.");
            foreach(string field in new[]{"private readonly MicroOp?[] _smtPorts = new MicroOp?[SMT_WAYS];","private readonly bool[] _smtPortValid = new bool[SMT_WAYS];"})
                if(schedulerOwner.Members.OfType<FieldDeclarationSyntax>().Count(f=>Same(f,SyntaxFactory.ParseMemberDeclaration(field)!))!=1)throw new InvalidDataException("SMT nomination port shape changed.");
            var opportunityState=schedulerOwner.Members.OfType<StructDeclarationSyntax>().SingleOrDefault(s=>s.Identifier.ValueText=="BundleOpportunityState");
            var create=opportunityState?.Members.OfType<MethodDeclarationSyntax>().SingleOrDefault(m=>m.Identifier.ValueText=="Create"&&m.Modifiers.Any(SyntaxKind.StaticKeyword));
            var createBody=create?.Body;
            string createTokens=createBody is null?string.Empty:string.Concat(createBody.DescendantTokens().Select(t=>t.Text));
            string[] orderedCreateFacts=["Math.Min(bundle?.Count??0,8)","slotOpisnull","emptySlotMask|=slotBit","referenceOpportunityMask|=slotBit","occupancyMask|=slotBit","slotOpisNopMicroOp","slotOp.AdmissionMetadata.IsStealable","referenceOpportunityMask|=slotBit","newBundleOpportunityState(occupancyMask,emptySlotMask,referenceOpportunityMask)"];
            int lastCreateFact=-1;foreach(string fact in orderedCreateFacts){int next=createTokens.IndexOf(fact,lastCreateFact+1,StringComparison.Ordinal);if(next<=lastCreateFact)throw new InvalidDataException("BundleOpportunityState.Create source ordering changed: "+fact);lastCreateFact=next;}
            var withOccupied=opportunityState!.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="WithOccupiedSlot");
            string withOccupiedTokens=string.Concat(withOccupied.Body!.DescendantTokens().Select(t=>t.Text));
            if(!withOccupiedTokens.Contains("OccupancyMask|slotBit",StringComparison.Ordinal)||!withOccupiedTokens.Contains("EmptySlotMask&~slotBit",StringComparison.Ordinal)||!withOccupiedTokens.Contains("ReferenceOpportunityMask&~slotBit",StringComparison.Ordinal))throw new InvalidDataException("BundleOpportunityState commit masks changed.");}
        var record=schedulerRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="RecordPhaseLane");
        if(record.Body is null||!Same(record.Body,SyntaxFactory.ParseStatement("{ byte idx = (byte)slotClass; if (idx < 8) _previousPhaseLane[idx] = lane; }")))throw new InvalidDataException("Replay lane record changed.");
        var child=candidate.Design.Modules.Single(m=>m.Id==candidate.Design.TopModule);if(!child.Ports.Any(p=>p.Name=="next_occupancy")||!child.Ports.Any(p=>p.Name=="next_occupied5"))throw new InvalidDataException("Admission state updates missing.");
        var pos=method.Identifier.GetLocation().GetLineSpan().StartLinePosition;string dependencyIdentity=string.Join("|",hashes)+(count==4?"|"+Hash(slotClassCapacity!)+"|"+Hash(slotClassDefinitions!):"");var origin=new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2",commit,"HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/Smt/MicroOpScheduler.SMT.cs",count==2?"CircuitWeave.TwoCandidate.Projection":"CircuitWeave.FourCandidate.Projection","YAKSys_Hybrid_CPU.Core.MicroOpScheduler","PackBundleIntraCoreSmt",Hash(Encoding.UTF8.GetBytes((count==2?"two-candidate/v1|":trackVt?"four-candidate-v3|":"four-candidate-v1|")+dependencyIdentity+"|"+candidate.CanonicalDigest)),HwSourceRole.Primary,pos.Line+1,pos.Character+1);
        HwSourceOrigin classCapacityOrigin=origin;
        if(count==4)
        {
            var capRoot=CSharpSyntaxTree.ParseText(new UTF8Encoding(false,true).GetString(slotClassCapacity!)).GetRoot();
            var capMethod=capRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(x=>x.Identifier.ValueText=="SlotClassCapacity").Members.OfType<MethodDeclarationSyntax>().Single(x=>x.Identifier.ValueText=="ComputeFromBundle");
            var capPos=capMethod.Identifier.GetLocation().GetLineSpan().StartLinePosition;
            classCapacityOrigin=new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2",commit,"HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/SlotLegality/SlotClassCapacity.cs","CircuitWeave.BundleClassOccupancyProjection","YAKSys_Hybrid_CPU.Core.SlotClassCapacity","ComputeFromBundle",Hash(Encoding.UTF8.GetBytes(Hash(slotClassCapacity!)+"|"+Hash(slotClassDefinitions!)+"|"+candidate.CanonicalDigest)),HwSourceRole.Primary,capPos.Line+1,capPos.Character+1);
        }
        var u1=new HwBitVectorType(1);var u8=new HwBitVectorType(8);var u32=new HwBitVectorType(32);var u64=new HwBitVectorType(64);
        string[] memoryStateNames=["remaining_memory_issue_budget","remaining_read_issue_budget","remaining_write_issue_budget","consumed_memory_budget_by_bank","consumed_read_budget_by_bank","consumed_write_budget_by_bank"];
        string[] shared=["template_hit","replay_active","stable_donor","donor_mask","boundary_blocked","owner_context_known","owner_context","owner_domain_certificate","kernel_domain_isolation","suppress_lsu","owner_vt","speculation_budget_enabled","speculation_budget","speculation_budget_exhaustion_events","memory_bank_budget_at_least_one_mask","memory_bank_budget_at_least_two_mask","read_bank_budget_at_least_one_mask","read_bank_budget_at_least_two_mask","write_bank_budget_at_least_one_mask","write_bank_budget_at_least_two_mask",..memoryStateNames];
        string[] certificateState=["bundle_shared_low","bundle_shared_high","bundle_vt0_mask","bundle_vt1_mask","bundle_vt2_mask","bundle_vt3_mask"];
        string[] state=["occupancy","current_injections","occupied0","occupied1","occupied2","occupied3","occupied4","occupied5"];
        string[] budgets=Enumerable.Range(0,8).Select(i=>"budget"+i).ToArray();string[] previous=Enumerable.Range(0,8).Select(i=>"previous"+i).ToArray();
        var ports=new List<HwPortContract>();var nodes=new List<HwNode>();var instances=new List<HwInstanceNode>();string[] initialClassCounts=Enumerable.Range(0,6).Select(i=>"bundle_class_count_wire"+i).ToArray();string[] classCapacityWires=Enumerable.Range(0,6).Select(i=>"bundle_class_capacity_wire"+i).ToArray();
        HwModule? classOccupancyModule=null;
        if(count==4)
        {
            var classPorts=new List<HwPortContract>();var classNodes=new List<HwNode>(); int[] classTotals =new[]{4,2,1,1,1,1};
            classNodes.Add(new HwConstantNode("class_zero8",u8,"00000000",classCapacityOrigin));
            for(int slot=0;slot<8;slot++){classPorts.Add(new HwPortContract("slot"+slot+"_present",HwPortDirection.Input,u1));classPorts.Add(new HwPortContract("slot"+slot+"_class",HwPortDirection.Input,new HwBitVectorType(3)));}
            for(int classIndex=0;classIndex<6;classIndex++)
            {
                string acc="class_zero8";classPorts.Add(new HwPortContract("class_count"+classIndex,HwPortDirection.Output,u8));classPorts.Add(new HwPortContract("class_total"+classIndex,HwPortDirection.Output,u8));
                classNodes.Add(new HwConstantNode("class_total_value"+classIndex,u8,Convert.ToString(classTotals[classIndex],2).PadLeft(8,'0'),classCapacityOrigin));
                for(int slot=0;slot<8;slot++)
                {
                    string q=$"slot{slot}_class{classIndex}_";classNodes.Add(new HwConstantNode(q+"id",new HwBitVectorType(3),Convert.ToString(classIndex,2).PadLeft(3,'0'),classCapacityOrigin));classNodes.Add(new HwOperationNode(q+"matches","eq",["slot"+slot+"_class",q+"id"],u1,Origin:classCapacityOrigin));classNodes.Add(new HwOperationNode(q+"present_class","and",["slot"+slot+"_present",q+"matches"],u1,Origin:classCapacityOrigin));classNodes.Add(new HwOperationNode(q+"present_class8","zext",[q+"present_class"],u8,Origin:classCapacityOrigin));string next=$"class_count{classIndex}_after_slot{slot}";classNodes.Add(new HwOperationNode(next,"add",[acc,q+"present_class8"],u8,Origin:classCapacityOrigin));acc=next;
                }
                classNodes.Add(new HwOutputDriveNode("drive_class_count"+classIndex,"class_count"+classIndex,acc,classCapacityOrigin));classNodes.Add(new HwOutputDriveNode("drive_class_total"+classIndex,"class_total"+classIndex,"class_total_value"+classIndex,classCapacityOrigin));
            }
            var classOrigin=classCapacityOrigin with { Assembly="CircuitWeave.BundleClassOccupancyProjection", Type="YAKSys_Hybrid_CPU.Core.SlotClassCapacity", Member="ComputeFromBundle", IdentityDigest=Hash(Encoding.UTF8.GetBytes(Hash(slotClassCapacity!)+"|"+Hash(slotClassDefinitions!)+"|"+candidate.CanonicalDigest)) };
            classOccupancyModule=new HwModule("slot_class_occupancy_from_bundle","slot_class_occupancy_from_bundle",classPorts,classNodes,[],new HwHardwareContract("scheduler.bundle-class-occupancy.unqualified.v1",HwModuleKind.Combinational,classPorts,new HwTimingContract(0,1)),classOrigin);
        }
        foreach(var p in child.Ports.Where(p=>p.Direction==HwPortDirection.Input&&shared.Contains(p.Name)))ports.Add(p);
        ports.Add(new("speculation_budget_exhaustion_events",HwPortDirection.Input,u64));
        ports.Add(new("smt_rejections_count",HwPortDirection.Input,u64));
        foreach(var p in child.Ports.Where(p=>p.Direction==HwPortDirection.Input&&certificateState.Contains(p.Name)))ports.Add(p);
        foreach(var p in child.Ports.Where(p=>p.Direction==HwPortDirection.Input&&((state.Contains(p.Name)&&!(count==4&&(p.Name=="occupancy"||p.Name.StartsWith("occupied",StringComparison.Ordinal))))||p.Name.StartsWith("total",StringComparison.Ordinal)&&count!=4)))ports.Add(p);
        if(count==4){for(int i=0;i<8;i++){ports.Add(new($"bundle_slot{i}_present",HwPortDirection.Input,u1));ports.Add(new($"bundle_slot{i}_class",HwPortDirection.Input,new HwBitVectorType(3)));}ports.Add(new("bundle_is_nop_mask",HwPortDirection.Input,u8));ports.Add(new("bundle_stealable_mask",HwPortDirection.Input,u8));ports.Add(new("initial_occupancy",HwPortDirection.Output,u8));ports.Add(new("initial_empty_slot_mask",HwPortDirection.Output,u8));ports.Add(new("initial_reference_opportunity_mask",HwPortDirection.Output,u8));for(int i=0;i<6;i++)ports.Add(new($"initial_class_occupied{i}",HwPortDirection.Output,u8));ports.Add(new("final_empty_slot_mask",HwPortDirection.Output,u8));ports.Add(new("final_reference_opportunity_mask",HwPortDirection.Output,u8));}
        foreach(string name in budgets)nodes.Add(new HwConstantNode("state_id_"+name,u8,Convert.ToString(int.Parse(name[6..]),2).PadLeft(8,'0'),origin));
        foreach(string name in budgets)ports.Add(new(name,HwPortDirection.Input,u8));foreach(string name in previous)ports.Add(new(name,HwPortDirection.Input,u32));
        for(int vt=0;vt<4;vt++){ports.Add(new($"projected_count_vt{vt}",HwPortDirection.Input,u8));ports.Add(new($"projected_capacity_vt{vt}",HwPortDirection.Input,u8));}
        string[] perCandidate=["slot_class","owner_context_id","domain_tag","candidate_shared_low","candidate_shared_high","candidate_register_hazard_mask","phase_key_valid","phase_template_matches","phase_shared_low","phase_shared_high","phase_vt0_mask","phase_vt1_mask","phase_vt2_mask","phase_vt3_mask","is_assist","is_memory","is_loadstore","is_load","is_store","bank_id","bank_pending","bundle_store_bank_pending","outer_gate_condition_facts","pinning_kind","pinned_lane","permitted_lanes","opportunistic"];
        string[] decisionOutputs=["defined","accepted","selected_lane","reject_reason","stage_b_evaluated","certificate_reject_detail","speculation_budget_exhaustion_event","smt_rejection_event"];
        for(int i=0;i<count;i++){if(count==4)ports.Add(new("c"+i+"_present",HwPortDirection.Input,u1));if(trackVt)ports.Add(new("c"+i+"_vt",HwPortDirection.Input,new HwBitVectorType(2)));foreach(var p in child.Ports.Where(p=>p.Direction==HwPortDirection.Input&&perCandidate.Contains(p.Name)))ports.Add(p with{Name="c"+i+"_"+p.Name});if(!trackVt)ports.Add(new("c"+i+"_candidate_virtual_thread_id",HwPortDirection.Input,u32));}
        ports.Add(new(count==2?"valid_pair":"valid_all",HwPortDirection.Output,u1));for(int i=0;i<count;i++)foreach(var p in child.Ports.Where(p=>p.Direction==HwPortDirection.Output&&decisionOutputs.Contains(p.Name)))ports.Add(p with{Name="c"+i+"_"+p.Name});
        foreach(var p in child.Ports.Where(p=>p.Direction==HwPortDirection.Output&&(p.Name=="next_occupancy"||p.Name.StartsWith("next_occupied",StringComparison.Ordinal))))ports.Add(p with{Name="final_"+p.Name});
        foreach(string name in budgets)ports.Add(new("final_"+name,HwPortDirection.Output,u8));foreach(string name in previous)ports.Add(new("final_"+name,HwPortDirection.Output,u32));ports.Add(new("final_injections",HwPortDirection.Output,u32));
        for(int vt=0;vt<4;vt++)ports.Add(new($"final_projected_count_vt{vt}",HwPortDirection.Output,u8));
        foreach(string name in memoryStateNames)ports.Add(new("final_"+name,HwPortDirection.Output,name.StartsWith("remaining_",StringComparison.Ordinal)?u8:u32));
        ports.Add(new("final_speculation_budget_exhaustion_events",HwPortDirection.Output,u64));
        ports.Add(new("final_smt_rejections_count",HwPortDirection.Output,u64));
        nodes.Add(new HwConstantNode("one32",u32,Convert.ToString(1,2).PadLeft(32,'0'),origin));nodes.Add(new HwConstantNode("one64",u64,Convert.ToString(1,2).PadLeft(64,'0'),origin));nodes.Add(new HwConstantNode("zero8",u8,"00000000",origin));
        if(count==4){nodes.Add(new HwConstantNode("zero1",u1,"0",origin));nodes.Add(new HwConstantNode("one1",u1,"1",origin));nodes.Add(new HwConstantNode("zero2",new HwBitVectorType(2),"00",origin));nodes.Add(new HwConstantNode("zero3",new HwBitVectorType(3),"000",origin));
            var classConnections=new Dictionary<string,string>(StringComparer.Ordinal);
            for(int slot=0;slot<8;slot++){classConnections.Add("slot"+slot+"_present","bundle_slot"+slot+"_present");classConnections.Add("slot"+slot+"_class","bundle_slot"+slot+"_class");}
            for(int classIndex=0;classIndex<6;classIndex++){string countWire=initialClassCounts[classIndex],totalWire=classCapacityWires[classIndex];nodes.Add(new HwWireNode(countWire,u8,origin));nodes.Add(new HwWireNode(totalWire,u8,origin));nodes.Add(new HwOutputDriveNode("drive_initial_class_occupied"+classIndex,"initial_class_occupied"+classIndex,countWire,origin));classConnections.Add("class_count"+classIndex,countWire);classConnections.Add("class_total"+classIndex,totalWire);}
            instances.Add(new HwInstanceNode("bundle_class_occupancy","slot_class_occupancy_from_bundle",classConnections,new Dictionary<string,string>(),Origin:origin));
            string occupancyMask="zero8",emptyMask="zero8",referenceMask="zero8";
            for(int i=0;i<8;i++){string bit=$"bundle_slot{i}_",oneHot=bit+"onehot";nodes.Add(new HwConstantNode(oneHot,u8,Convert.ToString(1<<i,2).PadLeft(8,'0'),origin));nodes.Add(new HwOperationNode(bit+"nop_raw","and",["bundle_is_nop_mask",oneHot],u8,Origin:origin));nodes.Add(new HwOperationNode(bit+"nop_zero","eq",[bit+"nop_raw","zero8"],u1,Origin:origin));nodes.Add(new HwOperationNode(bit+"is_nop","xor",[bit+"nop_zero","one1"],u1,Origin:origin));nodes.Add(new HwOperationNode(bit+"stealable_raw","and",["bundle_stealable_mask",oneHot],u8,Origin:origin));nodes.Add(new HwOperationNode(bit+"stealable_zero","eq",[bit+"stealable_raw","zero8"],u1,Origin:origin));nodes.Add(new HwOperationNode(bit+"stealable","xor",[bit+"stealable_zero","one1"],u1,Origin:origin));nodes.Add(new HwOperationNode(bit+"empty","xor",[bit+"present","one1"],u1,Origin:origin));nodes.Add(new HwOperationNode(bit+"stealable_nop","and",[bit+"is_nop",bit+"stealable"],u1,Origin:origin));nodes.Add(new HwOperationNode(bit+"reference","or",[bit+"empty",bit+"stealable_nop"],u1,Origin:origin));string occTerm=bit+"occupancy_term",emptyTerm=bit+"empty_term",refTerm=bit+"reference_term",nextOcc=bit+"occupancy_mask",nextEmpty=bit+"empty_mask",nextRef=bit+"reference_mask";nodes.Add(new HwMuxNode(occTerm,bit+"present",oneHot,"zero8",u8,origin));nodes.Add(new HwMuxNode(emptyTerm,bit+"empty",oneHot,"zero8",u8,origin));nodes.Add(new HwMuxNode(refTerm,bit+"reference",oneHot,"zero8",u8,origin));nodes.Add(new HwOperationNode(nextOcc,"or",[occupancyMask,occTerm],u8,Origin:origin));nodes.Add(new HwOperationNode(nextEmpty,"or",[emptyMask,emptyTerm],u8,Origin:origin));nodes.Add(new HwOperationNode(nextRef,"or",[referenceMask,refTerm],u8,Origin:origin));occupancyMask=nextOcc;emptyMask=nextEmpty;referenceMask=nextRef;}
            nodes.Add(new HwOutputDriveNode("drive_initial_occupancy","initial_occupancy",occupancyMask,origin));nodes.Add(new HwOutputDriveNode("drive_initial_empty","initial_empty_slot_mask",emptyMask,origin));nodes.Add(new HwOutputDriveNode("drive_initial_reference","initial_reference_opportunity_mask",referenceMask,origin));}
        string[] currentBudgets=budgets.ToArray(),currentPrevious=previous.ToArray();string currentOccupancy=count==4?"bundle_slot7_occupancy_mask":"occupancy",currentEmptyMask=count==4?"bundle_slot7_empty_mask":"",currentReferenceMask=count==4?"bundle_slot7_reference_mask":"",currentCount="current_injections",currentBundleLow="bundle_shared_low",currentBundleHigh="bundle_shared_high",currentSpeculationEvents="speculation_budget_exhaustion_events",currentSmtRejections="smt_rejections_count";string[] currentBundleVt=Enumerable.Range(0,4).Select(i=>$"bundle_vt{i}_mask").ToArray();string[] currentUsed=Enumerable.Range(0,6).Select(i=>count==4?initialClassCounts[i]:"occupied"+i).ToArray();string[] currentProjected=Enumerable.Range(0,4).Select(i=>$"projected_count_vt{i}").ToArray();string[] currentMemoryState=memoryStateNames.ToArray();
        for(int c=0;c<count;c++)
        {
            string prefix="c"+c+"_", selectedBudget=prefix+"zero_budget",selectedPrevious=prefix+"minus_previous";nodes.Add(new HwConstantNode(selectedBudget,u8,"00000000",origin));nodes.Add(new HwConstantNode(selectedPrevious,u32,new string('1',32),origin));
            string active=prefix+"present";if(trackVt)for(int prior=0;prior<c;prior++){string q=prefix+"prior"+prior+"_";nodes.Add(new HwOperationNode(q+"same","eq",[prefix+"vt","c"+prior+"_vt"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"consumed","and",[q+"same","c"+prior+"_gated_accepted"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"available","xor",[q+"consumed","one1"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"active","and",[active,q+"available"],u1,Origin:origin));active=q+"active";}
            for(int i=0;i<8;i++){string q=prefix+"class"+i+"_";nodes.Add(new HwConstantNode(q+"id",u8,Convert.ToString(i,2).PadLeft(8,'0'),origin));nodes.Add(new HwOperationNode(q+"match","eq",[prefix+"slot_class",q+"id"],u1,Origin:origin));nodes.Add(new HwMuxNode(q+"budget",q+"match",currentBudgets[i],selectedBudget,u8,origin));nodes.Add(new HwMuxNode(q+"previous",q+"match",currentPrevious[i],selectedPrevious,u32,origin));selectedBudget=q+"budget";selectedPrevious=q+"previous";}
            var connections=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var p in child.Ports)
            {
                string signal;
                if(p.Direction==HwPortDirection.Input){signal=p.Name switch{"template_remaining"=>selectedBudget,"previous_lane"=>selectedPrevious,"occupancy"=>currentOccupancy,"current_injections"=>currentCount,"bundle_shared_low"=>currentBundleLow,"bundle_shared_high"=>currentBundleHigh,"speculation_budget_exhaustion_events"=>currentSpeculationEvents,"candidate_virtual_thread_id" when trackVt=>prefix+"vt32","candidate_virtual_thread_id"=>prefix+"candidate_virtual_thread_id",_ when memoryStateNames.Contains(p.Name,StringComparer.Ordinal)=>currentMemoryState[Array.IndexOf(memoryStateNames,p.Name)],_ when p.Name.StartsWith("projected_count_vt",StringComparison.Ordinal)=>currentProjected[int.Parse(p.Name[18..])],_ when p.Name.StartsWith("bundle_vt",StringComparison.Ordinal)&&p.Name.EndsWith("_mask",StringComparison.Ordinal)=>currentBundleVt[int.Parse(p.Name[9..^5])],_ when perCandidate.Contains(p.Name)=>prefix+p.Name,_ when p.Name.StartsWith("occupied",StringComparison.Ordinal)=>currentUsed[int.Parse(p.Name[8..])],_ when p.Name.StartsWith("total",StringComparison.Ordinal)&&count==4=>classCapacityWires[int.Parse(p.Name[5..])],_=>p.Name};if(p.Name=="candidate_virtual_thread_id"&&trackVt)nodes.Add(new HwOperationNode(prefix+"vt32","zext",[prefix+"vt"],u32,Origin:origin));}
                else if(decisionOutputs.Contains(p.Name)){signal=prefix+"wire_"+p.Name;nodes.Add(new HwWireNode(signal,p.Type,origin));string output=signal;if(count==4){output=prefix+"gated_"+p.Name;string absent=p.Name=="defined"?"one1":p.Type.Width switch{1=>"zero1",2=>"zero2",3=>"zero3",8=>"zero8",_=>throw new InvalidDataException("Unexpected decision output width.")};nodes.Add(new HwMuxNode(output,active,signal,absent,p.Type,origin));}nodes.Add(new HwOutputDriveNode(prefix+"drive_"+p.Name,prefix+p.Name,output,origin));}
                else {signal=prefix+"wire_"+p.Name;nodes.Add(new HwWireNode(signal,p.Type,origin));}
                connections.Add(p.Name,signal);
            }
            instances.Add(new HwInstanceNode(prefix+"candidate","smt_candidate_admission_projection",connections,new Dictionary<string,string>(),Origin:origin));
            string speculationEvent=prefix+(count==4?"gated_speculation_budget_exhaustion_event":"wire_speculation_budget_exhaustion_event");nodes.Add(new HwOperationNode(prefix+"speculation_events_incremented","add",[currentSpeculationEvents,"one64"],u64,Origin:origin));string nextSpeculationEvents=prefix+"speculation_events_next";nodes.Add(new HwMuxNode(nextSpeculationEvents,speculationEvent,prefix+"speculation_events_incremented",currentSpeculationEvents,u64,origin));currentSpeculationEvents=nextSpeculationEvents;
            string rejectionEvent=prefix+(count==4?"gated_smt_rejection_event":"wire_smt_rejection_event");nodes.Add(new HwOperationNode(prefix+"smt_rejections_incremented","add",[currentSmtRejections,"one64"],u64,Origin:origin));string nextSmtRejections=prefix+"smt_rejections_next";nodes.Add(new HwMuxNode(nextSmtRejections,rejectionEvent,prefix+"smt_rejections_incremented",currentSmtRejections,u64,origin));currentSmtRejections=nextSmtRejections;
            string accepted=prefix+(count==4?"gated_accepted":"wire_accepted");nodes.Add(new HwOperationNode(prefix+"count_inc","add",[currentCount,"one32"],u32,Origin:origin));nodes.Add(new HwMuxNode(prefix+"count_next",accepted,prefix+"count_inc",currentCount,u32,origin));currentCount=prefix+"count_next";
            string commitLow=prefix+"bundle_shared_low_commit",commitHigh=prefix+"bundle_shared_high_commit";nodes.Add(new HwOperationNode(commitLow,"or",[currentBundleLow,prefix+"candidate_shared_low"],new HwBitVectorType(64),Origin:origin));nodes.Add(new HwOperationNode(commitHigh,"or",[currentBundleHigh,prefix+"candidate_shared_high"],new HwBitVectorType(64),Origin:origin));string activeCommit=count==4?prefix+"gated_accepted":prefix+"wire_accepted";string nextLow=prefix+"bundle_shared_low_next",nextHigh=prefix+"bundle_shared_high_next";nodes.Add(new HwMuxNode(nextLow,activeCommit,commitLow,currentBundleLow,new HwBitVectorType(64),origin));nodes.Add(new HwMuxNode(nextHigh,activeCommit,commitHigh,currentBundleHigh,new HwBitVectorType(64),origin));currentBundleLow=nextLow;currentBundleHigh=nextHigh;
            for(int stateIx=0;stateIx<memoryStateNames.Length;stateIx++){string name=memoryStateNames[stateIx],q=prefix+"memory_state_"+stateIx+"_";var type=name.StartsWith("remaining_",StringComparison.Ordinal)?u8:u32;string next=q+"next";nodes.Add(new HwMuxNode(next,activeCommit,prefix+"wire_next_"+name,currentMemoryState[stateIx],type,origin));currentMemoryState[stateIx]=next;}
            string candidateVtSignal=trackVt?prefix+"vt32":prefix+"candidate_virtual_thread_id";for(int vt=0;vt<4;vt++){string q=prefix+$"bundle_vt{vt}_";nodes.Add(new HwConstantNode(q+"id",u32,Convert.ToString(vt,2).PadLeft(32,'0'),origin));nodes.Add(new HwOperationNode(q+"match","eq",[candidateVtSignal,q+"id"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"or","or",[currentBundleVt[vt],prefix+"candidate_register_hazard_mask"],u32,Origin:origin));nodes.Add(new HwOperationNode(q+"enable","and",[activeCommit,q+"match"],u1,Origin:origin));string next=q+"next";nodes.Add(new HwMuxNode(next,q+"enable",q+"or",currentBundleVt[vt],u32,origin));currentBundleVt[vt]=next;}
            if(count==4){nodes.Add(new HwMuxNode(prefix+"gated_next_occupancy",active,prefix+"wire_next_occupancy",currentOccupancy,u8,origin));currentOccupancy=prefix+"gated_next_occupancy";for(int i=0;i<6;i++){nodes.Add(new HwMuxNode(prefix+"gated_next_occupied"+i,active,prefix+"wire_next_occupied"+i,currentUsed[i],u8,origin));currentUsed[i]=prefix+"gated_next_occupied"+i;}nodes.Add(new HwOperationNode(prefix+"gated_fast_consumed","and",[active,prefix+"wire_fast_consumed"],u1,Origin:origin));
                for(int lane=0;lane<8;lane++){string q=prefix+"opportunity_lane"+lane+"_";nodes.Add(new HwConstantNode(q+"id",new HwBitVectorType(3),Convert.ToString(lane,2).PadLeft(3,'0'),origin));nodes.Add(new HwOperationNode(q+"match","eq",[prefix+"wire_selected_lane",q+"id"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"commit","and",[prefix+"gated_accepted",q+"match"],u1,Origin:origin));nodes.Add(new HwConstantNode(q+"clear_mask",u8,Convert.ToString(255^(1<<lane),2).PadLeft(8,'0'),origin));nodes.Add(new HwOperationNode(q+"empty_cleared","and",[currentEmptyMask,q+"clear_mask"],u8,Origin:origin));nodes.Add(new HwMuxNode(q+"empty_next",q+"commit",q+"empty_cleared",currentEmptyMask,u8,origin));currentEmptyMask=q+"empty_next";nodes.Add(new HwOperationNode(q+"reference_cleared","and",[currentReferenceMask,q+"clear_mask"],u8,Origin:origin));nodes.Add(new HwMuxNode(q+"reference_next",q+"commit",q+"reference_cleared",currentReferenceMask,u8,origin));currentReferenceMask=q+"reference_next";}}
            else{currentOccupancy=prefix+"wire_next_occupancy";for(int i=0;i<6;i++)currentUsed[i]=prefix+"wire_next_occupied"+i;}
            nodes.Add(new HwOperationNode(prefix+"flexible","eq",[prefix+"pinning_kind","zero8"],u1,Origin:origin));nodes.Add(new HwOperationNode(prefix+"record","and",[accepted,prefix+"flexible"],u1,Origin:origin));nodes.Add(new HwOperationNode(prefix+"lane32","zext",[prefix+"wire_selected_lane"],u32,Origin:origin));
            for(int i=0;i<8;i++){string q=prefix+"class"+i+"_";nodes.Add(new HwOperationNode(q+"budget_write","and",[q+"match",prefix+(count==4?"gated_fast_consumed":"wire_fast_consumed")],u1,Origin:origin));nodes.Add(new HwMuxNode(q+"budget_next",q+"budget_write",prefix+"wire_template_next",currentBudgets[i],u8,origin));nodes.Add(new HwOperationNode(q+"record","and",[prefix+"record",q+"match"],u1,Origin:origin));nodes.Add(new HwMuxNode(q+"previous_next",q+"record",prefix+"lane32",currentPrevious[i],u32,origin));currentBudgets[i]=q+"budget_next";currentPrevious[i]=q+"previous_next";}
            for(int vt=0;vt<4;vt++){string q=prefix+$"projected_vt{vt}_";nodes.Add(new HwConstantNode(q+"id",u32,Convert.ToString(vt,2).PadLeft(32,'0'),origin));nodes.Add(new HwOperationNode(q+"match","eq",[candidateVtSignal,q+"id"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"memory","and",[activeCommit,prefix+"is_loadstore"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"enable","and",[q+"match",q+"memory"],u1,Origin:origin));nodes.Add(new HwConstantNode(q+"limit",u8,"00001000",origin));nodes.Add(new HwConstantNode(q+"one",u8,"00000001",origin));nodes.Add(new HwOperationNode(q+"count64","zext",[currentProjected[vt]],new HwBitVectorType(64),Origin:origin));nodes.Add(new HwOperationNode(q+"limit64","zext",[q+"limit"],new HwBitVectorType(64),Origin:origin));nodes.Add(new HwOperationNode(q+"below_limit","ult64",[q+"count64",q+"limit64"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"update","and",[q+"enable",q+"below_limit"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"incremented","add",[currentProjected[vt],q+"one"],u8,Origin:origin));string next=q+"next";nodes.Add(new HwMuxNode(next,q+"update",q+"incremented",currentProjected[vt],u8,origin));currentProjected[vt]=next;}
        }
        if(count==2){nodes.Add(new HwOperationNode("valid_pair_result","and",["c0_wire_defined","c1_wire_defined"],u1,Origin:origin));nodes.Add(new HwOutputDriveNode("drive_valid_pair","valid_pair","valid_pair_result",origin));}
        else{string valid="c0_gated_defined";for(int i=1;i<count;i++){string next="valid_through_"+i;nodes.Add(new HwOperationNode(next,"and",[valid,"c"+i+"_gated_defined"],u1,Origin:origin));valid=next;}nodes.Add(new HwOutputDriveNode("drive_valid_all","valid_all",valid,origin));}
        nodes.Add(new HwOutputDriveNode("drive_final_occupancy","final_next_occupancy",currentOccupancy,origin));if(count==4){nodes.Add(new HwOutputDriveNode("drive_final_empty_mask","final_empty_slot_mask",currentEmptyMask,origin));nodes.Add(new HwOutputDriveNode("drive_final_reference_mask","final_reference_opportunity_mask",currentReferenceMask,origin));}for(int i=0;i<6;i++)nodes.Add(new HwOutputDriveNode("drive_final_used"+i,"final_next_occupied"+i,currentUsed[i],origin));for(int stateIx=0;stateIx<memoryStateNames.Length;stateIx++)nodes.Add(new HwOutputDriveNode("drive_final_memory_state_"+stateIx,"final_"+memoryStateNames[stateIx],currentMemoryState[stateIx],origin));nodes.Add(new HwOutputDriveNode("drive_final_speculation_budget_exhaustion_events","final_speculation_budget_exhaustion_events",currentSpeculationEvents,origin));nodes.Add(new HwOutputDriveNode("drive_final_smt_rejections","final_smt_rejections_count",currentSmtRejections,origin));
        for(int i=0;i<8;i++){nodes.Add(new HwOutputDriveNode("drive_final_budget"+i,"final_budget"+i,currentBudgets[i],origin));nodes.Add(new HwOutputDriveNode("drive_final_previous"+i,"final_previous"+i,currentPrevious[i],origin));}nodes.Add(new HwOutputDriveNode("drive_final_count","final_injections",currentCount,origin));for(int vt=0;vt<4;vt++)nodes.Add(new HwOutputDriveNode($"drive_final_projected_count_vt{vt}",$"final_projected_count_vt{vt}",currentProjected[vt],origin));
        string topName=count==2?"smt_two_candidate_projection":"smt_four_candidate_projection";var top=new HwModule(topName,topName,ports,nodes,instances,new HwHardwareContract(count==2?"scheduler.smt-two-candidate.unqualified.v1":"scheduler.smt-four-candidate.unqualified.v3",HwModuleKind.Combinational,ports,new HwTimingContract(0,1)),origin);
        var result=new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",top.Id,count==4?[top,classOccupancyModule!,..candidate.Design.Modules]:[top,..candidate.Design.Modules],candidate.Design.Toolchain,[]));return result.Validated??throw new InvalidDataException("Two-candidate HWIR refused: "+string.Join("; ",result.Diagnostics.Select(d=>d.Message)));
    }
}
#endif
