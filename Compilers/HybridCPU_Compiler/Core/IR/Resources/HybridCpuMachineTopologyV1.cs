using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Compiler.Core.IR.Resources;

/// <summary>
/// Immutable compiler-owned topology snapshot. Null capacities/geometries are deliberate
/// Unknown facts; live runtime state is never consulted by this type.
/// </summary>
public sealed class HybridCpuMachineTopologyV1
{
    public const string SchemaName = "HybridCpuMachineTopologyV1";
    public const int SchemaVersion = 1;
    public const int VirtualThreadCount = 4;
    public const int RegisterGroupCount = 16;
    public const int RegistersPerGroup = 4;
    public const int MaximumRepresentableRegisterId = 63;
    public const int MaximumFiniteAddressSet = 8;

    public static HybridCpuMachineTopologyV1 Default { get; } = new();

    public HybridCpuMachineTopologyV1(
        int? prfReadPortCapacity = null,
        int? prfWritePortCapacity = null,
        int? memoryBankCount = null,
        int? memoryBankWidthBytes = null,
        int? memoryChannelCount = null,
        int? memoryChannelWidthBytes = null)
    {
        ValidatePositiveOrUnknown(prfReadPortCapacity, nameof(prfReadPortCapacity));
        ValidatePositiveOrUnknown(prfWritePortCapacity, nameof(prfWritePortCapacity));
        ValidateGeometry(memoryBankCount, memoryBankWidthBytes, nameof(memoryBankCount));
        ValidateGeometry(memoryChannelCount, memoryChannelWidthBytes, nameof(memoryChannelCount));

        PrfReadPortCapacity = prfReadPortCapacity;
        PrfWritePortCapacity = prfWritePortCapacity;
        MemoryBankCount = memoryBankCount;
        MemoryBankWidthBytes = memoryBankWidthBytes;
        MemoryChannelCount = memoryChannelCount;
        MemoryChannelWidthBytes = memoryChannelWidthBytes;
        ContractDigest = ComputeDigest();
        Key = $"{SchemaName}:{SchemaVersion}:{ContractDigest}";
    }

    public int? PrfReadPortCapacity { get; }
    public int? PrfWritePortCapacity { get; }
    public int? MemoryBankCount { get; }
    public int? MemoryBankWidthBytes { get; }
    public int? MemoryChannelCount { get; }
    public int? MemoryChannelWidthBytes { get; }
    public string ContractDigest { get; }
    public string Key { get; }

    public int GetRegisterGroup(int registerId)
    {
        if ((uint)registerId > MaximumRepresentableRegisterId)
            throw new ArgumentOutOfRangeException(nameof(registerId));
        return registerId / RegistersPerGroup;
    }

    private static void ValidatePositiveOrUnknown(int? value, string name)
    {
        if (value is <= 0) throw new ArgumentOutOfRangeException(name);
    }

    private static void ValidateGeometry(int? count, int? width, string name)
    {
        if (count.HasValue != width.HasValue || count is <= 0 || width is <= 0)
            throw new ArgumentException("Topology geometry must be wholly known and positive, or wholly Unknown.", name);
    }

    private string ComputeDigest()
    {
        static string Value(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "Unknown";
        string input = string.Join('|', SchemaName, SchemaVersion, VirtualThreadCount,
            RegisterGroupCount, RegistersPerGroup, Value(PrfReadPortCapacity),
            Value(PrfWritePortCapacity), Value(MemoryBankCount), Value(MemoryBankWidthBytes),
            Value(MemoryChannelCount), Value(MemoryChannelWidthBytes));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
}
