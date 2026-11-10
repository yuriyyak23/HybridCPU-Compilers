using System;
using System.Collections.Generic;
using System.Linq;

namespace HybridCPU.Compiler.Core.IR;

public enum CompilerMatrixTilePositiveEmissionKind : byte
{
    MtileLoad = 0,
    MtileStore = 1,
    MtileMacc = 2,
    Mtranspose = 3
}

public readonly record struct CompilerMatrixTileTileOperand(ushort TileId)
{
    public static CompilerMatrixTileTileOperand Create(ushort tileId) => new(tileId);
}

public readonly record struct CompilerMatrixTileDescriptorAbi(
    MatrixTileCanonicalDescriptorAbi CanonicalDescriptor,
    HybridCpuDataType ElementType)
{
    public static bool IsKnownMatrixTileElementType(HybridCpuDataType elementType)
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

    public static CompilerMatrixTileDescriptorAbi Create(
        ushort rows,
        ushort columns,
        HybridCpuDataType elementType,
        uint strideBytes = 0)
    {
        if (!IsKnownMatrixTileElementType(elementType))
        {
            throw new ArgumentOutOfRangeException(nameof(elementType), elementType, "Unknown MTILE element data type.");
        }

        ushort elementSizeBytes = checked((ushort)HybridCpuDataTypes.SizeOf(elementType));
        uint effectiveStride = strideBytes == 0
            ? checked((uint)columns * elementSizeBytes)
            : strideBytes;
        return new CompilerMatrixTileDescriptorAbi(
            MatrixTileCanonicalDescriptorAbi.Create(
                rows,
                columns,
                elementSizeBytes,
                effectiveStride),
            elementType);
    }

    public void Validate(string parameterName)
    {
        if (!IsKnownMatrixTileElementType(ElementType))
        {
            throw new ArgumentOutOfRangeException(parameterName, ElementType, "Unknown MTILE element data type.");
        }

        if (!CanonicalDescriptor.IsCanonical)
        {
            throw new ArgumentException(
                "MTILE compiler emission requires a canonical runtime tile descriptor.",
                parameterName);
        }

        ushort elementSizeBytes = checked((ushort)HybridCpuDataTypes.SizeOf(ElementType));
        if (CanonicalDescriptor.ElementSizeBytes != elementSizeBytes)
        {
            throw new ArgumentException(
                "MTILE compiler emission requires descriptor element size to match the encoded data type.",
                parameterName);
        }
    }
}

public readonly record struct CompilerMatrixTileMemoryFaultAbiInputs(
    ulong BaseAddress,
    ushort PageSizeBytes)
{
    public static CompilerMatrixTileMemoryFaultAbiInputs Create(
        ulong baseAddress,
        ushort pageSizeBytes = MatrixTileMemoryShapeAndFaultAbi.DefaultPageSizeBytes) =>
        new(baseAddress, pageSizeBytes);
}

public readonly record struct CompilerMatrixTileAccumulatorPolicyAbi(
    MatrixTileCanonicalDescriptorAbi RightSourceDescriptor,
    MatrixTileCanonicalDescriptorAbi AccumulatorDescriptor,
    MatrixTileNumericElementKind ElementKind,
    MatrixTileAccumulatorPolicyKind AccumulatorPolicy)
{
    public MatrixTileNumericPolicy? MatrixTileNumericPolicy { get; init; }

    public MatrixTileLayoutPolicy? MatrixTileLayoutPolicy { get; init; }

    public static CompilerMatrixTileAccumulatorPolicyAbi CreateForRuntimeDerivedFootprint(
        MatrixTileCanonicalDescriptorAbi leftSourceDescriptor,
        MatrixTileNumericElementKind elementKind = MatrixTileNumericElementKind.SignedInteger)
    {
        MatrixTileNumericPolicy numericPolicy =
            CompilerMatrixTilePositiveEmissionAbiContract.CreateRuntimeNumericPolicy(
                leftSourceDescriptor.ElementSizeBytes,
                elementKind);
        return CreateForRuntimeDerivedFootprint(leftSourceDescriptor, numericPolicy);
    }

    public static CompilerMatrixTileAccumulatorPolicyAbi CreateForRuntimeDerivedFootprint(
        MatrixTileCanonicalDescriptorAbi leftSourceDescriptor,
        MatrixTileNumericPolicy numericPolicy,
        MatrixTileLayoutPolicy? layoutPolicy = null)
    {
        ushort sourceElementSize = checked((ushort)
            MatrixTileNumericPolicyAbi.GetElementSizeBytes(numericPolicy.ElementType));
        if (leftSourceDescriptor.ElementSizeBytes != sourceElementSize)
        {
            throw new ArgumentException(
                "MTILE_MACC compiler sideband numeric policy must match the left source descriptor element size.",
                nameof(numericPolicy));
        }

        ushort accumulatorElementSize =
            checked((ushort)MatrixTileNumericPolicyAbi.GetElementSizeBytes(
                numericPolicy.AccumulatorType));
        MatrixTileCanonicalDescriptorAbi rightDescriptor =
            MatrixTileCanonicalDescriptorAbi.Create(
                leftSourceDescriptor.Columns,
                leftSourceDescriptor.Rows,
                leftSourceDescriptor.ElementSizeBytes,
                checked((uint)leftSourceDescriptor.Rows * leftSourceDescriptor.ElementSizeBytes));
        MatrixTileCanonicalDescriptorAbi accumulatorDescriptor =
            MatrixTileCanonicalDescriptorAbi.Create(
                leftSourceDescriptor.Rows,
                rightDescriptor.Columns,
                accumulatorElementSize,
                checked((uint)rightDescriptor.Columns * accumulatorElementSize));

        return new CompilerMatrixTileAccumulatorPolicyAbi(
            rightDescriptor,
            accumulatorDescriptor,
            CompilerMatrixTilePositiveEmissionAbiContract.GetElementKind(numericPolicy),
            CompilerMatrixTilePositiveEmissionAbiContract.GetAccumulatorPolicy(numericPolicy))
        {
            MatrixTileNumericPolicy = numericPolicy,
            MatrixTileLayoutPolicy = layoutPolicy ?? MatrixTileLayoutPolicyAbi.CreateMaccPolicy()
        };
    }
}

public readonly record struct CompilerMatrixTileTransposePolicyAbi(
    MatrixTileCanonicalDescriptorAbi DestinationDescriptor,
    MatrixTileTransposeAliasPolicyKind AliasPolicy)
{
    public MatrixTileLayoutPolicy? MatrixTileLayoutPolicy { get; init; }

    public static CompilerMatrixTileTransposePolicyAbi CreateForRuntimeDerivedDestination(
        MatrixTileCanonicalDescriptorAbi sourceDescriptor)
    {
        MatrixTileCanonicalDescriptorAbi destinationDescriptor =
            MatrixTileCanonicalDescriptorAbi.Create(
                sourceDescriptor.Columns,
                sourceDescriptor.Rows,
                sourceDescriptor.ElementSizeBytes,
                checked((uint)sourceDescriptor.Rows * sourceDescriptor.ElementSizeBytes));

        return new CompilerMatrixTileTransposePolicyAbi(
            destinationDescriptor,
            MatrixTileTransposeAliasPolicyKind.OutOfPlaceOrSquareInPlaceOnly)
        {
            MatrixTileLayoutPolicy = MatrixTileLayoutPolicyAbi.CreateTransposePolicy()
        };
    }
}

public sealed record CompilerMatrixTileEmissionRequest
{
    private CompilerMatrixTileEmissionRequest(
        CompilerMatrixTilePositiveEmissionKind kind,
        CompilerMatrixTileDescriptorAbi descriptor,
        CompilerMatrixTileTileOperand primaryTile,
        CompilerMatrixTileTileOperand secondaryTile,
        CompilerMatrixTileTileOperand destinationTile,
        CompilerMatrixTileMemoryFaultAbiInputs? memoryFaultAbi,
        CompilerMatrixTileAccumulatorPolicyAbi? accumulatorPolicyAbi,
        CompilerMatrixTileTransposePolicyAbi? transposePolicyAbi)
    {
        Kind = kind;
        Descriptor = descriptor;
        PrimaryTile = primaryTile;
        SecondaryTile = secondaryTile;
        DestinationTile = destinationTile;
        MemoryFaultAbi = memoryFaultAbi;
        AccumulatorPolicyAbi = accumulatorPolicyAbi;
        TransposePolicyAbi = transposePolicyAbi;
    }

    public CompilerMatrixTilePositiveEmissionKind Kind { get; }

    public CompilerMatrixTileDescriptorAbi Descriptor { get; }

    public CompilerMatrixTileTileOperand PrimaryTile { get; }

    public CompilerMatrixTileTileOperand SecondaryTile { get; }

    public CompilerMatrixTileTileOperand DestinationTile { get; }

    public CompilerMatrixTileMemoryFaultAbiInputs? MemoryFaultAbi { get; }

    public CompilerMatrixTileAccumulatorPolicyAbi? AccumulatorPolicyAbi { get; }

    public CompilerMatrixTileTransposePolicyAbi? TransposePolicyAbi { get; }

    public MatrixTileNumericPolicy? MatrixTileNumericPolicy =>
        Kind == CompilerMatrixTilePositiveEmissionKind.MtileMacc
            ? AccumulatorPolicyAbi?.MatrixTileNumericPolicy
            : null;

    public MatrixTileLayoutPolicy? MatrixTileLayoutPolicy => Kind switch
    {
        CompilerMatrixTilePositiveEmissionKind.MtileMacc =>
            AccumulatorPolicyAbi?.MatrixTileLayoutPolicy,
        CompilerMatrixTilePositiveEmissionKind.Mtranspose =>
            TransposePolicyAbi?.MatrixTileLayoutPolicy,
        _ => null
    };

    public HybridCpuOpcode Opcode => CompilerMatrixTilePositiveEmissionAbiContract.GetOpcode(Kind);

    public string Mnemonic => CompilerMatrixTilePositiveEmissionAbiContract.GetMnemonic(Kind);

    public static CompilerMatrixTileEmissionRequest MtileLoad(
        CompilerMatrixTileTileOperand destinationTile,
        CompilerMatrixTileDescriptorAbi descriptor,
        CompilerMatrixTileMemoryFaultAbiInputs memoryFaultAbi) =>
        new(
            CompilerMatrixTilePositiveEmissionKind.MtileLoad,
            descriptor,
            primaryTile: default,
            secondaryTile: destinationTile,
            destinationTile,
            memoryFaultAbi,
            accumulatorPolicyAbi: null,
            transposePolicyAbi: null);

    public static CompilerMatrixTileEmissionRequest MtileStore(
        CompilerMatrixTileTileOperand sourceTile,
        CompilerMatrixTileDescriptorAbi descriptor,
        CompilerMatrixTileMemoryFaultAbiInputs memoryFaultAbi) =>
        new(
            CompilerMatrixTilePositiveEmissionKind.MtileStore,
            descriptor,
            primaryTile: sourceTile,
            secondaryTile: sourceTile,
            destinationTile: default,
            memoryFaultAbi,
            accumulatorPolicyAbi: null,
            transposePolicyAbi: null);

    public static CompilerMatrixTileEmissionRequest MtileMacc(
        CompilerMatrixTileTileOperand leftSourceTile,
        CompilerMatrixTileTileOperand rightSourceTile,
        CompilerMatrixTileTileOperand accumulatorTile,
        CompilerMatrixTileDescriptorAbi leftSourceDescriptor,
        CompilerMatrixTileAccumulatorPolicyAbi accumulatorPolicyAbi) =>
        new(
            CompilerMatrixTilePositiveEmissionKind.MtileMacc,
            leftSourceDescriptor,
            primaryTile: leftSourceTile,
            secondaryTile: rightSourceTile,
            destinationTile: accumulatorTile,
            memoryFaultAbi: null,
            accumulatorPolicyAbi,
            transposePolicyAbi: null);

    public static CompilerMatrixTileEmissionRequest Mtranspose(
        CompilerMatrixTileTileOperand sourceTile,
        CompilerMatrixTileTileOperand destinationTile,
        CompilerMatrixTileDescriptorAbi sourceDescriptor,
        CompilerMatrixTileTransposePolicyAbi transposePolicyAbi) =>
        new(
            CompilerMatrixTilePositiveEmissionKind.Mtranspose,
            sourceDescriptor,
            primaryTile: sourceTile,
            secondaryTile: destinationTile,
            destinationTile,
            memoryFaultAbi: null,
            accumulatorPolicyAbi: null,
            transposePolicyAbi);
}

public sealed record CompilerMatrixTileEmissionPlan(
    CompilerMatrixTileEmissionRequest Request,
    MatrixTileCompilerEmissionHandoffRow RuntimeHandoffRow,
    HybridCpuInstructionWord EncodedInstruction,
    MatrixTileInstructionIrProjection RuntimeProjection,
    MatrixTileMaterializedInstruction RuntimeMaterialization,
    bool UsesFallbackPath,
    bool UsesAliasPromotion,
    bool UsesScalarVectorDotOrBackendFallback)
{
    public MatrixTileMemoryShapeValidationResult? MemoryValidation =>
        RuntimeProjection.MemoryValidation;

    public MatrixTileSemanticValidationResult? SemanticValidation =>
        RuntimeProjection.SemanticValidation;

    public MatrixTileNumericPolicy? MatrixTileNumericPolicy =>
        Request.MatrixTileNumericPolicy;

    public MatrixTileLayoutPolicy? MatrixTileLayoutPolicy =>
        Request.MatrixTileLayoutPolicy;
}

public readonly record struct CompilerMatrixTilePositiveEmissionRow(
    string Mnemonic,
    HybridCpuOpcode Opcode,
    ushort NumericOpcode,
    CompilerMatrixTilePositiveEmissionKind Kind,
    string HelperName,
    string RequiredTypedOperandContract,
    bool UsesPhase13RuntimeHandoff,
    bool RuntimeOwnedLegalityIsFinal,
    bool EmitsDirectMatrixTileOpcode,
    bool UsesFallbackPath,
    bool UsesAliasPromotion);

public static class CompilerMatrixTilePositiveEmissionAbiContract
{
    private static readonly CompilerMatrixTilePositiveEmissionRow[] RowTable =
    [
        Create(
            "MTILE_LOAD",
            HybridCpuOpcode.MTILE_LOAD,
            CompilerMatrixTilePositiveEmissionKind.MtileLoad,
            "CompileMtileLoad",
            "tile destination, canonical tile descriptor, tile memory/fault ABI inputs"),
        Create(
            "MTILE_STORE",
            HybridCpuOpcode.MTILE_STORE,
            CompilerMatrixTilePositiveEmissionKind.MtileStore,
            "CompileMtileStore",
            "tile source, canonical tile descriptor, tile memory/fault ABI inputs"),
        Create(
            "MTILE_MACC",
            HybridCpuOpcode.MTILE_MACC,
            CompilerMatrixTilePositiveEmissionKind.MtileMacc,
            "CompileMtileMacc",
            "source tiles, accumulator tile/result footprint, accumulator policy ABI"),
        Create(
            "MTRANSPOSE",
            HybridCpuOpcode.MTRANSPOSE,
            CompilerMatrixTilePositiveEmissionKind.Mtranspose,
            "CompileMtranspose",
            "source/destination tile operands, transpose policy ABI")
    ];

    public const string CompilerPositiveEmissionDecision = "CompilerOwnedMatrixTilePositiveEmissionOpenedFromPhase13Handoff";
    public const string RuntimeHandoffAuthorityDecision = "Phase13RuntimeIsaHandoffPackageIsOpcodeAndLegalityAuthority";
    public const string LegacyOptionalDisabledBoundaryDecision = "LegacyOptionalDisabledCompilerMatrixTileContractIsNotPositiveHelperAuthority";
    public const string NoFallbackDecision = "DirectMatrixTileEmissionNoScalarVectorDotLane6Lane7VmxOrBackendFallback";

    public const bool HasCurrentCompilerImplementation = true;
    public const bool HasCurrentCompilerHelper = true;
    public const bool HasCurrentCompilerEmission = true;
    public const bool UsesPhase13RuntimeHandoff = true;
    public const bool RuntimeOwnedLegalityIsFinal = true;
    public const bool AllowsCompilerToOverrideRuntimeLegality = false;
    public const bool UsesOldOptionalDisabledMetadataAsAuthority = false;
    public const bool UsesFallbackPath = false;
    public const bool UsesAliasPromotion = false;
    public const bool EmitsDirectMatrixTileOpcodes = true;
    public const bool CarriesRuntimeOwnedMatrixTilePolicySidebands = true;

    public static IReadOnlyList<CompilerMatrixTilePositiveEmissionRow> Rows => RowTable;

    public static IReadOnlySet<string> PublicHelperNames { get; } =
        new HashSet<string>(
            RowTable.Select(static row => row.HelperName)
                .Concat(
                [
                    "CompileMtileLoadWithDecision",
                    "CompileMtileStoreWithDecision",
                    "CompileMtileMaccWithDecision",
                    "CompileMtransposeWithDecision",
                    "MtileLoad",
                    "MtileStore",
                    "MtileMacc",
                    "Mtranspose"
                ]),
            StringComparer.Ordinal);

    public static bool IsMatrixTilePositiveOpcode(uint opCode) =>
        opCode <= ushort.MaxValue &&
        Enum.IsDefined(typeof(HybridCpuOpcode), (ushort)opCode) &&
        IsMatrixTilePositiveOpcode((HybridCpuOpcode)opCode);

    public static bool IsMatrixTilePositiveOpcode(HybridCpuOpcode opcode) =>
        opcode is HybridCpuOpcode.MTILE_LOAD or
            HybridCpuOpcode.MTILE_STORE or
            HybridCpuOpcode.MTILE_MACC or
            HybridCpuOpcode.MTRANSPOSE;

    public static HybridCpuOpcode GetOpcode(CompilerMatrixTilePositiveEmissionKind kind) =>
        kind switch
        {
            CompilerMatrixTilePositiveEmissionKind.MtileLoad => HybridCpuOpcode.MTILE_LOAD,
            CompilerMatrixTilePositiveEmissionKind.MtileStore => HybridCpuOpcode.MTILE_STORE,
            CompilerMatrixTilePositiveEmissionKind.MtileMacc => HybridCpuOpcode.MTILE_MACC,
            CompilerMatrixTilePositiveEmissionKind.Mtranspose => HybridCpuOpcode.MTRANSPOSE,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown MTILE compiler helper kind.")
        };

    public static MatrixTileNumericPolicy CreateRuntimeNumericPolicy(
        HybridCpuDataType elementType)
    {
        MatrixTileNumericProfileId profileId = elementType switch
        {
            HybridCpuDataType.INT8 => MatrixTileNumericProfileId.SignedInt8ToInt32,
            HybridCpuDataType.UINT8 => MatrixTileNumericProfileId.UnsignedInt8ToUInt32,
            HybridCpuDataType.INT16 => MatrixTileNumericProfileId.SignedInt16ToInt32,
            HybridCpuDataType.UINT16 => MatrixTileNumericProfileId.UnsignedInt16ToUInt32,
            HybridCpuDataType.INT32 => MatrixTileNumericProfileId.SignedInt32ToInt64,
            HybridCpuDataType.UINT32 => MatrixTileNumericProfileId.UnsignedInt32ToUInt64,
            HybridCpuDataType.INT64 => MatrixTileNumericProfileId.SignedInt64ToInt64,
            HybridCpuDataType.UINT64 => MatrixTileNumericProfileId.UnsignedInt64ToUInt64,
            HybridCpuDataType.FLOAT32 => MatrixTileNumericProfileId.Binary32ToBinary32,
            HybridCpuDataType.FLOAT64 => MatrixTileNumericProfileId.Binary64ToBinary64,
            _ => MatrixTileNumericProfileId.Unspecified
        };

        if (profileId == MatrixTileNumericProfileId.Unspecified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elementType),
                elementType,
                "Unsupported MTILE_MACC runtime numeric policy element type.");
        }

        return MatrixTileNumericPolicyAbi.CreateSupportedPolicy(profileId);
    }

    public static MatrixTileNumericPolicy CreateRuntimeNumericPolicy(
        ushort elementSizeBytes,
        MatrixTileNumericElementKind elementKind)
    {
        HybridCpuDataType elementType = (elementKind, elementSizeBytes) switch
        {
            (MatrixTileNumericElementKind.SignedInteger, 1) => HybridCpuDataType.INT8,
            (MatrixTileNumericElementKind.SignedInteger, 2) => HybridCpuDataType.INT16,
            (MatrixTileNumericElementKind.SignedInteger, 4) => HybridCpuDataType.INT32,
            (MatrixTileNumericElementKind.SignedInteger, 8) => HybridCpuDataType.INT64,
            (MatrixTileNumericElementKind.UnsignedInteger, 1) => HybridCpuDataType.UINT8,
            (MatrixTileNumericElementKind.UnsignedInteger, 2) => HybridCpuDataType.UINT16,
            (MatrixTileNumericElementKind.UnsignedInteger, 4) => HybridCpuDataType.UINT32,
            (MatrixTileNumericElementKind.UnsignedInteger, 8) => HybridCpuDataType.UINT64,
            (MatrixTileNumericElementKind.FloatingPoint, 4) => HybridCpuDataType.FLOAT32,
            (MatrixTileNumericElementKind.FloatingPoint, 8) => HybridCpuDataType.FLOAT64,
            _ => throw new ArgumentOutOfRangeException(
                nameof(elementKind),
                elementKind,
                "Unsupported MTILE_MACC runtime numeric policy element kind/size.")
        };

        return CreateRuntimeNumericPolicy(elementType);
    }

    public static MatrixTileNumericElementKind GetElementKind(
        MatrixTileNumericPolicy numericPolicy) =>
        numericPolicy.Signedness switch
        {
            MatrixTileNumericSignedness.Signed => MatrixTileNumericElementKind.SignedInteger,
            MatrixTileNumericSignedness.Unsigned => MatrixTileNumericElementKind.UnsignedInteger,
            MatrixTileNumericSignedness.NotApplicable => MatrixTileNumericElementKind.FloatingPoint,
            _ => MatrixTileNumericElementKind.Unspecified
        };

    public static MatrixTileAccumulatorPolicyKind GetAccumulatorPolicy(
        MatrixTileNumericPolicy numericPolicy) =>
        numericPolicy.Signedness == MatrixTileNumericSignedness.NotApplicable
            ? MatrixTileAccumulatorPolicyKind.PreserveBinaryFloatingAccumulator
            : MatrixTileAccumulatorPolicyKind.WideningIntegerAccumulatorWithOverflowTrap;

    public static string GetMnemonic(CompilerMatrixTilePositiveEmissionKind kind) =>
        kind switch
        {
            CompilerMatrixTilePositiveEmissionKind.MtileLoad => "MTILE_LOAD",
            CompilerMatrixTilePositiveEmissionKind.MtileStore => "MTILE_STORE",
            CompilerMatrixTilePositiveEmissionKind.MtileMacc => "MTILE_MACC",
            CompilerMatrixTilePositiveEmissionKind.Mtranspose => "MTRANSPOSE",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown MTILE compiler helper kind.")
        };

    public static CompilerMatrixTilePositiveEmissionRow GetRow(string mnemonic)
    {
        foreach (CompilerMatrixTilePositiveEmissionRow row in RowTable)
        {
            if (string.Equals(row.Mnemonic, mnemonic, StringComparison.Ordinal))
            {
                return row;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(mnemonic), mnemonic, "Unknown MTILE compiler emission row.");
    }

    public static void RequireRuntimeHandoffAuthority(string mnemonic)
    {
        MatrixTileRuntimeIsaPackageContract.RequirePositiveCompilerEmissionReadiness();
        MatrixTileCompilerEmissionHandoffPackage.RequireRuntimeExecutableAuthority(mnemonic);
    }

    private static CompilerMatrixTilePositiveEmissionRow Create(
        string mnemonic,
        HybridCpuOpcode opcode,
        CompilerMatrixTilePositiveEmissionKind kind,
        string helperName,
        string requiredTypedOperandContract)
    {
        return new CompilerMatrixTilePositiveEmissionRow(
            mnemonic,
            opcode,
            checked((ushort)opcode),
            kind,
            helperName,
            requiredTypedOperandContract,
            UsesPhase13RuntimeHandoff: true,
            RuntimeOwnedLegalityIsFinal: true,
            EmitsDirectMatrixTileOpcode: true,
            UsesFallbackPath: false,
            UsesAliasPromotion: false);
    }
}
