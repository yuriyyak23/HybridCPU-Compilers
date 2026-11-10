using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Platform.Contracts;

[Flags]
public enum HybridCpuVmProtectionV1 : byte
{
    None = 0,
    Read = 1,
    Write = 2,
    Execute = 4
}

public enum HybridCpuKernelStatusV1 : byte
{
    Success = 0,
    InvalidRequest = 1,
    VersionMismatch = 2,
    AddressConflict = 3,
    BudgetExhausted = 4,
    Unsupported = 5,
    NoCurrentContext = 6,
    TrapStateMismatch = 7,
    ProcessExited = 8
}

public enum HybridCpuStartupFailureKindV1 : byte
{
    None = 0,
    ImageMismatch = 1,
    RuntimeContractViolation = 2,
    KernelFailure = 3,
    ManagedException = 4
}

public enum HybridCpuHostServiceV1 : ushort
{
    ProcessExit = 1,
    Console = 2,
    File = 3,
    Network = 4,
    Clock = 5,
    Graphics = 6,
    Input = 7,
    ManagedRuntime = 8
}

public readonly record struct HybridCpuVmRangeV1(
    ulong Address,
    ulong Size,
    HybridCpuVmProtectionV1 Protection);

public sealed record HybridCpuBootInfoV1(
    string PlatformContractDigest,
    string ImageContractDigest,
    ulong ImageBase,
    ulong ImageSize,
    ulong EntryAddress,
    ulong StackBase,
    ulong StackSize,
    int InitialVirtualThreadId,
    ulong VirtualClockTicksPerSecond = 1_000);

public sealed record HybridCpuExecutionContextDescriptorV1(
    ulong ContextId,
    int VirtualThreadCarrier,
    ulong ContextCarrierAddress,
    ulong EntryAddress,
    ulong StackBase,
    ulong StackSize,
    ulong InitialStackPointer,
    IReadOnlyList<HybridCpuVmRangeV1> VmMappings);

public readonly record struct HybridCpuArchitecturalTrapFrameV1(
    int VirtualThreadId,
    ulong ProgramCounter,
    ulong StackPointer,
    ulong Cause,
    ulong FaultAddress);

public enum HybridCpuArchitecturalTrapClassV1 : byte
{
    Unknown = 0,
    SynchronousMemoryFault = 1,
    IllegalInstruction = 2,
    PrivilegeViolation = 3,
    IntegrityFailure = 4
}

public enum HybridCpuMemoryAccessKindV1 : byte
{
    None = 0,
    Read = 1,
    Write = 2,
    Execute = 3
}

public enum HybridCpuPrivilegeModeV1 : byte
{
    User = 0,
    Supervisor = 1,
    Machine = 2
}

public enum HybridCpuExecutionModeV1 : byte
{
    ManagedUser = 0,
    NativeUser = 1,
    Kernel = 2,
    VirtualizedGuest = 3
}

public enum HybridCpuFaultAddressSemanticV1 : byte
{
    None = 0,
    VirtualAddress = 1,
    GuestPhysicalAddress = 2,
    HostPhysicalAddress = 3
}

public enum HybridCpuTrapResumePolicyV1 : byte
{
    ProcessFailure = 0,
    RetryFaultingInstruction = 1,
    ManagedDispatch = 2
}

public sealed record HybridCpuCommittedRegisterStateV1(
    ulong ProgramCounter,
    ulong StackPointer,
    IReadOnlyList<ulong> IntegerRegisters,
    string StateDigest);

public sealed record HybridCpuArchitecturalTrapRecordV1(
    ulong ContextId,
    ulong Sequence,
    HybridCpuArchitecturalTrapFrameV1 Frame,
    HybridCpuArchitecturalTrapClassV1 TrapClass,
    ulong Subcause,
    HybridCpuMemoryAccessKindV1 AccessKind,
    HybridCpuPrivilegeModeV1 PrivilegeMode,
    HybridCpuExecutionModeV1 ExecutionMode,
    bool HasFaultAddress,
    HybridCpuFaultAddressSemanticV1 FaultAddressSemantic,
    HybridCpuCommittedRegisterStateV1 CommittedState,
    bool FaultingInstructionRetired,
    bool NoYoungerArchitecturalPublication,
    HybridCpuTrapResumePolicyV1 ResumePolicy,
    string RecordDigest);

public static class HybridCpuTrapAbiV1
{
    public const string SchemaId = "hybridcpu.trap-abi/v1";
    public const int SchemaVersion = 1;
    public const int IntegerRegisterCount = 32;
    public static string ContractDigest { get; } = Hash(string.Join('|',
        SchemaId, SchemaVersion, IntegerRegisterCount, "context:sequence:vt:pc:sp:cause:subcause",
        "class:access:privilege:mode:fault-address:committed-registers",
        "faulting-instruction-not-retired:no-younger-publication:resume-policy",
        "cpu-owns-fault-legality-publication-retire", "kernel-owns-classification",
        "managed-runtime-owns-approved-mapping"));

    public static HybridCpuCommittedRegisterStateV1 CreateCommittedState(
        ulong programCounter, ulong stackPointer, IEnumerable<ulong> integerRegisters)
    {
        ArgumentNullException.ThrowIfNull(integerRegisters);
        ulong[] registers = integerRegisters.ToArray();
        if (registers.Length != IntegerRegisterCount)
            throw new ArgumentException($"TrapAbi requires exactly {IntegerRegisterCount} committed integer registers.",
                nameof(integerRegisters));
        string digest = ComputeStateDigest(programCounter, stackPointer, registers);
        return new(programCounter, stackPointer, Array.AsReadOnly(registers), digest);
    }

    public static HybridCpuArchitecturalTrapRecordV1 CreateRecord(
        ulong contextId, ulong sequence, HybridCpuArchitecturalTrapFrameV1 frame,
        HybridCpuArchitecturalTrapClassV1 trapClass, ulong subcause,
        HybridCpuMemoryAccessKindV1 accessKind, HybridCpuPrivilegeModeV1 privilegeMode,
        HybridCpuExecutionModeV1 executionMode, bool hasFaultAddress,
        HybridCpuFaultAddressSemanticV1 faultAddressSemantic,
        HybridCpuCommittedRegisterStateV1 committedState, bool faultingInstructionRetired,
        bool noYoungerArchitecturalPublication, HybridCpuTrapResumePolicyV1 resumePolicy)
    {
        ArgumentNullException.ThrowIfNull(committedState);
        HybridCpuArchitecturalTrapRecordV1 draft = new HybridCpuArchitecturalTrapRecordV1(contextId, sequence, frame, trapClass, subcause,
            accessKind, privilegeMode, executionMode, hasFaultAddress, faultAddressSemantic, committedState,
            faultingInstructionRetired, noYoungerArchitecturalPublication, resumePolicy, string.Empty);
        if (!TryValidate(draft, requireRecordDigest: false, out string reason))
            throw new ArgumentException(reason, nameof(frame));
        return draft with { RecordDigest = ComputeRecordDigest(draft) };
    }

    public static bool TryValidate(HybridCpuArchitecturalTrapRecordV1? record, out string reason) =>
        TryValidate(record, requireRecordDigest: true, out reason);

    public static string ComputeRecordDigest(HybridCpuArchitecturalTrapRecordV1 record) =>
        Hash(string.Join('|', ContractDigest, record.ContextId, record.Sequence,
            record.Frame.VirtualThreadId, record.Frame.ProgramCounter, record.Frame.StackPointer,
            record.Frame.Cause, record.Frame.FaultAddress, record.TrapClass, record.Subcause, record.AccessKind,
            record.PrivilegeMode, record.ExecutionMode, record.HasFaultAddress, record.FaultAddressSemantic,
            record.CommittedState.StateDigest, record.FaultingInstructionRetired,
            record.NoYoungerArchitecturalPublication, record.ResumePolicy));

    private static bool TryValidate(HybridCpuArchitecturalTrapRecordV1? record, bool requireRecordDigest,
        out string reason)
    {
        if (record is null || record.CommittedState is null || record.ContextId == 0 || record.Sequence == 0 ||
            record.Frame.VirtualThreadId is < 0 or > 3 || record.Frame.StackPointer == 0 ||
            !Enum.IsDefined(record.TrapClass) || !Enum.IsDefined(record.AccessKind) ||
            !Enum.IsDefined(record.PrivilegeMode) || !Enum.IsDefined(record.ExecutionMode) ||
            !Enum.IsDefined(record.FaultAddressSemantic) || !Enum.IsDefined(record.ResumePolicy) ||
            record.CommittedState.IntegerRegisters is null ||
            record.CommittedState.IntegerRegisters.Count != IntegerRegisterCount ||
            record.CommittedState.ProgramCounter != record.Frame.ProgramCounter ||
            record.CommittedState.StackPointer != record.Frame.StackPointer ||
            record.CommittedState.StateDigest != ComputeStateDigest(record.CommittedState.ProgramCounter,
                record.CommittedState.StackPointer, record.CommittedState.IntegerRegisters) ||
            record.HasFaultAddress != (record.FaultAddressSemantic != HybridCpuFaultAddressSemanticV1.None) ||
            !record.HasFaultAddress && record.Frame.FaultAddress != 0)
        {
            reason = "TrapAbi record or committed architectural state is malformed.";
            return false;
        }
        if (requireRecordDigest && record.RecordDigest != ComputeRecordDigest(record))
        {
            reason = "TrapAbi record digest does not bind its exact architectural facts.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static string ComputeStateDigest(ulong programCounter, ulong stackPointer,
        IReadOnlyList<ulong> registers) => Hash(string.Join('|',
            SchemaId, "committed-state", programCounter, stackPointer, string.Join(',', registers)));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record HybridCpuHostTransitionRequestV1(
    HybridCpuHostServiceV1 Service,
    ulong Operation,
    IReadOnlyList<ulong> Arguments);

public sealed record HybridCpuHostTransitionResultV1(
    HybridCpuKernelStatusV1 Status,
    ulong ReturnValue,
    string Reason);

public sealed record HybridCpuRuntimeHelperImportV1(
    string Symbol,
    string Signature,
    bool Required);

public sealed record HybridCpuCodeManagerRegistrationV1(
    string MethodIdentity,
    int CodeStartOffsetBytes,
    int CodeSizeBytes,
    string GcInfoDigest,
    string UnwindInfoDigest);

public sealed record HybridCpuManagedStackMapRegistrationV1(
    string MethodIdentity,
    byte[] GcInfo,
    byte[] CodeManagerMetadata,
    string ManagedAbiDigest,
    string TargetPlatformDigest,
    string NativeAbiDigest,
    string RuntimePackRevision,
    byte[]? UnwindInfo = null);

public enum HybridCpuManagedEhClauseKindV1 : byte
{
    Catch = 0,
    Finally = 1
}

public sealed record HybridCpuManagedEhClauseRegistrationV1(
    HybridCpuManagedEhClauseKindV1 Kind,
    int TryStartOffsetBytes,
    int TrySizeBytes,
    int HandlerStartOffsetBytes,
    int HandlerSizeBytes,
    ulong CatchTypeId,
    int Ordinal);

public sealed record HybridCpuManagedEhMethodRegistrationV1(
    string MethodIdentity,
    int CodeStartOffsetBytes,
    int CodeSizeBytes,
    byte[] EhInfo,
    byte[] UnwindInfo);

public sealed record HybridCpuManagedEhFrameSnapshotV1(
    string MethodIdentity,
    int InstructionPointer,
    ulong StackPointer,
    ulong ReturnProgramCounter,
    ulong FramePointer = 0,
    bool InstructionPointerIsReturnAddress = false);

public static class HybridCpuManagedEhSchemaV1
{
    public const string SchemaId = "hybridcpu.managed-eh-unwind/v1";
    public const uint EhMagic = 0x48454348; // HCEH
    public const uint UnwindMagic = 0x32574348; // HCW2
    public const ushort SchemaVersion = 1;
    public const int MaximumClausesPerMethod = 256;
    public const int MaximumOperationsPerMethod = 4096;
    public const int MaximumFramesPerDispatch = 4096;
    public const int MaximumFinallyInvocations = 4096;
}

public sealed record HybridCpuManagedGcFrameSnapshotV1(
    string MethodIdentity,
    int InstructionPointer,
    IReadOnlyList<ulong> Registers,
    IReadOnlyDictionary<int, ulong> StackSlots);

public sealed record HybridCpuManagedMethodEntryV1(
    ulong MethodId,
    string StableIdentity,
    ulong DeclaringTypeId,
    string SignatureIdentity,
    ulong CodeAddress);

public sealed record HybridCpuManagedVirtualDispatchEntryV1(
    ulong RuntimeTypeId,
    ulong SlotId,
    ulong MethodId,
    ulong CodeAddress);

public sealed record HybridCpuManagedInterfaceDispatchEntryV1(
    ulong RuntimeTypeId,
    ulong InterfaceTypeId,
    ulong SlotId,
    ulong MethodId,
    ulong CodeAddress);

public enum HybridCpuManagedDelegateKindV1 : byte
{
    Static = 0,
    ClosedInstance = 1,
    OpenInstance = 2
}

public sealed record HybridCpuManagedFunctionPointerEntryV1(
    ulong MethodId,
    ulong SignatureId,
    ulong CodeAddress,
    bool IsInstanceMethod,
    ulong InvocationThunkAddress);

public sealed record HybridCpuManagedDelegateLayoutV1(
    int TargetObjectOffsetBytes,
    int CodePointerOffsetBytes,
    int ContextOffsetBytes,
    int SignatureIdOffsetBytes,
    int KindOffsetBytes,
    int InvocationThunkOffsetBytes,
    int MinimumObjectSizeBytes);

public sealed record HybridCpuStaticRootRegistrationV1(
    string Identity,
    ulong Address,
    ulong Size);

public sealed record HybridCpuModuleInitializerRegistrationV1(
    string ModuleIdentity,
    string InitializerSymbol,
    int Order,
    ulong? TypeId = null);

public sealed record HybridCpuManagedTypeRegistrationV1(
    ulong TypeId,
    string StableIdentity,
    string DescriptorDigest,
    int MetadataOffsetBytes,
    int MetadataSizeBytes,
    string? StaticRootIdentity,
    ulong TypeHandle = 0);

public sealed record HybridCpuManagedStringLiteralRegistrationV1(
    string Identity,
    ulong LiteralHandle,
    ulong StringTypeId,
    string Utf16Value);

public enum HybridCpuManagedTypeKindV1 : byte
{
    Class = 0,
    Interface = 1,
    SzArray = 2,
    String = 3,
    ValueType = 4
}

public enum HybridCpuManagedStorageKindV1 : byte
{
    Primitive = 0,
    ObjectReference = 1,
    BlittableValue = 2
}

public enum HybridCpuManagedTypeInitializationStateV1 : byte
{
    Uninitialized = 0,
    Running = 1,
    Initialized = 2,
    Failed = 3
}

public sealed record HybridCpuManagedFieldLayoutV1(
    string Identity,
    HybridCpuManagedStorageKindV1 StorageKind,
    int OffsetBytes,
    int SizeBytes,
    int AlignmentBytes);

public sealed record HybridCpuManagedStaticLayoutV1(
    int SizeBytes,
    int AlignmentBytes,
    IReadOnlyList<HybridCpuManagedFieldLayoutV1> Fields,
    IReadOnlyList<int> ObjectReferenceOffsets);

public sealed record HybridCpuManagedArrayShapeV1(
    HybridCpuManagedStorageKindV1 ElementStorageKind,
    ulong? ElementTypeId,
    int ElementSizeBytes,
    int ElementAlignmentBytes,
    int LengthOffsetBytes,
    int DataOffsetBytes,
    bool IsSzArray,
    bool RequiresReferenceStoreCheck);

public sealed record HybridCpuManagedStringShapeV1(
    int LengthOffsetBytes,
    int DataOffsetBytes,
    int CharacterSizeBytes,
    bool IsImmutable);

public sealed record HybridCpuManagedValueTypeShapeV1(
    int PayloadSizeBytes,
    int PayloadAlignmentBytes,
    IReadOnlyList<int> ObjectReferenceOffsets,
    int BoxedPayloadOffsetBytes);

public sealed record HybridCpuManagedTypeDescriptorV1(
    string SchemaId,
    int SchemaMajor,
    int SchemaMinor,
    ulong TypeId,
    string StableIdentity,
    HybridCpuManagedTypeKindV1 Kind,
    ulong? BaseTypeId,
    IReadOnlyList<ulong> InterfaceTypeIds,
    int InstanceSizeBytes,
    int InstanceAlignmentBytes,
    IReadOnlyList<HybridCpuManagedFieldLayoutV1> InstanceFields,
    HybridCpuManagedStaticLayoutV1 StaticLayout,
    IReadOnlyList<ulong> VirtualSlotMethodIds,
    IReadOnlyList<ulong> InterfaceSlotMethodIds,
    string DescriptorDigest,
    HybridCpuManagedArrayShapeV1? ArrayShape = null,
    HybridCpuManagedStringShapeV1? StringShape = null,
    HybridCpuManagedValueTypeShapeV1? ValueTypeShape = null);

public sealed record HybridCpuManagedFieldDataRegistrationV1(ulong DataHandle, byte[] Data);

public sealed record HybridCpuImageRuntimeBootstrapDescriptorV1(
    string SchemaId,
    int SchemaMajor,
    int SchemaMinor,
    string PlatformContractDigest,
    string ManagedAbiDigest,
    string DescriptorDigest,
    string RuntimeEntrySymbol,
    string ManagedEntrySymbol,
    IReadOnlyList<HybridCpuRuntimeHelperImportV1> RuntimeHelpers,
    IReadOnlyList<HybridCpuCodeManagerRegistrationV1> CodeManagerRecords,
    IReadOnlyList<HybridCpuStaticRootRegistrationV1> StaticRoots,
    IReadOnlyList<HybridCpuModuleInitializerRegistrationV1> ModuleInitializers,
    IReadOnlyList<HybridCpuManagedTypeRegistrationV1>? ManagedTypes = null,
    IReadOnlyList<HybridCpuManagedStringLiteralRegistrationV1>? StringLiterals = null,
    IReadOnlyList<HybridCpuManagedEhMethodRegistrationV1>? EhMethods = null,
    IReadOnlyList<HybridCpuManagedFieldDataRegistrationV1>? FieldData = null);

public static class HybridCpuPlatformContractV1
{
    public const string SchemaId = "hybridcpu.platform-contracts/v1";
    public const string ManagedCallSignatureSchemaId = "hybridcpu.managed-call-signature/v1";
    public const int SchemaMajor = 1;
    public const int SchemaMinor = 13;
    public const int AddressSizeBytes = 8;
    public const int MaximumVmMappings = 64;
    public const int MaximumTrapDepth = 8;
    public const int MaximumRuntimeHelpers = 256;
    public const int MaximumCodeManagerRecords = 4096;
    public const int MaximumStaticRoots = 4096;
    public const int MaximumModuleInitializers = 4096;
    public const int ManagedObjectHeaderSizeBytes = 16;
    public const int ManagedObjectAlignmentBytes = 8;
    public const int MaximumManagedTypes = 4096;
    public const int MaximumManagedFieldsPerType = 1024;
    public const int MaximumManagedInterfacesPerType = 256;
    public const int MaximumManagedStringLiterals = 4096;
    public const int MaximumManagedStringLiteralCodeUnits = 1_048_576;
    public const int MaximumManagedFieldData = 4096;
    public const int MaximumManagedMethods = 16_384;
    public const int MaximumManagedVirtualSlotsPerType = 1024;
    public const int MaximumManagedInterfaceSlotsPerType = 4096;
    public const int MaximumManagedFunctionPointers = 16_384;
    public const int MaximumExecutionContexts = 4;
    public const int MaximumManagedTlsBytesPerContext = 65_536;
    public static HybridCpuManagedDelegateLayoutV1 ManagedDelegateLayout { get; } =
        new(16, 24, 32, 40, 48, 56, 64);

    public static string ContractDigest { get; } = Hash(string.Join('|',
        SchemaId, $"{SchemaMajor}.{SchemaMinor}", "little-endian", AddressSizeBytes,
        MaximumVmMappings, MaximumTrapDepth, MaximumRuntimeHelpers, MaximumCodeManagerRecords,
        MaximumStaticRoots, MaximumModuleInitializers, ManagedObjectHeaderSizeBytes,
        ManagedObjectAlignmentBytes, MaximumManagedTypes, MaximumManagedFieldsPerType,
        MaximumManagedInterfacesPerType, MaximumManagedStringLiterals, MaximumManagedStringLiteralCodeUnits,
        MaximumManagedFieldData, MaximumManagedMethods, MaximumManagedVirtualSlotsPerType, MaximumManagedInterfaceSlotsPerType,
        MaximumManagedFunctionPointers, MaximumExecutionContexts, MaximumManagedTlsBytesPerContext,
        ManagedDelegateLayout,
        "boot=initial-context", "contexts=deterministic-cooperative-v1", "vt=execution-carrier", "vm=generic-ranges",
        $"trap={HybridCpuTrapAbiV1.SchemaId}:{HybridCpuTrapAbiV1.SchemaVersion}:{HybridCpuTrapAbiV1.ContractDigest}",
        "host-transition=generic", "managed-type-descriptor=pod-only",
        "managed-stack-map-registration=pod-only", "managed-frame-snapshot=pod-only",
        "managed-dispatch-rows=pod-only", "managed-delegate-function-pointer-rows=pod-only",
        $"kernel-threading={HybridCpuKernelThreadingContractV1.SchemaId}:{HybridCpuKernelThreadingContractV1.SchemaVersion}:{HybridCpuKernelThreadingContractV1.ContractDigest}",
        $"managed-sync={HybridCpuManagedSynchronizationContractV1.SchemaId}:{HybridCpuManagedSynchronizationContractV1.SchemaVersion}:{HybridCpuManagedSynchronizationContractV1.ContractDigest}",
        $"managed-interop={HybridCpuManagedInteropContractV1.SchemaId}:{HybridCpuManagedInteropContractV1.SchemaVersion}:{HybridCpuManagedInteropContractV1.ContractDigest}",
        $"managed-reflection={HybridCpuManagedReflectionContractV1.SchemaId}:{HybridCpuManagedReflectionContractV1.SchemaVersion}:{HybridCpuManagedReflectionContractV1.ContractDigest}",
        $"async-runtime={HybridCpuAsyncRuntimeContractV1.SchemaId}:{HybridCpuAsyncRuntimeContractV1.SchemaVersion}:{HybridCpuAsyncRuntimeContractV1.ContractDigest}",
        $"managed-eh={HybridCpuManagedEhSchemaV1.SchemaId}:{HybridCpuManagedEhSchemaV1.SchemaVersion}:" +
        $"clauses={HybridCpuManagedEhSchemaV1.MaximumClausesPerMethod}:operations={HybridCpuManagedEhSchemaV1.MaximumOperationsPerMethod}:" +
        $"frames={HybridCpuManagedEhSchemaV1.MaximumFramesPerDispatch}:finally={HybridCpuManagedEhSchemaV1.MaximumFinallyInvocations}:" +
        "frame-snapshot=method:ip:sp:return-pc:fp",
        $"managed-finally-continuation={HybridCpuManagedFinallyContinuationEncodingV1.Version}:" +
        $"header={HybridCpuManagedFinallyContinuationEncodingV1.HeaderSizeBytes}:" +
        $"continuation={HybridCpuManagedFinallyContinuationEncodingV1.ContinuationRowSizeBytes}:" +
        $"step={HybridCpuManagedFinallyContinuationEncodingV1.StepRowSizeBytes}:exceptional-token={HybridCpuManagedFinallyContinuationEncodingV1.ExceptionalToken}",
        "managed-call-signature-id=sha256-low64-little-endian-nonzero/v1",
        "managed-shapes=szarray:utf16-string:fixed-value", "type-initializer-owner=runtime",
        "no-compiler-ir", "no-ise-execution"));

    public static ulong ComputeManagedCallSignatureId(string canonicalSignature)
    {
        if (string.IsNullOrWhiteSpace(canonicalSignature))
            throw new ArgumentException("A canonical managed call signature is required.", nameof(canonicalSignature));
        ulong value = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{ManagedCallSignatureSchemaId}|{canonicalSignature}")));
        return value == 0 ? 1UL : value;
    }

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public static class HybridCpuImageRuntimeBootstrapContractV1
{
    public const string SchemaId = "hybridcpu.managed-bootstrap/v1";
    public const int SchemaMajor = 1;
    public const int SchemaMinor = 4;

    public static HybridCpuImageRuntimeBootstrapDescriptorV1 Create(
        string managedAbiDigest,
        string runtimeEntrySymbol,
        string managedEntrySymbol,
        IEnumerable<HybridCpuRuntimeHelperImportV1>? runtimeHelpers = null,
        IEnumerable<HybridCpuCodeManagerRegistrationV1>? codeManagerRecords = null,
        IEnumerable<HybridCpuStaticRootRegistrationV1>? staticRoots = null,
        IEnumerable<HybridCpuModuleInitializerRegistrationV1>? moduleInitializers = null,
        IEnumerable<HybridCpuManagedTypeRegistrationV1>? managedTypes = null,
        IEnumerable<HybridCpuManagedStringLiteralRegistrationV1>? stringLiterals = null,
        IEnumerable<HybridCpuManagedEhMethodRegistrationV1>? ehMethods = null,
        IEnumerable<HybridCpuManagedFieldDataRegistrationV1>? fieldData = null)
    {
        HybridCpuRuntimeHelperImportV1[] helpers = (runtimeHelpers ?? []).OrderBy(static row => row.Symbol, StringComparer.Ordinal).ToArray();
        HybridCpuCodeManagerRegistrationV1[] methods = (codeManagerRecords ?? []).OrderBy(static row => row.MethodIdentity, StringComparer.Ordinal).ToArray();
        HybridCpuStaticRootRegistrationV1[] roots = (staticRoots ?? []).OrderBy(static row => row.Identity, StringComparer.Ordinal).ToArray();
        HybridCpuModuleInitializerRegistrationV1[] initializers = (moduleInitializers ?? [])
            .OrderBy(static row => row.Order).ThenBy(static row => row.ModuleIdentity, StringComparer.Ordinal)
            .ThenBy(static row => row.InitializerSymbol, StringComparer.Ordinal).ToArray();
        HybridCpuManagedTypeRegistrationV1[] types = (managedTypes ?? [])
            .OrderBy(static row => row.TypeId).ToArray();
        HybridCpuManagedStringLiteralRegistrationV1[] literals = (stringLiterals ?? [])
            .OrderBy(static row => row.Identity, StringComparer.Ordinal).ToArray();
        HybridCpuManagedEhMethodRegistrationV1[] exceptionMethods = (ehMethods ?? [])
            .OrderBy(static row => row.MethodIdentity, StringComparer.Ordinal).ToArray();
        HybridCpuManagedFieldDataRegistrationV1[] data = (fieldData ?? [])
            .OrderBy(static row => row.DataHandle).ToArray();
        HybridCpuImageRuntimeBootstrapDescriptorV1 draft = new HybridCpuImageRuntimeBootstrapDescriptorV1(SchemaId, SchemaMajor, SchemaMinor,
            HybridCpuPlatformContractV1.ContractDigest, managedAbiDigest, string.Empty, runtimeEntrySymbol,
            managedEntrySymbol, helpers, methods, roots, initializers, types, literals, exceptionMethods, data);
        return draft with { DescriptorDigest = ComputeDigest(draft) };
    }

    public static string ComputeDigest(HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return HybridCpuPlatformContractV1.Hash(string.Join('|', descriptor.SchemaId,
            $"{descriptor.SchemaMajor}.{descriptor.SchemaMinor}", descriptor.PlatformContractDigest,
            descriptor.ManagedAbiDigest, descriptor.RuntimeEntrySymbol, descriptor.ManagedEntrySymbol,
            string.Join(';', descriptor.RuntimeHelpers.Select(static row => $"{row.Symbol}:{row.Signature}:{row.Required}")),
            string.Join(';', descriptor.CodeManagerRecords.Select(static row => $"{row.MethodIdentity}:{row.CodeStartOffsetBytes}:{row.CodeSizeBytes}:{row.GcInfoDigest}:{row.UnwindInfoDigest}")),
            string.Join(';', descriptor.StaticRoots.Select(static row => $"{row.Identity}:{row.Address}:{row.Size}")),
            string.Join(';', descriptor.ModuleInitializers.Select(static row => $"{row.Order}:{row.ModuleIdentity}:{row.InitializerSymbol}:{row.TypeId}")),
            string.Join(';', (descriptor.ManagedTypes ?? []).Select(static row =>
                $"{row.TypeId}:{row.TypeHandle}:{row.StableIdentity}:{row.DescriptorDigest}:{row.MetadataOffsetBytes}:{row.MetadataSizeBytes}:{row.StaticRootIdentity}")),
            string.Join(';', (descriptor.StringLiterals ?? []).Select(static row =>
                row.Utf16Value is null
                    ? $"{row.Identity}:{row.LiteralHandle}:{row.StringTypeId}:<null>"
                    : $"{row.Identity}:{row.LiteralHandle}:{row.StringTypeId}:{row.Utf16Value.Length}:{HybridCpuPlatformContractV1.Hash(row.Utf16Value)}")),
            string.Join(';', (descriptor.EhMethods ?? []).Select(static row =>
                $"{row.MethodIdentity}:{row.CodeStartOffsetBytes}:{row.CodeSizeBytes}:" +
                $"{HybridCpuPlatformContractV1.Hash(Convert.ToHexString(row.EhInfo))}:" +
                $"{HybridCpuPlatformContractV1.Hash(Convert.ToHexString(row.UnwindInfo))}")),
            string.Join(';', (descriptor.FieldData ?? []).Select(static row =>
                $"{row.DataHandle}:{row.Data.Length}:{HybridCpuPlatformContractV1.Hash(Convert.ToHexString(row.Data))}"))));
    }
}

public static class HybridCpuManagedTypeDescriptorContractV1
{
    public const string SchemaId = "hybridcpu.managed-type-descriptor/v1";
    public const int SchemaMajor = 1;
    public const int SchemaMinor = 1;

    public static string ComputeDigest(HybridCpuManagedTypeDescriptorV1 descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return HybridCpuPlatformContractV1.Hash(string.Join('|', descriptor.SchemaId,
            $"{descriptor.SchemaMajor}.{descriptor.SchemaMinor}", descriptor.TypeId,
            descriptor.StableIdentity, descriptor.Kind, descriptor.BaseTypeId,
            string.Join(',', descriptor.InterfaceTypeIds), descriptor.InstanceSizeBytes,
            descriptor.InstanceAlignmentBytes,
            Fields(descriptor.InstanceFields), descriptor.StaticLayout.SizeBytes,
            descriptor.StaticLayout.AlignmentBytes, Fields(descriptor.StaticLayout.Fields),
            string.Join(',', descriptor.StaticLayout.ObjectReferenceOffsets),
            string.Join(',', descriptor.VirtualSlotMethodIds),
            string.Join(',', descriptor.InterfaceSlotMethodIds),
            ArrayShape(descriptor.ArrayShape), StringShape(descriptor.StringShape),
            ValueShape(descriptor.ValueTypeShape)));
    }

    private static string Fields(IEnumerable<HybridCpuManagedFieldLayoutV1> fields) =>
        string.Join(';', fields.Select(static field =>
            $"{field.Identity}:{field.StorageKind}:{field.OffsetBytes}:{field.SizeBytes}:{field.AlignmentBytes}"));

    private static string ArrayShape(HybridCpuManagedArrayShapeV1? shape) => shape is null ? "-" :
        $"{shape.ElementStorageKind}:{shape.ElementTypeId}:{shape.ElementSizeBytes}:{shape.ElementAlignmentBytes}:{shape.LengthOffsetBytes}:{shape.DataOffsetBytes}:{shape.IsSzArray}:{shape.RequiresReferenceStoreCheck}";

    private static string StringShape(HybridCpuManagedStringShapeV1? shape) => shape is null ? "-" :
        $"{shape.LengthOffsetBytes}:{shape.DataOffsetBytes}:{shape.CharacterSizeBytes}:{shape.IsImmutable}";

    private static string ValueShape(HybridCpuManagedValueTypeShapeV1? shape) => shape is null ? "-" :
        $"{shape.PayloadSizeBytes}:{shape.PayloadAlignmentBytes}:{string.Join(',', shape.ObjectReferenceOffsets)}:{shape.BoxedPayloadOffsetBytes}";
}
