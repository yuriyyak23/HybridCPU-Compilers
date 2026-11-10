using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU.Compiler.Core.IR;

public enum IrCanonicalLoopStatusV1 : byte
{
    Qualified = 0,
    Unknown = 1,
    Unsupported = 2,
    BudgetExhausted = 3
}

public enum IrLoopProofPrecisionV1 : byte
{
    Exact = 0,
    Conservative = 1,
    Unknown = 2
}

public enum IrMiiComponentKindV1 : byte
{
    RecMii = 0,
    SlotMii = 1,
    PrfReadPortMii = 2,
    PrfWritePortMii = 3,
    RegisterGroupMii = 4,
    MemoryBankMii = 5,
    MemoryChannelMii = 6,
    Lane6Mii = 7,
    Lane7Mii = 8,
    CertificateMii = 9
}

public enum IrMiiComponentStatusV1 : byte
{
    Proven = 0,
    NotApplicable = 1,
    Unknown = 2,
    Unsupported = 3
}

public enum IrLoopMiiEligibilityV1 : byte
{
    EligibleLowerBound = 0,
    IneligibleUnknown = 1,
    IneligibleUnsupported = 2,
    BudgetExhausted = 3,
    StaleProof = 4
}

public sealed record IrLoopPhiIncomingV1(
    string ValueId,
    int SourceBlockId,
    int TargetBlockId,
    int InstructionIndex);

public sealed record IrLoopDistanceDependencyV1(
    string EdgeId,
    int ProducerInstructionIndex,
    int ConsumerInstructionIndex,
    int IterationDistance,
    int LatencyCycles,
    IrInstructionDependencyKind Kind,
    IrLoopProofPrecisionV1 Precision,
    string ProofReason);

public sealed record IrCanonicalLoopV1(
    string SchemaId,
    string LoopId,
    string VersionStamp,
    string ProgramVersionStamp,
    string InstructionDigest,
    string DistanceDagDigest,
    IrMutationStamp MutationStamp,
    int PreheaderBlockId,
    int HeaderBlockId,
    IReadOnlyList<int> LatchBlockIds,
    IReadOnlyList<int> ExitBlockIds,
    IReadOnlyList<int> BlockIds,
    IReadOnlyList<IrInstruction> Instructions,
    IReadOnlyList<IrLoopPhiIncomingV1> PhiIncoming,
    IReadOnlyList<IrLoopDistanceDependencyV1> DistanceDependencies,
    IrValueAnalysisReportV1 ValueAnalysis,
    IrCanonicalLoopStatusV1 Status,
    string Reason);

public sealed record IrLoopCanonicalizationResultV1(
    IrCanonicalLoopStatusV1 Status,
    string ProgramShapeDigest,
    string TargetDigest,
    string ResourceModelDigest,
    IReadOnlyList<IrCanonicalLoopV1> Loops,
    IReadOnlyList<IrRegionSchedulingDiagnosticV1> Diagnostics);

public sealed record IrMiiComponentResultV1(
    IrMiiComponentKindV1 Component,
    IrMiiComponentStatusV1 Status,
    int? Value,
    int Numerator,
    int? Capacity,
    IrLoopProofPrecisionV1 Precision,
    IReadOnlyList<string> Contributors,
    string Reason,
    string TargetDigest,
    string ResourceModelDigest);

public sealed record IrLoopMiiProofStampV1(
    string SchemaId,
    string LoopId,
    string LoopVersionStamp,
    IrMutationStamp MutationStamp,
    string TargetDigest,
    string ResourceModelDigest,
    string ContractDigest,
    string ProfileIdentity,
    string ProofDigest);

public sealed record IrLoopMiiReportV1(
    IrLoopMiiEligibilityV1 Eligibility,
    int? ProvenLowerBoundIi,
    IReadOnlyList<IrMiiComponentResultV1> Components,
    IrLoopMiiProofStampV1 ProofStamp,
    IReadOnlyList<IrRegionSchedulingDiagnosticV1> Diagnostics);

public sealed record HybridCpuMiiResourceModelV1(
    HybridCpuMachineTopologyV1 Topology,
    int? StructuralCertificateCapacity,
    string ModelDigest)
{
    public static HybridCpuMiiResourceModelV1 Default { get; } = Create(
        HybridCpuMachineTopologyV1.Default,
        structuralCertificateCapacity: null);

    public static HybridCpuMiiResourceModelV1 Create(
        HybridCpuMachineTopologyV1 topology,
        int? structuralCertificateCapacity)
    {
        ArgumentNullException.ThrowIfNull(topology);
        if (structuralCertificateCapacity is <= 0)
            throw new ArgumentOutOfRangeException(nameof(structuralCertificateCapacity));
        string input = string.Join('|',
            "hybridcpu.mii-resource-model/v1",
            topology.ContractDigest,
            structuralCertificateCapacity?.ToString(CultureInfo.InvariantCulture) ?? "Unknown",
            "certificate=compiler-structural-only");
        return new(
            topology,
            structuralCertificateCapacity,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant());
    }
}

public sealed record HybridCpuLoopMiiBudgetsV1(
    int MaximumLoops,
    int MaximumBlocksPerLoop,
    int MaximumDistanceEdges,
    int MaximumEnumeratedCycles)
{
    public static HybridCpuLoopMiiBudgetsV1 Production { get; } = new(64, 128, 4096, 8192);
    /// <summary>Separate edge-relaxation work limit for exact ratio fallback; not a cycle-count increase.</summary>
    public int MaximumRecurrenceRelaxations { get; init; } = 8_000_000;
}

public sealed class HybridCpuLoopMiiContractV1
{
    public const string SchemaId = "hybridcpu.loop-distance-mii/v1";

    public static HybridCpuLoopMiiContractV1 Default { get; } = new();

    private HybridCpuLoopMiiContractV1()
    {
        TargetDigest = HybridCpuTargetMachineContractV1.Default.ContractDigest;
        RegionContractDigest = HybridCpuRegionSchedulingContractV1.Default.ContractDigest;
        DefaultResourceModelDigest = HybridCpuMiiResourceModelV1.Default.ModelDigest;
        string components = string.Join(',', Enum.GetValues<IrMiiComponentKindV1>());
        ContractDigest = Hash(string.Join('|',
            SchemaId,
            TargetDigest,
            RegionContractDigest,
            DefaultResourceModelDigest,
            components,
            "unknown-is-not-zero",
            "recurrence-parallel-max-latency-same-distance-precision/v1",
            "recurrence-integer-positive-cycle-fallback/v1:relaxations=8000000",
            "canonical-zero-distance-dag-required/v1",
            "profile-cannot-change-proof",
            "schedule-output=unchanged",
            "wall-clock=false"));
    }

    public string TargetDigest { get; }
    public string RegionContractDigest { get; }
    public string DefaultResourceModelDigest { get; }
    public string ContractDigest { get; }
    public IrRegionOutputDispositionV1 OutputDisposition => IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly;

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
