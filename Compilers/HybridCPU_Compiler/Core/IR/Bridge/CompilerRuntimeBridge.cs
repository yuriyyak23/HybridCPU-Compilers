using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR.Artifacts;
using HybridCPU.Compiler.Core.IR.Authority;

namespace HybridCPU.Compiler.Core.IR.Bridge;

public enum TypedSlotFactStaging
{
    MissingCompatibility = 0,
    PresentUnvalidated,
    PresentValidated,
    PresentQuarantined,
    RejectedByRuntimeBridge,
    FutureRequiredForAdmission
}

public enum BridgeIngressStatus
{
    Unknown = 0,
    BridgeIngressAccepted,
    BridgeIngressRejected,
    VersionRejected,
    AgreementFailure,
    SidebandRejected,
    DescriptorRejected,
    TypedSlotFactsRejected,
    Quarantined,
    CompatibilityAcceptedMissingFacts,
    CompatibilityRecordedWithoutValidation
}

public sealed record BridgeAcceptanceReport(
    BridgeIngressStatus Status,
    bool RuntimeLegalityAStillRequired,
    bool RuntimeLegalityBStillRequired,
    bool RuntimeCommitStillRequired,
    bool RuntimeRetireStillRequired,
    bool RuntimePublicationStillRequired,
    string Reason);

public sealed record CompilerContractView(
    int ProducerCompilerContractVersion,
    int RuntimeContractVersionObserved,
    IrTypedSlotPolicyMode RuntimePolicyModeObserved,
    int SupportedBundleWidth,
    int SupportedBundleSizeBytes,
    int SupportedSidebandEnvelopeVersion,
    IReadOnlyList<string> SupportedDescriptorAbiVersions,
    IReadOnlyList<string> KnownFutureGatedContours);

public interface ICompilerRuntimeBridge
{
    BridgeAcceptanceReport DeclareCompilerContractVersion(CompilerContractView contract);

    BridgeAcceptanceReport AcceptSideband(CompilerSidebandEnvelope sideband);

    BridgeAcceptanceReport AcceptDescriptor(DescriptorEnvelope descriptor);

    BridgeAcceptanceReport AcceptTypedSlotFacts(TypedSlotFactsEnvelope facts);

    BridgeAcceptanceReport AcceptEmissionPackage(CompilerEmissionPackage package);
}

public sealed class CompilerRuntimeBridge : ICompilerRuntimeBridge
{
    public static CompilerRuntimeBridge Instance { get; } = new();

    private CompilerRuntimeBridge()
    {
    }

    public BridgeAcceptanceReport DeclareCompilerContractVersion(CompilerContractView contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.ProducerCompilerContractVersion != contract.RuntimeContractVersionObserved)
        {
            return Rejected(
                BridgeIngressStatus.VersionRejected,
                "Compiler contract version mismatch rejects bridge ingress before runtime authority.");
        }

        return Accepted("Compiler contract version is compatible; runtime legality remains runtime-owned.");
    }

    public BridgeAcceptanceReport AcceptSideband(CompilerSidebandEnvelope sideband)
    {
        ArgumentNullException.ThrowIfNull(sideband);
        if (sideband.Requirement == SidebandRequirement.Forbidden &&
            sideband.BundleAnnotations.Count != 0)
        {
            return Rejected(
                BridgeIngressStatus.SidebandRejected,
                "Sideband is present while sideband requirement is forbidden.");
        }

        return Accepted("Sideband ingress accepted as transport evidence only.");
    }

    public BridgeAcceptanceReport AcceptDescriptor(DescriptorEnvelope descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Status == DescriptorAbiStatus.RejectedDescriptor)
        {
            return Rejected(
                BridgeIngressStatus.DescriptorRejected,
                "Descriptor ABI rejected before runtime legality.");
        }

        return Accepted("Descriptor ingress accepted as ABI evidence only.");
    }

    public BridgeAcceptanceReport AcceptTypedSlotFacts(TypedSlotFactsEnvelope facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return facts.Staging switch
        {
            TypedSlotFactStaging.MissingCompatibility => new BridgeAcceptanceReport(
                BridgeIngressStatus.CompatibilityAcceptedMissingFacts,
                RuntimeLegalityAStillRequired: true,
                RuntimeLegalityBStillRequired: true,
                RuntimeCommitStillRequired: true,
                RuntimeRetireStillRequired: true,
                RuntimePublicationStillRequired: true,
                "Missing typed-slot facts are compatibility-only and weaker than validated facts."),

            TypedSlotFactStaging.PresentValidated => Accepted(
                "Validated typed-slot facts are structural evidence only; runtime Stage A/B still required."),

            TypedSlotFactStaging.PresentUnvalidated => new BridgeAcceptanceReport(
                BridgeIngressStatus.CompatibilityRecordedWithoutValidation,
                RuntimeLegalityAStillRequired: true,
                RuntimeLegalityBStillRequired: true,
                RuntimeCommitStillRequired: true,
                RuntimeRetireStillRequired: true,
                RuntimePublicationStillRequired: true,
                "Present facts recorded without validation are not runtime legality."),

            TypedSlotFactStaging.PresentQuarantined => Rejected(
                BridgeIngressStatus.Quarantined,
                "Typed-slot facts are quarantined; bridge ingress is not runtime legality."),

            TypedSlotFactStaging.RejectedByRuntimeBridge => Rejected(
                BridgeIngressStatus.TypedSlotFactsRejected,
                "Typed-slot facts rejected by bridge before runtime legality."),

            TypedSlotFactStaging.FutureRequiredForAdmission => Rejected(
                BridgeIngressStatus.BridgeIngressRejected,
                "Required-for-admission is a future runtime-owned policy seam, not compiler-owned policy."),

            _ => Rejected(
                BridgeIngressStatus.BridgeIngressRejected,
                "Unknown typed-slot fact staging fails closed.")
        };
    }

    public BridgeAcceptanceReport AcceptEmissionPackage(CompilerEmissionPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        BridgeAcceptanceReport contractReport = DeclareCompilerContractVersion(
            new CompilerContractView(
                package.Identity.CompilerContractVersion,
                package.RuntimeBridgeInput?.RuntimeContractVersionObservedAtBuild ?? HybridCpuCompilerContract.Version,
                package.RuntimeBridgeInput?.RuntimePolicyModeObserved ?? IrTypedSlotPolicyMode.CompatibilityValidation,
                SupportedBundleWidth: package.Carrier?.Image.BundleWidth ?? 0,
                SupportedBundleSizeBytes: package.Carrier?.Image.BundleSizeBytes ?? 0,
                SupportedSidebandEnvelopeVersion: 1,
                SupportedDescriptorAbiVersions: ["compat-v1"],
                KnownFutureGatedContours: Array.Empty<string>()));

        if (contractReport.Status == BridgeIngressStatus.VersionRejected)
        {
            return contractReport;
        }

        if (package.TypedSlotFacts is { } facts)
        {
            BridgeAcceptanceReport factsReport = AcceptTypedSlotFacts(facts);
            if (factsReport.Status is BridgeIngressStatus.TypedSlotFactsRejected
                or BridgeIngressStatus.BridgeIngressRejected
                or BridgeIngressStatus.Quarantined)
            {
                return factsReport;
            }
        }

        return Accepted("Emission package ingress accepted for runtime consideration; execution remains runtime-owned.");
    }

    private static BridgeAcceptanceReport Accepted(string reason) =>
        new(
            BridgeIngressStatus.BridgeIngressAccepted,
            RuntimeLegalityAStillRequired: true,
            RuntimeLegalityBStillRequired: true,
            RuntimeCommitStillRequired: true,
            RuntimeRetireStillRequired: true,
            RuntimePublicationStillRequired: true,
            reason);

    private static BridgeAcceptanceReport Rejected(BridgeIngressStatus status, string reason) =>
        new(
            status,
            RuntimeLegalityAStillRequired: true,
            RuntimeLegalityBStillRequired: true,
            RuntimeCommitStillRequired: true,
            RuntimeRetireStillRequired: true,
            RuntimePublicationStillRequired: true,
            reason);
}
