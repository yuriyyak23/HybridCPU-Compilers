using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Platform.Contracts;

public enum HybridCpuInteropValueKindV1 : byte
{
    Void = 0,
    SignedInteger = 1,
    UnsignedInteger = 2,
    NativeInteger = 3,
    Float = 4,
    RawPointer = 5,
    FixedLayoutStruct = 6,
    BufferPointer = 7,
    BufferLength = 8
}

public enum HybridCpuInteropCallingConventionV1 : byte
{
    HybridCpuNativeV2 = 0
}

public enum HybridCpuHostBufferAccessV1 : byte
{
    None = 0,
    Read = 1,
    Write = 2,
    ReadWrite = 3
}

public enum HybridCpuExternalServiceStatusV1 : byte
{
    Success = 0,
    InvalidRequest = 1,
    AccessDenied = 2,
    MissingProvider = 3,
    MissingSymbol = 4,
    ProviderFailure = 5,
    ManagedExceptionContained = 6,
    ReentrancyRejected = 7,
    Disabled = 8
}

public sealed record HybridCpuInteropValueDescriptorV1(
    HybridCpuInteropValueKindV1 Kind,
    int SizeBytes,
    int AlignmentBytes,
    bool ContainsManagedReferences = false);

public sealed record HybridCpuManagedInteropSignatureV1(
    string Library,
    string Symbol,
    HybridCpuInteropCallingConventionV1 CallingConvention,
    IReadOnlyList<HybridCpuInteropValueDescriptorV1> Parameters,
    HybridCpuInteropValueDescriptorV1 ReturnValue,
    bool SetLastError = false,
    bool AllowsCallback = false);

public sealed record HybridCpuExternalServiceRequestV1(
    ulong ContextId,
    HybridCpuHostServiceV1 Service,
    ulong Operation,
    HybridCpuPrivilegeModeV1 PrivilegeMode,
    ulong BufferAddress,
    ulong BufferLength,
    HybridCpuHostBufferAccessV1 BufferAccess,
    IReadOnlyList<ulong> Arguments,
    string TransitionDigest);

public sealed record HybridCpuExternalServiceResultV1(
    HybridCpuExternalServiceStatusV1 Status,
    ulong ReturnValue,
    int ErrorCode,
    string Reason,
    string ResultDigest)
{
    public bool IsSuccess => Status == HybridCpuExternalServiceStatusV1.Success;
}

/// <summary>
/// Language-neutral V1 interop and external-service boundary. Library policy, object pinning and
/// managed transition state are deliberately absent; those remain ManagedRuntime authority.
/// </summary>
public static class HybridCpuManagedInteropContractV1
{
    public const string SchemaId = "hybridcpu.managed-interop/v1";
    public const int SchemaVersion = 1;
    public const int MaximumParameters = 16;
    public const int MaximumFixedStructBytes = 16;
    public const int MaximumIdentityBytes = 64;
    public const int MaximumHostArguments = 16;

    public static string ContractDigest { get; } = Hash(string.Join('|', SchemaId, SchemaVersion,
        "native-abi=v2", "blittable-only", "integers=8,16,32,64", "fp=32,64-bit-carrier",
        "pointer=64", $"fixed-struct-max={MaximumFixedStructBytes}", "buffer=pointer+length",
        "varargs=false", "callbacks=false", "exceptions=contained", "host=service-id-not-opcode",
        "address=kernel-validated", "privilege=user", "deterministic-symbol-import",
        HybridCpuVirtualClockServiceContractV1.ContractDigest,
        HybridCpuFramebufferServiceContractV1.ContractDigest,
        HybridCpuConsoleServiceContractV1.ContractDigest,
        HybridCpuBootBlobServiceContractV1.ContractDigest,
        HybridCpuInputServiceContractV1.ContractDigest,
        HybridCpuExternalServiceEcallContractV1.ContractDigest));

    public static bool TryValidateSignature(HybridCpuManagedInteropSignatureV1? signature, out string reason)
    {
        if (signature is null || !Identity(signature.Library) || !Identity(signature.Symbol) ||
            !Enum.IsDefined(signature.CallingConvention) || signature.Parameters is null ||
            signature.ReturnValue is null || signature.Parameters.Count > MaximumParameters)
        {
            reason = "Interop signature identity, calling convention or parameter budget is invalid.";
            return false;
        }
        if (signature.SetLastError || signature.AllowsCallback)
        {
            reason = "SetLastError and callbacks are outside managed interop V1.";
            return false;
        }
        if (!ValidValue(signature.ReturnValue, allowVoid: true) ||
            signature.Parameters.Any(parameter => !ValidValue(parameter, allowVoid: false)))
        {
            reason = "Interop V1 admits only blittable scalars, raw pointers, bounded fixed structs and explicit buffers.";
            return false;
        }
        for (int index = 0; index < signature.Parameters.Count; index++)
        {
            HybridCpuInteropValueKindV1 kind = signature.Parameters[index].Kind;
            if (kind == HybridCpuInteropValueKindV1.BufferPointer &&
                (index + 1 >= signature.Parameters.Count ||
                 signature.Parameters[index + 1].Kind != HybridCpuInteropValueKindV1.BufferLength))
            {
                reason = "Every buffer pointer must be followed by its explicit length.";
                return false;
            }
            if (kind == HybridCpuInteropValueKindV1.BufferLength &&
                (index == 0 || signature.Parameters[index - 1].Kind != HybridCpuInteropValueKindV1.BufferPointer))
            {
                reason = "A buffer length is valid only immediately after its buffer pointer.";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    public static string SignatureDigest(HybridCpuManagedInteropSignatureV1 signature)
    {
        if (!TryValidateSignature(signature, out string reason)) throw new ArgumentException(reason, nameof(signature));
        return Hash(string.Join('|', ContractDigest, signature.Library, signature.Symbol,
            signature.CallingConvention, string.Join(';', signature.Parameters.Select(ValueText)),
            ValueText(signature.ReturnValue), signature.SetLastError, signature.AllowsCallback));
    }

    public static string ImportSymbol(HybridCpuManagedInteropSignatureV1 signature) =>
        $"__hybridcpu_pinvoke_{SignatureDigest(signature)[..24]}";

    public static string ComputeTransitionDigest(HybridCpuExternalServiceRequestV1 request) =>
        Hash(string.Join('|', ContractDigest, request.ContextId, request.Service, request.Operation,
            request.PrivilegeMode, request.BufferAddress, request.BufferLength, request.BufferAccess,
            string.Join(',', request.Arguments ?? [])));

    public static string ComputeResultDigest(HybridCpuExternalServiceRequestV1 request,
        HybridCpuExternalServiceStatusV1 status, ulong value, int errorCode, string reason) =>
        Hash(string.Join('|', request.TransitionDigest, status, value, errorCode, reason));

    private static bool ValidValue(HybridCpuInteropValueDescriptorV1 value, bool allowVoid)
    {
        if (!Enum.IsDefined(value.Kind) || value.ContainsManagedReferences) return false;
        return value.Kind switch
        {
            HybridCpuInteropValueKindV1.Void => allowVoid && value.SizeBytes == 0 && value.AlignmentBytes == 1,
            HybridCpuInteropValueKindV1.SignedInteger or HybridCpuInteropValueKindV1.UnsignedInteger =>
                value.SizeBytes is 1 or 2 or 4 or 8 && value.AlignmentBytes == value.SizeBytes,
            HybridCpuInteropValueKindV1.NativeInteger or HybridCpuInteropValueKindV1.RawPointer or
                HybridCpuInteropValueKindV1.BufferPointer or HybridCpuInteropValueKindV1.BufferLength =>
                value.SizeBytes == 8 && value.AlignmentBytes == 8,
            HybridCpuInteropValueKindV1.Float =>
                value.SizeBytes is 4 or 8 && value.AlignmentBytes == value.SizeBytes,
            HybridCpuInteropValueKindV1.FixedLayoutStruct => value.SizeBytes is > 0 and <= MaximumFixedStructBytes &&
                value.AlignmentBytes is 1 or 2 or 4 or 8 && value.SizeBytes % value.AlignmentBytes == 0,
            _ => false
        };
    }

    private static bool Identity(string value) => !string.IsNullOrWhiteSpace(value) &&
        Encoding.UTF8.GetByteCount(value) <= MaximumIdentityBytes &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static string ValueText(HybridCpuInteropValueDescriptorV1 value) =>
        $"{value.Kind}:{value.SizeBytes}:{value.AlignmentBytes}:{value.ContainsManagedReferences}";

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
