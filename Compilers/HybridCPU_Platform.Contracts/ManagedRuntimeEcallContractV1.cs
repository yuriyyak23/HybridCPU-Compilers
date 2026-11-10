using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Platform.Contracts;

public static class HybridCpuManagedRuntimeEcallContractV1
{
    public const string SchemaId = "hybridcpu.managed-runtime-ecall/v1";
    public const ulong ArgumentExceptionGetMessageOperation = 1;
    public const ulong EnsureTypeInitializedOperation = 2;
    public const ulong StaticStoreInt32Operation = 3;
    public const ulong StaticLoadInt32Operation = 4;
    public const ulong AllocateNullReferenceExceptionOperation = 5;
    public const ulong ArrayStoreInt32Operation = 6;
    public const ulong ArrayStoreReferenceOperation = 7;
    public const ulong InitializeArrayOperation = 8;
    public const ulong NewArrayOperation = 9;
    public const ulong StaticStoreReferenceOperation = 10;
    public const ulong StaticLoadReferenceOperation = 11;
    public const ulong AllocateObjectOperation = 12;
    public const ulong ArrayLoadReferenceOperation = 13;
    public const ulong DivideUInt32CheckedOperation = 14;
    public const ulong ArrayLoadInt32Operation = 15;
    public const ulong ArrayEmptyOperation = 16;
    public const ulong ArrayLengthOperation = 17;
    public const ulong ArrayStoreInt8Operation = 18;
    public const ulong StringCharacterOperation = 19;
    public const ulong StringLengthOperation = 20;
    public const ulong StringConcat2Operation = 21;
    public const ulong ArrayLoadUInt8Operation = 22;
    public const ulong DivideInt32CheckedOperation = 23;
    public const ulong DivideInt64CheckedOperation = 24;
    public const ulong MathAbsInt64CheckedOperation = 25;
    public const ulong ArrayLoadInt16Operation = 26;
    public const ulong ArrayLoadUInt16Operation = 27;
    public const ulong ArrayCopyAllOperation = 28;
    public const ulong IsInstanceOperation = 29;
    public const ulong RemainderInt32CheckedOperation = 30;
    public const ulong StringConcat3Operation = 31;
    public const ulong ArrayStoreInt16Operation = 32;
    public const ulong StringFromUtf16ArrayOperation = 33;
    public const ulong ArrayCopyOperation = 34;
    public const ulong ArgumentNullCtorParamNameOperation = 35;
    public const ulong ArgumentOutOfRangeCtorParamNameOperation = 36;
    public const ulong StringNotEqualsOperation = 37;
    public const ulong ArrayClearOperation = 38;
    public const ulong ArrayLoadInt64Operation = 39;
    public const ulong NullableValueInt64CheckedOperation = 40;
    public const int ArrayCopyArgumentBlockBytes = 40;
    public static string ContractDigest { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('|', SchemaId, (ushort)HybridCpuHostServiceV1.ManagedRuntime,
            ArgumentExceptionGetMessageOperation,
            AllocateNullReferenceExceptionOperation,
            ArrayStoreInt32Operation,
            ArrayStoreReferenceOperation,
            InitializeArrayOperation,
            NewArrayOperation,
            StaticStoreReferenceOperation,
            StaticLoadReferenceOperation,
            AllocateObjectOperation,
            ArrayLoadReferenceOperation,
            DivideUInt32CheckedOperation,
            ArrayLoadInt32Operation,
            ArrayEmptyOperation,
            ArrayLengthOperation,
            ArrayStoreInt8Operation,
            StringCharacterOperation,
            StringLengthOperation,
            StringConcat2Operation,
            ArrayLoadUInt8Operation,
            DivideInt32CheckedOperation,
            DivideInt64CheckedOperation,
            MathAbsInt64CheckedOperation,
            ArrayLoadInt16Operation,
            ArrayLoadUInt16Operation,
            ArrayCopyAllOperation,
            IsInstanceOperation,
            RemainderInt32CheckedOperation,
            StringConcat3Operation,
            ArrayStoreInt16Operation,
            StringFromUtf16ArrayOperation,
            ArrayCopyOperation,
            ArgumentNullCtorParamNameOperation,
            ArgumentOutOfRangeCtorParamNameOperation,
            StringNotEqualsOperation,
            ArrayClearOperation,
            NullableValueInt64CheckedOperation,
            ArrayLoadInt64Operation,
            $"array-copy-block={ArrayCopyArgumentBlockBytes}:u64-source:i64-source-index:u64-destination:i64-destination-index:i64-length:little-endian:read-only",
            "arg0=receiver-object-reference", "return=object-reference", "allocating-safepoint=required",
            "privilege=user", HybridCpuExternalServiceEcallContractV1.ContractDigest))))
        .ToLowerInvariant();
}
