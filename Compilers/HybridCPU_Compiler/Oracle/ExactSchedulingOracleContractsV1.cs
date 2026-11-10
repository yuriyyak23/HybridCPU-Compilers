using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU.Compiler.Oracle;

public enum HybridCpuExactOracleStatusV1 : byte
{
    Sat = 0,
    Unsat = 1,
    Unknown = 2,
    ResourceLimit = 3,
    InvalidModel = 4
}

public enum HybridCpuExactOracleLimitKindV1 : byte
{
    None = 0,
    MaximumOperations = 1,
    MaximumIiCandidates = 2,
    MaximumAssignmentStates = 3,
    MaximumStageRelaxations = 4,
    WallClockWatchdog = 5,
    SolverCrash = 6,
    UnsupportedSolverFeature = 7
}

public enum HybridCpuOracleConstraintFamilyV1 : byte
{
    None = 0,
    TemporalDistance = 1
}

public enum HybridCpuOracleProductionDependencyDispositionV1 : byte
{
    Forbidden = 0
}

public sealed record HybridCpuExactOracleBudgetsV1(
    int MaximumOperations,
    int MaximumIiCandidates,
    int MaximumAssignmentStates,
    int MaximumStageRelaxations)
{
    public static HybridCpuExactOracleBudgetsV1 Ci { get; } = new(12, 8, 2_000_000, 2_000_000);
}

public sealed record HybridCpuExactOracleOptionsV1(
    HybridCpuExactOracleBudgetsV1 Budgets,
    HybridCpuOracleConstraintFamilyV1 OmittedConstraintFamily,
    string OptionsDigest)
{
    public static HybridCpuExactOracleOptionsV1 Ci { get; } = Create(HybridCpuExactOracleBudgetsV1.Ci);

    public static HybridCpuExactOracleOptionsV1 Create(
        HybridCpuExactOracleBudgetsV1 budgets,
        HybridCpuOracleConstraintFamilyV1 omittedConstraintFamily = HybridCpuOracleConstraintFamilyV1.None)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        Validate(budgets);
        string digest = HybridCpuExactSchedulingOracleContractV1.Hash(string.Join('|',
            "hybridcpu.exact-oracle-options/v1",
            budgets.MaximumOperations.ToString(CultureInfo.InvariantCulture),
            budgets.MaximumIiCandidates.ToString(CultureInfo.InvariantCulture),
            budgets.MaximumAssignmentStates.ToString(CultureInfo.InvariantCulture),
            budgets.MaximumStageRelaxations.ToString(CultureInfo.InvariantCulture),
            omittedConstraintFamily,
            "wall-clock-not-used-for-code-or-proof",
            "external-solver=absent"));
        return new(budgets, omittedConstraintFamily, digest);
    }

    internal static void Validate(HybridCpuExactOracleBudgetsV1 budgets)
    {
        if (budgets.MaximumOperations <= 0 || budgets.MaximumIiCandidates <= 0 ||
            budgets.MaximumAssignmentStates <= 0 || budgets.MaximumStageRelaxations <= 0)
            throw new ArgumentOutOfRangeException(nameof(budgets));
    }
}

public sealed record IrExactOracleUnsatProofV1(
    int InitiationInterval,
    IReadOnlyList<string> ConstraintCore,
    int ExhaustiveModuloAssignments,
    int StageRelaxations,
    bool CompleteForComparableContract,
    string ProofDigest);

public sealed record IrExactOracleQueryV1(
    HybridCpuExactOracleStatusV1 Status,
    int InitiationInterval,
    IrModuloScheduleWitnessV1? Witness,
    IrModuloScheduleWitnessV1? RejectedSatWitness,
    IrModuloWitnessValidationV1? CoreValidation,
    IrExactOracleUnsatProofV1? UnsatProof,
    HybridCpuExactOracleLimitKindV1 LimitKind,
    bool LimitIsDeterministic,
    int AssignmentStates,
    int StageRelaxations,
    string Reason,
    string ModelFingerprint,
    string ResultDigest);

public sealed record IrExactOracleOptimalityGapV1(
    bool Comparable,
    int? ProductionIi,
    int? OracleMinimumIi,
    int? IiGap,
    decimal? RelativeIiGap,
    int? ProductionObjective,
    int? OracleObjective,
    int? ObjectiveGap,
    long? ProductionWorkUnits,
    long OracleWorkUnits,
    string Reason,
    string ComparisonDigest);

public sealed record IrExactOracleMinimumReportV1(
    HybridCpuExactOracleStatusV1 Status,
    int ProvenLowerBoundIi,
    int? MinimumFeasibleIi,
    IrModuloScheduleWitnessV1? Witness,
    IReadOnlyList<IrExactOracleQueryV1> Queries,
    IrExactOracleOptimalityGapV1 Gap,
    string TargetDigest,
    string ResourceModelDigest,
    string OracleContractDigest,
    string OptionsDigest,
    string ReportDigest);

public interface IHybridCpuExactSchedulingOracleV1
{
    IrExactOracleQueryV1 QueryAtIi(
        IrProgram program,
        IrCanonicalLoopV1 loop,
        IrLoopMiiReportV1 mii,
        int initiationInterval,
        HybridCpuMiiResourceModelV1? resourceModel = null,
        HybridCpuExactOracleOptionsV1? options = null);

    IrExactOracleMinimumReportV1 FindMinimum(
        IrProgram program,
        IrCanonicalLoopV1 loop,
        IrLoopMiiReportV1 mii,
        IrModuloScheduleResultV1? productionResult = null,
        HybridCpuMiiResourceModelV1? resourceModel = null,
        HybridCpuExactOracleOptionsV1? options = null);
}

public sealed class HybridCpuExactSchedulingOracleContractV1
{
    public const string SchemaId = "hybridcpu.exact-scheduling-oracle/v1";
    public const string BackendIdentity = "enumerative-modulo-stage-difference/v1";

    public static HybridCpuExactSchedulingOracleContractV1 Default { get; } = new();

    private HybridCpuExactSchedulingOracleContractV1()
    {
        TargetDigest = HybridCpuTargetMachineContractV1.Default.ContractDigest;
        ModuloSchedulerContractDigest = HybridCpuModuloSchedulingContractV1.Default.ContractDigest;
        CiOptionsDigest = HybridCpuExactOracleOptionsV1.Ci.OptionsDigest;
        ContractDigest = Hash(string.Join('|',
            SchemaId,
            BackendIdentity,
            TargetDigest,
            ModuloSchedulerContractDigest,
            CiOptionsDigest,
            "search=complete-modulo-time-enumeration-plus-exact-stage-difference-system",
            "placement=shared-exact-w8-constraint",
            "core-witness-validation=required",
            "gap=ii-relative-ii-kernel-span-and-work-units",
            "solver-package=absent",
            "production-dependency=false",
            "timeout-or-crash=resource-limit-never-unsat"));
    }

    public string TargetDigest { get; }
    public string ModuloSchedulerContractDigest { get; }
    public string CiOptionsDigest { get; }
    public string ContractDigest { get; }
    public HybridCpuOracleProductionDependencyDispositionV1 ProductionDependencyDisposition =>
        HybridCpuOracleProductionDependencyDispositionV1.Forbidden;

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
