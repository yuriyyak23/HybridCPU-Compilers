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
/// Source-anchored rank logic and observable consumer conditions for its unwritten tail.
/// The assist OOB output means a fault if iteration reaches rank 3; upstream early-return,
/// quota, and prior-injection conditions are not represented here.
/// </summary>
public static class BridgeHwirSmtRanking
{
    public const string AssistConsumerSha256 = "8515039c3d770382fd448439d2453c01bd42397f3b525b413d411161bf8035a8";

    public static HwValidatedDesign Compile(byte[] smt, byte[] scheduler, byte[] admission, byte[] assist,
        string commit, HwValidatedDesign collected, bool includeCollectedHierarchy = false)
    {
        if(collected.Design.TopModule!="smt_collected_scored_admission_projection")throw new InvalidDataException("Collected admission required.");
        static string Hash(byte[] b)=>Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        var expectedOrder=BridgeHwirPressureOrder.Compile(smt,scheduler,admission,[Hash(smt),Hash(scheduler),Hash(admission)],commit,collected.Design.Toolchain);
        if(collected.Design.Modules.Single(m=>m.Id=="smt_pressure_order_projection").Origin?.IdentityDigest!=expectedOrder.Design.Modules.Single().Origin?.IdentityDigest)throw new InvalidDataException("Ranking source identity differs.");
        static SyntaxNode Parse(byte[] data){var t=CSharpSyntaxTree.ParseText(new UTF8Encoding(false,true).GetString(data));if(t.GetDiagnostics().Any(d=>d.Severity==DiagnosticSeverity.Error))throw new InvalidDataException("Invalid ranking source.");return t.GetRoot();}
        static bool Same(SyntaxNode a,SyntaxNode b)=>!a.DescendantTrivia(descendIntoTrivia:true).Any(t=>t.IsDirective)&&a.DescendantTokens().Select(t=>t.Text).SequenceEqual(b.DescendantTokens().Select(t=>t.Text));
        static ClassDeclarationSyntax Owner(SyntaxNode n)=>n.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c=>c.Identifier.ValueText=="MicroOpScheduler"&&c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString()=="YAKSys_Hybrid_CPU.Core");
        if (Hash(assist) != AssistConsumerSha256) throw new InvalidDataException("Assist consumer source bytes changed.");
        var a=Owner(Parse(admission));var s=Owner(Parse(scheduler));var p=Owner(Parse(smt)).Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="PackBundleIntraCoreSmt"&&m.ParameterList.Parameters.Count==5);
        var assistMethod=Owner(Parse(assist)).Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="TryInjectAssistCandidates");
        var rank=a.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="GetCreditRankedOrder");
        const string expected="{ int count = 0; for (int vt = 0; vt < SMT_WAYS; vt++) { if (vt == ownerVirtualThreadId) continue; ranked[count++] = vt; } for (int i = 0; i < count - 1; i++) { for (int j = i + 1; j < count; j++) { int a = ranked[i]; int b = ranked[j]; if (_fairnessCredits[b] > _fairnessCredits[a] || (_fairnessCredits[b] == _fairnessCredits[a] && b < a)) { ranked[i] = b; ranked[j] = a; } } } }";
        var legacy=a.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="GetRankedVirtualThread");
        if(rank.Body is null||!Same(rank.Body,SyntaxFactory.ParseStatement(expected))||legacy.Body is null||!Same(legacy.Body,SyntaxFactory.ParseStatement("{ return forceTdm ? ((scanIndex + 1) % SMT_WAYS) : scanIndex; }")))throw new InvalidDataException("Fairness ranking changed.");
        if(!s.Members.OfType<FieldDeclarationSyntax>().Any(f=>Same(f,SyntaxFactory.ParseMemberDeclaration("public const int TDM_PERIOD = 64;")!)))throw new InvalidDataException("TDM period changed.");
        var nomination=s.Members.OfType<StructDeclarationSyntax>().Single(x=>x.Identifier.ValueText=="SmtNominationState");var background=nomination.Members.OfType<PropertyDeclarationSyntax>().Single(x=>x.Identifier.ValueText=="HasReadyBackgroundCandidates");
        if(background.ExpressionBody is null||!Same(background.ExpressionBody.Expression,SyntaxFactory.ParseExpression("(ReadyMask & 0b1110) != 0")))throw new InvalidDataException("Background readiness changed.");
        foreach(var statement in new[]{"bool isTdmSlot = (GlobalCycleCounter % TDM_PERIOD) == 0;","bool forceTdm = isTdmSlot && nominationState.HasReadyBackgroundCandidates;","Span<int> vtOrder = stackalloc int[SMT_WAYS];"})if(!p.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().Any(x=>Same(x,SyntaxFactory.ParseStatement(statement))))throw new InvalidDataException("Ranking caller changed.");
        var branch=p.DescendantNodes().OfType<IfStatementSyntax>().Single(i=>i.Condition.ToString()=="CreditFairnessEnabled"&&i.Statement.ToString().Contains("GetCreditRankedOrder",StringComparison.Ordinal));
        const string branchText="if (CreditFairnessEnabled) { GetCreditRankedOrder(ownerVirtualThreadId, vtOrder); } else { for (int i = 0; i < SMT_WAYS; i++) vtOrder[i] = GetRankedVirtualThread(i, forceTdm); }";
        if(!Same(branch,SyntaxFactory.ParseStatement(branchText)))throw new InvalidDataException("Ranking selection changed.");
        // The fairness helper writes only the three non-owner ranks, but the
        // active caller still iterates SMT_WAYS entries. Bind that read and the
        // subsequent append/guards so rank3 stays an explicit caller-provided
        // value in the candidate instead of being silently treated as absent.
        var rankConsumer=p.DescendantNodes().OfType<ForStatementSyntax>().SingleOrDefault(loop =>
            loop.Condition?.ToString()=="i < SMT_WAYS" &&
            loop.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().Any(local =>
                local.Declaration.Variables.Any(v => v.Identifier.ValueText=="vt" &&
                    v.Initializer?.Value.ToString()=="vtOrder[i]")) &&
            loop.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call =>
                call.Expression.ToString()=="nominationState.IsReadyNonOwnerCandidate" &&
                call.ArgumentList.Arguments.Select(arg => arg.ToString()).SequenceEqual(new[]{"vt","ownerVirtualThreadId"})) &&
            loop.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment =>
                assignment.Left.ToString()=="legalVts[legalCount]" && assignment.Right.ToString()=="vt") &&
            loop.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>().Any(increment =>
                increment.Operand.ToString()=="legalCount"));
        if(rankConsumer is null || rankConsumer.SpanStart <= branch.SpanStart)
            throw new InvalidDataException("Rank-tail consumer loop or its order changed.");
        if (!assistMethod.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
                .Any(x=>Same(x,SyntaxFactory.ParseStatement("Span<int> vtOrder = stackalloc int[SMT_WAYS];"))) ||
            !assistMethod.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
                .Any(x=>Same(x,SyntaxFactory.ParseStatement("int vtId = vtOrder[orderIndex];"))) ||
            !assistMethod.DescendantNodes().OfType<ForStatementSyntax>()
                .Any(x=>x.Condition?.ToString()=="orderIndex < SMT_WAYS && assistInjectedCount < AssistBundleQuota"))
            throw new InvalidDataException("Assist rank loop changed.");
        if (!assistMethod.DescendantNodes().OfType<IfStatementSyntax>().Any(x=>x.Condition is not null &&
            string.Concat(x.Condition.DescendantTokens().Select(t=>t.Text))=="vtId==ownerVirtualThreadId||!_smtAssistPortValid[vtId]||_smtAssistPorts[vtId]==null"))
            throw new InvalidDataException("Assist rank bounds/owner guard changed.");
        var body=p.Body??throw new InvalidDataException("Ranking caller body missing.");var declaration=body.Statements.OfType<LocalDeclarationStatementSyntax>().Single(x=>x.Declaration.Variables.Any(v=>v.Identifier.ValueText=="vtOrder"));int start=body.Statements.IndexOf(declaration),end=body.Statements.IndexOf(branch);
        if(start<0||end<=start||body.Statements.Skip(start+1).Take(end-start-1).Any(x=>x.DescendantTokens().Any(t=>t.ValueText=="vtOrder")))throw new InvalidDataException("VT order is modified before ranking.");
        var baseOrigin=collected.Design.Modules.Single(m=>m.Id==collected.Design.TopModule).Origin??throw new InvalidDataException("Collection origin missing.");var pos=rank.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        string digest=Hash(Encoding.UTF8.GetBytes("smt-ranking-with-tail-consumers/v2|"+Hash(smt)+"|"+Hash(scheduler)+"|"+Hash(admission)+"|"+Hash(assist)+"|"+collected.CanonicalDigest));
        var origin=new HwSourceOrigin(baseOrigin.Repository,commit,"HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/Admission/MicroOpScheduler.Admission.cs","CircuitWeave.SmtRanking.Projection","YAKSys_Hybrid_CPU.Core.MicroOpScheduler","GetCreditRankedOrder",digest,HwSourceRole.Primary,pos.Line+1,pos.Character+1);
        var u1=new HwBitVectorType(1);var u32=new HwBitVectorType(32);var u64=new HwBitVectorType(64);var ports=new List<HwPortContract>{new("owner_vt",HwPortDirection.Input,u32),new("fairness_enabled",HwPortDirection.Input,u1),new("cycle",HwPortDirection.Input,u64),new("unwritten_tail",HwPortDirection.Input,u32)};var nodes=new List<HwNode>();
        for(int i=0;i<4;i++){ports.Add(new("credit"+i,HwPortDirection.Input,u32));ports.Add(new("rank"+i,HwPortDirection.Output,u32));}
        for(int i=1;i<4;i++)ports.Add(new("vt"+i+"_ready",HwPortDirection.Input,u1));
        nodes.Add(new HwConstantNode("sign_flip",u32,"10000000000000000000000000000000",origin));nodes.Add(new HwConstantNode("one1",u1,"1",origin));nodes.Add(new HwConstantNode("zero64",u64,new string('0',64),origin));nodes.Add(new HwConstantNode("mask64",u64,Convert.ToString(63,2).PadLeft(64,'0'),origin));
        string[] ids=new string[4],credits=new string[4],isOwner=new string[4];
        for(int i=0;i<4;i++){ids[i]="id"+i;credits[i]="credit"+i;isOwner[i]="owner"+i;nodes.Add(new HwConstantNode(ids[i],u32,Convert.ToString(i,2).PadLeft(32,'0'),origin));nodes.Add(new HwOperationNode(isOwner[i],"eq",["owner_vt",ids[i]],u1,Origin:origin));}
        nodes.Add(new HwOperationNode("owner01","or",[isOwner[0],isOwner[1]],u1,Origin:origin));nodes.Add(new HwOperationNode("owner23","or",[isOwner[2],isOwner[3]],u1,Origin:origin));nodes.Add(new HwOperationNode("owner_found","or",["owner01","owner23"],u1,Origin:origin));
        nodes.Add(new HwOperationNode("cycle_mod","and",["cycle","mask64"],u64,Origin:origin));nodes.Add(new HwOperationNode("cycle_zero","eq",["cycle_mod","zero64"],u1,Origin:origin));nodes.Add(new HwOperationNode("ready12","or",["vt1_ready","vt2_ready"],u1,Origin:origin));nodes.Add(new HwOperationNode("ready_background","or",["ready12","vt3_ready"],u1,Origin:origin));nodes.Add(new HwOperationNode("force_tdm","and",["cycle_zero","ready_background"],u1,Origin:origin));
        for(int i=0;i<3;i++)for(int j=i+1;j<4;j++){
            string q=$"sort{i}{j}_";nodes.Add(new HwOperationNode(q+"ax","xor",[credits[i],"sign_flip"],u32,Origin:origin));nodes.Add(new HwOperationNode(q+"bx","xor",[credits[j],"sign_flip"],u32,Origin:origin));nodes.Add(new HwOperationNode(q+"a64","zext",[q+"ax"],u64,Origin:origin));nodes.Add(new HwOperationNode(q+"b64","zext",[q+"bx"],u64,Origin:origin));nodes.Add(new HwOperationNode(q+"greater","ult64",[q+"a64",q+"b64"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"equal","eq",[credits[i],credits[j]],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"ia64","zext",[ids[i]],u64,Origin:origin));nodes.Add(new HwOperationNode(q+"ib64","zext",[ids[j]],u64,Origin:origin));nodes.Add(new HwOperationNode(q+"id_less","ult64",[q+"ib64",q+"ia64"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"tie","and",[q+"equal",q+"id_less"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"prefer","or",[q+"greater",q+"tie"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"same_owner","eq",[isOwner[i],isOwner[j]],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"same_prefer","and",[q+"same_owner",q+"prefer"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"b_not_owner","xor",[isOwner[j],"one1"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"owner_last","and",[isOwner[i],q+"b_not_owner"],u1,Origin:origin));nodes.Add(new HwOperationNode(q+"swap","or",[q+"same_prefer",q+"owner_last"],u1,Origin:origin));
            foreach(var pair in new[]{(ids,u32,"id"),(credits,u32,"credit"),(isOwner,u1,"owner")}){nodes.Add(new HwMuxNode(q+pair.Item3+"a",q+"swap",pair.Item1[j],pair.Item1[i],pair.Item2,origin));nodes.Add(new HwMuxNode(q+pair.Item3+"b",q+"swap",pair.Item1[i],pair.Item1[j],pair.Item2,origin));pair.Item1[i]=q+pair.Item3+"a";pair.Item1[j]=q+pair.Item3+"b";}
        }
        for(int i=0;i<4;i++){string creditRank=ids[i];if(i==3){nodes.Add(new HwMuxNode("last_rank","owner_found","unwritten_tail",creditRank,u32,origin));creditRank="last_rank";}nodes.Add(new HwMuxNode("legacy"+i,"force_tdm","id"+((i+1)%4),"id"+i,u32,origin));nodes.Add(new HwMuxNode("selected"+i,"fairness_enabled",creditRank,"legacy"+i,u32,origin));nodes.Add(new HwOutputDriveNode("drive_rank"+i,"rank"+i,"selected"+i,origin));}
        var rankModule=new HwModule("smt_ranking_projection","smt_ranking_projection",ports,nodes,[],new HwHardwareContract("scheduler.smt-ranking.unqualified.v1",HwModuleKind.Combinational,ports,new HwTimingContract(0,1)),origin);
        var child=collected.Design.Modules.Single(m=>m.Id==collected.Design.TopModule);var topPorts=child.Ports.Where(p=>!(p.Direction==HwPortDirection.Input&&p.Name.Length==5&&p.Name.StartsWith("rank",StringComparison.Ordinal))).ToList();foreach(var rp in ports.Where(p=>p.Direction==HwPortDirection.Input&&!topPorts.Any(x=>x.Name==p.Name)))topPorts.Add(rp);
        var topNodes=new List<HwNode>();var rankConnections=new Dictionary<string,string>();foreach(var rp in ports){string n=rp.Name;if(rp.Direction==HwPortDirection.Output){topNodes.Add(new HwWireNode(n,rp.Type,baseOrigin));}rankConnections.Add(rp.Name,n);}
        var childConnections=new Dictionary<string,string>();foreach(var cp in child.Ports){string n=cp.Name;if(cp.Direction==HwPortDirection.Output){n="child_"+cp.Name;topNodes.Add(new HwWireNode(n,cp.Type,baseOrigin));topNodes.Add(new HwOutputDriveNode("drive_"+cp.Name,cp.Name,n,baseOrigin));}childConnections.Add(cp.Name,n);}
        var u1top=new HwBitVectorType(1);var u32top=new HwBitVectorType(32);var u64top=new HwBitVectorType(64);
        topPorts.Add(new HwPortContract("main_rank3_duplicate_snapshot_condition",HwPortDirection.Output,u1top));
        topPorts.Add(new HwPortContract("assist_rank3_index_oob_if_reached",HwPortDirection.Output,u1top));
        for(int i=0;i<4;i++)
        {
            string name="ranked_vt"+i;
            topPorts.Add(new HwPortContract(name,HwPortDirection.Output,u32top));
            topNodes.Add(new HwOutputDriveNode("drive_"+name,name,"rank"+i,baseOrigin));
        }
        topNodes.Add(new HwConstantNode("tail_one",u1top,"1",baseOrigin));
        topNodes.Add(new HwConstantNode("tail_four",u32top,Convert.ToString(4,2).PadLeft(32,'0'),baseOrigin));
        string[] tailIdEq=new string[4],tailReady=new string[4];
        for(int i=0;i<4;i++)
        {
            topNodes.Add(new HwConstantNode("tail_id"+i,u32top,Convert.ToString(i,2).PadLeft(32,'0'),baseOrigin));
            tailIdEq[i]="tail_is_id"+i;topNodes.Add(new HwOperationNode(tailIdEq[i],"eq",["rank3","tail_id"+i],u1top,Origin:baseOrigin));
            tailReady[i]="tail_ready_present"+i;topNodes.Add(new HwOperationNode(tailReady[i],"and",["vt"+i+"_ready","vt"+i+"_present"],u1top,Origin:baseOrigin));
        }
        topNodes.Add(new HwOperationNode("tail_rank3_64","zext",["rank3"],u64top,Origin:baseOrigin));
        topNodes.Add(new HwOperationNode("tail_four_64","zext",["tail_four"],u64top,Origin:baseOrigin));
        topNodes.Add(new HwOperationNode("tail_oob_lt","ult64",["tail_rank3_64","tail_four_64"],u1top,Origin:baseOrigin));
        topNodes.Add(new HwOperationNode("tail_rank3_owner","eq",["rank3","owner_vt"],u1top,Origin:baseOrigin));
        topNodes.Add(new HwOperationNode("tail_not_owner","xor",["tail_rank3_owner","tail_one"],u1top,Origin:baseOrigin));
        topNodes.Add(new HwOperationNode("tail_oob_raw","xor",["tail_oob_lt","tail_one"],u1top,Origin:baseOrigin));
        topNodes.Add(new HwOperationNode("tail_oob","and",["tail_oob_raw","tail_not_owner"],u1top,Origin:baseOrigin));
        topNodes.Add(new HwOperationNode("tail_dup_rank0","eq",["rank3","rank0"],u1top,Origin:baseOrigin));
        topNodes.Add(new HwOperationNode("tail_dup_rank1","eq",["rank3","rank1"],u1top,Origin:baseOrigin));
        topNodes.Add(new HwOperationNode("tail_dup_rank2","eq",["rank3","rank2"],u1top,Origin:baseOrigin));
        topNodes.Add(new HwOperationNode("tail_dup_rank01","or",["tail_dup_rank0","tail_dup_rank1"],u1top,Origin:baseOrigin));
        topNodes.Add(new HwOperationNode("tail_duplicate","or",["tail_dup_rank01","tail_dup_rank2"],u1top,Origin:baseOrigin));
        for(int i=0;i<4;i++)
            topNodes.Add(new HwOperationNode("tail_ready_selected"+i,"and",["tail_is_id"+i,tailReady[i]],u1top,Origin:baseOrigin));
        topNodes.Add(new HwOperationNode("tail_ready_selected01","or",["tail_ready_selected0","tail_ready_selected1"],u1top,Origin:baseOrigin));
        topNodes.Add(new HwOperationNode("tail_ready_selected23","or",["tail_ready_selected2","tail_ready_selected3"],u1top,Origin:baseOrigin));
        topNodes.Add(new HwOperationNode("tail_ready_selected_any","or",["tail_ready_selected01","tail_ready_selected23"],u1top,Origin:baseOrigin));
        topNodes.Add(new HwOperationNode("main_duplicate_attempt", "and", ["tail_duplicate", "tail_ready_selected_any"],u1top,Origin:baseOrigin));
        topNodes.Add(new HwOutputDriveNode("drive_main_duplicate_attempt","main_rank3_duplicate_snapshot_condition","main_duplicate_attempt",baseOrigin));
        topNodes.Add(new HwOutputDriveNode("drive_assist_rank3_index_oob_if_reached","assist_rank3_index_oob_if_reached","tail_oob",baseOrigin));
        var top=new HwModule("smt_ranked_collected_admission_projection","smt_ranked_collected_admission_projection",topPorts,topNodes,[new HwInstanceNode("ranking",rankModule.Id,rankConnections,new Dictionary<string,string>(),Origin:baseOrigin),new HwInstanceNode("collection",child.Id,childConnections,new Dictionary<string,string>(),Origin:baseOrigin)],new HwHardwareContract("scheduler.smt-ranked-collected-admission.unqualified.v1",HwModuleKind.Combinational,topPorts,new HwTimingContract(0,1)),baseOrigin);
        HwModule[] allModules=[top,rankModule,..collected.Design.Modules];
        // The source-derived eight-slot opportunity state adds per-slot facts and
        // observable final masks to the composed collector. Keep the local ceiling
        // below the HWIR semantic validator's 512-port bound while allowing that slice.
        if(allModules.Any(m=>m.Ports.Count>448||m.Nodes.Count>2048))throw new InvalidDataException(string.Join(";",allModules.Where(m=>m.Ports.Count>448||m.Nodes.Count>2048).Select(m=>$"{m.Id}: ports={m.Ports.Count}, nodes={m.Nodes.Count}")));
        var result=new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",top.Id,allModules,collected.Design.Toolchain,[]));
        HwValidatedDesign intermediate=result.Validated??throw new InvalidDataException("SMT ranking HWIR refused: "+string.Join("; ",result.Diagnostics.Select(d=>d.Message)));
        if(includeCollectedHierarchy)return intermediate;

        // Publish a focused standalone rank-tail consumer projection. The downstream
        // candidate collector remains a separately qualified block and is not replaced
        // with a dummy pass-through in the exported hierarchy.
        var consumerInputs=top.Ports.Where(p=>p.Direction==HwPortDirection.Input).ToList();
        var consumerOutputs=new[]
        {
            new HwPortContract("main_rank3_duplicate_snapshot_condition",HwPortDirection.Output,u1top),
            new HwPortContract("assist_rank3_index_oob_if_reached",HwPortDirection.Output,u1top),
            new HwPortContract("ranked_vt3",HwPortDirection.Output,u32top)
        };
        consumerInputs.AddRange(consumerOutputs);
        var oldNodes=top.Nodes.ToDictionary(n=>n.Id,StringComparer.Ordinal);
        var consumerNodeIds=new HashSet<string>(StringComparer.Ordinal)
        {
            "tail_one","tail_four","tail_rank3_64","tail_four_64","tail_oob_lt","tail_rank3_owner",
            "tail_not_owner","tail_oob_raw","tail_oob","tail_dup_rank0","tail_dup_rank1","tail_dup_rank2",
            "tail_dup_rank01","tail_duplicate","tail_ready_selected01","tail_ready_selected23","tail_ready_selected_any",
            "main_duplicate_attempt","drive_main_duplicate_attempt","drive_assist_rank3_index_oob_if_reached"
        };
        for(int i=0;i<4;i++)
        {
            consumerNodeIds.Add("tail_id"+i);consumerNodeIds.Add("tail_is_id"+i);
            consumerNodeIds.Add("tail_ready_present"+i);consumerNodeIds.Add("tail_ready_selected"+i);
        }
        List<HwNode> consumerNodes=top.Nodes.Where(n=>consumerNodeIds.Contains(n.Id)).Select(n=>n switch
        {
            HwConstantNode c=>c with{Origin=origin},
            HwOperationNode o=>o with{Origin=origin},
            HwOutputDriveNode d=>d with{Origin=origin},
            HwWireNode w=>w with{Origin=origin},
            _=>n
        }).ToList();
        for(int i=0;i<4;i++)consumerNodes.Add(new HwWireNode("rank"+i,u32top,origin));
        consumerNodes.Add(new HwOutputDriveNode("drive_ranked_vt3","ranked_vt3","rank3",origin));
        var rankInstance=top.Instances.Single(i=>i.Id=="ranking") with{Origin=origin};
        var consumerTop=new HwModule("smt_rank_tail_consumers_projection","smt_rank_tail_consumers_projection",
            consumerInputs,consumerNodes,[rankInstance],
            new HwHardwareContract("scheduler.smt-rank-tail-consumers.unqualified.v1",HwModuleKind.Combinational,consumerInputs,new HwTimingContract(0,1)),origin);
        var finalResult=new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",consumerTop.Id,[consumerTop,rankModule],intermediate.Design.Toolchain,[]));
        return finalResult.Validated??throw new InvalidDataException("SMT rank-tail consumer HWIR refused: "+string.Join("; ",finalResult.Diagnostics.Select(d=>d.Code+" "+d.Message)));
    }
}
#endif
