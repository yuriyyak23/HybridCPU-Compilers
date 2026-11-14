#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Source-anchored exchange ordering for four already collected candidates.</summary>
public static class BridgeHwirPressureOrder
{
    public static HwValidatedDesign Compile(byte[] smt, byte[] scheduler, byte[] admission, string[] hashes, string commit, HwToolchainIdentity toolchain)
    {
        static string Hash(byte[] b)=>Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        byte[][] files=[smt,scheduler,admission];
        if(hashes.Length!=3||files.Where((b,i)=>b.Length>2_000_000||Hash(b)!=hashes[i]).Any()||commit.Length!=40||commit.Any(c=>!Uri.IsHexDigit(c)))throw new InvalidDataException("Pressure ordering input identity differs.");
        static SyntaxNode Parse(byte[] b){var tree=CSharpSyntaxTree.ParseText(new UTF8Encoding(false,true).GetString(b));if(tree.GetDiagnostics().Any(x=>x.Severity==DiagnosticSeverity.Error))throw new InvalidDataException("Invalid source syntax.");return tree.GetRoot();}
        static bool Same(SyntaxNode a,SyntaxNode b)=>a.DescendantTokens().Select(x=>x.Text).SequenceEqual(b.DescendantTokens().Select(x=>x.Text));
        var root=Parse(smt);var owner=root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c=>c.Identifier.ValueText=="MicroOpScheduler"&&c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString()=="YAKSys_Hybrid_CPU.Core");
        var method=owner.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="PackBundleIntraCoreSmt"&&m.ParameterList.Parameters.Count==5);
        var sort=method.DescendantNodes().OfType<IfStatementSyntax>().Single(i=>i.Condition.ToString()=="BankPressureTieBreakEnabled && legalCount > 1");
        const string expected="if (BankPressureTieBreakEnabled && legalCount > 1) { for (int i = 0; i < legalCount - 1; i++) { for (int j = i + 1; j < legalCount; j++) { if (legalPressure[j] < legalPressure[i]) { (legalVts[i], legalVts[j]) = (legalVts[j], legalVts[i]); (legalPressure[i], legalPressure[j]) = (legalPressure[j], legalPressure[i]); BankPressureAvoidanceCount++; } } } }";
        if(sort.DescendantTrivia(descendIntoTrivia:true).Any(t=>t.IsDirective)||!Same(sort,SyntaxFactory.ParseStatement(expected)))throw new InvalidDataException("Pressure exchange ordering changed.");
        var schedulerRoot=Parse(scheduler);var sched=schedulerRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c=>c.Identifier.ValueText=="MicroOpScheduler"&&c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString()=="YAKSys_Hybrid_CPU.Core");
        foreach(var field in new[]{"private const int SMT_WAYS = 4;","private const int SCOREBOARD_SLOTS = 8;"})if(sched.Members.OfType<FieldDeclarationSyntax>().Count(f=>Same(f,SyntaxFactory.ParseMemberDeclaration(field)!))!=1)throw new InvalidDataException("Pressure-score bound changed.");
        var admissionRoot=Parse(admission);var adm=admissionRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c=>c.Identifier.ValueText=="MicroOpScheduler"&&c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString()=="YAKSys_Hybrid_CPU.Core");
        var score=adm.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="GetBankPressureScore");
        const string expectedScore="{ if (bankId < 0) return 0; int score = 0; for (int vt = 0; vt < SMT_WAYS; vt++) { for (int s = 0; s < SCOREBOARD_SLOTS; s++) { if (_smtScoreboard[vt, s] != -1 && _smtScoreboardBankId[vt, s] == bankId && (_smtScoreboardType[vt, s] == ScoreboardEntryType.OutstandingLoad || _smtScoreboardType[vt, s] == ScoreboardEntryType.OutstandingStore)) { score++; } } } return score; }";
        if(score.Body is null||!Same(score.Body,SyntaxFactory.ParseStatement(expectedScore)))throw new InvalidDataException("Pressure score calculation changed.");
        var pos=method.Identifier.GetLocation().GetLineSpan().StartLinePosition;var origin=new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2",commit,"HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/Smt/MicroOpScheduler.SMT.cs","CircuitWeave.PressureOrder.Projection","YAKSys_Hybrid_CPU.Core.MicroOpScheduler","PackBundleIntraCoreSmt",Hash(Encoding.UTF8.GetBytes("pressure-order/v1|"+string.Join("|",hashes))),HwSourceRole.Primary,pos.Line+1,pos.Character+1);
        var u1=new HwBitVectorType(1);var u3=new HwBitVectorType(3);var u6=new HwBitVectorType(6);var u64=new HwBitVectorType(64);var ports=new List<HwPortContract>{new("enabled",HwPortDirection.Input,u1),new("count",HwPortDirection.Input,u3),new("valid",HwPortDirection.Output,u1),new("swap_count",HwPortDirection.Output,u3)};var nodes=new List<HwNode>();
        for(int i=0;i<4;i++){ports.Add(new("id"+i,HwPortDirection.Input,u3));ports.Add(new("pressure"+i,HwPortDirection.Input,u6));ports.Add(new("ordered_id"+i,HwPortDirection.Output,u3));ports.Add(new("ordered_pressure"+i,HwPortDirection.Output,u6));}
        nodes.Add(new HwConstantNode("one3",u3,"001",origin));nodes.Add(new HwConstantNode("zero3",u3,"000",origin));nodes.Add(new HwConstantNode("one1",u1,"1",origin));nodes.Add(new HwConstantNode("limit64",u64,Convert.ToString(33,2).PadLeft(64,'0'),origin));nodes.Add(new HwOperationNode("count64","zext",["count"],u64,Origin:origin));nodes.Add(new HwConstantNode("five64",u64,Convert.ToString(5,2).PadLeft(64,'0'),origin));nodes.Add(new HwOperationNode("count_ok","ult64",["count64","five64"],u1,Origin:origin));string valid="count_ok";
        string[] ids=Enumerable.Range(0,4).Select(i=>"id"+i).ToArray(), pressures=Enumerable.Range(0,4).Select(i=>"pressure"+i).ToArray();
        for(int i=0;i<4;i++){nodes.Add(new HwOperationNode("p64_"+i,"zext",[pressures[i]],u64,Origin:origin));nodes.Add(new HwOperationNode("p_ok_"+i,"ult64",["p64_"+i,"limit64"],u1,Origin:origin));nodes.Add(new HwOperationNode("valid_"+i,"and",[valid,"p_ok_"+i],u1,Origin:origin));valid="valid_"+i;}
        string swaps="zero3";for(int i=0;i<3;i++)for(int j=i+1;j<4;j++){string p=$"s{i}{j}_";nodes.Add(new HwConstantNode(p+"threshold",u64,Convert.ToString(j,2).PadLeft(64,'0'),origin));nodes.Add(new HwOperationNode(p+"in_range","ult64",[p+"threshold","count64"],u1,Origin:origin));nodes.Add(new HwOperationNode(p+"left64","zext",[pressures[i]],u64,Origin:origin));nodes.Add(new HwOperationNode(p+"right64","zext",[pressures[j]],u64,Origin:origin));nodes.Add(new HwOperationNode(p+"less","ult64",[p+"right64",p+"left64"],u1,Origin:origin));nodes.Add(new HwOperationNode(p+"eligible","and",[p+"in_range","enabled"],u1,Origin:origin));nodes.Add(new HwOperationNode(p+"swap","and",[p+"eligible",p+"less"],u1,Origin:origin));nodes.Add(new HwMuxNode(p+"id_left",p+"swap",ids[j],ids[i],u3,origin));nodes.Add(new HwMuxNode(p+"id_right",p+"swap",ids[i],ids[j],u3,origin));nodes.Add(new HwMuxNode(p+"pressure_left",p+"swap",pressures[j],pressures[i],u6,origin));nodes.Add(new HwMuxNode(p+"pressure_right",p+"swap",pressures[i],pressures[j],u6,origin));ids[i]=p+"id_left";ids[j]=p+"id_right";pressures[i]=p+"pressure_left";pressures[j]=p+"pressure_right";nodes.Add(new HwOperationNode(p+"sum","add",[swaps,"one3"],u3,Origin:origin));nodes.Add(new HwMuxNode(p+"count",p+"swap",p+"sum",swaps,u3,origin));swaps=p+"count";}
        nodes.Add(new HwOutputDriveNode("drive_valid","valid",valid,origin));nodes.Add(new HwOutputDriveNode("drive_swaps","swap_count",swaps,origin));for(int i=0;i<4;i++){nodes.Add(new HwOutputDriveNode("drive_id"+i,"ordered_id"+i,ids[i],origin));nodes.Add(new HwOutputDriveNode("drive_pressure"+i,"ordered_pressure"+i,pressures[i],origin));}
        var top=new HwModule("smt_pressure_order_projection","smt_pressure_order_projection",ports,nodes,[],new HwHardwareContract("scheduler.smt-pressure-order.unqualified.v1",HwModuleKind.Combinational,ports,new HwTimingContract(0,1)),origin);var result=new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",top.Id,[top],toolchain,[]));return result.Validated??throw new InvalidDataException("Pressure ordering HWIR refused: "+string.Join("; ",result.Diagnostics.Select(d=>d.Message)));
    }
}
#endif
