using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR.Resources;

namespace HybridCPU.Compiler.Core.IR.Fsp;

public enum IrFspEvidenceSwitchV1 : byte { Disabled, ExplicitQualificationOnly }

public enum IrFspEvidenceStatusV1 : byte
{
    Accepted,
    DisabledBaseline,
    StaleInput,
    InvalidModel,
    BudgetExhausted,
    UnknownBaseline
}

public enum IrFspEvidenceAuthorityV1 : byte { CompilerEvidenceOnly }

public enum IrFspEvidencePrecisionV1 : byte { ExactStatic, ConservativeSet, Unknown, ProfileOnly }

public enum IrFspStaticCandidateDispositionV1 : byte
{
    EligibleStaticCandidate,
    ExcludedByStealability,
    ExcludedByMemoryEffect,
    ExcludedBySpecialContour,
    ExcludedByUnknownResource,
    ExcludedByNoStaticHole
}

public enum IrFspProfileDispositionV1 : byte { AbsentStaticPolicy, VersionedOfflineRankingOnly }

public enum IrFspStaticVtRelationV1 : byte { SourceVtKnownReceiverRelationUnspecified }

[Flags]
public enum IrFspResourcePressureReasonV1 : byte
{
    None = 0,
    RegisterGroupPressure = 1 << 0,
    BankOrChannelPossible = 1 << 1,
    SpecialContour = 1 << 2,
    UnknownResource = 1 << 3
}

public sealed record HybridCpuFspEvidenceBudgetsV1(
    int MaximumBundles,
    int MaximumInstructions,
    int MaximumEvidenceRecords)
{
    public static HybridCpuFspEvidenceBudgetsV1 Production { get; } = new(256, 1024, 1024);
}

public sealed record HybridCpuFspEvidenceOptionsV1(
    IrFspEvidenceSwitchV1 EvidenceSwitch,
    HybridCpuFspEvidenceBudgetsV1 Budgets,
    string OptionsDigest)
{
    public static HybridCpuFspEvidenceOptionsV1 Production { get; } = Create(
        IrFspEvidenceSwitchV1.Disabled,
        HybridCpuFspEvidenceBudgetsV1.Production);

    public static HybridCpuFspEvidenceOptionsV1 Qualification { get; } = Create(
        IrFspEvidenceSwitchV1.ExplicitQualificationOnly,
        HybridCpuFspEvidenceBudgetsV1.Production);

    public static HybridCpuFspEvidenceOptionsV1 Create(
        IrFspEvidenceSwitchV1 evidenceSwitch,
        HybridCpuFspEvidenceBudgetsV1 budgets)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        string digest = HybridCpuFspEvidenceContractV1.Hash(string.Join('|',
            HybridCpuFspEvidenceContractV1.SchemaId,
            (byte)evidenceSwitch,
            budgets.MaximumBundles,
            budgets.MaximumInstructions,
            budgets.MaximumEvidenceRecords));
        return new(evidenceSwitch, budgets, digest);
    }
}

public sealed record IrFspOfflineRankingProfileV1(
    string Schema,
    string ModelDigest,
    int StaticHoleWeight,
    int CriticalPathWeight,
    int DependencyWeight,
    int PressurePenaltyWeight,
    string ProfileDigest)
{
    public const string SchemaId = "hybridcpu.fsp-offline-ranking-profile/v1";

    public static IrFspOfflineRankingProfileV1 Create(
        string modelDigest,
        int staticHoleWeight,
        int criticalPathWeight,
        int dependencyWeight,
        int pressurePenaltyWeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDigest);
        if (staticHoleWeight is < 0 or > 1000 || criticalPathWeight is < 0 or > 1000 ||
            dependencyWeight is < 0 or > 1000 || pressurePenaltyWeight is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(staticHoleWeight));
        string digest = HybridCpuFspEvidenceContractV1.Hash(string.Join('|',
            SchemaId,
            modelDigest,
            staticHoleWeight,
            criticalPathWeight,
            dependencyWeight,
            pressurePenaltyWeight));
        return new(
            SchemaId,
            modelDigest,
            staticHoleWeight,
            criticalPathWeight,
            dependencyWeight,
            pressurePenaltyWeight,
            digest);
    }
}

public sealed record IrFspEvidenceFieldV1<T>(T Value, IrFspEvidencePrecisionV1 Precision, string Provenance);

public sealed record IrFspStaticCandidateEvidenceV1(
    string BundleIdentity,
    string InstructionIdentity,
    int InstructionIndex,
    byte VirtualThreadId,
    IrFspEvidenceFieldV1<IrFspStaticVtRelationV1> StaticVtRelation,
    IrFspStaticCandidateDispositionV1 Disposition,
    IrFspEvidenceFieldV1<IrIssueSlotMask> StructurallyCompatibleSlots,
    IrFspEvidenceFieldV1<IrSlotClass> StaticClass,
    IrFspEvidenceFieldV1<int> DependencyHorizonCycles,
    IrFspEvidenceFieldV1<int> DownstreamDependencyCount,
    IrFspEvidenceFieldV1<int> RegisterGroupPressure,
    IrFspEvidenceFieldV1<int> StaticHoleCount,
    IrFspEvidenceFieldV1<CompilerCertificateClassV1> CertificateClass,
    IrFspEvidenceFieldV1<IrFspResourcePressureReasonV1> ResourcePressureReasons,
    IrFspEvidenceFieldV1<int> EstimatedStaticCycleValue,
    IrFspProfileDispositionV1 ProfileDisposition,
    string TopologyDigest,
    string CandidateDigest);

public sealed record IrFspEvidenceCountersV1(
    int BundlesVisited,
    int InstructionsVisited,
    int EligibleStaticCandidates,
    int PredictedUsefulStaticOpportunities,
    int DeterministicWorkUnits);

public sealed record IrFspEvidenceDiagnosticV1(string Code, string Message);

public sealed record IrFspStaticEvidenceReportV1(
    IrFspEvidenceStatusV1 Status,
    IrFspEvidenceAuthorityV1 Authority,
    string ProgramDigest,
    string ScheduleDigest,
    string BundleDigest,
    string TopologyDigest,
    string ModelDigest,
    string OptionsDigest,
    IrFspProfileDispositionV1 ProfileDisposition,
    string? RankingProfileDigest,
    IReadOnlyList<IrFspStaticCandidateEvidenceV1> Candidates,
    IrFspEvidenceCountersV1 Counters,
    IReadOnlyList<IrFspEvidenceDiagnosticV1> Diagnostics,
    string ReportDigest);

public sealed class HybridCpuFspEvidenceContractV1
{
    public const string SchemaId = "hybridcpu.fsp-static-evidence/v1";

    public static HybridCpuFspEvidenceContractV1 Default { get; } = new();

    private HybridCpuFspEvidenceContractV1()
    {
        ContractDigest = Hash(string.Join('|',
            SchemaId,
            IrFspOfflineRankingProfileV1.SchemaId,
            HybridCpuMachineTopologyV1.SchemaName,
            HybridCpuMachineTopologyV1.SchemaVersion,
            HybridCpuMachineDescriptionV1.Default.Key,
            HybridCpuFspEvidenceOptionsV1.Production.OptionsDigest,
            HybridCpuFspEvidenceOptionsV1.Qualification.OptionsDigest,
            "compiler-evidence-only",
            "dynamic-fsp-revalidation-required",
            "no-runtime-context-fields"));
    }

    public string ContractDigest { get; }
    public string ProductionOptionsDigest => HybridCpuFspEvidenceOptionsV1.Production.OptionsDigest;
    public string QualificationOptionsDigest => HybridCpuFspEvidenceOptionsV1.Qualification.OptionsDigest;

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
