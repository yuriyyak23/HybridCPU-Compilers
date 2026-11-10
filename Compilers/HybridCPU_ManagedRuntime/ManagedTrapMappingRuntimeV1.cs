using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

namespace HybridCPU.ManagedRuntime;

public enum HybridCpuManagedTrapMappingStatusV1 : byte
{
    Mapped = 0,
    Disabled = 1,
    NotMappable = 2,
    InvalidTrap = 3,
    MissingManagedType = 4,
    AllocationFailed = 5
}

public sealed record HybridCpuManagedTrapMappingOptionsV1(
    bool Enabled,
    bool MapZeroAddressReadWriteToNullReference,
    bool ImplicitNullChecksEnabled,
    string OptionsDigest)
{
    public static HybridCpuManagedTrapMappingOptionsV1 Default { get; } = Create(false, false, false);
    public static HybridCpuManagedTrapMappingOptionsV1 Qualification { get; } = Create(true, true, false);

    public static HybridCpuManagedTrapMappingOptionsV1 Create(bool enabled,
        bool mapZeroAddressReadWriteToNullReference, bool implicitNullChecksEnabled)
    {
        if (implicitNullChecksEnabled)
            throw new ArgumentException("Phase 10 does not qualify compiler implicit null checks.",
                nameof(implicitNullChecksEnabled));
        string digest = HybridCpuPlatformContractV1.Hash(string.Join('|',
            "hybridcpu.managed-trap-mapping-options/v1", enabled,
            mapZeroAddressReadWriteToNullReference, implicitNullChecksEnabled));
        return new(enabled, mapZeroAddressReadWriteToNullReference, implicitNullChecksEnabled, digest);
    }
}

public sealed record HybridCpuManagedTrapMappingResultV1(
    HybridCpuManagedTrapMappingStatusV1 Status,
    ulong ExceptionReference,
    ulong ExceptionTypeId,
    string ExceptionTypeIdentity,
    string TrapRecordDigest,
    string Reason,
    string ResultDigest)
{
    public bool IsMapped => Status == HybridCpuManagedTrapMappingStatusV1.Mapped;
    public bool HasIseExecutionAuthority => false;
    public bool HasArchitecturalFaultAuthority => false;
}

public sealed record HybridCpuManagedTrapDispatchResultV1(
    HybridCpuManagedTrapMappingResultV1 Mapping,
    HybridCpuManagedExceptionDispatchResultV1? Dispatch)
{
    public bool IsHandled => Mapping.IsMapped && Dispatch?.Status == HybridCpuManagedExceptionStatusV1.Handled;
}

public sealed class HybridCpuManagedTrapMapperV1
{
    public const string SchemaId = "hybridcpu.managed-trap-mapping/v1";
    public const string NullReferenceExceptionIdentity = "System.NullReferenceException";

    private readonly HybridCpuManagedTypeSystemV1 _types;
    private readonly HybridCpuManagedTrapMappingOptionsV1 _options;

    public HybridCpuManagedTrapMapperV1(HybridCpuManagedTypeSystemV1 types,
        HybridCpuManagedTrapMappingOptionsV1? options = null)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _options = options ?? HybridCpuManagedTrapMappingOptionsV1.Default;
        HybridCpuManagedTrapMappingOptionsV1 expected = HybridCpuManagedTrapMappingOptionsV1.Create(
            _options.Enabled, _options.MapZeroAddressReadWriteToNullReference, _options.ImplicitNullChecksEnabled);
        if (_options.OptionsDigest != expected.OptionsDigest)
            throw new ArgumentException("Managed trap mapping options digest is invalid.", nameof(options));
    }

    public HybridCpuManagedTrapMappingResultV1 Map(HybridCpuArchitecturalTrapRecordV1 record,
        HybridCpuKernelTrapResultV1 kernelResult,
        Func<HybridCpuManagedTypeDescriptorV1, ulong> allocateException)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(kernelResult);
        ArgumentNullException.ThrowIfNull(allocateException);
        if (!_options.Enabled)
            return Result(HybridCpuManagedTrapMappingStatusV1.Disabled, record, 0, 0, string.Empty,
                "Managed trap mapping is default-off.");
        if (!HybridCpuTrapAbiV1.TryValidate(record, out string validationReason) ||
            !kernelResult.IsSuccess ||
            kernelResult.Disposition != HybridCpuKernelTrapDispositionV1.ManagedPolicyEligible ||
            kernelResult.RecordDigest != record.RecordDigest)
            return Result(HybridCpuManagedTrapMappingStatusV1.InvalidTrap, record, 0, 0, string.Empty,
                string.IsNullOrEmpty(validationReason)
                    ? "RuntimeKernel did not authorize this exact TrapAbi record for managed policy evaluation."
                    : validationReason);
        if (!_options.MapZeroAddressReadWriteToNullReference ||
            record.TrapClass != HybridCpuArchitecturalTrapClassV1.SynchronousMemoryFault ||
            !record.HasFaultAddress || record.Frame.FaultAddress != 0 ||
            record.FaultAddressSemantic != HybridCpuFaultAddressSemanticV1.VirtualAddress ||
            record.AccessKind is not (HybridCpuMemoryAccessKindV1.Read or HybridCpuMemoryAccessKindV1.Write) ||
            record.PrivilegeMode != HybridCpuPrivilegeModeV1.User ||
            record.ExecutionMode != HybridCpuExecutionModeV1.ManagedUser ||
            record.FaultingInstructionRetired || !record.NoYoungerArchitecturalPublication ||
            record.ResumePolicy != HybridCpuTrapResumePolicyV1.ManagedDispatch)
            return Result(HybridCpuManagedTrapMappingStatusV1.NotMappable, record, 0, 0, string.Empty,
                "Only an exact precise zero-address managed-user read/write fault is approved for V1 mapping.");
        HybridCpuManagedTypeDescriptorV1? type = _types.Descriptors.SingleOrDefault(static row =>
            row.StableIdentity == NullReferenceExceptionIdentity);
        if (type is null)
            return Result(HybridCpuManagedTrapMappingStatusV1.MissingManagedType, record, 0, 0,
                NullReferenceExceptionIdentity, "The exact managed NullReferenceException type is absent.");
        ulong exceptionReference = allocateException(type);
        if (exceptionReference == 0)
            return Result(HybridCpuManagedTrapMappingStatusV1.AllocationFailed, record, 0, type.TypeId,
                type.StableIdentity, "The managed runtime failed to allocate the mapped exception object.");
        return Result(HybridCpuManagedTrapMappingStatusV1.Mapped, record, exceptionReference, type.TypeId,
            type.StableIdentity, string.Empty);
    }

    public HybridCpuManagedTrapDispatchResultV1 MapAndDispatch(HybridCpuArchitecturalTrapRecordV1 record,
        HybridCpuKernelTrapResultV1 kernelResult,
        Func<HybridCpuManagedTypeDescriptorV1, ulong> allocateException,
        HybridCpuManagedExceptionRuntimeV1 exceptionRuntime,
        IReadOnlyList<HybridCpuManagedEhFrameSnapshotV1> frames)
    {
        ArgumentNullException.ThrowIfNull(exceptionRuntime);
        ArgumentNullException.ThrowIfNull(frames);
        HybridCpuManagedTrapMappingResultV1 mapping = Map(record, kernelResult, allocateException);
        if (!mapping.IsMapped) return new(mapping, null);
        return new(mapping, exceptionRuntime.Dispatch(mapping.ExceptionReference, mapping.ExceptionTypeId, frames));
    }

    private HybridCpuManagedTrapMappingResultV1 Result(HybridCpuManagedTrapMappingStatusV1 status,
        HybridCpuArchitecturalTrapRecordV1 record, ulong reference, ulong typeId, string identity, string reason)
    {
        string digest = HybridCpuPlatformContractV1.Hash(string.Join('|', SchemaId, _options.OptionsDigest,
            status, record.RecordDigest, reference, typeId, identity, reason));
        return new(status, reference, typeId, identity, record.RecordDigest, reason, digest);
    }
}
