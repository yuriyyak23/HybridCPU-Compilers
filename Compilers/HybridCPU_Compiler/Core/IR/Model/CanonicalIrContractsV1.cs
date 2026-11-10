using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR.Contracts;

namespace HybridCPU.Compiler.Core.IR;

public enum IrCanonicalValueKind : byte
{
    Integer = 0,
    FloatingPoint = 1,
    Pointer = 2,
    Predicate = 3,
    Vector = 4,
    Opaque = 5,
    Unknown = 6,
    ManagedObjectReference = 7,
    ManagedByRef = 8,
    ManagedInteriorReference = 9
}

public enum IrIntegerOverflowSemantics : byte
{
    Wrap = 0,
    Trap = 1,
    SourcePoison = 2,
    Unknown = 3,
    NotApplicable = 4
}

public enum IrShiftSemantics : byte
{
    MaskCount = 0,
    TrapOutOfRange = 1,
    SourcePoisonOutOfRange = 2,
    Unknown = 3,
    NotApplicable = 4
}

public enum IrUndefinedValueSemantics : byte
{
    NotApplicable = 0,
    ExplicitUnknownValue = 1,
    SourcePoison = 2,
    SourceUndef = 3,
    Unsupported = 4,
    Unknown = 5
}

public enum IrPointerArithmeticSemantics : byte
{
    NotApplicable = 0,
    TargetAddressWrap = 1,
    BoundsChecked = 2,
    Unsupported = 3,
    Unknown = 4
}

public sealed record IrCanonicalTypeV1(
    IrCanonicalValueKind Kind,
    int BitWidth,
    bool IsSigned,
    int LaneCount = 1);

public sealed record IrInstructionSemanticsV1(
    IrIntegerOverflowSemantics IntegerOverflow,
    IrShiftSemantics Shift,
    IrPointerArithmeticSemantics PointerArithmetic,
    IrUndefinedValueSemantics UndefinedValue,
    bool ConversionMayTrap,
    bool OperationMayFault)
{
    public static IrInstructionSemanticsV1 Native(bool mayFault) => new(
        IrIntegerOverflowSemantics.Wrap,
        IrShiftSemantics.MaskCount,
        IrPointerArithmeticSemantics.TargetAddressWrap,
        IrUndefinedValueSemantics.NotApplicable,
        ConversionMayTrap: false,
        OperationMayFault: mayFault);

    public static IrInstructionSemanticsV1 Unknown { get; } = new(
        IrIntegerOverflowSemantics.Unknown,
        IrShiftSemantics.Unknown,
        IrPointerArithmeticSemantics.Unknown,
        IrUndefinedValueSemantics.Unknown,
        ConversionMayTrap: true,
        OperationMayFault: true);

    public bool IsFullySpecified =>
        IntegerOverflow != IrIntegerOverflowSemantics.Unknown &&
        Shift != IrShiftSemantics.Unknown &&
        PointerArithmetic != IrPointerArithmeticSemantics.Unknown &&
        UndefinedValue != IrUndefinedValueSemantics.Unknown;
}

public enum IrSourceOriginKind : byte
{
    NativeCarrier = 0,
    NativeAssembly = 1,
    Llvm = 2,
    Cil = 3,
    Generated = 4,
    Migrated = 5,
    Unknown = 6
}

public enum IrFrontendEvidenceTrust : byte
{
    DiagnosticOnly = 0,
    ValidatedStructural = 1,
    ProfitabilityOnly = 2,
    Unknown = 3
}

public sealed record IrSourceOriginLinkV1(
    string StableSourceIdentity,
    IrSourceOriginKind Kind,
    string ProducerId,
    string ProducerVersion,
    IrSourceSpan? Span,
    IrFrontendEvidenceTrust Trust);

public sealed record IrSourceOriginChainV1(
    int SchemaVersion,
    IReadOnlyList<IrSourceOriginLinkV1> Links)
{
    public static IrSourceOriginChainV1 Native(int index, ulong address, IrSourceSpan? span) => new(
        1,
        [new(
            $"native:{address.ToString("x16", CultureInfo.InvariantCulture)}:{index.ToString(CultureInfo.InvariantCulture)}",
            IrSourceOriginKind.NativeCarrier,
            "HybridCPU.Compiler.Core",
            "1",
            span,
            IrFrontendEvidenceTrust.ValidatedStructural)]);
}

[Flags]
public enum IrMemoryEffectKind : byte
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    Atomic = 1 << 2,
    Volatile = 1 << 3,
    Fence = 1 << 4,
    Unknown = 1 << 7
}

public enum IrAddressSpaceIdentity : byte
{
    Generic = 0,
    Stack = 1,
    Global = 2,
    Constant = 3,
    ThreadLocal = 4,
    Device = 5,
    Unknown = 255
}

public enum IrMemoryOrdering : byte
{
    NotAtomic = 0,
    Relaxed = 1,
    Acquire = 2,
    Release = 3,
    AcquireRelease = 4,
    SequentiallyConsistent = 5,
    Unknown = 255
}

public sealed record IrCanonicalMemoryEffectV1(
    IrMemoryEffectKind Kind,
    IrAddressSpaceIdentity AddressSpace,
    IrMemoryOrdering Ordering,
    IrMemoryRegion? ReadRegion,
    IrMemoryRegion? WriteRegion)
{
    public static IrCanonicalMemoryEffectV1 None { get; } = new(
        IrMemoryEffectKind.None,
        IrAddressSpaceIdentity.Generic,
        IrMemoryOrdering.NotAtomic,
        null,
        null);

    public static IrCanonicalMemoryEffectV1 Unknown { get; } = new(
        IrMemoryEffectKind.Unknown,
        IrAddressSpaceIdentity.Unknown,
        IrMemoryOrdering.Unknown,
        null,
        null);

    public bool ConservativelyAliases(IrCanonicalMemoryEffectV1 other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Kind == IrMemoryEffectKind.None || other.Kind == IrMemoryEffectKind.None) return false;
        if (Kind.HasFlag(IrMemoryEffectKind.Unknown) || other.Kind.HasFlag(IrMemoryEffectKind.Unknown)) return true;
        if (AddressSpace == IrAddressSpaceIdentity.Unknown || other.AddressSpace == IrAddressSpaceIdentity.Unknown) return true;
        if (AddressSpace != other.AddressSpace) return false;
        return HasWrite(Kind) || HasWrite(other.Kind);
    }

    private static bool HasWrite(IrMemoryEffectKind kind) =>
        (kind & (IrMemoryEffectKind.Write | IrMemoryEffectKind.Atomic | IrMemoryEffectKind.Volatile | IrMemoryEffectKind.Fence)) != 0;
}

[Flags]
public enum IrArchitecturalEffectKind : ushort
{
    None = 0,
    Control = 1 << 0,
    Call = 1 << 1,
    Return = 1 << 2,
    TrapOrFault = 1 << 3,
    Fence = 1 << 4,
    SpecialArchitecturalState = 1 << 5,
    Unknown = 1 << 15
}

public sealed record IrSideEffectSummaryV1(
    IrCanonicalMemoryEffectV1 Memory,
    IrArchitecturalEffectKind ArchitecturalEffects);

public readonly record struct IrMutationStamp(long Value)
{
    public IrMutationStamp Next() => new(checked(Value + 1));
}

public sealed record IrDerivedFactVersionsV1(
    IrMutationStamp ProgramMutation,
    IrMutationStamp? Dependency,
    IrMutationStamp? Liveness,
    IrMutationStamp? Pressure,
    IrMutationStamp? Mii,
    IrMutationStamp? Placement)
{
    public static IrDerivedFactVersionsV1 Initial { get; } = new(new(0), null, null, null, null, null);

    public IrDerivedFactVersionsV1 WithDependenciesCurrent() => this with { Dependency = ProgramMutation };

    public IrDerivedFactVersionsV1 WithValueAnalysisCurrent() => this with
    {
        Liveness = ProgramMutation,
        Pressure = ProgramMutation
    };

    public IrDerivedFactVersionsV1 InvalidateAfterScheduleChangingMutation() =>
        new(ProgramMutation.Next(), null, null, null, null, null);

    public bool IsCurrent(IrMutationStamp? fact) => fact.HasValue && fact.Value == ProgramMutation;
}

public sealed record IrCanonicalProgramContractV1(
    string SchemaId,
    int SchemaVersion,
    IReadOnlyList<string> RequiredCapabilities,
    IrDerivedFactVersionsV1 DerivedFacts)
{
    public static IrCanonicalProgramContractV1 NativeV1 { get; } = new(
        "hybridcpu.canonical-ir/v1",
        1,
        ["isa.hybridcpu-w8-native-v1"],
        IrDerivedFactVersionsV1.Initial);
}

public sealed record IrControlPredicateV1(string StableIdentity, byte PredicateMask);

public sealed record IrSchedulingRegionDependenceViewV1(
    IrMutationStamp MutationStamp,
    IReadOnlyList<IrInstructionDependency> Dependencies);

public sealed record IrSchedulingRegionV1(
    string SchemaId,
    string RegionId,
    IReadOnlyList<IrBasicBlock> Blocks,
    IReadOnlyList<int> EntryBlockIds,
    IReadOnlyList<int> ExitBlockIds,
    IReadOnlyList<int> SideExitBlockIds,
    IReadOnlyList<IrControlPredicateV1> ControlPredicates,
    IrSchedulingRegionDependenceViewV1 DependenceView,
    IReadOnlyList<string> RequiredCapabilities,
    bool ExpansionEnabled);

public static class HybridCpuSchedulingRegionFactoryV1
{
    public static IReadOnlyList<IrSchedulingRegionV1> CreateBasicBlockRegions(
        IrProgram program,
        IrProgramDependencyGraph dependencies)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(dependencies);
        List<IrSchedulingRegionV1> regions = new List<IrSchedulingRegionV1>(program.BasicBlocks.Count);
        foreach (IrBasicBlock block in program.BasicBlocks.OrderBy(static block => block.Id))
        {
            if (!dependencies.TryGetBlockGraph(block.Id, out IrBasicBlockDependencyGraph? graph) || graph is null)
                throw new InvalidOperationException($"Dependency graph is missing BB region {block.Id}.");
            string identityInput = string.Join('|',
                "hybridcpu.scheduling-region/v1",
                program.VirtualThreadId.ToString(CultureInfo.InvariantCulture),
                block.Id.ToString(CultureInfo.InvariantCulture),
                string.Join(',', block.Instructions.Select(static instruction => instruction.StableIdentity)),
                string.Join(',', block.PredecessorBlockIds.OrderBy(static id => id)),
                string.Join(',', block.SuccessorBlockIds.OrderBy(static id => id)));
            string regionId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityInput))).ToLowerInvariant();
            int[] exits = block.SuccessorBlockIds.OrderBy(static id => id).ToArray();
            IReadOnlyList<IrInstructionDependency> regionDependencies = BuildConservativeRegionDependencies(block, graph);
            regions.Add(new(
                "hybridcpu.scheduling-region/v1",
                regionId,
                [block],
                [block.Id],
                exits,
                Array.Empty<int>(),
                block.Instructions
                    .Where(static instruction => instruction.PredicateMask != byte.MaxValue)
                    .Select(static instruction => new IrControlPredicateV1(instruction.StableIdentity, instruction.PredicateMask))
                    .OrderBy(static predicate => predicate.StableIdentity, StringComparer.Ordinal)
                    .ToArray(),
                new(program.Contract.DerivedFacts.ProgramMutation, regionDependencies),
                program.Contract.RequiredCapabilities.OrderBy(static capability => capability, StringComparer.Ordinal).ToArray(),
                ExpansionEnabled: false));
        }
        return regions;
    }

    private static IReadOnlyList<IrInstructionDependency> BuildConservativeRegionDependencies(
        IrBasicBlock block,
        IrBasicBlockDependencyGraph graph)
    {
        HashSet<IrInstructionDependency> dependencies = new HashSet<IrInstructionDependency>(graph.Dependencies);
        for (int producerIndex = 0; producerIndex < block.Instructions.Count; producerIndex++)
        {
            IrInstruction producer = block.Instructions[producerIndex];
            for (int consumerIndex = producerIndex + 1; consumerIndex < block.Instructions.Count; consumerIndex++)
            {
                IrInstruction consumer = block.Instructions[consumerIndex];
                if (!producer.SideEffects.Memory.ConservativelyAliases(consumer.SideEffects.Memory)) continue;
                dependencies.Add(new(
                    IrInstructionDependencyKind.Memory,
                    producer.Index,
                    consumer.Index,
                    MinimumLatencyCycles: 1,
                    MemoryPrecision: IrMemoryDependencyPrecision.May,
                    DominantEffectKind: IrHazardEffectKind.MemoryBank));
            }
        }
        return dependencies
            .OrderBy(static dependency => dependency.ProducerInstructionIndex)
            .ThenBy(static dependency => dependency.ConsumerInstructionIndex)
            .ThenBy(static dependency => dependency.Kind)
            .ToArray();
    }
}

public enum IrFrontendAdapterStatus : byte
{
    Success = 0,
    Unsupported = 1,
    InvalidInput = 2,
    UnknownSemantics = 3
}

public sealed record IrFrontendDiagnosticV1(string Code, string Message, string? StableSourceIdentity = null);

public sealed record IrFrontendAdapterResultV1(
    IrFrontendAdapterStatus Status,
    IrProgram? Program,
    IReadOnlyList<IrFrontendDiagnosticV1> Diagnostics);

public static class CanonicalIrFrontendBoundaryV1
{
    private static readonly string[] ForbiddenFrontendHandleNames = ["LLVMValueRef", "MetadataToken", "Roslyn", "ISymbol"];

    public static IrFrontendAdapterResultV1 Validate(IrProgram? program)
    {
        if (program is null)
            return Reject(IrFrontendAdapterStatus.InvalidInput, "HCIR0001", "Frontend produced no Canonical IR program.");
        foreach (IrInstruction instruction in program.Instructions)
        {
            if (string.IsNullOrWhiteSpace(instruction.StableIdentity))
                return Reject(IrFrontendAdapterStatus.InvalidInput, "HCIR0002", "Instruction stable identity is absent.");
            if (!instruction.Semantics.IsFullySpecified ||
                (instruction.SideEffects.Memory.Kind != IrMemoryEffectKind.None &&
                 instruction.SideEffects.Memory.AddressSpace == IrAddressSpaceIdentity.Unknown))
                return Reject(IrFrontendAdapterStatus.UnknownSemantics, "HCIR0003", "Unknown legality-relevant frontend semantics fail before scheduling.", instruction.StableIdentity);
            if (IsForbiddenFrontendHandle(instruction.DmaStreamComputeDescriptor) ||
                IsForbiddenFrontendHandle(instruction.AcceleratorCommandDescriptor))
                return Reject(IrFrontendAdapterStatus.Unsupported, "HCIR0004", "Frontend-specific object handle cannot enter Core scheduling objects.", instruction.StableIdentity);
        }
        foreach (Type type in typeof(IrInstruction).Assembly.GetExportedTypes())
        {
            if (type.Namespace?.StartsWith("HybridCPU.Compiler.Core.IR", StringComparison.Ordinal) != true) continue;
            foreach (PropertyInfo property in type.GetProperties())
            {
                string typeName = property.PropertyType.FullName ?? property.PropertyType.Name;
                if (ForbiddenFrontendHandleNames.Any(typeName.Contains))
                    return Reject(IrFrontendAdapterStatus.Unsupported, "HCIR0004", $"Frontend-specific handle retained by Core property {type.FullName}.{property.Name}.");
            }
        }
        return IrFrontendAnalysisEvidenceValidatorV1.Validate(program);
    }

    private static bool IsForbiddenFrontendHandle(object? value)
    {
        if (value is null) return false;
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return ForbiddenFrontendHandleNames.Any(typeName.Contains);
    }

    private static IrFrontendAdapterResultV1 Reject(
        IrFrontendAdapterStatus status,
        string code,
        string message,
        string? identity = null) =>
        new(status, null, [new(code, message, identity)]);
}

public static class IrCanonicalIdentityV1
{
    public static string Create(IrInstruction instruction, IrSourceOriginChainV1 origin)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(origin);
        string sourceIdentity = origin.Links.Count == 0 ? "unknown" : origin.Links[^1].StableSourceIdentity;
        string input = string.Join('|', "hybridcpu.ir-instruction/v1", sourceIdentity,
            ((ushort)instruction.Opcode).ToString(CultureInfo.InvariantCulture),
            ((byte)instruction.DataType).ToString(CultureInfo.InvariantCulture),
            instruction.VirtualThreadId.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
}
