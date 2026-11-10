using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Compiler.Core.IR;

public enum IrAnalysisEvidenceTrust : byte
{
    ProvenByIrSemantics = 0,
    FrontendStaticEvidence = 1,
    ProfileOnly = 2,
    Unknown = 255
}

public enum IrAnalysisEvidenceKind : byte
{
    Alias = 0,
    Tbaa = 1,
    Range = 2,
    Alignment = 3,
    Profile = 4,
    Loop = 5,
    SourceLocation = 6,
    Unknown = 255
}

public enum IrAliasEvidencePrecision : byte
{
    NotApplicable = 0,
    MayAlias = 1,
    NoAlias = 2,
    MustAlias = 3,
    Unknown = 255
}

public sealed record IrFrontendAnalysisFactV1(
    string StableIdentity,
    IrAnalysisEvidenceKind Kind,
    IrAnalysisEvidenceTrust Trust,
    IrAliasEvidencePrecision AliasPrecision,
    string Producer,
    string ProducerVersion,
    string PayloadDigest,
    bool MayAffectLegality,
    bool MayAffectProfitability);

public sealed record IrFrontendAnalysisEvidenceSetV1(
    string SchemaId,
    int SchemaVersion,
    IReadOnlyList<IrFrontendAnalysisFactV1> Facts,
    string Digest)
{
    public static IrFrontendAnalysisEvidenceSetV1 Empty { get; } = Create(Array.Empty<IrFrontendAnalysisFactV1>());

    public static IrFrontendAnalysisEvidenceSetV1 Create(IReadOnlyList<IrFrontendAnalysisFactV1> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        IrFrontendAnalysisFactV1[] ordered = facts
            .OrderBy(static fact => fact.StableIdentity, StringComparer.Ordinal)
            .ThenBy(static fact => fact.Kind)
            .ToArray();
        StringBuilder text = new StringBuilder("hybridcpu.frontend-analysis-evidence/v1");
        foreach (IrFrontendAnalysisFactV1 fact in ordered)
            text.Append('|').Append(fact.StableIdentity).Append(':').Append(fact.Kind).Append(':')
                .Append(fact.Trust).Append(':').Append(fact.AliasPrecision).Append(':')
                .Append(fact.Producer).Append(':').Append(fact.ProducerVersion).Append(':')
                .Append(fact.PayloadDigest).Append(':').Append(fact.MayAffectLegality).Append(':')
                .Append(fact.MayAffectProfitability);
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
        return new("hybridcpu.frontend-analysis-evidence/v1", 1, ordered, digest);
    }
}

public static class IrFrontendAnalysisEvidenceValidatorV1
{
    public static IrFrontendAdapterResultV1 Validate(IrProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        IrFrontendAnalysisEvidenceSetV1 canonical =
            IrFrontendAnalysisEvidenceSetV1.Create(program.FrontendEvidence.Facts);
        if (program.FrontendEvidence.SchemaId != canonical.SchemaId ||
            program.FrontendEvidence.SchemaVersion != canonical.SchemaVersion ||
            !string.Equals(program.FrontendEvidence.Digest, canonical.Digest, StringComparison.Ordinal))
            return Reject("HCIR0009", "Frontend analysis evidence envelope or digest is invalid.", "frontend-evidence");
        if (program.FrontendEvidence.Facts
            .Select(static fact => (fact.StableIdentity, fact.Kind))
            .Distinct().Count() != program.FrontendEvidence.Facts.Count)
            return Reject("HCIR0010", "Frontend analysis evidence identities must be unique per kind.", "frontend-evidence");
        foreach (IrFrontendAnalysisFactV1 fact in program.FrontendEvidence.Facts)
        {
            if (string.IsNullOrWhiteSpace(fact.StableIdentity) ||
                string.IsNullOrWhiteSpace(fact.Producer) ||
                string.IsNullOrWhiteSpace(fact.ProducerVersion) ||
                string.IsNullOrWhiteSpace(fact.PayloadDigest) ||
                fact.Kind == IrAnalysisEvidenceKind.Unknown ||
                fact.Trust == IrAnalysisEvidenceTrust.Unknown)
                return Reject("HCIR0010", "Frontend analysis evidence is incomplete or unknown.", fact.StableIdentity);

            if (fact.Trust == IrAnalysisEvidenceTrust.ProfileOnly && fact.MayAffectLegality)
                return Reject("HCIR0011", "Profile-only evidence cannot affect legality.", fact.StableIdentity);

            if (fact.AliasPrecision is IrAliasEvidencePrecision.NoAlias or IrAliasEvidencePrecision.MustAlias &&
                (fact.Trust != IrAnalysisEvidenceTrust.ProvenByIrSemantics ||
                 !string.Equals(fact.Producer, "HybridCPU.Compiler.Core", StringComparison.Ordinal)))
                return Reject("HCIR0012", "Frontend evidence cannot strengthen MayAlias without an explicit HybridCPU proof rule.", fact.StableIdentity);
        }
        return new(IrFrontendAdapterStatus.Success, program, Array.Empty<IrFrontendDiagnosticV1>());
    }

    private static IrFrontendAdapterResultV1 Reject(string code, string message, string identity) =>
        new(IrFrontendAdapterStatus.Unsupported, null, [new(code, message, identity)]);
}
