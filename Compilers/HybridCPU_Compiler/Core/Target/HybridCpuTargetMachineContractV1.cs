using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;

namespace HybridCPU.Compiler.Core.Target;

public enum HybridCpuTargetEndianness : byte
{
    Little = 0,
    Unknown = 255
}

public enum HybridCpuTargetFactSupport : byte
{
    Supported = 0,
    Unsupported = 1,
    Unknown = 2
}

public enum HybridCpuArchitecturalRegisterClass : byte
{
    ScalarInteger64 = 0
}

public enum HybridCpuSpecialStateClass : byte
{
    ControlStatus = 0,
    Predicate = 1,
    VirtualThreadContext = 2,
    ExecutionContourState = 3
}

public enum HybridCpuTargetCompatibility : byte
{
    Compatible = 0,
    UnsupportedVersion = 1,
    TargetMismatch = 2,
    DataLayoutMismatch = 3,
    Unknown = 4
}

public sealed record HybridCpuScalarLayoutV1(int BitWidth, int SizeBytes, int AbiAlignmentBytes);

public sealed record HybridCpuAddressSpaceContractV1(
    IrAddressSpaceIdentity Identity,
    int NumericId,
    HybridCpuTargetFactSupport Support);

public sealed record HybridCpuArchitecturalRegisterV1(
    int Id,
    string EncodedName,
    int BitWidth,
    HybridCpuArchitecturalRegisterClass RegisterClass,
    int RegisterGroup,
    bool IsAllocatable,
    bool IsFixedZero);

public sealed record HybridCpuSpecialStateContractV1(
    HybridCpuSpecialStateClass StateClass,
    string Identity,
    bool IsAllocatorOwned);

public sealed record HybridCpuPrimitiveCallBoundaryV1(
    string Identity,
    bool UsesExplicitLinkDestination,
    bool UsesExplicitReturnBase,
    IReadOnlyList<int> ImplicitUses,
    IReadOnlyList<int> ImplicitDefs,
    IReadOnlyList<int> ImplicitClobbers,
    HybridCpuTargetFactSupport FullFunctionAbiSupport);

public sealed record HybridCpuAggregateFieldV1(string Identity, int SizeBytes, int AlignmentBytes);

public sealed record HybridCpuAggregateFieldLayoutV1(
    string Identity,
    int OffsetBytes,
    int SizeBytes,
    int AlignmentBytes);

public sealed record HybridCpuAggregateLayoutV1(
    int SizeBytes,
    int AlignmentBytes,
    IReadOnlyList<HybridCpuAggregateFieldLayoutV1> Fields);

/// <summary>
/// Versioned compiler-visible HybridCPU-v2 target facts. This contract contains no live
/// backend allocation, rename, occupancy, scoreboard or execution-permission state.
/// </summary>
public sealed class HybridCpuTargetMachineContractV1
{
    public const string SchemaId = "hybridcpu.target-machine-core";
    public const int SchemaMajor = 1;
    public const int SchemaMinor = 0;
    public const string TargetArchitectureRevision = "HybridCPU-W8-native-v1";
    public const string TargetTriple = "hybridcpuv2-unknown-none";
    public const string DataLayoutVersion = "hybridcpu-native-datalayout/v1";
    public const string DataLayoutIdentity =
        "endianness=little;pointer=64:64;scalars=8:8,16:16,32:32,64:64;aggregate=natural-max64";
    public const int PointerBitWidth = 64;
    public const int PointerAbiAlignmentBytes = 8;
    public const int ArchitecturalRegisterCount = 32;
    public const int ArchitecturalRegisterBitWidth = 64;

    private static readonly HybridCpuScalarLayoutV1[] ScalarLayoutTable =
    [
        new(8, 1, 1),
        new(16, 2, 2),
        new(32, 4, 4),
        new(64, 8, 8)
    ];

    private static readonly HybridCpuAddressSpaceContractV1[] AddressSpaceTable =
    [
        new(IrAddressSpaceIdentity.Generic, 0, HybridCpuTargetFactSupport.Supported),
        new(IrAddressSpaceIdentity.Stack, 1, HybridCpuTargetFactSupport.Unsupported),
        new(IrAddressSpaceIdentity.Global, 2, HybridCpuTargetFactSupport.Unsupported),
        new(IrAddressSpaceIdentity.Constant, 3, HybridCpuTargetFactSupport.Unsupported),
        new(IrAddressSpaceIdentity.ThreadLocal, 4, HybridCpuTargetFactSupport.Unsupported),
        new(IrAddressSpaceIdentity.Device, 5, HybridCpuTargetFactSupport.Unsupported)
    ];

    private static readonly HybridCpuArchitecturalRegisterV1[] ArchitecturalRegisterTable =
        Enumerable.Range(0, ArchitecturalRegisterCount)
            .Select(static id => new HybridCpuArchitecturalRegisterV1(
                id,
                $"x{id.ToString(CultureInfo.InvariantCulture)}",
                ArchitecturalRegisterBitWidth,
                HybridCpuArchitecturalRegisterClass.ScalarInteger64,
                id / HybridCpuMachineTopologyV1.RegistersPerGroup,
                IsAllocatable: id != 0,
                IsFixedZero: id == 0))
            .ToArray();

    private static readonly HybridCpuSpecialStateContractV1[] SpecialStateTable =
    [
        new(HybridCpuSpecialStateClass.ControlStatus, "csr", IsAllocatorOwned: false),
        new(HybridCpuSpecialStateClass.Predicate, "instruction-predicate-mask", IsAllocatorOwned: false),
        new(HybridCpuSpecialStateClass.VirtualThreadContext, "virtual-thread-context", IsAllocatorOwned: false),
        new(HybridCpuSpecialStateClass.ExecutionContourState, "stream-vector-matrix-execution-state", IsAllocatorOwned: false)
    ];

    public static HybridCpuTargetMachineContractV1 Default { get; } = new();

    private HybridCpuTargetMachineContractV1()
    {
        ScalarLayouts = Array.AsReadOnly(ScalarLayoutTable);
        AddressSpaces = Array.AsReadOnly(AddressSpaceTable);
        ArchitecturalRegisters = Array.AsReadOnly(ArchitecturalRegisterTable);
        SpecialState = Array.AsReadOnly(SpecialStateTable);
        PrimitiveCallBoundary = new(
            "hybridcpu.native-explicit-control-transfer/v1",
            UsesExplicitLinkDestination: true,
            UsesExplicitReturnBase: true,
            ImplicitUses: Array.Empty<int>(),
            ImplicitDefs: Array.Empty<int>(),
            ImplicitClobbers: Array.Empty<int>(),
            FullFunctionAbiSupport: HybridCpuTargetFactSupport.Unsupported);
        ContractDigest = ComputeDigest();
    }

    public HybridCpuTargetEndianness Endianness => HybridCpuTargetEndianness.Little;
    public IReadOnlyList<HybridCpuScalarLayoutV1> ScalarLayouts { get; }
    public IReadOnlyList<HybridCpuAddressSpaceContractV1> AddressSpaces { get; }
    public IReadOnlyList<HybridCpuArchitecturalRegisterV1> ArchitecturalRegisters { get; }
    public IReadOnlyList<HybridCpuSpecialStateContractV1> SpecialState { get; }
    public HybridCpuPrimitiveCallBoundaryV1 PrimitiveCallBoundary { get; }
    public string ContractDigest { get; }

    public HybridCpuTargetCompatibility CheckCompatibility(
        int schemaMajor,
        string targetTriple,
        string dataLayoutVersion,
        string dataLayoutIdentity)
    {
        if (schemaMajor != SchemaMajor) return HybridCpuTargetCompatibility.UnsupportedVersion;
        if (string.IsNullOrWhiteSpace(targetTriple) ||
            string.IsNullOrWhiteSpace(dataLayoutVersion) ||
            string.IsNullOrWhiteSpace(dataLayoutIdentity))
            return HybridCpuTargetCompatibility.Unknown;
        if (!string.Equals(targetTriple, TargetTriple, StringComparison.Ordinal))
            return HybridCpuTargetCompatibility.TargetMismatch;
        return string.Equals(dataLayoutVersion, DataLayoutVersion, StringComparison.Ordinal) &&
               string.Equals(dataLayoutIdentity, DataLayoutIdentity, StringComparison.Ordinal)
            ? HybridCpuTargetCompatibility.Compatible
            : HybridCpuTargetCompatibility.DataLayoutMismatch;
    }

    public bool TryGetScalarLayout(int bitWidth, out HybridCpuScalarLayoutV1 layout)
    {
        layout = ScalarLayoutTable.FirstOrDefault(candidate => candidate.BitWidth == bitWidth)!;
        return layout is not null;
    }

    public bool TryGetArchitecturalRegister(int registerId, out HybridCpuArchitecturalRegisterV1 register)
    {
        if ((uint)registerId < ArchitecturalRegisterTable.Length)
        {
            register = ArchitecturalRegisterTable[registerId];
            return true;
        }

        register = null!;
        return false;
    }

    public HybridCpuTargetFactSupport GetAddressSpaceSupport(IrAddressSpaceIdentity identity) =>
        AddressSpaceTable.FirstOrDefault(candidate => candidate.Identity == identity)?.Support ??
        HybridCpuTargetFactSupport.Unknown;

    public bool TryLayoutAggregate(
        IReadOnlyList<HybridCpuAggregateFieldV1> fields,
        out HybridCpuAggregateLayoutV1 layout)
    {
        ArgumentNullException.ThrowIfNull(fields);
        List<HybridCpuAggregateFieldLayoutV1> placed = [];
        int offset = 0;
        int aggregateAlignment = 1;
        foreach (HybridCpuAggregateFieldV1 field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Identity) ||
                field.SizeBytes <= 0 ||
                field.AlignmentBytes is not (1 or 2 or 4 or 8))
            {
                layout = null!;
                return false;
            }

            try
            {
                offset = AlignUp(offset, field.AlignmentBytes);
                placed.Add(new(field.Identity, offset, field.SizeBytes, field.AlignmentBytes));
                offset = checked(offset + field.SizeBytes);
                aggregateAlignment = Math.Max(aggregateAlignment, field.AlignmentBytes);
            }
            catch (OverflowException)
            {
                layout = null!;
                return false;
            }
        }

        try
        {
            layout = new(AlignUp(offset, aggregateAlignment), aggregateAlignment, placed.ToArray());
            return true;
        }
        catch (OverflowException)
        {
            layout = null!;
            return false;
        }
    }

    private static int AlignUp(int value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);

    private string ComputeDigest()
    {
        StringBuilder builder = new StringBuilder();
        builder.Append(SchemaId).Append('|').Append(SchemaMajor).Append('.').Append(SchemaMinor)
            .Append('|').Append(TargetArchitectureRevision).Append('|').Append(TargetTriple)
            .Append('|').Append(DataLayoutVersion).Append('|').Append(DataLayoutIdentity)
            .Append('|').Append(PointerBitWidth).Append(':').Append(PointerAbiAlignmentBytes);
        foreach (HybridCpuScalarLayoutV1 scalar in ScalarLayoutTable)
            builder.Append("|s:").Append(scalar.BitWidth).Append(':').Append(scalar.SizeBytes).Append(':').Append(scalar.AbiAlignmentBytes);
        foreach (HybridCpuAddressSpaceContractV1 addressSpace in AddressSpaceTable)
            builder.Append("|a:").Append(addressSpace.Identity).Append(':').Append(addressSpace.NumericId).Append(':').Append(addressSpace.Support);
        foreach (HybridCpuArchitecturalRegisterV1 register in ArchitecturalRegisterTable)
            builder.Append("|r:").Append(register.Id).Append(':').Append(register.EncodedName).Append(':')
                .Append(register.BitWidth).Append(':').Append(register.RegisterClass).Append(':')
                .Append(register.RegisterGroup).Append(':').Append(register.IsAllocatable).Append(':').Append(register.IsFixedZero);
        foreach (HybridCpuSpecialStateContractV1 state in SpecialStateTable)
            builder.Append("|x:").Append(state.StateClass).Append(':').Append(state.Identity).Append(':').Append(state.IsAllocatorOwned);
        builder.Append("|c:").Append(PrimitiveCallBoundary.Identity).Append(':')
            .Append(PrimitiveCallBoundary.UsesExplicitLinkDestination).Append(':')
            .Append(PrimitiveCallBoundary.UsesExplicitReturnBase).Append(':')
            .Append(PrimitiveCallBoundary.FullFunctionAbiSupport);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
