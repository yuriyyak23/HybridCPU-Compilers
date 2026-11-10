namespace HybridCPU.Compiler.Core.IR;

public enum IrExternalExecutionRequirement : byte { Optional = 1, Required = 2 }
public enum IrExternalExecutionContour : byte { DmaStreaming = 1, SystemExternalAccelerator = 2 }
public enum IrExternalExecutionDomainRequirement : byte { Host = 1, Virtualized = 2, Secure = 3, SecureVirtualized = 4 }
public enum IrExternalSecureEvidenceRequirement : byte { NotRequired = 1, Required = 2 }
public enum IrExternalAssuranceRequirement : byte { Basic = 1, Elevated = 2, High = 3 }
public enum IrExternalVirtualDomainBindingRequirement : byte { NotRequired = 1, Required = 2 }
public enum IrExternalVirtualIoRequirement : byte { NotRequired = 1, BoundedRequired = 2 }
public enum IrExternalContainmentRequirement : byte { None = 1, CancellationOrContainmentRequired = 2 }

[Flags]
public enum IrExternalMemoryRegionRole : byte
{
    None = 0, Input = 1, Output = 2, ReadWrite = Input | Output
}

public enum IrExternalPublicationIntent : byte
{
    StagedOutputRequired = 1,
    StagedOutputPreferred = 2,
    /// <summary>May be lowered by a runtime to staged publication.</summary>
    DirectCoherentOutputRequested = 3,
    /// <summary>Must be rejected unless a future, separate proof boundary establishes it.</summary>
    DirectCoherentOutputRequired = 4
}
public enum IrExternalCoherenceRequirement : byte { NotRequired = 1, Preferred = 2, Required = 3 }
public enum IrExternalEffectRequirement : byte { Retryable = 1, Idempotent = 2, NonRetryable = 3 }
public enum IrExternalCancellationRequirement : byte { Unsupported = 1, BestEffort = 2, ExactAcknowledgement = 3 }
public enum IrExternalOrderingRequirement : byte { None = 1, Ordered = 2, FenceBeforeAndAfter = 3 }

/// <summary>
/// Stable semantic region hints. They never encode a memory fabric, address,
/// device, topology, or mapping and do not change CPU load/store semantics.
/// </summary>
[Flags]
public enum IrExternalMemoryPlacementIntent : byte
{
    None = 0,
    CapacityPreferred = 1 << 0,
    LowLatencyPreferred = 1 << 1,
    PersistentMemoryRequired = 1 << 2,
    ExternalDeviceAccessRequired = 1 << 3,
    CoherentSharedAccessPreferred = 1 << 4
}

[Flags]
public enum IrExternalDeviceAccessIntent : byte
{
    None = 0,
    DeviceReadable = 1 << 0,
    DeviceWritable = 1 << 1,
    CoherentOptional = 1 << 2,
    CoherentRequired = 1 << 3
}

/// <summary>
/// Immutable compiler semantic intent for an external operation. This is not
/// runtime authority and carries no transport, topology, address, capability,
/// provider, ownership, admission, visibility or publication fact.
/// </summary>
public sealed record IrExternalOperationIntent
{
    public static IrExternalOperationIntent Lane7StagedNonRetryable { get; } = new();
    public IrExternalExecutionRequirement ExecutionRequirement { get; init; } = IrExternalExecutionRequirement.Required;
    public IrExternalExecutionContour ExecutionContour { get; init; } = IrExternalExecutionContour.SystemExternalAccelerator;
    public IrExternalExecutionDomainRequirement ExecutionDomain { get; init; } = IrExternalExecutionDomainRequirement.Host;
    public IrExternalSecureEvidenceRequirement SecureEvidence { get; init; } = IrExternalSecureEvidenceRequirement.NotRequired;
    public IrExternalAssuranceRequirement MinimumAssurance { get; init; } = IrExternalAssuranceRequirement.Basic;
    public IrExternalVirtualDomainBindingRequirement VirtualDomainBinding { get; init; } = IrExternalVirtualDomainBindingRequirement.NotRequired;
    public IrExternalVirtualIoRequirement VirtualIo { get; init; } = IrExternalVirtualIoRequirement.NotRequired;
    public IrExternalContainmentRequirement Containment { get; init; } = IrExternalContainmentRequirement.CancellationOrContainmentRequired;
    public IrExternalMemoryRegionRole MemoryRoles { get; init; } = IrExternalMemoryRegionRole.ReadWrite;
    public IrExternalMemoryPlacementIntent MemoryPlacement { get; init; } = IrExternalMemoryPlacementIntent.None;
    public IrExternalDeviceAccessIntent DeviceAccess { get; init; } = IrExternalDeviceAccessIntent.DeviceReadable | IrExternalDeviceAccessIntent.DeviceWritable;
    public IrExternalPublicationIntent Publication { get; init; } = IrExternalPublicationIntent.StagedOutputRequired;
    public IrExternalCoherenceRequirement Coherence { get; init; } = IrExternalCoherenceRequirement.NotRequired;
    public IrExternalEffectRequirement Effect { get; init; } = IrExternalEffectRequirement.NonRetryable;
    public IrExternalCancellationRequirement Cancellation { get; init; } = IrExternalCancellationRequirement.ExactAcknowledgement;
    public IrExternalOrderingRequirement Ordering { get; init; } = IrExternalOrderingRequirement.FenceBeforeAndAfter;
}

/// <summary>
/// Explicit compiler fallback encoding derived only from semantic intent. It is
/// a policy declaration, never proof that a provider or CPU implementation exists.
/// </summary>
public readonly record struct IrExternalOperationFallbackPolicy(
    bool AllowsCpuFallbackBeforeSubmit,
    bool AllowsStagedPublication,
    bool RequiresCoherentAccess)
{
    public static IrExternalOperationFallbackPolicy From(IrExternalOperationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return new(
            intent.ExecutionRequirement == IrExternalExecutionRequirement.Optional,
            intent.Publication != IrExternalPublicationIntent.DirectCoherentOutputRequired,
            intent.Coherence == IrExternalCoherenceRequirement.Required);
    }
}
