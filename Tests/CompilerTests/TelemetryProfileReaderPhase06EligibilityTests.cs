using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR.Telemetry;
using Xunit;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Diagnostics;

namespace HybridCPU_ISE.Tests.CompilerTests;

public class TelemetryProfileReaderPhase06EligibilityTests
{
    [Fact]
    public void WhenReaderHasNoProfileThenEligibilityTelemetryDefaultsToZero()
    {
        TelemetryProfileReader reader = TelemetryProfileReader.CreateEmpty();

        Assert.False(reader.HasEligibilityTelemetry);
        Assert.Equal(0, reader.GetEligibilityMaskedCycles());
        Assert.Equal(0, reader.GetEligibilityMaskedReadyCandidates());
        Assert.Equal(0.0, reader.GetEligibilityMaskedReadyCandidatesPerMaskedCycle());
        Assert.False(reader.TryGetLastEligibilitySnapshot(out _));
    }

    [Fact]
    public void WhenProfileHasEligibilityTelemetryThenReaderReturnsCountsAndSnapshot()
    {
        TypedSlotTelemetryProfile profile = CreateProfileWithEligibilityTelemetry();
        TelemetryProfileReader reader = TelemetryProfileReader.Create(profile);

        Assert.True(reader.HasEligibilityTelemetry);
        Assert.Equal(4, reader.GetEligibilityMaskedCycles());
        Assert.Equal(6, reader.GetEligibilityMaskedReadyCandidates());
        Assert.Equal(1.5, reader.GetEligibilityMaskedReadyCandidatesPerMaskedCycle());
        Assert.True(reader.TryGetLastEligibilitySnapshot(out EligibilityTelemetrySnapshot snapshot));
        Assert.Equal((byte)0x0F, snapshot.RequestedMask);
        Assert.Equal((byte)0x05, snapshot.NormalizedMask);
        Assert.Equal((byte)0x07, snapshot.ReadyPortMask);
        Assert.Equal((byte)0x05, snapshot.VisibleReadyMask);
        Assert.Equal((byte)0x02, snapshot.MaskedReadyMask);
    }

    [Fact]
    public void WhenEligibilityTelemetryRoundTripsThroughJsonThenReaderPreservesIt()
    {
        string json = TelemetryExporter.SerializeToJson(CreateProfileWithEligibilityTelemetry());
        TypedSlotTelemetryProfile? roundTripped = TelemetryExporter.DeserializeFromJson(json);
        Assert.NotNull(roundTripped);

        TelemetryProfileReader reader = TelemetryProfileReader.Create(roundTripped!);

        Assert.True(reader.HasEligibilityTelemetry);
        Assert.True(reader.TryGetLastEligibilitySnapshot(out EligibilityTelemetrySnapshot snapshot));
        Assert.Equal((byte)0x02, snapshot.MaskedReadyMask);
    }

    private static TypedSlotTelemetryProfile CreateProfileWithEligibilityTelemetry()
    {
        return new TypedSlotTelemetryProfile(
            ProgramHash: "phase06-eligibility",
            TotalInjectionsPerClass: new Dictionary<SlotClass, long>(),
            TotalRejectsPerClass: new Dictionary<SlotClass, long>(),
            RejectsByReason: new Dictionary<TypedSlotRejectReason, long>(),
            AverageNopDensity: 0.0,
            AverageBundleUtilization: 1.0,
            TotalBundlesExecuted: 16,
            TotalNopsExecuted: 0,
            ReplayTemplateHits: 0,
            ReplayTemplateMisses: 0,
            ReplayHitRate: 0.0,
            FairnessStarvationEvents: 0,
            PerVtInjectionCounts: new Dictionary<int, long>(),
            WorkerMetrics: null)
        {
            EligibilityMaskedCycles = 4,
            EligibilityMaskedReadyCandidates = 6,
            LastEligibilityRequestedMask = 0x0F,
            LastEligibilityNormalizedMask = 0x05,
            LastEligibilityReadyPortMask = 0x07,
            LastEligibilityVisibleReadyMask = 0x05,
            LastEligibilityMaskedReadyMask = 0x02
        };
    }
}
