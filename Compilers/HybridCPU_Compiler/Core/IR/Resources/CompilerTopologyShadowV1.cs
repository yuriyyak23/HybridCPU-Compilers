using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Compiler.Core.IR.Resources;

public sealed record HybridCpuTopologyFeatureFlagsV1(
    bool EnforceRegisterGroups = false,
    bool EnforcePrfPorts = false,
    bool EnforceBanks = false,
    bool EnforceChannels = false,
    bool EnforceCertificateClasses = false,
    bool EnablePossibleOrUnknownCost = false)
{
    public static HybridCpuTopologyFeatureFlagsV1 DefaultOff { get; } = new();
}

public sealed record HybridCpuTopologyObservationFlagsV1(
    bool RegisterGroups = true,
    bool PrfPorts = true,
    bool Banks = true,
    bool Channels = true,
    bool CertificateClasses = true)
{
    public static HybridCpuTopologyObservationFlagsV1 All { get; } = new();
}

public enum HybridCpuTopologyReasonCodeV1 : byte
{
    None,
    StaleTopologyDigest,
    DuplicateInstruction,
    UnknownRegisterEvidence,
    RegisterRawConflict,
    RegisterWarConflict,
    RegisterWawConflict,
    PrfReadPortsExceeded,
    PrfWritePortsExceeded,
    ExactBankConflict,
    ExactChannelConflict,
    CertificateClassConflict
}

public enum CompilerTopologyShadowDecisionV1 : byte { Allowed, PredictedConflict, UnknownFallback }

public sealed class HybridCpuTopologyCycleStateV1
{
    private readonly IrTopologyResourceFootprintV1[] _reserved;

    private HybridCpuTopologyCycleStateV1(string topologyDigest, IrTopologyResourceFootprintV1[] reserved)
    {
        TopologyDigest = topologyDigest;
        _reserved = reserved;
        ReservedFootprints = Array.AsReadOnly(_reserved);
        ushort[] readMasks = new ushort[HybridCpuMachineTopologyV1.VirtualThreadCount];
        ushort[] writeMasks = new ushort[HybridCpuMachineTopologyV1.VirtualThreadCount];
        SortedSet<int> banks = new SortedSet<int>();
        SortedSet<int> channels = new SortedSet<int>();
        foreach (IrTopologyResourceFootprintV1 footprint in reserved)
        {
            if (footprint.VirtualThreadId < readMasks.Length)
            {
                readMasks[footprint.VirtualThreadId] |= footprint.RegisterReadGroupMask;
                writeMasks[footprint.VirtualThreadId] |= footprint.RegisterWriteGroupMask;
            }
            PrfReadPortsUsed += footprint.RequiredPrfReadPorts;
            PrfWritePortsUsed += footprint.RequiredPrfWritePorts;
            if (footprint.Banks.Precision is IrAddressEvidenceKindV1.Exact or IrAddressEvidenceKindV1.FiniteSet)
                foreach (int id in footprint.Banks.ResourceIds) banks.Add(id);
            if (footprint.Channels.Precision is IrAddressEvidenceKindV1.Exact or IrAddressEvidenceKindV1.FiniteSet)
                foreach (int id in footprint.Channels.ResourceIds) channels.Add(id);
            CertificateClassesUsed |= footprint.CertificateClass;
        }
        RegisterReadGroupMasks = Array.AsReadOnly(readMasks);
        RegisterWriteGroupMasks = Array.AsReadOnly(writeMasks);
        KnownBankIds = Array.AsReadOnly(banks.ToArray());
        KnownChannelIds = Array.AsReadOnly(channels.ToArray());
    }

    public string TopologyDigest { get; }
    public IReadOnlyList<IrTopologyResourceFootprintV1> ReservedFootprints { get; }
    public IReadOnlyList<ushort> RegisterReadGroupMasks { get; }
    public IReadOnlyList<ushort> RegisterWriteGroupMasks { get; }
    public int PrfReadPortsUsed { get; private set; }
    public int PrfWritePortsUsed { get; private set; }
    public IReadOnlyList<int> KnownBankIds { get; }
    public IReadOnlyList<int> KnownChannelIds { get; }
    public CompilerCertificateClassV1 CertificateClassesUsed { get; private set; }

    public static HybridCpuTopologyCycleStateV1 Empty(HybridCpuMachineTopologyV1 topology) =>
        new(topology.ContractDigest, Array.Empty<IrTopologyResourceFootprintV1>());

    internal HybridCpuTopologyCycleStateV1 Append(IrTopologyResourceFootprintV1 footprint)
    {
        IrTopologyResourceFootprintV1[] next = new IrTopologyResourceFootprintV1[_reserved.Length + 1];
        Array.Copy(_reserved, next, _reserved.Length);
        next[^1] = footprint;
        return new HybridCpuTopologyCycleStateV1(TopologyDigest, next);
    }
}

public sealed record HybridCpuTopologyReservationResultV1(
    CompilerTopologyShadowDecisionV1 Decision,
    HybridCpuTopologyReasonCodeV1 Reason,
    string ResourceFamily,
    int Requested,
    int Used,
    int? Capacity);

public readonly record struct HybridCpuTopologyIncrementalCostV1(
    int PossibleBankPressure,
    int PossibleChannelPressure,
    int UnknownFactCount,
    int InstructionIndex) : IComparable<HybridCpuTopologyIncrementalCostV1>
{
    public int CompareTo(HybridCpuTopologyIncrementalCostV1 other)
    {
        int result = PossibleBankPressure.CompareTo(other.PossibleBankPressure);
        if (result != 0) return result;
        result = PossibleChannelPressure.CompareTo(other.PossibleChannelPressure);
        if (result != 0) return result;
        result = UnknownFactCount.CompareTo(other.UnknownFactCount);
        return result != 0 ? result : InstructionIndex.CompareTo(other.InstructionIndex);
    }
}

public sealed class HybridCpuTopologyResourceModelV1
{
    public HybridCpuTopologyResourceModelV1(
        HybridCpuMachineTopologyV1? topology = null,
        HybridCpuTopologyObservationFlagsV1? observations = null)
    {
        Topology = topology ?? HybridCpuMachineTopologyV1.Default;
        Observations = observations ?? HybridCpuTopologyObservationFlagsV1.All;
    }

    public HybridCpuMachineTopologyV1 Topology { get; }
    public HybridCpuTopologyObservationFlagsV1 Observations { get; }

    public IrTopologyResourceFootprintV1 GetResourceFootprint(IrInstruction instruction) =>
        IrTopologyResourceFootprintBuilderV1.Build(instruction, Topology);

    public HybridCpuTopologyReservationResultV1 CanReserve(
        HybridCpuTopologyCycleStateV1 state,
        IrTopologyResourceFootprintV1 footprint)
    {
        if (!string.Equals(state.TopologyDigest, Topology.ContractDigest, StringComparison.Ordinal) ||
            !string.Equals(footprint.TopologyDigest, Topology.ContractDigest, StringComparison.Ordinal))
            return Result(CompilerTopologyShadowDecisionV1.UnknownFallback,
                HybridCpuTopologyReasonCodeV1.StaleTopologyDigest, "topology", 1, 0, null);
        if (state.ReservedFootprints.Any(existing => existing.InstructionIndex == footprint.InstructionIndex))
            return Result(CompilerTopologyShadowDecisionV1.PredictedConflict,
                HybridCpuTopologyReasonCodeV1.DuplicateInstruction, "instruction", 1, 1, 1);

        if (Observations.RegisterGroups && footprint.RegisterPrecision == IrResourceFactPrecisionV1.Unknown)
            return Result(CompilerTopologyShadowDecisionV1.UnknownFallback,
                HybridCpuTopologyReasonCodeV1.UnknownRegisterEvidence, "register-groups", 1, 0, null);

        if (Observations.RegisterGroups)
        {
            foreach (IrTopologyResourceFootprintV1 existing in state.ReservedFootprints.Where(
                         existing => existing.VirtualThreadId == footprint.VirtualThreadId))
            {
                if ((footprint.RegisterReadGroupMask & existing.RegisterWriteGroupMask) != 0)
                    return Result(CompilerTopologyShadowDecisionV1.PredictedConflict,
                        HybridCpuTopologyReasonCodeV1.RegisterRawConflict, "register-groups", 1, 1, 1);
                if ((footprint.RegisterWriteGroupMask & existing.RegisterReadGroupMask) != 0)
                    return Result(CompilerTopologyShadowDecisionV1.PredictedConflict,
                        HybridCpuTopologyReasonCodeV1.RegisterWarConflict, "register-groups", 1, 1, 1);
                if ((footprint.RegisterWriteGroupMask & existing.RegisterWriteGroupMask) != 0)
                    return Result(CompilerTopologyShadowDecisionV1.PredictedConflict,
                        HybridCpuTopologyReasonCodeV1.RegisterWawConflict, "register-groups", 1, 1, 1);
            }
        }

        if (Observations.PrfPorts && Topology.PrfReadPortCapacity is int readCapacity)
        {
            int used = state.ReservedFootprints.Sum(existing => existing.RequiredPrfReadPorts);
            if (used + footprint.RequiredPrfReadPorts > readCapacity)
                return Result(CompilerTopologyShadowDecisionV1.PredictedConflict,
                    HybridCpuTopologyReasonCodeV1.PrfReadPortsExceeded, "prf-read-ports",
                    footprint.RequiredPrfReadPorts, used, readCapacity);
        }
        if (Observations.PrfPorts && Topology.PrfWritePortCapacity is int writeCapacity)
        {
            int used = state.ReservedFootprints.Sum(existing => existing.RequiredPrfWritePorts);
            if (used + footprint.RequiredPrfWritePorts > writeCapacity)
                return Result(CompilerTopologyShadowDecisionV1.PredictedConflict,
                    HybridCpuTopologyReasonCodeV1.PrfWritePortsExceeded, "prf-write-ports",
                    footprint.RequiredPrfWritePorts, used, writeCapacity);
        }

        if (Observations.Banks && HasExactConflict(state.ReservedFootprints.Select(existing => existing.Banks), footprint.Banks))
            return Result(CompilerTopologyShadowDecisionV1.PredictedConflict,
                HybridCpuTopologyReasonCodeV1.ExactBankConflict, "banks", 1, 1, 1);
        if (Observations.Channels && HasExactConflict(state.ReservedFootprints.Select(existing => existing.Channels), footprint.Channels))
            return Result(CompilerTopologyShadowDecisionV1.PredictedConflict,
                HybridCpuTopologyReasonCodeV1.ExactChannelConflict, "channels", 1, 1, 1);
        if (Observations.CertificateClasses && footprint.CertificateClass != CompilerCertificateClassV1.None &&
            state.ReservedFootprints.Any(existing =>
                (existing.CertificateClass & footprint.CertificateClass) != CompilerCertificateClassV1.None))
            return Result(CompilerTopologyShadowDecisionV1.PredictedConflict,
                HybridCpuTopologyReasonCodeV1.CertificateClassConflict, "certificate-class", 1, 1, 1);

        return Result(CompilerTopologyShadowDecisionV1.Allowed,
            HybridCpuTopologyReasonCodeV1.None, "none", 1, 0, null);
    }

    public HybridCpuTopologyCycleStateV1 Reserve(
        HybridCpuTopologyCycleStateV1 state,
        IrTopologyResourceFootprintV1 footprint)
    {
        HybridCpuTopologyReservationResultV1 result = CanReserve(state, footprint);
        if (result.Decision != CompilerTopologyShadowDecisionV1.Allowed)
            throw new InvalidOperationException($"Cannot reserve topology footprint: {result.Decision}/{result.Reason}.");
        return state.Append(footprint);
    }

    public HybridCpuTopologyIncrementalCostV1 IncrementalCost(
        HybridCpuTopologyCycleStateV1 state,
        IrTopologyResourceFootprintV1 footprint)
    {
        int bankPressure = PossibleOverlapCount(state.ReservedFootprints.Select(existing => existing.Banks), footprint.Banks);
        int channelPressure = PossibleOverlapCount(state.ReservedFootprints.Select(existing => existing.Channels), footprint.Channels);
        int unknown = (footprint.RegisterPrecision == IrResourceFactPrecisionV1.Unknown ? 1 : 0) +
                      (footprint.Banks.Precision == IrAddressEvidenceKindV1.Unknown ? 1 : 0) +
                      (footprint.Channels.Precision == IrAddressEvidenceKindV1.Unknown ? 1 : 0) +
                      (Topology.PrfReadPortCapacity is null || Topology.PrfWritePortCapacity is null ? 1 : 0);
        return new(bankPressure, channelPressure, unknown, footprint.InstructionIndex);
    }

    private static bool HasExactConflict(
        IEnumerable<IrAddressResourceEvidenceV1> existingEvidence,
        IrAddressResourceEvidenceV1 candidate) =>
        candidate.Precision == IrAddressEvidenceKindV1.Exact && candidate.ResourceIds.Count == 1 &&
        existingEvidence.Any(existing => existing.Precision == IrAddressEvidenceKindV1.Exact &&
            existing.ResourceIds.Count == 1 && existing.ResourceIds[0] == candidate.ResourceIds[0]);

    private static int PossibleOverlapCount(
        IEnumerable<IrAddressResourceEvidenceV1> existingEvidence,
        IrAddressResourceEvidenceV1 candidate)
    {
        if (candidate.Precision is IrAddressEvidenceKindV1.Unknown or IrAddressEvidenceKindV1.All) return 1;
        HashSet<int> candidateIds = new HashSet<int>(candidate.ResourceIds);
        return existingEvidence.Count(existing =>
            existing.Precision is IrAddressEvidenceKindV1.Unknown or IrAddressEvidenceKindV1.All ||
            existing.ResourceIds.Any(candidateIds.Contains));
    }

    private static HybridCpuTopologyReservationResultV1 Result(
        CompilerTopologyShadowDecisionV1 decision, HybridCpuTopologyReasonCodeV1 reason,
        string family, int requested, int used, int? capacity) =>
        new(decision, reason, family, requested, used, capacity);
}

public sealed record CompilerTopologyCycleDecisionV1(
    int BlockId,
    int Cycle,
    IReadOnlyList<int> InstructionIndexes,
    CompilerTopologyShadowDecisionV1 Decision,
    HybridCpuTopologyReasonCodeV1 Reason,
    string ResourceFamily,
    IReadOnlyList<string> FootprintFingerprints);

public sealed record CompilerTopologyShadowReportV1(
    string Schema,
    string TopologyKey,
    int DecisionCount,
    int AllowedCount,
    int PredictedConflictCount,
    int UnknownFallbackCount,
    int TruncatedDecisionCount,
    string Fingerprint,
    IReadOnlyList<CompilerTopologyCycleDecisionV1> Decisions)
{
    public const string SchemaName = "CompilerTopologyShadowReportV1";
}

public static class CompilerTopologyShadowEvaluatorV1
{
    public const int MaximumCycleDecisions = 256;

    public static CompilerTopologyShadowReportV1 Evaluate(
        IrProgramSchedule schedule,
        HybridCpuTopologyResourceModelV1? model = null)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        model ??= new HybridCpuTopologyResourceModelV1();
        List<CompilerTopologyCycleDecisionV1> decisions = new List<CompilerTopologyCycleDecisionV1>();
        int attempted = 0;
        foreach (IrBasicBlockSchedule block in schedule.BlockSchedules.OrderBy(block => block.BlockId))
            foreach (IrScheduleCycleGroup group in block.CycleGroups.OrderBy(group => group.Cycle))
            {
                attempted++;
                if (decisions.Count >= MaximumCycleDecisions) continue;
                HybridCpuTopologyCycleStateV1 state = HybridCpuTopologyCycleStateV1.Empty(model.Topology);
                List<IrTopologyResourceFootprintV1> footprints = new List<IrTopologyResourceFootprintV1>();
                HybridCpuTopologyReservationResultV1 result = new(
                    CompilerTopologyShadowDecisionV1.Allowed, HybridCpuTopologyReasonCodeV1.None, "none", 1, 0, null);
                foreach (IrInstruction instruction in group.Instructions.OrderBy(instruction => instruction.Index))
                {
                    IrTopologyResourceFootprintV1 footprint = model.GetResourceFootprint(instruction);
                    footprints.Add(footprint);
                    result = model.CanReserve(state, footprint);
                    if (result.Decision != CompilerTopologyShadowDecisionV1.Allowed) break;
                    state = model.Reserve(state, footprint);
                }
                decisions.Add(new(block.BlockId, group.Cycle,
                    Array.AsReadOnly(group.Instructions.Select(instruction => instruction.Index).OrderBy(index => index).ToArray()),
                    result.Decision, result.Reason, result.ResourceFamily,
                    Array.AsReadOnly(footprints.Select(footprint => footprint.Fingerprint).ToArray())));
            }

        string fingerprint = ComputeFingerprint(model.Topology.Key, decisions, attempted);
        return new(CompilerTopologyShadowReportV1.SchemaName, model.Topology.Key, decisions.Count,
            decisions.Count(decision => decision.Decision == CompilerTopologyShadowDecisionV1.Allowed),
            decisions.Count(decision => decision.Decision == CompilerTopologyShadowDecisionV1.PredictedConflict),
            decisions.Count(decision => decision.Decision == CompilerTopologyShadowDecisionV1.UnknownFallback),
            attempted - decisions.Count, fingerprint, Array.AsReadOnly(decisions.ToArray()));
    }

    private static string ComputeFingerprint(
        string topologyKey, IReadOnlyList<CompilerTopologyCycleDecisionV1> decisions, int attempted)
    {
        StringBuilder builder = new StringBuilder(CompilerTopologyShadowReportV1.SchemaName)
            .Append('|').Append(topologyKey).Append('|').Append(attempted.ToString(CultureInfo.InvariantCulture));
        foreach (CompilerTopologyCycleDecisionV1 decision in decisions)
        {
            builder.Append('|').Append(decision.BlockId).Append(':').Append(decision.Cycle)
                .Append(':').Append((byte)decision.Decision).Append(':').Append((byte)decision.Reason);
            foreach (int index in decision.InstructionIndexes) builder.Append(':').Append(index);
            foreach (string footprint in decision.FootprintFingerprints) builder.Append(':').Append(footprint);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}

public sealed record HybridCpuCompilationTopologyShadowResultV1(
    HybridCpuCompiledProgram CompiledProgram,
    CompilerTopologyShadowReportV1 ShadowReport);
