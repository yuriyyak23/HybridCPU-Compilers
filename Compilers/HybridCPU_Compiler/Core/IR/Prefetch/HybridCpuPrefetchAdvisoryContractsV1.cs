using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Contracts;
using HybridCPU.Compiler.Core.IR.Fsp;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU.Compiler.Core.IR.Prefetch;

public enum IrPrefetchAdvisorySwitchV1 : byte { Disabled, ExplicitQualificationOnly }

public enum IrPrefetchAdvisoryStatusV1 : byte
{
    EvidenceProduced,
    DisabledBaseline,
    StaleInput,
    InvalidModel,
    BudgetExhausted,
    Unsupported
}

public enum IrPrefetchAdvisoryAuthorityV1 : byte { CompilerAdvisoryOnly }

public enum IrPrefetchCandidateDispositionV1 : byte
{
    EligibleConditionalAdvisory,
    ExcludedByMemorySemantics,
    ExcludedByAddressSpace,
    ExcludedByAddressEvidence,
    ExcludedByFaultBoundary,
    ExcludedByManagedReference,
    ExcludedBySpecialContour,
    ExcludedByUnknownDependence,
    ExcludedByMissingReuse,
    ExcludedByMissingDonorEvidence
}

public enum IrPrefetchConsumerRequirementV1 : byte
{
    RejectUnlessCurrentNonFaultingContractAndAddressValidation
}

public enum IrPrefetchDependenceRelationV1 : byte
{
    NoOriginalMemoryEdge,
    MustPreserveOriginalMemoryEdge,
    MayPreserveOriginalMemoryEdge,
    Unknown
}

public enum IrPrefetchProfileDispositionV1 : byte { AbsentStaticPolicy, VersionedOfflineRankingOnly }

public sealed record HybridCpuPrefetchAdvisoryBudgetsV1(
    int MaximumMemoryInstructions,
    int MaximumCandidates,
    int MaximumDistanceEdges,
    int MaximumPairComparisons)
{
    public static HybridCpuPrefetchAdvisoryBudgetsV1 Production { get; } = new(512, 256, 4096, 65536);
}

public sealed record HybridCpuPrefetchAdvisoryOptionsV1(
    IrPrefetchAdvisorySwitchV1 AdvisorySwitch,
    HybridCpuPrefetchAdvisoryBudgetsV1 Budgets,
    string OptionsDigest)
{
    public static HybridCpuPrefetchAdvisoryOptionsV1 Production { get; } = Create(
        IrPrefetchAdvisorySwitchV1.Disabled,
        HybridCpuPrefetchAdvisoryBudgetsV1.Production);

    public static HybridCpuPrefetchAdvisoryOptionsV1 Qualification { get; } = Create(
        IrPrefetchAdvisorySwitchV1.ExplicitQualificationOnly,
        HybridCpuPrefetchAdvisoryBudgetsV1.Production);

    public static HybridCpuPrefetchAdvisoryOptionsV1 Create(
        IrPrefetchAdvisorySwitchV1 advisorySwitch,
        HybridCpuPrefetchAdvisoryBudgetsV1 budgets)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        string digest = HybridCpuPrefetchAdvisoryContractV1.Hash(string.Join('|',
            HybridCpuPrefetchAdvisoryContractV1.SchemaId,
            (byte)advisorySwitch,
            budgets.MaximumMemoryInstructions,
            budgets.MaximumCandidates,
            budgets.MaximumDistanceEdges,
            budgets.MaximumPairComparisons));
        return new(advisorySwitch, budgets, digest);
    }
}

public sealed record IrPrefetchOfflineRankingProfileV1(
    string Schema,
    string ModelDigest,
    int ReuseWeight,
    int DistanceWeight,
    int HoleWeight,
    int PressurePenaltyWeight,
    string ProfileDigest)
{
    public const string SchemaId = "hybridcpu.prefetch-offline-ranking-profile/v1";

    public static IrPrefetchOfflineRankingProfileV1 Create(
        string modelDigest,
        int reuseWeight,
        int distanceWeight,
        int holeWeight,
        int pressurePenaltyWeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDigest);
        if (reuseWeight is < 0 or > 1000 || distanceWeight is < 0 or > 1000 ||
            holeWeight is < 0 or > 1000 || pressurePenaltyWeight is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(reuseWeight));
        string digest = HybridCpuPrefetchAdvisoryContractV1.Hash(string.Join('|',
            SchemaId, modelDigest, reuseWeight, distanceWeight, holeWeight, pressurePenaltyWeight));
        return new(SchemaId, modelDigest, reuseWeight, distanceWeight, holeWeight,
            pressurePenaltyWeight, digest);
    }
}

public sealed record IrPrefetchAdvisoryFieldV1<T>(
    T Value,
    IrFspEvidencePrecisionV1 Precision,
    string Provenance);

public sealed record IrPrefetchAdvisoryCandidateV1(
    string CandidateIdentity,
    string ConsumerInstructionIdentity,
    int ConsumerInstructionIndex,
    byte VirtualThreadId,
    string RegionIdentity,
    string? LoopIdentity,
    IrPrefetchCandidateDispositionV1 Disposition,
    IrPrefetchConsumerRequirementV1 ConsumerRequirement,
    IrPrefetchAdvisoryFieldV1<IrAddressSpaceIdentity> AddressSpace,
    IrPrefetchAdvisoryFieldV1<IrAddressEvidenceKindV1> AddressPrecision,
    string AddressEvidenceDigest,
    uint RegionLengthBytes,
    IrPrefetchAdvisoryFieldV1<IrPrefetchDependenceRelationV1> DependenceRelation,
    IrPrefetchAdvisoryFieldV1<int> IterationDistance,
    IrPrefetchAdvisoryFieldV1<int> ReuseCount,
    IrPrefetchAdvisoryFieldV1<int> ExpectedStaticLatencyBenefit,
    IrPrefetchAdvisoryFieldV1<IrAddressResourceEvidenceV1> Banks,
    IrPrefetchAdvisoryFieldV1<IrAddressResourceEvidenceV1> Channels,
    IrPrefetchAdvisoryFieldV1<IrFspResourcePressureReasonV1> ResourcePressure,
    string OriginalDependenceDigest,
    int OriginalDependenceCount,
    string FspReportDigest,
    string? DonorEvidenceDigest,
    IrPrefetchProfileDispositionV1 ProfileDisposition,
    int RankingScore,
    string TopologyDigest,
    string CandidateDigest);

public sealed record IrPrefetchAdvisoryCountersV1(
    int MemoryInstructionsVisited,
    int PairComparisons,
    int CandidatesProduced,
    int EligibleConditionalAdvisories,
    int DeterministicWorkUnits);

public sealed record IrPrefetchAdvisoryDiagnosticV1(string Code, string Message);

public sealed record IrPrefetchAdvisoryReportV1(
    IrPrefetchAdvisoryStatusV1 Status,
    IrPrefetchAdvisoryAuthorityV1 Authority,
    string ProgramDigest,
    string ScheduleDigest,
    string BundleDigest,
    string DependencyGraphDigest,
    string TargetDigest,
    string MachineDigest,
    string TopologyDigest,
    string ModelDigest,
    string OptionsDigest,
    string FspReportDigest,
    IrPrefetchProfileDispositionV1 ProfileDisposition,
    string? RankingProfileDigest,
    IReadOnlyList<IrPrefetchAdvisoryCandidateV1> Candidates,
    IrPrefetchAdvisoryCountersV1 Counters,
    IReadOnlyList<IrPrefetchAdvisoryDiagnosticV1> Diagnostics,
    string ReportDigest);

public sealed class HybridCpuPrefetchAdvisoryContractV1
{
    public const string SchemaId = "hybridcpu.vdsa-prefetch-advisory/v1";

    public static HybridCpuPrefetchAdvisoryContractV1 Default { get; } = new();

    private HybridCpuPrefetchAdvisoryContractV1()
    {
        SchemaDeclaration = new(
            SchemaId,
            new CompilerSchemaVersion(1, 0),
            "Version fault-neutral compiler advisory evidence without granting runtime permission.",
            CompilerAuthorityClass.CompilerEvidenceProduction,
            "HybridCPU.Compiler.Core",
            "Optional advisory metadata consumers; reject by default",
            0,
            MigrationIsLossless: false,
            [
                new("memory_and_dependence_evidence", CompilerSchemaFieldSemantics.Semantic, 0),
                new("fault_neutral_consumer_requirement", CompilerSchemaFieldSemantics.Semantic, 0),
                new("target_model_provenance", CompilerSchemaFieldSemantics.Semantic, 0),
                new("ranking_profile", CompilerSchemaFieldSemantics.ProfitabilityOnly, 0)
            ]);
        CompilerSchemaCompatibility.ValidateDeclaration(SchemaDeclaration);
        TargetDigest = HybridCpuTargetMachineContractV1.Default.ContractDigest;
        MachineDigest = HybridCpuMachineDescriptionV1.Default.ContractDigest;
        ContractDigest = Hash(string.Join('|',
            SchemaId,
            SchemaDeclaration.Version,
            CompilerCrossLayerSchemaCatalogV1.Envelope.SchemaId,
            TargetDigest,
            MachineDigest,
            IrPrefetchOfflineRankingProfileV1.SchemaId,
            HybridCpuFspEvidenceContractV1.Default.ContractDigest,
            HybridCpuPrefetchAdvisoryOptionsV1.Production.OptionsDigest,
            HybridCpuPrefetchAdvisoryOptionsV1.Qualification.OptionsDigest,
            "compiler-advisory-only",
            "original-memory-dependencies-preserved",
            "consumer-must-reject-or-suppress-faults",
            "managed-addresses-unsupported",
            "runtime-context-fields=absent"));
    }

    public CompilerSchemaDeclaration SchemaDeclaration { get; }
    public string TargetDigest { get; }
    public string MachineDigest { get; }
    public string ContractDigest { get; }
    public string ProductionOptionsDigest => HybridCpuPrefetchAdvisoryOptionsV1.Production.OptionsDigest;
    public string QualificationOptionsDigest => HybridCpuPrefetchAdvisoryOptionsV1.Qualification.OptionsDigest;

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
