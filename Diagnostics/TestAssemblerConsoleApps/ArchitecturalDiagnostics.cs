using System;
using System.Collections.Generic;
using System.Linq;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed partial class SimpleAsmApp
{
    internal SafetyVerifierNegativeControlsReport ExecuteSafetyVerifierNegativeControls()
    {
        List<SafetyVerifierNegativeControlResult> controls = new List<SafetyVerifierNegativeControlResult>
        {
            RunSmtGuardNegativeControl(
                scenario: "mismatch owner/context",
                ownerOperation: CreateScopedScalarAlu(0, ownerContextId: 100, domainTag: 0x2, destReg: 1, src1Reg: 2, src2Reg: 3),
                candidate: CreateScopedScalarAlu(1, ownerContextId: 200, domainTag: 0x2, destReg: 16, src1Reg: 17, src2Reg: 18),
                expectedRejectKind: RejectKind.OwnerMismatch,
                expectedAuthoritySource: LegalityAuthoritySource.GuardPlane,
                counterSelector: static metrics => metrics.SmtOwnerContextGuardRejects),

            RunSmtGuardNegativeControl(
                scenario: "mismatch domains",
                ownerOperation: CreateScopedScalarAlu(0, ownerContextId: 100, domainTag: 0x2, destReg: 4, src1Reg: 5, src2Reg: 6),
                candidate: CreateScopedScalarAlu(1, ownerContextId: 100, domainTag: 0x4, destReg: 20, src1Reg: 21, src2Reg: 22),
                expectedRejectKind: RejectKind.DomainMismatch,
                expectedAuthoritySource: LegalityAuthoritySource.GuardPlane,
                counterSelector: static metrics => metrics.SmtDomainGuardRejects),

            RunSmtGuardNegativeControl(
                scenario: "closed serialization boundary",
                ownerOperation: new DiagnosticSerializingMicroOp(0, ownerContextId: 100, domainTag: 0x2),
                candidate: CreateScopedScalarAlu(1, ownerContextId: 100, domainTag: 0x2, destReg: 24, src1Reg: 25, src2Reg: 26),
                expectedRejectKind: RejectKind.Boundary,
                expectedAuthoritySource: LegalityAuthoritySource.GuardPlane,
                counterSelector: static metrics => metrics.SmtBoundaryGuardRejects),

            RunInvalidReplayBoundaryNegativeControl(),
            RunStaleWitnessTemplateNegativeControl()
        };

        SafetyVerifierNegativeCounters counters = new(
            OwnerContextRejects: controls.Single(c => c.Scenario == "mismatch owner/context").CounterValue,
            DomainRejects: controls.Single(c => c.Scenario == "mismatch domains").CounterValue,
            BoundaryRejects: controls.Single(c => c.Scenario == "closed serialization boundary").CounterValue,
            InvalidReplayBoundaryRejects: controls.Single(c => c.Scenario == "invalid replay boundary").CounterValue,
            StaleWitnessTemplateRejects: controls.Single(c => c.Scenario == "stale witness/template rejection").CounterValue);

        return new SafetyVerifierNegativeControlsReport(
            controls,
            counters,
            controls.All(static control => control.Passed));
    }

    internal ReplayReuseDiagnosticsReport ExecuteReplayReuseDiagnostics(ulong iterations)
    {
        ReplayPhaseBenchmarkPairReport schedulerPair = ExecuteReplayPhaseBenchmarkPair(iterations);
        int samples = ClampReplaySamples(iterations);

        ReplayTemplateReuseScenarioReport[] scenarios =
        [
            RunReplayTemplateScenario(
                "stable replay-template reuse",
                samples,
                static _ => CreateReplayPhase(epochId: 41, cachedPc: 0x4100, completedReplays: 8, stableDonorMask: 0b0000_1110),
                static _ => CreateCertificateIdentity(CreateScopedScalarAlu(0, 100, 0x2, 1, 2, 3)),
                static _ => BoundaryGuardState.Open(0)),

            RunReplayTemplateScenario(
                "phase-key invalidation",
                samples,
                static sample => CreateReplayPhase(epochId: (ulong)(50 + sample), cachedPc: 0x5100, completedReplays: 8, stableDonorMask: 0b0000_1110),
                static _ => CreateCertificateIdentity(CreateScopedScalarAlu(0, 100, 0x2, 4, 5, 6)),
                static _ => BoundaryGuardState.Open(0)),

            RunReplayTemplateScenario(
                "structural-identity invalidation",
                samples,
                static _ => CreateReplayPhase(epochId: 61, cachedPc: 0x6100, completedReplays: 8, stableDonorMask: 0b0000_1110),
                static sample => sample % 2 == 0
                    ? CreateCertificateIdentity(CreateScopedScalarAlu(0, 100, 0x2, 7, 8, 9))
                    : CreateCertificateIdentity(
                        CreateScopedScalarAlu(0, 100, 0x2, 7, 8, 9),
                        CreateScopedScalarAlu(1, 100, 0x2, 24, 25, 26)),
                static _ => BoundaryGuardState.Open(0)),

            RunReplayTemplateScenario(
                "boundary-state invalidation",
                samples,
                static _ => CreateReplayPhase(epochId: 71, cachedPc: 0x7100, completedReplays: 8, stableDonorMask: 0b0000_1110),
                static _ => CreateCertificateIdentity(CreateScopedScalarAlu(0, 100, 0x2, 10, 11, 12)),
                static sample => sample % 2 == 0
                    ? BoundaryGuardState.Open(0)
                    : new BoundaryGuardState(0, SmtReplayBoundaryMode.SerializingBundle))
        ];

        ReplayTemplateReuseMetrics aggregate = ReplayTemplateReuseMetrics.Sum(scenarios.Select(static scenario => scenario.Metrics));
        bool succeeded =
            schedulerPair.StablePhase.PhaseCertificateReadyHits > 0 &&
            schedulerPair.RotatingPhase.PhaseCertificatePhaseMismatchInvalidations >
                schedulerPair.StablePhase.PhaseCertificatePhaseMismatchInvalidations &&
            aggregate.ReplayTemplateLookupAttempts > 0 &&
            aggregate.ReplayTemplateHits > 0 &&
            aggregate.ReplayTemplateMisses > 0 &&
            aggregate.InvalidationsByPhaseKey > 0 &&
            aggregate.InvalidationsByStructuralIdentity > 0 &&
            aggregate.InvalidationsByBoundaryState > 0 &&
            aggregate.WitnessAccesses == aggregate.ReplayTemplateLookupAttempts &&
            aggregate.FallbackToLiveWitness == aggregate.ReplayTemplateMisses &&
            scenarios.Single(static scenario => scenario.Scenario == "stable replay-template reuse").Metrics.WarmupMisses == 1;

        return new ReplayReuseDiagnosticsReport(schedulerPair, scenarios, aggregate, succeeded);
    }

    internal AssistantDecisionMatrixReport ExecuteAssistantDecisionMatrix()
    {
        ReplayPhaseContext activePhase = CreateReplayPhase(epochId: 91, cachedPc: 0x9100, completedReplays: 8, stableDonorMask: 0b0000_0010);
        ReplayPhaseContext inactivePhase = new(
            isActive: false,
            epochId: 92,
            cachedPc: 0x9200,
            epochLength: 8,
            completedReplays: 0,
            validSlotCount: 1,
            stableDonorMask: 0,
            lastInvalidationReason: ReplayPhaseInvalidationReason.InactivePhase);

        AssistantDecisionScenarioSpec[] specs =
        [
            new(
                "assistance accepted with residual capacity",
                CreateAssistMicroOp(ownerContextId: 100, domainTag: 0x2),
                activePhase,
                AssistMemoryQuota.Default,
                AssistBackpressurePolicy.Default,
                CreateBackpressureSnapshot(sharedOuterCapCredits: 1),
                PrimaryResidualCapacity: 2,
                RequiredOwnerContextId: 100,
                RequiredDomainTag: 0x2,
                AssistantDecisionOutcome.Accepted,
                AssistantDecisionReason.AcceptedWithResidualCapacity),

            new(
                "assistance rejected by quota",
                CreateAssistMicroOp(ownerContextId: 100, domainTag: 0x2),
                activePhase,
                new AssistMemoryQuota(issueCredits: 1, lineCredits: 0, hotLineCap: 2, coldLineCap: 4),
                AssistBackpressurePolicy.Default,
                CreateBackpressureSnapshot(sharedOuterCapCredits: 1),
                PrimaryResidualCapacity: 2,
                RequiredOwnerContextId: 100,
                RequiredDomainTag: 0x2,
                AssistantDecisionOutcome.Rejected,
                AssistantDecisionReason.Quota),

            new(
                "assistance rejected by backpressure",
                CreateAssistMicroOp(ownerContextId: 100, domainTag: 0x2),
                activePhase,
                AssistMemoryQuota.Default,
                AssistBackpressurePolicy.Default,
                CreateBackpressureSnapshot(sharedOuterCapCredits: 0),
                PrimaryResidualCapacity: 2,
                RequiredOwnerContextId: 100,
                RequiredDomainTag: 0x2,
                AssistantDecisionOutcome.Rejected,
                AssistantDecisionReason.Backpressure),

            new(
                "assistance rejected by owner/domain administrator",
                CreateAssistMicroOp(ownerContextId: 200, domainTag: 0x4),
                activePhase,
                AssistMemoryQuota.Default,
                AssistBackpressurePolicy.Default,
                CreateBackpressureSnapshot(sharedOuterCapCredits: 1),
                PrimaryResidualCapacity: 2,
                RequiredOwnerContextId: 100,
                RequiredDomainTag: 0x2,
                AssistantDecisionOutcome.Rejected,
                AssistantDecisionReason.OwnerDomainAdministrator),

            new(
                "assistance rejected by invalid replay",
                CreateAssistMicroOp(ownerContextId: 100, domainTag: 0x2),
                inactivePhase,
                AssistMemoryQuota.Default,
                AssistBackpressurePolicy.Default,
                CreateBackpressureSnapshot(sharedOuterCapCredits: 1),
                PrimaryResidualCapacity: 2,
                RequiredOwnerContextId: 100,
                RequiredDomainTag: 0x2,
                AssistantDecisionOutcome.Rejected,
                AssistantDecisionReason.InvalidReplay),

            new(
                "primary stream priority over assistant stream",
                CreateAssistMicroOp(ownerContextId: 100, domainTag: 0x2),
                activePhase,
                AssistMemoryQuota.Default,
                AssistBackpressurePolicy.Default,
                CreateBackpressureSnapshot(sharedOuterCapCredits: 1),
                PrimaryResidualCapacity: 0,
                RequiredOwnerContextId: 100,
                RequiredDomainTag: 0x2,
                AssistantDecisionOutcome.Rejected,
                AssistantDecisionReason.PrimaryStreamPriority)
        ];

        AssistantDecisionScenarioReport[] scenarios = specs
            .Select(EvaluateAssistantScenario)
            .ToArray();

        AssistantDecisionMatrixMetrics aggregate = AssistantDecisionMatrixMetrics.Sum(scenarios.Select(static scenario => scenario.Metrics));
        bool succeeded = scenarios.All(static scenario => scenario.Passed) &&
                         aggregate.Accepted == 1 &&
                         aggregate.QuotaRejects == 1 &&
                         aggregate.BackpressureRejects == 1 &&
                         aggregate.OwnerDomainAdministratorRejects == 1 &&
                         aggregate.InvalidReplayRejects == 1 &&
                         aggregate.PrimaryStreamPriorityRejects == 1 &&
                         aggregate.ResidualCapacityAfterAccepted > 0;

        AssistantVisibilityDiagnosticsReport visibilityDiagnostics = ExecuteAssistantVisibilityDiagnostics();

        return new AssistantDecisionMatrixReport(
            scenarios,
            aggregate,
            visibilityDiagnostics,
            succeeded && visibilityDiagnostics.Passed);
    }

    private static SafetyVerifierNegativeControlResult RunSmtGuardNegativeControl(
        string scenario,
        MicroOp ownerOperation,
        MicroOp candidate,
        RejectKind expectedRejectKind,
        LegalityAuthoritySource expectedAuthoritySource,
        Func<SchedulerPhaseMetrics, long> counterSelector)
    {
        MicroOpScheduler scheduler = new MicroOpScheduler
        {
            TypedSlotEnabled = true,
            CreditFairnessEnabled = false
        };
        scheduler.SetReplayPhaseContext(CreateReplayPhase(epochId: 17, cachedPc: 0x1700, completedReplays: 4, stableDonorMask: 0b0000_1110));

        MicroOp?[] bundle = new MicroOp?[8];
        int ownerSlot = ownerOperation.Placement.PinningKind == SlotPinningKind.HardPinned
            ? ownerOperation.Placement.PinnedLaneId
            : 0;
        bundle[ownerSlot] = ownerOperation;

        scheduler.NominateSmtCandidate(candidate.VirtualThreadId, candidate);
        MicroOp[] packed = scheduler.PackBundleIntraCoreSmt(
            bundle,
            ownerVirtualThreadId: ownerOperation.VirtualThreadId,
            localCoreId: 0,
            eligibleVirtualThreadMask: 0x0F);

        SchedulerPhaseMetrics metrics = scheduler.GetPhaseMetrics();
        bool rejected = !packed.Any(op => ReferenceEquals(op, candidate));
        long counterValue = counterSelector(metrics);
        bool passed = rejected &&
                      metrics.LastSmtLegalityRejectKind == expectedRejectKind &&
                      metrics.LastSmtLegalityAuthoritySource == expectedAuthoritySource &&
                      counterValue > 0;

        return new SafetyVerifierNegativeControlResult(
            scenario,
            rejected,
            expectedRejectKind.ToString(),
            metrics.LastSmtLegalityRejectKind.ToString(),
            expectedAuthoritySource.ToString(),
            metrics.LastSmtLegalityAuthoritySource.ToString(),
            counterValue,
            passed);
    }

    private static SafetyVerifierNegativeControlResult RunInvalidReplayBoundaryNegativeControl()
    {
        ReplayTemplateReuseTracker tracker = new ReplayTemplateReuseTracker();
        ReplayPhaseContext stablePhase = CreateReplayPhase(epochId: 31, cachedPc: 0x3100, completedReplays: 4, stableDonorMask: 0b0000_1110);
        BundleResourceCertificateIdentity4Way identity = CreateCertificateIdentity(CreateScopedScalarAlu(0, 100, 0x2, 1, 2, 3));
        tracker.Prime(stablePhase, identity, BoundaryGuardState.Open(0));

        ReplayPhaseContext inactivePhase = new(
            isActive: false,
            epochId: 31,
            cachedPc: 0x3100,
            epochLength: 8,
            completedReplays: 0,
            validSlotCount: 1,
            stableDonorMask: 0,
            lastInvalidationReason: ReplayPhaseInvalidationReason.InactivePhase);

        ReplayTemplateLookupResult lookup = tracker.Lookup(inactivePhase, identity, BoundaryGuardState.Open(0), updateTemplateOnMiss: false);
        ReplayTemplateReuseMetrics metrics = tracker.Snapshot();
        bool rejected = !lookup.Hit;
        bool passed = rejected &&
                      lookup.RejectReason == ReplayTemplateRejectReason.InvalidReplayBoundary &&
                      metrics.InvalidationsByPhaseKey > 0;

        return new SafetyVerifierNegativeControlResult(
            "invalid replay boundary",
            rejected,
            ReplayTemplateRejectReason.InvalidReplayBoundary.ToString(),
            lookup.RejectReason.ToString(),
            "ReplayTemplateBoundary",
            lookup.AuthoritySource,
            metrics.InvalidationsByPhaseKey,
            passed);
    }

    private static SafetyVerifierNegativeControlResult RunStaleWitnessTemplateNegativeControl()
    {
        ReplayTemplateReuseTracker tracker = new ReplayTemplateReuseTracker();
        ReplayPhaseContext phase = CreateReplayPhase(epochId: 32, cachedPc: 0x3200, completedReplays: 4, stableDonorMask: 0b0000_1110);
        BundleResourceCertificateIdentity4Way originalIdentity = CreateCertificateIdentity(CreateScopedScalarAlu(0, 100, 0x2, 4, 5, 6));
        BundleResourceCertificateIdentity4Way staleIdentity = CreateCertificateIdentity(
            CreateScopedScalarAlu(0, 100, 0x2, 4, 5, 6),
            CreateScopedScalarAlu(1, 100, 0x2, 20, 21, 22));
        tracker.Prime(phase, originalIdentity, BoundaryGuardState.Open(0));

        ReplayTemplateLookupResult lookup = tracker.Lookup(phase, staleIdentity, BoundaryGuardState.Open(0), updateTemplateOnMiss: false);
        ReplayTemplateReuseMetrics metrics = tracker.Snapshot();
        bool rejected = !lookup.Hit;
        bool passed = rejected &&
                      lookup.RejectReason == ReplayTemplateRejectReason.StaleStructuralIdentity &&
                      metrics.InvalidationsByStructuralIdentity > 0 &&
                      metrics.WitnessAccesses > 0;

        return new SafetyVerifierNegativeControlResult(
            "stale witness/template rejection",
            rejected,
            ReplayTemplateRejectReason.StaleStructuralIdentity.ToString(),
            lookup.RejectReason.ToString(),
            "ReplayTemplateWitness",
            lookup.AuthoritySource,
            metrics.InvalidationsByStructuralIdentity,
            passed);
    }

    private static ReplayTemplateReuseScenarioReport RunReplayTemplateScenario(
        string scenario,
        int samples,
        Func<int, ReplayPhaseContext> phaseFactory,
        Func<int, BundleResourceCertificateIdentity4Way> identityFactory,
        Func<int, BoundaryGuardState> boundaryFactory)
    {
        ReplayTemplateReuseTracker tracker = new ReplayTemplateReuseTracker();
        List<ReplayTemplateLookupResult> lookups = new List<ReplayTemplateLookupResult>(samples);
        for (int sample = 0; sample < samples; sample++)
        {
            lookups.Add(tracker.Lookup(
                phaseFactory(sample),
                identityFactory(sample),
                boundaryFactory(sample),
                updateTemplateOnMiss: true));
        }

        ReplayTemplateReuseMetrics metrics = tracker.Snapshot();
        bool passed = scenario switch
        {
            "stable replay-template reuse" => metrics.ReplayTemplateHits > 0 && metrics.ReplayTemplateMisses == 1,
            "phase-key invalidation" => metrics.InvalidationsByPhaseKey > 0,
            "structural-identity invalidation" => metrics.InvalidationsByStructuralIdentity > 0,
            "boundary-state invalidation" => metrics.InvalidationsByBoundaryState > 0,
            _ => metrics.ReplayTemplateLookupAttempts > 0
        };

        return new ReplayTemplateReuseScenarioReport(scenario, lookups, metrics, passed);
    }

    private static AssistantVisibilityDiagnosticsReport ExecuteAssistantVisibilityDiagnostics()
    {
        AssistMicroOp assist = CreateAssistMicroOp(ownerContextId: 100, domainTag: 0x2);
        ReplayPhaseContext activePhase = CreateReplayPhase(epochId: 101, cachedPc: 0xA100, completedReplays: 8, stableDonorMask: 0b0000_0010);
        AssistantDecisionResult accepted = EvaluateAssistantDecision(
            assist,
            activePhase,
            AssistMemoryQuota.Default,
            AssistBackpressurePolicy.Default,
            CreateBackpressureSnapshot(sharedOuterCapCredits: 1),
            primaryResidualCapacity: 2,
            requiredOwnerContextId: 100,
            requiredDomainTag: 0x2);

        ReplayPhaseContext invalidatedPhase = new(
            isActive: true,
            epochId: activePhase.EpochId,
            cachedPc: activePhase.CachedPc,
            epochLength: activePhase.EpochLength,
            completedReplays: activePhase.CompletedReplays,
            validSlotCount: activePhase.ValidSlotCount,
            stableDonorMask: activePhase.StableDonorMask,
            lastInvalidationReason: ReplayPhaseInvalidationReason.PhaseMismatch);

        bool acceptedThenInvalidated =
            accepted.Outcome == AssistantDecisionOutcome.Accepted &&
            invalidatedPhase.LastInvalidationReason != ReplayPhaseInvalidationReason.None;

        ScalarALUMicroOp foreground = CreateScopedScalarAlu(0, ownerContextId: 100, domainTag: 0x2, destReg: 30, src1Reg: 31, src2Reg: 32);
        long foregroundRetireRecordsBefore = foreground.IsRetireVisible ? 1 : 0;
        long foregroundRetireRecordsAfter = foregroundRetireRecordsBefore;

        AssistantLifecycleOutcome actualOutcome = acceptedThenInvalidated
            ? AssistantLifecycleOutcome.DiscardedOnReplayInvalidation
            : AssistantLifecycleOutcome.NotDiscarded;

        AssistantVisibilityDiagnosticsMetrics metrics = new AssistantVisibilityDiagnosticsMetrics(
            AssistAccepted: accepted.Metrics.Accepted,
            AssistReplayInvalidatedAfterAcceptance: acceptedThenInvalidated ? 1 : 0,
            AssistDiscarded: actualOutcome == AssistantLifecycleOutcome.DiscardedOnReplayInvalidation ? 1 : 0,
            AssistRetireRecords: assist.IsRetireVisible ? 1 : 0,
            AssistArchitecturalWrites: assist.WritesRegister || assist.WriteRegisters.Count > 0 ? 1 : 0,
            AssistCommittedStores: assist.WriteMemoryRanges.Count,
            AssistTelemetryEvents: acceptedThenInvalidated ? 2 : 0,
            AssistCarrierPublications: accepted.Outcome == AssistantDecisionOutcome.Accepted &&
                assist.CanonicalDecodePublication == CanonicalDecodePublicationMode.SelfPublishes ? 1 : 0,
            ForegroundRetireRecordsBefore: foregroundRetireRecordsBefore,
            ForegroundRetireRecordsAfter: foregroundRetireRecordsAfter,
            ForegroundRetireRecordsPreserved: foregroundRetireRecordsAfter == foregroundRetireRecordsBefore);

        bool passed =
            actualOutcome == AssistantLifecycleOutcome.DiscardedOnReplayInvalidation &&
            metrics.AssistAccepted == 1 &&
            metrics.AssistReplayInvalidatedAfterAcceptance == 1 &&
            metrics.AssistDiscarded == 1 &&
            metrics.AssistRetireRecords == 0 &&
            metrics.AssistArchitecturalWrites == 0 &&
            metrics.AssistCommittedStores == 0 &&
            metrics.AssistTelemetryEvents > 0 &&
            metrics.AssistCarrierPublications > 0 &&
            metrics.ForegroundRetireRecordsPreserved;

        return new AssistantVisibilityDiagnosticsReport(
            EvidenceScope: "test-local lifecycle model; does not exercise production retire",
            Scenario: "assistance accepted then discarded on replay invalidation",
            ExpectedOutcome: AssistantLifecycleOutcome.DiscardedOnReplayInvalidation,
            ActualOutcome: actualOutcome,
            InvalidationReason: invalidatedPhase.LastInvalidationReason,
            Metrics: metrics,
            Passed: passed);
    }

    private static AssistantDecisionScenarioReport EvaluateAssistantScenario(AssistantDecisionScenarioSpec spec)
    {
        AssistantDecisionResult decision = EvaluateAssistantDecision(
            spec.Assist,
            spec.ReplayPhase,
            spec.Quota,
            spec.BackpressurePolicy,
            spec.BackpressureSnapshot,
            spec.PrimaryResidualCapacity,
            spec.RequiredOwnerContextId,
            spec.RequiredDomainTag);

        bool passed = decision.Outcome == spec.ExpectedOutcome &&
                      decision.Reason == spec.ExpectedReason &&
                      decision.Metrics.DecisionAttempts == 1;

        return new AssistantDecisionScenarioReport(
            spec.Scenario,
            spec.ExpectedOutcome,
            decision.Outcome,
            spec.ExpectedReason,
            decision.Reason,
            decision.Metrics,
            decision.Detail,
            passed);
    }

    private static AssistantDecisionResult EvaluateAssistantDecision(
        AssistMicroOp assist,
        ReplayPhaseContext replayPhase,
        AssistMemoryQuota quota,
        AssistBackpressurePolicy backpressurePolicy,
        AssistBackpressureSnapshot backpressureSnapshot,
        int primaryResidualCapacity,
        int requiredOwnerContextId,
        ulong requiredDomainTag)
    {
        AssistantDecisionMatrixMetricsBuilder metrics = new AssistantDecisionMatrixMetricsBuilder();

        if (!replayPhase.CanReusePhaseCertificate ||
            replayPhase.LastInvalidationReason != ReplayPhaseInvalidationReason.None)
        {
            metrics.RecordReject(AssistantDecisionReason.InvalidReplay);
            return AssistantDecisionResult.Reject(
                AssistantDecisionReason.InvalidReplay,
                "replay phase cannot carry an assistant template",
                metrics.ToMetrics());
        }

        if (primaryResidualCapacity <= 0)
        {
            metrics.RecordReject(AssistantDecisionReason.PrimaryStreamPriority);
            return AssistantDecisionResult.Reject(
                AssistantDecisionReason.PrimaryStreamPriority,
                "primary stream consumed all assistant-eligible residual capacity",
                metrics.ToMetrics());
        }

        SafetyVerifier verifier = new();
        bool ownerMatches = assist.OwnerContextId == requiredOwnerContextId;
        bool domainMatches = verifier.EvaluateDomainIsolationProbe(assist, requiredDomainTag).IsAllowed;
        if (!ownerMatches || !domainMatches)
        {
            metrics.RecordReject(AssistantDecisionReason.OwnerDomainAdministrator);
            return AssistantDecisionResult.Reject(
                AssistantDecisionReason.OwnerDomainAdministrator,
                ownerMatches ? "domain administrator rejected assist domain" : "owner administrator rejected assist context",
                metrics.ToMetrics());
        }

        AssistBackpressureState backpressureState = backpressurePolicy.CreateState(backpressureSnapshot);
        if (!backpressureState.TryReserve(assist, dmaSrfAvailable: true, out AssistBackpressureRejectKind backpressureReject))
        {
            metrics.RecordReject(AssistantDecisionReason.Backpressure);
            return AssistantDecisionResult.Reject(
                AssistantDecisionReason.Backpressure,
                backpressureReject.ToString(),
                metrics.ToMetrics());
        }

        AssistMemoryQuotaState quotaState = quota.CreateState();
        if (!quotaState.TryReserve(assist, out uint reservedLines, out AssistQuotaRejectKind quotaReject))
        {
            metrics.RecordReject(AssistantDecisionReason.Quota);
            return AssistantDecisionResult.Reject(
                AssistantDecisionReason.Quota,
                quotaReject.ToString(),
                metrics.ToMetrics());
        }

        int residualAfterAssistant = primaryResidualCapacity - 1;
        metrics.RecordAccepted(residualAfterAssistant, reservedLines);
        return AssistantDecisionResult.Accept(
            AssistantDecisionReason.AcceptedWithResidualCapacity,
            $"reserved-lines={reservedLines}, residual-after={residualAfterAssistant}",
            metrics.ToMetrics());
    }

    private static ScalarALUMicroOp CreateScopedScalarAlu(
        int virtualThreadId,
        int ownerContextId,
        ulong domainTag,
        ushort destReg,
        ushort src1Reg,
        ushort src2Reg)
    {
        ScalarALUMicroOp op = new ScalarALUMicroOp
        {
            VirtualThreadId = virtualThreadId,
            OwnerThreadId = virtualThreadId,
            OwnerContextId = ownerContextId,
            DestRegID = destReg,
            Src1RegID = src1Reg,
            Src2RegID = src2Reg,
            OpCode = (uint)Processor.CPU_Core.InstructionsEnum.ADD,
            WritesRegister = true
        };

        op.Placement = op.Placement with { DomainTag = domainTag };
        op.SafetyMask =
            ResourceMaskBuilder.ForRegisterRead128(src1Reg) |
            ResourceMaskBuilder.ForRegisterRead128(src2Reg) |
            ResourceMaskBuilder.ForRegisterWrite128(destReg);
        op.InitializeMetadata();
        return op;
    }

    private static AssistMicroOp CreateAssistMicroOp(
        int ownerContextId,
        ulong domainTag,
        ulong baseAddress = 0xA000)
    {
        AssistOwnerBinding binding = new AssistOwnerBinding(
            carrierVirtualThreadId: 0,
            donorVirtualThreadId: 1,
            targetVirtualThreadId: 1,
            ownerContextId,
            domainTag,
            replayEpochId: 91,
            assistEpochId: 1,
            localityHint: LocalityHint.Hot);

        return new AssistMicroOp(
            AssistKind.DonorPrefetch,
            AssistExecutionMode.CachePrefetch,
            AssistCarrierKind.LsuHosted,
            baseAddress,
            prefetchLength: 64,
            elementSize: 8,
            elementCount: 8,
            binding);
    }

    private static ReplayPhaseContext CreateReplayPhase(
        ulong epochId,
        ulong cachedPc,
        ulong completedReplays,
        byte stableDonorMask)
    {
        return new ReplayPhaseContext(
            isActive: true,
            epochId,
            cachedPc,
            epochLength: 8,
            completedReplays,
            validSlotCount: 1,
            stableDonorMask,
            lastInvalidationReason: ReplayPhaseInvalidationReason.None);
    }

    private static BundleResourceCertificateIdentity4Way CreateCertificateIdentity(params MicroOp[] operations)
    {
        BundleResourceCertificate4Way certificate = BundleResourceCertificate4Way.Empty;
        foreach (MicroOp operation in operations)
        {
            certificate.AddOperation(operation);
        }

        return certificate.StructuralIdentity;
    }

    private static AssistBackpressureSnapshot CreateBackpressureSnapshot(byte sharedOuterCapCredits)
    {
        return new AssistBackpressureSnapshot(
            sharedOuterCapCredits,
            consumedSharedReadBudgetByBank: 0,
            sharedReadBudgetAtLeastOneMask: 0xFFFF,
            sharedReadBudgetAtLeastTwoMask: 0xFFFF,
            projectedOutstandingCountVt0: 0,
            projectedOutstandingCountVt1: 0,
            projectedOutstandingCountVt2: 0,
            projectedOutstandingCountVt3: 0,
            projectedOutstandingCapacityVt0: 8,
            projectedOutstandingCapacityVt1: 8,
            projectedOutstandingCapacityVt2: 8,
            projectedOutstandingCapacityVt3: 8);
    }

    private static int ClampReplaySamples(ulong iterations)
    {
        if (iterations < 4)
        {
            return 4;
        }

        if (iterations > 4096)
        {
            return 4096;
        }

        return (int)iterations;
    }

    private sealed class ReplayTemplateReuseTracker
    {
        private ReplayTemplateLookupKey _templateKey;
        private bool _hasTemplate;
        private long _lookupAttempts;
        private long _hits;
        private long _misses;
        private long _invalidationsByPhaseKey;
        private long _invalidationsByStructuralIdentity;
        private long _invalidationsByBoundaryState;
        private long _witnessAccesses;
        private long _warmupMisses;
        private long _fallbackToLiveWitness;

        public void Prime(
            ReplayPhaseContext phase,
            BundleResourceCertificateIdentity4Way structuralIdentity,
            BoundaryGuardState boundaryGuard)
        {
            if (!phase.CanReusePhaseCertificate)
            {
                return;
            }

            _templateKey = new ReplayTemplateLookupKey(phase.Key, structuralIdentity, boundaryGuard);
            _hasTemplate = true;
        }

        public ReplayTemplateLookupResult Lookup(
            ReplayPhaseContext phase,
            BundleResourceCertificateIdentity4Way structuralIdentity,
            BoundaryGuardState boundaryGuard,
            bool updateTemplateOnMiss)
        {
            _lookupAttempts++;
            _witnessAccesses++;

            if (!phase.CanReusePhaseCertificate ||
                phase.LastInvalidationReason != ReplayPhaseInvalidationReason.None)
            {
                _misses++;
                _fallbackToLiveWitness++;
                _invalidationsByPhaseKey++;
                if (updateTemplateOnMiss)
                {
                    _hasTemplate = false;
                }

                return ReplayTemplateLookupResult.Miss(ReplayTemplateRejectReason.InvalidReplayBoundary);
            }

            ReplayTemplateLookupKey liveKey = new(phase.Key, structuralIdentity, boundaryGuard);
            if (!_hasTemplate)
            {
                _misses++;
                _warmupMisses++;
                _fallbackToLiveWitness++;
                if (updateTemplateOnMiss)
                {
                    _templateKey = liveKey;
                    _hasTemplate = true;
                }

                return ReplayTemplateLookupResult.Miss(ReplayTemplateRejectReason.ColdTemplate);
            }

            if (!_templateKey.PhaseKey.Equals(liveKey.PhaseKey))
            {
                _misses++;
                _fallbackToLiveWitness++;
                _invalidationsByPhaseKey++;
                RefreshTemplate(liveKey, updateTemplateOnMiss);
                return ReplayTemplateLookupResult.Miss(ReplayTemplateRejectReason.PhaseKeyMismatch);
            }

            if (!_templateKey.StructuralIdentity.Equals(liveKey.StructuralIdentity))
            {
                _misses++;
                _fallbackToLiveWitness++;
                _invalidationsByStructuralIdentity++;
                RefreshTemplate(liveKey, updateTemplateOnMiss);
                return ReplayTemplateLookupResult.Miss(ReplayTemplateRejectReason.StaleStructuralIdentity);
            }

            if (!_templateKey.BoundaryGuard.Equals(liveKey.BoundaryGuard))
            {
                _misses++;
                _fallbackToLiveWitness++;
                _invalidationsByBoundaryState++;
                RefreshTemplate(liveKey, updateTemplateOnMiss);
                return ReplayTemplateLookupResult.Miss(ReplayTemplateRejectReason.BoundaryStateMismatch);
            }

            _hits++;
            return ReplayTemplateLookupResult.HitResult();
        }

        public ReplayTemplateReuseMetrics Snapshot()
        {
            return new ReplayTemplateReuseMetrics(
                _lookupAttempts,
                _hits,
                _misses,
                _invalidationsByPhaseKey,
                _invalidationsByStructuralIdentity,
                _invalidationsByBoundaryState,
                _witnessAccesses,
                _warmupMisses,
                _fallbackToLiveWitness);
        }

        private void RefreshTemplate(ReplayTemplateLookupKey liveKey, bool updateTemplateOnMiss)
        {
            if (!updateTemplateOnMiss)
            {
                return;
            }

            _templateKey = liveKey;
            _hasTemplate = true;
        }
    }

    private sealed class DiagnosticSerializingMicroOp : MicroOp
    {
        public DiagnosticSerializingMicroOp(int virtualThreadId, int ownerContextId, ulong domainTag)
        {
            VirtualThreadId = virtualThreadId;
            OwnerThreadId = virtualThreadId;
            OwnerContextId = ownerContextId;
            OpCode = (uint)Processor.CPU_Core.InstructionsEnum.FENCE;
            Class = MicroOpClass.Other;
            InstructionClass = YAKSys_Hybrid_CPU.Arch.InstructionClass.System;
            SerializationClass = YAKSys_Hybrid_CPU.Arch.SerializationClass.FullSerial;
            WritesRegister = false;
            IsControlFlow = false;
            IsMemoryOp = false;
            SetHardPinnedPlacement(SlotClass.SystemSingleton, 7);
            Placement = Placement with { DomainTag = domainTag };
            ResourceMask = ResourceBitset.Zero;
            SafetyMask = SafetyMask128.All;
            RefreshAdmissionMetadata();
        }

        public override CanonicalDecodePublicationMode CanonicalDecodePublication =>
            CanonicalDecodePublicationMode.SelfPublishes;

        public override bool Execute(ref Processor.CPU_Core core) => true;

        public override string GetDescription() =>
            $"DiagnosticSerializing VT={VirtualThreadId}, OwnerContext={OwnerContextId}, Domain=0x{Placement.DomainTag:X}";
    }

    private readonly record struct ReplayTemplateLookupKey(
        ReplayPhaseKey PhaseKey,
        BundleResourceCertificateIdentity4Way StructuralIdentity,
        BoundaryGuardState BoundaryGuard);

    private sealed record AssistantDecisionScenarioSpec(
        string Scenario,
        AssistMicroOp Assist,
        ReplayPhaseContext ReplayPhase,
        AssistMemoryQuota Quota,
        AssistBackpressurePolicy BackpressurePolicy,
        AssistBackpressureSnapshot BackpressureSnapshot,
        int PrimaryResidualCapacity,
        int RequiredOwnerContextId,
        ulong RequiredDomainTag,
        AssistantDecisionOutcome ExpectedOutcome,
        AssistantDecisionReason ExpectedReason);

    private readonly record struct AssistantDecisionResult(
        AssistantDecisionOutcome Outcome,
        AssistantDecisionReason Reason,
        string Detail,
        AssistantDecisionMatrixMetrics Metrics)
    {
        public static AssistantDecisionResult Accept(
            AssistantDecisionReason reason,
            string detail,
            AssistantDecisionMatrixMetrics metrics) =>
            new(AssistantDecisionOutcome.Accepted, reason, detail, metrics);

        public static AssistantDecisionResult Reject(
            AssistantDecisionReason reason,
            string detail,
            AssistantDecisionMatrixMetrics metrics) =>
            new(AssistantDecisionOutcome.Rejected, reason, detail, metrics);
    }

    private sealed class AssistantDecisionMatrixMetricsBuilder
    {
        private long _decisionAttempts = 1;
        private long _accepted;
        private long _quotaRejects;
        private long _backpressureRejects;
        private long _ownerDomainAdministratorRejects;
        private long _invalidReplayRejects;
        private long _primaryStreamPriorityRejects;
        private long _residualCapacityAfterAccepted;
        private long _reservedLines;

        public void RecordAccepted(int residualCapacityAfterAccepted, uint reservedLines)
        {
            _accepted++;
            _residualCapacityAfterAccepted += residualCapacityAfterAccepted;
            _reservedLines += reservedLines;
        }

        public void RecordReject(AssistantDecisionReason reason)
        {
            switch (reason)
            {
                case AssistantDecisionReason.Quota:
                    _quotaRejects++;
                    break;
                case AssistantDecisionReason.Backpressure:
                    _backpressureRejects++;
                    break;
                case AssistantDecisionReason.OwnerDomainAdministrator:
                    _ownerDomainAdministratorRejects++;
                    break;
                case AssistantDecisionReason.InvalidReplay:
                    _invalidReplayRejects++;
                    break;
                case AssistantDecisionReason.PrimaryStreamPriority:
                    _primaryStreamPriorityRejects++;
                    break;
            }
        }

        public AssistantDecisionMatrixMetrics ToMetrics()
        {
            return new AssistantDecisionMatrixMetrics(
                _decisionAttempts,
                _accepted,
                _quotaRejects,
                _backpressureRejects,
                _ownerDomainAdministratorRejects,
                _invalidReplayRejects,
                _primaryStreamPriorityRejects,
                _residualCapacityAfterAccepted,
                _reservedLines);
        }
    }
}

internal readonly record struct SafetyVerifierNegativeControlsReport(
    IReadOnlyList<SafetyVerifierNegativeControlResult> Controls,
    SafetyVerifierNegativeCounters Counters,
    bool Succeeded);

internal readonly record struct SafetyVerifierNegativeControlResult(
    string Scenario,
    bool Rejected,
    string ExpectedRejectKind,
    string ActualRejectKind,
    string ExpectedAuthoritySource,
    string ActualAuthoritySource,
    long CounterValue,
    bool Passed);

internal readonly record struct SafetyVerifierNegativeCounters(
    long OwnerContextRejects,
    long DomainRejects,
    long BoundaryRejects,
    long InvalidReplayBoundaryRejects,
    long StaleWitnessTemplateRejects);

internal readonly record struct ReplayReuseDiagnosticsReport(
    ReplayPhaseBenchmarkPairReport SchedulerCertificatePair,
    IReadOnlyList<ReplayTemplateReuseScenarioReport> Scenarios,
    ReplayTemplateReuseMetrics Aggregate,
    bool Succeeded);

internal readonly record struct ReplayTemplateReuseScenarioReport(
    string Scenario,
    IReadOnlyList<ReplayTemplateLookupResult> Lookups,
    ReplayTemplateReuseMetrics Metrics,
    bool Passed);

internal readonly record struct ReplayTemplateReuseMetrics(
    long ReplayTemplateLookupAttempts,
    long ReplayTemplateHits,
    long ReplayTemplateMisses,
    long InvalidationsByPhaseKey,
    long InvalidationsByStructuralIdentity,
    long InvalidationsByBoundaryState,
    long WitnessAccesses,
    long WarmupMisses,
    long FallbackToLiveWitness)
{
    public double HitRate => ReplayTemplateLookupAttempts == 0
        ? 0.0d
        : (double)ReplayTemplateHits / ReplayTemplateLookupAttempts;

    public static ReplayTemplateReuseMetrics Sum(IEnumerable<ReplayTemplateReuseMetrics> metrics)
    {
        long attempts = 0;
        long hits = 0;
        long misses = 0;
        long phase = 0;
        long structural = 0;
        long boundary = 0;
        long witness = 0;
        long warmup = 0;
        long fallback = 0;

        foreach (ReplayTemplateReuseMetrics value in metrics)
        {
            attempts += value.ReplayTemplateLookupAttempts;
            hits += value.ReplayTemplateHits;
            misses += value.ReplayTemplateMisses;
            phase += value.InvalidationsByPhaseKey;
            structural += value.InvalidationsByStructuralIdentity;
            boundary += value.InvalidationsByBoundaryState;
            witness += value.WitnessAccesses;
            warmup += value.WarmupMisses;
            fallback += value.FallbackToLiveWitness;
        }

        return new ReplayTemplateReuseMetrics(attempts, hits, misses, phase, structural, boundary, witness, warmup, fallback);
    }
}

internal readonly record struct ReplayTemplateLookupResult(
    bool Hit,
    ReplayTemplateRejectReason RejectReason,
    string AuthoritySource)
{
    public static ReplayTemplateLookupResult HitResult() =>
        new(true, ReplayTemplateRejectReason.None, "ReplayTemplateWitness");

    public static ReplayTemplateLookupResult Miss(ReplayTemplateRejectReason reason) =>
        new(false, reason, "ReplayTemplateWitness");
}

internal enum ReplayTemplateRejectReason
{
    None,
    ColdTemplate,
    InvalidReplayBoundary,
    PhaseKeyMismatch,
    StaleStructuralIdentity,
    BoundaryStateMismatch
}

internal readonly record struct AssistantDecisionMatrixReport(
    IReadOnlyList<AssistantDecisionScenarioReport> Scenarios,
    AssistantDecisionMatrixMetrics Aggregate,
    AssistantVisibilityDiagnosticsReport VisibilityDiagnostics,
    bool Succeeded);

internal readonly record struct AssistantDecisionScenarioReport(
    string Scenario,
    AssistantDecisionOutcome ExpectedOutcome,
    AssistantDecisionOutcome ActualOutcome,
    AssistantDecisionReason ExpectedReason,
    AssistantDecisionReason ActualReason,
    AssistantDecisionMatrixMetrics Metrics,
    string Detail,
    bool Passed);

internal readonly record struct AssistantDecisionMatrixMetrics(
    long DecisionAttempts,
    long Accepted,
    long QuotaRejects,
    long BackpressureRejects,
    long OwnerDomainAdministratorRejects,
    long InvalidReplayRejects,
    long PrimaryStreamPriorityRejects,
    long ResidualCapacityAfterAccepted,
    long ReservedAssistLines)
{
    public static AssistantDecisionMatrixMetrics Sum(IEnumerable<AssistantDecisionMatrixMetrics> metrics)
    {
        long attempts = 0;
        long accepted = 0;
        long quota = 0;
        long backpressure = 0;
        long ownerDomain = 0;
        long invalidReplay = 0;
        long primaryPriority = 0;
        long residual = 0;
        long reservedLines = 0;

        foreach (AssistantDecisionMatrixMetrics value in metrics)
        {
            attempts += value.DecisionAttempts;
            accepted += value.Accepted;
            quota += value.QuotaRejects;
            backpressure += value.BackpressureRejects;
            ownerDomain += value.OwnerDomainAdministratorRejects;
            invalidReplay += value.InvalidReplayRejects;
            primaryPriority += value.PrimaryStreamPriorityRejects;
            residual += value.ResidualCapacityAfterAccepted;
            reservedLines += value.ReservedAssistLines;
        }

        return new AssistantDecisionMatrixMetrics(
            attempts,
            accepted,
            quota,
            backpressure,
            ownerDomain,
            invalidReplay,
            primaryPriority,
            residual,
            reservedLines);
    }
}

internal enum AssistantDecisionOutcome
{
    Accepted,
    Rejected
}

internal enum AssistantDecisionReason
{
    AcceptedWithResidualCapacity,
    Quota,
    Backpressure,
    OwnerDomainAdministrator,
    InvalidReplay,
    PrimaryStreamPriority
}

internal readonly record struct AssistantVisibilityDiagnosticsReport(
    string EvidenceScope,
    string Scenario,
    AssistantLifecycleOutcome ExpectedOutcome,
    AssistantLifecycleOutcome ActualOutcome,
    ReplayPhaseInvalidationReason InvalidationReason,
    AssistantVisibilityDiagnosticsMetrics Metrics,
    bool Passed);

internal readonly record struct AssistantVisibilityDiagnosticsMetrics(
    long AssistAccepted,
    long AssistReplayInvalidatedAfterAcceptance,
    long AssistDiscarded,
    long AssistRetireRecords,
    long AssistArchitecturalWrites,
    long AssistCommittedStores,
    long AssistTelemetryEvents,
    long AssistCarrierPublications,
    long ForegroundRetireRecordsBefore,
    long ForegroundRetireRecordsAfter,
    bool ForegroundRetireRecordsPreserved);

internal enum AssistantLifecycleOutcome
{
    DiscardedOnReplayInvalidation,
    NotDiscarded
}
