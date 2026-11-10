using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR.Telemetry.Contracts;

public sealed record WorkerPerformanceMetrics(
    string WorkerName,
    long TotalCycles,
    double NopDensity,
    double RejectRate,
    long BundlesExecuted,
    long NopsExecuted);

public sealed record CertificatePressureMetrics(
    IReadOnlyDictionary<IrSlotClass, long>? RejectsPerClass,
    IReadOnlyDictionary<int, long>? RegisterGroupConflictsPerVt);

public sealed record LoopPhaseClassProfile(
    ulong LoopPcAddress,
    int IterationsSampled,
    double AluFreeVariance,
    double LsuFreeVariance,
    double DmaStreamFreeVariance,
    double BranchControlFreeVariance,
    double SystemSingletonFreeVariance,
    double OverallClassVariance,
    double TemplateReuseRate);

/// <summary>
/// Compiler-owned, authority-free projection of offline scheduling telemetry.
/// Optional runtime-only telemetry payloads are deliberately not represented.
/// </summary>
public sealed record TypedSlotTelemetryProfile(
    string ProgramHash,
    IReadOnlyDictionary<IrSlotClass, long> TotalInjectionsPerClass,
    IReadOnlyDictionary<IrSlotClass, long> TotalRejectsPerClass,
    IReadOnlyDictionary<IrTypedSlotRejectReason, long> RejectsByReason,
    double AverageNopDensity,
    double AverageBundleUtilization,
    long TotalBundlesExecuted,
    long TotalNopsExecuted,
    long ReplayTemplateHits,
    long ReplayTemplateMisses,
    double ReplayHitRate,
    long FairnessStarvationEvents,
    IReadOnlyDictionary<int, long> PerVtInjectionCounts,
    IReadOnlyDictionary<string, WorkerPerformanceMetrics>? WorkerMetrics)
{
    public CertificatePressureMetrics? CertificatePressure { get; init; }
    public IReadOnlyDictionary<int, long>? PerVtRejectionCounts { get; init; }
    public IReadOnlyDictionary<int, long>? PerVtRegGroupConflicts { get; init; }
    public IReadOnlyDictionary<IrSlotClass, long>? SmtLegalityRejectsPerClass { get; init; }
    public IReadOnlyDictionary<int, long>? BankPendingRejectsPerBank { get; init; }
    public long? MemoryClusteringEventCount { get; init; }
    public long? BankConflictStallCycles { get; init; }
    public long? HazardRegisterDataCount { get; init; }
    public long? HazardMemoryBankCount { get; init; }
    public long? HazardControlFlowCount { get; init; }
    public long? HazardSystemBarrierCount { get; init; }
    public long? HazardPinnedLaneCount { get; init; }
    public long? CrossDomainRejectCount { get; init; }
    public long? EligibilityMaskedCycles { get; init; }
    public long? EligibilityMaskedReadyCandidates { get; init; }
    public byte? LastEligibilityRequestedMask { get; init; }
    public byte? LastEligibilityNormalizedMask { get; init; }
    public byte? LastEligibilityReadyPortMask { get; init; }
    public byte? LastEligibilityVisibleReadyMask { get; init; }
    public byte? LastEligibilityMaskedReadyMask { get; init; }
    public IReadOnlyList<LoopPhaseClassProfile>? LoopPhaseProfiles { get; init; }
}
