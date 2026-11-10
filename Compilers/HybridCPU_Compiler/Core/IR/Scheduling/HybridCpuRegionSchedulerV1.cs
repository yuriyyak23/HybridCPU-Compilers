using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU.Compiler.Core.IR;

public enum IrRegionSchedulingModeV1 : byte
{
    Disabled = 0,
    Shadow = 1
}

public enum IrRegionEvaluationStatusV1 : byte
{
    DisabledFallback = 0,
    ShadowQualified = 1,
    NoQualifiedCandidate = 2,
    StaleWitness = 3,
    InvalidOptions = 4
}

public enum IrRegionCandidateStatusV1 : byte
{
    QualifiedStraightLineEbb = 0,
    RejectedControlEdge = 1,
    RejectedSideExit = 2,
    RejectedPredicate = 3,
    RejectedMemory = 4,
    RejectedFaultOrArchitecturalEffect = 5,
    RejectedSpecialContour = 6,
    RejectedUnresolvedControl = 7
}

public enum IrRegionMemoryRelationV1 : byte
{
    NoAlias = 0,
    MustAlias = 1,
    MayAlias = 2,
    Unknown = 3
}

public enum IrRegionMotionSupportV1 : byte
{
    SupportedStraightLine = 0,
    Unsupported = 1,
    Unknown = 2
}

public enum IrRegionFactDispositionV1 : byte
{
    FallbackFactsUnchanged = 0,
    DependencyLivenessPressureResourceAndPlacementRecomputedMiiInvalidated = 1,
    StaleFactsRejected = 2
}

public enum IrRegionOutputDispositionV1 : byte
{
    ExactBasicBlockFallbackOnly = 0
}

public sealed record HybridCpuRegionSchedulingOptionsV1(
    IrRegionSchedulingModeV1 Mode,
    int MaximumCandidates,
    string? ProfileDigest)
{
    public static HybridCpuRegionSchedulingOptionsV1 Disabled { get; } = new(
        IrRegionSchedulingModeV1.Disabled,
        HybridCpuRegionSchedulingContractV1.DefaultMaximumCandidates,
        null);

    public static HybridCpuRegionSchedulingOptionsV1 Shadow { get; } = new(
        IrRegionSchedulingModeV1.Shadow,
        HybridCpuRegionSchedulingContractV1.DefaultMaximumCandidates,
        null);
}

public sealed record IrRegionTargetCapabilitiesV1(
    string TargetContractDigest,
    IrRegionMotionSupportV1 StraightLineFallthrough,
    IrRegionMotionSupportV1 GuardedPredication,
    IrRegionMotionSupportV1 SpeculativeFaultSuppression,
    IrRegionMotionSupportV1 Hyperblocks);

public sealed record IrRegionMemoryWitnessV1(
    int ProducerInstructionIndex,
    int ConsumerInstructionIndex,
    IrRegionMemoryRelationV1 Relation);

public sealed record IrRegionControlWitnessV1(
    int InstructionIndex,
    byte PredicateMask,
    IrArchitecturalEffectKind ArchitecturalEffects,
    IrRegionMotionSupportV1 MotionSupport);

public sealed record IrRegionCandidateV1(
    string SchemaId,
    string RegionId,
    string ProgramShapeDigest,
    IrMutationStamp MutationStamp,
    IReadOnlyList<int> BlockIds,
    IReadOnlyList<int> SideExitBlockIds,
    IrRegionCandidateStatusV1 Status,
    IReadOnlyList<IrInstructionDependency> Dependencies,
    IReadOnlyList<IrRegionMemoryWitnessV1> MemoryWitnesses,
    IReadOnlyList<IrRegionControlWitnessV1> ControlWitnesses,
    IReadOnlyList<string> RequiredCapabilities);

public sealed record IrRegionSchedulingDiagnosticV1(string Code, string Message);

public sealed record IrRegionSchedulingResultV1(
    IrRegionEvaluationStatusV1 Status,
    IrProgramSchedule FallbackSchedule,
    IrProgramBundlingResult FallbackBundles,
    IReadOnlyList<IrRegionCandidateV1> Candidates,
    IrRegionCandidateV1? SelectedCandidate,
    IrProgramSchedule? ShadowSchedule,
    IrProgramBundlingResult? ShadowBundles,
    int FallbackCycles,
    int ShadowCycles,
    IrRegionFactDispositionV1 FactDisposition,
    IrRegionOutputDispositionV1 OutputDisposition,
    string OptionsDigest,
    string ProfileIdentity,
    IReadOnlyList<IrRegionSchedulingDiagnosticV1> Diagnostics);

public sealed class HybridCpuRegionSchedulingContractV1
{
    public const string SchemaId = "hybridcpu.region-scheduling/v1";
    public const int DefaultMaximumCandidates = 64;
    public const int MaximumProductionCandidates = 256;

    public static HybridCpuRegionSchedulingContractV1 Default { get; } = new();

    private HybridCpuRegionSchedulingContractV1()
    {
        Capabilities = new(
            HybridCpuTargetMachineContractV1.Default.ContractDigest,
            IrRegionMotionSupportV1.SupportedStraightLine,
            IrRegionMotionSupportV1.Unsupported,
            IrRegionMotionSupportV1.Unknown,
            IrRegionMotionSupportV1.Unsupported);
        OptionsDigest = Hash(string.Join('|',
            SchemaId,
            $"candidate-budget={DefaultMaximumCandidates.ToString(CultureInfo.InvariantCulture)}",
            "blocks-per-region=2",
            "default=disabled",
            "profile=profitability-only",
            "wall-clock=false",
            "witness-shape=full-canonical-semantics-v2",
            "fallback=exact-bb-path"));
        ContractDigest = Hash(string.Join('|',
            SchemaId,
            Capabilities.TargetContractDigest,
            Capabilities.StraightLineFallthrough,
            Capabilities.GuardedPredication,
            Capabilities.SpeculativeFaultSuppression,
            Capabilities.Hyperblocks,
            IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly,
            OptionsDigest));
    }

    public IrRegionTargetCapabilitiesV1 Capabilities { get; }
    public string OptionsDigest { get; }
    public string ContractDigest { get; }
    public IrRegionSchedulingModeV1 DefaultMode => IrRegionSchedulingModeV1.Disabled;
    public IrRegionOutputDispositionV1 OutputDisposition => IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class HybridCpuRegionSchedulerV1
{
    public IrRegionSchedulingResultV1 Evaluate(
        IrProgram program,
        HybridCpuRegionSchedulingOptionsV1? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        options ??= HybridCpuRegionSchedulingOptionsV1.Disabled;
        (IrProgramSchedule FallbackSchedule, IrProgramBundlingResult FallbackBundles) = ScheduleAndBundle(program);
        if (options.MaximumCandidates is < 1 or > HybridCpuRegionSchedulingContractV1.MaximumProductionCandidates)
        {
            return Result(
                IrRegionEvaluationStatusV1.InvalidOptions,
                FallbackSchedule,
                FallbackBundles,
                Array.Empty<IrRegionCandidateV1>(),
                null,
                null,
                null,
                IrRegionFactDispositionV1.FallbackFactsUnchanged,
                options,
                [new("HCRG0001", "Region candidate budget is outside the deterministic production range.")]);
        }
        if (options.ProfileDigest is not null && !IsSha256(options.ProfileDigest))
        {
            return Result(
                IrRegionEvaluationStatusV1.InvalidOptions,
                FallbackSchedule,
                FallbackBundles,
                Array.Empty<IrRegionCandidateV1>(),
                null,
                null,
                null,
                IrRegionFactDispositionV1.FallbackFactsUnchanged,
                options,
                [new("HCRG0005", "Profile identity must be a lowercase SHA-256 digest.")]);
        }

        IReadOnlyList<IrRegionCandidateV1> candidates = CreateCandidates(program, options.MaximumCandidates);
        if (options.Mode == IrRegionSchedulingModeV1.Disabled)
        {
            return Result(
                IrRegionEvaluationStatusV1.DisabledFallback,
                FallbackSchedule,
                FallbackBundles,
                candidates,
                null,
                null,
                null,
                IrRegionFactDispositionV1.FallbackFactsUnchanged,
                options,
                Array.Empty<IrRegionSchedulingDiagnosticV1>());
        }

        IrRegionCandidateV1? selected = candidates.FirstOrDefault(static candidate =>
            candidate.Status == IrRegionCandidateStatusV1.QualifiedStraightLineEbb);
        if (selected is null)
        {
            return Result(
                IrRegionEvaluationStatusV1.NoQualifiedCandidate,
                FallbackSchedule,
                FallbackBundles,
                candidates,
                null,
                null,
                null,
                IrRegionFactDispositionV1.FallbackFactsUnchanged,
                options,
                [new("HCRG0002", "No region candidate passed the conservative motion firewall.")]);
        }

        return EvaluateCandidate(program, selected, options, FallbackSchedule, FallbackBundles, candidates);
    }

    public IrRegionSchedulingResultV1 EvaluateCandidate(
        IrProgram program,
        IrRegionCandidateV1 candidate,
        HybridCpuRegionSchedulingOptionsV1 options)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(options);
        (IrProgramSchedule FallbackSchedule, IrProgramBundlingResult FallbackBundles) = ScheduleAndBundle(program);
        return EvaluateCandidate(program, candidate, options, FallbackSchedule, FallbackBundles, [candidate]);
    }

    public static IrRegionMemoryRelationV1 ClassifyMemoryRelation(
        IrCanonicalMemoryEffectV1 left,
        IrCanonicalMemoryEffectV1 right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Kind == IrMemoryEffectKind.None || right.Kind == IrMemoryEffectKind.None)
            return IrRegionMemoryRelationV1.NoAlias;
        if (left.Kind.HasFlag(IrMemoryEffectKind.Unknown) || right.Kind.HasFlag(IrMemoryEffectKind.Unknown) ||
            left.AddressSpace == IrAddressSpaceIdentity.Unknown || right.AddressSpace == IrAddressSpaceIdentity.Unknown)
            return IrRegionMemoryRelationV1.Unknown;
        if (left.AddressSpace != right.AddressSpace)
            return IrRegionMemoryRelationV1.NoAlias;
        if (!HasWrite(left.Kind) && !HasWrite(right.Kind))
            return IrRegionMemoryRelationV1.NoAlias;

        IReadOnlyList<IrMemoryRegion> leftRegions = Regions(left);
        IReadOnlyList<IrMemoryRegion> rightRegions = Regions(right);
        if (leftRegions.Count == 0 || rightRegions.Count == 0)
            return IrRegionMemoryRelationV1.MayAlias;
        foreach (IrMemoryRegion leftRegion in leftRegions)
        {
            foreach (IrMemoryRegion rightRegion in rightRegions)
            {
                if (leftRegion.Address == rightRegion.Address && leftRegion.Length == rightRegion.Length)
                    return IrRegionMemoryRelationV1.MustAlias;
                if (Overlaps(leftRegion, rightRegion))
                    return IrRegionMemoryRelationV1.MayAlias;
            }
        }
        return IrRegionMemoryRelationV1.NoAlias;
    }

    private static IrRegionSchedulingResultV1 EvaluateCandidate(
        IrProgram program,
        IrRegionCandidateV1 candidate,
        HybridCpuRegionSchedulingOptionsV1 options,
        IrProgramSchedule fallbackSchedule,
        IrProgramBundlingResult fallbackBundles,
        IReadOnlyList<IrRegionCandidateV1> candidates)
    {
        string currentShape = ComputeProgramShapeDigest(program);
        if (candidate.MutationStamp != program.Contract.DerivedFacts.ProgramMutation ||
            !string.Equals(candidate.ProgramShapeDigest, currentShape, StringComparison.Ordinal))
        {
            return Result(
                IrRegionEvaluationStatusV1.StaleWitness,
                fallbackSchedule,
                fallbackBundles,
                candidates,
                null,
                null,
                null,
                IrRegionFactDispositionV1.StaleFactsRejected,
                options,
                [new("HCRG0003", "Region witness does not match the current program mutation and CFG shape.")]);
        }
        if (options.Mode != IrRegionSchedulingModeV1.Shadow ||
            candidate.Status != IrRegionCandidateStatusV1.QualifiedStraightLineEbb)
        {
            return Result(
                IrRegionEvaluationStatusV1.NoQualifiedCandidate,
                fallbackSchedule,
                fallbackBundles,
                candidates,
                null,
                null,
                null,
                IrRegionFactDispositionV1.FallbackFactsUnchanged,
                options,
                [new("HCRG0002", "Candidate is not enabled and qualified for shadow scheduling.")]);
        }

        int[] allBlockIds = program.BasicBlocks.Select(static block => block.Id).OrderBy(static id => id).ToArray();
        if (!candidate.BlockIds.SequenceEqual(allBlockIds))
        {
            return Result(
                IrRegionEvaluationStatusV1.NoQualifiedCandidate,
                fallbackSchedule,
                fallbackBundles,
                candidates,
                null,
                null,
                null,
                IrRegionFactDispositionV1.FallbackFactsUnchanged,
                options,
                [new("HCRG0004", "Phase 12 shadow scheduling is limited to a whole two-block program.")]);
        }

        IrProgram mergedProgram = MergeWholeProgram(program, candidate.BlockIds);
        (IrProgramSchedule ShadowSchedule, IrProgramBundlingResult ShadowBundles) = ScheduleAndBundle(mergedProgram);
        return Result(
            IrRegionEvaluationStatusV1.ShadowQualified,
            fallbackSchedule,
            fallbackBundles,
            candidates,
            candidate,
            ShadowSchedule,
            ShadowBundles,
            IrRegionFactDispositionV1.DependencyLivenessPressureResourceAndPlacementRecomputedMiiInvalidated,
            options,
            Array.Empty<IrRegionSchedulingDiagnosticV1>());
    }

    private static IReadOnlyList<IrRegionCandidateV1> CreateCandidates(IrProgram program, int maximumCandidates)
    {
        IrProgramDependencyGraph dependencies = new HybridCpuProgramDependencyAnalyzer().AnalyzeProgram(program);
        Dictionary<int, IrBasicBlock> blocks = program.BasicBlocks.ToDictionary(static block => block.Id);
        string shapeDigest = ComputeProgramShapeDigest(program);
        return program.ControlFlowGraph.Edges
            .OrderBy(static edge => edge.SourceBlockId)
            .ThenBy(static edge => edge.TargetBlockId)
            .ThenBy(static edge => edge.Kind)
            .Take(maximumCandidates)
            .Select(edge => CreateCandidate(program, dependencies, blocks, edge, shapeDigest))
            .ToArray();
    }

    private static IrRegionCandidateV1 CreateCandidate(
        IrProgram program,
        IrProgramDependencyGraph dependencies,
        IReadOnlyDictionary<int, IrBasicBlock> blocks,
        IrControlFlowEdge edge,
        string shapeDigest)
    {
        IrBasicBlock source = blocks[edge.SourceBlockId];
        IrBasicBlock target = blocks[edge.TargetBlockId];
        IrInstruction[] instructions = [.. source.Instructions, .. target.Instructions];
        IrRegionCandidateStatusV1 status = ClassifyCandidate(edge, source, target, instructions);
        IrRegionMemoryWitnessV1[] memoryWitnesses = BuildMemoryWitnesses(source, target);
        if (status == IrRegionCandidateStatusV1.QualifiedStraightLineEbb &&
            memoryWitnesses.Any(static witness => witness.Relation != IrRegionMemoryRelationV1.NoAlias))
            status = IrRegionCandidateStatusV1.RejectedMemory;
        IrRegionControlWitnessV1[] controlWitnesses = instructions
            .Select(static instruction => new IrRegionControlWitnessV1(
                instruction.Index,
                instruction.PredicateMask,
                instruction.SideEffects.ArchitecturalEffects,
                ClassifyMotionSupport(instruction)))
            .ToArray();
        string regionId = Hash(string.Join('|',
            HybridCpuRegionSchedulingContractV1.SchemaId,
            shapeDigest,
            edge.SourceBlockId.ToString(CultureInfo.InvariantCulture),
            edge.TargetBlockId.ToString(CultureInfo.InvariantCulture),
            edge.Kind,
            string.Join(',', instructions.Select(static instruction => instruction.StableIdentity))));
        IrInstructionDependency[] regionDependencies = dependencies.BlockGraphs
            .Where(graph => graph.BlockId == source.Id || graph.BlockId == target.Id)
            .SelectMany(static graph => graph.Dependencies)
            .Concat(dependencies.GetEdgeDependencies(source.Id, target.Id).Select(static item => item.Dependency))
            .Distinct()
            .OrderBy(static dependency => dependency.ProducerInstructionIndex)
            .ThenBy(static dependency => dependency.ConsumerInstructionIndex)
            .ThenBy(static dependency => dependency.Kind)
            .ToArray();
        return new(
            HybridCpuRegionSchedulingContractV1.SchemaId,
            regionId,
            shapeDigest,
            program.Contract.DerivedFacts.ProgramMutation,
            [source.Id, target.Id],
            source.SuccessorBlockIds.Where(id => id != target.Id).OrderBy(static id => id).ToArray(),
            status,
            regionDependencies,
            memoryWitnesses,
            controlWitnesses,
            ["region.straight-line-fallthrough/v1"]);
    }

    private static IrRegionCandidateStatusV1 ClassifyCandidate(
        IrControlFlowEdge edge,
        IrBasicBlock source,
        IrBasicBlock target,
        IReadOnlyList<IrInstruction> instructions)
    {
        if (edge.Kind != IrControlFlowEdgeKind.Fallthrough)
            return IrRegionCandidateStatusV1.RejectedControlEdge;
        if (source.SuccessorBlockIds.Count != 1 || target.PredecessorBlockIds.Count != 1)
            return IrRegionCandidateStatusV1.RejectedSideExit;
        if (source.HasUnresolvedControlTransfer || target.HasUnresolvedControlTransfer)
            return IrRegionCandidateStatusV1.RejectedUnresolvedControl;
        if (instructions.Any(static instruction => instruction.PredicateMask != byte.MaxValue))
            return IrRegionCandidateStatusV1.RejectedPredicate;
        if (instructions.Any(static instruction => IsSpecialContour(instruction)))
            return IrRegionCandidateStatusV1.RejectedSpecialContour;
        if (instructions.Any(static instruction => ClassifyMotionSupport(instruction) != IrRegionMotionSupportV1.SupportedStraightLine))
            return IrRegionCandidateStatusV1.RejectedFaultOrArchitecturalEffect;
        return IrRegionCandidateStatusV1.QualifiedStraightLineEbb;
    }

    private static IrRegionMotionSupportV1 ClassifyMotionSupport(IrInstruction instruction)
    {
        if (instruction.Semantics.OperationMayFault)
            return IrRegionMotionSupportV1.Unsupported;
        return instruction.SideEffects.ArchitecturalEffects == IrArchitecturalEffectKind.None
            ? IrRegionMotionSupportV1.SupportedStraightLine
            : IrRegionMotionSupportV1.Unsupported;
    }

    private static IrRegionMemoryWitnessV1[] BuildMemoryWitnesses(IrBasicBlock source, IrBasicBlock target)
    {
        List<IrRegionMemoryWitnessV1> witnesses = new List<IrRegionMemoryWitnessV1>();
        foreach (IrInstruction producer in source.Instructions)
        {
            foreach (IrInstruction consumer in target.Instructions)
            {
                if (producer.SideEffects.Memory.Kind == IrMemoryEffectKind.None &&
                    consumer.SideEffects.Memory.Kind == IrMemoryEffectKind.None)
                    continue;
                witnesses.Add(new(
                    producer.Index,
                    consumer.Index,
                    ClassifyMemoryRelation(producer.SideEffects.Memory, consumer.SideEffects.Memory)));
            }
        }
        return witnesses.ToArray();
    }

    private static IrProgram MergeWholeProgram(IrProgram program, IReadOnlyList<int> blockIds)
    {
        Dictionary<int, IrBasicBlock> blocks = program.BasicBlocks.ToDictionary(static block => block.Id);
        IrBasicBlock first = blocks[blockIds[0]];
        IrBasicBlock last = blocks[blockIds[^1]];
        IrInstruction[] instructions = blockIds.SelectMany(id => blocks[id].Instructions).ToArray();
        IrBasicBlock merged = new IrBasicBlock(
            first.Id,
            first.StartInstructionIndex,
            last.EndInstructionIndex,
            first.StartAddress,
            last.EndAddress,
            HasUnresolvedControlTransfer: false,
            instructions,
            Array.Empty<int>(),
            Array.Empty<int>(),
            ExitBlock: true,
            BarrierBoundary: false,
            first.PrimaryLabel,
            first.LabelNames,
            first.SectionName,
            first.FunctionName,
            first.SourceSpan);
        return program with
        {
            Instructions = instructions,
            ControlFlowGraph = new([merged], Array.Empty<IrControlFlowEdge>()),
            Contract = program.Contract with
            {
                DerivedFacts = program.Contract.DerivedFacts.InvalidateAfterScheduleChangingMutation()
            }
        };
    }

    private static (IrProgramSchedule Schedule, IrProgramBundlingResult Bundles) ScheduleAndBundle(IrProgram program)
    {
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        return (schedule, new HybridCpuBundleFormer().BundleProgram(schedule));
    }

    private static IrRegionSchedulingResultV1 Result(
        IrRegionEvaluationStatusV1 status,
        IrProgramSchedule fallbackSchedule,
        IrProgramBundlingResult fallbackBundles,
        IReadOnlyList<IrRegionCandidateV1> candidates,
        IrRegionCandidateV1? selectedCandidate,
        IrProgramSchedule? shadowSchedule,
        IrProgramBundlingResult? shadowBundles,
        IrRegionFactDispositionV1 factDisposition,
        HybridCpuRegionSchedulingOptionsV1 options,
        IReadOnlyList<IrRegionSchedulingDiagnosticV1> diagnostics) => new(
            status,
            fallbackSchedule,
            fallbackBundles,
            candidates,
            selectedCandidate,
            shadowSchedule,
            shadowBundles,
            fallbackSchedule.BlockSchedules.Sum(static block => block.ScheduleLength),
            shadowSchedule?.BlockSchedules.Sum(static block => block.ScheduleLength) ?? 0,
            factDisposition,
            IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly,
            ComputeOptionsDigest(options),
            options.ProfileDigest ?? "absent",
            diagnostics);

    private static bool IsSpecialContour(IrInstruction instruction) =>
        instruction.Annotation.RequiredSlotClass is IrSlotClass.DmaStreamClass or
            IrSlotClass.SystemSingleton or IrSlotClass.MatrixTileStreamClass ||
        instruction.Annotation.Serialization != IrSerializationKind.None ||
        instruction.SideEffects.Memory.Kind.HasFlag(IrMemoryEffectKind.Atomic) ||
        instruction.SideEffects.Memory.Kind.HasFlag(IrMemoryEffectKind.Volatile) ||
        instruction.SideEffects.Memory.Kind.HasFlag(IrMemoryEffectKind.Fence);

    private static bool HasWrite(IrMemoryEffectKind kind) =>
        (kind & (IrMemoryEffectKind.Write | IrMemoryEffectKind.Atomic | IrMemoryEffectKind.Volatile |
            IrMemoryEffectKind.Fence)) != 0;

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IReadOnlyList<IrMemoryRegion> Regions(IrCanonicalMemoryEffectV1 effect) =>
        new[] { effect.ReadRegion, effect.WriteRegion }.Where(static region => region is not null).Cast<IrMemoryRegion>().ToArray();

    private static bool Overlaps(IrMemoryRegion left, IrMemoryRegion right)
    {
        ulong leftEnd;
        ulong rightEnd;
        try
        {
            leftEnd = checked(left.Address + left.Length);
            rightEnd = checked(right.Address + right.Length);
        }
        catch (OverflowException)
        {
            return true;
        }
        return left.Address < rightEnd && right.Address < leftEnd;
    }

    private static string ComputeProgramShapeDigest(IrProgram program) => Hash(string.Join('|',
        program.VirtualThreadId.ToString(CultureInfo.InvariantCulture),
        program.Contract.DerivedFacts.ProgramMutation.Value.ToString(CultureInfo.InvariantCulture),
        string.Join(';', program.BasicBlocks.OrderBy(static block => block.Id).Select(block => string.Join(':',
            block.Id,
            string.Join(',', block.Instructions.Select(InstructionKey)),
            string.Join(',', block.PredecessorBlockIds.OrderBy(static id => id)),
            string.Join(',', block.SuccessorBlockIds.OrderBy(static id => id))))),
        string.Join(';', program.ControlFlowGraph.Edges.OrderBy(static edge => edge.SourceBlockId)
            .ThenBy(static edge => edge.TargetBlockId).ThenBy(static edge => edge.Kind)
            .Select(static edge => $"{edge.SourceBlockId}:{edge.TargetBlockId}:{edge.Kind}"))));

    private static string ComputeOptionsDigest(HybridCpuRegionSchedulingOptionsV1 options) => Hash(string.Join('|',
        "hybridcpu.region-scheduling-options/v1",
        HybridCpuRegionSchedulingContractV1.Default.ContractDigest,
        options.Mode,
        options.MaximumCandidates.ToString(CultureInfo.InvariantCulture),
        options.ProfileDigest ?? "absent"));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string InstructionKey(IrInstruction instruction) => string.Join(':',
        instruction.Index,
        instruction.StableIdentity,
        instruction.Opcode,
        instruction.DataType,
        instruction.PredicateMask,
        instruction.Annotation.RequiredSlotClass,
        instruction.Annotation.StructurallyAllowedSlots,
        instruction.Annotation.MinimumLatencyCycles,
        instruction.Annotation.Serialization,
        instruction.Annotation.ControlFlowKind,
        instruction.Semantics.OperationMayFault,
        instruction.SideEffects.Memory.Kind,
        instruction.SideEffects.Memory.AddressSpace,
        instruction.SideEffects.Memory.Ordering,
        RegionKey(instruction.SideEffects.Memory.ReadRegion),
        RegionKey(instruction.SideEffects.Memory.WriteRegion),
        instruction.SideEffects.ArchitecturalEffects,
        string.Join(',', instruction.Annotation.Defs.Select(static operand => $"{operand.Kind}-{operand.Value}")),
        string.Join(',', instruction.Annotation.Uses.Select(static operand => $"{operand.Kind}-{operand.Value}")));

    private static string RegionKey(IrMemoryRegion? region) => region is null
        ? "none"
        : $"{region.Address.ToString(CultureInfo.InvariantCulture)}-{region.Length.ToString(CultureInfo.InvariantCulture)}-{region.IsWrite}";
}
