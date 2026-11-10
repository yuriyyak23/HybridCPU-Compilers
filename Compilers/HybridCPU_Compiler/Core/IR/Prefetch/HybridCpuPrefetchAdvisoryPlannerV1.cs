using System.Text;
using HybridCPU.Compiler.Core.IR.Fsp;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.IR.Telemetry;

namespace HybridCPU.Compiler.Core.IR.Prefetch;

/// <summary>
/// Produces read-only VDSA/prefetch opportunities. It never inserts an instruction,
/// removes a dependence, reserves a donor slot or grants runtime execution permission.
/// </summary>
public sealed class HybridCpuPrefetchAdvisoryPlannerV1
{
    private const ulong PageSize = 4096;

    public IrPrefetchAdvisoryReportV1 Analyze(
        IrProgramBundlingResult bundling,
        IrFspStaticEvidenceReportV1 fspEvidence,
        IReadOnlyList<IrCanonicalLoopV1>? loops = null,
        HybridCpuTopologyResourceModelV1? topologyModel = null,
        HybridCpuMiiResourceModelV1? loopResourceModel = null,
        HybridCpuPrefetchAdvisoryOptionsV1? options = null,
        IrPrefetchOfflineRankingProfileV1? rankingProfile = null)
    {
        ArgumentNullException.ThrowIfNull(bundling);
        ArgumentNullException.ThrowIfNull(fspEvidence);
        loops ??= Array.Empty<IrCanonicalLoopV1>();
        topologyModel ??= new HybridCpuTopologyResourceModelV1();
        loopResourceModel ??= HybridCpuMiiResourceModelV1.Create(
            topologyModel.Topology,
            structuralCertificateCapacity: null);
        options ??= HybridCpuPrefetchAdvisoryOptionsV1.Production;

        string programDigest = CompilerScheduleFingerprintV1.HashProgramInput(bundling.Program);
        string scheduleDigest = CompilerScheduleFingerprintV1.HashSchedule(bundling.ProgramSchedule);
        string bundleDigest = CompilerScheduleFingerprintV1.HashBundles(bundling);
        string dependencyDigest = DependencyGraphDigest(bundling.ProgramSchedule.DependencyGraph);
        string topologyDigest = topologyModel.Topology.ContractDigest;
        IrPrefetchProfileDispositionV1 profileDisposition = rankingProfile is null
            ? IrPrefetchProfileDispositionV1.AbsentStaticPolicy
            : IrPrefetchProfileDispositionV1.VersionedOfflineRankingOnly;

        if (!HasValidOptions(options) || !HasValidProfile(rankingProfile))
            return Baseline(IrPrefetchAdvisoryStatusV1.InvalidModel, programDigest, scheduleDigest,
                bundleDigest, dependencyDigest, topologyDigest, options.OptionsDigest,
                fspEvidence.ReportDigest, profileDisposition, rankingProfile?.ProfileDigest,
                "HCPREF1001", "Advisory options or offline ranking profile are invalid.");
        if (options.AdvisorySwitch == IrPrefetchAdvisorySwitchV1.Disabled)
            return Baseline(IrPrefetchAdvisoryStatusV1.DisabledBaseline, programDigest, scheduleDigest,
                bundleDigest, dependencyDigest, topologyDigest, options.OptionsDigest,
                fspEvidence.ReportDigest, profileDisposition, rankingProfile?.ProfileDigest,
                "HCPREF1002", "Advisory planning is disabled; schedule, bundles and memory obligations remain exact.");
        if (!ValidFspEvidence(fspEvidence, programDigest, scheduleDigest, bundleDigest, topologyDigest) ||
            !ValidLoops(bundling.Program, loops, loopResourceModel, topologyDigest))
            return Baseline(IrPrefetchAdvisoryStatusV1.StaleInput, programDigest, scheduleDigest,
                bundleDigest, dependencyDigest, topologyDigest, options.OptionsDigest,
                fspEvidence.ReportDigest, profileDisposition, rankingProfile?.ProfileDigest,
                "HCPREF1003", "FSP, loop, schedule, bundle or topology evidence is stale or not qualified.");
        if (CanonicalIrFrontendBoundaryV1.Validate(bundling.Program).Status != IrFrontendAdapterStatus.Success)
            return Baseline(IrPrefetchAdvisoryStatusV1.InvalidModel, programDigest, scheduleDigest,
                bundleDigest, dependencyDigest, topologyDigest, options.OptionsDigest,
                fspEvidence.ReportDigest, profileDisposition, rankingProfile?.ProfileDigest,
                "HCPREF1004", "Canonical IR boundary rejected the advisory subject.");

        IrInstruction[] memoryInstructions = bundling.Program.Instructions
            .Where(static instruction => instruction.SideEffects.Memory.Kind != IrMemoryEffectKind.None)
            .OrderBy(static instruction => instruction.Index)
            .ToArray();
        long maximumComparisons = (long)memoryInstructions.Length * memoryInstructions.Length;
        int distanceEdgeCount = loops.Sum(static loop => loop.DistanceDependencies.Count);
        if (memoryInstructions.Length > options.Budgets.MaximumMemoryInstructions ||
            memoryInstructions.Length > options.Budgets.MaximumCandidates ||
            distanceEdgeCount > options.Budgets.MaximumDistanceEdges ||
            maximumComparisons > options.Budgets.MaximumPairComparisons)
            return Baseline(IrPrefetchAdvisoryStatusV1.BudgetExhausted, programDigest, scheduleDigest,
                bundleDigest, dependencyDigest, topologyDigest, options.OptionsDigest,
                fspEvidence.ReportDigest, profileDisposition, rankingProfile?.ProfileDigest,
                "HCPREF1005", "Advisory subject exceeds deterministic production bounds.");

        List<IrPrefetchAdvisoryCandidateV1> records = new List<IrPrefetchAdvisoryCandidateV1>(memoryInstructions.Length);
        int comparisons = 0;
        foreach (IrInstruction instruction in memoryInstructions)
        {
            IrCanonicalMemoryEffectV1 memory = instruction.SideEffects.Memory;
            IrMemoryRegion? region = memory.ReadRegion ?? instruction.Annotation.MemoryReadRegion;
            IrTopologyResourceFootprintV1 footprint = topologyModel.GetResourceFootprint(instruction);
            IrSchedulingRegionV1? schedulingRegion = bundling.ProgramSchedule.SchedulingRegions
                .FirstOrDefault(candidate => candidate.Blocks.Any(block =>
                    block.Instructions.Any(item => item.Index == instruction.Index)));
            IrCanonicalLoopV1? loop = loops.OrderBy(static item => item.LoopId, StringComparer.Ordinal)
                .FirstOrDefault(candidate => candidate.Instructions.Any(item => item.Index == instruction.Index));
            IrInstructionDependency[] dependencies = RelatedDependencies(
                bundling.ProgramSchedule.DependencyGraph, instruction.Index);
            string originalDependenceDigest = RelatedDependencyDigest(dependencies);
            (IrPrefetchDependenceRelationV1 Relation, IrFspEvidencePrecisionV1 Precision) dependence =
                Dependence(dependencies, loop, instruction.Index);
            int iterationDistance = IterationDistance(loop, instruction.Index);
            int reuseCount = 0;
            if (region is not null)
            {
                foreach (IrInstruction candidate in memoryInstructions)
                {
                    comparisons++;
                    IrCanonicalMemoryEffectV1 candidateMemory = candidate.SideEffects.Memory;
                    IrMemoryRegion? candidateRegion = candidateMemory.ReadRegion ?? candidate.Annotation.MemoryReadRegion;
                    if (candidateRegion is not null && SameRegion(region, candidateRegion) &&
                        candidateMemory.AddressSpace == memory.AddressSpace)
                        reuseCount++;
                }
            }

            IrFspStaticCandidateEvidenceV1? donor = fspEvidence.Candidates
                .Where(candidate => candidate.VirtualThreadId == instruction.VirtualThreadId &&
                                    candidate.Disposition == IrFspStaticCandidateDispositionV1.EligibleStaticCandidate)
                .OrderByDescending(static candidate => candidate.EstimatedStaticCycleValue.Value)
                .ThenBy(static candidate => candidate.InstructionIndex)
                .FirstOrDefault();
            IrPrefetchCandidateDispositionV1 disposition = Classify(
                instruction, memory, region, footprint, dependence, reuseCount, donor);
            int staticBenefit = disposition == IrPrefetchCandidateDispositionV1.EligibleConditionalAdvisory
                ? Math.Max(1, checked(reuseCount * 16 + Math.Max(0, iterationDistance) * 8))
                : 0;
            int score = Score(staticBenefit, iterationDistance,
                donor?.StaticHoleCount.Value ?? 0,
                donor?.RegisterGroupPressure.Value ?? 0,
                disposition,
                rankingProfile);
            string regionIdentity = schedulingRegion?.RegionId ?? "region-unknown";
            string addressDigest = AddressDigest(memory.AddressSpace, region);
            string candidateDigest = CandidateDigest(instruction, regionIdentity, loop?.LoopId,
                disposition, addressDigest, dependence.Relation, iterationDistance, reuseCount,
                staticBenefit, footprint, originalDependenceDigest, donor?.CandidateDigest,
                profileDisposition, rankingProfile?.ProfileDigest, score);
            records.Add(new(
                HybridCpuPrefetchAdvisoryContractV1.Hash($"candidate|{candidateDigest}"),
                instruction.StableIdentity,
                instruction.Index,
                instruction.VirtualThreadId,
                regionIdentity,
                loop?.LoopId,
                disposition,
                IrPrefetchConsumerRequirementV1.RejectUnlessCurrentNonFaultingContractAndAddressValidation,
                new(memory.AddressSpace, IrFspEvidencePrecisionV1.ExactStatic,
                    "CanonicalIr.SideEffects.Memory.AddressSpace"),
                new(region is null ? IrAddressEvidenceKindV1.Unknown : IrAddressEvidenceKindV1.Exact,
                    region is null ? IrFspEvidencePrecisionV1.Unknown : IrFspEvidencePrecisionV1.ExactStatic,
                    "CanonicalIr.SideEffects.Memory.ReadRegion"),
                addressDigest,
                region?.Length ?? 0,
                new(dependence.Relation, dependence.Precision,
                    "IrProgramDependencyGraph and IrCanonicalLoopV1.DistanceDependencies"),
                new(iterationDistance,
                    loop is null ? IrFspEvidencePrecisionV1.ExactStatic : dependence.Precision,
                    loop is null ? "IrSchedulingRegionV1.IntraRegionDistance" : "IrCanonicalLoopV1.IterationDistance"),
                new(reuseCount, region is null ? IrFspEvidencePrecisionV1.Unknown : IrFspEvidencePrecisionV1.ExactStatic,
                    "CanonicalIr.ExactReadRegionEquivalence"),
                new(staticBenefit,
                    rankingProfile is null ? IrFspEvidencePrecisionV1.ExactStatic : IrFspEvidencePrecisionV1.ProfileOnly,
                    rankingProfile is null ? "HybridCpuPrefetchAdvisoryPlannerV1.StaticBenefit" : IrPrefetchOfflineRankingProfileV1.SchemaId),
                new(footprint.Banks, AddressPrecision(footprint.Banks),
                    "IrTopologyResourceFootprintV1.Banks"),
                new(footprint.Channels, AddressPrecision(footprint.Channels),
                    "IrTopologyResourceFootprintV1.Channels"),
                new(donor?.ResourcePressureReasons.Value ?? IrFspResourcePressureReasonV1.UnknownResource,
                    donor is null ? IrFspEvidencePrecisionV1.Unknown : donor.ResourcePressureReasons.Precision,
                    "Phase18.FspStaticCandidateEvidence.ResourcePressureReasons"),
                originalDependenceDigest,
                dependencies.Length,
                fspEvidence.ReportDigest,
                donor?.CandidateDigest,
                profileDisposition,
                score,
                topologyDigest,
                candidateDigest));
        }

        records = records.OrderByDescending(static candidate => candidate.RankingScore)
            .ThenBy(static candidate => candidate.VirtualThreadId)
            .ThenBy(static candidate => candidate.ConsumerInstructionIndex)
            .ToList();
        int eligible = records.Count(static candidate =>
            candidate.Disposition == IrPrefetchCandidateDispositionV1.EligibleConditionalAdvisory);
        IrPrefetchAdvisoryCountersV1 counters = new IrPrefetchAdvisoryCountersV1(
            memoryInstructions.Length,
            comparisons,
            records.Count,
            eligible,
            checked(memoryInstructions.Length + comparisons + distanceEdgeCount));
        string reportDigest = ReportDigest(IrPrefetchAdvisoryStatusV1.EvidenceProduced,
            programDigest, scheduleDigest, bundleDigest, dependencyDigest, topologyDigest,
            options.OptionsDigest, fspEvidence.ReportDigest, rankingProfile?.ProfileDigest,
            records, counters);
        return new(
            IrPrefetchAdvisoryStatusV1.EvidenceProduced,
            IrPrefetchAdvisoryAuthorityV1.CompilerAdvisoryOnly,
            programDigest,
            scheduleDigest,
            bundleDigest,
            dependencyDigest,
            HybridCpuPrefetchAdvisoryContractV1.Default.TargetDigest,
            HybridCpuPrefetchAdvisoryContractV1.Default.MachineDigest,
            topologyDigest,
            HybridCpuPrefetchAdvisoryContractV1.Default.ContractDigest,
            options.OptionsDigest,
            fspEvidence.ReportDigest,
            profileDisposition,
            rankingProfile?.ProfileDigest,
            records,
            counters,
            Array.Empty<IrPrefetchAdvisoryDiagnosticV1>(),
            reportDigest);
    }

    private static IrPrefetchCandidateDispositionV1 Classify(
        IrInstruction instruction,
        IrCanonicalMemoryEffectV1 memory,
        IrMemoryRegion? region,
        IrTopologyResourceFootprintV1 footprint,
        (IrPrefetchDependenceRelationV1 Relation, IrFspEvidencePrecisionV1 Precision) dependence,
        int reuseCount,
        IrFspStaticCandidateEvidenceV1? donor)
    {
        if (memory.Kind != IrMemoryEffectKind.Read || memory.Ordering != IrMemoryOrdering.NotAtomic ||
            instruction.SideEffects.ArchitecturalEffects != IrArchitecturalEffectKind.None)
            return IrPrefetchCandidateDispositionV1.ExcludedByMemorySemantics;
        if (memory.AddressSpace is IrAddressSpaceIdentity.Device or IrAddressSpaceIdentity.Unknown)
            return IrPrefetchCandidateDispositionV1.ExcludedByAddressSpace;
        if (region is null || region.Length == 0 || region.Address > ulong.MaxValue - (region.Length - 1UL))
            return IrPrefetchCandidateDispositionV1.ExcludedByAddressEvidence;
        ulong endAddress = region.Address + region.Length - 1UL;
        if (region.Address / PageSize != endAddress / PageSize)
            return IrPrefetchCandidateDispositionV1.ExcludedByFaultBoundary;
        if (instruction.OriginChain.Links.Any(static link => link.Kind == IrSourceOriginKind.Cil))
            return IrPrefetchCandidateDispositionV1.ExcludedByManagedReference;
        if (footprint.CertificateClass != CompilerCertificateClassV1.None ||
            footprint.CertificatePrecision != IrResourceFactPrecisionV1.Exact)
            return IrPrefetchCandidateDispositionV1.ExcludedBySpecialContour;
        if (dependence.Precision == IrFspEvidencePrecisionV1.Unknown ||
            dependence.Relation == IrPrefetchDependenceRelationV1.Unknown)
            return IrPrefetchCandidateDispositionV1.ExcludedByUnknownDependence;
        if (reuseCount < 2)
            return IrPrefetchCandidateDispositionV1.ExcludedByMissingReuse;
        return donor is null
            ? IrPrefetchCandidateDispositionV1.ExcludedByMissingDonorEvidence
            : IrPrefetchCandidateDispositionV1.EligibleConditionalAdvisory;
    }

    private static (IrPrefetchDependenceRelationV1 Relation, IrFspEvidencePrecisionV1 Precision) Dependence(
        IReadOnlyList<IrInstructionDependency> dependencies,
        IrCanonicalLoopV1? loop,
        int instructionIndex)
    {
        IrInstructionDependency[] memory = dependencies
            .Where(static dependency => dependency.Kind == IrInstructionDependencyKind.Memory)
            .ToArray();
        IrLoopDistanceDependencyV1[] loopMemory = loop?.DistanceDependencies
            .Where(edge => edge.Kind == IrInstructionDependencyKind.Memory &&
                           (edge.ProducerInstructionIndex == instructionIndex ||
                            edge.ConsumerInstructionIndex == instructionIndex))
            .ToArray() ?? Array.Empty<IrLoopDistanceDependencyV1>();
        if (memory.Any(static dependency => dependency.MemoryPrecision == IrMemoryDependencyPrecision.May) ||
            loopMemory.Any(static edge => edge.Precision == IrLoopProofPrecisionV1.Unknown))
            return (IrPrefetchDependenceRelationV1.Unknown, IrFspEvidencePrecisionV1.Unknown);
        if (memory.Any(static dependency => dependency.MemoryPrecision == IrMemoryDependencyPrecision.Must) ||
            loopMemory.Length != 0)
            return (IrPrefetchDependenceRelationV1.MustPreserveOriginalMemoryEdge,
                loopMemory.Any(static edge => edge.Precision == IrLoopProofPrecisionV1.Conservative)
                    ? IrFspEvidencePrecisionV1.ConservativeSet
                    : IrFspEvidencePrecisionV1.ExactStatic);
        return (IrPrefetchDependenceRelationV1.NoOriginalMemoryEdge,
            IrFspEvidencePrecisionV1.ExactStatic);
    }

    private static int IterationDistance(IrCanonicalLoopV1? loop, int instructionIndex) => loop?.DistanceDependencies
        .Where(edge => edge.Kind == IrInstructionDependencyKind.Memory &&
                       (edge.ProducerInstructionIndex == instructionIndex ||
                        edge.ConsumerInstructionIndex == instructionIndex))
        .Select(static edge => edge.IterationDistance)
        .DefaultIfEmpty(0)
        .Min() ?? 0;

    private static IrInstructionDependency[] RelatedDependencies(
        IrProgramDependencyGraph graph,
        int instructionIndex) => graph.GetIncomingDependencies(instructionIndex)
        .Concat(graph.GetOutgoingDependencies(instructionIndex))
        .Distinct()
        .OrderBy(static dependency => dependency.ProducerInstructionIndex)
        .ThenBy(static dependency => dependency.ConsumerInstructionIndex)
        .ThenBy(static dependency => dependency.Kind)
        .ToArray();

    private static bool SameRegion(IrMemoryRegion left, IrMemoryRegion right) =>
        left.Address == right.Address && left.Length == right.Length;

    private static bool ValidFspEvidence(
        IrFspStaticEvidenceReportV1 evidence,
        string programDigest,
        string scheduleDigest,
        string bundleDigest,
        string topologyDigest) => evidence.Status == IrFspEvidenceStatusV1.Accepted &&
        evidence.Authority == IrFspEvidenceAuthorityV1.CompilerEvidenceOnly &&
        string.Equals(evidence.ProgramDigest, programDigest, StringComparison.Ordinal) &&
        string.Equals(evidence.ScheduleDigest, scheduleDigest, StringComparison.Ordinal) &&
        string.Equals(evidence.BundleDigest, bundleDigest, StringComparison.Ordinal) &&
        string.Equals(evidence.TopologyDigest, topologyDigest, StringComparison.Ordinal) &&
        string.Equals(evidence.ModelDigest, HybridCpuFspEvidenceContractV1.Default.ContractDigest,
            StringComparison.Ordinal);

    private static bool ValidLoops(
        IrProgram program,
        IReadOnlyList<IrCanonicalLoopV1> loops,
        HybridCpuMiiResourceModelV1 resourceModel,
        string topologyDigest)
    {
        if (!string.Equals(resourceModel.Topology.ContractDigest, topologyDigest, StringComparison.Ordinal))
            return false;
        if (loops.Count == 0) return true;
        IrLoopCanonicalizationResultV1 canonical = new HybridCpuLoopMiiAnalyzerV1().Canonicalize(
            program,
            resourceModel);
        if (canonical.Status != IrCanonicalLoopStatusV1.Qualified) return false;
        foreach (IrCanonicalLoopV1 loop in loops)
        {
            IrCanonicalLoopV1? current = canonical.Loops.SingleOrDefault(candidate =>
                string.Equals(candidate.LoopId, loop.LoopId, StringComparison.Ordinal));
            if (current is null || loop.Status != IrCanonicalLoopStatusV1.Qualified ||
                loop.MutationStamp != program.Contract.DerivedFacts.ProgramMutation ||
                !string.Equals(loop.VersionStamp, current.VersionStamp, StringComparison.Ordinal) ||
                !string.Equals(loop.ProgramVersionStamp, current.ProgramVersionStamp, StringComparison.Ordinal) ||
                !string.Equals(loop.InstructionDigest, current.InstructionDigest, StringComparison.Ordinal) ||
                !string.Equals(loop.DistanceDagDigest, current.DistanceDagDigest, StringComparison.Ordinal) ||
                !string.Equals(loop.ValueAnalysis.ValueFlowDigest, current.ValueAnalysis.ValueFlowDigest,
                    StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static IrFspEvidencePrecisionV1 AddressPrecision(IrAddressResourceEvidenceV1 evidence) =>
        evidence.Precision switch
        {
            IrAddressEvidenceKindV1.Exact => IrFspEvidencePrecisionV1.ExactStatic,
            IrAddressEvidenceKindV1.FiniteSet or IrAddressEvidenceKindV1.All => IrFspEvidencePrecisionV1.ConservativeSet,
            _ => IrFspEvidencePrecisionV1.Unknown
        };

    private static int Score(
        int benefit,
        int distance,
        int holes,
        int pressure,
        IrPrefetchCandidateDispositionV1 disposition,
        IrPrefetchOfflineRankingProfileV1? profile)
    {
        if (disposition != IrPrefetchCandidateDispositionV1.EligibleConditionalAdvisory) return 0;
        int reuseWeight = profile?.ReuseWeight ?? 8;
        int distanceWeight = profile?.DistanceWeight ?? 4;
        int holeWeight = profile?.HoleWeight ?? 2;
        int pressureWeight = profile?.PressurePenaltyWeight ?? 2;
        return Math.Max(0, checked(benefit * reuseWeight + Math.Max(0, distance) * distanceWeight +
            holes * holeWeight - pressure * pressureWeight));
    }

    private static string AddressDigest(IrAddressSpaceIdentity addressSpace, IrMemoryRegion? region) =>
        HybridCpuPrefetchAdvisoryContractV1.Hash(region is null
            ? $"address|{(byte)addressSpace}|unknown"
            : $"address|{(byte)addressSpace}|{region.Address}|{region.Length}");

    private static string RelatedDependencyDigest(IReadOnlyList<IrInstructionDependency> dependencies) =>
        HybridCpuPrefetchAdvisoryContractV1.Hash("related|" + string.Join('|', dependencies.Select(DependencyKey)));

    private static string DependencyGraphDigest(IrProgramDependencyGraph graph) =>
        HybridCpuPrefetchAdvisoryContractV1.Hash("dependency-graph|" + string.Join('|', graph.BlockGraphs
            .OrderBy(static block => block.BlockId)
            .SelectMany(block => block.Dependencies
                .OrderBy(static dependency => dependency.ProducerInstructionIndex)
                .ThenBy(static dependency => dependency.ConsumerInstructionIndex)
                .ThenBy(static dependency => dependency.Kind)
                .Select(dependency => $"block:{block.BlockId}:{DependencyKey(dependency)}"))
            .Concat(graph.InterBlockGraph.Dependencies
                .OrderBy(static dependency => dependency.SourceBlockId)
                .ThenBy(static dependency => dependency.TargetBlockId)
                .ThenBy(static dependency => dependency.Dependency.ProducerInstructionIndex)
                .ThenBy(static dependency => dependency.Dependency.ConsumerInstructionIndex)
                .Select(dependency => $"edge:{dependency.SourceBlockId}:{dependency.TargetBlockId}:" +
                                      $"{(byte)dependency.EdgeKind}:{DependencyKey(dependency.Dependency)}"))));

    private static string DependencyKey(IrInstructionDependency dependency) => string.Join(':',
        dependency.ProducerInstructionIndex,
        dependency.ConsumerInstructionIndex,
        (byte)dependency.Kind,
        dependency.MinimumLatencyCycles,
        (byte)dependency.MemoryPrecision,
        (byte)dependency.DominantEffectKind);

    private static string CandidateDigest(
        IrInstruction instruction,
        string regionIdentity,
        string? loopIdentity,
        IrPrefetchCandidateDispositionV1 disposition,
        string addressDigest,
        IrPrefetchDependenceRelationV1 dependence,
        int distance,
        int reuse,
        int benefit,
        IrTopologyResourceFootprintV1 footprint,
        string dependencyDigest,
        string? donorDigest,
        IrPrefetchProfileDispositionV1 profileDisposition,
        string? profileDigest,
        int score) => HybridCpuPrefetchAdvisoryContractV1.Hash(string.Join('|',
            HybridCpuPrefetchAdvisoryContractV1.SchemaId,
            instruction.StableIdentity,
            instruction.Index,
            instruction.VirtualThreadId,
            regionIdentity,
            loopIdentity ?? "no-loop",
            (byte)disposition,
            addressDigest,
            (byte)dependence,
            distance,
            reuse,
            benefit,
            footprint.Fingerprint,
            dependencyDigest,
            donorDigest ?? "no-donor",
            (byte)profileDisposition,
            profileDigest ?? "no-profile",
            score));

    private static string ReportDigest(
        IrPrefetchAdvisoryStatusV1 status,
        string programDigest,
        string scheduleDigest,
        string bundleDigest,
        string dependencyDigest,
        string topologyDigest,
        string optionsDigest,
        string fspDigest,
        string? profileDigest,
        IReadOnlyList<IrPrefetchAdvisoryCandidateV1> candidates,
        IrPrefetchAdvisoryCountersV1 counters)
    {
        StringBuilder payload = new StringBuilder(HybridCpuPrefetchAdvisoryContractV1.SchemaId)
            .Append('|').Append((byte)status)
            .Append('|').Append(programDigest)
            .Append('|').Append(scheduleDigest)
            .Append('|').Append(bundleDigest)
            .Append('|').Append(dependencyDigest)
            .Append('|').Append(HybridCpuPrefetchAdvisoryContractV1.Default.TargetDigest)
            .Append('|').Append(HybridCpuPrefetchAdvisoryContractV1.Default.MachineDigest)
            .Append('|').Append(topologyDigest)
            .Append('|').Append(HybridCpuPrefetchAdvisoryContractV1.Default.ContractDigest)
            .Append('|').Append(optionsDigest)
            .Append('|').Append(fspDigest)
            .Append('|').Append(profileDigest ?? "no-profile")
            .Append('|').Append(counters.MemoryInstructionsVisited)
            .Append(':').Append(counters.PairComparisons)
            .Append(':').Append(counters.CandidatesProduced)
            .Append(':').Append(counters.EligibleConditionalAdvisories)
            .Append(':').Append(counters.DeterministicWorkUnits);
        foreach (IrPrefetchAdvisoryCandidateV1 candidate in candidates)
            payload.Append('|').Append(candidate.CandidateDigest);
        return HybridCpuPrefetchAdvisoryContractV1.Hash(payload.ToString());
    }

    private static bool HasValidOptions(HybridCpuPrefetchAdvisoryOptionsV1 options)
    {
        HybridCpuPrefetchAdvisoryBudgetsV1 budgets = options.Budgets;
        if (budgets is null || budgets.MaximumMemoryInstructions is < 1 or > 512 ||
            budgets.MaximumCandidates is < 1 or > 256 ||
            budgets.MaximumDistanceEdges is < 1 or > 4096 ||
            budgets.MaximumPairComparisons is < 1 or > 65536)
            return false;
        return options == HybridCpuPrefetchAdvisoryOptionsV1.Create(options.AdvisorySwitch, budgets);
    }

    private static bool HasValidProfile(IrPrefetchOfflineRankingProfileV1? profile)
    {
        if (profile is null) return true;
        try
        {
            return profile == IrPrefetchOfflineRankingProfileV1.Create(profile.ModelDigest,
                profile.ReuseWeight, profile.DistanceWeight, profile.HoleWeight,
                profile.PressurePenaltyWeight);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IrPrefetchAdvisoryReportV1 Baseline(
        IrPrefetchAdvisoryStatusV1 status,
        string programDigest,
        string scheduleDigest,
        string bundleDigest,
        string dependencyDigest,
        string topologyDigest,
        string optionsDigest,
        string fspDigest,
        IrPrefetchProfileDispositionV1 profileDisposition,
        string? profileDigest,
        string code,
        string message)
    {
        IrPrefetchAdvisoryCountersV1 counters = new IrPrefetchAdvisoryCountersV1(0, 0, 0, 0, 0);
        string reportDigest = ReportDigest(status, programDigest, scheduleDigest, bundleDigest,
            dependencyDigest, topologyDigest, optionsDigest, fspDigest, profileDigest,
            Array.Empty<IrPrefetchAdvisoryCandidateV1>(), counters);
        return new(status,
            IrPrefetchAdvisoryAuthorityV1.CompilerAdvisoryOnly,
            programDigest,
            scheduleDigest,
            bundleDigest,
            dependencyDigest,
            HybridCpuPrefetchAdvisoryContractV1.Default.TargetDigest,
            HybridCpuPrefetchAdvisoryContractV1.Default.MachineDigest,
            topologyDigest,
            HybridCpuPrefetchAdvisoryContractV1.Default.ContractDigest,
            optionsDigest,
            fspDigest,
            profileDisposition,
            profileDigest,
            Array.Empty<IrPrefetchAdvisoryCandidateV1>(),
            counters,
            [new(code, message)],
            reportDigest);
    }
}
