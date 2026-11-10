using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR.Resources;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Versioned deterministic limits for Phase 02 cycle-membership search.
/// No elapsed-time observation participates in these limits.
/// </summary>
public sealed record HybridCpuCycleSearchOptionsV1(
    int TopK,
    int MaximumDepth,
    int MaximumEvaluatedStates,
    int BeamWidth)
{
    public const string SchemaName = "HybridCpuCycleSearchOptionsV1";

    public static HybridCpuCycleSearchOptionsV1 Default { get; } = new(12, 8, 4096, 64);

    public string Fingerprint => ComputeFingerprint();

    internal void Validate()
    {
        if (TopK is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(TopK));
        if (MaximumDepth is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(MaximumDepth));
        if (MaximumEvaluatedStates is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(MaximumEvaluatedStates));
        if (BeamWidth is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(BeamWidth));
    }

    private string ComputeFingerprint()
    {
        string payload = string.Join('|', SchemaName, TopK, MaximumDepth, MaximumEvaluatedStates, BeamWidth);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}

/// <summary>
/// One stable, already-ready candidate. Priority facts are static compiler scheduling evidence.
/// </summary>
public sealed record IrCycleMembershipCandidate(
    int InstructionIndex,
    IrInstruction Instruction,
    int CriticalPathLengthCycles,
    int TransitiveSuccessorCount);

public sealed class IrCycleSearchRequest
{
    public IrCycleSearchRequest(
        IReadOnlyList<IrCycleMembershipCandidate> orderedReadyCandidates,
        HybridCpuCycleSearchOptionsV1 options,
        string machineDescriptionKey)
    {
        ArgumentNullException.ThrowIfNull(orderedReadyCandidates);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineDescriptionKey);
        options.Validate();

        OrderedReadyCandidates = Array.AsReadOnly(orderedReadyCandidates.ToArray());
        Options = options;
        MachineDescriptionKey = machineDescriptionKey;
    }

    public IReadOnlyList<IrCycleMembershipCandidate> OrderedReadyCandidates { get; }
    public HybridCpuCycleSearchOptionsV1 Options { get; }
    public string MachineDescriptionKey { get; }
}

/// <summary>
/// Exact compiler placement witness. It is structural evidence, never runtime lane permission.
/// </summary>
public sealed class IrCyclePlacementWitness
{
    public IrCyclePlacementWitness(
        string machineDescriptionKey,
        string optionsFingerprint,
        IReadOnlyList<int> instructionIndexes,
        IReadOnlyList<int> instructionSlots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineDescriptionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(optionsFingerprint);
        ArgumentNullException.ThrowIfNull(instructionIndexes);
        ArgumentNullException.ThrowIfNull(instructionSlots);
        if (instructionIndexes.Count != instructionSlots.Count)
        {
            throw new ArgumentException("Witness member and slot counts must match.", nameof(instructionSlots));
        }

        MachineDescriptionKey = machineDescriptionKey;
        OptionsFingerprint = optionsFingerprint;
        InstructionIndexes = Array.AsReadOnly(instructionIndexes.ToArray());
        InstructionSlots = Array.AsReadOnly(instructionSlots.ToArray());
        Fingerprint = ComputeFingerprint();
    }

    public string MachineDescriptionKey { get; }
    public string OptionsFingerprint { get; }
    public IReadOnlyList<int> InstructionIndexes { get; }
    public IReadOnlyList<int> InstructionSlots { get; }
    public string Fingerprint { get; }

    private string ComputeFingerprint()
    {
        StringBuilder payload = new StringBuilder(MachineDescriptionKey).Append('|').Append(OptionsFingerprint);
        for (int index = 0; index < InstructionIndexes.Count; index++)
        {
            payload.Append('|').Append(InstructionIndexes[index].ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(InstructionSlots[index].ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())))
            .ToLowerInvariant();
    }
}

public sealed record IrCycleSearchSummaryV1(
    int ReadyWindowSize,
    bool WindowTruncated,
    int StatesEvaluated,
    int ResourcePruned,
    int StructuralPruned,
    int BeamPruned,
    int ExactPlacementsEvaluated,
    bool StateCapHit,
    bool ExistingPathFallbackRequired,
    string? FallbackReason,
    string OptionsFingerprint);

public sealed class IrCycleSearchResult
{
    public IrCycleSearchResult(
        IReadOnlyList<IrCycleMembershipCandidate> chosenMembers,
        IrCyclePlacementWitness? placementWitness,
        IrCycleSearchSummaryV1 summary)
    {
        ChosenMembers = Array.AsReadOnly(chosenMembers.ToArray());
        PlacementWitness = placementWitness;
        Summary = summary;
    }

    public IReadOnlyList<IrCycleMembershipCandidate> ChosenMembers { get; }
    public IrCyclePlacementWitness? PlacementWitness { get; }
    public IrCycleSearchSummaryV1 Summary { get; }
}

public interface IHybridCpuCyclePlacementBackend
{
    IrBundlePlacementSearchResult SearchStructuralAssignments(IReadOnlyList<IrIssueSlotMask> structurallyAllowedSlots);
}

public sealed class HybridCpuExistingPlacementBackendV1 : IHybridCpuCyclePlacementBackend
{
    public static HybridCpuExistingPlacementBackendV1 Instance { get; } = new();

    private HybridCpuExistingPlacementBackendV1()
    {
    }

    public IrBundlePlacementSearchResult SearchStructuralAssignments(
        IReadOnlyList<IrIssueSlotMask> structurallyAllowedSlots) =>
        HybridCpuSlotModel.SearchStructuralAssignments(structurallyAllowedSlots);
}

/// <summary>
/// Counter-bounded comparison of complete ready subsets. The existing checker and exact slot
/// search remain the structural backends; this class contains no slot-assignment enumerator.
/// </summary>
public sealed class HybridCpuCycleGroupSearch
{
    private readonly HybridCpuInstructionLegalityChecker _legalityChecker;
    private readonly IHybridCpuMachineResourceModel _resourceModel;
    private readonly IHybridCpuCyclePlacementBackend _placementBackend;

    public HybridCpuCycleGroupSearch(
        HybridCpuInstructionLegalityChecker? legalityChecker = null,
        IHybridCpuMachineResourceModel? resourceModel = null,
        IHybridCpuCyclePlacementBackend? placementBackend = null)
    {
        _legalityChecker = legalityChecker ?? new HybridCpuInstructionLegalityChecker();
        _resourceModel = resourceModel ?? HybridCpuMachineResourceModelV1.Default;
        _placementBackend = placementBackend ?? HybridCpuExistingPlacementBackendV1.Instance;
    }

    public IrCycleSearchResult Search(IrCycleSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        HybridCpuCycleSearchOptionsV1 options = request.Options;
        int readyCount = request.OrderedReadyCandidates.Count;
        int windowSize = Math.Min(readyCount, options.TopK);
        bool windowTruncated = readyCount > windowSize;

        if (!string.Equals(request.MachineDescriptionKey, _resourceModel.Description.Key, StringComparison.Ordinal))
        {
            return Fallback("MachineDescriptionMismatch", readyCount, windowTruncated, options);
        }

        if (windowSize == 0)
        {
            return Fallback("EmptyReadyWindow", 0, false, options);
        }

        HashSet<int> identity = new HashSet<int>();
        for (int index = 0; index < windowSize; index++)
        {
            IrCycleMembershipCandidate candidate = request.OrderedReadyCandidates[index];
            if (candidate.Instruction is null || candidate.InstructionIndex != candidate.Instruction.Index ||
                !identity.Add(candidate.InstructionIndex))
            {
                return Fallback("InvalidCandidateIdentity", windowSize, windowTruncated, options);
            }
        }

        CandidateEvaluation? best = null;
        int statesEvaluated = 0;
        int resourcePruned = 0;
        int structuralPruned = 0;
        int beamPruned = 0;
        int exactPlacementsEvaluated = 0;
        bool capHit = false;
        int maximumWidth = Math.Min(options.MaximumDepth, windowSize);
        int maskLimit = 1 << windowSize;

        for (int width = maximumWidth; width >= 1 && !capHit; width--)
        {
            int admittedAtWidth = 0;
            for (int mask = 1; mask < maskLimit; mask++)
            {
                if (BitOperations.PopCount((uint)mask) != width) continue;
                if (statesEvaluated >= options.MaximumEvaluatedStates)
                {
                    capHit = true;
                    break;
                }

                statesEvaluated++;
                List<IrCycleMembershipCandidate> members = MaterializeMembers(
                    request.OrderedReadyCandidates, windowSize, mask);
                if (ResourceModelRejects(members))
                {
                    resourcePruned++;
                    continue;
                }

                IrInstruction[] instructions = members.Select(static member => member.Instruction).ToArray();
                IrCandidateBundleAnalysis structural = _legalityChecker.AnalyzeCandidateBundle(instructions);
                if (!structural.IsStructurallyAdmissible)
                {
                    structuralPruned++;
                    continue;
                }

                if (admittedAtWidth >= options.BeamWidth)
                {
                    beamPruned++;
                    continue;
                }

                admittedAtWidth++;
                IrBundlePlacementSearchResult placement;
                try
                {
                    placement = _placementBackend.SearchStructuralAssignments(
                        instructions.Select(static instruction => instruction.Annotation.StructurallyAllowedSlots).ToArray());
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    return Fallback(
                        "PlacementBackendFailure", windowSize, windowTruncated, options,
                        statesEvaluated, resourcePruned, structuralPruned, beamPruned,
                        exactPlacementsEvaluated, capHit);
                }

                exactPlacementsEvaluated++;
                if (!placement.HasStructuralPlacement)
                {
                    structuralPruned++;
                    continue;
                }

                CandidateEvaluation evaluation = new CandidateEvaluation(members, placement);
                if (best is null || IsBetter(evaluation, best, readyCount)) best = evaluation;
            }
        }

        if (best is null)
        {
            return Fallback(
                capHit ? "StateBudgetExhaustedWithoutValidatedCandidate" : "NoValidatedAlternative",
                windowSize, windowTruncated, options, statesEvaluated, resourcePruned,
                structuralPruned, beamPruned, exactPlacementsEvaluated, capHit);
        }

        IrCyclePlacementWitness witness = new IrCyclePlacementWitness(
            _resourceModel.Description.Key,
            options.Fingerprint,
            best.Members.Select(static member => member.InstructionIndex).ToArray(),
            best.Placement.BestInstructionSlots);
        return new IrCycleSearchResult(
            best.Members,
            witness,
            new IrCycleSearchSummaryV1(
                windowSize,
                windowTruncated,
                statesEvaluated,
                resourcePruned,
                structuralPruned,
                beamPruned,
                exactPlacementsEvaluated,
                capHit,
                false,
                null,
                options.Fingerprint));
    }

    private bool ResourceModelRejects(IReadOnlyList<IrCycleMembershipCandidate> members)
    {
        HybridCpuCycleResourceState state = HybridCpuCycleResourceState.Empty(_resourceModel.Description);
        foreach (IrCycleMembershipCandidate member in members)
        {
            IrResourceFootprint footprint = _resourceModel.GetResourceFootprint(member.Instruction);
            HybridCpuResourceReservationResultV1 result = _resourceModel.CanReserve(state, footprint);
            if (result.Decision == HybridCpuResourceReservationDecisionV1.Rejected) return true;
            if (result.Decision == HybridCpuResourceReservationDecisionV1.RequiresLegacyFallback) continue;
            state = _resourceModel.Reserve(state, footprint);
        }

        return false;
    }

    private static List<IrCycleMembershipCandidate> MaterializeMembers(
        IReadOnlyList<IrCycleMembershipCandidate> candidates,
        int windowSize,
        int mask)
    {
        List<IrCycleMembershipCandidate> members = new List<IrCycleMembershipCandidate>(BitOperations.PopCount((uint)mask));
        for (int index = 0; index < windowSize; index++)
        {
            if ((mask & (1 << index)) != 0) members.Add(candidates[index]);
        }

        return members;
    }

    private static bool IsBetter(CandidateEvaluation candidate, CandidateEvaluation current, int totalReadyCount)
    {
        int comparison = candidate.Members.Count.CompareTo(current.Members.Count);
        if (comparison != 0) return comparison > 0;

        int candidateProjected = (totalReadyCount - candidate.Members.Count + 7) / 8;
        int currentProjected = (totalReadyCount - current.Members.Count + 7) / 8;
        comparison = currentProjected.CompareTo(candidateProjected);
        if (comparison != 0) return comparison > 0;

        comparison = candidate.Members.Max(static member => member.CriticalPathLengthCycles)
            .CompareTo(current.Members.Max(static member => member.CriticalPathLengthCycles));
        if (comparison != 0) return comparison > 0;

        comparison = candidate.Members.Sum(static member => member.CriticalPathLengthCycles)
            .CompareTo(current.Members.Sum(static member => member.CriticalPathLengthCycles));
        if (comparison != 0) return comparison > 0;

        comparison = candidate.Members.Sum(static member => member.TransitiveSuccessorCount)
            .CompareTo(current.Members.Sum(static member => member.TransitiveSuccessorCount));
        if (comparison != 0) return comparison > 0;

        IrBundlePlacementQuality candidateQuality = candidate.Placement.BestQuality;
        IrBundlePlacementQuality currentQuality = current.Placement.BestQuality;
        comparison = currentQuality.InternalGapCount.CompareTo(candidateQuality.InternalGapCount);
        if (comparison != 0) return comparison > 0;
        comparison = currentQuality.OrderInversionCount.CompareTo(candidateQuality.OrderInversionCount);
        if (comparison != 0) return comparison > 0;
        comparison = currentQuality.ConstrainedSlotDisplacementCost.CompareTo(candidateQuality.ConstrainedSlotDisplacementCost);
        if (comparison != 0) return comparison > 0;

        for (int index = 0; index < candidate.Members.Count; index++)
        {
            comparison = candidate.Members[index].InstructionIndex.CompareTo(current.Members[index].InstructionIndex);
            if (comparison != 0) return comparison < 0;
        }

        for (int index = 0; index < candidate.Placement.BestInstructionSlots.Count; index++)
        {
            comparison = candidate.Placement.BestInstructionSlots[index]
                .CompareTo(current.Placement.BestInstructionSlots[index]);
            if (comparison != 0) return comparison < 0;
        }

        return false;
    }

    private static IrCycleSearchResult Fallback(
        string reason,
        int readyWindowSize,
        bool windowTruncated,
        HybridCpuCycleSearchOptionsV1 options,
        int statesEvaluated = 0,
        int resourcePruned = 0,
        int structuralPruned = 0,
        int beamPruned = 0,
        int exactPlacementsEvaluated = 0,
        bool capHit = false) =>
        new(
            Array.Empty<IrCycleMembershipCandidate>(),
            null,
            new IrCycleSearchSummaryV1(
                readyWindowSize,
                windowTruncated,
                statesEvaluated,
                resourcePruned,
                structuralPruned,
                beamPruned,
                exactPlacementsEvaluated,
                capHit,
                true,
                reason,
                options.Fingerprint));

    private sealed record CandidateEvaluation(
        IReadOnlyList<IrCycleMembershipCandidate> Members,
        IrBundlePlacementSearchResult Placement);
}
