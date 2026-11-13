#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Candidate projection of raw slot fields, not the complete decoder or exception contract.</summary>
public static class BridgeHwirRawSlot
{
    /// <summary>
    /// Eight-slot source projection. For a valid bundle and opcode-zero slot only,
    /// empty_failure_kind is candidate encoding 0=none, 1=carrier shape, 2=sideband;
    /// it is not the numeric DecodeFailureCode or a qualified exception protocol.
    /// All slot outputs are unspecified by this adapter when bundle valid is false.
    /// </summary>
    public static HwValidatedDesign CompileBundle(byte[] stages, byte[] layout, byte[] metadata, string stagesSha, string layoutSha, string metadataSha, string commit, HwToolchainIdentity toolchain)
    {
        var slot = Compile(stages, layout, metadata, stagesSha, layoutSha, metadataSha, commit, toolchain);
        var tree = CSharpSyntaxTree.ParseText(new UTF8Encoding(false, true).GetString(stages));
        var reader = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "RawBundleReader" && c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core.Decoder");
        var method = reader.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "Read");
        var expected = "{ if (bundle.Length != BundleMetadata.BundleSlotCount) { throw new ArgumentException($\"Decoder bundles must contain exactly {BundleMetadata.BundleSlotCount} slots.\", nameof(bundle)); } ImmutableArray<RawSlot>.Builder slots = ImmutableArray.CreateBuilder<RawSlot>(BundleMetadata.BundleSlotCount); for (int slotIndex = 0; slotIndex < bundle.Length; slotIndex++) { slots.Add(RawSlotReader.Read(in bundle[slotIndex], slotIndex)); } return new RawBundle(slots.MoveToImmutable()); }";
        if (method.Body?.NormalizeWhitespace().ToFullString() != SyntaxFactory.ParseStatement(expected).NormalizeWhitespace().ToFullString()) throw new InvalidDataException("Raw bundle reader changed.");
        var pipeline = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "DeclarativeDecoderPipeline" && c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core.Decoder");
        var decode = pipeline.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "TryDecodeBundle" && m.ParameterList.Parameters.Count == 7);
        var loop = decode.DescendantNodes().OfType<ForEachStatementSyntax>().Single(f => f.Identifier.ValueText == "slot");
        if (loop.Expression.NormalizeWhitespace().ToFullString() != "rawBundle.Slots" || loop.Statement.DescendantNodes().OfType<IfStatementSyntax>().Count(x => x.Condition.NormalizeWhitespace().ToFullString() == "slot.Opcode == 0") != 1)
            throw new InvalidDataException("Bundle empty-slot dispatch changed.");
        var expectedLoop = "{ InstructionSlotMetadata slotMetadata = ResolveSlotMetadata(bundleAnnotations, slot.SlotIndex); if (slot.Opcode == 0) { if (!TryDecodeEmptySlot(in slot, slotMetadata, out CanonicalDecodedInstruction emptySlot, out failure)) { decodedBundle = null; return false; } decodedSlots.Add(null); canonicalSlots.Add(emptySlot); continue; } if (!TryDecodeOccupiedSlot(in slot, slotMetadata, out DeclarativeDecodedSlot? decodedSlot, out failure)) { decodedBundle = null; return false; } decodedSlots.Add(decodedSlot); canonicalSlots.Add(decodedSlot!.CanonicalInstruction); }";
        if (loop.Statement.NormalizeWhitespace().ToFullString() != SyntaxFactory.ParseStatement(expectedLoop).NormalizeWhitespace().ToFullString())
            throw new InvalidDataException("Bundle slot order or early-return contract changed.");
        var emptyMethod=pipeline.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="TryDecodeEmptySlot");
        var emptyGuard=emptyMethod.Body?.Statements.OfType<IfStatementSyntax>().FirstOrDefault();
        if (emptyGuard?.Condition.NormalizeWhitespace().ToFullString() != "slot.Word0 != 0 || slot.Word1 != 0 || slot.Word2 != 0 || slot.Word3 != 0") throw new InvalidDataException("Empty-slot carrier guard changed.");
        var sidebandGuard=emptyMethod.Body?.Statements.OfType<IfStatementSyntax>().Skip(1).FirstOrDefault();
        if (sidebandGuard?.Condition.NormalizeWhitespace().ToFullString() != "slotMetadata.DmaStreamComputeDescriptor is not null || slotMetadata.AcceleratorCommandDescriptor is not null") throw new InvalidDataException("Empty-slot sideband guard changed.");
        static bool FailureBranch(IfStatementSyntax branch,string code) => branch.Statement.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(m=>m.ToString()=="DecodeFailureCode."+code) && branch.Statement.DescendantNodes().OfType<ReturnStatementSyntax>().Any(r=>r.Expression?.IsKind(SyntaxKind.FalseLiteralExpression)==true);
        if (emptyGuard is null || sidebandGuard is null || emptyMethod.Body!.Statements[0] != emptyGuard || emptyMethod.Body.Statements[1] != sidebandGuard || !FailureBranch(emptyGuard,"BundleShape") || !FailureBranch(sidebandGuard,"Sideband")) throw new InvalidDataException("Empty-slot failure priority changed.");
        var pos = method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        var origin = slot.Design.Modules.Single().Origin! with { Member = "Read", Type = "YAKSys_Hybrid_CPU.Core.Decoder.RawBundleReader", Assembly = "CircuitWeave.RawBundle.Projection", IdentityDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("raw-bundle-projection/v1|" + stagesSha + "|" + layoutSha + "|" + metadataSha))).ToLowerInvariant(), Line = pos.Line + 1, Column = pos.Character + 1 };
        var u1=new HwBitVectorType(1); var u2=new HwBitVectorType(2); var u3=new HwBitVectorType(3); var u16=new HwBitVectorType(16); var u32=new HwBitVectorType(32); var u64=new HwBitVectorType(64);
        var decodePos=decode.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        var emptyOrigin=origin with { Type="YAKSys_Hybrid_CPU.Core.Decoder.DeclarativeDecoderPipeline", Member="TryDecodeBundle", Assembly="CircuitWeave.EmptySlot.Projection", IdentityDigest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("empty-slot-projection/v1|"+stagesSha))).ToLowerInvariant(), Line=decodePos.Line+1, Column=decodePos.Character+1 };
        HwPortContract[] emptyPorts=[new("opcode",HwPortDirection.Input,u16),new("empty",HwPortDirection.Output,u1)];
        HwNode[] emptyNodes=[new HwConstantNode("zero",u16,new string('0',16),emptyOrigin),new HwOperationNode("is_empty","eq",["opcode","zero"],u1,Origin:emptyOrigin),new HwOutputDriveNode("drive_empty","empty","is_empty",emptyOrigin)];
        var emptyModule=new HwModule("raw_slot_empty_projection","raw_slot_empty_projection",emptyPorts,emptyNodes,[],new HwHardwareContract("decoder.empty-slot-projection.unqualified.v1",HwModuleKind.Combinational,emptyPorts,new HwTimingContract(0,1)),emptyOrigin);
        var emptyGuardPos=emptyMethod.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        var zeroOrigin=origin with { Type="YAKSys_Hybrid_CPU.Core.Decoder.DeclarativeDecoderPipeline", Member="TryDecodeEmptySlot", Assembly="CircuitWeave.ZeroCarrier.Projection", IdentityDigest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("zero-carrier-projection/v1|"+stagesSha))).ToLowerInvariant(), Line=emptyGuardPos.Line+1, Column=emptyGuardPos.Character+1 };
        HwPortContract[] zeroPorts=[new("word0",HwPortDirection.Input,u64),new("word1",HwPortDirection.Input,u64),new("word2",HwPortDirection.Input,u64),new("word3",HwPortDirection.Input,u64),new("dma_present",HwPortDirection.Input,u1),new("accelerator_present",HwPortDirection.Input,u1),new("all_zero",HwPortDirection.Output,u1),new("descriptor_present",HwPortDirection.Output,u1),new("failure_kind",HwPortDirection.Output,u2)];
        HwNode[] zeroNodes=[new HwConstantNode("zero",u64,new string('0',64),zeroOrigin),new HwConstantNode("ok",u2,"00",zeroOrigin),new HwConstantNode("shape",u2,"01",zeroOrigin),new HwConstantNode("sideband_failure",u2,"10",zeroOrigin),new HwOperationNode("or01","or",["word0","word1"],u64,Origin:zeroOrigin),new HwOperationNode("or23","or",["word2","word3"],u64,Origin:zeroOrigin),new HwOperationNode("combined","or",["or01","or23"],u64,Origin:zeroOrigin),new HwOperationNode("is_zero","eq",["combined","zero"],u1,Origin:zeroOrigin),new HwOperationNode("sideband","or",["dma_present","accelerator_present"],u1,Origin:zeroOrigin),new HwMuxNode("sideband_result","sideband","sideband_failure","ok",u2,zeroOrigin),new HwMuxNode("failure","is_zero","sideband_result","shape",u2,zeroOrigin),new HwOutputDriveNode("drive","all_zero","is_zero",zeroOrigin),new HwOutputDriveNode("drive_sideband","descriptor_present","sideband",zeroOrigin),new HwOutputDriveNode("drive_failure","failure_kind","failure",zeroOrigin)];
        var zeroModule=new HwModule("raw_slot_zero_carrier_projection","raw_slot_zero_carrier_projection",zeroPorts,zeroNodes,[],new HwHardwareContract("decoder.zero-carrier-projection.unqualified.v1",HwModuleKind.Combinational,zeroPorts,new HwTimingContract(0,1)),zeroOrigin);
        // Prefix result: 0 = all eight empty slots accepted, 1 = first empty-slot failure,
        // 2 = occupied slot requires the unimplemented decoder, 3 = invalid bundle length.
        // Slot index is meaningful only for outcomes 1 and 2; failure kind only for 1.
        var ports=new List<HwPortContract> { new("bundle_length",HwPortDirection.Input,u32), new("valid",HwPortDirection.Output,u1),
            new("prefix_outcome",HwPortDirection.Output,u2),new("prefix_stop_slot",HwPortDirection.Output,u3),new("prefix_failure_kind",HwPortDirection.Output,u2) };
        var nodes=new List<HwNode> { new HwConstantNode("eight",u32,Convert.ToString(8,2).PadLeft(32,'0'),origin), new HwOperationNode("length_valid","eq",["bundle_length","eight"],u1,Origin:origin), new HwOutputDriveNode("drive_valid","valid","length_valid",origin) };
        var instances=new List<HwInstanceNode>();
        for (var i=0;i<8;i++)
        {
            var prefix="slot"+i+"_"; nodes.Add(new HwConstantNode(prefix+"index",u32,Convert.ToString(i,2).PadLeft(32,'0'),origin));
            nodes.Add(new HwWireNode(prefix+"child_valid",u1,origin)); nodes.Add(new HwWireNode(prefix+"opcode_wire",u16,origin));
            nodes.Add(new HwWireNode(prefix+"empty_wire",u1,origin)); nodes.Add(new HwWireNode(prefix+"failure_wire",u2,origin));
            nodes.Add(new HwOutputDriveNode(prefix+"drive_empty",prefix+"empty",prefix+"empty_wire",origin));
            nodes.Add(new HwOutputDriveNode(prefix+"drive_failure",prefix+"empty_failure_kind",prefix+"failure_wire",origin));
            nodes.Add(new HwOutputDriveNode(prefix+"drive_opcode",prefix+"opcode",prefix+"opcode_wire",origin));
            var connections=new Dictionary<string,string> { ["slot_index"]=prefix+"index", ["valid"]=prefix+"child_valid", ["opcode"]=prefix+"opcode_wire" };
            ports.Add(new(prefix+"opcode",HwPortDirection.Output,u16));
            ports.Add(new(prefix+"empty",HwPortDirection.Output,u1));
            ports.Add(new(prefix+"all_zero",HwPortDirection.Output,u1));
            ports.Add(new(prefix+"dma_present",HwPortDirection.Input,u1)); ports.Add(new(prefix+"accelerator_present",HwPortDirection.Input,u1)); ports.Add(new(prefix+"descriptor_present",HwPortDirection.Output,u1));
            ports.Add(new(prefix+"empty_failure_kind",HwPortDirection.Output,u2));
            for (var n=0;n<4;n++) { var word="word"+n; ports.Add(new(prefix+word,HwPortDirection.Input,u64)); ports.Add(new(prefix+"out_"+word,HwPortDirection.Output,u64)); connections[word]=prefix+word; connections["out_"+word]=prefix+"out_"+word; }
            instances.Add(new HwInstanceNode(prefix+"reader","raw_slot_projection",connections,new Dictionary<string,string>(),Origin:origin));
            instances.Add(new HwInstanceNode(prefix+"empty_classifier","raw_slot_empty_projection",new Dictionary<string,string> { ["opcode"]=prefix+"opcode_wire",["empty"]=prefix+"empty_wire" },new Dictionary<string,string>(),Origin:origin));
            instances.Add(new HwInstanceNode(prefix+"zero_carrier","raw_slot_zero_carrier_projection",new Dictionary<string,string> { ["word0"]=prefix+"word0",["word1"]=prefix+"word1",["word2"]=prefix+"word2",["word3"]=prefix+"word3",["dma_present"]=prefix+"dma_present",["accelerator_present"]=prefix+"accelerator_present",["all_zero"]=prefix+"all_zero",["descriptor_present"]=prefix+"descriptor_present",["failure_kind"]=prefix+"failure_wire" },new Dictionary<string,string>(),Origin:origin));
        }
        var prefixPorts=new List<HwPortContract> { new("length_valid",HwPortDirection.Input,u1),new("prefix_outcome",HwPortDirection.Output,u2),
            new("prefix_stop_slot",HwPortDirection.Output,u3),new("prefix_failure_kind",HwPortDirection.Output,u2) };
        var prefixConnections=new Dictionary<string,string> { ["length_valid"]="length_valid",["prefix_outcome"]="prefix_outcome",
            ["prefix_stop_slot"]="prefix_stop_slot",["prefix_failure_kind"]="prefix_failure_kind" };
        for(var i=0;i<8;i++) { var name="slot"+i+"_"; prefixPorts.Add(new(name+"empty_wire",HwPortDirection.Input,u1));
            prefixPorts.Add(new(name+"failure_wire",HwPortDirection.Input,u2)); prefixConnections[name+"empty_wire"]=name+"empty_wire"; prefixConnections[name+"failure_wire"]=name+"failure_wire"; }
        var prefixNodes=new List<HwNode> { new HwConstantNode("prefix_zero2",u2,"00",emptyOrigin),new HwConstantNode("prefix_fail",u2,"01",emptyOrigin),
            new HwConstantNode("prefix_deferred",u2,"10",emptyOrigin),new HwConstantNode("prefix_invalid",u2,"11",emptyOrigin),new HwConstantNode("prefix_zero3",u3,"000",emptyOrigin),new HwConstantNode("prefix_one1",u1,"1",emptyOrigin) };
        string state="prefix_zero2", index="prefix_zero3", kind="prefix_zero2";
        for (var i=7;i>=0;i--)
        {
            var p="prefix"+i+"_"; var slotPrefix="slot"+i+"_";
            prefixNodes.Add(new HwConstantNode(p+"index",u3,Convert.ToString(i,2).PadLeft(3,'0'),emptyOrigin));
            prefixNodes.Add(new HwOperationNode(p+"ok","eq",[slotPrefix+"failure_wire","prefix_zero2"],u1,Origin:emptyOrigin));
            prefixNodes.Add(new HwOperationNode(p+"failed","xor",[p+"ok","prefix_one1"],u1,Origin:emptyOrigin));
            prefixNodes.Add(new HwOperationNode(p+"occupied","xor",[slotPrefix+"empty_wire","prefix_one1"],u1,Origin:emptyOrigin));
            prefixNodes.Add(new HwOperationNode(p+"stop","or",[p+"occupied",p+"failed"],u1,Origin:emptyOrigin));
            prefixNodes.Add(new HwMuxNode(p+"empty_state",p+"failed","prefix_fail",state,u2,emptyOrigin));
            prefixNodes.Add(new HwMuxNode(p+"state",slotPrefix+"empty_wire",p+"empty_state","prefix_deferred",u2,emptyOrigin));
            prefixNodes.Add(new HwMuxNode(p+"index_result",p+"stop",p+"index",index,u3,emptyOrigin));
            prefixNodes.Add(new HwMuxNode(p+"empty_kind",p+"failed",slotPrefix+"failure_wire",kind,u2,emptyOrigin));
            prefixNodes.Add(new HwMuxNode(p+"kind",slotPrefix+"empty_wire",p+"empty_kind","prefix_zero2",u2,emptyOrigin));
            state=p+"state"; index=p+"index_result"; kind=p+"kind";
        }
        prefixNodes.Add(new HwMuxNode("prefix_final_state","length_valid",state,"prefix_invalid",u2,emptyOrigin));
        prefixNodes.Add(new HwMuxNode("prefix_final_index","length_valid",index,"prefix_zero3",u3,emptyOrigin));
        prefixNodes.Add(new HwMuxNode("prefix_final_kind","length_valid",kind,"prefix_zero2",u2,emptyOrigin));
        prefixNodes.Add(new HwOutputDriveNode("drive_prefix_outcome","prefix_outcome","prefix_final_state",emptyOrigin));
        prefixNodes.Add(new HwOutputDriveNode("drive_prefix_slot","prefix_stop_slot","prefix_final_index",emptyOrigin));
        prefixNodes.Add(new HwOutputDriveNode("drive_prefix_kind","prefix_failure_kind","prefix_final_kind",emptyOrigin));
        var prefixModule=new HwModule("empty_slot_prefix_projection","empty_slot_prefix_projection",prefixPorts,prefixNodes,[],
            new HwHardwareContract("decoder.empty-prefix-projection.unqualified.v1",HwModuleKind.Combinational,prefixPorts,new HwTimingContract(0,1)),emptyOrigin);
        instances.Add(new HwInstanceNode("empty_prefix","empty_slot_prefix_projection",prefixConnections,new Dictionary<string,string>(),Origin:origin));
        var top=new HwModule("raw_bundle_projection","raw_bundle_projection",ports,nodes,instances,new HwHardwareContract("decoder.raw-bundle-projection.unqualified.v1",HwModuleKind.Combinational,ports,new HwTimingContract(0,1)),origin);
        var validated=new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",top.Id,[top,slot.Design.Modules.Single(),emptyModule,zeroModule,prefixModule],slot.Design.Toolchain,[]));
        return validated.Validated ?? throw new InvalidDataException("Raw bundle HWIR refused: "+string.Join("; ",validated.Diagnostics.Select(d=>d.Message)));
    }
    public static HwValidatedDesign Compile(byte[] stages, byte[] layout, byte[] metadata, string stagesSha, string layoutSha, string metadataSha, string commit, HwToolchainIdentity toolchain)
    {
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (new[] { stages, layout, metadata }.Any(x => x.Length > 2_000_000) || Hash(stages) != stagesSha || Hash(layout) != layoutSha || Hash(metadata) != metadataSha || commit.Length != 40 || commit.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Raw-slot captured identity differs.");
        var utf8 = new UTF8Encoding(false, true);
        var stageTree = CSharpSyntaxTree.ParseText(utf8.GetString(stages));
        var layoutTree = CSharpSyntaxTree.ParseText(utf8.GetString(layout));
        var metadataTree = CSharpSyntaxTree.ParseText(utf8.GetString(metadata));
        var reader = stageTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "RawSlotReader" && c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core.Decoder");
        var method = reader.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "Read");
        var expected = "{ if ((uint)slotIndex >= BundleMetadata.BundleSlotCount) { throw new ArgumentOutOfRangeException(nameof(slotIndex)); } return new RawSlot(slotIndex, instruction, unchecked((ushort)instruction.OpCode), instruction.Word0, instruction.Word1, instruction.Word2, instruction.Word3); }";
        if (method.Body?.NormalizeWhitespace().ToFullString() != SyntaxFactory.ParseStatement(expected).NormalizeWhitespace().ToFullString()) throw new InvalidDataException("Raw-slot reader body changed.");
        var carrier = layoutTree.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>().Single(s => s.Identifier.ValueText == "VLIW_Instruction" && s.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "HybridCPU_ISE.Arch");
        for (var i = 0; i < 4; i++)
        {
            var name = "word" + i;
            var field = carrier.Members.OfType<FieldDeclarationSyntax>().Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == name));
            var prop = carrier.Members.OfType<PropertyDeclarationSyntax>().Single(p => p.Identifier.ValueText == "Word" + i);
            var getter = prop.AccessorList!.Accessors.Single(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
            if (field.Declaration.Type.ToString() != "ulong" || getter.Body?.NormalizeWhitespace().ToFullString() != SyntaxFactory.ParseStatement("{ return " + name + "; }").NormalizeWhitespace().ToFullString()) throw new InvalidDataException("Carrier word field or getter changed.");
        }
        var opcode = carrier.Members.OfType<PropertyDeclarationSyntax>().Single(p => p.Identifier.ValueText == "OpCode");
        if (opcode.Type.ToString() != "uint" || opcode.AccessorList!.Accessors.Single(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)).Body?.NormalizeWhitespace().ToFullString() != SyntaxFactory.ParseStatement("{ return (uint)((word0 >> 48) & 0xFFFF); }").NormalizeWhitespace().ToFullString()) throw new InvalidDataException("Carrier opcode getter changed.");
        var bundle = metadataTree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().Single(c => c.Identifier.ValueText == "BundleMetadata");
        var count = bundle.Members.OfType<FieldDeclarationSyntax>().Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "BundleSlotCount"));
        if (count.Declaration.Type.ToString() != "int" || !count.Modifiers.Any(SyntaxKind.ConstKeyword) || count.Declaration.Variables.Single().Initializer?.Value.ToString() != "8") throw new InvalidDataException("Bundle size changed.");
        var rawSlot = stageTree.GetRoot().DescendantNodes().OfType<RecordDeclarationSyntax>().Single(r => r.Identifier.ValueText == "RawSlot" && r.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Single().Name.ToString() == "YAKSys_Hybrid_CPU.Core.Decoder");
        var projectedFields = carrier.Members.OfType<FieldDeclarationSyntax>().Where(f => f.Declaration.Variables.Any(v => new[] { "word0", "word1", "word2", "word3" }.Contains(v.Identifier.ValueText))).ToArray();
        var projectedProperties = carrier.Members.OfType<PropertyDeclarationSyntax>().Where(p => new[] { "Word0", "Word1", "Word2", "Word3", "OpCode" }.Contains(p.Identifier.ValueText)).ToArray();
        var projected = "using System; using HybridCPU_ISE.Arch; using YAKSys_Hybrid_CPU.Core; namespace HybridCPU_ISE.Arch { public struct VLIW_Instruction { " +
            string.Concat(projectedFields.Select(f => f.ToFullString())) + string.Concat(projectedProperties.Select(p => p.ToFullString())) +
            " } } namespace YAKSys_Hybrid_CPU.Core { public class BundleMetadata { " + count.ToFullString() + " } } namespace YAKSys_Hybrid_CPU.Core.Decoder { " +
            rawSlot.ToFullString() + " public static class RawSlotReader { " + method.ToFullString() + " } }";
        var tree = CSharpSyntaxTree.ParseText(projected, new CSharpParseOptions(LanguageVersion.Preview));
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("RawSlotProjection", [tree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        if (compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) throw new InvalidDataException("Raw-slot source projection does not bind.");
        var model = compilation.GetSemanticModel(tree);
        var projectedReader = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "RawSlotReader");
        var boundMethod = model.GetDeclaredSymbol(projectedReader.Members.OfType<MethodDeclarationSyntax>().Single()) ?? throw new InvalidDataException("Raw-slot method did not bind.");
        if (!boundMethod.IsStatic || boundMethod.ReturnType.ToDisplayString() != "YAKSys_Hybrid_CPU.Core.Decoder.RawSlot" || boundMethod.Parameters.Length != 2 ||
            boundMethod.Parameters[0].RefKind != RefKind.In || boundMethod.Parameters[0].Type.ToDisplayString() != "HybridCPU_ISE.Arch.VLIW_Instruction" ||
            boundMethod.Parameters[1].Type.SpecialType != SpecialType.System_Int32)
            throw new InvalidDataException("Raw-slot method signature changed.");
        var projectedMethod = (MethodDeclarationSyntax)boundMethod.DeclaringSyntaxReferences.Single().GetSyntax();
        foreach (var access in projectedMethod.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var symbol = model.GetSymbolInfo(access).Symbol;
            if (access.Name.Identifier.ValueText == "BundleSlotCount")
            {
                if (symbol is not IFieldSymbol { IsConst: true, ConstantValue: 8 } slotCount || slotCount.ContainingType.ToDisplayString() != "YAKSys_Hybrid_CPU.Core.BundleMetadata") throw new InvalidDataException("Raw-slot bundle bound incorrectly.");
            }
            else if (symbol is not IPropertySymbol property || property.ContainingType.ToDisplayString() != "HybridCPU_ISE.Arch.VLIW_Instruction" ||
                property.Type.SpecialType != (property.Name == "OpCode" ? SpecialType.System_UInt32 : SpecialType.System_UInt64) ||
                !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(access.Expression).Symbol, boundMethod.Parameters[0]))
                throw new InvalidDataException("Raw-slot carrier property bound incorrectly.");
        }
        var pos = method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        var identity = Hash(Encoding.UTF8.GetBytes("raw-slot-projection/v1|" + stagesSha + "|" + layoutSha + "|" + metadataSha));
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", commit, "HybridCPU_ISE/CloseToHSL/Core/Decoder/DeclarativeDecoderStages.cs", "CircuitWeave.RawSlot.Projection", "YAKSys_Hybrid_CPU.Core.Decoder.RawSlotReader", "Read", identity, HwSourceRole.Primary, pos.Line + 1, pos.Character + 1);
        var u1 = new HwBitVectorType(1); var u16 = new HwBitVectorType(16); var u32 = new HwBitVectorType(32); var u64 = new HwBitVectorType(64);
        HwPortContract[] ports = [new("slot_index",HwPortDirection.Input,u32), new("word0",HwPortDirection.Input,u64), new("word1",HwPortDirection.Input,u64), new("word2",HwPortDirection.Input,u64), new("word3",HwPortDirection.Input,u64), new("valid",HwPortDirection.Output,u1), new("opcode",HwPortDirection.Output,u16), new("out_word0",HwPortDirection.Output,u64), new("out_word1",HwPortDirection.Output,u64), new("out_word2",HwPortDirection.Output,u64), new("out_word3",HwPortDirection.Output,u64)];
        HwNode[] nodes = [new HwConstantNode("eight",u64,Convert.ToString(8,2).PadLeft(64,'0'),origin), new HwConstantNode("forty_eight",u64,Convert.ToString(48,2).PadLeft(64,'0'),origin), new HwOperationNode("index64","zext",["slot_index"],u64,Origin:origin), new HwOperationNode("in_range","ult64",["index64","eight"],u1,Origin:origin), new HwOperationNode("shifted","lshr64_mask6",["word0","forty_eight"],u64,Origin:origin), new HwOperationNode("opcode16","trunc",["shifted"],u16,Origin:origin), new HwOutputDriveNode("drive_valid","valid","in_range",origin), new HwOutputDriveNode("drive_opcode","opcode","opcode16",origin), new HwOutputDriveNode("drive_word0","out_word0","word0",origin), new HwOutputDriveNode("drive_word1","out_word1","word1",origin), new HwOutputDriveNode("drive_word2","out_word2","word2",origin), new HwOutputDriveNode("drive_word3","out_word3","word3",origin)];
        var module = new HwModule("raw_slot_projection","raw_slot_projection",ports,nodes,[],new HwHardwareContract("decoder.raw-slot-projection.unqualified.v1",HwModuleKind.Combinational,ports,new HwTimingContract(0,1)),origin);
        var validation = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",module.Id,[module],toolchain with { HardwareLanguageProfile = "hwp-hwir-comb-v1" },[]));
        return validation.Validated ?? throw new InvalidDataException("Raw-slot HWIR refused: " + string.Join("; ",validation.Diagnostics.Select(d => d.Message)));
    }
}
#endif
