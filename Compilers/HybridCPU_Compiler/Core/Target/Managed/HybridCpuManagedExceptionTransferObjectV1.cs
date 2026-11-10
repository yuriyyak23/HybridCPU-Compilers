using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>
/// Emits the image-linkable, non-returning native tail of managed exception dispatch.
/// The dispatcher supplies one already validated HCET record in x10. This object owns no
/// metadata lookup or host execution authority; it only restores the exact architectural
/// handler state and jumps without a return link.
/// </summary>
public static class HybridCpuManagedExceptionTransferObjectV1
{
    public const string Symbol = "__hybridcpu_managed_exception_transfer";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.exception-transfer/v1";

    public static HybridCpuObjectArtifactV1 Emit()
    {
        byte[] code = HybridCpuManagedExceptionTransferEmitterV1.Emit();
        if (code.Length == 0 || code.Length % HybridCpuBundleSerializer.BundleSizeBytes != 0)
            throw new InvalidOperationException("Managed exception transfer code is not whole-bundle aligned.");
        return new HybridCpuObjectWriterV1().Write(new(
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes,
                code, (ulong)code.Length)],
            [new(Symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default,
                ".text", 0, (ulong)code.Length, IsDefinition: true)],
            [], HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
    }
}
