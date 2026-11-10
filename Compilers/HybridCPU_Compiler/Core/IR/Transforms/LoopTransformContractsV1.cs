using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Compiler.Core.IR;

public enum IrLoopTransformKindV1 : byte
{
    ModuloExpansion = 0,
    ArchitectureDrivenUnroll = 1,
    LoopFusion = 2,
    UnrollAndJam = 3
}

public enum IrLoopTransformStatusV1 : byte
{
    Accepted = 0,
    DisabledFallback = 1,
    Ineligible = 2,
    StaleProof = 3,
    BudgetExhausted = 4,
    InvalidModel = 5
}

public enum IrLoopTransformSwitchV1 : byte
{
    Disabled = 0,
    ExplicitQualificationOnly = 1
}

[Flags]
public enum IrLoopInvalidatedAnalysisKindV1 : ushort
{
    None = 0,
    CanonicalCapabilityAndSideEffects = 1 << 0,
    DependencyDag = 1 << 1,
    LoopDistances = 1 << 2,
    Liveness = 1 << 3,
    Pressure = 1 << 4,
    ResourceModel = 1 << 5,
    Mii = 1 << 6,
    Placement = 1 << 7
}

public enum IrModuloExpansionPhaseV1 : byte
{
    Prolog = 0,
    Kernel = 1,
    Epilog = 2
}

public enum IrLoopTransformCandidateStatusV1 : byte
{
    Baseline = 0,
    Accepted = 1,
    NoKpiBenefit = 2,
    TripCountInsufficient = 3,
    CodeGrowthLimit = 4,
    PressureLimit = 5,
    UnsupportedControlOrEffect = 6,
    AnalysisRejected = 7,
    ScheduleRejected = 8,
    BudgetExhausted = 9
}

public enum IrLoopTransformProfileDispositionV1 : byte
{
    AbsentStaticPolicy = 0,
    ProfitabilityOnly = 1
}

public sealed record HybridCpuLoopTransformBudgetsV1(
    int MaximumBodyOperations,
    int MaximumTransformedOperations,
    int MaximumMaterializedOperations,
    int MaximumCandidateFactors,
    int MaximumCodeGrowthPercent,
    int MaximumPressureIncrease,
    int MinimumBenefitBasisPoints,
    long MaximumKnownTripCount)
{
    public static HybridCpuLoopTransformBudgetsV1 Production { get; } = new(
        64, 256, 4096, 4, 300, 16, 200, 1024);
}

public sealed record HybridCpuLoopTransformOptionsV1(
    HybridCpuLoopTransformBudgetsV1 Budgets,
    IReadOnlyList<int> CandidateUnrollFactors,
    IrLoopTransformSwitchV1 ModuloExpansionSwitch,
    IrLoopTransformSwitchV1 ArchitectureUnrollSwitch,
    IrLoopTransformSwitchV1 FusionSwitch,
    IrLoopTransformSwitchV1 UnrollAndJamSwitch,
    string OptionsDigest)
{
    public static HybridCpuLoopTransformOptionsV1 Production { get; } = Create(
        HybridCpuLoopTransformBudgetsV1.Production,
        [1, 2, 4, 8],
        IrLoopTransformSwitchV1.Disabled,
        IrLoopTransformSwitchV1.Disabled,
        IrLoopTransformSwitchV1.Disabled,
        IrLoopTransformSwitchV1.Disabled);

    public static HybridCpuLoopTransformOptionsV1 Qualification { get; } = Create(
        HybridCpuLoopTransformBudgetsV1.Production,
        [1, 2, 4, 8],
        IrLoopTransformSwitchV1.ExplicitQualificationOnly,
        IrLoopTransformSwitchV1.ExplicitQualificationOnly,
        IrLoopTransformSwitchV1.ExplicitQualificationOnly,
        IrLoopTransformSwitchV1.ExplicitQualificationOnly);

    public static HybridCpuLoopTransformOptionsV1 Create(
        HybridCpuLoopTransformBudgetsV1 budgets,
        IReadOnlyList<int> candidateUnrollFactors,
        IrLoopTransformSwitchV1 moduloExpansionSwitch,
        IrLoopTransformSwitchV1 architectureUnrollSwitch,
        IrLoopTransformSwitchV1 fusionSwitch,
        IrLoopTransformSwitchV1 unrollAndJamSwitch)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        ArgumentNullException.ThrowIfNull(candidateUnrollFactors);
        Validate(budgets, candidateUnrollFactors);
        int[] factors = candidateUnrollFactors.Distinct().Order().ToArray();
        string digest = HybridCpuLoopTransformContractV1.Hash(string.Join('|',
            "hybridcpu.loop-transform-options/v1",
            budgets.MaximumBodyOperations.ToString(CultureInfo.InvariantCulture),
            budgets.MaximumTransformedOperations.ToString(CultureInfo.InvariantCulture),
            budgets.MaximumMaterializedOperations.ToString(CultureInfo.InvariantCulture),
            budgets.MaximumCandidateFactors.ToString(CultureInfo.InvariantCulture),
            budgets.MaximumCodeGrowthPercent.ToString(CultureInfo.InvariantCulture),
            budgets.MaximumPressureIncrease.ToString(CultureInfo.InvariantCulture),
            budgets.MinimumBenefitBasisPoints.ToString(CultureInfo.InvariantCulture),
            budgets.MaximumKnownTripCount.ToString(CultureInfo.InvariantCulture),
            string.Join(',', factors),
            moduloExpansionSwitch,
            architectureUnrollSwitch,
            fusionSwitch,
            unrollAndJamSwitch,
            "profile=profitability-only",
            "wall-clock=false",
            "fallback=exact-original-loop"));
        return new(budgets, factors, moduloExpansionSwitch, architectureUnrollSwitch,
            fusionSwitch, unrollAndJamSwitch, digest);
    }

    internal static void Validate(
        HybridCpuLoopTransformBudgetsV1 budgets,
        IReadOnlyList<int> factors)
    {
        if (budgets.MaximumBodyOperations <= 0 || budgets.MaximumTransformedOperations <= 0 ||
            budgets.MaximumMaterializedOperations <= 0 || budgets.MaximumCandidateFactors <= 0 ||
            budgets.MaximumCodeGrowthPercent < 0 || budgets.MaximumPressureIncrease < 0 ||
            budgets.MinimumBenefitBasisPoints < 0 || budgets.MaximumKnownTripCount < 0 ||
            factors.Count == 0 || factors.Count > budgets.MaximumCandidateFactors ||
            factors.Any(static factor => factor is < 1 or > 8) || !factors.Contains(1))
            throw new ArgumentOutOfRangeException(nameof(budgets));
    }
}

public sealed record IrLoopTransformProfileV1(
    IrLoopTransformProfileDispositionV1 Disposition,
    long? ExpectedTripCount,
    string ProfileDigest)
{
    public static IrLoopTransformProfileV1 Absent { get; } = new(
        IrLoopTransformProfileDispositionV1.AbsentStaticPolicy, null, "absent");
}

public sealed record IrLoopIterationDomainProofV1(
    string LoopId,
    string LoopVersionStamp,
    long InitialValue,
    long ExclusiveLimit,
    long Step,
    long TripCount,
    string ProofDigest)
{
    public static IrLoopIterationDomainProofV1 Create(
        IrCanonicalLoopV1 loop,
        long initialValue,
        long exclusiveLimit,
        long step)
    {
        ArgumentNullException.ThrowIfNull(loop);
        if (step <= 0 || exclusiveLimit < initialValue) throw new ArgumentOutOfRangeException(nameof(step));
        long distance = checked(exclusiveLimit - initialValue);
        long tripCount = distance == 0 ? 0 : checked((distance + step - 1) / step);
        string digest = HybridCpuLoopTransformContractV1.Hash(string.Join('|',
            "hybridcpu.loop-iteration-domain-proof/v1",
            loop.LoopId,
            loop.VersionStamp,
            initialValue.ToString(CultureInfo.InvariantCulture),
            exclusiveLimit.ToString(CultureInfo.InvariantCulture),
            step.ToString(CultureInfo.InvariantCulture),
            tripCount.ToString(CultureInfo.InvariantCulture),
            "static-canonical-proof-not-profile"));
        return new(loop.LoopId, loop.VersionStamp, initialValue, exclusiveLimit, step, tripCount, digest);
    }
}

public sealed record IrExpandedValueBindingV1(
    string OriginalValueId,
    string VersionedValueId,
    IrValueAccessKind AccessKind,
    int SourceIteration,
    int? PhiSourceIteration);

public sealed record IrModuloExpandedOperationV1(
    string InstanceId,
    int MaterializedInstructionIndex,
    int OriginalInstructionIndex,
    string OriginalInstructionIdentity,
    int SourceIteration,
    int AbsoluteCycle,
    int ModuloCycle,
    int IssueSlot,
    IrModuloExpansionPhaseV1 Phase,
    IrInstruction Instruction,
    IReadOnlyList<IrExpandedValueBindingV1> ValueBindings);

public sealed record IrModuloExpansionV1(
    string SchemaId,
    string ExpansionDigest,
    string LoopId,
    string LoopVersionStamp,
    string KernelWitnessDigest,
    long TripCount,
    int InitiationInterval,
    int PipelineDepth,
    IReadOnlyList<IrModuloExpandedOperationV1> Prolog,
    IReadOnlyList<IrModuloExpandedOperationV1> Kernel,
    IReadOnlyList<IrModuloExpandedOperationV1> Epilog,
    IReadOnlyDictionary<string, string> LiveInMapping,
    IReadOnlyDictionary<string, string> LiveOutMapping,
    string PlacementValidationDigest,
    string TemporalValidationDigest);

public sealed record IrLoopTransformProfitabilityV1(
    IrLoopTransformProfileDispositionV1 ProfileDisposition,
    int BaselineIi,
    int CandidateIi,
    int SourceIterationsPerCandidateIteration,
    int BenefitBasisPoints,
    int CodeSizeDeltaOperations,
    int PressureDelta,
    long RemainderIterations,
    long DeterministicWorkUnits,
    string Reason,
    string EvidenceDigest);

public sealed record IrLoopTransformCandidateV1(
    IrLoopTransformKindV1 Kind,
    int Factor,
    IrLoopTransformCandidateStatusV1 Status,
    int? RecomputedIi,
    int CodeSizeDeltaOperations,
    int PressureDelta,
    int BenefitBasisPoints,
    long DeterministicWorkUnits,
    string Reason,
    string CandidateDigest);

public sealed record IrLoopTransformFreshFactsV1(
    IrMutationStamp MutationStamp,
    string DependencyDigest,
    string ValueFlowDigest,
    string MiiProofDigest,
    string PlacementWitnessDigest,
    string PlacementValidationDigest,
    IrLoopInvalidatedAnalysisKindV1 RecomputedAnalysisKinds,
    string FactsDigest);

public sealed record IrLoopTransformResultV1(
    IrLoopTransformKindV1 Kind,
    IrLoopTransformStatusV1 Status,
    IrProgram? TransformedProgram,
    IrCanonicalLoopV1? TransformedLoop,
    IrModuloExpansionV1? Expansion,
    IrLoopMiiReportV1? RecomputedMiiSummary,
    IrModuloScheduleWitnessV1? RecomputedWitness,
    IReadOnlyList<string> LegalityProofRefs,
    IrLoopInvalidatedAnalysisKindV1 InvalidatedAnalysisKinds,
    IrLoopTransformFreshFactsV1? FreshFacts,
    IrLoopTransformProfitabilityV1? ProfitabilityEvidence,
    IReadOnlyList<IrLoopTransformCandidateV1> Candidates,
    string FallbackLoopIdentity,
    IReadOnlyList<IrRegionSchedulingDiagnosticV1> Diagnostics,
    string ResultDigest);

public sealed class HybridCpuLoopTransformContractV1
{
    public const string SchemaId = "hybridcpu.loop-transform/v1";
    public const string ExpansionSchemaId = "hybridcpu.modulo-expansion/v1";

    public static HybridCpuLoopTransformContractV1 Default { get; } = new();

    private HybridCpuLoopTransformContractV1()
    {
        LoopMiiContractDigest = HybridCpuLoopMiiContractV1.Default.ContractDigest;
        ModuloSchedulerContractDigest = HybridCpuModuloSchedulingContractV1.Default.ContractDigest;
        ProductionOptionsDigest = HybridCpuLoopTransformOptionsV1.Production.OptionsDigest;
        QualificationOptionsDigest = HybridCpuLoopTransformOptionsV1.Qualification.OptionsDigest;
        ContractDigest = Hash(string.Join('|',
            SchemaId,
            ExpansionSchemaId,
            LoopMiiContractDigest,
            ModuloSchedulerContractDigest,
            ProductionOptionsDigest,
            QualificationOptionsDigest,
            "materialization=prolog-kernel-epilog-with-value-rotation",
            "mutation=all-derived-facts-invalidated-and-recomputed",
            "placement=shared-exact-w8-revalidated",
            "profile=profitability-only",
            "default=disabled",
            "production-output=exact-original-fallback",
            "runtime-authority=false",
            "wall-clock=false"));
    }

    public string LoopMiiContractDigest { get; }
    public string ModuloSchedulerContractDigest { get; }
    public string ProductionOptionsDigest { get; }
    public string QualificationOptionsDigest { get; }
    public string ContractDigest { get; }
    public IrLoopTransformSwitchV1 DefaultDisposition => IrLoopTransformSwitchV1.Disabled;
    public IrRegionOutputDispositionV1 OutputDisposition => IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly;

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
