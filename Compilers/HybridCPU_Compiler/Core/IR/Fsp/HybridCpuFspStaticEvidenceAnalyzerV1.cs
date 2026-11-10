using System.Globalization;
using System.Text;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.IR.Telemetry;

namespace HybridCPU.Compiler.Core.IR.Fsp;

/// <summary>
/// Emits deterministic compiler-only donor-quality evidence from an already materialized
/// schedule. The report never changes schedule membership, placement or carrier bytes.
/// </summary>
public sealed class HybridCpuFspStaticEvidenceAnalyzerV1
{
    public IrFspStaticEvidenceReportV1 Analyze(
        IrProgramBundlingResult bundling,
        HybridCpuTopologyResourceModelV1? topologyModel = null,
        HybridCpuFspEvidenceOptionsV1? options = null,
        IrFspOfflineRankingProfileV1? rankingProfile = null,
        string? expectedTopologyDigest = null)
    {
        ArgumentNullException.ThrowIfNull(bundling);
        topologyModel ??= new HybridCpuTopologyResourceModelV1();
        options ??= HybridCpuFspEvidenceOptionsV1.Production;
        string programDigest = CompilerScheduleFingerprintV1.HashProgramInput(bundling.Program);
        string scheduleDigest = CompilerScheduleFingerprintV1.HashSchedule(bundling.ProgramSchedule);
        string bundleDigest = CompilerScheduleFingerprintV1.HashBundles(bundling);
        string topologyDigest = topologyModel.Topology.ContractDigest;
        IrFspProfileDispositionV1 profileDisposition = rankingProfile is null
            ? IrFspProfileDispositionV1.AbsentStaticPolicy
            : IrFspProfileDispositionV1.VersionedOfflineRankingOnly;

        if (!HasValidOptions(options) || !HasValidProfile(rankingProfile))
            return Baseline(
                IrFspEvidenceStatusV1.InvalidModel,
                programDigest,
                scheduleDigest,
                bundleDigest,
                topologyDigest,
                options.OptionsDigest,
                profileDisposition,
                rankingProfile?.ProfileDigest,
                "HCFSP1001",
                "Static FSP evidence options or offline ranking profile are invalid.");
        if (options.EvidenceSwitch == IrFspEvidenceSwitchV1.Disabled)
            return Baseline(
                IrFspEvidenceStatusV1.DisabledBaseline,
                programDigest,
                scheduleDigest,
                bundleDigest,
                topologyDigest,
                options.OptionsDigest,
                profileDisposition,
                rankingProfile?.ProfileDigest,
                "HCFSP1002",
                "Static FSP evidence is disabled; the verified schedule and carrier stream remain exact.");
        if (expectedTopologyDigest is not null &&
            !string.Equals(expectedTopologyDigest, topologyDigest, StringComparison.Ordinal))
            return Baseline(
                IrFspEvidenceStatusV1.StaleInput,
                programDigest,
                scheduleDigest,
                bundleDigest,
                topologyDigest,
                options.OptionsDigest,
                profileDisposition,
                rankingProfile?.ProfileDigest,
                "HCFSP1003",
                "Pinned topology digest does not match the evidence model.");
        if (CanonicalIrFrontendBoundaryV1.Validate(bundling.Program).Status != IrFrontendAdapterStatus.Success)
            return Baseline(
                IrFspEvidenceStatusV1.InvalidModel,
                programDigest,
                scheduleDigest,
                bundleDigest,
                topologyDigest,
                options.OptionsDigest,
                profileDisposition,
                rankingProfile?.ProfileDigest,
                "HCFSP1004",
                "Canonical IR boundary rejected the evidence subject.");

        IrMaterializedBundle[] bundles = bundling.BlockResults
            .OrderBy(static block => block.BlockId)
            .SelectMany(block => block.Bundles.OrderBy(static bundle => bundle.Cycle)).ToArray();
        int instructionCount = bundles.Sum(static bundle => bundle.IssuedInstructionCount);
        if (bundles.Length > options.Budgets.MaximumBundles ||
            instructionCount > options.Budgets.MaximumInstructions ||
            instructionCount > options.Budgets.MaximumEvidenceRecords)
            return Baseline(
                IrFspEvidenceStatusV1.BudgetExhausted,
                programDigest,
                scheduleDigest,
                bundleDigest,
                topologyDigest,
                options.OptionsDigest,
                profileDisposition,
                rankingProfile?.ProfileDigest,
                "HCFSP1005",
                "Evidence subject exceeds deterministic production record bounds.");

        HybridCpuStealabilityAnalyzer stealability = new HybridCpuStealabilityAnalyzer();
        List<IrFspStaticCandidateEvidenceV1> records = new List<IrFspStaticCandidateEvidenceV1>(instructionCount);
        int eligibleCount = 0;
        int usefulCount = 0;
        foreach (IrBasicBlockBundlingResult block in bundling.BlockResults.OrderBy(static block => block.BlockId))
        {
            Dictionary<int, IrSchedulingNode> nodes = block.BlockSchedule.Dag.Nodes
                .ToDictionary(static node => node.InstructionIndex);
            int peakPressure = PeakPressure(bundling.ProgramSchedule.ValueAnalysis, block.BlockId);
            foreach (IrMaterializedBundle bundle in block.Bundles.OrderBy(static bundle => bundle.Cycle))
            {
                int holeCount = bundle.Slots.Count(static slot => slot.IsNop);
                string bundleIdentity = BundleIdentity(block.BlockId, bundle);
                foreach (IrMaterializedBundleSlot slot in bundle.Slots
                             .Where(static slot => slot.Instruction is not null)
                             .OrderBy(static slot => slot.SlotIndex))
                {
                    IrInstruction instruction = slot.Instruction!;
                    StealabilityVerdict verdict = stealability.AnalyzeInstruction(instruction);
                    IrTopologyResourceFootprintV1 footprint = topologyModel.GetResourceFootprint(instruction);
                    IrFspStaticCandidateDispositionV1 disposition = Classify(
                        instruction,
                        verdict,
                        footprint,
                        holeCount);
                    if (disposition == IrFspStaticCandidateDispositionV1.EligibleStaticCandidate)
                        eligibleCount++;
                    int criticalPath = nodes.GetValueOrDefault(instruction.Index)?.CriticalPathLengthCycles ?? 0;
                    int downstream = nodes.GetValueOrDefault(instruction.Index)?.OutgoingDependencies.Count ?? 0;
                    int estimatedValue = Score(
                        holeCount,
                        criticalPath,
                        downstream,
                        peakPressure,
                        disposition,
                        rankingProfile);
                    if (disposition == IrFspStaticCandidateDispositionV1.EligibleStaticCandidate &&
                        estimatedValue > 0)
                        usefulCount++;
                    IrFspEvidencePrecisionV1 registerPrecision = footprint.RegisterPrecision ==
                        IrResourceFactPrecisionV1.Exact
                        ? IrFspEvidencePrecisionV1.ExactStatic
                        : IrFspEvidencePrecisionV1.Unknown;
                    IrFspEvidencePrecisionV1 profilePrecision = rankingProfile is null
                        ? IrFspEvidencePrecisionV1.ExactStatic
                        : IrFspEvidencePrecisionV1.ProfileOnly;
                    IrFspResourcePressureReasonV1 pressureReasons = PressureReasons(footprint, peakPressure);
                    string candidateDigest = CandidateDigest(
                        bundleIdentity,
                        instruction,
                        disposition,
                        footprint,
                        holeCount,
                        criticalPath,
                        downstream,
                        peakPressure,
                        pressureReasons,
                        estimatedValue,
                        profileDisposition,
                        rankingProfile?.ProfileDigest);
                    records.Add(new(
                        bundleIdentity,
                        instruction.StableIdentity,
                        instruction.Index,
                        instruction.VirtualThreadId,
                        new(IrFspStaticVtRelationV1.SourceVtKnownReceiverRelationUnspecified,
                            IrFspEvidencePrecisionV1.ConservativeSet,
                            "CanonicalIr.VirtualThreadId; receiver relationship is intentionally unspecified"),
                        disposition,
                        new(instruction.Annotation.StructurallyAllowedSlots,
                            IrFspEvidencePrecisionV1.ExactStatic,
                            "CanonicalIr.Instruction.Annotation.StructurallyAllowedSlots"),
                        new(instruction.Annotation.RequiredSlotClass,
                            IrFspEvidencePrecisionV1.ExactStatic,
                            "CanonicalIr.Instruction.Annotation.RequiredSlotClass"),
                        new(criticalPath,
                            IrFspEvidencePrecisionV1.ExactStatic,
                            "IrBasicBlockSchedulingDag.CriticalPathLengthCycles"),
                        new(downstream,
                            IrFspEvidencePrecisionV1.ExactStatic,
                            "IrBasicBlockSchedulingDag.OutgoingDependencies"),
                        new(peakPressure,
                            registerPrecision,
                            "IrValueAnalysisReportV1.BlockPressure"),
                        new(holeCount,
                            IrFspEvidencePrecisionV1.ExactStatic,
                            "IrMaterializedBundle.EmptyStructuralSlots"),
                        new(footprint.CertificateClass,
                            footprint.CertificatePrecision == IrResourceFactPrecisionV1.Exact
                                ? IrFspEvidencePrecisionV1.ExactStatic
                                : IrFspEvidencePrecisionV1.Unknown,
                            "IrTopologyResourceFootprintV1.CertificateClass"),
                        new(pressureReasons,
                            footprint.RegisterPrecision == IrResourceFactPrecisionV1.Unknown ||
                            footprint.Banks.Precision == IrAddressEvidenceKindV1.Unknown ||
                            footprint.Channels.Precision == IrAddressEvidenceKindV1.Unknown
                                ? IrFspEvidencePrecisionV1.Unknown
                                : IrFspEvidencePrecisionV1.ConservativeSet,
                            "IrTopologyResourceFootprintV1.StaticPressureSummary"),
                        new(estimatedValue,
                            profilePrecision,
                            rankingProfile is null
                                ? "HybridCpuFspStaticEvidenceAnalyzerV1.StaticScore"
                                : IrFspOfflineRankingProfileV1.SchemaId),
                        profileDisposition,
                        topologyDigest,
                        candidateDigest));
                }
            }
        }

        records = records.OrderByDescending(static record => record.EstimatedStaticCycleValue.Value)
            .ThenBy(static record => record.VirtualThreadId)
            .ThenBy(static record => record.InstructionIndex)
            .ToList();
        IrFspEvidenceCountersV1 counters = new IrFspEvidenceCountersV1(
            bundles.Length,
            instructionCount,
            eligibleCount,
            usefulCount,
            checked(bundles.Length + instructionCount));
        string reportDigest = ReportDigest(
            IrFspEvidenceStatusV1.Accepted,
            programDigest,
            scheduleDigest,
            bundleDigest,
            topologyDigest,
            HybridCpuFspEvidenceContractV1.Default.ContractDigest,
            options.OptionsDigest,
            rankingProfile?.ProfileDigest,
            records,
            counters);
        return new(
            IrFspEvidenceStatusV1.Accepted,
            IrFspEvidenceAuthorityV1.CompilerEvidenceOnly,
            programDigest,
            scheduleDigest,
            bundleDigest,
            topologyDigest,
            HybridCpuFspEvidenceContractV1.Default.ContractDigest,
            options.OptionsDigest,
            profileDisposition,
            rankingProfile?.ProfileDigest,
            records,
            counters,
            Array.Empty<IrFspEvidenceDiagnosticV1>(),
            reportDigest);
    }

    private static IrFspStaticCandidateDispositionV1 Classify(
        IrInstruction instruction,
        StealabilityVerdict verdict,
        IrTopologyResourceFootprintV1 footprint,
        int holeCount)
    {
        if (!verdict.IsStealable)
            return IrFspStaticCandidateDispositionV1.ExcludedByStealability;
        if (instruction.SideEffects.Memory.Kind != IrMemoryEffectKind.None)
            return IrFspStaticCandidateDispositionV1.ExcludedByMemoryEffect;
        if (footprint.CertificateClass != CompilerCertificateClassV1.None ||
            instruction.Annotation.RequiredSlotClass is IrSlotClass.DmaStreamClass or
                IrSlotClass.MatrixTileStreamClass or IrSlotClass.BranchControl or
                IrSlotClass.SystemSingleton)
            return IrFspStaticCandidateDispositionV1.ExcludedBySpecialContour;
        if (footprint.RegisterPrecision != IrResourceFactPrecisionV1.Exact ||
            footprint.PrfPortPrecision != IrResourceFactPrecisionV1.Exact ||
            footprint.CertificatePrecision != IrResourceFactPrecisionV1.Exact)
            return IrFspStaticCandidateDispositionV1.ExcludedByUnknownResource;
        return holeCount == 0
            ? IrFspStaticCandidateDispositionV1.ExcludedByNoStaticHole
            : IrFspStaticCandidateDispositionV1.EligibleStaticCandidate;
    }

    private static int Score(
        int holeCount,
        int criticalPath,
        int downstream,
        int pressure,
        IrFspStaticCandidateDispositionV1 disposition,
        IrFspOfflineRankingProfileV1? profile)
    {
        if (disposition != IrFspStaticCandidateDispositionV1.EligibleStaticCandidate)
            return 0;
        int holeWeight = profile?.StaticHoleWeight ?? 100;
        int criticalWeight = profile?.CriticalPathWeight ?? 10;
        int dependencyWeight = profile?.DependencyWeight ?? 4;
        int pressureWeight = profile?.PressurePenaltyWeight ?? 2;
        return Math.Max(0, checked(
            holeCount * holeWeight +
            criticalPath * criticalWeight +
            downstream * dependencyWeight -
            pressure * pressureWeight));
    }

    private static int PeakPressure(IrValueAnalysisReportV1 report, int blockId) => report.Pressure
        .Where(pressure => pressure.BlockId == blockId)
        .SelectMany(static pressure => pressure.RegisterGroups)
        .Select(static pressure => pressure.PeakLiveValues)
        .DefaultIfEmpty(0)
        .Max();

    private static IrFspResourcePressureReasonV1 PressureReasons(
        IrTopologyResourceFootprintV1 footprint,
        int peakPressure)
    {
        IrFspResourcePressureReasonV1 result = peakPressure > 0
            ? IrFspResourcePressureReasonV1.RegisterGroupPressure
            : IrFspResourcePressureReasonV1.None;
        if (footprint.Banks.Precision is IrAddressEvidenceKindV1.FiniteSet or IrAddressEvidenceKindV1.All ||
            footprint.Channels.Precision is IrAddressEvidenceKindV1.FiniteSet or IrAddressEvidenceKindV1.All)
            result |= IrFspResourcePressureReasonV1.BankOrChannelPossible;
        if (footprint.CertificateClass != CompilerCertificateClassV1.None)
            result |= IrFspResourcePressureReasonV1.SpecialContour;
        if (footprint.RegisterPrecision == IrResourceFactPrecisionV1.Unknown ||
            footprint.Banks.Precision == IrAddressEvidenceKindV1.Unknown ||
            footprint.Channels.Precision == IrAddressEvidenceKindV1.Unknown ||
            footprint.CertificatePrecision == IrResourceFactPrecisionV1.Unknown)
            result |= IrFspResourcePressureReasonV1.UnknownResource;
        return result;
    }

    private static string BundleIdentity(int blockId, IrMaterializedBundle bundle)
    {
        StringBuilder payload = new StringBuilder("hybridcpu.fsp-static-bundle/v1")
            .Append('|').Append(blockId)
            .Append('|').Append(bundle.Cycle);
        foreach (IrMaterializedBundleSlot slot in bundle.Slots.OrderBy(static slot => slot.SlotIndex))
            payload.Append('|').Append(slot.SlotIndex).Append(':')
                .Append(slot.Instruction?.StableIdentity ?? "empty");
        return HybridCpuFspEvidenceContractV1.Hash(payload.ToString());
    }

    private static string CandidateDigest(
        string bundleIdentity,
        IrInstruction instruction,
        IrFspStaticCandidateDispositionV1 disposition,
        IrTopologyResourceFootprintV1 footprint,
        int holeCount,
        int criticalPath,
        int downstream,
        int pressure,
        IrFspResourcePressureReasonV1 pressureReasons,
        int estimatedValue,
        IrFspProfileDispositionV1 profileDisposition,
        string? profileDigest) => HybridCpuFspEvidenceContractV1.Hash(string.Join('|',
            HybridCpuFspEvidenceContractV1.SchemaId,
            bundleIdentity,
            instruction.StableIdentity,
            instruction.Index,
            instruction.VirtualThreadId,
            (byte)disposition,
            (byte)instruction.Annotation.RequiredSlotClass,
            (byte)instruction.Annotation.StructurallyAllowedSlots,
            footprint.Fingerprint,
            holeCount,
            criticalPath,
            downstream,
            pressure,
            (byte)pressureReasons,
            estimatedValue,
            (byte)profileDisposition,
            profileDigest ?? "absent"));

    private static string ReportDigest(
        IrFspEvidenceStatusV1 status,
        string programDigest,
        string scheduleDigest,
        string bundleDigest,
        string topologyDigest,
        string modelDigest,
        string optionsDigest,
        string? profileDigest,
        IReadOnlyList<IrFspStaticCandidateEvidenceV1> records,
        IrFspEvidenceCountersV1 counters)
    {
        StringBuilder payload = new StringBuilder(HybridCpuFspEvidenceContractV1.SchemaId)
            .Append('|').Append((byte)status)
            .Append('|').Append(programDigest)
            .Append('|').Append(scheduleDigest)
            .Append('|').Append(bundleDigest)
            .Append('|').Append(topologyDigest)
            .Append('|').Append(modelDigest)
            .Append('|').Append(optionsDigest)
            .Append('|').Append(profileDigest ?? "absent")
            .Append('|').Append(counters.BundlesVisited)
            .Append(':').Append(counters.InstructionsVisited)
            .Append(':').Append(counters.EligibleStaticCandidates)
            .Append(':').Append(counters.PredictedUsefulStaticOpportunities)
            .Append(':').Append(counters.DeterministicWorkUnits);
        foreach (IrFspStaticCandidateEvidenceV1 record in records)
            payload.Append('|').Append(record.CandidateDigest);
        return HybridCpuFspEvidenceContractV1.Hash(payload.ToString());
    }

    private static bool HasValidOptions(HybridCpuFspEvidenceOptionsV1 options)
    {
        HybridCpuFspEvidenceBudgetsV1 budgets = options.Budgets;
        if (budgets is null || budgets.MaximumBundles is < 1 or > 256 ||
            budgets.MaximumInstructions is < 1 or > 1024 ||
            budgets.MaximumEvidenceRecords is < 1 or > 1024)
            return false;
        return options == HybridCpuFspEvidenceOptionsV1.Create(options.EvidenceSwitch, budgets);
    }

    private static bool HasValidProfile(IrFspOfflineRankingProfileV1? profile)
    {
        if (profile is null) return true;
        try
        {
            return profile == IrFspOfflineRankingProfileV1.Create(
                profile.ModelDigest,
                profile.StaticHoleWeight,
                profile.CriticalPathWeight,
                profile.DependencyWeight,
                profile.PressurePenaltyWeight);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IrFspStaticEvidenceReportV1 Baseline(
        IrFspEvidenceStatusV1 status,
        string programDigest,
        string scheduleDigest,
        string bundleDigest,
        string topologyDigest,
        string optionsDigest,
        IrFspProfileDispositionV1 profileDisposition,
        string? profileDigest,
        string code,
        string message)
    {
        IrFspEvidenceCountersV1 counters = new IrFspEvidenceCountersV1(0, 0, 0, 0, 0);
        string digest = ReportDigest(
            status,
            programDigest,
            scheduleDigest,
            bundleDigest,
            topologyDigest,
            HybridCpuFspEvidenceContractV1.Default.ContractDigest,
            optionsDigest,
            profileDigest,
            Array.Empty<IrFspStaticCandidateEvidenceV1>(),
            counters);
        return new(
            status,
            IrFspEvidenceAuthorityV1.CompilerEvidenceOnly,
            programDigest,
            scheduleDigest,
            bundleDigest,
            topologyDigest,
            HybridCpuFspEvidenceContractV1.Default.ContractDigest,
            optionsDigest,
            profileDisposition,
            profileDigest,
            Array.Empty<IrFspStaticCandidateEvidenceV1>(),
            counters,
            [new(code, message)],
            digest);
    }
}
