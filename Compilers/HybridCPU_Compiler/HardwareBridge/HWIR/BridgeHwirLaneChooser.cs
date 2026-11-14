#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Combinational replay-aware lane chooser; caller supplies the already qualified free mask.</summary>
public static class BridgeHwirLaneChooser
{
    public static HwValidatedDesign Compile(byte[] source,string expectedHash,string commit,HwToolchainIdentity toolchain)
    {
        static string Hash(byte[] b)=>Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        if(source.Length>2_000_000 || Hash(source)!=expectedHash || commit.Length!=40 || commit.Any(c=>!Uri.IsHexDigit(c))) throw new InvalidDataException("Lane chooser source identity differs.");
        var tree=CSharpSyntaxTree.ParseText(new UTF8Encoding(false,true).GetString(source));
        var owner=tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c=>c.Identifier.ValueText=="DeterministicLaneChooser" && c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString()=="YAKSys_Hybrid_CPU.Core");
        var method=owner.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="SelectWithReplayHint");
        const string expected="{ if (freeLanes == 0) return -1; if (replayActive && previousLane >= 0 && previousLane < 8) { if ((freeLanes & (1 << previousLane)) != 0) return previousLane; } return BitOperations.TrailingZeroCount(freeLanes); }";
        if(method.ReturnType.ToString()!="int" || method.ParameterList.Parameters.Count!=3 || method.ParameterList.Parameters[0].Type?.ToString()!="byte" ||
           method.ParameterList.Parameters[1].Type?.ToString()!="bool" || method.ParameterList.Parameters[2].Type?.ToString()!="int" ||
           method.Body is null || !method.Body.DescendantTokens().Select(t=>t.Text).SequenceEqual(SyntaxFactory.ParseStatement(expected).DescendantTokens().Select(t=>t.Text))) throw new InvalidDataException("Lane chooser contract changed.");
        var position=method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        var origin=new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2",commit,"HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/LaneChoice/DeterministicLaneChooser.cs",
            "CircuitWeave.LaneChooser.Projection","YAKSys_Hybrid_CPU.Core.DeterministicLaneChooser","SelectWithReplayHint",Hash(Encoding.UTF8.GetBytes("lane-chooser/v1|"+expectedHash)),HwSourceRole.Primary,position.Line+1,position.Character+1);
        var u1=new HwBitVectorType(1); var u3=new HwBitVectorType(3); var u8=new HwBitVectorType(8); var u32=new HwBitVectorType(32);
        HwPortContract[] ports=[new("free_lanes",HwPortDirection.Input,u8),new("replay_active",HwPortDirection.Input,u1),new("previous_lane",HwPortDirection.Input,u32),
            new("valid",HwPortDirection.Output,u1),new("selected_lane",HwPortDirection.Output,u3),new("reused",HwPortDirection.Output,u1)];
        var nodes=new List<HwNode> { new HwConstantNode("zero8",u8,"00000000",origin),new HwConstantNode("zero3",u3,"000",origin),new HwConstantNode("zero1",u1,"0",origin),
            new HwConstantNode("one1",u1,"1",origin),new HwOperationNode("none","eq",["free_lanes","zero8"],u1,Origin:origin),new HwOperationNode("has_lane","xor",["none","one1"],u1,Origin:origin) };
        string lowest="zero3",reuse="zero1",previous="zero3";
        for(int i=7;i>=0;i--)
        {
            string p="lane"+i+"_";
            nodes.Add(new HwConstantNode(p+"mask",u8,Convert.ToString(1<<i,2).PadLeft(8,'0'),origin)); nodes.Add(new HwConstantNode(p+"id",u3,Convert.ToString(i,2).PadLeft(3,'0'),origin));
            nodes.Add(new HwConstantNode(p+"previous",u32,Convert.ToString(i,2).PadLeft(32,'0'),origin));
            nodes.Add(new HwOperationNode(p+"masked","and",["free_lanes",p+"mask"],u8,Origin:origin)); nodes.Add(new HwOperationNode(p+"clear","eq",[p+"masked","zero8"],u1,Origin:origin));
            nodes.Add(new HwOperationNode(p+"free","xor",[p+"clear","one1"],u1,Origin:origin)); nodes.Add(new HwOperationNode(p+"previous_match","eq",["previous_lane",p+"previous"],u1,Origin:origin));
            nodes.Add(new HwOperationNode(p+"hint_free","and",[p+"previous_match",p+"free"],u1,Origin:origin));
            nodes.Add(new HwMuxNode(p+"lowest",p+"free",p+"id",lowest,u3,origin)); nodes.Add(new HwMuxNode(p+"previous_result",p+"hint_free",p+"id",previous,u3,origin));
            nodes.Add(new HwOperationNode(p+"reuse_or","or",[p+"hint_free",reuse],u1,Origin:origin));
            lowest=p+"lowest"; previous=p+"previous_result"; reuse=p+"reuse_or";
        }
        nodes.Add(new HwOperationNode("reuse_active","and",["replay_active",reuse],u1,Origin:origin));
        nodes.Add(new HwMuxNode("selected","reuse_active",previous,lowest,u3,origin));
        nodes.Add(new HwOutputDriveNode("drive_valid","valid","has_lane",origin)); nodes.Add(new HwOutputDriveNode("drive_selected","selected_lane","selected",origin)); nodes.Add(new HwOutputDriveNode("drive_reused","reused","reuse_active",origin));
        var module=new HwModule("replay_lane_chooser_projection","replay_lane_chooser_projection",ports,nodes,[],new HwHardwareContract("scheduler.replay-lane-chooser.unqualified.v1",HwModuleKind.Combinational,ports,new HwTimingContract(0,1)),origin);
        var result=new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",module.Id,[module],toolchain,[]));
        return result.Validated ?? throw new InvalidDataException("Lane chooser HWIR refused: "+string.Join("; ",result.Diagnostics.Select(d=>d.Message)));
    }
}
#endif
