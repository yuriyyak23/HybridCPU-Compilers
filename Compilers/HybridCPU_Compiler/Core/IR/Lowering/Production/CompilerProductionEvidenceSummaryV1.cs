using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Contracts;

namespace HybridCPU.Compiler.Core.IR.Lowering.Production;

/// <summary>Reference to existing compiler evidence, not runtime permission.</summary>
public sealed record CompilerEvidenceReferenceV1(string SchemaId, string EvidenceId, string Digest);

public sealed record CompilerProductionEvidenceSummaryV1(
    CompilerProductionLoweringResultKind ResultKind,
    CompilerEvidenceReferenceV1 NoFallback,
    CompilerRuntimeAuthorityDependency RuntimeAuthorityStillRequired)
{
    public const string SchemaId = "hybridcpu.production-evidence-summary/v1";
    public string Digest => CompilerContractCanonicalJson.Hash(CompilerContractCanonicalJson.Serialize(new
    {
        SchemaId, ResultKind, NoFallback, RuntimeAuthorityStillRequired
    }));

    public static CompilerProductionEvidenceSummaryV1 From(CompilerProductionLoweringResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        const string proofSchema = "hybridcpu.no-fallback-evidence/v1";
        string proofDigest = CompilerContractCanonicalJson.Hash(CompilerContractCanonicalJson.Serialize(new
        {
            SchemaId = proofSchema, result.NoFallbackProof.ProofId,
            result.NoFallbackProof.PolicyKind, result.NoFallbackProof.Reason
        }));
        return new(result.ResultKind,
            new(proofSchema, result.NoFallbackProof.ProofId, proofDigest), result.RuntimeAuthorityStillRequired);
    }
}
