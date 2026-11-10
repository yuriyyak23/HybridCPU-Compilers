using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using static YAKSys_Hybrid_CPU.Processor;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan3Phase00EvidenceTests
{
    [Fact]
    public void ManifestIsVersionedOrderedAndByteDeterministic()
    {
        CompilerScheduleMetricsV1 metrics = CompileMetrics();
        CompilerSourceFileHashV1 first = CompilerSourceFileHashV1.Create(
            "HybridCPU_Compiler/Z.cs",
            Encoding.UTF8.GetBytes("z"));
        CompilerSourceFileHashV1 second = CompilerSourceFileHashV1.Create(
            "HybridCPU_Compiler/A.cs",
            Encoding.UTF8.GetBytes("a"));

        CompilerBenchmarkManifestV1 left = CreateManifest(metrics, [first, second]);
        CompilerBenchmarkManifestV1 right = CreateManifest(metrics, [second, first]);

        Assert.Equal(CompilerBenchmarkManifestV1.SchemaName, left.Schema);
        Assert.Equal(["HybridCPU_Compiler/A.cs", "HybridCPU_Compiler/Z.cs"],
            left.SourceFiles.Select(static file => file.RelativePath));
        Assert.Equal(left.SourceManifestFingerprint, right.SourceManifestFingerprint);
        Assert.Equal(left.RunId, right.RunId);
        Assert.Equal(left.ToDeterministicJsonBytes(), right.ToDeterministicJsonBytes());
        Assert.Equal(metrics.InputFingerprint, left.InputFingerprint);
        Assert.Equal(metrics.ProfileFingerprint, left.ProfileFingerprint);
        Assert.Equal(metrics.ModelFingerprint, left.ModelFingerprint);
        Assert.Equal(metrics.OptionsFingerprint, left.OptionsFingerprint);
    }

    [Fact]
    public void ManifestRejectsMissingHashesDuplicatesAndPathTraversal()
    {
        CompilerScheduleMetricsV1 metrics = CompileMetrics();
        CompilerSourceFileHashV1 valid = CompilerSourceFileHashV1.Create(
            "HybridCPU_Compiler/A.cs",
            Encoding.UTF8.GetBytes("a"));

        Assert.Throws<ArgumentException>(() => CreateManifest(metrics, []));
        Assert.Throws<ArgumentException>(() => CreateManifest(metrics, [valid, valid]));
        Assert.Throws<ArgumentException>(() => CompilerSourceFileHashV1.Create(
            "../HybridCPU_ISE/runtime.cs",
            Encoding.UTF8.GetBytes("forbidden")));
        Assert.Throws<ArgumentException>(() => CompilerSourceFileHashV1.Create(
            "C:/HybridCPU_Compiler/A.cs",
            Encoding.UTF8.GetBytes("absolute")));
        Assert.Throws<ArgumentException>(() => CompilerSourceFileHashV1.Create(
            "HybridCPU_Compiler/./A.cs",
            Encoding.UTF8.GetBytes("alias")));
        Assert.Throws<ArgumentException>(() => CreateManifest(
            metrics,
            [new CompilerSourceFileHashV1("HybridCPU_Compiler/A.cs", "not-a-hash", 1)]));
    }

    [Fact]
    public void EqualAttributedRecordsCompareEquivalentWithZeroDeltas()
    {
        CompilerBenchmarkRecordV1 record = CreateRecord();

        CompilerMetricsComparisonV1 comparison =
            CompilerScheduleMetricsComparatorV1.Compare(record, record);

        Assert.True(comparison.IsComparable);
        Assert.True(comparison.IsEquivalent);
        Assert.Null(comparison.ComparisonFailureReason);
        Assert.Null(comparison.FirstDivergence);
        Assert.NotEmpty(comparison.Deltas);
        Assert.All(comparison.Deltas, static delta => Assert.Equal(0, delta.AbsoluteDelta));
    }

    [Fact]
    public void ComparatorReportsAbsoluteRelativeDeltaAndFirstDivergence()
    {
        CompilerBenchmarkRecordV1 baseline = CreateRecord();
        CompilerScheduleMetricsV1 changedMetrics = baseline.Metrics with
        {
            ScheduleCycles = baseline.Metrics.ScheduleCycles + 2
        };
        CompilerBenchmarkRecordV1 candidate = new CompilerBenchmarkRecordV1(baseline.Manifest, changedMetrics);

        CompilerMetricsComparisonV1 comparison =
            CompilerScheduleMetricsComparatorV1.Compare(baseline, candidate);

        Assert.True(comparison.IsComparable);
        Assert.False(comparison.IsEquivalent);
        Assert.Equal("schedule_cycles", Assert.IsType<CompilerMetricsFirstDivergenceV1>(comparison.FirstDivergence).Path);
        CompilerMetricDeltaV1 delta = Assert.Single(
            comparison.Deltas,
            static delta => delta.Metric == "schedule_cycles");
        Assert.Equal(2, delta.AbsoluteDelta);
        Assert.Equal((decimal)2 / baseline.Metrics.ScheduleCycles, delta.RelativeDelta);
    }

    [Fact]
    public void UnknownSchemaMissingManifestHashAndUnavailableRequiredCounterFailComparisonOnly()
    {
        CompilerBenchmarkRecordV1 baseline = CreateRecord();
        CompilerBenchmarkRecordV1 unknownSchema = baseline with
        {
            Metrics = baseline.Metrics with { Schema = "CompilerScheduleMetricsV2" }
        };
        CompilerBenchmarkRecordV1 missingManifestHash = baseline with
        {
            Manifest = baseline.Manifest with { SourceManifestFingerprint = string.Empty }
        };

        CompilerMetricsComparisonV1 unknownResult =
            CompilerScheduleMetricsComparatorV1.Compare(baseline, unknownSchema);
        CompilerMetricsComparisonV1 missingHashResult =
            CompilerScheduleMetricsComparatorV1.Compare(baseline, missingManifestHash);
        CompilerMetricsComparisonV1 unavailableCounterResult =
            CompilerScheduleMetricsComparatorV1.Compare(
                baseline,
                baseline,
                new CompilerMetricsComparisonRequirementsV1(RequirePlacementPrunedCounter: true));

        Assert.False(unknownResult.IsComparable);
        Assert.Contains("unsupported metrics schema", unknownResult.ComparisonFailureReason, StringComparison.Ordinal);
        Assert.False(missingHashResult.IsComparable);
        Assert.Contains("fingerprint is missing", missingHashResult.ComparisonFailureReason, StringComparison.Ordinal);
        Assert.False(unavailableCounterResult.IsComparable);
        Assert.Contains("counter is unavailable", unavailableCounterResult.ComparisonFailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void DirtyUnattributedSubjectCannotBeComparedAsBaseline()
    {
        CompilerBenchmarkRecordV1 attributed = CreateRecord();
        CompilerBenchmarkRecordV1 unattributed = attributed with
        {
            Manifest = attributed.Manifest with
            {
                SubjectDisposition = CompilerSubjectDispositionV1.DirtyUnattributed
            }
        };

        CompilerMetricsComparisonV1 comparison =
            CompilerScheduleMetricsComparatorV1.Compare(unattributed, attributed);

        Assert.False(comparison.IsComparable);
        Assert.False(comparison.IsEquivalent);
        Assert.Contains("dirty subject is unattributed", comparison.ComparisonFailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TamperedSourceManifestAndMetricsIdentityFailComparisonOnly()
    {
        CompilerBenchmarkRecordV1 baseline = CreateRecord();
        CompilerBenchmarkRecordV1 tamperedHash = baseline with
        {
            Manifest = baseline.Manifest with
            {
                SourceFiles =
                [
                    new CompilerSourceFileHashV1(
                        "HybridCPU_Compiler/A.cs",
                        new string('0', 64),
                        1)
                ]
            }
        };
        CompilerBenchmarkRecordV1 mismatchedIdentity = baseline with
        {
            Manifest = baseline.Manifest with { InputFingerprint = new string('1', 64) }
        };

        CompilerMetricsComparisonV1 tamperedHashResult =
            CompilerScheduleMetricsComparatorV1.Compare(tamperedHash, baseline);
        CompilerMetricsComparisonV1 mismatchedIdentityResult =
            CompilerScheduleMetricsComparatorV1.Compare(mismatchedIdentity, baseline);

        Assert.False(tamperedHashResult.IsComparable);
        Assert.Contains("does not match exact file hashes", tamperedHashResult.ComparisonFailureReason, StringComparison.Ordinal);
        Assert.False(mismatchedIdentityResult.IsComparable);
        Assert.Contains("manifest identity does not match", mismatchedIdentityResult.ComparisonFailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileIdentityChangesAttributionButNeverScheduleOrBundleSelection()
    {
        CompilerScheduleMetricsV1 absent = CompileMetrics("absent");
        CompilerScheduleMetricsV1 ignored = CompileMetrics("profile:missing-or-corrupt");

        Assert.NotEqual(absent.ProfileFingerprint, ignored.ProfileFingerprint);
        Assert.Equal(absent.ScheduleFingerprint, ignored.ScheduleFingerprint);
        Assert.Equal(absent.BundleFingerprint, ignored.BundleFingerprint);
        Assert.Equal(absent.ScheduleCycles, ignored.ScheduleCycles);
        Assert.Equal(absent.BundleCount, ignored.BundleCount);
    }

    [Fact]
    public void FrozenSyntheticCorpusIsCompleteUniqueAndExecutesStructuralCases()
    {
        string[] requiredIds =
        [
            "independent-wide-set",
            "raw-chain",
            "zero-latency-chain",
            "greedy-membership-counterexample",
            "w8-fixed-lanes",
            "lane6-lane7-choke",
            "system-singleton",
            "serialization-boundary",
            "memory-must-dependence",
            "memory-may-dependence",
            "register-war",
            "register-waw",
            "no-placement-duplicate-lane6"
        ];
        CompilerSyntheticCaseV1[] cases = CompilerPhase00SyntheticCorpusV1.Cases.ToArray();

        Assert.Equal(requiredIds.OrderBy(static id => id), cases.Select(static item => item.Id).OrderBy(static id => id));
        Assert.Equal(cases.Length, cases.Select(static item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(cases, static item => Assert.False(string.IsNullOrWhiteSpace(item.ExpectedObservation)));

        foreach (CompilerSyntheticCaseV1 item in cases.Where(static item => item.ExpectedStructuralPlacement is not null))
        {
            IrBundlePlacementSearchResult placement =
                HybridCpuSlotModel.SearchStructuralAssignments(item.StructuralSlotMasks);
            Assert.Equal(item.ExpectedStructuralPlacement, placement.HasStructuralPlacement);
        }
    }

    [Fact]
    public void RepresentativeProfileInventoryClassifiesAllCapturedEvidenceWithoutZeroSubstitution()
    {
        string[] expectedIds =
        ["alu", "novt", "vt", "max", "lk", "bnmcz", "replay", "safety", "stream-vector", "matrix-tile"];
        CompilerBenchmarkProfileV1[] profiles = CompilerPhase00RepresentativeProfilesV1.Profiles.ToArray();

        Assert.Equal(expectedIds.OrderBy(static id => id), profiles.Select(static profile => profile.Id).OrderBy(static id => id));
        Assert.All(profiles, static profile => Assert.False(string.IsNullOrWhiteSpace(profile.EvidenceReason)));
        Assert.All(profiles, static profile => Assert.Equal(CompilerMetricEvidenceQualityV1.Measured, profile.EvidenceQuality));
        Assert.Equal(6, profiles.Count(static profile => profile.Scope == CompilerBenchmarkProfileScopeV1.CompilerSchedulingInput));
        Assert.Equal(4, profiles.Count(static profile => profile.Scope == CompilerBenchmarkProfileScopeV1.RuntimeNegativeControl));
        Assert.DoesNotContain(profiles, static profile =>
            profile.EvidenceQuality == CompilerMetricEvidenceQualityV1.HistoricalUnverified &&
            profile.EvidenceReason.Contains("measured", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SixFrozenRepresentativeInputsRemainDeterministicAndClassifyHistoricalDeltas()
    {
        _ = new Processor(ProcessorMode.Compiler);
        string[] expectedIds = ["alu", "novt", "vt", "max", "lk", "bnmcz"];
        CompilerRepresentativeInputDescriptorV1[] descriptors =
            CompilerPhase00RepresentativeInputCorpusV1.Descriptors.ToArray();
        HashSet<string> inputFingerprints = new HashSet<string>(StringComparer.Ordinal);
        // Current typed-slot scheduling uses both LSU lanes; historical measurements stay in the corpus.
        int[] expectedMeasuredScheduleCycles = [74, 74, 44, 44, 44, 40];

        Assert.Equal(expectedIds, descriptors.Select(static descriptor => descriptor.Id));
        for (int descriptorIndex = 0; descriptorIndex < descriptors.Length; descriptorIndex++)
        {
            CompilerRepresentativeInputDescriptorV1 descriptor = descriptors[descriptorIndex];
            CompilerRepresentativeInputInstanceV1 first =
                CompilerPhase00RepresentativeInputCorpusV1.Create(descriptor.Id);
            CompilerRepresentativeInputInstanceV1 second =
                CompilerPhase00RepresentativeInputCorpusV1.Create(descriptor.Id);

            Assert.Equal(descriptor.HistoricalBaseline.InstructionCount, first.Instructions.Length);
            Assert.Equal(EncodeInstructions(first.Instructions), EncodeInstructions(second.Instructions));
            AssertAnnotationsEqual(first.BundleAnnotations, second.BundleAnnotations);

            HybridCpuCompilationMetricsResultV1 result =
                HybridCpuCanonicalCompiler.CompileProgramWithMetrics(
                    0,
                    first.Instructions,
                    new CompilerScheduleMetricsRequestV1(ProfileIdentity: descriptor.Id),
                    bundleAnnotations: first.BundleAnnotations);
            CompilerScheduleMetricsV1 metrics =
                Assert.IsType<CompilerScheduleMetricsV1>(result.ScheduleMetrics);
            int issuedInstructionCount = result.CompiledProgram.BundleLayout.BlockResults
                .SelectMany(static block => block.Bundles)
                .Sum(static bundle => bundle.IssuedInstructionCount);
            decimal averageWidth = decimal.Round(
                (decimal)issuedInstructionCount / metrics.BundleCount,
                4,
                MidpointRounding.ToEven);

            Assert.Equal(first.Instructions.Length, issuedInstructionCount);
            Assert.True(inputFingerprints.Add(metrics.InputFingerprint));
            Assert.Equal(expectedMeasuredScheduleCycles[descriptorIndex], metrics.ScheduleCycles);
            Assert.Equal(expectedMeasuredScheduleCycles[descriptorIndex], metrics.BundleCount);
            Assert.Equal(
                metrics.BundleCount - descriptor.HistoricalBaseline.BundleCount,
                metrics.BundleCount - descriptor.HistoricalBaseline.CycleGroupCount);
            Assert.NotEqual(descriptor.HistoricalBaseline.AverageWidth, averageWidth);
            Assert.Equal(CompilerMetricEvidenceQualityV1.HistoricalUnverified,
                descriptor.HistoricalBaseline.EvidenceQuality);
            Assert.True(metrics.ScheduleCycles >= metrics.BundleCount);
        }
    }

    [Fact]
    public void RepresentativeBaselineCaptureIsDeterministicAndKeepsResourcesSeparate()
    {
        _ = new Processor(ProcessorMode.Compiler);
        CompilerSourceFileHashV1[] sourceFiles =
        [
            CompilerSourceFileHashV1.Create(
                "HybridCPU_Compiler/Core/IR/Telemetry/CompilerPhase00RepresentativeInputsV1.cs",
                Encoding.UTF8.GetBytes("exact-subject-placeholder-for-unit-test"))
        ];

        CompilerPhase00BaselineCaptureResultV1 first = CaptureBaseline(sourceFiles);
        CompilerPhase00BaselineCaptureResultV1 second = CaptureBaseline(sourceFiles);

        Assert.Equal(
            first.DeterministicReport.ToDeterministicJsonBytes(),
            second.DeterministicReport.ToDeterministicJsonBytes());
        Assert.Equal(6, first.DeterministicReport.Profiles.Count);
        Assert.Equal(6, first.ResourceReport.Samples.Count);
        Assert.All(first.DeterministicReport.Profiles, static profile =>
        {
            Assert.Equal(
                profile.CurrentCycleGroupCount - profile.HistoricalBaseline.CycleGroupCount,
                profile.HistoricalDelta.CycleGroupCountDelta);
            Assert.Equal(
                profile.CurrentRecord.Metrics.BundleCount - profile.HistoricalBaseline.BundleCount,
                profile.HistoricalDelta.BundleCountDelta);
            Assert.Equal(
                profile.CurrentAverageWidth - profile.HistoricalBaseline.AverageWidth,
                profile.HistoricalDelta.AverageWidthDelta);
            Assert.NotEqual(0, profile.HistoricalDelta.BundleCountDelta);
            Assert.Null(profile.HistoricalDelta.ScheduleCyclesDelta);
            Assert.Equal(
                CompilerMetricEvidenceQualityV1.Unavailable,
                profile.HistoricalDelta.HistoricalScheduleCyclesEvidenceQuality);
            Assert.Equal(
                CompilerSubjectDispositionV1.DirtyAttributed,
                profile.CurrentRecord.Manifest.SubjectDisposition);
        });
    }

    [Fact]
    public void BundleMaterializationPreservesSchedulerMembershipIdentity()
    {
        _ = new Processor(ProcessorMode.Compiler);
        VLIW_Instruction[] input =
        [
            CreateAddi(1, 20, 1),
            CreateAddi(2, 21, 2),
            CreateAddi(3, 22, 3),
            CreateAddi(4, 23, 4),
            CreateAddi(5, 24, 5)
        ];
        HybridCpuCompilationMetricsResultV1 result =
            NativeTransportRuntimeAdapter.CompileProgramWithMetrics(
                0,
                input,
                new CompilerScheduleMetricsRequestV1());

        foreach (IrBasicBlockBundlingResult block in result.CompiledProgram.BundleLayout.BlockResults)
        {
            IrBasicBlockSchedule schedule = block.BlockSchedule;
            Assert.Equal(schedule.CycleGroups.Count, block.Bundles.Count);
            for (int index = 0; index < block.Bundles.Count; index++)
            {
                Assert.Equal(
                    schedule.CycleGroups[index].Instructions.Select(static instruction => instruction.Index),
                    block.Bundles[index].CycleGroup.Instructions.Select(static instruction => instruction.Index));
            }
        }
    }

    [Fact]
    public void TelemetryAndProfileEvidenceDoNotEnterLegalityReservationOrLoweringOwners()
    {
        string root = FindRepositoryRoot();
        string[] authorityOwnerFiles =
        [
            "Compilers/HybridCPU_Compiler/Core/IR/Hazards/HybridCpuInstructionLegalityChecker.cs",
            "Compilers/HybridCPU_Compiler/Core/IR/Hazards/HybridCpuStructuralResourceModel.cs",
            "Compilers/HybridCPU_Compiler/Core/IR/Hazards/HybridCpuSlotModel.Analysis.cs",
            "Compilers/HybridCPU_Compiler/Core/IR/Hazards/HybridCpuSlotModel.BundleSearch.cs",
            "Compilers/HybridCPU_Compiler/Core/IR/Hazards/HybridCpuSlotModel.Helpers.cs",
            "Compilers/HybridCPU_Compiler/Core/IR/Hazards/HybridCpuSlotModel.ProgramSearch.cs",
            "Compilers/HybridCPU_Compiler/Core/IR/Hazards/HybridCpuSlotModel.TieBreakContext.cs",
            "Compilers/HybridCPU_Compiler/Core/IR/Bundling/HybridCpuClassCapacityChecker.cs",
            "Compilers/HybridCPU_Compiler/Core/IR/Bundling/HybridCpuBundleLowerer.cs"
        ];
        string[] forbiddenEvidenceDependencies =
        [
            nameof(CompilerScheduleMetricsV1),
            nameof(CompilerBenchmarkManifestV1),
            nameof(CompilerPhase00SyntheticCorpusV1),
            "TelemetryProfileReader"
        ];

        foreach (string relativePath in authorityOwnerFiles)
        {
            string source = File.ReadAllText(Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            foreach (string forbiddenDependency in forbiddenEvidenceDependencies)
            {
                Assert.DoesNotContain(forbiddenDependency, source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void RuntimeControlOutcomeNormalizationExcludesOnlyPhysicalTimeObservations()
    {
        byte[] first = Encoding.UTF8.GetBytes(
            """
            {
              "StartedUtc": "2026-08-19T10:00:00Z",
              "FinishedUtc": "2026-08-19T10:00:01Z",
              "Aggregate": {
                "ElapsedMilliseconds": 10.5,
                "RuntimeInstructionsPerMillisecond": 7.25,
                "RuntimeInstructionCount": 42,
                "FailClosedRejectionCount": 3,
                "ResultChecksum": 99
              },
              "Succeeded": true
            }
            """);
        byte[] second = Encoding.UTF8.GetBytes(
            """
            {
              "Succeeded": true,
              "Aggregate": {
                "ResultChecksum": 99,
                "FailClosedRejectionCount": 3,
                "RuntimeInstructionCount": 42,
                "RuntimeInstructionsPerMillisecond": 999.0,
                "ElapsedMilliseconds": 800.0
              },
              "FinishedUtc": "2026-08-20T10:00:00Z",
              "StartedUtc": "2026-08-20T09:00:00Z"
            }
            """);

        byte[] normalizedFirst = CompilerPhase00RuntimeControlEvidenceV1.NormalizeOutcomeJson(first);
        byte[] normalizedSecond = CompilerPhase00RuntimeControlEvidenceV1.NormalizeOutcomeJson(second);

        Assert.Equal(normalizedFirst, normalizedSecond);
        Assert.Equal(
            CompilerPhase00RuntimeControlEvidenceV1.ComputeSha256(normalizedFirst),
            CompilerPhase00RuntimeControlEvidenceV1.ComputeSha256(normalizedSecond));

        byte[] changedCounter = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(second).Replace(
                "\"FailClosedRejectionCount\": 3",
                "\"FailClosedRejectionCount\": 4",
                StringComparison.Ordinal));
        Assert.NotEqual(
            normalizedFirst,
            CompilerPhase00RuntimeControlEvidenceV1.NormalizeOutcomeJson(changedCounter));
    }

    [Fact]
    public void RuntimeControlTimingDistributionReportsMedianDispersionAndRejectsInvalidSamples()
    {
        CompilerPhase00RuntimeTimingDistributionV1 distribution =
            CompilerPhase00RuntimeControlEvidenceV1.CreateTimingDistribution([30.0d, 10.0d, 20.0d]);

        Assert.Equal(3, distribution.SampleCount);
        Assert.Equal(10.0d, distribution.MinimumMilliseconds);
        Assert.Equal(20.0d, distribution.MedianMilliseconds);
        Assert.Equal(30.0d, distribution.MaximumMilliseconds);
        Assert.Equal(20.0d, distribution.MeanMilliseconds);
        Assert.Equal(Math.Sqrt(200.0d / 3.0d), distribution.PopulationStandardDeviationMilliseconds, 10);
        Assert.Equal(Math.Sqrt(200.0d / 3.0d) / 20.0d, distribution.CoefficientOfVariation!.Value, 10);

        Assert.Throws<ArgumentException>(() =>
            CompilerPhase00RuntimeControlEvidenceV1.CreateTimingDistribution([]));
        Assert.Throws<ArgumentException>(() =>
            CompilerPhase00RuntimeControlEvidenceV1.CreateTimingDistribution([double.NaN]));
        Assert.Throws<ArgumentException>(() =>
            CompilerPhase00RuntimeControlEvidenceV1.CreateTimingDistribution([-1.0d]));
    }

    private static CompilerBenchmarkRecordV1 CreateRecord()
    {
        CompilerScheduleMetricsV1 metrics = CompileMetrics();
        CompilerBenchmarkManifestV1 manifest = CreateManifest(
            metrics,
            [CompilerSourceFileHashV1.Create("HybridCPU_Compiler/A.cs", Encoding.UTF8.GetBytes("a"))]);
        return new CompilerBenchmarkRecordV1(manifest, metrics);
    }

    private static CompilerPhase00BaselineCaptureResultV1 CaptureBaseline(
        CompilerSourceFileHashV1[] sourceFiles) =>
        CompilerPhase00BaselineCaptureV1.Capture(
            "d4eeec71fa7d9e3a0efa8042dc667422f46181ac",
            "refactor/compiler-core-authority-boundaries",
            CompilerSubjectDispositionV1.DirtyAttributed,
            "Debug",
            runtimeContractVersion: 1,
            toolVersion: "phase00-tests-v1",
            sourceFiles);

    private static CompilerBenchmarkManifestV1 CreateManifest(
        CompilerScheduleMetricsV1 metrics,
        CompilerSourceFileHashV1[] sourceFiles) =>
        CompilerBenchmarkManifestBuilderV1.Create(
            "d4eeec71fa7d9e3a0efa8042dc667422f46181ac",
            "refactor/compiler-core-authority-boundaries",
            CompilerSubjectDispositionV1.DirtyAttributed,
            "Debug",
            runtimeContractVersion: 1,
            toolVersion: "phase00-tests-v1",
            metrics,
            sourceFiles);

    private static CompilerScheduleMetricsV1 CompileMetrics(string profileIdentity = "absent")
    {
        _ = new Processor(ProcessorMode.Compiler);
        HybridCpuCompilationMetricsResultV1 result =
            NativeTransportRuntimeAdapter.CompileProgramWithMetrics(
                0,
                [CreateAddi(1, 20, 1), CreateAddi(2, 21, 2)],
                new CompilerScheduleMetricsRequestV1(ProfileIdentity: profileIdentity));
        return Assert.IsType<CompilerScheduleMetricsV1>(result.ScheduleMetrics);
    }

    private static VLIW_Instruction CreateAddi(byte destination, byte source, ushort immediate) =>
        new()
        {
            OpCode = (uint)InstructionsEnum.ADDI,
            DataTypeValue = DataTypeEnum.INT32,
            PredicateMask = 0xFF,
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(
                destination,
                source,
                VLIW_Instruction.NoArchReg),
            Immediate = immediate,
            Src2Pointer = immediate,
            VirtualThreadId = 0
        };

    private static byte[] EncodeInstructions(IReadOnlyList<VLIW_Instruction> instructions)
    {
        byte[] bytes = new byte[instructions.Count * 32];
        for (int index = 0; index < instructions.Count; index++)
        {
            Assert.True(instructions[index].TryWriteBytes(bytes.AsSpan(index * 32, 32)));
        }

        return bytes;
    }

    private static byte[] EncodeInstructions(IReadOnlyList<HybridCpuInstructionWord> instructions)
    {
        byte[] bytes = new byte[instructions.Count * HybridCpuInstructionWord.EncodedSize];
        for (int index = 0; index < instructions.Count; index++)
            Assert.True(instructions[index].TryWriteBytes(
                bytes.AsSpan(index * HybridCpuInstructionWord.EncodedSize, HybridCpuInstructionWord.EncodedSize)));
        return bytes;
    }

    private static void AssertAnnotationsEqual(
        VliwBundleAnnotations expected,
        VliwBundleAnnotations actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.True(expected.TryGetInstructionSlotMetadata(index, out InstructionSlotMetadata expectedMetadata));
            Assert.True(actual.TryGetInstructionSlotMetadata(index, out InstructionSlotMetadata actualMetadata));
            Assert.Equal(expectedMetadata, actualMetadata);
        }
    }

    private static void AssertAnnotationsEqual(
        IrBundleAnnotations expected,
        IrBundleAnnotations actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.True(expected.TryGetInstructionSlotMetadata(index, out IrInstructionSlotMetadata expectedMetadata));
            Assert.True(actual.TryGetInstructionSlotMetadata(index, out IrInstructionSlotMetadata actualMetadata));
            Assert.Equal(expectedMetadata, actualMetadata);
        }
    }

    private static string FindRepositoryRoot()
    {
        return HybridCPU_ISE.Tests.TestHelpers.CompatFreezeScanner.FindCompilerRepoRoot();
    }
}
