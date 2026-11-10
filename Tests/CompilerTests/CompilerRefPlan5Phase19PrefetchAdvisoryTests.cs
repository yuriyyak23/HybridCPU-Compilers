using System.Globalization;
using System.Reflection;
using System.Text.Json;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Authority;
using HybridCPU.Compiler.Core.IR.Contracts;
using HybridCPU.Compiler.Core.IR.Fsp;
using HybridCPU.Compiler.Core.IR.Prefetch;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.IR.Telemetry;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase19PrefetchAdvisoryTests
{
    [Fact]
    public void ContractIsPinnedBoundedVersionedDefaultOffAndAdvisoryOnly()
    {
        HybridCpuPrefetchAdvisoryContractV1 contract = HybridCpuPrefetchAdvisoryContractV1.Default;
        Assert.Equal("hybridcpu.vdsa-prefetch-advisory/v1", HybridCpuPrefetchAdvisoryContractV1.SchemaId);
        Assert.Equal("fdf90ee723dbfee817b76398d2eaad89f4e6a791beadf20254fec346a66076c3",
            contract.ContractDigest);
        Assert.Equal("432c61dda9b4188cf7e63fe8bb3cda08bfd6b189059ce54f7b43616155c9dd2e",
            contract.ProductionOptionsDigest);
        Assert.Equal("0011390132d7641a2a63646c99d4aa19d59d6c63d0a59500a43cd88ae5b485ca",
            contract.QualificationOptionsDigest);
        Assert.Equal(IrPrefetchAdvisorySwitchV1.Disabled,
            HybridCpuPrefetchAdvisoryOptionsV1.Production.AdvisorySwitch);
        Assert.Equal(new HybridCpuPrefetchAdvisoryBudgetsV1(512, 256, 4096, 65536),
            HybridCpuPrefetchAdvisoryOptionsV1.Production.Budgets);
        Assert.Equal(new CompilerSchemaVersion(1, 0), contract.SchemaDeclaration.Version);
        Assert.Equal(CompilerAuthorityClass.CompilerEvidenceProduction,
            contract.SchemaDeclaration.AuthorityClass);
        Assert.Equal("ae6ee979176be7f21dc396fe0ddb5d7023d8b6de1722eb4d4652647dd3e99232",
            contract.TargetDigest);
        Assert.Equal("ee837ac7c7bc9c81ac254ca133b6b7233c0d2a857d8c8371f25b570b65aefe0d",
            contract.MachineDigest);
        CompilerSchemaCompatibility.ValidateDeclaration(contract.SchemaDeclaration);
    }

    [Fact]
    public void ProductionKillSwitchAndIgnoredEvidencePreserveScheduleBundlesAndDependencies()
    {
        Subject subject = ReuseSubject();
        string beforeSchedule = CompilerScheduleFingerprintV1.HashSchedule(subject.Bundling.ProgramSchedule);
        string beforeBundles = CompilerScheduleFingerprintV1.HashBundles(subject.Bundling);
        string[] beforeDependencies = DependencyKeys(subject);

        IrPrefetchAdvisoryReportV1 report = new HybridCpuPrefetchAdvisoryPlannerV1().Analyze(
            subject.Bundling, subject.FspEvidence);

        Assert.Equal(IrPrefetchAdvisoryStatusV1.DisabledBaseline, report.Status);
        Assert.Equal(IrPrefetchAdvisoryAuthorityV1.CompilerAdvisoryOnly, report.Authority);
        Assert.Empty(report.Candidates);
        Assert.Equal(beforeSchedule, CompilerScheduleFingerprintV1.HashSchedule(subject.Bundling.ProgramSchedule));
        Assert.Equal(beforeBundles, CompilerScheduleFingerprintV1.HashBundles(subject.Bundling));
        Assert.Equal(beforeDependencies, DependencyKeys(subject));
    }

    [Fact]
    public void ExactRepeatedReadProducesConditionalFaultNeutralAdvisoryWithPhase18Provenance()
    {
        Subject subject = ReuseSubject();
        IrPrefetchAdvisoryReportV1 report = Analyze(subject);
        IrPrefetchAdvisoryCandidateV1[] eligible = report.Candidates
            .Where(static candidate =>
                candidate.Disposition == IrPrefetchCandidateDispositionV1.EligibleConditionalAdvisory)
            .ToArray();

        Assert.Equal(IrPrefetchAdvisoryStatusV1.EvidenceProduced, report.Status);
        Assert.Equal(2, eligible.Length);
        Assert.All(eligible, candidate =>
        {
            Assert.Equal(
                IrPrefetchConsumerRequirementV1.RejectUnlessCurrentNonFaultingContractAndAddressValidation,
                candidate.ConsumerRequirement);
            Assert.Equal(IrAddressSpaceIdentity.Generic, candidate.AddressSpace.Value);
            Assert.Equal(IrAddressEvidenceKindV1.Exact, candidate.AddressPrecision.Value);
            Assert.Equal(IrFspEvidencePrecisionV1.ExactStatic, candidate.AddressPrecision.Precision);
            Assert.Equal(2, candidate.ReuseCount.Value);
            Assert.True(candidate.ExpectedStaticLatencyBenefit.Value > 0);
            Assert.NotNull(candidate.DonorEvidenceDigest);
            Assert.Equal(subject.FspEvidence.ReportDigest, candidate.FspReportDigest);
            Assert.Equal(IrPrefetchProfileDispositionV1.AbsentStaticPolicy,
                candidate.ProfileDisposition);
            Assert.False(string.IsNullOrWhiteSpace(candidate.OriginalDependenceDigest));
            Assert.False(string.IsNullOrWhiteSpace(candidate.TopologyDigest));
            Assert.False(string.IsNullOrWhiteSpace(candidate.CandidateDigest));
        });
    }

    [Theory]
    [InlineData(IrMemoryEffectKind.Write, IrMemoryOrdering.NotAtomic)]
    [InlineData(IrMemoryEffectKind.Read | IrMemoryEffectKind.Volatile, IrMemoryOrdering.NotAtomic)]
    [InlineData(IrMemoryEffectKind.Read | IrMemoryEffectKind.Atomic, IrMemoryOrdering.Relaxed)]
    [InlineData(IrMemoryEffectKind.Read | IrMemoryEffectKind.Fence, IrMemoryOrdering.Acquire)]
    [InlineData(IrMemoryEffectKind.Unknown, IrMemoryOrdering.Unknown)]
    public void WritesAtomicVolatileFenceAndUnknownMemoryAreNeverAdvisory(
        IrMemoryEffectKind kind,
        IrMemoryOrdering ordering)
    {
        Subject subject = SubjectFor(instruction => instruction.Index < 2
            ? WithMemory(instruction, 0x1000, 8, kind, IrAddressSpaceIdentity.Generic, ordering)
            : instruction);
        Assert.All(Analyze(subject).Candidates, candidate => Assert.Equal(
            IrPrefetchCandidateDispositionV1.ExcludedByMemorySemantics, candidate.Disposition));
    }

    [Theory]
    [InlineData(IrAddressSpaceIdentity.Device)]
    [InlineData(IrAddressSpaceIdentity.Unknown)]
    public void DeviceAndUnknownAddressSpacesFailClosed(IrAddressSpaceIdentity addressSpace)
    {
        Subject subject = SubjectFor(instruction => instruction.Index < 2
            ? WithMemory(instruction, 0x1000, 8, IrMemoryEffectKind.Read, addressSpace,
                IrMemoryOrdering.NotAtomic)
            : instruction);
        Assert.All(Analyze(subject).Candidates, candidate => Assert.Equal(
            IrPrefetchCandidateDispositionV1.ExcludedByAddressSpace, candidate.Disposition));
    }

    [Fact]
    public void MissingAddressAndPageBoundaryAreExplicitNegativeControls()
    {
        Subject missing = SubjectFor(instruction => instruction.Index < 2
            ? WithMemory(instruction, null, IrMemoryEffectKind.Read, IrAddressSpaceIdentity.Generic,
                IrMemoryOrdering.NotAtomic)
            : instruction);
        Assert.All(Analyze(missing).Candidates, candidate => Assert.Equal(
            IrPrefetchCandidateDispositionV1.ExcludedByAddressEvidence, candidate.Disposition));

        Subject boundary = SubjectFor(instruction => instruction.Index < 2
            ? WithMemory(instruction, 0x0ff8, 16, IrMemoryEffectKind.Read,
                IrAddressSpaceIdentity.Generic, IrMemoryOrdering.NotAtomic)
            : instruction);
        Assert.All(Analyze(boundary).Candidates, candidate => Assert.Equal(
            IrPrefetchCandidateDispositionV1.ExcludedByFaultBoundary, candidate.Disposition));
    }

    [Fact]
    public void ManagedReferenceAndSpecialContourRemainUnsupported()
    {
        Subject managed = SubjectFor(instruction => instruction.Index < 2
            ? WithManagedOrigin(WithMemory(instruction, 0x1000, 8, IrMemoryEffectKind.Read,
                IrAddressSpaceIdentity.Generic, IrMemoryOrdering.NotAtomic))
            : instruction);
        Assert.All(Analyze(managed).Candidates, candidate => Assert.Equal(
            IrPrefetchCandidateDispositionV1.ExcludedByManagedReference, candidate.Disposition));

        Subject special = SubjectFor(instruction => instruction.Index < 2
            ? WithSlot(WithMemory(instruction, 0x1000, 8, IrMemoryEffectKind.Read,
                IrAddressSpaceIdentity.Generic, IrMemoryOrdering.NotAtomic),
                IrIssueSlotMask.Slot6, IrSlotClass.DmaStreamClass)
            : instruction);
        Assert.All(Analyze(special).Candidates, candidate => Assert.Equal(
            IrPrefetchCandidateDispositionV1.ExcludedBySpecialContour, candidate.Disposition));
    }

    [Fact]
    public void MissingReuseAndMissingDonorEvidenceRemainNonOpportunities()
    {
        Subject noReuse = SubjectFor(instruction => instruction.Index < 2
            ? WithMemory(instruction, 0x1000UL + (ulong)instruction.Index * 64, 8,
                IrMemoryEffectKind.Read, IrAddressSpaceIdentity.Generic, IrMemoryOrdering.NotAtomic)
            : instruction);
        Assert.All(Analyze(noReuse).Candidates, candidate => Assert.Equal(
            IrPrefetchCandidateDispositionV1.ExcludedByMissingReuse, candidate.Disposition));

        Subject noDonor = SubjectFor(instruction => WithMemory(instruction, 0x1000, 8,
            IrMemoryEffectKind.Read, IrAddressSpaceIdentity.Generic, IrMemoryOrdering.NotAtomic));
        Assert.All(Analyze(noDonor).Candidates, candidate => Assert.Equal(
            IrPrefetchCandidateDispositionV1.ExcludedByMissingDonorEvidence, candidate.Disposition));
    }

    [Fact]
    public void StalePhase18EvidenceAndDeterministicBudgetsReturnExactBaselineDigests()
    {
        Subject subject = ReuseSubject();
        IrFspStaticEvidenceReportV1 staleFsp = subject.FspEvidence with
        {
            BundleDigest = new string('0', 64)
        };
        HybridCpuPrefetchAdvisoryPlannerV1 planner = new HybridCpuPrefetchAdvisoryPlannerV1();
        IrPrefetchAdvisoryReportV1 stale = planner.Analyze(
            subject.Bundling,
            staleFsp,
            options: HybridCpuPrefetchAdvisoryOptionsV1.Qualification);
        Assert.Equal(IrPrefetchAdvisoryStatusV1.StaleInput, stale.Status);
        Assert.Empty(stale.Candidates);
        Assert.Equal(subject.FspEvidence.ScheduleDigest, stale.ScheduleDigest);

        Subject changedAddress = SubjectFor(instruction => instruction.Index < 2
            ? WithMemory(instruction, 0x2000, 8, IrMemoryEffectKind.Read,
                IrAddressSpaceIdentity.Generic, IrMemoryOrdering.NotAtomic)
            : instruction);
        IrPrefetchAdvisoryReportV1 staleAddress = planner.Analyze(
            changedAddress.Bundling,
            subject.FspEvidence,
            options: HybridCpuPrefetchAdvisoryOptionsV1.Qualification);
        Assert.Equal(IrPrefetchAdvisoryStatusV1.StaleInput, staleAddress.Status);
        Assert.Empty(staleAddress.Candidates);

        HybridCpuPrefetchAdvisoryOptionsV1 tiny = HybridCpuPrefetchAdvisoryOptionsV1.Create(
            IrPrefetchAdvisorySwitchV1.ExplicitQualificationOnly,
            HybridCpuPrefetchAdvisoryBudgetsV1.Production with { MaximumMemoryInstructions = 1 });
        IrPrefetchAdvisoryReportV1 bounded = planner.Analyze(
            subject.Bundling, subject.FspEvidence, options: tiny);
        Assert.Equal(IrPrefetchAdvisoryStatusV1.BudgetExhausted, bounded.Status);
        Assert.Empty(bounded.Candidates);
        Assert.Equal(subject.FspEvidence.BundleDigest, bounded.BundleDigest);
    }

    [Fact]
    public void ProfilePerturbationChangesRankingOnlyAndNotSafeCandidateSet()
    {
        Subject subject = ReuseSubject();
        IrPrefetchAdvisoryReportV1 baseline = Analyze(subject);
        IrPrefetchOfflineRankingProfileV1 profile = IrPrefetchOfflineRankingProfileV1.Create(
            HybridCpuPrefetchAdvisoryContractV1.Default.ContractDigest,
            reuseWeight: 1,
            distanceWeight: 100,
            holeWeight: 50,
            pressurePenaltyWeight: 0);
        IrPrefetchAdvisoryReportV1 ranked = new HybridCpuPrefetchAdvisoryPlannerV1().Analyze(
            subject.Bundling,
            subject.FspEvidence,
            options: HybridCpuPrefetchAdvisoryOptionsV1.Qualification,
            rankingProfile: profile);

        Assert.Equal(
            baseline.Candidates.Select(static candidate => (candidate.ConsumerInstructionIndex, candidate.Disposition)).Order(),
            ranked.Candidates.Select(static candidate => (candidate.ConsumerInstructionIndex, candidate.Disposition)).Order());
        Assert.All(ranked.Candidates, candidate =>
            Assert.Equal(IrPrefetchProfileDispositionV1.VersionedOfflineRankingOnly,
                candidate.ProfileDisposition));
        Assert.Contains(ranked.Candidates, candidate =>
            candidate.ExpectedStaticLatencyBenefit.Precision == IrFspEvidencePrecisionV1.ProfileOnly);
    }

    [Fact]
    public void CanonicalLoopIdentityAndDistanceDagAreRevalidatedBeforePlanning()
    {
        (Subject Subject, IrCanonicalLoopV1 Loop, HybridCpuMiiResourceModelV1 Model) = LoopReuseSubject();
        HybridCpuPrefetchAdvisoryPlannerV1 planner = new HybridCpuPrefetchAdvisoryPlannerV1();
        IrPrefetchAdvisoryReportV1 report = planner.Analyze(
            Subject.Bundling,
            Subject.FspEvidence,
            [Loop],
            loopResourceModel: Model,
            options: HybridCpuPrefetchAdvisoryOptionsV1.Qualification);
        IrPrefetchAdvisoryCandidateV1[] loopCandidates = report.Candidates
            .Where(candidate => candidate.LoopIdentity == Loop.LoopId)
            .ToArray();

        Assert.NotEmpty(loopCandidates);
        Assert.All(loopCandidates, candidate =>
            Assert.Contains("IrCanonicalLoopV1", candidate.IterationDistance.Provenance));

        IrPrefetchAdvisoryReportV1 forged = planner.Analyze(
            Subject.Bundling,
            Subject.FspEvidence,
            [Loop with { DistanceDagDigest = new string('0', 64) }],
            loopResourceModel: Model,
            options: HybridCpuPrefetchAdvisoryOptionsV1.Qualification);
        Assert.Equal(IrPrefetchAdvisoryStatusV1.StaleInput, forged.Status);
        Assert.Empty(forged.Candidates);
    }

    [Fact]
    public void PlanningIsRepeatedRunAndCultureDeterministicAndNeverMutatesDependencies()
    {
        Subject subject = ReuseSubject();
        string[] dependencies = DependencyKeys(subject);
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            IrPrefetchAdvisoryReportV1 first = Analyze(subject);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            IrPrefetchAdvisoryReportV1 second = Analyze(subject);
            Assert.Equal(first.ReportDigest, second.ReportDigest);
            Assert.Equal(first.Candidates.Select(static candidate => candidate.CandidateDigest),
                second.Candidates.Select(static candidate => candidate.CandidateDigest));
            Assert.Equal(dependencies, DependencyKeys(subject));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void SchemaContainsNoRuntimePermissionFreshnessOrManagedAddressFields()
    {
        Subject subject = ReuseSubject();
        IrPrefetchAdvisoryReportV1 report = Analyze(subject);
        string[] forbidden =
        [
            "Epoch", "Generation", "Fresh", "Owner", "Reservation", "Admission",
            "LegalityDecision", "Execute", "Commit", "Retire", "ReplayValid", "ManagedAddress"
        ];
        string[] propertyNames = typeof(IrPrefetchAdvisoryReportV1).GetProperties()
            .Concat(typeof(IrPrefetchAdvisoryCandidateV1).GetProperties())
            .Select(static property => property.Name)
            .ToArray();
        Assert.DoesNotContain(propertyNames, name => forbidden.Any(token =>
            name.Contains(token, StringComparison.OrdinalIgnoreCase)));
        string json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("LegalityDecision", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ManagedAddress", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(IrPrefetchAdvisoryAuthorityV1.CompilerAdvisoryOnly, report.Authority);
    }

    [Fact]
    public void PlannerStaysCoreOnlyHasNoProductionCallerAndCannotEmitOrConsumeHints()
    {
        Assembly core = typeof(HybridCpuPrefetchAdvisoryPlannerV1).Assembly;
        Assert.Equal("HybridCPU.Compiler.Core", core.GetName().Name);
        Assert.DoesNotContain(core.GetReferencedAssemblies(), reference =>
            reference.Name!.Contains("LLVM", StringComparison.OrdinalIgnoreCase) ||
            reference.Name.Contains("Oracle", StringComparison.OrdinalIgnoreCase) ||
            reference.Name.Contains("HybridCPU_ISE", StringComparison.Ordinal));
        string root = FindRepositoryRoot();
        string[] production = Directory.EnumerateFiles(
                Path.Combine(root, "Compilers", "HybridCPU_Compiler"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith("HybridCpuPrefetchAdvisoryPlannerV1.cs", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith("HybridCpuPrefetchAdvisoryContractsV1.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.DoesNotContain(production, path => File.ReadAllText(path)
            .Contains("HybridCpuPrefetchAdvisoryPlannerV1", StringComparison.Ordinal));
    }

    private static IrPrefetchAdvisoryReportV1 Analyze(Subject subject) =>
        new HybridCpuPrefetchAdvisoryPlannerV1().Analyze(
            subject.Bundling,
            subject.FspEvidence,
            options: HybridCpuPrefetchAdvisoryOptionsV1.Qualification);

    private static Subject ReuseSubject() => SubjectFor(instruction => instruction.Index < 2
        ? WithMemory(instruction, 0x1000, 8, IrMemoryEffectKind.Read,
            IrAddressSpaceIdentity.Generic, IrMemoryOrdering.NotAtomic)
        : instruction);

    private static (Subject Subject, IrCanonicalLoopV1 Loop, HybridCpuMiiResourceModelV1 Model)
        LoopReuseSubject()
    {
        IrProgram original = new HybridCpuIrBuilder().BuildProgram(0, Words(4));
        IrInstruction[] source = original.Instructions.ToArray();
        IrBasicBlock preheader = new IrBasicBlock(
            0, 0, 0, source[0].EncodedAddress, source[0].EncodedAddress,
            false, [source[0]], [], [1], false, false, null, [], null, null);
        IrBasicBlock loopBlock = new IrBasicBlock(
            1, 1, source.Length - 1, source[1].EncodedAddress, source[^1].EncodedAddress,
            false, source[1..], [0, 1], [1], false, false, null, [], null, null);
        IrProgram loopProgram = original with
        {
            ControlFlowGraph = new(
                [preheader, loopBlock],
                [new(0, 1, IrControlFlowEdgeKind.Fallthrough), new(1, 1, IrControlFlowEdgeKind.Branch)])
        };
        loopProgram = Transform(loopProgram, instruction => instruction.Index is 1 or 2
            ? WithMemory(instruction, 0x1800, 8, IrMemoryEffectKind.Read,
                IrAddressSpaceIdentity.Generic, IrMemoryOrdering.NotAtomic)
            : instruction);
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(loopProgram);
        IrProgramBundlingResult bundling = new HybridCpuBundleFormer().BundleProgram(schedule);
        IrFspStaticEvidenceReportV1 fsp = new HybridCpuFspStaticEvidenceAnalyzerV1().Analyze(
            bundling,
            options: HybridCpuFspEvidenceOptionsV1.Qualification);
        HybridCpuMiiResourceModelV1 model = HybridCpuMiiResourceModelV1.Create(
            HybridCpuMachineTopologyV1.Default,
            structuralCertificateCapacity: null);
        IrCanonicalLoopV1 loop = Assert.Single(
            new HybridCpuLoopMiiAnalyzerV1().Canonicalize(bundling.Program, model).Loops);
        return (new(bundling, fsp), loop, model);
    }

    private static Subject SubjectFor(Func<IrInstruction, IrInstruction> transform)
    {
        IrProgram program = new HybridCpuIrBuilder().BuildProgram(0, Words(3));
        program = Transform(program, instruction => transform(instruction with
        {
            Operands = Array.Empty<IrOperand>(),
            Annotation = instruction.Annotation with
            {
                Uses = Array.Empty<IrOperand>(),
                Defs = Array.Empty<IrOperand>()
            }
        }));
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrProgramBundlingResult bundling = new HybridCpuBundleFormer().BundleProgram(schedule);
        IrFspStaticEvidenceReportV1 fsp = new HybridCpuFspStaticEvidenceAnalyzerV1().Analyze(
            bundling,
            options: HybridCpuFspEvidenceOptionsV1.Qualification);
        return new(bundling, fsp);
    }

    private static HybridCpuInstructionWord[] Words(int count) => Enumerable.Range(0, count)
        .Select(_ => new HybridCpuInstructionWord
        {
            OpCode = (uint)HybridCpuOpcode.Nope,
            DataTypeValue = HybridCpuDataType.INT64,
            PredicateMask = byte.MaxValue,
            VirtualThreadId = 0,
            Word1 = HybridCpuInstructionWord.PackArchRegs(
                HybridCpuInstructionWord.NoArchReg,
                HybridCpuInstructionWord.NoArchReg,
                HybridCpuInstructionWord.NoArchReg)
        })
        .ToArray();

    private static IrInstruction WithMemory(
        IrInstruction instruction,
        ulong address,
        uint length,
        IrMemoryEffectKind kind,
        IrAddressSpaceIdentity addressSpace,
        IrMemoryOrdering ordering) => WithMemory(
            instruction,
            new IrMemoryRegion(address, length, false),
            kind,
            addressSpace,
            ordering);

    private static IrInstruction WithMemory(
        IrInstruction instruction,
        IrMemoryRegion? region,
        IrMemoryEffectKind kind,
        IrAddressSpaceIdentity addressSpace,
        IrMemoryOrdering ordering)
    {
        IrMemoryRegion? read = kind.HasFlag(IrMemoryEffectKind.Read) ? region : null;
        IrMemoryRegion? write = kind.HasFlag(IrMemoryEffectKind.Write) && region is not null
            ? region with { IsWrite = true }
            : null;
        return instruction with
        {
            Annotation = instruction.Annotation with
            {
                MemoryReadRegion = read,
                MemoryWriteRegion = write
            },
            SideEffects = new(
                new(kind, addressSpace, ordering, read, write),
                IrArchitecturalEffectKind.None)
        };
    }

    private static IrInstruction WithManagedOrigin(IrInstruction instruction) => instruction with
    {
        OriginChain = new(1,
        [
            new("managed:test", IrSourceOriginKind.Cil, "test", "1", null,
                IrFrontendEvidenceTrust.ValidatedStructural)
        ])
    };

#pragma warning disable CS0618
    private static IrInstruction WithSlot(
        IrInstruction instruction,
        IrIssueSlotMask slots,
        IrSlotClass slotClass) => instruction with
        {
            Annotation = instruction.Annotation with
            {
                LegalSlots = slots,
                RequiredSlotClass = slotClass
            }
        };
#pragma warning restore CS0618

    private static IrProgram Transform(IrProgram program, Func<IrInstruction, IrInstruction> transform)
    {
        Dictionary<int, IrInstruction> replacements = program.Instructions
            .ToDictionary(static instruction => instruction.Index, transform);
        return program with
        {
            Instructions = program.Instructions.Select(instruction => replacements[instruction.Index]).ToArray(),
            ControlFlowGraph = program.ControlFlowGraph with
            {
                Blocks = program.BasicBlocks.Select(block => block with
                {
                    Instructions = block.Instructions.Select(instruction => replacements[instruction.Index]).ToArray()
                }).ToArray()
            }
        };
    }

    private static string[] DependencyKeys(Subject subject)
    {
        IrProgramDependencyGraph graph = subject.Bundling.ProgramSchedule.DependencyGraph;
        return graph.BlockGraphs.OrderBy(static block => block.BlockId)
            .SelectMany(block => block.Dependencies.Select(dependency => string.Join(':',
                "block", block.BlockId, dependency.ProducerInstructionIndex,
                dependency.ConsumerInstructionIndex, (byte)dependency.Kind,
                dependency.MinimumLatencyCycles, (byte)dependency.MemoryPrecision)))
            .Concat(graph.InterBlockGraph.Dependencies.Select(dependency => string.Join(':',
                "edge", dependency.SourceBlockId, dependency.TargetBlockId,
                (byte)dependency.EdgeKind, dependency.Dependency.ProducerInstructionIndex,
                dependency.Dependency.ConsumerInstructionIndex, (byte)dependency.Dependency.Kind,
                dependency.Dependency.MinimumLatencyCycles,
                (byte)dependency.Dependency.MemoryPrecision)))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Compilers", "HybridCPU_Compiler")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record Subject(
        IrProgramBundlingResult Bundling,
        IrFspStaticEvidenceReportV1 FspEvidence);
}
