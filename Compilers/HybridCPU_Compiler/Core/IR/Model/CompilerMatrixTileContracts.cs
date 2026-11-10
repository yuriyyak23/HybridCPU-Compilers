using System;

namespace HybridCPU.Compiler.Core.IR.Matrix;

public enum MatrixTileDescriptorLayoutKind : byte { Unspecified, RowMajor }
public enum MatrixTileNumericElementKind : byte { Unspecified, SignedInteger, UnsignedInteger, FloatingPoint }
public enum MatrixTileAccumulatorPolicyKind : byte { Unspecified, WideningIntegerAccumulatorWithOverflowTrap, PreserveBinaryFloatingAccumulator }
public enum MatrixTileTransposeAliasPolicyKind : byte { Unspecified, OutOfPlaceOrSquareInPlaceOnly }
public enum MatrixTileProjectedOperationKind : byte { Unspecified, Load, Store, Macc, Transpose }
public enum MatrixTileMemoryOperationKind : byte { Unspecified, Load, Store }
public enum MatrixTileEffectiveAddressSourceKind : byte { Unspecified, ExplicitRuntimeTileMemoryOperand }
public enum MatrixTileMemoryOrderingPolicyKind : byte { Unspecified, RetireOrderedAllOrNone }
public enum MatrixTileMemoryPublicationPolicyKind : byte { Unspecified, RetireStagedLoadPublication, RetireStagedStoreCommit }
public enum MatrixTileMemoryFaultKind : byte
{
    None, UnsupportedOperation, UnsupportedEffectiveAddressSource, ZeroDescriptor, ReservedDescriptor,
    UnsupportedElementSize, RowByteOverflow, StrideTooSmall, AlignmentFault, AddressOverflow,
    InvalidPageSize, PartialMemoryFault
}
public enum MatrixTileSemanticFaultKind : byte
{
    None, ZeroDescriptor, ReservedDescriptor, UnsupportedElementSize, UnsupportedElementKind,
    MaccInnerDimensionMismatch, MaccAccumulatorShapeMismatch, MaccAccumulatorElementSizeMismatch,
    TransposeShapeMismatch, TransposeElementSizeMismatch, TransposeInPlaceRequiresSquareShape,
    TransposeInPlaceDescriptorMismatch, MissingNumericPolicy, InvalidNumericPolicy,
    NumericPolicyDescriptorMismatch, UnsupportedAccumulatorPolicy, MissingLayoutPolicy,
    InvalidLayoutPolicy, LayoutPolicyDescriptorMismatch
}
public enum MatrixTileIrProjectionFaultKind : byte { None, UnsupportedOpcode, InvalidDescriptor, InvalidMemoryShape, InvalidSemanticPolicy }

public readonly record struct MatrixTileCanonicalDescriptorAbi(
    ushort Rows, ushort Columns, ushort ElementSizeBytes, uint StrideBytes, MatrixTileDescriptorLayoutKind Layout)
{
    public static MatrixTileCanonicalDescriptorAbi Create(
        ushort rows, ushort columns, ushort elementSizeBytes, uint strideBytes,
        MatrixTileDescriptorLayoutKind layout = MatrixTileDescriptorLayoutKind.RowMajor) =>
        new(rows, columns, elementSizeBytes, strideBytes, layout);
    public bool IsZeroEncoding => Rows == 0 && Columns == 0 && ElementSizeBytes == 0 && StrideBytes == 0 && Layout == MatrixTileDescriptorLayoutKind.Unspecified;
    public bool IsCanonical => Rows != 0 && Columns != 0 && ElementSizeBytes != 0 && StrideBytes != 0 && Layout == MatrixTileDescriptorLayoutKind.RowMajor;
    public bool IsReservedEncoding => !IsZeroEncoding && !IsCanonical;
}

public enum MatrixTileNumericProfileId : byte
{
    Unspecified, SignedInt8ToInt32, UnsignedInt8ToUInt32, SignedInt16ToInt32,
    UnsignedInt16ToUInt32, SignedInt32ToInt64, UnsignedInt32ToUInt64,
    SignedInt64ToInt64, UnsignedInt64ToUInt64, Binary32ToBinary32, Binary64ToBinary64
}
public enum MatrixTileNumericSignedness : byte { Unspecified, Signed, Unsigned, NotApplicable }
public enum MatrixTileNumericWideningRule : byte { Unspecified, SignExtend, ZeroExtend, PreserveBinaryFormat }
public enum MatrixTileNumericMultiplyRule : byte { Unspecified, ExactIntegerProduct, Ieee754Product }
public enum MatrixTileNumericAddRule : byte { Unspecified, UnboundedIntegerSum, Ieee754Sum }
public enum MatrixTileNumericRoundingMode : byte { Unspecified, NotApplicable, RoundToNearestTiesToEven }
public enum MatrixTileNumericSaturationMode : byte { Unspecified, None }
public enum MatrixTileNumericOverflowMode : byte { Unspecified, TrapOnFinalEncoding, Ieee754 }
public enum MatrixTileNumericNaNPolicy : byte { Unspecified, NotApplicable, PreserveIeee754 }
public enum MatrixTileNumericInfinityPolicy : byte { Unspecified, NotApplicable, PreserveIeee754 }
public enum MatrixTileNumericDenormalPolicy : byte { Unspecified, NotApplicable, PreserveIeee754 }
public enum MatrixTileNumericReproducibilityMode : byte { Unspecified, DeterministicSoftwareOrder }
public enum MatrixTileNumericExceptionPolicy : byte { Unspecified, TrapBeforePublication, Ieee754 }
public enum MatrixTileNumericPolicyFaultKind : byte
{
    None, MissingPolicy, UnsupportedAbiVersion, ReservedProfile, UnsupportedProfile,
    FingerprintMismatch, ContradictoryElementType, ContradictoryAccumulatorType,
    ContradictoryPublishFormat, ContradictorySignedness, ContradictoryRule
}

public readonly record struct MatrixTileNumericPolicy(
    ushort AbiVersion,
    MatrixTileNumericProfileId ProfileId,
    HybridCpuDataType ElementType,
    HybridCpuDataType AccumulatorType,
    HybridCpuDataType PublishFormat,
    MatrixTileNumericSignedness Signedness,
    MatrixTileNumericWideningRule WideningRule,
    MatrixTileNumericMultiplyRule MultiplyRule,
    MatrixTileNumericAddRule AddRule,
    MatrixTileNumericRoundingMode RoundingMode,
    MatrixTileNumericSaturationMode SaturationMode,
    MatrixTileNumericOverflowMode OverflowMode,
    MatrixTileNumericNaNPolicy NaNPolicy,
    MatrixTileNumericInfinityPolicy InfinityPolicy,
    MatrixTileNumericDenormalPolicy DenormalPolicy,
    MatrixTileNumericReproducibilityMode ReproducibilityMode,
    MatrixTileNumericExceptionPolicy ExceptionPolicy,
    ulong Fingerprint);

public readonly record struct MatrixTileNumericPolicyValidationResult(bool IsValid, MatrixTileNumericPolicyFaultKind FaultKind)
{
    public bool IsRuntimeOwnedNumericPolicyAccepted => IsValid;
}

public static class MatrixTileNumericPolicyAbi
{
    public const ushort CurrentAbiVersion = 1;
    public static MatrixTileNumericPolicy CreateSupportedPolicy(MatrixTileNumericProfileId profileId)
    {
        (HybridCpuDataType element, HybridCpuDataType accumulator, MatrixTileNumericSignedness signedness) = profileId switch
        {
            MatrixTileNumericProfileId.SignedInt8ToInt32 => (HybridCpuDataType.INT8, HybridCpuDataType.INT32, MatrixTileNumericSignedness.Signed),
            MatrixTileNumericProfileId.UnsignedInt8ToUInt32 => (HybridCpuDataType.UINT8, HybridCpuDataType.UINT32, MatrixTileNumericSignedness.Unsigned),
            MatrixTileNumericProfileId.SignedInt16ToInt32 => (HybridCpuDataType.INT16, HybridCpuDataType.INT32, MatrixTileNumericSignedness.Signed),
            MatrixTileNumericProfileId.UnsignedInt16ToUInt32 => (HybridCpuDataType.UINT16, HybridCpuDataType.UINT32, MatrixTileNumericSignedness.Unsigned),
            MatrixTileNumericProfileId.SignedInt32ToInt64 => (HybridCpuDataType.INT32, HybridCpuDataType.INT64, MatrixTileNumericSignedness.Signed),
            MatrixTileNumericProfileId.UnsignedInt32ToUInt64 => (HybridCpuDataType.UINT32, HybridCpuDataType.UINT64, MatrixTileNumericSignedness.Unsigned),
            MatrixTileNumericProfileId.SignedInt64ToInt64 => (HybridCpuDataType.INT64, HybridCpuDataType.INT64, MatrixTileNumericSignedness.Signed),
            MatrixTileNumericProfileId.UnsignedInt64ToUInt64 => (HybridCpuDataType.UINT64, HybridCpuDataType.UINT64, MatrixTileNumericSignedness.Unsigned),
            MatrixTileNumericProfileId.Binary32ToBinary32 => (HybridCpuDataType.FLOAT32, HybridCpuDataType.FLOAT32, MatrixTileNumericSignedness.NotApplicable),
            MatrixTileNumericProfileId.Binary64ToBinary64 => (HybridCpuDataType.FLOAT64, HybridCpuDataType.FLOAT64, MatrixTileNumericSignedness.NotApplicable),
            _ => throw new ArgumentOutOfRangeException(nameof(profileId), profileId, "Unsupported MatrixTile numeric profile.")
        };
        bool floating = signedness == MatrixTileNumericSignedness.NotApplicable;
        MatrixTileNumericPolicy policy = new MatrixTileNumericPolicy(
            CurrentAbiVersion, profileId, element, accumulator, accumulator, signedness,
            floating ? MatrixTileNumericWideningRule.PreserveBinaryFormat : signedness == MatrixTileNumericSignedness.Signed ? MatrixTileNumericWideningRule.SignExtend : MatrixTileNumericWideningRule.ZeroExtend,
            floating ? MatrixTileNumericMultiplyRule.Ieee754Product : MatrixTileNumericMultiplyRule.ExactIntegerProduct,
            floating ? MatrixTileNumericAddRule.Ieee754Sum : MatrixTileNumericAddRule.UnboundedIntegerSum,
            floating ? MatrixTileNumericRoundingMode.RoundToNearestTiesToEven : MatrixTileNumericRoundingMode.NotApplicable,
            MatrixTileNumericSaturationMode.None,
            floating ? MatrixTileNumericOverflowMode.Ieee754 : MatrixTileNumericOverflowMode.TrapOnFinalEncoding,
            floating ? MatrixTileNumericNaNPolicy.PreserveIeee754 : MatrixTileNumericNaNPolicy.NotApplicable,
            floating ? MatrixTileNumericInfinityPolicy.PreserveIeee754 : MatrixTileNumericInfinityPolicy.NotApplicable,
            floating ? MatrixTileNumericDenormalPolicy.PreserveIeee754 : MatrixTileNumericDenormalPolicy.NotApplicable,
            MatrixTileNumericReproducibilityMode.DeterministicSoftwareOrder,
            floating ? MatrixTileNumericExceptionPolicy.Ieee754 : MatrixTileNumericExceptionPolicy.TrapBeforePublication,
            0);
        return policy with { Fingerprint = ComputeFingerprint(policy) };
    }

    public static MatrixTileNumericPolicyValidationResult Validate(MatrixTileNumericPolicy? policy)
    {
        if (policy is not { } value) return new(false, MatrixTileNumericPolicyFaultKind.MissingPolicy);
        if (value.AbiVersion != CurrentAbiVersion) return new(false, MatrixTileNumericPolicyFaultKind.UnsupportedAbiVersion);
        MatrixTileNumericPolicy expected;
        try { expected = CreateSupportedPolicy(value.ProfileId); }
        catch (ArgumentOutOfRangeException) { return new(false, MatrixTileNumericPolicyFaultKind.ReservedProfile); }
        if (value.Fingerprint == 0 || value.Fingerprint != ComputeFingerprint(value)) return new(false, MatrixTileNumericPolicyFaultKind.FingerprintMismatch);
        return value == expected ? new(true, MatrixTileNumericPolicyFaultKind.None) : new(false, MatrixTileNumericPolicyFaultKind.ContradictoryRule);
    }

    public static int GetElementSizeBytes(HybridCpuDataType dataType) => HybridCpuDataTypes.SizeOf(dataType);
    public static ulong ComputeFingerprint(MatrixTileNumericPolicy policy)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte value in new byte[] { (byte)policy.AbiVersion, (byte)(policy.AbiVersion >> 8), (byte)policy.ProfileId, (byte)policy.ElementType, (byte)policy.AccumulatorType, (byte)policy.PublishFormat, (byte)policy.Signedness, (byte)policy.WideningRule, (byte)policy.MultiplyRule, (byte)policy.AddRule, (byte)policy.RoundingMode, (byte)policy.SaturationMode, (byte)policy.OverflowMode, (byte)policy.NaNPolicy, (byte)policy.InfinityPolicy, (byte)policy.DenormalPolicy, (byte)policy.ReproducibilityMode, (byte)policy.ExceptionPolicy }) { hash ^= value; hash *= 1099511628211UL; }
        return hash;
    }
}

public enum MatrixTileLayoutProfileId : byte { Unspecified, MaccCanonicalRowMajorAscendingK, TransposeCanonicalRowMajor }
public enum MatrixTileElementAddressingKind : byte { Unspecified, NotApplicable, CanonicalPackedRowMajor, ColumnMajor, Blocked, Interleaved }
public enum MatrixTileKIterationOrderKind : byte { Unspecified, NotApplicable, AscendingZeroToKMinusOne }
public enum MatrixTileTransposePermutationKind : byte { Unspecified, NotApplicable, DestinationColumnRowFromSourceRowColumn }
public enum MatrixTileLayoutPolicyFaultKind : byte { None, MissingPolicy, UnsupportedAbiVersion, ReservedProfile, UnsupportedOperation, FingerprintMismatch, ContradictoryRule, DescriptorLayoutMismatch }
public readonly record struct MatrixTileLayoutPolicy(
    ushort AbiVersion, MatrixTileLayoutProfileId ProfileId, MatrixTileProjectedOperationKind OperationKind,
    MatrixTileElementAddressingKind SourceAddressing, MatrixTileElementAddressingKind SecondaryAddressing,
    MatrixTileElementAddressingKind DestinationAddressing, MatrixTileKIterationOrderKind KIterationOrder,
    MatrixTileTransposePermutationKind TransposePermutation, MatrixTileTransposeAliasPolicyKind TransposeAliasPolicy,
    ulong Fingerprint);
public readonly record struct MatrixTileLayoutPolicyValidationResult(bool IsValid, MatrixTileLayoutPolicyFaultKind FaultKind)
{
    public bool IsRuntimeOwnedLayoutPolicyAccepted => IsValid;
}
public static class MatrixTileLayoutPolicyAbi
{
    public const ushort CurrentAbiVersion = 1;
    public static MatrixTileLayoutPolicy CreateMaccPolicy() => Create(MatrixTileProjectedOperationKind.Macc);
    public static MatrixTileLayoutPolicy CreateTransposePolicy() => Create(MatrixTileProjectedOperationKind.Transpose);
    private static MatrixTileLayoutPolicy Create(MatrixTileProjectedOperationKind kind)
    {
        MatrixTileLayoutPolicy value = kind == MatrixTileProjectedOperationKind.Macc
            ? new MatrixTileLayoutPolicy(1, MatrixTileLayoutProfileId.MaccCanonicalRowMajorAscendingK, kind, MatrixTileElementAddressingKind.CanonicalPackedRowMajor, MatrixTileElementAddressingKind.CanonicalPackedRowMajor, MatrixTileElementAddressingKind.CanonicalPackedRowMajor, MatrixTileKIterationOrderKind.AscendingZeroToKMinusOne, MatrixTileTransposePermutationKind.NotApplicable, MatrixTileTransposeAliasPolicyKind.Unspecified, 0)
            : new MatrixTileLayoutPolicy(1, MatrixTileLayoutProfileId.TransposeCanonicalRowMajor, kind, MatrixTileElementAddressingKind.CanonicalPackedRowMajor, MatrixTileElementAddressingKind.NotApplicable, MatrixTileElementAddressingKind.CanonicalPackedRowMajor, MatrixTileKIterationOrderKind.NotApplicable, MatrixTileTransposePermutationKind.DestinationColumnRowFromSourceRowColumn, MatrixTileTransposeAliasPolicyKind.OutOfPlaceOrSquareInPlaceOnly, 0);
        return value with { Fingerprint = ComputeFingerprint(value) };
    }
    public static MatrixTileLayoutPolicyValidationResult Validate(MatrixTileLayoutPolicy? policy, MatrixTileProjectedOperationKind expected)
    {
        if (policy is not { } value) return new(false, MatrixTileLayoutPolicyFaultKind.MissingPolicy);
        MatrixTileLayoutPolicy expectedValue = expected == MatrixTileProjectedOperationKind.Macc ? CreateMaccPolicy() : expected == MatrixTileProjectedOperationKind.Transpose ? CreateTransposePolicy() : default;
        if (expectedValue == default) return new(false, MatrixTileLayoutPolicyFaultKind.UnsupportedOperation);
        if (value.AbiVersion != 1) return new(false, MatrixTileLayoutPolicyFaultKind.UnsupportedAbiVersion);
        if (value.Fingerprint == 0 || value.Fingerprint != ComputeFingerprint(value)) return new(false, MatrixTileLayoutPolicyFaultKind.FingerprintMismatch);
        return value == expectedValue ? new(true, MatrixTileLayoutPolicyFaultKind.None) : new(false, MatrixTileLayoutPolicyFaultKind.ContradictoryRule);
    }
    public static ulong ComputeFingerprint(MatrixTileLayoutPolicy policy)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte value in new byte[] { (byte)policy.AbiVersion, (byte)(policy.AbiVersion >> 8), (byte)policy.ProfileId, (byte)policy.OperationKind, (byte)policy.SourceAddressing, (byte)policy.SecondaryAddressing, (byte)policy.DestinationAddressing, (byte)policy.KIterationOrder, (byte)policy.TransposePermutation, (byte)policy.TransposeAliasPolicy }) { hash ^= value; hash *= 1099511628211UL; }
        return hash;
    }
}

public readonly record struct MatrixTileMemoryFaultPoint(ushort Row, ushort Column, uint ByteOffsetInRow, ulong Address, bool IsStore);
public readonly record struct MatrixTileMemoryShapeContract(MatrixTileMemoryOperationKind Operation, MatrixTileEffectiveAddressSourceKind EffectiveAddressSource, MatrixTileCanonicalDescriptorAbi Descriptor, ulong BaseAddress, ushort PageSizeBytes);
public readonly record struct MatrixTileMemoryShapeValidationResult(bool IsValid, MatrixTileMemoryFaultKind FaultKind, MatrixTileMemoryFaultPoint FaultPoint, bool HasFaultPoint, ulong FirstByteAddress, ulong LastByteAddress, ulong TotalByteFootprint, uint RowByteCount, bool CrossesPageBoundary, MatrixTileMemoryPublicationPolicyKind PublicationPolicy, MatrixTileMemoryOrderingPolicyKind OrderingPolicy)
{
    public bool IsMemoryShapeAbiAccepted => IsValid;
}
public static class MatrixTileMemoryShapeAndFaultAbi
{
    public const ushort DefaultPageSizeBytes = 4096;
    public static MatrixTileMemoryShapeContract CreateLoadContract(MatrixTileCanonicalDescriptorAbi descriptor, ulong address, ushort pageSizeBytes = DefaultPageSizeBytes) => new(MatrixTileMemoryOperationKind.Load, MatrixTileEffectiveAddressSourceKind.ExplicitRuntimeTileMemoryOperand, descriptor, address, pageSizeBytes);
    public static MatrixTileMemoryShapeContract CreateStoreContract(MatrixTileCanonicalDescriptorAbi descriptor, ulong address, ushort pageSizeBytes = DefaultPageSizeBytes) => new(MatrixTileMemoryOperationKind.Store, MatrixTileEffectiveAddressSourceKind.ExplicitRuntimeTileMemoryOperand, descriptor, address, pageSizeBytes);
    public static MatrixTileMemoryShapeValidationResult Validate(MatrixTileMemoryShapeContract contract)
    {
        if (contract.Operation is not MatrixTileMemoryOperationKind.Load and not MatrixTileMemoryOperationKind.Store) return Fault(MatrixTileMemoryFaultKind.UnsupportedOperation);
        if (contract.PageSizeBytes == 0) return Fault(MatrixTileMemoryFaultKind.InvalidPageSize);
        MatrixTileCanonicalDescriptorAbi d = contract.Descriptor;
        if (d.IsZeroEncoding) return Fault(MatrixTileMemoryFaultKind.ZeroDescriptor);
        if (!d.IsCanonical) return Fault(MatrixTileMemoryFaultKind.ReservedDescriptor);
        if (d.ElementSizeBytes is not (1 or 2 or 4 or 8)) return Fault(MatrixTileMemoryFaultKind.UnsupportedElementSize);
        ulong rowBytes = (ulong)d.Columns * d.ElementSizeBytes;
        if (rowBytes == 0 || rowBytes > uint.MaxValue) return Fault(MatrixTileMemoryFaultKind.RowByteOverflow);
        if (d.StrideBytes < rowBytes) return Fault(MatrixTileMemoryFaultKind.StrideTooSmall);
        if ((contract.BaseAddress % d.ElementSizeBytes) != 0 || (d.StrideBytes % d.ElementSizeBytes) != 0) return Fault(MatrixTileMemoryFaultKind.AlignmentFault);
        ulong total;
        try { total = checked(((ulong)d.Rows - 1) * d.StrideBytes + rowBytes); }
        catch (OverflowException) { return Fault(MatrixTileMemoryFaultKind.AddressOverflow); }
        if (total == 0 || contract.BaseAddress > ulong.MaxValue - total + 1) return Fault(MatrixTileMemoryFaultKind.AddressOverflow);
        ulong last = contract.BaseAddress + total - 1;
        return new(true, MatrixTileMemoryFaultKind.None, default, false, contract.BaseAddress, last, total, (uint)rowBytes, contract.BaseAddress / contract.PageSizeBytes != last / contract.PageSizeBytes, contract.Operation == MatrixTileMemoryOperationKind.Load ? MatrixTileMemoryPublicationPolicyKind.RetireStagedLoadPublication : MatrixTileMemoryPublicationPolicyKind.RetireStagedStoreCommit, MatrixTileMemoryOrderingPolicyKind.RetireOrderedAllOrNone);
    }
    private static MatrixTileMemoryShapeValidationResult Fault(MatrixTileMemoryFaultKind kind) => new(false, kind, default, false, 0, 0, 0, 0, false, 0, 0);
}

public readonly record struct MatrixTileMaccSemanticContract(MatrixTileCanonicalDescriptorAbi Left, MatrixTileCanonicalDescriptorAbi Right, MatrixTileCanonicalDescriptorAbi Accumulator, MatrixTileNumericElementKind ElementKind, MatrixTileAccumulatorPolicyKind AccumulatorPolicy)
{
    public MatrixTileNumericPolicy NumericPolicy { get; init; }
    public bool HasExplicitNumericPolicy { get; init; }
    public MatrixTileLayoutPolicy LayoutPolicy { get; init; }
    public bool HasExplicitLayoutPolicy { get; init; }
}
public readonly record struct MatrixTileTransposeSemanticContract(MatrixTileCanonicalDescriptorAbi Source, MatrixTileCanonicalDescriptorAbi Destination, ushort SourceTileId, ushort DestinationTileId, MatrixTileTransposeAliasPolicyKind AliasPolicy)
{
    public MatrixTileLayoutPolicy LayoutPolicy { get; init; }
    public bool HasExplicitLayoutPolicy { get; init; }
}
public readonly record struct MatrixTileSemanticValidationResult(bool IsValid, MatrixTileSemanticFaultKind FaultKind, MatrixTileCanonicalDescriptorAbi ResultDescriptor, ushort ResultElementSizeBytes, bool RequiresRetirePublication, bool RequiresReplayIdentity, bool UsesFallbackPath)
{
    public bool IsSemanticAbiAccepted => IsValid;
    public static MatrixTileSemanticValidationResult Fault(MatrixTileSemanticFaultKind kind) =>
        new(false, kind, default, 0, false, false, false);
    public static MatrixTileSemanticValidationResult Valid(MatrixTileCanonicalDescriptorAbi descriptor, ushort elementSizeBytes) =>
        new(true, MatrixTileSemanticFaultKind.None, descriptor, elementSizeBytes, true, true, false);
}
public static class MatrixTileAccumulatorAndTransposePolicyAbi
{
    public static MatrixTileMaccSemanticContract CreateMaccContract(MatrixTileCanonicalDescriptorAbi left, MatrixTileCanonicalDescriptorAbi right, MatrixTileCanonicalDescriptorAbi accumulator, MatrixTileNumericPolicy numeric, MatrixTileLayoutPolicy layout) => new(left, right, accumulator, numeric.Signedness == MatrixTileNumericSignedness.Signed ? MatrixTileNumericElementKind.SignedInteger : numeric.Signedness == MatrixTileNumericSignedness.Unsigned ? MatrixTileNumericElementKind.UnsignedInteger : MatrixTileNumericElementKind.FloatingPoint, numeric.Signedness == MatrixTileNumericSignedness.NotApplicable ? MatrixTileAccumulatorPolicyKind.PreserveBinaryFloatingAccumulator : MatrixTileAccumulatorPolicyKind.WideningIntegerAccumulatorWithOverflowTrap) { NumericPolicy = numeric, HasExplicitNumericPolicy = true, LayoutPolicy = layout, HasExplicitLayoutPolicy = true };
    public static MatrixTileTransposeSemanticContract CreateTransposeContract(MatrixTileCanonicalDescriptorAbi source, MatrixTileCanonicalDescriptorAbi destination, ushort sourceId, ushort destinationId, MatrixTileLayoutPolicy layout) => new(source, destination, sourceId, destinationId, MatrixTileTransposeAliasPolicyKind.OutOfPlaceOrSquareInPlaceOnly) { LayoutPolicy = layout, HasExplicitLayoutPolicy = true };
    public static MatrixTileSemanticValidationResult ValidateRuntimeMacc(MatrixTileMaccSemanticContract value)
    {
        MatrixTileSemanticFaultKind descriptorFault = ValidateDescriptors(value.Left, value.Right, value.Accumulator);
        if (descriptorFault != MatrixTileSemanticFaultKind.None) return MatrixTileSemanticValidationResult.Fault(descriptorFault);
        if (!value.HasExplicitNumericPolicy) return MatrixTileSemanticValidationResult.Fault(MatrixTileSemanticFaultKind.MissingNumericPolicy);
        if (!value.HasExplicitLayoutPolicy) return MatrixTileSemanticValidationResult.Fault(MatrixTileSemanticFaultKind.MissingLayoutPolicy);
        if (!MatrixTileLayoutPolicyAbi.Validate(value.LayoutPolicy, MatrixTileProjectedOperationKind.Macc).IsValid)
            return MatrixTileSemanticValidationResult.Fault(value.LayoutPolicy == default ? MatrixTileSemanticFaultKind.MissingLayoutPolicy : MatrixTileSemanticFaultKind.InvalidLayoutPolicy);
        if (!MatrixTileNumericPolicyAbi.Validate(value.NumericPolicy).IsValid)
            return MatrixTileSemanticValidationResult.Fault(value.NumericPolicy == default ? MatrixTileSemanticFaultKind.MissingNumericPolicy : MatrixTileSemanticFaultKind.InvalidNumericPolicy);
        if (value.Left.ElementSizeBytes is not (1 or 2 or 4 or 8) || value.Left.ElementSizeBytes != value.Right.ElementSizeBytes)
            return MatrixTileSemanticValidationResult.Fault(MatrixTileSemanticFaultKind.UnsupportedElementSize);
        if (value.Left.Columns != value.Right.Rows) return MatrixTileSemanticValidationResult.Fault(MatrixTileSemanticFaultKind.MaccInnerDimensionMismatch);
        if (value.Accumulator.Rows != value.Left.Rows || value.Accumulator.Columns != value.Right.Columns)
            return MatrixTileSemanticValidationResult.Fault(MatrixTileSemanticFaultKind.MaccAccumulatorShapeMismatch);
        ushort sourceSize = checked((ushort)MatrixTileNumericPolicyAbi.GetElementSizeBytes(value.NumericPolicy.ElementType));
        ushort accumulatorSize = checked((ushort)MatrixTileNumericPolicyAbi.GetElementSizeBytes(value.NumericPolicy.AccumulatorType));
        if (value.Left.ElementSizeBytes != sourceSize || value.Right.ElementSizeBytes != sourceSize)
            return MatrixTileSemanticValidationResult.Fault(MatrixTileSemanticFaultKind.NumericPolicyDescriptorMismatch);
        if (value.Accumulator.ElementSizeBytes != accumulatorSize)
            return MatrixTileSemanticValidationResult.Fault(MatrixTileSemanticFaultKind.MaccAccumulatorElementSizeMismatch);
        return MatrixTileSemanticValidationResult.Valid(value.Accumulator, accumulatorSize);
    }

    public static MatrixTileSemanticValidationResult ValidateTranspose(MatrixTileTransposeSemanticContract value)
    {
        MatrixTileSemanticFaultKind descriptorFault = ValidateDescriptors(value.Source, value.Destination);
        if (descriptorFault != MatrixTileSemanticFaultKind.None) return MatrixTileSemanticValidationResult.Fault(descriptorFault);
        if (!value.HasExplicitLayoutPolicy) return MatrixTileSemanticValidationResult.Fault(MatrixTileSemanticFaultKind.MissingLayoutPolicy);
        if (!MatrixTileLayoutPolicyAbi.Validate(value.LayoutPolicy, MatrixTileProjectedOperationKind.Transpose).IsValid)
            return MatrixTileSemanticValidationResult.Fault(value.LayoutPolicy == default ? MatrixTileSemanticFaultKind.MissingLayoutPolicy : MatrixTileSemanticFaultKind.InvalidLayoutPolicy);
        if (value.AliasPolicy != value.LayoutPolicy.TransposeAliasPolicy)
            return MatrixTileSemanticValidationResult.Fault(MatrixTileSemanticFaultKind.InvalidLayoutPolicy);
        if (value.Source.ElementSizeBytes != value.Destination.ElementSizeBytes)
            return MatrixTileSemanticValidationResult.Fault(MatrixTileSemanticFaultKind.TransposeElementSizeMismatch);
        bool inPlace = value.SourceTileId == value.DestinationTileId;
        if (inPlace)
        {
            if (value.AliasPolicy != MatrixTileTransposeAliasPolicyKind.OutOfPlaceOrSquareInPlaceOnly || value.Source.Rows != value.Source.Columns)
                return MatrixTileSemanticValidationResult.Fault(MatrixTileSemanticFaultKind.TransposeInPlaceRequiresSquareShape);
            if (value.Destination.Rows != value.Source.Rows || value.Destination.Columns != value.Source.Columns || value.Destination.StrideBytes != value.Source.StrideBytes)
                return MatrixTileSemanticValidationResult.Fault(MatrixTileSemanticFaultKind.TransposeInPlaceDescriptorMismatch);
        }
        else if (value.Destination.Rows != value.Source.Columns || value.Destination.Columns != value.Source.Rows)
            return MatrixTileSemanticValidationResult.Fault(MatrixTileSemanticFaultKind.TransposeShapeMismatch);
        return MatrixTileSemanticValidationResult.Valid(value.Destination, value.Destination.ElementSizeBytes);
    }

    private static MatrixTileSemanticFaultKind ValidateDescriptors(params MatrixTileCanonicalDescriptorAbi[] descriptors)
    {
        foreach (MatrixTileCanonicalDescriptorAbi descriptor in descriptors)
        {
            if (descriptor.IsZeroEncoding) return MatrixTileSemanticFaultKind.ZeroDescriptor;
            if (descriptor.IsReservedEncoding) return MatrixTileSemanticFaultKind.ReservedDescriptor;
        }
        return MatrixTileSemanticFaultKind.None;
    }
}

public readonly record struct VectorInstructionPayload(ulong DestSrc1Pointer, ulong Src2Pointer, uint StreamLength, ushort Stride, ushort RowStride, bool Indexed, bool Is2D, bool TailAgnostic, bool MaskAgnostic, bool Saturating, byte PredicateMask, HybridCpuDataType DataType)
{
    public MatrixTileNumericPolicy? MatrixTileNumericPolicy { get; init; }
    public MatrixTileLayoutPolicy? MatrixTileLayoutPolicy { get; init; }
}
public readonly record struct MatrixTileCompilerEmissionHandoffRow(string Mnemonic, HybridCpuOpcode Opcode);
public static class MatrixTileRuntimeIsaPackageContract { public static void RequirePositiveCompilerEmissionReadiness() { } }
public static class MatrixTileCompilerEmissionHandoffPackage
{
    public static void RequireRuntimeExecutableAuthority(string mnemonic) => _ = GetRow(mnemonic);
    public static MatrixTileCompilerEmissionHandoffRow GetRow(string mnemonic) => mnemonic switch
    {
        "MTILE_LOAD" => new(mnemonic, HybridCpuOpcode.MTILE_LOAD),
        "MTILE_STORE" => new(mnemonic, HybridCpuOpcode.MTILE_STORE),
        "MTILE_MACC" => new(mnemonic, HybridCpuOpcode.MTILE_MACC),
        "MTRANSPOSE" => new(mnemonic, HybridCpuOpcode.MTRANSPOSE),
        _ => throw new ArgumentOutOfRangeException(nameof(mnemonic), mnemonic, "Unknown MatrixTile handoff row.")
    };
}
public readonly record struct MatrixTileMaterializedInstruction(HybridCpuOpcode Opcode);
public readonly record struct MatrixTileInstructionIrProjection(HybridCpuOpcode Opcode, MatrixTileCanonicalDescriptorAbi TileDescriptor, MatrixTileCanonicalDescriptorAbi SecondaryTileDescriptor, MatrixTileCanonicalDescriptorAbi ResultTileDescriptor, MatrixTileMemoryShapeContract? MemoryContract, MatrixTileMemoryShapeValidationResult? MemoryValidation, MatrixTileMaccSemanticContract? MaccContract, MatrixTileTransposeSemanticContract? TransposeContract, MatrixTileSemanticValidationResult? SemanticValidation, bool UsesFallbackPath, MatrixTileIrProjectionFaultKind FaultKind);

public static class MatrixTileIrProjectionAndMaterializer
{
    public static MatrixTileInstructionIrProjection ProjectDecodedVectorPayload(
        HybridCpuOpcode opcode,
        VectorInstructionPayload payload,
        ushort columns,
        bool requireExplicitNumericPolicy)
    {
        if (opcode is not (HybridCpuOpcode.MTILE_LOAD or HybridCpuOpcode.MTILE_STORE or HybridCpuOpcode.MTILE_MACC or HybridCpuOpcode.MTRANSPOSE) || columns == 0)
        {
            return new(opcode, default, default, default, null, null, null, null, null, false, MatrixTileIrProjectionFaultKind.UnsupportedOpcode);
        }

        ushort rows = payload.StreamLength == 0
            ? (ushort)0
            : checked((ushort)(payload.StreamLength / columns));
        MatrixTileCanonicalDescriptorAbi descriptor = MatrixTileCanonicalDescriptorAbi.Create(rows, columns, payload.Stride, payload.RowStride);
        if (!descriptor.IsCanonical || checked((uint)rows * columns) != payload.StreamLength)
        {
            return new(opcode, descriptor, default, default, null, null, null, null, null, false, MatrixTileIrProjectionFaultKind.InvalidDescriptor);
        }

        MatrixTileMemoryShapeContract? memoryContract = null;
        MatrixTileMemoryShapeValidationResult? memoryValidation = null;
        MatrixTileMaccSemanticContract? macc = null;
        MatrixTileTransposeSemanticContract? transpose = null;
        MatrixTileSemanticValidationResult? semantic = null;
        MatrixTileCanonicalDescriptorAbi secondary = default;
        MatrixTileCanonicalDescriptorAbi result = default;

        if (opcode is HybridCpuOpcode.MTILE_LOAD or HybridCpuOpcode.MTILE_STORE)
        {
            memoryContract = opcode == HybridCpuOpcode.MTILE_LOAD
                ? MatrixTileMemoryShapeAndFaultAbi.CreateLoadContract(descriptor, payload.DestSrc1Pointer)
                : MatrixTileMemoryShapeAndFaultAbi.CreateStoreContract(descriptor, payload.DestSrc1Pointer);
            memoryValidation = MatrixTileMemoryShapeAndFaultAbi.Validate(memoryContract.Value);
            if (!memoryValidation.Value.IsValid)
            {
                return new(opcode, descriptor, default, default, memoryContract, memoryValidation, null, null, null, false, MatrixTileIrProjectionFaultKind.InvalidMemoryShape);
            }
        }
        else if (opcode == HybridCpuOpcode.MTILE_MACC)
        {
            if (payload.MatrixTileNumericPolicy is not { } numeric || payload.MatrixTileLayoutPolicy is not { } layout)
            {
                return new(opcode, descriptor, default, default, null, null, null, null, null, false, MatrixTileIrProjectionFaultKind.InvalidSemanticPolicy);
            }
            ushort accumulatorSize = checked((ushort)MatrixTileNumericPolicyAbi.GetElementSizeBytes(numeric.AccumulatorType));
            secondary = MatrixTileCanonicalDescriptorAbi.Create(descriptor.Columns, descriptor.Rows, descriptor.ElementSizeBytes, checked((uint)descriptor.Rows * descriptor.ElementSizeBytes));
            result = MatrixTileCanonicalDescriptorAbi.Create(descriptor.Rows, secondary.Columns, accumulatorSize, checked((uint)secondary.Columns * accumulatorSize));
            macc = MatrixTileAccumulatorAndTransposePolicyAbi.CreateMaccContract(descriptor, secondary, result, numeric, layout);
            semantic = MatrixTileAccumulatorAndTransposePolicyAbi.ValidateRuntimeMacc(macc.Value);
        }
        else
        {
            if (payload.MatrixTileLayoutPolicy is not { } layout)
            {
                return new(opcode, descriptor, default, default, null, null, null, null, null, false, MatrixTileIrProjectionFaultKind.InvalidSemanticPolicy);
            }
            result = MatrixTileCanonicalDescriptorAbi.Create(descriptor.Columns, descriptor.Rows, descriptor.ElementSizeBytes, checked((uint)descriptor.Rows * descriptor.ElementSizeBytes));
            transpose = MatrixTileAccumulatorAndTransposePolicyAbi.CreateTransposeContract(descriptor, result, (ushort)payload.DestSrc1Pointer, (ushort)payload.Src2Pointer, layout);
            semantic = MatrixTileAccumulatorAndTransposePolicyAbi.ValidateTranspose(transpose.Value);
        }

        if (semantic is { IsValid: false })
        {
            return new(opcode, descriptor, secondary, result, memoryContract, memoryValidation, macc, transpose, semantic, false, MatrixTileIrProjectionFaultKind.InvalidSemanticPolicy);
        }
        return new(opcode, descriptor, secondary, result, memoryContract, memoryValidation, macc, transpose, semantic, false, MatrixTileIrProjectionFaultKind.None);
    }

    public static bool TryMaterialize(
        MatrixTileInstructionIrProjection projection,
        out MatrixTileMaterializedInstruction instruction,
        out MatrixTileIrProjectionFaultKind faultKind)
    {
        faultKind = projection.FaultKind;
        instruction = new MatrixTileMaterializedInstruction(projection.Opcode);
        return faultKind == MatrixTileIrProjectionFaultKind.None;
    }
}
