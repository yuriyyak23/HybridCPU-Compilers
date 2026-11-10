using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

namespace HybridCPU.ManagedRuntime;

public sealed record HybridCpuManagedInteropOptionsV1(
    bool Enabled,
    bool AllowCallbacks,
    int MaximumPinnedHandles,
    string OptionsDigest)
{
    public static HybridCpuManagedInteropOptionsV1 Production { get; } = Create(false, false, 64);
    public static HybridCpuManagedInteropOptionsV1 Qualification { get; } = Create(true, false, 64);

    public static HybridCpuManagedInteropOptionsV1 Create(bool enabled, bool callbacks, int pins) =>
        new(enabled, callbacks, pins, HybridCpuPlatformContractV1.Hash(string.Join('|',
            "hybridcpu.managed-interop-options/v1", enabled, callbacks, pins, "default-off")));
}

public sealed record HybridCpuManagedNativeSymbolV1(
    string Library,
    string Symbol,
    string SignatureDigest,
    HybridCpuHostServiceV1 Service,
    ulong Operation,
    string ResolutionDigest);

public sealed class HybridCpuManagedNativeResolverV1
{
    private readonly SortedDictionary<string, HybridCpuManagedNativeSymbolV1> _symbols =
        new(StringComparer.Ordinal);

    public bool Register(HybridCpuManagedInteropSignatureV1 signature,
        HybridCpuHostServiceV1 service, ulong operation)
    {
        if (!HybridCpuManagedInteropContractV1.TryValidateSignature(signature, out _) ||
            !Enum.IsDefined(service) || service == HybridCpuHostServiceV1.ProcessExit || operation == 0)
            return false;
        string signatureDigest = HybridCpuManagedInteropContractV1.SignatureDigest(signature);
        string key = Key(signature.Library, signature.Symbol);
        HybridCpuManagedNativeSymbolV1 symbol = new HybridCpuManagedNativeSymbolV1(signature.Library, signature.Symbol,
            signatureDigest, service, operation, HybridCpuPlatformContractV1.Hash(string.Join('|',
                HybridCpuManagedInteropContractV1.ContractDigest, key, signatureDigest, service, operation)));
        return _symbols.TryAdd(key, symbol);
    }

    public HybridCpuManagedNativeSymbolV1? Resolve(HybridCpuManagedInteropSignatureV1 signature) =>
        _symbols.TryGetValue(Key(signature.Library, signature.Symbol), out HybridCpuManagedNativeSymbolV1? symbol) &&
        symbol.SignatureDigest == HybridCpuManagedInteropContractV1.SignatureDigest(signature) ? symbol : null;

    private static string Key(string library, string symbol) => $"{library}!{symbol}";
}

public sealed record HybridCpuManagedInteropInvocationV1(
    ulong ManagedThreadId,
    HybridCpuManagedInteropSignatureV1 Signature,
    IReadOnlyList<ulong> Arguments,
    ulong BufferAddress = 0,
    ulong BufferLength = 0,
    HybridCpuHostBufferAccessV1 BufferAccess = HybridCpuHostBufferAccessV1.None,
    IReadOnlyList<ulong>? PinnedObjectReferences = null);

public sealed record HybridCpuManagedInteropResultV1(
    HybridCpuExternalServiceStatusV1 Status,
    ulong ReturnValue,
    int ErrorCode,
    bool GcDeferredDuringTransition,
    IReadOnlyList<ulong> NativeCallRoots,
    string Reason,
    string ResultDigest)
{
    public bool IsSuccess => Status == HybridCpuExternalServiceStatusV1.Success;
}

public enum HybridCpuManagedInteropGcRequestV1 : byte
{
    Ready = 0,
    DeferredByNativeTransition = 1,
    InvalidThread = 2
}

/// <summary>
/// Runtime-owned managed/native transition state, exact symbol policy and pinned-root lifetime.
/// The kernel validates generic external services; no host API or ISE execution lives here.
/// </summary>
public sealed class HybridCpuManagedInteropRuntimeV1
{
    private readonly IHybridCpuRuntimeKernelV1 _kernel;
    private readonly HybridCpuManagedThreadingRuntimeV1 _threads;
    private readonly HybridCpuManagedNativeResolverV1 _resolver;
    private readonly HybridCpuManagedInteropOptionsV1 _options;
    private readonly SortedDictionary<ulong, TransitionState> _transitions = [];
    private readonly SortedDictionary<string, HybridCpuManagedGcRootV1> _nativeRoots =
        new(StringComparer.Ordinal);

    public HybridCpuManagedInteropRuntimeV1(IHybridCpuRuntimeKernelV1 kernel,
        HybridCpuManagedThreadingRuntimeV1 threads, HybridCpuManagedNativeResolverV1 resolver,
        HybridCpuManagedInteropOptionsV1? options = null)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _threads = threads ?? throw new ArgumentNullException(nameof(threads));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _options = options ?? HybridCpuManagedInteropOptionsV1.Production;
        HybridCpuManagedInteropOptionsV1 expected = HybridCpuManagedInteropOptionsV1.Create(
            _options.Enabled, _options.AllowCallbacks, _options.MaximumPinnedHandles);
        if (_options.OptionsDigest != expected.OptionsDigest || _options.MaximumPinnedHandles <= 0 ||
            _options.AllowCallbacks)
            throw new ArgumentException("Managed interop options are invalid or admit unsupported callbacks.", nameof(options));
    }

    public HybridCpuManagedInteropResultV1 Invoke(HybridCpuManagedInteropInvocationV1 invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!_options.Enabled)
            return Result(invocation, HybridCpuExternalServiceStatusV1.Disabled, 0, 0, false, [],
                "Managed interop is default-disabled.");
        if (!HybridCpuManagedInteropContractV1.TryValidateSignature(invocation.Signature, out string reason) ||
            invocation.Arguments is null ||
            invocation.Arguments.Count > HybridCpuManagedInteropContractV1.MaximumHostArguments ||
            !_threads.TryResolveContext(invocation.ManagedThreadId, out ulong contextId))
            return Result(invocation, HybridCpuExternalServiceStatusV1.InvalidRequest, 0, 0, false, [],
                string.IsNullOrEmpty(reason) ? "Interop invocation or managed thread is invalid." : reason);
        if (_transitions.ContainsKey(contextId))
            return Result(invocation, HybridCpuExternalServiceStatusV1.ReentrancyRejected, 0, 0, false, [],
                "Callbacks and managed reentrancy from a native frame are outside interop V1.");
        HybridCpuManagedNativeSymbolV1? symbol = _resolver.Resolve(invocation.Signature);
        if (symbol is null)
            return Result(invocation, HybridCpuExternalServiceStatusV1.MissingSymbol, 0, 2, false, [],
                "The native library or exact symbol/signature binding is missing.");
        ulong[] pins = (invocation.PinnedObjectReferences ?? []).Where(static value => value != 0)
            .Distinct().Order().ToArray();
        if (pins.Length > _options.MaximumPinnedHandles || _nativeRoots.Count > _options.MaximumPinnedHandles - pins.Length)
            return Result(invocation, HybridCpuExternalServiceStatusV1.InvalidRequest, 0, 0, false, [],
                "The deterministic pinned-handle budget was exceeded.");
        TransitionState state = new TransitionState(invocation.ManagedThreadId, contextId, false);
        _transitions.Add(contextId, state);
        string[] keys = pins.Select((value, index) => $"native:{contextId}:{index}:{value}").ToArray();
        for (int index = 0; index < pins.Length; index++)
            _nativeRoots.Add(keys[index], new(keys[index], HybridCpuManagedGcRootSourceV1.Pinned, pins[index]));
        ulong[] roots = pins.ToArray();
        try
        {
            HybridCpuExternalServiceRequestV1 draft = new HybridCpuExternalServiceRequestV1(contextId, symbol.Service, symbol.Operation,
                HybridCpuPrivilegeModeV1.User, invocation.BufferAddress, invocation.BufferLength,
                invocation.BufferAccess, invocation.Arguments, string.Empty);
            HybridCpuExternalServiceRequestV1 request = draft with
            {
                TransitionDigest = HybridCpuManagedInteropContractV1.ComputeTransitionDigest(draft)
            };
            HybridCpuExternalServiceResultV1 result = _kernel.ExternalServiceTransition(request);
            bool deferred = _transitions[contextId].GcRequested;
            return Result(invocation, result.Status, result.ReturnValue, result.ErrorCode, deferred, roots,
                result.Reason);
        }
        catch (Exception exception)
        {
            return Result(invocation, HybridCpuExternalServiceStatusV1.ManagedExceptionContained, 0, 5,
                _transitions[contextId].GcRequested, roots,
                $"Managed/native boundary exception was contained: {exception.GetType().Name}.");
        }
        finally
        {
            foreach (string key in keys) _nativeRoots.Remove(key);
            _transitions.Remove(contextId);
        }
    }

    public HybridCpuManagedInteropGcRequestV1 RequestGc(ulong managedThreadId)
    {
        if (!_threads.TryResolveContext(managedThreadId, out ulong contextId))
            return HybridCpuManagedInteropGcRequestV1.InvalidThread;
        if (!_transitions.TryGetValue(contextId, out TransitionState? transition))
            return HybridCpuManagedInteropGcRequestV1.Ready;
        _transitions[contextId] = transition with { GcRequested = true };
        return HybridCpuManagedInteropGcRequestV1.DeferredByNativeTransition;
    }

    public IReadOnlyList<HybridCpuManagedGcRootV1> NativeCallRoots() => _nativeRoots.Values.ToArray();
    public bool IsInNativeTransition(ulong managedThreadId) =>
        _threads.TryResolveContext(managedThreadId, out ulong contextId) && _transitions.ContainsKey(contextId);

    private static HybridCpuManagedInteropResultV1 Result(HybridCpuManagedInteropInvocationV1 invocation,
        HybridCpuExternalServiceStatusV1 status, ulong value, int errorCode, bool gcDeferred,
        IReadOnlyList<ulong> roots, string reason) => new(status, value, errorCode, gcDeferred, roots, reason,
            HybridCpuPlatformContractV1.Hash(string.Join('|', HybridCpuManagedInteropContractV1.ContractDigest,
                invocation.ManagedThreadId, invocation.Signature?.Library, invocation.Signature?.Symbol,
                status, value, errorCode, gcDeferred, string.Join(',', roots), reason)));

    private sealed record TransitionState(ulong ManagedThreadId, ulong ContextId, bool GcRequested);
}
