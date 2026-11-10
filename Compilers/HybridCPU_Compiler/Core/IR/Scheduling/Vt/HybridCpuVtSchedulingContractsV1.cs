using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.IR.Telemetry;

namespace HybridCPU.Compiler.Core.IR.Scheduling.Vt;

public enum IrVtStaticPlanningSwitchV1 : byte { Disabled, ExplicitQualificationOnly }

public enum IrVtStaticPlanStatusV1 : byte
{
    Accepted,
    DisabledFallback,
    Ineligible,
    StaleProof,
    BudgetExhausted,
    InvalidModel,
    UnknownFallback
}

public enum IrVtRelationshipDispositionV1 : byte { ValidatedIndependentStaticStreams }

public enum IrVtMemoryCoordinationV1 : byte { ProvenDisjointOrReadOnly }

public enum IrVtSynchronizationDispositionV1 : byte { Absent }

public enum IrVtClassCompatibilityEvidenceV1 : byte { StructurallyCompatible }

public enum IrVtSlotCompatibilityEvidenceV1 : byte { ExactW8StructuralPlacement }

public enum IrVtRuntimeStageDispositionV1 : byte { RequiredAtRuntime }

public sealed record HybridCpuVtStaticPlanningBudgetsV1(
    int MaximumStreams,
    int MaximumLocalCarriers,
    int MaximumOperations,
    int MaximumSubsetEvaluations)
{
    public static HybridCpuVtStaticPlanningBudgetsV1 Production { get; } = new(4, 256, 1024, 4096);
}

public sealed record HybridCpuVtStaticPlanningOptionsV1(
    IrVtStaticPlanningSwitchV1 PlanningSwitch,
    HybridCpuVtStaticPlanningBudgetsV1 Budgets,
    string OptionsDigest)
{
    public static HybridCpuVtStaticPlanningOptionsV1 Production { get; } = Create(
        IrVtStaticPlanningSwitchV1.Disabled,
        HybridCpuVtStaticPlanningBudgetsV1.Production);

    public static HybridCpuVtStaticPlanningOptionsV1 Qualification { get; } = Create(
        IrVtStaticPlanningSwitchV1.ExplicitQualificationOnly,
        HybridCpuVtStaticPlanningBudgetsV1.Production);

    public static HybridCpuVtStaticPlanningOptionsV1 Create(
        IrVtStaticPlanningSwitchV1 planningSwitch,
        HybridCpuVtStaticPlanningBudgetsV1 budgets)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        string digest = HybridCpuVtSchedulingContractV1.Hash(string.Join('|',
            HybridCpuVtSchedulingContractV1.SchemaId,
            (byte)planningSwitch,
            budgets.MaximumStreams,
            budgets.MaximumLocalCarriers,
            budgets.MaximumOperations,
            budgets.MaximumSubsetEvaluations));
        return new(planningSwitch, budgets, digest);
    }
}

public sealed record IrVtLocalScheduleEvidenceV1(
    byte VirtualThreadId,
    IrProgramSchedule LocalSchedule,
    string ProgramDigest,
    string ScheduleDigest,
    int LocalCarrierCount)
{
    public static IrVtLocalScheduleEvidenceV1 Create(byte virtualThreadId, IrProgramSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return new(
            virtualThreadId,
            schedule,
            CompilerScheduleFingerprintV1.HashProgramInput(schedule.Program),
            CompilerScheduleFingerprintV1.HashSchedule(schedule),
            schedule.BlockSchedules.Sum(static block => block.CycleGroups.Count));
    }
}

public sealed record IrCrossVtRelationshipProofV1(
    byte LeftVirtualThreadId,
    byte RightVirtualThreadId,
    string LeftProgramDigest,
    string RightProgramDigest,
    string LeftScheduleDigest,
    string RightScheduleDigest,
    IrVtRelationshipDispositionV1 Relationship,
    IrVtMemoryCoordinationV1 MemoryCoordination,
    IrVtSynchronizationDispositionV1 Synchronization,
    string ProofDigest)
{
    public static IrCrossVtRelationshipProofV1 CreateIndependent(
        IrVtLocalScheduleEvidenceV1 left,
        IrVtLocalScheduleEvidenceV1 right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        IrVtLocalScheduleEvidenceV1 first = left.VirtualThreadId < right.VirtualThreadId ? left : right;
        IrVtLocalScheduleEvidenceV1 second = ReferenceEquals(first, left) ? right : left;
        const IrVtRelationshipDispositionV1 relationship =
            IrVtRelationshipDispositionV1.ValidatedIndependentStaticStreams;
        const IrVtMemoryCoordinationV1 memory = IrVtMemoryCoordinationV1.ProvenDisjointOrReadOnly;
        const IrVtSynchronizationDispositionV1 synchronization = IrVtSynchronizationDispositionV1.Absent;
        string digest = HybridCpuVtSchedulingContractV1.Hash(string.Join('|',
            HybridCpuVtSchedulingContractV1.RelationshipProofSchemaId,
            first.VirtualThreadId,
            second.VirtualThreadId,
            first.ProgramDigest,
            second.ProgramDigest,
            first.ScheduleDigest,
            second.ScheduleDigest,
            (byte)relationship,
            (byte)memory,
            (byte)synchronization));
        return new(
            first.VirtualThreadId,
            second.VirtualThreadId,
            first.ProgramDigest,
            second.ProgramDigest,
            first.ScheduleDigest,
            second.ScheduleDigest,
            relationship,
            memory,
            synchronization,
            digest);
    }
}

public sealed record IrVtRuntimeAuthorityBoundaryV1(
    IrVtRuntimeStageDispositionV1 StageAClassAdmission,
    IrVtRuntimeStageDispositionV1 StageBLaneMaterialization,
    IrVtRuntimeStageDispositionV1 ExecutionReplay,
    IrVtRuntimeStageDispositionV1 PublicationCommitRetire);

public sealed record IrVtStaticCycleMemberV1(
    byte VirtualThreadId,
    int InstructionIndex,
    string InstructionIdentity,
    int LocalCarrierOrdinal,
    int StructuralSlot);

public sealed record IrVtStaticJointCycleV1(
    int JointCycle,
    IReadOnlyList<IrVtStaticCycleMemberV1> Members,
    IrVtClassCompatibilityEvidenceV1 ClassCompatibilityEvidence,
    IrVtSlotCompatibilityEvidenceV1 SlotCompatibilityEvidence,
    string TopologyEvidenceDigest,
    string PlacementEvidenceDigest);

public sealed record IrVtStaticPlanningCountersV1(
    int SubsetsEvaluated,
    int StructuralRejects,
    int SharedResourceRejects,
    int ExactPlacementsEvaluated,
    int DeterministicWorkUnits);

public sealed record IrVtPlanningDiagnosticV1(string Code, string Message);

public sealed record IrVtStaticPlanV1(
    IrVtStaticPlanStatusV1 Status,
    IReadOnlyList<IrVtLocalScheduleEvidenceV1> LocalFallbackSchedules,
    IReadOnlyList<IrVtStaticJointCycleV1> JointCycles,
    int LocalCarrierCount,
    int PlannedStaticCarrierCount,
    int SavedStaticCarrierCount,
    string LocalFallbackDigest,
    string TopologyDigest,
    string OptionsDigest,
    IrVtStaticPlanningCountersV1 Counters,
    CompilerCoreResultHeader AuthorityHeader,
    IrVtRuntimeAuthorityBoundaryV1 RuntimeAuthorityBoundary,
    IReadOnlyList<IrVtPlanningDiagnosticV1> Diagnostics,
    string PlanDigest);

public sealed class HybridCpuVtSchedulingContractV1
{
    public const string SchemaId = "hybridcpu.vt-static-scheduling/v1";
    public const string RelationshipProofSchemaId = "hybridcpu.cross-vt-relationship-proof/v1";

    public static HybridCpuVtSchedulingContractV1 Default { get; } = new();

    private HybridCpuVtSchedulingContractV1()
    {
        ContractDigest = Hash(string.Join('|',
            SchemaId,
            RelationshipProofSchemaId,
            HybridCpuMachineTopologyV1.SchemaName,
            HybridCpuMachineTopologyV1.SchemaVersion,
            HybridCpuMachineTopologyV1.VirtualThreadCount,
            HybridCpuMachineDescriptionV1.Default.Key,
            HybridCpuVtStaticPlanningOptionsV1.Production.OptionsDigest,
            HybridCpuVtStaticPlanningOptionsV1.Qualification.OptionsDigest,
            "whole-local-cycle-groups",
            "exact-w8-structural-placement",
            "runtime-stage-a-b-execution-replay-publication-commit-retire-required"));
    }

    public string ContractDigest { get; }
    public string ProductionOptionsDigest => HybridCpuVtStaticPlanningOptionsV1.Production.OptionsDigest;
    public string QualificationOptionsDigest => HybridCpuVtStaticPlanningOptionsV1.Qualification.OptionsDigest;

    public static CompilerCoreResultHeader EvidenceOnlyHeader { get; } = new(
        CompilerAuthorityClass.CompilerEvidenceProduction,
        CompilerAuthoritySourceKind.CompilerStructuralModel,
        CompilerEvidenceClass.StructuralPlacementEvidence,
        CompilerPublicationClass.EvidenceOnly,
        CompilerExecutionClaim.NoExecutionClaim,
        CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
        CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
        CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired |
        CompilerRuntimeAuthorityDependency.RuntimePublicationRequired |
        CompilerRuntimeAuthorityDependency.RuntimeCommitRequired |
        CompilerRuntimeAuthorityDependency.RuntimeRetireRequired);

    public static IrVtRuntimeAuthorityBoundaryV1 RuntimeBoundary { get; } = new(
        IrVtRuntimeStageDispositionV1.RequiredAtRuntime,
        IrVtRuntimeStageDispositionV1.RequiredAtRuntime,
        IrVtRuntimeStageDispositionV1.RequiredAtRuntime,
        IrVtRuntimeStageDispositionV1.RequiredAtRuntime);

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}
