using HybridCPU.Platform.Contracts;

namespace HybridCPU.RuntimeKernel;

public sealed record HybridCpuHostProviderResultV1(
    HybridCpuExternalServiceStatusV1 Status,
    ulong ReturnValue,
    int ErrorCode,
    string Reason);

public interface IHybridCpuHostServiceProviderV1
{
    HybridCpuHostProviderResultV1 Invoke(HybridCpuExternalServiceRequestV1 request);
}

public sealed record HybridCpuManagedBootBlobProviderResultV1(bool Success, ulong ManagedReference,
    int Length, string RegistryDigest, string Reason);

public interface IHybridCpuManagedBootBlobProviderV1
{
    HybridCpuManagedBootBlobProviderResultV1 Resolve(int blobId);
}

public sealed record HybridCpuManagedInputProviderResultV1(bool Success, ulong ManagedReference,
    string RegistryDigest, string Reason);

public interface IHybridCpuManagedInputProviderV1
{
    HybridCpuManagedInputProviderResultV1 Pull();
}

/// <summary>A deterministic provider used by qualification and embedders; it calls no ambient host API.</summary>
public sealed class DeterministicMockHostServiceProviderV1 : IHybridCpuHostServiceProviderV1
{
    private readonly SortedDictionary<(HybridCpuHostServiceV1 Service, ulong Operation),
        HybridCpuHostProviderResultV1> _results = [];
    private readonly List<string> _trace = [];

    public bool Register(HybridCpuHostServiceV1 service, ulong operation,
        HybridCpuHostProviderResultV1 result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!Enum.IsDefined(service) || service == HybridCpuHostServiceV1.ProcessExit || operation == 0 ||
            !Enum.IsDefined(result.Status)) return false;
        return _results.TryAdd((service, operation), result);
    }

    public HybridCpuHostProviderResultV1 Invoke(HybridCpuExternalServiceRequestV1 request)
    {
        _trace.Add($"{request.ContextId}:{request.Service}:{request.Operation}:" +
            $"{request.BufferAddress}:{request.BufferLength}:{string.Join(',', request.Arguments)}");
        return _results.TryGetValue((request.Service, request.Operation), out HybridCpuHostProviderResultV1? result)
            ? result
            : new(HybridCpuExternalServiceStatusV1.MissingSymbol, 0, 2,
                "The deterministic host provider has no matching service operation.");
    }

    public IReadOnlyList<string> Trace() => _trace.ToArray();
}

public sealed partial class DeterministicRuntimeKernelV1
{
    private readonly IHybridCpuHostServiceProviderV1? _hostServiceProvider;
    private readonly IHybridCpuManagedBootBlobProviderV1? _managedBootBlobProvider;
    private readonly IHybridCpuManagedInputProviderV1? _managedInputProvider;
    private ulong _serviceDeadlineSequence;
    private readonly Dictionary<ulong,(int Width,int Height)> _framebuffers=[];

    public DeterministicRuntimeKernelV1(IHybridCpuHostServiceProviderV1? hostServiceProvider = null,
        IHybridCpuManagedBootBlobProviderV1? managedBootBlobProvider = null,
        IHybridCpuManagedInputProviderV1? managedInputProvider = null)
    {
        _hostServiceProvider = hostServiceProvider;
        _managedBootBlobProvider = managedBootBlobProvider;
        _managedInputProvider = managedInputProvider;
    }

    public HybridCpuExternalServiceResultV1 ExternalServiceTransition(HybridCpuExternalServiceRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!HasLiveContext() || _context is null)
            return ExternalResult(request, HybridCpuExternalServiceStatusV1.InvalidRequest, 0, 0,
                "No live execution context.");
        if (request.ContextId != _context.ContextId || request.PrivilegeMode != HybridCpuPrivilegeModeV1.User ||
            !Enum.IsDefined(request.Service) || request.Service == HybridCpuHostServiceV1.ProcessExit ||
            request.Operation == 0 || !Enum.IsDefined(request.BufferAccess) || request.Arguments is null ||
            request.Arguments.Count > HybridCpuManagedInteropContractV1.MaximumHostArguments ||
            request.TransitionDigest != HybridCpuManagedInteropContractV1.ComputeTransitionDigest(request))
            return ExternalResult(request, HybridCpuExternalServiceStatusV1.InvalidRequest, 0, 0,
                "External service identity, context, privilege, arguments or transition digest is invalid.");
        if (!ValidExternalBuffer(request))
            return ExternalResult(request, HybridCpuExternalServiceStatusV1.AccessDenied, 0, 13,
                "The external service buffer is outside a compatible mapped user range.");
        if (request.Service == HybridCpuHostServiceV1.Clock && request.Operation == HybridCpuVirtualClockServiceContractV1.ReadTicksOperation)
        {
            if (request.Arguments.Count != 0 || request.BufferAddress != 0 || request.BufferLength != 0 ||
                request.BufferAccess != HybridCpuHostBufferAccessV1.None)
                return ExternalResult(request, HybridCpuExternalServiceStatusV1.InvalidRequest, 0, 0,
                    "Virtual clock read has no arguments or buffer.");
            return ExternalResult(request, HybridCpuExternalServiceStatusV1.Success, MonotonicTicks(), 0, string.Empty);
        }
        if (request.Service == HybridCpuHostServiceV1.Clock && request.Operation == HybridCpuVirtualClockServiceContractV1.ReadDoomTicsOperation)
        {
            if (request.Arguments.Count != 0 || request.BufferAddress != 0 || request.BufferLength != 0 ||
                request.BufferAccess != HybridCpuHostBufferAccessV1.None || _virtualClockTicksPerSecond == 0)
                return ExternalResult(request, HybridCpuExternalServiceStatusV1.InvalidRequest, 0, 0,
                    "Doom clock read requires the boot-owned nonzero timebase and no payload.");
            if (!TryScaleClock(MonotonicTicks(), _virtualClockTicksPerSecond, 35, out ulong tics) || tics > int.MaxValue)
                return ExternalResult(request, HybridCpuExternalServiceStatusV1.ProviderFailure, 0, 34,
                    "Doom clock projection exceeds its exact Int32 domain.");
            return ExternalResult(request, HybridCpuExternalServiceStatusV1.Success, tics, 0, string.Empty);
        }
        if (request.Service == HybridCpuHostServiceV1.Clock && request.Operation == HybridCpuVirtualClockServiceContractV1.WaitUntilDoomTicOperation)
        {
            if (request.Arguments.Count != 1 || request.BufferAddress != 0 || request.BufferLength != 0 ||
                request.BufferAccess != HybridCpuHostBufferAccessV1.None || request.Arguments[0] > int.MaxValue ||
                request.Arguments[0] == 0 || !TryDoomDeadline((uint)request.Arguments[0], out ulong deadline))
                return ExternalResult(request, HybridCpuExternalServiceStatusV1.InvalidRequest, 0, 0,
                    "Doom wait requires one positive Int32 target and a finite exact source deadline.");
            ulong token;
            do { token = 0x8000_0000_0000_0000UL | ++_serviceDeadlineSequence; }
            while (Deadlines().Any(row => row.Token == token) && _serviceDeadlineSequence != ulong.MaxValue);
            if (token == ulong.MaxValue || Deadlines().Any(row => row.Token == token))
                return ExternalResult(request, HybridCpuExternalServiceStatusV1.ProviderFailure, 0, 12,
                    "The kernel service deadline token domain is exhausted.");
            HybridCpuDeadlineResultV1 sleep = SleepUntil(new(request.ContextId, deadline, token));
            if (!sleep.IsSuccess)
                return ExternalResult(request, HybridCpuExternalServiceStatusV1.ProviderFailure, 0, 11, sleep.Reason);
            return ExternalResult(request, HybridCpuExternalServiceStatusV1.Success, token, 0, string.Empty);
        }
        if (request.Service == HybridCpuHostServiceV1.Graphics &&
            request.Operation == HybridCpuFramebufferServiceContractV1.InitializeOperation &&
            (request.Arguments.Count != 1 || request.BufferAddress != 0 || request.BufferLength != 0 ||
             request.BufferAccess != HybridCpuHostBufferAccessV1.None ||
             !HybridCpuFramebufferServiceContractV1.TryUnpackDimensions(
                 request.Arguments.Count == 1 ? request.Arguments[0] : 0, out _, out _)))
            return ExternalResult(request, HybridCpuExternalServiceStatusV1.InvalidRequest, 0, 0,
                "Framebuffer initialization requires one bounded packed width/height argument and no buffer.");
        if(request.Service==HybridCpuHostServiceV1.Graphics&&request.Operation==HybridCpuFramebufferServiceContractV1.PresentOperation)
        {
            if(request.Arguments.Count!=0||request.BufferAccess!=HybridCpuHostBufferAccessV1.Read||
               !_framebuffers.TryGetValue(request.ContextId,out (int Width, int Height) framebuffer)||
               request.BufferLength!=(ulong)framebuffer.Width*(ulong)framebuffer.Height)
                return ExternalResult(request,HybridCpuExternalServiceStatusV1.InvalidRequest,0,0,
                    "Framebuffer present requires successful initialization and one exact read-only width*height byte buffer.");
        }
        if(request.Service==HybridCpuHostServiceV1.Graphics&&request.Operation==HybridCpuFramebufferServiceContractV1.UpdatePaletteOperation)
        {
            if(request.Arguments.Count!=0||request.BufferAccess!=HybridCpuHostBufferAccessV1.Read||
               request.BufferLength!=HybridCpuFramebufferServiceContractV1.PaletteBytes||
               !_framebuffers.ContainsKey(request.ContextId))
                return ExternalResult(request,HybridCpuExternalServiceStatusV1.InvalidRequest,0,0,
                    "Palette update requires initialized graphics and one exact read-only 256-entry RGB24 buffer.");
        }
        if (request.Service == HybridCpuHostServiceV1.Console &&
            request.Operation is HybridCpuConsoleServiceContractV1.WriteUtf16Operation or
                HybridCpuConsoleServiceContractV1.SetTitleUtf16Operation &&
            (request.Arguments.Count != 0 ||
             (request.BufferLength == 0
                 ? request.BufferAddress != 0 || request.BufferAccess != HybridCpuHostBufferAccessV1.None
                 : request.BufferAccess != HybridCpuHostBufferAccessV1.Read) ||
             request.BufferLength > checked((ulong)HybridCpuConsoleServiceContractV1.MaximumCodeUnits * 2) ||
             (request.BufferLength & 1) != 0))
            return ExternalResult(request, HybridCpuExternalServiceStatusV1.InvalidRequest, 0, 0,
                "Console UTF-16 write requires one bounded even read-only buffer and no scalar arguments.");
        if (request.Service == HybridCpuHostServiceV1.File && request.Operation == HybridCpuBootBlobServiceContractV1.GetOperation)
        {
            if (request.Arguments.Count != 1 || request.Arguments[0] is 0 or > HybridCpuBootBlobServiceContractV1.MaximumBlobId ||
                request.BufferAddress != 0 || request.BufferLength != 0 || request.BufferAccess != HybridCpuHostBufferAccessV1.None)
                return ExternalResult(request, HybridCpuExternalServiceStatusV1.InvalidRequest,0,0,
                    "Boot blob lookup requires one positive bounded Int32 identity and no buffer.");
            if (_managedBootBlobProvider is null)
                return ExternalResult(request,HybridCpuExternalServiceStatusV1.MissingProvider,0,38,
                    "No trusted managed boot-blob registry is installed.");
            HybridCpuManagedBootBlobProviderResultV1 blob=_managedBootBlobProvider.Resolve((int)request.Arguments[0]);
            if (blob is null || !blob.Success || blob.ManagedReference==0 || blob.Length<=0 ||
                blob.Length>HybridCpuBootBlobServiceContractV1.MaximumBlobBytes || string.IsNullOrWhiteSpace(blob.RegistryDigest))
                return ExternalResult(request,HybridCpuExternalServiceStatusV1.ProviderFailure,0,5,
                    blob?.Reason ?? "The trusted boot-blob registry returned an invalid managed object.");
            return ExternalResult(request,HybridCpuExternalServiceStatusV1.Success,blob.ManagedReference,0,string.Empty);
        }
        if(request.Service==HybridCpuHostServiceV1.Input&&request.Operation==HybridCpuInputServiceContractV1.PullOperation)
        {
            if(request.Arguments.Count!=0||request.BufferAddress!=0||request.BufferLength!=0||
               request.BufferAccess!=HybridCpuHostBufferAccessV1.None)
                return ExternalResult(request,HybridCpuExternalServiceStatusV1.InvalidRequest,0,0,
                    "Input pull requires no arguments or buffer.");
            if(_managedInputProvider is null)
                return ExternalResult(request,HybridCpuExternalServiceStatusV1.MissingProvider,0,38,
                    "No trusted managed input registry is installed.");
            HybridCpuManagedInputProviderResultV1 input=_managedInputProvider.Pull();
            if(input is null||!input.Success||string.IsNullOrWhiteSpace(input.RegistryDigest))
                return ExternalResult(request,HybridCpuExternalServiceStatusV1.ProviderFailure,0,5,
                    input?.Reason??"The trusted input registry returned an invalid managed object.");
            return ExternalResult(request,HybridCpuExternalServiceStatusV1.Success,input.ManagedReference,0,string.Empty);
        }
        if (_hostServiceProvider is null)
            return ExternalResult(request, HybridCpuExternalServiceStatusV1.MissingProvider, 0, 38,
                "No host service provider is installed.");
        try
        {
            HybridCpuHostProviderResultV1 result = _hostServiceProvider.Invoke(request);
            if (result is null || !Enum.IsDefined(result.Status) ||
                result.Status is HybridCpuExternalServiceStatusV1.ManagedExceptionContained or
                    HybridCpuExternalServiceStatusV1.ReentrancyRejected or
                    HybridCpuExternalServiceStatusV1.Disabled)
                return ExternalResult(request, HybridCpuExternalServiceStatusV1.ProviderFailure, 0, 5,
                    "The host provider returned an invalid kernel-boundary disposition.");
            if(result.Status==HybridCpuExternalServiceStatusV1.Success&&request.Service==HybridCpuHostServiceV1.Graphics&&
               request.Operation==HybridCpuFramebufferServiceContractV1.InitializeOperation&&
               HybridCpuFramebufferServiceContractV1.TryUnpackDimensions(request.Arguments[0],out int width,out int height))
                _framebuffers[request.ContextId]=(width,height);
            return ExternalResult(request, result.Status, result.ReturnValue, result.ErrorCode,
                result.Reason ?? string.Empty);
        }
        catch (Exception exception)
        {
            return ExternalResult(request, HybridCpuExternalServiceStatusV1.ProviderFailure, 0, 5,
                $"Host provider exception was contained: {exception.GetType().Name}.");
        }
    }

    private bool ValidExternalBuffer(HybridCpuExternalServiceRequestV1 request)
    {
        if (request.BufferLength == 0)
            return request.BufferAddress == 0 && request.BufferAccess == HybridCpuHostBufferAccessV1.None;
        if (request.BufferAddress == 0 || request.BufferAddress > ulong.MaxValue - request.BufferLength ||
            request.BufferAccess == HybridCpuHostBufferAccessV1.None) return false;
        ulong end = request.BufferAddress + request.BufferLength;
        HybridCpuVmProtectionV1 needed = request.BufferAccess switch
        {
            HybridCpuHostBufferAccessV1.Read => HybridCpuVmProtectionV1.Read,
            HybridCpuHostBufferAccessV1.Write => HybridCpuVmProtectionV1.Write,
            HybridCpuHostBufferAccessV1.ReadWrite => HybridCpuVmProtectionV1.Read | HybridCpuVmProtectionV1.Write,
            _ => HybridCpuVmProtectionV1.None
        };
        return _mappings.Any(mapping => request.BufferAddress >= mapping.Address &&
            end <= mapping.Address + mapping.Size && (mapping.Protection & needed) == needed);
    }

    private static bool TryScaleClock(ulong source, ulong frequency, uint rate, out ulong result)
    {
        ulong whole = source / frequency, fraction = source % frequency;
        if (whole > ulong.MaxValue / rate) { result = 0; return false; }
        ulong quotient = 0, remainder = 0;
        for (int bit = 30; bit >= 0; bit--)
        {
            if (quotient > ulong.MaxValue / 2) { result = 0; return false; }
            quotient *= 2;
            if (remainder >= frequency - remainder) { remainder -= frequency - remainder; quotient++; }
            else remainder += remainder;
            if ((rate & (1U << bit)) == 0) continue;
            if (remainder >= frequency - fraction) { remainder -= frequency - fraction; quotient++; }
            else remainder += fraction;
        }
        ulong prefix = whole * rate;
        if (prefix > ulong.MaxValue - quotient) { result = 0; return false; }
        result = prefix + quotient;
        return true;
    }

    private bool TryDoomDeadline(uint targetTic, out ulong deadline)
    {
        ulong whole = _virtualClockTicksPerSecond / 35;
        ulong remainder = _virtualClockTicksPerSecond % 35;
        if (whole != 0 && targetTic > ulong.MaxValue / whole) { deadline = 0; return false; }
        ulong prefix = targetTic * whole;
        ulong fraction = ((ulong)targetTic * remainder + 34) / 35;
        if (prefix > ulong.MaxValue - fraction || prefix + fraction == ulong.MaxValue)
        { deadline = 0; return false; }
        deadline = prefix + fraction;
        return true;
    }

    private static HybridCpuExternalServiceResultV1 ExternalResult(HybridCpuExternalServiceRequestV1 request,
        HybridCpuExternalServiceStatusV1 status, ulong value, int errorCode, string reason) =>
        new(status, value, errorCode, reason,
            HybridCpuManagedInteropContractV1.ComputeResultDigest(request, status, value, errorCode, reason));
}
