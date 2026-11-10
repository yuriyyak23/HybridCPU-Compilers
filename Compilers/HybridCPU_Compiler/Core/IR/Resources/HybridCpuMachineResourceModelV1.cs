using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Compiler.Core.IR.Resources;

public interface IHybridCpuMachineResourceModel
{
    HybridCpuMachineDescriptionV1 Description { get; }
    IrResourceFootprint GetResourceFootprint(IrInstruction instruction);
    HybridCpuResourceReservationResultV1 CanReserve(HybridCpuCycleResourceState state, IrResourceFootprint footprint);
    HybridCpuCycleResourceState Reserve(HybridCpuCycleResourceState state, IrResourceFootprint footprint);
    HybridCpuResourceIncrementalCostV1 IncrementalCost(HybridCpuCycleResourceState state, IrResourceFootprint footprint);
}

/// <summary>
/// Immutable per-search cycle state. Reserve returns a new state and cannot mutate its input.
/// </summary>
public sealed class HybridCpuCycleResourceState
{
    private readonly IrResourceFootprint[] _reserved;

    private HybridCpuCycleResourceState(string machineDescriptionKey, IrResourceFootprint[] reserved)
    {
        MachineDescriptionKey = machineDescriptionKey;
        _reserved = reserved;
        ReservedFootprints = Array.AsReadOnly(_reserved);
        StateDigest = ComputeStateDigest(machineDescriptionKey, _reserved);
    }

    public string MachineDescriptionKey { get; }
    public IReadOnlyList<IrResourceFootprint> ReservedFootprints { get; }
    public int ReservedInstructionCount => _reserved.Length;
    public string StateDigest { get; }

    public static HybridCpuCycleResourceState Empty(HybridCpuMachineDescriptionV1 description)
    {
        ArgumentNullException.ThrowIfNull(description);
        return new HybridCpuCycleResourceState(description.Key, []);
    }

    internal HybridCpuCycleResourceState Append(IrResourceFootprint footprint)
    {
        IrResourceFootprint[] next = new IrResourceFootprint[_reserved.Length + 1];
        Array.Copy(_reserved, next, _reserved.Length);
        next[^1] = footprint;
        return new HybridCpuCycleResourceState(MachineDescriptionKey, next);
    }

    internal bool ContainsInstruction(int instructionIndex) =>
        Array.Exists(_reserved, footprint => footprint.InstructionIndex == instructionIndex);

    private static string ComputeStateDigest(string key, IReadOnlyList<IrResourceFootprint> footprints)
    {
        StringBuilder builder = new StringBuilder(key);
        foreach (IrResourceFootprint footprint in footprints)
        {
            builder.Append('|').Append(footprint.InstructionIndex.ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(footprint.Fingerprint);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }
}

public sealed class HybridCpuMachineResourceModelV1 : IHybridCpuMachineResourceModel
{
    private const IrStructuralResource KnownStructuralResources =
        IrStructuralResource.ReductionUnit |
        IrStructuralResource.VectorPermuteCrossbar |
        IrStructuralResource.AddressGenerationUnit |
        IrStructuralResource.LoadDataPort |
        IrStructuralResource.StoreDataPort |
        IrStructuralResource.BranchResolver |
        IrStructuralResource.ControlSequencer |
        IrStructuralResource.SystemSequencer |
        IrStructuralResource.CsrPort |
        IrStructuralResource.VmStatePort |
        IrStructuralResource.BarrierSequencer;

    public static HybridCpuMachineResourceModelV1 Default { get; } = new();

    public HybridCpuMachineDescriptionV1 Description => HybridCpuMachineDescriptionV1.Default;

    public IrResourceFootprint GetResourceFootprint(IrInstruction instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);

        IrInstructionAnnotation annotation = instruction.Annotation;
        List<IrStructuralResourceDemandV1> demands = new List<IrStructuralResourceDemandV1>();
        foreach (HybridCpuStructuralCapacityV1 resource in Description.StructuralCapacities)
        {
            if ((annotation.StructuralResources & resource.Resource) != 0)
            {
                demands.Add(new IrStructuralResourceDemandV1(
                    resource.Resource,
                    1,
                    IrResourceFactPrecisionV1.Exact));
            }
        }

        string? fallbackReason = null;
        bool requiresFallback = false;
        IrResourceFactPrecisionV1 slotPrecision = IrResourceFactPrecisionV1.Exact;
        IrResourceFactPrecisionV1 classPrecision = IrResourceFactPrecisionV1.Exact;
        IrResourceFootprintProvenanceV1 provenance = IrResourceFootprintProvenanceV1.StaticCanonical;

        if (annotation.StructurallyAllowedSlots == IrIssueSlotMask.None ||
            (annotation.StructurallyAllowedSlots & ~IrIssueSlotMask.All) != 0)
        {
            requiresFallback = true;
            fallbackReason = "UnknownStructuralSlotMask";
            slotPrecision = IrResourceFactPrecisionV1.Unknown;
        }

        if (!Description.TryGetSlotClass(annotation.RequiredSlotClass, out HybridCpuSlotClassTopologyV1 topology) ||
            !topology.CountedByLegacyCompiler)
        {
            requiresFallback = true;
            fallbackReason ??= $"UnsupportedSlotClass:{annotation.RequiredSlotClass}";
            classPrecision = IrResourceFactPrecisionV1.Unknown;
        }

        if (instruction.Opcode is HybridCpuOpcode.MTILE_LOAD or HybridCpuOpcode.MTILE_STORE)
        {
            requiresFallback = true;
            fallbackReason = "MatrixTileStreamTopologyGap";
            classPrecision = IrResourceFactPrecisionV1.Unknown;
        }

        if ((annotation.StructuralResources & ~KnownStructuralResources) != 0)
        {
            requiresFallback = true;
            fallbackReason ??= "UnknownStructuralResource";
        }

        if (requiresFallback)
        {
            provenance = fallbackReason == "UnknownStructuralResource" ||
                         fallbackReason == "UnknownStructuralSlotMask"
                ? IrResourceFootprintProvenanceV1.Unknown
                : IrResourceFootprintProvenanceV1.ConservativeFallback;
        }

        IrMemoryEffectV1 memoryEffect = IrMemoryEffectV1.None;
        if (annotation.MemoryReadRegion is not null) memoryEffect |= IrMemoryEffectV1.Read;
        if (annotation.MemoryWriteRegion is not null) memoryEffect |= IrMemoryEffectV1.Write;
        IrResourceFactPrecisionV1 memoryPrecision = memoryEffect == IrMemoryEffectV1.None
            ? IrResourceFactPrecisionV1.Exact
            : IrResourceFactPrecisionV1.Conservative;

        string fingerprint = ComputeFootprintFingerprint(
            instruction.Index,
            annotation,
            demands,
            memoryEffect,
            slotPrecision,
            classPrecision,
            memoryPrecision,
            provenance,
            requiresFallback,
            fallbackReason);

        return new IrResourceFootprint(
            IrResourceFootprint.SchemaName,
            IrResourceFootprint.CurrentSchemaVersion,
            instruction.Index,
            annotation.StructurallyAllowedSlots,
            annotation.RequiredSlotClass,
            annotation.BindingKind,
            Array.AsReadOnly(demands.ToArray()),
            annotation.Serialization,
            annotation.ControlFlowKind,
            memoryEffect,
            slotPrecision,
            classPrecision,
            memoryPrecision,
            provenance,
            RequiresExactPlacement: true,
            RequiresLegacyFallback: requiresFallback,
            FallbackReason: fallbackReason,
            Fingerprint: fingerprint)
        {
            Topology = IrTopologyResourceFootprintBuilderV1.Build(
                instruction,
                HybridCpuMachineTopologyV1.Default)
        };
    }

    public HybridCpuResourceReservationResultV1 CanReserve(
        HybridCpuCycleResourceState state,
        IrResourceFootprint footprint)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(footprint);

        if (!string.Equals(state.MachineDescriptionKey, Description.Key, StringComparison.Ordinal))
        {
            return Result(HybridCpuResourceReservationDecisionV1.Rejected,
                HybridCpuResourceReasonCodeV1.StateModelMismatch, "machine", 1, 0, 1, state);
        }

        if (state.ContainsInstruction(footprint.InstructionIndex))
        {
            return Result(HybridCpuResourceReservationDecisionV1.Rejected,
                HybridCpuResourceReasonCodeV1.DuplicateInstruction, "instruction", 1, 1, 1, state);
        }

        if (footprint.RequiresLegacyFallback)
        {
            return Result(HybridCpuResourceReservationDecisionV1.RequiresLegacyFallback,
                footprint.Provenance == IrResourceFootprintProvenanceV1.Unknown
                    ? HybridCpuResourceReasonCodeV1.UnknownFootprint
                    : HybridCpuResourceReasonCodeV1.UnsupportedTopology,
                footprint.FallbackReason ?? "unknown", 1, 0, 0, state);
        }

        int requestedWidth = state.ReservedInstructionCount + 1;
        if (requestedWidth > Description.Width)
        {
            return Result(HybridCpuResourceReservationDecisionV1.Rejected,
                HybridCpuResourceReasonCodeV1.IssueWidthExceeded, "issue-width",
                requestedWidth, state.ReservedInstructionCount, Description.Width, state);
        }

        if (!Description.TryGetSlotClass(footprint.RequiredSlotClass, out HybridCpuSlotClassTopologyV1 topology))
        {
            return Result(HybridCpuResourceReservationDecisionV1.RequiresLegacyFallback,
                HybridCpuResourceReasonCodeV1.UnsupportedTopology, "slot-class", 1, 0, 0, state);
        }

        int usedClass = CountClass(state.ReservedFootprints, footprint.RequiredSlotClass);
        if (usedClass + 1 > topology.Capacity)
        {
            return Result(HybridCpuResourceReservationDecisionV1.Rejected,
                HybridCpuResourceReasonCodeV1.ClassCapacityExceeded,
                $"class:{footprint.RequiredSlotClass}", 1, usedClass, topology.Capacity, state);
        }

        foreach (HybridCpuSlotClassTopologyV1 otherTopology in Description.SlotClasses)
        {
            if (!otherTopology.CountedByLegacyCompiler || otherTopology.SlotClass == footprint.RequiredSlotClass)
            {
                continue;
            }

            int otherUsed = CountClass(state.ReservedFootprints, otherTopology.SlotClass);
            if (otherUsed != 0 &&
                (otherTopology.PhysicalLaneMask & topology.PhysicalLaneMask) != 0)
            {
                return Result(HybridCpuResourceReservationDecisionV1.Rejected,
                    HybridCpuResourceReasonCodeV1.AliasedLaneConflict,
                    $"alias:{otherTopology.SlotClass}:{footprint.RequiredSlotClass}",
                    1, otherUsed, 1, state);
            }
        }

        bool newExclusive = (footprint.Serialization & IrSerializationKind.ExclusiveCycle) != 0;
        bool existingExclusive = state.ReservedFootprints.Any(existing =>
            (existing.Serialization & IrSerializationKind.ExclusiveCycle) != 0);
        if (state.ReservedInstructionCount > 0 && (newExclusive || existingExclusive))
        {
            return Result(HybridCpuResourceReservationDecisionV1.Rejected,
                HybridCpuResourceReasonCodeV1.ExclusiveCycleRequired,
                "serialization:exclusive", 1, state.ReservedInstructionCount, 1, state);
        }

        // Legacy checker applies structural overflow pairwise. Preserve that decision semantics
        // in shadow mode; aggregate accounting can only become authoritative in a later gate.
        foreach (IrResourceFootprint existing in state.ReservedFootprints)
        {
            foreach (IrStructuralResourceDemandV1 demand in footprint.StructuralDemands)
            {
                int pairUsed = demand.Units + GetDemand(existing, demand.Resource);
                if (Description.TryGetStructuralCapacity(demand.Resource, out int capacity) &&
                    pairUsed > capacity)
                {
                    return Result(HybridCpuResourceReservationDecisionV1.Rejected,
                        HybridCpuResourceReasonCodeV1.StructuralCapacityExceeded,
                        $"structural:{demand.Resource}", demand.Units, pairUsed - demand.Units,
                        capacity, state);
                }
            }
        }

        IrIssueSlotMask[] masks = new IrIssueSlotMask[state.ReservedInstructionCount + 1];
        for (int index = 0; index < state.ReservedInstructionCount; index++)
        {
            masks[index] = state.ReservedFootprints[index].StructurallyAllowedSlots;
        }
        masks[^1] = footprint.StructurallyAllowedSlots;
        if (!HybridCpuSlotModel.HasStructuralPlacement(masks))
        {
            return Result(HybridCpuResourceReservationDecisionV1.Rejected,
                HybridCpuResourceReasonCodeV1.NoStructuralPlacement,
                "compiler-slot-mask", 1, state.ReservedInstructionCount, Description.Width, state);
        }

        return Result(HybridCpuResourceReservationDecisionV1.Allowed,
            HybridCpuResourceReasonCodeV1.None, "none", 1, 0, 0, state);
    }

    public HybridCpuCycleResourceState Reserve(
        HybridCpuCycleResourceState state,
        IrResourceFootprint footprint)
    {
        HybridCpuResourceReservationResultV1 result = CanReserve(state, footprint);
        if (!result.IsAllowed)
        {
            throw new InvalidOperationException(
                $"Cannot reserve instruction {footprint.InstructionIndex}: {result.Decision}/{result.ReasonCode}.");
        }

        return state.Append(footprint);
    }

    public HybridCpuResourceIncrementalCostV1 IncrementalCost(
        HybridCpuCycleResourceState state,
        IrResourceFootprint footprint)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(footprint);

        int maximumPressure = 0;
        foreach (HybridCpuSlotClassTopologyV1 topology in Description.SlotClasses)
        {
            if (!topology.CountedByLegacyCompiler) continue;
            int used = CountClass(state.ReservedFootprints, topology.SlotClass) +
                       (footprint.RequiredSlotClass == topology.SlotClass ? 1 : 0);
            maximumPressure = Math.Max(maximumPressure, used * 1000 / topology.Capacity);
        }

        int structuralPressure = footprint.StructuralDemands.Sum(demand => demand.Units);
        int hardPinPressure = footprint.BindingKind == IrSlotBindingKind.HardPinned ? 1 : 0;
        return new HybridCpuResourceIncrementalCostV1(
            state.ReservedInstructionCount + 1,
            maximumPressure,
            structuralPressure,
            hardPinPressure,
            footprint.InstructionIndex);
    }

    private static int CountClass(IReadOnlyList<IrResourceFootprint> footprints, IrSlotClass slotClass) =>
        footprints.Count(footprint => footprint.RequiredSlotClass == slotClass);

    private static int GetDemand(IrResourceFootprint footprint, IrStructuralResource resource)
    {
        foreach (IrStructuralResourceDemandV1 demand in footprint.StructuralDemands)
        {
            if (demand.Resource == resource) return demand.Units;
        }
        return 0;
    }

    private static HybridCpuResourceReservationResultV1 Result(
        HybridCpuResourceReservationDecisionV1 decision,
        HybridCpuResourceReasonCodeV1 reason,
        string key,
        int requested,
        int used,
        int capacity,
        HybridCpuCycleResourceState state) =>
        new(decision, reason, key, requested, used, capacity, state.StateDigest);

    private static string ComputeFootprintFingerprint(
        int instructionIndex,
        IrInstructionAnnotation annotation,
        IReadOnlyList<IrStructuralResourceDemandV1> demands,
        IrMemoryEffectV1 memoryEffect,
        IrResourceFactPrecisionV1 slotPrecision,
        IrResourceFactPrecisionV1 classPrecision,
        IrResourceFactPrecisionV1 memoryPrecision,
        IrResourceFootprintProvenanceV1 provenance,
        bool requiresFallback,
        string? fallbackReason)
    {
        StringBuilder builder = new StringBuilder();
        builder.Append(IrResourceFootprint.SchemaName).Append('|')
            .Append(instructionIndex.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(((int)annotation.StructurallyAllowedSlots).ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(((byte)annotation.RequiredSlotClass).ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(((byte)annotation.BindingKind).ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(((int)annotation.Serialization).ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(((int)annotation.ControlFlowKind).ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(((byte)memoryEffect).ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(((byte)slotPrecision).ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(((byte)classPrecision).ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(((byte)memoryPrecision).ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(((byte)provenance).ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(requiresFallback ? '1' : '0').Append('|').Append(fallbackReason ?? string.Empty);
        foreach (IrStructuralResourceDemandV1 demand in demands)
        {
            builder.Append("|d:")
                .Append(((int)demand.Resource).ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(demand.Units.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(((byte)demand.Precision).ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }
}
