using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Compiler.Core.Target;

public enum HybridCpuAbiValueKindV2 : byte
{
    Integer = 0,
    Pointer = 1,
    Aggregate = 2,
    Unknown = 255
}

public enum HybridCpuAbiLocationKindV2 : byte
{
    Register = 0,
    RegisterPair = 1,
    Stack = 2,
    Indirect = 3,
    Unsupported = 255
}

public enum HybridCpuStackGrowthV2 : byte
{
    Down = 0
}

public sealed record HybridCpuAbiValueV2(
    string Identity,
    HybridCpuAbiValueKindV2 Kind,
    int SizeBytes,
    int AlignmentBytes);

public sealed record HybridCpuAbiSignatureV2(
    IReadOnlyList<HybridCpuAbiValueV2> Parameters,
    HybridCpuAbiValueV2? ReturnValue,
    bool IsVarArgs = false,
    bool IsTailCall = false,
    bool IsSpecialContourCall = false);

public sealed record HybridCpuAbiLocationV2(
    string ValueIdentity,
    HybridCpuAbiLocationKindV2 Kind,
    IReadOnlyList<int> Registers,
    int? StackOffsetBytes,
    int SizeBytes);

public sealed record HybridCpuAbiLayoutV2(
    HybridCpuPlatformFactStatus Status,
    string Reason,
    IReadOnlyList<HybridCpuAbiLocationV2> Parameters,
    HybridCpuAbiLocationV2? ReturnValue,
    int StackArgumentBytes,
    string Digest);

public sealed record HybridCpuFrameSlotRequestV2(
    string Identity,
    int SizeBytes,
    int AlignmentBytes);

public sealed record HybridCpuFrameRequestV2(
    IReadOnlyList<HybridCpuFrameSlotRequestV2> Slots,
    IReadOnlyList<int> SavedRegisters,
    bool RequiresDynamicAllocation = false,
    bool RequiresStackProbe = false,
    bool SavesReturnAddress = false);

public sealed record HybridCpuFrameSlotV2(
    string Identity,
    int OffsetFromAdjustedStackPointerBytes,
    int SizeBytes,
    int AlignmentBytes);

public sealed record HybridCpuFrameLayoutV2(
    HybridCpuPlatformFactStatus Status,
    string Reason,
    int FrameSizeBytes,
    int PrologueStackAdjustmentBytes,
    int EpilogueStackAdjustmentBytes,
    IReadOnlyList<HybridCpuFrameSlotV2> Slots,
    IReadOnlyList<int> SavedRegisters,
    string Digest);

/// <summary>
/// Compiler-visible completion of the native function and fixed stack-frame ABI.
/// V1 remains immutable for evidence replay; this successor is the sole ABI authority
/// for Phase 20 and later native-function lowering. Runtime allocation/publication state
/// is deliberately absent.
/// </summary>
public sealed class HybridCpuNativeAbiContractV2
{
    public const string SchemaId = "hybridcpu.native-abi";
    public const int SchemaMajor = 2;
    public const int SchemaMinor = 0;
    public const string ArchitectureBinding = "HybridCPU-W8-native-v1+rv64-register-roles/v2";
    public const int ZeroRegister = 0;
    public const int ReturnAddressRegister = 1;
    public const int StackPointerRegister = 2;
    public const int GlobalPointerRegister = 3;
    public const int ThreadPointerRegister = 4;
    public const int FramePointerRegister = 8;
    public const int StackAlignmentBytes = 16;
    public const int RedZoneBytes = 0;
    public const int MaximumParameters = 256;
    public const int MaximumValueSizeBytes = 1 << 20;
    public const int MaximumFrameSlots = 4096;
    public const int MaximumFrameSizeBytes = 16 << 20;

    private static readonly int[] ArgumentRegisterTable = [10, 11, 12, 13, 14, 15, 16, 17];
    private static readonly int[] ReturnRegisterTable = [10, 11];
    private static readonly int[] CallerSavedRegisterTable = [1, 5, 6, 7, 10, 11, 12, 13, 14, 15, 16, 17, 28, 29, 30, 31];
    private static readonly int[] CalleeSavedRegisterTable = [8, 9, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27];
    private static readonly int[] ReservedRegisterTable = [0, 1, 2, 3, 4, 8];
    private static readonly int[] AllocatableRegisterTable =
        Enumerable.Range(0, HybridCpuTargetMachineContractV1.ArchitecturalRegisterCount)
            .Except(ReservedRegisterTable)
            .ToArray();

    public static HybridCpuNativeAbiContractV2 Default { get; } = new();

    private HybridCpuNativeAbiContractV2()
    {
        TargetContractDigest = HybridCpuTargetMachineContractV1.Default.ContractDigest;
        PredecessorPlatformContractDigest = HybridCpuTargetPlatformContractV1.Default.ContractDigest;
        ArgumentRegisters = Array.AsReadOnly(ArgumentRegisterTable);
        ReturnRegisters = Array.AsReadOnly(ReturnRegisterTable);
        CallerSavedRegisters = Array.AsReadOnly(CallerSavedRegisterTable);
        CalleeSavedRegisters = Array.AsReadOnly(CalleeSavedRegisterTable);
        ReservedRegisters = Array.AsReadOnly(ReservedRegisterTable);
        AllocatableRegisters = Array.AsReadOnly(AllocatableRegisterTable);
        ContractDigest = ComputeContractDigest();
    }

    public string TargetContractDigest { get; }
    public string PredecessorPlatformContractDigest { get; }
    public IReadOnlyList<int> ArgumentRegisters { get; }
    public IReadOnlyList<int> ReturnRegisters { get; }
    public IReadOnlyList<int> CallerSavedRegisters { get; }
    public IReadOnlyList<int> CalleeSavedRegisters { get; }
    public IReadOnlyList<int> ReservedRegisters { get; }
    public IReadOnlyList<int> AllocatableRegisters { get; }
    public HybridCpuStackGrowthV2 StackGrowth => HybridCpuStackGrowthV2.Down;
    public HybridCpuPlatformFactStatus FixedStackFrames => HybridCpuPlatformFactStatus.Supported;
    public HybridCpuPlatformFactStatus AggregatePassing => HybridCpuPlatformFactStatus.Supported;
    public HybridCpuPlatformFactStatus VarArgs => HybridCpuPlatformFactStatus.Unsupported;
    public HybridCpuPlatformFactStatus TailCalls => HybridCpuPlatformFactStatus.Unsupported;
    public HybridCpuPlatformFactStatus SpecialContourCalls => HybridCpuPlatformFactStatus.Unsupported;
    public HybridCpuPlatformFactStatus DynamicStackAllocation => HybridCpuPlatformFactStatus.Unsupported;
    public HybridCpuPlatformFactStatus StackProbing => HybridCpuPlatformFactStatus.Unsupported;
    public string ContractDigest { get; }

    public HybridCpuAbiLayoutV2 Classify(HybridCpuAbiSignatureV2 signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(signature.Parameters);
        if (signature.Parameters.Count > MaximumParameters ||
            signature.Parameters.Any(static value => value is { SizeBytes: > MaximumValueSizeBytes }) ||
            signature.ReturnValue is { SizeBytes: > MaximumValueSizeBytes })
            return AbiFailure(HybridCpuPlatformFactStatus.Unsupported,
                "The deterministic native ABI classification budget was exceeded.", signature);
        if (signature.IsVarArgs || signature.IsTailCall || signature.IsSpecialContourCall)
            return AbiFailure(HybridCpuPlatformFactStatus.Unsupported,
                "Varargs, tail calls and special-contour calls are outside the native ABI v2 slice.", signature);
        if (signature.Parameters.Any(static value => !IsValidValue(value)) ||
            signature.ReturnValue is { } result && !IsValidValue(result))
            return AbiFailure(HybridCpuPlatformFactStatus.Unknown,
                "The signature contains an unknown or malformed ABI value.", signature);
        if (signature.Parameters.Select(static value => value.Identity).Distinct(StringComparer.Ordinal).Count() != signature.Parameters.Count)
            return AbiFailure(HybridCpuPlatformFactStatus.Invalid,
                "Parameter identities must be unique.", signature);

        List<HybridCpuAbiLocationV2> parameters = new List<HybridCpuAbiLocationV2>(signature.Parameters.Count);
        int nextArgumentRegister = 0;
        int stackOffset = 0;
        foreach (HybridCpuAbiValueV2 value in signature.Parameters)
        {
            int registerCount = RegisterCount(value);
            if (registerCount <= 2 && nextArgumentRegister + registerCount <= ArgumentRegisterTable.Length)
            {
                int[] registers = ArgumentRegisterTable.Skip(nextArgumentRegister).Take(registerCount).ToArray();
                nextArgumentRegister += registerCount;
                parameters.Add(new(value.Identity,
                    registerCount == 1 ? HybridCpuAbiLocationKindV2.Register : HybridCpuAbiLocationKindV2.RegisterPair,
                    registers, null, value.SizeBytes));
                continue;
            }

            int stackAlignment = Math.Min(StackAlignmentBytes, Math.Max(8, value.AlignmentBytes));
            stackOffset = AlignUp(stackOffset, stackAlignment);
            parameters.Add(new(value.Identity, HybridCpuAbiLocationKindV2.Stack,
                Array.Empty<int>(), stackOffset, value.SizeBytes));
            stackOffset = checked(stackOffset + AlignUp(value.SizeBytes, 8));
        }

        HybridCpuAbiLocationV2? returnLocation = null;
        if (signature.ReturnValue is { } returnValue)
        {
            int count = RegisterCount(returnValue);
            returnLocation = count <= 2
                ? new(returnValue.Identity,
                    count == 1 ? HybridCpuAbiLocationKindV2.Register : HybridCpuAbiLocationKindV2.RegisterPair,
                    ReturnRegisterTable.Take(count).ToArray(), null, returnValue.SizeBytes)
                : new(returnValue.Identity, HybridCpuAbiLocationKindV2.Indirect,
                    [ArgumentRegisterTable[0]], null, returnValue.SizeBytes);
        }

        int stackBytes = AlignUp(stackOffset, StackAlignmentBytes);
        return new(HybridCpuPlatformFactStatus.Supported, "Native ABI v2 signature is classified.",
            parameters, returnLocation, stackBytes,
            DigestAbi(signature, parameters, returnLocation, stackBytes));
    }

    public HybridCpuFrameLayoutV2 LayoutFrame(HybridCpuFrameRequestV2 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Slots);
        ArgumentNullException.ThrowIfNull(request.SavedRegisters);
        if (request.Slots.Count > MaximumFrameSlots ||
            request.Slots.Any(static slot => slot is { SizeBytes: > MaximumValueSizeBytes }) ||
            request.Slots.Sum(static slot => (long)Math.Max(0, slot.SizeBytes) + Math.Max(0, slot.AlignmentBytes)) +
                (long)request.SavedRegisters.Count * 8 > MaximumFrameSizeBytes)
            return FrameFailure(HybridCpuPlatformFactStatus.Unsupported,
                "The deterministic fixed-frame layout budget was exceeded.", request);
        if (request.RequiresDynamicAllocation || request.RequiresStackProbe)
            return FrameFailure(HybridCpuPlatformFactStatus.Unsupported,
                "Dynamic stack allocation and stack probing are unsupported.", request);
        if (request.Slots.Any(static slot => string.IsNullOrWhiteSpace(slot.Identity) || slot.SizeBytes <= 0 ||
                slot.AlignmentBytes is not (1 or 2 or 4 or 8 or 16)) ||
            request.Slots.Select(static slot => slot.Identity).Distinct(StringComparer.Ordinal).Count() != request.Slots.Count)
            return FrameFailure(HybridCpuPlatformFactStatus.Unknown, "Frame slots are malformed or ambiguous.", request);
        if (request.SavedRegisters.Distinct().Count() != request.SavedRegisters.Count ||
            request.SavedRegisters.Any(register =>
                !CalleeSavedRegisterTable.Contains(register) &&
                !(request.SavesReturnAddress && register == ReturnAddressRegister)) ||
            request.SavesReturnAddress && !request.SavedRegisters.Contains(ReturnAddressRegister))
            return FrameFailure(HybridCpuPlatformFactStatus.Invalid,
                "Only unique ABI callee-saved registers and an explicitly requested return address may be saved in a native frame.", request);

        List<HybridCpuFrameSlotV2> slots = new List<HybridCpuFrameSlotV2>();
        int offset = 0;
        foreach (int register in request.SavedRegisters.Order())
        {
            offset = AlignUp(offset, 8);
            slots.Add(new($"saved:x{register.ToString(CultureInfo.InvariantCulture)}", offset, 8, 8));
            offset = checked(offset + 8);
        }
        foreach (HybridCpuFrameSlotRequestV2 slot in request.Slots.OrderBy(static slot => slot.Identity, StringComparer.Ordinal))
        {
            offset = AlignUp(offset, slot.AlignmentBytes);
            slots.Add(new(slot.Identity, offset, slot.SizeBytes, slot.AlignmentBytes));
            offset = checked(offset + slot.SizeBytes);
        }

        int frameSize = AlignUp(offset, StackAlignmentBytes);
        if (frameSize > MaximumFrameSizeBytes)
            return FrameFailure(HybridCpuPlatformFactStatus.Unsupported,
                "The deterministic fixed-frame size budget was exceeded.", request);
        return new(HybridCpuPlatformFactStatus.Supported, "Fixed native stack frame is laid out.",
            frameSize, -frameSize, frameSize, slots, request.SavedRegisters.Order().ToArray(),
            DigestFrame(request, slots, frameSize));
    }

    private static bool IsValidValue(HybridCpuAbiValueV2 value) =>
        value is not null && !string.IsNullOrWhiteSpace(value.Identity) && value.Kind != HybridCpuAbiValueKindV2.Unknown &&
        value.SizeBytes > 0 && value.AlignmentBytes is 1 or 2 or 4 or 8 or 16;

    private static int RegisterCount(HybridCpuAbiValueV2 value) => checked((value.SizeBytes + 7) / 8);

    private static int AlignUp(int value, int alignment) => checked((value + alignment - 1) / alignment * alignment);

    private HybridCpuAbiLayoutV2 AbiFailure(
        HybridCpuPlatformFactStatus status,
        string reason,
        HybridCpuAbiSignatureV2 signature) =>
        new(status, reason, Array.Empty<HybridCpuAbiLocationV2>(), null, 0,
            Hash($"abi-failure|{ContractDigest}|{status}|{reason}|{SignatureText(signature)}"));

    private HybridCpuFrameLayoutV2 FrameFailure(
        HybridCpuPlatformFactStatus status,
        string reason,
        HybridCpuFrameRequestV2 request) =>
        new(status, reason, 0, 0, 0, Array.Empty<HybridCpuFrameSlotV2>(), Array.Empty<int>(),
            Hash($"frame-failure|{ContractDigest}|{status}|{reason}|{FrameRequestText(request)}"));

    private string ComputeContractDigest() => Hash(string.Join('|',
        SchemaId, $"{SchemaMajor}.{SchemaMinor}", ArchitectureBinding, TargetContractDigest,
        PredecessorPlatformContractDigest, $"args={string.Join(',', ArgumentRegisterTable)}",
        $"returns={string.Join(',', ReturnRegisterTable)}", $"caller={string.Join(',', CallerSavedRegisterTable)}",
        $"callee={string.Join(',', CalleeSavedRegisterTable)}", $"reserved={string.Join(',', ReservedRegisterTable)}",
        $"allocatable={string.Join(',', AllocatableRegisterTable)}", "stack=down:16:redzone0:fixed",
        "aggregate=two-register-or-stack:large-return-indirect", "varargs=unsupported", "tail=unsupported",
        "special=unsupported", "dynamic=unsupported", "probe=unsupported",
        $"budgets={MaximumParameters}:{MaximumValueSizeBytes}:{MaximumFrameSlots}:{MaximumFrameSizeBytes}"));

    private string DigestAbi(
        HybridCpuAbiSignatureV2 signature,
        IReadOnlyList<HybridCpuAbiLocationV2> parameters,
        HybridCpuAbiLocationV2? result,
        int stackBytes) => Hash(string.Join('|', "abi-layout", ContractDigest, SignatureText(signature),
        string.Join(';', parameters.Select(LocationText)), result is null ? "void" : LocationText(result), stackBytes));

    private string DigestFrame(
        HybridCpuFrameRequestV2 request,
        IReadOnlyList<HybridCpuFrameSlotV2> slots,
        int frameSize) => Hash(string.Join('|', "frame-layout", ContractDigest, FrameRequestText(request), frameSize,
        string.Join(';', slots.Select(static slot =>
            $"{slot.Identity}:{slot.OffsetFromAdjustedStackPointerBytes}:{slot.SizeBytes}:{slot.AlignmentBytes}"))));

    private static string SignatureText(HybridCpuAbiSignatureV2 signature) => string.Join('|',
        string.Join(';', signature.Parameters.Select(ValueText)),
        signature.ReturnValue is null ? "void" : ValueText(signature.ReturnValue),
        signature.IsVarArgs, signature.IsTailCall, signature.IsSpecialContourCall);

    private static string FrameRequestText(HybridCpuFrameRequestV2 request) => string.Join('|',
        string.Join(';', request.Slots.OrderBy(static slot => slot.Identity, StringComparer.Ordinal)
            .Select(static slot => $"{slot.Identity}:{slot.SizeBytes}:{slot.AlignmentBytes}")),
        string.Join(',', request.SavedRegisters.Order()), request.RequiresDynamicAllocation, request.RequiresStackProbe,
        request.SavesReturnAddress);

    private static string ValueText(HybridCpuAbiValueV2 value) =>
        $"{value.Identity}:{value.Kind}:{value.SizeBytes}:{value.AlignmentBytes}";

    private static string LocationText(HybridCpuAbiLocationV2 location) =>
        $"{location.ValueIdentity}:{location.Kind}:{string.Join(',', location.Registers)}:{location.StackOffsetBytes}:{location.SizeBytes}";

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
