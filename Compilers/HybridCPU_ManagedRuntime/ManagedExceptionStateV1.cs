using System.Buffers.Binary;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.ManagedRuntime;

/// <summary>Runtime-owned exception roots for one stopped/cooperatively executing managed context.
/// A handler retains its token until leave/unwind; nested handlers must release tokens in LIFO order.
/// This does not perform native control transfer or synthesize an exception for throw-null.</summary>
public sealed class HybridCpuManagedExceptionStateV1
{
    private readonly HybridCpuManagedTypeSystemV1 _types;
    private readonly HybridCpuManagedHeapAllocatorV1 _heap;
    private readonly ulong _exceptionBaseType;
    private readonly List<(ulong Token, ulong Reference)> _entries = [];
    private ulong _lastToken;

    public HybridCpuManagedExceptionStateV1(HybridCpuManagedTypeSystemV1 types,
        HybridCpuManagedHeapAllocatorV1 heap, ulong exceptionBaseType)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _heap = heap ?? throw new ArgumentNullException(nameof(heap));
        if (types.Resolve(exceptionBaseType)?.Kind != HybridCpuManagedTypeKindV1.Class)
            throw new ArgumentException("An exact exception base class is required.", nameof(exceptionBaseType));
        _exceptionBaseType = exceptionBaseType;
    }

    public ulong CurrentReference => _entries.Count == 0 ? 0 : _entries[^1].Reference;
    public ulong CurrentToken => _entries.Count == 0 ? 0 : _entries[^1].Token;
    public int Depth => _entries.Count;

    public bool TryPush(ulong reference, out ulong token)
    {
        token = 0;
        if (_entries.Count >= HybridCpuManagedEhSchemaV1.MaximumFramesPerDispatch ||
            _lastToken == ulong.MaxValue || !IsException(reference)) return false;
        token = ++_lastToken;
        _entries.Add((token, reference));
        return true;
    }

    public bool TryReplace(ulong token, ulong reference)
    {
        if (token == 0 || token != CurrentToken || !IsException(reference)) return false;
        // No safepoint occurs between validation and replacing the runtime-owned root.
        _entries[^1] = (token, reference);
        return true;
    }

    public bool TryPop(ulong token)
    {
        if (token == 0 || token != CurrentToken) return false;
        _entries.RemoveAt(_entries.Count - 1);
        return true;
    }

    internal bool BelongsTo(HybridCpuManagedTypeSystemV1 types, HybridCpuManagedHeapAllocatorV1 heap) =>
        ReferenceEquals(types, _types) && ReferenceEquals(heap, _heap);

    internal IReadOnlyList<ulong> SnapshotRoots() => _entries.Select(static entry => entry.Reference).ToArray();

    internal bool UsesTypes(HybridCpuManagedTypeSystemV1 types) => ReferenceEquals(types, _types);

    internal ulong TypeOf(ulong reference)
    {
        byte[]? bytes = _heap.ReadObjectBytes(reference);
        return bytes is null || bytes.Length < sizeof(ulong) ? 0 :
            _types.ResolveTypeHandle(BinaryPrimitives.ReadUInt64LittleEndian(bytes))?.TypeId ?? 0;
    }

    private bool IsException(ulong reference)
    {
        ulong type = TypeOf(reference);
        return type != 0 && _types.IsAssignable(type, _exceptionBaseType);
    }
}
