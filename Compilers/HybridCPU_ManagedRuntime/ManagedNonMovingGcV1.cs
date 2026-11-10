using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.ManagedRuntime;

public enum HybridCpuManagedNonMovingGcStatusV1 : byte
{
    Collected = 0,
    Disabled = 1,
    Unsupported = 2,
    InvalidMetadata = 3,
    InvalidState = 4,
    BudgetExhausted = 5,
    HeapFailure = 6
}

public enum HybridCpuManagedGcRootSourceV1 : byte
{
    Static = 0,
    StringLiteral = 1,
    Handle = 2,
    Pinned = 3,
    HelperTemporary = 4,
    ThreadStatic = 5
}

public sealed record HybridCpuManagedNonMovingGcBudgetsV1(
    int MaximumFrames,
    int MaximumSafepoints,
    int MaximumRoots,
    int MaximumObjects,
    int MaximumObjectReferences)
{
    public static HybridCpuManagedNonMovingGcBudgetsV1 Production { get; } =
        new(1024, 8192, 16384, 65536, 262144);
}

public sealed record HybridCpuManagedNonMovingGcOptionsV1(
    bool EnableCollection,
    HybridCpuManagedNonMovingGcBudgetsV1 Budgets,
    string OptionsDigest)
{
    public static HybridCpuManagedNonMovingGcOptionsV1 Production { get; } =
        Create(false, HybridCpuManagedNonMovingGcBudgetsV1.Production);

    public static HybridCpuManagedNonMovingGcOptionsV1 Qualification { get; } =
        Create(true, HybridCpuManagedNonMovingGcBudgetsV1.Production);

    public static HybridCpuManagedNonMovingGcOptionsV1 Create(bool enabled, HybridCpuManagedNonMovingGcBudgetsV1 budgets) =>
        new(enabled, budgets, HybridCpuPlatformContractV1.Hash(string.Join('|',
            "hybridcpu.managed-nonmoving-gc-options/v1", enabled, budgets.MaximumFrames,
            budgets.MaximumSafepoints, budgets.MaximumRoots, budgets.MaximumObjects,
            budgets.MaximumObjectReferences)));
}

public sealed record HybridCpuManagedGcRootV1(
    string Identity,
    HybridCpuManagedGcRootSourceV1 Source,
    ulong ObjectReference);

public sealed class HybridCpuManagedGcRootRegistryV1
{
    private readonly SortedDictionary<string, HybridCpuManagedGcRootV1> _roots = new(StringComparer.Ordinal);

    public bool Register(string identity, HybridCpuManagedGcRootSourceV1 source, ulong objectReference)
    {
        if (string.IsNullOrWhiteSpace(identity) || objectReference == 0 ||
            source is HybridCpuManagedGcRootSourceV1.Static or HybridCpuManagedGcRootSourceV1.StringLiteral)
            return false;
        string key = $"{(byte)source}:{identity}";
        if (_roots.TryGetValue(key, out HybridCpuManagedGcRootV1? existing))
            return existing.ObjectReference == objectReference;
        _roots.Add(key, new(identity, source, objectReference));
        return true;
    }

    public bool Unregister(string identity, HybridCpuManagedGcRootSourceV1 source) =>
        _roots.Remove($"{(byte)source}:{identity}");

    public IReadOnlyList<HybridCpuManagedGcRootV1> Snapshot() => _roots.Values.ToArray();
}

public sealed record HybridCpuManagedNonMovingGcRequestV1(
    IReadOnlyList<HybridCpuManagedStackMapRegistrationV1> Registrations,
    IReadOnlyList<HybridCpuManagedGcFrameSnapshotV1> Frames,
    IReadOnlyList<HybridCpuManagedGcRootV1> Roots,
    HybridCpuManagedStringRuntimeV1? Strings = null,
    HybridCpuManagedArrayRuntimeV1? Arrays = null,
    HybridCpuManagedExceptionStateV1? Exceptions = null);

public sealed record HybridCpuManagedNonMovingGcResultV1(
    HybridCpuManagedNonMovingGcStatusV1 Status,
    string Reason,
    IReadOnlyList<ulong> Roots,
    IReadOnlyList<ulong> ReachableObjects,
    IReadOnlyList<ulong> ReclaimedObjects,
    IReadOnlyList<HybridCpuManagedFreeBlockV1> FreeBlocks,
    string ResultDigest)
{
    public bool IsSuccess => Status == HybridCpuManagedNonMovingGcStatusV1.Collected;
}

public enum HybridCpuManagedRetiredSafepointStatusV1 : byte
{
    NotSafepoint = 0,
    Collected = 1,
    Rejected = 2
}

public sealed record HybridCpuManagedRetiredSafepointResultV1(
    HybridCpuManagedRetiredSafepointStatusV1 Status,
    string Reason,
    HybridCpuManagedNonMovingGcResultV1? Collection)
{
    public bool IsSafepoint => Status != HybridCpuManagedRetiredSafepointStatusV1.NotSafepoint;
    public bool IsSuccess => Status == HybridCpuManagedRetiredSafepointStatusV1.Collected && Collection?.IsSuccess == true;
}

// Execution-owner boundary for a single native stack. ReturnLink is the exact
// startup x1 value, including the existing managed return bias.
public sealed class HybridCpuManagedGcStackBoundaryV1(ulong imageBase, ulong stackTop, ulong returnLink)
{
    public ulong ImageBase { get; } = imageBase;
    public ulong StackTop { get; } = stackTop;
    public ulong ReturnLink { get; } = returnLink;
    internal Dictionary<ulong, HybridCpuManagedGcFrameSnapshotV1> SuspendedFrames { get; } = [];
}

/// <summary>
/// Runtime-owned, precise, single-context, stop-the-world, non-moving mark-sweep collector.
/// HCMG/HCMM are descriptive compiler inputs; this runtime validates roots and owns reachability,
/// lifetime and reclamation. There is no ISE execution, publication or retire authority here.
/// </summary>
public sealed class HybridCpuManagedNonMovingGcV1
{
    public const string SchemaId = "hybridcpu.managed-nonmoving-gc/v1";
    private const uint GcInfoMagic = 0x474d4348;
    private const uint CodeManagerMagic = 0x4d4d4348;
    private readonly HybridCpuManagedTypeSystemV1 _types;
    private readonly HybridCpuManagedHeapAllocatorV1 _heap;
    private readonly string _managedAbiDigest;
    private readonly string _targetPlatformDigest;
    private readonly string _nativeAbiDigest;
    private readonly string _runtimePackRevision;
    private readonly ConditionalWeakTable<HybridCpuManagedStackMapRegistrationV1, ParsedRegistrationCache> _registrationCache = new();
    private readonly object _imageRootBindingGate = new();
    private bool _collectionStarted;
    private bool _collecting;
    private (HybridCpuStaticRootRegistrationV1[] Locations, Func<ulong, int, byte[]?> Read)? _imageRoots;

    // Called by the admitted image execution owner before its first safepoint.
    // Explicit caller roots remain unable to impersonate static runtime roots.
    public bool BindImageStaticRoots(IReadOnlyList<HybridCpuStaticRootRegistrationV1> locations,
        Func<ulong, int, byte[]?> readMemory)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(readMemory);
        if (locations.Count > HybridCpuPlatformContractV1.MaximumStaticRoots) return false;
        HybridCpuStaticRootRegistrationV1[] captured = locations.ToArray();
        ulong slots = 0;
        foreach (HybridCpuStaticRootRegistrationV1? location in captured)
        {
            if (location is null || string.IsNullOrWhiteSpace(location.Identity) || location.Address == 0 ||
                location.Address % 8 != 0 || location.Size == 0 || location.Size % 8 != 0 ||
                location.Address > ulong.MaxValue - location.Size ||
                location.Size / 8 > (ulong)HybridCpuManagedNonMovingGcBudgetsV1.Production.MaximumRoots - slots)
                return false;
            slots += location.Size / 8;
        }
        if (captured.Select(static row => row.Identity).Distinct(StringComparer.Ordinal).Count() != captured.Length)
            return false;
        lock (_imageRootBindingGate)
        {
            if (_imageRoots is not null || _collectionStarted) return false;
            _imageRoots = (captured, readMemory);
            return true;
        }
    }

    public HybridCpuManagedNonMovingGcV1(HybridCpuManagedTypeSystemV1 types,
        HybridCpuManagedHeapAllocatorV1 heap, string managedAbiDigest, string targetPlatformDigest,
        string nativeAbiDigest, string runtimePackRevision)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _heap = heap ?? throw new ArgumentNullException(nameof(heap));
        _managedAbiDigest = Required(managedAbiDigest, nameof(managedAbiDigest));
        _targetPlatformDigest = Required(targetPlatformDigest, nameof(targetPlatformDigest));
        _nativeAbiDigest = Required(nativeAbiDigest, nameof(nativeAbiDigest));
        _runtimePackRevision = Required(runtimePackRevision, nameof(runtimePackRevision));
        ContractDigest = HybridCpuPlatformContractV1.Hash(string.Join('|', SchemaId,
            HybridCpuPlatformContractV1.ContractDigest, _managedAbiDigest, _targetPlatformDigest,
            _nativeAbiDigest, _runtimePackRevision,
            HybridCpuManagedNonMovingGcOptionsV1.Production.OptionsDigest,
            HybridCpuManagedNonMovingGcOptionsV1.Qualification.OptionsDigest,
            "precise", "stw", "single-context", "non-moving", "mark-sweep",
            "non-generational", "non-concurrent", "no-write-barrier", "byref=unsupported",
            "root-authority=static:type-system;literal:string-runtime;empty-array:array-runtime;explicit:handle-pin-helper",
            "ise-execution-authority=false"));
    }

    public string ContractDigest { get; }
    public bool HasObjectLifetimeAuthority => true;
    public bool HasIseExecutionAuthority => false;
    public bool IsSingleContext => true;
    public bool IsStopTheWorld => true;
    public bool IsMoving => false;
    public bool IsGenerational => false;
    public bool IsConcurrent => false;
    public bool RequiresWriteBarrier => false;
    public bool SupportsInteriorReferences => false;

    public HybridCpuManagedRetiredSafepointResultV1 CollectRetiredSafepoint(
        IReadOnlyList<HybridCpuManagedStackMapRegistrationV1> registrations,
        int instructionPointer,
        IReadOnlyList<ulong> registers,
        ulong stackPointer,
        Func<ulong, int, byte[]?> readMemory,
        IReadOnlyList<HybridCpuManagedGcRootV1> roots,
        HybridCpuManagedStringRuntimeV1? strings = null,
        HybridCpuManagedGcStackBoundaryV1? stackBoundary = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(readMemory);
        ArgumentNullException.ThrowIfNull(roots);
        if (registers.Count != 64)
            return new(HybridCpuManagedRetiredSafepointStatusV1.Rejected,
                "Retired GC snapshot requires exactly 64 ABI register locations.", null);

        Registration? matched = null;
        string? method = null;
        RootLocation[]? locations = null;
        foreach (HybridCpuManagedStackMapRegistrationV1 item in registrations)
        {
            ParseResult parsed = ParseRegistration(item, HybridCpuManagedNonMovingGcBudgetsV1.Production);
            if (parsed.Registration is null)
                return new(HybridCpuManagedRetiredSafepointStatusV1.Rejected, parsed.Reason, null);
            Registration candidate = parsed.Registration;
            int relative;
            try { relative = checked(instructionPointer - candidate.CodeStart); }
            catch (OverflowException) { continue; }
            if (relative < 0 || relative >= candidate.CodeSize ||
                !candidate.Safepoints.TryGetValue(relative, out RootLocation[]? exact)) continue;
            if (matched is not null)
                return new(HybridCpuManagedRetiredSafepointStatusV1.Rejected,
                    "Retired PC resolves to multiple managed safepoints.", null);
            matched = candidate;
            method = item.MethodIdentity;
            locations = exact;
        }
        if (matched is null || method is null || locations is null)
            return new(HybridCpuManagedRetiredSafepointStatusV1.NotSafepoint, string.Empty, null);

        Dictionary<int, ulong> stackSlots = new Dictionary<int, ulong>();
        foreach (int offset in locations.Where(static row => row.StackOffset is not null)
                     .Select(static row => row.StackOffset!.Value).Distinct().Order())
        {
            ulong address;
            try { address = checked(stackPointer + (ulong)offset); }
            catch (OverflowException)
            {
                return new(HybridCpuManagedRetiredSafepointStatusV1.Rejected,
                    "Retired safepoint stack-root address overflowed.", null);
            }
            byte[]? bytes = readMemory(address, 8);
            if (bytes is not { Length: 8 })
                return new(HybridCpuManagedRetiredSafepointStatusV1.Rejected,
                    "Retired safepoint stack-root read was rejected by ISE memory.", null);
            stackSlots.Add(offset, BinaryPrimitives.ReadUInt64LittleEndian(bytes));
        }

        List<HybridCpuManagedGcFrameSnapshotV1> frames = new List<HybridCpuManagedGcFrameSnapshotV1>
        {
            new(method, instructionPointer, registers, stackSlots)
        };
        if (stackBoundary is not null && !TryAppendCallers(registrations, method, registers,
                stackPointer, readMemory, stackBoundary, frames, out string walkReason))
            return new(HybridCpuManagedRetiredSafepointStatusV1.Rejected, walkReason, null);
        HybridCpuManagedNonMovingGcResultV1 collection = Collect(new(
            registrations,
            frames,
            roots,
            strings), HybridCpuManagedNonMovingGcOptionsV1.Qualification);
        if (collection.IsSuccess)
        {
            if (stackBoundary is not null)
            {
                foreach (ulong expired in stackBoundary.SuspendedFrames.Keys.Where(sp => sp <= stackPointer).ToArray())
                    stackBoundary.SuspendedFrames.Remove(expired);
                stackBoundary.SuspendedFrames.Add(stackPointer, frames[0] with { Registers = registers.ToArray() });
            }
            return new(HybridCpuManagedRetiredSafepointStatusV1.Collected, string.Empty, collection);
        }
        string rootSnapshot = string.Join(';', locations.Take(32).Select(row => row.Register is int register
            ? $"x{register}=0x{registers[register]:x16}"
            : $"sp+{row.StackOffset}=0x{stackSlots.GetValueOrDefault(row.StackOffset ?? 0):x16}"));
        return new(HybridCpuManagedRetiredSafepointStatusV1.Rejected,
            $"{collection.Reason} retired-method='{method}', image-pc=0x{instructionPointer:x}, " +
            $"sp=0x{stackPointer:x16}, root-locations=[{rootSnapshot}].", collection);
    }

    private bool TryAppendCallers(IReadOnlyList<HybridCpuManagedStackMapRegistrationV1> registrations,
        string method, IReadOnlyList<ulong> registers, ulong stackPointer,
        Func<ulong, int, byte[]?> readMemory, HybridCpuManagedGcStackBoundaryV1 boundary,
        List<HybridCpuManagedGcFrameSnapshotV1> frames, out string reason)
    {
        reason = "Native GC caller walk rejected an incomplete or invalid frame chain.";
        if (boundary.ImageBase == 0 || boundary.ReturnLink == 0 || stackPointer != registers[2] ||
            stackPointer > boundary.StackTop || stackPointer % 16 != 0 || boundary.StackTop % 16 != 0)
            return false;
        ulong lowerBound = stackPointer;
        ulong[] bank = registers.ToArray();
        bool ReadWord(ulong basis, int displacement, out ulong value)
        {
            value = 0;
            long delta = displacement;
            if (delta < 0 && basis < (ulong)-delta || delta >= 0 && basis > ulong.MaxValue - (ulong)delta)
                return false;
            ulong address = delta < 0 ? basis - (ulong)-delta : basis + (ulong)delta;
            if (address < lowerBound || address % 8 != 0 || boundary.StackTop < 8 || address > boundary.StackTop - 8)
                return false;
            byte[]? bytes = readMemory(address, 8);
            if (bytes is not { Length: 8 }) return false;
            value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            return true;
        }
        while (frames.Count <= HybridCpuManagedNonMovingGcBudgetsV1.Production.MaximumFrames)
        {
            HybridCpuManagedStackMapRegistrationV1[] current = registrations.Where(row => row.MethodIdentity == method).ToArray();
            if (current.Length != 1 || current[0].UnwindInfo is not { } unwind || !ValidManagedUnwind(unwind))
                return false;
            ulong basis = bank[unwind[9] == 0 ? 2 : 8];
            int cfaOffset = BinaryPrimitives.ReadInt32LittleEndian(unwind.AsSpan(12));
            if (basis > ulong.MaxValue - (ulong)cfaOffset) return false;
            ulong cfa = basis + (ulong)cfaOffset;
            if (cfa < bank[2] || cfa > boundary.StackTop || cfa % 16 != 0) return false;
            int returnRegister = BinaryPrimitives.ReadInt32LittleEndian(unwind.AsSpan(16));
            ulong link;
            if (returnRegister >= 1) link = bank[returnRegister];
            else if (!ReadWord(cfa, BinaryPrimitives.ReadInt32LittleEndian(unwind.AsSpan(20)), out link)) return false;
            if (cfa == boundary.StackTop && link == boundary.ReturnLink) return true;
            if (cfa <= bank[2] || frames.Count == HybridCpuManagedNonMovingGcBudgetsV1.Production.MaximumFrames)
                return false;
            // Native call-control v1: the architectural link is call PC + 4.
            // Lookup the suspended call's exact map; do not round an arbitrary PC.
            if (link < boundary.ImageBase || link - boundary.ImageBase < 4 || (link - boundary.ImageBase) % 256 != 4)
                return false;
            ulong callerOffset = link - boundary.ImageBase - 4;
            if (callerOffset > int.MaxValue) return false;
            int pc = (int)callerOffset;
            ulong[] restored = bank.ToArray();
            int savedCount = BinaryPrimitives.ReadUInt16LittleEndian(unwind.AsSpan(10));
            for (int index = 0, offset = 28; index < savedCount; index++, offset += 8)
            {
                int register = BinaryPrimitives.ReadInt32LittleEndian(unwind.AsSpan(offset));
                if (!ReadWord(cfa, BinaryPrimitives.ReadInt32LittleEndian(unwind.AsSpan(offset + 4)), out restored[register]))
                    return false;
            }
            restored[2] = cfa;
            string? callerMethod = null;
            RootLocation[]? locations = null;
            foreach (HybridCpuManagedStackMapRegistrationV1 registration in registrations)
            {
                ParseResult parsed = ParseRegistration(registration, HybridCpuManagedNonMovingGcBudgetsV1.Production);
                if (parsed.Registration is not { } candidate) return false;
                long relative = (long)pc - candidate.CodeStart;
                if (relative < 0 || relative >= candidate.CodeSize ||
                    !candidate.Safepoints.TryGetValue((int)relative, out RootLocation[]? exact)) continue;
                if (callerMethod is not null) return false;
                callerMethod = registration.MethodIdentity;
                locations = exact;
            }
            if (callerMethod is null || locations is null) return false;
            // Caller-saved registers cannot be reconstructed from the callee bank.
            // Every managed call is a retired safepoint: use its exact suspended
            // snapshot, bound to the physical HCW2 chain, not guessed volatile roots.
            if (!boundary.SuspendedFrames.TryGetValue(cfa, out HybridCpuManagedGcFrameSnapshotV1? suspended) ||
                suspended.MethodIdentity != callerMethod || suspended.InstructionPointer != pc)
                return false;
            for (int index = 0, offset = 28; index < savedCount; index++, offset += 8)
            {
                int register = BinaryPrimitives.ReadInt32LittleEndian(unwind.AsSpan(offset));
                if (restored[register] != suspended.Registers[register]) return false;
            }
            Dictionary<int, ulong> slots = new Dictionary<int, ulong>();
            foreach (int offset in locations.Where(row => row.StackOffset is not null)
                         .Select(row => row.StackOffset!.Value).Distinct())
            {
                if (!ReadWord(cfa, offset, out ulong value)) return false;
                slots.Add(offset, value);
            }
            if (slots.Any(row => !suspended.StackSlots.TryGetValue(row.Key, out ulong saved) || saved != row.Value))
                return false;
            frames.Add(suspended);
            bank = suspended.Registers.ToArray();
            method = callerMethod;
        }
        return false;
    }

    public HybridCpuManagedNonMovingGcResultV1 Collect(HybridCpuManagedNonMovingGcRequestV1 request,
        HybridCpuManagedNonMovingGcOptionsV1? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_imageRootBindingGate)
        {
            if (_collecting) return Failure(HybridCpuManagedNonMovingGcStatusV1.InvalidState,
                "Original single-context collection is already pending.");
            _collectionStarted = true;
            _collecting = true;
        }
        try { return CollectCore(request, options); }
        finally { lock (_imageRootBindingGate) _collecting = false; }
    }

    private HybridCpuManagedNonMovingGcResultV1 CollectCore(HybridCpuManagedNonMovingGcRequestV1 request,
        HybridCpuManagedNonMovingGcOptionsV1? options)
    {
        options ??= HybridCpuManagedNonMovingGcOptionsV1.Production;
        if (!ValidOptions(options)) return Failure(HybridCpuManagedNonMovingGcStatusV1.InvalidState,
            "GC options do not bind valid deterministic budgets.");
        if (!options.EnableCollection) return Failure(HybridCpuManagedNonMovingGcStatusV1.Disabled,
            "Production non-moving collection is default-disabled.");
        if (request.Registrations is null || request.Frames is null || request.Roots is null)
            return Failure(HybridCpuManagedNonMovingGcStatusV1.InvalidState,
                "Registrations, frame snapshots and explicit roots are required.");
        if (request.Frames.Count > options.Budgets.MaximumFrames ||
            _heap.ActiveAllocations().Count > options.Budgets.MaximumObjects)
            return Failure(HybridCpuManagedNonMovingGcStatusV1.BudgetExhausted,
                "Frame or active-object budget was exceeded.");

        Dictionary<string, Registration> registrations = new Dictionary<string, Registration>(StringComparer.Ordinal);
        foreach (HybridCpuManagedStackMapRegistrationV1 item in request.Registrations)
        {
            ParseResult parsed = ParseRegistration(item, options.Budgets);
            if (parsed.Registration is null) return Failure(parsed.Status, parsed.Reason);
            if (!registrations.TryAdd(item.MethodIdentity, parsed.Registration))
                return Failure(HybridCpuManagedNonMovingGcStatusV1.InvalidMetadata,
                    "Method registration identities must be unique.");
        }

        List<ulong> roots = new List<ulong>();
        foreach (HybridCpuManagedGcFrameSnapshotV1 frame in request.Frames)
        {
            if (frame is null || frame.Registers is null || frame.StackSlots is null ||
                frame.Registers.Count != 64 || !registrations.TryGetValue(frame.MethodIdentity, out Registration? registration))
                return Failure(HybridCpuManagedNonMovingGcStatusV1.InvalidState,
                    "Every frame requires 64 registers and one matching exact method registration.");
            int relativeIp;
            try { relativeIp = checked(frame.InstructionPointer - registration.CodeStart); }
            catch (OverflowException) { return Failure(HybridCpuManagedNonMovingGcStatusV1.InvalidState, "Frame IP overflowed its code range."); }
            if (relativeIp < 0 || relativeIp >= registration.CodeSize ||
                !registration.Safepoints.TryGetValue(relativeIp, out RootLocation[]? locations))
                return Failure(HybridCpuManagedNonMovingGcStatusV1.InvalidState,
                    "A frame IP is not an exact registered final safepoint.");
            foreach (RootLocation location in locations)
            {
                if (location.Register is int register) roots.Add(frame.Registers[register]);
                else if (location.StackOffset is int stack && frame.StackSlots.TryGetValue(stack, out ulong value)) roots.Add(value);
                else return Failure(HybridCpuManagedNonMovingGcStatusV1.InvalidState,
                    "A final stack-map location is absent from the runtime frame snapshot.");
            }
        }

        (HybridCpuStaticRootRegistrationV1[] Locations, Func<ulong, int, byte[]?> Read)? imageRoots;
        lock (_imageRootBindingGate) imageRoots = _imageRoots;
        if (imageRoots is { } image)
        {
            foreach (HybridCpuStaticRootRegistrationV1 location in image.Locations)
            {
                for (ulong offset = 0; offset < location.Size; offset += 8)
                {
                    byte[]? value = image.Read(location.Address + offset, 8);
                    if (value is null || value.Length != 8)
                        return Failure(HybridCpuManagedNonMovingGcStatusV1.InvalidState,
                            "Original image static root backing is unreadable.");
                    roots.Add(BinaryPrimitives.ReadUInt64LittleEndian(value));
                }
            }
        }
        foreach (HybridCpuManagedTypeDescriptorV1 type in _types.Descriptors.OrderBy(static row => row.TypeId))
        {
            byte[]? storage = _types.StaticStorage(type.TypeId);
            if (storage is null) return Failure(HybridCpuManagedNonMovingGcStatusV1.InvalidState,
                "Type static storage is absent.");
            foreach (int offset in type.StaticLayout.ObjectReferenceOffsets.Order())
            {
                if (offset < 0 || offset > storage.Length - 8)
                    return Failure(HybridCpuManagedNonMovingGcStatusV1.InvalidMetadata,
                        "A static pointer-map offset is outside its exact storage blob.");
                roots.Add(BinaryPrimitives.ReadUInt64LittleEndian(storage.AsSpan(offset, 8)));
            }
        }
        if (request.Strings is not null)
            roots.AddRange(request.Strings.LiteralHandles.OrderBy(static row => row.Key).Select(static row => row.Value));
        if (request.Arrays is not null)
            roots.AddRange(request.Arrays.EmptyArrayRoots);
        if (request.Exceptions is not null)
        {
            if (!request.Exceptions.BelongsTo(_types, _heap))
                return Failure(HybridCpuManagedNonMovingGcStatusV1.InvalidState,
                    "Exception roots belong to a different runtime heap or type system.");
            roots.AddRange(request.Exceptions.SnapshotRoots());
        }
        if (request.Roots.Any(static root => root is null || string.IsNullOrWhiteSpace(root.Identity) ||
                root.Source is HybridCpuManagedGcRootSourceV1.Static or HybridCpuManagedGcRootSourceV1.StringLiteral) ||
            request.Roots.Select(static root => $"{root.Source}:{root.Identity}").Distinct(StringComparer.Ordinal).Count() != request.Roots.Count)
            return Failure(HybridCpuManagedNonMovingGcStatusV1.InvalidState,
                "Explicit roots must be unique handle, pin or helper roots; static and literal roots come from their runtime owners.");
        roots.AddRange(request.Roots.OrderBy(static root => root.Source).ThenBy(static root => root.Identity, StringComparer.Ordinal)
            .Select(static root => root.ObjectReference));
        if (roots.Count > options.Budgets.MaximumRoots)
            return Failure(HybridCpuManagedNonMovingGcStatusV1.BudgetExhausted, "Root budget was exceeded.");

        ulong[] orderedRoots = roots.Where(static root => root != 0).Distinct().Order().ToArray();
        Dictionary<ulong, HybridCpuManagedActiveAllocationV1> active = _heap.ActiveAllocations().ToDictionary(static row => row.ObjectAddress);
        SortedSet<ulong> reachable = new SortedSet<ulong>();
        SortedSet<ulong> pending = new SortedSet<ulong>(orderedRoots);
        long referenceCount = 0;
        while (pending.Count != 0)
        {
            ulong address = pending.Min;
            pending.Remove(address);
            if (!reachable.Add(address)) continue;
            if (!active.TryGetValue(address, out HybridCpuManagedActiveAllocationV1? allocation))
                return Failure(HybridCpuManagedNonMovingGcStatusV1.InvalidState,
                    $"A root or managed field names a non-active heap address. address=0x{address:x16}, " +
                    $"source={(orderedRoots.Contains(address) ? "root" : "field")}, active-objects={active.Count}.");
            ScanResult scan = Scan(allocation);
            if (!scan.IsSuccess) return Failure(scan.Status, scan.Reason);
            referenceCount += scan.References.Length;
            if (referenceCount > options.Budgets.MaximumObjectReferences)
                return Failure(HybridCpuManagedNonMovingGcStatusV1.BudgetExhausted,
                    "Traversed object-reference budget was exceeded.");
            foreach (ulong child in scan.References.Where(static value => value != 0))
                if (!reachable.Contains(child)) pending.Add(child);
        }

        ulong[] reclaimed = active.Keys.Where(address => !reachable.Contains(address)).Order().ToArray();
        HybridCpuManagedHeapResultV1 sweep = _heap.ReclaimUnmarked(reachable);
        if (!sweep.IsSuccess) return Failure(HybridCpuManagedNonMovingGcStatusV1.HeapFailure, sweep.Reason);
        HybridCpuManagedFreeBlockV1[] free = _heap.FreeBlocks().ToArray();
        string digest = HybridCpuPlatformContractV1.Hash(string.Join('|',
            "hybridcpu.managed-nonmoving-gc-result/v1", options.OptionsDigest, _types.Digest,
            string.Join(',', orderedRoots), string.Join(',', reachable), string.Join(',', reclaimed),
            string.Join(';', free.Select(static row => $"{row.Address}:{row.SizeBytes}"))));
        return new(HybridCpuManagedNonMovingGcStatusV1.Collected,
            "Precise single-context STW non-moving mark-sweep collection completed.", orderedRoots,
            reachable.ToArray(), reclaimed, free, digest);
    }

    public HybridCpuManagedHeapResultV1 AllocateWithCollectionRetry(ulong typeHandle, int? objectSizeBytes,
        HybridCpuManagedNonMovingGcRequestV1 request, HybridCpuManagedNonMovingGcOptionsV1? options = null)
    {
        HybridCpuManagedHeapResultV1 first = objectSizeBytes is int size
            ? _heap.TryAllocateVariable(typeHandle, size) : _heap.TryAllocate(typeHandle);
        if (first.Status != HybridCpuManagedHeapStatusV1.OutOfMemory) return first;
        HybridCpuManagedNonMovingGcResultV1 collection = Collect(request, options);
        if (!collection.IsSuccess)
            return new(HybridCpuManagedHeapStatusV1.OutOfMemory,
                $"Collection retry failed: {collection.Status}: {collection.Reason}", 0, 0,
                HybridCpuPlatformContractV1.Hash($"gc-retry-failure|{collection.ResultDigest}"));
        HybridCpuManagedHeapResultV1 retry = objectSizeBytes is int retrySize
            ? _heap.TryAllocateVariable(typeHandle, retrySize) : _heap.TryAllocate(typeHandle);
        if (retry.Status != HybridCpuManagedHeapStatusV1.OutOfMemory) return retry;
        return objectSizeBytes is int terminatingSize
            ? _heap.AllocateVariable(typeHandle, terminatingSize) : _heap.Allocate(typeHandle);
    }

    private ScanResult Scan(HybridCpuManagedActiveAllocationV1 allocation)
    {
        int scanBytes = allocation.ObjectSizeBytes;
        HybridCpuManagedTypeDescriptorV1? declaredType = _types.Resolve(allocation.TypeId);
        if (declaredType is { Kind: HybridCpuManagedTypeKindV1.SzArray, ArrayShape: { } pointerless } &&
            pointerless.ElementStorageKind != HybridCpuManagedStorageKindV1.ObjectReference)
        {
            // Scalar payload has no GC edges. Read its header/length, validate the
            // complete declared bounds, and avoid copying e.g. the entire WAD.
            try { scanBytes = checked(Math.Max(16, pointerless.LengthOffsetBytes + 4)); }
            catch (OverflowException) { return ScanResult.Fail("SZARRAY length slot overflowed."); }
        }
        if (scanBytes < 16 || scanBytes > allocation.ObjectSizeBytes)
            return ScanResult.Fail("An active allocation cannot be read exactly.");
        byte[]? bytes;
        if (scanBytes == allocation.ObjectSizeBytes) bytes = _heap.ReadObjectBytes(allocation.ObjectAddress);
        else
        {
            bytes = new byte[scanBytes];
            if (!_heap.TryReadObjectBytes(allocation.ObjectAddress, 0, bytes)) bytes = null;
        }
        if (bytes is null || bytes.Length != scanBytes)
            return ScanResult.Fail("An active allocation cannot be read exactly.");
        ulong typeHandle = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        HybridCpuManagedTypeDescriptorV1? type = _types.ResolveTypeHandle(typeHandle);
        if (type is null || type.TypeId != allocation.TypeId ||
            !string.Equals(type.DescriptorDigest, allocation.TypeDescriptorDigest, StringComparison.Ordinal))
            return ScanResult.Fail("Object header type identity does not match its active allocation record.");
        List<ulong> references = new List<ulong>();
        switch (type.Kind)
        {
            case HybridCpuManagedTypeKindV1.Class:
                for (HybridCpuManagedTypeDescriptorV1? current = type; current is not null;
                    current = current.BaseTypeId is ulong parent ? _types.Resolve(parent) : null)
                    foreach (int offset in current.InstanceFields.Where(static field =>
                                 field.StorageKind == HybridCpuManagedStorageKindV1.ObjectReference)
                             .Select(static field => field.OffsetBytes).Order())
                        if (!ReadReference(bytes, offset, references)) return ScanResult.Fail("Class pointer map exceeds object bounds.");
                break;
            case HybridCpuManagedTypeKindV1.SzArray:
                if (type.ArrayShape is not { } array || array.LengthOffsetBytes > bytes.Length - 4)
                    return ScanResult.Fail("SZARRAY shape or length slot is invalid.");
                int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(array.LengthOffsetBytes, 4));
                if (length < 0) return ScanResult.Fail("SZARRAY length is negative.");
                int end;
                try { end = checked(array.DataOffsetBytes + length * array.ElementSizeBytes); }
                catch (OverflowException) { return ScanResult.Fail("SZARRAY payload bounds overflowed."); }
                if (end > allocation.ObjectSizeBytes) return ScanResult.Fail("SZARRAY payload exceeds its allocation.");
                if (array.ElementStorageKind == HybridCpuManagedStorageKindV1.ObjectReference)
                    for (int index = 0; index < length; index++)
                        if (!ReadReference(bytes, checked(array.DataOffsetBytes + index * 8), references))
                            return ScanResult.Fail("SZARRAY reference slot exceeds object bounds.");
                break;
            case HybridCpuManagedTypeKindV1.String:
                if (type.StringShape is not { } text || text.LengthOffsetBytes > bytes.Length - 4)
                    return ScanResult.Fail("String shape or length slot is invalid.");
                int chars = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(text.LengthOffsetBytes, 4));
                if (chars < 0 || text.DataOffsetBytes > bytes.Length || chars > (bytes.Length - text.DataOffsetBytes) / 2)
                    return ScanResult.Fail("String UTF-16 payload exceeds its allocation.");
                break;
            case HybridCpuManagedTypeKindV1.ValueType:
                if (type.ValueTypeShape is not { } value) return ScanResult.Fail("Boxed value shape is absent.");
                foreach (int offset in value.ObjectReferenceOffsets)
                    if (!ReadReference(bytes, checked(value.BoxedPayloadOffsetBytes + offset), references))
                        return ScanResult.Fail("Boxed value pointer map exceeds object bounds.");
                break;
            default:
                return ScanResult.Fail("The active allocation has a non-object type kind.");
        }
        return new(true, HybridCpuManagedNonMovingGcStatusV1.Collected, string.Empty, references.ToArray());
    }

    private ParseResult ParseRegistration(HybridCpuManagedStackMapRegistrationV1 item,
        HybridCpuManagedNonMovingGcBudgetsV1 budgets)
    {
        if (item is not null && _registrationCache.TryGetValue(item, out ParsedRegistrationCache? cached) &&
            cached.Budgets == budgets && item.GcInfo is not null && item.CodeManagerMetadata is not null &&
            item.GcInfo.AsSpan().SequenceEqual(cached.GcInfo) &&
            item.CodeManagerMetadata.AsSpan().SequenceEqual(cached.CodeManager) &&
            (item.UnwindInfo is null ? cached.Unwind is null :
                cached.Unwind is not null && item.UnwindInfo.AsSpan().SequenceEqual(cached.Unwind)))
            return cached.Result;
        ParseResult result = ParseRegistrationUncached(item!, budgets);
        if (result.Registration is not null)
        {
            _registrationCache.Remove(item!);
            _registrationCache.Add(item!, new(budgets, item!.GcInfo.ToArray(),
                item.CodeManagerMetadata.ToArray(), item.UnwindInfo?.ToArray(), result));
        }
        return result;
    }

    private ParseResult ParseRegistrationUncached(HybridCpuManagedStackMapRegistrationV1 item,
        HybridCpuManagedNonMovingGcBudgetsV1 budgets)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.MethodIdentity) || item.GcInfo is null ||
            item.CodeManagerMetadata is null || item.ManagedAbiDigest != _managedAbiDigest ||
            item.TargetPlatformDigest != _targetPlatformDigest || item.NativeAbiDigest != _nativeAbiDigest ||
            item.RuntimePackRevision != _runtimePackRevision)
            return ParseResult.Fail(HybridCpuManagedNonMovingGcStatusV1.Unsupported,
                "Method registration does not bind the exact runtime ABI tuple.");
        byte[] metadata = item.CodeManagerMetadata;
        if (metadata.Length != 92 || BinaryPrimitives.ReadUInt32LittleEndian(metadata) != CodeManagerMagic ||
            BinaryPrimitives.ReadUInt16LittleEndian(metadata.AsSpan(4)) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(metadata.AsSpan(6)) != 0 ||
            BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(8)) != 1)
            return ParseResult.Fail(HybridCpuManagedNonMovingGcStatusV1.InvalidMetadata,
                "Code-manager metadata must contain one HCMM 1.0 record.");
        ReadOnlySpan<byte> record = metadata.AsSpan(12, 80);
        if (!record[..8].SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(item.MethodIdentity)).AsSpan(0, 8)))
            return ParseResult.Fail(HybridCpuManagedNonMovingGcStatusV1.InvalidMetadata,
                "HCMM method identity does not match its registration.");
        int start = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
        int size = BinaryPrimitives.ReadInt32LittleEndian(record[12..]);
        bool hasUnwindDigest = record[48..80].ToArray().Any(static value => value != 0);
        if (start < 0 || size <= 0 || hasUnwindDigest != (item.UnwindInfo is { Length: > 0 }))
            return ParseResult.Fail(HybridCpuManagedNonMovingGcStatusV1.Unsupported,
                "Code range or managed unwind registration presence is inconsistent.");
        if (hasUnwindDigest && (!record[48..80].SequenceEqual(SHA256.HashData(item.UnwindInfo!)) ||
                !ValidManagedUnwind(item.UnwindInfo!)))
            return ParseResult.Fail(HybridCpuManagedNonMovingGcStatusV1.InvalidMetadata,
                "HCMM unwind digest or managed unwind bytes are invalid.");
        string digest = HybridCpuPlatformContractV1.Hash($"gc-info|{_managedAbiDigest}|{Convert.ToHexString(item.GcInfo)}");
        if (!record[16..48].SequenceEqual(Convert.FromHexString(digest)))
            return ParseResult.Fail(HybridCpuManagedNonMovingGcStatusV1.InvalidMetadata,
                "HCMM GC digest does not match HCMG bytes.");
        SafepointParse safepoints = ParseSafepoints(item.GcInfo, budgets);
        return safepoints.Safepoints is null ? ParseResult.Fail(safepoints.Status, safepoints.Reason) :
            new(safepoints.Status, safepoints.Reason, new(start, size, safepoints.Safepoints));
    }

    private static SafepointParse ParseSafepoints(byte[] bytes, HybridCpuManagedNonMovingGcBudgetsV1 budgets)
    {
        if (bytes.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != GcInfoMagic ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) != 0)
            return SafepointParse.Fail("GC metadata is not HCMG 1.0.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        if (count < 0 || count > budgets.MaximumSafepoints)
            return new(HybridCpuManagedNonMovingGcStatusV1.BudgetExhausted, "Safepoint budget was exceeded.", null);
        Dictionary<int, RootLocation[]> result = new Dictionary<int, RootLocation[]>();
        int offset = 12, roots = 0;
        for (int index = 0; index < count; index++)
        {
            if (bytes.Length - offset < 8) return SafepointParse.Fail("Safepoint header is truncated.");
            int codeOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            byte category = bytes[offset + 4];
            int referenceCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 6));
            offset += 8;
            roots = checked(roots + referenceCount);
            if (codeOffset < 0 || category > 1 || roots > budgets.MaximumRoots || bytes.Length - offset < referenceCount * 16)
                return SafepointParse.Fail("Safepoint or root collection is malformed.");
            RootLocation[] locations = new RootLocation[referenceCount];
            for (int reference = 0; reference < referenceCount; reference++, offset += 16)
            {
                byte referenceKind = bytes[offset + 8], locationKind = bytes[offset + 9];
                int register = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset + 10));
                int stack = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 12));
                if (referenceKind != 0 || locationKind > 1 ||
                    locationKind == 0 && (register is < 0 or >= 64 || stack != -1) ||
                    locationKind == 1 && (register != -1 || stack < 0 || stack % 8 != 0))
                    return SafepointParse.Fail("A root location is unsupported or malformed.");
                locations[reference] = locationKind == 0 ? new(register, null) : new(null, stack);
            }
            if (!result.TryAdd(codeOffset, locations)) return SafepointParse.Fail("Safepoint offsets are not unique.");
        }
        return offset == bytes.Length
            ? new(HybridCpuManagedNonMovingGcStatusV1.Collected, string.Empty, result)
            : SafepointParse.Fail("GC metadata has trailing bytes.");
    }

    private static bool ValidManagedUnwind(byte[] bytes)
    {
        if (bytes.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != HybridCpuManagedEhSchemaV1.UnwindMagic ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) != HybridCpuManagedEhSchemaV1.SchemaVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) != 0 ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)) != bytes.Length)
            return false;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10));
        int cfa = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        int register = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));
        int stack = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20));
        if (count > 32 || bytes.Length != 28 + count * 8 || bytes[8] > 1 || bytes[9] > 1 ||
            cfa < 0 || cfa % 8 != 0 ||
            !((register is >= 1 and <= 31 && stack == int.MinValue) ||
              (register == -1 && stack != int.MinValue && stack % 8 == 0)))
            return false;
        int prior = 0;
        for (int index = 0, offset = 28; index < count; index++, offset += 8)
        {
            int savedRegister = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            int savedOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4));
            if (savedRegister is < 1 or > 31 || savedRegister <= prior ||
                savedOffset == int.MinValue || savedOffset % 8 != 0)
                return false;
            prior = savedRegister;
        }
        return true;
    }

    private static bool ReadReference(byte[] bytes, int offset, ICollection<ulong> output)
    {
        if (offset < 16 || offset > bytes.Length - 8) return false;
        output.Add(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8)));
        return true;
    }

    private static bool ValidOptions(HybridCpuManagedNonMovingGcOptionsV1 options)
    {
        HybridCpuManagedNonMovingGcBudgetsV1 budgets = options.Budgets;
        return budgets is not null && budgets.MaximumFrames > 0 && budgets.MaximumSafepoints > 0 &&
            budgets.MaximumRoots > 0 && budgets.MaximumObjects > 0 && budgets.MaximumObjectReferences > 0 &&
            budgets.MaximumFrames <= HybridCpuManagedNonMovingGcBudgetsV1.Production.MaximumFrames &&
            budgets.MaximumSafepoints <= HybridCpuManagedNonMovingGcBudgetsV1.Production.MaximumSafepoints &&
            budgets.MaximumRoots <= HybridCpuManagedNonMovingGcBudgetsV1.Production.MaximumRoots &&
            budgets.MaximumObjects <= HybridCpuManagedNonMovingGcBudgetsV1.Production.MaximumObjects &&
            budgets.MaximumObjectReferences <= HybridCpuManagedNonMovingGcBudgetsV1.Production.MaximumObjectReferences &&
            options.OptionsDigest == HybridCpuManagedNonMovingGcOptionsV1.Create(options.EnableCollection, budgets).OptionsDigest;
    }

    private static string Required(string value, string name) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Exact contract identity is required.", name);

    private static HybridCpuManagedNonMovingGcResultV1 Failure(HybridCpuManagedNonMovingGcStatusV1 status, string reason) =>
        new(status, reason, [], [], [], [], HybridCpuPlatformContractV1.Hash($"nonmoving-gc-failure|{status}|{reason}"));

    private sealed record Registration(int CodeStart, int CodeSize, IReadOnlyDictionary<int, RootLocation[]> Safepoints);
    private sealed record ParsedRegistrationCache(HybridCpuManagedNonMovingGcBudgetsV1 Budgets,
        byte[] GcInfo, byte[] CodeManager, byte[]? Unwind, ParseResult Result);
    private sealed record RootLocation(int? Register, int? StackOffset);
    private sealed record ParseResult(HybridCpuManagedNonMovingGcStatusV1 Status, string Reason, Registration? Registration)
    {
        public static ParseResult Fail(HybridCpuManagedNonMovingGcStatusV1 status, string reason) => new(status, reason, null);
    }
    private sealed record SafepointParse(HybridCpuManagedNonMovingGcStatusV1 Status, string Reason,
        IReadOnlyDictionary<int, RootLocation[]>? Safepoints)
    {
        public static SafepointParse Fail(string reason) => new(HybridCpuManagedNonMovingGcStatusV1.InvalidMetadata, reason, null);
    }
    private sealed record ScanResult(bool IsSuccess, HybridCpuManagedNonMovingGcStatusV1 Status, string Reason, ulong[] References)
    {
        public static ScanResult Fail(string reason) => new(false, HybridCpuManagedNonMovingGcStatusV1.InvalidState, reason, []);
    }
}
