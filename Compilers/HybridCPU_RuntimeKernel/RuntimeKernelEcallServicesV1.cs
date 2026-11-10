using HybridCPU.Platform.Contracts;

namespace HybridCPU.RuntimeKernel;

public sealed partial class DeterministicRuntimeKernelV1
{
    /// <summary>
    /// Trusted ECALL gateway. It converts the fixed register envelope into a signed kernel
    /// request; native user code never supplies or bypasses the transition digest.
    /// </summary>
    public HybridCpuExternalServiceEcallResultV1 ExternalServiceEcallTransition(
        ulong contextId,
        HybridCpuPrivilegeModeV1 privilegeMode,
        HybridCpuExternalServiceEcallV1 ecall)
    {
        ArgumentNullException.ThrowIfNull(ecall);
        if (ecall.EcallNumber != HybridCpuExternalServiceEcallContractV1.EcallNumber ||
            privilegeMode != HybridCpuPrivilegeModeV1.User ||
            ecall.ArgumentCount > HybridCpuExternalServiceEcallContractV1.MaximumRegisterArguments ||
            ecall.Service > byte.MaxValue || ecall.BufferAccess > byte.MaxValue)
            return InvalidEcall("The ECALL identity, privilege or register envelope is invalid.");

        HybridCpuHostServiceV1 service = (HybridCpuHostServiceV1)ecall.Service;
        HybridCpuHostBufferAccessV1 access = (HybridCpuHostBufferAccessV1)ecall.BufferAccess;
        ulong[] arguments = ecall.ArgumentCount == 0 ? [] : [ecall.Argument0];
        HybridCpuExternalServiceRequestV1 unsigned = new HybridCpuExternalServiceRequestV1(contextId, service, ecall.Operation,
            privilegeMode, ecall.BufferAddress, ecall.BufferLength, access, arguments, string.Empty);
        HybridCpuExternalServiceRequestV1 signed = unsigned with
        {
            TransitionDigest = HybridCpuManagedInteropContractV1.ComputeTransitionDigest(unsigned)
        };
        HybridCpuExternalServiceResultV1 result = ExternalServiceTransition(signed);
        HybridCpuExternalServiceEcallDispositionV1 disposition = Contexts().SingleOrDefault(
                context => context.Descriptor.ContextId == contextId)?.State == HybridCpuExecutionContextStateV1.Parked
            ? HybridCpuExternalServiceEcallDispositionV1.Parked
            : HybridCpuExternalServiceEcallDispositionV1.Resume;
        return new(result.ReturnValue, result.Status, result.ErrorCode, result.ResultDigest, disposition);
    }

    private static HybridCpuExternalServiceEcallResultV1 InvalidEcall(string reason)
    {
        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('|',
                HybridCpuExternalServiceEcallContractV1.ContractDigest,
                HybridCpuExternalServiceStatusV1.InvalidRequest, reason)))).ToLowerInvariant();
        return new(0, HybridCpuExternalServiceStatusV1.InvalidRequest, 0, digest,
            HybridCpuExternalServiceEcallDispositionV1.Resume);
    }
}
