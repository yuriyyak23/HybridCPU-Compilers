#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text;
using CircuitWeave.Contracts;
using CircuitWeave.HWIR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Source-bound shared-resource and per-VT RAW/WAR/WAW certificate predicate.</summary>
public static class BridgeHwirSmtCertificatePredicate
{
    public const string CertificateSha256 = "e539d874d9be20605539cda9bf31ed0aed4199b3fced5b35c6531408483ba781";
    public const string AdmissionMetadataSha256 = "c0c9b134863ba11f59c5a7bbb2ba1122a1a933fe4379cee0053b085224ba7691";
    public const string Commit = "1bfe41a49cf11c80fa6c6aee87accb54b78b331f";

    public static HwValidatedDesign Compile(byte[] certificateSource, byte[] metadataSource, HwToolchainIdentity toolchain)
    {
        ArgumentNullException.ThrowIfNull(certificateSource);
        ArgumentNullException.ThrowIfNull(metadataSource);
        if (Hash(certificateSource) != CertificateSha256 || Hash(metadataSource) != AdmissionMetadataSha256)
            throw new InvalidDataException("Pinned SMT certificate predicate source bytes changed.");
        SyntaxNode certificateRoot = Parse(certificateSource);
        SyntaxNode metadataRoot = Parse(metadataSource);
        MethodDeclarationSyntax predicate = certificateRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "CanInjectWithReason" && m.ParameterList.Parameters.Count == 2 &&
                m.ParameterList.Parameters[0].Type?.ToString() == "MicroOp");
        string tokens = Tokens(predicate);
        string[] required =
        [
            "MicroOpAdmissionMetadataadmission=candidate.AdmissionMetadata;",
            "SafetyMask128candidateShared=admission.SharedStructuralMask;",
            "if((SharedMask&candidateShared).IsNonZero){detail=CertificateRejectDetail.SharedResourceConflict;returnfalse;}",
            "uintcandidateReg=admission.RegisterHazardMask;",
            "uintbundleReg=candidate.VirtualThreadIdswitch{0=>RegMaskVT0,1=>RegMaskVT1,2=>RegMaskVT2,_=>RegMaskVT3};",
            "ushortcandRead=(ushort)(candidateReg&0xFFFF);",
            "ushortcandWrite=(ushort)(candidateReg>>16);",
            "ushortbndRead=(ushort)(bundleReg&0xFFFF);",
            "ushortbndWrite=(ushort)(bundleReg>>16);",
            "if(((candRead&bndWrite)|(candWrite&bndRead)|(candWrite&bndWrite))!=0){detail=CertificateRejectDetail.RegisterGroupConflict;returnfalse;}"
        ];
        foreach (string token in required)
            if (!tokens.Contains(token, StringComparison.Ordinal))
                throw new InvalidDataException("CanInjectWithReason certificate semantics changed: " + token);
        if (!certificateRoot.DescendantNodes().OfType<EnumDeclarationSyntax>().Any(e => e.Identifier.ValueText == "CertificateRejectDetail" &&
                e.Members.Any(m => m.Identifier.ValueText == "None" && m.EqualsValue?.Value.ToString() == "0") &&
                e.Members.Any(m => m.Identifier.ValueText == "SharedResourceConflict" && m.EqualsValue?.Value.ToString() == "1") &&
                e.Members.Any(m => m.Identifier.ValueText == "RegisterGroupConflict" && m.EqualsValue?.Value.ToString() == "2")))
            throw new InvalidDataException("Certificate reject-detail encoding changed.");
        PropertyDeclarationSyntax shared = metadataRoot.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "SharedStructuralMask");
        if (!Tokens(shared).Contains("StructuralSafetyMask.Low&~RegisterBitsMaskLow32", StringComparison.Ordinal) ||
            !Tokens(shared).Contains("StructuralSafetyMask.High", StringComparison.Ordinal))
            throw new InvalidDataException("SharedStructuralMask projection changed.");
        if (!metadataRoot.DescendantNodes().OfType<ParameterSyntax>().Any(p => p.Identifier.ValueText == "RegisterHazardMask" && p.Type?.ToString() == "uint"))
            throw new InvalidDataException("RegisterHazardMask source projection changed.");

        var pos = predicate.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        string originDigest = Hash(Encoding.UTF8.GetBytes("smt-certificate-predicate/v1|" + CertificateSha256 + "|" + AdmissionMetadataSha256));
        var origin = new HwSourceOrigin("https://github.com/yuriyyak23/HybridCPU-v2", Commit,
            "HybridCPU_ISE/CloseToHSL/Core/Pipeline/Certificates/BundleResourceCertificate4Way.cs",
            "CircuitWeave.SmtCertificatePredicate", "YAKSys_Hybrid_CPU.Core.BundleResourceCertificate4Way",
            "CanInjectWithReason", originDigest, HwSourceRole.Primary, pos.Line + 1, pos.Character + 1);
        var bit = new HwBitVectorType(1); var u2 = new HwBitVectorType(2); var u32 = new HwBitVectorType(32); var u64 = new HwBitVectorType(64);
        var ports = new List<HwPortContract>
        {
            new("candidate_shared_low",HwPortDirection.Input,u64), new("candidate_shared_high",HwPortDirection.Input,u64),
            new("candidate_register_hazard_mask",HwPortDirection.Input,u32), new("candidate_virtual_thread_id",HwPortDirection.Input,u32),
            new("bundle_shared_low",HwPortDirection.Input,u64), new("bundle_shared_high",HwPortDirection.Input,u64),
            new("phase_shared_low",HwPortDirection.Input,u64), new("phase_shared_high",HwPortDirection.Input,u64),
            new("phase_key_valid",HwPortDirection.Input,bit), new("phase_template_matches",HwPortDirection.Input,bit),
            new("replay_certificate_selected",HwPortDirection.Output,bit),
            new("shared_conflict",HwPortDirection.Output,bit), new("register_conflict",HwPortDirection.Output,bit),
            new("reject_detail",HwPortDirection.Output,u2), new("allowed",HwPortDirection.Output,bit)
        };
        for (int i=0;i<4;i++)
        {
            ports.Add(new($"bundle_vt{i}_mask",HwPortDirection.Input,u32));
            ports.Add(new($"phase_vt{i}_mask",HwPortDirection.Input,u32));
        }
        var nodes = new List<HwNode>
        {
            new HwConstantNode("one1",bit,"1",origin), new HwConstantNode("zero1",bit,"0",origin),
            new HwConstantNode("zero2",u2,"00",origin), new HwConstantNode("one2",u2,"01",origin),
            new HwConstantNode("two2",u2,"10",origin),
            new HwConstantNode("zero32",u32,new string('0',32),origin),
            new HwConstantNode("readMask32",u32,Convert.ToString(0xFFFF,2).PadLeft(32,'0'),origin),
            new HwConstantNode("shift16",u32,Convert.ToString(16,2).PadLeft(32,'0'),origin),
            new HwOperationNode("replay_match","and",["phase_key_valid","phase_template_matches"],bit,Origin:origin),
            new HwOperationNode("bundle_low_hit","and",["bundle_shared_low","candidate_shared_low"],u64,Origin:origin),
            new HwOperationNode("bundle_high_hit","and",["bundle_shared_high","candidate_shared_high"],u64,Origin:origin),
            new HwOperationNode("phase_low_hit","and",["phase_shared_low","candidate_shared_low"],u64,Origin:origin),
            new HwOperationNode("phase_high_hit","and",["phase_shared_high","candidate_shared_high"],u64,Origin:origin),
            new HwMuxNode("selected_low_hit","replay_match","phase_low_hit","bundle_low_hit",u64,origin),
            new HwMuxNode("selected_high_hit","replay_match","phase_high_hit","bundle_high_hit",u64,origin),
            new HwOperationNode("selected_low_clear","eq",["selected_low_hit","zero64"],bit,Origin:origin),
            new HwOperationNode("selected_high_clear","eq",["selected_high_hit","zero64"],bit,Origin:origin),
            new HwOperationNode("shared_clear","and",["selected_low_clear","selected_high_clear"],bit,Origin:origin),
            new HwOperationNode("shared_conflict_value","xor",["shared_clear","one1"],bit,Origin:origin),
            new HwConstantNode("zero64",u64,new string('0',64),origin),
            new HwOutputDriveNode("drive_replay_selected","replay_certificate_selected","replay_match",origin),
            new HwOutputDriveNode("drive_shared_conflict","shared_conflict","shared_conflict_value",origin)
        };
        string selectedBundle="bundle_vt3_mask", selectedPhase="phase_vt3_mask";
        for(int i=2;i>=0;i--)
        {
            string id=i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            nodes.Add(new HwConstantNode("vt_id"+id,u32,Convert.ToString(i,2).PadLeft(32,'0'),origin));
            nodes.Add(new HwOperationNode("vt_match"+id,"eq",["candidate_virtual_thread_id","vt_id"+id],bit,Origin:origin));
            nodes.Add(new HwMuxNode("bundle_vt_select"+id,"vt_match"+id,$"bundle_vt{i}_mask",selectedBundle,u32,origin));
            nodes.Add(new HwMuxNode("phase_vt_select"+id,"vt_match"+id,$"phase_vt{i}_mask",selectedPhase,u32,origin));
            selectedBundle="bundle_vt_select"+id; selectedPhase="phase_vt_select"+id;
        }
        nodes.Add(new HwMuxNode("selected_bundle_vt","replay_match",selectedPhase,selectedBundle,u32,origin));
        nodes.Add(new HwOperationNode("candidate_read32","and",["candidate_register_hazard_mask","readMask32"],u32,Origin:origin));
        nodes.Add(new HwOperationNode("candidate_write32","lshr32_mask5",["candidate_register_hazard_mask","shift16"],u32,Origin:origin));
        nodes.Add(new HwOperationNode("bundle_read32","and",["selected_bundle_vt","readMask32"],u32,Origin:origin));
        nodes.Add(new HwOperationNode("bundle_write32","lshr32_mask5",["selected_bundle_vt","shift16"],u32,Origin:origin));
        nodes.Add(new HwOperationNode("raw_overlap","and",["candidate_read32","bundle_write32"],u32,Origin:origin));
        nodes.Add(new HwOperationNode("war_overlap","and",["candidate_write32","bundle_read32"],u32,Origin:origin));
        nodes.Add(new HwOperationNode("waw_overlap","and",["candidate_write32","bundle_write32"],u32,Origin:origin));
        nodes.Add(new HwOperationNode("hazard01","or",["raw_overlap","war_overlap"],u32,Origin:origin));
        nodes.Add(new HwOperationNode("hazard_any","or",["hazard01","waw_overlap"],u32,Origin:origin));
        nodes.Add(new HwOperationNode("register_clear","eq",["hazard_any","zero32"],bit,Origin:origin));
        nodes.Add(new HwOperationNode("register_conflict_value","xor",["register_clear","one1"],bit,Origin:origin));
        nodes.Add(new HwOperationNode("any_conflict","or",["shared_conflict_value","register_conflict_value"],bit,Origin:origin));
        nodes.Add(new HwOperationNode("allowed_value","xor",["any_conflict","one1"],bit,Origin:origin));
        nodes.Add(new HwMuxNode("register_detail","register_conflict_value","two2","zero2",u2,origin));
        nodes.Add(new HwMuxNode("detail_value","shared_conflict_value","one2","register_detail",u2,origin));
        nodes.Add(new HwOutputDriveNode("drive_register_conflict","register_conflict","register_conflict_value",origin));
        nodes.Add(new HwOutputDriveNode("drive_reject_detail","reject_detail","detail_value",origin));
        nodes.Add(new HwOutputDriveNode("drive_allowed","allowed","allowed_value",origin));
        var module = new HwModule("smt_bundle_certificate_predicate_projection","smt_bundle_certificate_predicate_projection",ports,nodes,[],
            new HwHardwareContract("scheduler.smt-certificate-predicate.unqualified.v1",HwModuleKind.Combinational,ports,new HwTimingContract(0,1)),origin);
        var result = new FailClosedHardwareIrValidator().Validate(new HwDesign("hwir/v0.1",module.Id,[module],toolchain,[]));
        return result.Validated ?? throw new InvalidDataException("SMT certificate predicate HWIR refused: " + string.Join("; ",result.Diagnostics.Select(d=>d.Code+" "+d.Message)));
    }

    private static SyntaxNode Parse(byte[] source)
    {
        SyntaxNode root=CSharpSyntaxTree.ParseText(new UTF8Encoding(false,true).GetString(source)).GetRoot();
        if(root.GetDiagnostics().Any(d=>d.Severity==DiagnosticSeverity.Error))throw new InvalidDataException("Certificate source syntax is invalid.");
        return root;
    }
    private static string Tokens(SyntaxNode node)=>string.Concat(node.DescendantTokens().Select(t=>t.Text));
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
#endif
