using System.Globalization;
using System.Reflection;
using System.Text.Json;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Fsp;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.IR.Telemetry;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase18FspStaticEvidenceTests
{
    [Fact]
    public void Contract_IsPinnedVersionedBoundedDefaultOffAndEvidenceOnly()
    {
        HybridCpuFspEvidenceContractV1 contract = HybridCpuFspEvidenceContractV1.Default;
        Assert.Equal("hybridcpu.fsp-static-evidence/v1", HybridCpuFspEvidenceContractV1.SchemaId);
        Assert.Equal("2b290787ecf521374f55a91c7a38fcd2137adc1350539019c6878d510578aeb5",
            contract.ContractDigest);
        Assert.Equal("406f7d042aae68f80d07e1fb0d739a44a6bcf82a218d128713217d91c31b2b6e",
            contract.ProductionOptionsDigest);
        Assert.Equal("71cc4ec39842645a0748ae20474614672cc67fe6e419738892d05abfa953a8a2",
            contract.QualificationOptionsDigest);
        Assert.Equal(IrFspEvidenceSwitchV1.Disabled,
            HybridCpuFspEvidenceOptionsV1.Production.EvidenceSwitch);
        Assert.Equal(new HybridCpuFspEvidenceBudgetsV1(256, 1024, 1024),
            HybridCpuFspEvidenceOptionsV1.Production.Budgets);
    }

    [Fact]
    public void ProductionKillSwitch_PreservesExactScheduleBundleAndNativeBytes()
    {
        Subject subject = SubjectFor(2);
        byte[] before = HybridCpuCanonicalCompiler.CompileProgram(0, Words(0, 2)).ProgramImage;
        IrFspStaticEvidenceReportV1 report = new HybridCpuFspStaticEvidenceAnalyzerV1().Analyze(subject.Bundling);
        byte[] after = HybridCpuCanonicalCompiler.CompileProgram(0, Words(0, 2)).ProgramImage;

        Assert.Equal(IrFspEvidenceStatusV1.DisabledBaseline, report.Status);
        Assert.Equal(IrFspEvidenceAuthorityV1.CompilerEvidenceOnly, report.Authority);
        Assert.Empty(report.Candidates);
        Assert.Equal(subject.ScheduleDigest, report.ScheduleDigest);
        Assert.Equal(subject.BundleDigest, report.BundleDigest);
        Assert.Equal(before, after);
    }

    [Fact]
    public void EligibleStaticCandidate_CarriesEveryRequiredPrecisionAndProvenance()
    {
        Subject subject = SubjectFor(1);
        IrFspStaticEvidenceReportV1 report = Analyze(subject);
        IrFspStaticCandidateEvidenceV1 candidate = Assert.Single(report.Candidates);

        Assert.Equal(IrFspEvidenceStatusV1.Accepted, report.Status);
        Assert.Equal(IrFspStaticCandidateDispositionV1.EligibleStaticCandidate, candidate.Disposition);
        Assert.Equal(IrFspStaticVtRelationV1.SourceVtKnownReceiverRelationUnspecified,
            candidate.StaticVtRelation.Value);
        Assert.Equal(IrFspEvidencePrecisionV1.ConservativeSet, candidate.StaticVtRelation.Precision);
        Assert.Equal(IrFspEvidencePrecisionV1.ExactStatic, candidate.StructurallyCompatibleSlots.Precision);
        Assert.Equal(IrFspEvidencePrecisionV1.ExactStatic, candidate.StaticClass.Precision);
        Assert.Equal(IrFspEvidencePrecisionV1.ExactStatic, candidate.DependencyHorizonCycles.Precision);
        Assert.Equal(IrFspEvidencePrecisionV1.ExactStatic, candidate.DownstreamDependencyCount.Precision);
        Assert.Equal(IrFspEvidencePrecisionV1.ExactStatic, candidate.StaticHoleCount.Precision);
        Assert.Equal(7, candidate.StaticHoleCount.Value);
        Assert.True(candidate.EstimatedStaticCycleValue.Value > 0);
        Assert.False(string.IsNullOrWhiteSpace(candidate.BundleIdentity));
        Assert.False(string.IsNullOrWhiteSpace(candidate.InstructionIdentity));
        Assert.False(string.IsNullOrWhiteSpace(candidate.TopologyDigest));
        Assert.False(string.IsNullOrWhiteSpace(candidate.CandidateDigest));
        Assert.Equal(HybridCpuFspEvidenceContractV1.Default.ContractDigest, report.ModelDigest);
        Assert.All(new object[]
        {
            candidate.StructurallyCompatibleSlots,
            candidate.StaticVtRelation,
            candidate.StaticClass,
            candidate.DependencyHorizonCycles,
            candidate.DownstreamDependencyCount,
            candidate.RegisterGroupPressure,
            candidate.StaticHoleCount,
            candidate.CertificateClass,
            candidate.ResourcePressureReasons,
            candidate.EstimatedStaticCycleValue
        }, field => Assert.Contains("Provenance", field.GetType().GetProperty("Provenance")!.Name));
    }

    [Fact]
    public void ExistingStealabilityVerdictIsReusedButDoesNotAloneGrantCandidateStatus()
    {
        Subject subject = SubjectFor(1);
        IrInstruction instruction = Assert.Single(subject.Bundling.Program.Instructions);
        Assert.True(new HybridCpuStealabilityAnalyzer().AnalyzeInstruction(instruction).IsStealable);
        Assert.Equal(IrFspStaticCandidateDispositionV1.EligibleStaticCandidate,
            Assert.Single(Analyze(subject).Candidates).Disposition);

        Subject memory = SubjectFor(1, item => WithMemory(item, 0x1000, write: false));
        Assert.True(new HybridCpuStealabilityAnalyzer().AnalyzeInstruction(
            Assert.Single(memory.Bundling.Program.Instructions)).IsStealable);
        Assert.Equal(IrFspStaticCandidateDispositionV1.ExcludedByMemoryEffect,
            Assert.Single(Analyze(memory).Candidates).Disposition);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MemoryEffectsAreConservativelyExcluded(bool write)
    {
        Subject subject = SubjectFor(1, instruction => WithMemory(instruction, 0x1000, write));
        IrFspStaticCandidateEvidenceV1 candidate = Assert.Single(Analyze(subject).Candidates);
        Assert.Equal(IrFspStaticCandidateDispositionV1.ExcludedByMemoryEffect, candidate.Disposition);
        Assert.Equal(0, candidate.EstimatedStaticCycleValue.Value);
    }

    [Theory]
    [InlineData(IrIssueSlotMask.Slot6, IrSlotClass.DmaStreamClass, CompilerCertificateClassV1.Lane6Stream)]
    [InlineData(IrIssueSlotMask.Slot7, IrSlotClass.BranchControl, CompilerCertificateClassV1.Lane7Control)]
    public void Lane6AndLane7SpecialContoursAreExplicitlyExcluded(
        IrIssueSlotMask slots,
        IrSlotClass slotClass,
        CompilerCertificateClassV1 certificate)
    {
        Subject subject = SubjectFor(1, instruction => WithSlot(instruction, slots, slotClass));
        IrFspStaticCandidateEvidenceV1 candidate = Assert.Single(Analyze(subject).Candidates);
        Assert.Equal(IrFspStaticCandidateDispositionV1.ExcludedBySpecialContour, candidate.Disposition);
        Assert.Equal(certificate, candidate.CertificateClass.Value);
        Assert.True(candidate.ResourcePressureReasons.Value.HasFlag(
            IrFspResourcePressureReasonV1.SpecialContour));
    }

    [Fact]
    public void BarrierTrapAndControlReuseExistingNegativeReasons()
    {
        Func<IrInstruction, IrInstruction>[] transforms =
        [
            instruction => instruction with
            {
                Annotation = instruction.Annotation with { IsBarrierLike = true }
            },
            instruction => instruction with
            {
                Annotation = instruction.Annotation with { MayTrap = true }
            },
            instruction => instruction with
            {
                Annotation = instruction.Annotation with
                {
                    ControlFlowKind = IrControlFlowKind.UnconditionalBranch
                }
            }
        ];
        foreach (Func<IrInstruction, IrInstruction> transform in transforms)
        {
            Subject subject = SubjectFor(1, transform);
            Assert.Equal(IrFspStaticCandidateDispositionV1.ExcludedByStealability,
                Assert.Single(Analyze(subject).Candidates).Disposition);
        }
    }

    [Fact]
    public void UnknownResourcePrecisionCannotBecomeStaticEligibility()
    {
        Subject subject = SubjectFor(1, instruction => instruction with
        {
            Annotation = instruction.Annotation with
            {
                Uses = [new(IrOperandKind.Tile, 0, "unknown-tile-domain")]
            }
        });
        IrFspStaticCandidateEvidenceV1 candidate = Assert.Single(Analyze(subject).Candidates);
        Assert.Equal(IrFspStaticCandidateDispositionV1.ExcludedByUnknownResource, candidate.Disposition);
        Assert.Equal(IrFspEvidencePrecisionV1.Unknown, candidate.RegisterGroupPressure.Precision);
        Assert.True(candidate.ResourcePressureReasons.Value.HasFlag(
            IrFspResourcePressureReasonV1.UnknownResource));
    }

    [Fact]
    public void RegisterGroupAndBankChannelPressureReasonsRemainStaticEvidence()
    {
        Subject registers = SubjectForRegisterPressure();
        IrFspStaticCandidateEvidenceV1[] registerEvidence = Analyze(registers).Candidates.ToArray();
        Assert.Equal(3, registerEvidence.Length);
        Assert.All(registerEvidence, evidence =>
        {
            Assert.True(evidence.RegisterGroupPressure.Value > 0);
            Assert.True(evidence.ResourcePressureReasons.Value.HasFlag(
                IrFspResourcePressureReasonV1.RegisterGroupPressure));
        });

        Subject memory = SubjectFor(1, instruction => WithMemoryRegion(instruction, 0x1000, 128));
        HybridCpuTopologyResourceModelV1 topology = new HybridCpuTopologyResourceModelV1(new HybridCpuMachineTopologyV1(
            memoryBankCount: 4,
            memoryBankWidthBytes: 64,
            memoryChannelCount: 2,
            memoryChannelWidthBytes: 64));
        IrFspStaticEvidenceReportV1 memoryReport = new HybridCpuFspStaticEvidenceAnalyzerV1().Analyze(
            memory.Bundling,
            topology,
            HybridCpuFspEvidenceOptionsV1.Qualification);
        IrFspStaticCandidateEvidenceV1 memoryEvidence = Assert.Single(memoryReport.Candidates);
        Assert.Equal(IrFspStaticCandidateDispositionV1.ExcludedByMemoryEffect, memoryEvidence.Disposition);
        Assert.True(memoryEvidence.ResourcePressureReasons.Value.HasFlag(
            IrFspResourcePressureReasonV1.BankOrChannelPossible));
    }

    [Fact]
    public void TopologyMismatchAndDeterministicBudgetExhaustionKeepBaselineDigests()
    {
        Subject subject = SubjectFor(2);
        HybridCpuFspStaticEvidenceAnalyzerV1 analyzer = new HybridCpuFspStaticEvidenceAnalyzerV1();
        IrFspStaticEvidenceReportV1 stale = analyzer.Analyze(
            subject.Bundling,
            options: HybridCpuFspEvidenceOptionsV1.Qualification,
            expectedTopologyDigest: new string('0', 64));
        Assert.Equal(IrFspEvidenceStatusV1.StaleInput, stale.Status);
        Assert.Empty(stale.Candidates);
        Assert.Equal(subject.ScheduleDigest, stale.ScheduleDigest);
        Assert.Equal(subject.BundleDigest, stale.BundleDigest);

        HybridCpuFspEvidenceOptionsV1 tiny = HybridCpuFspEvidenceOptionsV1.Create(
            IrFspEvidenceSwitchV1.ExplicitQualificationOnly,
            HybridCpuFspEvidenceBudgetsV1.Production with { MaximumInstructions = 1 });
        IrFspStaticEvidenceReportV1 bounded = analyzer.Analyze(subject.Bundling, options: tiny);
        Assert.Equal(IrFspEvidenceStatusV1.BudgetExhausted, bounded.Status);
        Assert.Empty(bounded.Candidates);
        Assert.Equal(subject.BundleDigest, bounded.BundleDigest);

        IrFspOfflineRankingProfileV1 invalidProfile = IrFspOfflineRankingProfileV1.Create(
            HybridCpuFspEvidenceContractV1.Default.ContractDigest,
            staticHoleWeight: 1,
            criticalPathWeight: 1,
            dependencyWeight: 1,
            pressurePenaltyWeight: 1) with
        {
            ProfileDigest = new string('0', 64)
        };
        IrFspStaticEvidenceReportV1 invalid = analyzer.Analyze(
            subject.Bundling,
            options: HybridCpuFspEvidenceOptionsV1.Qualification,
            rankingProfile: invalidProfile);
        Assert.Equal(IrFspEvidenceStatusV1.InvalidModel, invalid.Status);
        Assert.Empty(invalid.Candidates);
        Assert.Equal(subject.ScheduleDigest, invalid.ScheduleDigest);
        Assert.Equal(subject.BundleDigest, invalid.BundleDigest);
    }

    [Fact]
    public void OfflineProfileCanOnlyChangeRankingInputsNotStaticEligibility()
    {
        Subject subject = SubjectFor(2);
        IrFspStaticEvidenceReportV1 baseline = Analyze(subject);
        IrFspOfflineRankingProfileV1 profile = IrFspOfflineRankingProfileV1.Create(
            "qualified-offline-model-v1", 200, 30, 8, 1);
        IrFspStaticEvidenceReportV1 profiled = new HybridCpuFspStaticEvidenceAnalyzerV1().Analyze(
            subject.Bundling,
            options: HybridCpuFspEvidenceOptionsV1.Qualification,
            rankingProfile: profile);

        Assert.Equal(baseline.Candidates.Select(static candidate =>
                (candidate.InstructionIdentity, candidate.Disposition)).OrderBy(static item => item.InstructionIdentity),
            profiled.Candidates.Select(static candidate =>
                (candidate.InstructionIdentity, candidate.Disposition)).OrderBy(static item => item.InstructionIdentity));
        Assert.All(profiled.Candidates, candidate =>
            Assert.Equal(IrFspEvidencePrecisionV1.ProfileOnly,
                candidate.EstimatedStaticCycleValue.Precision));
        Assert.NotEqual(baseline.ReportDigest, profiled.ReportDigest);
    }

    [Fact]
    public void EvidenceIsCultureAndRepeatedRunDeterministic()
    {
        Subject subject = SubjectFor(3);
        IrFspStaticEvidenceReportV1 first = Analyze(subject);
        CultureInfo original = CultureInfo.CurrentCulture;
        IrFspStaticEvidenceReportV1 second;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            second = Analyze(subject);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
        Assert.Equal(first.ReportDigest, second.ReportDigest);
        Assert.Equal(first.Candidates.Select(static candidate => candidate.CandidateDigest),
            second.Candidates.Select(static candidate => candidate.CandidateDigest));
        Assert.Equal(first.Counters, second.Counters);
    }

    [Fact]
    public void EvidenceSchemaHasNoRuntimeContextOrPermissionFields()
    {
        IrFspStaticEvidenceReportV1 report = Analyze(SubjectFor(1));
        string[] propertyNames = typeof(IrFspStaticCandidateEvidenceV1).GetProperties()
            .Concat(typeof(IrFspStaticEvidenceReportV1).GetProperties())
            .Select(static property => property.Name).ToArray();
        string[] forbidden =
        [
            "epoch", "fresh", "generation", "owner", "admitted", "execute",
            "commit", "retire", "LegalityDecision", "runtimeDonor", "slotReservation"
        ];
        Assert.DoesNotContain(propertyNames, name => forbidden.Any(token =>
            name.Contains(token, StringComparison.OrdinalIgnoreCase)));
        string json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("LegalityDecision", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(IrFspEvidenceAuthorityV1.CompilerEvidenceOnly, report.Authority);
    }

    [Fact]
    public void AnalyzerStaysCoreOnlyAndHasNoProductionCaller()
    {
        Assembly core = typeof(HybridCpuFspStaticEvidenceAnalyzerV1).Assembly;
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
                !path.EndsWith("HybridCpuFspStaticEvidenceAnalyzerV1.cs", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith("HybridCpuFspEvidenceContractsV1.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.DoesNotContain(production, path => File.ReadAllText(path)
            .Contains("HybridCpuFspStaticEvidenceAnalyzerV1", StringComparison.Ordinal));
    }

    private static IrFspStaticEvidenceReportV1 Analyze(Subject subject) =>
        new HybridCpuFspStaticEvidenceAnalyzerV1().Analyze(
            subject.Bundling,
            options: HybridCpuFspEvidenceOptionsV1.Qualification);

    private static Subject SubjectFor(
        int instructionCount,
        Func<IrInstruction, IrInstruction>? transform = null)
    {
        IrProgram program = new HybridCpuIrBuilder().BuildProgram(0, Words(0, instructionCount));
        program = Transform(program, instruction =>
        {
            IrInstruction normalized = instruction with
            {
                Operands = Array.Empty<IrOperand>(),
                Annotation = instruction.Annotation with
                {
                    Uses = Array.Empty<IrOperand>(),
                    Defs = Array.Empty<IrOperand>()
                }
            };
            return transform is null ? normalized : transform(normalized);
        });
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrProgramBundlingResult bundling = new HybridCpuBundleFormer().BundleProgram(schedule);
        return new(
            bundling,
            CompilerScheduleFingerprintV1.HashSchedule(schedule),
            CompilerScheduleFingerprintV1.HashBundles(bundling));
    }

    private static HybridCpuInstructionWord[] Words(byte virtualThreadId, int count) =>
        Enumerable.Range(0, count).Select(_ => new HybridCpuInstructionWord
        {
            OpCode = (uint)HybridCpuOpcode.Nope,
            DataTypeValue = HybridCpuDataType.INT64,
            PredicateMask = byte.MaxValue,
            VirtualThreadId = virtualThreadId,
            Word1 = HybridCpuInstructionWord.PackArchRegs(
                HybridCpuInstructionWord.NoArchReg,
                HybridCpuInstructionWord.NoArchReg,
                HybridCpuInstructionWord.NoArchReg)
        }).ToArray();

    private static Subject SubjectForRegisterPressure()
    {
        (byte Destination, byte Source)[] registers = [(1, 0), (2, 1), (3, 2)];
        HybridCpuInstructionWord[] words = registers
            .Select(static pair => new HybridCpuInstructionWord
            {
                OpCode = (uint)HybridCpuOpcode.ADDI,
                DataTypeValue = HybridCpuDataType.INT64,
                VirtualThreadId = 0,
                Word1 = HybridCpuInstructionWord.PackArchRegs(
                    pair.Destination,
                    pair.Source,
                    HybridCpuInstructionWord.NoArchReg),
                Immediate = 1
            })
            .ToArray();
        IrProgram program = new HybridCpuIrBuilder().BuildProgram(0, words);
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrProgramBundlingResult bundling = new HybridCpuBundleFormer().BundleProgram(schedule);
        return new(
            bundling,
            CompilerScheduleFingerprintV1.HashSchedule(schedule),
            CompilerScheduleFingerprintV1.HashBundles(bundling));
    }

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

    private static IrInstruction WithMemory(IrInstruction instruction, ulong address, bool write)
    {
        IrMemoryRegion region = new IrMemoryRegion(address, 8, write);
        IrMemoryEffectKind kind = write ? IrMemoryEffectKind.Write : IrMemoryEffectKind.Read;
        return instruction with
        {
            Annotation = instruction.Annotation with
            {
                MemoryReadRegion = write ? null : region,
                MemoryWriteRegion = write ? region : null
            },
            SideEffects = new(
                new(kind, IrAddressSpaceIdentity.Generic, IrMemoryOrdering.NotAtomic,
                    write ? null : region, write ? region : null),
                IrArchitecturalEffectKind.None)
        };
    }

    private static IrInstruction WithMemoryRegion(IrInstruction instruction, ulong address, uint length)
    {
        IrMemoryRegion region = new IrMemoryRegion(address, length, true);
        return instruction with
        {
            Annotation = instruction.Annotation with { MemoryWriteRegion = region },
            SideEffects = new(
                new(IrMemoryEffectKind.Write, IrAddressSpaceIdentity.Generic,
                    IrMemoryOrdering.NotAtomic, null, region),
                IrArchitecturalEffectKind.None)
        };
    }

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
        string ScheduleDigest,
        string BundleDigest);
}
