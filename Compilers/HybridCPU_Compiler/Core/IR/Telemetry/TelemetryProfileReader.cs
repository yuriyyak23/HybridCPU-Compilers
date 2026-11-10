using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace HybridCPU.Compiler.Core.IR.Telemetry;

/// <summary>
/// Reads and queries a <see cref="TypedSlotTelemetryProfile"/> for compiler heuristic tuning.
/// Handles missing, corrupt, and stale profiles gracefully by falling back to default heuristics.
/// </summary>
public sealed partial class TelemetryProfileReader
{
    private readonly TypedSlotTelemetryProfile? _profile;

    /// <summary>
    /// Whether a valid profile is loaded and matches the current program hash.
    /// </summary>
    public bool HasProfile => _profile is not null;

    /// <summary>
    /// The loaded profile, or <c>null</c> if none is available.
    /// </summary>
    public TypedSlotTelemetryProfile? Profile => _profile;

    private TelemetryProfileReader(TypedSlotTelemetryProfile? profile)
    {
        _profile = profile;
    }

    /// <summary>
    /// Creates a reader with no profile (default heuristics).
    /// </summary>
    public static TelemetryProfileReader CreateEmpty() => new(null);

    /// <summary>
    /// Creates a reader from an existing profile instance.
    /// </summary>
    /// <param name="profile">The profile to use.</param>
    /// <param name="expectedProgramHash">
    /// If non-null, the profile is ignored when its <see cref="TypedSlotTelemetryProfile.ProgramHash"/>
    /// does not match (staleness guard).
    /// </param>
    public static TelemetryProfileReader Create(TypedSlotTelemetryProfile profile, string? expectedProgramHash = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (expectedProgramHash is not null &&
            !string.Equals(profile.ProgramHash, expectedProgramHash, StringComparison.Ordinal))
        {
            // Hash mismatch → stale profile, fall back to defaults
            return CreateEmpty();
        }

        return new TelemetryProfileReader(profile);
    }

    /// <summary>
    /// Imports an outward telemetry DTO through its JSON data contract. The Core assembly
    /// deliberately does not reference the runtime diagnostics assembly that owns that DTO.
    /// Unknown or incompatible payloads conservatively disable profile-guided heuristics.
    /// </summary>
    public static TelemetryProfileReader Create(object profile, string? expectedProgramHash = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        try
        {
            string json = JsonSerializer.Serialize(profile, profile.GetType());
            TypedSlotTelemetryProfile? projection = JsonSerializer.Deserialize<TypedSlotTelemetryProfile>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return projection is null ? CreateEmpty() : Create(projection, expectedProgramHash);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return CreateEmpty();
        }
    }

    /// <summary>
    /// Loads a profile from a JSON file.
    /// Returns a reader with no profile on any error (missing file, corrupt JSON, hash mismatch).
    /// </summary>
    /// <param name="filePath">Path to the <c>.typed_slot_profile.json</c> file.</param>
    /// <param name="expectedProgramHash">
    /// If non-null, the profile is ignored when its hash does not match.
    /// </param>
    public static TelemetryProfileReader LoadFromFile(string filePath, string? expectedProgramHash = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return CreateEmpty();

        try
        {
            if (!File.Exists(filePath))
                return CreateEmpty();

            string json = File.ReadAllText(filePath);
            TypedSlotTelemetryProfile? profile = JsonSerializer.Deserialize<TypedSlotTelemetryProfile>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (profile is null)
                return CreateEmpty();

            return Create(profile, expectedProgramHash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Graceful fallback: file I/O or parse error → default heuristics
            return CreateEmpty();
        }
    }

    // ── Query API ────────────────────────────────────────────────

    /// <summary>
    /// Returns the class-pressure ratio (rejects / (rejects + injections)) for a <see cref="IrSlotClass"/>.
    /// Returns 0.0 when no profile is available or no data exists for the class.
    /// </summary>
    public double GetClassPressure(IrSlotClass slotClass)
    {
        if (_profile is null)
            return 0.0;

        long injections = _profile.TotalInjectionsPerClass.TryGetValue(slotClass, out long inj) ? inj : 0;
        long rejects = _profile.TotalRejectsPerClass.TryGetValue(slotClass, out long rej) ? rej : 0;
        double total = (double)injections + rejects;
        return total > 0 ? (double)rejects / total : 0.0;
    }

    /// <summary>
    /// Returns the certificate-pressure ratio for a <see cref="IrSlotClass"/>.
    /// Prefers the dedicated certificate payload when present and falls back to
    /// <see cref="TypedSlotTelemetryProfile.TotalRejectsPerClass"/> for legacy-compatible profiles.
    /// Returns 0.0 when no profile is available or no data exists for the class.
    /// </summary>
    public double GetCertificatePressureByClass(IrSlotClass slotClass)
    {
        if (_profile is null)
            return 0.0;

        long injections = _profile.TotalInjectionsPerClass.TryGetValue(slotClass, out long inj) ? inj : 0;
        long rejects = GetCertificateRejectCountByClass(slotClass);
        double total = (double)injections + rejects;
        return total > 0 ? (double)rejects / total : 0.0;
    }

    /// <summary>
    /// Returns the certificate reject count attributed to a <see cref="IrSlotClass"/>.
    /// Prefers the dedicated certificate payload when present and falls back to
    /// <see cref="TypedSlotTelemetryProfile.TotalRejectsPerClass"/> for legacy-compatible profiles.
    /// Returns 0 when no profile is available or no data exists for the class.
    /// </summary>
    public long GetCertificateRejectCountByClass(IrSlotClass slotClass)
    {
        if (_profile is null)
            return 0;

        if (_profile.CertificatePressure?.RejectsPerClass is { } rejectsPerClass &&
            rejectsPerClass.TryGetValue(slotClass, out long certificateRejects))
        {
            return certificateRejects;
        }

        return _profile.TotalRejectsPerClass.TryGetValue(slotClass, out long fallbackRejects) ? fallbackRejects : 0;
    }

    /// <summary>
    /// Returns the raw register-group certificate conflict count for a virtual thread.
    /// Returns 0 when no certificate payload is available or the VT has no recorded conflicts.
    /// </summary>
    public long GetCertificateRegisterGroupConflictCount(int virtualThreadId)
    {
        if (virtualThreadId < 0 || _profile?.CertificatePressure?.RegisterGroupConflictsPerVt is not { } conflictsPerVt)
            return 0;

        return conflictsPerVt.TryGetValue(virtualThreadId, out long conflicts) ? conflicts : 0;
    }

    /// <summary>
    /// Returns the normalized register-group certificate pressure share for a virtual thread.
    /// Computed as VT-specific register-group conflicts divided by the total register-group conflicts
    /// across all VTs in the certificate payload. Returns 0.0 when no payload or no conflict data exists.
    /// </summary>
    public double GetCertificateRegisterGroupPressureByVt(int virtualThreadId)
    {
        if (virtualThreadId < 0 || _profile?.CertificatePressure?.RegisterGroupConflictsPerVt is not { } conflictsPerVt)
            return 0.0;

        long totalConflicts = 0;
        foreach (long count in conflictsPerVt.Values)
            totalConflicts += count;

        if (totalConflicts <= 0)
            return 0.0;

        return conflictsPerVt.TryGetValue(virtualThreadId, out long conflicts)
            ? (double)conflicts / totalConflicts
            : 0.0;
    }

    /// <summary>
    /// Returns the per-VT injectability rate as injections / (injections + rejections).
    /// This is advisory-only compiler telemetry and must not be used for runtime fairness steering.
    /// Returns 0.0 when no profile is available, the VT is invalid, or the Phase 3 payload is absent.
    /// </summary>
    public double GetPerVtInjectabilityRate(int virtualThreadId)
    {
        if (virtualThreadId < 0 || _profile?.PerVtRejectionCounts is not { } rejectionCounts)
            return 0.0;

        long injections = _profile.PerVtInjectionCounts.TryGetValue(virtualThreadId, out long inj) ? inj : 0;
        long rejections = rejectionCounts.TryGetValue(virtualThreadId, out long rej) ? rej : 0;
        double total = (double)injections + rejections;
        return total > 0 ? (double)injections / total : 0.0;
    }

    /// <summary>
    /// Returns the per-VT injectability pressure as rejections / (injections + rejections).
    /// This remains advisory-only compiler telemetry and returns 0.0 when the VT is invalid,
    /// the Phase 3 payload is absent, or no samples were recorded.
    /// </summary>
    public double GetPerVtInjectabilityPressure(int virtualThreadId)
    {
        if (virtualThreadId < 0 || _profile?.PerVtRejectionCounts is not { } rejectionCounts)
            return 0.0;

        long injections = _profile.PerVtInjectionCounts.TryGetValue(virtualThreadId, out long inj) ? inj : 0;
        long rejections = rejectionCounts.TryGetValue(virtualThreadId, out long rej) ? rej : 0;
        double total = (double)injections + rejections;
        return total > 0 ? (double)rejections / total : 0.0;
    }

    /// <summary>
    /// Returns the advisory register-group pressure to use for backend shaping for one virtual thread.
    /// Coordinator-special paths always return 0.0 so VT0 is not treated as symmetric with workers.
    /// Missing certificate telemetry also returns 0.0.
    /// </summary>
    public double GetCertificateRegisterGroupPressureForBackendShaping(int virtualThreadId, bool treatAsCoordinatorPath)
    {
        if (treatAsCoordinatorPath || virtualThreadId <= 0)
            return 0.0;

        return GetCertificateRegisterGroupPressureByVt(virtualThreadId);
    }

    /// <summary>
    /// Returns a bounded advisory backend-shaping pressure signal for one virtual thread.
    /// Worker paths combine per-VT injectability pressure and register-group certificate pressure,
    /// while coordinator-special paths return 0.0 to preserve explicit coordinator-vs-worker boundaries.
    /// This signal is advisory-only and must never be treated as fairness policy.
    /// </summary>
    public double GetBackendResourceShapingPressure(int virtualThreadId, bool treatAsCoordinatorPath)
    {
        if (treatAsCoordinatorPath || virtualThreadId <= 0)
            return 0.0;

        double injectabilityPressure = GetPerVtInjectabilityPressure(virtualThreadId);
        double registerGroupPressure = GetCertificateRegisterGroupPressureForBackendShaping(virtualThreadId, treatAsCoordinatorPath: false);
        return Math.Max(injectabilityPressure, registerGroupPressure);
    }

    /// <summary>
    /// Returns the raw bank-pending reject count for a runtime bank ID.
    /// Returns 0 when no per-bank payload is available or the bank has no recorded rejects.
    /// </summary>
    public long GetBankPendingRejectCount(int bankId)
    {
        if (bankId < 0 || _profile?.BankPendingRejectsPerBank is not { } rejectsPerBank)
            return 0;

        return rejectsPerBank.TryGetValue(bankId, out long rejects) ? rejects : 0;
    }

    /// <summary>
    /// Returns the normalized share of bank-pending rejects attributed to a runtime bank ID.
    /// Returns 0.0 when no per-bank payload is available or no bank rejects were recorded.
    /// </summary>
    public double GetBankPendingRejectPressureByBank(int bankId)
    {
        if (bankId < 0 || _profile?.BankPendingRejectsPerBank is not { } rejectsPerBank)
            return 0.0;

        long totalRejects = 0;
        foreach (long count in rejectsPerBank.Values)
            totalRejects += count;

        if (totalRejects <= 0)
            return 0.0;

        return rejectsPerBank.TryGetValue(bankId, out long rejects)
            ? (double)rejects / totalRejects
            : 0.0;
    }

    /// <summary>
    /// Returns the normalized share carried by the hottest runtime bank in the pending-reject payload.
    /// This remains advisory-only compiler metadata and returns 0.0 when the payload is absent or empty.
    /// </summary>
    public double GetPeakBankPendingRejectPressure()
    {
        if (_profile?.BankPendingRejectsPerBank is not { } rejectsPerBank)
            return 0.0;

        long totalRejects = 0;
        long hottestBankRejects = 0;
        foreach (long count in rejectsPerBank.Values)
        {
            totalRejects += count;
            hottestBankRejects = Math.Max(hottestBankRejects, count);
        }

        return totalRejects > 0
            ? (double)hottestBankRejects / totalRejects
            : 0.0;
    }

    /// <summary>
    /// Returns the decoded-bundle memory clustering event count, or 0 when unavailable.
    /// </summary>
    public long GetMemoryClusteringEventCount()
    {
        return _profile?.MemoryClusteringEventCount ?? 0;
    }

    /// <summary>
    /// Returns the decoded-bundle memory clustering rate as events / total bundles executed.
    /// Returns 0.0 when no profile or no bundle count is available.
    /// </summary>
    public double GetMemoryClusteringEventRate()
    {
        if (_profile is null)
            return 0.0;

        long totalBundles = _profile.TotalBundlesExecuted;
        long clusteringEvents = _profile.MemoryClusteringEventCount ?? 0;
        return totalBundles > 0 ? (double)clusteringEvents / totalBundles : 0.0;
    }

    /// <summary>
    /// Returns a bounded advisory memory-spacing signal derived from clustering events and bank-conflict stalls.
    /// Missing or sparse payloads gracefully fall back to 0.0.
    /// </summary>
    public double GetAdvisoryMemoryClusteringSignal()
    {
        return Math.Max(GetMemoryClusteringEventRate(), GetBankConflictStallRate());
    }

    /// <summary>
    /// Returns the total pipeline cycles lost to bank conflicts, or 0 when unavailable.
    /// </summary>
    public long GetBankConflictStallCycles()
    {
        return _profile?.BankConflictStallCycles ?? 0;
    }

    /// <summary>
    /// Returns the normalized bank-conflict stall rate as stall cycles / total bundles executed.
    /// Returns 0.0 when no profile or no bundle count is available.
    /// </summary>
    public double GetBankConflictStallRate()
    {
        if (_profile is null)
            return 0.0;

        long totalBundles = _profile.TotalBundlesExecuted;
        long stallCycles = _profile.BankConflictStallCycles ?? 0;
        return totalBundles > 0 ? (double)stallCycles / totalBundles : 0.0;
    }

    /// <summary>
    /// Returns a bounded advisory bank-pressure signal that consolidates hottest-bank reject share,
    /// clustering-derived pressure, and bank-conflict stall pressure for compiler shaping only.
    /// This must not be treated as final bank policy.
    /// </summary>
    public double GetAdvisoryBankPressureSignal()
    {
        return Math.Max(GetPeakBankPendingRejectPressure(), GetAdvisoryMemoryClusteringSignal());
    }

    /// <summary>
    /// Returns the total hazard pair count for a specific typed effect, or 0 when unavailable.
    /// </summary>
    public long GetHazardEffectCount(IrHazardEffectKind effectKind)
    {
        return effectKind switch
        {
            IrHazardEffectKind.RegisterData => _profile?.HazardRegisterDataCount ?? 0,
            IrHazardEffectKind.MemoryBank => _profile?.HazardMemoryBankCount ?? 0,
            IrHazardEffectKind.ControlFlow => _profile?.HazardControlFlowCount ?? 0,
            IrHazardEffectKind.SystemBarrier => _profile?.HazardSystemBarrierCount ?? 0,
            IrHazardEffectKind.PinnedLane => _profile?.HazardPinnedLaneCount ?? 0,
            _ => 0
        };
    }

    /// <summary>
    /// Returns the total cross-domain reject count exported by the runtime, or 0 when unavailable.
    /// This is read-only structural telemetry and must not be treated as runtime policy.
    /// </summary>
    public long GetCrossDomainRejectCount()
    {
        return _profile?.CrossDomainRejectCount ?? 0;
    }

    /// <summary>
    /// Returns the exported cross-domain reject rate as rejects / total bundles executed, or 0.0 when unavailable.
    /// This remains read-only advisory compiler telemetry and must not be treated as runtime policy.
    /// </summary>
    public double GetCrossDomainRejectRate()
    {
        if (_profile is null)
            return 0.0;

        long totalBundles = _profile.TotalBundlesExecuted;
        long crossDomainRejects = _profile.CrossDomainRejectCount ?? 0;
        return totalBundles > 0 ? (double)crossDomainRejects / totalBundles : 0.0;
    }

    /// <summary>
    /// Returns the exported loop-phase telemetry profiles, or an empty list when unavailable.
    /// </summary>
    public IReadOnlyList<LoopPhaseClassProfile> GetLoopPhaseProfiles()
    {
        return _profile?.LoopPhaseProfiles ?? Array.Empty<LoopPhaseClassProfile>();
    }

    /// <summary>
    /// Returns <c>true</c> when loop-phase telemetry exists for the specified loop PC.
    /// </summary>
    public bool HasLoopPhaseProfile(ulong loopPcAddress)
    {
        return GetLoopPhaseProfile(loopPcAddress) is not null;
    }

    /// <summary>
    /// Returns the sampled iteration count for a loop PC, or 0 when unavailable.
    /// </summary>
    public int GetLoopIterationsSampled(ulong loopPcAddress)
    {
        LoopPhaseClassProfile? profile = GetLoopPhaseProfile(loopPcAddress);
        return profile?.IterationsSampled ?? 0;
    }

    /// <summary>
    /// Returns the overall class-variance score for a loop PC, or 0.0 when unavailable.
    /// </summary>
    public double GetLoopOverallClassVariance(ulong loopPcAddress)
    {
        LoopPhaseClassProfile? profile = GetLoopPhaseProfile(loopPcAddress);
        return profile?.OverallClassVariance ?? 0.0;
    }

    /// <summary>
    /// Returns the loop-local template reuse rate for a loop PC, or 0.0 when unavailable.
    /// </summary>
    public double GetLoopTemplateReuseRate(ulong loopPcAddress)
    {
        LoopPhaseClassProfile? profile = GetLoopPhaseProfile(loopPcAddress);
        return profile?.TemplateReuseRate ?? 0.0;
    }

    /// <summary>
    /// Returns the class-local variance for the requested loop PC and slot class, or 0.0 when unavailable.
    /// </summary>
    public double GetLoopClassVariance(ulong loopPcAddress, IrSlotClass slotClass)
    {
        LoopPhaseClassProfile? profile = GetLoopPhaseProfile(loopPcAddress);
        if (profile is null)
            return 0.0;

        return slotClass switch
        {
            IrSlotClass.AluClass => profile.AluFreeVariance,
            IrSlotClass.LsuClass => profile.LsuFreeVariance,
            IrSlotClass.DmaStreamClass => profile.DmaStreamFreeVariance,
            IrSlotClass.BranchControl => profile.BranchControlFreeVariance,
            IrSlotClass.SystemSingleton => profile.SystemSingletonFreeVariance,
            _ => 0.0
        };
    }

    /// <summary>
    /// Maximum distance (in address units) between a loop header and a block start
    /// for the nearest-preceding resolution strategy. Blocks further away than this
    /// are not associated with the loop header. The value is intentionally conservative
    /// and may be refined after <c>ISE-V4-F</c> finalises the loop-boundary export format.
    /// </summary>
    private const ulong MaxLoopBodyDistance = 0x400;

    /// <summary>
    /// Attempts to resolve a loop-phase profile for a basic block using multi-strategy lookup.
    /// <list type="number">
    ///   <item>Exact match: <paramref name="blockStartAddress"/> equals a profiled <c>LoopPcAddress</c>.</item>
    ///   <item>Nearest-preceding match: the block starts after a known loop header within a bounded
    ///         distance, suggesting the block is inside that loop body.</item>
    /// </list>
    /// Returns <c>null</c> when no suitable profile can be resolved.
    /// This helper is designed for bounded compiler-side prep (Wave 2B-α) and may be refined
    /// after <c>ISE-V4-F</c> finalises the loop-boundary export format.
    /// </summary>
    /// <param name="blockStartAddress">The start PC of the basic block to resolve.</param>
    /// <param name="blockEndAddress">
    /// The end PC of the basic block. When non-zero, provides an additional upper-bound
    /// check for candidate loop headers. Pass <c>0</c> to rely only on the distance limit.
    /// </param>
    public LoopPhaseClassProfile? TryResolveLoopProfile(ulong blockStartAddress, ulong blockEndAddress = 0)
    {
        if (_profile?.LoopPhaseProfiles is not { Count: > 0 } loopPhaseProfiles)
            return null;

        // Strategy 1: exact match (trivial loop where block == loop header).
        LoopPhaseClassProfile? exactMatch = GetLoopPhaseProfile(blockStartAddress);
        if (exactMatch is not null)
            return exactMatch;

        // Strategy 2: nearest-preceding loop header within bounded distance.
        // If blockStartAddress falls shortly after a known LoopPcAddress,
        // the block likely belongs to that loop body.
        LoopPhaseClassProfile? bestCandidate = null;
        ulong bestDistance = ulong.MaxValue;

        for (int index = 0; index < loopPhaseProfiles.Count; index++)
        {
            LoopPhaseClassProfile candidate = loopPhaseProfiles[index];
            ulong loopPc = candidate.LoopPcAddress;

            // Loop header must precede the block start.
            if (loopPc >= blockStartAddress)
                continue;

            ulong distance = blockStartAddress - loopPc;

            // Bounded distance: don't associate blocks far from the loop header.
            if (distance > MaxLoopBodyDistance)
                continue;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestCandidate = candidate;
            }
        }

        return bestCandidate;
    }

    private LoopPhaseClassProfile? GetLoopPhaseProfile(ulong loopPcAddress)
    {
        if (_profile?.LoopPhaseProfiles is not { Count: > 0 } loopPhaseProfiles)
            return null;

        for (int index = 0; index < loopPhaseProfiles.Count; index++)
        {
            LoopPhaseClassProfile profile = loopPhaseProfiles[index];
            if (profile.LoopPcAddress == loopPcAddress)
                return profile;
        }

        return null;
    }

    /// <summary>
    /// Returns the per-bundle typed hazard effect rate for a specific effect kind.
    /// Returns 0.0 when no profile or no bundle count is available.
    /// </summary>
    public double GetHazardEffectRate(IrHazardEffectKind effectKind)
    {
        if (_profile is null)
            return 0.0;

        long totalBundles = _profile.TotalBundlesExecuted;
        long effectCount = GetHazardEffectCount(effectKind);
        return totalBundles > 0 ? (double)effectCount / totalBundles : 0.0;
    }

    /// <summary>
    /// Returns the reject rate for a specific <see cref="IrTypedSlotRejectReason"/>.
    /// Computed as reason-specific rejects / total rejects across all reasons.
    /// Returns 0.0 when no profile or no reject data is available.
    /// </summary>
    public double GetRejectRate(IrTypedSlotRejectReason reason)
    {
        if (_profile is null)
            return 0.0;

        long reasonRejects = _profile.RejectsByReason.TryGetValue(reason, out long r) ? r : 0;
        long totalRejects = 0;
        foreach (long count in _profile.RejectsByReason.Values)
            totalRejects += count;

        return totalRejects > 0 ? (double)reasonRejects / totalRejects : 0.0;
    }

    /// <summary>
    /// Returns the average NOP density (0.0–1.0) from the profile, or 0.0 if unavailable.
    /// </summary>
    public double GetNopDensity()
    {
        return _profile?.AverageNopDensity ?? 0.0;
    }

    /// <summary>
    /// Returns worker performance metrics by name, or <c>null</c> if unavailable.
    /// </summary>
    public WorkerPerformanceMetrics? GetWorkerMetrics(string workerName)
    {
        if (_profile?.WorkerMetrics is null || string.IsNullOrWhiteSpace(workerName))
            return null;

        return _profile.WorkerMetrics.TryGetValue(workerName, out WorkerPerformanceMetrics? m) ? m : null;
    }

    /// <summary>
    /// Returns the replay template hit rate (0.0–1.0) from the profile, or 0.0 if unavailable.
    /// </summary>
    public double GetReplayHitRate()
    {
        return _profile?.ReplayHitRate ?? 0.0;
    }

    /// <summary>
    /// Returns all worker metrics from the profile, or an empty dictionary.
    /// </summary>
    public IReadOnlyDictionary<string, WorkerPerformanceMetrics> GetAllWorkerMetrics()
    {
        return _profile?.WorkerMetrics
            ?? (IReadOnlyDictionary<string, WorkerPerformanceMetrics>)new Dictionary<string, WorkerPerformanceMetrics>();
    }

    // ── Phase 06: Decoder-modernization feedback API ────────────

    /// <summary>
    /// Phase 06: Decoder-modernization telemetry counters captured from runtime feedback.
    /// Null when no decoder-modernization data is available (pre-Phase 06 profiles).
    /// Set before calling scheduling APIs to enable decoder-fallback-aware penalties.
    /// </summary>
    public DecoderModernizationCounters? DecoderCounters { get; set; }

    /// <summary>
    /// Returns the decoder fallback ratio: prepared groups that fell back to legacy mode
    /// divided by total prepared scalar groups. Returns 0.0 when no data is available.
    /// Compiler uses this ratio to trigger grouping penalties when fallback is frequent.
    /// </summary>
    public double GetDecoderFallbackRatio()
    {
        if (DecoderCounters is null)
            return 0.0;

        ulong total = DecoderCounters.DecoderPreparedScalarGroupCount;
        ulong fallbacks = DecoderCounters.DecoderPreparedFallbackCount;
        return total > 0 ? (double)fallbacks / total : 0.0;
    }

    /// <summary>
    /// Returns the decoder prepared group rate: fraction of decoded bundles that had
    /// a successfully prepared scalar group (>= 2 candidates). Returns 0.0 when unavailable.
    /// </summary>
    public double GetDecoderPreparedGroupRate()
    {
        if (_profile is null || DecoderCounters is null)
            return 0.0;

        long totalBundles = _profile.TotalBundlesExecuted;
        ulong preparedGroups = DecoderCounters.DecoderPreparedScalarGroupCount;
        return totalBundles > 0 ? (double)preparedGroups / totalBundles : 0.0;
    }

    /// <summary>
    /// Returns the cross-slot reject rate: fraction of scalar-eligible ops that were
    /// rejected by QuerySlotHazards with HardReject. Returns 0.0 when unavailable.
    /// </summary>
    public double GetCrossSlotRejectRate()
    {
        if (DecoderCounters is null)
            return 0.0;

        ulong rejects = DecoderCounters.CrossSlotRejectCount;
        ulong prepared = DecoderCounters.DecoderPreparedScalarGroupCount;
        return prepared > 0 ? (double)rejects / prepared : 0.0;
    }
}

/// <summary>
/// Phase 06 decoder-modernization telemetry counters from runtime feedback.
/// Each counter has a planned compiler-side reaction per Phase 06 §3.1.
/// </summary>
public sealed record DecoderModernizationCounters(
    ulong DecoderPreparedScalarGroupCount,
    ulong DecoderPreparedFallbackCount,
    ulong CrossSlotRejectCount,
    ulong ScalarClusterEligibleButBlockedCount,
    ulong ReferenceFallbackDueToControlConflictCount,
    ulong ReferenceFallbackDueToMemoryConflictCount,
    ulong VTSpreadPerBundle);
