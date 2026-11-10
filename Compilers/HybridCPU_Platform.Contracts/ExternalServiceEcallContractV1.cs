using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Platform.Contracts;

/// <summary>
/// User-mode ECALL envelope used to enter the trusted RuntimeKernel service boundary.
/// The guest supplies only scalar register values; the kernel owns request signing.
/// </summary>
public static class HybridCpuExternalServiceEcallContractV1
{
    public const string SchemaId = "hybridcpu.external-service-ecall/v1";
    public const ulong EcallNumber = 0x4843_5356; // "HCSV"
    public const int EcallNumberRegister = 17;
    public const int ServiceRegister = 10;
    public const int OperationRegister = 11;
    public const int BufferAddressRegister = 12;
    public const int BufferLengthRegister = 13;
    public const int BufferAccessRegister = 14;
    public const int ArgumentCountRegister = 15;
    public const int FirstArgumentRegister = 16;
    public const int SecondArgumentRegister = 18;
    public const int ThirdArgumentRegister = 19;
    public const int MaximumRegisterArguments = 3;
    public const int ResultValueRegister = 10;
    public const int ResultStatusRegister = 11;
    public const int ResultErrorRegister = 12;

    public static string ContractDigest { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('|', SchemaId, EcallNumber, EcallNumberRegister, ServiceRegister, OperationRegister,
            BufferAddressRegister, BufferLengthRegister, BufferAccessRegister, ArgumentCountRegister,
            FirstArgumentRegister, SecondArgumentRegister, ThirdArgumentRegister,
            MaximumRegisterArguments, ResultValueRegister, ResultStatusRegister,
            ResultErrorRegister, "privilege=user", "request-digest=kernel-owned", "unknown=fail-closed"))))
        .ToLowerInvariant();
}

public sealed record HybridCpuExternalServiceEcallV1(
    ulong EcallNumber,
    ulong Service,
    ulong Operation,
    ulong BufferAddress,
    ulong BufferLength,
    ulong BufferAccess,
    ulong ArgumentCount,
    ulong Argument0 = 0);

public sealed record HybridCpuExternalServiceEcallResultV1(
    ulong ReturnValue,
    HybridCpuExternalServiceStatusV1 Status,
    int ErrorCode,
    string ResultDigest,
    HybridCpuExternalServiceEcallDispositionV1 Disposition);

public enum HybridCpuExternalServiceEcallDispositionV1 : byte
{
    Resume = 0,
    Parked = 1
}
