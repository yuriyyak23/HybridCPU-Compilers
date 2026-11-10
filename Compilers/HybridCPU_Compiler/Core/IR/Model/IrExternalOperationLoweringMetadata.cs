using System;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Version of the compiler-owned semantic external-operation sidecar.
/// This version is independent from both the CPU ABI and any transport revision.
/// </summary>
public static class IrExternalOperationSemanticContract
{
    public const int Version = 2;

    public static bool IsSupported(int version) => version == Version;

    public static void ThrowIfUnsupported(int version, string consumerSurface)
    {
        if (!IsSupported(version))
            throw new NotSupportedException($"{consumerSurface} does not support external-operation semantic contract version {version}.");
    }
}

/// <summary>
/// Opaque, compiler-stable correlation of a semantic sidecar to an existing
/// descriptor carrier. It is not a memory address, provider identity, or
/// authority to submit an external operation.
/// </summary>
public readonly record struct IrExternalOperationDescriptorIdentity(ulong Value)
{
    public bool IsValid => Value != 0;

    public static IrExternalOperationDescriptorIdentity Require(ulong value, string parameterName)
    {
        if (value == 0)
            throw new ArgumentOutOfRangeException(parameterName, value, "External-operation descriptor identity must be non-zero.");
        return new IrExternalOperationDescriptorIdentity(value);
    }
}

/// <summary>
/// Immutable source-indexed lowering record. It describes compiler semantics
/// for a pre-existing lane carrier only; provider availability, admission,
/// coherence, completion, visibility, publication and release stay runtime-owned.
/// </summary>
public sealed record IrExternalOperationLoweringMetadata
{
    public required int SemanticContractVersion { get; init; }
    public required int SourceInstructionIndex { get; init; }
    public required IrExternalOperationDescriptorIdentity DescriptorIdentity { get; init; }
    public required IrExternalOperationIntent Intent { get; init; }
    public IrExternalOperationFallbackPolicy FallbackPolicy =>
        IrExternalOperationFallbackPolicy.From(Intent);

    public void Validate()
    {
        IrExternalOperationSemanticContract.ThrowIfUnsupported(
            SemanticContractVersion,
            nameof(IrExternalOperationLoweringMetadata));
        if (SourceInstructionIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(SourceInstructionIndex));
        if (!DescriptorIdentity.IsValid)
            throw new InvalidOperationException("External-operation lowering metadata requires a non-zero descriptor identity.");
        ArgumentNullException.ThrowIfNull(Intent);
    }
}
