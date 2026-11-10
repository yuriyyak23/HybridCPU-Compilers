using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU.Compiler.Core.IR;

public enum IrRegisterAllocationStatusV1 : byte
{
    Allocated = 0,
    SafeFallback = 1,
    Unknown = 2,
    Unsupported = 3,
    BudgetExhausted = 4,
    InvalidInput = 5
}

public enum IrAllocationMutationKindV1 : byte
{
    PhysicalOperandLowering = 0,
    SpillStore = 1,
    SpillReload = 2,
    Prologue = 3,
    CalleeSave = 4,
    CalleeRestore = 5,
    Epilogue = 6
}

public sealed record HybridCpuRegisterAllocationBudgetsV1(
    int MaximumInstructions,
    int MaximumValues,
    int MaximumAllocationCandidates,
    int MaximumSpillAlternatives,
    int MaximumRepairStages,
    int MaximumInsertedInstructions)
{
    public static HybridCpuRegisterAllocationBudgetsV1 Production { get; } =
        // DoomSharp.Core.GameLogic.MapObjectInfo.AddPredefinedTypes reaches 20655
        // IR instructions / 15174 values after symbol-backed long-call expansion.
        // P_PathTraverseCollectinstance has an exact 439-value allocation subject and
        // requires more than 32 deterministic spill alternatives after long-branch x5
        // lifetimes are represented. Keep the search finite while admitting that proven
        // body; inserted instructions remain independently bounded.
        new(24576, 16384, 32, 1024, 2, 4096);
}

public sealed record HybridCpuRegisterAllocationOptionsV1(
    bool EnableAllocation,
    bool EnableSpills,
    HybridCpuRegisterAllocationBudgetsV1 Budgets,
    string OptionsDigest)
{
    public static HybridCpuRegisterAllocationOptionsV1 Production { get; } = Create(
        enableAllocation: false,
        enableSpills: false,
        HybridCpuRegisterAllocationBudgetsV1.Production);

    public static HybridCpuRegisterAllocationOptionsV1 Qualification { get; } = Create(
        enableAllocation: true,
        enableSpills: true,
        HybridCpuRegisterAllocationBudgetsV1.Production);

    public static HybridCpuRegisterAllocationOptionsV1 Create(
        bool enableAllocation,
        bool enableSpills,
        HybridCpuRegisterAllocationBudgetsV1 budgets)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        string digest = HybridCpuRegisterAllocationContractV1.Hash(string.Join('|',
            "hybridcpu.register-allocation-options/v1", enableAllocation, enableSpills,
            budgets.MaximumInstructions, budgets.MaximumValues, budgets.MaximumAllocationCandidates,
            budgets.MaximumSpillAlternatives, budgets.MaximumRepairStages, budgets.MaximumInsertedInstructions));
        return new(enableAllocation, enableSpills, budgets, digest);
    }
}

public sealed record IrSelectedAllocationPlansV1(
    IReadOnlyList<string> RegionPlanDigests,
    IReadOnlyList<string> LoopOrModuloPlanDigests,
    IReadOnlyList<string> VirtualThreadPlanDigests,
    IReadOnlyList<string> FspOrPrefetchPlanDigests)
{
    public static IrSelectedAllocationPlansV1 BasicBlockOnly { get; } =
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    public IEnumerable<string> AllDigests() =>
        RegionPlanDigests.Concat(LoopOrModuloPlanDigests)
            .Concat(VirtualThreadPlanDigests).Concat(FspOrPrefetchPlanDigests);
}

public sealed record IrRegisterAssignmentV1(
    string ValueId,
    int RegisterId,
    int RegisterGroup,
    bool IsPrecolored,
    bool LiveAcrossCall,
    int ScheduledStart,
    int ScheduledEndExclusive);

public sealed record IrSpillAccessV1(
    int OriginalInstructionIndex,
    IrValueAccessKind AccessKind,
    int ScratchRegisterId,
    string SyntheticInstructionIdentity);

public sealed record IrSpillDecisionV1(
    string ValueId,
    string FrameSlotIdentity,
    int SizeBytes,
    int AlignmentBytes,
    long SpillCost,
    IReadOnlyList<IrSpillAccessV1> Accesses);

public sealed record IrAllocatedSemanticValueV1(
    string ValueId,
    IrCanonicalTypeV1 ValueKind,
    IrVirtualValueClass VirtualClass);

public sealed record IrAllocationMutationRecordV1(
    IrAllocationMutationKindV1 Kind,
    string Identity,
    int FinalInstructionIndex,
    string? ResponsibleValueId);

public sealed record IrLoopAllocationRebuildV1(
    string LoopId,
    IrCanonicalLoopStatusV1 CanonicalStatus,
    IrLoopMiiEligibilityV1? MiiEligibility,
    int? ProvenLowerBoundIi,
    string DistanceDagDigest,
    string? MiiProofDigest);

public sealed record IrAllocationProofRebuildV1(
    IrMutationStamp OriginalMutationStamp,
    IrMutationStamp FinalMutationStamp,
    string DependencyDigest,
    string ValueFlowDigest,
    string ScheduleDigest,
    string PlacementDigest,
    IReadOnlyList<IrLoopAllocationRebuildV1> Loops,
    bool DependenciesCurrent,
    bool LivenessCurrent,
    bool PressureCurrent,
    bool ResourceFactsCurrent,
    bool MiiRecomputed,
    bool ExactW8PlacementRecomputed);

public sealed record IrRegisterAllocationWitnessV1(
    string SchemaId,
    string ContractDigest,
    string OptionsDigest,
    string TargetDigest,
    string AbiDigest,
    string TopologyDigest,
    string ResourceModelDigest,
    string InputScheduleDigest,
    string SelectedPlansDigest,
    IReadOnlyList<IrRegisterAssignmentV1> Assignments,
    IReadOnlyList<IrSpillDecisionV1> Spills,
    IReadOnlyList<IrAllocatedSemanticValueV1> SemanticValues,
    HybridCpuFrameLayoutV2 Frame,
    IReadOnlyList<IrAllocationMutationRecordV1> Mutations,
    IrAllocationProofRebuildV1 Rebuild,
    int RepairStages,
    string WitnessDigest);

public sealed record IrRegisterAllocationResultV1(
    IrRegisterAllocationStatusV1 Status,
    string Reason,
    IrProgramSchedule OriginalSchedule,
    IrProgramBundlingResult OriginalBundles,
    IrProgramSchedule FinalSchedule,
    IrProgramBundlingResult FinalBundles,
    IrRegisterAllocationWitnessV1? Witness,
    IReadOnlyList<string> Diagnostics,
    string ResultDigest)
{
    public bool UsedExactFallback =>
        ReferenceEquals(OriginalSchedule, FinalSchedule) && ReferenceEquals(OriginalBundles, FinalBundles);
}

public sealed class HybridCpuRegisterAllocationContractV1
{
    public const string SchemaId = "hybridcpu.schedule-aware-register-allocation/v1";

    public static HybridCpuRegisterAllocationContractV1 Default { get; } = new();

    private HybridCpuRegisterAllocationContractV1()
    {
        TargetDigest = HybridCpuTargetMachineContractV1.Default.ContractDigest;
        AbiDigest = HybridCpuNativeAbiContractV2.Default.ContractDigest;
        MachineTopologyDigest = HybridCpuMachineTopologyV1.Default.ContractDigest;
        DefaultResourceModelDigest = HybridCpuMiiResourceModelV1.Default.ModelDigest;
        ProductionOptionsDigest = HybridCpuRegisterAllocationOptionsV1.Production.OptionsDigest;
        QualificationOptionsDigest = HybridCpuRegisterAllocationOptionsV1.Qualification.OptionsDigest;
        ContractDigest = Hash(string.Join('|', SchemaId, TargetDigest, AbiDigest,
            MachineTopologyDigest, DefaultResourceModelDigest, ProductionOptionsDigest,
            QualificationOptionsDigest, "physical-architectural-only", "deterministic-interval-coalescing",
            "spill-scratch=x28,x29,x30,x31", "spill-is-real-exclusive-frame-memory", "abi-ingress-live-from-entry/v1",
            "semantic-value-kinds-in-witness", "all-proofs-rebuilt", "exact-fallback", "wall-clock=false"));
    }

    public string TargetDigest { get; }
    public string AbiDigest { get; }
    public string MachineTopologyDigest { get; }
    public string DefaultResourceModelDigest { get; }
    public string ProductionOptionsDigest { get; }
    public string QualificationOptionsDigest { get; }
    public string ContractDigest { get; }

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
