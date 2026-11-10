using System.Buffers.Binary;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

namespace HybridCPU.ManagedRuntime;

public enum HybridCpuManagedHeapStatusV1 : byte
{
    Success = 0,
    NotInitialized = 1,
    InvalidConfiguration = 2,
    KernelFailure = 3,
    InvalidTypeDescriptor = 4,
    ObjectTooLarge = 5,
    SizeOverflow = 6,
    OutOfMemory = 7,
    InvalidObjectAccess = 8
}

public sealed record HybridCpuManagedHeapOptionsV1(
    ulong BaseAddress,
    ulong SizeBytes,
    int MaximumObjectSizeBytes,
    int OutOfMemoryExitCode,
    string OptionsDigest)
{
    public const ulong DefaultBaseAddress = 0x0000_0000_4000_0000UL;
    public const ulong DefaultSizeBytes = 16UL * 1024 * 1024;
    public const int DefaultMaximumObjectSizeBytes = 1024 * 1024;
    public const int DefaultOutOfMemoryExitCode = -3;

    public static HybridCpuManagedHeapOptionsV1 Production { get; } = Create(
        DefaultBaseAddress, DefaultSizeBytes, DefaultMaximumObjectSizeBytes, DefaultOutOfMemoryExitCode);

    public static HybridCpuManagedHeapOptionsV1 Create(
        ulong baseAddress,
        ulong sizeBytes,
        int maximumObjectSizeBytes,
        int outOfMemoryExitCode) =>
        new(baseAddress, sizeBytes, maximumObjectSizeBytes, outOfMemoryExitCode,
            HybridCpuPlatformContractV1.Hash(string.Join('|',
                "hybridcpu.managed-heap-options/v1", baseAddress, sizeBytes,
                maximumObjectSizeBytes, outOfMemoryExitCode)));
}

public sealed record HybridCpuManagedAllocationTraceV1(
    int Ordinal,
    ulong ObjectAddress,
    int ObjectSizeBytes,
    ulong TypeId,
    string TypeDescriptorDigest,
    string TraceDigest);

public sealed record HybridCpuManagedActiveAllocationV1(
    ulong ObjectAddress,
    int ObjectSizeBytes,
    ulong TypeId,
    string TypeDescriptorDigest);

public sealed record HybridCpuManagedFreeBlockV1(ulong Address, int SizeBytes);

public sealed record HybridCpuManagedHeapResultV1(
    HybridCpuManagedHeapStatusV1 Status,
    string Reason,
    ulong ObjectReference,
    int ObjectSizeBytes,
    string ResultDigest)
{
    public bool IsSuccess => Status == HybridCpuManagedHeapStatusV1.Success;
}

/// <summary>
/// Exact byte-addressable backing for the guest-visible managed heap. Production loaders
/// must bind this contract to HybridCPU memory; the private array implementation is only
/// the deterministic component/test backing.
/// </summary>
public interface IHybridCpuManagedHeapMemoryV1
{
    ulong BaseAddress { get; }
    ulong SizeBytes { get; }
    bool Clear(ulong address, int byteCount);
    bool Read(ulong address, Span<byte> destination);
    bool Write(ulong address, ReadOnlySpan<byte> source);
}

public sealed class HybridCpuManagedArrayHeapMemoryV1 : IHybridCpuManagedHeapMemoryV1
{
    private readonly byte[] _bytes;

    public HybridCpuManagedArrayHeapMemoryV1(ulong baseAddress, ulong sizeBytes)
    {
        if (baseAddress == 0 || sizeBytes == 0 || sizeBytes > int.MaxValue ||
            baseAddress > ulong.MaxValue - sizeBytes)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        BaseAddress = baseAddress;
        SizeBytes = sizeBytes;
        _bytes = new byte[checked((int)sizeBytes)];
    }

    public ulong BaseAddress { get; }
    public ulong SizeBytes { get; }
    public bool Clear(ulong address, int byteCount)
    {
        if (!Range(address, byteCount, out int offset)) return false;
        _bytes.AsSpan(offset, byteCount).Clear();
        return true;
    }
    public bool Read(ulong address, Span<byte> destination)
    {
        if (!Range(address, destination.Length, out int offset)) return false;
        _bytes.AsSpan(offset, destination.Length).CopyTo(destination);
        return true;
    }
    public bool Write(ulong address, ReadOnlySpan<byte> source)
    {
        if (!Range(address, source.Length, out int offset)) return false;
        source.CopyTo(_bytes.AsSpan(offset, source.Length));
        return true;
    }

    private bool Range(ulong address, int byteCount, out int offset)
    {
        offset = 0;
        if (byteCount < 0 || address < BaseAddress) return false;
        ulong relative = address - BaseAddress;
        if (relative > SizeBytes || (ulong)byteCount > SizeBytes - relative) return false;
        offset = checked((int)relative);
        return true;
    }
}

/// <summary>
/// Runtime-owned deterministic single-context allocator. RuntimeKernel only owns the generic
/// VM reservation; this allocator owns exact object validation, zeroing, bump/first-fit placement,
/// coalesced free blocks and sweep reclamation. Reachability and collection remain separate GC authority.
/// </summary>
public sealed class HybridCpuManagedHeapAllocatorV1
{
    public const string SchemaId = "hybridcpu.managed-heap-allocator/v1";

    private readonly IHybridCpuRuntimeKernelV1 _kernel;
    private readonly HybridCpuManagedTypeSystemV1 _types;
    private readonly HybridCpuManagedHeapOptionsV1 _options;
    private readonly IHybridCpuManagedHeapMemoryV1 _memory;
    private readonly List<HybridCpuManagedAllocationTraceV1> _trace = [];
    private readonly SortedDictionary<ulong, HybridCpuManagedActiveAllocationV1> _active = [];
    private readonly SortedDictionary<ulong, int> _free = [];
    private bool _initialized;
    private ulong _bump;

    public HybridCpuManagedHeapAllocatorV1(
        IHybridCpuRuntimeKernelV1 kernel,
        HybridCpuManagedTypeSystemV1 types,
        HybridCpuManagedHeapOptionsV1? options = null,
        IHybridCpuManagedHeapMemoryV1? memory = null)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _options = options ?? HybridCpuManagedHeapOptionsV1.Production;
        _memory = memory ?? new HybridCpuManagedArrayHeapMemoryV1(_options.BaseAddress, _options.SizeBytes);
    }

    public IReadOnlyList<HybridCpuManagedAllocationTraceV1> Trace => _trace.AsReadOnly();
    public ulong BaseAddress => _options.BaseAddress;
    public ulong LimitAddress => checked(_options.BaseAddress + _options.SizeBytes);
    public ulong NextAddress => checked(_options.BaseAddress + _bump);
    public bool IsInitialized => _initialized;
    public bool HasGcAuthority => false;
    public bool HasReclamation => true;
    public bool IsThreadSafe => false;

    public HybridCpuManagedHeapResultV1 Initialize()
    {
        if (_initialized)
            return Failure(HybridCpuManagedHeapStatusV1.InvalidConfiguration,
                "Managed heap V1 can be initialized exactly once.");
        if (!ValidOptions(_options))
            return Failure(HybridCpuManagedHeapStatusV1.InvalidConfiguration,
                "Managed heap options are malformed or their deterministic digest is stale.");
        if (_options.SizeBytes > int.MaxValue)
            return Failure(HybridCpuManagedHeapStatusV1.InvalidConfiguration,
                "Managed heap V1 requires an in-process arena representable by Int32 bytes.");
        if (_memory.BaseAddress != _options.BaseAddress || _memory.SizeBytes != _options.SizeBytes)
            return Failure(HybridCpuManagedHeapStatusV1.InvalidConfiguration,
                "Managed heap memory backing does not exactly match the configured guest address range.");

        HybridCpuVmRangeV1 reserved = new HybridCpuVmRangeV1(_options.BaseAddress, _options.SizeBytes, HybridCpuVmProtectionV1.None);
        HybridCpuKernelResultV1 reserve = _kernel.ReserveVm(reserved);
        if (!reserve.IsSuccess)
            return Failure(HybridCpuManagedHeapStatusV1.KernelFailure,
                $"RuntimeKernel heap reservation failed: {reserve.Status}: {reserve.Reason}");
        HybridCpuVmRangeV1 committed = reserved with { Protection = HybridCpuVmProtectionV1.Read | HybridCpuVmProtectionV1.Write };
        HybridCpuKernelResultV1 commit = _kernel.CommitVm(committed);
        if (!commit.IsSuccess)
        {
            _kernel.ReleaseVm(reserved.Address, reserved.Size);
            return Failure(HybridCpuManagedHeapStatusV1.KernelFailure,
                $"RuntimeKernel heap commit failed: {commit.Status}: {commit.Reason}");
        }

        if (!_memory.Clear(_options.BaseAddress, checked((int)_options.SizeBytes)))
        {
            _kernel.ReleaseVm(reserved.Address, reserved.Size);
            return Failure(HybridCpuManagedHeapStatusV1.KernelFailure,
                "Managed heap backing rejected deterministic zero initialization.");
        }
        _initialized = true;
        _bump = 0;
        return Success(0, 0, "Managed heap region was reserved, committed read/write and zero-initialized.");
    }

    public HybridCpuManagedHeapResultV1 Allocate(ulong typeHandle)
        => AllocateCore(typeHandle, null, terminateOnExhaustion: true);

    public HybridCpuManagedHeapResultV1 AllocateVariable(ulong typeHandle, int objectSizeBytes)
        => AllocateCore(typeHandle, objectSizeBytes, terminateOnExhaustion: true);

    public HybridCpuManagedHeapResultV1 TryAllocate(ulong typeHandle)
        => AllocateCore(typeHandle, null, terminateOnExhaustion: false);

    public HybridCpuManagedHeapResultV1 TryAllocateVariable(ulong typeHandle, int objectSizeBytes)
        => AllocateCore(typeHandle, objectSizeBytes, terminateOnExhaustion: false);

    private HybridCpuManagedHeapResultV1 AllocateCore(ulong typeHandle, int? requestedSizeBytes, bool terminateOnExhaustion)
    {
        if (!_initialized)
            return Failure(HybridCpuManagedHeapStatusV1.NotInitialized, "Managed heap is not initialized.");
        HybridCpuManagedTypeDescriptorV1? descriptor = _types.ResolveTypeHandle(typeHandle);
        if (!ValidAllocatableDescriptor(descriptor, requestedSizeBytes is not null))
            return Failure(HybridCpuManagedHeapStatusV1.InvalidTypeDescriptor,
                "Allocation requires an exact runtime-owned class TypeDescriptor handle and digest.");

        int size;
        try
        {
            int rawSize = requestedSizeBytes ?? descriptor!.InstanceSizeBytes;
            if (rawSize < descriptor!.InstanceSizeBytes)
                return Failure(HybridCpuManagedHeapStatusV1.InvalidTypeDescriptor,
                    "Variable allocation cannot be smaller than the descriptor's fixed prefix.");
            size = Align(rawSize, HybridCpuPlatformContractV1.ManagedObjectAlignmentBytes);
        }
        catch (OverflowException)
        {
            return Failure(HybridCpuManagedHeapStatusV1.SizeOverflow,
                "Checked aligned object size overflowed.");
        }
        if (size > _options.MaximumObjectSizeBytes)
            return Failure(HybridCpuManagedHeapStatusV1.ObjectTooLarge,
                "Object size exceeds the deterministic V1 maximum object size.");
        ulong address;
        if (TryTakeFreeBlock(size, out address))
        {
            // The free block is already removed/split by TryTakeFreeBlock.
        }
        else if (_bump <= _options.SizeBytes && (ulong)size <= _options.SizeBytes - _bump)
        {
            address = checked(_options.BaseAddress + _bump);
            _bump = checked(_bump + (ulong)size);
        }
        else
        {
            HybridCpuKernelResultV1? exit = terminateOnExhaustion
                ? _kernel.ProcessExit(_options.OutOfMemoryExitCode)
                : null;
            return Failure(HybridCpuManagedHeapStatusV1.OutOfMemory,
                exit is null
                    ? "Managed heap exhausted; collection retry is required."
                    : exit.IsSuccess
                    ? $"Managed heap exhausted; process-exit:{_options.OutOfMemoryExitCode}."
                    : $"Managed heap exhausted and RuntimeKernel rejected OOM termination: {exit.Status}: {exit.Reason}");
        }

        if (!_memory.Clear(address, size))
            return Failure(HybridCpuManagedHeapStatusV1.InvalidObjectAccess,
                "Managed heap backing rejected allocation zeroing.");
        Span<byte> header = stackalloc byte[HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(header, typeHandle);
        if (!_memory.Write(address, header))
            return Failure(HybridCpuManagedHeapStatusV1.InvalidObjectAccess,
                "Managed heap backing rejected object-header initialization.");
        int ordinal = _trace.Count;
        string traceDigest = HybridCpuPlatformContractV1.Hash(string.Join('|', SchemaId,
            _options.OptionsDigest, ordinal, address, size, descriptor.TypeId, descriptor.DescriptorDigest));
        _trace.Add(new(ordinal, address, size, descriptor.TypeId, descriptor.DescriptorDigest, traceDigest));
        _active.Add(address, new(address, size, descriptor.TypeId, descriptor.DescriptorDigest));
        return Success(address, size, "Managed object was allocated and initialized deterministically.");
    }

    public IReadOnlyList<HybridCpuManagedActiveAllocationV1> ActiveAllocations() => _active.Values.ToArray();

    public IReadOnlyList<HybridCpuManagedFreeBlockV1> FreeBlocks() =>
        _free.Select(static row => new HybridCpuManagedFreeBlockV1(row.Key, row.Value)).ToArray();

    public HybridCpuManagedHeapResultV1 ReclaimUnmarked(IReadOnlySet<ulong> marked)
    {
        ArgumentNullException.ThrowIfNull(marked);
        if (!_initialized)
            return Failure(HybridCpuManagedHeapStatusV1.NotInitialized, "Managed heap is not initialized.");
        if (marked.Any(reference => reference == 0 || !_active.ContainsKey(reference)))
            return Failure(HybridCpuManagedHeapStatusV1.InvalidObjectAccess,
                "Marked references must identify exact active runtime-owned objects.");
        HybridCpuManagedActiveAllocationV1[] reclaimed = _active.Values
            .Where(row => !marked.Contains(row.ObjectAddress)).ToArray();
        foreach (HybridCpuManagedActiveAllocationV1 allocation in reclaimed)
        {
            if (!_memory.Clear(allocation.ObjectAddress, allocation.ObjectSizeBytes))
                return Failure(HybridCpuManagedHeapStatusV1.InvalidObjectAccess,
                    "Managed heap backing rejected reclaimed-object clearing.");
            _active.Remove(allocation.ObjectAddress);
            AddFreeBlock(allocation.ObjectAddress, allocation.ObjectSizeBytes);
        }
        return Success(0, reclaimed.Sum(static row => row.ObjectSizeBytes),
            $"Reclaimed {reclaimed.Length} unreachable managed allocation(s) without relocation.");
    }

    public byte[]? ReadObjectBytes(ulong objectReference)
    {
        if (!_initialized) return null;
        if (!_active.TryGetValue(objectReference, out HybridCpuManagedActiveAllocationV1? allocation)) return null;
        byte[] result = new byte[allocation.ObjectSizeBytes];
        return _memory.Read(objectReference, result) ? result : null;
    }

    internal bool TryGetAllocation(ulong objectReference, out HybridCpuManagedActiveAllocationV1? allocation) =>
        _active.TryGetValue(objectReference, out allocation);

    /// <summary>Copies a bounded object range without materializing a snapshot of the whole object.</summary>
    public bool TryReadObjectBytes(ulong objectReference, int objectOffset, Span<byte> destination)
    {
        if (!_initialized || !_active.TryGetValue(objectReference, out HybridCpuManagedActiveAllocationV1? allocation) || objectOffset < 0 ||
            objectOffset > allocation.ObjectSizeBytes || destination.Length > allocation.ObjectSizeBytes - objectOffset)
            return false;
        return _memory.Read(checked(objectReference + (ulong)objectOffset), destination);
    }

    public byte[]? ReadObjectPayload(ulong objectReference, int objectOffset, int byteCount)
    {
        if (!_initialized || byteCount < 0) return null;
        if (!_active.TryGetValue(objectReference, out HybridCpuManagedActiveAllocationV1? allocation) || objectOffset < HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes ||
            objectOffset > allocation.ObjectSizeBytes || byteCount > allocation.ObjectSizeBytes - objectOffset) return null;
        byte[] result = new byte[byteCount];
        return _memory.Read(checked(objectReference + (ulong)objectOffset), result) ? result : null;
    }

    public HybridCpuManagedHeapResultV1 WriteObjectBytes(ulong objectReference, int objectOffset, ReadOnlySpan<byte> bytes)
    {
        if (!_initialized)
            return Failure(HybridCpuManagedHeapStatusV1.NotInitialized, "Managed heap is not initialized.");
        if (!_active.TryGetValue(objectReference, out HybridCpuManagedActiveAllocationV1? allocation) || objectOffset < HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes ||
            objectOffset > allocation.ObjectSizeBytes || bytes.Length > allocation.ObjectSizeBytes - objectOffset)
            return Failure(HybridCpuManagedHeapStatusV1.InvalidObjectAccess,
                "Managed object write must remain within an allocated object's payload.");
        if (!_memory.Write(checked(objectReference + (ulong)objectOffset), bytes))
            return Failure(HybridCpuManagedHeapStatusV1.InvalidObjectAccess,
                "Managed heap backing rejected a bounded payload write.");
        return Success(objectReference, allocation.ObjectSizeBytes,
            "Managed object payload was written within the runtime-owned allocation.");
    }

    internal HybridCpuManagedHeapResultV1 ClearObjectBytes(ulong objectReference, int objectOffset, int byteCount)
    {
        if (!_initialized) return Failure(HybridCpuManagedHeapStatusV1.NotInitialized, "Managed heap is not initialized.");
        if (byteCount < 0 || !_active.TryGetValue(objectReference, out HybridCpuManagedActiveAllocationV1? allocation) ||
            objectOffset < HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes || objectOffset > allocation.ObjectSizeBytes ||
            byteCount > allocation.ObjectSizeBytes - objectOffset)
            return Failure(HybridCpuManagedHeapStatusV1.InvalidObjectAccess, "Managed clear must remain within an allocated object's payload.");
        if (!_memory.Clear(checked(objectReference + (ulong)objectOffset), byteCount))
            return Failure(HybridCpuManagedHeapStatusV1.InvalidObjectAccess,
                "Managed heap backing rejected a bounded payload clear.");
        return Success(objectReference, allocation.ObjectSizeBytes, "Managed payload range was zeroed.");
    }

    private static bool ValidOptions(HybridCpuManagedHeapOptionsV1 options) =>
        options.BaseAddress != 0 && options.BaseAddress % 4096 == 0 &&
        options.SizeBytes != 0 && options.SizeBytes % 4096 == 0 &&
        options.BaseAddress <= ulong.MaxValue - options.SizeBytes &&
        options.MaximumObjectSizeBytes >= HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes &&
        options.MaximumObjectSizeBytes % HybridCpuPlatformContractV1.ManagedObjectAlignmentBytes == 0 &&
        options.OptionsDigest == HybridCpuManagedHeapOptionsV1.Create(options.BaseAddress, options.SizeBytes,
            options.MaximumObjectSizeBytes, options.OutOfMemoryExitCode).OptionsDigest;

    private static bool ValidAllocatableDescriptor(HybridCpuManagedTypeDescriptorV1? descriptor, bool variable) =>
        descriptor is not null && descriptor.SchemaId == HybridCpuManagedTypeDescriptorContractV1.SchemaId &&
        descriptor.SchemaMajor == HybridCpuManagedTypeDescriptorContractV1.SchemaMajor &&
        descriptor.SchemaMinor <= HybridCpuManagedTypeDescriptorContractV1.SchemaMinor &&
        (descriptor.Kind == HybridCpuManagedTypeKindV1.Class && !variable ||
         descriptor.Kind is HybridCpuManagedTypeKindV1.SzArray or HybridCpuManagedTypeKindV1.String && variable ||
         descriptor.Kind == HybridCpuManagedTypeKindV1.ValueType && variable) && descriptor.TypeId != 0 &&
        (descriptor.Kind == HybridCpuManagedTypeKindV1.ValueType
            ? descriptor.ValueTypeShape is { BoxedPayloadOffsetBytes: >= HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes }
            : descriptor.InstanceSizeBytes >= HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes) &&
        descriptor.InstanceAlignmentBytes >= HybridCpuPlatformContractV1.ManagedObjectAlignmentBytes &&
        descriptor.InstanceAlignmentBytes % HybridCpuPlatformContractV1.ManagedObjectAlignmentBytes == 0 &&
        descriptor.DescriptorDigest == HybridCpuManagedTypeDescriptorContractV1.ComputeDigest(descriptor);

    private static int Align(int value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);

    private bool TryTakeFreeBlock(int size, out ulong address)
    {
        KeyValuePair<ulong, int> selected = _free.FirstOrDefault(row => row.Value >= size);
        if (selected.Key != 0)
        {
            _free.Remove(selected.Key);
            address = selected.Key;
            if (selected.Value != size)
                _free.Add(checked(selected.Key + (ulong)size), selected.Value - size);
            return true;
        }
        address = 0;
        return false;
    }

    private void AddFreeBlock(ulong address, int size)
    {
        ulong start = address;
        int length = size;
        KeyValuePair<ulong, int> left = _free.LastOrDefault(row => row.Key < address);
        if (left.Key != 0 && checked(left.Key + (ulong)left.Value) == address)
        {
            start = left.Key;
            length = checked(length + left.Value);
            _free.Remove(left.Key);
        }
        if (_free.TryGetValue(checked(start + (ulong)length), out int rightLength))
        {
            _free.Remove(checked(start + (ulong)length));
            length = checked(length + rightLength);
        }
        _free.Add(start, length);
    }

    private HybridCpuManagedHeapResultV1 Success(ulong reference, int size, string reason) =>
        Result(HybridCpuManagedHeapStatusV1.Success, reason, reference, size);

    private HybridCpuManagedHeapResultV1 Failure(HybridCpuManagedHeapStatusV1 status, string reason) =>
        Result(status, reason, 0, 0);

    private HybridCpuManagedHeapResultV1 Result(
        HybridCpuManagedHeapStatusV1 status,
        string reason,
        ulong reference,
        int size) =>
        new(status, reason, reference, size, HybridCpuPlatformContractV1.Hash(string.Join('|',
            SchemaId, _options.OptionsDigest, status, reason, reference, size, _bump, _trace.Count)));
}
