using System;
using HybridCPU.Compiler.Core.IR.Authority;
using YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute;
using YAKSys_Hybrid_CPU.Core.Execution.ExternalAccelerators.Auth;

namespace HybridCPU.Compiler.Core.Threading
{
    public enum CompilerRuntimeGuardObservationKind
    {
        DmaStreamComputeOwnerGuard = 0,
        AcceleratorDescriptorOwnerGuard,
        AcceleratorSubmitGuard
    }

    public sealed record CompilerRuntimeGuardObservation(
        CompilerRuntimeGuardObservationKind Kind,
        string SourceApi,
        bool ObservedGuardAllowsProgress,
        CompilerCoreResultHeader Header,
        string RuntimeGuardMessage,
        string AuthoritySemantics)
    {
        public static CompilerRuntimeGuardObservation FromDmaStreamComputeOwnerGuard(
            DmaStreamComputeOwnerGuardDecision guardDecision,
            string sourceApi,
            string authoritySemantics) =>
            Create(
                CompilerRuntimeGuardObservationKind.DmaStreamComputeOwnerGuard,
                sourceApi,
                guardDecision.IsAllowed,
                guardDecision.Message,
                authoritySemantics);

        public static CompilerRuntimeGuardObservation FromAcceleratorDescriptorOwnerGuard(
            AcceleratorGuardDecision guardDecision,
            string sourceApi,
            string authoritySemantics) =>
            Create(
                CompilerRuntimeGuardObservationKind.AcceleratorDescriptorOwnerGuard,
                sourceApi,
                guardDecision.IsAllowed,
                guardDecision.Message,
                authoritySemantics);

        public static CompilerRuntimeGuardObservation FromAcceleratorSubmitGuard(
            AcceleratorGuardDecision guardDecision,
            string sourceApi,
            string authoritySemantics) =>
            Create(
                CompilerRuntimeGuardObservationKind.AcceleratorSubmitGuard,
                sourceApi,
                guardDecision.IsAllowed,
                guardDecision.Message,
                authoritySemantics);

        private static CompilerRuntimeGuardObservation Create(
            CompilerRuntimeGuardObservationKind kind,
            string sourceApi,
            bool observedGuardAllowsProgress,
            string runtimeGuardMessage,
            string authoritySemantics)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceApi);
            ArgumentException.ThrowIfNullOrWhiteSpace(authoritySemantics);

            return new CompilerRuntimeGuardObservation(
                kind,
                sourceApi,
                observedGuardAllowsProgress,
                new CompilerCoreResultHeader(
                    CompilerAuthorityClass.CompilerEvidenceProduction,
                    CompilerAuthoritySourceKind.RuntimeOwnedPolicyReference,
                    CompilerEvidenceClass.RuntimeContractObservationEvidence,
                    CompilerPublicationClass.EvidenceOnly,
                    CompilerExecutionClaim.NoExecutionClaim,
                    CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
                    CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired |
                    CompilerRuntimeAuthorityDependency.RuntimeExecutionRequired |
                    CompilerRuntimeAuthorityDependency.RuntimeCommitRequired |
                    CompilerRuntimeAuthorityDependency.RuntimeRetireRequired |
                    CompilerRuntimeAuthorityDependency.RuntimePublicationRequired),
                runtimeGuardMessage,
                authoritySemantics);
        }
    }
}
