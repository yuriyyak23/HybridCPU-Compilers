using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU.Compiler.Core.IR;

public enum IrVirtualValueClass : byte
{
    ScalarInteger = 0,
    ScalarFloatingPoint = 1,
    Pointer = 2,
    Predicate = 3,
    Aggregate = 4,
    LoweredVector = 5,
    Opaque = 6,
    ManagedObjectReference = 7,
    ManagedByRef = 8,
    ManagedInteriorReference = 9,
    Unknown = 255
}

public enum IrValueAccessKind : byte
{
    Use = 0,
    Def = 1,
    PhiEdgeUse = 2
}

public enum IrValueAnalysisStatus : byte
{
    Complete = 0,
    Unknown = 1,
    Unsupported = 2,
    BudgetExhausted = 3
}

public sealed record IrAllocationConstraintV1(
    int WidthBits,
    int AlignmentRegisters,
    bool RequiresPair,
    bool IsAllocatable,
    HybridCpuArchitecturalRegisterClass? ArchitecturalClass,
    HybridCpuSpecialStateClass? SpecialStateClass,
    int? FixedRegisterId,
    byte? VirtualThreadId,
    IReadOnlyList<string> LegalContours,
    byte? LegalLaneMask,
    IReadOnlyList<int> LegalRegisterIds,
    IReadOnlyList<int> LegalRegisterGroups,
    string TargetContractDigest,
    string? VerifiedVectorLoweringIdentity);

public sealed record IrVirtualValueV1(
    string StableId,
    IrCanonicalTypeV1 ValueKind,
    IrVirtualValueClass VirtualClass,
    IrAllocationConstraintV1 Allocation);

public sealed record IrValueAccessV1(
    string ValueId,
    int InstructionIndex,
    IrValueAccessKind Kind,
    int? PhiSourceBlockId = null,
    int? PhiTargetBlockId = null);

public sealed record IrValueFlowGraphV1(
    string SchemaId,
    int SchemaVersion,
    IReadOnlyList<IrVirtualValueV1> Values,
    IReadOnlyList<IrValueAccessV1> Accesses)
{
    public static IrValueFlowGraphV1 Empty { get; } = new(
        "hybridcpu.value-flow/v1", 1, Array.Empty<IrVirtualValueV1>(), Array.Empty<IrValueAccessV1>());
}

public sealed record IrBlockLivenessV1(int BlockId, IReadOnlyList<string> LiveIn, IReadOnlyList<string> LiveOut);

public sealed record IrLoopLivenessV1(
    int HeaderBlockId,
    IReadOnlyList<int> LatchBlockIds,
    IReadOnlyList<string> LiveIn,
    IReadOnlyList<string> LiveOut);

public sealed record IrValueIntervalV1(
    string ValueId,
    int StartInstructionIndex,
    int EndInstructionIndexExclusive,
    IReadOnlyList<int> LiveBlockIds);

public sealed record IrClassPressureV1(HybridCpuArchitecturalRegisterClass RegisterClass, int PeakLiveValues);

public sealed record IrRegisterGroupPressureV1(
    int RegisterGroup,
    int PeakLiveValues,
    bool IsPossibleRatherThanAssigned);

public sealed record IrBlockPressureV1(
    int BlockId,
    IReadOnlyList<IrClassPressureV1> Classes,
    IReadOnlyList<IrRegisterGroupPressureV1> RegisterGroups,
    int PeakPredictedReads,
    int PeakPredictedWrites,
    int PeakUnassignedAllocatableValues);

public sealed record IrValueAnalysisReportV1(
    string SchemaId,
    IrValueAnalysisStatus Status,
    string? Reason,
    IrMutationStamp MutationStamp,
    string TargetContractDigest,
    string ValueFlowDigest,
    IReadOnlyList<IrBlockLivenessV1> Blocks,
    IReadOnlyList<IrLoopLivenessV1> Loops,
    IReadOnlyList<IrValueIntervalV1> Intervals,
    IReadOnlyList<IrBlockPressureV1> Pressure);

public sealed record HybridCpuValueAnalysisBudgetsV1(
    int MaximumInstructions,
    int MaximumValues,
    int MaximumAccesses)
{
    // Allocation must obtain real liveness both before and after its bounded
    // insertion repair. A larger allocator input limit alone cannot provide it.
    public static HybridCpuValueAnalysisBudgetsV1 Production { get; } = new(
        HybridCpuRegisterAllocationBudgetsV1.Production.MaximumInstructions +
            HybridCpuRegisterAllocationBudgetsV1.Production.MaximumInsertedInstructions,
        HybridCpuRegisterAllocationBudgetsV1.Production.MaximumValues,
        131072);
}

public static class HybridCpuNativeValueFlowFactoryV1
{
    public static IrValueFlowGraphV1 Create(IrProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        SortedDictionary<string, IrVirtualValueV1> values = new SortedDictionary<string, IrVirtualValueV1>(StringComparer.Ordinal);
        List<IrValueAccessV1> accesses = new List<IrValueAccessV1>();
        HybridCpuTargetMachineContractV1 target = HybridCpuTargetMachineContractV1.Default;

        foreach (IrInstruction instruction in program.Instructions.OrderBy(static item => item.Index))
        {
            Add(instruction, instruction.Annotation.Defs, IrValueAccessKind.Def);
            Add(instruction, instruction.Annotation.Uses, IrValueAccessKind.Use);
        }

        return new IrValueFlowGraphV1(
            "hybridcpu.value-flow/v1",
            1,
            values.Values.ToArray(),
            accesses.OrderBy(static access => access.InstructionIndex)
                .ThenBy(static access => access.Kind)
                .ThenBy(static access => access.ValueId, StringComparer.Ordinal)
                .ToArray());

        void Add(IrInstruction instruction, IReadOnlyList<IrOperand> operands, IrValueAccessKind kind)
        {
            foreach (IrOperand operand in operands)
            {
                if (operand.Kind != IrOperandKind.ArchitecturalRegister ||
                    operand.Value >= HybridCpuTargetMachineContractV1.ArchitecturalRegisterCount)
                    continue;

                int registerId = checked((int)operand.Value);
                if (registerId == 0) continue;
                string valueId = $"native:vt{instruction.VirtualThreadId}:x{registerId}";
                if (!values.ContainsKey(valueId))
                {
                    HybridCpuArchitecturalRegisterV1 register = target.ArchitecturalRegisters[registerId];
                    values.Add(valueId, new IrVirtualValueV1(
                        valueId,
                        new IrCanonicalTypeV1(IrCanonicalValueKind.Integer, register.BitWidth, IsSigned: false),
                        IrVirtualValueClass.ScalarInteger,
                        new IrAllocationConstraintV1(
                            register.BitWidth,
                            1,
                            RequiresPair: false,
                            IsAllocatable: register.IsAllocatable,
                            register.RegisterClass,
                            SpecialStateClass: null,
                            FixedRegisterId: register.Id,
                            instruction.VirtualThreadId,
                            ["native-scalar"],
                            LegalLaneMask: null,
                            [register.Id],
                            [register.RegisterGroup],
                            target.ContractDigest,
                            VerifiedVectorLoweringIdentity: null)));
                }

                accesses.Add(new(valueId, instruction.Index, kind));
            }
        }
    }
}

public sealed class HybridCpuValueLivenessPressureAnalyzerV1
{
    private readonly HybridCpuValueAnalysisBudgetsV1 _budgets;
    private readonly HybridCpuTargetMachineContractV1 _target;

    public HybridCpuValueLivenessPressureAnalyzerV1(
        HybridCpuValueAnalysisBudgetsV1? budgets = null,
        HybridCpuTargetMachineContractV1? target = null)
    {
        _budgets = budgets ?? HybridCpuValueAnalysisBudgetsV1.Production;
        _target = target ?? HybridCpuTargetMachineContractV1.Default;
    }

    public IrValueAnalysisReportV1 Analyze(
        IrProgram program,
        IReadOnlyList<IrBasicBlockSchedule>? schedules = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        IrValueFlowGraphV1 flow = program.ValueFlow;
        string digest = Digest(flow);
        IrMutationStamp stamp = program.Contract.DerivedFacts.ProgramMutation;

        if (program.Instructions.Count > _budgets.MaximumInstructions ||
            flow.Values.Count > _budgets.MaximumValues ||
            flow.Accesses.Count > _budgets.MaximumAccesses)
            return Failure(IrValueAnalysisStatus.BudgetExhausted, "Deterministic value-analysis budget exceeded.", stamp, digest);

        if (!Validate(program, flow, out IrValueAnalysisStatus validationStatus, out string? reason))
            return Failure(validationStatus, reason!, stamp, digest);

        Dictionary<int, IrBasicBlock> blocks = program.BasicBlocks.ToDictionary(static block => block.Id);
        Dictionary<int, int> instructionBlocks = program.BasicBlocks
            .SelectMany(static block => block.Instructions.Select(instruction => (instruction.Index, block.Id)))
            .ToDictionary(static item => item.Index, static item => item.Id);
        Dictionary<string, IrVirtualValueV1> values = flow.Values.ToDictionary(static value => value.StableId, StringComparer.Ordinal);
        Dictionary<int, HashSet<string>> uses = blocks.Keys.ToDictionary(static id => id, static _ => new HashSet<string>(StringComparer.Ordinal));
        Dictionary<int, HashSet<string>> defs = blocks.Keys.ToDictionary(static id => id, static _ => new HashSet<string>(StringComparer.Ordinal));
        Dictionary<(int Source, int Target), HashSet<string>> edgeUses = new Dictionary<(int Source, int Target), HashSet<string>>();

        foreach (IrValueAccessV1 access in flow.Accesses.OrderBy(static item => item.InstructionIndex).ThenBy(static item => item.Kind))
        {
            if (access.Kind == IrValueAccessKind.PhiEdgeUse)
            {
                (int, int) key = (access.PhiSourceBlockId!.Value, access.PhiTargetBlockId!.Value);
                if (!edgeUses.TryGetValue(key, out HashSet<string>? edge))
                {
                    edge = new(StringComparer.Ordinal);
                    edgeUses.Add(key, edge);
                }
                edge.Add(access.ValueId);
                continue;
            }

            int blockId = instructionBlocks[access.InstructionIndex];
            if (access.Kind == IrValueAccessKind.Use && !defs[blockId].Contains(access.ValueId))
                uses[blockId].Add(access.ValueId);
            else if (access.Kind == IrValueAccessKind.Def)
                defs[blockId].Add(access.ValueId);
        }

        Dictionary<int, HashSet<string>> liveIn = blocks.Keys.ToDictionary(static id => id, static _ => new HashSet<string>(StringComparer.Ordinal));
        Dictionary<int, HashSet<string>> liveOut = blocks.Keys.ToDictionary(static id => id, static _ => new HashSet<string>(StringComparer.Ordinal));
        int maximumIterations = checked(Math.Max(1, blocks.Count * (flow.Values.Count + 1) + 1));
        bool changed = true;
        for (int iteration = 0; iteration < maximumIterations && changed; iteration++)
        {
            changed = false;
            foreach (IrBasicBlock block in blocks.Values.OrderByDescending(static item => item.Id))
            {
                HashSet<string> nextOut = new HashSet<string>(StringComparer.Ordinal);
                foreach (int successor in block.SuccessorBlockIds.Order())
                {
                    nextOut.UnionWith(liveIn[successor]);
                    if (edgeUses.TryGetValue((block.Id, successor), out HashSet<string>? edge)) nextOut.UnionWith(edge);
                }
                HashSet<string> nextIn = new HashSet<string>(nextOut, StringComparer.Ordinal);
                nextIn.ExceptWith(defs[block.Id]);
                nextIn.UnionWith(uses[block.Id]);
                if (!nextOut.SetEquals(liveOut[block.Id])) { liveOut[block.Id] = nextOut; changed = true; }
                if (!nextIn.SetEquals(liveIn[block.Id])) { liveIn[block.Id] = nextIn; changed = true; }
            }
        }
        if (changed) return Failure(IrValueAnalysisStatus.Unknown, "Liveness fixed point did not converge within its deterministic bound.", stamp, digest);

        IrBlockLivenessV1[] blockReports = blocks.Keys.Order()
            .Select(id => new IrBlockLivenessV1(id, Sorted(liveIn[id]), Sorted(liveOut[id])))
            .ToArray();
        IrValueIntervalV1[] intervals = BuildIntervals(flow, blocks, instructionBlocks, liveIn, liveOut);
        IrLoopLivenessV1[] loops = BuildLoops(program, blocks, liveIn, liveOut);
        IrBlockPressureV1[] pressure = BuildPressure(flow, blocks, values, intervals, schedules);
        return new(
            "hybridcpu.value-analysis/v1",
            IrValueAnalysisStatus.Complete,
            null,
            stamp,
            _target.ContractDigest,
            digest,
            blockReports,
            loops,
            intervals,
            pressure);
    }

    private bool Validate(
        IrProgram program,
        IrValueFlowGraphV1 flow,
        out IrValueAnalysisStatus status,
        out string? reason)
    {
        HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (IrVirtualValueV1 value in flow.Values.OrderBy(static value => value.StableId, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(value.StableId) || !ids.Add(value.StableId))
                return Invalid(IrValueAnalysisStatus.Unknown, "Value identities must be non-empty and unique.", out status, out reason);
            IrAllocationConstraintV1 allocation = value.Allocation;
            if (value.ValueKind.Kind == IrCanonicalValueKind.Unknown ||
                value.VirtualClass == IrVirtualValueClass.Unknown ||
                allocation.WidthBits <= 0 ||
                allocation.AlignmentRegisters <= 0 ||
                (allocation.RequiresPair && allocation.AlignmentRegisters < 2) ||
                (allocation.VirtualThreadId is byte virtualThreadId &&
                 virtualThreadId >= HybridCpuMachineTopologyV1.VirtualThreadCount) ||
                allocation.LegalLaneMask == 0 ||
                allocation.LegalContours.Any(string.IsNullOrWhiteSpace))
                return Invalid(IrValueAnalysisStatus.Unknown, "Value kind or allocation constraint is incomplete.", out status, out reason);
            if (allocation.SpecialStateClass.HasValue && allocation.IsAllocatable)
                return Invalid(IrValueAnalysisStatus.Unsupported, "Special architectural state is not allocator-owned storage.", out status, out reason);
            if (allocation.SpecialStateClass.HasValue &&
                !_target.SpecialState.Any(state => state.StateClass == allocation.SpecialStateClass.Value))
                return Invalid(IrValueAnalysisStatus.Unknown, "Special state class is absent from the target contract.", out status, out reason);
            if (allocation.IsAllocatable)
            {
                if (!allocation.ArchitecturalClass.HasValue ||
                    !string.Equals(allocation.TargetContractDigest, _target.ContractDigest, StringComparison.Ordinal) ||
                    !_target.TryGetScalarLayout(allocation.WidthBits, out _))
                    return Invalid(IrValueAnalysisStatus.Unknown, "Allocatable value lacks a current target class, width or target digest.", out status, out reason);
                if (value.ValueKind.Kind == IrCanonicalValueKind.Vector &&
                    string.IsNullOrWhiteSpace(allocation.VerifiedVectorLoweringIdentity))
                    return Invalid(IrValueAnalysisStatus.Unsupported, "Vector-like value has no verified lowering to an architectural allocation class.", out status, out reason);
                if (allocation.LegalRegisterIds.Count == 0 ||
                    allocation.LegalRegisterGroups.Count == 0 ||
                    allocation.LegalRegisterIds.Any(registerId =>
                        !_target.TryGetArchitecturalRegister(registerId, out HybridCpuArchitecturalRegisterV1 register) ||
                        !register.IsAllocatable || register.RegisterClass != allocation.ArchitecturalClass.Value) ||
                    allocation.LegalRegisterGroups.Any(group =>
                        group < 0 || group >= HybridCpuMachineTopologyV1.RegisterGroupCount) ||
                    allocation.LegalRegisterIds.Any(registerId =>
                        !allocation.LegalRegisterGroups.Contains(_target.ArchitecturalRegisters[registerId].RegisterGroup)))
                    return Invalid(IrValueAnalysisStatus.Unknown, "Legal register or group set is not traceable to the target contract.", out status, out reason);
            }
            if (allocation.FixedRegisterId.HasValue &&
                (!_target.TryGetArchitecturalRegister(allocation.FixedRegisterId.Value, out _) ||
                 (allocation.IsAllocatable && !allocation.LegalRegisterIds.Contains(allocation.FixedRegisterId.Value))))
                return Invalid(IrValueAnalysisStatus.Unknown, "Fixed register is outside the target namespace.", out status, out reason);
        }

        HashSet<int> instructionIds = program.Instructions.Select(static instruction => instruction.Index).ToHashSet();
        HashSet<(int, int)> edges = program.ControlFlowGraph.Edges
            .Select(static edge => (edge.SourceBlockId, edge.TargetBlockId)).ToHashSet();
        foreach (IrValueAccessV1 access in flow.Accesses)
        {
            if (!ids.Contains(access.ValueId) || !instructionIds.Contains(access.InstructionIndex))
                return Invalid(IrValueAnalysisStatus.Unknown, "Value access references an unknown value or instruction.", out status, out reason);
            if (access.Kind == IrValueAccessKind.PhiEdgeUse &&
                (!access.PhiSourceBlockId.HasValue || !access.PhiTargetBlockId.HasValue ||
                 !edges.Contains((access.PhiSourceBlockId.Value, access.PhiTargetBlockId.Value)) ||
                 !program.BasicBlocks.Any(block =>
                     block.Id == access.PhiTargetBlockId.Value &&
                     block.Instructions.Any(instruction => instruction.Index == access.InstructionIndex))))
                return Invalid(IrValueAnalysisStatus.Unknown, "PHI use does not identify an existing CFG edge.", out status, out reason);
        }

        status = IrValueAnalysisStatus.Complete;
        reason = null;
        return true;
    }

    private static bool Invalid(IrValueAnalysisStatus value, string message, out IrValueAnalysisStatus status, out string? reason)
    {
        status = value;
        reason = message;
        return false;
    }

    private IrValueAnalysisReportV1 Failure(IrValueAnalysisStatus status, string reason, IrMutationStamp stamp, string digest) =>
        new("hybridcpu.value-analysis/v1", status, reason, stamp, _target.ContractDigest, digest,
            Array.Empty<IrBlockLivenessV1>(), Array.Empty<IrLoopLivenessV1>(),
            Array.Empty<IrValueIntervalV1>(), Array.Empty<IrBlockPressureV1>());

    private static IrValueIntervalV1[] BuildIntervals(
        IrValueFlowGraphV1 flow,
        IReadOnlyDictionary<int, IrBasicBlock> blocks,
        IReadOnlyDictionary<int, int> instructionBlocks,
        IReadOnlyDictionary<int, HashSet<string>> liveIn,
        IReadOnlyDictionary<int, HashSet<string>> liveOut)
    {
        Dictionary<string, List<int>> positions = flow.Values.ToDictionary(static value => value.StableId, static _ => new List<int>(), StringComparer.Ordinal);
        foreach (IrValueAccessV1 access in flow.Accesses)
        {
            int position = access.Kind == IrValueAccessKind.PhiEdgeUse
                ? blocks[access.PhiSourceBlockId!.Value].EndInstructionIndex
                : access.InstructionIndex;
            positions[access.ValueId].Add(position);
        }
        List<IrValueIntervalV1> result = new List<IrValueIntervalV1>();
        foreach (IrVirtualValueV1 value in flow.Values.OrderBy(static item => item.StableId, StringComparer.Ordinal))
        {
            List<int> valuePositions = positions[value.StableId];
            SortedSet<int> liveBlocks = new SortedSet<int>();
            foreach (int blockId in blocks.Keys)
            {
                if (liveIn[blockId].Contains(value.StableId))
                {
                    valuePositions.Add(blocks[blockId].StartInstructionIndex);
                    liveBlocks.Add(blockId);
                }
                if (liveOut[blockId].Contains(value.StableId))
                {
                    valuePositions.Add(blocks[blockId].EndInstructionIndex);
                    liveBlocks.Add(blockId);
                }
            }
            foreach (IrValueAccessV1 access in flow.Accesses.Where(access => access.ValueId == value.StableId))
                if (access.Kind != IrValueAccessKind.PhiEdgeUse) liveBlocks.Add(instructionBlocks[access.InstructionIndex]);
            if (valuePositions.Count == 0) continue;
            result.Add(new(value.StableId, valuePositions.Min(), checked(valuePositions.Max() + 1), liveBlocks.ToArray()));
        }
        return result.ToArray();
    }

    private static IrLoopLivenessV1[] BuildLoops(
        IrProgram program,
        IReadOnlyDictionary<int, IrBasicBlock> blocks,
        IReadOnlyDictionary<int, HashSet<string>> liveIn,
        IReadOnlyDictionary<int, HashSet<string>> liveOut)
    {
        Dictionary<int, HashSet<int>> dominators = ComputeDominators(blocks);
        return program.ControlFlowGraph.Edges
            .Where(edge => dominators[edge.SourceBlockId].Contains(edge.TargetBlockId))
            .GroupBy(static edge => edge.TargetBlockId)
            .OrderBy(static group => group.Key)
            .Select(group => new IrLoopLivenessV1(
                group.Key,
                group.Select(static edge => edge.SourceBlockId).Distinct().Order().ToArray(),
                Sorted(liveIn[group.Key]),
                Sorted(liveOut[group.Key])))
            .ToArray();
    }

    private static Dictionary<int, HashSet<int>> ComputeDominators(IReadOnlyDictionary<int, IrBasicBlock> blocks)
    {
        int[] all = blocks.Keys.Order().ToArray();
        Dictionary<int, HashSet<int>> result = new Dictionary<int, HashSet<int>>();
        foreach (IrBasicBlock block in blocks.Values)
            result[block.Id] = block.PredecessorBlockIds.Count == 0 ? [block.Id] : new HashSet<int>(all);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (IrBasicBlock block in blocks.Values.OrderBy(static item => item.Id))
            {
                if (block.PredecessorBlockIds.Count == 0) continue;
                HashSet<int> next = new HashSet<int>(result[block.PredecessorBlockIds[0]]);
                foreach (int predecessor in block.PredecessorBlockIds.Skip(1)) next.IntersectWith(result[predecessor]);
                next.Add(block.Id);
                if (!next.SetEquals(result[block.Id])) { result[block.Id] = next; changed = true; }
            }
        }
        return result;
    }

    private static IrBlockPressureV1[] BuildPressure(
        IrValueFlowGraphV1 flow,
        IReadOnlyDictionary<int, IrBasicBlock> blocks,
        IReadOnlyDictionary<string, IrVirtualValueV1> values,
        IReadOnlyList<IrValueIntervalV1> intervals,
        IReadOnlyList<IrBasicBlockSchedule>? schedules)
    {
        Dictionary<int, int> steps = schedules?
            .SelectMany(static schedule => schedule.ScheduledInstructions.Select(item => (item.InstructionIndex, item.Cycle)))
            .ToDictionary(static item => item.InstructionIndex, static item => item.Cycle) ?? [];
        List<IrBlockPressureV1> result = new List<IrBlockPressureV1>();
        foreach (IrBasicBlock block in blocks.Values.OrderBy(static item => item.Id))
        {
            Dictionary<HybridCpuArchitecturalRegisterClass, int> classPeaks = new Dictionary<HybridCpuArchitecturalRegisterClass, int>();
            Dictionary<int, (int Peak, bool Possible)> groupPeaks = new Dictionary<int, (int Peak, bool Possible)>();
            int unassignedPeak = 0;
            foreach (int position in Enumerable.Range(block.StartInstructionIndex, block.EndInstructionIndex - block.StartInstructionIndex + 1))
            {
                IrVirtualValueV1[] live = intervals
                    .Where(interval => interval.LiveBlockIds.Contains(block.Id))
                    .Where(interval => interval.StartInstructionIndex <= position && position < interval.EndInstructionIndexExclusive)
                    .Select(interval => values[interval.ValueId])
                    .Where(static value => value.Allocation.IsAllocatable)
                    .ToArray();
                foreach (IGrouping<HybridCpuArchitecturalRegisterClass, IrVirtualValueV1> group in live.GroupBy(static value => value.Allocation.ArchitecturalClass!.Value))
                    classPeaks[group.Key] = Math.Max(classPeaks.GetValueOrDefault(group.Key), group.Count());
                unassignedPeak = Math.Max(unassignedPeak, live.Count(static value => !value.Allocation.FixedRegisterId.HasValue));
                foreach (IrVirtualValueV1 value in live)
                {
                    bool possible = !value.Allocation.FixedRegisterId.HasValue || value.Allocation.LegalRegisterGroups.Count != 1;
                    foreach (int registerGroup in value.Allocation.LegalRegisterGroups.Distinct().Order())
                    {
                        int count = live.Count(candidate => candidate.Allocation.LegalRegisterGroups.Contains(registerGroup));
                        (int Peak, bool Possible) current = groupPeaks.GetValueOrDefault(registerGroup);
                        groupPeaks[registerGroup] = (Math.Max(current.Peak, count), current.Possible || possible);
                    }
                }
            }

            int peakReads = 0;
            int peakWrites = 0;
            foreach (IGrouping<int, IrValueAccessV1> group in flow.Accesses
                         .Where(access => access.Kind == IrValueAccessKind.PhiEdgeUse
                             ? access.PhiSourceBlockId == block.Id
                             : block.Instructions.Any(instruction => instruction.Index == access.InstructionIndex))
                         .Where(access => values[access.ValueId].Allocation.IsAllocatable)
                         .GroupBy(access => access.Kind == IrValueAccessKind.PhiEdgeUse
                             ? block.EndInstructionIndex
                             : steps.GetValueOrDefault(access.InstructionIndex, access.InstructionIndex)))
            {
                peakReads = Math.Max(peakReads, group.Count(static access => access.Kind != IrValueAccessKind.Def));
                peakWrites = Math.Max(peakWrites, group.Count(static access => access.Kind == IrValueAccessKind.Def));
            }
            result.Add(new(
                block.Id,
                classPeaks.OrderBy(static item => item.Key).Select(static item => new IrClassPressureV1(item.Key, item.Value)).ToArray(),
                groupPeaks.OrderBy(static item => item.Key).Select(static item => new IrRegisterGroupPressureV1(item.Key, item.Value.Peak, item.Value.Possible)).ToArray(),
                peakReads,
                peakWrites,
                unassignedPeak));
        }
        return result.ToArray();
    }

    private static IReadOnlyList<string> Sorted(IEnumerable<string> values) =>
        values.OrderBy(static value => value, StringComparer.Ordinal).ToArray();

    private static string Digest(IrValueFlowGraphV1 flow)
    {
        StringBuilder builder = new StringBuilder().Append(flow.SchemaId).Append('|').Append(flow.SchemaVersion);
        foreach (IrVirtualValueV1 value in flow.Values.OrderBy(static item => item.StableId, StringComparer.Ordinal))
            builder.Append("|v:").Append(value.StableId).Append(':').Append(value.ValueKind.Kind).Append(':')
                .Append(value.ValueKind.BitWidth).Append(':').Append(value.ValueKind.IsSigned).Append(':')
                .Append(value.ValueKind.LaneCount).Append(':').Append(value.VirtualClass).Append(':')
                .Append(value.Allocation.WidthBits).Append(':').Append(value.Allocation.AlignmentRegisters).Append(':')
                .Append(value.Allocation.RequiresPair).Append(':').Append(value.Allocation.IsAllocatable).Append(':')
                .Append(value.Allocation.ArchitecturalClass).Append(':').Append(value.Allocation.SpecialStateClass).Append(':')
                .Append(value.Allocation.FixedRegisterId).Append(':').Append(value.Allocation.VirtualThreadId).Append(':')
                .Append(value.Allocation.LegalLaneMask).Append(':').Append(value.Allocation.TargetContractDigest).Append(':')
                .Append(value.Allocation.VerifiedVectorLoweringIdentity).Append(':')
                .AppendJoin(',', value.Allocation.LegalContours.OrderBy(static item => item, StringComparer.Ordinal)).Append(':')
                .AppendJoin(',', value.Allocation.LegalRegisterIds.Order()).Append(':')
                .AppendJoin(',', value.Allocation.LegalRegisterGroups.Order());
        foreach (IrValueAccessV1 access in flow.Accesses.OrderBy(static item => item.InstructionIndex)
                     .ThenBy(static item => item.Kind).ThenBy(static item => item.ValueId, StringComparer.Ordinal)
                     .ThenBy(static item => item.PhiSourceBlockId).ThenBy(static item => item.PhiTargetBlockId))
            builder.Append("|a:").Append(access.ValueId).Append(':').Append(access.InstructionIndex).Append(':')
                .Append(access.Kind).Append(':').Append(access.PhiSourceBlockId).Append(':').Append(access.PhiTargetBlockId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
