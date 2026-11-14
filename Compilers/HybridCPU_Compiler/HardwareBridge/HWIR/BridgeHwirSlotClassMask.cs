#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Static lane eligibility only; no allocation, pinning or scheduling is inferred.</summary>
public static class BridgeHwirSlotClassMask
{
    public static HwValidatedDesign Compile(byte[] source, string expectedHash, string commit, HwToolchainIdentity toolchain)
    {
        static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        if (source.Length > 2_000_000 || Hash(source) != expectedHash || commit.Length != 40 || commit.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Slot class source identity differs.");
        var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(source)); var root = tree.GetRoot();
        var type = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "SlotClassLaneMap" && c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core");
        var slotEnum = root.DescendantNodes().OfType<EnumDeclarationSyntax>().Single(e => e.Identifier.ValueText == "SlotClass" && e.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core");
        if (slotEnum.BaseList?.Types.Single().ToString() != "byte") throw new InvalidDataException("SlotClass backing changed.");
        var classes = slotEnum.Members.ToDictionary(m => m.Identifier.ValueText, m => Convert.ToByte(((LiteralExpressionSyntax)m.EqualsValue!.Value).Token.Value), StringComparer.Ordinal);
        var constants = type.Members.OfType<FieldDeclarationSyntax>().Where(f => f.Modifiers.Any(SyntaxKind.ConstKeyword) && f.Declaration.Type.ToString() == "byte")
            .SelectMany(f => f.Declaration.Variables).ToDictionary(v => v.Identifier.ValueText, v => Convert.ToByte(((LiteralExpressionSyntax)v.Initializer!.Value).Token.Value), StringComparer.Ordinal);
        var method = type.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "GetLaneMask");
        if (method.ReturnType.ToString() != "byte" || method.ParameterList.Parameters.Count != 1 || method.ParameterList.Parameters[0].Type?.ToString() != "SlotClass" ||
            method.ExpressionBody?.Expression is not SwitchExpressionSyntax sw || sw.GoverningExpression.ToString() != "slotClass") throw new InvalidDataException("Lane mask signature changed.");
        var entries = new List<(byte Code, byte Mask)>();
        foreach (var arm in sw.Arms)
        {
            if (arm.Pattern is DiscardPatternSyntax)
            {
                if (arm.Expression is not ThrowExpressionSyntax || arm != sw.Arms.Last()) throw new InvalidDataException("Lane mask fallback changed.");
                continue;
            }
            var name = arm.Pattern.ToString();
            if (!name.StartsWith("SlotClass.", StringComparison.Ordinal) || arm.Expression is not IdentifierNameSyntax mask ||
                !classes.TryGetValue(name[10..], out byte code) || !constants.TryGetValue(mask.Identifier.ValueText, out byte value) || arm.WhenClause is not null)
                throw new InvalidDataException("Unsupported lane mask arm.");
            entries.Add((code, value));
        }
        if (entries.Count != classes.Count || entries.Select(e => e.Code).Distinct().Count() != entries.Count || sw.Arms.Count != entries.Count + 1)
            throw new InvalidDataException("Incomplete or ambiguous lane classes.");
        var pos = method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2",commit,"HybridCPU_ISE/CloseToHSL/Core/Pipeline/Scheduling/SlotLegality/SlotClassDefinitions.cs",
            "CircuitWeave.SlotClass.Projection","YAKSys_Hybrid_CPU.Core.SlotClassLaneMap","GetLaneMask",Hash(Encoding.UTF8.GetBytes("slot-class-mask/v1|"+expectedHash)),HwSourceRole.Primary,pos.Line+1,pos.Character+1);
        var u1=new HwBitVectorType(1); var u8=new HwBitVectorType(8);
        HwPortContract[] ports=[new("slot_class",HwPortDirection.Input,u8),new("valid",HwPortDirection.Output,u1),new("lane_mask",HwPortDirection.Output,u8)];
        var nodes=new List<HwNode> { new HwConstantNode("invalid",u1,"0",origin),new HwConstantNode("valid_bit",u1,"1",origin),new HwConstantNode("zero_mask",u8,"00000000",origin) };
        string valid="invalid", maskResult="zero_mask";
        foreach (var (entry,i) in entries.OrderBy(e=>e.Code).Select((entry,i)=>(entry,i)))
        {
            string p="class"+i+"_";
            nodes.Add(new HwConstantNode(p+"code",u8,Convert.ToString(entry.Code,2).PadLeft(8,'0'),origin));
            nodes.Add(new HwConstantNode(p+"mask",u8,Convert.ToString(entry.Mask,2).PadLeft(8,'0'),origin));
            nodes.Add(new HwOperationNode(p+"match","eq",["slot_class",p+"code"],u1,Origin:origin));
            nodes.Add(new HwMuxNode(p+"valid",p+"match","valid_bit",valid,u1,origin));
            nodes.Add(new HwMuxNode(p+"result",p+"match",p+"mask",maskResult,u8,origin));
            valid=p+"valid"; maskResult=p+"result";
        }
        nodes.Add(new HwOutputDriveNode("drive_valid","valid",valid,origin)); nodes.Add(new HwOutputDriveNode("drive_mask","lane_mask",maskResult,origin));
        var module=new HwModule("slot_class_mask_projection","slot_class_mask_projection",ports,nodes,[],new HwHardwareContract("scheduler.slot-class-mask.unqualified.v1",HwModuleKind.Combinational,ports,new HwTimingContract(0,1)),origin);
        var result=new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",module.Id,[module],toolchain,[]));
        return result.Validated ?? throw new InvalidDataException("Slot mask HWIR refused: "+string.Join("; ",result.Diagnostics.Select(d=>d.Message)));
    }
}
#endif
