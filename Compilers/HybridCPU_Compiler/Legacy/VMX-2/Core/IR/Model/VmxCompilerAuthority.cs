using System;
using System.Collections.Generic;
using System.Linq;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core.Vmcs.V2;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Compiler-facing VMX authority categories. These are diagnostics and
/// preflight facts only; runtime legality remains owned by the VMX runtime.
/// </summary>
public enum CompilerVmxAuthorityKind : byte
{
    RuntimeExecutable = 0,
    CompilerEmittable = 1,
    DescriptorCarrierOnly = 2,
    RootRuntimeApiOnly = 3,
    GuestVmreadVmwriteVisible = 4,
    GuestVmPolicyVisible = 5,
    HostEvidenceOnly = 6,
    ReservedOrProhibited = 7,
}

public readonly record struct CompilerVmxOpcodeAuthority(
    InstructionsEnum Opcode,
    ushort OpcodeValue,
    string Mnemonic,
    CompilerVmxAuthorityKind Authority,
    bool RuntimeExecutable,
    bool CompilerHelperEmittable,
    bool RawTransportOnly,
    bool RootRuntimePolicyGated);

public readonly record struct CompilerVmcsV2FieldAuthority(
    ushort FieldId,
    string Name,
    VmcsV2BlockKind Block,
    VmcsV2FieldValueType ValueType,
    CompilerVmxAuthorityKind Authority,
    bool IsVmReadVisible,
    bool IsVmWriteVisible,
    bool CanUseForValidationDiagnostics,
    bool CanAttachToExecutableCompilerInstruction,
    bool ContainsHostEvidence);

public readonly record struct CompilerVmcsV2DescriptorSideband(
    ushort VmcsV2Revision,
    ushort FieldId,
    string Name,
    VmcsV2BlockKind Block,
    VmcsV2FieldValueType ValueType,
    CompilerVmxAuthorityKind Authority,
    bool ValidationOnly)
{
    public bool CanAttachToExecutableCompilerInstruction => false;
}

[Flags]
public enum CompilerVmxRequestedFeature : ulong
{
    None = 0,
    NestedVmx = 1UL << 0,
    VectorStreamRestore = 1UL << 1,
    Lane6Restore = 1UL << 2,
    Lane7Restore = 1UL << 3,
    Migration = 1UL << 4,
    DirtyLogging = 1UL << 5,
    ObservabilityExport = 1UL << 6,
}

[Flags]
public enum CompilerVmxTargetCapability : ulong
{
    None = 0,
    VmcsV2Revision1 = 1UL << 0,
    NestedVmx = 1UL << 1,
    VectorStreamRestore = 1UL << 2,
    Lane6Restore = 1UL << 3,
    Lane7Restore = 1UL << 4,
    Migration = 1UL << 5,
    DirtyLogging = 1UL << 6,
    ObservabilityExport = 1UL << 7,
}

public enum CompilerVmxPreflightRejectionKind : byte
{
    None = 0,
    UnsupportedVmcsV2Revision = 1,
    UnsupportedTargetCapability = 2,
    RootPolicyDisabled = 3,
}

public readonly record struct CompilerVmxRootPolicy(
    bool NestedVmxEnabled,
    bool VectorStreamRestoreEnabled,
    bool Lane6RestoreEnabled,
    bool Lane7RestoreEnabled,
    bool MigrationEnabled,
    bool DirtyLoggingEnabled,
    bool ObservabilityExportEnabled)
{
    public static CompilerVmxRootPolicy Disabled { get; } = new(
        NestedVmxEnabled: false,
        VectorStreamRestoreEnabled: false,
        Lane6RestoreEnabled: false,
        Lane7RestoreEnabled: false,
        MigrationEnabled: false,
        DirtyLoggingEnabled: false,
        ObservabilityExportEnabled: false);
}

public readonly record struct CompilerVmxPreflightRequest(
    ushort RequiredVmcsV2Revision,
    CompilerVmxRequestedFeature RequestedFeatures);

public readonly record struct CompilerVmxPreflightResult(
    bool Succeeded,
    CompilerVmxPreflightRejectionKind RejectionKind,
    CompilerVmxRequestedFeature RejectedFeature,
    string Diagnostic)
{
    public static CompilerVmxPreflightResult ProjectionPreflightPassed { get; } =
        new(true, CompilerVmxPreflightRejectionKind.None, CompilerVmxRequestedFeature.None, string.Empty);

    [Obsolete(
        "Compiler VMX preflight success is projection/preflight evidence only; use ProjectionPreflightPassed.",
        false)]
    public static CompilerVmxPreflightResult Success => ProjectionPreflightPassed;
}

public static class CompilerVmxAuthority
{
    private static readonly InstructionsEnum[] RuntimeVmxOpcodes =
    [
        InstructionsEnum.VMXON,
        InstructionsEnum.VMXOFF,
        InstructionsEnum.VMLAUNCH,
        InstructionsEnum.VMRESUME,
        InstructionsEnum.VMREAD,
        InstructionsEnum.VMWRITE,
        InstructionsEnum.VMCLEAR,
        InstructionsEnum.VMPTRLD,
        InstructionsEnum.VMPTRST,
        InstructionsEnum.VMCALL,
        InstructionsEnum.INVEPT,
        InstructionsEnum.INVVPID,
        InstructionsEnum.VMFUNC,
        InstructionsEnum.VMSAVEX,
        InstructionsEnum.VMRESTX,
    ];

    private static readonly IReadOnlyList<CompilerVmxOpcodeAuthority> RuntimeOpcodeAuthority =
        RuntimeVmxOpcodes
            .Select(CreateRuntimeOpcodeAuthority)
            .ToArray();

    public static IReadOnlyList<CompilerVmxOpcodeAuthority> GetRuntimeOpcodeAuthority() =>
        RuntimeOpcodeAuthority;

    public static bool TryGetOpcodeAuthority(
        InstructionsEnum opcode,
        out CompilerVmxOpcodeAuthority authority)
    {
        foreach (CompilerVmxOpcodeAuthority candidate in RuntimeOpcodeAuthority)
        {
            if (candidate.Opcode == opcode)
            {
                authority = candidate;
                return true;
            }
        }

        authority = default;
        return false;
    }

    public static CompilerVmcsV2FieldAuthority ClassifyVmcsV2Field(
        VmcsV2FieldDescriptor descriptor)
    {
        CompilerVmxAuthorityKind authority = ClassifyVmcsAuthority(descriptor);
        bool containsHostEvidence =
            descriptor.AccessPolicy.HostRuntimeEvidence &&
            authority != CompilerVmxAuthorityKind.RootRuntimeApiOnly;

        return new CompilerVmcsV2FieldAuthority(
            descriptor.FieldId,
            descriptor.Name,
            descriptor.Block,
            descriptor.ValueType,
            authority,
            descriptor.IsVmReadVisible,
            descriptor.IsVmWriteVisible,
            CanUseForValidationDiagnostics: true,
            CanAttachToExecutableCompilerInstruction: false,
            ContainsHostEvidence: containsHostEvidence);
    }

    public static bool TryCreateVmcsV2ValidationSideband(
        ushort vmcsV2Revision,
        VmcsV2FieldDescriptor descriptor,
        out CompilerVmcsV2DescriptorSideband sideband,
        out string diagnostic)
    {
        if (vmcsV2Revision == 0)
        {
            sideband = default;
            diagnostic = "VMCSv2 compiler descriptor sideband requires an explicit non-zero VMCSv2 revision.";
            return false;
        }

        CompilerVmcsV2FieldAuthority authority = ClassifyVmcsV2Field(descriptor);
        sideband = new CompilerVmcsV2DescriptorSideband(
            vmcsV2Revision,
            descriptor.FieldId,
            descriptor.Name,
            descriptor.Block,
            descriptor.ValueType,
            authority.Authority,
            ValidationOnly: true);
        diagnostic = string.Empty;
        return true;
    }

    public static bool CanAttachVmcsV2SidebandToOpcode(
        InstructionsEnum opcode,
        CompilerVmcsV2DescriptorSideband sideband,
        out string diagnostic)
    {
        _ = opcode;
        _ = sideband;
        diagnostic =
            "VMCSv2 compiler sidebands are validation-only and cannot be attached to executable compiler instructions.";
        return false;
    }

    public static CompilerVmxPreflightResult EvaluatePreflight(
        CompilerVmxPreflightRequest request,
        CompilerVmxTargetCapability targetCapabilities,
        CompilerVmxRootPolicy rootPolicy)
    {
        if (request.RequiredVmcsV2Revision > 1 ||
            (request.RequiredVmcsV2Revision == 1 &&
             !targetCapabilities.HasFlag(CompilerVmxTargetCapability.VmcsV2Revision1)))
        {
            return new CompilerVmxPreflightResult(
                false,
                CompilerVmxPreflightRejectionKind.UnsupportedVmcsV2Revision,
                CompilerVmxRequestedFeature.None,
                "Target does not publish the requested VMCSv2 revision.");
        }

        foreach (CompilerVmxRequestedFeature feature in EnumerateRequestedFeatures(request.RequestedFeatures))
        {
            if (!TargetSupports(feature, targetCapabilities))
            {
                return new CompilerVmxPreflightResult(
                    false,
                    CompilerVmxPreflightRejectionKind.UnsupportedTargetCapability,
                    feature,
                    $"Target does not publish VMX capability {feature}.");
            }

            if (!RootPolicyEnables(feature, rootPolicy))
            {
                return new CompilerVmxPreflightResult(
                    false,
                    CompilerVmxPreflightRejectionKind.RootPolicyDisabled,
                    feature,
                    $"Root runtime policy has not enabled VMX feature {feature}.");
            }
        }

        return CompilerVmxPreflightResult.ProjectionPreflightPassed;
    }

    private static CompilerVmxOpcodeAuthority CreateRuntimeOpcodeAuthority(
        InstructionsEnum opcode)
    {
        YAKSys_Hybrid_CPU.Arch.OpcodeInfo? info = OpcodeRegistry.GetInfo((uint)opcode);
        if (!info.HasValue ||
            info.Value.InstructionClass != YAKSys_Hybrid_CPU.Arch.InstructionClass.Vmx ||
            info.Value.SerializationClass != YAKSys_Hybrid_CPU.Arch.SerializationClass.VmxSerial)
        {
            return new CompilerVmxOpcodeAuthority(
                opcode,
                (ushort)opcode,
                opcode.ToString(),
                CompilerVmxAuthorityKind.ReservedOrProhibited,
                RuntimeExecutable: false,
                CompilerHelperEmittable: false,
                RawTransportOnly: false,
                RootRuntimePolicyGated: true);
        }

        return new CompilerVmxOpcodeAuthority(
            opcode,
            (ushort)opcode,
            info.Value.Mnemonic,
            CompilerVmxAuthorityKind.RuntimeExecutable,
            RuntimeExecutable: true,
            CompilerHelperEmittable: false,
            RawTransportOnly: true,
            RootRuntimePolicyGated: true);
    }

    private static CompilerVmxAuthorityKind ClassifyVmcsAuthority(
        VmcsV2FieldDescriptor descriptor)
    {
        if (descriptor.Block is VmcsV2BlockKind.DirtyLog or VmcsV2BlockKind.DebugTrace)
        {
            return CompilerVmxAuthorityKind.RootRuntimeApiOnly;
        }

        if (descriptor.AccessPolicy.HostRuntimeEvidence ||
            descriptor.ValidationPolicy.Kind == VmcsV2ValidationPolicyKind.HostEvidenceForbidden)
        {
            return CompilerVmxAuthorityKind.HostEvidenceOnly;
        }

        if (descriptor.AccessPolicy.GuestVisible &&
            (descriptor.IsVmReadVisible || descriptor.IsVmWriteVisible))
        {
            return CompilerVmxAuthorityKind.GuestVmreadVmwriteVisible;
        }

        if (descriptor.MigrationPolicy.Kind == VmcsV2MigrationPolicyKind.MigratableGuestState)
        {
            return CompilerVmxAuthorityKind.GuestVmPolicyVisible;
        }

        return CompilerVmxAuthorityKind.RootRuntimeApiOnly;
    }

    private static IEnumerable<CompilerVmxRequestedFeature> EnumerateRequestedFeatures(
        CompilerVmxRequestedFeature features)
    {
        foreach (CompilerVmxRequestedFeature feature in Enum.GetValues<CompilerVmxRequestedFeature>())
        {
            if (feature != CompilerVmxRequestedFeature.None &&
                features.HasFlag(feature))
            {
                yield return feature;
            }
        }
    }

    private static bool TargetSupports(
        CompilerVmxRequestedFeature feature,
        CompilerVmxTargetCapability capabilities) =>
        feature switch
        {
            CompilerVmxRequestedFeature.NestedVmx =>
                capabilities.HasFlag(CompilerVmxTargetCapability.NestedVmx),
            CompilerVmxRequestedFeature.VectorStreamRestore =>
                capabilities.HasFlag(CompilerVmxTargetCapability.VectorStreamRestore),
            CompilerVmxRequestedFeature.Lane6Restore =>
                capabilities.HasFlag(CompilerVmxTargetCapability.Lane6Restore),
            CompilerVmxRequestedFeature.Lane7Restore =>
                capabilities.HasFlag(CompilerVmxTargetCapability.Lane7Restore),
            CompilerVmxRequestedFeature.Migration =>
                capabilities.HasFlag(CompilerVmxTargetCapability.Migration),
            CompilerVmxRequestedFeature.DirtyLogging =>
                capabilities.HasFlag(CompilerVmxTargetCapability.DirtyLogging),
            CompilerVmxRequestedFeature.ObservabilityExport =>
                capabilities.HasFlag(CompilerVmxTargetCapability.ObservabilityExport),
            _ => false,
        };

    private static bool RootPolicyEnables(
        CompilerVmxRequestedFeature feature,
        CompilerVmxRootPolicy policy) =>
        feature switch
        {
            CompilerVmxRequestedFeature.NestedVmx => policy.NestedVmxEnabled,
            CompilerVmxRequestedFeature.VectorStreamRestore => policy.VectorStreamRestoreEnabled,
            CompilerVmxRequestedFeature.Lane6Restore => policy.Lane6RestoreEnabled,
            CompilerVmxRequestedFeature.Lane7Restore => policy.Lane7RestoreEnabled,
            CompilerVmxRequestedFeature.Migration => policy.MigrationEnabled,
            CompilerVmxRequestedFeature.DirtyLogging => policy.DirtyLoggingEnabled,
            CompilerVmxRequestedFeature.ObservabilityExport => policy.ObservabilityExportEnabled,
            _ => false,
        };
}
