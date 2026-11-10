using System;
using System.Collections.Generic;
using System.Linq;

namespace HybridCPU.Compiler.Core.IR;

public enum CompilerVectorTransferPositiveEmissionKind : byte
{
    Vload = 0,
    Vstore = 1
}

public readonly record struct CompilerVectorTransferMemoryAddressAbi(ulong BaseAddress)
{
    public static CompilerVectorTransferMemoryAddressAbi Create(ulong baseAddress) =>
        new(baseAddress);
}

public readonly record struct CompilerVectorTransferShapeAbi(
    HybridCpuDataType ElementType,
    uint ElementCount,
    ushort StrideBytes,
    byte PredicateMask = 0)
{
    public static bool IsKnownVectorElementType(HybridCpuDataType elementType)
    {
        try
        {
            _ = HybridCpuDataTypes.SizeOf(elementType);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public static CompilerVectorTransferShapeAbi Create(
        HybridCpuDataType elementType,
        uint elementCount,
        ushort strideBytes,
        byte predicateMask = 0)
    {
        if (!IsKnownVectorElementType(elementType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(elementType),
                elementType,
                "Unknown vector transfer element data type.");
        }

        if (elementCount == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elementCount),
                elementCount,
                "Vector transfer helper requires a non-empty element count.");
        }

        ushort elementSizeBytes = checked((ushort)HybridCpuDataTypes.SizeOf(elementType));
        if (strideBytes < elementSizeBytes)
        {
            throw new ArgumentException(
                "Vector transfer helper requires stride bytes to cover the encoded element size.",
                nameof(strideBytes));
        }

        return new CompilerVectorTransferShapeAbi(
            elementType,
            elementCount,
            strideBytes,
            predicateMask);
    }

    public static CompilerVectorTransferShapeAbi CreateContiguous(
        HybridCpuDataType elementType,
        uint elementCount,
        byte predicateMask = 0)
    {
        if (!IsKnownVectorElementType(elementType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(elementType),
                elementType,
                "Unknown vector transfer element data type.");
        }

        ushort strideBytes = checked((ushort)HybridCpuDataTypes.SizeOf(elementType));
        return Create(elementType, elementCount, strideBytes, predicateMask);
    }

    public ulong EffectiveByteLength => checked((ulong)ElementCount * StrideBytes);

    public void Validate(string parameterName)
    {
        if (!IsKnownVectorElementType(ElementType))
        {
            throw new ArgumentOutOfRangeException(parameterName, ElementType, "Unknown vector transfer element data type.");
        }

        if (ElementCount == 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, ElementCount, "Vector transfer helper requires a non-empty element count.");
        }

        ushort elementSizeBytes = checked((ushort)HybridCpuDataTypes.SizeOf(ElementType));
        if (StrideBytes < elementSizeBytes)
        {
            throw new ArgumentException(
                "Vector transfer helper requires stride bytes to cover the encoded element size.",
                parameterName);
        }
    }
}

public sealed record CompilerVectorTransferEmissionRequest
{
    private CompilerVectorTransferEmissionRequest(
        CompilerVectorTransferPositiveEmissionKind kind,
        CompilerVectorTransferMemoryAddressAbi destination,
        CompilerVectorTransferMemoryAddressAbi source,
        CompilerVectorTransferShapeAbi shape)
    {
        Kind = kind;
        Destination = destination;
        Source = source;
        Shape = shape;
    }

    public CompilerVectorTransferPositiveEmissionKind Kind { get; }

    public CompilerVectorTransferMemoryAddressAbi Destination { get; }

    public CompilerVectorTransferMemoryAddressAbi Source { get; }

    public CompilerVectorTransferShapeAbi Shape { get; }

    public HybridCpuOpcode Opcode => CompilerVectorTransferPositiveEmissionAbiContract.GetOpcode(Kind);

    public string Mnemonic => CompilerVectorTransferPositiveEmissionAbiContract.GetMnemonic(Kind);

    public static CompilerVectorTransferEmissionRequest Vload(
        CompilerVectorTransferMemoryAddressAbi destination,
        CompilerVectorTransferMemoryAddressAbi source,
        CompilerVectorTransferShapeAbi shape) =>
        new(
            CompilerVectorTransferPositiveEmissionKind.Vload,
            destination,
            source,
            shape);

    public static CompilerVectorTransferEmissionRequest Vstore(
        CompilerVectorTransferMemoryAddressAbi source,
        CompilerVectorTransferMemoryAddressAbi destination,
        CompilerVectorTransferShapeAbi shape) =>
        new(
            CompilerVectorTransferPositiveEmissionKind.Vstore,
            destination,
            source,
            shape);
}

public sealed record CompilerVectorTransferEmissionPlan(
    CompilerVectorTransferEmissionRequest Request,
    string RuntimeHandoffReference,
    HybridCpuOpcodeInfo RuntimeOpcodeInfo,
    HybridCpuInstructionWord EncodedInstruction,
    bool UsesFallbackPath,
    bool UsesAliasPromotion,
    bool UsesScalarVectorDotOrBackendFallback,
    bool UsesBaseMemoryFallback,
    bool UsesBaseVectorFallback,
    bool UsesScalarHelperFallback,
    bool UsesWideningFmaFallback,
    bool UsesVectorTransposeOrSegmentFallback)
{
    public bool RuntimeOwnedLegalityIsFinal => true;
}

public readonly record struct CompilerVectorTransferPositiveEmissionRow(
    string Mnemonic,
    HybridCpuOpcode Opcode,
    ushort NumericOpcode,
    CompilerVectorTransferPositiveEmissionKind Kind,
    string HelperName,
    string RequiredTypedOperandContract,
    bool UsesRuntimeHandoff,
    bool RuntimeOwnedLegalityIsFinal,
    bool EmitsDirectVectorTransferOpcode,
    bool UsesFallbackPath,
    bool UsesAliasPromotion);

public static class CompilerVectorTransferPositiveEmissionAbiContract
{
    private static readonly CompilerVectorTransferPositiveEmissionRow[] RowTable =
    [
        Create(
            "VLOAD",
            HybridCpuOpcode.VLOAD,
            CompilerVectorTransferPositiveEmissionKind.Vload,
            "CompileVload",
            "destination memory base, source memory base, vector transfer shape ABI"),
        Create(
            "VSTORE",
            HybridCpuOpcode.VSTORE,
            CompilerVectorTransferPositiveEmissionKind.Vstore,
            "CompileVstore",
            "source memory base, destination memory base, vector transfer shape ABI")
    ];

    public const string CompilerPositiveEmissionDecision =
        "CompilerOwnedVectorTransferPositiveEmissionOpenedFromTypedHelperABI";

    public const string RuntimeHandoffAuthorityDecision =
        "OpcodeRegistryAndInstructionRegistryRemainRuntimeAuthorityForVectorTransferHandoff";

    public const string NoFallbackDecision =
        "DirectVectorTransferEmissionNoBaseMemoryBaseVectorScalarDotWideningFmaTransposeSegmentLane6Lane7VmxOrBackendFallback";

    public const string RuntimeHandoffReference = "InstructionRegistry.RegisterVectorTransferOp";

    public const bool HasCurrentCompilerImplementation = true;
    public const bool HasCurrentCompilerHelper = true;
    public const bool HasCurrentCompilerEmission = true;
    public const bool UsesRuntimeHandoff = true;
    public const bool RuntimeOwnedLegalityIsFinal = true;
    public const bool AllowsCompilerToOverrideRuntimeLegality = false;
    public const bool UsesFallbackPath = false;
    public const bool UsesAliasPromotion = false;
    public const bool EmitsDirectVectorTransferOpcode = true;

    public static IReadOnlyList<CompilerVectorTransferPositiveEmissionRow> Rows => RowTable;

    public static IReadOnlySet<string> PublicHelperNames { get; } =
        new HashSet<string>(
            RowTable.Select(static row => row.HelperName)
                .Concat(
                [
                    "VLoad",
                    "VStore",
                    "CompileVload",
                    "CompileVstore",
                    "CompileVloadWithDecision",
                    "CompileVstoreWithDecision"
                ]),
            StringComparer.Ordinal);

    public static bool IsVectorTransferPositiveOpcode(uint opCode) =>
        opCode <= ushort.MaxValue &&
        Enum.IsDefined(typeof(HybridCpuOpcode), (ushort)opCode) &&
        IsVectorTransferPositiveOpcode((HybridCpuOpcode)opCode);

    public static bool IsVectorTransferPositiveOpcode(HybridCpuOpcode opcode) =>
        opcode is HybridCpuOpcode.VLOAD or HybridCpuOpcode.VSTORE;

    public static HybridCpuOpcode GetOpcode(CompilerVectorTransferPositiveEmissionKind kind) =>
        kind switch
        {
            CompilerVectorTransferPositiveEmissionKind.Vload => HybridCpuOpcode.VLOAD,
            CompilerVectorTransferPositiveEmissionKind.Vstore => HybridCpuOpcode.VSTORE,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown vector transfer compiler helper kind.")
        };

    public static string GetMnemonic(CompilerVectorTransferPositiveEmissionKind kind) =>
        kind switch
        {
            CompilerVectorTransferPositiveEmissionKind.Vload => "VLOAD",
            CompilerVectorTransferPositiveEmissionKind.Vstore => "VSTORE",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown vector transfer compiler helper kind.")
        };

    public static CompilerVectorTransferPositiveEmissionRow GetRow(string mnemonic)
    {
        foreach (CompilerVectorTransferPositiveEmissionRow row in RowTable)
        {
            if (string.Equals(row.Mnemonic, mnemonic, StringComparison.Ordinal))
            {
                return row;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(mnemonic), mnemonic, "Unknown vector transfer compiler emission row.");
    }

    public static void RequireRuntimeHandoffAuthority(string mnemonic)
    {
        if (string.IsNullOrWhiteSpace(mnemonic))
        {
            throw new ArgumentException("Vector transfer compiler helper requires a mnemonic.", nameof(mnemonic));
        }

        GetRow(mnemonic);

        HybridCpuOpcode opcode = mnemonic switch
        {
            "VLOAD" => HybridCpuOpcode.VLOAD,
            "VSTORE" => HybridCpuOpcode.VSTORE,
            _ => throw new ArgumentOutOfRangeException(nameof(mnemonic), mnemonic, "Unknown vector transfer compiler emission row.")
        };

        HybridCpuOpcodeInfo? opcodeInfo = HybridCpuOpcodeCatalog.GetInfo(opcode);
        if (!opcodeInfo.HasValue || opcodeInfo.Value.InstructionClass != HybridCpuInstructionClass.Memory)
        {
            throw new InvalidOperationException(
                $"Vector transfer compiler helper requires runtime opcode authority for {mnemonic} before emission.");
        }

        _ = opcodeInfo.Value;
    }

    private static CompilerVectorTransferPositiveEmissionRow Create(
        string mnemonic,
        HybridCpuOpcode opcode,
        CompilerVectorTransferPositiveEmissionKind kind,
        string helperName,
        string requiredTypedOperandContract)
    {
        return new CompilerVectorTransferPositiveEmissionRow(
            mnemonic,
            opcode,
            checked((ushort)opcode),
            kind,
            helperName,
            requiredTypedOperandContract,
            UsesRuntimeHandoff: true,
            RuntimeOwnedLegalityIsFinal: true,
            EmitsDirectVectorTransferOpcode: true,
            UsesFallbackPath: false,
            UsesAliasPromotion: false);
    }
}
