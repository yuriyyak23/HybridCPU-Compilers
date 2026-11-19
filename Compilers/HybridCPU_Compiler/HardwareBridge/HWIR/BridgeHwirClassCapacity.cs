#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Alias-aware capacity query; counter updates are external state.</summary>
public static class BridgeHwirClassCapacity
{
    public static HwValidatedDesign AttachToStageA(HwValidatedDesign stageA,HwValidatedDesign capacity)
    {
        ArgumentNullException.ThrowIfNull(stageA);ArgumentNullException.ThrowIfNull(capacity);
        if(stageA.Design.Toolchain!=capacity.Design.Toolchain||stageA.Design.TopModule!="stage_a_control_projection"||capacity.Design.TopModule!="class_capacity_projection"||stageA.Design.Modules.Count!=1||capacity.Design.Modules.Count!=1)throw new InvalidDataException("Incompatible Stage A and capacity candidates.");
        var control=stageA.Design.Modules.Single();var cap=capacity.Design.Modules.Single();var origin=control.Origin!;var u1=new HwBitVectorType(1);
        var ports=control.Ports.Where(p=>p.Name!="capacity_free").Concat(cap.Ports.Where(p=>p.Direction==HwPortDirection.Input&&p.Name!="slot_class")).ToArray();
        var nodes=new List<HwNode>{new HwWireNode("capacity_free",u1,origin)};
        var aConnections=control.Ports.ToDictionary(p=>p.Name,p=>p.Name,StringComparer.Ordinal);
        var capacityConnections=cap.Ports.ToDictionary(p=>p.Name,p=>p.Name=="free"?"capacity_free":p.Name,StringComparer.Ordinal);
        var children=new[]{new HwInstanceNode("control","stage_a_control_projection",aConnections,new Dictionary<string,string>(),Origin:origin),new HwInstanceNode("capacity","class_capacity_projection",capacityConnections,new Dictionary<string,string>(),Origin:origin)};
        var top=new HwModule("stage_a_capacity_projection","stage_a_capacity_projection",ports,nodes,children,new HwHardwareContract("scheduler.stage-a-capacity.unqualified.v1",HwModuleKind.Combinational,ports,new HwTimingContract(0,1)),origin);
        var result=new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",top.Id,[top,control,cap],stageA.Design.Toolchain,[]));return result.Validated??throw new InvalidDataException("Composed Stage A capacity HWIR refused: "+string.Join("; ",result.Diagnostics.Select(d=>d.Message)));
    }
    public static HwValidatedDesign Compile(byte[] capacity,byte[] classes,string[] hashes,string commit,HwToolchainIdentity toolchain)
    {
        static string Hash(byte[] b)=>Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        if(hashes.Length!=2||capacity.Length>2_000_000||classes.Length>2_000_000||Hash(capacity)!=hashes[0]||Hash(classes)!=hashes[1]||commit.Length!=40||commit.Any(c=>!Uri.IsHexDigit(c)))throw new InvalidDataException("Capacity source identity differs.");
        static SyntaxNode Parse(byte[] b)=>CSharpSyntaxTree.ParseText(new UTF8Encoding(false,true).GetString(b)).GetRoot();
        static bool Same(SyntaxNode a,SyntaxNode b)=>a.DescendantTokens().Select(t=>t.Text).SequenceEqual(b.DescendantTokens().Select(t=>t.Text));
        var root=Parse(capacity);var type=root.DescendantNodes().OfType<StructDeclarationSyntax>().Single(s=>s.Identifier.ValueText=="SlotClassCapacityState"&&s.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString()=="YAKSys_Hybrid_CPU.Core");
        var has=type.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="HasFreeCapacity");
        const string hasBody="{ int free = GetFreeCapacity(slotClass); if (free <= 0) return false; ReadOnlySpan<SlotClass> aliased = SlotClassLaneMap.GetAliasedClasses(slotClass); int aliasedOccupied = 0; foreach (SlotClass alias in aliased) aliasedOccupied += GetOccupied(alias); return free - aliasedOccupied > 0; }";
        if(has.Body is null||!Same(has.Body,SyntaxFactory.ParseStatement(hasBody)))throw new InvalidDataException("Capacity guard changed.");
        var free=type.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="GetFreeCapacity");var occupied=type.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="GetOccupied");
        const string freeExpr="slotClass switch { SlotClass.AluClass => AluTotal - AluOccupied, SlotClass.LsuClass => LsuTotal - LsuOccupied, SlotClass.DmaStreamClass => DmaStreamTotal - DmaStreamOccupied, SlotClass.MatrixTileStreamClass => MatrixTileStreamTotal - MatrixTileStreamOccupied, SlotClass.BranchControl => BranchControlTotal - BranchControlOccupied, SlotClass.SystemSingleton => SystemSingletonTotal - SystemSingletonOccupied, _ => 0, }";
        const string occupiedExpr="slotClass switch { SlotClass.AluClass => AluOccupied, SlotClass.LsuClass => LsuOccupied, SlotClass.DmaStreamClass => DmaStreamOccupied, SlotClass.MatrixTileStreamClass => MatrixTileStreamOccupied, SlotClass.BranchControl => BranchControlOccupied, SlotClass.SystemSingleton => SystemSingletonOccupied, _ => 0, }";
        if(free.ExpressionBody is null||occupied.ExpressionBody is null||!Same(free.ExpressionBody.Expression,SyntaxFactory.ParseExpression(freeExpr))||!Same(occupied.ExpressionBody.Expression,SyntaxFactory.ParseExpression(occupiedExpr)))throw new InvalidDataException("Capacity counter mapping changed.");
        string[] stems=["Alu","Lsu","DmaStream","BranchControl","SystemSingleton","MatrixTileStream"];
        foreach(string stem in stems)foreach(string? suffix in new[]{"Total","Occupied"})if(!type.Members.OfType<FieldDeclarationSyntax>().Any(f=>f.Declaration.Type.ToString()=="byte"&&f.Declaration.Variables.Any(v=>v.Identifier.ValueText==stem+suffix)))throw new InvalidDataException("Capacity counter type changed.");
        var classRoot=Parse(classes);var map=classRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c=>c.Identifier.ValueText=="SlotClassLaneMap");var aliases=map.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="GetAliasedClasses");
        const string aliasesExpr="slotClass switch { SlotClass.BranchControl => [SlotClass.SystemSingleton], SlotClass.SystemSingleton => [SlotClass.BranchControl], SlotClass.DmaStreamClass => [SlotClass.MatrixTileStreamClass], SlotClass.MatrixTileStreamClass => [SlotClass.DmaStreamClass], _ => [] }";
        if(aliases.ExpressionBody is null||!Same(aliases.ExpressionBody.Expression,SyntaxFactory.ParseExpression(aliasesExpr)))throw new InvalidDataException("Capacity alias topology changed.");
        var enumType=classRoot.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(e=>e.Identifier.ValueText=="SlotClass");string[] names=["AluClass","LsuClass","DmaStreamClass","BranchControl","SystemSingleton","MatrixTileStreamClass"];
        for(int i=0;i<names.Length;i++)if(Convert.ToInt32(((LiteralExpressionSyntax)enumType.Members.Single(m=>m.Identifier.ValueText==names[i]).EqualsValue!.Value).Token.Value)!=i)throw new InvalidDataException("Slot class encoding changed.");
        var pos=has.Identifier.GetLocation().GetLineSpan().StartLinePosition;var origin=new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2",commit,"HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/SlotLegality/SlotClassCapacity.cs","CircuitWeave.Capacity.Projection","YAKSys_Hybrid_CPU.Core.SlotClassCapacityState","HasFreeCapacity",Hash(Encoding.UTF8.GetBytes("class-capacity/v1|"+string.Join("|",hashes))),HwSourceRole.Primary,pos.Line+1,pos.Character+1);
        var u1=new HwBitVectorType(1);var u8=new HwBitVectorType(8);var u64=new HwBitVectorType(64);
        var ports=new List<HwPortContract>{new("slot_class",HwPortDirection.Input,u8),new("free",HwPortDirection.Output,u1)};
        for(int i=0;i<6;i++){ports.Add(new("total"+i,HwPortDirection.Input,u8));ports.Add(new("occupied"+i,HwPortDirection.Input,u8));}
        var nodes=new List<HwNode>{new HwConstantNode("zero1",u1,"0",origin)};string result="zero1";
        int[] alias=[-1,-1,5,4,3,2];
        for(int i=0;i<6;i++)
        {
            string p="c"+i+"_";nodes.Add(new HwConstantNode(p+"id",u8,Convert.ToString(i,2).PadLeft(8,'0'),origin));nodes.Add(new HwOperationNode(p+"match","eq",["slot_class",p+"id"],u1,Origin:origin));
            nodes.Add(new HwOperationNode(p+"total","zext",["total"+i],u64,Origin:origin));nodes.Add(new HwOperationNode(p+"occupied","zext",["occupied"+i],u64,Origin:origin));
            string used=p+"occupied";if(alias[i]>=0){nodes.Add(new HwOperationNode(p+"alias","zext",["occupied"+alias[i]],u64,Origin:origin));nodes.Add(new HwOperationNode(p+"sum","add",[used,p+"alias"],u64,Origin:origin));used=p+"sum";}
            nodes.Add(new HwOperationNode(p+"free","ult64",[used,p+"total"],u1,Origin:origin));nodes.Add(new HwMuxNode(p+"result",p+"match",p+"free",result,u1,origin));result=p+"result";
        }
        nodes.Add(new HwOutputDriveNode("drive_free","free",result,origin));
        var module=new HwModule("class_capacity_projection","class_capacity_projection",ports,nodes,[],new HwHardwareContract("scheduler.class-capacity.unqualified.v1",HwModuleKind.Combinational,ports,new HwTimingContract(0,1)),origin);
        var validated=new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",module.Id,[module],toolchain,[]));return validated.Validated??throw new InvalidDataException("Capacity HWIR refused: "+string.Join("; ",validated.Diagnostics.Select(d=>d.Message)));
    }
}
#endif
