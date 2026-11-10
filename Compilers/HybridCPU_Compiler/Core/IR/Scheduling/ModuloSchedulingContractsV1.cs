using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU.Compiler.Core.IR;

public enum IrModuloScheduleStatusV1 : byte
{
    Feasible = 0,
    TemporalInfeasible = 1,
    DiscreteResourceInfeasible = 2,
    ExactPlacementInfeasible = 3,
    LifetimePressureInfeasible = 4,
    UnsupportedSemanticOrResourceFact = 5,
    BudgetExhausted = 6,
    StaleProof = 7
}

public sealed record IrModuloScheduleBudgetsV1(
    int MaximumIiAttempts,
    int MaximumSdcRelaxations,
    int MaximumCandidateStates,
    int MaximumResourceCuts,
    int MaximumRefinementIterations,
    int MaximumExactPlacementStates,
    int MaximumScheduleSpanCycles)
{
    public static IrModuloScheduleBudgetsV1 Production { get; } = new(
        16,
        65_536,
        131_072,
        16_384,
        256,
        262_144,
        64);
}

public sealed record HybridCpuModuloSchedulerOptionsV1(
    IrModuloScheduleBudgetsV1 Budgets,
    string OptionsDigest)
{
    public static HybridCpuModuloSchedulerOptionsV1 Production { get; } = Create(
        IrModuloScheduleBudgetsV1.Production);

    public static HybridCpuModuloSchedulerOptionsV1 Create(IrModuloScheduleBudgetsV1 budgets)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        Validate(budgets);
        string digest = HybridCpuModuloSchedulingContractV1.Hash(string.Join('|',
            "hybridcpu.modulo-scheduler-options/v1",
            budgets.MaximumIiAttempts.ToString(CultureInfo.InvariantCulture),
            budgets.MaximumSdcRelaxations.ToString(CultureInfo.InvariantCulture),
            budgets.MaximumCandidateStates.ToString(CultureInfo.InvariantCulture),
            budgets.MaximumResourceCuts.ToString(CultureInfo.InvariantCulture),
            budgets.MaximumRefinementIterations.ToString(CultureInfo.InvariantCulture),
            budgets.MaximumExactPlacementStates.ToString(CultureInfo.InvariantCulture),
            budgets.MaximumScheduleSpanCycles.ToString(CultureInfo.InvariantCulture),
            "wall-clock=false",
            "smt=absent"));
        return new(budgets, digest);
    }

    internal static void Validate(IrModuloScheduleBudgetsV1 budgets)
    {
        if (budgets.MaximumIiAttempts <= 0 || budgets.MaximumSdcRelaxations <= 0 ||
            budgets.MaximumCandidateStates <= 0 || budgets.MaximumResourceCuts <= 0 ||
            budgets.MaximumRefinementIterations <= 0 || budgets.MaximumExactPlacementStates <= 0 ||
            budgets.MaximumScheduleSpanCycles <= 0)
            throw new ArgumentOutOfRangeException(nameof(budgets));
    }
}

public sealed record IrModuloScheduledOperationV1(
    int InstructionIndex,
    string InstructionIdentity,
    int Cycle,
    int ModuloCycle,
    int IssueSlot);

public sealed record IrModuloResourceReservationV1(
    int ModuloCycle,
    int IssueCount,
    int PrfReads,
    int PrfWrites,
    IReadOnlyList<string> RegisterGroupReservations,
    IReadOnlyList<string> BankReservations,
    IReadOnlyList<string> ChannelReservations,
    int Lane6Reservations,
    int Lane7Reservations,
    int StructuralCertificateReservations,
    string ExactPlacementFingerprint);

public sealed record IrModuloLifetimePressureSummaryV1(
    string ValueFlowDigest,
    int MinimumCycle,
    int MaximumCycle,
    int KernelSpanCycles,
    int PeakRegisterGroupPressure,
    int CapacityAtChosenIi,
    IrLoopProofPrecisionV1 Precision);

public sealed record IrModuloScheduleWitnessV1(
    string SchemaId,
    string WitnessDigest,
    string LoopId,
    string LoopVersionStamp,
    string MiiProofDigest,
    int InitiationInterval,
    IReadOnlyList<IrModuloScheduledOperationV1> Operations,
    IReadOnlyList<IrModuloResourceReservationV1> ResourceReservations,
    IReadOnlyList<string> DependenceProofRefs,
    IReadOnlyList<string> MiiProofRefs,
    IrModuloLifetimePressureSummaryV1 LifetimePressure,
    string TargetDigest,
    string ResourceModelDigest,
    string SchedulerContractDigest,
    string OptionsDigest,
    IrRegionOutputDispositionV1 OutputDisposition);

public sealed record IrModuloInfeasibleIiReasonV1(
    int InitiationInterval,
    IrModuloScheduleStatusV1 Status,
    IReadOnlyList<string> BindingFacts,
    int SdcRelaxations,
    int CandidateStates,
    int ResourceCuts,
    int RefinementIterations,
    int ExactPlacementStates,
    string Reason);

public sealed record IrModuloWitnessValidationV1(
    IrModuloScheduleStatusV1 Status,
    IReadOnlyList<IrRegionSchedulingDiagnosticV1> Diagnostics,
    string ValidationDigest)
{
    public bool IsValid => Status == IrModuloScheduleStatusV1.Feasible;
}

public sealed record IrModuloCandidateWitnessResultV1(
    IrModuloScheduleStatusV1 Status,
    IrModuloScheduleWitnessV1? Witness,
    IrModuloWitnessValidationV1 Validation)
{
    public bool IsValid => Status == IrModuloScheduleStatusV1.Feasible &&
        Witness is not null && Validation.Status == IrModuloScheduleStatusV1.Feasible;
}

public sealed record IrModuloScheduleResultV1(
    IrModuloScheduleStatusV1 Status,
    int? ChosenIi,
    IrModuloScheduleWitnessV1? Witness,
    IReadOnlyList<IrModuloInfeasibleIiReasonV1> Attempts,
    IReadOnlyList<IrRegionSchedulingDiagnosticV1> Diagnostics,
    IrRegionOutputDispositionV1 OutputDisposition,
    string ResultDigest);

public sealed class HybridCpuModuloSchedulingContractV1
{
    public const string SchemaId = "hybridcpu.modulo-schedule-witness/v1";

    public static HybridCpuModuloSchedulingContractV1 Default { get; } = new();

    private HybridCpuModuloSchedulingContractV1()
    {
        TargetDigest = HybridCpuTargetMachineContractV1.Default.ContractDigest;
        LoopMiiContractDigest = HybridCpuLoopMiiContractV1.Default.ContractDigest;
        ProductionOptionsDigest = HybridCpuModuloSchedulerOptionsV1.Production.OptionsDigest;
        ContractDigest = Hash(string.Join('|',
            SchemaId,
            TargetDigest,
            LoopMiiContractDigest,
            ProductionOptionsDigest,
            "sdc=cycle-v-minus-cycle-u-gte-latency-minus-distance-times-ii",
            "placement=shared-exact-w8-search",
            "reservations=issue-prf-groups-bank-channel-lane6-lane7-certificate-exact-w8",
            "span=min-configured-or-ii-times-op-count",
            "witness=revalidated",
            "profile=non-authoritative",
            "wall-clock=false",
            "smt-runtime=false",
            "output=kernel-witness-only-exact-bb-fallback"));
    }

    public string TargetDigest { get; }
    public string LoopMiiContractDigest { get; }
    public string ProductionOptionsDigest { get; }
    public string ContractDigest { get; }
    public bool DefaultEnabled => false;
    public IrRegionOutputDispositionV1 OutputDisposition => IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly;

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static string OperationKey(IrModuloScheduledOperationV1 operation) => string.Join(':',
        operation.InstructionIndex.ToString(CultureInfo.InvariantCulture),
        operation.InstructionIdentity,
        operation.Cycle.ToString(CultureInfo.InvariantCulture),
        operation.ModuloCycle.ToString(CultureInfo.InvariantCulture),
        operation.IssueSlot.ToString(CultureInfo.InvariantCulture));
}
