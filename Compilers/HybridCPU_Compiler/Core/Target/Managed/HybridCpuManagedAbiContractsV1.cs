using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public enum HybridCpuManagedAbiSupportV1 : byte
{
    Supported = 0,
    ContractOnly = 1,
    Unsupported = 2
}

public enum HybridCpuManagedAbiCompatibilityV1 : byte
{
    Compatible = 0,
    UnsupportedMajorVersion = 1,
    UnsupportedMinorVersion = 2,
    TargetMismatch = 3,
    NativeAbiMismatch = 4,
    RuntimePackMismatch = 5,
    SubcontractMismatch = 6,
    MissingRequiredSupport = 7,
    InvalidMetadata = 8
}

public enum HybridCpuManagedValueKindV1 : byte
{
    PrimitiveInteger = 0,
    BlittableValue = 1,
    ObjectReference = 2,
    ManagedByRef = 3,
    InteriorReference = 4,
    Unknown = 255
}

public enum HybridCpuManagedCallDirectionV1 : byte
{
    ManagedToManaged = 0,
    ManagedToUnmanaged = 1,
    UnmanagedToManaged = 2
}

public enum HybridCpuGcReferenceKindV1 : byte
{
    ObjectReference = 0,
    ManagedByRef = 1,
    InteriorReference = 2
}

public enum HybridCpuGcLocationKindV1 : byte
{
    Register = 0,
    Stack = 1
}

public enum HybridCpuSafepointCategoryV1 : byte
{
    CallSite = 0,
    PollSite = 1
}

public enum HybridCpuRuntimeHelperGcTransitionV1 : byte
{
    None = 0,
    MaySafepoint = 1,
    RequiredSafepoint = 2,
    Unknown = 255
}

public sealed record HybridCpuManagedAbiSubcontractV1(
    string SchemaId,
    int SchemaMajor,
    int SchemaMinor,
    HybridCpuManagedAbiSupportV1 Support,
    string Scope,
    string Digest);

public sealed record HybridCpuManagedFeatureV1(
    string Identity,
    HybridCpuManagedAbiSupportV1 Support,
    string OwningSubcontract,
    string Reason);

public sealed record HybridCpuManagedValueV1(
    string Identity,
    HybridCpuManagedValueKindV1 Kind,
    int SizeBytes,
    int AlignmentBytes);

public sealed record HybridCpuManagedCallSignatureV1(
    HybridCpuManagedCallDirectionV1 Direction,
    IReadOnlyList<HybridCpuManagedValueV1> Parameters,
    HybridCpuManagedValueV1? ReturnValue,
    bool IsVarArgs = false,
    bool IsGenericSharedCall = false,
    bool HasRuntimeLookupArgument = false);

public sealed record HybridCpuManagedCallLayoutV1(
    HybridCpuPlatformFactStatus Status,
    string Reason,
    HybridCpuAbiLayoutV2? NativeLayout,
    string Digest);

public sealed record HybridCpuManagedReceiverLoanCallV1(
    HybridCpuManagedCallSignatureV1 Signature,
    int ReceiverParameterIndex,
    string ScopedTypeIdentity,
    int PayloadSizeBytes,
    int PayloadAlignmentBytes,
    int CallerStorageSizeBytes,
    int CallerStorageAlignmentBytes,
    bool CallerStorageNonNull,
    bool CallerStorageBoundsProven,
    bool NonEscaping,
    bool NoSafepoints,
    bool ReferenceFree,
    string ImporterPlanDigest,
    string CallerStorageProofDigest);

public sealed record HybridCpuGcReferenceLocationV1(
    string ValueIdentity,
    HybridCpuGcReferenceKindV1 ReferenceKind,
    HybridCpuGcLocationKindV1 LocationKind,
    int? RegisterId,
    int? StackOffsetBytes);

public sealed record HybridCpuSafepointRecordV1(
    int CodeOffsetBytes,
    HybridCpuSafepointCategoryV1 Category,
    IReadOnlyList<HybridCpuGcReferenceLocationV1> LiveReferences);

public sealed record HybridCpuSafepointMachineStateV1(
    int StackPointerRegister,
    int FramePointerRegister,
    int ReturnAddressRegister,
    IReadOnlyList<int> PreservedRegisters,
    bool RequiresInstructionPointer,
    bool RequiresExactFrameSize);

public sealed record HybridCpuGcInfoEncodingResultV1(
    HybridCpuPlatformFactStatus Status,
    string Reason,
    byte[] Bytes,
    string Digest);

public sealed record HybridCpuRuntimeHelperV1(
    string Symbol,
    string Signature,
    string CallingConvention,
    IrMemoryEffectKind MemoryEffects,
    HybridCpuRuntimeHelperGcTransitionV1 GcTransition,
    bool MayThrow,
    HybridCpuManagedAbiSupportV1 Support);

public sealed record HybridCpuManagedAbiEnvelopeV1(
    int FamilyMajor,
    int FamilyMinor,
    string FamilyDigest,
    string TargetContractDigest,
    string NativeAbiDigest,
    string RuntimePackRevision,
    IReadOnlyDictionary<string, string> SubcontractDigests,
    IReadOnlyList<string> RequiredSupportedFeatures);

public sealed record HybridCpuCodeManagerMethodRecordV1(
    string MethodIdentity,
    int CodeStartOffsetBytes,
    int CodeSizeBytes,
    string GcInfoDigest,
    string? UnwindInfoDigest);

/// <summary>
/// Phase 22 managed ABI family. This contract describes compiler metadata and locations;
/// it never grants object-lifetime, collection, suspension, transition, publication,
/// execution, commit or retire authority.
/// </summary>
public sealed class HybridCpuManagedAbiFamilyV1
{
    public const string SchemaId = "hybridcpu.managed-abi-family";
    public const int SchemaMajor = 1;
    public const int SchemaMinor = 63;
    public const string RuntimePackRevision = "hybridcpu.runtime-pack/managed-contract-v1.25";
    public const int MaximumSafepoints = 8192;
    public const int MaximumReferencesPerSafepoint = 1024;
    public const int MaximumCodeManagerRecords = 4096;

    private static readonly string[] SubcontractOrder =
    [
        "ManagedCallAbi", "ManagedReferenceAbi", "GcInfoAbi", "SafepointAbi",
        "RuntimeHelperAbi", "ThreadContextAbi", "TransitionThunkAbi", "EhUnwindAbi", "TrapMappingAbi",
        "CodeManagerMetadataAbi", "PlatformBootstrapAbi"
    ];

    private static readonly HybridCpuRuntimeHelperV1[] HelperTable =
    [
        new("__hybridcpu_runtime_bootstrap", "void(execution-context:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_null_check", "object-ref(object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_alloc", "object-ref(type-handle:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_newarr", "object-ref(type-handle:nuint,length:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_array_empty", "object-ref(type-handle:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_array_length", "native-uint(array-ref:object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_nullable_value_i8_checked", "int64(value:int64,has-value:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_array_load_i8", "int64(array-ref:object-ref,index:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
            HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_array_load_i4", "int32(array-ref:object-ref,index:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
            HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_array_load_i1", "int32(array-ref:object-ref,index:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_array_load_u1", "int32(array-ref:object-ref,index:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_array_load_i2", "int32(array-ref:object-ref,index:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_array_load_u2", "int32(array-ref:object-ref,index:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_array_store_i1", "void(array-ref:object-ref,index:int32,value:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_array_store_i2", "void(array-ref:object-ref,index:int32,value:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_array_store_i4", "void(array-ref:object-ref,index:int32,value:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_array_load_ref", "object-ref(array-ref:object-ref,index:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
            HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_array_store_ref", "void(array-ref:object-ref,index:int32,value:object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_array_copy", "void(source-array-ref:object-ref,source-index:int32,destination-array-ref:object-ref,destination-index:int32,length:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.None, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_array_copy_all", "void(source-array-ref:object-ref,destination-array-ref:object-ref,length:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_array_clear", "void(array-ref:object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_math_abs_i8", "int64(value:int64)", "managed-helper-v1",
            IrMemoryEffectKind.None, HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_math_max_i4", "int32(left:int32,right:int32)", "managed-helper-v1",
            IrMemoryEffectKind.None, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_initialize_array", "void(array-ref:object-ref,field-data-handle:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_ldstr", "object-ref(literal-handle:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_string_length", "int32(string-ref:object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_string_char", "uint16(string-ref:object-ref,index:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_string_from_utf16_array", "object-ref(type-handle:nuint,array-ref:object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_string_concat2", "object-ref(first:object-ref,second:object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_string_concat3", "object-ref(first:object-ref,second:object-ref,third:object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_string_equals", "boolean(first:object-ref,second:object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_string_not_equals", "boolean(first:object-ref,second:object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_exception_ctor_message", "void(exception-ref:object-ref,message-ref:object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.None, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_argument_null_ctor_param_name", "void(exception-ref:object-ref,param-name:object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_argument_out_of_range_ctor_param_name", "void(exception-ref:object-ref,param-name:object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_exception_get_message", "object-ref(exception-ref:object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_argument_exception_get_message", "object-ref(exception-ref:object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint,
            true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_ensure_type_initialized", "void(type-handle:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_static_load_i4", "int32(type-handle:nuint,offset:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_static_store_i4", "void(type-handle:nuint,offset:nuint,value:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_static_load_ref", "object-ref(type-handle:nuint,offset:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_static_store_ref", "void(type-handle:nuint,offset:nuint,value:object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_box_i4", "object-ref(type-handle:nuint,value:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_unbox_i4", "int32(type-handle:nuint,object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_box_i8", "object-ref(type-handle:nuint,value:int64)", "managed-helper-v1",
            IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_unbox_i8", "int64(type-handle:nuint,object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_resolve_virtual", "native-uint(receiver:object-ref,slot-id:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_resolve_interface", "native-uint(receiver:object-ref,interface-type-id:nuint,slot-id:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_castclass", "object-ref(receiver:object-ref,type-handle:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_isinst", "object-ref(receiver:object-ref,type-handle:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_get_function_pointer", "native-uint(method-id:nuint,signature-id:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_get_virtual_function_pointer", "native-uint(receiver:object-ref,slot-id:nuint,signature-id:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_validate_function_pointer", "native-uint(code-address:nuint,signature-id:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_create_delegate", "object-ref(delegate-type-handle:nuint,target:object-ref,code-address:nuint,signature-id:nuint,kind:int32)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_resolve_delegate", "native-uint(delegate:object-ref,signature-id:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_read_barrier", "object-ref(object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Unsupported),
        new("__hybridcpu_managed_write_barrier", "void(byref,object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.None, false, HybridCpuManagedAbiSupportV1.Unsupported),
        new("__hybridcpu_managed_poll", "void(thread-context)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.RequiredSafepoint, false, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_monitor_enter", "int32(object-ref,deadline-tick:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write | IrMemoryEffectKind.Atomic,
            HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_monitor_exit", "void(object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write | IrMemoryEffectKind.Atomic,
            HybridCpuRuntimeHelperGcTransitionV1.None, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_pinvoke_dispatch", "native-uint(signature-id:nuint,args:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint,
            true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_get_type", "object-ref(type-id:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint,
            true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_reflection_query", "object-ref(type-object:object-ref,query-id:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint,
            true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_task_complete", "void(task:object-ref,status:int32,result:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write | IrMemoryEffectKind.Atomic,
            HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_queue_continuation", "void(task:object-ref,continuation:object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write | IrMemoryEffectKind.Atomic,
            HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_delay", "void(task:object-ref,context:nuint,deadline:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write,
            HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint, true, HybridCpuManagedAbiSupportV1.Supported),
        new("__hybridcpu_managed_throw", "void(object-ref)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint, true, HybridCpuManagedAbiSupportV1.Supported)
        ,new("__hybridcpu_managed_rethrow", "void()", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint, true, HybridCpuManagedAbiSupportV1.Supported)
        ,new("__hybridcpu_managed_endfinally", "void(continuation-token:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint, true, HybridCpuManagedAbiSupportV1.Supported)
        ,new("__hybridcpu_managed_eh_leave_catch", "void()", "managed-helper-v1",
            IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.None, true, HybridCpuManagedAbiSupportV1.Supported)
        ,new("__hybridcpu_managed_exception_transfer", "void(transfer-record:nuint)", "managed-helper-v1",
            IrMemoryEffectKind.Read, HybridCpuRuntimeHelperGcTransitionV1.None, true, HybridCpuManagedAbiSupportV1.Supported)
        ,new("__hybridcpu_managed_divide_u4_checked", "uint32(uint32,uint32)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint,
            true, HybridCpuManagedAbiSupportV1.Supported)
        ,new("__hybridcpu_managed_divide_i4_checked", "int32(int32,int32)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint,
            true, HybridCpuManagedAbiSupportV1.Supported)
        ,new("__hybridcpu_managed_divide_i8_checked", "int64(int64,int64)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint,
            true, HybridCpuManagedAbiSupportV1.Supported)
        ,new("__hybridcpu_managed_remainder_i4_checked", "int32(int32,int32)", "managed-helper-v1",
            IrMemoryEffectKind.Read | IrMemoryEffectKind.Write, HybridCpuRuntimeHelperGcTransitionV1.MaySafepoint,
            true, HybridCpuManagedAbiSupportV1.Supported)
    ];

    public static HybridCpuManagedAbiFamilyV1 Default { get; } = new();

    private HybridCpuManagedAbiFamilyV1()
    {
        TargetContractDigest = HybridCpuTargetMachineContractV1.Default.ContractDigest;
        TargetDataLayoutVersion = HybridCpuTargetMachineContractV1.DataLayoutVersion;
        NativeAbiDigest = HybridCpuNativeAbiContractV2.Default.ContractDigest;

        Dictionary<string, HybridCpuManagedAbiSubcontractV1> subcontracts = new Dictionary<string, HybridCpuManagedAbiSubcontractV1>(StringComparer.Ordinal);
        Add("ManagedCallAbi", HybridCpuManagedAbiSupportV1.Supported,
            "Restricted direct instance/static and exact-bound virtual/interface calls reuse native ABI v2; x5 carries the resolved generic JALR target, while byref, varargs, generic context and transitions fail closed.");
        Add("ManagedReferenceAbi", HybridCpuManagedAbiSupportV1.Supported,
            "Object references are 64-bit aligned zero-null ordinary machine values with runtime-owned identity and validity; managed/interior byrefs remain unsupported.");
        Add("GcInfoAbi", HybridCpuManagedAbiSupportV1.Supported,
            "Deterministic final reference maps are consumed by the qualified runtime-owned single-context non-moving GC V1.");
        Add("SafepointAbi", HybridCpuManagedAbiSupportV1.Supported,
            "Every final call site is a safepoint; Phase 11 additionally qualifies explicit managed poll helper sites for cooperative multi-context rendezvous.");
        Add("RuntimeHelperAbi", HybridCpuManagedAbiSupportV1.ContractOnly,
            "Exact bootstrap, allocation, core managed-shape, type-initialization, dispatch, delegate, poll, monitor, P/Invoke, bounded reflection, cooperative async library and throw helpers are qualified; GC read/write barriers remain unsupported.");
        Add("ThreadContextAbi", HybridCpuManagedAbiSupportV1.Supported,
            "x4 carries an opaque RuntimeKernel execution-context address; ManagedRuntime owns managed Thread identity and deterministic TLS layout over the context TLS base.");
        Add("TransitionThunkAbi", HybridCpuManagedAbiSupportV1.Supported,
            "Exact default-off managed-to-native blittable thunks publish pinned roots and transition state; callbacks, reentrancy and cross-native exception unwind fail closed.");
        Add("EhUnwindAbi", HybridCpuManagedAbiSupportV1.Supported,
            "Final-PC catch/finally tables, final-frame unwind records and a managed-only personality/dispatcher are qualified default-off.");
        Add("TrapMappingAbi", HybridCpuManagedAbiSupportV1.Supported,
            "Digest-bound precise synchronous TrapAbi records may enter a runtime-owned default-off zero-address read/write mapping policy; execute, illegal, privilege and integrity traps remain kernel failures and implicit null checks remain disabled.");
        Add("CodeManagerMetadataAbi", HybridCpuManagedAbiSupportV1.Supported,
            "Deterministic code-range/method/GC/unwind registration is consumed during managed bootstrap.");
        Add("PlatformBootstrapAbi", HybridCpuManagedAbiSupportV1.Supported,
            "Versioned HCEXE bootstrap descriptors bind the neutral platform contract, runtime helper imports and registration tables.");
        Subcontracts = subcontracts;

        Features = Array.AsReadOnly(new[]
        {
            Feature("managed.direct-value-call", HybridCpuManagedAbiSupportV1.Supported, "ManagedCallAbi", "Restricted direct static and instance calls with hidden this are qualified."),
            Feature("managed.virtual-interface-dispatch", HybridCpuManagedAbiSupportV1.Supported, "ManagedCallAbi", "Exact-bound callvirt resolution uses runtime-owned slots and an ordinary relocation-free JALR through x5."),
            Feature("managed.delegates-function-pointers", HybridCpuManagedAbiSupportV1.Supported, "ManagedCallAbi", "Exact managed signatures admit single-cast static, closed-instance and open-instance delegates plus managed calli through ordinary x5/JALR transfers; multicast and native pointers fail closed."),
            Feature("managed.type-tests", HybridCpuManagedAbiSupportV1.Supported, "RuntimeHelperAbi", "Exact-bound castclass and isinst use runtime-owned assignability and null semantics."),
            Feature("managed.object-reference", HybridCpuManagedAbiSupportV1.Supported, "ManagedReferenceAbi", "Exact 64-bit zero-null object-reference carrier and final root-kind preservation are qualified."),
            Feature("managed.byref", HybridCpuManagedAbiSupportV1.Unsupported, "ManagedReferenceAbi", "Interior/byref lifetime semantics are absent."),
            Feature("managed.gc-info-format", HybridCpuManagedAbiSupportV1.Supported, "GcInfoAbi", "Exact final HCMG maps drive runtime-owned precise non-moving collection."),
            Feature("managed.safepoint-format", HybridCpuManagedAbiSupportV1.Supported, "SafepointAbi", "Complete final call-site and explicit poll-site metadata is qualified for runtime rendezvous collection."),
            Feature("managed.runtime-bootstrap-helper", HybridCpuManagedAbiSupportV1.Supported, "RuntimeHelperAbi", "Exact versioned bootstrap helper only."),
            Feature("managed.deterministic-allocation", HybridCpuManagedAbiSupportV1.Supported, "RuntimeHelperAbi", "Runtime-owned TypeDescriptor handle selects deterministic aligned bump/first-fit allocation with collection retry and stable terminal OOM."),
            Feature("managed.precise-stw-nonmoving-gc-v1", HybridCpuManagedAbiSupportV1.Supported, "GcInfoAbi", "Runtime-owned single-context precise mark-sweep scans exact object, array, boxed, static, frame, literal, handle, pin and helper roots."),
            Feature("managed.szarray", HybridCpuManagedAbiSupportV1.Supported, "RuntimeHelperAbi", "Exact SZARRAY descriptors provide checked length, primitive/reference element access and covariance checks; interior references fail closed."),
            Feature("managed.utf16-string", HybridCpuManagedAbiSupportV1.Supported, "RuntimeHelperAbi", "Immutable UTF-16 literal materialization and indexed reads are runtime-owned and deterministic."),
            Feature("managed.exception-message-v1", HybridCpuManagedAbiSupportV1.Supported, "RuntimeHelperAbi", "The bounded System.Exception message constructor/getter pair reads and writes one exact runtime-declared object-reference field; arbitrary CoreLib exception behavior remains fail-closed."),
            Feature("managed.blittable-value-boxing", HybridCpuManagedAbiSupportV1.Supported, "RuntimeHelperAbi", "Only fixed-layout value shapes without managed-reference offsets can be boxed and unboxed."),
            Feature("managed.type-initialization", HybridCpuManagedAbiSupportV1.Supported, "RuntimeHelperAbi", "Single-context ordered exactly-once initialization uses the runtime-owned state machine with sticky failure."),
            Feature("managed.runtime-helpers", HybridCpuManagedAbiSupportV1.ContractOnly, "RuntimeHelperAbi", "Bootstrap, null-check, deterministic allocation and managed throw are qualified individually; barriers and polls fail closed."),
            Feature("managed.tls", HybridCpuManagedAbiSupportV1.Supported, "ThreadContextAbi", "ManagedRuntime owns deterministic ThreadStatic/TLS layout over the RuntimeKernel context TLS base; x4 remains only the opaque context carrier."),
            Feature("managed.threading-rendezvous", HybridCpuManagedAbiSupportV1.Supported, "ThreadContextAbi", "Managed Thread identity/lifecycle and all-thread roots are runtime-owned; the language-neutral kernel coordinates deterministic contexts and STW rendezvous."),
            Feature("managed.synchronization-v1", HybridCpuManagedAbiSupportV1.Supported, "RuntimeHelperAbi", "Naturally aligned 32/64-bit volatile and Interlocked lowering uses existing loads/stores, LR/SC, AMO acquire/release bits and FENCE; ManagedRuntime owns Monitor over kernel address waits."),
            Feature("managed.transitions", HybridCpuManagedAbiSupportV1.Supported, "TransitionThunkAbi", "ManagedRuntime owns exact default-off managed-to-native state, pins and GC deferral over the generic RuntimeKernel host boundary."),
            Feature("managed.interop-v1", HybridCpuManagedAbiSupportV1.Supported, "TransitionThunkAbi", "Cdecl blittable integers, bit-preserving FP carriers, raw pointers, bounded fixed structs and explicit buffer/length pairs are qualified; callbacks and complex marshalling fail closed."),
            Feature("managed.reflection-v1", HybridCpuManagedAbiSupportV1.Supported, "RuntimeHelperAbi", "Explicitly rooted public Type objects and selected member queries use deterministic retained metadata; dynamic code and loading fail closed."),
            Feature("managed.async-runtime-v1", HybridCpuManagedAbiSupportV1.Supported, "RuntimeHelperAbi", "Ordinary compiled CIL state machines use runtime-owned bounded Task state, deterministic continuations and kernel monotonic deadlines; no async opcode or backend path exists."),
            Feature("managed.unwind-record-format", HybridCpuManagedAbiSupportV1.Supported, "EhUnwindAbi", "CFA/SP/FP, saved-register, return-PC and frame-kind records only."),
            Feature("managed.eh-unwind", HybridCpuManagedAbiSupportV1.Supported, "EhUnwindAbi", "Managed catch/finally dispatch uses final-PC metadata and remains independent of architectural traps."),
            Feature("managed.trap-mapping", HybridCpuManagedAbiSupportV1.Supported, "TrapMappingAbi", "RuntimeKernel-authorized exact TrapAbi records permit only the default-off precise zero-address read/write mapping contour; compiler metadata has no fault, execution, publication or retire authority."),
            Feature("managed.code-manager-registration", HybridCpuManagedAbiSupportV1.Supported, "CodeManagerMetadataAbi", "Bootstrap validates and registers canonical code ranges."),
            Feature("managed.platform-bootstrap", HybridCpuManagedAbiSupportV1.Supported, "PlatformBootstrapAbi", "Single-context bootstrap descriptor bound to neutral Platform.Contracts V1."),
            Feature("managed.execution-context-carrier", HybridCpuManagedAbiSupportV1.Supported, "ThreadContextAbi", "x4 carries an opaque RuntimeKernel execution-context address; it is not managed TLS or Thread identity."),
            Feature("managed.pinning-handles", HybridCpuManagedAbiSupportV1.Supported, "ManagedReferenceAbi", "Runtime root registry provides exact handles and explicit pins; non-moving V1 preserves every reachable address.")
        }.OrderBy(static feature => feature.Identity, StringComparer.Ordinal).ToArray());
        RuntimeHelpers = Array.AsReadOnly(HelperTable.OrderBy(static helper => helper.Symbol, StringComparer.Ordinal).ToArray());
        RequiredSafepointState = new(
            HybridCpuNativeAbiContractV2.StackPointerRegister,
            HybridCpuNativeAbiContractV2.FramePointerRegister,
            HybridCpuNativeAbiContractV2.ReturnAddressRegister,
            HybridCpuNativeAbiContractV2.Default.CalleeSavedRegisters,
            RequiresInstructionPointer: true,
            RequiresExactFrameSize: true);
        PlatformContractDigest = HybridCpuPlatformContractV1.ContractDigest;
        ContractDigest = Hash(string.Join('|', SchemaId, $"{SchemaMajor}.{SchemaMinor}", TargetContractDigest,
            TargetDataLayoutVersion, NativeAbiDigest, RuntimePackRevision,
            PlatformContractDigest,
            string.Join('|', SubcontractOrder.Select(identity => Subcontracts[identity].Digest)),
            string.Join('|', Features.Select(FeatureText)), string.Join('|', RuntimeHelpers.Select(HelperText)),
            $"safepoint-state={RequiredSafepointState.StackPointerRegister}:{RequiredSafepointState.FramePointerRegister}:{RequiredSafepointState.ReturnAddressRegister}:{string.Join(',', RequiredSafepointState.PreservedRegisters)}:{RequiredSafepointState.RequiresInstructionPointer}:{RequiredSafepointState.RequiresExactFrameSize}",
            $"budgets={MaximumSafepoints}:{MaximumReferencesPerSafepoint}:{MaximumCodeManagerRecords}"));

        void Add(string identity, HybridCpuManagedAbiSupportV1 support, string scope)
        {
            string schema = $"hybridcpu.managed-abi/{ToKebabCase(identity)}/v1";
            string digest = Hash(string.Join('|', schema, "1.0", support, scope, TargetContractDigest,
                TargetDataLayoutVersion, NativeAbiDigest, RuntimePackRevision));
            subcontracts.Add(identity, new(schema, 1, 0, support, scope, digest));
        }
    }

    public string TargetContractDigest { get; }
    public string TargetDataLayoutVersion { get; }
    public string NativeAbiDigest { get; }
    public string PlatformContractDigest { get; }
    public IReadOnlyDictionary<string, HybridCpuManagedAbiSubcontractV1> Subcontracts { get; }
    public IReadOnlyList<HybridCpuManagedFeatureV1> Features { get; }
    public IReadOnlyList<HybridCpuRuntimeHelperV1> RuntimeHelpers { get; }
    public HybridCpuSafepointMachineStateV1 RequiredSafepointState { get; }
    public string ContractDigest { get; }
    public int ReferenceBitWidth => HybridCpuTargetMachineContractV1.PointerBitWidth;
    public int ReferenceAlignmentBytes => HybridCpuTargetMachineContractV1.PointerAbiAlignmentBytes;
    public ulong NullReferenceValue => 0;
    public int ThreadPointerRegister => HybridCpuNativeAbiContractV2.ThreadPointerRegister;
    public bool HasRuntimeAuthority => false;
    public bool HasObjectLifetimeAuthority => false;
    public bool HasGcPhaseAuthority => false;
    public bool HasThreadSuspensionAuthority => false;
    public bool HasExceptionDispatchAuthority => false;
    public bool HasPublicationAuthority => false;

    public HybridCpuManagedCallLayoutV1 ClassifyCall(HybridCpuManagedCallSignatureV1 signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(signature.Parameters);
        if (signature.Direction == HybridCpuManagedCallDirectionV1.UnmanagedToManaged)
            return CallFailure(HybridCpuPlatformFactStatus.Unsupported, "Reverse P/Invoke and callbacks are outside interop V1.", signature);
        if (signature.Direction == HybridCpuManagedCallDirectionV1.ManagedToUnmanaged &&
            (signature.Parameters.Any(static value => value.Kind == HybridCpuManagedValueKindV1.ObjectReference) ||
             signature.ReturnValue is { Kind: HybridCpuManagedValueKindV1.ObjectReference }))
            return CallFailure(HybridCpuPlatformFactStatus.Unsupported,
                "Managed references cross native transitions only through runtime-owned explicit pins, never direct ABI values.", signature);
        if (signature.IsVarArgs || signature.IsGenericSharedCall || signature.HasRuntimeLookupArgument)
            return CallFailure(HybridCpuPlatformFactStatus.Unsupported, "Varargs, generic context and runtime lookup arguments are not qualified.", signature);
        if (signature.Parameters.Any(static value => value is null) ||
            signature.Parameters.Select(static value => value.Identity).Distinct(StringComparer.Ordinal).Count() != signature.Parameters.Count)
            return CallFailure(HybridCpuPlatformFactStatus.Invalid, "Managed call values must be present and uniquely named.", signature);
        if (signature.Parameters.Any(static value => !IsSupportedCallValue(value)) ||
            signature.ReturnValue is { } result && !IsSupportedCallValue(result))
        {
            HybridCpuPlatformFactStatus status = signature.Parameters.Any(static value => value.Kind == HybridCpuManagedValueKindV1.Unknown) ||
                signature.ReturnValue is { Kind: HybridCpuManagedValueKindV1.Unknown }
                ? HybridCpuPlatformFactStatus.Unknown : HybridCpuPlatformFactStatus.Unsupported;
            return CallFailure(status, "Managed byrefs, interior references or malformed values are outside the managed call v1.2 slice.", signature);
        }

        HybridCpuAbiSignatureV2 nativeSignature = new HybridCpuAbiSignatureV2(
            signature.Parameters.Select(ToNativeValue).ToArray(),
            signature.ReturnValue is null ? null : ToNativeValue(signature.ReturnValue));
        HybridCpuAbiLayoutV2 native = HybridCpuNativeAbiContractV2.Default.Classify(nativeSignature);
        return new(native.Status, native.Reason, native,
            Hash($"managed-call-layout|{ContractDigest}|{CallText(signature)}|{native.Digest}"));
    }

    public HybridCpuManagedCallLayoutV1 ClassifyReceiverLoan(HybridCpuManagedReceiverLoanCallV1 loan)
    {
        ArgumentNullException.ThrowIfNull(loan);
        ArgumentNullException.ThrowIfNull(loan.Signature);
        HybridCpuManagedCallSignatureV1 signature = loan.Signature;
        bool exactIdentity = loan.ScopedTypeIdentity is { Length: > 1 } &&
            !loan.ScopedTypeIdentity.EndsWith('&') &&
            loan.ReceiverParameterIndex >= 0 && loan.ReceiverParameterIndex < signature.Parameters.Count &&
            signature.Parameters[loan.ReceiverParameterIndex] is { Kind: HybridCpuManagedValueKindV1.ManagedByRef } receiver &&
            string.Equals(receiver.Identity, loan.ScopedTypeIdentity + "&", StringComparison.Ordinal);
        bool exactExtent = loan.PayloadSizeBytes is > 0 and <= 16 &&
            loan.PayloadAlignmentBytes is 1 or 2 or 4 or 8 or 16 &&
            loan.CallerStorageSizeBytes == loan.PayloadSizeBytes &&
            loan.CallerStorageAlignmentBytes >= loan.PayloadAlignmentBytes &&
            loan.CallerStorageAlignmentBytes is 1 or 2 or 4 or 8 or 16;
        bool closedSignature = signature.Direction == HybridCpuManagedCallDirectionV1.ManagedToManaged &&
            !signature.IsVarArgs && !signature.IsGenericSharedCall && !signature.HasRuntimeLookupArgument &&
            signature.Parameters.All(static value => value is not null) &&
            signature.ReturnValue?.Kind is not (HybridCpuManagedValueKindV1.ManagedByRef or HybridCpuManagedValueKindV1.InteriorReference) &&
            signature.Parameters.Where((_, index) => index != loan.ReceiverParameterIndex)
                .All(IsSupportedCallValue);
        bool proof = loan.CallerStorageNonNull && loan.CallerStorageBoundsProven && loan.NonEscaping &&
            loan.NoSafepoints && loan.ReferenceFree &&
            IsDigest(loan.ImporterPlanDigest) && IsDigest(loan.CallerStorageProofDigest);
        if (!exactIdentity || !exactExtent || !closedSignature || !proof)
            return new(HybridCpuPlatformFactStatus.Unsupported,
                "Receiver loan requires one exact Type&, equal-sized aligned caller storage, non-null bounds, noescape/no-safepoint lifetime, reference-free payload and two digest-bound proofs.",
                null, Hash($"receiver-loan-failure|{ContractDigest}|{ReceiverLoanText(loan)}"));

        HybridCpuAbiValueV2[] parameters = signature.Parameters.Select((value, index) =>
            index == loan.ReceiverParameterIndex
                ? new HybridCpuAbiValueV2(value.Identity, HybridCpuAbiValueKindV2.Pointer,
                    HybridCpuTargetMachineContractV1.PointerBitWidth / 8,
                    HybridCpuTargetMachineContractV1.PointerAbiAlignmentBytes)
                : ToNativeValue(value)).ToArray();
        HybridCpuAbiLayoutV2 native = HybridCpuNativeAbiContractV2.Default.Classify(new(parameters,
            signature.ReturnValue is null ? null : ToNativeValue(signature.ReturnValue)));
        return new(native.Status, native.Reason, native,
            Hash($"managed-receiver-loan-layout|{ContractDigest}|{ReceiverLoanText(loan)}|{native.Digest}"));
    }

    public HybridCpuManagedAbiEnvelopeV1 CreateEnvelope(IReadOnlyList<string>? requiredSupportedFeatures = null) => new(
        SchemaMajor,
        SchemaMinor,
        ContractDigest,
        TargetContractDigest,
        NativeAbiDigest,
        RuntimePackRevision,
        Subcontracts.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value.Digest, StringComparer.Ordinal),
        (requiredSupportedFeatures ?? Array.Empty<string>()).OrderBy(static feature => feature, StringComparer.Ordinal).ToArray());

    public HybridCpuRuntimeHelperV1? ResolveRuntimeHelper(string symbol) =>
        string.IsNullOrWhiteSpace(symbol) ? null : RuntimeHelpers.FirstOrDefault(helper => string.Equals(helper.Symbol, symbol, StringComparison.Ordinal));

    private HybridCpuManagedCallLayoutV1 CallFailure(
        HybridCpuPlatformFactStatus status,
        string reason,
        HybridCpuManagedCallSignatureV1 signature) =>
        new(status, reason, null, Hash($"managed-call-failure|{ContractDigest}|{status}|{reason}|{CallText(signature)}"));

    private static bool IsSupportedCallValue(HybridCpuManagedValueV1 value) =>
        value.Kind is (HybridCpuManagedValueKindV1.PrimitiveInteger or HybridCpuManagedValueKindV1.BlittableValue or HybridCpuManagedValueKindV1.ObjectReference) &&
        !string.IsNullOrWhiteSpace(value.Identity) && value.SizeBytes is > 0 and <= 16 &&
        value.AlignmentBytes is 1 or 2 or 4 or 8 or 16 &&
        (value.Kind != HybridCpuManagedValueKindV1.ObjectReference ||
         value.SizeBytes == HybridCpuTargetMachineContractV1.PointerBitWidth / 8 &&
         value.AlignmentBytes == HybridCpuTargetMachineContractV1.PointerAbiAlignmentBytes);

    private static HybridCpuAbiValueV2 ToNativeValue(HybridCpuManagedValueV1 value) => new(
        value.Identity,
        value.Kind == HybridCpuManagedValueKindV1.BlittableValue ? HybridCpuAbiValueKindV2.Aggregate :
            value.Kind == HybridCpuManagedValueKindV1.ObjectReference ? HybridCpuAbiValueKindV2.Pointer : HybridCpuAbiValueKindV2.Integer,
        value.SizeBytes,
        value.AlignmentBytes);

    private static HybridCpuManagedFeatureV1 Feature(
        string identity,
        HybridCpuManagedAbiSupportV1 support,
        string owner,
        string reason) => new(identity, support, owner, reason);

    private static string FeatureText(HybridCpuManagedFeatureV1 feature) =>
        $"{feature.Identity}:{feature.Support}:{feature.OwningSubcontract}:{feature.Reason}";

    private static string HelperText(HybridCpuRuntimeHelperV1 helper) =>
        $"{helper.Symbol}:{helper.Signature}:{helper.CallingConvention}:{helper.MemoryEffects}:{helper.GcTransition}:{helper.MayThrow}:{helper.Support}";

    private static string CallText(HybridCpuManagedCallSignatureV1 signature) => string.Join('|',
        signature.Direction,
        string.Join(';', signature.Parameters.Select(ValueText)),
        signature.ReturnValue is null ? "void" : ValueText(signature.ReturnValue),
        signature.IsVarArgs, signature.IsGenericSharedCall, signature.HasRuntimeLookupArgument);

    private static string ValueText(HybridCpuManagedValueV1 value) =>
        value is null ? "null" : $"{value.Identity}:{value.Kind}:{value.SizeBytes}:{value.AlignmentBytes}";

    private static bool IsDigest(string value) => value is { Length: 64 } &&
        value.All(static character => char.IsAsciiHexDigit(character));

    private static string ReceiverLoanText(HybridCpuManagedReceiverLoanCallV1 loan) => string.Join('|',
        CallText(loan.Signature), loan.ReceiverParameterIndex, loan.ScopedTypeIdentity,
        loan.PayloadSizeBytes, loan.PayloadAlignmentBytes, loan.CallerStorageSizeBytes,
        loan.CallerStorageAlignmentBytes, loan.CallerStorageNonNull, loan.CallerStorageBoundsProven,
        loan.NonEscaping, loan.NoSafepoints, loan.ReferenceFree,
        loan.ImporterPlanDigest?.ToLowerInvariant() ?? "null",
        loan.CallerStorageProofDigest?.ToLowerInvariant() ?? "null");

    private static string ToKebabCase(string value)
    {
        StringBuilder builder = new StringBuilder(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (index > 0 && char.IsUpper(character)) builder.Append('-');
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    internal static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

public static class HybridCpuManagedAbiEncodingV1
{
    private const uint CallFrameMagic = 0x434d4348; // HCMC, little endian
    private const uint GcInfoMagic = 0x474d4348; // HCMG, little endian
    private const uint CodeManagerMagic = 0x4d4d4348; // HCMM, little endian

    public static byte[] EncodeCallFrame(HybridCpuManagedCallLayoutV1 layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Status != HybridCpuPlatformFactStatus.Supported || layout.NativeLayout is null)
            throw new ArgumentException("Only supported managed call layouts can be encoded.", nameof(layout));
        HybridCpuAbiLayoutV2 native = layout.NativeLayout;
        int locationCount = native.Parameters.Count + (native.ReturnValue is null ? 0 : 1);
        byte[] bytes = new byte[20 + locationCount * 12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, CallFrameMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), HybridCpuManagedAbiFamilyV1.SchemaMajor);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), HybridCpuManagedAbiFamilyV1.SchemaMinor);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), native.Parameters.Count);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), native.ReturnValue is null ? 0 : 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), native.StackArgumentBytes);
        int offset = 20;
        foreach (HybridCpuAbiLocationV2 location in native.Parameters.Append(native.ReturnValue).Where(static location => location is not null)!)
        {
            bytes[offset] = (byte)location.Kind;
            bytes[offset + 1] = (byte)Math.Min(2, location.Registers.Count);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + 2), checked((short)(location.Registers.Count > 0 ? location.Registers[0] : -1)));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4), location.StackOffsetBytes ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 8), location.SizeBytes);
            offset += 12;
        }
        return bytes;
    }

    public static HybridCpuGcInfoEncodingResultV1 EncodeGcInfo(IReadOnlyList<HybridCpuSafepointRecordV1> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        HybridCpuManagedAbiFamilyV1 family = HybridCpuManagedAbiFamilyV1.Default;
        if (records.Count > HybridCpuManagedAbiFamilyV1.MaximumSafepoints ||
            records.Any(static record => record is null || record.LiveReferences is null ||
                record.LiveReferences.Count > HybridCpuManagedAbiFamilyV1.MaximumReferencesPerSafepoint))
            return Failure(HybridCpuPlatformFactStatus.Unsupported, "GC-info deterministic budgets were exceeded.", records);
        if (records.Select(static record => record.CodeOffsetBytes).Distinct().Count() != records.Count ||
            records.Any(static record => record.CodeOffsetBytes < 0))
            return Failure(HybridCpuPlatformFactStatus.Invalid, "Safepoint code offsets must be non-negative and unique.", records);
        foreach (HybridCpuGcReferenceLocationV1 location in records.SelectMany(static record => record.LiveReferences))
        {
            if (location is null || string.IsNullOrWhiteSpace(location.ValueIdentity))
                return Failure(HybridCpuPlatformFactStatus.Invalid, "GC reference identities must be explicit.", records);
            if (location.ReferenceKind is HybridCpuGcReferenceKindV1.ManagedByRef or HybridCpuGcReferenceKindV1.InteriorReference)
                return Failure(HybridCpuPlatformFactStatus.Unsupported, "Managed byref and interior-reference maps are not qualified.", records);
            bool valid = location.LocationKind switch
            {
                HybridCpuGcLocationKindV1.Register => location.RegisterId is >= 0 and < HybridCpuTargetMachineContractV1.ArchitecturalRegisterCount && location.StackOffsetBytes is null,
                HybridCpuGcLocationKindV1.Stack => location.RegisterId is null && location.StackOffsetBytes is >= 0 && location.StackOffsetBytes % family.ReferenceAlignmentBytes == 0,
                _ => false
            };
            if (!valid) return Failure(HybridCpuPlatformFactStatus.Invalid, "GC reference location is malformed or unaligned.", records);
        }

        HybridCpuSafepointRecordV1[] ordered = records.OrderBy(static record => record.CodeOffsetBytes).ToArray();
        int referenceCount = ordered.Sum(static record => record.LiveReferences.Count);
        byte[] bytes = new byte[12 + ordered.Length * 8 + referenceCount * 16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, GcInfoMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), ordered.Length);
        int offset = 12;
        foreach (HybridCpuSafepointRecordV1 record in ordered)
        {
            HybridCpuGcReferenceLocationV1[] locations = record.LiveReferences
                .OrderBy(static location => location.ValueIdentity, StringComparer.Ordinal)
                .ThenBy(static location => location.LocationKind)
                .ThenBy(static location => location.RegisterId)
                .ThenBy(static location => location.StackOffsetBytes)
                .ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), record.CodeOffsetBytes);
            bytes[offset + 4] = (byte)record.Category;
            bytes[offset + 5] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 6), checked((ushort)locations.Length));
            offset += 8;
            foreach (HybridCpuGcReferenceLocationV1 location in locations)
            {
                SHA256.HashData(Encoding.UTF8.GetBytes(location.ValueIdentity)).AsSpan(0, 8).CopyTo(bytes.AsSpan(offset, 8));
                bytes[offset + 8] = (byte)location.ReferenceKind;
                bytes[offset + 9] = (byte)location.LocationKind;
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + 10), checked((short)(location.RegisterId ?? -1)));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 12), location.StackOffsetBytes ?? -1);
                offset += 16;
            }
        }
        return new(HybridCpuPlatformFactStatus.Supported, "GC-info metadata encoded deterministically.", bytes,
            HybridCpuManagedAbiFamilyV1.Hash($"gc-info|{family.ContractDigest}|{Convert.ToHexString(bytes)}"));
    }

    public static HybridCpuPlatformFactStatus ValidateReferenceCoverage(
        HybridCpuSafepointRecordV1 record,
        IReadOnlyList<string> requiredLiveReferenceIdentities)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(requiredLiveReferenceIdentities);
        if (requiredLiveReferenceIdentities.Any(string.IsNullOrWhiteSpace) ||
            requiredLiveReferenceIdentities.Distinct(StringComparer.Ordinal).Count() != requiredLiveReferenceIdentities.Count)
            return HybridCpuPlatformFactStatus.Invalid;
        string[] actual = record.LiveReferences.Select(static location => location.ValueIdentity)
            .OrderBy(static identity => identity, StringComparer.Ordinal).ToArray();
        string[] required = requiredLiveReferenceIdentities.OrderBy(static identity => identity, StringComparer.Ordinal).ToArray();
        return actual.SequenceEqual(required, StringComparer.Ordinal)
            ? HybridCpuPlatformFactStatus.Supported
            : HybridCpuPlatformFactStatus.Unsupported;
    }

    public static byte[] EncodeCodeManagerMetadata(IReadOnlyList<HybridCpuCodeManagerMethodRecordV1> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count > HybridCpuManagedAbiFamilyV1.MaximumCodeManagerRecords ||
            records.Any(static record => record is null || string.IsNullOrWhiteSpace(record.MethodIdentity) ||
                record.CodeStartOffsetBytes < 0 || record.CodeSizeBytes <= 0 || !IsSha256(record.GcInfoDigest) ||
                record.UnwindInfoDigest is not null && !IsSha256(record.UnwindInfoDigest)) ||
            records.Select(static record => record.MethodIdentity).Distinct(StringComparer.Ordinal).Count() != records.Count)
            throw new ArgumentException("Code-manager metadata is malformed or exceeds deterministic budgets.", nameof(records));
        HybridCpuCodeManagerMethodRecordV1[] ordered = records.OrderBy(static record => record.CodeStartOffsetBytes)
            .ThenBy(static record => record.MethodIdentity, StringComparer.Ordinal).ToArray();
        byte[] bytes = new byte[12 + ordered.Length * 80];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, CodeManagerMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), ordered.Length);
        int offset = 12;
        foreach (HybridCpuCodeManagerMethodRecordV1 record in ordered)
        {
            SHA256.HashData(Encoding.UTF8.GetBytes(record.MethodIdentity)).AsSpan(0, 8).CopyTo(bytes.AsSpan(offset, 8));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 8), record.CodeStartOffsetBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 12), record.CodeSizeBytes);
            Convert.FromHexString(record.GcInfoDigest).CopyTo(bytes, offset + 16);
            if (record.UnwindInfoDigest is not null)
                Convert.FromHexString(record.UnwindInfoDigest).CopyTo(bytes, offset + 48);
            offset += 80;
        }
        return bytes;
    }

    private static HybridCpuGcInfoEncodingResultV1 Failure(
        HybridCpuPlatformFactStatus status,
        string reason,
        IReadOnlyList<HybridCpuSafepointRecordV1> records) =>
        new(status, reason, Array.Empty<byte>(), HybridCpuManagedAbiFamilyV1.Hash($"gc-info-failure|{status}|{reason}|{records.Count}"));

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(static character => char.IsAsciiHexDigit(character));
}

/// <summary>Fail-closed compatibility seam for a future runtime/code-manager consumer.</summary>
public sealed class HybridCpuManagedAbiConsumerV1
{
    public HybridCpuManagedAbiCompatibilityV1 CheckCompatibility(
        HybridCpuManagedAbiEnvelopeV1 envelope,
        int supportedMajor = HybridCpuManagedAbiFamilyV1.SchemaMajor,
        int supportedMinor = HybridCpuManagedAbiFamilyV1.SchemaMinor,
        string? targetContractDigest = null,
        string? nativeAbiDigest = null,
        string? runtimePackRevision = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        HybridCpuManagedAbiFamilyV1 family = HybridCpuManagedAbiFamilyV1.Default;
        if (envelope.FamilyMajor != supportedMajor) return HybridCpuManagedAbiCompatibilityV1.UnsupportedMajorVersion;
        if (envelope.FamilyMinor > supportedMinor) return HybridCpuManagedAbiCompatibilityV1.UnsupportedMinorVersion;
        if (!string.Equals(envelope.TargetContractDigest, targetContractDigest ?? family.TargetContractDigest, StringComparison.Ordinal))
            return HybridCpuManagedAbiCompatibilityV1.TargetMismatch;
        if (!string.Equals(envelope.NativeAbiDigest, nativeAbiDigest ?? family.NativeAbiDigest, StringComparison.Ordinal))
            return HybridCpuManagedAbiCompatibilityV1.NativeAbiMismatch;
        if (!string.Equals(envelope.RuntimePackRevision, runtimePackRevision ?? HybridCpuManagedAbiFamilyV1.RuntimePackRevision, StringComparison.Ordinal))
            return HybridCpuManagedAbiCompatibilityV1.RuntimePackMismatch;
        if (!string.Equals(envelope.FamilyDigest, family.ContractDigest, StringComparison.Ordinal) ||
            envelope.SubcontractDigests.Count != family.Subcontracts.Count ||
            family.Subcontracts.Any(pair => !envelope.SubcontractDigests.TryGetValue(pair.Key, out string? digest) ||
                !string.Equals(digest, pair.Value.Digest, StringComparison.Ordinal)))
            return HybridCpuManagedAbiCompatibilityV1.SubcontractMismatch;
        foreach (string feature in envelope.RequiredSupportedFeatures)
        {
            HybridCpuManagedFeatureV1? descriptor = family.Features.FirstOrDefault(candidate => string.Equals(candidate.Identity, feature, StringComparison.Ordinal));
            if (descriptor?.Support != HybridCpuManagedAbiSupportV1.Supported)
                return HybridCpuManagedAbiCompatibilityV1.MissingRequiredSupport;
        }
        return HybridCpuManagedAbiCompatibilityV1.Compatible;
    }

    public HybridCpuManagedAbiCompatibilityV1 ValidateGcInfoHeader(
        HybridCpuManagedAbiEnvelopeV1 envelope,
        ReadOnlySpan<byte> metadata)
    {
        HybridCpuManagedAbiCompatibilityV1 compatibility = CheckCompatibility(envelope);
        if (compatibility != HybridCpuManagedAbiCompatibilityV1.Compatible) return compatibility;
        HybridCpuManagedAbiFamilyV1 family = HybridCpuManagedAbiFamilyV1.Default;
        if (metadata.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(metadata) != 0x474d4348)
            return HybridCpuManagedAbiCompatibilityV1.InvalidMetadata;
        int major = BinaryPrimitives.ReadUInt16LittleEndian(metadata[4..]);
        int minor = BinaryPrimitives.ReadUInt16LittleEndian(metadata[6..]);
        if (major != 1) return HybridCpuManagedAbiCompatibilityV1.UnsupportedMajorVersion;
        if (minor > 0) return HybridCpuManagedAbiCompatibilityV1.UnsupportedMinorVersion;
        int count = BinaryPrimitives.ReadInt32LittleEndian(metadata[8..]);
        if (count is < 0 or > HybridCpuManagedAbiFamilyV1.MaximumSafepoints)
            return HybridCpuManagedAbiCompatibilityV1.InvalidMetadata;
        int offset = 12;
        int previousCodeOffset = -1;
        for (int record = 0; record < count; record++)
        {
            if (metadata.Length - offset < 8) return HybridCpuManagedAbiCompatibilityV1.InvalidMetadata;
            int codeOffset = BinaryPrimitives.ReadInt32LittleEndian(metadata[offset..]);
            byte category = metadata[offset + 4];
            int referenceCount = BinaryPrimitives.ReadUInt16LittleEndian(metadata[(offset + 6)..]);
            if (codeOffset < 0 || codeOffset <= previousCodeOffset || category > (byte)HybridCpuSafepointCategoryV1.PollSite ||
                referenceCount > HybridCpuManagedAbiFamilyV1.MaximumReferencesPerSafepoint)
                return HybridCpuManagedAbiCompatibilityV1.InvalidMetadata;
            previousCodeOffset = codeOffset;
            offset += 8;
            if (metadata.Length - offset < referenceCount * 16) return HybridCpuManagedAbiCompatibilityV1.InvalidMetadata;
            for (int reference = 0; reference < referenceCount; reference++)
            {
                byte referenceKind = metadata[offset + 8];
                byte locationKind = metadata[offset + 9];
                int register = BinaryPrimitives.ReadInt16LittleEndian(metadata[(offset + 10)..]);
                int stackOffset = BinaryPrimitives.ReadInt32LittleEndian(metadata[(offset + 12)..]);
                bool valid = referenceKind == (byte)HybridCpuGcReferenceKindV1.ObjectReference && locationKind switch
                {
                    (byte)HybridCpuGcLocationKindV1.Register => register is >= 0 and < HybridCpuTargetMachineContractV1.ArchitecturalRegisterCount && stackOffset == -1,
                    (byte)HybridCpuGcLocationKindV1.Stack => register == -1 && stackOffset >= 0 && stackOffset % family.ReferenceAlignmentBytes == 0,
                    _ => false
                };
                if (!valid) return HybridCpuManagedAbiCompatibilityV1.InvalidMetadata;
                offset += 16;
            }
        }
        return offset == metadata.Length
            ? HybridCpuManagedAbiCompatibilityV1.Compatible
            : HybridCpuManagedAbiCompatibilityV1.InvalidMetadata;
    }
}
