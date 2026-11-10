using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Compiler.Core.IR.Resources;

public sealed record HybridCpuSlotClassTopologyV1(
    IrSlotClass SlotClass,
    byte PhysicalLaneMask,
    int Capacity,
    bool CountedByLegacyCompiler);

public sealed record HybridCpuStructuralCapacityV1(
    IrStructuralResource Resource,
    int Capacity);

/// <summary>
/// Immutable compiler-owned snapshot of the structural topology used by Phase 01.
/// Runtime topology is a parity oracle, never the production object behind this contract.
/// </summary>
public sealed class HybridCpuMachineDescriptionV1
{
    public const string SchemaName = "HybridCpuMachineDescriptionV1";
    public const int SchemaVersion = 1;
    public const int IssueWidth = 8;

    private static readonly HybridCpuSlotClassTopologyV1[] SlotTopology =
    [
        new(IrSlotClass.AluClass, 0b_0000_1111, 4, true),
        new(IrSlotClass.LsuClass, 0b_0011_0000, 2, true),
        new(IrSlotClass.DmaStreamClass, 0b_0100_0000, 1, true),
        new(IrSlotClass.MatrixTileStreamClass, 0b_0100_0000, 1, false),
        new(IrSlotClass.BranchControl, 0b_1000_0000, 1, true),
        new(IrSlotClass.SystemSingleton, 0b_1000_0000, 1, true),
        new(IrSlotClass.Unclassified, 0b_1111_1111, 8, false)
    ];

    private static readonly HybridCpuStructuralCapacityV1[] StructuralTopology =
    [
        new(IrStructuralResource.ReductionUnit, 1),
        new(IrStructuralResource.VectorPermuteCrossbar, 1),
        new(IrStructuralResource.AddressGenerationUnit, 2),
        new(IrStructuralResource.LoadDataPort, 2),
        new(IrStructuralResource.StoreDataPort, 2),
        new(IrStructuralResource.BranchResolver, 1),
        new(IrStructuralResource.ControlSequencer, 1),
        new(IrStructuralResource.SystemSequencer, 1),
        new(IrStructuralResource.CsrPort, 1),
        new(IrStructuralResource.VmStatePort, 1),
        new(IrStructuralResource.BarrierSequencer, 1)
    ];

    public static HybridCpuMachineDescriptionV1 Default { get; } = new();

    private HybridCpuMachineDescriptionV1()
    {
        SlotClasses = Array.AsReadOnly(SlotTopology);
        StructuralCapacities = Array.AsReadOnly(StructuralTopology);
        ContractDigest = ComputeDigest();
        Key = $"{SchemaName}:{SchemaVersion}:W{IssueWidth}:{ContractDigest}";
    }

    public string Schema => SchemaName;
    public int Version => SchemaVersion;
    public int Width => IssueWidth;
    public IReadOnlyList<HybridCpuSlotClassTopologyV1> SlotClasses { get; }
    public IReadOnlyList<HybridCpuStructuralCapacityV1> StructuralCapacities { get; }
    public string ContractDigest { get; }
    public string Key { get; }

    public bool TryGetSlotClass(IrSlotClass slotClass, out HybridCpuSlotClassTopologyV1 topology)
    {
        foreach (HybridCpuSlotClassTopologyV1 candidate in SlotTopology)
        {
            if (candidate.SlotClass == slotClass)
            {
                topology = candidate;
                return true;
            }
        }

        topology = null!;
        return false;
    }

    public int GetSlotClassCapacity(IrSlotClass slotClass) =>
        TryGetSlotClass(slotClass, out HybridCpuSlotClassTopologyV1 topology)
            ? topology.Capacity
            : 0;

    public bool HasAliasedLanes(IrSlotClass slotClass)
    {
        if (!TryGetSlotClass(slotClass, out HybridCpuSlotClassTopologyV1 topology))
        {
            return false;
        }

        foreach (HybridCpuSlotClassTopologyV1 candidate in SlotTopology)
        {
            if (candidate.SlotClass != slotClass &&
                (candidate.PhysicalLaneMask & topology.PhysicalLaneMask) != 0)
            {
                return true;
            }
        }

        return false;
    }

    public bool TryGetStructuralCapacity(IrStructuralResource resource, out int capacity)
    {
        foreach (HybridCpuStructuralCapacityV1 candidate in StructuralTopology)
        {
            if (candidate.Resource == resource)
            {
                capacity = candidate.Capacity;
                return true;
            }
        }

        capacity = 0;
        return false;
    }

    private static string ComputeDigest()
    {
        StringBuilder builder = new StringBuilder();
        builder.Append(SchemaName).Append('|').Append(SchemaVersion).Append('|').Append(IssueWidth);
        foreach (HybridCpuSlotClassTopologyV1 slotClass in SlotTopology)
        {
            builder.Append("|c:")
                .Append(((byte)slotClass.SlotClass).ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(slotClass.PhysicalLaneMask.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(slotClass.Capacity.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(slotClass.CountedByLegacyCompiler ? '1' : '0');
        }

        foreach (HybridCpuStructuralCapacityV1 resource in StructuralTopology)
        {
            builder.Append("|r:")
                .Append(((int)resource.Resource).ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(resource.Capacity.ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }
}
