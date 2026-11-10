using System;
using System.Collections.Generic;
using System.Numerics;

namespace HybridCPU.Compiler.Core.IR
{
    internal readonly record struct HybridCpuBackendPlacementTieBreakContext(
        bool UseCertificateAwareCoalescingTieBreaks,
        bool TreatAsCoordinatorPath,
        int VirtualThreadId,
        double RegisterGroupPressure)
    {
        private const double CertificateAwareCoalescingPressureThreshold = 0.20;

        public bool PreferLowerCoalescingFootprint =>
            UseCertificateAwareCoalescingTieBreaks &&
            !TreatAsCoordinatorPath &&
            VirtualThreadId > 0 &&
            RegisterGroupPressure > CertificateAwareCoalescingPressureThreshold;
    }
}
