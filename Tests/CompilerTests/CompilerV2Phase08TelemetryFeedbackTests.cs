using System.Text.Json;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU_ISE.Tests.TestHelpers;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Diagnostics;
using static YAKSys_Hybrid_CPU.Processor;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

/// <summary>
/// Compiler V2 Phase 08 — Telemetry Feedback Loop tests.
/// Validates profile format, round-trip serialization, graceful error handling,
/// class-pressure-aware scheduling, NOP density feedback, and chunk rebalancing.
/// </summary>
public class CompilerV2Phase08TelemetryFeedbackTests
{
    #region Task 8.1 — Profile model types

    [Fact]
    public void WhenCreatingWorkerPerformanceMetricsThenAllPropertiesSet()
    {
        WorkerPerformanceMetrics metrics = new WorkerPerformanceMetrics(
            WorkerName: "worker_vt1",
            TotalCycles: 500,
            NopDensity: 0.12,
            RejectRate: 0.05,
            BundlesExecuted: 100,
            NopsExecuted: 96);

        Assert.Equal("worker_vt1", metrics.WorkerName);
        Assert.Equal(500, metrics.TotalCycles);
        Assert.Equal(0.12, metrics.NopDensity);
        Assert.Equal(0.05, metrics.RejectRate);
        Assert.Equal(100, metrics.BundlesExecuted);
        Assert.Equal(96, metrics.NopsExecuted);
    }

    [Fact]
    public void WhenCreatingTelemetryProfileThenAllFieldsSet()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfile();

        Assert.Equal("abc123", profile.ProgramHash);
        Assert.Equal(0.1, profile.AverageNopDensity);
        Assert.Equal(0.9, profile.AverageBundleUtilization);
        Assert.Equal(1000, profile.TotalBundlesExecuted);
        Assert.Equal(800, profile.TotalNopsExecuted);
        Assert.Equal(50, profile.ReplayTemplateHits);
        Assert.Equal(10, profile.ReplayTemplateMisses);
    }

    [Fact]
    public void WhenProfileHasWorkerMetricsThenAccessible()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfileWithWorkers();

        Assert.NotNull(profile.WorkerMetrics);
        Assert.True(profile.WorkerMetrics!.ContainsKey("worker_vt1"));
        Assert.Equal(500, profile.WorkerMetrics["worker_vt1"].TotalCycles);
    }

    [Fact]
    public void WhenProfileHasNoWorkerMetricsThenNull()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfile();
        Assert.Null(profile.WorkerMetrics);
    }

    #endregion

    #region Task 8.2 — Telemetry exporter (round-trip)

    [Fact]
    public void WhenSerializingProfileToJsonThenDeserializesCorrectly()
    {
        TypedSlotTelemetryProfile original = CreateSampleProfile();

        string json = TelemetryExporter.SerializeToJson(original);
        TypedSlotTelemetryProfile? deserialized = TelemetryExporter.DeserializeFromJson(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.ProgramHash, deserialized!.ProgramHash);
        Assert.Equal(original.AverageNopDensity, deserialized.AverageNopDensity);
        Assert.Equal(original.AverageBundleUtilization, deserialized.AverageBundleUtilization);
        Assert.Equal(original.TotalBundlesExecuted, deserialized.TotalBundlesExecuted);
        Assert.Equal(original.TotalNopsExecuted, deserialized.TotalNopsExecuted);
        Assert.Equal(original.ReplayTemplateHits, deserialized.ReplayTemplateHits);
        Assert.Equal(original.ReplayTemplateMisses, deserialized.ReplayTemplateMisses);
        Assert.Equal(original.ReplayHitRate, deserialized.ReplayHitRate);
        Assert.Equal(original.FairnessStarvationEvents, deserialized.FairnessStarvationEvents);
    }

    [Fact]
    public void WhenSerializingProfileWithWorkersThenWorkerMetricsPreserved()
    {
        TypedSlotTelemetryProfile original = CreateSampleProfileWithWorkers();

        string json = TelemetryExporter.SerializeToJson(original);
        TypedSlotTelemetryProfile? deserialized = TelemetryExporter.DeserializeFromJson(json);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.WorkerMetrics);
        Assert.True(deserialized.WorkerMetrics!.ContainsKey("worker_vt1"));
        Assert.Equal(500, deserialized.WorkerMetrics["worker_vt1"].TotalCycles);
        Assert.Equal(0.12, deserialized.WorkerMetrics["worker_vt1"].NopDensity);
    }

    [Fact]
    public void WhenSerializingPerClassInjectionsThenPreserved()
    {
        TypedSlotTelemetryProfile original = CreateSampleProfile();

        string json = TelemetryExporter.SerializeToJson(original);
        TypedSlotTelemetryProfile? deserialized = TelemetryExporter.DeserializeFromJson(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.TotalInjectionsPerClass.Count, deserialized!.TotalInjectionsPerClass.Count);
    }

    [Fact]
    public void WhenSerializingProfileWithLoopPhaseProfilesThenProfilesRoundTrip()
    {
        TypedSlotTelemetryProfile original = CreateProfileWithLoopPhaseTelemetry();

        string json = TelemetryExporter.SerializeToJson(original);
        TypedSlotTelemetryProfile? deserialized = TelemetryExporter.DeserializeFromJson(json);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.LoopPhaseProfiles);
        Assert.Single(deserialized.LoopPhaseProfiles!);
        Assert.Equal(0x5200UL, deserialized.LoopPhaseProfiles![0].LoopPcAddress);
    }

    #endregion

    #region Task 8.3 — Profile reader

    [Fact]
    public void WhenCreatingEmptyReaderThenHasProfileIsFalse()
    {
        TelemetryProfileReader reader = TelemetryProfileReader.CreateEmpty();

        Assert.False(reader.HasProfile);
        Assert.Null(reader.Profile);
    }

    [Fact]
    public void WhenCreatingReaderWithProfileThenHasProfileIsTrue()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfile();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.True(reader.HasProfile);
        Assert.NotNull(reader.Profile);
    }

    [Fact]
    public void WhenProfileHashMatchesThenProfileLoaded()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfile();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile, "abc123");

        Assert.True(reader.HasProfile);
    }

    [Fact]
    public void WhenProfileHashMismatchThenProfileIgnored()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfile();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile, "different_hash");

        Assert.False(reader.HasProfile);
    }

    [Fact]
    public void WhenLoadingMissingFileThenFallbackToDefault()
    {
        TelemetryProfileReader reader = TelemetryProfileReader.LoadFromFile(
            "nonexistent_file.typed_slot_profile.json");

        Assert.False(reader.HasProfile);
    }

    [Fact]
    public void WhenLoadingCorruptJsonThenFallbackToDefault()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "{ this is not valid JSON!!!");
            TelemetryProfileReader reader = TelemetryProfileReader.LoadFromFile(tempFile);

            Assert.False(reader.HasProfile);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void WhenLoadingValidProfileFileThenLoaded()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfile();
        string json = TelemetryExporter.SerializeToJson(profile);
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, json);
            TelemetryProfileReader reader = TelemetryProfileReader.LoadFromFile(tempFile);

            Assert.True(reader.HasProfile);
            Assert.Equal("abc123", reader.Profile!.ProgramHash);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void WhenLoadingProfileWithHashMismatchThenIgnored()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfile();
        string json = TelemetryExporter.SerializeToJson(profile);
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, json);
            TelemetryProfileReader reader = TelemetryProfileReader.LoadFromFile(tempFile, "wrong_hash");

            Assert.False(reader.HasProfile);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void WhenQueryingClassPressureWithNoProfileThenReturnsZero()
    {
        TelemetryProfileReader reader = TelemetryProfileReader.CreateEmpty();

        Assert.Equal(0.0, reader.GetClassPressure(SlotClass.AluClass));
    }

    [Fact]
    public void WhenQueryingCertificatePressureWithNoProfileThenReturnsZero()
    {
        TelemetryProfileReader reader = TelemetryProfileReader.CreateEmpty();

        Assert.Equal(0.0, reader.GetCertificatePressureByClass(SlotClass.AluClass));
    }

    [Fact]
    public void WhenQueryingCertificatePressureWithPayloadThenReturnsCertificateRatio()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithCertificatePressure();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.Equal(0.4, reader.GetCertificatePressureByClass(SlotClass.AluClass));
    }

    [Fact]
    public void WhenQueryingCertificateRejectCountWithoutPayloadThenFallsBackToLegacyRejects()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithHighAluPressure();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.Equal(150, reader.GetCertificateRejectCountByClass(SlotClass.AluClass));
    }

    [Fact]
    public void WhenQueryingCertificateRegisterGroupConflictCountWithoutPayloadThenReturnsZero()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfile();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.Equal(0, reader.GetCertificateRegisterGroupConflictCount(2));
    }

    [Fact]
    public void WhenQueryingCertificateRegisterGroupPressureWithPayloadThenReturnsNormalizedShare()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithCertificatePressure();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.Equal(0.75, reader.GetCertificateRegisterGroupPressureByVt(1));
    }

    [Fact]
    public void WhenQueryingPerVtInjectabilityWithoutPhase3PayloadThenReturnsZero()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfile();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.Equal(0.0, reader.GetPerVtInjectabilityRate(1));
    }

    [Fact]
    public void WhenQueryingPerVtInjectabilityWithPayloadThenReturnsDerivedRatio()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithPerVtInjectabilityTelemetry();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.Equal(0.8, reader.GetPerVtInjectabilityRate(1));
    }

    [Fact]
    public void WhenQueryingPerVtInjectabilityPressureWithPayloadThenReturnsRejectRatio()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithPerVtInjectabilityTelemetry();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.Equal(0.2, reader.GetPerVtInjectabilityPressure(1));
    }

    [Fact]
    public void WhenQueryingBackendResourceShapingPressureForCoordinatorThenReturnsZero()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithWave4BackendShapingTelemetry();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.Equal(0.0, reader.GetCertificateRegisterGroupPressureForBackendShaping(0, treatAsCoordinatorPath: true));
        Assert.Equal(0.0, reader.GetBackendResourceShapingPressure(0, treatAsCoordinatorPath: true));
    }

    [Fact]
    public void WhenQueryingBackendResourceShapingPressureForWorkerThenReturnsBoundedAdvisorySignal()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithWave4BackendShapingTelemetry();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.Equal(0.75, reader.GetCertificateRegisterGroupPressureForBackendShaping(1, treatAsCoordinatorPath: false));
        Assert.Equal(0.75, reader.GetBackendResourceShapingPressure(1, treatAsCoordinatorPath: false));
    }

    [Fact]
    public void WhenQueryingBankAwareSignalsWithoutPayloadThenReaderFallsBackToZero()
    {
        TelemetryProfileReader reader = TelemetryProfileReader.Create(CreateSampleProfile());

        Assert.Equal(0.0, reader.GetPeakBankPendingRejectPressure());
        Assert.Equal(0.0, reader.GetAdvisoryMemoryClusteringSignal());
        Assert.Equal(0.0, reader.GetAdvisoryBankPressureSignal());
    }

    [Fact]
    public void WhenQueryingBankAwareSignalsWithAllZeroPayloadThenReaderFallsBackToZero()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfile() with
        {
            BankPendingRejectsPerBank = new Dictionary<int, long>
            {
                [0] = 0,
                [1] = 0,
                [2] = 0
            },
            MemoryClusteringEventCount = 0,
            BankConflictStallCycles = 0
        };
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.Equal(0.0, reader.GetPeakBankPendingRejectPressure());
        Assert.Equal(0.0, reader.GetAdvisoryMemoryClusteringSignal());
        Assert.Equal(0.0, reader.GetAdvisoryBankPressureSignal());
    }

    [Fact]
    public void WhenQueryingBankAwareSignalsWithPayloadThenReaderConsolidatesAdvisoryPressure()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfile() with
        {
            BankPendingRejectsPerBank = new Dictionary<int, long>
            {
                [0] = 12,
                [1] = 6,
                [2] = 2
            },
            MemoryClusteringEventCount = 120,
            BankConflictStallCycles = 40
        };
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.Equal(0.6, reader.GetPeakBankPendingRejectPressure());
        Assert.Equal(0.12, reader.GetAdvisoryMemoryClusteringSignal());
        Assert.Equal(0.6, reader.GetAdvisoryBankPressureSignal());
    }

    [Fact]
    public void WhenReadingExporterBuiltCertificateTelemetryThenWave4CertificateSignalsRemainContinuous()
    {
        MicroOpScheduler scheduler = new MicroOpScheduler
        {
            TypedSlotEnabled = true
        };
        scheduler.TestSetPhaseMetrics(new SchedulerPhaseMetrics
        {
            CertificateRejectByAluClass = 2,
            CertificateRejectByLsuClass = 1,
            RejectionsVT1 = 2,
            RejectionsVT2 = 1,
            RegGroupConflictsVT1 = 3,
            RegGroupConflictsVT2 = 1,
            CertificateRegGroupConflictVT1 = 3,
            CertificateRegGroupConflictVT2 = 1
        });

        InjectCandidateViaScheduler(scheduler, CreateTelemetryAluCandidate(1, 20));
        InjectCandidateViaScheduler(scheduler, CreateTelemetryAluCandidate(1, 24));

        TelemetryProfileReader reader = CreateRoundTrippedReaderFromExporter(scheduler, "wave4-v4a");

        Assert.Equal(0.5, reader.GetCertificatePressureByClass(SlotClass.AluClass));
        Assert.Equal(2, reader.GetCertificateRejectCountByClass(SlotClass.AluClass));
        Assert.Equal(3, reader.GetCertificateRegisterGroupConflictCount(1));
        Assert.Equal(0.75, reader.GetCertificateRegisterGroupPressureByVt(1));
    }

    [Fact]
    public void WhenReadingExporterBuiltPerVtTelemetryThenWave4InjectabilitySignalsRemainContinuous()
    {
        MicroOpScheduler scheduler = new MicroOpScheduler
        {
            TypedSlotEnabled = true
        };
        scheduler.TestSetPhaseMetrics(new SchedulerPhaseMetrics
        {
            RejectionsVT1 = 1,
            RejectionsVT3 = 2
        });

        InjectCandidateViaScheduler(scheduler, CreateTelemetryAluCandidate(1, 32));
        InjectCandidateViaScheduler(scheduler, CreateTelemetryAluCandidate(1, 36));
        InjectCandidateViaScheduler(scheduler, CreateTelemetryAluCandidate(1, 40));
        InjectCandidateViaScheduler(scheduler, CreateTelemetryAluCandidate(1, 44));
        InjectCandidateViaScheduler(scheduler, CreateTelemetryAluCandidate(3, 48));
        InjectCandidateViaScheduler(scheduler, CreateTelemetryAluCandidate(3, 52));

        TelemetryProfileReader reader = CreateRoundTrippedReaderFromExporter(scheduler, "wave4-v4c");

        Assert.Equal(0.8, reader.GetPerVtInjectabilityRate(1));
        Assert.Equal(0.5, reader.GetPerVtInjectabilityRate(3));
        Assert.Equal(0.0, reader.GetPerVtInjectabilityRate(2));
    }

    [Fact]
    public void WhenReadingExporterBuiltPipelinedTelemetryThenWave4SignalsRemainContinuous()
    {
        MicroOpScheduler scheduler = new MicroOpScheduler
        {
            TypedSlotEnabled = true,
            PipelinedFspEnabled = true
        };
        scheduler.TestSetPhaseMetrics(new SchedulerPhaseMetrics
        {
            CertificateRejectByAluClass = 2,
            RejectionsVT1 = 1,
            RegGroupConflictsVT1 = 2,
            CertificateRegGroupConflictVT1 = 2
        });

        InjectCandidateViaPipelinedScheduler(scheduler, CreateTelemetryAluCandidate(1, 12));
        InjectCandidateViaPipelinedScheduler(scheduler, CreateTelemetryAluCandidate(1, 16));
        InjectCandidateViaPipelinedScheduler(scheduler, CreateTelemetryAluCandidate(1, 20));

        TelemetryProfileReader reader = CreateRoundTrippedReaderFromExporter(scheduler, "wave4-pipelined");

        Assert.Equal(0.4, reader.GetCertificatePressureByClass(SlotClass.AluClass));
        Assert.Equal(0.75, reader.GetPerVtInjectabilityRate(1));
        Assert.Equal(2, reader.GetCertificateRegisterGroupConflictCount(1));
    }

    [Fact]
    public void WhenQueryingCrossDomainRejectCountWithoutPayloadThenReturnsZero()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfile();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.Equal(0, reader.GetCrossDomainRejectCount());
    }

    [Fact]
    public void WhenQueryingCrossDomainRejectCountWithPayloadThenReturnsExportedValue()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithCrossDomainRejectTelemetry();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.Equal(9, reader.GetCrossDomainRejectCount());
    }

    [Fact]
    public void WhenQueryingLoopPhaseProfilesWithoutPayloadThenReaderReturnsEmpty()
    {
        TelemetryProfileReader reader = TelemetryProfileReader.CreateEmpty();

        Assert.Empty(reader.GetLoopPhaseProfiles());
        Assert.Equal(0.0, reader.GetLoopOverallClassVariance(0x5200));
        Assert.Equal(0.0, reader.GetLoopTemplateReuseRate(0x5200));
    }

    [Fact]
    public void WhenQueryingLoopPhaseProfilesWithPayloadThenReaderReturnsLoopSignals()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithLoopPhaseTelemetry();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.Single(reader.GetLoopPhaseProfiles());
        Assert.Equal(0.625, reader.GetLoopOverallClassVariance(0x5200));
        Assert.Equal(0.75, reader.GetLoopTemplateReuseRate(0x5200));
    }

    [Fact]
    public void WhenQueryingNopDensityWithNoProfileThenReturnsZero()
    {
        TelemetryProfileReader reader = TelemetryProfileReader.CreateEmpty();

        Assert.Equal(0.0, reader.GetNopDensity());
    }

    [Fact]
    public void WhenQueryingNopDensityWithProfileThenReturnsProfileValue()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfile();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.Equal(0.1, reader.GetNopDensity());
    }

    [Fact]
    public void WhenQueryingWorkerMetricsThenReturnsCorrectData()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfileWithWorkers();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        HybridCPU.Compiler.Core.IR.Telemetry.Contracts.WorkerPerformanceMetrics? metrics = reader.GetWorkerMetrics("worker_vt1");
        Assert.NotNull(metrics);
        Assert.Equal(500, metrics!.TotalCycles);
    }

    [Fact]
    public void WhenQueryingMissingWorkerThenReturnsNull()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfileWithWorkers();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        HybridCPU.Compiler.Core.IR.Telemetry.Contracts.WorkerPerformanceMetrics? metrics = reader.GetWorkerMetrics("nonexistent_worker");
        Assert.Null(metrics);
    }

    [Fact]
    public void WhenQueryingReplayHitRateThenReturnsProfileValue()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfile();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        double hitRate = reader.GetReplayHitRate();
        Assert.True(hitRate > 0.0);
    }

    #endregion

    #region Task 8.4 — Class-pressure-aware scheduling

    [Fact]
    public void WhenSchedulerHasNoProfileThenPenaltyIsZero()
    {
        HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler();
        scheduler.UseProfileGuidedScheduling = false;

        int penalty = scheduler.GetClassPressurePenalty(SlotClass.AluClass);
        Assert.Equal(0, penalty);
    }

    [Fact]
    public void WhenSchedulerHasProfileWithLowPressureThenNoPenalty()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfile();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler
        {
            UseProfileGuidedScheduling = true,
            ProfileReader = reader
        };

        int penalty = scheduler.GetClassPressurePenalty(SlotClass.AluClass);
        Assert.Equal(0, penalty);
    }

    [Fact]
    public void WhenSchedulerHasProfileWithHighAluPressureThenPenaltyPositive()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithHighAluPressure();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler
        {
            UseProfileGuidedScheduling = true,
            ProfileReader = reader
        };

        int penalty = scheduler.GetClassPressurePenalty(SlotClass.AluClass);
        Assert.True(penalty > 0);
    }

    [Fact]
    public void WhenProfileGuidedSchedulingDisabledThenNoPenaltyEvenWithHighPressure()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithHighAluPressure();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler
        {
            UseProfileGuidedScheduling = false,
            ProfileReader = reader
        };

        int penalty = scheduler.GetClassPressurePenalty(SlotClass.AluClass);
        Assert.Equal(0, penalty);
    }

    [Fact]
    public void WhenUseProfileGuidedSchedulingDefaultsThenFalse()
    {
        HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler();
        Assert.False(scheduler.UseProfileGuidedScheduling);
    }

    #endregion

    #region Task 8.5 — NOP density feedback

    [Fact]
    public void WhenEvaluatorHasNoProfileThenQualityGapIsZero()
    {
        HybridCpuBundlingQualityEvaluator evaluator = new HybridCpuBundlingQualityEvaluator();

        IrProgramBundlingQuality programQuality = CreateMinimalProgramQuality(bundleCount: 10, nopSlotCount: 10);
        double gap = evaluator.EvaluateQualityGap(programQuality);

        Assert.Equal(0.0, gap);
    }

    [Fact]
    public void WhenRuntimeNopDensityHigherThanCompilerThenPositiveGap()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithHighNopDensity(0.5);
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        HybridCpuBundlingQualityEvaluator evaluator = new HybridCpuBundlingQualityEvaluator { ProfileReader = reader };

        // Compiler saw 10 NOPs in 10 bundles × 8 slots = 0.125 density
        IrProgramBundlingQuality programQuality = CreateMinimalProgramQuality(bundleCount: 10, nopSlotCount: 10);
        double gap = evaluator.EvaluateQualityGap(programQuality);

        Assert.True(gap > 0.0);
    }

    [Fact]
    public void WhenRuntimeNopDensityLowerThanCompilerThenZeroGap()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithHighNopDensity(0.05);
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        HybridCpuBundlingQualityEvaluator evaluator = new HybridCpuBundlingQualityEvaluator { ProfileReader = reader };

        // Compiler saw 20 NOPs in 10 bundles × 8 slots = 0.25 density
        IrProgramBundlingQuality programQuality = CreateMinimalProgramQuality(bundleCount: 10, nopSlotCount: 20);
        double gap = evaluator.EvaluateQualityGap(programQuality);

        Assert.Equal(0.0, gap);
    }

    [Fact]
    public void WhenGetRuntimeNopDensityBaselineWithNoProfileThenZero()
    {
        HybridCpuBundlingQualityEvaluator evaluator = new HybridCpuBundlingQualityEvaluator();
        Assert.Equal(0.0, evaluator.GetRuntimeNopDensityBaseline());
    }

    [Fact]
    public void WhenGetRuntimeNopDensityBaselineWithProfileThenReturnsProfileValue()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithHighNopDensity(0.35);
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        HybridCpuBundlingQualityEvaluator evaluator = new HybridCpuBundlingQualityEvaluator { ProfileReader = reader };
        Assert.Equal(0.35, evaluator.GetRuntimeNopDensityBaseline());
    }

    #endregion

    #region Task 8.6 — Decomposition chunk size tuning

    [Fact]
    public void WhenPlannerHasNoProfileThenDefaultPartition()
    {
        ParallelRegionInfo region = CreateSimpleForLoopRegion(0, 100, 1);
        PartitionPlanner planner = new PartitionPlanner();

        ChunkPlan? plan = planner.PlanPartition(region);

        Assert.NotNull(plan);
        Assert.Equal(3, plan!.WorkerCount);
    }

    [Fact]
    public void WhenPlannerHasProfileWithHighRejectRateThenFewerWorkers()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithHighWorkerRejectRate();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        ParallelRegionInfo region = CreateSimpleForLoopRegion(0, 100, 1);
        PartitionPlanner planner = new PartitionPlanner
        {
            UseProfileGuidedDecomposition = true,
            ProfileReader = reader
        };

        ChunkPlan? plan = planner.PlanPartition(region);

        Assert.NotNull(plan);
        Assert.True(plan!.WorkerCount < 3);
    }

    [Fact]
    public void WhenPlannerHasProfileWithLowRejectRateThenMaxWorkers()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithLowWorkerRejectRate();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        ParallelRegionInfo region = CreateSimpleForLoopRegion(0, 100, 1);
        PartitionPlanner planner = new PartitionPlanner
        {
            UseProfileGuidedDecomposition = true,
            ProfileReader = reader
        };

        ChunkPlan? plan = planner.PlanPartition(region);

        Assert.NotNull(plan);
        Assert.Equal(3, plan!.WorkerCount);
    }

    [Fact]
    public void WhenProfileGuidedDecompositionDisabledThenDefaultPartition()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithHighWorkerRejectRate();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        ParallelRegionInfo region = CreateSimpleForLoopRegion(0, 100, 1);
        PartitionPlanner planner = new PartitionPlanner
        {
            UseProfileGuidedDecomposition = false,
            ProfileReader = reader
        };

        ChunkPlan? plan = planner.PlanPartition(region);

        Assert.NotNull(plan);
        Assert.Equal(3, plan!.WorkerCount);
    }

    [Fact]
    public void WhenUseProfileGuidedDecompositionDefaultsThenFalse()
    {
        PartitionPlanner planner = new PartitionPlanner();
        Assert.False(planner.UseProfileGuidedDecomposition);
    }

    [Fact]
    public void WhenImbalanceRatioWithNoProfileThenZero()
    {
        PartitionPlanner planner = new PartitionPlanner();
        Assert.Equal(0.0, planner.GetWorkerImbalanceRatio());
    }

    [Fact]
    public void WhenImbalanceRatioWithImbalancedWorkersThenPositive()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithImbalancedWorkers();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        PartitionPlanner planner = new PartitionPlanner
        {
            UseProfileGuidedDecomposition = true,
            ProfileReader = reader
        };

        double imbalance = planner.GetWorkerImbalanceRatio();
        Assert.True(imbalance > 0.0);
    }

    #endregion

    #region Task 8.7 — Integration and determinism

    [Fact]
    public void WhenDeserializingNullJsonThenReturnsNull()
    {
        TypedSlotTelemetryProfile? result = TelemetryExporter.DeserializeFromJson(null!);
        Assert.Null(result);
    }

    [Fact]
    public void WhenDeserializingEmptyStringThenReturnsNull()
    {
        TypedSlotTelemetryProfile? result = TelemetryExporter.DeserializeFromJson("");
        Assert.Null(result);
    }

    [Fact]
    public void WhenDeserializingMalformedJsonThenReturnsNull()
    {
        TypedSlotTelemetryProfile? result = TelemetryExporter.DeserializeFromJson("{broken json}}");
        Assert.Null(result);
    }

    [Fact]
    public void WhenSameProfileSerializedTwiceThenJsonIdentical()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfile();

        string json1 = TelemetryExporter.SerializeToJson(profile);
        string json2 = TelemetryExporter.SerializeToJson(profile);

        Assert.Equal(json1, json2);
    }

    [Fact]
    public void WhenProfileRoundTrippedThenFieldEquality()
    {
        TypedSlotTelemetryProfile original = CreateSampleProfileWithWorkers();

        string json = TelemetryExporter.SerializeToJson(original);
        TypedSlotTelemetryProfile? restored = TelemetryExporter.DeserializeFromJson(json);

        Assert.NotNull(restored);
        Assert.Equal(original.ProgramHash, restored!.ProgramHash);
        Assert.Equal(original.AverageNopDensity, restored.AverageNopDensity);
        Assert.Equal(original.AverageBundleUtilization, restored.AverageBundleUtilization);
        Assert.Equal(original.TotalBundlesExecuted, restored.TotalBundlesExecuted);
        Assert.Equal(original.TotalNopsExecuted, restored.TotalNopsExecuted);
        Assert.Equal(original.ReplayTemplateHits, restored.ReplayTemplateHits);
        Assert.Equal(original.ReplayTemplateMisses, restored.ReplayTemplateMisses);
        Assert.Equal(original.ReplayHitRate, restored.ReplayHitRate);
        Assert.Equal(original.FairnessStarvationEvents, restored.FairnessStarvationEvents);
    }

    [Fact]
    public void WhenLoadingProfileFromEmptyPathThenFallback()
    {
        TelemetryProfileReader reader = TelemetryProfileReader.LoadFromFile("");
        Assert.False(reader.HasProfile);
    }

    [Fact]
    public void WhenGetAllWorkerMetricsWithNoProfileThenEmpty()
    {
        TelemetryProfileReader reader = TelemetryProfileReader.CreateEmpty();
        IReadOnlyDictionary<string, HybridCPU.Compiler.Core.IR.Telemetry.Contracts.WorkerPerformanceMetrics> metrics = reader.GetAllWorkerMetrics();
        Assert.Empty(metrics);
    }

    [Fact]
    public void WhenGetRejectRateWithNoProfileThenZero()
    {
        TelemetryProfileReader reader = TelemetryProfileReader.CreateEmpty();
        Assert.Equal(0.0, reader.GetRejectRate(TypedSlotRejectReason.StaticClassOvercommit));
    }

    [Fact]
    public void WhenQueryingHazardEffectTelemetryThenCountsAndRatesReturned()
    {
        TypedSlotTelemetryProfile profile = CreateSampleProfile() with
        {
            HazardRegisterDataCount = 25,
            HazardMemoryBankCount = 10,
            HazardControlFlowCount = 5,
            HazardSystemBarrierCount = 2,
            HazardPinnedLaneCount = 1
        };

        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.Equal(25, reader.GetHazardEffectCount(HazardEffectKind.RegisterData));
        Assert.Equal(10, reader.GetHazardEffectCount(HazardEffectKind.MemoryBank));
        Assert.Equal(5, reader.GetHazardEffectCount(HazardEffectKind.ControlFlow));
        Assert.Equal(2, reader.GetHazardEffectCount(HazardEffectKind.SystemBarrier));
        Assert.Equal(1, reader.GetHazardEffectCount(HazardEffectKind.PinnedLane));
        Assert.Equal(0.025, reader.GetHazardEffectRate(HazardEffectKind.RegisterData));
        Assert.Equal(0.01, reader.GetHazardEffectRate(HazardEffectKind.MemoryBank));
    }

    [Fact]
    public void WhenQueryingCrossDomainRejectRateWithoutPayloadThenReturnsZero()
    {
        TelemetryProfileReader reader = TelemetryProfileReader.CreateEmpty();

        Assert.Equal(0.0, reader.GetCrossDomainRejectRate());
    }

    [Fact]
    public void WhenQueryingCrossDomainRejectRateWithPayloadThenReturnsNormalizedValue()
    {
        TelemetryProfileReader reader = TelemetryProfileReader.Create(CreateProfileWithCrossDomainRejectTelemetry());

        Assert.Equal(0.009, reader.GetCrossDomainRejectRate());
    }

    #endregion

    #region Helpers

    private static TypedSlotTelemetryProfile CreateSampleProfile()
    {
        return new TypedSlotTelemetryProfile(
            ProgramHash: "abc123",
            TotalInjectionsPerClass: new Dictionary<SlotClass, long>
            {
                [SlotClass.AluClass] = 500,
                [SlotClass.LsuClass] = 200,
                [SlotClass.DmaStreamClass] = 50,
                [SlotClass.BranchControl] = 100,
                [SlotClass.SystemSingleton] = 10
            },
            TotalRejectsPerClass: new Dictionary<SlotClass, long>
            {
                [SlotClass.AluClass] = 10,
                [SlotClass.LsuClass] = 5,
                [SlotClass.DmaStreamClass] = 2,
                [SlotClass.BranchControl] = 1,
                [SlotClass.SystemSingleton] = 0
            },
            RejectsByReason: new Dictionary<TypedSlotRejectReason, long>
            {
                [TypedSlotRejectReason.StaticClassOvercommit] = 3,
                [TypedSlotRejectReason.DynamicClassExhaustion] = 7,
                [TypedSlotRejectReason.PinnedLaneConflict] = 2
            },
            AverageNopDensity: 0.1,
            AverageBundleUtilization: 0.9,
            TotalBundlesExecuted: 1000,
            TotalNopsExecuted: 800,
            ReplayTemplateHits: 50,
            ReplayTemplateMisses: 10,
            ReplayHitRate: 50.0 / 60.0,
            FairnessStarvationEvents: 3,
            PerVtInjectionCounts: new Dictionary<int, long>
            {
                [0] = 100,
                [1] = 200,
                [2] = 150,
                [3] = 120
            },
            WorkerMetrics: null);
    }

    private static TelemetryProfileReader CreateRoundTrippedReaderFromExporter(MicroOpScheduler scheduler, string programHash)
    {
        TypedSlotTelemetryProfile profile = TelemetryExporter.BuildProfile(scheduler, programHash);
        string json = TelemetryExporter.SerializeToJson(profile);
        TypedSlotTelemetryProfile? roundTripped = TelemetryExporter.DeserializeFromJson(json);
        Assert.NotNull(roundTripped);
        return TelemetryProfileReader.Create(roundTripped!);
    }

    private static void InjectCandidateViaScheduler(MicroOpScheduler scheduler, MicroOp candidate)
    {
        scheduler.NominateSmtCandidate(candidate.VirtualThreadId, candidate);
        scheduler.PackBundleIntraCoreSmt(new MicroOp[8], ownerVirtualThreadId: 0, localCoreId: 0, eligibleVirtualThreadMask: (byte)(1 << candidate.VirtualThreadId));
    }

    private static void InjectCandidateViaPipelinedScheduler(MicroOpScheduler scheduler, MicroOp candidate)
    {
        scheduler.NominateSmtCandidate(candidate.VirtualThreadId, candidate);
        scheduler.PackBundleIntraCoreSmt(new MicroOp[8], ownerVirtualThreadId: 0, localCoreId: 0, eligibleVirtualThreadMask: (byte)(1 << candidate.VirtualThreadId));
        scheduler.PackBundleIntraCoreSmt(new MicroOp[8], ownerVirtualThreadId: 0, localCoreId: 0, eligibleVirtualThreadMask: (byte)(1 << candidate.VirtualThreadId));
    }

    private static MicroOp CreateTelemetryAluCandidate(int vtId, ushort registerBase)
    {
        MicroOp candidate = MicroOpTestHelper.CreateScalarALU(
            vtId,
            destReg: registerBase,
            src1Reg: (ushort)(registerBase + 1),
            src2Reg: (ushort)(registerBase + 2));
        candidate.Placement = candidate.Placement with
        {
            RequiredSlotClass = SlotClass.AluClass,
            PinningKind = SlotPinningKind.ClassFlexible
        };
        return candidate;
    }

    private static TypedSlotTelemetryProfile CreateProfileWithCrossDomainRejectTelemetry()
    {
        return CreateSampleProfile() with
        {
            CrossDomainRejectCount = 9
        };
    }

    private static TypedSlotTelemetryProfile CreateProfileWithLoopPhaseTelemetry()
    {
        return CreateSampleProfile() with
        {
            LoopPhaseProfiles =
            [
                new LoopPhaseClassProfile(
                    LoopPcAddress: 0x5200,
                    IterationsSampled: 12,
                    AluFreeVariance: 0.5,
                    LsuFreeVariance: 1.25,
                    DmaStreamFreeVariance: 0.0,
                    BranchControlFreeVariance: 0.5,
                    SystemSingletonFreeVariance: 0.875,
                    OverallClassVariance: 0.625,
                    TemplateReuseRate: 0.75)
            ]
        };
    }

    private static TypedSlotTelemetryProfile CreateSampleProfileWithWorkers()
    {
        return CreateSampleProfile() with
        {
            WorkerMetrics = new Dictionary<string, WorkerPerformanceMetrics>
            {
                ["worker_vt1"] = new("worker_vt1", 500, 0.12, 0.05, 100, 96),
                ["worker_vt2"] = new("worker_vt2", 480, 0.10, 0.04, 98, 78),
                ["worker_vt3"] = new("worker_vt3", 520, 0.15, 0.06, 102, 122)
            }
        };
    }

    private static TypedSlotTelemetryProfile CreateProfileWithWave4BackendShapingTelemetry()
    {
        return CreateProfileWithPerVtInjectabilityTelemetry() with
        {
            CertificatePressure = new CertificatePressureMetrics(
                new Dictionary<SlotClass, long>
                {
                    [SlotClass.AluClass] = 40,
                    [SlotClass.LsuClass] = 10,
                    [SlotClass.BranchControl] = 5
                },
                new Dictionary<int, long>
                {
                    [1] = 3,
                    [2] = 1
                })
        };
    }

    private static TypedSlotTelemetryProfile CreateProfileWithHighAluPressure()
    {
        return CreateSampleProfile() with
        {
            TotalInjectionsPerClass = new Dictionary<SlotClass, long>
            {
                [SlotClass.AluClass] = 100,
                [SlotClass.LsuClass] = 200,
                [SlotClass.DmaStreamClass] = 50,
                [SlotClass.BranchControl] = 100,
                [SlotClass.SystemSingleton] = 10
            },
            TotalRejectsPerClass = new Dictionary<SlotClass, long>
            {
                [SlotClass.AluClass] = 150, // 60% reject rate
                [SlotClass.LsuClass] = 5,
                [SlotClass.DmaStreamClass] = 2,
                [SlotClass.BranchControl] = 1,
                [SlotClass.SystemSingleton] = 0
            }
        };
    }

    private static TypedSlotTelemetryProfile CreateProfileWithHighNopDensity(double density)
    {
        return CreateSampleProfile() with
        {
            AverageNopDensity = density,
            AverageBundleUtilization = 1.0 - density
        };
    }

    private static TypedSlotTelemetryProfile CreateProfileWithCertificatePressure()
    {
        return CreateSampleProfile() with
        {
            TotalInjectionsPerClass = new Dictionary<SlotClass, long>
            {
                [SlotClass.AluClass] = 60,
                [SlotClass.LsuClass] = 30,
                [SlotClass.DmaStreamClass] = 10,
                [SlotClass.BranchControl] = 20,
                [SlotClass.SystemSingleton] = 5
            },
            TotalRejectsPerClass = new Dictionary<SlotClass, long>
            {
                [SlotClass.AluClass] = 40,
                [SlotClass.LsuClass] = 10,
                [SlotClass.DmaStreamClass] = 0,
                [SlotClass.BranchControl] = 5,
                [SlotClass.SystemSingleton] = 0
            },
            CertificatePressure = new CertificatePressureMetrics(
                new Dictionary<SlotClass, long>
                {
                    [SlotClass.AluClass] = 40,
                    [SlotClass.LsuClass] = 10,
                    [SlotClass.BranchControl] = 5
                },
                new Dictionary<int, long>
                {
                    [1] = 3,
                    [2] = 1
                })
        };
    }

    private static TypedSlotTelemetryProfile CreateProfileWithPerVtInjectabilityTelemetry()
    {
        return CreateSampleProfile() with
        {
            PerVtRejectionCounts = new Dictionary<int, long>
            {
                [0] = 10,
                [1] = 50,
                [2] = 30,
                [3] = 0
            },
            PerVtRegGroupConflicts = new Dictionary<int, long>
            {
                [1] = 6,
                [2] = 2
            }
        };
    }

    private static TypedSlotTelemetryProfile CreateProfileWithHighWorkerRejectRate()
    {
        return CreateSampleProfile() with
        {
            WorkerMetrics = new Dictionary<string, WorkerPerformanceMetrics>
            {
                ["worker_vt1"] = new("worker_vt1", 500, 0.12, 0.35, 100, 96),
                ["worker_vt2"] = new("worker_vt2", 480, 0.10, 0.40, 98, 78),
                ["worker_vt3"] = new("worker_vt3", 520, 0.15, 0.30, 102, 122)
            }
        };
    }

    private static TypedSlotTelemetryProfile CreateProfileWithLowWorkerRejectRate()
    {
        return CreateSampleProfile() with
        {
            WorkerMetrics = new Dictionary<string, WorkerPerformanceMetrics>
            {
                ["worker_vt1"] = new("worker_vt1", 500, 0.12, 0.05, 100, 96),
                ["worker_vt2"] = new("worker_vt2", 480, 0.10, 0.04, 98, 78),
                ["worker_vt3"] = new("worker_vt3", 520, 0.15, 0.06, 102, 122)
            }
        };
    }

    private static TypedSlotTelemetryProfile CreateProfileWithImbalancedWorkers()
    {
        return CreateSampleProfile() with
        {
            WorkerMetrics = new Dictionary<string, WorkerPerformanceMetrics>
            {
                ["worker_vt1"] = new("worker_vt1", 200, 0.12, 0.05, 100, 96),
                ["worker_vt2"] = new("worker_vt2", 800, 0.10, 0.04, 98, 78),
                ["worker_vt3"] = new("worker_vt3", 500, 0.15, 0.06, 102, 122)
            }
        };
    }

    private static ParallelRegionInfo CreateSimpleForLoopRegion(long start, long end, long step)
    {
        return new ParallelRegionInfo(
            StartInstructionIndex: 0,
            EndInstructionIndex: 10,
            Kind: IrParallelKind.ForLoop,
            InductionVariableRegister: 1,
            IterationStart: start,
            IterationEnd: end,
            IterationStep: step,
            SharedReadRegisters: [],
            SharedWriteRegisters: [],
            PrivateRegisters: [],
            Reduction: null);
    }

    private static IrProgramBundlingQuality CreateMinimalProgramQuality(int bundleCount, int nopSlotCount)
    {
        return new IrProgramBundlingQuality(
            BlockQualities: [],
            BundleCount: bundleCount,
            IssuedInstructionCount: bundleCount * 8 - nopSlotCount,
            NopSlotCount: nopSlotCount,
            CompactBundleCount: bundleCount,
            OrderPreservingBundleCount: bundleCount,
            OccupiedSlotSpanSum: 0,
            InternalGapCount: 0,
            LargestInternalGapSum: 0,
            OrderInversionCount: 0,
            LeadingEmptySlotCount: 0,
            TrailingEmptySlotCount: 0,
            SlotIndexSum: 0,
            ConstrainedSlotDisplacementCost: 0,
            CrossBundleOverlappingLaneCount: 0,
            CrossBundleReusedLaneCount: 0,
            CrossBundleSlotDriftCost: 0,
            PlacementSearchEvaluatedCount: 0,
            PlacementSearchParetoOptimalCount: 0,
            PlacementSearchDominatedCount: 0,
            AmbiguousBundleCount: 0);
    }

    #endregion
}
