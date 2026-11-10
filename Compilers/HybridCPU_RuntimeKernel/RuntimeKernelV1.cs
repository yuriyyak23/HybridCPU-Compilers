using HybridCPU.Platform.Contracts;

namespace HybridCPU.RuntimeKernel;

public sealed record HybridCpuKernelResultV1(
    HybridCpuKernelStatusV1 Status,
    string Reason)
{
    public bool IsSuccess => Status == HybridCpuKernelStatusV1.Success;
}

public enum HybridCpuKernelTrapDispositionV1 : byte
{
    Rejected = 0,
    Resume = 1,
    ManagedPolicyEligible = 2,
    ProcessFailure = 3
}

public sealed record HybridCpuKernelTrapResultV1(
    HybridCpuKernelStatusV1 Status,
    HybridCpuKernelTrapDispositionV1 Disposition,
    int TrapDepth,
    string RecordDigest,
    string Reason)
{
    public bool IsSuccess => Status == HybridCpuKernelStatusV1.Success;
    public bool GrantsManagedExceptionIdentity => false;
    public bool HasIseExecutionAuthority => false;
}

public sealed record HybridCpuKernelBootResultV1(
    HybridCpuKernelStatusV1 Status,
    string Reason,
    HybridCpuExecutionContextDescriptorV1? Context)
{
    public bool IsSuccess => Status == HybridCpuKernelStatusV1.Success && Context is not null;
}

public interface IHybridCpuRuntimeKernelV1
{
    HybridCpuKernelBootResultV1 Boot(HybridCpuBootInfoV1 bootInfo);
    HybridCpuExecutionContextDescriptorV1? CurrentContext();
    HybridCpuKernelResultV1 ReserveVm(HybridCpuVmRangeV1 range);
    HybridCpuKernelResultV1 CommitVm(HybridCpuVmRangeV1 range);
    HybridCpuKernelResultV1 ProtectVm(HybridCpuVmRangeV1 range);
    HybridCpuKernelResultV1 ReleaseVm(ulong address, ulong size);
    HybridCpuKernelResultV1 TrapEntry(HybridCpuArchitecturalTrapFrameV1 frame);
    HybridCpuKernelResultV1 TrapReturn(HybridCpuArchitecturalTrapFrameV1 frame);
    HybridCpuKernelTrapResultV1 ClassifyTrap(HybridCpuArchitecturalTrapRecordV1 record);
    HybridCpuKernelTrapResultV1 TrapEntry(HybridCpuArchitecturalTrapRecordV1 record);
    HybridCpuKernelTrapResultV1 TrapReturn(HybridCpuArchitecturalTrapRecordV1 record);
    HybridCpuHostTransitionResultV1 HostTransition(HybridCpuHostTransitionRequestV1 request);
    HybridCpuExternalServiceResultV1 ExternalServiceTransition(HybridCpuExternalServiceRequestV1 request);
    HybridCpuKernelResultV1 ProcessExit(int exitCode);
    HybridCpuKernelContextResultV1 CreateContext(HybridCpuExecutionContextCreateRequestV1 request);
    HybridCpuKernelContextResultV1 StartContext(ulong contextId);
    HybridCpuKernelContextResultV1 ExitContext(ulong contextId, int exitCode);
    HybridCpuKernelContextResultV1 JoinContext(ulong waitingContextId, ulong targetContextId);
    HybridCpuKernelContextResultV1 ParkContext(ulong contextId, string reason);
    HybridCpuKernelContextResultV1 UnparkContext(ulong contextId);
    HybridCpuKernelContextResultV1 BindContext(ulong contextId, int virtualThreadCarrier);
    HybridCpuKernelContextResultV1 UnbindContext(ulong contextId);
    HybridCpuKernelContextResultV1 ScheduleNext();
    IReadOnlyList<HybridCpuExecutionContextSnapshotV1> Contexts();
    HybridCpuKernelRendezvousResultV1 GcRendezvousEnter(ulong requestingContextId);
    HybridCpuKernelRendezvousResultV1 GcRendezvousLeave(ulong requestingContextId, ulong epoch);
    ulong AddressWakeEpoch(ulong address);
    HybridCpuAddressWaitResultV1 WaitOnAddress(HybridCpuAddressWaitRequestV1 request);
    HybridCpuAddressWaitResultV1 WakeAddress(ulong address, int count);
    HybridCpuAddressWaitResultV1 AdvanceAddressWaitClock(ulong logicalTick);
    HybridCpuAddressWaitResultV1 CancelAddressWait(ulong contextId);
    ulong MonotonicTicks();
    HybridCpuDeadlineResultV1 SleepUntil(HybridCpuDeadlineRequestV1 request);
    HybridCpuDeadlineResultV1 AdvanceMonotonicTime(ulong tick);
    HybridCpuDeadlineResultV1 CancelDeadline(ulong token);
}

public sealed partial class DeterministicRuntimeKernelV1 : IHybridCpuRuntimeKernelV1
{
    private readonly List<HybridCpuVmRangeV1> _mappings = [];
    private readonly Stack<HybridCpuArchitecturalTrapFrameV1> _traps = [];
    private readonly Stack<HybridCpuArchitecturalTrapRecordV1> _trapRecords = [];
    private readonly Stack<TrapOrderEntry> _trapOrder = [];
    private HybridCpuExecutionContextDescriptorV1? _context;
    private bool _exited;
    private ulong _virtualClockTicksPerSecond;

    public HybridCpuKernelBootResultV1 Boot(HybridCpuBootInfoV1 bootInfo)
    {
        ArgumentNullException.ThrowIfNull(bootInfo);
        if (_context is not null)
            return BootFailure(HybridCpuKernelStatusV1.Unsupported, "RuntimeKernel V1 admits exactly one execution context.");
        if (!string.Equals(bootInfo.PlatformContractDigest, HybridCpuPlatformContractV1.ContractDigest, StringComparison.Ordinal))
            return BootFailure(HybridCpuKernelStatusV1.VersionMismatch, "Platform contract digest mismatch.");
        if (!IsSha256(bootInfo.ImageContractDigest) ||
            !IsRangeValid(bootInfo.ImageBase, bootInfo.ImageSize) || !IsRangeValid(bootInfo.StackBase, bootInfo.StackSize) ||
            bootInfo.EntryAddress < bootInfo.ImageBase || bootInfo.EntryAddress >= checked(bootInfo.ImageBase + bootInfo.ImageSize) ||
            bootInfo.InitialVirtualThreadId is < 0 or > 3 || bootInfo.VirtualClockTicksPerSecond == 0 ||
            RangesOverlap(bootInfo.ImageBase, bootInfo.ImageSize, bootInfo.StackBase, bootInfo.StackSize))
            return BootFailure(HybridCpuKernelStatusV1.InvalidRequest, "Boot image, entry, stack or VT carrier is invalid.");

        _mappings.Add(new(bootInfo.ImageBase, bootInfo.ImageSize, HybridCpuVmProtectionV1.Read | HybridCpuVmProtectionV1.Execute));
        _mappings.Add(new(bootInfo.StackBase, bootInfo.StackSize, HybridCpuVmProtectionV1.Read | HybridCpuVmProtectionV1.Write));
        ulong contextCarrier = checked(bootInfo.StackBase + 0x100);
        _context = new(1, bootInfo.InitialVirtualThreadId, contextCarrier, bootInfo.EntryAddress,
            bootInfo.StackBase, bootInfo.StackSize, checked(bootInfo.StackBase + bootInfo.StackSize), SnapshotMappings());
        InitializeInitialContext(_context);
        _virtualClockTicksPerSecond = bootInfo.VirtualClockTicksPerSecond;
        return new(HybridCpuKernelStatusV1.Success, string.Empty, _context);
    }

    public HybridCpuExecutionContextDescriptorV1? CurrentContext() => _context;

    public HybridCpuKernelResultV1 ReserveVm(HybridCpuVmRangeV1 range)
    {
        if (!HasLiveContext()) return Failure(HybridCpuKernelStatusV1.NoCurrentContext, "No live execution context.");
        if (!IsRangeValid(range.Address, range.Size) || range.Protection != HybridCpuVmProtectionV1.None)
            return Failure(HybridCpuKernelStatusV1.InvalidRequest, "Reserve requires a valid no-access range.");
        if (_mappings.Count >= HybridCpuPlatformContractV1.MaximumVmMappings)
            return Failure(HybridCpuKernelStatusV1.BudgetExhausted, "VM mapping budget exhausted.");
        if (_mappings.Any(existing => RangesOverlap(existing.Address, existing.Size, range.Address, range.Size)))
            return Failure(HybridCpuKernelStatusV1.AddressConflict, "VM range overlaps an existing mapping.");
        _mappings.Add(range);
        RefreshContextMappings();
        return Success();
    }

    public HybridCpuKernelResultV1 CommitVm(HybridCpuVmRangeV1 range) => ReplaceExact(range, requireReserved: true);

    public HybridCpuKernelResultV1 ProtectVm(HybridCpuVmRangeV1 range) => ReplaceExact(range, requireReserved: false);

    public HybridCpuKernelResultV1 ReleaseVm(ulong address, ulong size)
    {
        if (!HasLiveContext()) return Failure(HybridCpuKernelStatusV1.NoCurrentContext, "No live execution context.");
        int index = _mappings.FindIndex(item => item.Address == address && item.Size == size);
        if (index < 0) return Failure(HybridCpuKernelStatusV1.InvalidRequest, "VM release requires an exact mapping.");
        HybridCpuVmRangeV1 mapping = _mappings[index];
        if (_context is not null && (mapping.Address == _context.StackBase || mapping.Address <= _context.EntryAddress &&
                _context.EntryAddress < checked(mapping.Address + mapping.Size)))
            return Failure(HybridCpuKernelStatusV1.Unsupported, "RuntimeKernel V1 cannot release its image or initial stack.");
        _mappings.RemoveAt(index);
        RefreshContextMappings();
        return Success();
    }

    public HybridCpuKernelResultV1 TrapEntry(HybridCpuArchitecturalTrapFrameV1 frame)
    {
        if (!HasLiveContext()) return Failure(HybridCpuKernelStatusV1.NoCurrentContext, "No live execution context.");
        if (_context is null || frame.VirtualThreadId != _context.VirtualThreadCarrier || frame.StackPointer == 0)
            return Failure(HybridCpuKernelStatusV1.InvalidRequest, "Trap frame does not belong to the current execution carrier.");
        if (_trapOrder.Count >= HybridCpuPlatformContractV1.MaximumTrapDepth)
            return Failure(HybridCpuKernelStatusV1.BudgetExhausted, "Trap nesting budget exhausted.");
        _traps.Push(frame);
        _trapOrder.Push(new(false, string.Empty, frame));
        return Success();
    }

    public HybridCpuKernelResultV1 TrapReturn(HybridCpuArchitecturalTrapFrameV1 frame)
    {
        if (!HasLiveContext()) return Failure(HybridCpuKernelStatusV1.NoCurrentContext, "No live execution context.");
        if (_traps.Count == 0 || _trapOrder.Count == 0 || _trapOrder.Peek().Modern ||
            _traps.Peek() != frame || _trapOrder.Peek().Legacy != frame)
            return Failure(HybridCpuKernelStatusV1.TrapStateMismatch, "Trap return must match the newest architectural trap frame exactly.");
        _traps.Pop();
        _trapOrder.Pop();
        return Success();
    }

    public HybridCpuKernelTrapResultV1 ClassifyTrap(HybridCpuArchitecturalTrapRecordV1 record)
    {
        if (!HasLiveContext()) return TrapFailure(HybridCpuKernelStatusV1.NoCurrentContext,
            HybridCpuKernelTrapDispositionV1.Rejected, record?.RecordDigest ?? string.Empty,
            "No live execution context.");
        if (!HybridCpuTrapAbiV1.TryValidate(record, out string reason))
            return TrapFailure(HybridCpuKernelStatusV1.InvalidRequest,
                HybridCpuKernelTrapDispositionV1.Rejected, record?.RecordDigest ?? string.Empty, reason);
        if (_context is null || record.ContextId != _context.ContextId ||
            record.Frame.VirtualThreadId != _context.VirtualThreadCarrier)
            return TrapFailure(HybridCpuKernelStatusV1.InvalidRequest,
                HybridCpuKernelTrapDispositionV1.Rejected, record.RecordDigest,
                "TrapAbi record does not belong to the current execution context.");
        if (record.FaultingInstructionRetired || !record.NoYoungerArchitecturalPublication)
            return TrapFailure(HybridCpuKernelStatusV1.InvalidRequest,
                HybridCpuKernelTrapDispositionV1.Rejected, record.RecordDigest,
                "A precise synchronous trap must not retire or publish younger architectural state.");

        if (record.TrapClass == HybridCpuArchitecturalTrapClassV1.SynchronousMemoryFault &&
            record.HasFaultAddress && record.FaultAddressSemantic == HybridCpuFaultAddressSemanticV1.VirtualAddress &&
            record.AccessKind is HybridCpuMemoryAccessKindV1.Read or HybridCpuMemoryAccessKindV1.Write &&
            record.PrivilegeMode == HybridCpuPrivilegeModeV1.User &&
            record.ExecutionMode == HybridCpuExecutionModeV1.ManagedUser &&
            record.ResumePolicy == HybridCpuTrapResumePolicyV1.ManagedDispatch)
            return new(HybridCpuKernelStatusV1.Success, HybridCpuKernelTrapDispositionV1.ManagedPolicyEligible,
                _trapOrder.Count, record.RecordDigest, string.Empty);

        if (record.ResumePolicy == HybridCpuTrapResumePolicyV1.RetryFaultingInstruction &&
            record.TrapClass == HybridCpuArchitecturalTrapClassV1.SynchronousMemoryFault)
            return new(HybridCpuKernelStatusV1.Success, HybridCpuKernelTrapDispositionV1.Resume,
                _trapOrder.Count, record.RecordDigest, string.Empty);

        return new(HybridCpuKernelStatusV1.ProcessExited, HybridCpuKernelTrapDispositionV1.ProcessFailure,
            _trapOrder.Count, record.RecordDigest,
            "Trap class or policy is not eligible for managed mapping and requires process failure.");
    }

    public HybridCpuKernelTrapResultV1 TrapEntry(HybridCpuArchitecturalTrapRecordV1 record)
    {
        HybridCpuKernelTrapResultV1 classified = ClassifyTrap(record);
        if (classified.Disposition == HybridCpuKernelTrapDispositionV1.ProcessFailure)
        {
            _exited = true;
            return classified;
        }
        if (!classified.IsSuccess) return classified;
        if (_trapOrder.Count >= HybridCpuPlatformContractV1.MaximumTrapDepth)
            return TrapFailure(HybridCpuKernelStatusV1.BudgetExhausted,
                HybridCpuKernelTrapDispositionV1.Rejected, record.RecordDigest,
                "Trap nesting budget exhausted.");
        _trapRecords.Push(record);
        _trapOrder.Push(new(true, record.RecordDigest, default));
        return classified with { TrapDepth = _trapOrder.Count };
    }

    public HybridCpuKernelTrapResultV1 TrapReturn(HybridCpuArchitecturalTrapRecordV1 record)
    {
        if (!HasLiveContext()) return TrapFailure(HybridCpuKernelStatusV1.NoCurrentContext,
            HybridCpuKernelTrapDispositionV1.Rejected, record?.RecordDigest ?? string.Empty,
            "No live execution context.");
        if (_trapRecords.Count == 0 || _trapOrder.Count == 0 || !_trapOrder.Peek().Modern ||
            _trapOrder.Peek().Digest != record.RecordDigest ||
            _trapRecords.Peek().RecordDigest != record.RecordDigest ||
            !HybridCpuTrapAbiV1.TryValidate(record, out _))
            return TrapFailure(HybridCpuKernelStatusV1.TrapStateMismatch,
                HybridCpuKernelTrapDispositionV1.Rejected, record?.RecordDigest ?? string.Empty,
                "Trap return must match the newest digest-bound TrapAbi record exactly.");
        HybridCpuKernelTrapDispositionV1 disposition = ClassifyTrap(record).Disposition;
        _trapRecords.Pop();
        _trapOrder.Pop();
        return new(HybridCpuKernelStatusV1.Success, disposition, _trapOrder.Count,
            record.RecordDigest, string.Empty);
    }

    public HybridCpuHostTransitionResultV1 HostTransition(HybridCpuHostTransitionRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!HasLiveContext()) return new(HybridCpuKernelStatusV1.NoCurrentContext, 0, "No live execution context.");
        if (request.Service != HybridCpuHostServiceV1.ProcessExit || request.Arguments.Count != 1)
            return new(HybridCpuKernelStatusV1.Unsupported, 0, "Host service is deferred beyond RuntimeKernel V1.");
        HybridCpuKernelResultV1 exit = ProcessExit(unchecked((int)request.Arguments[0]));
        return new(exit.Status, 0, exit.Reason);
    }

    public HybridCpuKernelResultV1 ProcessExit(int exitCode)
    {
        if (!HasLiveContext()) return Failure(HybridCpuKernelStatusV1.NoCurrentContext, "No live execution context.");
        if (_trapOrder.Count != 0)
            return Failure(HybridCpuKernelStatusV1.TrapStateMismatch, "Cannot exit with an attached trap frame.");
        TerminateProcessContexts(exitCode);
        _traps.Clear();
        _trapRecords.Clear();
        _trapOrder.Clear();
        _mappings.Clear();
        _context = null;
        _exited = true;
        return new(HybridCpuKernelStatusV1.Success, $"process-exit:{exitCode}");
    }

    private HybridCpuKernelResultV1 ReplaceExact(HybridCpuVmRangeV1 range, bool requireReserved)
    {
        if (!HasLiveContext()) return Failure(HybridCpuKernelStatusV1.NoCurrentContext, "No live execution context.");
        if (!IsRangeValid(range.Address, range.Size) || !IsProtectionValid(range.Protection) ||
            range.Protection == HybridCpuVmProtectionV1.None)
            return Failure(HybridCpuKernelStatusV1.InvalidRequest, "Commit/protect requires a valid accessible range.");
        int index = _mappings.FindIndex(item => item.Address == range.Address && item.Size == range.Size);
        if (index < 0 || requireReserved && _mappings[index].Protection != HybridCpuVmProtectionV1.None)
            return Failure(HybridCpuKernelStatusV1.InvalidRequest, "VM operation requires an exact compatible mapping.");
        _mappings[index] = range;
        RefreshContextMappings();
        return Success();
    }

    private bool HasLiveContext() => _context is not null && !_exited;

    private void RefreshContextMappings()
    {
        HybridCpuVmRangeV1[] mappings = SnapshotMappings();
        if (_context is not null) _context = _context with { VmMappings = mappings };
        RefreshRegisteredContextMappings(mappings);
    }

    private HybridCpuVmRangeV1[] SnapshotMappings() => _mappings
        .OrderBy(static item => item.Address)
        .ThenBy(static item => item.Size)
        .ToArray();

    private static bool IsRangeValid(ulong address, ulong size) =>
        size != 0 && address % 4096 == 0 && size % 4096 == 0 && address <= ulong.MaxValue - size;

    private static bool IsProtectionValid(HybridCpuVmProtectionV1 protection) =>
        (protection & ~(HybridCpuVmProtectionV1.Read | HybridCpuVmProtectionV1.Write | HybridCpuVmProtectionV1.Execute)) == 0;

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(static character => char.IsAsciiHexDigit(character));

    private static bool RangesOverlap(ulong leftAddress, ulong leftSize, ulong rightAddress, ulong rightSize) =>
        leftAddress < checked(rightAddress + rightSize) && rightAddress < checked(leftAddress + leftSize);

    private static HybridCpuKernelBootResultV1 BootFailure(HybridCpuKernelStatusV1 status, string reason) => new(status, reason, null);
    private static HybridCpuKernelResultV1 Failure(HybridCpuKernelStatusV1 status, string reason) => new(status, reason);
    private static HybridCpuKernelResultV1 Success() => new(HybridCpuKernelStatusV1.Success, string.Empty);
    private HybridCpuKernelTrapResultV1 TrapFailure(HybridCpuKernelStatusV1 status,
        HybridCpuKernelTrapDispositionV1 disposition, string digest, string reason) =>
        new(status, disposition, _trapOrder.Count, digest, reason);

    private readonly record struct TrapOrderEntry(bool Modern, string Digest,
        HybridCpuArchitecturalTrapFrameV1 Legacy);
}
